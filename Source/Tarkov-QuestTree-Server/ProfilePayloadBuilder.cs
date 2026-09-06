using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// Everything the client needs to reason about THIS player rather than the quest database:
    /// level, faction, trader loyalty and standing, live per-objective counters, and - the useful
    /// part - why each locked quest is locked.
    ///
    /// The lock rules are not re-implemented here. SPT already decides who may see what in
    /// QuestHelper.GetClientQuests, and every check it uses is public, so this calls the same
    /// methods and reports the reason instead of dropping the quest. Re-deriving those rules would
    /// mean silently disagreeing with the game the first time SPT changed one.
    ///
    /// Rebuilt per request like the Kappa payload: level, loyalty and counters all move as you play.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ProfilePayloadBuilder(
        ISptLogger<ProfilePayloadBuilder> logger,
        TemplateTable templateTable,
        ProfileHelper profileHelper,
        QuestHelper questHelper)
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public string GetPayloadJson(MongoId sessionId) =>
            JsonSerializer.Serialize(Build(sessionId), SerializerOptions);

        private ProfilePayloadDto Build(MongoId sessionId)
        {
            var payload = new ProfilePayloadDto();

            var profile = TryGetProfile(sessionId);
            if (profile == null)
            {
                // No profile means an out-of-game request. The shape is still valid, just empty -
                // the client shows what it can rather than failing the whole panel.
                logger.Warning("Quest Tracker: no profile for this session - profile payload will be empty.");
                return payload;
            }

            payload.HasProfile = true;
            payload.Level = profile.Info?.Level ?? 0;
            payload.Side = profile.Info?.Side ?? "";
            payload.GameVersion = profile.Info?.GameVersion ?? "";

            if (profile.TradersInfo != null)
            {
                foreach (var (traderId, info) in profile.TradersInfo)
                {
                    if (info == null) continue;

                    payload.Traders.Add(new TraderStateDto
                    {
                        Id = traderId.ToString(),
                        LoyaltyLevel = info.LoyaltyLevel ?? 0,
                        Standing = info.Standing ?? 0d,
                        Unlocked = info.Unlocked ?? false
                    });
                }
            }

            // Keyed by condition id, which is what the objectives in the quest payload carry - so
            // the client can pair "eliminate 15 Scavs" with "you have 7".
            if (profile.TaskConditionCounters != null)
            {
                foreach (var (conditionId, counter) in profile.TaskConditionCounters)
                {
                    if (counter == null) continue;
                    payload.ConditionProgress[conditionId.ToString()] = counter.Value ?? 0d;
                }
            }

            BuildLockReasons(profile, payload);

            logger.Info(
                $"Quest Tracker: profile payload - level {payload.Level}, {payload.Traders.Count} traders, " +
                $"{payload.ConditionProgress.Count} counters, {payload.LockReasons.Count} locked quests explained.");

            return payload;
        }

        /// <summary>
        /// Works out, for every quest the player has not started, which gate is stopping them.
        ///
        /// Checked in the order the player would act on them: things that can never change for this
        /// character first (faction, edition, event), then the ones they can move (level, loyalty,
        /// standing), then prerequisites. Only the FIRST blocker is reported - a list of five
        /// reasons is not more useful than the one thing to go and do.
        /// </summary>
        private void BuildLockReasons(PmcData profile, ProfilePayloadDto payload)
        {
            var quests = templateTable.Quests;
            if (quests == null) return;

            var started = profile.Quests?.Select(q => q.QId).ToHashSet() ?? new HashSet<MongoId>();

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;
                if (started.Contains(quest.Id)) continue; // already in the profile; not locked

                try
                {
                    var reason = ResolveLockReason(quest, profile);
                    if (reason != null) payload.LockReasons[quest.Id.ToString()] = reason;
                }
                catch (Exception ex)
                {
                    // A malformed or modded quest must not cost every other quest its explanation.
                    logger.Warning($"Quest Tracker: could not evaluate lock reason for '{quest.Id}': {ex.Message}");
                }
            }
        }

        /// <summary>Null when nothing is blocking the quest.</summary>
        private LockReasonDto? ResolveLockReason(Quest quest, PmcData profile)
        {
            if (questHelper.QuestIsForOtherSide(profile.Info?.Side, quest.Id))
                return new LockReasonDto { Kind = "OtherFaction", Detail = "For the other faction" };

            var gameVersion = profile.Info?.GameVersion;

            if (questHelper.QuestIsProfileBlacklisted(gameVersion, quest.Id) ||
                !questHelper.QuestIsProfileWhitelisted(gameVersion, quest.Id))
            {
                return new LockReasonDto { Kind = "Edition", Detail = "Not available in your game edition" };
            }

            if (!questHelper.ShowEventQuestToPlayer(quest.Id))
                return new LockReasonDto { Kind = "Event", Detail = "Seasonal event quest, not currently active" };

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return null;

            var playerLevel = profile.Info?.Level ?? 0;

            foreach (var condition in conditions.GetLevelConditions())
            {
                if (questHelper.DoesPlayerLevelFulfilCondition(playerLevel, condition)) continue;

                var required = (int)(condition.Value ?? 0d);
                return new LockReasonDto
                {
                    Kind = "Level",
                    Detail = $"Requires level {required}",
                    RequiredValue = required,
                    CurrentValue = playerLevel
                };
            }

            foreach (var condition in conditions.GetLoyaltyConditions())
            {
                if (questHelper.TraderLoyaltyLevelRequirementCheck(condition, profile)) continue;

                return new LockReasonDto
                {
                    Kind = "Loyalty",
                    Detail = $"Requires loyalty level {(int)(condition.Value ?? 0d)}",
                    RequiredValue = (int)(condition.Value ?? 0d),
                    TraderId = ResolveConditionTrader(condition)
                };
            }

            foreach (var condition in conditions.GetStandingConditions())
            {
                if (questHelper.TraderStandingRequirementCheck(condition, profile)) continue;

                return new LockReasonDto
                {
                    Kind = "Standing",
                    Detail = "Requires higher trader standing",
                    TraderId = ResolveConditionTrader(condition)
                };
            }

            // Prerequisites last: they are the common case, and the client can already name the
            // quest from its own graph, so this only has to say that one is outstanding.
            var outstanding = conditions.GetQuestConditions()
                .SelectMany(c => QuestPayloadBuilder.TargetIds(c.Target))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Where(id => !IsQuestSatisfied(profile, id))
                .ToList();

            if (outstanding.Count > 0)
            {
                return new LockReasonDto
                {
                    Kind = "Prerequisite",
                    Detail = outstanding.Count == 1
                        ? "Requires an earlier quest"
                        : $"Requires {outstanding.Count} earlier quests",
                    BlockingQuestIds = outstanding
                };
            }

            return null;
        }

        /// <summary>A prerequisite counts as met once the profile holds it as successful. Deliberately
        /// simpler than GetClientQuests' full status-set check: the client draws the real edge from
        /// the quest data, and this only needs to know whether it is still outstanding.</summary>
        private static bool IsQuestSatisfied(PmcData profile, string questId)
        {
            var status = profile.Quests?.FirstOrDefault(q => q.QId == questId);
            return status != null && status.Status == SPTarkov.Server.Core.Models.Enums.QuestStatusEnum.Success;
        }

        private static string ResolveConditionTrader(QuestCondition condition) =>
            condition.Target != null && !condition.Target.IsList
                ? condition.Target.Item ?? ""
                : condition.Target?.List?.FirstOrDefault() ?? "";

        /// <summary>GetPmcProfile throws rather than returning null on an empty session id, which is
        /// what an out-of-game request carries. Same guard as KappaPayloadBuilder.</summary>
        private PmcData? TryGetProfile(MongoId sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId.ToString())) return null;
                return profileHelper.GetPmcProfile(sessionId);
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: no profile for this session ({ex.Message}).");
                return null;
            }
        }
    }
}
