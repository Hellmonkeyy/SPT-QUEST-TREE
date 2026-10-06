// UI/SliceMemory.cs: the 3D map's floor remembered per map across sessions (slice slider S4). The floors' signature, the
// opening-floor rule (the remembered floor when the map's floors still sign the same, else the top floor), and the file's
// JSON: a home (NaN) height as null, unknown fields ignored, a corrupt entry skipped, a newer format left alone.
//
// @@MUTATE SliceMemory :: var steps = Math.Round(metres / SignatureMetres, MidpointRounding.AwayFromZero); :: var steps = Math.Round(metres, MidpointRounding.AwayFromZero);@@
// @@MUTATE SliceMemory :: if (seen.Contains(layers[i].Level)) continue; :: @@
// @@MUTATE SliceMemory :: if (remembered != null && has && remembered.Signature == Signature(layers)) return remembered.Level; :: if (remembered != null && has) return remembered.Level;@@
// @@MUTATE SliceMemory :: if (remembered != null && has && remembered.Signature == Signature(layers)) return remembered.Level; :: if (remembered != null && remembered.Signature == Signature(layers)) return remembered.Level;@@
// @@MUTATE SliceMemory :: if (!any || layers[i].Level > top) top = layers[i].Level; :: if (!any || layers[i].Level < top) top = layers[i].Level;@@
// @@MUTATE SliceMemory :: if (!drawable(layers[i].Level)) continue; :: @@
// @@MUTATE SliceMemory :: if (remembered != null && has && remembered.Signature == Signature(layers)) return remembered.Level; :: if (remembered != null && has && remembered.Signature.Length == Signature(layers).Length) return remembered.Level;@@
// @@MUTATE SliceMemory :: Expect(text, ref at, '}'); :: if (at < text.Length) Expect(text, ref at, '}');@@
// @@MUTATE SliceMemory :: text.Append(float.IsNaN(entry.Y) || float.IsInfinity(entry.Y) :: text.Append(float.IsInfinity(entry.Y)@@
// @@MUTATE SliceMemory :: if (number > FormatVersion) :: if (number > 99)@@
// @@MUTATE SliceMemory :: if (levelNumber != Math.Floor(levelNumber) || Math.Abs(levelNumber) > 1000d) continue; :: @@
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UnitTests.SliceMemory
{
    internal static class SliceMemory
    {
// @@REGION SliceMemory@@

        internal static class Tests
        {
            private static (int Level, float MinY, float MaxY)[] Woods() =>
                new[] { (-1, -4.5f, 1f), (0, 6.5f, 19f), (1, 20.5f, 25.5f) };

            public static void Run(T t)
            {
                t.Case("signature: the same floors sign the same, in any order, duplicates as LayerOf finds them", () =>
                {
                    var woods = Signature(Woods());
                    t.Eq(woods, Signature(Woods().Reverse().ToArray()), "listed top first");
                    t.Eq("n=3;-1:-4.5:1.0;0:6.5:19.0;1:20.5:25.5", woods, "text");
                    t.Eq(woods, Signature(Woods().Concat(new[] { (0, 100f, 200f) }).ToArray()), "a second layer on level 0 is not a floor");
                    t.Eq(woods, Signature(new[] { (-1, -4.5f, 1.04f), (0, 6.5f, 19f), (1, 20.5f, 25.5f) }), "under 0.1 m: the same");
                });

                t.Case("signature: a changed height, count or level signs differently", () =>
                {
                    var woods = Signature(Woods());
                    t.True(woods != Signature(new[] { (-1, -4.5f, 1f), (0, 6.5f, 19.3f), (1, 20.5f, 25.5f) }), "ground top 19.0 -> 19.3");
                    t.True(woods != Signature(new[] { (0, 6.5f, 19f), (1, 20.5f, 25.5f) }), "the basement gone");
                    t.True(woods != Signature(new[] { (-2, -4.5f, 1f), (0, 6.5f, 19f), (1, 20.5f, 25.5f) }), "a level renumbered");
                    t.Eq("n=1;0:?:?", Signature(new[] { (0, -2000f, 2000f) }), "the placeholder band is no height");
                    t.Eq("n=1;0:?:?", Signature(new[] { (0, float.NaN, float.NaN) }), "no band");
                });

                t.Case("opening floor: the remembered one when the floors still sign the same, else the top", () =>
                {
                    var woods = Woods();
                    var remembered = new Remembered { Level = -1, Signature = Signature(woods) };
                    Func<int, bool> all = _ => true;

                    t.Eq(-1, OpeningLevel(woods, remembered, all), "remembered, signature matches");
                    t.Eq(1, OpeningLevel(woods, null, all), "nothing remembered: the top floor");
                    t.Eq(1, OpeningLevel(woods, new Remembered { Level = -1, Signature = "n=2;0:0.0:1.0;1:2.0:3.0" }, all), "recaptured: the top");
                    t.Eq(1, OpeningLevel(woods, new Remembered { Level = 5, Signature = Signature(woods) }, all), "the floor is gone: the top");
                    t.Eq(null, OpeningLevel(new (int, float, float)[0], remembered, all), "no floor");
                    t.Eq(3, OpeningLevel(new[] { (3, 0f, 1f), (-1, -5f, -2f) }, null, all), "top by level, not by order");

                    // the same length, another floor: ground top 19.0 -> 19.3
                    var moved = Signature(new[] { (-1, -4.5f, 1f), (0, 6.5f, 19.3f), (1, 20.5f, 25.5f) });
                    t.Eq(moved.Length, Signature(woods).Length, "equal length");
                    t.Eq(1, OpeningLevel(woods, new Remembered { Level = -1, Signature = moved }, all), "a moved floor of the same length: the top");
                });

                t.Case("opening floor: only a floor whose picture can be drawn", () =>
                {
                    var woods = Woods();
                    t.Eq(0, OpeningLevel(woods, null, l => l != 1), "the top floor has no picture: the next one down");
                    t.Eq(1, OpeningLevel(woods, new Remembered { Level = -1, Signature = Signature(woods) }, l => l != -1),
                        "the remembered floor's picture failed: the top");
                    t.Eq(null, OpeningLevel(woods, null, _ => false), "none can be drawn: left as it is");
                });

                t.Case("file: a round trip keeps level, height (NaN as null) and signature", () =>
                {
                    var maps = new Dictionary<string, Remembered>
                    {
                        ["Woods"] = new Remembered { Level = -1, Y = float.NaN, Signature = Signature(Woods()) },
                        ["Inter\"change"] = new Remembered { Level = 1, Y = 31.4f, Signature = "a\\b\nc" }
                    };

                    var text = Encode(maps);
                    t.True(text.Contains("\"y\": null"), "NaN written as null");
                    t.True(TryDecode(text, out var back, out var newer), "read back");
                    t.True(!newer, "not newer");
                    t.Eq(2, back.Count, "entries");
                    t.Eq(-1, back["Woods"].Level, "Woods level");
                    t.True(float.IsNaN(back["Woods"].Y), "Woods home");
                    t.Eq(Signature(Woods()), back["Woods"].Signature, "Woods signature");
                    t.Eq(31.4f, back["Inter\"change"].Y, "a height");
                    t.Eq("a\\b\nc", back["Inter\"change"].Signature, "escapes");
                    t.True(TryDecode(Encode(new Dictionary<string, Remembered>()), out var none, out _) && none.Count == 0, "empty");
                });

                t.Case("file: unknown fields ignored, a corrupt entry skipped, the rest kept", () =>
                {
                    var text = "{ \"version\": 1, \"extra\": [1, {\"a\": true}], \"maps\": {" +
                               "\"Good\": { \"level\": 2, \"y\": null, \"signature\": \"s\", \"later\": \"x\" }," +
                               "\"NoLevel\": { \"y\": 1, \"signature\": \"s\" }," +
                               "\"HalfLevel\": { \"level\": 1.5, \"signature\": \"s\" }," +
                               "\"BadSignature\": { \"level\": 1, \"signature\": 7 }," +
                               "\"NotAnObject\": 3 } }";

                    t.True(TryDecode(text, out var maps, out _), "read");
                    t.Eq("Good", string.Join(",", maps.Keys), "only the good entry");
                    t.Eq(2, maps["Good"].Level, "its level");
                });

                t.Case("file: corrupt or foreign text is no memory; a newer format is left alone", () =>
                {
                    t.True(!TryDecode("{ \"version\": 1, \"maps\": {", out _, out _), "truncated");
                    t.True(!TryDecode("not json", out _, out _), "not JSON");
                    t.True(!TryDecode("[1, 2]", out _, out _), "not an object");
                    t.True(!TryDecode("{ \"maps\": {} }", out _, out _), "no version");
                    t.True(!TryDecode("{ \"version\": 1 } trailing", out _, out _), "trailing text");
                    t.True(!TryDecode("{ \"version\": 1, \"maps\": {} ", out _, out _), "the last brace missing");

                    t.True(TryDecode("{ \"version\": 2, \"maps\": { \"W\": { \"level\": 1, \"signature\": \"s\" } } }", out var maps, out var newer), "newer: read");
                    t.True(newer, "newer: said");
                    t.Eq(0, maps.Count, "newer: no memory");
                    t.True(TryDecode("{ \"version\": 1 }", out var bare, out var current) && !current && bare.Count == 0, "no maps: empty memory");
                });
            }
        }
    }
}
