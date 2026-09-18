using System;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// What a weapon plus a set of fitted mods actually scores, in the terms a Gunsmith quest
    /// compares against.
    ///
    /// This exists on its own, and before any build generator, BECAUSE it is the part that can be
    /// silently wrong. A solver built on a wrong model produces builds that look right, pass our own
    /// check, and then fail at the hand-in - which is worse than having no generator at all, because
    /// the player has no way to tell whose fault it is.
    ///
    /// So this ships first and gets proven against the running game before a solver is written. See
    /// <see cref="Describe"/>, which is the whole verification instrument: assemble a weapon in game,
    /// read the ergonomics and recoil the inspect screen shows, and compare. Several weapons,
    /// including one that fails a threshold. If they disagree, the model is wrong and the solver must
    /// not be started.
    ///
    /// What is modelled, and how each combines - derived from the item templates rather than guessed:
    ///
    ///   ergonomics          base + the sum of every fitted mod's Ergonomics, which is often negative
    ///   recoil              (RecoilForceUp + RecoilForceBack) scaled by the summed mod Recoil PERCENT
    ///   weight              base + the sum of every fitted mod's Weight
    ///   magazine capacity   SELECTED from the fitted magazine, never summed
    ///   effective distance  the MAX SightingRange over fitted sights
    ///
    /// Five rules, not one, and that is the point: a gate that only checks ergonomics and recoil
    /// passes while three of the families remain unmodelled.
    ///
    /// Two things deliberately absent. Durability is not a build property at all - it is the
    /// weapon's repair state, it appears in all 32 vanilla conditions, and no assembly can change
    /// it. Height and width are the assembled grid size, which folding stocks change; they are real
    /// in five quests each and are NOT modelled here.
    ///
    /// That is a statement about THIS class, not an instruction to its callers. It used to end "so any
    /// solver must treat them as unmodelled rather than assume they pass", and both the solver and the
    /// verifier now score them off the assembled grid instead - see WeaponSolver.Sized. Judging them is
    /// fine; judging them from these numbers is what is not available.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponStatModel(ISptLogger<WeaponStatModel> logger, TemplateTable templateTable)
    {
        /// <summary>What a build scores. Null fields are stats this model does not claim to know,
        /// which a caller must not read as "passes".</summary>
        public sealed class Stats
        {
            public double Ergonomics { get; set; }
            public double Recoil { get; set; }

            /// <summary>Templates only: the gun and its parts, unloaded. The game's hand-in gate
            /// compares TotalWeight, which INCLUDES the rounds in the magazine and chamber, against a
            /// "weight at most" threshold - so a build this model passes by a narrow margin can be
            /// refused at the trader once loaded. Known, not modelled: the model cannot know what
            /// ammo the player will load. The client-side gate check is what says so.</summary>
            public double Weight { get; set; }

            /// <summary>Null when no fitted part carries a magazine, which is not the same as zero.</summary>
            public int? MagazineCapacity { get; set; }

            /// <summary>Null when no fitted part carries a sighting range.</summary>
            public double? EffectiveDistance { get; set; }

            /// <summary>Stats that were clamped on the way in, for the log. A modded part with an
            /// absurd value would otherwise make "best still reachable" satisfy every threshold, so
            /// nothing ever prunes and a search degenerates to exhaustive - the node ceiling stops
            /// being a safety net and becomes the normal operating mode.</summary>
            public List<string> Clamped { get; } = new();
        }

        /// <summary>Bounds on one part's contribution. Generous by an order of magnitude against
        /// anything vanilla, and finite, which is the whole job.</summary>
        private const double MaxErgonomics = 200d;
        private const double MaxRecoilPercent = 100d;
        private const double MaxWeight = 100d;

        /// <summary>Scores a weapon with an exact set of fitted mods.
        ///
        /// <paramref name="fitted"/> is every mod on the gun, at any depth - this does not walk the
        /// slot tree itself, because the caller that has a build already knows what is in it, and a
        /// walk here would be a second place for the traversal to be wrong.</summary>
        public Stats? Score(MongoId weapon, IEnumerable<MongoId> fitted)
        {
            var items = templateTable.Items;
            if (items == null) return null;

            if (!items.TryGetValue(weapon, out var baseItem) || baseItem?.Properties == null)
            {
                logger.Warning($"Quest Tracker: no template for weapon '{weapon}' - cannot score a build.");
                return null;
            }

            var props = baseItem.Properties;
            var stats = new Stats
            {
                Ergonomics = props.Ergonomics ?? 0d,
                Weight = props.Weight ?? 0d
            };

            // The combined figure, vertical plus horizontal. Mods scale it rather than adding to it.
            var baseRecoil = (props.RecoilForceUp ?? 0d) + (props.RecoilForceBack ?? 0d);
            var recoilPercent = 0d;

            double? sightingRange = props.SightingRange > 0 ? props.SightingRange : null;

            // The game's GetMaxMagazineCount is GetCurrentMagazine()?.MaxCount ?? 0: the magazine in
            // the magazine slot, or nothing. So the weapon's own Cartridges do not count, and neither
            // does any other part that happens to carry a cartridge list - an underbarrel launcher's
            // chamber inflated this to 1 on builds with no magazine at all.
            int? magazine = null;

            foreach (var id in fitted ?? Enumerable.Empty<MongoId>())
            {
                if (!items.TryGetValue(id, out var mod) || mod?.Properties == null) continue;

                stats.Ergonomics += Clamp(mod.Properties.Ergonomics ?? 0d, MaxErgonomics, "ergonomics", id, stats);
                stats.Weight += Clamp(mod.Properties.Weight ?? 0d, MaxWeight, "weight", id, stats);
                recoilPercent += Clamp(mod.Properties.Recoil ?? 0d, MaxRecoilPercent, "recoil", id, stats);

                // MAX over sights, not a sum: fitting two scopes does not see twice as far.
                var range = mod.Properties.SightingRange;
                if (range > 0 && (sightingRange == null || range > sightingRange)) sightingRange = range;

                // SELECTED from the fitted magazine, not summed: two magazines is still one gun -
                // and only from a part that IS a magazine, the way the game reads it.
                if (TemplateClasses.IsA(items, mod, TemplateClasses.Magazine))
                {
                    var capacity = CapacityOf(mod);
                    if (capacity != null && (magazine == null || capacity > magazine)) magazine = capacity;
                }
            }

            stats.Recoil = baseRecoil * (1d + recoilPercent / 100d);
            stats.EffectiveDistance = sightingRange;
            stats.MagazineCapacity = magazine;

            return stats;
        }

        /// <summary>The magazine capacity a part offers, from its first cartridge slot's maximum
        /// stack. Null when the part is not a magazine.</summary>
        private static int? CapacityOf(TemplateItem item)
        {
            var cartridge = item.Properties?.Cartridges?.FirstOrDefault();
            var max = cartridge?.MaxCount;

            return max > 0 ? (int)max : null;
        }

        /// <summary>Bounds one part's contribution, and records that it did.
        ///
        /// Nothing in the item data forbids a modded part claiming +100000 ergonomics, and one that
        /// does would not merely skew a number - it would make every bound satisfiable, so a search
        /// over it never prunes.</summary>
        private static double Clamp(double value, double limit, string stat, MongoId id, Stats stats)
        {
            if (double.IsNaN(value)) { stats.Clamped.Add($"{stat} of {id} was NaN"); return 0d; }
            if (value > limit) { stats.Clamped.Add($"{stat} of {id} was {value:0.#}"); return limit; }
            if (value < -limit) { stats.Clamped.Add($"{stat} of {id} was {value:0.#}"); return -limit; }

            return value;
        }

        /// <summary>THE VERIFICATION INSTRUMENT. One line saying what this model believes about an
        /// exact part list, so it can be read against what the game's own inspect screen shows.
        ///
        /// This is what the gate is performed with, and it is the reason the model ships before any
        /// solver. Logged at Debug so it is off unless somebody is looking for it.</summary>
        public void Describe(MongoId weapon, IEnumerable<MongoId> fitted)
        {
            var ids = (fitted ?? Enumerable.Empty<MongoId>()).ToList();
            var stats = Score(weapon, ids);

            if (stats == null)
            {
                logger.Debug($"Quest Tracker: weapon model - no template for '{weapon}'.");
                return;
            }

            logger.Debug(
                $"Quest Tracker: weapon model for '{weapon}' with {ids.Count} mods - " +
                $"ergonomics {stats.Ergonomics:0.##}, recoil {stats.Recoil:0.##}, weight {stats.Weight:0.###} kg, " +
                $"magazine {(stats.MagazineCapacity?.ToString() ?? "n/a")}, " +
                $"effective distance {(stats.EffectiveDistance?.ToString("0") ?? "n/a")}. " +
                "Compare these against the game's own inspect screen for the same parts; if they " +
                "disagree the model is wrong and no build generator may be written on it." +
                (stats.Clamped.Count > 0 ? $" CLAMPED: {string.Join("; ", stats.Clamped)}." : ""));
        }
    }
}
