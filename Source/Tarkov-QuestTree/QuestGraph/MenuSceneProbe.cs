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

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// THROWAWAY DIAGNOSTIC - the menu scene experiment. Can a raid map's built-in scenes be loaded ADDITIVELY while the
    /// player sits in the main menu (no raid, no GameWorld), and would the capture then see the whole map with nothing
    /// streamed out? This answers the first half only: it loads the listed build indices, measures what arrived, and
    /// unloads them again. It captures nothing. Delete this file with <see cref="ModSettings.MenuSceneProbeKey"/>,
    /// <see cref="ModSettings.MenuSceneProbeLevels"/> and the one line in TrackerHotkey.Update.
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

        /// <summary>The hard limit on one level's load, and on each unload and the asset sweep.</summary>
        private const double TimeoutSeconds = 60d;

        private const int MaxDistinctMessages = 20;

        private const int MaxLevels = 64;

        /// <summary>Only raid-map scenes: never the main scene, a UI scene or the hideout.</summary>
        private const string AllowedPathPrefix = "Assets/Content/Locations/";

        private static bool _busy;
        private static int _press;
        private static MonoBehaviour _host;
        private static int _handledFrame = -1;
        private static bool _warned;
        private static bool _alive;

        /// <summary>The frame the run's coroutine last yielded on. A run whose coroutine has not yielded for more than
        /// <see cref="DeadAfterFrames"/> frames is dead (Unity stopped it without its finally).</summary>
        private static int _lastTick;

        private const int DeadAfterFrames = 3;

        /// <summary>Every yield of the run goes through this, so the poll can tell a live run from a dead one.</summary>
        private static object Tick()
        {
            _lastTick = Time.frameCount;
            return null;
        }

        /// <summary>The DontDestroyOnLoad scene's roots before the run, by instance id - see <see cref="DdolRoots"/>.</summary>
        private static HashSet<int> _ddolBefore;

        /// <summary>Sticky for the session: an earlier run could not prove it left the menu as it found it.</summary>
        private static string _restartAdvised;

        /// <summary>Build indices this probe asked to load and has not yet seen unloaded - what an emergency unload has to
        /// take down when the coroutine dies without its finally.</summary>
        private static readonly List<int> Loaded = new List<int>();

        // --- the log capture ---------------------------------------------------------------------------------------------

        private static bool _capturing;
        private static bool _inHandler;
        private static int _exceptions;
        private static int _errors;
        private static int _asserts;
        private static int _warnings;
        private static int _plainLogs;
        private static int _ownFailures;
        private static readonly Dictionary<string, int> MessageCounts = new Dictionary<string, int>();
        private static readonly List<string> MessageOrder = new List<string>();
        private static readonly Dictionary<string, string> MessageFrames = new Dictionary<string, string>();
        private static readonly HashSet<string> AllDistinct = new HashSet<string>();

        /// <summary>Polled from TrackerHotkey.Update, which is proven to tick in the menu. Never throws.</summary>
        internal static void PollFromHotkey(MonoBehaviour host)
        {
            if (host == null || _handledFrame == Time.frameCount) return;

            try
            {
                // Unity stops a coroutine whose host is destroyed WITHOUT running its finally, so a menu rebuilt mid-run
                // would leave scenes loaded and _busy set for the session. Caught here, on the next tick of the new host.
                // A coroutine that stopped yielding is dead the same way, whatever killed it.
                if (_busy && (_host == null || !_host.isActiveAndEnabled || Time.frameCount - _lastTick > DeadAfterFrames))
                {
                    _busy = false;
                    StopCapture();
                    Log($"the probe's coroutine is dead (host {(_host == null ? "destroyed" : _host.isActiveAndEnabled ? "alive" : "inactive")}, " +
                        $"last yield {Time.frameCount - _lastTick} frame(s) ago) - it died without its cleanup.");
                    EmergencyUnload("the probe's coroutine died mid-run");
                }

                if (!ModSettings.Ready || ModSettings.MenuSceneProbeKey == null) return;

                var shortcut = ModSettings.MenuSceneProbeKey.Value;

                // Once, so the log proves the watcher runs and says which key and levels are live.
                if (!_alive)
                {
                    _alive = true;
                    var bound = ModSettings.KeyText(shortcut, " + ");
                    Log($"polling from {host.GetType().Name}; key {(string.IsNullOrEmpty(bound) ? "unbound" : bound)}, " +
                        $"levels \"{ModSettings.MenuSceneProbeLevels?.Value}\", {SceneManager.sceneCountInBuildSettings} scenes in the build list.");
                }

                if (shortcut.MainKey == KeyCode.None) return;
                if (!ModSettings.ShortcutDown(shortcut)) return;

                _handledFrame = Time.frameCount;

                if (_busy)
                {
                    Log("ignored a press: a run is still going.");
                    return;
                }

                if (!InMenu(out var why))
                {
                    Log($"refused: {why}. It runs in the main menu only.");
                    return;
                }

                if (_restartAdvised != null)
                {
                    Log($"refused: an earlier run could not restore the menu ({_restartAdvised}). RESTART THE GAME before probing again.");
                    return;
                }

                var levels = ValidLevels(ModSettings.MenuSceneProbeLevels?.Value);
                if (levels.Count == 0)
                {
                    Log("nothing to load - no listed level passed the checks above.");
                    return;
                }

                _press++;
                _host = host;
                _busy = true;
                _lastTick = Time.frameCount;

                try
                {
                    host.StartCoroutine(Run(_press, levels));
                }
                catch (Exception ex)
                {
                    _busy = false;
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
        private static IEnumerator Run(int press, List<Level> levels)
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

                Try("the DontDestroyOnLoad baseline", () =>
                {
                    _ddolBefore = new HashSet<int>(DdolRoots().Select(g => g.GetInstanceID()));
                    Log($"DontDestroyOnLoad roots before: {_ddolBefore.Count}.");
                });

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
                    Try($"disabling level {level.Index}'s streamers", () => DisableStreamers(level.Index));

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
                    Try($"measuring level {level.Index}", () => MeasureScene(level, scene, loadMs, memBefore, errorsBefore));
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
                    LogMessages();

                    if (trouble.Count == 0)
                    {
                        Log($"FINISHED press {press} in {total.ElapsedMilliseconds} ms - every probed scene is unloaded and " +
                            "the compared state matches (scenes, LocationScene lists, LevelSettings and EnvironmentManager instances, " +
                            "RenderSettings ambient/probe/fog/skybox/sun, texture streaming and shadow distance, shader globals, " +
                            "DontDestroyOnLoad roots). State outside that list is not proven.");
                    }
                    else
                    {
                        _restartAdvised = trouble[0];
                        Log($"FINISHED press {press} in {total.ElapsedMilliseconds} ms - RESTART THE GAME ADVISED: " +
                            string.Join("; ", trouble) + ".");
                    }
                }
                catch (Exception)
                {
                    // The verdict line is the last thing; nothing may keep _busy from clearing.
                }

                _busy = false;
            }
        }

        // --- the measurements --------------------------------------------------------------------------------------------

        /// <summary>Disables every enabled component in the level whose type name contains "Streamer", and logs each.</summary>
        private static void DisableStreamers(int index)
        {
            var scene = SceneManager.GetSceneByBuildIndex(index);
            if (!IsLoaded(scene)) return;

            var names = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b == null || b.GetType().Name.IndexOf("Streamer", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var was = b.enabled;
                    b.enabled = false;
                    names.Add($"{b.GetType().FullName} on '{b.gameObject.name}' (was {(was ? "enabled" : "disabled")})");
                }
            }

            Log(names.Count == 0
                ? $"level {index}: no streamer component."
                : $"level {index}: disabled {names.Count} streamer component(s): {string.Join("; ", names.Take(10))}.");
        }

        /// <summary>One level, just loaded: what it contains and what it cost.</summary>
        private static void MeasureScene(Level level, Scene scene, long loadMs, string memBefore, int[] errorsBefore)
        {
            var clock = Stopwatch.StartNew();
            var roots = scene.GetRootGameObjects();
            var cameras = LiveCameras();

            int meshRenderers = 0, meshActive = 0, meshVisible = 0, renderersActive = 0, renderersVisible = 0, inAnyMask = 0;
            int terrains = 0, terrainsActive = 0, colliders = 0, collidersActive = 0, lights = 0, lightsActive = 0, directional = 0;
            int lods = 0, lodsActive = 0, behaviours = 0, behavioursActive = 0, missingScripts = 0;
            int bodiesFree = 0, audioEnabled = 0, sceneCameras = 0, locationScenes = 0, levelSettings = 0, streamers = 0;
            var bounds = new Bounds();
            var hasBounds = false;
            var xs = new List<float>();
            var zs = new List<float>();
            var layers = new Dictionary<int, int>();
            var terrainRects = new List<string>();
            var cameraNames = new List<string>();

            foreach (var root in roots)
            {
                if (root == null) continue;

                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;

                    var active = r.enabled && r.gameObject.activeInHierarchy;
                    var isMesh = r is MeshRenderer;
                    if (isMesh) meshRenderers++;
                    if (!active) continue;

                    renderersActive++;
                    var visible = r.isVisible;
                    if (visible) renderersVisible++;
                    if (isMesh)
                    {
                        meshActive++;
                        if (visible) meshVisible++;
                    }

                    var layer = r.gameObject.layer;
                    layers[layer] = layers.TryGetValue(layer, out var n) ? n + 1 : 1;
                    if (cameras.Any(c => (c.cullingMask & (1 << layer)) != 0)) inAnyMask++;

                    var b = r.bounds;
                    if (!Finite(b.center) || !Finite(b.size)) continue;
                    if (hasBounds) bounds.Encapsulate(b);
                    else { bounds = b; hasBounds = true; }

                    if (isMesh)
                    {
                        xs.Add(b.center.x);
                        zs.Add(b.center.z);
                    }
                }

                foreach (var t in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (t == null) continue;
                    terrains++;
                    if (!t.enabled || !t.gameObject.activeInHierarchy) continue;
                    terrainsActive++;

                    var at = t.GetPosition();
                    var size = t.terrainData != null ? t.terrainData.size : Vector3.zero;
                    if (terrainRects.Count < 8)
                        terrainRects.Add($"x {F(at.x)}..{F(at.x + size.x)} z {F(at.z)}..{F(at.z + size.z)} y {F(at.y)}..{F(at.y + size.y)}");
                }

                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    colliders++;
                    if (c.enabled && c.gameObject.activeInHierarchy) collidersActive++;
                }

                foreach (var l in root.GetComponentsInChildren<Light>(true))
                {
                    if (l == null) continue;
                    lights++;
                    if (!l.enabled || !l.gameObject.activeInHierarchy) continue;
                    lightsActive++;
                    if (l.type == LightType.Directional) directional++;
                }

                foreach (var g in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if (g == null) continue;
                    lods++;
                    if (g.enabled && g.gameObject.activeInHierarchy) lodsActive++;
                }

                foreach (var m in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    // A missing script comes back as a null entry.
                    if (m == null) { missingScripts++; continue; }

                    behaviours++;
                    if (m.isActiveAndEnabled) behavioursActive++;
                    if (m is LocationScene) locationScenes++;
                    else if (m is LevelSettings) levelSettings++;
                    else if (m is Streamer) streamers++;
                }

                foreach (var body in root.GetComponentsInChildren<Rigidbody>(false))
                    if (body != null && !body.isKinematic) bodiesFree++;

                // By name: the project references no audio module, and an enabled source is enough to say "may play".
                foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(false))
                    if (behaviour != null && behaviour.isActiveAndEnabled && behaviour.GetType().Name == "AudioSource") audioEnabled++;

                foreach (var cam in root.GetComponentsInChildren<Camera>(false))
                {
                    if (cam == null || !cam.enabled) continue;
                    sceneCameras++;
                    if (cameraNames.Count < 5) cameraNames.Add(cam.name);
                }
            }

            var errors = ErrorCounts();

            Log($"level {level.Index} '{level.Path}' loaded in {loadMs} ms: {scene.rootCount} roots; " +
                $"MeshRenderers {meshActive} active / {meshRenderers}; Terrains {terrainsActive} active / {terrains}; " +
                $"Colliders {collidersActive} active / {colliders}; Lights {lightsActive} active / {lights} ({directional} directional); " +
                $"LODGroups {lodsActive} active / {lods}; MonoBehaviours {behavioursActive} active / {behaviours}, {missingScripts} missing script(s).");

            Log($"level {level.Index} bounds of {renderersActive} active renderers: " +
                (hasBounds
                    ? $"x {F(bounds.min.x)}..{F(bounds.max.x)} z {F(bounds.min.z)}..{F(bounds.max.z)} y {F(bounds.min.y)}..{F(bounds.max.y)}"
                    : "none") +
                (xs.Count > 0 ? $"; MeshRenderer centres 1-99 %: x {F(Pct(xs, 0.01f))}..{F(Pct(xs, 0.99f))} z {F(Pct(zs, 0.01f))}..{F(Pct(zs, 0.99f))}" : "") +
                (terrainRects.Count > 0 ? $"; terrain(s): {string.Join(" | ", terrainRects)}" : ""));

            Log($"level {level.Index} drawn? {renderersVisible} of {renderersActive} active renderers isVisible " +
                $"({meshVisible} of {meshActive} MeshRenderers); {inAnyMask} on a layer some enabled camera's mask includes; " +
                $"layers {string.Join(", ", layers.OrderByDescending(p => p.Value).Take(8).Select(p => $"{LayerMask.LayerToName(p.Key)}({p.Key})={p.Value}"))}.");

            Log($"level {level.Index} side effects: {sceneCameras} enabled camera(s) of its own" +
                (cameraNames.Count > 0 ? $" [{string.Join(", ", cameraNames)}]" : "") +
                $", {bodiesFree} non-kinematic rigidbodies, {audioEnabled} enabled audio sources; " +
                $"LocationScene x{locationScenes}, LevelSettings x{levelSettings}, Streamer x{streamers}; " +
                $"log during the load: {errors[0] - errorsBefore[0]} exceptions, {errors[1] - errorsBefore[1]} errors, " +
                $"{errors[2] - errorsBefore[2]} asserts, {errors[3] - errorsBefore[3]} warnings.");

            Log($"level {level.Index} memory: before {memBefore}; after {Memory()}. (measured in {clock.ElapsedMilliseconds} ms)");
        }

        /// <summary>Everything loaded, together: what phase 2's capture would find, and whether any camera draws it.</summary>
        private static void MeasureTogether(List<Scene> scenes, MenuState before)
        {
            var clock = Stopwatch.StartNew();
            var triangulation = NavMesh.CalculateTriangulation();
            var navMs = clock.ElapsedMilliseconds;
            var navVerts = triangulation.vertices?.Length ?? 0;
            var navTris = (triangulation.indices?.Length ?? 0) / 3;

            var borderZones = SafeCount(() => LocationScene.GetAll<BorderZone>().Count());
            var spawns = SafeCount(() => LocationScene.GetAll<SpawnPointMarker>().Count());
            var exits = SafeCount(() => LocationScene.GetAll<ExfiltrationPoint>().Count());
            var botZones = SafeCount(() => LocationScene.GetAll<BotZone>().Count());
            var triggers = SafeCount(() => UnityEngine.Object.FindObjectsOfType<TriggerWithId>(true).Length);

            Log($"together ({scenes.Count} scene(s)) - phase 2 inputs: NavMesh {navVerts} vertices / {navTris} triangles " +
                $"(menu baseline {before?.NavVertices ?? -1}, {navMs} ms); Terrain.activeTerrains {Terrain.activeTerrains.Length} " +
                $"(baseline {before?.Terrains ?? -1}); LocationScene.LoadedScenes {LocationScene.LoadedScenes.Count}; BorderZones {borderZones}, " +
                $"SpawnPointMarkers {spawns}, ExfiltrationPoints {exits}, BotZones {botZones}; TriggerWithId (incl. inactive) {triggers}; " +
                $"Singleton<GameWorld> {(Singleton<GameWorld>.Instantiated ? "SET" : "not set")}, " +
                $"Singleton<LevelSettings> {(Singleton<LevelSettings>.Instantiated ? "SET" : "not set")}; Camera.main '{(Camera.main != null ? Camera.main.name : "none")}'.");

            if (before != null)
            {
                var now = MenuState.Take();
                var changes = before.Differences(now).ToList();
                Log(changes.Count == 0
                    ? "together: the compared global state is unchanged while loaded."
                    : $"together: the loaded scenes changed {string.Join("; ", changes)}.");
            }

            // Which camera could draw them: its mask against their layers, and its frustum against their bounds.
            var probed = new List<Renderer>();
            foreach (var scene in scenes)
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null) continue;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                        if (r != null && r.enabled) probed.Add(r);
                }
            }

            foreach (var cam in LiveCameras())
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                var at = cam.transform.position;
                int inMask = 0, inView = 0, visible = 0;

                foreach (var r in probed)
                {
                    if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                    inMask++;
                    if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) inView++;
                    if (r.isVisible) visible++;
                }

                Log($"camera '{cam.name}' (scene '{cam.gameObject.scene.name}', depth {F(cam.depth)}, mask 0x{cam.cullingMask:X8}, " +
                    $"{(cam.targetTexture != null ? "to a RenderTexture" : "to the screen")}, at {V(at)}, far {F(cam.farClipPlane)}): " +
                    $"of {probed.Count} probed renderers {inMask} on its layers, {inView} of those inside its frustum; " +
                    $"{visible} isVisible (any camera).");
            }

            Log($"together: measured in {clock.ElapsedMilliseconds} ms. {Memory()}");
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

        // --- the menu's global state --------------------------------------------------------------------------------------

        /// <summary>The global state a map scene is known (from the decompiled code) to change and not put back, plus the
        /// counts that say whether the menu is as it was.</summary>
        private sealed class MenuState
        {
            internal int SceneCount;
            internal string SceneNames;
            internal string ActiveScene;
            internal int LocationScenes;
            internal int DoorColliders;
            internal LevelSettings LevelSettings;
            internal bool GameWorld;
            internal int Lightmaps;
            internal int NavVertices;
            internal int Terrains;

            internal AmbientMode AmbientMode;
            internal Color AmbientEquator, AmbientGround, AmbientSky, AmbientLight;
            internal float AmbientIntensity;
            internal bool Fog;
            internal Color FogColor;
            internal float FogDensity, FogStart, FogEnd;
            internal FogMode FogMode;
            internal Material Skybox;
            internal Light Sun;

            internal float StreamingBudget;
            internal int StreamingReduction;
            internal float ShadowDistance;
            internal float ControllerBudget;
            internal int ControllerReduction;
            internal SphericalHarmonicsL2 AmbientProbe;
            internal EnvironmentManagerBase Environment;

            internal float DirectionLightShadow, WaterLevel, SsrFactor;
            internal Color MinAmbientColor, TopHorizontSkyColor;

            internal static MenuState Take()
            {
                var s = new MenuState
                {
                    SceneCount = SceneManager.sceneCount,
                    ActiveScene = SceneManager.GetActiveScene().name,
                    LocationScenes = LocationScene.LoadedScenes.Count,
                    DoorColliders = LocationScene.DoorsCollisionColliders.Count,
                    LevelSettings = Singleton<LevelSettings>.Instance,
                    GameWorld = Singleton<GameWorld>.Instantiated,
                    Lightmaps = LightmapSettings.lightmaps?.Length ?? 0,
                    Terrains = Terrain.activeTerrains.Length,
                    AmbientMode = RenderSettings.ambientMode,
                    AmbientEquator = RenderSettings.ambientEquatorColor,
                    AmbientGround = RenderSettings.ambientGroundColor,
                    AmbientSky = RenderSettings.ambientSkyColor,
                    AmbientLight = RenderSettings.ambientLight,
                    AmbientIntensity = RenderSettings.ambientIntensity,
                    Fog = RenderSettings.fog,
                    FogColor = RenderSettings.fogColor,
                    FogDensity = RenderSettings.fogDensity,
                    FogStart = RenderSettings.fogStartDistance,
                    FogEnd = RenderSettings.fogEndDistance,
                    FogMode = RenderSettings.fogMode,
                    Skybox = RenderSettings.skybox,
                    Sun = RenderSettings.sun,
                    StreamingBudget = QualitySettings.streamingMipmapsMemoryBudget,
                    StreamingReduction = QualitySettings.streamingMipmapsMaxLevelReduction,
                    ShadowDistance = QualitySettings.shadowDistance,
                    ControllerBudget = GraphicsSettingsController.MipStreamingMemoryBudget,
                    ControllerReduction = GraphicsSettingsController.StreamingMipmapsMaxLevelReduction,
                    AmbientProbe = RenderSettings.ambientProbe,
                    Environment = EnvironmentManagerBase.Instance,
                    DirectionLightShadow = Shader.GetGlobalFloat("_DirectionLightShadow"),
                    WaterLevel = Shader.GetGlobalFloat("_WaterLevel"),
                    SsrFactor = Shader.GetGlobalFloat("_SSRFactor"),
                    MinAmbientColor = Shader.GetGlobalColor("_MinAmbientColor"),
                    TopHorizontSkyColor = Shader.GetGlobalColor("_TopHorizontSkyColor"),
                };

                var names = new List<string>();
                for (var i = 0; i < s.SceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    names.Add($"{scene.name}#{scene.buildIndex}");
                }

                s.SceneNames = string.Join(", ", names);

                try
                {
                    s.NavVertices = NavMesh.CalculateTriangulation().vertices?.Length ?? 0;
                }
                catch (Exception)
                {
                    s.NavVertices = -1;
                }

                return s;
            }

            internal string Describe() =>
                $"{SceneCount} scene(s) [{SceneNames}], active '{ActiveScene}'; LocationScenes {LocationScenes}, door colliders " +
                $"{DoorColliders}; Singleton<LevelSettings> {(LevelSettings != null ? "set" : "not set")}, Singleton<GameWorld> " +
                $"{(GameWorld ? "SET" : "not set")}; lightmaps {Lightmaps}; NavMesh vertices {NavVertices}; terrains {Terrains}; " +
                $"ambient {AmbientMode}, fog {Fog}, skybox '{(Skybox != null ? Skybox.name : "none")}', sun '{(Sun != null ? Sun.name : "none")}'; " +
                $"streaming budget {F(StreamingBudget)} MB / reduction {StreamingReduction} (controller {F(ControllerBudget)}/{ControllerReduction}); " +
                $"shadow distance {F(ShadowDistance)}; EnvironmentManager {(Environment != null ? "'" + Environment.name + "'" : "none")}.";

            internal IEnumerable<string> Differences(MenuState now)
            {
                if (now.SceneCount != SceneCount) yield return $"scene count {SceneCount} -> {now.SceneCount}";
                if (now.SceneNames != SceneNames) yield return $"scenes [{SceneNames}] -> [{now.SceneNames}]";
                if (now.ActiveScene != ActiveScene) yield return $"active scene '{ActiveScene}' -> '{now.ActiveScene}'";
                if (now.LocationScenes != LocationScenes) yield return $"LocationScene.LoadedScenes {LocationScenes} -> {now.LocationScenes}";
                if (now.DoorColliders != DoorColliders) yield return $"LocationScene.DoorsCollisionColliders {DoorColliders} -> {now.DoorColliders}";
                if (!ReferenceEquals(now.LevelSettings, LevelSettings)) yield return "Singleton<LevelSettings> changed";
                if (now.GameWorld != GameWorld) yield return $"Singleton<GameWorld> {GameWorld} -> {now.GameWorld}";
                if (now.Lightmaps != Lightmaps) yield return $"lightmaps {Lightmaps} -> {now.Lightmaps}";
                if (now.Terrains != Terrains) yield return $"active terrains {Terrains} -> {now.Terrains}";
                if (now.AmbientMode != AmbientMode || now.AmbientSky != AmbientSky || now.AmbientEquator != AmbientEquator ||
                    now.AmbientGround != AmbientGround || now.AmbientLight != AmbientLight || now.AmbientIntensity != AmbientIntensity)
                    yield return "RenderSettings ambient";
                if (now.Fog != Fog || now.FogColor != FogColor || now.FogDensity != FogDensity || now.FogStart != FogStart ||
                    now.FogEnd != FogEnd || now.FogMode != FogMode)
                    yield return "RenderSettings fog";
                if (!ReferenceEquals(now.Skybox, Skybox)) yield return "RenderSettings skybox";
                if (!ReferenceEquals(now.Sun, Sun)) yield return "RenderSettings sun";
                if (now.StreamingBudget != StreamingBudget || now.StreamingReduction != StreamingReduction)
                    yield return $"texture streaming budget {F(StreamingBudget)}/{StreamingReduction} -> {F(now.StreamingBudget)}/{now.StreamingReduction}";
                if (now.DirectionLightShadow != DirectionLightShadow || now.WaterLevel != WaterLevel || now.SsrFactor != SsrFactor ||
                    now.MinAmbientColor != MinAmbientColor || now.TopHorizontSkyColor != TopHorizontSkyColor)
                    yield return "LevelSettings' shader globals";
                if (now.ShadowDistance != ShadowDistance) yield return $"shadow distance {F(ShadowDistance)} -> {F(now.ShadowDistance)}";
                if (now.ControllerBudget != ControllerBudget || now.ControllerReduction != ControllerReduction)
                    yield return "GraphicsSettingsController's streaming statics";
                if (now.AmbientProbe != AmbientProbe) yield return "RenderSettings.ambientProbe";
                if (!ReferenceEquals(now.Environment, Environment)) yield return "EnvironmentManagerBase._instance";
            }

            /// <summary>Puts back what LevelSettings.Awake and LocationScene.Awake change and their teardown does not. Every
            /// write is said in the log. Never touches Singleton&lt;GameWorld&gt;.</summary>
            internal void Restore(List<string> trouble)
            {
                var now = Take();
                var restored = new List<string>();

                if (now.AmbientMode != AmbientMode || now.AmbientSky != AmbientSky || now.AmbientEquator != AmbientEquator ||
                    now.AmbientGround != AmbientGround || now.AmbientLight != AmbientLight || now.AmbientIntensity != AmbientIntensity)
                {
                    RenderSettings.ambientMode = AmbientMode;
                    RenderSettings.ambientEquatorColor = AmbientEquator;
                    RenderSettings.ambientGroundColor = AmbientGround;
                    RenderSettings.ambientSkyColor = AmbientSky;
                    RenderSettings.ambientLight = AmbientLight;
                    RenderSettings.ambientIntensity = AmbientIntensity;
                    restored.Add("ambient");
                }

                if (now.Fog != Fog || now.FogColor != FogColor || now.FogDensity != FogDensity || now.FogStart != FogStart ||
                    now.FogEnd != FogEnd || now.FogMode != FogMode)
                {
                    RenderSettings.fog = Fog;
                    RenderSettings.fogColor = FogColor;
                    RenderSettings.fogDensity = FogDensity;
                    RenderSettings.fogStartDistance = FogStart;
                    RenderSettings.fogEndDistance = FogEnd;
                    RenderSettings.fogMode = FogMode;
                    restored.Add("fog");
                }

                if (!ReferenceEquals(now.Skybox, Skybox))
                {
                    RenderSettings.skybox = Skybox;
                    restored.Add("skybox");
                }

                if (!ReferenceEquals(now.Sun, Sun))
                {
                    RenderSettings.sun = Sun;
                    restored.Add("sun");
                }

                if (now.StreamingBudget != StreamingBudget || now.StreamingReduction != StreamingReduction)
                {
                    QualitySettings.streamingMipmapsMemoryBudget = StreamingBudget;
                    QualitySettings.streamingMipmapsMaxLevelReduction = StreamingReduction;
                    restored.Add("texture streaming budget");
                }

                if (now.DirectionLightShadow != DirectionLightShadow || now.WaterLevel != WaterLevel || now.SsrFactor != SsrFactor ||
                    now.MinAmbientColor != MinAmbientColor || now.TopHorizontSkyColor != TopHorizontSkyColor)
                {
                    Shader.SetGlobalFloat("_DirectionLightShadow", DirectionLightShadow);
                    Shader.SetGlobalFloat("_WaterLevel", WaterLevel);
                    Shader.SetGlobalFloat("_SSRFactor", SsrFactor);
                    Shader.SetGlobalColor("_MinAmbientColor", MinAmbientColor);
                    Shader.SetGlobalColor("_TopHorizontSkyColor", TopHorizontSkyColor);
                    restored.Add("shader globals");
                }

                if (now.ShadowDistance != ShadowDistance)
                {
                    QualitySettings.shadowDistance = ShadowDistance;
                    restored.Add("shadow distance");
                }

                if (now.ControllerBudget != ControllerBudget || now.ControllerReduction != ControllerReduction)
                {
                    GraphicsSettingsController.MipStreamingMemoryBudget = ControllerBudget;
                    GraphicsSettingsController.StreamingMipmapsMaxLevelReduction = ControllerReduction;
                    restored.Add("GraphicsSettingsController's streaming statics");
                }

                if (now.AmbientProbe != AmbientProbe)
                {
                    RenderSettings.ambientProbe = AmbientProbe;
                    restored.Add("ambient probe");
                }

                // A map's EnvironmentManager nulls the protected static _instance on destroy whatever it holds. Put back a
                // live one that was there before; drop a destroyed one. Reached by the field's type, never its name.
                if (!ReferenceEquals(now.Environment, Environment))
                {
                    var field = typeof(EnvironmentManagerBase)
                        .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .FirstOrDefault(f => f.FieldType == typeof(EnvironmentManagerBase));

                    if (field == null)
                    {
                        trouble.Add("EnvironmentManagerBase's instance field could not be found to restore");
                    }
                    else if (Environment != null)
                    {
                        field.SetValue(null, Environment);
                        restored.Add("EnvironmentManager instance (the menu's own)");
                    }
                    else if (now.Environment == null)
                    {
                        field.SetValue(null, null);
                        restored.Add("EnvironmentManager instance (released a destroyed one)");
                    }
                    else
                    {
                        trouble.Add("a live EnvironmentManager from the probed scenes is still the instance");
                    }
                }

                // A map's LevelSettings releases the singleton on destroy WHATEVER it holds, so a menu that had one of its own
                // loses it. Put back only a live object that was there before; drop only a destroyed one.
                if (!ReferenceEquals(now.LevelSettings, LevelSettings))
                {
                    if (LevelSettings != null)
                    {
                        Singleton<LevelSettings>.Create(LevelSettings);
                        restored.Add("Singleton<LevelSettings> (the menu's own)");
                    }
                    else if (now.LevelSettings == null)
                    {
                        // Unity-null (destroyed) but still referenced.
                        Singleton<LevelSettings>.Release(now.LevelSettings);
                        restored.Add("Singleton<LevelSettings> (released a destroyed one)");
                    }
                    else
                    {
                        trouble.Add("a live LevelSettings from the probed scenes is still the singleton");
                    }
                }

                // LocationScene.Awake adds a door's colliders to this static list and nothing removes them; unloaded, they
                // are destroyed entries. Only destroyed ones are removed.
                var removed = LocationScene.DoorsCollisionColliders.RemoveAll(c => c == null);
                if (removed > 0) restored.Add($"{removed} destroyed door collider(s) removed from LocationScene.DoorsCollisionColliders");

                Log(restored.Count == 0 ? "restore: nothing needed putting back." : $"restore: put back {string.Join(", ", restored)}.");
            }
        }

        // --- the checks --------------------------------------------------------------------------------------------------

        /// <summary>The main menu, strictly: no raid, no raid loading, no hideout, no capture. Reads only.</summary>
        private static bool InMenu(out string why)
        {
            why = null;

            try
            {
                // A HideoutGameWorld on its own is allowed (it can outlive a hideout visit); the hideout itself is refused by
                // its scene. Any other GameWorld is a raid or a raid loading.
                var worlds = UnityEngine.Object.FindObjectsOfType<GameWorld>();
                var raidWorld = worlds.FirstOrDefault(w => w != null && !(w is HideoutGameWorld));
                var singleton = Singleton<GameWorld>.Instance;
                var screen = CurrentScreen();

                if (MeshProbe.RaidWatchers > 0) why = "in a raid (a raid watcher is alive)";
                else if (singleton != null && !(singleton is HideoutGameWorld))
                    why = $"a raid GameWorld is the singleton ({singleton.GetType().Name})";
                else if (raidWorld != null) why = $"a raid GameWorld object exists ({raidWorld.GetType().Name}; a raid is loading)";
                else if (IsLoaded(SceneManager.GetSceneByName(EFT.Scenes.HideoutSceneName))) why = "the hideout scene (bunker_2) is loaded";
                else if (MapCapture.IsCapturing) why = "a map capture is running";
                else if (screen != EEftScreenType.MainMenu.ToString())
                    why = $"the current screen is {screen}, not the main menu - go back to the main menu screen first";
            }
            catch (Exception ex)
            {
                // A test that cannot be made is a refusal here: loading a map into a raid is the one outcome to avoid.
                why = $"the menu test itself failed ({ex.GetType().Name}: {ex.Message})";
            }

            return why == null;
        }

        /// <summary>The current EFT screen's type as text, or "unknown". Read through the manager's static field so a missing
        /// manager is not created here.</summary>
        private static string CurrentScreen()
        {
            try
            {
                var manager = EftScreenManager._instance;
                var controller = manager?.CurrentScreenController;
                return controller == null ? "unknown (no screen)" : controller.ScreenType.ToString();
            }
            catch (Exception ex)
            {
                return $"unknown ({ex.GetType().Name})";
            }
        }

        /// <summary>The cheap per-frame half of <see cref="InMenu"/>, while a level loads: a raid GameWorld or raid watcher.</summary>
        private static bool RaidStarting()
        {
            try
            {
                var singleton = Singleton<GameWorld>.Instance;
                return MeshProbe.RaidWatchers > 0 || (singleton != null && !(singleton is HideoutGameWorld));
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool IsLoaded(Scene scene) => scene.IsValid() && scene.isLoaded;

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

        /// <summary>The DontDestroyOnLoad scene's root objects, reached through a throwaway object of our own that is moved
        /// there and destroyed again at once.</summary>
        private static List<GameObject> DdolRoots()
        {
            var marker = new GameObject("QuestTreeMenuSceneProbeMarker");
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(marker);
                return marker.scene.GetRootGameObjects().Where(g => g != null && g != marker).ToList();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        /// <summary>Logs every DontDestroyOnLoad root that appeared during the run - NEVER destroys one: the game parks icon
        /// cameras, light pools, the physics overlap system and factory objects there at runtime - and advises a restart
        /// when any appeared. Clears destroyed entries from MineDirectional.Mines, a static list its mines add themselves to
        /// and never leave.</summary>
        private static void SweepDdol(List<string> trouble)
        {
            if (_ddolBefore == null)
            {
                trouble.Add("no DontDestroyOnLoad baseline was taken, so its leaks could not be checked");
                return;
            }

            var added = DdolRoots()
                .Where(g => !_ddolBefore.Contains(g.GetInstanceID()) && !g.name.StartsWith("QuestTree", StringComparison.Ordinal))
                .ToList();

            if (added.Count == 0)
            {
                Log("DontDestroyOnLoad: no new roots.");
            }
            else
            {
                Log($"DontDestroyOnLoad: {added.Count} new root(s) appeared during the run - left in place, not destroyed.");

                foreach (var g in added.Take(20))
                {
                    var types = g.GetComponents<Component>()
                        .Select(c => c == null ? "(missing script)" : c.GetType().Name)
                        .Distinct();
                    Log($"DontDestroyOnLoad new root '{g.name}': {string.Join(", ", types)}.");
                }

                trouble.Add($"{added.Count} new DontDestroyOnLoad root(s) appeared during the run (listed above)");
            }

            var mines = MineDirectional.Mines.RemoveAll(m => m == null);
            Log($"MineDirectional.Mines: removed {mines} destroyed entries, {MineDirectional.Mines.Count} left.");
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
            _restartAdvised = why;
            Log($"EMERGENCY UNLOAD ({why}): started for level(s) [{string.Join(", ", started)}], not waited for. " +
                "RESTART THE GAME ADVISED.");
        }

        // --- the log capture ---------------------------------------------------------------------------------------------

        private static void StartCapture()
        {
            _exceptions = _errors = _asserts = _warnings = _plainLogs = _ownFailures = 0;
            MessageCounts.Clear();
            MessageOrder.Clear();
            MessageFrames.Clear();
            AllDistinct.Clear();

            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            _capturing = true;
        }

        private static void StopCapture()
        {
            _capturing = false;

            try
            {
                Application.logMessageReceived -= OnLog;
            }
            catch (Exception)
            {
                // Nothing to do: the handler is inert once _capturing is false.
            }
        }

        /// <summary>Counts every message Unity logs during a run and keeps the first 20 distinct warnings, errors, asserts and
        /// exceptions. It never logs itself (no re-entry) and never throws.</summary>
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (!_capturing || _inHandler) return;
            _inHandler = true;

            try
            {
                if (type == LogType.Log)
                {
                    _plainLogs++;
                    return;
                }

                if (condition != null && condition.StartsWith("QuestTree", StringComparison.Ordinal)) return;

                switch (type)
                {
                    case LogType.Exception: _exceptions++; break;
                    case LogType.Error: _errors++; break;
                    case LogType.Assert: _asserts++; break;
                    default: _warnings++; break;
                }

                var text = (condition ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (text.Length > 300) text = text.Substring(0, 300) + "...";
                var key = $"{type}: {text}";

                if (AllDistinct.Count < 10000) AllDistinct.Add(key);

                if (MessageCounts.TryGetValue(key, out var n))
                {
                    MessageCounts[key] = n + 1;
                }
                else if (MessageOrder.Count < MaxDistinctMessages)
                {
                    MessageCounts[key] = 1;
                    MessageOrder.Add(key);
                    MessageFrames[key] = FirstFrame(stackTrace);
                }
            }
            catch (Exception)
            {
                // A log handler that throws would be re-entered by Unity's own report of it.
            }
            finally
            {
                _inHandler = false;
            }
        }

        private static int[] ErrorCounts() => new[] { _exceptions, _errors, _asserts, _warnings };

        private static void LogMessages()
        {
            Log($"messages over the run: {_exceptions} exceptions, {_errors} errors, {_asserts} asserts, {_warnings} warnings, " +
                $"{_plainLogs} plain logs; {AllDistinct.Count} distinct non-plain; {_ownFailures} failure(s) in the probe's own steps.");

            for (var i = 0; i < MessageOrder.Count; i++)
            {
                var key = MessageOrder[i];
                MessageFrames.TryGetValue(key, out var frame);
                Log($"message {i + 1} (x{MessageCounts[key]}): {key}" + (string.IsNullOrEmpty(frame) ? "" : $" @ {frame}"));
            }
        }

        // --- helpers -----------------------------------------------------------------------------------------------------

        /// <summary>Runs one step, counting and logging a throw. False when it threw.</summary>
        private static bool Try(string what, Action work)
        {
            try
            {
                work();
                return true;
            }
            catch (Exception ex)
            {
                _ownFailures++;
                Plugin.LogSource?.LogWarning($"{Tag}{what} threw {ex.GetType().Name}: {ex.Message} at {FirstFrame(ex.StackTrace)}");
                return false;
            }
        }

        private static int SafeCount(Func<int> count)
        {
            try
            {
                return count();
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static List<Camera> LiveCameras() =>
            Camera.allCameras.Where(c => c != null && c.enabled && c.gameObject.activeInHierarchy).ToList();

        private static string Memory()
        {
            long heap = -1, allocated = -1, reserved = -1, priv = -1;

            try { heap = GC.GetTotalMemory(false); } catch (Exception) { }
            try { allocated = Profiler.GetTotalAllocatedMemoryLong(); } catch (Exception) { }
            try { reserved = Profiler.GetTotalReservedMemoryLong(); } catch (Exception) { }

            try
            {
                using (var process = Process.GetCurrentProcess()) priv = process.PrivateMemorySize64;
            }
            catch (Exception)
            {
                // Not every runtime can read it; -1 says so.
            }

            return $"memory: managed heap {MB(heap)} MB, Unity allocated {MB(allocated)} MB, Unity reserved {MB(reserved)} MB, " +
                   $"process private {MB(priv)} MB";
        }

        private static string MB(long bytes) =>
            bytes < 0 ? "?" : (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);

        private static string F(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        private static bool Finite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) &&
            !float.IsNaN(v.z) && !float.IsInfinity(v.z);

        private static float Pct(List<float> values, float share)
        {
            values.Sort();
            var i = (int)Math.Round(share * (values.Count - 1));
            return values[Math.Max(0, Math.Min(values.Count - 1, i))];
        }

        private static string FirstFrame(string stackTrace) =>
            (stackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        private static void Log(string text) => Plugin.LogSource?.LogInfo(Tag + text);
    }
}
