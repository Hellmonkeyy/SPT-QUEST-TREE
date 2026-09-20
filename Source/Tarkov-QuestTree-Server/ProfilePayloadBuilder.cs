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
        QuestFacts facts)
    {

        public string GetPayloadJson(MongoId sessionId) =>
            JsonSerializer.Serialize(Build(sessionId), WireJson.Options);

        /// <summary>Whether the timing line has been written this process. Any SLOW request goes to the
        /// log at Info every time; the first request goes there as a QuestLog.Detail line, so it is seen
        /// while working on the mod and is quiet on a player's console; the rest stay at Debug.</summary>
        private bool _timed;

        /// <summary>A per-profile route slower than this is logged at Info every time. The client
        /// waits on these on its main thread, and 200 ms is where a wait starts to be felt.</summary>
        public const int SlowRequestMs = 200;

        private ProfilePayloadDto Build(MongoId sessionId)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
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

            // Each entry under its own guard, like the lock reasons below: one odd trader or
            // counter record in a profile used to cost the whole payload - every level, loyalty
            // and progress number on the client - rather than itself.
            if (profile.TradersInfo != null)
            {
                foreach (var (traderId, info) in profile.TradersInfo)
                {
                    if (info == null) continue;

                    try
                    {
                        payload.Traders.Add(new TraderStateDto
                        {
                            Id = traderId.ToString(),
                            LoyaltyLevel = info.LoyaltyLevel ?? 0,
                            Standing = info.Standing ?? 0d,
                            Unlocked = info.Unlocked ?? false
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Quest Tracker: skipped a trader record in the profile: {ex.Message}");
                    }
                }
            }

            // Keyed by condition id, which is what the objectives in the quest payload carry - so
            // the client can pair "eliminate 15 Scavs" with "you have 7".
            if (profile.TaskConditionCounters != null)
            {
                foreach (var (conditionId, counter) in profile.TaskConditionCounters)
                {
                    if (counter == null) continue;

                    try
                    {
                        payload.ConditionProgress[conditionId.ToString()] = counter.Value ?? 0d;
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Quest Tracker: skipped a condition counter in the profile: {ex.Message}");
                    }
                }
            }

            WalkQuests(profile, payload);

            // A SLOW one is always news and always at Info: it is the one the README tells a player to
            // quote when the panel is slow to open. The FIRST one is a measurement for whoever is
            // working on the mod, so it is a Detail line - visible under QUESTTREE_DEBUG, at Debug
            // otherwise - and every one after that stays at Debug, because this runs on every panel
            // open and at Info it was the loudest thing in the server console.
            var line =
                $"Quest Tracker: profile payload - level {payload.Level}, {payload.Traders.Count} traders, " +
                $"{payload.ConditionProgress.Count} counters, {payload.LockReasons.Count} locked quests explained, " +
                $"in {clock.ElapsedMilliseconds} ms.";

            if (clock.ElapsedMilliseconds > SlowRequestMs) { _timed = true; logger.Info(line); }
            else if (!_timed) { _timed = true; logger.Detail(line); }
            else logger.Debug(line);

            return payload;
        }

        /// <summary>
        /// One pass over the quest table for the two things the payload needs per quest: how many
        /// of each item a quest asks for the profile holds (every quest - scoped to templates some
        /// quest actually wants, so the payload is proportional to the quest database rather than
        /// to how much the player hoards), and for every quest not yet started, the one gate
        /// stopping it. These were two full walks per request, on every panel open.
        ///
        /// Lock reasons are checked in the order the player would act on them: things that can
        /// never change for this character first (faction, edition, event), then the ones they
        /// can move (level, loyalty, standing), then prerequisites. Only the FIRST blocker is
        /// reported - a list of five reasons is not more useful than the one thing to go and do.
        /// </summary>
        private void WalkQuests(PmcData profile, ProfilePayloadDto payload)
        {
            var quests = templateTable.Quests;
            if (quests == null) return;

            var owned = ProfileInventory.CountByTemplate(profile, out var locationsKnown);
            payload.InventoryLocationsKnown = locationsKnown;

            // One index, two answers. Built once because every unstarted quest checks each
            // prerequisite against the profile, and a scan of the quest list per check was millions
            // of comparisons per request on a large modded install.
            var progress = facts.IndexProgress(profile);
            var started = progress.Entries.Keys;
            var succeeded = progress.Succeeded;

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                // Two guards, not one: a quest whose conditions are malformed still gets its
                // lock reason, and the reverse.
                if (owned.Count > 0)
                {
                    try
                    {
                        CollectOwnership(quest, owned, payload);
                    }
                    catch (Exception ex)
                    {
                        // One quest's odd condition, not the ownership of every item.
                        logger.Warning($"Quest Tracker: skipped a quest's item ownership ({quest.Id}): {ex.Message}");
                    }
                }

                if (started.Contains(quest.Id)) continue; // already in the profile; not locked

                try
                {
                    var reason = facts.ResolveLockReason(quest, profile, succeeded);
                    if (reason != null) payload.LockReasons[quest.Id.ToString()] = reason;
                }
                catch (Exception ex)
                {
                    // A malformed or modded quest must not cost every other quest its explanation.
                    logger.Warning($"Quest Tracker: could not evaluate lock reason for '{quest.Id}': {ex.Message}");
                }
            }
        }

        private static void CollectOwnership(
            Quest quest, Dictionary<string, ProfileInventory.Held> owned, ProfilePayloadDto payload)
        {
            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                // The wide set, not the hand-in set: an MS2000 marker for a mark-the-place
                // objective is an item the player has to own, and a held count of nothing is how it
                // used to read.
                if (!QuestPayloadBuilder.IsAnyItemCondition(condition.ConditionType)) continue;

                foreach (var template in QuestPayloadBuilder.TargetIds(condition.Target))
                {
                    if (string.IsNullOrWhiteSpace(template)) continue;
                    if (payload.ItemsOwned.ContainsKey(template)) continue;
                    if (!owned.TryGetValue(template, out var held)) continue;

                    payload.ItemsOwned[template] = new HeldItemDto
                    {
                        FoundInRaid = held.FoundInRaid,
                        Total = held.Total,
                        OnPerson = held.OnPerson,
                        OnPersonFoundInRaid = held.OnPersonFoundInRaid,
                        InStash = held.InStash,
                        InTaskItems = held.InTaskItems
                    };
                }
            }
        }

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
