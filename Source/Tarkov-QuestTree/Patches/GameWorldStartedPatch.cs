using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using QuestTree.QuestGraph;
using SPT.Reflection.Patching;

namespace QuestTree.Patches
{
    /// <summary>
    /// Starts the zone harvest once a raid's world is up. GameWorld.OnGameStarted is the hook
    /// DynamicMaps uses for the same purpose, and a Unity lifecycle-shaped method name is the kind
    /// the deobfuscator leaves alone (see the notes on field-name drift).
    ///
    /// Enabled on a Fika headless client too, deliberately: it loads every map anyone plays, so it
    /// is the best harvester there is.
    /// </summary>
    internal class GameWorldStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), "OnGameStarted");

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            // Inside the game's own raid start; nothing thrown here may reach it.
            try
            {
                // Only an explicit "off" stops the harvest: with settings unbound (Plugin.Awake
                // guards that failure separately) the default, on, applies.
                if (ModSettings.Ready && !ModSettings.HarvestZones.Value) return;
                if (__instance == null) return;

                // The capture key, behind the same gate as the harvest itself: the picture is drawn
                // to the rectangle the harvest measures, so a raid that is not harvesting has
                // nothing for a capture to be aligned to. Installs a watcher only - nothing renders
                // until the key is pressed - and guards and catches for itself.
                MapCapture.Install(__instance);

                __instance.StartCoroutine(ZoneHarvester.HarvestCoroutine(__instance));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not start the zone harvest ({ex.Message}).");
            }
        }
    }
}
