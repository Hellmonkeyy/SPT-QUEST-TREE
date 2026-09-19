using System;
using System.Collections.Generic;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// What a part costs, and what a trader trip is worth avoiding.
    ///
    /// WHY THE SEARCH NEEDS THIS. The MP-133 quest demands a combined tactical device. The search fitted a
    /// Zenit Klesch-2P at 15,676 roubles; the cheapest thing that satisfies the same category is an NcSTAR
    /// blue laser at 4,100, which is what every community guide recommends. Both satisfy it identically and
    /// price was invisible, so headroom broke the tie and picked the one costing 3.8 times more. That is
    /// about 11,600 roubles wasted on one part of one build, and the same blindness applied to all sixty.
    ///
    /// TWO PRICES, AND THE DIFFERENCE BETWEEN THEM IS THE POINT.
    ///
    ///   Of      - the handbook's valuation. Static, listed for 4,288 items, the game's own number. Used
    ///             wherever something is being VALUED rather than bought: quest rewards, and the control
    ///             column of the bill measurement.
    ///   Shared  - what the shared weapon builds are optimised against, and the only thing the search sees.
    ///
    /// WHAT Shared IS, and why it is not the handbook any more. The cheapest CASH price any trader asks at
    /// any loyalty level, read once from the trader tables in the database; and when no trader sells the
    /// template for money at all, the handbook price times FleaOnlyMultiple, because that part has to come
    /// off the flea market and the flea does not charge handbook.
    ///
    /// BOTH HALVES CARRY INFORMATION, and an earlier version of this comment claimed the first one did not.
    /// It said trader prices were "a flat ~9% over handbook, which cannot reorder anything". The median is
    /// indeed 1.08, but a median is not a distribution: over the 519 trader-sold part instances in the
    /// shipped seed the ratio runs from 0.70x to 4.67x, mean 1.12, p90 1.25, with 6.9% of instances above
    /// 1.3x and 7.1% BELOW handbook - a trader undercutting the game's own valuation. Any of those can break
    /// a tie between two parts that satisfy the same constraint, which is the only thing price is ever asked
    /// to do here. The flea-only half is the bigger effect, not the only one: 8% of instances, 61% of the
    /// whole gap between what the handbook said and what the player paid, at three to four and a half times
    /// handbook. The multiple is what makes the search prefer a trader-sold part when one will do.
    ///
    /// STILL STATIC, WHICH IS WHAT KEEPS THE ANSWER SHIPPABLE. Read from TradersTable - the database's own
    /// assort, every offer at every loyalty level, not what one profile has unlocked - so it is the same
    /// number for every player on the same install and the solved history can ship. A trader's real price
    /// for a real profile belongs in what is DISPLAYED, and that is what PartAvailability is for.
    ///
    /// AND NEVER THE FLEA MARKET. templateTable.Prices and RagfairPriceService are rewritten hourly by
    /// LiveFleaPrices and differ between installs, so a build optimised against them would be
    /// irreproducible, would move under a cache that cannot see it move, and could not be shipped. The
    /// flea's influence enters as one constant multiple and nothing else.
    ///
    /// AN UNPRICED PART IS NOT A FREE PART. A modded part may be in no trader's list and in no handbook.
    /// Returning zero there would make the search prefer exactly the parts it knows least about, so an item
    /// with no price at all reports null, still counts as a purchase, and is reported as unpriced rather
    /// than quietly costed at nothing.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class PartPrices(ISptLogger<PartPrices> logger, TemplateTable templateTable, TradersTable tradersTable)
    {
        /// <summary>What one purchase is worth avoiding, in roubles, beyond what it costs.
        ///
        /// This is the whole of the "balance" between money and errands: two quantities in one currency, so
        /// they can be compared at all. At zero, a build made of thirty cheap parts beats one made of two
        /// dear ones; at fifty thousand, part count dominates and price barely matters. Ten thousand is the
        /// default and QUESTTREE_PER_PURCHASE moves it - and because it changes which build is best, it is
        /// part of what makes a remembered answer valid, so the history records the value it was solved
        /// under.</summary>
        public long PerPurchase
        {
            get
            {
                if (_perPurchase >= 0) return _perPurchase;

                var asked = Environment.GetEnvironmentVariable("QUESTTREE_PER_PURCHASE");

                if (string.IsNullOrWhiteSpace(asked)) return _perPurchase = DefaultPerPurchase;

                if (!long.TryParse(asked, out var wanted) || wanted < 0)
                {
                    logger.Warning(
                        $"Quest Tracker: QUESTTREE_PER_PURCHASE is '{asked}', which is not a rouble amount - " +
                        $"using the default {DefaultPerPurchase:N0}.");

                    return _perPurchase = DefaultPerPurchase;
                }

                return _perPurchase = wanted;
            }
        }

        public const long DefaultPerPurchase = 10_000;

        private long _perPurchase = -1;

        /// <summary>What a part no trader sells costs, as a multiple of its handbook price.
        ///
        /// Three, measured. Across the shipped builds the parts nothing sells for cash went for three to
        /// four and a half times what the handbook says, and they are where practically all of the
        /// handbook's error lives. The exact figure matters far less than its existence: any multiple above
        /// one makes the search break a tie towards the part a trader stocks, and that is the whole job.
        ///
        /// QUESTTREE_FLEA_MULTIPLE moves it, WITHIN ONE AND A HUNDRED. Both ends are refused rather than
        /// clamped. Below one would make an unbuyable part look cheaper than a buyable one, the exact
        /// inversion this exists to prevent. Above a hundred is not a price at all but a ban: the handbook's
        /// dearest part is over four million roubles, so even 100 puts a flea-only part past any plausible
        /// PerPurchase and turns "prefer a trader" into "never consider the flea", which is a different
        /// decision that should be asked for in different words. It is also where the arithmetic starts to
        /// matter - handbook x a large multiple x sixty parts is where a long would eventually stop holding.
        ///
        /// Like PerPurchase it changes which build is best, so a history solved under a different value is a
        /// history solved under a different question - which is why it is in the cache fingerprint.</summary>
        public long FleaOnlyMultiple
        {
            get
            {
                if (_fleaOnlyMultiple >= 0) return _fleaOnlyMultiple;

                var asked = Environment.GetEnvironmentVariable("QUESTTREE_FLEA_MULTIPLE");

                if (string.IsNullOrWhiteSpace(asked)) return _fleaOnlyMultiple = DefaultFleaOnlyMultiple;

                if (!long.TryParse(asked, out var wanted) || wanted < 1 || wanted > MaxFleaOnlyMultiple)
                {
                    logger.Warning(
                        $"Quest Tracker: QUESTTREE_FLEA_MULTIPLE is '{asked}', which is not a multiple between 1 " +
                        $"and {MaxFleaOnlyMultiple} - using the default {DefaultFleaOnlyMultiple}.");

                    return _fleaOnlyMultiple = DefaultFleaOnlyMultiple;
                }

                return _fleaOnlyMultiple = wanted;
            }
        }

        public const long DefaultFleaOnlyMultiple = 3;

        public const long MaxFleaOnlyMultiple = 100;

        private long _fleaOnlyMultiple = -1;

        private readonly object _lock = new();

        /// <summary>Volatile because it is read without the lock - Of() checks it first and the double-checked
        /// build below publishes it from whichever thread got there first.</summary>
        private volatile Dictionary<MongoId, long>? _prices;

        /// <summary>The handbook price of one template, or null when the handbook does not list it.</summary>
        public long? Of(MongoId template)
        {
            var prices = Prices();

            return prices.TryGetValue(template, out var price) ? price : null;
        }

        /// <summary>What the shared weapon builds are optimised against: the cheapest cash price any trader
        /// asks at any loyalty, else the handbook times FleaOnlyMultiple, else null.
        ///
        /// Null means no trader sells it and the handbook does not list it. That is genuinely unpriced and
        /// is carried as null so the search counts it as a purchase without costing it at nothing.</summary>
        public long? Shared(MongoId template)
        {
            if (TraderPrices().TryGetValue(template, out var trader)) return trader;

            return Of(template) is { } handbook ? handbook * FleaOnlyMultiple : null;
        }

        /// <summary>How many templates carry a price. Reported at boot, because "nothing is priced" and
        /// "the handbook could not be read" produce identical silence otherwise.</summary>
        public int Count => Prices().Count;

        /// <summary>How the item database splits under Shared, and the two numbers that say whether the
        /// currency conversion behind it works at all.
        ///
        /// FromTraders, FleaOnly and Unpriced partition templateTable.Items: every template falls in
        /// exactly one, so the three add up to the database and a broken trader read shows as the partition
        /// collapsing onto FleaOnly rather than as a plausible-looking price.
        ///
        /// NonRoubleOffers is the check that can actually fail. The shipped database settles 618 offers in
        /// dollars, 44 in euros and 156 in GP coins; if RoublesPer stops converting them they are skipped in
        /// silence, every Peacekeeper and every Ref part quietly becomes handbook times the multiple, and
        /// nothing else in the log moves. Zero here means exactly that.
        ///
        /// COUNTED PER OFFER, on the alternative that actually WON. An offer Prapor will take either 50,000
        /// roubles or 400 dollars for is a rouble price, because that is the cheaper of the two and the
        /// cheaper of the two is what gets used; counting both alternatives made the number bigger than the
        /// thing it was supposed to be counting and the name a lie.</summary>
        public readonly record struct Coverage(
            int Traders,
            int FromTraders,
            int FleaOnly,
            int Unpriced,
            int NonRoubleOffers,
            int UnknownCurrencyOffers);

        public Coverage SharedCoverage
        {
            get
            {
                // Forces the trader read, which is also what fills the offer counters beside it.
                var traders = TraderPrices();

                var fromTraders = 0;
                var fleaOnly = 0;
                var unpriced = 0;
                var items = templateTable.Items;

                if (items != null)
                    foreach (var id in items.Keys)
                    {
                        // CLASSIFIED ON Shared, not on whether the handbook merely HAS a row, because those
                        // are different questions for 168 templates: the handbook prices them at zero, and a
                        // zero is not a price - WeaponSolver.Priced counts `known > 0` as priced and
                        // everything else as an unpriced purchase. Asking the dictionary for membership
                        // instead put all 168 in the flea-only column and told the operator they were priced
                        // at handbook x3, which is zero.
                        if (traders.ContainsKey(id)) fromTraders++;
                        else if (Shared(id) is > 0) fleaOnly++;
                        else unpriced++;
                    }

                var read = _traderRead!;

                return new Coverage(read.Traders, fromTraders, fleaOnly, unpriced, read.NonRouble, read.UnknownCurrency);
            }
        }

        private Dictionary<MongoId, long> Prices()
        {
            if (_prices != null) return _prices;

            lock (_lock)
            {
                if (_prices != null) return _prices;

                var prices = new Dictionary<MongoId, long>();
                var items = templateTable.Handbook?.Items;

                if (items == null)
                {
                    logger.Warning(
                        "Quest Tracker: no handbook - every part reads as unpriced and builds are chosen on " +
                        "purchase count alone.");

                    return _prices = prices;
                }

                foreach (var item in items)
                {
                    if (item.Price is not { } price || price < 0d) continue;

                    // Rounded to whole roubles: the handbook carries doubles and a build's cost is money.
                    prices[item.Id] = (long)Math.Round(price);
                }

                return _prices = prices;
            }
        }

        private readonly object _traderLock = new();

        /// <summary>The trader read and the counters taken during it, published as ONE reference so a
        /// caller can never see the prices without the numbers that describe them. Three separate int
        /// fields beside the dictionary would be three writes another thread could read half of.</summary>
        private sealed class TraderRead
        {
            public Dictionary<MongoId, long> Prices { get; init; } = new();
            public int Traders { get; init; }
            public int NonRouble { get; init; }
            public int UnknownCurrency { get; init; }
        }

        /// <summary>Volatile because the double-checked read below publishes it without taking the lock.
        /// Without it the JIT is free to hoist the field read, and on a weak memory model a thread can see
        /// the reference before the TraderRead's own fields - which is the bug the single-object publish was
        /// supposed to have closed, still open one level up.</summary>
        private volatile TraderRead? _traderRead;

        /// <summary>Whether an assort row is an OFFER rather than something bolted to one.
        ///
        /// A scope fitted to a rifle on Prapor's list is not separately purchasable, and counting its price
        /// as that template's price is how a build gets costed at money nobody could spend. 420 of Prapor's
        /// 951 assort rows are offers.
        ///
        /// ParentId, which is the field SPT's own trader-assort filter tests - TraderAssortExtensions and
        /// RagfairOfferGenerator both read ParentId, while every generator that builds a root sets ParentId
        /// and SlotId together. Measured on the shipped database before settling on one: across all 5,790
        /// assort rows there is not a single row where the two fields disagree about being a root, so this is
        /// the same answer the shared read got from SlotId, from the field SPT would ask.
        ///
        /// ONE implementation because it was written twice, once per field, in the two places that ask -
        /// which is not a bug today and is exactly how one becomes one.</summary>
        public static bool IsRootOffer(Item? offer) =>
            offer != null && string.Equals(offer.ParentId, "hideout", StringComparison.Ordinal);

        /// <summary>The cheapest cash price each template is sold at by any trader, at any loyalty level.
        ///
        /// Built once, from the DATABASE'S assort rather than a generated one: TradersTable holds what the
        /// install ships, every offer at every level, with no profile in it. That is what makes the answer
        /// the same for every player and so shippable - and it is also why this cannot be compared against
        /// what PartAvailability reports, which is one profile's unlocked subset at one moment.
        ///
        /// ROOT OFFERS ONLY - see IsRootOffer.
        ///
        /// FENCE IS EXCLUDED, for the reason PartAvailability excludes him: his stock is random, rotates,
        /// and carries his mark-up, so it is not a price the next player will see. His database assort is an
        /// empty stub anyway - the real one is generated per boot by FenceService - so nothing is lost here,
        /// but a mod that fills it in must not be allowed to make the shared builds install-specific.</summary>
        private Dictionary<MongoId, long> TraderPrices()
        {
            if (_traderRead != null) return _traderRead.Prices;

            lock (_traderLock)
            {
                if (_traderRead != null) return _traderRead.Prices;

                var cheapest = new Dictionary<MongoId, long>();
                var tally = new Tally();
                var traders = 0;

                foreach (var (id, trader) in tradersTable)
                {
                    if (id == SPTarkov.Server.Core.Models.Enums.Traders.FENCE) continue;

                    var assort = trader?.Assort;
                    var items = assort?.Items;

                    if (items == null || items.Count == 0) continue;

                    traders++;

                    foreach (var offer in items)
                    {
                        if (!IsRootOffer(offer)) continue;

                        if (CashPrice(assort!, offer!.Id, tally) is not { } cash) continue;

                        if (!cheapest.TryGetValue(offer.Template, out var best) || cash < best)
                            cheapest[offer.Template] = cash;
                    }
                }

                _traderRead = new TraderRead
                {
                    Prices = cheapest,
                    Traders = traders,
                    NonRouble = tally.NonRouble,
                    UnknownCurrency = tally.UnknownCurrency
                };

                return _traderRead.Prices;
            }
        }

        /// <summary>Roubles per unit of a currency, from the handbook - which is how SPT's own
        /// HandbookHelper.InRUB converts a trader's dollar or euro price. Roubles are 1; a currency
        /// the handbook does not price (a modded one) is null, and a price in it is not a price.
        ///
        /// The handbook is what makes this work for the GP coin without a special case: it prices one at
        /// 7,500, so Ref's "12 GP" becomes 90,000 roubles by the same arithmetic that turns Peacekeeper's
        /// dollars into roubles.
        ///
        /// HERE, AND NOWHERE ELSE. Until 1.16.0 a Peacekeeper price of 335 dollars was carried as 335
        /// "roubles", on the panel's cost labels, in the Cash totals, and in the bill measurement whose
        /// widest disagreement - handbook 45,787 against paid 335 - is what gave it away. It now has one
        /// implementation, used by both the profile-specific read in PartAvailability and the shared read
        /// above, because two of them is how one of them stays wrong.</summary>
        public double? RoublesPer(MongoId currency)
        {
            if (currency.ToString().Equals(Currencies.Roubles, StringComparison.OrdinalIgnoreCase)) return 1d;

            var rate = Of(currency);
            return rate is > 0 ? rate : null;
        }

        /// <summary>The cheapest cash price among a trader's alternative schemes for one offer, or null when
        /// every alternative wants goods.
        ///
        /// The outer list is ALTERNATIVES - any one of them buys the item - and each inner list is that
        /// alternative's requirements. A single requirement naming a currency is a cash price; anything else
        /// is a barter, and has no rouble amount to report.
        ///
        /// CURRENCY MEANS Currencies.Charged, the four SPT's own Money.GetMoneyTpls() lists, and the GP coin
        /// is the one that was missing. Reading the three-currency display set here made Ref - all 156 of
        /// whose cash offers are priced in GP and none in roubles - look like a trader who sells nothing for
        /// money: every Ref-only part was a barter to PartAvailability whatever the player's loyalty, and
        /// unsold to the shared objective, which tripled it through the flea multiple. The Lega Medal is not
        /// in that set and Ref's other 11 offers want it, so those stay the barters they are.
        ///
        /// One implementation for both readers: the shared boot read here and PartAvailability's per-profile
        /// read, which delegates to this. The rule about what counts as a cash price is the sort of thing
        /// that drifts when it is written twice.</summary>
        public long? CashPrice(TraderAssort assort, MongoId offer) => CashPrice(assort, offer, null);

        /// <summary>Counters for the boot report, threaded through the scheme walk. Null when nobody is
        /// counting, which is every per-profile read.</summary>
        private sealed class Tally
        {
            public int NonRouble;
            public int UnknownCurrency;
        }

        private long? CashPrice(TraderAssort assort, MongoId offer, Tally? tally)
        {
            if (assort.BarterScheme == null || !assort.BarterScheme.TryGetValue(offer, out var alternatives))
                return null;

            long? cheapest = null;
            var cheapestIsRoubles = false;
            var sawUnknownCurrency = false;

            foreach (var scheme in alternatives ?? new List<List<BarterScheme>>())
            {
                if (scheme == null || scheme.Count != 1) continue;

                var requirement = scheme[0];

                if (requirement?.Template == null) continue;

                var currency = requirement.Template.ToString();

                if (!Currencies.Charged.Contains(currency)) continue;

                var rate = RoublesPer(requirement.Template);

                if (rate == null)
                {
                    sawUnknownCurrency = true;
                    continue;
                }

                var price = (long)Math.Round((requirement.Count ?? 0d) * rate.Value);

                if (price <= 0) continue;
                if (cheapest != null && price >= cheapest) continue;

                cheapest = price;

                // The CURRENCY, not the rate: a modded currency the handbook happens to price at one rouble
                // would read as roubles otherwise, and this counter's whole job is to notice a conversion
                // that stopped happening.
                cheapestIsRoubles = currency.Equals(Currencies.Roubles, StringComparison.OrdinalIgnoreCase);
            }

            // ONE BUMP PER OFFER, on the alternative that won, so the counter counts what its name says.
            // Both are only reported by the boot line, so both are only counted for the shared read.
            if (tally != null)
            {
                if (cheapest != null && !cheapestIsRoubles) tally.NonRouble++;
                if (sawUnknownCurrency) tally.UnknownCurrency++;
            }

            return cheapest;
        }
    }
}
