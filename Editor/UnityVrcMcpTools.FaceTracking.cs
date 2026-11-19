// ft_set: a play-mode stand-in for VRChat's native face tracking (VRCAvatarDescriptor Face Tracking, SDK 3.10+),
// which neither Av3Emulator nor Gesture Manager simulates. Takes Unified Expressions key values and
//  - drives the descriptor's Blendshapes Sets (several sets on one shape combine by max; key x multiplier x set weight),
//  - evaluates Parameter Behaviors with Forward to Animator / Custom expressions into animator parameters each frame,
//  - turns the eye bones toward the Eye Look states (eyesLookingUp/Down/Left/Right) for the EyeLook* keys.
// Based on the comparison harness written for Dergal's native FT migration (Assets/NativeFaceTracking/Compare).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        sealed class FtShape
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public string Name;
            public List<(string key, float mult)> Keys;
            public float Original;
            public float Weight;
        }

        sealed class FtSim
        {
            public Component Runtime;
            public readonly List<FtShape> Shapes = new List<FtShape>();
            public readonly List<(string param, string expr)> Forwards = new List<(string, string)>();
            public readonly List<string> Notes = new List<string>();
            public Quaternion[] Left, Right; // straight, up, down, left, right
            public Transform LeftEye, RightEye;
            public Quaternion LeftOriginal, RightOriginal, LeftRotation, RightRotation;
            public bool DriveEyes;
            public HashSet<string> KeyNames;
            public Dictionary<string, float> Keys = new Dictionary<string, float>(StringComparer.Ordinal);
            public int Sets;
            /// <summary>Forwarded parameters' live values from before the first ft_set, restored on stop.</summary>
            public readonly Dictionary<string, object> ParamOriginals = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        static FtSim _ft;
        static bool _ftHooked;

        static HashSet<string> FaceTrackingKeys()
        {
            var t = FindType("FaceTrackingKey");
            if (t == null || !t.IsEnum) throw new InvalidOperationException("This VRChat SDK has no native face tracking (FaceTrackingKey not found; needs SDK 3.10+).");
            return new HashSet<string>(Enum.GetNames(t), StringComparer.Ordinal);
        }

        static FtSim ReadNativeFt(Component rt)
        {
            var dt = FindType(DescriptorType) ?? throw new InvalidOperationException("VRChat Avatars SDK not found.");
            var desc = Get(rt, "avadesc") as Component ?? rt.GetComponent(dt) ?? throw new InvalidOperationException("No VRCAvatarDescriptor on the emulated avatar.");
            var so = new SerializedObject(desc);
            var sim = new FtSim { Runtime = rt, KeyNames = FaceTrackingKeys() };
            var enable = so.FindProperty("enableFaceTracking");
            if (enable == null) throw new InvalidOperationException("The descriptor has no Face Tracking section (needs SDK 3.10+).");
            if (!enable.boolValue) sim.Notes.Add("enableFaceTracking is off on the play-mode descriptor (did the build hooks, e.g. a VRCFury Full Controller, set it up?).");

            var keyType = FindType("FaceTrackingKey");
            var keyByIndex = new Dictionary<int, string>();
            foreach (var v in Enum.GetValues(keyType)) keyByIndex[Convert.ToInt32(v)] = v.ToString();

            var meshes = new List<SkinnedMeshRenderer>();
            var meshProp = so.FindProperty("faceTrackingMeshes");
            for (var i = 0; meshProp != null && i < meshProp.arraySize; i++)
                if (meshProp.GetArrayElementAtIndex(i).objectReferenceValue is SkinnedMeshRenderer smr && smr) meshes.Add(smr);
            if (meshes.Count == 0) sim.Notes.Add("No face tracking meshes on the descriptor.");

            var byShape = new Dictionary<string, List<(string, float)>>(StringComparer.Ordinal);
            var sets = so.FindProperty("faceTrackingBlendshapesSetList");
            for (var s = 0; sets != null && s < sets.arraySize; s++)
            {
                var set = sets.GetArrayElementAtIndex(s);
                var weight = set.FindPropertyRelative("setWeight")?.floatValue ?? 1f;
                var names = set.FindPropertyRelative("faceTrackingBlendshapeNames");
                var mults = set.FindPropertyRelative("faceTrackingBlendshapeMultipliers");
                if (names == null) continue;
                sim.Sets++;
                for (var k = 0; k < names.arraySize; k++)
                {
                    var shape = names.GetArrayElementAtIndex(k).stringValue;
                    if (string.IsNullOrEmpty(shape) || !keyByIndex.TryGetValue(k, out var key)) continue;
                    var mult = mults != null && k < mults.arraySize ? mults.GetArrayElementAtIndex(k).floatValue : 1f;
                    if (!byShape.TryGetValue(shape, out var list)) byShape[shape] = list = new List<(string, float)>();
                    list.Add((key, mult * weight));
                }
            }

            foreach (var smr in meshes)
            {
                var mesh = smr.sharedMesh;
                if (!mesh) continue;
                foreach (var kv in byShape)
                {
                    var idx = mesh.GetBlendShapeIndex(kv.Key);
                    if (idx < 0)
                    {
                        if (sim.Notes.Count < 20) sim.Notes.Add($"Blendshape '{kv.Key}' is not on {smr.name} at runtime (renamed by a build hook?).");
                        continue;
                    }

                    sim.Shapes.Add(new FtShape { Renderer = smr, Index = idx, Name = $"{smr.name}/{kv.Key}", Keys = kv.Value, Original = smr.GetBlendShapeWeight(idx) });
                }
            }

            var behaviors = so.FindProperty("faceTrackingParameterBehaviors");
            for (var b = 0; behaviors != null && b < behaviors.arraySize; b++)
            {
                var e = behaviors.GetArrayElementAtIndex(b);
                string EnumName(string field) => e.FindPropertyRelative(field) is SerializedProperty p && p.propertyType == SerializedPropertyType.Enum && p.enumValueIndex >= 0 ? p.enumNames[p.enumValueIndex] : null;
                var target = EnumName("targetMode");
                var combine = EnumName("combineMode");
                var output = e.FindPropertyRelative("outputParameterName")?.stringValue;
                if (target != "ForwardToAnimator" || combine != "Custom")
                {
                    sim.Notes.Add($"Not simulated: parameter behavior {target}/{combine} -> {output}");
                    continue;
                }

                sim.Forwards.Add((output, e.FindPropertyRelative("customExpression")?.stringValue ?? ""));
            }

            var eye = so.FindProperty("customEyeLookSettings");
            if (eye != null)
            {
                string[] states = { "eyesLookingStraight", "eyesLookingUp", "eyesLookingDown", "eyesLookingLeft", "eyesLookingRight" };
                Quaternion Q(string state, string side) => eye.FindPropertyRelative(state)?.FindPropertyRelative(side)?.quaternionValue ?? Quaternion.identity;
                sim.Left = states.Select(s => Q(s, "left")).ToArray();
                sim.Right = states.Select(s => Q(s, "right")).ToArray();
                sim.LeftEye = eye.FindPropertyRelative("leftEye")?.objectReferenceValue as Transform;
                sim.RightEye = eye.FindPropertyRelative("rightEye")?.objectReferenceValue as Transform;
                if (sim.LeftEye) sim.LeftOriginal = sim.LeftEye.localRotation;
                if (sim.RightEye) sim.RightOriginal = sim.RightEye.localRotation;
            }

            sim.Notes.Add("Eyelids via face tracking, Viseme Blend Shape Face Tracking and non-Custom parameter behaviors are not simulated.");
            return sim;
        }

        static void FtAfterAnimation()
        {
            var sim = _ft;
            if (sim == null || !sim.Runtime) return;
            foreach (var s in sim.Shapes)
                if (s.Renderer) s.Renderer.SetBlendShapeWeight(s.Index, s.Weight);
            if (!sim.DriveEyes) return;
            if (sim.LeftEye) sim.LeftEye.localRotation = sim.LeftRotation;
            if (sim.RightEye) sim.RightEye.localRotation = sim.RightRotation;
        }

        static void StopFt()
        {
            var sim = _ft;
            _ft = null;
            ReleaseHolds(h => h.Owner == "ft_set");
            if (sim == null) return;
            if (sim.Runtime && sim.ParamOriginals.Count > 0)
                Av3Apply(sim.Runtime, new JObject(sim.ParamOriginals.Select(kv => new JProperty(kv.Key, J(kv.Value)))), null, false);
            foreach (var s in sim.Shapes)
                if (s.Renderer) s.Renderer.SetBlendShapeWeight(s.Index, s.Original);
            if (!sim.DriveEyes) return;
            if (sim.LeftEye) sim.LeftEye.localRotation = sim.LeftOriginal;
            if (sim.RightEye) sim.RightEye.localRotation = sim.RightOriginal;
        }

        static string Euler(Quaternion q)
        {
            var e = q.eulerAngles;
            return $"({Math.Round(e.x, 1)}, {Math.Round(e.y, 1)}, {Math.Round(e.z, 1)})";
        }

        // x > 0: looking to the avatar's right; y > 0: up. States: straight, up, down, left, right.
        static Quaternion Gaze(Quaternion[] st, float x, float y)
        {
            var h = Quaternion.Slerp(st[0], x >= 0 ? st[4] : st[3], Mathf.Abs(x));
            var v = Quaternion.Slerp(st[0], y >= 0 ? st[1] : st[2], Mathf.Abs(y));
            return h * Quaternion.Inverse(st[0]) * v;
        }

        static JToken FtSet(JObject a)
        {
            if (Bool(a, "listKeys", false))
                return new JObject { ["keys"] = new JArray(FaceTrackingKeys().OrderBy(k => k, StringComparer.Ordinal)) };
            if (Bool(a, "stop", false))
            {
                var was = _ft != null;
                StopFt();
                return new JObject { ["stopped"] = was, ["note"] = "Blendshapes, eye rotations and forwarded parameters restored to their values from before ft_set." };
            }

            var rt = FindRuntime(a);
            if (_ft == null || _ft.Runtime != rt || Bool(a, "reload", false))
            {
                StopFt();
                _ft = ReadNativeFt(rt);
            }

            var sim = _ft;
            if (!_ftHooked)
            {
                McpPlayerLoop.Ensure();
                McpPlayerLoop.AfterAnimation += FtAfterAnimation;
                McpPlayerLoop.Reset += () => _ft = null;
                _ftHooked = true;
            }

            var keys = Bool(a, "merge", false) ? new Dictionary<string, float>(sim.Keys, StringComparer.Ordinal) : new Dictionary<string, float>(StringComparer.Ordinal);
            if (Arg(a, "keys") is JObject given)
                foreach (var kv in given)
                {
                    if (!sim.KeyNames.Contains(kv.Key))
                    {
                        var close = sim.KeyNames.Where(k => k.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 || kv.Key.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0).Take(12);
                        throw new ArgumentException($"'{kv.Key}' is not a Unified Expressions key. Similar: {string.Join(", ", close)} (listKeys: true lists all).");
                    }

                    keys[kv.Key] = kv.Value.Value<float>();
                }

            sim.Keys = keys;
            float K(string n) => keys.TryGetValue(n, out var v) ? v : 0f;

            foreach (var s in sim.Shapes) s.Weight = s.Keys.Max(e => Mathf.Clamp01(K(e.key) * e.mult)) * 100f;

            var forwards = new JObject();
            var errors = new JArray();
            foreach (var (param, expr) in sim.Forwards)
            {
                try { forwards[param] = Mathf.Clamp(new FtExpr(expr, K).Parse(), -1f, 1f); }
                catch (Exception e) { errors.Add($"{param} = {expr}: {e.Message}"); }
            }

            // forwarded values are held (re-applied every frame before animation) until the next ft_set or stop
            ReleaseHolds(h => h.Owner == "ft_set");
            var expected = new JObject();
            JArray applied = null;
            var missing = new JArray();
            var present = new JObject();
            var liveNow = LiveParams(rt);
            foreach (var p in forwards.Properties())
            {
                foreach (var n in new[] { p.Name }.Concat(Twins(rt, p.Name)))
                    if (!sim.ParamOriginals.ContainsKey(n) && liveNow.TryGetValue(n, out var lpo)) sim.ParamOriginals[n] = lpo.Value;
                if (WriteRuntimeParam(rt, p.Name, p.Value) != null || Twins(rt, p.Name).Any()) present[p.Name] = p.Value;
                else missing.Add(p.Name);
            }

            if (present.Count > 0) applied = Av3Apply(rt, present, expected, true, "ft_set");

            var gazeKeys = new[] { "EyeLookInLeft", "EyeLookOutLeft", "EyeLookUpLeft", "EyeLookDownLeft", "EyeLookInRight", "EyeLookOutRight", "EyeLookUpRight", "EyeLookDownRight" };
            var gaze = gazeKeys.Any(k => Math.Abs(K(k)) > 1e-4);
            if (!gaze && sim.DriveEyes)
            {
                if (sim.LeftEye) sim.LeftEye.localRotation = sim.LeftOriginal;
                if (sim.RightEye) sim.RightEye.localRotation = sim.RightOriginal;
            }

            sim.DriveEyes = gaze && sim.Left != null && (sim.LeftEye || sim.RightEye);
            if (sim.DriveEyes)
            {
                sim.LeftRotation = Gaze(sim.Left, K("EyeLookInLeft") - K("EyeLookOutLeft"), K("EyeLookUpLeft") - K("EyeLookDownLeft"));
                sim.RightRotation = Gaze(sim.Right, K("EyeLookOutRight") - K("EyeLookInRight"), K("EyeLookUpRight") - K("EyeLookDownRight"));
            }

            var driven = sim.Shapes.Where(s => s.Weight > 0.01f).OrderByDescending(s => s.Weight).ToList();
            var res = new JObject
            {
                ["avatar"] = GetPath(rt.transform),
                ["keys"] = new JObject(keys.Where(kv => Math.Abs(kv.Value) > 1e-4).Select(kv => new JProperty(kv.Key, R(kv.Value)))),
                ["setup"] = $"{sim.Sets} blendshape set(s), {sim.Shapes.Count} mesh shapes, {sim.Forwards.Count} forwarded parameters, eyes {(sim.LeftEye ? sim.LeftEye.name : "-")}/{(sim.RightEye ? sim.RightEye.name : "-")}",
                ["shapes"] = new JObject(driven.Take(Int(a, "limit", 40)).Select(s => new JProperty(s.Name, R(s.Weight)))),
                ["forwarded"] = new JObject(forwards.Properties().Where(p => Math.Abs(p.Value.Value<float>()) > 1e-4).Select(p => new JProperty(p.Name, R(p.Value.Value<float>())))),
                ["eyes"] = sim.DriveEyes ? $"local rotation left {Euler(sim.LeftRotation)}, right {Euler(sim.RightRotation)}" : "not driven (no EyeLook* key)",
                ["note"] = "Held every frame until the next ft_set or ft_set {stop: true}: blendshapes after animation, forwarded parameters before it.",
            };
            if (driven.Count > Int(a, "limit", 40)) res["moreShapes"] = driven.Count - Int(a, "limit", 40);
            if (applied != null) res["parameters"] = applied;
            if (missing.Count > 0) res["forwardedButNotOnAvatar"] = missing;
            if (errors.Count > 0) res["expressionErrors"] = errors;
            if (sim.Notes.Count > 0) res["notes"] = new JArray(sim.Notes);
            return res;
        }

        /// <summary>Custom expression of a Forward to Animator behavior: + - * / ( ), min max abs clamp, key names, numbers.</summary>
        sealed class FtExpr
        {
            readonly string _s;
            readonly Func<string, float> _key;
            int _p;

            public FtExpr(string s, Func<string, float> key)
            {
                _s = s ?? "";
                _key = key;
            }

            public float Parse()
            {
                var v = Sum();
                Ws();
                if (_p != _s.Length) throw new FormatException("unexpected text at " + _p);
                return v;
            }

            void Ws()
            {
                while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++;
            }

            bool Eat(char c)
            {
                Ws();
                if (_p >= _s.Length || _s[_p] != c) return false;
                _p++;
                return true;
            }

            float Sum()
            {
                var v = Product();
                while (true)
                {
                    if (Eat('+')) v += Product();
                    else if (Eat('-')) v -= Product();
                    else return v;
                }
            }

            float Product()
            {
                var v = Unary();
                while (true)
                {
                    if (Eat('*')) v *= Unary();
                    else if (Eat('/'))
                    {
                        var d = Unary();
                        v = Mathf.Approximately(d, 0f) ? 0f : v / d;
                    }
                    else return v;
                }
            }

            float Unary() => Eat('-') ? -Unary() : Atom();

            float Atom()
            {
                Ws();
                if (Eat('('))
                {
                    var v = Sum();
                    if (!Eat(')')) throw new FormatException("missing )");
                    return v;
                }

                var start = _p;
                if (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.'))
                {
                    while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
                    return float.Parse(_s.Substring(start, _p - start), CultureInfo.InvariantCulture);
                }

                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
                if (start == _p) throw new FormatException("unexpected character at " + _p);
                var id = _s.Substring(start, _p - start);
                if (!Eat('(')) return _key(id);
                var args = new List<float> { Sum() };
                while (Eat(',')) args.Add(Sum());
                if (!Eat(')')) throw new FormatException("missing ) after " + id);
                switch (id.ToLowerInvariant())
                {
                    case "min": return args.Min();
                    case "max": return args.Max();
                    case "abs": return Mathf.Abs(args[0]);
                    case "clamp": return args.Count >= 3 ? Mathf.Clamp(args[0], args[1], args[2]) : Mathf.Clamp01(args[0]);
                    default: throw new FormatException("unknown function " + id);
                }
            }
        }
    }
}
