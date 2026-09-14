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
    /// Quests you have already accepted come first, scored among themselves; then everything else
    /// by score. Within each, the order is what a SCORE says rather than what a tier says - rewards,
    /// how much of the tree the quest opens, how close it is to done, how close it is to startable,
    /// whether Kappa needs it, and how much work it looks like, weighted by the goal you picked.
    ///
    /// The tiers this replaced were not really an ordering. On a real profile with seventy-nine
    /// accepted quests, "how much of the items you hold" and "how many levels short" were both zero
    /// for nearly all of them, so the only live sort key left was the quest's NAME - and the tab
    /// listed forty in-progress quests alphabetically while calling itself a ranking.
    ///
    /// Every row says why it is where it is. A ranking has no ground truth to test against, so the
    /// check is whether a row's stated reason justifies its position; if it does not, the weights
    /// are wrong and you can see it.
    ///
    /// Every row opens the quest: its detail, and the tree framed on it.
    /// </summary>
    internal static class DoNextView
    {
        internal enum Bucket
        {
            /// <summary>Already accepted. Always above everything else, and scored among themselves:
            /// you have committed to these, and a started quest needing one more kill should not sit
            /// below an unstarted one that theoretically pays better.</summary>
            InProgress = 0,

            /// <summary>Everything not yet accepted, in score order. The old ReadyToHandIn and
            /// PartlyHeld tiers are gone - how much of a quest's items you hold is now one component
            /// of the score rather than a tier outranking every other consideration.</summary>
            Other = 1
        }

        internal sealed class Ranked
        {
            public QuestNode Node;
            public Bucket Bucket;

            /// <summary>The score under the current goal, and the components that made it.
            ///
            /// Kept whole rather than flattened to a number, because a row has to be able to explain
            /// its own position. That is the only check available on a ranking - there is no ground
            /// truth for "the best quest to do next", so the test is whether the reason a row gives
            /// justifies where it sits.</summary>
            public QuestScore.Breakdown Score;
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

            // The goal sits above the list because it changes the whole list. Its header is drawn
            // in place and the open rows are deferred to the very end of the page, which is the
            // same ordering SettingsView uses - an open list drawn mid-page is painted over by
            // everything stacked after it.
            var goalTop = y;
            y += AuxLayout.AddDropdownHeader(parent, goalTop, GoalLabels, (int)Goal(), _goalOpen,
                () => { _goalOpen = !_goalOpen; ModSettings.RequestRepaint(); }, 220f, x);
            AuxLayout.AddSpacer(ref y, 6f);

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

            // One line, not sections. Travel between maps is the real cost in Tarkov, so knowing
            // that four of your top ten are in one place is worth saying - but a single high-value
            // quest on an unpopular map must not be buried under a heading to say it.
            var batch = Batch(shown);
            if (!string.IsNullOrEmpty(batch))
                AuxLayout.AddWrapped(parent, $"<color=#FFFFFF80>{batch}</color>", x, ref y, width, 11);

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

                var where = MapLabel(node);
                var tail = string.IsNullOrEmpty(where)
                    ? GameStyle.Safe(node.TraderName)
                    : $"{GameStyle.Safe(node.TraderName)}  ·  {GameStyle.Safe(where)}";

                AuxLayout.AddClickableRow(parent,
                    $"<color=#{hex}>{QuestNodeView.GlyphFor(node.Status)}</color>  {GameStyle.Safe(node.Name)}  <color=#FFFFFF60>{tail}</color>",
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

            var bottom = y + AuxLayout.Padding;

            if (_goalOpen)
            {
                var listBottom = AuxLayout.AddDropdownList(parent, goalTop, GoalLabels, (int)Goal(),
                    index =>
                    {
                        _goalOpen = false;
                        if (ModSettings.Ready) ModSettings.DoNextGoal.Value = (RankGoal)index;
                        else ModSettings.RequestRepaint();
                    }, 220f, x);

                bottom = Mathf.Max(bottom, listBottom + AuxLayout.Padding);
            }

            return bottom;
        }

        /// <summary>Whether the goal list is open. Static because the aux views are rebuilt from
        /// scratch on every repaint, so there is nowhere else for a moment of UI state to live -
        /// the same reason SettingsView keeps its open dropdown in a static.</summary>
        private static bool _goalOpen;

        /// <summary>In RankGoal order, so the selected index is the enum value.</summary>
        private static readonly string[] GoalLabels =
        {
            "Balanced",
            "Kappa path",
            "Fast levelling",
            "Trader unlocks",
            "Item hoarding"
        };

        /// <summary>Where a quest happens, for the row. A quest with no location of its own is
        /// placed by its objectives, and one whose objectives are scattered says so rather than
        /// picking a map arbitrarily.</summary>
        private static string MapLabel(QuestNode node)
        {
            var named = node.Dto?.LocationId;
            if (!string.IsNullOrWhiteSpace(named) && !named.Equals("any", StringComparison.OrdinalIgnoreCase))
                return named;

            var derived = node.Dto?.DerivedLocations;
            if (derived == null || derived.Count == 0) return "";
            if (derived.Count == 1) return derived[0]?.Name ?? "";

            return $"{derived.Count} maps";
        }

        /// <summary>The one map several of the top rows share, when there is one. Silent below three,
        /// because two quests in a place is a coincidence rather than a plan.</summary>
        private static string Batch(List<Ranked> shown)
        {
            const int Window = 10;
            const int Worth = 3;

            var top = shown.Take(Window).ToList();
            if (top.Count < Worth) return "";

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in top)
            {
                var where = MapLabel(entry.Node);
                if (string.IsNullOrEmpty(where) || where.EndsWith("maps", StringComparison.OrdinalIgnoreCase)) continue;

                counts.TryGetValue(where, out var count);
                counts[where] = count + 1;
            }

            if (counts.Count == 0) return "";

            var best = counts.OrderByDescending(pair => pair.Value).First();
            if (best.Value < Worth) return "";

            return $"{best.Value} of your top {top.Count} are on {best.Key} - one raid clears several.";
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
            _ => "Worth starting"
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

            // Why this row is where it is, in the quest's own terms rather than as a number. A
            // score nobody can check is a score nobody should trust, and "0.62" checks nothing.
            var why = Why(entry, profile, graph);
            if (!string.IsNullOrEmpty(why)) return $"  ·  {why}";

            // The same wording as the detail panel: trader named, "(you are N)" added,
            // prerequisites resolved to quest names.
            var locked = QuestSummary.LockReasonDetail(entry.Node, graph, profile);
            if (locked != null) return $"  ·  {locked}";

            if (entry.Node.Level > 0) return $"  ·  level {entry.Node.Level}";

            // An available quest is the one row in a ranked "do this next" list that used to
            // explain nothing, sitting beside rows that all do - and its next action is the least
            // ambiguous of any of them.
            if (entry.Node.Status == ENodeStatus.Available)
            {
                var trader = graph != null && !string.IsNullOrEmpty(entry.Node.TraderId) &&
                             graph.TraderNames.TryGetValue(entry.Node.TraderId, out var name) &&
                             !string.IsNullOrEmpty(name)
                    ? name
                    : null;

                return trader == null
                    ? "  ·  not accepted - take it from its trader first"
                    : $"  ·  not accepted - take it from {trader} first";
            }

            return "";
        }

        /// <summary>The two or three biggest reasons this quest scored what it did, phrased as
        /// facts about the quest.
        ///
        /// A component is only named when it actually carried weight, so a quest that ranks on
        /// unlocks says "unlocks 12" and one that ranks on payout says "96k XP". The gate is folded
        /// in last, because for a locked quest the gate IS the headline.</summary>
        private static string Why(Ranked entry, ProfilePayloadDto profile, QuestGraphBuilder graph)
        {
            if (entry.Score == null) return "";

            var phrases = new List<string>();

            foreach (var part in entry.Score.Top(3))
            {
                var phrase = Phrase(part, entry.Node, profile, graph);
                if (!string.IsNullOrEmpty(phrase) && !phrases.Contains(phrase)) phrases.Add(phrase);
            }

            return phrases.Count == 0 ? "" : string.Join("  ·  ", phrases);
        }

        /// <summary>One component turned into something a player can act on.</summary>
        private static string Phrase(QuestScore.Part part, QuestNode node, ProfilePayloadDto profile,
            QuestGraphBuilder graph)
        {
            switch (part.Label)
            {
                case "unlocks":
                    return node.UnlockReach > 0 ? $"unlocks {node.UnlockReach}" : "";

                case "kappa":
                    return node.IsKappaRequired ? "Kappa" : node.IsCollectorPrerequisite ? "Collector" : "";

                case "reward":
                    return RewardPhrase(node);

                case "trader":
                    return TraderPhrase(node);

                case "ready":
                    return part.Score >= 0.999f ? "everything held"
                        : part.Score > 0f ? $"{Mathf.RoundToInt(part.Score * 100f)}% ready"
                        : "";

                case "quick":
                    return part.Score >= 0.8f ? "quick" : "";

                default:
                    return "";
            }
        }

        /// <summary>The biggest single number the quest pays, which is what a player compares.</summary>
        private static string RewardPhrase(QuestNode node)
        {
            var rewards = node.Dto?.Rewards;
            if (rewards == null) return "";

            var experience = 0d;
            var roubles = 0L;

            foreach (var reward in rewards)
            {
                if (reward == null) continue;
                if (reward.Type == "Experience") experience += reward.Value;
                else if (reward.Type == "Item" && reward.RoubleValue.HasValue) roubles += reward.RoubleValue.Value;
            }

            if (experience >= 1000d) return $"{experience / 1000d:0.#}k XP";
            if (experience > 0d) return $"{experience:0} XP";
            if (roubles >= 1000L) return $"{roubles / 1000d:0.#}k ₽";

            return "";
        }

        private static string TraderPhrase(QuestNode node)
        {
            var rewards = node.Dto?.Rewards;
            if (rewards == null) return "";

            if (rewards.Any(r => r != null && r.Type == "TraderUnlock")) return "new trader";

            var offers = rewards.Count(r => r != null && r.Type == "AssortmentUnlock");
            if (offers == 1) return "new offer";
            if (offers > 1) return $"{offers} new offers";

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
        private static RankGoal _rankedGoal;
        private static List<Ranked> _ranked;

        internal static List<Ranked> Rank(QuestGraphBuilder graph, ProfilePayloadDto profile)
        {
            // The goal is part of the key, and leaving it out is the one mistake that would make
            // this feature look broken on first use: changing the goal changes no graph version and
            // no profile reference, so the cache would hand back the previous order and the list
            // would sit there refusing to move.
            var goal = Goal();

            if (_ranked != null && ReferenceEquals(graph, _rankedGraph) && graph.Version == _rankedVersion &&
                ReferenceEquals(profile, _rankedProfile) && goal == _rankedGoal)
            {
                return _ranked;
            }

            var ranked = RankUncached(graph, profile);
            _rankedGraph = graph;
            _rankedVersion = graph.Version;
            _rankedProfile = profile;
            _rankedGoal = goal;
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
            var goal = Goal();
            var ranked = new List<Ranked>();

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;

                ranked.Add(new Ranked
                {
                    Node = node,
                    Bucket = node.Status == ENodeStatus.Active ? Bucket.InProgress : Bucket.Other,
                    Score = QuestScore.Of(node, profile, graph, goal)
                });
            }

            // Name is the last tiebreak only. It used to be the first LIVE one: on a real profile
            // with seventy-nine accepted quests, item progress and gate distance were both zero for
            // nearly all of them, so the tab listed forty in-progress quests in alphabetical order
            // and called it a ranking.
            return ranked
                .OrderBy(e => (int)e.Bucket)
                .ThenByDescending(e => e.Score?.Total ?? 0f)
                .ThenBy(e => e.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The goal to rank for. Balanced before settings are bound, which is the first
        /// paint of a fresh install.</summary>
        internal static RankGoal Goal() =>
            ModSettings.Ready ? ModSettings.DoNextGoal.Value : RankGoal.Balanced;

        /// <summary>How many rows the tab lists before it stops and counts the rest.</summary>
        private static int MaxRows =>
            ModSettings.Ready ? ModSettings.DoNextMaxRows.Value : 40;

    }
}
