using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.VectorGraphics;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// Reads the map images shipped by the DynamicMaps mod, if it is installed, so the Maps view can
    /// show the actual map beside the quest list.
    ///
    /// Only DynamicMaps' DATA is used - its map folder and the JSON beside each image. Its assembly
    /// is never referenced and none of its types are touched. That distinction matters: calling
    /// another mod's internals breaks whenever they refactor, whereas a data folder with a stable
    /// shape does not, and this mod has already been bitten once by depending on another mod's
    /// construction timing.
    ///
    /// Both DynamicMaps (Michael P. Starkweather) and the underlying map data (tarkov.dev, Oskar
    /// Risberg) are MIT licensed, so reading them is permitted; nothing is copied or redistributed,
    /// the files are read in place from the user's own install, and the map's own author credit
    /// travels with it in <see cref="MapEntry.Attribution"/> so it can be shown.
    ///
    /// COORDINATE SPACE, because getting this wrong cost three attempts. A map position is
    /// (game.x, game.z), and its height is game.y - the swap DynamicMaps' own MathUtils performs as
    /// `new Vector3(pos.x, pos.z, pos.y)`. Every Bounds, ImageBounds, GameBounds and Label position
    /// in these configs is in that space, which is why a GameBounds box's "z" is a HEIGHT band.
    ///
    /// Everything degrades: no DynamicMaps, no unreadable config and no failed SVG parse may stop
    /// the Maps view rendering its list.
    /// </summary>
    internal static class DynamicMapsLibrary
    {
        /// <summary>Where DynamicMaps installs. Resolved relative to our own plugin folder rather
        /// than hardcoded, so it follows a non-standard SPT install.</summary>
        private const string MapsModFolder = "mpstark-dynamicmaps";

        /// <summary>A volume of the world a floor applies over: x and y are the ground position,
        /// z the height - see the class remarks.</summary>
        internal readonly struct GameBox
        {
            public readonly Vector3 Min;
            public readonly Vector3 Max;

            public GameBox(Vector3 min, Vector3 max)
            {
                Min = min;
                Max = max;
            }

            public bool Contains(float x, float y, float height) =>
                x >= Min.x && x <= Max.x &&
                y >= Min.y && y <= Max.y &&
                height >= Min.z && height <= Max.z;
        }

        /// <summary>One floor of one map: its picture, the world rectangle that picture covers, and
        /// the volumes of the world it is the right floor for.</summary>
        internal sealed class MapLayer
        {
            public string Name = "";
            public int Level;
            public string ImagePath = "";

            /// <summary>
            /// Whether <see cref="ImagePath"/> is a bitmap - one of our own captured PNGs - rather
            /// than one of DynamicMaps' SVGs.
            ///
            /// Two loaders and two components to draw the result with, which is why this is a flag
            /// and not a guess at the extension. An SVG becomes a sprite backed by tessellated
            /// geometry with no texture behind it, which only SVGImage can draw; a captured PNG
            /// becomes a sprite backed by a Texture2D, which only a plain Image can draw
            /// (MapView.BuildMapViewport branches on this). Set by <see cref="MapCatalog"/> when it
            /// reads a capture's meta file.
            /// </summary>
            public bool IsRaster;

            /// <summary>
            /// The floor's name as the map ARTWORK calls it - the part of the image filename after
            /// the map's own name, so "Interchange-First_Floor.svg" gives "First_Floor". Empty for a
            /// map whose image carries no suffix, as Lighthouse's plain "Lighthouse.svg" does not.
            ///
            /// This exists because it is the only vocabulary the outside quest data shares. The
            /// config's display key for that same layer is "2nd Floor", and matching on it fails for
            /// 171 of the 232 objective locations; matching on the filename resolves all 232. It
            /// also settles a question a sensible guess gets backwards - Interchange's "First_Floor"
            /// is Level 1, the storey above the ground, not level 0.
            /// </summary>
            public string FloorName = "";

            public Vector2 BoundsMin;
            public Vector2 BoundsMax;

            public readonly List<GameBox> GameBounds = new();

            public bool HasBounds => BoundsMax.x > BoundsMin.x && BoundsMax.y > BoundsMin.y;

            /// <summary>Whether there is an image file to draw at all. False for a floor
            /// <see cref="MapCatalog"/> synthesised from a harvested extent: it knows the rectangle
            /// and the height band, and the view draws a plain backdrop over the rectangle instead
            /// of a picture. Both sprite calls answer "no sprite, and none is coming" for one of
            /// these rather than starting a tessellation of the empty path.</summary>
            public bool HasArtwork => !string.IsNullOrEmpty(ImagePath);

            /// <summary>Whether the picture has been tried and cannot be had: a PNG that would not
            /// decode, an SVG with no viewBox or too dense to tessellate. The view reads it to draw
            /// the floor's rectangle as a backdrop instead, so a picture that fails costs the
            /// picture rather than the map, its scale and its pins.</summary>
            public bool ArtworkFailed => _spriteFailed;

            public Vector2 BoundsSize => BoundsMax - BoundsMin;
            public Vector2 BoundsCentre => (BoundsMin + BoundsMax) * 0.5f;

            /// <summary>The SVG's viewBox, in SVG units. This is the rectangle the layer's
            /// ImageBounds describes, and so the one that has to land on it.</summary>
            public Rect Viewport;

            private Sprite _sprite;
            private bool _spriteFailed;

            /// <summary>The PNG read in flight on a worker thread, or null. Only the READ is off
            /// the main thread: decoding it is ImageConversion.LoadImage, which touches the
            /// graphics device. See TryGetRasterSprite.</summary>
            private Task<byte[]> _loadingBytes;

            /// <summary>How much texture memory this floor's decoded picture holds, or 0 when it
            /// holds none. Summed by <see cref="ResidentRasterBytes"/>: one captured floor of a big
            /// map is around 39 MiB (3262x3136 at RGBA32, which is the largest a floor can be - the
            /// capture's own memory budget caps one at 256 MiB of working set at 26 B a pixel, so no
            /// picture reaches 10.4 million pixels), so the cache ceiling is worth seeing. A field rather
            /// than a property with a private setter because the loader that knows the answer is
            /// BuildRasterSprite, in the enclosing class, which private would shut out.</summary>
            public long RasterBytes;

            /// <summary>The tessellation in flight on a worker thread, or null. See TryGetSprite.</summary>
            private Task<PreparedArtwork> _preparing;

            /// <summary>Whether a tessellation has finished and the sprite only awaits the main
            /// thread. Polled once a frame by the panel while the map is up.</summary>
            public bool IsReadyToBuild =>
                (_preparing != null && _preparing.IsCompleted) ||
                (_loadingBytes != null && _loadingBytes.IsCompleted);

            /// <summary>Rasterised on first use and kept - tessellating a 340KB SVG is not something
            /// to repeat every time a floor is switched back to. Kept for the most recently used
            /// few, not forever: each is a mesh of up to 65,500 vertices, and a session that
            /// browsed every floor of every map held all of them for the life of the process.
            ///
            /// SYNCHRONOUS, and the map view no longer calls it: measured at 908 ms on the panel's
            /// open path, two thirds of the whole wait. TryGetSprite below is what the view uses.
            /// Kept as the fallback for a tessellation that fails off-thread, and for any caller
            /// that would rather block than repaint.</summary>
            public Sprite GetSprite()
            {
                if (!HasArtwork) return null;

                if (_sprite != null)
                {
                    NoteSpriteUse(this);
                    return _sprite;
                }

                if (_spriteFailed) return null;

                if (IsRaster)
                {
                    // Read and decoded here and now. Nothing on the view's open path comes through
                    // GetSprite any more - TryGetSprite below is what it uses - so the blocking
                    // read is only for a caller that would rather block, and for a worker-thread
                    // read that faulted.
                    _loadingBytes = null;
                    _sprite = LoadRasterSprite(ImagePath, this);
                    _spriteFailed = _sprite == null;
                    if (_sprite != null) NoteSpriteUse(this);
                    return _sprite;
                }

                _preparing = null;
                _sprite = LoadSvgSprite(ImagePath, this);
                _spriteFailed = _sprite == null;
                if (_sprite != null) NoteSpriteUse(this);
                return _sprite;
            }

            /// <summary>The sprite if it exists or can be finished now, otherwise starts making it
            /// and returns false so the caller can paint without it and come back.
            ///
            /// The expensive part - reading the SVG, parsing it, tessellating it through the
            /// presets until it fits the index budget - is pure C# over structs and runs on a
            /// worker thread. Only BuildSprite, which makes a Mesh, a Sprite and possibly a
            /// Texture2D, has to be on Unity's thread, and it is the cheap part. The view paints
            /// its list at once, the panel polls <see cref="IsReadyToBuild"/> each frame, and the
            /// map arrives on the repaint that follows - which the player sees as the list first
            /// and the picture a moment later, instead of nothing for a second.
            ///
            /// A worker-thread failure falls back to the synchronous path once, so the answer is
            /// never "no image" because of a threading assumption in a library this mod does not
            /// own. True with a null sprite means there really is no usable image.</summary>
            public bool TryGetSprite(out Sprite sprite)
            {
                // Answered, not pending: there is no file, so no repaint would ever bring one. A
                // floor with no artwork used to hand Task.Run an empty path, which made the view
                // wait for a tessellation of nothing and log its failure.
                if (!HasArtwork)
                {
                    sprite = null;
                    return true;
                }

                if (_sprite != null)
                {
                    NoteSpriteUse(this);
                    sprite = _sprite;
                    return true;
                }

                if (_spriteFailed)
                {
                    sprite = null;
                    return true;
                }

                // A captured PNG: no vector library, no tessellation, no lock - just a file read
                // and a decode, split across the two threads that can do each.
                if (IsRaster) return TryGetRasterSprite(out sprite);

                if (_preparing == null)
                {
                    var path = ImagePath;
                    _preparing = Task.Run(() => PrepareArtwork(path));
                    sprite = null;
                    return false;
                }

                if (!_preparing.IsCompleted)
                {
                    sprite = null;
                    return false;
                }

                var task = _preparing;
                _preparing = null;

                if (task.IsFaulted)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: '{Path.GetFileName(ImagePath)}' could not be tessellated off-thread " +
                        $"({task.Exception?.GetBaseException().Message}) - trying on the main thread.");
                    sprite = GetSprite();
                    return true;
                }

                if (task.Result == null)
                {
                    // No viewBox, nothing parsed, or too dense at every preset - already logged by
                    // PrepareArtwork, and the same file would give the same answer on any thread.
                    _spriteFailed = true;
                    sprite = null;
                    return true;
                }

                var prepared = task.Result;
                var clock = System.Diagnostics.Stopwatch.StartNew();

                Viewport = prepared.Viewport;
                _sprite = BuildFromPrepared(prepared, this);
                _spriteFailed = _sprite == null;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: map image '{Path.GetFileName(ImagePath)}' tessellated off-thread in " +
                    $"{prepared.Millis} ms (preset {prepared.Preset + 1} of {TessellationPresets.Length}), " +
                    $"sprite built in {clock.ElapsedMilliseconds} ms.");

                if (_sprite != null) NoteSpriteUse(this);
                sprite = _sprite;
                return true;
            }

            /// <summary>
            /// The captured picture's half of <see cref="TryGetSprite"/>: the same contract, with a
            /// file read where the tessellation was.
            ///
            /// The split is where the thread rule falls. Reading 8 MB of PNG off a disk is exactly
            /// what a worker thread is for, and decoding it is not: ImageConversion.LoadImage
            /// uploads a texture through the graphics device and is main-thread only, and calling
            /// it from a worker either throws or corrupts the device state. So the read goes to a
            /// task, the frame after it lands decodes it here, and the caller paints its list in
            /// between - the same "list first, picture a moment later" the SVG path gives.
            /// </summary>
            private bool TryGetRasterSprite(out Sprite sprite)
            {
                if (_loadingBytes == null)
                {
                    var path = ImagePath;
                    _loadingBytes = Task.Run(() => File.ReadAllBytes(path));
                    sprite = null;
                    return false;
                }

                if (!_loadingBytes.IsCompleted)
                {
                    sprite = null;
                    return false;
                }

                var task = _loadingBytes;
                _loadingBytes = null;

                if (task.IsFaulted)
                {
                    // Answered rather than retried: the file is missing, locked or unreadable, and
                    // the next repaint would find it exactly as missing. The view draws the floor's
                    // rectangle instead - see ArtworkFailed.
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: could not read the map picture '{Path.GetFileName(ImagePath)}' " +
                        $"({task.Exception?.GetBaseException().Message}) - drawing its extent instead.");
                    _spriteFailed = true;
                    sprite = null;
                    return true;
                }

                _sprite = BuildRasterSprite(task.Result, this);
                _spriteFailed = _sprite == null;
                if (_sprite != null) NoteSpriteUse(this);

                sprite = _sprite;
                return true;
            }

            /// <summary>Frees the rasterised floor; the next TryGetSprite tessellates again, or
            /// reads and decodes the PNG again.</summary>
            internal void ReleaseSprite()
            {
                if (_sprite != null)
                {
                    // BuildSprite gives a map with gradient fills its own atlas texture, which
                    // destroying the Sprite does not touch; a flat map borrows Unity's shared
                    // white texture, which must not be destroyed.
                    var texture = _sprite.texture;
                    if (texture != null && texture != Texture2D.whiteTexture) UnityEngine.Object.Destroy(texture);
                    UnityEngine.Object.Destroy(_sprite);
                }

                _sprite = null;
                _spriteFailed = false;
                RasterBytes = 0;

                // A read in flight is abandoned rather than awaited: its bytes are for a picture
                // nothing is showing any more, and leaving the task here would make the next
                // TryGetSprite decode them into a texture this layer had just given up.
                _loadingBytes = null;
            }

            public bool Covers(float x, float y, float height)
            {
                foreach (var box in GameBounds)
                {
                    if (box.Contains(x, y, height)) return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Where a place name came from, which is what decides how it is drawn - see
        /// MapView.BuildPlaceLabels.
        ///
        /// <see cref="Place"/> is the default deliberately: it is what a DynamicMaps config's
        /// hand-placed names are, and they must keep the look they have had for releases. The other
        /// two are a capture's own names, collected in bulk by the writer - one map's 41 zone names
        /// in the place-name font overlapped each other and the pins - and they are drawn smaller,
        /// on a plate, and only where they fit.
        /// </summary>
        internal enum MapLabelKind
        {
            /// <summary>Hand-placed by a map's author. The only kind before captures existed.</summary>
            Place = 0,

            /// <summary>An extract, from the capture meta. Worth the space on the map: an exfil is
            /// somewhere you have to go.</summary>
            Exfil,

            /// <summary>A bot zone's cleaned name, from the capture meta. There are dozens per map
            /// and they are the ones that yield.</summary>
            Zone
        }

        /// <summary>A place name the map config carries. The SVGs contain no text at all, so without
        /// these the picture is unlabelled.</summary>
        internal sealed class MapLabel
        {
            /// <summary>What sort of name this is, and so how it is drawn. See
            /// <see cref="MapLabelKind"/>; the default leaves a DynamicMaps label untouched.</summary>
            public MapLabelKind Kind = MapLabelKind.Place;

            public string Text = "";
            public Vector2 Position;

            /// <summary>How high off the ground the place is, from the label's own Position.z - the
            /// same quantity the layers' GameBounds bands are in, so a name can be put on a floor
            /// exactly the way a quest marker is. Mostly 0, but 63 of Interchange's 77 names sit on
            /// the mall's upper storeys.</summary>
            public float Height;

            /// <summary>The angle the name is meant to be written at, for places that run along
            /// something rather than sitting on a point. All 22 of Reserve's names are set to 14.5
            /// to follow the base's grid; eight of Streets' run from -90 to 10.</summary>
            public float Rotation;
        }

        /// <summary>One map DynamicMaps ships: which game maps it covers, and its floors.</summary>
        internal sealed class MapEntry
        {
            public string DisplayName = "";
            public string Attribution = "";

            /// <summary>Internal game ids this map covers ("bigmap"), matched against a quest's
            /// LocationKey. A map can cover several - Factory has a day and a night id.</summary>
            public readonly List<string> InternalNames = new();

            /// <summary>Floors, lowest first. Never empty for an entry that is returned.</summary>
            public readonly List<MapLayer> Layers = new();

            public readonly List<MapLabel> Labels = new();

            public int DefaultLevel;

            /// <summary>The map's declared coordinate rotation, applied to the artwork
            /// (MapView.PlaceArtwork) and to percentage-placed objective pins (MapView.PositionFor).
            /// The artwork-rotation setting adds to it for a map whose data is wrong.</summary>
            public int CoordinateRotation;

            /// <summary>The floor to open on: the config's declared default where that exists, else
            /// the lowest one, so there is always something to draw.</summary>
            public MapLayer DefaultLayer =>
                Layers.FirstOrDefault(layer => layer.Level == DefaultLevel) ?? Layers.FirstOrDefault();

            /// <summary>The floor a point belongs to: the highest one whose declared volumes contain
            /// it, or null when none does. Highest wins because the bands overlap - Customs' ground
            /// floor claims everything from -100 to 100, so a point on the 2nd floor is inside both
            /// and the more specific answer is the useful one.</summary>
            public MapLayer LayerFor(float x, float y, float height)
            {
                MapLayer best = null;

                foreach (var layer in Layers)
                {
                    if (!layer.Covers(x, y, height)) continue;
                    if (best == null || layer.Level > best.Level) best = layer;
                }

                return best;
            }
        }

        private static List<MapEntry> _maps;

        /// <summary>Floors whose sprite is built, oldest use first. Past the ceiling the least
        /// recently viewed is released; the one on screen was just used, so it is never the one.</summary>
        private const int MaxCachedSprites = 6;
        private static readonly List<MapLayer> _spriteUse = new();

        private static void NoteSpriteUse(MapLayer layer)
        {
            _spriteUse.Remove(layer);
            _spriteUse.Add(layer);

            while (_spriteUse.Count > MaxCachedSprites)
            {
                var oldest = _spriteUse[0];
                _spriteUse.RemoveAt(0);
                oldest.ReleaseSprite();
            }
        }

        /// <summary>How much texture memory the cached captured pictures hold. Only the bitmaps
        /// count: a tessellated SVG is a mesh, and the biggest of those is a fraction of one
        /// 3262x3136 floor.</summary>
        internal static long ResidentRasterBytes
        {
            get
            {
                var bytes = 0L;
                foreach (var layer in _spriteUse) bytes += layer.RasterBytes;
                return bytes;
            }
        }

        /// <summary>Drops one floor's picture out of the cache and frees it. For a capture that has
        /// just been replaced by a fresh one: its layer objects are about to be thrown away, and a
        /// released-but-still-listed layer would sit in the LRU holding up to 39 MiB that nothing can
        /// ever show again.</summary>
        internal static void ReleaseLayer(MapLayer layer)
        {
            if (layer == null) return;

            // Only a floor the cache is listing can have a picture to free: every loader here calls
            // NoteSpriteUse the moment it has one, and every eviction releases as it removes. So an
            // unlisted layer has nothing, and skipping it also keeps this method out of Unity
            // altogether for that case - ReleaseSprite compares a Sprite against null, which is
            // Unity's own operator and a native call - which is what lets the capture reader be
            // exercised outside a running game.
            if (_spriteUse.Remove(layer)) layer.ReleaseSprite();
        }

        /// <summary>
        /// Frees every cached picture, or only the bitmaps.
        ///
        /// Called on a profile change (MapView.ResetSession), where the pictures held are the last
        /// profile's and the ceiling of six captured floors is about 235 MiB of texture at the worst.
        /// Bitmaps only by default because they are the memory: an SVG costs a 900 ms tessellation
        /// to get back and a mesh to keep, so throwing those away trades a real cost for almost
        /// nothing.
        /// </summary>
        internal static void ReleaseCachedSprites(bool rasterOnly = true)
        {
            var freed = 0L;
            var floors = 0;

            for (var i = _spriteUse.Count - 1; i >= 0; i--)
            {
                var layer = _spriteUse[i];
                if (rasterOnly && !layer.IsRaster) continue;

                freed += layer.RasterBytes;
                floors++;
                _spriteUse.RemoveAt(i);
                layer.ReleaseSprite();
            }

            if (floors > 0)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: released {floors} cached map floor(s), {freed / (1024f * 1024f):F1} MB of pictures.");
            }
        }

        /// <summary>True when DynamicMaps is installed and at least one map was readable.</summary>
        public static bool Available => Maps.Count > 0;

        public static IReadOnlyList<MapEntry> Maps => _maps ??= Discover();

        /// <summary>The map covering a quest's location, or null. Matched on the internal id rather
        /// than the display name, because the display name is localized and theirs is not.</summary>
        public static MapEntry FindByLocationKey(string locationKey)
        {
            if (string.IsNullOrEmpty(locationKey)) return null;

            return Maps.FirstOrDefault(m =>
                m.InternalNames.Any(n => string.Equals(n, locationKey, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>Where DynamicMaps keeps its marker icons.</summary>
        private const string MarkersFolder = "Markers";

        private static Sprite _questPin;
        private static bool _questPinFailed;

        /// <summary>
        /// The teardrop map pin, from DynamicMaps' own marker set.
        ///
        /// A PNG rather than anything bundled, read in place like everything else here. It is the
        /// game-icons.net "position marker" by Delapouite under CC BY 3.0, which is why the view
        /// credits it. Null when DynamicMaps is not installed, and the view falls back to a glyph.
        ///
        /// Pivoted at the bottom centre so the pin's TIP marks the spot. A pin pivoted in the middle
        /// points at nothing in particular.
        /// </summary>
        public static Sprite QuestPin
        {
            get
            {
                if (_questPin != null || _questPinFailed) return _questPin;
                _questPinFailed = true;

                Texture2D texture = null;

                try
                {
                    var folder = ModFolder;
                    if (string.IsNullOrEmpty(folder)) return null;

                    var path = Path.Combine(Path.Combine(folder, MarkersFolder), "quest.png");
                    if (!File.Exists(path)) return null;

                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(File.ReadAllBytes(path)))
                    {
                        // The texture is a native allocation; a failed decode used to leave it.
                        UnityEngine.Object.Destroy(texture);
                        return null;
                    }

                    texture.filterMode = FilterMode.Bilinear;

                    _questPin = Sprite.Create(
                        texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0f));

                    _questPinFailed = false;
                    return _questPin;
                }
                catch (Exception ex)
                {
                    if (texture != null) UnityEngine.Object.Destroy(texture);
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: could not load the quest pin icon ({ex.Message}) - using a glyph.");
                    return null;
                }
            }
        }

        /// <summary>DynamicMaps' own folder, resolved from ours rather than hardcoded so it follows
        /// a non-standard install.</summary>
        private static string ModFolder
        {
            get
            {
                var plugins = Path.GetDirectoryName(
                    Path.GetDirectoryName(typeof(DynamicMapsLibrary).Assembly.Location));

                return string.IsNullOrEmpty(plugins) ? null : Path.Combine(plugins, MapsModFolder);
            }
        }

        private static List<MapEntry> Discover()
        {
            var maps = new List<MapEntry>();

            try
            {
                var pluginsFolder = Path.GetDirectoryName(
                    Path.GetDirectoryName(typeof(DynamicMapsLibrary).Assembly.Location));

                if (string.IsNullOrEmpty(pluginsFolder)) return maps;

                var mapsRoot = Path.Combine(Path.Combine(pluginsFolder, MapsModFolder), "Maps");
                if (!Directory.Exists(mapsRoot))
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: DynamicMaps is not installed - the Maps view will show its quest list only.");
                    return maps;
                }

                foreach (var folder in Directory.GetDirectories(mapsRoot))
                {
                    var config = Directory.GetFiles(folder, "*.jsonc").FirstOrDefault()
                                 ?? Directory.GetFiles(folder, "*.json").FirstOrDefault();

                    if (config == null) continue;

                    var entry = ParseConfig(config, mapsRoot);
                    if (entry != null) maps.Add(entry);
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: found {maps.Count} DynamicMaps maps " +
                    $"({maps.Sum(m => m.Layers.Count)} floors) to draw from.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not read DynamicMaps' maps ({ex.Message}) - showing the quest list only.");
            }

            return maps;
        }

        /// <summary>
        /// Parses one map config. The files are .jsonc - JSON with // comments and trailing commas -
        /// so they are read through a reader that allows both rather than with a strict parser. A
        /// naive comment strip would also eat the // inside the AuthorLink URL.
        /// </summary>
        private static MapEntry ParseConfig(string configPath, string mapsRoot)
        {
            try
            {
                using var reader = new JsonTextReader(new StreamReader(configPath));
                var root = JObject.Load(reader);

                var entry = new MapEntry
                {
                    DisplayName = (string)root["DisplayName"] ?? Path.GetFileNameWithoutExtension(configPath),
                    Attribution = (string)root["Author"] ?? "",
                    DefaultLevel = (int?)root["DefaultLevel"] ?? 0,
                    CoordinateRotation = (int?)root["CoordinateRotation"] ?? 0
                };

                foreach (var name in root["MapInternalNames"] ?? Enumerable.Empty<JToken>())
                {
                    var value = (string)name;
                    if (!string.IsNullOrEmpty(value)) entry.InternalNames.Add(value);
                }

                // The top-level Bounds is the fallback for a layer that declares none of its own.
                ReadVector2Pair(root["Bounds"] as JObject, out var mapMin, out var mapMax);

                ReadLayers(root["Layers"] as JObject, mapsRoot, mapMin, mapMax, entry);
                ReadLabels(root["Labels"] as JArray, entry);

                return entry.Layers.Count == 0 ? null : entry;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: skipped DynamicMaps config '{Path.GetFileName(configPath)}' ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Every floor, lowest first. All of them rather than only ground level - a quest
        /// item underground needs the underground picture for its marker to mean anything.</summary>
        private static void ReadLayers(
            JObject layers, string mapsRoot, Vector2 mapMin, Vector2 mapMax, MapEntry entry)
        {
            if (layers == null) return;

            // The paths in the config are relative to the DynamicMaps folder, not to Maps/.
            var modRoot = Path.GetDirectoryName(mapsRoot);

            foreach (var property in layers.Properties())
            {
                var value = property.Value;
                var path = (string)value["ImagePath"];

                if (string.IsNullOrEmpty(path)) continue;

                var full = Path.Combine(modRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;

                var stem = Path.GetFileNameWithoutExtension(full) ?? "";
                var dash = stem.IndexOf('-');

                var layer = new MapLayer
                {
                    Name = property.Name,
                    Level = (int?)value["Level"] ?? 0,
                    ImagePath = full,
                    FloorName = dash >= 0 ? stem.Substring(dash + 1) : "",
                    BoundsMin = mapMin,
                    BoundsMax = mapMax
                };

                if (ReadVector2Pair(value["ImageBounds"] as JObject, out var layerMin, out var layerMax))
                {
                    layer.BoundsMin = layerMin;
                    layer.BoundsMax = layerMax;
                }

                foreach (var box in value["GameBounds"] ?? Enumerable.Empty<JToken>())
                {
                    if (ReadVector3Pair(box as JObject, out var boxMin, out var boxMax))
                        layer.GameBounds.Add(new GameBox(boxMin, boxMax));
                }

                if (layer.HasBounds) entry.Layers.Add(layer);
            }

            entry.Layers.Sort((a, b) => a.Level.CompareTo(b.Level));
        }

        private static void ReadLabels(JArray labels, MapEntry entry)
        {
            if (labels == null) return;

            foreach (var label in labels)
            {
                var text = (string)label["Text"];
                var position = label["Position"];

                if (string.IsNullOrEmpty(text) || position == null) continue;

                entry.Labels.Add(new MapLabel
                {
                    Text = text,
                    Position = new Vector2((float?)position["x"] ?? 0f, (float?)position["y"] ?? 0f),
                    Height = (float?)position["z"] ?? 0f,
                    Rotation = (float?)label["DegreesRotation"] ?? 0f
                });
            }
        }

        private static bool ReadVector2Pair(JObject node, out Vector2 min, out Vector2 max)
        {
            min = default;
            max = default;

            var minNode = node?["Min"];
            var maxNode = node?["Max"];
            if (minNode == null || maxNode == null) return false;

            min = new Vector2((float?)minNode["x"] ?? 0f, (float?)minNode["y"] ?? 0f);
            max = new Vector2((float?)maxNode["x"] ?? 0f, (float?)maxNode["y"] ?? 0f);
            return true;
        }

        private static bool ReadVector3Pair(JObject node, out Vector3 min, out Vector3 max)
        {
            min = default;
            max = default;

            var minNode = node?["Min"];
            var maxNode = node?["Max"];
            if (minNode == null || maxNode == null) return false;

            min = new Vector3((float?)minNode["x"] ?? 0f, (float?)minNode["y"] ?? 0f, (float?)minNode["z"] ?? 0f);
            max = new Vector3((float?)maxNode["x"] ?? 0f, (float?)maxNode["y"] ?? 0f, (float?)maxNode["z"] ?? 0f);
            return true;
        }

        /// <summary>
        /// The SVG's viewBox, straight out of the file's opening tag.
        ///
        /// SVGParser exposes this as SceneInfo.SceneViewport, but the version the game ships leaves
        /// it zero-sized for every one of these files. Reading the attribute is two lines and cannot
        /// be quietly empty, and this is the rectangle the layer's ImageBounds describes, so
        /// everything drawn on the map depends on getting it.
        ///
        /// Only the head of the file is scanned: the tag is the first element, and these run to
        /// 340KB.
        /// </summary>
        private static Rect ReadViewBox(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text, @"<svg[^>]*\sviewBox\s*=\s*[""']([^""']+)[""']",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!match.Success) return default;

            var parts = match.Groups[1].Value.Split(
                new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 4) return default;

            return new Rect(
                ParseFloat(parts[0]), ParseFloat(parts[1]),
                ParseFloat(parts[2]), ParseFloat(parts[3]));
        }

        /// <summary>Invariant culture, because an SVG's numbers use a dot whatever the machine's
        /// locale says and a comma-decimal machine would otherwise read 1470.3 as 14703.</summary>
        private static float ParseFloat(string value) =>
            float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0f;

        /// <summary>
        /// Reports the rectangles the placement depends on, once per layer.
        ///
        /// This exists because four attempts at aligning the map were each reasoned from pixel
        /// positions in a screenshot, and each was wrong in a way the next one only partly fixed.
        /// The two facts that actually decide it - whether the sprite came back pinned to the
        /// viewBox, and which way the tessellated ink's Y runs - are unknowable from outside the
        /// running game and obvious from inside it.
        /// </summary>
        private static void LogArtworkGeometry(MapLayer layer, Sprite sprite)
        {
            if (sprite == null) return;

            var pinned =
                Mathf.Abs(sprite.rect.width - layer.Viewport.width) < 1f &&
                Mathf.Abs(sprite.rect.height - layer.Viewport.height) < 1f;

            Plugin.LogSource?.LogDebug(
                $"QuestTree map geometry [{layer.Name}] " +
                $"viewBox={Describe(layer.Viewport)} " +
                $"spriteRect={Describe(sprite.rect)} " +
                $"spriteBounds={Describe(sprite.bounds)} " +
                $"pivot=({sprite.pivot.x:F1},{sprite.pivot.y:F1}) " +
                $"ppu={sprite.pixelsPerUnit:F1} " +
                $"bounds=({layer.BoundsMin.x:F0},{layer.BoundsMin.y:F0})-" +
                $"({layer.BoundsMax.x:F0},{layer.BoundsMax.y:F0}) " +
                $"viewBoxPinned={pinned}");
        }

        private static string Describe(Rect r) =>
            $"[x{r.x:F1} y{r.y:F1} w{r.width:F1} h{r.height:F1}]";

        private static string Describe(Bounds b) =>
            $"[c({b.center.x:F2},{b.center.y:F2}) s({b.size.x:F2},{b.size.y:F2})]";

        /// <summary>Tessellation presets, coarsening in order. Lifted from DynamicMaps, which
        /// walks them until the mesh fits Unity's index budget - a 340KB map can otherwise blow past
        /// it and produce a broken mesh rather than a coarse one.</summary>
        private static readonly VectorUtils.TessellationOptions[] TessellationPresets =
        {
            new() { StepDistance = 1.5f, MaxCordDeviation = 0.2f, MaxTanAngleDeviation = 0.2f, SamplingStepSize = 0.04f },
            new() { StepDistance = 2f, MaxCordDeviation = 0.3f, MaxTanAngleDeviation = 0.25f, SamplingStepSize = 0.05f },
            new() { StepDistance = 4f, MaxCordDeviation = 0.4f, MaxTanAngleDeviation = 0.3f, SamplingStepSize = 0.06f },
            new() { StepDistance = 6f, MaxCordDeviation = 0.5f, MaxTanAngleDeviation = 0.4f, SamplingStepSize = 0.07f },
            new() { StepDistance = 8f, MaxCordDeviation = 0.6f, MaxTanAngleDeviation = 0.5f, SamplingStepSize = 0.08f }
        };

        /// <summary>Unity meshes index vertices with 16 bits, so this is the ceiling a tessellation
        /// has to come in under.</summary>
        private const int VertexBudget = 65500;

        /// <summary>
        /// Turns an SVG into a Sprite, the way DynamicMaps does it.
        ///
        /// This is a faithful copy of its SvgUtils.LoadSvgFromPath rather than something derived
        /// from first principles, and deliberately so: six attempts at aligning this map were spent
        /// reverse-engineering a transform from screenshots, when the mod that already renders these
        /// exact files correctly ships its loader in a readable assembly. Every argument here was
        /// wrong before, and the one that mattered was the rect.
        ///
        /// Passing the viewBox to BuildSprite is the whole alignment fix. Without it the sprite is
        /// sized to the artwork's ink - the bounding box of what happens to be drawn - which is a
        /// different rectangle on every layer and bears no relation to the ImageBounds the map's
        /// coordinates use. A floor that draws only the few buildings having that floor inks a
        /// fraction of its viewBox, and was being stretched to fill the whole map.
        ///
        /// ViewportOptions.OnlyApplyRootViewBox is what puts the geometry in viewBox space to begin
        /// with, and flipYAxis reconciles SVG's downward y with Unity's upward y.
        /// </summary>
        private static Sprite LoadSvgSprite(string path, MapLayer layer)
        {
            try
            {
                var prepared = PrepareArtwork(path);
                if (prepared == null) return null;

                layer.Viewport = prepared.Viewport;
                return BuildFromPrepared(prepared, layer);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not render map image '{Path.GetFileName(path)}' ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Everything about a floor's picture that does not need Unity's thread: the
        /// viewBox and the tessellated geometry, plus how it was made.</summary>
        internal sealed class PreparedArtwork
        {
            public Rect Viewport;
            public List<VectorUtils.Geometry> Geometry;
            public int Preset;
            public long Millis;
        }

        /// <summary>The thread-free half of the loader: file, viewBox, parse, tessellate. Null
        /// when the file has no viewBox, parses to nothing, or is too dense at every preset; a
        /// parse exception propagates to the caller, which decides whether to log or fall back.
        /// Warnings go through the logger, which is safe from a worker thread.</summary>
        /// <summary>One tessellation at a time, on any thread. Unity.VectorGraphics is not
        /// re-entrant: TessellateScene clears and walks a process-wide static clip stack, and
        /// LibTessDotNet pools its vertices on unsynchronised static free-lists. Two floors
        /// tessellating together - a floor switch while one is in flight - would throw or, worse,
        /// share pooled vertices and build a silently wrong mesh. The synchronous fallback goes
        /// through here too, so it cannot race a worker either.</summary>
        private static readonly object TessellateLock = new();

        private static PreparedArtwork PrepareArtwork(string path)
        {
            lock (TessellateLock)
            {
                return PrepareArtworkLocked(path);
            }
        }

        private static PreparedArtwork PrepareArtworkLocked(string path)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var text = File.ReadAllText(path);

            // Read from the text, not from SceneInfo.SceneViewport: the parser the game ships
            // leaves that zero-sized for every one of these files. DynamicMaps parses the
            // attribute itself for the same reason, and rejects the layer when it is missing.
            var viewport = ReadViewBox(text);
            if (viewport.width <= 0f || viewport.height <= 0f)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: '{Path.GetFileName(path)}' has no viewBox, so it cannot be placed.");
                return null;
            }

            using var reader = new StringReader(text);
            var scene = SVGParser.ImportSVG(
                reader, ViewportOptions.OnlyApplyRootViewBox, 0f, 1f, 0, 0);

            if (scene.Scene?.Root == null) return null;

            for (var i = 0; i < TessellationPresets.Length; i++)
            {
                var geometry = VectorUtils.TessellateScene(scene.Scene, TessellationPresets[i], scene.NodeOpacity);
                if (geometry == null || geometry.Count == 0) return null;

                if (OverBudget(geometry)) continue;

                return new PreparedArtwork
                {
                    Viewport = viewport,
                    Geometry = geometry,
                    Preset = i,
                    Millis = clock.ElapsedMilliseconds
                };
            }

            Plugin.LogSource?.LogWarning(
                $"QuestTree: '{Path.GetFileName(path)}' is too dense to tessellate within " +
                $"{VertexBudget} vertices at any preset.");

            return null;
        }

        /// <summary>The Unity half: the Sprite (and its Mesh, and a Texture2D when the map has
        /// gradient fills) from geometry already tessellated. Main thread only.</summary>
        private static Sprite BuildFromPrepared(PreparedArtwork prepared, MapLayer layer)
        {
            var sprite = VectorUtils.BuildSprite(
                prepared.Geometry, prepared.Viewport, 1f, VectorUtils.Alignment.Center,
                Vector2.zero, 32, true);

            LogArtworkGeometry(layer, sprite);
            return sprite;
        }

        /// <summary>The blocking raster path: read the file and decode it. Main thread only, like
        /// <see cref="BuildRasterSprite"/>, and for the same reason.</summary>
        private static Sprite LoadRasterSprite(string path, MapLayer layer)
        {
            try
            {
                return BuildRasterSprite(File.ReadAllBytes(path), layer);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not read the map picture '{Path.GetFileName(path)}' " +
                    $"({ex.Message}) - drawing its extent instead.");
                return null;
            }
        }

        /// <summary>
        /// A captured PNG's bytes, decoded into a sprite. MAIN THREAD ONLY.
        ///
        /// TRANSPARENCY, which everything below has to survive: a capture is an RGBA PNG whose alpha
        /// is 0 outside the walkable area, so the panel shows through where the map has nothing to
        /// say. A host's cached set may instead be JPEG, which has no alpha and is opaque; both go
        /// through this one path.
        ///
        /// Four arguments here are decisions rather than defaults:
        ///
        /// RGBA32 as the constructed format. LoadImage reinitialises the texture to suit the file it
        /// decodes - an alpha PNG becomes RGBA32, a JPEG becomes RGB24 - so this is the format only
        /// until the next line. It is RGBA32 anyway because that is the one choice that cannot lose
        /// the alpha if a Unity version ever declines to reformat: a picture decoded into an RGB24
        /// texture would come out opaque, the map would be a rectangle of black or grey over the
        /// panel outside its walkable area, and nothing about it would look like a bug in a format
        /// argument. The accounting reads the format BACK off the texture afterwards, so an opaque
        /// JPEG is still counted at three bytes a pixel - see <see cref="TextureBytes"/>.
        ///
        /// Mipmaps off, because the picture is stretched onto its floor's world rectangle and the
        /// view's zoom is a container scale - there is no minification chain worth 33 % more memory
        /// on a 39 MiB texture. Bilinear filtering, so zooming in blurs rather than blocks.
        ///
        /// markNonReadable, which drops the CPU-side copy the decode leaves behind. That copy is the
        /// same size as the texture - 3262x3136 at RGBA32 is 39 MiB, which is the biggest a floor of a
        /// big map gets, and the cache holds six - and nothing here ever reads a pixel back.
        ///
        /// SpriteMeshType.FullRect rather than the default tight mesh, which matters twice over now.
        /// A tight mesh is traced from the texture's ALPHA, which a non-readable texture cannot be
        /// asked for - and on a picture that is deliberately transparent around its edges, tracing
        /// it would crop the sprite to the walkable area and then stretch THAT onto the floor's
        /// rectangle, moving every metre of the map. FullRect keeps the rectangle the capture
        /// recorded, which is the only rectangle its coordinates mean anything in.
        ///
        /// The pivot and pixels-per-unit are formalities: MapView draws this through a UI Image
        /// sized to the floor's bounds, which stretches the sprite's rect onto that rect and
        /// consults neither. They are centred and 100 so the sprite is well-formed for anything
        /// that does. The Image's white tint is full alpha, so the picture's own alpha is what
        /// reaches the screen, and the default UI material blends it - see MapView.BuildMapViewport.
        /// </summary>
        private static Sprite BuildRasterSprite(byte[] bytes, MapLayer layer)
        {
            var name = Path.GetFileName(layer.ImagePath);

            if (bytes == null || bytes.Length == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the map picture '{name}' is empty - drawing its extent instead.");
                return null;
            }

            Texture2D texture = null;

            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();

                // RGBA32, not RGB24: see the remarks. LoadImage picks the file's own format, and
                // this is the one that cannot discard alpha if it ever does not.
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);

                if (!texture.LoadImage(bytes, markNonReadable: true))
                {
                    // The texture is a native allocation, so a failed decode has to destroy it -
                    // the same trap QuestPin above fell into once.
                    UnityEngine.Object.Destroy(texture);
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the map picture '{name}' could not be decoded " +
                        $"({bytes.Length} bytes) - drawing its extent instead.");
                    return null;
                }

                texture.filterMode = FilterMode.Bilinear;
                texture.wrapMode = TextureWrapMode.Clamp;

                var sprite = Sprite.Create(
                    texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);

                if (sprite == null)
                {
                    UnityEngine.Object.Destroy(texture);
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the map picture '{name}' decoded but no sprite could be made of it.");
                    return null;
                }

                // Not a viewBox - there is no SVG here - but the same thing it stands for: the
                // pixel rectangle that lands on the layer's bounds. LogArtworkGeometry reads it.
                layer.Viewport = new Rect(0f, 0f, texture.width, texture.height);
                layer.RasterBytes = TextureBytes(texture);

                // The FORMAT is in the line because it is the one fact that decides both the cost
                // and whether the picture can be transparent at all: RGBA32 is our own capture with
                // its alpha intact, RGB24 is an opaque one (a host's JPEG, or a PNG saved without an
                // alpha channel), and anything else is a Unity version doing something unexpected.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: map picture '{name}' {texture.width}x{texture.height} {texture.format} " +
                    $"decoded in {clock.ElapsedMilliseconds} ms " +
                    $"({layer.RasterBytes / (1024f * 1024f):F1} MB); " +
                    $"{(ResidentRasterBytes + layer.RasterBytes) / (1024f * 1024f):F1} MB of pictures " +
                    $"resident, ceiling {MaxCachedSprites} floors.");

                LogArtworkGeometry(layer, sprite);
                return sprite;
            }
            catch (Exception ex)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not build the map picture '{name}' ({ex.Message}) - " +
                    $"drawing its extent instead.");
                return null;
            }
        }

        /// <summary>
        /// What a decoded picture costs, from the format the texture ENDED UP in rather than from the
        /// one it was constructed with or the file's extension: our captures are RGBA PNGs and decode
        /// to RGBA32 at four bytes a pixel (39 MiB for a 3262x3136 floor, the largest the capture's
        /// memory budget allows), a host's JPEG and any PNG without an alpha channel decode to RGB24 at
        /// three (29 MiB for the same floor, and a host's copy is downscaled to 2048 long side anyway).
        ///
        /// An unrecognised format is counted at four, so the number in the log is never optimistic.
        /// </summary>
        private static long TextureBytes(Texture2D texture)
        {
            var bytesPerPixel = texture.format switch
            {
                // The two LoadImage actually produces for our files.
                TextureFormat.RGBA32 => 4,
                TextureFormat.RGB24 => 3,

                // The rest are here so an unexpected answer is still counted rather than guessed at.
                TextureFormat.ARGB32 => 4,
                TextureFormat.BGRA32 => 4,
                TextureFormat.RGBAHalf => 8,
                TextureFormat.RGBAFloat => 16,
                TextureFormat.R8 => 1,
                TextureFormat.Alpha8 => 1,
                _ => 4
            };

            return (long)texture.width * texture.height * bytesPerPixel;
        }

        private static bool OverBudget(List<VectorUtils.Geometry> geometry)
        {
            var vertices = 0;

            foreach (var part in geometry)
            {
                vertices += part.Vertices?.Length ?? 0;
                if (vertices > VertexBudget) return true;
            }

            return false;
        }
    }
}
