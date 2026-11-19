// Per-frame hooks for play-mode tools, inserted into Unity's player loop (an editor assembly can't add MonoBehaviours):
// BeforeAnimation runs at the end of Update, after scripts (the Av3Emulator) and before animators evaluate;
// AfterAnimation runs at the start of PostLateUpdate, after animation and LateUpdate, before skinning and rendering.

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace UnityVrcMcp.Editor
{
    [InitializeOnLoad]
    internal static class McpPlayerLoop
    {
        struct McpBeforeAnimation { }
        struct McpAfterAnimation { }

        public static event Action BeforeAnimation;
        public static event Action AfterAnimation;
        /// <summary>Raised when play mode ends or scripts reload: per-play state must be dropped.</summary>
        public static event Action Reset;

        static bool _installed;

        static McpPlayerLoop()
        {
            Remove(); // leftovers from before a script reload point at the old domain
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingPlayMode || s == PlayModeStateChange.EnteredEditMode) Shutdown();
            };
        }

        static void Shutdown()
        {
            try { Reset?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }
            Remove();
        }

        public static void Ensure()
        {
            if (_installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            loop = Insert(loop, typeof(Update), new PlayerLoopSystem { type = typeof(McpBeforeAnimation), updateDelegate = () => Run(BeforeAnimation) }, true);
            loop = Insert(loop, typeof(PostLateUpdate), new PlayerLoopSystem { type = typeof(McpAfterAnimation), updateDelegate = () => Run(AfterAnimation) }, false);
            PlayerLoop.SetPlayerLoop(loop);
            _installed = true;
        }

        static void Run(Action a)
        {
            if (a == null || !Application.isPlaying) return;
            foreach (var d in a.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        static PlayerLoopSystem Insert(PlayerLoopSystem root, Type phase, PlayerLoopSystem sys, bool atEnd)
        {
            var subs = root.subSystemList;
            for (var i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != phase) continue;
                var list = (subs[i].subSystemList ?? Array.Empty<PlayerLoopSystem>()).Where(s => s.type != sys.type).ToList();
                if (atEnd) list.Add(sys);
                else list.Insert(0, sys);
                subs[i].subSystemList = list.ToArray();
            }

            root.subSystemList = subs;
            return root;
        }

        static void Remove()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (loop.subSystemList == null) return;
            var changed = false;
            for (var i = 0; i < loop.subSystemList.Length; i++)
            {
                var subs = loop.subSystemList[i].subSystemList;
                if (subs == null) continue;
                var kept = subs.Where(s => s.type == null || !s.type.FullName.StartsWith("UnityVrcMcp.", StringComparison.Ordinal)).ToArray();
                if (kept.Length == subs.Length) continue;
                loop.subSystemList[i].subSystemList = kept;
                changed = true;
            }

            if (changed) PlayerLoop.SetPlayerLoop(loop);
            _installed = false;
        }
    }
}
