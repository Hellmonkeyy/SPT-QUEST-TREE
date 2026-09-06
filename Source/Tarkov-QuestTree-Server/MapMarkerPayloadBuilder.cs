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

            var skipped = 0;

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

        private List<MapMarkerDto> CollectMarkers(
            Location location, Dictionary<string, WantedBy> wanted)
        {
            var markers = new List<MapMarkerDto>();
            var spawnsByItem = new Dictionary<string, List<Vector3>>(StringComparer.OrdinalIgnoreCase);
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

                    if (!spawnsByItem.TryGetValue(tpl!, out var spots))
                    {
                        spots = new List<Vector3>();
                        spawnsByItem[tpl!] = spots;
                    }

                    spots.Add(position.Value);
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
                        ItemName = ResolveItemName(tpl, locale),
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
