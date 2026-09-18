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

            /// <summary>What this part adds to the assembled gun's grid footprint, and for a weapon, the
            /// footprint it starts with.
            ///
            /// Here because the SEARCH has to know. A width limit that only the verifier understood produced
            /// a five-wide MP-133 against a limit of four: the solver reported it satisfied, the verifier
            /// caught it, and the quest ended up with no answer at all instead of a wrong one. A constraint
            /// the search cannot optimise against is not a constraint, it is a rejection notice.</summary>
            public int Width { get; init; }
            public int Height { get; init; }
            public int ExtraUp { get; init; }
            public int ExtraDown { get; init; }
            public int ExtraLeft { get; init; }
            public int ExtraRight { get; init; }
            public bool ExtraForced { get; init; }
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

        /// <summary>Walks the slot tree from one part, adding everything it can reach.
        ///
        /// BREADTH-first, and that is a correctness requirement rather than a preference. It was
        /// depth-first with this dictionary as the visited set, which is sound on its own and unsound
        /// together with a depth cap: a template first met at depth 12 was added with its own children cut
        /// off, and a later path arriving at depth 2 found it already present and did not descend it again.
        /// The reachable set therefore depended on the order the candidate lists happened to be in.
        ///
        /// What that cost: a subtree missing from the graph, and the solver reporting a part as "NOT
        /// REACHABLE from this weapon's slots" when it plainly is. Worse, a breadth-first walk of the same
        /// graph never has the flaw, so two walks of one weapon could disagree - which is how this was
        /// noticed, back when the verifier kept a walk of its own.
        ///
        /// Breadth-first removes the problem rather than working around it: the first time a template is
        /// dequeued is at its minimum depth, so descending it exactly once is correct and the visited set
        /// stays the whole cycle guard. Vanilla's deepest chain is 5 against a cap of 12, so nothing on a
        /// stock install ever hit it - this is for the modded weapon graphs that do.
        ///
        /// Returns whether a cap stopped it, so a caller can say the answer is partial rather than quietly
        /// presenting it as complete.</summary>
        private bool Walk(MongoId template, Dictionary<MongoId, PartInfo> reached, int depth, List<string> warnings)
        {
            var truncated = false;
            var queue = new Queue<(MongoId Template, int Depth)>();

            queue.Enqueue((template, depth));

            while (queue.Count > 0)
            {
                var (current, at) = queue.Dequeue();

                if (reached.Count >= MaxReachable)
                {
                    truncated = true;
                    break;
                }

                if (!_parts.TryGetValue(current, out var part)) continue;

                // The cycle guard, and the whole of it: a graph that points back at itself finds its own
                // template already here. Safe to treat as final now that the first arrival is the shallowest.
                if (!reached.TryAdd(current, part)) continue;

                if (at >= MaxSlotDepth)
                {
                    truncated = true;
                    continue;
                }

                foreach (var slot in part.Slots)
                    foreach (var candidate in slot.Candidates)
                        queue.Enqueue((candidate, at + 1));
            }

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

                    var part = Flatten(id, template, items);
                    _parts[id] = part;

                    if (part.Slots.Length > 0) slotted++;
                }

                _built = true;

                logger.Info(
                    $"Quest Tracker: weapon graph over {_parts.Count:N0} templates, {slotted:N0} of which have slots.");
            }
        }

        /// <summary>How many of a weapon's REQUIRED top-level slots a set of parts leaves empty.
        ///
        /// A quest's ContainsItems is a CONSTRAINT - "the build must contain these" - and not a
        /// build. On some weapons it happens to name nearly everything; on the PP-19-01 it names a
        /// sight, a grip, a stock and a suppressor and no magazine, dust cover or charging handle,
        /// so those eight parts do not add up to a working gun at all.
        ///
        /// That distinction is invisible in a list of part names, and it is the whole explanation
        /// for a model score that falls short of the quest's own threshold: the missing parts carry
        /// ergonomics. Counting the gaps turns "these numbers look wrong" into "these numbers are
        /// a floor".
        ///
        /// Top level only, and deliberately: a part fitted into another part's sub-slot is filling
        /// a slot that would not exist without its parent, so counting those would make the answer
        /// depend on assembly order rather than on the weapon.</summary>
        public int UnfilledRequiredSlots(MongoId weapon, IReadOnlyCollection<MongoId> fitted, out int requiredTotal)
        {
            requiredTotal = 0;
            EnsureBuilt();

            if (!_parts.TryGetValue(weapon, out var part)) return 0;

            var unfilled = 0;

            foreach (var slot in part.Slots)
            {
                if (!slot.Required) continue;

                requiredTotal++;

                var filled = false;

                foreach (var candidate in slot.Candidates)
                {
                    if (!fitted.Contains(candidate)) continue;

                    filled = true;
                    break;
                }

                if (!filled) unfilled++;
            }

            return unfilled;
        }

        /// <summary>Walks a set of weapons and reports what the graph looks like, once, at boot.
        ///
        /// The warm-up is the lesser half. The real job is that a cyclic slot graph is the one
        /// failure this codebase cannot recover from - a StackOverflowException is uncatchable, so
        /// Guarded never sees it and the process dies with every player's raid inside it. The guard
        /// against it is a visited set and a depth cap, and a guard nobody has ever seen fire is a
        /// guard nobody knows works.
        ///
        /// So this fires it deliberately, on every weapon the installed quests actually name, and
        /// says out loud whether anything was truncated. On a clean install that line reports
        /// nothing unusual; on an install whose mods have built a loop, it is the difference between
        /// knowing and finding out mid-raid.</summary>
        public void Survey(IEnumerable<MongoId> weapons)
        {
            EnsureBuilt();

            var surveyed = 0;
            var widest = 0;
            MongoId widestWeapon = default;
            var truncated = new List<string>();

            foreach (var weapon in weapons ?? Enumerable.Empty<MongoId>())
            {
                var reached = Reachable(weapon, out var notes);
                if (reached == null) continue;

                surveyed++;

                if (reached.Count > widest)
                {
                    widest = reached.Count;
                    widestWeapon = weapon;
                }

                foreach (var note in notes)
                    if (note.Contains("truncated", StringComparison.OrdinalIgnoreCase))
                        truncated.Add($"{weapon} ({reached.Count} parts)");
            }

            if (surveyed == 0)
            {
                logger.Info("Quest Tracker: no weapon-build quests to survey.");
                return;
            }

            logger.Info(
                $"Quest Tracker: walked the slot graph of {surveyed} quest weapon(s); the widest is " +
                $"'{widestWeapon}' at {widest:N0} reachable parts.");

            // Not a warning: truncation is the guard working. It is logged loudly because it also
            // means any build for that weapon is drawn from a partial set of parts, and that is a
            // caveat the solver has to carry rather than discover.
            if (truncated.Count > 0)
                logger.Warning(
                    $"Quest Tracker: {truncated.Count} weapon slot graph(s) hit the depth or size cap and were " +
                    $"truncated - {string.Join(", ", truncated.Take(5))}. Builds for these are drawn from a " +
                    "partial parts list. A cyclic graph from a mod is the usual cause, and the cap is what " +
                    "stops it taking the server down.");
        }

        private static PartInfo Flatten(MongoId id, TemplateItem template, IReadOnlyDictionary<MongoId, TemplateItem> items)
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

                    // SORTED, and that is load-bearing rather than tidiness. Filter is a HashSet and
                    // .NET randomises string hash codes per process, so the order it materialises in
                    // differs on every server start. The solver's search is greedy and breaks ties by
                    // iteration order, so without this the same quest gets a different build on each
                    // boot and a pass rate measured once means nothing. Ordinal on the id's own text,
                    // once per slot at boot.
                    Candidates = allowed == null
                        ? Array.Empty<MongoId>()
                        : allowed.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray()
                });
            }

            // Capacity only on a magazine-class part: the game reads GetCurrentMagazine()?.MaxCount
            // and nothing else, so a launcher's chamber or a weapon's own cartridge list must not
            // let the search believe a build has a magazine it does not.
            var cartridge = TemplateClasses.IsA(items, template, TemplateClasses.Magazine)
                ? props.Cartridges?.FirstOrDefault()
                : null;
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
                SightingRange = props.SightingRange > 0 ? props.SightingRange : null,
                Width = props.Width ?? 1,
                Height = props.Height ?? 1,
                ExtraUp = props.ExtraSizeUp ?? 0,
                ExtraDown = props.ExtraSizeDown ?? 0,
                ExtraLeft = props.ExtraSizeLeft ?? 0,
                ExtraRight = props.ExtraSizeRight ?? 0,
                ExtraForced = props.ExtraSizeForceAdd == true
            };
        }
    }
}
