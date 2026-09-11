using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils.Json;

namespace QuestTreeServer
{
    /// <summary>
    /// Builds the complete, unfiltered quest list the Quest Tracker client mod draws its tree from.
    ///
    /// This exists because the game's own quest list cannot answer the question. /client/quest/list
    /// is served by QuestHelper.GetClientQuests, which returns a quest only if it is already in the
    /// player's profile, or else passes every one of: side check, edition blacklist, edition
    /// whitelist, seasonal-event check, player-level check, trader-exists check, AND has all of its
    /// AvailableForStart prerequisite quests already satisfied in the profile. That is exactly the
    /// set of quests a progression tree needs to see PAST, so the client has never had the data.
    ///
    /// templateTable.Quests is the same source GetClientQuests filters down from, so this serves
    /// the real thing rather than a reconstruction - deliberately with no filtering whatsoever.
    /// Level-gated, other-faction, seasonal and edition-locked quests are all included and merely
    /// flagged, leaving the client to decide how to present them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class QuestPayloadBuilder(
        ISptLogger<QuestPayloadBuilder> logger,
        TemplateTable templateTable,
        LocaleService localeService,
        SeasonalEventService seasonalEventService,
        QuestConfig questConfig,
        QuestFacts facts,
        ZoneStore zoneStore) : IOnLoad
    {
        /// <summary>Built while the server starts, for the reason MapMarkerPayloadBuilder gives:
        /// the client's request handler is synchronous on Unity's main thread, so paying for the
        /// first build there froze the game on the first panel open.</summary>
        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            GetPayloadJson();
            return Task.CompletedTask;
        }

        /// <summary>Condition type that names another quest as a prerequisite.</summary>
        private const string QuestConditionType = "Quest";

        /// <summary>Condition type carrying the quest's player-level requirement.</summary>
        private const string LevelConditionType = "Level";

        /// <summary>Key into Quest.Rewards for the rewards paid on handing the quest in.</summary>
        private const string SuccessRewardKey = "Success";


        private readonly object _buildLock = new();
        private string? _cachedJson;

        private readonly RebuildGate _gate = new(60);

        /// <summary>Builds again now, after a harvest has taught the server where new zones are.
        ///
        /// Needed since 1.9.0 and its absence was a shipped-broken bug waiting to happen: derived
        /// locations come from the harvested zone index, so a raid that harvests a map would give a
        /// quest its PINS - the marker builder rebuilds - while its map on the client stayed empty
        /// until the next server restart. That is the "in the list with no pins, or the reverse"
        /// split the derivation exists to prevent, arriving by the back door.
        ///
        /// Built into a local and swapped, never nulled first: a GET arriving mid-rebuild would
        /// otherwise block on _buildLock, on the client's main thread, behind its 15-second cap. The
        /// cache is therefore never absent, only briefly stale.</summary>
        public void Rebuild()
        {
            lock (_buildLock)
            {
                _gate.Clear();

                try
                {
                    var payload = Build();
                    payload.ModVersion = ModInfo.Version;
                    _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
                }
                catch (Exception ex)
                {
                    // Keep serving the previous answer. A harvest is not a reason to lose the tree.
                    logger.Warning($"Quest Tracker: the quest list rebuild after a harvest failed ({ex.Message}) - serving the previous list.");
                }
            }
        }

        /// <summary>Serialized and cached. The quest database itself does not change while the
        /// server is running, but the derived locations on it do - see Rebuild.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;
                if (_gate.Paused) return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);

                QuestPayloadDto payload;

                try
                {
                    payload = Build();
                }
                catch (Exception ex)
                {
                    // Now built at startup, where a throw would abort SPT's boot. An empty list is
                    // a valid payload the client already degrades on - answered, not cached, so a
                    // fault at boot does not mean an empty tree until the server restarts.
                    logger.Error($"Quest Tracker: could not build the quest list - the tree will be empty: {ex}");
                    _gate.PauseThenRewarm(() => GetPayloadJson(),
                        message => logger.Warning($"Quest Tracker: the quest list re-build did not run ({message})."));
                    return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);
                }

                payload.ModVersion = ModInfo.Version;
                return _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
            }
        }

        private QuestPayloadDto Build()
        {
            var payload = new QuestPayloadDto();

            var quests = templateTable.Quests;
            if (quests == null)
            {
                logger.Error("Quest Tracker: the quest template table is empty - serving nothing.");
                return payload;
            }

            var locale = localeService.GetLocaleDb();

            // Hoisted: built once per payload rather than once per quest, like QuestsByLocation.
            var zoneToMap = zoneStore.ZoneToMap();

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                try
                {
                    payload.Quests.Add(MapQuest(quest, zoneToMap, locale));
                }
                catch (Exception ex)
                {
                    // One malformed quest - which in a modded install is entirely possible - must
                    // not cost the client every other quest in the game.
                    logger.Warning($"Quest Tracker: skipped quest '{quest.Id}': {ex.Message}");
                }
            }

            logger.Info($"Quest Tracker {ModInfo.Stamp}: serving {payload.Quests.Count} quests to the client mod.");
            return payload;
        }

        private QuestDto MapQuest(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            var id = quest.Id.ToString();

            return new QuestDto
            {
                Id = id,
                Name = ResolveQuestName(quest, id, locale),
                TraderId = quest.TraderId.ToString(),
                Side = ResolveSide(quest),
                Type = quest.Type.ToString(),
                Level = ResolveLevelRequirement(quest),
                LocationId = ResolveLocationName(quest, locale),
                LocationKey = ResolveLocationKey(quest),
                IsEvent = IsEventQuest(quest.Id),
                EditionRestricted = IsEditionRestricted(quest.Id),
                Prerequisites = MapPrerequisites(quest),
                DerivedLocations = DeriveLocations(quest, zoneToMap, locale),
                Objectives = MapObjectives(quest, locale),
                Rewards = MapRewards(quest, locale)
            };
        }

        /// <summary>Quest.Name is a locale key, not a display name - the real name lives under
        /// "&lt;questId&gt; name" (the same key QuestHelper.GetQuestNameFromLocale uses). Falls back
        /// to the internal QuestName rather than to a raw key, so the client never renders one.</summary>
        internal static string ResolveQuestName(Quest quest, string id, Dictionary<string, string> locale)
        {
            if (locale.TryGetValue($"{id} name", out var localized) && !string.IsNullOrWhiteSpace(localized))
                return localized;

            if (!string.IsNullOrWhiteSpace(quest.QuestName)) return quest.QuestName!;

            return string.IsNullOrWhiteSpace(quest.Name) ? id : quest.Name;
        }

        /// <summary>Most quests carry Side "Pmc" even when they are faction-locked; the actual
        /// restriction lives in the quest config's Bear/Usec-only sets, which is what
        /// QuestHelper.QuestIsForOtherSide checks.</summary>
        private string ResolveSide(Quest quest)
        {
            if (questConfig.BearOnlyQuests?.Contains(quest.Id) == true) return "Bear";
            if (questConfig.UsecOnlyQuests?.Contains(quest.Id) == true) return "Usec";
            return quest.Side ?? "";
        }

        private bool IsEventQuest(MongoId questId) =>
            seasonalEventService.IsQuestRelatedToEvent(questId, SeasonalEventType.Christmas) ||
            seasonalEventService.IsQuestRelatedToEvent(questId, SeasonalEventType.Halloween);

        /// <summary>True when the quest carries any game-edition restriction at all - an entry in
        /// the exclusive whitelist, or in any edition's inclusive blacklist.</summary>
        private bool IsEditionRestricted(MongoId questId)
        {
            if (questConfig.ProfileWhitelist?.ContainsKey(questId) == true) return true;

            return questConfig.ProfileBlacklist?.Values.Any(blacklisted => blacklisted.Contains(questId)) == true;
        }

        /// <summary>
        /// The map's internal name ("bigmap", "Labyrinth"), which is what map tools key on.
        ///
        /// Quest.Location is the location's MongoId, not its internal name, so the two never match
        /// without this. The locations table is the authority on the pairing - every location
        /// carries both forms, IdField being the MongoId a quest cites and Id the internal name -
        /// so it is read directly rather than inverting questConfig.LocationIdMap. That config was
        /// the obvious source and is the wrong one: it has no Labyrinth entry, so the Labyrinth
        /// quests resolved to a bare MongoId and matched nothing.
        ///
        /// The pairing itself lives on QuestFacts, which builds it once and hands the same table to
        /// the marker builder - the two must agree about which map a quest is on, and two copies of
        /// that is how they stop agreeing. Falls back to the raw id, which is at least stable, for a
        /// location that is not in the table at all.
        /// </summary>
        private string ResolveLocationKey(Quest quest)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase)) return location;

            // Blank rather than the raw id for a location that is not a location. Six vanilla quests
            // declare "marathon", which matches nothing in the table, and the raw-id fallback made
            // that into a map key: the locale answers "marathon Name" with "Transition", so
            // GroupByMap filed them under a phantom map of that name with no image, no floors and no
            // pins, because FindByLocationKey never matched. Blank is already skipped there.
            //
            // "any" is excepted deliberately - it is not a location id either, but it is a real
            // declaration the client names and tests for in three places.
            return facts.LocationIdToKey.TryGetValue(location, out var key) ? key : "";
        }

        /// <summary>The maps a quest is actually done on when it refuses to say.
        ///
        /// A quest whose Location is "any" can still be firmly placed: "Sanitary Investigation -
        /// Part 5" declares "any" and then names five Shoreline zones. Left alone, GroupByMap drops
        /// it and the marker builder never gives it pins, so the quest is missing from the one map
        /// it belongs to.
        ///
        /// A list, not a value: a quest can genuinely span maps - one plants at an aishi_shoreline
        /// zone and an aishi_woods zone - and collapsing that to a single map would be a different
        /// lie. Empty for hand-ins, skills and trader tasks, which have no zones and belong on no
        /// map.
        ///
        /// One line of derivation, shared with MapMarkerPayloadBuilder through QuestFacts. It is not
        /// enough for the two to agree today: the invariant is that a quest never appears in a map's
        /// list without pins, or the reverse, and that only holds if both read the same function.</summary>
        private List<DerivedLocationDto> DeriveLocations(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            // Only when the declaration is useless. A quest that names a REAL map is trusted, even
            // if its zones say otherwise - overriding the author would be this mod deciding it knows
            // better.
            if (!facts.IsUselessLocation(quest.Location)) return new List<DerivedLocationDto>();

            var maps = new List<DerivedLocationDto>();

            foreach (var key in facts.MapKeysOfQuest(quest, zoneToMap))
                maps.Add(new DerivedLocationDto { Key = key, Name = facts.LocationNameOf(key, locale) });

            return maps;
        }

        /// <summary>Quest.Location is a raw map id (e.g. 5704e3c2d2720bac5b8b4567), which is no use
        /// on a node subtitle. The map's display name lives in the locale table under
        /// "&lt;locationId&gt; Name". "any" is passed through untouched - the client treats it as
        /// "no specific map" and hides it.</summary>
        private string ResolveLocationName(Quest quest, Dictionary<string, string> locale)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase))
                return QuestFacts.AnyLocation;

            // Blank for a non-location, matching ResolveLocationKey: the locale WILL answer
            // "marathon Name" with "Transition", which is precisely how a phantom map got a display
            // name convincing enough to sit in the map dropdown.
            if (!facts.LocationIdToKey.ContainsKey(location)) return "";

            return locale.TryGetValue($"{location} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : location;
        }

        /// <summary>The player level gate, which lives as a "Level" condition alongside the quest
        /// prerequisites in AvailableForStart. Returns 0 when the quest has no level requirement.
        /// Takes the highest if a quest somehow declares more than one.</summary>
        private static int ResolveLevelRequirement(Quest quest)
        {
            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return 0;

            var level = 0;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, LevelConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Numbers.ToCount(condition.Value);
                if (value > level) level = value;
            }

            return level;
        }

        private static List<PrerequisiteDto> MapPrerequisites(Quest quest)
        {
            var prerequisites = new List<PrerequisiteDto>();

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return prerequisites;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, QuestConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var target in TargetIds(condition.Target))
                {
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    prerequisites.Add(new PrerequisiteDto
                    {
                        Target = target,
                        Status = condition.Status?.Select(status => status.ToString()).ToList() ?? new List<string>(),
                        AvailableAfter = condition.AvailableAfter ?? 0
                    });
                }
            }

            return prerequisites;
        }

        private static List<ObjectiveDto> MapObjectives(Quest quest, Dictionary<string, string> locale)
        {
            var objectives = new List<ObjectiveDto>();

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return objectives;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;

                var conditionId = condition.Id.ToString();

                var targetItems = IsAnyItemCondition(condition.ConditionType)
                    ? TargetIds(condition.Target).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                    : new List<string>();

                objectives.Add(new ObjectiveDto
                {
                    Id = conditionId,
                    // Objective descriptions are keyed in the locale table by the condition's own
                    // id. A modded quest may not have one, hence the condition type as a fallback.
                    Text = locale.TryGetValue(conditionId, out var text) && !string.IsNullOrWhiteSpace(text)
                        ? text
                        : condition.ConditionType ?? "",
                    IsNecessary = condition.IsNecessary ?? true,
                    ConditionType = condition.ConditionType ?? "",
                    TargetItems = targetItems,
                    TargetItemNames = targetItems.Select(template => ResolveItemName(template, locale)).ToList(),
                    Count = Numbers.ToCount(condition.Value),
                    FoundInRaid = condition.OnlyFoundInRaid ?? false,
                    ZoneIds = ZoneIdsOf(condition).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

            return objectives;
        }

        private static List<RewardDto> MapRewards(Quest quest, Dictionary<string, string> locale)
        {
            var rewards = new List<RewardDto>();

            if (quest.Rewards == null) return rewards;
            if (!quest.Rewards.TryGetValue(SuccessRewardKey, out var successRewards) || successRewards == null)
                return rewards;

            foreach (var reward in successRewards)
            {
                if (reward == null) continue;
                if (reward.IsHidden == true) continue;

                rewards.Add(new RewardDto
                {
                    Type = reward.Type?.ToString() ?? "",
                    Value = reward.Value ?? 0d,
                    Name = ResolveRewardName(reward, locale),
                    // Trader names are deliberately left to the client, which resolves them from
                    // the live session and so gets modded traders right for free.
                    TraderId = reward.TraderId?.ToString() ?? ""
                });
            }

            return rewards;
        }

        /// <summary>Item rewards are the only ones whose display name the client cannot work out for
        /// itself, so the item name is resolved here. Everything else is a number, a trader the
        /// client already knows, or carries its own target id.</summary>
        private static string ResolveRewardName(Reward reward, Dictionary<string, string> locale)
        {
            var template = reward.Items?.FirstOrDefault()?.Template.ToString();

            if (!string.IsNullOrWhiteSpace(template) &&
                locale.TryGetValue($"{template} Name", out var itemName) &&
                !string.IsNullOrWhiteSpace(itemName))
            {
                return itemName;
            }

            return reward.Target ?? "";
        }

        /// <summary>Condition types that TAKE an item from you - handed to a trader, or found in
        /// raid and turned in. These and only these are what the Collector hand-in checklist is
        /// built from, so the set must stay narrow: an item you plant and leave behind is not an
        /// item Collector will accept.</summary>
        internal static bool IsItemCondition(string? conditionType) =>
            string.Equals(conditionType, "HandoverItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "FindItem", StringComparison.OrdinalIgnoreCase);

        /// <summary>Condition types that have you CARRY an item into a raid and leave it somewhere -
        /// a marker on a trading post, a beacon in a warehouse. The template id is in the same
        /// Target field, but the item never reaches a trader, which is why these are kept apart
        /// from IsItemCondition above.</summary>
        internal static bool IsCarriedItemCondition(string? conditionType) =>
            string.Equals(conditionType, "LeaveItemAtLocation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "PlaceBeacon", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every condition whose Target is item template ids rather than quest ids or zone
        /// names. This is the set an objective's TargetItems is filled from, and the set the client
        /// asks "what do I need to bring" of - which is a wider question than "what does a trader
        /// want", and getting it wrong is what left markers and beacons off that list.</summary>
        internal static bool IsAnyItemCondition(string? conditionType) =>
            IsItemCondition(conditionType) || IsCarriedItemCondition(conditionType);

        /// <summary>An item's display name from the locale table, falling back to the template id.
        /// Was a private copy in three builders.</summary>
        internal static string ResolveItemName(string template, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{template} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : template;

        internal static IEnumerable<string> TargetIds(ListOrT<string>? target)
        {
            if (target == null) yield break;

            if (target.IsList)
            {
                foreach (var value in target.List ?? Enumerable.Empty<string>())
                    yield return value;

                yield break;
            }

            if (target.Item != null) yield return target.Item;
        }

        /// <summary>
        /// Every zone id a condition can point at. The shape follows DynamicMaps' own condition
        /// walk (QuestUtils.GetPositionsForCondition), which is the one known to line up with what
        /// the scene contains: the condition's own zoneId (LeaveItemAtLocation, PlaceBeacon), and
        /// inside a CounterCreator each sub-condition's VisitPlace target or zoneIds list (InZone,
        /// and the zoned Kills/Shots/LaunchFlare counters).
        /// </summary>
        internal static IEnumerable<string> ZoneIdsOf(QuestCondition condition)
        {
            if (!string.IsNullOrWhiteSpace(condition.ZoneId)) yield return condition.ZoneId!;

            var counters = condition.Counter?.Conditions;
            if (counters == null) yield break;

            foreach (var sub in counters)
            {
                if (sub == null) continue;

                if (string.Equals(sub.ConditionType, "VisitPlace", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var id in TargetIds(sub.Target))
                        if (!string.IsNullOrWhiteSpace(id)) yield return id;
                }

                // The JSON field is "zoneIds"; SPT's model names the property Zones.
                if (sub.Zones == null) continue;

                foreach (var id in sub.Zones)
                    if (!string.IsNullOrWhiteSpace(id)) yield return id;
            }
        }
    }
}
