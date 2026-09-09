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
    }
}
