﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Xml;
using log4net;
using OMV = OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
using OpenSim.Region.Physics.ConvexDecompositionDotNet;

namespace OpenSim.Region.Physics.BulletSNPlugin
{

    [Serializable]
public sealed class BSPrim : BSPhysObject
{
    private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[BULLETS PRIM]";

    // _size is what the user passed. Scale is what we pass to the physics engine with the mesh.
    private OMV.Vector3 _size;  // the multiplier for each mesh dimension as passed by the user

    private bool _grabbed;
    private bool _isSelected;
    private bool _isVolumeDetect;
    private OMV.Vector3 _position;
    private float _mass;    // the mass of this object
    private float _density;
    private OMV.Vector3 _force;
    private OMV.Vector3 _velocity;
    private OMV.Vector3 _torque;
    private float _collisionScore;
    private OMV.Vector3 _acceleration;
    private OMV.Quaternion _orientation;
    private int _physicsActorType;
    private bool _isPhysical;
    private bool _flying;
    private float _friction;
    private float _restitution;
    private bool _setAlwaysRun;
    private bool _throttleUpdates;
    private bool _isColliding;
    private bool _collidingGround;
    private bool _collidingObj;
    private bool _floatOnWater;
    private OMV.Vector3 _rotationalVelocity;
    private bool _kinematic;
    private float _buoyancy;

    private BSDynamics _vehicle;

    private OMV.Vector3 _PIDTarget;
    private bool _usePID;
    private float _PIDTau;
    private bool _useHoverPID;
    private float _PIDHoverHeight;
    private PIDHoverType _PIDHoverType;
    private float _PIDHoverTao;

    public BSPrim(uint localID, String primName, BSScene parent_scene, OMV.Vector3 pos, OMV.Vector3 size,
                       OMV.Quaternion rotation, PrimitiveBaseShape pbs, bool pisPhysical)
            : base(parent_scene, localID, primName, "BSPrim")
    {
        // m_log.DebugFormat("{0}: BSPrim creation of {1}, id={2}", LogHeader, primName, localID);
        _physicsActorType = (int)ActorTypes.Prim;
        _position = pos;
        _size = size;
        Scale = size;   // prims are the size the user wants them to be (different for BSCharactes).
        _orientation = rotation;
        _buoyancy = 1f;
        _velocity = OMV.Vector3.Zero;
        _rotationalVelocity = OMV.Vector3.Zero;
        BaseShape = pbs;
        _isPhysical = pisPhysical;
        _isVolumeDetect = false;

        // Someday set default attributes based on the material but, for now, we don't know the prim material yet.
        // MaterialAttributes primMat = BSMaterials.GetAttributes(Material, pisPhysical);
        _density = PhysicsScene.Params.defaultDensity;
        _friction = PhysicsScene.Params.defaultFriction;
        _restitution = PhysicsScene.Params.defaultRestitution;

        _vehicle = new BSDynamics(PhysicsScene, this);            // add vehicleness

        _mass = CalculateMass();

        Linkset.Refresh(this);

        DetailLog("{0},BSPrim.constructor,call", LocalID);
        // do the actual object creation at taint time
        PhysicsScene.TaintedObject("BSPrim.create", delegate()
        {
            CreateGeomAndObject(true);

            CurrentCollisionFlags = BulletSimAPI.GetCollisionFlags2(PhysBody.ptr);
        });
    }

    // called when this prim is being destroyed and we should free all the resources
    public override void Destroy()
    {
        // m_log.DebugFormat("{0}: Destroy, id={1}", LogHeader, LocalID);
        base.Destroy();

        // Undo any links between me and any other object
        BSPhysObject parentBefore = Linkset.LinksetRoot;
        int childrenBefore = Linkset.NumberOfChildren;

        Linkset = Linkset.RemoveMeFromLinkset(this);

        DetailLog("{0},BSPrim.Destroy,call,parentBefore={1},childrenBefore={2},parentAfter={3},childrenAfter={4}",
            LocalID, parentBefore.LocalID, childrenBefore, Linkset.LinksetRoot.LocalID, Linkset.NumberOfChildren);

        // Undo any vehicle properties
        this.VehicleType = (int)Vehicle.TYPE_NONE;

        PhysicsScene.TaintedObject("BSPrim.destroy", delegate()
        {
            DetailLog("{0},BSPrim.Destroy,taint,", LocalID);
            // If there are physical body and shape, release my use of same.
            PhysicsScene.Shapes.DereferenceBody(PhysBody, true, null);
            PhysBody.Clear();
            PhysicsScene.Shapes.DereferenceShape(PhysShape, true, null);
            PhysShape.Clear();
        });
    }

    // No one uses this property.
    public override bool Stopped {
        get { return false; }
    }
    public override OMV.Vector3 Size {
        get { return _size; }
        set {
            // We presume the scale and size are the same. If scale must be changed for
            //     the physical shape, that is done when the geometry is built.
            _size = value;
            Scale = _size;
            ForceBodyShapeRebuild(false);
        }
    }

    public override PrimitiveBaseShape Shape {
        set {
            BaseShape = value;
            ForceBodyShapeRebuild(false);
        }
    }
    // Whatever the linkset wants is what I want.
    public override BSPhysicsShapeType PreferredPhysicalShape
        { get { return Linkset.PreferredPhysicalShape(this); } }

    public override bool ForceBodyShapeRebuild(bool inTaintTime)
    {
        LastAssetBuildFailed = false;
        PhysicsScene.TaintedObject(inTaintTime, "BSPrim.ForceBodyShapeRebuild", delegate()
        {
            _mass = CalculateMass();   // changing the shape changes the mass
            CreateGeomAndObject(true);
        });
        return true;
    }
    public override bool Grabbed {
        set { _grabbed = value;
        }
    }
    public override bool Selected {
        set
        {
            if (value != _isSelected)
            {
                _isSelected = value;
                PhysicsScene.TaintedObject("BSPrim.setSelected", delegate()
                {
                    DetailLog("{0},BSPrim.selected,taint,selected={1}", LocalID, _isSelected);
                    SetObjectDynamic(false);
                });
            }
        }
    }
    public override void CrossingFailure() { return; }

    // link me to the specified parent
    public override void link(PhysicsActor obj) {
        BSPrim parent = obj as BSPrim;
        if (parent != null)
        {
            BSPhysObject parentBefore = Linkset.LinksetRoot;
            int childrenBefore = Linkset.NumberOfChildren;

            Linkset = parent.Linkset.AddMeToLinkset(this);

            DetailLog("{0},BSPrim.link,call,parentBefore={1}, childrenBefore=={2}, parentAfter={3}, childrenAfter={4}",
                LocalID, parentBefore.LocalID, childrenBefore, Linkset.LinksetRoot.LocalID, Linkset.NumberOfChildren);
        }
        return;
    }

    // delink me from my linkset
    public override void delink() {
        // TODO: decide if this parent checking needs to happen at taint time
        // Race condition here: if link() and delink() in same simulation tick, the delink will not happen

        BSPhysObject parentBefore = Linkset.LinksetRoot;
        int childrenBefore = Linkset.NumberOfChildren;

        Linkset = Linkset.RemoveMeFromLinkset(this);

        DetailLog("{0},BSPrim.delink,parentBefore={1},childrenBefore={2},parentAfter={3},childrenAfter={4}, ",
            LocalID, parentBefore.LocalID, childrenBefore, Linkset.LinksetRoot.LocalID, Linkset.NumberOfChildren);
        return;
    }

    // Set motion values to zero.
    // Do it to the properties so the values get set in the physics engine.
    // Push the setting of the values to the viewer.
    // Called at taint time!
    public override void ZeroMotion(bool inTaintTime)
    {
        _velocity = OMV.Vector3.Zero;
        _acceleration = OMV.Vector3.Zero;
        _rotationalVelocity = OMV.Vector3.Zero;

        // Zero some other properties in the physics engine
        PhysicsScene.TaintedObject(inTaintTime, "BSPrim.ZeroMotion", delegate()
        {
            if (PhysBody.HasPhysicalBody)
                BulletSimAPI.ClearAllForces2(PhysBody.ptr);
        });
    }
    public override void ZeroAngularMotion(bool inTaintTime)
    {
        _rotationalVelocity = OMV.Vector3.Zero;
        // Zero some other properties in the physics engine
        PhysicsScene.TaintedObject(inTaintTime, "BSPrim.ZeroMotion", delegate()
        {
            // DetailLog("{0},BSPrim.ZeroAngularMotion,call,rotVel={1}", LocalID, _rotationalVelocity);
            if (PhysBody.HasPhysicalBody)
            {
                BulletSimAPI.SetInterpolationAngularVelocity2(PhysBody.ptr, _rotationalVelocity);
                BulletSimAPI.SetAngularVelocity2(PhysBody.ptr, _rotationalVelocity);
            }
        });
    }

    public override void LockAngularMotion(OMV.Vector3 axis)
    {
        DetailLog("{0},BSPrim.LockAngularMotion,call,axis={1}", LocalID, axis);
        return;
    }

    public override OMV.Vector3 RawPosition
    {
        get { return _position; }
        set { _position = value; }
    }
    public override OMV.Vector3 Position {
        get {
            /* NOTE: this refetch is not necessary. The simulator knows about linkset children
             *    and does not fetch this position info for children. Thus this is commented out.
            // child prims move around based on their parent. Need to get the latest location
            if (!Linkset.IsRoot(this))
                _position = Linkset.PositionGet(this);
            */

            // don't do the GetObjectPosition for root elements because this function is called a zillion times.
            // _position = BulletSimAPI.GetObjectPosition2(PhysicsScene.World.ptr, BSBody.ptr);
            return _position;
        }
        set {
            // If the position must be forced into the physics engine, use ForcePosition.
            // All positions are given in world positions.
            if (_position == value)
            {
                DetailLog("{0},BSPrim.setPosition,taint,positionNotChanging,pos={1},orient={2}", LocalID, _position, _orientation);
                return;
            }
            _position = value;
            PositionSanityCheck(false);

            // A linkset might need to know if a component information changed.
            Linkset.UpdateProperties(this, false);

            PhysicsScene.TaintedObject("BSPrim.setPosition", delegate()
            {
                DetailLog("{0},BSPrim.SetPosition,taint,pos={1},orient={2}", LocalID, _position, _orientation);
                ForcePosition = _position;
            });
        }
    }
    public override OMV.Vector3 ForcePosition {
        get {
            _position = BulletSimAPI.GetPosition2(PhysBody.ptr);
            return _position;
        }
        set {
            _position = value;
            if (PhysBody.HasPhysicalBody)
            {
                BulletSimAPI.SetTranslation2(PhysBody.ptr, _position, _orientation);
                ActivateIfPhysical(false);
            }
        }
    }

    // Check that the current position is sane and, if not, modify the position to make it so.
    // Check for being below terrain and being out of bounds.
    // Returns 'true' of the position was made sane by some action.
    private bool PositionSanityCheck(bool inTaintTime)
    {
        bool ret = false;

        if (!PhysicsScene.TerrainManager.IsWithinKnownTerrain(_position))
        {
            // The physical object is out of the known/simulated area.
            // Upper levels of code will handle the transition to other areas so, for
            //     the time, we just ignore the position.
            return ret;
        }

        float terrainHeight = PhysicsScene.TerrainManager.GetTerrainHeightAtXYZ(_position);
        OMV.Vector3 upForce = OMV.Vector3.Zero;
        if (RawPosition.Z < terrainHeight)
        {
            DetailLog("{0},BSPrim.PositionAdjustUnderGround,call,pos={1},terrain={2}", LocalID, _position, terrainHeight);
            float targetHeight = terrainHeight + (Size.Z / 2f);
            // Upforce proportional to the distance away from the terrain. Correct the error in 1 sec.
            upForce.Z = (terrainHeight - RawPosition.Z) * 1f;
            ret = true;
        }

        if ((CurrentCollisionFlags & CollisionFlags.BS_FLOATS_ON_WATER) != 0)
        {
            float waterHeight = PhysicsScene.TerrainManager.GetWaterLevelAtXYZ(_position);
            // TODO: a floating motor so object will bob in the water
            if (Math.Abs(RawPosition.Z - waterHeight) > 0.1f)
            {
                // Upforce proportional to the distance away from the water. Correct the error in 1 sec.
                upForce.Z = (waterHeight - RawPosition.Z) * 1f;
                ret = true;
            }
        }

        // The above code computes a force to apply to correct any out-of-bounds problems. Apply same.
        // TODO: This should be intergrated with a geneal physics action mechanism.
        // TODO: This should be moderated with PID'ness.
        if (ret)
        {
            // Apply upforce and overcome gravity.
            OMV.Vector3 correctionForce = upForce - PhysicsScene.DefaultGravity;
            DetailLog("{0},BSPrim.PositionSanityCheck,applyForce,pos={1},upForce={2},correctionForce={3}", LocalID, _position, upForce, correctionForce);
            AddForce(correctionForce, false, inTaintTime);
        }
        return ret;
    }

    // Return the effective mass of the object.
        // The definition of this call is to return the mass of the prim.
        // If the simulator cares about the mass of the linkset, it will sum it itself.
    public override float Mass
    {
        get
        {
            return _mass;
        }
    }

    // used when we only want this prim's mass and not the linkset thing
    public override float RawMass { 
        get { return _mass; }
    }
    // Set the physical mass to the passed mass.
    // Note that this does not change _mass!
    public override void UpdatePhysicalMassProperties(float physMass, bool inWorld)
    {
        if (PhysBody.HasPhysicalBody)
        {
            if (IsStatic)
            {
                Inertia = OMV.Vector3.Zero;
                BulletSimAPI.SetMassProps2(PhysBody.ptr, 0f, Inertia);
                BulletSimAPI.UpdateInertiaTensor2(PhysBody.ptr);
            }
            else
            {
                if (inWorld)
                {
                    // Changing interesting properties doesn't change proxy and collision cache
                    //    information. The Bullet solution is to re-add the object to the world
                    //    after parameters are changed.
                    BulletSimAPI.RemoveObjectFromWorld2(PhysicsScene.World.ptr, PhysBody.ptr);
                }

                Inertia = BulletSimAPI.CalculateLocalInertia2(PhysShape.ptr, physMass);
                BulletSimAPI.SetMassProps2(PhysBody.ptr, physMass, Inertia);
                BulletSimAPI.UpdateInertiaTensor2(PhysBody.ptr);

                // center of mass is at the zero of the object
                // DEBUG DEBUG BulletSimAPI.SetCenterOfMassByPosRot2(PhysBody.ptr, ForcePosition, ForceOrientation);
                DetailLog("{0},BSPrim.UpdateMassProperties,mass={1},localInertia={2},inWorld={3}", LocalID, physMass, Inertia, inWorld);

                if (inWorld)
                {
                    BulletSimAPI.AddObjectToWorld2(PhysicsScene.World.ptr, PhysBody.ptr,_position,_orientation);
                }

                // Must set gravity after it has been added to the world because, for unknown reasons,
                //     adding the object resets the object's gravity to world gravity
                OMV.Vector3 grav = PhysicsScene.DefaultGravity * (1f - Buoyancy);
                BulletSimAPI.SetGravity2(PhysBody.ptr, grav);

            }
        }
    }

    // Is this used?
    public override OMV.Vector3 CenterOfMass
    {
        get { return Linkset.CenterOfMass; }
    }

    // Is this used?
    public override OMV.Vector3 GeometricCenter
    {
        get { return Linkset.GeometricCenter; }
    }

    public override OMV.Vector3 Force {
        get { return _force; }
        set {
            _force = value;
            if (_force != OMV.Vector3.Zero)
            {
                // If the force is non-zero, it must be reapplied each tick because
                //    Bullet clears the forces applied last frame.
                RegisterPreStepAction("BSPrim.setForce", LocalID,
                    delegate(float timeStep)
                    {
                        DetailLog("{0},BSPrim.setForce,preStep,force={1}", LocalID, _force);
                        if (PhysBody.HasPhysicalBody)
                        {
                            BulletSimAPI.ApplyCentralForce2(PhysBody.ptr, _force);
                            ActivateIfPhysical(false);
                        }
                    }
                );
            }
            else
            {
                UnRegisterPreStepAction("BSPrim.setForce", LocalID);
            }
        }
    }

    public override int VehicleType {
        get {
            return (int)_vehicle.Type;   // if we are a vehicle, return that type
        }
        set {
            Vehicle type = (Vehicle)value;

            PhysicsScene.TaintedObject("setVehicleType", delegate()
            {
                // Done at taint time so we're sure the physics engine is not using the variables
                // Vehicle code changes the parameters for this vehicle type.
                _vehicle.ProcessTypeChange(type);
                ActivateIfPhysical(false);

                // If an active vehicle, register the vehicle code to be called before each step
                if (_vehicle.Type == Vehicle.TYPE_NONE)
                    UnRegisterPreStepAction("BSPrim.Vehicle", LocalID);
                else
                    RegisterPreStepAction("BSPrim.Vehicle", LocalID, _vehicle.Step);
            });
        }
    }
    public override void VehicleFloatParam(int param, float value)
    {
        PhysicsScene.TaintedObject("BSPrim.VehicleFloatParam", delegate()
        {
            _vehicle.ProcessFloatVehicleParam((Vehicle)param, value);
            ActivateIfPhysical(false);
        });
    }
    public override void VehicleVectorParam(int param, OMV.Vector3 value)
    {
        PhysicsScene.TaintedObject("BSPrim.VehicleVectorParam", delegate()
        {
            _vehicle.ProcessVectorVehicleParam((Vehicle)param, value);
            ActivateIfPhysical(false);
        });
    }
    public override void VehicleRotationParam(int param, OMV.Quaternion rotation)
    {
        PhysicsScene.TaintedObject("BSPrim.VehicleRotationParam", delegate()
        {
            _vehicle.ProcessRotationVehicleParam((Vehicle)param, rotation);
            ActivateIfPhysical(false);
        });
    }
    public override void VehicleFlags(int param, bool remove)
    {
        PhysicsScene.TaintedObject("BSPrim.VehicleFlags", delegate()
        {
            _vehicle.ProcessVehicleFlags(param, remove);
        });
    }

    // Allows the detection of collisions with inherently non-physical prims. see llVolumeDetect for more
    public override void SetVolumeDetect(int param) {
        bool newValue = (param != 0);
        if (_isVolumeDetect != newValue)
        {
            _isVolumeDetect = newValue;
            PhysicsScene.TaintedObject("BSPrim.SetVolumeDetect", delegate()
            {
                // DetailLog("{0},setVolumeDetect,taint,volDetect={1}", LocalID, _isVolumeDetect);
                SetObjectDynamic(true);
            });
        }
        return;
    }
    public override OMV.Vector3 Velocity {
        get { return _velocity; }
        set {
            _velocity = value;
            PhysicsScene.TaintedObject("BSPrim.setVelocity", delegate()
            {
                // DetailLog("{0},BSPrim.SetVelocity,taint,vel={1}", LocalID, _velocity);
                ForceVelocity = _velocity;
            });
        }
    }
    public override OMV.Vector3 ForceVelocity {
        get { return _velocity; }
        set {
            PhysicsScene.AssertInTaintTime("BSPrim.ForceVelocity");

            _velocity = value;
            if (PhysBody.HasPhysicalBody)
            {
                BulletSimAPI.SetLinearVelocity2(PhysBody.ptr, _velocity);
                ActivateIfPhysical(false);
            }
        }
    }
    public override OMV.Vector3 Torque {
        get { return _torque; }
        set {
            _torque = value;
            if (_torque != OMV.Vector3.Zero)
            {
                // If the torque is non-zero, it must be reapplied each tick because
                //    Bullet clears the forces applied last frame.
                RegisterPreStepAction("BSPrim.setTorque", LocalID,
                    delegate(float timeStep)
                    {
                        if (PhysBody.HasPhysicalBody)
                            AddAngularForce(_torque, false, true);
                    }
                );
            }
            else
            {
                UnRegisterPreStepAction("BSPrim.setTorque", LocalID);
            }
            // DetailLog("{0},BSPrim.SetTorque,call,torque={1}", LocalID, _torque);
        }
    }
    public override float CollisionScore {
        get { return _collisionScore; }
        set { _collisionScore = value;
        }
    }
    public override OMV.Vector3 Acceleration {
        get { return _acceleration; }
        set { _acceleration = value; }
    }
    public override OMV.Quaternion RawOrientation
    {
        get { return _orientation; }
        set { _orientation = value; }
    }
    public override OMV.Quaternion Orientation {
        get {
            /* NOTE: this refetch is not necessary. The simulator knows about linkset children
             *    and does not fetch this position info for children. Thus this is commented out.
            // Children move around because tied to parent. Get a fresh value.
            if (!Linkset.IsRoot(this))
            {
                _orientation = Linkset.OrientationGet(this);
            }
             */
            return _orientation;
        }
        set {
            if (_orientation == value)
                return;
            _orientation = value;

            // A linkset might need to know if a component information changed.
            Linkset.UpdateProperties(this, false);

            PhysicsScene.TaintedObject("BSPrim.setOrientation", delegate()
            {
                if (PhysBody.HasPhysicalBody)
                {
                    // _position = BulletSimAPI.GetObjectPosition2(PhysicsScene.World.ptr, BSBody.ptr);
                    // DetailLog("{0},BSPrim.setOrientation,taint,pos={1},orient={2}", LocalID, _position, _orientation);
                    BulletSimAPI.SetTranslation2(PhysBody.ptr, _position, _orientation);
                }
            });
        }
    }
    // Go directly to Bullet to get/set the value.
    public override OMV.Quaternion ForceOrientation
    {
        get
        {
            _orientation = BulletSimAPI.GetOrientation2(PhysBody.ptr);
            return _orientation;
        }
        set
        {
            _orientation = value;
            BulletSimAPI.SetTranslation2(PhysBody.ptr, _position, _orientation);
        }
    }
    public override int PhysicsActorType {
        get { return _physicsActorType; }
        set { _physicsActorType = value; }
    }
    public override bool IsPhysical {
        get { return _isPhysical; }
        set {
            if (_isPhysical != value)
            {
                _isPhysical = value;
                PhysicsScene.TaintedObject("BSPrim.setIsPhysical", delegate()
                {
                    // DetailLog("{0},setIsPhysical,taint,isPhys={1}", LocalID, _isPhysical);
                    SetObjectDynamic(true);
                    // whether phys-to-static or static-to-phys, the object is not moving.
                    ZeroMotion(true);
                });
            }
        }
    }

    // An object is static (does not move) if selected or not physical
    public override bool IsStatic
    {
        get { return _isSelected || !IsPhysical; }
    }

    // An object is solid if it's not phantom and if it's not doing VolumeDetect
    public override bool IsSolid
    {
        get { return !IsPhantom && !_isVolumeDetect; }
    }

    // Make gravity work if the object is physical and not selected
    // Called at taint-time!!
    private void SetObjectDynamic(bool forceRebuild)
    {
        // Recreate the physical object if necessary
        CreateGeomAndObject(forceRebuild);
    }

    // Convert the simulator's physical properties into settings on BulletSim objects.
    // There are four flags we're interested in:
    //     IsStatic: Object does not move, otherwise the object has mass and moves
    //     isSolid: other objects bounce off of this object
    //     isVolumeDetect: other objects pass through but can generate collisions
    //     collisionEvents: whether this object returns collision events
    private void UpdatePhysicalParameters()
    {
        // DetailLog("{0},BSPrim.UpdatePhysicalParameters,entry,body={1},shape={2}", LocalID, BSBody, BSShape);

        // Mangling all the physical properties requires the object not be in the physical world.
        // This is a NOOP if the object is not in the world (BulletSim and Bullet ignore objects not found).
        BulletSimAPI.RemoveObjectFromWorld2(PhysicsScene.World.ptr, PhysBody.ptr);

        // Set up the object physicalness (does gravity and collisions move this object)
        MakeDynamic(IsStatic);

        // Update vehicle specific parameters (after MakeDynamic() so can change physical parameters)
        _vehicle.Refresh();

        // Arrange for collision events if the simulator wants them
        EnableCollisions(SubscribedEvents());

        // Make solid or not (do things bounce off or pass through this object).
        MakeSolid(IsSolid);

        BulletSimAPI.AddObjectToWorld2(PhysicsScene.World.ptr, PhysBody.ptr, _position, _orientation);

        // Rebuild its shape
        BulletSimAPI.UpdateSingleAabb2(PhysicsScene.World.ptr, PhysBody.ptr);

        // Collision filter can be set only when the object is in the world
        PhysBody.ApplyCollisionMask();

        // Recompute any linkset parameters.
        // When going from non-physical to physical, this re-enables the constraints that
        //     had been automatically disabled when the mass was set to zero.
        // For compound based linksets, this enables and disables interactions of the children.
        Linkset.Refresh(this);

        DetailLog("{0},BSPrim.UpdatePhysicalParameters,taintExit,static={1},solid={2},mass={3},collide={4},cf={5:X},body={6},shape={7}",
                        LocalID, IsStatic, IsSolid, Mass, SubscribedEvents(), CurrentCollisionFlags, PhysBody, PhysShape);
    }

    // "Making dynamic" means changing to and from static.
    // When static, gravity does not effect the object and it is fixed in space.
    // When dynamic, the object can fall and be pushed by others.
    // This is independent of its 'solidness' which controls what passes through
    //    this object and what interacts with it.
    private void MakeDynamic(bool makeStatic)
    {
        if (makeStatic)
        {
            // Become a Bullet 'static' object type
            CurrentCollisionFlags = BulletSimAPI.AddToCollisionFlags2(PhysBody.ptr, CollisionFlags.CF_STATIC_OBJECT);
            // Stop all movement
            ZeroMotion(true);

            // Set various physical properties so other object interact properly
            MaterialAttributes matAttrib = BSMaterials.GetAttributes(Material, false);
            BulletSimAPI.SetFriction2(PhysBody.ptr, matAttrib.friction);
            BulletSimAPI.SetRestitution2(PhysBody.ptr, matAttrib.restitution);

            // Mass is zero which disables a bunch of physics stuff in Bullet
            UpdatePhysicalMassProperties(0f, false);
            // Set collision detection parameters
            if (BSParam.CcdMotionThreshold > 0f)
            {
                BulletSimAPI.SetCcdMotionThreshold2(PhysBody.ptr, BSParam.CcdMotionThreshold);
                BulletSimAPI.SetCcdSweptSphereRadius2(PhysBody.ptr, BSParam.CcdSweptSphereRadius);
            }

            // The activation state is 'disabled' so Bullet will not try to act on it.
            // BulletSimAPI.ForceActivationState2(PhysBody.ptr, ActivationState.DISABLE_SIMULATION);
            // Start it out sleeping and physical actions could wake it up.
            BulletSimAPI.ForceActivationState2(PhysBody.ptr, ActivationState.ISLAND_SLEEPING);

            // This collides like a static object
            PhysBody.collisionType = CollisionType.Static;

            // There can be special things needed for implementing linksets
            Linkset.MakeStatic(this);
        }
        else
        {
            // Not a Bullet static object
            CurrentCollisionFlags = BulletSimAPI.RemoveFromCollisionFlags2(PhysBody.ptr, CollisionFlags.CF_STATIC_OBJECT);

            // Set various physical properties so other object interact properly
            MaterialAttributes matAttrib = BSMaterials.GetAttributes(Material, true);
            BulletSimAPI.SetFriction2(PhysBody.ptr, matAttrib.friction);
            BulletSimAPI.SetRestitution2(PhysBody.ptr, matAttrib.restitution);

            // per http://www.bulletphysics.org/Bullet/phpBB3/viewtopic.php?t=3382
            // Since this can be called multiple times, only zero forces when becoming physical
            // BulletSimAPI.ClearAllForces2(BSBody.ptr);

            // For good measure, make sure the transform is set through to the motion state
            BulletSimAPI.SetTranslation2(PhysBody.ptr, _position, _orientation);

            // Center of mass is at the center of the object
            // DEBUG DEBUG BulletSimAPI.SetCenterOfMassByPosRot2(Linkset.LinksetRoot.PhysBody.ptr, _position, _orientation);

            // A dynamic object has mass
            UpdatePhysicalMassProperties(RawMass, false);

            // Set collision detection parameters
            if (BSParam.CcdMotionThreshold > 0f)
            {
                BulletSimAPI.SetCcdMotionThreshold2(PhysBody.ptr, BSParam.CcdMotionThreshold);
                BulletSimAPI.SetCcdSweptSphereRadius2(PhysBody.ptr, BSParam.CcdSweptSphereRadius);
            }

            // Various values for simulation limits
            BulletSimAPI.SetDamping2(PhysBody.ptr, BSParam.LinearDamping, BSParam.AngularDamping);
            BulletSimAPI.SetDeactivationTime2(PhysBody.ptr, BSParam.DeactivationTime);
            BulletSimAPI.SetSleepingThresholds2(PhysBody.ptr, BSParam.LinearSleepingThreshold, BSParam.AngularSleepingThreshold);
            BulletSimAPI.SetContactProcessingThreshold2(PhysBody.ptr, BSParam.ContactProcessingThreshold);

            // This collides like an object.
            PhysBody.collisionType = CollisionType.Dynamic;

            // Force activation of the object so Bullet will act on it.
            // Must do the ForceActivationState2() to overcome the DISABLE_SIMULATION from static objects.
            BulletSimAPI.ForceActivationState2(PhysBody.ptr, ActivationState.ACTIVE_TAG);

            // There might be special things needed for implementing linksets.
            Linkset.MakeDynamic(this);
        }
    }

    // "Making solid" means that other object will not pass through this object.
    // To make transparent, we create a Bullet ghost object.
    // Note: This expects to be called from the UpdatePhysicalParameters() routine as
    //     the functions after this one set up the state of a possibly newly created collision body.
    private void MakeSolid(bool makeSolid)
    {
        CollisionObjectTypes bodyType = (CollisionObjectTypes)BulletSimAPI.GetBodyType2(PhysBody.ptr);
        if (makeSolid)
        {
            // Verify the previous code created the correct shape for this type of thing.
            if ((bodyType & CollisionObjectTypes.CO_RIGID_BODY) == 0)
            {
                m_log.ErrorFormat("{0} MakeSolid: physical body of wrong type for solidity. id={1}, type={2}", LogHeader, LocalID, bodyType);
            }
            CurrentCollisionFlags = BulletSimAPI.RemoveFromCollisionFlags2(PhysBody.ptr, CollisionFlags.CF_NO_CONTACT_RESPONSE);
        }
        else
        {
            if ((bodyType & CollisionObjectTypes.CO_GHOST_OBJECT) == 0)
            {
                m_log.ErrorFormat("{0} MakeSolid: physical body of wrong type for non-solidness. id={1}, type={2}", LogHeader, LocalID, bodyType);
            }
            CurrentCollisionFlags = BulletSimAPI.AddToCollisionFlags2(PhysBody.ptr, CollisionFlags.CF_NO_CONTACT_RESPONSE);

            // Change collision info from a static object to a ghosty collision object
            PhysBody.collisionType = CollisionType.VolumeDetect;
        }
    }

    // Enable physical actions. Bullet will keep sleeping non-moving physical objects so
    //     they need waking up when parameters are changed.
    // Called in taint-time!!
    private void ActivateIfPhysical(bool forceIt)
    {
        if (IsPhysical && PhysBody.HasPhysicalBody)
            BulletSimAPI.Activate2(PhysBody.ptr, forceIt);
    }

    // Turn on or off the flag controlling whether collision events are returned to the simulator.
    private void EnableCollisions(bool wantsCollisionEvents)
    {
        if (wantsCollisionEvents)
        {
            CurrentCollisionFlags = BulletSimAPI.AddToCollisionFlags2(PhysBody.ptr, CollisionFlags.BS_SUBSCRIBE_COLLISION_EVENTS);
        }
        else
        {
            CurrentCollisionFlags = BulletSimAPI.RemoveFromCollisionFlags2(PhysBody.ptr, CollisionFlags.BS_SUBSCRIBE_COLLISION_EVENTS);
        }
    }

    // prims don't fly
    public override bool Flying {
        get { return _flying; }
        set {
            _flying = value;
        }
    }
    public override bool SetAlwaysRun {
        get { return _setAlwaysRun; }
        set { _setAlwaysRun = value; }
    }
    public override bool ThrottleUpdates {
        get { return _throttleUpdates; }
        set { _throttleUpdates = value; }
    }
    public override bool IsColliding {
        get { return (CollidingStep == PhysicsScene.SimulationStep); }
        set { _isColliding = value; }
    }
    public override bool CollidingGround {
        get { return (CollidingGroundStep == PhysicsScene.SimulationStep); }
        set { _collidingGround = value; }
    }
    public override bool CollidingObj {
        get { return _collidingObj; }
        set { _collidingObj = value; }
    }
    public bool IsPhantom {
        get {
            // SceneObjectPart removes phantom objects from the physics scene
            // so, although we could implement touching and such, we never
            // are invoked as a phantom object
            return false;
        }
    }
    public override bool FloatOnWater {
        set {
            _floatOnWater = value;
            PhysicsScene.TaintedObject("BSPrim.setFloatOnWater", delegate()
            {
                if (_floatOnWater)
                    CurrentCollisionFlags = BulletSimAPI.AddToCollisionFlags2(PhysBody.ptr, CollisionFlags.BS_FLOATS_ON_WATER);
                else
                    CurrentCollisionFlags = BulletSimAPI.RemoveFromCollisionFlags2(PhysBody.ptr, CollisionFlags.BS_FLOATS_ON_WATER);
            });
        }
    }
    public override OMV.Vector3 RotationalVelocity {
        get {
            return _rotationalVelocity;
        }
        set {
            _rotationalVelocity = value;
            // m_log.DebugFormat("{0}: RotationalVelocity={1}", LogHeader, _rotationalVelocity);
            PhysicsScene.TaintedObject("BSPrim.setRotationalVelocity", delegate()
            {
                DetailLog("{0},BSPrim.SetRotationalVel,taint,rotvel={1}", LocalID, _rotationalVelocity);
                ForceRotationalVelocity = _rotationalVelocity;
            });
        }
    }
    public override OMV.Vector3 ForceRotationalVelocity {
        get {
            return _rotationalVelocity;
        }
        set {
            _rotationalVelocity = value;
            if (PhysBody.HasPhysicalBody)
            {
                BulletSimAPI.SetAngularVelocity2(PhysBody.ptr, _rotationalVelocity);
                ActivateIfPhysical(false);
            }
        }
    }
    public override bool Kinematic {
        get { return _kinematic; }
        set { _kinematic = value;
            // m_log.DebugFormat("{0}: Kinematic={1}", LogHeader, _kinematic);
        }
    }
    public override float Buoyancy {
        get { return _buoyancy; }
        set {
            _buoyancy = value;
            PhysicsScene.TaintedObject("BSPrim.setBuoyancy", delegate()
            {
                ForceBuoyancy = _buoyancy;
            });
        }
    }
    public override float ForceBuoyancy {
        get { return _buoyancy; }
        set {
            _buoyancy = value;
            // DetailLog("{0},BSPrim.setForceBuoyancy,taint,buoy={1}", LocalID, _buoyancy);
            // Force the recalculation of the various inertia,etc variables in the object
            UpdatePhysicalMassProperties(_mass, true);
            ActivateIfPhysical(false);
        }
    }

    // Used for MoveTo
    public override OMV.Vector3 PIDTarget {
        set { _PIDTarget = value; }
    }
    public override bool PIDActive {
        set { _usePID = value; }
    }
    public override float PIDTau {
        set { _PIDTau = value; }
    }

    // Used for llSetHoverHeight and maybe vehicle height
    // Hover Height will override MoveTo target's Z
    public override bool PIDHoverActive {
        set { _useHoverPID = value; }
    }
    public override float PIDHoverHeight {
        set { _PIDHoverHeight = value; }
    }
    public override PIDHoverType PIDHoverType {
        set { _PIDHoverType = value; }
    }
    public override float PIDHoverTau {
        set { _PIDHoverTao = value; }
    }

    // For RotLookAt
    public override OMV.Quaternion APIDTarget { set { return; } }
    public override bool APIDActive { set { return; } }
    public override float APIDStrength { set { return; } }
    public override float APIDDamping { set { return; } }

    public override void AddForce(OMV.Vector3 force, bool pushforce) {
        // Since this force is being applied in only one step, make this a force per second.
        OMV.Vector3 addForce = force / PhysicsScene.LastTimeStep;
        AddForce(addForce, pushforce, false);
    }
    // Applying a force just adds this to the total force on the object.
    // This added force will only last the next simulation tick.
    public void AddForce(OMV.Vector3 force, bool pushforce, bool inTaintTime) {
        // for an object, doesn't matter if force is a pushforce or not
        if (force.IsFinite())
        {
            float magnitude = force.Length();
            if (magnitude > 20000f)
            {
                // Force has a limit
                force = force / magnitude * 20000f;
            }

            OMV.Vector3 addForce = force;
            DetailLog("{0},BSPrim.addForce,call,force={1}", LocalID, addForce);

            PhysicsScene.TaintedObject(inTaintTime, "BSPrim.AddForce", delegate()
            {
                // Bullet adds this central force to the total force for this tick
                DetailLog("{0},BSPrim.addForce,taint,force={1}", LocalID, addForce);
                if (PhysBody.HasPhysicalBody)
                {
                    BulletSimAPI.ApplyCentralForce2(PhysBody.ptr, addForce);
                    ActivateIfPhysical(false);
                }
            });
        }
        else
        {
            m_log.WarnFormat("{0}: Got a NaN force applied to a prim. LocalID={1}", LogHeader, LocalID);
            return;
        }
    }

    public override void AddAngularForce(OMV.Vector3 force, bool pushforce) {
        AddAngularForce(force, pushforce, false);
    }
    public void AddAngularForce(OMV.Vector3 force, bool pushforce, bool inTaintTime)
    {
        if (force.IsFinite())
        {
            OMV.Vector3 angForce = force;
            PhysicsScene.TaintedObject(inTaintTime, "BSPrim.AddAngularForce", delegate()
            {
                if (PhysBody.HasPhysicalBody)
                {
                    BulletSimAPI.ApplyTorque2(PhysBody.ptr, angForce);
                    ActivateIfPhysical(false);
                }
            });
        }
        else
        {
            m_log.WarnFormat("{0}: Got a NaN force applied to a prim. LocalID={1}", LogHeader, LocalID);
            return;
        }
    }

    // A torque impulse.
    // ApplyTorqueImpulse adds torque directly to the angularVelocity.
    // AddAngularForce accumulates the force and applied it to the angular velocity all at once.
    // Computed as: angularVelocity += impulse * inertia;
    public void ApplyTorqueImpulse(OMV.Vector3 impulse, bool inTaintTime)
    {
        OMV.Vector3 applyImpulse = impulse;
        PhysicsScene.TaintedObject(inTaintTime, "BSPrim.ApplyTorqueImpulse", delegate()
        {
            if (PhysBody.HasPhysicalBody)
            {
                BulletSimAPI.ApplyTorqueImpulse2(PhysBody.ptr, applyImpulse);
                ActivateIfPhysical(false);
            }
        });
    }

    public override void SetMomentum(OMV.Vector3 momentum) {
        // DetailLog("{0},BSPrim.SetMomentum,call,mom={1}", LocalID, momentum);
    }
    #region Mass Calculation

    private float CalculateMass()
    {
        float volume = _size.X * _size.Y * _size.Z; // default
        float tmp;

        float returnMass = 0;
        float hollowAmount = (float)BaseShape.ProfileHollow * 2.0e-5f;
        float hollowVolume = hollowAmount * hollowAmount;

        switch (BaseShape.ProfileShape)
        {
            case ProfileShape.Square:
                // default box

                if (BaseShape.PathCurve == (byte)Extrusion.Straight)
                    {
                    if (hollowAmount > 0.0)
                        {
                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Square:
                            case HollowShape.Same:
                                break;

                            case HollowShape.Circle:

                                hollowVolume *= 0.78539816339f;
                                break;

                            case HollowShape.Triangle:

                                hollowVolume *= (0.5f * .5f);
                                break;

                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }

                else if (BaseShape.PathCurve == (byte)Extrusion.Curve1)
                    {
                    //a tube

                    volume *= 0.78539816339e-2f * (float)(200 - BaseShape.PathScaleX);
                    tmp= 1.0f -2.0e-2f * (float)(200 - BaseShape.PathScaleY);
                    volume -= volume*tmp*tmp;

                    if (hollowAmount > 0.0)
                        {
                        hollowVolume *= hollowAmount;

                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Square:
                            case HollowShape.Same:
                                break;

                            case HollowShape.Circle:
                                hollowVolume *= 0.78539816339f;;
                                break;

                            case HollowShape.Triangle:
                                hollowVolume *= 0.5f * 0.5f;
                                break;
                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }

                break;

            case ProfileShape.Circle:

                if (BaseShape.PathCurve == (byte)Extrusion.Straight)
                    {
                    volume *= 0.78539816339f; // elipse base

                    if (hollowAmount > 0.0)
                        {
                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Same:
                            case HollowShape.Circle:
                                break;

                            case HollowShape.Square:
                                hollowVolume *= 0.5f * 2.5984480504799f;
                                break;

                            case HollowShape.Triangle:
                                hollowVolume *= .5f * 1.27323954473516f;
                                break;

                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }

                else if (BaseShape.PathCurve == (byte)Extrusion.Curve1)
                    {
                    volume *= 0.61685027506808491367715568749226e-2f * (float)(200 - BaseShape.PathScaleX);
                    tmp = 1.0f - .02f * (float)(200 - BaseShape.PathScaleY);
                    volume *= (1.0f - tmp * tmp);

                    if (hollowAmount > 0.0)
                        {

                        // calculate the hollow volume by it's shape compared to the prim shape
                        hollowVolume *= hollowAmount;

                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Same:
                            case HollowShape.Circle:
                                break;

                            case HollowShape.Square:
                                hollowVolume *= 0.5f * 2.5984480504799f;
                                break;

                            case HollowShape.Triangle:
                                hollowVolume *= .5f * 1.27323954473516f;
                                break;

                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }
                break;

            case ProfileShape.HalfCircle:
                if (BaseShape.PathCurve == (byte)Extrusion.Curve1)
                {
                volume *= 0.52359877559829887307710723054658f;
                }
                break;

            case ProfileShape.EquilateralTriangle:

                if (BaseShape.PathCurve == (byte)Extrusion.Straight)
                    {
                    volume *= 0.32475953f;

                    if (hollowAmount > 0.0)
                        {

                        // calculate the hollow volume by it's shape compared to the prim shape
                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Same:
                            case HollowShape.Triangle:
                                hollowVolume *= .25f;
                                break;

                            case HollowShape.Square:
                                hollowVolume *= 0.499849f * 3.07920140172638f;
                                break;

                            case HollowShape.Circle:
                                // Hollow shape is a perfect cyllinder in respect to the cube's scale
                                // Cyllinder hollow volume calculation

                                hollowVolume *= 0.1963495f * 3.07920140172638f;
                                break;

                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }
                else if (BaseShape.PathCurve == (byte)Extrusion.Curve1)
                    {
                    volume *= 0.32475953f;
                    volume *= 0.01f * (float)(200 - BaseShape.PathScaleX);
                    tmp = 1.0f - .02f * (float)(200 - BaseShape.PathScaleY);
                    volume *= (1.0f - tmp * tmp);

                    if (hollowAmount > 0.0)
                        {

                        hollowVolume *= hollowAmount;

                        switch (BaseShape.HollowShape)
                            {
                            case HollowShape.Same:
                            case HollowShape.Triangle:
                                hollowVolume *= .25f;
                                break;

                            case HollowShape.Square:
                                hollowVolume *= 0.499849f * 3.07920140172638f;
                                break;

                            case HollowShape.Circle:

                                hollowVolume *= 0.1963495f * 3.07920140172638f;
                                break;

                            default:
                                hollowVolume = 0;
                                break;
                            }
                        volume *= (1.0f - hollowVolume);
                        }
                    }
                    break;

            default:
                break;
            }



        float taperX1;
        float taperY1;
        float taperX;
        float taperY;
        float pathBegin;
        float pathEnd;
        float profileBegin;
        float profileEnd;

        if (BaseShape.PathCurve == (byte)Extrusion.Straight || BaseShape.PathCurve == (byte)Extrusion.Flexible)
            {
            taperX1 = BaseShape.PathScaleX * 0.01f;
            if (taperX1 > 1.0f)
                taperX1 = 2.0f - taperX1;
            taperX = 1.0f - taperX1;

            taperY1 = BaseShape.PathScaleY * 0.01f;
            if (taperY1 > 1.0f)
                taperY1 = 2.0f - taperY1;
            taperY = 1.0f - taperY1;
            }
        else
            {
            taperX = BaseShape.PathTaperX * 0.01f;
            if (taperX < 0.0f)
                taperX = -taperX;
            taperX1 = 1.0f - taperX;

            taperY = BaseShape.PathTaperY * 0.01f;
            if (taperY < 0.0f)
                taperY = -taperY;
            taperY1 = 1.0f - taperY;

            }


        volume *= (taperX1 * taperY1 + 0.5f * (taperX1 * taperY + taperX * taperY1) + 0.3333333333f * taperX * taperY);

        pathBegin = (float)BaseShape.PathBegin * 2.0e-5f;
        pathEnd = 1.0f - (float)BaseShape.PathEnd * 2.0e-5f;
        volume *= (pathEnd - pathBegin);

        // this is crude aproximation
        profileBegin = (float)BaseShape.ProfileBegin * 2.0e-5f;
        profileEnd = 1.0f - (float)BaseShape.ProfileEnd * 2.0e-5f;
        volume *= (profileEnd - profileBegin);

        returnMass = _density * volume;

        /* Comment out code that computes the mass of the linkset. That is done in the Linkset class.
        if (IsRootOfLinkset)
        {
            foreach (BSPrim prim in _childrenPrims)
            {
                returnMass += prim.CalculateMass();
            }
        }
         */

        returnMass = Util.Clamp(returnMass, BSParam.MinimumObjectMass, BSParam.MaximumObjectMass);

        return returnMass;
    }// end CalculateMass
    #endregion Mass Calculation

    // Rebuild the geometry and object.
    // This is called when the shape changes so we need to recreate the mesh/hull.
    // Called at taint-time!!!
    public void CreateGeomAndObject(bool forceRebuild)
    {
        // If this prim is part of a linkset, we must remove and restore the physical
        //    links if the body is rebuilt.
        bool needToRestoreLinkset = false;
        bool needToRestoreVehicle = false;

        // Create the correct physical representation for this type of object.
        // Updates PhysBody and PhysShape with the new information.
        // Ignore 'forceRebuild'. This routine makes the right choices and changes of necessary.
        PhysicsScene.Shapes.GetBodyAndShape(false, PhysicsScene.World, this, null, delegate(BulletBody dBody)
        {
            // Called if the current prim body is about to be destroyed.
            // Remove all the physical dependencies on the old body.
            // (Maybe someday make the changing of BSShape an event to be subscribed to by BSLinkset, ...)
            needToRestoreLinkset = Linkset.RemoveBodyDependencies(this);
            needToRestoreVehicle = _vehicle.RemoveBodyDependencies(this);
        });

        if (needToRestoreLinkset)
        {
            // If physical body dependencies were removed, restore them
            Linkset.RestoreBodyDependencies(this);
        }
        if (needToRestoreVehicle)
        {
            // If physical body dependencies were removed, restore them
            _vehicle.RestoreBodyDependencies(this);
        }

        // Make sure the properties are set on the new object
        UpdatePhysicalParameters();
        return;
    }

    // The physics engine says that properties have updated. Update same and inform
    // the world that things have changed.
    // TODO: do we really need to check for changed? Maybe just copy values and call RequestPhysicsterseUpdate()
    enum UpdatedProperties {
        Position      = 1 << 0,
        Rotation      = 1 << 1,
        Velocity      = 1 << 2,
        Acceleration  = 1 << 3,
        RotationalVel = 1 << 4
    }

    const float ROTATION_TOLERANCE = 0.01f;
    const float VELOCITY_TOLERANCE = 0.001f;
    const float POSITION_TOLERANCE = 0.05f;
    const float ACCELERATION_TOLERANCE = 0.01f;
    const float ROTATIONAL_VELOCITY_TOLERANCE = 0.01f;

    public override void UpdateProperties(EntityProperties entprop)
    {
        // Updates only for individual prims and for the root object of a linkset.
        if (Linkset.IsRoot(this))
        {
            // A temporary kludge to suppress the rotational effects introduced on vehicles by Bullet
            // TODO: handle physics introduced by Bullet with computed vehicle physics.
            if (_vehicle.IsActive)
            {
                entprop.RotationalVelocity = OMV.Vector3.Zero;
            }

            // Assign directly to the local variables so the normal set action does not happen
            _position = entprop.Position;
            _orientation = entprop.Rotation;
            _velocity = entprop.Velocity;
            _acceleration = entprop.Acceleration;
            _rotationalVelocity = entprop.RotationalVelocity;

            // The sanity check can change the velocity and/or position.
            if (IsPhysical && PositionSanityCheck(true))
            {
                entprop.Position = _position;
                entprop.Velocity = _velocity;
            }

            OMV.Vector3 direction = OMV.Vector3.UnitX * _orientation;   // DEBUG DEBUG DEBUG
            DetailLog("{0},BSPrim.UpdateProperties,call,pos={1},orient={2},dir={3},vel={4},rotVel={5}",
                    LocalID, _position, _orientation, direction, _velocity, _rotationalVelocity);

            // remember the current and last set values
            LastEntityProperties = CurrentEntityProperties;
            CurrentEntityProperties = entprop;

            base.RequestPhysicsterseUpdate();
        }
            /*
        else
        {
            // For debugging, report the movement of children
            DetailLog("{0},BSPrim.UpdateProperties,child,pos={1},orient={2},vel={3},accel={4},rotVel={5}",
                    LocalID, entprop.Position, entprop.Rotation, entprop.Velocity,
                    entprop.Acceleration, entprop.RotationalVelocity);
        }
             */

        // The linkset implimentation might want to know about this.
        Linkset.UpdateProperties(this, true);
    }
}
}
