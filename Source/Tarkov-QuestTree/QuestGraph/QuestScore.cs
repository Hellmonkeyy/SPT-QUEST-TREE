using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.UI;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>What a player is currently playing for. The same quest is not equally worth doing
    /// under all of these, which is the whole reason this is a choice rather than a constant: a
    /// quest paying 200k experience and opening nothing is the best thing on the list when you are
    /// levelling and near the worst when you are chasing Kappa.</summary>
    internal enum RankGoal
    {
        Balanced,
        KappaPath,
        FastLevelling,
        TraderUnlocks,
        ItemHoarding
    }

    /// <summary>Scores a quest against a goal, and says why.
    ///
    /// Every component returns a number between 0 and 1 or NOTHING AT ALL, and the difference
    /// matters more than it looks. Forty of the 294 modded quests on a typical install carry no
    /// experience reward and sixty-three carry no trader standing; if a missing component scored
    /// zero, every one of those quests would sink for a reason that is about the quest data rather
    /// than the quest. So a component that cannot be computed drops out of the weighted mean and
    /// the remaining components share its weight.
    ///
    /// The score is deliberately explainable. Breakdown carries the contributors so a row can say
    /// "unlocks 12 - 22k XP - items held", and if that sentence does not justify the position the
    /// row is in, the weights are wrong and you can see it. That is the only check available on a
    /// ranking: there is no ground truth for "the best quest to do next".</summary>
    internal static class QuestScore
    {
        /// <summary>A component's score and what it was worth after weighting, kept so the row can
        /// explain itself.</summary>
        internal readonly struct Part
        {
            public Part(string label, float score, float weight)
            {
                Label = label;
                Score = score;
                Weight = weight;
            }

            public string Label { get; }
            public float Score { get; }
            public float Weight { get; }
            public float Weighted => Score * Weight;
        }

        internal sealed class Breakdown
        {
            public float Total;
            public readonly List<Part> Parts = new();

            /// <summary>The contributors worth naming, biggest first. Components that scored nothing
            /// are dropped - "0% of items" explains a position nobody is asking about.</summary>
            public IEnumerable<Part> Top(int count) =>
                Parts.Where(p => p.Weighted > 0.01f)
                     .OrderByDescending(p => p.Weighted)
                     .Take(count);
        }

        // Scale anchors, taken from the shipped quest database rather than guessed. Experience runs
        // 500 to 433,333 with a median of 13,450; standing runs to 1.1 with a median of 0.02; the
        // deepest unlock closure is 246. Log scaling throughout, because the difference between 5k
        // and 50k experience matters and the difference between 300k and 400k does not.
        private const float MaxExperience = 450_000f;
        private const float MaxRoubles = 2_000_000f;
        private const float MaxStanding = 0.5f;
        private const float MaxReach = 250f;
        private const float MaxObjectives = 12f;

        /// <summary>Weights per goal, in the component order used by Score. They do not need to sum
        /// to anything - the mean divides by whatever is present.</summary>
        private static readonly Dictionary<RankGoal, float[]> Weights = new()
        {
            //                        reward unlock trader ready  near  kappa  effort
            [RankGoal.Balanced]      = new[] { 1.0f, 1.0f, 0.6f, 1.0f, 1.0f, 0.5f, 0.5f },
            [RankGoal.KappaPath]     = new[] { 0.3f, 0.8f, 0.2f, 0.8f, 1.0f, 3.0f, 0.4f },
            [RankGoal.FastLevelling] = new[] { 2.5f, 0.3f, 0.2f, 1.0f, 1.2f, 0.0f, 1.2f },
            [RankGoal.TraderUnlocks] = new[] { 0.5f, 0.8f, 3.0f, 0.8f, 1.0f, 0.2f, 0.5f },
            [RankGoal.ItemHoarding]  = new[] { 1.2f, 0.4f, 0.6f, 2.0f, 0.8f, 0.6f, 0.8f }
        };

        private static readonly string[] Labels =
            { "reward", "unlocks", "trader", "ready", "close", "kappa", "quick" };

        internal static Breakdown Of(QuestNode node, ProfilePayloadDto profile, QuestGraphBuilder graph, RankGoal goal)
        {
            var result = new Breakdown();
            if (node == null) return result;

            var weights = Weights.TryGetValue(goal, out var w) ? w : Weights[RankGoal.Balanced];

            var scores = new[]
            {
                RewardScore(node, profile),
                ReachScore(node),
                TraderScore(node, profile),
                ReadyScore(node, profile),
                NearScore(node, profile, graph),
                KappaScore(node),
                EffortScore(node)
            };

            var sum = 0f;
            var divisor = 0f;

            for (var i = 0; i < scores.Length; i++)
            {
                if (scores[i] == null) continue;
                if (weights[i] <= 0f) continue;

                var score = Mathf.Clamp01(scores[i].Value);

                result.Parts.Add(new Part(Labels[i], score, weights[i]));
                sum += score * weights[i];
                divisor += weights[i];
            }

            result.Total = divisor > 0f ? sum / divisor : 0f;

            return result;
        }

        /// <summary>What the quest pays: experience, roubles, standing, skill points, blended.
        ///
        /// Null only when the quest has no rewards we can read at all, which is five vanilla quests
        /// and a scattering of modded ones. A quest that pays experience but no roubles is not
        /// missing a component - it scored what it pays.</summary>
        private static float? RewardScore(QuestNode node, ProfilePayloadDto profile)
        {
            var rewards = node.Dto?.Rewards;
            if (rewards == null || rewards.Count == 0) return null;

            var experience = 0d;
            var roubles = 0d;
            var standing = 0d;
            var skill = 0d;
            var sawAnything = false;

            foreach (var reward in rewards)
            {
                if (reward == null) continue;

                switch (reward.Type)
                {
                    case "Experience":
                        experience += reward.Value;
                        sawAnything = true;
                        break;

                    case "TraderStanding":
                        // Negative standing is a real thing on twelve rewards and it should count
                        // against the quest rather than be clamped away here.
                        standing += reward.Value;
                        sawAnything = true;
                        break;

                    case "Skill":
                        skill += reward.Value;
                        sawAnything = true;
                        break;

                    case "Item":
                        // RoubleValue is null for an item with no handbook price. Skipping it leaves
                        // the quest scored on what we CAN price rather than pretending it is worthless.
                        if (reward.RoubleValue.HasValue)
                        {
                            roubles += reward.RoubleValue.Value;
                            sawAnything = true;
                        }
                        break;

                    // Everything else - assort unlocks, production schemes, achievements, and
                    // whatever a mod invents - is either scored by TraderScore or is not a reward
                    // with a magnitude. A default arm exists so an unknown type cannot throw.
                    default:
                        break;
                }
            }

            if (!sawAnything) return null;

            var xp = LogScale(experience, MaxExperience);
            var cash = LogScale(roubles, MaxRoubles);
            var rep = LogScale(Math.Max(0d, standing), MaxStanding);
            var skl = LogScale(skill, 500d);

            // Experience and money carry the weight; standing and skills are seasoning. A quest is
            // rarely chosen for its 0.02 reputation.
            var score = (xp * 0.45f) + (cash * 0.35f) + (rep * 0.12f) + (skl * 0.08f);

            // A standing LOSS is worth flagging in the score, since a few quests cost you a trader.
            if (standing < 0d) score -= 0.15f;

            return Mathf.Clamp01(score);
        }

        /// <summary>How much of the tree this quest opens. Never null - a quest that opens nothing
        /// scores zero honestly, because we know its closure is empty rather than unknown.</summary>
        private static float? ReachScore(QuestNode node) => LogScale(node.UnlockReach, MaxReach);

        /// <summary>Trader unlocks and new offers, discounted by whether you could use them.
        ///
        /// An offer that appears at loyalty 4 is worth little to someone at loyalty 2 - it is a
        /// reason to do the quest eventually, not now. Null when the quest unlocks nothing, so a
        /// quest is not punished for being the wrong kind of quest.</summary>
        private static float? TraderScore(QuestNode node, ProfilePayloadDto profile)
        {
            var rewards = node.Dto?.Rewards;
            if (rewards == null || rewards.Count == 0) return null;

            var score = 0f;
            var found = false;

            foreach (var reward in rewards)
            {
                if (reward == null) continue;

                if (reward.Type == "TraderUnlock")
                {
                    // A whole new trader is the biggest unlock in the game.
                    score += 1f;
                    found = true;
                    continue;
                }

                if (reward.Type != "AssortmentUnlock" && reward.Type != "ProductionScheme") continue;

                found = true;

                var reachable = ReachableLoyalty(reward, profile);
                score += reachable ? 0.25f : 0.08f;
            }

            return found ? Mathf.Clamp01(score) : (float?)null;
        }

        /// <summary>Whether the player's loyalty with that trader already reaches the offer.
        ///
        /// A reward with no trader is not gated by one, and that is the ProductionScheme case. A hideout
        /// craft unlock carries a hideout AREA and the LEVEL of it the craft needs - verified against the
        /// shipped data, where all 31 of them name area 10, 2, 7 or 11, every one a real hideout area
        /// type, with levels 1 to 3. Nothing about a trader.
        ///
        /// It used to arrive in TraderId, so this matched an area number against the trader list, never
        /// found one, returned false, and scored every hideout-craft unlock 0.08 where a reachable unlock
        /// scores 0.25 - the "Do next" order was wrong for those quests and nothing said so. The server no
        /// longer sends an area id as a trader; this is the half that stops an absent trader reading as an
        /// unreachable one.
        ///
        /// The hideout level genuinely is not checked: the mod models no hideout state. Counting the
        /// unlock as reachable is the better of the two available errors, since the craft is permanent and
        /// the area is buildable, where 0.08 said "you effectively cannot have this".
        ///
        /// An older server still sends the area id here, the lookup still fails, and the score degrades to
        /// what it has always been rather than to something new.</summary>
        private static bool ReachableLoyalty(RewardDto reward, ProfilePayloadDto profile)
        {
            if (reward.LoyaltyLevel <= 0) return true;
            if (string.IsNullOrEmpty(reward.TraderId)) return true;
            if (profile?.Traders == null) return false;

            var trader = profile.Traders.FirstOrDefault(t => t != null && t.Id == reward.TraderId);

            return trader != null && trader.LoyaltyLevel >= reward.LoyaltyLevel;
        }

        /// <summary>Whether the payload can say WHERE the profile's items are.
        ///
        /// Both halves of the contract, which the two callers here were only honouring one of. The
        /// per-location counts arrived in schema 2, and reading them from an older payload gives zero
        /// rather than unknown - and zero reads exactly like "carrying nothing" on a full rig.
        /// QuestSummary.AddItemsToBring has always checked both; the version check was missing here.
        ///
        /// It matters more now than it did: a wrong answer used to nudge a ranking number, and now it
        /// flips a flat claim that a quest is ready to hand in.</summary>
        private static bool PlacesKnown(ProfilePayloadDto profile) =>
            profile != null &&
            profile.SchemaVersion >= ProfilePayloadDto.SupportedSchemaVersion &&
            profile.InventoryLocationsKnown;

        /// <summary>Whether this quest could be handed in right now: accepted, and every item it
        /// asks for already held.
        ///
        /// Deliberately NOT part of the score. The score already values readiness; this is about
        /// SAYING it, and it is the most actionable thing the tab can print - a trip to a trader for
        /// experience already earned.
        ///
        /// It exists because the row could not say it. Detail short-circuits an accepted quest to
        /// ObjectiveProgress, which counts an objective done only through ConditionProgress - and item
        /// objectives have no ConditionProgress entry at all. So a quest whose items you hold in full
        /// read "0/3 objectives": the row that should have said "walk to the trader" was the one that
        /// understated hardest.
        ///
        /// An ALL-of check over whole objectives, not a ratio. ReadyScore blends item progress and
        /// counter progress into one fraction, which is right for ranking and wrong for this: a quest
        /// at 0.99 cannot be handed in, and one at 0.5 whose remaining half is a kill counter cannot
        /// either.</summary>
        internal static bool CanHandIn(QuestNode node, ProfilePayloadDto profile)
        {
            if (node == null || node.Status != ENodeStatus.Active) return false;

            var objectives = node.Dto?.Objectives;
            if (objectives == null || objectives.Count == 0) return false;

            var judged = false;

            foreach (var objective in objectives)
            {
                if (objective == null) continue;

                judged = true;
                if (!ObjectiveSatisfied(objective, profile)) return false;
            }

            return judged;
        }

        /// <summary>Whether ONE objective is already satisfied - the single rule, so no surface has to
        /// invent its own.
        ///
        /// Extracted from CanHandIn because a second surface needed it and got it wrong on its own.
        /// The node box's "N/M objectives" counted every objective through ConditionProgress, which
        /// item objectives do not have, so a quest asking only for hand-overs read 0/2 with an empty
        /// bar while the Do next row for the same quest, going through CanHandIn, said "ready to hand
        /// in". Two surfaces disagreeing about one quest is the thing the shared fold exists to stop,
        /// and this is the same argument one level down.
        ///
        /// A completed quest is not special-cased here. Callers that know the quest is finished say so
        /// themselves - the box does - because this answers only what the numbers support.</summary>
        internal static bool ObjectiveSatisfied(ObjectiveDto objective, ProfilePayloadDto profile)
        {
            if (objective == null) return false;

            if (objective.TargetItems == null || objective.TargetItems.Count == 0)
            {
                // A counter that is finished does not block a hand-in, and a quest made only of
                // finished counters IS ready - an earlier version of this required at least one
                // ITEM objective, which drew the mark on one finished quest and not on another for
                // reasons invisible to the reader.
                //
                // Unreadable counts as NOT done. TryProgress returns false when the profile has no
                // entry for the condition, which means unknown rather than complete, and the whole
                // point of this check is that it is asserted rather than estimated.
                if (!QuestSummary.TryProgress(objective, profile, out var current, out var target)) return false;

                return target <= 0 || current >= target;
            }

            // Every alternative template counts toward the same requirement, and all three places
            // count: when the server cannot say WHERE things are, HeldCount puts the lot in OnYou
            // and leaves the other two at zero, so reading OnYou alone would be right by accident
            // on an old payload and wrong on a new one.
            var have = QuestSummary.HeldCount(
                profile, objective.TargetItems, QuestSummary.NeedsFoundInRaid(objective),
                PlacesKnown(profile));

            return have.OnYou + have.InStash + have.Elsewhere >= Mathf.Max(1, objective.Count);
        }

        /// <summary>How close the quest is to being finished right now - items already held and
        /// objectives already ticked.
        ///
        /// Null when the quest asks for nothing measurable, which is most kill and exploration
        /// quests; those are not "0% ready", they are quests whose readiness we cannot see.</summary>
        private static float? ReadyScore(QuestNode node, ProfilePayloadDto profile)
        {
            var objectives = node.Dto?.Objectives;
            if (objectives == null || objectives.Count == 0) return null;

            var placesKnown = PlacesKnown(profile);
            var required = 0;
            var held = 0;
            var measured = 0;
            var done = 0;

            foreach (var objective in objectives)
            {
                if (objective == null) continue;

                if (objective.TargetItems != null && objective.TargetItems.Count > 0)
                {
                    var need = Mathf.Max(1, objective.Count);
                    required += need;

                    // Every alternative template counts, which is what the old ItemProgress got
                    // wrong by reading TargetItems[0] and nothing else.
                    var have = QuestSummary.HeldCount(
                        profile, objective.TargetItems, QuestSummary.NeedsFoundInRaid(objective), placesKnown);

                    held += Mathf.Min(need, have.OnYou + have.InStash + have.Elsewhere);
                    continue;
                }

                // A counter objective's progress lives in the profile against the condition id.
                if (profile?.ConditionProgress == null || string.IsNullOrEmpty(objective.Id)) continue;
                if (!profile.ConditionProgress.TryGetValue(objective.Id, out var progress)) continue;

                var target = Mathf.Max(1, objective.Count);
                measured += target;
                done += Mathf.Min(target, (int)progress);
            }

            if (required + measured == 0) return null;

            return (float)(held + done) / (required + measured);
        }

        /// <summary>How close the quest is to being startable at all. An accepted quest is as near
        /// as it gets; a locked one falls away with the size of the gate.</summary>
        private static float? NearScore(QuestNode node, ProfilePayloadDto profile, QuestGraphBuilder graph)
        {
            switch (node.Status)
            {
                case ENodeStatus.Active:
                    return 1f;
                case ENodeStatus.Available:
                    return 0.9f;
            }

            // The lock reason carries the numbers: how many levels, how much loyalty, how much
            // standing short. Only the FIRST blocking gate is reported, so this is a floor on the
            // distance rather than the whole of it.
            if (profile?.LockReasons != null && profile.LockReasons.TryGetValue(node.Id, out var reason) && reason != null)
            {
                switch (reason.Kind)
                {
                    case "Level":
                    {
                        var short_ = Math.Max(0d, reason.RequiredValue - reason.CurrentValue);
                        return Mathf.Clamp01(1f - (float)(short_ / 20d)) * 0.7f;
                    }
                    case "Loyalty":
                    case "Standing":
                        return 0.35f;
                    case "OtherFaction":
                    case "Edition":
                    case "Event":
                        // Not a distance at all - you cannot get there from here.
                        return 0f;
                }
            }

            // Otherwise it is quests in the way, and the route knows how many.
            var remaining = QuestRoute.Remaining(node, graph)?.Count ?? 0;
            if (remaining <= 0) return 0.6f;

            return Mathf.Clamp01(1f - (remaining / 10f)) * 0.6f;
        }

        /// <summary>On the Kappa list, or required for Collector on this install. Never null: both
        /// are known booleans, and false means false.</summary>
        private static float? KappaScore(QuestNode node)
        {
            if (node.IsKappaRequired) return 1f;
            if (node.IsCollectorPrerequisite) return 0.7f;

            return 0f;
        }

        /// <summary>How little work the quest looks like, so a five-minute hand-in outranks a
        /// twenty-kill grind when everything else is equal.
        ///
        /// A rough proxy and honest about it: objective count, with found-in-raid requirements
        /// counted heavier because you cannot buy your way out of them.</summary>
        private static float? EffortScore(QuestNode node)
        {
            var objectives = node.Dto?.Objectives;
            if (objectives == null || objectives.Count == 0) return null;

            var cost = 0f;

            foreach (var objective in objectives)
            {
                if (objective == null) continue;

                // NOT filtered on IsNecessary, and the flag is why this comment exists. It is true on zero
                // of the 1,606 finish conditions in the quest database - absent 1,080 times, explicitly
                // false 526 - so filtering on it can only ever hide work. It hid the OBJECTIVES of 42% of
                // quests once and that was fixed; this consumer kept the filter, so 173 quests whose every
                // condition is explicitly false scored a cost of zero, returned null here, and had the
                // effort term dropped from their score entirely - a twenty-kill grind ranking as though it
                // cost nothing. Shootout Picnic, Operation Aquarius, The Punisher - Part 1 and Spa Tour -
                // Part 1 are among them, with 100 more merely understated.
                cost += 1f;

                if (QuestSummary.NeedsFoundInRaid(objective)) cost += 1f;

                // A counter with a big target is a grind however few objectives it is.
                if (objective.Count > 5) cost += 0.5f;
            }

            if (cost <= 0f) return null;

            return Mathf.Clamp01(1f - (cost / MaxObjectives));
        }

        /// <summary>Log scaling to 0-1. The gap between 5,000 and 50,000 experience is worth
        /// something; the gap between 300,000 and 400,000 is not, and a linear scale would say the
        /// opposite.</summary>
        private static float LogScale(double value, double max)
        {
            if (value <= 0d || max <= 0d) return 0f;

            return Mathf.Clamp01((float)(Math.Log(1d + value) / Math.Log(1d + max)));
        }
    }
}
