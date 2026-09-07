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

        private void Awake()
        {
            LogSource = Logger;

            try
            {
                // Inside the guard: a config that fails to bind must not take the whole plugin
                // down with an unhandled throw out of Awake. ModSettings.Ready stays false and the
                // views fall back to their defaults.
                ModSettings.Init(Config);

                // Before the headless check on purpose: a headless client loads every map anyone
                // plays, which makes it the best zone harvester in the group.
                new GameWorldStartedPatch().Enable();
            }
            catch (Exception ex)
            {
                Logger.LogError($"QuestTree: failed to initialise settings or the raid patch: {ex}");
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

            Logger.LogInfo($"QuestTree {ModInfo.Stamp}: loaded.");
        }
    }
}
