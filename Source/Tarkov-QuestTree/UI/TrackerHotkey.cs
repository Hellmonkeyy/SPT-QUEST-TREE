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

        private void Update()
        {
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
