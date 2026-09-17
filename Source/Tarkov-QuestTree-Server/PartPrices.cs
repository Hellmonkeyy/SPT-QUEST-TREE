using System;
using System.Collections.Generic;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
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
    /// THE HANDBOOK AND NOT TRADER ASSORTS, deliberately. Handbook prices are static and the same for
    /// everybody, so the answer stays cacheable, shippable and identical for every player. A trader's real
    /// price depends on loyalty level and quest progress, which would make every build profile-specific,
    /// destroy the shipped history and move the whole thing onto a per-request route. If real prices are
    /// wanted they belong in what is DISPLAYED, not in what is optimised.
    ///
    /// AN UNPRICED PART IS NOT A FREE PART. The handbook covers 4,288 items and a modded part may be in
    /// none of them. Returning zero there would make the search prefer exactly the parts it knows least
    /// about, so an item with no entry reports null, still counts as a purchase, and is reported as
    /// unpriced rather than quietly costed at nothing.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class PartPrices(ISptLogger<PartPrices> logger, TemplateTable templateTable)
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

        private readonly object _lock = new();
        private Dictionary<MongoId, long>? _prices;

        /// <summary>The handbook price of one template, or null when the handbook does not list it.</summary>
        public long? Of(MongoId template)
        {
            var prices = Prices();

            return prices.TryGetValue(template, out var price) ? price : null;
        }

        /// <summary>How many templates carry a price. Reported at boot, because "nothing is priced" and
        /// "the handbook could not be read" produce identical silence otherwise.</summary>
        public int Count => Prices().Count;

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
    }
}
