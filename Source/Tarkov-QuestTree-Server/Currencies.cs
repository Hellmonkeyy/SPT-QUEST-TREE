using System;
using System.Collections.Generic;

namespace QuestTreeServer
{
    /// <summary>
    /// The game's three currency template ids, in one place.
    ///
    /// ONE place because three places is what let one of them be wrong. The set was hand-copied into
    /// QuestPayloadBuilder, PartAvailability and the client's ItemWatchlistView, and two of those three
    /// carried "5696686a4bdc2d88308b456a" for dollars - not a typo of a real id, not a real id at all.
    /// Nothing failed: every check that asked "is this dollars" simply answered no.
    ///
    /// What that cost, measured on the shipped database. 63 quest rewards pay dollars - Car Repair,
    /// The Extortionist, The Punisher Parts 3 and 5 - and every one of them was emitted with
    /// IsCurrency false, so the ranking priced them through the handbook instead of at face value and
    /// the client drew them as a generic item drop rather than as cash. Worse on the other side: THREE
    /// objectives hand dollars OVER - Spa Tour - Part 6, Friend From the West - Part 2 and Overseas Trust -
    /// Part 2 - and with dollars unrecognised the item watchlist stopped excluding them, so the "do not
    /// sell that" list advised players to hold onto their money.
    ///
    /// Not four: Building Foundations and Key Partner name the currencies too, but as SellItemToTrader,
    /// which IsAnyItemCondition does not admit, so they never reach a watchlist row at all.
    ///
    /// The client half cannot reference this file: it is a separate assembly against a different
    /// framework. Its copy is hand-mirrored the way the DTOs are, and says so at both ends.
    /// </summary>
    internal static class Currencies
    {
        public const string Roubles = "5449016a4bdc2d6f028b456f";
        public const string Dollars = "5696686a4bdc2da3298b456a";
        public const string Euros = "569668774bdc2da2298b4568";

        /// <summary>All three, for a "is this money" test. OrdinalIgnoreCase because template ids arrive
        /// from the quest database and from item templates, and neither promises a casing.</summary>
        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Roubles, Dollars, Euros };
    }
}
