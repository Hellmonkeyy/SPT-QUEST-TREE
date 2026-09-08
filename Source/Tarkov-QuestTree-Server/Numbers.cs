using System;

using System.Globalization;

namespace QuestTreeServer
{
    internal static class Numbers
    {
        /// <summary>A coordinate rounded to the metre, invariant. The client draws the same key
        /// (ZoneHarvester.Grid); a locale with its own minus sign would otherwise make the two
        /// halves disagree about what "the same zone" is.</summary>
        public static string Grid(float value) => value.ToString("F0", CultureInfo.InvariantCulture);

        /// <summary>A quest count from the database's double, safely: an unchecked cast of NaN or
        /// a value past int range is undefined by the spec and lands on int.MinValue in practice,
        /// which a modded quest's "value": 1e30 would turn into a negative requirement.</summary>
        public static int ToCount(double? value, int fallback = 0)
        {
            if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return fallback;
            if (v <= 0d) return 0;
            return v >= int.MaxValue ? int.MaxValue : (int)v;
        }
    }
}
