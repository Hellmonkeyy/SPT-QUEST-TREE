using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// "What must I not sell?" - every item a quest will ask for, what you are holding, and which
    /// quest wants it.
    ///
    /// This is the most practical thing the mod can tell you: quest items are unremarkable junk
    /// until the quest that needs them unlocks, and vendoring one costs a raid to replace. The data
    /// was already there - objectives carry their item templates and counts, and the server counts
    /// the stash for the Kappa checklist - so this is mostly assembly.
    ///
    /// Found-in-raid is tracked separately throughout, because most hand-ins only accept found-in-
    /// raid copies and conflating them would report progress the player does not have.
    /// </summary>
    internal static class ItemWatchlistView
    {
        /// <summary>Currency is excluded. Roubles alone are asked for by 52 quests here, and "do not
        /// sell your roubles" is not advice - the list is about items you would otherwise vendor
        /// without realising a quest wanted them. Verified against the objective text ("Hand over
        /// RUB"/"EUR") rather than assumed from the ids.</summary>
        private static readonly HashSet<string> Currency = new()
        {
            "5449016a4bdc2d6f028b456f", // Roubles
            "5696686a4bdc2d88308b456a", // Dollars
            "569668774bdc2da2298b4568"  // Euros
        };

        /// <summary>Most rows built at once. Every quest here wants something and none are complete,
        /// which yields ~930 distinct items - and unlike the graph, this view builds every row
        /// eagerly. The ordering below puts what you still need first, so the cap trims the tail.</summary>
        private const int MaxRows = 150;

        /// <summary>One item, aggregated across every quest that wants it.</summary>
        internal sealed class WatchedItem
        {
            public string Template = "";
            public string Name = "";
            public int Required;
            public int OwnedFoundInRaid;
            public int OwnedTotal;
            public bool NeedsFoundInRaid;
            public readonly List<string> Quests = new();

            /// <summary>The same quests by id, so a row can open one.</summary>
            public readonly List<string> QuestIds = new();

            /// <summary>Sort key: things you still need, and are closest to having, first.</summary>
            public int Outstanding => Mathf.Max(0, Required - (NeedsFoundInRaid ? OwnedFoundInRaid : OwnedTotal));
        }

        public static float Build(
            RectTransform parent, QuestGraphBuilder graph, Vector2 panelSize, Action<QuestNode> onQuestSelected,
            Action onRefresh)
        {
            var x = AuxLayout.Padding;
            var width = Mathf.Min(AuxLayout.MaxContentWidth, panelSize.x - AuxLayout.Padding * 2f);
            var y = AuxLayout.Padding;

            var profile = QuestDataClient.GetProfile();

            if (profile == null || !profile.HasProfile)
            {
                AuxLayout.AddSectionHeader(parent, ref y, "Quest items", x, width);
                DoNextView.RefreshLink(parent, y, x, width, onRefresh);
                AuxLayout.AddWrapped(parent,
                    $"<color=#{GameStyle.ErrorHex}>Needs the server half of the mod.</color> It reads which items your " +
                    "quests want and what is in your stash - the client cannot see either.", x, ref y, width);
                return y + AuxLayout.Padding;
            }

            var items = Collect(graph, profile);

            if (items.Count == 0)
            {
                AuxLayout.AddSectionHeader(parent, ref y, "Quest items", x, width);
                DoNextView.RefreshLink(parent, y, x, width, onRefresh);
                AuxLayout.AddWrapped(parent,
                    "<color=#FFFFFF80>No outstanding quest items - nothing you are carrying is spoken for.</color>",
                    x, ref y, width);
                return y + AuxLayout.Padding;
            }

            var needed = items.Count(i => i.Outstanding > 0);
            AuxLayout.AddSectionHeader(parent, ref y, $"Quest items  ·  {items.Count - needed} / {items.Count} covered", x, width);
            DoNextView.RefreshLink(parent, y, x, width, onRefresh);
            AuxLayout.AddLabelAt(parent,
                "<color=#FFFFFF80>Items your unfinished quests will ask for. Do not sell these. Click one to open its quest.</color>",
                x, ref y, 20f, 11, width);
            AuxLayout.AddSpacer(ref y, 6f);

            // Still-needed first, then closest to done, so the top of the list is what to look for
            // on the next raid - and so the cap below trims the least useful end.
            var ordered = items
                .OrderByDescending(i => i.Outstanding > 0)
                .ThenBy(i => i.Outstanding)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ordered.Count > MaxRows)
            {
                AuxLayout.AddLabelAt(parent,
                    $"<color=#{GameStyle.WarningHex}>Showing the first {MaxRows} of {ordered.Count} - the rest are items you already have covered or quests far off.</color>",
                    x, ref y, 20f, 11, width);
                ordered = ordered.Take(MaxRows).ToList();
            }

            foreach (var item in ordered)
            {
                var firstId = item.QuestIds.Count > 0 ? item.QuestIds[0] : null;
                var target = firstId != null && graph.NodesById.TryGetValue(firstId, out var node) ? node : null;

                AuxLayout.AddClickableRow(parent, Format(item), x, ref y, width, false,
                    () => { if (target != null) onQuestSelected?.Invoke(target); });

                // Naming the quest is what makes the line actionable rather than a shopping list.
                var wanted = item.Quests.Count <= 2
                    ? string.Join(", ", item.Quests)
                    : $"{item.Quests[0]}, {item.Quests[1]} +{item.Quests.Count - 2} more";
                AuxLayout.AddLabelAt(parent, $"<color=#FFFFFF60>{wanted}</color>", x + 22f, ref y, 16f, 10, width - 22f);
            }

            return y + AuxLayout.Padding;
        }

        /// <summary>
        /// Aggregates item requirements across every quest that is not already finished.
        ///
        /// Completed quests are skipped - their items are spent. Everything else counts, including
        /// quests that are still locked: knowing an item matters BEFORE the quest unlocks is the
        /// entire point, since that is when you would otherwise sell it.
        /// </summary>
        internal static List<WatchedItem> Collect(QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            var byTemplate = new Dictionary<string, WatchedItem>();
            var owned = profile.ItemsOwned ?? new Dictionary<string, HeldItemDto>();

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;
                if (node.Dto?.Objectives == null) continue;

                foreach (var objective in node.Dto.Objectives)
                {
                    if (objective?.TargetItems == null || objective.TargetItems.Count == 0) continue;

                    // A condition can accept any one of several templates; the first is the one
                    // worth naming, and counting each alternative separately would inflate the list.
                    var template = objective.TargetItems[0];
                    if (string.IsNullOrWhiteSpace(template)) continue;
                    if (Currency.Contains(template)) continue;

                    if (!byTemplate.TryGetValue(template, out var watched))
                    {
                        owned.TryGetValue(template, out var held);

                        watched = new WatchedItem
                        {
                            Template = template,
                            Name = QuestSummary.ItemName(objective, 0),
                            OwnedFoundInRaid = held?.FoundInRaid ?? 0,
                            OwnedTotal = held?.Total ?? 0
                        };

                        byTemplate[template] = watched;
                    }

                    watched.Required += Mathf.Max(1, objective.Count);

                    // Accumulated like Required, not fixed by whichever quest came first: an item
                    // one quest takes plain and another wants found-in-raid showed a green [have]
                    // for stock the second quest would reject.
                    // The condition's own flag when the server sends one (schema v2); the
                    // sentence as the fallback for an older server.
                    watched.NeedsFoundInRaid |= objective.FoundInRaid || QuestSummary.MentionsFoundInRaid(objective.Text);

                    if (!watched.Quests.Contains(node.Name))
                    {
                        watched.Quests.Add(node.Name);
                        watched.QuestIds.Add(node.Id);
                    }
                }
            }

            return byTemplate.Values.ToList();
        }

        internal static string Format(WatchedItem item)
        {
            var held = item.NeedsFoundInRaid ? item.OwnedFoundInRaid : item.OwnedTotal;
            var fir = item.NeedsFoundInRaid ? " <color=#FFFFFF60>(FiR)</color>" : "";

            if (held >= item.Required)
                return $"<color=#{QuestNodeView.HexFor(ENodeStatus.Completed)}>[have]</color>  {GameStyle.Safe(item.Name)}  {held}/{item.Required}{fir}";

            // Held but not found-in-raid is its own state: you own the thing and it still will not
            // count, which is exactly the case someone would otherwise get wrong.
            if (item.NeedsFoundInRaid && item.OwnedTotal > 0)
                return $"<color=#{GameStyle.WarningHex}>[not FiR]</color>  {GameStyle.Safe(item.Name)}  {held}/{item.Required}" +
                       $"  <color=#FFFFFF60>{item.OwnedTotal} held, not found in raid</color>";

            return $"<color=#FFFFFF40>[need]</color>  {GameStyle.Safe(item.Name)}  {held}/{item.Required}{fir}";
        }
    }
}
