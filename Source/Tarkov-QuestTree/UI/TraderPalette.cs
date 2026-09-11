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

        /// <summary>Hues already given to a GENERATED colour, so two modded traders do not come
        /// out the same. Deliberately separate from <see cref="Resolved"/>, which also holds the
        /// curated eight - see <see cref="Generated"/> for why those cannot be avoided.</summary>
        private static readonly List<float> GeneratedHues = new List<float>();

        /// <summary>Every hue bucket outside every status band, built once from
        /// <see cref="ReservedHues"/> so the bands stay the one source of truth.
        ///
        /// Finer than the wheel this used to walk - 24 buckets rather than 12 - because the choice
        /// is now made only among hues that are allowed, instead of walking a coarse wheel and
        /// rejecting most of it.</summary>
        private static readonly List<float> FreeHues = BuildFreeHues();

        private static List<float> BuildFreeHues()
        {
            const int Buckets = 24;
            var free = new List<float>();

            for (var i = 0; i < Buckets; i++)
            {
                var hue = i / (float)Buckets;
                if (!IsReserved(hue)) free.Add(hue);
            }

            return free;
        }

        /// <summary>A colour for a trader nobody anticipated.
        ///
        /// The id picks a starting point among the hues no status colour claims; the walk then steps
        /// to the first that no other generated colour has taken.
        ///
        /// It does NOT try to avoid the curated eight, and that is the correction. It used to, and
        /// the result was that no modded trader ever got a generated colour at all: the eight
        /// vanilla hues, each excluding a bucket and a half either side, between them cover the
        /// entire wheel - Skier 0.03 through Peacekeeper 0.13 through Therapist 0.36, Jaeger 0.49,
        /// Prapor 0.59, Mechanic 0.73, Ragman 0.89 and back round to Skier. Every candidate was
        /// rejected, every modded trader fell through to the "every bucket taken" fallback, and that
        /// fallback hashed straight to a raw hue WITHOUT checking the status bands. So on an install
        /// with several trader mods the normal path was the one path that could hand a trader the
        /// in-progress green - which is what made Aishi's chains unreadable, every box green whether
        /// it was done or locked.
        ///
        /// The priority is what changed, not the mechanism. Never colliding with a STATUS colour is
        /// mandatory: the status colours are the signal this whole channel exists to protect.
        /// Differing from another TRADER is best-effort. A generated colour sitting near Prapor's
        /// blue costs a moment's confusion between two traders that have portraits and occupy
        /// different parts of the tree; a generated colour sitting on the in-progress green costs
        /// the meaning of every box in the chain.</summary>
        private static Color Generated(string traderId)
        {
            if (FreeHues.Count == 0) return GameStyle.DimTextColor;

            var hash = 0;
            foreach (var c in traderId) hash = unchecked(hash * 31 + c);

            var start = Math.Abs(hash % FreeHues.Count);

            for (var step = 0; step < FreeHues.Count; step++)
            {
                var hue = FreeHues[(start + step) % FreeHues.Count];
                if (IsTaken(hue)) continue;

                GeneratedHues.Add(hue);

                // A little stronger than the curated eight: this is read as a 9px slab on a
                // near-black box, not as a large flat area.
                return Color.HSVToRGB(hue, 0.6f, 0.8f);
            }

            // More modded traders than there are distinguishable hues left. Keep the hue the id
            // asked for and separate by brightness instead - still inside the allowed set, which is
            // the part that must not be given up.
            var fallback = FreeHues[start];
            GeneratedHues.Add(fallback);

            return Color.HSVToRGB(fallback, 0.6f, 0.55f + Math.Abs(hash / FreeHues.Count % 3) * 0.16f);
        }

        private static bool IsReserved(float hue)
        {
            foreach (var band in ReservedHues)
                if (hue >= band.From && hue <= band.To) return true;

            return false;
        }

        /// <summary>Whether another GENERATED colour is already this close.
        ///
        /// Hue is a circle, so 0.97 and 0.02 are neighbours; comparing the raw difference called
        /// them a wheel apart and let two traders sit either side of red looking identical.</summary>
        private static bool IsTaken(float hue)
        {
            foreach (var used in GeneratedHues)
            {
                var apart = Mathf.Abs(used - hue);
                if (apart > 0.5f) apart = 1f - apart;

                // Within a bucket and a half of the fine wheel is too close to call apart.
                if (apart < 1f / 24f * 1.5f) return true;
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
            GeneratedHues.Clear();
            _overridesSeen = null;
        }
    }
}
