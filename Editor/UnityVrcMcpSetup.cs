// Registers the package's MCP server (Server~/server.mjs) in the project's .mcp.json so Claude Code picks it up.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityVrcMcp.Editor
{
    [InitializeOnLoad]
    internal static class UnityVrcMcpSetup
    {
        const string PackageName = "com.data-unknown.unity-vrc-mcp";
        const string ServerName = "unity";
        const string MenuRoot = "Tools/Unity VRC MCP/";

        static UnityVrcMcpSetup()
        {
            EditorApplication.delayCall += () =>
            {
                try { Register(false); }
                catch (Exception e) { Debug.LogWarning($"[UnityVrcMcp] Could not update .mcp.json: {e.Message}"); }
            };
        }

        static string PackageRoot()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityVrcMcpSetup).Assembly);
            if (info != null) return info.resolvedPath;
            var embedded = Path.Combine(UnityVrcMcpBridge.ProjectRoot, "Packages", PackageName);
            return Directory.Exists(embedded) ? embedded : null;
        }

        static string ServerArg(string serverPath)
        {
            var root = UnityVrcMcpBridge.ProjectRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(serverPath);
            var arg = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            return arg.Replace('\\', '/');
        }

        [MenuItem(MenuRoot + "Register in .mcp.json")]
        static void RegisterMenu() => Register(true);

        [MenuItem(MenuRoot + "Open README")]
        static void OpenReadme()
        {
            var root = PackageRoot();
            if (root != null) EditorUtility.OpenWithDefaultApp(Path.Combine(root, "README.md"));
        }

        /// <param name="force">Also replace an entry that points at another existing server script.</param>
        internal static void Register(bool force)
        {
            var root = PackageRoot();
            var server = root != null ? Path.Combine(root, "Server~", "server.mjs") : null;
            if (server == null || !File.Exists(server))
            {
                if (force) Debug.LogWarning("[UnityVrcMcp] Server~/server.mjs not found in the package.");
                return;
            }

            var projectRoot = UnityVrcMcpBridge.ProjectRoot;
            var file = Path.Combine(projectRoot, ".mcp.json");
            var doc = File.Exists(file) ? JObject.Parse(File.ReadAllText(file)) : new JObject();
            if (!(doc["mcpServers"] is JObject servers))
            {
                servers = new JObject();
                doc["mcpServers"] = servers;
            }

            var arg = ServerArg(server);
            var current = (servers[ServerName]?["args"] as JArray)?.FirstOrDefault()?.ToString();
            if (current != null)
            {
                var currentPath = Path.IsPathRooted(current) ? current : Path.Combine(projectRoot, current);
                var same = string.Equals(current.Replace('\\', '/'), arg, StringComparison.OrdinalIgnoreCase);
                if (same || (File.Exists(currentPath) && !force))
                {
                    if (force) Debug.Log($"[UnityVrcMcp] .mcp.json already points '{ServerName}' at {current}.");
                    return;
                }
            }

            servers[ServerName] = new JObject
            {
                ["type"] = "stdio",
                ["command"] = "node",
                ["args"] = new JArray(arg),
            };
            File.WriteAllText(file, doc.ToString(Formatting.Indented) + "\n");
            Debug.Log($"[UnityVrcMcp] Registered MCP server '{ServerName}' ({arg}) in {file}. " +
                      "Restart Claude Code in this project (and approve the server) to use it. Needs Node.js 18+ on PATH.");
        }
    }
}
