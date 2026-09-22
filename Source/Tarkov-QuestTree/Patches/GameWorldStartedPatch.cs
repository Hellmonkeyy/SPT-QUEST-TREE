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
            // THROWAWAY (the 3D map experiments): the mesh probe key, in a try of its OWN. It used to
            // share the block below, which put a debug tool in front of the harvest, the capture and the
            // campaign: anything it threw past its own catch - or in the logging inside that catch -
            // would have taken all three down, and the warning would have blamed the zone harvest.
            //
            // Still first, and still ahead of the HarvestZones gate, because it is not part of the
            // harvest and a raid with the harvest switched off is exactly the raid the experiments are
            // run in. Delete with QuestGraph/MeshProbe.cs.
            try
            {
                MeshProbe.Install(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the mesh probe key ({ex.Message}).");
            }

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

                // The two ways to capture a whole map without walking it - the campaign key and
                // automatic capture as you play - which drive that same capture rather than rendering
                // anything themselves, so they sit behind the same gate. Also a watcher only: nothing
                // moves and nothing renders until a key is pressed or the setting is on.
                MapCampaign.Install(__instance);

                __instance.StartCoroutine(ZoneHarvester.HarvestCoroutine(__instance));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not start the zone harvest ({ex.Message}).");
            }
        }
    }
}
