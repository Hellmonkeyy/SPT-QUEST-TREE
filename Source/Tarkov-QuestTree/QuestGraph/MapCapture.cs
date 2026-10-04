using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

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
    ///     here; the darkness is the pipeline's raw linear output landing in an 8-bit target
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
    /// One fact about the path, since the paragraph above is written in terms of the game's: this
    /// camera is ORTHOGRAPHIC, and Unity's built-in pipeline does not do deferred shading for an
    /// orthographic projection - it falls back to forward, whatever DeferredShading the CopyFrom
    /// brought and whatever renderingPath reports. It changes none of the conclusions above (both
    /// paths sum lights, and the darkness and the half-float fix were both measured on this same
    /// orthographic camera), and it is why the capture's own light may carry a narrow culling mask and
    /// ForcePixel: in forward rendering both are honoured exactly, where deferred supports a culling
    /// mask for at most four layers and would draw artifacts for ours.
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
    /// TWO different things hide the far half of a map from this camera, and only one of them is a
    /// limitation:
    ///   - EFT's own distance culling (the DisablerCullingObject layer, and the terrain and building
    ///     chunks the game streams out) is measured from the PLAYER, not from our camera, and nothing
    ///     here can reach it. That is what walking somewhere else and pressing the key again covers -
    ///     it is why the merge exists and why it prefers the nearest view of each pixel.
    ///   - Unity's own LOD selection is measured from the RENDERING camera, and for an orthographic
    ///     one it is measured against <c>orthographicSize</c> rather than a distance: a 20 m building
    ///     is 2 % of a 1024 m tall tile, under the cull threshold of most of EFT's LOD groups, so the
    ///     whole object disappeared and the terrain and roads - which carry no LODGroup - were all
    ///     that came back. That produced the "no buildings anywhere, at any camera height" picture,
    ///     and it is fixed here rather than worked around: <see cref="RenderOnce"/> raises
    ///     <see cref="CaptureLodBias"/> for the one render and puts it back.
    ///
    /// Cost. Phase 0 timed a 2048 tile in a live raid at 10-61 ms to read back and 58-68 ms to
    /// encode, so the work is spread one step to a frame: each tile's render and readback, then, per
    /// floor, its exposure measurement, its development into eight bits - itself a step for the
    /// previous picture, one for that picture's sidecar and one per band of
    /// <see cref="SmoothingBandPixels"/> worth of rows, because the whole of it is a second and a half - its
    /// encode and write, and its sidecar. None of it can move off the main
    /// thread - ReadPixels, GetPixels, SetPixels32 and EncodeToPNG are all main-thread Texture2D
    /// calls - so a capture is a handful of short hitches on a key the player pressed, rather than
    /// one long freeze.
    ///
    /// WP4 moved two of those off the frame: the tile readback is asynchronous (a ring of resolve targets and
    /// AsyncGPUReadback, proven bit-identical to ReadPixels on the session's first tile), and the picture's and the
    /// sidecar's PNGs are encoded on workers by <see cref="PngEncoder"/> while the next floor renders, then staged on
    /// the main thread at a barrier (SettleEncodes, SettleSides). The files' pixels are the old ones; their bytes are
    /// not Unity's, so pictures are compared by pixels, not bytes. Unity's encoder remains the fallback and the
    /// rollback (ManagedPngEncode).
    ///
    /// Memory is BUDGETED, not hoped for. One floor may work in CaptureMemoryBudgetBytes - 1024 MiB - of
    /// arrays and textures, at WorkingSetBytesPerPixel (26 B a pixel: the float buffer, the drawn mask,
    /// two sets of distances, the picture, the sidecar texture and, on a merge, the previous picture),
    /// and the pixels per metre come down in half-metre steps until the floor fits. The figures below
    /// are of the harvested RECTANGLE, not of a map name - the rectangle is what the arithmetic sees,
    /// and a re-harvest moves it: a 965x925 m one (Interchange's, as this install measured it) at
    /// 8 px/m is 7720x7400 and 1417 MiB a floor, and it comes down through 7.5 and 7 px/m (1246 and
    /// 1085 MiB, still over) to 6.5 px/m, 6276x6016 and 936 MiB. A 1118x539 m one (Customs) at 8 px/m
    /// is 8944x4312 and 956 MiB and is not touched. (Every figure here is what the capture header
    /// prints: mebibytes, the way the code divides, of the sizes after PictureSide's rounding.) Why a
    /// budget at all: at 4 px/m and no budget, that Interchange rectangle's 354 MiB floor died in a raid
    /// - "GetPixels: scripting array creation failed" on its first floor and OutOfMemoryException on the
    /// other two.
    ///
    /// What is outside that budget and small: the 34 MB half-float staging texture (one per CAPTURE),
    /// two 32 KB sample rows, a band's worth of Color32 and the encoded PNG. What is no longer in it at
    /// all: the managed arrays GetPixels used to hand back - 16 bytes a pixel for a staging band, four
    /// for a whole decoded picture - which is what fragmented the heap in the first place. Every
    /// readback now goes through GetPixelData, a view of the texture's own memory.
    ///
    /// WP4: with the asynchronous tile readback (AsyncTileReadback) the staging texture is not allocated at all;
    /// in its place, and in the same outside-the-budget category, are ReadbackRing slots - each a 32 MiB
    /// single-sample resolve target (VRAM) and a 32 MiB native array (16 and 16 eight-bit), 96 + 96 MiB at 3 slots.
    /// The budget model is deliberately not changed: it decides the pixels per metre, and so every file. The
    /// session's first tile that can tell a correct readback from a flipped or offset one (Discriminates) proves the
    /// readback bit-identical to ReadPixels before it is trusted (VerifyNow); until then every tile is checked.
    ///
    /// Between floors, ReleaseTexture frees everything the floor held and GC.Collect runs once, a frame
    /// later so that Unity's deferred Destroy of the picture has happened first - the one place this mod
    /// collects by hand, because these are large-object-heap allocations and the next floor asks for the
    /// same sizes a frame later. After the LAST floor it does not run at all: nothing else is coming,
    /// and Cleanup drops the rest.
    ///
    /// That count is MANAGED memory. The tile target is video memory and is not in it: 2048 square at
    /// half-float is 34 MB, times the multisampling the device granted - up to 4, which is 134 MB plus
    /// 67 MB of depth, held for the length of the capture. See <see cref="MsaaLevels"/> for why it is
    /// four and not eight.
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

        /// <summary>Most pixels per metre, whatever the resolution setting allows. Eight - an eighth of
        /// a metre to the pixel. Rollback: 4f (the value until 1.19.0), which with the rest of the
        /// capture unchanged writes 0.25 m/px pictures again.
        ///
        /// Two was the first value, and the campaign capture of Customs at half a metre to the pixel is
        /// what argued it up to four: the buildings, vehicles and trees the LOD fix brought back are
        /// read at ten to forty pixels across at 0.5 m/px, which is enough to see that a warehouse is
        /// there and not enough to tell one door from the next. At 0.25 m/px a 4 m vehicle is 16 px.
        ///
        /// Eight because of what the picture is now FOR. Four was judged by the scene's detail - the
        /// terrain base map and the LOD meshes run out not far past it - but the picture is the one
        /// part of a capture that carries the game's own light (its sun and shadows, graded), and the
        /// 3D view draws roofs and ground from it at close zoom, where 0.25 m/px is visibly blocky; the
        /// 3D view only takes roofs from a picture of at least 8 px/m (Map3DView.RoofPictureMinPpm).
        /// The cost is four times the pixels, which is what the larger CaptureMemoryBudgetBytes,
        /// MaxFloorPngBytes, EncodeWaitSeconds and FloorPhaseSeconds pay for; the viewer holds it
        /// block-compressed (DynamicMapsLibrary), which is what PictureSide's multiple of four is for.
        ///
        /// It is a CAP, not a target: it binds only where the long-side setting does not, which is
        /// every map under about 2 km across at the 16384 setting, and it is what stops Factory's
        /// two-hundred-metre extent asking for eighty pixels per metre. A saved setting of 8192 still
        /// binds first on anything over a kilometre: Customs is then 7.32 px/m (8188 / 1118 m).
        ///
        /// Tile time per campaign stop goes up about 4x with it, so the first Customs campaign at 8 px/m
        /// has to be timed against the raid timer.</summary>
        private const float MaxPixelsPerMetre = 8f;

        /// <summary>Whether a picture's width and height are rounded UP to a multiple of
        /// <see cref="PictureBlock"/> (see <see cref="PictureSide"/>), the floor's extent widened east and
        /// south by the added pixels so a pixel is still exactly 1/ppm metres. Rollback: false, which is
        /// ceil(metres x ppm) and the harvested extent as they were - the only setting under which a
        /// capture from before it can still be merged into. Static readonly, not const, for
        /// FillWaterCyan's reason.</summary>
        private static readonly bool AlignPictureSides = true;

        /// <summary>What a picture's sides are rounded to: 4, the block of DXT1/DXT5. The viewer
        /// (DynamicMapsLibrary.BuildRasterSprite) block-compresses a picture only when both sides are
        /// multiples of four - a quarter of the GPU memory of RGBA32 - and keeps anything else RGBA32,
        /// which at 8 px/m is 154 MB for Customs' ground alone.</summary>
        private const int PictureBlock = 4;

        /// <summary>Whether the cyan water quads are painted out. The off switch for the whole step -
        /// <see cref="Inpaint"/> and the classifier below - for the case where a map's real content is
        /// being eaten by it. It is part of <see cref="RenderTag"/>, so turning it off replaces older
        /// captures rather than merging into them.
        ///
        /// Static readonly rather than const, like SmoothingEnabled below and for the same reason: a
        /// const folds the branches and the compiler then reports the off-path as unreachable code,
        /// which this project treats as an error. A switch nobody can flip without a build error is
        /// not a switch.</summary>
        private static readonly bool FillWaterCyan = true;

        /// <summary>How a water quad is recognised, in RAW linear light, before any stretch: both green
        /// and blue over <see cref="WaterCyanChannelFloor"/>, with red under
        /// <see cref="WaterCyanRedShare"/> of the smaller of them.
        ///
        /// Puddles and pools after rain draw through a water shader that has nothing to reflect from a
        /// camera that is not the player's, and what it writes instead is a flat, strongly saturated
        /// cyan - the last visible fault in the Customs campaign capture. The Water LAYER is already
        /// excluded (see ExcludedLayerNames); these quads are on ordinary layers and cannot be dropped
        /// by mask.
        ///
        /// The thresholds are read against what the rest of a map measures. A capture's 98th percentile
        /// comes in around 0.25-0.5 of linear white, so 0.45 in BOTH green and blue is already at the
        /// bright end of anything real; and the red test is what makes it a water test rather than a
        /// brightness test - a lit roof or a white van is bright in all three channels and keeps its
        /// red, while these quads have almost none. Grass is green without being blue, rust is red,
        /// the sky is not drawn. Nothing else on a map is strongly cyan.</summary>
        private const float WaterCyanChannelFloor = 0.45f;

        private const float WaterCyanRedShare = 0.35f;

        /// <summary>Square windows the inpainting tries, in pixels a side, smallest first: the mean of
        /// the non-cyan drawn pixels in the first one that holds any replaces the quad. 5 keeps a
        /// puddle's edge looking like the ground it is in, 17 reached across the biggest pool on
        /// Customs at four pixels to the metre, and 33 is the same two metres at eight (17 alone would
        /// reach only a metre there); a pixel with nothing but cyan and holes within 33 px is marked
        /// undrawn and left for another capture to fill. Rollback: { 5, 9, 17 }. InpaintReach (the
        /// tile-skip halo of a fill) follows the largest, 16 px.</summary>
        private static readonly int[] InpaintWindows = { 5, 9, 17, 33 };

        /// <summary>Cyan pixels repainted per frame. Each is up to 1,089 samples (the 33 window) of the float buffer, so
        /// twenty thousand of them is about the same work as one band of development - the unit the
        /// frame budget is built in.</summary>
        private const int InpaintChunkPixels = 20000;

        /// <summary>How many samples a side each output pixel is rendered from: 2, so every pixel is
        /// the average of four. The tile target stays 2048 and covers half the metres it did, which
        /// quadruples the tile count - Customs is 5x3 tiles at 4 px/m (256 m a tile) and 9x5 at 8 px/m
        /// (128 m a tile) - and leaves the float buffer,
        /// the drawn mask, the distances and everything downstream at the output resolution.
        ///
        /// It is here because of what a single sample per pixel looks like on a photographed map at a
        /// quarter of a metre to the pixel (where it was judged; an eighth is no different): every railing, wire, roof edge and tree trunk is a hard
        /// staircase, and no amount of smoothing afterwards can recover the coverage information that
        /// one sample never had. Supersampling is the only antialiasing that works on everything -
        /// geometry edges, alpha-tested foliage and texture detail alike - and unlike MSAA it does not
        /// depend on the rendering path.
        ///
        /// A pixel counts as drawn when ANY of its four samples was drawn, and its value is the mean of
        /// the DRAWN samples only, so a pixel half covered by a roof edge is the roof's colour rather
        /// than the roof mixed with the clear colour.</summary>
        private const int SupersampleFactor = 2;

        /// <summary>Samples of multisampling asked of the tile target, best first. Hardware MSAA is
        /// free-ish antialiasing on top of the supersampling above, and the two work on different
        /// things: MSAA resolves the coverage of a polygon edge inside one sample, supersampling
        /// resolves everything at four times the sample count.
        ///
        /// Asked for rather than assumed: a graphics device may refuse 8 on a half-float target, and
        /// the fallbacks are what keeps the capture working rather than failing. The value actually
        /// achieved goes in the capture header and into <see cref="RenderTag"/>, because two machines
        /// that resolved differently did not produce the same picture and must not merge into one
        /// another.
        ///
        /// The samples are USED, not merely allocated: <see cref="BuildTarget"/> sets the camera's own
        /// allowMSAA from what the target was granted, which is Unity's switch for it. The path is forward
        /// - an orthographic camera never gets deferred shading, whatever the CopyFrom brought, see the
        /// class doc - so multisampling applies here in a way it would not on a deferred camera.
        ///
        /// Four rather than eight because of what it costs in video memory, on a machine that is also
        /// running a raid: a 2048 half-float target at 8 samples is 268 MB of colour plus 134 MB of
        /// depth, four hundred megabytes for a tile. At 4 it is half that, and the difference between
        /// four and eight samples on geometry that is ALSO being supersampled two by two is not
        /// something anybody will find in the picture.</summary>
        private static readonly int[] MsaaLevels = { 4, 2, 1 };

        /// <summary>Shade of the flat reflection environment the capture renders against, as linear
        /// light, and how strongly it is reflected.
        ///
        /// One kind of blue-grey sheet came from here. The block by the warehouse yard and the basin at
        /// the fuel tanks are not water at all: they are reflective roof and metal materials reflecting
        /// the SKY, and our camera has no reflection environment of its own, so they sample whatever the
        /// scene default is and come back as a flat sheet of sky seen from above. The water pass below
        /// correctly left them alone.
        ///
        /// The fix is a reflection environment with no sky in it: a tiny cubemap of one neutral grey, at
        /// a modest intensity, so a reflective surface reads as the metal it is. 0.35 of linear white is
        /// the middle of the range these captures work in, and 0.6 keeps the reflection present - a wet
        /// roof should still look wet - without letting it dominate the surface own colour.
        ///
        /// Baked ReflectionProbe components are deliberately untouched: an interior with a probe in it
        /// reflects its own room, which is correct, and switching probes off would darken every interior
        /// the map has.</summary>
        private const float ReflectionGrey = 0.35f;

        private const float ReflectionIntensity = 0.6f;

        /// <summary>Side of the reflection cubemap, in pixels. Sixteen: it holds one colour, and the only
        /// reason not to make it 1 is that some drivers dislike a one-pixel cubemap with mips off.</summary>
        private const int ReflectionCubeSize = 16;

        /// <summary>The colour real water is painted in the capture - a muted map blue, as a display
        /// colour, which Unity converts to linear for the shader.
        ///
        /// This replaced hiding the water renderers, and the reason is the river. Switching them off took
        /// the Customs river out of the picture and left its bed showing, which is worse than a flat
        /// sheet: a map of Customs without its river is a map missing a landmark. So the renderers stay
        /// on and their materials are swapped for one flat unlit blue for the render - the water is drawn,
        /// in a colour that reads as water on a map, and nothing reflects the sky through it.</summary>
        private static readonly Color WaterPaint = new Color(0.30f, 0.50f, 0.68f, 1f);

        /// <summary>Shaders the flat water material is built from, in order: the first that this build has
        /// wins. Unlit/Color takes a colour directly; Unlit/Texture has no colour property, so it is given
        /// a one-pixel texture of the colour instead; Legacy Shaders/Diffuse is the last resort and is lit
        /// rather than unlit, which for a flat blue at this scale is a difference nobody will see.</summary>
        private static readonly string[] WaterShaders = { "Unlit/Color", "Unlit/Texture", "Legacy Shaders/Diffuse" };

        /// <summary>Which generation of the water treatment a capture was rendered with, for the render
        /// tag, bumped by hand because a picture with a blue river in it must not be merged pixel by
        /// pixel into one with a dry riverbed.
        ///
        /// 1 did nothing. 2 hid every renderer whose shader was named like water, which took the Customs
        /// river out of the picture and left its bed showing. 3 painted those same renderers flat blue,
        /// which put blue slabs over every yard, the bridge deck and several interior floors, because
        /// most of them were wet-surface decals rather than water. 4 paints the renderers on the WATER
        /// LAYER and nothing else, and leaves the decals to the neutral reflection and the cyan inpaint -
        /// see WaterLayerName.</summary>
        private const int WaterPassVersion = 4;

        /// <summary>Whether the per-player distance culling is forced visible while a tile renders.
        ///
        /// It has to be. The campaign capture of Customs has buildings - the boiler room, Big Red,
        /// several warehouses - drawn as a patch of ground with a black wall outline around it, because
        /// EFT hides distant geometry by DISABLING the renderers rather than by letting the camera cull
        /// them: layer 14 is "DisablerCullingObject", and a DisablerCullingObject holds a list of
        /// components it switches off whenever no player is inside its collider. Every stop of a
        /// campaign is outside most of those colliders, so the roof and the upper walls were off in
        /// every capture while the floor - which belongs to no culling object - was drawn. The merge
        /// cannot see that as a hole: a drawn floor is a drawn pixel.
        ///
        /// The game's own ForceEnable is no use here, because SetComponentsEnabled starts a coroutine
        /// that switches twenty-five components a frame (CustomCullingCommon.SetComponentsEnabledWorker),
        /// and a tile is rendered inside one frame. So the components are switched directly, with
        /// ComponentExtensions.SetEnabledUniversal - the very call the game's own worker makes.
        ///
        /// Held for a FLOOR, not for a render, and that is a measurement rather than a preference: a
        /// campaign stop on Customs flattens some twenty-seven THOUSAND components out of the culling
        /// objects, and the game's own worker considers twenty-five of these switches a frame's worth of
        /// work. Two full passes per tile - one to force them on, one to put them back - is then a
        /// hitch of its own on top of the render and the readback, twenty-four times over on a
        /// six-by-four floor. Taking the hold once before a floor's first tile and releasing it after
        /// its last is the same two passes for the whole floor.
        ///
        /// The trade, stated because it is real: for the second or so a floor takes, the player's own
        /// frames draw the distant geometry too (slower frames, and pop-in that undoes itself), and any
        /// water on the water layer is a flat blue sheet in them - the hold swaps its material rather than
        /// hiding it, see HoldWater. A player who walks INTO a culling collider during the hold keeps
        /// the roof over their head: the release asks each culler's own HasEntered before it switches
        /// anything back off, and then has the cullers it touched re-apply their own state (review F07).
        ///
        /// Two limitations of the GameObject half of the hold, stated because neither is obvious from the
        /// code and both would look like a bug in a picture:
        ///
        /// 1. No ancestor climb. A culled object is switched on with SetActive(true), which sets its OWN
        ///    activeSelf - and an object whose PARENT is inactive stays inactive in the scene however
        ///    active it is itself. The culling lists hold the objects the game's own ForceEnable switches,
        ///    so this matches what the game does, but geometry parented under something else's disabled
        ///    root cannot be brought back this way and will still be missing from the capture.
        /// 2. The pre-state is read at hold time, not at collect time. HoldScene reads activeSelf as it
        ///    switches each object and ReleaseScene puts back exactly that, so anything the GAME switched
        ///    between two floors is honoured. What is not honoured is a change made while the hold is on:
        ///    the release puts the object back to what it was when the floor started, unless its culler
        ///    says the player is inside it - and the culler's own ForceUpdate corrects the rest (review F07).</summary>
        private static readonly bool ForceCulling = true;

        /// <summary>The layer real water bodies live on. Layer 4 is Unity's own "Water", and EFT uses
        /// it for what it is: the river down the middle of Customs, the ponds, the sea.
        ///
        /// It is the LAYER and not the shader name, and that distinction is the whole of this pass. A
        /// shader-name search for "water" or "puddle" matched 256 renderers on Customs, almost all of
        /// them wet-surface DECALS - the sheen over a yard, the bridge deck, an interior floor - and
        /// painting those flat blue put slabs of blue all over the map. What is on layer 4 is a water
        /// body; what merely has a wet-looking shader is a surface that should be left to draw
        /// itself.</summary>
        private const string WaterLayerName = "Water";

        /// <summary>Whether the water-layer renderers are painted flat at all.</summary>
        private static readonly bool PaintWater = true;

        /// <summary>Whether the walkable-area mask is baked into the picture.</summary>
        private static readonly bool ReachEnabled = true;

        /// <summary>Side of one cell of the walkable mask, in metres. Two: the NavMesh is what a bot can
        /// stand on, its triangles are metres across, and a two-metre grid over a kilometre of map is
        /// 150 thousand cells - nothing to build and nothing to hold.</summary>
        private const float ReachCellMetres = 2f;

        /// <summary>How far the mask is grown outward from the walkable area before anything is cut away.
        /// Eight metres, because the NavMesh is not the map: it stops at every wall, under every
        /// staircase and short of every railing, and a player standing on a catwalk or shooting across a
        /// yard is looking at ground no bot can walk on. Eight metres is wide enough that no roof, no
        /// interior and no yard is dimmed, and narrow enough that the edge of the world still is.</summary>
        private const float ReachDilateMetres = 8f;

        /// <summary>Metres over which the dimming fades in past the dilated edge, so the boundary reads
        /// as a soft vignette rather than as a drawn line somebody might mistake for a wall.</summary>
        private const float ReachRampMetres = 6f;

        /// <summary>Rollback for the reach discs: false marks the walkable mask from NavMesh triangles alone, as
        /// before. See <see cref="MarkReachDiscs"/>.
        ///
        /// Why they exist: the VANILLA NavMesh does not reach every place a player spawns. Interchange's ends at
        /// z 280.3 while 10 of its spawn markers stand at z 333..385, and Streets has the same pattern (36 of 503).
        /// DrakiaXYZ-Waypoints' NavMesh reaches them - a raid with Waypoints runs on it, and the menu host loads it too
        /// when it is installed - so with it the discs add almost nothing (their log line says how much), and without
        /// it they are the fallback: MapExtentProbe grows the extent to hold such markers, and a mask from triangles
        /// alone would leave the strip it grew into transparent.
        ///
        /// Spawn markers only, not exfiltration points (review): an exit is often a road or a gate leading OFF the map,
        /// past the walkable world, and a 40 m disc there redraws exactly the hillside the mask exists to remove. A
        /// spawn marker is by definition where a player stands inside the map.</summary>
        private static readonly bool ReachDiscs = true;

        /// <summary>Radius, in metres, of the ground a spawn marker makes reachable: the mask is
        /// fully opaque out to this distance and fades over <see cref="ReachRampMetres"/> past it, the same ramp the
        /// NavMesh edge gets. Forty metres: a player who spawns somewhere sees and walks the yard around it, and the
        /// furthest Interchange marker lies 105 m past the NavMesh, so a smaller disc would draw islands rather than
        /// the strip.</summary>
        private const float ReachDiscMetres = 40f;

        /// <summary>What the mask DOES to a pixel outside the walkable area: nothing at all is drawn
        /// there. The weight becomes the picture's ALPHA - 255 inside, falling to 0 over the ramp - so the
        /// out-of-bounds skirt is transparent and the Maps tab shows its own backdrop through it.
        ///
        /// It was a 45 % darken and a half desaturation first, and the user's answer to seeing it was
        /// that the area should be gone rather than dimmed: a dimmed hillside is still a hillside
        /// somebody will try to walk to. Removing it also solves the holes for free - a chunk the game
        /// had streamed out is transparent too, which reads as "no picture here" instead of as a black
        /// building.
        ///
        /// Nothing else changes: the merge, the sidecar, the drawn mask and the exposure all work on the
        /// same numbers they did, and the meta gains no field.</summary>
        private const bool ReachIsAlpha = true;

        /// <summary>Whether the despeckle pass runs. Static readonly, not const, for the same reason as
        /// the two switches below it.</summary>
        private static readonly bool DespeckleEnabled = true;

        /// <summary>How far a pixel's stretched luminance must sit from the median of its eight drawn
        /// neighbours before it is treated as a speckle: a quarter of the picture's whole tonal range,
        /// which nothing that is part of a surface ever does.
        ///
        /// It exists because a bilateral filter cannot remove an isolated outlier - it reads one as an
        /// EDGE and preserves it, which is the whole reason a separate pass is needed after it. What is
        /// left after the smoothing is single bright or black pixels: specular glints on wet metal, a
        /// lamp seen end-on, one sample of sky through a gap in a roof.</summary>
        private const float DespeckleThreshold = 0.25f;

        /// <summary>How much the eight neighbours may disagree among themselves and still count as a
        /// surface the odd pixel out does not belong to. 0.12: at a real edge the neighbours straddle
        /// it and spread far wider than this, so the pass leaves edges, corners and thin lines alone
        /// and only touches a pixel its whole surroundings agree about.</summary>
        private const float DespeckleNeighbourSpread = 0.12f;

        /// <summary>How many of the eight neighbours must have been drawn before the test is allowed to
        /// judge. Five: at the edge of a hole or of the picture there is not enough around a pixel to
        /// call it an outlier, and guessing there would eat the real content at every border.</summary>
        private const int DespeckleMinNeighbours = 5;

        /// <summary>Whether the edge-preserving smoothing runs. Part of <see cref="RenderTag"/>, so
        /// turning it off replaces older captures rather than merging into them. Static readonly, not
        /// const, so both paths stay compiled - see FillWaterCyan.</summary>
        private static readonly bool SmoothingEnabled = true;

        /// <summary>Reach of the smoothing kernel in pixels: 2, a 5x5 window. What it is for is the
        /// speckle left in a photographed map - terrain detail textures that tile every couple of
        /// metres, foliage billboards, the dither in a half-float readback - none of which is
        /// information about the map, all of which survives a percentile stretch. A 5x5 window is a
        /// metre and a quarter across at 0.25 m/px and 0.625 m at 0.125 m/px (MaxPixelsPerMetre 8),
        /// smaller than anything on a map that matters and still bigger than the speckle.</summary>
        private const int SmoothingRadius = 2;

        /// <summary>Spread of the spatial weights, in pixels. 1.2 puts the corner of a 5x5 window at
        /// about a sixth of the centre's weight - a soft kernel rather than a box blur, so the result
        /// looks photographic rather than posterised.</summary>
        private const float SmoothingSigmaSpatial = 1.2f;

        /// <summary>Spread of the RANGE weight, in stretched units - the same 0..1 scale the grade
        /// works in, which is what makes the strength of the filter independent of the map's exposure.
        /// 0.06 is about a sixteenth of the picture's tonal range: two pixels that differ by less than
        /// that are the same surface and are averaged together, and anything sharper - a roof edge, a
        /// road margin, a wall's shadow - is left alone. That is the whole point of a bilateral filter
        /// over a blur, and it is why the buildings the LOD fix brought back survive this pass.</summary>
        private const float SmoothingSigmaRange = 0.06f;

        /// <summary>How many sigmas of luminance difference are worth computing. Past four the weight
        /// is under e^-8, and skipping those neighbours outright is what makes the filter affordable:
        /// at a real edge most of the window is skipped.</summary>
        private const float SmoothingRangeReach = 4f;

        /// <summary>Entries in the range-weight table. The filter needs one exp() per neighbour; a
        /// 256-entry table over the reach above replaces all of them with an index, which was worth
        /// about a third of the pass's time when measured.</summary>
        private const int SmoothingRangeSteps = 256;

        /// <summary>Rows of the picture developed in one frame when the smoothing is on, against
        /// <see cref="PixelBandRows"/> when it is off.
        ///
        /// Sixteen, from a measurement rather than a guess. The exact inner loop over a 4472x2156 floor
        /// (Customs at 0.25 m/px, 9.6 million pixels, 12 % of them holes) was timed at 1748 ms on a warm
        /// .NET 9 JIT, 0.81 ms a row: 32 rows is 26 ms there, and Mono in a raid put the same band at
        /// 40-80 ms. Every pixel then gained a reach sample and a despeckle window on top of that
        /// measurement, so 32 rows is now over the 40 ms that a capture's other steps are budgeted
        /// against - hence half of it, 13-20 ms on .NET and 20-45 ms in a raid, which is the same order
        /// as the tile readbacks Phase 0 measured at 10-61 ms. Those are 4 px/m figures: at 8 px/m a row
        /// is twice as wide (Customs 8944 px), so a band of sixteen is roughly twice the time - not yet
        /// measured in a raid.
        ///
        /// The band is a WORK SPLIT and nothing else: the luminance buffer carries the filter's halo
        /// either side (<see cref="FillLuminance"/>) and the smoothing reads the whole float buffer, so
        /// the picture is byte for byte the same at any band size. It costs a hundred and thirty-five
        /// frames instead of sixty-eight on a map that size, which is another second of wall clock and
        /// a shorter hitch in each of them.
        ///
        /// Counted in PIXELS, not rows (rollback: a fixed 16 rows): 16 x 4472 is what sixteen rows cost
        /// on Customs at 4 px/m, the band the measurement above was taken on. At 8 px/m a Customs row is
        /// 8944 px, so the same work is 8 rows (<see cref="DevelopBandRows"/>), and a wider or narrower
        /// picture gets a band of the same cost rather than the same height.</summary>
        private const int SmoothingBandPixels = 16 * 4472;

        /// <summary>The spatial weights, built once: (2r+1)^2 of them, indexed row-major from the
        /// window's top-left.</summary>
        private static readonly float[] SmoothingKernel = BuildSmoothingKernel();

        /// <summary>The range weights, built once, indexed by luminance difference scaled by
        /// <see cref="SmoothingRangeScale"/>.</summary>
        private static readonly float[] SmoothingRangeWeights = BuildSmoothingRangeWeights();

        /// <summary>The luminance difference past which a neighbour is skipped.</summary>
        private const float SmoothingRangeCut = SmoothingRangeReach * SmoothingSigmaRange;

        private const float SmoothingRangeScale = (SmoothingRangeSteps - 1) / SmoothingRangeCut;

        /// <summary>Rows developed in one frame: fewer when every pixel costs a 5x5 window - as many as
        /// fit <see cref="SmoothingBandPixels"/> across this picture's width, never under 4.</summary>
        /// <param name="plan">The floor's or side's plan, for its width.</param>
        private static int DevelopBandRows(Plan plan) =>
            SmoothingEnabled ? Math.Max(4, SmoothingBandPixels / Math.Max(1, plan.WidthPx)) : PixelBandRows;

        /// <summary>WP1: the rollback switch for tile skipping. Off = exactly the f1aa04f path: the previous picture
        /// loaded in Develop, every tile rendered, the scene held for every floor. Static readonly, not const - see
        /// FillWaterCyan for why a const switch cannot be flipped.</summary>
        private static readonly bool TileSkipEnabled = true;

        /// <summary>WP1: whether a tile whose only takeable pixels are transparent either way (walkable mask 0 AND
        /// alpha 0 on disk) is skipped too. The one class that is NOT byte-identical: the RGB under alpha 0 and the
        /// sidecar at those pixels keep the older values. Merge only. Off: only the byte-identical OwnedByCloser
        /// class ships (the maintainer's decision, PART-00 section 5 item 4).</summary>
        private static readonly bool TileSkipOutsideMask = false;

        /// <summary>WP1: shadow mode for verification - decide every verdict, render EVERY tile anyway (the old
        /// output), and count what the skip would have changed: see BuildSkipZone, AuditLight and the audit line in
        /// FinishFloor / FinishSide. Off in a release.</summary>
        private static readonly bool TileSkipAudit = false;

        /// <summary>Campaign speed step 1 (1): a floor that rendered no tile - every one owned by a closer capture or outside
        /// the mask - keeps its picture and distance sidecar on disk as they are: no Develop, no encode, no write. With no
        /// tile rendered the development only copies the previous picture and sidecar pixel for pixel (DevelopBand's
        /// not-taken branch), so the files a rewrite would stage hold the same pixels; skipping it saved ~12 s a Customs
        /// stop (1.3 s develop, 10.5 s encode and 67.6 MB write). Off (rollback): the floor is developed, encoded and
        /// rewritten as before.</summary>
        private static readonly bool SkipUnchangedFloors = true;

        /// <summary>Campaign speed step 1 (2): a side view whose tile plan leaves nothing to render keeps its picture and
        /// sidecar on disk (CommitSides carries its entry): no tiles, no develop, no encode, no write - the side's share of
        /// the 15.8 s a Customs stop spent on sides. Off (rollback): every side is developed, encoded and rewritten.</summary>
        private static readonly bool SkipUnchangedSides = true;

        /// <summary>Campaign speed step 1 (2): a side pixel inside a tile this capture RENDERED that came back with nothing
        /// drawn, and that lies ABOVE the scene's collider skyline (<see cref="BuildSkyline"/>) by
        /// <see cref="SkylineMarginPx"/>, records in the sidecar the step it was seen empty from - as a drawn pixel records
        /// the step it was seen from - so it is settled for every later capture that is no closer. Without it the top row
        /// of every side (sky: N/S tiles 0-4, E/W tiles 0-2 on Customs) stayed 255 "never seen" for ever,
        /// CaptureMerge.Takes said any drawn pixel there would be taken, and TileWorth rendered those tiles at every stop.
        /// Only above the skyline (review): a side pixel's step is the distance to its ray's ground point, not to what it
        /// shows, so an empty pixel over a building streamed out at this stop, settled, would have refused that building to
        /// a later stop farther away that had it loaded - lost for good. Colliders never stream out, so nothing solid can
        /// be above their skyline. At or below it an empty pixel keeps 255 and is retried as before; a pixel settled there
        /// by the first version of this rule is healed on load (HealSideDist). The colour stays transparent black, so
        /// check-capture's "255 means RGB 0" invariant still holds. Sides only (no walkable mask, alpha 255 exactly where
        /// drawn). Off (rollback): nothing is settled, and every transparent side pixel with a step is read as 255.</summary>
        private static readonly bool SideSettleEmpty = true;

        /// <summary>Campaign speed step 1 (2, review): texture rows above the collider skyline a pixel must be to be settled -
        /// room for what the relief grid's half-metre cells and a mesh drawn a little proud of its collider can hide.</summary>
        private const int SkylineMarginPx = 8;

        /// <summary>Campaign speed step 1 (2, re-review): metres of height, projected into the side's picture (x ppm x |u.y|,
        /// ~85 px at 4 px/m), above the collider skyline that can still hold something with NO collider - tree canopies,
        /// wires - which is exactly what distance-culls at the map's edges. An empty pixel above skyline + this is settled
        /// at once; one in the band between is settled only after it was rendered empty at <see cref="SettleEmptyStops"/>
        /// stops of the SAME campaign (<see cref="SideEmptyCounts"/>), and never outside a campaign.</summary>
        private const float SkylineFoliageMetres = 30f;

        /// <summary>Campaign speed step 1 (2, re-review): stops of one campaign a pixel in the foliage band must be rendered
        /// empty at before it is settled.</summary>
        private const int SettleEmptyStops = 2;

        /// <summary>Campaign speed step 1 (2, re-review): one side's per-pixel count of the running campaign's stops that
        /// rendered it empty (saturating), with the side's geometry it counts for.</summary>
        private sealed class EmptyCounts
        {
            public string Geometry;
            public byte[] Counts;
        }

        /// <summary>Campaign speed step 1 (2, re-review): the running campaign's counts, per map and side, and the session they
        /// belong to - about 32 MB on Customs; dropped by CampaignBegins and CampaignEnds, and never kept outside a
        /// campaign.</summary>
        private static readonly Dictionary<string, EmptyCounts> _sideEmptyCounts =
            new Dictionary<string, EmptyCounts>(StringComparer.OrdinalIgnoreCase);

        private static int _sideEmptyCountsSession;

        /// <summary>Campaign speed step 1 (2, re-review): the running campaign's empty counts for one side, made (zeroed) when
        /// the side is first met or its geometry changed; null outside a campaign.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side.</param>
        private static byte[] SideEmptyCounts(Plan plan, SideView view)
        {
            var live = LiveCampaignSession;
            if (live == 0 || view?.Plan == null || view.Frame == null) return null;

            if (_sideEmptyCountsSession != live)
            {
                _sideEmptyCounts.Clear();
                _sideEmptyCountsSession = live;
            }

            var side = view.Plan;
            var inv = CultureInfo.InvariantCulture;
            var geometry = $"{side.WidthPx.ToString(inv)}x{side.HeightPx.ToString(inv)}|{MapMeshIndex.Bits(side.Ppm).ToString(inv)}|" +
                           $"{MapMeshIndex.Bits(view.Frame[0]).ToString(inv)}|{MapMeshIndex.Bits(view.Frame[2]).ToString(inv)}";
            var id = plan.Key + "|" + view.Dir;
            var length = side.WidthPx * side.HeightPx;

            if (!_sideEmptyCounts.TryGetValue(id, out var held) || held.Geometry != geometry || held.Counts == null ||
                held.Counts.Length != length)
            {
                held = new EmptyCounts { Geometry = geometry, Counts = new byte[length] };
                _sideEmptyCounts[id] = held;
            }

            return held.Counts;
        }

        /// <summary>Campaign speed step 1 (2, re-review): the counts let go - a campaign starting or ending.</summary>
        private static void ForgetEmptyCounts()
        {
            _sideEmptyCounts.Clear();
            _sideEmptyCountsSession = 0;
        }

        /// <summary>WP1: metres taken off a tile's closed-form minimum distance before it becomes a step, so the
        /// per-tile bound is a lower bound of DevelopBand's float arithmetic whatever the rounding.</summary>
        private const double TileSkipSlackMetres = 0.05d;

        /// <summary>WP1: main-thread milliseconds of tile planning per frame before it yields.</summary>
        private const double TilePlanSliceMs = 8d;

        /// <summary>WP1: pixels around a tile the development reads for a pixel it TAKES - the bilateral window and
        /// the despeckle's neighbours. A taken pixel farther than this from an unrendered tile cannot see it.</summary>
        private static int TileSkipHalo =>
            Math.Max(SmoothingEnabled ? SmoothingRadius : 0, DespeckleEnabled ? 1 : 0);

        /// <summary>WP1: how far one water pixel's fill reads - half the largest inpaint window (Mean).</summary>
        private static readonly int InpaintReach = InpaintWindows.Max() / 2;

        /// <summary>WP1: what the tile loop does with one tile.</summary>
        private enum TileVerdict : byte
        {
            /// <summary>Rendered.</summary>
            Render = 0,

            /// <summary>No pixel of the tile or its halo could be taken by CaptureMerge.Takes.</summary>
            OwnedByCloser = 1,

            /// <summary>Pixels could be taken, but every one is transparent in the result either way.</summary>
            OutsideMask = 2,
        }

        /// <summary>Most managed and texture memory one floor of a capture may work in. 1024 MiB.
        /// Rollback: 256 MiB, the value with MaxPixelsPerMetre 4.
        ///
        /// Interchange is why it exists - or rather its harvested RECTANGLE is, which is what the
        /// arithmetic below sees and what a re-harvest can move: 965x925 m as this install measured it.
        /// Three floors of that at 4 px/m is 3860x3700 = 14.3 million pixels each, and at
        /// <see cref="WorkingSetBytesPerPixel"/> that is 354 MB a floor - so the first floor died with
        /// "GetPixels: scripting array creation failed, array size or length is too large" at tile
        /// 12 of 16, and the second and third with OutOfMemoryException. Nothing was written.
        ///
        /// The number is not the machine's memory, it is what MONO will hand out in one piece. Every
        /// allocation here is far over the 85 KB that puts an array on the large-object heap, and the LOH
        /// is not compacted - so the failure was fragmentation rather than exhaustion.
        ///
        /// It is 256 and not the 160 first written, because the two halves of that failure were fixed
        /// separately. The CHURN - a 16-bytes-a-pixel managed array per staging band, 128 of them a
        /// floor, plus a whole decoded picture per merge - is gone entirely: every readback now goes
        /// through GetPixelData, which is a view of the texture's own memory (see ReadSampleRow,
        /// CopyColours). What is left for this number to guard is the PEAK alone, a handful of long-lived
        /// arrays allocated once a floor and freed with a collect between floors, and that is a far
        /// easier thing for an allocator to place. At 256 MiB and 4 px/m a 1118x539 m rectangle (Customs)
        /// kept its 4 px/m and its 239 MiB, and a 965x925 m one (Interchange) landed at 3 px/m and 199 MiB.
        ///
        /// It is 1024 because MaxPixelsPerMetre went from 4 to 8, four times the pixels: Customs at 8 px/m
        /// is 8944x4312 and 956 MiB and is not touched, and Interchange walks 8, 7.5 and 7 px/m (1417,
        /// 1246, 1085 MiB) and lands at 6.5 px/m and 936 MiB. The largest single allocation is the float
        /// buffer, 12 of the 26 bytes - 463 MB on Customs - which the large-object heap has to place in
        /// one piece; an OutOfMemoryException there is caught and fails that floor alone (BeginFloor), and
        /// EFT disables the GC in a raid, which is why the floors collect by hand (CollectGarbage).</summary>
        private const long CaptureMemoryBudgetBytes = 1024L << 20;

        /// <summary>What one output pixel costs while its floor is being captured, in bytes, counted
        /// term by term so the budget can be checked by hand:
        ///   12  the float buffer, three floats a pixel (FloorPlan.Pixels)
        ///    1  the drawn mask (FloorPlan.Drawn)
        ///    1  this capture's distances (FloorPlan.Dist)
        ///    4  the eight-bit RGBA picture (FloorPlan.Texture, a Texture2D)
        ///    3  the distance sidecar, an RGB24 texture built and thrown away inside EncodeSidecar
        ///    4  on a merge, the previous picture (FloorPlan.PreviousColour)
        ///    1  on a merge, its sidecar (FloorPlan.PreviousDist)
        /// The merge terms are counted ALWAYS, because a capture that fits fresh and not merged would
        /// fail on its second visit to the map, which is the worst moment to find out. There is no
        /// "DistTexture" field, whatever an earlier version of this list said: the sidecar is a texture
        /// only for the length of one encode.
        ///
        /// It is a MODEL of the peak and not a running total, and these are the ways it is not exact. The
        /// sidecar's encode holds a Color32 staging array as well as its RGB24 texture, four bytes a pixel
        /// for the length of that one call, and it runs on the frame after the picture was written and
        /// after the development's own finally has dropped Pixels, PreviousColour and PreviousDist, so the
        /// two never peak together. Outside the model, and deliberately: the shared 34 MB staging texture
        /// (one per capture, not per floor), the band buffers (a few hundred KB, proportional to width
        /// alone) and the encoded PNG.</summary>
        private const long WorkingSetBytesPerPixel = 26L;

        /// <summary>How much the pixels per metre come down by when a floor does not fit the budget, and
        /// the floor under which it will not go. Half a metre a step, and never under one pixel per
        /// metre: below that a building is a smudge and there is no point capturing at all.</summary>
        private const float BudgetPpmStep = 0.5f;

        private const float MinBudgetPpm = 1f;

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
        /// <see cref="RenderTag"/>, which makes every capture taken under the old value be replaced
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
        /// A light of our own is the fix that needs no knowledge of that pipeline: lights SUM, so the
        /// scene's sun still draws its shadows when there is one, and this guarantees a floor of
        /// illumination when there is not. (The path is forward, not the deferred one the CopyFrom asked
        /// for - see the class doc: an orthographic camera never gets deferred shading. Both sum lights,
        /// and forward is the path in which this light's culling mask and ForcePixel mean what
        /// <see cref="BuildLight"/> intends.)</summary>
        private const float CaptureLightIntensity = 1.5f;

        /// <summary>The LOD bias held for the length of one tile's render - see
        /// <see cref="RenderOnce"/>. Unity picks an LOD group's level from the object's size relative
        /// to the view, and for an orthographic camera the view is <c>orthographicSize</c>: a 20 m
        /// building against our 512 m half-height is 2 %, which is under the threshold at which most of
        /// EFT's LOD groups cull the object entirely. That is the whole of the "warehouses and dorms
        /// draw as flat footprints at every camera height" picture. A bias this large is not a quality
        /// setting here but an OFF switch for LOD selection: multiply that 2 % by a thousand and every
        /// group is at its highest level, which is the only level whose geometry is the building.
        ///
        /// Global, so it is restored by the same statement that changed it; one render, not a
        /// frame of gameplay, is what pays for it.</summary>
        private const float CaptureLodBias = 1000f;

        /// <summary>The terrain base-map distance held for the length of one tile's render - see
        /// <see cref="RenderOnce"/>. Zero means every terrain patch, at any distance, draws with the
        /// terrain's pre-averaged BASE MAP instead of its detail splat layers, which from 300 m up at
        /// half a metre to the pixel is the difference between smooth ground and a 4-pixel checker: the
        /// detail textures tile about every two metres, and two metres is four pixels.
        ///
        /// The cheap, geometry-preserving one of the three ways to fix that. Blurring the picture
        /// afterwards would soften the buildings and the roads with it;
        /// QualitySettings.masterTextureLimit would re-upload every texture in the scene twice per
        /// capture and hitch the raid. This changes one float per terrain and puts it back.</summary>
        private const float CaptureBasemapDistance = 0f;

        /// <summary>What the meta records about HOW a capture was rendered, and what a later capture has
        /// to match before it may be merged into it, thirteen terms in this order: the capture light
        /// (<c>own-</c>), the LOD bias (<c>lod</c>), the terrain base-map distance (<c>basemap</c>),
        /// whether the flat cyan quads are inpainted afterwards (<c>water</c>, from FillWaterCyan),
        /// whether the distance culling was forced visible (<c>cull</c>), whether the flat grey reflection
        /// environment was built (<c>refl</c>), what the water-layer paint pass actually did (<c>wr</c>,
        /// below), the smoothing (<c>smooth</c>), the despeckle (<c>despeckle</c>), the walkable mask
        /// (<c>reach</c>), the supersampling (<c>ss</c>), the multisampling (<c>msaa</c>) and which edition of
        /// the excluded-layer list drew it (<c>layers</c>, see <see cref="LayerListVersion"/>). Every one of
        /// them changes what a pixel is a picture OF, and a picture of one thing must not be merged pixel
        /// by pixel into a picture of another.
        ///
        /// Nothing here counts suppressed shader tokens, whatever an earlier version of this comment said:
        /// there is no shader-name test in the water pass at all, only the LAYER - see
        /// <see cref="WaterLayerName"/>.
        ///
        /// The water term records the OUTCOME, not the intention: <c>wr4p</c> is the version-4 water pass
        /// with a flat material resolved and the water actually painted, <c>wr4n</c> the same build on a
        /// machine where <see cref="BuildWaterPaint"/> found none of <see cref="WaterShaders"/> and the
        /// water therefore drew as the game draws it, and <c>wr0n</c> a build with
        /// <see cref="PaintWater"/> off. Without the p/n those three merged into each other and a painted
        /// river was averaged pixel by pixel with an unpainted one. The p/n can be read here because
        /// <see cref="CollectWater"/> runs before the tag does: <see cref="Prepare"/> calls it alongside
        /// CollectCulling, a dozen lines ahead of the LoadPrevious that reads this property and long
        /// before the meta that records it (WriteMeta, which the run reaches before the finally), and
        /// <see cref="Cleanup"/> nulls <c>_waterFlat</c> again after
        /// each capture, so one capture's outcome is never read into another's. A map with nothing on the water
        /// layer never calls BuildWaterPaint at all and so records <c>n</c> - correctly: nothing was
        /// painted there. Adding the letter changes the tag, so every capture taken before this fix is
        /// replaced by the next capture of its map rather than merged into - once, and on purpose.
        ///
        /// "own-1.5;lod1000;basemap0" is the current recipe. Every one of the three is read from the
        /// constant that is actually applied, so tuning any of them replaces the older captures instead
        /// of merging into them - which is exactly what has to happen to the black rain-era pictures and
        /// to the building-less ones taken before the LOD bias. A capture written before this field
        /// existed records nothing and is replaced for the same reason. See
        /// <see cref="LoadPrevious"/>.</summary>
        /// <summary>Whether this capture's walkable mask was actually built; see the note beside
        /// BuildReach in Prepare. Part of <see cref="RenderTag"/> as the outcome, not the intent.</summary>
        private bool _reachBuilt;

        private string RenderTag =>
            "own-" + CaptureLightIntensity.ToString("0.###", CultureInfo.InvariantCulture) +
            ";lod" + CaptureLodBias.ToString("0.###", CultureInfo.InvariantCulture) +
            ";basemap" + CaptureBasemapDistance.ToString("0.###", CultureInfo.InvariantCulture) +
            ";water" + (FillWaterCyan ? "1" : "0") +
            ";cull" + (ForceCulling ? "1" : "0") +
            ";refl" + (_reflection != null ? "1" : "0") +
            ";wr" + (PaintWater ? WaterPassVersion : 0).ToString(CultureInfo.InvariantCulture) +
            (_waterFlat != null ? "p" : "n") +
            ";smooth" + (SmoothingEnabled ? (SmoothingRadius * 2 + 1).ToString(CultureInfo.InvariantCulture) : "0") +
            ";despeckle" + (DespeckleEnabled ? "1" : "0") +
            ";reach" + (ReachEnabled && _reachBuilt ? (ReachIsAlpha ? "2" : "1") : "0") +
            ";ss" + SupersampleFactor.ToString(CultureInfo.InvariantCulture) +
            ";msaa" + _msaa.ToString(CultureInfo.InvariantCulture) +
            ";layers" + LayerListVersion.ToString(CultureInfo.InvariantCulture) +
            (_rig != null ? ";" + MenuRigTag : "");

        /// <summary>Added to the far plane so the band's own floor is comfortably inside it rather
        /// than exactly on it.</summary>
        private const float FarClipSlack = 1f;

        /// <summary>A floor's PNG is not written past this. 192 MiB (rollback: 48 MiB), four times the
        /// last value, because the pixel count went up four times again with
        /// <see cref="MaxPixelsPerMetre"/> 4 -> 8: Customs was 4472x2156, 9.6 million pixels, which a PNG
        /// of a photographed map encoded to somewhere around 10-25 MB, and is 8944x4312 now, 38.6
        /// million, so somewhere around 40-100 MB. Hitting 192 still means something is wrong - noise
        /// rather than a map, or a resolution nobody wants - and the local capture is the only thing
        /// this bounds: what travels to a host and what ships in the zip are downscaled to 2048 long
        /// side by MapTransfer and package.ps1 respectively.</summary>
        private const int MaxFloorPngBytes = 192 * 1024 * 1024;

        /// <summary>WP4 B2: rollback for the managed encode. False = EncodeToPNG on the main thread for every floor, side
        /// and sidecar, exactly as before (the per-floor Texture2D, FinishFloor, WriteSidecar, FinishSide). Static
        /// readonly, not const, for FillWaterCyan's reason.</summary>
        private static readonly bool ManagedPngEncode = true;

        /// <summary>WP4 B2, verification build only: every picture and sidecar is ALSO encoded by Unity, as the old build
        /// did, into &lt;mod&gt;/captures-verify/&lt;key&gt;/ - outside the captures root, so no reader, sweep or checker run
        /// sees it - for tools/check-capture.py --compare-dir. Off in a release.</summary>
        private static readonly bool VerifyManagedPng = false;

        /// <summary>WP4 B2: the managed encoder's row filter. Adaptive (libpng's heuristic) keeps the sizes near Unity's,
        /// which matters only to the MaxFloorPngBytes cap - and that is judged on Unity's length anyway (SettlePicture).</summary>
        private const PngEncoder.Filter PngFilter = PngEncoder.Filter.Adaptive;

        /// <summary>WP4 B2: how long a settle waits, a frame at a time, for an encode before it falls back to Unity's
        /// encoder for that file. 90 s (rollback: 30 s): an encode's time goes with its pixels, four times as many
        /// at MaxPixelsPerMetre 8, and a fallback is Unity's encoder on the MAIN thread - a far longer freeze than the
        /// wait it would save. Two of these waits are in <see cref="WorstCaseSeconds"/>.</summary>
        private const double EncodeWaitSeconds = 90d;

        /// <summary>WP4 B2: a managed file failed its round trip this session - every later picture is encoded by Unity.</summary>
        private static bool _managedPngOff;

        /// <summary>WP4 B2: the first encode of each colour type per session carries the worker-side round trip.</summary>
        private static bool _rgbaRoundTripped;

        private static bool _rgbRoundTripped;

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
        /// CullingMask, DisablerCullingObject, the collider layers) and corpses.
        ///
        /// Water is NOT in this list, whatever an earlier version of this comment said. It was, for one
        /// build: the first real capture of Customs drew the pools by Dorms as flat cyan placeholder
        /// blocks - the water shader has nothing to reflect from a camera that is not the player's - and
        /// the layer was dropped so the ground under it drew instead. A map of Customs with no river in it
        /// is not the map, so water is kept and PAINTED instead: the renderers on the water layer are
        /// swapped for one flat blue material for the render (see WaterLayerName, WaterPaint and
        /// HoldWater), and whatever cyan still gets through is inpainted afterwards (FillWaterCyan).
        ///
        /// TransparentFX went the same way and for the same reason: the campaign capture of Customs has
        /// blue streaks lying across the crane and the railway where glass and transparent effects are
        /// drawn by a shader that expects the player's camera behind it. What is under them - the crane,
        /// the rails - is what a map should show.
        ///
        /// What is deliberately KEPT: Default, Terrain, Foliage, Grass, Interactive, Loot and
        /// LevelBorder. These are the map.
        ///
        /// The list is longer than the plan's four names because Phase 0 logged all 32 layer names of
        /// this game version (D1) and they could then be named exactly. Names this version does not
        /// carry are skipped, and the finished mask is logged once, so what was actually excluded is
        /// on the record rather than assumed.</summary>
        private static readonly string[] ExcludedLayerNames =
        {
            "Player", "PlayerRenderers", "PlayerCollisionTest", "PlayerSpiritAura",
            "Weapons", "Weapon Preview", "Shells", "Deadbody",
            "UI", "Menu Environment", "RainDrops", "Sky", "TransparentFX",
            "Triggers", "CullingMask", "DisablerCullingObject",
            "DoorLowPolyCollider", "LowPolyCollider", "HitCollider",
            "TransparentCollider"
        };

        /// <summary>Which edition of <see cref="ExcludedLayerNames"/> a capture was drawn with; part of
        /// <see cref="RenderTag"/>, so a change to the list replaces the older captures of a map
        /// instead of merging into them.
        ///
        /// 2: HighPolyCollider is DRAWN. The throwaway probe key, run inside Customs' Big Red (2026-09-22), found
        /// the building's own walls-and-roof mesh - "karkas", LOD 0 of its group, real materials, shadows
        /// on, the thing the player's camera shows - sitting on that layer, and the layer's name had
        /// put it on the collider list. With it dropped, the only renderers of that building left in
        /// the picture were its two interior-volume shells drawn with a flat vertex-paint shader, which
        /// is the translucent teal slab three campaigns showed where a warehouse should be. The
        /// player's camera draws the layer, so a renderer on it is meant to be seen; the collider
        /// layers that stay excluded are the ones the game's camera never draws either, which the
        /// copied mask already removes - the name list only ever subtracts.</summary>
        private const int LayerListVersion = 2;

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

                // A new raid starts with the game's own full collection (PrepareSession), so the trigger's baseline
                // must not carry the last raid's heap (hotfix review): the first capture would otherwise collect late.
                _heapAfterCollect = -1;

                // Stage M2c: a new raid asks the disk again - a menu capture since the last raid may have changed the set
                _guardedMenuSets.Clear();

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

        // --- stage M2: the capture in the main menu ------------------------------------------------------------------

        /// <summary>Stage M2a (review): what a menu capture's key adds to the location's Id - "bigmap-menu" - so it writes a
        /// set of its own beside the raid set (its own captures folder), never merging into or winning over the raid
        /// pictures while its lighting is not yet the menu rig. No upload is offered for it either. The Maps tab never asks
        /// for such a key (MapCatalog looks sets up by the viewed location's id), so it is scanned and counted but not
        /// drawn. Stage M3: the Maps tab's "Capture from game files" (MenuCaptureRunner) writes the location's OWN key;
        /// this suffix is the throwaway probe key's test set only (and ModSettings.ShowMenuTestSets draws it).</summary>
        internal const string MenuCaptureKeySuffix = "-menu";

        // --- stage M2b: a menu capture's time budgets ----------------------------------------------------------------------
        //
        // The raid's caps are sized so a player standing in a raid gets the frames back: the building phase is what
        // MapMeshBuilder.CaptureSecondsBudget leaves after the floors and the sides' estimate (Customs' menu capture got a
        // 36 s soft cap and kept 3,238 of 43,552 candidates, "hard cap reached with 34,557 not looked at"). In the main menu
        // nobody is waiting in a raid, so a menu capture takes the budgets below instead - every one routed through the
        // Plan (floors, watchdog) or the builder's Request (buildings, atlas), so a raid capture never reads them.

        /// <summary>Stage M2b rollback: false gives a menu capture the raid's time caps exactly (stage M2a).</summary>
        internal static readonly bool MenuCaptureBudgets = true;

        /// <summary>Stage M2b: seconds the menu capture's relief and building phase is given (the Request's BuildingSeconds;
        /// the builder takes the relief's measured seconds off and the hard cap is ten more) - in place of the raid's
        /// min(MaxBuildingSeconds 100, CaptureSecondsBudget 210 - floors - sides - atlas).</summary>
        internal const double MenuBuildingSeconds = 600d;

        /// <summary>Stage M2b: the menu capture's atlas cap (the Request's AtlasSeconds) - in place of the raid's 40 s
        /// MapMeshBuilder.AtlasSecondsCap. More buildings bring more materials to capture.</summary>
        internal const double MenuAtlasSeconds = 180d;

        /// <summary>Stage M2b: what the menu watchdog allows beyond the phases it bounds - the relief's cast, the atlas's
        /// settling and the build's own bookkeeping, which no cap covers (the raid's 200 s watchdog has 42 s over its 158).</summary>
        private const double MenuMeshWatchdogMarginSeconds = 60d;

        /// <summary>Stage M2b: the menu capture's mesh watchdog - the building soft cap, its hard cap's ten, the drain, the
        /// atlas cap and the margin: 858 s, in place of the raid's <see cref="MeshWatchdogSeconds"/> (200).</summary>
        internal const double MenuMeshWatchdogSeconds =
            MenuBuildingSeconds + MapMeshBuilder.HardOverSoftSeconds + MapMeshBuilder.DrainSeconds + MenuAtlasSeconds +
            MenuMeshWatchdogMarginSeconds;

        /// <summary>Stage M2b: the menu capture's floor phase cap, in place of <see cref="FloorPhaseSeconds"/> (180). A menu
        /// capture renders EVERY tile of every floor (there is no nearer earlier capture to keep), about 60 s a floor at the
        /// 1 GB floor budget (Customs 45 tiles in 41 s plus its encode), so a map of three or more floors would be cut at
        /// 180. The sides keep <see cref="SidePhaseSeconds"/>: they are four views at 4 px/m whatever the floors (Customs
        /// 43 s of 180).
        ///
        /// 1200 s (rollback: 600) since the finer ground (stage M2c): at about 0.9 s a tile, Customs at 14.3 px/m is 144
        /// tiles (about 130 s) and Interchange at 13.5 px/m is 169 tiles a floor, five floors about 770 s - which 600 would
        /// cut at the fourth or fifth floor.</summary>
        internal const double MenuFloorPhaseSeconds = 1200d;

        /// <summary>Stage M2b: the longest a menu capture can run with every cap in force - <see cref="WorstCaseSeconds"/>'s
        /// terms with the menu's floor cap and watchdog, and without a campaign checkpoint (a menu capture holds none):
        /// 3438 s (2178 s before the finer ground: its floor cap is 1200 s; the settles no phase cap covers are the in-loop
        /// floor settle and the last floor's, each at <see cref="MenuEncodeWaitSeconds"/>, and the last side's at
        /// <see cref="EncodeWaitSeconds"/>). Counted at the finer ground's numbers whatever <see cref="MenuFineGround"/> says -
        /// a cap too high only waits longer for a capture that is truly hung, and one too low cuts a capture that was
        /// working. MenuMapHost's whileLoaded cap is sized from it.</summary>
        internal const double MenuWorstCaseSeconds =
            MenuFloorPhaseSeconds * FloorPhaseOverrun +             // 1500
            MeshBaseWaitSeconds +                                   //  20
            MenuMeshWatchdogSeconds + MeshWatchdogGraceSeconds +    // 873
            AtlasEncodeWaitSeconds +                                //  60
            SidePhaseSeconds * SidePhaseOverrun +                   // 225
            FinishAllowanceSeconds +                                //  60
            CommitWaitSeconds +                                     //  10
            2 * MenuEncodeWaitSeconds +                             // 600 (the in-loop floor settle and the last floor's)
            EncodeWaitSeconds;                                      //  90 (the last side's settle)

        // --- stage M2c: a menu capture's finer ground ----------------------------------------------------------------------
        //
        // The ground picture is the one part of a capture drawn by the game's own terrain shader under the capture's own
        // light, and in the main menu nobody is waiting in a raid for the frames - so a menu capture takes it at up to twice
        // the raid's density. Every number below reaches the capture through the Plan (Prepare sets it only on a menu plan),
        // so a raid capture reads exactly the constants it always did. The sides keep the raid's scale and caps: they are
        // walls seen at 45 degrees, where density buys less (SidePixelsPerMetre).

        /// <summary>Stage M2c rollback: false gives a menu capture the raid's ground exactly - <see cref="MaxPixelsPerMetre"/>,
        /// <see cref="CaptureMemoryBudgetBytes"/>, <see cref="MaxFloorPngBytes"/> and <see cref="EncodeWaitSeconds"/>.</summary>
        internal static readonly bool MenuFineGround = true;

        /// <summary>Stage M2c: the most pixels per metre a menu capture's floors are taken at (the raid's
        /// <see cref="MaxPixelsPerMetre"/> is 8). A CAP, like the raid's: on a big map the long side binds first - the picture
        /// may be at most Resolution() long, min(16384, DynamicMapsLibrary.MaxPictureSide), the largest single texture the
        /// GPU takes - so Customs (1,143 m) lands at about 14.3 px/m and 16384 px, and only a map under about 1 km is held
        /// by this number. Then <see cref="MenuCaptureMemoryBudgetBytes"/> may lower it in half steps, as it does a raid's.
        /// The header line says which of the three decided it.</summary>
        private const float MenuMaxPixelsPerMetre = 16f;

        /// <summary>Stage M2c: one floor's working-set budget for a menu capture, in place of
        /// <see cref="CaptureMemoryBudgetBytes"/> (1 GiB), at the same <see cref="WorkingSetBytesPerPixel"/> through the same
        /// <see cref="Budget"/>. 4 GiB: Customs at its cap-bound 14.3 px/m is 16384x8512, 139 million pixels and 3,458 MiB,
        /// and is not touched; Interchange's 965x925 m rectangle walks 16 -> 13.5 px/m (about 13030x12490, 4,035 MiB). The
        /// largest single allocation is the float buffer, 12 of the 26 bytes: 1,596 MiB on Customs, about 1,862 on Interchange -
        /// both under 2 GiB, so not even a runtime with the CLR's 2 GB object limit would refuse it, and every int index
        /// (W x H x 3 floats, at most 16384 x 16384 x 3 = 805 million) is far under int.MaxValue. This budget also keeps a
        /// floor under 165 million pixels, which is under the 179 million PIL refuses outright (tools/check-capture.py
        /// --pixels). The machine this was sized on has 63 GB; one floor is worked at a time and collected between.
        ///
        /// Gated by the machine's RAM: at most an eighth of SystemInfo.systemMemorySize, so a 16 GB machine works a floor in
        /// 2 GiB and an 8 GB one in the raid's 1 GiB - and Budget lowers the scale to fit, which costs sharpness rather than
        /// an OutOfMemoryException. A machine that reports no memory gets the raid's <see cref="CaptureMemoryBudgetBytes"/>.
        /// MAIN THREAD (SystemInfo): read in Prepare. The capture header says which number it was.</summary>
        private static long MenuCaptureMemoryBudgetBytes
        {
            get
            {
                var ramMb = SystemInfo.systemMemorySize;
                if (ramMb <= 0) return CaptureMemoryBudgetBytes;

                return Math.Min(MenuCaptureMemoryBudgetCapBytes, ((long)ramMb << 20) / MenuCaptureMemoryRamShare);
            }
        }

        /// <summary>Stage M2c: the most <see cref="MenuCaptureMemoryBudgetBytes"/> can be, whatever the RAM. 4 GiB.</summary>
        private const long MenuCaptureMemoryBudgetCapBytes = 4L << 30;

        /// <summary>Stage M2c: the share of the machine's RAM one menu floor may work in: an eighth.</summary>
        private const long MenuCaptureMemoryRamShare = 8L;

        /// <summary>Stage M2c: a menu floor's PNG cap, in place of <see cref="MaxFloorPngBytes"/> (192 MiB). Customs at 8 px/m
        /// encoded to 63 MB (8576x4456); at 16384x8512 that is about 230 MB, over the raid's cap - 512 MiB keeps the cap a
        /// test for noise rather than for a map. The managed encoder writes chunked parts with a long length
        /// (PngEncoder.Result), so nothing in it counts bytes in an int; the menu set is never uploaded at this size (a
        /// host copy is downscaled to 2048).</summary>
        private const int MenuMaxFloorPngBytes = 512 * 1024 * 1024;

        /// <summary>Stage M2c: how long a menu floor's settle waits for its managed encode, in place of
        /// <see cref="EncodeWaitSeconds"/> (90 s). Up to 3.6x the pixels of an 8 px/m floor, and a fallback is Unity's encoder
        /// on the main thread for a 557 MB picture - a far longer freeze than this wait - so 300 s.</summary>
        private const double MenuEncodeWaitSeconds = 300d;

        /// <summary>Stage M2c: the meta's <c>capturedIn</c> value on a set a menu capture wrote (CaptureMeta.CapturedIn) - an
        /// explicit marker, so a menu set is known as one whatever its render recipe says (with the menu rig off it carries
        /// no rig term). A raid capture merged into a menu set keeps it. tools/check-capture.py reads it.</summary>
        internal const string MenuSetMarker = "menu";

        /// <summary>Stage M2c rollback: false lets a raid capture replace a menu set it cannot merge into, as before. True:
        /// a RAID capture that finds a stored menu set it may not merge into - a different density or extent, which the
        /// finer ground makes the normal case, or any other of LoadPrevious's refusals - is REFUSED for that map and the
        /// set is left untouched, because a raid picture is coarser than the menu's and "starting fresh" would replace the
        /// sharper set with it. A merge picture to picture cannot take two densities, so there is no third way.
        ///
        /// The refusal is PERMANENT for that map until a fresh menu capture is taken over it: a SchemaVersion bump, or a
        /// re-harvested extent (the zone harvester measuring the map differently), makes every later raid capture mismatch
        /// a menu set that no raid capture may replace - so after either, recapture the map in the menu. With the switch
        /// off a raid capture replaces the set as before. A guarded map is remembered for the raid
        /// (<see cref="IsGuardedMenuSet"/>), so AutoCapture and the campaign stop asking.</summary>
        internal static readonly bool RaidLeavesMenuSets = true;

        /// <summary>Stage M2c: the maps whose stored menu set refused a raid capture in THIS raid (LoadPrevious) - so the
        /// next capture of one stops in Prepare at once, and AutoCapture and the campaign can refuse up front with the real
        /// reason. Cleared per GameWorld (Install): a menu capture taken between raids may have made the set mergeable, or
        /// replaced it.</summary>
        private static readonly HashSet<string> _guardedMenuSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Stage M2c: whether this raid already found that <paramref name="key"/>'s stored set is a menu set a raid
        /// capture must leave alone. Main thread.</summary>
        /// <param name="key">The map key (MapKey).</param>
        internal static bool IsGuardedMenuSet(string key) => !string.IsNullOrEmpty(key) && _guardedMenuSets.Contains(key);

        /// <summary>Stage M2c: the reason a guarded map's capture is refused, worded for the campaign's and AutoCapture's
        /// lines.</summary>
        internal const string GuardedMenuSetReason = "the stored set is a sharper menu capture; raid captures leave it alone";

        // --- stage M2b: a menu capture's light ------------------------------------------------------------------------------
        //
        // A raid capture photographs the raid's sun plus its own straight-down light. The menu has no TOD sky and no weather,
        // so a menu capture brings the whole of its light: one directional sun at a fixed height and bearing that casts
        // soft shadows (the relief and the buildings read as shapes), plus a flat ambient so a face turned from the sun is
        // not black - the same for every map, and written into the meta's lighting block so the 3D view's sun matches.

        /// <summary>Stage M2b rollback: false lights a menu capture as stage M2a did (the raid rig: the own straight-down
        /// light, the scene's lights as loaded, no lighting block).</summary>
        internal static readonly bool MenuLightingRig = true;

        /// <summary>Degrees above the horizon of the menu rig's sun. High enough that a street between two blocks is not in
        /// shadow all day, low enough that a building's shadow says how tall it is; inside the 3D view's accepted 15..55.</summary>
        internal const float MenuSunElevation = 50f;

        /// <summary>The menu rig sun's bearing, degrees clockwise from world +z (north) towards +x - the convention the 3D
        /// view reads a sun direction back with (Atan2(x, z)). 135: from the south-east.</summary>
        internal const float MenuSunAzimuth = 135f;

        /// <summary>The menu rig sun's intensity: the raid capture's own light's (<see cref="CaptureLightIntensity"/>), so
        /// the percentile stretch lands the ground on the mid-greys the raid pictures have.</summary>
        internal const float MenuSunIntensity = 1.5f;

        /// <summary>The menu rig sun's shadow strength. Full: the flat ambient is what lifts a shadowed face, as it is in
        /// the 3D view, which draws its spot sun at the strength the lighting block records.</summary>
        internal const float MenuSunShadowStrength = 1f;

        /// <summary>The menu rig sun's colour when the hosted map has no LevelSettings: a neutral warm white.</summary>
        internal static readonly Color MenuSunFallbackColour = new Color(1f, 0.96f, 0.88f, 1f);

        /// <summary>The menu rig's flat ambient as a share of the sun (colour times intensity, per channel, at most 1).</summary>
        internal const float MenuAmbientOfSun = 0.35f;

        /// <summary>The least shadow distance the menu rig renders with: twice a tile's side at the full 8 px/m (2048 samples
        /// at 16 samples a metre, supersampled 2x, is a 128 m tile), so a tile at a lower pixels per metre is covered too.
        /// The render uses the camera's far plane when that is further, which on a floor it always is - the top band's
        /// camera stands 300 m over the roofs, so its shadows must reach past that to the ground.</summary>
        private const float MenuShadowMinDistance = 256f;

        /// <summary>The menu rig's shadow cascades: one. An orthographic view has every pixel at the same scale, so a split by
        /// distance would only spend the shadow map on depth slices no nearer than another.</summary>
        private const int MenuShadowCascades = 1;

        /// <summary>The <see cref="RenderTag"/> term a menu-rig capture adds - so a set lit by the rig replaces a raid-lit
        /// set once (LoadPrevious refuses the merge) instead of merging exposures taken under two different lights.</summary>
        private const string MenuRigTag = "menu-rig-1";

        /// <summary>Stage M2b (review) rollback: false makes a raid capture replace a menu-rig set (the tags differ) rather
        /// than merge into it (LoadPrevious).</summary>
        internal static readonly bool MenuRigMerge = true;

        // --- stage M2b: a menu capture's upload ----------------------------------------------------------------------------

        /// <summary>Stage M2b rollback: false never offers a menu capture to the host. True: a menu capture of a map's REAL
        /// key (stage M3) holds the map's uploads for the capture and releases the hold after the write, so exactly one
        /// upload follows. A <see cref="MenuCaptureKeySuffix"/> test set is never uploaded either way.</summary>
        internal static readonly bool MenuCaptureUploads = true;

        /// <summary>Whether a menu capture of <paramref name="key"/> is offered to the host: the switch on and the key a
        /// real one, not a "-menu" test set.</summary>
        /// <param name="key">The capture's key.</param>
        internal static bool MenuUploads(string key) =>
            MenuCaptureUploads && !string.IsNullOrEmpty(key) &&
            !key.EndsWith(MenuCaptureKeySuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Stage M2: one main-menu capture - what stands in for the raid's GameWorld. The map key (the location's Id - plus
        /// <see cref="MenuCaptureKeySuffix"/> for the probe's test set), its write mode (stage M3), the identity the mesh builder's scene cache is keyed on (a raid keys it on
        /// its GameWorld; each menu capture is its own "world"), <see cref="MenuMode"/>, which every menu branch of this
        /// file reads, and the hook the wake's check runs from just before the first tile.
        /// </summary>
        internal sealed class MenuSession
        {
            /// <summary>The location id as it was asked for (the throwaway setting's text, or the Maps tab's map).</summary>
            internal readonly string LocationId;

            /// <summary>The capture's key: the location's own Id - plus <see cref="MenuCaptureKeySuffix"/> for the throwaway
            /// probe's test set.</summary>
            internal readonly string Key;

            /// <summary>Stage M3: how this capture treats the set already stored under <see cref="Key"/>.</summary>
            internal readonly MenuWriteMode Write;

            /// <summary>Stage M3: why the stored set could not take this capture and nothing was captured - the Maps tab
            /// says "choose Replace" with it. Null otherwise.</summary>
            internal string NeedsReplace;

            /// <summary>Stage M3: the write's summary ("2 floor(s), 251,234,567 bytes"), or null when nothing was
            /// written.</summary>
            internal string Written;

            /// <summary>Stage M3: the folder name the replaced set was moved to (captures/&lt;key&gt;.bak-&lt;time&gt;),
            /// or null when nothing was set aside.</summary>
            internal string SetAside;

            /// <summary>Stage M3 (review): the bytes the set moved aside holds.</summary>
            internal long SetAsideBytes;

            /// <summary>Stage M3 (review): the write failed after the set was moved aside and the old set was put back in
            /// place (true), or could not be (false while <see cref="SetAside"/> is set and the write failed) - the result
            /// line says which.</summary>
            internal bool SetAsideRestored;

            /// <summary>Stage M3 (review): the write failed after its first commit (no meta was written).</summary>
            internal string WriteFailed;

            /// <summary>Stage M3: why the capture stopped before its write, when it said one (Prepare's refusals), or null.</summary>
            internal string Stopped;

            /// <summary>What the scene cache is keyed on in place of a GameWorld.</summary>
            internal readonly object Identity = new object();

            /// <summary>Always true for a session: the flag the plan copies (Plan.MenuMode).</summary>
            internal readonly bool MenuMode = true;

            /// <summary>Run once, just before the capture's first tile - RunMenuCapture sets it to the wake's "switched
            /// off again" count.</summary>
            internal Action BeforeFirstTile;

            /// <summary>Stage M2b: how many of the hosted scenes' directional lights were disabled for the menu rig, for the
            /// capture header; -1 when none were looked for (the rig off).</summary>
            internal int DirectionalLightsDisabled = -1;

            /// <summary>The throwaway probe's session: the "-menu" test set, merged when it can be and replaced (with
            /// no copy kept) when it cannot - stage M2's behaviour.</summary>
            /// <param name="locationId">As asked for.</param>
            /// <param name="locationKey">The location's own Id (MenuMapHost.LocationKey).</param>
            internal MenuSession(string locationId, string locationKey)
                : this(locationId, locationKey + MenuCaptureKeySuffix, MenuWriteMode.MergeOrFresh)
            {
            }

            /// <summary>Stage M3: a session writing <paramref name="key"/> as <paramref name="write"/> says.</summary>
            /// <param name="locationId">As asked for.</param>
            /// <param name="key">The capture key - the location's own Id for the Maps tab's action.</param>
            /// <param name="write">Merge, replace, or (the test set) merge-or-fresh.</param>
            internal MenuSession(string locationId, string key, MenuWriteMode write)
            {
                LocationId = locationId;
                Key = key;
                Write = write;
            }
        }

        /// <summary>Stage M3: what a menu capture does with the set already stored under its key.</summary>
        internal enum MenuWriteMode
        {
            /// <summary>Stage M2's rule, for the throwaway "-menu" test set: merged when LoadPrevious accepts it, else
            /// replaced in place (no copy kept).</summary>
            MergeOrFresh,

            /// <summary>"Capture from game files": merged into a stored set LoadPrevious accepts (a menu set of the same
            /// extent, density and recipe), started when there is none; any other stored set - a raid set, one at another
            /// density - REFUSES the capture and is left exactly as it is (the Maps tab says to choose Replace).</summary>
            Merge,

            /// <summary>"Replace with a fresh capture": the stored set is never read; at the write it is moved aside to
            /// captures/&lt;key&gt;.bak-&lt;time&gt;/ and the fresh set written in its place.</summary>
            Replace,
        }

        /// <summary>Stage M3 rollback: false makes Merge and Replace both behave as M2's merge-or-fresh (the stored set is
        /// merged when it can be and replaced in place, with no copy kept).</summary>
        internal static readonly bool MenuReplaceMode = true;

        /// <summary>Stage M3: what a replaced set's folder is renamed to, between the key and a local time stamp -
        /// captures/bigmap.bak-20261001-142233/. MapCatalog and tools/check-capture.py skip such folders, so a backup is
        /// never drawn or checked as the map; to restore one, delete the map's folder and rename the backup back.</summary>
        internal const string SetAsideInfix = ".bak-";

        // --- stage M3: the viewing copy ------------------------------------------------------------------------------------

        /// <summary>Stage M3: the longest side of a menu floor's viewing copy, px. A menu capture's ground is up to 16384 px
        /// long (Customs 16380x8516, a 246 MB PNG), which the Maps tab would decode and block-compress on the main thread
        /// at every open; the copy is what it draws instead - a quarter of the pixels, inside the 8192 every GPU takes.</summary>
        internal const int ViewPictureSide = 8192;

        /// <summary>Stage M3 rollback: false writes no viewing copy, and every set is drawn from its full picture as in
        /// stage M2c. True: any floor whose picture is longer than <see cref="ViewPictureSide"/> gets one (review: menu
        /// and raid alike, so a raid merge into a menu set keeps its copy). A floor kept unchanged (no tile rendered)
        /// carries its stored copy but never rebuilds a missing one: its pixels are not in memory at the write, and
        /// decoding the full picture for it would be the very main-thread cost the copy exists to avoid - the next
        /// capture that renders the floor writes it.</summary>
        internal static readonly bool MenuViewCopy = true;

        /// <summary>Stage M3: a floor's viewing copy's file name, beside its picture: bigmap-0.view.png.</summary>
        /// <param name="key">The map key.</param>
        /// <param name="level">The floor's level.</param>
        internal static string ViewFileName(string key, int level) =>
            $"{key}-{level.ToString(CultureInfo.InvariantCulture)}.view.png";

        /// <summary>Stage M3: the viewing copy's size for a <paramref name="width"/> x <paramref name="height"/> picture: the
        /// long side <see cref="ViewPictureSide"/>, the short side scaled and rounded to the nearest multiple of 4 (the
        /// viewer block-compresses only a picture whose sides are multiples of 4 - DynamicMapsLibrary.CompressPictures).
        /// Each axis keeps its own scale (the box filter's ratio per axis), so the copy covers exactly the full picture's
        /// extent; at most 2 px of 8192 make its pixels a hair off square, which nothing reads. False when the picture is
        /// no larger than the copy would be.</summary>
        internal static bool ViewSize(int width, int height, out int viewWidth, out int viewHeight)
        {
            viewWidth = width;
            viewHeight = height;

            var longSide = Math.Max(width, height);
            if (width < 1 || height < 1 || longSide <= ViewPictureSide) return false;

            int Scaled(int side)
            {
                if (side == longSide) return ViewPictureSide;

                var scaled = (int)Math.Round((double)side * ViewPictureSide / longSide / 4d, MidpointRounding.AwayFromZero) * 4;
                return Math.Max(4, Math.Min(side, scaled));
            }

            viewWidth = Scaled(width);
            viewHeight = Scaled(height);
            return viewWidth < width || viewHeight < height;
        }

        /// <summary>Stage M2: adds a capture component for <paramref name="session"/> to a new root object of the ACTIVE
        /// scene - the menu's own, since MenuMapHost loads every map scene additively and never makes one active - so the
        /// map's unload never takes it. Nothing relies on it ticking (a plugin-made object may not tick in the menu -
        /// memory ddol-objects-dead-in-menu): <see cref="RunMenuCapture"/> hands its run to MenuMapHost's work loop,
        /// which runs on TrackerHotkey's coroutine. Null, with the reason, when it cannot be made.</summary>
        /// <param name="session">The menu capture.</param>
        /// <param name="why">Why nothing was installed.</param>
        internal static MapCapture InstallForMenu(MenuSession session, out string why)
        {
            why = null;

            try
            {
                if (session == null)
                {
                    why = "no menu session";
                    return null;
                }

                if (ModEnvironment.IsHeadlessClient)
                {
                    why = "a headless client has no menu to capture from";
                    return null;
                }

                if (IsCapturing)
                {
                    why = "a map capture is already running";
                    return null;
                }

                // Any GameWorld at all - a raid's, or the hideout's, which can outlive a hideout visit - is a world the
                // capture's scene-wide reads (FindObjectsOfType) would reach into beside the hosted map.
                var world = Comfort.Common.Singleton<GameWorld>.Instance;
                if (world != null)
                {
                    why = $"a GameWorld is set ({world.GetType().Name}) - restart the game and capture from the main menu " +
                          "before visiting the hideout or a raid";
                    return null;
                }

                var go = new GameObject("QuestTreeMenuMapCapture");
                var runner = go.AddComponent<MapCapture>();
                runner._menu = session;

                // IsCapturing finds this one rather than an older reference.
                _current = runner;
                return runner;
            }
            catch (Exception ex)
            {
                why = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Stage M2: a whole capture of the map MenuMapHost has loaded in the main menu - floors, 3D mesh, sides, the write
        /// - for <c>MenuMapHost.Run(location, () =&gt; MapCapture.RunMenuCapture(session))</c>. Before the capture the
        /// hosted scenes' switched-off geometry is switched on (MenuMapHost.WakeHosted: EFT hides interiors by culling
        /// around a player the menu does not have), and in the finally - whatever ended the run, including the host
        /// disposing it - the capture is cleaned up first (its own finally, innermost), then every switch is put back and
        /// the component is destroyed. The lighting is today's (the capture's own straight-down light).
        /// </summary>
        /// <param name="session">The menu capture.</param>
        internal static IEnumerator RunMenuCapture(MenuSession session)
        {
            const string tag = "QuestTree: menu capture: ";
            var clock = Stopwatch.StartNew();
            MapCapture runner = null;
            var wake = new MenuMapHost.Wake();
            MapTransfer.UploadHold uploads = null;

            try
            {
                runner = InstallForMenu(session, out var why);
                if (runner == null)
                {
                    Plugin.LogSource?.LogWarning($"{tag}nothing was captured - {why}.");
                    yield break;
                }

                Plugin.LogSource?.LogInfo(
                    $"{tag}{session.Key} (asked for as '{session.LocationId}', {MenuWriteText(session.Write)}) - waking the hosted " +
                    "scenes, then capturing.");

                MenuMapHost.SetPhase("waking the map's scenes");
                var waking = MenuMapHost.WakeHosted(wake, MenuCaptureMask());
                while (waking.MoveNext()) yield return waking.Current;

                // Stage M2b: the menu rig is the capture's whole light, so no scene sun (the scripts scene's directional
                // light, say) may add to it - every directional light in the hosted scenes is switched off, in the same
                // journal the wake restores from. After the wake, so one a woken object brought along is off as well.
                if (MenuLightingRig) session.DirectionalLightsDisabled = MenuMapHost.DisableHostedDirectionalLights(wake);

                // (review) whether anything switched the wake back off between the wake and the first tile
                session.BeforeFirstTile = () =>
                    Plugin.LogSource?.LogInfo($"{tag}{session.Key} just before the first tile: {wake.StillOff()}.");

                // Stage M2b: a menu capture of a map's real key is uploaded ONCE, after its write - the hold makes the
                // write's UploadCapture an owed upload, and the release below issues it. A "-menu" test set never goes up.
                if (MenuUploads(session.Key))
                {
                    uploads = MapTransfer.HoldUploads(runner, session.Key, "menu capture", preempts: false);
                    Plugin.LogSource?.LogInfo(
                        $"{tag}{session.Key}'s uploads are held for the capture{(uploads == null ? " (no hold was taken - WP3's switch is off, so the write uploads it itself)" : "")}; one upload follows the write.");
                }

                // As the key press sets them: a person asked for this map, so it builds the mesh and takes the sides.
                runner._running = true;
                runner._automatic = false;
                runner._skipMesh = false;
                runner._verifyMesh = false;

                // Yielded, not started: MenuMapHost's work loop drives a nested enumerator itself, so every frame of the
                // capture is one of the host's (its dead-run check, its raid test, its cap) and none needs this component
                // to tick.
                yield return runner.Run();

                // Stage M2b: the write is done (or the capture wrote nothing, and nothing is owed) - the one upload goes now.
                if (uploads != null)
                {
                    var outcome = MapTransfer.ReleaseUploads(uploads);
                    Plugin.LogSource?.LogInfo($"{tag}{session.Key}'s upload hold released after the write: {outcome}.");
                }

                // The capture has cleaned up (its own finally ran as it finished); the switches go back a chunk a frame. The
                // finally below finishes whatever this did not get to.
                MenuMapHost.SetPhase("switching the map's scenes back");
                yield return wake.RestoreSpread();

                Plugin.LogSource?.LogInfo($"{tag}{session.Key} ended after {Ms(clock.Elapsed.TotalMilliseconds)} ms (the capture's own lines above say what it wrote).");
            }
            finally
            {
                // After the capture's own finally (the host disposes the innermost enumerator first): the capture has let
                // the scene go before its original switches come back.
                wake.Restore();

                // Stage M2b: a run the host stopped part way still ends its hold - idempotent, and an upload is issued only
                // when the write had happened (the map is owed only then).
                if (uploads != null)
                {
                    try
                    {
                        MapTransfer.ReleaseUploads(uploads);
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"{tag}its upload hold could not be released ({ex.Message}).");
                    }
                }

                if (runner != null)
                {
                    runner._running = false;

                    try
                    {
                        Destroy(runner.gameObject);
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning($"{tag}its component could not be destroyed ({ex.Message}).");
                    }
                }
            }
        }

        /// <summary>
        /// Stage M3 (review): why a "Capture from game files" (Merge) of <paramref name="key"/> would be refused, decided
        /// from the stored meta alone BEFORE any scene is loaded - so neither one map nor capture all loads a whole map
        /// just to be told to choose Replace. Null when there is no stored set, or it is a menu set (whose finer checks -
        /// extent, density, recipe - still run in LoadPrevious after the load). A set this cannot read refuses too, as
        /// LoadPrevious would. Reads only; never creates the map's folder. Never throws.
        /// </summary>
        /// <param name="key">The capture key.</param>
        internal static string MergeRefusal(string key)
        {
            try
            {
                var root = CapturesRootDir();
                if (root == null || !IsUsableKey(key)) return null;

                var path = Path.Combine(root, key, $"{key}.map.json");
                if (!File.Exists(path)) return null;

                var meta = JsonConvert.DeserializeObject<CaptureMeta>(File.ReadAllText(path));
                if (meta == null || meta.Floors == null || meta.Floors.Count == 0) return "the stored set cannot be read";

                return IsMenuSet(meta) ? null : "the stored set is a raid capture, not one from game files";
            }
            catch (Exception ex)
            {
                return $"the stored set could not be read ({ex.GetType().Name})";
            }
        }

        /// <summary>Stage M3: a write mode in the words the log and the Maps tab use.</summary>
        /// <param name="write">The mode.</param>
        internal static string MenuWriteText(MenuWriteMode write) =>
            write == MenuWriteMode.Replace ? "replacing the stored set"
            : write == MenuWriteMode.Merge ? "merging into a stored menu set, or starting one"
            : "the test set: merged when it can be, else replaced";

        /// <summary>Stage M3: the Maps tab's progress line, for a menu capture only - a raid capture never has a session,
        /// so this is a no-op there.</summary>
        /// <param name="text">A few words.</param>
        private void MenuPhase(string text)
        {
            if (_menu != null && _menu.MenuMode) MenuMapHost.SetPhase(text);
        }

        /// <summary>Stage M2: the mask a menu capture draws with - <see cref="CaptureMask"/> of every layer, exactly what
        /// <see cref="BuildCamera"/> builds in menu mode - for MenuMapHost.WakeHosted to decide what is geometry. The
        /// once-a-session layer line is left for the capture itself, as <see cref="ProbeCaptureMask"/> does.</summary>
        internal static int MenuCaptureMask()
        {
            var logged = _loggedLayers;

            try
            {
                _loggedLayers = true;
                return CaptureMask(~0);
            }
            finally
            {
                _loggedLayers = logged;
            }
        }

        private GameWorld _gameWorld;

        /// <summary>Stage M2: the main-menu capture this component was installed for (<see cref="InstallForMenu"/>), or null
        /// for a raid's. Every menu-mode branch in this file asks this, so a raid capture - which never has one - runs
        /// exactly as before.</summary>
        private MenuSession _menu;

        private bool _running;
        private bool _warnedOnPoll;

        /// <summary>Whether the capture now starting was asked for by the AUTOMATIC tick rather than by
        /// a key press or a campaign stop. The one thing it decides is the 3D mesh's cost gate (see
        /// <see cref="Plan.WantsMesh"/>); the pictures are taken the same way either way. Set before
        /// the coroutine starts and read once, in <see cref="Prepare"/>.</summary>
        private bool _automatic;

        /// <summary>This capture builds no 3D mesh (TryStartCapture's buildMesh).</summary>
        private bool _skipMesh;

        /// <summary>WP2: this capture is a campaign's last stop and, with MeshVerifyLastStop on, also builds the mesh from
        /// scratch into &lt;key&gt;-mesh.verify.bin for tools/compare-mesh.py (TryStartCapture's verifyMesh).</summary>
        private bool _verifyMesh;

        /// <summary>Campaign speed step 1 (4): a capture campaign is running (<see cref="CampaignBegins"/> ..
        /// <see cref="CampaignEnds"/>), and which one - the relief cast at its first stop is reused by its later stops
        /// (MapMeshBuilder.Request.ReliefSession). Static: MapCampaign is not this component, and a campaign outlives none of
        /// the raid's captures. False (every capture casts its own relief, as before) until MapCampaign says otherwise.</summary>
        private static bool _inCampaign;

        private static int _campaignSession;

        /// <summary>Campaign speed step 1 (4, review): the session a build may store or reuse the relief for - the running
        /// campaign's, or 0. A build that outlives its campaign sees 0 (or a newer session) and stores nothing.</summary>
        internal static int LiveCampaignSession => _inCampaign ? _campaignSession : 0;

        /// <summary>Campaign speed step 1 (4): MapCampaign calls this when a campaign starts, before its first stop's capture.
        /// A new session: the first stop casts the relief and every later stop of the same campaign, in the same raid,
        /// reuses it (colliders never stream out - see MapMeshBuilder's header).</summary>
        /// <param name="stops">Campaign speed step 2: the stops the campaign planned, for the checkpoint lines ("stop N of M").</param>
        internal static void CampaignBegins(int stops = 0)
        {
            unchecked { _campaignSession++; }
            if (_campaignSession == 0) _campaignSession = 1;

            _inCampaign = true;
            MapMeshBuilder.ForgetRelief();
            ForgetEmptyCounts();

            // Campaign speed step 2: a new session holds nothing yet - the first stop loads from disk. A hold a previous
            // campaign left (CampaignEnds hands it to a write, so none should be here) is let go with its lost stops said.
            if (_hold != null) DropHold(_hold, "a new campaign started", raidEnded: false);
            _hold = null;
            _holdOff = false;
            _campaignStop = 0;
            _campaignStopCount = Math.Max(0, stops);
        }

        /// <summary>Campaign speed step 1 (4): MapCampaign calls this whenever a campaign ends - done, cut short, cancelled,
        /// or the raid gone. Later captures cast their own relief again, and the held grids are let go.
        ///
        /// Campaign speed step 2: and the held session with them. The campaign's own ends (done, early for time, cancelled,
        /// a death) have written their last checkpoint before they get here (MapCampaign's CampaignCheckpoint); what is still
        /// unsaved now - the raid ended between two stops, or a checkpoint wait gave up - is written on a worker thread
        /// (<see cref="StartFlush"/>), which the scene's unload does not stop. A stop cut off mid-way by the raid leaves a
        /// hold that may mix two stops, and that is never written: its unsaved stops are said to be lost.</summary>
        internal static void CampaignEnds()
        {
            _inCampaign = false;
            MapMeshBuilder.ForgetRelief();
            ForgetEmptyCounts();

            var hold = _hold;
            _hold = null;
            _holdOff = false;

            if (hold == null) return;

            // Already being written (a checkpoint the campaign stopped waiting for): the write owns the hold now, and the
            // plugin object finishes it if nothing here is left to.
            if (_flush != null && !_flush.Completed && ReferenceEquals(_flush.Hold, hold))
            {
                // (review) nothing holds on to this hold after the write: its arrays go the moment it ends
                _flush.Detached = true;
                EnsureFlushPoller(_flush);
                return;
            }

            if (hold.Unsaved == 0 || hold.Meta == null)
            {
                SweepHeldPages(hold);
                return;
            }

            if (hold.StopInProgress)
            {
                DropHold(hold, "the raid ended in the middle of a stop, so what is held may mix two stops", raidEnded: true);
                return;
            }

            var job = StartFlush(hold, "the campaign's end", detached: true);
            if (job == null) return;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: writing {hold.Key}'s {hold.Unsaved.ToString(CultureInfo.InvariantCulture)} stop(s) since the last " +
                "checkpoint on a worker thread - the raid's end does not stop it, closing the game before it finishes would.");

            EnsureFlushPoller(job);
        }

        /// <summary>Campaign speed step 2: MapCampaign calls this before each stop's capture, so the checkpoint line can
        /// name the stop it followed.</summary>
        /// <param name="stop">The stop about to be captured, 1-based.</param>
        internal static void CampaignStopIs(int stop) => _campaignStop = Math.Max(0, stop);

        // --- campaign speed step 2: the campaign session that writes at checkpoints -----------------

        /// <summary>
        /// Campaign speed step 2, the rollback: true - inside a capture campaign the stored pictures, their distance sidecars
        /// and the stored 3D mesh are loaded ONCE, at the campaign's first stop, and kept in memory (<see cref="CampaignHold"/>);
        /// every later stop merges into those copies, and the set's files - pictures, sidecars, mesh, index, atlas pages and
        /// meta, all of one stop - are written only at a checkpoint: every <see cref="CampaignCheckpointStops"/> stops, at
        /// the campaign's end (done, early for time, cancelled), before the extract teleport, and on a worker when the raid
        /// ends with stops unsaved. False: every stop loads and writes its files, as before. A capture outside a campaign is
        /// never held either way. Static readonly rather than const, like this file's other switches, so the path it turns
        /// off still compiles clean.
        /// </summary>
        private static readonly bool CampaignSessionWrites = true;

        /// <summary>Campaign speed step 2: stops held in memory between two checkpoints. Four, not eight: a raid can end
        /// abruptly - a death screen clicked through, a disconnect, the game closed - and until a checkpoint the stops since
        /// the last one exist nowhere but in this process.</summary>
        private const int CampaignCheckpointStops = 4;

        /// <summary>Campaign speed step 2: the most the held copies may be ESTIMATED at, when the campaign's first stop takes
        /// its hold, before that campaign falls back to writing every stop. 3 GiB. Rollback: 1.5 GiB (3L &lt;&lt; 29).
        /// Interchange - five floors, four sides and its mesh - estimates at about 1.5 GB, Customs at about 0.6 GB.</summary>
        private const long CampaignHoldMaxBytes = 3L << 30;

        /// <summary>Campaign speed step 2: what one held pixel costs - its colour (four bytes) and its distance step.</summary>
        private const long HeldBytesPerPixel = 5L;

        /// <summary>Campaign speed step 2, the estimate's mesh terms, measured on Interchange's stop 11: 272 MB of building
        /// arrays for 11.1 million triangles (24.5 B) with the ranges and objects on top, and 51 MB of relief for 17.9 million
        /// cells; and the mesh assumed when there is no stored one to measure yet.</summary>
        private const long HeldMeshBytesPerTriangle = 26L;

        private const long HeldMeshBytesPerCell = 3L;

        private const long HeldMeshNominalBytes = 400L << 20;

        /// <summary>Campaign speed step 2: seconds a stop waits for its checkpoint's write before it lets the stop end anyway
        /// (the write goes on, on its worker, and the campaign waits for it before the next stop). Parallel encodes of
        /// Interchange's nine pictures and its mesh deflate are tens of seconds; this is a ceiling for a stuck disk. In
        /// <see cref="WorstCaseSeconds"/>.</summary>
        internal const double CheckpointWaitSeconds = 180d;

        /// <summary>Campaign speed step 2: a floor's or a side's picture and distance sidecar, held between stops - exactly what
        /// the next stop would have decoded from the two PNGs (both are lossless), in texture order.</summary>
        private sealed class HeldPicture
        {
            public string File;
            public string DistFile;
            public int Width;
            public int Height;
            public Color32[] Pixels;
            public byte[] Dist;

            /// <summary>Changed since the last checkpoint wrote it (or never written): the next checkpoint encodes it.</summary>
            public bool Dirty;

            public long Bytes => (Pixels?.LongLength ?? 0L) * 4L + (Dist?.LongLength ?? 0L);
        }

        /// <summary>Campaign speed step 2: the 3D mesh and its identity sidecar, held between stops - the last stop's build,
        /// which the next stop's build takes as its base instead of reading, hashing and inflating the stored file.</summary>
        private sealed class HeldMesh
        {
            public MapMeshFile File;
            public MapMeshIndex Index;
            public long Cells;
            public long Triangles;
            public long ReliefBytes;
            public long BuildingBytes;
            public bool Accumulated;

            /// <summary>The mesh differs from the file on disk: the next checkpoint serialises it.</summary>
            public bool Dirty;

            /// <summary>Only the sidecar differs (a recorded attempt, the mesh unchanged): the next checkpoint writes it alone.</summary>
            public bool IndexDirty;

            /// <summary>Campaign speed step 3: the standing points of the captures built into this mesh (<see cref="Stood"/>).</summary>
            public List<CaptureStand> Stands;
        }

        /// <summary>
        /// Campaign speed step 2: one campaign's set, held in memory between checkpoints - for ONE map, in ONE session (raid).
        /// <see cref="Meta"/> is the meta the last finished stop would have written; the next stop merges into it exactly as it
        /// would have merged into the file (LoadPrevious' checks included). Pictures a stop did not load come from disk as
        /// before, and the files on disk are always the last checkpoint's - complete and of one stop.
        /// </summary>
        private sealed class CampaignHold
        {
            public string Key;
            public string Dir;
            public int Session;

            /// <summary>The held set's meta: the last finished stop's, or null before the first stop finishes. Its mesh block
            /// names the file on disk while the held mesh is clean, and is a placeholder (no length, no hash) while it is dirty.</summary>
            public CaptureMeta Meta;

            /// <summary>The mesh block and atlas list the meta on disk names - what a checkpoint writes when the held mesh cannot be.</summary>
            public CaptureMesh WrittenMesh;

            public List<CaptureAtlas> WrittenAtlas;

            public readonly Dictionary<string, HeldPicture> Pictures =
                new Dictionary<string, HeldPicture>(StringComparer.OrdinalIgnoreCase);

            public HeldMesh Mesh;

            /// <summary>Atlas pages a held stop encoded, by page: files beside the set under <see cref="HeldPageSuffix"/>, which
            /// nothing reads but the next stop's build (MeshRequest.AtlasPagePath) and the checkpoint that commits them.</summary>
            public readonly Dictionary<int, string> PendingPages = new Dictionary<int, string>();

            /// <summary>Stops held since the last checkpoint.</summary>
            public int Unsaved;

            /// <summary>Campaign speed step 2 (review): an allocation of a held stop failed for memory - the stop is written
            /// at its end and the campaign writes every stop from there (<see cref="NoteOutOfMemory"/>).</summary>
            public bool OutOfMemory;

            /// <summary>Campaign speed step 2 (review): the meta on disk - what a checkpoint carries a picture's entry from when
            /// that picture cannot be written (over the size cap, a side that will not commit).</summary>
            public CaptureMeta WrittenMeta;

            /// <summary>A stop has started changing the held copies and has not finished: they may mix two stops. Never written
            /// in this state; the Run that set it clears it at the stop's end or drops the hold.</summary>
            public bool StopInProgress;

            public long Estimate;
        }

        /// <summary>Campaign speed step 2 (review): a capture allocation that failed for memory inside a held campaign - the
        /// held copies are part of why, so the campaign stops holding: this stop is written at its end (Run) and every later
        /// stop writes as before (<see cref="_holdOff"/>).</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="ex">What the allocation threw.</param>
        private static void NoteOutOfMemory(Plan plan, Exception ex)
        {
            if (plan?.Hold == null || !(ex is OutOfMemoryException)) return;

            plan.Hold.OutOfMemory = true;
            _holdOff = true;
        }

        /// <summary>A held atlas page's file: <c>&lt;key&gt;-atlas-&lt;n&gt;.png.held</c> - not a .png, so no reader, the stale
        /// sweep or tools/check-capture.py sees it, and not .tmp, so DropStaged leaves it.</summary>
        private const string HeldPageSuffix = ".held";

        /// <summary>The running campaign's hold, or null (outside a campaign, before its first stop, or writing every stop).</summary>
        private static CampaignHold _hold;

        /// <summary>This campaign writes every stop, as before: over the memory cap, the managed encoder off, or a checkpoint
        /// that failed. Reset by CampaignBegins and CampaignEnds.</summary>
        private static bool _holdOff;

        private static int _campaignStop;

        private static int _campaignStopCount;

        /// <summary>Campaign speed step 2: one checkpoint's write - the snapshot a worker writes from, and what it came to.</summary>
        private sealed class HeldFlush
        {
            public CampaignHold Hold;
            public string Key;
            public string Dir;
            public string Why;
            public int Stop;
            public int Unsaved;
            public CaptureMeta Meta;
            public List<HeldPicture> Pictures;
            public HeldMesh Mesh;
            public bool WriteMesh;
            public bool WriteIndex;
            public CaptureMesh WrittenMesh;
            public List<CaptureAtlas> WrittenAtlas;
            public List<KeyValuePair<int, string>> Pages;
            public Stopwatch Clock;
            public Task Task;

            // what the worker came to
            public bool Written;
            public string Failed;
            public long Bytes;
            public bool MeshWritten;
            public string MeshWhy;
            public CaptureMesh NewMesh;
            /// <summary>In: the meta on disk, which a picture that cannot be written is carried from. Out: the meta this write
            /// put down.</summary>
            public CaptureMeta WrittenMeta;

            public HashSet<string> Keep;

            /// <summary>Review: the pictures this write committed (the rest stay dirty), whether the sidecar alone went in
            /// place, whether the atlas ended early, and what the main thread says about it.</summary>
            public readonly List<HeldPicture> WrittenPictures = new List<HeldPicture>();

            public bool IndexWritten;
            public bool IndexFailed;
            public bool PagesCut;
            public readonly List<string> Notes = new List<string>();

            /// <summary>Review: nothing holds on to this hold after the write - its arrays go when it ends.</summary>
            public volatile bool Detached;

            // the main thread's side
            public bool Completed;
            public bool Polled;
            public readonly List<Action> Then = new List<Action>();
        }

        /// <summary>The checkpoint write in flight or last finished, or null.</summary>
        private static HeldFlush _flush;

        /// <summary>Campaign speed step 2 (review): seconds the last checkpoint took - what a campaign bounds its wait for a
        /// running write by, and adds to the raid-time guard's need.</summary>
        internal static double LastCheckpointSeconds;

        /// <summary>Campaign speed step 2: a checkpoint's write is running - no capture may start (Prepare refuses) and a
        /// campaign waits before its next stop.</summary>
        internal static bool CampaignWriting
        {
            get
            {
                PollFlush();
                return _flush != null && !_flush.Completed;
            }
        }

        /// <summary>Campaign speed step 2: runs <paramref name="then"/> on the main thread once the checkpoint being written
        /// has finished - how a campaign's upload hold outlives its last write - and says whether one was running (false:
        /// the caller goes ahead now).</summary>
        /// <param name="then">What to do once the files are down.</param>
        internal static bool WhenCampaignWritten(Action then)
        {
            PollFlush();

            var job = _flush;
            if (job == null || job.Completed || then == null) return false;

            job.Then.Add(then);
            EnsureFlushPoller(job);
            return true;
        }

        /// <summary>
        /// Campaign speed step 2: writes the held stops now and waits for the write (a frame at a time, up to
        /// <see cref="CheckpointWaitSeconds"/>; past it the write finishes on its own). Nothing when nothing is unsaved. What a
        /// stop runs every <see cref="CampaignCheckpointStops"/> stops and what MapCampaign runs at its end, before the
        /// extract teleport. Never throws.
        /// </summary>
        /// <param name="why">For the line: why this checkpoint.</param>
        internal static IEnumerator CampaignCheckpoint(string why)
        {
            PollFlush();

            var job = _flush != null && !_flush.Completed ? _flush : null;

            // Another hold's write still running (the last raid's end): this hold's stops wait behind it.
            if (job != null && !ReferenceEquals(job.Hold, _hold))
            {
                var behind = Stopwatch.StartNew();
                while (!job.Task.IsCompleted && behind.Elapsed.TotalSeconds < CheckpointWaitSeconds) yield return null;

                if (!job.Task.IsCompleted)
                {
                    EnsureFlushPoller(job);
                    yield break;
                }

                CompleteFlush(job);
                job = null;
            }

            if (job == null)
            {
                var hold = _hold;
                if (hold == null || hold.Unsaved == 0 || hold.Meta == null) yield break;

                // An upload of this map reading a file on a worker: the commit waits for it, as a stop's does (WP3 Phase A).
                var reading = Stopwatch.StartNew();
                while (MapTransfer.IsReadingCapture(hold.Key) && reading.Elapsed.TotalSeconds < CommitWaitSeconds)
                    yield return null;

                if (!ReferenceEquals(_hold, hold)) yield break;

                job = StartFlush(hold, why);
                if (job == null) yield break;
            }

            var clock = Stopwatch.StartNew();

            while (!job.Task.IsCompleted && clock.Elapsed.TotalSeconds < CheckpointWaitSeconds)
                yield return null;

            if (job.Task.IsCompleted)
            {
                CompleteFlush(job);
                yield break;
            }

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the checkpoint of {job.Key} has been writing for {CheckpointWaitSeconds:0} s - it finishes on its " +
                "own, and the campaign waits for it before the next stop.");
            EnsureFlushPoller(job);
        }

        /// <summary>
        /// Campaign speed step 2 (review): the campaign's END write - started, not waited for. The held set cannot change after
        /// the last stop, so the player goes to the extract at once while a worker writes; the upload hold's release waits for
        /// the write (<see cref="WhenCampaignWritten"/>), and CampaignEnds, which follows, marks it detached. True when a write
        /// is running or was started. Never throws.
        /// </summary>
        /// <param name="why">For the line.</param>
        internal static bool StartCampaignWrite(string why)
        {
            try
            {
                PollFlush();

                if (_flush != null && !_flush.Completed)
                {
                    EnsureFlushPoller(_flush);
                    return true;
                }

                var hold = _hold;
                if (hold == null || hold.Unsaved == 0 || hold.Meta == null || hold.StopInProgress) return false;

                var job = StartFlush(hold, why);
                if (job == null) return false;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: writing {hold.Key}'s {hold.Unsaved.ToString(CultureInfo.InvariantCulture)} stop(s) since the last " +
                    $"checkpoint on a worker thread ({why}) - the campaign does not wait for it.");

                EnsureFlushPoller(job);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the campaign's last write could not be started ({ex.Message}).");
                return false;
            }
        }

        /// <summary>Campaign speed step 2 (review): finishes a checkpoint whose worker is done, from any main-thread Update that
        /// ticks - TrackerHotkey's, whose root canvas is proven to tick in the menu, where the raid's end write lands.</summary>
        internal static void PollCampaignWrite()
        {
            try
            {
                PollFlush();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: a campaign checkpoint could not be finished ({ex.Message}).");
            }
        }

        /// <summary>Campaign speed step 2: the running campaign's hold for this map, or null.</summary>
        /// <param name="key">The map.</param>
        private static CampaignHold LiveHold(string key)
        {
            var hold = _hold;
            var live = LiveCampaignSession;

            if (hold == null || live == 0 || hold.Session != live) return null;
            return string.Equals(hold.Key, key, StringComparison.OrdinalIgnoreCase) ? hold : null;
        }

        /// <summary>
        /// Campaign speed step 2: takes a hold at a campaign's first stop - or null, writing every stop as before: outside a
        /// campaign, with the rollback off, when this campaign has already fallen back, with the managed PNG encoder off (a
        /// checkpoint encodes on a worker, where Unity's encoder cannot run), or when the estimate is over
        /// <see cref="CampaignHoldMaxBytes"/> - which is said once, with the numbers. Stale held atlas pages an earlier
        /// session left are swept. Never throws.
        /// </summary>
        /// <param name="plan">The first stop's plan, <see cref="Plan.Previous"/> already read from disk.</param>
        private static CampaignHold NewHold(Plan plan)
        {
            try
            {
                var live = LiveCampaignSession;
                if (!CampaignSessionWrites || live == 0 || _holdOff || _hold != null) return null;

                if (!ManagedPngEncode || _managedPngOff)
                {
                    _holdOff = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: this campaign writes {plan.Key} at every stop - the managed PNG encoder is off, and a " +
                        "checkpoint encodes on a worker thread, where Unity's encoder cannot run.");
                    return null;
                }

                var estimate = EstimateHold(plan, out var parts);

                if (estimate > CampaignHoldMaxBytes)
                {
                    _holdOff = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: this campaign writes {plan.Key} at every stop - holding it between checkpoints is estimated at " +
                        $"{Mb(estimate)} MB ({parts}), over the {Mb(CampaignHoldMaxBytes)} MB cap.");
                    return null;
                }

                // What the meta on disk names and a checkpoint falls back to - by WriteMeta's own carry rules (the format
                // version, the file still there, every page still there).
                var writtenMesh = CarriedMesh(plan, null);

                var hold = new CampaignHold
                {
                    Key = plan.Key,
                    Dir = plan.Dir,
                    Session = live,
                    WrittenMesh = writtenMesh,
                    WrittenAtlas = writtenMesh != null ? CarriedAtlas(plan) : null,
                    WrittenMeta = plan.Previous,
                    Estimate = estimate,
                };

                SweepHeldPages(hold);
                _hold = hold;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: this campaign holds {plan.Key} in memory between checkpoints (every " +
                    $"{CampaignCheckpointStops.ToString(CultureInfo.InvariantCulture)} stops, at its end and before the extract) - " +
                    $"estimated {Mb(estimate)} MB ({parts}) of the {Mb(CampaignHoldMaxBytes)} MB cap.");

                return hold;
            }
            catch (Exception ex)
            {
                _holdOff = true;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan?.Key} could not be held between stops ({ex.GetType().Name}: {ex.Message}) - this campaign " +
                    "writes every stop.");
                return null;
            }
        }

        /// <summary>
        /// Campaign speed step 2: what holding this map between stops will cost - every floor's picture and sidecar at five
        /// bytes a pixel; the sides already on disk (scaled to four), or about one floor's pixels when there are none yet
        /// (Interchange's four are 1.2 of its floors, Customs' 0.8 of its one); and the stored mesh's triangles and cells, or
        /// <see cref="HeldMeshNominalBytes"/> without one. Interchange's set today: 944 + 220 + 343 MB; Customs': 193 + 159 +
        /// 267 MB.
        /// </summary>
        /// <param name="plan">The first stop's plan.</param>
        /// <param name="parts">The three terms, for the line.</param>
        private static long EstimateHold(Plan plan, out string parts)
        {
            var floorPixels = (long)plan.WidthPx * plan.HeightPx * plan.Floors.Count;

            var sidePixels = 0L;
            var sides = plan.Previous?.Sides?.Where(s => s != null && s.Width > 0 && s.Height > 0).ToList();

            if (sides != null && sides.Count > 0)
            {
                foreach (var side in sides) sidePixels += (long)side.Width * side.Height;
                sidePixels = sidePixels * MapSideView.Directions.Length / sides.Count;
            }
            else
            {
                sidePixels = (long)plan.WidthPx * plan.HeightPx;
            }

            var stored = plan.Previous?.Mesh;
            var mesh = stored != null && stored.Triangles > 0
                ? stored.Triangles * HeldMeshBytesPerTriangle + stored.Cells * HeldMeshBytesPerCell
                : HeldMeshNominalBytes;

            parts = $"floors {Mb(floorPixels * HeldBytesPerPixel)} MB, sides {Mb(sidePixels * HeldBytesPerPixel)} MB, " +
                    $"mesh {Mb(mesh)} MB{(stored == null ? " assumed" : "")}";

            return (floorPixels + sidePixels) * HeldBytesPerPixel + mesh;
        }

        /// <summary>Campaign speed step 2: what the hold keeps alive now - the pictures and sidecars and the mesh's arrays.</summary>
        /// <param name="hold">The hold.</param>
        private static long HeldBytes(CampaignHold hold)
        {
            if (hold == null) return 0L;

            var total = 0L;
            foreach (var picture in hold.Pictures.Values) total += picture.Bytes;

            try
            {
                if (hold.Mesh?.File != null) total += hold.Mesh.File.ApproximateBytes();
            }
            catch
            {
                // a mesh that cannot count itself is left out of a log line's number
            }

            return total;
        }

        /// <summary>Campaign speed step 2: the held picture for a file of this size, or null.</summary>
        /// <param name="plan">The plan (a side's own plan for a side), carrying the hold.</param>
        /// <param name="file">The picture's file name.</param>
        private static HeldPicture HeldPictureOf(Plan plan, string file)
        {
            if (plan?.Hold == null || string.IsNullOrEmpty(file)) return null;
            if (!plan.Hold.Pictures.TryGetValue(file, out var held) || held.Pixels == null || held.Dist == null) return null;

            return held.Width == plan.WidthPx && held.Height == plan.HeightPx &&
                   held.Pixels.Length == plan.WidthPx * plan.HeightPx && held.Dist.Length == held.Pixels.Length
                ? held
                : null;
        }

        /// <summary>Campaign speed step 2: whether a picture the meta may name is there - held in memory, or on disk. The held
        /// copy counts: in a held session the meta describes the held set, which a checkpoint writes whole.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="name">The file name inside the capture's folder.</param>
        private static bool PictureExists(Plan plan, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (plan?.Hold != null && plan.Hold.Pictures.ContainsKey(name)) return true;

            return File.Exists(Path.Combine(plan.Dir, name));
        }

        /// <summary>Campaign speed step 2: takes a picture and its sidecar into the hold - the one place a held entry changes.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        /// <param name="pixels">The picture, texture order.</param>
        /// <param name="dist">Its distances.</param>
        /// <param name="dirty">It differs from the file on disk.</param>
        /// <returns>The array it displaced, or null.</returns>
        private static Color32[] HoldPicture(Plan plan, FloorPlan floor, Color32[] pixels, byte[] dist, bool dirty)
        {
            var hold = plan.Hold;
            hold.StopInProgress = true;

            if (!hold.Pictures.TryGetValue(floor.File, out var held))
            {
                held = new HeldPicture { File = floor.File };
                hold.Pictures[floor.File] = held;
            }

            var displaced = held.Pixels;

            held.DistFile = floor.DistFile;
            held.Width = plan.WidthPx;
            held.Height = plan.HeightPx;
            held.Pixels = pixels;
            held.Dist = dist;
            held.Dirty = dirty || held.Dirty && ReferenceEquals(displaced, pixels);

            return ReferenceEquals(displaced, pixels) ? null : displaced;
        }

        /// <summary>
        /// Campaign speed step 2: a developed floor, instead of its encode, sidecar and stage, is STASHED on the floor - the
        /// plan's pool with it, so the next floor develops into a pool of its own - and taken into the hold only once the
        /// floor loop is over (<see cref="HoldStashedFloors"/>), after every floor's light test (review): a refusal then
        /// leaves the hold exactly as it was. The "captured" line says the floor is held.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, developed.</param>
        private void StashFloor(Plan plan, FloorPlan floor)
        {
            try
            {
                if (floor.Rgba == null || floor.Dist == null || floor.Exposure == null ||
                    floor.Rgba.Length != plan.WidthPx * plan.HeightPx || floor.Dist.Length != floor.Rgba.Length)
                {
                    floor.Failed = true;
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" has no developed picture to hold.");
                    return;
                }

                floor.StashPixels = floor.Rgba;
                floor.StashDist = floor.Dist;
                floor.StashDirty = true;

                if (ReferenceEquals(plan.RgbaPool, floor.Rgba)) plan.RgbaPool = null;
                floor.Rgba = null;
                floor.Dist = null;

                var bytes = (long)floor.StashPixels.Length * HeldBytesPerPixel;
                RecordFloor(plan, floor, bytes, null, null,
                    $", held in memory for the next checkpoint ({Mb(bytes)} MB)", held: true);
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                floor.StashPixels = null;
                floor.StashDist = null;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be held ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Campaign speed step 2 (review): the stashed floors into the hold, once the floor loop has ended unrefused -
        /// a developed one dirty, an unchanged one as it is (clean when it came from disk at the campaign's first stop).
        /// A refused stop drops the stashes instead, and the hold is as the last stop left it.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static void HoldStashedFloors(Plan plan)
        {
            foreach (var floor in plan.Floors)
            {
                var pixels = floor.StashPixels;
                var dist = floor.StashDist;
                var dirty = floor.StashDirty;

                floor.StashPixels = null;
                floor.StashDist = null;

                if (pixels == null || dist == null || plan.Hold == null || plan.Refused || floor.Failed) continue;

                var held = HeldPictureOf(plan, floor.File);
                if (!dirty && held != null && ReferenceEquals(held.Pixels, pixels)) continue;

                HoldPicture(plan, floor, pixels, dist, dirty);
            }
        }

        /// <summary>Campaign speed step 2: a developed side into the hold, as <see cref="StashFloor"/> would a floor - its entry for the meta
        /// and its line are RecordSide's. False, having said why, when it is not held.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side, developed.</param>
        private static bool HoldSide(Plan plan, SideView view)
        {
            var floor = view.Floor;
            var side = view.Plan;

            try
            {
                if (floor.Rgba == null || floor.Dist == null)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: {plan.Key} side view {view.Dir} has no developed picture to hold.");
                    return false;
                }

                var drawn = 0;
                if (floor.Drawn != null)
                    foreach (var d in floor.Drawn)
                        if (d) drawn++;

                var pixels = floor.Rgba;
                HoldPicture(side, floor, pixels, floor.Dist, dirty: true);

                side.RgbaPool = null;
                floor.Rgba = null;
                floor.Dist = null;

                var bytes = (long)pixels.Length * HeldBytesPerPixel;
                var held = RecordSide(plan, view, bytes, null, drawn, floor.Drawn?.Length ?? pixels.Length, null,
                    $", held in memory for the next checkpoint ({Mb(bytes)} MB)", held: true);

                var entry = plan.Sides.LastOrDefault(s => s.Dir == view.Dir);
                if (entry != null) entry.DistStale = false;

                return held;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {view.Dir} could not be held ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>
        /// Campaign speed step 2: an unchanged floor or side (no tile rendered, the stored picture and sidecar loaded) is
        /// held as it is - a copy loaded from disk at the campaign's first stop is taken in CLEAN (it is the file), one
        /// already held stays as it was. The length the "was it written" gates read is the held size.
        /// </summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private static long HoldUnchanged(Plan plan, FloorPlan floor)
        {
            var held = HeldPictureOf(plan, floor.File);

            if (held == null || !ReferenceEquals(held.Pixels, floor.PreviousColour))
            {
                HoldPicture(plan, floor, floor.PreviousColour, floor.UnhealedDist ?? floor.PreviousDist, dirty: false);
                held = HeldPictureOf(plan, floor.File);
            }

            return held?.Bytes ?? 0L;
        }

        /// <summary>
        /// Campaign speed step 2: the stop's build into the hold instead of its serialise and stage. The build took the held
        /// mesh as its base and changed its buildings in place (MapMeshBuilder's merge), so a build that produced no file
        /// may have left that base half-changed: the held mesh is then let go (<see cref="RevertHeldMesh"/>) and the next
        /// stop reads the stored one again. A build that changed nothing keeps the held mesh's state.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">The build's result.</param>
        private static void HoldMesh(Plan plan, MapMeshBuilder.Result mesh)
        {
            var hold = plan.Hold;

            if (mesh?.File == null)
            {
                if (hold.Mesh != null) RevertHeldMesh(hold, "the build that took it as its base produced nothing");
                return;
            }

            hold.StopInProgress = true;

            var was = hold.Mesh;
            var dirty = !mesh.Unchanged || (was?.Dirty ?? false) || hold.WrittenMesh == null;

            hold.Mesh = new HeldMesh
            {
                File = mesh.File,
                Index = mesh.Index,
                Cells = mesh.Cells,
                Triangles = mesh.Triangles,
                ReliefBytes = mesh.ReliefBytes,
                BuildingBytes = mesh.BuildingBytes,
                Accumulated = mesh.Accumulated,
                Dirty = dirty,
                IndexDirty = mesh.IndexChanged || (was?.IndexDirty ?? false),

                // Campaign speed step 3: built onto the held mesh, its points; onto the stored one (none held yet, or the
                // held one let go), the stored meta's; from scratch, this capture's alone
                Stands = Stood(was != null ? was.Stands : hold.WrittenMeta?.Stands, plan, true, mesh.Accumulated),
            };

            plan.MeshHeld = true;
            plan.MeshLevels = new HashSet<int>();
            foreach (var band in mesh.File.Bands) plan.MeshLevels.Add(band.Level);

            // WP2 (2.12)'s one-shot rebuild is spent once a rebuilt mesh is held - or every held stop would rebuild again
            if (!mesh.Accumulated && ModSettings.MeshRebuildNext != null && ModSettings.MeshRebuildNext.Value)
                ModSettings.MeshRebuildNext.Value = false;

            Plugin.LogSource?.LogInfo(mesh.Unchanged
                ? $"QuestTree: {plan.Key} built no new 3D geometry this time - the held mesh is kept."
                : $"QuestTree: mesh for {plan.Key} held in memory for the next checkpoint - {MB(mesh.ReliefBytes)} MB relief + " +
                  $"{MB(mesh.BuildingBytes)} MB buildings" + (mesh.Accumulated ? " (accumulated)." : " (from scratch)."));
        }

        /// <summary>Campaign speed step 2: the held mesh and its held atlas pages let go - the meta goes back to naming the mesh
        /// and pages on disk, and the next stop's build reads the stored mesh as a stop outside a campaign does.</summary>
        /// <param name="hold">The hold.</param>
        /// <param name="why">For the line.</param>
        private static void RevertHeldMesh(CampaignHold hold, string why)
        {
            if (hold == null) return;

            var had = hold.Mesh != null && (hold.Mesh.Dirty || hold.Mesh.IndexDirty);

            hold.Mesh = null;
            SweepHeldPages(hold);

            if (hold.Meta != null)
            {
                hold.Meta.Mesh = hold.WrittenMesh;
                hold.Meta.Atlas = hold.WrittenMesh != null ? hold.WrittenAtlas : null;
            }

            Plugin.LogSource?.LogWarning(
                $"QuestTree: {hold.Key}'s held 3D mesh is let go - {why}; " +
                (had
                    ? "what the stops since the last checkpoint added to it is lost, and the next stop builds onto the stored mesh."
                    : "the next stop builds onto the stored mesh."));
        }

        /// <summary>Campaign speed step 2: the held atlas pages' files deleted and forgotten. Also sweeps any an earlier session
        /// left (a game closed between two checkpoints). Never throws.</summary>
        /// <param name="hold">The hold.</param>
        private static void SweepHeldPages(CampaignHold hold)
        {
            if (hold == null) return;

            hold.PendingPages.Clear();

            try
            {
                if (hold.Dir == null || !Directory.Exists(hold.Dir)) return;

                foreach (var file in Directory.GetFiles(hold.Dir, $"{hold.Key}-atlas-*.png{HeldPageSuffix}"))
                    DeleteQuietly(file);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: {hold.Key}'s held atlas pages could not be swept ({ex.Message}).");
            }
        }

        /// <summary>Campaign speed step 2: where a held stop's atlas page waits for its checkpoint.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="name">The page's file name.</param>
        private static string HeldPagePath(Plan plan, string name) => Path.Combine(plan.Dir, name) + HeldPageSuffix;

        /// <summary>
        /// Campaign speed step 2: a hold let go without being written - a stop that did not finish (refused, an exception, the
        /// raid gone mid-stop), a meta that no longer fits, or a checkpoint that failed. The files on disk are the last
        /// checkpoint's, complete as they were written; the stops since are lost, and said so. The next stop of a campaign
        /// still running takes a new hold from disk.
        /// </summary>
        /// <param name="hold">The hold.</param>
        /// <param name="why">For the line.</param>
        /// <param name="raidEnded">The raid is over: the line says the stops were lost with it.</param>
        private static void DropHold(CampaignHold hold, string why, bool raidEnded)
        {
            if (hold == null) return;

            if (ReferenceEquals(_hold, hold)) _hold = null;

            var unsaved = hold.Unsaved + (hold.StopInProgress ? 1 : 0);
            hold.Unsaved = 0;
            hold.StopInProgress = false;
            SweepHeldPages(hold);

            if (unsaved == 0) return;

            Plugin.LogSource?.LogWarning(raidEnded
                ? $"QuestTree: {unsaved.ToString(CultureInfo.InvariantCulture)} stop(s) since the last checkpoint were lost with " +
                  $"the raid ({why}) - {hold.Key}'s files on disk are the last checkpoint's, complete as they were written."
                : $"QuestTree: {unsaved.ToString(CultureInfo.InvariantCulture)} stop(s) of {hold.Key} since the last checkpoint " +
                  $"were dropped ({why}) - the files on disk are the last checkpoint's, and the next stop starts again from them.");

            Journal(hold.Key, $"{unsaved.ToString(CultureInfo.InvariantCulture)} held stop(s) lost - {why}.");
        }

        /// <summary>
        /// Campaign speed step 2: the end of a held stop - what WriteMeta would have written, built and kept as the held meta
        /// instead of committed: the floors this stop took (developed or unchanged) or the entries it carries, the held mesh
        /// (a placeholder block while it differs from the file) or the stored one, the atlas the mesh names, the sides, and
        /// the raid's light and clock read NOW, while there is a raid to read them from. The stop's line says it is held.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="clock">Running since the capture started.</param>
        private void HoldMeta(Plan plan, Stopwatch clock)
        {
            var hold = plan.Hold;

            try
            {
                var written = plan.Floors.Where(f => !f.Failed && f.Bytes > 0).ToList();

                if (written.Count == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: no floor of {plan.Key} could be captured - nothing was held. The warnings above say why for each.");
                    return;
                }

                var floors = new List<CaptureFloor>();
                var carried = 0;
                var unchanged = 0;

                foreach (var floor in plan.Floors)
                {
                    CaptureFloor entry;

                    if (!floor.Failed && floor.Bytes > 0)
                    {
                        entry = Described(plan, floor);
                        if (floor.Unchanged) unchanged++;
                    }
                    else
                    {
                        entry = Carried(plan, floor);
                        if (entry == null) continue;

                        carried++;
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was not captured this time, so the picture an earlier " +
                            "stop left is kept and the meta goes on naming it.");
                    }

                    floors.Add(entry);
                }

                // The check that can fail, as WriteMeta's: a held mesh whose bands are not the floors the meta names is let go.
                if (plan.MeshHeld && !SameLevels(plan.MeshLevels, floors))
                    RevertHeldMesh(hold,
                        $"its relief covers floor(s) [{Levels(plan.MeshLevels)}] while the meta names [{Levels(floors)}]");

                CaptureMesh mesh;
                List<CaptureAtlas> atlas;

                if (hold.Mesh != null)
                {
                    mesh = hold.Mesh.Dirty
                        ? new CaptureMesh
                        {
                            File = plan.MeshFile,
                            Version = MapMeshFile.Version,
                            Cells = hold.Mesh.Cells,
                            Triangles = hold.Mesh.Triangles,
                        }
                        : hold.WrittenMesh;

                    atlas = plan.MeshHeld ? new List<CaptureAtlas>(plan.Atlas) : plan.Previous?.Atlas;
                }
                else
                {
                    mesh = CarriedMesh(plan, floors);
                    atlas = mesh != null ? CarriedAtlas(plan) : null;
                }

                if (atlas != null && atlas.Count == 0) atlas = null;

                var sides = HeldSides(plan);

                CaptureLighting lighting = null;
                try
                {
                    // Stage M2b: a menu-rig capture describes the rig it was lit by, not the (absent) raid's light
                    lighting = _rig != null ? MenuLighting(plan.Key) : ReadLighting();
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: the raid's light could not be read ({ex.GetType().Name}: {ex.Message}) - the capture carries none.");
                }

                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

                hold.Meta = new CaptureMeta
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
                    // Stage M2b (review): a campaign stop that merged into a menu set keeps it one, as WriteMeta does
                    Render = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.Render : RenderTag,
                    TimeOfDay = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.TimeOfDay : TimeOfDay(),
                    CapturedIn = plan.MenuMode || plan.IntoMenuSet ? MenuSetMarker : null,
                    Lighting = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.Lighting : lighting,
                    Floors = floors,
                    Labels = plan.Labels,
                    Mesh = mesh,
                    Atlas = mesh != null ? atlas : null,
                    Sides = sides,

                    // Campaign speed step 3: the held mesh's points, or the written one's when the meta names that
                    Stands = mesh == null ? null : hold.Mesh != null ? hold.Mesh.Stands : hold.WrittenMeta?.Stands,
                };

                hold.Unsaved++;
                hold.StopInProgress = false;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capture of {plan.Key} held for the next checkpoint - {written.Count} floor(s)" +
                    (unchanged > 0 ? $" ({unchanged} unchanged)" : "") +
                    (carried > 0 ? $", {carried} kept from an earlier stop" : "") +
                    (sides != null ? $", {sides.Count} side view(s)" : "") +
                    (hold.Mesh != null ? $", mesh {(hold.Mesh.Dirty ? "changed" : "as stored")}" : "") +
                    $", {hold.Unsaved.ToString(CultureInfo.InvariantCulture)} of {CampaignCheckpointStops.ToString(CultureInfo.InvariantCulture)} " +
                    $"stop(s) since the last checkpoint, {Ms(clock.Elapsed.TotalMilliseconds)} ms total.");
            }
            catch (Exception ex)
            {
                // StopInProgress stays set: Run's finally lets the hold go rather than keep a set this stop half-described.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture of {plan.Key} could not be held ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Campaign speed step 2: the side entries a held stop's meta names - CommitSides' per-direction rule with
        /// nothing committed: the side this stop held, else the earlier entry whose picture is held or on disk.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static List<CaptureSide> HeldSides(Plan plan)
        {
            var named = new List<CaptureSide>();
            var carried = CarriedSides(plan);
            var carriedCount = 0;

            foreach (var dir in MapSideView.Directions)
            {
                var chosen = plan.Sides.FirstOrDefault(s => s.Dir == dir);

                if (chosen == null)
                {
                    chosen = carried.FirstOrDefault(s => s.Dir == dir);
                    if (chosen != null) carriedCount++;
                }

                if (chosen != null) named.Add(chosen);
            }

            plan.SidesCarried = carriedCount;
            return named.Count == 0 ? null : named;
        }

        /// <summary>
        /// Campaign speed step 2: starts a checkpoint's write on a worker - or null when there is nothing to write or a write
        /// is already running. The snapshot is taken here, on the main thread: the held meta, the pictures that differ from
        /// disk, the held mesh when it does, and the held atlas pages. Nothing changes the held copies while it runs: a stop
        /// waits for its own checkpoint, a campaign for any before its next stop, Prepare refuses a capture, and a raid that
        /// has ended has no stop to run. The pixel counter is bumped before any file is replaced (WP3's supersede guard).
        /// </summary>
        /// <param name="hold">The hold to write.</param>
        /// <param name="why">For the line.</param>
        /// <param name="detached">Review: nothing holds on to the hold after this write - its arrays are let go when it ends.</param>
        private static HeldFlush StartFlush(CampaignHold hold, string why, bool detached = false)
        {
            PollFlush();

            if (_flush != null && !_flush.Completed) return null;
            if (hold == null || hold.Meta == null || hold.Unsaved == 0 || hold.StopInProgress) return null;

            try
            {
                // Only what the meta names: a held picture it does not (none today) would be staged and never committed.
                var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var floor in hold.Meta.Floors ?? new List<CaptureFloor>())
                    if (floor?.File != null) named.Add(floor.File);
                foreach (var side in hold.Meta.Sides ?? new List<CaptureSide>())
                    if (side?.File != null) named.Add(side.File);

                var job = new HeldFlush
                {
                    Hold = hold,
                    Key = hold.Key,
                    Dir = hold.Dir,
                    Why = why,
                    Stop = _campaignStop,
                    Unsaved = hold.Unsaved,
                    Meta = hold.Meta,
                    Pictures = hold.Pictures.Values
                        .Where(p => p.Dirty && p.Pixels != null && p.Dist != null && named.Contains(p.File) && !string.IsNullOrEmpty(p.DistFile))
                        .ToList(),
                    Mesh = hold.Mesh,
                    WriteMesh = hold.Mesh != null && hold.Mesh.Dirty,
                    WriteIndex = hold.Mesh != null && hold.Mesh.Index != null && (hold.Mesh.Dirty || hold.Mesh.IndexDirty),
                    WrittenMesh = hold.WrittenMesh,
                    WrittenAtlas = hold.WrittenAtlas,
                    WrittenMeta = hold.WrittenMeta,
                    Pages = hold.PendingPages.OrderBy(p => p.Key).ToList(),
                    Clock = Stopwatch.StartNew(),
                    Detached = detached,
                };

                Bump(hold.Key, shape: false);

                job.Task = Task.Run(() => WriteHeld(job));
                _flush = job;
                return job;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the checkpoint of {hold.Key} could not be started ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        /// <summary>One picture's two encodes, on a worker.</summary>
        private sealed class HeldEncode
        {
            public HeldPicture Picture;
            public PngEncoder.Result Png;
            public PngEncoder.Result Sidecar;
            public string Error;

            /// <summary>Over <see cref="MaxFloorPngBytes"/>: carried, not written (review) - not an error.</summary>
            public bool OverCap;
        }

        /// <summary>
        /// Campaign speed step 2, ON A WORKER - no Unity call, and no line but a failed delete's Debug one (the main thread says
        /// what came of it, from <see cref="HeldFlush.Notes"/>): a checkpoint's files, written as WriteMeta writes a stop's.
        /// Every changed picture and its sidecar encoded in parallel with the mesh's deflate and hash; everything staged; then
        /// committed in WriteMeta's order - the floors (picture, then sidecar), the mesh, its sidecar and its atlas pages, the
        /// sides - and the meta LAST, so a reader finds the last checkpoint's set or this one. As WriteMeta (review): a picture
        /// that will not encode fails the whole write before anything is committed; a picture over the size cap is left out
        /// and its entry carried from the meta on disk; a mesh that will not serialise or commit is left out and the meta
        /// names the stored one; a sidecar that will not commit is deleted; a page that will not commit ends the atlas there;
        /// a side that will not commit keeps the earlier one. The meta is then written naming exactly what is on disk.
        /// </summary>
        /// <param name="job">The snapshot.</param>
        private static void WriteHeld(HeldFlush job)
        {
            var staged = new List<string>();
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var movedPages = new List<KeyValuePair<string, string>>();

            try
            {
                // 1. the encodes and the deflate, side by side
                var encodes = job.Pictures.Select(p => new HeldEncode { Picture = p }).ToList();
                var tasks = new List<Task>();

                foreach (var e in encodes)
                {
                    var encode = e;
                    tasks.Add(Task.Run(() => EncodeHeld(encode)));
                }

                SerialisedMesh serialised = null;

                if (job.WriteMesh)
                {
                    var mesh = job.Mesh;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            serialised = SerialiseHeldMesh(mesh);
                        }
                        catch (Exception ex)
                        {
                            job.MeshWhy = $"it would not serialise ({ex.GetType().Name}: {ex.Message})";
                        }
                    }));
                }

                Task.WaitAll(tasks.ToArray());

                var bad = encodes.FirstOrDefault(e => e.Error != null);
                if (bad != null)
                {
                    job.Failed = $"{bad.Picture.File} would not encode - {bad.Error}";
                    return;
                }

                // 2. the meta this checkpoint writes: the held one - a picture over the size cap carried from the meta on disk,
                // as a floor the old path would not write was (Carried), or left out when there is none
                var meta = CopyMeta(job.Meta);
                var good = encodes.Where(e => !e.OverCap).ToList();
                var over = new HashSet<string>(encodes.Where(e => e.OverCap).Select(e => e.Picture.File), StringComparer.OrdinalIgnoreCase);
                var floorLeftOut = false;

                foreach (var e in encodes.Where(e => e.OverCap))
                    job.Notes.Add($"{e.Picture.File} encoded to {e.Png.Length.ToString(CultureInfo.InvariantCulture)} bytes, over the " +
                                  $"{(MaxFloorPngBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB a picture may take - it " +
                                  "is not written, and the one on disk is kept");

                if (over.Count > 0)
                {
                    var floors = new List<CaptureFloor>();

                    foreach (var floor in meta.Floors ?? new List<CaptureFloor>())
                    {
                        if (floor == null) continue;

                        if (!over.Contains(floor.File))
                        {
                            floors.Add(floor);
                            continue;
                        }

                        var was = job.WrittenMeta?.Floors?.FirstOrDefault(f => f != null && f.Level == floor.Level);
                        if (was != null && IsPlainFileName(was.File) && File.Exists(Path.Combine(job.Dir, was.File))) floors.Add(was);
                        else floorLeftOut = true;
                    }

                    meta.Floors = floors;
                    meta.Sides = CarriedOver(meta.Sides, over, job);
                }

                foreach (var e in good)
                {
                    var picture = Path.Combine(job.Dir, e.Picture.File);
                    staged.Add(picture);
                    Stage(picture, e.Png.Parts, e.Png.LastLength);
                    lengths[picture] = e.Png.Length;

                    var dist = Path.Combine(job.Dir, e.Picture.DistFile);
                    staged.Add(dist);
                    Stage(dist, e.Sidecar.Parts, e.Sidecar.LastLength);
                    lengths[dist] = e.Sidecar.Length;

                    job.WrittenPictures.Add(e.Picture);
                }

                // 3. the mesh. A floor left out changes the floors the meta names: a mesh whose bands are not those floors is
                // left out of the meta altogether (WriteMeta's SameLevels rule).
                var indexPath = Path.Combine(job.Dir, MapMeshIndex.FileNameFor(job.Key));
                var meshPath = Path.Combine(job.Dir, MapMeshFile.FileNameFor(job.Key));
                var indexStaged = false;
                var dropMesh = false;

                if (floorLeftOut && meta.Mesh != null)
                {
                    var levels = job.Mesh?.File?.Bands != null ? new HashSet<int>(job.Mesh.File.Bands.Select(b => b.Level)) : null;
                    if (levels == null || !SameLevels(levels, meta.Floors))
                    {
                        dropMesh = true;
                        job.MeshWhy = "a floor left out of this checkpoint means its bands are not the floors the meta names";
                    }
                }

                // The held pages go with the mesh that names them and never without it: one missing (swept by hand, say)
                // leaves the mesh out of this checkpoint rather than naming a page whose file is the older one.
                if (job.WriteMesh && serialised != null && !dropMesh)
                {
                    var missing = job.Pages.FirstOrDefault(p => p.Value == null || !File.Exists(p.Value));
                    if (missing.Value != null || job.Pages.Any(p => p.Value == null))
                    {
                        job.MeshWhy = $"its held atlas page {missing.Key.ToString(CultureInfo.InvariantCulture)} is missing";
                        serialised = null;
                    }
                }

                var meshStaged = false;

                if (job.WriteMesh && serialised != null && !dropMesh)
                {
                    staged.Add(meshPath);
                    Stage(meshPath, serialised.Bytes, serialised.Length);
                    lengths[meshPath] = serialised.Length;
                    meshStaged = true;

                    if (serialised.IndexBytes != null)
                    {
                        staged.Add(indexPath);
                        Stage(indexPath, serialised.IndexBytes);
                        lengths[indexPath] = serialised.IndexBytes.Length;
                        indexStaged = true;
                    }
                    else
                    {
                        job.Notes.Add($"the mesh has no identity sidecar this time ({serialised.IndexWhy}) - the next capture rebuilds it from scratch");
                    }

                    job.NewMesh = new CaptureMesh
                    {
                        File = MapMeshFile.FileNameFor(job.Key),
                        Bytes = serialised.Length,
                        Version = MapMeshFile.Version,
                        Cells = job.Mesh.Cells,
                        Triangles = job.Mesh.Triangles,
                        Sha256 = serialised.Sha256,
                    };

                    job.Notes.Add(MeshTargetNote(serialised.Length));

                    foreach (var page in job.Pages)
                    {
                        var final = page.Value.Substring(0, page.Value.Length - HeldPageSuffix.Length);
                        var pageTemp = Staged(final);
                        if (File.Exists(pageTemp)) File.Delete(pageTemp);
                        File.Move(page.Value, pageTemp);
                        movedPages.Add(new KeyValuePair<string, string>(pageTemp, page.Value));
                        staged.Add(final);
                        lengths[final] = new FileInfo(pageTemp).Length;
                    }
                }
                else if (!job.WriteMesh && !dropMesh && job.WriteIndex && job.WrittenMesh != null &&
                         !string.IsNullOrEmpty(job.WrittenMesh.Sha256))
                {
                    // a recorded attempt beside the stored mesh: its sidecar alone, bound to that mesh's own hash
                    job.Mesh.Index.MeshSha = MapMeshIndex.ShaBytes(job.WrittenMesh.Sha256);
                    var bytes = MapMeshIndex.ToBytes(job.Mesh.Index);
                    staged.Add(indexPath);
                    Stage(indexPath, bytes);
                    lengths[indexPath] = bytes.Length;
                    indexStaged = true;
                }

                if (job.WriteMesh && !meshStaged && job.MeshWhy == null) job.MeshWhy = "it could not be serialised";

                // The mesh block the meta names before the commits: this write's, the one on disk, or none.
                if (dropMesh)
                {
                    meta.Mesh = null;
                }
                else if (job.WriteMesh && !meshStaged)
                {
                    meta.Mesh = WrittenMeshOnDisk(job);
                    meta.Atlas = meta.Mesh != null ? job.WrittenAtlas : null;
                }
                else if (meshStaged)
                {
                    meta.Mesh = job.NewMesh;
                }

                // 4. the commits, in WriteMeta's order, the meta last
                var written = new HashSet<string>(job.WrittenPictures.Select(p => p.File), StringComparer.OrdinalIgnoreCase);
                var byFile = job.WrittenPictures.ToDictionary(p => p.File, StringComparer.OrdinalIgnoreCase);

                // The floors: as WriteMeta, with no try of their own - a floor that will not go in place fails the write, and
                // no meta names the half.
                foreach (var floor in meta.Floors)
                {
                    if (floor == null || !written.Contains(floor.File)) continue;

                    var picture = Path.Combine(job.Dir, floor.File);
                    Commit(picture);
                    job.Bytes += lengths[picture];

                    var dist = Path.Combine(job.Dir, byFile[floor.File].DistFile);
                    Commit(dist);
                    job.Bytes += lengths[dist];
                }

                if (meshStaged)
                {
                    // Its own try, as WriteMeta's: a .bin that will not move must not take the meta down with it.
                    try
                    {
                        Commit(meshPath);
                        job.Bytes += lengths[meshPath];
                        job.MeshWritten = true;

                        try
                        {
                            if (indexStaged)
                            {
                                Commit(indexPath);
                                job.Bytes += lengths[indexPath];
                            }
                            else
                            {
                                DeleteOrWarn(indexPath);
                            }
                        }
                        catch (Exception indexEx)
                        {
                            DeleteQuietly(Staged(indexPath));
                            DeleteOrWarn(indexPath);
                            job.IndexFailed = true;
                            job.Notes.Add($"the mesh sidecar could not be put in place ({indexEx.GetType().Name}: {indexEx.Message}) - " +
                                          "the next capture rebuilds the 3D mesh from scratch");
                        }

                        // The pages, each in its own try: the first that will not go in place ends the list.
                        var atlas = new List<CaptureAtlas>();

                        foreach (var page in meta.Atlas ?? new List<CaptureAtlas>())
                        {
                            if (page == null || atlas.Count != page.Page || !IsPlainFileName(page.File))
                            {
                                job.PagesCut = true;
                                break;
                            }

                            var path = Path.Combine(job.Dir, page.File);

                            try
                            {
                                var had = File.Exists(Staged(path));
                                Commit(path);
                                if (had && lengths.TryGetValue(path, out var length)) job.Bytes += length;
                                atlas.Add(page);
                            }
                            catch (Exception pageEx)
                            {
                                job.PagesCut = true;
                                job.Notes.Add($"atlas page {page.Page.ToString(CultureInfo.InvariantCulture)} could not be put in place " +
                                              $"({pageEx.GetType().Name}: {pageEx.Message}) - the buildings on it and later pages keep " +
                                              "the side views");
                                break;
                            }
                        }

                        meta.Atlas = atlas.Count == 0 ? null : atlas;
                    }
                    catch (Exception meshEx)
                    {
                        job.MeshWritten = false;
                        job.MeshWhy = $"it could not be put in place ({meshEx.GetType().Name}: {meshEx.Message})";
                        DeleteQuietly(Staged(indexPath));
                        meta.Mesh = WrittenMeshOnDisk(job);
                        meta.Atlas = meta.Mesh != null ? job.WrittenAtlas : null;
                    }
                }
                else if (indexStaged)
                {
                    try
                    {
                        Commit(indexPath);
                        job.Bytes += lengths[indexPath];
                        job.IndexWritten = true;
                    }
                    catch (Exception indexEx)
                    {
                        DeleteQuietly(Staged(indexPath));
                        DeleteOrWarn(indexPath);
                        job.IndexFailed = true;
                        job.Notes.Add($"the mesh's updated sidecar could not be put in place ({indexEx.GetType().Name}: {indexEx.Message}) - " +
                                      "the next capture rebuilds the 3D mesh from scratch");
                    }
                }

                if (meta.Mesh == null) meta.Atlas = null;

                // Campaign speed step 3: a mesh that fell back (any MeshWhy) is not the held one the held stands describe - the
                // meta names the mesh on disk, so it names that mesh's stands
                if (job.MeshWhy != null) meta.Stands = meta.Mesh != null ? job.WrittenMeta?.Stands : null;

                // The sides, each in its own try, as CommitSides: one that will not go in place keeps the earlier one.
                if (meta.Sides != null)
                {
                    var sides = new List<CaptureSide>();

                    foreach (var side in meta.Sides)
                    {
                        if (side == null) continue;

                        if (!written.Contains(side.File))
                        {
                            sides.Add(side);
                            continue;
                        }

                        var picture = Path.Combine(job.Dir, side.File);

                        try
                        {
                            Commit(picture);
                            job.Bytes += lengths[picture];
                            sides.Add(side);

                            var dist = Path.Combine(job.Dir, byFile[side.File].DistFile);
                            try
                            {
                                Commit(dist);
                                job.Bytes += lengths[dist];
                            }
                            catch (Exception distEx)
                            {
                                DeleteQuietly(Staged(dist));
                                DeleteOrWarn(dist);
                                job.Notes.Add($"{Path.GetFileName(dist)} could not be put in place ({distEx.Message}) - it is removed, " +
                                              "so the next capture of that side merges as if fresh");
                            }
                        }
                        catch (Exception sideEx)
                        {
                            job.Notes.Add($"{side.File} could not be put in place ({sideEx.GetType().Name}: {sideEx.Message}) - " +
                                          "the earlier one is kept if there is one");
                            job.WrittenPictures.Remove(byFile[side.File]);

                            var was = job.WrittenMeta?.Sides?.FirstOrDefault(s => s != null && s.Dir == side.Dir);
                            if (was != null && IsPlainFileName(was.File) && File.Exists(Path.Combine(job.Dir, was.File))) sides.Add(was);
                        }
                    }

                    meta.Sides = sides.Count == 0 ? null : sides;
                }

                var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
                var metaPath = Path.Combine(job.Dir, $"{job.Key}.map.json");
                var temp = metaPath + ".tmp";

                File.WriteAllText(temp, json);
                if (File.Exists(metaPath)) File.Delete(metaPath);
                File.Move(temp, metaPath);

                job.WrittenMeta = meta;
                job.Keep = KeepOf(meta, job.Key);
                job.Written = true;
                movedPages.Clear();
            }
            catch (Exception ex)
            {
                job.Failed = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                // A held page this write moved and did not commit goes back beside the set; whatever else it staged and did
                // not commit goes.
                foreach (var page in movedPages)
                {
                    try
                    {
                        if (File.Exists(page.Key) && !File.Exists(page.Value)) File.Move(page.Key, page.Value);
                    }
                    catch
                    {
                        // swept with the hold
                    }
                }

                foreach (var file in staged) DeleteQuietly(Staged(file));

                // Review: a write nothing will hold on to after it (the raid's end, a campaign that has ended) lets the held
                // arrays go the moment it is done - up to 1.5 GB on Interchange - rather than when the main thread next looks.
                if (job.Detached) ReleaseHeldArrays(job);
            }
        }

        /// <summary>The mesh block the meta on disk names, when its file is still there - what a checkpoint falls back to.</summary>
        /// <param name="job">The write.</param>
        private static CaptureMesh WrittenMeshOnDisk(HeldFlush job)
        {
            var mesh = job.WrittenMesh;
            return mesh != null && IsPlainFileName(mesh.File) && File.Exists(Path.Combine(job.Dir, mesh.File)) ? mesh : null;
        }

        /// <summary>Campaign speed step 2 (review): the held sides with any over the size cap carried from the meta on disk, or
        /// left out.</summary>
        /// <param name="sides">The held meta's sides.</param>
        /// <param name="over">The pictures over the cap.</param>
        /// <param name="job">The write.</param>
        private static List<CaptureSide> CarriedOver(List<CaptureSide> sides, HashSet<string> over, HeldFlush job)
        {
            if (sides == null) return null;

            var kept = new List<CaptureSide>();

            foreach (var side in sides)
            {
                if (side == null) continue;

                if (!over.Contains(side.File))
                {
                    kept.Add(side);
                    continue;
                }

                var was = job.WrittenMeta?.Sides?.FirstOrDefault(s => s != null && s.Dir == side.Dir);
                if (was != null && IsPlainFileName(was.File) && File.Exists(Path.Combine(job.Dir, was.File))) kept.Add(was);
            }

            return kept.Count == 0 ? null : kept;
        }

        /// <summary>Campaign speed step 2 (review): a detached write's held arrays let go - every picture and the mesh.</summary>
        /// <param name="job">The write.</param>
        private static void ReleaseHeldArrays(HeldFlush job)
        {
            var hold = job?.Hold;
            if (hold == null) return;

            foreach (var picture in hold.Pictures.Values)
            {
                picture.Pixels = null;
                picture.Dist = null;
            }

            hold.Mesh = null;
        }

        /// <summary>Campaign speed step 2, on a worker: one held picture and its sidecar through the managed encoder, with its
        /// round trip the first time a colour type is encoded this session, and the floors' size cap.</summary>
        /// <param name="e">The picture, and where its results go.</param>
        private static void EncodeHeld(HeldEncode e)
        {
            try
            {
                var p = e.Picture;

                e.Png = PngEncoder.Encode(p.Width, p.Height, 6, PngFilter, RgbaRows(p.Pixels, p.Width, p.Height), !_rgbaRoundTripped);
                if (e.Png.Error != null)
                {
                    if (e.Png.RoundTripFailed) _managedPngOff = true;
                    e.Error = $"{e.Png.Error.GetType().Name}: {e.Png.Error.Message}";
                    return;
                }

                if (e.Png.RoundTripChecked) _rgbaRoundTripped = true;

                if (e.Png.Length <= 0)
                {
                    e.Error = "it encoded to nothing";
                    return;
                }

                // Review: a picture over the cap is carried, as the old path did with a floor it would not write.
                if (e.Png.Length > MaxFloorPngBytes)
                {
                    e.OverCap = true;
                    return;
                }

                e.Sidecar = PngEncoder.Encode(p.Width, p.Height, 2, PngFilter, GreyRgbRows(p.Dist, p.Width, p.Height), !_rgbRoundTripped);
                if (e.Sidecar.Error != null)
                {
                    if (e.Sidecar.RoundTripFailed) _managedPngOff = true;
                    e.Error = $"its sidecar: {e.Sidecar.Error.GetType().Name}: {e.Sidecar.Error.Message}";
                    return;
                }

                if (e.Sidecar.RoundTripChecked) _rgbRoundTripped = true;
            }
            catch (Exception ex)
            {
                e.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>Campaign speed step 2, on a worker: SerialiseMesh's body for a held mesh - the deflate, the hash, and the
        /// sidecar bound to those bytes.</summary>
        /// <param name="mesh">The held mesh.</param>
        private static SerialisedMesh SerialiseHeldMesh(HeldMesh mesh)
        {
            using (var stream = MapMeshFile.ToStream(mesh.File))
            {
                var buffer = stream.GetBuffer();
                var length = (int)stream.Length;
                var sha = Sha256(buffer, length);

                byte[] indexBytes = null;
                string indexWhy = null;

                if (mesh.Index != null)
                {
                    try
                    {
                        mesh.Index.MeshSha = MapMeshIndex.ShaBytes(sha);
                        indexBytes = MapMeshIndex.ToBytes(mesh.Index);
                    }
                    catch (Exception ex)
                    {
                        indexWhy = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }
                else
                {
                    indexWhy = "the build produced none";
                }

                return new SerialisedMesh { Bytes = buffer, Length = length, Sha256 = sha, IndexBytes = indexBytes, IndexWhy = indexWhy };
            }
        }

        /// <summary>A shallow copy of a meta, so a checkpoint can name its own mesh block without touching the held one.</summary>
        /// <param name="m">The meta.</param>
        private static CaptureMeta CopyMeta(CaptureMeta m) => new CaptureMeta
        {
            SchemaVersion = m.SchemaVersion,
            Map = m.Map,
            Extent = m.Extent,
            Rotation = m.Rotation,
            PxPerMetre = m.PxPerMetre,
            TileSize = m.TileSize,
            CapturedAt = m.CapturedAt,
            FirstCapturedAt = m.FirstCapturedAt,
            Captures = m.Captures,
            ModVersion = m.ModVersion,
            Render = m.Render,
            TimeOfDay = m.TimeOfDay,
            CapturedIn = m.CapturedIn,
            Lighting = m.Lighting,
            Floors = m.Floors,
            Labels = m.Labels,
            Mesh = m.Mesh,
            Atlas = m.Atlas,
            Sides = m.Sides,
            Stands = m.Stands,
        };

        /// <summary>The file names a meta accounts for - its floors and their sidecars, its mesh, its pages, its sides and their
        /// sidecars - for the stale sweep, as WriteMeta's keep.</summary>
        /// <param name="meta">The meta written.</param>
        /// <param name="key">The map.</param>
        private static HashSet<string> KeepOf(CaptureMeta meta, string key)
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var floor in meta.Floors ?? new List<CaptureFloor>())
            {
                if (floor == null) continue;
                keep.Add(floor.File);
                keep.Add($"{key}-{floor.Level.ToString(CultureInfo.InvariantCulture)}.dist.png");

                // Stage M3: a viewing copy the meta names (a carried menu floor's) is not stale either
                if (!string.IsNullOrEmpty(floor.ViewFile)) keep.Add(floor.ViewFile);
            }

            if (meta.Mesh != null) keep.Add(meta.Mesh.File);

            if (meta.Atlas != null)
                foreach (var page in meta.Atlas)
                    if (page != null) keep.Add(page.File);

            if (meta.Sides != null)
                foreach (var side in meta.Sides)
                {
                    if (side == null) continue;
                    keep.Add(side.File);
                    keep.Add(SideDistFileName(key, side.Dir));
                }

            return keep;
        }

        /// <summary>Campaign speed step 2: finishes a checkpoint once its worker has, if one has. Main thread; cheap; asked every
        /// frame by the waits, by this component's Update and by the poller the plugin object runs.</summary>
        private static void PollFlush()
        {
            var job = _flush;
            if (job != null && !job.Completed && job.Task != null && job.Task.IsCompleted) CompleteFlush(job);
        }

        /// <summary>Campaign speed step 2: a checkpoint nobody is waiting for - the raid's end, or a wait that gave up - is
        /// finished by a coroutine on the plugin object, which outlives the raid (MapTransfer's upload runs there too).</summary>
        /// <param name="job">The write.</param>
        private static void EnsureFlushPoller(HeldFlush job)
        {
            if (job == null || job.Polled || job.Completed) return;

            try
            {
                var host = Plugin.Instance;
                if (host == null) return;

                job.Polled = true;
                host.StartCoroutine(PollFlushUntilDone(job));
            }
            catch (Exception ex)
            {
                job.Polled = false;
                Plugin.LogSource?.LogDebug($"QuestTree: the checkpoint's poller could not be started ({ex.Message}).");
            }
        }

        private static IEnumerator PollFlushUntilDone(HeldFlush job)
        {
            while (!job.Completed && job.Task != null && !job.Task.IsCompleted) yield return null;

            if (!job.Completed) CompleteFlush(job);
        }

        /// <summary>
        /// Campaign speed step 2, on the main thread: what a checkpoint's write came to. Written: the held copies it wrote are
        /// clean again, the stale sweep, WP3's shape counter, the Maps tab told, the upload asked for (owed while the campaign
        /// holds uploads - the campaign's release is the one upload, and it sees these files), the checkpoint line with the
        /// heap after a collection and the held bytes. Failed: nothing was committed or the commit was cut short; the hold is
        /// let go with its stops said lost, and the campaign writes every stop from here, as it did before step 2. Then
        /// whatever waited for the write (a campaign's upload hold). Never throws.
        /// </summary>
        /// <param name="job">The finished write.</param>
        private static void CompleteFlush(HeldFlush job)
        {
            if (job == null || job.Completed) return;

            job.Completed = true;
            var hold = job.Hold;

            try
            {
                if (job.Task.IsFaulted && job.Failed == null)
                    job.Failed = job.Task.Exception?.GetBaseException().Message ?? "the worker failed";

                if (job.Written)
                {
                    // Only what went in place is clean: a picture over the size cap, or a side that would not commit, stays
                    // dirty and is tried again at the next checkpoint.
                    foreach (var picture in job.WrittenPictures) picture.Dirty = false;

                    // What the meta on disk now names - whatever the mesh came to (review: the fallbacks are WriteMeta's).
                    hold.WrittenMeta = job.WrittenMeta;
                    hold.WrittenMesh = job.WrittenMeta.Mesh;
                    hold.WrittenAtlas = job.WrittenMeta.Atlas;

                    var meshFell = job.Mesh != null && (job.WriteMesh && !job.MeshWritten || job.PagesCut || job.WrittenMeta.Mesh == null);

                    if (meshFell && hold.Mesh != null)
                    {
                        RevertHeldMesh(hold, job.PagesCut
                            ? "an atlas page of the checkpoint could not be put in place, so the atlas on disk ends early"
                            : $"it could not be written at the checkpoint ({job.MeshWhy ?? "no reason given"})");
                    }
                    else if (job.MeshWritten)
                    {
                        hold.PendingPages.Clear();

                        if (hold.Mesh != null && ReferenceEquals(hold.Mesh, job.Mesh))
                        {
                            hold.Mesh.Dirty = false;
                            hold.Mesh.IndexDirty = job.IndexFailed;
                        }

                        if (hold.Meta != null && ReferenceEquals(hold.Meta, job.Meta))
                        {
                            hold.Meta.Mesh = job.NewMesh;
                            hold.Meta.Atlas = job.WrittenMeta.Atlas;
                        }
                    }
                    else if (job.IndexWritten && hold.Mesh != null)
                    {
                        hold.Mesh.IndexDirty = false;
                    }

                    hold.Unsaved = Math.Max(0, hold.Unsaved - job.Unsaved);

                    DropStalePictures(new Plan { Key = job.Key, Dir = job.Dir }, job.Keep);

                    var shape = ShapeSignature(job.WrittenMeta);
                    if (!_shapes.TryGetValue(job.Key, out var oldShape) || !string.Equals(oldShape, shape, StringComparison.Ordinal))
                    {
                        Bump(job.Key, shape: true);
                        _shapes[job.Key] = shape;
                    }

                    try
                    {
                        UI.MapCatalog.InvalidateCaptures();
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: the Maps tab could not be told about the checkpoint ({ex.Message}).");
                    }

                    try
                    {
                        MapTransfer.UploadCapture(job.Key);
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: the checkpoint of {job.Key} could not be offered to the host ({ex.Message}).");
                    }

                    var ms = job.Clock.Elapsed.TotalMilliseconds;
                    LastCheckpointSeconds = ms / 1000d;

                    foreach (var note in job.Notes)
                        Plugin.LogSource?.LogWarning($"QuestTree: the checkpoint of {job.Key}: {note}.");

                    if (job.Detached) ReleaseHeldArrays(job);

                    // (review) a campaign that has stopped holding (memory ran short) lets its hold go once it is written
                    if (_holdOff && ReferenceEquals(_hold, hold) && hold.Unsaved == 0)
                    {
                        SweepHeldPages(hold);
                        _hold = null;
                    }

                    CollectGarbage("after a campaign checkpoint", force: true);

                    var inv = CultureInfo.InvariantCulture;
                    var of = _campaignStopCount > 0 ? $" of {_campaignStopCount.ToString(inv)}" : "";

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: campaign checkpoint after stop {job.Stop.ToString(inv)}{of} - wrote {Mb(job.Bytes)} MB in " +
                        $"{Ms(ms)} ms (heap {GB(GC.GetTotalMemory(false))} GB, held {Mb(HeldBytes(hold))} MB) - {job.Key}, " +
                        $"{job.Unsaved.ToString(inv)} stop(s), {job.WrittenPictures.Count.ToString(inv)} of " +
                        $"{job.Pictures.Count.ToString(inv)} changed picture(s) written" +
                        (job.MeshWritten
                            ? $", mesh {MB(job.NewMesh.Bytes)} MB sha256 {ShortSha(job.NewMesh.Sha256)}" +
                              (job.PagesCut ? $", atlas cut to {(job.WrittenMeta.Atlas?.Count ?? 0).ToString(inv)} page(s)" : "")
                            : job.WriteMesh
                                ? (job.WrittenMeta.Mesh != null ? ", the stored mesh kept" : ", no mesh named") + $" ({job.MeshWhy})"
                                : job.IndexWritten ? ", the mesh sidecar alone" : "") +
                        $"; {job.Why}.");

                    Journal(job.Key, $"checkpoint after stop {job.Stop.ToString(inv)}{of} - {Mb(job.Bytes)} MB in {Ms(ms)} ms ({job.Why}).");
                }
                else
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the campaign checkpoint of {job.Key} after stop {job.Stop.ToString(CultureInfo.InvariantCulture)} " +
                        $"failed ({job.Failed ?? "no reason given"}) - this campaign writes every stop from here.");

                    if (ReferenceEquals(_hold, hold)) _holdOff = true;

                    DropHold(hold, "its checkpoint could not be written", raidEnded: !ReferenceEquals(_hold, hold) && LiveCampaignSession == 0);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the campaign checkpoint of {job.Key} could not be finished ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                if (ReferenceEquals(_flush, job)) _flush = null;

                foreach (var then in job.Then)
                {
                    try
                    {
                        then();
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: what waited for the checkpoint of {job.Key} failed ({ex.Message}).");
                    }
                }

                job.Then.Clear();
            }
        }

        /// <summary>WP2 (7): the raid's LOD map and path hashes, kept across its captures (MapMeshBuilder.CacheFor, keyed
        /// on the GameWorld). This component hangs off the GameWorld, so it goes with the raid anyway.</summary>
        private MapMeshBuilder.SceneCache _sceneCache;

        /// <summary>WP2 (fixes 2): consecutive captures, per map this session, that carried the stored mesh for a
        /// temporary reason, and the count at which that is said once at Warning.</summary>
        private static readonly Dictionary<string, int> _carries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private const int CarriesBeforeWarning = 3;

        /// <summary>WP2: seconds the capture waits for the stored mesh and its sidecar to load and check on a worker
        /// before it builds from scratch instead.</summary>
        private const double MeshBaseWaitSeconds = 20d;

        /// <summary>WP2: the extra seconds a campaign's last stop may take when MeshVerifyLastStop builds the mesh a
        /// second time - the watchdog, its grace and the atlas encode wait, once more.</summary>
        internal const double VerifyExtraSeconds = MeshWatchdogSeconds + MeshWatchdogGraceSeconds + AtlasEncodeWaitSeconds;

        /// <summary>The 3D mesh build now running, so <see cref="Cleanup"/> can dispose it. An iterator
        /// that is disposed runs its finally blocks, which is where the builder waits for a GPU readback
        /// in flight before releasing its buffer - so a raid that ends in the middle of one is the
        /// reason this field exists rather than a local.</summary>
        private IEnumerator _meshBuild;

        /// <summary>Seconds the floor phase may take before the floors not yet started are skipped (review F45),
        /// and the overrun a floor that started in time may take before it too is abandoned. With the side
        /// phase's cap and the mesh watchdog this bounds a capture at <see cref="WorstCaseSeconds"/>, which is
        /// what the campaign waits for.
        ///
        /// 180 s (rollback: 70 s). Nearly all of a floor's time goes with its pixels - the tiles (Customs 9x5 of
        /// them at 8 px/m against 5x3 at 4, about 32 s a full floor at ~720 ms a tile), the development and the
        /// encode - and those went up four times with MaxPixelsPerMetre 4 -> 8, so the 70 s that let a multi-floor
        /// map finish would now cut its upper floors every time.</summary>
        internal const double FloorPhaseSeconds = 180d;

        private const double FloorPhaseOverrun = 1.25d;

        /// <summary>The running mesh build's request - the watchdog's handle on it (Abort).</summary>
        private MapMeshBuilder.Request _meshRequest;

        /// <summary>Seconds of frames the mesh build may take before the watchdog asks it to finish with
        /// what it has, and the seconds after that before it is disposed outright.</summary>
        private const double MeshWatchdogSeconds = 200d;

        private const double MeshWatchdogGraceSeconds = 15d;

        /// <summary>Seconds the side phase may take before the sides not yet started are skipped, and the overrun
        /// a side that started in time may take (review F45) - the floors' own numbers, because the sides render
        /// the same tiles and on a one-floor map take about the floors' time (SideSeconds). A skipped side keeps the
        /// picture an earlier capture took of it (CommitSides carries it).</summary>
        internal const double SidePhaseSeconds = FloorPhaseSeconds;

        private const double SidePhaseOverrun = FloorPhaseOverrun;

        /// <summary>Seconds for the steps no cap covers: the last floor's and the last side's development and
        /// encode after their tiles, the collect before the mesh, the mesh file's deflate on a worker, and the meta.
        /// Each is measured in seconds, not tens of them.</summary>
        private const double FinishAllowanceSeconds = 60d;

        /// <summary>1.19.0 hotfix: the longest the capture waits for the mesh file's deflate and hash on a worker (about
        /// five seconds at today's sizes) before it gives the mesh up for this capture - inside FinishAllowanceSeconds.</summary>
        private const double SerialiseWaitSeconds = 45d;

        /// <summary>
        /// 1.19.0 hotfix: the managed heap growth, in bytes since the last collection this class forced, at which
        /// <see cref="CollectGarbage"/> collects. A capture stop allocates about 2 GB it drops again (the stored mesh's
        /// load and parse, the builder's copies, the deflate, the floors' and sides' pictures and encodes), so this
        /// collects once or twice a stop.
        /// </summary>
        private const long CollectEveryBytes = 1L << 30;

        /// <summary>The managed heap right after the last collection <see cref="CollectGarbage"/> forced, or -1.</summary>
        private static long _heapAfterCollect = -1;

        /// <summary>
        /// 1.19.0 hotfix: a garbage collection that HAPPENS in a raid. EFT switches Unity's collector off for the whole
        /// raid (BaseLocalGame.PrepareSession sets InGameMemoryManagement.GCEnabled = false, which is
        /// GarbageCollector.GCMode = Disabled on any machine with 12 GB or more), and with the collector off a
        /// GC.Collect() does nothing - EFT's own InGameMemoryManagement.Collect switches it on around its GC.Collect for
        /// that reason. So every GC.Collect this capture made was a no-op, nothing a capture dropped was ever reclaimed
        /// before the raid ended, and a 22-stop campaign reached 52 GB private and hung. This switches the collector on,
        /// collects and puts the game's mode back - EFT's own sequence - once the heap has grown
        /// <see cref="CollectEveryBytes"/> since the last time. Never throws.
        /// </summary>
        /// <param name="where">Where in the capture, for the debug line.</param>
        /// <param name="force">Campaign speed step 2: collect whatever the growth - a checkpoint's line reports the heap after a
        /// collection, which is the number that shows whether holding a campaign's set grows it from one checkpoint to the next.</param>
        internal static void CollectGarbage(string where, bool force = false)
        {
            try
            {
                var before = GC.GetTotalMemory(false);
                if (!force && _heapAfterCollect >= 0 && before - _heapAfterCollect < CollectEveryBytes) return;

                var mode = UnityEngine.Scripting.GarbageCollector.GCMode;
                var clock = Stopwatch.StartNew();

                try
                {
                    if (mode != UnityEngine.Scripting.GarbageCollector.Mode.Enabled)
                        UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;

                    GC.Collect();
                }
                finally
                {
                    if (mode != UnityEngine.Scripting.GarbageCollector.Mode.Enabled)
                        UnityEngine.Scripting.GarbageCollector.GCMode = mode;
                }

                var after = GC.GetTotalMemory(false);
                _heapAfterCollect = after;

                // Info, not Debug (hotfix review): the installed log level does not record Debug, and this line is the
                // one piece of evidence that the collection ran in a raid and what it freed - one to three a stop.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: garbage collected {where} - managed heap {GB(before)} -> {GB(after)} GB in " +
                    $"{clock.ElapsedMilliseconds} ms (the game's collector mode: {mode}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: a garbage collection {where} failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static string GB(long bytes) => (bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>Seconds a capture waits, before it commits, for an upload of the same map to finish reading a file on
        /// a worker (WP3 Phase A, MapTransfer.IsReadingCapture). A read is one file - frames - so this bounds only a hung
        /// disk, after which the commit goes ahead as it always did.</summary>
        private const double CommitWaitSeconds = 10d;

        /// <summary>The longest a capture can run with every cap in force (review F45): floors, the stored mesh's load
        /// (WP2), mesh watchdog and grace, the atlas encode wait, sides, the uncapped finishing steps, the wait for an
        /// upload's read before the commit (WP3), and the two encode settles no phase cap covers (WP4: the last floor's
        /// and the last side's managed encodes, each waited for up to EncodeWaitSeconds after its phase), and a held
        /// campaign stop's checkpoint (campaign speed step 2: the upload-read wait and the write's). 1185 s with
        /// today's numbers (995 s before step 2, 600 s before the 8 px/m ground raised FloorPhaseSeconds and EncodeWaitSeconds). The
        /// campaign waits this long for a stop, so a slow capture is never taken for a stuck one; it is a ceiling for a
        /// hung capture, not what a stop takes.</summary>
        internal const double WorstCaseSeconds =
            FloorPhaseSeconds * FloorPhaseOverrun +                 // 225
            MeshBaseWaitSeconds +                                   //  20 (WP2: the stored mesh's load)
            MeshWatchdogSeconds + MeshWatchdogGraceSeconds +        // 215
            AtlasEncodeWaitSeconds +                                //  60
            SidePhaseSeconds * SidePhaseOverrun +                   // 225 (the floors' numbers)
            FinishAllowanceSeconds +                                //  60
            CommitWaitSeconds +                                     //  10 (WP3: an upload's read before the commit)
            2 * EncodeWaitSeconds +                                 // 180 (WP4: the last floor's and last side's settle)
            CommitWaitSeconds + CheckpointWaitSeconds;              // 190 (campaign speed step 2: a checkpoint stop's write)

        private Camera _camera;

        /// <summary>The capture's own light - see <see cref="CaptureLightIntensity"/>. Enabled only
        /// for the instant each tile renders, so the player's own view is never lit by it.</summary>
        private Light _light;

        private RenderTexture _rt;

        /// <summary>WP4 A2: rollback for the asynchronous tile readback. False = every tile goes through RenderTile's
        /// ReadPixels exactly as before (the ring is never built). Static readonly, not const, for FillWaterCyan's
        /// reason.</summary>
        private static readonly bool AsyncTileReadback = true;

        /// <summary>WP4 A2, verification build only: every tile is ALSO read with ReadPixels and compared bit for bit
        /// (VerifyNow on every tile, one Debug line each), and the staging texture is kept. Off in a release.</summary>
        private static readonly bool VerifyEveryTileReadback = false;

        /// <summary>WP4 A3, SPECIFIED BUT NOT BUILT, off: AverageTile on a worker per consumed slot. It touches no
        /// Unity API once A0's table is in (Mathf.HalfToFloat is thread-safe anyway) and writes only its tile's own
        /// disjoint rows and columns of Pixels and Drawn, so it could run as a Task per slot with the slot staying in
        /// flight until the task ends - each task with its own two sample rows, the drain waiting for the tasks, and
        /// Cleanup waiting for them before the NativeArrays are disposed. It is not the default: it reads a
        /// NativeArray off the main thread (legal only because the player compiles the safety checks out), and A0 is
        /// expected to take most of the average's cost. Build it only if the per-floor Debug line shows averaging
        /// above about 20 ms a tile with the half table on. Setting it true today changes nothing but one Debug
        /// line.</summary>
        private static readonly bool AverageOnWorker = false;

        /// <summary>WP4 A2: resolve targets and readback buffers in the ring. Each slot is a non-multisampled,
        /// depth-less copy of the render target's format - 32 MiB of VRAM (16 MiB eight-bit) - and a 32 MiB
        /// Allocator.Persistent NativeArray, so 3 slots cost 96 MiB VRAM and 96 MiB native memory, and the 32 MiB
        /// staging texture is not allocated on this path (net +64 MiB CPU). Outside the 26 B/px model, in the same
        /// category as the staging texture: Budget, WorkingSet and CaptureMemoryBudgetBytes are NOT changed (they decide
        /// the pixel size, and so every file). 3 rather than 2 because with one render a frame, 2 stalls whenever a
        /// readback takes two frames; the per-floor Debug line counts the waits, so 2 is a one-line change made from
        /// numbers.</summary>
        private const int ReadbackRing = 3;

        /// <summary>WP4 A2: frames the end-of-pass drain polls before it waits for the oldest readback outright.</summary>
        private const int ReadbackDrainFrames = 8;

        /// <summary>WP4 A2: readback errors in one capture after which the session goes back to ReadPixels. Each error's
        /// tile is re-rendered synchronously under the same hold, so an error never costs a pixel.</summary>
        private const int ReadbackErrorLimit = 2;

        /// <summary>WP4 A2: one slot of the readback ring - the resolve target a tile is copied into, the array its
        /// readback lands in, and the tile it holds while it is in flight.</summary>
        private sealed class ReadbackSlot
        {
            /// <summary>Non-MSAA, no depth, the render target's graphicsFormat.</summary>
            public RenderTexture Target;

            /// <summary>Allocator.Persistent, TileSize*TileSize, when _hdr.</summary>
            public NativeArray<Half4> Half;

            /// <summary>Allocator.Persistent, TileSize*TileSize, when !_hdr.</summary>
            public NativeArray<Color32> Bytes;

            public AsyncGPUReadbackRequest Request;

            /// <summary>Request issued, not yet consumed or abandoned.</summary>
            public bool InFlight;

            /// <summary>A wait for its request threw: never reused, and its array is left allocated rather than freed
            /// under a copy that may still land in it.</summary>
            public bool Broken;

            public Plan Plan;
            public FloorPlan Floor;
            public int Tile;
            public int Px0;
            public int Py0;
            public int Tw;
            public int Th;
        }

        /// <summary>WP4 A2: the ring, or null for the synchronous path (switch off, no device support, a format the
        /// path does not read, or the allocation refused - BuildRing says which in the capture header).</summary>
        private ReadbackSlot[] _ring;

        /// <summary>WP4 A2: the slots in flight, oldest first. Consumed strictly in this order (FIFO).</summary>
        private readonly Queue<ReadbackSlot> _inFlight = new Queue<ReadbackSlot>();

        /// <summary>WP4 A2: tiles of the current pass whose readback reported an error, re-rendered synchronously after
        /// the drain, still under the pass's scene hold.</summary>
        private readonly List<int> _retryTiles = new List<int>();

        /// <summary>WP4 A2: session-wide - the first-tile proof failed, or ReadbackErrorLimit was reached; every later
        /// tile of the session reads with ReadPixels.</summary>
        private static bool _asyncReadbackOff;

        /// <summary>WP4 A2: the "graphicsFormat/msaa" (ProvenKey) the first-tile proof passed on this session.</summary>
        private static string _asyncReadbackProven;

        /// <summary>WP4 A2 (review): tiles that matched ReadPixels but could not PROVE anything - all zero, or equal to
        /// their own vertical mirror, which a flipped or row-offset readback matches too. Such a tile is consumed from the
        /// verified data and the proof stays open; the next tile is verified again. After ReadbackProofTiles of them in a
        /// session one Info line says so - and every tile goes on being verified. Unproven is never trusted.</summary>
        private const int ReadbackProofTiles = 8;

        private static int _proofTilesTried;
        private static bool _proofTilesSaid;

        /// <summary>WP4 A2: what the capture header says about the readback - "async x3 (format)" or "ReadPixels (why)".</summary>
        private string _readbackNote;

        /// <summary>WP4 A2: readback errors this capture (against ReadbackErrorLimit).</summary>
        private int _readbackErrors;

        /// <summary>WP4 A2: the per-floor Debug line's numbers, reset by RenderTiles.</summary>
        private int _floorAsync;

        private int _floorWaits;
        private int _floorErrors;
        private int _floorAveraged;
        private int _floorMismatches;
        private double _floorWaitMs;
        private double _floorAverageMs;

        /// <summary>Two rows of samples, reused by every tile of every floor: one per row of a
        /// supersample block, four floats a sample. 2048 samples is 32 KB a row, which is the entire
        /// managed cost of the readback now - see ReadSampleRow.</summary>
        private float[][] _sampleRows;

        /// <summary>WP4 A0: Mathf.HalfToFloat for every ushort, computed BY Mathf.HalfToFloat once per session - so a
        /// lookup is the icall's own answer, NaN payloads and denormals included, and the table and the icall give the
        /// same float bits by construction. 256 KB, about a millisecond to build; it replaces four managed-to-native
        /// transitions a sample (16.8 million a 2048 tile). See <see cref="HalfTableEnabled"/>.</summary>
        private static float[] _halfTable;

        /// <summary>Off switch for WP4 A0: false puts ReadSampleRow back on four Mathf.HalfToFloat icalls a sample.
        /// Static readonly, not const, for the reason FillWaterCyan gives.</summary>
        private static readonly bool HalfTableEnabled = true;

        /// <summary>The texture each tile is read back into, one tile wide and tall, reused for every
        /// tile of every floor. Half-float when the hardware will render one, so the pipeline's
        /// linear values arrive intact instead of clipped into eight bits.</summary>
        private Texture2D _stage;

        /// <summary>The flat grey reflection environment this capture renders against, built once and
        /// destroyed with the rest - see <see cref="ReflectionGrey"/>. Null when it could not be built,
        /// and then the scene own reflections are used and the render tag says so.</summary>
        private Cubemap _reflection;

        /// <summary>Every renderer and LOD group the scene's distance culling switches off, flattened
        /// out of the DisablerCullingObjects once per capture, with room to remember what each was set
        /// to while a tile renders. See <see cref="ForceCulling"/>.</summary>
        private Component[] _culling;

        private bool[] _cullingWasEnabled;

        /// <summary>The culling objects, and which of them owns each component and each object in the flat lists
        /// - so the release can ask each culler's own HasEntered, and have the ones it touched re-apply their
        /// state (review F07).</summary>
        private DisablerCullingObject[] _cullers;

        private int[] _cullingOwner;
        private int[] _cullingObjectOwner;

        /// <summary>WP8 (D6), for the mesh build: the baked-LOD proxies (never buildings), the renderers a runtime
        /// culling system owns (read even when switched off), and the part of those from the occlusion bake groups.
        /// Collected with the culling lists, once a capture; null when that failed.</summary>
        private HashSet<Renderer> _proxyRenderers;

        private HashSet<Renderer> _gameCulled;
        private HashSet<Renderer> _occlusionCulled;

        /// <summary>The GameObjects the scene's distance culling DEACTIVATES, flattened out of the same
        /// culling objects, with room to remember which of them this capture switched on. Separate from
        /// the component list because they are switched with SetActive rather than an enabled flag, and
        /// because switching one runs whatever Awake and OnEnable are attached to it.</summary>
        private GameObject[] _cullingObjectsHeld;

        private bool[] _cullingObjectWasActive;

        /// <summary>How many of the GameObject list the current floor took hold of, so the restore walks
        /// exactly as far as the hold got.</summary>
        private int _cullingObjectsHeldCount;

        /// <summary>How many culling objects the components came from, for the capture header.</summary>
        private int _cullingObjects;

        /// <summary>The water renderers this capture does not draw, and their states while it does not.
        /// See <see cref="WaterLayerName"/>.</summary>
        private Renderer[] _water;

        /// <summary>What each water renderer was drawing before this capture painted it flat, read once
        /// in <see cref="CollectWater"/> because a scene does not reassign water materials mid-raid, and
        /// put back by <see cref="ReleaseWater"/>. The whole ARRAY per renderer, so a mesh whose second
        /// submesh is the water restores every slot exactly as it was.</summary>
        private Material[][] _waterMaterials;

        /// <summary>The flat blue the water is painted with, one material shared by every water renderer,
        /// and the one-pixel texture behind it when the shader that resolved needs one. Null when no
        /// shader resolved, and then the water is left exactly as the game draws it.</summary>
        private Material _waterFlat;

        private Texture2D _waterFlatTexture;

        /// <summary>Arrays of <see cref="_waterFlat"/>, one per material-slot count seen in the scene, so
        /// a render assigns a cached array rather than allocating one per renderer per tile.</summary>
        private Dictionary<int, Material[]> _waterFlatArrays;

        /// <summary>How many water renderers the current render swapped, so the restore walks exactly as
        /// far as the swap got.</summary>
        private int _waterSwapped;

        /// <summary>How many entries of the culling list the current FLOOR actually took hold of - see
        /// <see cref="HoldScene"/>. The water has its own count above, because it is painted per
        /// render.</summary>
        private int _cullingHeld;

        /// <summary>Samples of multisampling the tile target was actually allocated with, 1 when the
        /// device refused all of them. Part of <see cref="RenderTag"/> and of the capture header.</summary>
        private int _msaa = 1;

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
            // Campaign speed step 2: a checkpoint nobody waits for any more is finished on the main thread - here as well as
            // by the plugin object's poller.
            PollFlush();

            // Stage M2: a menu capture has no key and no player - it is driven by MenuMapHost's work loop, never by a press.
            if (_menu != null) return;

            if (!ModSettings.Ready || ModSettings.CaptureMapKey == null) return;

            try
            {
                // ModSettings.ShortcutDown, not the shortcut's own IsDown: BepInEx refuses a press
                // while ANY key outside the combination is held, which in a raid means the capture
                // key did nothing whenever the player was moving. A held modifier the shortcut does
                // not name still blocks, so Ctrl+Shift+F9 is the campaign's key and not this one.
                if (!ModSettings.ShortcutDown(ModSettings.CaptureMapKey.Value)) return;

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

                // A key press is the player asking for this map, so it always builds the 3D mesh.
                _automatic = false;
                _skipMesh = false;
                _verifyMesh = false;
                StartCoroutine(Run());
            }
            catch (Exception ex)
            {
                if (_warnedOnPoll) return;
                _warnedOnPoll = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the map capture key failed ({ex.Message}).");
            }
        }

        private void OnDestroy()
        {
            Cleanup();

            // Campaign speed step 1 (4): this component goes with the raid - a campaign's relief never outlives it, even
            // when the raid ends mid-capture before MapCampaign's own OnDestroy. Stage M2: a menu capture's component is
            // no raid's and ends no campaign.
            if (_menu == null) CampaignEnds();
        }

        /// <summary>Whether there is a raid with a living player to photograph. A dead player's world
        /// is still loaded, but the screen has moved on and the frames are not the player's to
        /// spend.
        ///
        /// Stage M2: a menu capture skips this test - <see cref="RunMenuCapture"/> starts its run without asking - and a
        /// menu component answers false here, so neither the key nor <see cref="TryStartCapture"/> can start a raid-style
        /// capture on it.</summary>
        private bool PlayerIsAlive()
        {
            if (_menu != null) return false;

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

                // The floors' own wall time and pixels, which is what the side views' hold is estimated
                // from - see CaptureSides.
                var floorsClock = Stopwatch.StartNew();
                var floorsCut = 0;

                foreach (var floor in plan.Floors)
                {
                    // Stage M3: the Maps tab's progress line (menu captures only).
                    MenuPhase($"floor {plan.Floors.IndexOf(floor) + 1}/{plan.Floors.Count} '{floor.Dto?.Name}'");

                    // The floor phase's budget (review F45): past it the floors not yet started are skipped - an
                    // earlier capture's picture of them is carried - so a campaign stop stays inside WorstCaseSeconds.
                    if (floorsClock.Elapsed.TotalSeconds > plan.FloorCapSeconds)
                    {
                        floor.Failed = true;
                        floorsCut++;
                        continue;
                    }

                    if (!BeginFloor(plan, floor))
                    {
                        floor.Failed = true;

                        // Whatever it had allocated before it gave up goes back HERE, not at Cleanup.
                        // The floor's three buffers are the last thing BeginFloor does and an
                        // allocation failure is one of the ways it fails, so this path is exactly the
                        // one where the next floor - asking for the same sizes a frame later - must not
                        // be measured against a heap still holding this one's.
                        ReleaseTexture(floor);
                        continue;
                    }

                    // WP1: the previous picture and sidecar BEFORE the tiles when there is one to merge into, and the
                    // tile plan made from them - see LoadAndPlan. Nothing at all with the switch off or on a fresh
                    // capture, and then the loads stay in Develop, as before.
                    var loading = LoadAndPlan(plan, floor);
                    while (loading.MoveNext()) yield return loading.Current;

                    // Stage M2 (review): the menu capture's check of its wake, once, just before the first tile
                    if (_menu?.BeforeFirstTile != null)
                    {
                        var check = _menu.BeforeFirstTile;
                        _menu.BeforeFirstTile = null;

                        try
                        {
                            check();
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogDebug($"QuestTree: the menu capture's first-tile check failed ({ex.Message}).");
                        }
                    }

                    // The tiles, the water rule and the scene hold around them - see RenderTiles. The floor phase's
                    // overrun (review F45) is asked before every render, late water tiles included.
                    var tiles = RenderTiles(plan, floor, () =>
                    {
                        // A floor still rendering well past the budget is abandoned too - half a floor is not a
                        // picture - at a margin, so the one floor that started in time normally finishes.
                        if (floorsClock.Elapsed.TotalSeconds <= plan.FloorCapSeconds * FloorPhaseOverrun) return false;

                        floorsCut++;
                        return true;
                    });

                    while (tiles.MoveNext()) yield return tiles.Current;

                    // WP4 B2, BARRIER 1: the previous floor's encode ran during this floor's hold and tiles (the overlap);
                    // it is staged now, before this floor's MeasureFloor (which may refuse) and Develop (which reuses
                    // the pool it was encoded from).
                    var settle = SettleEncodes(plan, plan.Floors, floor);
                    while (settle.MoveNext()) yield return settle.Current;

                    // FULL-floor pixels whenever the floor was planned or rendered (WP1 3.5): counting only the
                    // rendered ones would put a whole floor's develop and encode on a tile's pixels, estimate the
                    // sides long and shrink the mesh budget below the old path's.
                    if (floor.Tiles > 0 || floor.TilesOwned + floor.TilesOutside > 0)
                        plan.FloorPixels += (long)plan.WidthPx * plan.HeightPx;

                    // The water quads go before the exposure is measured, not just before the merge:
                    // a flat cyan pool is one of the brightest things in a capture, and the rain
                    // capture's 98th percentile was read off exactly this kind of object. Painting
                    // them out first means the exposure describes the map. A planned floor that rendered no tile
                    // has no drawn pixel and so no water (WP1).
                    if (!floor.Failed && (floor.Verdicts == null || floor.Tiles > 0))
                    {
                        var inpaint = Inpaint(plan, floor);
                        while (inpaint.MoveNext()) yield return inpaint.Current;
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

                    // Campaign speed step 1 (1): no tile rendered, so the develop would only copy the stored picture and
                    // sidecar - they are kept on disk as they are, and the three steps below are skipped for this floor.
                    var unchanged = !floor.Failed && KeepUnchangedFloor(plan, floor);

                    if (!floor.Failed && !unchanged)
                    {
                        // Driven here rather than started as a coroutine of its own, so the floor
                        // loop cannot run ahead of a development that is still going.
                        var develop = Develop(plan, floor);
                        while (develop.MoveNext()) yield return develop.Current;
                    }

                    if (!floor.Failed && !unchanged)
                    {
                        yield return null;

                        // Campaign speed step 2: a held campaign keeps the developed floor in memory for its next stop and
                        // its next checkpoint - no encode, no sidecar, nothing staged.
                        if (plan.Hold != null) StashFloor(plan, floor);

                        // WP4 B2: FinishFloor on the Texture path; on the managed path the picture's and the sidecar's
                        // encodes start on workers and are staged at the next barrier.
                        else StartFloorEncode(plan, floor);
                    }

                    // The sidecar AFTER the picture, and only when the picture was written - see
                    // WriteSidecar, where the order is the whole of what makes a crash between the
                    // two files survivable. (The managed path keeps that order in SettleEncodes.)
                    if (plan.Hold == null && !floor.Failed && !unchanged && floor.PictureEncode == null)
                    {
                        yield return null;
                        WriteSidecar(plan, floor);
                    }

                    ReleaseTexture(floor);

                    yield return null;

                    // The ONE place this mod collects by hand, and a multi-floor map is why. A floor's
                    // working set is a hundred megabytes of arrays well over the large-object heap's
                    // threshold, the LOH is not compacted, and the next floor asks for the same sizes
                    // again a frame later - so without this the second floor of Interchange was looking
                    // for its buffers in a heap still holding the first floor's.
                    //
                    // AFTER the yield, not before it: ReleaseTexture drops the references and calls
                    // Object.Destroy on the floor's picture, and Unity's Destroy is deferred to the end
                    // of the current frame - so a collect in the same frame ran while the texture and
                    // its wrapper were still alive and reclaimed the arrays only.
                    //
                    // Skipped on the LAST floor: there is no next floor to make room for, Cleanup in
                    // the finally is about to drop everything anyway, and the collect is tens of
                    // milliseconds on the frame the player gets control back in.
                    if (!ReferenceEquals(floor, plan.Floors[plan.Floors.Count - 1])) CollectGarbage($"after {plan.Key}'s floor");
                }

                // WP4 B2, BARRIER 2: every floor's encode staged before anything reads Bytes (BeginMesh's gate, WillBeNamed,
                // the sides' gate) - and inside FloorSeconds, which counted the encodes before WP4 too. Then the pool goes,
                // so the mesh phase sees the memory it saw before.
                var settled = SettleEncodes(plan, plan.Floors, null);
                while (settled.MoveNext()) yield return settled.Current;

                plan.RgbaPool = null;

                // Campaign speed step 2 (review): every floor's light test has run - the stashed floors go into the hold now
                if (plan.Hold != null)
                {
                    HoldStashedFloors(plan);

                    // The arrays the hold just displaced (~1 GB on Interchange) are garbage only from here, and the next
                    // collect ("before 3D mesh") runs only after 1 GiB of growth - so it is forced here, or that garbage
                    // goes into the mesh build with EFT's collector off (step 2 re-review).
                    CollectGarbage("after the held floors", force: true);
                }

                plan.FloorSeconds = floorsClock.Elapsed.TotalSeconds;

                if (floorsCut > 0)
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} - {floorsCut} floor(s) were cut at the {F0(plan.FloorCapSeconds)} s floor budget " +
                        $"({plan.FloorSeconds:0} s spent); the pictures an earlier capture took of them are kept.");

                // WP2 (2.5): the stored mesh and its identity sidecar, read and checked on a worker while the frames go on -
                // what this capture's build adds to. Anything that makes it untrustworthy, or a load slower than
                // MeshBaseWaitSeconds, leaves plan.MeshBase null and the build takes the from-scratch path, which is the
                // pre-WP2 build exactly.
                if (!plan.Refused && plan.WantsMesh && plan.Floors.Any(f => !f.Failed && f.Bytes > 0))
                {
                    var baseLoad = StartMeshBase(plan, _culling != null);
                    var baseClock = Stopwatch.StartNew();

                    while (baseLoad != null && !baseLoad.IsCompleted && baseClock.Elapsed.TotalSeconds < MeshBaseWaitSeconds)
                        yield return null;

                    TakeMeshBase(plan, baseLoad);

                    // WP2 (fixes): a temporary refusal carries the stored mesh - WriteMeta names it as it is - rather than a
                    // from-scratch build replacing the union every earlier stop added to
                    if (plan.MeshBaseTemporary)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: {plan.Key}'s stored 3D mesh could not be added to this time ({plan.MeshBaseRefused}) - it is " +
                            "carried unchanged and no mesh is built at this stop.");

                        // WP2 (fixes 2): said once, at Warning, when it keeps happening - accumulation has stopped for this map
                        _carries.TryGetValue(plan.Key, out var carries);
                        _carries[plan.Key] = ++carries;

                        if (carries == CarriesBeforeWarning)
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: {plan.Key}'s stored 3D mesh has been carried {carries} times - {plan.MeshBaseRefused}; " +
                                "accumulation has stopped. Set '3D map: rebuild from scratch on the next capture' to start over.");
                    }
                    else
                    {
                        _carries.Remove(plan.Key);
                    }
                }

                // The 3D geometry, after the last picture and before the meta that will name it.
                //
                // Inside a hold of its own: the relief's rays do not care what is switched on, but the
                // building walk reads the renderers the game has streamed out and the culler has
                // switched off, which is exactly what HoldScene puts back. A GC first (inside
                // BeginMesh), because the last floor's hundred megabytes have just been dropped and the
                // mesh is about to ask for arrays of its own - and then a FRAME before the hold, so the
                // collect and the pass over twenty-seven thousand components are never the same frame.
                //
                // ReleaseScene is idempotent and Cleanup calls it too, so a raid that ends in the
                // middle of the build leaves the scene as the game had it.
                var meshPhase = Stopwatch.StartNew();
                MenuPhase("the 3D mesh");
                var mesh = plan.Refused || !plan.WantsMesh || plan.MeshBaseTemporary ? null : BeginMesh(plan);

                if (mesh != null)
                {
                    yield return null;

                    // Never throws (see HoldScene); the frame after it is the hold's own, as it is for
                    // every floor.
                    HoldScene();

                    yield return null;

                    // Driven by hand rather than yielded as a nested coroutine, and each MoveNext in
                    // its own try: the mesh is an UPGRADE to a capture and may never cost one its
                    // pictures. Every step inside the builder is guarded already; this is the line that
                    // holds even if one is not.
                    // THE WATCHDOG (second review, C1): whatever the builder's own caps say, a mesh phase that
                    // has yielded 200 s of frames is asked to stop reading and finish with what it stored -
                    // a file of what exists - and one still running 15 s after that is disposed (its
                    // finally blocks wait for any readback in flight) and writes nothing. Either way the
                    // scene is released below and the capture goes on to its sides and meta.
                    var meshClock = Stopwatch.StartNew();
                    var asked = false;

                    while (true)
                    {
                        object current = null;
                        var more = false;
                        var elapsed = meshClock.Elapsed.TotalSeconds;

                        if (!asked && elapsed > plan.MeshWatchdogCapSeconds && _meshRequest != null)
                        {
                            asked = true;
                            _meshRequest.Abort = true;
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D mesh of {plan.Key} has run {elapsed:0} s - the watchdog stops it; " +
                                "what is built so far is written.");
                        }

                        if (asked && elapsed > plan.MeshWatchdogCapSeconds + MeshWatchdogGraceSeconds)
                        {
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D mesh of {plan.Key} did not finish {MeshWatchdogGraceSeconds:0} s after " +
                                "the watchdog stopped it - it is abandoned; the pictures are unaffected.");
                            DisposeMeshBuild();
                            break;
                        }

                        try
                        {
                            more = _meshBuild != null && _meshBuild.MoveNext();
                            if (more) current = _meshBuild.Current;
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D mesh of {plan.Key} was abandoned ({ex.GetType().Name}: " +
                                $"{ex.Message}) - the pictures are unaffected.");
                            more = false;
                        }

                        if (!more) break;

                        yield return current;
                    }

                    EndMesh(plan, mesh);

                    yield return null;

                    // Stage W: the atlas pages' encodes, started inside the hold, are waited for HERE - after the
                    // scene is released - and settled before the mesh is serialised, so the file's page count and
                    // ranges name only pages that made it.
                    if (mesh.AtlasPages != null && mesh.AtlasPages.Count > 0)
                    {
                        var encodeClock = Stopwatch.StartNew();

                        while (mesh.AtlasPages.Any(p => p.Encode != null && !p.Encode.IsCompleted) &&
                               encodeClock.Elapsed.TotalSeconds < AtlasEncodeWaitSeconds)
                            yield return null;
                    }

                    // WP2: settled whether or not a page was encoded - a mesh built onto a stored one names the stored
                    // pages it carries as well as the ones it rewrote.
                    SettleAtlasPages(plan, mesh);

                    if (plan.Hold != null)
                    {
                        // Campaign speed step 2: the build is held for the next stop's base and the next checkpoint - no
                        // deflate, no hash, nothing staged.
                        HoldMesh(plan, mesh);
                    }
                    else if (mesh.Unchanged)
                    {
                        // WP2 (2.13): nothing was added, replaced or re-targeted, no tile or page changed, and the relief
                        // and y range are the stored ones byte for byte - the stored mesh, sidecar and pages are carried
                        // (WriteMeta's CarriedMesh), not rewritten with a new date.
                        plan.MeshCarriedUnchanged = true;
                        plan.MeshAccumulated = mesh.Accumulated;

                        // WP2 (fixes 2): a recorded attempt changed the sidecar - it is written beside the carried mesh (its
                        // sha is that mesh's), the mesh itself is not
                        if (mesh.IndexChanged && mesh.Index != null)
                        {
                            try
                            {
                                Stage(Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key)), MapMeshIndex.ToBytes(mesh.Index));
                                plan.IndexStagedAlone = true;
                            }
                            catch (Exception ex)
                            {
                                Forget(plan, MapMeshIndex.FileNameFor(plan.Key));
                                Plugin.LogSource?.LogDebug($"QuestTree: {plan.Key}'s updated sidecar could not be staged ({ex.Message}).");
                            }
                        }

                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: {plan.Key} built no new 3D geometry this time - the stored mesh is kept.");
                    }
                    else
                    {
                        // The deflate and the hash on a worker, the coroutine waiting a frame at a time: at
                        // CompressionLevel.Optimal a few megabytes of buildings is well over a frame of
                        // main-thread work, and nothing in it touches a Unity object - the mesh file is plain
                        // arrays. The write of the staged file stays here, on this thread, so the abort path
                        // (DropStaged) can never race a worker still writing a .tmp.
                        var serialised = SerialiseMesh(plan, mesh);

                        // 1.19.0 hotfix: bounded, as every other wait on a worker in this capture is - a worker that has
                        // not finished in SerialiseWaitSeconds costs this capture its mesh (the meta names the stored one
                        // or none), never the capture; and StageMesh is never handed an unfinished task, whose Result
                        // would block this thread
                        var serialiseClock = Stopwatch.StartNew();

                        while (serialised != null && !serialised.IsCompleted &&
                               serialiseClock.Elapsed.TotalSeconds < SerialiseWaitSeconds)
                            yield return null;

                        if (serialised != null && !serialised.IsCompleted)
                        {
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D mesh of {plan.Key} was not serialised within {SerialiseWaitSeconds:0} s - " +
                                "it is not written this capture; the pictures are unaffected.");
                            serialised = null;
                        }

                        StageMesh(plan, mesh, serialised);
                    }

                    // WP2 (4.1a): the campaign's last stop builds the mesh a second time FROM SCRATCH, in the same scene,
                    // into <key>-mesh.verify.bin - file L of the comparison tools/compare-mesh.py makes against the
                    // accumulated file A. Debug only; never named by the meta.
                    if (_verifyMesh && (ModSettings.MeshVerifyLastStop?.Value ?? false) && !plan.Refused)
                    {
                        var verify = VerifyMesh(plan);

                        while (true)
                        {
                            object current = null;
                            var more = false;

                            try
                            {
                                more = verify.MoveNext();
                                if (more) current = verify.Current;
                            }
                            catch (Exception ex)
                            {
                                Plugin.LogSource?.LogWarning(
                                    $"QuestTree: the verification mesh of {plan.Key} was abandoned ({ex.GetType().Name}: {ex.Message}).");
                                more = false;
                            }

                            if (!more) break;

                            yield return current;
                        }

                        EndMesh(plan, null);
                    }
                }

                plan.MeshSeconds = meshPhase.Elapsed.TotalSeconds;
                var sidesPhase = Stopwatch.StartNew();
                MenuPhase("the side views");

                // The four side views, after the mesh because the box they frame is the mesh's y range.
                // Behind the same "wrote a picture" test BeginMesh asks: WriteMeta writes nothing
                // without one, so sides staged for it would only be dropped. Driven through a guarded
                // MoveNext for the same reason the mesh is - a side view may never cost a capture its
                // floors.
                if (!plan.Refused && plan.WantsSides && plan.Floors.Any(f => !f.Failed && f.Bytes > 0))
                {
                    plan.SidesTaken = true;

                    // Campaign speed step 1 (2, review): the collider skyline the sides settle empty pixels above
                    plan.Tops = mesh?.Tops;

                    var sides = CaptureSides(plan, mesh?.File);

                    while (true)
                    {
                        object current = null;
                        var more = false;

                        try
                        {
                            more = sides.MoveNext();
                            if (more) current = sides.Current;
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the side views of {plan.Key} were abandoned ({ex.GetType().Name}: " +
                                $"{ex.Message}) - the pictures and the mesh are unaffected.");
                            more = false;
                        }

                        if (!more) break;

                        yield return current;
                    }

                    // Whatever the side loop was doing when it stopped: the scene let go, its buffers
                    // freed and the camera looking down again. All three idempotent.
                    ReleaseScene();
                    ReleaseTexture(plan.SideFloor);
                    plan.SideFloor = null;
                    RestoreTopCamera();
                }

                // WP3 Phase A, the commit handshake: never commit over a file an upload of this map is reading on a
                // worker - Windows refuses the delete and the rename, and a Commit that throws leaves the capture with
                // its pictures but no meta. A read is at most one file, so this is frames; the bound only matters for a
                // hung disk, and then the commit goes ahead as it always did.
                // WP4 B2, BARRIER 5: a defensive no-op - nothing is committed while an encode is outstanding (a side view
                // whose loop was abandoned after its encode started is staged here, as it would have been before).
                plan.SidesSeconds = sidesPhase.Elapsed.TotalSeconds;

                MenuPhase("finishing the pictures");
                var lastFloors = SettleEncodes(plan, plan.Floors, null);
                while (lastFloors.MoveNext()) yield return lastFloors.Current;

                var lastSides = SettleSides(plan, null, new SideTally());
                while (lastSides.MoveNext()) yield return lastSides.Current;

                var commitWait = Stopwatch.StartNew();
                while (!plan.Refused && plan.Hold == null && MapTransfer.IsReadingCapture(plan.Key) &&
                       commitWait.Elapsed.TotalSeconds < CommitWaitSeconds)
                    yield return null;

                // Nothing is in place until this runs: it commits every staged picture and then
                // writes the meta. A refused capture skips it, which is the whole of what makes the
                // refusal cost nothing.
                var writePhase = Stopwatch.StartNew();
                MenuPhase("writing the set");
                if (!plan.Refused && plan.Hold == null) WriteMeta(plan, clock);

                // Stage M2b: a menu capture says where its time went, phase by phase
                if (plan.MenuMode) LogMenuPhases(plan, writePhase.Elapsed.TotalSeconds, clock.Elapsed.TotalSeconds);

                // Campaign speed step 2: a held stop ends by holding its meta - the stop is then whole in memory - and every
                // CampaignCheckpointStops stops by writing the held set, which this stop waits for (so does the campaign).
                if (!plan.Refused && plan.Hold != null)
                {
                    HoldMeta(plan, clock);

                    // (review) memory ran short in a held stop: written now, whatever the count, and the hold let go after
                    var outOfMemory = plan.Hold.OutOfMemory;
                    if (outOfMemory)
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: memory ran short while {plan.Key} was held between stops - it is written now, and this " +
                            "campaign writes every stop from here.");

                    if (!plan.Hold.StopInProgress && (plan.Hold.Unsaved >= CampaignCheckpointStops || outOfMemory) &&
                        ReferenceEquals(_hold, plan.Hold))
                    {
                        var checkpoint = CampaignCheckpoint(outOfMemory
                            ? "memory ran short"
                            : $"every {CampaignCheckpointStops.ToString(CultureInfo.InvariantCulture)} stops");
                        while (checkpoint.MoveNext()) yield return checkpoint.Current;
                    }

                    // Written (or being written, which owns it): the campaign holds nothing from here.
                    if (outOfMemory && ReferenceEquals(_hold, plan.Hold) && plan.Hold.Unsaved == 0)
                    {
                        SweepHeldPages(plan.Hold);
                        _hold = null;
                    }
                }
            }
            finally
            {
                // Campaign speed step 2: a held stop that changed the held copies and did not finish - refused, thrown, or
                // abandoned - may have left them mixing two stops, so they are let go; the next stop starts from the last
                // checkpoint on disk.
                // (review) and the campaign writes every stop from here: a failure that repeats (memory) must not drop a
                // fresh hold at every stop.
                if (plan?.Hold != null && plan.Hold.StopInProgress)
                {
                    DropHold(plan.Hold, plan.Refused ? "the stop was refused after it had changed them" : "the stop did not finish",
                        raidEnded: false);
                    _holdOff = true;
                }

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

                // Stage M2c: this raid already found the map's stored set is a menu set raid captures leave alone - said
                // once at Warning when found (LoadPrevious); every retry stops here, before any scene read or camera.
                if (!(_menu != null && _menu.MenuMode) && IsGuardedMenuSet(key))
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: nothing was captured on {key} - {GuardedMenuSetReason}.");
                    return false;
                }

                // Campaign speed step 2: a checkpoint's write owns the held set and its files until it finishes - a capture
                // started under it would merge into copies the worker is writing and stage files it may sweep.
                if (CampaignWriting)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: a campaign checkpoint is still being written - nothing was captured; try again in a moment.");
                    return false;
                }

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

                // The cap less one block when the sides are rounded: ceil(longSide x ppm) can land a pixel
                // over what cap / longSide promises (float), and rounding that up to a multiple of four would
                // then pass the cap - and a picture over DynamicMapsLibrary.MaxPictureSide is refused by the
                // viewer. Resolution() hands back a multiple of four, so cap - 4 rounds up to at most cap.
                var capPx = AlignPictureSides ? cap - PictureBlock : cap;

                // Stage M2c: a menu capture's floors take the finer ground's cap and budget - decided here, before the
                // plan exists, because the scale is; the plan then carries the PNG cap and encode wait that go with it.
                var menu = _menu != null && _menu.MenuMode;
                var fineGround = menu && MenuFineGround;
                var ppmCap = fineGround ? MenuMaxPixelsPerMetre : MaxPixelsPerMetre;
                var budgetBytes = fineGround ? MenuCaptureMemoryBudgetBytes : CaptureMemoryBudgetBytes;

                var wanted = (float)Math.Min(capPx / longSide, ppmCap);
                var ppm = Budget(wanted, widthM, heightM, budgetBytes, out var budgetNote);

                if (!(ppm > 0f))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - {F(longSide)} m at a {cap} px cap gives no " +
                        "usable pixel size.");
                    return false;
                }

                var widthPx = PictureSide(widthM, ppm);
                var heightPx = PictureSide(heightM, ppm);

                plan = new Plan
                {
                    Key = key,
                    Dir = dir,
                    Extent = AlignPictureSides ? PictureExtent(extent, widthPx, heightPx, ppm) : extent,
                    Cap = cap,
                    Ppm = ppm,
                    WidthPx = widthPx,
                    HeightPx = heightPx,
                    MeshFile = MapMeshFile.FileNameFor(key),
                    MenuMode = menu,
                    MenuWrite = menu ? _menu.Write : MenuWriteMode.MergeOrFresh,
                };

                // Stage M2c: the finer ground's file cap and encode wait. Never set on a raid plan.
                if (fineGround)
                {
                    plan.FloorPngCapBytes = MenuMaxFloorPngBytes;
                    plan.EncodeWaitCapSeconds = MenuEncodeWaitSeconds;
                }

                // Stage M2b: a menu capture's time caps - see MenuCaptureBudgets. Never set on a raid plan.
                if (plan.MenuMode && MenuCaptureBudgets)
                {
                    plan.MenuBudgets = true;
                    plan.FloorCapSeconds = MenuFloorPhaseSeconds;
                    plan.MeshWatchdogCapSeconds = MenuMeshWatchdogSeconds;
                }

                if (plan.WidthPx < 1 || plan.HeightPx < 1)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: nothing was captured on {key} - {F(widthM)}x{F(heightM)} m at " +
                        $"{plan.Ppm.ToString("0.###", CultureInfo.InvariantCulture)} px/m is not a picture.");
                    plan = null;
                    return false;
                }

                // Counted in SAMPLES, not output pixels: a tile is 2048 samples, which at
                // SupersampleFactor 2 is 1024 output pixels, so a Customs-sized floor takes 9x5 tiles
                // at 8 px/m (5x3 at 4 px/m, where one sample a pixel took 3x2).
                plan.TilesX = (plan.SampleWidth + TileSize - 1) / TileSize;
                plan.TilesY = (plan.SampleHeight + TileSize - 1) / TileSize;

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

                // The three scene-wide reads, all of them once per capture rather than once per tile:
                // what the distance culler has switched off, what renders as water, and where a player
                // can actually get to.
                CollectCulling();
                CollectWater();
                plan.Reach = BuildReach(plan);

                // The OUTCOME, for the render tag: BuildReach returns null when the NavMesh has no
                // triangulation, none of it lies in the extent, or it threw, and such a capture writes
                // alpha 255 everywhere. Before the merge worked that was self-consistent (every capture
                // rewrote the whole picture); merged, a masked set and an unmasked capture would leave a
                // hard seam, so a capture without the mask must not merge into one with it - the same
                // rule the water pass records with its p/n letter.
                _reachBuilt = plan.Reach != null;

                if (!BuildCamera(plan, out var note))
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - {note}.");
                    plan = null;
                    return false;
                }

                // After the camera, because whether the previous capture can be merged into this one
                // depends on the encoding the camera decided (see LoadPrevious).
                //
                // Campaign speed step 2: inside a held campaign the "previous capture" is the held set's meta - the last
                // stop's, not the file, which is the last checkpoint's - held to the same checks. One that no longer fits
                // is let go (its stops said lost) and this stop starts from disk, as the first stop of a campaign does.
                // Stage M2: a menu capture is no campaign stop - it never merges from, joins or starts a campaign's hold,
                // whatever static a raid left behind; it reads and writes the files on disk.
                var hold = plan.MenuMode ? null : LiveHold(key);
                plan.Previous = LoadPrevious(plan, _needsGamma, RenderTag, hold?.Meta);

                if (hold?.Meta != null && plan.Previous == null && !plan.MenuSetGuarded)
                {
                    DropHold(hold, "the held set no longer fits this capture", raidEnded: false);
                    hold = null;
                    plan.Previous = LoadPrevious(plan, _needsGamma, RenderTag);
                }

                // Stage M2c: the stored set is a menu set this raid capture may not merge into - LoadPrevious said so.
                // Stop here, before anything is held or written, so the set stays exactly as it is.
                if (plan.MenuSetGuarded)
                {
                    plan = null;
                    return false;
                }

                // Stage M3: a menu capture without Replace found a stored set it cannot merge into (a raid set, another
                // density) - LoadPrevious said why. The set is left exactly as it is; the Maps tab offers Replace.
                if (plan.MenuNeedsReplace != null)
                {
                    if (_menu != null) _menu.NeedsReplace = plan.MenuNeedsReplace;
                    plan = null;
                    return false;
                }

                plan.Hold = plan.MenuMode ? null : hold ?? NewHold(plan);

                plan.Captures = plan.Previous == null ? 1 : Math.Max(1, plan.Previous.Captures) + 1;
                plan.FirstCapturedAt = plan.Previous == null
                    ? null
                    : FirstOf(plan.Previous);

                // The 3D mesh's cost gate, HERE and not in the plan above, because it depends on
                // whether the previous meta was accepted: a key press and a campaign stop always build
                // the geometry, and an AutoCapture tick - which comes round every few seconds - builds
                // it only when this map has none that the new meta will be able to carry forward. Phase
                // 3-0 measured why it is gated at all: the relief is 45 ms and DETERMINISTIC (colliders
                // do not stream out, so every capture produces the same grid), but the building walk is
                // a pass over 184,000 renderers and a handful of GPU readbacks.
                //
                // CarriedMesh, not File.Exists: a mesh file on disk that the new meta cannot name -
                // because this capture's recipe or scale refused the previous meta - is a file nothing
                // will read, and skipping the build for it would silently lose the map's geometry.
                plan.WantsMesh = !_skipMesh && (!_automatic || CarriedMesh(plan, null) == null);

                // The side views' gate. Every key press and every campaign stop takes them: they MERGE
                // best-of-by-distance now (see SidePrevious), so each stop sharpens the walls near it and
                // nothing a stop renders is thrown away. An AutoCapture tick: by the mesh's rule and for
                // the same reason - four floor-sized renders are not something to spend every few
                // seconds on a map that already has them. A capture that takes none carries the earlier
                // ones forward (CommitSides).
                plan.WantsSides = !_automatic || CarriedSides(plan).Count == 0;

                if (_culling != null && _culling.Length > 0)
                {
                    note = $"{note}, culling forced ({_cullingObjects} objects)";
                }

                note = _water != null && _water.Length > 0 && _waterFlat != null
                    ? $"{note}, {_water.Length} water-layer renderers painted"
                    : $"{note}, water drawn as is";
                if (plan.Reach != null) note = $"{note}, reach mask {plan.ReachCellsX}x{plan.ReachCellsZ}";

                if (budgetNote != null) note = $"{note}, {budgetNote}";

                // Stage M2: said in the header, so a set's log says which kind of capture wrote it
                if (plan.MenuMode)
                {
                    note = $"{note}, MENU capture (every pixel step 0, no stand, " +
                           $"{(MenuUploads(key) ? "one upload after the write" : "no upload")})";

                    // Stage M2b: the light and the time caps it was taken under
                    if (_rig != null) note = $"{note}, {MenuRigNote(plan)}";

                    // Stage M2c: the ground's density and what decided it - the px/m cap, the long side's pixel cap
                    // (the GPU's one-texture limit), or the memory budget - so a set's log says why it is as sharp as it is
                    var decidedBy = plan.Ppm < wanted
                        ? "the memory budget"
                        : wanted < ppmCap ? $"the {cap.ToString(CultureInfo.InvariantCulture)} px long side" : "the px/m cap";
                    note = fineGround
                        ? $"{note}, finer ground {Ppm(plan.Ppm)} px/m (cap {Ppm(ppmCap)}, set by {decidedBy}; " +
                          $"{Mb(budgetBytes)} MB floor budget (the less of {Mb(MenuCaptureMemoryBudgetCapBytes)} MB and an " +
                          $"eighth of {SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture)} MB RAM), " +
                          $"{MenuMaxFloorPngBytes / (1024 * 1024)} MB PNG cap, " +
                          $"{F0(MenuEncodeWaitSeconds)} s encode wait)"
                        : $"{note}, the raid's ground {Ppm(plan.Ppm)} px/m (MenuFineGround off)";

                    note = plan.MenuBudgets
                        ? $"{note}, menu budgets: floors {F0(plan.FloorCapSeconds)} s, buildings {F0(MenuBuildingSeconds)} s, " +
                          $"atlas {F0(MenuAtlasSeconds)} s, mesh watchdog {F0(plan.MeshWatchdogCapSeconds)} s, sides " +
                          $"{F0(SidePhaseSeconds)} s, worst case {F0(MenuWorstCaseSeconds)} s"
                        : $"{note}, the raid's time budgets (MenuCaptureBudgets off)";
                }

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
                    $"{F(extent.MaxX)},{F(extent.MaxZ)} ({extent.Source}), picture extent " +
                    $"{F(plan.Extent.MinX)},{F(plan.Extent.MinZ)}..{F(plan.Extent.MaxX)},{F(plan.Extent.MaxZ)}, cap {cap}, " +
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

                floor.CameraY = BandCameraY(maxY, floor.NextMinY);

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
                _camera.orthographicSize = TileSize / (2f * plan.SamplePpm);
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
                // in. Customs at 8 px/m, 8944x4312, is 463 MB of it, freed the moment the floor is
                // written. Int arithmetic is safe: Resolution() holds a side to 16384, and
                // 16384 x 16384 x 3 = 805,306,368 is under int.MaxValue.
                floor.Pixels = new float[plan.WidthPx * plan.HeightPx * 3];

                // Which of those pixels the camera actually drew, and how far each was from the
                // player - the two things the merge decides on.
                floor.Drawn = new bool[plan.WidthPx * plan.HeightPx];
                floor.Dist = new byte[plan.WidthPx * plan.HeightPx];
                return true;
            }
            catch (Exception ex)
            {
                NoteOutOfMemory(plan, ex);
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was not captured " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>
        /// The height a band is photographed from: the ONE rule, so the picture's camera and the 3D
        /// relief's rays start in the same place.
        ///
        /// The topmost band - which is every band of a single-band map, Customs included - is the
        /// outside of the world, and its buildings are the map. Its camera goes
        /// <see cref="TopBandCameraHeight"/> up so roofs, upper walls and their shadows are all in
        /// front of it; nothing is above it to clip.
        ///
        /// An INTERIOR band has another floor over it, and that floor's slab would hide everything the
        /// band is for. Its camera goes just under the band above - <see cref="CeilingClearance"/>
        /// below the upper band's lower edge - so the slab is behind the near plane and cut away, and
        /// the room's own walls and contents are not. Two bands can end up close enough together that
        /// the clearance would put the camera below the surface it is photographing, which is what
        /// <see cref="MinCameraAboveBand"/> holds it off.
        ///
        /// Extracted from <see cref="BeginFloor"/> when the 3D relief needed it: a second copy of this
        /// arithmetic in MapMeshBuilder would be a relief sampled from a different height than the
        /// picture drawn over it, and nothing downstream could tell.
        /// </summary>
        /// <param name="maxY">The band's own upper edge.</param>
        /// <param name="nextMinY">The lower edge of the band directly above, or NaN for the topmost
        /// band.</param>
        private static float BandCameraY(float maxY, float nextMinY)
        {
            if (!IsFinite(nextMinY)) return maxY + TopBandCameraHeight;

            var ceiling = nextMinY - CeilingClearance;

            return ceiling < maxY + MinCameraAboveBand ? maxY + MinCameraAboveBand : ceiling;
        }

        /// <summary>Renders one tile and copies it into the floor's pixel buffer at that tile's
        /// offset. The last row and column of tiles are rendered whole and read back clipped, so every
        /// tile is the same view size and the arithmetic the self-check proved holds for all of them.
        ///
        /// Two steps rather than one, unlike the eight-bit version this replaces: the render target is
        /// half-float and cannot be read straight into the RGBA texture a PNG is made from, so the tile
        /// lands in the staging texture and its values are copied out as floats.
        ///
        /// The copy goes through <see cref="ReadSampleRow"/> and a NativeArray, NOT GetPixels. GetPixels
        /// ALLOCATES the array it hands back - sixteen bytes a pixel, so 67 MB for a 2048 tile, or 8 MB
        /// a band if the copy is banded - and doing that sixteen times a floor on a Mono heap that has
        /// been running a raid for half an hour is what made Interchange's first floor die at tile 12
        /// with "scripting array creation failed, array size or length is too large". GetPixelData hands
        /// back a view of the texture's own memory and allocates nothing at all; the only buffers left
        /// are two reused rows of <see cref="TileSize"/> samples.</summary>
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

                // In SAMPLES. Both the tile size and the sample dimensions are multiples of
                // SupersampleFactor, so every tile holds whole sample blocks and no output pixel is
                // ever split across two tiles.
                var tw = Math.Min(TileSize, plan.SampleWidth - px0);
                var th = Math.Min(TileSize, plan.SampleHeight - py0);
                if (tw <= 0 || th <= 0) return;

                // WP4 A2: the staging texture exists only while the synchronous path may need it - made here on
                // demand (a retry, or a session the proof or the error limit turned back to ReadPixels).
                EnsureStage();

                PositionCamera(plan, floor, px0, py0);
                RenderOnce();

                RenderTexture.active = _rt;

                // Unity counts the RenderTexture's rows from the BOTTOM, so the tile's own TOP rows -
                // the ones inside the extent when the tile is clipped - are the last th rows of the
                // render. They are read to the staging texture's origin, which is why the band loop
                // below starts at 0 rather than TileSize-th. A clipped tile therefore drops its bottom
                // and its right, which is exactly the part that lies outside the extent.
                // No Apply: it would upload the 33 MB staging texture to the GPU, and the only reader
                // is ReadSampleRow below, which reads the CPU-side copy ReadPixels just filled.
                _stage.ReadPixels(new Rect(0f, TileSize - th, tw, th), 0, 0);

                // WP4 A1: the average, shared with the asynchronous path - the staging texture's rows start at the
                // tile's top row (see above), so rowOffset 0.
                var averaging = Stopwatch.StartNew();

                AverageTile(plan, floor, px0, py0, tw, th,
                    _hdr ? _stage.GetPixelData<Half4>(0) : default,
                    _hdr ? default : _stage.GetPixelData<Color32>(0),
                    0);

                _floorAverageMs += averaging.Elapsed.TotalMilliseconds;
                _floorAveraged++;
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

        /// <summary>WP4 A2: the staging texture, made when it is not there. The synchronous path's only buffer: built
        /// by BuildTarget when the ring is not, by VerifyNow for the proof, and by RenderTile for a retry or a session
        /// turned back to ReadPixels.</summary>
        private void EnsureStage()
        {
            if (_stage != null) return;

            _stage = new Texture2D(
                TileSize, TileSize,
                _hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32,
                mipChain: false);
        }

        /// <summary>WP4 A2: the readback ring, or false - with the reason in <paramref name="note"/> - for the
        /// synchronous path. Only the two sample layouts ReadSampleRow reads, and what the staging texture has always
        /// held, are accepted: R,G,B,A halves or R,G,B,A bytes, checked on the target's graphicsFormat rather than on
        /// the RenderTextureFormat's historical name.</summary>
        /// <param name="note">"async x3 (format)" or "ReadPixels (why)", for the capture header.</param>
        private bool BuildRing(out string note)
        {
            // Anything a previous capture left (Cleanup always runs, but a ring is never dropped unreleased).
            ReleaseRing();
            _readbackErrors = 0;

            if (AverageOnWorker)
                Plugin.LogSource?.LogDebug(
                    "QuestTree: AverageOnWorker (WP4 A3) is specified but not built in this version - tiles are averaged " +
                    "on the main thread.");

            if (!AsyncTileReadback)
            {
                note = "ReadPixels (async off by switch)";
                return false;
            }

            if (_asyncReadbackOff)
            {
                note = "ReadPixels (async turned off earlier this session)";
                return false;
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                note = "ReadPixels (no AsyncGPUReadback on this device)";
                return false;
            }

            if (_rt == null || !_rt.IsCreated())
            {
                note = "ReadPixels (the target is created on first use)";
                return false;
            }

            var format = _rt.graphicsFormat;
            var readable = _hdr
                ? format == GraphicsFormat.R16G16B16A16_SFloat
                : format == GraphicsFormat.R8G8B8A8_UNorm || format == GraphicsFormat.R8G8B8A8_SRGB;

            if (!readable)
            {
                note = $"ReadPixels (target is {format}, not a layout the async path reads)";
                return false;
            }

            if (_msaa == 1 && SystemInfo.copyTextureSupport == CopyTextureSupport.None)
            {
                note = "ReadPixels (no CopyTexture for a single-sample target)";
                return false;
            }

            var slots = new ReadbackSlot[ReadbackRing];

            try
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    // Same size, graphicsFormat and sRGB-ness as the render target; one sample, no depth.
                    var d = _rt.descriptor;
                    d.msaaSamples = 1;
                    d.depthBufferBits = 0;
                    d.bindMS = false;
                    d.useMipMap = false;
                    d.autoGenerateMips = false;

                    var target = new RenderTexture(d) { name = "QuestTreeCaptureResolve" + i };
                    slots[i] = new ReadbackSlot { Target = target };

                    if (!target.Create()) throw new InvalidOperationException("a resolve target would not create");

                    if (_hdr)
                        slots[i].Half = new NativeArray<Half4>(TileSize * TileSize, Allocator.Persistent,
                            NativeArrayOptions.UninitializedMemory);
                    else
                        slots[i].Bytes = new NativeArray<Color32>(TileSize * TileSize, Allocator.Persistent,
                            NativeArrayOptions.UninitializedMemory);
                }
            }
            catch (Exception ex)
            {
                FreeSlots(slots);
                note = $"ReadPixels (ring refused: {ex.GetType().Name}: {ex.Message})";
                return false;
            }

            _ring = slots;
            note = $"async x{ReadbackRing} ({format})";
            return true;
        }

        /// <summary>WP4 A2: frees slots that never had a request - BuildRing's refusal path.</summary>
        /// <param name="slots">The slots, some possibly null.</param>
        private static void FreeSlots(ReadbackSlot[] slots)
        {
            foreach (var slot in slots)
            {
                if (slot == null) continue;

                try
                {
                    if (slot.Half.IsCreated) slot.Half.Dispose();
                    if (slot.Bytes.IsCreated) slot.Bytes.Dispose();

                    if (slot.Target != null)
                    {
                        slot.Target.Release();
                        Destroy(slot.Target);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: a readback slot could not be freed ({ex.Message}).");
                }
            }
        }

        /// <summary>WP4 A2: the MSAA target's samples into a ring slot's single-sample target. The hardware resolve -
        /// the same one ReadPixels triggers internally on a multisampled active target - or, for one sample, a raw
        /// texel copy. Never Blit: a shader pass samples, and exactness would then hang on filtering and precision.</summary>
        /// <param name="target">The slot's resolve target.</param>
        private void ResolveInto(RenderTexture target)
        {
            if (_msaa > 1) _rt.ResolveAntiAliasedSurface(target);
            else Graphics.CopyTexture(_rt, 0, 0, target, 0, 0);
        }

        /// <summary>WP4 A2: the proof's key - a proof holds for one format and one sample count.</summary>
        private string ProvenKey() => $"{_rt.graphicsFormat}/{_msaa}";

        /// <summary>WP4 A2: renders one tile, resolves it into a ring slot and asks for it back - the first half of
        /// RenderTile. The tile's pixels arrive in a later frame through ConsumeSlot, except while the session's proof
        /// is outstanding (or VerifyEveryTileReadback), when VerifyNow completes and consumes it in this frame. A throw
        /// in the render fails the floor exactly as RenderTile's catch does. A throw in the steps only the asynchronous
        /// path has - the resolve, the request, the proof - turns the session back to ReadPixels (one Warning), abandons
        /// the slots in flight (their tiles go on the retry list) and returns false: the caller renders THIS tile again
        /// through RenderTile, under the same hold, and the floor survives.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being rendered.</param>
        /// <param name="tile">The tile's index, row-major from the top-left.</param>
        /// <param name="slot">A free slot.</param>
        /// <returns>False when the tile must be rendered again synchronously.</returns>
        private bool SubmitTile(Plan plan, FloorPlan floor, int tile, ReadbackSlot slot)
        {
            var previousActive = RenderTexture.active;
            var rendered = false;

            try
            {
                var tileX = tile % plan.TilesX;
                var tileY = tile / plan.TilesX;
                var px0 = tileX * TileSize;
                var py0 = tileY * TileSize;
                var tw = Math.Min(TileSize, plan.SampleWidth - px0);
                var th = Math.Min(TileSize, plan.SampleHeight - py0);
                if (tw <= 0 || th <= 0) return true;

                PositionCamera(plan, floor, px0, py0);
                RenderOnce();
                rendered = true;

                ResolveInto(slot.Target);

                var verify = VerifyEveryTileReadback || _asyncReadbackProven != ProvenKey();

                // The overload with no format and no region: the whole target in its own format, no conversion. A
                // clipped edge tile reads a few MB it does not need, which is harmless.
                if (_hdr) slot.Request = AsyncGPUReadback.RequestIntoNativeArray(ref slot.Half, slot.Target, 0);
                else slot.Request = AsyncGPUReadback.RequestIntoNativeArray(ref slot.Bytes, slot.Target, 0);

                slot.InFlight = true;
                slot.Plan = plan;
                slot.Floor = floor;
                slot.Tile = tile;
                slot.Px0 = px0;
                slot.Py0 = py0;
                slot.Tw = tw;
                slot.Th = th;
                _inFlight.Enqueue(slot);

                if (verify) VerifyNow(slot);
                return true;
            }
            catch (Exception ex)
            {
                if (!rendered)
                {
                    floor.Failed = true;
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was abandoned at tile {tile + 1} of " +
                        $"{plan.TileCount} ({ex.GetType().Name}: {ex.Message}).");
                    return true;
                }

                if (!_asyncReadbackOff)
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the asynchronous tile readback failed at {plan.Key} \"{floor.Dto?.Name}\" tile " +
                        $"{tile + 1} ({ex.GetType().Name}: {ex.Message}) - captures go back to ReadPixels for this session; " +
                        "the tile is rendered again.");

                _asyncReadbackOff = true;
                AbandonForRetry();
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        /// <summary>WP4 A2 (review): every slot in flight abandoned (AbandonInFlight), each live tile among them put on the
        /// retry list - so turning the session back to ReadPixels mid-pass costs no pixel.</summary>
        private void AbandonForRetry()
        {
            foreach (var slot in _inFlight)
                if (slot.Floor != null && !slot.Floor.Failed && slot.Floor.Pixels != null && !_retryTiles.Contains(slot.Tile))
                    _retryTiles.Add(slot.Tile);

            AbandonInFlight();
        }

        /// <summary>WP4 A2, the self-check - always on, until it passes once per session per format and sample count: the
        /// old read of the SAME render (ReadPixels of the still-bound target into the staging texture), the readback
        /// waited for, and every sample of the tile compared bit for bit. Equal on a tile that Discriminates: the key is
        /// proven, the staging texture goes (unless VerifyEveryTileReadback) and the tile is averaged from the readback.
        /// Equal on one that does not (empty, or its own vertical mirror): averaged from the verified readback, the proof
        /// left open for the next tile (ReadbackProofTiles). Not equal, or a readback error:
        /// the session goes back to ReadPixels and THIS tile is averaged from the staging texture - so the output is the
        /// old build's whichever way the check goes.</summary>
        /// <param name="slot">The slot just submitted (the only one in flight).</param>
        private void VerifyNow(ReadbackSlot slot)
        {
            EnsureStage();

            RenderTexture.active = _rt;
            _stage.ReadPixels(new Rect(0f, TileSize - slot.Th, slot.Tw, slot.Th), 0, 0);

            slot.Request.WaitForCompletion();

            var error = slot.Request.hasError;
            var mismatches = error ? -1 : SameSamples(slot);

            // A match on a tile that cannot tell a correct readback from a flipped or row-offset one proves nothing:
            // the tile is consumed from the (verified) data, and the proof stays open for the next tile.
            if (mismatches == 0 && _asyncReadbackProven != ProvenKey() && !Discriminates(slot))
            {
                _proofTilesTried++;

                if (_proofTilesTried >= ReadbackProofTiles && !_proofTilesSaid)
                {
                    _proofTilesSaid = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: no tile of {_proofTilesTried} could prove the asynchronous tile readback on " +
                        $"{ProvenKey()} (each was empty or its own vertical mirror) - every tile goes on being checked " +
                        "against ReadPixels until one can.");
                }

                ConsumeSlot(slot, fromStage: false);
                return;
            }

            if (mismatches == 0)
            {
                var first = _asyncReadbackProven != ProvenKey();
                _asyncReadbackProven = ProvenKey();

                if (first)
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: asynchronous tile readback proven bit-identical to ReadPixels on {ProvenKey()} " +
                        $"({(long)slot.Tw * slot.Th} samples).");
                else
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: tile {slot.Tile + 1} readback matched ReadPixels ({(long)slot.Tw * slot.Th} samples).");

                if (!VerifyEveryTileReadback && _stage != null)
                {
                    var stage = _stage;
                    _stage = null;
                    Destroy(stage);
                }

                ConsumeSlot(slot, fromStage: false);
                return;
            }

            _asyncReadbackOff = true;
            if (mismatches > 0) _floorMismatches += mismatches;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the asynchronous tile readback did not match ReadPixels on {ProvenKey()} " +
                (error
                    ? "(the readback reported an error)"
                    : $"({mismatches} of {(long)slot.Tw * slot.Th} samples differ)") +
                " - captures go back to ReadPixels for this session.");

            ConsumeSlot(slot, fromStage: true);
        }

        /// <summary>WP4 A2 (review): whether this tile can PROVE the readback - it is not all zero, and some row r differs
        /// from row th-1-r, so a flipped or row-offset readback of it could not match. Read on the staging texture's data
        /// (ReadPixels' own), rows 0..th-1, columns 0..tw-1.</summary>
        /// <param name="slot">The verified slot (its Tw and Th).</param>
        private bool Discriminates(ReadbackSlot slot)
        {
            var tw = slot.Tw;
            var th = slot.Th;
            var nonZero = false;
            var asymmetric = false;

            if (_hdr)
            {
                var data = _stage.GetPixelData<Half4>(0);

                for (var r = 0; r < th && !(nonZero && asymmetric); r++)
                {
                    var a = r * TileSize;
                    var b = (th - 1 - r) * TileSize;

                    for (var x = 0; x < tw; x++)
                    {
                        var p = data[a + x];
                        if (!nonZero && (p.R | p.G | p.B | p.A) != 0) nonZero = true;

                        if (!asymmetric)
                        {
                            var q = data[b + x];
                            if (p.R != q.R || p.G != q.G || p.B != q.B || p.A != q.A) asymmetric = true;
                        }

                        if (nonZero && asymmetric) break;
                    }
                }

                return nonZero && asymmetric;
            }

            var bytes = _stage.GetPixelData<Color32>(0);

            for (var r = 0; r < th && !(nonZero && asymmetric); r++)
            {
                var a = r * TileSize;
                var b = (th - 1 - r) * TileSize;

                for (var x = 0; x < tw; x++)
                {
                    var p = bytes[a + x];
                    if (!nonZero && (p.r | p.g | p.b | p.a) != 0) nonZero = true;

                    if (!asymmetric)
                    {
                        var q = bytes[b + x];
                        if (p.r != q.r || p.g != q.g || p.b != q.b || p.a != q.a) asymmetric = true;
                    }

                    if (nonZero && asymmetric) break;
                }
            }

            return nonZero && asymmetric;
        }

        /// <summary>WP4 A2: how many of the tile's samples differ between the slot (the whole target, rows from the
        /// bottom, the tile's top th rows at TileSize-th..) and the staging texture (those rows at 0..th-1) - the two
        /// paths' own indexing, so a flip or an offset is caught. Compared as raw bits: four ushorts, or four bytes.</summary>
        /// <param name="slot">The completed slot.</param>
        private int SameSamples(ReadbackSlot slot)
        {
            var differ = 0;
            var offset = TileSize - slot.Th;

            if (_hdr)
            {
                var stage = _stage.GetPixelData<Half4>(0);

                for (var r = 0; r < slot.Th; r++)
                {
                    var a = (offset + r) * TileSize;
                    var b = r * TileSize;

                    for (var x = 0; x < slot.Tw; x++)
                    {
                        var p = slot.Half[a + x];
                        var q = stage[b + x];
                        if (p.R != q.R || p.G != q.G || p.B != q.B || p.A != q.A) differ++;
                    }
                }

                return differ;
            }

            var bytes = _stage.GetPixelData<Color32>(0);

            for (var r = 0; r < slot.Th; r++)
            {
                var a = (offset + r) * TileSize;
                var b = r * TileSize;

                for (var x = 0; x < slot.Tw; x++)
                {
                    var p = slot.Bytes[a + x];
                    var q = bytes[b + x];
                    if (p.r != q.r || p.g != q.g || p.b != q.b || p.a != q.a) differ++;
                }
            }

            return differ;
        }

        /// <summary>WP4 A2: the second half of RenderTile - one completed slot averaged into its floor, then freed. A
        /// readback error is counted, its tile goes on the retry list, and ReadbackErrorLimit turns the session back to
        /// ReadPixels. An abandoned floor (failed, or its buffers gone) only frees the slot.</summary>
        /// <param name="slot">The oldest slot in flight (FIFO), or the verify slot just enqueued.</param>
        /// <param name="fromStage">Average from the staging texture (the proof failed) rather than the slot.</param>
        private void ConsumeSlot(ReadbackSlot slot, bool fromStage)
        {
            RemoveFromQueue(slot);
            slot.InFlight = false;

            var plan = slot.Plan;
            var floor = slot.Floor;
            slot.Plan = null;
            slot.Floor = null;

            if (floor == null || plan == null || floor.Failed || floor.Pixels == null) return;

            if (!fromStage && slot.Request.hasError)
            {
                ReadbackFailed(slot.Tile);
                return;
            }

            var clock = Stopwatch.StartNew();

            try
            {
                if (fromStage)
                    AverageTile(plan, floor, slot.Px0, slot.Py0, slot.Tw, slot.Th,
                        _hdr ? _stage.GetPixelData<Half4>(0) : default,
                        _hdr ? default : _stage.GetPixelData<Color32>(0),
                        0);
                else
                {
                    AverageTile(plan, floor, slot.Px0, slot.Py0, slot.Tw, slot.Th, slot.Half, slot.Bytes, TileSize - slot.Th);
                    _floorAsync++;
                }

                _floorAveraged++;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" was abandoned at tile {slot.Tile + 1} of " +
                    $"{plan.TileCount} ({ex.GetType().Name}: {ex.Message}).");
            }

            _floorAverageMs += clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>WP4 A2: one failed readback - counted, its tile put on the retry list (re-rendered synchronously
        /// after the drain), and at ReadbackErrorLimit in one capture the session goes back to ReadPixels.</summary>
        /// <param name="tile">The tile whose readback failed.</param>
        private void ReadbackFailed(int tile)
        {
            _readbackErrors++;
            _floorErrors++;
            _retryTiles.Add(tile);

            if (_readbackErrors < ReadbackErrorLimit || _asyncReadbackOff) return;

            _asyncReadbackOff = true;
            Plugin.LogSource?.LogWarning(
                $"QuestTree: {_readbackErrors} asynchronous tile readbacks failed in this capture - captures go back to " +
                "ReadPixels for this session (every failed tile is rendered again).");
        }

        /// <summary>WP4 A2: takes a slot off the in-flight queue - the head in every call but VerifyNow's, where it is
        /// the only entry.</summary>
        /// <param name="slot">The slot.</param>
        private void RemoveFromQueue(ReadbackSlot slot)
        {
            if (_inFlight.Count == 0) return;

            if (ReferenceEquals(_inFlight.Peek(), slot))
            {
                _inFlight.Dequeue();
                return;
            }

            var rest = _inFlight.Where(s => !ReferenceEquals(s, slot)).ToList();
            _inFlight.Clear();
            foreach (var s in rest) _inFlight.Enqueue(s);
        }

        /// <summary>WP4 A2: the oldest slot in flight consumed when its readback has completed (polled, never waited
        /// for) - at most one average a frame. False when nothing was consumed.</summary>
        private bool ConsumeOldestIfDone()
        {
            if (_inFlight.Count == 0) return false;

            var head = _inFlight.Peek();
            head.Request.Update();
            if (!head.Request.done) return false;

            ConsumeSlot(head, fromStage: false);
            return true;
        }

        /// <summary>WP4 A2: waits for the oldest slot in flight - never longer than ReadPixels' own stall - and consumes
        /// it. A wait that throws marks the slot broken and abandons it.</summary>
        private void WaitOldest()
        {
            var head = _inFlight.Peek();
            var clock = Stopwatch.StartNew();

            try
            {
                head.Request.WaitForCompletion();
            }
            catch (Exception ex)
            {
                head.Broken = true;
                RemoveFromQueue(head);
                head.InFlight = false;

                var live = head.Floor != null && !head.Floor.Failed && head.Floor.Pixels != null;
                head.Plan = null;
                head.Floor = null;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: a tile readback would not be waited for ({ex.Message}) - its slot is retired and its tile " +
                    "rendered again.");

                if (live) ReadbackFailed(head.Tile);
                return;
            }
            finally
            {
                _floorWaits++;
                _floorWaitMs += clock.Elapsed.TotalMilliseconds;
            }

            ConsumeSlot(head, fromStage: false);
        }

        /// <summary>WP4 A2: the first ring slot not in flight and not broken, or null.</summary>
        private ReadbackSlot FreeSlot()
        {
            if (_ring == null) return null;

            foreach (var slot in _ring)
                if (slot != null && !slot.InFlight && !slot.Broken) return slot;

            return null;
        }

        /// <summary>WP4 A2: every slot in flight waited for (guarded) and freed WITHOUT being consumed - a cut or failed
        /// floor. A wait that throws leaves its slot broken rather than free.</summary>
        private void AbandonInFlight()
        {
            while (_inFlight.Count > 0)
            {
                var slot = _inFlight.Dequeue();

                try
                {
                    slot.Request.WaitForCompletion();
                }
                catch (Exception ex)
                {
                    slot.Broken = true;
                    Plugin.LogSource?.LogDebug($"QuestTree: an abandoned tile readback would not be waited for ({ex.Message}).");
                }

                slot.InFlight = false;
                slot.Plan = null;
                slot.Floor = null;
            }
        }

        /// <summary>WP4 A2: the ring given back - called by Cleanup before anything else is freed. A slot in flight is
        /// waited for first: its destination is OUR NativeArray, and freeing it under a pending copy would be a GPU to
        /// CPU write into freed memory (the rule MapMeshBuilder states for its GraphicsBuffers). A wait that throws
        /// leaves that 32 MB array allocated rather than freed under it. Safe twice.</summary>
        private void ReleaseRing()
        {
            if (_ring == null)
            {
                _inFlight.Clear();
                _retryTiles.Clear();
                return;
            }

            foreach (var slot in _ring)
            {
                if (slot == null) continue;

                var safeToFree = !slot.Broken;

                if (slot.InFlight)
                {
                    try
                    {
                        slot.Request.WaitForCompletion();
                    }
                    catch (Exception ex)
                    {
                        safeToFree = false;
                        Plugin.LogSource?.LogDebug(
                            $"QuestTree: a tile readback would not be waited for ({ex.Message}) - its 32 MB buffer is left " +
                            "allocated rather than freed under it.");
                    }

                    slot.InFlight = false;
                }

                try
                {
                    if (safeToFree)
                    {
                        if (slot.Half.IsCreated) slot.Half.Dispose();
                        if (slot.Bytes.IsCreated) slot.Bytes.Dispose();
                    }

                    if (slot.Target != null)
                    {
                        slot.Target.Release();
                        Destroy(slot.Target);
                        slot.Target = null;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: a readback slot could not be freed ({ex.Message}).");
                }

                slot.Plan = null;
                slot.Floor = null;
            }

            _ring = null;
            _inFlight.Clear();
            _retryTiles.Clear();
        }

        /// <summary>WP4 A2: one pass over a list of tiles - the first pass or a batch of the water rule's late ones -
        /// through the readback ring: one render a frame, as before; each tile's pixels averaged in the frame its
        /// readback is found complete, at most ONE average a frame; a full ring with nothing complete waits for the
        /// oldest (never longer than ReadPixels' own stall); then the drain, and then - still inside the caller's scene
        /// hold - a synchronous re-render of any tile whose readback failed. With no ring, or the session turned back
        /// to ReadPixels, every tile goes through RenderTile exactly as before. Every tile the pass rendered is in
        /// Pixels and Drawn when it returns, which the water rule needs.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        /// <param name="tiles">The tiles, in order.</param>
        /// <param name="overrun">Asked before every render; true fails the floor.</param>
        private IEnumerator RenderPass(Plan plan, FloorPlan floor, List<int> tiles, Func<bool> overrun)
        {
            // Nothing of an earlier pass can still be here (each drains), except after a throw it did not survive.
            if (_inFlight.Count > 0) AbandonInFlight();
            _retryTiles.Clear();

            for (var k = 0; k < tiles.Count; k++)
            {
                var tile = tiles[k];

                if (overrun())
                {
                    floor.Failed = true;
                    break;
                }

                if (_ring == null || _asyncReadbackOff)
                {
                    RenderTile(plan, floor, tile);
                    if (floor.Failed) break;

                    // The whole point of the coroutine: one render and one readback per frame, never two.
                    yield return null;
                    continue;
                }

                var consumed = ConsumeOldestIfDone();
                var slot = FreeSlot();

                if (slot == null && !consumed && _inFlight.Count > 0)
                {
                    // Ring full and nothing finished: wait for the oldest.
                    WaitOldest();
                    slot = FreeSlot();
                }

                // Every slot retired (each wait threw): nothing can be submitted again this session.
                if (slot == null && _inFlight.Count == 0 && !_asyncReadbackOff)
                {
                    _asyncReadbackOff = true;
                    Plugin.LogSource?.LogWarning(
                        "QuestTree: every asynchronous tile readback slot was retired - captures go back to ReadPixels for " +
                        "this session.");
                }

                if (floor.Failed) break;

                // An error just turned the session back to ReadPixels: this tile goes the synchronous way.
                if (_asyncReadbackOff)
                {
                    k--;
                    continue;
                }

                if (slot == null)
                {
                    // Consumed one this frame and the ring is still full: the render waits a frame (a backpressure
                    // frame - counted with the waits).
                    _floorWaits++;
                    yield return null;
                    k--;
                    continue;
                }

                var submitted = SubmitTile(plan, floor, tile, slot);
                if (floor.Failed) break;

                yield return null;

                // The asynchronous steps threw and the session went back to ReadPixels: this tile again, the
                // synchronous way, next frame (one render a frame).
                if (!submitted) k--;
            }

            // THE DRAIN - before the caller runs the water rule or releases the scene, so every tile is folded in and
            // a retry below still renders under the same hold.
            var frames = 0;
            var drained = false;

            while (_inFlight.Count > 0)
            {
                if (floor.Failed)
                {
                    AbandonInFlight();
                    break;
                }

                if (ConsumeOldestIfDone())
                {
                    drained = true;
                    if (_inFlight.Count > 0) yield return null;
                    continue;
                }

                if (++frames > ReadbackDrainFrames)
                {
                    WaitOldest();
                    drained = true;
                    if (_inFlight.Count > 0) yield return null;
                    continue;
                }

                yield return null;
            }

            // The last average had its frame; a retry render or the caller's water rule gets the next one.
            if (drained) yield return null;

            foreach (var tile in _retryTiles.ToArray())
            {
                if (floor.Failed) break;

                if (overrun())
                {
                    floor.Failed = true;
                    break;
                }

                RenderTile(plan, floor, tile);
                if (floor.Failed) break;

                yield return null;
            }

            _retryTiles.Clear();
        }

        /// <summary>WP4 A2: the per-floor and per-side Debug line, after the last pass's drain.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private void ReadbackLine(Plan plan, FloorPlan floor)
        {
            if (_floorAveraged == 0 && _floorErrors == 0) return;

            var perTile = _floorAveraged > 0 ? _floorAverageMs / _floorAveraged : 0d;

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tiles: {_floorAsync} read back async, {_floorWaits} waited on " +
                $"({Ms(_floorWaitMs)} ms), {_floorErrors} readback error(s) re-rendered, averaging {Ms(_floorAverageMs)} ms " +
                $"({perTile.ToString("0.0", CultureInfo.InvariantCulture)} ms a tile, half table " +
                $"{(_hdr && HalfTableEnabled ? "on" : "off")})" +
                (VerifyEveryTileReadback ? $", verified against ReadPixels: {_floorMismatches} mismatches" : "") + ".");
        }

        /// <summary>WP4 A1: one tile's samples folded into the floor - the average RenderTile always did, moved here
        /// VERBATIM so the synchronous and the asynchronous readback run the same arithmetic on the same bits. The
        /// only edited line is the row read, which takes its data and its row offset from the caller: the staging
        /// texture holds the tile's top th rows at its rows 0..th-1 (rowOffset 0); a whole-target readback holds the
        /// render target's own rows, the tile's top th being rows TileSize-th..TileSize-1 (rowOffset TileSize - th).
        /// dataRow = rowOffset + row + dy is then the same render-target texel on both paths.
        ///
        /// Throws on anything unexpected; the caller's catch fails the floor with the "abandoned at tile" line.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being rendered.</param>
        /// <param name="px0">The tile's left edge, in samples.</param>
        /// <param name="py0">The tile's top edge, in samples.</param>
        /// <param name="tw">The tile's width inside the extent, in samples.</param>
        /// <param name="th">The tile's height inside the extent, in samples.</param>
        /// <param name="half">The samples when _hdr (row stride TileSize, row 0 at the bottom), else default.</param>
        /// <param name="bytes">The samples when !_hdr (row stride TileSize, row 0 at the bottom), else default.</param>
        /// <param name="rowOffset">The data row holding the tile's top sample row.</param>
        private void AverageTile(Plan plan, FloorPlan floor, int px0, int py0, int tw, int th, NativeArray<Half4> half,
            NativeArray<Color32> bytes, int rowOffset)
        {
            // The floor buffer is kept in TEXTURE order - row 0 at the bottom, world -z - because
            // that is the order SetPixels32 and EncodeToPNG want. sampleBase is this tile's first
            // SAMPLE row in that order; it is a multiple of SupersampleFactor because the sample
            // height, the tile size and py0 all are, which is what lets two sample rows be folded
            // into one output row without any carry between tiles or bands.
            var sampleBase = plan.SampleHeight - py0 - th;
            var outCol0 = px0 / SupersampleFactor;
            var outCols = tw / SupersampleFactor;

            // Two sample rows at a time: one output row, and each output pixel's four samples are
            // all in hand at once, so nothing has to be accumulated across frames. The rows come
            // out of the staging texture's own memory - see ReadSampleRow.
            {
                for (var row = 0; row + SupersampleFactor <= th; row += SupersampleFactor)
                {
                    var outRow = (sampleBase + row) / SupersampleFactor;
                    var pixelRow = outRow * plan.WidthPx + outCol0;

                    for (var dy = 0; dy < SupersampleFactor; dy++) ReadSampleRow(half, bytes, rowOffset + row + dy, tw, _sampleRows[dy]);

                    for (var outCol = 0; outCol < outCols; outCol++)
                    {
                        var r = 0f;
                        var g = 0f;
                        var b = 0f;
                        var drawn = 0;

                        for (var dy = 0; dy < SupersampleFactor; dy++)
                        {
                            var samples = _sampleRows[dy];
                            var source = outCol * SupersampleFactor * 4;

                            for (var dx = 0; dx < SupersampleFactor; dx++)
                            {
                                var sampleAt = source + dx * 4;
                                var sample = new Color(
                                    samples[sampleAt], samples[sampleAt + 1],
                                    samples[sampleAt + 2], samples[sampleAt + 3]);

                                // The camera clears to (0,0,0,0) and draws nothing over a chunk the
                                // game has streamed out, so a sample with anything at all in any
                                // channel - alpha included, which opaque geometry writes as 1 - was
                                // drawn, and one that is four exact zeroes was not. Both signals
                                // together rather than either alone: a rendered sample in true black
                                // shadow has alpha, and a shader that writes no alpha still has
                                // colour.
                                // NOT A NUMBER is treated as not drawn, and this is the first of
                                // three places that stop it - see FillLuminance and Smooth for the
                                // other two. A half-float HDR render can hand back a NaN (a shader
                                // dividing by a zero-length vector is the usual way), it survives
                                // every comparison below by being false to all of them, and four
                                // stops of a Customs campaign died of one: it reached the smoothing
                                // filter, whose range-weight lookup casts a float to an int, and
                                // Mono's cast of a NaN is int.MinValue rather than the 0 a desktop
                                // .NET gives - an index a long way outside the array.
                                // Alpha is tested too, because alpha is half of the drawn test below:
                                // a NaN alpha is false to "a <= 0f", so a sample with three zero
                                // colour channels and a NaN alpha would count as DRAWN - an empty
                                // pixel recorded as a black one, in the mask the whole picture is
                                // composed against.
                                if (!IsFinite(sample.r) || !IsFinite(sample.g) || !IsFinite(sample.b) ||
                                    !IsFinite(sample.a))
                                {
                                    continue;
                                }

                                if (sample.r <= 0f && sample.g <= 0f && sample.b <= 0f && sample.a <= 0f)
                                {
                                    continue;
                                }

                                r += sample.r;
                                g += sample.g;
                                b += sample.b;
                                drawn++;
                            }
                        }

                        var index = pixelRow + outCol;
                        var at = index * 3;

                        // The mean of the DRAWN samples, not of all four: a pixel half covered by a
                        // roof edge is the roof's colour rather than the roof mixed with the clear
                        // colour, which would draw a dark fringe around everything.
                        if (drawn > 0)
                        {
                            var inverse = 1f / drawn;
                            floor.Pixels[at] = r * inverse;
                            floor.Pixels[at + 1] = g * inverse;
                            floor.Pixels[at + 2] = b * inverse;
                            floor.Drawn[index] = true;
                        }
                        else
                        {
                            floor.Pixels[at] = 0f;
                            floor.Pixels[at + 1] = 0f;
                            floor.Pixels[at + 2] = 0f;
                            floor.Drawn[index] = false;
                        }
                    }
                }
            }

            floor.Tiles++;
        }

        /// <summary>One row of the staging texture as plain floats, four to a pixel, read straight out
        /// of the texture's own memory.
        ///
        /// GetPixelData is a VIEW of that memory - no copy, no managed array, nothing for the large
        /// object heap to fragment over - which is the whole reason this method exists; see RenderTile.
        /// The two formats are handled apart rather than through Color, because the half-float one has
        /// to be converted a channel at a time and the eight-bit one is already bytes.
        ///
        /// The branch is per ROW, not per pixel: two thousand pixels of the same format follow every
        /// test.</summary>
        /// <param name="half">The tile's samples when _hdr (row stride TileSize), else default - the staging texture's
        /// GetPixelData view, or a readback slot's own array (WP4 A1).</param>
        /// <param name="bytes">The tile's samples when !_hdr (row stride TileSize), else default.</param>
        /// <param name="dataRow">The row of THAT array, counting from its bottom.</param>
        /// <param name="samples">How many samples of that row to read.</param>
        /// <param name="into">The row buffer to fill, four floats a sample.</param>
        private void ReadSampleRow(NativeArray<Half4> half, NativeArray<Color32> bytes, int dataRow, int samples, float[] into)
        {
            var from = dataRow * TileSize;

            if (_hdr)
            {
                // WP4 A0: Mathf.HalfToFloat's own answers, looked up; null when HalfTableEnabled is off.
                var table = _halfTable;

                for (var x = 0; x < samples; x++)
                {
                    var sample = half[from + x];
                    var at = x * 4;

                    if (table != null)
                    {
                        into[at] = table[sample.R];
                        into[at + 1] = table[sample.G];
                        into[at + 2] = table[sample.B];
                        into[at + 3] = table[sample.A];
                    }
                    else
                    {
                        into[at] = Mathf.HalfToFloat(sample.R);
                        into[at + 1] = Mathf.HalfToFloat(sample.G);
                        into[at + 2] = Mathf.HalfToFloat(sample.B);
                        into[at + 3] = Mathf.HalfToFloat(sample.A);
                    }
                }

                return;
            }

            for (var x = 0; x < samples; x++)
            {
                var sample = bytes[from + x];
                var at = x * 4;

                into[at] = sample.r / 255f;
                into[at + 1] = sample.g / 255f;
                into[at + 2] = sample.b / 255f;
                into[at + 3] = sample.a / 255f;
            }
        }

        /// <summary>One half-float RGBA sample, as it sits in the staging texture's memory. Four
        /// ushorts, which is what RGBAHalf is; Mathf.HalfToFloat turns each into the number it means.
        ///
        /// The four fields are never assigned in C#: GetPixelData hands back the texture's own bytes
        /// reinterpreted as this struct, so the GPU readback is what writes them and the compiler cannot
        /// see it. That is what CS0649 is warning about below, and why it is switched off for exactly
        /// these four lines.</summary>
#pragma warning disable CS0649
        private struct Half4
        {
            public ushort R;
            public ushort G;
            public ushort B;
            public ushort A;
        }
#pragma warning restore CS0649

        /// <summary>WP4 A0: the half-float table, built on first use on the main thread (BuildTarget, once _hdr is
        /// known) and kept for the session; null when <see cref="HalfTableEnabled"/> is off.</summary>
        private static float[] HalfTable()
        {
            if (!HalfTableEnabled) return null;
            if (_halfTable != null) return _halfTable;

            var table = new float[65536];
            for (var i = 0; i < table.Length; i++) table[i] = Mathf.HalfToFloat((ushort)i);

            return _halfTable = table;
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

                // WP4 B2: everything after the encode is RecordFloor, shared with the managed path's settle.
                RecordFloor(plan, floor, png == null ? 0 : png.Length, path => Stage(path, png), null, null);
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>WP4 B2: the second half of FinishFloor, moved verbatim so the managed path's settle runs the same checks
        /// and the same line on the same numbers: the empty test, the MaxFloorPngBytes cap, the stage, Bytes, the
        /// "captured ..." line, the audit line and the "nothing improved" line. Throws on anything unexpected; the
        /// caller's catch fails the floor.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor.</param>
        /// <param name="length">The encoded file's length (0 = encoded to nothing).</param>
        /// <param name="write">Stages the file at the path it is given.</param>
        /// <param name="encodeMs">The floor clock to report, or null for the clock now (the synchronous path).</param>
        /// <param name="encoded">Added inside the captured line's sentence (the managed path's timing), or null.</param>
        /// <param name="held">Campaign speed step 2: the floor was HELD, not encoded - <paramref name="length"/> is what it
        /// holds, nothing is staged, and the size cap (a file's) is the checkpoint's to judge.</param>
        private void RecordFloor(Plan plan, FloorPlan floor, long length, Action<string> write, double? encodeMs,
            string encoded, bool held = false)
        {
            // A held floor has no file yet: the checkpoint encodes it, and judges the cap on that file (EncodeHeld).
            if (!held && length == 0)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" encoded to nothing and was not written.");
                return;
            }

            // Stage M2c: the plan's cap - MaxFloorPngBytes on every raid plan, MenuMaxFloorPngBytes on a menu capture's
            if (!held && length > plan.FloorPngCapBytes)
            {
                // Not written rather than written and large: these files ship in the release zip
                // and are uploaded to Fika hosts, and a floor this size is a sign the picture is
                // noise rather than a map.
                floor.Failed = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" encoded to {length} bytes, over the " +
                    $"{plan.FloorPngCapBytes / (1024 * 1024)} MB a floor may take - it was not written. Set Settings > " +
                    "Map > Capture resolution to 2048 and capture again.");
                return;
            }

            // STAGED, not written: see Stage. It goes in place in WriteMeta, with every other
            // floor of this capture, once the last of them has passed the light test.
            write?.Invoke(Path.Combine(plan.Dir, floor.File));

            floor.Bytes = length;
            plan.Bytes += length;

            var ms = encodeMs ?? floor.Clock?.Elapsed.TotalMilliseconds ?? 0d;
            var pixels = plan.WidthPx * plan.HeightPx;

            var line =
                $"QuestTree: captured {plan.Key} \"{floor.Dto.Name}\" {plan.WidthPx}x{plan.HeightPx} px " +
                $"({MetresPerPixel(plan.Ppm)} m/px), {TilesPhrase(plan, floor)}, {Ms(ms)} ms, exposure " +
                $"{E(floor.Exposure.Low)}..{E(floor.Exposure.High)} " +
                $"({(_hdr ? "half-float" : "8-bit")}, gamma {G(floor.Exposure.Gamma)}" +
                (floor.ReusedExposure ? ", kept from the first capture" : "") + ")";

            if (floor.CyanFilled > 0 || floor.CyanDropped > 0)
            {
                line += $", {floor.CyanFilled} cyan water pixels filled";
                if (floor.CyanDropped > 0) line += $" and {floor.CyanDropped} left as holes";
            }

            if (floor.Despeckled > 0) line += $", {floor.Despeckled} speckles medianed";
            if (floor.Outside > 0) line += $", {Share(floor.Outside, pixels)} % outside the walkable area";

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

            // WP4 B2: the managed path says so, inside the sentence.
            if (encoded != null && line.EndsWith(".", StringComparison.Ordinal))
                line = line.Substring(0, line.Length - 1) + encoded + ".";

            Plugin.LogSource?.LogInfo(line);

            AuditLine(plan, floor);

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

        /// <summary>WP4 B2: FinishFloor's call site. The Texture path is FinishFloor, unchanged. The managed path starts
        /// the picture's encode - and the sidecar's, since Dist is final after DevelopFinish - on workers; SettleEncodes
        /// stages both at the next barrier, exactly as FinishFloor and WriteSidecar did.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, developed.</param>
        private void StartFloorEncode(Plan plan, FloorPlan floor)
        {
            if (floor.Rgba == null)
            {
                FinishFloor(plan, floor);
                return;
            }

            if (floor.Exposure == null)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" has no developed picture to write.");
                return;
            }

            floor.EncodeMs = floor.Clock?.Elapsed.TotalMilliseconds ?? 0d;
            floor.PictureEncode = StartPng(floor.Rgba, plan.WidthPx, plan.HeightPx, rgba: true);

            // Stage M3: a viewing copy, from the same pixels, on its own worker - settled with the picture. Gated on the
            // picture's size, not on menu mode (review): a raid capture merged into a menu set rewrites a 16384 px floor,
            // and without a new copy the stale one would be swept and the Maps tab left decoding the full picture. A raid
            // floor of a big map at 8 px/m (Customs 8944 px) gets one too - a worker's few seconds, off the main thread.
            if (MenuViewCopy && Math.Max(plan.WidthPx, plan.HeightPx) > ViewPictureSide) StartViewEncode(plan, floor);

            if (floor.Dist != null)
            {
                floor.SidecarSource = floor.Dist;
                floor.SidecarEncode = StartPng(floor.Dist, plan.WidthPx, plan.HeightPx, rgba: false);
            }
        }

        /// <summary>
        /// Campaign speed step 1 (1): whether a floor keeps its stored picture and sidecar as they are - and if so, marks it
        /// <see cref="FloorPlan.Unchanged"/>, gives it the stored picture's length as <see cref="FloorPlan.Bytes"/> (every
        /// "was a picture written" gate - the mesh, the sides, WriteMeta - asks <c>!Failed &amp;&amp; Bytes &gt; 0</c>, and the
        /// floor IS named by this meta) and says so in the floor's line.
        ///
        /// Only the exact case, where a rewrite would stage the same pixels: a tile plan was made and no tile rendered, the
        /// stored picture AND its sidecar were loaded before the tiles (DevelopBand's not-taken branch then copies the
        /// picture pixel for pixel and the sidecar byte for byte - the sidecar is the authority for "drawn" whenever there
        /// is one), the exposure is the stored one (MeasureFloor's no-tile path), and the previous meta names this very
        /// file, which is still on disk with its sidecar beside it. Anything else - no sidecar, audit mode, a renamed file -
        /// takes the full develop and rewrite as before.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, measured.</param>
        private static bool KeepUnchangedFloor(Plan plan, FloorPlan floor)
        {
            if (!SkipUnchangedFloors || TileSkipAudit || floor == null || floor.Failed) return false;

            try
            {
                if (floor.Verdicts == null || floor.Tiles != 0 || !floor.PreviousLoaded || floor.PreviousColour == null ||
                    floor.PreviousDist == null || !floor.ReusedExposure || floor.Exposure == null ||
                    string.IsNullOrEmpty(floor.File) || string.IsNullOrEmpty(floor.DistFile))
                    return false;

                // The entry WriteMeta would otherwise carry: the meta names this file, so Described (what a rewrite writes)
                // and the stored entry agree on it.
                var stored = Carried(plan, floor);
                if (stored == null || !string.Equals(stored.File, floor.File, StringComparison.OrdinalIgnoreCase)) return false;

                // Campaign speed step 2: in a held campaign "kept as it is" means kept in the hold - taken in clean when this
                // is the campaign's first stop and the copy came from disk, left as it was when it came from the hold.
                if (plan.Hold != null)
                {
                    // Stashed, not held yet (review): a later floor's light test may still refuse this stop.
                    if (floor.PreviousColour.Length != plan.WidthPx * plan.HeightPx ||
                        floor.PreviousDist.Length != floor.PreviousColour.Length)
                        return false;

                    floor.StashPixels = floor.PreviousColour;
                    floor.StashDist = floor.PreviousDist;
                    floor.StashDirty = false;

                    var bytes = (long)floor.PreviousColour.Length * HeldBytesPerPixel;

                    floor.Unchanged = true;
                    floor.Merged = true;
                    floor.Bytes = bytes;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" unchanged, not developed - {TilesPhrase(plan, floor)}; its " +
                        $"picture and distance sidecar are held as they are, exposure {E(floor.Exposure.Low)}..{E(floor.Exposure.High)} " +
                        $"(kept from the first capture), {Ms(floor.Clock?.Elapsed.TotalMilliseconds ?? 0d)} ms.");

                    return true;
                }

                var picture = new FileInfo(Path.Combine(plan.Dir, floor.File));
                if (!picture.Exists || picture.Length <= 0 || !File.Exists(Path.Combine(plan.Dir, floor.DistFile))) return false;

                floor.Unchanged = true;
                floor.Merged = true;
                floor.Bytes = picture.Length;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" unchanged, not rewritten - {TilesPhrase(plan, floor)}; its " +
                    $"picture ({picture.Length.ToString(CultureInfo.InvariantCulture)} bytes) and distance sidecar stay on disk as " +
                    $"they are, exposure {E(floor.Exposure.Low)}..{E(floor.Exposure.High)} (kept from the first capture), " +
                    $"{Ms(floor.Clock?.Elapsed.TotalMilliseconds ?? 0d)} ms.");

                return true;
            }
            catch (Exception ex)
            {
                floor.Unchanged = false;
                floor.Bytes = 0;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: whether {plan.Key} \"{floor.Dto?.Name}\" could keep its stored picture was not decided " +
                    $"({ex.GetType().Name}: {ex.Message}) - it is developed and rewritten.");
                return false;
            }
        }

        /// <summary>WP4 B2: one encode on a worker. A METHOD, so the worker's closure holds these parameters and nothing an
        /// iterator later nulls - the atlas bug MapMeshBuilder records. Never faults: PngEncoder.Encode returns its errors,
        /// and a Task.Run that throws comes back as a Result with the error.</summary>
        /// <param name="source">The picture (Color32[], texture order) or the distances (byte[], texture order).</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        /// <param name="rgba">A picture (colour type 6) rather than a sidecar (colour type 2).</param>
        private static System.Threading.Tasks.Task<PngEncoder.Result> StartPng(Array source, int w, int h, bool rgba)
        {
            var roundTrip = rgba ? !_rgbaRoundTripped : !_rgbRoundTripped;

            try
            {
                return System.Threading.Tasks.Task.Run(() => rgba
                    ? PngEncoder.Encode(w, h, 6, PngFilter, RgbaRows((Color32[])source, w, h), roundTrip)
                    : PngEncoder.Encode(w, h, 2, PngFilter, GreyRgbRows((byte[])source, w, h), roundTrip));
            }
            catch (Exception ex)
            {
                return System.Threading.Tasks.Task.FromResult(new PngEncoder.Result { Error = ex });
            }
        }

        /// <summary>WP4 B2: the settled result of an encode, or null with the reason when it cannot be used as it is (not
        /// finished in EncodeWaitSeconds, an error, or a round trip that failed - which turns the managed encoder off for
        /// the session).</summary>
        /// <param name="task">The encode.</param>
        /// <param name="waitSeconds">How long the settle waited for it, for the line (the plan's EncodeWaitCapSeconds
        /// for a floor and its sidecar, EncodeWaitSeconds for a side).</param>
        /// <param name="why">Why it cannot be used.</param>
        private static PngEncoder.Result Settled(System.Threading.Tasks.Task<PngEncoder.Result> task, double waitSeconds,
            out string why)
        {
            why = null;

            if (task == null)
            {
                why = "no encode was started";
                return null;
            }

            if (!task.IsCompleted)
            {
                why = $"the managed encode did not finish in {F0(waitSeconds)} s";
                return null;
            }

            if (task.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || task.Result == null)
            {
                why = $"the managed encode ended {task.Status}";
                return null;
            }

            var result = task.Result;
            if (result.Error == null) return result;

            if (result.RoundTripFailed)
            {
                _managedPngOff = true;
                why = $"its round trip failed - {result.Error.Message}; the managed encoder is off for this session";
            }
            else
            {
                why = $"the managed encode failed - {result.Error.GetType().Name}: {result.Error.Message}";
            }

            return null;
        }

        /// <summary>WP4 B2: Unity's encode of a developed picture, as the old build made it - a temporary RGBA32 texture of
        /// the same pixels, EncodeToPNG, Destroy. The fallback and the verification copy.</summary>
        /// <param name="pool">The picture, texture order, exactly width x height.</param>
        /// <param name="width">Its width.</param>
        /// <param name="height">Its height.</param>
        private static byte[] UnityPicture(Color32[] pool, int width, int height)
        {
            Texture2D texture = null;

            try
            {
                texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
                texture.SetPixels32(pool);
                return texture.EncodeToPNG();
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }

        /// <summary>WP4 B2, VerifyManagedPng only: Unity's encode of a file written to captures-verify/&lt;key&gt;/, beside
        /// (not in) the captures root, for tools/check-capture.py --compare-dir.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="name">The file's name.</param>
        /// <param name="png">Unity's bytes.</param>
        private static void VerifyCopy(Plan plan, string name, byte[] png)
        {
            if (!VerifyManagedPng || png == null || png.Length == 0 || string.IsNullOrEmpty(name)) return;

            try
            {
                var root = CapturesRootDir();
                if (root == null) return;

                var dir = Path.Combine(Path.GetDirectoryName(root) ?? root, "captures-verify", plan.Key);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(name)), png);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the verification copy of {name} could not be written ({ex.Message}).");
            }
        }

        /// <summary>WP4 B2: an encode dropped without being staged - a refused capture, or a picture that was not written.</summary>
        /// <param name="floor">The floor or side.</param>
        private static void DropEncode(FloorPlan floor)
        {
            floor.PictureEncode = null;
            floor.SidecarEncode = null;
            floor.ViewEncode = null;
            floor.SidecarSource = null;
            floor.Verdicts = null;
            floor.AuditVerdicts = null;
        }

        /// <summary>WP4 B2, THE SETTLE (barriers 1, 2 and 5): waits, a frame at a time, for every floor of this plan (but
        /// <paramref name="except"/>) whose encodes are still running, then stages them exactly as FinishFloor and
        /// WriteSidecar did - picture first, then, a frame later and only when the picture was staged, its sidecar.
        /// Nothing is staged once the plan is refused.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floors">The floors.</param>
        /// <param name="except">A floor not to wait for (the one being rendered), or null.</param>
        private IEnumerator SettleEncodes(Plan plan, IEnumerable<FloorPlan> floors, FloorPlan except)
        {
            foreach (var floor in floors)
            {
                if (floor == null || floor == except || floor.PictureEncode == null) continue;

                var clock = Stopwatch.StartNew();

                // Stage M3: and the viewing copy's (a menu floor's only) - it reads the same pool the next floor reuses.
                while ((!floor.PictureEncode.IsCompleted || (floor.SidecarEncode != null && !floor.SidecarEncode.IsCompleted) ||
                        (floor.ViewEncode != null && !floor.ViewEncode.IsCompleted)) &&
                       clock.Elapsed.TotalSeconds < plan.EncodeWaitCapSeconds)
                    yield return null;

                if (plan.Refused)
                {
                    DropEncode(floor);
                    continue;
                }

                // One file written a frame, as FinishFloor and WriteSidecar had.
                SettlePicture(plan, floor);

                yield return null;

                if (!floor.Failed && floor.Bytes > 0) SettleSidecar(plan, floor);

                // Stage M3: the viewing copy, a frame later, only beside a staged picture.
                if (floor.ViewEncode != null)
                {
                    yield return null;
                    if (!floor.Failed && floor.Bytes > 0) SettleView(plan, floor);
                }

                DropEncode(floor);
            }
        }

        /// <summary>WP4 B2: a floor's picture staged from its managed encode, or - on an error, a timeout, a failed round
        /// trip, or a managed file over MaxFloorPngBytes - from Unity's encode of the same pixels (the pool still holds
        /// them: the next floor's DevelopBegin comes after this barrier), with the cap judged on Unity's length, so the set
        /// of floors written is the old set. Then RecordFloor: FinishFloor's checks and line.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor.</param>
        private void SettlePicture(Plan plan, FloorPlan floor)
        {
            try
            {
                var result = Settled(floor.PictureEncode, plan.EncodeWaitCapSeconds, out var why);

                if (result != null && result.Length > plan.FloorPngCapBytes)
                    why = $"the managed file is {result.Length} bytes, over the {plan.FloorPngCapBytes / (1024 * 1024)} MB a " +
                          "floor may take - the cap is judged on Unity's encode";

                if (why == null)
                {
                    if (result.RoundTripChecked) _rgbaRoundTripped = true;

                    RecordFloor(plan, floor, result.Length, path => Stage(path, result.Parts, result.LastLength), floor.EncodeMs,
                        $", encoded off the main thread in {Ms(result.Milliseconds)} ms");

                    if (VerifyManagedPng && !floor.Failed)
                        VerifyCopy(plan, floor.File, UnityPicture(plan.RgbaPool, plan.WidthPx, plan.HeightPx));

                    return;
                }

                FallbackLine($"QuestTree: {plan.Key} \"{floor.Dto?.Name}\"", why);

                var png = UnityPicture(plan.RgbaPool, plan.WidthPx, plan.HeightPx);
                RecordFloor(plan, floor, png == null ? 0 : png.Length, path => Stage(path, png), floor.EncodeMs, null);

                if (!floor.Failed) VerifyCopy(plan, floor.File, png);
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Stage M3: starts a menu floor's viewing copy on a worker: <see cref="ViewRows"/> box-filters the developed
        /// pixels row by row straight into the encoder (no second full-size array), at <see cref="ViewSize"/>. No round
        /// trip - the encoder's is proven on the full picture, and it would run the filter twice. Nothing when the picture
        /// is already no larger than the copy.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, developed (Rgba is the plan's pool).</param>
        private static void StartViewEncode(Plan plan, FloorPlan floor)
        {
            floor.ViewFile = null;
            floor.ViewStaged = false;

            try
            {
                if (floor.Rgba == null || floor.Dto == null) return;

                var w = plan.WidthPx;
                var h = plan.HeightPx;

                if (!ViewSize(w, h, out var vw, out var vh))
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" needs no viewing copy - {w}x{h} px is within {ViewPictureSide}.");
                    return;
                }

                var source = floor.Rgba;
                floor.ViewFile = ViewFileName(plan.Key, floor.Dto.Level);
                floor.ViewWidth = vw;
                floor.ViewHeight = vh;
                floor.ViewEncode = System.Threading.Tasks.Task.Run(() =>
                    PngEncoder.Encode(vw, vh, 6, PngFilter, ViewRows(source, w, h, vw, vh), false));
            }
            catch (Exception ex)
            {
                floor.ViewFile = null;
                floor.ViewEncode = null;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" gets no viewing copy ({ex.GetType().Name}: {ex.Message}) - the " +
                    "Maps tab draws the full picture.");
            }
        }

        /// <summary>Stage M3: the viewing copy's rows, PNG row 0 first (the top, as <see cref="RgbaRows"/>): each output
        /// pixel the box average of the source pixels whose integer span maps onto it - per axis floor(i x w / vw) to
        /// floor((i + 1) x w / vw), so every source pixel lands in exactly one output pixel and the copy covers exactly the
        /// picture's extent. Colour is averaged weighted by alpha (a cut-out edge does not take the colour behind a
        /// transparent pixel); alpha is the plain average, so the reach mask's edge comes out as a soft ramp. A pixel with no
        /// alpha at all keeps the plain colour average. Deterministic, as PngEncoder asks; one worker, so the sums are
        /// shared across calls.</summary>
        /// <param name="px">The picture, texture order (row 0 at the bottom), w x h.</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        /// <param name="vw">The copy's width, less than or equal to w.</param>
        /// <param name="vh">The copy's height, less than or equal to h.</param>
        private static Action<int, byte[]> ViewRows(Color32[] px, int w, int h, int vw, int vh)
        {
            var xs = new int[vw + 1];
            for (var i = 0; i <= vw; i++) xs[i] = (int)((long)i * w / vw);

            var ys = new int[vh + 1];
            for (var i = 0; i <= vh; i++) ys[i] = (int)((long)i * h / vh);

            var r = new long[vw];
            var g = new long[vw];
            var b = new long[vw];
            var a = new long[vw];
            var pr = new long[vw];
            var pg = new long[vw];
            var pb = new long[vw];

            return (row, buf) =>
            {
                Array.Clear(r, 0, vw);
                Array.Clear(g, 0, vw);
                Array.Clear(b, 0, vw);
                Array.Clear(a, 0, vw);
                Array.Clear(pr, 0, vw);
                Array.Clear(pg, 0, vw);
                Array.Clear(pb, 0, vw);

                // PNG row 0 is the top: the copy's texture row vh - 1 - row, which covers source rows ys[t]..ys[t + 1].
                var t = vh - 1 - row;
                var y0 = ys[t];
                var y1 = Math.Max(y0 + 1, ys[t + 1]);

                for (var sy = y0; sy < y1 && sy < h; sy++)
                {
                    var from = sy * w;

                    for (var x = 0; x < vw; x++)
                    {
                        var x1 = Math.Max(xs[x] + 1, xs[x + 1]);

                        for (var sx = xs[x]; sx < x1 && sx < w; sx++)
                        {
                            var c = px[from + sx];
                            r[x] += c.r;
                            g[x] += c.g;
                            b[x] += c.b;
                            a[x] += c.a;
                            pr[x] += c.r * c.a;
                            pg[x] += c.g * c.a;
                            pb[x] += c.b * c.a;
                        }
                    }
                }

                var rows = Math.Max(1, Math.Min(y1, h) - y0);

                for (int x = 0, o = 0; x < vw; x++, o += 4)
                {
                    var n = (long)rows * Math.Max(1, Math.Min(Math.Max(xs[x] + 1, xs[x + 1]), w) - xs[x]);
                    var alpha = a[x];

                    if (alpha > 0)
                    {
                        buf[o] = (byte)((pr[x] + alpha / 2) / alpha);
                        buf[o + 1] = (byte)((pg[x] + alpha / 2) / alpha);
                        buf[o + 2] = (byte)((pb[x] + alpha / 2) / alpha);
                    }
                    else
                    {
                        buf[o] = (byte)((r[x] + n / 2) / n);
                        buf[o + 1] = (byte)((g[x] + n / 2) / n);
                        buf[o + 2] = (byte)((b[x] + n / 2) / n);
                    }

                    buf[o + 3] = (byte)((alpha + n / 2) / n);
                }
            };
        }

        /// <summary>Stage M3: stages a menu floor's viewing copy from its settled encode, or - on an error or a timeout -
        /// stages none, with one line: the copy is optional, and the meta then names only the full picture (an older
        /// copy on disk is swept as stale at the write). Called only beside a staged picture.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor.</param>
        private static void SettleView(Plan plan, FloorPlan floor)
        {
            try
            {
                if (floor.ViewFile == null) return;

                var result = Settled(floor.ViewEncode, plan.EncodeWaitCapSeconds, out var why);

                if (why != null)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" gets no viewing copy ({why}) - the Maps tab draws the full picture.");
                    floor.ViewFile = null;
                    return;
                }

                Stage(Path.Combine(plan.Dir, floor.ViewFile), result.Parts, result.LastLength);
                floor.ViewStaged = true;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" viewing copy {floor.ViewFile} " +
                    $"{floor.ViewWidth.ToString(CultureInfo.InvariantCulture)}x{floor.ViewHeight.ToString(CultureInfo.InvariantCulture)} px " +
                    $"(from {plan.WidthPx.ToString(CultureInfo.InvariantCulture)}x{plan.HeightPx.ToString(CultureInfo.InvariantCulture)}), " +
                    $"{result.Length.ToString(CultureInfo.InvariantCulture)} bytes, box-filtered and encoded off the main thread in " +
                    $"{Ms(result.Milliseconds)} ms.");
            }
            catch (Exception ex)
            {
                floor.ViewFile = null;
                floor.ViewStaged = false;
                Forget(plan, ViewFileName(plan.Key, floor.Dto?.Level ?? 0));
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\"'s viewing copy could not be staged ({ex.GetType().Name}: " +
                    $"{ex.Message}) - the Maps tab draws the full picture.");
            }
        }

        /// <summary>WP4 B2: the line a fallback to Unity's encoder writes - a Warning when the round trip failed (the
        /// managed encoder is then off for the session), Info otherwise.</summary>
        /// <param name="who">"QuestTree: key "floor"" or the side's or sidecar's equivalent.</param>
        /// <param name="why">The reason.</param>
        private static void FallbackLine(string who, string why)
        {
            var line = $"{who} falls back to Unity's encoder ({why}).";

            if (_managedPngOff && why.Contains("round trip")) Plugin.LogSource?.LogWarning(line);
            else Plugin.LogSource?.LogInfo(line);
        }

        /// <summary>WP4 B2: a picture's sidecar staged from its managed encode, or from Unity's (EncodeSidecar on the kept
        /// SidecarSource) on an error or a timeout - WriteSidecar's rules and lines: no distances, or nothing encoded, or a
        /// write that throws, sets DistStale (the old sidecar is then deleted at commit, review F09). Called only when the
        /// picture was staged.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private static void SettleSidecar(Plan plan, FloorPlan floor)
        {
            var path = Path.Combine(plan.Dir, floor.DistFile);
            var source = floor.SidecarSource;
            var task = floor.SidecarEncode;

            try
            {
                if (source == null)
                {
                    floor.DistStale = true;
                    return;
                }

                var result = Settled(task, plan.EncodeWaitCapSeconds, out var why);

                if (why == null)
                {
                    if (result.RoundTripChecked) _rgbRoundTripped = true;

                    Stage(path, result.Parts, result.LastLength);

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {floor.DistFile} written, {result.Length} bytes.");

                    if (VerifyManagedPng) VerifyCopy(plan, floor.DistFile, EncodeSidecar(plan, source));
                    return;
                }

                FallbackLine($"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto?.Name}\"", why);

                var png = EncodeSidecar(plan, source);

                if (png == null || png.Length == 0)
                {
                    floor.DistStale = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto.Name}\" could not be encoded - the " +
                        "old one is removed with the picture's commit, so the next capture merges as if fresh.");
                    return;
                }

                Stage(path, png);

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {floor.DistFile} written, {png.Length} bytes.");

                VerifyCopy(plan, floor.DistFile, png);
            }
            catch (Exception ex)
            {
                floor.DistStale = true;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}) - the old one is removed with the picture's commit.");
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
                if (floor.Dist == null)
                {
                    floor.DistStale = true;
                    return;
                }

                var png = EncodeSidecar(plan, floor);

                if (png == null || png.Length == 0)
                {
                    // DELETED at commit (review F09), not left: after a fresh capture that kept the extent (a
                    // render-tag or gamma change) the old sidecar describes a different picture, and its
                    // distances would keep this picture's empty pixels empty. No sidecar is the safe state -
                    // the next merge lets this capture win everywhere.
                    floor.DistStale = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto.Name}\" could not be encoded - the " +
                        "old one is removed with the picture's commit, so the next capture merges as if fresh.");
                    return;
                }

                Stage(path, png);

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {floor.DistFile} written, {png.Length} bytes.");
            }
            catch (Exception ex)
            {
                floor.DistStale = true;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the distance sidecar for {plan.Key} \"{floor.Dto?.Name}\" could not be written " +
                    $"({ex.GetType().Name}: {ex.Message}) - the old one is removed with the picture's commit.");
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
        private static byte[] EncodeSidecar(Plan plan, FloorPlan floor) => EncodeSidecar(plan, floor.Dist);

        /// <summary>WP4 B2: <see cref="EncodeSidecar(Plan, FloorPlan)"/> from a given distance array - the managed path's
        /// fallback, whose floor's Dist ReleaseTexture has already dropped (it keeps SidecarSource).</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="dist">The distances, texture order.</param>
        private static byte[] EncodeSidecar(Plan plan, byte[] dist)
        {
            Texture2D grey = null;

            try
            {
                grey = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGB24, mipChain: false);

                var pixels = new Color32[plan.WidthPx * plan.HeightPx];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var step = dist[i];
                    pixels[i] = new Color32(step, step, step, 255);
                }

                // No Apply: EncodeToPNG reads the CPU copy, and uploading ~29 MB to a GPU nothing reads it from was
                // pure cost (review F10).
                grey.SetPixels32(pixels);

                return grey.EncodeToPNG();
            }
            finally
            {
                if (grey != null) Destroy(grey);
            }
        }

        /// <summary>WP4 B1: the row filler for a floor or side picture handed to <see cref="PngEncoder"/>. Texture row 0
        /// is the BOTTOM (SetPixels32's order, which DevelopBand fills the pool in), PNG row 0 the TOP - as EncodeToPNG
        /// writes it - so PNG row r is texture row h-1-r, four bytes a pixel in R, G, B, A order.</summary>
        /// <param name="px">The developed picture, texture order.</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        private static Action<int, byte[]> RgbaRows(Color32[] px, int w, int h) => (row, buf) =>
        {
            var from = (h - 1 - row) * w;

            for (int x = 0, o = 0; x < w; x++, o += 4)
            {
                var c = px[from + x];
                buf[o] = c.r;
                buf[o + 1] = c.g;
                buf[o + 2] = c.b;
                buf[o + 3] = c.a;
            }
        };

        /// <summary>WP4 B1: the row filler for a distance sidecar - RGB of (d, d, d), exactly what EncodeSidecar's
        /// SetPixels32(Color32(step, step, step, 255)) into an RGB24 texture stores, top row first. Colour type 2, never
        /// grey: LoadImage of a grey PNG comes back in a format CopyRed does not take (see EncodeSidecar).</summary>
        /// <param name="dist">The floor's distances, texture order.</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        private static Action<int, byte[]> GreyRgbRows(byte[] dist, int w, int h) => (row, buf) =>
        {
            var from = (h - 1 - row) * w;

            for (int x = 0, o = 0; x < w; x++, o += 3)
            {
                var d = dist[from + x];
                buf[o] = d;
                buf[o + 1] = d;
                buf[o + 2] = d;
            }
        };

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

        /// <summary>Stages the first <paramref name="length"/> bytes of a buffer - a stream's own buffer, written
        /// without copying it to an array of its exact size first. Same bytes on disk as <see cref="Stage(string, byte[])"/>.</summary>
        /// <param name="path">The file these bytes are for.</param>
        /// <param name="bytes">A buffer whose first <paramref name="length"/> bytes are the contents.</param>
        /// <param name="length">How many bytes of it are the file.</param>
        private static void Stage(string path, byte[] bytes, int length)
        {
            using (var stream = new FileStream(Staged(path), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                stream.Write(bytes, 0, length);
        }

        /// <summary>WP4 B2: stages a managed encode's parts, in order, with one FileStream - on the main thread, as every
        /// capture file is written (a worker never writes one, so DropStaged can never meet an open file).</summary>
        /// <param name="path">The file these bytes are for.</param>
        /// <param name="parts">The file in parts, each full but the last.</param>
        /// <param name="lastLength">How many bytes of the last part are the file.</param>
        private static void Stage(string path, List<byte[]> parts, int lastLength)
        {
            using (var stream = new FileStream(Staged(path), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                for (var i = 0; i < parts.Count; i++)
                    stream.Write(parts[i], 0, i == parts.Count - 1 ? lastLength : parts[i].Length);
            }
        }

        /// <summary>Puts a staged file in place, and does nothing when none was staged - a floor
        /// whose sidecar could not be encoded, for instance.
        ///
        /// The ONE window this leaves, stated plainly because no sequence of single-file moves closes it:
        /// <see cref="WriteMeta"/> commits the floors one at a time and writes the meta last, so a process
        /// killed between two floors leaves new pixels under a floor's old file name with the OLD meta
        /// still describing them. A floor keeps its name across captures, so nothing is missing and
        /// nothing is half-written; the only thing that can be wrong is the SIZE the old meta claims. On a
        /// merge it cannot be: LoadPrevious accepted the merge because the extent and the scale match to
        /// the double, so the new pixels are the size the old meta says. On a fresh capture after the
        /// harvest re-measured the extent, the old meta's width and height are the previous extent's, and
        /// the reader stretches the picture onto the extent it is given - MapCatalog.CheckPictureSize
        /// compares the meta's own numbers against the meta's own extent, not against the PNG, so it does
        /// not catch this. Accepted rather than fixed: the crash has to land inside the few milliseconds
        /// between two File.Move calls AND the map's extent has to have changed since the last capture,
        /// and the next capture of that map corrects it. Closing it properly means a fresh name per
        /// capture, which is the stale-picture sweep, the upload and the host cache as well.</summary>
        /// <param name="path">The file to end up with.</param>
        private static void Commit(string path)
        {
            var temp = Staged(path);
            if (!File.Exists(temp)) return;

            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            // WP2 (fixes 3): no delete-then-move window - the old file steps aside to .old first, is deleted only once the
            // new one is in place, and comes back when the move fails (the mesh, its index and every picture alike)
            var old = path + OldSuffix;
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);

            try
            {
                File.Move(temp, path);
            }
            catch
            {
                try
                {
                    if (!File.Exists(path)) File.Move(old, path);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: {Path.GetFileName(path)} could not be put back from {OldSuffix}: {ex.Message}");
                }

                throw;
            }

            DeleteQuietly(old);
        }

        /// <summary>WP2 (fixes 3): where a committed file's previous version waits while its replacement moves in.</summary>
        private const string OldSuffix = ".old";

        /// <summary>
        /// WP2 (fixes 4): a commit interrupted between its two moves leaves the file under <see cref="OldSuffix"/> and nothing
        /// under its own name. Before anything decides on the earlier capture's files, each one the meta names with a hash -
        /// the mesh and every atlas page by their sha256, the index by the mesh sha it is bound to - that is missing while
        /// its .old is there and matches is moved back. Anything else is left to <see cref="DropStalePictures"/>. Only a
        /// missing file is ever looked at, so a normal load pays a File.Exists per file.
        /// </summary>
        private static void RestoreOld(Plan plan, CaptureMeta meta)
        {
            if (meta == null || plan?.Dir == null) return;

            var mesh = meta.Mesh;
            if (mesh != null && IsPlainFileName(mesh.File) && !string.IsNullOrEmpty(mesh.Sha256))
            {
                RestoreOld(Path.Combine(plan.Dir, mesh.File), bytes => string.Equals(Sha256(bytes), mesh.Sha256, StringComparison.OrdinalIgnoreCase));
                RestoreOld(Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key)),
                    bytes => MapMeshIndex.SameBytes(MapMeshIndex.Read(bytes).MeshSha, MapMeshIndex.ShaBytes(mesh.Sha256)));
            }

            if (meta.Atlas == null) return;

            foreach (var page in meta.Atlas)
            {
                if (page == null || !IsPlainFileName(page.File) || string.IsNullOrEmpty(page.Sha256)) continue;
                RestoreOld(Path.Combine(plan.Dir, page.File), bytes => string.Equals(Sha256(bytes), page.Sha256, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>WP2 (fixes 4): one file moved back from its .old when it is missing and the .old passes the check.</summary>
        private static void RestoreOld(string path, Func<byte[], bool> matches)
        {
            try
            {
                if (File.Exists(path)) return;

                var old = path + OldSuffix;
                if (!File.Exists(old)) return;

                bool ok;
                try
                {
                    ok = matches(File.ReadAllBytes(old));
                }
                catch
                {
                    ok = false;
                }

                if (!ok)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: {Path.GetFileName(old)} is not the file the meta names - left for the sweep.");
                    return;
                }

                File.Move(old, path);
                Plugin.LogSource?.LogInfo($"QuestTree: {Path.GetFileName(path)} was put back from {OldSuffix} (a commit was interrupted).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: {Path.GetFileName(path)} could not be put back from {OldSuffix} ({ex.Message}).");
            }
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

                // Stage M3: the viewing copy, by its canonical name whether or not this capture started one
                if (floor.Dto != null) Forget(plan, ViewFileName(plan.Key, floor.Dto.Level));
            }

            // The 3D mesh is staged the same way and has to be dropped the same way: a refused capture
            // must not leave a <key>-mesh.bin.tmp behind for the next one to trip over.
            Forget(plan, plan.MeshFile);

            // WP2: and its identity sidecar.
            Forget(plan, MapMeshIndex.FileNameFor(plan.Key));

            // And its atlas pages, every possible one by name.
            for (var page = 0; page < MapMeshFile.MaxAtlasPages; page++)
            {
                Forget(plan, MapMeshFile.AtlasFileNameFor(plan.Key, page));

                try
                {
                    var part = AtlasPartPath(plan, page);
                    if (File.Exists(part)) File.Delete(part);

                    // WP2: and MeshVerifyLastStop's, which never reach a staged name
                    var verify = Path.Combine(plan.Dir, VerifyAtlasName(plan.Key, page)) + ".part";
                    if (File.Exists(verify)) File.Delete(verify);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: an atlas .part could not be removed ({ex.Message}).");
                }
            }

            // And the side views, all four by name whether or not this capture got to them - forgetting
            // a .tmp that is not there costs one File.Exists.
            foreach (var dir in MapSideView.Directions)
            {
                Forget(plan, SideFileName(plan.Key, dir));
                Forget(plan, SideDistFileName(plan.Key, dir));
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

        /// <summary>Deletes a file if it is there, quietly.</summary>
        /// <param name="path">The file.</param>
        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: {Path.GetFileName(path)} could not be removed ({ex.Message}).");
            }
        }

        /// <summary>Deletes a file if it is there, and says whether it is gone - DeleteQuietly for a caller
        /// that warns when a stale file stays (review F09).</summary>
        /// <param name="path">The file.</param>
        private static bool DeleteOrWarn(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: {Path.GetFileName(path)} could not be removed ({ex.Message}).");
                return false;
            }
        }

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
        /// <param name="renderTag">This capture's render recipe, which the stored one has to equal. Passed
        /// in rather than read from the property, because it depends on the multisampling this capture's
        /// device actually granted and this method is static.</param>
        /// <param name="held">Campaign speed step 2: the held set's meta, checked in place of the file; null reads the file.</param>
        private static CaptureMeta LoadPrevious(Plan plan, bool needsGamma, string renderTag, CaptureMeta held = null)
        {
            var path = Path.Combine(plan.Dir, $"{plan.Key}.map.json");

            // Stage M2b (review): set only on the success return below, so a set refused by a later check (or the retry
            // without the held set) never leaves the flag standing over a null Previous
            plan.IntoMenuSet = false;
            var intoMenuSet = false;

            plan.MenuNeedsReplace = null;

            // Stage M3: menu captures decide here what a stored set means for them - never a raid capture.
            var menuWrite = plan.MenuMode && MenuReplaceMode ? plan.MenuWrite : MenuWriteMode.MergeOrFresh;

            try
            {
                if (held == null && !File.Exists(path)) return null;

                // Stage M3: Replace never reads the stored set - this capture starts fresh, and WriteMeta moves the stored
                // set aside before it puts anything in place (SetStoredAside).
                if (menuWrite == MenuWriteMode.Replace)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} - Replace: the stored set is not merged; this capture starts fresh, and the " +
                        $"stored set is moved to {plan.Key}{SetAsideInfix}<time> just before the new one is written.");
                    return null;
                }

                var meta = held ?? JsonConvert.DeserializeObject<CaptureMeta>(File.ReadAllText(path));
                if (meta == null || meta.Extent == null || meta.Floors == null || meta.Floors.Count == 0)
                {
                    // Stage M3: a menu merge never replaces what it cannot read - Replace does, keeping a copy.
                    if (menuWrite == MenuWriteMode.Merge)
                    {
                        NeedsReplace(plan, "the stored set cannot be read");
                        return null;
                    }

                    Fresh(plan, "the capture already there cannot be read");
                    return null;
                }

                // Stage M2c: a raid capture never replaces a menu set - every refusal below refuses THIS capture instead
                // (RaidLeavesMenuSets), and Prepare stops on plan.MenuSetGuarded with the set on disk untouched. Every
                // refusal from here on goes through Refuse, so none can slip past it.
                var guardMenu = !plan.MenuMode && RaidLeavesMenuSets && IsMenuSet(meta);

                void Refuse(Plan p, string why)
                {
                    // Stage M3: a menu capture without Replace leaves a set it cannot merge into exactly as it is.
                    if (menuWrite == MenuWriteMode.Merge)
                    {
                        NeedsReplace(p, why);
                        return;
                    }

                    if (!guardMenu)
                    {
                        Fresh(p, why);
                        return;
                    }

                    p.MenuSetGuarded = true;
                    _guardedMenuSets.Add(p.Key);
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {p.Key} - {GuardedMenuSetReason} ({why}).");
                }

                if (meta.SchemaVersion != SchemaVersion)
                {
                    Refuse(plan, $"the capture already there is schema {meta.SchemaVersion} and this build writes {SchemaVersion}");
                    return null;
                }

                if (meta.Extent.MinX != plan.Extent.MinX || meta.Extent.MinZ != plan.Extent.MinZ ||
                    meta.Extent.MaxX != plan.Extent.MaxX || meta.Extent.MaxZ != plan.Extent.MaxZ)
                {
                    Refuse(plan, "the map has been measured differently since (its extent moved)");
                    return null;
                }

                if (meta.PxPerMetre != plan.Ppm)
                {
                    Refuse(plan, $"it was captured at {Ppm(meta.PxPerMetre)} px/m and this one is {Ppm(plan.Ppm)}");
                    return null;
                }

                var mine = plan.Floors.Select(f => f.Dto.Level).OrderBy(l => l).ToArray();
                var theirs = meta.Floors.Select(f => f.Level).OrderBy(l => l).ToArray();

                if (!mine.SequenceEqual(theirs))
                {
                    Refuse(plan, $"its floors were {Levels(theirs)} and this capture's are {Levels(mine)}");
                    return null;
                }

                // Stage M2b (review): a raid capture of a map whose set a menu capture wrote MERGES into it. The menu set's
                // tag is the raid recipe plus the rig term; its pixels are all step 0, so they win every distance test and
                // the raid adds only what the menu never drew, developed with the exposure the menu set stored (Stored).
                // The set keeps the menu's recipe, light and time (WriteMeta), so it stays a menu set for the next merge.
                // One way only: a menu capture over a raid set still replaces it once (its own tag carries the rig term).
                if (!plan.MenuMode && MenuRigMerge && string.Equals(meta.Render, renderTag + ";" + MenuRigTag, StringComparison.Ordinal))
                {
                    intoMenuSet = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} - merging into a menu-captured set (rendered {meta.Render}): its step-0 pixels are " +
                        "kept everywhere and this capture adds only what it never drew, under its stored exposure.");
                }
                else if (!string.Equals(meta.Render, renderTag, StringComparison.Ordinal))
                {
                    // The render recipe is part of what a pixel IS. A capture taken before the recipe was
                    // recorded is one of the black rain-era or building-less pictures this replaces; one
                    // taken under a different light would merge into a visible seam, and one taken without
                    // the LOD bias would win the distance test over ground that actually has buildings in
                    // it. Any difference at all, and this capture starts fresh.
                    Refuse(plan, string.IsNullOrEmpty(meta.Render)
                        ? $"it was taken before the render recipe was recorded, and this one is rendered {renderTag}"
                        : $"it was rendered {meta.Render} and this one is rendered {renderTag}");
                    return null;
                }

                var gamma = needsGamma ? 1f / 2.2f : 1f;

                foreach (var floor in meta.Floors)
                {
                    if (floor.Exposure == null)
                    {
                        Refuse(plan, "it does not record the exposure it was developed with");
                        return null;
                    }

                    if (floor.Exposure.High - floor.Exposure.Low < MinExposureRange)
                    {
                        Refuse(plan, "the exposure it records is degenerate");
                        return null;
                    }

                    if (Math.Abs(floor.Exposure.Gamma - gamma) > 1e-4f)
                    {
                        Refuse(plan, $"it was developed with gamma {G(floor.Exposure.Gamma)} and this machine " +
                                    $"renders in {(needsGamma ? "half-float linear" : "eight bits")}, which needs {G(gamma)}");
                        return null;
                    }
                }

                // WP2 (fixes 4): a file an interrupted commit left under .old is put back before anything looks for it - on
                // disk, so not for a held meta (the campaign's first stop did it)
                if (held == null) RestoreOld(plan, meta);

                plan.IntoMenuSet = intoMenuSet;
                return meta;
            }
            catch (Exception ex)
            {
                // Stage M3: as above - a menu merge never starts fresh over a set it could not read.
                if (menuWrite == MenuWriteMode.Merge && held == null && File.Exists(path))
                {
                    NeedsReplace(plan, $"the stored set could not be read ({ex.GetType().Name})");
                    return null;
                }

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the capture already in {plan.Dir} could not be read ({ex.GetType().Name}: {ex.Message}) - " +
                    "this capture starts fresh.");
                return null;
            }
        }

        /// <summary>Stage M3: a menu capture without Replace found a stored set it may not merge into - Prepare stops on
        /// <see cref="Plan.MenuNeedsReplace"/> before anything is rendered, and the set stays as it is.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="why">What differs, as a phrase.</param>
        private static void NeedsReplace(Plan plan, string why)
        {
            plan.MenuNeedsReplace = why;
            Plugin.LogSource?.LogWarning(
                $"QuestTree: nothing was captured on {plan.Key} - the stored set cannot take this menu capture ({why}). It is " +
                "left as it is; choose \"Replace with a fresh capture from game files\" to replace it (a copy is kept).");
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

        /// <summary>Stage M2c: whether a stored set is a menu capture's - its explicit <see cref="CaptureMeta.CapturedIn"/>
        /// marker, or, for a set written before the marker existed, the menu rig's term in its render recipe.</summary>
        /// <param name="meta">The stored meta.</param>
        private static bool IsMenuSet(CaptureMeta meta) =>
            meta != null &&
            (string.Equals(meta.CapturedIn, MenuSetMarker, StringComparison.Ordinal) ||
             (meta.Render ?? "").Split(';').Any(term => term.StartsWith(MenuRigTermStem, StringComparison.Ordinal)));

        /// <summary>Stage M2c: what every version of <see cref="MenuRigTag"/> starts with.</summary>
        private const string MenuRigTermStem = "menu-rig-";

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

            // Campaign speed step 2: the held copy is exactly what the PNG would decode to (the encode is lossless), so
            // there is nothing to read, decode or copy - the merge reads it in place and never writes it.
            var held = HeldPictureOf(plan, floor.File);
            if (held != null)
            {
                floor.PreviousColour = held.Pixels;
                return;
            }

            Texture2D texture = null;

            try
            {
                texture = LoadPicture(Path.Combine(plan.Dir, floor.File), plan, floor, "picture");
                if (texture == null) return;

                var pixels = new Color32[plan.WidthPx * plan.HeightPx];

                // Out of the texture's own memory into the one array that has to outlive it - no
                // GetPixels32, which would allocate a SECOND array of the same size first. See
                // ReadSampleRow for the same reasoning on the staging texture.
                if (!CopyColours(texture, pixels, plan, floor, "picture")) return;

                floor.PreviousColour = pixels;
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }

        /// <summary>The distance sidecar beside the picture <see cref="LoadPreviousColour"/> just
        /// read, as one byte a pixel. Absent or unreadable leaves it null, which makes this capture's
        /// own pixels the better ones everywhere.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose previous sidecar is wanted.</param>
        private static void LoadPreviousDist(Plan plan, FloorPlan floor)
        {
            // Campaign speed step 2: the held sidecar. A floor's is read in place (the merge writes this stop's own array);
            // a side's is COPIED, because HealSideDist rewrites the previous sidecar it is given and the held one must stay
            // what the held picture was merged with until this stop is held in its place.
            var held = HeldPictureOf(plan, floor.File);
            if (held != null && ReferenceEquals(held.Pixels, floor.PreviousColour))
            {
                floor.PreviousDist = plan.Side != null ? (byte[])held.Dist.Clone() : held.Dist;
                return;
            }

            Texture2D texture = null;

            try
            {
                texture = LoadPicture(Path.Combine(plan.Dir, floor.DistFile), plan, floor, "distance sidecar");

                if (texture == null)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a picture but no distance sidecar, so this " +
                        "capture's own pixels are taken as the better ones everywhere. The sidecar it writes now " +
                        "makes every later capture choose per pixel.");
                    return;
                }

                // Straight into ONE byte a pixel. The old path decoded the whole sidecar into a
                // Color32 array first - four bytes a pixel, 31 MB on a floor of a 965x925 m rectangle at
                // the 3 px/m its memory budget settled that map on at 4 px/m (144 MiB at today's 6.5) -
                // to read one channel out of it,
                // which is most of what made the merge unaffordable.
                var red = new byte[plan.WidthPx * plan.HeightPx];
                if (!CopyRed(texture, red, plan, floor)) return;

                floor.PreviousDist = red;

                // Campaign speed step 2 (review): a held side keeps the sidecar as stored - HealSideDist is about to
                // rewrite this one, and a side held unchanged must not carry the healed copy forward
                if (plan.Hold != null && plan.Side != null) floor.UnhealedDist = (byte[])red.Clone();
            }
            finally
            {
                if (texture != null) Destroy(texture);
            }
        }

        /// <summary>One PNG beside the meta, decoded into a texture of this floor's size, or null when
        /// it is absent, unreadable or the wrong size. The CALLER destroys it.</summary>
        /// <param name="path">The file.</param>
        /// <param name="plan">The capture's plan, for the size the picture has to be.</param>
        /// <param name="floor">The floor, for the log lines.</param>
        /// <param name="what">What this file is, for the log lines.</param>
        private static Texture2D LoadPicture(string path, Plan plan, FloorPlan floor, string what)
        {
            Texture2D texture = null;

            try
            {
                if (!File.Exists(path)) return null;

                var bytes = File.ReadAllBytes(path);

                // The header before the decode (review F49): this file has to be exactly this capture's size, so any
                // other size - or no readable header - is refused before a texture is made, rather than after a
                // decode of whatever size the file claims.
                if (!QuestTree.UI.DynamicMapsLibrary.PictureSize(bytes, out var declaredWidth, out var declaredHeight))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} that is not a readable image - " +
                        "this capture draws over it.");
                    return null;
                }

                if (declaredWidth != plan.WidthPx || declaredHeight != plan.HeightPx)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} of {declaredWidth}x{declaredHeight} px " +
                        $"where this capture is {plan.WidthPx}x{plan.HeightPx} - this capture draws over it.");
                    return null;
                }

                // RGBA32 asked for; LoadImage reformats to suit the PNG anyway, which is why the two
                // copies below both check what they actually got.
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);

                if (!texture.LoadImage(bytes))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} that is not a readable image - " +
                        "this capture draws over it.");

                    Destroy(texture);
                    return null;
                }

                // Kept as a belt behind the header check above: the decode's own size.
                if (texture.width != plan.WidthPx || texture.height != plan.HeightPx)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} of {texture.width}x{texture.height} px " +
                        $"where this capture is {plan.WidthPx}x{plan.HeightPx} - this capture draws over it.");

                    Destroy(texture);
                    return null;
                }

                return texture;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" - its {what} could not be read " +
                    $"({ex.GetType().Name}: {ex.Message}).");

                if (texture != null) Destroy(texture);
                return null;
            }
        }

        /// <summary>Copies a decoded picture into a Color32 array through the texture's own memory,
        /// handling the formats a PNG decode actually produces here: ARGB32 for a picture with an alpha
        /// channel - which is what LoadImage hands back for every capture written since the walkable
        /// mask went into the alpha, and what this method REFUSED until 2026-09-23 - RGBA32 in case a
        /// decode ever comes back that way, and RGB24 for a picture without alpha, which every capture
        /// before the mask wrote. Anything else is refused rather than misread.
        ///
        /// The refusal was the whole merge for two weeks of captures. A refused previous picture leaves
        /// PreviousColour null, DevelopBand then sees no old pixel anywhere and takes every pixel of
        /// the new capture, and the distance sidecar written afterwards records that one capture's
        /// distances over the whole map - which is exactly what the sidecar of a two-campaign Customs
        /// set showed: every pixel measured from one south-east stop, 90 m there, 980 m in the far
        /// north-west, not a single empty pixel. A campaign looked merged only because the streamer
        /// keeps most of Customs loaded from any one stop; a capture taken inside Big Red replaced the
        /// far side with the ground its unloaded chunks drew.
        ///
        /// The ARGB32 byte order is not taken on trust: <see cref="ChannelOrder"/> reads sixteen sample
        /// pixels both ways and keeps the order that agrees with GetPixel, which decodes correctly
        /// whatever the format is, and refuses the picture when neither does.</summary>
        /// <param name="texture">The decoded picture.</param>
        /// <param name="into">The array to fill, one entry a pixel.</param>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, for the log line.</param>
        /// <param name="what">What this file is, for the log line.</param>
        private static bool CopyColours(Texture2D texture, Color32[] into, Plan plan, FloorPlan floor, string what)
        {
            if (texture.format == TextureFormat.RGBA32)
            {
                var data = texture.GetPixelData<Color32>(0);
                for (var i = 0; i < into.Length; i++) into[i] = data[i];
                return true;
            }

            if (texture.format == TextureFormat.ARGB32)
            {
                var order = ChannelOrder(texture, plan, floor, what);
                if (order == 0) return false;

                var data = texture.GetPixelData<Color32>(0);

                if (order == 1)
                {
                    // Bytes A,R,G,B: the Color32 fields read r=A, g=R, b=G, a=B.
                    for (var i = 0; i < into.Length; i++)
                    {
                        var raw = data[i];
                        into[i] = new Color32(raw.g, raw.b, raw.a, raw.r);
                    }
                }
                else
                {
                    // Bytes B,G,R,A: the Color32 fields read r=B, g=G, b=R, a=A.
                    for (var i = 0; i < into.Length; i++)
                    {
                        var raw = data[i];
                        into[i] = new Color32(raw.b, raw.g, raw.r, raw.a);
                    }
                }

                return true;
            }

            if (texture.format == TextureFormat.RGB24)
            {
                var data = texture.GetPixelData<Rgb24>(0);

                for (var i = 0; i < into.Length; i++)
                {
                    var pixel = data[i];
                    into[i] = new Color32(pixel.R, pixel.G, pixel.B, 255);
                }

                return true;
            }

            Plugin.LogSource?.LogInfo(
                $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} decoded as {texture.format}, which this " +
                "build does not read - this capture draws over it.");

            return false;
        }

        /// <summary>The red channel of a decoded picture, one byte a pixel: the distance sidecar's
        /// value, whichever of the two formats it came back as. See <see cref="CopyColours"/>.</summary>
        /// <param name="texture">The decoded sidecar.</param>
        /// <param name="into">The array to fill, one byte a pixel.</param>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor, for the log line.</param>
        private static bool CopyRed(Texture2D texture, byte[] into, Plan plan, FloorPlan floor)
        {
            if (texture.format == TextureFormat.RGBA32)
            {
                var data = texture.GetPixelData<Color32>(0);
                for (var i = 0; i < into.Length; i++) into[i] = data[i].r;
                return true;
            }

            if (texture.format == TextureFormat.ARGB32)
            {
                var order = ChannelOrder(texture, plan, floor, "distance sidecar");
                if (order == 0) return false;

                var data = texture.GetPixelData<Color32>(0);
                if (order == 1) for (var i = 0; i < into.Length; i++) into[i] = data[i].g;
                else for (var i = 0; i < into.Length; i++) into[i] = data[i].b;
                return true;
            }

            if (texture.format == TextureFormat.RGB24)
            {
                var data = texture.GetPixelData<Rgb24>(0);
                for (var i = 0; i < into.Length; i++) into[i] = data[i].R;
                return true;
            }

            Plugin.LogSource?.LogInfo(
                $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a distance sidecar decoded as {texture.format}, " +
                "which this build does not read - this capture's own pixels win everywhere.");

            return false;
        }

        /// <summary>Which byte order an ARGB32 texture's memory actually holds: 1 for A,R,G,B, 2 for
        /// B,G,R,A, 0 for neither (the picture is then refused, exactly as an unknown format is).
        /// Decided by evidence rather than by the format's name: sixteen pixels spread over the picture
        /// are read raw and both ways, and compared with GetPixel, which decodes correctly for every
        /// format and is only too slow to use for the whole picture. An order counts only when every
        /// sample agrees within one count per channel. Logged once per picture, at Debug.</summary>
        /// <param name="texture">The decoded ARGB32 texture.</param>
        /// <param name="plan">The capture's plan, for the log line.</param>
        /// <param name="floor">The floor, for the log line.</param>
        /// <param name="what">What this file is, for the log line.</param>
        private static int ChannelOrder(Texture2D texture, Plan plan, FloorPlan floor, string what)
        {
            var data = texture.GetPixelData<Color32>(0);
            var width = texture.width;
            var height = texture.height;
            var argb = true;
            var bgra = true;

            // The two readings of one pixel differ only when its bytes do: (m0,m1,m2,m3) reads the same
            // either way exactly when m1 == m2 and m0 == m3, which an empty (0,0,0,0) pixel satisfies -
            // and an outer floor of Interchange is 85 % empty pixels, so sixteen fixed samples can all
            // land on pixels that cannot tell the orders apart and "agree" with both. The first pixel
            // that CAN tell them apart is found by one linear scan (a dense picture ends it within a few
            // pixels) and is the one whose verdict counts; the sixteen fixed samples stay as a cross-check
            // that can only take an order away. When no pixel in the whole picture discriminates, the two
            // loops produce byte-identical output and the choice cannot matter, which is said in the log.
            var decisive = -1;

            for (var i = 0; i < data.Length; i++)
            {
                var raw = data[i];
                if (Math.Abs(raw.g - raw.b) > 1 || Math.Abs(raw.r - raw.a) > 1)
                {
                    decisive = i;
                    break;
                }
            }

            if (decisive >= 0)
            {
                var truth = (Color32)texture.GetPixel(decisive % width, decisive / width);
                var raw = data[decisive];

                if (!Near(new Color32(raw.g, raw.b, raw.a, raw.r), truth)) argb = false;
                if (!Near(new Color32(raw.b, raw.g, raw.r, raw.a), truth)) bgra = false;
            }

            for (var sample = 0; sample < 16 && (argb || bgra); sample++)
            {
                var x = (int)((sample % 4 + 0.5f) * width / 4f);
                var y = (int)((sample / 4 + 0.5f) * height / 4f);
                var truth = (Color32)texture.GetPixel(x, y);
                var raw = data[y * width + x];

                var asArgb = new Color32(raw.g, raw.b, raw.a, raw.r);
                var asBgra = new Color32(raw.b, raw.g, raw.r, raw.a);

                if (!Near(asArgb, truth)) argb = false;
                if (!Near(asBgra, truth)) bgra = false;
            }

            int order;

            if (decisive < 0)
            {
                // Every pixel reads the same both ways, so either loop writes the same bytes.
                order = 1;
            }
            else
            {
                order = argb && !bgra ? 1 : bgra && !argb ? 2 : 0;
            }

            if (order == 0)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" has a {what} decoded as ARGB32 whose bytes match " +
                    (argb && bgra
                        ? "both A,R,G,B and B,G,R,A on a pixel that should tell them apart"
                        : "neither A,R,G,B nor B,G,R,A against GetPixel") +
                    ", so it is refused - this capture draws over it.");
            }
            else
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" - its {what} decoded as ARGB32 with bytes in " +
                    $"{(order == 1 ? "A,R,G,B" : "B,G,R,A")} order" +
                    (decisive < 0
                        ? " (no pixel distinguishes the two orders, so either reads the same bytes)."
                        : $" (decided by pixel {decisive}, cross-checked on 16 samples)."));
            }

            return order;
        }

        /// <summary>Whether two colours agree within one count per channel.</summary>
        private static bool Near(Color32 a, Color32 b) =>
            Math.Abs(a.r - b.r) <= 1 && Math.Abs(a.g - b.g) <= 1 && Math.Abs(a.b - b.b) <= 1 && Math.Abs(a.a - b.a) <= 1;

        /// <summary>One RGB24 pixel as it sits in a decoded texture's memory. Never assigned in C# for
        /// the same reason <see cref="Half4"/> is not.</summary>
#pragma warning disable CS0649
        private struct Rgb24
        {
            public byte R;
            public byte G;
            public byte B;
        }
#pragma warning restore CS0649

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
                var skipping = floor.Verdicts != null && !TileSkipAudit && floor.TilesOwned + floor.TilesOutside > 0;

                // WP1 (2.10): nothing rendered - every tile owned by a closer capture (or outside the mask). No pixel
                // can be taken, so the exposure only has to be the one the pictures on disk were developed with,
                // and the develop reproduces them; there is nothing to measure the light with.
                if (stored != null && skipping && floor.Tiles == 0)
                {
                    floor.Exposure = stored;
                    floor.ReusedExposure = true;

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" rendered no tile - every one is owned by a closer " +
                        "capture or outside the walkable mask - so it keeps its stored exposure with no light test.");
                    return;
                }

                var measured = Measure(plan, floor, out var empty);

                if (TileSkipAudit && stored != null && floor.Verdicts != null) AuditLight(plan, floor, stored, measured);

                if (measured == null)
                {
                    // WP1 (2.10): the rendered tiles drew nothing, so nothing of this capture can be taken either -
                    // the same exact case as no tile rendered.
                    if (empty && stored != null && skipping)
                    {
                        floor.Exposure = stored;
                        floor.ReusedExposure = true;

                        Plugin.LogSource?.LogDebug(
                            $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" - its {floor.Tiles} rendered tile(s) drew nothing " +
                            "to measure, so it keeps its stored exposure with no light test.");
                        return;
                    }

                    if (empty)
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" came back with no drawn pixels at all and was not " +
                            "written.");

                    // Nothing this render can contribute: Measure (or the line above) has said which of the
                    // ways it came back empty. On a merge the floor's earlier picture is kept and the meta
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
        /// <param name="empty">True when no drawn pixel was sampled at all - the caller says so (WP1: or keeps
        /// the stored exposure when the rendered tiles simply drew nothing).</param>
        /// <param name="onlyRendered">WP1 audit mode only: sample only pixels whose tile has this verdict
        /// Render. Null samples every pixel, which is what the capture does.</param>
        /// <param name="quiet">WP1 audit mode's second measurement: say nothing.</param>
        private ExposureResult Measure(Plan plan, FloorPlan floor, out bool empty, TileVerdict[] onlyRendered = null,
            bool quiet = false)
        {
            var pixels = floor.Pixels;
            var count = plan.WidthPx * plan.HeightPx;

            empty = false;

            if (pixels == null)
            {
                if (!quiet)
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

                if (onlyRendered != null &&
                    onlyRendered[TileOf(plan, i % plan.WidthPx, i / plan.WidthPx)] != TileVerdict.Render) continue;

                var at = i * 3;
                var luminance = Luminance(pixels[at], pixels[at + 1], pixels[at + 2]);
                if (IsFinite(luminance)) samples.Add(luminance);
            }

            // The warning is the caller's (WP1): a planned floor whose rendered tiles drew nothing is not a failure.
            if (samples.Count == 0)
            {
                empty = true;
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
            // developed.
            //
            // A MERGE reaches this too, and is meant to: MeasureFloor measures on a merge as well - not
            // to develop with, but for the light test - so a dark render of a floor that already has a
            // good picture fails HERE first, the floor is marked failed, and Carried keeps the picture
            // already on disk. That is the same outcome by a shorter road than the drift test. The line
            // below names the FLOOR for that reason: "nothing was written" is about that floor, and on a
            // multi-floor map the other floors are decided on their own pixels.
            if (high < MinUsableHigh)
            {
                if (!quiet)
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

                if (!quiet)
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has almost no tonal range " +
                        $"({E(low)}..{E(high)}) - stretching its full range instead of its percentiles.");
            }

            if (high - low < MinExposureRange)
            {
                // The check that can fail, and the one that catches a black render: a floor with no
                // range at all is not a picture of anything.
                if (!quiet)
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

            // WP1 (2.9): a floor that rendered no tile after a load before the tiles has no drawn pixel, so none is
            // taken and no band needs the luminance buffer or the smoothing - the picture is the same at any band
            // height, so it goes in PixelBandRows at a time.
            floor.BandRows = floor.Tiles == 0 && floor.PreviousLoaded ? PixelBandRows : DevelopBandRows(plan);

            if (!DevelopBegin(plan, floor)) yield break;

            // The previous picture and its sidecar, a file to a frame: each is a PNG decode of a
            // floor-sized image plus the GetPixels32 that copies it out of the texture. Loaded here unless
            // the tile plan loaded them before the tiles (WP1).
            if (plan.Previous != null && !floor.PreviousLoaded)
            {
                yield return null;
                LoadPreviousColour(plan, floor);

                if (floor.PreviousColour != null)
                {
                    yield return null;
                    LoadPreviousDist(plan, floor);
                    HealSideDist(plan, floor);
                }
            }

            floor.Merged = floor.PreviousColour != null;

            for (var y0 = 0; y0 < plan.HeightPx; y0 += floor.BandRows)
            {
                yield return null;
                if (!DevelopBand(plan, floor, y0)) yield break;
            }

            yield return null;
            DevelopFinish(plan, floor);
        }

        // --- the walkable mask -------------------------------------------------------------------

        /// <summary>
        /// Rasterises the NavMesh into a coarse grid over the extent, grows it, and turns the distance
        /// from it into a per-cell weight the picture is dimmed by.
        ///
        /// What it is for: the extent is padded and clamped to reach past the playable world, so a
        /// capture has a border of hillside, water and skybox terrain that looks exactly like the map and
        /// is not part of it. A player reading the picture cannot tell where the map stops. The NavMesh
        /// is the one thing in the scene that knows: it is where a bot can stand, so it is the playable
        /// world, give or take the walls it stops at - which is what <see cref="ReachDilateMetres"/> is
        /// for.
        ///
        /// Three passes, all cheap on a 150-thousand-cell grid: mark every cell a NavMesh triangle covers
        /// (by the triangle's own bounding box and a barycentric test on the cell centre, so a triangle
        /// larger than a cell fills it rather than only marking its corners); a two-sweep distance
        /// transform outward from the marked cells - city block, see <see cref="Sweep"/>; then the weight,
        /// 255 inside the dilation, falling to 0 over the ramp.
        ///
        /// Null, having said so, when there is no NavMesh - and then nothing is dimmed, which is the
        /// right failure: a map with no mask looks exactly as it did before this existed.
        /// </summary>
        /// <param name="plan">The capture's plan, for the extent the grid covers.</param>
        /// <param name="fromY">Campaign speed step 3: only NavMesh triangles whose mean height is at least this - one band's
        /// own walkable area (see MapCapture.StoredSet.BuildMask). The capture passes nothing: every triangle.</param>
        /// <param name="untilY">...and below this.</param>
        private static byte[] BuildReach(Plan plan, float fromY = float.NegativeInfinity, float untilY = float.PositiveInfinity)
        {
            if (!ReachEnabled) return null;

            try
            {
                var clock = Stopwatch.StartNew();
                var triangulation = MapExtentProbe.Triangulation(plan.Key);
                var vertices = triangulation.vertices;
                var indices = triangulation.indices;

                if (vertices == null || indices == null || vertices.Length == 0 || indices.Length < 3)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} has no NavMesh to mark its walkable area with, so the whole picture " +
                        "is drawn as reachable.");
                    return null;
                }

                var cellsX = Math.Max(1, (int)Math.Ceiling((plan.Extent.MaxX - plan.Extent.MinX) / ReachCellMetres));
                var cellsZ = Math.Max(1, (int)Math.Ceiling((plan.Extent.MaxZ - plan.Extent.MinZ) / ReachCellMetres));

                plan.ReachCellsX = cellsX;
                plan.ReachCellsZ = cellsZ;

                // Distance from the walkable area, in cells, as a chamfer transform. Seeded with 0 on a
                // walkable cell and a number bigger than anything the sweeps can produce elsewhere.
                var far = cellsX + cellsZ + 2;
                var distance = new int[cellsX * cellsZ];
                for (var i = 0; i < distance.Length; i++) distance[i] = far;

                var triangles = 0;
                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var a = vertices[indices[t]];
                    var b = vertices[indices[t + 1]];
                    var c = vertices[indices[t + 2]];

                    // Campaign speed step 3: a band's own mask takes only the triangles at its height; the capture's takes all.
                    var y = (a.y + b.y + c.y) / 3f;
                    if (y < fromY || y >= untilY) continue;

                    if (!Mark(plan, distance, cellsX, cellsZ, a, b, c)) continue;
                    triangles++;
                }

                // Campaign speed step 3: a band with no walkable triangle of its own has an empty mask, not "all reachable" -
                // nothing there is anywhere a player stands.
                if (triangles == 0 && (fromY > float.NegativeInfinity || untilY < float.PositiveInfinity))
                    return new byte[cellsX * cellsZ];

                if (triangles == 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: none of {plan.Key}'s NavMesh falls inside its extent, so the whole picture is " +
                        "drawn as reachable.");
                    return null;
                }

                var inside = Math.Max(1, (int)Math.Round(ReachDilateMetres / ReachCellMetres));
                var ramp = Math.Max(1, (int)Math.Round(ReachRampMetres / ReachCellMetres));

                // The reach discs go in AFTER the "no triangle on this band" returns above, on purpose: a disc never
                // makes a band walkable that has no NavMesh of its own. Such a band's mask is empty here and
                // StoredPicture.BuildMask falls back to the all-storey mask, which carries every disc - so a marker on
                // a band with no NavMesh is never dropped; it lands in the all-storey fallback, as that band's own
                // triangles would have.
                int[] before = null;
                var discs = 0;
                if (ReachDiscs)
                {
                    before = (int[])distance.Clone();
                    discs = MarkReachDiscs(plan, distance, cellsX, cellsZ, fromY, untilY, inside);
                    if (discs == 0) before = null;
                }

                Sweep(distance, cellsX, cellsZ);

                // Cells the discs newly reached: outside the triangles' own fade (weight 0 from triangles alone) and
                // inside the final mask. Measured by sweeping the triangles-only field as well - two passes over a
                // grid of a few hundred thousand cells, and only when a disc was added at all.
                var newlyReached = 0;
                if (before != null)
                {
                    Sweep(before, cellsX, cellsZ);
                    for (var i = 0; i < distance.Length; i++)
                    {
                        if (before[i] - inside >= ramp && distance[i] - inside < ramp) newlyReached++;
                    }

                    var banded = fromY > float.NegativeInfinity || untilY < float.PositiveInfinity;
                    var line =
                        $"QuestTree: {plan.Key}'s walkable mask{(banded ? $" (band y {F(fromY)}..{F(untilY)})" : "")}: " +
                        $"{discs} reach disc(s) of {F(ReachDiscMetres)} m around spawn markers, " +
                        $"{newlyReached} cell(s) of {F(ReachCellMetres)} m newly reached past the NavMesh" +
                        (newlyReached > 0 ? " - the NavMesh in use does not reach every spawn marker." : ".");

                    // At Info only when the discs actually reached something - the fallback firing, which with
                    // Waypoints' NavMesh loaded should be rare and is worth seeing - and for the capture's own mask;
                    // the stored set's per-band masks and a no-op stay at Debug, so a resume does not print one line
                    // per floor.
                    if (banded || newlyReached == 0) Plugin.LogSource?.LogDebug(line);
                    else Plugin.LogSource?.LogInfo(line);
                }

                var reach = new byte[distance.Length];
                var dimmed = 0;

                for (var i = 0; i < distance.Length; i++)
                {
                    var steps = distance[i] - inside;

                    if (steps <= 0)
                    {
                        reach[i] = 255;
                        continue;
                    }

                    dimmed++;

                    if (steps >= ramp)
                    {
                        reach[i] = 0;
                        continue;
                    }

                    reach[i] = (byte)(255 - 255 * steps / ramp);
                }

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key}'s walkable mask is {cellsX}x{cellsZ} cells of {F(ReachCellMetres)} m from " +
                    $"{triangles} NavMesh triangle(s), grown {F(ReachDilateMetres)} m with a {F(ReachRampMetres)} m " +
                    $"ramp; {Share(dimmed, distance.Length)} % of its cells are outside, built in " +
                    $"{Ms(clock.Elapsed.TotalMilliseconds)} ms.");

                return reach;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the walkable mask for {plan.Key} could not be built " +
                    $"({ex.GetType().Name}: {ex.Message}) - the whole picture is drawn as reachable.");
                return null;
            }
        }

        /// <summary>Seeds the distance field with a disc around every spawn marker that lies inside
        /// the extent and on this mask's band, and returns how many discs went in.
        ///
        /// The band rule is the NavMesh triangles' own, on the point's height instead of a triangle's mean: a point
        /// counts when fromY &lt;= y &lt; untilY, so a stored set's per-band mask (StoredPicture.BuildMask, the same
        /// slack included) takes exactly the markers standing at that band's height, and the capture's own mask (no
        /// band) takes them all. Markers never make a FLOOR: the floors come from MapExtentProbe's NavMesh histogram
        /// alone, and a disc only adds reach to a band that already has one.
        ///
        /// The disc is seeded at radius <see cref="ReachDiscMetres"/> less the dilation <paramref name="inside"/> adds
        /// to every seeded cell, so after the sweep it is opaque to <see cref="ReachDiscMetres"/> and fades over the
        /// same <see cref="ReachRampMetres"/> as the NavMesh edge. Marking only ever sets cells to 0, so a disc adds
        /// reach and never takes any away; the reached cells are then like any other - their alpha, the menu's step 0
        /// and a raid's closer-stand merge all read only the finished mask.</summary>
        /// <param name="plan">The capture's plan, for the extent.</param>
        /// <param name="distance">The distance field being seeded.</param>
        /// <param name="cellsX">Grid width.</param>
        /// <param name="cellsZ">Grid height.</param>
        /// <param name="fromY">The band's lowest height, as BuildReach takes it.</param>
        /// <param name="untilY">The height the band stops below.</param>
        /// <param name="inside">The dilation in cells the sweep's weight adds to every seeded cell.</param>
        private static int MarkReachDiscs(
            Plan plan, int[] distance, int cellsX, int cellsZ, float fromY, float untilY, int inside)
        {
            var points = new List<Vector3>();

            try
            {
                foreach (var marker in All<EFT.Game.Spawning.SpawnPointMarker>())
                {
                    if (marker != null) points.Add(marker.transform.position);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the spawn markers for {plan.Key}'s reach discs could not be read ({ex.Message}).");
            }

            // No exfiltration points - see ReachDiscs for why.

            var radiusCells = Math.Max(0d, ReachDiscMetres / ReachCellMetres - inside);
            var span = (int)Math.Ceiling(radiusCells);
            var radiusSquared = radiusCells * radiusCells;
            var discs = 0;

            foreach (var p in points)
            {
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                    float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z)) continue;

                // The triangles' band test, word for word (BuildReach).
                if (p.y < fromY || p.y >= untilY) continue;

                // Inside the final extent only: a point off the picture would mark nothing anybody sees, and a
                // stray far marker - one MapExtentProbe declined to grow to - must not mark the border either.
                if (p.x < plan.Extent.MinX || p.x > plan.Extent.MaxX || p.z < plan.Extent.MinZ || p.z > plan.Extent.MaxZ)
                    continue;

                // The point's position in cell units, so the test below is against cell centres.
                var cx = (p.x - plan.Extent.MinX) / ReachCellMetres;
                var cz = (p.z - plan.Extent.MinZ) / ReachCellMetres;
                var centreX = (int)Math.Floor(cx);
                var centreZ = (int)Math.Floor(cz);

                for (var z = Math.Max(0, centreZ - span); z <= Math.Min(cellsZ - 1, centreZ + span); z++)
                {
                    var dz = z + 0.5d - cz;

                    for (var x = Math.Max(0, centreX - span); x <= Math.Min(cellsX - 1, centreX + span); x++)
                    {
                        var dx = x + 0.5d - cx;
                        if (dx * dx + dz * dz <= radiusSquared) distance[z * cellsX + x] = 0;
                    }
                }

                discs++;
            }

            return discs;
        }

        /// <summary>Marks every cell whose centre falls inside one NavMesh triangle, projected onto XZ.
        /// False when the triangle is outside the extent altogether, which on a map whose NavMesh reaches
        /// past the padding is most of the far ones.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="distance">The distance field being seeded.</param>
        /// <param name="cellsX">Grid width.</param>
        /// <param name="cellsZ">Grid height.</param>
        /// <param name="a">The triangle's first vertex.</param>
        /// <param name="b">Its second.</param>
        /// <param name="c">Its third.</param>
        private static bool Mark(
            Plan plan, int[] distance, int cellsX, int cellsZ, Vector3 a, Vector3 b, Vector3 c)
        {
            var minX = Math.Min(a.x, Math.Min(b.x, c.x));
            var maxX = Math.Max(a.x, Math.Max(b.x, c.x));
            var minZ = Math.Min(a.z, Math.Min(b.z, c.z));
            var maxZ = Math.Max(a.z, Math.Max(b.z, c.z));

            var fromX = Cell(minX - plan.Extent.MinX, cellsX);
            var untilX = Cell(maxX - plan.Extent.MinX, cellsX);
            var fromZ = Cell(minZ - plan.Extent.MinZ, cellsZ);
            var untilZ = Cell(maxZ - plan.Extent.MinZ, cellsZ);

            if (untilX < 0 || untilZ < 0 || fromX >= cellsX || fromZ >= cellsZ) return false;

            fromX = Math.Max(0, fromX);
            fromZ = Math.Max(0, fromZ);
            untilX = Math.Min(cellsX - 1, untilX);
            untilZ = Math.Min(cellsZ - 1, untilZ);

            // The edge functions of the triangle, once, so the per-cell test is three multiplies.
            var area = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
            if (Math.Abs(area) < 1e-6f)
            {
                // A triangle with no area in XZ - a wall's worth of NavMesh, seen edge on: a SEGMENT, between its
                // two farthest corners. Walked at a quarter-cell step and only the cells it crosses marked (review
                // F12) - its bounding box is a square of cells when the segment is diagonal.
                var p = a; var q = b;
                if ((c - a).sqrMagnitude > (q - p).sqrMagnitude) q = c;
                if ((c - b).sqrMagnitude > (q - p).sqrMagnitude) { p = b; q = c; }

                var length = Math.Sqrt((q.x - p.x) * (q.x - p.x) + (q.z - p.z) * (q.z - p.z));
                var steps = Math.Max(1, (int)Math.Ceiling(length / (ReachCellMetres * 0.25)));
                var any = false;

                for (var k = 0; k <= steps; k++)
                {
                    var t = k / (float)steps;
                    var cx = Cell(p.x + (q.x - p.x) * t - plan.Extent.MinX, cellsX);
                    var cz = Cell(p.z + (q.z - p.z) * t - plan.Extent.MinZ, cellsZ);
                    if (cx < 0 || cz < 0 || cx >= cellsX || cz >= cellsZ) continue;

                    distance[cz * cellsX + cx] = 0;
                    any = true;
                }

                return any;
            }

            var inverse = 1f / area;
            var marked = false;

            for (var z = fromZ; z <= untilZ; z++)
            {
                var worldZ = (float)(plan.Extent.MinZ + (z + 0.5d) * ReachCellMetres);

                for (var x = fromX; x <= untilX; x++)
                {
                    var worldX = (float)(plan.Extent.MinX + (x + 0.5d) * ReachCellMetres);

                    var u = ((b.x - worldX) * (c.z - worldZ) - (c.x - worldX) * (b.z - worldZ)) * inverse;
                    if (u < 0f) continue;

                    var v = ((c.x - worldX) * (a.z - worldZ) - (a.x - worldX) * (c.z - worldZ)) * inverse;
                    if (v < 0f) continue;

                    var w = 1f - u - v;
                    if (w < 0f) continue;

                    distance[z * cellsX + x] = 0;
                    marked = true;
                }
            }

            // A triangle smaller than a cell can fall between four cell centres and mark none of them,
            // which would punch a hole in the middle of a walkable floor. Its own cell is marked for it.
            if (!marked)
            {
                var cx = Cell((minX + maxX) * 0.5f - plan.Extent.MinX, cellsX);
                var cz = Cell((minZ + maxZ) * 0.5f - plan.Extent.MinZ, cellsZ);

                if (cx >= 0 && cz >= 0 && cx < cellsX && cz < cellsZ) distance[cz * cellsX + cx] = 0;
            }

            return true;
        }

        /// <summary>A two-sweep distance transform: the number of cells from each cell to the nearest
        /// walkable one, near enough for a mask measured in metres.
        ///
        /// Four-neighbour, so the distance it produces is CITY BLOCK and not Euclidean - the dilation is
        /// a diamond, and where the nearest walkable cell lies diagonally the 8 m of
        /// <see cref="ReachDilateMetres"/> reaches 5.7 m and the ramp ends at 9.9 m instead of 14. That
        /// is written down rather than fixed: the dilation exists to stop a catwalk or a yard being
        /// dimmed, nothing real is 6 m diagonally from every walkable cell around it, and a diagonal
        /// term would change every byte of every capture already on disk for a boundary nobody can see
        /// the shape of.</summary>
        /// <param name="distance">The seeded field, changed in place.</param>
        /// <param name="cellsX">Grid width.</param>
        /// <param name="cellsZ">Grid height.</param>
        private static void Sweep(int[] distance, int cellsX, int cellsZ)
        {
            for (var z = 0; z < cellsZ; z++)
            {
                for (var x = 0; x < cellsX; x++)
                {
                    var at = z * cellsX + x;
                    var best = distance[at];

                    if (x > 0 && distance[at - 1] + 1 < best) best = distance[at - 1] + 1;
                    if (z > 0 && distance[at - cellsX] + 1 < best) best = distance[at - cellsX] + 1;

                    distance[at] = best;
                }
            }

            for (var z = cellsZ - 1; z >= 0; z--)
            {
                for (var x = cellsX - 1; x >= 0; x--)
                {
                    var at = z * cellsX + x;
                    var best = distance[at];

                    if (x + 1 < cellsX && distance[at + 1] + 1 < best) best = distance[at + 1] + 1;
                    if (z + 1 < cellsZ && distance[at + cellsX] + 1 < best) best = distance[at + cellsX] + 1;

                    distance[at] = best;
                }
            }
        }

        /// <summary>Which cell a distance from the extent's corner falls in, which can be off the grid at
        /// either end and is clamped by the caller.</summary>
        /// <param name="metres">Metres from the extent's minimum on that axis.</param>
        /// <param name="cells">How many cells the axis has.</param>
        private static int Cell(double metres, int cells)
        {
            var cell = (int)Math.Floor(metres / ReachCellMetres);
            return cell >= cells ? cells : cell;
        }

        /// <summary>How reachable one output pixel is, 0 to 1, sampled from the mask with bilinear
        /// interpolation - the cells are sixteen pixels across at an eighth of a metre to the pixel, and
        /// nearest-cell sampling would draw the ramp as a staircase of 16-pixel blocks.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="col">The pixel's column.</param>
        /// <param name="row">The pixel's row in texture order, counting from the bottom.</param>
        private static float ReachAt(Plan plan, int col, int row)
        {
            var reach = plan.Reach;
            if (reach == null) return 1f;

            var cellsX = plan.ReachCellsX;
            var cellsZ = plan.ReachCellsZ;

            // The picture is anchored at the extent's MaxZ edge (PositionCamera: row t is at
            // MaxZ - (HeightPx - t - 0.5) / ppm), and HeightPx / ppm overshoots the extent's depth by up to a
            // pixel; the mask's cells are anchored at MinZ. So a row's z is taken from the MaxZ side (review
            // F11) - anchoring it at MinZ shifted the ramp up to 1/ppm south of the NavMesh.
            var x = ((col + 0.5f) / plan.Ppm) / ReachCellMetres - 0.5f;
            var depth = (float)(plan.Extent.MaxZ - plan.Extent.MinZ);
            var z = (depth - (plan.HeightPx - row - 0.5f) / plan.Ppm) / ReachCellMetres - 0.5f;

            var x0 = (int)Math.Floor(x);
            var z0 = (int)Math.Floor(z);
            var fx = x - x0;
            var fz = z - z0;

            var x1 = x0 + 1;
            var z1 = z0 + 1;

            if (x0 < 0) { x0 = 0; fx = 0f; }
            if (z0 < 0) { z0 = 0; fz = 0f; }
            if (x1 > cellsX - 1) x1 = cellsX - 1;
            if (z1 > cellsZ - 1) z1 = cellsZ - 1;
            if (x0 > cellsX - 1) x0 = cellsX - 1;
            if (z0 > cellsZ - 1) z0 = cellsZ - 1;

            var bottom = reach[z0 * cellsX + x0] + (reach[z0 * cellsX + x1] - reach[z0 * cellsX + x0]) * fx;
            var top = reach[z1 * cellsX + x0] + (reach[z1 * cellsX + x1] - reach[z1 * cellsX + x0]) * fx;

            return (bottom + (top - bottom) * fz) / 255f;
        }

        // --- what the scene hides ----------------------------------------------------------------

        /// <summary>Flattens every DisablerCullingObject's switch list into one array of renderers and LOD
        /// groups, and its deactivated GameObjects into a second, once per capture. See
        /// <see cref="ForceCulling"/> for why this is needed at all.
        ///
        /// Renderers and LOD groups from the component lists, and nothing else from them: the lists also
        /// hold lights, fog lights and lamp controllers, and those are deliberately left alone - enabling a
        /// hundred distant lights would change the exposure between one tile and the next and seam the
        /// picture, and the capture brings its own light for exactly that reason.
        ///
        /// The GameObject list (_gameObjectsToTurnOff) IS taken, which this comment used to deny. It was
        /// left alone at first because activating a GameObject runs Awake and OnEnable on whatever is
        /// attached to it - the game's own code running because a map was being photographed - and that
        /// caution cost the capture whole buildings: a culler that deactivates the OBJECT never disables
        /// the renderer on it, so nothing in the component lists could bring it back, and a capture with
        /// the components forced still had buildings missing. Interchange's interior floors are in the
        /// picture since these were taken; Big Red's roof was not, which the throwaway probe key settled
        /// (its mesh is on the HighPolyCollider layer, which the mask left out by name). So
        /// they are taken, and the risk is paid for instead: each object is switched one at a time inside
        /// its own try (HoldScene), its pre-state is recorded before the switch, and ReleaseScene puts back
        /// only what was changed. <see cref="ForceCulling"/> states the two limitations that remain.
        ///
        /// FindObjectsOfType is the expensive part, so it happens here and never again, and the count
        /// and the time are logged.</summary>
        private void CollectCulling()
        {
            _culling = null;
            _cullingWasEnabled = null;
            _cullingObjects = 0;
            _proxyRenderers = null;
            _gameCulled = null;
            _occlusionCulled = null;

            if (!ForceCulling) return;

            try
            {
                var clock = Stopwatch.StartNew();
                var components = new List<Component>();
                var objectsToTurnOn = new List<GameObject>();
                var objects = FindObjectsOfType<DisablerCullingObject>();
                var owners = new List<int>();
                var objectOwners = new List<int>();

                for (var c = 0; c < objects.Length; c++)
                {
                    var culler = objects[c];
                    if (culler == null) continue;

                    _cullingObjects++;

                    var before = components.Count;
                    Take(components, culler._componentsToTurnOff);
                    Take(components, culler._compsToTurnOffWhoIgnoreInversedColliders);
                    for (var k = before; k < components.Count; k++) owners.Add(c);

                    var beforeObjects = objectsToTurnOn.Count;
                    TakeObjects(objectsToTurnOn, culler._gameObjectsToTurnOff);
                    for (var k = beforeObjects; k < objectsToTurnOn.Count; k++) objectOwners.Add(c);
                }

                _cullers = objects;
                _cullingOwner = owners.ToArray();
                _cullingObjectOwner = objectOwners.ToArray();
                _culling = components.ToArray();
                _cullingWasEnabled = new bool[_culling.Length];
                _cullingObjectsHeld = objectsToTurnOn.ToArray();
                _cullingObjectWasActive = new bool[_cullingObjectsHeld.Length];

                var found = clock.Elapsed.TotalMilliseconds;
                var gameCulled = CollectGameCulled();

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {_cullingObjects} culling object(s) hold {_culling.Length} renderer(s) and LOD " +
                    $"group(s) plus {_cullingObjectsHeld.Length} whole GameObject(s) the capture will force " +
                    $"visible, found in {Ms(found)} ms; {gameCulled} in " +
                    $"{Ms(clock.Elapsed.TotalMilliseconds - found)} ms.");
            }
            catch (Exception ex)
            {
                _culling = null;
                _cullingWasEnabled = null;
                _cullingObjectsHeld = null;
                _cullingObjectWasActive = null;
                _cullers = null;
                _cullingOwner = null;
                _cullingObjectOwner = null;
                _proxyRenderers = null;
                _gameCulled = null;
                _occlusionCulled = null;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scene's distance culling could not be read " +
                    $"({ex.GetType().Name}: {ex.Message}) - buildings whose roofs are switched off at this " +
                    "distance will be captured as ground.");
            }
        }

        /// <summary>Adds one culling object's DEACTIVATED GameObject list to the flat array.
        ///
        /// These are what Big Red's and the tower's roofs turned out to be: the component lists hold the
        /// renderers of a building's shell, but a whole storey - the roof, its beams, the shelving under
        /// it - is switched by deactivating the object it hangs on, and forcing the components alone left
        /// those buildings open to the sky with their interiors showing.
        ///
        /// Activating a GameObject is a heavier thing than enabling a renderer: whatever is attached to
        /// it gets Awake and OnEnable, so a particle system starts emitting, a light comes on, an audio
        /// source begins. The hold lasts a floor and is put back, so the worst of that is a second of a
        /// distant chimney smoking - but it IS a side effect, it is why each object is switched under its
        /// own guard, and it is why only objects that were INACTIVE are touched.</summary>
        /// <param name="into">The flat list being built.</param>
        /// <param name="objects">One culling object's deactivation list, which may be null.</param>
        private static void TakeObjects(List<GameObject> into, List<GameObject> objects)
        {
            if (objects == null) return;

            foreach (var item in objects)
            {
                if (item == null) continue;
                into.Add(item);
            }
        }

        /// <summary>
        /// WP8 (D6): the two runtime culling systems the hold does not touch, read once a capture for the mesh
        /// build. EFT's baked-LOD <c>ScreenDistanceSwitcher</c> shows a merged, auto-simplified PROXY of its area
        /// (<c>GetBakedLodRenderers</c>) when the area is far from the player's camera and switches the detail
        /// off (forceRenderingOff, or enabled through its delegates); Perfect Culling's occlusion
        /// (<c>PerfectCullingCrossSceneGroup.bakeGroups[].renderers</c>) switches renderers off by
        /// <c>Renderer.enabled</c> from where the player stands. Proxies are never buildings; the rest is
        /// GAME-CULLED - geometry hidden from a position, not from the map - and the build reads it even when it
        /// is off. Each switcher and each bake group is taken inside its own guard, as TakeObjects does. Decided
        /// per renderer from what owns it, never from a map. Returns the counts for the culling debug line.
        /// </summary>
        private string CollectGameCulled()
        {
            _proxyRenderers = new HashSet<Renderer>();
            _gameCulled = new HashSet<Renderer>();
            _occlusionCulled = new HashSet<Renderer>();

            int switchers = 0, bakeGroups = 0, failed = 0;
            var content = 0;

            Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher[] found = null;

            try
            {
                // PART-10 (hardening): a switcher on an inactive object too - its control group's proxies are still marked
                // (only the renderers are used, so this is harmless)
                found = FindObjectsOfType<Koenigz.PerfectCulling.EFT.ScreenDistanceSwitcher>(true);
            }
            catch (Exception)
            {
                failed++;
            }

            if (found != null)
            {
                // Every proxy first: one switcher's content may hold another's hull.
                foreach (var switcher in found)
                {
                    if (switcher == null) continue;

                    try
                    {
                        foreach (var renderer in switcher.GetBakedLodRenderers())
                            if (renderer != null) _proxyRenderers.Add(renderer);

                        switchers++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }

                foreach (var switcher in found)
                {
                    if (switcher == null) continue;

                    try
                    {
                        foreach (var renderer in switcher.GetComponentsInChildren<MeshRenderer>(true))
                            if (renderer != null && !_proxyRenderers.Contains(renderer) && _gameCulled.Add(renderer))
                                content++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }
            }

            Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneGroup[] groups = null;

            try
            {
                groups = FindObjectsOfType<Koenigz.PerfectCulling.EFT.PerfectCullingCrossSceneGroup>();
            }
            catch (Exception)
            {
                failed++;
            }

            if (groups != null)
                foreach (var group in groups)
                {
                    if (group == null || group.bakeGroups == null) continue;

                    foreach (var bake in group.bakeGroups)
                    {
                        try
                        {
                            if (bake?.renderers == null) continue;

                            bakeGroups++;

                            foreach (var renderer in bake.renderers)
                            {
                                if (renderer == null || _proxyRenderers.Contains(renderer)) continue;

                                _gameCulled.Add(renderer);
                                _occlusionCulled.Add(renderer);
                            }
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }

            return $"{switchers} baked-LOD switcher(s) with {_proxyRenderers.Count} proxy renderer(s) the mesh excludes and " +
                   $"{content} content renderer(s), {bakeGroups} occlusion bake group(s) with {_occlusionCulled.Count} " +
                   $"renderer(s) - {_gameCulled.Count} game-culled renderer(s) the mesh reads even when switched off" +
                   (failed > 0 ? $", {failed} unreadable" : "");
        }

        /// <summary>Adds the renderers and LOD groups of one switch list to the flat array.</summary>
        /// <param name="into">The flat list being built.</param>
        /// <param name="components">One culling object's switch list, which may be null.</param>
        private static void Take(List<Component> into, List<Component> components)
        {
            if (components == null) return;

            foreach (var component in components)
            {
                if (component == null) continue;
                if (component is Renderer || component is LODGroup) into.Add(component);
            }
        }

        /// <summary>Finds every renderer on the water LAYER, once per capture, keeps the materials each of
        /// them draws with, and logs the distinct shader names it saw on them - as evidence about what a
        /// map puts on that layer, not as a test: nothing here looks at a shader's name to decide
        /// anything, which an earlier version of this summary claimed it did. See
        /// <see cref="WaterLayerName"/> and <see cref="WaterPaint"/>.</summary>
        private void CollectWater()
        {
            _water = null;
            _waterMaterials = null;

            if (!PaintWater) return;

            try
            {
                var clock = Stopwatch.StartNew();
                var layer = LayerMask.NameToLayer(WaterLayerName);

                if (layer < 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: this game version has no \"{WaterLayerName}\" layer, so water is captured as " +
                        "the game draws it.");
                    return;
                }

                var found = new List<Renderer>();
                var shaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Reused for every renderer in the scene: the alternative is renderer.sharedMaterials,
                // which allocates an array apiece - tens of thousands of them on a map this size.
                var materials = new List<Material>(4);

                foreach (var renderer in FindObjectsOfType<Renderer>())
                {
                    if (renderer == null) continue;

                    // The layer IS the test - see WaterLayerName. Everything below only collects the
                    // shader names of what was found, for the log line.
                    if (renderer.gameObject.layer != layer) continue;

                    found.Add(renderer);

                    // sharedMaterial(s), not material(s): reading material INSTANTIATES a copy of it on
                    // the renderer, which would leak a material per water surface per capture. Read here
                    // only to NAME what was found - the layer above is what decided it.
                    materials.Clear();
                    renderer.GetSharedMaterials(materials);

                    foreach (var material in materials)
                    {
                        if (material == null || material.shader == null) continue;
                        if (string.IsNullOrEmpty(material.shader.name)) continue;

                        shaders.Add(material.shader.name);
                    }
                }

                _water = found.ToArray();

                // Read ONCE, not per render: a scene does not reassign its water materials mid-raid, and
                // sharedMaterials allocates an array every time it is read.
                _waterMaterials = new Material[_water.Length][];
                for (var i = 0; i < _water.Length; i++) _waterMaterials[i] = _water[i].sharedMaterials;

                if (_water.Length > 0)
                {
                    BuildWaterPaint();

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {_water.Length} renderer(s) on the {WaterLayerName} layer will be painted " +
                        (_waterFlat != null ? "flat blue" : "NOTHING - no flat shader resolved, so they draw as they are") +
                        $", on shader(s) [{string.Join(", ", shaders.ToArray())}], found in " +
                        $"{Ms(clock.Elapsed.TotalMilliseconds)} ms.");
                }
                else
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: nothing in this scene is on the {WaterLayerName} layer, so the cyan pass is the " +
                        "only thing standing between a pool and the picture.");
                }
            }
            catch (Exception ex)
            {
                _water = null;
                _waterMaterials = null;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scene water renderers could not be found " +
                    $"({ex.GetType().Name}: {ex.Message}) - pools may appear as sheets of sky.");
            }
        }

        /// <summary>Builds the one flat material every water renderer is painted with, from the first
        /// shader of <see cref="WaterShaders"/> this build has. Leaves it null, with one Info line, when
        /// none resolves - and then the water draws as the game draws it, which with the neutral
        /// reflection above is a duller sheet than it was but still not a river.</summary>
        private void BuildWaterPaint()
        {
            if (_waterFlat != null) return;

            var tried = new List<string>();

            foreach (var name in WaterShaders)
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

                if (shader == null)
                {
                    tried.Add(name);
                    continue;
                }

                try
                {
                    var material = new Material(shader) { color = WaterPaint };

                    // Unlit/Texture has no colour property at all, so the colour has to arrive as a
                    // texture. One pixel of it, which the sampler stretches over the whole surface.
                    if (material.HasProperty("_MainTex"))
                    {
                        _waterFlatTexture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
                        _waterFlatTexture.SetPixel(0, 0, WaterPaint);
                        _waterFlatTexture.Apply(updateMipmaps: false);
                        material.mainTexture = _waterFlatTexture;
                    }

                    _waterFlat = material;
                    _waterFlatArrays = new Dictionary<int, Material[]>();

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: water is painted with \"{name}\" in " +
                        $"{F(WaterPaint.r)},{F(WaterPaint.g)},{F(WaterPaint.b)}" +
                        (_waterFlatTexture != null ? " through a one-pixel texture." : "."));
                    return;
                }
                catch (Exception ex)
                {
                    tried.Add($"{name} would not make a material ({ex.GetType().Name})");
                }
            }

            Plugin.LogSource?.LogInfo(
                $"QuestTree: no flat shader for the water resolved (tried [{string.Join(", ", tried.ToArray())}]), " +
                "so water renderers are captured as they are. The cyan pass is still there for whatever comes " +
                "back flat.");
        }

        /// <summary>An array of the flat water material as long as a renderer has material slots, cached
        /// per length: assigning sharedMaterials needs an array of the renderer own length, and building
        /// one per renderer per tile would be thousands of allocations a floor.</summary>
        /// <param name="slots">How many material slots the renderer has.</param>
        private Material[] FlatWaterArray(int slots)
        {
            if (_waterFlatArrays.TryGetValue(slots, out var array)) return array;

            array = new Material[slots];
            for (var i = 0; i < slots; i++) array[i] = _waterFlat;

            _waterFlatArrays[slots] = array;
            return array;
        }

        /// <summary>Paints every water renderer flat for the FLOOR that follows, from
        /// <see cref="HoldScene"/>, and counts how far it got so <see cref="ReleaseWater"/> puts back
        /// exactly what it changed.
        ///
        /// sharedMaterials, never material or materials: the latter two INSTANTIATE the material on the
        /// renderer, which would leave a copy per water surface behind in the scene for good.</summary>
        private void HoldWater()
        {
            _waterSwapped = 0;

            if (_water == null || _waterFlat == null || _waterMaterials == null) return;

            try
            {
                for (var i = 0; i < _water.Length; i++)
                {
                    var renderer = _water[i];
                    _waterSwapped = i + 1;

                    var original = _waterMaterials[i];
                    if (renderer == null || original == null || original.Length == 0) continue;

                    renderer.sharedMaterials = FlatWaterArray(original.Length);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the water could not be painted for this tile ({ex.GetType().Name}: " +
                    $"{ex.Message}) - whatever was swapped is put back.");
            }
        }

        /// <summary>Puts every water renderer back to the materials <see cref="CollectWater"/> found on
        /// it. Idempotent and never throws: <see cref="ReleaseScene"/> calls it after every floor, and
        /// <see cref="Cleanup"/> calls it again for a raid that ended mid-floor.</summary>
        private void ReleaseWater()
        {
            if (_water == null || _waterMaterials == null)
            {
                _waterSwapped = 0;
                return;
            }

            try
            {
                for (var i = 0; i < _waterSwapped && i < _water.Length; i++)
                {
                    var renderer = _water[i];
                    var original = _waterMaterials[i];

                    if (renderer == null || original == null || original.Length == 0) continue;

                    renderer.sharedMaterials = original;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the water materials could not be put back ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                _waterSwapped = 0;
            }
        }

        /// <summary>Builds the one-colour cubemap the capture reflects, or leaves it null with a warning.
        /// A half-float cubemap so the value written is the LINEAR value meant - an eight-bit one would be
        /// read as sRGB and reflect a third of the intended brightness - with an eight-bit fallback whose
        /// grey is converted so both reflect the same light.</summary>
        private void BuildReflection()
        {
            try
            {
                var half = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf);

                var cube = new Cubemap(
                    ReflectionCubeSize,
                    half ? TextureFormat.RGBAHalf : TextureFormat.RGBA32,
                    mipChain: false);

                var grey = half
                    ? new Color(ReflectionGrey, ReflectionGrey, ReflectionGrey, 1f)
                    : new Color(ReflectionGrey, ReflectionGrey, ReflectionGrey, 1f).gamma;

                var face = new Color[ReflectionCubeSize * ReflectionCubeSize];
                for (var i = 0; i < face.Length; i++) face[i] = grey;

                cube.SetPixels(face, CubemapFace.PositiveX);
                cube.SetPixels(face, CubemapFace.NegativeX);
                cube.SetPixels(face, CubemapFace.PositiveY);
                cube.SetPixels(face, CubemapFace.NegativeY);
                cube.SetPixels(face, CubemapFace.PositiveZ);
                cube.SetPixels(face, CubemapFace.NegativeZ);
                cube.Apply(updateMipmaps: false);

                _reflection = cube;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the capture reflects a flat {F(ReflectionGrey)} grey " +
                    $"({(half ? "half-float" : "eight-bit")}) at {F(ReflectionIntensity)} intensity, so reflective " +
                    "roofs and metal do not come back as sheets of sky.");
            }
            catch (Exception ex)
            {
                _reflection = null;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture reflection environment could not be built " +
                    $"({ex.GetType().Name}: {ex.Message}) - reflective surfaces may appear as flat sheets of sky " +
                    "colour.");
            }
        }

        /// <summary>Switches every hidden renderer on and every water renderer off, remembering what each
        /// was, for the FLOOR that follows - see <see cref="ForceCulling"/> for why it is a floor and not
        /// a render. Undone by <see cref="ReleaseScene"/>, which the floor loop calls after the last tile
        /// however that tile went, and which <see cref="Cleanup"/> calls again for the raid that ends in
        /// the middle of one.
        ///
        /// Never throws. A hold that failed halfway would otherwise take the floor down with it, and
        /// what it has already switched is recorded as it goes, so the release puts back exactly the
        /// entries this got to and no others.</summary>
        private void HoldScene()
        {
            // How far each loop got, so a throw partway through cannot have the restore act on entries
            // still holding the PREVIOUS floor's states - which would switch off geometry the game had
            // on and leave it off.
            _cullingHeld = 0;

            try
            {
                var clock = Stopwatch.StartNew();
                var forced = 0;

                if (_culling != null)
                {
                    for (var i = 0; i < _culling.Length; i++)
                    {
                        var component = _culling[i];

                        // "Was on" first, so an entry counted before its state is read is one the release leaves
                        // alone - never the previous floor's state (review F08).
                        _cullingWasEnabled[i] = true;
                        _cullingHeld = i + 1;

                        if (component == null) continue;

                        _cullingWasEnabled[i] = component.IsEnabledUniversal();
                        if (_cullingWasEnabled[i]) continue;

                        component.SetEnabledUniversal(true);
                        forced++;
                    }
                }

                var activated = 0;

                if (_cullingObjectsHeld != null)
                {
                    for (var i = 0; i < _cullingObjectsHeld.Length; i++)
                    {
                        var item = _cullingObjectsHeld[i];
                        _cullingObjectsHeldCount = i + 1;

                        if (item == null) continue;

                        // Guarded ONE BY ONE, not as a block: activating an object runs its Awake and
                        // OnEnable, and a script of the game's own that throws in one must not stop the
                        // rest of the roofs coming back.
                        var recorded = false;

                        try
                        {
                            _cullingObjectWasActive[i] = item.activeSelf;
                            recorded = true;
                            if (_cullingObjectWasActive[i]) continue;

                            item.SetActive(true);
                            activated++;
                        }
                        catch (Exception ex)
                        {
                            // Only when the pre-state was never read. SetActive(true) is what runs the
                            // object's Awake and OnEnable, so a throw from here means the object may
                            // ALREADY be active - and writing "was active" over a recorded false would
                            // make the release skip it and leave a roof switched on for the rest of the
                            // raid. A record that was taken is left exactly as it was taken; only the
                            // activeSelf read above, which can throw on a destroyed object before
                            // anything was written, needs the safe default.
                            if (!recorded) _cullingObjectWasActive[i] = true;

                            Plugin.LogSource?.LogDebug(
                                $"QuestTree: a culled object would not switch on ({ex.GetType().Name}: {ex.Message}).");
                        }
                    }
                }

                // The water goes flat here too, on the same schedule and for the same reason: once a
                // floor is cheaper than once a tile, and a floor is the unit the scene is held for.
                HoldWater();

                // The measurement the per-floor hold rests on - see ForceCulling. Once a floor, and
                // only interesting when a capture hitches, so debug rather than info.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the scene is held for a floor - {forced} of {_cullingHeld} culled " +
                    $"component(s) forced visible, {activated} of {_cullingObjectsHeldCount} culled object(s) " +
                    $"switched on and {_waterSwapped} water renderer(s) painted flat in " +
                    $"{Ms(clock.Elapsed.TotalMilliseconds)} ms.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scene could not be held for this floor ({ex.GetType().Name}: " +
                    $"{ex.Message}) - whatever it had already switched is put back, and the floor is " +
                    "captured with what the game is drawing.");
            }
        }

        /// <summary>Puts every renderer back to what <see cref="HoldScene"/> found it at. Only the ones
        /// that were changed are written to, so the scene is left as the game had it and nothing is
        /// touched twice; only as far as the hold actually got, so a hold that threw cannot have this
        /// switch off something it never switched on.
        ///
        /// Idempotent and never throws, because the floor loop calls it and <see cref="Cleanup"/> calls
        /// it again: the counts are cleared here, so the second call has nothing to do.
        ///
        /// An entry whose culler says the player is inside it (its own HasEntered) is left on, and every
        /// culler an entry of which was restored is then asked to re-apply its own state (ForceUpdate), so
        /// the game - not a guess at its colliders - decides what a player who moved during the hold sees
        /// (review F07).</summary>
        private void ReleaseScene()
        {
            var cullers = _cullers;
            var held = _cullingHeld > 0 || _cullingObjectsHeldCount > 0;   // the second (Cleanup) call has nothing
            var asks = ReleaseAsksCullers && held && cullers != null;
            var touched = asks ? new bool[cullers.Length] : null;
            var all = false;
            var keptOn = 0;

            try
            {
                if (_culling != null)
                {
                    for (var i = 0; i < _cullingHeld && i < _culling.Length; i++)
                    {
                        var component = _culling[i];
                        if (component == null) continue;

                        // Marked BEFORE the already-on skip (review of PART-01): a culler the player stands inside
                        // has every entry on before the hold, so nothing below writes to it - but a renderer it
                        // shares with a neighbouring culler is switched off when THAT one re-applies its state, and
                        // only a culler that is asked as well turns the shared renderer back on.
                        var owner = OwnerOf(_cullingOwner, i);
                        if (touched != null && owner >= 0 && owner < touched.Length) touched[owner] = true;

                        if (_cullingWasEnabled[i]) continue;

                        // The game's own answer (review F07): a culler whose triggers hold the player wants all of its
                        // lists ON - HasEntered implies _enteredColliders.Count > 0, which is what the
                        // ignore-inverse list follows too - so its entries stay as the hold left them.
                        if (asks && Entered(cullers, owner))
                        {
                            keptOn++;
                            continue;
                        }

                        component.SetEnabledUniversal(false);
                    }
                }

                if (_cullingObjectsHeld != null)
                {
                    for (var i = 0; i < _cullingObjectsHeldCount && i < _cullingObjectsHeld.Length; i++)
                    {
                        var item = _cullingObjectsHeld[i];
                        if (item == null) continue;

                        var owner = OwnerOf(_cullingObjectOwner, i);
                        if (touched != null && owner >= 0 && owner < touched.Length) touched[owner] = true;

                        if (_cullingObjectWasActive[i]) continue;

                        if (asks && Entered(cullers, owner))
                        {
                            keptOn++;
                            continue;
                        }

                        // Guarded one by one for the same reason the hold is: OnDisable runs here.
                        try
                        {
                            item.SetActive(false);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogDebug(
                                $"QuestTree: a culled object would not switch off again ({ex.GetType().Name}: " +
                                $"{ex.Message}) - the game's own culling switches it when the player next moves.");
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                all = true;   // a partial restore: let the game re-apply every culler, not just the ones reached

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scene could not be put back after a floor ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
            }
            finally
            {
                _cullingHeld = 0;
                _cullingObjectsHeldCount = 0;
                ReleaseWater();

                if (touched != null) Resync(cullers, touched, all, keptOn);
            }
        }

        /// <summary>Rollback switch for review F07: false has the release restore the hold's snapshot exactly and
        /// ask no culler anything (f1aa04f without its geometric test, which gave wrong answers both ways).</summary>
        private static readonly bool ReleaseAsksCullers = true;

        /// <summary>The culler that owns flat-list entry <paramref name="i"/>, or -1.</summary>
        /// <param name="owners">The owner indices.</param>
        /// <param name="i">The entry.</param>
        private static int OwnerOf(int[] owners, int i) => owners != null && i < owners.Length ? owners[i] : -1;

        /// <summary>Whether the game says the player is inside this culler - its own HasEntered, i.e. its trigger
        /// colliders have reported the player and no inverse one has. False for no culler, or one that will not say.</summary>
        /// <param name="cullers">The culling objects.</param>
        /// <param name="owner">The culler's index.</param>
        private static bool Entered(DisablerCullingObject[] cullers, int owner)
        {
            if (cullers == null || owner < 0 || owner >= cullers.Length) return false;

            try
            {
                var culler = cullers[owner];
                return culler != null && culler.HasEntered;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Asks each culler this hold touched (or all of them) to re-apply its own state on its next
        /// ManualUpdate (DisablerCullingObject.ForceUpdate - a flag): SetComponentsEnabled(HasEntered), with the
        /// ignore-inverse list on _enteredColliders.Count > 0. So whatever the snapshot restore got wrong for a player
        /// who moved during the hold - an inverse collider entered, a trigger crossed while the game's own worker
        /// was mid-switch - the game corrects, exactly as it does whenever the player crosses a culler's trigger.</summary>
        /// <param name="cullers">The culling objects.</param>
        /// <param name="touched">Which of them had an entry the release switched or left on.</param>
        /// <param name="all">True to ask every culler: the restore did not finish.</param>
        /// <param name="keptOn">Entries left on for the player inside their culler, for the line.</param>
        private static void Resync(DisablerCullingObject[] cullers, bool[] touched, bool all, int keptOn)
        {
            var asked = 0;

            for (var c = 0; c < cullers.Length; c++)
            {
                if (!all && (c >= touched.Length || !touched[c])) continue;

                try
                {
                    var culler = cullers[c];
                    if (culler == null) continue;

                    culler.ForceUpdate();
                    asked++;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: a culling object would not take a re-check ({ex.Message}).");
                }
            }

            Plugin.LogSource?.LogDebug(
                $"QuestTree: the scene is let go - {asked} culling object(s) asked to re-apply their own state" +
                (keptOn > 0 ? $", {keptOn} entry(ies) left on for the player inside their culler" : "") +
                (all ? " (all of them: the restore did not finish)" : "") + ".");
        }

        // --- the water quads ---------------------------------------------------------------------

        /// <summary>
        /// Paints out the flat cyan water quads, in the float buffer, before anything reads it.
        ///
        /// Two passes, both spread over frames. The first finds them - one band of rows to a frame,
        /// appending to <see cref="FloorPlan.Cyan"/>. The second replaces each with the mean of the
        /// non-cyan drawn pixels around it, <see cref="InpaintChunkPixels"/> to a frame, growing the
        /// window until it finds some; a pixel with nothing usable within the largest window is marked
        /// UNDRAWN, which makes it a hole another capture may fill rather than a patch of invented
        /// colour.
        ///
        /// The fill reads the buffer as it goes, so a pixel already repainted can be the source for its
        /// neighbour and the fill spreads inward from a pool's edge. That is deliberate: a strict
        /// version reading only original pixels would leave the middle of a large pool unfilled, and a
        /// pool's interior is exactly what has to go. It makes the result depend on the scan order,
        /// which is fixed (rows, then columns), so two captures of the same scene still produce the
        /// same picture.
        ///
        /// Skipped entirely, and cheaply, when <see cref="FillWaterCyan"/> is off.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor whose pixels are to be cleaned.</param>
        private IEnumerator Inpaint(Plan plan, FloorPlan floor)
        {
            if (!FillWaterCyan || floor.Pixels == null || floor.Drawn == null) yield break;

            floor.Cyan = new List<int>();

            for (var y0 = 0; y0 < plan.HeightPx; y0 += PixelBandRows)
            {
                yield return null;
                if (!ClassifyBand(plan, floor, y0)) yield break;
            }

            if (floor.Cyan.Count == 0)
            {
                floor.Cyan = null;
                yield break;
            }

            for (var from = 0; from < floor.Cyan.Count; from += InpaintChunkPixels)
            {
                yield return null;
                if (!FillChunk(plan, floor, from)) yield break;
            }

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" - {floor.Cyan.Count} cyan water pixels found, " +
                $"{floor.CyanFilled} painted out, {floor.CyanDropped} left as holes.");

            floor.Cyan = null;
        }

        /// <summary>One band of rows searched for water quads. False, having failed the floor, on any
        /// error.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being cleaned.</param>
        /// <param name="y0">The band's first row, counting from the bottom.</param>
        private static bool ClassifyBand(Plan plan, FloorPlan floor, int y0)
        {
            try
            {
                var rows = Math.Min(PixelBandRows, plan.HeightPx - y0);
                var pixels = floor.Pixels;

                for (var row = 0; row < rows; row++)
                {
                    var index = (y0 + row) * plan.WidthPx;

                    for (var col = 0; col < plan.WidthPx; col++, index++)
                    {
                        if (!floor.Drawn[index]) continue;

                        var at = index * 3;
                        if (IsWaterCyan(pixels[at], pixels[at + 1], pixels[at + 2])) floor.Cyan.Add(index);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not be searched for water " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>One chunk of the found water pixels repainted. False, having failed the floor, on
        /// any error.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being cleaned.</param>
        /// <param name="from">The first index of this chunk in <see cref="FloorPlan.Cyan"/>.</param>
        private static bool FillChunk(Plan plan, FloorPlan floor, int from)
        {
            try
            {
                var until = Math.Min(from + InpaintChunkPixels, floor.Cyan.Count);
                var pixels = floor.Pixels;

                for (var i = from; i < until; i++)
                {
                    var index = floor.Cyan[i];
                    var row = index / plan.WidthPx;
                    var col = index - row * plan.WidthPx;

                    if (Mean(plan, floor, row, col, out var r, out var g, out var b))
                    {
                        var at = index * 3;
                        pixels[at] = r;
                        pixels[at + 1] = g;
                        pixels[at + 2] = b;
                        floor.CyanFilled++;
                    }
                    else
                    {
                        // Nothing to copy from: the middle of a pool whose every neighbour is water or
                        // hole. Left UNDRAWN, so the merge keeps whatever the last capture had there
                        // and the log counts it among the pixels still to fill.
                        floor.Drawn[index] = false;
                        floor.CyanDropped++;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" could not have its water painted out " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>The mean of the drawn, non-water pixels in the smallest window around one pixel that
        /// holds any. False when even the largest window holds none.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being cleaned.</param>
        /// <param name="row">The pixel's row, counting from the bottom.</param>
        /// <param name="col">The pixel's column.</param>
        /// <param name="r">The mean red.</param>
        /// <param name="g">The mean green.</param>
        /// <param name="b">The mean blue.</param>
        private static bool Mean(
            Plan plan, FloorPlan floor, int row, int col, out float r, out float g, out float b)
        {
            var pixels = floor.Pixels;

            foreach (var window in InpaintWindows)
            {
                var reach = window / 2;
                var rowFrom = Math.Max(0, row - reach);
                var rowUntil = Math.Min(plan.HeightPx - 1, row + reach);
                var colFrom = Math.Max(0, col - reach);
                var colUntil = Math.Min(plan.WidthPx - 1, col + reach);

                var sumR = 0f;
                var sumG = 0f;
                var sumB = 0f;
                var found = 0;

                for (var y = rowFrom; y <= rowUntil; y++)
                {
                    var index = y * plan.WidthPx + colFrom;

                    for (var x = colFrom; x <= colUntil; x++, index++)
                    {
                        if (!floor.Drawn[index]) continue;

                        var at = index * 3;
                        var pr = pixels[at];
                        var pg = pixels[at + 1];
                        var pb = pixels[at + 2];

                        // A pixel that still tests as water is not a source, whether it is one this
                        // pass has yet to reach or the one being filled.
                        if (IsWaterCyan(pr, pg, pb)) continue;

                        sumR += pr;
                        sumG += pg;
                        sumB += pb;
                        found++;
                    }
                }

                if (found == 0) continue;

                r = sumR / found;
                g = sumG / found;
                b = sumB / found;
                return true;
            }

            r = 0f;
            g = 0f;
            b = 0f;
            return false;
        }

        /// <summary>Whether one raw linear pixel is one of the flat cyan water quads - see
        /// <see cref="WaterCyanChannelFloor"/>.</summary>
        /// <param name="r">Linear red.</param>
        /// <param name="g">Linear green.</param>
        /// <param name="b">Linear blue.</param>
        private static bool IsWaterCyan(float r, float g, float b) =>
            g > WaterCyanChannelFloor &&
            b > WaterCyanChannelFloor &&
            r < WaterCyanRedShare * Math.Min(g, b);

        // --- edge-preserving smoothing -----------------------------------------------------------

        /// <summary>The spatial half of the bilateral kernel: a Gaussian of
        /// <see cref="SmoothingSigmaSpatial"/> over the window, unnormalised, since the filter divides
        /// by the weight it actually used.</summary>
        private static float[] BuildSmoothingKernel()
        {
            var side = SmoothingRadius * 2 + 1;
            var kernel = new float[side * side];

            for (var dy = -SmoothingRadius; dy <= SmoothingRadius; dy++)
            {
                for (var dx = -SmoothingRadius; dx <= SmoothingRadius; dx++)
                {
                    kernel[(dy + SmoothingRadius) * side + (dx + SmoothingRadius)] = (float)Math.Exp(
                        -(dx * dx + dy * dy) / (2d * SmoothingSigmaSpatial * SmoothingSigmaSpatial));
                }
            }

            return kernel;
        }

        /// <summary>The range half, as a table: exp(-d^2 / 2 sigma^2) sampled over the reach.</summary>
        private static float[] BuildSmoothingRangeWeights()
        {
            var weights = new float[SmoothingRangeSteps];

            for (var i = 0; i < SmoothingRangeSteps; i++)
            {
                var d = SmoothingRangeCut * i / (SmoothingRangeSteps - 1);
                weights[i] = (float)Math.Exp(-(d * d) / (2d * SmoothingSigmaRange * SmoothingSigmaRange));
            }

            return weights;
        }

        /// <summary>
        /// One pixel through the 5x5 bilateral filter, in linear light, reading the floor's own
        /// unmodified buffer.
        ///
        /// A bilateral filter is a blur whose weights fall off with BRIGHTNESS DIFFERENCE as well as
        /// distance, which is what lets it take the speckle out of a field of grass and leave the edge
        /// of the warehouse beside it exactly where it was. The difference is measured on the STRETCHED
        /// luminance - the 0..1 scale the grade works in, precomputed for the band - so a dark map and
        /// a bright one are smoothed by the same amount rather than by whatever their raw values
        /// happen to be.
        ///
        /// Undrawn neighbours are skipped, so a hole neither bleeds into the picture nor pulls its edge
        /// toward black; a pixel that finds no usable neighbour at all keeps its own value.
        ///
        /// No copy of the buffer and no halo bookkeeping: the whole float buffer is in memory and this
        /// only ever READS it, writing its result into the band's Color32 block by way of
        /// <see cref="Grade"/>. That is the one design decision here worth stating - the alternative
        /// was a second float buffer - 116 MB on Customs at 0.25 m/px, 463 MB at 0.125 m/px - on top of a
        /// merge that already peaks near the whole budget.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed, for its pixels, its drawn mask and the band's
        /// luminance.</param>
        /// <param name="lumFrom">The first picture row the band's luminance buffer covers.</param>
        /// <param name="row">The pixel's row, counting from the bottom.</param>
        /// <param name="col">The pixel's column.</param>
        /// <param name="r">The filtered red.</param>
        /// <param name="g">The filtered green.</param>
        /// <param name="b">The filtered blue.</param>
        private static void Smooth(
            Plan plan, FloorPlan floor, int lumFrom, int row, int col,
            out float r, out float g, out float b)
        {
            var pixels = floor.Pixels;
            var drawn = floor.Drawn;
            var lum = floor.LumBand;
            var width = plan.WidthPx;
            var side = SmoothingRadius * 2 + 1;

            var centre = lum[(row - lumFrom) * width + col];

            var rowFrom = Math.Max(0, row - SmoothingRadius);
            var rowUntil = Math.Min(plan.HeightPx - 1, row + SmoothingRadius);
            var colFrom = Math.Max(0, col - SmoothingRadius);
            var colUntil = Math.Min(width - 1, col + SmoothingRadius);

            var sumR = 0f;
            var sumG = 0f;
            var sumB = 0f;
            var sumW = 0f;

            for (var y = rowFrom; y <= rowUntil; y++)
            {
                var pixelRow = y * width;
                var lumRow = (y - lumFrom) * width;
                var kernelRow = (y - row + SmoothingRadius) * side;

                for (var x = colFrom; x <= colUntil; x++)
                {
                    var other = pixelRow + x;
                    if (!drawn[other]) continue;

                    var difference = lum[lumRow + x] - centre;
                    if (difference < 0f) difference = -difference;
                    if (difference >= SmoothingRangeCut) continue;

                    // Clamped, although the guard above should already have made it impossible: this is
                    // the ONE float-to-int cast in the whole develop path that indexes an array, the
                    // guard above is a comparison and comparisons are false for a NaN, and Mono casts a
                    // NaN to int.MinValue. Two lines here against a capture that throws away a floor.
                    var step = (int)(difference * SmoothingRangeScale);
                    if (step < 0) step = 0;
                    else if (step >= SmoothingRangeWeights.Length) step = SmoothingRangeWeights.Length - 1;

                    var weight =
                        SmoothingKernel[kernelRow + (x - col + SmoothingRadius)] *
                        SmoothingRangeWeights[step];

                    var at = other * 3;
                    sumR += pixels[at] * weight;
                    sumG += pixels[at + 1] * weight;
                    sumB += pixels[at + 2] * weight;
                    sumW += weight;
                }
            }

            var here = (row * width + col) * 3;

            if (sumW <= 0f)
            {
                r = pixels[here];
                g = pixels[here + 1];
                b = pixels[here + 2];
                return;
            }

            var inverse = 1f / sumW;
            r = sumR * inverse;
            g = sumG * inverse;
            b = sumB * inverse;
        }

        /// <summary>Fills the band's stretched-luminance buffer, including the two rows of halo above
        /// and below that the filter reads. Returns the first picture row it covers, which is what
        /// indexes it.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed.</param>
        /// <param name="y0">The band's first row.</param>
        /// <param name="rows">How many rows the band has.</param>
        /// <param name="low">The exposure's low percentile.</param>
        /// <param name="scale">1 / (high - low).</param>
        private static int FillLuminance(Plan plan, FloorPlan floor, int y0, int rows, float low, float scale)
        {
            var from = Math.Max(0, y0 - SmoothingRadius);
            var until = Math.Min(plan.HeightPx - 1, y0 + rows - 1 + SmoothingRadius);

            var pixels = floor.Pixels;
            var lum = floor.LumBand;
            var width = plan.WidthPx;

            for (var y = from; y <= until; y++)
            {
                var pixelRow = y * width;
                var lumRow = (y - from) * width;

                for (var x = 0; x < width; x++)
                {
                    var at = (pixelRow + x) * 3;
                    var value = (Luminance(pixels[at], pixels[at + 1], pixels[at + 2]) - low) * scale;

                    // IsFinite FIRST, because the plain clamp passes a NaN straight through - a NaN is
                    // neither less than 0 nor greater than 1, so both arms are false and it is stored.
                    // That is how one bad pixel used to reach Smooth's lookup table. See RenderTile.
                    lum[lumRow + x] = !IsFinite(value) ? 0f : value < 0f ? 0f : value > 1f ? 1f : value;
                }
            }

            return from;
        }

        /// <summary>
        /// Replaces a lone outlier pixel with the median of its eight neighbours, after the bilateral
        /// filter has run.
        ///
        /// It is a separate pass because a bilateral filter CANNOT do this: the range weight is what
        /// makes it preserve an edge, and a single pixel unlike everything around it looks exactly like
        /// an edge to it, so every speckle survives the smoothing untouched. What is left after that
        /// pass is single bright or black pixels - a specular glint on wet metal, a lamp seen end-on,
        /// one sample of sky through a gap in a roof - and at a quarter of a metre to the pixel there
        /// were thousands of them on a map (more at an eighth).
        ///
        /// Three conditions, all of which have to hold, so that the pass cannot eat real content:
        ///   - at least <see cref="DespeckleMinNeighbours"/> of the eight neighbours were drawn, or
        ///     there is not enough around this pixel to judge it by (the edge of a hole, the edge of
        ///     the picture);
        ///   - those neighbours agree among themselves within <see cref="DespeckleNeighbourSpread"/>,
        ///     which at a real edge, corner or thin line they never do;
        ///   - and this pixel differs from their median by more than <see cref="DespeckleThreshold"/>.
        /// The replacement is a real neighbour's colour - the one holding the median luminance - rather
        /// than an average, so nothing is invented and a coloured surface keeps its hue.
        ///
        /// Luminance is read from the band's buffer, which holds the pixels as they were BEFORE the
        /// smoothing. That is deliberate: the smoothing leaves an outlier alone, so the unsmoothed
        /// value is the right thing to test, and it costs nothing extra to read.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor being developed, for its pixels, drawn mask, luminance band and
        /// the two reused neighbour buffers.</param>
        /// <param name="lumFrom">The first picture row the band's luminance buffer covers.</param>
        /// <param name="row">The pixel's row, counting from the bottom.</param>
        /// <param name="col">The pixel's column.</param>
        /// <param name="r">The pixel's red, replaced when it is a speckle.</param>
        /// <param name="g">The pixel's green, replaced when it is a speckle.</param>
        /// <param name="b">The pixel's blue, replaced when it is a speckle.</param>
        private static void Despeckle(
            Plan plan, FloorPlan floor, int lumFrom, int row, int col,
            ref float r, ref float g, ref float b)
        {
            var width = plan.WidthPx;
            var lum = floor.LumBand;
            var drawn = floor.Drawn;
            var values = floor.NeighbourLum;
            var indices = floor.NeighbourIndex;

            var centre = lum[(row - lumFrom) * width + col];
            var count = 0;

            for (var dy = -1; dy <= 1; dy++)
            {
                var y = row + dy;
                if (y < 0 || y >= plan.HeightPx) continue;

                var pixelRow = y * width;
                var lumRow = (y - lumFrom) * width;

                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;

                    var x = col + dx;
                    if (x < 0 || x >= width) continue;

                    var other = pixelRow + x;
                    if (!drawn[other]) continue;

                    // Insertion sort as they are collected: eight items at most, so this is cheaper
                    // than sorting afterwards and it keeps each value beside the pixel it came from.
                    var value = lum[lumRow + x];
                    var at = count;

                    while (at > 0 && values[at - 1] > value)
                    {
                        values[at] = values[at - 1];
                        indices[at] = indices[at - 1];
                        at--;
                    }

                    values[at] = value;
                    indices[at] = other;
                    count++;
                }
            }

            if (count < DespeckleMinNeighbours) return;
            if (values[count - 1] - values[0] > DespeckleNeighbourSpread) return;

            // The lower middle of an even count, which is a real pixel rather than an average of two.
            var middle = count / 2;
            var difference = centre - values[middle];
            if (difference < 0f) difference = -difference;
            if (difference <= DespeckleThreshold) return;

            var source = indices[middle] * 3;
            r = floor.Pixels[source];
            g = floor.Pixels[source + 1];
            b = floor.Pixels[source + 2];
            floor.Despeckled++;
        }

        // --- WP1: the shared arithmetic of the merge, and the tile plan built on it ------------------------

        /// <summary>Every column's squared X distance from the capturing player, in DevelopBand's float arithmetic
        /// (DevelopBegin's buffer; the tile plan builds the same one).</summary>
        /// <param name="plan">The capture's plan.</param>
        private static float[] BuildDxSquared(Plan plan)
        {
            var dx2 = new float[plan.WidthPx];

            for (var col = 0; col < plan.WidthPx; col++)
            {
                var dx = (float)(plan.Extent.MinX + (col + 0.5d) / plan.Ppm) - plan.From.x;
                dx2[col] = dx * dx;
            }

            return dx2;
        }

        /// <summary>One texture row's squared Z distance from the capturing player, in DevelopBand's float
        /// arithmetic. Texture row 0 is the BOTTOM of the picture, which is the extent's -z edge; image row 0 is its
        /// top - see RenderTile for the same conversion.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="textureRow">The row, 0 at the bottom.</param>
        private static float RowDzSquared(Plan plan, int textureRow)
        {
            var worldZ = (float)(plan.Extent.MaxZ - (plan.HeightPx - 1 - textureRow + 0.5d) / plan.Ppm);
            var dz = worldZ - plan.From.y;
            return dz * dz;
        }

        /// <summary>The step this capture writes for a DRAWN pixel - DevelopBand's distance term. THE HOOK for a
        /// side view: its pixels are not at (x, z) = (column, row), so its distance is to the ground point the pixel
        /// looks at (SideSteps); a side's plan carries its SideView, a floor's does not.</summary>
        /// <param name="plan">The capture's plan (a side's own plan for a side).</param>
        /// <param name="dxSquared">BuildDxSquared's buffer; unused for a side.</param>
        /// <param name="dzSquared">RowDzSquared of the row; unused for a side.</param>
        /// <param name="col">The column.</param>
        /// <param name="textureRow">The row, 0 at the bottom.</param>
        private static byte NewStep(Plan plan, float[] dxSquared, float dzSquared, int col, int textureRow) =>
            StepZero(plan) ? (byte)0 :
            plan.Side != null ? SideSteps(plan, col, textureRow) : Steps(Mathf.Sqrt(dxSquared[col] + dzSquared));

        /// <summary>Stage M2 rollback: false has a menu capture record real distances - from the world origin, since
        /// there is no player (CapturePoint) - instead of step 0.</summary>
        private static readonly bool MenuStepZero = true;

        /// <summary>Stage M2: whether every pixel this plan writes records step 0 - a menu capture, which saw the whole
        /// map loaded with nothing streamed out, so no raid capture can see a pixel better. Step 0 is unbeatable
        /// (CaptureMerge.Takes is strictly less), so a later raid capture never overwrites a menu pixel, and a side's
        /// empty pixel settles at 0 as well. A raid plan never has MenuMode, so its steps are exactly as before.</summary>
        /// <param name="plan">The capture's plan, or a side's own plan (which copies MenuMode).</param>
        private static bool StepZero(Plan plan) => MenuStepZero && plan != null && plan.MenuMode;

        /// <summary>What the picture on disk has at one pixel: its recorded step, its colour, and whether it has a
        /// pixel there at all - the SIDECAR is the authority whenever there is one, the colour test only for a
        /// picture written before sidecars existed. DevelopBand's rule; the tile plan asks the same method.</summary>
        /// <param name="previous">The previous picture, or null.</param>
        /// <param name="previousDist">Its sidecar, or null.</param>
        /// <param name="index">The pixel.</param>
        /// <param name="oldDrawn">Whether the picture on disk has a pixel there.</param>
        /// <param name="oldDistance">Its recorded step, or DistanceEmpty.</param>
        /// <param name="oldPixel">Its colour, or default.</param>
        private static void OldState(Color32[] previous, byte[] previousDist, int index,
            out bool oldDrawn, out byte oldDistance, out Color32 oldPixel)
        {
            oldDistance = previousDist != null && previous != null ? previousDist[index] : DistanceEmpty;
            oldPixel = previous != null ? previous[index] : default(Color32);
            oldDrawn = previous != null && (previousDist != null
                ? oldDistance != DistanceEmpty
                : oldPixel.r > 0 || oldPixel.g > 0 || oldPixel.b > 0);
        }

        /// <summary>A tile's rect in OUTPUT pixels: columns col0..col0+cols-1 and texture rows row0..row0+rows-1
        /// (row 0 = bottom). contract=true gives a side's rect in the contract's (mirrored) columns, which is the
        /// orientation of its previous picture, its sidecar and SideSteps; false gives the camera's, which is the
        /// orientation of the float buffer until MirrorSide. Floors are the same either way. RenderTile's tile
        /// arithmetic in closed form.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="tile">The tile index, row-major from the top-left.</param>
        /// <param name="contract">Contract (true) or camera (false) columns; floors ignore it.</param>
        /// <param name="col0">First column.</param>
        /// <param name="cols">Column count.</param>
        /// <param name="row0">First texture row.</param>
        /// <param name="rows">Row count.</param>
        private static void TileRect(Plan plan, int tile, bool contract, out int col0, out int cols, out int row0, out int rows)
        {
            var px0 = tile % plan.TilesX * TileSize;
            var py0 = tile / plan.TilesX * TileSize;
            var tw = Math.Max(0, Math.Min(TileSize, plan.SampleWidth - px0));
            var th = Math.Max(0, Math.Min(TileSize, plan.SampleHeight - py0));

            cols = tw / SupersampleFactor;
            rows = th / SupersampleFactor;
            row0 = (plan.SampleHeight - py0 - th) / SupersampleFactor;

            var cameraCol0 = px0 / SupersampleFactor;
            col0 = contract && plan.Side != null ? plan.WidthPx - cameraCol0 - cols : cameraCol0;
        }

        /// <summary>A rect grown by a number of pixels on every side (Chebyshev) and clipped to the picture, as the
        /// half-open ranges a0..a1-1 and b0..b1-1.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="col0">First column.</param>
        /// <param name="cols">Column count.</param>
        /// <param name="row0">First row.</param>
        /// <param name="rows">Row count.</param>
        /// <param name="by">Pixels to grow by.</param>
        /// <param name="a0">First column of the result.</param>
        /// <param name="a1">One past its last column.</param>
        /// <param name="b0">First row of the result.</param>
        /// <param name="b1">One past its last row.</param>
        private static void Dilate(Plan plan, int col0, int cols, int row0, int rows, int by,
            out int a0, out int a1, out int b0, out int b1)
        {
            a0 = Math.Max(0, col0 - by);
            a1 = Math.Min(plan.WidthPx, col0 + cols + by);
            b0 = Math.Max(0, row0 - by);
            b1 = Math.Min(plan.HeightPx, row0 + rows + by);
        }

        /// <summary>Per tile, the largest previous step over the tile and its halo (255 = something there is
        /// undrawn). One pass over the sidecar's bytes, about W*H reads; the bound in TileWorth needs nothing
        /// else.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side, with its previous picture and sidecar loaded.</param>
        private static void SummariseTiles(Plan plan, FloorPlan floor)
        {
            floor.TileMaxOld = null;

            var dist = floor.PreviousDist;
            if (dist == null || floor.PreviousColour == null) return;

            var max = new byte[plan.TileCount];

            for (var tile = 0; tile < plan.TileCount; tile++)
            {
                TileRect(plan, tile, true, out var c0, out var n, out var r0, out var m);
                Dilate(plan, c0, n, r0, m, TileSkipHalo, out var a0, out var a1, out var b0, out var b1);

                byte best = 0;

                for (var row = b0; row < b1 && best != DistanceEmpty; row++)
                {
                    var i = row * plan.WidthPx + a0;

                    for (var col = a0; col < a1; col++, i++)
                    {
                        var v = dist[i];
                        if (v <= best) continue;

                        best = v;
                        if (v == DistanceEmpty) break;
                    }
                }

                max[tile] = best;
            }

            floor.TileMaxOld = max;
        }

        /// <summary>A lower bound of NewStep over every pixel centre of cols a0..a1-1, rows b0..b1-1 (contract
        /// orientation). Floors: the closed-form distance from plan.From to the rectangle of pixel centres. Sides:
        /// GroundPointOf is affine, so the rect's image is the parallelogram of its four corner centres, inside
        /// their XZ bounding box - the distance to that box is no larger than to any ground point. The slack covers
        /// float rounding; Steps is monotone non-decreasing, so the result is at most every pixel's step.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="a0">First column.</param>
        /// <param name="a1">One past the last column.</param>
        /// <param name="b0">First texture row.</param>
        /// <param name="b1">One past the last row.</param>
        private static byte LowerStep(Plan plan, int a0, int a1, int b0, int b1)
        {
            // Stage M2: every step a menu capture writes is 0, so 0 is also their lower bound - the tile skip then keeps
            // a tile only where the disk already holds step 0 everywhere (an earlier menu capture).
            if (StepZero(plan)) return 0;

            double xLo, xHi, zLo, zHi;

            if (plan.Side == null)
            {
                xLo = plan.Extent.MinX + (a0 + 0.5d) / plan.Ppm;
                xHi = plan.Extent.MinX + (a1 - 1 + 0.5d) / plan.Ppm;
                zLo = plan.Extent.MaxZ - (plan.HeightPx - 1 - b0 + 0.5d) / plan.Ppm;
                zHi = plan.Extent.MaxZ - (plan.HeightPx - 1 - (b1 - 1) + 0.5d) / plan.Ppm;
            }
            else
            {
                xLo = zLo = double.PositiveInfinity;
                xHi = zHi = double.NegativeInfinity;

                var v = plan.Side;

                foreach (var c in new[] { a0, a1 - 1 })
                foreach (var r in new[] { b0, b1 - 1 })
                {
                    MapSideView.GroundPointOf(v.Right, v.Up, v.Frame[0], v.Frame[2], plan.Ppm, plan.HeightPx, v.YMin,
                        c + 0.5d, plan.HeightPx - r - 0.5d, out var x, out var z);

                    xLo = Math.Min(xLo, x);
                    xHi = Math.Max(xHi, x);
                    zLo = Math.Min(zLo, z);
                    zHi = Math.Max(zHi, z);
                }
            }

            var dx = Math.Max(0d, Math.Max(xLo - plan.From.x, plan.From.x - xHi));
            var dz = Math.Max(0d, Math.Max(zLo - plan.From.y, plan.From.y - zHi));

            return Steps((float)Math.Max(0d, Math.Sqrt(dx * dx + dz * dz) - TileSkipSlackMetres));
        }

        /// <summary>
        /// Whether a tile has to be rendered. The rule is the merge's own (CaptureMerge.Takes, as DevelopBand calls
        /// it), asked of every pixel of the tile and of the TileSkipHalo pixels around it that the development reads
        /// for a pixel it takes, assuming this capture would DRAW every one of them - the one thing it cannot know
        /// without rendering, and the assumption that makes "not takeable" safe:
        ///   Render        - some pixel could be taken and could be visible;
        ///   OwnedByCloser - no pixel could be taken: the picture on disk has each from as close or closer;
        ///   OutsideMask   - pixels could be taken, but each is transparent in the result whichever capture
        ///                   supplies it (walkable weight 0 here, alpha 0 on disk). Only with TileSkipOutsideMask
        ///                   and a walkable mask.
        /// Always Render on a fresh capture or when the previous picture did not load.
        /// </summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side, its previous picture loaded.</param>
        /// <param name="tile">The tile.</param>
        private static TileVerdict TileWorth(Plan plan, FloorPlan floor, int tile)
        {
            var previous = floor.PreviousColour;
            if (!TileSkipEnabled || plan.Previous == null || previous == null) return TileVerdict.Render;

            TileRect(plan, tile, true, out var col0, out var cols, out var row0, out var rows);
            if (cols <= 0 || rows <= 0) return TileVerdict.Render;

            Dilate(plan, col0, cols, row0, rows, TileSkipHalo, out var a0, out var a1, out var b0, out var b1);

            var previousDist = floor.PreviousDist;

            // 1. The bound: every step this capture could write here is at least the largest step on disk, and
            //    nothing on disk here is undrawn - so Takes is false for every pixel.
            if (previousDist != null && floor.TileMaxOld != null)
            {
                var maxOld = floor.TileMaxOld[tile];
                if (maxOld != DistanceEmpty && LowerStep(plan, a0, a1, b0, b1) >= maxOld) return TileVerdict.OwnedByCloser;
            }

            // 2. Exact: DevelopBand's own distance and Takes, pixel by pixel, stopping at the first visible one.
            var masked = TileSkipOutsideMask && plan.Reach != null;
            var anyTakeable = false;

            for (var row = b0; row < b1; row++)
            {
                var dzSquared = plan.Side == null ? RowDzSquared(plan, row) : 0f;
                var index = row * plan.WidthPx + a0;

                for (var col = a0; col < a1; col++, index++)
                {
                    OldState(previous, previousDist, index, out var oldDrawn, out var oldDistance, out var oldPixel);
                    var distance = NewStep(plan, floor.DxSquared, dzSquared, col, row);
                    if (!CaptureMerge.Takes(true, distance, oldDrawn, oldDistance)) continue;

                    if (!masked) return TileVerdict.Render;
                    anyTakeable = true;

                    // Visible if taken: Grade's alpha is 0 only at reach 0 or below; kept, it would be the old alpha.
                    if (oldPixel.a > 0 || ReachAt(plan, col, row) > 0f) return TileVerdict.Render;
                }
            }

            return anyTakeable ? TileVerdict.OutsideMask : TileVerdict.OwnedByCloser;
        }

        /// <summary>The tile plan of one floor or side: the verdict of every tile, time-sliced at TilePlanSliceMs,
        /// every step guarded so an exception gives "render everything". Needs the previous picture and sidecar
        /// loaded; leaves Verdicts null (render every tile) on a fresh capture, with the switch off, or when the
        /// previous picture did not load.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private IEnumerator PlanTiles(Plan plan, FloorPlan floor)
        {
            floor.Verdicts = null;
            if (!TileSkipEnabled || plan.Previous == null || floor.PreviousColour == null) yield break;

            var clock = Stopwatch.StartNew();

            if (!PlanStep(plan, floor, () =>
                {
                    if (plan.Side == null) floor.DxSquared = BuildDxSquared(plan);
                    SummariseTiles(plan, floor);
                }))
                yield break;

            var verdicts = new TileVerdict[plan.TileCount];
            var slice = Stopwatch.StartNew();

            for (var tile = 0; tile < plan.TileCount; tile++)
            {
                var t = tile;
                if (!PlanStep(plan, floor, () => verdicts[t] = TileWorth(plan, floor, t))) yield break;

                if (slice.Elapsed.TotalMilliseconds > TilePlanSliceMs)
                {
                    yield return null;
                    slice.Restart();
                }
            }

            floor.Verdicts = verdicts;
            floor.TilesOwned = verdicts.Count(v => v == TileVerdict.OwnedByCloser);
            floor.TilesOutside = verdicts.Count(v => v == TileVerdict.OutsideMask);

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tile plan: {plan.TileCount - floor.TilesOwned - floor.TilesOutside} " +
                $"of {plan.TileCount} to render, {floor.TilesOwned} owned by closer captures [{TileList(verdicts, TileVerdict.OwnedByCloser)}], " +
                $"{floor.TilesOutside} outside the walkable mask [{TileList(verdicts, TileVerdict.OutsideMask)}], planned in " +
                $"{Ms(clock.Elapsed.TotalMilliseconds)} ms.");
        }

        /// <summary>One guarded planning step; false (having logged at Debug and left Verdicts null, which renders
        /// every tile) when it threw.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side.</param>
        /// <param name="step">The step.</param>
        private static bool PlanStep(Plan plan, FloorPlan floor, Action step)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception ex)
            {
                floor.Verdicts = null;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tile plan abandoned ({ex.GetType().Name}: " +
                    $"{ex.Message}) - every tile is rendered.");
                return false;
            }
        }

        /// <summary>The indices of the tiles with one verdict, comma-separated - what the offline compare tool
        /// (tools/compare-captures.py --skipped) is given.</summary>
        /// <param name="v">The verdicts.</param>
        /// <param name="which">The verdict to list.</param>
        private static string TileList(TileVerdict[] v, TileVerdict which) =>
            string.Join(",", Enumerable.Range(0, v.Length).Where(i => v[i] == which)
                .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());

        /// <summary>THE tile decision: whether the loop renders this tile. True in audit mode, with no plan, and
        /// for a Render verdict (a water promotion sets one).</summary>
        /// <param name="floor">The floor or side.</param>
        /// <param name="tile">The tile.</param>
        private static bool Renders(FloorPlan floor, int tile) =>
            TileSkipAudit || floor.Verdicts == null || floor.Verdicts[tile] == TileVerdict.Render;

        /// <summary>The tiles the first pass renders, in index order - the one list the tile loop walks (an
        /// asynchronous readback pipeline consumes the same list, then the water rule's late ones).</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side.</param>
        private static IEnumerable<int> TilesToRender(Plan plan, FloorPlan floor) =>
            Enumerable.Range(0, plan.TileCount).Where(t => Renders(floor, t));

        /// <summary>WP1: the previous picture and sidecar BEFORE the tiles, when there is one to merge into, and the
        /// tile plan made from them. The tile plan is the merge's own question and needs both; they were loaded in
        /// Develop until now - same files, same calls, one frame each - and the memory model
        /// (WorkingSetBytesPerPixel) already counts both. Nothing with the switch off or on a fresh capture: the
        /// loads then stay in Develop and every tile renders.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side, its buffers allocated.</param>
        private IEnumerator LoadAndPlan(Plan plan, FloorPlan floor)
        {
            if (!TileSkipEnabled || plan.Previous == null) yield break;

            yield return null;
            LoadPreviousColour(plan, floor);

            if (floor.PreviousColour != null)
            {
                yield return null;
                LoadPreviousDist(plan, floor);
                HealSideDist(plan, floor);
            }

            floor.PreviousLoaded = true;

            var planning = PlanTiles(plan, floor);
            while (planning.MoveNext()) yield return planning.Current;
        }

        /// <summary>
        /// WP1: THE tile loop of one floor or side - the one place the renders are iterated. The scene is held only
        /// when something will be rendered (a floor every tile of which is owned costs no pass over the culling
        /// lists at all); then the first pass over TilesToRender, one render and one readback per frame, a skipped
        /// tile costing no render and no frame; then the water rule (PromoteNearWater), still under the hold, whose
        /// tiles are rendered late until it finds none; then the release. With no plan every tile renders under one
        /// hold, exactly the old loop.
        ///
        /// WP4 A2: both lists go through RenderPass - the asynchronous readback ring when there is one, RenderTile
        /// otherwise - which drains before it returns, so the water rule finds every first-pass tile folded into Pixels
        /// and Drawn, and a failed readback is re-rendered synchronously still under this hold. The per-floor readback
        /// Debug line (ReadbackLine) is written before the release.
        /// </summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        /// <param name="overrun">Asked before every render: true when the phase budget is past its margin (it
        /// counts the cut itself); the floor is then failed and the loop stops.</param>
        private IEnumerator RenderTiles(Plan plan, FloorPlan floor, Func<bool> overrun)
        {
            // The scene's distance culling forced visible and its water hidden for the whole floor, rather than
            // around each render: see ForceCulling for the measurement that decides it and for what the player's own
            // frames look like meanwhile. Never throws, and the release below runs however the tiles went -
            // including a floor abandoned at its first tile - while Cleanup releases it again for a raid that ends
            // mid-floor.
            var held = false;

            if (TilesToRender(plan, floor).Any())
            {
                HoldScene();
                held = true;

                // Its own frame, like every other step here: the hold is the one pass over all twenty-seven thousand
                // components, and putting it in the same frame as the first tile's render and readback would make
                // that frame the longest of the capture.
                yield return null;
            }

            // WP4 A2: the per-floor readback numbers start here.
            _floorAsync = _floorWaits = _floorErrors = _floorAveraged = _floorMismatches = 0;
            _floorWaitMs = _floorAverageMs = 0d;

            // The first pass - one render a frame, through the readback ring when there is one (RenderPass), drained
            // before the water rule reads the pixels and before the release.
            var first = RenderPass(plan, floor, TilesToRender(plan, floor).ToList(), overrun);
            while (first.MoveNext()) yield return first.Current;

            // The water rule (WP1 2.6), still under the hold. In audit mode it only reports, into the skip zones.
            if (!floor.Failed && floor.Verdicts != null)
            {
                if (TileSkipAudit)
                {
                    try
                    {
                        BuildSkipZone(plan, floor, PromoteNearWater(plan, floor, true));
                    }
                    catch (Exception ex)
                    {
                        floor.SkipZone = null;
                        floor.AuditVerdicts = null;
                        Plugin.LogSource?.LogDebug(
                            $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tile-skip audit abandoned ({ex.GetType().Name}: " +
                            $"{ex.Message}).");
                    }
                }
                else
                {
                    while (!floor.Failed)
                    {
                        var late = WaterTiles(plan, floor);
                        if (late.Count == 0) break;

                        // Unreachable in practice - water needs a rendered tile - but a late render is never unheld.
                        if (!held)
                        {
                            HoldScene();
                            held = true;
                            yield return null;
                        }

                        // The late tiles through the same pass, drained before the rule looks again.
                        var pass = RenderPass(plan, floor, late, overrun);
                        while (pass.MoveNext()) yield return pass.Current;
                    }
                }
            }

            ReadbackLine(plan, floor);

            if (held) ReleaseScene();
        }

        /// <summary>WP1: PromoteNearWater, guarded - when the ring scan throws, every skipped tile is promoted, which
        /// is the old output.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side.</param>
        private static List<int> WaterTiles(Plan plan, FloorPlan floor)
        {
            try
            {
                return PromoteNearWater(plan, floor, false);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" water rule abandoned ({ex.GetType().Name}: {ex.Message}) - " +
                    "every skipped tile is rendered.");

                var all = new List<int>();

                for (var tile = 0; tile < plan.TileCount; tile++)
                {
                    if (floor.Verdicts[tile] == TileVerdict.Render) continue;

                    if (floor.Verdicts[tile] == TileVerdict.OwnedByCloser) floor.TilesOwned--;
                    else floor.TilesOutside--;

                    floor.Verdicts[tile] = TileVerdict.Render;
                    floor.TilesPromoted++;
                    all.Add(tile);
                }

                return all;
            }
        }

        /// <summary>
        /// WP1 (2.6): the skipped tiles that must be rendered after all because a drawn water pixel lies within
        /// InpaintReach of their edge: its fill (Mean) could read their pixels, and a filled pixel is read in turn by
        /// the smoothing of a pixel this capture takes. Checked on the float buffer in its CURRENT orientation (a
        /// side's camera orientation - this runs before MirrorSide), in the ring around each skipped tile only.
        /// Marks them Render and returns them (report=false); called until it returns none, because a tile rendered
        /// late can bring water of its own near another. report=true (audit) only returns them.
        /// </summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side, its first-pass tiles rendered.</param>
        /// <param name="report">Only report, change nothing.</param>
        private static List<int> PromoteNearWater(Plan plan, FloorPlan floor, bool report)
        {
            var promoted = new List<int>();
            if (!FillWaterCyan || floor.Verdicts == null || floor.Pixels == null || floor.Drawn == null) return promoted;

            for (var tile = 0; tile < plan.TileCount; tile++)
            {
                if (floor.Verdicts[tile] == TileVerdict.Render) continue;

                TileRect(plan, tile, false, out var c0, out var n, out var r0, out var m);
                Dilate(plan, c0, n, r0, m, InpaintReach, out var a0, out var a1, out var b0, out var b1);

                var found = false;

                for (var row = b0; row < b1 && !found; row++)
                {
                    var inside = row >= r0 && row < r0 + m;

                    for (var col = a0; col < a1; col++)
                    {
                        // The tile itself is skipped over: its own pixels are not drawn.
                        if (inside && col >= c0 && col < c0 + n)
                        {
                            col = c0 + n - 1;
                            continue;
                        }

                        var i = row * plan.WidthPx + col;
                        if (!floor.Drawn[i]) continue;

                        var at = i * 3;
                        if (!IsWaterCyan(floor.Pixels[at], floor.Pixels[at + 1], floor.Pixels[at + 2])) continue;

                        found = true;
                        break;
                    }
                }

                if (found) promoted.Add(tile);
            }

            if (!report)
            {
                foreach (var tile in promoted)
                {
                    if (floor.Verdicts[tile] == TileVerdict.OwnedByCloser) floor.TilesOwned--;
                    else floor.TilesOutside--;

                    floor.Verdicts[tile] = TileVerdict.Render;
                    floor.TilesPromoted++;
                }
            }

            return promoted;
        }

        /// <summary>WP1 audit mode (2.11): the verdicts with the would-be water promotions set to Render
        /// (AuditVerdicts), and SkipZone - per pixel, bit 1 over every owned tile dilated by TileSkipHalo, bit 2 over
        /// every outside one - in the contract's orientation, which is the one DevelopBand indexes. 1 byte a pixel,
        /// audit mode only.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor or side.</param>
        /// <param name="wouldPromote">The tiles the water rule would have rendered late.</param>
        private static void BuildSkipZone(Plan plan, FloorPlan floor, List<int> wouldPromote)
        {
            floor.SkipZone = null;
            floor.AuditVerdicts = null;
            if (floor.Verdicts == null) return;

            var verdicts = (TileVerdict[])floor.Verdicts.Clone();
            foreach (var tile in wouldPromote) verdicts[tile] = TileVerdict.Render;

            var zone = new byte[plan.WidthPx * plan.HeightPx];

            for (var tile = 0; tile < plan.TileCount; tile++)
            {
                if (verdicts[tile] == TileVerdict.Render) continue;

                var bit = verdicts[tile] == TileVerdict.OwnedByCloser ? (byte)1 : (byte)2;

                TileRect(plan, tile, true, out var c0, out var n, out var r0, out var m);
                Dilate(plan, c0, n, r0, m, TileSkipHalo, out var a0, out var a1, out var b0, out var b1);

                for (var row = b0; row < b1; row++)
                {
                    var i = row * plan.WidthPx + a0;
                    for (var col = a0; col < a1; col++, i++) zone[i] |= bit;
                }
            }

            floor.SkipZone = zone;
            floor.AuditVerdicts = verdicts;
        }

        /// <summary>WP1 audit mode (2.11): the light test judged twice - on every tile (what the capture uses, since
        /// audit renders every tile) and on the tiles the skip would have rendered - and one line with both verdicts,
        /// a Warning when they differ.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The floor.</param>
        /// <param name="stored">The stored exposure.</param>
        /// <param name="measured">Measure over every tile.</param>
        private void AuditLight(Plan plan, FloorPlan floor, ExposureResult stored, ExposureResult measured)
        {
            try
            {
                if (floor.AuditVerdicts == null) return;

                var subset = Measure(plan, floor, out _, floor.AuditVerdicts, true);

                var all = LightVerdict(measured, stored, out var allHigh, out var allDrift);
                var rendered = LightVerdict(subset, stored, out var subHigh, out var subDrift);

                var line =
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tile-skip audit, light test: all tiles p98 {allHigh} " +
                    $"(drift {allDrift} %) -> {all}; rendered tiles only p98 {subHigh} (drift {subDrift} %) -> {rendered}";

                if (all == rendered) Plugin.LogSource?.LogInfo(line + ".");
                else Plugin.LogSource?.LogWarning(line + " - the verdicts DIFFER.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" tile-skip audit of the light test abandoned " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>WP1 audit: what MeasureFloor would decide from one measurement - accept, refuse (drift past
        /// MaxExposureDrift), or fail (no usable picture: empty, too dark, or flat).</summary>
        /// <param name="measured">The measurement, or null.</param>
        /// <param name="stored">The stored exposure.</param>
        /// <param name="high">Its p98, for the line.</param>
        /// <param name="drift">Its drift in percent, for the line.</param>
        private static string LightVerdict(ExposureResult measured, ExposureResult stored, out string high, out string drift)
        {
            if (measured == null)
            {
                high = "-";
                drift = "-";
                return "fail (no usable picture)";
            }

            var d = IsFinite(measured.High) && IsFinite(stored.High) && stored.High > 0f
                ? Math.Abs(measured.High / stored.High - 1f)
                : 0f;

            high = E(measured.High);
            drift = (d * 100f).ToString("0", CultureInfo.InvariantCulture);

            return d > MaxExposureDrift ? "refuse" : "accept";
        }

        /// <summary>WP1 audit mode (2.11): the line FinishFloor and FinishSide add - what the skip would have
        /// skipped, and the pixels this capture took inside those tiles' halos. Any of those is a broken premise:
        /// a Warning with INCONSISTENT.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private static void AuditLine(Plan plan, FloorPlan floor)
        {
            if (!TileSkipAudit || floor.AuditVerdicts == null || floor.Verdicts == null) return;

            var owned = floor.AuditVerdicts.Count(v => v == TileVerdict.OwnedByCloser);
            var outside = floor.AuditVerdicts.Count(v => v == TileVerdict.OutsideMask);
            var water = Enumerable.Range(0, plan.TileCount)
                .Count(i => floor.Verdicts[i] != TileVerdict.Render && floor.AuditVerdicts[i] == TileVerdict.Render);

            var line =
                $"QuestTree: tile-skip audit for {plan.Key} \"{floor.Dto?.Name}\" - {owned + outside} of {plan.TileCount} tiles " +
                $"would be skipped ({owned} owned, {outside} outside, {water} re-rendered for water); pixels this capture " +
                $"took inside them: {floor.AuditOwnedTaken} owned-zone, {floor.AuditOutsideVisible} visible outside-zone - ";

            if (floor.AuditOwnedTaken + floor.AuditOutsideVisible > 0) Plugin.LogSource?.LogWarning(line + "INCONSISTENT.");
            else Plugin.LogSource?.LogInfo(line + "consistent.");
        }

        /// <summary>WP1 (2.12): the tiles part of a floor's or side's captured line - how many of the tiles were
        /// rendered and, with a tile plan outside audit mode, why the others were not.</summary>
        /// <param name="plan">The plan (a side's own plan for a side).</param>
        /// <param name="floor">The floor or side.</param>
        private static string TilesPhrase(Plan plan, FloorPlan floor) =>
            floor.Verdicts != null && !TileSkipAudit
                ? $"rendered {floor.Tiles} of {plan.TileCount} tiles (skipped {floor.TilesOwned} owned by closer captures, " +
                  $"{floor.TilesOutside} outside the walkable mask" +
                  (floor.TilesPromoted > 0 ? $", {floor.TilesPromoted} rendered late for water at their edge" : "") + ")"
                : $"rendered {floor.Tiles} of {plan.TileCount} tiles";

        /// <summary>WP1: the tile holding a floor pixel (texture row, 0 at the bottom) - TileRect inverted.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="col">The column.</param>
        /// <param name="row">The texture row.</param>
        private static int TileOf(Plan plan, int col, int row) =>
            (plan.HeightPx - 1 - row) * SupersampleFactor / TileSize * plan.TilesX + col * SupersampleFactor / TileSize;

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
                // RGBA, not RGB: the walkable mask travels as the picture's alpha and EncodeToPNG keeps
                // it. A quarter more memory than the eight-bit RGB it replaces - see the memory note on
                // the class - and the only thing in the pipeline that changes.
                // WP4 B2: on the managed path the bands go into the plan's one pool (every column of every row is written
                // below, so nothing of the previous floor survives), and a worker encodes it - no texture at all.
                if (ManagedPngEncode && !_managedPngOff)
                    floor.Rgba = plan.RgbaPool ?? (plan.RgbaPool = new Color32[plan.WidthPx * plan.HeightPx]);
                else
                    floor.Texture = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGBA32, mipChain: false);

                floor.Block = new Color32[plan.WidthPx * floor.BandRows];

                // One band's stretched luminance plus the filter's halo - 640 KB at 0.25 m/px and 1.3 MB
                // at 0.125, against the 116 / 463 MB a second full float buffer would have cost. See Smooth.
                // Wanted by the smoothing AND by the despeckle, which measures its neighbours on the
                // same stretched luminance.
                // Not for a floor that drew nothing (WP1 2.9): only a taken pixel reads it, and there is none.
                if ((SmoothingEnabled || DespeckleEnabled) && !(floor.Tiles == 0 && floor.PreviousLoaded))
                {
                    floor.LumBand = new float[plan.WidthPx * (floor.BandRows + SmoothingRadius * 2)];
                    floor.NeighbourLum = new float[8];
                    floor.NeighbourIndex = new int[8];
                }

                // The squared X distance of every column from the player, once for the whole floor
                // rather than once per pixel: the per-pixel work is then one add and one square root.
                floor.DxSquared = BuildDxSquared(plan);

                // Campaign speed step 1 (2): which pixels this capture rendered, so an undrawn one there is settled.
                BuildRenderedTiles(plan, floor);

                return true;
            }
            catch (Exception ex)
            {
                floor.Failed = true;
                NoteOutOfMemory(plan, ex);
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

                var rows = Math.Min(floor.BandRows, plan.HeightPx - y0);

                var clock = Stopwatch.StartNew();
                var lumFrom = floor.LumBand != null ? FillLuminance(plan, floor, y0, rows, low, scale) : 0;

                for (var row = 0; row < rows; row++)
                {
                    var textureRow = y0 + row;

                    // Texture row 0 is the BOTTOM of the picture, which is the extent's -z edge;
                    // image row 0 is its top. See RenderTile for the same conversion.
                    var dzSquared = RowDzSquared(plan, textureRow);

                    var pixelRow = textureRow * plan.WidthPx;
                    var target = row * plan.WidthPx;

                    for (var col = 0; col < plan.WidthPx; col++)
                    {
                        var index = pixelRow + col;
                        var at = index * 3;

                        var drawn = floor.Drawn[index];
                        // THE HOOK for a side view: its pixels are not at (x, z) = (column, row), so its
                        // distance is to the ground point the pixel looks at - SideSteps - and everything
                        // else about the merge below is the floors' own code, unchanged. A side's plan
                        // carries its SideView; a floor's does not.
                        var distance = drawn ? NewStep(plan, dxSquared, dzSquared, col, textureRow) : DistanceEmpty;

                        // Campaign speed step 1 (2): rendered by this capture and nothing drawn - the step it was seen
                        // empty from, recorded below only where nothing drawn is on disk (SettleEmpty).
                        var seenEmpty = !drawn && floor.RenderedTile != null && floor.SkyRow != null &&
                                        RenderedAt(plan, floor, col, textureRow) && EmptySettles(floor, index, col, textureRow);

                        // Sampled for EVERY pixel, not only the ones this capture supplies: the mask is a
                        // property of the map, so the share it dims is a fact about the picture rather
                        // than about this capture, and counting it inside the merge would have reported a
                        // third of the truth on a merge that kept two thirds of its pixels.
                        var reach = ReachAt(plan, col, textureRow);
                        if (reach < 1f) floor.Outside++;

                        floor.Dist[index] = distance;

                        // Whether the picture on disk has a pixel here, and the SIDECAR is the
                        // authority whenever there is one. A pixel the grade took to pure black is a
                        // real pixel of real shadow - everything at or below the exposure's low
                        // percentile lands there, which is 2 % of every capture by construction - so
                        // reading black as "nothing drawn" would let a capture taken from 900 m away
                        // overwrite one taken from 50 m, and would throw that pixel's recorded
                        // distance away the first time a later capture happened not to draw it. The
                        // colour test is only for a picture written before sidecars existed, where
                        // black is the only signal there is. OldState is that rule; the tile plan (WP1) asks
                        // the same method.
                        OldState(previous, previousDist, index, out var oldDrawn, out var oldDistance, out var oldPixel);

                        // Take this capture's pixel when it drew one AND either nothing better is
                        // there or it saw the spot from closer. Everything else keeps what was
                        // there, which for a first capture is black.
                        var take = CaptureMerge.Takes(drawn, distance, oldDrawn, oldDistance);

                        // WP1 audit mode: a pixel taken inside the halo of a tile the plan would have skipped is a
                        // broken premise - never, for an owned tile; never visible, for an outside one.
                        if (floor.SkipZone != null && take)
                        {
                            var zone = floor.SkipZone[index];
                            if ((zone & 1) != 0) floor.AuditOwnedTaken++;
                            if ((zone & 2) != 0 && (reach > 0f || oldPixel.a > 0)) floor.AuditOutsideVisible++;
                        }

                        // A pixel nothing has drawn is TRANSPARENT rather than black, for the same reason
                        // the out-of-bounds skirt is: a hole should read as no picture, not as a dark
                        // building. Only what this capture draws is written here; a pixel kept from the
                        // previous capture keeps that capture's own alpha with its colour.
                        if (take)
                        {
                            // Smoothed for every pixel this capture supplies, and only for those: a
                            // pixel the merge keeps from disk was smoothed when IT was captured, and
                            // filtering it again here would soften it once per capture.
                            float pr, pg, pb;

                            if (SmoothingEnabled)
                            {
                                Smooth(plan, floor, lumFrom, textureRow, col, out pr, out pg, out pb);
                            }
                            else
                            {
                                pr = pixels[at];
                                pg = pixels[at + 1];
                                pb = pixels[at + 2];
                            }

                            // After the smoothing, which by design leaves a lone outlier exactly as it
                            // was - see Despeckle.
                            if (DespeckleEnabled && floor.LumBand != null)
                            {
                                Despeckle(plan, floor, lumFrom, textureRow, col, ref pr, ref pg, ref pb);
                            }

                            block[target + col] = Grade(pr, pg, pb, low, scale, gamma, reach);

                            floor.Filled++;
                        }
                        else if (seenEmpty && SettleEmpty(oldDrawn, oldDistance, oldPixel,
                                     NewStep(plan, dxSquared, dzSquared, col, textureRow), out var settled))
                        {
                            // Campaign speed step 1 (2): still nothing here, now seen from this step - transparent as it
                            // was, but settled for every later capture that is no closer.
                            block[target + col] = oldPixel;
                            floor.Dist[index] = settled;
                            floor.SettledEmpty++;
                            floor.StillEmpty++;
                        }
                        else
                        {
                            block[target + col] = oldPixel;
                            floor.Dist[index] = oldDrawn ? oldDistance : DistanceEmpty;

                            // A side pixel settled empty earlier (transparent, with a step) is still empty, not kept.
                            if (oldDrawn && !(SideSettleEmpty && plan.Side != null && EmptyPixel(oldPixel))) floor.Kept++;
                            else floor.StillEmpty++;
                        }
                    }
                }

                // WP4 B2: SetPixels32(0, y0, W, rows, c) stores c[r*W+col] at texel (col, y0+r), index (y0+r)*W+col - the
                // same place Array.Copy puts it in the pool.
                if (floor.Rgba != null) Array.Copy(block, 0, floor.Rgba, y0 * plan.WidthPx, plan.WidthPx * rows);
                else floor.Texture.SetPixels32(0, y0, plan.WidthPx, rows, Slice(block, plan.WidthPx * rows));
                floor.SmoothMs += clock.Elapsed.TotalMilliseconds;
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
                // WP4 B2: the managed path uploads nothing - nothing on the GPU ever read this picture.
                if (floor.Texture != null) floor.Texture.Apply(updateMipmaps: false);
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
                // What the development cost, in work rather than in frames - the smoothing is most of
                // it on a large floor, and this is the number that decides SmoothingBandPixels.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" developed in {Ms(floor.SmoothMs)} ms of " +
                    $"main-thread work over {(plan.HeightPx + floor.BandRows - 1) / Math.Max(1, floor.BandRows)} band(s) of " +
                    $"{floor.BandRows} rows" +
                    (SmoothingEnabled
                        ? $", the {SmoothingRadius * 2 + 1}x{SmoothingRadius * 2 + 1} bilateral filter included."
                        : ", with no smoothing."));

                // Everything the development held and nothing else: floor.Dist stays, because the
                // sidecar is written out of it a frame later.
                floor.Pixels = null;
                floor.PreviousColour = null;
                floor.PreviousDist = null;
                floor.Block = null;
                floor.DxSquared = null;
                floor.LumBand = null;
                floor.NeighbourLum = null;
                floor.NeighbourIndex = null;
                floor.TileRowOf = null;
                floor.TileColOf = null;
                floor.RenderedTile = null;
            }
        }

        /// <summary>Campaign speed step 1 (2): for a side with <see cref="SideSettleEmpty"/>, each texture row's and column's
        /// tile row and column (from TileRect in the contract's orientation - the orientation Develop indexes after
        /// MirrorSide) and whether each tile was rendered this capture (<see cref="Renders"/>: every tile with no plan,
        /// the Render verdicts and the water promotions with one). Left null for a floor, which keeps 255 for an undrawn
        /// pixel.</summary>
        /// <param name="plan">The plan (a side's own plan).</param>
        /// <param name="floor">The side.</param>
        private static void BuildRenderedTiles(Plan plan, FloorPlan floor)
        {
            floor.TileRowOf = null;
            floor.TileColOf = null;
            floor.RenderedTile = null;

            if (!SideSettleEmpty || plan.Side == null || plan.TilesX <= 0 || plan.TileCount <= 0) return;

            var rows = new int[plan.HeightPx];
            var cols = new int[plan.WidthPx];
            for (var i = 0; i < rows.Length; i++) rows[i] = -1;
            for (var i = 0; i < cols.Length; i++) cols[i] = -1;

            var rendered = new bool[plan.TileCount];
            var any = false;

            for (var tile = 0; tile < plan.TileCount; tile++)
            {
                TileRect(plan, tile, true, out var col0, out var n, out var row0, out var m);

                for (var r = Math.Max(0, row0); r < Math.Min(plan.HeightPx, row0 + m); r++) rows[r] = tile / plan.TilesX;
                for (var c = Math.Max(0, col0); c < Math.Min(plan.WidthPx, col0 + n); c++) cols[c] = tile % plan.TilesX;

                rendered[tile] = Renders(floor, tile);
                any |= rendered[tile];
            }

            if (!any) return;

            floor.TileRowOf = rows;
            floor.TileColOf = cols;
            floor.RenderedTile = rendered;
        }

        /// <summary>
        /// Campaign speed step 1 (2, review): the side's collider skyline - every relief cell's highest collider (x, top, z)
        /// projected into the side's picture by MapSideView's own mapping (dot(r, p) = originR + px / ppm, dot(u, p) =
        /// originU + (height - py) / ppm, the equations GroundPointOf inverts), the highest texture row kept per column, each
        /// cell widened by its own half-width in columns and rows, plus <see cref="SkylineMarginPx"/>. Nothing solid can
        /// draw above it: colliders never stream out. Left null (nothing settled, everything settled before healed) when
        /// there are no tops or on any error.
        /// </summary>
        /// <param name="plan">The capture's plan, with its tops.</param>
        /// <param name="view">The side.</param>
        private static void BuildSkyline(Plan plan, SideView view)
        {
            var floor = view?.Floor;
            if (floor == null) return;

            floor.SkyRow = null;
            floor.SkyFarRow = null;
            floor.EmptyCounts = null;

            var tops = plan.Tops;
            var side = view.Plan;
            if (!SideSettleEmpty || tops?.Y == null || side == null || view.Right == null || view.Up == null || view.Frame == null)
                return;

            try
            {
                var clock = Stopwatch.StartNew();
                var width = side.WidthPx;
                var height = side.HeightPx;
                var ppm = (double)side.Ppm;
                var r = view.Right;
                var u = view.Up;
                var originR = view.Frame[0];
                var originU = view.Frame[2];

                var sky = new int[width];
                for (var c = 0; c < width; c++) sky[c] = int.MinValue;

                var halfPx = tops.CellMetres * 0.5 * ppm;
                var colPad = (int)Math.Ceiling(halfPx) + 1;
                var rowPad = (int)Math.Ceiling(halfPx) + 1;
                var cells = 0;

                for (var row = 0; row < tops.Height; row++)
                {
                    var z = tops.MinZ + (row + 0.5) * tops.CellMetres;

                    for (var col = 0; col < tops.Width; col++)
                    {
                        var y = tops.Y[row * tops.Width + col];
                        if (float.IsNaN(y) || float.IsInfinity(y)) continue;

                        var x = tops.MinX + (col + 0.5) * tops.CellMetres;
                        var pc = (r[0] * x + r[1] * y + r[2] * z - originR) * ppm;
                        var tr = (int)Math.Floor((u[0] * x + u[1] * y + u[2] * z - originU) * ppm) + rowPad;
                        var centre = (int)Math.Floor(pc);

                        var c0 = Math.Max(0, centre - colPad);
                        var c1 = Math.Min(width - 1, centre + colPad);

                        for (var c = c0; c <= c1; c++)
                            if (tr > sky[c])
                                sky[c] = tr;

                        cells++;
                    }
                }

                for (var c = 0; c < width; c++)
                    sky[c] = sky[c] == int.MinValue ? -1 : sky[c] + SkylineMarginPx;

                // The foliage band's top: 30 m of height is 30 x ppm x |u.y| texture rows (u is the picture's up)
                var band = (int)Math.Ceiling(SkylineFoliageMetres * ppm * Math.Abs(u[1]));
                var far = new int[width];
                for (var c = 0; c < width; c++) far[c] = sky[c] < 0 ? -1 : sky[c] + band;

                floor.SkyRow = sky;
                floor.SkyFarRow = far;
                floor.EmptyCounts = SideEmptyCounts(plan, view);

                var clear = 0L;
                foreach (var v in far) clear += Math.Max(0, height - 1 - Math.Max(-1, v));

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} side view {view.Dir} - collider skyline from {cells.ToString(CultureInfo.InvariantCulture)} " +
                    $"relief cell(s): {(100d * clear / Math.Max(1L, (long)width * height)).ToString("0", CultureInfo.InvariantCulture)} % of its pixels lie above it and its " +
                    $"{band.ToString(CultureInfo.InvariantCulture)}-row foliage band (settled when rendered empty), " +
                    (floor.EmptyCounts != null ? "the band settled after " + SettleEmptyStops.ToString(CultureInfo.InvariantCulture) +
                                                 " empty stops of this campaign, "
                        : "the band never settled outside a campaign, ") +
                    $"{Ms(clock.Elapsed.TotalMilliseconds)} ms.");
            }
            catch (Exception ex)
            {
                floor.SkyRow = null;
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} side view {view.Dir}'s collider skyline could not be made ({ex.GetType().Name}: " +
                    $"{ex.Message}) - no empty pixel of it is settled this capture.");
            }
        }

        /// <summary>Campaign speed step 1 (2, re-review): whether a side pixel this capture RENDERED and found empty is
        /// settled: above the foliage band at once; inside it (above the collider skyline) only once the running campaign
        /// has rendered it empty at <see cref="SettleEmptyStops"/> stops, this one counted here - so called once per pixel
        /// per develop, and only for a rendered, undrawn pixel; at or below the skyline, or in the band outside a campaign,
        /// never.</summary>
        /// <param name="floor">The side, its skyline built.</param>
        /// <param name="index">The pixel (texture order).</param>
        /// <param name="col">The column (contract orientation).</param>
        /// <param name="textureRow">The texture row, 0 at the bottom.</param>
        private static bool EmptySettles(FloorPlan floor, int index, int col, int textureRow)
        {
            var sky = floor.SkyRow;
            var far = floor.SkyFarRow;
            if (sky == null || far == null || col < 0 || col >= sky.Length || textureRow <= sky[col]) return false;
            if (textureRow > far[col]) return true;

            var counts = floor.EmptyCounts;
            if (counts == null || index < 0 || index >= counts.Length) return false;

            if (counts[index] < byte.MaxValue) counts[index]++;
            return counts[index] >= SettleEmptyStops;
        }

        /// <summary>Campaign speed step 1 (2, re-review): whether a pixel the sidecar on disk records as settled empty may stay
        /// settled - the same rule without counting: above the foliage band, or in it with the running campaign's count
        /// already at <see cref="SettleEmptyStops"/>.</summary>
        /// <param name="floor">The side, its skyline built.</param>
        /// <param name="index">The pixel (texture order).</param>
        /// <param name="col">The column.</param>
        /// <param name="textureRow">The texture row.</param>
        private static bool SettledMayStay(FloorPlan floor, int index, int col, int textureRow)
        {
            var sky = floor.SkyRow;
            var far = floor.SkyFarRow;
            if (sky == null || far == null || col < 0 || col >= sky.Length || textureRow <= sky[col]) return false;
            if (textureRow > far[col]) return true;

            var counts = floor.EmptyCounts;
            return counts != null && index >= 0 && index < counts.Length && counts[index] >= SettleEmptyStops;
        }

        /// <summary>Campaign speed step 1 (2, review): the repair on load. A side sidecar pixel that is transparent black AND
        /// carries a step is a settled-empty pixel; one the settle rule would not settle now (<see cref="SettledMayStay"/>:
        /// at or below the collider skyline, or in the foliage band without this campaign's count - or any, with the settle
        /// off or no skyline) is read as 255 - never seen - so a pixel settled over a streamed-out building or canopy is
        /// taken again by the next capture that draws it. Floors are untouched.</summary>
        /// <param name="plan">The plan (a side's own plan).</param>
        /// <param name="floor">The side, its previous picture and sidecar just loaded.</param>
        private static void HealSideDist(Plan plan, FloorPlan floor)
        {
            if (plan?.Side == null || floor?.PreviousDist == null || floor.PreviousColour == null) return;

            try
            {
                var dist = floor.PreviousDist;
                var colour = floor.PreviousColour;
                var width = plan.WidthPx;
                var keepAbove = SideSettleEmpty && floor.SkyRow != null;
                var healed = 0;

                for (var i = 0; i < dist.Length && i < colour.Length; i++)
                {
                    if (dist[i] == DistanceEmpty || !EmptyPixel(colour[i])) continue;
                    if (keepAbove && SettledMayStay(floor, i, i % width, i / width)) continue;

                    dist[i] = DistanceEmpty;
                    healed++;
                }

                if (healed > 0)
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" - {healed.ToString(CultureInfo.InvariantCulture)} empty " +
                        "pixel(s) settled at or below the collider skyline read as never seen, so they are taken again.");
            }
            catch (Exception ex)
            {
                // unhealed is the old rule's state: the capture goes on, it is only less willing to retake those pixels
                Plugin.LogSource?.LogDebug($"QuestTree: {plan.Key}'s side sidecar could not be healed ({ex.Message}).");
            }
        }

        /// <summary>Campaign speed step 1 (2): whether this capture rendered the tile holding a pixel.</summary>
        /// <param name="plan">The plan.</param>
        /// <param name="floor">The side, with its lookup built.</param>
        /// <param name="col">The column (contract orientation).</param>
        /// <param name="textureRow">The texture row, 0 at the bottom.</param>
        private static bool RenderedAt(Plan plan, FloorPlan floor, int col, int textureRow)
        {
            var r = floor.TileRowOf[textureRow];
            var c = floor.TileColOf[col];
            if (r < 0 || c < 0) return false;

            var tile = r * plan.TilesX + c;
            return tile < floor.RenderedTile.Length && floor.RenderedTile[tile];
        }

        /// <summary>Campaign speed step 1 (2): a side pixel with nothing drawn in it - transparent black, which is what
        /// Develop leaves where no capture drew (a drawn side pixel is alpha 255: a side has no walkable mask).</summary>
        /// <param name="pixel">The pixel on disk.</param>
        private static bool EmptyPixel(Color32 pixel) => pixel.a == 0 && pixel.r == 0 && pixel.g == 0 && pixel.b == 0;

        /// <summary>Campaign speed step 1 (2): the sidecar step a side pixel this capture rendered and found empty records -
        /// its own step where nothing on disk was ever seen there (255), or where an earlier capture saw it empty from
        /// farther; false where the picture on disk has a real pixel (kept, with its step) or an empty one seen from as
        /// close or closer. Best of by distance, as for a drawn pixel.</summary>
        /// <param name="oldDrawn">Whether the sidecar on disk has a step here.</param>
        /// <param name="oldDistance">That step.</param>
        /// <param name="oldPixel">The pixel on disk.</param>
        /// <param name="step">This capture's step for the pixel.</param>
        /// <param name="settled">The step to record.</param>
        private static bool SettleEmpty(bool oldDrawn, byte oldDistance, Color32 oldPixel, byte step, out byte settled)
        {
            settled = step;

            if (step == DistanceEmpty) return false;
            if (!oldDrawn) return EmptyPixel(oldPixel);

            return EmptyPixel(oldPixel) && step < oldDistance;
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
        /// <param name="reach">How reachable this pixel is, 1 inside the walkable area and 0 well outside
        /// it - see <see cref="BuildReach"/>. A property of the MAP, so it cannot make two captures of one
        /// map disagree about a pixel.</param>
        private static Color32 Grade(float r, float g, float b, float low, float scale, float gamma, float reach)
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

            // Outside the walkable area: darker and greyer, by the share of the way out this pixel is,
            // so the boundary is a ramp rather than a line. Applied after the S-curve and before the
            // highlight ceiling, i.e. on the finished picture rather than on the light, so that what is
            // dimmed is the PICTURE and the exposure the map was developed with is untouched - which is
            // what keeps a merge byte-stable: the mask is a property of the map, identical in every
            // capture of it.
            // The mask is the ALPHA, and the colour is left alone - see ReachIsAlpha. A pixel outside
            // the walkable area is not dimmed, it is not there.
            var alpha = reach >= 1f
                ? (byte)255
                : reach <= 0f
                    ? (byte)0
                    : (byte)(reach * 255f + 0.5f);

            return new Color32(
                Encode(sr, gamma), Encode(sg, gamma), Encode(sb, gamma), alpha);
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
        /// half-float one, which is the other half of the fix: the pipeline's output is linear light
        /// well outside 0..1, and eight bits of it is the near-black picture round 1 produced.
        ///
        /// What the copied DeferredShading actually renders as is forward, because this camera is
        /// orthographic - see the class doc, and the header line below, which reads the path back AFTER
        /// the projection is set rather than trusting the copy.
        ///
        /// With no live camera to copy - not seen in a raid, but Camera.main is null in some loading
        /// states - a bare camera is used instead and said so in the note. It renders through the
        /// project's default path rather than the game's, which round 1 shows is darker still; the
        /// exposure stretch is what makes even that usable.</summary>
        /// <param name="plan">The capture's plan, for the tile size the ortho view is framed to.</param>
        /// <param name="note">A phrase for the log describing how the camera was built.</param>
        private bool BuildCamera(Plan plan, out string note)
        {
            // Stage M2: in the main menu the "live camera" is the menu's own, whose mask is scene data that would hide the
            // map (the probe measured it) - so a menu capture copies nothing and draws every layer less the excluded ones.
            var main = _menu != null ? null : LiveCamera();
            int copied;

            _camera = new GameObject("QuestTreeCaptureCamera").AddComponent<Camera>();

            if (_menu != null)
            {
                copied = ~0;
                note = "a bare camera (menu capture: the menu camera's settings and mask are never copied)";
            }
            else if (main != null)
            {
                // Settings only - rendering path, HDR, layer mask, clear flags - and no components.
                _camera.CopyFrom(main);
                copied = main.cullingMask;
                note = $"settings copied from \"{main.name}\"";
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
            _camera.orthographicSize = TileSize / (2f * plan.SamplePpm);
            _camera.aspect = 1f;
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = 1000f;
            _camera.useOcclusionCulling = false;

            // Unity's own switch for multisampling, false HERE and set for real in BuildTarget, which is
            // the first place the samples the device actually granted are known (allowMSAA = _msaa > 1).
            // False until then rather than true-and-hope: a camera allowed to multisample into a target
            // that was granted one sample is a resolve of nothing. This comment used to say the switch was
            // left off for the whole capture, from the build where it was - see MsaaLevels.
            _camera.allowMSAA = false;
            _camera.depth = -100f;
            var mask = CaptureMask(copied);
            _camera.cullingMask = mask;

            // Stage M2: the mask is the one thing a menu capture decides differently, so it is on the record every time
            if (_menu != null) note = $"{note}, mask 0x{mask:X8} [{MaskNames(mask)}]";

            // Kept for the 3D mesh's building walk, which filters renderers by what the PICTURE draws -
            // see Plan.RenderMask.
            plan.RenderMask = mask;

            // Read HERE and not at the CopyFrom above, which is the whole point of the move: the path a
            // camera reports changes with its projection - an orthographic camera does not get deferred
            // shading in this pipeline - so a path read before the orthographic switch describes the
            // camera we copied FROM and not the one that renders. The header said DeferredShading for a
            // capture that cannot have been deferred.
            note = $"{note} ({_camera.renderingPath}/{_camera.actualRenderingPath}, orthographic)";

            // Stage M2b: a menu capture brings its whole light (BuildMenuRig) and has no own straight-down light; a raid
            // capture - and a menu capture with the rig switched off or failed - builds the own light exactly as before.
            if (_menu == null || !MenuLightingRig || !BuildMenuRig(mask)) BuildLight(mask);

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

            note = $"{note}, {SupersampleFactor}x supersampled, msaa {_msaa}";

            // WP4 A2: how the tiles are read back - "async x3 (R16G16B16A16_SFloat)" or "ReadPixels (why)".
            note = $"{note}, readback {_readbackNote}";

            // Named in the header because they are the two settings that decide whether the picture has
            // buildings in it and whether its ground is smooth - see RenderOnce - and a capture that came
            // back wrong should say on the record what it was rendered under.
            note = $"{note}, lod bias {CaptureLodBias.ToString("0.###", CultureInfo.InvariantCulture)}, terrain basemap";

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

        /// <summary>Stage M2b: the menu rig as this capture built it - what the renders apply, what the header and the
        /// lighting block say, and the proof counts the closing line reports. Null on a raid capture.</summary>
        private sealed class MenuRig
        {
            /// <summary>Towards the sun, unit, world axes (the lighting block's convention).</summary>
            internal Vector3 TowardsSun;

            internal Color SunColour;

            /// <summary>True when <see cref="SunColour"/> is the hosted level's LevelSettings.SunColor.</summary>
            internal bool FromLevel;

            internal string ColourSource = "";

            /// <summary>The flat ambient, <see cref="MenuAmbientOfSun"/> of the sun's colour times its intensity.</summary>
            internal Color Ambient;

            /// <summary>Renders taken under the rig.</summary>
            internal int Renders;

            /// <summary>Times our pre-cull hook ran for the capture camera, and how many of those found the ambient no longer
            /// ours - rewritten between our set before Render and the cull, which is LevelSettings' hook at work.</summary>
            internal int PreCulls;

            internal int Rewritten;

            /// <summary>At the first render: the Camera.onPreCull hooks ahead of ours, how many of them are a
            /// LevelSettings', and whether ours was the last in the list (-1 / false until read).</summary>
            internal int HooksAhead = -1;

            internal int LevelHooksAhead = -1;

            internal bool OursLast;
        }

        /// <summary>What <see cref="ApplyMenuRig"/> changed, read before it changed anything, for
        /// <see cref="ReleaseMenuRig"/> to put back.</summary>
        private sealed class MenuRigHold
        {
            internal AmbientMode Mode;
            internal Color Light;
            internal Color Sky;
            internal Color Equator;
            internal Color Ground;
            internal float Intensity;
            internal SphericalHarmonicsL2 Probe;
            internal float ShadowDistance;
            internal int Cascades;
            internal ShadowProjection Projection;
            internal ShadowQuality Shadows;
            internal ShadowResolution Resolution;
        }

        /// <summary>Stage M2b: the menu rig this capture built, or null (every raid capture).</summary>
        private MenuRig _rig;

        /// <summary>Stage M2b: the menu rig's sun - enabled only for the instant a tile renders, as the own light is.</summary>
        private Light _sun;

        /// <summary>Stage M2b: our Camera.onPreCull hook, one instance so the -= finds the += (see <see cref="MenuPreCull"/>).</summary>
        private Camera.CameraCallback _menuPreCull;

        /// <summary>Metres past the camera's far plane the menu rig's shadows reach, so the far plane's own surface is in.</summary>
        private const float MenuShadowFarSlack = 1f;

        /// <summary>
        /// Stage M2b: the menu rig's sun, disabled until a tile renders, and its description. The colour is the hosted
        /// level's LevelSettings.SunColor when it has one (read in <see cref="LevelSunColour"/>, its own method for
        /// ReadLighting's JIT reason), else <see cref="MenuSunFallbackColour"/>; the direction is
        /// <see cref="MenuSunElevation"/> up at bearing <see cref="MenuSunAzimuth"/>; the shadows are soft, at the
        /// light's highest resolution, on the capture's own layers. False - having said why, with nothing left behind -
        /// when it cannot be built; the caller then builds the own light, and the render tag says so (no rig term).
        /// </summary>
        /// <param name="mask">The capture's culling mask.</param>
        private bool BuildMenuRig(int mask)
        {
            GameObject go = null;

            try
            {
                var colour = MenuSunFallbackColour;
                var fromLevel = false;
                var source = "fallback warm white - the level has no LevelSettings";

                try
                {
                    if (LevelSunColour(out var level))
                    {
                        colour = level;
                        fromLevel = true;
                        source = "LevelSettings.SunColor";
                    }
                }
                catch (Exception ex)
                {
                    source = $"fallback warm white - LevelSettings could not be read ({ex.GetType().Name})";
                }

                var elevation = MenuSunElevation * Mathf.Deg2Rad;
                var azimuth = MenuSunAzimuth * Mathf.Deg2Rad;
                var towards = new Vector3(
                    Mathf.Sin(azimuth) * Mathf.Cos(elevation),
                    Mathf.Sin(elevation),
                    Mathf.Cos(azimuth) * Mathf.Cos(elevation)).normalized;

                go = new GameObject("QuestTreeMenuSun");
                go.transform.SetParent(null, worldPositionStays: true);

                // A directional light shines along its forward: away from the sun
                go.transform.rotation = Quaternion.LookRotation(-towards);

                var sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = colour;
                sun.intensity = MenuSunIntensity;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = MenuSunShadowStrength;
                sun.shadowResolution = UnityEngine.Rendering.LightShadowResolution.VeryHigh;
                sun.cullingMask = mask;
                sun.renderMode = LightRenderMode.ForcePixel;

                // Off until a tile renders - see ApplyMenuRig - so it never lights a frame of the menu.
                sun.enabled = false;

                _sun = sun;
                _menuPreCull = MenuPreCull;
                _rig = new MenuRig
                {
                    TowardsSun = towards,
                    SunColour = colour,
                    FromLevel = fromLevel,
                    ColourSource = source,
                    Ambient = new Color(
                        Mathf.Min(1f, colour.r * MenuSunIntensity * MenuAmbientOfSun),
                        Mathf.Min(1f, colour.g * MenuSunIntensity * MenuAmbientOfSun),
                        Mathf.Min(1f, colour.b * MenuSunIntensity * MenuAmbientOfSun), 1f),
                };

                return true;
            }
            catch (Exception ex)
            {
                _sun = null;
                _rig = null;
                _menuPreCull = null;
                if (go != null) Destroy(go);

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the menu capture's light rig could not be built ({ex.GetType().Name}: {ex.Message}) - it is lit " +
                    "by the raid capture's own straight-down light instead.");
                return false;
            }
        }

        /// <summary>Stage M2b: the hosted level's declared sun colour, opaque - a LevelSettings in a hosted scene first, else
        /// any. Its own method so a game update that removes
        /// LevelSettings fails the JIT here, inside the caller's try, and not in <see cref="BuildMenuRig"/>.</summary>
        /// <param name="colour">The colour, when there is a LevelSettings.</param>
        private static bool LevelSunColour(out Color colour)
        {
            colour = default;

            // (review) the hosted map's own, when several exist - FindObjectOfType's first could be any scene's
            var all = UnityEngine.Object.FindObjectsOfType<LevelSettings>();
            if (all == null || all.Length == 0) return false;

            var hosted = new HashSet<int>(MenuMapHost.HostedScenes().Select(s => s.handle));
            var settings = all.FirstOrDefault(s => s != null && hosted.Contains(s.gameObject.scene.handle)) ??
                           all.FirstOrDefault(s => s != null);
            if (settings == null) return false;

            colour = settings.SunColor;
            colour.a = 1f;
            return true;
        }

        /// <summary>
        /// Stage M2b: the flat ambient, set for the capture camera only, from Camera.onPreCull. It has to be set THERE:
        /// LevelSettings (decompile, LevelSettings.cs:166-170 and 240-262) adds its own OnPreCullCallback to
        /// Camera.onPreCull in Awake, and for EVERY camera - ours included - rewrites RenderSettings.ambientMode,
        /// ambientSkyColor/EquatorColor/GroundColor, ambientLight and ambientIntensity from its own fields, so an ambient
        /// set before Render is gone by the cull. Delegates run in the order they were added, and ours is added right
        /// before each render (<see cref="ApplyMenuRig"/>) and removed right after, so it runs after LevelSettings' and
        /// its values are the ones the render uses. The game's hook is never removed or edited.
        /// </summary>
        /// <param name="cam">The camera about to cull.</param>
        private void MenuPreCull(Camera cam)
        {
            var rig = _rig;
            if (rig == null || cam == null || cam != _camera) return;

            rig.PreCulls++;
            if (RenderSettings.ambientMode != AmbientMode.Flat || RenderSettings.ambientLight != rig.Ambient) rig.Rewritten++;

            SetMenuAmbient(rig);
        }

        /// <summary>The flat ambient itself.</summary>
        private static void SetMenuAmbient(MenuRig rig)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = rig.Ambient;
            RenderSettings.ambientIntensity = 1f;
        }

        /// <summary>Stage M2b: reads what <see cref="ApplyMenuRig"/> will change. Changes nothing, so a throw here leaves
        /// nothing to put back.</summary>
        private static MenuRigHold SnapshotMenuRig() => new MenuRigHold
        {
            Mode = RenderSettings.ambientMode,
            Light = RenderSettings.ambientLight,
            Sky = RenderSettings.ambientSkyColor,
            Equator = RenderSettings.ambientEquatorColor,
            Ground = RenderSettings.ambientGroundColor,
            Intensity = RenderSettings.ambientIntensity,
            Probe = RenderSettings.ambientProbe,
            ShadowDistance = QualitySettings.shadowDistance,
            Cascades = QualitySettings.shadowCascades,
            Projection = QualitySettings.shadowProjection,
            Shadows = QualitySettings.shadows,
            Resolution = QualitySettings.shadowResolution,
        };

        /// <summary>
        /// Stage M2b: the menu rig for one render - the sun on; soft shadows at the highest resolution, one cascade, STABLE
        /// fit (review: its shadow map is fitted to a sphere round the view and snapped to whole texels, so every tile of the
        /// same size gets the same world texel grid and the same softness - a close fit sizes the map to each tile's own
        /// contents, and neighbouring tiles would meet at a seam of different softness), reaching past the camera's far plane (at least <see cref="MenuShadowMinDistance"/>), so the whole depth an
        /// orthographic tile sees is shadowed; the flat ambient set now AND from our pre-cull hook, appended last to
        /// Camera.onPreCull (see <see cref="MenuPreCull"/>). Every change is put back by <see cref="ReleaseMenuRig"/>.
        /// </summary>
        private void ApplyMenuRig()
        {
            var rig = _rig;
            if (rig == null || _sun == null) return;

            rig.Renders++;

            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.shadowProjection = ShadowProjection.StableFit;
            QualitySettings.shadowCascades = MenuShadowCascades;
            QualitySettings.shadowDistance = Mathf.Max(MenuShadowMinDistance, _camera.farClipPlane + MenuShadowFarSlack);

            SetMenuAmbient(rig);

            if (_menuPreCull != null)
            {
                Camera.onPreCull += _menuPreCull;

                // Once: who else is on the hook, and that ours runs last - the proof the ambient is ours at the cull
                if (rig.HooksAhead < 0)
                {
                    var list = Camera.onPreCull?.GetInvocationList() ?? new Delegate[0];
                    rig.HooksAhead = Math.Max(0, list.Length - 1);
                    rig.LevelHooksAhead = list.Count(d => d.Target is LevelSettings);
                    rig.OursLast = list.Length > 0 && Equals(list[list.Length - 1], _menuPreCull);
                }
            }

            _sun.enabled = true;
        }

        /// <summary>Stage M2b: puts back everything <see cref="ApplyMenuRig"/> changed, the hook first. Never throws.</summary>
        /// <param name="held">What was there before, or null when nothing was read (then nothing was changed).</param>
        private void ReleaseMenuRig(MenuRigHold held)
        {
            if (held == null) return;

            try
            {
                if (_menuPreCull != null) Camera.onPreCull -= _menuPreCull;
                if (_sun != null) _sun.enabled = false;

                QualitySettings.shadowDistance = held.ShadowDistance;
                QualitySettings.shadowCascades = held.Cascades;
                QualitySettings.shadowProjection = held.Projection;
                QualitySettings.shadowResolution = held.Resolution;
                QualitySettings.shadows = held.Shadows;

                RenderSettings.ambientMode = held.Mode;
                RenderSettings.ambientSkyColor = held.Sky;
                RenderSettings.ambientEquatorColor = held.Equator;
                RenderSettings.ambientGroundColor = held.Ground;
                RenderSettings.ambientLight = held.Light;
                RenderSettings.ambientIntensity = held.Intensity;

                // Last: the colours above re-derive a flat or gradient probe; the one the scene had is put back over it
                RenderSettings.ambientProbe = held.Probe;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the menu rig could not be put back after a render ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Stage M2b: the far plane of the tallest floor band this capture renders - the top band's reaches from
        /// 300 m over the roofs to 50 m under its floor - for the header's shadow distance (BeginFloor's arithmetic).</summary>
        /// <param name="plan">The capture's plan.</param>
        private static float MenuShadowDistanceFor(Plan plan)
        {
            var far = 0f;

            foreach (var floor in plan.Floors)
            {
                if (floor?.Dto == null) continue;

                var top = !IsFinite(floor.NextMinY);
                var f = BandCameraY(floor.Dto.MaxY, floor.NextMinY) - floor.Dto.MinY + FarClipSlack + (top ? TopBandDepthBelow : 0f);
                if (IsFinite(f) && f > far) far = f;
            }

            return Mathf.Max(MenuShadowMinDistance, far + MenuShadowFarSlack);
        }

        /// <summary>Stage M2b: the header's rig phrase - "menu rig: sun 50/135 colour r/g/b intensity x, shadows soft to N m,
        /// ambient flat r/g/b, scene directional lights disabled N".</summary>
        /// <param name="plan">The capture's plan.</param>
        private string MenuRigNote(Plan plan)
        {
            var rig = _rig;
            if (rig == null) return "no menu rig";

            var lights = _menu != null && _menu.DirectionalLightsDisabled >= 0
                ? _menu.DirectionalLightsDisabled.ToString(CultureInfo.InvariantCulture)
                : "none looked for";

            return $"menu rig: sun {F0(MenuSunElevation)}/{F0(MenuSunAzimuth)} colour {Rgb3(rig.SunColour)} ({rig.ColourSource}) " +
                   $"intensity {MenuSunIntensity.ToString("0.###", CultureInfo.InvariantCulture)}, shadows soft to " +
                   $"{F0(MenuShadowDistanceFor(plan))} m ({MenuShadowCascades} cascade, stable fit, very high; strength " +
                   $"{MenuSunShadowStrength.ToString("0.##", CultureInfo.InvariantCulture)}; each render to its own far plane, at least " +
                   $"{F0(MenuShadowMinDistance)} m), ambient flat {Rgb3(rig.Ambient)}, scene directional lights disabled {lights}, " +
                   "own light off";
        }

        /// <summary>Stage M2b: a menu capture's closing line - its phases' seconds and the rig's proof counts.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="writeSeconds">The write's seconds (WriteMeta).</param>
        /// <param name="totalSeconds">The capture's seconds so far.</param>
        private void LogMenuPhases(Plan plan, double writeSeconds, double totalSeconds)
        {
            try
            {
                var rig = _rig;
                var rigLine = rig == null
                    ? "no menu rig (the raid's own light)"
                    : $"menu rig on {rig.Renders} render(s): our pre-cull hook set the ambient {rig.PreCulls} time(s) and found it " +
                      $"rewritten {rig.Rewritten} time(s) (LevelSettings' hook at work); {rig.HooksAhead} onPreCull hook(s) ahead of " +
                      $"ours, {rig.LevelHooksAhead} of them LevelSettings', ours last: {(rig.OursLast ? "yes" : "NO")}";

                // Stage M2b (review): the proof, said loudly when it fails - a render our hook did not reach, or a hook
                // added after ours, means the flat ambient may not be what those tiles were lit by
                if (rig != null && rig.Renders > 0 && (rig.PreCulls != rig.Renders || !rig.OursLast))
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: menu capture of {plan.Key} - the rig's ambient hook ran {rig.PreCulls} time(s) for " +
                        $"{rig.Renders} render(s) and was {(rig.OursLast ? "" : "NOT ")}last on Camera.onPreCull - some tiles may " +
                        "carry the level's own ambient rather than the rig's.");

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: menu capture of {plan.Key} - phases: floors {F(plan.FloorSeconds)} s (cap {F0(plan.FloorCapSeconds)}), " +
                    $"3D mesh {F(plan.MeshSeconds)} s (watchdog {F0(plan.MeshWatchdogCapSeconds)}), sides {F(plan.SidesSeconds)} s " +
                    $"(cap {F0(SidePhaseSeconds)}), write {F(writeSeconds)} s, total {F(totalSeconds)} s; {rigLine}.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the menu capture's phase line failed ({ex.Message}).");
            }
        }

        private static string F0(double v) => IsFinite(v) ? v.ToString("0", CultureInfo.InvariantCulture) : "n/a";

        private static string Rgb3(Color c) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.00}/{1:0.00}/{2:0.00}", c.r, c.g, c.b);

        /// <summary>The render target and the texture tiles are read back into, half-float when the
        /// hardware renders one.</summary>
        private void BuildTarget()
        {
            _hdr = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf);

            // WP4 A0: the table ReadSampleRow looks the half floats up in - Mathf.HalfToFloat's own answers.
            if (_hdr) HalfTable();

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

            var format = _hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;

            // Multisampling, best first, and ALLOCATED here rather than left to the first render: a
            // device that will not give 8 samples of a half-float target says so by failing Create,
            // and asking now means the fallback happens before a single tile has been rendered rather
            // than silently per frame. ReadPixels resolves a multisampled target on its own.
            foreach (var samples in MsaaLevels)
            {
                var candidate = new RenderTexture(TileSize, TileSize, 24, format) { antiAliasing = samples };

                try
                {
                    if (candidate.Create())
                    {
                        _rt = candidate;
                        _msaa = candidate.antiAliasing;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: a {TileSize} {format} target with {samples}x multisampling was refused " +
                        $"({ex.GetType().Name}: {ex.Message}).");
                }

                Destroy(candidate);
            }

            if (_rt == null)
            {
                // Every multisampled allocation refused, including one sample. The plain constructor
                // creates on first use, which is the behaviour every capture before this had.
                _rt = new RenderTexture(TileSize, TileSize, 24, format);
                _msaa = 1;
            }

            // WP4 A2: the readback ring when the device and the target allow it; the staging texture only for the
            // synchronous path (it is made on demand later for the proof and any retry).
            if (!BuildRing(out _readbackNote)) EnsureStage();

            _sampleRows = new float[SupersampleFactor][];
            for (var i = 0; i < _sampleRows.Length; i++) _sampleRows[i] = new float[TileSize * 4];

            // Unity's own switch for the samples the target was granted: without it the multisampled
            // target is allocated and resolved and nothing is antialiased by it. BuildCamera sets it
            // false, which is right until this is known - and it is known here.
            _camera.allowMSAA = _msaa > 1;

            BuildReflection();

            Plugin.LogSource?.LogDebug(
                $"QuestTree: capture target {_rt.format} with {_msaa}x multisampling, staging " +
                $"{(_stage != null ? _stage.format.ToString() : "none (async)")}, " +
                $"{SupersampleFactor}x{SupersampleFactor} samples a pixel, colour space " +
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
            // A side view's plan carries its side, and the camera stands somewhere else entirely - see
            // PositionSideCamera. The branch is HERE so RenderTile and every other caller stay the one
            // tile loop for floors and sides alike.
            if (plan.Side != null)
            {
                PositionSideCamera(plan.Side, px0, py0);
                return;
            }

            // px0 and py0 are SAMPLE offsets, and the divisor is samples per metre - see
            // Plan.SamplePpm. Everything else about the framing is unchanged by supersampling.
            var centreX = plan.Extent.MinX + (px0 + TileSize * 0.5d) / plan.SamplePpm;
            var centreZ = plan.Extent.MaxZ - (py0 + TileSize * 0.5d) / plan.SamplePpm;

            _camera.transform.position = new Vector3((float)centreX, floor.CameraY, (float)centreZ);
        }

        /// <summary>One render of the camera where it stands, under four settings this capture needs and
        /// the player's own frame must not see:
        ///   - fog OFF: a hundred metres of aerial perspective over a map read from above is a grey wash;
        ///   - the capture's own light ON (<see cref="CaptureLightIntensity"/>), which is what makes an
        ///     overcast raid produce a picture at all;
        ///   - the LOD bias at <see cref="CaptureLodBias"/> and the maximum LOD level at 0, which is what
        ///     makes the BUILDINGS draw for an orthographic camera;
        ///   - every terrain's base-map distance at <see cref="CaptureBasemapDistance"/>, which is what
        ///     stops the ground being a two-metre checker.
        /// Every one of them is a GLOBAL or a scene object's own field, so each is restored in the finally
        /// by the statement that changed it - the whole reason this is one method and not four.
        ///
        /// RenderSettings.ambient is deliberately NOT touched: Phase 0 round 2 forced flat white ambient
        /// on two of its five variants and the pictures came back identical to the ones without it, so it
        /// would be a side effect with no benefit.</summary>
        private void RenderOnce()
        {
            var fog = RenderSettings.fog;
            var lodBias = QualitySettings.lodBias;
            var maximumLod = QualitySettings.maximumLODLevel;
            var reflectionMode = RenderSettings.defaultReflectionMode;
            var customReflection = RenderSettings.customReflectionTexture;
            var reflectionIntensity = RenderSettings.reflectionIntensity;

            // Declared out here and assigned inside the try, so that everything this method changes is
            // changed under the finally that puts it back.
            List<KeyValuePair<Terrain, float>> basemaps = null;

            // Stage M2b: the menu rig's snapshot - null on a raid capture, which never has a sun
            MenuRigHold rigHeld = null;

            try
            {
                RenderSettings.fog = false;
                if (_light != null) _light.enabled = true;

                if (_sun != null)
                {
                    rigHeld = SnapshotMenuRig();
                    ApplyMenuRig();
                }

                basemaps = HoldTerrainBasemaps();

                // The scene's own distance culling and its water are NOT held here: they are held once
                // per floor by the run loop, because two passes over twenty-seven thousand components
                // per tile is a hitch of its own - see ForceCulling.

                // The LOD switch, not a quality preference - see CaptureLodBias. maximumLODLevel is
                // usually already 0 and setting it costs nothing; where the game's quality level has
                // raised it, it is a second way for the detailed mesh to be unreachable.
                QualitySettings.lodBias = CaptureLodBias;
                QualitySettings.maximumLODLevel = 0;

                // The sky taken out of every reflective surface - see ReflectionGrey. Baked
                // ReflectionProbes are untouched, so an interior still reflects its own room.
                if (_reflection != null)
                {
                    RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
                    RenderSettings.customReflectionTexture = _reflection;
                    RenderSettings.reflectionIntensity = ReflectionIntensity;
                }

                _camera.Render();
            }
            finally
            {
                // All four restored by the statements that changed them, and for the same reason: the
                // player's next frame must be drawn with the scene's own fog, the scene's own lights, the
                // player's own LOD distances and the terrain's own detail.
                ReleaseMenuRig(rigHeld);
                RenderSettings.reflectionIntensity = reflectionIntensity;
                RenderSettings.customReflectionTexture = customReflection;
                RenderSettings.defaultReflectionMode = reflectionMode;
                ReleaseTerrainBasemaps(basemaps);
                QualitySettings.maximumLODLevel = maximumLod;
                QualitySettings.lodBias = lodBias;
                if (_light != null) _light.enabled = false;
                RenderSettings.fog = fog;
            }
        }

        /// <summary>Sets every active terrain's base-map distance to <see cref="CaptureBasemapDistance"/>
        /// and hands back what each of them had, for <see cref="ReleaseTerrainBasemaps"/> to put back.
        ///
        /// Null on any failure, which <see cref="ReleaseTerrainBasemaps"/> reads as "nothing was changed":
        /// a tiling ground is a worse picture, not a broken one, and it is not worth a capture. A failure
        /// PART WAY through puts back the terrains it had already changed before saying so, so there is no
        /// path out of here that leaves a terrain holding our value.</summary>
        private static List<KeyValuePair<Terrain, float>> HoldTerrainBasemaps()
        {
            var held = new List<KeyValuePair<Terrain, float>>();

            try
            {
                var terrains = Terrain.activeTerrains;
                if (terrains == null || terrains.Length == 0) return null;

                foreach (var terrain in terrains)
                {
                    if (terrain == null) continue;

                    held.Add(new KeyValuePair<Terrain, float>(terrain, terrain.basemapDistance));
                    terrain.basemapDistance = CaptureBasemapDistance;
                }

                return held;
            }
            catch (Exception ex)
            {
                ReleaseTerrainBasemaps(held);

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the terrain base map could not be forced ({ex.GetType().Name}: {ex.Message}) - " +
                    "the ground will show its detail textures tiling.");
                return null;
            }
        }

        /// <summary>Puts every terrain's own base-map distance back. Guarded and null-safe: a terrain
        /// destroyed between the two calls is skipped rather than allowed to cost the restore of the
        /// others, and the player's ground must come back however this render went.</summary>
        /// <param name="held">What <see cref="HoldTerrainBasemaps"/> returned, or null.</param>
        private static void ReleaseTerrainBasemaps(List<KeyValuePair<Terrain, float>> held)
        {
            if (held == null) return;

            foreach (var entry in held)
            {
                try
                {
                    if (entry.Key != null) entry.Key.basemapDistance = entry.Value;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: a terrain's base map distance could not be restored ({ex.Message}).");
                }
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
                // FIRST of all, before the scene is let go: a raid that ended in the middle of the 3D
                // mesh build may have left an AsyncGPUReadback writing into a GraphicsBuffer, and
                // disposing the build's iterator is what runs the finally that waits for it and
                // releases the buffer. A no-op once the build has finished.
                DisposeMeshBuild();

                // WP4 A2: and the tile readbacks - a slot in flight is waited for before its array is freed.
                ReleaseRing();

                // Then, and before anything is freed: a raid that ended between a floor's first and
                // last tile left the scene's culling forced on and its water off, and this is the only
                // thing that ever runs on that path. Idempotent, so the ordinary path - where the floor
                // loop has already released - pays nothing.
                ReleaseScene();

                // And then let the scene go. These two arrays are tens of thousands of component
                // references collected in Prepare; held past the capture they would keep a whole raid's
                // renderers reachable until the next key press replaced them.
                _culling = null;
                _cullingWasEnabled = null;
                _cullingObjectsHeld = null;
                _cullingObjectWasActive = null;
                _cullers = null;
                _cullingOwner = null;
                _cullingObjectOwner = null;
                _proxyRenderers = null;
                _gameCulled = null;
                _occlusionCulled = null;
                _cullingObjects = 0;
                _water = null;
                _waterMaterials = null;

                if (_plan != null)
                {
                    foreach (var floor in _plan.Floors) ReleaseTexture(floor);

                    // A side view in the middle of being taken when the raid ended.
                    ReleaseTexture(_plan.SideFloor);
                    _plan.SideFloor = null;

                    // Anything this capture staged and did not commit - a refused capture's floors, a
                    // raid that ended mid-capture. Each deletion is guarded on its own, so this
                    // cannot keep the camera below from being destroyed.
                    // Campaign speed step 2: not while a checkpoint of this map is being written - its worker stages under
                    // the same names, and a stop whose checkpoint wait gave up ends here while that write goes on.
                    if (!(_flush != null && !_flush.Completed &&
                          string.Equals(_flush.Key, _plan.Key, StringComparison.OrdinalIgnoreCase)))
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

                // Stage M2b: the menu rig's sun, and our hook in case a render never reached its release - in a try of
                // their own (review), so a throw here cannot keep the render target and the rest below from going
                try
                {
                    if (_menuPreCull != null) Camera.onPreCull -= _menuPreCull;

                    if (_sun != null)
                    {
                        var sun = _sun.gameObject;
                        _sun.enabled = false;
                        Destroy(sun);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: the menu rig could not be taken down ({ex.GetType().Name}: {ex.Message}).");
                }
                finally
                {
                    _menuPreCull = null;
                    _sun = null;
                    _rig = null;
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

                _sampleRows = null;

                // The flat material and its texture, LAST of the water work and never before it: a
                // renderer still holding _waterFlat when it is destroyed draws the "missing material"
                // magenta for the rest of the raid. What guarantees the order is the ReleaseScene at
                // the TOP of this method - its finally calls ReleaseWater whatever the culling restore
                // did, which is the path a raid that ended mid-tile takes - and not a second
                // ReleaseWater here, which is what this used to be: by this line the arrays it reads
                // are nulled above and the count it works from was cleared by the first call, so it
                // could never put anything back. A no-op is not a safety net; the release above is.
                if (_waterFlat != null)
                {
                    var flat = _waterFlat;
                    _waterFlat = null;
                    _waterFlatArrays = null;
                    Destroy(flat);
                }

                if (_waterFlatTexture != null)
                {
                    var texture = _waterFlatTexture;
                    _waterFlatTexture = null;
                    Destroy(texture);
                }

                if (_reflection != null)
                {
                    var reflection = _reflection;
                    _reflection = null;
                    Destroy(reflection);
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
            floor.UnhealedDist = null;
            floor.Block = null;
            floor.DxSquared = null;
            floor.LumBand = null;
            floor.NeighbourLum = null;
            floor.NeighbourIndex = null;
            floor.Cyan = null;
            floor.TileMaxOld = null;
            floor.SkipZone = null;
            floor.Rgba = null;
            floor.SkyRow = null;
            floor.SkyFarRow = null;
            floor.EmptyCounts = null;

            // WP4 B2: the tile verdicts (a few dozen entries) outlive the floor while its encode runs - the settle's
            // captured line (TilesPhrase) and audit line read them; DropEncode drops them.
            if (floor.PictureEncode == null)
            {
                floor.Verdicts = null;
                floor.AuditVerdicts = null;
            }

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
            // Sample space throughout, because that is what the camera renders in: a tile is TileSize
            // SAMPLES across and the projection maps world metres to samples. The output picture's
            // geometry follows from it by an exact integer factor, which is what the box average in
            // RenderTile relies on.
            var centreTileX = Mathf.Clamp(plan.SampleWidth / 2 / TileSize, 0, plan.TilesX - 1);
            var centreTileY = Mathf.Clamp(plan.SampleHeight / 2 / TileSize, 0, plan.TilesY - 1);
            var px0 = centreTileX * TileSize;
            var py0 = centreTileY * TileSize;

            PositionCamera(plan, floor, px0, py0);

            var ppm = plan.SamplePpm;
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

        // --- the side views ---------------------------------------------------------------------
        //
        // Four oblique renders a capture, one from each side of the map looking in at 45 degrees -
        // the texture source for the 3D map's walls (plan, stage U; the geometry is a FROZEN contract
        // that the viewer and the host code against, and MapSideView is its arithmetic). They go
        // through the SAME tile machinery as the floors - RenderTile, Inpaint, Develop - by giving each
        // side a Plan of its own (its size, its tiles, its pixels per metre) and a FloorPlan of its own
        // (its buffers). The only things that differ are where the camera stands (PositionCamera's side
        // branch), the columns being flipped once after the tiles (see MapSideView: the camera's right
        // is the contract's -r), no merge, no walkable mask, and the exposure, which is the top band's
        // and is never re-measured.

        /// <summary>The most pixels per metre a side view is taken at - the contract's number (rollback:
        /// 2f). Half the floors' <see cref="MaxPixelsPerMetre"/>, as it was at 2 against 4: a wall seen at
        /// 45 degrees from outside the map is coarser and more occluded than the ground from above, so
        /// doubling it buys less. Brought down, per side, by the resolution cap (see
        /// <see cref="SideWantedPpm"/>) and then by the same memory budget as a floor when a side's span
        /// needs it. An earlier capture's side at another scale is not merged into (MapSideView.Mismatch
        /// compares the ppm) and is taken again whole; a side carried from it keeps its own pxPerMetre,
        /// which the viewer honours.</summary>
        private const float SidePixelsPerMetre = 4f;

        /// <summary>The scale a side asks <see cref="Budget"/> for: <see cref="SidePixelsPerMetre"/>, held
        /// so its long side, once <see cref="SidePictureSide"/> has rounded it up, stays within
        /// <see cref="Resolution"/> - and so within DynamicMapsLibrary.MaxPictureSide, past which the viewer
        /// refuses a picture. At 2 px/m no side came near the cap; at 4 px/m a map over 2 km on a side, or
        /// a resolution setting of 2048 or 4096, would pass it. The floor's rule (Prepare): the cap less one
        /// block when sides are rounded, because ceil(span x ppm) can land a pixel over what cap / span
        /// promises and rounding that up to a multiple of four would then pass the cap; Resolution() is a
        /// multiple of four, so cap - 4 rounds up to at most cap. Budget only ever lowers it further.
        /// Deterministic for a given frame and setting, which the merge needs (SidePrevious).</summary>
        /// <param name="frame">The side's frame - see MapSideView.Frame: [1] is its r span, [3] its u span.</param>
        private static float SideWantedPpm(double[] frame)
        {
            var cap = Resolution();
            var capPx = AlignPictureSides ? cap - PictureBlock : cap;
            var longSpan = Math.Max(frame[1], frame[3]);

            // A degenerate frame is left to SidePictureSide's one pixel and the self-check, as before.
            if (!IsFinite(longSpan) || longSpan <= 0d) return SidePixelsPerMetre;

            return (float)Math.Min(SidePixelsPerMetre, capPx / longSpan);
        }

        /// <summary>How far in front of the box's nearest corner the side camera stands, in metres;
        /// one metre less of it is the near plane. What that clips is only what lies CLOSER to the camera
        /// than the box's nearest corner along f - it is a plane, not the box's walls. Geometry outside
        /// the extent on the camera's side (the hillside past the map's edge, a building across the
        /// boundary road) whose depth along f falls inside the box's range still projects, and draws over
        /// the map's edge in the picture. The viewer only samples a side where a wall's projection lands,
        /// so this costs the outermost walls some texture, not the map its geometry.</summary>
        private const float SideStandOffMetres = 50f;

        /// <summary>The capture light's intensity multiplier during a side's tiles: 1 / cos 45, so a wall
        /// facing the camera with the light along f gets the irradiance a roof gets from the floors'
        /// down-light. See BeginSide.</summary>
        private const float SideLightGain = 1.41421356f;

        /// <summary>The side views' y range when no mesh was built this capture: the bands' lowest
        /// minY less this, to their highest maxY plus <see cref="SideBandsAbove"/> - roofs stand well
        /// above the walkable band they belong to.</summary>
        private const float SideBandsBelow = 5f;

        /// <summary>See <see cref="SideBandsBelow"/>.</summary>
        private const float SideBandsAbove = 50f;

        /// <summary>One side view while it is being taken: its basis and frame (MapSideView's
        /// numbers), and the Plan and FloorPlan it borrows the floor pipeline through.</summary>
        private sealed class SideView
        {
            public string Dir;
            public double[] Forward;
            public double[] Right;
            public double[] Up;

            /// <summary>originR, spanR, originU, spanU, minF, maxF - see MapSideView.Frame.</summary>
            public double[] Frame;

            public float YMin;
            public float YMax;
            public Plan Plan;
            public FloorPlan Floor;
        }

        /// <summary>A side view's distance sidecar: <c>&lt;key&gt;-side-&lt;dir&gt;.dist.png</c>.</summary>
        /// <param name="key">The map's key.</param>
        /// <param name="dir">"N", "S", "E" or "W".</param>
        private static string SideDistFileName(string key, string dir) => $"{key}-side-{dir}.dist.png";

        /// <summary>
        /// The meta a side view may be merged into, or null - having said why, once, when there WAS an
        /// earlier picture of this side and it cannot be added to.
        ///
        /// A side merges best-of-by-distance exactly as a floor does, so every campaign stop sharpens the
        /// walls near it instead of replacing the whole side with the view from wherever the last stop
        /// stood. The gate is the floors' (LoadPrevious has already required the same extent, render
        /// recipe and gamma for plan.Previous to exist at all) plus what makes two side pictures the
        /// same PIXELS: the same width, height and pixels per metre, the same originR and originU and the
        /// same basis to 1e-4 - a different y range moves originU and the height, so it is caught here -
        /// and the same exposure, the top band's, as the earlier side was developed with. Anything else
        /// and this capture's side replaces the earlier one.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="dir">The side.</param>
        /// <param name="side">The side's own plan, with its size and pixels per metre.</param>
        /// <param name="frame">Its frame - see MapSideView.Frame.</param>
        /// <param name="f">Its forward.</param>
        /// <param name="r">Its right.</param>
        /// <param name="u">Its up.</param>
        /// <param name="exposure">The exposure this capture develops it with.</param>
        private static CaptureMeta SidePrevious(Plan plan, string dir, Plan side, double[] frame, double[] f,
            double[] r, double[] u, ExposureResult exposure)
        {
            var meta = plan.Previous;
            if (meta?.Sides == null) return null;

            var old = meta.Sides.FirstOrDefault(s => s != null && s.Dir == dir);
            if (old == null) return null;

            string why = null;

            if (old.File != SideFileName(plan.Key, dir))
                why = $"it is filed as {old.File}";
            else
                why = MapSideView.Mismatch(old.Width, old.Height, old.PxPerMetre, old.OriginR, old.OriginU,
                    old.Forward, old.Right, old.Up, side.WidthPx, side.HeightPx, side.Ppm, frame[0], frame[2],
                    f, r, u);

            if (why == null)
            {
                var top = plan.Floors[plan.Floors.Count - 1];
                var stored = meta.Floors?.FirstOrDefault(fl => fl != null && fl.Level == top.Dto.Level)?.Exposure;

                if (stored == null || exposure == null ||
                    Math.Abs(stored.Low - exposure.Low) > 1e-6f || Math.Abs(stored.High - exposure.High) > 1e-6f ||
                    Math.Abs(stored.Gamma - exposure.Gamma) > 1e-6f)
                    why = "it was developed with a different exposure";
            }

            if (why == null) return meta;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: side view {dir} of {plan.Key} already on disk cannot be added to - {why} - so this one " +
                "replaces it.");

            return null;
        }

        /// <summary>A side pixel's distance step: the XZ distance from the capturing player to the ground
        /// point the pixel looks at (MapSideView.GroundPointOf on y = the side's yMin), in the floors'
        /// four-metre steps. The side branch of DevelopBand's distance term - the hook, not a copy of the
        /// merge.</summary>
        /// <param name="side">The side's own plan, carrying its SideView.</param>
        /// <param name="col">The pixel's column, from the left of the contract's picture.</param>
        /// <param name="textureRow">Its texture row, 0 at the picture's BOTTOM.</param>
        private static byte SideSteps(Plan side, int col, int textureRow)
        {
            // Stage M2: a menu capture's side pixels are step 0 like its floors' (NewStep asks first; this covers any
            // other caller).
            if (StepZero(side)) return 0;

            var view = side.Side;

            MapSideView.GroundPointOf(view.Right, view.Up, view.Frame[0], view.Frame[2], side.Ppm, side.HeightPx,
                view.YMin, col + 0.5d, side.HeightPx - textureRow - 0.5d, out var x, out var z);

            var dx = x - side.From.x;
            var dz = z - side.From.y;

            return Steps((float)Math.Sqrt(dx * dx + dz * dz));
        }

        /// <summary>A side view's file name: <c>&lt;key&gt;-side-&lt;dir&gt;.png</c>.</summary>
        /// <param name="key">The map's key.</param>
        /// <param name="dir">"N", "S", "E" or "W".</param>
        private static string SideFileName(string key, string dir) => $"{key}-side-{dir}.png";

        /// <summary>
        /// Takes the four side views, one at a time and a step to a frame: for each, the frame and the
        /// camera (<see cref="BeginSide"/>, which runs the self-check), the scene held and the tiles
        /// rendered exactly as a floor's are, the columns flipped into the contract's order, the water
        /// painted out, the development with the top band's exposure, and the encode and the stage
        /// (<see cref="FinishSide"/>). Each side's buffers are released before the next is allocated -
        /// one side's float buffer at a time is the memory rule - and a collect runs between them, as it
        /// does between floors.
        ///
        /// Nothing here can cost the capture its floors: every step is guarded, a side that fails is
        /// left out of the meta with one line, and Run drives this through the same guarded MoveNext it
        /// drives the mesh with.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">The mesh built this capture, whose y range the box uses; null for the
        /// bands' own range.</param>
        private IEnumerator CaptureSides(Plan plan, MapMeshFile mesh)
        {
            var clock = Stopwatch.StartNew();

            if (!SidesSetup(plan, mesh, out var exposure, out var yMin, out var yMax, out var yFrom)) yield break;

            // Said on EVERY capture that renders sides, automatic ticks included, with a ceiling measured
            // from this capture's own floors: the sides go through the same tiles, so their time scales
            // with their pixels - about a third of the floors' on Interchange, about the same again on a
            // one-floor map like Customs. A quarter on top for the per-side set-up and development.
            Plugin.LogSource?.LogInfo(
                $"QuestTree: rendering {plan.Key}'s four side views for the 3D map's walls - the scene is held " +
                $"for each as it is for a floor, up to about {SideSecondsEstimate(plan, yMin, yMax)} s; " +
                $"y {F(yMin)}..{F(yMax)} m from {yFrom}.");

            // WP4 B2: the tally a side's settle adds to, whenever it runs.
            var tally = new SideTally();
            var sizes = tally.Sizes;
            var scales = tally.Scales;
            var cut = 0;

            for (var i = 0; i < MapSideView.Directions.Length; i++)
            {
                // The side phase's budget (review F45), as the floors have: past it the sides not yet started are
                // skipped and carried from an earlier capture.
                if (clock.Elapsed.TotalSeconds > SidePhaseSeconds)
                {
                    cut++;
                    continue;
                }

                var dir = MapSideView.Directions[i];
                var view = BeginSide(plan, dir, yMin, yMax, exposure);

                if (view == null) continue;

                plan.SideFloor = view.Floor;

                // Campaign speed step 1 (2, review): the skyline BEFORE the previous sidecar is loaded (HealSideDist reads it)
                BuildSkyline(plan, view);

                // WP1: as for a floor - the side's previous picture and sidecar (<key>-side-<dir>.png and its
                // .dist.png) and the tile plan before the tiles, when the side merges.
                var loading = LoadAndPlan(view.Plan, view.Floor);
                while (loading.MoveNext()) yield return loading.Current;

                // Campaign speed step 1 (2): a side whose plan renders nothing keeps its picture and sidecar on disk, as a
                // floor does (KeepUnchangedFloor) - no tiles, no develop, no encode; CommitSides carries its entry.
                var unchanged = KeepUnchangedSide(plan, view);
                if (unchanged) tally.Unchanged++;

                // As for a floor: the scene's culling forced and its water flat for the side's rendered tiles,
                // released after its last tile however the tiles went. The water rule runs in the camera's
                // orientation, before MirrorSide.
                if (!unchanged)
                {
                    var tiles = RenderTiles(view.Plan, view.Floor, () =>
                    {
                        // A side still rendering well past the budget is abandoned, as a floor is (review F45).
                        if (clock.Elapsed.TotalSeconds <= SidePhaseSeconds * SidePhaseOverrun) return false;

                        cut++;
                        return true;
                    });

                    while (tiles.MoveNext()) yield return tiles.Current;
                }

                // WP4 B2, BARRIER 3: the previous side's encode, which ran during this side's hold and tiles.
                var settle = SettleSides(plan, view, tally);
                while (settle.MoveNext()) yield return settle.Current;

                if (!view.Floor.Failed && !unchanged)
                {
                    yield return null;
                    MirrorSide(plan, view);
                }

                if (!view.Floor.Failed && !unchanged && (view.Floor.Verdicts == null || view.Floor.Tiles > 0))
                {
                    var inpaint = Inpaint(view.Plan, view.Floor);
                    while (inpaint.MoveNext()) yield return inpaint.Current;
                }

                if (!view.Floor.Failed && !unchanged)
                {
                    // No merge: the side Plan has no Previous, so Develop takes every pixel this render
                    // drew and leaves every other one transparent - alpha 255 where drawn, 0 where not,
                    // which is the contract. No walkable mask: the side Plan has no Reach, so Grade's
                    // alpha is the drawn test alone.
                    var develop = Develop(view.Plan, view.Floor);
                    while (develop.MoveNext()) yield return develop.Current;
                }

                if (!view.Floor.Failed && !unchanged && plan.Hold != null)
                {
                    yield return null;

                    // Campaign speed step 2: held for the next stop and the next checkpoint - no encode, nothing staged.
                    if (HoldSide(plan, view))
                    {
                        tally.Rendered++;
                        sizes.Add($"{view.Plan.WidthPx}x{view.Plan.HeightPx}");
                        scales.Add(view.Plan.Ppm);
                    }
                }
                else if (!view.Floor.Failed && !unchanged && view.Floor.Rgba != null)
                {
                    yield return null;

                    // WP4 B2: the managed path - the encodes start on workers, SettleSides stages them.
                    StartSideEncode(plan, view);
                }
                else if (!view.Floor.Failed && !unchanged)
                {
                    yield return null;

                    if (FinishSide(plan, view))
                    {
                        tally.Rendered++;
                        sizes.Add($"{view.Plan.WidthPx}x{view.Plan.HeightPx}");
                        scales.Add(view.Plan.Ppm);

                        // The distance sidecar AFTER the picture and only when the picture was
                        // written, for the floors' reason (see WriteSidecar): a crash between the two
                        // leaves a sidecar one capture old, which is a coarser merge next time, and never
                        // a sidecar describing pixels that are not there. Written from floor.Dist, which
                        // Develop filled in the contract's orientation - MirrorSide ran before it.
                        yield return null;
                        WriteSidecar(view.Plan, view.Floor);

                        var staged = plan.Sides.LastOrDefault(s => s.Dir == view.Dir);
                        if (staged != null) staged.DistStale = view.Floor.DistStale;
                    }
                }

                ReleaseTexture(view.Floor);
                plan.SideFloor = null;

                yield return null;

                // Between sides, as between floors, and after the yield so Unity's deferred Destroy of
                // the side's texture has happened; not after the last, where nothing is coming.
                if (i < MapSideView.Directions.Length - 1) CollectGarbage($"after {plan.Key}'s side {MapSideView.Directions[i]}");
            }

            RestoreTopCamera();

            // WP4 B2, BARRIER 4: every side's encode staged before the summary - plan.Sides and SideBytes complete
            // before CommitSides.
            var settleAll = SettleSides(plan, null, tally);
            while (settleAll.MoveNext()) yield return settleAll.Current;

            var scale = scales.Count == 0
                ? "at no scale"
                : $"at {string.Join("/", scales.Select(Ppm).ToArray())} px/m";

            Plugin.LogSource?.LogInfo(
                $"QuestTree: side views for {plan.Key} - {tally.Rendered} of {MapSideView.Directions.Length} rendered " +
                $"{scale} ({string.Join(", ", sizes.ToArray())}), " +
                (tally.Unchanged > 0 ? $"{tally.Unchanged} unchanged, not rewritten, " : "") +
                $"{(clock.Elapsed.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture)} s this stop.");

            if (cut > 0)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} - {cut} side view(s) were cut at the {SidePhaseSeconds:0} s side budget; " +
                    "the pictures an earlier capture took of them are kept.");
        }

        /// <summary>
        /// Campaign speed step 1 (2): whether a side keeps its stored picture and sidecar as they are - the side's version of
        /// <see cref="KeepUnchangedFloor"/>. Only the exact case: the side merges (SidePrevious accepted the stored one, so
        /// the same size, frame, basis and exposure), a tile plan was made and renders no tile, the stored picture and its
        /// sidecar were loaded (a develop would copy both unchanged), and the previous meta's entry for this direction is
        /// one CarriedSides will carry, its file and sidecar still on disk. Says so in the side's line.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side, its tile plan made.</param>
        private static bool KeepUnchangedSide(Plan plan, SideView view)
        {
            if (!SkipUnchangedSides || TileSkipAudit || view?.Floor == null || view.Plan == null) return false;

            var floor = view.Floor;
            var side = view.Plan;

            try
            {
                if (floor.Failed || side.Previous == null || floor.Verdicts == null || !floor.PreviousLoaded ||
                    floor.PreviousColour == null || floor.PreviousDist == null || TilesToRender(side, floor).Any())
                    return false;

                var carried = CarriedSides(plan).FirstOrDefault(s => s.Dir == view.Dir);
                if (carried == null || !string.Equals(carried.File, floor.File, StringComparison.OrdinalIgnoreCase))
                    return false;

                // Campaign speed step 2: held as it is (HoldUnchanged), and CommitSides' carry becomes HeldSides'
                if (side.Hold != null)
                {
                    if (HoldUnchanged(side, floor) <= 0) return false;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: side view {view.Dir} of {plan.Key} unchanged, not developed - {TilesPhrase(side, floor)}; its " +
                        "picture and distance sidecar are held as they are.");

                    return true;
                }

                if (!File.Exists(Path.Combine(plan.Dir, SideDistFileName(plan.Key, view.Dir))))
                    return false;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: side view {view.Dir} of {plan.Key} unchanged, not rewritten - {TilesPhrase(side, floor)}; its " +
                    "picture and distance sidecar stay on disk as they are.");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: whether {plan.Key}'s side view {view.Dir} could keep its stored picture was not decided " +
                    $"({ex.GetType().Name}: {ex.Message}) - it is rendered and rewritten.");
                return false;
            }
        }

        /// <summary>The side views' expected wall time, whole seconds rounded up: the floors' measured
        /// seconds per pixel times the four sides' pixels (each framed and budgeted exactly as BeginSide
        /// will), plus a quarter. "?" when the floors rendered nothing to measure from.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="yMin">The box's low y.</param>
        /// <param name="yMax">The box's high y.</param>
        private static string SideSecondsEstimate(Plan plan, float yMin, float yMax)
        {
            var seconds = SideSeconds(plan, yMin, yMax);

            return double.IsNaN(seconds) ? "?" : Math.Ceiling(seconds).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>The number behind <see cref="SideSecondsEstimate"/>: NaN when the floors rendered nothing
        /// to measure from. The mesh build's time cap is derived from it too (see MeshRequest).</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="yMin">The box's low y.</param>
        /// <param name="yMax">The box's high y.</param>
        private static double SideSeconds(Plan plan, float yMin, float yMax)
        {
            try
            {
                if (plan.FloorPixels <= 0 || !(plan.FloorSeconds > 0d)) return double.NaN;

                var e = plan.Extent;
                var pixels = 0L;

                foreach (var dir in MapSideView.Directions)
                {
                    if (!MapSideView.Basis(dir, out var f, out var r, out var u)) continue;

                    MapSideView.Frame(f, r, u, e.MinX, e.MinZ, e.MaxX, e.MaxZ, yMin, yMax, out var frame);

                    var ppm = Budget(SideWantedPpm(frame), frame[1], frame[3], CaptureMemoryBudgetBytes, out _);
                    pixels += (long)SidePictureSide(frame[1], ppm) * SidePictureSide(frame[3], ppm);
                }

                return plan.FloorSeconds * pixels / plan.FloorPixels * 1.25d;
            }
            catch
            {
                return double.NaN;
            }
        }

        /// <summary>The two things every side needs before any of them is framed: the exposure to
        /// develop with and the box's y range. False, having said why, when there is no exposure.
        ///
        /// The exposure is the TOP band's, as the capture developed it - its stored "kept from the first
        /// capture" value on a merge, or this capture's own measurement on a fresh set - and never a
        /// measurement of the side itself. A side view is mostly walls in shadow and a strip of sky-lit
        /// roof; measured on its own it would be stretched to a different tone from the picture draped
        /// over the same buildings' roofs, and the walls would not match their tops. When the top band
        /// failed this time its stored exposure is used; when there is neither, no side is taken.
        ///
        /// The y range is the mesh's (the contract's yMin/yMax) united with the earlier sides' range, so it
        /// only grows (review F13); with no mesh this capture it is the earlier sides' range exactly, and the
        /// bands' own range widened by <see cref="SideBandsBelow"/> and <see cref="SideBandsAbove"/> only
        /// when there is neither.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">The mesh built this capture, or null.</param>
        /// <param name="exposure">The exposure to develop every side with.</param>
        /// <param name="yMin">The box's low y.</param>
        /// <param name="yMax">The box's high y.</param>
        /// <param name="yFrom">Where the y range came from, for the log line.</param>
        private static bool SidesSetup(Plan plan, MapMeshFile mesh, out ExposureResult exposure, out float yMin,
            out float yMax, out string yFrom)
        {
            exposure = null;
            yMin = yMax = 0f;
            yFrom = null;

            try
            {
                var top = plan.Floors[plan.Floors.Count - 1];

                exposure = top.Exposure ?? Stored(plan, top);

                if (exposure == null || !(exposure.High > exposure.Low))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: no side views for {plan.Key} - its top floor has no exposure to develop them " +
                        "with, this capture's or a stored one.");
                    return false;
                }

                if (mesh != null && IsFinite(mesh.YMin) && IsFinite(mesh.YMax) && mesh.YMax > mesh.YMin)
                {
                    yMin = mesh.YMin;
                    yMax = mesh.YMax;
                    yFrom = "the mesh";
                    return Stabilise(plan, ref yMin, ref yMax, ref yFrom);
                }

                // No mesh this capture (an AutoCapture tick, a campaign stop that builds none): the earlier sides' own
                // range, exactly - it was framed on a mesh, and anything else would reframe and refuse their merge.
                if (SideRangeOnlyGrows && PreviousSideRange(plan, out var keptMin, out var keptMax))
                {
                    yMin = keptMin;
                    yMax = keptMax;
                    yFrom = "the earlier sides (no mesh this capture)";
                    return true;
                }

                yMin = float.PositiveInfinity;
                yMax = float.NegativeInfinity;

                foreach (var floor in plan.Floors)
                {
                    if (floor?.Dto == null) continue;
                    if (floor.Dto.MinY < yMin) yMin = floor.Dto.MinY;
                    if (floor.Dto.MaxY > yMax) yMax = floor.Dto.MaxY;
                }

                yMin -= SideBandsBelow;
                yMax += SideBandsAbove;
                yFrom = "the bands (no mesh this capture)";

                return IsFinite(yMin) && IsFinite(yMax) && yMax > yMin && Stabilise(plan, ref yMin, ref yMax, ref yFrom);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no side views for {plan.Key} ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>Metres the side box's y range is snapped out to.</summary>
        private const float SideYSnapMetres = 10f;

        /// <summary>
        /// Makes the side box's y range STABLE between captures (review F13): the per-pixel side merge refuses
        /// any change of framing (originU to 1e-4, and u.y is 0.707, so a centimetre of yMin refused it), and
        /// the range came from this build's measured mesh. Snapped out to whole <see cref="SideYSnapMetres"/>;
        /// and when an earlier capture's sides were framed on a range that holds this one, that range is kept
        /// exactly, so the new sides merge into the old instead of replacing them. When it does not hold this
        /// one, the union of the two is used, never the new range alone, so the range only grows and settles
        /// after one replacement (review F13).
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="yMin">The box's low y, snapped or replaced.</param>
        /// <param name="yMax">The box's high y, snapped or replaced.</param>
        /// <param name="yFrom">Where it came from, for the line.</param>
        private static bool Stabilise(Plan plan, ref float yMin, ref float yMax, ref string yFrom)
        {
            yMin = (float)(Math.Floor(yMin / SideYSnapMetres) * SideYSnapMetres);
            yMax = (float)(Math.Ceiling(yMax / SideYSnapMetres) * SideYSnapMetres);

            if (SideRangeOnlyGrows && PreviousSideRange(plan, out var oldMin, out var oldMax))
            {
                if (oldMin <= yMin && oldMax >= yMax)
                {
                    yMin = oldMin;
                    yMax = oldMax;
                    yFrom += ", kept at the earlier sides' range so they merge";
                }
                else
                {
                    // The union, never the new range alone (review F13): the range only grows, so it settles
                    // after one replacement instead of two overlapping ranges replacing each other stop after stop.
                    yFrom += $", widened from the earlier sides' {F(oldMin)}..{F(oldMax)} m - they are replaced once";
                    yMin = Math.Min(yMin, oldMin);
                    yMax = Math.Max(yMax, oldMax);
                }
            }
            else if (!SideRangeOnlyGrows)
            {
                var previous = plan.Previous?.Sides;

                if (previous != null && previous.Count > 0)
                {
                    var old = previous[0];

                    if (old != null && IsFinite(old.YMin) && IsFinite(old.YMax) && old.YMin <= yMin && old.YMax >= yMax)
                    {
                        yMin = old.YMin;
                        yMax = old.YMax;
                        yFrom += ", kept at the earlier sides' range so they merge";
                    }
                }
            }

            return yMax > yMin;
        }

        /// <summary>Rollback switch for review F13's union rule: false frames the sides as f1aa04f did (the earlier
        /// sides' range only when it holds the new one, the first entry's only; the bands' when no mesh). Static
        /// readonly rather than const, like the file's other switches, so the path it turns off still compiles clean.</summary>
        private static readonly bool SideRangeOnlyGrows = true;

        /// <summary>The union of every earlier side's y range in the previous meta - all four directions, which
        /// can have been framed by different captures - or false when there is none.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="yMin">The union's low y.</param>
        /// <param name="yMax">The union's high y.</param>
        private static bool PreviousSideRange(Plan plan, out float yMin, out float yMax)
        {
            yMin = float.PositiveInfinity;
            yMax = float.NegativeInfinity;

            var previous = plan.Previous?.Sides;
            if (previous == null) return false;

            foreach (var side in previous)
            {
                if (side == null || !IsFinite(side.YMin) || !IsFinite(side.YMax) || !(side.YMax > side.YMin)) continue;
                if (side.YMin < yMin) yMin = side.YMin;
                if (side.YMax > yMax) yMax = side.YMax;
            }

            return IsFinite(yMin) && IsFinite(yMax) && yMax > yMin;
        }

        /// <summary>
        /// Frames one side: its basis and frame from the contract, its pixels per metre from the memory
        /// budget, its own Plan and FloorPlan, the camera turned and clipped to the box - and then the
        /// self-check, BEFORE a byte of the side's buffers is allocated. Null, having said why, when the
        /// side is abandoned.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="dir">"N", "S", "E" or "W".</param>
        /// <param name="yMin">The box's low y.</param>
        /// <param name="yMax">The box's high y.</param>
        /// <param name="exposure">The exposure every side is developed with.</param>
        private SideView BeginSide(Plan plan, string dir, float yMin, float yMax, ExposureResult exposure)
        {
            try
            {
                if (!MapSideView.Basis(dir, out var f, out var r, out var u)) return null;

                var e = plan.Extent;
                MapSideView.Frame(f, r, u, e.MinX, e.MinZ, e.MaxX, e.MaxZ, yMin, yMax, out var frame);

                var ppm = Budget(SideWantedPpm(frame), frame[1], frame[3], CaptureMemoryBudgetBytes, out var budgetNote);

                var side = new Plan
                {
                    Key = plan.Key,
                    Dir = plan.Dir,
                    Extent = plan.Extent,
                    Cap = plan.Cap,
                    Ppm = ppm,
                    WidthPx = SidePictureSide(frame[1], ppm),
                    HeightPx = SidePictureSide(frame[3], ppm),
                    From = plan.From,

                    // Campaign speed step 2: the side merges from and into the same held set as its capture
                    Hold = plan.Hold,

                    // Stage M2: a menu capture's sides record step 0 as its floors do (SideSteps)
                    MenuMode = plan.MenuMode,
                };

                side.TilesX = (side.SampleWidth + TileSize - 1) / TileSize;
                side.TilesY = (side.SampleHeight + TileSize - 1) / TileSize;

                // What this side may be MERGED into: the previous capture's meta, when its picture of this
                // side has exactly this geometry and was developed with this exposure. See SidePrevious.
                side.Previous = SidePrevious(plan, dir, side, frame, f, r, u, exposure);

                var top = plan.Floors[plan.Floors.Count - 1];

                var view = new SideView
                {
                    Dir = dir,
                    Forward = f,
                    Right = r,
                    Up = u,
                    Frame = frame,
                    YMin = yMin,
                    YMax = yMax,
                    Plan = side,
                    Floor = new FloorPlan
                    {
                        Dto = new MapFloorDto
                        {
                            Level = top.Dto.Level,
                            Name = $"side {dir}",
                            MinY = yMin,
                            MaxY = yMax,
                        },
                        File = SideFileName(plan.Key, dir),

                        // The side's distance sidecar: the same RGB24 steps a floor's is, written by the
                        // same WriteSidecar, read back by the same LoadPreviousDist - which is what lets
                        // DevelopBand's merge run on a side unchanged.
                        DistFile = SideDistFileName(plan.Key, dir),
                        Exposure = exposure,
                        ReusedExposure = true,
                        Clock = Stopwatch.StartNew(),
                    },
                };

                side.Side = view;

                // The camera, for this side: looking along f with u up, framed to one tile's worth of
                // samples exactly as a floor is, and clipped to the box along f - near a metre short of
                // the box's nearest corner, far a metre past its farthest.
                _camera.transform.rotation = Quaternion.LookRotation(V(f), V(u));

                // The capture's own light along f too: for a floor it points straight down, which lights
                // roofs head-on and every wall at grazing incidence - exactly the faces a side view is
                // for. Along f, which is 45 degrees down, a vertical wall facing the camera is NOT lit
                // head-on: its normal is horizontal, so n.L = cos 45 = 0.707 against a roof's 1 under the
                // floors' down-light, and under the top band's reused exposure the walls came out about
                // 30 % darker than the roofs they sit under. So the light is also raised by 1/0.707 for
                // the side's tiles, which gives a camera-facing wall the irradiance a roof had. (A roof
                // seen from the side now gets 0.707 x 1.414 = 1 too; nothing it lights is brighter than
                // what the floors developed.) RestoreTopCamera turns it back down and puts the intensity
                // back.
                if (_light != null)
                {
                    _light.transform.rotation = Quaternion.LookRotation(V(f));
                    _light.intensity = CaptureLightIntensity * SideLightGain;
                }

                // Stage M2b (review): the menu rig's sun likewise - held at its bearing, the sides facing away from it (N and W
                // under a south-east sun) would be ambient only under the top band's reused exposure. RestoreTopCamera puts
                // the rig's direction and intensity back.
                if (_sun != null)
                {
                    _sun.transform.rotation = Quaternion.LookRotation(V(f));
                    _sun.intensity = MenuSunIntensity * SideLightGain;
                }

                _camera.orthographicSize = TileSize / (2f * side.SamplePpm);
                _camera.aspect = 1f;
                _camera.nearClipPlane = SideStandOffMetres - 1f;
                _camera.farClipPlane = SideStandOffMetres + (float)(frame[5] - frame[4]) + FarClipSlack;

                if (!SideSelfCheck(view, out var why))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} side view {dir} was not taken - the camera does not agree with the " +
                        $"side's own geometry: {why}.");
                    return null;
                }

                if (budgetNote != null)
                    Plugin.LogSource?.LogInfo($"QuestTree: {plan.Key} side view {dir} - {budgetNote}.");

                // Only now, after the check: a side that fails it costs nothing.
                view.Floor.Pixels = new float[side.WidthPx * side.HeightPx * 3];
                view.Floor.Drawn = new bool[side.WidthPx * side.HeightPx];
                view.Floor.Dist = new byte[side.WidthPx * side.HeightPx];

                return view;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {dir} was not taken ({ex.GetType().Name}: {ex.Message}).");
                NoteOutOfMemory(plan, ex);
                return null;
            }
        }

        /// <summary>Points the side camera at one tile. The tiles are laid out over the CAMERA's picture,
        /// which is the contract's picture mirrored (see MapSideView): tile column 0 is at the image's
        /// high-r edge, rMax = originR + width / ppm, and a tile's centre is TileSize/2 samples further
        /// along -r; rows count down from uMax = originU + height / ppm exactly as a floor's count down
        /// from maxZ. The camera then stands at that centre on the (r, u) plane, backed off along -f to
        /// <see cref="SideStandOffMetres"/> in front of the box's nearest corner.</summary>
        /// <param name="view">The side.</param>
        /// <param name="px0">The tile's left edge, in SAMPLES from the camera picture's left.</param>
        /// <param name="py0">The tile's top edge, in samples from the top.</param>
        private void PositionSideCamera(SideView view, int px0, int py0)
        {
            var side = view.Plan;
            var sp = (double)side.SamplePpm;

            var rMax = view.Frame[0] + side.WidthPx / (double)side.Ppm;
            var uMax = view.Frame[2] + side.HeightPx / (double)side.Ppm;

            var rc = rMax - (px0 + TileSize * 0.5d) / sp;
            var uc = uMax - (py0 + TileSize * 0.5d) / sp;
            var d = view.Frame[4] - SideStandOffMetres;

            var r = view.Right;
            var u = view.Up;
            var f = view.Forward;

            _camera.transform.position = new Vector3(
                (float)(rc * r[0] + uc * u[0] + d * f[0]),
                (float)(rc * r[1] + uc * u[1] + d * f[1]),
                (float)(rc * r[2] + uc * u[2] + d * f[2]));
        }

        /// <summary>
        /// The side's check that can fail, before anything is rendered: three world points projected
        /// by Unity through the camera positioned for tile 0, and by the contract's arithmetic
        /// (MapSideView.Pixel) - the extent's centre at yMin, and the same point ten metres along r and
        /// ten metres along u. They must agree within one output pixel, after the one column flip the
        /// capture applies.
        ///
        /// It fails for exactly the mistakes that would texture every wall wrongly and still look like a
        /// picture: a camera whose right is not the contract's -r (a basis or LookRotation that differs
        /// from MapSideView's - the +r probe then moves the wrong way by 2 x 10 m of pixels), a picture
        /// upside down (the +u probe), a tile origin laid out from the wrong edge, an orthographic size
        /// that does not match the pixels per metre, or a point that falls outside the near and far
        /// planes.
        ///
        /// What it can NOT see, said plainly: whether <see cref="MirrorSide"/> actually runs. It checks
        /// the camera against the contract's picture with the flip APPLIED IN THE ARITHMETIC, so a capture
        /// that forgot to call the flip would pass it and write a mirrored picture. Proving the call from
        /// here would mean rendering a probe and reading a pixel back, a tile's cost per side; instead the
        /// harness asserts from the source that MirrorSide is called exactly once, between the tiles and
        /// the development.
        /// </summary>
        /// <param name="view">The side, framed and with the camera turned.</param>
        /// <param name="why">What disagreed, or null.</param>
        private bool SideSelfCheck(SideView view, out string why)
        {
            var side = view.Plan;
            var e = side.Extent;

            PositionCamera(side, view.Floor, 0, 0);

            var cx = (e.MinX + e.MaxX) * 0.5d;
            var cz = (e.MinZ + e.MaxZ) * 0.5d;
            double cy = view.YMin;

            var probes = new[]
            {
                new[] { cx, cy, cz },
                new[] { cx + 10d * view.Right[0], cy + 10d * view.Right[1], cz + 10d * view.Right[2] },
                new[] { cx + 10d * view.Up[0], cy + 10d * view.Up[1], cz + 10d * view.Up[2] },
            };

            var names = new[] { "the extent's centre at yMin", "10 m along r from it", "10 m along u from it" };

            for (var i = 0; i < probes.Length; i++)
            {
                var p = probes[i];
                var screen = _camera.WorldToScreenPoint(new Vector3((float)p[0], (float)p[1], (float)p[2]));

                if (!(screen.z > _camera.nearClipPlane) || !(screen.z < _camera.farClipPlane))
                {
                    why = $"{names[i]} is {F(screen.z)} m in front of the camera, outside its " +
                          $"{F(_camera.nearClipPlane)}..{F(_camera.farClipPlane)} m clip range";
                    return false;
                }

                MapSideView.Pixel(view.Right, view.Up, view.Frame[0], view.Frame[2], side.Ppm, side.HeightPx,
                    p[0], p[1], p[2], out var expected);

                // Unity's pixel in the CAMERA's picture, in output pixels: tile 0 is that picture's
                // top-left corner, screen y counts up from the tile's bottom. The contract's pixel after the
                // capture's column flip is (width - px, py).
                var cameraX = screen.x / SupersampleFactor;
                var cameraRow = (TileSize - screen.y) / SupersampleFactor;
                var flippedX = side.WidthPx - expected[0];

                if (Math.Abs(cameraX - flippedX) > 1d || Math.Abs(cameraRow - expected[1]) > 1d)
                {
                    why = $"{names[i]} renders at picture pixel ({F((float)(side.WidthPx - cameraX))}, " +
                          $"{F((float)cameraRow)}) where the contract puts it at ({F((float)expected[0])}, " +
                          $"{F((float)expected[1])}), over the 1 px allowed";
                    return false;
                }
            }

            why = null;
            return true;
        }

        /// <summary>Turns the camera's picture into the contract's: every row's columns reversed, in
        /// the float buffer and the drawn mask together. See MapSideView for why the two differ - and
        /// <see cref="SideSelfCheck"/> for the check that proves this flip is the one the camera needs
        /// (not that it runs - the harness checks that from the source).</summary>
        /// <param name="plan">The capture's plan, for the log line.</param>
        /// <param name="view">The side.</param>
        private static void MirrorSide(Plan plan, SideView view)
        {
            try
            {
                var width = view.Plan.WidthPx;
                var height = view.Plan.HeightPx;
                var pixels = view.Floor.Pixels;
                var drawn = view.Floor.Drawn;

                for (var row = 0; row < height; row++)
                {
                    var first = row * width;

                    for (int a = first, b = first + width - 1; a < b; a++, b--)
                    {
                        var drawnA = drawn[a];
                        drawn[a] = drawn[b];
                        drawn[b] = drawnA;

                        for (var c = 0; c < 3; c++)
                        {
                            var value = pixels[a * 3 + c];
                            pixels[a * 3 + c] = pixels[b * 3 + c];
                            pixels[b * 3 + c] = value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                view.Floor.Failed = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {view.Dir} could not be turned round ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
            }
        }

        /// <summary>Encodes a developed side, stages it beside the pictures, records its meta entry and
        /// says what it was. False, having said why, when it is not written.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side, developed.</param>
        private static bool FinishSide(Plan plan, SideView view)
        {
            try
            {
                var floor = view.Floor;
                var side = view.Plan;

                if (floor.Texture == null)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key} side view {view.Dir} has no developed picture to write.");
                    return false;
                }

                var png = floor.Texture.EncodeToPNG();

                var drawn = 0;
                foreach (var d in floor.Drawn) if (d) drawn++;

                // WP4 B2: everything after the encode is RecordSide, shared with the managed path's settle.
                return RecordSide(plan, view, png == null ? 0 : png.Length, path => Stage(path, png), drawn, floor.Drawn.Length,
                    null, null);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {view.Dir} could not be written ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
                return false;
            }
        }

        /// <summary>WP4 B2: the second half of FinishSide, moved verbatim: the empty and cap test, the stage, SideBytes, the
        /// plan.Sides entry (DistStale true until the sidecar is staged), the side's line and the audit line. False,
        /// having said why, when it is not written. Throws on anything unexpected; the caller's catch says so.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side.</param>
        /// <param name="length">The encoded file's length.</param>
        /// <param name="write">Stages the file at the path it is given.</param>
        /// <param name="drawn">Pixels this capture drew (counted before ReleaseTexture on the managed path).</param>
        /// <param name="drawnOf">The side's pixel count.</param>
        /// <param name="encodeMs">The side clock to report, or null for the clock now.</param>
        /// <param name="encoded">Added to the line (the managed path's timing), or null.</param>
        /// <param name="held">Campaign speed step 2: the side was HELD, not encoded - nothing is staged and the size cap is the
        /// checkpoint's to judge.</param>
        private static bool RecordSide(Plan plan, SideView view, long length, Action<string> write, int drawn, int drawnOf,
            double? encodeMs, string encoded, bool held = false)
        {
            var floor = view.Floor;
            var side = view.Plan;

            if (!held && (length == 0 || length > MaxFloorPngBytes))
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {view.Dir} encoded to {length} " +
                    "bytes and was not written.");
                return false;
            }

            write?.Invoke(Path.Combine(plan.Dir, floor.File));

            plan.SideBytes += length;
            plan.Sides.Add(new CaptureSide
            {
                Dir = view.Dir,
                File = floor.File,
                Width = side.WidthPx,
                Height = side.HeightPx,
                PxPerMetre = side.Ppm,
                Forward = Floats(view.Forward),
                Right = Floats(view.Right),
                Up = Floats(view.Up),
                OriginR = view.Frame[0],
                OriginU = view.Frame[2],
                YMin = view.YMin,
                YMax = view.YMax,
                DistStale = true,
            });

            Plugin.LogSource?.LogInfo(
                $"QuestTree: side view {view.Dir} of {plan.Key} - {side.WidthPx}x{side.HeightPx} px at " +
                $"{Ppm(side.Ppm)} px/m, {TilesPhrase(side, floor)}, {Share(drawn, drawnOf)} % drawn this capture, " +
                $"{length} bytes, {Ms(encodeMs ?? floor.Clock?.Elapsed.TotalMilliseconds ?? 0d)} ms, " +
                $"lit along f ({F((float)view.Forward[0])}, {F((float)view.Forward[1])}, " +
                $"{F((float)view.Forward[2])}) at x{SideLightGain.ToString("0.00", CultureInfo.InvariantCulture)}" +
                (floor.Merged
                    ? $", merged: {Share(floor.Kept, drawnOf)} % of pixels kept from earlier, " +
                      $"{Share(floor.Filled, drawnOf)} % newly drawn, " +
                      $"{Share(floor.StillEmpty, drawnOf)} % still empty"
                    : ", fresh (nothing earlier to merge into)") +
                (floor.CyanFilled > 0 ? $", {floor.CyanFilled} cyan water pixels filled" : "") +
                (floor.Despeckled > 0 ? $", {floor.Despeckled} speckles medianed" : "") +
                (floor.SettledEmpty > 0 ? $", {floor.SettledEmpty} empty pixel(s) settled at the step they were seen from" : "") +
                (encoded ?? "") + ".");

            AuditLine(side, floor);

            return true;
        }

        /// <summary>WP4 B2: what a side's settle adds to - CaptureSides' summary line.</summary>
        private sealed class SideTally
        {
            public int Rendered;

            /// <summary>Campaign speed step 1 (2): sides kept on disk as they are (KeepUnchangedSide).</summary>
            public int Unchanged;

            public readonly List<string> Sizes = new List<string>();
            public readonly HashSet<float> Scales = new HashSet<float>();
        }

        /// <summary>WP4 B2: FinishSide's call site on the managed path - the picture's and the sidecar's encodes start on
        /// workers, and what the side's line needs from buffers ReleaseTexture is about to drop (the drawn count, the
        /// clock) is kept on the floor. SettleSides stages them.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side, developed.</param>
        private void StartSideEncode(Plan plan, SideView view)
        {
            var floor = view.Floor;
            var side = view.Plan;

            var drawn = 0;
            if (floor.Drawn != null)
                foreach (var d in floor.Drawn)
                    if (d) drawn++;

            floor.EncodeDrawn = drawn;
            floor.EncodeDrawnOf = floor.Drawn?.Length ?? side.WidthPx * side.HeightPx;
            floor.EncodeMs = floor.Clock?.Elapsed.TotalMilliseconds ?? 0d;
            floor.PictureEncode = StartPng(floor.Rgba, side.WidthPx, side.HeightPx, rgba: true);

            if (floor.Dist != null)
            {
                floor.SidecarSource = floor.Dist;
                floor.SidecarEncode = StartPng(floor.Dist, side.WidthPx, side.HeightPx, rgba: false);
            }

            plan.PendingSides.Add(view);
        }

        /// <summary>WP4 B2, the sides' settle (barriers 3, 4 and 5): every pending side but <paramref name="except"/> waited
        /// for a frame at a time, then staged as FinishSide did (picture, the plan.Sides entry, the counts), and a frame
        /// later its sidecar as WriteSidecar did - with F09's DistStale carried to the entry, the same meaning as
        /// before.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="except">The side being rendered, or null.</param>
        /// <param name="tally">The summary line's counts.</param>
        private IEnumerator SettleSides(Plan plan, SideView except, SideTally tally)
        {
            foreach (var view in plan.PendingSides.ToList())
            {
                if (view == null || view == except) continue;

                var floor = view.Floor;
                var clock = Stopwatch.StartNew();

                while (floor.PictureEncode != null &&
                       (!floor.PictureEncode.IsCompleted || (floor.SidecarEncode != null && !floor.SidecarEncode.IsCompleted)) &&
                       clock.Elapsed.TotalSeconds < EncodeWaitSeconds)
                    yield return null;

                plan.PendingSides.Remove(view);

                if (plan.Refused || floor.PictureEncode == null)
                {
                    DropEncode(floor);
                    view.Plan.RgbaPool = null;
                    continue;
                }

                if (SettleSide(plan, view))
                {
                    tally.Rendered++;
                    tally.Sizes.Add($"{view.Plan.WidthPx}x{view.Plan.HeightPx}");
                    tally.Scales.Add(view.Plan.Ppm);

                    // The sidecar AFTER the picture and only when the picture was written (see WriteSidecar), a frame
                    // later.
                    yield return null;
                    SettleSidecar(view.Plan, floor);

                    var staged = plan.Sides.LastOrDefault(s => s.Dir == view.Dir);
                    if (staged != null) staged.DistStale = floor.DistStale;
                }

                DropEncode(floor);
                view.Plan.RgbaPool = null;
            }
        }

        /// <summary>WP4 B2: a side's picture staged from its managed encode, or from Unity's encode of the same pixels on an
        /// error, a timeout, a failed round trip or a managed file over the cap (judged on Unity's length) - then
        /// RecordSide. False when it was not written.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="view">The side.</param>
        private static bool SettleSide(Plan plan, SideView view)
        {
            var floor = view.Floor;
            var side = view.Plan;

            try
            {
                var result = Settled(floor.PictureEncode, EncodeWaitSeconds, out var why);

                if (result != null && result.Length > MaxFloorPngBytes)
                    why = $"the managed file is {result.Length} bytes, over the {MaxFloorPngBytes / (1024 * 1024)} MB a " +
                          "picture may take - the cap is judged on Unity's encode";

                if (why == null)
                {
                    if (result.RoundTripChecked) _rgbaRoundTripped = true;

                    var written = RecordSide(plan, view, result.Length, path => Stage(path, result.Parts, result.LastLength),
                        floor.EncodeDrawn, floor.EncodeDrawnOf, floor.EncodeMs,
                        $", encoded off the main thread in {Ms(result.Milliseconds)} ms");

                    if (written && VerifyManagedPng)
                        VerifyCopy(plan, floor.File, UnityPicture(side.RgbaPool, side.WidthPx, side.HeightPx));

                    return written;
                }

                FallbackLine($"QuestTree: {plan.Key} side view {view.Dir}", why);

                var png = UnityPicture(side.RgbaPool, side.WidthPx, side.HeightPx);
                var fallback = RecordSide(plan, view, png == null ? 0 : png.Length, path => Stage(path, png), floor.EncodeDrawn,
                    floor.EncodeDrawnOf, floor.EncodeMs, null);

                if (fallback) VerifyCopy(plan, floor.File, png);
                return fallback;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {plan.Key} side view {view.Dir} could not be written ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
                return false;
            }
        }

        /// <summary>Puts the capture camera AND its light back to pointing straight down, as every floor
        /// is taken - nothing renders after the sides today, but a camera or a light left turned is a
        /// trap for whatever does.</summary>
        private void RestoreTopCamera()
        {
            try
            {
                if (_camera != null) _camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                if (_light != null)
                {
                    _light.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    _light.intensity = CaptureLightIntensity;
                }

                // Stage M2b (review): the rig's sun back to its own bearing and strength
                if (_sun != null && _rig != null)
                {
                    _sun.transform.rotation = Quaternion.LookRotation(-_rig.TowardsSun);
                    _sun.intensity = MenuSunIntensity;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the capture camera could not be turned back ({ex.Message}).");
            }
        }

        /// <summary>The side entries an EARLIER capture wrote, when this capture takes none and their
        /// files are still on disk - the same carry-forward the mesh gets (CarriedMesh), and only
        /// reachable on a merge, i.e. with the same extent. Each file name is checked, not trusted.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static List<CaptureSide> CarriedSides(Plan plan)
        {
            var kept = new List<CaptureSide>();
            var previous = plan.Previous?.Sides;

            if (previous == null) return kept;

            foreach (var side in previous)
            {
                if (side == null || string.IsNullOrEmpty(side.Dir) || side.File != SideFileName(plan.Key, side.Dir))
                    continue;

                try
                {
                    // Campaign speed step 2: held counts as there (PictureExists)
                    if (PictureExists(plan, side.File)) kept.Add(side);
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {plan.Key}'s earlier side view {side.Dir} could not be looked for ({ex.Message}).");
                }
            }

            return kept;
        }

        /// <summary>
        /// The side entries the meta will name, PER DIRECTION: the side this capture wrote when it wrote
        /// one and its file moved into place, otherwise the entry an earlier capture wrote for that
        /// direction when its file is still on disk (<see cref="CarriedSides"/>), otherwise nothing. Every
        /// named file joins <paramref name="keep"/>, so DropStalePictures - whose <c>{key}-*.png</c> glob
        /// matches <c>{key}-side-*.png</c> - removes exactly the side files the new meta no longer names.
        ///
        /// Per direction, not all-or-nothing, after review: the side phase can end with three of four
        /// written (a self-check refusal, a too-large encode), with none (no exposure, an exception), or
        /// not run at all (an AutoCapture tick, a campaign stop that is not the last). Taking "the phase
        /// ran" to mean "only this capture's sides" handed every one of those cases to the stale sweep,
        /// which then deleted the EARLIER good pictures of the directions this capture failed. Carried
        /// entries are sound for the same reason a carried mesh is: they are only reachable on a merge,
        /// i.e. the same extent, and each entry carries its own basis, origins and y range.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="keep">The file names the meta accounts for.</param>
        private static List<CaptureSide> CommitSides(Plan plan, HashSet<string> keep)
        {
            var named = new List<CaptureSide>();
            var carried = CarriedSides(plan);
            var carriedCount = 0;

            foreach (var dir in MapSideView.Directions)
            {
                CaptureSide chosen = null;

                var written = plan.Sides.FirstOrDefault(s => s.Dir == dir);

                if (written != null)
                {
                    try
                    {
                        Commit(Path.Combine(plan.Dir, written.File));
                        chosen = written;

                        // Its sidecar after it, in its own try: a sidecar that will not move leaves the
                        // one the previous capture wrote, which is one capture old - a coarser merge
                        // next time, never a wrong one (see WriteSidecar).
                        try
                        {
                            var dist = Path.Combine(plan.Dir, SideDistFileName(plan.Key, dir));

                            // Mirrors WriteMeta's floor rule (review F09): no sidecar staged this capture means the old one
                            // describes a different picture, and its distances would keep this picture's empty pixels empty.
                            if (written.DistStale)
                            {
                                Forget(plan, SideDistFileName(plan.Key, dir));
                                if (!DeleteOrWarn(dist))
                                    Plugin.LogSource?.LogWarning(
                                        $"QuestTree: {plan.Key} side view {dir}'s old distance sidecar could not be removed - the " +
                                        "next capture of that side may keep some of its empty pixels empty.");
                            }
                            else Commit(dist);
                        }
                        catch (Exception ex)
                        {
                            Forget(plan, SideDistFileName(plan.Key, dir));
                            Plugin.LogSource?.LogDebug(
                                $"QuestTree: {plan.Key} side view {dir}'s distance sidecar could not be put in " +
                                $"place ({ex.Message}).");
                        }
                    }
                    catch (Exception ex)
                    {
                        Forget(plan, written.File);
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {plan.Key} side view {dir} could not be put in place " +
                            $"({ex.GetType().Name}: {ex.Message}) - the earlier one is kept if there is one.");
                    }
                }

                // Looked for AFTER a failed commit as well as when nothing was written: Commit deletes
                // the target before it moves the new file in, so a commit that died between the two has
                // taken the old file with it - and CarriedSides' File.Exists is asked again here, which
                // is what keeps the meta from naming a file that is gone.
                if (chosen == null)
                {
                    chosen = carried.FirstOrDefault(s => s.Dir == dir &&
                                                         File.Exists(Path.Combine(plan.Dir, s.File)));
                    if (chosen != null) carriedCount++;
                }

                if (chosen != null) named.Add(chosen);
            }

            if (carriedCount > 0)
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} keeps {carriedCount} side view(s) an earlier capture wrote, for the " +
                    $"direction(s) this capture {(plan.SidesTaken ? "could not write" : "did not take")}.");

            // The picture AND its sidecar: DropStalePictures' {key}-*.png glob matches
            // {key}-side-N.dist.png as well, and a sidecar swept away is every later capture of that side
            // taking its own pixels everywhere - the merge silently undone.
            foreach (var side in named)
            {
                keep.Add(side.File);
                keep.Add(SideDistFileName(plan.Key, side.Dir));
            }

            plan.SidesCarried = carriedCount;

            return named.Count == 0 ? null : named;
        }

        private static Vector3 V(double[] v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);

        private static float[] Floats(double[] v) => new[] { (float)v[0], (float)v[1], (float)v[2] };

        // --- WP2: the stored mesh ---------------------------------------------------------------------

        /// <summary>WP2: the stored mesh and its sidecar, read and checked on a worker (LoadMeshBase), and the lowest and
        /// highest stored building height; or why they cannot be used.</summary>
        private sealed class MeshBaseLoad
        {
            public MapMeshFile File;
            public MapMeshIndex Index;
            public float YLow = float.PositiveInfinity;
            public float YHigh = float.NegativeInfinity;
            public string Refused;

            /// <summary>WP2 (fixes): the refusal is TEMPORARY - a slow load, a file locked or missing this once, the
            /// culling lists unknown this once, a mesh over this machine's memory ceiling - so the capture CARRIES the
            /// stored mesh (no build this stop) instead of rebuilding it from scratch and losing the campaign's union.</summary>
            public bool Temporary;

            /// <summary>The mesh file's deflated size, for the load's peak line.</summary>
            public long Bytes;
        }

        /// <summary>
        /// WP2 (2.5): starts reading the stored mesh this capture's build can add to, or returns null with the reason in
        /// plan.MeshBaseRefused: accumulation off (the MeshAccumulate rollback), a rebuild asked for (MeshRebuildNext), no
        /// earlier capture merged (LoadPrevious refused it: extent, scale, floors, recipe, exposure), no mesh this meta
        /// can carry, or no sidecar beside it. Everything the worker needs is read here, on the main thread - the game
        /// string included. Never throws.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="cullingKnown">Whether this capture knows the culling lists (the request's CullingKnown).</param>
        private static Task<MeshBaseLoad> StartMeshBase(Plan plan, bool cullingKnown)
        {
            plan.MeshBase = null;
            plan.MeshBaseRefused = null;
            plan.MeshBaseTemporary = false;

            try
            {
                plan.Game = Application.version + "|" + Application.unityVersion;

                if (!(ModSettings.MeshAccumulate?.Value ?? true))
                {
                    plan.MeshBaseRefused = "accumulation is off ('3D map: add to the stored mesh')";
                    return null;
                }

                if (ModSettings.MeshRebuildNext?.Value ?? false)
                {
                    plan.MeshBaseRefused = "a rebuild was asked for ('3D map: rebuild from scratch on the next capture')";
                    return null;
                }

                if (plan.Previous == null)
                {
                    plan.MeshBaseRefused = "no earlier mesh (no earlier capture of this map this one merges into)";
                    return null;
                }

                // Campaign speed step 2: the held mesh - the last stop's build, already checked when it was loaded or built -
                // is the base as it is: no read, no hash, no inflate, no page hashes. Only this machine's memory ceiling is
                // asked again, as it is of a stored one.
                var held = plan.Hold?.Mesh;

                // Campaign speed step 2 (review): a held mesh built under another recipe (a setting changed mid-campaign) is
                // let go, and this stop reads the stored mesh as a stop outside a campaign does - LoadMeshBase's recipe rule
                if (held != null && !string.Equals(held.Index?.Recipe, MapMeshBuilder.MeshRecipe, StringComparison.Ordinal))
                {
                    RevertHeldMesh(plan.Hold, "it was built under another mesh recipe");
                    held = null;
                }

                if (held != null)
                {
                    if (held.Index == null)
                    {
                        plan.MeshBaseRefused = "the held mesh has no index";
                        return null;
                    }

                    // The one sidecar check a held mesh can fail within a raid (MapMeshIndex.Mismatch's culling rule, as
                    // LoadMeshBase applies it): known when held and unknown now is this once - carried, no build; unknown when
                    // held and known now rebuilds from scratch, and the rebuilt index says known.
                    if (held.Index.CullingKnown != cullingKnown)
                    {
                        plan.MeshBaseRefused = $"the held index does not fit it - {MapMeshIndex.CullingChanged}";
                        plan.MeshBaseTemporary = held.Index.CullingKnown && !cullingKnown;
                        return null;
                    }

                    var heldCeiling = MapMeshBuilder.MemoryCeiling();
                    if (held.Triangles > heldCeiling)
                    {
                        plan.MeshBaseRefused =
                            $"the held mesh's {held.Triangles.ToString("#,##0", CultureInfo.InvariantCulture)} triangles are over this " +
                            $"machine's memory ceiling of {heldCeiling.ToString("#,##0", CultureInfo.InvariantCulture)}";
                        plan.MeshBaseTemporary = true;
                        return null;
                    }

                    // The deflated size the builder's shipped-size bound is estimated from (Request.BaseFileBytes): the last
                    // written file's bytes a triangle, times the held triangles - the held mesh has no file to measure.
                    var written = plan.Hold.WrittenMesh;
                    var heldBytes = written != null && written.Triangles > 0 && written.Bytes > 0
                        ? (long)(written.Bytes * (double)held.Triangles / written.Triangles)
                        : 0L;

                    // The build changes its base's buildings in place (MapMeshBuilder's merge): from here until the stop is
                    // held whole, the held set is this stop's to finish or to lose - never to be written half-changed.
                    plan.Hold.StopInProgress = true;

                    var heldFile = held.File;
                    var heldIndex = held.Index;
                    return Task.Run(() => HeldMeshBase(heldFile, heldIndex, heldBytes));
                }

                RestoreOld(plan, plan.Previous);

                var carried = CarriedMesh(plan, null);
                if (carried == null)
                {
                    plan.MeshBaseRefused = "no earlier mesh this capture could carry";
                    return null;
                }

                // PART-03's memory ceiling bounds what this machine loads: a mesh over it is carried as it is, not read
                var ceiling = MapMeshBuilder.MemoryCeiling();
                if (carried.Triangles > ceiling)
                {
                    plan.MeshBaseRefused = $"the stored mesh's {carried.Triangles:#,##0} triangles are over this machine's memory ceiling of {ceiling:#,##0}";
                    plan.MeshBaseTemporary = true;
                    return null;
                }

                var indexPath = Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key));
                if (!File.Exists(indexPath))
                {
                    plan.MeshBaseRefused = "no index beside the mesh";
                    return null;
                }

                var meshPath = Path.Combine(plan.Dir, carried.File);
                var bytes = carried.Bytes;
                var sha = carried.Sha256;
                var recipe = MapMeshBuilder.MeshRecipe;
                var game = plan.Game;
                double minX = plan.Extent.MinX, minZ = plan.Extent.MinZ, maxX = plan.Extent.MaxX, maxZ = plan.Extent.MaxZ;
                var mask = plan.RenderMask;

                var pages = new List<KeyValuePair<string, string>>();
                if (plan.Previous.Atlas != null)
                    foreach (var page in plan.Previous.Atlas)
                        pages.Add(new KeyValuePair<string, string>(
                            page == null || !IsPlainFileName(page.File) ? null : Path.Combine(plan.Dir, page.File), page?.Sha256));

                return Task.Run(() => LoadMeshBase(meshPath, bytes, sha, indexPath, recipe, game, minX, minZ, maxX, maxZ, mask,
                    cullingKnown, pages, ceiling));
            }
            catch (Exception ex)
            {
                plan.MeshBaseRefused = $"the stored mesh could not be looked at ({ex.GetType().Name}: {ex.Message})";
                return null;
            }
        }

        /// <summary>Whether a meta-given file name is a bare name in the capture's folder (no path).</summary>
        private static bool IsPlainFileName(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && name != "." && name != "..";

        /// <summary>
        /// WP2 (2.5), on a worker: the stored mesh read and bound to its sidecar - its length and SHA-256 the meta's, the
        /// file read with its caps (MapMeshFile.Read), the sidecar read with its caps and matched (MapMeshIndex.Mismatch:
        /// sha, buildings, triangles, ranges, recipe, game, extent, render mask, culling, pages), and every atlas page on
        /// disk with the sha the meta AND the sidecar name. Then the lowest and highest stored building height, so the
        /// main thread is not charged for it. Any failure is a reason, never a throw.
        /// </summary>
        private static MeshBaseLoad LoadMeshBase(string meshPath, long bytes, string sha, string indexPath, string recipe, string game,
            double minX, double minZ, double maxX, double maxZ, int mask, bool cullingKnown, List<KeyValuePair<string, string>> pages,
            long maxTriangles)
        {
            var load = new MeshBaseLoad();

            try
            {
                var data = File.ReadAllBytes(meshPath);
                load.Bytes = data.Length;
                if (data.Length != bytes)
                {
                    load.Refused = $"the mesh is {data.Length} bytes where the meta says {bytes}";
                    return load;
                }

                var hex = Sha256(data);
                if (!string.Equals(hex, sha, StringComparison.OrdinalIgnoreCase))
                {
                    load.Refused = "the mesh's sha256 is not the one the meta names";
                    return load;
                }

                MapMeshFile file;
                using (var stream = new MemoryStream(data, false)) file = MapMeshFile.Read(stream, maxTriangles);
                var index = MapMeshIndex.Read(File.ReadAllBytes(indexPath));

                var why = index.Mismatch(file, MapMeshIndex.ShaBytes(hex), recipe, game, minX, minZ, maxX, maxZ, mask, cullingKnown);
                if (why != null)
                {
                    load.Refused = $"the index does not fit it - {why}";
                    // only "known when stored, unknown now" is this once; "unknown when stored, known now" rebuilds, and the
                    // rebuilt index says known
                    load.Temporary = why == MapMeshIndex.CullingChanged && index.CullingKnown && !cullingKnown;
                    return load;
                }

                if (pages.Count != file.AtlasPages)
                {
                    load.Refused = $"the meta names {pages.Count} atlas page(s) where the mesh has {file.AtlasPages}";
                    return load;
                }

                for (var p = 0; p < pages.Count; p++)
                {
                    var path = pages[p].Key;
                    if (path == null || !File.Exists(path))
                    {
                        load.Refused = $"atlas page {p} is missing";
                        return load;
                    }

                    var pageHex = Sha256(File.ReadAllBytes(path));
                    if (!string.Equals(pageHex, pages[p].Value, StringComparison.OrdinalIgnoreCase) ||
                        !MapMeshIndex.SameBytes(MapMeshIndex.ShaBytes(pageHex), index.Pages[p].Sha))
                    {
                        load.Refused = $"atlas page {p} is not the one the meta and the index name";
                        return load;
                    }
                }

                file.Bind();

                foreach (var b in file.Buildings)
                    foreach (var code in b.Y)
                    {
                        if (code == MapMeshFile.NoHit) continue;

                        var y = file.HeightOf(code);
                        if (y < load.YLow) load.YLow = y;
                        if (y > load.YHigh) load.YHigh = y;
                    }

                load.File = file;
                load.Index = index;
                return load;
            }
            catch (MapMeshFile.ReaderBoundException ex)
            {
                load.Refused = $"the stored mesh is over this machine's memory ceiling ({ex.Message})";
                load.Temporary = true;
                return load;
            }
            catch (Exception ex) when (ex is IOException && !(ex is InvalidDataException) || ex is UnauthorizedAccessException)
            {
                // a file locked by the Maps tab, an antivirus or a host sync: this once
                load.Refused = $"the stored mesh or a page could not be opened ({ex.GetType().Name}: {ex.Message})";
                load.Temporary = true;
                return load;
            }
            catch (Exception ex)
            {
                load.Refused = $"the stored mesh or its index would not read ({ex.GetType().Name}: {ex.Message})";
                return load;
            }
        }

        /// <summary>Campaign speed step 2, on a worker: the held mesh as a base - LoadMeshBase's last step, the lowest and highest
        /// building height, on the file already in memory.</summary>
        /// <param name="file">The held mesh.</param>
        /// <param name="index">Its sidecar.</param>
        /// <param name="bytes">Its estimated deflated size.</param>
        private static MeshBaseLoad HeldMeshBase(MapMeshFile file, MapMeshIndex index, long bytes)
        {
            var load = new MeshBaseLoad { Bytes = bytes };

            try
            {
                file.Bind();

                foreach (var b in file.Buildings)
                    foreach (var code in b.Y)
                    {
                        if (code == MapMeshFile.NoHit) continue;

                        var y = file.HeightOf(code);
                        if (y < load.YLow) load.YLow = y;
                        if (y > load.YHigh) load.YHigh = y;
                    }

                load.File = file;
                load.Index = index;
                return load;
            }
            catch (Exception ex)
            {
                load.Refused = $"the held mesh would not bind ({ex.GetType().Name}: {ex.Message})";
                return load;
            }
        }

        /// <summary>WP2: the stored mesh taken for this capture's build, or the reason it is not - a load that did not
        /// finish within MeshBaseWaitSeconds included - written to the log's build line and the journal.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="load">The worker's task, or null (the reason is already on the plan).</param>
        private static void TakeMeshBase(Plan plan, Task<MeshBaseLoad> load)
        {
            try
            {
                if (load != null)
                {
                    if (!load.IsCompleted)
                    {
                        plan.MeshBaseRefused = $"the stored mesh did not load within {MeshBaseWaitSeconds:0} s";
                        plan.MeshBaseTemporary = true;
                    }
                    else if (load.IsFaulted || load.IsCanceled)
                    {
                        plan.MeshBaseRefused = $"the stored mesh would not load ({load.Exception?.GetBaseException().Message ?? "cancelled"})";
                        plan.MeshBaseTemporary = true;
                    }
                    else if (load.Result.Refused != null)
                    {
                        plan.MeshBaseRefused = load.Result.Refused;
                        plan.MeshBaseTemporary = load.Result.Temporary;
                    }
                    else
                    {
                        plan.MeshBase = load.Result;
                    }
                }

                if (plan.MeshBase == null && plan.MeshBaseRefused != null)
                    Journal(plan.Key, plan.MeshBaseTemporary
                        ? $"3D mesh carried unchanged, not built this stop - {plan.MeshBaseRefused}."
                        : $"3D mesh rebuilt from scratch - {plan.MeshBaseRefused}.");
            }
            catch (Exception ex)
            {
                plan.MeshBase = null;
                plan.MeshBaseRefused = $"the stored mesh could not be taken ({ex.GetType().Name}: {ex.Message})";
            }
        }

        /// <summary>WP2 (4.1a): a MeshVerifyLastStop page's file - never named by a meta, left alone by every sweep.</summary>
        private static string VerifyAtlasName(string key, int page) =>
            $"{key}-verify-atlas-{page.ToString(CultureInfo.InvariantCulture)}.png";

        /// <summary>
        /// WP2 (4.1a): MeshVerifyLastStop - the same stop's mesh built again FROM SCRATCH (Request.Base null), in the
        /// same scene and a hold of its own, into &lt;key&gt;-mesh.verify.bin with its sidecar and pages
        /// (&lt;key&gt;-verify-atlas-&lt;n&gt;.png): file L, for tools/compare-mesh.py to hold the accumulated file A to.
        /// Written in place, not staged - nothing reads them but the tool. Under the same watchdog as the capture's own build.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        private IEnumerator VerifyMesh(Plan plan)
        {
            MapMeshBuilder.Request request;
            MapMeshBuilder.Result result;

            try
            {
                request = new MapMeshBuilder.Request
                {
                    Map = plan.Key,
                    MinX = plan.Extent.MinX,
                    MinZ = plan.Extent.MinZ,
                    MaxX = plan.Extent.MaxX,
                    MaxZ = plan.Extent.MaxZ,
                    From = plan.From,
                    StepZero = StepZero(plan),
                    RenderMask = plan.RenderMask,
                    CullingKnown = _culling != null,
                    ProxyRenderers = _culling != null ? _proxyRenderers : null,
                    GameCulled = _culling != null ? _gameCulled : null,
                    OcclusionCulled = _culling != null ? _occlusionCulled : null,
                    Scene = _sceneCache,
                    Game = plan.Game ?? "",
                    CaptureOrdinal = plan.Captures,
                    BaseRefused = "this is MeshVerifyLastStop's comparison build",
                    IncludeFoliage = ModSettings.MeshFoliage?.Value ?? false,
                    MeshSizeTargetBytes = MapMeshBuilder.MeshSizeTargetFor(ModSettings.MeshSizeTargetMb?.Value ?? 180),
                    AtlasPartPath = page => Path.Combine(plan.Dir, VerifyAtlasName(plan.Key, page)) + ".part",
                };

                // the same bands and budget the capture's own build had - MeshRequest clears plan.Atlas, which by now is
                // the list the meta will name for the capture's OWN mesh, so it is put back
                var named = new List<CaptureAtlas>(plan.Atlas);
                var own = MeshRequest(plan);
                plan.Atlas.Clear();
                plan.Atlas.AddRange(named);
                request.Bands = own.Bands;
                request.BuildingSeconds = own.BuildingSeconds;

                result = new MapMeshBuilder.Result();
                _meshBuild = MapMeshBuilder.Build(request, result);
                _meshRequest = request;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: MeshVerifyLastStop - building {plan.Key}'s 3D map again from scratch into " +
                    $"{plan.Key}-mesh.verify.bin for tools/compare-mesh.py; the scene is held again.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the verification mesh of {plan.Key} could not be started ({ex.GetType().Name}: {ex.Message}).");
                yield break;
            }

            yield return null;
            HoldScene();
            yield return null;

            var clock = Stopwatch.StartNew();
            var asked = false;

            while (true)
            {
                if (!asked && clock.Elapsed.TotalSeconds > MeshWatchdogSeconds && _meshRequest != null)
                {
                    asked = true;
                    _meshRequest.Abort = true;
                }

                if (asked && clock.Elapsed.TotalSeconds > MeshWatchdogSeconds + MeshWatchdogGraceSeconds)
                {
                    DisposeMeshBuild();
                    break;
                }

                object current = null;
                var more = false;

                try
                {
                    more = _meshBuild != null && _meshBuild.MoveNext();
                    if (more) current = _meshBuild.Current;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: the verification mesh of {plan.Key} was abandoned ({ex.Message}).");
                    more = false;
                }

                if (!more) break;

                yield return current;
            }

            EndMesh(plan, result);
            yield return null;

            if (result.AtlasPages != null && result.AtlasPages.Count > 0)
            {
                var encodeClock = Stopwatch.StartNew();

                while (result.AtlasPages.Any(p => p.Encode != null && !p.Encode.IsCompleted) &&
                       encodeClock.Elapsed.TotalSeconds < AtlasEncodeWaitSeconds)
                    yield return null;
            }

            if (result.File == null)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the verification mesh of {plan.Key} built nothing.");
                yield break;
            }

            Task<byte[][]> write = null;

            try
            {
                foreach (var page in MapMeshBuilder.SettleAccumulated(result))
                {
                    var target = Path.Combine(plan.Dir, VerifyAtlasName(plan.Key, page.Page));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(page.PartPath, target);
                }

                var file = result.File;
                var index = result.Index;

                write = Task.Run(() =>
                {
                    var mesh = MapMeshFile.ToBytes(file);
                    byte[] sidecar = null;

                    if (index != null)
                    {
                        index.MeshSha = MapMeshIndex.ShaBytes(Sha256(mesh));
                        sidecar = MapMeshIndex.ToBytes(index);
                    }

                    return new[] { mesh, sidecar };
                });
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the verification mesh of {plan.Key} could not be settled ({ex.GetType().Name}: {ex.Message}).");
                yield break;
            }

            // 1.19.0 hotfix: bounded like the capture's own serialise
            var writeClock = Stopwatch.StartNew();
            while (!write.IsCompleted && writeClock.Elapsed.TotalSeconds < SerialiseWaitSeconds) yield return null;

            if (!write.IsCompleted)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the verification mesh of {plan.Key} was not serialised within {SerialiseWaitSeconds:0} s - not written.");
                yield break;
            }

            try
            {
                if (write.IsFaulted) throw write.Exception.GetBaseException();

                File.WriteAllBytes(Path.Combine(plan.Dir, plan.Key + "-mesh.verify.bin"), write.Result[0]);
                if (write.Result[1] != null)
                    File.WriteAllBytes(Path.Combine(plan.Dir, plan.Key + "-mesh.verify.index"), write.Result[1]);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: MeshVerifyLastStop - {plan.Key}-mesh.verify.bin written ({result.File.Describe()}); compare it with " +
                    $"python tools/compare-mesh.py captures/{plan.Key}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the verification mesh of {plan.Key} could not be written ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // --- the 3D mesh -------------------------------------------------------------------------

        /// <summary>What <see cref="MapMeshBuilder"/> needs to build this capture's geometry, out of
        /// the plan the pictures were taken from - so the mesh and the pictures are of the same
        /// rectangle, the same bands and the same scene, with nothing having to agree twice.
        ///
        /// The bands' camera height is recomputed through <see cref="BandCameraY"/> rather than read
        /// from <see cref="FloorPlan.CameraY"/>: it is the same number for every floor that was
        /// captured, and it is the RIGHT number for a floor whose <see cref="BeginFloor"/> gave up
        /// before setting it - a band with no picture can still have a relief.
        ///
        /// Only the floors the META WILL NAME get a band, which is why this is built after the floor
        /// loop rather than in Prepare: a mesh whose bands are not the meta's floors is one
        /// tools/check-capture.py refuses and <see cref="SameLevels"/> throws away, so a single floor
        /// that failed with no earlier picture to carry would otherwise cost the whole map its
        /// geometry - reachable on Interchange, where a dark basement is exactly the floor that
        /// fails.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static MapMeshBuilder.Request MeshRequest(Plan plan)
        {
            var request = new MapMeshBuilder.Request
            {
                Map = plan.Key,
                MinX = plan.Extent.MinX,
                MinZ = plan.Extent.MinZ,
                MaxX = plan.Extent.MaxX,
                MaxZ = plan.Extent.MaxZ,
                From = plan.From,

                // Stage M2: a menu capture's relief cells record step 0, as its pixels do
                StepZero = StepZero(plan),
                RenderMask = plan.RenderMask,

                // WP2: what the build adds to, and what it stamps its sidecar with
                Base = plan.MeshBase?.File,
                BaseIndex = plan.MeshBase?.Index,
                BaseYLow = plan.MeshBase?.YLow ?? float.PositiveInfinity,
                BaseYHigh = plan.MeshBase?.YHigh ?? float.NegativeInfinity,
                BaseRefused = plan.MeshBaseRefused,
                BaseFileBytes = plan.MeshBase?.Bytes ?? 0L,
                CaptureOrdinal = plan.Captures,
                Game = plan.Game ?? "",
                // Campaign speed step 2: a page a held stop re-encoded is read from its held file - the one on disk is the last
                // checkpoint's, and the base's index names the held one's sha
                AtlasPagePath = page =>
                    plan.Hold != null && plan.Hold.PendingPages.TryGetValue(page, out var held) && File.Exists(held)
                        ? held
                        : Path.Combine(plan.Dir, MapMeshFile.AtlasFileNameFor(plan.Key, page)),

                // PART-10: trees and bushes in the model, or left out (the default)
                IncludeFoliage = ModSettings.MeshFoliage?.Value ?? false,

                // 2026-09-30: the stored-mesh size target (Advanced, "3D map: mesh size target (MB)"), never past ShippedMeshBytes
                MeshSizeTargetBytes = MapMeshBuilder.MeshSizeTargetFor(ModSettings.MeshSizeTargetMb?.Value ?? 180),
            };

            foreach (var floor in plan.Floors)
            {
                if (floor?.Dto == null || !WillBeNamed(plan, floor)) continue;

                // The topmost band's rays reach as far below it as the picture's far plane does, and
                // only the topmost band's: see MapMeshBuilder.Band.DepthBelow and BeginFloor's use of
                // TopBandDepthBelow, which is the same constant for the same reason.
                var top = !IsFinite(floor.NextMinY);

                request.Bands.Add(new MapMeshBuilder.Band
                {
                    Level = floor.Dto.Level,
                    Name = floor.Dto.Name,
                    MinY = floor.Dto.MinY,
                    MaxY = floor.Dto.MaxY,
                    CameraY = BandCameraY(floor.Dto.MaxY, floor.NextMinY),
                    DepthBelow = top ? TopBandDepthBelow : 0f,

                    // An interior band's relief is its floor, not the tops of its shelves - see
                    // MapMeshBuilder.Band.Interior.
                    Interior = !top,
                });
            }

            // The building phase's cap, from this capture's own clock (second review, H1): the capture's
            // budget (MapMeshBuilder.CaptureSecondsBudget, 210 s) less what the floors MEASURED and what the side
            // views are expected to take (the same estimate their own line prints, over the bands' y range - the
            // sides' box is not known until the mesh is), never under the builder's minimum and never over its
            // MaxBuildingSeconds, which keeps the mesh phase inside the watchdog.
            var sides = 0d;

            if (plan.WantsSides && request.Bands.Count > 0)
            {
                var yMin = request.Bands.Min(b => b.MinY - b.DepthBelow);
                var yMax = request.Bands.Max(b => Math.Max(b.MaxY, b.CameraY));
                var estimate = SideSeconds(plan, yMin, yMax);

                if (!double.IsNaN(estimate)) sides = estimate;
            }

            var floors = plan.FloorSeconds > 0d ? plan.FloorSeconds : 0d;

            // Not floored here: the builder takes the relief's measured seconds off it first and floors what is
            // left at MinBuildingSeconds.
            request.BuildingSeconds = Math.Min(MapMeshBuilder.MaxBuildingSeconds,
                MapMeshBuilder.CaptureSecondsBudget - floors - sides - MapMeshBuilder.AtlasSecondsReserve);

            // Stage M2b: a menu capture has no raid to give the frames back to - its building phase and atlas take the
            // menu's own budgets (MenuBuildingSeconds, MenuAtlasSeconds), which its watchdog is summed from.
            if (plan.MenuBudgets)
            {
                request.BuildingSeconds = MenuBuildingSeconds;
                request.AtlasSeconds = MenuAtlasSeconds;
            }

            // Stage W: each atlas page is streamed by its encoder (a worker) to its staged name plus ".part",
            // renamed to the staged name once the capture has waited for it after the hold (SettleAtlasPages), and
            // committed with the mesh in WriteMeta.
            plan.Atlas.Clear();
            request.AtlasPartPath = page => AtlasPartPath(plan, page);

            return request;
        }

        /// <summary>Where atlas page n's encoder streams it: the page's staged name plus ".part".</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="page">The page.</param>
        private static string AtlasPartPath(Plan plan, int page) =>
            Staged(Path.Combine(plan.Dir, MapMeshFile.AtlasFileNameFor(plan.Key, page))) + ".part";

        /// <summary>Seconds the capture waits, after releasing the scene, for the atlas encodes still running.</summary>
        private const double AtlasEncodeWaitSeconds = 60d;

        /// <summary>
        /// The atlas pages' second half (stage W review, H1): their encodes were started inside the hold and ran
        /// on workers while the builder went on; the capture has waited for them after releasing the scene. Each
        /// finished page's ".part" is renamed to its staged name here, on the main thread, and recorded for the
        /// meta; the first page that did not finish or will not rename ends the atlas - the mesh file's page count
        /// and its later ranges are cut to match BEFORE it is serialised, and the later pages' files deleted.
        /// </summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">The build's result.</param>
        private static void SettleAtlasPages(Plan plan, MapMeshBuilder.Result mesh)
        {
            plan.Atlas.Clear();
            if (mesh?.File == null) return;

            // WP2: page by page, with the sidecar kept in step (MapMeshBuilder.SettleAccumulated): from scratch the stage-W
            // rule (pages 0..n-1 up to the first that did not finish); onto a stored atlas a rewritten page that did not
            // finish keeps its stored file, a new one ends the atlas there.
            var done = MapMeshBuilder.SettleAccumulated(mesh);
            var staged = new Dictionary<int, CaptureAtlas>();

            // Campaign speed step 2: a held stop's pages wait beside the set under .held for the checkpoint that writes the
            // mesh naming them. A build from scratch owes nothing to the pages held before it, so those go first.
            var hold = plan.Hold;
            if (hold != null && !mesh.Accumulated)
            {
                hold.StopInProgress = true;
                SweepHeldPages(hold);
            }

            for (var i = 0; i < done.Count; i++)
            {
                var page = done[i];
                var name = MapMeshFile.AtlasFileNameFor(plan.Key, page.Page);

                try
                {
                    var target = hold != null ? HeldPagePath(plan, name) : Staged(Path.Combine(plan.Dir, name));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(page.PartPath, target);

                    if (hold != null)
                    {
                        hold.StopInProgress = true;
                        hold.PendingPages[page.Page] = target;
                    }

                    staged[page.Page] = new CaptureAtlas
                    {
                        File = name,
                        Page = page.Page,
                        Width = MapMeshFile.AtlasPageSize,
                        Height = MapMeshFile.AtlasPageSize,
                        Tiles = page.Tiles,
                        Bytes = page.Bytes,
                        Sha256 = page.Sha256,
                    };
                }
                catch (Exception ex)
                {
                    var fresh = !mesh.Accumulated || page.Page >= mesh.BasePages;

                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: atlas page {page.Page} of {plan.Key} could not be staged ({ex.GetType().Name}: {ex.Message}) - " +
                        (fresh
                            ? "it and every later page are dropped; their buildings keep the side views."
                            : "the stored page stays and the tiles this capture put on it are dropped."));

                    MapMeshBuilder.FailAtlasPage(mesh, page.Page);

                    if (!fresh) continue;

                    for (var k = i; k < done.Count; k++)
                    {
                        try
                        {
                            if (File.Exists(done[k].PartPath)) File.Delete(done[k].PartPath);
                        }
                        catch
                        {
                            // swept by DropStaged
                        }
                    }

                    break;
                }
            }

            // The meta's list: every page the mesh names, each staged now or carried from the earlier capture (already in
            // place; WriteMeta's Commit is a no-op for it). A page that is neither ends the atlas there.
            var carried = 0;

            for (var page = 0; page < mesh.File.AtlasPages; page++)
            {
                if (staged.TryGetValue(page, out var entry))
                {
                    plan.Atlas.Add(entry);
                    continue;
                }

                var previous = plan.Previous?.Atlas;
                var old = previous != null && page < previous.Count ? previous[page] : null;

                if (mesh.Accumulated && page < mesh.BasePages && old != null && old.Page == page &&
                    old.File == MapMeshFile.AtlasFileNameFor(plan.Key, page))
                {
                    plan.Atlas.Add(old);
                    carried++;
                    continue;
                }

                MapMeshBuilder.TruncateAtlasIndexed(mesh.File, page, mesh.Index);
                break;
            }

            // Campaign speed step 2: a held page past the atlas the mesh now names is nothing's
            if (hold != null)
                foreach (var gone in hold.PendingPages.Keys.Where(p => p >= plan.Atlas.Count).ToList())
                {
                    DeleteQuietly(hold.PendingPages[gone]);
                    hold.PendingPages.Remove(gone);
                }

            if (done.Count > 0 || carried > 0 || (mesh.AtlasPages?.Count ?? 0) > 0)
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: texture atlas for {plan.Key} - {staged.Count} of {mesh.AtlasPages?.Count ?? 0} page(s) encoded " +
                    $"and staged, {MB(staged.Values.Sum(p => p.Bytes))} MB" +
                    (mesh.Accumulated ? $", {carried} stored page(s) carried" : "") + ".");
        }

        /// <summary>The atlas an earlier capture wrote for the mesh this meta carries forward, when every page of
        /// it is still on disk - null otherwise (the carried mesh's ranges then name pages that are gone, and
        /// the viewer falls those buildings back, which it must handle anyway).</summary>
        /// <param name="plan">The capture's plan.</param>
        private static List<CaptureAtlas> CarriedAtlas(Plan plan)
        {
            var previous = plan.Previous?.Atlas;
            if (previous == null || previous.Count == 0) return null;

            RestoreOld(plan, plan.Previous);

            try
            {
                for (var i = 0; i < previous.Count; i++)
                {
                    var page = previous[i];
                    if (page == null || page.Page != i || page.File != MapMeshFile.AtlasFileNameFor(plan.Key, i) ||
                        !File.Exists(Path.Combine(plan.Dir, page.File)))
                        return null;
                }

                return previous;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether <see cref="WriteMeta"/> will name this floor: it was captured this time, or
        /// an earlier capture's picture of it is still on disk to carry. The one predicate, so the
        /// mesh's bands and the meta's floors are the same set by construction and
        /// <see cref="SameLevels"/> is a check rather than a coin toss.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floor">The floor in question.</param>
        private static bool WillBeNamed(Plan plan, FloorPlan floor)
        {
            if (floor == null) return false;
            if (!floor.Failed && floor.Bytes > 0) return true;

            try
            {
                return Carried(plan, floor) != null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: whether {plan.Key} \"{floor.Dto?.Name}\" keeps an earlier picture could not be " +
                    $"decided ({ex.Message}) - it is treated as not kept.");
                return false;
            }
        }

        /// <summary>Starts the mesh build: the request, the iterator the run will drive, and the collect.
        /// Null - having said why - means the mesh phase does not happen and the capture goes straight on
        /// to its meta. The scene hold is the run's, a frame later (see Run).
        ///
        /// All of the setup that can throw is in here, inside one try, because none of it can be inside
        /// the guarded MoveNext loop that follows: a throw from MeshRequest out there would skip
        /// WriteMeta entirely and Cleanup would then drop every staged picture - the capture lost to the
        /// feature that was meant to add to it.</summary>
        /// <param name="plan">The capture's plan.</param>
        private MapMeshBuilder.Result BeginMesh(Plan plan)
        {
            try
            {
                // WriteMeta returns without writing anything when this capture wrote no picture - carried
                // floors or not - so a mesh built now would be staged, never committed, and dropped by
                // Cleanup: a twenty-second hold of the player's view for nothing. Asked with the same
                // predicate WriteMeta uses.
                if (!plan.Floors.Any(f => !f.Failed && f.Bytes > 0))
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: no 3D mesh for {plan.Key} - this capture wrote no picture, so it writes no " +
                        "meta for a mesh to belong to.");
                    return null;
                }

                var request = MeshRequest(plan);

                // The hidden-renderer filter trusts "switched off" only when the hold knows the culling lists; a
                // capture whose culling scan failed keeps the old behaviour (stage W review, M2).
                request.CullingKnown = _culling != null;

                // WP8 (D6): the proxies the build never takes, and the game-culled renderers it reads even when off.
                request.ProxyRenderers = _culling != null ? _proxyRenderers : null;
                request.GameCulled = _culling != null ? _gameCulled : null;
                request.OcclusionCulled = _culling != null ? _occlusionCulled : null;

                // WP2 (7): the raid's LOD map and path hashes, read once a raid rather than once a stop
                // Stage M2: a menu capture has no GameWorld - its session's identity keys the cache instead
                _sceneCache = MapMeshBuilder.CacheFor(_sceneCache, (object)_gameWorld ?? _menu?.Identity);
                request.Scene = _sceneCache;

                // Campaign speed step 1 (4): inside a campaign the relief is cast at its first stop and reused after; 0 (a
                // key press, an automatic tick, or no campaign flag) casts it as before
                // Stage M2: never a campaign's relief for a menu capture - its cells are its own, cast with step 0
                request.ReliefSession = _inCampaign && _menu == null ? _campaignSession : 0;

                if (request.Bands.Count == 0)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: no 3D mesh for {plan.Key} - this capture's meta will name no floor for a " +
                        "relief band to belong to.");
                    return null;
                }

                var result = new MapMeshBuilder.Result();
                var build = MapMeshBuilder.Build(request, result);

                // Said out loud, because the player is standing in the raid while it happens and the
                // screen shows it: the hold forces every renderer and object EFT's distance culling
                // switched off back on, so distant buildings appear for as long as the build runs.
                var soft = Math.Max(MapMeshBuilder.MinBuildingSeconds, request.BuildingSeconds);

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: building {plan.Key}'s 3D map - the scene is held for up to about " +
                    $"{(MapMeshBuilder.HardSecondsFor(soft) + MapMeshBuilder.DrainSeconds + request.AtlasSeconds).ToString("0", CultureInfo.InvariantCulture)} s " +
                    $"(decimating for the first {soft.ToString("0", CultureInfo.InvariantCulture)}, then up to " +
                    $"{request.AtlasSeconds.ToString("0", CultureInfo.InvariantCulture)} s of textures), so distant " +
                    "geometry stays drawn while it runs; the side views after it announce their own" +
                    (request.Base != null
                        ? $" - accumulating: {request.Base.Buildings.Count.ToString("#,##0", CultureInfo.InvariantCulture)} stored building(s) will not be read again unless degraded."
                        : $" - from scratch: {plan.MeshBaseRefused ?? "there is no earlier mesh to add to"}."));

                // the stored mesh is the request's now; the plan lets go of it
                plan.MeshBase = null;

                // The collect LAST, so nothing above it can have thrown after it. The hold is NOT here:
                // Run takes it a frame later, so the collect and the pass over the culled components
                // are two frames rather than one long one. The iterator is lazy - nothing in Build runs
                // until the first MoveNext - so holding it in the field now costs nothing and means
                // Cleanup can dispose it from this line on.
                _meshBuild = build;
                _meshRequest = request;

                CollectGarbage($"before {plan.Key}'s 3D mesh");

                return result;
            }
            catch (Exception ex)
            {
                _meshBuild = null;

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D mesh of {plan.Key} could not be started ({ex.GetType().Name}: " +
                    $"{ex.Message}) - the capture's pictures are unaffected.");

                return null;
            }
        }

        /// <summary>Ends the mesh build: the iterator disposed - which is what runs the finally that
        /// waits for a readback still in flight and releases its GPU buffers - and the scene let go.
        /// Both are idempotent and <see cref="Cleanup"/> does them again, for the raid that ends in the
        /// middle of a build.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">The builder's result, for the debug line.</param>
        private void EndMesh(Plan plan, MapMeshBuilder.Result mesh)
        {
            DisposeMeshBuild();
            ReleaseScene();

            // The line last and in its own try: the two statements above are what the raid needs, and a
            // Describe that threw must not be the reason the scene stayed held.
            try
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key}'s scene is released after the 3D mesh " +
                    $"({(mesh?.File == null ? "nothing was built" : mesh.File.Describe())}).");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D mesh could not describe itself ({ex.Message}).");
            }
        }

        /// <summary>Disposes the mesh build's iterator, if one is running. Disposing an iterator runs
        /// its finally blocks, and inside the builder those are what wait for an AsyncGPUReadback still
        /// writing into a buffer before that buffer is released - so this is not tidiness, it is the
        /// last line between a raid ending mid-readback and a GPU write into freed memory.</summary>
        private void DisposeMeshBuild()
        {
            var build = _meshBuild;
            _meshBuild = null;
            _meshRequest = null;

            if (build == null) return;

            try
            {
                (build as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the 3D mesh build would not be disposed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>Starts the mesh's deflate and hash on a worker thread, or null when there is nothing
        /// to write or the worker could not be started (StageMesh then says so). The mesh file is plain
        /// managed arrays with no Unity object in it, and nothing touches it while the run waits, so the
        /// worker has it to itself.</summary>
        /// <param name="plan">The capture's plan, for the log line.</param>
        /// <param name="mesh">What the builder produced.</param>
        private static Task<SerialisedMesh> SerialiseMesh(Plan plan, MapMeshBuilder.Result mesh)
        {
            if (mesh?.File == null) return null;

            var file = mesh.File;
            var index = mesh.Index;

            try
            {
                return Task.Run(() =>
                {
                    // Into a pre-sized stream, kept as its own buffer and length: the same bytes and hash as
                    // ToBytes, one copy instead of three (WP7 S9.2).
                    using (var stream = MapMeshFile.ToStream(file))
                    {
                        var buffer = stream.GetBuffer();
                        var length = (int)stream.Length;
                        var sha = Sha256(buffer, length);

                        // WP2: the sidecar, bound to these bytes by their hash. One that will not serialise is left out -
                        // the next capture then rebuilds from scratch, which is safe.
                        byte[] indexBytes = null;
                        string indexWhy = null;

                        if (index != null)
                        {
                            try
                            {
                                index.MeshSha = MapMeshIndex.ShaBytes(sha);
                                indexBytes = MapMeshIndex.ToBytes(index);
                            }
                            catch (Exception ex)
                            {
                                indexWhy = $"{ex.GetType().Name}: {ex.Message}";
                            }
                        }
                        else
                        {
                            indexWhy = "the build produced none";
                        }

                        return new SerialisedMesh
                        {
                            Bytes = buffer, Length = length, Sha256 = sha, IndexBytes = indexBytes, IndexWhy = indexWhy,
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D mesh of {plan.Key} could not be handed to a worker ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
                return null;
            }
        }

        /// <summary>A mesh file's bytes and their hash, as the worker hands them back. <see cref="Bytes"/> is the
        /// stream's own buffer: only its first <see cref="Length"/> bytes are the file.</summary>
        private sealed class SerialisedMesh
        {
            public byte[] Bytes;
            public int Length;
            public string Sha256;

            /// <summary>WP2: the sidecar's bytes, or null with the reason.</summary>
            public byte[] IndexBytes;

            public string IndexWhy;
        }

        /// <summary>Writes the built mesh beside the pictures as a STAGED file and records what the
        /// meta will say about it - the same Stage/Commit pair the pictures use, for the same reason:
        /// nothing a capture writes is in place until <see cref="WriteMeta"/> puts it there, so a
        /// refusal or a raid that ends mid-capture leaves the set on disk exactly as it was.
        ///
        /// A failure here loses the mesh and nothing else: the staged file is forgotten, the meta gets
        /// no mesh block, and the capture's pictures are written as though this feature did not
        /// exist.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="mesh">What the builder produced. A null file means nothing was built.</param>
        /// <param name="serialised">The worker that deflated and hashed it, already finished - or null
        /// when none could be started.</param>
        private static void StageMesh(Plan plan, MapMeshBuilder.Result mesh, Task<SerialisedMesh> serialised)
        {
            if (plan == null || mesh == null || mesh.File == null) return;

            try
            {
                if (serialised == null)
                    throw new InvalidOperationException("the mesh was never serialised");

                // A worker that threw hands its exception back wrapped; the inner one is the reason.
                if (serialised.IsFaulted)
                {
                    var inner = serialised.Exception?.GetBaseException();
                    throw new InvalidOperationException(
                        inner == null ? "the worker failed" : $"{inner.GetType().Name}: {inner.Message}", inner);
                }

                var bytes = serialised.Result.Bytes;
                var length = serialised.Result.Length;
                var sha = serialised.Result.Sha256;

                Stage(Path.Combine(plan.Dir, plan.MeshFile), bytes, length);

                // WP2: the sidecar beside it, staged the same way and committed with it
                plan.IndexStaged = false;

                if (serialised.Result.IndexBytes != null)
                {
                    Stage(Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key)), serialised.Result.IndexBytes);
                    plan.IndexStaged = true;
                }
                else
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {plan.Key}'s mesh has no identity sidecar this time ({serialised.Result.IndexWhy}) - the next " +
                        "capture rebuilds its 3D mesh from scratch.");
                }

                // The bands that ended up in the file, for the one thing WriteMeta can check and this
                // cannot: that they are exactly the floors the meta will name.
                plan.MeshLevels = new HashSet<int>();

                foreach (var band in mesh.File.Bands) plan.MeshLevels.Add(band.Level);

                plan.MeshAccumulated = mesh.Accumulated;
                plan.Mesh = new CaptureMesh
                {
                    File = plan.MeshFile,
                    Bytes = length,
                    Version = MapMeshFile.Version,
                    Cells = mesh.Cells,
                    Triangles = mesh.Triangles,
                    Sha256 = sha,
                };

                // The two halves are the arrays' own sizes, BEFORE deflate: the file is one deflate
                // block, so there is no relief section and building section on disk to measure
                // separately. The third number is what is actually on the disk.
                plan.MeshNote =
                    $"{MB(mesh.ReliefBytes)} MB relief + {MB(mesh.BuildingBytes)} MB buildings, " +
                    $"{MB(length)} MB deflated, sha256 {ShortSha(sha)}" + (mesh.Accumulated ? " (accumulated)" : "") +
                    "; " + MeshTargetNote(length);
            }
            catch (Exception ex)
            {
                plan.Mesh = null;
                plan.MeshNote = null;
                plan.MeshLevels = null;
                plan.IndexStaged = false;
                Forget(plan, plan.MeshFile);
                Forget(plan, MapMeshIndex.FileNameFor(plan.Key));

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D mesh of {plan.Key} could not be written ({ex.GetType().Name}: " +
                    $"{ex.Message}) - the capture's pictures are unaffected and the meta names no mesh.");
            }
        }

        /// <summary>The mesh block an EARLIER capture of this map wrote, when this capture built none
        /// and that file is still on disk - and null when there is none to keep.
        ///
        /// Only reachable on a merge, which means <see cref="LoadPrevious"/> has already accepted the
        /// previous meta: the same extent, the same scale and the same floors, which are exactly the
        /// things a mesh has to agree with the pictures about. The file itself is untouched by this
        /// capture, so its length and its hash are still the ones that meta recorded.
        ///
        /// The file name is checked rather than trusted - it comes out of a JSON file - so a meta
        /// naming <c>..\\..\\something-mesh.bin</c> carries nothing forward.
        ///
        /// Says nothing itself: it is asked twice - once by the cost gate in <see cref="Prepare"/>,
        /// before anything has happened, and once by <see cref="WriteMeta"/>, which is where a line
        /// about keeping a mesh belongs.
        ///
        /// Its BANDS are checked as well, through the previous meta's floors: that mesh was written
        /// against those floors and this capture's <see cref="SameLevels"/> gate is what proved it, so
        /// if this meta names a different set of floors the old mesh is one the checker would refuse
        /// and the viewer could not peel. It is then left on disk unnamed for
        /// <see cref="DropStalePictures"/> to sweep.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="floors">The floors this meta will name, or null at the cost gate, where no meta
        /// exists yet and the previous floors are the best statement of what a carried mesh covers.</param>
        private static CaptureMesh CarriedMesh(Plan plan, List<CaptureFloor> floors)
        {
            var previous = plan.Previous?.Mesh;

            if (previous == null || !MapMeshFile.IsMeshFileName(previous.File)) return null;

            // A mesh of another format version is one this build cannot read and must not name (a v1 file carried
            // into a v2 meta would be refused by every reader).
            if (previous.Version != MapMeshFile.Version)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key}'s earlier 3D mesh is format {previous.Version}, not {MapMeshFile.Version} - " +
                    "it is not carried forward.");
                return null;
            }

            if (floors != null && !SameLevels(LevelsOf(plan.Previous.Floors), floors))
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key}'s earlier 3D mesh covers floor(s) " +
                    $"[{Levels(LevelsOf(plan.Previous.Floors))}] while this meta names [{Levels(floors)}] - it " +
                    "is not carried forward.");
                return null;
            }

            try
            {
                return File.Exists(Path.Combine(plan.Dir, previous.File)) ? previous : null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key}'s earlier mesh could not be looked for ({ex.Message}) - the meta " +
                    "names no mesh.");
                return null;
            }
        }

        /// <summary>Whether the staged mesh's bands are exactly the floors the meta is about to
        /// name.</summary>
        /// <param name="levels">The levels the mesh carries a band for, or null.</param>
        /// <param name="floors">The floors the meta will name.</param>
        private static bool SameLevels(HashSet<int> levels, List<CaptureFloor> floors)
        {
            if (levels == null || floors == null) return false;
            if (levels.Count != floors.Count) return false;

            foreach (var floor in floors)
                if (floor == null || !levels.Contains(floor.Level)) return false;

            return true;
        }

        /// <summary>The levels a list of meta floors carries, as a set.</summary>
        /// <param name="floors">The floors, or null.</param>
        private static HashSet<int> LevelsOf(List<CaptureFloor> floors)
        {
            var levels = new HashSet<int>();

            if (floors == null) return levels;

            foreach (var floor in floors)
                if (floor != null) levels.Add(floor.Level);

            return levels;
        }

        /// <summary>A set of floor levels for a log line, lowest first.</summary>
        /// <param name="levels">The levels.</param>
        private static string Levels(HashSet<int> levels)
        {
            if (levels == null) return "none";

            var sorted = new List<int>(levels);
            sorted.Sort();

            return string.Join(", ", sorted.Select(l => l.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>The floor levels a meta names, for the line above.</summary>
        /// <param name="floors">The floors.</param>
        private static string Levels(List<CaptureFloor> floors)
        {
            if (floors == null) return "none";

            var sorted = floors.Where(f => f != null).Select(f => f.Level).ToList();
            sorted.Sort();

            return string.Join(", ", sorted.Select(l => l.ToString(CultureInfo.InvariantCulture)).ToArray());
        }

        /// <summary>A byte array's SHA-256 as lower-case hex. The meta carries it so a reader - this
        /// machine's Maps tab, or a client that downloaded the set from a host - can tell a mesh that
        /// belongs to a meta from one that was replaced under it.</summary>
        /// <param name="bytes">The bytes to hash.</param>
        private static string Sha256(byte[] bytes) => Sha256(bytes, bytes.Length);

        /// <summary>The hash of a buffer's first <paramref name="length"/> bytes, as <see cref="Sha256(byte[])"/>.</summary>
        private static string Sha256(byte[] bytes, int length)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes, 0, length);
                var text = new StringBuilder(hash.Length * 2);

                foreach (var b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));

                return text.ToString();
            }
        }

        /// <summary>The head of a hash, for a log line that has to be readable.</summary>
        /// <param name="sha">The full hex hash.</param>
        private static string ShortSha(string sha) =>
            string.IsNullOrEmpty(sha) ? "?" : (sha.Length <= 8 ? sha : sha.Substring(0, 8) + "...");

        /// <summary>Bytes as megabytes, two decimals - the way the mesh's log line reports its
        /// sections.</summary>
        /// <param name="bytes">The byte count.</param>
        private static string MB(long bytes) =>
            (bytes / (1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>2026-09-30: the stored mesh file's size against the player's target (ModSettings.MeshSizeTargetMb). The
        /// target is what the builder AIMS for, from bytes a triangle measured on the stored file: a mesh over it is brought
        /// down at the next captures, not cut to it now.</summary>
        /// <param name="length">The mesh file's bytes as written.</param>
        private static string MeshTargetNote(long length)
        {
            var target = MapMeshBuilder.MeshSizeTargetFor(ModSettings.MeshSizeTargetMb?.Value ?? 180);

            return $"mesh file {MB(length)} MiB against the {MB(target)} MiB target" +
                   (length > target ? " - over it; the next captures shrink it toward the target" : "");
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
            // Stage M3 (review): Replace's backup and whether the meta has landed - a failure in between puts the old set
            // back (the catch below), one after it leaves the new set as it is.
            string setAside = null;
            var metaWritten = false;

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

                // Stage M3: Replace moves the stored set aside BEFORE anything of this capture is put in place - so the
                // old set is restorable whatever happens next, and the commits below land in a folder holding only this
                // capture's staged files. A set that will not move is not replaced: nothing is written, and the staged
                // files are dropped by Cleanup.
                if (plan.MenuMode && MenuReplaceMode && plan.MenuWrite == MenuWriteMode.Replace)
                {
                    if (!SetStoredAside(plan, out var aside, out var asideBytes, out var why))
                    {
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the menu capture of {plan.Key} was NOT written - the stored set could not be moved aside " +
                            $"({why}); it is left as it was.");
                        if (_menu != null) _menu.Stopped = $"the stored set could not be moved aside ({why})";
                        return;
                    }

                    setAside = aside;

                    if (_menu != null)
                    {
                        _menu.SetAside = aside;
                        _menu.SetAsideBytes = asideBytes;
                    }
                }

                // WP3: before the first file is replaced - an upload of this map reading between two of its items sees
                // that pictures under their names have changed (the supersede guard, MapTransfer.Overtaken).
                Bump(plan.Key, shape: false);

                // The floors the meta will name, and the files that are therefore NOT stale. A floor
                // this capture could not take keeps whatever an earlier capture of it left on disk:
                // see Carried, where the reason is that the alternative is throwing a set away.
                var floors = new List<CaptureFloor>();
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var carried = 0;
                var unchanged = 0;

                foreach (var floor in plan.Floors)
                {
                    CaptureFloor entry;

                    if (!floor.Failed && floor.Bytes > 0 && floor.Unchanged)
                    {
                        // Campaign speed step 1 (1): nothing was staged - the picture and sidecar on disk ARE this
                        // floor's, and the entry is the one a rewrite would have written (the same file, size, band and
                        // stored exposure).
                        entry = Described(plan, floor);
                        unchanged++;

                        // Stage M3: and its pixels are the stored ones, so a stored viewing copy still shows them
                        CarryView(plan, entry, Carried(plan, floor));
                    }
                    else if (!floor.Failed && floor.Bytes > 0)
                    {
                        entry = Described(plan, floor);

                        // Only now does this capture touch anything a reader looks at, and in the
                        // order the crash story depends on: the picture, then its sidecar, and the
                        // meta after every floor - see Stage and WriteSidecar.
                        Commit(Path.Combine(plan.Dir, floor.File));
                        if (!string.IsNullOrEmpty(floor.DistFile))
                        {
                            if (floor.DistStale)
                            {
                                if (!DeleteOrWarn(Path.Combine(plan.Dir, floor.DistFile)))
                                    Plugin.LogSource?.LogWarning(
                                        $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\"'s old distance sidecar could not be " +
                                        "removed - the next capture of that floor may keep some of its empty pixels empty.");
                            }
                            else Commit(Path.Combine(plan.Dir, floor.DistFile));
                        }

                        // Stage M3: the viewing copy after its picture. One that will not go in place costs only itself:
                        // the meta then names no copy, and the old one is swept as stale below.
                        if (floor.ViewStaged && !string.IsNullOrEmpty(floor.ViewFile))
                        {
                            try
                            {
                                Commit(Path.Combine(plan.Dir, floor.ViewFile));
                                entry.ViewFile = floor.ViewFile;
                                entry.ViewWidth = floor.ViewWidth;
                                entry.ViewHeight = floor.ViewHeight;
                            }
                            catch (Exception viewEx)
                            {
                                Forget(plan, floor.ViewFile);
                                Plugin.LogSource?.LogInfo(
                                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\"'s viewing copy could not be put in place " +
                                    $"({viewEx.GetType().Name}: {viewEx.Message}) - the Maps tab draws the full picture.");
                            }
                        }
                    }
                    else
                    {
                        entry = Carried(plan, floor);
                        if (entry == null) continue;

                        // Stage M3: a carried entry names its own viewing copy - kept only while it is on disk
                        CarryView(plan, entry, entry);

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

                    // Stage M3: a viewing copy the meta names is not stale; one it does not name is swept below
                    if (!string.IsNullOrEmpty(entry.ViewFile)) keep.Add(entry.ViewFile);
                }

                // The mesh, before the meta that names it and after the pictures, for the same reason
                // the sidecars go before it: the meta is the last thing a reader trusts, so a mesh it
                // names is a mesh that is already there.
                //
                // Three cases, in order:
                //   - this capture BUILT one: it is checked against the floors below, committed, and
                //     named - by accumulating onto the mesh and index an earlier capture left
                //     (MapMeshBuilder.Request.Base), or from scratch when there is none it can trust. A build that
                //     changed nothing (Result.Unchanged) carries the earlier one instead, below.
                //   - this capture built NONE (the AutoCapture cost gate, or a mesh phase that failed):
                //     the one an earlier capture wrote is carried forward, when it is still on disk and
                //     its floors are this meta's floors - the same rule Carried follows for a floor's
                //     picture, because the meta is rewritten from scratch every time and a block it does
                //     not carry is a file nothing reads. See CarriedMesh.
                //   - neither: the meta names no mesh, and DropStalePictures removes any left on disk.
                //
                // The check that can fail: a mesh whose bands are not the floors this meta names is a
                // mesh the checker refuses and the viewer could not peel, so it is dropped here - with
                // the numbers in the line - rather than shipped. Normally they are the same set by
                // construction: both come from WillBeNamed.
                if (plan.Mesh != null && !SameLevels(plan.MeshLevels, floors))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the 3D mesh of {plan.Key} was thrown away - its relief covers floor(s) " +
                        $"[{Levels(plan.MeshLevels)}] while the meta names [{Levels(floors)}], and a mesh whose " +
                        "bands are not the picture's floors cannot be drawn.");

                    Forget(plan, plan.Mesh.File);
                    plan.Mesh = null;
                    plan.MeshNote = null;
                }

                var mesh = plan.Mesh ?? CarriedMesh(plan, floors);
                List<CaptureAtlas> atlas = null;

                if (plan.Mesh != null)
                {
                    // Its OWN try, unlike the pictures': by this line every picture is committed and the
                    // meta is the only thing left to write, so a .bin that will not move - held open by
                    // the Maps tab, an antivirus or a host sync - must not take the meta down with it.
                    // The capture then has its pictures and no mesh, which is a state everything
                    // downstream already handles.
                    try
                    {
                        Commit(Path.Combine(plan.Dir, plan.Mesh.File));

                        // WP2: the sidecar right after the mesh it describes. One that will not go in place - or a mesh
                        // that has none - leaves no sidecar at all: the old one's sha no longer matches the mesh, and the
                        // next capture rebuilds from scratch rather than trusting it.
                        var indexPath = Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key));

                        try
                        {
                            if (plan.IndexStaged) Commit(indexPath);
                            else DeleteOrWarn(indexPath);
                        }
                        catch (Exception indexEx)
                        {
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: {plan.Key}'s mesh sidecar could not be put in place ({indexEx.GetType().Name}: " +
                                $"{indexEx.Message}) - the next capture rebuilds the 3D mesh from scratch.");
                            Forget(plan, MapMeshIndex.FileNameFor(plan.Key));
                            DeleteOrWarn(indexPath);
                        }

                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: mesh for {plan.Key} written - {plan.MeshNote}.");

                        // WP2 (2.12): the one-shot rebuild is spent once a mesh is in place
                        if (ModSettings.MeshRebuildNext != null && ModSettings.MeshRebuildNext.Value)
                            ModSettings.MeshRebuildNext.Value = false;

                        // Stage W: the mesh's atlas pages, each in its own try; the first that will not go in
                        // place ends the list, so the meta names pages 0..n-1 and nothing past a hole.
                        atlas = new List<CaptureAtlas>();

                        foreach (var page in plan.Atlas)
                        {
                            try
                            {
                                if (atlas.Count != page.Page) break;
                                Commit(Path.Combine(plan.Dir, page.File));
                                atlas.Add(page);
                            }
                            catch (Exception pageEx)
                            {
                                Plugin.LogSource?.LogWarning(
                                    $"QuestTree: {plan.Key}'s atlas page {page.Page} could not be put in place " +
                                    $"({pageEx.GetType().Name}: {pageEx.Message}) - the buildings on it and later pages " +
                                    "keep the side views.");
                                break;
                            }
                        }

                        if (atlas.Count == 0) atlas = null;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {plan.Key}'s 3D mesh could not be put in place ({ex.GetType().Name}: " +
                            $"{ex.Message}) - the pictures and the meta are written without it.");

                        Forget(plan, plan.Mesh.File);
                        plan.Mesh = null;
                        plan.MeshNote = null;
                        mesh = CarriedMesh(plan, floors);
                    }
                }

                if (mesh != null && plan.Mesh == null) atlas = CarriedAtlas(plan);

                // WP2 (fixes 2): the sidecar updated beside a carried mesh - committed only when that mesh is the one named
                if (plan.IndexStagedAlone)
                {
                    try
                    {
                        if (mesh != null && plan.Mesh == null) Commit(Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key)));
                        else Forget(plan, MapMeshIndex.FileNameFor(plan.Key));
                    }
                    catch (Exception ex)
                    {
                        Forget(plan, MapMeshIndex.FileNameFor(plan.Key));
                        Plugin.LogSource?.LogDebug($"QuestTree: {plan.Key}'s updated sidecar could not be put in place ({ex.Message}).");
                    }
                }

                if (atlas != null)
                    foreach (var page in atlas)
                        keep.Add(page.File);

                if (mesh != null)
                {
                    keep.Add(mesh.File);

                    if (plan.Mesh == null)
                        Plugin.LogSource?.LogDebug(
                            $"QuestTree: {plan.Key} built no 3D mesh this time, so the one an earlier capture " +
                            $"wrote ({mesh.File}, {mesh.Bytes} bytes) is kept and the meta goes on naming it.");
                }

                // The side views, after the mesh and before the meta, by the same rule: each committed in
                // its own try, and the files named added to keep so the stale sweep below removes exactly
                // the side files this meta no longer names.
                var sides = CommitSides(plan, keep);

                var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

                // Lighting stage 2: read in its own try, out here, because a game update that removes a type
                // ReadLighting names (TOD_Sky, PrismEffects, LevelSettings, ...) makes Mono throw when it compiles
                // that method, before any try inside it runs - and without this one the catch around this whole
                // method would take it, and the capture would keep its pictures but lose its meta. The light is
                // optional; the meta is not.
                CaptureLighting lighting = null;
                try
                {
                    // Stage M2b: a menu-rig capture describes the rig it was lit by, not the (absent) raid's light
                    lighting = _rig != null ? MenuLighting(plan.Key) : ReadLighting();
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogDebug($"QuestTree: the raid's light could not be read ({ex.GetType().Name}: {ex.Message}) - the capture carries none.");
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
                    CapturedAt = now,
                    FirstCapturedAt = string.IsNullOrEmpty(plan.FirstCapturedAt) ? now : plan.FirstCapturedAt,
                    Captures = plan.Captures,
                    ModVersion = ModInfo.Stamp,
                    Render = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.Render : RenderTag,
                    TimeOfDay = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.TimeOfDay : TimeOfDay(),
                    CapturedIn = plan.MenuMode || plan.IntoMenuSet ? MenuSetMarker : null,
                    Lighting = plan.IntoMenuSet && plan.Previous != null ? plan.Previous.Lighting : lighting,
                    Floors = floors,
                    Labels = plan.Labels,
                    Mesh = mesh,
                    Atlas = mesh != null ? atlas : null,
                    Sides = sides,

                    // Campaign speed step 3: this capture's point when its mesh stage completed into the mesh named here - a
                    // new file, or the stored one carried because nothing changed; a mesh carried because none was built
                    // keeps the earlier points without this one
                    Stands = mesh == null
                        ? null
                        : Stood(plan.Previous?.Stands, plan, plan.Mesh != null || plan.MeshCarriedUnchanged,
                            plan.Mesh == null || plan.MeshAccumulated),
                };

                var json = JsonConvert.SerializeObject(meta, Formatting.Indented);
                var path = Path.Combine(plan.Dir, $"{plan.Key}.map.json");
                var temp = path + ".tmp";

                File.WriteAllText(temp, json);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                metaWritten = true;

                // Stage M3 (review): the replace is done - only the newest backups of this map are kept
                if (setAside != null) PruneSetAside(plan);

                // WP3: in the frame the meta lands - a commit that changed what the meta DESCRIBES (extent, scale, floors,
                // mesh, pages, side geometry) stops an upload of the older set at its next item rather than mixing the two.
                var shape = ShapeSignature(meta);
                if (!_shapes.TryGetValue(plan.Key, out var oldShape) || !string.Equals(oldShape, shape, StringComparison.Ordinal))
                {
                    Bump(plan.Key, shape: true);
                    _shapes[plan.Key] = shape;
                }

                DropStalePictures(plan, keep);

                // Stage M3: the Maps tab's result line
                if (_menu != null)
                {
                    var views = floors.Count(fl => !string.IsNullOrEmpty(fl.ViewFile));
                    _menu.Written =
                        $"{written.Count.ToString(CultureInfo.InvariantCulture)} floor(s) at {Ppm(plan.Ppm)} px/m " +
                        $"({plan.WidthPx.ToString(CultureInfo.InvariantCulture)}x{plan.HeightPx.ToString(CultureInfo.InvariantCulture)} px" +
                        (views > 0 ? $", {views.ToString(CultureInfo.InvariantCulture)} viewing cop{(views == 1 ? "y" : "ies")}" : "") + ")" +
                        (mesh != null ? ", a 3D mesh" : "") +
                        (sides != null && sides.Count > 0 ? $", {sides.Count.ToString(CultureInfo.InvariantCulture)} side view(s)" : "");
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: capture of {plan.Key} written - {written.Count} floor(s)" +
                    (unchanged > 0 ? $" ({unchanged} unchanged, not rewritten)" : "") + $", {plan.Bytes} bytes, " +
                    (carried > 0 ? $"{carried} floor(s) kept from an earlier capture, " : "") +
                    (sides != null
                        ? $"{sides.Count} side view(s)" +
                          (plan.SidesCarried > 0 ? $" ({plan.SidesCarried} kept from an earlier capture)" : "") +
                          (plan.SideBytes > 0 ? $", {plan.SideBytes} bytes new" : "") + ", "
                        : "") +
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
                // this one, so an upload outlives the raid the capture was taken in. While a campaign or
                // automatic capture holds this map (WP3) the capture is recorded as owed instead, and the
                // hold's release uploads it once.
                // Stage M2b: a menu capture of a real key is offered like any other - RunMenuCapture holds the map's uploads
                // around the capture, so this call is recorded as owed and the hold's release uploads it once. A "-menu"
                // test set (or the switch off) is never offered.
                if (plan.MenuMode && !MenuUploads(plan.Key))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the menu capture of {plan.Key} is not offered to the host (" +
                        (MenuCaptureUploads ? $"a '{MenuCaptureKeySuffix}' test set is never uploaded" : "MenuCaptureUploads is off") + ").");
                }
                else
                {
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
            }
            catch (Exception ex)
            {
                // WP3: a half-committed capture is, conservatively, a change of shape.
                Bump(plan.Key, shape: true);

                // Stage M3 (review): Replace moved the old set aside and this write failed before its meta - the folder
                // holds part of a set nothing describes. This capture's files go and the old set comes back.
                if (setAside != null && !metaWritten)
                {
                    if (_menu != null) _menu.WriteFailed = $"{ex.GetType().Name}: {ex.Message}";

                    var restored = RestoreSetAside(plan, setAside);
                    if (_menu != null) _menu.SetAsideRestored = restored;

                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the menu capture of {plan.Key} failed while it was written ({ex.GetType().Name}: {ex.Message}) - " +
                        (restored
                            ? $"its files were removed and the old set was put back from captures\\{setAside}."
                            : $"the old set could NOT be put back; it is in captures\\{setAside} (the warnings above say what is left)."));
                    return;
                }

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture of {plan.Key} has its pictures but no meta file " +
                    $"({ex.GetType().Name}: {ex.Message}) - it will be ignored until it is captured again.");
            }
        }

        /// <summary>Stage M3 (review): how many of a map's Replace backups are kept - the newest ones; older ones are deleted
        /// after a replace has written its meta.</summary>
        internal const int SetAsideKept = 2;

        /// <summary>Stage M3 (review): the map's backup folders (captures/&lt;key&gt;.bak-*), newest first - the local time
        /// stamp in the name sorts as the time does, and a same-second "-2" sorts after its first.</summary>
        /// <param name="root">The captures folder.</param>
        /// <param name="key">The map key.</param>
        private static List<string> SetAsideFolders(string root, string key) =>
            Directory.GetDirectories(root, key + SetAsideInfix + "*")
                .Where(dir => (Path.GetFileName(dir) ?? "").StartsWith(key + SetAsideInfix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Stage M3 (review): deletes all but the newest <see cref="SetAsideKept"/> backups of this map, one line each.
        /// A backup that will not delete is left and said. Never throws.</summary>
        /// <param name="plan">The capture's plan.</param>
        private static void PruneSetAside(Plan plan)
        {
            try
            {
                var root = Path.GetDirectoryName(plan.Dir);
                if (string.IsNullOrEmpty(root)) return;

                foreach (var old in SetAsideFolders(root, plan.Key).Skip(SetAsideKept))
                {
                    try
                    {
                        Directory.Delete(old, recursive: true);
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: {plan.Key} - deleted the older backup captures\\{Path.GetFileName(old)} (the newest " +
                            $"{SetAsideKept.ToString(CultureInfo.InvariantCulture)} are kept).");
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {plan.Key} - the older backup captures\\{Path.GetFileName(old)} could not be deleted ({ex.Message}).");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: {plan.Key}'s older backups could not be listed ({ex.Message}).");
            }
        }

        /// <summary>Stage M3 (review): undoes a Replace whose write failed before its meta: every file now in the map's folder
        /// except staged temporaries (Cleanup drops those) and the campaign journal is deleted - all of it this capture's,
        /// since <see cref="SetStoredAside"/> emptied the folder - then every file of the backup is moved back and the empty
        /// backup folder removed. True when everything went back. Logs each step that fails. Never throws.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="aside">The backup folder's name.</param>
        private static bool RestoreSetAside(Plan plan, string aside)
        {
            var ok = true;

            try
            {
                var root = Path.GetDirectoryName(plan.Dir) ?? plan.Dir;
                var backup = Path.Combine(root, aside);
                var removed = 0;

                foreach (var file in Directory.GetFiles(plan.Dir))
                {
                    var name = Path.GetFileName(file) ?? "";
                    if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf(".tmp.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Plugin.LogSource?.LogWarning($"QuestTree: {plan.Key} - this capture's {name} could not be removed ({ex.Message}).");
                    }
                }

                var back = 0;

                foreach (var file in Directory.GetFiles(backup))
                {
                    var to = Path.Combine(plan.Dir, Path.GetFileName(file));

                    try
                    {
                        if (File.Exists(to))
                        {
                            ok = false;
                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: {plan.Key} - {Path.GetFileName(file)} was not moved back: a file of that name is in the way.");
                            continue;
                        }

                        File.Move(file, to);
                        back++;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {plan.Key} - {Path.GetFileName(file)} could not be moved back from captures\\{aside} ({ex.Message}).");
                    }
                }

                if (ok)
                {
                    try
                    {
                        Directory.Delete(backup, recursive: false);
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: the empty backup captures\\{aside} could not be removed ({ex.Message}).");
                    }
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} - Replace rolled back: {removed.ToString(CultureInfo.InvariantCulture)} file(s) of the " +
                    $"failed capture removed, {back.ToString(CultureInfo.InvariantCulture)} file(s) of the old set moved back from " +
                    $"captures\\{aside}{(ok ? "" : " - NOT complete, see the warnings above")}.");
                return ok;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: {plan.Key} - the Replace could not be rolled back ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

        /// <summary>Stage M3: gives <paramref name="entry"/> the viewing copy <paramref name="stored"/> names, when it is a plain
        /// file name still on disk - else none, so the meta never names a copy that is not there (the reader would fall
        /// back anyway, but a meta that says what is true is what check-capture.py checks).</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="entry">The entry the meta will carry.</param>
        /// <param name="stored">The previous meta's entry for the floor, or null.</param>
        private static void CarryView(Plan plan, CaptureFloor entry, CaptureFloor stored)
        {
            if (entry == null) return;

            var file = stored?.ViewFile;
            var ok = false;

            try
            {
                ok = !string.IsNullOrEmpty(file) && IsPlainFileName(file) && stored.ViewWidth > 0 && stored.ViewHeight > 0 &&
                     File.Exists(Path.Combine(plan.Dir, file));
            }
            catch (Exception)
            {
                ok = false;
            }

            entry.ViewFile = ok ? file : null;
            entry.ViewWidth = ok ? stored.ViewWidth : null;
            entry.ViewHeight = ok ? stored.ViewHeight : null;
        }

        /// <summary>Stage M3: Replace's backup - every file of the stored set in the map's folder (everything but this
        /// capture's staged temporaries and the campaign journal, which is history rather than the set) is moved into a new
        /// folder captures/&lt;key&gt;.bak-&lt;local time&gt;/ beside it. A move that fails part way moves back what it had
        /// moved, and the capture is not written. True with no folder when nothing was stored.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="aside">The backup folder's name, or null when nothing was stored.</param>
        /// <param name="bytes">What the moved set weighs.</param>
        /// <param name="why">Why the set could not be moved.</param>
        private static bool SetStoredAside(Plan plan, out string aside, out long bytes, out string why)
        {
            aside = null;
            why = null;
            bytes = 0;

            var moved = new List<(string From, string To)>();

            try
            {
                var files = Directory.GetFiles(plan.Dir)
                    .Where(path =>
                    {
                        var name = Path.GetFileName(path) ?? "";
                        return !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                               name.IndexOf(".tmp.", StringComparison.OrdinalIgnoreCase) < 0 &&
                               !name.EndsWith(JournalSuffix, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (files.Count == 0)
                {
                    Plugin.LogSource?.LogInfo($"QuestTree: {plan.Key} - Replace: no stored set to move aside.");
                    return true;
                }

                var root = Path.GetDirectoryName(plan.Dir) ?? plan.Dir;
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var name0 = plan.Key + SetAsideInfix + stamp;
                var target = Path.Combine(root, name0);

                for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
                    target = Path.Combine(root, $"{name0}-{n.ToString(CultureInfo.InvariantCulture)}");

                Directory.CreateDirectory(target);

                foreach (var file in files)
                {
                    var to = Path.Combine(target, Path.GetFileName(file));
                    var length = new FileInfo(file).Length;
                    File.Move(file, to);
                    moved.Add((file, to));
                    bytes += length;
                }

                aside = Path.GetFileName(target);
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: {plan.Key} - Replace: the stored set ({moved.Count.ToString(CultureInfo.InvariantCulture)} file(s), " +
                    $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes) was moved aside to captures\\{aside}\\ before the new set " +
                    "is written. To restore it: with the game closed, delete the map's folder and rename that one back to " +
                    $"{plan.Key}.");
                return true;
            }
            catch (Exception ex)
            {
                why = $"{ex.GetType().Name}: {ex.Message}";

                // Back, newest first, so the folder is as it was.
                for (var i = moved.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        File.Move(moved[i].To, moved[i].From);
                    }
                    catch (Exception back)
                    {
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: {Path.GetFileName(moved[i].From)} could not be moved back from the backup ({back.Message}) - " +
                            $"it is in {Path.GetDirectoryName(moved[i].To)}.");
                    }
                }

                return false;
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
                // captures would otherwise be named by this meta and draw as nothing. Campaign speed step 2: or HELD, which
                // a checkpoint writes with the meta that names it.
                return PictureExists(plan, stored.File) ? stored : null;
            }

            return null;
        }

        /// <summary>Removes pictures left by an earlier capture of this map that the new meta does not
        /// name - a floor that has since merged into another, or a level that renumbered - AND a 3D mesh
        /// the new meta has stopped naming. Run only after the new meta is safely down, so a failure
        /// above never deletes a working set.
        ///
        /// The mesh belongs here and not in a sweep of its own because the rule is the same one: a file
        /// the meta does not name is a file nothing reads, and leaving a megabyte of geometry from a
        /// capture whose extent or floors have moved on is how a folder grows things that look current
        /// and are not. The packaging gates call an unnamed mesh an orphan for the same reason.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="keep">The file names the new meta accounts for - every named picture and its
        /// distance sidecar, which is NOT stale: it is what the next capture merges against, and the
        /// glob below matches it as well as the pictures - plus the mesh, when one is named.</param>
        private static void DropStalePictures(Plan plan, HashSet<string> keep)
        {
            try
            {
                foreach (var file in Directory.GetFiles(plan.Dir, $"{plan.Key}-*.png"))
                {
                    var name = Path.GetFileName(file);
                    if (keep.Contains(name)) continue;

                    // WP2: MeshVerifyLastStop's pages are a debug comparison's, never named by a meta
                    if (name.IndexOf("-verify-atlas-", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    File.Delete(file);
                    Plugin.LogSource?.LogDebug($"QuestTree: removed {name}, which this capture of {plan.Key} has no floor for.");
                }

                foreach (var file in Directory.GetFiles(plan.Dir, MapMeshFile.FileNameFor("*")))
                {
                    var name = Path.GetFileName(file);
                    if (!MapMeshFile.IsMeshFileName(name) || keep.Contains(name)) continue;

                    File.Delete(file);
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: removed {name}, a 3D mesh this capture of {plan.Key} no longer names.");
                }

                // WP2: a sidecar with no mesh beside it describes nothing
                var index = Path.Combine(plan.Dir, MapMeshIndex.FileNameFor(plan.Key));
                if (!keep.Any(MapMeshFile.IsMeshFileName) && File.Exists(index))
                {
                    File.Delete(index);
                    Plugin.LogSource?.LogDebug($"QuestTree: removed {Path.GetFileName(index)}, the sidecar of a mesh {plan.Key} no longer names.");
                }

                // WP2 (fixes 4): what an interrupted commit left under .old and the load did not put back - this map's only
                foreach (var file in Directory.GetFiles(plan.Dir, "*" + OldSuffix))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith(plan.Key + "-", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith(plan.Key + ".", StringComparison.OrdinalIgnoreCase)) continue;

                    File.Delete(file);
                    Plugin.LogSource?.LogDebug($"QuestTree: removed {name}, left by an interrupted commit.");
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
            // Stage M2b: a menu-rig capture has no raid clock - its light is the rig's, named as such
            if (_rig != null) return "menu";

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

        /// <summary>Stage M2b: the lighting block of a menu-rig capture - the rig it was lit by (<see cref="BuildMenuRig"/>),
        /// with its flat ambient as harmonics - and one line saying what the 3D view will make of it.</summary>
        /// <param name="key">The capture's key, for the line.</param>
        private CaptureLighting MenuLighting(string key)
        {
            var rig = _rig;
            float[] Rgb(Color c) => FiniteOrNull(new[] { c.r, c.g, c.b });

            // The flat ambient as harmonics, by Unity's own convention (AddAmbientLight puts the colour in the L0 terms), so
            // the 3D view's convention check - EFT's top formula sh1 + sh0 - sh6 - sh8 against Unity's Evaluate - reads it
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(rig.Ambient);

            var coefficients = new float[27];
            for (var c = 0; c < 3; c++)
                for (var i = 0; i < 9; i++)
                    coefficients[c * 9 + i] = sh[c, i];

            var l = new CaptureLighting
            {
                Source = MenuRigTag,
                SunDirection = FiniteOrNull(new[] { rig.TowardsSun.x, rig.TowardsSun.y, rig.TowardsSun.z }),
                SunColor = Rgb(rig.SunColour),
                SunIntensity = Finite(MenuSunIntensity),
                SunShadowStrength = Finite(MenuSunShadowStrength),

                // The viewer refuses a night sun; the rig is a day's
                IsDay = true,
                Fogginess = 0f,
                AmbientSh = FiniteOrNull(coefficients),
                LevelSunColor = rig.FromLevel ? Rgb(rig.SunColour) : null,
                Fog = false,
                AmbientMode = AmbientMode.Flat.ToString(),
                AmbientIntensity = 1f,
                ColorSpace = QualitySettings.activeColorSpace.ToString(),
            };

            // What the 3D view will make of it (Map3DView.ResolveLight/ResolveAmbient's rules, restated): its sun where the
            // block's elevation is inside 15..55 and the intensity over 0.05 on a day sun, else its preset; the ambient's top
            // by Unity's evaluation, which must equal EFT's top formula (the view's convention check) to be drawn.
            var up = new Color[1];
            sh.Evaluate(new[] { Vector3.up }, up);
            var eftTop = new Color(sh[0, 1] + sh[0, 0] - sh[0, 6] - sh[0, 8], sh[1, 1] + sh[1, 0] - sh[1, 6] - sh[1, 8],
                sh[2, 1] + sh[2, 0] - sh[2, 6] - sh[2, 8], 1f);
            var elevation = Mathf.Asin(Mathf.Clamp(rig.TowardsSun.y, -1f, 1f)) * Mathf.Rad2Deg;
            var azimuth = Mathf.Atan2(rig.TowardsSun.x, rig.TowardsSun.z) * Mathf.Rad2Deg;
            var sunUsed = elevation >= 15f && MenuSunIntensity >= 0.05f && l.SunColor != null && l.SunDirection != null;
            var sunTop = Mathf.Max(0.0001f, MenuSunIntensity * rig.SunColour.maxColorComponent);

            Plugin.LogSource?.LogInfo(
                $"QuestTree: {key}'s lighting block describes the menu rig - sun towards {V3(rig.TowardsSun)} (elevation " +
                $"{F(elevation)}, azimuth {F(azimuth < 0 ? azimuth + 360f : azimuth)}), colour {Rgb3(rig.SunColour)}, intensity " +
                $"{F(MenuSunIntensity)}, shadow strength {F(MenuSunShadowStrength)}, day, time 'menu', ambient flat " +
                $"{Rgb3(rig.Ambient)} as harmonics. The 3D view will use: " +
                (sunUsed
                    ? $"the captured sun at {F(Mathf.Min(elevation, 55f))} deg{(elevation > 55f ? " (clamped from " + F(elevation) + ")" : "")}"
                    : "its preset sun (the block's sun fails its checks)") +
                $", the ambient's top {Rgb3(up[0])} by Unity's evaluation against {Rgb3(eftTop)} by EFT's formula (" +
                $"{(Mathf.Abs(up[0].maxColorComponent - eftTop.maxColorComponent) <= 0.02f * Mathf.Max(0.0001f, eftTop.maxColorComponent) ? "passes" : "FAILS")} " +
                $"the convention check), {F(up[0].maxColorComponent / sunTop)} of the sun.");

            return l;
        }

        private static string V3(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.000}, {1:0.000}, {2:0.000})", v.x, v.y, v.z);

        /// <summary>
        /// Lighting stage 2: the raid's light, read on the main thread where the meta is written, each part in its own
        /// try so one missing type (a modded scene, a game update) costs that part and nothing else. Null when neither the
        /// game's sky nor its ambient was there (the menu, a test) - the meta then carries no block at all. The caller
        /// (WriteMeta) still wraps the call in a try of its own: a member this method names directly that a game update
        /// removed makes Mono throw when it JIT-compiles the method, which happens before any of the trys below run, so
        /// only the CALLER's try can catch that. Every number is written finite (see <see cref="Finite"/>): the
        /// server reads the meta with System.Text.Json, which refuses the "NaN" string Newtonsoft writes for a
        /// non-finite float and would fail the whole upload over one bad coefficient.
        /// </summary>
        private static CaptureLighting ReadLighting()
        {
            var l = new CaptureLighting();
            var any = false;

            float[] Rgb(Color c) => FiniteOrNull(new[] { c.r, c.g, c.b });

            try
            {
                if (MonoBehaviourSingleton<TOD_Sky>.Instantiated)
                {
                    var sky = TOD_Sky.Instance;
                    var light = sky != null && sky.Components != null ? sky.Components.LightSource : null;

                    if (light != null)
                    {
                        // TOD's light source: the sun by day, the MOON by night, never below TOD's minimum height
                        var towards = -light.transform.forward;
                        l.SunDirection = FiniteOrNull(new[] { towards.x, towards.y, towards.z });
                        l.SunColor = Rgb(light.color);
                        l.SunIntensity = Finite(light.intensity);
                        l.SunShadowStrength = Finite(light.shadowStrength);
                        any = true;
                    }

                    if (sky != null)
                    {
                        l.Source = "TOD_Sky";
                        l.IsDay = sky.IsDay;
                        l.Fogginess = Finite(sky.Atmosphere != null ? sky.Atmosphere.Fogginess : 0f);
                        l.SkyColor = Rgb(sky.SampleSkyColor());
                        l.EquatorColor = Rgb(sky.SampleEquatorColor());

                        // Without the direct-light term: with it, TOD samples along the capture camera's yaw, and the
                        // recorded horizon would be warm and bright or not by which way the player happened to face.
                        l.FogColor = Rgb(sky.SampleFogColor(false));
                        any = true;

                        // The true sun, wherever it is (under the horizon at night), in its own try: the light's
                        // direction above is the one to light by, and this one only says where the sun really was.
                        try
                        {
                            var sun = sky.SunDirection;
                            l.SunTrueDirection = FiniteOrNull(new[] { sun.x, sun.y, sun.z });
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogDebug($"QuestTree: the sky's true sun could not be read ({ex.GetType().Name}: {ex.Message}).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the raid's sun could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                var weather = EFT.Weather.WeatherController.Instance;
                var tod = weather != null ? weather.TimeOfDayController : null;

                if (tod != null)
                {
                    var sh = tod.SH;
                    var coefficients = new float[27];
                    for (var c = 0; c < 3; c++)
                        for (var i = 0; i < 9; i++)
                            coefficients[c * 9 + i] = sh[c, i];

                    l.AmbientSh = FiniteOrNull(coefficients);
                    any = any || l.AmbientSh != null;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the raid's ambient could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                l.Fog = RenderSettings.fog;
                l.FogMode = RenderSettings.fogMode.ToString();
                l.FogDensity = Finite(RenderSettings.fogDensity);
                l.FogStart = Finite(RenderSettings.fogStartDistance);
                l.FogEnd = Finite(RenderSettings.fogEndDistance);
                l.RenderFogColor = Rgb(RenderSettings.fogColor);
                l.AmbientMode = RenderSettings.ambientMode.ToString();
                l.AmbientIntensity = Finite(RenderSettings.ambientIntensity);
                l.ColorSpace = QualitySettings.activeColorSpace.ToString();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the render settings could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                var settings = UnityEngine.Object.FindObjectOfType<LevelSettings>();
                if (settings != null) l.LevelSunColor = Rgb(settings.SunColor);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the level settings could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            try
            {
                var camera = EFT.CameraControl.CameraManager.Instance?.Camera;
                var prism = camera != null ? camera.GetComponent<PrismEffects>() : null;

                if (prism != null)
                {
                    l.PrismTonemap = prism.useTonemap ? prism.tonemapType.ToString() : "";
                    l.PrismExposure = prism.useExposure;
                    l.PrismMiddleGrey = Finite(prism.exposureMiddleGrey);
                    l.PrismGamma = Finite(prism.useGammaCorrection ? prism.gammaValue : 0f);
                    l.PrismLut = prism.useLut && prism.twoDLookupTex != null ? prism.twoDLookupTex.name ?? "" : "";
                }

                // the PostProcessing v2 volume, by reflection: the mod does not reference its assembly (stage 4 may)
                var volume = camera != null ? camera.GetComponent("PostProcessVolume") : null;
                if (volume != null)
                {
                    // Reading "profile" CLONES the shared profile the first time (Unity's material rule), so it is read
                    // only when the volume already holds its own instance; otherwise the shared one is what renders.
                    var type = volume.GetType();
                    var instantiated = type.GetMethod("HasInstantiatedProfile", Type.EmptyTypes)?.Invoke(volume, null) as bool? ?? false;
                    var profile = instantiated ? type.GetProperty("profile")?.GetValue(volume) : type.GetField("sharedProfile")?.GetValue(volume);
                    var list = profile?.GetType().GetField("settings")?.GetValue(profile) as System.Collections.IEnumerable;

                    if (list != null)
                        foreach (var effect in list)
                        {
                            if (effect == null || l.PostProcess.Count >= 32) continue;

                            // ParameterOverride<T>.value is a public FIELD in the game's PostProcessing, so a property
                            // lookup alone read every effect as off; the property stays as the fallback.
                            var enabled = effect.GetType().GetField("enabled")?.GetValue(effect);
                            var enabledType = enabled?.GetType();
                            var on = (enabledType?.GetField("value")?.GetValue(enabled) ?? enabledType?.GetProperty("value")?.GetValue(enabled)) as bool? ?? false;
                            var active = effect.GetType().GetField("active")?.GetValue(effect) as bool? ?? true;
                            l.PostProcess.Add($"{effect.GetType().Name}:{(on && active ? "on" : "off")}");
                        }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the camera's post-processing could not be read ({ex.GetType().Name}: {ex.Message}).");
            }

            return any ? l : null;
        }

        /// <summary>Lighting stage 2: a float the host can read - NaN and infinity become 0, because Newtonsoft writes
        /// them as the string "NaN", which the server's System.Text.Json refuses for a float, failing the whole upload.</summary>
        private static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;

        /// <summary>Lighting stage 2: the array, or null when any element is NaN or infinite - a colour, a direction or
        /// harmonics with a hole in them are not ones to light by, and null is what every reader already handles.</summary>
        private static float[] FiniteOrNull(float[] values)
        {
            foreach (var v in values)
                if (float.IsNaN(v) || float.IsInfinity(v)) return null;
            return values;
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
            // The position checks FIRST (review F14): a label rejected for its place must not claim its text
            // and silence a later, valid one of the same name.
            if (float.IsNaN(at.x) || float.IsNaN(at.z) || float.IsInfinity(at.x) || float.IsInfinity(at.z)) return;
            if (at.x < plan.Extent.MinX || at.x > plan.Extent.MaxX) return;
            if (at.z < plan.Extent.MinZ || at.z > plan.Extent.MaxZ) return;

            if (!seen.Add(text)) return;

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
            // Stage M2: a menu capture has no GameWorld - its session carries the location's Id (what the raid's
            // Player.Location and GameWorld.LocationId spell) plus MenuCaptureKeySuffix: "bigmap-menu", its own folder.
            // Prepare still holds it to IsUsableKey.
            if (_menu != null) return _menu.Key;

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
        internal static bool IsUsableKey(string key)
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

        /// <summary>The name of the per-map campaign journal, beside the meta.</summary>
        private const string JournalSuffix = ".campaign.txt";

        /// <summary>How many campaign runs the journal keeps, INCLUDING the one being written. Twenty:
        /// enough to see whether a map has always been awkward or has just started being, small enough
        /// that the file stays a few kilobytes and can be pasted whole into a report.</summary>
        private const int JournalRuns = 20;

        /// <summary>The line that starts a run in the journal. Counted to trim the file, so it has to be
        /// something no reason line can begin with.</summary>
        private const string JournalRunMark = "=== ";

        /// <summary>
        /// Appends one line to a map's campaign journal, <c>captures/&lt;key&gt;/&lt;key&gt;.campaign.txt</c>.
        ///
        /// It exists because the evidence kept being lost. A campaign reports what it did in the game log,
        /// and a game log is gone the moment the game is restarted - the run that captured 11 of 16 stops
        /// had nothing left to say why by the time anybody looked. The journal is small, per map, and next
        /// to the pictures it describes, so it survives the game and travels with the capture folder.
        ///
        /// Nothing reads it but a person. It is not in the meta, nothing validates it, and the uploader
        /// and the packager both ignore it - see DropStalePictures, which keeps only the files the meta
        /// names and matches PNGs alone.
        ///
        /// Never throws: a campaign must not fail because a text file would not open.
        /// </summary>
        /// <param name="map">The map's internal name, which is also its capture folder.</param>
        /// <param name="line">One line, without a newline, in whatever words the caller used in the log.</param>
        /// <param name="startsRun">True for the line that begins a campaign, which is what the trimming
        /// counts and what carries the timestamp.</param>
        internal static void Journal(string map, string line, bool startsRun = false)
        {
            try
            {
                if (string.IsNullOrEmpty(map) || string.IsNullOrEmpty(line)) return;
                if (!IsUsableKey(map)) return;

                var dir = CaptureDir(map);
                if (dir == null) return;

                var path = Path.Combine(dir, map + JournalSuffix);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                var entry = startsRun
                    ? $"{JournalRunMark}{stamp} {line}"
                    : $"    {stamp} {line}";

                var kept = new List<string>();

                if (File.Exists(path))
                {
                    var existing = File.ReadAllLines(path);

                    // Trimmed by RUNS, not by lines: a campaign writes as many lines as it had stops, so
                    // a line budget would keep a different number of runs on every map.
                    //
                    // How many OLD runs may stay depends on what is being appended. A line that starts a
                    // run makes the file hold one more than it did, so nineteen old ones plus this one is
                    // the twenty the constant promises - keeping twenty and adding a start wrote
                    // twenty-one. A continuation line belongs to the run already at the end of the file
                    // and adds none, so all twenty stay.
                    var keepRuns = startsRun ? JournalRuns - 1 : JournalRuns;
                    var runs = 0;
                    var from = keepRuns > 0 ? 0 : existing.Length;

                    if (keepRuns > 0)
                    {
                        for (var i = existing.Length - 1; i >= 0; i--)
                        {
                            if (!existing[i].StartsWith(JournalRunMark, StringComparison.Ordinal)) continue;

                            runs++;
                            if (runs < keepRuns) continue;

                            from = i;
                            break;
                        }
                    }

                    for (var i = from; i < existing.Length; i++) kept.Add(existing[i]);
                }

                kept.Add(entry);
                File.WriteAllLines(path, kept.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the campaign journal for {map} could not be written ({ex.GetType().Name}: " +
                    $"{ex.Message}) - the log line above is all there is.");
            }
        }

        /// <summary>BepInEx/plugins/QuestTree/captures/&lt;key&gt;/, created on demand. Null when the
        /// plugin has no file location - the case KappaQuests and the experiment both guard.</summary>
        /// <param name="key">The map's internal name.</param>
        private static string CaptureDir(string key)
        {
            try
            {
                var root = CapturesRootDir();
                if (root == null) return null;

                var dir = Path.Combine(root, key);
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

        /// <summary>BepInEx/plugins/QuestTree/captures/ itself, created on demand. Null when the plugin
        /// has no file location - the case KappaQuests and the experiment both guard.
        ///
        /// Split out of <see cref="CaptureDir"/> rather than written twice, because the mesh probe
        /// writes ONE text file beside the capture folders and a second copy of the path rule is a
        /// second place for it to drift.</summary>
        private static string CapturesRootDir()
        {
            var modPath = Path.GetDirectoryName(typeof(MapCapture).Assembly.Location);
            if (string.IsNullOrEmpty(modPath))
            {
                Plugin.LogSource?.LogWarning(
                    "QuestTree: the plugin has no file location, so a map capture cannot be written.");
                return null;
            }

            var dir = Path.Combine(modPath, "captures");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // --- THROWAWAY, with QuestGraph/MeshProbe.cs -------------------------------------------------
        //
        // Three accessors the Phase 3-0 mesh probe reads, and nothing else in the mod does. They exist
        // so the probe measures what a CAPTURE would do rather than a second opinion of it: the same
        // layer mask, the same camera height rule, the same folder. DELETE all three with MeshProbe.cs.

        /// <summary>THROWAWAY: the folder the mesh probe's text file goes in. Delete with
        /// QuestGraph/MeshProbe.cs.</summary>
        internal static string ProbeCapturesRoot()
        {
            try
            {
                return CapturesRootDir();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the captures folder could not be made ({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        /// <summary>THROWAWAY: the culling mask a capture of this raid would draw with, built exactly as
        /// <see cref="BuildCamera"/> builds it - the live camera's own mask, or ~0 when there is none to
        /// copy, minus <see cref="ExcludedLayerNames"/>.
        ///
        /// The once-per-session layer log line is SUPPRESSED for the probe's call and then put back the way
        /// it was found, so the line is neither printed by a diagnostic nor stolen from the capture that
        /// owes it. Saving the flag without setting it first was worse than leaving it alone: the probe's
        /// call printed the line (the flag was still false), and then the restore un-marked it, so the next
        /// capture printed the same line again. Delete with QuestGraph/MeshProbe.cs.</summary>
        internal static int ProbeCaptureMask()
        {
            var main = LiveCamera();
            var logged = _loggedLayers;

            try
            {
                _loggedLayers = true;
                return CaptureMask(main != null ? main.cullingMask : ~0);
            }
            finally
            {
                _loggedLayers = logged;
            }
        }

        /// <summary>THROWAWAY: the metres above the topmost band's maxY a capture's camera stands - see
        /// <see cref="TopBandCameraHeight"/> and <see cref="BeginFloor"/>. Read rather than copied, so
        /// the probe's raycast grid starts where the picture's camera does. Delete with
        /// QuestGraph/MeshProbe.cs.</summary>
        internal static float ProbeTopBandCameraHeight => TopBandCameraHeight;

        /// <summary>The pixels per metre a floor of this map can actually be captured in: what the
        /// resolution setting asks for, brought down in <see cref="BudgetPpmStep"/> steps until one
        /// floor's working set fits <see cref="CaptureMemoryBudgetBytes"/>.
        ///
        /// Lowering the scale rather than refusing the map, because a map captured at 2.5 px/m is a map
        /// and a map that threw an OutOfMemoryException is not. It is deterministic - the same map at the
        /// same setting always lands on the same number - which matters because the pixels per metre is
        /// part of what decides whether a later capture may be merged into this one (LoadPrevious), and
        /// two captures of one map have to agree on it without having to agree on anything else.
        ///
        /// The last step is a short one: the scale asked for is rarely a whole number of steps above the
        /// floor - the pixel cap hands out numbers like 2.197 px/m - so the walk is clamped to
        /// <see cref="MinBudgetPpm"/> instead of stopping at the last full step above it. The result is
        /// the highest step at or above the floor that fits, or the floor itself. The floor is a floor
        /// and not a promise: a map big enough that even 1 px/m is over the budget is captured at 1 px/m
        /// anyway, with the note saying so in those words, because a smudged map is worth more than a
        /// refusal - and a scale the pixel cap already hands back at or under the floor gets that note
        /// too, having never entered the walk.</summary>
        /// <param name="wanted">The scale the resolution setting and the pixel cap ask for.</param>
        /// <param name="widthM">The extent's width in metres.</param>
        /// <param name="heightM">Its height in metres.</param>
        /// <param name="budgetBytes">The working set one floor may take: <see cref="CaptureMemoryBudgetBytes"/>, or
        /// <see cref="MenuCaptureMemoryBudgetBytes"/> for a menu capture's floors (stage M2c).</param>
        /// <param name="note">A phrase for the capture header when the budget lowered the scale, or when
        /// it could not lower it far enough; null when the scale asked for fits as it is.</param>
        private static float Budget(float wanted, double widthM, double heightM, long budgetBytes, out string note)
        {
            note = null;

            var ppm = wanted;
            var first = WorkingSet(widthM, heightM, ppm);

            // Clamped to the floor rather than stopping half a step above it. The old condition was
            // "ppm - step >= MinBudgetPpm", which can only ever REACH 1 px/m from a scale that is a whole
            // number of steps above it - and the scale asked for usually is not, because the pixel cap
            // divides a long side in metres into a power of two and hands back numbers like 2.197. From
            // 2.197 the old walk went 2.197 -> 1.697 -> 1.197 and stopped there, since 0.697 is under the
            // floor. On any extent where 1.197 px/m is over the budget and 1 px/m is not - a 2900 m square
            // is 299 MiB against 208 - that abandoned the walk one step early and captured the floor over
            // budget anyway, which is the OutOfMemoryException this method exists to prevent.
            while (ppm > MinBudgetPpm && WorkingSet(widthM, heightM, ppm) > budgetBytes)
            {
                ppm = Math.Max(MinBudgetPpm, ppm - BudgetPpmStep);
            }

            var settled = WorkingSet(widthM, heightM, ppm);

            // Said FIRST, and whether or not the walk moved: a map the floor itself cannot fit is
            // captured over the budget, and that is the one outcome this method cannot make safe, so it
            // has to be in the header rather than inferred from two numbers in it. Whether or not the
            // walk moved, because a scale already at or under the floor never enters the loop at all -
            // the pixel cap hands one back for an extent over 8 km on its long side - and the test below
            // would then return with no note and nothing said.
            if (settled > budgetBytes)
            {
                note =
                    $"{Ppm(wanted)} px/m would need {Mb(first)} MB a floor and even {Ppm(ppm)} px/m, the lowest " +
                    $"scale there is, needs {Mb(settled)} MB - over the {Mb(budgetBytes)} MB budget " +
                    "even at the floor, so this is captured over budget";

                return ppm;
            }

            if (ppm >= wanted) return ppm;

            note =
                $"{Ppm(wanted)} px/m would need {Mb(first)} MB a floor, over the {Mb(budgetBytes)} MB " +
                $"budget, so {Ppm(ppm)} px/m ({Mb(settled)} MB)";

            return ppm;
        }

        /// <summary>What one floor of this map at this scale would work in, in bytes. See
        /// <see cref="WorkingSetBytesPerPixel"/> for the terms.</summary>
        /// <param name="widthM">The extent's width in metres.</param>
        /// <param name="heightM">Its height in metres.</param>
        /// <param name="ppm">Pixels per metre.</param>
        private static long WorkingSet(double widthM, double heightM, float ppm)
        {
            // The sizes the picture will actually have (PictureSide's rounding included), so the model counts
            // the pixels that are allocated - at most three more a side, never fewer.
            var width = (long)PictureSide(widthM, ppm);
            var height = (long)PictureSide(heightM, ppm);

            return width * height * WorkingSetBytesPerPixel;
        }

        /// <summary>A floor picture's side in pixels: ceil(metres x ppm), rounded UP to a multiple of
        /// <see cref="PictureBlock"/> when <see cref="AlignPictureSides"/> - so the viewer can hold it
        /// block-compressed. Rounded on the OUTPUT size; the sample size (Plan.SampleWidth) is
        /// SupersampleFactor times this, so it stays an exact multiple and every tile holds whole sample
        /// blocks. The added pixels are real ground, not padding: <see cref="PictureExtent"/> widens the
        /// extent to cover them.</summary>
        /// <param name="metres">The span in metres.</param>
        /// <param name="ppm">Pixels per metre.</param>
        private static int PictureSide(double metres, float ppm)
        {
            var px = Math.Max(1, (int)Math.Ceiling(metres * ppm));
            return AlignPictureSides ? RoundUpToBlock(px) : px;
        }

        /// <summary>A side picture's size in pixels: <see cref="MapSideView.Size"/> rounded up like
        /// <see cref="PictureSide"/>. No extent to widen here: a side is placed by its origin and its
        /// own pixels per metre (MapSideView.Pixel, the viewer's SideUv, both divide by the stored width
        /// and height), so the added pixels simply reach past the frame's high r and high u edges and
        /// every pixel keeps exactly 1/ppm metres.</summary>
        /// <param name="span">The frame's span in metres.</param>
        /// <param name="ppm">The side's pixels per metre.</param>
        private static int SidePictureSide(double span, float ppm)
        {
            var px = MapSideView.Size(span, ppm);
            return AlignPictureSides ? RoundUpToBlock(px) : px;
        }

        private static int RoundUpToBlock(int px) => (px + PictureBlock - 1) / PictureBlock * PictureBlock;

        /// <summary>
        /// The rectangle a floor picture of <paramref name="widthPx"/> x <paramref name="heightPx"/> at
        /// <paramref name="ppm"/> actually covers: the harvested one widened EAST (MaxX) and SOUTH (MinZ).
        ///
        /// Widened rather than the ppm recomputed, because one ppm cannot make both axes exact (width and
        /// height are rounded separately) and the ppm is the merge key (LoadPrevious). Those two edges,
        /// because the picture is anchored at MinX and MaxZ - the camera (PositionCamera), the develop's
        /// world positions and the self-check all count from there - so its pixels already run past MaxX
        /// and below MinZ, and this only writes down where they end. Every reader then agrees without
        /// being told: the meta's extent, the mesh and atlas files' extents (the 3D view's PlanarUv and its
        /// ExtentTolerance check) and the 2D map's bounds all stretch the picture over exactly
        /// widthPx / ppm by heightPx / ppm metres. The old ceil(metres x ppm) left up to a pixel of
        /// stretch in that; the widened rectangle leaves none.
        ///
        /// Deterministic - the same harvested extent and ppm always give the same doubles - so two
        /// captures of a map still agree on it for a merge. A copy: the probe's object is not touched.
        /// </summary>
        /// <param name="extent">The harvested extent.</param>
        /// <param name="widthPx">The picture's width in pixels.</param>
        /// <param name="heightPx">Its height in pixels.</param>
        /// <param name="ppm">Pixels per metre.</param>
        private static MapExtentDto PictureExtent(MapExtentDto extent, int widthPx, int heightPx, float ppm) =>
            new MapExtentDto
            {
                MinX = extent.MinX,
                MaxX = extent.MinX + widthPx / (double)ppm,
                MinZ = extent.MaxZ - heightPx / (double)ppm,
                MaxZ = extent.MaxZ,
                Source = extent.Source,
                Rotation = extent.Rotation,
                SampledAt = extent.SampledAt,
                Floors = extent.Floors,
            };

        private static string Mb(long bytes) =>
            (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>The long side the picture is allowed, from the setting, held to something the
        /// viewer can load whatever a hand-edited config file says: at most 16384 (rollback: 8192) and at
        /// most <see cref="QuestTree.UI.DynamicMapsLibrary.MaxPictureSide"/>, the largest picture this GPU's
        /// textures take (min(16384, SystemInfo.maxTextureSize), sized in Plugin.Awake) - a picture past it
        /// would be captured and then refused. A multiple of four when AlignPictureSides, for Prepare's rounding.
        ///
        /// The setting is the user's and is not overridden: a config saved at 8192 (the old default and
        /// the old ceiling) still asks for 8192, which holds Customs at 7.32 px/m rather than 8. The
        /// fallback, when the settings are not up, is 16384 (rollback: 8192).</summary>
        private static int Resolution()
        {
            var value = ModSettings.Ready && ModSettings.CaptureResolution != null
                ? ModSettings.CaptureResolution.Value
                : 16384;

            var limit = Math.Max(512, Math.Min(16384, QuestTree.UI.DynamicMapsLibrary.MaxPictureSide));

            var clamped = Mathf.Clamp(value, 512, limit);

            return AlignPictureSides ? clamped / PictureBlock * PictureBlock : clamped;
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

            /// <summary>The picture's size in SAMPLES rather than output pixels - the resolution the
            /// camera actually renders at, <see cref="SupersampleFactor"/> times the output in each
            /// direction. The tiling, the camera's framing and the self-check all work in this space;
            /// everything from the float buffer onwards works in output pixels.</summary>
            public int SampleWidth => WidthPx * SupersampleFactor;

            public int SampleHeight => HeightPx * SupersampleFactor;

            /// <summary>Samples per metre: what the camera is framed to, and what the self-check holds
            /// the projection against.</summary>
            public float SamplePpm => Ppm * SupersampleFactor;

            /// <summary>How reachable each cell of the map is, 255 inside the walkable area and 0 well
            /// outside it, with the ramp in between - see <see cref="BuildReach"/>. Null when there is no
            /// NavMesh to build one from, and then nothing is dimmed. One mask for every floor: it is
            /// built from the whole triangulation, which covers every band.</summary>
            public byte[] Reach;

            public int ReachCellsX;

            public int ReachCellsZ;

            /// <summary>Where the player was standing when the key was pressed, in world XZ. Every
            /// pixel's distance from here goes into the sidecar, and that is what decides whether this
            /// capture's view of a spot beats the one already on disk.</summary>
            public Vector2 From;

            /// <summary>Stage M2: a main-menu capture (<see cref="RunMenuCapture"/>) - every pixel and relief cell it writes
            /// records step 0 (<see cref="StepZero"/>), it records no stand and offers no upload. False on every raid plan.</summary>
            public bool MenuMode;

            /// <summary>Stage M2b (review): a raid capture merging into a set a menu capture wrote (LoadPrevious) - the meta
            /// keeps that set's render recipe, lighting block and time.</summary>
            public bool IntoMenuSet;

            /// <summary>Stage M2c: a raid capture LoadPrevious refused because the stored set is a menu set it may not merge
            /// into (<see cref="RaidLeavesMenuSets"/>) - Prepare stops, and nothing on disk is touched.</summary>
            public bool MenuSetGuarded;

            /// <summary>Stage M3: a menu capture's write mode (MergeOrFresh on every raid plan).</summary>
            public MenuWriteMode MenuWrite;

            /// <summary>Stage M3: why a menu capture without Replace may not merge into the stored set (Prepare stops), or
            /// null.</summary>
            public string MenuNeedsReplace;

            /// <summary>Stage M2b: the time caps this capture runs under - the raid's constants unless a menu capture set the
            /// menu's (<see cref="MenuCaptureBudgets"/>), so a raid plan reads exactly the numbers it always did. The floor
            /// phase's cap (<see cref="FloorPhaseSeconds"/> / <see cref="MenuFloorPhaseSeconds"/>) ...</summary>
            public double FloorCapSeconds = FloorPhaseSeconds;

            /// <summary>... the mesh watchdog (<see cref="MeshWatchdogSeconds"/> / <see cref="MenuMeshWatchdogSeconds"/>) ...</summary>
            public double MeshWatchdogCapSeconds = MeshWatchdogSeconds;

            /// <summary>Stage M2c: the largest PNG a FLOOR of this capture may write - <see cref="MaxFloorPngBytes"/> unless a
            /// menu capture's finer ground set <see cref="MenuMaxFloorPngBytes"/> (<see cref="MenuFineGround"/>). A side's own
            /// plan never sets it, and RecordSide keeps the raid's constant.</summary>
            public long FloorPngCapBytes = MaxFloorPngBytes;

            /// <summary>Stage M2c: how long a floor's settle waits for its managed encode - <see cref="EncodeWaitSeconds"/>
            /// unless the finer ground set <see cref="MenuEncodeWaitSeconds"/>.</summary>
            public double EncodeWaitCapSeconds = EncodeWaitSeconds;

            /// <summary>... and whether the builder's request takes <see cref="MenuBuildingSeconds"/> and
            /// <see cref="MenuAtlasSeconds"/> in place of the raid's budget arithmetic (MeshRequest).</summary>
            public bool MenuBudgets;

            /// <summary>Stage M2b: the menu capture's phases, seconds, for its closing line (the floors' are
            /// <see cref="FloorSeconds"/>).</summary>
            public double MeshSeconds;

            public double SidesSeconds;

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

            /// <summary>The culling mask the capture's camera was given (<see cref="BuildCamera"/>) -
            /// the game's own mask minus <see cref="ExcludedLayerNames"/>. Kept on the plan so the 3D
            /// mesh's building walk can filter renderers by exactly what the PICTURE drew, without a
            /// second opinion of it: Big Red's shell is on HighPolyCollider, which a list of "building
            /// layers" would never have guessed.</summary>
            public int RenderMask = ~0;

            /// <summary>The 3D mesh's file name in this capture's folder - <c>&lt;key&gt;-mesh.bin</c>,
            /// always set, whether or not this capture builds one. It has to be: the abort path
            /// (<see cref="DropStaged"/>) deletes the staged file by this name, and a capture that got
            /// far enough to stage one and then refused is exactly the case where
            /// <see cref="WantsMesh"/> says nothing useful.</summary>
            public string MeshFile;

            /// <summary>Whether this capture builds the 3D geometry at all. True for a key press and a
            /// campaign stop; for an AutoCapture tick, only when there is no earlier mesh this capture's
            /// meta could carry forward (CarriedMesh) - a mesh file on disk that the previous meta does
            /// not name, or that was built for other floors, does not count. The relief is 45 ms, but
            /// the building walk is a pass over 184,000 renderers and a handful of GPU readbacks, which
            /// is not something to spend every five seconds on a map that already has one.</summary>
            public bool WantsMesh;

            /// <summary>What the meta will say about the mesh, once it is staged - null when none was
            /// built or the write failed. Its presence is also what tells <see cref="WriteMeta"/> to
            /// commit the staged file.</summary>
            public CaptureMesh Mesh;

            /// <summary>The mesh's own log line, held from the staging to the commit so the line is
            /// printed when the file is really in place.</summary>
            public string MeshNote;

            /// <summary>Stage W: the atlas pages this capture's mesh build staged, in page order. WP2: onto a stored mesh
            /// the whole list the meta names - the pages this capture rewrote or added (staged) and the stored ones it
            /// carries (already in place).</summary>
            public List<CaptureAtlas> Atlas = new List<CaptureAtlas>();

            /// <summary>WP2: the stored mesh and sidecar this capture's build adds to (StartMeshBase), or null - with the
            /// reason in <see cref="MeshBaseRefused"/> - for the from-scratch path.</summary>
            public MeshBaseLoad MeshBase;

            public string MeshBaseRefused;

            /// <summary>WP2 (fixes): the refusal is temporary - the stored mesh is carried and no mesh is built this stop.</summary>
            public bool MeshBaseTemporary;

            /// <summary>WP2: Application.version + "|" + Application.unityVersion - the sidecar's game string.</summary>
            public string Game;

            /// <summary>WP2: the build changed nothing, so the stored mesh, sidecar and pages are carried (2.13).</summary>
            public bool MeshCarriedUnchanged;

            /// <summary>Campaign speed step 3: the mesh this capture built was built onto a stored one (MapMeshBuilder's
            /// Accumulated) - so the standing points before it still describe it (<see cref="Stood"/>).</summary>
            public bool MeshAccumulated;

            /// <summary>WP2: a sidecar was staged beside the staged mesh.</summary>
            public bool IndexStaged;

            /// <summary>WP2 (fixes 2): a sidecar was staged beside the CARRIED mesh (the mesh unchanged, a recorded
            /// attempt changed the index).</summary>
            public bool IndexStagedAlone;

            /// <summary>Set on a SIDE VIEW's own plan only: which side it is and how it is framed, which is
            /// what sends PositionCamera down the side branch. Null on the capture's plan.</summary>
            public SideView Side;

            /// <summary>Whether this capture takes the four side views. True for every key press and
            /// every campaign stop - the sides merge best-of-by-distance, so each stop adds to them; for
            /// an AutoCapture tick only when there are no earlier side views the meta could carry forward
            /// (CarriedSides) - they are four floor-sized renders, the same cost argument as the mesh's
            /// (see WantsMesh).</summary>
            public bool WantsSides;

            /// <summary>Whether the side phase actually RAN this capture - which decides between the
            /// sides it staged and the ones an earlier capture wrote (CommitSides).</summary>
            public bool SidesTaken;

            /// <summary>The side views staged this capture, as the meta will list them once they are
            /// committed.</summary>
            public readonly List<CaptureSide> Sides = new List<CaptureSide>();

            /// <summary>The side being taken right now, so Cleanup can release its buffers when a raid
            /// ends in the middle of one.</summary>
            public FloorPlan SideFloor;

            /// <summary>Bytes the staged side views take, for the capture's line.</summary>
            public long SideBytes;

            /// <summary>Campaign speed step 1 (2, review): this capture's collider tops (MapMeshBuilder.Result.Tops), or null
            /// when no mesh was built - then no side pixel is settled and every one settled before is read as 255.</summary>
            public MapMeshBuilder.ColliderTops Tops;

            /// <summary>How many of the named side views came from an earlier capture - CommitSides.</summary>
            public int SidesCarried;

            /// <summary>Wall time the floor loop took, and the pixels it rendered - what the side views'
            /// hold is estimated from, since a side renders through the same tiles.</summary>
            public double FloorSeconds;

            public long FloorPixels;

            /// <summary>The floor levels the staged mesh carries a relief band for. Checked against the
            /// floors the meta ends up naming: the mesh's bands and the meta's floors ARE the same set
            /// by construction, and a capture where they are not is one whose mesh
            /// tools/check-capture.py would refuse - so it is not written at all.</summary>
            public HashSet<int> MeshLevels;

            /// <summary>WP4 B2: the developed picture of every floor of this plan in turn (all share WidthPx x HeightPx),
            /// when ManagedPngEncode - in place of a Texture2D a floor. Kept until the last floor's encode is settled
            /// (barrier 2), then dropped. A side's own plan has its own.</summary>
            public Color32[] RgbaPool;

            /// <summary>WP4 B2: side views whose encodes are running - settled by SettleSides (barriers 3, 4, 5).</summary>
            public readonly List<SideView> PendingSides = new List<SideView>();

            /// <summary>Campaign speed step 2: the campaign's held set this capture merges into and is held into instead of
            /// written (the capture's plan and its sides' own plans alike), or null - every capture outside a campaign, and
            /// every stop of a campaign that writes every stop.</summary>
            public CampaignHold Hold;

            /// <summary>Campaign speed step 2: this stop's build went into the hold (HoldMesh) - the meta names the held mesh and
            /// this stop's atlas list.</summary>
            public bool MeshHeld;
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

            /// <summary>The pixels this floor's water quads were found at, in scan order, held only
            /// between the two passes of <see cref="Inpaint"/>. Up to a few hundred thousand ints on a
            /// rainy map - under a megabyte - and null the moment the fill is done.</summary>
            public List<int> Cyan;

            /// <summary>How many water pixels were painted out with the ground around them, and how many
            /// had nothing to copy from and were left as holes. Both go in the floor's log line.</summary>
            public int CyanFilled;

            public int CyanDropped;

            /// <summary>One band's stretched luminance, with the smoothing filter's halo rows above and
            /// below it - what the range weight is measured on. Refilled per band by FillLuminance and
            /// dropped by DevelopFinish.</summary>
            public float[] LumBand;

            /// <summary>The eight neighbours' luminances and the pixels they came from, sorted as they
            /// are collected. Two arrays of eight, allocated once a floor and reused for every pixel,
            /// because a per-pixel allocation here would be ten million of them.</summary>
            public float[] NeighbourLum;

            public int[] NeighbourIndex;

            /// <summary>How many of this floor's pixels were dimmed as outside the walkable area, for its
            /// log line.</summary>
            public int Outside;

            /// <summary>How many lone outliers were replaced by their neighbours' median, for the
            /// floor's log line.</summary>
            public int Despeckled;

            /// <summary>Wall time spent developing this floor's bands, the smoothing included, for the
            /// debug line. Accumulated across the band frames, so it is work rather than elapsed
            /// frames.</summary>
            public double SmoothMs;

            /// <summary>The two buffers the development works from: one band's worth of finished
            /// pixels, reused for every band, and every column's squared X distance from the
            /// capturing player. Both are dropped by DevelopFinish.</summary>
            public Color32[] Block;

            public float[] DxSquared;

            public string DistFile;

            /// <summary>The sidecar could not be staged: the old one is deleted at commit (review F09).</summary>
            public bool DistStale;

            /// <summary>WP4 B2: the developed picture when ManagedPngEncode (the plan's RgbaPool; replaces Texture).</summary>
            public Color32[] Rgba;

            /// <summary>WP4 B2: the picture's and the sidecar's encodes on workers, settled (staged) at a barrier.</summary>
            public System.Threading.Tasks.Task<PngEncoder.Result> PictureEncode;

            /// <summary>Stage M3: a menu floor's viewing copy - its file name, size and encode (box filter and PNG on a
            /// worker, settled with the picture), and whether it was staged. Null/false on every raid floor.</summary>
            public string ViewFile;

            public int ViewWidth;
            public int ViewHeight;
            public System.Threading.Tasks.Task<PngEncoder.Result> ViewEncode;
            public bool ViewStaged;

            public System.Threading.Tasks.Task<PngEncoder.Result> SidecarEncode;

            /// <summary>WP4 B2: Dist as handed to the sidecar's worker - kept for the Unity fallback, since ReleaseTexture
            /// nulls Dist before the settle.</summary>
            public byte[] SidecarSource;

            /// <summary>WP4 B2: what the settle's log line needs from buffers ReleaseTexture drops first: the floor clock
            /// at the encode's start, and for a side the drawn pixels and the pixel count.</summary>
            public double EncodeMs;

            public int EncodeDrawn;
            public int EncodeDrawnOf;

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

            /// <summary>WP1: per tile, what the loop does with it; null = render every tile (fresh capture, switch
            /// off, no usable previous picture, or the planning threw).</summary>
            public TileVerdict[] Verdicts;

            /// <summary>WP1: per tile, the largest previous sidecar step over the tile and its halo; 255 when any
            /// of it is undrawn. Built once when the sidecar is loaded. Null without a sidecar.</summary>
            public byte[] TileMaxOld;

            /// <summary>WP1: the previous picture and sidecar were loaded before the tiles; Develop must not load
            /// again.</summary>
            public bool PreviousLoaded;

            /// <summary>WP1: counts for the log lines - skipped as owned, skipped as outside, and skipped ones
            /// rendered after all because water lay within the inpaint's reach of their edge.</summary>
            public int TilesOwned;

            public int TilesOutside;

            public int TilesPromoted;

            /// <summary>WP1 (2.9): rows developed per frame for this floor - DevelopBandRows, or PixelBandRows when
            /// no tile was rendered after a load before the tiles, so nothing can be taken. Set by Develop.</summary>
            public int BandRows;

            /// <summary>WP1 audit mode only: per pixel, bit 1 = inside an owned tile's halo, bit 2 = inside an
            /// outside tile's halo; the verdicts with the would-be water promotions set to Render; and the
            /// violations DevelopBand counted. Null / 0 otherwise.</summary>
            public byte[] SkipZone;

            public TileVerdict[] AuditVerdicts;

            public int AuditOwnedTaken;

            public int AuditOutsideVisible;

            /// <summary>Campaign speed step 1 (1): the floor rendered no tile and its stored picture and sidecar are kept as
            /// they are (SkipUnchangedFloors) - nothing is staged, WriteMeta names the stored files and commits nothing.
            /// <see cref="Bytes"/> is then the stored picture's length, so every "was it written" gate still holds.</summary>
            public bool Unchanged;

            /// <summary>Campaign speed step 1 (2): per texture row and per column (contract orientation), the tile row and
            /// column it belongs to, -1 for none; and per tile whether this capture rendered it. Built by DevelopBegin for
            /// a side when SideSettleEmpty, dropped by DevelopFinish.</summary>
            public int[] TileRowOf;

            public int[] TileColOf;

            public bool[] RenderedTile;

            /// <summary>Campaign speed step 1 (2, review): per column (contract orientation), the texture row a side pixel must
            /// be ABOVE to be settled empty - the collider skyline plus <see cref="SkylineMarginPx"/>; -1 where no collider
            /// projects into the column. Null: no skyline, nothing settled. Built by BuildSkyline, dropped by
            /// ReleaseTexture.</summary>
            public int[] SkyRow;

            /// <summary>Campaign speed step 1 (2, re-review): per column, the top of the foliage band - SkyRow plus
            /// <see cref="SkylineFoliageMetres"/> projected; above it an empty pixel is settled at once. Null with SkyRow.</summary>
            public int[] SkyFarRow;

            /// <summary>Campaign speed step 1 (2, re-review): the running campaign's empty counts for this side (texture
            /// order, contract orientation), or null outside a campaign.</summary>
            public byte[] EmptyCounts;

            /// <summary>Campaign speed step 1 (2): side pixels whose sidecar now records the step they were seen empty
            /// from (newly, or from closer than before), for the side's line.</summary>
            public int SettledEmpty;

            /// <summary>Campaign speed step 2 (review): a held floor's picture and sidecar waiting for the end of the floor loop
            /// - developed (dirty) or kept unchanged (clean) - so a refusal by a later floor's light test leaves the hold as
            /// it was. Taken into the hold by <see cref="HoldStashedFloors"/>.</summary>
            public Color32[] StashPixels;

            public byte[] StashDist;

            public bool StashDirty;

            /// <summary>Campaign speed step 2 (review): a side's sidecar as it came off disk, before HealSideDist rewrote the
            /// copy the merge uses - what an unchanged side is held with, so every later stop heals from the stored data
            /// exactly as a stop that reads the file does.</summary>
            public byte[] UnhealedDist;
        }

        // --- campaign resume (step 3): the stored set, read only ---------------------------------

        /// <summary>
        /// Campaign speed step 3: what a campaign needs to know about the set already on disk to leave out the stops that
        /// can add nothing to it (MapCampaign's resume) - the pictures' geometry and files, each band's own walkable mask,
        /// and the standing points of the captures whose 3D mesh stage completed (CaptureMeta.Stands). Built on the main
        /// thread by <see cref="ReadStoredSet"/> (the masks need the NavMesh); its files are then decoded one at a time on a
        /// worker by <see cref="ReadStoredDist"/> and <see cref="ReadStoredEmpty"/>. Nothing here writes.
        /// </summary>
        internal sealed class StoredSet
        {
            /// <summary>Why the set cannot be resumed from, or null when it can.</summary>
            internal string Why;

            internal string Key;
            internal string Dir;

            /// <summary>The stored extent, pixels per metre and picture size - the floor pixels' own geometry.</summary>
            internal double MinX;

            internal double MinZ;
            internal double MaxX;
            internal double MaxZ;
            internal float Ppm;
            internal int Width;
            internal int Height;

            /// <summary>The meta's first-captured stamp: whether a later capture merged into this set or replaced it
            /// (<see cref="ResumeFirstCapturedAt"/>).</summary>
            internal string FirstCapturedAt;

            /// <summary>The floors, lowest band first (the order the capture sorts them in), and the sides.</summary>
            internal readonly List<StoredPicture> Floors = new List<StoredPicture>();

            internal readonly List<StoredPicture> Sides = new List<StoredPicture>();

            /// <summary>Whether the meta names a stored 3D mesh whose file is there.</summary>
            internal bool HasMesh;

            /// <summary>Where the captures that built into the named mesh stood (world x, z), oldest first; empty when none
            /// was recorded.</summary>
            internal readonly List<Vector2> Stands = new List<Vector2>();

            /// <summary>Main-thread milliseconds spent building this (the meta read and the masks).</summary>
            internal double MainMs;

            /// <summary>World x of a floor pixel column's centre - BuildDxSquared's arithmetic.</summary>
            internal float PixelX(int col) => (float)(MinX + (col + 0.5d) / Ppm);

            /// <summary>World z of a floor texture row's centre (row 0 at the bottom) - RowDzSquared's arithmetic.</summary>
            internal float PixelZ(int textureRow) => (float)(MaxZ - (Height - 1 - textureRow + 0.5d) / Ppm);

            /// <summary>
            /// Each floor's OWN walkable mask - main thread only, the NavMesh is Unity's. The capture's BuildReach (its grid,
            /// growth and ramp) over only the NavMesh triangles at the band's height: from the band's MinY up to the next
            /// band's MinY (the lowest band from below everything, the top band to above everything) - the heights whose
            /// floors the capture photographs in that band, which renders from its own floor up to the next one's
            /// (FloorPlan.NextMinY). So a basement's never-seen pixel counts only where the basement is walkable, not
            /// where the ground above it is.
            /// </summary>
            internal void BuildMasks()
            {
                for (var f = 0; f < Floors.Count; f++)
                {
                    // Widened by BandSlackMetres each way: a NavMesh is draped a little above or below the floor it covers, and
                    // a wider band only makes more pixels count - the safe direction.
                    var from = f == 0 ? float.NegativeInfinity : Floors[f].MinY - StoredPicture.BandSlackMetres;
                    var until = f == Floors.Count - 1 ? float.PositiveInfinity : Floors[f + 1].MinY + StoredPicture.BandSlackMetres;

                    Floors[f].BuildMask(this, from, until);
                }
            }
        }

        /// <summary>Campaign speed step 3: one stored floor or side, as <see cref="StoredSet"/> lists it.</summary>
        internal sealed class StoredPicture
        {
            /// <summary>Metres each band's NavMesh height range is widened by on both sides (<see cref="StoredSet.BuildMasks"/>).</summary>
            internal const float BandSlackMetres = 1f;

            internal string Name;
            internal string DistPath;

            /// <summary>The picture itself - read for a side only, whose transparent pixels the heal needs.</summary>
            internal string ColourPath;

            internal int Width;
            internal int Height;
            internal float Ppm;

            /// <summary>A floor: its band's lowest height.</summary>
            internal float MinY;

            /// <summary>A floor: its own walkable mask (<see cref="StoredSet.BuildMasks"/>), on a plan that holds only the
            /// stored pixels' geometry. Its Reach is null when there is no NavMesh at all, and then every pixel counts, as
            /// the capture then draws every pixel as reachable.</summary>
            private Plan _mask;

            /// <summary>Builds <see cref="_mask"/> from the NavMesh triangles at heights [from, until) - main thread.</summary>
            internal void BuildMask(StoredSet set, float from, float until)
            {
                _mask = new Plan
                {
                    Key = set.Key,
                    Dir = set.Dir,
                    Extent = new MapExtentDto { MinX = set.MinX, MinZ = set.MinZ, MaxX = set.MaxX, MaxZ = set.MaxZ },
                    Ppm = set.Ppm,
                    WidthPx = set.Width,
                    HeightPx = set.Height,
                };

                _mask.Reach = BuildReach(_mask, from, until);

                // A band with no walkable triangle of its own (an empty mask) falls back to the all-storey mask: nothing on
                // it would otherwise ever count, and a band nobody can stand on by this rule may still be one the NavMesh
                // reaches from a height this split puts in the neighbouring band.
                if (_mask.Reach != null && Array.TrueForAll(_mask.Reach, v => v == 0)) _mask.Reach = BuildReach(_mask);
            }

            /// <summary>A side: its basis and frame, which GroundPointOf needs for the ground point a pixel looks at.</summary>
            internal bool Side;

            internal double[] Right;
            internal double[] Up;
            internal double OriginR;
            internal double OriginU;
            internal float YMin;

            /// <summary>Whether a floor pixel lies inside this band's walkable mask - what the capture would draw with an
            /// alpha above zero, for this band's own floors. Pure arithmetic on the mask's arrays, so a worker may call
            /// it.</summary>
            internal bool Walkable(int col, int textureRow) => _mask?.Reach == null || ReachAt(_mask, col, textureRow) > 0f;
        }

        /// <summary>Campaign speed step 3: the sidecar's step for a distance - <see cref="Steps"/> itself, so the resume
        /// compares in the same four-metre steps and with the same rounding the merge writes.</summary>
        /// <param name="metres">Flat distance from the capturing player.</param>
        internal static byte ResumeStep(float metres) => Steps(metres);

        /// <summary>Campaign speed step 3: the step a sidecar stores for a pixel nothing has drawn.</summary>
        internal const byte ResumeUnseen = DistanceEmpty;

        /// <summary>
        /// Campaign speed step 3, main thread: the stored set of a map as a resume needs it - null when the map has none, a
        /// set with <see cref="StoredSet.Why"/> when it cannot be used. The meta is read and checked the way LoadPrevious
        /// checks the parts it can check without a capture's plan (schema, floors, floor levels against the extent the capture
        /// will use); the parts only a capture can decide (its pixels per metre, render tag, exposure) are checked after the
        /// campaign's first capture instead (<see cref="ResumeFirstCapturedAt"/>). Never throws.
        /// </summary>
        /// <param name="key">The map's key.</param>
        /// <param name="extent">The extent the capture will use (MapExtentProbe.TryProbeForCapture), for its floor levels.</param>
        internal static StoredSet ReadStoredSet(string key, MapExtentDto extent)
        {
            var clock = Stopwatch.StartNew();
            var set = new StoredSet { Key = key };

            try
            {
                var root = StoredRoot();
                if (root == null) return null;

                set.Dir = Path.Combine(root, key);
                var path = Path.Combine(set.Dir, $"{key}.map.json");
                if (!File.Exists(path)) return null;

                var meta = JsonConvert.DeserializeObject<CaptureMeta>(File.ReadAllText(path));

                if (meta == null || meta.Extent == null || meta.Floors == null || meta.Floors.Count == 0)
                    return Refuse(set, "its meta cannot be read");

                if (meta.SchemaVersion != SchemaVersion) return Refuse(set, $"it is schema {meta.SchemaVersion}");

                if (!(meta.PxPerMetre > 0f)) return Refuse(set, "it records no pixel size");

                var mine = (extent?.Floors ?? new List<MapFloorDto>()).Where(f => f != null).Select(f => f.Level).OrderBy(l => l);
                var theirs = meta.Floors.Where(f => f != null).Select(f => f.Level).OrderBy(l => l);
                if (!mine.SequenceEqual(theirs)) return Refuse(set, "its floors are not the ones this raid would capture");

                set.MinX = meta.Extent.MinX;
                set.MinZ = meta.Extent.MinZ;
                set.MaxX = meta.Extent.MaxX;
                set.MaxZ = meta.Extent.MaxZ;
                set.Ppm = meta.PxPerMetre;
                set.FirstCapturedAt = FirstOf(meta);

                foreach (var floor in meta.Floors.Where(f => f != null).OrderBy(f => f.MinY))
                {
                    if (set.Floors.Count == 0)
                    {
                        set.Width = floor.Width;
                        set.Height = floor.Height;
                    }

                    if (floor.Width != set.Width || floor.Height != set.Height || floor.Width < 1 || floor.Height < 1)
                        return Refuse(set, $"floor \"{floor.Name}\" is {floor.Width}x{floor.Height} px");

                    var level = floor.Level.ToString(CultureInfo.InvariantCulture);
                    set.Floors.Add(new StoredPicture
                    {
                        Name = floor.Name,
                        DistPath = Path.Combine(set.Dir, $"{key}-{level}.dist.png"),
                        Width = floor.Width,
                        Height = floor.Height,
                        Ppm = meta.PxPerMetre,
                        MinY = floor.MinY,
                    });
                }

                foreach (var side in meta.Sides ?? new List<CaptureSide>())
                {
                    if (side?.Right == null || side.Up == null || side.Right.Length < 3 || side.Up.Length < 3 ||
                        side.Width < 1 || side.Height < 1 || !(side.PxPerMetre > 0f) || string.IsNullOrEmpty(side.File))
                        continue;

                    set.Sides.Add(new StoredPicture
                    {
                        Name = $"side {side.Dir}",
                        DistPath = Path.Combine(set.Dir, SideDistFileName(key, side.Dir)),
                        ColourPath = Path.Combine(set.Dir, side.File),
                        Width = side.Width,
                        Height = side.Height,
                        Ppm = side.PxPerMetre,
                        Side = true,
                        Right = side.Right.Select(v => (double)v).ToArray(),
                        Up = side.Up.Select(v => (double)v).ToArray(),
                        OriginR = side.OriginR,
                        OriginU = side.OriginU,
                        YMin = side.YMin,
                    });
                }

                set.HasMesh = meta.Mesh != null && !string.IsNullOrEmpty(meta.Mesh.File) &&
                              File.Exists(Path.Combine(set.Dir, meta.Mesh.File));

                if (set.HasMesh && meta.Stands != null)
                    foreach (var stand in meta.Stands)
                        if (stand != null && IsFinite(stand.X) && IsFinite(stand.Z))
                            set.Stands.Add(new Vector2(stand.X, stand.Z));

                set.BuildMasks();

                set.MainMs = clock.Elapsed.TotalMilliseconds;
                return set;
            }
            catch (Exception ex)
            {
                return Refuse(set, $"reading it threw ({ex.GetType().Name}: {ex.Message})");
            }

            StoredSet Refuse(StoredSet s, string why)
            {
                s.Why = why;
                s.MainMs = clock.Elapsed.TotalMilliseconds;
                return s;
            }
        }

        /// <summary>Campaign speed step 3: the captures folder, without creating it as <see cref="CapturesRootDir"/> does -
        /// a resume only reads. Null when the plugin has no file location.</summary>
        private static string StoredRoot()
        {
            var modPath = Path.GetDirectoryName(typeof(MapCapture).Assembly.Location);
            return string.IsNullOrEmpty(modPath) ? null : Path.Combine(modPath, "captures");
        }

        /// <summary>Campaign speed step 3, any thread: one stored distance sidecar as one byte a pixel in TEXTURE order (row 0
        /// at the picture's bottom, the order the merge indexes it in: CopyRed of Unity's decode, whose first row is the PNG's
        /// last) - or null, with why. See <see cref="DecodeStored"/>.</summary>
        /// <param name="picture">The stored picture whose sidecar is wanted.</param>
        /// <param name="why">When null is returned: why.</param>
        internal static byte[] ReadStoredDist(StoredPicture picture, out string why) =>
            DecodeStored(picture.DistPath, picture.Width, picture.Height, false, "distance sidecar", out why);

        /// <summary>Campaign speed step 3, any thread: which pixels of a stored SIDE picture are <see cref="EmptyPixel"/> -
        /// transparent black, what Develop leaves where nothing was drawn - as 1 (empty) or 0, in texture order; or null, with
        /// why. What <see cref="HealSideDist"/> reads the colour for.</summary>
        /// <param name="picture">The stored side.</param>
        /// <param name="why">When null is returned: why.</param>
        internal static byte[] ReadStoredEmpty(StoredPicture picture, out string why) =>
            DecodeStored(picture.ColourPath, picture.Width, picture.Height, true, "picture", out why);

        /// <summary>
        /// Campaign speed step 3: the managed decode behind <see cref="ReadStoredDist"/> and <see cref="ReadStoredEmpty"/>,
        /// so it can run off the main thread - 8-bit, non-interlaced, grey, grey-alpha, RGB or RGBA, any of the five row
        /// filters, the zlib stream's Adler-32 checked. A file read wrong would let a campaign leave out a stop it needed,
        /// so a file that is not exactly right is refused. Nothing is written.
        /// </summary>
        /// <param name="path">The PNG.</param>
        /// <param name="width">The width it must be.</param>
        /// <param name="height">The height it must be.</param>
        /// <param name="empty">False: the first channel of each pixel. True: 1 where every channel is 0 (RGBA only).</param>
        /// <param name="what">For the reasons.</param>
        /// <param name="why">When null is returned: why.</param>
        private static byte[] DecodeStored(string path, int width, int height, bool empty, string what, out string why)
        {
            why = null;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    why = $"it has no {what}";
                    return null;
                }

                var file = File.ReadAllBytes(path);
                byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

                if (file.Length < 8 || !png.SequenceEqual(file.Take(8)))
                {
                    why = $"its {what} is not a PNG";
                    return null;
                }

                var idat = new MemoryStream();
                var bpp = 0;
                var at = 8;

                while (at + 12 <= file.Length)
                {
                    var length = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
                    var type = Encoding.ASCII.GetString(file, at + 4, 4);
                    var data = at + 8;

                    if (length < 0 || data + (long)length + 4 > file.Length)
                    {
                        why = $"its {what} is cut short";
                        return null;
                    }

                    if (type == "IHDR")
                    {
                        int Be(int o) => (file[o] << 24) | (file[o + 1] << 16) | (file[o + 2] << 8) | file[o + 3];

                        var colour = file[data + 9];
                        bpp = colour == 0 ? 1 : colour == 4 ? 2 : colour == 2 ? 3 : colour == 6 ? 4 : 0;

                        if (length != 13 || Be(data) != width || Be(data + 4) != height || file[data + 8] != 8 ||
                            bpp == 0 || file[data + 12] != 0 || (empty && bpp != 4))
                        {
                            why = $"its {what} is not an 8-bit {width}x{height} picture this reads";
                            return null;
                        }
                    }
                    else if (type == "IDAT")
                    {
                        idat.Write(file, data, length);
                    }
                    else if (type == "IEND")
                    {
                        break;
                    }

                    at = data + length + 4;
                }

                if (bpp == 0 || idat.Length < 6)
                {
                    why = $"its {what} has no picture data";
                    return null;
                }

                var z = idat.GetBuffer();
                var zLength = (int)idat.Length;

                // deflate, no preset dictionary
                if ((z[0] & 0x0F) != 8 || (z[1] & 0x20) != 0)
                {
                    why = $"its {what}'s data is not a zlib stream this reads";
                    return null;
                }

                var stride = width * bpp;
                var row = new byte[stride + 1];
                var prior = new byte[stride];
                var current = new byte[stride];
                var result = new byte[width * height];
                uint a = 1, b = 0;

                using (var compressed = new MemoryStream(z, 2, zLength - 2, false))
                using (var inflate = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionMode.Decompress))
                {
                    for (var y = 0; y < height; y++)
                    {
                        for (var got = 0; got < row.Length;)
                        {
                            var n = inflate.Read(row, got, row.Length - got);
                            if (n <= 0)
                            {
                                why = $"its {what} ends at row {y} of {height}";
                                return null;
                            }

                            got += n;
                        }

                        for (var i = 0; i < row.Length; i++)
                        {
                            a += row[i];
                            if (a >= 65521) a -= 65521;
                            b += a;
                            if (b >= 65521) b -= 65521;
                        }

                        if (!UnfilterRow(row, current, prior, stride, bpp))
                        {
                            why = $"its {what}'s row {y} has filter {row[0]}";
                            return null;
                        }

                        // PNG row y is texture row height - 1 - y.
                        var into = (height - 1 - y) * width;

                        if (empty)
                            for (int x = 0, o = 0; x < width; x++, o += 4)
                                result[into + x] = (byte)((current[o] | current[o + 1] | current[o + 2] | current[o + 3]) == 0 ? 1 : 0);
                        else
                            for (int x = 0, o = 0; x < width; x++, o += bpp)
                                result[into + x] = current[o];

                        var swap = prior;
                        prior = current;
                        current = swap;
                    }
                }

                var adler = ((uint)z[zLength - 4] << 24) | ((uint)z[zLength - 3] << 16) | ((uint)z[zLength - 2] << 8) | z[zLength - 1];
                if (adler != ((b << 16) | a))
                {
                    why = $"its {what}'s checksum does not match";
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                why = $"its {what} could not be read ({ex.GetType().Name}: {ex.Message})";
                return null;
            }
        }

        /// <summary>
        /// Campaign speed step 3: <see cref="HealSideDist"/> on a stored side as the resume reads it - the same rule on the
        /// same two inputs: a pixel that is transparent black on disk AND carries a step is read as 255 (never seen) unless
        /// the settle rule would keep it. That rule needs the collider skyline, which a campaign casts at its first stop -
        /// after the resume has decided - so here there is none, and HealSideDist's own no-skyline branch applies: every
        /// such pixel is healed. Stricter than the first stop's heal (which keeps the settled pixels above the skyline), so
        /// it can only keep a stop the capture would not need, never leave out one it would.
        /// </summary>
        /// <param name="dist">The side's sidecar, texture order; healed in place.</param>
        /// <param name="emptyPixels">1 where the side's picture is transparent black (<see cref="ReadStoredEmpty"/>).</param>
        /// <returns>How many pixels were healed.</returns>
        internal static int HealStoredSide(byte[] dist, byte[] emptyPixels)
        {
            var healed = 0;

            for (var i = 0; i < dist.Length && i < emptyPixels.Length; i++)
            {
                if (dist[i] == DistanceEmpty || emptyPixels[i] == 0) continue;

                dist[i] = DistanceEmpty;
                healed++;
            }

            return healed;
        }

        /// <summary>Campaign speed step 3: one PNG row un-filtered - the five filters of the PNG specification.</summary>
        /// <param name="row">The row as inflated: its filter byte, then the filtered bytes.</param>
        /// <param name="current">Receives the row's bytes.</param>
        /// <param name="prior">The row above, un-filtered (zeroes above the first).</param>
        /// <param name="stride">Bytes in a row.</param>
        /// <param name="bpp">Bytes in a pixel.</param>
        private static bool UnfilterRow(byte[] row, byte[] current, byte[] prior, int stride, int bpp)
        {
            switch (row[0])
            {
                case 0:
                    Buffer.BlockCopy(row, 1, current, 0, stride);
                    return true;
                case 1:
                    for (var i = 0; i < stride; i++) current[i] = (byte)(row[i + 1] + (i >= bpp ? current[i - bpp] : 0));
                    return true;
                case 2:
                    for (var i = 0; i < stride; i++) current[i] = (byte)(row[i + 1] + prior[i]);
                    return true;
                case 3:
                    for (var i = 0; i < stride; i++) current[i] = (byte)(row[i + 1] + (((i >= bpp ? current[i - bpp] : 0) + prior[i]) >> 1));
                    return true;
                case 4:
                    for (var i = 0; i < stride; i++)
                    {
                        int left = i >= bpp ? current[i - bpp] : 0, up = prior[i], corner = i >= bpp ? prior[i - bpp] : 0;
                        int p = left + up - corner, pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - corner);
                        current[i] = (byte)(row[i + 1] + (pa <= pb && pa <= pc ? left : pb <= pc ? up : corner));
                    }

                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Campaign speed step 3: the first-captured stamp of the set a campaign is building - the held one while a
        /// campaign holds it, else the one on disk - or null. A capture that MERGED into a set keeps its stamp; one that
        /// replaced it (a different pixel size, render recipe or exposure, which only the capture can decide) starts a new
        /// one. So a resume compares this after its first capture with what it read at the start, and visits the stops it
        /// left out when they differ. Read only; never throws.</summary>
        /// <param name="key">The map's key.</param>
        internal static string ResumeFirstCapturedAt(string key)
        {
            try
            {
                var hold = _hold;
                if (hold?.Meta != null && string.Equals(hold.Key, key, StringComparison.OrdinalIgnoreCase)) return FirstOf(hold.Meta);

                var root = StoredRoot();
                if (root == null) return null;

                var path = Path.Combine(root, key, $"{key}.map.json");
                if (!File.Exists(path)) return null;

                var meta = JsonConvert.DeserializeObject<CaptureMeta>(File.ReadAllText(path));
                return meta != null ? FirstOf(meta) : null;
            }
            catch
            {
                return null;
            }
        }

        // --- campaign resume (step 3): where the mesh's captures stood --------------------------

        /// <summary>Campaign speed step 3: standing points a meta keeps - the newest; the oldest go first. 256 is five
        /// Interchange campaigns. Local only: MapTransferDto has no such field, so an upload drops it, and no viewer reads
        /// it.</summary>
        private const int MaxStands = 256;

        /// <summary>Campaign speed step 3: the standing points a meta names - those of the captures whose 3D mesh stage
        /// completed into the mesh that meta names. A mesh built from scratch starts the list again (the points before it
        /// describe a mesh that is gone); a capture whose mesh stage did not complete adds nothing (its streamed buildings
        /// are not in the mesh).</summary>
        /// <param name="before">The list the mesh this one was built onto carried, or null.</param>
        /// <param name="plan">The capture's plan: where it stood (From) and which capture it was.</param>
        /// <param name="completed">Whether this capture's mesh stage completed into the mesh the meta names.</param>
        /// <param name="accumulated">Whether that mesh was built onto a stored one rather than from scratch.</param>
        private static List<CaptureStand> Stood(List<CaptureStand> before, Plan plan, bool completed, bool accumulated)
        {
            var list = completed && !accumulated
                ? new List<CaptureStand>()
                : new List<CaptureStand>(before ?? new List<CaptureStand>());

            // Stage M2: a menu capture stood nowhere - it saw the whole map loaded - so it records no stand (a campaign's
            // resume would otherwise read the world origin as a place the mesh was taken from)
            if (completed && !plan.MenuMode) list.Add(new CaptureStand { X = plan.From.x, Z = plan.From.y, Capture = plan.Captures });
            if (list.Count > MaxStands) list.RemoveRange(0, list.Count - MaxStands);

            return list.Count > 0 ? list : null;
        }

        /// <summary>Campaign speed step 3: one standing point in the meta.</summary>
        private sealed class CaptureStand
        {
            [JsonProperty("x")] public float X { get; set; }
            [JsonProperty("z")] public float Z { get; set; }

            /// <summary>The capture's number (meta.captures when it was taken), for a person reading the file.</summary>
            [JsonProperty("capture")] public int Capture { get; set; }
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

            /// <summary>HOW the capture was rendered - "own-1.5;lod1000;basemap0": the capture light's
            /// intensity, the LOD bias the render was taken under and the terrain base-map distance. Absent
            /// in a capture taken before the field existed. Not decoration - it is the one gate that stops a
            /// picture taken under one recipe being merged pixel by pixel into one taken under another,
            /// which would seam the two together or let building-less ground win the distance test; see
            /// <see cref="RenderTag"/> and <see cref="LoadPrevious"/>.
            ///
            /// Read by nothing but <see cref="LoadPrevious"/>: the Maps tab's reader takes the fields it
            /// names and ignores the rest, tools/check-capture.py the same, and the upload's
            /// MapCaptureMetaDto does not carry it - so a set synced from a host has no recipe, which is
            /// correct, since a merge only ever happens against this machine's own captures folder.</summary>
            [JsonProperty("render")] public string Render { get; set; }

            /// <summary>The raid's own clock, "HH:mm", or "" when it could not be read. A map
            /// captured at 03:00 is a dark map and worth taking again.</summary>
            [JsonProperty("timeOfDay")] public string TimeOfDay { get; set; }

            /// <summary>Stage M2c: <see cref="MenuSetMarker"/> on a set a menu capture wrote (and kept by a raid capture merged
            /// into one); ABSENT on every raid set, so a raid meta is written exactly as before. See
            /// <see cref="IsMenuSet"/>.</summary>
            [JsonProperty("capturedIn", NullValueHandling = NullValueHandling.Ignore)] public string CapturedIn { get; set; }

            /// <summary>Lighting stage 2 (2026-09-28): the raid's LIGHT as the game had it when this capture was written -
            /// its sun, its ambient harmonics, its sky and fog colours, its tonemap - so the 3D map can be lit like the
            /// game (Map3DView, stage 3). Absent in a capture taken before the field existed or when nothing could be
            /// read (the menu, a test scene); every reader falls back to its preset light. NOT part of RenderTag: a
            /// merge is of pictures, and the newest capture's light wins. See <see cref="ReadLighting"/>.</summary>
            [JsonProperty("lighting", NullValueHandling = NullValueHandling.Ignore)] public CaptureLighting Lighting { get; set; }

            [JsonProperty("floors")] public List<CaptureFloor> Floors { get; set; } = new List<CaptureFloor>();
            [JsonProperty("labels")] public List<CaptureLabel> Labels { get; set; } = new List<CaptureLabel>();

            /// <summary>The 3D geometry beside the pictures, or ABSENT when this capture built none -
            /// a set captured before the feature existed, a set from DynamicMaps' art folder, a set
            /// synced from an old host, or a capture whose mesh phase failed. Every reader carries on
            /// drawing the flat picture without it, which is what makes "absent" cost nothing and is
            /// why adding it is not a schema bump.
            ///
            /// Never merged: a capture that builds a mesh rebuilds it whole (the relief is 45 ms and
            /// deterministic) and this block describes the file it wrote. A capture that builds none -
            /// an AutoCapture tick on a map that has one - carries the previous meta's block forward
            /// unchanged, file, length and hash, when that file is still on disk and was built for the
            /// same floors (see CarriedMesh). Either way the block describes the file beside it.</summary>
            [JsonProperty("mesh")] public CaptureMesh Mesh { get; set; }

            /// <summary>The oblique side views the 3D map textures its walls from (plan, stage U - a
            /// frozen contract). ABSENT, not null and not empty, when there are none: the contract says
            /// "absent or empty = no sides", and absent is the one of the two every older reader
            /// already handles. Rebuilt whole by a capture that takes them, carried forward unchanged
            /// by one that does not (CarriedSides).</summary>
            [JsonProperty("sides", NullValueHandling = NullValueHandling.Ignore)]
            public List<CaptureSide> Sides { get; set; }

            /// <summary>Stage W: the mesh's texture atlas pages, page n at index n - a CONTRACT with the viewer,
            /// the host and tools/check-capture.py. ABSENT when there is none (no mesh, or a mesh with no captured
            /// texture); a building range naming a page past this list falls back to the side views.</summary>
            [JsonProperty("atlas", NullValueHandling = NullValueHandling.Ignore)]
            public List<CaptureAtlas> Atlas { get; set; }

            /// <summary>Campaign speed step 3: where the captures whose 3D mesh stage completed into <see cref="Mesh"/>
            /// stood, oldest first, at most MaxStands - what a campaign's resume reads to know a stop's own area was
            /// streamed into the mesh (see <see cref="Stood"/>). Local: no upload or viewer reads it.</summary>
            [JsonProperty("stands", NullValueHandling = NullValueHandling.Ignore)]
            public List<CaptureStand> Stands { get; set; }
        }

        /// <summary>One atlas page as the meta describes it (stage W). The JSON names are the contract.</summary>
        private sealed class CaptureAtlas
        {
            /// <summary><c>&lt;key&gt;-atlas-&lt;n&gt;.png</c>, beside this meta.</summary>
            [JsonProperty("file")] public string File { get; set; }

            /// <summary>The page number the mesh's ranges use - its index in the list.</summary>
            [JsonProperty("page")] public int Page { get; set; }

            [JsonProperty("width")] public int Width { get; set; }
            [JsonProperty("height")] public int Height { get; set; }

            /// <summary>Tiles packed on it, for the log lines and the checker.</summary>
            [JsonProperty("tiles")] public int Tiles { get; set; }

            /// <summary>Its size on disk.</summary>
            [JsonProperty("bytes")] public long Bytes { get; set; }

            /// <summary>SHA-256 of the PNG's bytes, lower-case hex.</summary>
            [JsonProperty("sha256")] public string Sha256 { get; set; }
        }

        /// <summary>One side view as the meta describes it. The JSON names are the CONTRACT (plan,
        /// stage U): the viewer projects building walls into the picture by exactly these numbers, the
        /// host mirrors the shape as MapCaptureSideDto, and tools/check-capture.py recomputes the
        /// origins from them - a rename breaks three readers at once.
        ///
        /// The mapping they define: px = (dot(right, p) - originR) * pxPerMetre, py = height -
        /// (dot(up, p) - originU) * pxPerMetre, row 0 at the top; the vectors are unit, world x/y/z,
        /// stored at float precision - and every number here was derived from those float values, so a
        /// reader recomputing from them gets these origins to within float rounding.</summary>
        private sealed class CaptureSide
        {
            /// <summary>"N", "S", "E" or "W": the side of the map the camera stood on.</summary>
            [JsonProperty("dir")] public string Dir { get; set; }

            /// <summary><c>&lt;key&gt;-side-&lt;dir&gt;.png</c>, beside this meta.</summary>
            [JsonProperty("file")] public string File { get; set; }

            [JsonProperty("width")] public int Width { get; set; }
            [JsonProperty("height")] public int Height { get; set; }
            [JsonProperty("pxPerMetre")] public float PxPerMetre { get; set; }

            /// <summary>f, the way the camera looked.</summary>
            [JsonProperty("forward")] public float[] Forward { get; set; }

            /// <summary>r, the picture's +x.</summary>
            [JsonProperty("right")] public float[] Right { get; set; }

            /// <summary>u, the picture's +y, towards row 0.</summary>
            [JsonProperty("up")] public float[] Up { get; set; }

            /// <summary>The minimum of dot(right, corner) over the box's 8 corners.</summary>
            [JsonProperty("originR")] public double OriginR { get; set; }

            /// <summary>The minimum of dot(up, corner) over the box's 8 corners.</summary>
            [JsonProperty("originU")] public double OriginU { get; set; }

            /// <summary>The box's y range: the union of every mesh range the map's sides were framed on, snapped to
            /// 10 m; the bands' widened only before any mesh.</summary>
            [JsonProperty("yMin")] public float YMin { get; set; }
            [JsonProperty("yMax")] public float YMax { get; set; }

            /// <summary>Not in the meta. True until this capture has STAGED the side's distance sidecar: a side
            /// whose sidecar was not written gets the old one deleted at commit rather than kept beside a picture it
            /// does not describe (review F09). Carried sides (read back from the meta) are never committed, so the
            /// default of a deserialised entry does not matter.</summary>
            [JsonIgnore] public bool DistStale { get; set; }
        }

        /// <summary>The mesh file the capture wrote, as the meta describes it. The JSON names here are
        /// a CONTRACT: the host mirrors this shape on the wire as MapCaptureMeshDto and
        /// tools/check-capture.py reads it, so a rename is a change on three sides at once.</summary>
        private sealed class CaptureMesh
        {
            /// <summary>The mesh's file name, beside this meta - <c>&lt;key&gt;-mesh.bin</c>.</summary>
            [JsonProperty("file")] public string File { get; set; }

            /// <summary>Its size on disk, deflated. Checked by the packaging gates and by the download,
            /// which will not install a mesh whose length disagrees with this.</summary>
            [JsonProperty("bytes")] public long Bytes { get; set; }

            /// <summary>The format version inside the file (<see cref="MapMeshFile.Version"/>), so a
            /// reader can tell a mesh it cannot read from one that is merely absent WITHOUT inflating
            /// it.</summary>
            [JsonProperty("version")] public int Version { get; set; }

            /// <summary>Relief cells across every band, and triangles across every building: the two
            /// numbers that say what is in the file, for a reader deciding whether it is worth loading
            /// and for the checker proving the file matches its meta.</summary>
            [JsonProperty("cells")] public long Cells { get; set; }

            [JsonProperty("triangles")] public long Triangles { get; set; }

            /// <summary>SHA-256 of the file's bytes, lower-case hex. What tells a mesh that belongs to
            /// this meta from one left behind by an older capture or truncated in transit.</summary>
            [JsonProperty("sha256")] public string Sha256 { get; set; }
        }

        /// <summary>
        /// Lighting stage 2: the raid's light, as the game had it - flat fields, every array a fixed length, so the host
        /// can bound it (MapStore) and the wire mirrors stay simple. Colours are r, g, b in the game's own (gamma) values;
        /// the sun direction points TOWARDS the sun in world axes; the ambient is Unity's SphericalHarmonicsL2 as 27
        /// floats (3 channels x 9 coefficients, [c, i] order) - EFT's own ambient, computed by its time-of-day controller,
        /// which the game applies through its own shader globals rather than RenderSettings. The block is finite by
        /// construction: ReadLighting writes 0 for a non-finite number and null for an array with one in it.
        /// </summary>
        internal sealed class CaptureLighting
        {
            /// <summary>What was read: "TOD_Sky" when the game's sky was there, else "" (RenderSettings only).</summary>
            [JsonProperty("source")] public string Source { get; set; } = "";

            /// <summary>Towards the game's light, world axes: the sun by day, the MOON by night, and never below TOD's
            /// minimum height - so IsDay, not the elevation, says night. Null when unreadable.</summary>
            [JsonProperty("sunDirection")] public float[] SunDirection { get; set; }

            /// <summary>Towards TOD_Sky's true sun (sky.SunDirection), below the horizon at night; null when unreadable.</summary>
            [JsonProperty("sunTrueDirection", NullValueHandling = NullValueHandling.Ignore)] public float[] SunTrueDirection { get; set; }

            [JsonProperty("sunColor")] public float[] SunColor { get; set; }
            [JsonProperty("sunIntensity")] public float SunIntensity { get; set; }
            [JsonProperty("sunShadowStrength")] public float SunShadowStrength { get; set; }
            [JsonProperty("isDay")] public bool IsDay { get; set; }
            [JsonProperty("fogginess")] public float Fogginess { get; set; }

            /// <summary>EFT's ambient as spherical harmonics, 27 floats, or null.</summary>
            [JsonProperty("ambientSh")] public float[] AmbientSh { get; set; }

            [JsonProperty("skyColor")] public float[] SkyColor { get; set; }
            [JsonProperty("equatorColor")] public float[] EquatorColor { get; set; }
            /// <summary>TOD's fog colour sampled with directLight false: the horizon colour without the sun's direct term,
            /// so it does not depend on where the player looked.</summary>
            [JsonProperty("fogColor")] public float[] FogColor { get; set; }

            /// <summary>The level's declared sun colour (LevelSettings.SunColor), or null.</summary>
            [JsonProperty("levelSunColor")] public float[] LevelSunColor { get; set; }

            /// <summary>The fog as applied (RenderSettings) at the time of the capture.</summary>
            [JsonProperty("fog")] public bool Fog { get; set; }

            [JsonProperty("fogMode")] public string FogMode { get; set; } = "";
            [JsonProperty("fogDensity")] public float FogDensity { get; set; }
            [JsonProperty("fogStart")] public float FogStart { get; set; }
            [JsonProperty("fogEnd")] public float FogEnd { get; set; }
            [JsonProperty("renderFogColor")] public float[] RenderFogColor { get; set; }
            [JsonProperty("ambientMode")] public string AmbientMode { get; set; } = "";
            [JsonProperty("ambientIntensity")] public float AmbientIntensity { get; set; }

            /// <summary>The FPS camera's Prism tonemap: its type name when in use ("RomB", "ACES", ...), else "".</summary>
            [JsonProperty("prismTonemap")] public string PrismTonemap { get; set; } = "";

            [JsonProperty("prismExposure")] public bool PrismExposure { get; set; }
            [JsonProperty("prismMiddleGrey")] public float PrismMiddleGrey { get; set; }

            /// <summary>Prism's gamma when its correction is on, else 0.</summary>
            [JsonProperty("prismGamma")] public float PrismGamma { get; set; }

            [JsonProperty("prismLut")] public string PrismLut { get; set; } = "";

            /// <summary>The FPS camera's PostProcessing volume: each effect as "TypeName:on" or ":off".</summary>
            [JsonProperty("postProcess")] public List<string> PostProcess { get; set; } = new List<string>();

            [JsonProperty("colorSpace")] public string ColorSpace { get; set; } = "";
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

            /// <summary>Stage M3: the floor's viewing copy beside it (<see cref="ViewFileName"/>) - the same picture box-
            /// filtered to at most <see cref="ViewPictureSide"/> px, alpha included - which the Maps tab draws instead of the
            /// full picture. Optional: raid sets and older menu sets have none, and readers fall back to <see cref="File"/>.</summary>
            [JsonProperty("viewFile", NullValueHandling = NullValueHandling.Ignore)] public string ViewFile { get; set; }

            [JsonProperty("viewWidth", NullValueHandling = NullValueHandling.Ignore)] public int? ViewWidth { get; set; }
            [JsonProperty("viewHeight", NullValueHandling = NullValueHandling.Ignore)] public int? ViewHeight { get; set; }

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

        // --- driving a capture from outside ------------------------------------------------------

        /// <summary>Per map: how many commits have replaced its files (Pixels), and how many of those changed what the
        /// meta DESCRIBES - extent, scale, floor set and sizes, mesh, atlas pages, side geometry (Shape). Read by
        /// MapTransfer's upload between frames (WP3, the supersede guard). Main thread only.</summary>
        internal struct CommitStamp
        {
            public int Pixels;
            public int Shape;
        }

        private static readonly Dictionary<string, CommitStamp> _commits =
            new Dictionary<string, CommitStamp>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last meta's <see cref="ShapeSignature"/> per map, this session.</summary>
        private static readonly Dictionary<string, string> _shapes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The map's commit counters now; zero for a map not committed this session.</summary>
        /// <param name="key">The map's internal id.</param>
        internal static CommitStamp CommitStampOf(string key) =>
            key != null && _commits.TryGetValue(key, out var stamp) ? stamp : default;

        /// <summary>Counts one commit of the map: its pictures (<paramref name="shape"/> false) or its shape.</summary>
        private static void Bump(string key, bool shape)
        {
            if (string.IsNullOrEmpty(key)) return;

            _commits.TryGetValue(key, out var stamp);

            if (shape) stamp.Shape++;
            else stamp.Pixels++;

            _commits[key] = stamp;
        }

        /// <summary>
        /// What a meta DESCRIBES, as text: two metas with the same signature describe pictures an upload can send
        /// under either (WP3). The extent and scale, each floor's level, file and size, the mesh's file and sha, each
        /// atlas page's number, file and sha, and each side's file, size, scale, origins and height range. A stop that
        /// left the mesh unchanged keeps its sha (PART-05), so it is pixel-only here.
        ///
        /// Deliberately NOT in it: exposure (informational - no reader or host uses it), labels, capturedAt and the
        /// capture count - carried by the meta the upload already holds, and corrected by the next upload.
        /// </summary>
        /// <param name="m">The meta just written.</param>
        private static string ShapeSignature(CaptureMeta m)
        {
            string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

            var b = new StringBuilder();

            if (m.Extent != null)
                b.Append(R(m.Extent.MinX)).Append(',').Append(R(m.Extent.MinZ)).Append(',')
                 .Append(R(m.Extent.MaxX)).Append(',').Append(R(m.Extent.MaxZ));

            b.Append('|').Append(R(m.PxPerMetre));

            if (m.Floors != null)
                foreach (var f in m.Floors.Where(f => f != null).OrderBy(f => f.Level))
                    b.Append("|F").Append(f.Level).Append(':').Append(f.File).Append(':').Append(f.Width).Append('x').Append(f.Height);

            if (m.Mesh != null) b.Append("|M").Append(m.Mesh.File).Append(':').Append(m.Mesh.Sha256);

            if (m.Atlas != null)
                foreach (var p in m.Atlas.Where(p => p != null).OrderBy(p => p.Page))
                    b.Append("|A").Append(p.Page).Append(':').Append(p.File).Append(':').Append(p.Sha256);

            if (m.Sides != null)
                foreach (var s in m.Sides.Where(s => s != null).OrderBy(s => s.Dir, StringComparer.Ordinal))
                    b.Append("|S").Append(s.Dir).Append(':').Append(s.File).Append(':').Append(s.Width).Append('x').Append(s.Height)
                     .Append(':').Append(R(s.PxPerMetre)).Append(':').Append(R(s.OriginR)).Append(':').Append(R(s.OriginU))
                     .Append(':').Append(R(s.YMin)).Append(':').Append(R(s.YMax));

            return b.ToString();
        }

        /// <summary>The capture installed in the current raid, remembered so the two members below do
        /// not scan the scene on every frame of a campaign's wait loop. Unity's == null is true for a
        /// DESTROYED object as well as a missing one, so a reference left over from the previous raid
        /// is looked up again rather than handed back.</summary>
        private static MapCapture _current;

        private static MapCapture Current()
        {
            if (_current != null) return _current;

            _current = UnityEngine.Object.FindObjectOfType<MapCapture>();
            return _current;
        }

        /// <summary>Whether a capture is running right now - for anything that must not start a second
        /// one, or move the player, while a picture is being taken. See <see cref="MapCampaign"/>,
        /// which is the only caller: the streamer loads the world around the PLAYER, so a capture is
        /// a photograph of wherever the player was standing when it started.</summary>
        public static bool IsCapturing
        {
            get
            {
                try
                {
                    var runner = Current();
                    return runner != null && runner._running;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Starts a capture exactly as the key press does, for a caller that wants one without
        /// a key: the same refusals (one already running, no living player) and the same coroutine.
        /// False - having said nothing itself, though <see cref="Prepare"/> will have said why - means
        /// nothing is being captured: there is no capture installed in this raid, one is already
        /// running, there is nobody alive to photograph from, or the capture refused this raid in its
        /// own first step. The caller knows what a refusal means for IT and says so itself.</summary>
        /// <param name="automatic">True for the AutoCapture tick, which comes round every few seconds:
        /// it takes its pictures exactly as any other capture does, but it builds the 3D mesh only for
        /// a map that has none yet - see <see cref="Plan.WantsMesh"/>. False, the default, for a
        /// campaign stop, which is a place somebody chose.</param>
        /// <param name="buildMesh">False skips the 3D mesh for this capture; the campaign passes true at every stop, and
        /// each stop ADDS what it has loaded to the stored mesh (WP2, MapMeshBuilder.Request.Base) - the map's mesh is the
        /// union of every stop, not the last one's. The pictures and side views are taken either way.</param>
        /// <param name="verifyMesh">WP2: the campaign's last stop - with MeshVerifyLastStop on, the mesh is built a second
        /// time from scratch into &lt;key&gt;-mesh.verify.bin for tools/compare-mesh.py.</param>
        public static bool TryStartCapture(bool automatic = false, bool buildMesh = true, bool verifyMesh = false)
        {
            try
            {
                var runner = Current();
                if (runner == null) return false;
                if (runner._running) return false;
                if (!runner.PlayerIsAlive()) return false;

                // Set before the coroutine is started, for the same reason the key press does it: a
                // second caller in the same frame must be refused rather than fight for the camera.
                runner._running = true;
                runner._automatic = automatic;
                runner._skipMesh = !buildMesh;
                runner._verifyMesh = verifyMesh && buildMesh;
                runner.StartCoroutine(runner.Run());

                // The flag, not a bare true: StartCoroutine runs the coroutine's body up to its first
                // yield THERE AND THEN, so a capture whose Prepare refused this raid has already run its
                // finally and cleared the flag by the time this line reads it. Saying "started" for that
                // would tell a campaign a picture was taken at this stop - eighteen "captured" lines for
                // a raid that wrote nothing - and would hide the refusal from a caller counting failures.
                // A capture that really started has yielded and is still running.
                return runner._running;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: a map capture could not be started ({ex.Message}).");
                return false;
            }
        }
    }
}
