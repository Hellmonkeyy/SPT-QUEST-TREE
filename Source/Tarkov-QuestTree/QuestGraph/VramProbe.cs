using System;
using System.Globalization;
using System.Runtime.InteropServices;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// 2026-10-03: the GPU's local (dedicated) memory in use and the OS budget for it, read through EFT's own native plugin
    /// (EscapeFromTarkov_Data/Plugins/x86_64/VRamUsage.dll) with the declarations EFT's VRamUsageWrapper uses, copied
    /// verbatim (Assembly-CSharp-firstpass). The plugin queries DXGI QueryVideoMemoryInfo ON THE RENDER THREAD when its
    /// fetch callback runs as a plugin event, and GetMemoryInfo hands back the last values fetched. The game issues that
    /// event every frame from a CommandBuffer on its menu (EnvironmentUIRoot) and raid (CameraManager) cameras; this issues
    /// one more per read, so a read with no such camera rendering is at most one read stale - the line says "last fetch".
    /// Main thread only (GL.IssuePluginEvent). Any failure - the DLL missing, an entry point renamed - switches it off for
    /// the session and the text says so; it never throws.
    /// </summary>
    internal static class VramProbe
    {
        [DllImport("VRamUsage")]
        private static extern IntPtr GetFunc_FetchLocalMemoryInfo();

        [DllImport("VRamUsage")]
        private static extern bool GetMemoryInfo(bool isLocal, out ulong budget, out ulong currentUsage,
            out ulong availableForReservation, out ulong currentReservation);

        private static bool _off;
        private static string _why;
        private static IntPtr _fetch;

        /// <summary>"VRAM 9,812 of 15,104 MB budget (last fetch)", or why there is no reading.</summary>
        internal static string Text()
        {
            if (_off) return $"VRAM unread ({_why})";

            try
            {
                var got = GetMemoryInfo(true, out var budget, out var usage, out _, out _);

                // a fresh fetch for the next read (render thread, after this frame's commands)
                if (_fetch == IntPtr.Zero) _fetch = GetFunc_FetchLocalMemoryInfo();
                if (_fetch != IntPtr.Zero) GL.IssuePluginEvent(_fetch, 0);

                if (!got || budget == 0UL) return "VRAM not fetched yet";

                return string.Format(CultureInfo.InvariantCulture, "VRAM {0:#,##0} of {1:#,##0} MB budget (last fetch)",
                    usage / (1024d * 1024d), budget / (1024d * 1024d));
            }
            catch (Exception ex)
            {
                _off = true;
                _why = ex.GetType().Name;
                Plugin.LogSource?.LogDebug($"QuestTree: EFT's VRamUsage plugin could not be read ({ex.GetType().Name}: {ex.Message}) - VRAM is not reported this session.");
                return $"VRAM unread ({_why})";
            }
        }
    }
}
