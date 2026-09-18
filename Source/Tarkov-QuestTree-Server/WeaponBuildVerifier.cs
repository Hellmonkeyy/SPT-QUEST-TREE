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
            CheckConflicts(weapon, templates, verdict);
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

        /// <summary>No two parts on the gun refuse each other.
        ///
        /// The verifier had no opinion on conflicts at all, which is the one gap that could pass a build the
        /// game itself rejects. Every other failure here is "the numbers do not add up"; this one is "the
        /// modding screen will not let you build it", and the player meets it at the workbench holding a
        /// preset the mod told them was verified.
        ///
        /// It matters most for a build restored from the cache. The fingerprint now covers ConflictingItems
        /// so a mod update invalidates the file - but Rebuild takes a stored build as the incumbent without
        /// re-measuring it, so within one boot this is the check standing between a conflicting pair and a
        /// saved preset.
        ///
        /// BOTH directions, because ConflictingItems is not symmetric in the data - the solver's Compatible
        /// says so and is right. Scanning every part's own list against the set of everything present gets
        /// both for free: A naming B is found while looking at A, and B naming A while looking at B.
        ///
        /// The weapon is in the set too. A part that refuses the receiver it is being bolted to is the same
        /// refusal, and nothing else here would catch it.</summary>
        private void CheckConflicts(MongoId weapon, List<MongoId> templates, Verdict verdict)
        {
            var present = new HashSet<MongoId>(templates) { weapon };
            var said = new HashSet<string>(StringComparer.Ordinal);

            foreach (var template in present)
            {
                if (!Template(template, out var item)) continue;

                var conflicts = item.Properties?.ConflictingItems;
                if (conflicts == null) continue;

                foreach (var conflict in conflicts)
                {
                    // A part listing ITSELF is not a conflict, and the data does it: five items in
                    // WTT-ContentBackport's stock config name their own id among 54 others, and on this
                    // install that rejected a build the solver had just produced - "6984b82c... and
                    // 6984b82c... conflict", the same id twice, on Weapon Acquisition V.
                    //
                    // The solver never tripped on it because Compatible tests a candidate before it enters
                    // Counts, so it never sees itself; this walks the finished gun, where it does. What the
                    // flag means for one instance is nothing - and two copies of one part is what
                    // CheckDuplicates already answers, on the slots rather than on this list.
                    if (conflict == template) continue;

                    if (!present.Contains(conflict)) continue;

                    // One line per unordered pair. With both directions scanned, a symmetric pair would
                    // otherwise be reported twice and read as two problems.
                    var first = string.CompareOrdinal(template.ToString(), conflict.ToString()) <= 0;
                    var key = first ? $"{template}|{conflict}" : $"{conflict}|{template}";

                    if (!said.Add(key)) continue;

                    verdict.Failures.Add(
                        $"{template} and {conflict} conflict, so the gun cannot be assembled");
                }
            }
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

        /// <summary>Takes every part off in turn and confirms the build stops satisfying the quest.
        ///
        /// This is the one proof kept, and it applies to EVERY build rather than to a favoured few:
        /// not "no smaller build exists", which needs an argument about every build there could be, but
        /// "nothing here is spare", which needs only this one. The first claim used to be made too, by a
        /// lower bound; that was removed, and this one stands on its own.
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


