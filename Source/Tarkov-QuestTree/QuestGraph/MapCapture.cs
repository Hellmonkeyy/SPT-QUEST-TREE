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
    /// <see cref="SmoothingBandRows"/> rows, because the whole of it is a second and a half - its
    /// encode and write, and its sidecar. None of it can move off the main
    /// thread - ReadPixels, GetPixels, SetPixels32 and EncodeToPNG are all main-thread Texture2D
    /// calls - so a capture is a handful of short hitches on a key the player pressed, rather than
    /// one long freeze.
    ///
    /// Memory is BUDGETED, not hoped for. One floor may work in CaptureMemoryBudgetBytes - 256 MB - of
    /// arrays and textures, at WorkingSetBytesPerPixel (26 B a pixel: the float buffer, the drawn mask,
    /// two sets of distances, the picture, the sidecar texture and, on a merge, the previous picture),
    /// and the pixels per metre come down in half-metre steps until the floor fits. The figures below
    /// are of the harvested RECTANGLE, not of a map name - the rectangle is what the arithmetic sees,
    /// and a re-harvest moves it: a 965x925 m one (Interchange's, as this install measured it) at
    /// 4 px/m is 3860x3700 and 354 MB a floor, which is exactly what died in a raid - "GetPixels:
    /// scripting array creation failed" on its first floor and OutOfMemoryException on the other two -
    /// and it comes down through 3.5 px/m (271 MB, still over) to 3 px/m and 199 MB. A 1118x539 m one
    /// (Customs) at 4 px/m is 239 MB and is not touched. (Every figure here is what the capture header
    /// prints: mebibytes, the way the code divides.)
    ///
    /// What is outside that budget and small: the 34 MB half-float staging texture (one per CAPTURE),
    /// two 32 KB sample rows, a band's worth of Color32 and the encoded PNG. What is no longer in it at
    /// all: the managed arrays GetPixels used to hand back - 16 bytes a pixel for a staging band, four
    /// for a whole decoded picture - which is what fragmented the heap in the first place. Every
    /// readback now goes through GetPixelData, a view of the texture's own memory.
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

        /// <summary>Most pixels per metre, whatever the resolution setting allows. Four - a quarter of
        /// a metre to the pixel.
        ///
        /// Two was the first value, and the campaign capture of Customs at half a metre to the pixel is
        /// what argued it up: the buildings, vehicles and trees the LOD fix brought back are read at
        /// ten to forty pixels across at 0.5 m/px, which is enough to see that a warehouse is there and
        /// not enough to tell one door from the next. At 0.25 m/px a 4 m vehicle is 16 px and a
        /// stairwell is visible. Past four there is genuinely no more detail in the scene to record -
        /// the terrain base map and the LOD meshes run out - only a bigger file.
        ///
        /// It is a CAP, not a target: it binds only where the long-side setting does not, which is
        /// every map under about 2 km across at the 8192 setting, and it is what stops Factory's
        /// two-hundred-metre extent asking for forty pixels per metre.</summary>
        private const float MaxPixelsPerMetre = 4f;

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
        /// puddle's edge looking like the ground it is in, 17 reaches across the biggest pool on
        /// Customs at four pixels to the metre; a pixel with nothing but cyan and holes within 17 px is
        /// marked undrawn and left for another capture to fill.</summary>
        private static readonly int[] InpaintWindows = { 5, 9, 17 };

        /// <summary>Cyan pixels repainted per frame. Each is up to 289 samples of the float buffer, so
        /// twenty thousand of them is about the same work as one band of development - the unit the
        /// frame budget is built in.</summary>
        private const int InpaintChunkPixels = 20000;

        /// <summary>How many samples a side each output pixel is rendered from: 2, so every pixel is
        /// the average of four. The tile target stays 2048 and covers half the metres it did, which
        /// quadruples the tile count - Customs goes from 3x2 to 6x4 tiles - and leaves the float buffer,
        /// the drawn mask, the distances and everything downstream at the output resolution.
        ///
        /// It is here because of what a single sample per pixel looks like on a photographed map at a
        /// quarter of a metre to the pixel: every railing, wire, roof edge and tree trunk is a hard
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
        /// hiding it, see HoldWater. And if the player walks INTO a culling collider during that
        /// second, the game switches those components on while we hold them and the release switches
        /// them back off - the roof over their head goes until they cross the collider again. A second
        /// of that, on a key they pressed, against a picture with buildings in it.
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
        ///    the release puts the object back to what it was when the floor started, which is the
        ///    walked-into-a-collider case above.</summary>
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
        /// information about the map, all of which survives a percentile stretch. A 5x5 window at
        /// 0.25 m/px is a metre and a quarter across, which is smaller than anything on a map that
        /// matters and bigger than the speckle.</summary>
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
        /// as the tile readbacks Phase 0 measured at 10-61 ms.
        ///
        /// The band is a WORK SPLIT and nothing else: the luminance buffer carries the filter's halo
        /// either side (<see cref="FillLuminance"/>) and the smoothing reads the whole float buffer, so
        /// the picture is byte for byte the same at any band size. It costs a hundred and thirty-five
        /// frames instead of sixty-eight on a map that size, which is another second of wall clock and
        /// a shorter hitch in each of them.</summary>
        private const int SmoothingBandRows = 16;

        /// <summary>The spatial weights, built once: (2r+1)^2 of them, indexed row-major from the
        /// window's top-left.</summary>
        private static readonly float[] SmoothingKernel = BuildSmoothingKernel();

        /// <summary>The range weights, built once, indexed by luminance difference scaled by
        /// <see cref="SmoothingRangeScale"/>.</summary>
        private static readonly float[] SmoothingRangeWeights = BuildSmoothingRangeWeights();

        /// <summary>The luminance difference past which a neighbour is skipped.</summary>
        private const float SmoothingRangeCut = SmoothingRangeReach * SmoothingSigmaRange;

        private const float SmoothingRangeScale = (SmoothingRangeSteps - 1) / SmoothingRangeCut;

        /// <summary>Rows developed in one frame: fewer when every pixel costs a 5x5 window.</summary>
        private static int DevelopBandRows => SmoothingEnabled ? SmoothingBandRows : PixelBandRows;

        /// <summary>Most managed and texture memory one floor of a capture may work in. Two hundred and
        /// fifty-six megabytes.
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
        /// easier thing for an allocator to place. At 256 MB a 1118x539 m rectangle (Customs) keeps its
        /// 4 px/m and its 239 MB - which also keeps the captures already on disk mergeable - and a
        /// 965x925 m one (Interchange) walks 4 px/m (354 MB) past 3.5 (271 MB, still over) and lands at
        /// 3 px/m and 199 MB.</summary>
        private const long CaptureMemoryBudgetBytes = 256L * 1024L * 1024L;

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
            ";reach" + (ReachEnabled ? (ReachIsAlpha ? "2" : "1") : "0") +
            ";ss" + SupersampleFactor.ToString(CultureInfo.InvariantCulture) +
            ";msaa" + _msaa.ToString(CultureInfo.InvariantCulture) +
            ";layers" + LayerListVersion.ToString(CultureInfo.InvariantCulture);

        /// <summary>Added to the far plane so the band's own floor is comfortably inside it rather
        /// than exactly on it.</summary>
        private const float FarClipSlack = 1f;

        /// <summary>A floor's PNG is not written past this. 48 MB, four times the first value, because
        /// the pixel count went up four times with <see cref="MaxPixelsPerMetre"/>: Customs is
        /// 4472x2156 now, 9.6 million pixels, which a PNG of a photographed map encodes to somewhere
        /// around 10-25 MB. Hitting 48 still means something is wrong - noise rather than a map, or a
        /// resolution nobody wants - and the local capture is the only thing this bounds: what travels
        /// to a host and what ships in the zip are downscaled to 2048 long side by MapTransfer and
        /// package.ps1 respectively.</summary>
        private const int MaxFloorPngBytes = 48 * 1024 * 1024;

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

        /// <summary>Two rows of samples, reused by every tile of every floor: one per row of a
        /// supersample block, four floats a sample. 2048 samples is 32 KB a row, which is the entire
        /// managed cost of the readback now - see ReadSampleRow.</summary>
        private float[][] _sampleRows;

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

                        // Whatever it had allocated before it gave up goes back HERE, not at Cleanup.
                        // The floor's three buffers are the last thing BeginFloor does and an
                        // allocation failure is one of the ways it fails, so this path is exactly the
                        // one where the next floor - asking for the same sizes a frame later - must not
                        // be measured against a heap still holding this one's.
                        ReleaseTexture(floor);
                        continue;
                    }

                    // The scene's distance culling forced visible and its water hidden for the whole
                    // floor, rather than around each render: see ForceCulling for the measurement that
                    // decides it and for what the player's own frames look like meanwhile. Never
                    // throws, and the release below runs however the tiles went - including a floor
                    // abandoned at its first tile - while Cleanup releases it again for a raid that
                    // ends mid-floor.
                    HoldScene();

                    // Its own frame, like every other step here: the hold is the one pass over all
                    // twenty-seven thousand components, and putting it in the same frame as the first
                    // tile's render and readback would make that frame the longest of the capture.
                    yield return null;

                    for (var tile = 0; tile < plan.TileCount; tile++)
                    {
                        RenderTile(plan, floor, tile);
                        if (floor.Failed) break;

                        // The whole point of the coroutine: one render and one readback per frame,
                        // never two.
                        yield return null;
                    }

                    ReleaseScene();

                    // The water quads go before the exposure is measured, not just before the merge:
                    // a flat cyan pool is one of the brightest things in a capture, and the rain
                    // capture's 98th percentile was read off exactly this kind of object. Painting
                    // them out first means the exposure describes the map.
                    if (!floor.Failed)
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
                    if (!ReferenceEquals(floor, plan.Floors[plan.Floors.Count - 1])) GC.Collect();
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
                var wanted = (float)Math.Min(cap / longSide, MaxPixelsPerMetre);
                var ppm = Budget(wanted, widthM, heightM, out var budgetNote);

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

                // Counted in SAMPLES, not output pixels: a tile is 2048 samples, which at
                // SupersampleFactor 2 is 1024 output pixels, so a Customs-sized floor takes 6x4 tiles
                // where one sample a pixel took 3x2.
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

                if (!BuildCamera(plan, out var note))
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: nothing was captured on {key} - {note}.");
                    plan = null;
                    return false;
                }

                // After the camera, because whether the previous capture can be merged into this one
                // depends on the encoding the camera decided (see LoadPrevious).
                plan.Previous = LoadPrevious(plan, _needsGamma, RenderTag);
                plan.Captures = plan.Previous == null ? 1 : Math.Max(1, plan.Previous.Captures) + 1;
                plan.FirstCapturedAt = plan.Previous == null
                    ? null
                    : FirstOf(plan.Previous);

                if (_culling != null && _culling.Length > 0)
                {
                    note = $"{note}, culling forced ({_cullingObjects} objects)";
                }

                note = _water != null && _water.Length > 0 && _waterFlat != null
                    ? $"{note}, {_water.Length} water-layer renderers painted"
                    : $"{note}, water drawn as is";
                if (plan.Reach != null) note = $"{note}, reach mask {plan.ReachCellsX}x{plan.ReachCellsZ}";

                if (budgetNote != null) note = $"{note}, {budgetNote}";

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

                        for (var dy = 0; dy < SupersampleFactor; dy++) ReadSampleRow(row + dy, tw, _sampleRows[dy]);

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
        /// <param name="stageRow">The row of the staging texture, counting from its bottom.</param>
        /// <param name="samples">How many samples of that row to read.</param>
        /// <param name="into">The row buffer to fill, four floats a sample.</param>
        private void ReadSampleRow(int stageRow, int samples, float[] into)
        {
            var from = stageRow * TileSize;

            if (_hdr)
            {
                var data = _stage.GetPixelData<Half4>(0);

                for (var x = 0; x < samples; x++)
                {
                    var sample = data[from + x];
                    var at = x * 4;

                    into[at] = Mathf.HalfToFloat(sample.R);
                    into[at + 1] = Mathf.HalfToFloat(sample.G);
                    into[at + 2] = Mathf.HalfToFloat(sample.B);
                    into[at + 3] = Mathf.HalfToFloat(sample.A);
                }

                return;
            }

            var bytes = _stage.GetPixelData<Color32>(0);

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

                if (png.Length > MaxFloorPngBytes)
                {
                    // Not written rather than written and large: these files ship in the release zip
                    // and are uploaded to Fika hosts, and a floor this size is a sign the picture is
                    // noise rather than a map.
                    floor.Failed = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" encoded to {png.Length} bytes, over the " +
                        $"{MaxFloorPngBytes / (1024 * 1024)} MB a floor may take - it was not written. Set Settings > " +
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
        /// <param name="renderTag">This capture's render recipe, which the stored one has to equal. Passed
        /// in rather than read from the property, because it depends on the multisampling this capture's
        /// device actually granted and this method is static.</param>
        private static CaptureMeta LoadPrevious(Plan plan, bool needsGamma, string renderTag)
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

                if (!string.Equals(meta.Render, renderTag, StringComparison.Ordinal))
                {
                    // The render recipe is part of what a pixel IS. A capture taken before the recipe was
                    // recorded is one of the black rain-era or building-less pictures this replaces; one
                    // taken under a different light would merge into a visible seam, and one taken without
                    // the LOD bias would win the distance test over ground that actually has buildings in
                    // it. Any difference at all, and this capture starts fresh.
                    Fresh(plan, string.IsNullOrEmpty(meta.Render)
                        ? $"it was taken before the render recipe was recorded, and this one is rendered {renderTag}"
                        : $"it was rendered {meta.Render} and this one is rendered {renderTag}");
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
                // the 3 px/m its memory budget settles that map on - to read one channel out of it,
                // which is most of what made the merge unaffordable.
                var red = new byte[plan.WidthPx * plan.HeightPx];
                if (!CopyRed(texture, red, plan, floor)) return;

                floor.PreviousDist = red;
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

                // RGBA32 asked for; LoadImage reformats to suit the PNG anyway, which is why the two
                // copies below both check what they actually got.
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);

                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {plan.Key} \"{floor.Dto.Name}\" has a {what} that is not a readable image - " +
                        "this capture draws over it.");

                    Destroy(texture);
                    return null;
                }

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
        /// handling the two formats a PNG decode actually produces here: RGBA32 for a picture with an
        /// alpha channel, which is what this build writes, and RGB24 for one without, which is what
        /// every capture before the walkable mask wrote. Anything else is refused rather than
        /// misread.</summary>
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

            for (var y0 = 0; y0 < plan.HeightPx; y0 += DevelopBandRows)
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
        private static byte[] BuildReach(Plan plan)
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

                    if (!Mark(plan, distance, cellsX, cellsZ, a, b, c)) continue;
                    triangles++;
                }

                if (triangles == 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: none of {plan.Key}'s NavMesh falls inside its extent, so the whole picture is " +
                        "drawn as reachable.");
                    return null;
                }

                Sweep(distance, cellsX, cellsZ);

                var inside = Math.Max(1, (int)Math.Round(ReachDilateMetres / ReachCellMetres));
                var ramp = Math.Max(1, (int)Math.Round(ReachRampMetres / ReachCellMetres));

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
                // A triangle with no area in XZ - a wall's worth of NavMesh, seen edge on. Its bounding
                // box is one row or column of cells and marking them all is right.
                for (var z = fromZ; z <= untilZ; z++)
                {
                    for (var x = fromX; x <= untilX; x++) distance[z * cellsX + x] = 0;
                }

                return true;
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
        /// interpolation - the cells are eight pixels across at a quarter of a metre to the pixel, and
        /// nearest-cell sampling would draw the ramp as a staircase of 8-pixel blocks.</summary>
        /// <param name="plan">The capture's plan.</param>
        /// <param name="col">The pixel's column.</param>
        /// <param name="row">The pixel's row in texture order, counting from the bottom.</param>
        private static float ReachAt(Plan plan, int col, int row)
        {
            var reach = plan.Reach;
            if (reach == null) return 1f;

            var cellsX = plan.ReachCellsX;
            var cellsZ = plan.ReachCellsZ;

            // Texture row 0 is the extent's -z edge, and so is cell row 0, so this axis needs no flip.
            var x = ((col + 0.5f) / plan.Ppm) / ReachCellMetres - 0.5f;
            var z = ((row + 0.5f) / plan.Ppm) / ReachCellMetres - 0.5f;

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

            if (!ForceCulling) return;

            try
            {
                var clock = Stopwatch.StartNew();
                var components = new List<Component>();
                var objectsToTurnOn = new List<GameObject>();
                var objects = FindObjectsOfType<DisablerCullingObject>();

                foreach (var culler in objects)
                {
                    if (culler == null) continue;

                    _cullingObjects++;
                    Take(components, culler._componentsToTurnOff);
                    Take(components, culler._compsToTurnOffWhoIgnoreInversedColliders);
                    TakeObjects(objectsToTurnOn, culler._gameObjectsToTurnOff);
                }

                _culling = components.ToArray();
                _cullingWasEnabled = new bool[_culling.Length];
                _cullingObjectsHeld = objectsToTurnOn.ToArray();
                _cullingObjectWasActive = new bool[_cullingObjectsHeld.Length];

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {_cullingObjects} culling object(s) hold {_culling.Length} renderer(s) and LOD " +
                    $"group(s) plus {_cullingObjectsHeld.Length} whole GameObject(s) the capture will force " +
                    $"visible, found in {Ms(clock.Elapsed.TotalMilliseconds)} ms.");
            }
            catch (Exception ex)
            {
                _culling = null;
                _cullingWasEnabled = null;
                _cullingObjectsHeld = null;
                _cullingObjectWasActive = null;
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
        /// it again: the counts are cleared here, so the second call has nothing to do.</summary>
        private void ReleaseScene()
        {
            try
            {
                if (_culling != null)
                {
                    for (var i = 0; i < _cullingHeld && i < _culling.Length; i++)
                    {
                        var component = _culling[i];
                        if (component == null || _cullingWasEnabled[i]) continue;

                        component.SetEnabledUniversal(false);
                    }
                }

                if (_cullingObjectsHeld != null)
                {
                    for (var i = 0; i < _cullingObjectsHeldCount && i < _cullingObjectsHeld.Length; i++)
                    {
                        var item = _cullingObjectsHeld[i];
                        if (item == null || _cullingObjectWasActive[i]) continue;

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
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scene could not be put back after a floor ({ex.GetType().Name}: " +
                    $"{ex.Message}).");
            }
            finally
            {
                _cullingHeld = 0;
                _cullingObjectsHeldCount = 0;
                ReleaseWater();
            }
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
        /// was a second 116 MB float buffer at 0.25 m/px, on top of a merge that already peaks near
        /// 300 MB.
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
        /// are thousands of them on a map.
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
                floor.Texture = new Texture2D(plan.WidthPx, plan.HeightPx, TextureFormat.RGBA32, mipChain: false);
                floor.Block = new Color32[plan.WidthPx * DevelopBandRows];

                // One band's stretched luminance plus the filter's halo - 640 KB at 0.25 m/px, against
                // the 116 MB a second full float buffer would have cost. See Smooth.
                // Wanted by the smoothing AND by the despeckle, which measures its neighbours on the
                // same stretched luminance.
                if (SmoothingEnabled || DespeckleEnabled)
                {
                    floor.LumBand = new float[plan.WidthPx * (DevelopBandRows + SmoothingRadius * 2)];
                    floor.NeighbourLum = new float[8];
                    floor.NeighbourIndex = new int[8];
                }

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

                var rows = Math.Min(DevelopBandRows, plan.HeightPx - y0);

                var clock = Stopwatch.StartNew();
                var lumFrom = floor.LumBand != null ? FillLuminance(plan, floor, y0, rows, low, scale) : 0;

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

                        // Sampled for EVERY pixel, not only the ones this capture supplies: the mask is a
                        // property of the map, so the share it dims is a fact about the picture rather
                        // than about this capture, and counting it inside the merge would have reported a
                        // third of the truth on a merge that kept two thirds of its pixels.
                        var reach = ReachAt(plan, col, textureRow);
                        if (reach < 1f) floor.Outside++;

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
                // What the development cost, in work rather than in frames - the smoothing is most of
                // it on a large floor, and this is the number that decides SmoothingBandRows.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {plan.Key} \"{floor.Dto?.Name}\" developed in {Ms(floor.SmoothMs)} ms of " +
                    $"main-thread work over {(plan.HeightPx + DevelopBandRows - 1) / DevelopBandRows} band(s) of " +
                    $"{DevelopBandRows} rows" +
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
            var main = LiveCamera();
            int copied;

            _camera = new GameObject("QuestTreeCaptureCamera").AddComponent<Camera>();

            if (main != null)
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

            // Read HERE and not at the CopyFrom above, which is the whole point of the move: the path a
            // camera reports changes with its projection - an orthographic camera does not get deferred
            // shading in this pipeline - so a path read before the orthographic switch describes the
            // camera we copied FROM and not the one that renders. The header said DeferredShading for a
            // capture that cannot have been deferred.
            note = $"{note} ({_camera.renderingPath}/{_camera.actualRenderingPath}, orthographic)";

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

            note = $"{note}, {SupersampleFactor}x supersampled, msaa {_msaa}";

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

            _stage = new Texture2D(
                TileSize, TileSize,
                _hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32,
                mipChain: false);

            _sampleRows = new float[SupersampleFactor][];
            for (var i = 0; i < _sampleRows.Length; i++) _sampleRows[i] = new float[TileSize * 4];

            // Unity's own switch for the samples the target was granted: without it the multisampled
            // target is allocated and resolved and nothing is antialiased by it. BuildCamera sets it
            // false, which is right until this is known - and it is known here.
            _camera.allowMSAA = _msaa > 1;

            BuildReflection();

            Plugin.LogSource?.LogDebug(
                $"QuestTree: capture target {_rt.format} with {_msaa}x multisampling, staging {_stage.format}, " +
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

            try
            {
                RenderSettings.fog = false;
                if (_light != null) _light.enabled = true;
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
                // First, and before anything is freed: a raid that ended between a floor's first and
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
                _cullingObjects = 0;
                _water = null;
                _waterMaterials = null;

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
            floor.Block = null;
            floor.DxSquared = null;
            floor.LumBand = null;
            floor.NeighbourLum = null;
            floor.NeighbourIndex = null;
            floor.Cyan = null;

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
                    Render = RenderTag,
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
        /// <param name="note">A phrase for the capture header when the budget lowered the scale, or when
        /// it could not lower it far enough; null when the scale asked for fits as it is.</param>
        private static float Budget(float wanted, double widthM, double heightM, out string note)
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
            while (ppm > MinBudgetPpm && WorkingSet(widthM, heightM, ppm) > CaptureMemoryBudgetBytes)
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
            if (settled > CaptureMemoryBudgetBytes)
            {
                note =
                    $"{Ppm(wanted)} px/m would need {Mb(first)} MB a floor and even {Ppm(ppm)} px/m, the lowest " +
                    $"scale there is, needs {Mb(settled)} MB - over the {Mb(CaptureMemoryBudgetBytes)} MB budget " +
                    "even at the floor, so this is captured over budget";

                return ppm;
            }

            if (ppm >= wanted) return ppm;

            note =
                $"{Ppm(wanted)} px/m would need {Mb(first)} MB a floor, over the {Mb(CaptureMemoryBudgetBytes)} MB " +
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
            var width = (long)Math.Ceiling(widthM * ppm);
            var height = (long)Math.Ceiling(heightM * ppm);

            return width * height * WorkingSetBytesPerPixel;
        }

        private static string Mb(long bytes) =>
            (bytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>The long side the picture is allowed, from the setting, held to something a
        /// texture and a release zip can carry whatever a hand-edited config file says.</summary>
        private static int Resolution()
        {
            var value = ModSettings.Ready && ModSettings.CaptureResolution != null
                ? ModSettings.CaptureResolution.Value
                : 8192;

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

        // --- driving a capture from outside ------------------------------------------------------

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
        public static bool TryStartCapture()
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
