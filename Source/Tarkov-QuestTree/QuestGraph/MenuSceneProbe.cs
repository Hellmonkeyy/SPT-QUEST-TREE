using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using EFT.Settings.Graphics;
using EFT.UI.Screens;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using static QuestTree.QuestGraph.MenuMapHost;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// THROWAWAY DIAGNOSTIC - the menu scene experiment. Can a raid map's built-in scenes be loaded ADDITIVELY while the
    /// player sits in the main menu (no raid, no GameWorld), and would the capture then see the whole map with nothing
    /// streamed out? This answers the first half only: it loads the listed build indices, measures what arrived, and
    /// unloads them again. It captures nothing. Delete this file with <see cref="ModSettings.MenuSceneProbeKey"/>,
    /// <see cref="ModSettings.MenuSceneProbeLevels"/>, <see cref="ModSettings.MenuMapHostLocation"/>,
    /// <see cref="ModSettings.MenuCaptureLocation"/> (the stage M2a hook: host and capture) and the one line in
    /// TrackerHotkey.Update. Its proven machinery (the menu gate, the state snapshot/restore, the DontDestroyOnLoad diff,
    /// the dead-run check, the Streamer switch-off, the message counter, the per-scene counts) now lives in
    /// <see cref="MenuMapHost"/>, which this calls; with <see cref="ModSettings.MenuMapHostLocation"/> set, the key runs the
    /// host on that location instead of the level list.
    ///
    /// Hosted on TrackerHotkey (a child of the tracker's root canvas), because a plugin-created DontDestroyOnLoad object
    /// never ticks in EFT's menu (memory: ddol-objects-dead-in-menu). It reads nothing off a mesh buffer and writes no
    /// buffer target (memory: never-write-mesh-buffer-targets).
    ///
    /// What EFT itself does (decompiled 4.1.6, read-only): TarkovApplication.LoadMapAndData creates the GameWorld and runs
    /// its InitLevel FIRST, then LoadScenesFromPresetOperation loads the location's ScenesPreset (maps/&lt;x&gt;_preset.bundle)
    /// with loadFirstAsSingle: true - the preset's first scene REPLACES every loaded scene, the menu's included - and the
    /// rest additively, one after the other, in the preset's order, then sets the preset's ActiveSceneName active, and
    /// only then calls LevelSettings.OnPostLoadingScene (which reaches Singleton&lt;GameWorld&gt;),
    /// PerfectCullingCrossSceneSampler.InitializeAutoCulling, PhysicsExtensions.Worlds.Create,
    /// StaticDeferredDecalRenderer.UpdateInstancesBuffers, AmbientLight.RuntimeOptimizePrepare and the spatial audio.
    /// None of that runs here: this probe loads bare scenes, so every script in them wakes up with no GameWorld, and the
    /// map never shares the process with the menu in EFT's own flow. The menu's own 3D background IS loaded additively
    /// (EnvironmentUI.ScheduleEnvironmentLoading), so an additive scene in the menu is not foreign to the game as such.
    ///
    /// State a map scene changes that its own teardown does NOT put back, found in the code and restored here:
    /// LevelSettings.Awake writes RenderSettings (ambient, fog, skybox), the texture streaming budget
    /// (QualitySettings.streamingMipmapsMemoryBudget / MaxLevelReduction) and five shader globals, and its OnDestroy
    /// releases Singleton&lt;LevelSettings&gt; UNCONDITIONALLY (Comfort's Release nulls whatever is there);
    /// LocationScene.Awake adds door colliders to the static DoorsCollisionColliders and never removes them.
    ///
    /// Whether the map is DRAWN in the menu is not in the code: the menu camera is the environment scene's own camera
    /// (EnvironmentUIRoot.CameraContainer) and its culling mask is scene data. So it is measured - every enabled camera's
    /// mask and frustum against the loaded renderers, and Renderer.isVisible two frames after the load.
    /// </summary>
    internal static class MenuSceneProbe
    {
        private const string Tag = "QuestTree: [menu scene probe] ";

        private const int MaxLevels = 64;

        /// <summary>Only raid-map scenes: never the main scene, a UI scene or the hideout.</summary>
        private const string AllowedPathPrefix = "Assets/Content/Locations/";

        private static int _press;
        private static int _handledFrame = -1;
        private static bool _warned;
        private static bool _alive;

        /// <summary>Build indices this probe asked to load and has not yet seen unloaded - what an emergency unload has to
        /// take down when the coroutine dies without its finally.</summary>
        private static readonly List<int> Loaded = new List<int>();

        /// <summary>Polled from TrackerHotkey.Update, which is proven to tick in the menu. Never throws.</summary>
        internal static void PollFromHotkey(MonoBehaviour host)
        {
            if (host == null || _handledFrame == Time.frameCount) return;

            try
            {
                // A dead run (its host destroyed, or its coroutine no longer yielding) is caught by MenuMapHost.PollFromHotkey,
                // which runs just before this in TrackerHotkey.Update and calls OnDead.

                if (!ModSettings.Ready || ModSettings.MenuSceneProbeKey == null) return;

                var shortcut = ModSettings.MenuSceneProbeKey.Value;

                // Once, so the log proves the watcher runs and says which key and levels are live.
                if (!_alive)
                {
                    _alive = true;
                    var bound = ModSettings.KeyText(shortcut, " + ");
                    Log($"polling from {host.GetType().Name}; key {(string.IsNullOrEmpty(bound) ? "unbound" : bound)}, " +
                        $"levels \"{ModSettings.MenuSceneProbeLevels?.Value}\", {SceneManager.sceneCountInBuildSettings} scenes in the build list.");

                    var capture = ModSettings.MenuCaptureLocation?.Value?.Trim();
                    if (!string.IsNullOrEmpty(capture))
                        Log($"menu capture location \"{capture}\" is set: the key hosts and captures that map instead of the levels.");

                    var hosted = ModSettings.MenuMapHostLocation?.Value?.Trim();
                    if (!string.IsNullOrEmpty(hosted))
                        Log($"menu map host location \"{hosted}\" is set: the key runs MenuMapHost on it instead of the levels.");
                }

                if (shortcut.MainKey == KeyCode.None) return;
                if (!ModSettings.ShortcutDown(shortcut)) return;

                _handledFrame = Time.frameCount;

                if (Busy)
                {
                    Log("ignored a press: a run is still going.");
                    return;
                }

                if (!InMenu(out var why))
                {
                    Log($"refused: {why}. It runs in the main menu only.");
                    return;
                }

                if (RestartAdvised != null)
                {
                    Log($"refused: an earlier run could not restore the menu ({RestartAdvised}). RESTART THE GAME before probing again.");
                    return;
                }

                // THROWAWAY test hook for stage M2a: a location id here hosts that map AND captures it while it is loaded.
                // Wins over the stage M1 hook below.
                var captured = ModSettings.MenuCaptureLocation?.Value?.Trim();
                if (!string.IsNullOrEmpty(captured))
                {
                    // Before anything loads (re-review): under a GameWorld the hosted scenes would register into it.
                    if (MenuMapHost.WorldSet(out var world))
                    {
                        Log($"the menu capture refused '{captured}': {MenuMapHost.WorkRefusal(world)}. Nothing was loaded.");
                        return;
                    }

                    var key =MenuMapHost.LocationKey(captured, out var noKey);
                    if (key == null)
                    {
                        Log($"the menu capture refused '{captured}': {noKey}.");
                        return;
                    }

                    var session = new MapCapture.MenuSession(captured, key);
                    Log($"running a menu capture of '{captured}' (location {key}, capture key {session.Key}) instead of the level list.");
                    if (!MenuMapHost.Start(host, captured, () => MapCapture.RunMenuCapture(session), out var refused))
                        Log($"the menu capture refused: {refused}.");
                    return;
                }

                // THROWAWAY test hook for stage M1: a location id here runs the menu map host on it (nothing done while
                // loaded) instead of the level list.
                var location = ModSettings.MenuMapHostLocation?.Value?.Trim();
                if (!string.IsNullOrEmpty(location))
                {
                    Log($"running the menu map host on '{location}' instead of the level list.");
                    if (!MenuMapHost.Start(host, location, null, out var refusal)) Log($"the menu map host refused: {refusal}.");
                    return;
                }

                var levels = ValidLevels(ModSettings.MenuSceneProbeLevels?.Value);
                if (levels.Count == 0)
                {
                    Log("nothing to load - no listed level passed the checks above.");
                    return;
                }

                var claim = TryClaim(Tag, host, OnDead);
                if (claim == 0)
                {
                    Log("ignored a press: a run is still going.");
                    return;
                }

                _press++;

                try
                {
                    host.StartCoroutine(Run(_press, levels, claim));
                }
                catch (Exception ex)
                {
                    Release(claim);
                    StopCapture();
                    Log($"could not start its coroutine on {host.GetType().Name} ({ex.GetType().Name}: {ex.Message}).");
                }
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"{Tag}the key failed ({ex.GetType().Name}: {ex.Message}) at {FirstFrame(ex.StackTrace)}");
            }
        }

        // --- the run -----------------------------------------------------------------------------------------------------

        private sealed class Level
        {
            internal int Index;
            internal string Path;
        }

        /// <summary>Loads each level additively, one at a time, measuring each; measures them together; unloads them all;
        /// sweeps unused assets; restores the global state the scenes changed; and says whether the menu is as it was.
        /// Every non-yield step is guarded (C# allows no yield in a try with a catch), and the try/finally takes down
        /// whatever is still loaded if the body ends early.</summary>
        private static IEnumerator Run(int press, List<Level> levels, int claim)
        {
            var total = Stopwatch.StartNew();
            var completed = false;
            string abort = null;
            var trouble = new List<string>();
            MenuState before = null;
            var pending = new List<KeyValuePair<Level, AsyncOperation>>();
            var measured = new List<KeyValuePair<Level, Scene>>();

            StartCapture();

            try
            {
                Log($"press {press}: {levels.Count} level(s) [{string.Join(", ", levels.Select(l => l.Index))}]. " +
                    "Do not click anything until the FINISHED line.");

                Try("the DontDestroyOnLoad baseline", () => Log($"DontDestroyOnLoad roots before: {TakeDdolBaseline()}."));

                Try("the baseline", () =>
                {
                    before = MenuState.Take();
                    Log($"baseline: {before.Describe()}");
                    Log($"baseline memory: {Memory()}");
                });

                Try("the MapCapture dependency list", LogCaptureDependencies);

                foreach (var level in levels)
                {
                    if (!InMenu(out var why))
                    {
                        abort = $"no longer in the menu before level {level.Index} ({why})";
                        break;
                    }

                    var memBefore = Memory();
                    var errorsBefore = ErrorCounts();
                    var clock = Stopwatch.StartNew();
                    AsyncOperation op = null;

                    if (!Try($"LoadSceneAsync({level.Index})", () => op = SceneManager.LoadSceneAsync(level.Index, LoadSceneMode.Additive)) ||
                        op == null)
                    {
                        trouble.Add($"level {level.Index} did not start loading");
                        Log($"level {level.Index}: LoadSceneAsync returned no operation - skipped.");
                        continue;
                    }

                    Loaded.Add(level.Index);

                    var timedOut = false;
                    while (!op.isDone)
                    {
                        if (clock.Elapsed.TotalSeconds > TimeoutSeconds) { timedOut = true; break; }
                        if (RaidStarting()) { abort = $"a GameWorld appeared while level {level.Index} was loading"; break; }
                        yield return Tick();
                    }

                    if (timedOut || abort != null)
                    {
                        if (timedOut)
                        {
                            abort = $"level {level.Index} did not finish loading in {TimeoutSeconds:0} s (progress " +
                                    $"{op.progress.ToString("0.00", CultureInfo.InvariantCulture)})";
                        }

                        pending.Add(new KeyValuePair<Level, AsyncOperation>(level, op));
                        break;
                    }

                    var loadMs = clock.ElapsedMilliseconds;

                    // A Streamer loads and unloads further scenes around the camera from its own Update - scenes this probe
                    // would not know to unload. Switched off before it gets a frame.
                    Try($"disabling level {level.Index}'s streamers",
                        () => DisableStreamers(SceneManager.GetSceneByBuildIndex(level.Index), $"level {level.Index}"));

                    // Two frames: Start() runs on the first, and a camera has rendered (so Renderer.isVisible means
                    // something) by the second.
                    yield return Tick();
                    yield return Tick();

                    var scene = default(Scene);
                    Try($"GetSceneByBuildIndex({level.Index})", () => scene = SceneManager.GetSceneByBuildIndex(level.Index));

                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        trouble.Add($"level {level.Index} reported done but is not a loaded scene");
                        Log($"level {level.Index}: the load reported done in {loadMs} ms but the scene is not loaded.");
                        continue;
                    }

                    measured.Add(new KeyValuePair<Level, Scene>(level, scene));
                    Try($"measuring level {level.Index}",
                        () => MeasureScene($"level {level.Index}", level.Path, scene, loadMs, memBefore, errorsBefore));
                }

                if (abort != null) Log($"STOPPED LOADING: {abort}.");

                if (measured.Count > 0 && abort == null)
                    Try("the combined measurement", () => MeasureTogether(measured.Select(m => m.Value).ToList(), before));

                // --- unload --------------------------------------------------------------------------------------------

                foreach (var entry in pending)
                {
                    var clock = Stopwatch.StartNew();
                    Log($"waiting up to {TimeoutSeconds:0} s more for level {entry.Key.Index}'s load to end, so it can be unloaded.");
                    while (!entry.Value.isDone && clock.Elapsed.TotalSeconds < TimeoutSeconds) yield return Tick();

                    if (!entry.Value.isDone)
                        trouble.Add($"level {entry.Key.Index}'s load never finished, so it could not be unloaded");
                }

                for (var i = Loaded.Count - 1; i >= 0; i--)
                {
                    var index = Loaded[i];
                    var scene = default(Scene);
                    Try($"GetSceneByBuildIndex({index})", () => scene = SceneManager.GetSceneByBuildIndex(index));

                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        Log($"unload level {index}: not loaded (any more) - nothing to do.");
                        Loaded.RemoveAt(i);
                        continue;
                    }

                    var clock = Stopwatch.StartNew();
                    AsyncOperation op = null;

                    if (!Try($"UnloadSceneAsync({index})", () => op = SceneManager.UnloadSceneAsync(scene)) || op == null)
                    {
                        trouble.Add($"level {index} could not be unloaded (no operation)");
                        continue;
                    }

                    while (!op.isDone && clock.Elapsed.TotalSeconds < TimeoutSeconds) yield return Tick();

                    if (!op.isDone)
                    {
                        trouble.Add($"level {index}'s unload did not finish in {TimeoutSeconds:0} s");
                        continue;
                    }

                    Loaded.RemoveAt(i);
                    Log($"unloaded level {index} in {clock.ElapsedMilliseconds} ms. {Memory()}");
                }

                yield return Tick();
                Log($"after the unloads: {Memory()}");

                if (RaidStarting())
                {
                    // A raid is loading: its first scene loads Single and takes ours with it, and a sweep or a restore now
                    // would land on the raid's own state (its LevelSettings writes the same RenderSettings).
                    trouble.Add("a raid started loading during the run - the asset sweep and the restore were skipped");
                }
                else
                {
                    var clock = Stopwatch.StartNew();
                    AsyncOperation sweep = null;
                    Try("Resources.UnloadUnusedAssets", () => sweep = Resources.UnloadUnusedAssets());

                    if (sweep != null)
                    {
                        while (!sweep.isDone && clock.Elapsed.TotalSeconds < TimeoutSeconds) yield return Tick();
                        if (!sweep.isDone) trouble.Add($"Resources.UnloadUnusedAssets did not finish in {TimeoutSeconds:0} s");
                    }

                    Try("the garbage collection", () => MapCapture.CollectGarbage("after the menu scene probe", force: true));
                    Log($"after UnloadUnusedAssets ({clock.ElapsedMilliseconds} ms) and a collection: {Memory()}");
                }

                if (before != null && !RaidStarting())
                {
                    Try("the DontDestroyOnLoad leak check", () => SweepDdol(trouble));
                    Try("restoring the global state", () => before.Restore(trouble));
                    yield return Tick();
                    Try("the final comparison", () =>
                    {
                        var after = MenuState.Take();
                        Log($"after: {after.Describe()}");
                        foreach (var change in before.Differences(after)) trouble.Add($"still different: {change}");
                    });
                }
                else if (before == null)
                {
                    trouble.Add("no baseline was taken, so nothing could be compared or restored");
                }

                if (Loaded.Count > 0) trouble.Add($"level(s) {string.Join(", ", Loaded)} are still loaded");

                completed = true;
            }
            finally
            {
                StopCapture();

                if (!completed)
                {
                    trouble.Add("the run ended early (an exception escaped, or Unity stopped the coroutine)");
                    EmergencyUnload("the run ended early");
                }

                try
                {
                    LogMessages("the probe's own steps");

                    if (trouble.Count == 0)
                    {
                        Log($"FINISHED press {press} in {total.ElapsedMilliseconds} ms - every probed scene is unloaded and " +
                            "the compared state matches (scenes, LocationScene lists, LevelSettings and EnvironmentManager instances, " +
                            "RenderSettings ambient/probe/fog/skybox/sun, texture streaming and shadow distance, shader globals, " +
                            "DontDestroyOnLoad roots). State outside that list is not proven.");
                    }
                    else
                    {
                        RestartAdvised = trouble[0];
                        Log($"FINISHED press {press} in {total.ElapsedMilliseconds} ms - RESTART THE GAME ADVISED: " +
                            string.Join("; ", trouble) + ".");
                    }
                }
                catch (Exception)
                {
                    // The verdict line is the last thing; nothing may keep the run slot from freeing.
                }

                Release(claim);
            }
        }

        /// <summary>Told by <see cref="MenuMapHost.PollFromHotkey"/> that this probe's run died without its cleanup (Unity
        /// stops a coroutine whose host is destroyed WITHOUT running its finally, so a menu rebuilt mid-run would leave scenes
        /// loaded); the slot is already free and the log capture stopped.</summary>
        private static void OnDead(string hostState, int framesAgo)
        {
            Log($"the probe's coroutine is dead (host {hostState}, last yield {framesAgo} frame(s) ago) - it died without its cleanup.");
            EmergencyUnload("the probe's coroutine died mid-run");
        }

        /// <summary>What MapCapture needs, read from its code (1.19.0 testing branch), so phase 2 can be planned.</summary>
        private static void LogCaptureDependencies()
        {
            Log("MapCapture could run here: NO, not as written. Hard dependencies: " +
                "(1) the MapCapture component exists only when GameWorldStartedPatch calls MapCapture.Install(gameWorld), parented " +
                "to the GameWorld, and TryStartCapture finds it by FindObjectOfType; " +
                "(2) TryStartCapture and the key refuse unless PlayerIsAlive - GameWorld.MainPlayer.HealthController.IsAlive; " +
                "(3) Prepare's MapKey is MainPlayer.Location or GameWorld.LocationId, no key = no capture; " +
                "(4) the extent comes from MapExtentProbe (the harvest's memo, else BorderZones via LocationScene, " +
                "Terrain.activeTerrains and NavMesh.CalculateTriangulation) and its FLOORS from the NavMesh height histogram - no " +
                "NavMesh, no floors, no capture.");
            Log("MapCapture soft dependencies (degrade, do not refuse): BuildReach - NavMesh (null = no walkable mask); CapturePoint - " +
                "the player's position (else world origin); TimeOfDay - GameWorld.GameDateTime (else empty); BuildCamera - copies " +
                "CameraManager.instance.Camera or Camera.main (else a bare camera on the default path); Labels - ExfiltrationPoint and " +
                "BotZone via LocationScene.GetAll; CollectCulling - DisablerCullingObject and PerfectCulling groups; the 3D mesh's " +
                "SceneCache is keyed on the GameWorld object; MapCampaign moves the player and uses NavMesh.SamplePosition and the " +
                "AbstractGame timer. See the 'together' line for which of these inputs arrive with the scenes.");
        }

        /// <summary>The listed build indices that are in the build list, are raid-map scenes and are not loaded yet. Each
        /// one's path is logged, with the reason for any refusal.</summary>
        private static List<Level> ValidLevels(string text)
        {
            var result = new List<Level>();
            var count = SceneManager.sceneCountInBuildSettings;
            var seen = new HashSet<int>();

            foreach (var token in (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    Log($"refused '{token}': not a whole number.");
                    continue;
                }

                if (!seen.Add(index)) continue;

                if (index < 0 || index >= count)
                {
                    Log($"refused level {index}: outside the build list (0..{count - 1}).");
                    continue;
                }

                var path = SceneUtility.GetScenePathByBuildIndex(index) ?? "";

                if (!path.StartsWith(AllowedPathPrefix, StringComparison.OrdinalIgnoreCase) ||
                    path.IndexOf("hideout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log($"refused level {index} = '{path}': only raid-map scenes under {AllowedPathPrefix} are loaded.");
                    continue;
                }

                if (IsLoaded(SceneManager.GetSceneByBuildIndex(index)))
                {
                    Log($"refused level {index} = '{path}': it is already loaded.");
                    continue;
                }

                if (result.Count >= MaxLevels)
                {
                    Log($"refused level {index} = '{path}': more than {MaxLevels} levels in one press.");
                    continue;
                }

                Log($"level {index} = '{path}' - accepted.");
                result.Add(new Level { Index = index, Path = path });
            }

            return result;
        }

        /// <summary>Starts an unload of every scene this probe still has loaded, without waiting - for a run that cannot
        /// finish its own. The menu is then unproven, so a restart is advised for the session.</summary>
        private static void EmergencyUnload(string why)
        {
            var started = new List<int>();

            foreach (var index in Loaded.ToList())
            {
                try
                {
                    var scene = SceneManager.GetSceneByBuildIndex(index);
                    if (!IsLoaded(scene)) continue;
                    if (SceneManager.UnloadSceneAsync(scene) != null) started.Add(index);
                }
                catch (Exception ex)
                {
                    Log($"emergency unload of level {index} failed ({ex.GetType().Name}: {ex.Message}).");
                }
            }

            Loaded.Clear();
            RestartAdvised = why;
            Log($"EMERGENCY UNLOAD ({why}): started for level(s) [{string.Join(", ", started)}], not waited for. " +
                "RESTART THE GAME ADVISED.");
        }

        private static void Log(string text) => Plugin.LogSource?.LogInfo(Tag + text);
    }
}
