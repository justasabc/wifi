/*
 * Copyright (c) Contributors 
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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
using Mono.Addins;

using System;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Net;
using System.Net.Sockets;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenSim.Region.OptionalModules.Scripting.JsonStore
{
    public class JsonStore
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private OSD m_ValueStore;

        protected class TakeValueCallbackClass
        {
            public string Path { get; set; }
            public bool UseJson { get; set; }
            public TakeValueCallback Callback { get; set; }

            public TakeValueCallbackClass(string spath, bool usejson, TakeValueCallback cback)
            {
                Path = spath;
                UseJson = usejson;
                Callback = cback;
            }
        }

        protected List<TakeValueCallbackClass> m_TakeStore;
        protected List<TakeValueCallbackClass> m_ReadStore;
        

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public JsonStore() : this("") {}
        
        public JsonStore(string value)
        {
            m_TakeStore = new List<TakeValueCallbackClass>();
            m_ReadStore = new List<TakeValueCallbackClass>();
           
            if (String.IsNullOrEmpty(value))
                m_ValueStore = new OSDMap();
            else
                m_ValueStore = OSDParser.DeserializeJson(value);
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool TestPath(string expr, bool useJson)
        {
            Stack<string> path = ParsePathExpression(expr);
            OSD result = ProcessPathExpression(m_ValueStore,path);

            if (result == null)
                return false;
            
            if (useJson || result.Type == OSDType.String)
                return true;
            
            return false;
        }
        
        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool GetValue(string expr, out string value, bool useJson)
        {
            Stack<string> path = ParsePathExpression(expr);
            OSD result = ProcessPathExpression(m_ValueStore,path);
            return ConvertOutputValue(result,out value,useJson); 
        }
     
                
        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool RemoveValue(string expr)
        {
            return SetValueFromExpression(expr,null);
        }
       
        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool SetValue(string expr, string value, bool useJson)
        {
            OSD ovalue = useJson ? OSDParser.DeserializeJson(value) :  new OSDString(value);
            return SetValueFromExpression(expr,ovalue);
        }
        
        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool TakeValue(string expr, bool useJson, TakeValueCallback cback)
        {
            Stack<string> path = ParsePathExpression(expr);
            string pexpr = PathExpressionToKey(path);

            OSD result = ProcessPathExpression(m_ValueStore,path);
            if (result == null)
            {
                m_TakeStore.Add(new TakeValueCallbackClass(pexpr,useJson,cback));
                return false;
            }
            
            string value = String.Empty;
            if (! ConvertOutputValue(result,out value,useJson))
            {
                // the structure does not match the request so i guess we'll wait
                m_TakeStore.Add(new TakeValueCallbackClass(pexpr,useJson,cback));
                return false;
            }

            SetValueFromExpression(expr,null);
            cback(value);

            return true;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        public bool ReadValue(string expr, bool useJson, TakeValueCallback cback)
        {
            Stack<string> path = ParsePathExpression(expr);
            string pexpr = PathExpressionToKey(path);

            OSD result = ProcessPathExpression(m_ValueStore,path);
            if (result == null)
            {
                m_ReadStore.Add(new TakeValueCallbackClass(pexpr,useJson,cback));
                return false;
            }
            
            string value = String.Empty;
            if (! ConvertOutputValue(result,out value,useJson))
            {
                // the structure does not match the request so i guess we'll wait
                m_ReadStore.Add(new TakeValueCallbackClass(pexpr,useJson,cback));
                return false;
            }

            cback(value);

            return true;
        }
     
        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        protected bool SetValueFromExpression(string expr, OSD ovalue)
        {
            Stack<string> path = ParsePathExpression(expr);
            if (path.Count == 0)
            {
                m_ValueStore = ovalue;
                return true;
            }

            string pkey = path.Pop();
            string pexpr = PathExpressionToKey(path);
            if (pexpr != "")
                pexpr += ".";

            OSD result = ProcessPathExpression(m_ValueStore,path);
            if (result == null)
                return false;

            Regex aPattern = new Regex("\\[([0-9]+|\\+)\\]");
            MatchCollection amatches = aPattern.Matches(pkey,0);
            
            if (amatches.Count > 0)
            {
                if (result.Type != OSDType.Array)
                    return false;

                OSDArray amap = result as OSDArray;

                Match match = amatches[0];
                GroupCollection groups = match.Groups;
                string akey = groups[1].Value;

                if (akey == "+")
                {
                    string npkey = String.Format("[{0}]",amap.Count);

                    amap.Add(ovalue);
                    InvokeNextCallback(pexpr + npkey);
                    return true;
                }

                int aval = Convert.ToInt32(akey);
                if (0 <= aval && aval < amap.Count)
                {
                    if (ovalue == null)
                        amap.RemoveAt(aval);
                    else
                    {
                        amap[aval] = ovalue;
                        InvokeNextCallback(pexpr + pkey);
                    }
                    return true;
                }

                return false;
            }

            Regex hPattern = new Regex("{([^}]+)}");
            MatchCollection hmatches = hPattern.Matches(pkey,0);
            
            if (hmatches.Count > 0)
            {
                Match match = hmatches[0];
                GroupCollection groups = match.Groups;
                string hkey = groups[1].Value;
                
                if (result is OSDMap)
                {
                    OSDMap hmap = result as OSDMap;
                    if (ovalue != null)
                    {
                        hmap[hkey] = ovalue;
                        InvokeNextCallback(pexpr + pkey);
                    }
                    else if (hmap.ContainsKey(hkey))
                        hmap.Remove(hkey);
                    
                    return true;
                }

                return false;
            }

            // Shouldn't get here if the path was checked correctly
            m_log.WarnFormat("[JsonStore] invalid path expression");
            return false;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        protected bool InvokeNextCallback(string pexpr)
        {
            // Process all of the reads that match the expression first
            List<TakeValueCallbackClass> reads = 
                m_ReadStore.FindAll(delegate(TakeValueCallbackClass tb) { return pexpr.StartsWith(tb.Path); });

            foreach (TakeValueCallbackClass readcb in reads)
            {
                m_ReadStore.Remove(readcb);
                ReadValue(readcb.Path,readcb.UseJson,readcb.Callback);
            }

            // Process one take next
            TakeValueCallbackClass takecb =
                m_TakeStore.Find(delegate(TakeValueCallbackClass tb) { return pexpr.StartsWith(tb.Path); });
                                                               
            if (takecb != null)
            {
                m_TakeStore.Remove(takecb);
                TakeValue(takecb.Path,takecb.UseJson,takecb.Callback);

                return true;
            }

            return false;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// Parse the path expression and put the components into a stack. We
        /// use a stack because we process the path in inverse order later
        /// </summary>
        // -----------------------------------------------------------------
        protected static Stack<string> ParsePathExpression(string path)
        {
            Stack<string> m_path = new Stack<string>();

            // add front and rear separators
            path = "." + path + ".";
            
            // add separators for quoted paths
            Regex pass1 = new Regex("{[^}]+}");
            path = pass1.Replace(path,".$0.",-1,0);
                
            // add separators for array references
            Regex pass2 = new Regex("(\\[[0-9]+\\]|\\[\\+\\])");
            path = pass2.Replace(path,".$0.",-1,0);
                
            // add quotes to bare identifier
            Regex pass3 = new Regex("\\.([a-zA-Z]+)");
            path = pass3.Replace(path,".{$1}",-1,0);
                
            // remove extra separators
            Regex pass4 = new Regex("\\.+");
            path = pass4.Replace(path,".",-1,0);

            Regex validate = new Regex("^\\.(({[^}]+}|\\[[0-9]+\\]|\\[\\+\\])\\.)+$");
            if (validate.IsMatch(path))
            {
                Regex parser = new Regex("\\.({[^}]+}|\\[[0-9]+\\]|\\[\\+\\]+)");
                MatchCollection matches = parser.Matches(path,0);
                foreach (Match match in matches)
                    m_path.Push(match.Groups[1].Value);
            }

            return m_path;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        /// <param>path is a stack where the top level of the path is at the bottom of the stack</param>
        // -----------------------------------------------------------------
        protected static OSD ProcessPathExpression(OSD map, Stack<string> path)
        {
            if (path.Count == 0)
                return map;
            
            string pkey = path.Pop();

            OSD rmap = ProcessPathExpression(map,path);
            if (rmap == null)
                return null;
            
            // ---------- Check for an array index ----------
            Regex aPattern = new Regex("\\[([0-9]+)\\]");
            MatchCollection amatches = aPattern.Matches(pkey,0);
            
            if (amatches.Count > 0)
            {
                if (rmap.Type != OSDType.Array)
                {
                    m_log.WarnFormat("[JsonStore] wrong type for key {2}, expecting {0}, got {1}",OSDType.Array,rmap.Type,pkey);
                    return null;
                }

                OSDArray amap = rmap as OSDArray;

                Match match = amatches[0];
                GroupCollection groups = match.Groups;
                string akey = groups[1].Value;
                int aval = Convert.ToInt32(akey);
                
                if (aval < amap.Count)
                    return (OSD) amap[aval];

                return null;
            }

            // ---------- Check for a hash index ----------
            Regex hPattern = new Regex("{([^}]+)}");
            MatchCollection hmatches = hPattern.Matches(pkey,0);
            
            if (hmatches.Count > 0)
            {
                if (rmap.Type != OSDType.Map)
                {
                    m_log.WarnFormat("[JsonStore] wrong type for key {2}, expecting {0}, got {1}",OSDType.Map,rmap.Type,pkey);
                    return null;
                }
                
                OSDMap hmap = rmap as OSDMap;

                Match match = hmatches[0];
                GroupCollection groups = match.Groups;
                string hkey = groups[1].Value;
                
                if (hmap.ContainsKey(hkey))
                    return (OSD) hmap[hkey];

                return null;
            }

            // Shouldn't get here if the path was checked correctly
            m_log.WarnFormat("[JsonStore] Path type (unknown) does not match the structure");
            return null;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        protected static bool ConvertOutputValue(OSD result, out string value, bool useJson)
        {
            value = String.Empty;
            
            // If we couldn't process the path
            if (result == null)
                return false;

            if (useJson)
            {
                // The path pointed to an intermediate hash structure
                if (result.Type == OSDType.Map)
                {
                    value = OSDParser.SerializeJsonString(result as OSDMap);
                    return true;
                }

                // The path pointed to an intermediate hash structure
                if (result.Type == OSDType.Array)
                {
                    value = OSDParser.SerializeJsonString(result as OSDArray);
                    return true;
                }

                value = "'" + result.AsString() + "'"; 
                return true;
            }

            if (result.Type == OSDType.String)
            {
                value = result.AsString(); 
                return true;
            }

            return false;
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// 
        /// </summary>
        // -----------------------------------------------------------------
        protected static string PathExpressionToKey(Stack<string> path)
        {
            if (path.Count == 0)
                return "";
                
            string pkey = "";
            foreach (string k in path)
                pkey = (pkey == "") ? k : (k + "." + pkey);
            
            return pkey;
        }
    }
}
