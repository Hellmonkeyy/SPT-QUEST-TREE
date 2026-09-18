using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Quests;

namespace QuestTree.QuestGraph
{
    /// <summary>Display status bucket a node is colored by. Collapses EQuestStatus down to the
    /// states the tree actually needs to distinguish.
    ///
    /// <see cref="Gated"/> is a split of Locked, and it is the one distinction a player acts on:
    /// "another quest has to happen first" and "I just need to level up" are the difference between
    /// not actionable and actionable-later, and until now the tree drew them identically. The data
    /// to tell them apart was already on the wire - LockReasonDto.Kind - and was being thrown away
    /// at the node.
    ///
    /// APPENDED, not inserted. The order is not persisted anywhere, but several switches compare
    /// against it and a couple of views sort by it; adding at the end keeps every existing value
    /// where it was.</summary>
    internal enum ENodeStatus
    {
        Locked,
        Available,
        Active,
        Completed,

        /// <summary>Every prerequisite quest is done; a level, loyalty or standing threshold is
        /// not. Never set for a gate that can never change (faction, edition, event) - those stay
        /// Locked, because "come back when you are level 30" is advice and "wrong edition" is
        /// not.</summary>
        Gated,

        /// <summary>The game has failed or expired the quest: Fail, FailRestartable, MarkedAsFailed
        /// or Expired. These used to fold into Locked, so a quest that can never be handed in drew
        /// exactly like one not yet reached - the tree stating something untrue. Whether it can be
        /// restarted is a detail for the panel (<see cref="QuestNode.FailureDetail"/>), not a
        /// sixth box.</summary>
        Failed
    }

    /// <summary>A maximal single-file run of quests from one trader, in order from the quest that
    /// starts it to the one that ends it. Two members is the shortest run worth naming.</summary>
    internal sealed class QuestChain
    {
        public readonly List<QuestNode> Members = new();

        public QuestNode Head => Members[0];
        public QuestNode Tail => Members[Members.Count - 1];

        public int Completed
        {
            get
            {
                var done = 0;
                foreach (var node in Members)
                    if (node.Status == ENodeStatus.Completed) done++;
                return done;
            }
        }
    }

    /// <summary>
    /// One quest as a node in the tree. Built once per quest by <see cref="QuestGraphBuilder"/> and
    /// re-used across status refreshes - only <see cref="Status"/> changes on a live update, so the
    /// graph structure (nodes, edges, depth) never needs recomputing just because a quest was
    /// turned in.
    ///
    /// Data-transfer-object first, not QuestTemplate first. The client is never sent templates for
    /// quests it has not unlocked, so the full list comes from the QuestTreeServer companion mod as
    /// a <see cref="QuestDto"/>. When that mod is absent, QuestGraphBuilder synthesizes equivalent
    /// DTOs from the quests the client does know about - so there is exactly one shape here, and
    /// nothing downstream has to care which source it came from.
    ///
    /// <see cref="LiveQuest"/> stays null until the game actually materializes a Quest instance for
    /// this template, which is what makes live status tracking work.
    /// </summary>
    internal sealed class QuestNode
    {
        /// <summary>Fallback TraderId for a quest with no assigned trader. Deliberately not ""
        /// (QuestTreePanel.AllTradersId's sentinel) or null (a Dictionary&lt;string,string&gt; key) -
        /// either would make a traderless quest indistinguishable from the "All" tab itself.</summary>
        public const string NoTraderId = "__no_trader__";

        public readonly QuestDto Dto;
        public readonly string Id;
        public readonly string TraderId;

        /// <summary>Null until QuestGraphBuilder finds a matching live instance in
        /// QuestController.Quests - i.e. until the game has actually unlocked this quest.</summary>
        public Quest LiveQuest;

        /// <summary>Resolved from the live session's trader list, not a hardcoded name - so a
        /// modded trader's quests are labelled correctly with no code changes.</summary>
        public string TraderName;

        /// <summary>Ids of quests that must be completed (per AvailableForStart/ConditionQuest)
        /// before this one unlocks. Empty for a trader's opening quest.</summary>
        public readonly List<string> PrerequisiteIds = new();

        /// <summary>Populated after the graph is built: every node that lists this one as a
        /// prerequisite. This is what the tree actually draws lines out to.</summary>
        public readonly List<QuestNode> Unlocks = new();

        /// <summary>Prerequisite ids the quest list does not contain, filled by the builder. Such a
        /// quest can never unlock - a quest mod referencing a quest another mod removed, or a
        /// half-updated install - and it draws as a root, because the depth walk skips what it
        /// cannot find. Empty on a healthy install; the ids are raw because the name is, by
        /// definition, unavailable, and the id is what identifies the absent mod.</summary>
        public readonly List<string> UnresolvedPrerequisiteIds = new();

        /// <summary>Topological layer - 0 for a quest with no prerequisites, otherwise
        /// 1 + max(depth of prerequisites). Drives the node's horizontal column.</summary>
        public int Depth;

        /// <summary>How many quests this one eventually opens - the whole downstream closure, not
        /// just the direct successors in Unlocks.
        ///
        /// Direct successors barely tell you anything: the median quest has one, and a quest that
        /// opens one which opens fifty looks identical to a dead end. The closure separates them -
        /// Saving the Mole reaches 246 where the median reaches 2 - which is what makes it worth
        /// computing rather than reading Unlocks.Count.</summary>
        public int UnlockReach;

        public ENodeStatus Status;

        /// <summary>The linear chain this quest sits in, or null. A chain is a maximal run of
        /// quests where each has exactly one prerequisite and exactly one unlock, both in the run,
        /// all from one trader - a Weapon Proficiency sequence, say. Found once per build by
        /// QuestGraphBuilder.FindChains; the tree can draw such a run as one box.</summary>
        public QuestChain Chain;

        /// <summary>This quest's position in <see cref="Chain"/>, 0 for the head.</summary>
        public int ChainIndex;

        /// <summary>On the canonical Kappa list - Collector's start conditions in SPT's shipped
        /// database, or the player's own kappa-quests.json.</summary>
        public bool IsKappaRequired;

        /// <summary>Must be finished before Collector can be accepted ON THIS INSTALL: a
        /// transitive prerequisite of the live Collector quest. Distinct from the above, because a
        /// quest mod can trim Collector's requirements to a handful while the canonical list still
        /// names a hundred and thirty.</summary>
        public bool IsCollectorPrerequisite;

        public string Name => string.IsNullOrEmpty(Dto.Name) ? Id : Dto.Name;

        /// <summary>Player level the quest requires, or 0 if none.</summary>
        public int Level => Dto.Level;

        public string LocationId => Dto.LocationId;

        /// <summary>Raw map id, used to match this quest's map against external map data.</summary>
        public string LocationKey => Dto.LocationKey;

        /// <summary>The maps this quest was placed on by its objectives, when its own declaration
        /// said nothing useful. Empty for a quest that names its own map, and for one whose zones
        /// have never been harvested - better silent than wrong, since a false entry would drag a
        /// false readiness verdict with it.
        ///
        /// Never null, matching StatedObjectives: QuestGraphBuilder synthesises DTOs when the
        /// server half is absent, and a null here would be a crash rather than a quiet degrade.</summary>
        public IEnumerable<DerivedLocationDto> DerivedLocations =>
            Dto.DerivedLocations == null
                ? Enumerable.Empty<DerivedLocationDto>()
                : Dto.DerivedLocations.Where(d => d != null && !string.IsNullOrEmpty(d.Key));

        /// <summary>Every map this quest belongs to: its own, plus any derived. Used wherever the
        /// question is "does this quest appear on that map" rather than "what does its subtitle
        /// say".</summary>
        public IEnumerable<string> MapKeys
        {
            get
            {
                if (!string.IsNullOrEmpty(LocationKey) &&
                    !LocationKey.Equals(AnyLocation, StringComparison.OrdinalIgnoreCase))
                {
                    yield return LocationKey;
                }

                foreach (var derived in DerivedLocations) yield return derived.Key;
            }
        }

        /// <summary>What a quest's Location field says when it declines to name a map.</summary>
        public const string AnyLocation = "any";

        /// <summary>Everything this quest can be found by, lowercased and joined, built once per
        /// graph.
        ///
        /// The search box runs over every node on every keystroke, so what it does per node has to
        /// be ONE comparison rather than fifteen. Rebuilt whenever the graph is - which now includes
        /// after a zone harvest, because the derived locations in here move with it.
        ///
        /// Status is deliberately not in here: it changes as you play, and it is what the filters
        /// are for.</summary>
        public string SearchText { get; private set; } = "";

        /// <summary>Fills the search haystack. Called by the builder once the node's unlocks and
        /// trader name are resolved, since both are searchable and neither exists at
        /// construction.</summary>
        public void BuildSearchText(string traderName)
        {
            var parts = new List<string> { Name, traderName, LocationId };

            foreach (var derived in DerivedLocations) parts.Add(derived.Name);
            foreach (var reward in Rewards) parts.Add(reward?.Name);
            foreach (var unlock in Unlocks) parts.Add(unlock?.Name);

            foreach (var objective in StatedObjectives)
            {
                if (objective?.TargetItemNames == null) continue;
                foreach (var item in objective.TargetItemNames) parts.Add(item);
            }

            var builder = new System.Text.StringBuilder();

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(part.ToLowerInvariant());
            }

            SearchText = builder.ToString();
        }

        /// <summary>Why this node matched, computed only for nodes that ALREADY matched - a handful
        /// - so it can afford to walk the fields properly and name both the field and the value.
        ///
        /// A box titled "Debut" matching a search for "PL-15" is baffling with no explanation, and
        /// there is no room in the box for one.</summary>
        public string MatchReason(string needle)
        {
            if (string.IsNullOrEmpty(needle)) return null;

            if (Contains(Name, needle)) return null;   // matched on its own name: self-evident

            foreach (var reward in Rewards)
                if (Contains(reward?.Name, needle)) return $"reward: {reward.Name}";

            foreach (var unlock in Unlocks)
                if (Contains(unlock?.Name, needle)) return $"unlocks: {unlock.Name}";

            foreach (var objective in StatedObjectives)
            {
                if (objective?.TargetItemNames == null) continue;

                foreach (var item in objective.TargetItemNames)
                    if (Contains(item, needle)) return $"needs: {item}";
            }

            if (Contains(LocationId, needle)) return $"map: {LocationId}";

            foreach (var derived in DerivedLocations)
                if (Contains(derived.Name, needle)) return $"map: {derived.Name}";

            return "trader";
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        public QuestNode(QuestDto dto)
        {
            Dto = dto;
            Id = dto.Id;

            // Normalized rather than left null or "": it's used as a Dictionary<string,string> key
            // for trader-name lookups and a GroupBy/sort key for layout (a null reference-type key
            // throws on Dictionary.TryGetValue), and "" specifically is QuestTreePanel.AllTradersId's
            // sentinel - either would make a traderless quest collide with something else.
            TraderId = string.IsNullOrEmpty(dto.TraderId) ? NoTraderId : dto.TraderId;

            if (dto.Prerequisites == null) return;

            // Deduplicated: a quest can list the same prerequisite twice (once per condition
            // type), and each copy used to draw its own line on top of the other.
            foreach (var prerequisite in dto.Prerequisites)
            {
                var target = prerequisite?.Target;
                if (!string.IsNullOrEmpty(target) && !PrerequisiteIds.Contains(target))
                    PrerequisiteIds.Add(target);
            }
        }

        /// <summary>Every objective the quest states in words.
        ///
        /// This used to filter on IsNecessary and was called NecessaryObjectives, which hid the
        /// objectives of **42% of quests**. The flag does not mean what the name assumed: across the
        /// 1,606 finish conditions in the shipped database it is absent 1,080 times, explicitly
        /// false 526 times, and **true not once**. The absent ones survive because the server maps
        /// `IsNecessary ?? true`; the explicit false ones were dropped on the floor.
        ///
        /// So the flag carries no positive signal and filtering on it could only ever hide things -
        /// 173 vanilla quests and 38 modded ones showed NO objectives at all, and 149 more showed a
        /// partial list. Sanitary Investigation - Part 5 marks all six false and the text reads
        /// "Plant the modified 1GPhone at the first set of TerraGroup..." - which IS the quest.
        ///
        /// Nothing is marked optional in the result either: a flag that is false on 526 conditions
        /// and true on none does not mean optional, it means nothing.
        ///
        /// The empty-text guard stays. An objective the quest author wrote no sentence for has
        /// nothing to show a reader. Unlike the old template-reading version this works for a quest
        /// that has never been unlocked, because the text arrives with the payload rather than being
        /// read out of a live instance the game has not created.</summary>
        public IEnumerable<ObjectiveDto> StatedObjectives =>
            Dto.Objectives == null
                ? Enumerable.Empty<ObjectiveDto>()
                : Dto.Objectives.Where(o => o != null && !string.IsNullOrEmpty(o.Text));

        /// <summary>Every weapon build this quest asks for. Usually one; "Old Friend's Request"
        /// asks for three.</summary>
        public List<WeaponBuildDto> WeaponBuilds =>
            Dto.WeaponBuilds ?? new List<WeaponBuildDto>();

        /// <summary>The weapon-build requirement this quest states, or null for the 774 quests
        /// that state none.</summary>
        public IEnumerable<RewardDto> Rewards =>
            Dto.Rewards == null ? Enumerable.Empty<RewardDto>() : Dto.Rewards.Where(r => r != null);

        /// <summary>A failed quest the trader will hand back: the one kind of failure that is still
        /// something to do, which is why Do next keeps it and drops the rest.</summary>
        public bool CanRestart => LiveQuest != null && LiveQuest.QuestStatus == EQuestStatus.FailRestartable;

        /// <summary>How the game failed this quest, in the words the panel shows, or null when it
        /// has not. Read from the live instance, which is the only place the distinction between a
        /// restartable failure and a final one exists.</summary>
        public string FailureDetail
        {
            get
            {
                if (LiveQuest == null) return null;

                return LiveQuest.QuestStatus switch
                {
                    EQuestStatus.FailRestartable => "Failed - can be restarted at the trader",
                    EQuestStatus.Expired => "Expired",
                    EQuestStatus.Fail or EQuestStatus.MarkedAsFailed => "Failed",
                    _ => null
                };
            }
        }

        /// <summary>Why this quest can never be completed on this profile, or null when it can be.
        /// Faction- and edition-locked quests are deliberately shown rather than hidden, so this is
        /// what stops them reading as a bug in the tree.</summary>
        public string UnobtainableReason
        {
            get
            {
                if (Dto.EditionRestricted) return "Restricted to another game edition";
                if (Dto.IsEvent) return "Seasonal event quest";

                if (!string.IsNullOrEmpty(Dto.Side) &&
                    !Dto.Side.Equals("Pmc", System.StringComparison.OrdinalIgnoreCase))
                {
                    return $"{Dto.Side} only";
                }

                return null;
            }
        }
    }
}
