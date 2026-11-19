// execute_code: compiles a C# snippet with the compiler Unity ships (CodeDom -> mcs/csc), loads it and runs it on the
// main thread, without writing into Assets/ or reloading scripts, so it also works in the middle of a play-mode session.
// Approach adapted from MCP for Unity (https://github.com/CoplayDev/unity-mcp, ExecuteCode.cs), MIT License,
// Copyright (c) 2025 CoplayDev: response file for references, compiling to a file and loading its bytes, filtering
// duplicate core assemblies.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityVrcMcp.Editor
{
    /// <summary>Helpers for execute_code snippets (the snippet's assembly can only see public members).</summary>
    public static class Mcp
    {
        /// <summary>Scene object by path ("Avatar/Armature/Hips"), unique name or "#instanceId".</summary>
        public static GameObject Find(string path) => UnityVrcMcpTools.FindGameObject(path);

        /// <summary>Full hierarchy path of a GameObject or Component.</summary>
        public static string PathOf(Object o) =>
            o is Component c ? UnityVrcMcpTools.GetPath(c.transform) : o is GameObject g ? UnityVrcMcpTools.GetPath(g.transform) : o ? o.name : null;

        /// <summary>Adds a line to the snippet's returned log.</summary>
        public static void Log(object message) => UnityVrcMcpExecuteCode.CurrentLog?.Add(message?.ToString() ?? "null");
    }

    internal static class UnityVrcMcpExecuteCode
    {
        const string WrapperClass = "UnityVrcMcpSnippet";
        const string WrapperMethod = "Run";
        const int MaxCodeLength = 50000;
        const int MaxCache = 32;

        static readonly string[] DefaultUsings =
        {
            "System", "System.Collections", "System.Collections.Generic", "System.IO", "System.Linq", "System.Reflection",
            "UnityEngine", "UnityEditor", "UnityEditor.Animations", "UnityVrcMcp.Editor",
        };

        static readonly HashSet<string> DuplicateCoreAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mscorlib", "System.Runtime", "System.Private.CoreLib", "System.Collections" };

        static readonly Dictionary<string, Assembly> Cache = new Dictionary<string, Assembly>();
        static string[] _references;

        static List<string> _currentLog;

        internal static List<string> CurrentLog => _currentLog;

        internal static JToken Execute(JObject a)
        {
            var code = UnityVrcMcpTools.Required(a, "code");
            if (code.Length > MaxCodeLength) throw new ArgumentException($"Code is longer than {MaxCodeLength} characters.");
            var usings = DefaultUsings.Concat((UnityVrcMcpTools.Arg(a, "usings") as JArray)?.Select(u => u.ToString().Trim().TrimEnd(';')) ?? Enumerable.Empty<string>())
                .Select(u => u.StartsWith("using ", StringComparison.Ordinal) ? u.Substring(6) : u).Distinct().ToList();
            var (source, offset) = Wrap(code, usings);

            var started = DateTime.UtcNow;
            if (!Cache.TryGetValue(source, out var asm))
            {
                asm = Compile(source, offset, out var errors);
                if (asm == null)
                    return new JObject { ["ok"] = false, ["stage"] = "compile", ["errors"] = new JArray(errors), ["note"] = Hint(errors) };
                if (Cache.Count >= MaxCache) Cache.Clear();
                Cache[source] = asm;
            }

            var compileMs = (DateTime.UtcNow - started).TotalMilliseconds;
            var method = asm.GetType(WrapperClass)?.GetMethod(WrapperMethod, BindingFlags.Public | BindingFlags.Static)
                         ?? throw new InvalidOperationException("Compiled snippet has no entry point.");

            var log = new List<string>();
            var unityLog = new JArray();
            Application.LogCallback cb = (msg, stack, type) => { if (unityLog.Count < 200) unityLog.Add($"{type}: {msg}"); };
            _currentLog = log;
            Application.logMessageReceived += cb;
            object result;
            var runStarted = DateTime.UtcNow;
            try
            {
                result = method.Invoke(null, null);
            }
            catch (TargetInvocationException e)
            {
                var inner = e.InnerException ?? e;
                return new JObject
                {
                    ["ok"] = false, ["stage"] = "run", ["error"] = $"{inner.GetType().Name}: {inner.Message}",
                    ["stack"] = string.Join("\n", (inner.StackTrace ?? "").Split('\n').Take(12)),
                    ["log"] = new JArray(log), ["unityLog"] = unityLog,
                };
            }
            finally
            {
                Application.logMessageReceived -= cb;
                _currentLog = null;
            }

            var res = new JObject
            {
                ["ok"] = true,
                ["result"] = UnityVrcMcpTools.ToJson(result),
                ["compileMs"] = Math.Round(compileMs),
                ["runMs"] = Math.Round((DateTime.UtcNow - runStarted).TotalMilliseconds),
            };
            if (log.Count > 0) res["log"] = new JArray(log);
            if (unityLog.Count > 0) res["unityLog"] = unityLog;
            return res;
        }

        static string Hint(List<string> errors) =>
            errors.Any(e => e.Contains("CS1525") || e.Contains("CS8") || e.Contains("feature"))
                ? "Unity's bundled compiler only knows older C# (roughly C# 7): avoid switch expressions, records, 'is not', target-typed new."
                : null;

        static (string source, int offset) Wrap(string code, List<string> usings)
        {
            var sb = new StringBuilder();
            foreach (var u in usings) sb.Append("using ").Append(u).Append(";\n");
            sb.Append("using Object = UnityEngine.Object;\n");
            sb.Append("public static class ").Append(WrapperClass).Append("\n{\n");
            sb.Append("    public static object ").Append(WrapperMethod).Append("()\n    {\n");
            var offset = sb.ToString().Count(ch => ch == '\n');
            var body = code.Contains("return ") || code.Contains("return;") ? code : code + "\nreturn null;";
            sb.Append(body).Append("\n    }\n}\n");
            return (sb.ToString(), offset);
        }

        static string[] References()
        {
            if (_references != null) return _references;
            var paths = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location) || !File.Exists(asm.Location)) continue;
                    paths.Add(asm.Location);
                }
                catch (NotSupportedException)
                {
                    // no location
                }
            }

            var hasNetstandard = paths.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), "netstandard", StringComparison.OrdinalIgnoreCase));
            if (hasNetstandard) paths = paths.Where(p => !DuplicateCoreAssemblies.Contains(Path.GetFileNameWithoutExtension(p))).ToList();

            // One path per assembly name (the same DLL can be loaded from Library and from a package cache).
            _references = paths.GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            return _references;
        }

        static Assembly Compile(string source, int offset, out List<string> errors)
        {
            errors = new List<string>();
            var tmp = Path.Combine(Path.GetTempPath(), "UnityVrcMcp");
            Directory.CreateDirectory(tmp);
            var id = Guid.NewGuid().ToString("N");
            var rsp = Path.Combine(tmp, id + ".rsp");
            var dll = Path.Combine(tmp, id + ".dll");
            try
            {
                // References go through a response file: ~150 /r: flags overflow Windows' command-line limit.
                File.WriteAllLines(rsp, References().Select(p => "/r:\"" + p + "\""), new UTF8Encoding(false));
                using (var provider = new CSharpCodeProvider())
                {
                    var parameters = new CompilerParameters
                    {
                        GenerateInMemory = false, GenerateExecutable = false, OutputAssembly = dll,
                        TreatWarningsAsErrors = false, CompilerOptions = "@\"" + rsp + "\"",
                    };
                    var results = provider.CompileAssemblyFromSource(parameters, source);
                    foreach (CompilerError e in results.Errors)
                    {
                        if (e.IsWarning) continue;
                        // mcs prints a BOM line that Mono's CodeDom reports as an error without a number: ignore it.
                        var text = (e.ErrorText ?? "").Trim('\uFEFF', ' ', '\t', '\r', '\n');
                        if (string.IsNullOrEmpty(e.ErrorNumber) && text.Length == 0) continue;
                        errors.Add($"line {Math.Max(1, e.Line - offset)}: {e.ErrorNumber} {text}");
                    }
                }

                if (errors.Count > 0) return null;
                if (!File.Exists(dll))
                {
                    errors.Add("The compiler reported success but wrote no assembly.");
                    return null;
                }

                return Assembly.Load(File.ReadAllBytes(dll));
            }
            finally
            {
                TryDelete(rsp);
                TryDelete(dll);
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
