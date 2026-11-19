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
            var names = (Arg(a, "names") as JArray)?.Select(x => x.ToString()).ToList();
            var res = new JObject { ["avatar"] = GetPath(rt.transform) };

            var inputs = new JObject();
            foreach (var (name, def) in Builtins())
            {
                if (names != null && !names.Contains(name)) continue;
                if (names == null && paramFilter != null && name.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { inputs[name] = J(((Delegate)Get(def, "valueGetter")).DynamicInvoke(rt)); }
                catch { /* some getters need state that isn't ready */ }
            }

            if (inputs.Count > 0 && inputs["GestureLeft"] != null) inputs["GestureLeft"] = $"{inputs["GestureLeft"]} ({Get(rt, "GestureLeft")})";
            if (inputs["GestureRight"] != null) inputs["GestureRight"] = $"{inputs["GestureRight"]} ({Get(rt, "GestureRight")})";
            res["builtins"] = inputs;

            if (Bool(a, "includeParams", true))
            {
                bool Wanted(string name) =>
                    !string.IsNullOrEmpty(name) && inputs[name] == null && (names == null || names.Contains(name)) &&
                    (paramFilter == null || name.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                var emulator = RuntimeParams(rt).Where(p => p.name != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().value);
                var live = LiveParams(rt);
                var useLive = live.Count > 0 && !string.Equals(Str(a, "source"), "emulator", StringComparison.OrdinalIgnoreCase);
                var ps = new JObject();
                if (useLive)
                {
                    var curve = new JObject();
                    var disagree = new JObject();
                    var stale = new JObject();
                    foreach (var lp in live.Values.Where(p => Wanted(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        ps[lp.Name] = J(lp.Value);
                        if (lp.CurveIn.Count > 0) curve[lp.Name] = string.Join(", ", lp.CurveIn);
                        if (Disagree(lp)) disagree[lp.Name] = new JObject(lp.BySource.Select(kv => new JProperty(kv.Key, J(kv.Value))));
                        if (emulator.TryGetValue(lp.Name, out var ev) && Math.Abs(Num(ev) - Num(lp.Value)) > 1e-3 && stale.Count < 30)
                            stale[lp.Name] = $"emulator {J(ev)}, animator {J(lp.Value)}";
                    }

                    foreach (var kv in emulator.Where(kv => Wanted(kv.Key) && !live.ContainsKey(kv.Key)))
                        ps[kv.Key] = J(kv.Value); // expression parameters no controller uses
                    res["source"] = "live animator playables (curve-driven values first, then FX); source: \"emulator\" for the emulator's own lists";
                    if (curve.Count > 0) res["curveDriven"] = curve;
                    if (disagree.Count > 0) res["playablesDisagree"] = disagree;
                    if (stale.Count > 0) res["emulatorListDiffers"] = stale;
                }
                else
                {
                    foreach (var kv in emulator.Where(kv => Wanted(kv.Key))) ps[kv.Key] = J(kv.Value);
                    res["source"] = live.Count == 0 ? "emulator lists (the playables don't exist yet)" : "emulator lists";
                }

                res["parameters"] = ps;
            }

            if (HoldsJson() is JObject held) res["held"] = held;

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

            if (names == null)
            {
                var outputs = BuildOutputs(GetPlayables(rt).Select(pi => (pi.Type, (Object)pi.Controller)));
                if (outputs.Count > 0) res["controllersBuiltBy"] = outputs;
                if (BuildNote(outputs) is string note) res["buildNote"] = note;
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

        /// <summary>
        /// Sets built-ins and parameters (plus VRCFury VFnn_ twins unless <paramref name="twins"/> is false), writing the
        /// playables directly. <paramref name="expected"/> collects name -> value for a later read-back.
        /// </summary>
        internal static JArray Av3Apply(Component rt, JObject values, JObject expected = null, bool twins = true, string holdOwner = null)
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

                var kindSet = WriteRuntimeParam(rt, name, v);
                if (kindSet == null && twins && Twins(rt, name).ToList() is var only && only.Count > 0)
                {
                    // VRCFury renamed it everywhere: set the VFnn_ copies
                    foreach (var t in only)
                    {
                        var tk = WriteRuntimeParam(rt, t, v);
                        if (expected != null && tk != null) expected[t] = Num(Box(v, tk));
                        if (holdOwner != null) HoldParam(rt, t, v, holdOwner);
                    }

                    applied.Add($"{name}: not on the runtime under that name; set VRCFury's {string.Join(", ", only)}");
                    continue;
                }

                if (kindSet == null)
                {
                    var close = RuntimeParams(rt).Select(p => p.name).Where(n => n != null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(10);
                    throw new ArgumentException($"Parameter '{name}' is not a built-in or an animator/expression parameter of this avatar. Similar: {string.Join(", ", close)}");
                }

                var line = $"{name} = {Box(v, kindSet)} ({kindSet})";
                if (expected != null) expected[name] = Num(Box(v, kindSet));
                if (holdOwner != null) HoldParam(rt, name, v, holdOwner);
                if (twins)
                {
                    var tw = Twins(rt, name).ToList();
                    foreach (var t in tw)
                    {
                        var tk = WriteRuntimeParam(rt, t, v);
                        if (expected != null && tk != null) expected[t] = Num(Box(v, tk));
                        if (holdOwner != null) HoldParam(rt, t, v, holdOwner);
                    }

                    if (tw.Count > 0) line += $", also VRCFury twin{(tw.Count > 1 ? "s" : "")} {string.Join(", ", tw)}";
                }

                applied.Add(line);
            }

            return applied;
        }

        static JToken Av3Set(JObject a)
        {
            var rt = FindRuntime(a);
            var res = new JObject { ["avatar"] = GetPath(rt.transform) };
            var release = Arg(a, "release");
            if (release != null)
            {
                var all = release.Type == JTokenType.Boolean ? release.Value<bool>() : release.ToString() == "all";
                var list = release is JArray arr ? arr.Select(x => x.ToString()).ToList() : new List<string> { release.ToString() };
                var before = Holds.Count;
                ReleaseHolds(h => h.Owner == "av3_set" && (all || list.Contains(h.Name) || list.Any(n => VrcFuryTwin.Match(h.Name) is var m && m.Success && m.Groups[1].Value == n)));
                res["released"] = before - Holds.Count;
            }

            if (GetPlayables(rt).Count == 0)
                res["warning"] = "The emulator hasn't built its animator playables yet (just entered play mode?). The values are stored and pushed once they exist.";
            if (Arg(a, "params") is JObject values)
            {
                var expected = new JObject();
                res["applied"] = Av3Apply(rt, values, expected, Bool(a, "twins", true), Bool(a, "hold", false) ? "av3_set" : null);
                if (expected.Count > 0) res["expected"] = expected; // the MCP server reads these back a few frames later
            }

            if (HoldsJson() is JObject held) res["held"] = held;
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

            Dictionary<string, object> values = null;
            if (rt != null)
            {
                values = RuntimeParams(rt).Where(p => p.name != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().value);
                foreach (var lp in LiveParams(rt).Values) values[lp.Name] = lp.Value; // what the animators see
            }

            var root = Get(desc, "expressionsMenu") as Object;
            if (!root) return "The avatar has no expressions menu (VRCFury may build one at play/upload time; in play mode the runtime's menu is used).";

            // menuPath: walk down to a submenu by control names ("Avatar/Face Tracking")
            var menuPath = Str(a, "menuPath");
            var start = root;
            var walked = new List<string>();
            if (menuPath != null)
                foreach (var part in menuPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var controls = (Get(start, "controls") as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
                    var hit = controls.FirstOrDefault(c => string.Equals(Get(c, "type")?.ToString(), "SubMenu") && string.Equals(((string)Get(c, "name"))?.Trim(), part.Trim(), StringComparison.OrdinalIgnoreCase))
                              ?? controls.FirstOrDefault(c => string.Equals(Get(c, "type")?.ToString(), "SubMenu") && ((string)Get(c, "name") ?? "").IndexOf(part.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!(Get(hit, "subMenu") is Object sub) || !sub)
                        throw new ArgumentException($"No submenu '{part}' under '{(walked.Count == 0 ? "(root)" : string.Join("/", walked))}'. Submenus there: " +
                                                    string.Join(", ", controls.Where(c => Get(c, "type")?.ToString() == "SubMenu").Select(c => (string)Get(c, "name"))));
                    walked.Add((string)Get(hit, "name"));
                    start = sub;
                }

            // find: flat list of controls whose name or parameter matches the glob
            var find = Str(a, "find");
            if (find != null)
            {
                var globs = Globs(new[] { find.Contains("*") ? find : "*" + find + "*" });
                var hits = new JArray();
                FindControls(start, string.Join("/", walked), globs, values, new HashSet<Object>(), hits);
                return new JObject { ["menu"] = ObjRef(start), ["find"] = find, ["matches"] = hits };
            }

            return new JObject
            {
                ["menu"] = ObjRef(start),
                ["menuPath"] = walked.Count > 0 ? string.Join("/", walked) : null,
                ["controls"] = MenuControls(start, values, new HashSet<Object>(), 0, Int(a, "maxDepth", menuPath != null ? 3 : 2)),
                ["note"] = rt == null ? "Edit mode: values not shown; enter play mode for live values." : null,
            };
        }

        static void FindControls(Object menu, string prefix, List<System.Text.RegularExpressions.Regex> globs, Dictionary<string, object> values, HashSet<Object> visited, JArray hits)
        {
            if (!visited.Add(menu) || !(Get(menu, "controls") is IEnumerable controls)) return;
            foreach (var c in controls)
            {
                var name = (string)Get(c, "name") ?? "";
                var type = Get(c, "type")?.ToString();
                var param = Get(Get(c, "parameter"), "name") as string;
                var subs = (Get(c, "subParameters") as IEnumerable)?.Cast<object>().Select(s => Get(s, "name") as string).Where(n => !string.IsNullOrEmpty(n)).ToList() ?? new List<string>();
                var path = prefix.Length > 0 ? prefix + "/" + name : name;
                if (globs.Any(g => g.IsMatch(name) || (param != null && g.IsMatch(param)) || subs.Any(g.IsMatch)) && hits.Count < 100)
                {
                    var line = $"{path} ({type})";
                    foreach (var p in new[] { param }.Concat(subs).Where(p => !string.IsNullOrEmpty(p)))
                        line += values != null ? $" {p}={(values.TryGetValue(p, out var cur) ? J(cur) : "MISSING")}" : $" {p}";
                    if (type == "Toggle" || type == "Button") line += $" (sets {J(Get(c, "value"))})";
                    hits.Add(line);
                }

                if (type == "SubMenu" && Get(c, "subMenu") is Object sub && sub) FindControls(sub, path, globs, values, visited, hits);
            }
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
        static JObject _recSpec;

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

            var live = LiveParams(rt);
            foreach (var (name, value, _, _) in RuntimeParams(rt))
            {
                if (name == null || Skip(name) || d.ContainsKey("builtin:" + name)) continue;
                var v = live.TryGetValue(name, out var lp) ? lp.Value : value;
                d["param:" + name] = v is float f ? Math.Round(f, 2).ToString() : v?.ToString();
            }

            foreach (var lp in live.Values) // animator-only parameters (AAPs, VRCFury internals)
            {
                if (Skip(lp.Name) || d.ContainsKey("param:" + lp.Name) || d.ContainsKey("builtin:" + lp.Name)) continue;
                d["param:" + lp.Name] = lp.Value is float f ? Math.Round(f, 2).ToString() : lp.Value?.ToString();
            }

            if (_recSpec != null)
                foreach (var kv in Capture(rt.gameObject, _recSpec).Values)
                    d[kv.Key] = kv.Key.StartsWith("bone:", StringComparison.Ordinal) ? Show(kv.Key, kv.Value).ToString() : Math.Round(kv.Value[0], 1).ToString();

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
                    _recSpec = null;
                    if (Arg(a, "blendshapes") != null || Arg(a, "bones") != null)
                        _recSpec = new JObject { ["blendshapes"] = Arg(a, "blendshapes"), ["bones"] = Arg(a, "bones") };
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
