using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    ///     again in a finally. A per-light culling mask is honoured in forward rendering and is not a
    ///     guarantee under the deferred path this game uses, so the mask is not the safeguard: the light
    ///     being off outside those two statements is;
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
        private const int MaxVerticesPerMesh = 1_000_000;

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
            /// The dollhouse cut: for each cut height, each building mesh of this entry clipped to what lies
            /// below it (null when nothing does). Made the first time a mesh is drawn under that cut and kept
            /// for as long as the entry - a map has a handful of floors, and stepping back to one already
            /// seen costs nothing. Keyed by the HEIGHT and not the floor level, because the height comes from
            /// the meta and an entry reused across a rescan could otherwise serve a cut made at the old one.
            /// Destroyed with everything else by <see cref="DestroyBuilt"/>.
            /// </summary>
            public readonly Dictionary<float, Dictionary<Mesh, Mesh>> Cuts = new Dictionary<float, Dictionary<Mesh, Mesh>>();

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
            if (!SidesActive || _reservedSides > 0) return;

            DynamicMapsLibrary.ReserveSprites(_sideCount);
            _reservedSides = _sideCount;
        }

        /// <summary>Gives back whatever room this view holds. Idempotent.</summary>
        private void ReturnSideRoom()
        {
            if (_reservedSides <= 0) return;

            DynamicMapsLibrary.ReserveSprites(-_reservedSides);
            _reservedSides = 0;
        }

        /// <summary>Whether the side pictures take part at all: they need a textured shader, so under the
        /// flat-colour fallback every wall is a tint, exactly as if the capture had no sides.</summary>
        private bool SidesActive => _sideCount > 0 && !_flatColours;

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

        /// <summary>The read that <see cref="BuildMeshes"/> last built from, kept for a rebuild.</summary>
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

            // The side pictures this capture has, by slot, and the room for them in the picture cache.
            view.TakeSides();

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
            _loading = Task.Run(() => ReadFile(path));

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
        private static Loaded ReadFile(string path)
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;

            lock (CacheLock)
            {
                if (_cachedFile != null && _cachedPath == path && _cachedStamp == stamp)
                    return new Loaded { File = _cachedFile, Stamp = stamp };
            }

            int generation;
            lock (CacheLock) generation = _cacheGeneration;

            var parsed = MapMeshFile.Read(File.ReadAllBytes(path));

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

            foreach (var cut in built.Cuts.Values)
            {
                foreach (var pair in cut)
                {
                    // A "self" entry - a mesh wholly below the cut is its own cut - is the source mesh,
                    // destroyed once above with the list it belongs to. Destroying it here too would be the
                    // double destroy the self entry exists to avoid.
                    if (pair.Value == null || ReferenceEquals(pair.Key, pair.Value)) continue;
                    Discard(pair.Value);
                    count++;
                }
            }

            built.Cuts.Clear();

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

                for (var i = 0; i < tint.Meshes.Count; i++) { Discard(tint.Meshes[i]); count++; }

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

        /// <summary>Turns the parsed file into meshes, or refuses it. Runs once, on the main thread, in
        /// the first LateUpdate after the read lands.</summary>
        private void BuildMeshes()
        {
            _built = true;

            var clock = Stopwatch.StartNew();

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

            var levels = DrawnLevels();

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

            // Kept for the walls, which can be built on a later frame than the rest - see TryBuildWalls.
            _buildingShader = shader;
            _flatColours = flat;

            // Now, and not at Attach: whether the sides take part depends on the shader just resolved.
            TakeSideRoom();

            // The built meshes belong to a (file, write time), and anything cached under another one is
            // the geometry of a map that has been replaced. The sides are in the key because they decide
            // which faces are textured and which are tinted: the same mesh classified against four sides
            // and against none is two different builds. HERE and not earlier, because whether the sides
            // take part at all depends on the shader just resolved (SidesActive reads _flatColours).
            var key = _meshPath + "|" + loaded.Stamp.ToString(CultureInfo.InvariantCulture) + "|" +
                      (SidesActive ? _sidesKey : "-");

            if (_builtKey != key)
            {
                DropBuiltMeshes();
                _builtKey = key;
            }

            _groundShader = ResolveGroundShader(shader, shaderName, out _groundCutout, out var cutoutNote);

            for (var i = 0; i < levels.Count; i++) BuildFloor(levels[i], shader, flat);

            // The check that can fail, and the one an empty or garbage file gets caught by: a view with
            // no triangles in it is a viewport with the markers of a map floating over a flat colour,
            // which looks like a bug in the markers rather than in the mesh.
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
                    $"QuestTree: the 3D relief of '{_mapKey}' has {levels.Count} band(s) but no picture for " +
                    $"any of them - drawing the flat picture instead.");
                Refuse("no band of it has a picture");
                return;
            }

            if (drawable == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D relief of '{_mapKey}' has {levels.Count} band(s) but not one " +
                    $"triangle in them - drawing the flat picture instead.");
                Refuse("there is no ground in the bands it has");
                return;
            }

            // The buildings of a level the peel does not draw are simply not built - see BuildFloor,
            // which takes the buildings whose Level is this one. A building naming a level no band has
            // is moved to the nearest band by BandLevelFor, so none is lost to a file from a future
            // builder.
            _groundBand = _file.Band(levels[levels.Count - 1]);
            _groundFallbackY = FallbackGroundY();

            _cutY = CutHeight();

            // The cut is MADE here, with the rest of the build, rather than on the first frame that draws
            // it: clipping a mall is tens of milliseconds, and a hitch that is part of opening the map is a
            // hitch the player already expects. Walls or sides built on a later frame are cut when first
            // drawn. The time is in the build line.
            _cutMillis = PrecutFloors();

            if (!_restored) _distance = FitDistance();

            Place();

            var cells = 0L;
            var groundTriangles = 0L;
            var buildings = 0;
            var buildingTriangles = 0L;
            var dropped = 0;
            var wallTriangles = 0L;
            var tints = 0;
            var wallsWaiting = 0;

            // The side pictures first, then the floors lowest to highest: the floor SHOWING ends up the most
            // recently used of everything this view holds.
            if (SidesActive)
            {
                for (var slot = 0; slot < _sides.Length; slot++) _sides[slot]?.Picture?.TryGetSprite(out _);
            }

            var topTriangles = 0L;
            var sideTriangles = 0L;

            foreach (var floor in _floors)
            {
                topTriangles += floor.Meshes.TopTriangles;
                sideTriangles += floor.Meshes.SideTriangles;
                cells += floor.Meshes.Cells;
                groundTriangles += floor.Meshes.GroundTriangles;
                buildings += floor.Meshes.BuildingCount;
                buildingTriangles += floor.Meshes.BuildingTriangles;
                dropped += floor.Meshes.Dropped;
                wallTriangles += floor.Meshes.WallTriangles;
                tints += floor.Meshes.Tints;
                if (floor.Meshes.WallsPending) wallsWaiting++;

                // Asks the picture cache for every floor the peel holds, lowest first, so the floor
                // SHOWING ends up the most recently used and is the last thing the cache would ever
                // evict. Without this only the floors whose first frame has been drawn count as used,
                // and the 2D layer's own sprite could push a lower storey out from under us.
                if (!_flatColours) floor.Layer?.TryGetSprite(out _);
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
            var faces = topTriangles + sideTriangles + wallTriangles;
            var sidesNote =
                (SidesActive
                    ? string.Format(CultureInfo.InvariantCulture, ", sides {0} ({1})",
                        _sideCount, string.Join(",", _sidesKey.ToCharArray()))
                    : ", sides 0") +
                (faces > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", faces top {0:0} % / sides {1:0} % / tint {2:0} %",
                        100d * topTriangles / faces, 100d * sideTriangles / faces, 100d * wallTriangles / faces)
                    : "");

            Plugin.LogSource?.LogInfo(string.Format(
                CultureInfo.InvariantCulture,
                "QuestTree: 3D map for {0} - {1} band(s) {2:#,##0} cells -> {3:#,##0} triangles, " +
                "{4:#,##0} buildings {5:#,##0} triangles ({11}), built in {6:#,##0} ms, layer {7}, shader {8}, " +
                "ground cutout: {9}{10}{12}{13}.",
                _mapKey, levels.Count, cells, groundTriangles, buildings, buildingTriangles,
                clock.ElapsedMilliseconds, _drawLayer,
                flat ? shaderName + " (flat colours, no picture)" : shaderName,
                cutoutNote + (_reusedFloors ? ", meshes reused" : ""),
                dropped > 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        ", dropped {0:#,##0} building triangle(s) with a vertex that is not a number", dropped)
                    : "",
                wallNote,
                sidesNote,
                float.IsNaN(_cutY)
                    ? ""
                    : string.Format(CultureInfo.InvariantCulture, ", cut at {0:0.0} m (level {1}) built in {2:#,##0} ms",
                        _cutY, _selectedLevel, _cutMillis)));

            // ANNOUNCED, not just placed. The labels were culled when the viewport was built - against the
            // camera as it stood before the mesh landed, with the ground at the fallback height - and
            // Place() above moves the camera without telling anyone. Without this the cull and the pin
            // names kept that placeholder's decisions until the player's first drag.
            Moved();
        }

        private bool _restored;

        /// <summary>The height buildings are cut at, or NaN for no cut. See <see cref="CutHeight"/>.</summary>
        private float _cutY = float.NaN;

        /// <summary>How long the build spent cutting, for the log line.</summary>
        private long _cutMillis;

        /// <summary>Cuts every building mesh this view has, now. Cached in each entry, so a floor seen before
        /// costs nothing here. Returns the milliseconds spent.</summary>
        private long PrecutFloors()
        {
            if (float.IsNaN(_cutY)) return 0L;

            var clock = Stopwatch.StartNew();

            foreach (var floor in _floors)
            {
                var built = floor?.Meshes;
                if (built == null) continue;

                for (var i = 0; i < built.Buildings.Count; i++) Under(built, built.Buildings[i], _cutY);

                foreach (var tint in built.Walls)
                    for (var i = 0; i < tint.Meshes.Count; i++) Under(built, tint.Meshes[i], _cutY);

                foreach (var side in built.Sides)
                {
                    if (side == null) continue;
                    for (var i = 0; i < side.Meshes.Count; i++) Under(built, side.Meshes[i], _cutY);
                }
            }

            return clock.ElapsedMilliseconds;
        }

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
            // there cuts nothing and would only cost the clipping.
            if (float.IsNaN(top) || float.IsInfinity(top) || top >= 1000f) return float.NaN;

            return top + CutAboveFloor;
        }

        /// <summary>
        /// The mesh to draw for a building mesh under the current cut: itself when there is no cut, else its
        /// clipped twin, made on first use and cached in the entry. Null when nothing of it is below the
        /// cut. A dictionary lookup per draw call and no allocation once made - the clipping itself happens
        /// once per mesh per cut height.
        /// </summary>
        private static Mesh Under(Built built, Mesh mesh, float cutY)
        {
            if (mesh == null || float.IsNaN(cutY)) return mesh;

            if (!built.Cuts.TryGetValue(cutY, out var cut))
            {
                cut = new Dictionary<Mesh, Mesh>();
                built.Cuts[cutY] = cut;
            }

            if (cut.TryGetValue(mesh, out var clipped)) return clipped;

            // Stored even when null - "nothing below the cut" is an answer, and without it a mesh entirely
            // above would be clipped again every frame. A mesh wholly BELOW the cut is stored as ITSELF (a
            // "self" entry): its cut is the mesh, and copying it would double its memory for nothing.
            // DestroyBuilt skips self entries so the mesh is destroyed once, with the list it lives in.
            clipped = ClipBelow(mesh, cutY);
            cut[mesh] = clipped;

            return clipped;
        }

        /// <summary>
        /// A mesh cut by the plane y = <paramref name="cutY"/>, keeping what is below it.
        ///
        /// Each triangle is clipped on its own. All three corners below: kept as it is. All above: dropped.
        /// Otherwise the plane crosses two of its edges, at points found by interpolating along each edge:
        ///   - ONE corner below (two above): the kept part is a smaller triangle - that corner and the two
        ///     crossing points. One triangle out.
        ///   - TWO corners below (one above): the kept part is a quadrilateral - the two corners and the two
        ///     crossing points - split into two triangles.
        /// The corners are taken in the triangle's own cyclic order, rotated so the odd one out comes first,
        /// and the output keeps that order - so every output triangle faces the way its source did, which
        /// matters because back faces are culled and the side pictures are chosen by facing.
        ///
        /// Crack-free across BOTH kinds of mesh. A crossing POINT is cached per geometric edge - keyed by its
        /// two endpoint positions, quantised to 0.1 mm, in a canonical order (lexicographic on the quantised
        /// position) and interpolated from the canonical low end - so every triangle that has that edge gets
        /// that point to the bit, whether it shares vertex indices with its neighbour (the roofs) or only
        /// positions (the walls and sides, whose vertices are unshared for their per-face normals). The
        /// crossing VERTEX is still made per index pair, so an unshared mesh keeps its per-face normal at the
        /// cut edge. A corner exactly on the plane counts as below.
        ///
        /// Normals are interpolated from the source's rather than recalculated, so a cut building is shaded
        /// exactly like the uncut one; UVs and vertex colours are interpolated the same way.
        /// </summary>
        /// <returns>The clipped mesh; the SOURCE itself when no vertex is above the plane; null when
        /// nothing of the source is below it.</returns>
        private static Mesh ClipBelow(Mesh source, float cutY)
        {
            // Wholly below: the bounds say so without reading a vertex back. The bounds are exact for these
            // meshes (SetTriangles computed them from the vertices), and "not above" is all that is asked.
            if (source.bounds.max.y <= cutY) return source;

            var vertices = source.vertices;
            var triangles = source.triangles;

            if (vertices == null || triangles == null || vertices.Length == 0) return null;

            var normals = source.normals;
            var uvs = source.uv;
            var colours = source.colors32;

            var hasNormals = normals != null && normals.Length == vertices.Length;
            var hasUvs = uvs != null && uvs.Length == vertices.Length;
            var hasColours = colours != null && colours.Length == vertices.Length;

            var clip = new Clipper
            {
                Source = vertices,
                Normals = hasNormals ? normals : null,
                Uvs = hasUvs ? uvs : null,
                Colours = hasColours ? colours : null,
                CutY = cutY,
                Remap = new int[vertices.Length]
            };

            for (var i = 0; i < clip.Remap.Length; i++) clip.Remap[i] = -1;

            for (var t = 0; t + 2 < triangles.Length; t += 3)
                clip.Triangle(triangles[t], triangles[t + 1], triangles[t + 2]);

            if (clip.Indices.Count == 0) return null;

            var mesh = new Mesh { name = source.name + "-cut", indexFormat = IndexFormat.UInt32 };

            mesh.SetVertices(clip.Vertices);
            if (hasNormals) mesh.SetNormals(clip.OutNormals);
            if (hasUvs) mesh.SetUVs(0, clip.OutUvs);
            if (hasColours) mesh.SetColors(clip.OutColours);
            mesh.SetTriangles(clip.Indices, 0, calculateBounds: true);

            if (!hasNormals) mesh.RecalculateNormals();

            return mesh;
        }

        /// <summary>The working state of one <see cref="ClipBelow"/>.</summary>
        private sealed class Clipper
        {
            public Vector3[] Source;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public Color32[] Colours;
            public float CutY;

            /// <summary>Source vertex -> output vertex, -1 until used. Kept corners stay shared.</summary>
            public int[] Remap;

            /// <summary>Source edge (lower index, higher index) -> its crossing point's output vertex.</summary>
            private readonly Dictionary<long, int> _crossings = new Dictionary<long, int>();

            /// <summary>Geometric edge (quantised canonical endpoints) -> its crossing POSITION. See the
            /// comment on ClipBelow: this is what makes the cut crack-free on unshared meshes.</summary>
            private readonly Dictionary<(long, long, long, long, long, long), Vector3> _points =
                new Dictionary<(long, long, long, long, long, long), Vector3>();

            /// <summary>0.1 mm - far under the file's own quantisation step, far over float noise.</summary>
            private const double PointQuantum = 1e4;

            private static (long, long, long) Quantise(Vector3 v) => (
                (long)Math.Round(v.x * PointQuantum),
                (long)Math.Round(v.y * PointQuantum),
                (long)Math.Round(v.z * PointQuantum));

            private static int Compare((long, long, long) a, (long, long, long) b)
            {
                var x = a.Item1.CompareTo(b.Item1);
                if (x != 0) return x;

                var y = a.Item2.CompareTo(b.Item2);
                return y != 0 ? y : a.Item3.CompareTo(b.Item3);
            }

            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> OutNormals = new List<Vector3>();
            public readonly List<Vector2> OutUvs = new List<Vector2>();
            public readonly List<Color32> OutColours = new List<Color32>();
            public readonly List<int> Indices = new List<int>();

            private bool Below(int i) => Source[i].y <= CutY;

            public void Triangle(int a, int b, int c)
            {
                var below = (Below(a) ? 1 : 0) + (Below(b) ? 1 : 0) + (Below(c) ? 1 : 0);

                if (below == 0) return;

                if (below == 3)
                {
                    Emit(Keep(a), Keep(b), Keep(c));
                    return;
                }

                // Rotate (a, b, c) cyclically - which keeps the winding - so that the ODD corner is first:
                // the one below when only one is, the one above when only one is.
                var oddIsBelow = below == 1;

                if (Below(b) == oddIsBelow) { var t = a; a = b; b = c; c = t; }
                else if (Below(c) == oddIsBelow) { var t = a; a = c; c = b; b = t; }

                if (oddIsBelow)
                {
                    // a below; b, c above: the triangle a, (a-b crossing), (a-c crossing).
                    Emit(Keep(a), Cross(a, b), Cross(a, c));
                }
                else
                {
                    // a above; b, c below: the quad b, c, (c-a crossing), (a-b crossing), as two triangles
                    // in the source's order b -> c -> ca -> ab.
                    var ab = Cross(a, b);
                    var ca = Cross(c, a);
                    var kb = Keep(b);
                    var kc = Keep(c);

                    Emit(kb, kc, ca);
                    Emit(kb, ca, ab);
                }
            }

            private void Emit(int x, int y, int z)
            {
                Indices.Add(x);
                Indices.Add(y);
                Indices.Add(z);
            }

            private int Keep(int i)
            {
                if (Remap[i] >= 0) return Remap[i];

                Remap[i] = Add(Source[i],
                    Normals != null ? Normals[i] : Vector3.up,
                    Uvs != null ? Uvs[i] : Vector2.zero,
                    Colours != null ? Colours[i] : new Color32(255, 255, 255, 255));

                return Remap[i];
            }

            private int Cross(int i, int j)
            {
                // The VERTEX, per index pair: reused by the triangle on the other side of a shared-index edge.
                var key = ((long)Math.Min(i, j) << 32) | (uint)Math.Max(i, j);

                if (_crossings.TryGetValue(key, out var known)) return known;

                // The canonical low end by POSITION (index as the tie-break for a degenerate edge), so every
                // triangle with this geometric edge interpolates from the same end with the same t.
                var qi = Quantise(Source[i]);
                var qj = Quantise(Source[j]);
                var order = Compare(qi, qj);

                var lo = order < 0 || (order == 0 && i < j) ? i : j;
                var hi = lo == i ? j : i;
                var qlo = lo == i ? qi : qj;
                var qhi = lo == i ? qj : qi;

                var p = Source[lo];
                var q = Source[hi];

                // One end is at or below the plane and the other strictly above, so the difference is not zero.
                var t = (CutY - p.y) / (q.y - p.y);

                // The POSITION, per geometric edge: the first triangle to cut this edge fixes it for all.
                var edge = (qlo.Item1, qlo.Item2, qlo.Item3, qhi.Item1, qhi.Item2, qhi.Item3);

                if (!_points.TryGetValue(edge, out var point))
                {
                    point = new Vector3(p.x + (q.x - p.x) * t, CutY, p.z + (q.z - p.z) * t);
                    _points[edge] = point;
                }

                var normal = Normals != null ? Vector3.Lerp(Normals[lo], Normals[hi], t) : Vector3.up;
                if (normal.sqrMagnitude > 1e-12f) normal.Normalize();

                var index = Add(
                    point,
                    normal,
                    Uvs != null ? Vector2.Lerp(Uvs[lo], Uvs[hi], t) : Vector2.zero,
                    Colours != null ? Color32.Lerp(Colours[lo], Colours[hi], t) : new Color32(255, 255, 255, 255));

                _crossings[key] = index;
                return index;
            }

            private int Add(Vector3 position, Vector3 normal, Vector2 uv, Color32 colour)
            {
                Vertices.Add(position);
                OutNormals.Add(normal);
                OutUvs.Add(uv);
                OutColours.Add(colour);
                return Vertices.Count - 1;
            }
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

        /// <summary>One floor of the peel: its two materials, and its geometry - built now, or taken
        /// from the cache when a previous view of this same file and floor already built it.</summary>
        /// <param name="level">The band level.</param>
        /// <param name="shader">The shader every floor shares.</param>
        /// <param name="flat">Whether the shader draws vertex colours instead of a texture.</param>
        private void BuildFloor(int level, Shader shader, bool flat)
        {
            var band = _file.Band(level);
            if (band == null) return;

            if (!_cachedFloors.TryGetValue(level, out var meshes) || meshes == null || !meshes.Complete)
            {
                // A half-built entry left by a build that threw: its meshes are ours to destroy (it was
                // never handed to a view, so nobody is drawing it) and it is built again from scratch.
                if (meshes != null && meshes.Users == 0) DestroyBuilt(meshes);

                // INTO THE CACHE FIRST, then built. Everything BuildGround and BuildBuildings make goes
                // straight into this entry, and Release never touches meshes - so an entry made outside
                // the cache and lost to a throw in BuildBuildings or MakeMesh would have leaked the ground
                // it had already built. In the cache, a drop finds it whatever state it is in.
                meshes = new Built { Cells = band.CellCount };
                _cachedFloors[level] = meshes;

                BuildGround(band, meshes, flat);
                BuildBuildings(level, meshes, flat);

                meshes.Complete = true;
                _reusedFloors = false;
            }

            // The walls' colours come from this floor's picture. The floor SHOWING always has its picture
            // by now (the 3D branch only runs once the flat path has it), so its walls are built here with
            // the rest; a peeled lower floor may still be decoding, and its walls are then built by the
            // first frame that has the picture (Draw). An entry reused from the cache with its walls still
            // waiting gets the same chance here.
            if (meshes.WallsPending)
            {
                Texture picture = null;
                var layer = LayerOf(level);

                if (!flat && layer != null && layer.TryGetSprite(out var sprite) && sprite != null)
                    picture = sprite.texture;

                TryBuildWalls(meshes, level, picture, late: false);
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

        /// <summary>
        /// One band's ground: a quad per cell whose four CORNERS - the four neighbouring cell centres -
        /// were all measured. A cell with no hit is a hole, and a hole is left as one rather than filled
        /// at some invented height: the relief has holes where the rays found nothing, and inventing
        /// ground there is how a map grows a floor over a pit.
        ///
        /// The vertices are the cell CENTRES, in world metres, because that is where the ray that
        /// measured them was cast. Row 0 is the extent's MinZ edge and column 0 its MinX - world order,
        /// NOT the picture's image order - so the UV rule below is the straightforward one and the
        /// picture lands the right way up. (The pictures put +z at image TOP, and a sprite's v = 0 is its
        /// bottom row, which is MinZ. The two conventions meet exactly here.)
        /// </summary>
        /// <param name="band">The band to triangulate.</param>
        /// <param name="into">The cache entry being filled in.</param>
        /// <param name="flat">Whether to write vertex colours.</param>
        private void BuildGround(MapMeshFile.ReliefBand band, Built into, bool flat)
        {
            if (band.Width < 2 || band.Height < 2) return;

            var spanX = (float)(_file.MaxX - _file.MinX);
            var spanZ = (float)(_file.MaxZ - _file.MinZ);
            if (!(spanX > 0f) || !(spanZ > 0f)) return;

            // Rows per chunk, sharing one row with the next chunk so the quads that straddle the seam
            // are still drawn. Two rows is the least that makes a quad.
            var rowsPerChunk = Mathf.Clamp(MaxVerticesPerMesh / Mathf.Max(1, band.Width), 2, band.Height);

            var map = new int[band.Width * rowsPerChunk];

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = flat ? new List<Color32>() : null;

            for (var first = 0; first < band.Height - 1; first += rowsPerChunk - 1)
            {
                var last = Mathf.Min(band.Height - 1, first + rowsPerChunk - 1);

                for (var i = 0; i < map.Length; i++) map[i] = -1;

                vertices.Clear();
                uvs.Clear();
                indices.Clear();
                colours?.Clear();

                for (var row = first; row < last; row++)
                {
                    for (var col = 0; col < band.Width - 1; col++)
                    {
                        // All four or none. Three corners would be one triangle and a notch, and a
                        // relief made of notches reads as damage rather than as a hole.
                        if (band.CodeAt(col, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col, row + 1) == MapMeshFile.NoHit) continue;
                        if (band.CodeAt(col + 1, row + 1) == MapMeshFile.NoHit) continue;

                        var a = Corner(band, col, row, first, map, vertices, uvs, colours, spanX, spanZ);
                        var b = Corner(band, col + 1, row, first, map, vertices, uvs, colours, spanX, spanZ);
                        var c = Corner(band, col, row + 1, first, map, vertices, uvs, colours, spanX, spanZ);
                        var d = Corner(band, col + 1, row + 1, first, map, vertices, uvs, colours, spanX, spanZ);

                        // Wound so the face points UP: with +x to the right and +z away, (a, c, b) has
                        // its cross product along +y. The same winding the phase 3-0 heightfield used,
                        // which rendered lit rather than black.
                        indices.Add(a);
                        indices.Add(c);
                        indices.Add(b);

                        indices.Add(b);
                        indices.Add(c);
                        indices.Add(d);
                    }
                }

                if (indices.Count == 0) continue;

                into.Ground.Add(MakeMesh($"{_mapKey}-relief-{band.Level}-{first}", vertices, uvs, indices, colours));
                into.GroundTriangles += indices.Count / 3;

                if (last >= band.Height - 1) break;
            }
        }

        /// <summary>One cell centre as a vertex, added on first use. The map is per CHUNK - indices are
        /// relative to the mesh being filled - which is why its row is offset by the chunk's first
        /// row.</summary>
        private int Corner(
            MapMeshFile.ReliefBand band, int col, int row, int firstRow, int[] map,
            List<Vector3> vertices, List<Vector2> uvs, List<Color32> colours, float spanX, float spanZ)
        {
            var slot = (row - firstRow) * band.Width + col;
            var known = map[slot];
            if (known >= 0) return known;

            var x = band.CellCentreX(col);
            var z = band.CellCentreZ(row);
            var y = _file.HeightOf(band.CodeAt(col, row));

            map[slot] = vertices.Count;

            vertices.Add(new Vector3(x, y, z));
            uvs.Add(PlanarUv(x, z, spanX, spanZ));
            colours?.Add(FlatGroundColour);

            return map[slot];
        }

        /// <summary>
        /// The buildings that belong to this floor, merged into as few meshes as the vertex cap allows.
        ///
        /// Merged rather than one mesh each because a draw call per building is 212 of them a frame on
        /// Customs for geometry that never moves relative to the rest, and a merged mesh is also a
        /// merged bounds - one frustum test instead of two hundred.
        ///
        /// ROOFS ONLY. A face whose normal is within 60 degrees of vertical (<c>|n.y| &gt;= 0.5</c>,
        /// <see cref="IsRoof"/>) takes the top-down picture on planar UVs, as every face used to. A
        /// steeper one - a wall - is only COUNTED here and built by <see cref="BuildWalls"/> in a flat
        /// colour: planar UVs on a vertical face sample one column of roof-edge pixels and stretch it
        /// down the whole height, which on screen was a building striped from eaves to ground. The two
        /// passes classify with the same function, so a triangle is in exactly one of them.
        /// </summary>
        /// <param name="level">The floor's band level.</param>
        /// <param name="into">The cache entry being filled in.</param>
        /// <param name="flat">Whether to write vertex colours.</param>
        private void BuildBuildings(int level, Built into, bool flat)
        {
            if (_file.Buildings == null || _file.Buildings.Count == 0) return;

            var spanX = (float)(_file.MaxX - _file.MinX);
            var spanZ = (float)(_file.MaxZ - _file.MinZ);
            if (!(spanX > 0f) || !(spanZ > 0f)) return;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var colours = flat ? new List<Color32>() : null;

            // One per side slot, made on the first face that side takes.
            var sideAccumulators = new WallAccumulator[SideOrder.Length];

            var part = 0;

            foreach (var building in _file.Buildings)
            {
                if (building == null || building.VertexCount == 0 || building.Indices == null) continue;
                if (BandLevelFor(building.Level) != level) continue;

                into.BuildingCount++;

                if (vertices.Count + building.VertexCount > MaxVerticesPerMesh && indices.Count > 0)
                {
                    into.Buildings.Add(MakeMesh(
                        $"{_mapKey}-buildings-{level}-{part++}", vertices, uvs, indices, colours));
                    into.BuildingTriangles += indices.Count / 3;

                    vertices.Clear();
                    uvs.Clear();
                    indices.Clear();
                    colours?.Clear();
                }

                var offset = vertices.Count;

                // Which of this building's vertices are usable at all. A y stored as NoHit dequantises
                // to NaN (MapMeshFile.HeightOf answers NaN on purpose, so a caller that forgets fails
                // visibly), and ONE NaN vertex in a merged bucket makes the whole bucket's bounds NaN -
                // which fails the frustum test and takes two hundred buildings off the screen together.
                // So the vertex goes in, to keep the file's own indices valid, and every triangle that
                // touches it is dropped.
                if (_finite.Length < building.VertexCount) _finite = new bool[building.VertexCount];

                for (var i = 0; i < building.VertexCount; i++)
                {
                    var vertex = building.VertexAt(i);

                    _finite[i] = !float.IsNaN(vertex.x) && !float.IsInfinity(vertex.x) &&
                                 !float.IsNaN(vertex.y) && !float.IsInfinity(vertex.y) &&
                                 !float.IsNaN(vertex.z) && !float.IsInfinity(vertex.z);

                    // Zero rather than the NaN: an unreferenced vertex still goes through
                    // RecalculateNormals and RecalculateBounds.
                    vertices.Add(_finite[i] ? vertex : Vector3.zero);
                    uvs.Add(_finite[i] ? PlanarUv(vertex.x, vertex.z, spanX, spanZ) : Vector2.zero);
                    colours?.Add(FlatBuildingColour);
                }

                // Three at a time, and a triangle with an index past the building's own vertices is
                // dropped: the format's reader validates this, and a caller that trusts a file it did
                // not write is a caller that throws inside a mesh build.
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count; i += 3)
                {
                    var a = building.Indices[i];
                    var b = building.Indices[i + 1];
                    var c = building.Indices[i + 2];

                    if (a >= building.VertexCount || b >= building.VertexCount || c >= building.VertexCount)
                        continue;

                    if (!_finite[a] || !_finite[b] || !_finite[c])
                    {
                        into.Dropped++;
                        continue;
                    }

                    var pa = vertices[offset + (int)a];
                    var pb = vertices[offset + (int)b];
                    var pc = vertices[offset + (int)c];

                    var view = ViewFor(pa, pb, pc);

                    if (view == TintView)
                    {
                        into.WallTriangles++;
                        continue;
                    }

                    if (view != TopView)
                    {
                        // A side picture: unshared vertices (crisp per-face light, as for the tints) with
                        // UVs from that side's projection.
                        var slot = view - 1;
                        var target = sideAccumulators[slot] ??= NewSideAccumulator(into, level, slot);
                        var picture = _sides[slot];

                        if (target.Vertices.Count + 3 > MaxVerticesPerMesh) target.Flush($"{_mapKey}-side{SideOrder[slot]}-{level}");

                        target.Add(pa, SideUv(picture, pa));
                        target.Add(pb, SideUv(picture, pb));
                        target.Add(pc, SideUv(picture, pc));

                        into.SideTriangles++;
                        continue;
                    }

                    indices.Add(offset + (int)a);
                    indices.Add(offset + (int)b);
                    indices.Add(offset + (int)c);
                    into.TopTriangles++;
                }
            }

            for (var slot = 0; slot < sideAccumulators.Length; slot++)
                sideAccumulators[slot]?.Flush($"{_mapKey}-side{SideOrder[slot]}-{level}");

            // Counted into the building total whether or not they are built yet, so the log line's
            // totals are the file's and do not move when a floor's walls arrive a frame later.
            into.BuildingTriangles += into.WallTriangles + into.SideTriangles;
            into.WallsPending = into.WallTriangles > 0;

            if (indices.Count == 0) return;

            into.Buildings.Add(MakeMesh($"{_mapKey}-buildings-{level}-{part}", vertices, uvs, indices, colours));
            into.BuildingTriangles += indices.Count / 3;
        }

        /// <summary>One side's submesh on the entry, registered BEFORE anything is built into it (so a
        /// throw halfway leaves every mesh where DestroyBuilt finds it), and its accumulator.</summary>
        private WallAccumulator NewSideAccumulator(Built into, int level, int slot)
        {
            var side = new SideTexture
            {
                Material = Matte(new Material(_buildingShader) { name = $"QuestTreeMap3D-side{SideOrder[slot]}-{level}" })
            };

            into.Sides[slot] = side;

            return new WallAccumulator { Target = side.Meshes, Flat = false };
        }

        // --- which picture a face takes ------------------------------------------------------------

        /// <summary>What <see cref="ViewFor"/> answers for a face textured by the top-down picture.</summary>
        private const int TopView = 0;

        /// <summary>What <see cref="ViewFor"/> answers for a face drawn in a flat tint.</summary>
        private const int TintView = -1;

        /// <summary>The least score a face needs to be textured by the picture that sees it best: below
        /// this every picture sees it too obliquely, and a stretched texture is worse than a tint.</summary>
        private const float MinViewScore = 0.35f;

        /// <summary>
        /// Which picture textures a building face: <see cref="TopView"/>, a side (slot + 1), or
        /// <see cref="TintView"/>.
        ///
        /// WITHOUT side pictures this is exactly the tint build's rule - top when <c>|n.y| &gt;= 0.5</c>
        /// (<see cref="IsRoof"/>), else tint - so a capture taken before the sides existed draws exactly
        /// as it did.
        ///
        /// WITH them it is the Stage U contract: the picture whose camera looks most squarely at the face,
        /// scored <c>-dot(n, f)</c> with the top-down camera's f = (0,-1,0) among them; the best score under
        /// <see cref="MinViewScore"/> is a tint. The normal is the triangle's own (the file carries none),
        /// and SIGNED - which side a wall faces is the whole question here - so it relies on the builder's
        /// winding, which stage T fixed for mirrored transforms. Ties go to the earlier view (top, then N,
        /// S, E, W), so a 45-degree face is classified the same way on every build.
        /// </summary>
        private int ViewFor(Vector3 a, Vector3 b, Vector3 c)
        {
            if (!SidesActive) return IsRoof(a, b, c) ? TopView : TintView;

            var n = Vector3.Cross(b - a, c - a);
            var length = n.magnitude;

            // No facing: with the roofs, where it draws nothing either way - the tint build's rule too.
            if (!(length > 1e-6f)) return TopView;

            n /= length;

            // The top camera looks straight down: -dot(n, (0,-1,0)) = n.y.
            var best = TopView;
            var bestScore = n.y;

            for (var slot = 0; slot < _sides.Length; slot++)
            {
                var side = _sides[slot];
                if (side == null) continue;

                var score = -Vector3.Dot(n, side.Forward);
                if (score <= bestScore) continue;

                bestScore = score;
                best = slot + 1;
            }

            return bestScore < MinViewScore ? TintView : best;
        }

        /// <summary>
        /// A world point's UV on a side picture - the Stage U contract's image mapping, turned into
        /// texture coordinates.
        ///
        /// The contract gives pixels with row 0 at the image TOP: <c>px = (dot(r,p) - originR) * ppm</c>,
        /// <c>py = height - (dot(u,p) - originU) * ppm</c>. A decoded texture has v = 0 at its BOTTOM row
        /// (LoadImage puts the file's first row at the top of the texture), so <c>v = 1 - py / height =
        /// (dot(u,p) - originU) * ppm / height</c> - the "height minus" and the "one minus" cancel. That is
        /// the same convention the floors are sampled in: a floor picture's top row is MaxZ and its v = 0
        /// is MinZ, and here the top row is the highest dot(u, p) and v = 0 the lowest. Clamped, like every
        /// UV here.
        /// </summary>
        private static Vector2 SideUv(DynamicMapsLibrary.SidePicture side, Vector3 p)
        {
            var u = (Vector3.Dot(side.Right, p) - side.OriginR) * side.PxPerMetre / side.Width;
            var v = (Vector3.Dot(side.Up, p) - side.OriginU) * side.PxPerMetre / side.Height;

            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        /// <summary>How close to vertical a face's normal has to be to count as a roof: the cosine of 60
        /// degrees. Above it the top-down picture is a fair texture; below it the face is seen edge-on
        /// from above and the picture has nothing to give it.</summary>
        private const float RoofNormalY = 0.5f;

        /// <summary>
        /// Whether a triangle faces up or down enough to take the top-down picture - from its own cross
        /// product, because the file carries no normals.
        ///
        /// |n.y| and not n.y: an overhang's underside faces straight down and is as flat as a roof, and
        /// the winding of a building read off the GPU is only as reliable as the builder's mirror test.
        /// A degenerate triangle has no facing at all and goes with the roofs, where it draws nothing
        /// either way.
        /// </summary>
        private static bool IsRoof(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            var length = n.magnitude;

            if (!(length > 1e-6f)) return true;

            return Mathf.Abs(n.y) / length >= RoofNormalY;
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

        /// <summary>
        /// Builds one floor's walls, if they are waiting and there is now a way to colour them: in flat
        /// colours straight away, else once the floor's picture is resident. Never throws - a wall build
        /// that fails costs the walls of one floor (the roofs and the ground still draw) and says so once.
        /// </summary>
        /// <param name="into">The floor's cache entry.</param>
        /// <param name="level">The floor's band level.</param>
        /// <param name="picture">The floor's picture, or null when it is not decoded yet.</param>
        /// <param name="late">True when called from a frame after the view was built, which is the case
        /// that gets its own log line (the build line has already been written).</param>
        private void TryBuildWalls(Built into, int level, Texture picture, bool late)
        {
            if (into == null || !into.WallsPending) return;
            if (!_flatColours && picture == null) return;

            var clock = Stopwatch.StartNew();

            try
            {
                BuildWalls(into, level, picture);

                if (late)
                {
                    Plugin.LogSource?.LogInfo(string.Format(
                        CultureInfo.InvariantCulture,
                        "QuestTree: 3D map walls for {0} floor {1} - {2:#,##0} triangles in {3} tint(s), built in " +
                        "{4:#,##0} ms (the floor's picture had just arrived).",
                        _mapKey, level, into.WallTriangles, into.Tints, clock.ElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                // What was made before the throw is already in the entry (each mesh is registered as it
                // is made) and goes here, rather than being left half-drawn.
                DestroyWalls(into);

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the walls of floor {level} of the 3D map for '{_mapKey}' could not be built " +
                    $"({ex.GetType().Name}: {ex.Message}) - that floor draws its roofs and ground only.");
            }
            finally
            {
                // Once, success or not: a failure retried every frame is a warning every frame.
                into.WallsPending = false;
            }
        }

        /// <summary>
        /// One floor's walls: coloured from the picture under each building, grouped into at most
        /// <see cref="MaxWallTints"/> colours, one mesh-and-material per colour.
        ///
        /// Two walks over the floor's buildings. The first finds each building that has walls and its
        /// colour, so the colours can be bucketed knowing all of them; the second builds the geometry into
        /// the buckets. Dequantising twice costs a few milliseconds and saves holding every wall triangle
        /// of the floor in memory between the two.
        ///
        /// Wall vertices are NOT shared between triangles, unlike the roofs': each triangle gets its own
        /// three. RecalculateNormals averages the normals of the faces a vertex belongs to, and a corner
        /// vertex shared by two walls at right angles would light both of them as if they faced the
        /// corner - a box would shade like a cylinder. Unshared, every wall takes the light at its own
        /// angle, and the sunny side of a building is visibly not its shady side.
        /// </summary>
        /// <param name="into">The floor's cache entry; the walls are added to it as they are made.</param>
        /// <param name="level">The floor's band level.</param>
        /// <param name="picture">The floor's picture, or null under flat colours.</param>
        private void BuildWalls(Built into, int level, Texture picture)
        {
            var spanX = (float)(_file.MaxX - _file.MinX);
            var spanZ = (float)(_file.MaxZ - _file.MinZ);
            if (!(spanX > 0f) || !(spanZ > 0f)) return;

            Color32[] palette = null;
            var paletteWidth = 0;
            var paletteHeight = 0;

            if (!_flatColours && picture != null)
            {
                try
                {
                    palette = ReadPalette(picture, out paletteWidth, out paletteHeight);
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

            // --- walk 1: which buildings have walls, and in what colour
            var walled = new List<int>();
            var colours = new List<Color>();

            for (var index = 0; index < _file.Buildings.Count; index++)
            {
                var building = _file.Buildings[index];
                if (!Usable(building) || BandLevelFor(building.Level) != level) continue;

                if (!LoadBuilding(building)) continue;

                var hasWall = false;
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count && !hasWall; i += 3)
                {
                    if (WallTriangle(building, i, out _, out _, out _)) hasWall = true;
                }

                if (!hasWall) continue;

                walled.Add(index);
                colours.Add(_flatColours ? (Color)FlatWallColour : WallColour(palette, paletteWidth, paletteHeight, spanX, spanZ));
            }

            if (walled.Count == 0) return;

            // The floor's wall average, for the faces of a side whose picture is missing - see
            // Built.SideFallback. Recoloured in place if that material already exists.
            if (!_flatColours)
            {
                var sum = Color.black;
                for (var i = 0; i < colours.Count; i++) sum += colours[i];

                var average = sum / colours.Count;
                average.a = 1f;

                into.WallAverage = average;
                if (into.SideFallback != null) into.SideFallback.color = average;
            }

            // --- the buckets
            var centres = new List<Color>();
            var bucketOf = BucketColours(colours, _flatColours ? 1 : MaxWallTints, centres);

            // Registered BEFORE anything is built into them, so a throw anywhere below leaves every mesh
            // and material already made where DestroyWalls will find it.
            var accumulators = new WallAccumulator[centres.Count];

            for (var k = 0; k < centres.Count; k++)
            {
                var tint = new WallTint { Colour = centres[k], Material = MakeWallMaterial(level, k, centres[k]) };
                into.Walls.Add(tint);

                accumulators[k] = new WallAccumulator { Target = tint.Meshes, Flat = _flatColours };
            }

            into.Tints = _flatColours ? 0 : centres.Count;

            // --- walk 2: the geometry
            for (var w = 0; w < walled.Count; w++)
            {
                var building = _file.Buildings[walled[w]];
                if (!LoadBuilding(building)) continue;

                var target = accumulators[bucketOf[w]];
                var count = building.Indices.Length - building.Indices.Length % 3;

                for (var i = 0; i < count; i += 3)
                {
                    if (!WallTriangle(building, i, out var a, out var b, out var c)) continue;

                    if (target.Vertices.Count + 3 > MaxVerticesPerMesh) target.Flush($"{_mapKey}-walls-{level}");

                    target.Add(a, PlanarUv(a.x, a.z, spanX, spanZ));
                    target.Add(b, PlanarUv(b.x, b.z, spanX, spanZ));
                    target.Add(c, PlanarUv(c.x, c.z, spanX, spanZ));
                }
            }

            for (var k = 0; k < accumulators.Length; k++) accumulators[k].Flush($"{_mapKey}-walls-{level}");
        }

        /// <summary>Whether a building has anything to build from at all.</summary>
        private static bool Usable(MapMeshFile.Building building) =>
            building != null && building.VertexCount > 0 && building.Indices != null;

        /// <summary>Dequantises one building's vertices into <see cref="_positions"/> and flags the finite
        /// ones in <see cref="_finite"/>, the same test the roof pass makes. False for a building with
        /// nothing usable.</summary>
        private bool LoadBuilding(MapMeshFile.Building building)
        {
            if (_finite.Length < building.VertexCount) _finite = new bool[building.VertexCount];
            if (_positions.Length < building.VertexCount) _positions = new Vector3[building.VertexCount];

            var any = false;
            _lastVertexCount = building.VertexCount;

            for (var i = 0; i < building.VertexCount; i++)
            {
                var vertex = building.VertexAt(i);

                _finite[i] = !float.IsNaN(vertex.x) && !float.IsInfinity(vertex.x) &&
                             !float.IsNaN(vertex.y) && !float.IsInfinity(vertex.y) &&
                             !float.IsNaN(vertex.z) && !float.IsInfinity(vertex.z);

                _positions[i] = vertex;
                any |= _finite[i];
            }

            return any;
        }

        /// <summary>Scratch for <see cref="LoadBuilding"/>, grown as needed.</summary>
        private Vector3[] _positions = new Vector3[0];

        /// <summary>How many of <see cref="_positions"/> belong to the building last loaded - the arrays
        /// are grown, never shrunk, so their length says nothing.</summary>
        private int _lastVertexCount;

        /// <summary>The triangle at <paramref name="i"/> of the building <see cref="LoadBuilding"/> last
        /// loaded, if it is a usable WALL - in range, finite, and not a roof by <see cref="IsRoof"/>. The
        /// exact complement of what the roof pass keeps.</summary>
        private bool WallTriangle(MapMeshFile.Building building, int i, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            a = b = c = Vector3.zero;

            var ia = building.Indices[i];
            var ib = building.Indices[i + 1];
            var ic = building.Indices[i + 2];

            if (ia >= building.VertexCount || ib >= building.VertexCount || ic >= building.VertexCount) return false;
            if (!_finite[ia] || !_finite[ib] || !_finite[ic]) return false;

            a = _positions[ia];
            b = _positions[ib];
            c = _positions[ic];

            return ViewFor(a, b, c) == TintView;
        }

        /// <summary>
        /// The colour of the building <see cref="LoadBuilding"/> last loaded: the picture under the middle
        /// of its footprint, a quarter darker and a little greyer.
        ///
        /// Darker because a wall is lit from the side while the roof in the picture was lit from above, and
        /// the same material at a glancing light reads darker; greyer because a roof's colour is its
        /// covering - red iron, green felt - and the walls under it are usually plainer than that. So a
        /// red-roofed warehouse gets dark reddish-grey walls and a concrete block gets grey ones.
        ///
        /// A 3x3 average around the centroid, ignoring transparent pixels (the picture's walkable cut-out),
        /// so one odd pixel - a vent, an aerial - does not colour a whole building.
        /// </summary>
        private Color WallColour(Color32[] palette, int width, int height, float spanX, float spanZ)
        {
            if (palette == null || width <= 0 || height <= 0) return FallbackWallColour;

            var sumX = 0d;
            var sumZ = 0d;
            var n = 0;

            // The building LoadBuilding loaded last - walk 1 calls this straight after loading it.
            for (var i = 0; i < _lastVertexCount; i++)
            {
                if (!_finite[i]) continue;
                sumX += _positions[i].x;
                sumZ += _positions[i].z;
                n++;
            }

            if (n == 0) return FallbackWallColour;

            var uv = PlanarUv((float)(sumX / n), (float)(sumZ / n), spanX, spanZ);

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

                    // Row 0 of a texture read back with ReadPixels is its BOTTOM row, which for these
                    // pictures is MinZ - the same convention the planar UV is in, so uv.y indexes it
                    // directly with no flip.
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

            var material = Matte(new Material(_buildingShader) { name = name });

            if (!material.HasProperty("_Color"))
            {
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

        /// <summary>One wall colour's geometry while it is built: unshared vertices, flushed into a mesh
        /// on the tint whenever the vertex cap is reached.</summary>
        private sealed class WallAccumulator
        {
            /// <summary>Where the finished meshes go: a tint's list or a side's.</summary>
            public List<Mesh> Target;
            public bool Flat;

            public readonly List<Vector3> Vertices = new List<Vector3>();
            private readonly List<Vector2> _uvs = new List<Vector2>();
            private readonly List<int> _indices = new List<int>();
            private readonly List<Color32> _colours = new List<Color32>();
            private int _part;

            public void Add(Vector3 position, Vector2 uv)
            {
                _indices.Add(Vertices.Count);
                Vertices.Add(position);
                _uvs.Add(uv);
                if (Flat) _colours.Add(FlatWallColour);
            }

            public void Flush(string name)
            {
                if (_indices.Count == 0) return;

                Target.Add(MakeMesh($"{name}-{_part++}", Vertices, _uvs, _indices, Flat ? _colours : null));

                Vertices.Clear();
                _uvs.Clear();
                _indices.Clear();
                _colours.Clear();
            }
        }

        /// <summary>Scratch for <see cref="BuildBuildings"/>'s finite test, grown as needed rather than
        /// allocated per building - twenty thousand buildings is twenty thousand arrays otherwise.</summary>
        private bool[] _finite = new bool[0];

        /// <summary>
        /// The planar UV of a world point: where it falls across the extent, which is where it falls
        /// across the picture.
        ///
        /// CLAMPED, because the grid can overhang. A band is <c>ceil(span / cell)</c> cells wide, so the
        /// last column's centre can sit up to half a cell past MaxX, and a building's triangles are kept
        /// up to a metre outside the extent - both give a u or v a little over 1. The texture's wrap mode
        /// is Clamp, so today those sample the edge pixel anyway; clamping here means that stays true if
        /// anything ever hands this view a texture that repeats.
        /// </summary>
        /// <param name="x">World x in metres.</param>
        /// <param name="z">World z in metres.</param>
        /// <param name="spanX">The extent's x span.</param>
        /// <param name="spanZ">The extent's z span.</param>
        private Vector2 PlanarUv(float x, float z, float spanX, float spanZ) =>
            new Vector2(
                Mathf.Clamp01((x - (float)_file.MinX) / spanX),
                Mathf.Clamp01((z - (float)_file.MinZ) / spanZ));

        /// <summary>The band a building's declared level belongs to: its own where the file has that
        /// band, else the nearest. The writer never produces a building without a band, but a file from
        /// a future builder might, and dropping the geometry would be the worse answer.</summary>
        /// <param name="level">The building's declared level.</param>
        private int BandLevelFor(int level)
        {
            var best = int.MinValue;
            var distance = int.MaxValue;

            foreach (var band in _file.Bands)
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

        /// <summary>One mesh from the lists just filled. <see cref="IndexFormat.UInt32"/> is set BEFORE
        /// the vertices, which is not a style choice: a mesh left on the default sixteen-bit format
        /// silently wraps its indices past 65,535 vertices, and Customs' relief is 150,000.</summary>
        private static Mesh MakeMesh(
            string name, List<Vector3> vertices, List<Vector2> uvs, List<int> indices, List<Color32> colours)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };

            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            if (colours != null) mesh.SetColors(colours);
            mesh.SetTriangles(indices, 0, calculateBounds: true);
            mesh.RecalculateNormals();

            return mesh;
        }

        // --- the frame ------------------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_broke) return;

            try
            {
                if (_loading != null)
                {
                    if (!_loading.IsCompleted) return;

                    if (_loading.IsFaulted)
                    {
                        var reason = _loading.Exception?.GetBaseException();

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
                        BuildMeshes();
                    }
                    catch (Exception ex)
                    {
                        // A throw in the mesh build leaves an empty viewport, which is the one outcome
                        // worse than 2D - so this one refuses the mesh rather than just going quiet.
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the 3D relief of '{_mapKey}' could not be turned into meshes " +
                            $"({ex.GetType().Name}: {ex.Message}) - drawing the flat picture instead.");

                        Refuse("could not be turned into meshes");
                    }

                    return;
                }

                // Before anything is drawn this frame, so no mesh queued below belongs to the build being thrown
                // away. See RebuildWithoutFailedSides.
                if (_sideFailed) RebuildWithoutFailedSides();

                if (_camera == null || _floors.Count == 0) return;

                EnsureRenderTexture();
                Place();

                for (var i = 0; i < _floors.Count; i++) Draw(_floors[i]);

                RenderNow();
                PlaceOverlays();
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

            // The first frame this floor's picture is here: its walls can be coloured now. Once per entry
            // (TryBuildWalls clears the flag whatever happens), so this is not a per-frame cost.
            if (meshes.WallsPending) TryBuildWalls(meshes, floor.Level, ground.mainTexture, late: true);

            for (var i = 0; i < meshes.Ground.Count; i++)
            {
                var mesh = meshes.Ground[i];
                if (mesh != null) Graphics.DrawMesh(mesh, Matrix4x4.identity, ground, _drawLayer, _camera);
            }

            // Every BUILDING mesh goes through Under(): itself with no cut, its clipped twin with one. The
            // ground above is never cut - the peel already leaves out every band over the chosen floor.
            for (var i = 0; i < meshes.Buildings.Count; i++)
            {
                var mesh = Under(meshes, meshes.Buildings[i], _cutY);
                if (mesh != null) Graphics.DrawMesh(mesh, Matrix4x4.identity, walls, _drawLayer, _camera);
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
                    var mesh = Under(meshes, tint.Meshes[i], _cutY);
                    if (mesh != null) Graphics.DrawMesh(mesh, Matrix4x4.identity, material, _drawLayer, _camera);
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
                    var mesh = Under(meshes, side.Meshes[i], _cutY);
                    if (mesh != null) Graphics.DrawMesh(mesh, Matrix4x4.identity, draw, _drawLayer, _camera);
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

            var dropped = "";

            for (var slot = 0; slot < _sides.Length; slot++)
            {
                var picture = _sides[slot]?.Picture;
                if (picture == null || !picture.ArtworkFailed) continue;

                dropped += SideOrder[slot];
                _sides[slot] = null;
            }

            if (dropped.Length == 0 || _loaded == null) return;

            CountSides();

            // The room for the dropped sides goes back; BuildMeshes takes what the rest need.
            ReturnSideRoom();

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the side picture(s) {string.Join(",", dropped.ToCharArray())} of the 3D map for " +
                $"'{_mapKey}' could not be decoded - the walls are rebuilt without them.");

            ReleaseFloors();
            BuildMeshes();
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

            try
            {
                RenderSettings.fog = false;
                _light.enabled = true;

                _camera.Render();
            }
            finally
            {
                // FOG FIRST. Both matter, and if one of them is going to be skipped by a throw it must
                // not be the global: a light left on is ours to find on our own object, while fog left
                // off is the menu's own scene changed under the player for the rest of the session. Each
                // is guarded separately for the same reason - one throwing must not skip the other.
                try { RenderSettings.fog = fog; } catch (Exception) { /* nothing further to try */ }
                try { if (_light != null) _light.enabled = false; } catch (Exception) { /* as above */ }
            }
        }

        /// <summary>Puts the camera where the orbit says, looking at the focus point on the ground.</summary>
        private void Place()
        {
            if (_camera == null) return;

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var target = new Vector3(_focus.x, GroundAt(_focus.x, _focus.y), _focus.y);

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

            if (layer != null && layer.GameBounds.Count > 0) return layer.GameBounds[0].Min.z;

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
