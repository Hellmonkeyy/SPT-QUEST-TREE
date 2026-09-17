using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
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
    /// WHAT IT DOES INSTEAD - three stages, restarted, and the split is the whole design
    ///
    ///   PLAN     The parts the quest NAMES are placed first, into an explicit tree, and LOCKED.
    ///            These are not preferences and must not compete with stats for a slot. Routing is
    ///            against the tree as it stands rather than against routes chosen in advance, so a
    ///            chain one part paid for is reused by the next instead of duplicated.
    ///   DRESS/CLIMB both work against a cost whose FIRST rank is whole requirements rather than
    ///            numbers: a slot the game marks Required and will not let the player leave empty,
    ///            and a category the quest names - "must include a Silencer", 16 of the 32 vanilla
    ///            conditions - with nothing on the gun from it. Neither is a degree of anything, and
    ///            a build that trades either for a better number is one that cannot be handed in.
    ///   DRESS    Every remaining slot is filled greedily, by a per-part heuristic that counts what
    ///            a part lets you fit BEHIND it as well as what it is. A starting point, not an
    ///            answer.
    ///   CLIMB    Every unlocked slot is then re-examined against the REAL stat model: swap the
    ///            part, swap it for nothing, rescore the whole gun, keep what helps - and give the
    ///            best few candidates a second look with their own slots chosen the same way.
    ///            Repeat until a sweep finds no improvement.
    ///
    /// The climb is why the heuristic does not have to be clever, and the heuristic is why the climb
    /// has anything to work with. A per-part score cannot know that a suppressor's -21 ergonomics is
    /// affordable when recoil is the binding threshold and ergonomics has room; scoring the assembled
    /// gun knows exactly that. Equally, a climb that judges one part at a time cannot discover a part
    /// worth fitting only for what mounts on it - an M1A barrel is nothing but weight until a muzzle
    /// brake is threaded onto it - which is what the look-behind heuristic and the second look are for.
    ///
    /// All three are restarted from different dressings, because a climb is a local search: which
    /// optimum it reaches depends on where it starts, and the interesting failures are builds no
    /// single-slot move can leave. The restarts are seeded from the attempt number, so the same
    /// request always gets the same build.
    ///
    /// The ceiling is an ANSWER, not a safety net. "No build found within the search budget" is a
    /// different statement from "no build exists", and conflating the two would be a failure that
    /// looks like an answer.
    ///
    /// The measure of all of it: 60 of the 60 weapon-build requirements on the reference install,
    /// vanilla and modded, in 217 ms for all sixty - every threshold met, every part and category the
    /// quest names present, and no required slot left empty, so each one is a gun the player can
    /// actually assemble. Enforcing the last two cost nothing: before they were enforced the same
    /// search scored 60 and only 33 of those builds were assemblable and complete.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponSolver(
        ISptLogger<WeaponSolver> logger,
        WeaponGraph graph,
        WeaponStatModel model,
        ItemHelper itemHelper,
        WeaponPresets presets)
    {
        /// <summary>Nodes the search may open before it gives up and reports what it has.
        ///
        /// Sized for the shrink loop, which is the expensive part: a full round of restarts for every
        /// part it tries to remove, and most of those rounds end in "no". The worst request on this
        /// install spends 375,148, and a ceiling just above that would make the answer depend on how
        /// busy the machine is - the one thing a search reporting a measured result must not do.</summary>
        private const int NodeCeiling = 2_000_000;

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

        /// <summary>Rounds of shedding, bypassing and condensing. Each round that changes anything
        /// takes a part off, so this only bounds a build that started implausibly large.</summary>
        private const int PruneRounds = 8;

        /// <summary>How many candidates per slot the condensing pass will try as a replacement for two
        /// parts. It is pairs-of-parts times homes times this, which makes it the largest thing in the
        /// solver, and the best few by the look-behind heuristic are where a single part that does the
        /// work of two is actually found.</summary>
        private const int CondenseCandidates = 8;

        /// <summary>Improvement a move must make to be taken. Guards the climb against oscillating
        /// on floating point noise.</summary>
        private const double MinGain = 1e-6;

        /// <summary>How many required categories one condition may name. Three is the most any
        /// condition on the reference install uses; 30 is the width of the mask that tracks them.</summary>
        private const int MaxCategories = 30;

        /// <summary>Most headroom one met threshold may contribute, as a fraction of itself. Capped so
        /// that a gun with 200 ergonomics against a threshold of 15 cannot outvote four other
        /// thresholds sitting on the line.</summary>
        private const double HeadroomCap = 1d;

        /// <summary>The cap for a goal named as BINDING - one a previous search found sitting on the line,
        /// and therefore the reason the build cannot lose a part.
        ///
        /// Slack is what pays for a removal. A build meeting recoil exactly cannot drop the part that bought
        /// the last two points, however spare everything else is, and the ordinary cap tells the climb to
        /// stop caring about recoil the moment it is met - so it never accumulates what the next pruning
        /// pass would have to spend. Raising the cap on that one goal asks for the same gun with room to
        /// spare where the room is worth something.
        ///
        /// Safe because of where headroom sits in Cost: BELOW parts. A build carrying an extra part can
        /// never win on headroom however slack it is, so this cannot bring back the seventeen-part AKS-74N.
        /// It can only choose between builds of the same size.</summary>
        private const double BindingHeadroomCap = 4d;

        /// <summary>How little room to spare counts as sitting on the line, as a fraction of the threshold.
        /// Five percent: recoil met at 605 against 610 is binding, met at 520 is not.</summary>
        private const double BindingSlack = 0.05d;

        public sealed class FittedPart
        {
            public string SlotName { get; init; } = "";
            public MongoId Template { get; init; }
            public int Depth { get; init; }

            /// <summary>Position in Parts of the part this one is fitted TO, or -1 for one fitted
            /// straight to the weapon.
            ///
            /// Here so that something other than the search can check the build. A flat list of
            /// templates and slot names cannot say which slot on which INSTANCE a part occupies, so
            /// "is this part even allowed in that slot" and "is that slot already taken" are both
            /// unanswerable from it - and two copies of one template make the question sharper, not
            /// softer.</summary>
            public int Parent { get; init; } = -1;
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

            /// <summary>Parts that are not what the weapon ships with: a swap for a different part in a
            /// slot the default preset fills, plus an addition to a slot it leaves empty.
            ///
            /// NOT the objective, despite saying so here for several releases. Cost is - see below - and
            /// Cost.Beats ranks price first and part count second, reading Changes nowhere at all. This is
            /// the count the price is over, and it is reported because a player reads "four changes" more
            /// easily than a rouble total; it decides nothing.
            ///
            /// Minimise changes, never "maximise defaults kept": the second sounds the same and is a trap,
            /// because keeping every stock part while bolting ten more on scores perfectly by it. Counting
            /// what has to be BOUGHT is self-limiting - covering for a kept-but-poor default with three
            /// purchases costs more than swapping the default once.
            ///
            /// Zero when the weapon has no default preset, which is not a claim that nothing changed: it is
            /// the absence of anything to compare against, and the objective then collapses to the part
            /// count, which is what the two agree on exactly when there is no preset to differ about.</summary>
            public int Changes { get; set; }

            /// <summary>THE OBJECTIVE, in roubles: the price of every part the player has to obtain plus
            /// Pricing.PerPurchase for each one, with parts on the default preset and parts the caller lists
            /// as free costing nothing. Changes stays beside it as the count the price is over.</summary>
            public long Cost { get; set; }

            /// <summary>Purchases charged with no price behind them - a part the price table does not know.
            /// Charged PerPurchase alone, and counted here so an unpriced build is never mistaken for a
            /// cheap one.</summary>
            public int Unpriced { get; set; }

            /// <summary>Thresholds this build MEETS with almost nothing to spare.
            ///
            /// Why a build cannot get smaller is nearly always one or two of these, and it was computed on
            /// every measurement and thrown away. Recorded here, remembered beside the build, and handed
            /// back to the next search as the number worth buying slack on - which is the difference
            /// between exploring uniformly and exploring where the answer is.</summary>
            public List<string> Binding { get; } = new();

            /// <summary>The size of ONE mandatory skeleton: what the planner had to place, the slots the
            /// game will not leave empty, and one part for each category still unaccounted for.
            ///
            /// NOT a lower bound, and it was reported as one until the data said otherwise - Gunsmith 18
            /// comes in at 9 parts against a skeleton of 10, because a different plan and different
            /// occupants make a different skeleton. It is what the search stops at, which is all it was
            /// ever entitled to be. The only real bound lives in WeaponBuildVerifier, which argues from
            /// the item data instead of from one arrangement of it.</summary>
            public int Floor { get; set; }

            public int NodesOpened { get; set; }
        }

        /// <summary>What a part costs the player this search is for, and what a trip to get one is worth
        /// avoiding.
        ///
        /// THE OBJECTIVE IS price + PerPurchase x purchases. Two quantities in one currency, so money and
        /// errands can be compared at all: at PerPurchase zero a build of thirty cheap parts beats one of two
        /// dear ones; at fifty thousand the count dominates and price barely matters. The shared baseline is
        /// priced from the handbook - static, profile-blind, the same for everyone, so its history ships;
        /// a profile's own pass is priced from its traders, its flea and its stash.
        ///
        /// A part on the weapon's default preset costs nothing; so does one in Free, which is how a caller
        /// says "loose in the stash". A part with no price is charged PerPurchase alone and COUNTED as
        /// unpriced: returning zero for it would make the search prefer exactly the parts it knows least
        /// about, and the count is what stops that reading as cheap.</summary>
        public sealed class Pricing
        {
            public Func<MongoId, long?> Price { get; init; } = _ => null;

            public long PerPurchase { get; init; }

            public IReadOnlyCollection<MongoId>? Free { get; init; }
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

        /// <summary>Stats this solver scores. Anything else a quest constrains is reported as unchecked
        /// rather than assumed to pass - base accuracy, muzzle velocity and the empty-tactical-slot count
        /// are real constraints nothing here can see.
        ///
        /// Height and width ARE in this set, and used to be named above as things it could not see. They
        /// are not scored by the stat model - see WeaponStatModel, which still cannot - but by Sized below,
        /// off the assembled grid. The comment outlived the code that made it true.</summary>
        private static readonly HashSet<string> Scored = new(StringComparer.OrdinalIgnoreCase)
        {
            "ergonomics", "recoil", "weight", "magazine capacity", "effective distance", "height", "width"
        };

        /// <summary>Goals measured from the assembled grid rather than from the stat model.
        ///
        /// Computed here as well as in the verifier, in separate code - but NOT independently, and the
        /// difference matters. Both are the same algorithm: max per direction, ExtraSizeForceAdd stacking,
        /// no reduction. They agree because they were written to agree, so neither catches the other being
        /// wrong about the RULE - only about applying it. The one real difference is that the verifier also
        /// computes the reduced extent and reports it, which is the half that adds information.
        ///
        /// Worth knowing before trusting "the solver and the verifier both say so" about a size threshold.</summary>
        private static readonly HashSet<string> Sized = new(StringComparer.OrdinalIgnoreCase)
        {
            "height", "width"
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
            IReadOnlyCollection<MongoId> mustIncludeCategories,
            IReadOnlyCollection<MongoId>? allowed,
            IReadOnlyList<FittedPart>? knownGood = null,
            int seed = 0,
            int restarts = 0,
            int ceiling = 0,
            IReadOnlyCollection<string>? binding = null,
            Pricing? pricing = null)
        {
            var result = new Result();

            var reachable = graph.Reachable(weapon, out var notes);

            if (reachable == null)
            {
                logger.Debug($"Quest Tracker: cannot solve for '{weapon}' - {string.Join("; ", notes)}.");
                return result;
            }

            var goals = Goals(thresholds, result);

            // The bare weapon, scored once. Recoil is why: a mod moves recoil by a PERCENTAGE, so
            // "-8%" is worth eight percent of THIS weapon's recoil - 52 points on a P226 at 651 and
            // 20 on an AK-102 at 254 - and comparing that raw percentage against ergonomics points
            // is what made the old ordering leave muzzle brakes on the floor.
            var bare = model.Score(weapon, Array.Empty<MongoId>());

            var state = new SearchState(reachable, allowed, goals, mustIncludeCategories, Stopwatch.StartNew())
            {
                RecoilPerPercent = (bare?.Recoil ?? 0d) / 100d,
                Seed = seed,
                Restarts = restarts > 0 ? restarts : Attempts,

                // What the last search learned about this build: which thresholds it could not get any
                // slack on. A first boot has none of this and searches exactly as it did before.
                Binding = binding == null || binding.Count == 0
                    ? null
                    : new HashSet<string>(binding, StringComparer.OrdinalIgnoreCase),

                Defaults = presets.For(weapon),

                // No pricing means every purchase costs one unit: the objective collapses to the change
                // count, which is what it was before prices existed.
                Pricing = pricing ?? new Pricing { PerPurchase = 1 }
            };

            state.Measure(weapon);

            var required = Required(weapon, mustInclude);

            Node? best = null;
            var bestWhole = int.MaxValue;
            var bestShortfall = double.PositiveInfinity;
            var bestCount = int.MaxValue;
            var bestCost = long.MaxValue;

            // THE FLOOR, worked out once before anything is searched: what the plan has to place, the
            // slots the game will not let the player leave empty, and one part for each category still
            // unaccounted for. Nothing can be smaller, so a build that reaches it is provably minimal
            // and the search can stop.
            //
            // Once, and not as a running minimum over the attempts, because it is the threshold the
            // search stops at: a floor that starts too high and falls as attempts go by lets the loop
            // break on an early fat build that happened to match the loose bound it had at the time.
            // That cost two parts across the sixty when the bypass pass made early builds leaner.
            var scratch = new Node { Template = weapon, Locked = true };

            state.Shuffle = null;
            state.ResetTree(weapon);
            Plan(scratch, required, state);
            Fill(scratch, state, MaxDepth, requiredOnly: true);

            var floor = CountParts(scratch) + UnmetCategories(scratch, state);

            result.Floor = floor;

            // Search once for a build that works, then keep asking for one part fewer until the answer
            // is no. The climb only ever ADDS a part that closes a shortfall and the pruning passes only
            // ever take off what carries nothing, so between them they find a build that cannot be made
            // smaller BY EDITING IT - which is not the same as the smallest build, and the difference is
            // the whole reason for asking again from scratch under a ceiling.
            //
            // The ceiling is enforced where every other requirement is, in the cost: a part over the
            // limit counts as a gap, so it outranks every threshold and the climb spends its moves
            // getting under the limit before it spends any on a number. An attempt that cannot is not a
            // worse answer, it is evidence that the size is impossible for this search.
            // A build carried over from a previous boot is the incumbent, and the only question worth
            // asking about it is whether anything SMALLER works. So the search starts under a ceiling one
            // part below it and is seeded with it, which makes the first move "take a part off and repair
            // what that broke" - by far the likeliest way to find a build one part smaller than one that
            // already works.
            //
            // Nothing found means the incumbent stands, which is not a failure: it is a boot's worth of
            // evidence that the size is hard to beat, and the next boot will try again from the same
            // place. Across boots the answer can only get smaller.
            state.Incumbent = knownGood == null ? null : Rebuild(weapon, knownGood, state);

            if (state.Incumbent != null)
            {
                bestWhole = 0;
                bestShortfall = 0d;
                bestCount = CountParts(state.Incumbent);
                best = state.Incumbent;

                // The incumbent's PRICE too, not only its size. Left at long.MaxValue, the shrink loop's
                // first iteration set a cost ceiling of long.MaxValue - 1, which is no ceiling at all, and
                // burned a full round of restarts against the time budget before any real one applied.
                (bestCost, _) = Priced(state.Incumbent, state);

                state.PartCeiling = bestCount - 1;
            }

            // An explicit ceiling is how a caller asks a different question. "Find one THIS size" rather
            // than "find one smaller" is a search of a different basin at the same cost, and a build found
            // that way is somewhere new to try shrinking from next time - which is the difference between
            // a search that keeps exploring and one that has converged and stopped.
            if (ceiling > 0) state.PartCeiling = ceiling;

            var found = Search(
                weapon, required, state, floor, ref bestWhole, ref bestShortfall, ref bestCount, ref bestCost);

            if (found != null) best = found;

            // Squeeze the OBJECTIVE, not the part count: ask for a build that qualifies for less than the best
            // so far costs and keep going while the answer is yes. The part ceiling is released while this
            // runs, because a cheaper build is allowed to be a larger one - that is the whole trade. Bounded
            // by the search budget: every round is a full set of restarts, and the time ceiling ends it.
            while (best != null && bestWhole == 0 && bestShortfall <= 0d && !state.Exhausted
                   && (bestCost > 0 || bestCount > floor))
            {
                state.PartCeiling = bestCost > 0 ? int.MaxValue : bestCount - 1;
                state.CostCeiling = bestCost > 0 ? bestCost - 1 : bestCost;
                state.Incumbent = best;

                var whole = int.MaxValue;
                var shortfall = double.PositiveInfinity;
                var count = int.MaxValue;
                var cost = long.MaxValue;
                var cheaper = Search(
                    weapon, required, state, floor, ref whole, ref shortfall, ref count, ref cost);

                // Only a build that satisfies EVERYTHING counts. One that merely fits inside the ceiling
                // while missing a threshold is the ceiling being too tight, which is the answer to the
                // question rather than a better build.
                if (cheaper == null || whole != 0 || shortfall > 0d) break;
                if (cost > bestCost || (cost == bestCost && count >= bestCount)) break;

                best = cheaper;
                bestWhole = whole;
                bestShortfall = shortfall;
                bestCount = count;
                bestCost = cost;
            }

            state.CostCeiling = long.MaxValue;
            state.PartCeiling = int.MaxValue;
            state.Incumbent = null;

            result.NodesOpened = state.Nodes;
            result.HitCeiling = state.Exhausted;

            if (best == null) return result;
            Report(weapon, best, goals, required, state, result);

            return result;
        }

        /// <summary>One full round of restarts, and the best build any of them reached.
        ///
        /// Separate from Solve because it is run more than once: first to find a build at all, then again
        /// under a ceiling one part lower, and again, until the answer comes back no.</summary>
        private Node? Search(
            MongoId weapon,
            List<MongoId> required,
            SearchState state,
            int floor,
            ref int bestWhole,
            ref double bestShortfall,
            ref int bestCount,
            ref long bestCost)
        {
            Node? best = null;

            // Many starting points, because a climb is a local search and which optimum it reaches
            // depends on where it starts. Starting from the FLOOR - the weapon, the parts the quest
            // names, and nothing else but the slots the game refuses to leave empty - is the one that
            // arrives lean, because the climb's moves only ever add a part that closes a shortfall.
            // A fully dressed start still earns its place: it reaches builds the lean one cannot,
            // and it is why the KRISS Vector solves at all. Past those, a randomised start is what
            // gets out of a basin no single move can leave.
            for (var attempt = 0; attempt < state.Restarts; attempt++)
            {
                state.ResetTree(weapon);

                // Seeded by the attempt AND by which boot this is. Within a boot it is fixed, so the
                // same request twice gives the same build and the dry run measures what the panel shows.
                // Across boots it moves, and that is deliberate: an identical search finds an identical
                // answer, so a solver that never varies its starting points stops improving the moment it
                // has run once. The incumbent makes that safe - a new starting point can only ever
                // replace the build with a smaller one that verifies.
                state.Shuffle = attempt < 2 ? null : new Random(state.Seed + attempt);

                // The first attempt starts from the build already in hand when there is one. Over the
                // ceiling by exactly one part, so the climb's first move is to shed one - and because a
                // part over the limit counts as a gap, shedding outranks every threshold and the repair
                // work happens afterwards, on a gun that is already the right size.
                var root = attempt == 0 && state.Incumbent != null
                    ? Clone(state.Incumbent, state)
                    : new Node { Template = weapon, Locked = true };

                if (attempt != 0 || state.Incumbent == null)
                {
                    Plan(root, required, state);

                    // Even attempts start at the floor, odd ones fully dressed.
                    Fill(root, state, MaxDepth, requiredOnly: attempt % 2 == 0);
                }

                // The randomness belongs to the dressing only. The climb fills the sub-slots of every
                // candidate it tries, and a random fill there would have it judging parts by a throw
                // of the dice rather than by what they are worth.
                state.Shuffle = null;

                var climbed = Climb(weapon, root, state);

                // Pruned before it is compared, not after the winner is picked: a fat attempt that
                // trims to nine parts should beat a lean one that settles at ten, and it cannot if
                // only the winner is ever trimmed.
                Prune(weapon, root, state, required, floor);

                var measured = Measure(weapon, root, state, new List<MongoId>());
                var whole = measured.Gaps + required.Count(part => Find(root, part) == null);
                var shortfall = measured.Shortfall;
                var count = CountParts(root);

                // Whole requirements, then how far short the numbers are, then HOW MANY PARTS. The
                // count is the objective once the rest is satisfied - a quest asks for a gun that
                // meets its numbers, and the fewest parts that do it is the answer. Comparing attempts
                // without it is exactly what made every build fat: the first attempt that passed won,
                // and the first attempt was the fully dressed one.
                // The same ordering as Cost.Beats, and it has to be: an attempt comparison that disagreed
                // with the cost function would throw away the build the climb just worked to prefer.
                var cost = measured.Price;
                var settled = whole == bestWhole && shortfall <= bestShortfall + MinGain;

                if (whole < bestWhole
                    || (whole == bestWhole && shortfall < bestShortfall - MinGain)
                    || (settled && cost < bestCost)
                    || (settled && cost == bestCost && count < bestCount))
                {
                    bestWhole = whole;
                    bestShortfall = shortfall;
                    bestCount = count;
                    bestCost = cost;
                    best = root;
                }

                // Nothing left to want: everything satisfied, nothing to buy, and at a size nothing could
                // undercut.
                //
                // bestCost == 0 makes this all but unreachable, and that is a deliberate trade rather than an
                // oversight. Under handbook pricing every purchase costs PerPurchase, so a zero-cost build is
                // one assembled entirely from the default preset and parts already owned - which a Gunsmith
                // quest naming specific parts essentially never is. Weakening it to "no cheaper build is
                // possible" needs a lower bound over PRICE, which does not exist; stopping on size alone
                // would abandon the objective the search is actually minimising. So the restarts run and the
                // budget is what stops them.
                if (bestWhole == 0 && bestShortfall <= 0d && bestCost == 0 && bestCount <= floor) break;
                if (state.Exhausted) break;
            }

            return best;
        }

        // ---------------------------------------------------------------------------------------
        // PLAN - the parts the quest names, placed into an explicit tree
        // ---------------------------------------------------------------------------------------

        /// <summary>The thresholds this search can act on, and a note of the ones it cannot.</summary>
        private static List<Goal> Goals(
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds, Result result)
        {
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


            return goals;
        }

        /// <summary>The parts the quest names, deduplicated, the weapon itself excluded.</summary>
        private static List<MongoId> Required(MongoId weapon, IReadOnlyCollection<MongoId> mustInclude)
        {
            var required = new List<MongoId>();

            foreach (var id in mustInclude)
                if (id != weapon && !required.Contains(id)) required.Add(id);

            return required;
        }

        /// <summary>Everything a caller is told about a finished build, without searching for one.
        ///
        /// Here so that a build carried over from a previous run costs what it should cost - nothing. The
        /// search is what is expensive; describing its result is a handful of dictionary lookups, and a
        /// shipped mod that already knows the answer should not be re-deriving it sixty times on every
        /// start.</summary>
        public Result Describe(
            MongoId weapon,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories,
            IReadOnlyList<FittedPart> parts,
            Pricing? pricing = null)
        {
            var result = new Result();
            var reachable = graph.Reachable(weapon, out _);

            if (reachable == null) return result;

            var goals = Goals(thresholds, result);

            var state = new SearchState(reachable, null, goals, mustIncludeCategories, Stopwatch.StartNew())
            {
                Defaults = presets.For(weapon),
                Pricing = pricing ?? new Pricing { PerPurchase = 1 }
            };

            var required = Required(weapon, mustInclude);

            var root = Rebuild(weapon, parts, state);

            if (root == null) return result;

            Report(weapon, root, goals, required, state, result);

            return result;
        }

        /// <summary>Fills in the verdict on one finished build: what it scores, what it fails, and the
        /// fewest parts one mandatory skeleton for the quest would take.</summary>
        private Result Report(
            MongoId weapon,
            Node best,
            List<Goal> goals,
            List<MongoId> required,
            SearchState state,
            Result result)
        {

            var fitted = new List<MongoId>();
            CollectTemplates(best, fitted);

            var stats = model.Score(weapon, fitted);

            result.Stats = stats;
            result.Changes = Changed(best, state);

            var (cost, unpriced) = Priced(best, state);
            result.Cost = cost;
            result.Unpriced = unpriced;

            CollectParts(best, result.Parts, -1);

            if (stats == null) return result;

            var footprint = Footprint(best, state);

            foreach (var goal in goals)
            {
                var actual = Sized.Contains(goal.Field)
                    ? goal.Field.ToLowerInvariant() == "height" ? footprint.Height : footprint.Width
                    : Read(stats, goal.Field);

                if (actual == null)
                {
                    // The model returns null for "I do not claim to know", which must never be read
                    // as a pass: a quest wanting a magazine over 30 rounds is not satisfied by a
                    // build with no magazine at all.
                    result.Unmet.Add($"{goal.Field}: no value - the build has nothing that provides it");
                    continue;
                }

                if (goal.Met(actual.Value))
                {
                    // Met, and by how little. A goal on the line is the reason the gun cannot be leaner, so
                    // it is reported as a fact about the build rather than left in a local. A sized goal is
                    // whole grid squares, so on the line means exactly on it.
                    var slack = goal.Margin(actual.Value) / goal.Scale;

                    if (slack <= (Sized.Contains(goal.Field) ? 0d : BindingSlack)) result.Binding.Add(goal.Field);

                    continue;
                }

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

                result.Unmet.Add(state.Reachable.ContainsKey(part)
                    ? $"could not fit the required part {part} (reachable, not placed)"
                    : $"required part {part} is NOT REACHABLE from this weapon's slots");
            }

            // A slot the game marks Required cannot be left empty in the modding screen, so a build
            // that leaves one is not a build at all - it is arithmetic the player cannot assemble.
            // Every one of the 30 vanilla quest weapons has at least one: mod_barrel on the M1A,
            // mod_pistol_grip and mod_gas_block on every AK, mod_reciever and mod_charge on the M4A1.
            var starved = new List<string>();
            EmptyRequired(best, state, starved);

            foreach (var slot in starved)
                result.Unmet.Add($"required slot {slot} is empty - the game will not assemble this build");

            // Categories are requirements in exactly the way the named parts are - "must include a
            // Silencer" is 16 of the 32 vanilla conditions - and an unenforced requirement is an
            // unmet one.
            var carried = 0;
            foreach (var template in fitted) carried |= CategoryMask(template, state);

            for (var index = 0; index < state.Categories.Count; index++)
            {
                if ((carried & (1 << index)) != 0) continue;

                result.Unmet.Add($"no fitted part from the required category {state.Categories[index]}");
            }

            result.Found = result.Unmet.Count == 0;

            return result;
        }

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
            PlanCategories(root, state);

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

        /// <summary>Seats one part for each category the quest names that nothing on the gun already
        /// satisfies.
        ///
        /// Categories used to be left to the climb, and the climb reaches them only by luck. Closing a
        /// category gap is not a move any single slot offers when the part that would close it mounts
        /// on a thread adapter - the adapter alone closes nothing, so it is judged on its stats and
        /// rejected. The SVDS lost its Silencer for exactly that reason, in all sixteen restarts, and
        /// had only ever passed because one particular randomised dressing happened to fit one.
        ///
        /// A category is a requirement, so the planner places it, and it places the member that costs
        /// the fewest slots to reach - which is also what the count wants.</summary>
        private void PlanCategories(Node root, SearchState state)
        {
            for (var index = 0; index < state.Categories.Count; index++)
            {
                if (Carries(root, index, state)) continue;

                var choice = Nearest(index, state);
                if (choice == null) continue;

                var part = new PlanPart { Part = choice.Value, Steps = StepsTo(choice.Value, state) };
                var alone = new List<PlanPart> { part };

                foreach (var seat in Seatings(root, part))
                    if (Extend(seat, part.Steps[seat.Template] + RouteSlack, root, alone, 0, state)) break;
            }
        }

        private bool Carries(Node node, int category, SearchState state)
        {
            foreach (var child in node.Children)
            {
                if ((CategoryMask(child.Template, state) & (1 << category)) != 0) return true;
                if (Carries(child, category, state)) return true;
            }

            return false;
        }

        /// <summary>The member of one category that sits fewest slots from the weapon, and of those the
        /// one the thresholds like best.</summary>
        private MongoId? Nearest(int category, SearchState state)
        {
            MongoId? best = null;
            var bestCost = int.MaxValue;
            var bestWorth = double.NegativeInfinity;

            foreach (var (template, _) in state.Reachable)
            {
                if ((CategoryMask(template, state) & (1 << category)) == 0) continue;
                if (!state.Distance.TryGetValue(template, out var steps)) continue;

                // A member the player cannot get does not satisfy the category for THEM. Without this a
                // restricted search plans a silencer nobody sells, locks it, and reports the quest solved.
                if (state.Allowed != null && !state.Allowed.Contains(template)) continue;

                // The chain to reach it, plus whatever it forces once it is there. The thresholds only
                // break a tie, because the requirement is "a part of this category" and any member
                // satisfies it - so the one that costs fewest parts is the right one.
                var cost = steps - 1 + Burden(template, state, 0);
                var worth = Potential(template, state, 0);

                if (cost > bestCost) continue;
                if (cost == bestCost && worth <= bestWorth) continue;

                bestCost = cost;
                bestWorth = worth;
                best = template;
            }

            return best;
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

                    // A chain is parts the player has to obtain like any other, so a restricted search may
                    // not route through one they cannot. The part being routed TO is exempt: the quest
                    // names it, and whether the player can get it is reported, not searched around.
                    if (state.Allowed != null && candidate != part.Part && !state.Allowed.Contains(candidate))
                        continue;

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
        private void Fill(Node node, SearchState state, int depthLimit, bool requiredOnly = false)
        {
            if (node.Depth >= depthLimit || node.Depth >= MaxDepth) return;
            if (state.OutOfBudget()) return;
            if (!state.Reachable.TryGetValue(node.Template, out var part)) return;

            for (var index = 0; index < part.Slots.Length; index++)
            {
                if (Occupant(node, index) != null) continue;

                // The FLOOR: only the slots the game will not let the player leave empty. Nothing can
                // be smaller than this plus the parts the quest names, so a climb that starts here and
                // only adds what closes a shortfall arrives lean by construction instead of being
                // trimmed down to lean afterwards.
                if (requiredOnly && !part.Slots[index].Required) continue;

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
                Fill(child, state, depthLimit, requiredOnly);
        }

        /// <summary>The best-scoring legal candidate for one slot, or null to leave it empty.
        ///
        /// An empty slot is a legitimate choice: a part that helps nothing measured is weight for
        /// free, and weight is a threshold in its own right. The climb revisits every slot this
        /// leaves empty, so a wrong "no" here costs a sweep rather than the answer.</summary>
        private MongoId? Choose(Node parent, WeaponGraph.SlotInfo slot, SearchState state)
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
                // A required slot is filled either way, so the cheapest occupant wins and the
                // thresholds only break a tie between equally cheap ones.
                if (slot.Required)
                {
                    var pick = shortlist[0];
                    var cost = Burden(pick.Template, state, 0);

                    foreach (var entry in shortlist)
                    {
                        var burden = Burden(entry.Template, state, 0);

                        if (burden > cost) continue;
                        if (burden == cost && entry.Score <= pick.Score) continue;

                        pick = entry;
                        cost = burden;
                    }

                    return pick.Template;
                }

                var best = shortlist[0];

                foreach (var entry in shortlist)
                    if (entry.Score > best.Score) best = entry;

                // An empty slot is a legitimate choice: a part that helps nothing measured is weight
                // for free, and weight is a threshold in its own right.
                //
                // UNLESS the game marks the slot Required, in which case it cannot be left empty in
                // the modding screen and "this part helps nothing" is no reason to hand the player a
                // gun they cannot assemble.
                return best.Score > 0d || slot.Required ? best.Template : (MongoId?)null;
            }

            if (!slot.Required && state.Shuffle.Next(SkipOneSlotIn) == 0) return null;

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
        private double Potential(MongoId template, SearchState state, int depth)
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

        /// <summary>How many parts fitting one template forces onto the gun: itself, plus the
        /// cheapest occupant of every slot it cannot leave empty, recursively.
        ///
        /// A required slot is going to be filled whatever happens, so the question there is not which
        /// part helps most but which costs least - and the cost is not one. An M1A barrel forces a
        /// muzzle device; an AR receiver forces a charging handle; an occupant chosen for its
        /// ergonomics can bring three parts with it. Choosing by stats and counting the parts
        /// afterwards is how a floor of eight becomes a build of twelve.</summary>
        private int Burden(MongoId template, SearchState state, int depth)
        {
            if (depth > MaxDepth) return 1;
            if (state.Burdens.TryGetValue(template, out var cached)) return cached;
            if (!state.Reachable.TryGetValue(template, out var part)) return 1;

            // Marked before descending, at the honest minimum for one part: a slot graph that admits
            // its own host meets the marker and stops instead of looping.
            state.Burdens[template] = 1;

            var total = 1;

            foreach (var slot in part.Slots)
            {
                if (!slot.Required) continue;

                var cheapest = int.MaxValue;

                foreach (var candidate in slot.Candidates)
                {
                    var burden = Burden(candidate, state, depth + 1);
                    if (burden < cheapest) cheapest = burden;
                }

                if (cheapest != int.MaxValue) total += cheapest;
            }

            state.Burdens[template] = total;

            return total;
        }

        /// <summary>How much one part helps BY ITSELF, summed over the goals, each in units of its own
        /// threshold so that percentages, kilograms and ergonomics points are comparable.</summary>
        private double Score(WeaponGraph.PartInfo part, SearchState state)
        {
            // A part from a category the quest names is worth a whole threshold's worth of anything
            // else, because without one the build cannot be handed in at all. Counted here and not
            // only in the cost, so that Potential carries it back through an adapter: the AKM's Kiba
            // muzzle adapter is worth nothing whatsoever except that a suppressor screws onto it.
            var score = CategoryMask(part.Template, state) != 0 ? 1d : 0d;

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

                    // A size limit always wants LESS, so a part that widens or heightens the gun is a cost.
                    // Approximate on purpose: the real rule takes a maximum across parts rather than a sum,
                    // and this only has to steer the dressing away from the widest options - the climb
                    // measures the assembled footprint exactly.
                    case "height":
                        score += direction * (part.ExtraUp + part.ExtraDown) / goal.Scale;
                        break;

                    case "width":
                        score += direction * (part.ExtraLeft + part.ExtraRight) / goal.Scale;
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

            for (var sweep = 0; sweep < MaxSweeps && !current.Done; sweep++)
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
                        if (current.Done) return current;
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
        /// <summary>Takes off every part the quest does not actually need.
        ///
        /// The climb can only ever ADD. It starts from a greedily dressed gun and its moves swap one
        /// part for another in a slot, so there is no move that empties a slot and no way for a
        /// finished build to shed the parts it picked up on the way. Ranking part count in the cost
        /// helped barely at all for that reason - it can pick the leaner of two builds it is already
        /// considering, and it is never considering a leaner one.
        ///
        /// So this is a separate pass, and it is deliberately the dumbest thing that works: take a
        /// part off, re-measure, put it back if anything got worse. Deepest parts first, so a scope
        /// comes off before the mount holding it and the mount is then free to go too.
        ///
        /// It cannot make a build worse, which is the whole reason it is safe to run on an answer
        /// that is already correct: a removal is kept only when gaps stay closed and shortfall does
        /// not rise. A part that was carrying a threshold pays for itself and stays.</summary>
        private void Prune(MongoId weapon, Node root, SearchState state, List<MongoId> required, int floor)
        {
            var buffer = new List<MongoId>();
            var before = Measure(weapon, root, state, buffer);

            // Only worth doing on a build that actually passes. On one that does not, every part is
            // still a candidate for carrying the shortfall and stripping it would only hide how
            // close the search got.
            if (!before.Done) return;

            // Both passes, repeated. Dropping a leaf can leave its parent a leaf, and bypassing a
            // mount can leave the part above it removable, so one pass of either is not closed. It
            // terminates because every accepted change takes a part off and a build has finitely many.
            for (var round = 0; round < PruneRounds; round++)
            {
                var shed = Shed(weapon, root, state, required, buffer, ref before);
                var bypassed = Bypass(weapon, root, state, required, buffer, ref before);

                // Only above the floor: at the floor the build is provably minimal and there is
                // nothing for the expensive pass to find.
                var condensed = before.Parts > floor
                                && Condense(weapon, root, state, required, buffer, ref before);

                if (!shed && !bypassed && !condensed) break;
            }
        }

        /// <summary>Tries to do with one part what the build is doing with two.
        ///
        /// Shedding and bypassing between them leave a build from which nothing can be REMOVED, and
        /// that is not the same as the smallest build. If ergonomics is met by two +5 parts in two
        /// slots and a single +10 part exists, every pass above keeps both: each one is individually
        /// load-bearing, so no removal is ever safe, and nothing has asked whether one part could do
        /// the work of both.
        ///
        /// So this asks. Take any two discretionary parts off, then try putting a single part anywhere
        /// legal and see whether everything is satisfied again. One net part saved each time it works,
        /// and the whole of Prune runs again afterwards because one saving can enable another.
        ///
        /// Leaves only, on both sides of the pair: a part with something mounted on it cannot come off
        /// without rehoming its passenger, which is what Bypass is for, and the two passes reach the
        /// combination between them across rounds.</summary>
        private bool Condense(
            MongoId weapon, Node root, SearchState state, List<MongoId> required, List<MongoId> buffer, ref Cost before)
        {
            var loose = new List<Node>();
            CollectNodes(root, loose);

            loose.RemoveAll(node =>
                node.Parent == null || node.Locked || node.Children.Count > 0 || required.Contains(node.Template));

            for (var first = 0; first < loose.Count; first++)
            for (var second = first + 1; second < loose.Count; second++)
            {
                if (state.OutOfBudget()) return false;

                var one = loose[first];
                var other = loose[second];
                var oneHost = one.Parent!;
                var otherHost = other.Parent!;

                Detach(one, state);
                Detach(other, state);

                if (Substitute(weapon, root, state, buffer, ref before)) return true;

                Reattach(otherHost, other, state);
                Reattach(oneHost, one, state);
            }

            return false;
        }

        /// <summary>Puts ONE part somewhere legal and keeps it if the build ends up satisfied and
        /// smaller than it was. Filled to its required slots only - the point is the fewest parts, so a
        /// replacement that drags a dressing along with it is not a replacement.</summary>
        private bool Substitute(
            MongoId weapon, Node root, SearchState state, List<MongoId> buffer, ref Cost before)
        {
            var hosts = new List<Node>();
            CollectNodes(root, hosts);
            hosts.Insert(0, root);

            foreach (var host in hosts)
            {
                if (host.Depth >= MaxDepth) continue;
                if (!state.Reachable.TryGetValue(host.Template, out var part)) continue;

                for (var index = 0; index < part.Slots.Length; index++)
                {
                    if (Occupant(host, index) != null) continue;

                    foreach (var candidate in Likeliest(part.Slots[index], host, state))
                    {
                        if (state.OutOfBudget()) return false;

                        var trial = new Node
                        {
                            Template = candidate,
                            SlotName = part.Slots[index].Name,
                            SlotIndex = index,
                            Depth = host.Depth + 1
                        };

                        Attach(host, trial, state);
                        Fill(trial, state, MaxDepth, requiredOnly: true);

                        var after = Measure(weapon, root, state, buffer);

                        if (after.Gaps <= before.Gaps
                            && after.Shortfall <= before.Shortfall + MinGain
                            && after.Parts < before.Parts)
                        {
                            before = after;
                            return true;
                        }

                        Detach(trial, state);
                    }
                }
            }

            return false;
        }

        /// <summary>The few candidates for one slot most worth trying as a replacement.</summary>
        private List<MongoId> Likeliest(WeaponGraph.SlotInfo slot, Node host, SearchState state)
        {
            var ranked = new List<(MongoId Template, double Worth)>();

            foreach (var candidate in slot.Candidates)
            {
                if (state.Allowed != null && !state.Allowed.Contains(candidate)) continue;
                if (!state.Reachable.ContainsKey(candidate)) continue;
                if (IsAncestor(host, candidate)) continue;
                if (!Compatible(candidate, state)) continue;

                ranked.Add((candidate, Potential(candidate, state, 0)));
            }

            ranked.Sort((left, right) => right.Worth.CompareTo(left.Worth));

            var take = Math.Min(CondenseCandidates, ranked.Count);
            var best = new List<MongoId>(take);

            for (var index = 0; index < take; index++) best.Add(ranked[index].Template);

            return best;
        }

        /// <summary>Takes off every part that carries nothing and pays for nothing.</summary>
        private bool Shed(
            MongoId weapon, Node root, SearchState state, List<MongoId> required, List<MongoId> buffer, ref Cost before)
        {
            var removable = new List<Node>();
            CollectNodes(root, removable);

            // Deepest first: a part cannot come off while something is mounted on it, and taking the
            // deepest first is what lets a whole chain go in one pass.
            removable.Sort((left, right) => right.Depth.CompareTo(left.Depth));

            var shed = false;

            foreach (var node in removable)
            {
                if (node.Parent == null) continue;
                if (node.Children.Count > 0) continue;

                // A part the quest named is not optional however little it contributes, and neither
                // is anything the planner placed - a locked part is there to satisfy a requirement, not
                // to help a number.
                if (node.Locked) continue;
                if (required.Contains(node.Template)) continue;

                var parent = node.Parent;

                Detach(node, state);

                var after = Measure(weapon, root, state, buffer);

                // Strictly not worse on the two that matter. Headroom is allowed to fall - that is the
                // point of the pass.
                if (after.Gaps <= before.Gaps && after.Shortfall <= before.Shortfall + MinGain)
                {
                    before = after;
                    shed = true;
                    continue;
                }

                Reattach(parent, node, state);
            }

            return shed;
        }

        /// <summary>Takes out a part that carries exactly one other part and nothing else, re-homing
        /// its passenger onto the slot it vacated.
        ///
        /// This is the half of pruning that removing leaves cannot reach, and it is where the waste
        /// actually lives. A LaRue LT101 riser has one optional scope slot, -1 ergonomics and 0.11 kg,
        /// and the optic sitting on it is in the filter of the slot the riser itself occupies - so the
        /// riser is a pass-through that costs a part and gives nothing back. It is never a leaf, so a
        /// leaf-only pass keeps it forever. Three of the six wasted parts found by audit were this
        /// exact shape: a riser or mount bypassable in place.
        ///
        /// Only a single passenger, deliberately: a mount carrying two parts has nowhere to put the
        /// second, and inventing a second home for it is a different move with different risks.
        ///
        /// A LOCKED pass-through is fair game, and that is the whole reason this finds anything. The
        /// planner locks the parts it places, INCLUDING the intermediates it routes through - and an
        /// intermediate is not a requirement, it is a route to one. If the passenger is legal in the
        /// slot the intermediate is giving up, then the route was a slot longer than it needed to be
        /// and every requirement is still satisfied one level higher. Three of the six wasted parts
        /// found by audit were locked risers, and skipping locked nodes kept every one of them.
        ///
        /// What must NOT be dropped is a part the quest names, because nothing in Measure knows about
        /// those - they are counted separately - so the guard here is the only thing standing between
        /// this pass and a build that quietly loses one.</summary>
        private bool Bypass(
            MongoId weapon, Node root, SearchState state, List<MongoId> required, List<MongoId> buffer, ref Cost before)
        {
            var nodes = new List<Node>();
            CollectNodes(root, nodes);

            nodes.Sort((left, right) => right.Depth.CompareTo(left.Depth));

            var bypassed = false;

            foreach (var node in nodes)
            {
                if (node.Parent == null) continue;
                if (node.Children.Count != 1) continue;

                // Measure does not count named parts - Solve counts those separately - so this is the
                // only thing that stops the pass dropping one.
                if (required.Contains(node.Template)) continue;

                var parent = node.Parent;
                var child = node.Children[0];

                if (!state.Reachable.TryGetValue(parent.Template, out var host)) continue;
                if (node.SlotIndex < 0 || node.SlotIndex >= host.Slots.Length) continue;

                // The passenger has to be legal in the slot the pass-through is giving up, or this is
                // not a bypass, it is a build the modding screen refuses.
                if (Array.IndexOf(host.Slots[node.SlotIndex].Candidates, child.Template) < 0) continue;

                var slot = child.SlotIndex;
                var name = child.SlotName;

                // The passenger comes off its carrier BEFORE the carrier comes off the gun, and goes
                // back on in the mirror order. The other way round leaves the passenger listed under
                // the carrier as well as under its new host, and reverting then adds it a second time -
                // which is how the ASh-12 ended up with two Cobra foregrips in one slot. The verifier
                // caught that on the first boot after this pass was written; nothing else would have.
                Detach(child, state);
                Detach(node, state);

                child.SlotIndex = node.SlotIndex;
                child.SlotName = node.SlotName;
                Redepth(child, parent.Depth + 1);

                Attach(parent, child, state);

                var after = Measure(weapon, root, state, buffer);

                if (after.Gaps <= before.Gaps && after.Shortfall <= before.Shortfall + MinGain)
                {
                    before = after;
                    bypassed = true;
                    continue;
                }

                Detach(child, state);

                child.SlotIndex = slot;
                child.SlotName = name;

                Reattach(parent, node, state);
                Redepth(child, node.Depth + 1);
                Attach(node, child, state);
            }

            return bypassed;
        }

        private static void Redepth(Node node, int depth)
        {
            node.Depth = depth;

            foreach (var child in node.Children)
                Redepth(child, depth + 1);
        }

        private static void CollectNodes(Node node, List<Node> into)
        {
            foreach (var child in node.Children)
            {
                into.Add(child);
                CollectNodes(child, into);
            }
        }

        private Cost Measure(MongoId weapon, Node root, SearchState state, List<MongoId> buffer)
        {
            buffer.Clear();
            CollectTemplates(root, buffer);

            var stats = model.Score(weapon, buffer);
            if (stats == null) return new Cost(int.MaxValue, double.MaxValue, int.MaxValue, int.MaxValue, 0d);

            var footprint = Footprint(root, state);

            // Structural gaps, counted before any number is looked at. A required slot left empty or
            // a category with nothing from it is not a worse build - it is one the player cannot
            // assemble or cannot hand in, and the search has to close that before it spends anything
            // on a threshold.
            //
            // A part over the ceiling counts the same way, which is what makes "find one that works in
            // nine parts" a question the climb can answer: getting under the limit outranks every
            // number, so it is done first and never traded away for one.
            var gaps = EmptyRequired(root, state, null);

            if (buffer.Count > state.PartCeiling) gaps += buffer.Count - state.PartCeiling;

            // A build over the COST ceiling counts as structurally wrong in the same way, which is what makes
            // "find one that qualifies for less than this" a question the climb can answer: getting under the
            // limit outranks every threshold, so it is done first and never traded away for one. The excess is
            // charged in purchases - roubles over the line divided by what one purchase is worth - so a ceiling
            // one rouble under the incumbent still reads as one gap, not a thousand.
            var (priced, _) = Priced(root, state);

            if (priced > state.CostCeiling)
            {
                var unit = Math.Max(1L, state.Pricing.PerPurchase);
                gaps += 1 + (int)Math.Min(int.MaxValue / 2, (priced - state.CostCeiling) / unit);
            }

            if (state.Categories.Count > 0)
            {
                var carried = 0;
                foreach (var template in buffer) carried |= CategoryMask(template, state);

                for (var index = 0; index < state.Categories.Count; index++)
                    if ((carried & (1 << index)) == 0) gaps++;
            }

            var shortfall = 0d;
            var headroom = 0d;

            foreach (var goal in state.Goals)
            {
                var actual = Sized.Contains(goal.Field)
                    ? goal.Field.ToLowerInvariant() == "height" ? footprint.Height : footprint.Width
                    : Read(stats, goal.Field);

                // No value is neither a pass nor a near miss: a magazine-capacity threshold on a gun
                // with no magazine is completely unmet.
                if (actual == null)
                {
                    shortfall += MissingStatPenalty;
                    continue;
                }

                var margin = goal.Margin(actual.Value) / goal.Scale;

                if (margin < 0d) shortfall += -margin;
                else headroom += Math.Min(margin, state.Binds(goal.Field) ? BindingHeadroomCap : HeadroomCap);
            }

            return new Cost(gaps, shortfall, priced, buffer.Count, headroom);
        }

        /// <summary>What a build is worth to the climb, in four ranks: how many requirements it
        /// structurally fails, how far short of the thresholds it falls, how many parts it took, and
        /// how much room to spare it has on the ones it already meets.
        ///
        /// Gaps outrank everything because they are not degrees of anything. A gun with an empty
        /// mod_charge cannot be assembled in the modding screen and a gun with no silencer does not
        /// satisfy a condition that asks for one, however good its recoil; trading either away for a
        /// better number would produce a build that reads well and cannot be handed in.
        ///
        /// The headroom half is not a nicety, it is what unsticks the M1A. Shortfall alone gives a met
        /// threshold no pull at all, so the climb spends every point of spare ergonomics on whatever
        /// else it is chasing - and then the muzzle device that would fix recoil costs more ergonomics
        /// than is left, no single move improves anything, and the gun settles at its BARE recoil with
        /// the threshold 64 points away. Ranked strictly below shortfall, so a build that passes always
        /// beats one that does not, however roomy.</summary>
        private readonly struct Cost(int gaps, double shortfall, long price, int parts, double headroom)
        {
            /// <summary>Required slots left empty plus required categories with nothing from them.
            /// Whole requirements, not degrees of one.</summary>
            public int Gaps { get; } = gaps;

            /// <summary>Summed distance from the thresholds not met, each as a fraction of its own
            /// threshold.</summary>
            public double Shortfall { get; } = shortfall;

            /// <summary>What the player would have to spend: the price of every part not already on the gun
            /// plus PerPurchase for each, see Pricing.
            ///
            /// THE OBJECTIVE, and it outranks part count. A gun of nine parts that needs five of them bought
            /// is worse to a player than a gun of eleven that needs two, and the count was only ever standing
            /// in for this. Part count stays directly below as the tiebreak, so among builds that cost the
            /// same the leaner one still wins.</summary>
            public long Price { get; } = price;

            /// <summary>How many parts are on the gun.
            ///
            /// Ranked ABOVE headroom, and that ordering is the whole of this. Without it the climb
            /// has nothing to tell it when to stop: gaps close, shortfall reaches zero, and then the
            /// only remaining direction is more headroom - so it keeps bolting parts on to grow a
            /// margin nobody asked for. It produced a seventeen-part AKS-74N scoring ergonomics 74.5
            /// against a threshold of 65, wearing two identical Kobra sights and two identical sight
            /// shades, because a second copy of a part is never worse by headroom and the search had
            /// no other opinion.
            ///
            /// A quest asks for a gun that meets its numbers, not the best gun reachable. Below
            /// shortfall so a passing build still beats a failing one however lean, and above
            /// headroom so spare margin is a tiebreak between equal builds rather than a reason to
            /// keep shopping.</summary>
            public int Parts { get; } = parts;

            /// <summary>Summed room to spare on the thresholds that are met, each capped so one very
            /// slack threshold cannot outvote the rest.</summary>
            public double Headroom { get; } = headroom;

            /// <summary>A build that satisfies everything the search can see. There is nothing left
            /// to climb for.</summary>
            public bool Done => Gaps == 0 && Shortfall <= 0d;

            public bool Beats(in Cost other)
            {
                if (Gaps != other.Gaps) return Gaps < other.Gaps;

                if (Shortfall < other.Shortfall - MinGain) return true;
                if (Shortfall > other.Shortfall + MinGain) return false;

                // Equal on everything that must be true: the gun that costs least to assemble wins.
                if (Price != other.Price) return Price < other.Price;

                // Then, between two equally cheap builds, the leaner one.
                if (Parts != other.Parts) return Parts < other.Parts;

                return Headroom > other.Headroom + MinGain;
            }
        }

        /// <summary>The grid the assembled gun takes up. The receiver is 1x1 and every part extends it,
        /// so each direction takes the LARGEST extension any one part asks for, except parts marked
        /// ExtraSizeForceAdd which stack on top.
        ///
        /// Folding and collapsing are NOT applied, matching the verifier's reading for the same reason: the
        /// item data does not say whether a hand-in measures a weapon extended or reduced, and a build that
        /// fits extended fits whatever the player does with the stock.</summary>
        /// <summary>Parts of this build the weapon does not already wear.
        ///
        /// A swap and an addition each count one, because each is something the player has to go and get. A
        /// part of the DEFAULT that the build does not carry counts nothing: taking a part off costs a trip
        /// to nowhere, and a quest that needs it off needs it off.</summary>
        private static int Changed(Node root, SearchState state)
        {
            if (state.Defaults == null) return 0;

            var changes = 0;

            Count(root);

            return changes;

            void Count(Node node)
            {
                foreach (var child in node.Children)
                {
                    if (!state.Defaults!.Occupants.TryGetValue((node.Template, child.SlotName), out var stock)
                        || stock != child.Template)
                        changes++;

                    Count(child);
                }
            }
        }

        /// <summary>What the build costs under the search's pricing: for every part not already on the
        /// default preset and not listed as free, its price plus PerPurchase - and the count of those charged
        /// with no price at all.
        ///
        /// A part of the default that the build does not carry costs nothing, exactly as in Changed: taking a
        /// part off is a trip to nowhere.</summary>
        private static (long Cost, int Unpriced) Priced(Node root, SearchState state)
        {
            var pricing = state.Pricing;
            var cost = 0L;
            var unpriced = 0;

            Walk(root);

            return (cost, unpriced);

            void Walk(Node node)
            {
                foreach (var child in node.Children)
                {
                    var stock = state.Defaults != null
                                && state.Defaults.Occupants.TryGetValue((node.Template, child.SlotName), out var fitted)
                                && fitted == child.Template;

                    if (!stock && (pricing.Free == null || !pricing.Free.Contains(child.Template)))
                    {
                        var price = pricing.Price(child.Template);

                        if (price is { } known && known > 0) cost += known;
                        else unpriced++;

                        cost += pricing.PerPurchase;
                    }

                    Walk(child);
                }
            }
        }

        private static (int Width, int Height) Footprint(Node root, SearchState state)
        {
            if (!state.Reachable.TryGetValue(root.Template, out var weapon)) return (1, 1);

            var up = 0;
            var down = 0;
            var left = 0;
            var right = 0;

            var forcedUp = 0;
            var forcedDown = 0;
            var forcedLeft = 0;
            var forcedRight = 0;

            Extend(root, state, ref up, ref down, ref left, ref right,
                ref forcedUp, ref forcedDown, ref forcedLeft, ref forcedRight);

            return (weapon.Width + left + right + forcedLeft + forcedRight,
                weapon.Height + up + down + forcedUp + forcedDown);
        }

        private static void Extend(
            Node node, SearchState state,
            ref int up, ref int down, ref int left, ref int right,
            ref int forcedUp, ref int forcedDown, ref int forcedLeft, ref int forcedRight)
        {
            foreach (var child in node.Children)
            {
                if (state.Reachable.TryGetValue(child.Template, out var part))
                {
                    if (part.ExtraForced)
                    {
                        forcedUp += part.ExtraUp;
                        forcedDown += part.ExtraDown;
                        forcedLeft += part.ExtraLeft;
                        forcedRight += part.ExtraRight;
                    }
                    else
                    {
                        if (part.ExtraUp > up) up = part.ExtraUp;
                        if (part.ExtraDown > down) down = part.ExtraDown;
                        if (part.ExtraLeft > left) left = part.ExtraLeft;
                        if (part.ExtraRight > right) right = part.ExtraRight;
                    }
                }

                Extend(child, state, ref up, ref down, ref left, ref right,
                    ref forcedUp, ref forcedDown, ref forcedLeft, ref forcedRight);
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

        /// <summary>Required slots left empty anywhere on the gun, counted and optionally named.</summary>
        private static int EmptyRequired(Node node, SearchState state, List<string>? into)
        {
            if (!state.Reachable.TryGetValue(node.Template, out var part)) return 0;

            var starved = 0;

            for (var index = 0; index < part.Slots.Length; index++)
            {
                if (!part.Slots[index].Required) continue;
                if (Occupant(node, index) != null) continue;

                starved++;
                into?.Add($"'{part.Slots[index].Name}' on {node.Template}");
            }

            foreach (var child in node.Children)
                starved += EmptyRequired(child, state, into);

            return starved;
        }

        /// <summary>Which of the quest's required categories a template belongs to, one bit each.
        ///
        /// Memoised because IsOfBaseclass walks the item's parent chain and the climb asks this of
        /// thousands of candidates per request.</summary>
        private int CategoryMask(MongoId template, SearchState state)
        {
            if (state.Categories.Count == 0) return 0;
            if (state.CategoryMasks.TryGetValue(template, out var cached)) return cached;

            var mask = 0;

            for (var index = 0; index < state.Categories.Count; index++)
                if (itemHelper.IsOfBaseclass(template, state.Categories[index])) mask |= 1 << index;

            state.CategoryMasks[template] = mask;

            return mask;
        }

        /// <summary>How many parts are on the gun, the weapon itself excluded.</summary>
        private static int CountParts(Node node)
        {
            var count = node.Children.Count;

            foreach (var child in node.Children)
                count += CountParts(child);

            return count;
        }

        /// <summary>Required categories nothing on the gun belongs to yet. Each one needs at least one
        /// more part, so it belongs in the floor.</summary>
        private int UnmetCategories(Node root, SearchState state)
        {
            if (state.Categories.Count == 0) return 0;

            var templates = new List<MongoId>();
            CollectTemplates(root, templates);

            var carried = 0;
            foreach (var template in templates) carried |= CategoryMask(template, state);

            var missing = 0;

            for (var index = 0; index < state.Categories.Count; index++)
                if ((carried & (1 << index)) == 0) missing++;

            return missing;
        }

        /// <summary>Turns a flat parts list back into a tree, or null when it does not describe one this
        /// weapon could wear.
        ///
        /// Returning null is the guard on a cache written by an older install: a slot name that no longer
        /// exists, or a parent index out of order, costs a search rather than producing a gun with parts
        /// hanging off nothing.</summary>
        private static Node? Rebuild(MongoId weapon, IReadOnlyList<FittedPart> parts, SearchState state)
        {
            var root = new Node { Template = weapon, Locked = true };
            var placed = new List<Node>(parts.Count);

            foreach (var part in parts)
            {
                if (part.Parent < -1 || part.Parent >= placed.Count) return null;

                var host = part.Parent < 0 ? root : placed[part.Parent];

                if (!state.Reachable.TryGetValue(host.Template, out var info)) return null;

                var index = Array.FindIndex(info.Slots, slot => slot.Name == part.SlotName);
                if (index < 0 || Occupant(host, index) != null) return null;

                // The part must still BE something this slot accepts, and must still be compatible with
                // what is already on the gun. Neither was checked: the slot's name existing and being free
                // was the whole test, so a stored build survived a mod update that removed the part from
                // the slot's filter or introduced a conflict with another part it uses - and survived it
                // silently, because nothing downstream re-measures an incumbent.
                //
                // Returning null is the right failure. It is what every other check here does, and the
                // caller treats a null incumbent as "no history worth keeping" and searches from scratch,
                // which is exactly the outcome a build that can no longer be assembled deserves.
                if (!state.Reachable.ContainsKey(part.Template)) return null;
                if (Array.IndexOf(info.Slots[index].Candidates, part.Template) < 0) return null;
                if (!Compatible(part.Template, state)) return null;

                var node = new Node
                {
                    Template = part.Template,
                    SlotName = part.SlotName,
                    SlotIndex = index,
                    Depth = host.Depth + 1
                };

                Attach(host, node, state);
                placed.Add(node);
            }

            return root;
        }

        /// <summary>A fresh copy of a tree, so an attempt can edit it without spoiling the build it came
        /// from - which is still the answer if the attempt finds nothing better.</summary>
        private static Node Clone(Node node, SearchState state)
        {
            var copy = new Node
            {
                Template = node.Template,
                SlotName = node.SlotName,
                SlotIndex = node.SlotIndex,
                Depth = node.Depth,
                Locked = node.Locked
            };

            state.Counts[copy.Template] = state.Counts.GetValueOrDefault(copy.Template) + 1;

            foreach (var child in node.Children)
            {
                var branch = Clone(child, state);

                branch.Parent = copy;
                copy.Children.Add(branch);
            }

            return copy;
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

        /// <summary>Flattens the tree depth-first, each part carrying the POSITION of the part it is
        /// fitted to. Depth-first and parent-before-child, so a parent's index is always lower than
        /// its children's and a reader can rebuild the tree in one pass.</summary>
        private static void CollectParts(Node node, List<FittedPart> into, int parent)
        {
            foreach (var child in node.Children)
            {
                var index = into.Count;

                into.Add(new FittedPart
                {
                    SlotName = child.SlotName,
                    Template = child.Template,
                    Depth = child.Depth,
                    Parent = parent
                });

                CollectParts(child, into, index);
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
            /// <summary>Goals a previous search found sitting on the line for this build, so the climb keeps
            /// buying slack on them after they are met. Null for a search with no history to go on, which is
            /// what a first boot is - and that case behaves exactly as it did before this existed.</summary>
            public HashSet<string>? Binding;

            /// <summary>Whether this goal is one the climb should keep paying for after it is met.</summary>
            public bool Binds(string field) => Binding != null && Binding.Contains(field);

            public SearchState(
                IReadOnlyDictionary<MongoId, WeaponGraph.PartInfo> reachable,
                IReadOnlyCollection<MongoId>? allowed,
                List<Goal> goals,
                IReadOnlyCollection<MongoId> categories,
                Stopwatch clock)
            {
                Reachable = reachable;
                Allowed = allowed;
                Goals = goals;
                Clock = clock;

                // Capped at the width of the mask that tracks them. No condition on any install comes
                // near it, and a mask that silently wrapped would report a category as satisfied by a
                // part from a different one.
                foreach (var category in categories)
                    if (Categories.Count < MaxCategories && !Categories.Contains(category))
                        Categories.Add(category);

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

            /// <summary>How many dressings to climb from. More of them is the only lever that reliably
            /// finds anything on a build that has resisted, and a training run has time to spend.</summary>
            public int Restarts { get; init; } = Attempts;

            /// <summary>Where this boot's random starting points begin. Advanced once per boot by the
            /// cache, so no two boots explore the same ground.</summary>
            public int Seed { get; init; }

            /// <summary>A build already known to satisfy this quest, from a previous boot or from the
            /// round before this one. The search is only ever asked to beat it.</summary>
            public Node? Incumbent;

            /// <summary>Most parts the build may carry. Set while the search is asked for something
            /// leaner than it has already found; unbounded the rest of the time.</summary>
            public int PartCeiling = int.MaxValue;

            /// <summary>What a build may cost before the search treats it as structurally wrong. The
            /// objective's own ceiling, driven the way PartCeiling is: ask for less than the best answer so
            /// far costs, and see whether anything qualifies.</summary>
            public long CostCeiling = long.MaxValue;

            /// <summary>What parts cost the player this search is for. Never null once Solve has set it.</summary>
            public Pricing Pricing = new() { PerPurchase = 1 };

            /// <summary>What the weapon ships with, or null when the game has no preset for it. Read once
            /// per search rather than once per measurement.</summary>
            public WeaponPresets.Defaults? Defaults;

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

            /// <summary>Slots from the weapon to each part it can reach, by the shortest chain.
            /// One walk per request, and the only thing that can rank two candidates for a category by
            /// how many parts seating them would cost.</summary>
            public Dictionary<MongoId, int> Distance { get; } = new();

            /// <summary>Categories the quest insists a fitted part come from.</summary>
            public List<MongoId> Categories { get; } = new();

            /// <summary>Which categories each template belongs to, memoised.</summary>
            public Dictionary<MongoId, int> CategoryMasks { get; } = new();

            /// <summary>How many parts each template drags onto the gun, memoised. Depends only on the
            /// graph, so one pass serves every restart of one request.</summary>
            public Dictionary<MongoId, int> Burdens { get; } = new();

            public int Nodes;
            public bool Exhausted;

            public void ResetTree(MongoId weapon)
            {
                Counts.Clear();
                Counts[weapon] = 1;
            }

            /// <summary>Breadth-first distances from the weapon. The dictionary is the visited set, so
            /// a cyclic slot graph closes on a part it has already measured instead of looping, and the
            /// depth cap bounds it either way.</summary>
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


