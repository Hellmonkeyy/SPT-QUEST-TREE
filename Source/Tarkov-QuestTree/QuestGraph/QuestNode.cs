using System;
using System.Collections.Generic;
using System.Linq;
using EFT.Quests;

namespace QuestTree.QuestGraph
{
    /// <summary>Display status bucket a node is colored by. Collapses EQuestStatus down to the
    /// four states the tree actually needs to distinguish.</summary>
    internal enum ENodeStatus
    {
        Locked,
        Available,
        Active,
        Completed
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

        /// <summary>Topological layer - 0 for a quest with no prerequisites, otherwise
        /// 1 + max(depth of prerequisites). Drives the node's horizontal column.</summary>
        public int Depth;

        public ENodeStatus Status;

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
        /// Never null, matching NecessaryObjectives: QuestGraphBuilder synthesises DTOs when the
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

        /// <summary>The objectives worth showing - the necessary ones. Unlike the old
        /// template-reading version, this works for a quest that has never been unlocked, because
        /// the objective text arrives with the payload rather than being read out of a live
        /// instance the game has not created.</summary>
        public IEnumerable<ObjectiveDto> NecessaryObjectives =>
            Dto.Objectives == null
                ? Enumerable.Empty<ObjectiveDto>()
                : Dto.Objectives.Where(o => o != null && o.IsNecessary && !string.IsNullOrEmpty(o.Text));

        public IEnumerable<RewardDto> Rewards =>
            Dto.Rewards == null ? Enumerable.Empty<RewardDto>() : Dto.Rewards.Where(r => r != null);

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
