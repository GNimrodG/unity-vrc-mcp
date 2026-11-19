// edit_animator: targeted, undoable edits to an AnimatorController for avatar fixes (transitions, states, write
// defaults, parameters, layers). Refuses generated controllers (VRCFury builds, optimizer output, read-only packages)
// unless forced, because edits there are thrown away on the next build.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        const string AnyStateName = "AnyState";
        const string EntryName = "Entry";
        const string ExitName = "Exit";
        const string ArgState = "state";
        const string ArgDefault = "default";

        static readonly Regex ConditionPattern = new Regex(@"^(!?)\s*(.+?)\s*(?:(==|!=|>|<)\s*(\S+))?$", RegexOptions.None, TimeSpan.FromMilliseconds(100));

        sealed class EditContext
        {
            public AnimatorController Ctrl;
            public AnimatorControllerLayer Layer;
            public int LayerIndex;
            public List<(string path, AnimatorState state)> States;
            public readonly List<string> Changes = new List<string>();
        }

        static AnimatorController ControllerForEdit(JObject a)
        {
            var assetPath = Str(a, "assetPath");
            if (assetPath != null)
                return AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) ?? throw new ArgumentException($"No AnimatorController at {assetPath}.");
            var path = Str(a, "path") ?? throw new ArgumentException("Pass 'assetPath', or 'path' + 'avatarLayer' (e.g. FX).");
            var go = FindGameObject(path);
            var avatarLayer = Str(a, "avatarLayer");
            if (avatarLayer != null) return ControllerFromDescriptor(go, avatarLayer);
            var rac = go.GetComponent<Animator>()?.runtimeAnimatorController;
            if (rac is AnimatorOverrideController oc) rac = oc.runtimeAnimatorController;
            return rac as AnimatorController ?? throw new ArgumentException($"No AnimatorController on {path}; pass avatarLayer or assetPath.");
        }

        static string GeneratedReason(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "it isn't saved as an asset";
            if (assetPath.Contains("com.vrcfury.temp")) return "it is a VRCFury build output";
            if (assetPath.Contains("/TrashBin/")) return "it is an optimizer's output (TrashBin)";
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return "it belongs to a package (edits are lost on update)";
            return null;
        }

        static AnimatorControllerLayer FindLayer(AnimatorController ctrl, string name, out int index)
        {
            var layers = ctrl.layers;
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < layers.Length) return layers[index];
            index = Array.FindIndex(layers, l => l.name == name);
            if (index < 0)
            {
                var matches = layers.Select((l, i) => (l, i)).Where(x => x.l.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matches.Count == 1) index = matches[0].i;
                else if (matches.Count > 1) throw new ArgumentException($"Layer '{name}' is ambiguous: {string.Join(", ", matches.Select(m => m.l.name))}");
            }

            return index >= 0 ? layers[index] : throw new ArgumentException($"No layer '{name}'. Layers: {string.Join(", ", layers.Select(l => l.name))}");
        }

        static AnimatorState FindState(EditContext c, string name)
        {
            var exact = c.States.Where(s => s.path == name || s.state.name == name).ToList();
            if (exact.Count == 1) return exact[0].state;
            if (exact.Count > 1) throw new ArgumentException($"State '{name}' is ambiguous: {string.Join(", ", exact.Select(s => s.path))}. Use the full path.");
            throw new ArgumentException($"No state '{name}' in layer {c.Layer.name}. States: {string.Join(", ", c.States.Select(s => s.path).Take(40))}");
        }

        static AnimatorStateMachine MachineOf(AnimatorStateMachine root, AnimatorState state)
        {
            if (root.states.Any(s => s.state == state)) return root;
            foreach (var child in root.stateMachines)
            {
                var found = MachineOf(child.stateMachine, state);
                if (found) return found;
            }

            return null;
        }

        static void Touch(UnityEngine.Object o, string what)
        {
            if (!o) return;
            Undo.RecordObject(o, "edit_animator: " + what);
            EditorUtility.SetDirty(o);
        }

        // ---------- conditions ----------

        static AnimatorCondition ParseCondition(string text, Dictionary<string, AnimatorControllerParameterType> types, bool allowMissing)
        {
            var m = ConditionPattern.Match(text.Trim());
            if (!m.Success) throw new ArgumentException($"Can't parse condition '{text}'. Use 'Param', '!Param', 'Param > 0.5', 'Param == 3', 'Param != 3'.");
            var not = m.Groups[1].Value == "!";
            var name = m.Groups[2].Value.Trim();
            var op = m.Groups[3].Success ? m.Groups[3].Value : null;
            var value = m.Groups[4].Success ? m.Groups[4].Value : null;
            if (!types.TryGetValue(name, out var type))
            {
                if (!allowMissing) throw new ArgumentException($"Parameter '{name}' isn't in the controller (add it first, or pass allowMissingParameters).");
                type = op == null ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float;
            }

            var c = new AnimatorCondition { parameter = name };
            if (type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger)
            {
                var truthy = op == null || ((op == "==") == (value == "true" || value == "1"));
                if (op != null && op != "==" && op != "!=") throw new ArgumentException($"'{text}': use Param, !Param or == true/false for a {type}.");
                if (op == "!=") truthy = !(value == "true" || value == "1");
                c.mode = truthy != not ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
                return c;
            }

            if (op == null || not) throw new ArgumentException($"'{text}': a {type} parameter needs a comparison (> < == !=).");
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                throw new ArgumentException($"'{text}': '{value}' isn't a number.");
            if (type == AnimatorControllerParameterType.Float && (op == "==" || op != "!=" && op != ">" && op != "<"))
                throw new ArgumentException($"'{text}': == on a Float never matches reliably (VRChat syncs floats lossy); use > and <.");
            if (type == AnimatorControllerParameterType.Float && op == "!=")
                throw new ArgumentException($"'{text}': != isn't available for Float parameters; use > and <.");
            c.threshold = threshold;
            c.mode = op == ">" ? AnimatorConditionMode.Greater : op == "<" ? AnimatorConditionMode.Less
                : op == "==" ? AnimatorConditionMode.Equals : AnimatorConditionMode.NotEqual;
            return c;
        }

        // ---------- transitions ----------

        static List<(AnimatorTransitionBase t, Action remove)> SourceTransitions(EditContext c, string from)
        {
            var sm = c.Layer.stateMachine;
            if (from == AnyStateName)
                return sm.anyStateTransitions.Select(t => ((AnimatorTransitionBase)t, (Action)(() => sm.RemoveAnyStateTransition(t)))).ToList();
            if (from == EntryName)
                return sm.entryTransitions.Select(t => ((AnimatorTransitionBase)t, (Action)(() => sm.RemoveEntryTransition(t)))).ToList();
            var state = FindState(c, from);
            return state.transitions.Select(t => ((AnimatorTransitionBase)t, (Action)(() => state.RemoveTransition(t)))).ToList();
        }

        static string DestName(AnimatorTransitionBase t) =>
            t.isExit ? ExitName : t.destinationState ? t.destinationState.name : t.destinationStateMachine ? t.destinationStateMachine.name : "?";

        static List<(AnimatorTransitionBase t, Action remove)> SelectTransitions(EditContext c, JObject a)
        {
            var from = Required(a, "from");
            var all = SourceTransitions(c, from);
            var to = Str(a, "to");
            var list = to == null ? all : all.Where(x => DestName(x.t) == to || (x.t.destinationState && c.States.Any(s => s.state == x.t.destinationState && s.path == to))).ToList();
            var index = Arg(a, "index");
            if (index != null)
            {
                var i = index.Value<int>();
                if (i < 0 || i >= list.Count) throw new ArgumentException($"index {i} out of range ({list.Count} matching transitions).");
                list = new List<(AnimatorTransitionBase, Action)> { list[i] };
            }

            if (list.Count == 0)
                throw new ArgumentException($"No transition from {from}{(to != null ? " to " + to : "")}. Existing: " +
                                            string.Join("; ", all.Select(x => TransitionString(x.t))));
            return list;
        }

        static void ApplyTransitionFields(AnimatorTransitionBase t, JObject set, EditContext c, bool allowMissing)
        {
            Touch(t, "transition");
            var types = c.Ctrl.parameters.ToDictionary(p => p.name, p => p.type);
            if (set["conditions"] is JArray conds)
                t.conditions = conds.Select(x => ParseCondition(x.ToString(), types, allowMissing)).ToArray();
            if (set["mute"] != null) t.mute = set.Value<bool>("mute");
            if (set["solo"] != null) t.solo = set.Value<bool>("solo");
            if (!(t is AnimatorStateTransition st))
            {
                if (set.Properties().Any(p => p.Name != "conditions" && p.Name != "mute" && p.Name != "solo"))
                    throw new ArgumentException("Entry/state-machine transitions only have conditions, mute and solo.");
                return;
            }

            if (set["hasExitTime"] != null) st.hasExitTime = set.Value<bool>("hasExitTime");
            if (set["exitTime"] != null) st.exitTime = set.Value<float>("exitTime");
            if (set["duration"] != null) st.duration = set.Value<float>("duration");
            if (set["hasFixedDuration"] != null) st.hasFixedDuration = set.Value<bool>("hasFixedDuration");
            if (set["offset"] != null) st.offset = set.Value<float>("offset");
            if (set["canTransitionToSelf"] != null) st.canTransitionToSelf = set.Value<bool>("canTransitionToSelf");
            if (set["orderedInterruption"] != null) st.orderedInterruption = set.Value<bool>("orderedInterruption");
            if (set["interruptionSource"] != null)
                st.interruptionSource = (TransitionInterruptionSource)Enum.Parse(typeof(TransitionInterruptionSource), set.Value<string>("interruptionSource"), true);
        }

        static JObject TransitionFields(JObject a)
        {
            var set = new JObject();
            foreach (var k in new[] { "conditions", "mute", "solo", "hasExitTime", "exitTime", "duration", "hasFixedDuration", "offset", "canTransitionToSelf", "orderedInterruption", "interruptionSource" })
                if (Arg(a, k) is JToken v) set[k] = v;
            return set;
        }

        static void SetTransition(EditContext c, JObject a)
        {
            var set = TransitionFields(a);
            if (set.Count == 0) throw new ArgumentException("Nothing to set: pass conditions, hasExitTime, exitTime, duration, canTransitionToSelf, ...");
            foreach (var (t, _) in SelectTransitions(c, a))
            {
                var before = TransitionString(t);
                ApplyTransitionFields(t, set, c, Bool(a, "allowMissingParameters", false));
                c.Changes.Add($"{Required(a, "from")} {before}  =>  {TransitionString(t)}");
            }
        }

        static void AddTransition(EditContext c, JObject a)
        {
            var from = Required(a, "from");
            var to = Required(a, "to");
            var sm = c.Layer.stateMachine;
            AnimatorTransitionBase t;
            if (from == EntryName)
            {
                Touch(sm, "add transition");
                t = sm.AddEntryTransition(FindState(c, to));
            }
            else if (from == AnyStateName)
            {
                Touch(sm, "add transition");
                var st = sm.AddAnyStateTransition(FindState(c, to));
                st.canTransitionToSelf = false; // the usual intent on avatars; set it explicitly to override
                st.hasExitTime = false;
                st.duration = 0f;
                t = st;
            }
            else
            {
                var src = FindState(c, from);
                Touch(src, "add transition");
                var st = to == ExitName ? src.AddExitTransition() : src.AddTransition(FindState(c, to));
                st.hasExitTime = false;
                st.duration = 0f;
                t = st;
            }

            ApplyTransitionFields(t, TransitionFields(a), c, Bool(a, "allowMissingParameters", false));
            c.Changes.Add($"added {from} {TransitionString(t)}");
        }

        static void RemoveTransition(EditContext c, JObject a)
        {
            var from = Required(a, "from");
            var sm = c.Layer.stateMachine;
            foreach (var (t, remove) in SelectTransitions(c, a))
            {
                var text = TransitionString(t);
                Touch(sm, "remove transition");
                if (from != AnyStateName && from != EntryName) Touch(FindState(c, from), "remove transition");
                remove();
                c.Changes.Add($"removed {from} {text}");
            }
        }

        // ---------- states ----------

        static Motion LoadMotion(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null || v.ToString().Length == 0) return null;
            var parts = v.ToString().Split(new[] { "::" }, 2, StringSplitOptions.None);
            return LoadAsset(parts[0], parts.Length > 1 ? parts[1] : null, "Motion") as Motion
                   ?? throw new ArgumentException($"'{v}' isn't an AnimationClip or BlendTree.");
        }

        static void SetState(EditContext c, JObject a)
        {
            var state = FindState(c, Required(a, ArgState));
            Touch(state, "state");
            var before = $"motion={(state.motion ? state.motion.name : "none")}, wd={state.writeDefaultValues}, speed={R(state.speed)}";
            if (Arg(a, "writeDefaults") is JToken wd) state.writeDefaultValues = wd.Value<bool>();
            if (a.TryGetValue("motion", StringComparison.OrdinalIgnoreCase, out var motion)) state.motion = LoadMotion(motion);
            if (Arg(a, "speed") is JToken speed) state.speed = speed.Value<float>();
            if (a.TryGetValue("speedParameter", StringComparison.OrdinalIgnoreCase, out var sp))
            {
                state.speedParameterActive = sp.Type != JTokenType.Null && sp.ToString().Length > 0;
                if (state.speedParameterActive) state.speedParameter = sp.ToString();
            }

            if (a.TryGetValue("timeParameter", StringComparison.OrdinalIgnoreCase, out var tp))
            {
                state.timeParameterActive = tp.Type != JTokenType.Null && tp.ToString().Length > 0;
                if (state.timeParameterActive) state.timeParameter = tp.ToString();
            }

            c.Changes.Add($"state {state.name}: {before}  =>  motion={(state.motion ? state.motion.name : "none")}, wd={state.writeDefaultValues}, speed={R(state.speed)}" +
                          (state.speedParameterActive ? $", speedParam={state.speedParameter}" : "") + (state.timeParameterActive ? $", motionTime={state.timeParameter}" : ""));
        }

        static void AddState(EditContext c, JObject a)
        {
            var name = Required(a, ArgState);
            var sm = c.Layer.stateMachine;
            if (sm.states.Any(s => s.state.name == name)) throw new ArgumentException($"Layer {c.Layer.name} already has a state '{name}'.");
            Touch(sm, "add state");
            var pos = sm.states.Length > 0 ? sm.states.Max(s => s.position.y) + 70f : 0f;
            var state = sm.AddState(name, new Vector3(250f, pos, 0f));
            // Match the layer's write defaults unless told otherwise, to avoid introducing a WD mix.
            var majority = c.States.Count(s => s.state.writeDefaultValues) * 2 >= c.States.Count;
            state.writeDefaultValues = Arg(a, "writeDefaults")?.Value<bool>() ?? (c.States.Count == 0 || majority);
            if (a.TryGetValue("motion", StringComparison.OrdinalIgnoreCase, out var motion)) state.motion = LoadMotion(motion);
            if (Bool(a, ArgDefault, false)) sm.defaultState = state;
            c.Changes.Add($"added state {name} (motion={(state.motion ? state.motion.name : "none")}, wd={state.writeDefaultValues}{(sm.defaultState == state ? ", default" : "")})");
        }

        static void RemoveState(EditContext c, JObject a)
        {
            var state = FindState(c, Required(a, ArgState));
            var name = state.name;
            var incoming = c.States.Sum(s => s.state.transitions.Count(t => t.destinationState == state)) +
                           c.Layer.stateMachine.anyStateTransitions.Count(t => t.destinationState == state);
            var sm = MachineOf(c.Layer.stateMachine, state);
            Touch(sm, "remove state");
            // Unity removes transitions into the state along with it.
            sm.RemoveState(state);
            c.Changes.Add($"removed state {name}" + (incoming > 0 ? $" and {incoming} transition(s) into it" : ""));
        }

        static void SetWriteDefaults(EditContext c, JObject a)
        {
            var value = Arg(a, "value")?.Value<bool>() ?? throw new ArgumentException("'value' (true/false) is required.");
            var layers = Str(a, "layer") != null ? new[] { (c.Layer, c.LayerIndex) } : c.Ctrl.layers.Select((l, i) => (l, i)).ToArray();
            var changed = 0;
            foreach (var (layer, _) in layers)
            {
                var states = new List<(string, AnimatorState)>();
                if (layer.stateMachine) Walk(layer.stateMachine, "", states, new List<(string, AnimatorTransitionBase)>());
                foreach (var (_, s) in states.Where(x => x.Item2.writeDefaultValues != value))
                {
                    Touch(s, "write defaults");
                    s.writeDefaultValues = value;
                    changed++;
                }
            }

            c.Changes.Add($"write defaults -> {value} on {changed} state(s) in {(layers.Length == 1 ? layers[0].Item1.name : "all layers")}");
        }

        // ---------- parameters / layers ----------

        static void AddParameter(EditContext c, JObject a)
        {
            var name = Required(a, "name");
            if (c.Ctrl.parameters.Any(p => p.name == name)) throw new ArgumentException($"Parameter '{name}' already exists.");
            var type = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), Str(a, "type") ?? "Bool", true);
            Touch(c.Ctrl, "add parameter");
            c.Ctrl.AddParameter(name, type);
            SetParameterDefault(c, name, Arg(a, ArgDefault));
            c.Changes.Add($"added parameter {name}: {type}");
        }

        static void SetParameterDefault(EditContext c, string name, JToken value)
        {
            if (value == null) return;
            var ps = c.Ctrl.parameters;
            var i = Array.FindIndex(ps, p => p.name == name);
            if (i < 0) throw new ArgumentException($"No parameter '{name}'.");
            switch (ps[i].type)
            {
                case AnimatorControllerParameterType.Float: ps[i].defaultFloat = value.Value<float>(); break;
                case AnimatorControllerParameterType.Int: ps[i].defaultInt = value.Value<int>(); break;
                default: ps[i].defaultBool = value.Type == JTokenType.Boolean ? value.Value<bool>() : value.Value<float>() != 0f; break;
            }

            Touch(c.Ctrl, "parameter default");
            c.Ctrl.parameters = ps;
        }

        static void SetParameter(EditContext c, JObject a)
        {
            var name = Required(a, "name");
            SetParameterDefault(c, name, Arg(a, ArgDefault) ?? throw new ArgumentException("'default' is required."));
            c.Changes.Add($"parameter {name} default -> {Arg(a, ArgDefault)}");
        }

        static void RemoveParameter(EditContext c, JObject a)
        {
            var name = Required(a, "name");
            var i = Array.FindIndex(c.Ctrl.parameters, p => p.name == name);
            if (i < 0) throw new ArgumentException($"No parameter '{name}'.");
            var users = new List<string>();
            foreach (var layer in c.Ctrl.layers)
            {
                var states = new List<(string, AnimatorState)>();
                var transitions = new List<(string label, AnimatorTransitionBase t)>();
                if (layer.stateMachine) Walk(layer.stateMachine, "", states, transitions);
                users.AddRange(transitions.Where(x => x.t.conditions.Any(cd => cd.parameter == name)).Select(x => $"{layer.name}: {x.label} {TransitionString(x.t)}"));
            }

            if (users.Count > 0 && !Bool(a, "force", false))
                throw new ArgumentException($"'{name}' is still used by {users.Count} transition(s): {string.Join("; ", users.Take(5))}. Pass force: true to remove it anyway.");
            Touch(c.Ctrl, "remove parameter");
            c.Ctrl.RemoveParameter(i);
            c.Changes.Add($"removed parameter {name}" + (users.Count > 0 ? $" (still referenced by {users.Count} transition(s))" : ""));
        }

        static void SetLayer(EditContext c, JObject a)
        {
            var layers = c.Ctrl.layers;
            var l = layers[c.LayerIndex];
            if (Arg(a, "weight") is JToken w) l.defaultWeight = w.Value<float>();
            if (Str(a, "blending") is string b) l.blendingMode = (AnimatorLayerBlendingMode)Enum.Parse(typeof(AnimatorLayerBlendingMode), b, true);
            if (a.TryGetValue("mask", StringComparison.OrdinalIgnoreCase, out var m))
                l.avatarMask = m.Type == JTokenType.Null || m.ToString().Length == 0 ? null
                    : AssetDatabase.LoadAssetAtPath<AvatarMask>(m.ToString()) ?? throw new ArgumentException($"No AvatarMask at {m}.");
            Touch(c.Ctrl, "layer");
            c.Ctrl.layers = layers;
            c.Changes.Add($"layer {l.name}: weight={R(l.defaultWeight)}, blending={l.blendingMode}, mask={(l.avatarMask ? l.avatarMask.name : "none")}");
        }

        // ---------- entry ----------

        static JToken EditAnimator(JObject a)
        {
            var action = (Required(a, "action")).ToLowerInvariant();
            var ctrl = ControllerForEdit(a);
            var assetPath = AssetDatabase.GetAssetPath(ctrl);
            var generated = GeneratedReason(assetPath);
            if (generated != null && !Bool(a, "force", false))
                throw new ArgumentException($"Not editing {assetPath}: {generated}. Edit the source controller instead (or pass force: true).");

            var c = new EditContext { Ctrl = ctrl };
            var layerName = Str(a, "layer");
            var needsLayer = action != "add_parameter" && action != "remove_parameter" && action != "set_parameter" && action != "set_write_defaults";
            if (layerName != null || needsLayer)
            {
                c.Layer = FindLayer(ctrl, layerName ?? throw new ArgumentException($"'{action}' needs 'layer'."), out c.LayerIndex);
                c.States = new List<(string, AnimatorState)>();
                if (c.Layer.stateMachine) Walk(c.Layer.stateMachine, "", c.States, new List<(string, AnimatorTransitionBase)>());
            }

            Undo.SetCurrentGroupName("edit_animator " + action);
            var group = Undo.GetCurrentGroup();
            switch (action)
            {
                case "set_transition": SetTransition(c, a); break;
                case "add_transition": AddTransition(c, a); break;
                case "remove_transition": RemoveTransition(c, a); break;
                case "set_state": SetState(c, a); break;
                case "add_state": AddState(c, a); break;
                case "remove_state": RemoveState(c, a); break;
                case "set_write_defaults": SetWriteDefaults(c, a); break;
                case "add_parameter": AddParameter(c, a); break;
                case "set_parameter": SetParameter(c, a); break;
                case "remove_parameter": RemoveParameter(c, a); break;
                case "set_layer": SetLayer(c, a); break;
                default:
                    throw new ArgumentException("action must be set_transition, add_transition, remove_transition, set_state, add_state, remove_state, " +
                                                "set_write_defaults, add_parameter, set_parameter, remove_parameter or set_layer.");
            }

            Undo.CollapseUndoOperations(group);
            EditorUtility.SetDirty(ctrl);
            var saved = Bool(a, "save", true);
            if (saved) AssetDatabase.SaveAssetIfDirty(ctrl);
            var res = new JObject
            {
                ["controller"] = ObjRef(ctrl),
                ["changes"] = new JArray(c.Changes),
                ["saved"] = saved,
                ["undo"] = "Ctrl+Z in Unity (while the editor session lasts), or revert the .controller file in git.",
            };
            if (Application.isPlaying)
                res["note"] = "Play mode runs a copy built at play start (VRCFury/Av3Emulator): restart play mode to test this change.";
            return res;
        }
    }
}
