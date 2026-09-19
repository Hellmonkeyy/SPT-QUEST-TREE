using System.Collections.Concurrent;
using System.Collections.Generic;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;

namespace QuestTreeServer
{
    /// <summary>
    /// What the game itself puts on a weapon out of the box: the default preset, reduced to "which
    /// template occupies which slot of which part".
    ///
    /// WHY A BUILD NEEDS THIS. Two builds of the same size are the same to the cost function and not at
    /// all the same to a player. The MP-133 default ships with a 510mm barrel; the search picked the
    /// 510mm barrel WITH RIB. Identical part count, identical everything the thresholds can see - and one
    /// of them is already on the gun while the other is a trip to a trader. Multiplied over sixty builds
    /// that is a shopping list nobody needed.
    ///
    /// So the preset is a tiebreak, and only a tiebreak. It ranks below part count, which means it can
    /// never make a build bigger, and it changes no bound: every bound here is computed over part count
    /// alone, so which particular part sits in a slot is invisible to the proof.
    ///
    /// 399 presets exist on the reference install and the MP-133 has exactly one. A weapon with NONE -
    /// which a modded weapon may well be - gets a null here and everything downstream shows what it
    /// always did. Nothing guesses at a default.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponPresets(ISptLogger<WeaponPresets> logger, PresetHelper presetHelper)
    {
        /// <summary>One weapon's factory configuration.</summary>
        public sealed class Defaults
        {
            /// <summary>Keyed by the HOST's template and the slot name, because a slot name alone is not a
            /// place on a gun: "mod_scope" exists on the receiver and again on every mount that can carry
            /// one, and a part that matches the default in one of them has not matched it in another.</summary>
            public IReadOnlyDictionary<(MongoId Host, string Slot), MongoId> Occupants { get; init; } =
                new Dictionary<(MongoId, string), MongoId>();

            public int Count => Occupants.Count;

            /// <summary>Which templates the preset carries at all, ignoring where. The slot-blind question,
            /// and the only thing still asked of it is "does this weapon's preset mention this part" - as a
            /// set of candidates to price, in PartAvailability.FreeCopyBudget.
            ///
            /// AN `OccupiesAnySlot(template)` PREDICATE OVER THIS SET IS GONE, with the answer it gave: a
            /// build carrying TWO copies of a part the preset carries once showed BOTH rows as fitted and
            /// free, while WeaponSolver.Priced keyed on (host, slot) and charged for the second, so the
            /// display under-reported what the search itself optimised against. CopiesOf answers with the
            /// number instead, which is what every caller actually needed; the yes-or-no question has no
            /// callers left and is not kept for one.
            ///
            /// A set rather than the Values.Contains scan it replaces, which was O(n) per part per
            /// classify. Built with the object rather than lazily: one Defaults is cached per weapon and
            /// read by every training thread at once - however many that is, which is half the processor
            /// count and not the fifteen this comment first claimed - so a `??=` here would let one of them
            /// publish a half-built HashSet to the others.</summary>
            public IReadOnlySet<MongoId> AnySlot { get; init; } = new HashSet<MongoId>();

            /// <summary>HOW MANY of the preset's slots this template fills - the number a set of templates
            /// cannot hold. A preset that carries one rail gives the gun one rail: a build that fits two is
            /// one rail short and somebody has to buy it. Counted here so the answer costs a lookup rather
            /// than a scan of Occupants' values per part per classify - the same reason AnySlot is a set -
            /// and assigned with the object for the same reason it is.
            ///
            /// Zero for a template the preset does not carry, which is the honest answer and not a claim
            /// that the weapon has no preset: Defaults is null when there is none.</summary>
            public int CopiesOf(MongoId template) => Copies.TryGetValue(template, out var copies) ? copies : 0;

            /// <summary>Occupants' values counted per template, which CopiesOf reads. Not to be confused
            /// with Count, which is how many slots the preset fills in total.</summary>
            public IReadOnlyDictionary<MongoId, int> Copies { get; init; } = new Dictionary<MongoId, int>();
        }

        /// <summary>Cached per weapon, and the miss is cached too - a weapon with no preset must not be
        /// looked up again on every measurement of every build.</summary>
        private readonly ConcurrentDictionary<MongoId, Defaults?> _known = new();

        /// <summary>The weapon's default configuration, or null when the game ships none for it.</summary>
        public Defaults? For(MongoId weapon) => _known.GetOrAdd(weapon, Read);

        /// <summary>How many of a set of weapons have a default preset. Reported at boot rather than
        /// assumed: this reads an SPT helper, and "no weapon has a preset" and "the lookup does not do
        /// what I think" produce identical silence otherwise.</summary>
        public int Known(IEnumerable<MongoId> weapons)
        {
            var known = 0;

            foreach (var weapon in weapons)
                if (For(weapon) != null) known++;

            return known;
        }

        private Defaults? Read(MongoId weapon)
        {
            try
            {
                var items = presetHelper.GetDefaultPreset(weapon)?.Items;

                if (items == null || items.Count == 0) return null;

                // The preset is a flat item list held together by ids, so the templates have to be
                // resolved first - a child names its host by ID and the map has to be keyed by the host's
                // TEMPLATE, which is all a build in progress knows about the part it is fitted to.
                var templates = new Dictionary<MongoId, MongoId>(items.Count);

                foreach (var item in items) templates[item.Id] = item.Template;

                var occupants = new Dictionary<(MongoId Host, string Slot), MongoId>(items.Count);

                foreach (var item in items)
                {
                    if (item.ParentId is not { } parent) continue;
                    if (string.IsNullOrEmpty(item.SlotId)) continue;
                    if (!templates.TryGetValue(parent, out var host)) continue;

                    // TryAdd: a preset that somehow names one slot twice keeps its first answer rather
                    // than throwing inside a lookup nothing important depends on.
                    occupants.TryAdd((host, item.SlotId!), item.Template);
                }

                if (occupants.Count == 0) return null;

                var copies = new Dictionary<MongoId, int>(occupants.Count);

                foreach (var template in occupants.Values)
                    copies[template] = copies.TryGetValue(template, out var seen) ? seen + 1 : 1;

                return new Defaults
                {
                    Occupants = occupants,
                    AnySlot = new HashSet<MongoId>(occupants.Values),
                    Copies = copies
                };
            }
            catch (System.Exception ex)
            {
                // A preset nobody can read is the same as no preset: every row shows unmarked and the
                // heading counts parts. It is not a reason to fail a payload.
                logger.Warning($"Quest Tracker: could not read the default preset for '{weapon}' ({ex.Message}).");
                return null;
            }
        }
    }
}
