using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EFT;
using EFT.Game.Spawning;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Measures, from a loaded raid, the two things needed to draw a map of our own: the rectangle
    /// the world occupies in game coordinates, and the height bands that read as floors.
    ///
    /// Why it has to be measured in raid at all: no file on disk says how big a map is. The locations
    /// table has no bounds, and the numbers the Maps tab has used until now were hand-written per map
    /// inside the DynamicMaps mod - which is why a map that mod does not ship has never had a picture.
    /// The scene, on the other hand, knows exactly: BorderZone is the invisible wall that stops a
    /// player leaving, Terrain is the heightmap, and the NavMesh is where a bot can stand.
    ///
    /// Three sources, best first, because each fails differently. The order is the one a measured
    /// Customs raid produced, NOT the one the plan guessed (it had BorderZone first):
    ///   1 NavMesh triangulation - the walkable world, present on everything with AI. It stops at
    ///     walls and water and so under-reports roofs and rooftops, which is what the 4 % pad below
    ///     is for. On Customs its box is -344,-265..690,234 against DynamicMaps' hand-made
    ///     -372,-306..698,235 - within about 40 m on a kilometre-wide map, and closer once padded.
    ///   2 Terrain - the heightmaps' union. Right shape outdoors, but it reaches far past anywhere
    ///     the map is played: Customs' terrains span -553,-359..847,341, 1400x700 m around a
    ///     playable 1034x499, and Interchange's four span -647,-810..753,590 around a NavMesh box of
    ///     -361,-455..531,401. Absent on the maps built entirely from meshes (Factory, Labs).
    ///   3 BorderZone - the invisible walls. The plan expected this to be the playable area as the
    ///     game itself defines it; on Customs the five zones union to -59,-326..222,364, an interior
    ///     box 323x647 m that left 180 of 282 harvested zones outside and was thrown away by the
    ///     containment check below, and Interchange has none at all. Whatever these volumes are on a
    ///     given map, they are not the world's edge, so they are the last resort.
    /// Nothing clamps the winner against anything else. An earlier draft held a lower-ranked box
    /// inside the NavMesh box plus 50 m, which was the guard for source 1 being absurdly large when
    /// BorderZone ranked first; with the NavMesh box ranked first that clamp could never do anything
    /// - Terrain and BorderZone are reached only when there is no NavMesh box to clamp against - so
    /// it is gone rather than sitting here as unreachable arithmetic. The containment check below is
    /// what catches a wrong rectangle now, whichever source produced it.
    ///
    /// Run on the harvester's SECOND pass (27 s in), on the main thread, inside the pass that is
    /// already reading the scene. Every step is guarded: this is a player's raid frame, and an extent
    /// is a nicety - the harvest's zones are not, and they are sent whatever happens here.
    ///
    /// Read a second time by the capture key (<see cref="MapCapture"/>), which must draw its picture
    /// to the same rectangle the harvest sent rather than a second measurement of its own; that is
    /// what <see cref="TryProbeForCapture"/> is for.
    ///
    /// The member names for the scene reads (BorderZone.Collider, BorderZone._extents,
    /// Terrain.activeTerrains, LocationScene.GetAll, SpawnPointMarker) are the ones Phase 0's
    /// throwaway experiment proved live in this game version, in raid, before any of this was built
    /// on them.
    ///
    /// The three source names are spelled LOWERCASE ("borderzone", "terrain", "navmesh") because
    /// that is the canonical casing ZoneStore normalises an accepted extent to and therefore the
    /// casing the extent comes back down in. Sending the pretty form would have meant the string in
    /// this half's log line and the string in the payload this half then reads were different words
    /// for the same source - nothing compares them today, and nothing should have to check first.
    /// </summary>
    internal static class MapExtentProbe
    {
        /// <summary>Margin added to each side of the measured rectangle, as a fraction of that axis'
        /// size and never less than <see cref="MinimumPad"/> metres. Every source under-reports
        /// something (the NavMesh stops at walls, BorderZones sit inside the visible skyline), and a
        /// pin clipped off the edge of the picture is worse than a little empty border.</summary>
        private const float PadFraction = 0.04f;

        private const float MinimumPad = 20f;

        /// <summary>Height of one histogram bin, in metres. Half a metre: a storey is 3 m and a
        /// staircase landing is about one bin, so bins are small enough to leave a gap between two
        /// floors and large enough that a sloping road does not shatter into noise.</summary>
        private const float BinHeight = 0.5f;

        /// <summary>Share of ALL NavMesh vertices one bin must hold to count as part of a floor.
        /// A deliberately high bar: what is wanted is the two or three heights a map is mostly
        /// built at, not every ledge. Bins under it are the empty air between floors.
        ///
        /// The one number to tune if a map reads with too few floors, together with
        /// <see cref="BandGap"/>. Measured so far: Customs' histogram is one unbroken run from
        /// y = -3 to y = 7 - its terrain simply slopes - so Customs is one "Ground" band at this
        /// setting, which is right for it. Nothing else in the floor code needs touching to try a
        /// different value.
        ///
        /// Open risk, which only a raid settles: Phase 0 measured its histograms in 1 m bins, and
        /// <see cref="BinHeight"/> here is 0.5 m, so the same 2 % bar is twice as hard to clear.
        /// Interchange's parking garage holds 11532 of 382k vertices in its 1 m bin - 3.0 %, over the
        /// bar - but spread evenly over two half-metre bins that is 1.5 % each, under it, and the
        /// garage would vanish instead of becoming its own floor. A garage floor is flat, so its
        /// vertices should pile into one half-metre bin rather than split evenly, which is why the
        /// number is left at 0.02; if a raid reports Interchange with three bands and no basement,
        /// this is the line to halve.</summary>
        private const float BandBinShare = 0.02f;

        /// <summary>Metres of thin bins that must separate two bands for them to be different floors.
        /// Under a storey height: two bands closer than this are one floor read twice (a mezzanine, a
        /// sloping ground) and are merged. The second of the two tuning numbers - see
        /// <see cref="BandBinShare"/>.
        ///
        /// 2.0 rather than the 2.5 first written, because of Interchange. Its dense 1 m bins are
        /// y = 18 (the parking garage, 11532 verts), y = 21-23 (the ground run, 140768/13818/9572),
        /// y = 27 (114635) and y = 36 (33588), of 382k; the thin bins between the garage and the
        /// ground are y = 19 and y = 20, so the garage's band ends at 19 and the ground's begins at
        /// 21 - a gap of exactly 2.0 m. At 2.5 the garage merged into the ground and Interchange lost
        /// the floor a player is most often shot from; at 2.0 it splits off as its own band below.
        /// The runs above the ground are 3 m and 8 m clear, so they are unaffected either way.</summary>
        private const float BandGap = 2.0f;

        /// <summary>Added above and below each band's bins, so a pin standing on a floor rather than
        /// inside its walkable surface still falls in the band. Half the gap, so bands cannot come
        /// to overlap.</summary>
        private const float FloorMargin = 0.5f;

        /// <summary>Most floors an extent may carry. The server refuses more; the smallest bands are
        /// dropped to fit, since a map with nine height bands has noise among them.</summary>
        private const int MaxFloors = 8;

        /// <summary>The check that can fail. Every zone and spawn point on the map is a place a
        /// player goes, so the rectangle has to contain nearly all of them. Over this share outside
        /// and the measurement is wrong in a way no later code could notice - pins would simply be
        /// drawn off the picture - so it is thrown away and the harvest goes without it.</summary>
        private const float MaxOutsideShare = 0.02f;

        /// <summary>The share of the harvest's TRIGGERS alone the rectangle must contain, which is
        /// the server's ZoneStore.MinTriggerCoverage and has to be checked here as well.
        ///
        /// The check above is not this check. It is stricter on its own numbers - 2 % against 10 % -
        /// but it measures a different population: triggers AND spawn point markers together, and a
        /// map can carry ten quest triggers beside a hundred spawn markers. Two of those ten outside
        /// the rectangle is 2 % of the pool and passes here, while the server sees 8 of 10 contained,
        /// under its 90 %, and throws the extent away. The two halves would then log opposite
        /// verdicts about the same rectangle: this side "extent for factory4_day ... 2 of 110 zones
        /// outside", success, and the server a warning nobody reading the client's raid log would
        /// look for - and the map would sit with no extent for good while nothing said why.
        ///
        /// Mirrored as the same arithmetic in the same precision rather than a tighter number, so
        /// what this side accepts the other side accepts too, exactly.</summary>
        private const double MinTriggerCoverage = 0.9d;

        /// <summary>The last extent this probe accepted, and the map it was measured on. Written by
        /// the one place that produces an extent, read by <see cref="TryProbeForCapture"/>.</summary>
        private static string _lastMap;

        private static MapExtentDto _lastExtent;

        /// <summary>The extent a capture of <paramref name="map"/> must be drawn to: the very one the
        /// harvest sent, when this raid has already measured it, and a fresh measurement otherwise.
        /// Null when nothing usable can be measured - the caller then captures nothing.
        ///
        /// Why not simply call <see cref="TryProbe"/> again with no triggers: the containment check
        /// would then run over the spawn point markers alone, a different population from the one the
        /// harvest tested, and a rectangle the harvest accepted at 2 % outside could fail here at
        /// 4 % - two halves of the same release logging opposite verdicts about one rectangle, which
        /// is the trap <see cref="MinTriggerCoverage"/> exists to keep out of this file. Reusing the
        /// accepted result also guarantees the meta's extent is bit-for-bit the one on the wire, so a
        /// pin drawn from the server's copy lands on the picture.
        ///
        /// The memo outlives the raid on purpose. It is keyed by map name, and a map's BorderZones,
        /// Terrain and NavMesh are the same geometry every time it loads, so a capture in a second
        /// raid on the same map is drawn to the same rectangle as the first. A different map has a
        /// different key and is measured afresh.</summary>
        /// <param name="map">The map's internal name, as the harvest spells it.</param>
        public static MapExtentDto TryProbeForCapture(string map)
        {
            if (!string.IsNullOrEmpty(map) && _lastExtent != null &&
                string.Equals(_lastMap, map, StringComparison.OrdinalIgnoreCase))
            {
                return _lastExtent;
            }

            return TryProbe(map, null);
        }

        /// <summary>Whether this raid ALREADY has an extent for <paramref name="map"/> - the memo test
        /// above without the measurement below it.
        ///
        /// It exists because <see cref="TryProbeForCapture"/> is expensive on a miss: it triangulates
        /// the whole NavMesh, hundreds of thousands of vertices on a large map, which is a visible
        /// hitch in a player's raid. That is the right price for a key press and the wrong one for
        /// anything that POLLS - automatic capture asks every few seconds, and before the harvester's
        /// second pass (27 s in) every one of those asks would have measured the map again. Callers
        /// that can simply wait ask this first.
        ///
        /// Waiting is also the better answer for a different reason: an extent measured here, with no
        /// harvested triggers to check containment against, is not necessarily the rectangle the
        /// harvest goes on to accept - and a capture drawn to a rectangle the harvest then supersedes
        /// is replaced rather than merged into.</summary>
        /// <param name="map">The map's internal name, as the harvest spells it.</param>
        public static bool HasExtentFor(string map) =>
            !string.IsNullOrEmpty(map) && _lastExtent != null &&
            string.Equals(_lastMap, map, StringComparison.OrdinalIgnoreCase);

        /// <summary>Measures the current scene. Returns null - never throws - when there is nothing
        /// to measure, when the result fails its own containment check, or on any error; the caller
        /// sends the harvest either way.</summary>
        /// <param name="map">The map's internal name, for the log lines.</param>
        /// <param name="triggers">The harvest's own trigger list, which the containment check is run
        /// against: these are the positions the extent exists to hold.</param>
        /// <returns>The measured extent, or null.</returns>
        public static MapExtentDto TryProbe(string map, ICollection<HarvestedTrigger> triggers)
        {
            try
            {
                return Probe(map ?? "unknown", triggers);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not measure {map}'s extent ({ex.GetType().Name}: {ex.Message}) - " +
                    "the harvest is sent without one.");
                return null;
            }
        }

        private static MapExtentDto Probe(string map, ICollection<HarvestedTrigger> triggers)
        {
            // Calculated exactly once: the triangulation copies the whole NavMesh into managed
            // arrays (hundreds of thousands of vertices on Streets), and both the extent fallback
            // and every floor band are read out of this one result.
            var vertices = NavMesh.CalculateTriangulation().vertices ?? Array.Empty<Vector3>();

            var nav = Union("navmesh", vertices);
            var border = BorderZoneBox();
            var terrain = TerrainBox();

            // NavMesh first - see the ranking in the class comment. This order is measured, not
            // assumed: on Customs the BorderZone union that used to win here was an interior box
            // that left 64 % of the harvested zones outside it, and the containment check below
            // threw the whole extent away.
            var chosen = nav.Valid ? nav : terrain.Valid ? terrain : border;
            if (!chosen.Valid)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: nothing on {map} to measure an extent from (no NavMesh, no Terrain, " +
                    "no BorderZone) - the harvest is sent without one.");
                return null;
            }

            var spawns = SpawnMarkerPositions();

            // The rectangle everything below measures against: the chosen source's own box, with
            // nothing done to it. Named because four lines below read it, and because this is where
            // the clamp used to be - see the class comment for why there is none.
            var box = chosen;

            var rect = Pad(box);
            var floors = Floors(map, vertices, spawns, box, nav);

            var triggerTotal = 0;
            var triggerInside = 0;
            var total = 0;
            var outside = 0;

            foreach (var trigger in triggers ?? Array.Empty<HarvestedTrigger>())
            {
                if (trigger == null) continue;
                total++;
                triggerTotal++;

                if (Outside(rect, trigger.X, trigger.Z)) outside++;
                else triggerInside++;
            }

            foreach (var spawn in spawns)
            {
                total++;
                if (Outside(rect, spawn.x, spawn.z)) outside++;
            }

            var width = (int)(rect.MaxX - rect.MinX);
            var height = (int)(rect.MaxZ - rect.MinZ);

            // The check that can fail, and HAS: on Customs the BorderZone union this file used to
            // rank first left 180 of 282 zones and spawn points outside, 63.8 %, and this is what
            // threw it away and sent the harvest without an extent. That measurement is why the
            // ranking now starts at the NavMesh. A NavMesh-only Factory whose triggers sit on
            // catwalks off the mesh is the case that can still trip it.
            if (total > 0 && outside > total * MaxOutsideShare)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the extent measured for {map} ({width}x{height} m, {box.Source}) leaves " +
                    $"{outside} of {total} zones and spawn points outside it " +
                    $"({Percent(outside, total)} %, over {Percent(MaxOutsideShare)} %) - it is wrong, so the " +
                    "harvest is sent without one.");
                return null;
            }

            // The server's own containment rule, run here so it cannot be the server that discovers
            // the rectangle is wrong: see MinTriggerCoverage. Same expression, same precision.
            if (triggerTotal > 0 && triggerInside < triggerTotal * MinTriggerCoverage)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the extent measured for {map} ({width}x{height} m, {box.Source}) holds only " +
                    $"{triggerInside} of {triggerTotal} harvested zones, under the " +
                    $"{Percent((float)MinTriggerCoverage)} % the server requires - it is wrong, so the " +
                    "harvest is sent without one.");
                return null;
            }

            Plugin.LogSource?.LogInfo(
                $"QuestTree: extent for {map} {width}x{height} m ({box.Source}), {floors.Count} floors, " +
                $"{outside} of {total} zones outside.");

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {map} floors: " +
                string.Join(", ", floors.Select(f => $"{f.Name} ({Signed(f.Level)}) y {F(f.MinY)}..{F(f.MaxY)}").ToArray()) +
                $" (navmesh {vertices.Length} verts, {spawns.Length} spawn markers, " +
                $"rect {F(rect.MinX)},{F(rect.MinZ)}..{F(rect.MaxX)},{F(rect.MaxZ)}).");

            var extent = new MapExtentDto
            {
                MinX = rect.MinX,
                MinZ = rect.MinZ,
                MaxX = rect.MaxX,
                MaxZ = rect.MaxZ,
                Source = box.Source,
                Rotation = 0f,
                SampledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Floors = floors
            };

            // Remembered for the capture key, which must draw its picture to this exact rectangle
            // and no other - see TryProbeForCapture. Only an ACCEPTED extent is kept: every return
            // above is a rectangle this file decided was wrong.
            _lastMap = map;
            _lastExtent = extent;

            return extent;
        }

        private static bool Outside(Rect rect, float x, float z) =>
            !IsFinite(x) || !IsFinite(z) ||
            x < rect.MinX || x > rect.MaxX || z < rect.MinZ || z > rect.MaxZ;

        // --- the rectangle --------------------------------------------------------------------

        /// <summary>Union of the XZ AABBs of every BorderZone's collider - the invisible walls a
        /// player cannot pass. Read through LocationScene's registered array, so a zone the game
        /// keeps disabled counts too; its Collider field is the box, and _extents is the serialised
        /// half-size to build one from when there is no collider component.
        ///
        /// Last of the three sources, because measurement says these volumes are not the world's
        /// edge: Customs' five zones union to an interior box a third of the map's width (see the
        /// class comment).</summary>
        private static Box BorderZoneBox()
        {
            var box = new Box("borderzone");

            foreach (var zone in BorderZones())
            {
                if (zone == null) continue;

                Collider collider = zone.Collider;
                if (collider == null) collider = zone.GetComponent<Collider>();

                if (collider != null)
                {
                    box.Add(collider.bounds);
                }
                else
                {
                    var half = zone._extents;
                    box.Add(new Bounds(
                        zone.transform.position,
                        new Vector3(Mathf.Abs(half.x) * 2f, Mathf.Abs(half.y) * 2f, Mathf.Abs(half.z) * 2f)));
                }

                box.Count++;
            }

            return box;
        }

        private static IEnumerable<BorderZone> BorderZones()
        {
            try
            {
                var registered = LocationScene.GetAll<BorderZone>()?.ToArray();
                if (registered != null && registered.Length > 0) return registered;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: LocationScene.GetAll<BorderZone> failed ({ex.Message}) - falling back to a scene search.");
            }

            return UnityEngine.Object.FindObjectsOfType<BorderZone>();
        }

        /// <summary>Union of every active Terrain's AABB, from its transform position and
        /// terrainData.size. Second of the three sources: a heightmap runs out to the horizon, so it
        /// is only right where there is no NavMesh to measure the walkable world with.</summary>
        private static Box TerrainBox()
        {
            var box = new Box("terrain");

            var terrains = Terrain.activeTerrains;
            if (terrains == null) return box;

            foreach (var terrain in terrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;

                var origin = terrain.transform.position;
                var size = terrain.terrainData.size;
                box.Add(new Bounds(origin + size * 0.5f, size));
                box.Count++;
            }

            return box;
        }

        private static Box Union(string source, Vector3[] points)
        {
            var box = new Box(source);
            if (points == null) return box;

            foreach (var p in points) box.Add(p);
            box.Count = points.Length;
            return box;
        }

        /// <summary>Grows the box by 4 % of each axis (at least 20 m) and rounds outward to whole
        /// metres, so the number in the log and the number on disk are the same and a re-measurement
        /// of the same map does not drift by centimetres.</summary>
        /// <param name="box">The box the ranking chose.</param>
        private static Rect Pad(Box box)
        {
            var padX = Mathf.Max((box.MaxX - box.MinX) * PadFraction, MinimumPad);
            var padZ = Mathf.Max((box.MaxZ - box.MinZ) * PadFraction, MinimumPad);

            return new Rect
            {
                MinX = Math.Floor(box.MinX - padX),
                MinZ = Math.Floor(box.MinZ - padZ),
                MaxX = Math.Ceiling(box.MaxX + padX),
                MaxZ = Math.Ceiling(box.MaxZ + padZ)
            };
        }

        /// <summary>The MEASURED rectangle recovered from a padded one: <see cref="Pad"/> run
        /// backwards, to within the whole metre it rounded outward to.
        ///
        /// Why anything needs it. The extent a capture is drawn to, and the one the server stores, is
        /// the padded rectangle - deliberately, because a pin clipped off the edge of a picture is
        /// worse than an empty border. But the pad is, by construction, ground the map does NOT have:
        /// at least 20 m past the last NavMesh vertex, past the invisible walls, and on many maps past
        /// the level border that kills a player who crosses it. Anything that means to put the PLAYER
        /// somewhere inside the map has to plan inside this rectangle rather than the padded one; the
        /// picture still covers the pad.
        ///
        /// Every output equals its input when the rectangle is too small to inset or is not a
        /// rectangle at all, so a caller may use the result unconditionally.</summary>
        /// <param name="minX">West edge of the padded rectangle.</param>
        /// <param name="minZ">South edge of the padded rectangle.</param>
        /// <param name="maxX">East edge of the padded rectangle.</param>
        /// <param name="maxZ">North edge of the padded rectangle.</param>
        /// <param name="insetMinX">West edge with the pad taken off.</param>
        /// <param name="insetMinZ">South edge with the pad taken off.</param>
        /// <param name="insetMaxX">East edge with the pad taken off.</param>
        /// <param name="insetMaxZ">North edge with the pad taken off.</param>
        public static void Inset(
            double minX, double minZ, double maxX, double maxZ,
            out double insetMinX, out double insetMinZ, out double insetMaxX, out double insetMaxZ)
        {
            var padX = PadTakenOff(maxX - minX);
            var padZ = PadTakenOff(maxZ - minZ);

            insetMinX = minX + padX;
            insetMaxX = maxX - padX;
            insetMinZ = minZ + padZ;
            insetMaxZ = maxZ - padZ;
        }

        /// <summary>How much <see cref="Pad"/> added to ONE side of an axis, worked out from the padded
        /// length alone, or zero when it cannot be taken off.
        ///
        /// Pad adds max(4 % of the measured length, 20 m) per side, so a padded length S is either
        /// 1.08 x the measured one (the fractional case, which is the one over 500 m) or the measured
        /// one plus 40 m. Inverting the first gives S x 0.04 / 1.08 and the second gives 20, and the
        /// LARGER of the two is the one that was used: the fractional pad beats 20 m exactly when
        /// S >= 540 m, which is exactly when the measured length was over 500 m. So one max() recovers
        /// both cases, to within the metre Pad rounded outward to.</summary>
        /// <param name="padded">The padded length of one axis.</param>
        private static double PadTakenOff(double padded)
        {
            if (double.IsNaN(padded) || double.IsInfinity(padded) || padded <= 0d) return 0d;

            var pad = Math.Max(padded * PadFraction / (1d + 2d * PadFraction), MinimumPad);

            // A rectangle the pad would consume keeps what it has: a map measured smaller than 40 m
            // across is not one anybody is planning a route over, and an inverted rectangle would be
            // worse than a padded one.
            return padded - 2d * pad <= 1d ? 0d : pad;
        }

        // --- the floors ----------------------------------------------------------------------

        /// <summary>The height bands, as floors in level order. Always at least one: a map whose
        /// NavMesh says nothing usable still gets a single "Ground" spanning the box's own Y range,
        /// because the Maps tab needs exactly one layer to put its pins on.</summary>
        /// <param name="map">The map's name, for the log line.</param>
        /// <param name="vertices">The NavMesh triangulation's vertices.</param>
        /// <param name="spawns">Spawn point marker positions; their median Y names the ground floor.</param>
        /// <param name="box">The box the ranking chose, for the single-floor fallback's Y range.</param>
        /// <param name="nav">The NavMesh box, whose Y range joins the fallback's: a BorderZone volume
        /// is often far shorter than the world is tall, and the one floor of a map with no readable
        /// bands has to cover everything a pin could stand on.</param>
        private static List<MapFloorDto> Floors(string map, Vector3[] vertices, Vector3[] spawns, Box box, Box nav)
        {
            var bands = Bands(vertices);

            if (bands.Count <= 1)
            {
                var minY = bands.Count == 1 ? bands[0].MinY : Lower(box.MinY, nav.Valid ? nav.MinY : float.NaN);
                var maxY = bands.Count == 1 ? bands[0].MaxY : Higher(box.MaxY, nav.Valid ? nav.MaxY : float.NaN);

                if (!IsFinite(minY) || !IsFinite(maxY) || maxY < minY)
                {
                    // No NavMesh and a source that reported no height either. One floor covering
                    // everything is still better than none: it makes every pin land somewhere.
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: no height information for {map} - its one floor takes the whole Y axis.");
                    minY = -1000f;
                    maxY = 1000f;
                }

                return new List<MapFloorDto>
                {
                    new MapFloorDto { Level = 0, Name = NameFor(0), MinY = minY - FloorMargin, MaxY = maxY + FloorMargin }
                };
            }

            var ground = GroundIndex(bands, spawns);

            // Over the cap: drop the thinnest bands, never the ground. Done before the levels are
            // numbered, so the numbering has no holes in it.
            if (bands.Count > MaxFloors)
            {
                var keep = bands
                    .Select((band, index) => new { band, index })
                    .OrderByDescending(b => b.index == ground ? int.MaxValue : b.band.Count)
                    .Take(MaxFloors)
                    .OrderBy(b => b.band.MinY)
                    .ToList();

                var kept = keep.FindIndex(b => b.index == ground);
                ground = kept >= 0 ? kept : 0;
                bands = keep.Select(b => b.band).ToList();
            }

            var floors = new List<MapFloorDto>(bands.Count);
            for (var i = 0; i < bands.Count; i++)
            {
                var level = i - ground;
                floors.Add(new MapFloorDto
                {
                    Level = level,
                    Name = NameFor(level),
                    MinY = bands[i].MinY - FloorMargin,
                    MaxY = bands[i].MaxY + FloorMargin
                });
            }

            return floors;
        }

        /// <summary>The half-metre histogram of NavMesh vertex Y, reduced to bands: maximal runs of
        /// bins each holding at least 2 % of all vertices, with runs less than BandGap apart merged.
        /// Ascending by height. Empty when there are no usable vertices.</summary>
        /// <param name="vertices">The NavMesh triangulation's vertices.</param>
        private static List<Band> Bands(Vector3[] vertices)
        {
            var bands = new List<Band>();
            if (vertices == null || vertices.Length == 0) return bands;

            var bins = new Dictionary<int, int>();
            var counted = 0;

            foreach (var v in vertices)
            {
                if (!IsFinite(v.y)) continue;

                var bin = Mathf.FloorToInt(v.y / BinHeight);
                bins.TryGetValue(bin, out var count);
                bins[bin] = count + 1;
                counted++;
            }

            if (counted == 0) return bands;

            var threshold = counted * BandBinShare;
            var dense = bins.Where(b => b.Value >= threshold).OrderBy(b => b.Key).ToArray();

            foreach (var bin in dense)
            {
                // A new band unless this bin continues the last one.
                if (bands.Count > 0 && bands[bands.Count - 1].LastBin == bin.Key - 1)
                {
                    var last = bands[bands.Count - 1];
                    last.LastBin = bin.Key;
                    last.Count += bin.Value;
                    continue;
                }

                bands.Add(new Band { FirstBin = bin.Key, LastBin = bin.Key, Count = bin.Value });
            }

            // Two bands nearer than a storey are one floor read twice - a ramp, a mezzanine, a
            // ground that slopes. Merged until nothing is close any more, since merging two can
            // bring the result within reach of a third.
            for (var i = bands.Count - 1; i > 0; i--)
            {
                if (bands[i].MinY - bands[i - 1].MaxY >= BandGap) continue;

                bands[i - 1].LastBin = bands[i].LastBin;
                bands[i - 1].Count += bands[i].Count;
                bands.RemoveAt(i);
            }

            return bands;
        }

        /// <summary>Which band is the ground floor: the one holding the median spawn point's height,
        /// because that is where players start and it is the floor a map is drawn from. Falls back to
        /// the band with the most NavMesh in it when there are no markers, or when the median lands in
        /// the air between bands.</summary>
        /// <param name="bands">The bands, ascending.</param>
        /// <param name="spawns">Spawn point marker positions.</param>
        private static int GroundIndex(List<Band> bands, Vector3[] spawns)
        {
            var median = MedianY(spawns);

            if (IsFinite(median))
            {
                for (var i = 0; i < bands.Count; i++)
                {
                    if (median >= bands[i].MinY && median <= bands[i].MaxY) return i;
                }
            }

            var best = 0;
            for (var i = 1; i < bands.Count; i++)
            {
                if (bands[i].Count > bands[best].Count) best = i;
            }

            return best;
        }

        private static float MedianY(Vector3[] spawns)
        {
            if (spawns == null || spawns.Length == 0) return float.NaN;

            var ys = spawns.Select(s => s.y).Where(IsFinite).ToArray();
            if (ys.Length == 0) return float.NaN;

            Array.Sort(ys);
            return ys.Length % 2 == 1
                ? ys[ys.Length / 2]
                : (ys[ys.Length / 2 - 1] + ys[ys.Length / 2]) * 0.5f;
        }

        /// <summary>Positions of the scene's SpawnPointMarkers, from LocationScene's registered array
        /// so disabled markers count, falling back to a scene search. Never null.</summary>
        private static Vector3[] SpawnMarkerPositions()
        {
            try
            {
                var registered = LocationScene.GetAll<SpawnPointMarker>()
                    ?.Where(m => m != null)
                    .Select(m => m.transform.position)
                    .ToArray();

                if (registered != null && registered.Length > 0) return registered;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: LocationScene.GetAll<SpawnPointMarker> failed ({ex.Message}) - falling back to a scene search.");
            }

            return UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>()
                .Where(m => m != null)
                .Select(m => m.transform.position)
                .ToArray();
        }

        /// <summary>"Ground", "Floor 2", "Floor 3", "Basement", "Basement 2" - what the Maps tab shows
        /// and what an objective's floor vocabulary is matched against.</summary>
        /// <param name="level">0 for the ground floor, positive up, negative down.</param>
        private static string NameFor(int level)
        {
            if (level == 0) return "Ground";
            if (level > 0) return $"Floor {level + 1}";
            return level == -1 ? "Basement" : $"Basement {-level}";
        }

        // --- helpers -------------------------------------------------------------------------

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>The smaller of two heights, ignoring one that is not a number. Float.MaxValue is
        /// treated as "nothing was added" - a Box's initial value.</summary>
        private static float Lower(float a, float b) => Pick(a, b, lower: true);

        /// <summary>The larger of two heights, on the same terms as <see cref="Lower"/>.</summary>
        private static float Higher(float a, float b) => Pick(a, b, lower: false);

        private static float Pick(float a, float b, bool lower)
        {
            var aOk = IsFinite(a) && a != float.MaxValue && a != float.MinValue;
            var bOk = IsFinite(b) && b != float.MaxValue && b != float.MinValue;

            if (!aOk) return bOk ? b : float.NaN;
            if (!bOk) return a;
            return lower ? Mathf.Min(a, b) : Mathf.Max(a, b);
        }

        private static string F(float v) =>
            IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

        private static string F(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "n/a" : v.ToString("0.0", CultureInfo.InvariantCulture);

        private static string Signed(int level) =>
            level > 0 ? $"+{level}" : level.ToString(CultureInfo.InvariantCulture);

        private static string Percent(int part, int whole) =>
            (part * 100f / whole).ToString("0.0", CultureInfo.InvariantCulture);

        private static string Percent(float share) =>
            (share * 100f).ToString("0.0", CultureInfo.InvariantCulture);

        // --- types ---------------------------------------------------------------------------

        /// <summary>The finished rectangle in whole metres, as it goes on the wire.</summary>
        private struct Rect
        {
            public double MinX;
            public double MinZ;
            public double MaxX;
            public double MaxZ;
        }

        /// <summary>One run of dense histogram bins. Y is derived from the bin indices rather than
        /// stored, so a merge cannot leave the two disagreeing.</summary>
        private sealed class Band
        {
            public int FirstBin;
            public int LastBin;
            public int Count;

            public float MinY => FirstBin * BinHeight;
            public float MaxY => (LastBin + 1) * BinHeight;
        }

        /// <summary>A 3D AABB accumulated over colliders, terrains or points, with where it came from
        /// and how many objects went into it. Y is kept only for the single-floor fallback.</summary>
        private sealed class Box
        {
            public Box(string source) => Source = source;

            public readonly string Source;

            public int Count;
            public bool Valid;

            public float MinX = float.MaxValue;
            public float MinY = float.MaxValue;
            public float MinZ = float.MaxValue;
            public float MaxX = float.MinValue;
            public float MaxY = float.MinValue;
            public float MaxZ = float.MinValue;

            public void Add(Bounds bounds)
            {
                Add(bounds.min);
                Add(bounds.max);
            }

            public void Add(Vector3 p)
            {
                if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z)) return;

                if (p.x < MinX) MinX = p.x;
                if (p.y < MinY) MinY = p.y;
                if (p.z < MinZ) MinZ = p.z;
                if (p.x > MaxX) MaxX = p.x;
                if (p.y > MaxY) MaxY = p.y;
                if (p.z > MaxZ) MaxZ = p.z;

                // A box with no area at all is no more use than none: a single degenerate volume
                // must not win the ranking over a real Terrain or NavMesh.
                Valid = MaxX > MinX && MaxZ > MinZ;
            }
        }
    }
}
