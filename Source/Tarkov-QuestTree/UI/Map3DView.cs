using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuestTree.QuestGraph;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The map as geometry: the captured picture draped over the ground heights a raid measured, with the
    /// buildings standing on it, rendered into the Maps tab's viewport.
    ///
    /// HOW IT DRAWS, and why this shape rather than any of the obvious ones. There is no scene, no
    /// MeshRenderer and no enabled camera anywhere in here. Every frame this component queues its meshes
    /// for one private camera with <see cref="Graphics.DrawMesh(Mesh, Matrix4x4, Material, int, Camera)"/>
    /// and then renders that camera BY HAND. Phase 3-0 measured both candidates in the real menu
    /// (captures/menu.meshprobe.txt, 2026-09-23): this path lit 64 of 64 sampled pixels at 0.37 ms a
    /// frame, and the alternative - an enabled camera with <c>cullingMask = 0</c> and a CommandBuffer on
    /// AfterForwardOpaque - drew nothing at all. So this is the path that works, and the mechanics below
    /// are that experiment's, promoted to production code.
    ///
    /// WHY NOTHING OF OURS LEAKS INTO THE MENU OR THE HIDEOUT, which is the risk a second camera and a
    /// second light in someone else's scene actually carries:
    ///
    ///   - the camera is <c>enabled = false</c>, so Unity never renders it in its own loop; the only
    ///     render it ever does is the <see cref="Camera.Render"/> call in <see cref="RenderNow"/>;
    ///   - it draws one layer, chosen at runtime as a layer with NO Renderer on it at all, active or
    ///     inactive (see <see cref="ChoosePrivateLayer"/>) - a camera-mask test is the wrong test,
    ///     because EFT switches distant renderers off and a layer that looks free is then a statement
    ///     about where the player is standing;
    ///   - the directional light is disabled and is switched on ONLY inside the render bracket, and off
    ///     again in a finally. A per-light culling mask is honoured in forward rendering (the path this
    ///     camera uses, for the oblique cut - see PrivateCameraPath) and is not a guarantee under the
    ///     deferred path the game uses, so the mask is not the safeguard: the light being off outside
    ///     those two statements is;
    ///   - scene fog is switched off for the render and restored in the same finally, because our
    ///     geometry is hundreds of metres across and the menu's fog would swallow it;
    ///   - <see cref="OnDisable"/> stops rendering entirely, and <see cref="OnDestroy"/> destroys every
    ///     mesh, material, texture, camera and light it made. The viewport is destroyed by
    ///     MapView.DiscardViewport and by the menu teardown, and this component goes with it.
    ///
    /// The camera and the light are ROOT GameObjects rather than children of the viewport. That is not an
    /// oversight: a Camera under a uGUI canvas inherits the canvas's transform, and an overlay canvas is
    /// positioned and scaled in SCREEN pixels - a camera under one has its view matrix scaled by the
    /// canvas scale factor and offset by half the screen, which is not a camera that can be placed in
    /// world space. They are destroyed explicitly, and they are ordinary objects of the menu scene (not
    /// DontDestroyOnLoad), so a scene change takes them even if nothing else does.
    ///
    /// WHAT THE MARKERS DO. In 2D the map container's local units ARE map metres, so a pin is placed once
    /// and pan and zoom move it for free. Here there is no such rect - the ground is geometry inside a
    /// camera - so this component implements <see cref="IOverlayHost"/> the other way round: it records
    /// each registered rect's map (x, z) and re-places it every frame from
    /// <see cref="Camera.WorldToViewportPoint(Vector3)"/>, at the ground height under that point plus
    /// a metre. The builders in MapView cannot tell which host they have.
    /// </summary>
    internal sealed class Map3DView : MonoBehaviour, IOverlayHost
    {
        // --- the view's constants -----------------------------------------------------------------

        /// <summary>The camera's vertical field of view. 45 as the experiment ran it.</summary>
        private const float FieldOfView = 45f;

        private const float NearClip = 0.5f;

        /// <summary>Far enough for the diagonal of the biggest map from outside it. Streets' extent is
        /// about 1.3 km across, and the dolly is capped at 1.5 times the diagonal.</summary>
        private const float FarClip = 4000f;

        /// <summary>The closest the dolly comes. Thirty metres is about a building's width across the
        /// screen: closer than that the relief's 2 m cells are visible as facets and there is nothing
        /// more to see.</summary>
        private const float MinDistance = 30f;

        /// <summary>The furthest, as a multiple of the extent's diagonal.</summary>
        private const float MaxDistanceOfDiagonal = 1.5f;

        private const float MinPitch = 15f;
        private const float MaxPitch = 89f;

        /// <summary>The opening tilt. Steep enough to read the map as a map, shallow enough that the
        /// buildings have sides.</summary>
        private const float DefaultPitch = 55f;

        /// <summary>Degrees of orbit per screen pixel dragged.</summary>
        internal const float OrbitDegreesPerPixel = 0.3f;

        /// <summary>How much of the distance one wheel notch takes off.</summary>
        internal const float DollyPerNotch = 0.12f;

        /// <summary>The light's intensity and direction, as the experiment ran them.</summary>
        private const float LightIntensity = 1.2f;

        /// <summary>Vertices per mesh chunk. Unity takes more than this in one mesh with
        /// <see cref="IndexFormat.UInt32"/>, but a chunked mesh is a mesh that can be freed and drawn in
        /// pieces, and the relief of a 4-million-cell band would otherwise be one 96 MB buffer.</summary>
        private const int MaxVerticesPerMesh = 250_000;

        /// <summary>
        /// How far the mesh file's extent may differ from the picture's before the mesh is refused, in
        /// metres.
        ///
        /// Five centimetres, and the tightness is the point: the two are written from the SAME doubles by
        /// the same capture, and the only thing between them is the meta's extent being read back as a
        /// float - which at the ±2 km a map coordinate reaches costs about 0.0002 m. A metre of slack
        /// would have been 4,000 times the error it was there to absorb, and a check with that much slack
        /// in it is a check that cannot fail: a re-harvested extent moves by tens of metres (Interchange
        /// moved 1073x1033 against a meta of 965x925), but a cell-sized disagreement is exactly the kind
        /// that draws a plausible map with everything in the wrong place.
        /// </summary>
        private const float ExtentTolerance = 0.05f;

        /// <summary>The shaders this view will make a material from, in order of preference. All four
        /// were confirmed loaded and supported in the menu by phase 3-0 experiment 3; the first one
        /// wins, so in practice this is Standard. NOT UI/Default, which is ZWrite Off - a scene drawn
        /// with it has no depth order at all and a wall behind a hill draws in front of it.</summary>
        private static readonly string[] Shaders =
        {
            "Standard",
            "Legacy Shaders/Diffuse",
            "Unlit/Texture",

            // The floor, and the only one of the four with no texture: it draws vertex colours. A view
            // that falls through to here gets flat grey geometry and says so in the log.
            "Hidden/Internal-Colored"
        };

        /// <summary>The flat colours used when only Hidden/Internal-Colored resolved: the ground and the
        /// buildings, told apart by shade so the map is still readable without the picture on it.</summary>
        private static readonly Color32 FlatGroundColour = new Color32(120, 118, 110, 255);
        private static readonly Color32 FlatBuildingColour = new Color32(168, 164, 156, 255);

        // --- what one drawn floor holds -----------------------------------------------------------

        /// <summary>One floor of the peel: its relief, its buildings, and the two materials that carry its
        /// picture. Two per floor and not one per mesh, because the texture is the only thing that
        /// differs between the meshes - and TWO rather than one because the ground clips on the picture's
        /// alpha and the buildings must not. See <see cref="MakeGroundMaterial"/>.</summary>
        private sealed class Floor
        {
            public int Level;

            /// <summary>The map layer this floor's picture comes from, or null when the mesh has a band
            /// the pictures do not - which happens to a capture whose upper floor's PNG was dropped.
            /// Drawn untextured rather than not drawn: the shape of the ground is most of the value.</summary>
            public DynamicMapsLibrary.MapLayer Layer;

            /// <summary>The relief's material: alpha-clipped where the shader has a cutout, so the
            /// picture's transparent surround does not draw as a black apron over ground the player can
            /// never reach. See <see cref="MakeGroundMaterial"/>.</summary>
            public Material GroundMaterial;

            /// <summary>The buildings' material: always OPAQUE. A wall's UVs are the planar ones the
            /// ground uses, so a wall standing near the edge of the walkable area samples transparent
            /// pixels up its height - and a cutout there would cut the wall away rather than the
            /// ground.</summary>
            public Material BuildingMaterial;

            /// <summary>The geometry, which this floor does NOT own: it belongs to the static cache and
            /// outlives every view of this map. See <see cref="Built"/>.</summary>
            public Built Meshes;
        }

        /// <summary>
        /// One band's built geometry, cached across views.
        ///
        /// The meshes are the expensive part of opening a 3D map - 151,000 vertices triangulated and
        /// uploaded - and EVERY repaint destroys the viewport: a quest row clicked, a floor stepped, a
        /// setting touched, the pin filter turned. Rebuilding them per click was a hitch per click. So
        /// they are built once per (file, floor) and handed to each new view; the materials are not
        /// cached, because they are one allocation each and they carry per-view state.
        ///
        /// Which means nothing here may be destroyed by a view's teardown while it is still cached. It
        /// is dropped by <see cref="DropCaches"/>, which MapView calls when the map memory goes: a
        /// profile change, a capture landing, and the menu teardown before a raid - the last of those
        /// being the one that matters, since a Mesh is not a scene object and a scene change does not
        /// take it.
        ///
        /// REFERENCE-COUNTED, because a drop and a live view are not ordered. MapView.ForgetDrawnMap
        /// destroys the viewport and then drops the caches in the same frame, and Destroy is deferred -
        /// so the view is still drawing when its meshes are asked to go. A drop therefore takes each
        /// entry OUT of the cache at once (no later view can reuse it) but only destroys the meshes of
        /// an entry nobody is drawing; an entry still in use is marked <see cref="Orphaned"/>, and the
        /// last view to let go of it destroys it (<see cref="Unuse"/>). Every order ends with the meshes
        /// destroyed exactly once and never under a view that is using them.
        /// </summary>
        internal sealed class Built
        {
            public readonly List<Mesh> Ground = new List<Mesh>();
            public readonly List<Mesh> Buildings = new List<Mesh>();

            /// <summary>How many live views are drawing this entry.</summary>
            public int Users;

            /// <summary>Taken out of the cache by a drop while still in use: the last user destroys it.</summary>
            public bool Orphaned;

            /// <summary>Set only when the ground AND the buildings were both built. An entry put in the
            /// cache before its build (so a throw halfway cannot leak what was already made) and never
            /// finished is not reused - it is destroyed and built again.</summary>
            public bool Complete;

            public long GroundTriangles;
            public long BuildingTriangles;
            public int BuildingCount;
            public long Cells;

            /// <summary>The buildings' vertical faces, one entry per colour: see <see cref="WallTint"/>.
            /// Empty until <see cref="WallsPending"/> clears. Owned here like the rest of the geometry -
            /// meshes AND materials - and destroyed with it by <see cref="DestroyBuilt"/>.</summary>
            public readonly List<WallTint> Walls = new List<WallTint>();

            /// <summary>Wall triangles found by the roof pass, whether or not the walls are built yet -
            /// counted into <see cref="BuildingTriangles"/> so the totals do not change when they are.</summary>
            public long WallTriangles;

            /// <summary>The walls still have to be built, because their colours come from this floor's
            /// picture and the picture was not decoded yet when the roofs were. Built by the first frame
            /// that has it - see <see cref="Map3DView.Draw"/>.</summary>
            public bool WallsPending;

            /// <summary>How many colours the walls were built in, for the log line.</summary>
            public int Tints;

            /// <summary>The faces textured from a side picture, one slot per compass side in
            /// <see cref="SideOrder"/> order; null where that side's picture is absent. Meshes and their
            /// material live here like the rest, and go with the entry.</summary>
            public readonly SideTexture[] Sides = new SideTexture[4];

            /// <summary>Building triangles given the top-down picture (roofs), a side picture, or a tint -
            /// the three shares the build log line reports.</summary>
            public long TopTriangles;
            public long SideTriangles;

            /// <summary>
            /// The faces textured with the game's OWN materials (Stage X): one mesh list and one material per TILE
            /// this floor uses - one per game material - so each material a band draws is one draw call. UVs are the
            /// material's raw UVs, in repeats of its tile, which the tile's Repeat wrap tiles on the GPU (a wall 40
            /// repeats long is 40 bricks, not a stretched one). So a brick wall is brick and a crane is a crane - where the
            /// projected textures (top, sides, tints) can only paint what a camera saw from outside. Faces of a
            /// building with no captured texture keep those projected textures.
            /// </summary>
            public readonly List<SideTexture> Atlas = new List<SideTexture>();

            /// <summary><see cref="Atlas"/> by tile, for the upload units. Main thread only.</summary>
            public readonly Dictionary<int, SideTexture> AtlasByTile = new Dictionary<int, SideTexture>();

            /// <summary>Building triangles textured from an atlas page, for the log line.</summary>
            public long AtlasTriangles;

            /// <summary>
            /// Top faces that stand ON ANOTHER FLOOR than the band their building is filed under, with the
            /// level whose picture textures them. See <see cref="Prep.FloorForFace"/>.
            ///
            /// Seen on Interchange: the mall's ground-floor slab (184,000 m2 of floor at y = 21 m) belongs to
            /// a building the builder filed under the BASEMENT band, because the same object reaches down to
            /// the parking level at 16 m. Textured with its building's band, the ground floor of the mall
            /// sampled the basement picture - transparent over the whole mall, so it drew as the black of a
            /// transparent pixel's RGB, speckled where the basement picture happened to be opaque. A face on
            /// a floor now takes that floor's picture: the same one the relief under it uses.
            /// </summary>
            public readonly List<(int Level, Mesh Mesh)> RoofsOnOtherFloors = new List<(int Level, Mesh Mesh)>();

            /// <summary>How many top faces went to another floor's picture, for the log line.</summary>
            public long MovedRoofTriangles;

            /// <summary>How many ground-skirt faces were left out as ground, for the log line. See
            /// Prep.GroundSkirt.</summary>
            public long GroundSkirtTriangles;

            /// <summary>A wall build for this entry is in flight - a worker, or its meshes being uploaded - by
            /// the view that started it. Abandoned with that view, which puts the entry back to waiting.</summary>
            public bool WallsRunning;

            /// <summary>What a side's faces are drawn with while that side's picture is not there - still
            /// decoding, evicted, or failed. Untextured, matte, in <see cref="WallAverage"/>. Made on first
            /// need and destroyed with the entry. Without it those faces were skipped, and every wall facing
            /// a side whose picture never came was a hole for the session - worse than having no sides.</summary>
            public Material SideFallback;

            /// <summary>The mean of this floor's wall tints once they are built, else the fallback grey: what
            /// a side's faces look like while their picture is missing, so they match the tinted walls.</summary>
            public Color WallAverage = new Color(0.55f, 0.53f, 0.50f, 1f);

            /// <summary>Building triangles dropped for having a vertex that is not a finite number -
            /// a NoHit y in the file dequantises to NaN, and one NaN vertex in a merged bucket makes
            /// the whole bucket's bounds NaN, which fails frustum culling and takes the bucket off
            /// screen entirely.</summary>
            public int Dropped;
        }

        /// <summary>The faces of one floor's buildings that one side picture textures, and the material
        /// that carries it. The picture itself is not held here: the material's texture is assigned by
        /// each view from its OWN entry's side picture, every frame it is missing, exactly as the floors'
        /// pictures are - an entry held over a capture rescan must not keep a released picture alive.</summary>
        internal sealed class SideTexture
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();
            public Material Material;

            /// <summary>For an atlas group: the tile (<see cref="TileStore"/> index) its material draws. -1 for a side.</summary>
            public int Tile = -1;
        }

        /// <summary>The compass sides in slot order. Slot i of <see cref="Built.Sides"/> and of a view's
        /// side pictures is SideOrder[i].</summary>
        private static readonly string[] SideOrder = { "N", "S", "E", "W" };

        /// <summary>
        /// The walls of every building on a floor whose colour fell in one bucket: their meshes and the
        /// one material that colours them.
        ///
        /// Buckets rather than a colour per building, because the Standard shader has no per-vertex
        /// colour: a colour is a material, a material is a draw call, and two hundred buildings would be
        /// two hundred draw calls a frame. Sixteen colours at most per floor is sixteen - and on a map
        /// whose roofs are concrete, red iron and grey felt, sixteen is more than the picture has to
        /// say.
        /// </summary>
        internal sealed class WallTint
        {
            public readonly List<Mesh> Meshes = new List<Mesh>();

            /// <summary>Untextured, matte, coloured. NULL when the shader has no _Color to tint with
            /// (Unlit/Texture, or the flat-colour fallback) - those walls then draw with the floor's own
            /// building material, which is exactly how they drew before walls had colours.</summary>
            public Material Material;

            public Color Colour;
        }

        // --- the orbit's state --------------------------------------------------------------------

        /// <summary>Where the view is looking from, so a rebuild - a floor change, a quest selected, a
        /// setting touched - can put it back. The 3D half of MapView's <c>_savedScale</c>/<c>_savedPan</c>.</summary>
        internal struct ViewState
        {
            public float Yaw;
            public float Pitch;
            public float Distance;
            public Vector2 Focus;
        }

        private float _yaw;
        private float _pitch = DefaultPitch;
        private float _distance;
        private Vector2 _focus;

        /// <summary>The orbit, for MapView to keep across a rebuild.</summary>
        internal ViewState State => new ViewState
        {
            Yaw = _yaw,
            Pitch = _pitch,
            Distance = _distance,
            Focus = _focus
        };

        // --- what it was given --------------------------------------------------------------------

        private RectTransform _viewport;
        private DynamicMapsLibrary.MapEntry _entry;
        private int _selectedLevel;
        private string _mapKey = "";
        private string _meshPath = "";
        private Color _backdrop;

        /// <summary>Told when the mesh turns out to be unusable, so the Maps tab can draw this map in 2D
        /// instead of showing an empty viewport, with a short reason for the toggle's tooltip. Called at
        /// most once.</summary>
        private Action<string, string> _onRefused;

        /// <summary>This capture's side pictures by slot (<see cref="SideOrder"/>), null where absent.</summary>
        private readonly DynamicMapsLibrary.SidePicture[] _sides = new DynamicMapsLibrary.SidePicture[4];

        /// <summary>How many of <see cref="_sides"/> are present.</summary>
        private int _sideCount;

        /// <summary>The present sides as letters in slot order - "NSEW", "NE", "" - for the cache key and
        /// the log line.</summary>
        private string _sidesKey = "";

        /// <summary>Picture-cache slots this view has reserved for its sides, returned in
        /// <see cref="Release"/>.</summary>
        private int _reservedSides;

        /// <summary>
        /// Reads the entry's USABLE side pictures into their slots: present, and not already known to have
        /// failed to decode. Reserves nothing - the cache room is taken by <see cref="TakeSideRoom"/> once
        /// the shader is known, since under the flat-colour fallback the sides take no part at all.
        /// </summary>
        private void TakeSides()
        {
            if (_entry?.Sides == null) return;

            foreach (var side in _entry.Sides)
            {
                if (side?.Picture == null) continue;

                var slot = Array.IndexOf(SideOrder, side.Dir);
                if (slot < 0 || _sides[slot] != null) continue;

                // A picture this session has already failed to decode is not a side this view can use: the
                // key is made from the sides actually usable, so a view built now never waits on it.
                if (side.Picture.ArtworkFailed) continue;

                _sides[slot] = side;
            }

            CountSides();
        }

        /// <summary>Recounts <see cref="_sideCount"/> and <see cref="_sidesKey"/> from the slots.</summary>
        private void CountSides()
        {
            _sideCount = 0;
            _sidesKey = "";

            for (var slot = 0; slot < SideOrder.Length; slot++)
            {
                if (_sides[slot] == null) continue;

                _sideCount++;
                _sidesKey += SideOrder[slot];
            }
        }

        /// <summary>Takes picture-cache room for the sides this view uses, if it uses any and does not hold
        /// the room already. See DynamicMapsLibrary.ReserveSprites for why a 3D view with sides needs it.</summary>
        private void TakeSideRoom()
        {
            if (_reservedSides > 0) return;

            // The side pictures: held by this view for as long as it draws and in the floors' picture cache, so
            // without their room a full peel would evict a floor this view asks for again next frame. NOT the
            // atlas pages any more (stage X): those are decoded by the TileStore, cut into tiles and let go, and
            // never enter the picture cache - the tiles' bytes are counted in the build line instead.
            var room = SidesActive ? _sideCount : 0;
            if (room <= 0) return;

            DynamicMapsLibrary.ReserveSprites(room);
            _reservedSides = room;
        }

        /// <summary>Gives back whatever room this view holds. Idempotent. With <paramref name="evict"/> (the
        /// view closing or switched off) the side and page pictures it held past the flat map's ceiling are
        /// freed on the spot rather than whenever the next floor decode happens to trim the cache; a rebuild
        /// that takes the room straight back passes false, so the pages it still uses are not thrown away and
        /// decoded again.</summary>
        private void ReturnSideRoom(bool evict = true)
        {
            if (_reservedSides <= 0) return;

            if (evict) DynamicMapsLibrary.ReturnSprites(_reservedSides, HeldPictures());
            else DynamicMapsLibrary.ReserveSprites(-_reservedSides);

            _reservedSides = 0;
        }

        /// <summary>The side pictures this view holds, for <see cref="ReturnSideRoom"/>. (Atlas pages are not in the
        /// picture cache: see <see cref="TileStore"/>.)</summary>
        private IEnumerable<DynamicMapsLibrary.MapLayer> HeldPictures()
        {
            foreach (var side in _sides)
                if (side?.Picture != null) yield return side.Picture;
        }

        /// <summary>Whether the side pictures take part at all: they need a textured shader, so under the
        /// flat-colour fallback every wall is a tint, exactly as if the capture had no sides.</summary>
        private bool SidesActive => _sideCount > 0 && !_flatColours;

        /// <summary>The most atlas pages a map has: MapMeshFile's cap, referenced rather than copied.</summary>
        private const int MaxAtlasPages = MapMeshFile.MaxAtlasPages;

        /// <summary>This capture's USABLE atlas pages by page number, null where absent or already failed.</summary>
        private readonly DynamicMapsLibrary.AtlasPage[] _pages = new DynamicMapsLibrary.AtlasPage[MaxAtlasPages];

        private int _pageCount;

        /// <summary>The usable page numbers, for the cache key and the log line.</summary>
        private string _pagesKey = "";

        /// <summary>Whether the atlas takes part: a textured shader and at least one usable page. Under the flat
        /// fallback, or with no pages (a capture from before Stage W), every building face keeps the stage U/V
        /// rule, exactly as before.</summary>
        private bool AtlasActive => _pageCount > 0 && !_flatColours;

        /// <summary>Reads the entry's usable atlas pages into their slots - present and not already failed.</summary>
        private void TakeAtlas()
        {
            if (_entry?.AtlasPages != null)
            {
                foreach (var page in _entry.AtlasPages)
                {
                    if (page?.Picture == null || page.Page < 0 || page.Page >= MaxAtlasPages) continue;
                    if (_pages[page.Page] != null || page.Picture.ArtworkFailed) continue;

                    _pages[page.Page] = page;
                }
            }

            CountPages();
        }

        private void CountPages()
        {
            _pageCount = 0;
            _pagesKey = "";

            for (var page = 0; page < _pages.Length; page++)
            {
                if (_pages[page] == null) continue;

                _pageCount++;
                _pagesKey += (_pagesKey.Length > 0 ? "," : "") + page.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Why the mesh was refused, in a few words for the tooltip.</summary>
        private string _refusal = "";

        /// <summary>Whether <see cref="_refusal"/> is about the scene, not the file. See
        /// <see cref="LastRefusalIsScene"/>.</summary>
        private bool _sceneRefusal;

        // --- what it made -------------------------------------------------------------------------

        private RawImage _image;
        private RenderTexture _rt;
        private Camera _camera;
        private Light _light;
        private GameObject _cameraGo;
        private GameObject _lightGo;

        private int _drawLayer = -1;
        private int _privateMask;

        private readonly List<Floor> _floors = new List<Floor>();

        /// <summary>Where an overlay that is off screen is put: far enough outside the viewport that the
        /// mask culls it, near enough that no float precision is lost.</summary>
        private static readonly Vector2 Parked = new Vector2(-100000f, -100000f);

        /// <summary>The overlays to re-place, with the map (x, z) each one stands at.</summary>
        private readonly List<(RectTransform Rect, Vector2 Map)> _overlays =
            new List<(RectTransform, Vector2)>();

        private MapMeshFile _file;
        private MapMeshFile.ReliefBand _groundBand;
        private float _groundFallbackY;

        private Task<Loaded> _loading;

        /// <summary>The read that <see cref="BeginBuild"/> last built from, kept for a rebuild.</summary>
        private Loaded _loaded;
        private bool _built;
        private bool _broke;
        private int _rtWidth;
        private int _rtHeight;

        public event Action<float, Vector2> OnViewChanged;

        /// <summary>Bumped by every orbit, pan, dolly and fly-to, and by the mesh landing - whatever moves
        /// the camera. See <see cref="IOverlayHost.ViewVersion"/>: it is how a subscriber knows the view
        /// moved when the scale did not, which in 3D is every orbit and every pan.</summary>
        public int ViewVersion { get; private set; }

        /// <summary>Screen pixels per map metre at the focus distance - what the label cull and the
        /// at-rest pin names measure their collisions in. The vertical field of view spans
        /// <c>2 d tan(fov/2)</c> metres at the focus depth, and the viewport is that many canvas units
        /// tall, so this is the same quantity the 2D view's container scale is.</summary>
        public float Scale
        {
            get
            {
                var height = _viewport != null ? _viewport.rect.height : 0f;
                var span = 2f * Mathf.Max(1f, _distance) * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);

                return span > 0f ? Mathf.Max(0.0001f, height / span) : 1f;
            }
        }

        /// <summary>
        /// Where a map point is on screen, in the same canvas units an overlay's anchoredPosition is in -
        /// what the label cull and the at-rest pin names decide their overlaps with.
        ///
        /// NOT FINITE (NaN, NaN) for a point BEHIND the camera, where the projection flips and a point
        /// past the horizon would come back mirrored on the far side of the viewport. Not a fixed
        /// off-screen value: every point behind the camera would then project to the same spot, and the
        /// overlap deciders would have them hide each other. Both deciders treat a non-finite point as
        /// "not on screen" - hidden, and claiming no space.
        /// </summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        public Vector2 Project(Vector2 mapXZ) =>
            TryProject(mapXZ, out var local, out _) ? local : NotOnScreen;

        /// <summary>What <see cref="Project"/> answers for a point the view cannot place.</summary>
        private static readonly Vector2 NotOnScreen = new Vector2(float.NaN, float.NaN);

        /// <summary>The projection both <see cref="Project"/> and <see cref="PlaceOverlays"/> use.</summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        /// <param name="local">Its position in the viewport, in canvas units from the centre.</param>
        /// <param name="inside">Whether it lands on the viewport, with a margin of its own size.</param>
        /// <returns>False when the point is behind the camera.</returns>
        private bool TryProject(Vector2 mapXZ, out Vector2 local, out bool inside)
        {
            local = Parked;
            inside = false;

            if (_camera == null || _viewport == null) return false;

            var world = new Vector3(mapXZ.x, GroundAt(mapXZ.x, mapXZ.y) + 1f, mapXZ.y);
            var view = _camera.WorldToViewportPoint(world);

            if (!(view.z > 0f)) return false;

            local = new Vector2(
                (view.x - 0.5f) * _viewport.rect.width,
                (view.y - 0.5f) * _viewport.rect.height);

            inside = view.x > -OverlayMargin && view.x < 1f + OverlayMargin &&
                     view.y > -OverlayMargin && view.y < 1f + OverlayMargin;

            return true;
        }

        /// <summary>How far outside the viewport an overlay is still placed rather than parked, as a
        /// fraction of the viewport. A marker just off the edge is one the mask would clip anyway.</summary>
        private const float OverlayMargin = 0.15f;

        // --- construction --------------------------------------------------------------------------

        /// <summary>
        /// Puts a 3D view on a viewport, starts reading its mesh file, and returns it. Never throws: on
        /// any failure it tears itself down, tells the caller the mesh is refused and returns null, and
        /// the caller draws the flat picture.
        /// </summary>
        /// <param name="viewport">The Maps tab's viewport, which this component is added to.</param>
        /// <param name="entry">The map being drawn - its layers carry the pictures.</param>
        /// <param name="meshPath">The relief file, already checked for existence and length by
        /// MapCatalog.</param>
        /// <param name="mapKey">The map's key, for the log lines.</param>
        /// <param name="selectedLevel">The floor showing; this and everything below it are drawn.</param>
        /// <param name="backdrop">What the camera clears to, so the 3D view sits on the same colour the
        /// flat one does.</param>
        /// <param name="restore">The orbit to resume, or null to open on the fitted view.</param>
        /// <param name="onRefused">Called with the mesh path and a short reason if the file turns out to
        /// be unusable, so the Maps tab can say why its 3D toggle is grey.</param>
        internal static Map3DView Attach(
            RectTransform viewport, DynamicMapsLibrary.MapEntry entry, string meshPath, string mapKey,
            int selectedLevel, Color backdrop, ViewState? restore, Action<string, string> onRefused)
        {
            LastRefusal = "";
            LastRefusalIsScene = false;

            if (viewport == null || entry == null || string.IsNullOrEmpty(meshPath)) return null;

            var view = viewport.gameObject.AddComponent<Map3DView>();

            view._viewport = viewport;
            view._entry = entry;
            view._meshPath = meshPath;
            view._mapKey = mapKey ?? "";
            view._selectedLevel = selectedLevel;
            view._backdrop = backdrop;
            view._onRefused = onRefused;

            // The side pictures and atlas pages this capture has, by slot. Their cache room is taken once the
            // shader is known (TakeSideRoom).
            view.TakeSides();
            view.TakeAtlas();

            try
            {
                view.Build(restore);
                return view;
            }
            catch (Exception ex)
            {
                // A half-built view is a camera and a light loose in the menu, which is the one thing
                // this must not leave behind.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view for '{view._mapKey}' could not be built " +
                    $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");

                // NOT through Refuse: this failure is synchronous, so the caller is still inside its own
                // build and draws the flat picture in THIS pass. Telling it again through the callback
                // would only ask for a second repaint of a view that is already correct - so the reason
                // is handed over on the side instead, for the toggle's tooltip.
                LastRefusal = string.IsNullOrEmpty(view._refusal)
                    ? "could not be opened in this scene"
                    : view._refusal;

                LastRefusalIsScene = view._sceneRefusal;

                view._onRefused = null;
                view.Release();
                Destroy(view);

                return null;
            }
        }

        /// <summary>Why the last <see cref="Attach"/> that returned null gave up, for the caller to put in
        /// front of the player. A static because the alternative is an out parameter on a method whose
        /// other seven arguments are the interesting ones, and it is read on the line after the call that
        /// set it, on the one thread that builds UI.</summary>
        internal static string LastRefusal { get; private set; } = "";

        /// <summary>Whether <see cref="LastRefusal"/> is about the SCENE rather than the file - there is
        /// no spare layer to draw on right now. The caller must not hold that against the mesh file: the
        /// next scene may well have a free layer, and a file refusal lasts until a capture or a profile
        /// change.</summary>
        internal static bool LastRefusalIsScene { get; private set; }

        private void Build(ViewState? restore)
        {
            _drawLayer = PrivateLayer();

            if (_drawLayer < 0)
            {
                // Every layer has a renderer on it or is in a live camera's mask. Drawing on a shared
                // layer would put our geometry in front of the player's menu, so we do not draw at all.
                _refusal = "no spare layer in this scene";
                _sceneRefusal = true;

                throw new InvalidOperationException(
                    "no layer of the loaded scene is free of renderers, so there is nowhere private to draw");
            }

            _privateMask = 1 << _drawLayer;

            // The picture of the render, under the viewport's own backing plate but UNDER MapSpace too:
            // first sibling, because uGUI draws siblings in order and the markers live in MapSpace. Made
            // the wrong way round, every pin would be hidden behind the render of the map they are on.
            var imageGo = new GameObject("Map3DImage", typeof(RectTransform), typeof(RawImage));
            var rect = (RectTransform)imageGo.transform;
            rect.SetParent(_viewport, worldPositionStays: false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.SetAsFirstSibling();

            _image = imageGo.GetComponent<RawImage>();
            _image.color = Color.white;

            // OFF: the viewport's own backing Image is the raycast target the drag and scroll handlers
            // need, and a RawImage over it with this on would consume every gesture before the handlers
            // on the viewport saw it.
            _image.raycastTarget = false;

            EnsureRenderTexture();

            _cameraGo = new GameObject("QuestTreeMap3DCamera", typeof(Camera));
            _camera = _cameraGo.GetComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = _backdrop;
            _camera.orthographic = false;
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = NearClip;
            _camera.farClipPlane = FarClip;
            _camera.targetTexture = _rt;
            _camera.cullingMask = _privateMask;
            _camera.useOcclusionCulling = false;
            _camera.allowMSAA = false;

            // Forward, for the oblique cut: see PrivateCameraPath.
            _camera.renderingPath = PrivateCameraPath;

            // Below every camera the game has, so even a frame in which this one were somehow enabled
            // would draw under the menu rather than over it.
            _camera.depth = -50f;

            _lightGo = new GameObject("QuestTreeMap3DLight", typeof(Light));
            _lightGo.layer = _drawLayer;
            _light = _lightGo.GetComponent<Light>();
            _light.type = LightType.Directional;
            _light.intensity = LightIntensity;
            _light.shadows = LightShadows.None;
            _light.cullingMask = _privateMask;
            _light.enabled = false;
            _lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // The file read and the deflate are a worker's job; every Mesh it turns into is Unity's
            // thread only, and that happens in LateUpdate when this lands. A capture's relief is a
            // third of a megabyte and reads in a few milliseconds, but the panel must not wait on a
            // disk however fast it is - the same rule DynamicMapsLibrary's picture read follows.
            var path = _meshPath;
            var bound = _readBound = ViewerReadBound(out _readBoundVramMb);
            _loading = Task.Run(() => ReadFile(path, bound));

            _restored = restore.HasValue;

            if (restore.HasValue)
            {
                _yaw = restore.Value.Yaw;
                _pitch = Mathf.Clamp(restore.Value.Pitch, MinPitch, MaxPitch);
                _distance = restore.Value.Distance;
                _focus = restore.Value.Focus;
            }
            else
            {
                var layer = ResolveLayer();
                _focus = layer != null ? layer.BoundsCentre : Vector2.zero;
                _yaw = 0f;
                _pitch = DefaultPitch;
                _distance = FitDistance();
            }

            Place();

            // One render with nothing queued, so the viewport shows the backdrop colour from the first
            // frame. Without it the RawImage displays an uninitialised render texture - black, or worse -
            // for the two or three frames the file read takes, which reads as the map having failed.
            try
            {
                RenderNow();
            }
            catch (Exception ex)
            {
                // Not fatal: the first frames look wrong and the view still works.
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map's first clear failed ({ex.Message}).");
            }
        }

        /// <summary>A parsed file and the write time it was read at - which is half of the key the built
        /// meshes are cached under, and has to come back from the worker rather than be read again on the
        /// main thread, or a file rewritten between the two would be cached under the wrong stamp.</summary>
        private sealed class Loaded
        {
            public MapMeshFile File;
            public long Stamp;
        }

        /// <summary>The file, read and parsed off the main thread. A cache of one, keyed by path and
        /// write time: switching floor rebuilds the whole viewport, and re-inflating the same file for
        /// every step of the peel is work nobody asked for. The parsed object holds no Unity object, so
        /// it is safe to keep across a rebuild and across a scene change - and it is dropped with the
        /// meshes by <see cref="DropCaches"/>.</summary>
        /// <param name="path">The mesh file.</param>
        /// <param name="maxTriangles">The most triangles this machine will draw (<see cref="ViewerReadBound"/>).
        /// A file past it is refused by the reader before its arrays are allocated.</param>
        private static Loaded ReadFile(string path, long maxTriangles)
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;

            lock (CacheLock)
            {
                if (_cachedFile != null && _cachedPath == path && _cachedStamp == stamp)
                    return new Loaded { File = _cachedFile, Stamp = stamp };
            }

            int generation;
            lock (CacheLock) generation = _cacheGeneration;

            // Through a FileStream, not ReadAllBytes: the same bytes through the same reader, minus a
            // compressed copy of the whole file on the large-object heap.
            MapMeshFile parsed;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                parsed = MapMeshFile.Read(stream, maxTriangles);

            lock (CacheLock)
            {
                // Only if nothing dropped the cache while this read was in flight. A read started before
                // a drop and finished after it would otherwise put the dropped file straight back - after
                // the menu teardown, into the raid.
                if (generation == _cacheGeneration)
                {
                    _cachedFile = parsed;
                    _cachedPath = path;
                    _cachedStamp = stamp;
                }
            }

            return new Loaded { File = parsed, Stamp = stamp };
        }

        /// <summary>A graphics card's share the 3D view may fill with building triangles: a quarter of its
        /// memory at 64 bytes a triangle - twice the builder's share (MapMeshBuilder.MemoryCeiling), so a file
        /// built on a similar machine always fits.</summary>
        private const long ViewerVramShare = 4;

        /// <summary>GPU bytes per drawn triangle, for <see cref="ViewerReadBound"/>: 12 of indices plus ~1.2
        /// vertices at 32 bytes, with margin.</summary>
        private const long ViewerBytesPerTriangle = 64;

        /// <summary>
        /// MAIN THREAD (SystemInfo). The most building triangles this view reads:
        /// min(MapMeshFile.MaxTriangles, VRAM / 4 / 64 B) - 33.5 M on an 8 GB card, 8.4 M on a 2 GB one - or the
        /// format's own bound when the card does not say how much memory it has. Passed to MapMeshFile.Read,
        /// which refuses a file past it before allocating it, and the flat map is drawn instead.
        /// </summary>
        private static long ViewerReadBound(out int vramMb)
        {
            try { vramMb = SystemInfo.graphicsMemorySize; } catch (Exception) { vramMb = 0; }

            if (vramMb <= 0) return MapMeshFile.MaxTriangles;

            var byVram = ((long)vramMb << 20) / ViewerVramShare / ViewerBytesPerTriangle;

            return Math.Min(MapMeshFile.MaxTriangles, byVram);
        }

        /// <summary>This view's read bound and the VRAM it came from, for the refusal line.</summary>
        private long _readBound;
        private int _readBoundVramMb;

        private static readonly object CacheLock = new object();
        private static MapMeshFile _cachedFile;
        private static string _cachedPath;
        private static long _cachedStamp;

        /// <summary>Bumped by every <see cref="DropCaches"/>, under <see cref="CacheLock"/>. See
        /// <see cref="ReadFile"/>.</summary>
        private static int _cacheGeneration;

        /// <summary>The (file, write time) the built meshes in <see cref="_cachedFloors"/> belong to, or null.
        /// A file rewritten under the same name gets a new stamp and so a new key, which is what throws
        /// the geometry of the capture before last away.</summary>
        private static string _builtKey;

        /// <summary>The built geometry, per band level, for the file <see cref="_builtKey"/> names. Main
        /// thread only - every value holds Unity Meshes - and owned by this class rather than by any view:
        /// see <see cref="Built"/>.</summary>
        private static readonly Dictionary<int, Built> _cachedFloors = new Dictionary<int, Built>();

        /// <summary>The private layer this session settled on, or -1 before the first scan, after any
        /// failure and after any scene load. Once per SCENE rather than once per view: the scan walks
        /// every Renderer in the loaded scene, and a repaint happens on every click - but a scene that
        /// loads (the hideout, additively, into the menu) can put renderers on the very layer chosen, and
        /// ours would then draw them into the map without a line in the log. See
        /// <see cref="OnSceneLoaded"/>.</summary>
        private static int _sessionLayer = -1;

        /// <summary>Whether <see cref="OnSceneLoaded"/> is subscribed. Static and subscribed at most once:
        /// SceneManager.sceneLoaded is a static event, and a second subscription would be a second call
        /// per load, and a leaked one a call into a class that has dropped everything.</summary>
        private static bool _sceneHooked;

        /// <summary>A scene has loaded: the layer scan describes a scene that has changed, so the next
        /// view scans again. The live view keeps drawing on the layer it has until it is rebuilt - the
        /// panel rebuilds on the way back from any scene change that matters (the hideout, a raid).</summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _sessionLayer = -1;
        }

        private static void HookScenes()
        {
            if (_sceneHooked) return;

            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _sceneHooked = true;
            }
            catch (Exception ex)
            {
                // Not fatal: the layer is then re-scanned only after failures and drops, as before.
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map could not watch scene loads ({ex.Message}).");
            }
        }

        private static void UnhookScenes()
        {
            if (!_sceneHooked) return;

            try
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _sceneHooked = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map could not stop watching scene loads ({ex.Message}).");
            }
        }

        /// <summary>
        /// Drops everything cached across views: the parsed file, the built meshes and the chosen layer.
        ///
        /// Called by MapView when the map memory goes - a profile change, a capture landing, and the
        /// menu teardown before a raid. The last is the one that must not be missed: a Mesh is not a
        /// scene object, so nothing else would ever free it, and up to some tens of megabytes of map
        /// geometry would sit in the raid's memory for no reason.
        /// </summary>
        internal static void DropCaches()
        {
            DropBuiltMeshes();
            DropTiles();
            CancelAllPreps();

            lock (CacheLock)
            {
                _cachedFile = null;
                _cachedPath = null;
                _cachedStamp = 0L;
                _cacheGeneration++;
            }

            // The layer goes only on THIS path, not on a map switch. All three callers of DropCaches are
            // points where the loaded scene may have changed under us - a raid has been and gone by the
            // time the menu builds another map - and that is the one thing that can invalidate the scan.
            _sessionLayer = -1;

            // And the scene hook with it: nothing is cached any more for a scene load to invalidate, and
            // the next view's scan subscribes again.
            UnhookScenes();
        }

        /// <summary>
        /// Takes every built entry out of the cache and forgets which file they were built from. For a
        /// map switch, where the parsed file replaces itself and the layer is still good, and as the
        /// mesh half of <see cref="DropCaches"/>.
        ///
        /// An entry no view is drawing is destroyed now. One a LIVE view is still drawing - the view
        /// MapView.DiscardViewport has just destroyed gets one more LateUpdate, Destroy being deferred -
        /// is only orphaned, and that view destroys it when it lets go. See <see cref="Built"/>.
        /// </summary>
        private static void DropBuiltMeshes()
        {
            var destroyed = 0;
            var deferred = 0;

            foreach (var built in _cachedFloors.Values)
            {
                if (built == null) continue;

                if (built.Users > 0)
                {
                    built.Orphaned = true;
                    deferred++;
                    continue;
                }

                destroyed += DestroyBuilt(built);
            }

            _cachedFloors.Clear();
            _builtKey = null;

            if (destroyed > 0 || deferred > 0)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: dropped the cached 3D map - {destroyed} mesh(es) destroyed, {deferred} floor(s) " +
                    $"left to the view still drawing them.");
            }
        }

        /// <summary>Destroys one entry's meshes and empties it, so a second call is a no-op.</summary>
        /// <param name="built">The entry.</param>
        /// <returns>How many meshes were destroyed.</returns>
        private static int DestroyBuilt(Built built)
        {
            var count = 0;

            for (var i = 0; i < built.Ground.Count; i++) { Discard(built.Ground[i]); count++; }
            for (var i = 0; i < built.Buildings.Count; i++) { Discard(built.Buildings[i]); count++; }

            count += DestroyWalls(built);

            for (var i = 0; i < built.RoofsOnOtherFloors.Count; i++) { Discard(built.RoofsOnOtherFloors[i].Mesh); count++; }
            built.RoofsOnOtherFloors.Clear();

            Discard(built.SideFallback);
            built.SideFallback = null;

            for (var slot = 0; slot < built.Sides.Length; slot++)
            {
                var side = built.Sides[slot];
                if (side == null) continue;

                for (var i = 0; i < side.Meshes.Count; i++) { Discard(side.Meshes[i]); count++; }

                side.Meshes.Clear();
                Discard(side.Material);
                side.Material = null;
                built.Sides[slot] = null;
            }

            // The tile groups: meshes and materials are the entry's; the tile TEXTURES are the TileStore's and are
            // not touched here (destroying a material never destroys its texture).
            foreach (var atlas in built.Atlas)
            {
                if (atlas == null) continue;

                for (var i = 0; i < atlas.Meshes.Count; i++) { Discard(atlas.Meshes[i]); count++; }

                atlas.Meshes.Clear();
                Discard(atlas.Material);
                atlas.Material = null;
            }

            built.Atlas.Clear();
            built.AtlasByTile.Clear();

            built.Ground.Clear();
            built.Buildings.Clear();
            built.Complete = false;

            return count;
        }

        /// <summary>Destroys an entry's walls - their meshes and their materials - and empties the list.
        /// Also the clean-up for a wall build that threw halfway, which is why it is its own method.</summary>
        /// <param name="built">The entry.</param>
        /// <returns>How many meshes were destroyed.</returns>
        private static int DestroyWalls(Built built)
        {
            var count = 0;

            foreach (var tint in built.Walls)
            {
                if (tint == null) continue;

                for (var i = 0; i < tint.Meshes.Count; i++)
                {
                    Discard(tint.Meshes[i]);
                    count++;
                }

                tint.Meshes.Clear();
                Discard(tint.Material);
                tint.Material = null;
            }

            built.Walls.Clear();
            built.Tints = 0;

            return count;
        }

        /// <summary>A view has stopped drawing an entry: the last one out destroys it if a drop has
        /// already taken it out of the cache.</summary>
        /// <param name="built">The entry.</param>
        private static void Unuse(Built built)
        {
            if (built == null) return;

            if (built.Users > 0) built.Users--;

            if (built.Users == 0 && built.Orphaned) DestroyBuilt(built);
        }

        /// <summary>
        /// A layer 8..31 that nothing in the loaded scene draws on and no live camera would show.
        ///
        /// INACTIVE renderers count. EFT hides distant geometry by switching renderers and whole
        /// GameObjects off - the reason MapCapture.ForceCulling exists - so "no ACTIVE renderer on this
        /// layer" is a statement about where the player is standing, and a layer with ten thousand
        /// disabled renderers on it would show a piece of the hideout the moment one came back. The
        /// camera-mask test is the second condition, not the first.
        ///
        /// Layers 0-7 are Unity's own and are never taken. Phase 3-0 found 19 candidates in the menu, so
        /// this failing is not the expected case - but it is possible, and then nothing is drawn at all.
        /// </summary>
        private static int PrivateLayer()
        {
            if (_sessionLayer >= 0) return _sessionLayer;

            // Watching for scene loads from the first scan on, so the result is never older than the
            // scene it describes.
            HookScenes();

            _sessionLayer = ChoosePrivateLayer();

            return _sessionLayer;
        }

        /// <summary>The scan itself. See <see cref="PrivateLayer"/>, which is what callers use.</summary>
        private static int ChoosePrivateLayer()
        {
            var used = 0;

            var renderers = FindObjectsOfType<Renderer>(true);

            for (var i = 0; i < (renderers?.Length ?? 0); i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                var layer = renderer.gameObject.layer;
                if (layer >= 0 && layer < 32) used |= 1 << layer;
            }

            var shown = 0;
            var cameras = Camera.allCameras;

            for (var i = 0; i < (cameras?.Length ?? 0); i++)
            {
                if (cameras[i] != null) shown |= cameras[i].cullingMask;
            }

            for (var layer = 8; layer < 32; layer++)
            {
                var bit = 1 << layer;
                if ((used & bit) == 0 && (shown & bit) == 0) return layer;
            }

            return -1;
        }

        // --- the render texture --------------------------------------------------------------------

        /// <summary>Makes the render texture the viewport's size in REAL pixels, or remakes it when the
        /// panel has been resized. The rect is in canvas units and the canvas may be scaled, so a
        /// texture sized from the rect alone is soft on a scaled-up UI and wasteful on a scaled-down
        /// one.</summary>
        private void EnsureRenderTexture()
        {
            var scale = CanvasScale();

            var width = Mathf.Clamp(Mathf.RoundToInt(_viewport.rect.width * scale), 64, 4096);
            var height = Mathf.Clamp(Mathf.RoundToInt(_viewport.rect.height * scale), 64, 4096);

            if (_rt != null && width == _rtWidth && height == _rtHeight) return;

            var previous = _rt;

            _rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "QuestTreeMap3D" };
            _rt.Create();

            _rtWidth = width;
            _rtHeight = height;

            if (_camera != null) _camera.targetTexture = _rt;
            if (_image != null) _image.texture = _rt;

            if (previous == null) return;

            // The old one goes only after the new one is in place on both the camera and the image: a
            // released texture still assigned to either is a frame rendered into nothing, or a UI quad
            // sampling freed memory.
            previous.Release();
            Destroy(previous);
        }

        // --- building the meshes -------------------------------------------------------------------

        /// <summary>
        /// Starts turning the parsed file into meshes, or refuses it. Runs on the main thread in the first
        /// LateUpdate after the read lands (and again for a rebuild that drops a failed side).
        ///
        /// ONLY THE CHEAP HALF runs here: the checks, the shader, the cache key, one Floor (and one registered
        /// cache entry) per drawn band. The geometry of every band not already cached is prepared on a worker
        /// (<see cref="PrepareFloor"/> - plain arrays, no Unity object) and uploaded afterwards one mesh per
        /// unit under a per-frame budget (<see cref="Pump"/>), so a map of three million building triangles
        /// never freezes the panel for longer than a frame's budget. Until the last unit is done the view
        /// draws nothing: the RawImage keeps the backdrop it was cleared to, and the overlays stay parked.
        /// </summary>
        private void BeginBuild()
        {
            _built = true;
            ResetPipeline();

            _buildClock.Reset();
            _buildClock.Start();

            // From the worker the first time; from the kept result on a rebuild (a side dropped).
            var loaded = _loading != null ? _loading.Result : _loaded;
            _loading = null;
            _loaded = loaded;

            _file = loaded?.File;

            if (_file == null || _file.Bands == null || _file.Bands.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has no ground in it - drawing the flat " +
                    $"picture instead.");
                Refuse("the file has no ground in it");
                return;
            }

            if (!ExtentAgrees(out var complaint))
            {
                // The check the plan puts here and nowhere else: a mesh quantised over one rectangle and
                // drawn under a picture stretched over another is a map whose landmarks are all in
                // plausible-looking wrong places, and nothing about it reads as broken.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' {complaint} - it is not of the same " +
                    $"rectangle as the picture, so this map draws flat.");
                Refuse("its extent is not the picture's");
                return;
            }

            _levels = DrawnLevels();

            var shader = ResolveShader(out var shaderName);

            if (shader == null)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: none of the {Shaders.Length} shaders the 3D map can draw with is loaded " +
                    $"in this scene - '{_mapKey}' draws flat.");
                Refuse("no shader this scene has loaded can draw it");
                return;
            }

            // Only Hidden/Internal-Colored has no texture of its own, and a view on it draws vertex
            // colours or nothing at all.
            var flat = shaderName == "Hidden/Internal-Colored";

            // Kept for the walls, which can be built on a later frame than the rest - see StartWalls.
            _buildingShader = shader;
            _flatColours = flat;
            _shaderName = shaderName;

            // Which heights are which floor's surfaces - the roof routing reads it while the floors build.
            MeasureFloorRanges();

            // Now, and not at Attach: whether the sides take part depends on the shader just resolved.
            TakeSideRoom();

            // The built meshes belong to a (file, write time), and anything cached under another one is
            // the geometry of a map that has been replaced. The sides are in the key because they decide
            // which faces are textured and which are tinted: the same mesh classified against four sides
            // and against none is two different builds. HERE and not earlier, because whether the sides
            // take part at all depends on the shader just resolved (SidesActive reads _flatColours).
            var key = _meshPath + "|" + loaded.Stamp.ToString(CultureInfo.InvariantCulture) + "|" +
                      (SidesActive ? _sidesKey : "-") + "|atlas:" + (AtlasActive ? _pagesKey : "-") + "|" +
                      FloorRangesKey();

            if (_builtKey != key)
            {
                DropBuiltMeshes();
                _builtKey = key;
            }

            _viewBuildKey = key;

            _groundShader = ResolveGroundShader(shader, shaderName, out _groundCutout, out _cutoutNote);

            // One Floor per drawn band, each with a cache entry - reused when complete, registered empty
            // when not, and then filled by the worker's data.
            var toPrepare = new List<(int Level, Built Into)>();

            for (var i = 0; i < _levels.Count; i++) RegisterFloor(_levels[i], shader, toPrepare);

            _groundBand = _file.Band(_levels[_levels.Count - 1]);
            _groundFallbackY = FallbackGroundY();
            var cut = CutHeight();
            if (!SameCut(cut, _cutY)) _timeRender = true;
            _cutY = cut;

            // Fitted ONCE per view: RebuildWithoutFailedSides comes back through here, and refitting then snapped
            // the player's zoom back and Finish saved the snapped distance (review F26).
            if (!_restored)
            {
                _distance = FitDistance();
                _restored = true;
            }

            Place();

            // The side pictures first, then the floors lowest to highest: the floor SHOWING ends up the most
            // recently used of everything this view holds. Asked now, so they decode while the geometry is
            // being prepared rather than after.
            if (SidesActive)
            {
                for (var slot = 0; slot < _sides.Length; slot++) _sides[slot]?.Picture?.TryGetSprite(out _);
            }

            // The atlas: the tiles of this file, cut from its pages a few a frame by the shared TileStore (see
            // PumpTiles). Taken before the prep snapshot, which reads the tile index.
            if (AtlasActive) AcquireTiles();

            foreach (var floor in _floors)
                if (!_flatColours) floor.Layer?.TryGetSprite(out _);

            _preparing = toPrepare;

            if (toPrepare.Count == 0)
            {
                // Everything came out of the cache: nothing to prepare, straight to the walls and the cut.
                AfterPrepare(new List<FloorData>());
                return;
            }

            // One worker job per (build, level), SHARED: a repaint during a build makes a new view with the same
            // key, and it attaches to the job already running instead of starting another - so clicking quests
            // while a big map builds does not stack up workers (each one is the whole floor's geometry in
            // memory). A level with no job, or only a cancelled one, gets a new job with its own snapshot.
            foreach (var item in toPrepare)
                _held.Add((item.Level, AcquirePrep(PrepKey(item.Level), item.Level, SnapshotPrep())));
        }

        /// <summary>
        /// One floor of the peel: its two materials, and its cache entry - taken from the cache when a
        /// previous view of this same file and floor completed it, else registered EMPTY here and listed in
        /// <paramref name="toPrepare"/> for the worker. Registered before it is built, as ever, so a view that
        /// goes away mid-build leaves every mesh it made where a drop will find it.
        /// </summary>
        private void RegisterFloor(int level, Shader shader, List<(int Level, Built Into)> toPrepare)
        {
            var band = _file.Band(level);
            if (band == null) return;

            if (!_cachedFloors.TryGetValue(level, out var meshes) || meshes == null || !meshes.Complete)
            {
                if (meshes != null)
                {
                    // A half-built entry: destroyed now when nobody holds it; ORPHANED when a view still
                    // does (one whose build was abandoned in the frame this view was made), so that view's
                    // release destroys it. Replacing it in the cache without either would lose it for good -
                    // a view-held entry that is no longer in the cache is found by nothing.
                    if (meshes.Users == 0) DestroyBuilt(meshes);
                    else meshes.Orphaned = true;
                }

                meshes = new Built { Cells = band.CellCount };
                _cachedFloors[level] = meshes;

                toPrepare.Add((level, meshes));
                _reusedFloors = false;
            }

            // Counted as in use from here until this view releases it - see Built.
            meshes.Users++;

            _floors.Add(new Floor
            {
                Level = level,
                Layer = LayerOf(level),
                GroundMaterial = MakeGroundMaterial(level),
                BuildingMaterial = Matte(new Material(shader) { name = $"QuestTreeMap3D-buildings-{level}" }),
                Meshes = meshes
            });
        }

        /// <summary>
        /// The worker's result has landed: the counts go onto their entries, the checks that need them are
        /// made, and every mesh is queued as one unit of upload work. Then the walls of every floor whose
        /// picture is already here are started, and the pump takes it from there.
        /// </summary>
        private void AfterPrepare(List<FloorData> prepared)
        {
            _prepDone = true;

            foreach (var data in prepared)
            {
                var into = IntoFor(data.Level);
                if (into == null) continue;

                into.Cells = data.Cells;
                into.GroundTriangles = data.GroundTriangles;
                into.BuildingTriangles = data.BuildingTriangles;
                into.BuildingCount = data.BuildingCount;
                into.Dropped = data.Dropped;
                into.TopTriangles = data.TopTriangles;
                into.SideTriangles = data.SideTriangles;
                into.WallTriangles = data.WallTriangles;
                into.MovedRoofTriangles = data.MovedRoofTriangles;
                into.GroundSkirtTriangles = data.GroundSkirtTriangles;
                into.AtlasTriangles = data.AtlasTriangles;
                into.WallsPending = data.WallTriangles > 0;
            }

            // The check that can fail, and the one an empty or garbage file gets caught by: a view with
            // no triangles in it is a viewport with the markers of a map floating over a flat colour,
            // which looks like a bug in the markers rather than in the mesh. On the COUNTS, before a single
            // mesh is uploaded, so a file that would show nothing costs nothing to refuse.
            var drawable = 0L;
            var pictured = 0;

            foreach (var floor in _floors)
            {
                // Only a floor that CAN be drawn: Draw skips any floor whose material has no picture on
                // it (a textured shader samples white without one), so a band with no picture layer is
                // never drawn at all, and its triangles counting here would pass this check for a view
                // that shows nothing. Flat colours need no picture, so there every floor counts.
                var canDraw = _flatColours || (floor.Layer != null && floor.Layer.HasArtwork);
                if (!canDraw) continue;

                pictured++;
                drawable += floor.Meshes.GroundTriangles + floor.Meshes.BuildingTriangles;
            }

            if (pictured == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has {_levels.Count} band(s) but no picture for " +
                    $"any of them - drawing the flat picture instead.");
                Refuse("no band of it has a picture");
                return;
            }

            if (drawable == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has {_levels.Count} band(s) but not one " +
                    $"triangle in them - drawing the flat picture instead.");
                Refuse("there is no ground in the bands it has");
                return;
            }

            // One unit per mesh. The side materials are made with the first mesh of their side, on this
            // thread, where a Material can be made at all.
            foreach (var data in prepared)
            {
                var into = IntoFor(data.Level);
                if (into == null) continue;

                var level = data.Level;

                // Every mesh through MakeMesh: nothing keeps a CPU copy of any of them. The dollhouse cut is the
                // camera's oblique near plane (ApplyCut), which clips on the GPU and needs no arrays to clip from.
                foreach (var mesh in data.Ground) _work.Enqueue(() => into.Ground.Add(MakeMesh(mesh)));
                foreach (var mesh in data.Roofs) _work.Enqueue(() => into.Buildings.Add(MakeMesh(mesh)));

                foreach (var roof in data.RoofsElsewhere)
                {
                    var item = roof;
                    _work.Enqueue(() => into.RoofsOnOtherFloors.Add((item.Level, MakeMesh(item.Data))));
                }

                for (var slot = 0; slot < data.Sides.Length; slot++)
                {
                    if (data.Sides[slot] == null) continue;

                    var s = slot;

                    foreach (var mesh in data.Sides[slot])
                    {
                        _work.Enqueue(() =>
                        {
                            if (into.Sides[s] == null)
                            {
                                into.Sides[s] = new SideTexture
                                {
                                    Material = Matte(new Material(_buildingShader)
                                        { name = $"QuestTreeMap3D-side{SideOrder[s]}-{level}" })
                                };
                            }

                            into.Sides[s].Meshes.Add(MakeMesh(mesh));
                        });
                    }
                }

                foreach (var pair in data.Atlas)
                {
                    var tile = pair.Key;

                    foreach (var mesh in pair.Value)
                    {
                        _work.Enqueue(() =>
                        {
                            // One Standard, matte, opaque material per (tile, floor); its _MainTex is the tile, assigned
                            // by Draw from this view's TileStore once the tile is cut.
                            if (!into.AtlasByTile.TryGetValue(tile, out var group))
                            {
                                group = new SideTexture
                                {
                                    Tile = tile,
                                    Material = Matte(new Material(_buildingShader)
                                        { name = $"QuestTreeMap3D-tile{tile}-{level}" })
                                };

                                into.AtlasByTile[tile] = group;
                                into.Atlas.Add(group);
                            }

                            group.Meshes.Add(MakeMesh(mesh));
                        });
                    }
                }

                // Complete only once every mesh of it is in: a view that goes away before this unit leaves
                // an incomplete entry, which the next view throws away and builds again.
                _work.Enqueue(() => into.Complete = true);
            }

            // The walls' colours come from each floor's picture. The floor SHOWING always has its picture by
            // now (the 3D branch only runs once the flat path has it), so its walls start with the rest; a
            // peeled lower floor may still be decoding, and its walls are started by the first frame that
            // has the picture (Draw). An entry reused from the cache with its walls still waiting gets the
            // same chance here.
            _work.Enqueue(StartInitialWalls);
        }

        /// <summary>The cache entry this build prepared a level into.</summary>
        private Built IntoFor(int level)
        {
            if (_preparing == null) return null;

            foreach (var item in _preparing)
                if (item.Level == level) return item.Into;

            return null;
        }

        /// <summary>Starts the walls of every floor whose picture is here - the "initial" walls, which the
        /// build line waits for, so its tint count is the map's and not whatever finished first.</summary>
        private void StartInitialWalls()
        {
            foreach (var floor in _floors)
            {
                var built = floor.Meshes;
                if (built == null || !built.WallsPending || built.WallsRunning) continue;

                Texture picture = null;

                if (!_flatColours && floor.Layer != null && floor.Layer.TryGetSprite(out var sprite) && sprite != null)
                    picture = sprite.texture;

                StartWalls(built, floor.Level, picture, late: false);
            }
        }

        /// <summary>
        /// Advances the build by at most a frame's budget: runs queued units until the budget is spent (one
        /// at least), collects wall workers that have finished, and - once nothing of the first build is
        /// left - queues the cut, then finishes. Called every frame while anything is outstanding.
        /// </summary>
        private void Pump()
        {
            var frame = Stopwatch.StartNew();

            PollWallJobs();

            while (_work.Count > 0 && !_broke)
            {
                var unit = _work.Dequeue();

                try
                {
                    unit();
                }
                catch (Exception ex)
                {
                    // A unit of the first build that throws leaves a map half made - the flat picture is the
                    // better answer. (Wall units catch their own; see EnqueueWallUpload.)
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                        $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");
                    Refuse("could not be turned into meshes");
                    return;
                }

                if (frame.ElapsedMilliseconds >= (_ready ? BackgroundBudgetMs : FrameBudgetMs)) break;
            }

            if (!_ready && _prepDone && _work.Count == 0 && !InitialWallsRunning()) Finish();

            if (!_ready)
            {
                _buildFrames++;
                _longestFrameMs = Math.Max(_longestFrameMs, frame.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Main-thread work per frame while a build is uploading, in milliseconds. One unit is at most one
        /// mesh of <see cref="MaxVerticesPerMesh"/> vertices uploaded, which
        /// measures tens of milliseconds - so a frame is at most this plus one unit, inside the 200 ms the
        /// panel may stall for.
        /// </summary>
        private const long FrameBudgetMs = 100;

        /// <summary>The same budget once the map is on screen, for work that arrives later (a lower floor's
        /// walls): one frame's worth, so the view stays interactive while it finishes. Still one unit at least.</summary>
        private const long BackgroundBudgetMs = 16;

        /// <summary>The first build is done: log what it came to, announce the view, and draw from the next
        /// frame on.</summary>
        private void Finish()
        {
            _ready = true;
            _measureFirstFrame = true;
            _timeRender = true;
            _buildClock.Stop();

            // The jobs this build used are no longer needed by it. See the collection in LateUpdate.
            foreach (var held in _held) ReleasePrep(held.Prep);
            _held.Clear();

            var cells = 0L;
            var groundTriangles = 0L;
            var buildings = 0;
            var buildingTriangles = 0L;
            var dropped = 0;
            var wallTriangles = 0L;
            var tints = 0;
            var wallsWaiting = 0;
            var topTriangles = 0L;
            var sideTriangles = 0L;
            var movedRoofs = 0L;
            var skirts = 0L;
            var atlasTriangles = 0L;

            GroundAboveCut(out var groundAbove, out var groundMeasured);

            foreach (var floor in _floors)
            {
                atlasTriangles += floor.Meshes.AtlasTriangles;
                topTriangles += floor.Meshes.TopTriangles;
                sideTriangles += floor.Meshes.SideTriangles;
                movedRoofs += floor.Meshes.MovedRoofTriangles;
                skirts += floor.Meshes.GroundSkirtTriangles;
                cells += floor.Meshes.Cells;
                groundTriangles += floor.Meshes.GroundTriangles;
                buildings += floor.Meshes.BuildingCount;
                buildingTriangles += floor.Meshes.BuildingTriangles;
                dropped += floor.Meshes.Dropped;
                wallTriangles += floor.Meshes.WallTriangles;
                tints += floor.Meshes.Tints;
                if (floor.Meshes.WallsPending) wallsWaiting++;
            }

            // What the walls came to. "in N tints" is summed over the floors whose walls are built; a floor
            // still waiting for its picture is counted separately, and logs its own line when they are.
            var wallNote = wallTriangles == 0
                ? "no walls"
                : _flatColours
                    ? "walls in flat colour"
                    : string.Format(CultureInfo.InvariantCulture, "walls in {0} tints", tints) +
                      (wallsWaiting > 0
                          ? string.Format(CultureInfo.InvariantCulture,
                              ", {0} floor(s) of walls waiting for a picture", wallsWaiting)
                          : "");

            // Which pictures the building faces went to: the Stage U split. Over every building triangle
            // that was kept (top + sides + tint); percentages rounded, so they may sum to 99 or 101.
            var faces = atlasTriangles + topTriangles + sideTriangles + wallTriangles;
            var sidesNote =
                (AtlasActive
                    ? string.Format(CultureInfo.InvariantCulture, ", atlas {0} page(s) {1} tile(s)", _pageCount,
                        _heldTiles?.Tiles.Count ?? 0)
                    : "") +
                (SidesActive
                    ? string.Format(CultureInfo.InvariantCulture, ", sides {0} ({1})",
                        _sideCount, string.Join(",", _sidesKey.ToCharArray()))
                    : ", sides 0") +
                (faces > 0
                    ? (AtlasActive
                        ? string.Format(CultureInfo.InvariantCulture, ", faces atlas {0:0} % / top {1:0} % / sides {2:0} % / tint {3:0} %",
                            100d * atlasTriangles / faces, 100d * topTriangles / faces, 100d * sideTriangles / faces,
                            100d * wallTriangles / faces)
                        : string.Format(CultureInfo.InvariantCulture, ", faces top {0:0} % / sides {1:0} % / tint {2:0} %",
                            100d * topTriangles / faces, 100d * sideTriangles / faces, 100d * wallTriangles / faces))
                    : "") +
                (movedRoofs > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", {0:#,##0} top face(s) on the picture of the floor they stand on", movedRoofs)
                    : "") +
                (skirts > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", {0:#,##0} ground-skirt face(s) left to the relief", skirts)
                    : "");

            Plugin.LogSource?.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "QuestTree: 3D map for {0} - {1} band(s) {2:#,##0} cells -> {3:#,##0} triangles, " +
                "{4:#,##0} buildings {5:#,##0} triangles ({11}), built in {6:#,##0} ms over {14} frame(s) " +
                "(longest {15:#,##0} ms), meshes ~{16:#,##0} MB, textures resident ~{17:#,##0} MB, layer {7}, shader {8}, " +
                "ground cutout: {9}{10}{12}{13}, path {18}.",
                _mapKey, _levels.Count, cells, groundTriangles, buildings, buildingTriangles,
                _buildClock.ElapsedMilliseconds, _drawLayer,
                _flatColours ? _shaderName + " (flat colours, no picture)" : _shaderName,
                _cutoutNote + (_reusedFloors ? ", meshes reused" : ""),
                dropped > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", dropped {0:#,##0} building triangle(s) with a vertex that is not a number", dropped)
                    : "",
                wallNote,
                sidesNote,
                float.IsNaN(_cutY) || CutMode != CutByNearPlane
                    ? ""
                    : string.Format(CultureInfo.InvariantCulture,
                        ", cut at {0:0.0} m (level {1}) by the camera's near plane, ground above the cut: {2:#,##0} of {3:#,##0} cells",
                        _cutY, _selectedLevel, groundAbove, groundMeasured),
                _buildFrames,
                _longestFrameMs,
                ResidentMeshBytes() / (1024d * 1024d),

                // Every decoded picture in the shared cache (floors, sides), at 4 B a pixel plus a third for a mip
                // chain, and every atlas tile cut so far (DXT1: half a byte a pixel plus a third). Tiles still
                // waiting their paced cut are not in it yet; the TileStore logs its own total when it finishes.
                (DynamicMapsLibrary.ResidentRasterBytes + TileStore.ResidentBytesAll) / (1024d * 1024d),
                RenderingPathOf(_camera)));

            // A floor switch whose entries were all cached: nothing was uploaded, and the cut is one matrix.
            if (_reusedFloors)
            {
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map floor switch for {0} to level {1} - ready in {2:#,##0} ms over {3} frame(s), " +
                    "0 meshes uploaded, 0 clipped, cut {4}.",
                    _mapKey, _selectedLevel, _buildClock.ElapsedMilliseconds, _buildFrames, CutText()));
            }

            // ANNOUNCED, not just placed. The labels were culled when the viewport was built - against the
            // camera as it stood before the mesh landed, with the ground at the fallback height - and
            // Place() moved the camera without telling anyone. Without this the cull and the pin names
            // kept that placeholder's decisions until the player's first drag.
            Moved();
        }

        /// <summary>Forgets everything of a build in progress: queued units, a worker's pending result, wall
        /// jobs. A wall job abandoned mid-flight leaves its entry's walls to be built again by whichever view
        /// draws it next - never half made (see <see cref="AbandonWalls"/>).</summary>
        private void ResetPipeline()
        {
            _work.Clear();

            // Let go of every job this build waited on. Not cancelled here: another view of the same build may
            // attach within the grace - see SharedPrep.
            foreach (var held in _held) ReleasePrep(held.Prep);
            _held.Clear();

            _preparing = null;
            _prepDone = false;
            _ready = false;
            _measureFirstFrame = false;
            _buildFrames = 0;
            _longestFrameMs = 0;
            _reusedFloors = true;

            AbandonWalls();
        }

        /// <summary>The shared jobs this build is waiting on, one per level it prepares. See
        /// <see cref="SharedPrep"/>.</summary>
        private readonly List<(int Level, SharedPrep Prep)> _held = new List<(int Level, SharedPrep Prep)>();

        /// <summary>
        /// One floor's preparation, shared by every view of the same build that needs it. A view HOLDS it while
        /// it waits and lets go once it has the result (or goes away); a job nobody holds is cancelled after
        /// <see cref="PrepIdleGraceMs"/> - a grace, not at once, because a repaint destroys the old view at the
        /// end of its frame and the new view attaches a frame or so later, and cancelling in between would
        /// throw away exactly the work the new view wants. The registry is touched by the main thread and by
        /// the grace timers, so every access is under <see cref="PrepLock"/>.
        /// </summary>
        private sealed class SharedPrep
        {
            public string Key = "";
            public Task<FloorData> Task;
            public CancellationTokenSource Cancel;
            public int Holders;
            public int IdleStamp;
        }

        private static readonly object PrepLock = new object();
        private static readonly Dictionary<string, SharedPrep> _preps = new Dictionary<string, SharedPrep>();

        /// <summary>A job's identity: the build's cache key (file, write time, sides, floor ranges), the level,
        /// and whether the flat-colour shader is in use - the one input the cache key leaves out that changes
        /// the arrays (vertex colours). Two views with the same key would prepare the same floor to the byte.</summary>
        private string PrepKey(int level) =>
            _builtKey + "|" + level.ToString(CultureInfo.InvariantCulture) + (_flatColours ? "|flat" : "");

        /// <summary>How long a job nobody holds is kept for a view that may still attach, in milliseconds.</summary>
        private const int PrepIdleGraceMs = 2000;

        /// <summary>The running (or finished, not yet collected) job for this key - attached to - or a new one.</summary>
        private static SharedPrep AcquirePrep(string key, int level, Prep prep)
        {
            lock (PrepLock)
            {
                if (_preps.TryGetValue(key, out var existing) && !existing.Cancel.IsCancellationRequested &&
                    !existing.Task.IsCanceled && !existing.Task.IsFaulted)
                {
                    existing.Holders++;
                    existing.IdleStamp++;
                    return existing;
                }

                var cancel = new CancellationTokenSource();
                var token = cancel.Token;

                var shared = new SharedPrep
                {
                    Key = key,
                    Cancel = cancel,
                    Holders = 1,
                    Task = System.Threading.Tasks.Task.Run(() => PrepareFloor(prep, level, token), token)
                };

                _preps[key] = shared;
                return shared;
            }
        }

        /// <summary>A view lets go of a job. The last one out starts the grace; if nobody has attached when it
        /// runs out, the job is cancelled and forgotten, and its output - if it had any - is garbage.</summary>
        private static void ReleasePrep(SharedPrep shared)
        {
            int stamp;

            lock (PrepLock)
            {
                if (shared.Holders > 0) shared.Holders--;
                if (shared.Holders > 0) return;

                stamp = ++shared.IdleStamp;
            }

            System.Threading.Tasks.Task.Delay(PrepIdleGraceMs).ContinueWith(_ =>
            {
                lock (PrepLock)
                {
                    // Attached to again (the stamp moved), or held again: nothing to do.
                    if (shared.Holders > 0 || shared.IdleStamp != stamp) return;

                    shared.Cancel.Cancel();

                    if (_preps.TryGetValue(shared.Key, out var current) && ReferenceEquals(current, shared))
                        _preps.Remove(shared.Key);
                }
            });
        }

        /// <summary>Cancels every job and forgets them all - for <see cref="DropCaches"/>, where the map memory
        /// goes: a worker that finished after it would hand its floor to nobody.</summary>
        private static void CancelAllPreps()
        {
            lock (PrepLock)
            {
                foreach (var shared in _preps.Values) shared.Cancel.Cancel();
                _preps.Clear();
            }
        }

        /// <summary>Whether a finished job was cancelled rather than failed - its level is simply asked for again.</summary>
        private static bool WasCancelled(Task task) =>
            task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException;

        /// <summary>The levels this build is preparing, and the entries they go into.</summary>
        private List<(int Level, Built Into)> _preparing;

        /// <summary>The drawn band levels of this build, lowest first.</summary>
        private List<int> _levels = new List<int>();

        /// <summary>Units of main-thread work - a mesh uploaded, a wall build landed - run by <see cref="Pump"/>.</summary>
        private readonly Queue<Action> _work = new Queue<Action>();

        private readonly Stopwatch _buildClock = new Stopwatch();
        private int _buildFrames;
        private long _longestFrameMs;
        private bool _prepDone;

        /// <summary>The first build is finished and the view draws. Until then the backdrop shows.</summary>
        private bool _ready;

        /// <summary>The next drawn frame is the first: time it and say so, once.</summary>
        private bool _measureFirstFrame;

        /// <summary>Draw calls submitted this frame - see <see cref="Submit"/>.</summary>
        private int _drawCalls;

        private string _shaderName = "";
        private string _cutoutNote = "";

        private bool _restored;

        /// <summary>The cache key this view's current build was begun under. See the cancelled branch of LateUpdate.</summary>
        private string _viewBuildKey;

        /// <summary>The height buildings are cut at, or NaN for no cut. See <see cref="CutHeight"/>.</summary>
        private float _cutY = float.NaN;

        /// <summary>Every building mesh of an entry - roofs, roofs on other floors, wall tints, sides, atlas
        /// faces. Not the ground. For <see cref="ResidentMeshBytes"/>.</summary>
        private static IEnumerable<Mesh> BuildingMeshesOf(Built built)
        {
            foreach (var mesh in built.Buildings) yield return mesh;
            foreach (var roof in built.RoofsOnOtherFloors) yield return roof.Mesh;

            foreach (var tint in built.Walls)
                foreach (var mesh in tint.Meshes)
                    yield return mesh;

            foreach (var side in built.Sides)
            {
                if (side == null) continue;
                foreach (var mesh in side.Meshes) yield return mesh;
            }

            foreach (var atlas in built.Atlas)
            {
                if (atlas == null) continue;
                foreach (var mesh in atlas.Meshes) yield return mesh;
            }
        }

        /// <summary>
        /// Roughly what a mesh of ours costs in memory, in bytes: position, normal and UV per vertex (and a
        /// colour under the flat shader), four bytes per index, times the copies held. Every mesh is held ONCE,
        /// on the GPU: each is uploaded non-readable and nothing keeps its worker arrays, since the cut is the
        /// camera's near plane and clips nothing on the CPU. An estimate for the log line, not an accounting.
        /// </summary>
        private static long MeshBytes(Mesh mesh, int copies)
        {
            if (mesh == null) return 0L;

            var stride = 12 + 12 + 8 + (mesh.HasVertexAttribute(VertexAttribute.Color) ? 4 : 0);
            var indices = 0L;

            for (var sub = 0; sub < mesh.subMeshCount; sub++) indices += (long)mesh.GetIndexCount(sub);

            return ((long)mesh.vertexCount * stride + indices * 4L) * copies;
        }

        /// <summary>What this view's geometry holds resident: every mesh of every entry it draws, GPU only - no
        /// CPU copy is kept of any mesh. For the build line.</summary>
        private long ResidentMeshBytes()
        {
            var total = 0L;
            var seen = new HashSet<Built>();

            foreach (var floor in _floors)
            {
                var built = floor?.Meshes;
                if (built == null || !seen.Add(built)) continue;

                foreach (var mesh in built.Ground) total += MeshBytes(mesh, 1);
                foreach (var mesh in BuildingMeshesOf(built)) total += MeshBytes(mesh, 1);
            }

            return total;
        }

        /// <summary>Two cut heights are the same cut (NaN is "no cut", equal to itself here).</summary>
        private static bool SameCut(float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : a == b;

        /// <summary>How far above the chosen floor's top the cut is made, in metres: enough to keep that
        /// floor's own ceiling slab and furniture-height geometry out of the cut, not so much that the
        /// floor above starts to show.</summary>
        private const float CutAboveFloor = 0.3f;

        /// <summary>
        /// Where the dollhouse cut goes: just above the top of the chosen floor, or NaN for none.
        ///
        /// Seen on Interchange: choosing the second floor drew that floor's relief as a tray lying inside
        /// the WHOLE mall, because the mall is one building filed under the ground floor (by its centroid)
        /// and stands through every storey. Peeling bands cannot fix that - the building is not in the
        /// bands above - so the buildings of every drawn band are cut at this height instead, and the
        /// floor being looked at is seen from above with the storeys over it taken off.
        ///
        /// No cut when the chosen floor is the top band: nothing is above it to take away, and the map
        /// then looks exactly as it did before the cut existed. No cut either when the floor's height band
        /// is unknown - a cut at a guessed height would slice a building somewhere meaningless.
        /// </summary>
        private float CutHeight()
        {
            if (_file?.Bands == null) return float.NaN;

            var anyAbove = false;

            foreach (var band in _file.Bands)
                if (band != null && band.Level > _selectedLevel) anyAbove = true;

            if (!anyAbove) return float.NaN;

            var layer = LayerOf(_selectedLevel);
            if (layer == null || layer.GameBounds.Count == 0) return float.NaN;

            var top = layer.GameBounds[0].Max.z;

            // The catalog files a floor with no height band as claiming every height (+-2000 m); a cut
            // there cuts nothing.
            if (float.IsNaN(top) || float.IsInfinity(top) || top >= 1000f) return float.NaN;

            return top + CutAboveFloor;
        }

        // --- the dollhouse cut: the camera's oblique near plane -----------------------------------------

        /// <summary>No cut at all: every drawn band is drawn whole. The rollback for <see cref="CutMode"/> - the
        /// CPU precut this replaced comes back only by reverting WP7's viewer commit.</summary>
        private const int CutNone = 0;

        /// <summary>The cut is the private camera's near plane, made oblique so it lies along y = cut height:
        /// the GPU clips every triangle there, exactly at the pixel. See <see cref="ApplyCut"/>.</summary>
        private const int CutByNearPlane = 1;

        /// <summary>How the dollhouse cut is made. Rollback: <see cref="CutNone"/>. Static readonly, not const, so
        /// the test in <see cref="ApplyCut"/> is not a constant the compiler would call unreachable code.</summary>
        private static readonly int CutMode = CutByNearPlane;

        /// <summary>
        /// How far above the cut the camera is kept, in metres. An oblique near plane needs the camera on the
        /// plane's negative side - above the cut - or it clips the wrong half; <see cref="Place"/> dollies out
        /// until the camera is at least this far over it. At the lowest pitch that is about (band height + 0.8)
        /// x 3.9 m from the floor's ground, which is under <see cref="MinDistance"/> for any ordinary storey.
        /// </summary>
        private const float MinCameraAboveCut = 0.5f;

        /// <summary>The private camera's rendering path. Forward: the deferred lighting pass rebuilds positions
        /// from depth with the parameters of an ordinary projection, which an oblique projection breaks (a
        /// view-dependent shading error on Standard). The light is masked to the private layer and switched on
        /// only inside the render bracket either way. Rollback: <see cref="RenderingPath.UsePlayerSettings"/>.</summary>
        private const RenderingPath PrivateCameraPath = RenderingPath.Forward;

        /// <summary>Frames drawn uncut because the camera was not above the cut - said once, in
        /// <see cref="Release"/>.</summary>
        private int _cutSkippedFrames;

        /// <summary>The uncut-frames line is said once per view.</summary>
        private bool _cutSkipLogged;

        /// <summary>
        /// The cut plane y = <paramref name="cutY"/> in the camera's space, as <see cref="Camera.CalculateObliqueMatrix"/>
        /// takes it. In world space the plane is (0, -1, 0, cutY): its normal points DOWN, so a point's value
        /// cutY - y is positive below the cut (kept) and negative above it (clipped), and the camera, above the
        /// cut, is on the negative side - which is what an oblique near plane requires (its w in camera space
        /// is cutY - y_camera, below zero). The view matrix is orthonormal, so the plane goes over as a point
        /// and a normal.
        /// </summary>
        private Vector4 CutPlaneInCameraSpace(float cutY)
        {
            var view = _camera.worldToCameraMatrix;
            var n = view.MultiplyVector(Vector3.down).normalized;
            var p = view.MultiplyPoint(new Vector3(0f, cutY, 0f));

            return new Vector4(n.x, n.y, n.z, -Vector3.Dot(p, n));
        }

        /// <summary>
        /// MAIN THREAD, inside the render bracket only. Replaces the projection's near plane with the cut plane,
        /// so everything above the cut is clipped on the GPU. <see cref="Camera.CalculateObliqueMatrix"/> changes
        /// only the projection's z row: x, y and w of every clip position - so the picture, the pins and the
        /// labels - are those of the ordinary projection. Returns whether the matrix was set; the caller resets
        /// it in its finally, so no other reader of the camera ever sees it.
        ///
        /// A camera that is not above the cut (a band taller than <see cref="Place"/> can dolly past) draws
        /// that frame uncut rather than broken, and the frame is counted.
        /// </summary>
        private bool ApplyCut()
        {
            if (CutMode != CutByNearPlane || float.IsNaN(_cutY) || _camera == null) return false;

            if (!(_camera.transform.position.y > _cutY + 0.01f))
            {
                _cutSkippedFrames++;
                return false;
            }

            // From the camera's own perspective each time, so the aspect follows the render texture
            // (EnsureRenderTexture) and the oblique matrix is never derived from the previous frame's.
            _camera.ResetProjectionMatrix();
            _camera.projectionMatrix = _camera.CalculateObliqueMatrix(CutPlaneInCameraSpace(_cutY));

            return true;
        }

        /// <summary>
        /// How much of the drawn relief rises above the cut: measured cells of the drawn bands whose height is
        /// over <see cref="_cutY"/>, of all measured cells of those bands. The GPU plane clips the ground too
        /// (the CPU cut never touched it), so terrain higher than the chosen floor's top no longer hides it -
        /// intended, and said in the build line. O(cells), once per build, main thread.
        /// </summary>
        private void GroundAboveCut(out long above, out long measured)
        {
            above = 0L;
            measured = 0L;

            if (float.IsNaN(_cutY) || _file == null) return;

            foreach (var level in _levels)
            {
                var band = _file.Band(level);
                var heights = band?.Heights;
                if (heights == null) continue;

                for (var i = 0; i < heights.Length; i++)
                {
                    var code = heights[i];
                    if (code == MapMeshFile.NoHit) continue;

                    measured++;
                    if (_file.HeightOf(code) > _cutY) above++;
                }
            }
        }

        /// <summary>The cut for the log lines: its height, or "off".</summary>
        private string CutText() =>
            float.IsNaN(_cutY) || CutMode != CutByNearPlane
                ? "off"
                : _cutY.ToString("0.0", CultureInfo.InvariantCulture) + " m";

        /// <summary>The path the private camera actually renders with, for the build line.</summary>
        private static string RenderingPathOf(Camera camera)
        {
            try { return camera != null ? camera.actualRenderingPath.ToString() : "none"; }
            catch (Exception) { return "unknown"; }
        }

        /// <summary>Whether the mesh's extent is the picture's. See <see cref="ExtentTolerance"/>.</summary>
        /// <param name="complaint">What differs, for the log line.</param>
        private bool ExtentAgrees(out string complaint)
        {
            complaint = "";

            var layer = ResolveLayer();

            // REFUSED, not waved through. The extent is the only thing that ties the geometry to the
            // picture and to the markers; with no rectangle to check against there is nothing to say the
            // mesh belongs to this map at all, and a viewer that draws it anyway is guessing.
            if (layer == null || !layer.HasBounds)
            {
                complaint = "cannot be checked - this floor declares no rectangle of its own";
                return false;
            }

            var dx = Mathf.Abs((float)_file.MinX - layer.BoundsMin.x);
            var dz = Mathf.Abs((float)_file.MinZ - layer.BoundsMin.y);
            var dX = Mathf.Abs((float)_file.MaxX - layer.BoundsMax.x);
            var dZ = Mathf.Abs((float)_file.MaxZ - layer.BoundsMax.y);

            if (dx <= ExtentTolerance && dz <= ExtentTolerance &&
                dX <= ExtentTolerance && dZ <= ExtentTolerance)
            {
                return true;
            }

            complaint = string.Format(
                CultureInfo.InvariantCulture,
                "covers ({0:0.#}, {1:0.#})..({2:0.#}, {3:0.#}) where the picture covers " +
                "({4:0.#}, {5:0.#})..({6:0.#}, {7:0.#})",
                _file.MinX, _file.MinZ, _file.MaxX, _file.MaxZ,
                layer.BoundsMin.x, layer.BoundsMin.y, layer.BoundsMax.x, layer.BoundsMax.y);

            return false;
        }

        /// <summary>
        /// The band levels to draw, lowest first: the floor peel. Every band at or below the chosen
        /// floor, so looking at the second storey of Interchange shows it standing on the first and the
        /// ground - that is what makes it read as a building rather than a floating slab.
        ///
        /// Capped at the picture cache's ceiling, keeping the HIGHEST bands. Each drawn band holds its
        /// floor's picture resident, and asking for one more than the cache holds would evict a texture
        /// this view asks for again next frame - a 39 MiB decode per frame, forever. Eight bands are
        /// possible in the format and six fit in the cache; no captured map has more than four.
        /// </summary>
        private List<int> DrawnLevels()
        {
            var levels = new List<int>();

            foreach (var band in _file.Bands)
            {
                if (band == null || band.Level > _selectedLevel) continue;
                levels.Add(band.Level);
            }

            // Nothing at or below the floor showing: the lowest band there is, rather than nothing at
            // all. That happens to a capture whose basement has a picture but no relief - the player has
            // selected level -1 and the mesh starts at 0 - and drawing the ground under them is a better
            // answer than refusing the whole file over which floor happens to be picked.
            if (levels.Count == 0)
            {
                var lowest = int.MaxValue;

                foreach (var band in _file.Bands)
                    if (band != null && band.Level < lowest) lowest = band.Level;

                if (lowest != int.MaxValue) levels.Add(lowest);

                return levels;
            }

            levels.Sort();

            // ONE LESS than the cache holds. The floor showing is also loaded by the flat path - the
            // sidebar and the kept-viewport key both read its sprite - so a peel that filled the cache
            // exactly would leave that one texture as the eviction victim, and the two sides would take
            // turns evicting each other's picture at 39 MiB a decode.
            var ceiling = Mathf.Max(1, DynamicMapsLibrary.MaxResidentSprites - 1);

            while (levels.Count > ceiling) levels.RemoveAt(0);

            return levels;
        }

        /// <summary>The first of <see cref="Shaders"/> that resolves in this scene.</summary>
        /// <param name="name">Its name, for the log line.</param>
        private static Shader ResolveShader(out string name)
        {
            for (var i = 0; i < Shaders.Length; i++)
            {
                var shader = Shader.Find(Shaders[i]);
                if (shader == null) continue;

                name = Shaders[i];
                return shader;
            }

            name = "none";
            return null;
        }

        /// <summary>Whether every floor of this view came out of the cache, for the build log line -
        /// "reused" is the difference between a click that hitches and one that does not, and it is worth
        /// being able to read that off the log rather than infer it from the milliseconds.</summary>
        private bool _reusedFloors = true;

        /// <summary>
        /// The ground's shader, and whether it will really clip on the picture's alpha.
        ///
        /// WHY THE GROUND IS A SPECIAL CASE. A captured picture is the walkable cut-out: alpha 255 inside
        /// the playable area and 0 outside it. The relief is not - the rays hit the whole rectangle, so
        /// the ground mesh covers every cell of it. Drawn with an opaque shader, the surround draws as the
        /// RGB of a fully transparent pixel, which is black: a black apron around the map where the flat
        /// view shows the panel's own backdrop through the alpha. Clipping the ground on alpha puts that
        /// back.
        ///
        /// Standard does it with a material setup rather than a different shader - the same switch its own
        /// inspector makes for Rendering Mode "Cutout", spelled out here because there is no inspector at
        /// runtime. Legacy Diffuse has a separate cutout shader instead, which may or may not be loaded in
        /// this scene; if it is not, the ground stays opaque and the log says "no", because a black apron
        /// is a cosmetic fault and a missing shader is not worth refusing a map over.
        /// </summary>
        /// <param name="opaque">The shader the buildings use - the first of <see cref="Shaders"/> that
        /// resolved.</param>
        /// <param name="opaqueName">Its name.</param>
        /// <param name="cutout">Whether the returned shader, set up by
        /// <see cref="MakeGroundMaterial"/>, clips on alpha.</param>
        /// <param name="note">What the build log line says after "ground cutout: ".</param>
        private static Shader ResolveGroundShader(
            Shader opaque, string opaqueName, out bool cutout, out string note)
        {
            if (opaqueName == "Standard")
            {
                cutout = true;
                note = "yes";
                return opaque;
            }

            if (opaqueName == "Legacy Shaders/Diffuse")
            {
                var legacy = Shader.Find(LegacyCutout);

                if (legacy != null)
                {
                    cutout = true;
                    note = "yes (" + LegacyCutout + ")";
                    return legacy;
                }
            }

            // Unlit/Texture and Hidden/Internal-Colored: no cutout variant worth reaching for - the first
            // has one only under a name the probe never saw loaded, and the second has no texture at all,
            // so there is no alpha to clip against in the first place.
            cutout = false;
            note = "no";
            return opaque;
        }

        /// <summary>The cutout shader tried for a scene where only Legacy Diffuse resolved.</summary>
        private const string LegacyCutout = "Legacy Shaders/Transparent/Cutout/Diffuse";

        private Shader _groundShader;
        private bool _groundCutout;

        /// <summary>Whether the only shader that resolved draws vertex colours and has no texture. Read by
        /// <see cref="Draw"/>, which must not ask the picture cache for a texture it cannot use: the
        /// material's mainTexture would stay null whatever was assigned, so the ask would repeat every
        /// frame and move a floor to the front of the sprite cache's queue sixty times a second.</summary>
        private bool _flatColours;

        /// <summary>
        /// One floor's ground material, alpha-clipped when <see cref="ResolveGroundShader"/> found a way
        /// to be.
        ///
        /// The Standard recipe is the whole of what its inspector's "Cutout" mode does: the render type
        /// tag, the mode value the shader's own GUI reads back, the clip threshold, opaque blending with
        /// depth written, the _ALPHATEST_ON keyword that actually compiles the clip in - and the
        /// AlphaTest queue, so the ground draws after the opaque geometry and its discarded fragments
        /// leave no depth behind. The two blend keywords are switched OFF explicitly rather than left
        /// alone: a material that has been through this path once must not carry a blend mode into a
        /// clip mode, and Unity's own setup function disables them for exactly that reason.
        /// </summary>
        /// <param name="level">The floor, for the material's name.</param>
        private Material MakeGroundMaterial(int level)
        {
            var material = Matte(new Material(_groundShader) { name = $"QuestTreeMap3D-ground-{level}" });

            if (!_groundCutout) return material;

            material.SetOverrideTag("RenderType", "TransparentCutout");

            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 1f);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", 0.5f);

            if (material.HasProperty("_SrcBlend"))
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);

            if (material.HasProperty("_DstBlend"))
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);

            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 1);

            material.EnableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            return material;
        }

        /// <summary>Matte, not plastic. Standard's defaults are a smooth dielectric, which turns a
        /// hillside into a mirror of the one light in the scene.</summary>
        /// <param name="material">The material to dull.</param>
        private static Material Matte(Material material)
        {
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0f);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);

            return material;
        }

        /// <summary>The floor ranges as text, for the cache key: the roof routing depends on them, and they
        /// come from the meta, which a rescan can change while the mesh file stays the same.</summary>
        private string FloorRangesKey()
        {
            var text = "";

            for (var i = 0; i < _floorRanges.Count; i++)
            {
                var r = _floorRanges[i];
                text += string.Format(CultureInfo.InvariantCulture, "{0}:{1:0.##}-{2:0.##};", r.Level, r.Low, r.High);
            }

            return text;
        }

        /// <summary>The height range each floor's own surfaces are found in, from the meta: [minY - slack,
        /// maxY + slack] per band the mesh file has. Set by <see cref="BeginBuild"/> before any floor is
        /// built; the same for every selection, so the routing it drives is safe to cache.</summary>
        private readonly List<(int Level, float Low, float High)> _floorRanges = new List<(int Level, float Low, float High)>();

        /// <summary>How far outside its declared height band a face may be and still be ON that floor: the
        /// half metre the harvest's bands and the relief's own floor test already allow.</summary>
        private const float FloorFaceSlack = 0.5f;

        /// <summary>Fills <see cref="_floorRanges"/> from the entry's layers, for the bands the file has. A
        /// band with the catalog's "any height" placeholder (+-2000 m) is left out - a range that claims
        /// every face would take every roof.</summary>
        private void MeasureFloorRanges()
        {
            _floorRanges.Clear();

            foreach (var band in _file.Bands)
            {
                if (band == null) continue;

                var layer = LayerOf(band.Level);
                if (layer == null || layer.GameBounds.Count == 0) continue;

                var low = layer.GameBounds[0].Min.z;
                var high = layer.GameBounds[0].Max.z;

                if (!(low > -1000f) || !(high < 1000f) || high < low) continue;

                _floorRanges.Add((band.Level, low - FloorFaceSlack, high + FloorFaceSlack));
            }
        }

        /// <summary>This view's floor with the given level, or null. At most six floors, so a loop.</summary>
        private Floor FloorAt(int level)
        {
            for (var i = 0; i < _floors.Count; i++)
                if (_floors[i] != null && _floors[i].Level == level) return _floors[i];

            return null;
        }

        // --- the walls -------------------------------------------------------------------------------

        /// <summary>A wall's colour when the picture has nothing to say: a transparent pixel under the
        /// building, a readback that failed, or no picture at all. Warm grey, which reads as rendered
        /// concrete and is the commonest wall there is.</summary>
        private static readonly Color FallbackWallColour = new Color(0.55f, 0.53f, 0.50f, 1f);

        /// <summary>The walls' vertex colour under the flat-colour shader: the buildings' own flat colour
        /// a quarter darker, so walls still read as walls against their roofs.</summary>
        private static readonly Color32 FlatWallColour = new Color32(126, 123, 117, 255);

        /// <summary>The most colours one floor's walls are drawn in. See <see cref="WallTint"/>.</summary>
        private const int MaxWallTints = 16;

        /// <summary>How wide the readable copy of a floor's picture is. The tint is an average over a few
        /// metres of roof, so 256 columns across a kilometre - 4 m to the pixel - is all the detail the
        /// question needs, and it reads back in well under a millisecond.</summary>
        private const int PaletteWidth = 256;

        /// <summary>How much of the roof's brightness a wall keeps: three quarters.</summary>
        private const float WallValue = 0.75f;

        /// <summary>How much of the roof's saturation a wall keeps.</summary>
        private const float WallSaturation = 0.85f;

        /// <summary>
        /// A small READABLE copy of a floor's picture, as pixels.
        ///
        /// The picture itself cannot be read: DynamicMapsLibrary decodes it with markNonReadable, which
        /// frees the CPU copy - the right call for a 39 MiB texture that is only ever drawn. So the GPU
        /// copies it down into a temporary render texture, and ReadPixels brings that back. Once per floor
        /// per built entry (the entry is cached, so once per session per floor in practice), and nothing
        /// of it outlives the call: the render texture goes back to the pool and the readable texture is
        /// destroyed in the finally, so there is no Unity object here for a teardown to miss.
        /// </summary>
        /// <param name="source">The floor's picture.</param>
        /// <param name="width">The copy's width, <see cref="PaletteWidth"/>.</param>
        /// <param name="height">The copy's height, keeping the picture's aspect.</param>
        private static Color32[] ReadPalette(Texture source, out int width, out int height)
        {
            width = PaletteWidth;
            height = source.width > 0
                ? Mathf.Clamp(Mathf.RoundToInt(PaletteWidth * (float)source.height / source.width), 1, 1024)
                : PaletteWidth;

            var previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D readable = null;

            try
            {
                target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, target);

                RenderTexture.active = target;

                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);

                // GetPixels32 reads the CPU copy ReadPixels just wrote - no Apply, which would only upload
                // it back to the GPU for nothing.
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) Destroy(readable);
            }
        }

        /// <summary>
        /// Groups the buildings' colours into at most <paramref name="max"/> buckets, deterministically.
        ///
        /// Each colour falls in a cell of a 4x4x4 grid over RGB; the most populous cells become the
        /// buckets, coloured by the mean of their members, and a colour in any other cell goes to the
        /// nearest bucket. Deterministic (ties by cell number), so the same file gives the same walls on
        /// every open - a k-means seeded at random would repaint a building between one click and the
        /// next.
        /// </summary>
        /// <param name="colours">One colour per walled building.</param>
        /// <param name="max">The most buckets.</param>
        /// <param name="centres">Filled with each bucket's colour.</param>
        /// <returns>The bucket of each colour, in step with <paramref name="colours"/>.</returns>
        internal static int[] BucketColours(List<Color> colours, int max, List<Color> centres)
        {
            centres.Clear();
            var bucketOf = new int[colours.Count];
            if (colours.Count == 0 || max <= 0) return bucketOf;

            var cells = new Dictionary<int, (Color Sum, int Count)>();
            var cellOf = new int[colours.Count];

            for (var i = 0; i < colours.Count; i++)
            {
                var key = Cell(colours[i]);
                cellOf[i] = key;

                cells.TryGetValue(key, out var held);
                cells[key] = (held.Sum + colours[i], held.Count + 1);
            }

            var chosen = new List<int>(cells.Keys);
            chosen.Sort((x, y) =>
            {
                var byCount = cells[y].Count.CompareTo(cells[x].Count);
                return byCount != 0 ? byCount : x.CompareTo(y);
            });

            if (chosen.Count > max) chosen.RemoveRange(max, chosen.Count - max);

            var bucketOfCell = new Dictionary<int, int>();

            for (var k = 0; k < chosen.Count; k++)
            {
                var cell = cells[chosen[k]];
                var mean = cell.Sum / cell.Count;
                mean.a = 1f;

                centres.Add(mean);
                bucketOfCell[chosen[k]] = k;
            }

            for (var i = 0; i < colours.Count; i++)
            {
                if (bucketOfCell.TryGetValue(cellOf[i], out var own))
                {
                    bucketOf[i] = own;
                    continue;
                }

                var best = 0;
                var bestDistance = float.MaxValue;

                for (var k = 0; k < centres.Count; k++)
                {
                    var d = colours[i] - centres[k];
                    var distance = d.r * d.r + d.g * d.g + d.b * d.b;
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    best = k;
                }

                bucketOf[i] = best;
            }

            return bucketOf;
        }

        /// <summary>A colour's cell in a 4x4x4 grid over RGB.</summary>
        private static int Cell(Color c)
        {
            var r = Mathf.Clamp(Mathf.FloorToInt(c.r * 4f), 0, 3);
            var g = Mathf.Clamp(Mathf.FloorToInt(c.g * 4f), 0, 3);
            var b = Mathf.Clamp(Mathf.FloorToInt(c.b * 4f), 0, 3);

            return r * 16 + g * 4 + b;
        }

        /// <summary>
        /// One wall colour's material: the building shader, matte, untextured, coloured. Null when the
        /// shader has no _Color to colour with - under the flat-colour fallback the walls carry vertex
        /// colours instead, and under Unlit/Texture there is nothing to tint - and the walls then draw with
        /// the floor's own building material.
        /// </summary>
        private Material MakeWallMaterial(int level, int bucket, Color colour) =>
            MakeTintMaterial($"QuestTreeMap3D-walls-{level}-{bucket}", colour);

        /// <summary>A matte, untextured, coloured material on the building shader, or null when that shader
        /// has no _Color (or the flat-colour fallback is in use). See <see cref="MakeWallMaterial"/>.</summary>
        private Material MakeTintMaterial(string name, Color colour)
        {
            if (_flatColours || _buildingShader == null) return null;

            // Remembered per shader: under Unlit/Texture every untextured side slot asks here EVERY frame, and a
            // null answer that was not remembered made and destroyed a Material each time (review F25).
            if (ReferenceEquals(_tintlessShader, _buildingShader)) return null;

            var material = Matte(new Material(_buildingShader) { name = name });

            if (!material.HasProperty("_Color"))
            {
                _tintlessShader = _buildingShader;
                Discard(material);
                return null;
            }

            // No texture: Standard's _MainTex then samples white, and the wall is exactly _Color, lit.
            material.mainTexture = null;
            material.color = colour;

            return material;
        }

        /// <summary>The shader the buildings are drawn with, kept for the wall materials, which may be made
        /// on a later frame than the rest (when the floor's picture arrives).</summary>
        private Shader _buildingShader;

        /// <summary>The building shader last found to have no _Color, or null. See <see cref="MakeTintMaterial"/>.</summary>
        private Shader _tintlessShader;

        // --- the atlas tiles (stage X) -----------------------------------------------------------------

        /// <summary>The tile store of the file last drawn with an atlas, or null. Main thread only; shared by every
        /// view of that file, like <see cref="_cachedFloors"/>, and dropped with it by <see cref="DropCaches"/>.</summary>
        private static TileStore _tiles;

        /// <summary>The store this view holds a use of, or null. See <see cref="AcquireTiles"/>.</summary>
        private TileStore _heldTiles;

        /// <summary>Takes a use of this file's tile store, making it on the first view of the file. Idempotent.</summary>
        private void AcquireTiles()
        {
            if (_heldTiles != null || _file == null) return;

            if (_tiles == null || !ReferenceEquals(_tiles.File, _file))
            {
                DropTiles();

                var paths = new string[MaxAtlasPages];
                for (var page = 0; page < _pages.Length; page++) paths[page] = _pages[page]?.Picture?.ImagePath;

                _tiles = TileStore.For(_file, _mapKey, paths);
            }

            _tiles.Users++;
            _heldTiles = _tiles;
        }

        /// <summary>Lets go of this view's use of its tile store; the last user of a dropped store destroys it.
        /// Idempotent.</summary>
        private void ReleaseTiles()
        {
            var tiles = _heldTiles;
            if (tiles == null) return;

            _heldTiles = null;
            tiles.Users--;

            if (tiles.Users <= 0 && tiles.Orphaned) tiles.Destroy();
        }

        /// <summary>Takes the tile store out of the cache: destroyed now when no view uses it, else orphaned for its
        /// last user to destroy - the same contract as <see cref="Built"/>.</summary>
        private static void DropTiles()
        {
            var tiles = _tiles;
            _tiles = null;

            if (tiles == null) return;

            if (tiles.Users > 0) tiles.Orphaned = true;
            else tiles.Destroy();
        }

        /// <summary>
        /// One file's atlas TILES: every (page, tile rect) its buildings' ranges name, each cut out of its page into a
        /// texture of its own - so the GPU can REPEAT it. Stage W's page could not wrap (a UV past its tile sampled
        /// the neighbour), and EFT's walls are UV'd in world units, many repeats long: on Customs 1,737 of ~1,900
        /// textured uses fell back to a flat colour. A tile of its own with wrapMode Repeat draws the material's raw
        /// UVs as they are, with the Standard shader and nothing compiled at runtime.
        ///
        /// Per page, in order, paced: the PNG read on a worker; decoded READABLE on the main thread in the frame's
        /// one decode turn (DynamicMapsLibrary.TryTakeDecodeTurn - the picture cache's own LoadImage pacing, so a
        /// page and a floor never decode in one frame); its pixels taken once (GetPixels32) and the page texture
        /// destroyed at once; then its tiles cut a few a frame (<see cref="TileBudgetMs"/>) - each an RGB24 texture
        /// with mips, compressed to DXT1 (the builder makes every side a multiple of 4), Repeat, trilinear, aniso 4,
        /// and made non-readable - and the page's pixels let go. 284 tiles of 256 px are about 12 MB this way,
        /// against about 99 MB uncompressed.
        ///
        /// The page never enters the picture cache: nothing else draws it, and holding it would be 85 MB for nothing.
        /// REFERENCE-COUNTED by views and orphaned by a drop, as <see cref="Built"/> is; a tile's texture is assigned
        /// to the entries' materials by Draw and never destroyed by them.
        /// </summary>
        internal sealed class TileStore
        {
            /// <summary>Main-thread time a frame spends cutting tiles, in milliseconds (a tile is about 1 ms).</summary>
            private const double TileBudgetMs = 4d;

            /// <summary>One tile: its rect on its page, and its texture once cut.</summary>
            internal sealed class Tile
            {
                public int Page;
                public int X;
                public int Y;
                public int W;
                public int H;
                public Texture2D Texture;
                public bool Failed;
                public long Bytes;
            }

            /// <summary>The parsed file this store belongs to - its identity in the cache.</summary>
            public MapMeshFile File;

            public readonly List<Tile> Tiles = new List<Tile>();

            /// <summary>How many live views hold this store.</summary>
            public int Users;

            /// <summary>Taken out of the cache while in use: the last user destroys it.</summary>
            public bool Orphaned;

            /// <summary>Every tile in every live store's bytes, for the build line.</summary>
            public static long ResidentBytesAll { get; private set; }

            private readonly Dictionary<long, int> _index = new Dictionary<long, int>();
            private readonly string[] _paths = new string[MaxAtlasPages];
            private readonly bool[] _pageFailed = new bool[MaxAtlasPages];
            private readonly List<int>[] _tilesOfPage = new List<int>[MaxAtlasPages];
            private readonly Dictionary<int, Color32[]> _buffers = new Dictionary<int, Color32[]>();
            private string _mapKey = "";

            private int _page = -1;
            private int _nextPage;
            private int _cursor;
            private Task<byte[]> _reading;
            private Color32[] _pixels;
            private int _pageWidth;
            private int _pageHeight;

            private int _pumpedFrame = -1;
            private bool _done;
            private bool _destroyed;
            private int _frames;
            private int _pagesCut;
            private int _cut;
            private int _failed;
            private long _bytes;
            private bool _allCompressed = true;
            private readonly Stopwatch _clock = new Stopwatch();

            /// <summary>The store for a file: every distinct tile its ranges name, indexed. Main thread; nothing is
            /// read or decoded until the first <see cref="Pump"/>.</summary>
            /// <param name="file">The parsed file.</param>
            /// <param name="mapKey">For the log lines.</param>
            /// <param name="paths">Each page's PNG by page number, null where the view has none.</param>
            public static TileStore For(MapMeshFile file, string mapKey, string[] paths)
            {
                var store = new TileStore { File = file, _mapKey = mapKey ?? "" };

                for (var page = 0; page < MaxAtlasPages && page < paths.Length; page++) store._paths[page] = paths[page];

                if (file?.Buildings != null)
                {
                    foreach (var building in file.Buildings)
                    {
                        if (building?.Ranges == null) continue;

                        foreach (var range in building.Ranges)
                        {
                            if (range.Page < 0 || range.Page >= MaxAtlasPages) continue;

                            var key = KeyOf(range.Page, range.TileX, range.TileY, range.TileW, range.TileH);
                            if (store._index.ContainsKey(key)) continue;

                            store._index[key] = store.Tiles.Count;
                            store.Tiles.Add(new Tile
                            {
                                Page = range.Page, X = range.TileX, Y = range.TileY, W = range.TileW, H = range.TileH
                            });

                            (store._tilesOfPage[range.Page] ??= new List<int>()).Add(store.Tiles.Count - 1);
                        }
                    }
                }

                // A page with tiles but no file here: those tiles can never be cut. Failed up front, so the view drops
                // the page rather than drawing its faces in the fallback colour for good.
                for (var page = 0; page < MaxAtlasPages; page++)
                    if (store._tilesOfPage[page] != null && string.IsNullOrEmpty(store._paths[page]))
                        store.FailPage(page, null);

                return store;
            }

            /// <summary>A tile's identity: page (3 bits), x and y (13 each), w and h (9 each, up to 256... 511).</summary>
            private static long KeyOf(int page, int x, int y, int w, int h) =>
                ((long)page << 44) | ((long)(x & 0x1FFF) << 31) | ((long)(y & 0x1FFF) << 18) | ((long)(w & 0x1FF) << 9) |
                (long)(h & 0x1FF);

            /// <summary>WORKER-SAFE (the index is complete before any prep starts and never changes): the tile a
            /// range draws, or -1.</summary>
            public int TileOf(MapMeshFile.AtlasRange range) =>
                _index.TryGetValue(KeyOf(range.Page, range.TileX, range.TileY, range.TileW, range.TileH), out var tile)
                    ? tile
                    : -1;

            /// <summary>The tile's texture, or null while it is not cut yet, or failed.</summary>
            public Texture2D TextureOf(int tile) =>
                tile >= 0 && tile < Tiles.Count ? Tiles[tile].Texture : null;

            /// <summary>Whether a page could not be had at all (missing, unreadable, will not decode, too big).</summary>
            public bool PageFailed(int page) => page >= 0 && page < MaxAtlasPages && _pageFailed[page];

            /// <summary>Whether the page of <paramref name="tile"/> failed.</summary>
            public bool PageFailedFor(int tile) => tile >= 0 && tile < Tiles.Count && _pageFailed[Tiles[tile].Page];

            /// <summary>MAIN THREAD, once a frame however many views call it: the next step of the paced cut.</summary>
            public void Pump()
            {
                if (_done || _destroyed || _pumpedFrame == Time.frameCount) return;

                _pumpedFrame = Time.frameCount;
                _frames++;
                if (!_clock.IsRunning) _clock.Start();

                var frame = Stopwatch.StartNew();

                while (frame.Elapsed.TotalMilliseconds < TileBudgetMs)
                {
                    if (_page < 0 && !NextPage())
                    {
                        Finish();
                        return;
                    }

                    if (_pixels == null)
                    {
                        // Read on a worker; decoded in this frame's one decode turn, and that decode is the frame.
                        if (_reading == null)
                        {
                            var path = _paths[_page];
                            _reading = Task.Run(() => System.IO.File.ReadAllBytes(path));
                            return;
                        }

                        if (!_reading.IsCompleted || !DynamicMapsLibrary.TryTakeDecodeTurn()) return;

                        var task = _reading;
                        _reading = null;

                        if (!Decode(task)) _page = -1;
                        return;
                    }

                    var tiles = _tilesOfPage[_page];

                    if (_cursor >= tiles.Count)
                    {
                        // Every tile of the page is cut: its pixels go (64 MB of managed array for a 4096 page).
                        _pixels = null;
                        _pagesCut++;
                        _page = -1;
                        continue;
                    }

                    Cut(tiles[_cursor++]);
                }
            }

            /// <summary>Moves to the next page with tiles still to cut; false when there is none.</summary>
            private bool NextPage()
            {
                while (_nextPage < MaxAtlasPages)
                {
                    var page = _nextPage++;
                    if (_tilesOfPage[page] == null || _pageFailed[page]) continue;

                    _page = page;
                    _cursor = 0;
                    return true;
                }

                return false;
            }

            /// <summary>MAIN THREAD. The page's pixels, from a READABLE decode whose texture is destroyed at once.</summary>
            private bool Decode(Task<byte[]> task)
            {
                if (task.IsFaulted || task.IsCanceled)
                {
                    FailPage(_page, $"could not be read ({task.Exception?.GetBaseException().Message})");
                    return false;
                }

                var bytes = task.Result;

                // The header before the decode, as for every picture (review F49): a page is AtlasPageSize a side.
                if (!DynamicMapsLibrary.PictureSize(bytes, out var width, out var height) ||
                    width > MapMeshFile.AtlasPageSize || height > MapMeshFile.AtlasPageSize)
                {
                    FailPage(_page, $"is not a PNG of at most {MapMeshFile.AtlasPageSize} px a side ({width}x{height})");
                    return false;
                }

                Texture2D texture = null;

                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false) { name = "QuestTreeMap3D-atlaspage" };

                    // Readable (markNonReadable false): the tiles are cut from its pixels.
                    if (!texture.LoadImage(bytes, markNonReadable: false))
                    {
                        FailPage(_page, "would not decode");
                        return false;
                    }

                    _pageWidth = texture.width;
                    _pageHeight = texture.height;
                    _pixels = texture.GetPixels32();
                    return true;
                }
                catch (Exception ex)
                {
                    _pixels = null;
                    FailPage(_page, $"could not be decoded ({ex.GetType().Name}: {ex.Message})");
                    return false;
                }
                finally
                {
                    Discard(texture);
                }
            }

            /// <summary>MAIN THREAD. One tile out of the current page's pixels into a texture of its own.</summary>
            private void Cut(int index)
            {
                var tile = Tiles[index];

                if (tile.W <= 0 || tile.H <= 0 || tile.X < 0 || tile.Y < 0 ||
                    tile.X + tile.W > _pageWidth || tile.Y + tile.H > _pageHeight)
                {
                    tile.Failed = true;
                    _failed++;
                    return;
                }

                Texture2D texture = null;

                try
                {
                    var size = tile.W * tile.H;

                    if (!_buffers.TryGetValue(size, out var buffer))
                    {
                        buffer = new Color32[size];
                        _buffers[size] = buffer;
                    }

                    // Row by row, bottom up on both sides: the page's row 0 is its bottom (LoadImage's order, and the
                    // builder's TileY is from the bottom), and so is the tile's - no flip.
                    for (var row = 0; row < tile.H; row++)
                        Array.Copy(_pixels, (tile.Y + row) * _pageWidth + tile.X, buffer, row * tile.W, tile.W);

                    texture = new Texture2D(tile.W, tile.H, TextureFormat.RGB24, mipChain: true)
                    {
                        name = $"QuestTreeMap3D-tile{index}",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4
                    };

                    texture.SetPixels32(buffer);
                    texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);

                    // DXT1 needs 4 x 4 blocks; the builder aligns every tile, and one that is not stays RGB24.
                    if (tile.W % MapMeshFile.TileAlign == 0 && tile.H % MapMeshFile.TileAlign == 0)
                        texture.Compress(highQuality: false);

                    // Uploaded and the CPU copy freed.
                    texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

                    var compressed = texture.format == TextureFormat.DXT1;
                    if (!compressed) _allCompressed = false;

                    // DXT1: half a byte a pixel; anything else counted at 4 (D3D11 has no 24-bit format). Mips + 1/3.
                    var bytes = compressed ? (long)size / 2 : (long)size * 4;
                    if (texture.mipmapCount > 1) bytes += bytes / 3;

                    tile.Texture = texture;
                    tile.Bytes = bytes;
                    _bytes += bytes;
                    ResidentBytesAll += bytes;
                    _cut++;
                    texture = null;
                }
                catch (Exception ex)
                {
                    tile.Failed = true;
                    _failed++;

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: atlas tile {index} of '{_mapKey}' could not be cut ({ex.GetType().Name}: {ex.Message}).");
                }
                finally
                {
                    Discard(texture);
                }
            }

            /// <summary>Marks a page and every tile on it failed, and says so once.</summary>
            private void FailPage(int page, string reason)
            {
                if (page < 0 || page >= MaxAtlasPages || _pageFailed[page]) return;

                _pageFailed[page] = true;

                var tiles = _tilesOfPage[page];
                if (tiles != null)
                    foreach (var index in tiles)
                        if (!Tiles[index].Failed && Tiles[index].Texture == null) { Tiles[index].Failed = true; _failed++; }

                if (reason != null)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: atlas page {page} of the 3D map for '{_mapKey}' {reason} - its faces are drawn " +
                        $"without the game's textures.");
                }
            }

            private void Finish()
            {
                _done = true;
                _clock.Stop();
                _buffers.Clear();

                Plugin.LogSource?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "QuestTree: 3D map textures for {0} - {1} tile(s) cut from {2} page(s) in {3:#,##0} ms over {4} " +
                    "frame(s), ~{5:0.0} MB {6}{7}.",
                    _mapKey, _cut, _pagesCut, _clock.ElapsedMilliseconds, _frames, _bytes / (1024d * 1024d),
                    _allCompressed ? "(DXT1)" : "(some uncompressed)",
                    _failed > 0 ? string.Format(CultureInfo.InvariantCulture, ", {0} failed", _failed) : ""));
            }

            /// <summary>MAIN THREAD. Destroys every tile's texture and forgets the work in flight. Idempotent.</summary>
            public void Destroy()
            {
                if (_destroyed) return;

                _destroyed = true;
                _done = true;
                _reading = null;
                _pixels = null;
                _buffers.Clear();

                foreach (var tile in Tiles)
                {
                    if (tile.Texture != null) Discard(tile.Texture);

                    tile.Texture = null;
                    ResidentBytesAll -= tile.Bytes;
                    tile.Bytes = 0;
                }

                _bytes = 0;
            }
        }

        // --- the geometry, prepared off the main thread ------------------------------------------------

        /// <summary>
        /// One mesh's worth of arrays, prepared by a worker and uploaded by <see cref="MakeMesh(MeshData)"/>.
        /// Copies, not the worker's lists: the accumulators reuse their lists for the next chunk.
        /// </summary>
        internal sealed class MeshData
        {
            public string Name = "";
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Indices;
            public Color32[] Colours;

            public static MeshData From(
                string name, List<Vector3> vertices, List<Vector2> uvs, List<int> indices, List<Color32> colours)
            {
                var data = new MeshData
                {
                    Name = name,
                    Vertices = vertices.ToArray(),
                    Uvs = uvs.ToArray(),
                    Indices = indices.ToArray(),
                    Colours = colours?.ToArray()
                };

                data.Normals = NormalsOf(data.Vertices, data.Indices);
                return data;
            }

            /// <summary>
            /// WORKER. <see cref="From"/> over only the vertices <paramref name="indices"/> reference, renumbered in
            /// first-use order. A roof chunk's vertices are shared by its own-floor list and every "on another
            /// floor" list, and each destination copied ALL of them - up to 250k vertices per destination, most
            /// unreferenced, on the CPU and the GPU alike (review F24). The normals are the same either way:
            /// NormalsOf only sums the triangles of the list it is given.
            /// </summary>
            public static MeshData Compacted(
                string name, List<Vector3> vertices, List<Vector2> uvs, List<int> indices, List<Color32> colours)
            {
                var remap = new Dictionary<int, int>(Math.Min(indices.Count, vertices.Count));
                var keptVertices = new List<Vector3>();
                var keptUvs = new List<Vector2>();
                var keptColours = colours != null ? new List<Color32>() : null;
                var keptIndices = new List<int>(indices.Count);

                foreach (var old in indices)
                {
                    if (!remap.TryGetValue(old, out var index))
                    {
                        index = keptVertices.Count;
                        remap[old] = index;
                        keptVertices.Add(vertices[old]);
                        keptUvs.Add(uvs[old]);
                        keptColours?.Add(colours[old]);
                    }

                    keptIndices.Add(index);
                }

                return From(name, keptVertices, keptUvs, keptIndices, keptColours);
            }

            /// <summary>
            /// WORKER. Per-vertex normals: the sum of the (area-weighted) face normals of every triangle using the
            /// vertex, normalised - cross(v1 - v0, v2 - v0) for a triangle (v0, v1, v2), which is up for the
            /// ground's (a, c, b) winding, the winding that rendered lit. Computed HERE rather than by
            /// Mesh.RecalculateNormals on the main thread, so the upload is cheaper. A vertex no
            /// triangle uses, or whose faces cancel, gets straight up.
            /// </summary>
            private static Vector3[] NormalsOf(Vector3[] vertices, int[] indices)
            {
                var normals = new Vector3[vertices.Length];

                for (var t = 0; t + 2 < indices.Length; t += 3)
                {
                    var a = indices[t];
                    var b = indices[t + 1];
                    var c = indices[t + 2];

                    var n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);

                    normals[a] += n;
                    normals[b] += n;
                    normals[c] += n;
                }

                for (var i = 0; i < normals.Length; i++)
                {
                    var n = normals[i];
                    var length = n.magnitude;
                    normals[i] = length > 1e-12f ? n / length : Vector3.up;
                }

                return normals;
            }
        }

        /// <summary>One floor's geometry and counts, as a worker prepared it. The counts go onto the entry in
        /// <see cref="AfterPrepare"/>; every <see cref="MeshData"/> becomes one unit of upload.</summary>
        private sealed class FloorData
        {
            public int Level;
            public long Cells;
            public long GroundTriangles;
            public long BuildingTriangles;
            public long TopTriangles;
            public long SideTriangles;
            public long WallTriangles;
            public long MovedRoofTriangles;
            public long GroundSkirtTriangles;
            public int BuildingCount;
            public int Dropped;

            public readonly List<MeshData> Ground = new List<MeshData>();
            public readonly List<MeshData> Roofs = new List<MeshData>();
            public readonly List<(int Level, MeshData Data)> RoofsElsewhere = new List<(int Level, MeshData Data)>();
            public readonly List<MeshData>[] Sides = new List<MeshData>[4];

            /// <summary>Atlas-textured faces, per tile (<see cref="TileStore"/> index).</summary>
            public readonly Dictionary<int, List<MeshData>> Atlas = new Dictionary<int, List<MeshData>>();
            public long AtlasTriangles;
        }

        /// <summary>One floor's walls as a worker prepared them: a colour and its meshes per tint.</summary>
        private sealed class WallData
        {
            public readonly List<(Color Colour, List<MeshData> Meshes)> Tints = new List<(Color Colour, List<MeshData> Meshes)>();
            public Color Average = FallbackWallColour;
            public bool HasAverage;
            public int TintCount;
            public long Triangles;
        }

        /// <summary>
        /// What a worker builds from: a SNAPSHOT of everything the classification and the UVs read, taken on
        /// the main thread, plus scratch arrays of its own. No Unity object is in it (a SidePicture is read for
        /// its numbers only - its picture slot is never touched), and nothing in it changes after the snapshot,
        /// so a worker cannot see the view rebuilt or a side dropped under it halfway through a floor.
        ///
        /// Everything that decides a triangle's fate - <see cref="ViewFor"/>, <see cref="FloorForFace"/>, the
        /// finite test - lives here, so the roof pass and the wall pass, even on two workers, classify with the
        /// SAME code and the same inputs: a triangle is in exactly one of them, as it was on one thread.
        /// </summary>
        private sealed class Prep
        {
            public MapMeshFile File;
            public string MapKey = "";
            public bool Flat;
            public bool SidesActive;
            public readonly DynamicMapsLibrary.SidePicture[] Sides = new DynamicMapsLibrary.SidePicture[4];
            public (int Level, float Low, float High)[] FloorRanges = new (int, float, float)[0];
            public float SpanX;
            public float SpanZ;

            /// <summary>Which atlas pages this view can draw (usable, textured shader). A face on a page that is
            /// not here keeps the stage U/V rule, so a page lost in transport costs its faces' texture, never
            /// the faces.</summary>
            public readonly bool[] PagePresent = new bool[MaxAtlasPages];

            /// <summary>The tiles of this file, for their index. Read-only on the worker: built whole before any
            /// prep starts and never changed after (see <see cref="TileStore.TileOf"/>). Null without an atlas.</summary>
            public TileStore Tiles;

            /// <summary>The atlas ranges of the building last loaded, or null.</summary>
            private List<MapMeshFile.AtlasRange> _ranges;

            /// <summary>Per range of the building last loaded, its tile index, or -1 when it draws no tile here (page
            /// absent, or a tile the store does not know).</summary>
            private int[] _rangeTile = new int[0];

            /// <summary>The building last loaded - the one whose UVs <see cref="AtlasUv"/> reads.</summary>
            private MapMeshFile.Building _building;

            /// <summary>
            /// The atlas range of the triangle at index position <paramref name="i"/> of the building last loaded,
            /// or -1 when it is in none this view can draw. Decides a face BEFORE the stage U/V rule does: an atlas
            /// face is never a roof, a side face or a tint, so the roof pass and the wall pass both ask here first
            /// and still split every triangle exactly once between them.
            /// </summary>
            public int AtlasRangeAt(int i)
            {
                if (_ranges == null) return -1;

                for (var k = 0; k < _ranges.Count; k++)
                {
                    var range = _ranges[k];
                    if (i < range.First || i >= range.First + range.Count) continue;

                    return _rangeTile[k] >= 0 ? k : -1;
                }

                return -1;
            }

            /// <summary>The tile range <paramref name="k"/> of the building last loaded draws with.</summary>
            public int TileOfRange(int k) => _rangeTile[k];

            /// <summary>The raw material UV of vertex <paramref name="i"/> of the building last loaded, in range
            /// <paramref name="k"/> - see <see cref="AtlasUvOf"/>.</summary>
            public Vector2 AtlasUv(int i, int k) => AtlasUvOf(_building, i, _ranges[k]);

            // Scratch, per worker. Grown, never shrunk.
            private bool[] _finite = new bool[0];
            private Vector3[] _positions = new Vector3[0];
            private int[] _remap = new int[0];
            private int _count;

            public bool Finite(int i) => _finite[i];
            public Vector3 Position(int i) => _positions[i];

            /// <summary>The planar UV of a world point: where it falls across the extent, which is where it
            /// falls across the picture. CLAMPED, because the grid overhangs by up to half a cell and a
            /// building's triangles are kept up to a metre outside the extent.</summary>
            public Vector2 PlanarUv(float x, float z) =>
                new Vector2(
                    Mathf.Clamp01((x - (float)File.MinX) / SpanX),
                    Mathf.Clamp01((z - (float)File.MinZ) / SpanZ));

            /// <summary>The band a building's declared level belongs to: its own where the file has that
            /// band, else the nearest.</summary>
            public int BandLevelFor(int level)
            {
                var best = int.MinValue;
                var distance = int.MaxValue;

                foreach (var band in File.Bands)
                {
                    if (band == null) continue;
                    if (band.Level == level) return level;

                    var gap = Math.Abs(band.Level - level);
                    if (gap >= distance) continue;

                    distance = gap;
                    best = band.Level;
                }

                return best;
            }

            /// <summary>Dequantises a building into the scratch and flags its finite vertices. A y stored as
            /// NoHit dequantises to NaN, and one NaN vertex in a merged bucket makes the whole bucket's bounds
            /// NaN - so every triangle touching one is dropped. False when no vertex is finite.</summary>
            public bool LoadBuilding(MapMeshFile.Building building)
            {
                var n = building.VertexCount;

                if (_finite.Length < n) _finite = new bool[n];
                if (_positions.Length < n) _positions = new Vector3[n];
                if (_remap.Length < n) _remap = new int[n];

                _count = n;
                _building = building;
                _ranges = AtlasRangesOf(building);

                if (_ranges != null)
                {
                    if (_rangeTile.Length < _ranges.Count) _rangeTile = new int[_ranges.Count];

                    for (var k = 0; k < _ranges.Count; k++)
                    {
                        var range = _ranges[k];
                        var present = Tiles != null && range.Page >= 0 && range.Page < MaxAtlasPages && PagePresent[range.Page];
                        _rangeTile[k] = present ? Tiles.TileOf(range) : -1;
                    }
                }

                _skirtBand = File.Band(BandLevelFor(building.Level));
                _minY = float.PositiveInfinity;

                var any = false;

                for (var i = 0; i < n; i++)
                {
                    var vertex = building.VertexAt(i);

                    _finite[i] = !float.IsNaN(vertex.x) && !float.IsInfinity(vertex.x) &&
                                 !float.IsNaN(vertex.y) && !float.IsInfinity(vertex.y) &&
                                 !float.IsNaN(vertex.z) && !float.IsInfinity(vertex.z);

                    _positions[i] = vertex;
                    any |= _finite[i];
                    if (_finite[i] && vertex.y < _minY) _minY = vertex.y;
                }

                return any;
            }

            /// <summary>The relief of the band the building last loaded is filed in, or null. See GroundSkirt.</summary>
            private MapMeshFile.ReliefBand _skirtBand;

            /// <summary>The lowest finite vertex height of the building last loaded.</summary>
            private float _minY;

            /// <summary>
            /// Whether a triangle of the building last loaded is GROUND the building happens to own - a
            /// foundation skirt, a pavement apron, a kiosk's plinth - rather than a roof: near-horizontal
            /// (<c>|n.y| &gt;= 0.5</c>), at the building's base (centroid within <see cref="GroundSkirtRise"/> of its
            /// lowest vertex), and within <see cref="GroundSkirtTolerance"/> of the band's relief height under
            /// the centroid. The relief already draws that ground with the floor's own picture; the face drawn
            /// as well took a side view or a wall tint and smeared it over the street (the brown apron around
            /// the Bridge kiosk). The base test is what keeps real roofs: the top band's relief is measured from
            /// over the roofs, so a roof is ALSO within 0.3 m of its relief - but never within a metre of its
            /// building's foot. No relief under the centroid (a hole, off the grid) keeps the face.
            /// </summary>
            public bool GroundSkirt(Vector3 a, Vector3 b, Vector3 c)
            {
                if (_skirtBand == null) return false;

                var n = Vector3.Cross(b - a, c - a);
                var length = n.magnitude;
                if (!(length > 1e-6f) || Mathf.Abs(n.y) / length < RoofNormalY) return false;

                var y = (a.y + b.y + c.y) / 3f;
                if (!(y <= _minY + GroundSkirtRise)) return false;

                if (!_skirtBand.TryHeightAt((a.x + b.x + c.x) / 3f, (a.z + b.z + c.z) / 3f, out var ground)) return false;

                return Mathf.Abs(y - ground) <= GroundSkirtTolerance;
            }

            /// <summary>Forgets every roof vertex placed from the building last loaded - at the start of each
            /// building, and after a chunk is flushed mid-building (its indices restart at zero).</summary>
            public void ResetRemap()
            {
                for (var i = 0; i < _count; i++) _remap[i] = -1;
            }

            /// <summary>The roof chunk's vertex for building vertex <paramref name="i"/>, added on first use.</summary>
            public int RoofVertex(int i, List<Vector3> vertices, List<Vector2> uvs, List<Color32> colours)
            {
                if (_remap[i] >= 0) return _remap[i];

                var p = _positions[i];

                _remap[i] = vertices.Count;
                vertices.Add(p);
                uvs.Add(PlanarUv(p.x, p.z));
                colours?.Add(FlatBuildingColour);

                return _remap[i];
            }

            /// <summary>
            /// Which picture textures a building face: <see cref="TopView"/>, a side (slot + 1), or
            /// <see cref="TintView"/>. WITHOUT side pictures, exactly the tint build's rule - top when
            /// <c>|n.y| &gt;= 0.5</c> (<see cref="IsRoof"/>), else tint. WITH them, the Stage U contract: the
            /// picture whose camera looks most squarely at the face, scored <c>-dot(n, f)</c> with the top
            /// camera's f = (0,-1,0) among them; below <see cref="MinViewScore"/> a tint. Signed normal, ties to
            /// the earlier view (top, then N, S, E, W).
            /// </summary>
            public int ViewFor(Vector3 a, Vector3 b, Vector3 c)
            {
                if (!SidesActive) return IsRoof(a, b, c) ? TopView : TintView;

                var n = Vector3.Cross(b - a, c - a);
                var length = n.magnitude;

                if (!(length > 1e-6f)) return TopView;

                n /= length;

                var best = TopView;
                var bestScore = n.y;

                for (var slot = 0; slot < Sides.Length; slot++)
                {
                    var side = Sides[slot];
                    if (side == null) continue;

                    var score = -Vector3.Dot(n, side.Forward);
                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = slot + 1;
                }

                return bestScore < MinViewScore ? TintView : best;
            }

            /// <summary>The floor whose picture textures a top face at height <paramref name="y"/>: the floor
            /// it STANDS ON when its height is inside one floor's band (plus the slack), else
            /// <paramref name="filed"/>. Nearer floor wins an overlap; ties to the higher floor. See
            /// Built.RoofsOnOtherFloors for why.</summary>
            public int FloorForFace(float y, int filed)
            {
                var best = filed;
                var bestDistance = float.MaxValue;

                for (var i = 0; i < FloorRanges.Length; i++)
                {
                    var range = FloorRanges[i];
                    if (y < range.Low || y > range.High) continue;

                    var distance = Mathf.Max(0f, Mathf.Max(range.Low + FloorFaceSlack - y, y - (range.High - FloorFaceSlack)));

                    if (distance < bestDistance || (Mathf.Approximately(distance, bestDistance) && range.Level > best))
                    {
                        best = range.Level;
                        bestDistance = distance;
                    }
                }

                return best;
            }

            /// <summary>The triangle at <paramref name="i"/> of the building last loaded, if it is a usable
            /// WALL (a tint): in range, finite, and <see cref="ViewFor"/> says tint. The exact complement of
            /// what the roof pass keeps as top or side.</summary>
            public bool WallTriangle(MapMeshFile.Building building, int i, out Vector3 a, out Vector3 b, out Vector3 c)
            {
                a = b = c = Vector3.zero;

                var ia = building.Indices[i];
                var ib = building.Indices[i + 1];
                var ic = building.Indices[i + 2];

                if (ia >= building.VertexCount || ib >= building.VertexCount || ic >= building.VertexCount) return false;
                if (!_finite[ia] || !_finite[ib] || !_finite[ic]) return false;

                var pa = _positions[ia];
                var pb = _positions[ib];
                var pc = _positions[ic];

                // Ground a building owns is the relief's, in both passes: the roof pass skips it the same way.
                if (GroundSkirt(pa, pb, pc)) return false;

                // An atlas face is textured by its own material, never tinted.
                if (AtlasRangeAt(i) >= 0) return false;

                a = pa;
                b = pb;
                c = pc;

                return ViewFor(a, b, c) == TintView;
            }

            /// <summary>
            /// The colour of the building last loaded: the picture under the middle of its footprint, a
            /// quarter darker and a little greyer - a 3x3 average around the centroid, ignoring transparent
            /// pixels. Row 0 of the readback is the picture's BOTTOM (MinZ), the planar UV's convention.
            /// </summary>
            public Color WallColour(Color32[] palette, int width, int height)
            {
                if (palette == null || width <= 0 || height <= 0) return FallbackWallColour;

                var sumX = 0d;
                var sumZ = 0d;
                var n = 0;

                for (var i = 0; i < _count; i++)
                {
                    if (!_finite[i]) continue;
                    sumX += _positions[i].x;
                    sumZ += _positions[i].z;
                    n++;
                }

                if (n == 0) return FallbackWallColour;

                var uv = PlanarUv((float)(sumX / n), (float)(sumZ / n));

                var cx = Mathf.Clamp(Mathf.RoundToInt(uv.x * (width - 1)), 0, width - 1);
                var cy = Mathf.Clamp(Mathf.RoundToInt(uv.y * (height - 1)), 0, height - 1);

                float r = 0f, g = 0f, bl = 0f;
                var taken = 0;

                for (var dy = -1; dy <= 1; dy++)
                {
                    var y = cy + dy;
                    if (y < 0 || y >= height) continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var x = cx + dx;
                        if (x < 0 || x >= width) continue;

                        var pixel = palette[y * width + x];
                        if (pixel.a < 128) continue;

                        r += pixel.r;
                        g += pixel.g;
                        bl += pixel.b;
                        taken++;
                    }
                }

                if (taken == 0) return FallbackWallColour;

                var average = new Color(r / (255f * taken), g / (255f * taken), bl / (255f * taken), 1f);

                Color.RGBToHSV(average, out var hue, out var saturation, out var value);

                var tinted = Color.HSVToRGB(hue, saturation * WallSaturation, value * WallValue);
                tinted.a = 1f;

                return tinted;
            }
        }

        // --- the mesh file's atlas data (format v2) - the ONE place the viewer reads it --------------------

        /// <summary>
        /// A building's atlas ranges, or null when it has none: a building whose materials were not captured, or
        /// one with ranges but no UVs. WORKER-safe: plain reads of the parsed file, which MapMeshFile.Read has
        /// already validated (ranges ascending, whole triangles, inside the building, on a page the file has, tile
        /// rects on the page, finite bounds, every vertex in at most one range).
        ///
        /// With <see cref="AtlasUvOf"/> and <see cref="TileStore.For"/>, the only places the viewer reads MapMeshFile
        /// format v3's atlas data.
        /// </summary>
        private static List<MapMeshFile.AtlasRange> AtlasRangesOf(MapMeshFile.Building building)
        {
            var ranges = building.Ranges;
            if (ranges == null || ranges.Count == 0 || building.U == null || building.V == null) return null;

            return ranges;
        }

        /// <summary>
        /// Vertex <paramref name="i"/>'s RAW material UV (format v3), dequantised over the bounds of the one range
        /// that uses it: <c>u = UMin + code / 65535 * (UMax - UMin)</c> (MapMeshFile.AtlasRange.U), in repeats of
        /// the range's tile. Drawn on the tile's OWN texture with wrapMode Repeat, so 17.25 samples the tile at
        /// 0.25 - the GPU does the repeating a page never could. No flip: v = 0 is the tile's bottom row, which is
        /// where a texture's v = 0 is, and the tile was cut from the page bottom-up (TileStore.Cut).
        /// </summary>
        private static Vector2 AtlasUvOf(MapMeshFile.Building building, int i, MapMeshFile.AtlasRange range) =>
            new Vector2(building.UOf(i, range), building.VOf(i, range));

        /// <summary>The worker's snapshot of this view, with fresh scratch. Main thread only.</summary>
        private Prep SnapshotPrep()
        {
            var prep = new Prep
            {
                File = _file,
                MapKey = _mapKey,
                Flat = _flatColours,
                SidesActive = SidesActive,
                FloorRanges = _floorRanges.ToArray(),
                SpanX = (float)(_file.MaxX - _file.MinX),
                SpanZ = (float)(_file.MaxZ - _file.MinZ)
            };

            for (var slot = 0; slot < _sides.Length; slot++) prep.Sides[slot] = _sides[slot];

            if (AtlasActive && _heldTiles != null)
            {
                prep.Tiles = _heldTiles;
                for (var page = 0; page < _pages.Length; page++) prep.PagePresent[page] = _pages[page] != null;
            }

            return prep;
        }

        /// <summary>WORKER: one floor, prepared. Cancellable between chunks and buildings - a cancelled job
        /// throws OperationCanceledException and its output is never made.</summary>
        private static FloorData PrepareFloor(Prep p, int level, CancellationToken cancel)
        {
            var band = p.File.Band(level);
            var data = new FloorData { Level = level, Cells = band?.CellCount ?? 0L };

            if (band != null && p.SpanX > 0f && p.SpanZ > 0f)
            {
                PrepareGround(p, band, data, cancel);
                PrepareBuildings(p, level, data, cancel);
            }

            return data;
        }

        /// <summary>
        /// WORKER. One band's ground: a quad per cell whose four CORNERS - the four neighbouring cell centres -
        /// were all measured; a cell with no hit is a hole and stays one. Vertices are the cell centres in world
        /// metres, row 0 at MinZ (world order, not the picture's), so the planar UV lands the picture the right
        /// way up. Chunked in rows sharing one row with the next chunk, each chunk under
        /// <see cref="MaxVerticesPerMesh"/> vertices. Wound (a, c, b) / (b, c, d) so faces point up - the phase
        /// 3-0 winding that rendered lit.
        /// </summary>
        private static void PrepareGround(Prep p, MapMeshFile.ReliefBand band, FloorData data, CancellationToken cancel)
        {
            if (band.Width < 2 || band.Height < 2) return;

            var rowsPerChunk = Mathf.Clamp(MaxVerticesPerMesh / Mathf.Max(1, band.Width), 2, band.Height);

            var map = new int[band.Width * rowsPerChunk];

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = p.Flat ? new List<Color32>() : null;

            for (var first = 0; first < band.Height - 1; first += rowsPerChunk - 1)
            {
                var last = Mathf.Min(band.Height - 1, first + rowsPerChunk - 1);

                cancel.ThrowIfCancellationRequested();

                for (var i = 0; i < map.Length; i++) map[i] = -1;

                vertices.Clear();
                uvs.Clear();
                indices.Clear();
                colours?.Clear();

                for (var row = first; row < last; row++)
                {
                    for (var col = 0; col < band.Width - 1; col++)
                    {
                        // All four or none: three corners would be a notch, which reads as damage.
                        if (band.CodeAt(col, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col, row + 1) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row + 1) == MapMeshFile.NoHit) continue;

                        var a = Corner(p, band, col, row, first, map, vertices, uvs, colours);
                        var b = Corner(p, band, col + 1, row, first, map, vertices, uvs, colours);
                        var c = Corner(p, band, col, row + 1, first, map, vertices, uvs, colours);
                        var d = Corner(p, band, col + 1, row + 1, first, map, vertices, uvs, colours);

                        indices.Add(a);
                        indices.Add(c);
                        indices.Add(b);

                        indices.Add(b);
                        indices.Add(c);
                        indices.Add(d);
                    }
                }

                if (indices.Count > 0)
                {
                    data.Ground.Add(MeshData.From($"{p.MapKey}-relief-{band.Level}-{first}", vertices, uvs, indices, colours));
                    data.GroundTriangles += indices.Count / 3;
                }

                if (last >= band.Height - 1) break;
            }
        }

        /// <summary>One cell centre as a vertex, added on first use; the map is per CHUNK.</summary>
        private static int Corner(
            Prep p, MapMeshFile.ReliefBand band, int col, int row, int firstRow, int[] map,
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colours)
        {
            var slot = (row - firstRow) * band.Width + col;
            var known = map[slot];
            if (known >= 0) return known;

            var x = band.CellCentreX(col);
            var z = band.CellCentreZ(row);
            var y = p.File.HeightOf(band.CodeAt(col, row));

            map[slot] = vertices.Count;

            vertices.Add(new Vector3(x, y, z));
            uvs.Add(p.PlanarUv(x, z));
            colours?.Add(FlatGroundColour);

            return map[slot];
        }

        /// <summary>
        /// WORKER. The buildings filed under this floor: every usable triangle to the top picture (a roof), a
        /// side picture, or a tint (only COUNTED here; the walls are built by <see cref="PrepareWalls"/>). Roof
        /// faces on another floor's height go to that floor's picture (<see cref="Prep.FloorForFace"/>).
        ///
        /// Roof vertices are placed LAZILY through a per-building remap, and the chunk is flushed whenever the
        /// next triangle would take it past <see cref="MaxVerticesPerMesh"/> - so a single building of two
        /// million vertices still lands in meshes under the cap, where appending a whole building at once
        /// could not. Side faces use unshared vertices (per-face light), like the tints.
        /// </summary>
        private static void PrepareBuildings(Prep p, int level, FloorData data, CancellationToken cancel)
        {
            if (p.File.Buildings == null || p.File.Buildings.Count == 0) return;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = p.Flat ? new List<Color32>() : null;

            // Top faces standing on ANOTHER floor, by that floor's level: indices into the same chunk.
            var elsewhere = new Dictionary<int, List<int>>();

            // One per side slot, made on the first face that side takes.
            var sideSinks = new MeshSink[SideOrder.Length];

            // One per TILE (game material), made on the first face that uses it. Shared vertices per building, as
            // the file indexes them: the game mesh's own topology, so its hard edges (split vertices) stay hard.
            var tileSinks = new Dictionary<int, PageSink>();
            var serial = 0;

            var part = 0;

            foreach (var building in p.File.Buildings)
            {
                cancel.ThrowIfCancellationRequested();
                serial++;

                if (building == null || building.VertexCount == 0 || building.Indices == null) continue;
                if (p.BandLevelFor(building.Level) != level) continue;

                data.BuildingCount++;

                p.LoadBuilding(building);
                p.ResetRemap();

                // Three at a time; a triangle with an index past the building's own vertices is dropped - a
                // caller that trusts a file it did not write is a caller that throws inside a mesh build.
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count; i += 3)
                {
                    var ua = building.Indices[i];
                    var ub = building.Indices[i + 1];
                    var uc = building.Indices[i + 2];

                    // Range-checked as uint BEFORE the cast: an index past int.MaxValue must be refused, not
                    // wrapped negative into a valid-looking one.
                    if (ua >= building.VertexCount || ub >= building.VertexCount || uc >= building.VertexCount) continue;

                    var a = (int)ua;
                    var b = (int)ub;
                    var c = (int)uc;

                    if (!p.Finite(a) || !p.Finite(b) || !p.Finite(c))
                    {
                        data.Dropped++;
                        continue;
                    }

                    // Ground the building owns (a foundation skirt): the relief draws it with the floor's picture.
                    // Before the atlas, as in WallTriangle, so both passes still split every triangle once.
                    if (p.GroundSkirt(p.Position(a), p.Position(b), p.Position(c)))
                    {
                        data.GroundSkirtTriangles++;
                        continue;
                    }

                    // An atlas face first: the game's own material, the file's own raw UVs.
                    var range = p.AtlasRangeAt(i);

                    if (range >= 0)
                    {
                        var tile = p.TileOfRange(range);

                        if (!tileSinks.TryGetValue(tile, out var sink))
                        {
                            sink = new PageSink();
                            tileSinks[tile] = sink;
                        }

                        if (sink.Count + 3 > MaxVerticesPerMesh) sink.Flush($"{p.MapKey}-tile{tile}-{level}", AtlasList(data, tile));

                        sink.Triangle(p, serial, building.VertexCount, range, a, b, c);
                        data.AtlasTriangles++;
                        continue;
                    }

                    var pa = p.Position(a);
                    var pb = p.Position(b);
                    var pc = p.Position(c);

                    var view = p.ViewFor(pa, pb, pc);

                    if (view == TintView)
                    {
                        data.WallTriangles++;
                        continue;
                    }

                    if (view != TopView)
                    {
                        var slot = view - 1;
                        var sink = sideSinks[slot] ??= new MeshSink { Flat = false };
                        var side = p.Sides[slot];

                        if (sink.Count + 3 > MaxVerticesPerMesh)
                            sink.Flush($"{p.MapKey}-side{SideOrder[slot]}-{level}", data.Sides[slot] ??= new List<MeshData>());

                        sink.Add(pa, SideUv(side, pa));
                        sink.Add(pb, SideUv(side, pb));
                        sink.Add(pc, SideUv(side, pc));

                        data.SideTriangles++;
                        continue;
                    }

                    data.TopTriangles++;

                    // Room for up to three new vertices, or the chunk goes now and this building's vertices are
                    // placed afresh in the next one.
                    if (vertices.Count + 3 > MaxVerticesPerMesh)
                    {
                        FlushRoofs(p, data, level, part++, vertices, uvs, indices, colours, elsewhere);

                        vertices.Clear();
                        uvs.Clear();
                        indices.Clear();
                        colours?.Clear();
                        p.ResetRemap();
                    }

                    var floorLevel = p.FloorForFace((pa.y + pb.y + pc.y) / 3f, level);
                    var roofIndices = indices;

                    if (floorLevel != level)
                    {
                        if (!elsewhere.TryGetValue(floorLevel, out roofIndices))
                        {
                            roofIndices = new List<int>();
                            elsewhere[floorLevel] = roofIndices;
                        }

                        data.MovedRoofTriangles++;
                    }

                    roofIndices.Add(p.RoofVertex(a, vertices, uvs, colours));
                    roofIndices.Add(p.RoofVertex(b, vertices, uvs, colours));
                    roofIndices.Add(p.RoofVertex(c, vertices, uvs, colours));
                }
            }

            for (var slot = 0; slot < sideSinks.Length; slot++)
            {
                if (sideSinks[slot] == null) continue;
                sideSinks[slot].Flush($"{p.MapKey}-side{SideOrder[slot]}-{level}", data.Sides[slot] ??= new List<MeshData>());
            }

            foreach (var pair in tileSinks) pair.Value.Flush($"{p.MapKey}-tile{pair.Key}-{level}", AtlasList(data, pair.Key));

            // Counted into the building total whether or not they are built yet, so the log line's totals are
            // the file's and do not move when a floor's walls arrive a frame later.
            data.BuildingTriangles += data.WallTriangles + data.SideTriangles + data.AtlasTriangles;

            FlushRoofs(p, data, level, part, vertices, uvs, indices, colours, elsewhere);
        }

        /// <summary>WORKER. A floor's mesh list for one tile, made on first use.</summary>
        private static List<MeshData> AtlasList(FloorData data, int tile)
        {
            if (!data.Atlas.TryGetValue(tile, out var list))
            {
                list = new List<MeshData>();
                data.Atlas[tile] = list;
            }

            return list;
        }

        /// <summary>One chunk of roofs into mesh data: the building band's own, and one per other floor its
        /// faces stand on, all sharing the chunk's vertices. The index lists are emptied for the next chunk.</summary>
        private static void FlushRoofs(
            Prep p, FloorData data, int level, int part, List<Vector3> vertices, List<Vector2> uvs, List<int> indices,
            List<Color32> colours, Dictionary<int, List<int>> elsewhere)
        {
            if (indices.Count > 0)
            {
                data.Roofs.Add(MeshData.Compacted($"{p.MapKey}-buildings-{level}-{part}", vertices, uvs, indices, colours));
                data.BuildingTriangles += indices.Count / 3;
            }

            foreach (var pair in elsewhere)
            {
                if (pair.Value.Count == 0) continue;

                data.RoofsElsewhere.Add((pair.Key, MeshData.Compacted(
                    $"{p.MapKey}-buildings-{level}-on{pair.Key}-{part}", vertices, uvs, pair.Value, colours)));
                data.BuildingTriangles += pair.Value.Count / 3;

                pair.Value.Clear();
            }
        }

        /// <summary>
        /// WORKER. One floor's walls: each walled building's colour from the palette (read back on the main
        /// thread before this started), the colours grouped into at most <see cref="MaxWallTints"/> buckets,
        /// and the geometry into each bucket with UNSHARED vertices - a corner shared by two walls at right
        /// angles would light both as if they faced the corner. Two walks, as before: colours first so they can
        /// be bucketed knowing all of them, then geometry. Null when the floor has no walls.
        /// </summary>
        private static WallData PrepareWalls(Prep p, int level, Color32[] palette, int width, int height, CancellationToken cancel)
        {
            if (!(p.SpanX > 0f) || !(p.SpanZ > 0f)) return null;

            // --- walk 1: which buildings have walls, and in what colour
            var walled = new List<int>();
            var colours = new List<Color>();

            for (var index = 0; index < p.File.Buildings.Count; index++)
            {
                cancel.ThrowIfCancellationRequested();

                var building = p.File.Buildings[index];
                if (building == null || building.VertexCount == 0 || building.Indices == null) continue;
                if (p.BandLevelFor(building.Level) != level) continue;

                if (!p.LoadBuilding(building)) continue;

                var hasWall = false;
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count && !hasWall; i += 3)
                    if (p.WallTriangle(building, i, out _, out _, out _)) hasWall = true;

                if (!hasWall) continue;

                walled.Add(index);
                colours.Add(p.Flat ? (Color)FlatWallColour : p.WallColour(palette, width, height));
            }

            if (walled.Count == 0) return null;

            var data = new WallData();

            if (!p.Flat)
            {
                var sum = Color.black;
                for (var i = 0; i < colours.Count; i++) sum += colours[i];

                var average = sum / colours.Count;
                average.a = 1f;

                data.Average = average;
                data.HasAverage = true;
            }

            // --- the buckets
            var centres = new List<Color>();
            var bucketOf = BucketColours(colours, p.Flat ? 1 : MaxWallTints, centres);

            var sinks = new MeshSink[centres.Count];

            for (var k = 0; k < centres.Count; k++)
            {
                sinks[k] = new MeshSink { Flat = p.Flat };
                data.Tints.Add((centres[k], new List<MeshData>()));
            }

            data.TintCount = p.Flat ? 0 : centres.Count;

            // --- walk 2: the geometry
            for (var w = 0; w < walled.Count; w++)
            {
                cancel.ThrowIfCancellationRequested();

                var building = p.File.Buildings[walled[w]];
                if (!p.LoadBuilding(building)) continue;

                var k = bucketOf[w];
                var sink = sinks[k];
                var target = data.Tints[k].Meshes;
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count; i += 3)
                {
                    if (!p.WallTriangle(building, i, out var a, out var b, out var c)) continue;

                    if (sink.Count + 3 > MaxVerticesPerMesh) sink.Flush($"{p.MapKey}-walls-{level}-{k}", target);

                    sink.Add(a, p.PlanarUv(a.x, a.z));
                    sink.Add(b, p.PlanarUv(b.x, b.z));
                    sink.Add(c, p.PlanarUv(c.x, c.z));

                    data.Triangles++;
                }
            }

            for (var k = 0; k < sinks.Length; k++) sinks[k].Flush($"{p.MapKey}-walls-{level}-{k}", data.Tints[k].Meshes);

            return data;
        }

        /// <summary>
        /// Atlas-textured geometry while it is prepared: SHARED vertices per building, as the file indexes them,
        /// with the file's UVs, flushed into mesh data under the vertex cap. The remap is stamped rather than
        /// cleared - a new building, or a flush in the middle of one, bumps the stamp, and every older entry reads
        /// as unset - so eight page sinks cost nothing per building they are not touched by.
        /// </summary>
        private sealed class PageSink
        {
            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<int> _indices = new List<int>();
            private int[] _index = new int[0];
            private int[] _stamp = new int[0];
            private int _current = 1;
            private int _building = -1;
            private int _part;

            public int Count => _vertices.Count;

            public void Triangle(Prep p, int building, int vertexCount, int range, int a, int b, int c)
            {
                if (building != _building)
                {
                    _building = building;
                    _current++;

                    if (_index.Length < vertexCount)
                    {
                        _index = new int[vertexCount];
                        _stamp = new int[vertexCount];
                    }
                }

                _indices.Add(Vertex(p, range, a));
                _indices.Add(Vertex(p, range, b));
                _indices.Add(Vertex(p, range, c));
            }

            /// <summary>A building vertex placed once per chunk. Its UV is dequantised over ITS range, and a vertex is
            /// in at most one range (MapMeshFile.Read checks it), so the first use's UV is every use's.</summary>
            private int Vertex(Prep p, int range, int i)
            {
                if (_stamp[i] == _current) return _index[i];

                _stamp[i] = _current;
                _index[i] = _vertices.Count;

                _vertices.Add(p.Position(i));
                _uvs.Add(p.AtlasUv(i, range));

                return _index[i];
            }

            public void Flush(string name, List<MeshData> target)
            {
                if (_indices.Count > 0)
                    target.Add(MeshData.From($"{name}-{_part++}", _vertices, _uvs, _indices, null));

                _vertices.Clear();
                _uvs.Clear();
                _indices.Clear();

                // The next chunk starts empty, so this building's vertices are placed afresh in it.
                _current++;
            }
        }

        /// <summary>Unshared-vertex geometry while it is prepared, flushed into mesh data under the vertex cap:
        /// a tint's walls, or a side's faces.</summary>
        private sealed class MeshSink
        {
            public bool Flat;

            private readonly List<Vector3> _vertices = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<int> _indices = new List<int>();
            private readonly List<Color32> _colours = new List<Color32>();
            private int _part;

            public int Count => _vertices.Count;

            public void Add(Vector3 position, Vector2 uv)
            {
                _indices.Add(_vertices.Count);
                _vertices.Add(position);
                _uvs.Add(uv);
                if (Flat) _colours.Add(FlatWallColour);
            }

            public void Flush(string name, List<MeshData> target)
            {
                if (_indices.Count == 0) return;

                target.Add(MeshData.From($"{name}-{_part++}", _vertices, _uvs, _indices, Flat ? _colours : null));

                _vertices.Clear();
                _uvs.Clear();
                _indices.Clear();
                _colours.Clear();
            }
        }

        /// <summary>What <see cref="Prep.ViewFor"/> answers for a face textured by the top-down picture.</summary>
        private const int TopView = 0;

        /// <summary>What <see cref="Prep.ViewFor"/> answers for a face drawn in a flat tint.</summary>
        private const int TintView = -1;

        /// <summary>The least score a face needs to be textured by the picture that sees it best.</summary>
        private const float MinViewScore = 0.35f;

        /// <summary>How close to vertical a face's normal has to be to count as a roof: cos 60 degrees.</summary>
        private const float RoofNormalY = 0.5f;

        /// <summary>How near the relief a near-horizontal building face has to be to count as ground. See
        /// Prep.GroundSkirt.</summary>
        private const float GroundSkirtTolerance = 0.3f;

        /// <summary>How far above its building's lowest vertex a face can sit and still be a ground skirt: well
        /// under a storey, so no roof - whose relief it also matches - ever qualifies.</summary>
        private const float GroundSkirtRise = 1f;

        /// <summary>Whether a triangle faces up or down enough to take the top-down picture, from its own
        /// cross product. |n.y|, not n.y: an overhang's underside is as flat as a roof. Degenerate goes with the
        /// roofs, where it draws nothing either way.</summary>
        private static bool IsRoof(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            var length = n.magnitude;

            if (!(length > 1e-6f)) return true;

            return Mathf.Abs(n.y) / length >= RoofNormalY;
        }

        /// <summary>A world point's UV on a side picture: the Stage U contract's pixel mapping (row 0 at the
        /// image TOP) turned into a texture's bottom-origin v - <c>v = (dot(u,p) - originU) * ppm / height</c>,
        /// the "height minus" and the "one minus" cancelling. Clamped.</summary>
        private static Vector2 SideUv(DynamicMapsLibrary.SidePicture side, Vector3 p)
        {
            var u = (Vector3.Dot(side.Right, p) - side.OriginR) * side.PxPerMetre / side.Width;
            var v = (Vector3.Dot(side.Up, p) - side.OriginU) * side.PxPerMetre / side.Height;

            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        /// <summary>
        /// MAIN THREAD. One mesh from prepared arrays. <see cref="IndexFormat.UInt32"/> is set BEFORE the
        /// vertices: a mesh left on sixteen-bit indices wraps them past 65,535 vertices. Normals recalculated
        /// here - the one Unity call that costs, and the reason a mesh is one unit of paced work.
        /// </summary>
        private static Mesh MakeMesh(MeshData data)
        {
            var mesh = new Mesh { name = data.Name, indexFormat = IndexFormat.UInt32 };

            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            if (data.Colours != null) mesh.SetColors(data.Colours);
            mesh.SetTriangles(data.Indices, 0, calculateBounds: true);

            // Non-readable: nothing reads it back. The cut is the camera's near plane, so no CPU copy of any
            // mesh is kept, and the MeshData behind it is garbage once this unit returns.
            mesh.UploadMeshData(true);

            return mesh;
        }

        // --- the walls, started on the main thread, prepared on a worker, uploaded paced ----------------

        /// <summary>One floor's wall build in flight: its worker, then its upload units.</summary>
        private sealed class WallJob
        {
            public Built Built;
            public int Level;
            public bool Late;
            public Stopwatch Clock;
            public Task<WallData> Task;

            /// <summary>Finished - uploaded, failed or abandoned. The build line waits for the initial ones.</summary>
            public bool Done;
        }

        /// <summary>Wall builds this view started, in flight or uploading.</summary>
        private readonly List<WallJob> _wallJobs = new List<WallJob>();

        /// <summary>Cancels this view's wall workers; replaced after each <see cref="AbandonWalls"/>.</summary>
        private CancellationTokenSource _wallCancel;

        /// <summary>
        /// Starts one floor's walls, if they are waiting and there is a way to colour them: in flat colours at
        /// once, else once the floor's picture is resident. The palette is read back HERE (a GPU readback, main
        /// thread only, well under a millisecond at 256 columns); the rest goes to a worker.
        /// </summary>
        private void StartWalls(Built built, int level, Texture picture, bool late)
        {
            if (built == null || !built.WallsPending || built.WallsRunning) return;
            if (!_flatColours && picture == null) return;

            Color32[] palette = null;
            var width = 0;
            var height = 0;

            if (!_flatColours)
            {
                try
                {
                    palette = ReadPalette(picture, out width, out height);
                }
                catch (Exception ex)
                {
                    // Not fatal: every building then gets the fallback grey, and the walls still stand.
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: could not read the colours of floor {level} of '{_mapKey}' back from the " +
                        $"GPU ({ex.GetType().Name}: {ex.Message}) - its walls are drawn in one grey.");
                    palette = null;
                }
            }

            var prep = SnapshotPrep();
            built.WallsRunning = true;

            // The view's wall token: AbandonWalls cancels it, so a repaint stops the walls it abandons rather
            // than leaving the worker to finish into nothing.
            _wallCancel ??= new CancellationTokenSource();
            var token = _wallCancel.Token;

            _wallJobs.Add(new WallJob
            {
                Built = built,
                Level = level,
                Late = late,
                Clock = Stopwatch.StartNew(),
                Task = Task.Run(() => PrepareWalls(prep, level, palette, width, height, token), token)
            });
        }

        /// <summary>Wall workers that have finished: their meshes queued, or their failure handled.</summary>
        private void PollWallJobs()
        {
            for (var i = 0; i < _wallJobs.Count; i++)
            {
                var job = _wallJobs[i];
                if (job.Done || job.Task == null || !job.Task.IsCompleted) continue;

                var task = job.Task;
                job.Task = null;

                if (WasCancelled(task)) continue;   // abandoned: AbandonWalls already put the entry back to waiting

                if (task.IsFaulted)
                {
                    WallsFailed(job, task.Exception?.GetBaseException());
                    continue;
                }

                EnqueueWallUpload(job, task.Result);
            }

            // Finished jobs leave the list, so an idle view stops pumping. Backwards, to remove in place.
            for (var i = _wallJobs.Count - 1; i >= 0; i--)
                if (_wallJobs[i].Done) _wallJobs.RemoveAt(i);
        }

        /// <summary>
        /// A floor's prepared walls into the entry, paced: the tints (and their materials) are registered in
        /// the first unit, so a failure later finds every one; each mesh is a unit; the last unit finishes the
        /// job. Each unit catches its own failure, which costs this floor its walls and nothing else.
        /// </summary>
        private void EnqueueWallUpload(WallJob job, WallData data)
        {
            var built = job.Built;

            if (data == null)
            {
                _work.Enqueue(() => FinishWalls(job));
                return;
            }

            var tints = new WallTint[data.Tints.Count];

            _work.Enqueue(() => WallUnit(job, () =>
            {
                if (data.HasAverage)
                {
                    built.WallAverage = data.Average;
                    if (built.SideFallback != null) built.SideFallback.color = data.Average;
                }

                for (var k = 0; k < data.Tints.Count; k++)
                {
                    tints[k] = new WallTint
                    {
                        Colour = data.Tints[k].Colour,
                        Material = MakeWallMaterial(job.Level, k, data.Tints[k].Colour)
                    };

                    built.Walls.Add(tints[k]);
                }

                built.Tints = data.TintCount;
            }));

            for (var k = 0; k < data.Tints.Count; k++)
            {
                var bucket = k;

                foreach (var mesh in data.Tints[k].Meshes)
                {
                    var source = mesh;

                    _work.Enqueue(() => WallUnit(job, () =>
                    {
                        tints[bucket].Meshes.Add(MakeMesh(source));
                    }));
                }
            }

            _work.Enqueue(() => FinishWalls(job));
        }

        /// <summary>One wall unit, skipped once its job has failed or been abandoned, failing its job (not the
        /// map) when it throws.</summary>
        private void WallUnit(WallJob job, Action work)
        {
            if (job.Done) return;

            try
            {
                work();
            }
            catch (Exception ex)
            {
                WallsFailed(job, ex);
            }
        }

        /// <summary>The job's walls are all in: the entry stops waiting, and a floor whose picture came late
        /// says so in its own line (the build line has been written).</summary>
        private void FinishWalls(WallJob job)
        {
            if (job.Done) return;

            job.Done = true;
            job.Built.WallsRunning = false;
            job.Built.WallsPending = false;

            if (job.Late)
            {
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map walls for {0} floor {1} - {2:#,##0} triangles in {3} tint(s), built in " +
                    "{4:#,##0} ms (the floor's picture had just arrived).",
                    _mapKey, job.Level, job.Built.WallTriangles, job.Built.Tints, job.Clock.ElapsedMilliseconds));
            }
        }

        /// <summary>A wall build that failed: what it made goes, the entry stops waiting (once - a failure
        /// retried every frame is a warning every frame), and the floor keeps its roofs and ground.</summary>
        private void WallsFailed(WallJob job, Exception ex)
        {
            if (job.Done) return;

            job.Done = true;
            DestroyWalls(job.Built);
            job.Built.WallsRunning = false;
            job.Built.WallsPending = false;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the walls of floor {job.Level} of the 3D map for '{_mapKey}' could not be built " +
                $"({ex?.GetType().Name}: {ex?.Message}) - that floor draws its roofs and ground only.");
        }

        /// <summary>Whether a wall build started with the first build is still going - the build line waits
        /// for those, so its tint count is the map's.</summary>
        private bool InitialWallsRunning()
        {
            foreach (var job in _wallJobs)
                if (!job.Late && !job.Done) return true;

            return false;
        }

        /// <summary>
        /// Forgets every wall build of this view that has not finished. What an abandoned build had already
        /// registered is destroyed, and its entry goes back to WAITING - so the next view to draw it starts the
        /// walls again, and never inherits half of them. A worker still running is left to finish into
        /// nothing: its result is never collected.
        /// </summary>
        private void AbandonWalls()
        {
            foreach (var job in _wallJobs)
            {
                if (job.Done) continue;

                job.Done = true;
                DestroyWalls(job.Built);
                job.Built.WallsRunning = false;
                job.Built.WallsPending = true;
            }

            _wallJobs.Clear();

            if (_wallCancel != null)
            {
                _wallCancel.Cancel();
                _wallCancel = null;
            }
        }

        // --- the frame ------------------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_broke) return;

            try
            {
                // The atlas tiles, a few a frame, whatever else this frame is waiting for. Shared by every view of
                // the file and pumped once a frame however many views ask.
                _heldTiles?.Pump();

                if (_loading != null)
                {
                    if (!_loading.IsCompleted) return;

                    if (_loading.IsFaulted)
                    {
                        var reason = _loading.Exception?.GetBaseException();

                        // Past this machine's own bound (ViewerReadBound), not a broken file: said as what it is.
                        if (reason is MapMeshFile.ReaderBoundException over)
                        {
                            Plugin.LogSource?.LogWarning(string.Format(
                                CultureInfo.InvariantCulture,
                                "QuestTree: the 3D relief of '{0}' has more detail ({1:#,##0} {2}) than this graphics " +
                                "card can hold (bound {3:#,##0} triangles from {4:#,##0} MB of VRAM) - drawing the flat map.",
                                _mapKey, over.Count, over.Unit, _readBound, _readBoundVramMb));

                            _loading = null;
                            Refuse("has more detail than this graphics card can hold");
                            return;
                        }

                        // InvalidDataException is the format's own refusal - a truncated file, a bad
                        // magic, a count past the caps it enforces on read. One line and the flat
                        // picture, never a stack trace at the player.
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the 3D relief of '{_mapKey}' could not be read " +
                            $"({reason?.GetType().Name}: {reason?.Message}) - drawing the flat picture " +
                            $"instead.");

                        _loading = null;

                        // The exception TYPE, not its message: InvalidDataException is the format's own
                        // refusal and reads as "the file is not a mesh file", while an IOException is
                        // "the file could not be read at all" - two different things for a player to do
                        // about. The message itself can be a paragraph and this goes in a tooltip.
                        Refuse(reason is System.IO.InvalidDataException
                            ? "is not a readable relief file"
                            : "could not be read from disk");

                        return;
                    }

                    if (_built) return;

                    try
                    {
                        // Its own time counts toward the build's longest frame: the checks, one material pair
                        // per floor and the registration all run in this frame, before any pacing starts.
                        var start = Stopwatch.StartNew();
                        BeginBuild();
                        _longestFrameMs = Math.Max(_longestFrameMs, start.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        // A throw in the build leaves an empty viewport, which is the one outcome worse
                        // than 2D - so this one refuses the mesh rather than just going quiet.
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                            $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");

                        Refuse("could not be turned into meshes");
                    }

                    return;
                }

                // The workers preparing the floors: nothing to do until every one has landed - the backdrop shows.
                if (_held.Count > 0 && !_prepDone)
                {
                    for (var i = 0; i < _held.Count; i++)
                    {
                        var task = _held[i].Prep.Task;
                        if (!task.IsCompleted) return;

                        // Cancelled under us (a drop while this view waited): that level is asked for again.
                        if (WasCancelled(task))
                        {
                            // Cancelled by a cache DROP (the shared key is gone or another map's): every caller of
                            // DropCaches destroys this view first, so asking again would start a full-floor job
                            // for a view on its way out, keyed "|level" off the null key (review F27). Stopped.
                            if (_builtKey != _viewBuildKey)
                            {
                                _broke = true;
                                return;
                            }

                            var level = _held[i].Level;
                            ReleasePrep(_held[i].Prep);
                            _held[i] = (level, AcquirePrep(PrepKey(level), level, SnapshotPrep()));
                            return;
                        }

                        if (task.IsFaulted)
                        {
                            var reason = task.Exception?.GetBaseException();

                            Plugin.LogSource?.LogWarning(
                                $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                                $"({reason?.GetType().Name}: {reason?.Message}) - drawing the flat picture instead.");

                            Refuse("could not be turned into meshes");
                            return;
                        }
                    }

                    var prepared = new List<FloorData>();
                    foreach (var held in _held) prepared.Add(held.Prep.Task.Result);

                    // Collected, but STILL HELD until the build finishes: a repaint during the paced upload (which
                    // on a big map takes a second or two) then finds the finished job and only re-uploads from its
                    // arrays, instead of starting the worker again. Let go in Finish, or in ResetPipeline.
                    AfterPrepare(prepared);
                    if (_broke) return;
                }

                // Before anything is drawn this frame, so no mesh queued below belongs to the build being thrown
                // away. See RebuildWithoutFailedSides.
                if (_sideFailed) RebuildWithoutFailedSides();
                if (_broke) return;

                // Uploads, cuts and wall builds, paced - while the first build is on, and after it for walls
                // that arrive late.
                if (!_ready || _work.Count > 0 || _wallJobs.Count > 0) Pump();

                if (_broke || !_ready) return;
                if (_camera == null || _floors.Count == 0) return;

                var first = _measureFirstFrame;
                var clock = first ? Stopwatch.StartNew() : null;
                _drawCalls = 0;

                EnsureRenderTexture();
                Place();

                for (var i = 0; i < _floors.Count; i++) Draw(_floors[i]);

                RenderNow();
                PlaceOverlays();

                if (first)
                {
                    // The CPU side of one frame: the DrawMesh submissions and the manual Render. What a map of
                    // this size costs to look at, frame after frame - the number that says whether it holds 60.
                    _measureFirstFrame = false;

                    Plugin.LogSource?.LogInfo(string.Format(
                        CultureInfo.InvariantCulture,
                        "QuestTree: 3D map for {0} - first frame drawn in {1:0.0} ms, render {2:0.0} ms, {3} draw call(s), cut {4}.",
                        _mapKey, clock.Elapsed.TotalMilliseconds, _renderMs, _drawCalls, CutText()));
                }
            }
            catch (Exception ex)
            {
                // Once. A throw here would otherwise be a console line sixty times a second, and the
                // view goes quiet rather than noisy: the last frame stays in the texture.
                _broke = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view for '{_mapKey}' stopped drawing " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>One DrawMesh for our camera, counted. Every mesh this view draws goes through here, so the
        /// first-frame line's draw-call count is the real one.</summary>
        private void Submit(Mesh mesh, Material material)
        {
            Graphics.DrawMesh(mesh, Matrix4x4.identity, material, _drawLayer, _camera);
            _drawCalls++;
        }

        /// <summary>Queues one floor's meshes for our camera, with the floor's picture on them - the
        /// relief through the clipping material, the buildings through the opaque one.</summary>
        private void Draw(Floor floor)
        {
            var ground = floor.GroundMaterial;
            var walls = floor.BuildingMaterial;

            if (ground == null || walls == null) return;

            // Asked for every frame, and cheap: a resident sprite is a dictionary-free list touch. It
            // has to be asked, because the picture cache can release a floor's texture under us - and a
            // material whose mainTexture has been destroyed draws white, not the last picture. Both
            // materials are checked, since either can be the one holding the destroyed reference.
            if (!_flatColours && (ground.mainTexture == null || walls.mainTexture == null) &&
                floor.Layer != null && floor.Layer.TryGetSprite(out var sprite) &&
                sprite != null && sprite.texture != null)
            {
                ground.mainTexture = sprite.texture;
                walls.mainTexture = sprite.texture;
            }

            // NOT DRAWN until its picture is on it. A textured shader with a null _MainTex samples white,
            // so a peeled lower storey whose PNG is still being decoded would draw as a blank white slab
            // under the floor being looked at - which reads as the map having gone wrong, where a floor
            // that appears a moment later reads as a floor that appeared a moment later. Asked for above,
            // so the frame that has it draws it.
            if (!_flatColours && ground.mainTexture == null) return;

            var meshes = floor.Meshes;
            if (meshes == null) return;

            // The first frame this floor's picture is here: its walls can be coloured now - started here,
            // prepared on a worker and uploaded paced (StartWalls), so not a per-frame cost either.
            if (meshes.WallsPending && !meshes.WallsRunning) StartWalls(meshes, floor.Level, ground.mainTexture, late: true);

            for (var i = 0; i < meshes.Ground.Count; i++)
            {
                var mesh = meshes.Ground[i];
                if (mesh != null) Submit(mesh, ground);
            }

            // Every mesh is drawn whole: the dollhouse cut is the camera's oblique near plane (ApplyCut), which
            // clips each triangle on the GPU at the cut height. The peel still leaves out every band over the
            // chosen floor.
            for (var i = 0; i < meshes.Buildings.Count; i++)
            {
                var mesh = meshes.Buildings[i];
                if (mesh != null) Submit(mesh, walls);
            }

            // Roofs standing on another floor, with THAT floor's building material - its picture. A floor
            // this view does not draw (it is above the chosen one) has its faces above the cut anyway; if it
            // is not here they fall back to this floor's material. A floor whose picture has not arrived yet
            // is skipped this frame rather than drawn white, as the floors themselves are.
            for (var i = 0; i < meshes.RoofsOnOtherFloors.Count; i++)
            {
                var roof = meshes.RoofsOnOtherFloors[i];
                // An owner above the chosen floor is not drawn: a SLOPED face routed there by its centroid can still
                // reach below the cut, and that sliver takes the chosen floor's picture - the one at the cut - not
                // this filing band's (review F28).
                var owner = FloorAt(roof.Level) ?? FloorAt(_selectedLevel);
                var material = owner != null && owner.BuildingMaterial != null ? owner.BuildingMaterial : walls;

                if (material == null || (!_flatColours && material.mainTexture == null)) continue;

                var mesh = roof.Mesh;
                if (mesh != null) Submit(mesh, material);
            }

            // The walls, one colour at a time: at most sixteen more DrawMesh calls per floor. A tint with
            // no material of its own (a shader with nothing to tint) draws with the buildings' material.
            for (var t = 0; t < meshes.Walls.Count; t++)
            {
                var tint = meshes.Walls[t];
                if (tint == null) continue;

                var material = tint.Material != null ? tint.Material : walls;

                for (var i = 0; i < tint.Meshes.Count; i++)
                {
                    var mesh = tint.Meshes[i];
                    if (mesh != null) Submit(mesh, material);
                }
            }

            // The faces the game's own materials texture: one DrawMesh per (tile, mesh chunk) - one per material a
            // band uses, on a map of ~300 materials about 300 calls. A tile not cut yet draws in the floor's wall
            // colour rather than leaving holes; a page that FAILED is dropped and the view rebuilt without it
            // (RebuildWithoutFailedSides), its faces going back to the U/V rule.
            if (AtlasActive)
            {
                var tiles = _heldTiles;

                for (var g = 0; g < meshes.Atlas.Count; g++)
                {
                    var atlas = meshes.Atlas[g];
                    var material = atlas?.Material;
                    if (material == null) continue;

                    // The tile's texture, once the store has cut it (a few a frame); until then, and for good if it
                    // failed, the floor's wall colour. Unity's null covers a store destroyed under a cached entry.
                    if (material.mainTexture == null && tiles != null)
                    {
                        var texture = tiles.TextureOf(atlas.Tile);
                        if (texture != null) material.mainTexture = texture;
                    }

                    // A whole PAGE that failed (unreadable, will not decode, too big): the view is rebuilt without it,
                    // its faces going back to the U/V rule - as a failed side picture's do.
                    if (tiles != null && tiles.PageFailedFor(atlas.Tile)) _sideFailed = true;

                    var draw = material.mainTexture != null ? material : SideFallbackFor(meshes, floor.Level, walls);

                    for (var i = 0; i < atlas.Meshes.Count; i++)
                    {
                        var mesh = atlas.Meshes[i];
                        if (mesh != null) Submit(mesh, draw);
                    }
                }
            }

            // The faces the side pictures texture: at most four more DrawMesh calls per floor. Each side's
            // picture is fetched from THIS view's entry whenever the material has lost it (the picture
            // cache can evict a side like a floor), and a side whose picture is not here yet is skipped
            // rather than drawn white - its faces appear the frame it arrives, as a peeled floor does.
            if (!SidesActive) return;

            for (var slot = 0; slot < meshes.Sides.Length; slot++)
            {
                var side = meshes.Sides[slot];
                var material = side?.Material;
                if (material == null) continue;

                var picture = _sides[slot]?.Picture;

                if (material.mainTexture == null && picture != null && picture.TryGetSprite(out var sideSprite) &&
                    sideSprite != null && sideSprite.texture != null)
                {
                    material.mainTexture = sideSprite.texture;
                }

                // A side whose picture has FAILED to decode will never have one: noted, and the view is
                // rebuilt without that side at the top of the next frame (not mid-draw - see LateUpdate), so
                // its faces go back to the top picture or a tint as if the side had never been captured.
                if (picture != null && picture.ArtworkFailed) _sideFailed = true;

                // No picture on it (decoding, evicted, or failed): drawn in the floor's wall colour rather
                // than skipped. A skipped face is a hole straight through the building.
                var draw = material.mainTexture != null ? material : SideFallbackFor(meshes, floor.Level, walls);

                for (var i = 0; i < side.Meshes.Count; i++)
                {
                    var mesh = side.Meshes[i];
                    if (mesh != null) Submit(mesh, draw);
                }
            }
        }

        /// <summary>The material a side's faces are drawn with while the side has no picture: the entry's
        /// untextured wall-coloured material, made on first need; the floor's building material if the shader
        /// has nothing to colour with.</summary>
        private Material SideFallbackFor(Built built, int level, Material buildings)
        {
            if (built.SideFallback == null)
                built.SideFallback = MakeTintMaterial($"QuestTreeMap3D-sidefallback-{level}", built.WallAverage);

            return built.SideFallback != null ? built.SideFallback : buildings;
        }

        /// <summary>Set by <see cref="Draw"/> when a side's picture has failed to decode; consumed at the top
        /// of the next frame by <see cref="RebuildWithoutFailedSides"/>.</summary>
        private bool _sideFailed;

        /// <summary>
        /// Rebuilds this view's geometry without every side whose picture has FAILED - not one still loading,
        /// which gets the fallback colour until it arrives.
        ///
        /// The faces a failed side would have textured are then classified again among the sides that are
        /// left: they go to another side that sees them well enough, or to the top picture, or to a tint -
        /// the same answer a capture without that side would have given. The cache key carries the sides in
        /// use, so the entries built WITH the failed side are dropped (or orphaned, if another live view
        /// still draws them) and nothing reuses them.
        /// </summary>
        private void RebuildWithoutFailedSides()
        {
            _sideFailed = false;

            var droppedSides = "";
            var droppedPages = "";

            for (var slot = 0; slot < _sides.Length; slot++)
            {
                var picture = _sides[slot]?.Picture;
                if (picture == null || !picture.ArtworkFailed) continue;

                droppedSides += SideOrder[slot];
                _sides[slot] = null;
            }

            for (var page = 0; page < _pages.Length; page++)
            {
                if (_pages[page] == null || _heldTiles == null || !_heldTiles.PageFailed(page)) continue;

                droppedPages += (droppedPages.Length > 0 ? "," : "") + page.ToString(CultureInfo.InvariantCulture);
                _pages[page] = null;
            }

            // "sides NE, atlas pages 3" - two lists, not the letters and the page numbers run together.
            var dropped = droppedSides.Length > 0 ? "sides " + droppedSides : "";
            if (droppedPages.Length > 0) dropped += (dropped.Length > 0 ? ", " : "") + "atlas pages " + droppedPages;

            if (dropped.Length == 0 || _loaded == null) return;

            CountSides();
            CountPages();

            // The room for the dropped sides goes back; BeginBuild takes what the rest need straight away, so
            // nothing is evicted in between.
            ReturnSideRoom(evict: false);

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the picture(s) {dropped} of the 3D map for '{_mapKey}' could not be decoded - the " +
                $"buildings are rebuilt without them.");

            ReleaseFloors();
            BeginBuild();
        }

        /// <summary>
        /// The one render: our light on, the scene's fog off, the camera rendered by hand, and both put
        /// back by the statement that changed them. The finally is the whole safety of this design - a
        /// directional light left enabled here is a directional light in the player's hideout, and fog
        /// left off is the menu's own background flattened.
        /// </summary>
        private void RenderNow()
        {
            var fog = RenderSettings.fog;
            var oblique = false;

            try
            {
                RenderSettings.fog = false;
                _light.enabled = true;

                oblique = ApplyCut();

                var clock = _timeRender ? Stopwatch.StartNew() : null;
                _camera.Render();

                if (clock != null)
                {
                    _renderMs = clock.Elapsed.TotalMilliseconds;
                    _timeRender = false;
                }
            }
            finally
            {
                // FOG FIRST. Both matter, and if one of them is going to be skipped by a throw it must
                // not be the global: a light left on is ours to find on our own object, while fog left
                // off is the menu's own scene changed under the player for the rest of the session. Each
                // is guarded separately for the same reason - one throwing must not skip the other.
                try { RenderSettings.fog = fog; } catch (Exception) { /* nothing further to try */ }
                try { if (_light != null) _light.enabled = false; } catch (Exception) { /* as above */ }

                // The oblique projection lives ONLY inside this bracket: every other reader of the camera
                // (TryProject, PanBy, the next ApplyCut) sees its own perspective, recomputed from the field of
                // view, the aspect and the clip planes.
                try { if (oblique && _camera != null) _camera.ResetProjectionMatrix(); } catch (Exception) { /* as above */ }
            }
        }

        /// <summary>Time the next render, for the first-frame line. Set by <see cref="Finish"/> and whenever the
        /// cut height changes, so the first frame after a floor switch is timed too.</summary>
        private bool _timeRender;

        /// <summary>The last timed render, in milliseconds.</summary>
        private double _renderMs;

        /// <summary>Puts the camera where the orbit says, looking at the focus point on the ground.</summary>
        private void Place()
        {
            if (_camera == null) return;

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var target = new Vector3(_focus.x, GroundAt(_focus.x, _focus.y), _focus.y);

            // The oblique cut needs the camera above it (see ApplyCut): dolly out until it is at least
            // MinCameraAboveCut over the cut, never past the furthest the dolly goes. sin(pitch) >= sin 15.
            // Only while the cut is the near plane (PART-03 review): the CutNone rollback draws uncut and must
            // not dolly. The pushed-out distance is kept, as any dolly is - it is the view's own state.
            if (CutMode == CutByNearPlane && !float.IsNaN(_cutY))
            {
                var sin = Mathf.Sin(_pitch * Mathf.Deg2Rad);
                var need = (_cutY + MinCameraAboveCut - target.y) / sin;
                if (need > _distance) _distance = Mathf.Min(need, MaxDistance());
            }

            _camera.transform.position = target + rotation * new Vector3(0f, 0f, -_distance);
            _camera.transform.rotation = rotation;
        }

        /// <summary>The ground height at a map point: the chosen floor's relief where it has one, else
        /// the floor's own band bottom. Never NaN - a NaN in a camera position loses the whole
        /// view.</summary>
        private float GroundAt(float x, float z)
        {
            if (_groundBand != null && _groundBand.TryHeightAt(x, z, out var y)) return y;

            return _groundFallbackY;
        }

        /// <summary>What the ground is taken to be where the relief has a hole: the selected floor's
        /// declared band bottom, which is the height the flat map already files its markers under, else
        /// the file's own low end.</summary>
        private float FallbackGroundY()
        {
            var layer = ResolveLayer();

            // Not the "any height" placeholders (about -1003 m from the extent probe, -2000 m from the catalog):
            // the same test MeasureFloorRanges makes, or the camera and pins went a kilometre under the map (F22).
            if (layer != null && layer.GameBounds.Count > 0)
            {
                var low = layer.GameBounds[0].Min.z;
                if (low > -1000f && low < 1000f) return low;
            }

            return _file != null && !float.IsNaN(_file.YMin) ? _file.YMin : 0f;
        }

        // --- the overlays ---------------------------------------------------------------------------

        /// <summary>Records an overlay and where on the map it stands. Its scale is set to one and left
        /// there: the container it is in is at scale one in 3D, so the pixel sizes the builders use are
        /// already screen pixels and there is nothing to counter-scale.</summary>
        /// <param name="child">The overlay's rect, placed at its map coordinates.</param>
        public void KeepConstantScale(RectTransform child)
        {
            if (child == null) return;

            child.localScale = Vector3.one;
            _overlays.Add((child, child.anchoredPosition));

            // PARKED on the spot. Its anchoredPosition is raw map metres - the position the flat view
            // reads through a scaled container - and this container is at scale one, so left alone it
            // would draw at a thousand canvas units from the centre for the frames between here and the
            // mesh landing. Every overlay stays parked until a real projection places it.
            child.anchoredPosition = Parked;
        }

        /// <summary>
        /// Every overlay, moved to where its map position is on screen this frame.
        ///
        /// The container the overlays are in is anchored at the viewport's centre, pivoted at its centre
        /// and at scale one, so a child's anchoredPosition IS its offset from the middle of the viewport
        /// in canvas units - which is what makes this two multiplications rather than a rect
        /// conversion.
        ///
        /// Parked rather than placed when the point is behind the camera (where the projection flips and
        /// a pin would appear mirrored on the far side of the map) or outside the viewport by more than
        /// <see cref="OverlayMargin"/> - a marker the mask would clip anyway. See
        /// <see cref="TryProject"/>, which both this and <see cref="Project"/> go through.
        /// </summary>
        private void PlaceOverlays()
        {
            if (_overlays.Count == 0) return;

            for (var i = 0; i < _overlays.Count; i++)
            {
                var rect = _overlays[i].Rect;

                // Unity's null: the viewport can be destroyed a frame before this component is.
                if (rect == null) continue;

                var onScreen = TryProject(_overlays[i].Map, out var local, out var inside) && inside;

                // PARKED far outside the viewport rather than deactivated, and that is deliberate:
                // SetActive on these rects belongs to LabelCull, which switches a zone name off when it
                // would collide with an extract's. A frame loop that also wrote activeSelf would turn
                // every culled name back on the moment it came on screen, and the cull would be dead in
                // 3D. The viewport's RectMask2D culls a parked rect from rendering, which is the cost
                // this was for.
                rect.anchoredPosition = onScreen ? local : Parked;
            }
        }

        // --- the gestures ---------------------------------------------------------------------------

        /// <summary>Turns and tilts the view. Yaw is free, pitch is clamped short of straight down so
        /// the camera never looks along its own up vector, where the rotation is undefined.</summary>
        /// <param name="yaw">Degrees to turn.</param>
        /// <param name="pitch">Degrees to tilt.</param>
        internal void Orbit(float yaw, float pitch)
        {
            _yaw += yaw;
            _pitch = Mathf.Clamp(_pitch + pitch, MinPitch, MaxPitch);

            Moved();
        }

        /// <summary>
        /// Drags the focus point across the ground, so the map follows the cursor.
        ///
        /// The conversion is screen pixels -> world metres at the focus depth: the viewport spans
        /// <c>2 d tan(fov/2)</c> metres vertically at that depth, over its own height in pixels. The
        /// vertical half is then divided by the sine of the pitch, because the ground is TILTED away
        /// from the screen: at 20 degrees a pixel of screen height is nearly three metres of ground, and
        /// without the correction a drag at a low tilt moves the map a third as far as the cursor.
        /// </summary>
        /// <param name="delta">The drag, in screen pixels.</param>
        internal void PanBy(Vector2 delta)
        {
            if (_camera == null) return;

            var pixels = _viewport.rect.height * CanvasScale();
            if (!(pixels > 0f)) return;

            var metresPerPixel = 2f * _distance * Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) / pixels;

            var right = _camera.transform.right;
            right.y = 0f;
            right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;

            var forward = _camera.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;

            var tilt = Mathf.Max(0.26f, Mathf.Sin(_pitch * Mathf.Deg2Rad));

            // MINUS: the map follows the cursor, so the camera goes the other way.
            var move = -right * (delta.x * metresPerPixel) -
                       forward * (delta.y * metresPerPixel / tilt);

            _focus = ClampFocus(_focus + new Vector2(move.x, move.z));

            Moved();
        }

        /// <summary>Comes closer or goes further, clamped so the map can neither be lost behind the
        /// camera nor followed out past the far plane.</summary>
        /// <param name="notches">The wheel delta.</param>
        internal void Dolly(float notches)
        {
            _distance = Mathf.Clamp(
                _distance * (1f - DollyPerNotch * notches), MinDistance, MaxDistance());

            Moved();
        }

        /// <summary>Moves the focus to a map point, keeping the current distance and tilt - the 3D
        /// answer to "fly to this quest's pin". The requested scale is ignored on purpose: in 3D it
        /// would be a distance, and jumping the player's zoom as well as their position on a row click
        /// is two surprises where one was asked for.</summary>
        /// <param name="contentPoint">The point, in map coordinates.</param>
        /// <param name="scale">Ignored. See above.</param>
        public void FocusOn(Vector2 contentPoint, float scale)
        {
            _focus = ClampFocus(contentPoint);

            Moved();
        }

        /// <summary>
        /// Keeps the focus point over the map: within the floor's rectangle plus a fifth of its size on
        /// every side. Without a limit a long drag slides the whole map off the screen with nothing to
        /// drag back, and a pan's speed scales with the distance, so at the far dolly limit one flick is
        /// kilometres.
        /// </summary>
        /// <param name="focus">The focus wanted, in map coordinates.</param>
        private Vector2 ClampFocus(Vector2 focus)
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return focus;

            var margin = layer.BoundsSize * FocusMargin;

            return new Vector2(
                Mathf.Clamp(focus.x, layer.BoundsMin.x - margin.x, layer.BoundsMax.x + margin.x),
                Mathf.Clamp(focus.y, layer.BoundsMin.y - margin.y, layer.BoundsMax.y + margin.y));
        }

        /// <summary>How far past the floor's rectangle the focus may go, as a fraction of its size.</summary>
        private const float FocusMargin = 0.2f;

        /// <summary>Re-places the camera and tells whoever is listening the view has moved.</summary>
        private void Moved()
        {
            Place();

            unchecked { ViewVersion++; }

            // Zero for the pan: it means nothing here, and no subscriber reads it - MapView keeps the
            // 3D view's own State instead. See IOverlayHost.
            OnViewChanged?.Invoke(Scale, Vector2.zero);
        }

        /// <summary>The furthest the dolly goes: one and a half times the extent's diagonal, which frames
        /// the whole map from outside it whatever the tilt.</summary>
        private float MaxDistance()
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return FarClip * 0.5f;

            var size = layer.BoundsSize;

            return Mathf.Min(FarClip * 0.5f, Mathf.Max(MinDistance + 1f, size.magnitude * MaxDistanceOfDiagonal));
        }

        /// <summary>The distance the whole floor just fits at, at the opening tilt: the greater of what
        /// the extent's width needs across the screen and what its depth needs up it, the depth
        /// foreshortened by the tilt, with a tenth of margin.</summary>
        private float FitDistance()
        {
            var layer = ResolveLayer();
            if (layer == null || !layer.HasBounds) return MinDistance * 4f;

            var size = layer.BoundsSize;
            var tangent = Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad);
            var aspect = _viewport != null && _viewport.rect.height > 0f
                ? Mathf.Max(0.2f, _viewport.rect.width / _viewport.rect.height)
                : 1.6f;

            var forDepth = size.y * Mathf.Sin(_pitch * Mathf.Deg2Rad) * 0.5f / Mathf.Max(0.01f, tangent);
            var forWidth = size.x * 0.5f / Mathf.Max(0.01f, tangent * aspect);

            return Mathf.Clamp(Mathf.Max(forDepth, forWidth) * 1.1f, MinDistance, MaxDistance());
        }

        /// <summary>The canvas scale factor, for turning screen pixels into canvas units and back. The
        /// canvas is found once and kept: this is read every frame by the render-texture sizing, and a
        /// parent walk per frame is the mistake PanZoomHandler.ResolveEventCamera already had to fix.</summary>
        private float CanvasScale()
        {
            if (_canvas == null && _viewport != null) _canvas = _viewport.GetComponentInParent<Canvas>();

            return _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        }

        private Canvas _canvas;

        /// <summary>The layer of the floor showing, which is what the extent, the fit and the fallback
        /// ground height are read from.</summary>
        private DynamicMapsLibrary.MapLayer ResolveLayer() => LayerOf(_selectedLevel) ?? _entry?.DefaultLayer;

        /// <summary>The map layer with this level, or null.</summary>
        /// <param name="level">The floor level.</param>
        private DynamicMapsLibrary.MapLayer LayerOf(int level)
        {
            if (_entry == null) return null;

            foreach (var layer in _entry.Layers)
                if (layer != null && layer.Level == level) return layer;

            return null;
        }

        // --- the end ---------------------------------------------------------------------------------

        /// <summary>
        /// Stops this view for good, from MapView.DiscardViewport just before it destroys the viewport. Destroy is
        /// deferred to the end of the frame, so the view still got one LateUpdate - and in the paced upload that
        /// was up to <see cref="FrameBudgetMs"/> spent uploading into an entry the next view throws away (review
        /// F23). Release still runs from OnDestroy and frees everything as before.
        /// </summary>
        internal void Abandon() => _broke = true;

        /// <summary>Says once that this map's mesh is not usable, and stops. The caller drops the mesh
        /// for the session and repaints into the flat picture.</summary>
        /// <param name="reason">A few words for the toggle's tooltip, written to follow "it was not used:
        /// ", so "the file has no ground in it" rather than "no ground".</param>
        private void Refuse(string reason = "")
        {
            // Before the callback, and before anything else can throw: what LateUpdate tests first.
            _broke = true;

            if (!string.IsNullOrEmpty(reason)) _refusal = reason;

            var refused = _onRefused;
            _onRefused = null;

            // The overlays are about to be the only thing in the viewport, for the one frame between
            // here and the repaint that redraws them flat. Parked, or they would flash at raw map metres.
            Park();

            // This session's layer choice goes with any failure: it is the one piece of cached state a
            // failed view could have been caused by, and one extra scan is a cheap way to be sure the
            // next attempt is not refused for a stale reason.
            _sessionLayer = -1;

            Release();

            try
            {
                refused?.Invoke(_meshPath, _refusal);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: could not report the refused 3D relief ({ex.Message}).");
            }
        }

        /// <summary>Puts every registered overlay back off screen. For the frame between a refusal and
        /// the repaint that draws the map flat, where this view no longer projects anything.</summary>
        private void Park()
        {
            for (var i = 0; i < _overlays.Count; i++)
            {
                var rect = _overlays[i].Rect;
                if (rect != null) rect.anchoredPosition = Parked;
            }
        }

        /// <summary>Stops rendering while the viewport is inactive - a visit to the tree tab leaves this
        /// viewport in the hierarchy, switched off, and a view that kept rendering there would be
        /// burning a camera render a frame on a picture nobody is looking at.</summary>
        private void OnDisable()
        {
            if (_light != null) _light.enabled = false;

            // A viewport switched off (a visit to the tree tab) holds no pictures anyone is looking at, so
            // the cache room for its sides goes back while it is off and is taken again when it returns.
            ReturnSideRoom();
        }

        private void OnEnable()
        {
            // Unity calls this on AddComponent too, before anything is known - nothing is taken then, since
            // the sides are counted, and the shader resolved, only later. After a return it retakes the room.
            if (_built && !_broke) TakeSideRoom();
        }

        private void OnDestroy()
        {
            Release();
        }

        /// <summary>
        /// Everything this view made, destroyed. The order matters: the camera stops pointing at the
        /// texture before the texture is released, or Unity renders one more frame - LateUpdate runs
        /// again in the frame a Destroy is requested in - into freed memory, and a camera whose
        /// targetTexture is null renders to the BACKBUFFER, over the player's menu.
        ///
        /// Idempotent, because it is reached from three places: a refused mesh, OnDestroy, and a failed
        /// Attach.
        /// </summary>
        private void Release()
        {
            _broke = true;

            if (_cutSkippedFrames > 0 && !_cutSkipLogged)
            {
                _cutSkipLogged = true;
                Plugin.LogSource?.LogInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "QuestTree: 3D map drew {0} frame(s) uncut - the camera was not above the cut at {1:0.0} m.",
                    _cutSkippedFrames, _cutY));
            }

            // The picture-cache room reserved for this view's side pictures goes back first, whatever
            // else fails below: a reservation outliving its view raises the ceiling for good.
            ReturnSideRoom();

            try
            {
                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.targetTexture = null;
                }

                if (_light != null) _light.enabled = false;
                if (_image != null) _image.texture = null;
                if (_rt != null) _rt.Release();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the 3D map view could not be quietened ({ex.Message}).");
            }

            // Each in its own guard, in a finally-shaped sequence: one refusal must not leave the rest
            // of a camera, a light and two hundred meshes in the menu.
            ReleaseFloors();

            _overlays.Clear();
            _groundBand = null;

            Discard(_rt);
            Discard(_cameraGo);
            Discard(_lightGo);
            Discard(_image != null ? _image.gameObject : null);

            _rt = null;
            _camera = null;
            _cameraGo = null;
            _light = null;
            _lightGo = null;
            _image = null;
        }

        /// <summary>Lets go of this view's floors: their materials destroyed, their cached geometry released
        /// (see <see cref="Unuse"/>). Half of <see cref="Release"/>, and all of what a rebuild throws away.</summary>
        private void ReleaseFloors()
        {
            // First: queued units and wall jobs point at these floors' entries, and must not run after them.
            ResetPipeline();

            // The tiles are let go like the meshes: kept for the next view of this file, destroyed only when a drop
            // has orphaned the store and this was its last user.
            ReleaseTiles();

            foreach (var floor in _floors)
            {
                if (floor == null) continue;

                // The MESHES are let go, not destroyed. They belong to the static cache and outlive this
                // view by design, and destroying them here is the bug that would make the cache worse
                // than no cache - the next view would find a dictionary full of destroyed Mesh references
                // and draw nothing. Unuse destroys them only when a drop has already taken them out of
                // the cache and this was the last view drawing them. See Built.
                Unuse(floor.Meshes);
                floor.Meshes = null;

                // The texture is NOT ours - it belongs to the picture cache, which hands the same
                // Texture2D to the flat view's Image. Destroying the material never destroys what was
                // assigned to it, which is exactly what is wanted here.
                Discard(floor.GroundMaterial);
                Discard(floor.BuildingMaterial);

                floor.GroundMaterial = null;
                floor.BuildingMaterial = null;
            }

            _floors.Clear();
        }

        /// <summary>One Destroy that cannot take the rest of the teardown with it.</summary>
        private static void Discard(UnityEngine.Object thing)
        {
            if (thing == null) return;

            try
            {
                Destroy(thing);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D map view could not destroy a {thing.GetType().Name} ({ex.Message}).");
            }
        }
    }
}
