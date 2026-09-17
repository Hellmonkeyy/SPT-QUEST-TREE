using System;
using BepInEx;
using BepInEx.Logging;
using QuestTree.Patches;

namespace QuestTree
{
    // The version is ModInfo.Version rather than a literal: BepInEx shows one number, the
    // mismatch message shows another, and four copies of a version string is four chances to
    // ship halves that disagree about which build they are.
    [BepInPlugin("com.takov.questtree", "QuestTree", ModInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        /// <summary>The plugin behaviour itself, for the rare coroutine that has to outlive the
        /// thing it is watching.
        ///
        /// Unity stops a coroutine the moment its host GameObject is deactivated, so anything
        /// that watches for a panel being HIDDEN cannot be hosted on that panel - it would freeze
        /// at exactly the moment it was meant to act. This object is never destroyed.</summary>
        public static Plugin Instance { get; private set; }

        private void Awake()
        {
            LogSource = Logger;
            Instance = this;

            try
            {
                // Inside the guard: a config that fails to bind must not take the whole plugin
                // down with an unhandled throw out of Awake. ModSettings.Ready stays false and the
                // views fall back to their defaults.
                ModSettings.Init(Config);
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuestTree: failed to initialise settings: {ex}");
            }

            try
            {
                // Before the headless check on purpose: a headless client loads every map anyone
                // plays, which makes it the best zone harvester in the group. Its own guard: a
                // config failure above has nothing to do with whether raids can be harvested.
                new GameWorldStartedPatch().Enable();
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuestTree: failed to enable the raid patch: {ex}");
            }

            // A Fika headless client has no player, no UI, and no taskbar to add a button to.
            if (ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuestTree: headless client detected - UI disabled, zone harvest on.");
                return;
            }

            try
            {
                new MenuTaskBarAwakePatch().Enable();
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuestTree: failed to enable the taskbar patch: {ex}");
            }

            try
            {
                // Its own guard: the ready-up screen's button is a convenience, and the shortcut
                // still reaches that screen without it.
                new MatchMakerAcceptScreenPatch().Enable();
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuestTree: failed to enable the pre-raid button patch: {ex}");
            }

            Logger.LogInfo($"QuestTree {ModInfo.Stamp}: loaded.");
        }
    }
}
