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
    /// THROWAWAY. Phase 0 of the in-house map pictures plan: four measurements that decide the
    /// constants of the real capture code. This whole file, the ModSettings.ExperimentKey entry,
    /// the one line in GameWorldStartedPatch that installs it and the two csproj references added
    /// for it (UnityEngine.AIModule, UnityEngine.TerrainModule - AIModule is wanted by the real
    /// Phase A too) are to be DELETED once Phase 0's numbers are in hand. Nothing here ships and
    /// nothing else in the mod may come to depend on it.
    ///
    /// What it measures, one step per frame in a coroutine, on a key press inside a raid:
    /// E4 where the playable extent can be read from (BorderZone colliders, Terrain, NavMesh);
    /// E2 the 1 m histogram of NavMesh vertex Y, for the floor bands;
    /// E1 whether one orthographic top-down render of the whole map has occlusion holes;
    /// E3 what ReadPixels and EncodeToPNG of a 2048 tile cost in a live raid.
    /// E4 runs first because E1 needs an extent; E2 sits before the two render frames so the
    /// camera's frames are contiguous.
    ///
    /// Every step is wrapped: a failure in one is a warning and the others still run. Nothing may
    /// throw into the game - this runs inside a raid, on the player's own frame.
    /// </summary>
    internal sealed class MapCaptureExperiment : MonoBehaviour
    {
        /// <summary>Layers left out of the capture camera, by name. Absent names are skipped and
        /// reported, which is itself a Phase 0 finding: the real capture needs the names that
        /// actually exist in this game version.</summary>
        private static readonly string[] ExcludedLayerNames = { "Player", "UI", "Weapons", "Triggers" };

        /// <summary>Fraction of NavMesh vertices a 1 m bin needs before E2 reports it.</summary>
        private const float BinReportShare = 0.005f;

        private const int SmallTile = 1024;
        private const int LargeTile = 2048;

        /// <summary>Height above the extent's top the camera is put at, per the plan.</summary>
        private const float CameraHeightAboveTop = 300f;

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
        private string _excluded = "";
        private string _missingLayers = "(not checked)";
        private float _cameraY;
        private float _orthoSize;

        /// <summary>The four report lines, indexed 0..3 as E1..E4, so the text file reads in
        /// experiment order whatever order they were produced in.</summary>
        private readonly string[] _lines = new string[4];

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

        /// <summary>One experiment per frame. The steps are plain methods rather than inline code
        /// because C# forbids a yield inside a try that has a catch, and every step must be
        /// caught.</summary>
        private IEnumerator Run()
        {
            _running = true;

            var map = MapName();
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment starting on {map}.");

            yield return null;
            Guarded("E4", () => RunE4(map));

            yield return null;
            Guarded("E2", () => RunE2(map));

            yield return null;
            Guarded("E1", () => RunE1(map));

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

            Record(3,
                $"QuestTree E4: {map} borderzones {_border.Count} {Describe(_border)} " +
                $"terrains {_terrain.Count} {Describe(_terrain)} navmesh {Describe(_navmesh)}");

            Plugin.LogSource?.LogInfo(
                $"QuestTree E4: {map} extent for E1 = {(_chosen.Valid ? _chosen.Source : "none")} " +
                $"{Describe(_chosen)}, y {(_chosen.Valid ? $"{F(_chosen.MinY)}..{F(_chosen.MaxY)}" : "n/a")}.");
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
                Plugin.LogSource?.LogInfo($"QuestTree: LocationScene.GetAll<BorderZone> failed ({ex.Message}) - falling back to a scene search.");
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

        // --- E2: floor bands ------------------------------------------------------------------

        /// <summary>1 m histogram of NavMesh vertex Y, plus the median Y of the scene's spawn point
        /// markers - the candidate for "which band is the ground floor".</summary>
        /// <param name="map">The map name, for the log line.</param>
        private void RunE2(string map)
        {
            var vertices = NavVertices();
            if (vertices == null || vertices.Length == 0)
            {
                Record(1, $"QuestTree E2: {map} navmesh 0 verts - no triangulation.");
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

            Record(1,
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
                Plugin.LogSource?.LogInfo($"QuestTree: LocationScene.GetAll<SpawnPointMarker> failed ({ex.Message}) - falling back to a scene search.");
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

        // --- E1 and E3: the render ------------------------------------------------------------

        /// <summary>One orthographic top-down render of the whole extent at 1024x1024, written to
        /// a PNG for the occlusion inspection.</summary>
        /// <param name="map">The map name, for the file and log line.</param>
        private void RunE1(string map)
        {
            if (!_chosen.Valid)
            {
                Record(0, $"QuestTree E1: {map} skipped - no extent from E4.");
                return;
            }

            BuildCamera(_chosen);

            var shot = Render(SmallTile);

            Record(0,
                $"QuestTree E1: {map} ortho {SmallTile} rendered from y={F(_cameraY)}, size {F(_orthoSize)}, " +
                $"excluded layers [{_excluded}], {Ms(shot.RenderMs + shot.ReadMs)} ms render+readback");

            Save($"{FileStem(map)}-E1-ortho{SmallTile}.png", shot.Png);
        }

        /// <summary>The same view at 2048x2048, timing ReadPixels and EncodeToPNG separately: the
        /// question is whether one tile's readback fits inside a raid frame budget. The PNG is
        /// written as well, since it is the better picture to inspect for occlusion holes and the
        /// raid that produced it is not free.</summary>
        /// <param name="map">The map name, for the file and log line.</param>
        private void RunE3(string map)
        {
            if (_camera == null)
            {
                Record(2, $"QuestTree E3: {map} skipped - no camera (E1 did not run).");
                return;
            }

            var shot = Render(LargeTile);

            Record(2,
                $"QuestTree E3: {map} {LargeTile} tile readpixels {Ms(shot.ReadMs)} ms, " +
                $"encode {Ms(shot.EncodeMs)} ms, png {(shot.Png?.Length ?? 0)} bytes");

            Save($"{FileStem(map)}-E3-ortho{LargeTile}.png", shot.Png);
        }

        /// <summary>A fresh camera of our own, with nothing of the game's post-processing on it.
        /// Disabled, so it only ever renders when Render() is called by hand, and at depth -100 so
        /// it could not draw over the player's view even if it were enabled.</summary>
        /// <param name="extent">The extent the view is framed to.</param>
        private void BuildCamera(Extent extent)
        {
            DestroyCamera();

            var centreX = (extent.MinX + extent.MaxX) * 0.5f;
            var centreZ = (extent.MinZ + extent.MaxZ) * 0.5f;
            var longer = Mathf.Max(extent.MaxX - extent.MinX, extent.MaxZ - extent.MinZ);

            _cameraY = extent.MaxY + CameraHeightAboveTop;
            _orthoSize = longer * 0.5f;

            // Left unparented on purpose: a parent with any scale or rotation of its own would
            // distort an orthographic view, and this object is destroyed by hand at the end of the
            // run and again in OnDestroy if the raid ends first.
            var go = new GameObject("QuestTreeExperimentCamera");
            go.transform.position = new Vector3(centreX, _cameraY, centreZ);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _camera = go.AddComponent<Camera>();
            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.orthographicSize = _orthoSize;
            _camera.aspect = 1f;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 1000f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.useOcclusionCulling = false;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.depth = -100f;
            _camera.cullingMask = CullingMask();

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
        }

        private int CullingMask()
        {
            var mask = ~0;
            var excluded = new List<string>();
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
                excluded.Add($"{name}({layer})");
            }

            _excluded = string.Join(", ", excluded.ToArray());
            _missingLayers = string.Join(", ", missing.ToArray());

            if (missing.Count > 0)
            {
                // A Phase 0 finding in its own right: the real capture cannot exclude a layer this
                // game version does not have under that name.
                Plugin.LogSource?.LogInfo($"QuestTree: map-capture experiment found no layer named {_missingLayers}.");
            }

            return mask;
        }

        /// <summary>Renders the camera once into a square RenderTexture, reads it back and encodes
        /// it, timing the three parts. Fog is off for the render and restored whatever happens, as
        /// is the previously active RenderTexture.</summary>
        /// <param name="size">Side of the square tile in pixels.</param>
        private Shot Render(int size)
        {
            var shot = new Shot();

            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;
            var fog = RenderSettings.fog;

            try
            {
                RenderSettings.fog = false;
                _camera.targetTexture = rt;

                var sw = Stopwatch.StartNew();
                _camera.Render();
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

        private void Record(int index, string line)
        {
            _lines[index] = line;
            Plugin.LogSource?.LogInfo(line);
        }

        /// <summary>Writes the four lines beside the PNGs, so the results survive a log that has
        /// rolled over and can be pasted back whole.</summary>
        /// <param name="map">The map name, for the file name.</param>
        /// <param name="stamp">The run's timestamp, for the file name.</param>
        private void WriteReport(string map, string stamp)
        {
            var dir = ExperimentsDir();
            if (dir == null) return;

            var text = new StringBuilder();
            text.AppendLine($"QuestTree map-capture experiment (throwaway, Phase 0) - {map} - {stamp}");
            text.AppendLine($"mod {ModInfo.Version}, layers not found: {(_missingLayers.Length == 0 ? "none" : _missingLayers)}");
            text.AppendLine();

            foreach (var line in _lines)
            {
                text.AppendLine(line ?? "(missing - the step failed, see the log)");
            }

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
