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
        LocationTable locationTable) : IOnLoad
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
        private Dictionary<string, string>? _locationIdToKey;

        /// <summary>When a failed build may be tried again - see MapMarkerPayloadBuilder for why
        /// a failure is answered but not cached.</summary>
        private DateTime _retryAfter = DateTime.MinValue;
        private const int RetrySeconds = 60;

        /// <summary>Tries the build again in the background once the pause is over, so a GET is
        /// not the one to pay for a full walk of the quest table - see MapMarkerPayloadBuilder.</summary>
        private void RewarmLater()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(RetrySeconds + 1));
                try
                {
                    GetPayloadJson();
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: the quest list re-build did not run ({ex.Message}).");
                }
            });
        }

        /// <summary>Serialized once and cached: the quest database does not change while the server
        /// is running, and this payload covers every quest in the game.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;
                if (DateTime.UtcNow < _retryAfter) return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);

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
                    _retryAfter = DateTime.UtcNow.AddSeconds(RetrySeconds);
                    RewarmLater();
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

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                try
                {
                    payload.Quests.Add(MapQuest(quest, locale));
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

        private QuestDto MapQuest(Quest quest, Dictionary<string, string> locale)
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
        /// Built once and cached. Falls back to the raw id, which is at least stable, for a
        /// location that is not in the table at all.
        /// </summary>
        private string ResolveLocationKey(Quest quest)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";

            _locationIdToKey ??= BuildLocationKeyLookup();

            return _locationIdToKey.TryGetValue(location, out var key) ? key : location;
        }

        private Dictionary<string, string> BuildLocationKeyLookup()
        {
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var location in locationTable.GetDictionary().Values)
            {
                var internalName = location?.Base?.Id;
                var locationId = location?.Base?.IdField.ToString();

                if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(locationId))
                    continue;

                lookup[locationId] = internalName;
            }

            return lookup;
        }

        /// <summary>Quest.Location is a raw map id (e.g. 5704e3c2d2720bac5b8b4567), which is no use
        /// on a node subtitle. The map's display name lives in the locale table under
        /// "&lt;locationId&gt; Name". "any" is passed through untouched - the client treats it as
        /// "no specific map" and hides it.</summary>
        private static string ResolveLocationName(Quest quest, Dictionary<string, string> locale)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any";

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

                var value = (int)(condition.Value ?? 0d);
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
                    TargetItems = IsItemCondition(condition.ConditionType)
                        ? TargetIds(condition.Target).ToList()
                        : new List<string>(),
                    Count = (int)(condition.Value ?? 0d),
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

        /// <summary>Condition types whose Target is a list of item template ids rather than quest
        /// ids, zone names or anything else. Only these get TargetItems populated - notably this is
        /// what the Collector hand-in checklist is built from.</summary>
        internal static bool IsItemCondition(string? conditionType) =>
            string.Equals(conditionType, "HandoverItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "FindItem", StringComparison.OrdinalIgnoreCase);

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
