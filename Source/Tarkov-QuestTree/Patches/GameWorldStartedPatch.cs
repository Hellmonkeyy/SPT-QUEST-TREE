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
    ///
    /// OnGameStarted also fires for a trader visit (TarkovApplication's Narrate path, on a NarrateGameWorld
    /// that outlives the visit) and never for the hideout. The Postfix returns at once for the former.
    /// </summary>
    internal class GameWorldStartedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GameWorld), "OnGameStarted");

        /// <summary>Whether the three key watchers (mesh probe, capture, campaign; not the raid watch) are installed only when the map name
        /// is already known at OnGameStarted. True: a local SPT raid sets MainPlayer.Location inside LocalGame.Run,
        /// before the hook (review F58). The rollback switch if a co-op client turns out to set it later.</summary>
        private const bool RequireMapNameAtStart = true;

        [PatchPostfix]
        private static void Postfix(GameWorld __instance)
        {
            // A trader visit (the Narrate scene) fires this same hook on a NarrateGameWorld, which has a registered
            // MainPlayer but no map, and outlives the visit - the watchers installed there kept the mesh probe's
            // raid count up for the rest of the menu session (review F58). Nothing of this mod runs there.
            if (__instance == null || __instance is NarrateGameWorld)
            {
                Plugin.LogSource?.LogDebug(__instance == null
                    ? "QuestTree: OnGameStarted with no GameWorld - nothing installed."
                    : "QuestTree: OnGameStarted on a trader visit - nothing installed.");
                return;
            }

            // The raid watch the menu map host's raid checks read (MenuMapHost.InMenu / RaidStarting). Permanent and the
            // first thing done: on EVERY raid world, with no map-name gate, because a raid the watch missed is a raid the
            // menu host could load a map into - the check must fail closed. Ahead of the HarvestZones gate: whether a raid
            // is running does not depend on whether it is harvested. Not on a HideoutGameWorld, which InMenu allows (it can
            // outlive a hideout visit); the trader visit has returned above. Catches for itself; the try is so nothing it
            // throws can reach the harvest below.
            try
            {
                if (!(__instance is HideoutGameWorld)) RaidWatch.Install(__instance);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the raid watch ({ex.Message}).");
            }

            var hasMap = !string.IsNullOrEmpty(MapName(__instance));
            var watchers = hasMap || !RequireMapNameAtStart;

            if (!watchers)
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: OnGameStarted on a {__instance.GetType().Name} with no map name - the capture, " +
                    "campaign keys are not installed (the raid watch is); the zone harvest still looks for one in a few seconds.");

            // Inside the game's own raid start; nothing thrown here may reach it.
            try
            {
                // Only an explicit "off" stops the harvest: with settings unbound (Plugin.Awake
                // guards that failure separately) the default, on, applies.
                if (ModSettings.Ready && !ModSettings.HarvestZones.Value) return;

                // The capture key, behind the same gate as the harvest itself: the picture is drawn
                // to the rectangle the harvest measures, so a raid that is not harvesting has
                // nothing for a capture to be aligned to. Installs a watcher only - nothing renders
                // until the key is pressed - and guards and catches for itself.
                if (watchers)
                {
                    MapCapture.Install(__instance);

                    // The two ways to capture a whole map without walking it - the campaign key and
                    // automatic capture as you play - which drive that same capture rather than rendering
                    // anything themselves, so they sit behind the same gate. Also a watcher only: nothing
                    // moves and nothing renders until a key is pressed or the setting is on.
                    MapCampaign.Install(__instance);
                }

                // Not gated on the name: it reads it 3 s from now (FirstPassDelay), and a Fika headless client -
                // which has no player at this point - is exactly the harvester this hook is kept for.
                __instance.StartCoroutine(ZoneHarvester.HarvestCoroutine(__instance));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not start the zone harvest ({ex.Message}).");
            }
        }

        /// <summary>The map key the rest of the mod uses: MainPlayer.Location, else GameWorld.LocationId.</summary>
        /// <param name="world">The world that started.</param>
        private static string MapName(GameWorld world)
        {
            try
            {
                var map = world.MainPlayer?.Location;
                return string.IsNullOrEmpty(map) ? world.LocationId : map;
            }
            catch
            {
                return null;
            }
        }
    }
}
