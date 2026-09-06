using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// "I'm going to Customs - what can I do there?"
    ///
    /// The quest tree answers questions about progression; this answers the question you actually
    /// have while staring at the map select screen. Quests already carry a resolved location name
    /// from the server, so this is a grouping rather than new data.
    ///
    /// Quests that can happen anywhere are deliberately left out. Listing them under every map
    /// would bury the handful that are map-specific, which are the only ones that change what you
    /// do with the raid you are about to load into.
    /// </summary>
    internal static class MapView
    {
        /// <summary>The server sends "any" untranslated for quests with no specific map.</summary>
        private const string AnyLocation = "any";

        /// <summary>Per map. Long trader chains can put dozens of quests on one map and the list is
        /// built eagerly; the ordering below puts actionable ones first so the cap trims the tail.</summary>
        private const int MaxPerMap = 25;

        public static float Build(RectTransform parent, QuestGraphBuilder graph)
        {
            var y = AuxLayout.Padding;

            var byMap = new Dictionary<string, List<QuestNode>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Nodes)
            {
                if (node.Status == ENodeStatus.Completed) continue;

                var location = node.LocationId;
                if (string.IsNullOrEmpty(location)) continue;
                if (location.Equals(AnyLocation, StringComparison.OrdinalIgnoreCase)) continue;

                if (!byMap.TryGetValue(location, out var list))
                {
                    list = new List<QuestNode>();
                    byMap[location] = list;
                }

                list.Add(node);
            }

            if (byMap.Count == 0)
            {
                AuxLayout.AddHeading(parent, ref y, "By map");
                AuxLayout.AddText(parent, ref y,
                    "<color=#FFFFFF80>No unfinished quests are tied to a specific map.</color>", 24f, 12);
                return y + AuxLayout.Padding;
            }

            AuxLayout.AddHeading(parent, ref y, "By map");
            AuxLayout.AddText(parent, ref y,
                "<color=#FFFFFF80>Unfinished quests that happen on a specific map. Quests that can be " +
                "done anywhere are left out.</color>", 32f, 11);
            AuxLayout.AddSpacer(ref y, 6f);

            // Busiest map first - that is usually the one worth loading into next.
            foreach (var (map, quests) in byMap.OrderByDescending(m => m.Value.Count))
            {
                var actionable = quests.Count(q => q.Status == ENodeStatus.Active || q.Status == ENodeStatus.Available);

                AuxLayout.AddSpacer(ref y, 8f);
                AuxLayout.AddHeading(parent, ref y,
                    actionable > 0
                        ? $"{map}      {actionable} available, {quests.Count} total"
                        : $"{map}      {quests.Count} quests");

                // In-progress first, then startable, then the rest: the order you would act on them.
                var ordered = quests
                    .OrderBy(q => StatusRank(q.Status))
                    .ThenBy(q => q.TraderName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var shown = ordered.Take(MaxPerMap).ToList();

                foreach (var node in shown)
                {
                    AuxLayout.AddText(parent, ref y,
                        $"{StatusGlyph(node.Status)}  {node.Name}  <color=#FFFFFF60>{node.TraderName}</color>",
                        AuxLayout.RowHeight, 12, indent: 6f);
                }

                if (ordered.Count > shown.Count)
                {
                    AuxLayout.AddText(parent, ref y,
                        $"<color=#FFFFFF60>+{ordered.Count - shown.Count} more</color>",
                        AuxLayout.RowHeight, 11, indent: 22f);
                }
            }

            return y + AuxLayout.Padding;
        }

        /// <summary>Sort order, not display order: in progress, then startable, then locked.</summary>
        private static int StatusRank(ENodeStatus status) => status switch
        {
            ENodeStatus.Active => 0,
            ENodeStatus.Available => 1,
            _ => 2
        };

        /// <summary>Reuses the graph's own glyphs and colours so a status means the same thing here
        /// as it does on a node.</summary>
        private static string StatusGlyph(ENodeStatus status)
        {
            var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(status));
            return $"<color=#{hex}>{QuestNodeView.GlyphFor(status)}</color>";
        }
    }
}
