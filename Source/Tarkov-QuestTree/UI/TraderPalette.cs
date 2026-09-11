using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// A colour per trader, for the stripe down the left edge of each quest node.
    ///
    /// Modelled on eft.monster's quest tree, which tints each trader's chain — with one deliberate
    /// difference. That site colours the whole node by trader because it knows nothing about your
    /// profile; this tree uses node colour for STATUS, which is how you read in-progress from
    /// available from locked at a glance and is the more valuable signal in game. So trader colour
    /// gets its own channel rather than taking that one.
    ///
    /// Every trader gets a colour, including ones that do not exist yet. The eight vanilla traders
    /// are named; anything else is DERIVED FROM ITS ID, because a grey fallback would have left most
    /// of this install grey — it already runs ISB-Aishi, Scorpion and others, so a modded trader is
    /// the normal case rather than a future hypothetical.
    /// </summary>
    internal static class TraderPalette
    {
        /// <summary>The vanilla eight, by trader id. Roughly the hues eft.monster uses, so somebody
        /// coming from that site reads the same chains the same way.</summary>
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["54cb50c76803fa8b248b4571"] = "#4A7EBB",   // Prapor    - blue
            ["54cb57776803fa99248b456e"] = "#3FA34D",   // Therapist - green
            ["579dc571d53a0658a154fbec"] = "#8A8F98",   // Fence     - grey
            ["58330581ace78e27b8b10cee"] = "#B5563F",   // Skier     - rust
            ["5935c25fb3acc3127c3d8cd9"] = "#C9A227",   // Peacekeeper - sand
            ["5a7c2eca46aef81a7ca2145d"] = "#7E57C2",   // Mechanic  - violet
            ["5ac3b934156ae10c4430e83c"] = "#C77DAE",   // Ragman    - pink
            ["5c0647fdd443bc2504c2d371"] = "#4FA8A0"    // Jaeger    - teal
        };

        /// <summary>Hues already handed out this session, so two generated colours never come out
        /// indistinguishable. Cleared when the overrides change.</summary>
        private static readonly Dictionary<string, Color> Resolved = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        private static string _overridesSeen;

        /// <summary>Hue bands the status colours occupy, which a generated trader colour must avoid.
        ///
        /// In-progress green sits near 0.28 and available amber near 0.11 by default. A stripe in
        /// either would read as a status rather than as a trader, which is the one thing this must
        /// not do - the status colours are the signal being protected.</summary>
        private static readonly (float From, float To)[] ReservedHues =
        {
            (0.06f, 0.16f),   // the amber "available" band
            (0.24f, 0.42f)    // the green "in progress" band
        };

        /// <summary>The colour for a trader, whatever it is.
        ///
        /// Order: an explicit override from Settings, then the vanilla table, then a colour derived
        /// from the id. The derivation depends on nothing but the id, so it is stable across
        /// sessions and across machines - two players comparing screenshots see the same colours,
        /// and a trader does not change colour because a mod was added beside it.</summary>
        public static Color For(string traderId)
        {
            if (string.IsNullOrEmpty(traderId)) return GameStyle.DimTextColor;

            var overrides = ModSettings.Ready ? ModSettings.TraderColours.Value ?? "" : "";

            // The whole cache, not just one entry: the generated hues depend on which are already
            // taken, so one override changing can move another trader.
            if (!string.Equals(_overridesSeen, overrides, StringComparison.Ordinal))
            {
                _overridesSeen = overrides;
                Resolved.Clear();
            }

            if (Resolved.TryGetValue(traderId, out var cached)) return cached;

            var colour = Resolve(traderId, overrides);
            Resolved[traderId] = colour;
            return colour;
        }

        private static Color Resolve(string traderId, string overrides)
        {
            if (TryOverride(overrides, traderId, out var chosen)) return chosen;

            if (Known.TryGetValue(traderId, out var hex) &&
                ColorUtility.TryParseHtmlString(hex, out var known))
            {
                return known;
            }

            return Generated(traderId);
        }

        /// <summary>A "traderId=#RRGGBB;traderId=#RRGGBB" list from Settings.
        ///
        /// One setting rather than one per trader: the mod cannot grow a colour setting every time
        /// somebody installs a trader mod, and a string is what lets an unknown id be named at
        /// all.</summary>
        private static bool TryOverride(string overrides, string traderId, out Color colour)
        {
            colour = default;
            if (string.IsNullOrEmpty(overrides)) return false;

            foreach (var pair in overrides.Split(';'))
            {
                var at = pair.IndexOf('=');
                if (at <= 0) continue;

                var id = pair.Substring(0, at).Trim();
                if (!string.Equals(id, traderId, StringComparison.OrdinalIgnoreCase)) continue;

                return ColorUtility.TryParseHtmlString(pair.Substring(at + 1).Trim(), out colour);
            }

            return false;
        }

        /// <summary>A colour for a trader nobody anticipated.
        ///
        /// The id picks a starting bucket on a coarse hue wheel; the walk then steps to the first
        /// bucket that is neither reserved by a status colour nor already in use. Coarse on purpose
        /// - a dozen buckets that are obviously different beats a continuous hue nobody can tell
        /// from its neighbour.</summary>
        private static Color Generated(string traderId)
        {
            const int Buckets = 12;

            var hash = 0;
            foreach (var c in traderId) hash = unchecked(hash * 31 + c);

            var start = Math.Abs(hash % Buckets);

            for (var step = 0; step < Buckets; step++)
            {
                var hue = ((start + step) % Buckets) / (float)Buckets;

                if (IsReserved(hue) || IsTaken(hue)) continue;

                // Saturation and value held where the curated eight sit, so a generated colour
                // belongs to the same palette rather than glowing next to them.
                return Color.HSVToRGB(hue, 0.55f, 0.72f);
            }

            // Every bucket taken - more traders than the wheel has room for. Distinctness has run
            // out, so fall back to a stable hue rather than pretending otherwise.
            return Color.HSVToRGB(Math.Abs(hash % 1000) / 1000f, 0.45f, 0.65f);
        }

        private static bool IsReserved(float hue)
        {
            foreach (var band in ReservedHues)
                if (hue >= band.From && hue <= band.To) return true;

            return false;
        }

        private static bool IsTaken(float hue)
        {
            foreach (var used in Resolved.Values)
            {
                Color.RGBToHSV(used, out var usedHue, out _, out _);

                // Within a bucket and a half of an existing one is too close to call apart.
                if (Mathf.Abs(usedHue - hue) < 1f / 12f * 1.5f) return true;
            }

            return false;
        }

        /// <summary>Assigns every trader's colour up front, in a fixed order.
        ///
        /// This is what makes the generated colours STABLE. A generated hue depends on which hues
        /// are already taken, so resolving lazily would let the answer depend on which quest
        /// happened to render first - and a trader that changes colour between sessions, or between
        /// two players looking at the same tree, is worse than one that is merely grey.
        ///
        /// Sorted by id, which is a property of the install rather than of the render: the same set
        /// of mods always produces the same assignment. Called once per graph build.</summary>
        public static void Prime(IEnumerable<string> traderIds)
        {
            Forget();

            if (traderIds == null) return;

            var ordered = new List<string>();
            foreach (var id in traderIds)
                if (!string.IsNullOrEmpty(id) && !ordered.Contains(id)) ordered.Add(id);

            ordered.Sort(StringComparer.OrdinalIgnoreCase);

            // Named traders first, so a curated colour is never displaced by a generated one that
            // happened to claim its hue.
            foreach (var id in ordered)
                if (Known.ContainsKey(id)) For(id);

            foreach (var id in ordered) For(id);
        }

        /// <summary>Drops the cache, for a settings change or a graph rebuild.</summary>
        public static void Forget()
        {
            Resolved.Clear();
            _overridesSeen = null;
        }
    }
}
