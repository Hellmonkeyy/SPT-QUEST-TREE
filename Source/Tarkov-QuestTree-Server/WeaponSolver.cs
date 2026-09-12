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
    /// anything claiming otherwise would be lying. So this looks for a feasible build and spends its
    /// intelligence on getting there, not on proving it optimal.
    ///
    /// WHY THERE IS NO BRANCH-AND-BOUND HERE
    ///
    /// The original design credited branch-and-bound with making this tractable. It does not. The
    /// constraints are slack - best reachable recoil is single digits against thresholds of 250-950,
    /// and best reachable ergonomics clears every threshold from 15 to 75 - so an admissible bound
    /// passes every partial build and prunes nothing at all.
    ///
    /// WHAT IT DOES INSTEAD - three stages, and the split is the whole design
    ///
    ///   PLAN     The parts the quest NAMES are placed first, into an explicit tree, with
    ///            backtracking over alternative routes onto the gun. These are not preferences and
    ///            they must not compete with stats for a slot.
    ///   DRESS    Every remaining slot is filled greedily, ordered by a cheap per-part heuristic.
    ///            This is a starting point, not an answer.
    ///   CLIMB    Every unlocked slot is then re-examined against the REAL stat model: swap the
    ///            part, swap it for nothing, rescore the whole gun, keep what helps. Repeat until a
    ///            sweep finds no improvement.
    ///
    /// The climb is why the heuristic no longer has to be clever. A per-part score cannot know that
    /// a suppressor's -20 ergonomics is worth paying for when recoil is the binding threshold and
    /// ergonomics has 30 points of headroom; scoring the assembled gun knows exactly that. The
    /// previous pure-greedy version left muzzle brakes on the floor for precisely this reason and
    /// missed recoil thresholds by three points.
    ///
    /// The ceiling is an ANSWER, not a safety net. "No build found within the search budget" is a
    /// different statement from "no build exists", and conflating the two would be a failure that
    /// looks like an answer.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponSolver(
        ISptLogger<WeaponSolver> logger,
        WeaponGraph graph,
        WeaponStatModel model)
    {
        /// <summary>Nodes the search may open before it gives up and reports what it has. A node is
        /// one scored trial, and the climb spends them in thousands rather than the greedy's dozens,
        /// so this is sized for the climb.</summary>
        private const int NodeCeiling = 400000;

        /// <summary>Wall clock, because a node is not a fixed cost. This runs on a request thread
        /// and may not become a way to stall the server.</summary>
        private static readonly TimeSpan TimeCeiling = TimeSpan.FromSeconds(2);

        /// <summary>How deep the slot tree may nest. The graph's own cap, repeated here because the
        /// search descends independently of the graph walk.</summary>
        private const int MaxDepth = 12;

        /// <summary>Alternative routes onto the gun the planner will consider for one required
        /// part.</summary>
        private const int RoutesPerPart = 16;

        /// <summary>Steps past the shortest a route may take. Two covers "scope on a riser on a
        /// mount" where "scope on a mount" is shortest, and keeps the backward walk finite - the
        /// number of routes through a 667-part graph is not bounded in any useful sense.</summary>
        private const int RouteSlack = 2;

        /// <summary>Sweeps of the climb. It stops on its own as soon as a sweep finds no move, so
        /// this only bounds a pathological oscillation.</summary>
        private const int MaxSweeps = 24;

        /// <summary>How far below a candidate a trial fills before scoring it.
        ///
        /// A mount is worth nothing by itself and everything with a scope on it, so a trial that
        /// judged parts bare would reject every adapter ever made. Two levels sees the scope, and is
        /// cheap because the fill uses the per-part heuristic rather than the stat model.</summary>
        private const int TrialFillDepth = 2;

        /// <summary>Charged for a threshold the build produces no value for at all - no magazine
        /// against a magazine-capacity goal, no sight against a range goal. One whole unit, so it
        /// reads as "completely unmet" against shortfalls measured as fractions of a threshold, and
        /// the climb always prefers fitting something to fitting nothing.</summary>
        private const double MissingStatPenalty = 1d;

        /// <summary>What one unplaced required part costs when two attempts are compared. Larger
        /// than any plausible sum of shortfalls, because a build missing a part the quest named is
        /// the wrong answer rather than a worse one.</summary>
        private const double MissingPartPenalty = 1000d;

        /// <summary>Improvement a move must make to be taken. Guards the climb against oscillating
        /// on floating point noise.</summary>
        private const double MinGain = 1e-6;

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

        /// <summary>One threshold, reduced to what the search needs: which stat, which direction is
        /// better, and how big a unit of it is.</summary>
        private sealed class Goal
        {
            public string Field = "";
            public double Value;
            public bool HigherIsBetter;

            /// <summary>What one point of this stat is worth, so shortfalls on ergonomics, recoil
            /// and kilograms can be added up. The threshold IS the stat's scale: ten short of 40
            /// ergonomics and ten short of 610 recoil are not the same size of problem.</summary>
            public double Scale = 1d;

            public bool Met(double actual) => HigherIsBetter ? actual >= Value : actual <= Value;
            public double Margin(double actual) => HigherIsBetter ? actual - Value : Value - actual;

            /// <summary>How far from met, as a fraction of the threshold. Zero once met - the search
            /// wants a build that passes, not the best one, and a met goal must stop pulling.</summary>
            public double Shortfall(double actual)
            {
                var margin = Margin(actual);
                return margin < 0d ? -margin / Scale : 0d;
            }
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
                    HigherIsBetter = compare.StartsWith(">", StringComparison.Ordinal),
                    Scale = Math.Max(Math.Abs(value), 1d)
                });
            }

            // The bare weapon, scored once. Recoil is why: a mod moves recoil by a PERCENTAGE, so
            // "-8%" is worth eight percent of THIS weapon's recoil - 52 points on a P226 at 651 and
            // 20 on an AK-102 at 254 - and comparing that raw percentage against ergonomics points
            // is what made the old ordering leave muzzle brakes on the floor.
            var bare = model.Score(weapon, Array.Empty<MongoId>());

            var state = new SearchState(reachable, allowed, goals, Stopwatch.StartNew())
            {
                RecoilPerPercent = (bare?.Recoil ?? 0d) / 100d
            };

            state.Measure(weapon);

            var required = new List<MongoId>();
            foreach (var id in mustInclude)
                if (id != weapon && !required.Contains(id)) required.Add(id);

            Node? best = null;
            var bestCost = double.PositiveInfinity;

            // Two starting points, because a climb is a local search and which optimum it reaches
            // depends on where it starts. A greedily dressed gun is the better start most of the
            // time; a gun wearing nothing but what the quest demands wins when the thresholds want
            // LESS of something, where every part the dressing adds is a step away from the answer.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var root = new Node { Template = weapon, Locked = true };

                state.ResetTree(weapon);
                Plan(root, required, state);

                if (attempt == 0) Fill(root, state, MaxDepth);

                var penalty = Climb(weapon, root, state);
                var missing = required.Count(part => Find(root, part) == null);
                var cost = penalty + missing * MissingPartPenalty;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = root;
                }

                if (bestCost <= 0d || state.Exhausted) break;
            }

            result.NodesOpened = state.Nodes;
            result.HitCeiling = state.Exhausted;

            if (best == null) return result;

            var fitted = new List<MongoId>();
            CollectTemplates(best, fitted);

            var stats = model.Score(weapon, fitted);

            result.Stats = stats;
            CollectParts(best, result.Parts);

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
            // filter read, a slot type not walked. A part the graph reaches but the planner could
            // not place is a search problem, and no amount of widening the graph will fix it.
            foreach (var part in required)
            {
                if (fitted.Contains(part)) continue;

                result.Unmet.Add(reachable.ContainsKey(part)
                    ? $"could not fit the required part {part} (reachable, not placed)"
                    : $"required part {part} is NOT REACHABLE from this weapon's slots");
            }

            result.Found = result.Unmet.Count == 0;

            return result;
        }

        // ---------------------------------------------------------------------------------------
        // PLAN - the parts the quest names, placed into an explicit tree
        // ---------------------------------------------------------------------------------------

        /// <summary>Places every required part, backtracking over routes when two of them want the
        /// same slot.
        ///
        /// The previous version marked every template on a shortest route as "forced" and let the
        /// greedy take any forced part it met. That loses on two shapes that both occur here: two
        /// required parts whose shortest routes share one rail, and a forced part being claimed by
        /// the first slot that happens to admit it rather than the slot its own route meant. Both
        /// need the routes to be chosen TOGETHER, which is what this does.</summary>
        private bool Plan(Node root, List<MongoId> required, SearchState state)
        {
            if (required.Count == 0) return true;

            var order = new List<(MongoId Part, List<List<MongoId>> Routes)>(required.Count);

            foreach (var part in required)
                order.Add((part, Routes(root.Template, part, state)));

            // Fewest routes first. A part with one way onto the gun has to get that way, and the old
            // failure was exactly the reverse: a part with a dozen routes took the shared rail first
            // and left the part with one route nowhere to go.
            order.Sort((left, right) => left.Routes.Count.CompareTo(right.Routes.Count));

            return PlanFrom(root, order, 0, state);
        }

        private bool PlanFrom(
            Node root, List<(MongoId Part, List<List<MongoId>> Routes)> order, int index, SearchState state)
        {
            if (index >= order.Count) return true;
            if (state.OutOfBudget()) return false;

            var (part, routes) = order[index];

            // An earlier part's route may already have brought this one along: two required parts on
            // the same chain share it rather than competing for it.
            if (Find(root, part) != null) return PlanFrom(root, order, index + 1, state);

            foreach (var route in routes)
            {
                var added = new List<Node>();

                if (Lay(root, route, 0, added, state) && PlanFrom(root, order, index + 1, state)) return true;

                for (var i = added.Count - 1; i >= 0; i--) Detach(added[i], state);
            }

            return false;
        }

        /// <summary>Fits one route's templates in order, from the node they hang off, choosing a
        /// slot for each and backtracking when a deeper step cannot be placed.</summary>
        private bool Lay(Node parent, List<MongoId> route, int step, List<Node> added, SearchState state)
        {
            if (step >= route.Count) return true;
            if (state.OutOfBudget()) return false;
            if (parent.Depth >= MaxDepth) return false;

            var template = route[step];

            // The chain may already be half built by another part's route.
            var existing = parent.Children.FirstOrDefault(child => child.Template == template);
            if (existing != null) return Lay(existing, route, step + 1, added, state);

            if (!state.Reachable.TryGetValue(parent.Template, out var host)) return false;
            if (!Compatible(template, state)) return false;

            // Narrowest slot first. A slot admitting three things is almost certainly the one this
            // part was made for, and spending a general rail that admits eighty denies it to a part
            // with nowhere else to go.
            var slots = Enumerable.Range(0, host.Slots.Length)
                .Where(index => Occupant(parent, index) == null && Array.IndexOf(host.Slots[index].Candidates, template) >= 0)
                .OrderBy(index => host.Slots[index].Candidates.Length)
                .ToList();

            foreach (var index in slots)
            {
                var node = new Node
                {
                    Template = template,
                    SlotName = host.Slots[index].Name,
                    SlotIndex = index,
                    Depth = parent.Depth + 1,
                    Locked = true
                };

                Attach(parent, node, state);
                added.Add(node);

                if (Lay(node, route, step + 1, added, state)) return true;

                added.RemoveAt(added.Count - 1);
                Detach(node, state);
            }

            return false;
        }

        /// <summary>Near-shortest routes from the weapon to one part, each a list of templates to
        /// fit in order with the target last. Empty when there is no route.
        ///
        /// More than one, because a single route is what the old search had and slot contention is
        /// exactly where the shortest route is the wrong one - two required parts whose shortest
        /// routes both want the same rail need one of them to take the longer way round.
        ///
        /// Shortest first, and never longer than the shortest plus <see cref="RouteSlack"/>: a long
        /// route spends slots the rest of the build needs, and there is no useful bound on how many
        /// routes exist through a 667-part graph.</summary>
        private static List<List<MongoId>> Routes(MongoId weapon, MongoId target, SearchState state)
        {
            var routes = new List<List<MongoId>>();

            if (target == weapon || !state.Distance.TryGetValue(target, out var shortest)) return routes;

            var limit = shortest + RouteSlack;
            var path = new List<MongoId>();
            var onPath = new HashSet<MongoId>();

            // Backwards from the target, so the walk is over the parts that can actually reach it
            // rather than over the whole graph. The visited set is what makes a cyclic slot graph
            // terminate rather than loop, and the length prune is what keeps it finite.
            void Walk(MongoId node)
            {
                if (routes.Count >= RoutesPerPart) return;
                if (path.Count > MaxDepth) return;
                if (!state.Distance.TryGetValue(node, out var distance) || distance + path.Count > limit) return;

                path.Add(node);
                onPath.Add(node);

                if (state.Hosts.TryGetValue(node, out var hosts))
                    foreach (var host in hosts)
                    {
                        if (host == weapon)
                        {
                            var route = new List<MongoId>(path);
                            route.Reverse();
                            routes.Add(route);
                        }
                        else if (!onPath.Contains(host))
                        {
                            Walk(host);
                        }

                        if (routes.Count >= RoutesPerPart) break;
                    }

                path.RemoveAt(path.Count - 1);
                onPath.Remove(node);
            }

            Walk(target);

            return routes;
        }

        // ---------------------------------------------------------------------------------------
        // DRESS - the remaining slots, greedily
        // ---------------------------------------------------------------------------------------

        /// <summary>Fills every unoccupied slot at or below one node, by the per-part heuristic.
        ///
        /// Greedy and deliberately unambitious: this is the climb's starting point, and the climb
        /// scores the assembled gun, which is the only thing that can tell whether a part with -20
        /// ergonomics and -12% recoil is worth fitting.</summary>
        private void Fill(Node node, SearchState state, int depthLimit)
        {
            if (node.Depth >= depthLimit || node.Depth >= MaxDepth) return;
            if (state.OutOfBudget()) return;
            if (!state.Reachable.TryGetValue(node.Template, out var part)) return;

            for (var index = 0; index < part.Slots.Length; index++)
            {
                if (Occupant(node, index) != null) continue;

                var choice = Choose(node, part.Slots[index], state);
                if (choice == null) continue;

                Attach(node, new Node
                {
                    Template = choice.Value,
                    SlotName = part.Slots[index].Name,
                    SlotIndex = index,
                    Depth = node.Depth + 1
                }, state);
            }

            foreach (var child in node.Children)
                Fill(child, state, depthLimit);
        }

        /// <summary>The best-scoring legal candidate for one slot, or null to leave it empty.
        ///
        /// An empty slot is a legitimate choice: a part that helps nothing measured is weight for
        /// free, and weight is a threshold in its own right. The climb revisits every slot this
        /// leaves empty, so a wrong "no" here costs a sweep rather than the answer.</summary>
        private static MongoId? Choose(Node parent, WeaponGraph.SlotInfo slot, SearchState state)
        {
            MongoId? best = null;
            var bestScore = 0d;

            foreach (var candidate in slot.Candidates)
            {
                if (state.Allowed != null && !state.Allowed.Contains(candidate)) continue;
                if (!state.Reachable.TryGetValue(candidate, out var part)) continue;
                if (IsAncestor(parent, candidate)) continue;
                if (!Compatible(candidate, state)) continue;

                var score = Score(part, state);
                if (score <= bestScore) continue;

                bestScore = score;
                best = candidate;
            }

            return best;
        }

        /// <summary>How much one part helps, summed over the goals, each in units of its own
        /// threshold so that percentages, kilograms and ergonomics points are comparable.</summary>
        private static double Score(WeaponGraph.PartInfo part, SearchState state)
        {
            var score = 0d;

            foreach (var goal in state.Goals)
            {
                var direction = goal.HigherIsBetter ? 1d : -1d;

                switch (goal.Field.ToLowerInvariant())
                {
                    case "ergonomics":
                        score += direction * part.Ergonomics / goal.Scale;
                        break;

                    case "recoil":
                        // Recoil mods are negative percentages against the WEAPON's recoil, so the
                        // conversion is what makes this comparable to anything else.
                        score += direction * part.RecoilPercent * state.RecoilPerPercent / goal.Scale;
                        break;

                    case "weight":
                        score += direction * part.Weight / goal.Scale;
                        break;

                    case "magazine capacity":
                        if (part.MagazineCapacity is { } capacity)
                            score += direction * capacity / goal.Scale;
                        break;

                    case "effective distance":
                        if (part.SightingRange is { } range)
                            score += direction * range / goal.Scale;
                        break;
                }
            }

            return score;
        }

        // ---------------------------------------------------------------------------------------
        // CLIMB - the assembled gun, against the real model
        // ---------------------------------------------------------------------------------------

        /// <summary>Re-examines every unlocked slot against the stat model until a sweep finds no
        /// improvement, and returns the penalty of the build it settles on. Zero means every
        /// threshold is met.</summary>
        private double Climb(MongoId weapon, Node root, SearchState state)
        {
            var buffer = new List<MongoId>();
            var current = Penalty(weapon, root, state, buffer);

            for (var sweep = 0; sweep < MaxSweeps && current > 0d; sweep++)
            {
                var moved = false;
                var queue = new Queue<Node>();

                CollectNodes(root, queue);

                while (queue.Count > 0)
                {
                    if (state.OutOfBudget()) return current;

                    var node = queue.Dequeue();

                    // A node a previous move displaced. The queue outlives the tree it was taken
                    // from, and editing a stale node would put parts back on a gun they left.
                    if (node.Removed) continue;
                    if (node.Depth >= MaxDepth) continue;
                    if (!state.Reachable.TryGetValue(node.Template, out var part)) continue;

                    for (var index = 0; index < part.Slots.Length; index++)
                    {
                        var occupant = Occupant(node, index);

                        // A part the quest named is not a candidate for improvement.
                        if (occupant is { Locked: true }) continue;

                        if (!BestSwap(weapon, root, node, part.Slots[index], index, occupant, state, buffer,
                                ref current, out var placed)) continue;

                        moved = true;
                        if (placed != null) queue.Enqueue(placed);
                        if (current <= 0d) return current;
                    }
                }

                if (!moved) break;
            }

            return current;
        }

        /// <summary>Tries every candidate for one slot, plus leaving it empty, and applies the best
        /// that beats the build already in hand. Returns whether anything changed.</summary>
        private bool BestSwap(
            MongoId weapon,
            Node root,
            Node node,
            WeaponGraph.SlotInfo slot,
            int index,
            Node? occupant,
            SearchState state,
            List<MongoId> buffer,
            ref double current,
            out Node? placed)
        {
            placed = null;

            // Out of the way first, so every candidate is measured against a gun that does NOT also
            // carry the part it would replace - and so that emptying the slot is itself a trial.
            if (occupant != null) Detach(occupant, state);

            var bestPenalty = current;
            Node? bestNode = null;
            var bestEmpty = false;

            if (occupant != null)
            {
                var empty = Penalty(weapon, root, state, buffer);

                if (empty < bestPenalty - MinGain)
                {
                    bestPenalty = empty;
                    bestEmpty = true;
                }
            }

            foreach (var candidate in slot.Candidates)
            {
                if (state.OutOfBudget()) break;
                if (occupant != null && candidate == occupant.Template) continue;
                if (state.Allowed != null && !state.Allowed.Contains(candidate)) continue;
                if (!state.Reachable.ContainsKey(candidate)) continue;
                if (IsAncestor(node, candidate)) continue;
                if (!Compatible(candidate, state)) continue;

                var trial = new Node
                {
                    Template = candidate,
                    SlotName = slot.Name,
                    SlotIndex = index,
                    Depth = node.Depth + 1
                };

                Attach(node, trial, state);
                Fill(trial, state, node.Depth + 1 + TrialFillDepth);

                var penalty = Penalty(weapon, root, state, buffer);

                Detach(trial, state);

                if (penalty >= bestPenalty - MinGain) continue;

                bestPenalty = penalty;
                bestNode = trial;
                bestEmpty = false;
            }

            if (bestNode != null)
            {
                Reattach(node, bestNode, state);

                current = bestPenalty;
                placed = bestNode;

                return true;
            }

            if (bestEmpty)
            {
                current = bestPenalty;
                return true;
            }

            // Nothing beat what was there, so put it back exactly as it was.
            if (occupant != null) Reattach(node, occupant, state);

            return false;
        }

        /// <summary>How far the assembled gun is from meeting every threshold, in units of the
        /// thresholds themselves. Zero is a build that passes.</summary>
        private double Penalty(MongoId weapon, Node root, SearchState state, List<MongoId> buffer)
        {
            buffer.Clear();
            CollectTemplates(root, buffer);

            var stats = model.Score(weapon, buffer);
            if (stats == null) return double.MaxValue;

            var total = 0d;

            foreach (var goal in state.Goals)
            {
                var actual = Read(stats, goal.Field);

                // No value is neither a pass nor a near miss: a magazine-capacity threshold on a gun
                // with no magazine is completely unmet.
                if (actual == null)
                {
                    total += MissingStatPenalty;
                    continue;
                }

                total += goal.Shortfall(actual.Value);
            }

            return total;
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

        // ---------------------------------------------------------------------------------------
        // The build, as a tree
        // ---------------------------------------------------------------------------------------

        /// <summary>One part on the gun. A tree rather than a list because a swap has to replace a
        /// part AND everything hanging off it, which a flat list cannot express.</summary>
        private sealed class Node
        {
            public MongoId Template;
            public string SlotName = "";

            /// <summary>The slot's position on its parent, not its name. Names are unique per part
            /// in the data, but occupancy is too load-bearing to rest on that.</summary>
            public int SlotIndex;

            public int Depth;
            public Node? Parent;
            public List<Node> Children { get; } = new();

            /// <summary>Placed by the planner to satisfy the quest's own part list. The climb may
            /// neither swap nor remove it.</summary>
            public bool Locked;

            /// <summary>Detached by a swap, and therefore no longer part of the build.</summary>
            public bool Removed;
        }

        private static Node? Occupant(Node node, int slotIndex)
        {
            foreach (var child in node.Children)
                if (child.SlotIndex == slotIndex) return child;

            return null;
        }

        private static void Attach(Node parent, Node child, SearchState state)
        {
            child.Parent = parent;
            parent.Children.Add(child);
            Count(child, state, 1);
        }

        /// <summary>Takes a subtree off the gun, leaving it intact so it can be put back if the swap
        /// that displaced it turns out not to be an improvement.</summary>
        private static void Detach(Node child, SearchState state)
        {
            child.Parent?.Children.Remove(child);
            Count(child, state, -1);
        }

        private static void Reattach(Node parent, Node child, SearchState state)
        {
            child.Parent = parent;
            parent.Children.Add(child);
            Count(child, state, 1);
        }

        /// <summary>Keeps the present-template counts in step with the tree, for the whole subtree.
        /// Counts rather than a set because the same template may legally be fitted twice - two
        /// identical rail mounts on one handguard is a build, not a mistake.</summary>
        private static void Count(Node node, SearchState state, int delta)
        {
            node.Removed = delta < 0;

            var count = state.Counts.GetValueOrDefault(node.Template) + delta;

            if (count <= 0) state.Counts.Remove(node.Template);
            else state.Counts[node.Template] = count;

            foreach (var child in node.Children)
                Count(child, state, delta);
        }

        /// <summary>Whether a part may coexist with everything already on the gun.</summary>
        private static bool Compatible(MongoId candidate, SearchState state)
        {
            if (!state.Reachable.TryGetValue(candidate, out var part)) return false;

            foreach (var conflict in part.Conflicts)
                if (state.Counts.ContainsKey(conflict)) return false;

            // Both directions. ConflictingItems is not reliably symmetric in the data, and a part
            // already on the gun naming this one is the same refusal as the reverse.
            foreach (var present in state.Counts.Keys)
                if (state.Reachable.TryGetValue(present, out var other) && other.Conflicts.Contains(candidate))
                    return false;

            return true;
        }

        /// <summary>Whether a template is already on the chain this node hangs from. Nothing in the
        /// data forbids a slot admitting its own host, and a part nested inside itself is both
        /// nonsense and a way to spend the whole depth budget on one chain.</summary>
        private static bool IsAncestor(Node node, MongoId template)
        {
            for (var step = node; step != null; step = step.Parent)
                if (step.Template == template) return true;

            return false;
        }

        private static Node? Find(Node node, MongoId template)
        {
            foreach (var child in node.Children)
            {
                if (child.Template == template) return child;

                var found = Find(child, template);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>Every fitted template, the weapon itself excluded - it is the thing being fitted
        /// to, and the model takes it separately.</summary>
        private static void CollectTemplates(Node node, List<MongoId> into)
        {
            foreach (var child in node.Children)
            {
                into.Add(child.Template);
                CollectTemplates(child, into);
            }
        }

        private static void CollectParts(Node node, List<FittedPart> into)
        {
            foreach (var child in node.Children)
            {
                into.Add(new FittedPart { SlotName = child.SlotName, Template = child.Template, Depth = child.Depth });
                CollectParts(child, into);
            }
        }

        private static void CollectNodes(Node node, Queue<Node> into)
        {
            into.Enqueue(node);

            foreach (var child in node.Children)
                CollectNodes(child, into);
        }

        // ---------------------------------------------------------------------------------------
        // Everything one search carries
        // ---------------------------------------------------------------------------------------

        private sealed class SearchState
        {
            public SearchState(
                IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> reachable,
                IReadOnlyCollection<MongoId>? allowed,
                List<Goal> goals,
                Stopwatch clock)
            {
                Reachable = reachable;
                Allowed = allowed;
                Goals = goals;
                Clock = clock;

                Index(reachable);
            }

            public IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> Reachable { get; }
            public IReadOnlyCollection<MongoId>? Allowed { get; }
            public List<Goal> Goals { get; }
            public Stopwatch Clock { get; }

            /// <summary>Points of this weapon's recoil one percent of mod recoil is worth.</summary>
            public double RecoilPerPercent { get; init; }

            /// <summary>Slots from the weapon to each part, by the shortest chain. Computed once
            /// because it does not depend on which part is being routed to.</summary>
            public Dictionary<MongoId, int> Distance { get; } = new();

            /// <summary>Parts that have a slot admitting a given part - the slot graph reversed, so
            /// a route can be walked backwards from a target rather than forwards from the weapon
            /// over everything. Nearest the weapon first, so the shortest routes are found
            /// first.</summary>
            public Dictionary<MongoId, List<MongoId>> Hosts { get; } = new();

            /// <summary>How many of each template the build currently carries.</summary>
            public Dictionary<MongoId, int> Counts { get; } = new();

            public int Nodes;
            public bool Exhausted;

            public void ResetTree(MongoId weapon)
            {
                Counts.Clear();
                Counts[weapon] = 1;
            }

            public bool OutOfBudget()
            {
                if (Exhausted) return true;
                if (++Nodes < NodeCeiling && Clock.Elapsed < TimeCeiling) return false;

                Exhausted = true;
                return true;
            }

            /// <summary>Distances and reverse edges, in one pass each over the reachable set.</summary>
            private void Index(IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> reachable)
            {
                foreach (var (id, part) in reachable)
                    foreach (var slot in part.Slots)
                        foreach (var candidate in slot.Candidates)
                        {
                            if (!reachable.ContainsKey(candidate)) continue;

                            if (!Hosts.TryGetValue(candidate, out var hosts))
                                Hosts[candidate] = hosts = new List<MongoId>();

                            if (!hosts.Contains(id)) hosts.Add(id);
                        }
            }

            /// <summary>Breadth-first distances from the weapon. Separate from Index because it
            /// needs to know which part is the weapon, and the reverse edges do not.</summary>
            public void Measure(MongoId weapon)
            {
                Distance.Clear();
                Distance[weapon] = 0;

                var queue = new Queue<MongoId>();
                queue.Enqueue(weapon);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var depth = Distance[current];

                    if (depth >= MaxDepth) continue;
                    if (!Reachable.TryGetValue(current, out var part)) continue;

                    foreach (var slot in part.Slots)
                        foreach (var candidate in slot.Candidates)
                        {
                            if (!Distance.TryAdd(candidate, depth + 1)) continue;

                            queue.Enqueue(candidate);
                        }
                }

                foreach (var hosts in Hosts.Values)
                    hosts.Sort((left, right) => Depth(left).CompareTo(Depth(right)));
            }

            private int Depth(MongoId template) =>
                Distance.TryGetValue(template, out var depth) ? depth : int.MaxValue;
        }
    }
}
