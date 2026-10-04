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
            if (TryRead(out var usageMb, out var budgetMb))
                return string.Format(CultureInfo.InvariantCulture, "VRAM {0:#,##0} of {1:#,##0} MB budget (last fetch)", usageMb, budgetMb);

            return _off ? $"VRAM unread ({_why})" : "VRAM not fetched yet";
        }

        /// <summary>The last fetched local-memory use and budget in MB, and a fresh fetch issued for the next read (so a
        /// caller wanting a current value reads, waits a frame or two, and reads again). False, with -1s, when nothing was
        /// fetched yet or the plugin is off for the session. Main thread; never throws. QuestGraph.SelfTest reads this.</summary>
        /// <param name="usageMb">The GPU's dedicated memory in use, MB.</param>
        /// <param name="budgetMb">The OS budget for it, MB.</param>
        internal static bool TryRead(out double usageMb, out double budgetMb)
        {
            usageMb = -1d;
            budgetMb = -1d;

            if (_off) return false;

            try
            {
                var got = GetMemoryInfo(true, out var budget, out var usage, out _, out _);

                // a fresh fetch for the next read (render thread, after this frame's commands)
                if (_fetch == IntPtr.Zero) _fetch = GetFunc_FetchLocalMemoryInfo();
                if (_fetch != IntPtr.Zero) GL.IssuePluginEvent(_fetch, 0);

                if (!got || budget == 0UL) return false;

                usageMb = usage / (1024d * 1024d);
                budgetMb = budget / (1024d * 1024d);
                return true;
            }
            catch (Exception ex)
            {
                _off = true;
                _why = ex.GetType().Name;
                Plugin.LogSource?.LogDebug($"QuestTree: EFT's VRamUsage plugin could not be read ({ex.GetType().Name}: {ex.Message}) - VRAM is not reported this session.");
                return false;
            }
        }
    }
}
