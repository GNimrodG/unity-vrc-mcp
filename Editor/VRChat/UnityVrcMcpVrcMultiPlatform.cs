// Multi-platform uploads (Windows / Android / iOS) through the SDK's own multi-platform build chain.
// The chain switches Unity's build target for each platform, which reloads scripts; the SDK Control Panel resumes the
// chain after each reload from its SessionState. A small record in SessionState lets the MCP job re-attach under the same
// id after every reload, and at the end the VRChat API is asked which platforms really got a new bundle.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor.Api;
using T = UnityVrcMcp.Editor.UnityVrcMcpTools;

namespace UnityVrcMcp.Editor.VRChat
{
    internal static partial class UnityVrcMcpVrcSdk
    {
        const string MpbRecordKey = "UnityVrcMcp.VRChat.MultiPlatformUpload";
        const string MpbJobKind = "vrc_upload_multi";
        const string RecordId = "id";
        const string RecordSeen = "seenMpb";
        const string RecordReturned = "callReturned";
        const string RecordReloaded = "reloaded";
        const string KeyPlatforms = "platforms";

        static readonly BuildTarget[] AllTargets = { BuildTarget.StandaloneWindows64, BuildTarget.Android, BuildTarget.iOS };

        static readonly Dictionary<string, BuildTarget> PlatformNames = new Dictionary<string, BuildTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = BuildTarget.StandaloneWindows64, ["pc"] = BuildTarget.StandaloneWindows64,
            ["standalonewindows"] = BuildTarget.StandaloneWindows64, ["standalonewindows64"] = BuildTarget.StandaloneWindows64,
            ["android"] = BuildTarget.Android, ["quest"] = BuildTarget.Android,
            ["ios"] = BuildTarget.iOS,
        };

        static string ApiPlatform(BuildTarget t) =>
            t == BuildTarget.Android ? "android" : t == BuildTarget.iOS ? "ios" : "standalonewindows";

        static bool Supported(BuildTarget t) => BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(t), t);

        static List<BuildTarget> ParsePlatforms(JArray names)
        {
            if (names == null || names.Count == 0) return null;
            var list = new List<BuildTarget>();
            foreach (var n in names.Select(x => x.ToString().Trim()))
            {
                if (n.Equals("all", StringComparison.OrdinalIgnoreCase)) list.AddRange(AllTargets);
                else if (PlatformNames.TryGetValue(n, out var t)) list.Add(t);
                else throw new ArgumentException($"Unknown platform '{n}'. Use windows, android, ios or all.");
            }

            return list.Distinct().ToList();
        }

        // ---------- the SDK's multi-platform state (internal class VRC.SDKBase.VRCMultiPlatformBuild) ----------

        static Type MpbType => typeof(VRC_EditorTools).Assembly.GetType("VRC.SDKBase.VRCMultiPlatformBuild");
        static object MpbGet(string property) => MpbType?.GetProperty(property, AnyStatic)?.GetValue(null);
        static bool MpbActive => MpbGet("MPB") is bool b && b;

        static JObject MpbSnapshot()
        {
            if (!MpbActive) return null;
            var list = (MpbGet("MPBPlatformsList") as IEnumerable)?.Cast<BuildTarget>().ToList() ?? new List<BuildTarget>();
            return new JObject
            {
                ["state"] = MpbGet("MPBState")?.ToString(),
                [KeyPlatforms] = new JArray(list.Select(ApiPlatform)),
                ["built"] = MpbGet("MPBBuiltCount") is int n ? n : 0,
                ["currentTarget"] = ApiPlatform(EditorUserBuildSettings.activeBuildTarget),
                ["next"] = MpbGet("MPBNextPlatform") is BuildTarget next && next != BuildTarget.NoTarget ? ApiPlatform(next) : null,
            };
        }

        // ---------- record persisted across domain reloads ----------

        static JObject LoadRecord()
        {
            var s = SessionState.GetString(MpbRecordKey, "");
            if (string.IsNullOrEmpty(s)) return null;
            try { return JObject.Parse(s); }
            catch { return null; }
        }

        static void SaveRecord(JObject rec) => SessionState.SetString(MpbRecordKey, rec.ToString(Newtonsoft.Json.Formatting.None));
        static void ClearRecord() => SessionState.EraseString(MpbRecordKey);

        static void MarkRecord(string id, string flag)
        {
            var rec = LoadRecord();
            if (rec == null || (string)rec[RecordId] != id || rec[flag]?.Value<bool>() == true) return;
            rec[flag] = true;
            SaveRecord(rec);
        }

        /// <summary>Called on every domain load: re-attach a multi-platform job that was running before the reload.</summary>
        static void RestoreMultiPlatformJob()
        {
            var rec = LoadRecord();
            if (rec == null) return;
            if (DateTime.TryParse((string)rec["startedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started) &&
                DateTime.UtcNow - started > TimeSpan.FromHours(12))
            {
                ClearRecord();
                return;
            }

            rec[RecordReloaded] = true;
            SaveRecord(rec);
            var job = McpJobs.Adopt((string)rec[RecordId], MpbJobKind, rec["started"]?.Value<double>() ?? EditorApplication.timeSinceStartup, PollMultiPlatform);
            job.OnCancel = () => CancelMultiPlatform(job);
            job.Log("re-attached after a script reload (build target now " + ApiPlatform(EditorUserBuildSettings.activeBuildTarget) + ")");
        }

        // ---------- plan / start ----------

        static void AddPlatformPlan(JObject plan, GameObject go, List<BuildTarget> targets)
        {
            var overrides = PerPlatformOverrides.GetPlatformOverrides(go) ?? new List<PerPlatformOverrides.Option>();
            var rows = new JArray();
            foreach (var t in targets)
            {
                var o = overrides.FirstOrDefault(x => x.platform == t);
                var built = o.avatar ? o.avatar.gameObject : go;
                var row = $"{ApiPlatform(t)}: builds {T.GetPath(built.transform)}";
                if (built != go) row += " (per-platform override)";
                if (!Supported(t)) row += " - Unity build support NOT installed, can't upload";
                rows.Add(row);
            }

            var current = EditorUserBuildSettings.activeBuildTarget;
            plan[KeyPlatforms] = rows;
            plan["platformSwitching"] =
                $"Unity is on {ApiPlatform(current)}. For each other platform the SDK switches Unity's build target (scripts reload; Android/iOS " +
                $"may re-import assets, which can take a long time) and switches back to {ApiPlatform(current)} at the end. " +
                "Keep the VRChat SDK Control Panel open and don't change the build target meanwhile.";
        }

        static JObject StartMultiPlatform(IVRCSdkAvatarBuilderApi builder, GameObject go, List<BuildTarget> targets, UploadPrep prep, string thumbPath)
        {
            // The SDK Control Panel resumes the chain after each reload using these (they are what its own UI saves).
            AvatarBuilderSessionState.AvatarName = prep.Avatar.Name ?? "";
            AvatarBuilderSessionState.AvatarDesc = prep.Avatar.Description ?? "";
            AvatarBuilderSessionState.AvatarTags = string.Join("|", prep.Avatar.Tags ?? new List<string>());
            AvatarBuilderSessionState.AvatarReleaseStatus = prep.Avatar.ReleaseStatus ?? "private";
            AvatarBuilderSessionState.AvatarThumbPath = thumbPath ?? "";
            AvatarBuilderSessionState.AvatarPlatforms = new List<BuildTarget>(targets);
            builder.SelectAvatar(go);
            builder.GetType().GetField("_selectedBuildTargets", AnyInstance)?.SetValue(builder, new List<BuildTarget>(targets));

            var id = $"{MpbJobKind}-{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var started = EditorApplication.timeSinceStartup;
            SaveRecord(new JObject
            {
                [RecordId] = id,
                ["avatarPath"] = T.GetPath(go.transform),
                ["avatarId"] = prep.Id,
                [KeyPlatforms] = new JArray(targets.Select(ApiPlatform)),
                ["initialTarget"] = ApiPlatform(EditorUserBuildSettings.activeBuildTarget),
                ["startedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["started"] = started,
            });

            var job = McpJobs.Adopt(id, MpbJobKind, started, PollMultiPlatform);
            job.OnCancel = () => CancelMultiPlatform(job);
            job.Stage = "starting";
            _ = RunMultiPlatform(builder, go, prep.Avatar, thumbPath, job);
            return new JObject
            {
                ["followJob"] = id,
                ["plan"] = prep.Plan,
                ["note"] = $"Multi-platform upload started. Follow it with job_status {{id: \"{id}\", wait: 3600}}; the id stays valid across Unity's script reloads.",
            };
        }

        static async Task RunMultiPlatform(IVRCSdkAvatarBuilderApi builder, GameObject go, VRCAvatar avatar, string thumbPath, McpJob job)
        {
            try
            {
                using (new BuilderEvents(builder, job))
                    await builder.BuildAndUploadMultiPlatform(go, PerPlatformOverrides.GetPlatformOverrides(go), avatar, thumbPath, job.Token);
                job.Log("SDK call returned" + (MpbActive ? " (switching platform)" : ""));
                MarkRecord(job.Id, RecordReturned);
            }
            catch (Exception e)
            {
                var inner = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
                job.Error = $"{inner.GetType().Name}: {inner.Message}";
                job.ErrorDetails = DescribeError(inner);
                job.Finish(inner is OperationCanceledException ? McpJob.Cancelled : McpJob.Failed);
                ClearRecord();
            }
        }

        // ---------- follow / finish ----------

        static void PollMultiPlatform(McpJob job)
        {
            var rec = LoadRecord();
            if (rec == null || (string)rec[RecordId] != job.Id)
            {
                if (job.Stage != "verifying")
                {
                    job.Error = "The multi-platform upload record is gone (finished elsewhere or cleared).";
                    job.Finish(McpJob.Failed);
                }

                return;
            }

            var snap = MpbSnapshot();
            if (snap != null)
            {
                MarkRecord(job.Id, RecordSeen);
                var count = ((JArray)snap[KeyPlatforms]).Count;
                var built = snap["built"].Value<int>();
                job.Stage = $"{snap["state"]}: on {snap["currentTarget"]}" + (snap["next"]?.Type == JTokenType.String ? $", next {snap["next"]}" : "") +
                            $" ({Math.Min(built + 1, count)}/{count})";
                job.Progress = count > 0 ? (double)built / count : (double?)null;
                return;
            }

            var mayConclude = rec[RecordSeen]?.Value<bool>() == true || rec[RecordReturned]?.Value<bool>() == true || rec[RecordReloaded]?.Value<bool>() == true;
            if (!mayConclude || job.Stage == "verifying") return;
            job.Stage = "verifying";
            _ = VerifyMultiPlatform(job, rec);
        }

        static async Task VerifyMultiPlatform(McpJob job, JObject rec)
        {
            try
            {
                var go = T.FindGameObject((string)rec["avatarPath"]);
                var id = BlueprintId(go) ?? (string)rec["avatarId"]
                         ?? throw new InvalidOperationException("The avatar still has no blueprint id, so the first upload failed. See console_logs.");
                var started = DateTime.Parse((string)rec["startedUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddMinutes(-2);
                var av = await VRCApi.GetAvatar(id, true, job.Token);
                var results = new JObject();
                var allOk = true;
                foreach (var p in rec[KeyPlatforms].Select(x => x.ToString()))
                {
                    var latest = av.UnityPackages?.Where(u => u.Platform == p).OrderByDescending(u => u.CreatedAt).Cast<VRCUnityPackage?>().FirstOrDefault();
                    var ok = latest.HasValue && latest.Value.CreatedAt.ToUniversalTime() >= started;
                    allOk &= ok;
                    results[p] = ok ? $"uploaded {latest.Value.CreatedAt:u} (Unity {latest.Value.UnityVersion}, asset v{latest.Value.AssetVersion})" : "NOT uploaded in this run";
                }

                job.Result = new JObject
                {
                    [KeyPlatforms] = results,
                    ["buildTargetNow"] = ApiPlatform(EditorUserBuildSettings.activeBuildTarget),
                    ["avatar"] = DescribeAvatar(av),
                };
                if (allOk) job.Finish(McpJob.Done);
                else
                {
                    job.Error = "Not every platform got a new bundle; console_logs shows the SDK's error.";
                    job.Finish(McpJob.Failed);
                }
            }
            catch (Exception e)
            {
                job.Error = $"{e.GetType().Name}: {e.Message}";
                job.Finish(McpJob.Failed);
            }
            finally
            {
                ClearRecord();
            }
        }

        static void CancelMultiPlatform(McpJob job)
        {
            if (VRCSdkControlPanel.window != null && VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var b) && b != null)
            {
                try { b.CancelUpload(); }
                catch (Exception e) { job.Log("cancel: " + e.Message); }
            }

            MpbType?.GetMethod("ClearMPBState", AnyStatic)?.Invoke(null, null);
            ClearRecord();
            job.Finish(McpJob.Cancelled);
        }
    }
}
