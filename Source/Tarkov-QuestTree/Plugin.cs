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
            ModSettings.Init(Config);

            // A Fika headless client has no player, no UI, and no taskbar to add a button to.
            if (ModEnvironment.IsHeadlessClient)
            {
                Logger.LogInfo("QuestTree: headless client detected, plugin disabled.");
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

            Logger.LogInfo("QuestTree: loaded.");
        }
    }
}
