using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Ragfair;

namespace QuestTreeServer
{
    /// <summary>
    /// What one profile can actually get, and what it would cost them.
    ///
    /// THE DEFECT THIS EXISTS TO MEASURE. The search picks from every part that exists, so it can recommend
    /// a part the player cannot obtain at any price, and nothing in the mod noticed. A build you cannot
    /// assemble is worse than an expensive one. So the first thing this does is count it: how many of the
    /// sixty builds name a part this profile cannot buy.
    ///
    /// SIX TIERS, and the tiers matter more than the numbers:
    ///
    ///   FITTED   - already on the weapon's default preset. Nothing to do.
    ///   OWNED    - already in the stash. The best thing a panel can say is "you have this".
    ///   BUYABLE  - on a trader's list for THIS profile right now, at the price being asked.
    ///   BARTER   - offered, but not for money. No rouble price exists and inventing one would be a lie.
    ///   FLEA     - sold by no trader this profile can use, but flea-listable, and the profile has flea
    ///              access. The price is the server's flea price, which is GENERATED and moves between
    ///              restarts - so it is carried as an estimate and ranked below a trader's fixed price.
    ///   ABSENT   - not obtainable. Not "expensive": absent.
    ///
    /// GetAssort(sessionId, trader, showLockedAssorts: false) is what makes this honest - the server has
    /// already stripped what loyalty and quest progress lock, so there is no filtering here to get wrong.
    /// Trader ids come from the profile rather than a fixed list, so a modded trader counts as much as
    /// Prapor does - with ONE exclusion, Fence, for the reason given at the loop that skips him.
    ///
    /// The flea tier is switched by the profile's level against the game's own RagFair.MinUserLevel, read
    /// from the globals rather than assumed to be fifteen, and by the template's CanSellOnRagfair flag. A
    /// profile below the level has no flea tier at all, which is the honest answer for the player doing
    /// Gunsmith early - the one this is most for.
    ///
    /// MULTI-PROFILE, because this is used with FIKA: a host serves a group, and this install already has
    /// two profiles. Everything here is per session and allocated per call - the only shared state is the
    /// read-only currency set and the flea read, which is replaced wholesale on a timer and never written
    /// into - so several sessions asking at once cannot interfere with each other.
    ///
    /// THAT CHANGED, and the difference is the one to know before writing to a Sources: For memoises the
    /// whole object for a few seconds, so one instance is now shared between request threads and the
    /// background worker. It is treated as immutable once returned, and every consumer today reads it or
    /// copies out of it - see the note on Recent, and on Sources itself.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class PartAvailability(
        ISptLogger<PartAvailability> logger,
        ProfileHelper profileHelper,
        TraderAssortHelper assortHelper,
        TemplateTable templateTable,
        GlobalTable globals,
        RagfairPriceService fleaPrices,
        SPTarkov.Server.Core.Helpers.Ragfair.RagfairServerHelper ragfairRules,
        SPTarkov.Server.Core.Helpers.Items.ItemHelper itemHelper,
        PartPrices partPrices)
    {
        /// <summary>The equipment slots a weapon in use sits in. A part under one of these is on a gun
        /// the player is carrying into raids.</summary>
        private static readonly HashSet<string> EquippedWeaponSlots = new(StringComparer.Ordinal)
        {
            "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster"
        };
        public enum Tier
        {
            /// <summary>On the weapon's default preset. Nothing to obtain.</summary>
            Fitted,

            /// <summary>Already fitted to a copy of the QUEST'S weapon this profile owns. Not "owned
            /// elsewhere" - already done.</summary>
            InPlace,

            /// <summary>Loose in the stash. The only ownership that is genuinely free.</summary>
            Owned,

            Buyable,
            Barter,
            Flea,
            Absent
        }

        /// <summary>Where a profile's copies of one template are. THREE STATES, because "you own this" is
        /// wrong advice when the copy is bolted to the gun they take into raids: loose in the stash is free;
        /// fitted to a stored weapon costs stripping it; fitted to an equipped weapon costs disarming them.
        /// Only the first is free to the optimiser. The other two are priced as purchases and REPORTED, by
        /// weapon, so the player can decide in one glance whether to strip or buy.</summary>
        public sealed class Holding
        {
            public int Loose;

            /// <summary>Weapon templates this part is fitted to, in the stash and on the character.</summary>
            public List<MongoId> FittedToStored { get; } = new();

            public List<MongoId> FittedToEquipped { get; } = new();

            public bool FittedAnywhere => FittedToStored.Count > 0 || FittedToEquipped.Count > 0;
        }

        /// <summary>One trader's lowest loyalty level for a part, counting offers the profile has not
        /// unlocked. A diagnostic, never a source.</summary>
        public readonly record struct Gate(int Level, MongoId Trader, long? Price);

        /// <summary>Everything one profile can get, as of one moment.
        ///
        /// IMMUTABLE ONCE RETURNED. Nothing may write to an instance, or to the collections it holds, after
        /// For has handed it out.
        ///
        /// Not enforceable in the type - every member is settable or a mutable collection, and Fresh() is
        /// code that sets exactly those, on its own new instance. The rule exists because For memoises each
        /// answer for a few seconds, so one instance is shared between request threads and the background
        /// worker: a write would land in another request's copy with nothing wrong at the write site to see.
        ///
        /// A consumer that needs to change something copies out. ProfileBuilds builds its allowed-set into
        /// fresh collections for precisely this reason.</summary>
        public sealed class Sources
        {
            /// <summary>Templates with at least one LOOSE copy - the free ones.</summary>
            public HashSet<MongoId> Owned { get; } = new();

            /// <summary>Every template the profile holds anywhere, with where. Loose, fitted to a stored
            /// weapon, or fitted to an equipped one.</summary>
            public Dictionary<MongoId, Holding> Holdings { get; } = new();

            /// <summary>Weapon template to the part templates fitted on copies of it that this profile owns.
            /// What makes "already on your MDR" sayable for the quest's own weapon.</summary>
            public Dictionary<MongoId, HashSet<MongoId>> OwnedWeapons { get; } = new();

            /// <summary>Template to the cheapest cash price any trader is asking of this profile.</summary>
            public Dictionary<MongoId, long> Buyable { get; } = new();

            /// <summary>Offered, but only for goods. Obtainable, with no price to put on it.</summary>
            public HashSet<MongoId> Barter { get; } = new();

            /// <summary>Template to the LOWEST loyalty level at which some trader offers it, counting offers
            /// this profile has not unlocked, and which trader. Not a source - a diagnostic: it is what says
            /// whether a part is missing because the game gates it behind trader progress or because it is
            /// not sold at all.</summary>
            public Dictionary<MongoId, Gate> Gated { get; } = new();

            /// <summary>Whether this profile may use the flea market at all, and the level that decides
            /// it.</summary>
            public bool FleaAccess { get; set; }

            public int FleaLevel { get; set; }

            /// <summary>Flea-listable templates and the server's price for each. Shared and read-only;
            /// only consulted when FleaAccess is true.</summary>
            public IReadOnlyDictionary<MongoId, long> Flea { get; set; } = new Dictionary<MongoId, long>();

            /// <summary>Templates the game refuses on the flea - CanSellOnRagfair false, or on the server's
            /// ragfair blacklist. Diagnostic: "flea-banned" is a different fact from "not sold".</summary>
            public IReadOnlySet<MongoId> FleaBanned { get; set; } = new HashSet<MongoId>();

            /// <summary>Whether a template ships with the game rather than arriving from a mod. Read from
            /// the vanilla items file's keys, which is the one thing that file IS the authority on.</summary>
            public Func<MongoId, bool> IsVanilla { get; set; } = _ => true;

            public int Traders { get; set; }

            /// <summary>Loyalty levels and offer counts per trader, plus flea access, hashed. When this
            /// changes the profile can buy things it could not before, which makes a cheapest-build answer
            /// stale - a QUALITY invalidation, not a correctness one: the old build is still legal, just no
            /// longer cheapest.</summary>
            public string Fingerprint { get; set; } = "";

            /// <summary>Obtainable by any means this profile has, the flea market included when it has
            /// access.</summary>
            public bool Has(MongoId template) =>
                Owned.Contains(template) || Buyable.ContainsKey(template) || Barter.Contains(template)
                || (FleaAccess && Flea.ContainsKey(template));

            /// <summary>Obtainable without the flea market. Kept separately because the flea is a generated
            /// source whose prices move, and the ledger's earlier figures were taken without it.</summary>
            public bool HasFromTraders(MongoId template) =>
                Owned.Contains(template) || Buyable.ContainsKey(template) || Barter.Contains(template);

            /// <summary>The tier a part falls in for this profile and what it costs there. A price is only
            /// returned for Buyable and Flea; Fitted and Owned cost nothing, Barter and Absent have no rouble
            /// figure and are never given one.</summary>
            public (Tier Tier, long? Price) Classify(MongoId template, WeaponPresets.Defaults? defaults) =>
                Classify(template, defaults, null);

            /// <summary>As above, and with the quest's weapon named: a part already fitted to a copy of that
            /// weapon the profile owns is in place rather than owned.</summary>
            public (Tier Tier, long? Price) Classify(MongoId template, WeaponPresets.Defaults? defaults, MongoId? questWeapon)
            {
                // Slot-blind, and OccupiesAnySlot's own comment says what that costs.
                if (defaults != null && defaults.OccupiesAnySlot(template)) return (Tier.Fitted, 0);
                if (questWeapon is { } owned && OwnedWeapons.TryGetValue(owned, out var onIt) && onIt.Contains(template))
                    return (Tier.InPlace, 0);
                if (Owned.Contains(template)) return (Tier.Owned, 0);
                if (Buyable.TryGetValue(template, out var cash)) return (Tier.Buyable, cash);
                if (Barter.Contains(template)) return (Tier.Barter, null);
                if (FleaAccess && Flea.TryGetValue(template, out var flea)) return (Tier.Flea, flea);

                return (Tier.Absent, null);
            }
        }

        /// <summary>Everything this profile can get. Null when there is no profile to read, which is what a
        /// request from outside a game session looks like.</summary>
        public Sources? For(MongoId sessionId)
        {
            var profile = TryGetProfile(sessionId);

            return profile == null ? null : For(sessionId, profile);
        }

        /// <summary>The last answer per profile, kept for a few seconds.
        ///
        /// Every call generates each of the profile's traders' assorts TWICE - once as the profile sees
        /// them and once including what is locked, for the Gated diagnostic - and walks the whole
        /// inventory. Measured rather than guessed: the shipped database ships twelve traders that all have
        /// an assort, so a stock install is about twenty-four generations, and both profiles on this machine
        /// carry fourteen TradersInfo entries, so roughly twenty-eight here. ProfileBuilds asks for it on
        /// EVERY /questtree/builds GET, and GetBuilds re-asks on every render while the answer is not ready.
        /// WeaponGraph's own comment sets the rule this was breaking: a request handler must not pay for
        /// work like this.
        ///
        /// Safe to share rather than merely cheap: nothing mutates a Sources after For returns it - its
        /// consumers read it to build an allowed-set and to classify rows - and the correctness of a
        /// stale read is already handled a level up, where Fingerprint decides whether the answer needs
        /// recomputing. A few seconds behind is the same trade ProfileInventory makes for the same reason.
        ///
        /// Deliberately short, and deliberately not invalidated on purchase: trader stock moving is what
        /// Fingerprint is for, and the panel asks again.</summary>
        private static readonly ConcurrentDictionary<string, (DateTime At, Sources Sources)> Recent = new();

        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

        public Sources? For(MongoId sessionId, PmcData profile)
        {
            var key = sessionId.ToString();

            if (!string.IsNullOrEmpty(key) && Recent.TryGetValue(key, out var recent))
            {
                if (DateTime.UtcNow - recent.At < CacheFor) return recent.Sources;

                // Dropped rather than left to be overwritten. Overwriting keeps the memory of whichever
                // profile asked; removing releases it, which is what matters for a profile that asked once
                // and never came back. Each entry pins that profile's whole Owned/Buyable/Barter/Gated
                // picture, so on a long-lived server rotating sessions - Fika, or a developer switching
                // profiles all day - the map only ever grew.
                Recent.TryRemove(key, out _);
            }

            var sources = Build(sessionId, profile);

            if (!string.IsNullOrEmpty(key)) Recent[key] = (DateTime.UtcNow, sources);

            return sources;
        }

        private Sources Build(MongoId sessionId, PmcData profile)
        {
            var sources = new Sources();

            Hold(profile, sources);

            var stamp = new System.Text.StringBuilder();

            foreach (var (trader, info) in (profile.TradersInfo ?? new Dictionary<MongoId, TraderInfo>())
                         .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            {
                // FENCE IS NOT A SOURCE. His stock is randomly generated, rotates on a timer, and is priced
                // at his own mark-up, so "Fence has one" is not a fact the player can act on ten minutes
                // later and not a price anybody else will be quoted. Counting him made a part look buyable
                // that will not be there, which is the one thing this class exists to stop.
                //
                // It also costs two FenceService generations per profile per read - GetAssort is called
                // twice, once for the Gated diagnostic - which is the most expensive trader here by far and
                // was being paid for an answer that should not have been used.
                if (trader == SPTarkov.Server.Core.Models.Enums.Traders.FENCE) continue;

                try
                {
                    Read(sessionId, trader, sources, stamp, info);
                }
                catch (Exception ex)
                {
                    // One trader that cannot be read is not a reason to call every part unobtainable.
                    logger.Warning(
                        $"Quest Tracker: could not read trader '{trader}' ({ex.Message}) - its stock is not counted.");
                }
            }

            // The flea market, gated the way the game gates it. A missing globals entry means no flea
            // tier at all rather than a guessed level: a source that might not exist must not be counted.
            var minimum = globals.Configuration?.RagFair?.MinUserLevel;
            var level = profile.Info?.Level ?? 0;

            var flea = FleaPrices();

            sources.FleaLevel = minimum ?? int.MaxValue;
            sources.FleaAccess = minimum is { } needed && level >= needed;
            sources.Flea = flea.Prices;
            sources.FleaBanned = flea.Banned;
            // Empty means the file could not be READ, not that no vanilla items exist - so the empty
            // case answers true rather than asking the set, which would answer false for every template.
            // Asking it directly made every part read as mod-injected on an install where nothing was: the
            // exact inversion of the direction Vanilla()'s own catch says it takes.
            var vanilla = Vanilla();
            sources.IsVanilla = vanilla.Count == 0 ? _ => true : template => vanilla.Contains(template);

            stamp.Append("flea:").Append(sources.FleaAccess ? 1 : 0).Append('|');

            sources.Fingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(stamp.ToString())));

            return sources;
        }

        /// <summary>The same traders as a FRESH profile would see them: every trader at loyalty one, an
        /// empty stash, no flea market. Derived from the locked-inclusive read rather than from any real
        /// profile's state, so it can be measured on an install whose profiles are all long past that
        /// point - which is what the player doing Gunsmith early looks like, and nobody here is one.
        ///
        /// A hypothetical, and labelled as one wherever it is reported. What it cannot know is quest locks:
        /// an offer gated behind a quest a fresh profile has not done is counted by its loyalty level only,
        /// so this reads as slightly MORE obtainable than a real fresh profile, never less.</summary>
        public Sources Fresh(Sources real)
        {
            var fresh = new Sources
            {
                Traders = real.Traders,
                FleaAccess = false,
                FleaLevel = real.FleaLevel,
                Flea = real.Flea,
                FleaBanned = real.FleaBanned,
                IsVanilla = real.IsVanilla,
                Fingerprint = "fresh:" + real.Fingerprint
            };

            foreach (var (template, gate) in real.Gated)
            {
                fresh.Gated[template] = gate;

                if (gate.Level != 1) continue;

                if (gate.Price is { } cash) fresh.Buyable[template] = cash;
                else fresh.Barter.Add(template);
            }

            return fresh;
        }

        /// <summary>Walks the inventory from its two roots and says where every copy of every template
        /// is. Descent from the roots rather than a climb from each item, for the reason ProfileInventory
        /// gives: a parent chain that loops is unreachable from a root and so cannot be entered. The same
        /// CreateParentIdLookupCache, for the same reason: GenerateItemsMap throws on a duplicated id and
        /// the throw would cost the whole answer.
        ///
        /// A part is FITTED when its slot is a mod slot and a weapon sits above it; loose otherwise. The
        /// weapon is equipped when its own slot is one of the primary or holster slots on the equipment
        /// root. Anything the walk cannot place - hideout stashes, the sorting table - counts as loose,
        /// which is the generous reading and the one a mistaken classification should fall to.</summary>
        private void Hold(PmcData profile, Sources sources)
        {
            var inventory = profile.Inventory;
            var items = inventory?.Items;

            if (inventory == null || items == null) return;

            var children = items.CreateParentIdLookupCache(out _);
            var placed = new HashSet<MongoId>();

            void Descend(Item item, MongoId? weapon, bool equipped, int depth)
            {
                if (depth > 40 || !placed.Add(item.Id)) return;

                var template = item.Template;
                var slot = item.SlotId ?? "";

                var isWeapon = IsWeapon(template);
                var onWeapon = weapon;
                var fitted = weapon != null && slot.StartsWith("mod_", StringComparison.Ordinal);

                if (isWeapon)
                {
                    // A weapon is a holding too, and it starts a new chain: the parts under it are its.
                    onWeapon = template;
                    equipped = EquippedWeaponSlots.Contains(slot);

                    if (!sources.OwnedWeapons.TryGetValue(template, out var onIt))
                        sources.OwnedWeapons[template] = onIt = new HashSet<MongoId>();
                }

                if (!sources.Holdings.TryGetValue(template, out var holding))
                    sources.Holdings[template] = holding = new Holding();

                if (fitted && weapon is { } host)
                {
                    (equipped ? holding.FittedToEquipped : holding.FittedToStored).Add(host);
                    sources.OwnedWeapons[host].Add(template);
                }
                else
                {
                    holding.Loose++;
                    sources.Owned.Add(template);
                }

                if (!children.TryGetValue(item.Id, out var below) || below == null) return;

                foreach (var child in below)
                    if (child != null) Descend(child, onWeapon, equipped, depth + 1);
            }

            foreach (var item in items)
            {
                if (item == null) continue;
                if (item.Id != inventory.Equipment && item.Id != inventory.Stash) continue;

                if (!children.TryGetValue(item.Id, out var below) || below == null) continue;

                foreach (var child in below)
                    if (child != null) Descend(child, null, false, 0);
            }

            // Whatever the roots did not reach - hideout stashes, the sorting table, a profile with no
            // named roots - counted as loose: the walk claims nothing about where it is.
            foreach (var item in items)
            {
                if (item == null || placed.Contains(item.Id)) continue;
                if (item.Id == inventory.Equipment || item.Id == inventory.Stash) continue;

                if (!sources.Holdings.TryGetValue(item.Template, out var holding))
                    sources.Holdings[item.Template] = holding = new Holding();

                holding.Loose++;
                sources.Owned.Add(item.Template);
            }
        }

        /// <summary>The "Weapon" base class every gun descends from - checked against the vanilla items
        /// file (_name "Weapon", parent "Item") rather than taken from an enum SPT does not expose.</summary>
        private static readonly MongoId WeaponBaseClass = new("5422acb9af1c889c16000029");

        private readonly Dictionary<MongoId, bool> _weapons = new();
        private readonly object _weaponsLock = new();

        private bool IsWeapon(MongoId template)
        {
            lock (_weaponsLock)
            {
                if (_weapons.TryGetValue(template, out var known)) return known;

                bool weapon;

                try
                {
                    weapon = itemHelper.IsOfBaseclass(template, WeaponBaseClass);
                }
                catch (Exception)
                {
                    weapon = false;
                }

                return _weapons[template] = weapon;
            }
        }

        private void Read(MongoId sessionId, MongoId trader, Sources sources, System.Text.StringBuilder stamp, TraderInfo? info)
        {
            var assort = assortHelper.GetAssort(sessionId, trader, false);
            var items = assort?.Items;

            if (items == null) return;

            sources.Traders++;

            stamp.Append(trader).Append(':').Append(info?.LoyaltyLevel ?? 0).Append(':')
                .Append(items.Count).Append('|');

            foreach (var offer in items)
            {
                // Only the OFFERS, not what hangs off them: a scope fitted to a rifle on Prapor's list is not
                // separately purchasable, and counting it as available is exactly how a build gets called
                // buildable when it is not.
                if (offer == null || offer.ParentId != "hideout") continue;

                var price = CashPrice(assort!, offer.Id);

                if (price is { } cash)
                {
                    if (!sources.Buyable.TryGetValue(offer.Template, out var cheapest) || cash < cheapest)
                        sources.Buyable[offer.Template] = cash;
                }
                else
                {
                    sources.Barter.Add(offer.Template);
                }
            }

            // The same trader again, INCLUDING what loyalty and quests currently lock. Only to answer "would
            // this be available to a less progressed player, and at what level" - never as a source.
            var everything = assortHelper.GetAssort(sessionId, trader, true);

            if (everything?.Items == null) return;

            foreach (var offer in everything.Items)
            {
                if (offer == null || offer.ParentId != "hideout") continue;

                var level = everything.LoyalLevelItems != null
                            && everything.LoyalLevelItems.TryGetValue(offer.Id, out var required)
                    ? required
                    : 1;

                var cash = CashPrice(everything, offer.Id);

                // Lowest level first; at the same level, a cash offer over a barter, and the cheaper cash.
                if (!sources.Gated.TryGetValue(offer.Template, out var lowest)
                    || level < lowest.Level
                    || (level == lowest.Level && cash != null && (lowest.Price == null || cash < lowest.Price)))
                    sources.Gated[offer.Template] = new Gate(level, trader, cash);
            }
        }

        /// <summary>The cheapest cash price among a trader's alternative schemes for one offer, or null when
        /// every alternative wants goods.
        ///
        /// DELEGATED, not implemented here. What counts as a cash price - a single requirement naming one of
        /// the game's currencies, converted at the handbook's rate - and what a dollar is worth in roubles
        /// both live in PartPrices now, because the shared boot read needs the identical rule and two
        /// implementations of it is how the 1.16.0 currency bug survived as long as it did: a Peacekeeper
        /// price of 335 dollars was carried as 335 "roubles" on every cost label the panel drew.</summary>
        private long? CashPrice(TraderAssort assort, MongoId offer) => partPrices.CashPrice(assort, offer);

        private readonly object _fleaLock = new();
        private HashSet<MongoId>? _vanilla;

        /// <summary>One read of the flea: the prices and the bans together, with when they were taken.
        ///
        /// ONE reference for both, rather than two fields, because they are two halves of one answer and a
        /// caller that got the new prices with the old bans would be reporting a part as flea-banned and
        /// flea-priced at the same time.</summary>
        private sealed class FleaRead
        {
            public Dictionary<MongoId, long> Prices { get; init; } = new();
            public HashSet<MongoId> Banned { get; init; } = new();
            public DateTime At { get; init; }
        }

        private FleaRead? _fleaRead;

        /// <summary>How long a flea read is trusted.
        ///
        /// NOT FOREVER, which is what it used to be. LiveFleaPrices rewrites the server's price table about
        /// once an hour, and this was memoised for the life of the process - so a server left running
        /// overnight reported yesterday's flea prices on every panel and in every "cost of restriction"
        /// figure, with nothing anywhere saying the number was stale.
        ///
        /// A minute rather than the five seconds For uses: the walk is 6,500 templates with two service
        /// calls each, which is far too much to pay every five seconds, and a minute is already sixty times
        /// finer than the hourly rewrite it exists to follow. Nothing here is a correctness question - a
        /// flea price is an estimate and is labelled one - so the only thing being bought is that the
        /// estimate moves when the thing it estimates does.</summary>
        private static readonly TimeSpan FleaPricesFor = TimeSpan.FromMinutes(1);

        /// <summary>Every template the game lets a player list on the flea, with the server's price for it.
        /// A property of the item data and the price table, not of any profile - so it is shared across
        /// profiles and rebuilt on a timer rather than per call.
        ///
        /// Listable is the SERVER'S rule - RagfairServerHelper.IsItemValidRagfairItem, which applies the
        /// ragfair blacklist as well as CanSellOnRagfair - rather than the flag alone, so a part the config
        /// bans is banned here too. A template with no price is left out: GetFleaPriceForItem hands back 1
        /// rouble when it has no price at all, and 1 rouble is not a price, it is the absence of one dressed
        /// as a number. An unpriced part must never look cheap.</summary>
        private FleaRead FleaPrices()
        {
            var known = _fleaRead;

            if (known != null && DateTime.UtcNow - known.At < FleaPricesFor) return known;

            lock (_fleaLock)
            {
                known = _fleaRead;

                if (known != null && DateTime.UtcNow - known.At < FleaPricesFor) return known;

                var first = known == null;
                var prices = new Dictionary<MongoId, long>();
                var banned = new HashSet<MongoId>();
                var unpriced = 0;

                foreach (var pair in templateTable.Items ?? new Dictionary<MongoId, TemplateItem>())
                {
                    var (id, item) = pair;

                    if (item?.Properties == null) continue;

                    // Only things a player could fit to a gun are worth classifying; the rule is asked of
                    // everything anyway because it is cheap and the counts are reported.
                    bool valid;

                    try
                    {
                        // The helper takes the (found, item) pair ItemHelper.GetItem returns, not the table's own.
                        valid = item.Properties.CanSellOnRagfair == true
                                && ragfairRules.IsItemValidRagfairItem(new KeyValuePair<bool, TemplateItem?>(true, item));
                    }
                    catch (Exception)
                    {
                        valid = false;
                    }

                    if (!valid)
                    {
                        banned.Add(id);
                        continue;
                    }

                    double price;

                    try
                    {
                        price = fleaPrices.GetFleaPriceForItem(id);
                    }
                    catch (Exception)
                    {
                        unpriced++;
                        continue;
                    }

                    if (price <= 1d || double.IsNaN(price) || double.IsInfinity(price))
                    {
                        unpriced++;
                        continue;
                    }

                    prices[id] = (long)Math.Round(price);
                }

                var line =
                    $"Quest Tracker: {prices.Count:N0} template(s) are flea-listable with a price, {unpriced:N0} " +
                    $"listable but unpriced (not counted), {banned.Count:N0} refused by the game's flea rules.";

                // Info once, Debug on every refresh after that. The counts are worth seeing at boot and
                // would be a line a minute for the rest of the server's life otherwise.
                if (first) logger.Info(line);
                else logger.Debug(line + " (re-read; the server's flea prices move on their own clock)");

                return _fleaRead = new FleaRead { Prices = prices, Banned = banned, At = DateTime.UtcNow };
            }
        }

        /// <summary>The ids in the vanilla items file. NOT the item database - the live one is merged from
        /// mods and is a third larger - and the one question this file can answer is which ids the game
        /// itself ships, which is what "mod-injected" means.</summary>
        private HashSet<MongoId> Vanilla()
        {
            if (_vanilla != null) return _vanilla;

            lock (_fleaLock)
            {
                if (_vanilla != null) return _vanilla;

                var ids = new HashSet<MongoId>();
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "items.json");

                try
                {
                    using var stream = System.IO.File.OpenRead(path);
                    using var document = System.Text.Json.JsonDocument.Parse(stream);

                    foreach (var property in document.RootElement.EnumerateObject())
                        if (property.Name.TryParseMongoId(out var id)) ids.Add(id);

                    logger.Info($"Quest Tracker: {ids.Count:N0} template id(s) in the vanilla items file, for telling mod-injected parts apart.");
                }
                catch (Exception ex)
                {
                    // Left EMPTY, and the caller turns that into "every part reads as vanilla" - the
                    // direction that claims LESS, since it never calls a shipped part mod-injected. Doing
                    // it at the caller rather than by filling this set with something is deliberate: there
                    // is nothing honest to fill it with, and an empty set asked directly says the opposite
                    // of what this comment promises. See where IsVanilla is assigned.
                    logger.Warning($"Quest Tracker: could not read the vanilla items file ({ex.Message}) - mod-injected parts cannot be told apart.");
                }

                return _vanilla = ids;
            }
        }

        private PmcData? TryGetProfile(MongoId sessionId)
        {
            try
            {
                // GetPmcProfile THROWS on an empty session id rather than returning null, and an empty id is
                // exactly what a request made outside a game session carries.
                if (string.IsNullOrEmpty(sessionId.ToString())) return null;

                return profileHelper.GetPmcProfile(sessionId);
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: no profile for this session ({ex.Message}) - availability cannot be judged.");
                return null;
            }
        }
    }
}
