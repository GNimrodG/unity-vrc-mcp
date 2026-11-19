// VRChat avatar / animator specific tools for the Unity VRC MCP bridge.
// The VRChat SDK is accessed by reflection so this assembly compiles without it.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityVrcMcp.Editor
{
    internal static partial class UnityVrcMcpTools
    {
        const string DescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";

        // ---------- avatar_report ----------

        static JToken AvatarReport(JObject a)
        {
            var descType = FindType(DescriptorType) ?? throw new InvalidOperationException("VRChat Avatars SDK (VRCAvatarDescriptor) not found in this project.");
            var path = Str(a, "path");
            IEnumerable<Component> descs;
            if (path != null)
            {
                var c = FindGameObject(path).GetComponent(descType);
                descs = c ? new[] { c } : throw new ArgumentException($"No VRCAvatarDescriptor on {path}.");
            }
            else descs = AllRoots().SelectMany(r => r.GetComponentsInChildren(descType, true));

            var list = descs.ToList();
            if (list.Count == 0) return "No VRCAvatarDescriptor found in the open scenes.";
            var sections = ReportSections(a);

            // Several avatars: one short line each, details only for one avatar at a time.
            if (path == null && list.Count > 1 && !Bool(a, "all", false))
                return new JObject
                {
                    ["avatars"] = new JArray(list.Select(d => SceneSummary(ReportAvatar(d, false)))),
                    ["note"] = "Pass path for one avatar's report (sections for more detail), or all: true for every avatar's report.",
                };

            var reports = list.Select(d =>
            {
                var r = CompactReport(ReportAvatar(d, sections.Contains("parameters")), sections);
                if (Application.isPlaying) AddBuildOutputs(r, d);
                return (JToken)r;
            }).ToList();
            return reports.Count == 1 ? reports[0] : new JArray(reports);
        }

        /// <summary>Play mode: which build hooks (VRCFury, d4rkAvatarOptimizer) made the controllers the avatar runs.</summary>
        static void AddBuildOutputs(JObject report, Component desc)
        {
            var ctrls = new List<(string, Object)>();
            foreach (var field in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                if (Get(desc, field) is IEnumerable layers)
                    foreach (var l in layers)
                        ctrls.Add((Get(l, "type")?.ToString(), Get(l, "animatorController") as Object));
            var outputs = BuildOutputs(ctrls.Where(c => c.Item1 != null));
            if (outputs.Count == 0) return;
            report["playModeControllers"] = outputs;
            if (BuildNote(outputs) is string note) report["buildNote"] = note;
        }

        static readonly string[] AllSections = { "parameters", "components", "vrcfury", "meshes", "materials" };

        static HashSet<string> ReportSections(JObject a)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Arg(a, "sections") is JArray arr)
                foreach (var s in arr.Select(x => x.ToString()))
                {
                    if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) set.UnionWith(AllSections);
                    else if (AllSections.Contains(s, StringComparer.OrdinalIgnoreCase)) set.Add(s);
                    else throw new ArgumentException($"Unknown section '{s}'. Use {string.Join(", ", AllSections)} or all.");
                }

            if (Bool(a, "listParameters", false)) set.Add("parameters"); // 1.x compatibility
            return set;
        }

        static JObject SceneSummary(JObject full)
        {
            var stats = (JObject)full["stats"];
            var issues = (JArray)full["issues"];
            var o = new JObject
            {
                ["avatar"] = full["avatar"],
                ["active"] = full["activeInHierarchy"],
                ["summary"] = $"{stats["activeTriangles"]} tris, {stats["materialSlots"]} material slots, {stats["physBones"]} PhysBones, " +
                              $"{full["expressionParameters"]?["syncedBits"] ?? 0}/256 synced bits",
                ["issueCount"] = issues.Count,
            };
            if (issues.Count > 0) o["issues"] = new JArray(issues.Take(3).Concat(issues.Count > 3 ? new JToken[] { $"... {issues.Count - 3} more" } : Array.Empty<JToken>()));
            return o;
        }

        /// <summary>Turns the full report into the default compact one; <paramref name="sections"/> keep their full detail.</summary>
        static JObject CompactReport(JObject full, HashSet<string> sections)
        {
            var r = new JObject();
            foreach (var key in new[] { "avatar", "activeInHierarchy", "viewPosition", "lipSync", "visemeMesh", "enableEyeLook" })
                if (full[key] != null && full[key].Type != JTokenType.Null) r[key] = full[key];

            var layers = ((JArray)full["baseAnimationLayers"]).Concat((JArray)full["specialAnimationLayers"]).ToList();
            r["playableLayers"] = new JArray(layers.Where(l => l["controller"] != null || l["isDefault"]?.Value<bool>() == false)
                .Select(l => $"{l["type"]}: {l["controller"] ?? "custom, no controller"}" + (l["mask"] != null ? $" (mask {l["mask"]})" : "")));
            var defaults = layers.Where(l => l["controller"] == null && l["isDefault"]?.Value<bool>() != false).Select(l => l["type"].ToString()).ToList();
            if (defaults.Count > 0) r["sdkDefaultLayers"] = string.Join(", ", defaults);

            if (full["expressionsMenu"] is JObject menu) r["expressionsMenu"] = $"{menu["asset"]} ({menu["controls"]} controls)";
            if (full["expressionParameters"] is JObject prm)
            {
                var p = new JObject { ["asset"] = prm["asset"], ["count"] = prm["count"], ["syncedBits"] = $"{prm["syncedBits"]}/256" };
                if (prm["duplicates"] != null) p["duplicates"] = prm["duplicates"];
                if (sections.Contains("parameters") && prm["parameters"] != null) p["parameters"] = prm["parameters"];
                r["expressionParameters"] = p;
            }

            if (full["animator"] is JObject anim) r["animator"] = $"avatar {anim["avatar"] ?? "none"}, humanoid {anim["isHuman"]}, controller {anim["controller"] ?? "none"}";
            r["stats"] = full["stats"];

            if (sections.Contains("components")) r["componentCounts"] = full["componentCounts"];
            if (full["missingScripts"] is JArray missing)
                r["missingScripts"] = sections.Contains("components") || missing.Count <= 10 ? missing : new JArray(missing.Take(10).Concat(new JToken[] { $"... {missing.Count - 10} more" }));

            if (full["vrcFury"] is JArray vf)
            {
                if (sections.Contains("vrcfury")) r["vrcFury"] = vf;
                else
                {
                    // "path: Feature" -> counts per feature type
                    var byType = vf.Select(x => x.ToString()).Select(s => s.Substring(s.LastIndexOf(": ", StringComparison.Ordinal) + 2))
                        .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}");
                    r["vrcFury"] = $"{vf.Count} components: {string.Join(", ", byType)}";
                }
            }

            var smrs = (JArray)full["skinnedMeshes"];
            var mrs = full["meshRenderers"] as JArray ?? new JArray();
            if (sections.Contains("meshes"))
            {
                r["skinnedMeshes"] = smrs;
                if (mrs.Count > 0) r["meshRenderers"] = mrs;
            }
            else
            {
                r["skinnedMeshes"] = new JArray(smrs.Select(m =>
                    $"{m["path"]}: {m["triangles"] ?? 0} tris, {m["blendShapes"] ?? 0} shapes, {((JArray)m["materials"]).Count} mats" +
                    (m["visible"]?.Value<bool>() == false ? " (hidden)" : "")));
                if (mrs.Count > 0)
                    r["meshRenderers"] = $"{mrs.Count} ({mrs.Count(m => m["visible"]?.Value<bool>() != false)} visible)";
            }

            if (sections.Contains("materials")) r["materials"] = full["uniqueMaterialList"];
            r["issues"] = full["issues"];
            var missingSections = AllSections.Where(s => !sections.Contains(s)).ToList();
            r["note"] = "Pre-build state (VRCFury and optimizers change it at build/play time)." +
                        (missingSections.Count > 0 ? $" More detail: sections [{string.Join(", ", missingSections.Select(s => $"\"{s}\""))}] or [\"all\"]." : "");
            return r;
        }

        static JArray DescribeLayers(object layers)
        {
            var res = new JArray();
            if (!(layers is IEnumerable e)) return res;
            foreach (var l in e)
            {
                var ctrl = Get(l, "animatorController") as Object;
                var mask = Get(l, "mask") as Object;
                var o = new JObject { ["type"] = Get(l, "type")?.ToString(), ["isDefault"] = Get(l, "isDefault") as bool? };
                if (Get(l, "isEnabled") is bool en && !en) o["isEnabled"] = false;
                if (ctrl) o["controller"] = ObjRef(ctrl);
                if (mask) o["mask"] = ObjRef(mask);
                res.Add(o);
            }

            return res;
        }

        static JObject DescribeParameters(Object prms, bool list)
        {
            var o = new JObject { ["asset"] = ObjRef(prms) };
            var ps = Get(prms, "parameters") as IEnumerable;
            var lines = new JArray();
            int bits = 0, count = 0;
            var names = new HashSet<string>();
            var dupes = new JArray();
            if (ps != null)
                foreach (var p in ps)
                {
                    var name = Get(p, "name") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    count++;
                    if (!names.Add(name)) dupes.Add(name);
                    var vt = Get(p, "valueType")?.ToString() ?? "?";
                    var synced = !(Get(p, "networkSynced") is bool ns) || ns;
                    var saved = Get(p, "saved") is bool sv && sv;
                    if (synced) bits += vt == "Bool" ? 1 : 8;
                    lines.Add($"{name}: {vt} = {Get(p, "defaultValue")}{(saved ? " saved" : "")}{(synced ? "" : " local-only")}");
                }

            o["count"] = count;
            o["syncedBits"] = bits;
            o["maxBits"] = 256;
            if (dupes.Count > 0) o["duplicates"] = dupes;
            if (list) o["parameters"] = lines;
            return o;
        }

        /// <summary>True when t and its parents up to (not including) root are activeSelf: active when the avatar is worn.</summary>
        static bool ActiveWithin(Transform t, Transform root)
        {
            for (var x = t; x != null && x != root; x = x.parent)
                if (!x.gameObject.activeSelf) return false;
            return true;
        }

        static long TriangleCount(Mesh m)
        {
            long n = 0;
            for (var i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i) / 3;
            return n;
        }

        static string MaterialLabel(Material m) => m ? $"{m.name} [{(m.shader ? m.shader.name : "no shader")}]" : "<None>";

        static JObject ReportAvatar(Component d, bool listParams)
        {
            var go = d.gameObject;
            var issues = new JArray();
            var r = new JObject { ["avatar"] = GetPath(go.transform), ["activeInHierarchy"] = go.activeInHierarchy };

            if (Get(d, "ViewPosition") is Vector3 vp)
            {
                r["viewPosition"] = V3(vp);
                if (vp == Vector3.zero) issues.Add("View position is (0,0,0).");
            }

            r["lipSync"] = Get(d, "lipSync")?.ToString();
            if (Get(d, "VisemeSkinnedMesh") is Object vm && vm) r["visemeMesh"] = ObjRef(vm);
            r["enableEyeLook"] = Get(d, "enableEyeLook") as bool?;
            r["customizeAnimationLayers"] = Get(d, "customizeAnimationLayers") as bool?;
            r["baseAnimationLayers"] = DescribeLayers(Get(d, "baseAnimationLayers"));
            r["specialAnimationLayers"] = DescribeLayers(Get(d, "specialAnimationLayers"));
            foreach (var l in ((JArray)r["baseAnimationLayers"]).Concat((JArray)r["specialAnimationLayers"]))
                if (l["isDefault"]?.Type == JTokenType.Boolean && !(bool)l["isDefault"] && l["controller"] == null)
                    issues.Add($"Layer {l["type"]} is set to custom but has no controller.");

            var menu = Get(d, "expressionsMenu") as Object;
            var prms = Get(d, "expressionParameters") as Object;
            r["customExpressions"] = Get(d, "customExpressions") as bool?;
            r["expressionsMenu"] = menu ? new JObject { ["asset"] = ObjRef(menu), ["controls"] = (Get(menu, "controls") as IList)?.Count } : null;
            if (prms)
            {
                var pr = DescribeParameters(prms, listParams);
                r["expressionParameters"] = pr;
                if (pr.Value<int>("syncedBits") > 256) issues.Add($"Synced parameters use {pr["syncedBits"]} bits (> 256).");
            }

            var anim = go.GetComponent<Animator>();
            if (anim)
            {
                r["animator"] = new JObject
                {
                    ["avatar"] = ObjRef(anim.avatar), ["isHuman"] = anim.avatar ? anim.avatar.isHuman : (bool?)null,
                    ["isValid"] = anim.avatar ? anim.avatar.isValid : (bool?)null, ["controller"] = ObjRef(anim.runtimeAnimatorController),
                };
                if (!anim.avatar) issues.Add("Root Animator has no Avatar (humanoid rig) assigned.");
            }
            else issues.Add("No Animator on the avatar root.");

            // Components, missing scripts
            var counts = new SortedDictionary<string, int>();
            var missing = new JArray();
            var vrcFury = new JArray();
            foreach (var (t, c) in go.GetComponentsInChildren<Transform>(true).SelectMany(x => x.GetComponents<Component>().Select(y => (x, y))))
            {
                if (c == null)
                {
                    if (missing.Count < 30) missing.Add(GetPath(t));
                    continue;
                }

                var tn = c.GetType().Name;
                counts.TryGetValue(tn, out var n);
                counts[tn] = n + 1;
                if (tn == "VRCFury" && vrcFury.Count < 100)
                {
                    var content = Get(c, "content");
                    var features = content != null
                        ? content.GetType().Name
                        : Get(Get(c, "config"), "features") is IEnumerable fs
                            ? string.Join(", ", fs.Cast<object>().Where(x => x != null).Select(x => x.GetType().Name))
                            : "?";
                    vrcFury.Add($"{GetPath(t)}: {features}");
                }
            }

            r["componentCounts"] = JObject.FromObject(counts);
            if (missing.Count > 0)
            {
                r["missingScripts"] = missing;
                issues.Add($"{missing.Count} GameObject(s) have missing scripts.");
            }

            if (vrcFury.Count > 0) r["vrcFury"] = vrcFury;

            // Renderers / meshes / materials
            long tris = 0;
            var materialSlots = 0;
            var uniqueMats = new HashSet<Material>();
            var smrs = new JArray();
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                var o = new JObject { ["path"] = GetPath(smr.transform) };
                if (!ActiveWithin(smr.transform, go.transform) || !smr.enabled) o["visible"] = false;
                materialSlots += smr.sharedMaterials.Length;
                foreach (var m in smr.sharedMaterials) if (m) uniqueMats.Add(m);
                o["materials"] = new JArray(smr.sharedMaterials.Select(MaterialLabel));
                if (!mesh)
                {
                    issues.Add($"{GetPath(smr.transform)}: SkinnedMeshRenderer has no mesh.");
                    smrs.Add(o);
                    continue;
                }

                var mt = TriangleCount(mesh);
                if (ActiveWithin(smr.transform, go.transform)) tris += mt;
                o["mesh"] = ObjRef(mesh);
                o["vertices"] = mesh.vertexCount;
                o["triangles"] = mt;
                o["subMeshes"] = mesh.subMeshCount;
                o["blendShapes"] = mesh.blendShapeCount;
                var nonZero = Enumerable.Range(0, mesh.blendShapeCount).Count(i => smr.GetBlendShapeWeight(i) > 0.001f);
                if (nonZero > 0) o["nonZeroBlendShapes"] = nonZero;
                var bones = smr.bones;
                o["bones"] = bones.Length;
                var nullBones = bones.Count(b => b == null);
                if (nullBones > 0) issues.Add($"{GetPath(smr.transform)}: {nullBones} missing bone reference(s).");
                var outside = bones.Count(b => b && !b.IsChildOf(go.transform));
                if (outside > 0) issues.Add($"{GetPath(smr.transform)}: {outside} bone(s) outside the avatar hierarchy.");
                o["rootBone"] = smr.rootBone ? GetPath(smr.rootBone) : null;
                if (smr.sharedMaterials.Length > mesh.subMeshCount)
                    issues.Add($"{GetPath(smr.transform)}: {smr.sharedMaterials.Length} material slots for {mesh.subMeshCount} submeshes (extra slots draw the mesh again).");
                smrs.Add(o);
            }

            var mrs = new JArray();
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf ? mf.sharedMesh : null;
                materialSlots += mr.sharedMaterials.Length;
                foreach (var m in mr.sharedMaterials) if (m) uniqueMats.Add(m);
                if (mesh && ActiveWithin(mr.transform, go.transform)) tris += TriangleCount(mesh);
                var o = new JObject { ["path"] = GetPath(mr.transform), ["mesh"] = ObjRef(mesh), ["materials"] = new JArray(mr.sharedMaterials.Select(MaterialLabel)) };
                if (!ActiveWithin(mr.transform, go.transform) || !mr.enabled) o["visible"] = false;
                mrs.Add(o);
            }

            foreach (var m in uniqueMats)
            {
                if (!m.shader || m.shader.name == "Hidden/InternalErrorShader")
                    issues.Add($"Material {AssetDatabase.GetAssetPath(m)} has a missing/error shader.");
                else if (m.shader.name.StartsWith("Hidden/Locked/") && !AssetDatabase.Contains(m.shader))
                    issues.Add($"Material {m.name} uses a locked shader that isn't in the AssetDatabase.");
            }

            r["skinnedMeshes"] = smrs;
            if (mrs.Count > 0) r["meshRenderers"] = mrs;
            r["stats"] = new JObject
            {
                ["activeTriangles"] = tris, ["materialSlots"] = materialSlots, ["uniqueMaterials"] = uniqueMats.Count,
                ["physBones"] = counts.TryGetValue("VRCPhysBone", out var pb) ? pb : 0,
                ["physBoneColliders"] = counts.TryGetValue("VRCPhysBoneCollider", out var pbc) ? pbc : 0,
                ["contactReceivers"] = counts.TryGetValue("VRCContactReceiver", out var cr) ? cr : 0,
                ["contactSenders"] = counts.TryGetValue("VRCContactSender", out var cs) ? cs : 0,
            };
            r["uniqueMaterialList"] = new JArray(uniqueMats.Select(m => $"{MaterialLabel(m)} @ {AssetDatabase.GetAssetPath(m)}"));
            r["issues"] = issues;
            r["note"] = "Pre-build state: VRCFury, d4rk optimizer and similar tools change layers, parameters and meshes at build/play time.";
            return r;
        }

        // ---------- inspect_animator ----------

        static string ConditionString(AnimatorCondition c)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If: return $"{c.parameter}";
                case AnimatorConditionMode.IfNot: return $"!{c.parameter}";
                case AnimatorConditionMode.Greater: return $"{c.parameter} > {R(c.threshold)}";
                case AnimatorConditionMode.Less: return $"{c.parameter} < {R(c.threshold)}";
                case AnimatorConditionMode.Equals: return $"{c.parameter} == {R(c.threshold)}";
                case AnimatorConditionMode.NotEqual: return $"{c.parameter} != {R(c.threshold)}";
                default: return $"{c.parameter} {c.mode} {c.threshold}";
            }
        }

        static string TransitionString(AnimatorTransitionBase t)
        {
            var dest = t.isExit ? "Exit" : t.destinationState ? t.destinationState.name : t.destinationStateMachine ? "SM:" + t.destinationStateMachine.name : "?";
            var s = $"-> {dest}";
            if (t.conditions.Length > 0) s += $" [{string.Join(" && ", t.conditions.Select(ConditionString))}]";
            if (t.mute) s += " (muted)";
            if (t.solo) s += " (solo)";
            if (t is AnimatorStateTransition st)
            {
                if (st.canTransitionToSelf && st.destinationState) s += " toSelf";
                if (st.hasExitTime) s += $" exitTime={R(st.exitTime)}";
                if (st.duration > 0) s += $" dur={R(st.duration)}{(st.hasFixedDuration ? "s" : "")}";
                if (st.interruptionSource != TransitionInterruptionSource.None) s += $" interrupt={st.interruptionSource}";
                if (!st.hasExitTime && st.conditions.Length == 0) s += " (NO CONDITIONS, fires immediately)";
            }

            return s;
        }

        static JToken DescribeMotion(Motion m, int depth, HashSet<AnimationClip> clips, HashSet<string> btParams)
        {
            if (!m) return null;
            if (m is AnimationClip c)
            {
                clips.Add(c);
                return c.name;
            }

            if (!(m is BlendTree bt)) return m.name;
            var o = new JObject { ["blendTree"] = bt.name, ["type"] = bt.blendType.ToString() };
            if (bt.blendType != BlendTreeType.Direct)
            {
                o["param"] = bt.blendParameter;
                btParams.Add(bt.blendParameter);
            }

            if (bt.blendType != BlendTreeType.Simple1D && bt.blendType != BlendTreeType.Direct)
            {
                o["paramY"] = bt.blendParameterY;
                btParams.Add(bt.blendParameterY);
            }

            if (depth >= _treeDepth)
            {
                foreach (var child in bt.children) CollectMotion(child.motion, clips, btParams, bt.blendType == BlendTreeType.Direct ? child.directBlendParameter : null);
                o["children"] = $"{bt.children.Length} children (deeper than maxTreeDepth {_treeDepth})";
                return o;
            }

            var ch = new JArray();
            var shown = 0;
            foreach (var child in bt.children)
            {
                if (shown++ >= _treeChildren)
                {
                    CollectMotion(child.motion, clips, btParams, bt.blendType == BlendTreeType.Direct ? child.directBlendParameter : null);
                    continue;
                }

                var co = new JObject();
                if (bt.blendType == BlendTreeType.Direct)
                {
                    co["param"] = child.directBlendParameter;
                    btParams.Add(child.directBlendParameter);
                }
                else if (bt.blendType == BlendTreeType.Simple1D) co["threshold"] = R(child.threshold);
                else co["position"] = V2(child.position);
                co["motion"] = DescribeMotion(child.motion, depth + 1, clips, btParams);
                ch.Add(co);
            }

            if (bt.children.Length > _treeChildren) ch.Add($"... {bt.children.Length - _treeChildren} more children (maxChildren)");
            o["children"] = ch;
            return o;
        }

        [ThreadStatic] static int _treeDepth, _treeChildren;

        /// <summary>Records clips and parameters of a motion that isn't printed (for the checks).</summary>
        static void CollectMotion(Motion m, HashSet<AnimationClip> clips, HashSet<string> btParams, string directParam)
        {
            if (!string.IsNullOrEmpty(directParam)) btParams.Add(directParam);
            if (m is AnimationClip c) clips.Add(c);
            if (!(m is BlendTree bt)) return;
            if (bt.blendType != BlendTreeType.Direct) btParams.Add(bt.blendParameter);
            if (bt.blendType != BlendTreeType.Simple1D && bt.blendType != BlendTreeType.Direct) btParams.Add(bt.blendParameterY);
            foreach (var child in bt.children) CollectMotion(child.motion, clips, btParams, bt.blendType == BlendTreeType.Direct ? child.directBlendParameter : null);
        }

        static void Walk(AnimatorStateMachine sm, string prefix, List<(string path, AnimatorState state)> states,
            List<(string label, AnimatorTransitionBase t)> transitions)
        {
            foreach (var t in sm.anyStateTransitions) transitions.Add(($"{prefix}AnyState", t));
            foreach (var t in sm.entryTransitions) transitions.Add(($"{prefix}Entry", t));
            foreach (var cs in sm.states)
            {
                states.Add((prefix + cs.state.name, cs.state));
                foreach (var t in cs.state.transitions) transitions.Add((prefix + cs.state.name, t));
            }

            foreach (var csm in sm.stateMachines)
            {
                foreach (var t in sm.GetStateMachineTransitions(csm.stateMachine)) transitions.Add(($"{prefix}{csm.stateMachine.name} (exit)", t));
                Walk(csm.stateMachine, prefix + csm.stateMachine.name + "/", states, transitions);
            }
        }

        static AnimatorController ControllerFromDescriptor(GameObject go, string layerType)
        {
            var descType = FindType(DescriptorType) ?? throw new InvalidOperationException("VRChat Avatars SDK not found.");
            var d = go.GetComponent(descType) ?? throw new ArgumentException($"No VRCAvatarDescriptor on {GetPath(go.transform)}.");
            foreach (var field in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                if (Get(d, field) is IEnumerable layers)
                    foreach (var l in layers)
                        if (string.Equals(Get(l, "type")?.ToString(), layerType, StringComparison.OrdinalIgnoreCase))
                            return Get(l, "animatorController") as AnimatorController
                                   ?? throw new ArgumentException($"Avatar layer {layerType} has no custom controller (it uses the SDK default).");
            throw new ArgumentException($"Avatar layer '{layerType}' not found. Use Base, Additive, Gesture, Action, FX, Sitting, TPose or IKPose.");
        }

        static JToken InspectAnimator(JObject a)
        {
            AnimatorController ctrl;
            GameObject go = null;
            Animator live = null;
            var assetPath = Str(a, "assetPath");
            var path = Str(a, "path");
            var avatarLayer = Str(a, "avatarLayer");
            if (assetPath != null)
                ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) ?? throw new ArgumentException($"No AnimatorController at {assetPath}.");
            else if (path != null)
            {
                go = FindGameObject(path);
                if (avatarLayer != null) ctrl = ControllerFromDescriptor(go, avatarLayer);
                else
                {
                    live = go.GetComponent<Animator>() ?? throw new ArgumentException($"No Animator on {path}. Pass avatarLayer (e.g. FX) to read the descriptor's controller.");
                    var rac = live.runtimeAnimatorController;
                    if (rac is AnimatorOverrideController oc) rac = oc.runtimeAnimatorController;
                    ctrl = rac as AnimatorController ?? throw new ArgumentException("That Animator has no AnimatorController asset assigned.");
                }
            }
            else throw new ArgumentException("Pass 'assetPath', or 'path' (+ optional 'avatarLayer').");

            var detail = (Str(a, "detail") ?? "states").ToLowerInvariant();
            var layerFilter = Str(a, "layer");
            _treeDepth = Int(a, "maxTreeDepth", detail == "full" ? 4 : 2);
            _treeChildren = Int(a, "maxChildren", detail == "full" ? 24 : 12);
            var maxStates = Int(a, "maxStates", 120);
            var usedParams = new HashSet<string>(StringComparer.Ordinal);
            var filteredIssues = 0;
            var bindPath = Str(a, "bindingRoot");
            var bindRoot = bindPath != null ? FindGameObject(bindPath) : go;
            var issues = new JArray();

            var paramTypes = ctrl.parameters.ToDictionary(p => p.name, p => p.type);
            var res = new JObject
            {
                ["controller"] = ObjRef(ctrl),
            };

            var allClips = new HashSet<AnimationClip>();
            var btParams = new HashSet<string>();
            int wdOn = 0, wdOff = 0;
            var wdByLayer = new JObject();
            var layersOut = new JArray();
            var layers = ctrl.layers;
            var stateMaps = new List<Dictionary<int, string>>();

            for (var li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                var states = new List<(string path, AnimatorState state)>();
                var transitions = new List<(string label, AnimatorTransitionBase t)>();
                if (layer.stateMachine) Walk(layer.stateMachine, "", states, transitions);
                var map = new Dictionary<int, string>();
                foreach (var s in states) map[Animator.StringToHash(layer.name + "." + s.path.Replace('/', '.'))] = s.path;
                stateMaps.Add(map);

                int on = states.Count(s => s.state.writeDefaultValues), off = states.Count - on;
                wdOn += on;
                wdOff += off;
                if (on > 0 && off > 0) wdByLayer[layer.name] = $"{on} on / {off} off";

                // checks (all layers; with a layer filter only the matching layers' findings are listed)
                var matches = layerFilter == null || layer.name.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                var issuesBefore = issues.Count;
                var btBefore = new HashSet<string>(btParams);
                var layerClips = new HashSet<AnimationClip>();
                var motions = states.Select(s => (s.path, s.state, motion: DescribeMotion(s.state.motion, 0, layerClips, btParams))).ToList();
                if (matches) allClips.UnionWith(layerClips);
                foreach (var (label, t) in transitions)
                foreach (var c in t.conditions)
                {
                    if (!paramTypes.TryGetValue(c.parameter, out var pt))
                    {
                        issues.Add($"[{layer.name}] {label} {TransitionString(t)}: parameter '{c.parameter}' is not defined in the controller.");
                        continue;
                    }

                    var bad = (c.mode == AnimatorConditionMode.If || c.mode == AnimatorConditionMode.IfNot)
                        ? pt == AnimatorControllerParameterType.Int || pt == AnimatorControllerParameterType.Float
                        : (c.mode == AnimatorConditionMode.Equals || c.mode == AnimatorConditionMode.NotEqual)
                            ? pt != AnimatorControllerParameterType.Int
                            : pt == AnimatorControllerParameterType.Bool || pt == AnimatorControllerParameterType.Trigger;
                    if (bad) issues.Add($"[{layer.name}] {label}: condition '{ConditionString(c)}' uses {c.mode} on a {pt} parameter.");
                }

                // AnyState transitions that may re-enter their own target keep restarting it (and re-running its
                // behaviours: parameter drivers, tracking control, ...) for as long as the condition holds.
                foreach (var (label, t) in transitions)
                    if (label.EndsWith("AnyState") && t is AnimatorStateTransition ast && ast.canTransitionToSelf && ast.destinationState)
                        issues.Add($"[{layer.name}] {label} {TransitionString(t)}: 'Can Transition To Self' is on, so the state restarts every time the transition ends while the condition is true" +
                                   (ast.destinationState.behaviours.Length > 0 ? $" (re-runs {string.Join(", ", ast.destinationState.behaviours.Where(b => b).Select(b => b.GetType().Name))})." : "."));

                if (!matches)
                {
                    filteredIssues += issues.Count - issuesBefore;
                    while (issues.Count > issuesBefore) issues.RemoveAt(issues.Count - 1);
                    layersOut.Add($"{li}: {layer.name} ({states.Count} states)");
                    continue;
                }

                foreach (var (_, t) in transitions)
                foreach (var c in t.conditions) usedParams.Add(c.parameter);
                usedParams.UnionWith(btParams.Except(btBefore));
                foreach (var (_, st) in states)
                {
                    if (st.speedParameterActive) usedParams.Add(st.speedParameter);
                    if (st.timeParameterActive) usedParams.Add(st.timeParameter);
                }

                var lo = new JObject
                {
                    ["index"] = li, ["name"] = layer.name, ["weight"] = R(layer.defaultWeight),
                    ["blending"] = layer.blendingMode.ToString(),
                    ["mask"] = ObjRef(layer.avatarMask),
                    ["defaultState"] = layer.stateMachine && layer.stateMachine.defaultState ? layer.stateMachine.defaultState.name : null,
                    ["stateCount"] = states.Count,
                    ["writeDefaults"] = $"{on} on / {off} off",
                };
                if (layer.syncedLayerIndex >= 0) lo["syncedLayer"] = layer.syncedLayerIndex;
                if (li == 0 && layer.defaultWeight < 1f) lo["note"] = "Layer 0 always has weight 1 at runtime.";

                if (detail != "summary")
                {
                    var so = new JArray();
                    foreach (var (spath, st, motion) in motions.Take(maxStates))
                    {
                        var s = new JObject { ["state"] = spath, ["motion"] = motion, ["wd"] = st.writeDefaultValues };
                        if (st.speedParameterActive) s["speedParam"] = st.speedParameter;
                        else if (!Mathf.Approximately(st.speed, 1f)) s["speed"] = R(st.speed);
                        if (st.timeParameterActive) s["motionTime"] = st.timeParameter;
                        if (st.behaviours.Length > 0)
                            s["behaviours"] = detail == "full"
                                ? new JArray(st.behaviours.Select(b => b ? DumpObject(b, new JObject { ["depth"] = 2, ["maxArray"] = 16 }) : "<Missing>"))
                                : new JArray(st.behaviours.Select(b => b ? b.GetType().Name : "<Missing>"));
                        if (detail == "full" && st.transitions.Length > 0) s["transitions"] = new JArray(st.transitions.Select(TransitionString));
                        so.Add(s);
                    }

                    if (motions.Count > maxStates) so.Add($"... {motions.Count - maxStates} more states (maxStates)");
                    lo["states"] = so;
                    if (detail == "full")
                    {
                        var other = transitions.Where(x => x.label.EndsWith("AnyState") || x.label.EndsWith("Entry") || x.label.EndsWith("(exit)"))
                            .Select(x => $"{x.label} {TransitionString(x.t)}").ToList();
                        if (other.Count > 0) lo["otherTransitions"] = new JArray(other);
                    }
                }

                layersOut.Add(lo);
            }

            string ParamLine(AnimatorControllerParameter p) =>
                $"{p.name}: {p.type} = {(p.type == AnimatorControllerParameterType.Float ? R(p.defaultFloat).ToString() : p.type == AnimatorControllerParameterType.Int ? p.defaultInt.ToString() : p.defaultBool.ToString())}";
            if (layerFilter == null) res["parameters"] = new JArray(ctrl.parameters.Select(ParamLine));
            else
            {
                if (!layers.Any(l => l.name.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                    res["warning"] = $"No layer name contains '{layerFilter}'; the layers are listed below.";
                res["parameters"] = new JArray(ctrl.parameters.Where(p => usedParams.Contains(p.name)).Select(ParamLine));
                res["parametersNote"] = $"Only the {ctrl.parameters.Count(p => usedParams.Contains(p.name))} of {ctrl.parameters.Length} parameters the matching layers use.";
                if (filteredIssues > 0) res["otherLayersIssues"] = $"{filteredIssues} issue(s) in layers outside the filter.";
            }

            res["layers"] = layersOut;
            res["writeDefaults"] = $"{wdOn} on / {wdOff} off";
            if (wdOn > 0 && wdOff > 0)
                issues.Add($"Mixed Write Defaults ({wdOn} on / {wdOff} off). Mixed layers: {(wdByLayer.Count > 0 ? string.Join(", ", wdByLayer.Properties().Select(p => p.Name)) : "none (mixed across layers)")}");
            foreach (var p in btParams.Where(p => !string.IsNullOrEmpty(p) && !paramTypes.ContainsKey(p)))
                issues.Add($"Blend tree parameter '{p}' is not defined in the controller.");
            foreach (var p in btParams.Where(p => paramTypes.TryGetValue(p ?? "", out var t) && t != AnimatorControllerParameterType.Float))
                issues.Add($"Blend tree parameter '{p}' is {paramTypes[p]}, blend trees need Float.");

            if (bindRoot && Bool(a, "checkBindings", true))
            {
                var bad = new JArray();
                foreach (var clip in allClips.OrderBy(c => c.name))
                {
                    var missing = MissingBindings(bindRoot, clip);
                    if (missing.Count > 0)
                        bad.Add(new JObject { ["clip"] = ObjRef(clip), ["missing"] = missing.Count, ["examples"] = new JArray(missing.Take(5)) });
                }

                res["bindingCheck"] = new JObject { ["root"] = GetPath(bindRoot.transform), ["clipsChecked"] = allClips.Count, ["clipsWithMissingBindings"] = bad };
            }

            if (live && Application.isPlaying && live.isActiveAndEnabled && live.runtimeAnimatorController)
            {
                var liveO = new JObject();
                foreach (var p in live.parameters)
                    liveO[p.name] = p.type == AnimatorControllerParameterType.Float ? R(live.GetFloat(p.nameHash))
                        : p.type == AnimatorControllerParameterType.Int ? live.GetInteger(p.nameHash)
                        : (JToken)live.GetBool(p.nameHash);
                var cur = new JArray();
                for (var i = 0; i < live.layerCount && i < stateMaps.Count; i++)
                {
                    var info = live.GetCurrentAnimatorStateInfo(i);
                    cur.Add($"{live.GetLayerName(i)}: {(stateMaps[i].TryGetValue(info.fullPathHash, out var n) ? n : "?")} (t={R(info.normalizedTime)}, w={R(live.GetLayerWeight(i))})");
                }

                res["live"] = new JObject { ["parameters"] = liveO, ["currentStates"] = cur };
            }

            res["issues"] = issues;
            return res;
        }

        // ---------- inspect_clip ----------

        static List<string> MissingBindings(GameObject root, AnimationClip clip)
        {
            var res = new List<string>();
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var why = CheckBinding(root, b, false);
                if (why != null) res.Add($"{b.path}:{b.type.Name}.{b.propertyName} ({why})");
            }

            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var why = CheckBinding(root, b, true);
                if (why != null) res.Add($"{b.path}:{b.type.Name}.{b.propertyName} ({why})");
            }

            return res;
        }

        static string CheckBinding(GameObject root, EditorCurveBinding b, bool objRef)
        {
            if (b.type == typeof(Animator)) return null; // humanoid muscles / animator parameters
            var t = string.IsNullOrEmpty(b.path) ? root.transform : root.transform.Find(b.path);
            if (!t) return "path not found";
            if (b.type != typeof(GameObject) && b.type != typeof(Transform) && !t.GetComponent(b.type)) return $"no {b.type.Name} there";

            if (b.propertyName.StartsWith("blendShape."))
            {
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                return smr && smr.sharedMesh && smr.sharedMesh.GetBlendShapeIndex(b.propertyName.Substring(11)) >= 0 ? null : "blendshape not on mesh";
            }

            if (b.propertyName.StartsWith("material."))
            {
                var prop = b.propertyName.Substring(9);
                var dot = prop.LastIndexOf('.');
                if (dot > 0 && prop.Length - dot == 2) prop = prop.Substring(0, dot); // _Color.r -> _Color
                var rend = t.GetComponent(b.type) as Renderer;
                return rend && rend.sharedMaterials.Any(m => m && m.HasProperty(prop)) ? null : "material property not on any material (locked shader?)";
            }

            if (objRef) return AnimationUtility.GetObjectReferenceValue(root, b, out _) ? null : "property not found";
            return AnimationUtility.GetFloatValue(root, b, out _) ? null : "property not found";
        }

        static JToken InspectClip(JObject a)
        {
            var ap = Required(a, "assetPath");
            var clip = LoadAsset(ap, Str(a, "subAsset"), "AnimationClip") as AnimationClip ?? throw new ArgumentException($"No AnimationClip at {ap}.");
            var rootPath = Str(a, "bindingRoot");
            var root = rootPath != null ? FindGameObject(rootPath) : null;
            var max = Int(a, "maxBindings", 200);
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var res = new JObject
            {
                ["clip"] = ObjRef(clip), ["length"] = R(clip.length), ["frameRate"] = clip.frameRate,
                ["loopTime"] = settings.loopTime, ["humanMotion"] = clip.humanMotion, ["legacy"] = clip.legacy,
                ["events"] = AnimationUtility.GetAnimationEvents(clip).Length,
            };

            var list = new JArray();
            var missingCount = 0;
            var curves = AnimationUtility.GetCurveBindings(clip);
            foreach (var b in curves)
            {
                var why = root ? CheckBinding(root, b, false) : null;
                if (why != null) missingCount++;
                if (list.Count >= max) continue;
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                var keys = curve?.keys ?? Array.Empty<Keyframe>();
                var val = keys.Length == 0 ? "no keys" : keys.Length == 1 || Mathf.Approximately(keys[0].value, keys[keys.Length - 1].value) && keys.All(k => Mathf.Approximately(k.value, keys[0].value))
                    ? $"{R(keys[0].value)}"
                    : $"{R(keys[0].value)} .. {R(keys[keys.Length - 1].value)} ({keys.Length} keys)";
                list.Add($"{b.path} : {b.type.Name}.{b.propertyName} = {val}{(why != null ? $"  !! {why}" : "")}");
            }

            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var why = root ? CheckBinding(root, b, true) : null;
                if (why != null) missingCount++;
                if (list.Count >= max) continue;
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                var vals = string.Join(", ", keys.Take(4).Select(k => k.value ? k.value.name : "null"));
                list.Add($"{b.path} : {b.type.Name}.{b.propertyName} = {vals}{(keys.Length > 4 ? $" ... ({keys.Length} keys)" : "")}{(why != null ? $"  !! {why}" : "")}");
            }

            res["bindingCount"] = curves.Length;
            res["bindings"] = list;
            if (root)
            {
                res["bindingRoot"] = GetPath(root.transform);
                res["missingBindings"] = missingCount;
            }

            return res;
        }
    }
}
