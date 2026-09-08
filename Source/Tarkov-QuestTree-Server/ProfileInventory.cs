using System.Collections.Generic;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// Counts what a profile is holding, by item template.
    ///
    /// Shared because two payloads need the same answer for different questions - the Kappa
    /// checklist asks about Collector's 41 items, the item watchlist asks about everything any
    /// quest wants - and a second copy of this would be a second place for the found-in-raid rule
    /// to drift.
    /// </summary>
    internal static class ProfileInventory
    {
        /// <summary>How many of an item the profile holds, split by found-in-raid status. The split
        /// matters: quest hand-ins routinely require found-in-raid, so counting a bought copy as
        /// progress would tell the player they are finished when they are not.</summary>
        internal readonly struct Held
        {
            public Held(int foundInRaid, int total)
            {
                FoundInRaid = foundInRaid;
                Total = total;
            }

            public int FoundInRaid { get; }
            public int Total { get; }

            public Held Plus(int foundInRaid, int total) => new(FoundInRaid + foundInRaid, Total + total);
        }

        /// <summary>Walks the profile's inventory once and totals it by template. Everything the
        /// profile holds is included - stash, equipment, containers - because "do I own one" is the
        /// question being asked, not "where is it".</summary>
        public static Dictionary<string, Held> CountByTemplate(BotBase? profile)
        {
            var owned = new Dictionary<string, Held>();

            var items = profile?.Inventory?.Items;
            if (items == null) return owned;

            foreach (var item in items)
            {
                if (item == null) continue;

                var template = item.Template.ToString();
                if (string.IsNullOrEmpty(template)) continue;

                // A stack of one carries no StackObjectsCount, so absent means one.
                var count = Numbers.ToCount(item.Upd?.StackObjectsCount, 1);
                if (count < 1) count = 1;

                var foundInRaid = item.Upd?.SpawnedInSession == true ? count : 0;

                owned.TryGetValue(template, out var existing);
                owned[template] = existing.Plus(foundInRaid, count);
            }

            return owned;
        }
    }
}
