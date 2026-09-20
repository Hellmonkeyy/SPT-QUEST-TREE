using System;
using System.Collections.Generic;
using System.Linq;
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
        /// 1. (later stages) our own capture in the install's captures folder, then the picture this
        ///    profile's host has cached - both of which carry their own bounds and floors.
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

            // ---- (a) HOOK for the stages that follow: our own captured picture, then the host's
            // cached copy of someone else's capture. Both come before DynamicMaps, both bring their
            // own bounds and floors from the capture's meta file, and neither exists yet.
            // (Phase B: captures/<key>/<key>.map.json; Phase C: maps/<key>/ from the server.)

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

        /// <summary>Whether this entry is one of ours - a rectangle with no picture - asked by
        /// identity, so a map file claiming our attribution string cannot be mistaken for one.</summary>
        internal static bool IsSynthesised(DynamicMapsLibrary.MapEntry entry)
        {
            if (entry == null) return false;

            foreach (var cached in _synthesised.Values)
                if (ReferenceEquals(cached.Entry, entry)) return true;

            return false;
        }

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
