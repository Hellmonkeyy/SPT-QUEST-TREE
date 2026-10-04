using System;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// Watches for the open-tracker shortcut, everywhere in the menu.
    ///
    /// It has to be its own always-active object, and that is not a style choice: the panel's own
    /// Update cannot open the panel, because Unity does not call Update on an inactive GameObject
    /// and the panel is inactive precisely when it is closed. This lives on a small empty object
    /// under the same root canvas the panel is parented to - a canvas that survives screen changes
    /// and, unlike the taskbar, is never deactivated by a screen controller.
    ///
    /// This is the half of pre-raid access that cannot be defeated by a screen hiding its chrome,
    /// so it is the half that matters. The button on the ready-up screen is the discoverable half.
    /// </summary>
    internal sealed class TrackerHotkey : MonoBehaviour
    {
        /// <summary>Builds the watcher under <paramref name="parent"/>. Active immediately, and
        /// stays that way for the life of the menu.
        ///
        /// Exactly one exists at a time, and that is not tidiness: the menu can be rebuilt within a
        /// session, and two watchers would both see the same keypress, toggling the panel open and
        /// straight back shut in one frame - a shortcut that does nothing at all, which is a far
        /// worse failure than one that is missing.</summary>
        public static TrackerHotkey Create(Transform parent)
        {
            if (_instance != null) Destroy(_instance.gameObject);

            var go = new GameObject("QuestTreeHotkey");
            go.transform.SetParent(parent, worldPositionStays: false);

            _instance = go.AddComponent<TrackerHotkey>();
            return _instance;
        }

        private static TrackerHotkey _instance;

        /// <summary>Stage M3: the live watcher, or null - the behaviour the Maps tab's capture from game files and a menu
        /// upload (MapTransfer.UploadHost) run their coroutines on, because it is proven to tick in the menu. Unity's null
        /// test, so a destroyed one reads as null.</summary>
        internal static TrackerHotkey Current => _instance != null ? _instance : null;

        private void Update()
        {
            // THROWAWAY (the 3D map experiments' probe key): polled from here too, because this Update
            // is proven to run in the menu and the probe's own object's was not seen to. Delete with
            // QuestGraph/MeshProbe.cs.
            QuestGraph.MenuMeshProbe.PollFromHotkey(this);

            // The menu map host's dead-run check (it and the probe below share one run slot): a run whose host died or whose
            // coroutine stopped yielding has its scenes unloaded. First, so a dead run is freed before a key press is read.
            QuestGraph.MenuMapHost.PollFromHotkey(this);

            // THROWAWAY (the menu scene experiment): the menu scene probe's key, hosted here for the same reason. Delete with
            // QuestGraph/MenuSceneProbe.cs.
            QuestGraph.MenuSceneProbe.PollFromHotkey(this);

            // Campaign speed step 2 (review): a capture campaign's last write can finish after its raid, in the menu - this
            // Update is proven to tick there, so the write's main-thread end (the upload release, the Maps tab) runs from here
            // too, not only from the plugin object's coroutine.
            QuestGraph.MapCapture.PollCampaignWrite();

            // Stage M3: the Maps tab's capture from game files - a run's end and capture all's next map - and an upload
            // hosted here whose host was replaced (a menu rebuilt mid-upload). Both after the host's dead-run check above.
            QuestGraph.MenuCaptureRunner.Poll();
            QuestGraph.MapTransfer.CheckUploadHost();

            // The in-game self-test's start switch, cancel and dead-run check. Its runs go on this behaviour, which is
            // proven to tick in the menu. After the host's dead-run check above.
            QuestGraph.SelfTest.Poll(this);

            // Track T stage T0: the map data probe's start switch, cancel and end. After the host's dead-run check above.
            QuestGraph.MapDataProbe.Poll(this);

            if (!ModSettings.Ready) return;

            var shortcut = ModSettings.OpenTracker.Value;
            if (shortcut.MainKey == KeyCode.None) return;

            try
            {
                if (!shortcut.IsDown()) return;

                // The panel's own search box swallows typing, and a shortcut with a modifier cannot
                // be typed into it - but the shortcut is the player's to rebind, and a bare letter
                // would otherwise close the panel mid-search.
                if (TrackerAccess.IsOpen && TrackerAccess.Panel.IsTyping) return;

                TrackerAccess.Toggle();
            }
            catch (Exception ex)
            {
                // Never let a per-frame poll spam the log or take the menu down with it.
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the open-tracker shortcut failed ({ex.Message}).");
            }
        }

        private bool _warned;
    }
}
