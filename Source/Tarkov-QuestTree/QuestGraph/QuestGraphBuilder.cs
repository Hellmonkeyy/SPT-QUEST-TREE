using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Quests;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Builds the quest DAG once, from the full quest list served by the QuestTreeServer companion
    /// mod.
    ///
    /// The client is never sent quests it has not unlocked - /client/quest/list is filtered down by
    /// the server's QuestHelper.GetClientQuests to quests already in the profile plus those whose
    /// prerequisites are all already satisfied - so the full list has to come from the companion
    /// mod. When that mod is not installed, this falls back to synthesizing the same DTO shape from
    /// QuestController.Quests, which yields a correct but much smaller tree.
    ///
    /// QuestController.Quests is still used either way, but only to find a LIVE status for the
    /// quests the game has actually materialized. Everything else renders Locked.
    ///
    /// Only <see cref="RefreshStatuses"/> needs to re-run when the game reports a quest status
    /// change (or unlocks a new one); the structure (nodes, edges, depth) never changes at runtime.
    /// </summary>
    internal sealed class QuestGraphBuilder
    {
        private Dictionary<string, QuestNode> _byId = new();
        private QuestController _questController;

        public IReadOnlyList<QuestNode> Nodes { get; private set; } = Array.Empty<QuestNode>();

        /// <summary>Same nodes as <see cref="Nodes"/>, keyed by id - lets callers (e.g. the detail
        /// panel resolving a prerequisite's name) do an O(1) lookup instead of scanning Nodes.</summary>
        public IReadOnlyDictionary<string, QuestNode> NodesById => _byId;

        /// <summary>True when the graph was built from the companion server mod's full quest list
        /// rather than the client's own unlocked-only view. Surfaced so the UI can say which.</summary>
        public bool HasFullQuestList { get; private set; }

        /// <summary>Trader id -> display name, resolved from the live session rather than a
        /// hardcoded vanilla trader list, so modded traders get a correct label automatically.</summary>
        public IReadOnlyDictionary<string, string> TraderNames { get; private set; } =
            new Dictionary<string, string>();

        public void Build(QuestController questController, IEftSession session)
        {
            _questController = questController;
            _byId = new Dictionary<string, QuestNode>();

            var quests = QuestDataClient.TryFetchAll();
            HasFullQuestList = quests != null;

            if (quests == null) quests = BuildFallbackDtos(questController);

            foreach (var dto in quests)
            {
                if (string.IsNullOrEmpty(dto?.Id)) continue;
                // A quest can appear more than once across repeatable/profile-specific churn; keep
                // the first instance seen rather than throwing on a duplicate key.
                _byId.TryAdd(dto.Id, new QuestNode(dto));
            }

            LinkLiveQuests();

            // PrerequisiteIds are populated by QuestNode's constructor; this is the reverse edge.
            foreach (var node in _byId.Values)
            {
                foreach (var prerequisiteId in node.PrerequisiteIds)
                {
                    if (_byId.TryGetValue(prerequisiteId, out var prerequisiteNode))
                        prerequisiteNode.Unlocks.Add(node);
                }
            }

            var depths = ComputeDepths(_byId);
            foreach (var node in _byId.Values)
                node.Depth = depths[node.Id];

            RefreshKappaFlags();

            // Seeded here rather than special-cased wherever a trader name is displayed (tab
            // labels, node subtitles, the detail panel), so every one of those gets a friendly
            // string instead of QuestNode.NoTraderId's raw "__no_trader__" sentinel leaking out.
            var traderNames = new Dictionary<string, string> { [QuestNode.NoTraderId] = "No Trader" };
            if (session?.Traders != null)
            {
                foreach (var trader in session.Traders)
                {
                    if (trader?.Id == null) continue;
                    // LocalizedName can be null (locale not yet loaded, or a modded trader missing
                    // a locale key) - falling back to the id here, not just where it's read back
                    // out, means every consumer (tab labels included) gets a non-null string to
                    // work with instead of needing its own null guard.
                    traderNames[trader.Id] = string.IsNullOrEmpty(trader.LocalizedName) ? trader.Id : trader.LocalizedName;
                }
            }

            foreach (var node in _byId.Values)
            {
                node.TraderName = traderNames.TryGetValue(node.TraderId, out var name)
                    ? name
                    : node.TraderId; // unresolved trader (e.g. not yet loaded) - fall back to the raw id rather than blank
            }

            RefreshStatusesInternal();

            Nodes = _byId.Values.ToArray();
            TraderNames = traderNames;
        }

        /// <summary>
        /// The no-companion-mod path: the same DTO shape, synthesized from the quests the client
        /// already has. Building the fallback as DTOs rather than as a second node type means every
        /// consumer downstream - layout, search, detail panel - has exactly one shape to handle.
        ///
        /// This is necessarily the unlocked-only view; that is the whole reason the companion mod
        /// exists.
        /// </summary>
        private static List<QuestDto> BuildFallbackDtos(QuestController questController)
        {
            var dtos = new List<QuestDto>();
            if (questController?.Quests == null) return dtos;

            foreach (var quest in questController.Quests)
            {
                var template = quest?.Template;
                if (template?.Id == null) continue;

                dtos.Add(new QuestDto
                {
                    Id = template.Id,
                    Name = template.Name,
                    TraderId = template.TraderId,
                    Side = "Pmc",
                    Type = "",
                    Level = template.Level,
                    LocationId = template.LocationId,
                    Prerequisites = BuildFallbackPrerequisites(template),
                    Objectives = BuildFallbackObjectives(template),
                    // The client has no reward data for a quest in a form worth showing, and this
                    // path is the degraded one by definition - the companion mod supplies rewards.
                    Rewards = new List<RewardDto>()
                });
            }

            return dtos;
        }

        private static List<PrerequisiteDto> BuildFallbackPrerequisites(QuestTemplate template)
        {
            var prerequisites = new List<PrerequisiteDto>();

            // A malformed/modded quest's Conditions dict must not be able to abort the whole build.
            if (template.Conditions == null) return prerequisites;
            if (!template.Conditions.TryGetValue(EQuestStatus.AvailableForStart, out var conditions)) return prerequisites;

            foreach (var condition in conditions.OfType<ConditionQuest>())
            {
                if (string.IsNullOrEmpty(condition.target)) continue;
                prerequisites.Add(new PrerequisiteDto { Target = condition.target, Status = new List<string>() });
            }

            return prerequisites;
        }

        private static List<ObjectiveDto> BuildFallbackObjectives(QuestTemplate template)
        {
            var objectives = new List<ObjectiveDto>();

            if (template.Conditions == null) return objectives;
            if (!template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions)) return objectives;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;

                objectives.Add(new ObjectiveDto
                {
                    Id = condition.id,
                    Text = condition.FormattedDescription,
                    IsNecessary = condition.IsNecessary
                });
            }

            return objectives;
        }

        /// <summary>Finds a live Quest instance for every quest the game has actually materialized.
        /// Re-run from RefreshStatuses too, not just Build - a quest that just got unlocked
        /// mid-session gets its first-ever live instance picked up the same way a status change on
        /// an already-known one does.</summary>
        private void LinkLiveQuests()
        {
            if (_questController?.Quests == null) return;

            foreach (var quest in _questController.Quests)
            {
                if (quest?.Template?.Id == null) continue;
                if (_byId.TryGetValue(quest.Template.Id, out var node))
                    node.LiveQuest = quest;
            }
        }

        /// <summary>Quest ids the server read out of Collector's own start conditions - the list
        /// the Kappa tab shows. Until 1.8.0 the nodes were badged only from kappa-quests.json,
        /// which ships empty, so on a default install no box or detail ever said "Kappa".</summary>
        private readonly HashSet<string> _serverKappaIds = new(StringComparer.Ordinal);

        /// <summary>Takes the server's Kappa list and re-badges the nodes from it.</summary>
        public void ApplyServerKappaIds(IEnumerable<string> ids)
        {
            _serverKappaIds.Clear();
            if (ids != null)
                foreach (var id in ids)
                    if (!string.IsNullOrEmpty(id)) _serverKappaIds.Add(id);

            RefreshKappaFlags();
        }

        /// <summary>The one rule for the badge: the player's own list by name while
        /// kappa-quests.json has any (a name there is "track this instead", as the Kappa tab
        /// already reads it), else the server's list by id. Re-run after a Reload in Settings and
        /// when the server list arrives, since the flag is baked into each node at build time
        /// rather than looked up per draw.</summary>
        public void RefreshKappaFlags()
        {
            var ownList = KappaQuests.Count > 0;

            foreach (var node in _byId.Values)
                node.IsKappaRequired = ownList
                    ? KappaQuests.IsKappaRequired(node.Name)
                    : _serverKappaIds.Contains(node.Id);
        }

        /// <summary>Recolors every node from its live Quest.QuestStatus (or Locked, if the game
        /// hasn't unlocked it yet) without touching edges or depth. Call this from
        /// QuestController.OnConditionalStatusChanged.</summary>
        public void RefreshStatuses()
        {
            LinkLiveQuests();
            RefreshStatusesInternal();
        }

        private void RefreshStatusesInternal()
        {
            foreach (var node in _byId.Values)
                node.Status = node.LiveQuest != null ? ToNodeStatus(node.LiveQuest.QuestStatus) : ENodeStatus.Locked;
        }

        /// <summary>
        /// The longest prerequisite chain under every quest, in one O(N + E) pass with an explicit
        /// stack. The recursive form it replaces went one frame deeper per link - fine for the
        /// game's forty-deep chains, not for what a quest mod can produce - and its cycle guard
        /// wrote a provisional 0 into the memo that other branches read before the real value
        /// landed, so a cyclic cluster's depths depended on enumeration order.
        ///
        /// Here a node is finished only after every prerequisite is. A prerequisite still on the
        /// stack is a cycle: it contributes nothing to the node that met it, which is the one rule
        /// applied consistently to every member. A prerequisite outside the loaded set is skipped,
        /// as before, so such a quest draws as a root rather than not at all.
        /// </summary>
        private static Dictionary<string, int> ComputeDepths(Dictionary<string, QuestNode> byId)
        {
            var depth = new Dictionary<string, int>(byId.Count);
            var onStack = new HashSet<string>();
            var stack = new Stack<(QuestNode Node, int Next)>();

            foreach (var root in byId.Values)
            {
                if (depth.ContainsKey(root.Id)) continue;

                stack.Push((root, 0));
                onStack.Add(root.Id);

                while (stack.Count > 0)
                {
                    var (node, next) = stack.Pop();

                    if (next < node.PrerequisiteIds.Count)
                    {
                        // Come back to this node after the prerequisite below it is done.
                        stack.Push((node, next + 1));

                        var prereqId = node.PrerequisiteIds[next];
                        if (depth.ContainsKey(prereqId) || onStack.Contains(prereqId)) continue;
                        if (!byId.TryGetValue(prereqId, out var prereq)) continue;

                        stack.Push((prereq, 0));
                        onStack.Add(prereq.Id);
                        continue;
                    }

                    var max = -1;
                    foreach (var prereqId in node.PrerequisiteIds)
                        if (depth.TryGetValue(prereqId, out var d)) max = Math.Max(max, d);

                    depth[node.Id] = max + 1;
                    onStack.Remove(node.Id);
                }
            }

            return depth;
        }

        private static ENodeStatus ToNodeStatus(EQuestStatus status) => status switch
        {
            EQuestStatus.Success => ENodeStatus.Completed,
            EQuestStatus.Started => ENodeStatus.Active,
            EQuestStatus.AvailableForFinish => ENodeStatus.Active,
            EQuestStatus.AvailableForStart => ENodeStatus.Available,
            _ => ENodeStatus.Locked // Locked, AvailableAfter, Fail, FailRestartable, MarkedAsFailed, Expired
        };
    }
}
