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
    /// What can be fitted where, flattened out of the item templates once per boot.
    ///
    /// The item table answers "what may go in this slot" through four levels of indirection -
    /// Properties.Slots, then a slot's Properties.Filters, then the first filter's Filter set - and
    /// that walk happens once per candidate per slot per partial build in a search. Doing it against
    /// the live table every time is the difference between a solver that answers and one that does
    /// not, so it is done here, once, into flat arrays.
    ///
    /// THE HAZARD THIS TYPE EXISTS TO CONTAIN
    ///
    /// A cycle in the slot graph kills the server outright. Nothing in the data forbids one: a modded
    /// adapter whose slot filter admits an item whose own filter admits the adapter back is enough,
    /// and this install carries 28 modded weapon-build conditions. Unbounded recursion here is a
    /// StackOverflowException, which is the one exception .NET will not let anybody catch - the
    /// route's Guarded wrapper never sees it, and the SPT process dies, taking every player's raid
    /// with it.
    ///
    /// So every walk in this file carries a visited set on template ids AND a hard depth cap, and
    /// neither is optional. ProfileInventory designed this same hazard away by descending from known
    /// roots rather than climbing from every item; this type cannot use that trick, because a slot
    /// graph has no roots - it reintroduces the hazard deliberately and has to handle it head on.
    ///
    /// Built at boot rather than on demand, for the reason QuestPayloadBuilder gives: the client's
    /// request handler is synchronous on Unity's main thread, so paying for a multi-second build
    /// inside a GET freezes the game.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponGraph(ISptLogger<WeaponGraph> logger, TemplateTable templateTable)
    {
        /// <summary>How deep a slot chain may nest before this stops descending.
        ///
        /// Vanilla's deepest is a receiver into a handguard into a rail into a mount into a sight,
        /// which is five. Twelve is generous enough that no real weapon reaches it and small enough
        /// that a cyclic graph is stopped long before the stack is.</summary>
        private const int MaxSlotDepth = 12;

        /// <summary>A ceiling on how many distinct templates one weapon's graph may reach.
        ///
        /// The M4A1 reaches 469. Ten thousand is far past anything real and is here so that a
        /// pathological modded graph is bounded by a number rather than by memory.</summary>
        private const int MaxReachable = 10000;

        /// <summary>One slot on a part: what it is called, whether it must be filled, and what may
        /// go in it.</summary>
        public sealed class SlotInfo
        {
            public string Name { get; init; } = "";
            public bool Required { get; init; }

            /// <summary>Allowed templates, already materialised. Filters is an IEnumerable on the
            /// template and re-enumerating it inside a search is a cost paid thousands of times.</summary>
            public MongoId[] Candidates { get; init; } = Array.Empty<MongoId>();
        }

        /// <summary>One part, as the solver needs it.</summary>
        public sealed class PartInfo
        {
            public MongoId Template { get; init; }
            public SlotInfo[] Slots { get; init; } = Array.Empty<SlotInfo>();

            /// <summary>Items this part cannot coexist with. Already a HashSet on the template, so
            /// conflict checks are O(1) with no preprocessing of ours.</summary>
            public HashSet<MongoId> Conflicts { get; init; } = new();

            public double Ergonomics { get; init; }
            public double RecoilPercent { get; init; }
            public double Weight { get; init; }
            public int? MagazineCapacity { get; init; }
            public double? SightingRange { get; init; }
        }

        private readonly Dictionary<MongoId, PartInfo> _parts = new();
        private readonly object _buildLock = new();
        private bool _built;

        /// <summary>Everything reachable from a weapon through its slot tree, and the parts
        /// themselves. Null when the weapon is not in the table or its graph could not be walked.</summary>
        public IReadOnlyDictionary<MongoId, PartInfo>? Reachable(MongoId weapon, out IReadOnlyList<string> notes)
        {
            var warnings = new List<string>();
            notes = warnings;

            EnsureBuilt();

            if (!_parts.ContainsKey(weapon))
            {
                warnings.Add($"no template for weapon '{weapon}'");
                return null;
            }

            var reached = new Dictionary<MongoId, PartInfo>();
            var truncated = Walk(weapon, reached, 0, warnings);

            if (truncated)
                warnings.Add("the slot graph was truncated - a depth cap or the reachable ceiling was hit");

            return reached;
        }

        /// <summary>Descends one part's slots, adding everything it can reach.
        ///
        /// Returns whether a cap stopped it, so a caller can say the answer is partial rather than
        /// quietly presenting it as complete. The visited set is the dictionary being filled: a
        /// template already in it has already been descended, so a cycle closes instead of looping.</summary>
        private bool Walk(MongoId template, Dictionary<MongoId, PartInfo> reached, int depth, List<string> warnings)
        {
            if (depth > MaxSlotDepth) return true;
            if (reached.Count >= MaxReachable) return true;

            if (!_parts.TryGetValue(template, out var part)) return false;

            // Already descended. This is the cycle guard, and it is the whole of it: a graph that
            // points back at itself finds its own template here and stops.
            if (!reached.TryAdd(template, part)) return false;

            var truncated = false;

            foreach (var slot in part.Slots)
                foreach (var candidate in slot.Candidates)
                    truncated |= Walk(candidate, reached, depth + 1, warnings);

            return truncated;
        }

        /// <summary>Flattens the whole item table once. Cheap enough to do eagerly - it is a pass
        /// over a dictionary, not a graph walk - and it means a solve never waits on it.</summary>
        private void EnsureBuilt()
        {
            if (_built) return;

            lock (_buildLock)
            {
                if (_built) return;

                var items = templateTable.Items;

                if (items == null)
                {
                    logger.Warning("Quest Tracker: no item table - weapon builds cannot be generated.");
                    _built = true;
                    return;
                }

                var slotted = 0;

                foreach (var (id, template) in items)
                {
                    if (template?.Properties == null) continue;

                    var part = Flatten(id, template);
                    _parts[id] = part;

                    if (part.Slots.Length > 0) slotted++;
                }

                _built = true;

                logger.Info(
                    $"Quest Tracker: weapon graph over {_parts.Count:N0} templates, {slotted:N0} of which have slots.");
            }
        }

        private static PartInfo Flatten(MongoId id, TemplateItem template)
        {
            var props = template.Properties!;
            var slots = new List<SlotInfo>();

            foreach (var slot in props.Slots ?? Enumerable.Empty<Slot>())
            {
                if (slot == null) continue;

                // Filters is an IEnumerable, and only the first filter carries the allowed set -
                // SlotFilter has no ExcludedFilter, unlike GridFilter, so a slot's negative
                // constraints live entirely in the parts' own ConflictingItems.
                var allowed = slot.Properties?.Filters?.FirstOrDefault()?.Filter;

                slots.Add(new SlotInfo
                {
                    Name = slot.Name ?? "",
                    Required = slot.Required ?? false,
                    Candidates = allowed == null ? Array.Empty<MongoId>() : allowed.ToArray()
                });
            }

            var cartridge = props.Cartridges?.FirstOrDefault();
            var capacity = cartridge?.MaxCount;

            return new PartInfo
            {
                Template = id,
                Slots = slots.ToArray(),
                Conflicts = props.ConflictingItems ?? new HashSet<MongoId>(),
                Ergonomics = props.Ergonomics ?? 0d,
                RecoilPercent = props.Recoil ?? 0d,
                Weight = props.Weight ?? 0d,
                MagazineCapacity = capacity > 0 ? (int)capacity : null,
                SightingRange = props.SightingRange > 0 ? props.SightingRange : null
            };
        }
    }
}
