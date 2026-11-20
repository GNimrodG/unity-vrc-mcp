// Local HTTP bridge that lets an MCP server (Server~/server.mjs) inspect and drive this Unity editor.
// Listens on 127.0.0.1 only and requires the per-project token in Library/UnityVrcMcp/token.
// Lives in its own assembly so it keeps running (and can report the errors) when other editor scripts fail to compile.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityVrcMcp.Editor
{
    [InitializeOnLoad]
    public static class UnityVrcMcpBridge
    {
        public const int DefaultPort = 47811;
        const string PortFile = "ProjectSettings/UnityVrcMcpPort.txt";
        const string TokenFile = "Library/UnityVrcMcp/token";
        const int CallTimeoutMs = 120000;
        const double MaxPumpSecondsPerFrame = 0.25;

        sealed class Job
        {
            public string Tool;
            public JObject Args;
            public string Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static HttpListener _listener;
        static volatile bool _running;
        static readonly ConcurrentQueue<Job> Jobs = new ConcurrentQueue<Job>();
        static string _token;
        static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        static int _port;
        static volatile string _statusJson = "{}";
        static double _nextStatus;

        internal static readonly string ProjectRoot = Path.GetFullPath(Path.GetDirectoryName(Application.dataPath) ?? ".");
        static readonly object CompileLock = new object();
        static readonly List<string> CompileErrors = new List<string>();

        // Fallback log buffer, used if the Console window internals can't be reached by reflection.
        internal static readonly ConcurrentQueue<string> RecentLogs = new ConcurrentQueue<string>();

        static UnityVrcMcpBridge()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            CompilationPipeline.compilationStarted += _ => { lock (CompileLock) CompileErrors.Clear(); };
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            Application.logMessageReceivedThreaded += (msg, stack, type) =>
            {
                RecentLogs.Enqueue($"{type}: {msg}");
                while (RecentLogs.Count > 300 && RecentLogs.TryDequeue(out _))
                {
                    // drop the oldest entries
                }
            };
            Start();
        }

        internal static int Port => _port;

        internal static string[] GetCompileErrors()
        {
            lock (CompileLock) return CompileErrors.ToArray();
        }

        static void OnAssemblyCompiled(string assembly, CompilerMessage[] messages)
        {
            lock (CompileLock)
            {
                foreach (var m in messages)
                    if (m.type == CompilerMessageType.Error)
                        CompileErrors.Add($"{Path.GetFileName(assembly)}: {m.file}({m.line},{m.column}): {m.message}");
            }
        }

        static int ReadPort()
        {
            try
            {
                var f = Path.Combine(ProjectRoot, PortFile);
                if (File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out var p) && p > 0 && p < 65536) return p;
            }
            catch { /* use default */ }
            return DefaultPort;
        }

        static string LoadOrCreateToken()
        {
            var f = Path.Combine(ProjectRoot, TokenFile);
            try
            {
                if (File.Exists(f))
                {
                    var t = File.ReadAllText(f).Trim();
                    if (t.Length >= 32) return t;
                }
            }
            catch { /* regenerate */ }

            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            Directory.CreateDirectory(Path.GetDirectoryName(f) ?? ".");
            File.WriteAllText(f, token);
            return token;
        }

        static void Start()
        {
            _port = ReadPort();
            _token = LoadOrCreateToken();
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
            }
            catch (Exception e)
            {
                _listener = null;
                Debug.LogWarning($"[UnityVrcMcp] Could not listen on 127.0.0.1:{_port} ({e.Message}). " +
                                 $"If another Unity project uses it, put a different port in {PortFile}.");
                return;
            }

            _running = true;
            new Thread(ListenLoop) { IsBackground = true, Name = "UnityVrcMcpBridge" }.Start();
        }

        static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); _listener?.Close(); }
            catch { /* already closed */ }
            _listener = null;
            while (Jobs.TryDequeue(out var job))
            {
                job.Response = Error("Unity is reloading scripts or quitting; retry in a few seconds.");
                job.Done.Set();
            }
        }

        static void ListenLoop()
        {
            var listener = _listener;
            while (_running && listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var host = (req.Headers["Host"] ?? "").Split(':')[0];
                if (host != "127.0.0.1" && host != "localhost")
                {
                    Write(ctx, 403, Error("Forbidden host"));
                    return;
                }

                if (req.Headers["X-Unity-Vrc-Mcp-Token"] != _token)
                {
                    Write(ctx, 401, Error("Missing or wrong X-Unity-Vrc-Mcp-Token"));
                    return;
                }

                var path = req.Url.AbsolutePath;
                if (req.HttpMethod == "GET" && path == "/health")
                {
                    Write(ctx, 200, _statusJson);
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/call")
                {
                    string body;
                    using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                    var o = JObject.Parse(body);
                    var job = new Job { Tool = (string)o["tool"], Args = o["args"] as JObject ?? new JObject() };
                    Jobs.Enqueue(job);
                    if (!job.Done.Wait(CallTimeoutMs))
                    {
                        Write(ctx, 504, Error("Timed out waiting for the Unity main thread (a modal dialog or long import may be blocking it)."));
                        return;
                    }

                    Write(ctx, 200, job.Response);
                    return;
                }

                Write(ctx, 404, Error("Not found"));
            }
            catch (Exception e)
            {
                try { Write(ctx, 500, Error(e.Message)); }
                catch { /* client gone */ }
            }
        }

        static void Write(HttpListenerContext ctx, int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        static string Error(string message) =>
            new JObject { ["ok"] = false, ["error"] = message }.ToString(Formatting.None);

        static void Pump()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now >= _nextStatus)
            {
                _nextStatus = now + 0.25;
                _statusJson = new JObject
                {
                    ["ok"] = true,
                    ["project"] = ProjectRoot,
                    ["unityVersion"] = Application.unityVersion,
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["isPlaying"] = EditorApplication.isPlaying,
                    ["isPaused"] = EditorApplication.isPaused,
                    ["compileErrors"] = GetCompileErrors().Length,
                    ["updatedUnix"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    ["pid"] = ProcessId, // lets the MCP server find (and answer) native dialogs that block the editor
                }.ToString(Formatting.None);
            }

            var deadline = now + MaxPumpSecondsPerFrame;
            while (EditorApplication.timeSinceStartup < deadline && Jobs.TryDequeue(out var job))
            {
                try
                {
                    var result = UnityVrcMcpTools.Dispatch(job.Tool, job.Args);
                    job.Response = new JObject { ["ok"] = true, ["result"] = result }.ToString(Formatting.None);
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    var err = new JObject { ["ok"] = false, ["error"] = $"{inner.GetType().Name}: {inner.Message}" };
                    if (!(inner is ArgumentException)) err["stack"] = inner.StackTrace;
                    job.Response = err.ToString(Formatting.None);
                }
                finally
                {
                    job.Done.Set();
                }
            }
        }
    }
}
