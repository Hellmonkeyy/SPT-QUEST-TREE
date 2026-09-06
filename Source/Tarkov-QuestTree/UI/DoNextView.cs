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
    /// </summary>
    internal static class DoNextView
    {
        private const int MaxRows = 40;

        private enum Bucket
        {
            InProgress = 0,
            ReadyToHandIn = 1,
            PartlyHeld = 2,
            Other = 3
        }

        private sealed class Ranked
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

        public static float Build(RectTransform parent, QuestGraphBuilder graph, Action onRefresh)
        {
            var y = AuxLayout.Padding;

            AuxLayout.AddButton(parent, ref y, "Refresh from server", onRefresh);
            AuxLayout.AddSpacer(ref y, 8f);

            var profile = QuestDataClient.GetProfile();
            var ranked = Rank(graph, profile);

            if (ranked.Count == 0)
            {
                AuxLayout.AddHeading(parent, ref y, "Do next");
                AuxLayout.AddText(parent, ref y,
                    "<color=#FFFFFF80>Nothing outstanding - every quest is complete.</color>", 24f, 12);
                return y + AuxLayout.Padding;
            }

            AuxLayout.AddHeading(parent, ref y, "Do next");

            if (profile == null || !profile.HasProfile)
            {
                AuxLayout.AddText(parent, ref y,
                    "<color=#D9A61A>Without the server half this can only order by quest status - " +
                    "it cannot see your stash or your level.</color>", 32f, 11);
            }

            var shown = ranked.Take(MaxRows).ToList();
            var lastBucket = (Bucket?)null;

            foreach (var entry in shown)
            {
                // A heading per group, so the list explains its own ordering rather than looking
                // arbitrary.
                if (lastBucket != entry.Bucket)
                {
                    AuxLayout.AddSpacer(ref y, 8f);
                    AuxLayout.AddHeading(parent, ref y, BucketHeading(entry.Bucket));
                    lastBucket = entry.Bucket;
                }

                var node = entry.Node;
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(node.Status));

                AuxLayout.AddText(parent, ref y,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {node.Name}" +
                    $"  <color=#FFFFFF60>{node.TraderName}{Detail(entry, profile)}</color>",
                    AuxLayout.RowHeight, 12, indent: 6f);
            }

            if (ranked.Count > shown.Count)
            {
                AuxLayout.AddSpacer(ref y, 6f);
                AuxLayout.AddText(parent, ref y,
                    $"<color=#FFFFFF60>+{ranked.Count - shown.Count} more outstanding quests</color>",
                    AuxLayout.RowHeight, 11, indent: 6f);
            }

            return y + AuxLayout.Padding;
        }

        private static string BucketHeading(Bucket bucket) => bucket switch
        {
            Bucket.InProgress => "In progress",
            Bucket.ReadyToHandIn => "Ready to hand in",
            Bucket.PartlyHeld => "You already have some of the items",
            _ => "Everything else"
        };

        /// <summary>The one extra fact worth showing per row - what is left, or what is blocking.</summary>
        private static string Detail(Ranked entry, ProfilePayloadDto profile)
        {
            if (entry.Bucket == Bucket.ReadyToHandIn) return "  ·  all items held";

            if (entry.Bucket == Bucket.PartlyHeld)
                return $"  ·  {Mathf.RoundToInt(entry.ItemProgress * 100f)}% of items";

            if (profile?.LockReasons != null &&
                profile.LockReasons.TryGetValue(entry.Node.Id, out var reason) &&
                reason != null)
            {
                return $"  ·  {reason.Detail}";
            }

            if (entry.Node.Level > 0) return $"  ·  level {entry.Node.Level}";

            return "";
        }

        private static List<Ranked> Rank(QuestGraphBuilder graph, ProfilePayloadDto profile)
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

        /// <summary>How much of this quest's item requirement the stash already covers, 0-1.
        /// Found-in-raid is respected where the objective asks for it, since a bought copy will not
        /// be accepted and counting it would put the quest in the wrong bucket entirely.</summary>
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

                var usable = MentionsFoundInRaid(objective.Text) ? have.FoundInRaid : have.Total;
                held += Mathf.Min(need, usable);
            }

            return required == 0 ? 0f : (float)held / required;
        }

        private static bool MentionsFoundInRaid(string text) =>
            !string.IsNullOrEmpty(text) &&
            text.IndexOf("found in raid", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Levels short of the requirement, so "two levels away" sorts above "thirty".
        /// Zero when the gate is not a level or is already met.</summary>
        private static int GateDistance(QuestNode node, int playerLevel)
        {
            if (node.Level <= 0 || playerLevel <= 0) return 0;
            return Mathf.Max(0, node.Level - playerLevel);
        }
    }
}
