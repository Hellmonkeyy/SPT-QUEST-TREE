using System;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Traders;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

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
    /// FOUR TIERS, and the tiers matter more than the numbers:
    ///
    ///   OWNED    - already in the stash. The best thing a panel can say is "you have this".
    ///   BUYABLE  - on a trader's list for THIS profile right now, at the price being asked.
    ///   BARTER   - offered, but not for money. No rouble price exists and inventing one would be a lie.
    ///   ABSENT   - not obtainable. Not "expensive": absent.
    ///
    /// GetAssort(sessionId, trader, showLockedAssorts: false) is what makes this honest - the server has
    /// already stripped what loyalty and quest progress lock, so there is no filtering here to get wrong.
    /// Trader ids come from the profile rather than a fixed list, so a modded trader counts as much as
    /// Prapor does.
    ///
    /// Flea is deliberately NOT a source. Its offers are generated and its prices move every restart, so
    /// counting it would make "obtainable" mean something different tomorrow.
    ///
    /// MULTI-PROFILE, because this is used with FIKA: a host serves a group, and this install already has
    /// two profiles. Everything here is per session and allocated per call - the only shared state is the
    /// read-only currency set - so several sessions asking at once cannot interfere with each other.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class PartAvailability(
        ISptLogger<PartAvailability> logger,
        ProfileHelper profileHelper,
        TraderAssortHelper assortHelper)
    {
        /// <summary>Currencies a cash price can be quoted in. A requirement naming anything else is a
        /// barter.</summary>
        private static readonly HashSet<string> Currencies = new(StringComparer.Ordinal)
        {
            "5449016a4bdc2d6f028b456f", // roubles
            "5696686a4bdc2da3298b456a", // dollars
            "569668774bdc2da2298b4568"  // euros
        };

        public sealed class Sources
        {
            public HashSet<MongoId> Owned { get; } = new();

            /// <summary>Template to the cheapest cash price any trader is asking of this profile.</summary>
            public Dictionary<MongoId, long> Buyable { get; } = new();

            /// <summary>Offered, but only for goods. Obtainable, with no price to put on it.</summary>
            public HashSet<MongoId> Barter { get; } = new();

            /// <summary>Template to the LOWEST loyalty level at which some trader offers it, counting offers
            /// this profile has not unlocked. Not a source - a diagnostic: it is what says whether a part is
            /// missing because the game gates it behind trader progress or because it is not sold at all.</summary>
            public Dictionary<MongoId, int> Gated { get; } = new();

            public int Traders { get; set; }

            /// <summary>Loyalty levels and offer counts per trader, hashed. When this changes the profile can
            /// buy things it could not before, which makes a cheapest-build answer stale - a QUALITY
            /// invalidation, not a correctness one: the old build is still legal, just no longer cheapest.</summary>
            public string Fingerprint { get; set; } = "";

            public bool Has(MongoId template) =>
                Owned.Contains(template) || Buyable.ContainsKey(template) || Barter.Contains(template);
        }

        /// <summary>Everything this profile can get. Null when there is no profile to read, which is what a
        /// request from outside a game session looks like.</summary>
        public Sources? For(MongoId sessionId)
        {
            var profile = TryGetProfile(sessionId);

            return profile == null ? null : For(sessionId, profile);
        }

        public Sources? For(MongoId sessionId, PmcData profile)
        {
            var sources = new Sources();

            foreach (var item in profile.Inventory?.Items ?? new List<Item>())
                if (item != null)
                    sources.Owned.Add(item.Template);

            var stamp = new System.Text.StringBuilder();

            foreach (var (trader, info) in (profile.TradersInfo ?? new Dictionary<MongoId, TraderInfo>())
                         .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
            {
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

            sources.Fingerprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(stamp.ToString())));

            return sources;
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

                if (!sources.Gated.TryGetValue(offer.Template, out var lowest) || level < lowest)
                    sources.Gated[offer.Template] = level;
            }
        }

        /// <summary>The cheapest cash price among a trader's alternative schemes for one offer, or null when
        /// every alternative wants goods.
        ///
        /// The outer list is ALTERNATIVES - any one of them buys the item - and each inner list is that
        /// alternative's requirements. A single requirement naming a currency is a cash price; anything else
        /// is a barter, and has no rouble amount to report.</summary>
        private static long? CashPrice(TraderAssort assort, MongoId offer)
        {
            if (assort.BarterScheme == null || !assort.BarterScheme.TryGetValue(offer, out var alternatives))
                return null;

            long? cheapest = null;

            foreach (var scheme in alternatives ?? new List<List<BarterScheme>>())
            {
                if (scheme == null || scheme.Count != 1) continue;

                var requirement = scheme[0];

                if (requirement?.Template == null) continue;
                if (!Currencies.Contains(requirement.Template.ToString())) continue;

                var price = (long)Math.Round(requirement.Count ?? 0d);

                if (price <= 0) continue;
                if (cheapest == null || price < cheapest) cheapest = price;
            }

            return cheapest;
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
