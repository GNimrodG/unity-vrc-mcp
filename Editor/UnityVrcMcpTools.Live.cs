// Play-mode helpers that read and write the Av3Emulator's animator playables directly: live parameter values
// (the emulator's own lists are stale for curve-driven parameters), reliable writes, VRCFury parameter twins,
// held parameters, renderer/transform snapshots with diffs, build-output detection, EditorPrefs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        // ---------- live animator parameters ----------

        internal sealed class LiveParam
        {
            public string Name;
            public AnimatorControllerParameterType Type;
            public object Value;
            public string Source;
            public readonly List<string> CurveIn = new List<string>();
            public readonly Dictionary<string, object> BySource = new Dictionary<string, object>();
        }

        static object ReadParam(UnityEngine.Animations.AnimatorControllerPlayable p, AnimatorControllerParameter prm)
        {
            switch (prm.type)
            {
                case AnimatorControllerParameterType.Float: return p.GetFloat(prm.nameHash);
                case AnimatorControllerParameterType.Int: return p.GetInteger(prm.nameHash);
                default: return p.GetBool(prm.nameHash);
            }
        }

        static double Num(object v) => v is bool b ? (b ? 1 : 0) : v is int i ? i : v is float f ? f : v is double d ? d : 0;

        /// <summary>Values as the animators see them, from every valid playable. Curve-driven values win, then FX.</summary>
        internal static Dictionary<string, LiveParam> LiveParams(Component rt)
        {
            var res = new Dictionary<string, LiveParam>(StringComparer.Ordinal);
            foreach (var pi in GetPlayables(rt))
            {
                var p = pi.Playable;
                for (var j = 0; j < p.GetParameterCount(); j++)
                {
                    var prm = p.GetParameter(j);
                    var v = ReadParam(p, prm);
                    var curve = p.IsParameterControlledByCurve(prm.nameHash);
                    if (!res.TryGetValue(prm.name, out var lp)) res[prm.name] = lp = new LiveParam { Name = prm.name, Type = prm.type };
                    lp.BySource[pi.Type] = v;
                    if (curve) lp.CurveIn.Add(pi.Type);
                    var better = lp.Source == null || (curve && !lp.CurveIn.Contains(lp.Source)) || (!lp.CurveIn.Contains(lp.Source) && pi.Type == "FX");
                    if (!better) continue;
                    lp.Value = v;
                    lp.Source = pi.Type;
                }
            }

            return res;
        }

        static bool Disagree(LiveParam lp) => lp.BySource.Values.Any(x => Math.Abs(Num(x) - Num(lp.Value)) > 1e-4);

        /// <summary>Writes a value into every playable that has the parameter and doesn't animate it with a curve.</summary>
        static void WritePlayables(Component rt, string name, double value)
        {
            var hash = Animator.StringToHash(name);
            foreach (var pi in GetPlayables(rt))
            {
                var p = pi.Playable;
                for (var j = 0; j < p.GetParameterCount(); j++)
                {
                    var prm = p.GetParameter(j);
                    if (prm.nameHash != hash || p.IsParameterControlledByCurve(hash)) continue;
                    switch (prm.type)
                    {
                        case AnimatorControllerParameterType.Float: p.SetFloat(hash, (float)value); break;
                        case AnimatorControllerParameterType.Int: p.SetInteger(hash, (int)Math.Round(value)); break;
                        case AnimatorControllerParameterType.Bool: p.SetBool(hash, value != 0); break;
                    }
                }
            }
        }

        static readonly Regex VrcFuryTwin = new Regex(@"^VF\d+_(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        /// <summary>VRCFury's renamed copies of a parameter (VF&lt;n&gt;_&lt;name&gt;) that exist on this runtime.</summary>
        static IEnumerable<string> Twins(Component rt, string name) =>
            RuntimeParams(rt).Select(p => p.name).Where(n => n != null && n != name)
                .Where(n => VrcFuryTwin.Match(n) is Match m && m.Success && m.Groups[1].Value == name).Distinct();

        /// <summary>
        /// Sets one emulator parameter: its list entry (lastValue cleared so the emulator pushes it again) and the
        /// playables directly. Returns the parameter kind, or null when the runtime has no such parameter.
        /// </summary>
        static string WriteRuntimeParam(Component rt, string name, JToken v)
        {
            foreach (var (list, kind) in new[] { ("Bools", "Bool"), ("Ints", "Int"), ("Floats", "Float") })
            {
                if (!(Get(rt, list) is IList items)) continue;
                foreach (var p in items)
                {
                    if (!string.Equals((string)Get(p, "name"), name, StringComparison.Ordinal)) continue;
                    var boxed = Box(v, kind);
                    if (kind == "Float" && p.GetType().GetProperty("exportedValue") is var ev && ev != null) ev.SetValue(p, boxed); // value + expressionValue
                    else p.GetType().GetField("value").SetValue(p, boxed);
                    // The emulator only pushes a value when it differs from lastValue; a write made before the playables
                    // existed would otherwise never reach them.
                    var last = p.GetType().GetField("lastValue");
                    if (last != null && (!last.FieldType.IsValueType || Nullable.GetUnderlyingType(last.FieldType) != null)) last.SetValue(p, null);
                    WritePlayables(rt, name, Num(boxed));
                    return kind;
                }
            }

            return null;
        }

        // ---------- held parameters ----------

        sealed class Hold
        {
            public Component Runtime;
            public string Name;
            public JToken Value;
            public string Owner; // "av3_set" or "ft_set"
            public int Restores;
        }

        static readonly Dictionary<string, Hold> Holds = new Dictionary<string, Hold>(StringComparer.Ordinal);
        static bool _holdHooked;

        static void HoldParam(Component rt, string name, JToken value, string owner)
        {
            Holds[name] = new Hold { Runtime = rt, Name = name, Value = value, Owner = owner };
            if (_holdHooked) return;
            McpPlayerLoop.Ensure();
            McpPlayerLoop.BeforeAnimation += ApplyHolds;
            McpPlayerLoop.Reset += () => Holds.Clear();
            _holdHooked = true;
        }

        static void ReleaseHolds(Func<Hold, bool> which)
        {
            foreach (var k in Holds.Where(kv => which(kv.Value)).Select(kv => kv.Key).ToList()) Holds.Remove(k);
        }

        static void ApplyHolds()
        {
            if (Holds.Count == 0) return;
            foreach (var h in Holds.Values.ToList())
            {
                if (!h.Runtime)
                {
                    Holds.Remove(h.Name);
                    continue;
                }

                // count frames where something (a VRCFury compressor, a driver, a curve) changed it since the last frame
                var live = LiveParams(h.Runtime);
                if (live.TryGetValue(h.Name, out var lp) && Math.Abs(Num(lp.Value) - Num(Box(h.Value, lp.Type == AnimatorControllerParameterType.Float ? "Float" : lp.Type == AnimatorControllerParameterType.Int ? "Int" : "Bool"))) > 1e-4)
                    h.Restores++;
                WriteRuntimeParam(h.Runtime, h.Name, h.Value);
            }
        }

        static JObject HoldsJson() =>
            Holds.Count == 0 ? null : new JObject(Holds.Values.Select(h => new JProperty(h.Name, $"{h.Value} ({h.Owner}{(h.Restores > 0 ? $", restored {h.Restores}x after being changed" : "")})")));

        // ---------- build output ----------

        /// <summary>Who made each playable layer's controller: VRCFury, d4rkAvatarOptimizer, another package, or the source asset.</summary>
        internal static JObject BuildOutputs(IEnumerable<(string layer, Object controller)> controllers)
        {
            var o = new JObject();
            foreach (var (layer, ctrl) in controllers)
            {
                if (!ctrl || o[layer] != null) continue;
                var p = AssetDatabase.GetAssetPath(ctrl);
                string who;
                if (string.IsNullOrEmpty(p)) who = "generated in memory";
                else if (p.IndexOf("d4rkpl4y3r", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("/TrashBin/", StringComparison.OrdinalIgnoreCase) >= 0) who = "d4rkAvatarOptimizer";
                else if (p.IndexOf("com.vrcfury.temp", StringComparison.OrdinalIgnoreCase) >= 0 || p.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0 && p.IndexOf("temp", StringComparison.OrdinalIgnoreCase) >= 0) who = "VRCFury";
                else if (p.StartsWith("Packages/", StringComparison.Ordinal)) who = "package";
                else who = "source";
                o[layer] = $"{who}: {p}";
            }

            return o;
        }

        static string BuildNote(JObject outputs)
        {
            var all = string.Join(" ", outputs.Properties().Select(p => p.Value.ToString()));
            var notes = new List<string>();
            if (all.Contains("VRCFury")) notes.Add("VRCFury built these controllers: layers get [VFnn] prefixes, parameters VFnn_ names, blendshapes may be renamed (VF_*)");
            if (all.Contains("d4rkAvatarOptimizer")) notes.Add("d4rkAvatarOptimizer ran in play mode: layers, parameters and meshes may be merged or renamed");
            return notes.Count > 0 ? string.Join("; ", notes) + "." : null;
        }

        // ---------- snapshot ----------

        sealed class Snap
        {
            public string Root;
            public double Time;
            public readonly Dictionary<string, double[]> Values = new Dictionary<string, double[]>(StringComparer.Ordinal);
        }

        static readonly Dictionary<string, Snap> Snaps = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);

        static List<Regex> GlobArg(JObject a, string key, out bool all)
        {
            var t = Arg(a, key);
            all = t != null && t.Type == JTokenType.Boolean && t.Value<bool>();
            if (t == null || t.Type == JTokenType.Boolean) return null;
            return Globs(t is JArray arr ? arr.Select(x => x.ToString()) : new[] { t.ToString() });
        }

        static GameObject SnapshotRoot(JObject a)
        {
            var path = Str(a, "path");
            if (path != null) return FindGameObject(path);
            if (Application.isPlaying && FindType(RuntimeTypeName) != null)
                return FindRuntime(a).gameObject;
            var dt = FindType(DescriptorType);
            var avatars = dt == null ? new List<Component>() : AllRoots().SelectMany(r => r.GetComponentsInChildren(dt, false)).ToList();
            return avatars.Count == 1 ? avatars[0].gameObject : throw new ArgumentException("Pass 'path' (the avatar or object to snapshot).");
        }

        /// <summary>Blendshape weights (shape:Renderer/Shape), local transforms (bone:path), live parameters (param:Name).</summary>
        static Snap Capture(GameObject root, JObject a)
        {
            var snap = new Snap { Root = GetPath(root.transform), Time = EditorApplication.timeSinceStartup };
            var shapeGlobs = GlobArg(a, "blendshapes", out var allShapes);
            if (allShapes || shapeGlobs != null)
            {
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var mesh = smr.sharedMesh;
                    if (!mesh) continue;
                    var rel = smr.transform == root.transform ? smr.name : AnimationUtility.CalculateTransformPath(smr.transform, root.transform);
                    for (var i = 0; i < mesh.blendShapeCount; i++)
                    {
                        var n = mesh.GetBlendShapeName(i);
                        if (shapeGlobs != null && !shapeGlobs.Any(g => g.IsMatch(n) || g.IsMatch(rel + "/" + n))) continue;
                        snap.Values[$"shape:{rel}/{n}"] = new double[] { smr.GetBlendShapeWeight(i) };
                    }
                }
            }

            var bones = Arg(a, "bones");
            if (bones != null)
            {
                var animator = root.GetComponent<Animator>();
                foreach (var b in bones is JArray arr ? arr.Select(x => x.ToString()) : new[] { bones.ToString() })
                {
                    Transform top = null;
                    if (animator && animator.isHuman && Enum.TryParse<HumanBodyBones>(b, true, out var hb) && hb != HumanBodyBones.LastBone) top = animator.GetBoneTransform(hb);
                    if (!top) top = root.transform.Find(b);
                    if (!top) top = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == b); // a bone by name
                    if (!top) throw new ArgumentException($"Bone '{b}' not found: use a humanoid bone name (Head, LeftEye, Jaw...), a transform name, or a path under {snap.Root}.");
                    foreach (var t in top.GetComponentsInChildren<Transform>(true))
                    {
                        var r = t.localRotation;
                        var p = t.localPosition;
                        snap.Values["bone:" + AnimationUtility.CalculateTransformPath(t, root.transform)] = new double[] { r.x, r.y, r.z, r.w, p.x, p.y, p.z };
                    }
                }
            }

            var paramGlobs = GlobArg(a, "params", out var allParams);
            if ((allParams || paramGlobs != null) && Application.isPlaying)
            {
                var rt = FindType(RuntimeTypeName) != null ? root.GetComponent(FindType(RuntimeTypeName)) : null;
                if (rt)
                    foreach (var lp in LiveParams(rt).Values)
                        if (paramGlobs == null || paramGlobs.Any(g => g.IsMatch(lp.Name)))
                            snap.Values["param:" + lp.Name] = new[] { Num(lp.Value) };
            }

            return snap;
        }

        static JToken Show(string key, double[] v)
        {
            if (!key.StartsWith("bone:", StringComparison.Ordinal)) return R(v[0]);
            var e = new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]).eulerAngles;
            return $"rot({Math.Round(e.x, 1)}, {Math.Round(e.y, 1)}, {Math.Round(e.z, 1)}) pos({Math.Round(v[4], 4)}, {Math.Round(v[5], 4)}, {Math.Round(v[6], 4)})";
        }

        static string Change(string key, double[] x, double[] y, JObject tol)
        {
            if (key.StartsWith("bone:", StringComparison.Ordinal))
            {
                var angle = Quaternion.Angle(new Quaternion((float)x[0], (float)x[1], (float)x[2], (float)x[3]), new Quaternion((float)y[0], (float)y[1], (float)y[2], (float)y[3]));
                var dist = Math.Sqrt(Math.Pow(x[4] - y[4], 2) + Math.Pow(x[5] - y[5], 2) + Math.Pow(x[6] - y[6], 2));
                if (angle <= Float(tol, "angle", 0.5f) && dist <= Float(tol, "position", 0.0005f)) return null;
                return $"{Show(key, x)} => {Show(key, y)} (turned {Math.Round(angle, 1)}°, moved {Math.Round(dist, 4)} m)";
            }

            var limit = key.StartsWith("shape:", StringComparison.Ordinal) ? Float(tol, "shape", 0.5f) : Float(tol, "param", 0.001f);
            return Math.Abs(x[0] - y[0]) <= limit ? null : $"{R(x[0])} => {R(y[0])}";
        }

        static JToken SnapshotTool(JObject a)
        {
            var action = (Str(a, "action") ?? "capture").ToLowerInvariant();
            var limit = Int(a, "limit", 150);
            switch (action)
            {
                case "capture":
                {
                    var snap = Capture(SnapshotRoot(a), a);
                    if (snap.Values.Count == 0) throw new ArgumentException("Nothing to capture: pass blendshapes (true or globs), bones (e.g. \"Head\") and/or params (play mode).");
                    var name = Str(a, "name");
                    if (name != null) Snaps[name] = snap;
                    var includeZero = Bool(a, "includeZero", false);
                    var shown = snap.Values.Where(kv => includeZero || !kv.Key.StartsWith("shape:", StringComparison.Ordinal) || Math.Abs(kv.Value[0]) > 1e-4).ToList();
                    var res = new JObject
                    {
                        ["root"] = snap.Root, ["saved"] = name, ["captured"] = snap.Values.Count,
                        ["values"] = new JObject(shown.Take(limit).Select(kv => new JProperty(kv.Key, Show(kv.Key, kv.Value)))),
                    };
                    if (!includeZero && shown.Count < snap.Values.Count) res["zeroShapesHidden"] = snap.Values.Count - shown.Count;
                    if (shown.Count > limit) res["truncated"] = $"{shown.Count - limit} more; narrow the globs, raise limit, or save with name and diff.";
                    return res;
                }
                case "diff":
                {
                    var from = Snaps.TryGetValue(Required(a, "from"), out var f) ? f : throw new ArgumentException($"No snapshot '{Str(a, "from")}'. Saved: {string.Join(", ", Snaps.Keys)}");
                    Snap to;
                    var toName = Str(a, "to");
                    if (toName != null) to = Snaps.TryGetValue(toName, out var t) ? t : throw new ArgumentException($"No snapshot '{toName}'.");
                    else
                    {
                        var spec = Arg(a, "blendshapes") != null || Arg(a, "bones") != null || Arg(a, "params") != null ? a : SpecOf(from, a);
                        to = Capture(FindGameObject(from.Root), spec);
                    }

                    var tol = Arg(a, "tolerance") as JObject ?? new JObject();
                    var changes = new JObject();
                    var count = 0;
                    foreach (var kv in from.Values)
                    {
                        if (!to.Values.TryGetValue(kv.Key, out var y)) continue;
                        var c = Change(kv.Key, kv.Value, y, tol);
                        if (c == null) continue;
                        if (count++ < limit) changes[kv.Key] = c;
                    }

                    var res = new JObject
                    {
                        ["from"] = Str(a, "from"), ["to"] = toName ?? "now", ["seconds"] = R(to.Time - from.Time),
                        ["compared"] = from.Values.Keys.Count(to.Values.ContainsKey), ["changed"] = count, ["changes"] = changes,
                    };
                    var gone = from.Values.Keys.Where(k => !to.Values.ContainsKey(k)).ToList();
                    var added = to.Values.Keys.Where(k => !from.Values.ContainsKey(k)).ToList();
                    if (gone.Count > 0) res["onlyInFrom"] = new JArray(gone.Take(20));
                    if (added.Count > 0) res["onlyInTo"] = new JArray(added.Take(20));
                    if (count > limit) res["truncated"] = $"{count - limit} more changes.";
                    return res;
                }
                case "list":
                    return new JObject(Snaps.Select(kv => new JProperty(kv.Key, $"{kv.Value.Root}: {kv.Value.Values.Count} values")));
                case "clear":
                    Snaps.Clear();
                    return "cleared";
                default:
                    throw new ArgumentException("action must be capture, diff, list or clear.");
            }
        }

        /// <summary>Re-captures what a saved snapshot holds (its keys' categories).</summary>
        static JObject SpecOf(Snap s, JObject a)
        {
            var o = new JObject(a);
            var shapes = s.Values.Keys.Where(k => k.StartsWith("shape:", StringComparison.Ordinal)).ToList();
            if (shapes.Count > 0) o["blendshapes"] = true;
            if (s.Values.Keys.Any(k => k.StartsWith("param:", StringComparison.Ordinal))) o["params"] = true;
            var bones = s.Values.Keys.Where(k => k.StartsWith("bone:", StringComparison.Ordinal)).Select(k => k.Substring(5)).ToList();
            // the shortest paths are the roots that were captured
            var roots = bones.Where(b => !bones.Any(o2 => o2 != b && b.StartsWith(o2 + "/", StringComparison.Ordinal))).ToList();
            if (roots.Count > 0) o["bones"] = new JArray(roots);
            return o;
        }

        // ---------- editor_prefs ----------

        static JToken EditorPrefsTool(JObject a)
        {
            var key = Required(a, "key");
            var action = (Str(a, "action") ?? "get").ToLowerInvariant();
            var type = (Str(a, "type") ?? "").ToLowerInvariant();
            switch (action)
            {
                case "get":
                    if (!EditorPrefs.HasKey(key)) return new JObject { ["key"] = key, ["exists"] = false };
                    switch (type)
                    {
                        case "bool": return new JObject { ["key"] = key, ["value"] = EditorPrefs.GetBool(key) };
                        case "int": return new JObject { ["key"] = key, ["value"] = EditorPrefs.GetInt(key) };
                        case "float": return new JObject { ["key"] = key, ["value"] = EditorPrefs.GetFloat(key) };
                        case "string": return new JObject { ["key"] = key, ["value"] = EditorPrefs.GetString(key) };
                        default:
                            // EditorPrefs doesn't store the type: show the readings that make sense
                            return new JObject
                            {
                                ["key"] = key, ["exists"] = true, ["string"] = EditorPrefs.GetString(key), ["int"] = EditorPrefs.GetInt(key),
                                ["float"] = EditorPrefs.GetFloat(key), ["note"] = "Pass type (bool, int, float, string) for one reading; bools are stored as int.",
                            };
                    }
                case "set":
                {
                    var v = Arg(a, "value") ?? throw new ArgumentException("'value' is required.");
                    switch (type == "" ? (v.Type == JTokenType.Boolean ? "bool" : v.Type == JTokenType.Integer ? "int" : v.Type == JTokenType.Float ? "float" : "string") : type)
                    {
                        case "bool": EditorPrefs.SetBool(key, v.Value<bool>()); break;
                        case "int": EditorPrefs.SetInt(key, v.Value<int>()); break;
                        case "float": EditorPrefs.SetFloat(key, v.Value<float>()); break;
                        case "string": EditorPrefs.SetString(key, v.ToString()); break;
                        default: throw new ArgumentException("type must be bool, int, float or string.");
                    }

                    return new JObject { ["key"] = key, ["set"] = v };
                }
                case "delete":
                    EditorPrefs.DeleteKey(key);
                    return new JObject { ["key"] = key, ["deleted"] = true };
                default:
                    throw new ArgumentException("action must be get, set or delete.");
            }
        }
    }
}
