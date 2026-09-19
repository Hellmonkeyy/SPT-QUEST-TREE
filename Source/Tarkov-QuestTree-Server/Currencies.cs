using System;
using System.Collections.Generic;

namespace QuestTreeServer
{
    /// <summary>
    /// The game's currency template ids, in one place - and the two different questions "is this money"
    /// turns out to be.
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
    ///
    /// TWO SETS, AND THEY ARE NOT THE SAME SET. SPT's own Money.GetMoneyTpls() answers four - roubles,
    /// dollars, euros and the GP coin - and that is the right answer to "may a trader demand this instead
    /// of goods". It is the WRONG answer to "should this reward be drawn as a pile of cash and valued at
    /// its face", which is what All is for, because a GP coin's face is 1 and its worth is 7,500.
    ///
    /// Measured, and this is why the two are separate rather than one set of four: seventeen quests pay GP
    /// coins as Item rewards - Chumming 2, A Shooter Born in Heaven 3, To Great Heights - Part 1 seventy
    /// five - and RewardWorth prices a currency at its face value. Folding GP into All would have valued
    /// that last reward at 75 roubles instead of 562,500, a factor of seven and a half thousand, on the
    /// panel and in the ranking; and it would have dropped GP coins out of the client's "do not sell this"
    /// watchlist, which two quests need there because they hand GP coins over. Nothing would have failed.
    /// </summary>
    internal static class Currencies
    {
        public const string Roubles = "5449016a4bdc2d6f028b456f";
        public const string Dollars = "5696686a4bdc2da3298b456a";
        public const string Euros = "569668774bdc2da2298b4568";

        /// <summary>The GP coin - money to a trader, an item to a quest reward. Ref charges in it and in
        /// nothing else. NOT in All, for the reason the class doc measures.</summary>
        public const string GpCoin = "5d235b4d86f7742e017bc88a";

        /// <summary>Money for the purposes of DISPLAY and VALUATION: drawn as cash, worth its face, and
        /// excluded from the item watchlist. The three real currencies only.
        ///
        /// This is the set the client's ItemWatchlistView mirrors, so it is also the set that may not change
        /// without changing the client half in the same release. OrdinalIgnoreCase because template ids
        /// arrive from the quest database and from item templates, and neither promises a casing.</summary>
        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Roubles, Dollars, Euros };

        /// <summary>Money for the purposes of BUYING: what a trader may put in a barter scheme as a price
        /// rather than as goods. SPT's Money.GetMoneyTpls(), which is the four.
        ///
        /// The GP coin belongs here and the measurement is stark: all 156 of Ref's cash offers are priced
        /// in GP and none in roubles, so a set of three read Ref as selling nothing for money at all. Every
        /// Ref-only part was a barter to PartAvailability - so never Buyable, whatever the player's loyalty
        /// - and unsold to the shared objective, which fell back to handbook times the flea multiple and
        /// tripled the price of parts Ref sells at about the handbook rate.
        ///
        /// The Lega Medal (6656560053eaaa7a23349c86) is deliberately NOT here. It is not in
        /// Money.GetMoneyTpls(), the game does not treat it as currency, and Ref's other 11 offers want it
        /// - so those stay barters, which is what they are.</summary>
        public static readonly IReadOnlySet<string> Charged =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Roubles, Dollars, Euros, GpCoin };
    }
}
