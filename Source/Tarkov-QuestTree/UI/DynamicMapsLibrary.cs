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
    /// Everything degrades: no DynamicMaps, no unreadable config and no failed SVG parse may stop
    /// the Maps view rendering its list.
    /// </summary>
    internal static class DynamicMapsLibrary
    {
        /// <summary>Where DynamicMaps installs. Resolved relative to our own plugin folder rather
        /// than hardcoded, so it follows a non-standard SPT install.</summary>
        private const string MapsModFolder = "mpstark-dynamicmaps";

        /// <summary>One map DynamicMaps ships: which game maps it covers, and where its image is.</summary>
        internal sealed class MapEntry
        {
            public string DisplayName = "";
            public string Attribution = "";

            /// <summary>Internal game ids this map covers ("bigmap"), matched against a quest's
            /// LocationKey. A map can cover several - Factory has a day and a night id.</summary>
            public readonly List<string> InternalNames = new();

            /// <summary>The layer chosen to display: ground level where one is marked, else the
            /// first. Showing every layer would need a layer picker; ground level is the one that
            /// answers "where is this on the map".</summary>
            public string ImagePath = "";

            /// <summary>The world-coordinate rectangle the image covers, as (minX, minZ) to
            /// (maxX, maxZ). This is what turns a spawn position into a point on the picture.
            /// Zero-sized when the config did not declare Bounds, which is the signal not to draw
            /// markers rather than to draw them somewhere invented.</summary>
            public Vector2 BoundsMin;
            public Vector2 BoundsMax;

            public bool HasBounds => BoundsMax.x > BoundsMin.x && BoundsMax.y > BoundsMin.y;

            /// <summary>Where a world position lands on the image, as 0-1 across and up. Verified
            /// against the DynamicMaps source rather than guessed: its rotation is applied to the
            /// map CONTAINER's transform, and markers are placed in unrotated world (x, z) against
            /// these bounds - which is also what the data says, since the declared rotation puts
            /// only a quarter of Sandbox's markers inside its bounds and none of Labs', while no
            /// rotation puts every marker on every map inside.</summary>
            public Vector2 Normalize(float worldX, float worldZ) => new(
                (worldX - BoundsMin.x) / (BoundsMax.x - BoundsMin.x),
                (worldZ - BoundsMin.y) / (BoundsMax.y - BoundsMin.y));

            /// <summary>Width divided by height of the area the image covers. The map rect is built
            /// to this so the picture fills it exactly - with letterboxing the drawn rectangle would
            /// be unknown, and every marker would sit at the wrong place by the size of the bars.</summary>
            public float AspectRatio =>
                (BoundsMax.y - BoundsMin.y) <= 0f
                    ? 1f
                    : (BoundsMax.x - BoundsMin.x) / (BoundsMax.y - BoundsMin.y);

            private Sprite _sprite;
            private bool _spriteFailed;

            /// <summary>Rasterised on first use and kept - tessellating a 100KB SVG is not something
            /// to repeat every time the tab is opened.</summary>
            public Sprite GetSprite()
            {
                if (_sprite != null || _spriteFailed) return _sprite;

                _sprite = LoadSvgSprite(ImagePath);
                _spriteFailed = _sprite == null;
                return _sprite;
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

                Plugin.LogSource?.LogInfo($"QuestTree: found {maps.Count} DynamicMaps maps to draw from.");
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
        /// so they are read through a reader with both allowed rather than with a strict parser.
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
                    Attribution = (string)root["Author"] ?? ""
                };

                foreach (var name in root["MapInternalNames"] ?? Enumerable.Empty<JToken>())
                {
                    var value = (string)name;
                    if (!string.IsNullOrEmpty(value)) entry.InternalNames.Add(value);
                }

                entry.ImagePath = ResolveImagePath(root["Layers"] as JObject, mapsRoot);
                ReadBounds(root["Bounds"] as JObject, entry);

                return string.IsNullOrEmpty(entry.ImagePath) ? null : entry;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: skipped DynamicMaps config '{Path.GetFileName(configPath)}' ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Reads the world rectangle the image covers. Left zero-sized when absent, which
        /// the marker code reads as "do not place markers on this map".</summary>
        private static void ReadBounds(JObject bounds, MapEntry entry)
        {
            if (bounds == null) return;

            var min = bounds["Min"];
            var max = bounds["Max"];
            if (min == null || max == null) return;

            entry.BoundsMin = new Vector2((float?)min["x"] ?? 0f, (float?)min["y"] ?? 0f);
            entry.BoundsMax = new Vector2((float?)max["x"] ?? 0f, (float?)max["y"] ?? 0f);
        }

        /// <summary>Picks the layer to show: the one at level 0 where there is one, else the first.
        /// Level 0 is ground level, which is what someone means by "the map".</summary>
        private static string ResolveImagePath(JObject layers, string mapsRoot)
        {
            if (layers == null) return "";

            // The paths in the config are relative to the DynamicMaps folder, not to Maps/.
            var modRoot = Path.GetDirectoryName(mapsRoot);

            string first = null;

            foreach (var layer in layers.Properties())
            {
                var path = (string)layer.Value["ImagePath"];
                if (string.IsNullOrEmpty(path)) continue;

                var full = Path.Combine(modRoot, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;

                first ??= full;

                var level = layer.Value["Level"];
                if (level != null && (int)level == 0) return full;
            }

            return first ?? "";
        }

        /// <summary>
        /// Turns an SVG into a Sprite using Unity's own vector graphics package, which the game
        /// already ships (Unity.VectorGraphics.dll in EscapeFromTarkov_Data/Managed) - the same API
        /// DynamicMaps itself uses. No third-party rasteriser and no bundled assets.
        /// </summary>
        private static Sprite LoadSvgSprite(string path)
        {
            try
            {
                using var stream = new StreamReader(path);
                var scene = SVGParser.ImportSVG(stream);

                if (scene.Scene?.Root == null) return null;

                // Step sizes govern how finely curves are subdivided. These are deliberately coarse:
                // the map is shown at panel size, not zoomed into, and a finer tessellation on a
                // 100KB SVG costs noticeably more time for detail nobody can see here.
                var options = new VectorUtils.TessellationOptions
                {
                    StepDistance = 10f,
                    MaxCordDeviation = 0.5f,
                    MaxTanAngleDeviation = 0.1f,
                    SamplingStepSize = 0.01f
                };

                var geometry = VectorUtils.TessellateScene(scene.Scene, options);
                if (geometry == null || geometry.Count == 0) return null;

                return VectorUtils.BuildSprite(geometry, 100f, VectorUtils.Alignment.Center, Vector2.zero, 128);
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
