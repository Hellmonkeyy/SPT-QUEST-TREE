using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// Where the map for a location comes from - the one place that decides, so nothing else has to.
    ///
    /// The Maps view used to ask <see cref="DynamicMapsLibrary"/> directly, which meant a location
    /// that mod does not ship (every modded map, and every map at all on an install without it) had
    /// no map: no bounds, no floors, no pins, only the sidebar list. The server now harvests each
    /// map's real world rectangle and its floor bands in raid and sends them on the marker set, so a
    /// map can be drawn - to scale, with its pins in the right places - before anyone has a picture
    /// of it. That is what <see cref="Synthesise"/> builds.
    ///
    /// The order in <see cref="Resolve"/> is the whole point of this class, and it is picture first:
    /// a real picture beats a rectangle, and our own picture will beat a borrowed one.
    /// </summary>
    internal static class MapCatalog
    {
        /// <summary>Marks an entry this class built rather than read from a map file. Shown instead
        /// of a map author's credit - see MapView.AddCredit - and never a real attribution, so
        /// <see cref="IsSynthesised"/> answers by identity rather than by reading it.</summary>
        internal const string SynthesisedAttribution = "harvested extent";

        /// <summary>The floor name used when an extent carries no bands at all.</summary>
        private const string SingleFloorName = "Ground";

        /// <summary>The height band given to that single floor: the map's whole vertical range as
        /// far as anything here is concerned. A band has to contain the markers or
        /// <see cref="DynamicMapsLibrary.MapEntry.LayerFor"/> answers "no floor" for all of them.</summary>
        private const float UnknownFloorMinY = -2000f;
        private const float UnknownFloorMaxY = 2000f;

        /// <summary>Synthesised entries by location key, with the stamp of the extent they were
        /// built from. Kept because the view resolves the map several times per build and rebuilds
        /// on every dropdown click: a fresh entry each time would be a fresh
        /// <see cref="DynamicMapsLibrary.MapLayer"/> each time, and the viewport's kept-from key
        /// (MapView._keptFrom) compares its marker set by reference, so a new object per repaint
        /// would also throw away the pan and zoom the player had just set.</summary>
        private static readonly Dictionary<string, (string Stamp, DynamicMapsLibrary.MapEntry Entry)> _synthesised =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The map to draw for a location, or null when there is nothing to draw it from.
        ///
        /// In order:
        /// 1. Our own capture, from the install's captures folder, which carries its own bounds,
        ///    floors and place names - and then, in the stage that adds the transport, the picture
        ///    this profile's host has cached, read out of the same meta shape.
        /// 2. DynamicMaps, while it is still a supported source: a real picture with hand-placed
        ///    place names beats a bare rectangle.
        /// 3. The harvested extent on the marker set: bounds and floor bands with no picture.
        /// 4. Nothing - the sidebar says so and the view draws its list alone, exactly as before.
        /// </summary>
        /// <param name="locationKey">The map's internal id ("bigmap"), as a quest's LocationKey and
        /// the marker payload both key it.</param>
        /// <param name="set">This location's marker set, which carries the harvested extent. May be
        /// null - no server half, no payload yet, or no harvest of this map.</param>
        /// <param name="displayName">What the view calls this map, where it knows; only the log
        /// line in MapView.BuildMarkers reads it back off the entry.</param>
        internal static DynamicMapsLibrary.MapEntry Resolve(
            string locationKey, MapMarkerSetDto set, string displayName = null)
        {
            if (string.IsNullOrEmpty(locationKey)) return null;

            // ---- (a) our own capture, from a raid on this map. It brings its own bounds, floors
            // and place names out of its meta file, and its picture is the one thing here that is
            // certainly of THIS install's version of the map.
            //
            // (The host's cached copy of someone else's capture goes next, in the stage that adds
            // the transport; it reads the same meta shape out of a different folder.)
            var captured = LocalCapture(locationKey, displayName);
            if (captured != null) return captured;

            // ---- (b) DynamicMaps, if the player has it and it ships this location.
            var installed = DynamicMapsLibrary.FindByLocationKey(locationKey);
            if (installed != null) return installed;

            // ---- (c) the harvested extent.
            return Synthesise(locationKey, set, displayName);
        }

        /// <summary>
        /// The objective data's floor vocabulary, mapped onto the level numbers <see cref="Synthesise"/>
        /// hands its layers.
        ///
        /// Needed because the two sides name floors differently and only one of them can be changed.
        /// A percentage-placed objective pin carries a floor NAME - "Ground_Level", "First_Floor" -
        /// which MapView.OwnerFor matches against the layer's FloorName, and a harvested band is
        /// named from its level ("Ground", "Floor 2", "Basement"). The names never meet, so on a
        /// multi-band synthesised map every such pin resolved to no floor at all and was drawn at
        /// full strength on whichever storey the player happened to be looking at. The harvested band
        /// names cannot be changed to suit this table: they are what the floor picker prints.
        ///
        /// The values are the level numbering the data itself uses, which is the one piece of
        /// evidence there is: the vocabulary lines up with the layer names DynamicMaps' own configs
        /// carry, where Interchange's "First_Floor" is level 1 rather than the ground - see
        /// MapView.OwnerFor. Anything not in this table stays unmatched, which is the honest answer
        /// and the behaviour that was there before.
        ///
        /// Consulted for SYNTHESISED entries only. A DynamicMaps entry's FloorName comes from its own
        /// artwork filenames in this same vocabulary, so exact matching already works there, and
        /// aliasing on top of it would silently re-file pins on maps that have been drawing correctly
        /// for releases.
        /// </summary>
        private static readonly Dictionary<string, int> FloorLevelAliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Ground"] = 0,
                ["Ground_Level"] = 0,
                ["Ground_Floor"] = 0,
                ["First_Floor"] = 1,
                ["Second_Floor"] = 2,
                ["Third_Floor"] = 3,
                ["Basement"] = -1,
                ["Bunkers"] = -1,
                ["Underground_Level"] = -1
            };

        /// <summary>The level an objective's floor name means, or null when this table has never
        /// heard of it. See <see cref="FloorLevelAliases"/>.</summary>
        internal static int? LevelForFloorName(string floorName)
        {
            if (string.IsNullOrEmpty(floorName)) return null;

            return FloorLevelAliases.TryGetValue(floorName.Trim(), out var level) ? level : (int?)null;
        }

        /// <summary>
        /// Whether this entry's floors are named the way the HARVESTER names bands ("Ground",
        /// "Floor 2", "Basement") rather than the way a map's artwork filenames do
        /// ("Ground_Level", "First_Floor").
        ///
        /// True for both of the entries this class builds - a synthesised rectangle and a capture -
        /// because both take their floor names from the harvested bands and neither has an artwork
        /// filename to take a second name from. It is what MapView.OwnerFor asks before translating
        /// an objective's floor name through <see cref="FloorLevelAliases"/>: without it a captured
        /// multi-storey map would put every floor-naming pin on whichever storey was being looked
        /// at, which is exactly the defect the alias table was added to fix for synthesised maps.
        /// </summary>
        internal static bool UsesBandFloorNames(DynamicMapsLibrary.MapEntry entry) =>
            IsSynthesised(entry) || IsLocalCapture(entry);

        /// <summary>Whether this entry is one of ours - a rectangle with no picture - asked by
        /// identity, so a map file claiming our attribution string cannot be mistaken for one.</summary>
        internal static bool IsSynthesised(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null) return false;

            foreach (var cached in _synthesised.Values)
                if (ReferenceEquals(cached.Entry, entry)) return true;

            return false;
        }

        // ------------------------------------------------------------------ local captures

        /// <summary>Where the in-raid capture writes: BepInEx/plugins/QuestTree/captures/(key)/,
        /// one folder per map, holding one PNG per floor and one meta file.</summary>
        private const string CapturesFolder = "captures";

        /// <summary>The meta file's ending. Named after the map rather than fixed
        /// ("bigmap.map.json") so a folder copied somewhere else still says what it is.</summary>
        private const string MetaSuffix = ".map.json";

        /// <summary>The highest capture meta schema this build understands. A capture written by a
        /// newer Quest Tracker is SKIPPED rather than read optimistically: the fields it adds are
        /// exactly the ones that would change where the picture goes, and a map drawn from half of
        /// a newer format is worse than the harvested rectangle it falls back to.</summary>
        internal const int SupportedCaptureSchema = 1;

        /// <summary>One capture, as read from disk.</summary>
        private sealed class Capture
        {
            /// <summary>What has to change on disk for the entry to be rebuilt rather than kept -
            /// see <see cref="ScanCaptures"/>.</summary>
            public string Stamp = "";

            /// <summary>When the raid that made it happened, for choosing between two captures of
            /// one map. Null when the meta's timestamp could not be read, which loses every
            /// comparison.</summary>
            public DateTime? CapturedAt;

            public DynamicMapsLibrary.MapEntry Entry;
        }

        /// <summary>Captures by location key, or null before the first scan. Scanned once per
        /// session: the folder only changes when a raid writes to it, and the writer says so
        /// through <see cref="InvalidateCaptures"/>.</summary>
        private static Dictionary<string, Capture> _captures;

        /// <summary>
        /// Re-reads the captures folder, for the capture writer to call when a capture has just
        /// finished.
        ///
        /// Called from the raid, on Unity's thread, which is what makes it safe to free the
        /// pictures of a capture this replaces: destroying a Texture2D is a main-thread act. Safe
        /// to call when nothing has ever been scanned (the first Resolve scans anyway) and safe to
        /// call twice.
        ///
        /// Anything holding a MapEntry from before the call keeps a live object with the bounds and
        /// floors it had; only its pictures are dropped, and the view rebuilds from
        /// <see cref="Resolve"/> on its next repaint. The one thing that could be holding a dropped
        /// picture rather than a reference to it is the map on screen, so that is let go of first -
        /// see MapView.ForgetDrawnMap.
        /// </summary>
        internal static void InvalidateCaptures()
        {
            var previous = _captures;

            // Never scanned: nothing is held, and the next Resolve reads the folder fresh.
            if (previous == null) return;

            // Before anything is freed. The view may still be holding the very sprite this is about
            // to destroy - see MapView.ForgetDrawnMap, which is where the reason is written down.
            MapView.ForgetDrawnMap();

            _captures = ScanCaptures(previous);
            ReleaseDropped(previous, _captures);

            // And a redraw, so the map area does not sit empty where the viewport just was. A no-op
            // when no panel is listening, which is the normal case for a capture taken in a raid -
            // QuestTreePanel unsubscribes as it is destroyed and defers the repaint while it is
            // hidden - so this costs nothing and covers the case where the tab is up.
            ModSettings.RequestRepaint();
        }

        /// <summary>Whether this entry came from one of our own captures, asked by identity for the
        /// same reason <see cref="IsSynthesised"/> is: the credit line under the map depends on the
        /// answer, and a map file is free to claim any attribution string it likes.</summary>
        internal static bool IsLocalCapture(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null || _captures == null) return false;

            foreach (var capture in _captures.Values)
                if (ReferenceEquals(capture.Entry, entry)) return true;

            return false;
        }

        /// <summary>
        /// The capture of this map, or null when there is none. The display name arrives later than
        /// the map does on some call sites, exactly as in <see cref="Synthesise"/>.
        ///
        /// The aliased id is tried second, and without it half the campaign's captures would never
        /// be drawn. A capture is written under the location id the RAID had - factory4_night for a
        /// night Factory, Sandbox_high for a Ground Zero raid above level 20, which is the only
        /// Ground Zero a levelled account can enter - while the view asks by the folded key
        /// (MapView.CanonicalMapKey), so factory4_day and Sandbox are the only names that ever
        /// reach here. The two ids in a pair load one scene with one set of coordinates, which is
        /// the same fact MapView.MarkerSetForKey already leans on to let a night harvest supply the
        /// day map's extent.
        ///
        /// The requested key is then added to the entry's own names, so
        /// MapView.MarkerSetFor(entry) - which matches the marker payload against InternalNames -
        /// still finds a set for the map the player is looking at. That is what DynamicMaps' own
        /// Factory entry does, which carries both ids for one picture.
        /// </summary>
        /// <param name="locationKey">The map's internal id, as the view spells it.</param>
        /// <param name="displayName">What the view calls this map, or null.</param>
        private static DynamicMapsLibrary.MapEntry LocalCapture(string locationKey, string displayName)
        {
            var captures = _captures ??= ScanCaptures(null);

            if (!captures.TryGetValue(locationKey, out var capture))
            {
                var aliased = AliasOf(locationKey);
                if (aliased == null || !captures.TryGetValue(aliased, out capture)) return null;

                if (!capture.Entry.InternalNames.Any(
                        n => string.Equals(n, locationKey, StringComparison.OrdinalIgnoreCase)))
                {
                    capture.Entry.InternalNames.Add(locationKey);
                }
            }

            if (!string.IsNullOrEmpty(displayName)) capture.Entry.DisplayName = displayName;
            return capture.Entry;
        }

        /// <summary>The other id of the pair that loads the same scene as this one, or null when
        /// this map has no twin. The pairs are MapView's, which mirrors the server's
        /// ZoneStore.Aliases - see <see cref="MapView.SceneAliases"/>.</summary>
        /// <param name="locationKey">The map's internal id.</param>
        private static string AliasOf(string locationKey)
        {
            foreach (var (a, b) in MapView.SceneAliases)
            {
                if (string.Equals(a, locationKey, StringComparison.OrdinalIgnoreCase)) return b;
                if (string.Equals(b, locationKey, StringComparison.OrdinalIgnoreCase)) return a;
            }

            return null;
        }

        /// <summary>
        /// Reads every capture in the folder.
        ///
        /// Entries are carried over from a previous scan when the file on disk has not changed,
        /// which is not an optimisation: a fresh MapEntry means fresh MapLayer objects, and those
        /// hold the decoded pictures and are what the viewport's kept-from key compares. Rebuilding
        /// an unchanged map would re-decode 43 MB per floor and throw away the player's pan and
        /// zoom - the same trap <see cref="Synthesise"/>'s cache exists for.
        ///
        /// Two folders claiming the same map is settled by capturedAt, newest first, so a copied-in
        /// capture cannot displace a fresh one. Nothing here throws: a bad file costs one warning
        /// and that map falls through to DynamicMaps or its harvested rectangle.
        /// </summary>
        private static Dictionary<string, Capture> ScanCaptures(Dictionary<string, Capture> previous)
        {
            var captures = new Dictionary<string, Capture>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var root = CapturesRoot();
                if (root == null || !Directory.Exists(root)) return captures;

                foreach (var folder in Directory.GetDirectories(root))
                {
                    foreach (var meta in Directory.GetFiles(folder, "*" + MetaSuffix))
                    {
                        var parsed = ReadMeta(meta, folder);
                        if (parsed == null) continue;

                        // Newest wins. An unreadable timestamp is treated as the oldest possible
                        // capture, so it is kept only when nothing else claims the map.
                        if (captures.TryGetValue(parsed.Key, out var held) &&
                            Ticks(held.CapturedAt) >= Ticks(parsed.CapturedAt)) continue;

                        var carried = previous != null &&
                                      previous.TryGetValue(parsed.Key, out var old) &&
                                      old.Stamp == parsed.Stamp
                            ? old.Entry
                            : null;

                        captures[parsed.Key] = new Capture
                        {
                            Stamp = parsed.Stamp,
                            CapturedAt = parsed.CapturedAt,
                            Entry = carried ?? BuildEntry(parsed)
                        };
                    }
                }

                if (captures.Count > 0)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {captures.Count} captured map(s) " +
                        $"({captures.Values.Sum(c => c.Entry.Layers.Count)} floors) to draw from.");
                }
            }
            catch (Exception ex)
            {
                // The folder itself: missing permissions, a file in place of a directory. One line,
                // and every map falls back to what it had before captures existed.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not read the captured maps ({ex.Message}) - using the map " +
                    $"sources that were there before.");
            }

            return captures;
        }

        /// <summary>Frees the pictures of captures a rescan replaced or dropped. Compared by
        /// identity, because a carried-over entry appears in both dictionaries and its pictures are
        /// the ones worth keeping.</summary>
        private static void ReleaseDropped(
            Dictionary<string, Capture> previous, Dictionary<string, Capture> current)
        {
            foreach (var capture in previous.Values)
            {
                if (capture.Entry == null) continue;
                if (current.Values.Any(c => ReferenceEquals(c.Entry, capture.Entry))) continue;

                foreach (var layer in capture.Entry.Layers) DynamicMapsLibrary.ReleaseLayer(layer);
            }
        }

        /// <summary>BepInEx/plugins/QuestTree/captures/, or null when the plugin has no file
        /// location - the same case KappaQuests and the capture writer guard. NOT created here:
        /// this half only reads, and a reader that creates its own empty folder makes the writer's
        /// absence look like a capture that failed.</summary>
        private static string CapturesRoot()
        {
            var modPath = Path.GetDirectoryName(typeof(MapCatalog).Assembly.Location);
            return string.IsNullOrEmpty(modPath) ? null : Path.Combine(modPath, CapturesFolder);
        }

        /// <summary>A capture's meta file, read and validated. Everything a
        /// <see cref="DynamicMapsLibrary.MapEntry"/> needs, and nothing that needs Unity.</summary>
        private sealed class ParsedCapture
        {
            public string Key = "";
            public string Stamp = "";
            public DateTime? CapturedAt;
            public string Attribution = "";
            public float MinX, MinZ, MaxX, MaxZ;
            public int Rotation;
            public readonly List<(int Level, string Name, string File, float MinY, float MaxY)> Floors = new();
            public readonly List<(string Text, float X, float Z)> Labels = new();
        }

        /// <summary>
        /// One meta file, or null with one warning logged.
        ///
        /// Guarded field by field rather than deserialised into a DTO, because this file is on the
        /// player's disk: it can be hand-edited, half-written by a raid that crashed, or copied in
        /// from somewhere else, and the failure that matters is not an exception but a number that
        /// parses and puts the picture in the wrong place. Every quantity the placement depends on
        /// is checked here, and the ones that only decorate are allowed to be missing.
        /// </summary>
        private static ParsedCapture ReadMeta(string metaPath, string folder)
        {
            var name = Path.GetFileName(metaPath);

            try
            {
                var root = JObject.Parse(File.ReadAllText(metaPath));

                var schema = (int?)Field(root, "schemaVersion") ?? 0;
                if (schema <= 0)
                {
                    Warn(name, "it has no schemaVersion");
                    return null;
                }

                if (schema > SupportedCaptureSchema)
                {
                    Warn(name, $"it is schema {schema} and this build reads {SupportedCaptureSchema} " +
                               $"- update Quest Tracker to use it");
                    return null;
                }

                var extent = Field(root, "extent") as JObject;
                if (extent == null)
                {
                    Warn(name, "it has no extent");
                    return null;
                }

                var parsed = new ParsedCapture
                {
                    MinX = Number(extent, "minX"),
                    MinZ = Number(extent, "minZ"),
                    MaxX = Number(extent, "maxX"),
                    MaxZ = Number(extent, "maxZ")
                };

                if (!IsFinite(parsed.MinX) || !IsFinite(parsed.MinZ) ||
                    !IsFinite(parsed.MaxX) || !IsFinite(parsed.MaxZ))
                {
                    Warn(name, "its extent is not a set of numbers");
                    return null;
                }

                if (parsed.MaxX - parsed.MinX < 1f || parsed.MaxZ - parsed.MinZ < 1f)
                {
                    Warn(name, "its extent is not a rectangle at least a metre across");
                    return null;
                }

                // The folder name is the fallback, so a meta whose "map" field was lost still draws
                // on the map its folder is named after.
                parsed.Key = ((string)Field(root, "map") ?? "").Trim();
                if (parsed.Key.Length == 0) parsed.Key = Path.GetFileName(folder) ?? "";

                if (parsed.Key.Length == 0)
                {
                    Warn(name, "it names no map");
                    return null;
                }

                // Only the four right angles. The writer records 0 and the picture is captured
                // axis-aligned; anything else here would be turned by MapView.PlaceArtwork, and a
                // rotation of 37 degrees would stretch the picture off its own rectangle.
                var rotation = (int?)Field(root, "rotation") ?? 0;
                parsed.Rotation = rotation == 90 || rotation == 180 || rotation == 270 ? rotation : 0;

                var pxPerMetre = Number(root, "pxPerMetre");
                var capturedAt = ((string)Field(root, "capturedAt") ?? "").Trim();
                parsed.CapturedAt = ParseTimestamp(capturedAt);

                ReadFloors(root, folder, name, pxPerMetre, parsed);
                if (parsed.Floors.Count == 0)
                {
                    Warn(name, "none of its floors have a picture on disk");
                    return null;
                }

                ReadLabels(root, parsed);

                parsed.Attribution = Attribution(
                    (string)Field(root, "modVersion"), capturedAt, parsed.CapturedAt,
                    (string)Field(root, "timeOfDay"));

                // What has to change for the entry to be rebuilt: the capture's own identity plus
                // the file's write time, which catches a re-capture written within the same second
                // and a hand edit that keeps the timestamp.
                parsed.Stamp = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3:0.##},{4:0.##},{5:0.##},{6:0.##}|{7}|{8}",
                    metaPath, capturedAt, File.GetLastWriteTimeUtc(metaPath).Ticks,
                    parsed.MinX, parsed.MinZ, parsed.MaxX, parsed.MaxZ,
                    parsed.Rotation, parsed.Floors.Count);

                return parsed;
            }
            catch (Exception ex)
            {
                // Unparseable JSON, a truncated file, a field of the wrong type: one line, and this
                // map falls through to DynamicMaps or its harvested rectangle.
                Warn(name, ex.Message);
                return null;
            }
        }

        /// <summary>The floors whose picture is actually on disk, lowest first and one per level.
        /// A floor missing its PNG is dropped rather than failing the capture: a map whose upper
        /// storey did not get written is still a map with a ground floor.</summary>
        private static void ReadFloors(
            JObject root, string folder, string metaName, float pxPerMetre, ParsedCapture parsed)
        {
            var floors = Field(root, "floors") as JArray;
            if (floors == null) return;

            var seen = new HashSet<int>();

            foreach (var floor in floors)
            {
                if (!(floor is JObject node)) continue;

                var declared = ((string)Field(node, "file") ?? "").Trim();
                if (declared.Length == 0) continue;

                // The meta is a file on the player's disk and the name in it becomes a path we
                // read, so it has to be a bare file name in the capture's own folder. Compared
                // against GetFileName rather than searched for "..": that rejects every separator,
                // every drive letter and every traversal in one test.
                if (!string.Equals(declared, Path.GetFileName(declared), StringComparison.Ordinal))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: capture '{metaName}' names the floor picture '{declared}', which " +
                        $"is not a plain file name in the capture's own folder - that floor is skipped.");
                    continue;
                }

                var file = Path.Combine(folder, declared);
                if (!File.Exists(file)) continue;

                var level = (int?)Field(node, "level") ?? 0;
                if (!seen.Add(level)) continue;

                var minY = Number(node, "minY");
                var maxY = Number(node, "maxY");

                // A band that is not a pair of numbers claims every height instead of none: the
                // alternative is a floor whose markers all resolve to no floor at all.
                if (!IsFinite(minY) || !IsFinite(maxY))
                {
                    minY = UnknownFloorMinY;
                    maxY = UnknownFloorMaxY;
                }

                var floorName = ((string)Field(node, "name") ?? "").Trim();
                if (floorName.Length == 0) floorName = $"Level {level}";

                CheckPictureSize(node, metaName, declared, pxPerMetre, parsed);

                parsed.Floors.Add((level, floorName, file, Mathf.Min(minY, maxY), Mathf.Max(minY, maxY)));
            }

            parsed.Floors.Sort((a, b) => a.Level.CompareTo(b.Level));
        }

        /// <summary>
        /// Says so when a floor's picture is not the size its own extent and scale call for.
        ///
        /// A check that can fail, and the one that would catch the writer and the reader disagreeing
        /// about the contract: the picture is stretched onto the extent whatever its size, so a
        /// picture built at a different scale or with a margin lands perfectly rectangularly and
        /// entirely in the wrong place, and nothing about it looks broken. Drawn anyway - a slightly
        /// wrong map beats no map - but never silently.
        /// </summary>
        private static void CheckPictureSize(
            JObject floor, string metaName, string file, float pxPerMetre, ParsedCapture parsed)
        {
            if (!IsFinite(pxPerMetre) || pxPerMetre <= 0f) return;

            var width = (int?)Field(floor, "width") ?? 0;
            var height = (int?)Field(floor, "height") ?? 0;
            if (width <= 0 || height <= 0) return;

            var wanted = Mathf.CeilToInt((parsed.MaxX - parsed.MinX) * pxPerMetre);
            var tall = Mathf.CeilToInt((parsed.MaxZ - parsed.MinZ) * pxPerMetre);

            if (Math.Abs(width - wanted) <= 2 && Math.Abs(height - tall) <= 2) return;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: capture '{metaName}' says '{file}' is {width}x{height} px, but its " +
                $"extent at {pxPerMetre:0.###} px/m needs {wanted}x{tall} - the picture will be " +
                $"stretched onto the extent and may not line up.");
        }

        /// <summary>The capture's place names: exfils and cleaned zone names, at a ground position
        /// and no height. Anything without both is dropped.</summary>
        private static void ReadLabels(JObject root, ParsedCapture parsed)
        {
            var labels = Field(root, "labels") as JArray;
            if (labels == null) return;

            foreach (var label in labels)
            {
                if (!(label is JObject node)) continue;

                var text = ((string)Field(node, "text") ?? "").Trim();
                if (text.Length == 0) continue;

                var x = Number(node, "x");
                var z = Number(node, "z");
                if (!IsFinite(x) || !IsFinite(z)) continue;

                parsed.Labels.Add((text, x, z));
            }
        }

        /// <summary>The entry a read capture describes. No file access and no Unity objects: the
        /// picture is a path until the view asks a layer for its sprite.</summary>
        private static DynamicMapsLibrary.MapEntry BuildEntry(ParsedCapture parsed)
        {
            var entry = new DynamicMapsLibrary.MapEntry
            {
                DisplayName = parsed.Key,
                Attribution = parsed.Attribution,
                CoordinateRotation = parsed.Rotation,

                // The ground band, which the harvester numbers 0 and the capture copies. Not the
                // lowest floor: a map with a basement should not open in it.
                DefaultLevel = 0
            };

            // One key, for the reason Synthesise gives: a capture is of one location, and claiming
            // Factory's other id would hand this map the other location's markers.
            entry.InternalNames.Add(parsed.Key);

            var boundsMin = new Vector2(parsed.MinX, parsed.MinZ);
            var boundsMax = new Vector2(parsed.MaxX, parsed.MaxZ);

            foreach (var floor in parsed.Floors)
            {
                var layer = new DynamicMapsLibrary.MapLayer
                {
                    Name = floor.Name,

                    // The same name in both, as for a synthesised floor: the outside objective data
                    // names floors in a vocabulary the harvester aliases onto these band names, and
                    // a captured PNG has no artwork filename suffix to take a second name from.
                    FloorName = floor.Name,
                    Level = floor.Level,

                    ImagePath = floor.File,

                    // The one flag that decides how it is loaded and what draws it.
                    IsRaster = true,

                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };

                // One box over the whole rectangle at this floor's height band. Map space is
                // (game.x, game.z) with game.y as the box's z - see DynamicMapsLibrary.
                layer.GameBounds.Add(new DynamicMapsLibrary.GameBox(
                    new Vector3(parsed.MinX, parsed.MinZ, floor.MinY),
                    new Vector3(parsed.MaxX, parsed.MaxZ, floor.MaxY)));

                entry.Layers.Add(layer);
            }

            foreach (var label in parsed.Labels)
            {
                entry.Labels.Add(new DynamicMapsLibrary.MapLabel
                {
                    Text = label.Text,

                    // Map space: the label's x and z, in the same coordinates every marker uses.
                    Position = new Vector2(label.X, label.Z),

                    // The capture records no height, so the name belongs to no one floor - see
                    // MapLabel.FloorAgnostic. Height 0 with the flag off would put an exfil on
                    // whichever band contains y=0 and dim it everywhere else.
                    FloorAgnostic = true,
                    Height = 0f,
                    Rotation = 0f
                });
            }

            return entry;
        }

        /// <summary>The credit line for a captured map: ours, so it names the build and the raid
        /// rather than a licence. Shown by MapView.AddCredit.</summary>
        private static string Attribution(
            string modVersion, string capturedAtText, DateTime? capturedAt, string timeOfDay)
        {
            var version = string.IsNullOrEmpty(modVersion) ? "" : $" {modVersion.Trim()}";

            var date = capturedAt.HasValue
                ? capturedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : (capturedAtText ?? "").Trim();

            var when = date.Length > 0 ? $", {date}" : "";

            var light = string.IsNullOrEmpty(timeOfDay) ? "" : $" ({timeOfDay.Trim()})";

            return $"captured in-game with Quest Tracker{version}{when}{light}";
        }

        /// <summary>The meta's ISO UTC timestamp, or null. Round-tripped rather than read in the
        /// machine's locale: these are written with a Z and have to mean the same instant on a
        /// player's machine as on the one that captured them.</summary>
        private static DateTime? ParseTimestamp(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static long Ticks(DateTime? when) => when?.Ticks ?? long.MinValue;

        /// <summary>A field by name, case-insensitively. The writer emits camelCase and JObject's
        /// indexer is case-sensitive, so a meta hand-edited into PascalCase - or written by a tool
        /// with different serializer settings - would otherwise read as a file with no fields at
        /// all rather than as a file with a typo.</summary>
        private static JToken Field(JObject node, string name) =>
            node?.GetValue(name, StringComparison.OrdinalIgnoreCase);

        /// <summary>A number field as a float, or NaN when it is absent or not a number. NaN rather
        /// than 0 on purpose: 0 is a legitimate coordinate and a silent default here is how a map
        /// ends up drawn in the corner of the world.</summary>
        private static float Number(JObject node, string name)
        {
            var token = Field(node, name);
            if (token == null || token.Type == JTokenType.Null) return float.NaN;

            try
            {
                return token.Type == JTokenType.Integer || token.Type == JTokenType.Float
                    ? (float)token
                    : float.TryParse(
                        (string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : float.NaN;
            }
            catch
            {
                return float.NaN;
            }
        }

        /// <summary>One line per bad capture, and the map falls back to what it had before.</summary>
        private static void Warn(string metaName, string because) =>
            Plugin.LogSource?.LogWarning(
                $"QuestTree: skipped the captured map '{metaName}' - {because}.");

        /// <summary>The entry a harvested extent describes, or null when the set carries none or
        /// carries one that is not a rectangle. Cached per location and extent stamp.</summary>
        private static DynamicMapsLibrary.MapEntry Synthesise(
            string locationKey, MapMarkerSetDto set, string displayName)
        {
            var extent = set?.Extent;
            if (extent == null) return null;

            // Rounded to metres by the harvester and validated by the server, and checked again
            // here: a NaN would make the viewport's fit scale NaN, which loses the whole panel
            // rather than one map.
            var minX = (float)extent.MinX;
            var minZ = (float)extent.MinZ;
            var maxX = (float)extent.MaxX;
            var maxZ = (float)extent.MaxZ;

            if (!IsFinite(minX) || !IsFinite(minZ) || !IsFinite(maxX) || !IsFinite(maxZ)) return null;
            if (maxX - minX < 1f || maxZ - minZ < 1f) return null;

            var stamp = Stamp(extent, minX, minZ, maxX, maxZ);

            if (_synthesised.TryGetValue(locationKey, out var cached) && cached.Stamp == stamp)
            {
                // The name can arrive later than the map does: the call sites that only have a
                // location key pass none, and the one that builds the view passes the real one.
                if (!string.IsNullOrEmpty(displayName)) cached.Entry.DisplayName = displayName;
                return cached.Entry;
            }

            var entry = new DynamicMapsLibrary.MapEntry
            {
                DisplayName = string.IsNullOrEmpty(displayName) ? locationKey : displayName,
                Attribution = SynthesisedAttribution,

                // Rotation zero, deliberately, whatever the extent says: CoordinateRotation turns
                // the artwork and the percentage-placed objective pins with it, and an extent has
                // no artwork to turn. The harvester writes rotation 0 for the same reason.
                CoordinateRotation = 0,
                DefaultLevel = 0
            };

            // One key only. A DynamicMaps entry covers Factory's day and night ids together because
            // one picture serves both; an extent is harvested per location, so claiming the other
            // id here would hand this map the other location's markers.
            entry.InternalNames.Add(locationKey);

            var boundsMin = new Vector2(minX, minZ);
            var boundsMax = new Vector2(maxX, maxZ);

            foreach (var floor in Floors(extent))
            {
                var layer = new DynamicMapsLibrary.MapLayer
                {
                    Name = floor.Name,

                    // The same name in both fields on purpose. FloorName is the vocabulary the
                    // outside objective data names floors in (MapView.OwnerFor), and for a map with
                    // no artwork there is no filename suffix to take it from - the harvested band
                    // name, which the harvester aliases that vocabulary onto, is all there is.
                    FloorName = floor.Name,
                    Level = floor.Level,

                    // No picture. ImagePath empty is what MapLayer.HasArtwork reads, and it is why
                    // nothing here ever starts a tessellation.
                    ImagePath = "",
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };

                // One box over the whole rectangle, the floor's own height band. Map space is
                // (game.x, game.z) with game.y as the box's z - see DynamicMapsLibrary.
                layer.GameBounds.Add(new DynamicMapsLibrary.GameBox(
                    new Vector3(minX, minZ, Mathf.Min(floor.MinY, floor.MaxY)),
                    new Vector3(maxX, maxZ, Mathf.Max(floor.MinY, floor.MaxY))));

                entry.Layers.Add(layer);
            }

            if (entry.Layers.Count == 0) return null;

            // Labels stay empty: DynamicMaps' place names are hand-placed work of its own, and
            // nothing harvested has names for places yet (the capture meta will).

            _synthesised[locationKey] = (stamp, entry);
            return entry;
        }

        /// <summary>The extent's bands, lowest first, or one band covering everything when it has
        /// none. Lowest first is the order <see cref="DynamicMapsLibrary.MapEntry.Layers"/> is
        /// documented in and the order the floor picker lists.</summary>
        private static List<(int Level, string Name, float MinY, float MaxY)> Floors(MapExtentDto extent)
        {
            var floors = new List<(int Level, string Name, float MinY, float MaxY)>();

            if (extent.Floors != null)
            {
                foreach (var floor in extent.Floors)
                {
                    if (floor == null) continue;
                    if (!IsFinite(floor.MinY) || !IsFinite(floor.MaxY)) continue;

                    var name = string.IsNullOrEmpty(floor.Name) ? $"Level {floor.Level}" : floor.Name;
                    floors.Add((floor.Level, name, floor.MinY, floor.MaxY));
                }
            }

            if (floors.Count == 0)
            {
                // A rectangle with no bands is still a map. One floor, claiming every height, so
                // every marker on it has a floor and the picker stays away.
                floors.Add((0, SingleFloorName, UnknownFloorMinY, UnknownFloorMaxY));
                return floors;
            }

            // De-duplicated by level as well as sorted: two layers at one level would make the
            // floor picker offer the same storey twice and MapEntry.LayerFor pick between them by
            // list order.
            return floors
                .GroupBy(f => f.Level)
                .Select(g => g.First())
                .OrderBy(f => f.Level)
                .ToList();
        }

        /// <summary>What has to change for a synthesised entry to be rebuilt. SampledAt alone is
        /// not enough: a host that sends an extent with no timestamp would pin the first shape this
        /// session saw for the rest of it.</summary>
        private static string Stamp(MapExtentDto extent, float minX, float minZ, float maxX, float maxZ)
        {
            var floors = extent.Floors != null ? extent.Floors.Count : 0;

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0}|{1}|{2:0.##},{3:0.##},{4:0.##},{5:0.##}|{6}",
                extent.SampledAt ?? "", extent.Source ?? "", minX, minZ, maxX, maxZ, floors);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
