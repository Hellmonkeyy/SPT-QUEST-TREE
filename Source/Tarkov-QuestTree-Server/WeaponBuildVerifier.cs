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
