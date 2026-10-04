using System;
using EFT;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// "Is a raid running?" asked without trusting the game to register anything. One of these is put on every raid's
    /// GameWorld by <see cref="QuestTree.Patches.GameWorldStartedPatch"/> and dies with it, so <see cref="Live"/> is true
    /// from GameWorld.OnGameStarted until that world is torn down.
    ///
    /// What it guards: <see cref="MenuMapHost.InMenu"/> and <see cref="MenuMapHost.RaidStarting"/> (and through them the
    /// menu map build, the menu scene probe and the self-test's raid cancel) must never load a map's scenes into a raid,
    /// and must unload at once when one starts. Those checks also ask Singleton&lt;GameWorld&gt; and look for GameWorld
    /// objects; this is their second, independent test, which depends on nothing the game does beyond calling
    /// OnGameStarted and destroying the world afterwards.
    ///
    /// Why a file of its own: the count used to live on the throwaway mesh probe's own raid watcher, which is to be
    /// deleted with the in-raid capture. This must not depend on MeshProbe, MapCapture or MapCampaign.
    ///
    /// Installed on every raid world, map name known at OnGameStarted or not: a raid the watch missed is a raid the menu
    /// host could load a map into, so it fails closed. The mesh probe's old watcher was skipped when the map name was not
    /// yet known; this one is not. Not on a trader visit (NarrateGameWorld - the patch returns first), not on a
    /// HideoutGameWorld (which InMenu allows: it can outlive a hideout visit), not on a headless client (which has no menu
    /// to guard).
    /// </summary>
    internal sealed class RaidWatch : MonoBehaviour
    {
        // BEGIN TESTABLE RaidWatch
        /// <summary>How many raid watches are enabled - 0 in the menu, 1 in a raid.</summary>
        private static int _alive;

        /// <summary>Whether a raid GameWorld's watch is alive: a raid has started and its world is not yet torn down.</summary>
        internal static bool Live => _alive > 0;

        private static void Enter() => _alive++;

        /// <summary>Never below 0: an extra disable must not leave a later raid's enable reading as "no raid".</summary>
        private static void Leave()
        {
            if (_alive > 0) _alive--;
        }
        // END TESTABLE RaidWatch

        /// <summary>Puts a watch on the world that just started. Catches for itself: it runs inside the game's raid start.</summary>
        /// <param name="gameWorld">The raid's world; the watch is its child and is destroyed with it.</param>
        internal static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeRaidWatch");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);
                go.AddComponent<RaidWatch>();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the raid watch ({ex.Message}).");
            }
        }

        // OnEnable/OnDisable, not Awake/OnDestroy: Unity calls OnDisable before OnDestroy whenever an enabled component is
        // destroyed (scene unload included), and these pair exactly if the object is ever deactivated and reactivated.
        private void OnEnable() => Enter();

        private void OnDisable() => Leave();
    }
}
