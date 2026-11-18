// Background jobs for long or async editor work (VRChat API calls, builds, uploads).
// A tool starts a job and returns its id at once; the MCP server polls job_status until it finishes.
// Jobs run on the main thread through Unity's synchronization context and are lost on a domain reload.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

[assembly: InternalsVisibleTo("UnityVrcMcp.Editor.VRChat")]

namespace UnityVrcMcp.Editor
{
    internal sealed class McpJob
    {
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public string Id;
        public string Kind;
        public string State = Running;
        public string Stage;
        public double? Progress;
        public JToken Result;
        public string Error;
        public JArray ErrorDetails;
        public Action OnCancel;
        /// <summary>For jobs whose work outlives a domain reload: refreshes State/Stage/Result when status is read.</summary>
        public Action<McpJob> Poll;
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();
        readonly List<string> _log = new List<string>();
        readonly double _started;

        public McpJob(double? started = null)
        {
            _started = started ?? EditorApplication.timeSinceStartup;
        }
        double? _finished;

        public double Elapsed => (_finished ?? EditorApplication.timeSinceStartup) - _started;
        public CancellationToken Token => Cts.Token;

        public void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (_log.Count > 0 && _log[_log.Count - 1].EndsWith(line, StringComparison.Ordinal)) return;
            if (_log.Count >= 1000) _log.RemoveAt(0);
            _log.Add($"{Elapsed:0.0}s {line}");
        }

        public void Finish(string state)
        {
            State = state;
            _finished = EditorApplication.timeSinceStartup;
        }

        public JObject ToJson(int logTail)
        {
            var o = new JObject { ["job"] = Id, ["kind"] = Kind, ["state"] = State, ["elapsedSeconds"] = Math.Round(Elapsed, 1) };
            if (Stage != null) o["stage"] = Stage;
            if (Progress.HasValue) o["progress"] = Math.Round(Progress.Value, 3);
            if (Error != null) o["error"] = Error;
            if (ErrorDetails != null) o["errorDetails"] = ErrorDetails;
            if (Result != null) o["result"] = Result;
            if (logTail > 0 && _log.Count > 0) o["log"] = new JArray(_log.Skip(Math.Max(0, _log.Count - logTail)));
            return o;
        }
    }

    internal static class McpJobs
    {
        static readonly Dictionary<string, McpJob> All = new Dictionary<string, McpJob>();
        static int _counter;

        /// <summary>Starts <paramref name="run"/> as a job; extra exception details come from <paramref name="describeError"/>.</summary>
        internal static JObject Start(string kind, Func<McpJob, Task<JToken>> run, Func<Exception, JArray> describeError = null)
        {
            var job = new McpJob { Id = $"{kind}-{++_counter}", Kind = kind };
            All[job.Id] = job;
            foreach (var old in All.Values.Where(j => j.State != McpJob.Running && j.Elapsed > 3600).Select(j => j.Id).ToList())
                All.Remove(old);
            _ = Run(job, run, describeError); // fire and forget: Run catches everything and stores it on the job
            return job.ToJson(10);
        }

        static async Task Run(McpJob job, Func<McpJob, Task<JToken>> run, Func<Exception, JArray> describeError)
        {
            try
            {
                job.Result = await run(job);
                job.Finish(McpJob.Done);
            }
            catch (OperationCanceledException)
            {
                job.Finish(McpJob.Cancelled);
            }
            catch (Exception e)
            {
                var inner = e is AggregateException ae && ae.InnerException != null ? ae.InnerException : e;
                job.Error = $"{inner.GetType().Name}: {inner.Message}";
                try { job.ErrorDetails = describeError?.Invoke(inner); }
                catch { /* best effort */ }
                job.Finish(McpJob.Failed);
            }
        }

        static McpJob Find(JObject a)
        {
            var id = UnityVrcMcpTools.Required(a, "id");
            return All.TryGetValue(id, out var job)
                ? job
                : throw new ArgumentException($"No job '{id}' (jobs are lost when Unity reloads scripts). Running: " +
                                              string.Join(", ", All.Values.Where(j => j.State == McpJob.Running).Select(j => j.Id)));
        }

        /// <summary>Registers a job under a known id (e.g. re-created after a domain reload); its state comes from Poll.</summary>
        internal static McpJob Adopt(string id, string kind, double started, Action<McpJob> poll)
        {
            var job = new McpJob(started) { Id = id, Kind = kind, Poll = poll };
            All[id] = job;
            return job;
        }

        internal static JToken Status(JObject a)
        {
            var job = Find(a);
            if (job.State == McpJob.Running && job.Poll != null)
            {
                try { job.Poll(job); }
                catch (Exception e) { job.Log("status check failed: " + e.Message); }
            }

            return job.ToJson(UnityVrcMcpTools.Int(a, "logTail", 30));
        }

        internal static JToken Cancel(JObject a)
        {
            var job = Find(a);
            if (job.State != McpJob.Running) return job.ToJson(5);
            job.Cts.Cancel();
            job.OnCancel?.Invoke();
            job.Log("cancel requested");
            return job.ToJson(5);
        }
    }
}
