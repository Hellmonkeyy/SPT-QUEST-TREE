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
    /// HOW the picture is taken was measured, not designed. Phase 0 rendered the same view of Customs
    /// five ways at 08:07 game time and compared the pictures:
    ///   - a bare new Camera, a CopyFrom of the first-person camera, a clone of its whole GameObject
    ///     and a cut-down culling mask all drew the SAME picture - the terrain, the roads, every
    ///     building and the river - and all four drew it very dark. Identical output means the
    ///     components on the game's camera do not matter and RenderSettings.ambientLight does nothing
    ///     here; the darkness is the deferred pipeline's raw linear output landing in an 8-bit target
    ///     with none of the exposure and tonemapping the player's own view gets.
    ///   - a replacement unlit shader drew flat-coloured objects and NO terrain at all. Rejected.
    /// So: copy the live camera's settings (DeferredShading, HDR), render into a HALF-FLOAT target so
    /// the values survive, and do the exposure ourselves - a percentile stretch per floor, which needs
    /// no scene knowledge and cannot blow out a night map.
    ///
    /// A daytime raid IN RAIN then showed that was not enough: the capture came back black with a few
    /// specks, exposure 0.0000..0.5051 - every drawn pixel at exactly zero and a 98th percentile read
    /// off a handful of emissive objects - where a sunny noon capture of the same map on the same code
    /// was correct. Our camera receives the scene's DIRECT sun and nothing else; EFT's ambient and sky
    /// light are produced by components on the first-person camera that CopyFrom does not bring. So the
    /// capture brings a light of its own (<see cref="CaptureLightIntensity"/>), enabled only while a
    /// tile renders, and a floor whose brightest 2 % is still under <see cref="MinUsableHigh"/> is
    /// refused rather than developed into noise. <see cref="BuildCamera"/>,
    /// <see cref="RenderTile"/>, <see cref="Measure"/> and <see cref="Develop"/> are the whole of it.
    ///
    /// WHERE the camera goes is two rules, not one, and both were learnt from a picture that was
    /// wrong (<see cref="BeginFloor"/>). The topmost band of a map is photographed from 300 m above
    /// the world, because a camera three metres over the ground has every roof and upper wall BEHIND
    /// it and renders a town as a set of floor slabs. A band with another band above it keeps the
    /// ceiling clip - the camera sits just under the floor above - since that slab is exactly what
    /// would otherwise hide the room.
    ///
    /// A capture ADDS to the one already on disk rather than replacing it. The game streams distant
    /// chunks out, so any one capture of a large map has regions the camera found empty - the west
    /// third of Customs, from a player at the east end - and a second capture from somewhere else has
    /// them. The merge is BEST OF, by distance: a sidecar beside each picture records how far the
    /// capturing player was from every pixel, and a pixel is only replaced by one seen from closer, so
    /// repeated captures converge on the sharpest view of every spot instead of overwriting it with
    /// the blurriest. <see cref="LoadPrevious"/> says when a merge is refused and why.
    ///
    /// Known limitation, not a bug: terrain and mesh LOD follow the PLAYER, not our camera, so the
    /// far half of a large map is drawn at its lowest detail and looks soft next to the ground the
    /// player is standing on. Capturing the same map from two or three places is what fixes it, which
    /// is the other reason the merge prefers the nearest view of each pixel.
    ///
    /// Cost. Phase 0 timed a 2048 tile in a live raid at 10-61 ms to read back and 58-68 ms to
    /// encode, so the work is spread one step to a frame: each tile's render and readback, then, per
    /// floor, its exposure measurement, its development into eight bits - itself a step for the
    /// previous picture, one for that picture's sidecar and one per 256 rows, because the whole of
    /// it is half a second - its encode and write, and its sidecar. None of it can move off the main
    /// thread - ReadPixels, GetPixels, SetPixels32 and EncodeToPNG are all main-thread Texture2D
    /// calls - so a capture is a handful of short hitches on a key the player pressed, rather than
    /// one long freeze.
    ///
    /// Memory, at the worst moment of a 2360x2040 floor: 58 MB of float buffer (three floats a pixel,
    /// freed as soon as the floor is developed), a 33 MB half-float staging texture shared by every
    /// tile, 14 MB of eight-bit picture, 4.8 MB each for the drawn mask and the distance sidecar, and
    /// on a merge 19 MB of the previous picture plus 4.8 MB of its sidecar, both dropped as soon as
    /// the merge is done. One floor at a time, by construction.
    ///
    /// Everything here is guarded and reversible. It runs on a player's raid frame: fog is restored by
    /// the same statement that changed it, the camera is destroyed in a finally and again in
    /// OnDestroy, and no failure is allowed to reach the game.
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

        /// <summary>Metres above the TOPMOST band the camera sits. Three hundred - the height Phase 0
        /// rendered its pictures from, and high enough to be above anything any map builds.
        ///
        /// It is this high because of what the first real capture of Customs looked like when it was
        /// not: with the camera three metres over the walkable surface, every warehouse, Dorms and Big
        /// Red rendered as a flat slab, because their roofs and upper walls were BEHIND the camera and
        /// clipped away. A map of a town has to show the buildings, so the outermost band is
        /// photographed from above the whole world, roofs, chimneys, shadows and all.</summary>
        private const float TopBandCameraHeight = 300f;

        /// <summary>How far UNDER the next band up an interior band's camera sits, so it sees the room
        /// and not the floor slab above it. Half a metre below the upper band's own lower edge: the
        /// slab is what that edge is measured from, and clipping it is the point - this is the one
        /// place a ceiling should be cut away.</summary>
        private const float CeilingClearance = 0.5f;

        /// <summary>The least an interior band's camera may sit above its own surface, for the case
        /// where two bands end up close enough together that the clearance above would put the camera
        /// under the floor it is photographing.</summary>
        private const float MinCameraAboveBand = 0.5f;

        /// <summary>How far BELOW the topmost band's own lower edge that band's far plane still
        /// reaches.
        ///
        /// A band's minY is the lowest point the NAVMESH has in it, and a map's ground goes well
        /// under that: river beds, ditches, the tunnel mouths, the slopes outside the walkable area
        /// and the terrain skirt that carries the extent's padding. A far plane that stopped at the
        /// band's own floor would clip all of it, and clipped geometry draws NOTHING - so those
        /// metres would come back as the clear colour, be recorded as holes, and be reported to the
        /// player as ground a second capture could fill. It could not: every capture clips the same
        /// terrain. Fifty metres is deeper than any such dip and still leaves the top band's far
        /// plane around 350 m, where an orthographic depth buffer has precision to spare.
        ///
        /// The TOP band only. An interior band's far plane has to stop at its own floor, because
        /// what lies below it is the storey below, and drawing that through the floor is exactly
        /// what the band split exists to prevent.</summary>
        private const float TopBandDepthBelow = 50f;

        private const float NearClip = 0.05f;

        /// <summary>Intensity of the capture's OWN directional light. Tunable, and the one number to
        /// change if pictures come out too dark or washed out; changing it changes
        /// <see cref="LightingTag"/>, which makes every capture taken under the old value be replaced
        /// rather than merged into - see <see cref="LoadPrevious"/>.
        ///
        /// It exists because of a daytime Customs raid IN RAIN: the capture came back black with a few
        /// specks, its exposure reading 0.0000..0.5051, which is every drawn pixel at exactly zero and
        /// a 98th percentile taken from a handful of emissive objects. A sunny noon capture of the same
        /// map with the same code was bright and correct. So our camera receives the scene's DIRECT
        /// sunlight and nothing else: EFT's ambient and sky lighting come out of its own pipeline, on
        /// components attached to the first-person camera (SSAA, Prism) that a CopyFrom deliberately
        /// does not bring. Overcast weather removes the sun, and with it everything we had.
        ///
        /// A light of our own is the fix that needs no knowledge of that pipeline: deferred shading
        /// SUMS lights, so the scene's sun still draws its shadows when there is one, and this
        /// guarantees a floor of illumination when there is not.</summary>
        private const float CaptureLightIntensity = 1.5f;

        /// <summary>What the meta records about how a capture was lit, and what a later capture has to
        /// match before it may be merged into it. "own-1.5" is the current lighting; a capture from
        /// before this light existed records nothing and is therefore replaced, which is what has to
        /// happen to the black rain-era pictures.</summary>
        private static string LightingTag =>
            "own-" + CaptureLightIntensity.ToString("0.###", CultureInfo.InvariantCulture);

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

        /// <summary>Share of the darkest pixels the exposure stretch throws away, and by symmetry the
        /// brightest. Two percent: enough that one specular highlight or one black hole under a roof
        /// cannot decide the whole picture's brightness, little enough that a map with a genuinely
        /// dark quarter keeps it dark.</summary>
        private const float ExposureLowPercentile = 0.02f;

        private const float ExposureHighPercentile = 0.98f;

        /// <summary>Every Nth pixel goes into the percentile sample. Eight: a 2360x2040 floor is 4.8
        /// million pixels and 600k of them describe its brightness distribution to far better than
        /// the eighth of a stop this is deciding.</summary>
        private const int ExposureSampleStride = 8;

        /// <summary>How far this capture's own 98th percentile may sit from the one the pictures on
        /// disk were developed with, as a share of it, before the capture is REFUSED rather than
        /// merged into them.
        ///
        /// Merging is only honest when identical light became identical bytes in both captures, which
        /// is why a merge develops with the stored exposure rather than its own (see
        /// <see cref="MeasureFloor"/>). That holds the seam together as long as the light is the same.
        /// It is not the same at 03:00 as at noon: the stored exposure would clamp a daylight render
        /// to flat white wherever it filled a hole, and the picture would grow patches instead of
        /// detail. So the light is measured on every merge and compared, and a third out is refused.
        ///
        /// 35 % - about half a stop - is a SETTING, not a measurement, and it is the one number here
        /// that raids will move. What has to stay inside it: two captures of different PARTS of one
        /// map at the same hour, since each capture's percentiles describe only the pixels it drew,
        /// and the east third of a map is not as bright as the west. What has to fall outside it: the
        /// same map at a different hour, which is a factor of several. The refusal line prints both
        /// numbers and the accepted case prints the drift as a percentage in the debug log, so the
        /// threshold can be moved from evidence rather than from this paragraph.</summary>
        private const float MaxExposureDrift = 0.35f;

        /// <summary>The dimmest 98th percentile a FRESH capture may have and still be a map. Below
        /// this, the picture is the rain capture: a black field with a few emissive specks in it, whose
        /// percentile stretch would multiply near-nothing by two hundred and write noise. 0.02 of
        /// linear white is far under anything daylight produces and far over what an unlit scene
        /// does.</summary>
        private const float MinUsableHigh = 0.02f;

        /// <summary>The narrowest luminance range the stretch will believe. Under it the floor is one
        /// flat tone - a picture of nothing, or a bug - and stretching it would amplify noise into a
        /// pattern that looks like a map.</summary>
        private const float MinExposureRange = 1e-5f;

        /// <summary>Rows of pixels moved between the staging texture and the floor buffer at a time.
        /// Whole tiles in one call would allocate a 67 MB Color[] per tile; 256 rows is 8 MB and the
        /// same total work.</summary>
        private const int PixelBandRows = 256;

        /// <summary>How far the grade pulls every pixel toward its own luminance, 0 none and 1 grey.
        /// A third: the raw render is a photograph, with saturated grass, orange rust and blue shade
        /// fighting the pins drawn on top of it. A muted picture reads as a map and lets a coloured
        /// pin be the brightest thing on screen.</summary>
        private const float GradeDesaturation = 0.35f;

        /// <summary>How much of a smoothstep S-curve is blended into the luminance, which deepens the
        /// shadows and lifts the midtones the way a printed map is drawn. 40 %: enough to give the
        /// picture shape, little enough to leave a dark interior legible.</summary>
        private const float GradeSCurveBlend = 0.4f;

        /// <summary>What the exposure's high percentile is scaled down to. A map with white
        /// highlights has nothing left to draw a white pin or a white label on, so the brightest the
        /// picture itself goes is 82 %.</summary>
        private const float GradeHighlightCeiling = 0.82f;

        /// <summary>Metres per step of the distance sidecar's eight-bit values. Four metres a step
        /// covers a kilometre in 250 steps, which is the whole of the largest map, and four metres is
        /// far finer than the difference in sharpness it is there to compare.</summary>
        private const float DistanceStepMetres = 4f;

        /// <summary>The value the distance sidecar stores for a pixel nothing has drawn yet. 255 is
        /// reserved for it, so a real pixel saturates at 254 (1016 m) and can never be mistaken for
        /// an empty one.</summary>
        private const byte DistanceEmpty = 255;

        private const byte DistanceMax = 254;

        /// <summary>Layers kept out of the picture, by name. Everything the player carries or is
        /// (Player, PlayerRenderers, Weapon Preview, Weapons, PlayerSpiritAura, PlayerCollisionTest),
        /// everything drawn for a screen rather than a world (UI, Menu Environment, RainDrops, Shells,
        /// Sky - which is above a camera that looks down anyway), the invisible volumes (Triggers,
        /// CullingMask, DisablerCullingObject, the collider layers), corpses - and Water, which the
        /// first real capture of Customs showed renders as flat cyan placeholder blocks where the
        /// pools by Dorms are, because the water shader has nothing to reflect from a camera that is
        /// not the player's. The ground under it draws instead, which reads as a map should.
        ///
        /// What is deliberately KEPT: Default, Terrain, Foliage, Grass, Interactive, Loot,
        /// LevelBorder, TransparentFX. These are the map.
        ///
        /// The list is longer than the plan's four names because Phase 0 logged all 32 layer names of
        /// this game version (D1) and they could then be named exactly. Names this version does not
        /// carry are skipped, and the finished mask is logged once, so what was actually excluded is
        /// on the record rather than assumed.</summary>
        private static readonly string[] ExcludedLayerNames =
        {
            "Player", "PlayerRenderers", "PlayerCollisionTest", "PlayerSpiritAura",
            "Weapons", "Weapon Preview", "Shells", "Deadbody",
            "UI", "Menu Environment", "RainDrops", "Sky", "Water",
            "Triggers", "CullingMask", "DisablerCullingObject",
            "DoorLowPolyCollider", "HighPolyCollider", "LowPolyCollider", "HitCollider",
            "TransparentCollider"
        };

        /// <summary>The two values a label's "kind" takes. Constants rather than literals because the
        /// Maps tab filters on them and a typo would simply hide a label.</summary>
        private const string LabelKindExfil = "exfil";

        private const string LabelKindZone = "zone";

        /// <summary>Leading words stripped from a BotZone's name before it becomes a label.</summary>
        private static readonly string[] ZoneNamePrefixes = { "BotZone", "Zone" };

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

        /// <summary>The capture's own light - see <see cref="CaptureLightIntensity"/>. Enabled only
        /// for the instant each tile renders, so the player's own view is never lit by it.</summary>
        private Light _light;

        private RenderTexture _rt;

        /// <summary>The texture each tile is read back into, one tile wide and tall, reused for every
        /// tile of every floor. Half-float when the hardware will render one, so the deferred
        /// pipeline's values arrive intact instead of clipped into eight bits.</summary>
        private Texture2D _stage;

        /// <summary>Whether the render target and staging texture are half-float. False means the
        /// hardware refused the format and the whole capture is running on eight-bit data - which
        /// still works, because the exposure stretch is the same arithmetic either way, just with
        /// fewer values to stretch (visible as banding in a dark floor).</summary>
        private bool _hdr;

        /// <summary>Whether the values read back are LINEAR and so need encoding for a display.
        /// Half-float targets in a linear-space project hold linear light; an eight-bit target in the
        /// same project is written through the GPU's sRGB encoder and is already display-encoded, and
        /// a gamma-space project encodes nothing anywhere. Getting this wrong is not subtle - a
        /// linear picture shown as if encoded looks black, which is exactly the bug this whole pass
        /// exists to fix.</summary>
        private bool _needsGamma;

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

                    // The rest of the floor, never two of these in one frame: its brightness, its
                    // development into eight bits - itself a dozen steps, see Develop - its encode
                    // and write, and its distance sidecar. Each is on the order of a hundred
                    // milliseconds of main-thread work on a large floor, and Phase 0 measured the
                    // encode alone at 58-68 ms for a single tile.
                    if (!floor.Failed)
                    {
                        yield return null;
                        MeasureFloor(plan, floor);
                    }

                    // The light test in MeasureFloor can refuse the whole capture, and then nothing
                    // more is done at all: every picture this run has encoded is still only staged
                    // beside the one it would have replaced (see Stage), the meta is never written,
                    // and Cleanup drops the staged files - so the set on disk is exactly what it was
                    // before the key was pressed. MeasureFloor has already said so in one line.
                    if (plan.Refused) break;

                    if (!floor.Failed)
                    {
                        // Driven here rather than started as a coroutine of its own, so the floor
                        // loop cannot run ahead of a development that is still going.
                        var develop = Develop(plan, floor);
                        while (develop.MoveNext()) yield return develop.Current;
                    }

                    if (!floor.Failed)
                    {
                        yield return null;
                        FinishFloor(plan, floor);
                    }

                    // The sidecar AFTER the picture, and only when the picture was written - see
                    // WriteSidecar, where the order is the whole of what makes a crash between the
                    // two files survivable.
                    if (!floor.Failed)
                    {
                        yield return null;
                        WriteSidecar(plan, floor);
                    }

                    ReleaseTexture(floor);
                    yield return null;
                }

                // Nothing is in place until this runs: it commits every staged picture and then
                // writes the meta. A refused capture skips it, which is the whole of what makes the
                // refusal cost nothing.
                if (!plan.Refused) WriteMeta(plan, clock);
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

                    var level = floor.Level.ToString(CultureInfo.InvariantCulture);
                    plan.Floors.Add(new FloorPlan
                    {
                        Dto = floor,
                        File = $"{key}-{level}.png",
                        DistFile = $"{key}-{level}.dist.png"
                    });
                }

                if (plan.Floors.Count == 0)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - it has no usable floor.");
                    plan = null;
                    return false;
                }

                // Lowest band first, whatever order the extent listed them in, so each one knows what
                // is above it: that is what decides its camera height, and the topmost band - the one
                // with nothing above it - is the only one photographed from over the roofs.
                plan.Floors.Sort((a, b) => a.Dto.MinY.CompareTo(b.Dto.MinY));

                for (var i = 0; i < plan.Floors.Count - 1; i++)
                {
                    plan.Floors[i].NextMinY = plan.Floors[i + 1].Dto.MinY;
                }

                plan.Labels = Labels(plan);
                plan.From = CapturePoint();

                if (!BuildCamera(plan, out var note))
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - {note}.");
                    plan = null;
                    return false;
                }

                // After the camera, because whether the previous capture can be merged into this one
                // depends on the encoding the camera decided (see LoadPrevious).
                plan.Previous = LoadPrevious(plan, _needsGamma);
                plan.Captures = plan.Previous == null ? 1 : Math.Max(1, plan.Previous.Captures) + 1;
                plan.FirstCapturedAt = plan.Previous == null
                    ? null
                    : FirstOf(plan.Previous);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capturing {key} - {plan.WidthPx}x{plan.HeightPx} px, " +
                    $"{MetresPerPixel(plan.Ppm)} m/px, {plan.TilesX}x{plan.TilesY} tiles of {TileSize}, " +
                    $"{plan.Floors.Count} floor(s), {plan.Labels.Count} label(s), {note}" +
                    (plan.Previous == null
                        ? "."
                        : $", merging into capture {plan.Captures} of this map from " +
                          $"{F(plan.From.x)},{F(plan.From.y)}."));

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

                // Two rules, because the two cases want opposite things.
                //
                // The TOPMOST band - which is every band of a single-band map, Customs included - is
                // the outside of the world, and its buildings are the map. Its camera goes 300 m up so
                // roofs, upper walls and their shadows are all in front of it. Nothing is above it to
                // clip.
                //
                // An INTERIOR band has another floor over it, and that floor's slab would hide
                // everything this band is for. Its camera goes just under the band above - half a
                // metre below the upper band's lower edge - so the slab is behind the near plane and
                // cut away, and the room's own walls and contents are not.
                var top = !IsFinite(floor.NextMinY);

                if (top)
                {
                    floor.CameraY = maxY + TopBandCameraHeight;
                }
                else
                {
                    var ceiling = floor.NextMinY - CeilingClearance;

                    // Two bands can end up close enough together that the ceiling clearance would put
                    // the camera below the surface it is photographing.
                    if (ceiling < maxY + MinCameraAboveBand) ceiling = maxY + MinCameraAboveBand;

                    floor.CameraY = ceiling;
                }

                // Down to a metre below the band's floor - and, for the top band, fifty metres
                // further, so the terrain that lies under the lowest walkable point is drawn instead
                // of clipped into holes no later capture could fill. See TopBandDepthBelow.
                var far = floor.CameraY - minY + FarClipSlack + (top ? TopBandDepthBelow : 0f);
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

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" band {F(minY)}..{F(maxY)} rendered from " +
                    $"y={F(floor.CameraY)} ({(top ? "top band, above the world" : $"interior band, under the floor at {F(floor.NextMinY)}")}), " +
                    $"near {F(NearClip)}, far {F(far)}.");

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

                // Three floats per pixel, not a texture: the tiles arrive as linear light and the
                // exposure that turns them into eight bits cannot be decided until the whole floor is
                // in. 2360x2040 is 58 MB of it, freed the moment the floor is written.
                floor.Pixels = new float[plan.WidthPx * plan.HeightPx * 3];

                // Which of those pixels the camera actually drew, and how far each was from the
                // player - the two things the merge decides on.
                floor.Drawn = new bool[plan.WidthPx * plan.HeightPx];
                floor.Dist = new byte[plan.WidthPx * plan.HeightPx];
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

        /// <summary>Renders one tile and copies it into the floor's pixel buffer at that tile's
        /// offset. The last row and column of tiles are rendered whole and read back clipped, so every
        /// tile is the same view size and the arithmetic the self-check proved holds for all of them.
        ///
        /// Two steps rather than one, unlike the eight-bit version this replaces: the render target is
        /// half-float and cannot be read straight into the RGB24 texture a PNG is made from, so the
        /// tile lands in the staging texture and its values are copied out as floats. The copy runs in
        /// bands of <see cref="PixelBandRows"/> rows because GetPixels allocates the array it returns -
        /// a whole 2048 tile would be a 67 MB allocation per tile, against 8 MB a band.</summary>
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

                // Unity counts the RenderTexture's rows from the BOTTOM, so the tile's own TOP rows -
                // the ones inside the extent when the tile is clipped - are the last th rows of the
                // render. They are read to the staging texture's origin, which is why the band loop
                // below starts at 0 rather than TileSize-th. A clipped tile therefore drops its bottom
                // and its right, which is exactly the part that lies outside the extent.
                // No Apply: it would upload the 33 MB staging texture to the GPU, and the only reader
                // is GetPixels below, which reads the CPU-side copy ReadPixels just filled.
                _stage.ReadPixels(new Rect(0f, TileSize - th, tw, th), 0, 0);

                // The floor buffer is kept in TEXTURE order - row 0 at the bottom, world -z - because
                // that is the order SetPixels32 and EncodeToPNG want, and it makes the destination row
                // of a tile the same expression the one-step version used: HeightPx - py0 - th.
                var baseRow = plan.HeightPx - py0 - th;

                for (var bandBottom = 0; bandBottom < th; bandBottom += PixelBandRows)
                {
                    var rows = Math.Min(PixelBandRows, th - bandBottom);
                    var band = _stage.GetPixels(0, bandBottom, tw, rows);

                    for (var row = 0; row < rows; row++)
                    {
                        var pixelRow = (baseRow + bandBottom + row) * plan.WidthPx + px0;
                        var target = pixelRow * 3;
                        var source = row * tw;

                        for (var col = 0; col < tw; col++)
                        {
                            var pixel = band[source + col];
                            var at = target + col * 3;

                            floor.Pixels[at] = pixel.r;
                            floor.Pixels[at + 1] = pixel.g;
                            floor.Pixels[at + 2] = pixel.b;

                            // The camera clears to (0,0,0,0) and draws nothing over a chunk the game
                            // has streamed out, so a pixel with anything at all in any channel -
                            // alpha included, which opaque geometry writes as 1 - was drawn, and one
                            // that is four exact zeroes was not. Both signals together rather than
                            // either alone: a rendered pixel in true black shadow has alpha, and a
                            // shader that writes no alpha still has colour.
                            floor.Drawn[pixelRow + col] =
                                pixel.r > 0f || pixel.g > 0f || pixel.b > 0f || pixel.a > 0f;
                        }
                    }
                }

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

        /// <summary>Encodes a developed floor, writes it and its distance sidecar, and says what the
        /// merge did. The last of the floor's three post-tile frames - measure, develop, encode - which
        /// are separate frames because each is a hundred milliseconds or so of main-thread work on a
        /// large floor and three short hitches are kinder than one long one.
        ///
        /// The sidecar goes down AFTER the picture, so a crash between the two leaves a picture with a
        /// sidecar that is one capture out of date rather than a sidecar describing pixels that are not
        /// there: the first is a slightly worse merge next time, the second would keep the better
        /// pixels out.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose picture is developed.</param>
        private void FinishFloor(Plan plan, FloorPlan floor)
        {
            try
            {
                if (floor.Texture == null || floor.Exposure == null)
                {
                    floor.Failed = true;
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" has no developed picture to write.");
                    return;
                }

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

                // STAGED, not written: see Stage. It goes in place in WriteMeta, with every other
                // floor of this capture, once the last of them has passed the light test.
                Stage(Path.Combine(plan.Dir, floor.File), png);

                floor.Bytes = png.Length;
                plan.Bytes += png.Length;

                var ms = floor.Clock?.Elapsed.TotalMilliseconds ?? 0d;
                var pixels = plan.WidthPx * plan.HeightPx;

                var line =
                    $"QuestTree: captured {plan.Key} \"{floor.Dto.Name}\" {plan.WidthPx}x{plan.HeightPx} px " +
                    $"({MetresPerPixel(plan.Ppm)} m/px), {floor.Tiles} tiles, {Ms(ms)} ms, exposure " +
                    $"{E(floor.Exposure.Low)}..{E(floor.Exposure.High)} " +
                    $"({(_hdr ? "half-float" : "8-bit")}, gamma {G(floor.Exposure.Gamma)}" +
                    (floor.ReusedExposure ? ", kept from the first capture" : "") + ")";

                if (floor.Merged)
                {
                    line +=
                        $", merged with the previous capture: {Share(floor.Filled, pixels)} % newly drawn, " +
                        $"{Share(floor.Kept, pixels)} % kept, {Share(floor.StillEmpty, pixels)} % still empty.";
                }
                else if (floor.StillEmpty > 0)
                {
                    line +=
                        $", {Share(floor.StillEmpty, pixels)} % of it not drawn - press the key again from " +
                        "another part of the map and this capture fills in what that one could not see.";
                }
                else
                {
                    line += ".";
                }

                Plugin.LogSource?.LogInfo(line);

                // A merge that drew nothing new is not an error - the player pressed the key twice in
                // the same spot, or somewhere with nothing left to add - but it is worth saying, since
                // the picture on disk is exactly what it was.
                if (floor.Merged && floor.Filled == 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: nothing in {plan.Key} \"{floor.Dto.Name}\" was improved by this capture - every " +
                        "pixel it drew was already there from closer. Try a spot further from where the last " +
                        "captures were taken.");
                }
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Writes this floor's distance sidecar beside its picture. A failure here is one
        /// debug line and nothing more: the sidecar only decides which of two captures of a pixel is
        /// the better one, and without it the next capture simply takes its own everywhere.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose sidecar is to be written.</param>
        private static void WriteSidecar(Plan plan, FloorPlan floor)
        {
            var path = Path.Combine(plan.Dir, floor.DistFile);

            try
            {
                if (floor.Dist == null) return;

                var png = EncodeSidecar(plan, floor);

                if (png == null || png.Length == 0)
                {
                    // Left alone rather than deleted. The one already there - if there is one - was
                    // written by the previous capture of this floor, so it is never NEWER than the
                    // picture: every pixel this capture filled reads as empty in it and is simply
                    // taken again next time, and every pixel it kept still carries the distance it
                    // was seen from. A coarser merge, and nothing worse.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto.Name}\" could not be encoded, so " +
                        "the next capture of this map compares against the one the last capture left.");
                    return;
                }

                Stage(path, png);

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {floor.DistFile} written, {png.Length} bytes.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>
        /// The floor's distances as PNG bytes: the eight-bit steps in three identical channels, so
        /// the file is grey and its red channel is the value - which is how <see cref="LoadPreviousDist"/>
        /// reads it back.
        ///
        /// RGB24 rather than the single-channel R8 this obviously wants, because R8 is not among the
        /// formats Texture2D.SetPixels32 documents support for, and a SetPixels32 that Unity IGNORES
        /// writes no warning and throws nothing: the sidecar would encode as a field of zeroes, every
        /// pixel would read back as "seen from 0 m", and every later merge would keep the old picture
        /// everywhere and report itself as having improved nothing. That is a failure in the
        /// direction that looks like success, on a file nobody opens. Three bytes a pixel and a PNG a
        /// megabyte larger - on a file that never leaves this machine, since only the capture's own
        /// pictures are uploaded or packaged - buys a format both halves of the API certainly take.
        ///
        /// Built here rather than kept as a texture through the development, so the cost lands on the
        /// frame that writes the file and nothing holds a 14 MB texture across the bands.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose distances are to be encoded.</param>
        private static byte[] EncodeSidecar(Plan plan, FloorPlan floor)
        {
            Texture2D grey = null;

            try
            {
                grey = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGB24, mipChain: false);

                var pixels = new Color32[plan.WidthPx * plan.HeightPx];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var step = floor.Dist[i];
                    pixels[i] = new Color32(step, step, step, 255);
                }

                grey.SetPixels32(pixels);
                grey.Apply(updateMipmaps: false);

                return grey.EncodeToPNG();
            }
            finally
            {
                if (grey != null) Destroy(grey);
            }
        }

        /// <summary>
        /// Writes a file's bytes BESIDE the file they are for, as a temporary, for
        /// <see cref="Commit"/> to put in place at the end of the capture.
        ///
        /// Two frames pass between a picture being encoded and the next floor being measured, and the
        /// light test there can refuse the whole capture (see <see cref="MeasureFloor"/>). Staging is
        /// what makes that refusal free: a floor that has already been encoded has not replaced
        /// anything, so a set on disk is either left exactly as it was or replaced in one burst at
        /// the end. It is also what the temporary was always for - a reader never sees half a file.
        /// </summary>
        /// <param name="path">The file these bytes are for.</param>
        /// <param name="bytes">Its contents.</param>
        private static void Stage(string path, byte[] bytes) => File.WriteAllBytes(Staged(path), bytes);

        /// <summary>Puts a staged file in place, and does nothing when none was staged - a floor
        /// whose sidecar could not be encoded, for instance.</summary>
        /// <param name="path">The file to end up with.</param>
        private static void Commit(string path)
        {
            var temp = Staged(path);
            if (!File.Exists(temp)) return;

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Removes whatever this capture staged and is not going to commit. Called for every
        /// floor from <see cref="Cleanup"/>, so a refused, failed or abandoned capture leaves no
        /// temporaries behind; a no-op for a file already committed.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static void DropStaged(Plan plan)
        {
            if (plan?.Dir == null) return;

            foreach (var floor in plan.Floors)
            {
                Forget(plan, floor.File);
                Forget(plan, floor.DistFile);
            }
        }

        /// <summary>One staged file deleted, if it is there. Guarded on its own: a temporary nobody
        /// will ever read is not worth interrupting a cleanup for.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="name">The file name inside the capture's folder.</param>
        private static void Forget(Plan plan, string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            try
            {
                var temp = Staged(Path.Combine(plan.Dir, name));
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: {name}.tmp could not be removed ({ex.Message}).");
            }
        }

        private static string Staged(string path) => path + ".tmp";

        private static string Share(int part, int whole) =>
            whole <= 0 ? "0" : (part * 100f / whole).ToString("0", CultureInfo.InvariantCulture);

        // --- the previous capture ----------------------------------------------------------------

        /// <summary>Where the player is standing, in world XZ, at the moment the key was pressed.
        /// Zero when there is no player to ask, which makes every distance in the sidecar a distance
        /// from the world origin - wrong, but consistently wrong, and the merge still prefers the
        /// nearer of two such captures.</summary>
        private Vector2 CapturePoint()
        {
            try
            {
                var player = _gameWorld?.MainPlayer;
                if (player == null) return Vector2.zero;

                // Player.Transform, not the obsolete MonoBehaviour transform: the game hides the
                // latter behind an Obsolete attribute this project treats as an error.
                var at = player.Transform.position;
                return new Vector2(at.x, at.z);
            }
            catch
            {
                return Vector2.zero;
            }
        }

        /// <summary>The meta of a capture of this map already on disk, when this capture may be merged
        /// into it, and null - having said in the log why - when it may not.
        ///
        /// Why a merge is wanted at all: the game streams distant terrain and building chunks OUT, so
        /// one capture of a large map has whole regions the camera found empty - the west third of
        /// Customs, from a player standing at the east end. There is no way to force them back in from
        /// here, but a second capture taken from the other end of the map has them, and its own holes
        /// are somewhere else. So captures accumulate into one picture instead of replacing it.
        ///
        /// Merging two pictures byte for byte is only honest when every one of these matches:
        ///   - the schema, or the fields do not mean the same things;
        ///   - the extent, to the exact double, or the pixels are of different places;
        ///   - the pixels per metre, or they are of different SIZES;
        ///   - the floor levels, or a basement would be merged into a rooftop;
        ///   - the encoding - the stored exposure and the gamma this capture would apply - or identical
        ///     light would become different bytes and the seam between the two would be visible.
        /// Any mismatch and this capture starts fresh, which is the correct outcome and not a failure;
        /// it says so in one Info line naming what changed.</summary>
        /// <param name="plan">The capture's plan, already holding the extent, scale and floors.</param>
        /// <param name="needsGamma">Whether this capture will gamma-encode its pixels, which has to
        /// match what the stored exposure was written with.</param>
        private static CaptureMeta LoadPrevious(Plan plan, bool needsGamma)
        {
            var path = Path.Combine(plan.Dir, $"{plan.Key}.map.json");

            try
            {
                if (!File.Exists(path)) return null;

                var meta = JsonConvert.DeserializeObject<CaptureMeta>(File.ReadAllText(path));
                if (meta == null || meta.Extent == null || meta.Floors == null || meta.Floors.Count == 0)
                {
                    Fresh(plan, "the capture already there cannot be read");
                    return null;
                }

                if (meta.SchemaVersion != SchemaVersion)
                {
                    Fresh(plan, $"the capture already there is schema {meta.SchemaVersion} and this build writes {SchemaVersion}");
                    return null;
                }

                if (meta.Extent.MinX != plan.Extent.MinX || meta.Extent.MinZ != plan.Extent.MinZ ||
                    meta.Extent.MaxX != plan.Extent.MaxX || meta.Extent.MaxZ != plan.Extent.MaxZ)
                {
                    Fresh(plan, "the map has been measured differently since (its extent moved)");
                    return null;
                }

                if (meta.PxPerMetre != plan.Ppm)
                {
                    Fresh(plan, $"it was captured at {Ppm(meta.PxPerMetre)} px/m and this one is {Ppm(plan.Ppm)}");
                    return null;
                }

                var mine = plan.Floors.Select(f => f.Dto.Level).OrderBy(l => l).ToArray();
                var theirs = meta.Floors.Select(f => f.Level).OrderBy(l => l).ToArray();

                if (!mine.SequenceEqual(theirs))
                {
                    Fresh(plan, $"its floors were {Levels(theirs)} and this capture's are {Levels(mine)}");
                    return null;
                }

                if (!string.Equals(meta.Lighting, LightingTag, StringComparison.Ordinal))
                {
                    // The lighting is part of what a pixel's brightness MEANS. A capture taken before
                    // the capture light existed records no lighting at all and is one of the black
                    // rain-era pictures this replaces; one taken at a different intensity would merge
                    // into a visible seam.
                    Fresh(plan, string.IsNullOrEmpty(meta.Lighting)
                        ? $"it was taken before the capture light existed, and this one is lit {LightingTag}"
                        : $"it was lit {meta.Lighting} and this one is lit {LightingTag}");
                    return null;
                }

                var gamma = needsGamma ? 1f / 2.2f : 1f;

                foreach (var floor in meta.Floors)
                {
                    if (floor.Exposure == null)
                    {
                        Fresh(plan, "it does not record the exposure it was developed with");
                        return null;
                    }

                    if (floor.Exposure.High - floor.Exposure.Low < MinExposureRange)
                    {
                        Fresh(plan, "the exposure it records is degenerate");
                        return null;
                    }

                    if (Math.Abs(floor.Exposure.Gamma - gamma) > 1e-4f)
                    {
                        Fresh(plan, $"it was developed with gamma {G(floor.Exposure.Gamma)} and this machine " +
                                    $"renders in {(needsGamma ? "half-float linear" : "eight bits")}, which needs {G(gamma)}");
                        return null;
                    }
                }

                return meta;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the capture already in {plan.Dir} could not be read ({ex.GetType().Name}: {ex.Message}) - " +
                    "this capture starts fresh.");
                return null;
            }
        }

        /// <summary>One line saying this capture replaces rather than adds to what is there, and why.
        /// Info rather than a warning: it is the right thing to do whenever the map's own measurements
        /// have moved, and the player has lost nothing they can still use.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="why">What differs, as a phrase.</param>
        private static void Fresh(Plan plan, string why)
        {
            Plugin.LogSource?.LogInfo(
                $"QuestTree: the capture of {plan.Key} already on disk cannot be added to - {why} - so this one " +
                "replaces it.");
        }

        /// <summary>When the set on disk was first captured: its own firstCapturedAt, or its
        /// capturedAt when it was written before that field existed.</summary>
        /// <param name="meta">The previous meta.</param>
        private static string FirstOf(CaptureMeta meta) =>
            string.IsNullOrEmpty(meta.FirstCapturedAt) ? meta.CapturedAt : meta.FirstCapturedAt;

        /// <summary>The exposure the previous capture of this floor was developed with, or null when
        /// there is no previous capture to match.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed.</param>
        private static ExposureResult Stored(Plan plan, FloorPlan floor)
        {
            var previous = plan.Previous;
            if (previous?.Floors == null || floor?.Dto == null) return null;

            foreach (var stored in previous.Floors)
            {
                if (stored == null || stored.Level != floor.Dto.Level || stored.Exposure == null) continue;

                return new ExposureResult
                {
                    Low = stored.Exposure.Low,
                    High = stored.Exposure.High,
                    Gamma = stored.Exposure.Gamma
                };
            }

            return null;
        }

        /// <summary>Loads the picture and the distance sidecar already on disk for this floor into the
        /// floor's merge buffers. Silently leaves them null when there is nothing to merge; says so
        /// when there is something and it cannot be used.
        ///
        /// The sidecar is what makes the merge BEST-OF rather than newest-wins: it holds, per pixel,
        /// how far the capturing player was from that spot. A picture rendered from 900 m away is drawn
        /// at the lowest level of detail the game has, so overwriting a pixel somebody captured from
        /// 50 m away with one captured from 900 would make the picture worse the more it was captured.
        /// With the sidecar, every pixel converges on the closest view anybody has taken of it.
        ///
        /// A picture with NO sidecar - one taken by a build before this existed - is treated as all
        /// distances unknown, so this capture's own pixels win everywhere. That happens once per map.
        ///
        /// Two steps on two frames, one file each: a floor-sized PNG decode plus the GetPixels32 that
        /// copies it out is a couple of hundred milliseconds, and the second read only happens at all
        /// when the first found a picture to merge into.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose previous picture is wanted.</param>
        private static void LoadPreviousColour(Plan plan, FloorPlan floor)
        {
            if (plan.Previous == null) return;

            floor.PreviousColour = ReadPicture(
                Path.Combine(plan.Dir, floor.File), plan, floor, "picture", TextureFormat.RGB24);
        }

        /// <summary>The distance sidecar beside the picture <see cref="LoadPreviousColour"/> just
        /// read, as one byte a pixel. Absent or unreadable leaves it null, which makes this capture's
        /// own pixels the better ones everywhere.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose previous sidecar is wanted.</param>
        private static void LoadPreviousDist(Plan plan, FloorPlan floor)
        {
            // RGBA32 for the read: LoadImage reformats the texture to suit the PNG anyway, and only
            // the red channel is taken out of it - which is the channel EncodeSidecar wrote the
            // value into, in all three.
            var distances = ReadPicture(
                Path.Combine(plan.Dir, floor.DistFile), plan, floor, "distance sidecar", TextureFormat.RGBA32);

            if (distances == null)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a picture but no distance sidecar, so this " +
                    "capture's own pixels are taken as the better ones everywhere. The sidecar it writes now " +
                    "makes every later capture choose per pixel.");
                return;
            }

            floor.PreviousDist = new byte[distances.Length];
            for (var i = 0; i < distances.Length; i++) floor.PreviousDist[i] = distances[i].r;
        }

        /// <summary>One PNG beside the meta as pixels in texture order, or null when it is absent,
        /// unreadable or the wrong size. Its texture is freed before this returns - only the array
        /// outlives it.</summary>
        /// <param name="path">The file.</param>
        /// <param name="plan">The capture's plan, for the size the picture has to be.</param>
        /// <param name="floor">The floor, for the log lines.</param>
        /// <param name="what">What this file is, for the log lines.</param>
        /// <param name="format">The texture format to decode into.</param>
        private static Color32[] ReadPicture(
            string path, Plan plan, FloorPlan floor, string what, TextureFormat format)
        {
            Texture2D texture = null;

            try
            {
                if (!File.Exists(path)) return null;

                texture = new Texture2D(2, 2, format, mipChain: false);

                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} that is not a readable image - " +
                        "this capture draws over it.");
                    return null;
                }

                if (texture.width != plan.WidthPx || texture.height != plan.HeightPx)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} of {texture.width}x{texture.height} px " +
                        $"where this capture is {plan.WidthPx}x{plan.HeightPx} - this capture draws over it.");
                    return null;
                }

                return texture.GetPixels32();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" - its {what} could not be read " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }

        private static string Levels(int[] levels) =>
            levels.Length == 0 ? "none" : string.Join("/", levels.Select(Signed).ToArray());

        private static string Signed(int level) =>
            level > 0 ? $"+{level}" : level.ToString(CultureInfo.InvariantCulture);

        // --- exposure --------------------------------------------------------------------------

        /// <summary>The measure step as the coroutine calls it: guarded, and failing the floor when no
        /// exposure can be decided for it.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose tiles are all in.</param>
        private void MeasureFloor(Plan plan, FloorPlan floor)
        {
            try
            {
                // The exposure the pictures on disk were developed with, when this is a merge.
                var stored = Stored(plan, floor);

                // Measured either way, and on a merge it is measured but not USED: the bytes already
                // on disk were developed with the stored exposure, and two captures may only be mixed
                // pixel by pixel when identical light became identical bytes in both. What this
                // capture's own percentiles are for on a merge is the light test below - the one
                // thing the stored exposure cannot survive is the sun having moved.
                var measured = Measure(plan, floor);

                if (measured == null)
                {
                    // Nothing this render can contribute: Measure has said which of the two ways it
                    // came back empty. On a merge the floor's earlier picture is kept and the meta
                    // goes on naming it - see Carried - so a dark basement that fails here costs
                    // nothing that was already captured.
                    floor.Failed = true;
                    return;
                }

                if (stored == null)
                {
                    floor.Exposure = measured;
                    return;
                }

                // THE LIGHT TEST, and it can fail: refusing the whole capture is the only outcome
                // that loses nothing. Re-measuring would develop this capture's pixels differently
                // from every pixel already on disk and put a seam along every boundary between them;
                // merging anyway would clamp the brighter render to flat white where it filled a
                // hole; starting fresh would throw away every capture the player has taken of this
                // map. Leaving the set exactly as it is and saying so does none of those.
                var drift = IsFinite(measured.High) && IsFinite(stored.High) && stored.High > 0f
                    ? Math.Abs(measured.High / stored.High - 1f)
                    : 0f;

                if (drift > MaxExposureDrift)
                {
                    // The capture, not the floor: the sun is the same for every band of one raid, and
                    // a set half of which is this raid's light and half of which is another's is
                    // worse than a set that did not change. Run sees this and stops before anything
                    // is put in place - see Stage, and Run's own refusal line.
                    plan.Refused = true;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} was captured under different light than the picture on disk " +
                        $"(p98 {E(measured.High)} vs {E(stored.High)} stored) - nothing was changed. Capture at " +
                        $"a similar time of day to add to it, or delete " +
                        $"BepInEx/plugins/QuestTree/captures/{plan.Key} to start over.");
                    return;
                }

                floor.Exposure = stored;
                floor.ReusedExposure = true;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" keeps the exposure its first capture was " +
                    $"developed with ({E(stored.Low)}..{E(stored.High)}, gamma {G(stored.Gamma)}); this " +
                    $"capture's own p98 is {E(measured.High)}, {(drift * 100f).ToString("0", CultureInfo.InvariantCulture)} % from it.");
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be exposed " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Turns one floor's linear pixels into the eight-bit texture a PNG is made from, and
        /// says what it did.
        ///
        /// The player's own view of this world is tonemapped by the game's post-processing, which our
        /// camera does not run; what comes back instead is raw linear light, mostly a long way below 1.
        /// Rather than guess an exposure from the sun or the time of day - which would be wrong indoors,
        /// at night, and on any modded map - the picture is stretched to its own content: the 2nd and
        /// 98th luminance percentiles become black and white. That cannot blow out a night capture and
        /// cannot be thrown off by one bright window, and it needs to know nothing about the scene.
        ///
        /// The percentiles are read from every 8th pixel, which is 600k samples on a large floor.
        /// Then, when the data is linear, the stretched value is encoded with the usual 1/2.2 so the
        /// midtones land where an eye expects them; eight-bit data has already been through the GPU's
        /// sRGB encoder and is left alone.
        ///
        /// Null, having warned, when the floor has no usable range at all - a black picture, which is
        /// what a failed render looks like, and stretching it would turn noise into a pattern that
        /// could be mistaken for a map.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being measured; its pixel buffer is read and nothing is
        /// written.</param>
        private ExposureResult Measure(Plan plan, FloorPlan floor)
        {
            var pixels = floor.Pixels;
            var count = plan.WidthPx * plan.HeightPx;

            if (pixels == null)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has no pixels to expose.");
                return null;
            }

            var samples = new List<float>(count / ExposureSampleStride + 1);
            var drawn = floor.Drawn;

            for (var i = 0; i < count; i += ExposureSampleStride)
            {
                // Pixels nothing drew are the clear colour, not the map. Sampling them would put the
                // low percentile at exactly zero on any map with streamed-out chunks - which is what
                // the first real capture of Customs did, where a third of the picture was hole - and
                // the whole point of a percentile is that it describes the CONTENT.
                if (drawn != null && !drawn[i]) continue;

                var at = i * 3;
                var luminance = Luminance(pixels[at], pixels[at + 1], pixels[at + 2]);
                if (IsFinite(luminance)) samples.Add(luminance);
            }

            if (samples.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" came back with no drawn pixels at all and was not " +
                    "written.");
                return null;
            }

            samples.Sort();

            var low = Percentile(samples, ExposureLowPercentile);
            var high = Percentile(samples, ExposureHighPercentile);

            // The check the rain capture would have failed. Judged on the RAW linear percentile,
            // before any stretch: a floor whose brightest 2 % is under a fiftieth of white was not lit,
            // and stretching it would multiply a near-nothing by two hundred and write a field of
            // noise with a few emissive specks in it - which is exactly the picture that came back,
            // 80 kB for two million pixels, and which the stretch reported as a successful
            // 0.0000..0.5051 exposure. A fresh capture is therefore refused outright rather than
            // developed; a merge cannot reach this, because a merge keeps the exposure the first
            // capture stored and never measures.
            if (high < MinUsableHigh)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" rendered too dark to be a map (p98 {E(high)}) - " +
                    "nothing was written. Heavy weather or night; try again in daylight.");
                return null;
            }

            // The percentiles can sit on top of each other on a floor that is mostly one tone - a
            // basement, or a render that failed - and then the full range is the better description.
            if (high - low < MinExposureRange)
            {
                low = samples[0];
                high = samples[samples.Count - 1];

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has almost no tonal range " +
                    $"({E(low)}..{E(high)}) - stretching its full range instead of its percentiles.");
            }

            if (high - low < MinExposureRange)
            {
                // The check that can fail, and the one that catches a black render: a floor with no
                // range at all is not a picture of anything.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" is a single flat tone " +
                    $"({E(low)}..{E(high)}) - nothing was drawn, so it was not written. If every floor says " +
                    "this, the capture camera is rendering nothing at all.");
                return null;
            }

            return new ExposureResult { Low = low, High = high, Gamma = _needsGamma ? 1f / 2.2f : 1f };
        }

        /// <summary>
        /// Turns the floor's linear pixels into the eight-bit picture a PNG is made from and fills
        /// its distance sidecar, merging whatever is already on disk into both.
        ///
        /// SPREAD OVER FRAMES, one step each, and it has to be. The per-pixel arithmetic alone was
        /// measured at 180-220 ms for a 2360x2040 floor on a desktop JIT - Mono in a raid is slower -
        /// and before it run a PNG decode and a GetPixels32 of each of the two files already on disk,
        /// a couple of hundred milliseconds apiece. In ONE frame that is the half-second freeze in
        /// the middle of a raid this whole class is written to avoid; as one step a frame it is the
        /// same handful of short hitches the tile renders already are. Every step is guarded on its
        /// own, and any of them failing abandons the floor exactly as the single-frame version did.
        ///
        /// Three things happen per pixel:
        ///   1 the stretch and the grade (see <see cref="Grade"/>) turn linear light into a byte;
        ///   2 the distance from the capturing player is written to the sidecar, in four-metre steps,
        ///     or <see cref="DistanceEmpty"/> where the camera drew nothing;
        ///   3 the merge chooses between this capture's pixel and the one on disk - BEST OF the two,
        ///     by that distance, not newest wins. A pixel rendered from 900 m away is drawn at the
        ///     game's lowest level of detail, so newest-wins would make a map worse the more often it
        ///     was captured. This way every pixel converges on the closest look anybody has had at it.
        /// A pixel this capture did not draw never replaces anything, which is what makes the holes
        /// fill in rather than move around.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed.</param>
        private IEnumerator Develop(Plan plan, FloorPlan floor)
        {
            yield return null;

            if (!DevelopBegin(plan, floor)) yield break;

            // The previous picture and its sidecar, a file to a frame: each is a PNG decode of a
            // floor-sized image plus the GetPixels32 that copies it out of the texture.
            if (plan.Previous != null)
            {
                yield return null;
                LoadPreviousColour(plan, floor);

                if (floor.PreviousColour != null)
                {
                    yield return null;
                    LoadPreviousDist(plan, floor);
                }
            }

            floor.Merged = floor.PreviousColour != null;

            for (var y0 = 0; y0 < plan.HeightPx; y0 += PixelBandRows)
            {
                yield return null;
                if (!DevelopBand(plan, floor, y0)) yield break;
            }

            yield return null;
            DevelopFinish(plan, floor);
        }

        /// <summary>The development's first step: the checks, the floor's eight-bit texture, and the
        /// two buffers every band works from. False, having failed the floor and said why, when there
        /// is nothing to develop.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed.</param>
        private bool DevelopBegin(Plan plan, FloorPlan floor)
        {
            if (floor.Pixels == null || floor.Exposure == null || floor.Drawn == null || floor.Dist == null)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be developed - it has no pixels or no " +
                    "exposure.");
                return false;
            }

            try
            {
                floor.Texture = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGB24, mipChain: false);
                floor.Block = new Color32[plan.WidthPx * PixelBandRows];

                // The squared X distance of every column from the player, once for the whole floor
                // rather than once per pixel: the per-pixel work is then one add and one square root.
                floor.DxSquared = new float[plan.WidthPx];

                for (var col = 0; col < plan.WidthPx; col++)
                {
                    var dx = (float)(plan.Extent.MinX + (col + 0.5d) / plan.Ppm) - plan.From.x;
                    floor.DxSquared[col] = dx * dx;
                }

                return true;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be developed " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>One band of rows developed, merged and uploaded to the floor's texture. The step
        /// the frame budget is built around: <see cref="PixelBandRows"/> rows of a large floor is
        /// 600k pixels, the same order of work as one tile's readback. False, having failed the
        /// floor, when it threw.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed.</param>
        /// <param name="y0">The band's first row, counting from the bottom of the texture.</param>
        private bool DevelopBand(Plan plan, FloorPlan floor, int y0)
        {
            try
            {
                var pixels = floor.Pixels;
                var previous = floor.PreviousColour;
                var previousDist = floor.PreviousDist;
                var block = floor.Block;
                var dxSquared = floor.DxSquared;

                var low = floor.Exposure.Low;
                var gamma = floor.Exposure.Gamma;
                var scale = 1f / (floor.Exposure.High - floor.Exposure.Low);

                var rows = Math.Min(PixelBandRows, plan.HeightPx - y0);

                for (var row = 0; row < rows; row++)
                {
                    var textureRow = y0 + row;

                    // Texture row 0 is the BOTTOM of the picture, which is the extent's -z edge;
                    // image row 0 is its top. See RenderTile for the same conversion.
                    var worldZ = (float)(plan.Extent.MaxZ - (plan.HeightPx - 1 - textureRow + 0.5d) / plan.Ppm);
                    var dz = worldZ - plan.From.y;
                    var dzSquared = dz * dz;

                    var pixelRow = textureRow * plan.WidthPx;
                    var target = row * plan.WidthPx;

                    for (var col = 0; col < plan.WidthPx; col++)
                    {
                        var index = pixelRow + col;
                        var at = index * 3;

                        var drawn = floor.Drawn[index];
                        var distance = drawn
                            ? Steps(Mathf.Sqrt(dxSquared[col] + dzSquared))
                            : DistanceEmpty;

                        floor.Dist[index] = distance;

                        var oldDistance = previousDist != null && previous != null
                            ? previousDist[index]
                            : DistanceEmpty;

                        var oldPixel = previous != null ? previous[index] : default(Color32);

                        // Whether the picture on disk has a pixel here, and the SIDECAR is the
                        // authority whenever there is one. A pixel the grade took to pure black is a
                        // real pixel of real shadow - everything at or below the exposure's low
                        // percentile lands there, which is 2 % of every capture by construction - so
                        // reading black as "nothing drawn" would let a capture taken from 900 m away
                        // overwrite one taken from 50 m, and would throw that pixel's recorded
                        // distance away the first time a later capture happened not to draw it. The
                        // colour test is only for a picture written before sidecars existed, where
                        // black is the only signal there is.
                        var oldDrawn = previous != null && (previousDist != null
                            ? oldDistance != DistanceEmpty
                            : oldPixel.r > 0 || oldPixel.g > 0 || oldPixel.b > 0);

                        // Take this capture's pixel when it drew one AND either nothing better is
                        // there or it saw the spot from closer. Everything else keeps what was
                        // there, which for a first capture is black.
                        var take = drawn && (!oldDrawn || distance < oldDistance);

                        if (take)
                        {
                            block[target + col] = Grade(
                                pixels[at], pixels[at + 1], pixels[at + 2], low, scale, gamma);

                            floor.Filled++;
                        }
                        else
                        {
                            block[target + col] = oldPixel;
                            floor.Dist[index] = oldDrawn ? oldDistance : DistanceEmpty;

                            if (oldDrawn) floor.Kept++;
                            else floor.StillEmpty++;
                        }
                    }
                }

                floor.Texture.SetPixels32(0, y0, plan.WidthPx, rows, Slice(block, plan.WidthPx * rows));
                return true;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be developed " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>The upload of the finished picture and the release of everything the development
        /// needed. Its own frame because Apply pushes the whole eight-bit picture to the GPU.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor that has just been developed.</param>
        private static void DevelopFinish(Plan plan, FloorPlan floor)
        {
            try
            {
                floor.Texture.Apply(updateMipmaps: false);
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be developed " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                // Everything the development held and nothing else: floor.Dist stays, because the
                // sidecar is written out of it a frame later.
                floor.Pixels = null;
                floor.PreviousColour = null;
                floor.PreviousDist = null;
                floor.Block = null;
                floor.DxSquared = null;
            }
        }

        /// <summary>One pixel from linear light to the byte that goes in the PNG.
        ///
        /// A pure function of the pixel and the MAP's stored exposure, deliberately: two captures of
        /// one map are merged pixel by pixel, so the same light has to become the same byte in both,
        /// whenever and wherever each was taken. Nothing here may depend on this capture's own
        /// statistics.
        ///
        /// In order: the percentile stretch, clamped so the top 2 % cannot blow out; 35 % of the
        /// saturation taken out, because a photograph of grass and rust fights the pins drawn over it
        /// and a map should not; 40 % of a smoothstep S-curve on the luminance, which gives the
        /// picture the shape a drawn map has; a scale to 82 % so nothing in the picture is as bright as
        /// a white label; and the gamma the data needs. The result is meant to read as a muted
        /// satellite photograph rather than a screenshot.</summary>
        /// <param name="r">Linear red.</param>
        /// <param name="g">Linear green.</param>
        /// <param name="b">Linear blue.</param>
        /// <param name="low">The stored luminance that becomes black.</param>
        /// <param name="scale">1 / (high - low) from the stored exposure.</param>
        /// <param name="gamma">The stored exponent, 1/2.2 for linear data or 1 for encoded data.</param>
        private static Color32 Grade(float r, float g, float b, float low, float scale, float gamma)
        {
            var sr = Stretch(r, low, scale);
            var sg = Stretch(g, low, scale);
            var sb = Stretch(b, low, scale);

            var luminance = Luminance(sr, sg, sb);

            // Toward grey by a third, which keeps the difference between a roof and the road and drops
            // the difference between two shades of moss.
            sr += (luminance - sr) * GradeDesaturation;
            sg += (luminance - sg) * GradeDesaturation;
            sb += (luminance - sb) * GradeDesaturation;

            // The S-curve is applied as a per-pixel GAIN on the luminance, so the colours keep their
            // ratios instead of drifting as each channel is curved on its own.
            if (luminance > 1e-4f)
            {
                var curved = luminance * luminance * (3f - 2f * luminance);
                var gain = 1f + (curved / luminance - 1f) * GradeSCurveBlend;

                sr *= gain;
                sg *= gain;
                sb *= gain;
            }

            return new Color32(
                Encode(sr, gamma), Encode(sg, gamma), Encode(sb, gamma), 255);
        }

        /// <summary>The percentile stretch alone: black at the low percentile, white at the high one,
        /// and clamped, so a highlight above the high percentile cannot reach past white and out the
        /// other side of the grade.</summary>
        /// <param name="value">The channel's linear value.</param>
        /// <param name="low">The luminance mapped to black.</param>
        /// <param name="scale">1 / (high - low).</param>
        private static float Stretch(float value, float low, float scale)
        {
            if (!IsFinite(value)) return 0f;

            var stretched = (value - low) * scale;
            if (stretched <= 0f) return 0f;
            return stretched >= 1f ? 1f : stretched;
        }

        /// <summary>A graded channel to a byte: down to the highlight ceiling, encoded, rounded.</summary>
        /// <param name="value">The graded channel, 0..1 or a little outside it.</param>
        /// <param name="gamma">The exponent to apply, or 1 for none.</param>
        private static byte Encode(float value, float gamma)
        {
            if (!IsFinite(value) || value <= 0f) return 0;

            var ceilinged = value * GradeHighlightCeiling;
            if (ceilinged > GradeHighlightCeiling) ceilinged = GradeHighlightCeiling;

            if (gamma != 1f) ceilinged = Mathf.Pow(ceilinged, gamma);

            var scaled = ceilinged * 255f + 0.5f;
            return scaled >= 255f ? (byte)255 : (byte)scaled;
        }

        /// <summary>Metres to a sidecar step, saturating one below the value that means "nothing drawn
        /// here" so a real pixel a kilometre away is never mistaken for an empty one.</summary>
        /// <param name="metres">Distance from the capturing player.</param>
        private static byte Steps(float metres)
        {
            if (!IsFinite(metres) || metres <= 0f) return 0;

            var steps = (int)(metres / DistanceStepMetres + 0.5f);
            return steps >= DistanceMax ? DistanceMax : (byte)steps;
        }

        /// <summary>Rec. 709 luminance, which is what "how bright is this pixel" means for a picture
        /// meant to be looked at.</summary>
        private static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

        /// <summary>The value at a share of the way through a SORTED sample.</summary>
        /// <param name="sorted">The sample, ascending.</param>
        /// <param name="share">0..1.</param>
        private static float Percentile(List<float> sorted, float share)
        {
            var index = Mathf.Clamp(Mathf.RoundToInt((sorted.Count - 1) * share), 0, sorted.Count - 1);
            return sorted[index];
        }

        /// <summary>The first <paramref name="used"/> entries of a reused block, since SetPixels32
        /// insists the array it is given is exactly the size of the region.</summary>
        /// <param name="block">The reused block.</param>
        /// <param name="used">How many entries the last band filled.</param>
        private static Color32[] Slice(Color32[] block, int used)
        {
            if (used == block.Length) return block;

            var exact = new Color32[used];
            Array.Copy(block, exact, used);
            return exact;
        }

        /// <summary>What the exposure pass decided, for the log line and the meta.</summary>
        private sealed class ExposureResult
        {
            public float Low;
            public float High;
            public float Gamma;
        }

        // --- the camera ------------------------------------------------------------------------

        /// <summary>Builds the one camera every floor and tile is rendered with, its render target and
        /// the staging texture tiles are read back into.
        ///
        /// The camera is a COPY of the live first-person camera's settings, because that is what Phase
        /// 0 round 2 established gets the world drawn: DeferredShading, allowHDR and the game's own
        /// rendering path, without this file having to name any of them. CopyFrom copies settings and
        /// no components, so none of the game's own scripts come with it. Only the framing, the
        /// background, the culling mask and the target are then overridden - and the target is a
        /// half-float one, which is the other half of the fix: the deferred pipeline's output is
        /// linear light well outside 0..1, and eight bits of it is the near-black picture round 1
        /// produced.
        ///
        /// With no live camera to copy - not seen in a raid, but Camera.main is null in some loading
        /// states - a bare camera is used instead and said so in the note. It renders through the
        /// project's default path rather than the game's, which round 1 shows is darker still; the
        /// exposure stretch is what makes even that usable.</summary>
        /// <param name="plan">The capture's plan, for the tile size the ortho view is framed to.</param>
        /// <param name="note">A phrase for the log describing how the camera was built.</param>
        private bool BuildCamera(Plan plan, out string note)
        {
            var main = LiveCamera();
            int copied;

            _camera = new GameObject("QuestTreeCaptureCamera").AddComponent<Camera>();

            if (main != null)
            {
                // Settings only - rendering path, HDR, layer mask, clear flags - and no components.
                _camera.CopyFrom(main);
                copied = main.cullingMask;
                note = $"settings copied from \"{main.name}\" ({_camera.renderingPath}/{_camera.actualRenderingPath})";
            }
            else
            {
                // Everything a copy would have brought has to be named, and the one that matters -
                // the rendering path - cannot be: it is whatever the project defaults to.
                copied = ~0;
                note = "a bare camera (CameraManager.instance.Camera and Camera.main are both null, " +
                       "so the game's own rendering path could not be copied)";
            }

            // A known background rather than the skybox or whatever the first-person camera was
            // clearing with. Black with ZERO ALPHA, so a pixel nothing drew is four exact zeroes in
            // the float target - which is what tells the merge that a chunk the game had streamed out
            // is a hole to be filled rather than a black roof to be kept. See RenderTile.
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);

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
            _camera.allowMSAA = false;
            _camera.depth = -100f;
            var mask = CaptureMask(copied);
            _camera.cullingMask = mask;

            BuildLight(mask);
            BuildTarget();

            // Assigned for the whole capture, not per tile: WorldToScreenPoint reads the camera's
            // pixel size from its target, and the self-check is only meaningful in the tile's own
            // 2048x2048 pixels.
            _camera.targetTexture = _rt;

            note = _hdr
                ? $"{note}, half-float target"
                : $"{note}, EIGHT-BIT target (no half-float support)";

            if (_light != null)
            {
                note = $"{note}, own light {CaptureLightIntensity.ToString("0.###", CultureInfo.InvariantCulture)}";
            }

            return true;
        }

        /// <summary>Adds the capture's own directional light, disabled. Straight DOWN, so it casts no
        /// long shadows of its own and the scene's sun keeps whatever shadows it is drawing; white,
        /// shadowless, per-pixel, and on exactly the layers the capture draws, so it lights the map and
        /// nothing else. A failure here is one debug line: a capture in sunlight does not need it.</summary>
        /// <param name="mask">The capture's culling mask, so the light reaches what the camera sees.</param>
        private void BuildLight(int mask)
        {
            try
            {
                var go = new GameObject("QuestTreeCaptureLight");
                go.transform.SetParent(null, worldPositionStays: true);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                _light = go.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.color = Color.white;
                _light.intensity = CaptureLightIntensity;
                _light.shadows = LightShadows.None;
                _light.cullingMask = mask;
                _light.renderMode = LightRenderMode.ForcePixel;

                // Off until a tile is actually being rendered - see RenderOnce. A directional light
                // left enabled would light the player's own frame as well, which is a cheat and looks
                // like one.
                _light.enabled = false;
            }
            catch (Exception ex)
            {
                _light = null;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the capture's own light could not be made ({ex.GetType().Name}: {ex.Message}) - " +
                    "the picture will be as bright as the scene's own lighting makes it.");
            }
        }

        /// <summary>The render target and the texture tiles are read back into, half-float when the
        /// hardware renders one.</summary>
        private void BuildTarget()
        {
            _hdr = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);

            if (!_hdr)
            {
                // Recoverable, and worth a warning rather than a debug line: every picture from this
                // machine will band in its dark areas, and the reason should be findable.
                Plugin.LogSource?.LogWarning(
                    "QuestTree: this graphics device reports no half-float render target, so map captures " +
                    "are taken in eight bits. The picture is still exposed the same way; expect banding " +
                    "in dark interiors.");
            }

            // allowHDR only means anything against a float target; with an eight-bit one it costs an
            // extra resolve for nothing.
            _camera.allowHDR = _hdr;

            // Linear values only come back from a float target. An eight-bit target in a linear-space
            // project is written through the GPU's sRGB encoder, and a gamma-space project encodes
            // nothing at all, so in both of those the data is already display-ready.
            _needsGamma = _hdr && QualitySettings.activeColorSpace == ColorSpace.Linear;

            _rt = new RenderTexture(
                TileSize, TileSize, 24,
                _hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32);

            _stage = new Texture2D(
                TileSize, TileSize,
                _hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32,
                mipChain: false);

            Plugin.LogSource?.LogDebug(
                $"QuestTree: capture target {_rt.format}, staging {_stage.format}, colour space " +
                $"{QualitySettings.activeColorSpace}, gamma encoding " +
                $"{(_needsGamma ? "applied by us" : "already in the data")}.");
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

        /// <summary>One render of the camera where it stands, with fog off - a hundred metres of aerial
        /// perspective over a map read from above is a grey wash - and restored by the finally, so a
        /// player's own next frame is drawn with the scene's own settings whatever happens here.
        ///
        /// Nothing is done to the lighting. Phase 0 round 2 forced flat white ambient on two of its
        /// five variants and the pictures came back the same as the ones without it, so
        /// RenderSettings.ambient has no effect on this scene's deferred output and changing it would
        /// be a side effect with no benefit.</summary>
        private void RenderOnce()
        {
            var fog = RenderSettings.fog;

            try
            {
                RenderSettings.fog = false;
                if (_light != null) _light.enabled = true;

                _camera.Render();
            }
            finally
            {
                // Both restored by the same statement that changed them, and for the same reason: the
                // player's next frame must be drawn with the scene's own fog and the scene's own
                // lights, not ours.
                if (_light != null) _light.enabled = false;
                RenderSettings.fog = fog;
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

        /// <summary>The copied mask with everything in <see cref="ExcludedLayerNames"/> taken out.
        ///
        /// Built by SUBTRACTING from what the game's own camera draws rather than by listing what to
        /// keep, so a layer this game version has and this file has never heard of still appears in
        /// the picture. What is left is logged once per session, by name, because a mask is a number
        /// nobody can check by eye and the layer numbering is not the same in every game version.</summary>
        /// <param name="copied">The live camera's own mask, or ~0 when there was none to copy.</param>
        private static int CaptureMask(int copied)
        {
            var mask = copied;
            var removed = new List<string>();
            var missing = new List<string>();

            foreach (var name in ExcludedLayerNames)
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer < 0)
                {
                    missing.Add(name);
                    continue;
                }

                if ((mask & (1 << layer)) != 0) removed.Add($"{name}({layer})");
                mask &= ~(1 << layer);
            }

            if (!_loggedLayers)
            {
                _loggedLayers = true;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: map captures draw layers [{MaskNames(mask)}] and leave out " +
                    $"[{string.Join(", ", removed.ToArray())}]" +
                    (missing.Count == 0
                        ? "."
                        : $"; this game version has no layer named {string.Join(", ", missing.ToArray())}."));
            }

            return mask;
        }

        /// <summary>The named layers a mask includes, for the log line above.</summary>
        /// <param name="mask">A culling mask.</param>
        private static string MaskNames(int mask)
        {
            var names = new List<string>();

            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;

                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name)) names.Add($"{name}({i})");
            }

            return string.Join(", ", names.ToArray());
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

                    // Anything this capture staged and did not commit - a refused capture's floors, a
                    // raid that ended mid-capture. Each deletion is guarded on its own, so this
                    // cannot keep the camera below from being destroyed.
                    DropStaged(_plan);

                    _plan = null;
                }

                if (_camera != null)
                {
                    var go = _camera.gameObject;
                    _camera.targetTexture = null;
                    _camera = null;
                    Destroy(go);
                }

                if (_light != null)
                {
                    var light = _light.gameObject;
                    _light.enabled = false;
                    _light = null;
                    Destroy(light);
                }

                if (_rt != null)
                {
                    if (ReferenceEquals(RenderTexture.active, _rt)) RenderTexture.active = null;
                    _rt.Release();
                    Destroy(_rt);
                    _rt = null;
                }

                if (_stage != null)
                {
                    var stage = _stage;
                    _stage = null;
                    Destroy(stage);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the map capture camera could not be cleaned up ({ex.Message}).");
            }
        }

        /// <summary>Frees one floor's two big allocations - the float buffer and the eight-bit picture -
        /// whichever of them it still holds.</summary>
        /// <param name="floor">The floor to release, or null.</param>
        private static void ReleaseTexture(FloorPlan floor)
        {
            if (floor == null) return;

            floor.Pixels = null;
            floor.Drawn = null;
            floor.Dist = null;
            floor.PreviousColour = null;
            floor.PreviousDist = null;
            floor.Block = null;
            floor.DxSquared = null;

            if (floor.Texture != null)
            {
                var tex = floor.Texture;
                floor.Texture = null;
                Destroy(tex);
            }
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

                // Nothing new on disk, nothing to say about it: the meta already there still
                // describes the pictures already there, and rewriting it would only move its date
                // and its capture count for a capture that produced nothing.
                if (written.Count == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: no floor of {plan.Key} could be captured - nothing was written. The warnings " +
                        "above say why for each.");
                    return;
                }

                // The floors the meta will name, and the files that are therefore NOT stale. A floor
                // this capture could not take keeps whatever an earlier capture of it left on disk:
                // see Carried, where the reason is that the alternative is throwing a set away.
                var floors = new List<CaptureFloor>();
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var carried = 0;

                foreach (var floor in plan.Floors)
                {
                    CaptureFloor entry;

                    if (!floor.Failed && floor.Bytes > 0)
                    {
                        entry = Described(plan, floor);

                        // Only now does this capture touch anything a reader looks at, and in the
                        // order the crash story depends on: the picture, then its sidecar, and the
                        // meta after every floor - see Stage and WriteSidecar.
                        Commit(Path.Combine(plan.Dir, floor.File));
                        if (!string.IsNullOrEmpty(floor.DistFile)) Commit(Path.Combine(plan.Dir, floor.DistFile));
                    }
                    else
                    {
                        entry = Carried(plan, floor);
                        if (entry == null) continue;

                        carried++;

                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was not captured this time, so the " +
                            "picture an earlier capture left is kept and the meta goes on naming it.");
                    }

                    floors.Add(entry);

                    // The entry's OWN file name, not this plan's: a carried entry names whatever the
                    // capture that wrote it named, and keeping the wrong name here would have the
                    // stale-picture sweep delete the file the meta has just promised.
                    keep.Add(entry.File);
                    if (!string.IsNullOrEmpty(floor.DistFile)) keep.Add(floor.DistFile);
                }

                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

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
                    CapturedAt = now,
                    FirstCapturedAt = string.IsNullOrEmpty(plan.FirstCapturedAt) ? now : plan.FirstCapturedAt,
                    Captures = plan.Captures,
                    ModVersion = ModInfo.Stamp,
                    Lighting = LightingTag,
                    TimeOfDay = TimeOfDay(),
                    Floors = floors,
                    Labels = plan.Labels,
                };

                var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
                var path = Path.Combine(plan.Dir, $"{plan.Key}.map.json");
                var temp = path + ".tmp";

                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                DropStalePictures(plan, keep);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capture of {plan.Key} written - {written.Count} floor(s), {plan.Bytes} bytes, " +
                    (carried > 0 ? $"{carried} floor(s) kept from an earlier capture, " : "") +
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

                // Offered to the host, which may well refuse - see MapTransfer.UploadCapture and the
                // UploadCaptures setting. It returns at once and runs on the plugin object rather than
                // this one, so an upload outlives the raid the capture was taken in.
                try
                {
                    MapTransfer.UploadCapture(plan.Key);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: the capture of {plan.Key} could not be offered to the host ({ex.Message}) - it " +
                        "stays on this machine.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture of {plan.Key} has its pictures but no meta file " +
                    $"({ex.GetType().Name}: {ex.Message}) - it will be ignored until it is captured again.");
            }
        }

        /// <summary>One captured floor as the meta describes it.</summary>
        /// <param name="plan">The capture's plan, for the picture's size.</param>
        /// <param name="floor">The floor that was just written.</param>
        private static CaptureFloor Described(Plan plan, FloorPlan floor) => new CaptureFloor
        {
            Level = floor.Dto.Level,
            Name = floor.Dto.Name,
            File = floor.File,
            Width = plan.WidthPx,
            Height = plan.HeightPx,
            MinY = floor.Dto.MinY,
            MaxY = floor.Dto.MaxY,
            Exposure = floor.Exposure == null
                ? null
                : new CaptureExposure
                {
                    Low = floor.Exposure.Low,
                    High = floor.Exposure.High,
                    Gamma = floor.Exposure.Gamma,
                },
        };

        /// <summary>
        /// The entry an EARLIER capture wrote for a floor this one could not take, when its picture
        /// is still on disk - and null when there is none to keep.
        ///
        /// Without this, one floor failing throws away every capture of that floor. A merged set is
        /// the work of several raids (the picture on disk is the best of all of them), the meta is
        /// rewritten from the floors this capture managed, and anything the new meta does not name is
        /// deleted as stale a few lines later. So a basement that comes back as one flat tone once -
        /// which is exactly what a dark interior does - would cost the player every capture of that
        /// basement, silently, while the log said the capture had been written.
        ///
        /// Safe to carry because a previous meta only exists at all when LoadPrevious accepted it,
        /// and that means the same extent, the same pixels per metre, the same floor levels and the
        /// same encoding: the entry describes the picture beside it as accurately today as when it
        /// was written.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor this capture could not take.</param>
        private static CaptureFloor Carried(Plan plan, FloorPlan floor)
        {
            if (plan.Previous?.Floors == null || floor?.Dto == null) return null;

            foreach (var stored in plan.Previous.Floors)
            {
                if (stored == null || stored.Level != floor.Dto.Level) continue;
                if (string.IsNullOrEmpty(stored.File)) return null;

                // The meta may only name a file that is there: one deleted by hand between two
                // captures would otherwise be named by this meta and draw as nothing.
                return File.Exists(Path.Combine(plan.Dir, stored.File)) ? stored : null;
            }

            return null;
        }

        /// <summary>Removes pictures left by an earlier capture of this map that the new meta does not
        /// name - a floor that has since merged into another, or a level that renumbered. Run only
        /// after the new meta is safely down, so a failure above never deletes a working set.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="keep">The file names the new meta accounts for - every named picture and its
        /// distance sidecar, which is NOT stale: it is what the next capture merges against, and the
        /// glob below matches it as well as the pictures.</param>
        private static void DropStalePictures(Plan plan, HashSet<string> keep)
        {
            try
            {
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

                    Add(labels, seen, plan, LabelKindExfil, text, exit.transform.position);
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

                    Add(labels, seen, plan, LabelKindZone, text, at);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the bot zone labels could not be read ({ex.Message}).");
            }

            return labels;
        }

        /// <param name="labels">The list being built.</param>
        /// <param name="seen">Texts already added, so one name is drawn once.</param>
        /// <param name="plan">The capture's plan, for the extent a label has to fall inside.</param>
        /// <param name="kind">"exfil" or "zone" - see <see cref="CaptureLabel.Kind"/>.</param>
        /// <param name="text">The label's text, already cleaned.</param>
        /// <param name="at">Where it belongs, in world space.</param>
        private static void Add(
            List<CaptureLabel> labels, HashSet<string> seen, Plan plan, string kind, string text, Vector3 at)
        {
            if (!seen.Add(text)) return;

            if (float.IsNaN(at.x) || float.IsNaN(at.z) || float.IsInfinity(at.x) || float.IsInfinity(at.z)) return;
            if (at.x < plan.Extent.MinX || at.x > plan.Extent.MaxX) return;
            if (at.z < plan.Extent.MinZ || at.z > plan.Extent.MaxZ) return;

            labels.Add(new CaptureLabel { Text = text, Kind = kind, X = at.x, Z = at.z });
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

        /// <summary>A linear light value for a log line. Four decimals and no exponent, because these
        /// numbers are mostly between 0.001 and 1 and are compared by eye against each other.</summary>
        /// <param name="v">The value.</param>
        private static string E(float v) =>
            IsFinite(v) ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "n/a";

        private static string G(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

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

            /// <summary>Where the player was standing when the key was pressed, in world XZ. Every
            /// pixel's distance from here goes into the sidecar, and that is what decides whether this
            /// capture's view of a spot beats the one already on disk.</summary>
            public Vector2 From;

            /// <summary>The meta of a capture of this map already on disk that this one may be merged
            /// into: same extent, same scale, same floors, same encoding. Null for a fresh capture -
            /// see <see cref="LoadPrevious"/>, which says in the log why when it refuses one.</summary>
            public CaptureMeta Previous;

            /// <summary>Which capture of this map this is: 1 for a fresh one, one more than the
            /// previous meta's count for a merge.</summary>
            public int Captures = 1;

            /// <summary>Set when a floor came back under light the pictures on disk were not
            /// developed for, which refuses the whole capture: nothing is committed, no meta is
            /// written, and the set on disk is left as it was. See MeasureFloor's light test.</summary>
            public bool Refused;

            /// <summary>When the FIRST capture of this set was taken, carried forward across merges;
            /// the meta's capturedAt is always the latest, because that is the field the server's
            /// "is this newer" rule reads.</summary>
            public string FirstCapturedAt;

            public readonly List<FloorPlan> Floors = new List<FloorPlan>();
            public List<CaptureLabel> Labels = new List<CaptureLabel>();
        }

        /// <summary>One floor's state while it is being captured.</summary>
        private sealed class FloorPlan
        {
            public MapFloorDto Dto;
            public string File;

            /// <summary>The floor's pixels as linear RGB floats, three per pixel, in texture order -
            /// row 0 is the bottom of the picture, world -z. Filled a tile at a time, read once by
            /// <see cref="Measure"/> and <see cref="Develop"/>, then dropped: it is 58 MB on a large floor.</summary>
            public float[] Pixels;

            /// <summary>The eight-bit picture, built by the exposure pass out of
            /// <see cref="Pixels"/>.</summary>
            public Texture2D Texture;

            /// <summary>Whether each pixel was DRAWN by this capture, one byte a pixel (4.8 MB on a
            /// large floor). A pixel the camera did not draw comes back as the clear colour, exactly
            /// zero in every channel including alpha, and that is what this records - the chunks the
            /// game had streamed out at that distance, which are the holes a second capture from
            /// somewhere else fills in.</summary>
            public bool[] Drawn;

            /// <summary>This capture's distance sidecar: for each pixel, how far it was from the
            /// player, in four-metre steps, or <see cref="DistanceEmpty"/> where nothing was drawn.
            /// Written beside the picture and read by the next capture.</summary>
            public byte[] Dist;

            /// <summary>The picture already on disk, and its distances, when this is a merge. Both are
            /// dropped as soon as the merge is done: 19 MB and 4.8 MB on a large floor.</summary>
            public Color32[] PreviousColour;

            public byte[] PreviousDist;

            /// <summary>The two buffers the development works from: one band's worth of finished
            /// pixels, reused for every band, and every column's squared X distance from the
            /// capturing player. Both are dropped by DevelopFinish.</summary>
            public Color32[] Block;

            public float[] DxSquared;

            public string DistFile;

            /// <summary>Whether the previous picture was merged into this one, and the three counts
            /// the log line reports: pixels this capture supplied, pixels kept from the previous
            /// capture because it saw them from closer, and pixels nothing has drawn yet.</summary>
            public bool Merged;

            public int Filled;
            public int Kept;
            public int StillEmpty;

            /// <summary>The lower edge of the band directly ABOVE this one, or NaN when this is the
            /// topmost band. It decides where the camera goes and therefore whether the picture has
            /// roofs in it - see <see cref="BeginFloor"/>.</summary>
            public float NextMinY = float.NaN;

            public ExposureResult Exposure;

            /// <summary>Whether the exposure came from the previous meta rather than from this
            /// capture's own pixels. It has to, on a merge: two captures may only be mixed byte for
            /// byte if identical light became identical bytes in both.</summary>
            public bool ReusedExposure;

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

            /// <summary>When the LATEST capture of this set was taken. The latest rather than the
            /// first because the server's "is this newer than what I hold" rule reads this field, and
            /// a set that has just gained a capture is newer.</summary>
            [JsonProperty("capturedAt")] public string CapturedAt { get; set; }

            /// <summary>When the first capture of this set was taken, which is what the credit line
            /// under the map means by the date. Absent in a meta written before merging existed, and
            /// then <see cref="CapturedAt"/> is the same thing.</summary>
            [JsonProperty("firstCapturedAt")] public string FirstCapturedAt { get; set; }

            /// <summary>How many captures have been merged into this set, 1 for a fresh one. The
            /// number a player watches go up while they fill in a map's holes.</summary>
            [JsonProperty("captures")] public int Captures { get; set; }

            [JsonProperty("modVersion")] public string ModVersion { get; set; }

            /// <summary>How the capture was LIT: "own-1.5" for the capture light this build adds at
            /// that intensity, and absent in a capture taken before it existed. Not decoration - it is
            /// what stops a picture taken under one lighting being merged pixel by pixel into one taken
            /// under another, which would seam the two together; see <see cref="LoadPrevious"/> and
            /// <see cref="CaptureLightIntensity"/>.</summary>
            [JsonProperty("lighting")] public string Lighting { get; set; }

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

            /// <summary>What the exposure pass did to this floor. PER FLOOR rather than per capture,
            /// because each floor is stretched to its own content - a basement and a rooftop share
            /// nothing about brightness - so one number for the whole map would describe none of
            /// them. Nothing reads it yet; it is here so a picture that came out too dark or too
            /// flat can be diagnosed from the file rather than from a log that has rolled over.</summary>
            [JsonProperty("exposure")] public CaptureExposure Exposure { get; set; }
        }

        /// <summary>The percentile stretch one floor was developed with: the linear luminance that
        /// became black, the one that became white, and the exponent applied afterwards (1/2.2 for
        /// linear data, 1 when the data was already display-encoded). See <see cref="Measure"/>.</summary>
        private sealed class CaptureExposure
        {
            [JsonProperty("low")] public float Low { get; set; }
            [JsonProperty("high")] public float High { get; set; }
            [JsonProperty("gamma")] public float Gamma { get; set; }
        }

        private sealed class CaptureLabel
        {
            [JsonProperty("text")] public string Text { get; set; }

            /// <summary>Where the label came from: "exfil" for an extraction point, "zone" for a bot
            /// zone's cleaned-up name. The Maps tab filters on it - a map with forty zone names on it
            /// is unreadable, and the extracts are the ones worth drawing by default - so the writer
            /// keeps producing both and the reader decides.</summary>
            [JsonProperty("kind")] public string Kind { get; set; }

            [JsonProperty("x")] public float X { get; set; }
            [JsonProperty("z")] public float Z { get; set; }
        }
    }
}
