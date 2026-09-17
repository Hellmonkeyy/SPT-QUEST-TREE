using UnityEngine;
using System;
using EFT;
using SPT.Reflection.Utils;

namespace QuestTree.UI
{
    /// <summary>
    /// The one way in and out of the tracker panel, and the one place that knows where the panel is.
    ///
    /// It exists because the taskbar button stopped being the only way to open the tracker. The
    /// taskbar is hidden outright on the two matchmaker screens - every screen controller carries a
    /// MenuChatBarVisibility, and those two override it to Disabled, which does a SetActive(false)
    /// on the whole bar - so the button that lives there cannot help on the screen where "what am I
    /// meant to do on this map" is the most useful question in the game. A hotkey and a button on
    /// that screen both need to open the same panel the taskbar opens, and none of the three should
    /// know how the others work.
    /// </summary>
    internal static class TrackerAccess
    {
        /// <summary>The live panel, set when the taskbar patch builds it. Null before the main menu
        /// has been reached, and again if the menu that owned it was torn down.</summary>
        public static QuestTreePanel Panel { get; set; }

        /// <summary>Whether the panel exists and is on screen. Unity's == is the only null test a
        /// destroyed object answers to.</summary>
        public static bool IsOpen => Panel != null && Panel.gameObject.activeSelf;

        /// <summary>Opens the panel, or closes it if it is already up - what every entry point
        /// wants. Never throws: it is called from a Unity Update and from inside the game's own
        /// screen code.</summary>
        public static void Toggle()
        {
            try
            {
                if (Panel == null)
                {
                    // The panel died with an earlier menu and something outlived it; said once
                    // rather than an error per press.
                    if (!_deadPanelWarned)
                    {
                        _deadPanelWarned = true;
                        Plugin.LogSource?.LogWarning(
                            "QuestTree: the Quest Tracker panel is gone - reopen the menu to get a new one.");
                    }

                    return;
                }

                if (Panel.gameObject.activeSelf) Panel.HideGameObject();
                else Show();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: opening the Quest Tracker failed: {ex}");
            }
        }

        /// <summary>How many frames to give the game to create its window before giving up.</summary>
        private const int InspectWatchFrames = 6;

        /// <summary>Gets the tracker out of the way of the item inspect window.
        ///
        /// The first two attempts at this were built on a wrong premise, and the log line they
        /// carried is what corrected it: "the tracker stayed open across an item inspect". The panel
        /// is NEVER hidden. The window simply renders underneath it, because TrackerAccess.Show
        /// calls SetAsLastSibling and parks the panel above EFT's own window layer - so all the
        /// restore-if-hidden machinery was solving a problem that did not exist.
        ///
        /// The window is an EFT.UI.InfoWindow, instantiated from ItemUiContext's template. It is not
        /// ours to raise - it lives in the game's own window stack - so the panel moves DOWN to meet
        /// it instead: walk up from the window until an ancestor shares the panel's parent, then
        /// take that ancestor's sibling index.
        ///
        /// If no shared ancestor exists the window is under some other canvas entirely, and then the
        /// panel is HIDDEN rather than left covering it. The player clicked to see an item; a
        /// tracker that swallows it is the one outcome that is not acceptable.</summary>
        public static void KeepOpenThroughInspect()
        {
            var panel = Panel;
            if (panel == null || !panel.gameObject.activeSelf) return;

            var host = Plugin.Instance;
            if (host == null || !host.isActiveAndEnabled) return;

            host.StartCoroutine(MoveBelowInspectWindow(panel));
        }

        private static System.Collections.IEnumerator MoveBelowInspectWindow(QuestTreePanel panel)
        {
            for (var frame = 1; frame <= InspectWatchFrames; frame++)
            {
                yield return null;

                if (panel == null) yield break;

                var parent = panel.transform.parent;
                if (parent == null) yield break;

                var window = FindInspectWindow();
                if (window == null) continue;   // not built yet; look again next frame

                // Up from the window to whichever ancestor is our own sibling.
                var ancestor = window;
                while (ancestor != null && ancestor.parent != parent) ancestor = ancestor.parent;

                if (ancestor == null)
                {
                    // Different canvas. Nothing about sibling order can help, so get out of the way
                    // the only other way available.
                    panel.HideGameObject();

                    LogInspect(
                        "QuestTree: the item inspect window is not a sibling of the tracker, so the " +
                        "tracker has been hidden to keep the window readable. Reopen it with the " +
                        "taskbar button or the hotkey.");

                    yield break;
                }

                var windowIndex = ancestor.GetSiblingIndex();
                if (panel.transform.GetSiblingIndex() > windowIndex)
                {
                    // Below it. SetSiblingIndex shifts the window up by one, which is the intent:
                    // the thing the player clicked stays readable and the tracker sits behind it.
                    panel.transform.SetSiblingIndex(windowIndex);
                }

                LogInspect(
                    $"QuestTree: the item inspect window appeared {frame} frame(s) after the click and " +
                    "the tracker has been moved behind it.");

                yield break;
            }

            LogInspect(
                "QuestTree: no item inspect window appeared after a click on an item row - the tracker " +
                "has been left as it is.");
        }

        /// <summary>The live inspect window, or null while it is still being built.
        ///
        /// FindObjectsOfType is not cheap, and that is fine here: this runs once per click on an
        /// item row, not per frame, and only until the window is found.</summary>
        private static Transform FindInspectWindow()
        {
            EFT.UI.InfoWindow best = null;

            foreach (var window in UnityEngine.Object.FindObjectsOfType<EFT.UI.InfoWindow>())
            {
                if (window == null || !window.gameObject.activeInHierarchy) continue;
                best = window;
            }

            return best != null ? best.transform : null;
        }

        /// <summary>One line a session about the inspect interaction, whichever outcome happened.
        /// It is what corrected the premise this method was first built on.</summary>
        private static void LogInspect(string message)
        {
            if (_inspectLogged) return;
            _inspectLogged = true;

            Plugin.LogSource?.LogInfo(message);
        }

        private static bool _inspectLogged;

        private static bool _deadPanelWarned;
        private static bool _raidLocationWarned;

        /// <summary>Opens the panel against the live session. Was private to the taskbar patch.</summary>
        public static void Show()
        {
            var panel = Panel;
            if (panel == null) return;

            var app = ClientAppUtils.GetMainApp();
            var mainMenu = Compat.Get<MainMenuShowOperation>(app);

            if (mainMenu?.QuestController == null || mainMenu.iEftSession == null)
            {
                Plugin.LogSource?.LogWarning("QuestTree: session/quest data not available yet.");
                return;
            }

            // Caught here, not inside RaidLocationOf: a member that a game update has renamed
            // fails when the method that names it is JIT-compiled, i.e. at the call, so a try
            // inside that method would never see it. The cue is the only thing at stake.
            string raidLocation = null;
            try
            {
                raidLocation = RaidLocationOf(app);
            }
            catch (Exception ex)
            {
                if (!_raidLocationWarned)
                {
                    _raidLocationWarned = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: could not read the selected raid location ({ex.GetType().Name}: {ex.Message}).");
                }
            }

            panel.Show(mainMenu.QuestController, mainMenu.iEftSession, raidLocation);
            panel.transform.SetAsLastSibling();
        }

        /// <summary>The map picked on the matchmaker screen, by its internal name ("bigmap",
        /// "factory4_night"), or null from the main menu before one is picked. This is why opening
        /// the tracker from the ready-up screen is worth the trouble: it is the one moment the mod
        /// knows exactly which map you are about to load.</summary>
        private static string RaidLocationOf(TarkovApplication app) =>
            app?.CurrentRaidSettings?.SelectedLocation?.Id;

        /// <summary>The selected map, for callers outside this class - the pre-raid cue needs the
        /// same answer the panel opens on, and reading it twice in two ways is how the button and
        /// the panel would come to disagree about which map you are looking at.
        ///
        /// Guarded the same way and for the same reason: a renamed member fails when the naming
        /// method is JIT-compiled, so the try has to sit at the call rather than inside it.</summary>
        public static string CurrentRaidLocation()
        {
            try
            {
                return RaidLocationOf(ClientAppUtils.GetMainApp());
            }
            catch (Exception ex)
            {
                if (!_raidLocationWarned)
                {
                    _raidLocationWarned = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: could not read the selected raid location ({ex.GetType().Name}: {ex.Message}).");
                }

                return null;
            }
        }

        /// <summary>Whether the raid being readied is a scav run.
        ///
        /// The raid check reads the PMC profile, so a scav run would be judged against gear the
        /// player is not taking - a green light on a rig they do not have. On a scav raid the cue
        /// says nothing instead. Scav runs carry no quest objectives, so nothing useful is lost, and
        /// neutral is honest where green would be a lie.</summary>
        public static bool IsCurrentRaidScav()
        {
            try
            {
                var side = ClientAppUtils.GetMainApp()?.CurrentRaidSettings?.Side;
                return side == ESideType.Savage;
            }
            catch (Exception ex)
            {
                if (!_sideWarned)
                {
                    _sideWarned = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: could not read the raid side ({ex.GetType().Name}: {ex.Message}) - " +
                        "the pre-raid cue will treat it as a PMC raid.");
                }

                return false;
            }
        }

        private static bool _sideWarned;
    }
}
