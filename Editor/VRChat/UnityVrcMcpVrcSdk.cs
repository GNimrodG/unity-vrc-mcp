// VRChat Avatars SDK tools: SDK/login state, the SDK's own validation, performance ratings, avatar records
// from the VRChat API, local builds, and (only with explicit confirmation) uploads and info updates.
// Login and the copyright agreement stay in the SDK's own UI; these tools never handle credentials.
// Compiled only when com.vrchat.avatars 3.4+ is installed (see the asmdef's versionDefines).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;
using Object = UnityEngine.Object;
using T = UnityVrcMcp.Editor.UnityVrcMcpTools;

namespace UnityVrcMcp.Editor.VRChat
{
    [InitializeOnLoad]
    internal static partial class UnityVrcMcpVrcSdk
    {
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const string KeyAvatar = "avatar";
        const string KeyPlatform = "platform";
        const string KeyReleaseStatus = "releaseStatus";
        const string ControlPanelMenu = "VRChat SDK/Show Control Panel";
        const string NotLoggedIn = "Not logged in to VRChat in the SDK, and logging in with the SDK's saved credentials failed " +
                                   "(none saved, or they expired). Log in in the VRChat SDK Control Panel (VRChat SDK > Show Control Panel); " +
                                   "this tool never handles credentials.";

        static UnityVrcMcpVrcSdk()
        {
            T.RegisterTool("vrc_sdk_status", Status);
            T.RegisterTool("vrc_validate", Validate);
            T.RegisterTool("vrc_performance", Performance);
            T.RegisterTool("vrc_avatar_info", AvatarInfo);
            T.RegisterTool("vrc_my_avatars", MyAvatars);
            T.RegisterTool("vrc_update_avatar", UpdateAvatar);
            T.RegisterTool("vrc_build", Build);
            T.RegisterTool("vrc_upload", Upload);
            RestoreMultiPlatformJob();
        }

        // ---------- helpers ----------

        static string PackageVersion(string name) =>
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + name)?.version;

        static string CurrentUserId => APIUser.IsLoggedIn ? APIUser.CurrentUser?.id : null;

        static Task<bool> _login;

        /// <summary>
        /// Logs in with the credentials the SDK saved, like the Control Panel does when it opens, so tools don't depend on
        /// the panel having been opened in this editor session. Concurrent callers share one attempt.
        /// </summary>
        static Task<bool> EnsureLoggedIn()
        {
            if (APIUser.IsLoggedIn) return Task.FromResult(true);
            if (_login != null && !_login.IsCompleted) return _login;
            _login = LogInWithSavedCredentials();
            return _login;
        }

        static async Task<bool> LogInWithSavedCredentials()
        {
            VRCSdkControlPanel.RefreshApiUrlSetting();
            if (!ApiCredentials.Load()) return false;
            var done = new TaskCompletionSource<bool>();
            APIUser.InitialFetchCurrentUser(_ => done.TrySetResult(APIUser.IsLoggedIn), _ => done.TrySetResult(false));
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            return finished == done.Task && done.Task.Result;
        }

        static async Task RequireLogin()
        {
            if (!await EnsureLoggedIn()) throw new InvalidOperationException(NotLoggedIn);
        }

        static string Platform()
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "standalonewindows";
                case BuildTarget.Android: return "android";
                case BuildTarget.iOS: return "ios";
                default: return EditorUserBuildSettings.activeBuildTarget.ToString();
            }
        }

        static IVRCSdkAvatarBuilderApi RequireBuilder(bool openPanel)
        {
            if (VRCSdkControlPanel.window == null)
            {
                if (!openPanel) throw new InvalidOperationException("The VRChat SDK Control Panel isn't open. Pass openPanel: true or open VRChat SDK > Show Control Panel.");
                EditorApplication.ExecuteMenuItem(ControlPanelMenu);
            }

            if (VRCSdkControlPanel.window == null || !VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder) || builder == null)
                throw new InvalidOperationException("Could not get the SDK's avatar builder from the Control Panel.");
            return builder;
        }

        static (GameObject go, VRCAvatarDescriptor desc) RequireAvatar(JObject a)
        {
            var go = T.FindGameObject(T.Required(a, "path"));
            var desc = go.GetComponent<VRCAvatarDescriptor>();
            return desc ? (go, desc) : throw new ArgumentException($"{T.GetPath(go.transform)} has no VRCAvatarDescriptor.");
        }

        static string BlueprintId(GameObject go)
        {
            var pm = go.GetComponent<PipelineManager>();
            return pm && !string.IsNullOrEmpty(pm.blueprintId) ? pm.blueprintId : null;
        }

        static string AvatarIdFrom(JObject a)
        {
            var id = T.Str(a, "avatarId");
            if (id != null) return id;
            var (go, _) = RequireAvatar(a);
            return BlueprintId(go) ?? throw new ArgumentException($"{T.GetPath(go.transform)} has no blueprint id yet (never uploaded).");
        }

        static Dictionary<string, List<string>> SceneAvatarsById()
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var d in T.AllRoots().SelectMany(r => r.GetComponentsInChildren<VRCAvatarDescriptor>(true)))
            {
                var id = BlueprintId(d.gameObject);
                if (id == null) continue;
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<string>();
                list.Add(T.GetPath(d.transform));
            }

            return map;
        }

        static JObject DescribeAvatar(VRCAvatar av)
        {
            var o = new JObject
            {
                ["id"] = av.ID,
                ["name"] = av.Name,
                ["description"] = av.Description,
                [KeyReleaseStatus] = av.ReleaseStatus,
                ["tags"] = new JArray(av.Tags ?? new List<string>()),
                ["version"] = av.Version,
                ["author"] = $"{av.AuthorName} ({av.AuthorId})",
                ["ownedByYou"] = CurrentUserId != null && av.AuthorId == CurrentUserId,
                ["created"] = av.CreatedAt.ToString("u"),
                ["updated"] = av.UpdatedAt.ToString("u"),
                ["thumbnailUrl"] = av.ThumbnailImageUrl,
            };
            if (!string.IsNullOrEmpty(av.Styles.Primary) || !string.IsNullOrEmpty(av.Styles.Secondary))
                o["styles"] = $"{av.Styles.Primary} / {av.Styles.Secondary}";
            if (av.PendingUpload) o["pendingUpload"] = true;
            if (av.Lock) o["locked"] = true;
            if (!string.IsNullOrEmpty(av.ActiveAssetReviewId)) o["activeAssetReviewId"] = av.ActiveAssetReviewId;
            if (av.UnityPackages != null)
                o["platforms"] = new JArray(av.UnityPackages
                    .GroupBy(p => (p.Platform, p.Variant))
                    .Select(g => g.OrderByDescending(p => p.AssetVersion).ThenByDescending(p => p.CreatedAt).First())
                    .OrderBy(p => p.Platform)
                    .Select(p => $"{p.Platform}{(string.IsNullOrEmpty(p.Variant) || p.Variant == "standard" ? "" : $" ({p.Variant})")}: " +
                                 $"Unity {p.UnityVersion}, asset v{p.AssetVersion}, {p.CreatedAt:u}"));
            return o;
        }

        static JArray DescribeError(Exception e)
        {
            if (e is ValidationException ve && ve.Errors != null && ve.Errors.Count > 0) return new JArray(ve.Errors);
            return null;
        }

        /// <summary>Forwards SDK build/upload events into the job log; dispose to unsubscribe.</summary>
        static readonly System.Text.RegularExpressions.Regex UrlQuery =
            new System.Text.RegularExpressions.Regex(@"(https?://[^\s?""']+)\?[^\s""']*", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));

        /// <summary>Upload URLs are pre-signed (temporary credentials in the query): keep host and path only.</summary>
        static string RedactUrls(string s) => s == null ? null : UrlQuery.Replace(s, "$1?[signed query removed]");

        /// <summary>
        /// The SDK reports upload/API failures (HTTP status, S3 response, failing step) only through VRC.Core.Logger, which
        /// drops them unless their category is enabled. While a build/upload runs, let errors and warnings through and copy
        /// them (and Unity's own errors) into the job; restore the logger afterwards.
        /// </summary>
        sealed class SdkLogCapture : IDisposable
        {
            readonly McpJob _job;
            readonly Type _logger = typeof(APIUser).Assembly.GetType("VRC.Core.Logger");
            readonly bool? _prevErrors, _prevWarnings;
            readonly JArray _errors = new JArray();

            public SdkLogCapture(McpJob job)
            {
                _job = job;
                _prevErrors = SetForce("ForcePassAllErrors", true);
                _prevWarnings = SetForce("ForcePassAllWarnings", true);
                Application.logMessageReceived += OnLog;
            }

            bool? SetForce(string property, bool value)
            {
                var p = _logger?.GetProperty(property, AnyStatic);
                if (p == null || !p.CanWrite) return null;
                var prev = (bool)p.GetValue(null);
                p.SetValue(null, value);
                return prev;
            }

            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Log) return;
                var text = RedactUrls(message.Split('\n')[0].Trim());
                if (text.Length == 0) return;
                _job.Log($"console {type.ToString().ToLowerInvariant()}: {text}");
                if (type != LogType.Warning && _errors.Count < 30) _errors.Add(text);
            }

            public void Dispose()
            {
                Application.logMessageReceived -= OnLog;
                if (_prevErrors.HasValue) SetForce("ForcePassAllErrors", _prevErrors.Value);
                if (_prevWarnings.HasValue) SetForce("ForcePassAllWarnings", _prevWarnings.Value);
                if (_errors.Count > 0 && _job.ErrorDetails == null) _job.ErrorDetails = _errors;
            }
        }

        sealed class BuilderEvents : IDisposable
        {
            readonly IVRCSdkAvatarBuilderApi _b;
            readonly SdkLogCapture _capture;
            readonly EventHandler<string> _buildProgress, _buildError, _uploadError;
            readonly EventHandler<(string status, float percentage)> _uploadProgress;
            readonly EventHandler<SdkBuildState> _buildState;
            readonly EventHandler<SdkUploadState> _uploadState;

            public BuilderEvents(IVRCSdkAvatarBuilderApi b, McpJob job)
            {
                _b = b;
                _capture = new SdkLogCapture(job);
                _buildProgress = (_, s) => { job.Stage = "build"; job.Log(s); };
                _buildError = (_, s) => job.Log("build error: " + RedactUrls(s));
                _uploadError = (_, s) => job.Log("upload error: " + RedactUrls(s));
                _uploadProgress = (_, p) =>
                {
                    // Called from the SDK's HTTP worker thread: an exception here would abort the upload.
                    try
                    {
                        job.Stage = "upload: " + p.status;
                        job.Progress = p.percentage;
                        job.Log($"{p.status} {p.percentage:P0}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[UnityVrcMcp] upload progress handler: " + e.Message);
                    }
                };
                _buildState = (_, s) => job.Log("build state: " + s);
                _uploadState = (_, s) => job.Log("upload state: " + s);
                b.OnSdkBuildProgress += _buildProgress;
                b.OnSdkBuildError += _buildError;
                b.OnSdkUploadError += _uploadError;
                b.OnSdkUploadProgress += _uploadProgress;
                b.OnSdkBuildStateChange += _buildState;
                b.OnSdkUploadStateChange += _uploadState;
            }

            public void Dispose()
            {
                _b.OnSdkBuildProgress -= _buildProgress;
                _b.OnSdkBuildError -= _buildError;
                _b.OnSdkUploadError -= _uploadError;
                _b.OnSdkUploadProgress -= _uploadProgress;
                _b.OnSdkBuildStateChange -= _buildState;
                _b.OnSdkUploadStateChange -= _uploadState;
                _capture.Dispose();
            }
        }

        // ---------- vrc_sdk_status ----------

        static JToken Status(JObject a)
        {
            if (T.Bool(a, "openPanel", false) && VRCSdkControlPanel.window == null) EditorApplication.ExecuteMenuItem(ControlPanelMenu);
            if (APIUser.IsLoggedIn) return StatusNow(false);
            return McpJobs.Start("vrc_sdk_status", async job =>
            {
                job.Stage = "logging in with the SDK's saved credentials";
                var ok = await EnsureLoggedIn();
                return StatusNow(!ok);
            });
        }

        static JObject StatusNow(bool loginFailed)
        {
            var res = new JObject
            {
                ["sdkBase"] = PackageVersion("com.vrchat.base"),
                ["sdkAvatars"] = PackageVersion("com.vrchat.avatars"),
                ["buildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                [KeyPlatform] = Platform(),
                ["loggedIn"] = APIUser.IsLoggedIn,
                ["controlPanelOpen"] = VRCSdkControlPanel.window != null,
            };
            if (APIUser.IsLoggedIn && APIUser.CurrentUser != null)
                res["user"] = $"{APIUser.CurrentUser.displayName} ({APIUser.CurrentUser.id})";
            if (VRCSdkControlPanel.window != null && VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var b) && b != null)
            {
                res["buildState"] = b.BuildState.ToString();
                res["uploadState"] = b.UploadState.ToString();
                res["panelSelectedAvatar"] = b.SelectedAvatar ? T.GetPath(b.SelectedAvatar.transform) : null;
            }

            res["platformsInstalled"] = new JObject(AllTargets.Select(t => new JProperty(ApiPlatform(t), Supported(t))));
            var mpb = MpbSnapshot();
            if (mpb != null)
            {
                var rec = LoadRecord();
                if (rec != null) mpb["job"] = rec[RecordId];
                res["multiPlatformUpload"] = mpb;
            }

            res["sceneAvatars"] = new JArray(T.AllRoots().SelectMany(r => r.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                .Select(d => $"{T.GetPath(d.transform)}{(d.gameObject.activeInHierarchy ? "" : " (inactive)")}: {BlueprintId(d.gameObject) ?? "no blueprint id"}"));
            if (!APIUser.IsLoggedIn && loginFailed) res["note"] = NotLoggedIn;
            return res;
        }

        // ---------- vrc_validate ----------

        static JToken Validate(JObject a)
        {
            var (go, desc) = RequireAvatar(a);
            var builder = RequireBuilder(T.Bool(a, "openPanel", true));
            var panel = VRCSdkControlPanel.window;

            // Same steps as the SDK's own pre-upload check (CheckAvatarForValidationIssues).
            panel.CheckedForIssues = false;
            panel.ResetIssues();
            var setup = typeof(VRC_EditorTools).GetMethod("GetCheckProjectSetupMethod", AnyStatic)?.Invoke(null, null) as MethodInfo;
            setup?.Invoke(panel, Array.Empty<object>());
            var check = builder.GetType().GetMethod("OnGUIAvatarCheck", AnyInstance)
                        ?? throw new InvalidOperationException("This SDK version has no OnGUIAvatarCheck; validation isn't supported.");
            check.Invoke(builder, new object[] { desc });
            panel.CheckedForIssues = true;
            panel.Repaint();

            var res = new JObject { [KeyAvatar] = T.GetPath(go.transform), [KeyPlatform] = Platform() };
            foreach (var (field, key) in new[] { ("GUIErrors", "errors"), ("GUIWarnings", "warnings"), ("GUIInfos", "infos"), ("GUIStats", "stats") })
                res[key] = Issues(panel, field, go);
            res["ok"] = ((JArray)res["errors"]).Count == 0;
            res["note"] = "Issues with fix: true have an Auto Fix button in the SDK Control Panel. VRCFury and other build-time tools are not applied here.";
            return res;
        }

        static JArray Issues(VRCSdkControlPanel panel, string field, GameObject avatar)
        {
            var res = new JArray();
            if (!(typeof(VRCSdkControlPanel).GetField(field, AnyInstance)?.GetValue(panel) is IDictionary dict)) return res;
            foreach (DictionaryEntry e in dict)
            {
                var subject = e.Key as Object;
                string where;
                if (subject == null || subject == panel) where = "project";
                else if (subject is Component c) where = c.transform.IsChildOf(avatar.transform) ? T.GetPath(c.transform) : null;
                else if (subject is GameObject g) where = g.transform.IsChildOf(avatar.transform) ? T.GetPath(g.transform) : null;
                else where = AssetDatabase.GetAssetPath(subject) is var p && !string.IsNullOrEmpty(p) ? p : subject.name;
                if (where == null || !(e.Value is IEnumerable issues)) continue; // another avatar's issue

                foreach (var issue in issues)
                {
                    var text = issue.GetType().GetField("issueText")?.GetValue(issue) as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var fix = issue.GetType().GetField("fixThisIssue")?.GetValue(issue) != null;
                    var o = new JObject { ["on"] = where, ["text"] = text.Trim() };
                    if (fix) o["fix"] = true;
                    res.Add(o);
                }
            }

            return res;
        }

        // ---------- vrc_performance ----------

        static JToken Performance(JObject a)
        {
            var (go, _) = RequireAvatar(a);
            var res = new JObject { [KeyAvatar] = T.GetPath(go.transform) };
            foreach (var mobile in new[] { false, true })
            {
                var stats = new AvatarPerformanceStats(mobile);
                AvatarPerformance.CalculatePerformanceStats(go.name, go, stats, mobile);
                var ratings = new JObject();
                foreach (AvatarPerformanceCategory cat in Enum.GetValues(typeof(AvatarPerformanceCategory)))
                {
                    var name = cat.ToString();
                    if (name == "None" || name == "Overall" || name.EndsWith("Count", StringComparison.Ordinal) && name.StartsWith("AvatarPerformanceCategory", StringComparison.Ordinal)) continue;
                    try
                    {
                        var r = stats.GetPerformanceRatingForCategory(cat);
                        if (r.ToString() != "None") ratings[name] = r.ToString();
                    }
                    catch
                    {
                        // category not rated on this platform
                    }
                }

                var values = new JObject();
                foreach (var f in stats.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var v = f.GetValue(stats);
                    if (v is int || v is float || v is bool || v is long || v is double) values[f.Name] = JToken.FromObject(v);
                    else if (v is Bounds bounds) values[f.Name] = T.V3(bounds.size);
                }

                res[mobile ? "mobile" : "pc"] = new JObject
                {
                    ["overall"] = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall).ToString(),
                    ["ratings"] = ratings,
                    ["values"] = values,
                };
            }

            res["note"] = "Pre-build numbers: VRCFury, optimizers and other build-time tools usually change them. vrc_build gives the real bundle.";
            return res;
        }

        // ---------- VRChat API reads ----------

        static JToken AvatarInfo(JObject a)
        {
            var id = AvatarIdFrom(a);
            return McpJobs.Start("vrc_avatar_info", async job =>
            {
                await RequireLogin();
                var av = await VRCApi.GetAvatar(id, true, job.Token);
                var o = DescribeAvatar(av);
                if (SceneAvatarsById().TryGetValue(id, out var paths)) o["inScene"] = new JArray(paths);
                return o;
            });
        }

        static JToken MyAvatars(JObject a)
        {
            var limit = Mathf.Clamp(T.Int(a, "limit", 50), 1, 100);
            var offset = Math.Max(0, T.Int(a, "offset", 0));
            var search = T.Str(a, "search");
            return McpJobs.Start("vrc_my_avatars", async job =>
            {
                await RequireLogin();
                var query = new Dictionary<string, string>
                {
                    ["user"] = "me", [KeyReleaseStatus] = "all", ["sort"] = "updated", ["order"] = "descending",
                    ["n"] = limit.ToString(), ["offset"] = offset.ToString(),
                };
                var list = await VRCApi.Get<List<VRCAvatar>>("avatars", query, true, false, job.Token) ?? new List<VRCAvatar>();
                var scene = SceneAvatarsById();
                var rows = new JArray();
                foreach (var av in list)
                {
                    if (search != null && (av.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var platforms = av.UnityPackages?.Select(p => p.Platform).Distinct().OrderBy(p => p) ?? Enumerable.Empty<string>();
                    var row = $"{av.ID}: {av.Name} [{av.ReleaseStatus}] updated {av.UpdatedAt:u}, platforms: {string.Join(", ", platforms)}";
                    if (scene.TryGetValue(av.ID, out var paths)) row += $" | in scene: {string.Join(", ", paths)}";
                    rows.Add(row);
                }

                return new JObject
                {
                    ["count"] = list.Count, ["offset"] = offset, ["avatars"] = rows,
                    ["more"] = list.Count == limit ? $"Pass offset {offset + limit} for the next page." : null,
                };
            });
        }

        // ---------- vrc_update_avatar (outward-facing) ----------

        static void ApplyChanges(ref VRCAvatar av, JObject a, JObject changes)
        {
            var name = T.Str(a, "name");
            var description = a.TryGetValue("description", StringComparison.OrdinalIgnoreCase, out var d) ? d.ToString() : null;
            var releaseStatus = T.Str(a, KeyReleaseStatus);
            var tags = T.Arg(a, "tags") as JArray;
            if (name != null && name != av.Name) { changes["name"] = $"{av.Name} -> {name}"; av.Name = name; }
            if (description != null && description != av.Description) { changes["description"] = $"{av.Description} -> {description}"; av.Description = description; }
            if (releaseStatus != null)
            {
                releaseStatus = releaseStatus.ToLowerInvariant();
                if (releaseStatus != "public" && releaseStatus != "private") throw new ArgumentException("releaseStatus must be public or private.");
                if (releaseStatus != av.ReleaseStatus) { changes[KeyReleaseStatus] = $"{av.ReleaseStatus} -> {releaseStatus}"; av.ReleaseStatus = releaseStatus; }
            }

            if (tags != null)
            {
                var newTags = tags.Select(t => t.ToString()).ToList();
                var oldTags = av.Tags ?? new List<string>();
                if (!newTags.SequenceEqual(oldTags)) { changes["tags"] = $"[{string.Join(", ", oldTags)}] -> [{string.Join(", ", newTags)}]"; av.Tags = newTags; }
            }
        }

        static JToken UpdateAvatar(JObject a)
        {
            var id = AvatarIdFrom(a);
            var confirm = T.Bool(a, "confirm", false);
            return McpJobs.Start("vrc_update_avatar", async job =>
            {
                await RequireLogin();
                var av = await VRCApi.GetAvatar(id, true, job.Token);
                if (av.AuthorId != CurrentUserId) throw new InvalidOperationException($"Avatar {id} belongs to {av.AuthorName}, not the logged-in user.");
                var changes = new JObject();
                ApplyChanges(ref av, a, changes);
                if (changes.Count == 0) return new JObject { [KeyAvatar] = id, ["note"] = "Nothing to change." };
                if (!confirm)
                    return new JObject
                    {
                        ["preview"] = new JObject { [KeyAvatar] = $"{id} ({av.Name})", ["changes"] = changes },
                        ["note"] = "Nothing was changed. Show this to the user and call again with confirm: true only after they approve.",
                    };
                var updated = await VRCApi.UpdateAvatarInfo(id, av, job.Token);
                return new JObject { ["updated"] = DescribeAvatar(updated), ["changes"] = changes };
            }, DescribeError);
        }

        // ---------- vrc_build (local only) ----------

        static JToken Build(JObject a)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit play mode before building.");
            var (go, _) = RequireAvatar(a);
            var builder = RequireBuilder(true);
            var test = T.Bool(a, "test", false);
            return McpJobs.Start("vrc_build", async job =>
            {
                using (new BuilderEvents(builder, job))
                {
                    job.Stage = "build";
                    if (test)
                    {
                        await builder.BuildAndTest(go);
                        return new JObject
                        {
                            [KeyAvatar] = T.GetPath(go.transform), ["test"] = true, [KeyPlatform] = Platform(),
                            ["note"] = "Built as a local test avatar: in VRChat (same PC) it is under Avatars > Other > SDK test avatars. Nothing was uploaded.",
                        };
                    }

                    var bundle = await builder.Build(go);
                    var res = new JObject { [KeyAvatar] = T.GetPath(go.transform), [KeyPlatform] = Platform(), ["bundle"] = bundle };
                    if (!string.IsNullOrEmpty(bundle) && File.Exists(bundle))
                        res["bundleSizeMB"] = Math.Round(new FileInfo(bundle).Length / 1048576.0, 2);
                    return res;
                }
            }, DescribeError);
        }

        // ---------- vrc_upload (outward-facing) ----------

        sealed class UploadPrep
        {
            public VRCAvatar Avatar;
            public string Id;
            public bool IsNew;
            public string Thumbnail;
            public JObject Plan;
        }

        static async Task<UploadPrep> PrepareUpload(JObject a, GameObject go, string thumbnail, McpJob job)
        {
            var prep = new UploadPrep { Id = BlueprintId(go), Thumbnail = thumbnail };
            prep.IsNew = prep.Id == null;
            if (prep.IsNew)
            {
                var name = T.Str(a, "name") ?? throw new ArgumentException("A new avatar needs 'name'.");
                prep.Avatar = new VRCAvatar { Name = name, Description = "", Tags = new List<string>(), ReleaseStatus = "private" };
                prep.Thumbnail = thumbnail ?? "capture";
            }
            else
            {
                prep.Avatar = await VRCApi.GetAvatar(prep.Id, true, job.Token);
                if (prep.Avatar.AuthorId != CurrentUserId)
                    throw new InvalidOperationException($"Blueprint {prep.Id} belongs to {prep.Avatar.AuthorName}, not the logged-in user.");
            }

            var changes = new JObject();
            ApplyChanges(ref prep.Avatar, a, changes);
            prep.Plan = new JObject
            {
                ["action"] = prep.IsNew ? "create a NEW avatar on VRChat" : $"upload a new version of {prep.Id}",
                ["sceneObject"] = T.GetPath(go.transform),
                ["name"] = prep.Avatar.Name,
                [KeyReleaseStatus] = prep.Avatar.ReleaseStatus,
                ["thumbnail"] = prep.Thumbnail == null ? "keep the current one"
                    : prep.Thumbnail == "capture" ? "capture a new 1200x900 render of the avatar" : prep.Thumbnail,
                ["infoChanges"] = changes.Count > 0 ? (JToken)changes : "none",
            };
            return prep;
        }

        internal const string WaitingForAgreement =
            "waiting for you to click OK on the VRChat copyright agreement in Unity (in the SDK Control Panel, or a dialog)";

        /// <summary>The SDK's own check: agreed on VRChat's side AND in this Unity session (VRCCopyrightAgreement.HasAgreement).</summary>
        static async Task<bool> CopyrightAgreed(string contentId)
        {
            var m = typeof(VRCCopyrightAgreement).GetMethod("HasAgreement", AnyStatic);
            return m != null && await (Task<bool>)m.Invoke(null, new object[] { contentId });
        }

        /// <summary>Records the agreement exactly as the SDK's OK button does (VRCCopyrightAgreement.Agree).</summary>
        static async Task<bool> RecordCopyrightAgreement(string contentId)
        {
            var m = typeof(VRCCopyrightAgreement).GetMethod("Agree", AnyStatic)
                    ?? throw new InvalidOperationException("This SDK version has no VRCCopyrightAgreement.Agree; answer the dialog in Unity instead.");
            return await (Task<bool>)m.Invoke(null, new object[] { contentId });
        }

        /// <summary>Adds the agreement status to the plan; returns whether Unity would ask.</summary>
        static async Task<bool> AddAgreementPlan(UploadPrep prep)
        {
            if (!prep.IsNew && await CopyrightAgreed(prep.Id))
            {
                prep.Plan["copyrightAgreement"] = "already agreed for this avatar in this Unity session: no dialog";
                return false;
            }

            prep.Plan["copyrightAgreement"] = new JObject
            {
                ["required"] = true,
                ["text"] = VRCCopyrightAgreement.AgreementText,
                ["howToAnswer"] = prep.IsNew
                    ? "A new avatar only gets its id during the upload, so the SDK asks in Unity: the user clicks OK there (SDK Control Panel or a dialog). Tell them before starting."
                    : "Show this exact text to the user. If they agree to it in this conversation, pass agreeCopyright: true together with confirm: true and it is " +
                      "recorded with VRChat like the SDK's OK button, so no dialog appears. Otherwise Unity shows it during the upload and they click OK there.",
            };
            return true;
        }

        static string ResolveThumbnail(string thumbnail, GameObject go, McpJob job)
        {
            if (thumbnail == null) return null;
            if (thumbnail == "capture")
            {
                var file = T.CaptureView(new JObject { ["frame"] = T.GetPath(go.transform), ["width"] = 1200, ["height"] = 900, ["fov"] = 25 })["file"]?.ToString();
                job.Log("captured thumbnail " + file);
                return file;
            }

            var path = Path.GetFullPath(Path.IsPathRooted(thumbnail) ? thumbnail : Path.Combine(UnityVrcMcpBridge.ProjectRoot, thumbnail));
            return File.Exists(path) ? path : throw new ArgumentException($"Thumbnail '{path}' not found.");
        }

        static JToken Upload(JObject a)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit play mode before uploading.");
            var (go, _) = RequireAvatar(a);
            if (!go.GetComponent<PipelineManager>()) throw new ArgumentException($"{T.GetPath(go.transform)} has no PipelineManager.");
            var confirm = T.Bool(a, "confirm", false);
            var thumbnail = T.Str(a, "thumbnail");
            var targets = ParsePlatforms(T.Arg(a, "platforms") as JArray);
            var current = EditorUserBuildSettings.activeBuildTarget;
            var multi = targets != null && !(targets.Count == 1 && targets[0] == current);
            if (multi && MpbActive) throw new InvalidOperationException("A multi-platform upload is already running; check vrc_sdk_status.");
            var builder = confirm ? RequireBuilder(true) : null;

            return McpJobs.Start("vrc_upload", async job =>
            {
                await RequireLogin();
                var prep = await PrepareUpload(a, go, thumbnail, job);
                if (multi) AddPlatformPlan(prep.Plan, go, targets);
                else prep.Plan[KeyPlatform] = $"{Platform()} only (this editor's build target)";
                var needsAgreement = await AddAgreementPlan(prep);

                if (!confirm)
                    return new JObject
                    {
                        ["preview"] = prep.Plan,
                        ["note"] = "Nothing was built or uploaded. Show this plan to the user and call again with confirm: true only after they approve." +
                                   (needsAgreement ? " The copyright agreement is required: see copyrightAgreement.howToAnswer." : ""),
                    };

                if (needsAgreement && !prep.IsNew && T.Bool(a, "agreeCopyright", false))
                {
                    job.Stage = "recording the copyright agreement";
                    if (!await RecordCopyrightAgreement(prep.Id))
                        throw new InvalidOperationException("VRChat didn't accept the copyright agreement; console_logs may say why. The user can still click OK in Unity.");
                    job.Log("copyright agreement recorded (the user agreed in chat)");
                    needsAgreement = false;
                }

                if (multi)
                {
                    var missing = targets.Where(t => !Supported(t)).Select(ApiPlatform).ToList();
                    if (missing.Count > 0)
                        throw new InvalidOperationException($"Unity's build support for {string.Join(", ", missing)} isn't installed (add the module in Unity Hub).");
                }

                var thumbPath = ResolveThumbnail(prep.Thumbnail, go, job);
                if (multi) return StartMultiPlatform(builder, go, targets, prep, thumbPath, needsAgreement);

                job.OnCancel = builder.CancelUpload;
                using (new BuilderEvents(builder, job))
                {
                    // The SDK asks before it builds, so the first build progress event replaces this stage.
                    job.Stage = needsAgreement ? WaitingForAgreement : "build";
                    if (needsAgreement) job.Log(WaitingForAgreement);
                    await builder.BuildAndUpload(go, prep.Avatar, thumbPath, job.Token);
                }

                var finalId = BlueprintId(go) ?? prep.Id;
                var after = finalId != null ? DescribeAvatar(await VRCApi.GetAvatar(finalId, true, job.Token)) : null;
                return new JObject { ["uploaded"] = after, ["plan"] = prep.Plan };
            }, DescribeError);
        }
    }
}
