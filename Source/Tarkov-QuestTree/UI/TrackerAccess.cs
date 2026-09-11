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

        /// <summary>How many frames to watch for the panel being hidden behind an inspect
        /// window. Whatever hides it does so within a frame or two of the window opening; watching
        /// longer would risk fighting a player who closed the tracker themselves.</summary>
        private const int InspectWatchFrames = 6;

        /// <summary>Keeps the tracker on screen across an item inspect.
        ///
        /// Opening the game's inspect window from an item row leaves the window correct and the
        /// tracker GONE - the player is dropped onto the bare main menu with a knife tooltip over
        /// it. Nothing throws and nothing is logged, so the panel is being deactivated by something
        /// else: EFT's own window stack, or one of the menu-restyling mods this install runs.
        ///
        /// Which one is not knowable by reading code - Unity scene behaviour never is, and this mod
        /// has shipped two wrong guesses about exactly that. So this does not try to PREVENT the
        /// hide. It notices and undoes it, which works whatever the cause.
        ///
        /// Hosted on Plugin rather than on the panel, because a coroutine stops dead when its own
        /// GameObject is deactivated - hosting the watcher on the thing being hidden would freeze it
        /// at the one moment it exists to act.</summary>
        public static void KeepOpenThroughInspect()
        {
            var panel = Panel;
            if (panel == null) return;

            // Already closed: the player is not looking at the tracker, so there is nothing to
            // restore and re-showing it would be the mod opening itself uninvited.
            if (!panel.gameObject.activeSelf) return;

            var host = Plugin.Instance;
            if (host == null || !host.isActiveAndEnabled) return;

            host.StartCoroutine(RestoreIfHidden(panel));
        }

        private static System.Collections.IEnumerator RestoreIfHidden(QuestTreePanel panel)
        {
            for (var frame = 1; frame <= InspectWatchFrames; frame++)
            {
                yield return null;

                // Destroyed while we waited - a menu teardown. Nothing to put back.
                if (panel == null) yield break;
                if (panel.gameObject.activeSelf) continue;

                panel.gameObject.SetActive(true);

                // Deliberately NOT SetAsLastSibling. The panel is a full-screen dark overlay, so
                // raising it above the inspect window would hide the very thing the player just
                // opened - a worse bug than the one being fixed. Behind the window is where it
                // belongs.
                if (!_inspectRestoreLogged)
                {
                    _inspectRestoreLogged = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the tracker was hidden {frame} frame(s) after an item inspect and " +
                        "has been restored behind the inspect window. If the tracker now looks wrong " +
                        "rather than merely restored, this line is the place to start.");
                }

                yield break;
            }

            // Never hidden this session: the restore is not needed on this install, and saying so
            // is what tells the difference between "fixed" and "never happened here".
            if (!_inspectRestoreLogged && !_inspectIntactLogged)
            {
                _inspectIntactLogged = true;
                Plugin.LogSource?.LogInfo(
                    "QuestTree: the tracker stayed open across an item inspect - no restore needed.");
            }
        }

        private static bool _inspectRestoreLogged;
        private static bool _inspectIntactLogged;

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
