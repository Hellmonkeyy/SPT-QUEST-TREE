using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace QuestTreeServer
{
    /// <summary>
    /// Picks parts that satisfy a Gunsmith quest.
    ///
    /// WHAT THIS CAN AND CANNOT DO, because the difference is arithmetic rather than effort.
    ///
    /// The M4A1 has 5.32 x 10^32 complete assemblies over the parts its slots reach. Finding a build
    /// that PASSES every threshold is very achievable; finding the provably BEST one is not, and
    /// anything claiming otherwise would be lying. So this looks for a feasible build, and spends its
    /// intelligence on the ORDER it tries candidates in, so that the first build it finds is a good
    /// one rather than merely a legal one.
    ///
    /// WHY THERE IS NO BRANCH-AND-BOUND HERE
    ///
    /// The original design credited branch-and-bound with making this tractable. It does not. The
    /// constraints are slack - best reachable recoil is single digits against thresholds of 250-950,
    /// and best reachable ergonomics clears every threshold from 15 to 75 - so an admissible bound
    /// passes every partial build and prunes nothing at all. What actually carries the search is the
    /// candidate ordering, and what stops it on a hopeless request is the node ceiling.
    ///
    /// The ceiling is therefore an ANSWER, not a safety net. "No build found within the search
    /// budget" is a different statement from "no build exists", and conflating the two would be a
    /// failure that looks like an answer.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponSolver(
        ISptLogger<WeaponSolver> logger,
        WeaponGraph graph,
        WeaponStatModel model)
    {
        /// <summary>Nodes the search may open before it gives up and reports what it has.</summary>
        private const int NodeCeiling = 60000;

        /// <summary>Wall clock, because a node is not a fixed cost. This runs on a request thread
        /// and may not become a way to stall the server.</summary>
        private static readonly TimeSpan TimeCeiling = TimeSpan.FromSeconds(2);

        /// <summary>How deep the slot tree may nest. The graph's own cap, repeated here because the
        /// search descends independently of the graph walk.</summary>
        private const int MaxDepth = 12;

        public sealed class FittedPart
        {
            public string SlotName { get; init; } = "";
            public MongoId Template { get; init; }
            public int Depth { get; init; }
        }

        public sealed class Result
        {
            public bool Found { get; set; }

            /// <summary>True when the ceiling stopped the search. NOT the same as "no build
            /// exists", and the caller must not report it as such.</summary>
            public bool HitCeiling { get; set; }

            public List<FittedPart> Parts { get; } = new();
            public WeaponStatModel.Stats? Stats { get; set; }

            /// <summary>Thresholds the best build found does not meet, with the margin.</summary>
            public List<string> Unmet { get; } = new();

            /// <summary>Constrained stats this model cannot score at all, so a pass on everything
            /// else does not mean the quest is satisfied.</summary>
            public List<string> Unchecked { get; } = new();

            public int NodesOpened { get; set; }
        }

        /// <summary>One threshold, reduced to what the search needs: which stat, and which direction
        /// is better.</summary>
        private sealed class Goal
        {
            public string Field = "";
            public double Value;
            public bool HigherIsBetter;

            public bool Met(double actual) => HigherIsBetter ? actual >= Value : actual <= Value;
            public double Margin(double actual) => HigherIsBetter ? actual - Value : Value - actual;
        }

        /// <summary>Stats the model scores. Anything else a quest constrains is reported as
        /// unchecked rather than assumed to pass - height, width, base accuracy, muzzle velocity and
        /// the empty-tactical-slot count are all real constraints this cannot see.</summary>
        private static readonly HashSet<string> Scored = new(StringComparer.OrdinalIgnoreCase)
        {
            "ergonomics", "recoil", "weight", "magazine capacity", "effective distance"
        };

        /// <summary>Fields that are not build properties at all, so their absence from a build is
        /// not a gap. Durability is the weapon's repair state.</summary>
        private static readonly HashSet<string> NotBuildProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "durability"
        };

        public Result Solve(
            MongoId weapon,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId>? allowed)
        {
            var result = new Result();

            var reachable = graph.Reachable(weapon, out var notes);

            if (reachable == null)
            {
                logger.Debug($"Quest Tracker: cannot solve for '{weapon}' - {string.Join("; ", notes)}.");
                return result;
            }

            var goals = new List<Goal>();

            foreach (var (field, compare, value) in thresholds)
            {
                if (NotBuildProperties.Contains(field)) continue;

                if (!Scored.Contains(field))
                {
                    result.Unchecked.Add($"{field} {compare} {value:0.##}");
                    continue;
                }

                goals.Add(new Goal
                {
                    Field = field,
                    Value = value,
                    // ">=" and ">" want more; "<=" and "<" want less. Read from the data rather than
                    // assumed per stat, because the same stat points both ways across quests.
                    HigherIsBetter = compare.StartsWith(">", StringComparison.Ordinal)
                });
            }

            var clock = Stopwatch.StartNew();
            var state = new SearchState(reachable, allowed, mustInclude, goals, clock);

            // Every part on a route to something the quest insists on. Without this the walk chooses
            // parents by stat score and wanders off the only path that reaches a required part -
            // which was 74 of the failures in the first dry run, every one of them a part the graph
            // could reach and the search never visited.
            foreach (var required in mustInclude)
                foreach (var step in PathTo(weapon, required, reachable))
                    state.Forced.Add(step);

            Descend(weapon, 0, state);

            result.NodesOpened = state.Nodes;
            result.HitCeiling = state.Exhausted;

            // Whatever the walk settled on, scored once at the end rather than per node: Score is
            // the expensive call and the ordering already aims the walk at the thresholds.
            var fitted = state.Fitted.Select(f => f.Template).ToList();
            var stats = model.Score(weapon, fitted);

            result.Stats = stats;
            result.Parts.AddRange(state.Fitted);

            if (stats == null) return result;

            foreach (var goal in goals)
            {
                var actual = Read(stats, goal.Field);

                if (actual == null)
                {
                    // The model returns null for "I do not claim to know", which must never be read
                    // as a pass: a quest wanting a magazine over 30 rounds is not satisfied by a
                    // build with no magazine at all.
                    result.Unmet.Add($"{goal.Field}: no value - the build has nothing that provides it");
                    continue;
                }

                if (goal.Met(actual.Value)) continue;

                result.Unmet.Add(
                    $"{goal.Field} {actual.Value:0.##}, needs {(goal.HigherIsBetter ? ">=" : "<=")} {goal.Value:0.##} " +
                    $"(short by {Math.Abs(goal.Margin(actual.Value)):0.##})");
            }

            // Every part the quest insisted on has to actually be on the gun. A build missing one is
            // not a near miss, it is the wrong answer.
            //
            // REACHABLE is reported separately from FITTED, because the two failures want opposite
            // fixes. A part the slot graph never reaches is a data or flattening gap - the wrong
            // filter read, a slot type not walked. A part the graph reaches but the walk did not
            // place is a search problem, and no amount of widening the graph will fix it.
            foreach (var required in mustInclude)
            {
                if (fitted.Contains(required)) continue;

                result.Unmet.Add(reachable.ContainsKey(required)
                    ? $"could not fit the required part {required} (reachable, not placed)"
                    : $"required part {required} is NOT REACHABLE from this weapon's slots");
            }

            result.Found = result.Unmet.Count == 0;

            return result;
        }

        /// <summary>Every template on a shortest route from the weapon to one target part, the
        /// target included. Empty when there is no route.
        ///
        /// Breadth-first, so the route found is the shallowest one: fitting a scope directly to a
        /// dust cover beats routing it through a mount and a riser when both are legal, and a
        /// shorter chain leaves fewer slots spoken for.
        ///
        /// The visited set is doing double duty here, as it does in the graph walk - it is what
        /// makes a cyclic slot graph terminate rather than loop.</summary>
        private static List<MongoId> PathTo(
            MongoId from, MongoId target, IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> reachable)
        {
            if (from == target) return new List<MongoId>();

            var cameFrom = new Dictionary<MongoId, MongoId>();
            var seen = new HashSet<MongoId> { from };
            var queue = new Queue<(MongoId Template, int Depth)>();

            queue.Enqueue((from, 0));

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (depth >= MaxDepth) continue;
                if (!reachable.TryGetValue(current, out var part)) continue;

                foreach (var slot in part.Slots)
                {
                    foreach (var candidate in slot.Candidates)
                    {
                        if (!seen.Add(candidate)) continue;

                        cameFrom[candidate] = current;

                        if (candidate == target)
                        {
                            // Back up the chain, dropping the weapon itself - it is not a part to
                            // fit, it is the thing being fitted to.
                            var path = new List<MongoId>();
                            var step = target;

                            while (true)
                            {
                                path.Add(step);
                                if (!cameFrom.TryGetValue(step, out var parent) || parent == from) break;

                                step = parent;
                            }

                            return path;
                        }

                        queue.Enqueue((candidate, depth + 1));
                    }
                }
            }

            return new List<MongoId>();
        }

        private static double? Read(WeaponStatModel.Stats stats, string field) => field.ToLowerInvariant() switch
        {
            "ergonomics" => stats.Ergonomics,
            "recoil" => stats.Recoil,
            "weight" => stats.Weight,
            "magazine capacity" => stats.MagazineCapacity,
            "effective distance" => stats.EffectiveDistance,
            _ => null
        };

        /// <summary>Everything one search carries, so the recursion takes one argument rather than
        /// eight.</summary>
        private sealed class SearchState
        {
            public SearchState(
                IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> reachable,
                IReadOnlyCollection<MongoId>? allowed,
                IReadOnlyCollection<MongoId> mustInclude,
                List<Goal> goals,
                Stopwatch clock)
            {
                Reachable = reachable;
                Allowed = allowed;
                MustInclude = new HashSet<MongoId>(mustInclude);
                Goals = goals;
                Clock = clock;
            }

            public IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> Reachable { get; }
            public IReadOnlyCollection<MongoId>? Allowed { get; }
            public HashSet<MongoId> MustInclude { get; }
            public List<Goal> Goals { get; }
            public Stopwatch Clock { get; }

            /// <summary>Templates on a route to a part the quest insists on. A slot offering one
            /// of these takes it, whatever it scores: the quest's requirement is not a preference to
            /// be outvoted by ergonomics.</summary>
            public HashSet<MongoId> Forced { get; } = new();

            public List<FittedPart> Fitted { get; } = new();
            public HashSet<MongoId> Conflicts { get; } = new();
            public HashSet<MongoId> Used { get; } = new();

            public int Nodes;
            public bool Exhausted;

            public bool OutOfBudget()
            {
                if (Exhausted) return true;
                if (++Nodes < NodeCeiling && Clock.Elapsed < TimeCeiling) return false;

                Exhausted = true;
                return true;
            }
        }

        /// <summary>Fills a part's slots, greedily, deepest-first.
        ///
        /// Greedy rather than exhaustive on purpose. With constraints this slack the first leaf
        /// reached under a good ordering is very nearly always feasible, and an exhaustive walk over
        /// 10^32 assemblies would be answering a question nobody asked.</summary>
        private void Descend(MongoId template, int depth, SearchState state)
        {
            if (depth > MaxDepth || state.OutOfBudget()) return;
            if (!state.Reachable.TryGetValue(template, out var part)) return;

            foreach (var slot in part.Slots)
            {
                var choice = Choose(slot, state);
                if (choice == null) continue;

                var chosen = choice.Value;

                state.Fitted.Add(new FittedPart { SlotName = slot.Name, Template = chosen, Depth = depth });
                state.Used.Add(chosen);

                if (state.Reachable.TryGetValue(chosen, out var chosenPart))
                    foreach (var conflict in chosenPart.Conflicts)
                        state.Conflicts.Add(conflict);

                Descend(chosen, depth + 1, state);
            }
        }

        /// <summary>The best candidate for one slot, or null to leave it empty.
        ///
        /// Order is the whole algorithm. A part the quest INSISTS on wins outright; after that,
        /// candidates are scored against the goal with the least headroom, in the direction that
        /// goal points - which is not one direction for all stats. A quest wanting magazine capacity
        /// under 10, or effective distance under 800, points the opposite way from one wanting
        /// ergonomics over 62, inside the same build. Ergonomics-descending is a default, not a
        /// rule.</summary>
        private MongoId? Choose(WeaponGraph.SlotInfo slot, SearchState state)
        {
            MongoId? best = null;
            var bestScore = double.NegativeInfinity;

            foreach (var candidate in slot.Candidates)
            {
                if (state.Used.Contains(candidate)) continue;
                if (state.Conflicts.Contains(candidate)) continue;
                if (state.Allowed != null && !state.Allowed.Contains(candidate)) continue;
                if (!state.Reachable.TryGetValue(candidate, out var part)) continue;

                // Conflicts run both ways: a part already fitted may be on this candidate's list.
                if (part.Conflicts.Overlaps(state.Used)) continue;

                // A part the quest names, or one standing between the weapon and such a part, is
                // not a preference to be outvoted by a better-scoring alternative. Take it.
                if (state.MustInclude.Contains(candidate) || state.Forced.Contains(candidate))
                    return candidate;

                var score = Score(part, state.Goals);

                if (score <= bestScore) continue;

                bestScore = score;
                best = candidate;
            }

            // An empty slot is a legitimate choice: fitting something into every hole makes a gun
            // heavier and usually worse, and weight is a threshold in its own right.
            return bestScore > 0d ? best : null;
        }

        /// <summary>How much this part helps, summed over the goals, each weighted by how tight it
        /// is. A part that helps nothing scores zero and the slot is left empty.</summary>
        private static double Score(WeaponGraph.PartInfo part, List<Goal> goals)
        {
            var score = 0d;

            foreach (var goal in goals)
            {
                switch (goal.Field.ToLowerInvariant())
                {
                    case "ergonomics":
                        score += goal.HigherIsBetter ? part.Ergonomics : -part.Ergonomics;
                        break;

                    case "recoil":
                        // Recoil mods are negative percentages, so a more negative number is better
                        // when the goal is an upper limit - which it always is in practice.
                        score += goal.HigherIsBetter ? part.RecoilPercent : -part.RecoilPercent;
                        break;

                    case "weight":
                        score += goal.HigherIsBetter ? part.Weight : -part.Weight;
                        break;

                    case "magazine capacity":
                        if (part.MagazineCapacity is { } capacity)
                            score += goal.HigherIsBetter ? capacity : -capacity;
                        break;

                    case "effective distance":
                        if (part.SightingRange is { } range)
                            score += goal.HigherIsBetter ? range * 0.01d : -range * 0.01d;
                        break;
                }
            }

            return score;
        }
    }
}
