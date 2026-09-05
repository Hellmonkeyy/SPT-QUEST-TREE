using System;
using BepInEx;
using BepInEx.Logging;
using QuestTree.Patches;

namespace QuestTree
{
    [BepInPlugin("com.takov.questtree", "QuestTree", "1.0.0")]
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
