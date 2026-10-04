// (b) MapExtentProbe.GrowToPoints / Beyond / Pad (05fd2d6): without Waypoints' NavMesh the extent grows to the spawn
// markers just past its padded rectangle - never a box that already passes, never to a point over 150 m out, never
// over 40 % of an axis.
//
// @@MUTATE ExtentGrowth :: outside <= points.Count * MaxOutsideShare :: outside < 0@@
// @@MUTATE ExtentGrowth :: if (beyond > MaxGrowMetres) :: if (beyond > 1e9)@@
// @@MUTATE ExtentGrowth :: growZ > lengthZ * MaxGrowFraction :: growZ > lengthZ * 10f@@
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UnitTests.ExtentGrowth
{
    internal sealed class HarvestedTrigger
    {
        public string Id = "";
        public string Kind = "";
        public float X;
        public float Y;
        public float Z;
    }

    internal sealed class LogSink
    {
        public readonly List<string> Lines = new List<string>();
        public void LogInfo(object message) => Lines.Add("info " + message);
        public void LogWarning(object message) => Lines.Add("warning " + message);
        public void LogDebug(object message) => Lines.Add("debug " + message);
    }

    internal static class Plugin
    {
        public static LogSink LogSource = new LogSink();
    }

    internal static class MapExtentProbe
    {
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs PadFraction@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs MinimumPad@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs BinHeight@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs MaxOutsideShare@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs MaxGrowMetres@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapExtentProbe.cs MaxGrowFraction@@

// @@REGION ExtentGrowth@@

        internal static class Tests
        {
            // Interchange in the menu without Waypoints (bd0dc8c / 05fd2d6): the vanilla NavMesh box as logged, and 10
            // spawn markers evenly spread over the real logged range (x 162-280, z 333-385.5) - NOT Interchange's real
            // markers, whose exact positions are not on disk - that leave 4.0 % of the points outside, as the log did.
            private static Box Interchange()
            {
                var box = new Box("navmesh");
                box.Add(new Vector3(-354.5f, -5f, -435.2f));
                box.Add(new Vector3(491.9f, 40f, 280.3f));
                return box;
            }

            private static readonly Vector3[] Markers =
            {
                new Vector3(162f, 20f, 333f), new Vector3(175f, 20f, 340.2f), new Vector3(188f, 20f, 346f),
                new Vector3(201f, 20f, 351.7f), new Vector3(214f, 20f, 357f), new Vector3(227f, 20f, 362.4f),
                new Vector3(240f, 20f, 368f), new Vector3(253f, 20f, 373.9f), new Vector3(266f, 20f, 379.5f),
                new Vector3(280f, 20f, 385.5f)
            };

            // n points spread inside a box, deterministic
            private static IEnumerable<Vector3> Inside(Box box, int n)
            {
                for (var i = 0; i < n; i++)
                {
                    var u = (i * 0.618034f) % 1f;
                    var v = (i * 0.414214f + 0.5f / n) % 1f;
                    yield return new Vector3(box.MinX + (box.MaxX - box.MinX) * u, 0f, box.MinZ + (box.MaxZ - box.MinZ) * v);
                }
            }

            private static int OutsideCount(Box box, IEnumerable<Vector3> points)
            {
                var rect = Pad(box);
                return points.Count(p => Outside(rect, p.x, p.z));
            }

            public static void Run(T t)
            {
                t.Case("a box that passes is returned unchanged (1 of 250 outside)", () =>
                {
                    var box = Interchange();
                    var spawns = Inside(box, 249).Concat(new[] { Markers[9] }).ToArray();
                    var grown = GrowToPoints("Interchange", box, null, spawns);
                    t.True(ReferenceEquals(box, grown), "a new box was returned");
                    t.Eq(280.3f, grown.MaxZ, "MaxZ");
                });

                t.Case("a box that passes is returned unchanged (all 10 markers, 1 % outside)", () =>
                {
                    var box = Interchange();
                    var spawns = Inside(box, 990).Concat(Markers).ToArray();
                    t.True(ReferenceEquals(box, GrowToPoints("Interchange", box, null, spawns)), "a new box was returned");
                });

                t.Case("Interchange grows north to z 385.5 and then contains every point", () =>
                {
                    var box = Interchange();
                    var spawns = Inside(box, 240).Concat(Markers).ToArray();
                    t.Eq(10, OutsideCount(box, spawns), "markers outside the padded NavMesh box");

                    var grown = GrowToPoints("Interchange", box, null, spawns);
                    t.True(!ReferenceEquals(box, grown), "not grown");
                    t.Eq(385.5f, grown.MaxZ, "grown MaxZ");
                    t.Eq(-435.2f, grown.MinZ, "MinZ");
                    t.Eq(-354.5f, grown.MinX, "MinX");
                    t.Eq(491.9f, grown.MaxX, "MaxX");
                    t.Eq(280.3f, box.MaxZ, "the box handed in was changed");
                    t.Eq(0, OutsideCount(grown, spawns), "points outside the grown padded box");
                    t.True(Pad(grown).MaxZ >= 385.5 + MinimumPad, "the grown point did not get the full pad");
                });

                t.Case("a bogus marker 10 km out does not drag the box", () =>
                {
                    var box = Interchange();
                    var bogus = new Vector3(200f, 20f, 10385f);
                    var spawns = Inside(box, 240).Concat(Markers).Concat(new[] { bogus }).ToArray();
                    var grown = GrowToPoints("Interchange", box, null, spawns);
                    t.Eq(385.5f, grown.MaxZ, "grown MaxZ");
                    t.Eq(1, OutsideCount(grown, spawns), "the bogus marker is still outside (and counted by the check)");
                    t.True(double.IsPositiveInfinity(Beyond(Pad(box), float.NaN, 0f)), "a NaN point is not infinitely far");
                });

                t.Case("growth over the 0.40 cap is refused", () =>
                {
                    var box = new Box("navmesh");
                    box.Add(new Vector3(0f, 0f, 0f));
                    box.Add(new Vector3(100f, 10f, 100f));
                    // pad 20 m: rect -20..120; points 50 m past it at z 170 - within 150 m, but 70 m is 70 % of the axis
                    var far = Enumerable.Range(0, 10).Select(i => new Vector3(10f * i, 0f, 170f));
                    var spawns = Inside(box, 50).Concat(far).ToArray();
                    Plugin.LogSource.Lines.Clear();
                    var grown = GrowToPoints("Capped", box, null, spawns);
                    t.True(ReferenceEquals(box, grown), "grown past the cap");
                    t.True(Plugin.LogSource.Lines.Any(l => l.StartsWith("warning ") && l.Contains("not grown")), "no warning logged");
                });

                t.Case("triggers count with the spawns", () =>
                {
                    var box = Interchange();
                    var triggers = Markers.Select((m, i) => new HarvestedTrigger { Id = "z" + i, X = m.x, Y = m.y, Z = m.z }).ToList();
                    var grown = GrowToPoints("Interchange", box, triggers, Inside(box, 240).ToArray());
                    t.Eq(385.5f, grown.MaxZ, "grown MaxZ");
                });

                t.Case("Pad: 4 % of each axis, at least 20 m, outward to whole metres", () =>
                {
                    var rect = Pad(Interchange());
                    t.Eq(Math.Floor(-354.5 - 846.4 * 0.04), rect.MinX, "MinX");   // -389
                    t.Eq(309d, rect.MaxZ, "MaxZ");
                    var small = new Box("x");
                    small.Add(new Vector3(0f, 0f, 0f));
                    small.Add(new Vector3(10f, 0f, 10f));
                    t.Eq(-20d, Pad(small).MinX, "minimum pad");
                });
            }
        }
    }
}
