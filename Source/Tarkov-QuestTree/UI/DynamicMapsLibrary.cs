using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            public Vector2 BoundsMin;
            public Vector2 BoundsMax;

            public readonly List<GameBox> GameBounds = new();

            public bool HasBounds => BoundsMax.x > BoundsMin.x && BoundsMax.y > BoundsMin.y;

            public Vector2 BoundsSize => BoundsMax - BoundsMin;
            public Vector2 BoundsCentre => (BoundsMin + BoundsMax) * 0.5f;

            /// <summary>The SVG's viewBox, in SVG units. This is the rectangle the layer's
            /// ImageBounds describes, and so the one that has to land on it.</summary>
            public Rect Viewport;

            /// <summary>The tessellated artwork's own bounding box, in the same SVG units - the
            /// rectangle BuildSprite gives the sprite, and therefore what SVGImage stretches into
            /// whatever rect it is given. It is NOT the viewBox: a floor layer only draws the
            /// buildings that have that floor, so GroundZero's third floor inks 11% of its viewBox,
            /// while Customs' ground floor overhangs its own by 42% in height with extract routes.
            /// Placing the picture correctly needs both.</summary>
            public Rect Ink;

            public bool HasArtworkBounds =>
                Viewport.width > 0f && Viewport.height > 0f && Ink.width > 0f && Ink.height > 0f;

            private Sprite _sprite;
            private bool _spriteFailed;

            /// <summary>Rasterised on first use and kept - tessellating a 340KB SVG is not something
            /// to repeat every time a floor is switched back to.</summary>
            public Sprite GetSprite()
            {
                if (_sprite != null || _spriteFailed) return _sprite;

                _sprite = LoadSvgSprite(ImagePath, this);
                _spriteFailed = _sprite == null;
                return _sprite;
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

        /// <summary>A place name the map config carries. The SVGs contain no text at all, so without
        /// these the picture is unlabelled.</summary>
        internal sealed class MapLabel
        {
            public string Text = "";
            public Vector2 Position;
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
                    DefaultLevel = (int?)root["DefaultLevel"] ?? 0
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

                var layer = new MapLayer
                {
                    Name = property.Name,
                    Level = (int?)value["Level"] ?? 0,
                    ImagePath = full,
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
                    Position = new Vector2((float?)position["x"] ?? 0f, (float?)position["y"] ?? 0f)
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
        private static Rect ReadViewBox(string path)
        {
            try
            {
                var head = new char[2048];
                using var reader = new StreamReader(path);
                var read = reader.Read(head, 0, head.Length);

                var match = System.Text.RegularExpressions.Regex.Match(
                    new string(head, 0, read),
                    @"viewBox\s*=\s*[""']\s*([-\d.eE+]+)[\s,]+([-\d.eE+]+)[\s,]+([-\d.eE+]+)[\s,]+([-\d.eE+]+)");

                if (!match.Success) return default;

                return new Rect(
                    ParseFloat(match.Groups[1].Value), ParseFloat(match.Groups[2].Value),
                    ParseFloat(match.Groups[3].Value), ParseFloat(match.Groups[4].Value));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not read the viewBox of '{Path.GetFileName(path)}' ({ex.Message}).");
                return default;
            }
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

            Plugin.LogSource?.LogInfo(
                $"QuestTree map geometry [{layer.Name}] " +
                $"viewBox={Describe(layer.Viewport)} " +
                $"ink={Describe(layer.Ink)} " +
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

        /// <summary>
        /// Turns an SVG into a Sprite using Unity's own vector graphics package, which the game
        /// already ships (Unity.VectorGraphics.dll in EscapeFromTarkov_Data/Managed) - the same API
        /// DynamicMaps itself uses. No third-party rasteriser and no bundled assets.
        ///
        /// The sprite's own extent does not have to line up with anything, because the view stretches
        /// it to a rect sized in map units - which is exactly what DynamicMaps does with its own.
        /// </summary>
        private static Sprite LoadSvgSprite(string path, MapLayer layer)
        {
            try
            {
                using var stream = new StreamReader(path);
                var scene = SVGParser.ImportSVG(stream);

                if (scene.Scene?.Root == null) return null;

                // Step sizes govern how finely curves are subdivided. These are deliberately coarse:
                // the map is shown at panel size, and a finer tessellation on a 340KB SVG costs
                // noticeably more time for detail nobody can see here.
                var options = new VectorUtils.TessellationOptions
                {
                    StepDistance = 10f,
                    MaxCordDeviation = 0.5f,
                    MaxTanAngleDeviation = 0.1f,
                    SamplingStepSize = 0.01f
                };

                var geometry = VectorUtils.TessellateScene(scene.Scene, options);
                if (geometry == null || geometry.Count == 0) return null;

                // The viewBox is read out of the file rather than taken from SceneViewport, which
                // this version of the parser leaves empty - logged as w0.0 h0.0 for every map, which
                // is what silently disabled the previous attempt at this and sent it down its
                // fallback path.
                layer.Viewport = ReadViewBox(path);

                // The artwork's own extent. Each geometry's vertices are in its own local space, so
                // WorldTransform has to be applied before they can be compared with the viewBox.
                // Confirmed against the running game: Woods tessellates to y -30.9 height 1490.8,
                // matching the file to a decimal, so the ink shares the viewBox's y-down space and
                // there is no flip to undo.
                layer.Ink = VectorUtils.Bounds(
                    geometry.SelectMany(part => part.Vertices.Select(part.WorldTransform.MultiplyPoint)));

                var sprite = VectorUtils.BuildSprite(
                    geometry, 100f, VectorUtils.Alignment.Center, Vector2.zero, 128);

                LogArtworkGeometry(layer, sprite);

                return sprite;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not render map image '{Path.GetFileName(path)}' ({ex.Message}).");
                return null;
            }
        }
    }
}
