using System.Collections.Generic;
using System.Linq;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// "What do I actually have to do to get to this quest?"
    ///
    /// A quest's own Requires line names only its immediate prerequisites, which on a deep chain
    /// answers almost nothing - the quest before it has its own, and so on back to a trader's
    /// opening quest. This walks the whole closure and hands back the part that is still undone,
    /// in the order it can be done.
    /// </summary>
    internal static class QuestRoute
    {
        /// <summary>
        /// Every quest the target transitively requires, completed ones included.
        ///
        /// Breadth-first, and the visited set doubles as the cycle guard: modded quest data is not
        /// guaranteed acyclic, and a cycle here would otherwise hang the UI thread rather than
        /// merely render something wrong.
        /// </summary>
        public static List<QuestNode> Prerequisites(QuestNode target, QuestGraphBuilder graph)
        {
            var found = new List<QuestNode>();

            if (target == null || graph == null) return found;

            var seen = new HashSet<string>();
            var pending = new Queue<QuestNode>();
            pending.Enqueue(target);

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();

                foreach (var prerequisiteId in current.PrerequisiteIds)
                {
                    if (!seen.Add(prerequisiteId)) continue;
                    if (!graph.NodesById.TryGetValue(prerequisiteId, out var node)) continue;

                    found.Add(node);
                    pending.Enqueue(node);
                }
            }

            return found;
        }

        /// <summary>
        /// The route proper: the prerequisites still outstanding, ordered so the list can be worked
        /// down from the top.
        ///
        /// Completed quests are dropped rather than ticked off. A checklist of things already done
        /// is the Kappa tab's job; this answers what is left, and on a mature profile the done ones
        /// would be most of the list.
        ///
        /// Depth orders it, because a quest's depth is one past its deepest prerequisite - so
        /// nothing can appear above something it needs. Trader and name only break ties, keeping
        /// the same trader's quests together within a step.
        /// </summary>
        public static List<QuestNode> Remaining(QuestNode target, QuestGraphBuilder graph)
        {
            return Prerequisites(target, graph)
                .Where(node => node.Status != ENodeStatus.Completed)
                .OrderBy(node => node.Depth)
                .ThenBy(node => node.TraderName, System.StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
