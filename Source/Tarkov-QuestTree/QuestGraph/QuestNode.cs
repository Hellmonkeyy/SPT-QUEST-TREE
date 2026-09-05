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

        public bool IsKappaRequired;

        public string Name => string.IsNullOrEmpty(Dto.Name) ? Id : Dto.Name;

        /// <summary>Player level the quest requires, or 0 if none.</summary>
        public int Level => Dto.Level;

        public string LocationId => Dto.LocationId;

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

            foreach (var prerequisite in dto.Prerequisites)
            {
                if (!string.IsNullOrEmpty(prerequisite?.Target))
                    PrerequisiteIds.Add(prerequisite.Target);
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
