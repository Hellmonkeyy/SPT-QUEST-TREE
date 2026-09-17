using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// Checks a finished build against the quest and the raw item data, sharing nothing with the
    /// search that produced it.
    ///
    /// WHY THIS EXISTS AS A SEPARATE TYPE
    ///
    /// The dry run was the solver marking its own homework. It asked the solver whether the solver
    /// was happy, using the solver's own cost function, its own flattened slot graph, and its own
    /// bookkeeping - so every class of bug that lives in that bookkeeping was invisible to it by
    /// construction. A pass rate produced that way is not evidence.
    ///
    /// So this re-derives everything from scratch. It reads <see cref="TemplateTable"/> directly and
    /// deliberately NOT <see cref="WeaponGraph"/>, so a flattening bug is caught rather than shared;
    /// it re-reads the thresholds off the condition's own DTO rather than trusting the Goal list the
    /// solver built from them; and it re-scores the parts with a fresh call to the stat model rather
    /// than reading the Stats the solver cached. Its only input is the weapon and the parts list.
    ///
    /// If this and the solver disagree, THE SOLVER IS WRONG. That is the rule, and the caller logs
    /// the disagreement loudly rather than picking a side quietly.
    ///
    /// WHAT IT CHECKS, and each one is a failure a passing dry run had previously hidden:
    ///
    ///   1  every threshold the model can score, re-compared
    ///   2  every part sits in a slot that exists on its parent and admits it
    ///   3  every required slot on EVERY fitted part is filled, not only the weapon's
    ///   4  every part and category the quest names is present
    ///   5  no template appears twice except in two distinct slots that both admit it
    ///
    /// What it deliberately does not do is claim a verdict on height, width, base accuracy or muzzle
    /// velocity. No model here scores them, so they are reported as unverifiable and never as passed.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponBuildVerifier(
        ISptLogger<WeaponBuildVerifier> logger,
        TemplateTable templateTable,
        WeaponStatModel model,
        ItemHelper itemHelper)
    {
        /// <summary>Fields this verifier can read a value for. Spelled out here rather than shared
        /// with the solver's own list: two copies that must agree will be noticed when they stop
        /// agreeing, and one copy consulted twice proves nothing.</summary>
        private static readonly HashSet<string> Scoreable = new(StringComparer.OrdinalIgnoreCase)
        {
            "ergonomics", "recoil", "weight", "magazine capacity", "effective distance"
        };

        /// <summary>The assembled grid footprint, which this verifier works out itself from the item data.
        ///
        /// These were the last constraints nothing checked. Five builds carried a height or width limit and
        /// were reported as satisfying their quest anyway, with the limit listed as unverifiable - which is
        /// the one place the count was asserting rather than checking. The stat model cannot produce them and
        /// is not going to be changed to, so they are computed here instead, where things nothing else can
        /// verify are supposed to live.</summary>
        private static readonly HashSet<string> Sized = new(StringComparer.OrdinalIgnoreCase)
        {
            "height", "width"
        };

        /// <summary>Not a property of the assembly at all - it is the weapon's repair state, no
        /// arrangement of parts changes it, and it appears in all 32 vanilla conditions.</summary>
        private static readonly HashSet<string> RepairState = new(StringComparer.OrdinalIgnoreCase)
        {
            "durability"
        };

        /// <summary>How deep this walks a weapon's slots. Vanilla's deepest real chain is five; the cap
        /// is what stops a cyclic graph from the visited set's blind side.</summary>
        private const int MaxReachDepth = 12;

        /// <summary>Largest build the part-count bound reasons about. The widest build on this install is
        /// 19 parts; past this the bound simply declines to make a claim.</summary>
        private const int MaxBudget = 28;

        /// <summary>Stat per part used where the real ceiling is unavailable. Far above anything in the
        /// game, so a bound resting on it can never prove a build minimal that is not.</summary>
        private const double HugePerPart = 1_000_000d;

        /// <summary>Weightings of ergonomics against recoil that the bound is computed at.
        ///
        /// A build that satisfies two thresholds satisfies every non-negative COMBINATION of them, and
        /// the combination is where the proof lives. Bounding each threshold on its own concedes the only
        /// thing that makes a build big: the parts that buy ergonomics are not the parts that buy recoil,
        /// so a gun needing both needs more parts than either needs alone. Gunsmith 10 sits at nine parts
        /// against separate bounds of two and two.
        ///
        /// So the same knapsack is run over a WEIGHTED per-part stat, once per weighting, and the bound is
        /// the best of them - which is the Lagrangian dual of the joint problem, evaluated on a grid
        /// rather than optimised, because a grid is enough and is cheap. The ends of the grid are the two
        /// single-stat bounds, so this can only ever be at least as good as what it replaces.
        ///
        /// The weights multiply raw ergonomics points against raw recoil PERCENT, with no weapon-specific
        /// conversion between them, and that is deliberate: it keeps the knapsack a property of the item
        /// data so one cache serves all 46 weapons. A conversion using each weapon's base recoil would be
        /// sharper per weapon and would multiply the cost by 46.</summary>
        private static readonly double[] ErgonomicsWeight = { 1d, 1d, 1d, 1d, 1d, 1d, 0d };
        private static readonly double[] RecoilWeight = { 0d, 0.5d, 1d, 2d, 4d, 8d, 1d };

        /// <summary>The tree knapsack, memoised per template and per weighting, shared across every
        /// weapon: the answer is a property of the subtree, not of the weapon the walk began at.
        ///
        /// CONCURRENT, because this is reached from every training thread at once and used to be a plain
        /// Dictionary written without a lock. The class comment claimed the tables "have their own locks";
        /// they had none. An unsynchronised Dictionary being written from fifteen threads does not merely
        /// return stale values - a resize racing an insert can lose entries, spin, or throw - and these
        /// tables are what every minimality proof rests on. Append-only and keyed by template, so a
        /// concurrent map is the whole of what is needed: TryAdd keeps whichever thread finished first and
        /// the values are a property of the item data, not of the caller.</summary>
        private readonly ConcurrentDictionary<MongoId, double[]>[] _best =
            Enumerable.Range(0, ErgonomicsWeight.Length)
                .Select(_ => new ConcurrentDictionary<MongoId, double[]>())
                .ToArray();

        /// <summary>The ceiling nothing can reach, built once at construction.
        ///
        /// Eager rather than lazy because a lazily built shared array is a race for no gain: two threads
        /// would each build an identical copy and one would win, which works by luck rather than by
        /// argument.</summary>
        private readonly double[] _unreachable = Unreachable();

        /// <summary>The fewest parts any satisfying build could have, and what forces it.
        ///
        /// One number, and it assumes nothing: every argument behind it is generous to the hypothetical
        /// smaller build, so a build matching it is minimal outright rather than minimal-if.</summary>
        public sealed class Floor
        {
            /// <summary>Unconditional. No build with fewer parts than this can satisfy the quest. Only
            /// meaningful while Unbounded is false.</summary>
            public int Parts { get; set; }

            /// <summary>No bound could be argued at all, so Parts states nothing.
            ///
            /// This exists because the absence of a bound used to be carried as int.MaxValue in Parts,
            /// and every consumer read it as the strongest possible claim instead of the weakest. The
            /// gate was "build parts is at most bound", which is unconditionally true against
            /// int.MaxValue - so a requirement nothing could reach marked its build provably minimal,
            /// cached that as a bound, counted it in the "N of 60 are minimal" line, and switched OFF
            /// the falsifier for it. The one check that tests a proof against reality was disabled by
            /// the proof being unavailable.
            ///
            /// A separate flag rather than a nullable Parts or another sentinel: a sentinel is what
            /// failed, and it failed in the direction that looks like success.
            ///
            /// Parts is zeroed alongside it, and that is deliberately conservative rather than merely
            /// tidy. The floor is a max over thresholds, so the bound from the thresholds that COULD be
            /// argued is still sound, and leaning on it would be defensible. It is declined anyway,
            /// because whatever the reason, the answer and the build in hand cannot both be right:
            ///
            /// Spread, Percent and Selected come back empty when NOTHING reachable moves the stat the
            /// required way - no positively-ergonomic part, no recoil-reducing part, no magazine that
            /// holds enough. Taken at face value that says no build satisfies the requirement, and yet a
            /// build exists that the verifier passed.
            ///
            /// Reach is different and weaker: it searches budgets 0 to MaxBudget, so empty means "not
            /// within 28 parts", NOT "impossible". The sound inference there would be a bound of
            /// MaxBudget + 1 - except that a bound of 29 against a 19-part build in hand says the same
            /// contradiction out loud, and would pass the "at most the bound" gate and call that build
            /// minimal. A bigger number is not a safer one.
            ///
            /// So in every case the honest state is "no claim", and zeroing Parts means no reader can
            /// mistake a partial bound for a whole one. What it does NOT mean is that the quest has been
            /// shown to be impossible - the reach tables are generous in some places and incomplete in
            /// others, and this is not the evidence for that.</summary>
            public bool Unbounded { get; set; }

            public string Reason { get; set; } = "";
        }

        public sealed class Verdict
        {
            public bool Verified => Failures.Count == 0;

            public List<string> Failures { get; } = new();

            /// <summary>Constraints nothing here can score, so this verdict is silent on them. Never
            /// folded into Verified, in either direction.</summary>
            public List<string> Unverifiable { get; } = new();

            /// <summary>Every part in the build is load-bearing: taking any one of them off, together
            /// with whatever is mounted on it, breaks at least one requirement.
            ///
            /// A weaker claim than minimum and a stronger one than the solver could make for itself. It
            /// says nothing about whether some entirely different, smaller arrangement exists - only that
            /// THIS build has no fat on it - and it is checked here, by removing parts and re-deriving
            /// everything, rather than inferred from the passes that built it.</summary>
            public bool Irreducible { get; set; }

            /// <summary>Parts whose removal changed nothing, which is a failure of the search rather than
            /// an observation about the quest.</summary>
            public List<string> Spare { get; } = new();

            /// <summary>Copies beyond the first of any template. Legal when each sits in its own slot,
            /// and worth counting anyway: two identical Kobra sights on one AKS-74N is what started
            /// the search being audited at all.</summary>
            public int Duplicates { get; set; }
        }

        public Verdict Verify(
            MongoId weapon,
            IReadOnlyList<WeaponSolver.FittedPart> parts,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories)
        {
            var verdict = new Verdict();

            if (!Template(weapon, out _))
            {
                verdict.Failures.Add($"no template for the weapon {weapon}");
                return verdict;
            }

            var templates = new List<MongoId>(parts.Count);
            foreach (var part in parts) templates.Add(part.Template);

            CheckThresholds(weapon, templates, thresholds, verdict);
            CheckSeating(weapon, parts, verdict);
            CheckRequiredSlots(weapon, parts, verdict);
            CheckNamed(templates, mustInclude, mustIncludeCategories, verdict);
            CheckDuplicates(parts, verdict);
            CheckIrreducible(weapon, parts, thresholds, mustInclude, mustIncludeCategories, verdict);

            return verdict;
        }

        /// <summary>Re-scores the parts and re-compares every threshold. The comparison is rebuilt
        /// from the condition's own compare string rather than from a flag the solver derived.</summary>
        private void CheckThresholds(
            MongoId weapon,
            List<MongoId> templates,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            Verdict verdict)
        {
            var stats = model.Score(weapon, templates);

            if (stats == null)
            {
                verdict.Failures.Add($"the stat model cannot score {weapon}");
                return;
            }

            var size = Footprint(weapon, templates);

            foreach (var (field, compare, value) in thresholds)
            {
                if (RepairState.Contains(field)) continue;

                if (Sized.Contains(field))
                {
                    var extent = field.ToLowerInvariant() == "height" ? size.Height : size.Width;

                    if (Meets(extent, compare, value)) continue;

                    // A stock that folds or collapses takes room off the gun, and a player who needs it to
                    // fit will fold or collapse it. Reported rather than quietly counted as a pass: whether
                    // the hand-in measures the weapon in its reduced state is a question about the GAME, and
                    // this cannot answer it from the item data. Counting it as a pass on a guess is exactly
                    // the kind of assertion this check exists to remove.
                    var reduced = field.ToLowerInvariant() == "height" ? size.ReducedHeight : size.ReducedWidth;

                    verdict.Failures.Add(size.Reducible && Meets(reduced, compare, value)
                        ? $"{field} is {extent} extended, needs {compare} {value:0.##} - it is {reduced} with the " +
                          "stock folded or collapsed, and whether the hand-in measures it that way is not " +
                          "something this can check from the item data"
                        : $"{field} is {extent}, needs {compare} {value:0.##}");

                    continue;
                }

                if (!Scoreable.Contains(field))
                {
                    verdict.Unverifiable.Add($"{field} {compare} {value:0.##}");
                    continue;
                }

                var actual = Actual(stats, field);

                // A stat this CAN score and the build has nothing for is unmet, not unknown: a
                // magazine-capacity threshold on a gun with no magazine fails.
                if (actual == null)
                {
                    verdict.Failures.Add($"{field} {compare} {value:0.##} - the build provides no value for it");
                    continue;
                }

                if (Meets(actual.Value, compare, value)) continue;

                verdict.Failures.Add($"{field} is {actual.Value:0.##}, needs {compare} {value:0.##}");
            }
        }

        /// <summary>The grid the assembled weapon takes up, unfolded and folded.
        ///
        /// The receiver is 1x1 and every part extends it, which is why a height limit of one is a real
        /// constraint rather than a formality: it says no fitted part may add vertical size at all. The rule
        /// is the game's - each direction takes the LARGEST extension any one part asks for, except parts
        /// marked ExtraSizeForceAdd which stack on top of it.
        ///
        /// SizeReduceRight is reported and NOT applied, which is a deliberate refusal to guess. 63 templates
        /// carry it and only some of those are Foldable - an AKS-74U skeletonised stock and an AK-74M polymer
        /// stock both reduce width without folding at all - so it is the property of a stock that folds or
        /// collapses rather than a folded flag. What the item data does not say is whether the hand-in
        /// measures a weapon extended or reduced, and that decides whether a build measuring 5 wide against a
        /// limit of 4 is a real failure or a gun somebody needs to collapse. Applying it would risk calling a
        /// failing build a pass, which is the one direction a verifier may not err in, so the extended figure
        /// is the verdict and the reduced figure is said out loud beside it.</summary>
        private (int Width, int Height, int ReducedWidth, int ReducedHeight, bool Reducible) Footprint(
            MongoId weapon, List<MongoId> parts)
        {
            if (!Template(weapon, out var item)) return (0, 0, 0, 0, false);

            var props = item.Properties!;

            var width = props.Width ?? 1;
            var height = props.Height ?? 1;

            var up = 0;
            var down = 0;
            var left = 0;
            var right = 0;

            var forcedUp = 0;
            var forcedDown = 0;
            var forcedLeft = 0;
            var forcedRight = 0;

            var reduce = props.SizeReduceRight ?? 0;
            var reducible = props.Foldable == true || reduce > 0;

            foreach (var template in parts)
            {
                if (!Template(template, out var part)) continue;

                var p = part.Properties!;

                // Either makes the gun smaller in a state the player can choose: a folding stock and a
                // collapsing one are the same thing to a size limit, and only checking Foldable would miss
                // every carbine stock in the game.
                if (p.Foldable == true || (p.SizeReduceRight ?? 0) > 0) reducible = true;

                reduce += p.SizeReduceRight ?? 0;

                if (p.ExtraSizeForceAdd == true)
                {
                    forcedUp += p.ExtraSizeUp ?? 0;
                    forcedDown += p.ExtraSizeDown ?? 0;
                    forcedLeft += p.ExtraSizeLeft ?? 0;
                    forcedRight += p.ExtraSizeRight ?? 0;
                    continue;
                }

                up = Math.Max(up, p.ExtraSizeUp ?? 0);
                down = Math.Max(down, p.ExtraSizeDown ?? 0);
                left = Math.Max(left, p.ExtraSizeLeft ?? 0);
                right = Math.Max(right, p.ExtraSizeRight ?? 0);
            }

            var full = width + left + right + forcedLeft + forcedRight;
            var tall = height + up + down + forcedUp + forcedDown;

            return (full, tall, Math.Max(1, full - reduce), tall, reducible);
        }

        /// <summary>Every part must sit in a slot that exists on its parent, that admits it, and that
        /// nothing else already occupies.
        ///
        /// Nothing checked this before. A search that places a part into a slot its filter does not
        /// list produces a build the modding screen refuses, and it would read as a pass all the way
        /// to the hand-in.</summary>
        private void CheckSeating(MongoId weapon, IReadOnlyList<WeaponSolver.FittedPart> parts, Verdict verdict)
        {
            var seats = new HashSet<(int Parent, string Slot)>();

            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];

                // Parent before child, always: anything else is a malformed list rather than a build,
                // and reading it as a tree would be guessing.
                if (part.Parent < -1 || part.Parent >= index)
                {
                    verdict.Failures.Add($"{part.Template} names part {part.Parent} as its host, which is not above it in the list");
                    continue;
                }

                var host = part.Parent < 0 ? weapon : parts[part.Parent].Template;

                if (!Template(host, out var hostItem))
                {
                    verdict.Failures.Add($"no template for {host}, which {part.Template} is fitted to");
                    continue;
                }

                var slot = hostItem.Properties?.Slots?.FirstOrDefault(s => (s.Name ?? "") == part.SlotName);

                if (slot == null)
                {
                    verdict.Failures.Add($"{host} has no slot '{part.SlotName}' to hold {part.Template}");
                    continue;
                }

                if (!Admits(slot, part.Template))
                    verdict.Failures.Add($"slot '{part.SlotName}' on {host} does not admit {part.Template}");

                if (!seats.Add((part.Parent, part.SlotName)))
                    verdict.Failures.Add($"slot '{part.SlotName}' on {host} holds more than one part");
            }
        }

        /// <summary>Every slot the game marks Required, on the weapon AND on every fitted part, has
        /// something in it. A scope mount with an empty required slot is as unassemblable as a
        /// receiver with one, and only the weapon's own slots were ever looked at.</summary>
        private void CheckRequiredSlots(MongoId weapon, IReadOnlyList<WeaponSolver.FittedPart> parts, Verdict verdict)
        {
            var filled = new HashSet<(int Host, string Slot)>();

            for (var index = 0; index < parts.Count; index++)
                filled.Add((parts[index].Parent, parts[index].SlotName));

            CheckRequiredSlotsOf(-1, weapon, filled, verdict);

            for (var index = 0; index < parts.Count; index++)
                CheckRequiredSlotsOf(index, parts[index].Template, filled, verdict);
        }

        private void CheckRequiredSlotsOf(int host, MongoId template, HashSet<(int, string)> filled, Verdict verdict)
        {
            if (!Template(template, out var item)) return;

            foreach (var slot in item.Properties?.Slots ?? Enumerable.Empty<Slot>())
            {
                if (slot?.Required != true) continue;
                if (filled.Contains((host, slot.Name ?? ""))) continue;

                verdict.Failures.Add(
                    $"required slot '{slot.Name}' on {template} is empty - the game will not assemble this");
            }
        }

        private void CheckNamed(
            List<MongoId> templates,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories,
            Verdict verdict)
        {
            foreach (var part in mustInclude)
                if (!templates.Contains(part))
                    verdict.Failures.Add($"the quest names the part {part} and the build does not carry it");

            foreach (var category in mustIncludeCategories)
                if (!templates.Any(template => itemHelper.IsOfBaseclass(template, category)))
                    verdict.Failures.Add($"the quest names the category {category} and no fitted part belongs to it");
        }

        /// <summary>A template may appear twice only by occupying two distinct slots, which
        /// <see cref="CheckSeating"/> has already established both admit it. Anything else is one
        /// physical part claimed twice.</summary>
        private static void CheckDuplicates(IReadOnlyList<WeaponSolver.FittedPart> parts, Verdict verdict)
        {
            foreach (var group in parts.GroupBy(part => part.Template).Where(group => group.Count() > 1))
            {
                var copies = group.ToList();
                var places = copies.Select(part => (part.Parent, part.SlotName)).Distinct().Count();

                verdict.Duplicates += copies.Count - 1;

                if (places == copies.Count) continue;

                verdict.Failures.Add(
                    $"{group.Key} is fitted {copies.Count} times but occupies only {places} distinct slot(s)");
            }
        }

        /// <summary>The fewest parts ANY satisfying build could have, and why.
        ///
        /// This is what turns "nothing can be removed from this build" into "no smaller build exists",
        /// and the two are not the same claim. A build can be removal-closed and still carry one part
        /// more than necessary, because every pass that shrinks a build works by editing THAT build -
        /// none of them can see a different, smaller arrangement.
        ///
        /// So this argues from the other side, and never touches the build at all. Ergonomics is a sum
        /// over fitted parts, so a build with n parts cannot score more than the weapon's base, plus
        /// exactly what the parts the quest NAMES contribute, plus n minus those, each contributing at
        /// most the best single contribution anywhere in reach. If that ceiling is below the threshold
        /// then no build of n parts meets it, whatever it is made of. Recoil is the same argument
        /// through the percentage. Magazine capacity and sighting range are selected rather than summed,
        /// so they need one part that carries enough, and no number of parts substitutes for it.
        ///
        /// Deliberately generous at every step - duplicates allowed, slot feasibility ignored, the best
        /// part assumed available in every slot - because a bound that errs the other way would prove
        /// things that are not true. The cost of that generosity is that it proves fewer builds
        /// minimal, not that it proves any of them wrongly.
        ///
        /// Weight, height and width contribute nothing here: fitting FEWER parts can only help a weight
        /// limit, so no weight threshold ever forces a part.</summary>
        public Floor LowestPossible(
            MongoId weapon,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories)
        {
            var floor = new Floor();

            if (!Template(weapon, out var item)) return floor;

            var reach = Reachable(weapon);

            // Named parts are forced and distinct, so their contribution is exact rather than bounded.
            var named = new List<MongoId>();
            foreach (var part in mustInclude)
                if (reach.ContainsKey(part) && !named.Contains(part)) named.Add(part);

            // A category nothing named covers needs a part of its own.
            var uncovered = 0;
            foreach (var category in mustIncludeCategories)
                if (!named.Any(part => itemHelper.IsOfBaseclass(part, category))) uncovered++;

            floor.Parts = named.Count + uncovered;
            floor.Reason = floor.Parts > 0 ? "the parts and categories the quest names" : "nothing";

            // Every slot the game will not leave empty needs an occupant, that occupant may have
            // required slots of its own, and so on down - so the forced count is a minimum over
            // occupant choices rather than a count of the weapon's own slots. Counting only the top
            // level understates it badly: an M1A forces a barrel, the barrel forces a muzzle device.
            var forced = new Dictionary<MongoId, int>();
            var structural = ForcedBelow(weapon, forced, 0);

            // A named part or a category member that cannot fill ANY required slot anywhere in reach is
            // a part on top of the forced ones rather than one of them, so the two counts add instead of
            // competing. Anything that could fill one is left out of this sum, which keeps it a bound.
            var fillers = new HashSet<MongoId>();

            foreach (var (template, _) in reach)
            {
                if (!Template(template, out var host)) continue;

                foreach (var slot in host.Properties?.Slots ?? Enumerable.Empty<Slot>())
                {
                    if (slot?.Required != true) continue;

                    foreach (var filter in slot.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                        foreach (var candidate in filter?.Filter ?? Enumerable.Empty<MongoId>())
                            fillers.Add(candidate);
                }
            }

            var extra = named.Count(part => !fillers.Contains(part));

            foreach (var category in mustIncludeCategories)
            {
                if (named.Any(part => itemHelper.IsOfBaseclass(part, category))) continue;
                if (fillers.Any(filler => itemHelper.IsOfBaseclass(filler, category))) continue;

                extra++;
            }

            // A magazine holding 30 and a sight seeing 500 metres are TWO parts, and neither of them is
            // the gas block a required slot insists on. Selected stats were being folded in with a max,
            // which threw that away - and it is most of the remaining gap, because a quest asking for a
            // magazine, a sight and two filled slots asks for four parts before a single number is met.
            //
            // Summed only where the sets that could satisfy them are provably disjoint from each other
            // and from the parts already counted: a template that could fill a required slot, or that the
            // quest already names, is not an extra part.
            var selected = 0;
            var claimed = new HashSet<MongoId>();

            // What those parts might contribute to the SUMMED stats while they are busy being a magazine
            // or a sight. Needed because the threshold bound below counts them as budget spent, and it may
            // only do that if it also concedes whatever they could have been worth.
            var selectedErgonomics = 0d;
            var selectedRecoil = 0d;

            foreach (var (field, compare, value) in thresholds)
            {
                if (!compare.StartsWith(">", StringComparison.Ordinal)) continue;

                var provider = field.ToLowerInvariant() switch
                {
                    "magazine capacity" => Providers(reach, value, CapacityOf),
                    "effective distance" => Providers(reach, value, RangeOf),
                    _ => null
                };

                if (provider == null || provider.Count == 0) continue;

                // Already carried by something counted: the weapon itself or a part the quest names. If
                // the model cannot say, this does NOT get to assume an extra part is needed - a bound
                // must fall back to claiming less, never more.
                var carried = model.Score(weapon, named);
                if (carried == null) continue;

                if (Actual(carried, field) is { } already && Meets(already, compare, value)) continue;

                if (fillers.Any(filler => provider.Contains(filler))) continue;

                // Disjoint from every provider already counted, or it might be the same part twice.
                if (provider.Any(claimed.Contains)) continue;

                foreach (var template in provider) claimed.Add(template);

                var bestErgonomicsOf = 0d;
                var bestRecoilOf = 0d;

                foreach (var template in provider)
                {
                    if (!Template(template, out var part)) continue;

                    if ((part.Properties!.Ergonomics ?? 0d) > bestErgonomicsOf)
                        bestErgonomicsOf = part.Properties!.Ergonomics ?? 0d;

                    if (-(part.Properties!.Recoil ?? 0d) > bestRecoilOf)
                        bestRecoilOf = -(part.Properties!.Recoil ?? 0d);
                }

                selectedErgonomics += bestErgonomicsOf;
                selectedRecoil += bestRecoilOf;

                selected++;
            }

            if (structural + extra + selected > floor.Parts)
            {
                floor.Parts = structural + extra + selected;
                floor.Reason = "the slots the game will not leave empty, plus what the quest names and selects";
            }

            // A named part that sits four slots deep needs three parts under it, and a DEPTH LEVEL with
            // no named part at it must be occupied by something the quest did not name. Counting those
            // levels is the chain cost the earlier terms miss entirely, and it is why a bound that only
            // counted named parts came out far below the builds.
            //
            // Levels rather than paths, because the exact answer is the smallest subtree joining the
            // weapon to every named part - a Steiner tree, and genuinely hard. This is the part of it
            // that can be had for one pass.
            var deepest = 0;
            foreach (var part in named)
                if (reach[part] > deepest) deepest = reach[part];

            var chains = 0;
            for (var level = 1; level <= deepest; level++)
                if (!named.Any(part => reach[part] == level)) chains++;

            var routed = named.Count + chains;

            if (routed > floor.Parts)
            {
                floor.Parts = routed;
                floor.Reason = "the parts the quest names and the chains they hang off";
            }

            var props = item.Properties!;
            var baseRecoil = (props.RecoilForceUp ?? 0d) + (props.RecoilForceBack ?? 0d);

            // The best single contribution available anywhere in reach, which is what each part beyond
            // the named ones is allowed to be worth.
            var bestErgonomics = 0d;
            var bestRecoil = 0d;
            var bestMagazine = 0;
            var bestRange = 0d;

            foreach (var (template, _) in reach)
            {
                if (!Template(template, out var part)) continue;

                var p = part.Properties!;

                if ((p.Ergonomics ?? 0d) > bestErgonomics) bestErgonomics = p.Ergonomics ?? 0d;
                if ((p.Recoil ?? 0d) < bestRecoil) bestRecoil = p.Recoil ?? 0d;

                var capacity = p.Cartridges?.FirstOrDefault()?.MaxCount ?? 0;
                if (capacity > bestMagazine) bestMagazine = (int)capacity;

                if ((p.SightingRange ?? 0d) > bestRange) bestRange = p.SightingRange ?? 0d;
            }

            // The best occupant of each slot in reach, one entry per slot, biggest first. Every part
            // beyond the named ones occupies a slot, and no two occupy the same one - so r parts can be
            // worth no more than the r best slots, which is a far tighter statement than r times the
            // best part on the gun.
            var ergonomicSlots = new List<double>();
            var recoilSlots = new List<double>();

            foreach (var (template, _) in reach)
            {
                if (!Template(template, out var host)) continue;

                foreach (var slot in host.Properties?.Slots ?? Enumerable.Empty<Slot>())
                {
                    var ergonomics = 0d;
                    var recoil = 0d;

                    foreach (var filter in slot?.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                        foreach (var candidate in filter?.Filter ?? Enumerable.Empty<MongoId>())
                        {
                            if (!Template(candidate, out var part)) continue;

                            if ((part.Properties!.Ergonomics ?? 0d) > ergonomics) ergonomics = part.Properties!.Ergonomics ?? 0d;
                            if ((part.Properties!.Recoil ?? 0d) < recoil) recoil = part.Properties!.Recoil ?? 0d;
                        }

                    if (ergonomics > 0d) ergonomicSlots.Add(ergonomics);
                    if (recoil < 0d) recoilSlots.Add(recoil);
                }
            }

            ergonomicSlots.Sort((left, right) => right.CompareTo(left));
            recoilSlots.Sort();

            var namedErgonomics = 0d;
            var namedRecoil = 0d;

            foreach (var template in named)
            {
                if (!Template(template, out var part)) continue;

                namedErgonomics += part.Properties!.Ergonomics ?? 0d;
                namedRecoil += part.Properties!.Recoil ?? 0d;
            }

            // What the parts OTHER than the named ones have to supply, in each stat's own units. Not
            // clamped at zero: a threshold the bare weapon already meets contributes a negative amount,
            // which weakens the combined requirement and therefore the bound - and weakening a bound is
            // safe where strengthening it without cause is not.
            var wantsErgonomics = false;
            var wantsRecoil = false;
            var needErgonomics = 0d;
            var needRecoil = 0d;

            foreach (var (field, compare, value) in thresholds)
            {
                switch (field.ToLowerInvariant())
                {
                    case "ergonomics" when compare.StartsWith(">", StringComparison.Ordinal):
                        wantsErgonomics = true;
                        needErgonomics = value - (props.Ergonomics ?? 0d) - namedErgonomics;
                        break;

                    case "recoil" when baseRecoil > 0d:
                        wantsRecoil = true;
                        needRecoil = namedRecoil - (value / baseRecoil - 1d) * 100d;
                        break;
                }
            }

            if (wantsErgonomics || wantsRecoil)
            {
                var joint = 0;

                for (var weighting = 0; weighting < ErgonomicsWeight.Length; weighting++)
                {
                    // A weighting that leans on a threshold the quest does not state proves nothing: the
                    // stat is unconstrained, so its contribution is free and the requirement is empty.
                    if (!wantsErgonomics && ErgonomicsWeight[weighting] > 0d) continue;
                    if (!wantsRecoil && RecoilWeight[weighting] > 0d) continue;

                    var wanted = ErgonomicsWeight[weighting] * needErgonomics
                                 + RecoilWeight[weighting] * needRecoil;

                    // The parts that have to BE a magazine or a sight are budget the summed stats do not
                    // get to spend, so they are taken off the count - and in exchange the requirement is
                    // reduced by the most they could have contributed while doing it. Conceding that is
                    // what keeps this a bound rather than a guess, and the trade is worth it because a
                    // magazine is rarely the part that buys ergonomics.
                    var allowance = ErgonomicsWeight[weighting] * selectedErgonomics
                                    + RecoilWeight[weighting] * selectedRecoil;

                    var needs = Reach(
                        BestBelow(weapon, weighting, 0, new HashSet<MongoId>()),
                        wanted - allowance,
                        named.Count + selected);

                    // Unreachable is not a big number, it is the absence of one, and it cannot be maxed
                    // into a bound. Each weighting is a relaxation every satisfying build must meet, so
                    // one that nothing reaches says the requirement is unsatisfiable - which is a claim
                    // about the data disagreeing with itself, since the solver found a build the verifier
                    // passed. Either way there is no part count to state, so state none.
                    if (needs == null)
                    {
                        floor.Unbounded = true;
                        floor.Parts = 0;
                        // The WEIGHTING is named, not the pair: weighting 0 leans entirely on ergonomics and
                        // the last entirely on recoil, so "both together" would be wrong for either end of
                        // the range even when the quest does constrain both stats.
                        floor.Reason =
                            $"no build of at most {MaxBudget} parts reaches the ergonomics/recoil combination " +
                            $"at weighting {weighting}";

                        return floor;
                    }

                    if (needs > joint) joint = needs.Value;
                }

                if (joint > floor.Parts)
                {
                    floor.Parts = joint;
                    floor.Reason = wantsErgonomics && wantsRecoil
                        ? "ergonomics and recoil together"
                        : wantsErgonomics ? "ergonomics" : "recoil";
                }
            }

            foreach (var (field, compare, value) in thresholds)
            {
                if (RepairState.Contains(field)) continue;
                if (!compare.StartsWith(">", StringComparison.Ordinal) && field.ToLowerInvariant() != "recoil") continue;

                var lower = field.ToLowerInvariant();

                var needs = lower switch
                {
                    "ergonomics" => Spread(value - (props.Ergonomics ?? 0d) - namedErgonomics, bestErgonomics, named.Count),
                    "recoil" => Percent(value, baseRecoil, namedRecoil, bestRecoil, named.Count),
                    "magazine capacity" => Selected(value, bestMagazine, CapacityOf(item), named, CapacityOf),
                    "effective distance" => Selected(value, bestRange, props.SightingRange ?? 0d, named, RangeOf),
                    _ => 0
                };

                if (needs == null)
                {
                    floor.Unbounded = true;
                    floor.Parts = 0;
                    floor.Reason = $"nothing reachable can satisfy {field} {compare} {value:0.##}";

                    return floor;
                }

                if (needs <= floor.Parts) continue;

                floor.Parts = needs.Value;
                floor.Reason = $"{field} {compare} {value:0.##}";
            }

            return floor;
        }

        /// <summary>The most a build of at most k parts can add to one summed stat, for every k, worked
        /// out exactly over the slot tree.
        ///
        /// THIS is what turns "unproven" into "proven". The other arguments in this file assume every
        /// slot in the data is available at once, which is wildly generous - a gun has the slots its own
        /// parts provide and no others - and that generosity is the whole reason they could only prove a
        /// handful of builds. This respects the tree: a slot exists only if something is fitted to hold
        /// it, so spending a part on a mount IS spending a part and the scope it carries costs another.
        ///
        /// A 0/1 knapsack per part over its slots, at every budget, where a slot's worth at cost c is the
        /// best occupant plus the best use of c-1 parts beneath it. Exact for the relaxation "any legal
        /// assembly of at most k parts, ignoring what the quest demands" - and ignoring the demands can
        /// only raise the ceiling, so the bound stays a bound.
        ///
        /// Cached across every weapon on the install, because the answer depends on the item data and
        /// not on which weapon the walk started from: the same handguard is worth the same wherever it
        /// hangs. That is what makes it affordable - one pass over the distinct templates in the game
        /// rather than one pass per quest.</summary>
        /// <remarks>underway is the templates on the path being descended RIGHT NOW, and nothing else. It
        /// was an instance field shared by every thread, which conflated two different things: a graph that
        /// admits its own host (a real cycle, which must be cut) and a template another thread happens to be
        /// working on (not a cycle at all, and cutting it hands back the generous answer for no reason). One
        /// set per descent is both correct and thread-safe without a lock.
        ///
        /// It does NOT replace the depth cap. A cyclic slot graph is an uncatchable StackOverflowException
        /// that would take the server and every player's raid with it, so both guards stay: the visited set
        /// closes the cycles it can see, and the cap catches anything it cannot.</remarks>
        private double[] BestBelow(MongoId template, int weighting, int depth, HashSet<MongoId> underway)
        {
            // Past the depth cap, hand back something deliberately unreachable rather than something
            // small. A zero here would UNDERSTATE the ceiling, and an understated ceiling proves builds
            // minimal that are not - the one failure mode this whole file exists to avoid. Not cached,
            // so a template first met at the cap is still computed properly when met higher up.
            if (depth > MaxReachDepth) return _unreachable;

            var cache = _best[weighting];

            if (cache.TryGetValue(template, out var cached)) return cached;

            // The visited set, and it hands back the generous answer for the same reason as the depth
            // cap: a slot graph that admits its own host must not be scored as worth nothing.
            if (!underway.Add(template)) return _unreachable;

            var best = new double[MaxBudget + 1];

            if (Template(template, out var item))
                foreach (var slot in item.Properties?.Slots ?? Enumerable.Empty<Slot>())
                {
                    var worth = new double[MaxBudget + 1];

                    // A slot the game will not leave empty COSTS a part, and the part it costs is whatever
                    // that slot admits - not whatever the gun would most like to be wearing. Modelling it
                    // as optional is what kept this bound below the builds: it let the hypothetical smaller
                    // gun spend every part on ergonomics and leave its gas block off.
                    var mandatory = slot?.Required == true;

                    if (mandatory) worth[0] = double.NegativeInfinity;

                    foreach (var filter in slot?.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                        foreach (var candidate in filter?.Filter ?? Enumerable.Empty<MongoId>())
                        {
                            if (!Template(candidate, out var part)) continue;

                            var own = ErgonomicsWeight[weighting] * (part.Properties!.Ergonomics ?? 0d)
                                      + RecoilWeight[weighting] * -(part.Properties!.Recoil ?? 0d);

                            var below = BestBelow(candidate, weighting, depth + 1, underway);

                            for (var cost = 1; cost <= MaxBudget; cost++)
                            {
                                var value = own + below[cost - 1];
                                if (value > worth[cost]) worth[cost] = value;
                            }
                        }

                    // A bigger budget is never worth less. Leaving the slot empty is an option only where
                    // the game allows it, which is what the seeded minus-infinity expresses.
                    for (var cost = 1; cost <= MaxBudget; cost++)
                        if (worth[cost] < worth[cost - 1]) worth[cost] = worth[cost - 1];

                    // Fold the slot in, biggest budget first, so what is read on the right of the sum is
                    // always the total WITHOUT this slot and no slot is spent twice.
                    for (var budget = MaxBudget; budget >= 0; budget--)
                    {
                        var take = mandatory ? double.NegativeInfinity : best[budget];

                        for (var spend = 1; spend <= budget; spend++)
                        {
                            if (double.IsNegativeInfinity(best[budget - spend])) continue;
                            if (double.IsNegativeInfinity(worth[spend])) continue;

                            var value = worth[spend] + best[budget - spend];
                            if (value > take) take = value;
                        }

                        best[budget] = take;
                    }
                }

            underway.Remove(template);

            // TryAdd, not assignment: two threads may have computed the same subtree at once and the first
            // answer is as good as the second. Nothing is ever overwritten, so a reader can never see a
            // half-built array.
            cache.TryAdd(template, best);

            return best;
        }

        /// <summary>A ceiling nothing can reach, for the two places where the honest answer is not
        /// available and guessing low would prove something false.</summary>
        private static double[] Unreachable()
        {
            var ceiling = new double[MaxBudget + 1];

            for (var budget = 0; budget <= MaxBudget; budget++)
                ceiling[budget] = budget * HugePerPart;

            return ceiling;
        }

        /// <summary>How many parts the slots below one template force onto the gun: for every slot it
        /// cannot leave empty, the cheapest occupant, plus whatever that occupant forces in turn.
        ///
        /// A minimum over occupant choices, so it is a bound rather than a guess. The dictionary is the
        /// visited set and is written before descending, so a slot graph that admits its own host meets
        /// its own marker instead of looping, and the depth cap bounds it regardless.</summary>
        private int ForcedBelow(MongoId template, Dictionary<MongoId, int> forced, int depth)
        {
            if (depth > MaxReachDepth) return 0;
            if (forced.TryGetValue(template, out var cached)) return cached;
            if (!Template(template, out var item)) return 0;

            forced[template] = 0;

            var total = 0;

            foreach (var slot in item.Properties?.Slots ?? Enumerable.Empty<Slot>())
            {
                if (slot?.Required != true) continue;

                var cheapest = int.MaxValue;

                foreach (var filter in slot.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                    foreach (var candidate in filter?.Filter ?? Enumerable.Empty<MongoId>())
                    {
                        var cost = 1 + ForcedBelow(candidate, forced, depth + 1);
                        if (cost < cheapest) cheapest = cost;
                    }

                if (cheapest != int.MaxValue) total += cheapest;
            }

            forced[template] = total;

            return total;
        }

        /// <summary>The fewest parts in total whose ceiling covers what is wanted, given that
        /// <paramref name="named"/> of them are already spoken for. NULL when no build within the budget
        /// can reach it at all - which is "I cannot bound this", not "the bound is enormous".</summary>
        private static int? Reach(double[] ceiling, double wanted, int named)
        {
            for (var budget = 0; budget < ceiling.Length; budget++)
            {
                // Minus infinity is a budget too small to fill the slots the game insists on, which is
                // not a build at all however good its numbers would have been.
                if (double.IsNegativeInfinity(ceiling[budget])) continue;
                if (ceiling[budget] >= wanted) return named + budget;
            }

            return null;
        }

        /// <summary>How many parts, each worth at most <paramref name="best"/>, it takes to cover a
        /// shortfall. Unconditional, and loose precisely because it allows the same part twice. Null when
        /// nothing reachable moves the stat in the right direction at all.</summary>
        private static int? Spread(double shortfall, double best, int named)
        {
            if (shortfall <= 0d) return named;
            if (best <= 0d) return null;

            return named + (int)Math.Ceiling(shortfall / best);
        }

        /// <summary>The same argument through recoil's percentage. Null on the same terms as Spread.</summary>
        private static int? Percent(double threshold, double baseRecoil, double namedPercent, double bestPercent, int named)
        {
            if (baseRecoil <= 0d) return named;

            // The build needs the summed percentage to be at least this negative.
            var wanted = (threshold / baseRecoil - 1d) * 100d;
            var shortfall = namedPercent - wanted;

            if (shortfall <= 0d) return named;
            if (bestPercent >= 0d) return null;

            return named + (int)Math.Ceiling(shortfall / -bestPercent);
        }

        /// <summary>Every reachable template that carries enough of a selected stat on its own.</summary>
        private static HashSet<MongoId> Providers(
            Dictionary<MongoId, int> reach, double threshold, Func<MongoId, double> of)
        {
            var providers = new HashSet<MongoId>();

            foreach (var (template, _) in reach)
                if (of(template) >= threshold) providers.Add(template);

            return providers;
        }

        /// <summary>A selected stat needs ONE part that carries enough, and no number of lesser parts
        /// substitutes. Zero extra when the weapon or a named part already carries it. Null when nothing
        /// reachable carries enough - a 100-round magazine requirement with no such magazine in reach.</summary>
        private static int? Selected(
            double threshold, double bestAvailable, double onWeapon, List<MongoId> named, Func<MongoId, double> of)
        {
            if (onWeapon >= threshold) return named.Count;

            foreach (var part in named)
                if (of(part) >= threshold) return named.Count;

            return bestAvailable >= threshold ? named.Count + 1 : (int?)null;
        }

        private double RangeOf(MongoId template) =>
            Template(template, out var item) ? item.Properties!.SightingRange ?? 0d : 0d;

        private double CapacityOf(MongoId template) =>
            Template(template, out var item) ? CapacityOf(item) : 0d;



        private static double CapacityOf(TemplateItem item) =>
            item.Properties?.Cartridges?.FirstOrDefault()?.MaxCount ?? 0d;

        /// <summary>Every template the weapon's slots can reach, walked here rather than taken from the
        /// graph so that the bound rests on the item data and not on the search's view of it.
        ///
        /// The visited set is what makes a cyclic slot graph terminate, and the depth cap bounds it even
        /// if the set somehow did not.</summary>
        private Dictionary<MongoId, int> Reachable(MongoId weapon)
        {
            var seen = new Dictionary<MongoId, int> { [weapon] = 0 };
            var queue = new Queue<MongoId>();

            queue.Enqueue(weapon);

            while (queue.Count > 0)
            {
                var template = queue.Dequeue();
                var depth = seen[template];

                if (depth >= MaxReachDepth) continue;
                if (!Template(template, out var item)) continue;

                foreach (var slot in item.Properties?.Slots ?? Enumerable.Empty<Slot>())
                    foreach (var filter in slot?.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                        foreach (var candidate in filter?.Filter ?? Enumerable.Empty<MongoId>())
                            if (seen.TryAdd(candidate, depth + 1)) queue.Enqueue(candidate);
            }

            return seen;
        }

        /// <summary>Takes every part off in turn and confirms the build stops satisfying the quest.
        ///
        /// This is the proof that applies to ALL builds rather than the few whose size matches a bound:
        /// not "no smaller build exists", which needs an argument about every build there could be, but
        /// "nothing here is spare", which needs only this one. Both are worth having and they are not the
        /// same claim, so they are reported separately and never merged.
        ///
        /// Whatever is mounted on a part comes off with it, because that is what removing it means. A
        /// part whose removal changes nothing is a hole in the search, not a fact about the quest, so it
        /// is named.</summary>
        private void CheckIrreducible(
            MongoId weapon,
            IReadOnlyList<WeaponSolver.FittedPart> parts,
            IReadOnlyList<(string Field, string Compare, double Value)> thresholds,
            IReadOnlyCollection<MongoId> mustInclude,
            IReadOnlyCollection<MongoId> mustIncludeCategories,
            Verdict verdict)
        {
            // Only meaningful on a build that satisfies the quest in the first place.
            if (!verdict.Verified) return;

            verdict.Irreducible = true;

            for (var index = 0; index < parts.Count; index++)
            {
                var without = new List<WeaponSolver.FittedPart>(parts.Count);
                var gone = new HashSet<int> { index };

                // Parents always precede children, so one forward pass closes the subtree.
                for (var other = 0; other < parts.Count; other++)
                {
                    if (gone.Contains(other)) continue;

                    if (parts[other].Parent >= 0 && gone.Contains(parts[other].Parent))
                    {
                        gone.Add(other);
                        continue;
                    }

                    without.Add(parts[other]);
                }

                // Re-derived from scratch on the smaller build, exactly as for the real one. Seating and
                // duplicates cannot break by REMOVING parts, so only the requirements are re-asked.
                var lighter = new Verdict();

                var templates = new List<MongoId>(without.Count);
                foreach (var part in without) templates.Add(part.Template);

                CheckThresholds(weapon, templates, thresholds, lighter);
                CheckRequiredSlots(weapon, RenumberedWithout(parts, gone), lighter);
                CheckNamed(templates, mustInclude, mustIncludeCategories, lighter);

                if (!lighter.Verified) continue;

                verdict.Irreducible = false;
                verdict.Spare.Add($"{parts[index].Template} in '{parts[index].SlotName}' carries nothing the quest needs");
            }
        }

        /// <summary>The build minus a subtree, with parent indices renumbered so the result is still a
        /// tree that can be read the same way.</summary>
        private static List<WeaponSolver.FittedPart> RenumberedWithout(
            IReadOnlyList<WeaponSolver.FittedPart> parts, HashSet<int> gone)
        {
            var moved = new int[parts.Count];
            var kept = new List<WeaponSolver.FittedPart>(parts.Count);

            for (var index = 0; index < parts.Count; index++)
            {
                if (gone.Contains(index))
                {
                    moved[index] = -1;
                    continue;
                }

                moved[index] = kept.Count;

                kept.Add(new WeaponSolver.FittedPart
                {
                    SlotName = parts[index].SlotName,
                    Template = parts[index].Template,
                    Depth = parts[index].Depth,
                    Parent = parts[index].Parent < 0 ? -1 : moved[parts[index].Parent]
                });
            }

            return kept;
        }

        /// <summary>Whether a slot's filters admit a template.
        ///
        /// ANY filter, not the first one. The graph reads only the first, which is right for every
        /// weapon slot in the data - and a verifier that repeated the assumption could not catch it
        /// being wrong. Reading more widely here means this can only ever be more permissive than the
        /// search, so it never rejects a legal build and still catches an illegal one.</summary>
        private static bool Admits(Slot slot, MongoId template)
        {
            foreach (var filter in slot.Properties?.Filters ?? Enumerable.Empty<SlotFilter>())
                if (filter?.Filter?.Contains(template) == true) return true;

            return false;
        }

        private bool Template(MongoId id, out TemplateItem item)
        {
            item = null!;

            var items = templateTable.Items;
            if (items == null) return false;

            if (!items.TryGetValue(id, out var found) || found?.Properties == null) return false;

            item = found;
            return true;
        }

        /// <summary>The build's value for one condition field. Its own mapping, on purpose.</summary>
        private static double? Actual(WeaponStatModel.Stats stats, string field) => field.ToLowerInvariant() switch
        {
            "ergonomics" => stats.Ergonomics,
            "recoil" => stats.Recoil,
            "weight" => stats.Weight,
            "magazine capacity" => stats.MagazineCapacity,
            "effective distance" => stats.EffectiveDistance,
            _ => null
        };

        /// <summary>The condition's comparison, rebuilt from the string the quest data carries.
        ///
        /// An unrecognised operator is NOT treated as satisfied. A modded condition can write anything
        /// here, and reading "whatever this is, call it a pass" would turn an unknown into a green
        /// tick - the one outcome a verifier must never produce.</summary>
        private bool Meets(double actual, string compare, double value) => compare switch
        {
            ">=" => actual >= value,
            ">" => actual > value,
            "<=" => actual <= value,
            "<" => actual < value,
            "=" or "==" => Math.Abs(actual - value) < 1e-9,
            _ => Unrecognised(compare)
        };

        private bool Unrecognised(string compare)
        {
            logger.Warning(
                $"Quest Tracker: a weapon-build condition compares with '{compare}', which this verifier " +
                "does not know. Treated as NOT met, because an unknown operator must not read as a pass.");

            return false;
        }
    }
}


