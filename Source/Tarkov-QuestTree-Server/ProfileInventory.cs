using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// Counts what a profile is holding, by item template, and says where each copy is.
    ///
    /// Shared because three payloads need the same answer for different questions - the Kappa
    /// checklist asks about Collector's items, the item watchlist asks about everything any quest
    /// wants, and the pre-raid check asks what is on your character right now - and a second copy
    /// of this would be a second place for the found-in-raid rule to drift.
    /// </summary>
    internal static class ProfileInventory
    {
        /// <summary>Where a held item actually is. The pre-raid readiness check turns on this and
        /// nothing else: a marker in the stash is not a marker you can plant, and reporting it as
        /// held is the one failure that check exists to prevent.</summary>
        internal enum Bucket
        {
            /// <summary>Neither on the character nor in the stash: hideout area stashes, the sorting
            /// table, the quest stashes. 330 items on the reference profile, so not an edge case.
            ///
            /// Pinned to 0 deliberately - Count falls back to it for an item the walk never
            /// classified, and if a later edit reorders this enum that fallback silently becomes
            /// OnPerson, which is a false "you are carrying it" on every unclassified item.</summary>
            Elsewhere = 0,
            OnPerson = 1,
            InStash = 2
        }

        /// <summary>How many of an item the profile holds, split by found-in-raid status and by
        /// where it is.</summary>
        internal readonly struct Held
        {
            public Held(int foundInRaid, int total, int onPerson, int onPersonFoundInRaid, int inStash)
            {
                FoundInRaid = foundInRaid;
                Total = total;
                OnPerson = onPerson;
                OnPersonFoundInRaid = onPersonFoundInRaid;
                InStash = inStash;
            }

            /// <summary>Found-in-raid copies anywhere in the profile. Split out because quest
            /// hand-ins routinely require found-in-raid, so counting a bought copy as progress would
            /// tell the player they are finished when they are not.</summary>
            public int FoundInRaid { get; }

            /// <summary>Every copy the profile holds, wherever it sits. Meaning deliberately
            /// unchanged: the Items tab and the Collector checklist ask "do I own one", which is
            /// still right for them.</summary>
            public int Total { get; }

            /// <summary>Copies on the character - gear, rig, pockets, backpack, secure container.
            /// What "on you" means, and the only count the readiness cue may go green on.</summary>
            public int OnPerson { get; }

            /// <summary>The found-in-raid subset of OnPerson. Needed as its own number because a
            /// carried item can itself be found-in-raid-only, and without it that case regresses to
            /// counting bought copies. No vanilla carry condition sets the flag, so this ships
            /// unexercised - kept because a quest mod can, not because it is verified.</summary>
            public int OnPersonFoundInRaid { get; }

            /// <summary>Copies in the stash. Never counts as ready, but reported: "missing, 1 in
            /// stash" and "missing" call for completely different actions.</summary>
            public int InStash { get; }

            // Argument order matches the constructor's, and both lead with foundInRaid as the
            // shipped version did. Two adjacent ints with no type to tell them apart is a swap
            // waiting to happen, so the two places that take them agree rather than each reading
            // well alone.
            public Held Plus(int foundInRaid, int count, Bucket bucket) => new(
                FoundInRaid + foundInRaid,
                Total + count,
                OnPerson + (bucket == Bucket.OnPerson ? count : 0),
                OnPersonFoundInRaid + (bucket == Bucket.OnPerson ? foundInRaid : 0),
                InStash + (bucket == Bucket.InStash ? count : 0));
        }

        /// <summary>The last count per profile, kept for a few seconds. The Kappa and profile
        /// routes are fetched together on every panel open and each walked the whole inventory -
        /// tens of thousands of items on a hoarder's stash - for the same answer.</summary>
        private static readonly ConcurrentDictionary<string,
            (DateTime At, Dictionary<string, Held> Counts, bool LocationsKnown)> Recent = new();

        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

        /// <summary>Walks the profile's inventory once and totals it by template, memoised.</summary>
        public static Dictionary<string, Held> CountByTemplate(BotBase? profile, out bool locationsKnown)
        {
            var key = profile?.Id.ToString();
            if (!string.IsNullOrEmpty(key) && Recent.TryGetValue(key, out var recent) &&
                DateTime.UtcNow - recent.At < CacheFor)
            {
                locationsKnown = recent.LocationsKnown;
                return recent.Counts;
            }

            var owned = Count(profile, out locationsKnown);
            if (!string.IsNullOrEmpty(key)) Recent[key] = (DateTime.UtcNow, owned, locationsKnown);
            return owned;
        }

        /// <summary>The shipped one-argument form, for callers with no use for the flag. Kept so
        /// adding the location split does not drag KappaPayloadBuilder into the same commit.</summary>
        public static Dictionary<string, Held> CountByTemplate(BotBase? profile) =>
            CountByTemplate(profile, out _);

        /// <summary>The uncached count, for the readiness check.
        ///
        /// The three-second memo exists for the panel-open double fetch, where two routes ask the
        /// same question in the same breath. Here it is actively harmful: "move the marker and look
        /// again" is the test this feature is judged by, and backing out of the ready-up screen and
        /// re-entering within three seconds would answer from the pre-move snapshot - failing the
        /// test while looking entirely correct.
        /// </summary>
        public static Dictionary<string, Held> CountFresh(BotBase? profile, out bool locationsKnown) =>
            Count(profile, out locationsKnown);

        private static Dictionary<string, Held> Count(BotBase? profile, out bool locationsKnown)
        {
            var owned = new Dictionary<string, Held>();
            locationsKnown = false;

            var inventory = profile?.Inventory;
            var items = inventory?.Items;
            if (items == null) return owned;

            var buckets = ClassifyByRoot(inventory!, out locationsKnown);

            foreach (var item in items)
            {
                if (item == null) continue;

                var template = item.Template.ToString();
                if (string.IsNullOrEmpty(template)) continue;

                // A stack of one carries no StackObjectsCount, so absent means one.
                var count = Numbers.ToCount(item.Upd?.StackObjectsCount, 1);
                if (count < 1) count = 1;

                var foundInRaid = item.Upd?.SpawnedInSession == true ? count : 0;

                // Explicit rather than letting TryGetValue hand back default: the value is the same
                // today, but leaning on it makes the enum's declaration order load-bearing from
                // another file.
                var bucket = buckets.TryGetValue(item.Id, out var found) ? found : Bucket.Elsewhere;

                owned.TryGetValue(template, out var existing);
                owned[template] = existing.Plus(foundInRaid, count, bucket);
            }

            return owned;
        }

        /// <summary>Classifies each item as on the character, in the stash, or neither, by
        /// descending from the two roots the profile names rather than climbing from every item.
        ///
        /// Descent is what makes this safe as well as short. A profile is not obliged to be sane,
        /// and a parent chain that loops would spin a bottom-up walk forever; a cycle is by
        /// definition unreachable from a root, so it cannot be entered here at all. The reference
        /// profile nests 31 deep, so a depth cap would have been a real hazard rather than a
        /// theoretical one.
        ///
        /// The root containers themselves are skipped - the equipment container is an item too, and
        /// counting its template as something you are carrying is meaningless. They therefore fall
        /// to Elsewhere, which is why that bucket holds 330 rather than 328 on the reference
        /// profile.
        /// </summary>
        private static Dictionary<MongoId, Bucket> ClassifyByRoot(BotBaseInventory inventory, out bool known)
        {
            var buckets = new Dictionary<MongoId, Bucket>();
            known = false;

            var items = inventory.Items;
            if (items == null) return buckets;

            // One cache, both roots found in one pass.
            //
            // GenerateItemsMap was used here first and must not be: it is a ToDictionary, so a
            // single duplicated item id throws - and the throw unwinds through CountByTemplate and
            // WalkQuests, which has no try around it, to Guarded, which answers with an EMPTY
            // profile payload. One bad id in a copied or dupe-modded profile would cost the client
            // every level, loyalty, counter and lock reason, not merely the on-person split.
            //
            // baseItemId is deliberately not passed. Verified by decompiling ItemExtensions: it does
            // NOT scope the returned dictionary - the cache always holds every parented item and the
            // parameter only selects which item is reported back as rootItem. So one unscoped build
            // serves both descents, and calling it twice would walk the whole inventory twice to
            // throw one result away.
            var childrenByParent = items.CreateParentIdLookupCache(out _);

            Item? equipmentRoot = null;
            Item? stashRoot = null;

            // Compared directly against the fields, with no null test on them: Equipment and Stash
            // are MongoId?, and MongoId declares op_Equality(MongoId, MongoId?) which is false when
            // the nullable has no value. So a root the profile does not name matches no item, the
            // local stays null, and that is already the "absent" signal `known` reports.
            foreach (var item in items)
            {
                if (item == null) continue;
                if (item.Id == inventory.Equipment) equipmentRoot = item;
                else if (item.Id == inventory.Stash) stashRoot = item;
            }

            void Mark(Item? root, Bucket bucket)
            {
                if (root == null) return;

                foreach (var item in root.GetItemWithChildren(childrenByParent))
                    if (item != null && item.Id != root.Id) buckets[item.Id] = bucket;
            }

            Mark(equipmentRoot, Bucket.OnPerson);
            Mark(stashRoot, Bucket.InStash);

            // BOTH roots, not either. The flag is plural on the wire and the client reads it as
            // "both splits are trustworthy": with Equipment resolved and Stash not, every one of the
            // 3,206 stash items on the reference profile falls to Elsewhere, InStash reads 0 for the
            // whole profile, and the amber "in stash" state - which this feature's own measurement
            // calls the primary content - collapses into a bare red MISSING while the flag says the
            // answer is sound.
            known = equipmentRoot != null && stashRoot != null;
            return buckets;
        }
    }
}
