using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// The zone positions harvested from loaded raids, one file per map under
    /// user/mods/QuestTree/zones/. This is what makes an objective's zone id - which the quest
    /// data has always carried - into a place on the map, without asking any website.
    ///
    /// Harvests of one map are unioned by zone: Factory day and night share a file but not a
    /// scene (the night scene has zones the day one lacks, and mods spawn zones per variant), so
    /// a replace-whole file lost whichever variant was raided first. A zone seen again at a new
    /// position keeps both entries until the file is deleted - a moved zone after a game update
    /// is rare and visible; a lost zone was neither. Shipped seed files are read exactly like
    /// harvested ones, so a release can carry them and a later raid still adds to them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ZoneStore(ISptLogger<ZoneStore> logger)
    {
        /// <summary>Maps that load the same scene, so a harvest of one serves the other. The
        /// quest data keys them separately (different location ids), the triggers are identical.</summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["factory4_night"] = "factory4_day",
            ["Sandbox_high"] = "Sandbox"
        };

        /// <summary>The map name becomes a file name, so it is held to what a location id can be.</summary>
        private static readonly Regex SafeName = new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);

        /// <summary>Names the regex admits that Windows still treats as devices: "NUL.json" is the
        /// NUL device, and "CON.json" can hang a write.</summary>
        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Bounds on one harvested entry. A zone id or a template id is a short token;
        /// anything longer is not from a raid. Coordinates must be finite: an Infinity written
        /// to the file is refused by the reader, which would cost the map its whole history.</summary>
        private const int MaxIdLength = 128;
        private const int MaxKindLength = 64;

        /// <summary>ClientVersion is copied into the file verbatim and was never bounded, so a
        /// 100 MB version string with one trigger passed every other check and landed on disk.</summary>
        private const int MaxClientVersionLength = 64;

        /// <summary>Coordinates were checked finite but not bounded, so 3.4e38 was accepted and
        /// became a pin position on every player's map. Tarkov's maps fit inside a couple of
        /// kilometres; this is generous by two orders of magnitude and still finite enough to draw.</summary>
        private const float MaxCoordinate = 100_000f;

        /// <summary>Bounds on a harvested extent (schema v2).
        ///
        /// MaxFloors: the bands come from clustering NavMesh heights, and no Tarkov map has eight
        /// walkable layers. A client reporting more has found noise, not storeys, and each band costs
        /// the client a map layer and - later in 1.19.0 - a captured picture.
        ///
        /// MaxFloorNameLength: a floor name is a label on screen. Long enough for "Basement 2",
        /// short enough that it cannot be a payload.
        ///
        /// MaxSampledAtLength: the one free-text field on an extent. Bounded BEFORE it is parsed for
        /// the same reason MaxClientVersionLength exists - a 100 MB string that happens to satisfy
        /// every other check must not reach the disk - and bounded rather than truncated because a
        /// cut-off timestamp is not a timestamp.
        ///
        /// MinExtentArea / MaxExtentSide: 10 m x 10 m is smaller than Factory; 25 km is a hundred
        /// times Streets' long side. Between those two an extent is at worst wrong, outside them it is
        /// not a measurement of a map. The area floor matters because the client DIVIDES by the
        /// rectangle to place a pin: a one-metre extent puts every pin in the same pixel.
        ///
        /// MinTriggerCoverage: the check that decides whether the rectangle and the triggers in the
        /// same post describe the same world. See ExtentIsUsable.</summary>
        private const int MaxFloors = 8;
        private const int MaxFloorNameLength = 32;
        private const int MaxSampledAtLength = 64;
        private const double MinExtentArea = 100d;
        private const double MaxExtentSide = 25_000d;
        private const double MinTriggerCoverage = 0.9d;

        /// <summary>The extent sources, BEST FIRST, which is what makes this array the ranking: its
        /// index is the rank, so nothing else has to agree about which source wins - and it MUST be
        /// the order the client measures in (MapExtentProbe.TryProbe: NavMesh, then Terrain, then
        /// BorderZone), or the two halves fight. Until 2026-09-23 this array held the plan's original
        /// order (BorderZone first) while the client had long since been changed to NavMesh first
        /// because a measured Customs raid said so: every Interchange harvest then sent a 965x925 m
        /// NavMesh rectangle, the store kept a 1073x1033 m terrain one from an older build as
        /// "better ranked", and the shipped pictures disagreed with the zone file by 54 m on every
        /// edge. The NavMesh box is the rectangle the pictures and the 3D relief are actually drawn
        /// over; terrain covers ground the player never reaches; BorderZones are absent on some
        /// maps. A stored terrain extent now loses to any incoming NavMesh one.</summary>
        private static readonly string[] ExtentSources = { "navmesh", "terrain", "borderzone" };

        /// <summary>A ceiling on one map's file. Real maps hold a few hundred entries; the union
        /// never shrinks, and a client that varies positions by a metre could otherwise grow it
        /// without bound.</summary>
        private const int MaxEntriesPerMap = 50_000;

        private static readonly JsonSerializerOptions FileOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        private readonly object _lock = new();

        /// <summary>Canonical map name -> file, or null once a map is known to have no file. Read
        /// once; Save replaces the entry.</summary>
        private readonly Dictionary<string, ZoneFile?> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Zone id -> the maps it was harvested on. Built lazily, dropped by Save.</summary>
        private Dictionary<string, IReadOnlyCollection<string>>? _zoneToMap;

        /// <summary>Canonical maps with at least one usable trigger. Built beside _zoneToMap.</summary>
        private HashSet<string>? _harvestedMaps;

        private static string Folder =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "zones");

        public static string Canonical(string map) =>
            Aliases.TryGetValue(map, out var canonical) ? canonical : map;

        public static bool IsValidMapName(string? map) =>
            map != null && SafeName.IsMatch(map) && !ReservedNames.Contains(map);

        /// <summary>Drops the entries of a harvest that cannot be stored or drawn, in place, and
        /// says how many. The client only sends what it read from a scene, but a scene can hold
        /// a destroyed object's NaN position, and nothing but this stands between a hostile or
        /// buggy Fika client and the file every other player's pins are drawn from.
        ///
        /// The count covers triggers and quest items only. An unusable EXTENT is not counted as a
        /// dropped entry: the caller turns a non-zero count plus an empty harvest into "nothing usable
        /// harvested" and refuses the post, and a bad rectangle must not cost a map its triggers.
        /// It is reported through <paramref name="warn"/> instead, one line naming the map and the
        /// reason, because the extent is the one part of a harvest that can be wrong while being
        /// well-formed - a rectangle in the wrong place draws every pin in the wrong place, and
        /// silence here would look exactly like success.</summary>
        /// <param name="request">The harvest, edited in place.</param>
        /// <param name="warn">Where to say that an extent was dropped, or null to say nothing. Passed
        /// in rather than logged directly because this method is static - it is called before any
        /// store instance is involved - and because the route already owns the once-per-boot dedup
        /// that keeps a client in a loop from filling the log.</param>
        public static int Sanitise(ZoneHarvestRequest request, Action<string>? warn = null)
        {
            var dropped = 0;

            if (request.ClientVersion != null && request.ClientVersion.Length > MaxClientVersionLength)
                request.ClientVersion = request.ClientVersion[..MaxClientVersionLength];

            if (request.Triggers != null)
            {
                dropped += request.Triggers.RemoveAll(t =>
                    t == null || string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > MaxIdLength ||
                    !InWorld(t.X) || !InWorld(t.Y) || !InWorld(t.Z));

                foreach (var t in request.Triggers)
                {
                    t.Kind ??= "";
                    if (t.Kind.Length > MaxKindLength) t.Kind = t.Kind[..MaxKindLength];
                    if (!Finite(t.ExtentX) || !Finite(t.ExtentY) || !Finite(t.ExtentZ))
                        t.ExtentX = t.ExtentY = t.ExtentZ = 0f;
                }
            }

            if (request.QuestItems != null)
            {
                dropped += request.QuestItems.RemoveAll(i =>
                    i == null || string.IsNullOrWhiteSpace(i.TemplateId) || i.TemplateId.Length > MaxIdLength ||
                    (i.ItemId != null && i.ItemId.Length > MaxIdLength) ||
                    !InWorld(i.X) || !InWorld(i.Y) || !InWorld(i.Z));

                foreach (var i in request.QuestItems) i.ItemId ??= "";
            }

            // LAST, so the containment test below reads the triggers that will actually be stored
            // rather than the ones that arrived: a harvest whose NaN positions were just removed
            // would otherwise fail its own extent on entries nothing will ever draw.
            if (request.Extent != null && !ExtentIsUsable(request.Map, request.Extent, request.Triggers, out var problem))
            {
                request.Extent = null;
                warn?.Invoke(problem);
            }

            return dropped;
        }

        /// <summary>Whether this extent may be stored, and if not, the line to log.
        ///
        /// Everything here is a DROP, never a repair, with two exceptions noted at their check:
        /// rotation is forced to 0 and a long floor name is truncated. The difference is whether
        /// guessing changes where a pin lands. Clamping an inverted rectangle or inventing a missing
        /// bound would produce a plausible extent nobody measured, and the client would then draw a
        /// map picture stretched over it with every pin confidently in the wrong place; dropping it
        /// falls back to "this map has no measured rectangle", which the client already handles by
        /// asking for a raid. The harvest's triggers are kept either way.
        ///
        /// <paramref name="map"/> is the name as posted, only ever for the message; the caller has
        /// already held it to IsValidMapName.</summary>
        private static bool ExtentIsUsable(
            string map, MapExtentDto extent, List<HarvestedTrigger>? triggers, out string problem)
        {
            string Drop(string reason)
            {
                return $"Quest Tracker: extent for '{map}' dropped - {reason}. The map keeps its " +
                       "harvested pins; raid it again to measure the rectangle.";
            }

            // The source first: it names the rectangle in every message below, and it is a string a
            // client chose, so it is clipped before it is ever printed.
            var named = extent.Source ?? "";
            var source = ExtentSources.FirstOrDefault(s => string.Equals(s, named, StringComparison.OrdinalIgnoreCase));
            if (source == null)
            {
                problem = Drop($"source '{Clip(named, MaxFloorNameLength)}' is not one of " +
                               string.Join(", ", ExtentSources));
                return false;
            }

            // Normalised to the canonical casing, so the rank lookup and the log lines in Save agree
            // with the table above whatever the client sent.
            extent.Source = source;

            if (!InWorld(extent.MinX) || !InWorld(extent.MinZ) || !InWorld(extent.MaxX) || !InWorld(extent.MaxZ))
            {
                problem = Drop($"its corners are not finite metres inside +/-{MaxCoordinate:0} m");
                return false;
            }

            // Not <=: a rectangle with no width divides by zero on the client.
            if (extent.MinX >= extent.MaxX || extent.MinZ >= extent.MaxZ)
            {
                problem = Drop("its minimum corner is not below its maximum corner " +
                               $"({extent.MinX:0.#},{extent.MinZ:0.#} .. {extent.MaxX:0.#},{extent.MaxZ:0.#})");
                return false;
            }

            var width = extent.MaxX - extent.MinX;
            var depth = extent.MaxZ - extent.MinZ;

            if (width * depth < MinExtentArea)
            {
                problem = Drop($"it covers {width * depth:0.#} m2, under the {MinExtentArea:0} m2 a map needs");
                return false;
            }

            if (width > MaxExtentSide || depth > MaxExtentSide)
            {
                problem = Drop($"it is {width:0} x {depth:0} m, past the {MaxExtentSide / 1000:0} km limit");
                return false;
            }

            // FORCED, not checked: see the class summary on MapExtentDto. This release captures
            // straight down with the image axis-aligned to the world, so 0 is the only rotation that
            // describes the picture, and storing anything else would rotate every pin on the map.
            extent.Rotation = 0f;

            if (string.IsNullOrWhiteSpace(extent.SampledAt) || extent.SampledAt.Length > MaxSampledAtLength ||
                ParseSampledAt(extent.SampledAt) == DateTime.MinValue)
            {
                problem = Drop("its sampledAt is not a timestamp, so nothing could rank it against a later harvest");
                return false;
            }

            extent.Floors ??= new List<MapFloorDto>();

            if (extent.Floors.Count > MaxFloors)
            {
                problem = Drop($"it claims {extent.Floors.Count} floors, past the {MaxFloors} a map may have");
                return false;
            }

            var levels = new HashSet<int>();
            foreach (var floor in extent.Floors)
            {
                if (floor == null)
                {
                    problem = Drop("one of its floors is empty");
                    return false;
                }

                // Truncated rather than dropped, the one place in this method where a repair happens:
                // the name is a caption the client prints beside a layer button, it decides nothing,
                // and a map that measured well is not worth losing over a long label. Empty is still
                // fatal - an unnamed layer is a button nobody can read.
                floor.Name = Clip((floor.Name ?? "").Trim(), MaxFloorNameLength);

                if (floor.Name.Length == 0)
                {
                    problem = Drop($"its floor at level {floor.Level} has no name");
                    return false;
                }

                if (!InWorld(floor.MinY) || !InWorld(floor.MaxY) || floor.MinY >= floor.MaxY)
                {
                    problem = Drop($"floor '{floor.Name}' spans {floor.MinY:0.#}..{floor.MaxY:0.#} m, which is not a height band");
                    return false;
                }

                if (!levels.Add(floor.Level))
                {
                    problem = Drop($"two of its floors are both level {floor.Level}");
                    return false;
                }
            }

            // The check that makes the rest of them worth having, and the only one that compares the
            // extent against something measured independently of it: the triggers in this same post
            // were read from the same scene, at raw world coordinates, by the same client. If the
            // rectangle does not contain them it is not this map's rectangle - a stale extent kept
            // across a map change, a unit mix-up, or a peer inventing one - and a map picture
            // stretched over it would put every pin somewhere plausible and wrong.
            //
            // A harvest with no triggers at all is exempt: there is nothing to contradict, and the
            // quest-item-only harvest that shape describes is a real one.
            var total = triggers?.Count ?? 0;
            if (total > 0)
            {
                var inside = triggers!.Count(t =>
                    t.X >= extent.MinX && t.X <= extent.MaxX && t.Z >= extent.MinZ && t.Z <= extent.MaxZ);

                if (inside < total * MinTriggerCoverage)
                {
                    problem = $"Quest Tracker: extent for '{map}' from {source} holds only {inside} of " +
                              $"{total} triggers - ignored.";
                    return false;
                }
            }

            problem = "";
            return true;
        }

        /// <summary>A client-supplied string cut to a length before it is logged or stored, with its
        /// line breaks turned into spaces. The same treatment HarvestedTrigger.Kind gets above, as a
        /// helper because the extent has three of them.
        ///
        /// THE LINE BREAKS ARE THE POINT, and leaving them out was a real hole: both values that pass
        /// through here - the extent's source and a floor's name - are text a Fika peer chose on an
        /// unauthenticated route, and both are printed verbatim into a log line by ExtentIsUsable. A
        /// floor called "Ground\nQuest Tracker: ..." therefore wrote a SECOND line into the server log
        /// that reads exactly like one of this mod's own. Proven in a harness: the refusal line for a
        /// bad height band arrived split across two lines. The floor name is also stored and shown on
        /// a layer button, so this is not only about the log.
        ///
        /// A space rather than a refusal, for the reason the name is clipped rather than refused at
        /// all: it is a caption and it decides nothing - the same repair MapStore already applied to a
        /// label's text and QuestTreeRouter to a build key.</summary>
        private static string Clip(string value, int max)
        {
            var line = value.Replace('\r', ' ').Replace('\n', ' ');

            return line.Length <= max ? line : line[..max];
        }

        /// <summary>An extent's sample time as a UTC instant, or DateTime.MinValue when it is not a
        /// timestamp at all. MinValue rather than an exception or a null: this is asked both when
        /// validating a fresh extent - where MinValue is refused - and when ranking a stored one
        /// against an incoming one, where a file hand-edited into nonsense should simply lose.</summary>
        private static DateTime ParseSampledAt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSampledAtLength) return DateTime.MinValue;

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : DateTime.MinValue;
        }

        /// <summary>How good an extent's source is, higher being better, and -1 for an extent whose
        /// source is not in the table. Only a Sanitise-approved extent is ever stored, so -1 can only
        /// come from a file edited by hand, and it loses to anything.
        ///
        /// The not-found case is spelled out rather than folded into the arithmetic: FindIndex answers
        /// -1, and "Length - 1 - index" turns that into 3 - one BETTER than borderzone - so the
        /// shorter version of this method promoted an unrecognised source to the best rank there
        /// is.</summary>
        private static int RankOf(MapExtentDto extent)
        {
            var index = Array.FindIndex(
                ExtentSources, s => string.Equals(s, extent.Source, StringComparison.OrdinalIgnoreCase));

            return index < 0 ? -1 : ExtentSources.Length - 1 - index;
        }

        /// <summary>Which of two extents to keep, the stored one or the one that just arrived.
        ///
        /// Better SOURCE first, then newer sample - and better means NAVMESH first, then terrain, then
        /// BorderZone (see ExtentSources), the order the client measures in. The NavMesh box is the
        /// rectangle the capture's pictures and 3D relief are actually drawn over; terrain covers ground
        /// the player never reaches; BorderZones are absent on some maps. The ranking used to be the
        /// other way round, BorderZone first, while the client had long since been changed to NavMesh
        /// first - and on Interchange that let an older build's 1073x1033 m terrain rectangle beat every
        /// new 965x925 m NavMesh one, so the shipped pictures disagreed with the zone file by 54 m on
        /// every edge. Source outranks recency because the sources are not equally good measurements of
        /// the same thing: ranking by time instead would let one raid whose NavMesh failed to load
        /// coarsen a map every earlier raid had measured properly - and on Fika, whichever peer posted
        /// last would decide.
        ///
        /// An incoming null returns the stored extent: a v1 client, a headless peer, or a v2 client
        /// whose own containment check failed all send no extent, and none of them is evidence that
        /// the stored rectangle is wrong. Erasing on null would mean one old client in a Fika raid
        /// wiped the map every other player had measured.
        ///
        /// Returns one of the two arguments by reference, never a copy, which is what lets Save tell
        /// whether anything changed.</summary>
        private static MapExtentDto? BetterExtent(MapExtentDto? stored, MapExtentDto? incoming)
        {
            if (incoming == null) return stored;
            if (stored == null) return incoming;

            var storedRank = RankOf(stored);
            var incomingRank = RankOf(incoming);

            if (incomingRank != storedRank) return incomingRank > storedRank ? incoming : stored;

            // Strictly newer: a re-post of the same harvest - every Fika client in a raid sends one -
            // must not count as a change, or Save would rewrite the file for each of them.
            return ParseSampledAt(incoming.SampledAt) > ParseSampledAt(stored.SampledAt) ? incoming : stored;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>The double overloads the extent needs: its corners are metres in double precision
        /// because the client computes them from Unity bounds and rounds to the metre, and a float
        /// round-trip of a value near the coordinate ceiling is not the number that was checked.</summary>
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool InWorld(double value) =>
            Finite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;

        /// <summary>Finite AND somewhere a map could plausibly be. Finite alone let 3.4e38 through,
        /// which draws as a pin at the edge of the world on everyone's map.</summary>
        private static bool InWorld(float value) =>
            Finite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;

        /// <summary>Zone id -> the canonical maps it was harvested on, across every map on disk.
        ///
        /// This is what lets a quest declaring "any" be placed on the map it is actually done on:
        /// its objectives name zone ids, and only the harvest knows where those are. Built from
        /// harvested data only - never from the zone's name. Ids like "Aishi_Shoreline_TGCrate_1"
        /// invite string matching, and that guess would file a modded map's quests on whichever
        /// stock map their name resembles.
        ///
        /// A SET of maps per zone, not one. Three shipped zone ids sit on more than one map with no
        /// attacker present - fuel4 on RezervBase and bigmap, exit777 on Labyrinth and bigmap,
        /// rshg_event_04_jaeger_r_point on Shoreline, Woods and bigmap - and all three are named by
        /// real quests. First-harvest-wins dropped one of them, which removes a requirement row, and
        /// an empty requirement list draws GREEN.
        ///
        /// Cached because both payload builders ask, and dropped by Save: a harvest that adds zones
        /// changes the answer, and the marker rebuild it triggers must not run against a stale
        /// index.</summary>
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> ZoneToMap()
        {
            lock (_lock)
            {
                BuildIndexes();
                return _zoneToMap!;
            }
        }

        /// <summary>The canonical maps with at least one usable trigger.
        ///
        /// Deliberately "usable trigger" rather than "a file exists": the question it stands for is
        /// "could an any-location condition have been placed here", and a file holding nothing
        /// placeable answers no. Used to keep the readiness cue neutral on an unharvested map rather
        /// than letting an empty requirement list read as "you are ready".</summary>
        public IReadOnlyCollection<string> HarvestedMaps()
        {
            lock (_lock)
            {
                BuildIndexes();
                return _harvestedMaps!;
            }
        }

        /// <summary>Both indexes from one walk. Caller holds _lock.</summary>
        private void BuildIndexes()
        {
            if (_zoneToMap != null && _harvestedMaps != null) return;

            // OrdinalIgnoreCase, matching every other zone-id comparison in this server - the marker
            // lookup, ZonesWanted, ObjectiveDto.ZoneIds. Case-sensitive here would give a quest pins
            // (found case-insensitively) and no derived location (missed case-sensitively), which is
            // the exact split this index exists to prevent.
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in CachedAndOnDisk())
            {
                if (file?.Triggers == null) continue;

                // ZoneFile.Map, never the file name: filenames carry the disk's casing (Bigmap.json)
                // and the key has to be the canonical one Save wrote.
                var map = file.Map;
                if (string.IsNullOrWhiteSpace(map)) continue;

                foreach (var trigger in file.Triggers)
                {
                    if (trigger == null || string.IsNullOrWhiteSpace(trigger.Id)) continue;

                    // Union, never first-harvest-wins. Besides being wrong on shipped data, keeping
                    // the first mapping means a legitimate harvest arriving after a bad one can
                    // never correct it.
                    if (!index.TryGetValue(trigger.Id, out var set))
                        index[trigger.Id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    set.Add(map);
                    maps.Add(map);
                }
            }

            var frozen = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index) frozen[entry.Key] = entry.Value;

            _zoneToMap = frozen;
            _harvestedMaps = maps;
        }

        /// <summary>Every zone file this server knows about: the cache first, then any file on disk
        /// not already cached.
        ///
        /// The cache first because Save keeps a harvest in memory when the disk write fails, and a
        /// folder-only listing would silently omit it - pins would appear and derivation would not.
        /// Caller holds _lock.</summary>
        private IEnumerable<ZoneFile?> CachedAndOnDisk()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _cache)
            {
                seen.Add(entry.Key);
                if (entry.Value != null) yield return entry.Value;
            }

            if (!System.IO.Directory.Exists(Folder)) yield break;

            foreach (var path in System.IO.Directory.EnumerateFiles(Folder, "*.json"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);

                // Reuses the validation already here rather than trusting the directory.
                if (!IsValidMapName(name)) continue;

                var key = Canonical(name);
                if (!seen.Add(key)) continue;

                var file = Read(key);
                _cache[key] = file;
                if (file != null) yield return file;
            }
        }

        /// <summary>The harvest for a map, or null when none has been taken.</summary>
        public ZoneFile? TryGet(string map)
        {
            if (!IsValidMapName(map)) return null;

            var key = Canonical(map);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;

                var file = Read(key);
                _cache[key] = file;
                return file;
            }
        }

        /// <summary>Maps already warned about their ceiling this boot, so a client that keeps
        /// posting is heard once rather than filling the log.</summary>
        private readonly HashSet<string> OverflowReported = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops newest-first until the union fits, and says how many went.
        ///
        /// Newest-first is the point: the entries already in the file include the shipped seed
        /// zones, which are the ones worth keeping, and the overflow is by definition what this
        /// post is trying to add.</summary>
        private static int DropOverflow(
            Dictionary<string, HarvestedTrigger> triggers,
            Dictionary<string, HarvestedQuestItem> items,
            int overflow)
        {
            var dropped = 0;

            foreach (var key in items.Keys.Reverse().Take(overflow).ToList())
            {
                items.Remove(key);
                dropped++;
            }

            var stillOver = overflow - dropped;
            if (stillOver <= 0) return dropped;

            foreach (var key in triggers.Keys.Reverse().Take(stillOver).ToList())
            {
                triggers.Remove(key);
                dropped++;
            }

            return dropped;
        }

        /// <summary>How often one map may be written. A raid harvests its map up to three times -
        /// a first pass, one after a transit, and a closing pass - so this must not be so tight
        /// that a single player's own raid is throttled.</summary>
        private static readonly TimeSpan WriteWindow = TimeSpan.FromSeconds(20);

        /// <summary>Canonical map -> when it may next be written, and what is waiting.</summary>
        private readonly Dictionary<string, DateTime> _nextWrite = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ZoneHarvestRequest> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Seconds until this map's pending harvest will be written, or 0 when the last
        /// post was applied immediately. The client waits this long before invalidating its caches:
        /// invalidating straight away would refetch the PRE-harvest payload and - because the quest
        /// list latches for the session - cache it for good.</summary>
        public int PendingSeconds(string map)
        {
            var key = Canonical(map);

            lock (_lock)
            {
                if (!_pending.ContainsKey(key)) return 0;
                if (!_nextWrite.TryGetValue(key, out var at)) return 0;

                var wait = at - DateTime.UtcNow;
                return wait <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(wait.TotalSeconds);
            }
        }

        /// <summary>Applies any harvest that was buffered while a map was inside its write window,
        /// and says which maps actually changed. Called by the harvest route, so a later post is
        /// what flushes an earlier one - no timer to own, and nothing is ever lost to the clock.</summary>
        public bool DrainPending()
        {
            List<ZoneHarvestRequest> due;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                due = _pending
                    .Where(e => !_nextWrite.TryGetValue(e.Key, out var at) || now >= at)
                    .Select(e => e.Value)
                    .ToList();

                foreach (var request in due) _pending.Remove(Canonical(request.Map));
            }

            var changed = false;
            foreach (var request in due)
            {
                // The extent counts as a change here for the same reason it does in the route: a
                // buffered harvest whose only news is the map's rectangle is still news, and this
                // is the only place that buffered post is ever written.
                var saved = Save(request, out var added, out var extentChanged);
                if (saved != null && (added > 0 || extentChanged)) changed = true;
            }

            return changed;
        }

        /// <summary>Merges a harvest into whatever is already waiting for that map.
        ///
        /// Buffered rather than refused, which is the whole point: ZoneHarvester posts
        /// fire-and-forget with no retry, so a bare "too soon" throws the post away for good - and
        /// on Fika the first peer to post would win the window while every other player's harvest
        /// was discarded. Returns false only when the buffer itself is at the entry ceiling, which
        /// is the abuse case rather than the normal one.</summary>
        private bool Buffer(string key, ZoneHarvestRequest request)
        {
            if (!_pending.TryGetValue(key, out var waiting))
            {
                _pending[key] = request;
                return true;
            }

            var held = (waiting.Triggers?.Count ?? 0) + (waiting.QuestItems?.Count ?? 0);
            if (held >= MaxEntriesPerMap) return false;

            waiting.Triggers ??= new List<HarvestedTrigger>();
            waiting.QuestItems ??= new List<HarvestedQuestItem>();

            if (request.Triggers != null) waiting.Triggers.AddRange(request.Triggers);
            if (request.QuestItems != null) waiting.QuestItems.AddRange(request.QuestItems);

            // The extent merges by the same rule the file does, or a harvest that arrived inside the
            // write window would lose its rectangle entirely: only the FIRST buffered request is the
            // one Save eventually sees, and on Fika that is whichever peer posted first.
            waiting.Extent = BetterExtent(waiting.Extent, request.Extent);

            return true;
        }

        /// <summary>Whether this harvest was written, or buffered for the map's next window.
        ///
        /// The gate sits above Save rather than at the top of the route: Sanitise has to run either
        /// way, because the buffer may only ever hold validated entries. What is being defended is
        /// the WRITE - Save builds two dictionaries over the whole union inside the lock and
        /// rewrites the entire file indented, which near the ceiling is tens of megabytes a post,
        /// and it holds the lock every ZoneToMap read on the quest and marker paths needs.</summary>
        public ZoneFile? SaveOrBuffer(
            ZoneHarvestRequest request, out int added, out bool extentChanged, out bool buffered)
        {
            added = 0;
            extentChanged = false;
            buffered = false;

            var key = Canonical(request.Map);

            lock (_lock)
            {
                if (_nextWrite.TryGetValue(key, out var at) && DateTime.UtcNow < at)
                {
                    buffered = Buffer(key, request);
                    return null;
                }

                _nextWrite[key] = DateTime.UtcNow + WriteWindow;
            }

            return Save(request, out added, out extentChanged);
        }

        /// <summary>Unions this harvest into the map's file, and writes it when the union changed anything.
        /// A post that adds nothing new - every Fika client in a raid sending the same scene - returns the
        /// file it already had without touching the disk.
        ///
        /// ALWAYS returns a file when it got that far - it cannot return null for a full map any more.
        /// It used to, and the summary saying so outlived the behaviour by three releases: DropOverflow sheds
        /// the excess and keeps the rest, because refusing the post bricked a map permanently. Anyone
        /// reading the router's "map file full" rejection should know it now answers only for the
        /// in-memory BUFFER being full, never the file.
        ///
        /// `added` is how many entries were new - zero when every Fika client in a raid posts the same
        /// scene, which is the case the caller must not rebuild for.
        ///
        /// `extentChanged` is the OTHER reason a caller must rebuild, and it is separate from `added`
        /// rather than folded into it because the two are different news: the first v2 harvest of a
        /// map whose zones were all found long ago adds no entry at all and yet changes the payload
        /// completely - it is the post that gives that map its rectangle. Folded into `added` it
        /// would also corrupt the "N new" count the route logs and the client prints.</summary>
        public ZoneFile? Save(ZoneHarvestRequest request, out int added, out bool extentChanged)
        {
            added = 0;
            extentChanged = false;
            var key = Canonical(request.Map);

            lock (_lock)
            {
                var fromCache = _cache.TryGetValue(key, out var existing);
                if (!fromCache) existing = Read(key);

                var triggers = new Dictionary<string, HarvestedTrigger>(StringComparer.Ordinal);
                var items = new Dictionary<string, HarvestedQuestItem>(StringComparer.Ordinal);

                foreach (var t in existing?.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in existing?.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                var before = triggers.Count + items.Count;

                // Newest wins on an exact match; anything new is added.
                foreach (var t in request.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in request.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                // The excess, never the whole harvest. Refusing the post once a map was full
                // BRICKED that map permanently: pad a real map to the ceiling and no legitimate
                // harvest could ever land there again. Eviction is worse, not better - a
                // HarvestedTrigger carries no timestamp, so there is nothing to evict BY, and
                // insertion order would delete the eleven shipped seed files first.
                var overflow = triggers.Count + items.Count - MaxEntriesPerMap;
                if (overflow > 0)
                {
                    var shed = DropOverflow(triggers, items, overflow);

                    // Once per map per boot, and it names the remedy: refusing quietly would mean
                    // the map can never accept a new zone again while every post still answers
                    // "saved", so the failure would look exactly like success.
                    if (OverflowReported.Add(key))
                    {
                        logger.Warning(
                            $"Quest Tracker: zones/{key}.json is at its {MaxEntriesPerMap} entry ceiling - " +
                            $"{shed} newly harvested entries were dropped. Delete that file to start it " +
                            "over; the next raid there will rebuild it.");
                    }
                }

                added = triggers.Count + items.Count - before;

                // The rectangle merges by rank, not by arrival: see BetterExtent. Compared by
                // REFERENCE, which is exactly what BetterExtent's contract provides - the result is
                // one of its two arguments - so "changed" means "the file would say something
                // different", and a re-post of the same extent by every Fika client in a raid is not
                // a change.
                var extent = BetterExtent(existing?.Extent, request.Extent);
                extentChanged = !ReferenceEquals(extent, existing?.Extent);

                // Nothing new: the file already says all this, so it is not rewritten and the
                // caller is told to skip the marker rebuild. Only when it really is on disk and
                // in the cache, though - a harvest whose write failed is kept in memory with a
                // promise to try the disk next time, and this is next time. A re-read of the same
                // positions with changed flags is not persisted; the flags are informational.
                //
                // extentChanged is part of the test, not an afterthought: the first v2 harvest of a
                // map that was already fully harvested adds no trigger at all, and without this the
                // extent - the entire point of that post - would be dropped on the floor while the
                // route answered "saved".
                if (added == 0 && !extentChanged && existing != null && fromCache &&
                    System.IO.File.Exists(ResolvePath(key)))
                    return existing;

                var file = new ZoneFile
                {
                    Map = key,
                    HarvestedAt = DateTime.UtcNow.ToString("u"),
                    ClientVersion = request.ClientVersion ?? "",
                    Triggers = new List<HarvestedTrigger>(triggers.Values),
                    QuestItems = new List<HarvestedQuestItem>(items.Values),
                    Extent = extent
                };

                try
                {
                    // Written beside the file and moved over it: the file is the union of every
                    // harvest ever taken here, and a write cut short by a crash or a full disk
                    // used to leave a truncated file the reader refuses, losing all of it.
                    System.IO.Directory.CreateDirectory(Folder);
                    var path = ResolvePath(key);
                    var temp = path + ".tmp";
                    System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(file, FileOptions));
                    System.IO.File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Kept in memory regardless: this server session still benefits, and the next
                    // raid will try the disk again.
                    logger.Warning($"Quest Tracker: could not write zones/{key}.json ({ex.Message}) - kept for this session only.");
                }

                _cache[key] = file;

                // Both indexes, inside the lock that already guards the cache: a harvest that adds
                // zones changes which map a zone is on, and the marker rebuild it triggers must not
                // run against a stale answer.
                _zoneToMap = null;
                _harvestedMaps = null;

                logger.Info(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"harvested on '{key}'" + (request.Map != key ? $" (as '{request.Map}')" : "") +
                    (existing != null ? $", {added} new" : "") +
                    (extent != null && extentChanged
                        ? $", extent {extent.MaxX - extent.MinX:0} x {extent.MaxZ - extent.MinZ:0} m " +
                          $"({extent.Source}, {extent.Floors.Count} floors)"
                        : "") + ".");

                return file;
            }
        }

        /// <summary>The key the client de-duplicates triggers with as well, so the two sides agree on
        /// what "the same zone" means: id plus rounded position, since one zone can be several
        /// volumes.</summary>
        private static string TriggerKey(HarvestedTrigger t) =>
            $"{t.Id}|{Numbers.Grid(t.X)}|{Numbers.Grid(t.Y)}|{Numbers.Grid(t.Z)}";

        /// <summary>The same shape as a trigger's key, and for the same reason: a template at a rounded
        /// position is one quest-item place, however many raids have seen it.
        ///
        /// IT USED TO PREFER THE ITEM'S OWN ID, which is right inside one raid and wrong across them -
        /// and the shipped seeds prove it. A LootItem's id is minted when the raid generates its loot,
        /// so the same crate of documents on the same shelf arrives under a new id every raid and was
        /// stored as a new entry every time. Interchange ships 24 quest-item entries at 12 distinct
        /// positions and Factory 20 at 14 - every duplicate a second sighting of one place, all with
        /// distinct ids - while the triggers, which have always been keyed this way, carry not one
        /// duplicate on any of the eleven seeds.
        ///
        /// What that cost: the marker builder emits one pin per ENTRY and reports the count as
        /// Alternatives, which the client draws as "(1 of N)". So a well-raided map told the player an
        /// item might be in any of N places when every one of them was the same place, and the number
        /// grew with the raids played rather than with the spawns. The union also grew per raid instead
        /// of settling, toward a ceiling that sheds newly harvested entries when it is reached.
        ///
        /// The client's own ZoneHarvester still keys its within-raid pass on the item id, which is
        /// correct there: inside one raid the id is unique per object, and this union is the only place
        /// the question is "have I seen this before".</summary>
        private static string ItemKey(HarvestedQuestItem i) =>
            $"{i.TemplateId}|{Numbers.Grid(i.X)}|{Numbers.Grid(i.Y)}|{Numbers.Grid(i.Z)}";

        private static string PathFor(string key) => System.IO.Path.Combine(Folder, key + ".json");

        /// <summary>The map's file as it exists on disk, whatever its casing, else where a new one
        /// goes. The cache is case-insensitive but a filesystem may not be: a harvest posted as
        /// "Bigmap" wrote Bigmap.json, which a Linux host then never read back as bigmap.</summary>
        private static string ResolvePath(string key)
        {
            var wanted = PathFor(key);
            if (System.IO.File.Exists(wanted) || !System.IO.Directory.Exists(Folder)) return wanted;

            var options = new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive };
            foreach (var candidate in System.IO.Directory.EnumerateFiles(Folder, key + ".json", options))
                return candidate;

            return wanted;
        }

        private ZoneFile? Read(string key)
        {
            try
            {
                var path = ResolvePath(key);
                if (!System.IO.File.Exists(path)) return null;

                var file = JsonSerializer.Deserialize<ZoneFile>(System.IO.File.ReadAllText(path), FileOptions);
                if (file == null) return null;

                // Written by a newer server than this one. The fields this build recognises are NOT
                // taken for the whole file: a v3 file could mean a trigger's position is relative to
                // something v2 never recorded, and drawing those pins would put quest markers in the
                // wrong place while every log line said the map was fine.
                //
                // Skipped exactly as an unreadable file is skipped, with the same consequence: this
                // map has no harvested pins this boot, and the next raid there writes a fresh
                // current-schema file over it. That trade is deliberate - a downgrade costs one raid
                // per map, and there is nothing an older reader could do with a newer file that would
                // not be a guess. Read's result, null included, is cached by both callers, so this is
                // said once per boot per map rather than on every payload rebuild.
                if (file.SchemaVersion > ZoneFile.CurrentSchemaVersion)
                {
                    logger.Warning(
                        $"Quest Tracker: zones/{key}.json is schema v{file.SchemaVersion}, newer than the " +
                        $"v{ZoneFile.CurrentSchemaVersion} this server reads - skipped, so '{key}' has no " +
                        "harvested zones this boot. Update the server half, or delete that file and raid the map again.");
                    return null;
                }

                // Filled in rather than dereferenced. Files this server wrote always carry both arrays, but
                // a hand-edited or third-party seed with "triggers": null threw into the catch below and was
                // reported as unreadable - discarding the questItems it did have, and telling the player to
                // raid the map again to fix a file that was fine. BuildIndexes guards for exactly this
                // shape; this path did not.
                file.Triggers ??= new List<HarvestedTrigger>();
                file.QuestItems ??= new List<HarvestedQuestItem>();

                // A v1 file - every shipped seed - has no extent, which lands here as null and is the
                // truth about it: that map has not been measured. Not defaulted to an empty rectangle,
                // which the payload would then hand the client as a map 0 m across. An extent that IS
                // present is trusted as written: Sanitise ran on it before it reached the disk, and
                // re-validating here would have to decide what to do with a file it disliked while
                // nothing stood ready to replace it.
                //
                // Floors filled in for the same reason the two lists above are: a hand-edited or
                // third-party file with "floors": null is otherwise a rectangle whose floor list
                // throws the first time anything counts it - the log line right below, for one.
                if (file.Extent != null) file.Extent.Floors ??= new List<MapFloorDto>();

                logger.Detail(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"known for '{key}' (harvested {file.HarvestedAt})" +
                    (file.Extent != null
                        ? $", extent {file.Extent.MaxX - file.Extent.MinX:0} x " +
                          $"{file.Extent.MaxZ - file.Extent.MinZ:0} m ({file.Extent.Source})"
                        : ", no extent yet") + ".");

                return file;
            }
            catch (Exception ex)
            {
                // A corrupt file costs one map its harvested pins until the next raid there.
                logger.Warning($"Quest Tracker: could not read zones/{key}.json ({ex.Message}) - raid the map again to replace it.");
                return null;
            }
        }
    }
}
