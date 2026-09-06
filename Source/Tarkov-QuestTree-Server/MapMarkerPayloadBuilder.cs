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
        TarkovDevClient tarkovDev) : IOnLoad
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
            // Awaited here so the network call happens during startup, where nothing is waiting on
            // it, rather than inside the synchronous request handler that serves the client.
            _objectiveLocations = await tarkovDev.GetLocationsAsync(cancellationToken);

            GetPayloadJson();
        }

        private IReadOnlyList<TarkovDevClient.ObjectiveLocation> _objectiveLocations =
            new List<TarkovDevClient.ObjectiveLocation>();

        /// <summary>Objective types that mean "go and pick this up". A hand-in condition also names
        /// target items, but where you find those is not this map, and pinning them would be a
        /// confident lie.</summary>
        private static readonly HashSet<string> FindConditions = new(StringComparer.OrdinalIgnoreCase)
        {
            "FindItem",
            "LeaveItemAtLocation",
            "PlaceBeacon"
        };

        /// <summary>Most markers for any one item. A few templates have dozens of forced spawns,
        /// which would paint the map rather than mark it.</summary>
        private const int MaxSpawnsPerItem = 12;

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

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private readonly object _buildLock = new();
        private string? _cachedJson;

        /// <summary>Cached like the quest list. The loot tables do not change while the server is
        /// up, and reading every map's forced spawns off disk is not work to repeat per request.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;

                var payload = Build();
                _cachedJson = JsonSerializer.Serialize(payload, SerializerOptions);

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

            foreach (var location in locationTable.GetDictionary().Values)
            {
                var internalName = location?.Base?.Id;
                var locationId = location?.Base?.IdField.ToString();

                if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(locationId))
                    continue;

                if (!wantedByLocation.TryGetValue(locationId, out var wanted) || wanted.Count == 0)
                    continue;

                List<MapMarkerDto> markers;

                try
                {
                    markers = CollectMarkers(location!, wanted);
                }
                catch (Exception ex)
                {
                    // One unreadable loot table must not cost every other map its markers.
                    logger.Warning($"Quest Tracker: no markers for '{internalName}': {ex.Message}");
                    continue;
                }

                markers.AddRange(ObjectiveMarkersFor(locationId!));

                if (markers.Count == 0) continue;

                payload.Maps.Add(new MapMarkerSetDto
                {
                    LocationKey = internalName!,
                    Markers = markers
                });
            }

            return payload;
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

            var locale = localeService.GetLocaleDb();

            foreach (var quest in quests.Values)
            {
                var location = quest?.Location;
                if (quest == null || string.IsNullOrWhiteSpace(location)) continue;

                var conditions = quest.Conditions?.AvailableForFinish;
                if (conditions == null) continue;

                var id = quest.Id.ToString();
                var name = QuestPayloadBuilder.ResolveQuestName(quest, id, locale);

                foreach (var condition in conditions)
                {
                    if (condition == null) continue;
                    if (!FindConditions.Contains(condition.ConditionType ?? "")) continue;

                    foreach (var target in QuestPayloadBuilder.TargetIds(condition.Target))
                    {
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        if (!byLocation.TryGetValue(location!, out var wanted))
                        {
                            wanted = new Dictionary<string, WantedBy>(StringComparer.OrdinalIgnoreCase);
                            byLocation[location!] = wanted;
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

            return byLocation;
        }

        private List<MapMarkerDto> CollectMarkers(
            Location location, Dictionary<string, WantedBy> wanted)
        {
            var markers = new List<MapMarkerDto>();
            var perItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var locale = localeService.GetLocaleDb();

            // LazyLoad, so this is where the map's loot table is actually read off disk. Only the
            // forced spawns are walked: the full spawn list is 42MB on Customs alone, and a
            // random-chance spawn point is not somewhere to send someone.
            var loot = location.LooseLoot?.Value;
            if (loot?.SpawnpointsForced == null) return markers;

            foreach (var spawn in loot.SpawnpointsForced)
            {
                var template = spawn?.Template;
                var position = template?.Position;

                if (template?.Items == null || position == null) continue;

                foreach (var item in template.Items)
                {
                    var tpl = item?.Template.ToString();

                    if (string.IsNullOrWhiteSpace(tpl)) continue;
                    if (!wanted.TryGetValue(tpl!, out var wanting)) continue;

                    perItem.TryGetValue(tpl!, out var used);
                    if (used >= MaxSpawnsPerItem) continue;
                    perItem[tpl!] = used + 1;

                    markers.Add(new MapMarkerDto
                    {
                        ItemName = ResolveItemName(tpl!, locale),
                        Quests = wanting.Names,
                        QuestIds = wanting.Ids,
                        Kind = ItemKind,
                        X = position.Value.X,
                        Y = position.Value.Y,
                        Z = position.Value.Z
                    });
                }
            }

            return markers;
        }

        /// <summary>
        /// Objective locations for one map, from tarkov.dev.
        ///
        /// Matched on the map's own id, which tarkov.dev keys on the same BSG location id SPT does.
        /// Several objectives of one quest can share a spot and several zones can belong to one
        /// objective, so identical points are collapsed - otherwise a three-zone objective puts
        /// three pins on the same doorway.
        /// </summary>
        private List<MapMarkerDto> ObjectiveMarkersFor(string locationId)
        {
            var markers = new List<MapMarkerDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var objective in _objectiveLocations)
            {
                if (!string.Equals(objective.MapId, locationId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Rounded before de-duplicating: two zones a few centimetres apart are one pin as
                // far as anybody reading the map is concerned.
                var key = $"{objective.QuestId}|{objective.X:F0}|{objective.Z:F0}";
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

        private static string ResolveItemName(string template, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{template} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : template;
    }
}
