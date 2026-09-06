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
        LocaleService localeService)
    {
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

                logger.Info(
                    $"Quest Tracker: built {payload.Maps.Sum(m => m.Markers.Count)} quest-item markers " +
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
        private Dictionary<string, Dictionary<string, List<string>>> BuildWantedItems()
        {
            var byLocation = new Dictionary<string, Dictionary<string, List<string>>>(
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

                var name = QuestPayloadBuilder.ResolveQuestName(quest, quest.Id.ToString(), locale);

                foreach (var condition in conditions)
                {
                    if (condition == null) continue;
                    if (!FindConditions.Contains(condition.ConditionType ?? "")) continue;

                    foreach (var target in QuestPayloadBuilder.TargetIds(condition.Target))
                    {
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        if (!byLocation.TryGetValue(location!, out var wanted))
                        {
                            wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                            byLocation[location!] = wanted;
                        }

                        if (!wanted.TryGetValue(target, out var wanting))
                        {
                            wanting = new List<string>();
                            wanted[target] = wanting;
                        }

                        if (!wanting.Contains(name)) wanting.Add(name);
                    }
                }
            }

            return byLocation;
        }

        private List<MapMarkerDto> CollectMarkers(
            Location location, Dictionary<string, List<string>> wanted)
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
                        Quests = wanting,
                        X = position.Value.X,
                        Z = position.Value.Z
                    });
                }
            }

            return markers;
        }

        private static string ResolveItemName(string template, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{template} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : template;
    }
}
