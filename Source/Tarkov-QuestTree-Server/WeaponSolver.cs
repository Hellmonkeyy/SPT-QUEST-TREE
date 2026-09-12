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

        /// <summary>Slots past the shortest chain the planner may spend routing to a required part.
        /// Two covers "scope on a riser on a mount" where "scope on a mount" is shortest and the
        /// mount's slot is already taken.</summary>
        private const int RouteSlack = 2;

        /// <summary>Sweeps of the climb. It stops on its own as soon as a sweep finds no move, so
        /// this only bounds a pathological oscillation.</summary>
        private const int MaxSweeps = 24;

        /// <summary>Restarts of the dress-and-climb. The first two are deterministic - a greedily
        /// dressed gun, and a gun wearing nothing but what the quest demands - and the rest randomise
        /// the dressing.
        ///
        /// Randomising is the cheapest way out of a local optimum, and single-slot moves leave real
        /// ones: the KRISS Vector can reach 221 recoil, 79 ergonomics, 800 metres and a 100-round
        /// magazine, each on its own, and one climb from one dressing settled at 232 against a
        /// threshold of 230. A different starting point is worth more there than a longer look at
        /// this one.</summary>
        private const int Attempts = 16;

        /// <summary>How many candidates for one slot get their own slots chosen against the model
        /// rather than by the heuristic. Three, because the cost of a sweep is multiplied by this and
        /// the heuristic is wrong about the ORDER of good candidates far less often than it is wrong
        /// about whether a part is worth fitting at all.</summary>
        private const int PolishCandidates = 3;

        /// <summary>One in this many slots a randomised dressing leaves empty on purpose. Weight and
        /// recoil thresholds are met by NOT fitting things, and a dressing that fills every hole it
        /// can never starts the climb anywhere near that.</summary>
        private const int SkipOneSlotIn = 3;

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

        /// <summary>Most headroom one met threshold may contribute, as a fraction of itself. Capped so
        /// that a gun with 200 ergonomics against a threshold of 15 cannot outvote four other
        /// thresholds sitting on the line.</summary>
        private const double HeadroomCap = 1d;

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

            var required = new List<MongoId>();
            foreach (var id in mustInclude)
                if (id != weapon && !required.Contains(id)) required.Add(id);

            Node? best = null;
            var bestCost = double.PositiveInfinity;

            // Many starting points, because a climb is a local search and which optimum it reaches
            // depends on where it starts. A greedily dressed gun is the better start most of the
            // time; a gun wearing nothing but what the quest demands wins when the thresholds want
            // LESS of something, where every part the dressing adds is a step away from the answer;
            // and past those two, a randomised dressing is what gets out of a basin a single-slot
            // move cannot leave.
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var root = new Node { Template = weapon, Locked = true };

                state.ResetTree(weapon);

                // Seeded by the attempt alone, so the same request always returns the same build. A
                // solver that answers differently on each boot cannot be measured, and a player
                // comparing two panels would be told two different things.
                state.Shuffle = attempt < 2 ? null : new Random(attempt);

                Plan(root, required, state);

                if (attempt != 1) Fill(root, state, MaxDepth);

                // The randomness belongs to the dressing only. The climb fills the sub-slots of every
                // candidate it tries, and a random fill there would have it judging parts by a throw
                // of the dice rather than by what they are worth.
                state.Shuffle = null;

                var climbed = Climb(weapon, root, state);
                var missing = required.Count(part => Find(root, part) == null);
                var cost = climbed.Shortfall + missing * MissingPartPenalty;

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

        /// <summary>Places every required part, backtracking when two of them want the same slot.
        ///
        /// The first version of this marked every template on a shortest route as "forced" and let
        /// the greedy take any forced part it met, which loses two ways: two required parts whose
        /// shortest routes share one rail, and a forced part claimed by the first slot that happened
        /// to admit it rather than the slot its own route meant.
        ///
        /// The second version chose whole routes from the bare weapon, together, with backtracking -
        /// and still lost the AKS-74N, because a route list computed before anything is placed does
        /// not know that another part's route has already fitted the very handguard this part hangs
        /// off. There are more near-shortest routes through the graph than any cap will hold, and the
        /// one that reused it simply was not in the list.
        ///
        /// So routing happens against the gun AS IT STANDS. Each required part carries a map of how
        /// many slots every template is from it, which makes "extend from here" a local decision and
        /// makes a node already on the gun, one slot away, the first thing tried.</summary>
        private void Plan(Node root, List<MongoId> required, SearchState state)
        {
            if (required.Count == 0) return;

            var order = new List<PlanPart>(required.Count);

            foreach (var part in required)
                order.Add(new PlanPart { Part = part, Steps = StepsTo(part, state) });

            foreach (var part in order)
            {
                part.Seats = state.Hosts.TryGetValue(part.Part, out var hosts) ? hosts.Count : 0;
                part.Carries = order.Count(other => other != part && other.Steps.ContainsKey(part.Part));
            }

            // A part that can CARRY another required part goes first: seat the foregrip first and it
            // picks some other handguard, and the handguard the quest named then has nowhere to go.
            // After that, fewest places to sit first - the Zenit PT-3 stock has exactly two hosts in
            // the whole game and the Klesch illuminator has a hundred, and the one with two must not
            // be asked to work around the one with a hundred.
            order.Sort((left, right) =>
            {
                var carries = right.Carries.CompareTo(left.Carries);
                return carries != 0 ? carries : left.Seats.CompareTo(right.Seats);
            });

            if (PlanFrom(root, order, 0, state)) return;

            // Seating them all together failed. Seat what can be seated, one at a time, so the gun is
            // still the right shape for the rest and the report names the part that would not fit
            // rather than every part the unwinding took back off.
            foreach (var part in order)
            {
                if (Find(root, part.Part) != null) continue;

                var alone = new List<PlanPart> { part };

                foreach (var seat in Seatings(root, part))
                    if (Extend(seat, part.Steps[seat.Template] + RouteSlack, root, alone, 0, state)) break;
            }
        }

        private bool PlanFrom(Node root, List<PlanPart> order, int index, SearchState state)
        {
            if (index >= order.Count) return true;
            if (state.OutOfBudget()) return false;

            var part = order[index];

            // An earlier part's chain may already have brought this one along: two required parts on
            // one chain share it rather than competing for it.
            if (Find(root, part.Part) != null) return PlanFrom(root, order, index + 1, state);

            foreach (var seat in Seatings(root, part))
                if (Extend(seat, part.Steps[seat.Template] + RouteSlack, root, order, index, state)) return true;

            return false;
        }

        /// <summary>Where on the gun as it stands a part could be routed from, fewest slots away
        /// first. Nearest wins because a node already fitted one slot from the part gives both the
        /// shortest chain and the one that spends no slot the rest of the build needs.</summary>
        private static List<Node> Seatings(Node root, PlanPart part)
        {
            var seats = new List<Node>();

            CollectSeats(root, part.Steps, seats);
            seats.Sort((left, right) => part.Steps[left.Template].CompareTo(part.Steps[right.Template]));

            return seats;
        }

        /// <summary>Builds a chain of slots from one node down to the required part and hands the gun
        /// on to the next part, undoing its own work on the way back out - so a part that cannot be
        /// seated leaves no trace, and a part sitting where another one needed to be can be moved.
        ///
        /// <paramref name="remaining"/> is how many slots the chain may still spend. It starts at the
        /// shortest chain plus <see cref="RouteSlack"/>, because the shortest is sometimes taken and
        /// the way round is a rail longer.</summary>
        private bool Extend(Node node, int remaining, Node root, List<PlanPart> order, int index, SearchState state)
        {
            if (state.OutOfBudget()) return false;
            if (remaining <= 0 || node.Depth >= MaxDepth) return false;
            if (!state.Reachable.TryGetValue(node.Template, out var host)) return false;

            var part = order[index];
            var steps = new List<(int Slot, MongoId Candidate, int Left, double Worth, int Toss)>();

            for (var slot = 0; slot < host.Slots.Length; slot++)
            {
                if (Occupant(node, slot) != null) continue;

                foreach (var candidate in host.Slots[slot].Candidates)
                {
                    // Anything that cannot reach the part, or cannot reach it inside what is left of
                    // the chain, is not a step towards it.
                    if (!part.Steps.TryGetValue(candidate, out var left) || left >= remaining) continue;

                    steps.Add((slot, candidate, left, Potential(candidate, state, 0),
                        state.Shuffle?.Next() ?? 0));
                }
            }

            // The best step first, WORTH before distance, because where a chain runs is a stats
            // decision and not only a routing one - and a chain the planner lays is locked against the
            // climb, so a step chosen badly here cannot be recovered later.
            //
            // Both of the last two failures were this. The M1A hides the UltiMAK mount's seat on its
            // stock, so the stock the chain runs through is the stock the gun wears, and the nearest
            // one was 12 ergonomics and 2% recoil worse than the chassis beside it. The ASh-12 needs
            // its required foregrip fitted to the polymer handguard rather than straight into the slot
            // that admits them both - the handguard is +5 ergonomics, the quest wants 40, and the best
            // build there reaches exactly 40. Shortest-route-first could not see either.
            //
            // Distance still ranks, below worth: an equally good step that spends fewer slots leaves
            // more of the gun for the rest of the build. So does narrowest slot, last - a slot taking
            // three things is the one the part was made for, and spending a rail that admits eighty
            // denies it to a part with nowhere else to go.
            steps.Sort((left, right) =>
            {
                var tossed = left.Toss.CompareTo(right.Toss);
                if (tossed != 0) return tossed;

                var worth = right.Worth.CompareTo(left.Worth);
                if (worth != 0) return worth;

                var closer = left.Left.CompareTo(right.Left);
                if (closer != 0) return closer;

                return host.Slots[left.Slot].Candidates.Length.CompareTo(host.Slots[right.Slot].Candidates.Length);
            });

            foreach (var (slot, candidate, left, _, _) in steps)
            {
                if (IsAncestor(node, candidate)) continue;
                if (!Compatible(candidate, state)) continue;

                var child = new Node
                {
                    Template = candidate,
                    SlotName = host.Slots[slot].Name,
                    SlotIndex = slot,
                    Depth = node.Depth + 1,
                    Locked = true
                };

                Attach(node, child, state);

                if (left == 0
                        ? PlanFrom(root, order, index + 1, state)
                        : Extend(child, remaining - 1, root, order, index, state))
                    return true;

                Detach(child, state);
            }

            return false;
        }

        /// <summary>How many slots each template is from one part, over the reversed slot graph.
        ///
        /// Computed once per required part, and then it answers every routing question about that
        /// part from anywhere on the gun - which is what lets the planner route against the tree it
        /// has rather than against a list of routes decided before the tree existed.</summary>
        private static Dictionary<MongoId, int> StepsTo(MongoId target, SearchState state)
        {
            var steps = new Dictionary<MongoId, int> { [target] = 0 };
            var queue = new Queue<MongoId>();

            queue.Enqueue(target);

            // Breadth-first, and the dictionary is the visited set: a cyclic slot graph meets a
            // template it has already measured and stops instead of looping.
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var depth = steps[current];

                if (depth >= MaxDepth) continue;
                if (!state.Hosts.TryGetValue(current, out var hosts)) continue;

                foreach (var host in hosts)
                {
                    if (!steps.TryAdd(host, depth + 1)) continue;

                    queue.Enqueue(host);
                }
            }

            return steps;
        }

        private static void CollectSeats(Node node, Dictionary<MongoId, int> steps, List<Node> into)
        {
            if (steps.TryGetValue(node.Template, out var left) && left >= 1) into.Add(node);

            foreach (var child in node.Children)
                CollectSeats(child, steps, into);
        }

        /// <summary>One required part, with everything the planner precomputes about it.</summary>
        private sealed class PlanPart
        {
            public MongoId Part;

            /// <summary>Slots from each template down to this part.</summary>
            public Dictionary<MongoId, int> Steps = new();

            /// <summary>Reachable parts with a slot that admits this one.</summary>
            public int Seats;

            /// <summary>Other required parts that could hang off this one.</summary>
            public int Carries;
        }

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
            var shortlist = state.Shortlist;
            shortlist.Clear();

            foreach (var candidate in slot.Candidates)
            {
                if (state.Allowed != null && !state.Allowed.Contains(candidate)) continue;
                if (!state.Reachable.ContainsKey(candidate)) continue;
                if (IsAncestor(parent, candidate)) continue;
                if (!Compatible(candidate, state)) continue;

                shortlist.Add((candidate, Potential(candidate, state, 0)));
            }

            if (shortlist.Count == 0) return null;

            if (state.Shuffle == null)
            {
                var best = shortlist[0];

                foreach (var entry in shortlist)
                    if (entry.Score > best.Score) best = entry;

                // An empty slot is a legitimate choice: a part that helps nothing measured is weight
                // for free, and weight is a threshold in its own right.
                return best.Score > 0d ? best.Template : (MongoId?)null;
            }

            if (state.Shuffle.Next(SkipOneSlotIn) == 0) return null;

            // EVERY legal candidate, not the ones the heuristic likes. That distinction is what
            // cracked the M1A: an M14 suppressor scores badly on its own - it costs a great deal of
            // ergonomics for its recoil - so it never appeared in any dressing, and the climb only
            // ever saw it as a single move that made things worse. A dressing that starts with it
            // already fitted is a different basin, and the one with the answer in it.
            return shortlist[state.Shuffle.Next(shortlist.Count)].Template;
        }

        /// <summary>What a part is worth INCLUDING everything it lets you fit behind it.
        ///
        /// This is not an embellishment of <see cref="Score"/>, it is the difference between finding
        /// the M1A's build and not. An AR-15 buffer tube has no ergonomics, no recoil and no weight
        /// worth naming, so it scores zero on its own and a dressing that asks "does this part help?"
        /// never fits one - and the stock worth -24% recoil that only mounts on a buffer tube is then
        /// unreachable, at every depth, in every restart. The gun sat at -25% recoil with -36% on the
        /// shelf behind an adapter nothing would fit.
        ///
        /// Optimistic on purpose: it assumes every slot behind the part gets its best occupant, which
        /// conflicts and slot contention may deny. That is the right bias for a dressing whose whole
        /// job is to hand the climb somewhere worth standing.</summary>
        private static double Potential(MongoId template, SearchState state, int depth)
        {
            if (depth > MaxDepth) return 0d;
            if (state.Potentials.TryGetValue(template, out var cached)) return cached;
            if (!state.Reachable.TryGetValue(template, out var part)) return 0d;

            // Marked BEFORE descending, so a slot graph that admits its own host meets the marker and
            // contributes nothing instead of looping. The memo doubles as the visited set, exactly as
            // it does in the graph's own walk.
            state.Potentials[template] = 0d;

            var total = Score(part, state);

            foreach (var slot in part.Slots)
            {
                var best = 0d;

                foreach (var candidate in slot.Candidates)
                {
                    var value = Potential(candidate, state, depth + 1);
                    if (value > best) best = value;
                }

                total += best;
            }

            state.Potentials[template] = total;

            return total;
        }

        /// <summary>How much one part helps BY ITSELF, summed over the goals, each in units of its own
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
        /// improvement, and returns the cost of the build it settles on. A zero shortfall means every
        /// threshold is met.</summary>
        private Cost Climb(MongoId weapon, Node root, SearchState state)
        {
            var buffer = new List<MongoId>();
            var current = Measure(weapon, root, state, buffer);

            for (var sweep = 0; sweep < MaxSweeps && current.Shortfall > 0d; sweep++)
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
                                ref current, refine: true, out var placed)) continue;

                        moved = true;
                        if (placed != null) queue.Enqueue(placed);
                        if (current.Shortfall <= 0d) return current;
                    }
                }

                if (!moved) break;
            }

            return current;
        }

        /// <summary>Tries every candidate for one slot, plus leaving it empty, and applies the best
        /// that beats the build already in hand. Returns whether anything changed.
        ///
        /// <paramref name="refine"/> buys the few most promising candidates a second look with their
        /// OWN slots chosen against the model rather than by the heuristic. Without it the M1A cannot
        /// be solved at all: the recoil it needs lives on a muzzle device, the muzzle device mounts on
        /// a barrel, and a barrel by itself is nothing but weight - so the barrel is judged as weight,
        /// rejected, and everything threaded onto it is unreachable at every depth in every restart.
        /// The heuristic cannot see past that, because it cannot know that a suppressor's 15 points of
        /// ergonomics are affordable when ergonomics is met and recoil is 64 short.</summary>
        private bool BestSwap(
            MongoId weapon,
            Node root,
            Node node,
            WeaponGraph.SlotInfo slot,
            int index,
            Node? occupant,
            SearchState state,
            List<MongoId> buffer,
            ref Cost current,
            bool refine,
            out Node? placed)
        {
            placed = null;

            // Out of the way first, so every candidate is measured against a gun that does NOT also
            // carry the part it would replace - and so that emptying the slot is itself a trial.
            if (occupant != null) Detach(occupant, state);

            var best = current;
            Node? bestNode = null;
            var bestEmpty = false;

            if (occupant != null)
            {
                var empty = Measure(weapon, root, state, buffer);

                if (empty.Beats(best))
                {
                    best = empty;
                    bestEmpty = true;
                }
            }

            // Candidates worth the closer look, and only a few: refining every candidate of every
            // slot would multiply the cost of a sweep by the size of a subtree.
            var promising = refine ? new List<(Node Trial, Cost Cost)>(PolishCandidates + 1) : null;

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

                // Filled before it is judged. A mount is worth nothing by itself and everything with
                // a scope on it, so a trial that weighed parts bare would reject every adapter ever
                // made. Cheap, because the fill uses the per-part heuristic rather than the model.
                Fill(trial, state, MaxDepth);

                var cost = Measure(weapon, root, state, buffer);

                Detach(trial, state);

                if (promising != null) Remember(promising, trial, cost);

                if (!cost.Beats(best)) continue;

                best = cost;
                bestNode = trial;
                bestEmpty = false;
            }

            if (promising != null)
                foreach (var (trial, _) in promising)
                {
                    if (state.OutOfBudget()) break;

                    Reattach(node, trial, state);

                    var cost = Measure(weapon, root, state, buffer);
                    Polish(weapon, root, trial, state, buffer, ref cost);

                    Detach(trial, state);

                    if (!cost.Beats(best)) continue;

                    best = cost;
                    bestNode = trial;
                    bestEmpty = false;
                }

            if (bestNode != null)
            {
                Reattach(node, bestNode, state);

                current = best;
                placed = bestNode;

                return true;
            }

            if (bestEmpty)
            {
                current = best;
                return true;
            }

            // Nothing beat what was there, so put it back exactly as it was.
            if (occupant != null) Reattach(node, occupant, state);

            return false;
        }

        /// <summary>Chooses the occupants of one part's own slots against the model instead of the
        /// heuristic. One level deep, which is all that is needed: the part's children are dressed by
        /// <see cref="Potential"/>, which already looks through an adapter to what mounts on it - what
        /// it cannot do is judge a part whose own numbers look bad and whose effect on THIS gun is
        /// good.</summary>
        private void Polish(
            MongoId weapon, Node root, Node node, SearchState state, List<MongoId> buffer, ref Cost cost)
        {
            if (node.Depth >= MaxDepth) return;
            if (!state.Reachable.TryGetValue(node.Template, out var part)) return;

            for (var index = 0; index < part.Slots.Length; index++)
            {
                if (state.OutOfBudget()) return;

                var occupant = Occupant(node, index);
                if (occupant is { Locked: true }) continue;

                BestSwap(weapon, root, node, part.Slots[index], index, occupant, state, buffer,
                    ref cost, refine: false, out _);
            }
        }

        /// <summary>Keeps the best few trials seen, by cost.</summary>
        private static void Remember(List<(Node Trial, Cost Cost)> promising, Node trial, Cost cost)
        {
            promising.Add((trial, cost));

            if (promising.Count <= PolishCandidates) return;

            var worst = 0;

            for (var i = 1; i < promising.Count; i++)
                if (promising[worst].Cost.Beats(promising[i].Cost)) worst = i;

            promising.RemoveAt(worst);
        }

        /// <summary>What the assembled gun costs, in units of the thresholds themselves.</summary>
        private Cost Measure(MongoId weapon, Node root, SearchState state, List<MongoId> buffer)
        {
            buffer.Clear();
            CollectTemplates(root, buffer);

            var stats = model.Score(weapon, buffer);
            if (stats == null) return new Cost(double.MaxValue, 0d);

            var shortfall = 0d;
            var headroom = 0d;

            foreach (var goal in state.Goals)
            {
                var actual = Read(stats, goal.Field);

                // No value is neither a pass nor a near miss: a magazine-capacity threshold on a gun
                // with no magazine is completely unmet.
                if (actual == null)
                {
                    shortfall += MissingStatPenalty;
                    continue;
                }

                var margin = goal.Margin(actual.Value) / goal.Scale;

                if (margin < 0d) shortfall += -margin;
                else headroom += Math.Min(margin, HeadroomCap);
            }

            return new Cost(shortfall, headroom);
        }

        /// <summary>What a build is worth to the climb: how far short of the thresholds it falls, and
        /// how much room to spare it has on the ones it already meets.
        ///
        /// The headroom half is not a nicety, it is what unsticks the M1A. Shortfall alone gives a met
        /// threshold no pull at all, so the climb spends every point of spare ergonomics on whatever
        /// else it is chasing - and then the muzzle device that would fix recoil costs more ergonomics
        /// than is left, no single move improves anything, and the gun settles at its BARE recoil with
        /// the threshold 64 points away. Ranked strictly below shortfall, so a build that passes always
        /// beats one that does not, however roomy.</summary>
        private readonly struct Cost(double shortfall, double headroom)
        {
            /// <summary>Summed distance from the thresholds not met, each as a fraction of its own
            /// threshold. Zero is a build that passes.</summary>
            public double Shortfall { get; } = shortfall;

            /// <summary>Summed room to spare on the thresholds that are met, each capped so one very
            /// slack threshold cannot outvote the rest.</summary>
            public double Headroom { get; } = headroom;

            public bool Beats(in Cost other)
            {
                if (Shortfall < other.Shortfall - MinGain) return true;
                if (Shortfall > other.Shortfall + MinGain) return false;

                return Headroom > other.Headroom + MinGain;
            }
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

            /// <summary>Parts with a slot admitting a given part - the slot graph reversed. It is
            /// what makes "how far is this template from that part" answerable by one walk per
            /// required part instead of one per question.</summary>
            public Dictionary<MongoId, List<MongoId>> Hosts { get; } = new();

            /// <summary>How many of each template the build currently carries.</summary>
            public Dictionary<MongoId, int> Counts { get; } = new();

            /// <summary>Set while a restart is dressing the gun randomly, null the rest of the time.
            /// Seeded from the attempt number, never from the clock.</summary>
            public Random? Shuffle;

            /// <summary>Reused by the dressing, which runs once per slot per restart and would
            /// otherwise allocate a list each time.</summary>
            public List<(MongoId Template, double Score)> Shortlist { get; } = new();

            /// <summary>What each part is worth with everything behind it, memoised. Depends only on
            /// the goals, so one pass over the graph serves every restart of one request.</summary>
            public Dictionary<MongoId, double> Potentials { get; } = new();

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

            /// <summary>The reverse edges, in one pass over the reachable set.</summary>
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

        }
    }
}
