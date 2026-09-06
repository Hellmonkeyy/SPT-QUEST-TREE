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

            public bool HasArtworkBounds => Viewport.width > 0f && Viewport.height > 0f;

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

            /// <summary>The map's declared coordinate rotation. Read but not yet applied: it is the
            /// prime suspect for how the artwork is turned relative to game coordinates, and the
            /// artwork-rotation setting exists to confirm that before it is wired in.</summary>
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

            Plugin.LogSource?.LogInfo(
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
                var text = File.ReadAllText(path);

                // Read from the text, not from SceneInfo.SceneViewport: the parser the game ships
                // leaves that zero-sized for every one of these files. DynamicMaps parses the
                // attribute itself for the same reason, and rejects the layer when it is missing.
                layer.Viewport = ReadViewBox(text);
                if (layer.Viewport.width <= 0f || layer.Viewport.height <= 0f)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: '{Path.GetFileName(path)}' has no viewBox, so it cannot be placed.");
                    return null;
                }

                using var reader = new StringReader(text);
                var scene = SVGParser.ImportSVG(
                    reader, ViewportOptions.OnlyApplyRootViewBox, 0f, 1f, 0, 0);

                if (scene.Scene?.Root == null) return null;

                foreach (var preset in TessellationPresets)
                {
                    var geometry = VectorUtils.TessellateScene(scene.Scene, preset, scene.NodeOpacity);
                    if (geometry == null || geometry.Count == 0) return null;

                    if (OverBudget(geometry)) continue;

                    var sprite = VectorUtils.BuildSprite(
                        geometry, layer.Viewport, 1f, VectorUtils.Alignment.Center,
                        Vector2.zero, 32, true);

                    LogArtworkGeometry(layer, sprite);
                    return sprite;
                }

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: '{Path.GetFileName(path)}' is too dense to tessellate within " +
                    $"{VertexBudget} vertices at any preset.");

                return null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not render map image '{Path.GetFileName(path)}' ({ex.Message}).");
                return null;
            }
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
