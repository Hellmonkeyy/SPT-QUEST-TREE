using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Takes the map's picture from inside a raid: one orthographic top-down render per floor of the
    /// rectangle <see cref="MapExtentProbe"/> measured, written beside the plugin as PNGs plus one
    /// meta file the Maps tab reads.
    ///
    /// Why the game has to draw it: every map picture the Maps tab has ever shown came out of the
    /// DynamicMaps mod's art folder, so a map that mod does not ship - any modded location - has
    /// never had one. The game itself, on the other hand, has the whole world loaded in front of a
    /// camera we are free to point downwards.
    ///
    /// The geometry is the contract. Pixels are square (one <see cref="Plan.Ppm"/> for both axes),
    /// image right is world +x, image top is world +z, and pixel (0,0) is the extent's
    /// (minX, maxZ) corner - which is exactly what an orthographic camera at Euler(90,0,0) produces,
    /// and is CHECKED rather than assumed: see <see cref="SelfCheck"/>. The meta carries the same
    /// extent, to the same doubles, that the harvest sent the server, so a pin the server places
    /// from a harvested trigger lands on the picture without anything having to agree twice.
    ///
    /// Cost. Phase 0 timed a 2048 tile in a live raid at 10-61 ms to read back and 58-68 ms to
    /// encode, so the work is spread: ONE tile render plus readback per frame, then ONE floor's
    /// EncodeToPNG per frame once its tiles are in. Encoding cannot be moved off the main thread -
    /// EncodeToPNG is a Texture2D method and touches the managed object - so the frame it lands on
    /// is a ~60 ms hitch, once per floor, on a key the player pressed.
    ///
    /// Everything here is guarded and reversible. It runs on a player's raid frame: fog and ambient
    /// light are restored by the same statement that changed them, the camera is destroyed in a
    /// finally and again in OnDestroy, and no failure is allowed to reach the game.
    /// </summary>
    internal sealed class MapCapture : MonoBehaviour
    {
        /// <summary>The shape of the meta file. Bumped when a reader would misread the old one.</summary>
        private const int SchemaVersion = 1;

        /// <summary>Side of one render, in pixels. 2048 because Phase 0 measured that tile's readback
        /// at 10-61 ms and its encode at 58-68 ms in a live raid - one per frame is affordable, and a
        /// larger tile would be a longer hitch for no gain.</summary>
        private const int TileSize = 2048;

        /// <summary>Most pixels per metre, whatever the resolution setting allows. A capture is a
        /// photograph of a 3D scene: past about two pixels to the metre there is no more detail in
        /// the world to record, only a bigger file. It matters for the small maps - Factory's extent
        /// is a couple of hundred metres, and without this cap a 4096 long side would ask for twenty
        /// pixels per metre.</summary>
        private const float MaxPixelsPerMetre = 2f;

        /// <summary>Metres above a floor band's top the camera sits. Three: enough that the band's
        /// own ceiling geometry is behind the near plane and the floor is seen from above, little
        /// enough that the storey above is clipped away rather than drawn over it.</summary>
        private const float CameraHeightAboveBand = 3f;

        private const float NearClip = 0.05f;

        /// <summary>Added to the far plane so the band's own floor is comfortably inside it rather
        /// than exactly on it.</summary>
        private const float FarClipSlack = 1f;

        /// <summary>A floor's PNG is not written past this. 12 MB is well over what a 4096-pixel
        /// render of a real map encodes to (a few MB), so hitting it means something is wrong -
        /// noise, or a resolution nobody wants in a release zip.</summary>
        private const int MaxFloorBytes = 12 * 1024 * 1024;

        /// <summary>How far a projected point may sit from where the meta's arithmetic puts it, in
        /// pixels, before the floor is abandoned. One pixel: the two calculations are of the same
        /// linear map and should agree to a rounding error, so anything visible is a real
        /// disagreement.</summary>
        private const float SelfCheckTolerance = 1f;

        /// <summary>Metres north the orientation probe steps. Ten is far enough that its pixel
        /// distance is unambiguous at any resolution this code produces and short enough to stay
        /// inside the tile being checked.</summary>
        private const float NorthProbeMetres = 10f;

        /// <summary>Layers kept out of the picture, by name: the player's own body and weapon, the
        /// UI, the invisible quest trigger volumes, and the transparent effects layer. Names absent
        /// from this game version are skipped, and the whole list is logged once so what was actually
        /// excluded is on the record rather than assumed.</summary>
        private static readonly string[] ExcludedLayerNames = { "Player", "UI", "Weapons", "Triggers", "TransparentFX" };

        /// <summary>Replacement shaders <see cref="RenderMode.Unlit"/> tries, in order; Shader.Find
        /// returns null for one the build stripped, so the first that resolves is used.</summary>
        private static readonly string[] UnlitShaderNames =
        {
            "Unlit/Texture", "Unlit/Color", "Mobile/Unlit (Supports Lightmap)", "Sprites/Default",
            "Legacy Shaders/Diffuse", "Standard"
        };

        /// <summary>Leading words stripped from a BotZone's name before it becomes a label.</summary>
        private static readonly string[] ZoneNamePrefixes = { "BotZone", "Zone" };

        /// <summary>
        /// How the camera that takes the picture is built and rendered.
        ///
        /// TODO(1.19.0, Phase 0 round 2): choose the winner and set <see cref="Rendering"/> to it.
        /// Round 1 on Customs came back almost entirely black in daylight - a few faint building
        /// outlines and some cyan debug-looking blocks - so <see cref="Fresh"/>, which is what the
        /// plan assumed, does NOT draw EFT's world. Round 2 renders the same view five ways
        /// (MapCaptureExperiment V1-V4) to separate the causes, and exactly one line changes here
        /// when it reports:
        ///   1 <see cref="CopyMain"/>        - the live FPS camera's settings (rendering path, HDR,
        ///     culling mask) on a camera of our own. Cheapest fix if the settings were the problem.
        ///   2 <see cref="InstantiateMain"/> - a clone of the FPS camera's GameObject with its own
        ///     scripts disabled, for the case where a COMPONENT on it is what draws the world.
        ///   3 <see cref="Unlit"/>           - a replacement shader plus flat white ambient, for the
        ///     case where the geometry is there and only the lighting was missing. Also the variant
        ///     that would give a flat, legible map rather than a screenshot.
        ///   4 <see cref="Bright"/>          - white ambient with the culling mask cut to the named
        ///     world layers, if the blackness is lighting and the cyan blocks are one layer.
        /// Everything that differs between them lives in <see cref="BuildCamera"/> and
        /// <see cref="RenderOnce"/> and nowhere else, so the switch is a one-line change and the
        /// tiling, the self-check, the files and the meta are untouched by it.
        /// </summary>
        private enum RenderMode
        {
            /// <summary>A bare new Camera. Round 1's control, and known to render black.</summary>
            Fresh,

            /// <summary>A new Camera with CopyFrom of the live FPS camera applied first.</summary>
            CopyMain,

            /// <summary>A clone of the FPS camera's whole GameObject, its other scripts disabled.</summary>
            InstantiateMain,

            /// <summary>Fresh, rendered through a replacement shader with flat white ambient.</summary>
            Unlit,

            /// <summary>Fresh, flat white ambient, culling mask cut to the named world layers.</summary>
            Bright,
        }

        /// <summary>The variant in use. Fresh until round 2 of the experiment names the winner - see
        /// the TODO on <see cref="RenderMode"/>. Deliberately NOT a setting: a player has no way to
        /// judge which of these renders their map correctly, and shipping the question as a dropdown
        /// would be shipping the bug. Static readonly rather than const so the four variants stay
        /// compiled and reachable - a const folds the comparisons and the compiler then reports the
        /// unchosen ones as unreachable code, which is a warning this project treats as an
        /// error.</summary>
        private static readonly RenderMode Rendering = RenderMode.Fresh;

        /// <summary>Adds the capture key's watcher to a raid that has just started, from
        /// <see cref="QuestTree.Patches.GameWorldStartedPatch"/> - the same place the zone harvester
        /// is started, and behind the same HarvestZones gate, because a capture is the picture half
        /// of exactly that job. Its GameObject hangs off the GameWorld, so it dies with the raid.
        /// Never throws.</summary>
        /// <param name="gameWorld">The raid's world, as handed to the patch.</param>
        public static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;

                // A Fika headless client has no player, no camera and nobody to press a key.
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeMapCapture");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);

                var runner = go.AddComponent<MapCapture>();
                runner._gameWorld = gameWorld;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the map capture key ({ex.Message}).");
            }
        }

        private GameWorld _gameWorld;
        private bool _running;
        private bool _warnedOnPoll;

        private Camera _camera;
        private RenderTexture _rt;
        private Shader _replacement;

        /// <summary>The capture in progress, held in a field as well as on the coroutine's stack so
        /// that OnDestroy can free its textures. A raid ending mid-capture destroys this object, and
        /// Unity abandoning a coroutine is not guaranteed to run the finally that would otherwise
        /// release a 50 MB picture.</summary>
        private Plan _plan;

        private static bool _loggedLayers;

        // --- the key ---------------------------------------------------------------------------

        private void Update()
        {
            if (!ModSettings.Ready || ModSettings.CaptureMapKey == null) return;

            try
            {
                var shortcut = ModSettings.CaptureMapKey.Value;
                if (shortcut.MainKey == KeyCode.None) return;
                if (!shortcut.IsDown()) return;

                if (_running)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: a map capture is already running - the key does nothing until it has finished.");
                    return;
                }

                if (!PlayerIsAlive())
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the map capture key works inside a raid with a living player only - " +
                        "nothing was captured.");
                    return;
                }

                // Set here rather than only inside the coroutine: Update can run again before the
                // coroutine's first statement, and two captures at once would fight over the camera.
                _running = true;
                StartCoroutine(Run());
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the map capture key failed ({ex.Message}).");
            }
        }

        private void OnDestroy() => Cleanup();

        /// <summary>Whether there is a raid with a living player to photograph. A dead player's world
        /// is still loaded, but the screen has moved on and the frames are not the player's to
        /// spend.</summary>
        private bool PlayerIsAlive()
        {
            try
            {
                var player = _gameWorld?.MainPlayer;
                if (player == null) return false;

                var health = player.HealthController;
                return health != null && health.IsAlive;
            }
            catch
            {
                return false;
            }
        }

        // --- the run ---------------------------------------------------------------------------

        /// <summary>One capture, spread over frames: the measurement, then one tile render per frame,
        /// then one floor's encode per frame, then the meta.
        ///
        /// Written as guarded steps returning a bool rather than one try/catch, because C# forbids a
        /// yield inside a try that has a catch. The try/finally around the whole body is allowed and
        /// is what guarantees the camera goes away - including when an exception escapes a step, or
        /// when Unity stops the coroutine because the raid ended mid-capture.</summary>
        private IEnumerator Run()
        {
            var clock = Stopwatch.StartNew();
            Plan plan = null;

            try
            {
                if (!Prepare(out plan)) yield break;

                _plan = plan;

                yield return null;

                foreach (var floor in plan.Floors)
                {
                    if (!BeginFloor(plan, floor))
                    {
                        floor.Failed = true;
                        continue;
                    }

                    for (var tile = 0; tile < plan.TileCount; tile++)
                    {
                        RenderTile(plan, floor, tile);
                        if (floor.Failed) break;

                        // The whole point of the coroutine: one render and one readback per frame,
                        // never two.
                        yield return null;
                    }

                    if (!floor.Failed)
                    {
                        // The encode is its own frame for the same reason - Phase 0 measured it at
                        // 58-68 ms, which is a visible hitch on its own.
                        yield return null;
                        FinishFloor(plan, floor);
                    }

                    ReleaseTexture(floor);
                    yield return null;
                }

                WriteMeta(plan, clock);
            }
            finally
            {
                Cleanup();
                _running = false;
            }
        }

        /// <summary>Everything that has to be true before a single pixel is rendered: a usable map
        /// name, a folder to write into, the extent and floors the harvest measured, the pixel
        /// geometry derived from them, the camera, and the labels. False - having said why - means
        /// nothing is captured at all.
        ///
        /// This is the one step that can cost a frame before any rendering: pressing the key before
        /// the harvester's second pass has run (27 s in) means the extent has not been measured yet,
        /// and MapExtentProbe then triangulates the NavMesh here. Its own timing is in the debug line
        /// below, so a long press-to-first-tile gap has an explanation on the record.</summary>
        /// <param name="plan">The capture's plan, or null on failure.</param>
        private bool Prepare(out Plan plan)
        {
            plan = null;

            try
            {
                var clock = Stopwatch.StartNew();
                var key = MapKey();

                if (!IsUsableKey(key))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured - \"{key ?? "(no map name)"}\" cannot be a folder name " +
                        "(letters, digits, dash and underscore, 1 to 64 of them), so the capture has nowhere to go.");
                    return false;
                }

                var dir = CaptureDir(key);
                if (dir == null) return false;

                // The extent the HARVEST sent, when this raid has already measured it - not a second
                // measurement that could differ. See MapExtentProbe.TryProbeForCapture.
                var extent = MapExtentProbe.TryProbeForCapture(key);
                if (extent == null)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - its extent could not be measured, or was " +
                        "measured and rejected (the warning above says which), and a picture with no rectangle " +
                        "to draw to could never be placed on the map.");
                    return false;
                }

                if (extent.Floors == null || extent.Floors.Count == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - its extent carries no floors.");
                    return false;
                }

                var widthM = extent.MaxX - extent.MinX;
                var heightM = extent.MaxZ - extent.MinZ;
                var longSide = Math.Max(widthM, heightM);

                if (!IsFinite(widthM) || !IsFinite(heightM) || widthM <= 0d || heightM <= 0d)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - its extent has no area " +
                        $"({F(widthM)}x{F(heightM)} m).");
                    return false;
                }

                var cap = Resolution();
                var ppm = (float)Math.Min(cap / longSide, MaxPixelsPerMetre);

                if (!(ppm > 0f))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - {F(longSide)} m at a {cap} px cap gives no " +
                        "usable pixel size.");
                    return false;
                }

                plan = new Plan
                {
                    Key = key,
                    Dir = dir,
                    Extent = extent,
                    Cap = cap,
                    Ppm = ppm,
                    WidthPx = (int)Math.Ceiling(widthM * ppm),
                    HeightPx = (int)Math.Ceiling(heightM * ppm),
                };

                if (plan.WidthPx < 1 || plan.HeightPx < 1)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - {F(widthM)}x{F(heightM)} m at " +
                        $"{plan.Ppm.ToString("0.###", CultureInfo.InvariantCulture)} px/m is not a picture.");
                    plan = null;
                    return false;
                }

                plan.TilesX = (plan.WidthPx + TileSize - 1) / TileSize;
                plan.TilesY = (plan.HeightPx + TileSize - 1) / TileSize;

                foreach (var floor in extent.Floors)
                {
                    if (floor == null) continue;
                    plan.Floors.Add(new FloorPlan
                    {
                        Dto = floor,
                        File = $"{key}-{floor.Level.ToString(CultureInfo.InvariantCulture)}.png"
                    });
                }

                if (plan.Floors.Count == 0)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - it has no usable floor.");
                    plan = null;
                    return false;
                }

                plan.Labels = Labels(plan);

                if (!BuildCamera(plan, out var note))
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - {note}.");
                    plan = null;
                    return false;
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capturing {key} - {plan.WidthPx}x{plan.HeightPx} px, " +
                    $"{MetresPerPixel(plan.Ppm)} m/px, {plan.TilesX}x{plan.TilesY} tiles of {TileSize}, " +
                    $"{plan.Floors.Count} floor(s), {plan.Labels.Count} label(s), {note}.");

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {key} capture geometry: extent {F(extent.MinX)},{F(extent.MinZ)}.." +
                    $"{F(extent.MaxX)},{F(extent.MaxZ)} ({extent.Source}), cap {cap}, " +
                    $"prepared in {Ms(clock.Elapsed.TotalMilliseconds)} ms.");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: nothing was captured - the capture could not be set up " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                plan = null;
                return false;
            }
        }

        /// <summary>Frames the camera on one floor band, proves the framing against the meta's own
        /// arithmetic, and allocates that floor's picture. False means this floor is skipped and the
        /// others still run.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being started.</param>
        private bool BeginFloor(Plan plan, FloorPlan floor)
        {
            try
            {
                floor.Clock = Stopwatch.StartNew();

                var minY = floor.Dto.MinY;
                var maxY = floor.Dto.MaxY;

                // maxY EQUAL to minY is refused as well, not only below it: a band of no height is a
                // floor nothing can stand on, and tools/check-capture.py rejects one - so the mod
                // must not write what its own checker would fail.
                if (!IsFinite(minY) || !IsFinite(maxY) || maxY <= minY)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" was not captured - its height band is " +
                        $"{F(minY)}..{F(maxY)}.");
                    return false;
                }

                floor.CameraY = maxY + CameraHeightAboveBand;

                // Everything from the camera down to the band's floor, and nothing above the band:
                // the storey overhead is in front of the near plane and is not drawn.
                var far = floor.CameraY - minY + FarClipSlack;
                if (!(far > NearClip))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" was not captured - its band is thinner than " +
                        "the camera's near plane.");
                    return false;
                }

                _camera.nearClipPlane = NearClip;
                _camera.farClipPlane = far;
                _camera.orthographicSize = TileSize / (2f * plan.Ppm);
                _camera.aspect = 1f;

                if (!SelfCheck(plan, floor, out var why))
                {
                    // The check that can fail. Abandoning the floor is the point: a picture whose
                    // pixels do not mean what the meta says they mean puts every pin in the wrong
                    // place, and nothing downstream could tell.
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" was not captured - the camera does not agree " +
                        $"with the picture's own geometry: {why}.");
                    return false;
                }

                floor.Texture = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGB24, mipChain: false);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was not captured " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>Renders one tile and reads it into the floor's picture at that tile's pixel
        /// offset. The last row and column of tiles are rendered whole and read back clipped, so
        /// every tile is the same view size and the arithmetic the self-check proved holds for all of
        /// them.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being rendered.</param>
        /// <param name="tile">The tile's index, row-major from the top-left.</param>
        private void RenderTile(Plan plan, FloorPlan floor, int tile)
        {
            var previousActive = RenderTexture.active;

            try
            {
                var tileX = tile % plan.TilesX;
                var tileY = tile / plan.TilesX;
                var px0 = tileX * TileSize;
                var py0 = tileY * TileSize;

                var tw = Math.Min(TileSize, plan.WidthPx - px0);
                var th = Math.Min(TileSize, plan.HeightPx - py0);
                if (tw <= 0 || th <= 0) return;

                PositionCamera(plan, floor, px0, py0);
                RenderOnce();

                RenderTexture.active = _rt;

                // Unity counts both the RenderTexture's and the Texture2D's rows from the BOTTOM,
                // and EncodeToPNG writes the top row of the texture as the first row of the image.
                // So image row 0 is texture row HeightPx-1, the tile's own top rows are the TOP of
                // the render (y from TileSize-th up), and they land at texture row
                // HeightPx-py0-th upwards. A clipped tile therefore drops its bottom and right,
                // which is exactly the part that lies outside the extent.
                floor.Texture.ReadPixels(new Rect(0f, TileSize - th, tw, th), px0, plan.HeightPx - py0 - th);
                floor.Tiles++;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was abandoned at tile {tile + 1} of " +
                    $"{plan.TileCount} ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>Encodes a finished floor and writes it, unless it came out implausibly large.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose tiles are all in.</param>
        private void FinishFloor(Plan plan, FloorPlan floor)
        {
            try
            {
                floor.Texture.Apply(updateMipmaps: false);

                var png = floor.Texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    floor.Failed = true;
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" encoded to nothing and was not written.");
                    return;
                }

                if (png.Length > MaxFloorBytes)
                {
                    // Not written rather than written and large: these files ship in the release zip
                    // and are uploaded to Fika hosts, and a floor this size is a sign the picture is
                    // noise rather than a map.
                    floor.Failed = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" encoded to {png.Length} bytes, over the " +
                        $"{MaxFloorBytes / (1024 * 1024)} MB a floor may take - it was not written. Set Settings > " +
                        "Map > Capture resolution to 2048 and capture again.");
                    return;
                }

                File.WriteAllBytes(Path.Combine(plan.Dir, floor.File), png);

                floor.Bytes = png.Length;
                plan.Bytes += png.Length;

                var ms = floor.Clock?.Elapsed.TotalMilliseconds ?? 0d;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: captured {plan.Key} \"{floor.Dto.Name}\" {plan.WidthPx}x{plan.HeightPx} px " +
                    $"({MetresPerPixel(plan.Ppm)} m/px), {floor.Tiles} tiles, {Ms(ms)} ms.");
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // --- the camera ------------------------------------------------------------------------

        /// <summary>Builds the one camera every floor and tile is rendered with, and the
        /// RenderTexture it draws into. The ONLY place the rendering variant matters - see the TODO
        /// on <see cref="RenderMode"/>. False, with a note saying why, when this variant cannot be
        /// built at all.</summary>
        /// <param name="plan">The capture's plan, for the tile size the ortho view is framed to.</param>
        /// <param name="note">A phrase for the log describing how the camera was built.</param>
        private bool BuildCamera(Plan plan, out string note)
        {
            note = "fresh camera";

            if (Rendering == RenderMode.CopyMain || Rendering == RenderMode.InstantiateMain)
            {
                var main = LiveCamera();
                if (main == null)
                {
                    note = "there is no live camera to copy (CameraManager.instance.Camera and Camera.main are both null)";
                    return false;
                }

                if (Rendering == RenderMode.CopyMain)
                {
                    _camera = new GameObject("QuestTreeCaptureCamera").AddComponent<Camera>();

                    // CopyFrom copies SETTINGS - rendering path, culling mask, HDR, clear flags -
                    // and no components whatever.
                    _camera.CopyFrom(main);
                    note = $"settings copied from \"{main.name}\"";
                }
                else
                {
                    var clone = Instantiate(main.gameObject);
                    clone.name = "QuestTreeCaptureCameraClone";
                    clone.transform.SetParent(null, worldPositionStays: true);

                    _camera = clone.GetComponent<Camera>();
                    if (_camera == null)
                    {
                        Destroy(clone);
                        note = $"the clone of \"{main.name}\" has no Camera component";
                        return false;
                    }

                    note = $"a clone of \"{main.name}\" ({Quieten(clone, _camera)})";
                }

                // Whatever was copied, the picture needs a known background rather than the
                // skybox or whatever the first-person camera was clearing with.
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = Color.black;
            }
            else
            {
                _camera = new GameObject("QuestTreeCaptureCamera").AddComponent<Camera>();
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = Color.black;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;

                if (Rendering == RenderMode.Unlit)
                {
                    _replacement = UnlitShader(out var resolved);
                    if (_replacement == null)
                    {
                        note = $"no replacement shader could be resolved ({resolved})";
                        return false;
                    }

                    note = $"fresh camera, replacement shader {resolved}";
                }

                if (Rendering == RenderMode.Bright) note = "fresh camera, world layers only";
            }

            // The framing, applied last so it wins over anything copied. Unparented on purpose: a
            // parent with a scale or a rotation of its own would distort an orthographic view.
            var t = _camera.transform;
            t.SetParent(null, worldPositionStays: true);
            t.rotation = Quaternion.Euler(90f, 0f, 0f);

            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.orthographicSize = TileSize / (2f * plan.Ppm);
            _camera.aspect = 1f;
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = 1000f;
            _camera.useOcclusionCulling = false;
            _camera.depth = -100f;
            _camera.cullingMask = Rendering == RenderMode.Bright ? WorldLayerMask() : PlainMask();

            _rt = new RenderTexture(TileSize, TileSize, 24, RenderTextureFormat.ARGB32);

            // Assigned for the whole capture, not per tile: WorldToScreenPoint reads the camera's
            // pixel size from its target, and the self-check is only meaningful in the tile's own
            // 2048x2048 pixels.
            _camera.targetTexture = _rt;

            return true;
        }

        /// <summary>Points the camera at one tile. The tile's centre in world metres, derived from
        /// its pixel origin and the pixels-per-metre the meta records - the one arithmetic the
        /// self-check verifies.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being rendered, for the camera's height.</param>
        /// <param name="px0">The tile's left edge, in image pixels from the left.</param>
        /// <param name="py0">The tile's top edge, in image pixels from the top.</param>
        private void PositionCamera(Plan plan, FloorPlan floor, int px0, int py0)
        {
            var centreX = plan.Extent.MinX + (px0 + TileSize * 0.5d) / plan.Ppm;
            var centreZ = plan.Extent.MaxZ - (py0 + TileSize * 0.5d) / plan.Ppm;

            _camera.transform.position = new Vector3((float)centreX, floor.CameraY, (float)centreZ);
        }

        /// <summary>One render of the camera where it stands, with fog off and - for the variants
        /// that ask for it - flat white ambient light. Both are restored by the finally, so a
        /// player's own next frame is drawn with the scene's own settings whatever happens
        /// here.</summary>
        private void RenderOnce()
        {
            var fog = RenderSettings.fog;
            var ambientMode = RenderSettings.ambientMode;
            var ambientLight = RenderSettings.ambientLight;
            var ambientIntensity = RenderSettings.ambientIntensity;

            try
            {
                RenderSettings.fog = false;

                if (Rendering == RenderMode.Unlit || Rendering == RenderMode.Bright)
                {
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                    RenderSettings.ambientLight = Color.white;
                    RenderSettings.ambientIntensity = 1f;
                }

                if (_replacement != null) _camera.RenderWithShader(_replacement, "");
                else _camera.Render();
            }
            finally
            {
                RenderSettings.fog = fog;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientIntensity = ambientIntensity;
            }
        }

        /// <summary>The live first-person camera: EFT.CameraControl.CameraManager.instance.Camera,
        /// or Camera.main when the manager is not up.</summary>
        private static Camera LiveCamera()
        {
            try
            {
                var manager = EFT.CameraControl.CameraManager.instance;
                if (manager != null && manager.Camera != null) return manager.Camera;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: CameraManager.instance.Camera failed ({ex.Message}) - using Camera.main.");
            }

            return Camera.main;
        }

        /// <summary>Switches off everything on a cloned camera object that could act on its own -
        /// post-processing, the audio listener, the game's own scripts - and destroys its children,
        /// which on the first-person camera are optics and effects with cameras of their own that
        /// would otherwise render to the screen.</summary>
        /// <param name="clone">The cloned GameObject.</param>
        /// <param name="keep">The camera to leave alone.</param>
        private static string Quieten(GameObject clone, Camera keep)
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
                    // A component that will not be switched off is not worth taking a capture down for.
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

        /// <summary>Everything except the named layers, with what was actually excluded logged once
        /// per session: a layer this game version does not carry under that name cannot be excluded,
        /// and that belongs on the record rather than in an assumption.</summary>
        private static int PlainMask()
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

            if (!_loggedLayers)
            {
                _loggedLayers = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: map captures leave out layers [{string.Join(", ", excluded.ToArray())}]" +
                    (missing.Count == 0
                        ? "."
                        : $"; this game version has no layer named {string.Join(", ", missing.ToArray())}, so nothing " +
                          "was excluded for those."));
            }

            return mask;
        }

        /// <summary>The Bright variant's mask: named layers only, minus the excluded ones.</summary>
        private static int WorldLayerMask()
        {
            var mask = 0;

            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (ExcludedLayerNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;

                mask |= 1 << i;
            }

            return mask;
        }

        private static Shader UnlitShader(out string resolved)
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
                    resolved = $"\"{name}\"";
                    return shader;
                }

                tried.Add(name);
            }

            resolved = $"none of [{string.Join(", ", tried.ToArray())}]";
            return null;
        }

        /// <summary>Gives back everything the capture holds: the floor pictures, the camera and its
        /// render target. Run from the coroutine's finally and again from OnDestroy, so it has to be
        /// safe to call twice and safe to call having captured nothing.</summary>
        private void Cleanup()
        {
            try
            {
                if (_plan != null)
                {
                    foreach (var floor in _plan.Floors) ReleaseTexture(floor);
                    _plan = null;
                }

                if (_camera != null)
                {
                    var go = _camera.gameObject;
                    _camera.targetTexture = null;
                    _camera = null;
                    Destroy(go);
                }

                if (_rt != null)
                {
                    if (ReferenceEquals(RenderTexture.active, _rt)) RenderTexture.active = null;
                    _rt.Release();
                    Destroy(_rt);
                    _rt = null;
                }

                _replacement = null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the map capture camera could not be cleaned up ({ex.Message}).");
            }
        }

        private static void ReleaseTexture(FloorPlan floor)
        {
            if (floor?.Texture == null) return;

            var tex = floor.Texture;
            floor.Texture = null;
            Destroy(tex);
        }

        // --- the check that can fail -----------------------------------------------------------

        /// <summary>Proves that the camera's own projection agrees with the linear world-to-pixel map
        /// the meta file declares, on the tile holding the extent's centre.
        ///
        /// Five points - the tile's four world corners and its centre - are projected by Unity and
        /// compared with px = (x - minX) * ppm and py = (maxZ - z) * ppm, offset by the tile's pixel
        /// origin; then a point ten metres further north must come out on a SMALLER image row than
        /// the centre, by ten metres' worth of pixels.
        ///
        /// It can fail, and these are the ways: an orthographicSize that does not match the pixels
        /// per metre (half the view, twice the scale), an aspect other than 1 against a square
        /// target, a camera rotation whose up vector is not world +z (the whole picture upside down,
        /// which no later code could notice because a map is roughly symmetric), a tile origin
        /// computed in the wrong direction, or a target texture whose size is not the tile's - all of
        /// them produce a picture that looks like a map and puts every pin somewhere else.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being checked, for the camera's height.</param>
        /// <param name="why">What disagreed, for the warning, or null when everything agreed.</param>
        private bool SelfCheck(Plan plan, FloorPlan floor, out string why)
        {
            var centreTileX = Mathf.Clamp(plan.WidthPx / 2 / TileSize, 0, plan.TilesX - 1);
            var centreTileY = Mathf.Clamp(plan.HeightPx / 2 / TileSize, 0, plan.TilesY - 1);
            var px0 = centreTileX * TileSize;
            var py0 = centreTileY * TileSize;

            PositionCamera(plan, floor, px0, py0);

            var ppm = plan.Ppm;
            var span = TileSize / (double)ppm;
            var tileMinX = plan.Extent.MinX + px0 / (double)ppm;
            var tileMaxX = tileMinX + span;
            var tileMaxZ = plan.Extent.MaxZ - py0 / (double)ppm;
            var tileMinZ = tileMaxZ - span;
            var centreX = (tileMinX + tileMaxX) * 0.5d;
            var centreZ = (tileMinZ + tileMaxZ) * 0.5d;

            // Any height inside the frustum projects to the same pixel under an orthographic camera;
            // one metre below the camera is certainly inside it.
            var y = floor.CameraY - 1f;

            var spots = new[]
            {
                new Spot("the tile's -x,-z corner", tileMinX, tileMinZ),
                new Spot("the tile's +x,-z corner", tileMaxX, tileMinZ),
                new Spot("the tile's -x,+z corner", tileMinX, tileMaxZ),
                new Spot("the tile's +x,+z corner", tileMaxX, tileMaxZ),
                new Spot("the tile's centre", centreX, centreZ),
            };

            foreach (var spot in spots)
            {
                var screen = _camera.WorldToScreenPoint(new Vector3((float)spot.X, y, (float)spot.Z));

                var expectedX = (spot.X - plan.Extent.MinX) * ppm - px0;

                // The meta's row index counts DOWN from the top of the image; a screen y counts up
                // from the bottom of the tile.
                var expectedRow = (plan.Extent.MaxZ - spot.Z) * ppm - py0;
                var expectedScreenY = TileSize - expectedRow;

                if (Math.Abs(screen.x - expectedX) > SelfCheckTolerance ||
                    Math.Abs(screen.y - expectedScreenY) > SelfCheckTolerance)
                {
                    why =
                        $"{spot.What}, world ({F(spot.X)}, {F(spot.Z)}), renders at tile pixel " +
                        $"({F(screen.x)}, {F(screen.y)}) where the meta's {Ppm(ppm)} px/m puts it at " +
                        $"({F(expectedX)}, {F(expectedScreenY)}), over the {F(SelfCheckTolerance)} px allowed";
                    return false;
                }
            }

            var here = _camera.WorldToScreenPoint(new Vector3((float)centreX, y, (float)centreZ));
            var north = _camera.WorldToScreenPoint(new Vector3((float)centreX, y, (float)(centreZ + NorthProbeMetres)));

            var rowHere = TileSize - here.y + py0;
            var rowNorth = TileSize - north.y + py0;
            var expectedRise = NorthProbeMetres * ppm;

            if (rowNorth >= rowHere || Math.Abs(rowHere - rowNorth - expectedRise) > SelfCheckTolerance)
            {
                why =
                    $"{F(NorthProbeMetres)} m further +z lands on image row {F(rowNorth)} against " +
                    $"{F(rowHere)} at the centre, which is not {F(expectedRise)} px nearer the top - the " +
                    "picture's top is not world +z";
                return false;
            }

            why = null;
            return true;
        }

        /// <summary>One point the self-check projects, with the name it is blamed by.</summary>
        private struct Spot
        {
            public Spot(string what, double x, double z)
            {
                What = what;
                X = x;
                Z = z;
            }

            public readonly string What;
            public readonly double X;
            public readonly double Z;
        }

        // --- the meta --------------------------------------------------------------------------

        /// <summary>Writes the meta file, deletes the pictures of a previous capture that this one
        /// no longer has a floor for, and says what the whole capture cost. The meta goes down LAST
        /// and through a temporary file, so a reader either finds a complete set or finds the
        /// previous one.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="clock">Running since the key was pressed.</param>
        private void WriteMeta(Plan plan, Stopwatch clock)
        {
            try
            {
                var written = plan.Floors.Where(f => !f.Failed && f.Bytes > 0).ToList();

                if (written.Count == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: no floor of {plan.Key} could be captured - nothing was written. The warnings " +
                        "above say why for each.");
                    return;
                }

                var meta = new CaptureMeta
                {
                    SchemaVersion = SchemaVersion,
                    Map = plan.Key,
                    Extent = new CaptureExtent
                    {
                        MinX = plan.Extent.MinX,
                        MinZ = plan.Extent.MinZ,
                        MaxX = plan.Extent.MaxX,
                        MaxZ = plan.Extent.MaxZ,
                    },
                    Rotation = 0f,
                    PxPerMetre = plan.Ppm,
                    TileSize = TileSize,
                    CapturedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ModVersion = ModInfo.Stamp,
                    TimeOfDay = TimeOfDay(),
                    Floors = written.Select(f => new CaptureFloor
                    {
                        Level = f.Dto.Level,
                        Name = f.Dto.Name,
                        File = f.File,
                        Width = plan.WidthPx,
                        Height = plan.HeightPx,
                        MinY = f.Dto.MinY,
                        MaxY = f.Dto.MaxY,
                    }).ToList(),
                    Labels = plan.Labels,
                };

                var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
                var path = Path.Combine(plan.Dir, $"{plan.Key}.map.json");
                var temp = path + ".tmp";

                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                DropStalePictures(plan, written);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capture of {plan.Key} written - {written.Count} floor(s), {plan.Bytes} bytes, " +
                    $"{Ms(clock.Elapsed.TotalMilliseconds)} ms total.");

                Plugin.LogSource?.LogDebug($"QuestTree: {plan.Key} capture is in {plan.Dir}.");

                // The Maps tab caches what it found in the captures folder for the whole session, so
                // it has to be told a new one exists or a map captured this raid would still be
                // drawn from DynamicMaps until the game restarted. Done here, on Unity's thread,
                // which is what makes freeing the replaced pictures safe - see
                // MapCatalog.InvalidateCaptures.
                try
                {
                    UI.MapCatalog.InvalidateCaptures();
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: the Maps tab could not be told about the new capture ({ex.Message}) - it will " +
                        "find it on the next game start.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture of {plan.Key} has its pictures but no meta file " +
                    $"({ex.GetType().Name}: {ex.Message}) - it will be ignored until it is captured again.");
            }
        }

        /// <summary>Removes pictures left by an earlier capture of this map that the new meta does not
        /// name - a floor that has since merged into another, or a level that renumbered. Run only
        /// after the new meta is safely down, so a failure above never deletes a working set.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="written">The floors this capture wrote.</param>
        private static void DropStalePictures(Plan plan, List<FloorPlan> written)
        {
            try
            {
                var keep = new HashSet<string>(written.Select(f => f.File), StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.GetFiles(plan.Dir, $"{plan.Key}-*.png"))
                {
                    var name = Path.GetFileName(file);
                    if (keep.Contains(name)) continue;

                    File.Delete(file);
                    Plugin.LogSource?.LogDebug($"QuestTree: removed {name}, which this capture of {plan.Key} has no floor for.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: could not tidy {plan.Key}'s older pictures ({ex.Message}) - they are ignored " +
                    "anyway, since the meta does not name them.");
            }
        }

        /// <summary>The raid's own clock as "HH:mm", or "" when it cannot be read. Recorded, never
        /// changed: a capture is taken in whatever light the raid is in, and the meta says which so a
        /// night capture can be recognised and retaken.</summary>
        private string TimeOfDay()
        {
            try
            {
                var clock = _gameWorld?.GameDateTime;
                if (clock == null) return "";

                return clock.Calculate().ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the raid's clock could not be read ({ex.Message}).");
                return "";
            }
        }

        // --- labels ----------------------------------------------------------------------------

        /// <summary>The place names drawn on the picture: the extraction points by their localised
        /// names, and the bot zones by their own names cleaned up. Our replacement for the
        /// hand-placed labels the DynamicMaps files carried, which is the one thing of theirs that
        /// nothing else in the game provides.
        ///
        /// Both halves are guarded separately and either can come back empty; a label is a
        /// convenience and no reason for a capture to fail. Names are kept plain - a '&lt;' from a
        /// modded zone name would be read as a tag by the text mesh that draws it - and a position
        /// outside the extent is dropped, since it could only be drawn off the picture.</summary>
        /// <param name="plan">The capture's plan, for the extent labels must fall inside.</param>
        private static List<CaptureLabel> Labels(Plan plan)
        {
            var labels = new List<CaptureLabel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var exit in All<ExfiltrationPoint>())
                {
                    if (exit == null) continue;

                    var raw = exit.Settings?.Name;
                    var text = Plain(Localised(raw) ?? CleanZoneName(raw));
                    if (text == null) continue;

                    Add(labels, seen, plan, text, exit.transform.position);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the exfil labels could not be read ({ex.Message}).");
            }

            try
            {
                foreach (var zone in All<BotZone>())
                {
                    if (zone == null) continue;

                    var text = Plain(CleanZoneName(zone.NameZone));
                    if (text == null) continue;

                    // CenterOfSpawnPoints is the average of the zone's spawn markers, which is a
                    // better label position than the zone object's own transform; it is left at zero
                    // when the zone has no markers, and then the transform is all there is.
                    var at = zone.CenterOfSpawnPoints;
                    if (at == Vector3.zero) at = zone.transform.position;

                    Add(labels, seen, plan, text, at);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the bot zone labels could not be read ({ex.Message}).");
            }

            return labels;
        }

        private static void Add(
            List<CaptureLabel> labels, HashSet<string> seen, Plan plan, string text, Vector3 at)
        {
            if (!seen.Add(text)) return;

            if (float.IsNaN(at.x) || float.IsNaN(at.z) || float.IsInfinity(at.x) || float.IsInfinity(at.z)) return;
            if (at.x < plan.Extent.MinX || at.x > plan.Extent.MaxX) return;
            if (at.z < plan.Extent.MinZ || at.z > plan.Extent.MaxZ) return;

            labels.Add(new CaptureLabel { Text = text, X = at.x, Z = at.z });
        }

        /// <summary>Everything of a type the scene registered, falling back to a scene search - the
        /// same two steps MapExtentProbe uses, and for the same reason: LocationScene's arrays
        /// include objects the game keeps disabled.</summary>
        private static IEnumerable<T> All<T>() where T : Component
        {
            try
            {
                var registered = LocationScene.GetAll<T>()?.ToArray();
                if (registered != null && registered.Length > 0) return registered;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: LocationScene.GetAll<{typeof(T).Name}> failed ({ex.Message}) - falling back to a scene search.");
            }

            return FindObjectsOfType<T>();
        }

        /// <summary>The game's own translation of a localisation key, or null when there is none or
        /// when the key comes back unchanged (which is what the manager does with a key it has no
        /// entry for, and is not a name anybody wants on a map).</summary>
        /// <param name="key">The key from the scene, e.g. an exfil's Settings.Name.</param>
        private static string Localised(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            try
            {
                var text = key.Localized();
                if (string.IsNullOrEmpty(text)) return null;
                return string.Equals(text.Trim(), key.Trim(), StringComparison.Ordinal) ? null : text.Trim();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: \"{key}\" could not be localised ({ex.Message}).");
                return null;
            }
        }

        /// <summary>A scene object's name as a place name: the Zone prefix dropped, underscores and
        /// dashes turned into spaces, and runs of capitals split into words, so "ZoneGasStation"
        /// reads "Gas Station". Null when nothing is left.</summary>
        /// <param name="raw">The name as the scene spells it.</param>
        private static string CleanZoneName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            var name = raw.Trim();

            foreach (var prefix in ZoneNamePrefixes)
            {
                if (name.Length <= prefix.Length) continue;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                name = name.Substring(prefix.Length);
                break;
            }

            name = name.Trim('_', '-', ' ');
            if (name.Length == 0) return null;

            var text = new StringBuilder(name.Length + 8);

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];

                if (c == '_' || c == '-')
                {
                    if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                    continue;
                }

                // A capital straight after a lower-case letter or a digit starts a new word.
                if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                {
                    if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                }

                text.Append(c);
            }

            var cleaned = text.ToString().Trim();
            return cleaned.Length == 0 ? null : cleaned;
        }

        /// <summary>The text with the two characters a text mesh would read as a tag taken out, or
        /// null when nothing is left. The meta holds data, so it holds no markup and no escape
        /// either - whoever draws it should not have to undo one.</summary>
        /// <param name="text">The label text.</param>
        private static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var plain = text.Replace("<", "").Replace(">", "").Trim();
            return plain.Length == 0 ? null : plain;
        }

        // --- names, paths and numbers ----------------------------------------------------------

        /// <summary>The map's internal name, spelled exactly as the zone harvest spells it, because
        /// the server files, the marker payloads and this folder all have to agree on one key.</summary>
        private string MapKey()
        {
            try
            {
                var map = _gameWorld?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(map)) map = _gameWorld?.LocationId;
                return map;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a map name can be a folder and a file name. The same rule as the server's
        /// ZoneStore.IsValidMapName - letters, digits, dash and underscore, 1 to 64, and none of
        /// Windows' device names - because these files are uploaded to a host that applies it, and a
        /// capture nobody could ever transport is not worth taking.</summary>
        /// <param name="key">The map's internal name.</param>
        private static bool IsUsableKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64) return false;

            foreach (var c in key)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
                if (c > 127) return false;
            }

            return !ReservedNames.Contains(key);
        }

        /// <summary>Names the rule above admits that Windows still treats as devices.</summary>
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>BepInEx/plugins/QuestTree/captures/&lt;key&gt;/, created on demand. Null when the
        /// plugin has no file location - the case KappaQuests and the experiment both guard.</summary>
        /// <param name="key">The map's internal name.</param>
        private static string CaptureDir(string key)
        {
            try
            {
                var modPath = Path.GetDirectoryName(typeof(MapCapture).Assembly.Location);
                if (string.IsNullOrEmpty(modPath))
                {
                    Plugin.LogSource?.LogWarning(
                        "QuestTree: the plugin has no file location, so a map capture cannot be written.");
                    return null;
                }

                var dir = Path.Combine(Path.Combine(modPath, "captures"), key);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the captures folder for {key} could not be made ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        /// <summary>The long side the picture is allowed, from the setting, held to something a
        /// texture and a release zip can carry whatever a hand-edited config file says.</summary>
        private static int Resolution()
        {
            var value = ModSettings.Ready && ModSettings.CaptureResolution != null
                ? ModSettings.CaptureResolution.Value
                : 4096;

            return Mathf.Clamp(value, 512, 8192);
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static string F(double v) =>
            IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

        private static string Ms(double v) => v.ToString("0", CultureInfo.InvariantCulture);

        private static string Ppm(float ppm) => ppm.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Metres to the pixel, which is how a map's detail is usually spoken about, from
        /// pixels to the metre, which is how it is calculated.</summary>
        /// <param name="ppm">Pixels per metre.</param>
        private static string MetresPerPixel(float ppm) =>
            ppm > 0f ? (1f / ppm).ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

        // --- plan ------------------------------------------------------------------------------

        /// <summary>Everything one capture works from, computed once in <see cref="Prepare"/>.</summary>
        private sealed class Plan
        {
            public string Key;
            public string Dir;
            public MapExtentDto Extent;
            public int Cap;
            public float Ppm;
            public int WidthPx;
            public int HeightPx;
            public int TilesX;
            public int TilesY;
            public long Bytes;

            public int TileCount => TilesX * TilesY;

            public readonly List<FloorPlan> Floors = new List<FloorPlan>();
            public List<CaptureLabel> Labels = new List<CaptureLabel>();
        }

        /// <summary>One floor's state while it is being captured.</summary>
        private sealed class FloorPlan
        {
            public MapFloorDto Dto;
            public string File;
            public Texture2D Texture;
            public Stopwatch Clock;
            public float CameraY;
            public int Tiles;
            public long Bytes;
            public bool Failed;
        }

        // --- the meta file ---------------------------------------------------------------------

        /// <summary>The &lt;key&gt;.map.json beside the pictures: what they are of, where they sit in
        /// the world, and what drew them. Read by the Maps tab (UI/MapCatalog) and by
        /// tools/check-capture.py, so the names below ARE the interface - a rename is a schema
        /// bump.</summary>
        private sealed class CaptureMeta
        {
            [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
            [JsonProperty("map")] public string Map { get; set; }

            /// <summary>The padded rectangle the probe measured, to the same doubles the harvest sent
            /// the server. The pictures are exactly this rectangle, so anything the server places
            /// inside it lands on them.</summary>
            [JsonProperty("extent")] public CaptureExtent Extent { get; set; }

            /// <summary>Degrees the pictures are turned from world XZ. Always 0: the camera looks
            /// straight down the world axes. Present so a hand-corrected capture needs no schema
            /// bump.</summary>
            [JsonProperty("rotation")] public float Rotation { get; set; }

            [JsonProperty("pxPerMetre")] public float PxPerMetre { get; set; }

            /// <summary>The render tile the pictures were assembled from. Diagnostic: a seam or a
            /// black band in a picture is read against this.</summary>
            [JsonProperty("tileSize")] public int TileSize { get; set; }

            [JsonProperty("capturedAt")] public string CapturedAt { get; set; }
            [JsonProperty("modVersion")] public string ModVersion { get; set; }

            /// <summary>The raid's own clock, "HH:mm", or "" when it could not be read. A map
            /// captured at 03:00 is a dark map and worth taking again.</summary>
            [JsonProperty("timeOfDay")] public string TimeOfDay { get; set; }

            [JsonProperty("floors")] public List<CaptureFloor> Floors { get; set; } = new List<CaptureFloor>();
            [JsonProperty("labels")] public List<CaptureLabel> Labels { get; set; } = new List<CaptureLabel>();
        }

        private sealed class CaptureExtent
        {
            [JsonProperty("minX")] public double MinX { get; set; }
            [JsonProperty("minZ")] public double MinZ { get; set; }
            [JsonProperty("maxX")] public double MaxX { get; set; }
            [JsonProperty("maxZ")] public double MaxZ { get; set; }
        }

        private sealed class CaptureFloor
        {
            /// <summary>0 is the ground floor, positive up, negative down - the same numbering the
            /// zone file's floors carry, and part of the file name.</summary>
            [JsonProperty("level")] public int Level { get; set; }

            [JsonProperty("name")] public string Name { get; set; }

            /// <summary>The picture's file name, beside this meta.</summary>
            [JsonProperty("file")] public string File { get; set; }

            [JsonProperty("width")] public int Width { get; set; }
            [JsonProperty("height")] public int Height { get; set; }

            /// <summary>The height band this floor was rendered for, as the probe measured it.</summary>
            [JsonProperty("minY")] public float MinY { get; set; }
            [JsonProperty("maxY")] public float MaxY { get; set; }
        }

        private sealed class CaptureLabel
        {
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("x")] public float X { get; set; }
            [JsonProperty("z")] public float Z { get; set; }
        }
    }
}
