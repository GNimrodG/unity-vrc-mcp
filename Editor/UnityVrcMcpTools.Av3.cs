// Av3Emulator (Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime) play-mode tools for the Unity VRC MCP bridge:
// live inputs/parameters/animator states, setting inputs, the expression menu map and a change recorder.
// Uses reflection so the bridge compiles without the emulator package.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        const string RuntimeTypeName = "Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime";

        static Type Av3RuntimeType =>
            FindType(RuntimeTypeName) ?? throw new InvalidOperationException("Av3Emulator (LyumaAv3Runtime) not found in this project.");

        static bool IsMainRuntime(Component r) =>
            !(Get(r, "IsMirrorClone") is bool m && m) && !(Get(r, "IsShadowClone") is bool s && s) &&
            !(Get(r, "AvatarSyncSource") is Object src && src && src != r);

        static Component FindRuntime(JObject a)
        {
            var type = Av3RuntimeType;
            if (!Application.isPlaying)
                throw new InvalidOperationException("Not in play mode. Use play_mode {state:'play'} first (the Av3Emulator runtime only exists while playing).");
            var path = Str(a, "path");
            if (path != null)
            {
                var go = FindGameObject(path);
                return go.GetComponent(type) ?? throw new ArgumentException($"No LyumaAv3Runtime on {path}.");
            }

            var all = Object.FindObjectsOfType(type).Cast<Component>().Where(IsMainRuntime).ToList();
            if (all.Count == 0) throw new InvalidOperationException("No LyumaAv3Runtime in the scene. Is there an Av3Emulator object (Tools > Avatars 3.0 Emulator > Enable) and an active avatar?");
            var local = all.Where(r => Get(r, "IsLocal") is bool l && l).ToList();
            if (local.Count == 1) return local[0];
            if (all.Count == 1) return all[0];
            throw new ArgumentException("Several avatars are emulated; pass 'path': " + string.Join(" | ", all.Select(r => GetPath(r.transform))));
        }

        // ---------- helpers ----------

        sealed class PlayableInfo
        {
            public int Index;
            public string Type;
            public AnimatorControllerPlayable Playable;
            public AnimatorController Controller;
            public List<Dictionary<int, string>> StateNames;
            public float Weight;
        }

        static readonly Dictionary<AnimatorController, List<Dictionary<int, string>>> StateMapCache = new Dictionary<AnimatorController, List<Dictionary<int, string>>>();

        static List<Dictionary<int, string>> StateHashMaps(AnimatorController ctrl)
        {
            if (StateMapCache.TryGetValue(ctrl, out var cached)) return cached;
            var res = new List<Dictionary<int, string>>();
            foreach (var layer in ctrl.layers)
            {
                var map = new Dictionary<int, string>();
                var states = new List<(string path, AnimatorState state)>();
                if (layer.stateMachine) Walk(layer.stateMachine, "", states, new List<(string, AnimatorTransitionBase)>());
                foreach (var s in states)
                {
                    map[Animator.StringToHash(layer.name + "." + s.path.Replace('/', '.'))] = s.path;
                    map[Animator.StringToHash(s.path.Replace('/', '.'))] = s.path;
                    map[s.state.nameHash] = s.path;
                }

                res.Add(map);
            }

            StateMapCache[ctrl] = res;
            return res;
        }

        static List<PlayableInfo> GetPlayables(Component rt)
        {
            var list = (Get(rt, "playables") as IList)?.Cast<AnimatorControllerPlayable>().ToList() ?? new List<AnimatorControllerPlayable>();
            var mixer = Get(rt, "playableMixer") is AnimationLayerMixerPlayable mx ? mx : default;
            var names = new Dictionary<int, string>();
            foreach (var (field, label) in new[]
                     {
                         ("fxIndex", "FX"), ("actionIndex", "Action"), ("gestureIndex", "Gesture"), ("additiveIndex", "Additive"),
                         ("sittingIndex", "Sitting"), ("tposeIndex", "TPose"), ("ikposeIndex", "IKPose"),
                         ("altFXIndex", "FX (dup)"), ("altActionIndex", "Action (dup)"), ("altGestureIndex", "Gesture (dup)"), ("altAdditiveIndex", "Additive (dup)"),
                     })
                if (Get(rt, field) is int i && i >= 0 && !names.ContainsKey(i))
                    names[i] = label;

            var controllers = new Dictionary<string, AnimatorController>(StringComparer.OrdinalIgnoreCase);
            if (Get(rt, "allControllers") is IDictionary dict)
                foreach (DictionaryEntry e in dict)
                    if (e.Value is AnimatorController ac)
                        controllers[e.Key.ToString()] = ac;

            var res = new List<PlayableInfo>();
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (!p.IsValid()) continue;
                var type = names.TryGetValue(i, out var n) ? n : "Base";
                controllers.TryGetValue(type.Replace(" (dup)", ""), out var ctrl);
                res.Add(new PlayableInfo
                {
                    Index = i, Type = type, Playable = p, Controller = ctrl,
                    StateNames = ctrl ? StateHashMaps(ctrl) : null,
                    Weight = mixer.IsValid() && i + 1 < mixer.GetInputCount() ? mixer.GetInputWeight(i + 1) : 1f,
                });
            }

            return res;
        }

        static string StateName(PlayableInfo pi, int layer, AnimatorStateInfo info)
        {
            if (pi.StateNames != null && layer < pi.StateNames.Count)
            {
                var m = pi.StateNames[layer];
                if (m.TryGetValue(info.fullPathHash, out var n) || m.TryGetValue(info.shortNameHash, out n))
                {
                    n = n.Replace("\r", "").Replace("\n", " | ");
                    return n.Length > 90 ? n.Substring(0, 87) + "..." : n;
                }
            }

            return $"#{info.fullPathHash}";
        }

        /// <summary>Per animator layer: (key, "State" or "State -> Next", weight).</summary>
        static IEnumerable<(string key, string state, float weight, float time)> LayerStates(PlayableInfo pi)
        {
            var p = pi.Playable;
            for (var l = 0; l < p.GetLayerCount(); l++)
            {
                var cur = p.GetCurrentAnimatorStateInfo(l);
                var s = StateName(pi, l, cur);
                if (p.IsInTransition(l)) s += " -> " + StateName(pi, l, p.GetNextAnimatorStateInfo(l));
                yield return ($"{pi.Type}/{p.GetLayerName(l)}", s, p.GetLayerWeight(l), cur.normalizedTime);
            }
        }

        static IEnumerable<(string name, object value, string kind, bool synced)> RuntimeParams(Component rt)
        {
            foreach (var (list, kind) in new[] { ("Bools", "Bool"), ("Ints", "Int"), ("Floats", "Float") })
                if (Get(rt, list) is IEnumerable items)
                    foreach (var p in items)
                        yield return ((string)Get(p, "name"), Get(p, "value"), kind, Get(p, "synced") is bool s && s);
        }

        static IEnumerable<(string name, object def)> Builtins()
        {
            if (Av3RuntimeType.GetField("BUILTIN_PARAMETERS")?.GetValue(null) is IEnumerable set)
                foreach (var d in set)
                    yield return ((string)Get(d, "name"), d);
        }

        static JToken J(object v) => v is float f ? R(f) : v is double d ? R(d) : v == null ? null : v is bool b ? b : v is int i ? i : (JToken)v.ToString();

        // ---------- av3_state ----------

        static JToken Av3State(JObject a)
        {
            var rt = FindRuntime(a);
            var paramFilter = Str(a, "params");
            var layerFilter = Str(a, "layer");
            var res = new JObject { ["avatar"] = GetPath(rt.transform) };

            var inputs = new JObject();
            foreach (var (name, def) in Builtins())
            {
                try { inputs[name] = J(((Delegate)Get(def, "valueGetter")).DynamicInvoke(rt)); }
                catch { /* some getters need state that isn't ready */ }
            }

            if (inputs["GestureLeft"] != null) inputs["GestureLeft"] = $"{inputs["GestureLeft"]} ({Get(rt, "GestureLeft")})";
            if (inputs["GestureRight"] != null) inputs["GestureRight"] = $"{inputs["GestureRight"]} ({Get(rt, "GestureRight")})";
            res["builtins"] = inputs;

            if (Bool(a, "includeParams", true))
            {
                var ps = new JObject();
                foreach (var (name, value, _, _) in RuntimeParams(rt))
                {
                    if (string.IsNullOrEmpty(name) || inputs[name] != null) continue;
                    if (paramFilter != null && name.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    ps[name] = J(value);
                }

                res["parameters"] = ps;
            }

            if (Bool(a, "includeLayers", true))
            {
                var layers = new JArray();
                foreach (var pi in GetPlayables(rt))
                {
                    if (layerFilter != null && pi.Type.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        !Enumerable.Range(0, pi.Playable.GetLayerCount()).Any(l => pi.Playable.GetLayerName(l).IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    var lo = new JArray();
                    foreach (var (key, state, weight, time) in LayerStates(pi))
                    {
                        var layerName = key.Substring(pi.Type.Length + 1);
                        if (layerFilter != null && pi.Type.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            layerName.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        lo.Add($"{layerName} (w={R(weight)}): {state} t={R(time)}");
                    }

                    layers.Add(new JObject { ["playable"] = pi.Type, ["weight"] = R(pi.Weight), ["controller"] = ObjRef(pi.Controller), ["layers"] = lo });
                }

                res["playableLayers"] = layers;
            }

            var flags = new JObject();
            foreach (var f in new[] { "IsMirrorClone", "LocomotionIsDisabled", "EnableAvatarOSC", "CreateNonLocalClone", "ViewMirrorReflection" })
                if (Get(rt, f) is bool b && b) flags[f] = true;
            if (flags.Count > 0) res["runtimeFlags"] = flags;
            if (Get(rt, "IKTrackingOutputData") is object ik)
            {
                var tracking = new JObject();
                foreach (var f in new[] { "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot", "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers", "trackingEyesAndEyelids", "trackingMouthAndJaw" })
                    tracking[f.Substring(8)] = Get(ik, f)?.ToString();
                res["trackingControl"] = tracking;
            }

            return res;
        }

        // ---------- av3_set ----------

        static object Box(JToken v, string kind, Type enumType = null)
        {
            switch (kind)
            {
                case "Bool":
                    return v.Type == JTokenType.Boolean ? v.Value<bool>() : v.Value<float>() != 0f;
                case "Int":
                    if (v.Type == JTokenType.String && enumType != null && enumType.IsEnum)
                        return Convert.ToInt32(Enum.Parse(enumType, v.ToString(), true));
                    return v.Type == JTokenType.Boolean ? (v.Value<bool>() ? 1 : 0) : (int)Math.Round(v.Value<double>());
                default:
                    return v.Type == JTokenType.Boolean ? (v.Value<bool>() ? 1f : 0f) : v.Value<float>();
            }
        }

        internal static JArray Av3Apply(Component rt, JObject values)
        {
            var applied = new JArray();
            var builtins = Builtins().ToDictionary(b => b.name, b => b.def, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                var name = kv.Key;
                var v = kv.Value;
                if (builtins.TryGetValue(name, out var def))
                {
                    var kind = Get(def, "type")?.ToString();
                    var enumType = rt.GetType().GetField(name)?.FieldType;
                    var boxed = Box(v, kind, enumType);
                    if (name == "VRMode") boxed = Convert.ToInt32(boxed);
                    ((Delegate)Get(def, "valueSetter")).DynamicInvoke(rt, boxed);
                    applied.Add($"{name} = {boxed} (built-in)");
                    continue;
                }

                var found = false;
                foreach (var (list, kind) in new[] { ("Bools", "Bool"), ("Ints", "Int"), ("Floats", "Float") })
                {
                    if (!(Get(rt, list) is IList items)) continue;
                    foreach (var p in items)
                    {
                        if (!string.Equals((string)Get(p, "name"), name, StringComparison.Ordinal)) continue;
                        var boxed = Box(v, kind);
                        if (kind == "Float") p.GetType().GetProperty("exportedValue")?.SetValue(p, boxed); // value + expressionValue
                        else p.GetType().GetField("value").SetValue(p, boxed);
                        applied.Add($"{name} = {boxed} ({kind})");
                        found = true;
                        break;
                    }

                    if (found) break;
                }

                if (!found)
                {
                    var close = RuntimeParams(rt).Select(p => p.name).Where(n => n != null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(10);
                    throw new ArgumentException($"Parameter '{name}' is not a built-in or an animator/expression parameter of this avatar. Similar: {string.Join(", ", close)}");
                }
            }

            return applied;
        }

        static JToken Av3Set(JObject a)
        {
            var rt = FindRuntime(a);
            var res = new JObject { ["avatar"] = GetPath(rt.transform) };
            if (Arg(a, "params") is JObject values) res["applied"] = Av3Apply(rt, values);
            if (Arg(a, "fields") is JObject fields)
            {
                var set = new JArray();
                foreach (var kv in fields)
                {
                    var f = rt.GetType().GetField(kv.Key) ?? throw new ArgumentException($"LyumaAv3Runtime has no public field '{kv.Key}'.");
                    var val = f.FieldType.IsEnum ? Enum.Parse(f.FieldType, kv.Value.ToString(), true) : kv.Value.ToObject(f.FieldType);
                    f.SetValue(rt, val);
                    set.Add($"{kv.Key} = {val}");
                }

                res["fields"] = set;
            }

            return res;
        }

        // ---------- av3_menu ----------

        static JToken Av3Menu(JObject a)
        {
            Component rt = null;
            object desc;
            if (Application.isPlaying)
            {
                rt = FindRuntime(a);
                desc = Get(rt, "avadesc") ?? rt.GetComponent(FindType(DescriptorType));
            }
            else
            {
                var dt = FindType(DescriptorType) ?? throw new InvalidOperationException("VRChat SDK not found.");
                var path = Str(a, "path");
                desc = path != null ? FindGameObject(path).GetComponent(dt) : AllRoots().SelectMany(r => r.GetComponentsInChildren(dt, true)).FirstOrDefault();
                if (desc == null) throw new ArgumentException("No VRCAvatarDescriptor found.");
            }

            var values = rt != null ? RuntimeParams(rt).Where(p => p.name != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().value) : null;
            var root = Get(desc, "expressionsMenu") as Object;
            if (!root) return "The avatar has no expressions menu (VRCFury may build one at play/upload time; in play mode the runtime's menu is used).";
            var visited = new HashSet<Object>();
            return new JObject
            {
                ["menu"] = ObjRef(root),
                ["controls"] = MenuControls(root, values, visited, 0, Int(a, "maxDepth", 6)),
                ["note"] = rt == null ? "Edit mode: values not shown; enter play mode for live values." : null,
            };
        }

        static JArray MenuControls(Object menu, Dictionary<string, object> values, HashSet<Object> visited, int depth, int maxDepth)
        {
            var res = new JArray();
            if (!visited.Add(menu)) return new JArray("(menu already listed)");
            if (!(Get(menu, "controls") is IEnumerable controls)) return res;
            foreach (var c in controls)
            {
                var type = Get(c, "type")?.ToString();
                var param = Get(Get(c, "parameter"), "name") as string;
                var o = new JObject { ["name"] = Get(c, "name") as string, ["type"] = type };
                if (!string.IsNullOrEmpty(param))
                {
                    o["parameter"] = param;
                    if (type == "Toggle" || type == "Button" || type == "SubMenu") o["value"] = J(Get(c, "value"));
                    if (values != null) o["current"] = values.TryGetValue(param, out var cur) ? J(cur) : "MISSING from runtime parameters";
                }

                if (Get(c, "subParameters") is IEnumerable subs)
                {
                    var sp = new JArray();
                    foreach (var s in subs)
                    {
                        var n = Get(s, "name") as string;
                        if (string.IsNullOrEmpty(n)) continue;
                        sp.Add(values != null ? $"{n} = {(values.TryGetValue(n, out var cur) ? J(cur) : "MISSING")}" : n);
                    }

                    if (sp.Count > 0) o["subParameters"] = sp;
                }

                if (type == "SubMenu" && Get(c, "subMenu") is Object sub && sub)
                    o["subMenu"] = depth < maxDepth ? (JToken)MenuControls(sub, values, visited, depth + 1, maxDepth) : ObjRef(sub);
                else if (type == "SubMenu") o["subMenu"] = "<none>";
                res.Add(o);
            }

            return res;
        }

        // ---------- av3_record ----------

        static Component _recRuntime;
        static Dictionary<string, string> _recLast;
        static List<string> _recEvents;
        static double _recStart;
        static int _recStartFrame;
        static List<System.Text.RegularExpressions.Regex> _recIgnore, _recOnly;

        static readonly string[] DefaultRecordIgnore =
        {
            "Voice", "AngularY", "Velocity*", "Upright", "EyeHeight*", "Scale*",
            "*_SyncData*", "*_SyncIndex*", "*Parameter Compressor*", "*FrameTime Counter*",
        };

        static List<System.Text.RegularExpressions.Regex> Globs(IEnumerable<string> patterns) =>
            patterns?.Select(p => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))).ToList();

        static bool Skip(string name) =>
            _recIgnore.Any(r => r.IsMatch(name)) || (_recOnly != null && !_recOnly.Any(r => r.IsMatch(name)));
        static int _recSamples;

        static Dictionary<string, string> Snapshot(Component rt)
        {
            var d = new Dictionary<string, string>();
            foreach (var (name, def) in Builtins())
            {
                if (Skip(name)) continue;
                try
                {
                    var v = ((Delegate)Get(def, "valueGetter")).DynamicInvoke(rt);
                    d["builtin:" + name] = v is float f ? Math.Round(f, 2).ToString() : v?.ToString();
                }
                catch { /* skip */ }
            }

            foreach (var (name, value, _, _) in RuntimeParams(rt))
            {
                if (name == null || Skip(name) || d.ContainsKey("builtin:" + name)) continue;
                d["param:" + name] = value is float f ? Math.Round(f, 2).ToString() : value?.ToString();
            }

            foreach (var pi in GetPlayables(rt))
            {
                d["weight:" + pi.Type] = Math.Round(pi.Weight, 2).ToString();
                foreach (var (key, state, weight, _) in LayerStates(pi))
                {
                    if (Skip(key)) continue;
                    d["state:" + key] = state;
                    d["layerWeight:" + key] = Math.Round(weight, 2).ToString();
                }
            }

            return d;
        }

        static void RecordTick()
        {
            if (_recRuntime == null || !Application.isPlaying)
            {
                StopRecording();
                return;
            }

            Dictionary<string, string> snap;
            try { snap = Snapshot(_recRuntime); }
            catch { return; }

            _recSamples++;
            var t = EditorApplication.timeSinceStartup - _recStart;
            foreach (var kv in snap)
            {
                if (_recLast.TryGetValue(kv.Key, out var old) && old == kv.Value) continue;
                if (_recEvents.Count < 3000)
                    _recEvents.Add($"t={t:0.000}s f+{Time.frameCount - _recStartFrame} {kv.Key}: {(old ?? "(new)")} => {kv.Value}");
            }

            _recLast = snap;
        }

        static void StopRecording()
        {
            EditorApplication.update -= RecordTick;
            _recRuntime = null;
        }

        static JToken Av3Record(JObject a)
        {
            var action = (Str(a, "action") ?? "status").ToLowerInvariant();
            switch (action)
            {
                case "start":
                {
                    var rt = FindRuntime(a);
                    StopRecording();
                    _recIgnore = Globs((Arg(a, "ignore") as JArray)?.Select(x => x.ToString()) ?? DefaultRecordIgnore);
                    _recOnly = Globs((Arg(a, "only") as JArray)?.Select(x => x.ToString()));
                    _recRuntime = rt;
                    _recEvents = new List<string>();
                    _recStart = EditorApplication.timeSinceStartup;
                    _recStartFrame = Time.frameCount;
                    _recSamples = 0;
                    _recLast = Snapshot(rt);
                    EditorApplication.update += RecordTick;
                    if (Arg(a, "set") is JObject values)
                        _recEvents.Add("t=0.000s applied: " + string.Join(", ", Av3Apply(rt, values).Select(x => x.ToString())));
                    return new JObject { ["recording"] = GetPath(rt.transform), ["tracked"] = _recLast.Count };
                }
                case "stop":
                case "status":
                {
                    var res = new JObject
                    {
                        ["recording"] = _recRuntime != null,
                        ["seconds"] = _recEvents != null ? R(EditorApplication.timeSinceStartup - _recStart) : 0,
                        ["frames"] = _recEvents != null ? Time.frameCount - _recStartFrame : 0,
                        ["samples"] = _recSamples,
                        ["events"] = _recEvents != null ? new JArray(_recEvents) : new JArray(),
                    };
                    if (action == "stop") StopRecording();
                    return res;
                }
                default:
                    throw new ArgumentException("action must be start, stop or status.");
            }
        }
    }
}
