using System;
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

        /// <summary>Not a property of the assembly at all - it is the weapon's repair state, no
        /// arrangement of parts changes it, and it appears in all 32 vanilla conditions.</summary>
        private static readonly HashSet<string> RepairState = new(StringComparer.OrdinalIgnoreCase)
        {
            "durability"
        };

        /// <summary>How deep this walks a weapon's slots. Vanilla's deepest real chain is five; the cap
        /// is what stops a cyclic graph from the visited set's blind side.</summary>
        private const int MaxReachDepth = 12;

        /// <summary>The fewest parts any satisfying build could have, and what forces it.
        ///
        /// Two numbers, because two different claims are available and conflating them would overstate
        /// one of them. <see cref="Parts"/> assumes nothing whatsoever: it allows the hypothetical
        /// smaller build to fit the single best part in reach as many times as it likes, which the game
        /// does permit. That makes it unarguable and very loose. <see cref="Distinct"/> adds one stated
        /// assumption - that no HOST template appears twice on the gun, so each slot in the data is
        /// available once - which is true of every build this solver produces and of essentially every
        /// build anybody assembles, and it is sharper by a wide margin because the one slot whose best
        /// occupant is worth +15 ergonomics is one slot, not twelve.</summary>
        public sealed class Floor
        {
            /// <summary>Unconditional. No build with fewer parts than this can satisfy the quest.</summary>
            public int Parts { get; set; }

            /// <summary>The same, assuming no host template is fitted more than once.</summary>
            public int Distinct { get; set; }

            public string Reason { get; set; } = "";
        }

        public sealed class Verdict
        {
            public bool Verified => Failures.Count == 0;

            public List<string> Failures { get; } = new();

            /// <summary>Constraints nothing here can score, so this verdict is silent on them. Never
            /// folded into Verified, in either direction.</summary>
            public List<string> Unverifiable { get; } = new();

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

            foreach (var (field, compare, value) in thresholds)
            {
                if (RepairState.Contains(field)) continue;

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
            floor.Distinct = floor.Parts;
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

            if (structural + extra > floor.Parts)
            {
                floor.Parts = structural + extra;
                floor.Reason = "the slots the game will not leave empty, plus what the quest names";
            }

            if (structural + extra > floor.Distinct) floor.Distinct = structural + extra;

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

            if (routed > floor.Distinct) floor.Distinct = routed;

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

                var sharper = lower switch
                {
                    "ergonomics" => Fill(value - (props.Ergonomics ?? 0d) - namedErgonomics, ergonomicSlots, named.Count, 1d),
                    "recoil" => baseRecoil <= 0d
                        ? named.Count
                        : Fill(namedRecoil - (value / baseRecoil - 1d) * 100d, recoilSlots, named.Count, -1d),
                    _ => needs
                };

                if (sharper > floor.Distinct) floor.Distinct = sharper;

                if (needs <= floor.Parts) continue;

                floor.Parts = needs;
                floor.Reason = $"{field} {compare} {value:0.##}";
            }

            return floor;
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

        /// <summary>How many of the best available SLOTS it takes to cover a shortfall, each slot
        /// counted once. <paramref name="sign"/> is 1 for a stat where more is better and -1 for recoil,
        /// whose entries are negative percentages.</summary>
        private static int Fill(double shortfall, List<double> slots, int named, double sign)
        {
            if (shortfall <= 0d) return named;

            var covered = 0d;

            for (var index = 0; index < slots.Count; index++)
            {
                covered += slots[index] * sign;

                if (covered >= shortfall) return named + index + 1;
            }

            return int.MaxValue;
        }

        /// <summary>How many parts, each worth at most <paramref name="best"/>, it takes to cover a
        /// shortfall. Unconditional, and loose precisely because it allows the same part twice.</summary>
        private static int Spread(double shortfall, double best, int named)
        {
            if (shortfall <= 0d) return named;
            if (best <= 0d) return int.MaxValue;

            return named + (int)Math.Ceiling(shortfall / best);
        }

        /// <summary>The same argument through recoil's percentage.</summary>
        private static int Percent(double threshold, double baseRecoil, double namedPercent, double bestPercent, int named)
        {
            if (baseRecoil <= 0d) return named;

            // The build needs the summed percentage to be at least this negative.
            var wanted = (threshold / baseRecoil - 1d) * 100d;
            var shortfall = namedPercent - wanted;

            if (shortfall <= 0d) return named;
            if (bestPercent >= 0d) return int.MaxValue;

            return named + (int)Math.Ceiling(shortfall / -bestPercent);
        }

        /// <summary>A selected stat needs ONE part that carries enough, and no number of lesser parts
        /// substitutes. Zero extra when the weapon or a named part already carries it.</summary>
        private static int Selected(
            double threshold, double bestAvailable, double onWeapon, List<MongoId> named, Func<MongoId, double> of)
        {
            if (onWeapon >= threshold) return named.Count;

            foreach (var part in named)
                if (of(part) >= threshold) return named.Count;

            return bestAvailable >= threshold ? named.Count + 1 : int.MaxValue;
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

