// General editor tools for the Unity VRC MCP bridge: status, console, hierarchy, inspection, editing, capture.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        static readonly Dictionary<string, Func<JObject, JToken>> Handlers =
            new Dictionary<string, Func<JObject, JToken>>(StringComparer.OrdinalIgnoreCase)
            {
                ["unity_status"] = Status,
                ["console_logs"] = ConsoleLogs,
                ["hierarchy"] = Hierarchy,
                ["find_objects"] = FindObjects,
                ["inspect_object"] = InspectObject,
                ["set_property"] = SetProperty,
                ["blendshapes"] = Blendshapes,
                ["find_assets"] = FindAssetsTool,
                ["select"] = SelectObject,
                ["execute_menu_item"] = ExecuteMenuItem,
                ["play_mode"] = SetPlayMode,
                ["refresh_assets"] = RefreshAssets,
                ["invoke_static_method"] = InvokeStaticMethod,
                ["capture_view"] = CaptureView,
                ["avatar_report"] = AvatarReport,
                ["inspect_animator"] = InspectAnimator,
                ["inspect_clip"] = InspectClip,
                ["av3_state"] = Av3State,
                ["av3_set"] = Av3Set,
                ["av3_menu"] = Av3Menu,
                ["av3_record"] = Av3Record,
                ["execute_code"] = UnityVrcMcpExecuteCode.Execute,
                ["batch_execute"] = BatchExecute,
                ["edit_animator"] = EditAnimator,
                ["job_status"] = McpJobs.Status,
                ["job_cancel"] = McpJobs.Cancel,
            };

        public static JToken Dispatch(string tool, JObject args)
        {
            if (tool == null || !Handlers.TryGetValue(tool, out var handler))
            {
                if (tool != null && tool.StartsWith("vrc_", StringComparison.Ordinal))
                    throw new ArgumentException($"'{tool}' needs the VRChat Avatars SDK (com.vrchat.avatars 3.4+), which this project doesn't have.");
                throw new ArgumentException($"Unknown tool '{tool}'. Known: {string.Join(", ", Handlers.Keys)}");
            }

            return handler(args ?? new JObject());
        }

        const int MaxBatch = 50;

        /// <summary>Runs several tools in one request (one main-thread pass), returning each result or error.</summary>
        static JToken BatchExecute(JObject a)
        {
            var commands = Arg(a, "commands") as JArray ?? throw new ArgumentException("'commands' must be an array of {tool, args}.");
            if (commands.Count > MaxBatch) throw new ArgumentException($"At most {MaxBatch} commands per batch.");
            var stopOnError = Bool(a, "stopOnError", false);
            var results = new JArray();
            var failed = 0;
            foreach (var c in commands)
            {
                var tool = (string)c["tool"];
                if (string.Equals(tool, "batch_execute", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("batch_execute can't be nested.");
                try
                {
                    results.Add(new JObject { ["tool"] = tool, ["ok"] = true, ["result"] = Dispatch(tool, c["args"] as JObject ?? new JObject()) });
                }
                catch (Exception e)
                {
                    var inner = e is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    failed++;
                    results.Add(new JObject { ["tool"] = tool, ["ok"] = false, ["error"] = $"{inner.GetType().Name}: {inner.Message}" });
                    if (stopOnError) break;
                }
            }

            return new JObject { ["count"] = results.Count, ["failed"] = failed, ["results"] = results };
        }

        /// <summary>Lets optional assemblies (e.g. the VRChat SDK tools) add tools.</summary>
        internal static void RegisterTool(string name, Func<JObject, JToken> handler) => Handlers[name] = handler;

        // ---------- argument helpers ----------

        internal static JToken Arg(JObject a, string k) =>
            a.TryGetValue(k, StringComparison.OrdinalIgnoreCase, out var t) && t.Type != JTokenType.Null ? t : null;

        internal static string Str(JObject a, string k)
        {
            var t = Arg(a, k);
            var s = t?.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        internal static int Int(JObject a, string k, int d)
        {
            var t = Arg(a, k);
            try { return t != null ? t.Value<int>() : d; }
            catch { return d; }
        }

        internal static float Float(JObject a, string k, float d)
        {
            var t = Arg(a, k);
            try { return t != null ? t.Value<float>() : d; }
            catch { return d; }
        }

        internal static bool Bool(JObject a, string k, bool d)
        {
            var t = Arg(a, k);
            try { return t != null ? t.Value<bool>() : d; }
            catch { return d; }
        }

        internal static string Required(JObject a, string k) =>
            Str(a, k) ?? throw new ArgumentException($"'{k}' is required.");

        // ---------- formatting helpers ----------

        internal static double R(double v) => Math.Round(v, 4);
        static JArray V2(Vector2 v) => new JArray(R(v.x), R(v.y));
        internal static JArray V3(Vector3 v) => new JArray(R(v.x), R(v.y), R(v.z));
        static JArray V4(Vector4 v) => new JArray(R(v.x), R(v.y), R(v.z), R(v.w));
        static JArray Col(Color c) => new JArray(R(c.r), R(c.g), R(c.b), R(c.a));

        internal static string GetPath(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static JToken ObjRef(Object o)
        {
            if (!o) return null;
            var ap = AssetDatabase.GetAssetPath(o);
            var where = !string.IsNullOrEmpty(ap) ? ap
                : o is Component c ? GetPath(c.transform)
                : o is GameObject g ? GetPath(g.transform)
                : "";
            return $"{o.name} ({o.GetType().Name})" + (where.Length > 0 ? $" @ {where}" : "");
        }

        // ---------- reflection helpers ----------

        static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        internal static Type FindType(string name)
        {
            if (TypeCache.TryGetValue(name, out var cached)) return cached;
            Type found = null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                found = asm.GetType(name, false);
                if (found != null) break;
            }

            if (found == null)
            {
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    found = types.FirstOrDefault(t => t.Name == name || t.FullName == name);
                    if (found != null) break;
                }
            }

            TypeCache[name] = found;
            return found;
        }

        static object Get(object o, string member)
        {
            if (o == null) return null;
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var t = o.GetType();
            var f = t.GetField(member, bf);
            if (f != null) return f.GetValue(o);
            var p = t.GetProperty(member, bf);
            return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o) : null;
        }

        static bool TypeMatches(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
                if (string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ---------- object resolution ----------

        internal static IEnumerable<GameObject> AllRoots()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot) yield return stage.prefabContentsRoot;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (var r in s.GetRootGameObjects()) yield return r;
            }
        }

        internal static GameObject FindGameObject(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("'path' is required.");
            if (path.StartsWith('#') && int.TryParse(path.Substring(1), out var id))
            {
                var o = EditorUtility.InstanceIDToObject(id);
                if (o is GameObject g) return g;
                if (o is Component c) return c.gameObject;
                throw new ArgumentException($"No GameObject with instance id {id}.");
            }

            path = path.Trim('/');
            foreach (var root in AllRoots())
            {
                var rn = root.name;
                if (path == rn) return root;
                if (path.StartsWith(rn + "/"))
                {
                    var t = root.transform.Find(path.Substring(rn.Length + 1));
                    if (t) return t.gameObject;
                }
            }

            // Fallback: unique object name anywhere.
            var matches = AllRoots().SelectMany(r => r.GetComponentsInChildren<Transform>(true))
                .Where(t => t.name == path).Take(10).ToList();
            if (matches.Count == 1) return matches[0].gameObject;
            if (matches.Count > 1)
                throw new ArgumentException($"'{path}' is ambiguous: {string.Join(" | ", matches.Select(GetPath))}");
            throw new ArgumentException($"GameObject '{path}' not found. Use a full path such as 'Root/Child' (see the hierarchy tool).");
        }

        static Object LoadAsset(string assetPath, string sub, string wantedType = null)
        {
            if (sub != null)
                return AssetDatabase.LoadAllAssetsAtPath(assetPath).FirstOrDefault(o => o && o.name == sub &&
                           (wantedType == null || TypeMatches(o.GetType(), wantedType)))
                       ?? throw new ArgumentException($"No sub-asset '{sub}' in {assetPath}.");
            var main = AssetDatabase.LoadMainAssetAtPath(assetPath)
                       ?? throw new ArgumentException($"No asset at '{assetPath}'.");
            if (wantedType == null || TypeMatches(main.GetType(), wantedType)) return main;
            return AssetDatabase.LoadAllAssetsAtPath(assetPath).FirstOrDefault(o => o && TypeMatches(o.GetType(), wantedType)) ?? main;
        }

        /// <summary>Resolves path|assetPath (+subAsset, childPath, component, index) to an object.</summary>
        static Object ResolveTarget(JObject a)
        {
            var assetPath = Str(a, "assetPath");
            var path = Str(a, "path");
            var comp = Str(a, "component");
            Object obj;
            if (assetPath != null)
            {
                obj = LoadAsset(assetPath, Str(a, "subAsset"));
                var child = Str(a, "childPath");
                if (child != null)
                {
                    if (!(obj is GameObject pg)) throw new ArgumentException("'childPath' needs a prefab or model asset.");
                    var t = pg.transform.Find(child);
                    obj = t ? t.gameObject : throw new ArgumentException($"'{child}' not found under {assetPath}.");
                }
            }
            else if (path != null) obj = FindGameObject(path);
            else throw new ArgumentException("Pass 'path' (scene GameObject path or #instanceId) or 'assetPath'.");

            if (comp == null) return obj;
            if (!(obj is GameObject go)) throw new ArgumentException("'component' only applies to GameObjects.");
            var comps = go.GetComponents<Component>().Where(c => c != null && TypeMatches(c.GetType(), comp)).ToList();
            if (comps.Count == 0)
                throw new ArgumentException($"No '{comp}' on {GetPath(go.transform)}. Components: " +
                                            string.Join(", ", go.GetComponents<Component>().Select(c => c ? c.GetType().Name : "<Missing Script>")));
            var index = Int(a, "index", 0);
            if (index < 0 || index >= comps.Count) throw new ArgumentException($"index {index} out of range (found {comps.Count}).");
            return comps[index];
        }

        // ---------- unity_status ----------

        static JToken Status(JObject a)
        {
            var scenes = new JArray();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                scenes.Add(new JObject
                {
                    ["name"] = s.name, ["path"] = s.path, ["isLoaded"] = s.isLoaded, ["isDirty"] = s.isDirty,
                    ["roots"] = s.isLoaded ? new JArray(s.GetRootGameObjects().Select(g => g.name)) : null,
                });
            }

            var res = new JObject
            {
                ["project"] = UnityVrcMcpBridge.ProjectRoot,
                ["unityVersion"] = Application.unityVersion,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["scenes"] = scenes,
                ["prefabStage"] = PrefabStageUtility.GetCurrentPrefabStage()?.assetPath,
                ["selection"] = new JArray(Selection.objects.Take(20).Select(ObjRef)),
                ["compileErrors"] = new JArray(UnityVrcMcpBridge.GetCompileErrors().Take(50)),
            };
            if (TryConsoleCounts(out var e, out var w, out var l))
                res["console"] = new JObject { ["errors"] = e, ["warnings"] = w, ["logs"] = l };
            return res;
        }

        // ---------- console_logs ----------

        const int ErrorMask = 1 | 2 | 16 | 64 | 256 | 2048 | 8192 | 131072 | 1048576 | 2097152 | 4194304;
        const int WarningMask = 128 | 512 | 4096;
        const BindingFlags StaticAny = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static bool TryConsoleCounts(out int errors, out int warnings, out int logs)
        {
            errors = warnings = logs = 0;
            try
            {
                var m = FindType("UnityEditor.LogEntries")?.GetMethod("GetCountsByType", StaticAny);
                if (m == null) return false;
                var args = new object[] { 0, 0, 0 };
                m.Invoke(null, args);
                errors = (int)args[0];
                warnings = (int)args[1];
                logs = (int)args[2];
                return true;
            }
            catch { return false; }
        }

        static JToken ConsoleLogs(JObject a)
        {
            var limit = Int(a, "limit", 30);
            var types = (Str(a, "types") ?? "error,warning,log").ToLowerInvariant();
            var contains = Str(a, "contains");
            var withStack = Bool(a, "stackTrace", true);
            var collapse = Bool(a, "collapse", true);

            var le = FindType("UnityEditor.LogEntries");
            var entryType = FindType("UnityEditor.LogEntry");
            if (le == null || entryType == null)
                return new JObject { ["note"] = "Console internals not found; showing bridge log buffer.", ["entries"] = new JArray(UnityVrcMcpBridge.RecentLogs.Reverse().Take(limit)) };

            if (Bool(a, "clear", false))
            {
                le.GetMethod("Clear", StaticAny)?.Invoke(null, null);
                return new JObject { ["cleared"] = true };
            }

            var start = le.GetMethod("StartGettingEntries", StaticAny);
            var end = le.GetMethod("EndGettingEntries", StaticAny);
            var get = le.GetMethod("GetEntryInternal", StaticAny);
            var fMsg = entryType.GetField("message");
            var fMode = entryType.GetField("mode");
            var fInst = entryType.GetField("instanceID");
            var entry = Activator.CreateInstance(entryType);

            var results = new List<JObject>();
            var byKey = new Dictionary<string, JObject>();
            var total = (int)start.Invoke(null, null);
            try
            {
                for (var i = total - 1; i >= 0 && results.Count < limit; i--)
                {
                    get.Invoke(null, new[] { i, entry });
                    var full = (string)fMsg.GetValue(entry) ?? "";
                    var mode = (int)fMode.GetValue(entry);
                    var type = (mode & ErrorMask) != 0 ? "error" : (mode & WarningMask) != 0 ? "warning" : "log";
                    if (!types.Contains(type)) continue;
                    if (contains != null && full.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var key = type + "|" + full;
                    if (collapse && byKey.TryGetValue(key, out var existing))
                    {
                        existing["count"] = existing.Value<int>("count") + 1;
                        continue;
                    }

                    var nl = full.IndexOf('\n');
                    var first = nl < 0 ? full : full.Substring(0, nl);
                    var o = new JObject { ["type"] = type, ["message"] = first };
                    if (collapse) o["count"] = 1;
                    if (withStack && nl >= 0 && type != "log")
                    {
                        var lines = full.Substring(nl + 1).Split('\n').Where(s => s.Trim().Length > 0).Take(12);
                        o["stack"] = string.Join("\n", lines);
                    }

                    var inst = fInst != null ? (int)fInst.GetValue(entry) : 0;
                    if (inst != 0)
                    {
                        var ctx = EditorUtility.InstanceIDToObject(inst);
                        if (ctx) o["context"] = ObjRef(ctx);
                    }

                    results.Add(o);
                    byKey[key] = o;
                }
            }
            finally
            {
                end.Invoke(null, null);
            }

            results.Reverse(); // oldest first
            return new JObject { ["totalEntries"] = total, ["returned"] = results.Count, ["entries"] = new JArray(results) };
        }

        // ---------- hierarchy / find ----------

        static JToken Hierarchy(JObject a)
        {
            var depth = Int(a, "depth", 2);
            var comps = Bool(a, "components", false);
            var inactive = Bool(a, "includeInactive", true);
            var counter = new int[1];
            var max = Int(a, "maxNodes", 400);
            var path = Str(a, "path");
            if (path != null) return Node(FindGameObject(path).transform, depth, comps, inactive, counter, max, true);

            var res = new JArray();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                res.Add(new JObject { ["prefabStage"] = stage.assetPath, ["roots"] = new JArray(Node(stage.prefabContentsRoot.transform, depth, comps, inactive, counter, max, false)) });
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                res.Add(new JObject
                {
                    ["scene"] = s.path,
                    ["roots"] = new JArray(s.GetRootGameObjects().Select(r => Node(r.transform, depth, comps, inactive, counter, max, false))),
                });
            }

            return res;
        }

        static JObject Node(Transform t, int depth, bool comps, bool inactive, int[] counter, int max, bool fullPath)
        {
            counter[0]++;
            var o = new JObject { ["name"] = fullPath ? GetPath(t) : t.name };
            if (!t.gameObject.activeSelf) o["active"] = false;
            if (comps)
                o["components"] = new JArray(t.GetComponents<Component>().Where(c => !(c is Transform))
                    .Select(c => c ? c.GetType().Name : "<Missing Script>"));
            if (t.childCount == 0) return o;
            if (depth <= 0)
            {
                o["childCount"] = t.childCount;
                return o;
            }

            var children = new JArray();
            foreach (Transform c in t)
            {
                if (!inactive && !c.gameObject.activeSelf) continue;
                if (counter[0] >= max)
                {
                    o["truncated"] = $"node limit {max} reached; query a sub-path";
                    break;
                }

                children.Add(Node(c, depth - 1, comps, inactive, counter, max, false));
            }

            o["children"] = children;
            return o;
        }

        static JToken FindObjects(JObject a)
        {
            var name = Str(a, "name");
            var comp = Str(a, "component");
            var under = Str(a, "under");
            var limit = Int(a, "limit", 100);
            if (name == null && comp == null) throw new ArgumentException("Pass 'name' and/or 'component'.");

            var roots = under != null ? new[] { FindGameObject(under) } : AllRoots();
            var res = new JArray();
            var total = 0;
            foreach (var t in roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)))
            {
                if (name != null && t.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                List<string> matched = null;
                if (comp != null)
                {
                    var cs = t.GetComponents<Component>();
                    matched = comp.Equals("missing", StringComparison.OrdinalIgnoreCase)
                        ? cs.Where(c => c == null).Select(_ => "<Missing Script>").ToList()
                        : cs.Where(c => c != null && TypeMatches(c.GetType(), comp)).Select(c => c.GetType().Name).ToList();
                    if (matched.Count == 0) continue;
                }

                total++;
                if (res.Count >= limit) continue;
                var o = new JObject { ["path"] = GetPath(t) };
                if (!t.gameObject.activeInHierarchy) o["activeInHierarchy"] = false;
                if (matched != null) o["components"] = new JArray(matched.Distinct());
                res.Add(o);
            }

            return new JObject { ["total"] = total, ["results"] = res };
        }

        // ---------- inspect_object ----------

        sealed class DumpOpts
        {
            public int MaxDepth = 4;
            public int MaxArray = 32;
            public bool Hidden;
        }

        static JToken InspectObject(JObject a)
        {
            var target = ResolveTarget(a);
            if (target is GameObject go && Str(a, "component") == null && Str(a, "property") == null)
                return DescribeGameObject(go);
            return DumpObject(target, a);
        }

        static JObject DescribeGameObject(GameObject go)
        {
            var t = go.transform;
            var o = new JObject
            {
                ["path"] = GetPath(t),
                ["instanceId"] = go.GetInstanceID(),
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["localPosition"] = V3(t.localPosition),
                ["localEuler"] = V3(t.localEulerAngles),
                ["localScale"] = V3(t.localScale),
                ["worldPosition"] = V3(t.position),
            };
            var asset = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(asset)) o["asset"] = asset;
            else if (PrefabUtility.IsPartOfPrefabInstance(go))
                o["prefab"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);

            var perType = new Dictionary<string, int>();
            var comps = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    comps.Add("<Missing Script>");
                    continue;
                }

                var tn = c.GetType().Name;
                perType.TryGetValue(tn, out var n);
                perType[tn] = n + 1;
                var label = n > 0 ? $"{tn} [index {n}]" : tn;
                if (c is Behaviour b && !b.enabled) label += " (disabled)";
                if (c is Renderer r && !r.enabled) label += " (disabled)";
                comps.Add(label);
            }

            o["components"] = comps;
            if (t.childCount > 0)
                o["children"] = new JArray(t.Cast<Transform>().Take(60).Select(c => c.gameObject.activeSelf ? c.name : c.name + " (inactive)"));
            o["hint"] = "Pass 'component' (type name, e.g. SkinnedMeshRenderer, VRCPhysBone) to dump its serialized properties.";
            return o;
        }

        static JToken DumpObject(Object obj, JObject a)
        {
            var opts = new DumpOpts { MaxDepth = Int(a, "depth", 4), MaxArray = Int(a, "maxArray", 32), Hidden = Bool(a, "includeHidden", false) };
            var so = new SerializedObject(obj);
            var res = new JObject { ["target"] = ObjRef(obj) };
            var prop = Str(a, "property");
            if (prop != null)
            {
                var p = so.FindProperty(prop) ?? throw new ArgumentException($"Property '{prop}' not found on {ObjRef(obj)}.");
                res["property"] = prop;
                res["type"] = p.type;
                res["value"] = DumpProperty(p, 0, opts);
                return res;
            }

            var filter = Str(a, "filter");
            var props = new JObject();
            var it = so.GetIterator();
            var enter = true;
            while (opts.Hidden ? it.Next(enter) : it.NextVisible(enter))
            {
                enter = false;
                if (!opts.Hidden && it.name == "m_Script") continue;
                if (filter != null && it.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    it.displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                props[it.name] = DumpProperty(it.Copy(), 0, opts);
            }

            res["properties"] = props;
            return res;
        }

        static JToken DumpProperty(SerializedProperty p, int depth, DumpOpts o)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return R(p.doubleValue);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Color: return Col(p.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return p.objectReferenceValue ? ObjRef(p.objectReferenceValue)
                        : p.objectReferenceInstanceIDValue != 0 ? "<Missing reference>" : null;
                case SerializedPropertyType.LayerMask: return p.intValue;
                case SerializedPropertyType.Enum:
                {
                    var names = p.enumDisplayNames;
                    var i = p.enumValueIndex;
                    return i >= 0 && i < names.Length ? names[i] : (JToken)p.intValue;
                }
                case SerializedPropertyType.Vector2: return V2(p.vector2Value);
                case SerializedPropertyType.Vector3: return V3(p.vector3Value);
                case SerializedPropertyType.Vector4: return V4(p.vector4Value);
                case SerializedPropertyType.Quaternion: return V3(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.Rect: return p.rectValue.ToString();
                case SerializedPropertyType.Bounds:
                    return new JObject { ["center"] = V3(p.boundsValue.center), ["size"] = V3(p.boundsValue.size) };
                case SerializedPropertyType.ArraySize: return p.intValue;
                case SerializedPropertyType.Character: return ((char)p.intValue).ToString();
                case SerializedPropertyType.AnimationCurve: return $"AnimationCurve({p.animationCurveValue?.length ?? 0} keys)";
                case SerializedPropertyType.Gradient: return "Gradient";
                case SerializedPropertyType.Vector2Int: return p.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return p.vector3IntValue.ToString();
                case SerializedPropertyType.RectInt: return p.rectIntValue.ToString();
                case SerializedPropertyType.BoundsInt: return p.boundsIntValue.ToString();
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                case SerializedPropertyType.ExposedReference: return ObjRef(p.exposedReferenceValue);
            }

            if (p.isArray)
            {
                var n = p.arraySize;
                if (depth >= o.MaxDepth) return $"[{n} items]";
                var arr = new JArray();
                for (var i = 0; i < Math.Min(n, o.MaxArray); i++)
                    arr.Add(DumpProperty(p.GetArrayElementAtIndex(i), depth + 1, o));
                if (n > o.MaxArray) arr.Add($"... {n - o.MaxArray} more (size {n}; raise maxArray or use 'property')");
                return arr;
            }

            if (p.hasChildren)
            {
                if (depth >= o.MaxDepth) return "{...}";
                var obj = new JObject();
                if (p.propertyType == SerializedPropertyType.ManagedReference)
                    obj["$type"] = p.managedReferenceFullTypename;
                var it = p.Copy();
                var end = p.GetEndProperty();
                var enter = true;
                while ((o.Hidden ? it.Next(enter) : it.NextVisible(enter)) && !SerializedProperty.EqualContents(it, end))
                {
                    enter = false;
                    obj[it.name] = DumpProperty(it.Copy(), depth + 1, o);
                }

                return obj;
            }

            return p.propertyType == SerializedPropertyType.ManagedReference ? (JToken)"null (SerializeReference)" : $"<{p.type}>";
        }

        // ---------- set_property ----------

        static JToken SetProperty(JObject a)
        {
            var target = ResolveTarget(a);
            var prop = Required(a, "property");
            var value = a.TryGetValue("value", StringComparison.OrdinalIgnoreCase, out var v) ? v : throw new ArgumentException("'value' is required (may be null).");
            var so = new SerializedObject(target);
            var p = so.FindProperty(prop) ?? throw new ArgumentException($"Property '{prop}' not found on {ObjRef(target)}. Use inspect_object to list property names.");
            var shallow = new DumpOpts { MaxDepth = 1, MaxArray = 8 };
            var before = DumpProperty(p.Copy(), 0, shallow);
            Assign(p, value);
            so.ApplyModifiedProperties(); // registers Undo and marks the scene/asset dirty
            so.Update();
            return new JObject
            {
                ["target"] = ObjRef(target), ["property"] = prop,
                ["before"] = before, ["after"] = DumpProperty(so.FindProperty(prop), 0, shallow),
                ["note"] = "Not saved yet: save the scene/assets in Unity (or Ctrl+Z to undo).",
            };
        }

        static float[] Floats(JToken v)
        {
            if (v is JArray arr) return arr.Select(x => x.Value<float>()).ToArray();
            if (v is JObject o)
                return new[] { "x", "y", "z", "w" }.Where(k => o[k] != null).Select(k => o[k].Value<float>()).ToArray();
            throw new ArgumentException("Expected an array like [x, y, z].");
        }

        static void Assign(SerializedProperty p, JToken v)
        {
            if (p.isArray && p.propertyType == SerializedPropertyType.Generic)
            {
                p.arraySize = v.Value<int>();
                return;
            }

            float[] f;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: p.longValue = v.Value<long>(); break;
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Character: p.intValue = v.Value<int>(); break;
                case SerializedPropertyType.Boolean: p.boolValue = v.Value<bool>(); break;
                case SerializedPropertyType.Float: p.doubleValue = v.Value<double>(); break;
                case SerializedPropertyType.String: p.stringValue = v.Type == JTokenType.Null ? "" : v.ToString(); break;
                case SerializedPropertyType.Enum:
                    if (v.Type == JTokenType.String)
                    {
                        var s = v.ToString();
                        var idx = Array.FindIndex(p.enumNames, n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) idx = Array.FindIndex(p.enumDisplayNames, n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0) throw new ArgumentException($"'{s}' is not one of: {string.Join(", ", p.enumDisplayNames)}");
                        p.enumValueIndex = idx;
                    }
                    else p.intValue = v.Value<int>();

                    break;
                case SerializedPropertyType.Color:
                    if (v.Type == JTokenType.String && ColorUtility.TryParseHtmlString(v.ToString(), out var c)) p.colorValue = c;
                    else
                    {
                        f = Floats(v);
                        p.colorValue = new Color(f[0], f[1], f[2], f.Length > 3 ? f[3] : 1f);
                    }

                    break;
                case SerializedPropertyType.Vector2: f = Floats(v); p.vector2Value = new Vector2(f[0], f[1]); break;
                case SerializedPropertyType.Vector3: f = Floats(v); p.vector3Value = new Vector3(f[0], f[1], f[2]); break;
                case SerializedPropertyType.Vector4: f = Floats(v); p.vector4Value = new Vector4(f[0], f[1], f[2], f[3]); break;
                case SerializedPropertyType.Quaternion: f = Floats(v); p.quaternionValue = Quaternion.Euler(f[0], f[1], f[2]); break;
                case SerializedPropertyType.ObjectReference: p.objectReferenceValue = ResolveReference(v, p.type); break;
                default:
                    throw new ArgumentException($"Setting {p.propertyType} properties isn't supported; set its child fields, or use invoke_static_method with an editor script.");
            }
        }

        static Object ResolveReference(JToken v, string pptrType)
        {
            if (v == null || v.Type == JTokenType.Null || v.ToString().Length == 0) return null;
            var s = v.ToString();
            // "PPtr<$Material>" -> "Material"
            var wanted = pptrType;
            var dollar = pptrType.IndexOf('$');
            if (dollar >= 0) wanted = pptrType.Substring(dollar + 1).TrimEnd('>');

            Object o;
            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                var parts = s.Split(new[] { "::" }, 2, StringSplitOptions.None);
                o = LoadAsset(parts[0], parts.Length > 1 ? parts[1] : null, wanted == "Object" ? null : wanted);
            }
            else o = FindGameObject(s);

            if (o is GameObject g && wanted != "GameObject" && wanted != "Object")
            {
                var comp = g.GetComponents<Component>().FirstOrDefault(c => c && TypeMatches(c.GetType(), wanted));
                if (comp) return comp;
            }

            return o;
        }

        // ---------- blendshapes ----------

        static JToken Blendshapes(JObject a)
        {
            var go = FindGameObject(Required(a, "path"));
            var smr = go.GetComponent<SkinnedMeshRenderer>() ?? throw new ArgumentException($"No SkinnedMeshRenderer on {GetPath(go.transform)}.");
            var mesh = smr.sharedMesh ? smr.sharedMesh : throw new ArgumentException("SkinnedMeshRenderer has no mesh.");
            var res = new JObject { ["renderer"] = GetPath(go.transform), ["mesh"] = ObjRef(mesh), ["count"] = mesh.blendShapeCount };

            if (Arg(a, "set") is JObject set)
            {
                Undo.RecordObject(smr, "Set blendshapes (Unity VRC MCP)");
                var notFound = new JArray();
                foreach (var kv in set)
                {
                    var idx = mesh.GetBlendShapeIndex(kv.Key);
                    if (idx < 0) notFound.Add(kv.Key);
                    else smr.SetBlendShapeWeight(idx, kv.Value.Value<float>());
                }

                if (notFound.Count > 0) res["notFound"] = notFound;
            }

            var filter = Str(a, "filter");
            var nonZero = Bool(a, "nonZeroOnly", false);
            var limit = Int(a, "limit", 400);
            var list = new JArray();
            for (var i = 0; i < mesh.blendShapeCount && list.Count < limit; i++)
            {
                var n = mesh.GetBlendShapeName(i);
                var w = smr.GetBlendShapeWeight(i);
                if (filter != null && n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (nonZero && Mathf.Approximately(w, 0)) continue;
                list.Add($"{i}: {n} = {R(w)}");
            }

            res["blendshapes"] = list;
            return res;
        }

        // ---------- find_assets / select / menu / play mode / refresh ----------

        static JToken FindAssetsTool(JObject a)
        {
            var filter = Required(a, "filter");
            var folder = Str(a, "folder");
            var limit = Int(a, "limit", 100);
            var guids = folder != null ? AssetDatabase.FindAssets(filter, new[] { folder }) : AssetDatabase.FindAssets(filter);
            var res = new JArray();
            foreach (var g in guids.Take(limit))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                res.Add($"{p} ({AssetDatabase.GetMainAssetTypeAtPath(p)?.Name})");
            }

            return new JObject { ["total"] = guids.Length, ["assets"] = res };
        }

        static JToken SelectObject(JObject a)
        {
            var target = ResolveTarget(a);
            Selection.activeObject = target;
            if (Bool(a, "ping", true)) EditorGUIUtility.PingObject(target);
            if (Bool(a, "frame", false) && SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.FrameSelected();
            return new JObject { ["selected"] = ObjRef(target) };
        }

        static JToken ExecuteMenuItem(JObject a)
        {
            var menu = Required(a, "menuPath");
            return new JObject { ["menuPath"] = menu, ["executed"] = EditorApplication.ExecuteMenuItem(menu) };
        }

        static JToken SetPlayMode(JObject a)
        {
            switch ((Str(a, "state") ?? "").ToLowerInvariant())
            {
                case "play": EditorApplication.EnterPlaymode(); break;
                case "stop": EditorApplication.ExitPlaymode(); break;
                case "pause": EditorApplication.isPaused = true; break;
                case "resume": EditorApplication.isPaused = false; break;
                case "step": EditorApplication.Step(); break;
                default: throw new ArgumentException("state must be play, stop, pause, resume or step.");
            }

            return new JObject { ["requested"] = Str(a, "state"), ["isPlaying"] = EditorApplication.isPlaying };
        }

        static JToken RefreshAssets(JObject a)
        {
            AssetDatabase.Refresh();
            if (Bool(a, "forceCompile", false)) CompilationPipeline.RequestScriptCompilation();
            return new JObject { ["refreshed"] = true, ["isCompiling"] = EditorApplication.isCompiling };
        }

        // ---------- invoke_static_method ----------

        static JToken InvokeStaticMethod(JObject a)
        {
            var typeName = Required(a, "type");
            var methodName = Required(a, "method");
            var args = Arg(a, "args") as JArray ?? new JArray();
            var type = FindType(typeName) ?? throw new ArgumentException($"Type '{typeName}' not found (did the script compile? check unity_status).");
            var candidates = type.GetMethods(StaticAny).Where(m => m.Name == methodName && m.GetParameters().Length == args.Count).ToList();
            if (candidates.Count == 0) throw new ArgumentException($"No static {type.FullName}.{methodName} taking {args.Count} argument(s).");

            Exception lastConvert = null;
            foreach (var m in candidates)
            {
                object[] callArgs;
                try
                {
                    var ps = m.GetParameters();
                    callArgs = ps.Select((p, i) => ConvertArg(args[i], p.ParameterType)).ToArray();
                }
                catch (Exception e)
                {
                    lastConvert = e;
                    continue;
                }

                var logs = new JArray();
                Application.LogCallback cb = (msg, stack, lt) => { if (logs.Count < 200) logs.Add($"{lt}: {msg}"); };
                Application.logMessageReceived += cb;
                object ret;
                try { ret = m.Invoke(null, callArgs); }
                finally { Application.logMessageReceived -= cb; }

                return new JObject { ["method"] = $"{type.FullName}.{m.Name}", ["returned"] = ToJson(ret), ["logs"] = logs };
            }

            throw new ArgumentException($"Arguments don't match {type.FullName}.{methodName}: {lastConvert?.Message}");
        }

        static object ConvertArg(JToken t, Type pt)
        {
            if (typeof(Object).IsAssignableFrom(pt))
            {
                var s = t.ToString();
                return s.StartsWith("Assets/") || s.StartsWith("Packages/") ? LoadAsset(s, null, pt.Name) : (object)ResolveReference(t, "PPtr<$" + pt.Name + ">");
            }

            return t.ToObject(pt);
        }

        internal static JToken ToJson(object o)
        {
            switch (o)
            {
                case null: return null;
                case Object uo: return ObjRef(uo);
                case string s: return s;
                case JToken jt: return jt;
                case Vector3 v: return V3(v);
                case IEnumerable e when !(o is IDictionary):
                    return new JArray(e.Cast<object>().Take(500).Select(ToJson));
            }

            if (o.GetType().IsPrimitive || o is decimal || o is Enum) return JToken.FromObject(o is Enum ? o.ToString() : o);
            try
            {
                return JToken.FromObject(o, JsonSerializer.Create(new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore, MaxDepth = 4 }));
            }
            catch
            {
                return o.ToString();
            }
        }

        // ---------- capture_view ----------

        /// <summary>World bounds of the posed geometry (skinned meshes often carry large custom bounds).</summary>
        static Bounds TightBounds(Renderer r)
        {
            if (!(r is SkinnedMeshRenderer s) || !s.sharedMesh) return r.bounds;
            var baked = new Mesh();
            try
            {
                s.BakeMesh(baked, false); // local space without scale; localToWorld adds it
                baked.RecalculateBounds();
                var lb = baked.bounds;
                var m = s.transform.localToWorldMatrix;
                var res = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
                for (var i = 0; i < 8; i++)
                    res.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                return res;
            }
            finally
            {
                Object.DestroyImmediate(baked);
            }
        }

        internal static JToken CaptureView(JObject a)
        {
            var w = Mathf.Clamp(Int(a, "width", 768), 64, 4096);
            var h = Mathf.Clamp(Int(a, "height", 768), 64, 4096);
            var camPath = Str(a, "camera");
            var framePath = Str(a, "frame");
            GameObject temp = null;
            Camera cam;
            string described;

            if (camPath != null)
            {
                cam = FindGameObject(camPath).GetComponent<Camera>() ?? throw new ArgumentException($"No Camera on {camPath}.");
                described = $"camera {camPath}";
            }
            else
            {
                temp = new GameObject("UnityVrcMcpCaptureCam") { hideFlags = HideFlags.HideAndDontSave };
                cam = temp.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.33f, 0.36f, 1f);
                cam.nearClipPlane = 0.005f;
                cam.farClipPlane = 1000f;
                cam.fieldOfView = Float(a, "fov", 30f);
                cam.enabled = false;

                if (framePath != null)
                {
                    var target = FindGameObject(framePath);
                    // Frame the main mesh (most vertices) plus meshes overlapping it, so props, particles
                    // and world-space toys far from the avatar don't blow up the framing.
                    var renderers = target.GetComponentsInChildren<Renderer>(false)
                        .Where(r => r.enabled && (r is SkinnedMeshRenderer || r is MeshRenderer)).ToList();
                    int Verts(Renderer r) => r is SkinnedMeshRenderer s ? (s.sharedMesh ? s.sharedMesh.vertexCount : 0)
                        : r.GetComponent<MeshFilter>() is MeshFilter mf && mf.sharedMesh ? mf.sharedMesh.vertexCount : 0;
                    Bounds b;
                    if (renderers.Count > 0)
                    {
                        var main = renderers.OrderByDescending(Verts).First();
                        b = TightBounds(main);
                        var zone = new Bounds(b.center, b.size * 1.5f);
                        foreach (var r in renderers)
                        {
                            if (r == main) continue;
                            var rb = TightBounds(r);
                            if (zone.Intersects(rb) && rb.size.magnitude < b.size.magnitude * 2f) b.Encapsulate(rb);
                        }
                    }
                    else
                    {
                        var size = Float(a, "size", 0.3f);
                        b = new Bounds(target.transform.position, Vector3.one * size);
                    }

                    var yaw = Float(a, "yaw", 0f);
                    var pitch = Float(a, "pitch", 0f);
                    var radius = Mathf.Max(b.extents.magnitude, 0.01f);
                    var dist = radius * Float(a, "padding", 1.05f) / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    // yaw 0 = looking at the avatar's front (+Z side of its root).
                    var dir = target.transform.root.rotation * (Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
                    cam.transform.position = b.center + dir * dist;
                    cam.transform.LookAt(b.center);
                    described = $"framed {GetPath(target.transform)} (yaw {yaw}, pitch {pitch})";
                }
                else
                {
                    var sv = SceneView.lastActiveSceneView ?? throw new ArgumentException("No Scene view open; pass 'frame' or 'camera'.");
                    cam.transform.SetPositionAndRotation(sv.camera.transform.position, sv.camera.transform.rotation);
                    cam.fieldOfView = sv.camera.fieldOfView;
                    cam.orthographic = sv.camera.orthographic;
                    cam.orthographicSize = sv.camera.orthographicSize;
                    described = "current Scene view angle";
                }
            }

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            Texture2D tex = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var dir = Path.Combine(UnityVrcMcpBridge.ProjectRoot, "Library", "UnityVrcMcp", "captures");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                return new JObject { ["file"] = file, ["width"] = w, ["height"] = h, ["view"] = described };
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Object.DestroyImmediate(rt);
                if (tex) Object.DestroyImmediate(tex);
                if (temp) Object.DestroyImmediate(temp);
            }
        }
    }
}
