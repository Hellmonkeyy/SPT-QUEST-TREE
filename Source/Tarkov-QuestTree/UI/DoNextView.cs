using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// "What should I do next?" - the screen that turns the mod from a reference into something you
    /// act on.
    ///
    /// Deliberately RANKED, not filtered. The obvious design is to list quests whose gates are all
    /// met, but that collapses on a profile where everything already reads as available - and this
    /// mod is meant to be shared, so it has to cope with profiles it did not create. Ranking
    /// degrades gracefully instead: on a normal profile the top of the list is "what can I start",
    /// on an unlocked one it is "what am I closest to finishing". Same screen, useful either way.
    ///
    /// The order is the order you would act on things:
    ///   1. in progress - already accepted, finish them
    ///   2. ready to hand in - every item held, found-in-raid where the objective demands it
    ///   3. partly held - closest to complete first
    ///   4. everything else - nearest gate first
    ///
    /// Every row opens the quest: its detail, and the tree framed on it.
    /// </summary>
    internal static class DoNextView
    {
        private const int MaxRows = 40;

        internal enum Bucket
        {
            InProgress = 0,
            ReadyToHandIn = 1,
            PartlyHeld = 2,
            Other = 3
        }

        internal sealed class Ranked
        {
            public QuestNode Node;
            public Bucket Bucket;

            /// <summary>How much of the item requirement is already met, 0-1. Only meaningful for
            /// the item buckets; used to put "one more" above "none of six".</summary>
            public float ItemProgress;

            /// <summary>How far the nearest gate is, for the Other bucket - levels short, or 0 when
            /// nothing numeric is known.</summary>
            public int GateDistance;
        }

        public static float Build(
            RectTransform parent, QuestGraphBuilder graph, Vector2 panelSize, Action<QuestNode> onQuestSelected,
            Action onRefresh)
        {
            var x = AuxLayout.Padding;
            var width = Mathf.Min(AuxLayout.MaxContentWidth, panelSize.x - AuxLayout.Padding * 2f);
            var y = AuxLayout.Padding;

            var profile = QuestDataClient.GetProfile();
            var ranked = Rank(graph, profile);

            AuxLayout.AddSectionHeader(parent, ref y, "Do next", x, width);
            RefreshLink(parent, y, x, width, onRefresh);

            if (ranked.Count == 0)
            {
                AuxLayout.AddWrapped(parent, "<color=#FFFFFF80>Nothing outstanding - every quest is complete.</color>", x, ref y, width);
                return y + AuxLayout.Padding;
            }

            if (profile == null || !profile.HasProfile)
            {
                AuxLayout.AddWrapped(parent,
                    $"<color=#{GameStyle.WarningHex}>Without the server half this can only order by quest status - it cannot see your stash or your level.</color>",
                    x, ref y, width, 11);
            }

            var shown = ranked.Take(MaxRows).ToList();
            var lastBucket = (Bucket?)null;

            foreach (var entry in shown)
            {
                // A heading per group, so the list explains its own ordering rather than looking
                // arbitrary.
                if (lastBucket != entry.Bucket)
                {
                    AuxLayout.AddSpacer(ref y, lastBucket == null ? 4f : 12f);
                    AuxLayout.AddSectionHeader(parent, ref y, BucketHeading(entry.Bucket), x, width);
                    lastBucket = entry.Bucket;
                }

                var node = entry.Node;
                var hex = QuestNodeView.HexFor(node.Status);
                var captured = node;

                AuxLayout.AddClickableRow(parent,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {GameStyle.Safe(node.Name)}  <color=#FFFFFF60>{GameStyle.Safe(node.TraderName)}</color>",
                    x, ref y, width, false, () => onQuestSelected?.Invoke(captured));

                var reason = Reason(entry, profile, graph);
                if (!string.IsNullOrEmpty(reason))
                    AuxLayout.AddLabelAt(parent, $"<color=#FFFFFF60>{reason}</color>", x + 22f, ref y, 16f, 11, width - 22f);
            }

            if (ranked.Count > shown.Count)
            {
                AuxLayout.AddSpacer(ref y, 6f);
                AuxLayout.AddLabelAt(parent, $"<color=#FFFFFF60>+{ranked.Count - shown.Count} more outstanding quests</color>",
                    x, ref y, AuxLayout.RowHeight, 11, width);
            }

            return y + AuxLayout.Padding;
        }

        /// <summary>The "Refresh from server" link, at the right end of the first header line. The
        /// checklists reflect your stash and the mod deliberately does not poll for that.</summary>
        internal static void RefreshLink(RectTransform parent, float headerY, float x, float width, Action onRefresh)
        {
            const float linkWidth = 150f;
            var linkY = headerY - 30f;
            AuxLayout.AddClickableRow(parent, "<color=#FFFFFF80>Refresh from server  ⟳</color>",
                x + width - linkWidth, ref linkY, linkWidth, false, onRefresh, 20f);
        }

        private static string BucketHeading(Bucket bucket) => bucket switch
        {
            Bucket.InProgress => "In progress",
            Bucket.ReadyToHandIn => "Ready to hand in",
            Bucket.PartlyHeld => "You already have some of the items",
            _ => "Everything else"
        };

        /// <summary>The one extra fact worth showing per row - what is left, or what is blocking.
        /// Shared with the map's sidebar, which ranks the same way for one map.</summary>
        internal static string Reason(Ranked entry, ProfilePayloadDto profile, QuestGraphBuilder graph) =>
            Detail(entry, profile, graph).TrimStart(' ', '·');

        private static string Detail(Ranked entry, ProfilePayloadDto profile, QuestGraphBuilder graph)
        {
            // A quest already accepted has no lock reason, so it fell through to "level 12" -
            // the requirement of a quest you are already doing. What it is is objective progress.
            if (entry.Bucket == Bucket.InProgress)
                return $"  ·  {QuestSummary.ObjectiveProgress(entry.Node, profile)}";

            if (entry.Bucket == Bucket.ReadyToHandIn) return "  ·  all items held";

            if (entry.Bucket == Bucket.PartlyHeld)
                return $"  ·  {Mathf.RoundToInt(entry.ItemProgress * 100f)}% of items";

            // The same wording as the detail panel: trader named, "(you are N)" added,
            // prerequisites resolved to quest names.
            var locked = QuestSummary.LockReasonDetail(entry.Node, graph, profile);
            if (locked != null) return $"  ·  {locked}";

            if (entry.Node.Level > 0) return $"  ·  level {entry.Node.Level}";
            return "";
        }

        /// <summary>The last ranking and what it was made from. The map sidebar asks for this on
        /// every repaint - every pin click - to show eight rows, and ranking walks every objective
        /// of every quest; the answer only changes when the graph, its statuses or the profile do.
        /// Static because the aux views are rebuilt per repaint; dropped by Forget when the graph
        /// is rebuilt so it never keeps an old graph's nodes alive.</summary>
        private static QuestGraphBuilder _rankedGraph;
        private static int _rankedVersion;
        private static ProfilePayloadDto _rankedProfile;
        private static List<Ranked> _ranked;

        internal static List<Ranked> Rank(QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            if (_ranked != null && ReferenceEquals(graph, _rankedGraph) && graph.Version == _rankedVersion &&
                ReferenceEquals(profile, _rankedProfile))
            {
                return _ranked;
            }

            var ranked = RankUncached(graph, profile);
            _rankedGraph = graph;
            _rankedVersion = graph.Version;
            _rankedProfile = profile;
            _ranked = ranked;
            return ranked;
        }

        /// <summary>Drops the cached ranking - called when the graph is rebuilt.</summary>
        internal static void Forget()
        {
            _rankedGraph = null;
            _rankedProfile = null;
            _ranked = null;
        }

        private static List<Ranked> RankUncached(QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            var owned = profile?.ItemsOwned ?? new Dictionary<string, HeldItemDto>();
            var playerLevel = profile?.Level ?? 0;
            var ranked = new List<Ranked>();

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;

                var entry = new Ranked { Node = node };

                if (node.Status == ENodeStatus.Active)
                {
                    entry.Bucket = Bucket.InProgress;
                }
                else
                {
                    var progress = ItemProgress(node, owned);
                    entry.ItemProgress = progress;

                    // Only quests that actually want items can be "ready" - a quest with no item
                    // requirement scores 1.0 vacuously and would otherwise flood the top.
                    if (progress >= 1f && WantsItems(node)) entry.Bucket = Bucket.ReadyToHandIn;
                    else if (progress > 0f) entry.Bucket = Bucket.PartlyHeld;
                    else entry.Bucket = Bucket.Other;
                }

                entry.GateDistance = GateDistance(node, playerLevel);
                ranked.Add(entry);
            }

            return ranked
                .OrderBy(e => (int)e.Bucket)
                .ThenByDescending(e => e.ItemProgress)
                .ThenBy(e => e.GateDistance)
                .ThenBy(e => e.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool WantsItems(QuestNode node) =>
            node.Dto?.Objectives != null &&
            node.Dto.Objectives.Any(o => o?.TargetItems != null && o.TargetItems.Count > 0);

        private static float ItemProgress(QuestNode node, Dictionary<string, HeldItemDto> owned)
        {
            if (node.Dto?.Objectives == null) return 0f;

            var required = 0;
            var held = 0;

            foreach (var objective in node.Dto.Objectives)
            {
                if (objective?.TargetItems == null || objective.TargetItems.Count == 0) continue;

                var template = objective.TargetItems[0];
                if (string.IsNullOrWhiteSpace(template)) continue;

                var need = Mathf.Max(1, objective.Count);
                required += need;

                if (!owned.TryGetValue(template, out var have) || have == null) continue;

                // The condition's own flag when the server is new enough to send it; the sentence
                // as the fallback for an older server and for wordings the sentence test misses.
                var usable = objective.FoundInRaid || QuestSummary.MentionsFoundInRaid(objective.Text) ? have.FoundInRaid : have.Total;
                held += Mathf.Min(need, usable);
            }

            return required == 0 ? 0f : (float)held / required;
        }

        private static int GateDistance(QuestNode node, int playerLevel)
        {
            if (node.Level <= 0 || playerLevel <= 0) return 0;
            return Mathf.Max(0, node.Level - playerLevel);
        }
    }
}
