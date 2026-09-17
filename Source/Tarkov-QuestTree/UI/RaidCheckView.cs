using System;
using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;

namespace QuestTree.UI
{
    /// <summary>
    /// Folds the per-condition raid check into what one map actually needs, and into a verdict.
    ///
    /// Written once, here, because two surfaces read it - the cue on the ready-up screen and the
    /// "Take with you" section in the map sidebar - and the whole promise of the feature is that
    /// those two agree. Two folds would be two chances to disagree, and the disagreement would be
    /// invisible until somebody loaded into a raid short of a marker.
    /// </summary>
    internal static class RaidCheckView
    {
        /// <summary>How much of a requirement the player has, and where it is.</summary>
        internal enum Have
        {
            /// <summary>Enough of it is on the character. Green.
            ///
            /// Includes the game's task-item containers, which are on the character in the only sense
            /// this cue cares about: you will have them in the raid, and there is no packing step you
            /// could forget. Line.InTaskItems is what tells the row to say so.</summary>
            OnYou,

            /// <summary>Owned and reachable, but not on you. Amber - a to-do rather than a problem,
            /// and the common case, because the items quests ask you to carry live in the stash until
            /// you deliberately pack them.
            ///
            /// A row count used to be quoted here. It was measured before task items counted as held,
            /// which moves rows out of this state, so it is dropped rather than restated from a
            /// measurement the same change invalidated.
            ///
            /// A task item held in FULL is never here - it is OnYou. A partial one still can be, since
            /// StateOf compares OnPerson against Needed and knows nothing about which of the shortfall
            /// is packable: two of a quest-item template with one in the task container and one in a
            /// hideout stash lands here, and the row does ask for something half unpackable. It takes a
            /// modded condition wanting two or more, since every vanilla plant wants one.</summary>
            ToPack,

            /// <summary>Not owned at all; you have to go and get one. Red.</summary>
            Missing
        }

        /// <summary>One folded requirement: everything a row or a cue state needs, and nothing
        /// else.</summary>
        internal sealed class Line
        {
            /// <summary>Every template the fold grouped on - the KEY as well as the content, since
            /// grouping is per (map, template SET).</summary>
            public List<string> Templates = new List<string>();

            /// <summary>What the row shows: the held template with the most on person, ties broken
            /// by total held, then by the order the server sent. Falls back to the canonical name
            /// when nothing at all is held, which is the red case.</summary>
            public string Name;

            public int Needed;
            public int OnPerson;
            public int InStash;
            public int Total;
            public int Elsewhere;

            /// <summary>The part of OnPerson that is in the task-item containers. Wording only - it is
            /// already inside OnPerson, so adding it to any sum would count it twice.</summary>
            public int InTaskItems;

            /// <summary>The found-in-raid subsets, summed the same way. Only read when a condition
            /// demands found-in-raid, which no vanilla one does.</summary>
            public int OnPersonFoundInRaid;
            public int FoundInRaid;

            public bool NeedsFoundInRaid;

            /// <summary>Quest names asking for it, distinct, in the order the server sent.</summary>
            public List<string> Quests = new List<string>();

            public Have State;
        }

        /// <summary>The whole answer for one map, so the button and the sidebar cannot compute it
        /// twice.
        ///
        /// The verdict is here rather than left to each surface for the same reason the fold is:
        /// "the sidebar summary and the button agree, on the same map, at the same moment" is a
        /// thing this release is judged on, and two derivations of one rule is how they stop
        /// agreeing.</summary>
        internal sealed class Verdict
        {
            public List<Line> Lines = new List<Line>();

            /// <summary>Null when the answer is untrustworthy - the neutral state. Reason says
            /// which of the causes fired, for the one log line a session.</summary>
            public Have? State;

            public int MissingCount;
            public int ToPackCount;

            /// <summary>Why neutral, or null when the verdict stands. Five causes are
            /// indistinguishable on screen, which is exactly why this is recorded rather than
            /// inferred.</summary>
            public string Reason;

            /// <summary>Why this map cannot be called READY, while every row on it still stands.
            /// Null when the answer can be confirmed.
            ///
            /// Distinct from Reason, and the distinction is the whole point: Reason means "do not
            /// believe any of this", and this means "believe the list, do not believe the green".
            /// Conflating them is what made a map with one unplaceable requirement draw no summary
            /// line, no "Take with you" section and a dead ready-up button - discarding N honest
            /// requirements to avoid overstating one.</summary>
            public string Caveat;

            /// <summary>Whether the green light has been earned. Every surface that would paint one
            /// asks this rather than testing State alone.</summary>
            public bool Confirmed => Caveat == null;

            /// <summary>Nothing to carry here - a real answer, not a failure. Rendered as "nothing
            /// to bring for this map" rather than as a green light.</summary>
            public bool Empty => State != null && Lines.Count == 0;
        }

        /// <summary>Folds one map's rows. Never returns null; an untrustworthy answer comes back
        /// with State null and Reason set.
        ///
        /// <paramref name="graph"/> may be null - it IS null on the ready-up screen, where the
        /// panel has never been opened - and the client half has Nullable disabled, so nothing
        /// warns about that. Status then comes from the payload alone, which is precisely why the
        /// payload carries it.</summary>
        internal static Verdict Fold(
            RaidCheckDto payload, string locationKey, QuestGraphBuilder graph, bool countUnaccepted)
        {
            var verdict = new Verdict();

            if (payload == null) { verdict.Reason = "no answer from the server half"; return verdict; }
            if (!payload.HasProfile) { verdict.Reason = "no profile to read"; return verdict; }

            if (!payload.InventoryLocationsKnown)
            {
                verdict.Reason = "the inventory roots could not be read, so on-person counts are unknown";
                return verdict;
            }

            var map = payload.MapFor(locationKey);
            if (map == null)
            {
                // NOT the same as "nothing to bring here". The server emits a row for every real
                // location, so a miss means a map it does not recognise - and an empty list drawn
                // as green is the worst thing this feature can say.
                verdict.Reason = $"the server does not recognise the map '{locationKey}'";
                return verdict;
            }

            // A caveat, not a verdict, and the server has always said so. Its own comment where
            // the counter is raised reads: "The row still appears - over-listing costs a false amber,
            // which is the safe direction - but the map can no longer go green." The row appearing is
            // half of that rule and this end never implemented it: returning here threw away every
            // OTHER requirement on the map, so one condition placed by assumption silently cost the
            // whole pre-raid check - no summary, no list, a plain button - with nothing on screen
            // saying why. It fires on the developer's own Customs.
            //
            // Recorded and carried instead, so the list stands and only the green light is withheld.
            if (map.UnplaceableConditions > 0)
            {
                verdict.Caveat =
                    $"{map.UnplaceableConditions} of this map's requirements were placed by assumption " +
                    "rather than by a harvested zone, so one of them may belong to another map";
            }

            if (!map.ZonesHarvested)
            {
                verdict.Reason =
                    "this map has no harvested zones, so any-location objectives cannot be placed on it - " +
                    "raid it once to fix this";
                return verdict;
            }

            verdict.Lines = FoldLines(map, payload, graph, countUnaccepted);

            verdict.MissingCount = verdict.Lines.Count(l => l.State == Have.Missing);
            verdict.ToPackCount = verdict.Lines.Count(l => l.State == Have.ToPack);

            // Red wins when both apply: you can pack a stash item before you load in, and you
            // cannot conjure one you do not own.
            verdict.State =
                verdict.MissingCount > 0 ? Have.Missing :
                verdict.ToPackCount > 0 ? Have.ToPack :
                Have.OnYou;

            return verdict;
        }

        private static List<Line> FoldLines(
            RaidCheckMapDto map, RaidCheckDto payload, QuestGraphBuilder graph, bool countUnaccepted)
        {
            var byKey = new Dictionary<string, Line>(StringComparer.Ordinal);
            var order = new List<Line>();

            foreach (var requirement in map.Requirements ?? new List<RaidCheckRequirementDto>())
            {
                if (requirement?.Templates == null || requirement.Templates.Count == 0) continue;
                if (!Counts(requirement, graph, countUnaccepted)) continue;

                // Keyed on the whole template SET, not on one template.
                //
                // The second condition of Hot Zone wants 12 and accepts nine ballistic plates, of
                // which the reference profile holds two of one and fifteen of another. Split per
                // template it becomes nine rows each demanding 12 against a single template's
                // holding, and every one reads MISSING on a requirement that is satisfied nearly
                // twice over. As one row keyed on the set it is 17 against 12.
                var key = string.Join("|", requirement.Templates.ToArray());

                if (!byKey.TryGetValue(key, out var line))
                {
                    line = new Line
                    {
                        Templates = requirement.Templates,
                        Name = requirement.Name,
                        NeedsFoundInRaid = requirement.FoundInRaid
                    };

                    Sum(line, payload);
                    byKey[key] = line;
                    order.Add(line);
                }

                // One quest asks for any copy, another insists on found-in-raid: the strict rule
                // wins, or the row promises a stack the second quest refuses.
                if (requirement.FoundInRaid) line.NeedsFoundInRaid = true;

                line.Needed += Math.Max(0, requirement.Needed);

                if (!string.IsNullOrEmpty(requirement.QuestName) && !line.Quests.Contains(requirement.QuestName))
                    line.Quests.Add(requirement.QuestName);
            }

            foreach (var line in order)
            {
                line.Name = NameFor(line, payload);
                line.State = StateOf(line);
            }

            // Worst first, so the list reads as a to-do and everything needing action sits above
            // everything that does not. Alphabetical would bury the one item you have to go and buy
            // between sixteen cameras you already have.
            return order
                .OrderBy(l => l.State == Have.Missing ? 0 : l.State == Have.ToPack ? 1 : 2)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Held counts, summed across every template the condition accepts.</summary>
        private static void Sum(Line line, RaidCheckDto payload)
        {
            foreach (var template in line.Templates)
            {
                if (payload.Held == null || !payload.Held.TryGetValue(template, out var held) || held == null)
                    continue;

                line.OnPerson += held.OnPerson;
                line.InStash += held.InStash;
                line.InTaskItems += held.InTaskItems;
                line.Total += held.Total;
                line.Elsewhere += held.Elsewhere;
                line.OnPersonFoundInRaid += held.OnPersonFoundInRaid;
                line.FoundInRaid += held.FoundInRaid;
            }
        }

        /// <summary>The name of the template actually satisfying the row.
        ///
        /// Measured on the reference profile: Confidential Info accepts six grenades and the player
        /// carries seven on person - two F-1s, two VOG-17s, an M67, an RGD-5 and a VOG-25 - while
        /// the only V40 they own sits in the stash. Named after the first template it would render
        /// "V40 Mini-Grenade - 7 of 1 - on you": green, arithmetically right, and naming an item
        /// they are not carrying a single one of.
        ///
        /// The tie-break matters as much as the rule. Hot Zone's nine plates are all at zero on
        /// person, so "most on person" is a nine-way tie - and breaking it by the server's order
        /// hands back the first template, which the profile owns none of, while it holds fifteen of
        /// another. Total held breaks it correctly.</summary>
        private static string NameFor(Line line, RaidCheckDto payload)
        {
            if (payload.ItemNames == null) return line.Name;

            string best = null;
            var bestOnPerson = 0;
            var bestTotal = 0;
            var held = 0;

            foreach (var template in line.Templates)
            {
                if (payload.Held == null || !payload.Held.TryGetValue(template, out var counts) || counts == null)
                    continue;

                if (counts.Total <= 0) continue;
                held++;

                if (best != null && counts.OnPerson <= bestOnPerson &&
                    (counts.OnPerson < bestOnPerson || counts.Total <= bestTotal))
                {
                    continue;
                }

                best = template;
                bestOnPerson = counts.OnPerson;
                bestTotal = counts.Total;
            }

            // Nothing held at all: the canonical name is right, because there is no carried item to
            // name the row after.
            if (best == null) return line.Name;

            var name = payload.ItemNames.TryGetValue(best, out var resolved) && !string.IsNullOrEmpty(resolved)
                ? resolved
                : line.Name;

            // One requirement stays one row; the tail says the rest exist.
            return held > 1 ? $"{name}  +{held - 1} more" : name;
        }

        private static Have StateOf(Line line)
        {
            // A found-in-raid condition compares found-in-raid counts on BOTH sides, or it goes
            // green on bought copies - the precise regression the separate found-in-raid counts were
            // added to prevent. No vanilla carry condition sets the flag, so this branch ships
            // unexercised and must not be recorded as verified.
            var onPerson = line.NeedsFoundInRaid ? line.OnPersonFoundInRaid : line.OnPerson;
            var owned = line.NeedsFoundInRaid ? line.FoundInRaid : line.Total;

            if (onPerson >= line.Needed) return Have.OnYou;

            // Against TOTAL, not OnPerson + InStash. The reference profile holds 325 items that are
            // neither on the character nor in the stash - chiefly hideout area stashes - and calling
            // those Missing would tell the player to go and buy a marker they already own.
            //
            // Nothing here needed changing for task items, which is the point of counting them inside
            // OnPerson: the branch above already returns green for them.
            return owned >= line.Needed ? Have.ToPack : Have.Missing;
        }

        /// <summary>Whether this requirement counts toward the verdict.
        ///
        /// Accepted always - Started, and AvailableForFinish, which means every objective is done
        /// but the quest is not handed in. Naming only Started would have let the graph-bearing
        /// sidebar count such a quest while the graph-less button did not, which is exactly the
        /// disagreement the shared fold exists to prevent.
        ///
        /// Unaccepted only when the setting says so. Everything else - locked, failed, already
        /// handed in - has no place on a pre-raid list at all.</summary>
        private static bool Counts(
            RaidCheckRequirementDto requirement, QuestGraphBuilder graph, bool countUnaccepted)
        {
            var status = requirement.Status ?? "";

            // The graph knows better where it has a node, because it reads the live quest instance.
            // It is null on the ready-up screen, which is why the payload carries status at all.
            if (graph != null)
            {
                QuestNode node = null;
                if (!string.IsNullOrEmpty(requirement.QuestId))
                    graph.NodesById.TryGetValue(requirement.QuestId, out node);

                if (node != null)
                {
                    return node.Status == ENodeStatus.Active ||
                           (countUnaccepted && node.Status == ENodeStatus.Available);
                }
            }

            if (status.Equals("Started", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("AvailableForFinish", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return countUnaccepted &&
                   status.Equals("AvailableForStart", StringComparison.OrdinalIgnoreCase);
        }
    }
}
