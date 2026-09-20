using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.DI;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Server.Core.Services.Locales;

namespace QuestTreeServer
{
    /// <summary>
    /// Where the items your quests want actually spawn, as world coordinates the client can pin.
    ///
    /// The quest data itself carries no positions - of roughly 10,600 objectives not one has a
    /// coordinate, and the few that name a zone name it as a string whose position lives in the map
    /// bundle rather than in any JSON. So a marker cannot come from the quest.
    ///
    /// It can come from the loot table. Each location's looseLoot carries spawnpointsForced: real
    /// world positions paired with the item template that spawns there, and quest items are placed
    /// through exactly that mechanism. Cross-referencing an objective's target items against those
    /// forced spawns gives a genuine "the thing you need is here" marker, derived from the server's
    /// own database rather than guessed, hardcoded, or fetched from a website.
    ///
    /// It is deliberately partial and the client says so. On Customs, 15 of the 28 item templates
    /// named by quest objectives have forced spawns; the rest are handed over rather than found, or
    /// come out of containers. A partial set of true markers beats a complete set of invented ones.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class MapMarkerPayloadBuilder(
        ISptLogger<MapMarkerPayloadBuilder> logger,
        TemplateTable templateTable,
        LocationTable locationTable,
        LocaleService localeService,
        TarkovDevClient tarkovDev,
        ObjectiveGpsClient objectiveGps,
        ZoneStore zoneStore,
        QuestFacts facts) : IOnLoad
    {
        /// <summary>
        /// Builds the markers while the server is starting rather than when the client first asks.
        ///
        /// This is the difference between a working Maps tab and a frozen game. The first build
        /// reads every map's loose-loot table off disk - 42MB for Customs alone - and takes several
        /// seconds; SPT's client request handler is synchronous and runs on Unity's main thread, so
        /// paying that on the first request froze the whole game for the duration. Paid here it
        /// costs the server a few seconds of its own startup, where there is nothing to block.
        /// </summary>
        public async Task OnLoadAsync(CancellationToken cancellationToken)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // Awaited here so the network calls happen during startup rather than inside the
                // synchronous request handler that serves the client - but SPT does wait on this,
                // so they run together and say so first: a quiet pause at boot with no line in the
                // log reads as a hang.
                logger.Detail("Quest Tracker: fetching quest objective locations (tarkov.dev, tarkovdata)...");

                // Started together, awaited apart: with Task.WhenAll one source's fault threw
                // away the other's answer too.
                var locations = tarkovDev.GetLocationsAsync(cancellationToken);
                var places = objectiveGps.GetPlacesAsync(cancellationToken);

                try
                {
                    _objectiveLocations = await locations;
                }
                catch (Exception ex)
                {
                    logger.Error($"Quest Tracker: could not fetch tarkov.dev objective locations: {ex}");
                }

                try
                {
                    _objectivePlaces = await places;
                }
                catch (Exception ex)
                {
                    logger.Error($"Quest Tracker: could not fetch the objective GPS file: {ex}");
                }
            }
            catch (Exception ex)
            {
                // Both clients swallow their own failures, so this is for the unexpected - and an
                // exception out of OnLoadAsync aborts SPT's boot.
                logger.Error($"Quest Tracker: could not fetch objective locations: {ex}");
            }

            // The build itself runs BEHIND the boot, not on it. Measured at 5.0 and 5.7 s on two
            // boots - 70% of everything this mod cost the server's start - almost all of it the
            // loose-loot tables being deserialised off disk (42 MB for Customs alone; see
            // CollectMarkers for why that is per read, not per process). Nothing at boot needs the
            // answer: a client request arriving before it is done waits on _buildLock inside
            // GetPayloadJson for the build already in progress, which is the same wait it would
            // have paid before, and one that in practice never happens - a player is not at the
            // map tab within seconds of the server's port opening.
            //
            // It overlaps whatever loads after this - this mod's own QuestPayloadBuilder, and any
            // other mod's IOnLoad at default priority. Everything Build reads is assigned whole or
            // locked (QuestFacts' lookups, ZoneStore, SPT's LazyLoad), so the one exposure is a
            // later mod ADDING to the quest or location tables while Build enumerates them: that
            // throws, GetPayloadJson catches it, the route answers no pins for 60 s, and the gate
            // rebuilds. Self-healing, and mods mutate the database at PostLoad or earlier, which
            // has finished by now. Do not move this back onto the boot path to "fix" that.
            var boot = clock.ElapsedMilliseconds;
            _ = Task.Run(() =>
            {
                var build = System.Diagnostics.Stopwatch.StartNew();
                GetPayloadJson();

                if (_cachedJson != null)
                    logger.Detail($"Quest Tracker: map markers built in the background in {build.ElapsedMilliseconds:N0} ms; the boot paid {boot:N0} ms for the objective sources.");
                else
                    logger.Warning($"Quest Tracker: the background map marker build did not produce markers after {build.ElapsedMilliseconds:N0} ms - see the error above; it retries in a minute.");
            });
        }

        private IReadOnlyList<TarkovDevClient.ObjectiveLocation> _objectiveLocations =
            new List<TarkovDevClient.ObjectiveLocation>();

        private IReadOnlyDictionary<string, ObjectiveGpsClient.ObjectivePlace> _objectivePlaces =
            new Dictionary<string, ObjectiveGpsClient.ObjectivePlace>();

        /// <summary>
        /// The one condition type that means "go and pick this up on this map".
        ///
        /// LeaveItemAtLocation and PlaceBeacon used to be here and were wrong. Their target is an
        /// item you BRING, not one you find: all 96 PlaceBeacon targets are the MS2000 Marker, which
        /// you buy from a trader and which a dozen quests share, so pinning where one happens to
        /// spawn as loot said nothing about any of them. Where those objectives actually happen is
        /// their zoneId, which no offline data resolves - that is what the tarkov.dev markers are
        /// for.
        /// </summary>
        private const string FindItemCondition = "FindItem";

        /// <summary>Most distinct places to show for any one item, after nearby spawns have been
        /// merged. A cap on top of the merge, for the rare item scattered across a whole map.</summary>
        private const int MaxSpawnsPerItem = 8;

        /// <summary>Spawns of the same item closer together than this are one place. The loot table
        /// lists every candidate position, including several within one room, and a room is one
        /// place to go and look.</summary>
        private const float MergeRadius = 25f;

        /// <summary>Marker kinds, so the view can tell "the thing you need spawns here" from "the
        /// objective happens here" - they warrant different weight on screen.</summary>
        public const string ItemKind = "item";
        public const string ObjectiveKind = "objective";

        /// <summary>The quests wanting one item on one map: names to show, ids so the client can
        /// look their live status up in its own graph.</summary>
        private sealed class WantedBy
        {
            public readonly List<string> Names = new();
            public readonly List<string> Ids = new();

            public void Add(string name, string id)
            {
                if (!Names.Contains(name)) Names.Add(name);
                if (!Ids.Contains(id)) Ids.Add(id);
            }
        }


        private readonly object _buildLock = new();
        private string? _cachedJson;

        /// <summary>The extent-coverage line last written, so a rebuild that changes nothing about it
        /// stays quiet. A harvest rebuilds every map's markers, and this line would otherwise repeat
        /// three times a raid; kept as the LINE rather than a flag so the boot line is written again
        /// when the count actually moves - which is the moment worth seeing, since it means a raid
        /// measured a map that had never been measured.</summary>
        private string? _extentLineLogged;

        /// <summary>Map plus its kept and dropped percentage-pin counts, already reported this boot. The
        /// kept count is an INPUT to the calibration work, not a warning about this build, so it is said
        /// once rather than on every rebuild - and keyed on both counts, because a later harvest covering
        /// a quest moves pins from kept to dropped and the new pair is the one to work from. A rebuild
        /// after a harvest therefore says the line again, which is the point. Both fields are only ever touched
        /// inside _buildLock, which both callers of Build already hold.</summary>
        private readonly HashSet<string> _calibrationReported = new(StringComparer.OrdinalIgnoreCase);

        private readonly RebuildGate _gate = new(60);

        /// <summary>Builds again now. Called by the zones route after a harvest lands, so the cost
        /// is paid on the client's fire-and-forget POST and never on a GET.
        ///
        /// Built into a local and SWAPPED, never nulled first. Nulling the cache and then building
        /// meant every GET arriving during those seconds blocked on _buildLock - on the client's
        /// main thread, behind its 15-second cap - while the loot tables were re-read. The cache is
        /// now never absent, only briefly stale, which is the right trade for data that changes
        /// when somebody finishes a raid.</summary>
        public void Rebuild()
        {
            lock (_buildLock)
            {
                // An explicit rebuild ignores the pause a failed build set: the harvest that asked
                // for it is new data, and answering "saved" while still serving empty would hide it.
                _gate.Clear();

                try
                {
                    var payload = Build();
                    _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
                }
                catch (Exception ex)
                {
                    // Keep serving the previous answer. A harvest is not a reason to lose the pins
                    // every other map already has.
                    logger.Warning(
                        $"Quest Tracker: the map marker rebuild after a harvest failed ({ex.Message}) - " +
                        "serving the previous markers.");
                }
            }
        }

        /// <summary>Cached like the quest list. The loot tables do not change while the server is
        /// up, and reading every map's forced spawns off disk is not work to repeat per request.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;

                var empty = new MapMarkerPayloadDto { Version = ModInfo.Version };
                if (_gate.Paused) return JsonSerializer.Serialize(empty, WireJson.Options);

                MapMarkerPayloadDto payload;

                try
                {
                    payload = Build();
                }
                catch (Exception ex)
                {
                    // Built during server startup (OnLoadAsync), where an exception would abort
                    // SPT's boot - one malformed modded quest is not worth the whole server. An
                    // empty set means "no pins", which the client already handles; it is answered,
                    // not cached, so the next request after the pause tries again.
                    logger.Error($"Quest Tracker: could not build map markers - maps will show no pins: {ex}");
                    _gate.PauseThenRewarm(() => GetPayloadJson(),
                        message => logger.Warning($"Quest Tracker: the map marker re-build did not run ({message})."));
                    return JsonSerializer.Serialize(empty, WireJson.Options);
                }

                _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);

                var items = payload.Maps.Sum(m => m.Markers.Count(x => x.Kind == ItemKind));
                var objectives = payload.Maps.Sum(m => m.Markers.Count(x => x.Kind == ObjectiveKind));

                logger.Info(
                    $"Quest Tracker: built {items} item-spawn and {objectives} objective markers " +
                    $"across {payload.Maps.Count} maps.");

                return _cachedJson;
            }
        }

        private MapMarkerPayloadDto Build()
        {
            var payload = new MapMarkerPayloadDto { Version = ModInfo.Version };
            var wantedByLocation = BuildWantedItems();

            // Per map, for the two boot lines at the end of this method: the percentage pins that
            // survived the harvest filter (the calibration work) and the ones it dropped.
            var percentagePins = new Dictionary<string, (int Kept, int Dropped)>(StringComparer.OrdinalIgnoreCase);

            // Grouped once here rather than scanned once per map below: with a few thousand modded
            // quests, thirteen maps each walking the whole table was the bulk of the build.
            var questsByLocation = QuestsByLocation();
            var zoneToMap = zoneStore.ZoneToMap();
            var objectivesByMap = _objectiveLocations.ToLookup(o => o.MapId, StringComparer.OrdinalIgnoreCase);
            var locale = localeService.GetLocaleDb();

            foreach (var location in locationTable.GetDictionary().Values)
            {
                var internalName = location?.Base?.Id;
                var locationId = location?.Base?.IdField.ToString();

                if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(locationId))
                    continue;

                // The whole map under one guard: the loot-table read had its own, but the
                // harvested, objective and GPS sources sat outside it, so one malformed quest
                // on one map escaped to the whole-build catch and blanked every map's pins.
                try
                {
                    var markers = new List<MapMarkerDto>();
                    wantedByLocation.TryGetValue(locationId, out var wanted);

                    // Harvested first: positions read from the loaded scene beat every other
                    // source, so a quest or item they cover is left out of the ones below.
                    var zones = zoneStore.TryGet(internalName!);
                    var harvested = HarvestedMarkersFor(
                        locationId!, zones, questsByLocation, wanted, locale, zoneToMap);
                    markers.AddRange(harvested.Markers);

                    // Item spawns need the map's loot table, so only maps with wanted items pay
                    // for reading one. The objective pins below do not, and must not be skipped
                    // with it: Factory and Labs have no find-item quests and used to lose every
                    // pin this way.
                    var remainingWanted = wanted?
                        .Where(kv => !harvested.CoveredTemplates.Contains(kv.Key))
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                    if (remainingWanted != null && remainingWanted.Count > 0)
                    {
                        try
                        {
                            markers.AddRange(CollectMarkers(internalName!, location!, remainingWanted));
                        }
                        catch (Exception ex)
                        {
                            // One unreadable loot table must not cost this map its other pins.
                            logger.Warning($"Quest Tracker: no item markers for '{internalName}': {ex.Message}");
                        }
                    }

                    markers.AddRange(ObjectiveMarkersFor(
                        objectivesByMap[locationId!].Where(o => !harvested.CoveredQuestIds.Contains(o.QuestId))));

                    // Held rather than added straight in, so the pins that carry PERCENTAGES instead
                    // of world coordinates can be filtered and counted: they are the only ones that
                    // cannot be drawn from an extent alone, because a percentage is measured against
                    // whichever map image its source used. Counted here and not by scanning markers
                    // for a non-zero LeftPercent - an objective pin legitimately sitting at 0,0 of its
                    // image would be missed, and a world-coordinate pin at x=0 would not.
                    //
                    // Every quest's percentage pins are built, the harvest-covered ones included, and
                    // the drop happens in the one place below. Skipping them earlier would leave the
                    // dropped count in the boot line reporting whatever this call declined to build
                    // rather than what the payload actually lost.
                    var gps = GpsMarkersFor(locationId!, questsByLocation, locale);
                    var keptGps = KeepUncoveredPercentagePins(harvested.Markers, gps, out var droppedGps);
                    markers.AddRange(keptGps);

                    // A map with nothing to pin and nothing to locate stays out - a harvest file
                    // alone (Ground Zero's other variant, aliased) is not a reason to list it.
                    if (markers.Count == 0 && harvested.ZonesWanted.Count == 0) continue;

                    payload.Maps.Add(new MapMarkerSetDto
                    {
                        LocationKey = internalName!,
                        Markers = markers,
                        ZonesWanted = harvested.ZonesWanted.Count,
                        ZonesKnown = harvested.ZonesKnown.Count,
                        HarvestedAt = zones?.HarvestedAt ?? "",

                        // Straight from the zone file, the same object rather than a copy: the
                        // rectangle a raid measured is the rectangle the client draws against, and a
                        // transform on the way out is a second source of truth for map geometry. Null
                        // on a map no v2 harvest has reached, which the client reads as "no picture
                        // can be placed here yet".
                        Extent = zones?.Extent
                    });

                    if (keptGps.Count > 0 || droppedGps > 0)
                        percentagePins[internalName!] = (keptGps.Count, droppedGps);
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: no markers for '{internalName}' - {ex.Message}");
                }
            }

            ReportExtents(payload, percentagePins);

            return payload;
        }

        /// <summary>The two lines that say what the maps have to work with, written once each per boot
        /// rather than per rebuild (see the fields they dedup through).
        ///
        /// DIAGNOSTIC lines (<see cref="QuestLog.Detail"/>): both are per-map statistics for whoever is
        /// running the capture campaign, not news a player can act on, so they print at Information only
        /// under QUESTTREE_DEBUG and at Debug otherwise. The dedup stays either way - a Debug line
        /// repeated on every rebuild is still a log line.
        ///
        /// The first answers the question the 1.19.0 capture campaign turns on: how many of the maps
        /// this install serves have been measured in a raid. Without it the only way to know would be
        /// to count files in zones\ and open each one.
        ///
        /// The second is the input to the pin calibration: a percentage pin is placed against whichever
        /// map image its source measured against, so every one of them has to be re-checked once a map
        /// carries an in-house picture instead. Only the pins that SURVIVED the harvest filter are
        /// counted as that work - a dropped pin never reaches a picture, so calibrating it would be
        /// calibrating something nobody sees. The dropped count is said alongside it so the two numbers
        /// can be read against the previous boot's single one.
        /// </summary>
        private void ReportExtents(
            MapMarkerPayloadDto payload, Dictionary<string, (int Kept, int Dropped)> percentagePins)
        {
            var withExtent = payload.Maps.Count(m => m.Extent != null);
            var line = $"Quest Tracker: {withExtent} of {payload.Maps.Count} maps carry a harvested extent.";

            if (_extentLineLogged != line)
            {
                _extentLineLogged = line;
                logger.Detail(line);
            }

            foreach (var entry in percentagePins.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!_calibrationReported.Add($"{entry.Key}|{entry.Value.Kept}|{entry.Value.Dropped}")) continue;

                logger.Detail(
                    $"Quest Tracker: {entry.Key} - {entry.Value.Kept} percentage pin(s) kept " +
                    $"(no harvested pin for their quest yet), {entry.Value.Dropped} dropped as " +
                    "covered by harvested pins.");
            }
        }

        /// <summary>What one map's harvest yielded, and what it therefore supersedes.</summary>
        private sealed class HarvestResult
        {
            public readonly List<MapMarkerDto> Markers = new();
            public readonly HashSet<string> CoveredQuestIds = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> CoveredTemplates = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ZonesWanted = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> ZonesKnown = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Whether a zone id is one this map should be counting as its own.
        ///
        /// True when nothing has harvested it yet - an unlocated zone is what the coverage line
        /// exists to report - and true when the harvest says it is here. False only when the harvest
        /// places it somewhere else entirely, which since 1.9.0 happens on any map that shares a
        /// quest with another.</summary>
        private static bool BelongsHere(
            string zoneId,
            string? canonical,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap)
        {
            if (string.IsNullOrWhiteSpace(zoneId)) return false;
            if (!zoneToMap.TryGetValue(zoneId, out var maps) || maps.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(canonical)) return true;

            foreach (var map in maps)
                if (string.Equals(map, canonical, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Markers from the zones a raid harvested: every zone-shaped objective of every quest on
        /// this map, at the position the scene gave it, plus the actual spots of the quest items
        /// the map's find-item quests want. World coordinates, which the client draws as they are.
        ///
        /// ZonesWanted is counted whether or not a harvest exists, so the client can say how much
        /// of the map is still unlocated.
        /// </summary>
        private HarvestResult HarvestedMarkersFor(
            string locationId, ZoneFile? zones, Dictionary<string, List<Quest>> questsByLocation,
            Dictionary<string, WantedBy>? wanted, Dictionary<string, string> locale,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap)
        {
            var result = new HarvestResult();

            if (!questsByLocation.TryGetValue(locationId, out var quests)) return result;

            // The canonical map this location folds onto, for the wanted-zone test below.
            var canonical = zones?.Map;

            var triggersById = zones?.Triggers
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .ToLookup(t => t.Id, StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var quest in quests)
            {
                // Per quest, as the quest list builder guards: one modded quest with a malformed
                // condition costs its own pins, not the map's.
                try
                {
                    var questId = quest.Id.ToString();
                    var questName = QuestPayloadBuilder.ResolveQuestName(quest, questId, locale);

                    foreach (var condition in quest.Conditions?.AvailableForFinish ?? [])
                    {
                        if (condition == null) continue;

                        foreach (var zoneId in QuestPayloadBuilder.ZoneIdsOf(condition))
                        {
                            // A zone this map's quests want, but only if it plausibly belongs here.
                            // A quest spanning two maps is now filed under both, so counting its
                            // other map's KNOWN zones as wanted-and-unlocated here would make the
                            // "50/50 zones located" line read 50/55 on both maps and never reach
                            // parity - which looks exactly like a broken harvester.
                            //
                            // An unharvested zone still counts, which is the whole point of the
                            // line: nothing knows where it is yet, so it is precisely what is still
                            // to be found. Only a zone known to live somewhere ELSE is excluded.
                            if (!BelongsHere(zoneId, canonical, zoneToMap)) continue;

                            result.ZonesWanted.Add(zoneId);

                            if (triggersById == null || !triggersById.Contains(zoneId)) continue;
                            result.ZonesKnown.Add(zoneId);

                            foreach (var trigger in triggersById[zoneId])
                            {
                                // A zone made of several volumes is one place per volume, but
                                // two volumes a metre apart are one pin.
                                if (!seen.Add($"{questId}|{Numbers.Grid(trigger.X)}|{Numbers.Grid(trigger.Z)}")) continue;

                                result.Markers.Add(new MapMarkerDto
                                {
                                    ItemName = DescribeObjective(condition, questName, locale),
                                    Quests = new List<string> { questName },
                                    QuestIds = new List<string> { questId },
                                    Kind = ObjectiveKind,
                                    X = trigger.X,
                                    Y = trigger.Y,
                                    Z = trigger.Z
                                });

                                result.CoveredQuestIds.Add(questId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: skipped a quest's zones on '{locationId}': {ex.Message}");
                }
            }

            // Quest items: the harvest holds where they actually were, which beats the loot
            // table's list of where they might be. Grouped by template the way CollectMarkers
            // groups them, so several quests wanting one item share its pins.
            if (zones != null && wanted != null && zones.QuestItems.Count > 0)
            {
                var itemsByTemplate = zones.QuestItems
                    .Where(i => !string.IsNullOrWhiteSpace(i.TemplateId))
                    .ToLookup(i => i.TemplateId, StringComparer.OrdinalIgnoreCase);

                foreach (var (template, wanting) in wanted)
                {
                    if (!itemsByTemplate.Contains(template)) continue;

                    var places = itemsByTemplate[template].ToList();
                    result.CoveredTemplates.Add(template);

                    foreach (var place in places)
                    {
                        result.Markers.Add(new MapMarkerDto
                        {
                            ItemName = QuestPayloadBuilder.ResolveItemName(template, locale),
                            Template = template,
                            Quests = wanting.Names,
                            QuestIds = wanting.Ids,
                            Kind = ItemKind,
                            Alternatives = places.Count,
                            X = place.X,
                            Y = place.Y,
                            Z = place.Z
                        });
                    }
                }
            }

            return result;
        }

        /// <summary>Location id -> the quests set there, for the per-map passes.
        ///
        /// A quest whose own Location field says nothing useful - blank, "any", or a string that is
        /// not a location id at all - is filed under each map its objective zones actually sit on.
        /// Without that, "any" is a bucket no real locationId ever matches, and a quest like
        /// "Sanitary Investigation - Part 5" gets no pins on the one map it belongs to.
        ///
        /// The derivation is facts.MapKeysOfQuest, the same call the quest payload makes. The two
        /// must agree about which map a quest is on, and two copies of that rule is how a quest ends
        /// up in a map's list with no pins, or the reverse.</summary>
        private Dictionary<string, List<Quest>> QuestsByLocation()
        {
            var byLocation = new Dictionary<string, List<Quest>>(StringComparer.OrdinalIgnoreCase);

            var quests = templateTable.Quests;
            if (quests == null) return byLocation;

            var zoneToMap = zoneStore.ZoneToMap();
            var keyToId = facts.LocationIdsByKey();

            void File(string locationId, Quest quest)
            {
                if (!byLocation.TryGetValue(locationId, out var list))
                    byLocation[locationId] = list = new List<Quest>();

                list.Add(quest);
            }

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                if (!facts.IsUselessLocation(quest.Location))
                {
                    File(quest.Location!, quest);
                    continue;
                }

                // Derived from harvested zones, so the pins land where the objectives actually are.
                foreach (var key in facts.MapKeysOfQuest(quest, zoneToMap))
                    if (keyToId.TryGetValue(key, out var locationId)) File(locationId, quest);
            }

            return byLocation;
        }

        /// <summary>
        /// Location id -> item template -> the quests that want it.
        ///
        /// Grouped by map because the same item is wanted by quests on several maps, and a marker
        /// only means anything on the map whose own loot table placed that item.
        /// </summary>
        private Dictionary<string, Dictionary<string, WantedBy>> BuildWantedItems()
        {
            var byLocation = new Dictionary<string, Dictionary<string, WantedBy>>(
                StringComparer.OrdinalIgnoreCase);

            var quests = templateTable.Quests;
            if (quests == null) return byLocation;

            var skipped = 0;

            var locale = localeService.GetLocaleDb();

            // Every RAIDABLE map, for the quests that do not name one. Through AllLocationKeys, which is
            // already the playable subset - LocationIdsByKey.Values is all nineteen entries including the
            // six nobody can raid, and fanning every unlocated quest's items onto the hideout and the
            // development scene allocates a WantedBy per quest per non-place for maps CollectMarkers can
            // never produce a pin on. (The filtered accessor arrived after this code did, in a later pass;
            // this is the one caller that was left reading the unfiltered list.)
            //
            // Materialised once: the lookups are memoised, but the join and Distinct would not be.
            var keyToId = facts.LocationIdsByKey();

            var everyLocationId = facts.AllLocationKeys()
                .Select(key => keyToId.TryGetValue(key, out var id) ? id : "")
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                var conditions = quest.Conditions?.AvailableForFinish;
                if (conditions == null) continue;

                // A quest that names its own map is filed there. One that does not - blank, "any", or a
                // string that is no location - is filed on EVERY map, and that is deliberate rather
                // than lazy.
                //
                // The obvious alternative, deriving the map from the quest's objective zones the way
                // QuestsByLocation does, was tried first and does not work here: MapKeysOfQuest reads
                // zone ids off the conditions, and a FindItem or HandoverItem condition carries none.
                // Measured on the shipped database, seven of the affected quests - Lend-Lease - Part 1
                // and Vitamins - Part 1 among them - have zero zone ids across every finish condition,
                // so they resolved to no map at all and nothing changed for them; ten more resolved to
                // a map that does not have the item. The two passes need different rules because they
                // answer different questions: where the quest is, versus where its items are.
                //
                // Filing broadly is safe because this dictionary does not decide anything. It is a
                // FILTER that the per-map passes intersect with map-local spawn data - CollectMarkers
                // walks that map's own SpawnpointsForced and emits nothing for a template that is not
                // in it, and HarvestedMarkersFor reads that map's own harvest. So a wrong map cannot
                // produce a pin; it can only fail to produce one. The cost is that every map now reads
                // its forced-spawn list rather than only maps with a named find-item quest, which is
                // once per payload build, and the payload is cached.
                var locationIds = facts.IsUselessLocation(quest.Location)
                    ? everyLocationId
                    : (IReadOnlyList<string>)new[] { quest.Location! };

                string id;
                string name;
                try
                {
                    id = quest.Id.ToString();
                    name = QuestPayloadBuilder.ResolveQuestName(quest, id, locale);
                }
                catch (Exception ex)
                {
                    // One quest, not the item list for every map.
                    logger.Warning($"Quest Tracker: skipped a quest's wanted items: {ex.Message}");
                    continue;
                }

                foreach (var condition in conditions)
                {
                    if (condition == null) continue;
                    if (!string.Equals(condition.ConditionType, FindItemCondition,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var target in QuestPayloadBuilder.TargetIds(condition.Target))
                    {
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        // Only true quest items. Over half of what FindItem names is ordinary loot -
                        // Hindsight 20/20 alone lists about sixty ammo packs, Gratitude lists
                        // sunglasses, The Survivalist Path lists bottles of water. Marking where
                        // ammo and water spawn is not a quest location, it is noise on top of one.
                        if (!IsQuestItem(target))
                        {
                            skipped++;
                            continue;
                        }

                        foreach (var locationId in locationIds)
                        {
                            if (!byLocation.TryGetValue(locationId, out var wanted))
                            {
                                wanted = new Dictionary<string, WantedBy>(StringComparer.OrdinalIgnoreCase);
                                byLocation[locationId] = wanted;
                            }

                            if (!wanted.TryGetValue(target, out var wanting))
                            {
                                wanting = new WantedBy();
                                wanted[target] = wanting;
                            }

                            wanting.Add(name, id);
                        }
                    }
                }
            }

            if (skipped > 0)
            {
                logger.Debug(
                    $"Quest Tracker: ignored {skipped} find-item targets that are ordinary loot " +
                    "rather than quest items.");
            }

            return byLocation;
        }

        /// <summary>Every template flagged as a quest item, built once. A set rather than a lookup
        /// per target: the item table has tens of thousands of entries and this is asked several
        /// hundred times.</summary>
        private HashSet<string>? _questItems;

        /// <summary>Whether a template is a real quest item - the flag the game itself uses for the
        /// documents, samples and packages that exist only to be fetched.</summary>
        private bool IsQuestItem(string template)
        {
            if (_questItems == null)
            {
                _questItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (id, item) in templateTable.Items ?? [])
                {
                    if (item?.Properties?.QuestItem == true) _questItems.Add(id.ToString());
                }

                logger.Debug($"Quest Tracker: {_questItems.Count} templates are flagged as quest items.");
            }

            return _questItems.Contains(template);
        }

        /// <summary>
        /// Objective markers from tarkovdata, joined on the objective id.
        ///
        /// SPT puts an id on every quest condition and this source is keyed by the same ids, so the
        /// join is exact rather than a name match: 218 of its 232 entries land on a condition in
        /// this install's quest table, covering 92 quests.
        ///
        /// Positions are passed through as percentages. The conversion needs the map image's
        /// rectangle and rotation, which live on the client with the rest of the map geometry, and
        /// duplicating that here is how a second source of truth gets born.
        ///
        /// Every quest's pins are built, the harvest-covered ones too. Which of them the payload keeps
        /// is <see cref="KeepUncoveredPercentagePins"/>'s decision alone, so there is one place that
        /// knows the rule and one count of what it dropped.
        /// </summary>
        private List<MapMarkerDto> GpsMarkersFor(
            string locationId, Dictionary<string, List<Quest>> questsByLocation, Dictionary<string, string> locale)
        {
            var markers = new List<MapMarkerDto>();

            if (_objectivePlaces.Count == 0) return markers;
            if (!questsByLocation.TryGetValue(locationId, out var quests)) return markers;

            foreach (var quest in quests)
            {
                // Per quest, like the harvested and wanted-item passes: one malformed modded quest
                // costs its own pins, not the map's.
                try
                {
                    var questId = quest.Id.ToString();
                    var questName = QuestPayloadBuilder.ResolveQuestName(quest, questId, locale);

                    foreach (var condition in quest.Conditions?.AvailableForFinish ?? [])
                    {
                        var conditionId = condition?.Id.ToString();

                        if (string.IsNullOrEmpty(conditionId)) continue;
                        if (!_objectivePlaces.TryGetValue(conditionId!, out var place)) continue;

                        // The source states the map too. Trusting the quest's own location over
                        // it would put a pin on the wrong map wherever the two disagree.
                        if (!string.Equals(place.Map, locationId, StringComparison.OrdinalIgnoreCase)) continue;

                        markers.Add(new MapMarkerDto
                        {
                            ItemName = DescribeObjective(condition!, questName, locale),
                            Quests = new List<string> { questName },
                            QuestIds = new List<string> { questId },
                            Kind = ObjectiveKind,
                            LeftPercent = place.LeftPercent,
                            TopPercent = place.TopPercent,
                            Floor = place.Floor ?? ""
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: skipped a quest's GPS pins on '{locationId}' ({quest?.Id}): {ex.Message}");
                }
            }

            return markers;
        }

        /// <summary>
        /// The percentage pins worth sending for one map: the ones whose quest the harvest has not
        /// already placed there.
        ///
        /// A percentage pin states a point as a fraction across and down the image its SOURCE measured
        /// against - tarkov.dev's map picture. Drawn against any other rectangle, including the one an
        /// in-raid capture measures, it lands somewhere else. Where a raid has already harvested a
        /// world-coordinate pin for the same quest on the same map, the percentage pin is therefore both
        /// redundant and wrong, and the harvested one is strictly better: it is a real position from the
        /// loaded scene, drawn through the map's own extent.
        ///
        /// Per QUEST, not per objective or per point: the harvested and percentage sources describe the
        /// same quest through different condition ids and different geometry, so there is no honest way
        /// to pair one pin with another. A quest the harvest reached on this map keeps the harvest's pins
        /// alone; a quest it has not reached keeps all of its percentage pins, because for that quest
        /// they are the only positions anything has.
        ///
        /// Both harvested kinds count as coverage. An item pin is where a raid actually found the thing
        /// the quest wants - a world position for that quest on this map, the same as a zone pin.
        ///
        /// Pure and static so it can be tested on hand-built lists: the rule is the whole feature, and
        /// the rest of this class needs a running SPT to reach.
        /// </summary>
        /// <param name="harvestedMarkers">This map's harvested markers, and only those. tarkov.dev's
        /// world-coordinate objective pins are NOT harvest coverage - they are the same kind of
        /// second-hand guess as the percentage pins, so they suppress nothing.</param>
        /// <param name="percentagePins">This map's percentage pins.</param>
        /// <param name="dropped">How many pins were dropped, for the boot line.</param>
        internal static List<MapMarkerDto> KeepUncoveredPercentagePins(
            IReadOnlyCollection<MapMarkerDto> harvestedMarkers,
            IReadOnlyCollection<MapMarkerDto> percentagePins,
            out int dropped)
        {
            dropped = 0;
            if (percentagePins.Count == 0) return new List<MapMarkerDto>();

            var harvestedQuestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var marker in harvestedMarkers)
            {
                foreach (var questId in marker?.QuestIds ?? [])
                    if (!string.IsNullOrWhiteSpace(questId)) harvestedQuestIds.Add(questId);
            }

            if (harvestedQuestIds.Count == 0) return percentagePins.ToList();

            var kept = new List<MapMarkerDto>(percentagePins.Count);

            foreach (var pin in percentagePins)
            {
                // A pin naming no quest cannot be covered by one, so it is kept: dropping it would
                // silently lose a position on the strength of missing data.
                if (pin != null && (pin.QuestIds?.Any(harvestedQuestIds.Contains) ?? false))
                {
                    dropped++;
                    continue;
                }

                if (pin != null) kept.Add(pin);
            }

            return kept;
        }

        /// <summary>What the pin says. The objective's own localized text where there is one, since
        /// it reads as an instruction rather than a label, falling back to the quest's name.</summary>
        private static string DescribeObjective(
            QuestCondition condition, string questName, Dictionary<string, string> locale)
        {
            var id = condition.Id.ToString();

            return locale.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : questName;
        }

        /// <summary>Each map's forced spawn points - position and the item templates placed there -
        /// read out of the loot table ONCE per process.
        ///
        /// SPT's LazyLoad does not cache: LooseLoot carries no [CacheLazyLoad], so every read of
        /// LooseLoot.Value deserialises the whole file again - 42 MB for Customs - and this builder
        /// reads every map's on every Build, which is every harvest rebuild as well as the boot.
        /// The forced spawns are the only part it wants, they are a few hundred entries, and the
        /// table does not change while the server runs, so they are kept. Written only under
        /// _buildLock, which every Build holds.</summary>
        private readonly Dictionary<string, List<(Vector3 Position, List<string> Items)>> _forcedSpawns =
            new(StringComparer.OrdinalIgnoreCase);

        private List<(Vector3 Position, List<string> Items)> ForcedSpawnsFor(string internalName, Location location)
        {
            if (_forcedSpawns.TryGetValue(internalName, out var known)) return known;

            var spawns = new List<(Vector3, List<string>)>();

            // This is where the loot table is actually read off disk. Only the forced spawns are
            // kept: a random-chance spawn point is not somewhere to send someone.
            var loot = location.LooseLoot?.Value;

            if (loot?.SpawnpointsForced != null)
            {
                foreach (var spawn in loot.SpawnpointsForced)
                {
                    var template = spawn?.Template;
                    var position = template?.Position;

                    if (template?.Items == null || position == null) continue;

                    var items = new List<string>();

                    foreach (var item in template.Items)
                    {
                        var tpl = item?.Template.ToString();
                        if (!string.IsNullOrWhiteSpace(tpl)) items.Add(tpl!);
                    }

                    if (items.Count > 0) spawns.Add((position.Value, items));
                }
            }

            _forcedSpawns[internalName] = spawns;
            return spawns;
        }

        private List<MapMarkerDto> CollectMarkers(
            string internalName, Location location, Dictionary<string, WantedBy> wanted)
        {
            var markers = new List<MapMarkerDto>();
            var spawnsByItem = new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase);
            var locale = localeService.GetLocaleDb();

            foreach (var (position, items) in ForcedSpawnsFor(internalName, location))
            {
                foreach (var tpl in items)
                {
                    if (!wanted.ContainsKey(tpl)) continue;

                    if (!spawnsByItem.TryGetValue(tpl, out var spots))
                    {
                        spots = new List<Vector3>();
                        spawnsByItem[tpl] = spots;
                    }

                    spots.Add(position);
                }
            }

            // Merged only now that every spawn is known, because merging needs to see them all.
            foreach (var (tpl, spots) in spawnsByItem)
            {
                if (!wanted.TryGetValue(tpl, out var wanting)) continue;

                var places = Merge(spots);

                foreach (var place in places.Take(MaxSpawnsPerItem))
                {
                    markers.Add(new MapMarkerDto
                    {
                        ItemName = QuestPayloadBuilder.ResolveItemName(tpl, locale),
                        Template = tpl!,
                        Quests = wanting.Names,
                        QuestIds = wanting.Ids,
                        Kind = ItemKind,
                        Alternatives = places.Count,
                        X = place.X,
                        Y = place.Y,
                        Z = place.Z
                    });
                }
            }

            return markers;
        }

        /// <summary>
        /// Collapses spawn points that sit on top of each other into one place each.
        ///
        /// This is what stops the map being painted rather than marked. The loot table lists every
        /// candidate position an item can take, and the item takes exactly ONE of them per raid -
        /// on Streets that meant ten pins for a single chemical container and ten more for one
        /// guitar pick, of which nine each were wrong in any given raid. Merging by proximity turns
        /// a scatter of positions inside one room into the one room you would go and search.
        /// </summary>
        private static List<Vector3> Merge(List<Vector3> spots)
        {
            var places = new List<Vector3>();

            foreach (var spot in spots)
            {
                var merged = false;

                foreach (var place in places)
                {
                    var dx = place.X - spot.X;
                    var dz = place.Z - spot.Z;

                    if (dx * dx + dz * dz > MergeRadius * MergeRadius) continue;

                    merged = true;
                    break;
                }

                if (!merged) places.Add(spot);
            }

            return places;
        }

        /// <summary>
        /// Objective locations for one map, from tarkov.dev.
        ///
        /// Matched on the map's own id, which tarkov.dev keys on the same BSG location id SPT does.
        /// Several objectives of one quest can share a spot and several zones can belong to one
        /// objective, so identical points are collapsed - otherwise a three-zone objective puts
        /// three pins on the same doorway.
        /// </summary>
        private static List<MapMarkerDto> ObjectiveMarkersFor(IEnumerable<TarkovDevClient.ObjectiveLocation> objectives)
        {
            var markers = new List<MapMarkerDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var objective in objectives)
            {
                // Rounded before de-duplicating: two zones a few centimetres apart are one pin as
                // far as anybody reading the map is concerned.
                var key = $"{objective.QuestId}|{Numbers.Grid(objective.X)}|{Numbers.Grid(objective.Z)}";
                if (!seen.Add(key)) continue;

                markers.Add(new MapMarkerDto
                {
                    ItemName = string.IsNullOrWhiteSpace(objective.Description)
                        ? objective.QuestName
                        : objective.Description,
                    Quests = new List<string> { objective.QuestName },
                    QuestIds = new List<string> { objective.QuestId },
                    Kind = ObjectiveKind,
                    X = objective.X,
                    Y = objective.Y,
                    Z = objective.Z
                });
            }

            return markers;
        }
    }
}
