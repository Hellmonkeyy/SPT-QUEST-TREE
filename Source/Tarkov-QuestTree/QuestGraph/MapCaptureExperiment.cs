using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// THROWAWAY. Phase 0 of the in-house map pictures plan: measurements that decide the constants
    /// of the real capture code. This whole file, the ModSettings.ExperimentKey entry, the one line
    /// in GameWorldStartedPatch that installs it and the two csproj references added for it
    /// (UnityEngine.AIModule, UnityEngine.TerrainModule - AIModule is wanted by the real Phase A
    /// too) are to be DELETED once Phase 0's numbers are in hand. Nothing here ships and nothing
    /// else in the mod may come to depend on it.
    ///
    /// Round 1 on Customs answered E2, E3 and E4 and failed E1: a fresh Camera rendered an almost
    /// black picture with cyan blocks in it, so a camera of our own does not draw EFT's world as
    /// the player sees it. Round 2 keeps E2/E3/E4 unchanged and replaces E1 with four renders of
    /// the same view, to separate the candidate causes:
    ///   V1  fresh      - the round 1 camera, as the control.
    ///   V2  copymain   - CopyFrom the live FPS camera, then override only the framing. Tests
    ///                    whether the rendering path, HDR flag and culling mask were the problem.
    ///   V2b instantiate- a clone of the FPS camera's GameObject with its scripts disabled. Tests
    ///                    whether a COMPONENT on that camera is what draws the world (CopyFrom
    ///                    copies settings, never components).
    ///   V3  unlit      - the fresh camera with a replacement shader and flat white ambient. If the
    ///                    geometry appears here, the world is there and the lighting was missing.
    ///   V4  bright     - the fresh camera with white ambient and a culling mask cut down to the
    ///                    named world layers, to identify and drop the cyan geometry.
    /// Plus diagnostics D1-D4: all 32 layer names, the time of day, what the live FPS camera is set
    /// to, and how many MeshRenderers in the scene are switched off (the PerfectCulling
    /// hypothesis - if most of the map's renderers are disabled, no camera of ours can ever see it).
    ///
    /// Every step is wrapped: a failure in one is a warning and the others still run. Nothing may
    /// throw into the game - this runs inside a raid, on the player's own frame.
    /// </summary>
    internal sealed class MapCaptureExperiment : MonoBehaviour
    {
        /// <summary>Layers left out of the plain capture camera, by name. Absent names are skipped
        /// and reported, which is itself a Phase 0 finding: the real capture needs the names that
        /// actually exist in this game version.</summary>
        private static readonly string[] ExcludedLayerNames = { "Player", "UI", "Weapons", "Triggers" };

        /// <summary>Extra substrings V4 drops, on top of the four names above: the round 1 picture
        /// had bright cyan blocks in it that look like collision or debug geometry.</summary>
        private static readonly string[] V4DroppedSubstrings = { "Debug", "Trigger", "Collider", "Interactive", "Loot" };

        /// <summary>Replacement shaders V3 tries, in order. Shader.Find returns null for a shader
        /// the build stripped, so the first one that resolves is used and named in the log.</summary>
        private static readonly string[] UnlitShaderNames =
        {
            "Unlit/Texture", "Unlit/Color", "Mobile/Unlit (Supports Lightmap)", "Sprites/Default", "Legacy Shaders/Diffuse", "Standard"
        };

        /// <summary>Fraction of NavMesh vertices a 1 m bin needs before E2 reports it.</summary>
        private const float BinReportShare = 0.005f;

        private const int SmallTile = 1024;
        private const int LargeTile = 2048;

        /// <summary>Height above the extent's top the camera is put at, per the plan.</summary>
        private const float CameraHeightAboveTop = 300f;

        /// <summary>How the camera for one render is built.</summary>
        private enum Variant
        {
            /// <summary>A bare new Camera, the round 1 control.</summary>
            Fresh,

            /// <summary>A bare new Camera with CopyFrom of the live FPS camera applied first.</summary>
            CopyMain,

            /// <summary>A clone of the FPS camera's whole GameObject, scripts disabled.</summary>
            InstantiateMain,

            /// <summary>Fresh, rendered through a replacement shader with white ambient.</summary>
            Unlit,

            /// <summary>Fresh, white ambient, culling mask cut to the named world layers.</summary>
            Bright,
        }

        /// <summary>Adds the experiment watcher to a raid that has just started. Called from
        /// <see cref="QuestTree.Patches.GameWorldStartedPatch"/>, the same place the zone harvester
        /// is started; deliberately independent of the HarvestZones setting, since this is a debug
        /// key and not part of what the mod does for a player.
        ///
        /// Its GameObject is parented to the GameWorld, so it dies with the raid exactly as the
        /// harvester's coroutine does. Never throws.</summary>
        /// <param name="gameWorld">The raid's world, as handed to the patch.</param>
        public static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;

                // Same gate the rest of the in-raid UI work uses: a Fika headless client has no
                // player, no camera and nobody to press a key.
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeMapCaptureExperiment");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);

                var runner = go.AddComponent<MapCaptureExperiment>();
                runner._gameWorld = gameWorld;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the map-capture experiment ({ex.Message}).");
            }
        }

        private GameWorld _gameWorld;
        private bool _running;
        private bool _warnedOnPoll;

        private Camera _camera;
        private string _missingLayers = "(not checked)";
        private float _cameraY;
        private float _orthoSize;

        /// <summary>Every report line, in the order it was produced, so the text file holds whatever
        /// the run managed to measure even when a step failed.</summary>
        private readonly List<string> _lines = new List<string>();

        private Extent _border;
        private Extent _terrain;
        private Extent _navmesh;
        private Extent _chosen;

        private Vector3[] _navVertices;

        private void Update()
        {
            if (_running) return;
            if (!ModSettings.Ready || ModSettings.ExperimentKey == null) return;

            try
            {
                var shortcut = ModSettings.ExperimentKey.Value;
                if (shortcut.MainKey == KeyCode.None) return;
                if (!shortcut.IsDown()) return;

                // Set here rather than only in the coroutine: Update can run again before the
                // coroutine's first statement, and two of these at once would fight over the camera.
                _running = true;
                StartCoroutine(Run());
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the map-capture experiment key failed ({ex.Message}).");
            }
        }

        private void OnDestroy() => DestroyCamera();

        /// <summary>One step per frame. The steps are plain methods rather than inline code because
        /// C# forbids a yield inside a try that has a catch, and every step must be caught.</summary>
        private IEnumerator Run()
        {
            _running = true;
            _lines.Clear();

            var map = MapName();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment starting on {map}.");

            yield return null;
            Guarded("E4", () => RunE4(map));

            yield return null;
            Guarded("E2", () => RunE2(map));

            yield return null;
            Guarded("D1 layers", RunD1Layers);

            yield return null;
            Guarded("D2 time", () => RunD2Time(map));

            yield return null;
            Guarded("D3 main camera", () => RunD3MainCamera(map));

            yield return null;
            Guarded("D4 renderers", () => RunD4Renderers(map));

            // One variant per frame, each with its own camera, its own PNG and its own line.
            foreach (var variant in new[] { Variant.Fresh, Variant.CopyMain, Variant.InstantiateMain, Variant.Unlit, Variant.Bright })
            {
                yield return null;

                var v = variant;
                Guarded($"E1 {Label(v)}", () => RunE1(map, v));

                yield return null;
                Guarded($"E1 {Label(v)} cleanup", DestroyCamera);
            }

            yield return null;
            Guarded("E3", () => RunE3(map));

            yield return null;
            Guarded("cleanup", DestroyCamera);
            Guarded("report", () => WriteReport(map, stamp));

            _navVertices = null;
            _running = false;
        }

        private static void Guarded(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: map-capture experiment {what} failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // --- E4: where an extent can be read from ---------------------------------------------

        /// <summary>Reads the three candidate extents and picks the first non-empty one in the
        /// plan's order: BorderZone colliders, Terrain, NavMesh.</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunE4(string map)
        {
            _border = BorderZoneExtent();
            _terrain = TerrainExtent();
            _navmesh = NavMeshExtent();

            _chosen = _border.Valid ? _border : _terrain.Valid ? _terrain : _navmesh;

            Record(
                $"QuestTree E4: {map} borderzones {_border.Count} {Describe(_border)} " +
                $"terrains {_terrain.Count} {Describe(_terrain)} navmesh {Describe(_navmesh)}");

            Plugin.LogSource?.LogInfo(
                $"QuestTree E4: {map} rank-1 extent = {(_chosen.Valid ? _chosen.Source : "none")} " +
                $"{Describe(_chosen)}; round 1 showed the BorderZone box is a small interior one, so E1 " +
                $"and E3 frame the NAVMESH box {Describe(_navmesh)} instead.");
        }

        /// <summary>Union of the XZ AABBs of every BorderZone's collider. The members used are
        /// BorderZone.Collider (a BoxCollider field) and BorderZone._extents, both public fields on
        /// the decompiled EFT.Interactive.BorderZone; the zones are reached through
        /// LocationScene.GetAll, which reads the scene's registered BorderZones array and so sees
        /// inactive ones too.</summary>
        private static Extent BorderZoneExtent()
        {
            var box = new Box("BorderZone");

            var zones = BorderZones();
            foreach (var zone in zones)
            {
                if (zone == null) continue;

                Collider collider = zone.Collider;
                if (collider == null) collider = zone.GetComponent<Collider>();

                if (collider != null)
                {
                    box.Add(collider.bounds);
                }
                else
                {
                    // _extents is the serialised half-size the zone would build its box from.
                    var centre = zone.transform.position;
                    var half = zone._extents;
                    box.Add(new Bounds(centre, new Vector3(Mathf.Abs(half.x) * 2f, Mathf.Abs(half.y) * 2f, Mathf.Abs(half.z) * 2f)));
                }

                box.Count++;
            }

            return box.ToExtent();
        }

        private static IEnumerable<BorderZone> BorderZones()
        {
            try
            {
                var registered = LocationScene.GetAll<BorderZone>()?.ToArray();
                if (registered != null && registered.Length > 0) return registered;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: LocationScene.GetAll of BorderZone failed ({ex.Message}) - falling back to a scene search.");
            }

            return FindObjectsOfType<BorderZone>();
        }

        /// <summary>Union of every active Terrain's AABB, from its transform position and
        /// terrainData.size.</summary>
        private static Extent TerrainExtent()
        {
            var box = new Box("Terrain");

            var terrains = Terrain.activeTerrains;
            if (terrains != null)
            {
                foreach (var terrain in terrains)
                {
                    if (terrain == null || terrain.terrainData == null) continue;

                    var origin = terrain.transform.position;
                    var size = terrain.terrainData.size;
                    box.Add(new Bounds(origin + size * 0.5f, size));
                    box.Count++;
                }
            }

            return box.ToExtent();
        }

        /// <summary>AABB of the NavMesh triangulation, whose vertices are cached here for E2 so the
        /// triangulation is only calculated once.</summary>
        private Extent NavMeshExtent()
        {
            var box = new Box("NavMesh");

            var vertices = NavVertices();
            if (vertices != null)
            {
                foreach (var v in vertices) box.Add(v);
                box.Count = vertices.Length;
            }

            return box.ToExtent();
        }

        private Vector3[] NavVertices()
        {
            if (_navVertices != null) return _navVertices;

            var triangulation = NavMesh.CalculateTriangulation();
            _navVertices = triangulation.vertices ?? Array.Empty<Vector3>();
            return _navVertices;
        }

        /// <summary>What E1 and E3 frame: the NavMesh box, which round 1 proved is the one that
        /// covers the playable map, with the rank-1 extent as a fallback.</summary>
        private Extent RenderExtent() => _navmesh.Valid ? _navmesh : _chosen;

        // --- E2: floor bands ------------------------------------------------------------------

        /// <summary>1 m histogram of NavMesh vertex Y, plus the median Y of the scene's spawn point
        /// markers - the candidate for "which band is the ground floor".</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunE2(string map)
        {
            var vertices = NavVertices();
            if (vertices == null || vertices.Length == 0)
            {
                Record($"QuestTree E2: {map} navmesh 0 verts - no triangulation.");
                return;
            }

            var min = float.MaxValue;
            var max = float.MinValue;
            var bins = new Dictionary<int, int>();

            foreach (var v in vertices)
            {
                if (float.IsNaN(v.y) || float.IsInfinity(v.y)) continue;

                if (v.y < min) min = v.y;
                if (v.y > max) max = v.y;

                var bin = Mathf.FloorToInt(v.y);
                bins.TryGetValue(bin, out var count);
                bins[bin] = count + 1;
            }

            var threshold = vertices.Length * BinReportShare;
            var reported = bins
                .Where(b => b.Value >= threshold)
                .OrderBy(b => b.Key)
                .Select(b => $"y={b.Key}: {b.Value}");

            Record(
                $"QuestTree E2: {map} navmesh {vertices.Length} verts y {F(min)}..{F(max)}, " +
                $"spawn median y {SpawnMedianText()}, bins: {string.Join(", ", reported.ToArray())}");
        }

        /// <summary>Median Y of EFT.Game.Spawning.SpawnPointMarker.Position (which is just the
        /// marker's transform position). The markers come from LocationScene's registered
        /// SpawnPointMarkers array, so disabled markers count too.</summary>
        private static string SpawnMedianText()
        {
            float[] ys;

            try
            {
                ys = LocationScene.GetAll<SpawnPointMarker>()
                    .Where(m => m != null)
                    .Select(m => m.transform.position.y)
                    .ToArray();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: LocationScene.GetAll of SpawnPointMarker failed ({ex.Message}) - falling back to a scene search.");
                ys = Array.Empty<float>();
            }

            if (ys.Length == 0)
            {
                ys = FindObjectsOfType<SpawnPointMarker>()
                    .Where(m => m != null)
                    .Select(m => m.transform.position.y)
                    .ToArray();
            }

            if (ys.Length == 0) return "n/a (0 markers)";

            Array.Sort(ys);
            var median = ys.Length % 2 == 1
                ? ys[ys.Length / 2]
                : (ys[ys.Length / 2 - 1] + ys[ys.Length / 2]) * 0.5f;

            return $"{F(median)} ({ys.Length} markers)";
        }

        // --- D1-D4: what the scene and the live camera actually are ---------------------------

        /// <summary>All 32 layer names with their indices, so the cyan geometry in round 1's picture
        /// can be named and the real capture's culling mask can be written against names that exist.</summary>
        private void RunD1Layers()
        {
            var named = new List<string>();
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name)) named.Add($"{i}={name}");
            }

            Record($"QuestTree D1: layers {named.Count} named of 32: {string.Join(", ", named.ToArray())}");
        }

        /// <summary>The in-game time of day, from TOD_Sky (a MonoBehaviourSingleton, so
        /// MonoBehaviourSingleton of TOD_Sky dot Instance dot Cycle dot Hour) and from
        /// GameWorld.GameDateTime.Calculate as a second opinion. A night raid would explain a dark
        /// picture on its own.</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunD2Time(string map)
        {
            var sky = "n/a";
            try
            {
                if (MonoBehaviourSingleton<TOD_Sky>.Instantiated)
                {
                    var cycle = MonoBehaviourSingleton<TOD_Sky>.Instance?.Cycle;
                    if (cycle != null)
                    {
                        var hour = cycle.Hour;
                        var hh = Mathf.Clamp(Mathf.FloorToInt(hour), 0, 23);
                        var mm = Mathf.Clamp(Mathf.FloorToInt((hour - Mathf.Floor(hour)) * 60f), 0, 59);
                        sky = $"{hh:00}:{mm:00} (Cycle.Hour {F(hour)}, day {cycle.Day}/{cycle.Month})";
                    }
                }
            }
            catch (Exception ex)
            {
                sky = $"failed ({ex.Message})";
            }

            var world = "n/a";
            try
            {
                var dateTime = _gameWorld?.GameDateTime;
                if (dateTime != null) world = dateTime.Calculate().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                world = $"failed ({ex.Message})";
            }

            var sun = "n/a";
            try
            {
                var lights = FindObjectsOfType<Light>();
                var directional = lights.FirstOrDefault(l => l != null && l.type == LightType.Directional);
                sun = directional == null
                    ? $"none of {lights.Length} lights is directional"
                    : $"\"{directional.name}\" intensity {F(directional.intensity)}, euler {F(directional.transform.eulerAngles.x)} down, enabled {directional.enabled}, of {lights.Length} lights";
            }
            catch (Exception ex)
            {
                sun = $"failed ({ex.Message})";
            }

            Record($"QuestTree D2: {map} TOD_Sky {sky}, GameWorld time {world}, sun: {sun}");
        }

        /// <summary>What the live FPS camera is set to. The camera is reached through
        /// EFT.CameraControl.CameraManager.instance.Camera (a public static field holding the
        /// manager, and a public Camera property on it; the manager's CAMERA_NAME constant is
        /// "FPS Camera"), with Camera.main as the fallback.</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunD3MainCamera(string map)
        {
            var main = MainCamera();
            if (main == null)
            {
                Record($"QuestTree D3: {map} no live camera found (CameraManager.instance.Camera and Camera.main are both null).");
                return;
            }

            var components = "n/a";
            try
            {
                components = string.Join(", ", main.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray());
            }
            catch (Exception ex)
            {
                components = $"failed ({ex.Message})";
            }

            Record(
                $"QuestTree D3: {map} live camera \"{main.name}\" renderingPath {main.renderingPath} " +
                $"(actual {main.actualRenderingPath}), cullingmask 0x{main.cullingMask:X8} [{MaskNames(main.cullingMask)}], " +
                $"allowHDR {main.allowHDR}, allowMSAA {main.allowMSAA}, ortho {main.orthographic}, " +
                $"fov {F(main.fieldOfView)}, clip {F(main.nearClipPlane)}..{F(main.farClipPlane)}, " +
                $"clear {main.clearFlags}, occlusion {main.useOcclusionCulling}, depth {F(main.depth)}, " +
                $"components [{components}]");
        }

        /// <summary>How many of the scene's MeshRenderers are switched off. PerfectCulling bakes
        /// visibility per cell and disables renderers the player cannot see, which would make an
        /// almost black picture from anywhere but the player's own position - and no camera setting
        /// could fix it. Resources.FindObjectsOfTypeAll is used because it includes inactive
        /// objects on every Unity version; the sweep is timed because it is not cheap.</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunD4Renderers(string map)
        {
            var sw = Stopwatch.StartNew();

            var renderers = Resources.FindObjectsOfTypeAll<MeshRenderer>();
            var total = renderers.Length;
            var enabled = 0;
            var active = 0;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (r.enabled) enabled++;
                if (r.enabled && r.gameObject.activeInHierarchy) active++;
            }

            var culling = 0;
            try
            {
                culling = FindObjectsOfType<Koenigz.PerfectCulling.PerfectCullingBakingBehaviour>().Length;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: could not count PerfectCulling behaviours ({ex.Message}).");
                culling = -1;
            }

            Record(
                $"QuestTree D4: {map} meshrenderers {total}, enabled {enabled}, enabled+active {active}, " +
                $"PerfectCulling behaviours {culling}, swept in {Ms(sw.Elapsed.TotalMilliseconds)} ms");
        }

        // --- E1: the four renders -------------------------------------------------------------

        private static string Label(Variant variant) => variant switch
        {
            Variant.Fresh => "V1 fresh",
            Variant.CopyMain => "V2 copymain",
            Variant.InstantiateMain => "V2b instantiate",
            Variant.Unlit => "V3 unlit",
            Variant.Bright => "V4 bright",
            _ => variant.ToString(),
        };

        private static string FileTag(Variant variant) => variant switch
        {
            Variant.Fresh => "V1-fresh",
            Variant.CopyMain => "V2-copymain",
            Variant.InstantiateMain => "V2b-instantiate",
            Variant.Unlit => "V3-unlit",
            Variant.Bright => "V4-bright",
            _ => variant.ToString(),
        };

        /// <summary>One 1024 tile of the NavMesh box, built the way this variant says, written to
        /// its own PNG and its own log line.</summary>
        /// <param name="map">The map name, for the file and log line.</param>
        /// <param name="variant">Which camera setup to test.</param>
        private void RunE1(string map, Variant variant)
        {
            var extent = RenderExtent();
            if (!extent.Valid)
            {
                Record($"QuestTree E1 {Label(variant)}: {map} skipped - no extent from E4.");
                return;
            }

            var note = BuildCamera(extent, variant);
            if (_camera == null)
            {
                Record($"QuestTree E1 {Label(variant)}: {map} skipped - {note}");
                return;
            }

            Shader replacement = null;
            if (variant == Variant.Unlit)
            {
                replacement = FindUnlitShader(out var shaderName);
                note = $"{note}, shader {shaderName}";

                if (replacement == null)
                {
                    Record($"QuestTree E1 {Label(variant)}: {map} skipped - no replacement shader resolved ({shaderName})");
                    return;
                }
            }

            var white = variant == Variant.Unlit || variant == Variant.Bright;
            var shot = Render(SmallTile, replacement, white);

            Record(
                $"QuestTree E1 {Label(variant)}: {map} ortho {SmallTile} from y={F(_cameraY)}, size {F(_orthoSize)}, " +
                $"cullingmask 0x{_camera.cullingMask:X8} [{MaskNames(_camera.cullingMask)}], " +
                $"renderingPath {_camera.renderingPath}/{_camera.actualRenderingPath}, allowHDR {_camera.allowHDR}, " +
                $"ambient {(white ? "forced white" : "as the scene has it")}, {note}, " +
                $"{Ms(shot.RenderMs + shot.ReadMs)} ms render+readback, png {(shot.Png?.Length ?? 0)} bytes");

            Save($"{FileStem(map)}-E1-{FileTag(variant)}-{SmallTile}.png", shot.Png);
        }

        private static Shader FindUnlitShader(out string resolved)
        {
            var tried = new List<string>();

            foreach (var name in UnlitShaderNames)
            {
                Shader shader = null;
                try
                {
                    shader = Shader.Find(name);
                }
                catch (Exception ex)
                {
                    tried.Add($"{name} threw {ex.GetType().Name}");
                    continue;
                }

                if (shader != null)
                {
                    resolved = $"\"{name}\" (tried {tried.Count} before it)";
                    return shader;
                }

                tried.Add(name);
            }

            resolved = $"none of [{string.Join(", ", tried.ToArray())}]";
            return null;
        }

        // --- E3: readback cost ----------------------------------------------------------------

        /// <summary>The same view at 2048x2048 on a plain fresh camera, timing ReadPixels and
        /// EncodeToPNG separately: the question is only what one tile's readback costs in a live
        /// raid, and round 1 already answered it - this is kept so round 2 confirms it unchanged.</summary>
        /// <param name="map">The map name, for the file and log line.</param>
        private void RunE3(string map)
        {
            var extent = RenderExtent();
            if (!extent.Valid)
            {
                Record($"QuestTree E3: {map} skipped - no extent from E4.");
                return;
            }

            BuildCamera(extent, Variant.Fresh);
            if (_camera == null)
            {
                Record($"QuestTree E3: {map} skipped - no camera.");
                return;
            }

            var shot = Render(LargeTile, null, false);

            Record(
                $"QuestTree E3: {map} {LargeTile} tile readpixels {Ms(shot.ReadMs)} ms, " +
                $"encode {Ms(shot.EncodeMs)} ms, png {(shot.Png?.Length ?? 0)} bytes");

            Save($"{FileStem(map)}-E3-ortho{LargeTile}.png", shot.Png);
        }

        // --- the camera -----------------------------------------------------------------------

        /// <summary>Builds the camera for one variant and frames it on the extent. Returns a note
        /// for the log line describing how it was built; leaves _camera null when the variant could
        /// not be built at all (no live camera to copy).</summary>
        /// <param name="extent">The extent the view is framed to.</param>
        /// <param name="variant">Which camera setup to build.</param>
        private string BuildCamera(Extent extent, Variant variant)
        {
            DestroyCamera();

            var centreX = (extent.MinX + extent.MaxX) * 0.5f;
            var centreZ = (extent.MinZ + extent.MaxZ) * 0.5f;
            var longer = Mathf.Max(extent.MaxX - extent.MinX, extent.MaxZ - extent.MinZ);

            _cameraY = extent.MaxY + CameraHeightAboveTop;
            _orthoSize = longer * 0.5f;

            var note = "fresh camera";

            if (variant == Variant.CopyMain || variant == Variant.InstantiateMain)
            {
                var main = MainCamera();
                if (main == null) return "no live camera to copy (CameraManager.instance.Camera and Camera.main are both null)";

                if (variant == Variant.CopyMain)
                {
                    var go = new GameObject("QuestTreeExperimentCamera");
                    _camera = go.AddComponent<Camera>();

                    // CopyFrom copies the camera's SETTINGS - rendering path, culling mask, HDR,
                    // clear flags, everything - and no components at all. That is the point of
                    // having V2b as well.
                    _camera.CopyFrom(main);
                    note = $"CopyFrom \"{main.name}\" (settings only, no components)";
                }
                else
                {
                    var clone = Instantiate(main.gameObject);
                    clone.name = "QuestTreeExperimentCameraClone";
                    clone.transform.SetParent(null, worldPositionStays: true);

                    _camera = clone.GetComponent<Camera>();
                    if (_camera == null)
                    {
                        Destroy(clone);
                        return $"the clone of \"{main.name}\" has no Camera component";
                    }

                    var disabled = QuietenClone(clone, _camera);
                    note = $"Instantiate of \"{main.name}\" ({disabled})";
                }
            }
            else
            {
                var go = new GameObject("QuestTreeExperimentCamera");
                _camera = go.AddComponent<Camera>();

                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = Color.black;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.cullingMask = variant == Variant.Bright ? BrightMask() : PlainMask();

                if (variant == Variant.Bright) note = "fresh camera, world layers only";
                if (variant == Variant.Unlit) note = "fresh camera, replacement shader";
            }

            // The framing overrides, applied last so they win over anything copied.
            var t = _camera.transform;
            t.SetParent(null, worldPositionStays: true);
            t.position = new Vector3(centreX, _cameraY, centreZ);
            t.rotation = Quaternion.Euler(90f, 0f, 0f);

            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.orthographicSize = _orthoSize;
            _camera.aspect = 1f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 1000f;
            _camera.useOcclusionCulling = false;
            _camera.depth = -100f;
            _camera.targetTexture = null;

            if (variant == Variant.CopyMain || variant == Variant.InstantiateMain)
            {
                // Kept from the copy: cullingMask, renderingPath, allowHDR. Overridden because the
                // picture is useless otherwise: a black solid clear instead of the skybox or
                // whatever the FPS camera was clearing with.
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = Color.black;
            }

            // The far plane is fixed at 1000 for this experiment. If the extent's own top is high
            // enough that the ground falls outside it the picture comes back black, and a reader of
            // the log should not have to work that out from the PNG.
            var reach = _cameraY - extent.MinY;
            if (reach > _camera.farClipPlane)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: map-capture experiment camera is {F(reach)} m above the extent's floor, " +
                    $"past its {F(_camera.farClipPlane)} m far plane - expect a black picture.");
            }

            return note;
        }

        /// <summary>The live first-person camera: EFT.CameraControl.CameraManager.instance.Camera,
        /// or Camera.main if the manager is not up.</summary>
        private static Camera MainCamera()
        {
            try
            {
                var manager = EFT.CameraControl.CameraManager.instance;
                if (manager != null && manager.Camera != null) return manager.Camera;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: CameraManager.instance.Camera failed ({ex.Message}) - using Camera.main.");
            }

            return Camera.main;
        }

        /// <summary>Switches off everything on a cloned camera object that could act on its own:
        /// every Behaviour but the camera itself (post-processing, the audio listener, the game's
        /// own scripts, which would run against a camera they were not built for), and every child,
        /// which on the FPS camera is optics and effects with cameras of their own that would
        /// otherwise render to the screen. Disabling rather than destroying, because Destroy on a
        /// component something else requires is its own kind of failure.</summary>
        /// <param name="clone">The cloned GameObject.</param>
        /// <param name="keep">The camera to leave alone.</param>
        private static string QuietenClone(GameObject clone, Camera keep)
        {
            var behaviours = 0;
            var children = 0;

            foreach (var behaviour in clone.GetComponents<Behaviour>())
            {
                if (behaviour == null || ReferenceEquals(behaviour, keep)) continue;

                try
                {
                    behaviour.enabled = false;
                    behaviours++;
                }
                catch
                {
                    // A component that will not be switched off is not worth taking the run down for.
                }
            }

            for (var i = clone.transform.childCount - 1; i >= 0; i--)
            {
                var child = clone.transform.GetChild(i);
                if (child == null) continue;

                try
                {
                    Destroy(child.gameObject);
                    children++;
                }
                catch
                {
                    // Same.
                }
            }

            return $"{behaviours} behaviours disabled, {children} children destroyed";
        }

        /// <summary>Everything except the four named layers of round 1.</summary>
        private int PlainMask()
        {
            var mask = ~0;
            var missing = new List<string>();

            foreach (var name in ExcludedLayerNames)
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0)
                {
                    missing.Add(name);
                    continue;
                }

                mask &= ~(1 << layer);
            }

            _missingLayers = missing.Count == 0 ? "" : string.Join(", ", missing.ToArray());

            if (missing.Count > 0)
            {
                // A Phase 0 finding in its own right: the real capture cannot exclude a layer this
                // game version does not have under that name.
                Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment found no layer named {_missingLayers}.");
            }

            return mask;
        }

        /// <summary>V4's mask: only layers that actually have a name, minus the four of round 1 and
        /// minus anything whose name reads like debug, trigger, collider, interactive or loot
        /// geometry - the candidates for the cyan blocks.</summary>
        private static int BrightMask()
        {
            var mask = 0;

            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (ExcludedLayerNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (V4DroppedSubstrings.Any(s => name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                mask |= 1 << i;
            }

            return mask;
        }

        private static string MaskNames(int mask)
        {
            var names = new List<string>();

            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;

                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }

            return string.Join(", ", names.ToArray());
        }

        /// <summary>Renders the camera once into a square RenderTexture, reads it back and encodes
        /// it, timing the three parts. Fog is off for the render, ambient light is forced flat white
        /// when asked, and every one of those is restored whatever happens - as is the previously
        /// active RenderTexture.</summary>
        /// <param name="size">Side of the square tile in pixels.</param>
        /// <param name="replacement">A replacement shader to render the whole scene with, or null
        /// for the materials the scene actually uses.</param>
        /// <param name="whiteAmbient">Whether to force flat white ambient light for the frame.</param>
        private Shot Render(int size, Shader replacement, bool whiteAmbient)
        {
            var shot = new Shot();

            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;

            var fog = RenderSettings.fog;
            var ambientMode = RenderSettings.ambientMode;
            var ambientLight = RenderSettings.ambientLight;
            var ambientIntensity = RenderSettings.ambientIntensity;

            try
            {
                RenderSettings.fog = false;

                if (whiteAmbient)
                {
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = Color.white;
                    RenderSettings.ambientIntensity = 1f;
                }

                _camera.targetTexture = rt;

                var sw = Stopwatch.StartNew();
                if (replacement != null) _camera.RenderWithShader(replacement, "");
                else _camera.Render();
                shot.RenderMs = sw.Elapsed.TotalMilliseconds;

                RenderTexture.active = rt;

                // Apply is counted as part of the readback: E3's question is what the whole hitch
                // costs, and this is the half of it that is not the encode.
                sw.Reset();
                sw.Start();
                tex.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                tex.Apply(false);
                shot.ReadMs = sw.Elapsed.TotalMilliseconds;

                sw.Reset();
                sw.Start();
                shot.Png = tex.EncodeToPNG();
                shot.EncodeMs = sw.Elapsed.TotalMilliseconds;
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientIntensity = ambientIntensity;
                RenderTexture.active = previousActive;
                if (_camera != null) _camera.targetTexture = null;
                rt.Release();
                Destroy(rt);
                Destroy(tex);
            }

            return shot;
        }

        private void DestroyCamera()
        {
            if (_camera == null) return;

            var go = _camera.gameObject;
            _camera.targetTexture = null;
            _camera.enabled = false;
            _camera = null;
            Destroy(go);
        }

        /// <summary>What one render produced: the PNG bytes and the three costs in milliseconds.</summary>
        private sealed class Shot
        {
            public byte[] Png;
            public double RenderMs;
            public double ReadMs;
            public double EncodeMs;
        }

        // --- output ---------------------------------------------------------------------------

        private void Record(string line)
        {
            _lines.Add(line);
            Plugin.LogSource?.LogInfo(line);
        }

        /// <summary>Writes every line beside the PNGs, so the results survive a log that has rolled
        /// over and can be pasted back whole.</summary>
        /// <param name="map">The map name, for the file name.</param>
        /// <param name="stamp">The run's timestamp, for the file name.</param>
        private void WriteReport(string map, string stamp)
        {
            var dir = ExperimentsDir();
            if (dir == null) return;

            var text = new StringBuilder();
            text.AppendLine($"QuestTree map-capture experiment (throwaway, Phase 0 round 2) - {map} - {stamp}");
            text.AppendLine($"mod {ModInfo.Version}, layers not found: {(string.IsNullOrEmpty(_missingLayers) ? "none" : _missingLayers)}");
            text.AppendLine();

            foreach (var line in _lines) text.AppendLine(line);

            var path = Path.Combine(dir, $"{FileStem(map)}-{stamp}.txt");
            File.WriteAllText(path, text.ToString());
            Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment wrote {path}.");
        }

        private void Save(string fileName, byte[] png)
        {
            if (png == null || png.Length == 0)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: map-capture experiment encoded no bytes for {fileName}.");
                return;
            }

            var dir = ExperimentsDir();
            if (dir == null) return;

            var path = Path.Combine(dir, fileName);
            File.WriteAllBytes(path, png);
            Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment wrote {path} ({png.Length} bytes).");
        }

        /// <summary>BepInEx/plugins/QuestTree/experiments/, created on demand. Null when the plugin
        /// has no file location, the same case KappaQuests guards.</summary>
        private static string ExperimentsDir()
        {
            var modPath = Path.GetDirectoryName(typeof(MapCaptureExperiment).Assembly.Location);
            if (string.IsNullOrEmpty(modPath))
            {
                Plugin.LogSource?.LogWarning("QuestTree: the plugin has no file location, so the experiment files cannot be written.");
                return null;
            }

            var dir = Path.Combine(modPath, "experiments");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string MapName()
        {
            try
            {
                var map = _gameWorld?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(map)) map = _gameWorld?.LocationId;
                return string.IsNullOrEmpty(map) ? "unknown" : map;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string FileStem(string map)
        {
            var stem = new StringBuilder(map.Length);
            foreach (var c in map)
            {
                stem.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return stem.Length == 0 ? "unknown" : stem.ToString();
        }

        private static string F(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "n/a" : v.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Ms(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Describe(Extent e) =>
            e.Valid
                ? $"[{F(e.MinX)},{F(e.MinZ)}..{F(e.MaxX)},{F(e.MaxZ)}]"
                : "[none]";

        // --- extents --------------------------------------------------------------------------

        /// <summary>One candidate extent: an XZ rectangle, the Y span it was read from, where it
        /// came from and how many objects it was built out of.</summary>
        private struct Extent
        {
            public bool Valid;
            public string Source;
            public int Count;
            public float MinX;
            public float MinZ;
            public float MaxX;
            public float MaxZ;
            public float MinY;
            public float MaxY;
        }

        /// <summary>Accumulates a 3D AABB over whatever it is given.</summary>
        private sealed class Box
        {
            public Box(string source) => _source = source;

            private readonly string _source;

            public int Count;

            private float _minX = float.MaxValue;
            private float _minY = float.MaxValue;
            private float _minZ = float.MaxValue;
            private float _maxX = float.MinValue;
            private float _maxY = float.MinValue;
            private float _maxZ = float.MinValue;
            private bool _any;

            public void Add(Bounds bounds)
            {
                Add(bounds.min);
                Add(bounds.max);
            }

            public void Add(Vector3 p)
            {
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return;
                if (float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) return;

                if (p.x < _minX) _minX = p.x;
                if (p.y < _minY) _minY = p.y;
                if (p.z < _minZ) _minZ = p.z;
                if (p.x > _maxX) _maxX = p.x;
                if (p.y > _maxY) _maxY = p.y;
                if (p.z > _maxZ) _maxZ = p.z;

                _any = true;
            }

            /// <summary>The accumulated box, or an invalid extent when nothing was added or the
            /// result has no area at all - a degenerate rectangle is no more use to E1 than none.</summary>
            public Extent ToExtent() => new Extent
            {
                Valid = _any && _maxX > _minX && _maxZ > _minZ,
                Source = _source,
                Count = Count,
                MinX = _minX,
                MinZ = _minZ,
                MaxX = _maxX,
                MaxZ = _maxZ,
                MinY = _minY,
                MaxY = _maxY,
            };
        }
    }
}
