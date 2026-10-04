// (d) MapMeshBuilder's mesh guards (e7234f8): InBox (NaN is outside), the out-of-ground rule (OutOfGroundSpan,
// OutOfGroundRefused) over GroundRange, with RealBand dropping the extent probe's float.MaxValue/MinValue sentinels.
// The Lab's +-1000 m planes are refused; a 150 m mast is kept.
//
// @@MUTATE MeshGuards :: min != float.MaxValue && min != float.MinValue && max != float.MaxValue && max != float.MinValue :: true@@
// @@MUTATE MeshGuards :: p.x >= source.BoxMin.x && p.x <= source.BoxMax.x && :: !(p.x < source.BoxMin.x) && !(p.x > source.BoxMax.x) &&@@
// @@MUTATE MeshGuards :: ownHigh > groundHigh + OutOfGroundMetres :: ownHigh > groundHigh + OutOfGroundMetres * 10f@@
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UnitTests.MeshGuards
{
    internal sealed class Renderer
    {
        public int Id;
        public string Path = "";
    }

    internal static class MapMeshBuilder
    {
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs OutOfGroundMetres@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs OutOfGroundNamed@@

        // The members of the real types the region reads, with the real defaults.
        internal sealed class Source
        {
            internal Vector3 BoxMin;
            internal Vector3 BoxMax;
        }

        internal sealed class Band
        {
            internal float MinY;
            internal float MaxY;
        }

        internal sealed class BandWork
        {
            internal Band Source;
        }

        internal sealed class Candidate
        {
            internal Renderer Renderer;
            internal Bounds Bounds;
        }

        internal sealed class Job
        {
            internal readonly List<BandWork> Bands = new List<BandWork>();
            internal float Lowest = float.PositiveInfinity;
            internal float Highest = float.NegativeInfinity;
            internal bool GroundTaken;
            internal float GroundLow = float.PositiveInfinity;
            internal float GroundHigh = float.NegativeInfinity;
            internal int OutOfGround;
            internal readonly List<string> OutOfGroundEntries = new List<string>();
            internal readonly HashSet<int> OutOfGroundSeen = new HashSet<int>();
        }

        private static int RendererId(Renderer renderer) => renderer != null ? renderer.Id : 0;

        private static string SafePath(Renderer renderer) => renderer?.Path ?? "";

// @@REGION MeshGuards@@

        internal static class Tests
        {
            private static int _ids;

            // The Lab as the live capture measured it: bands to -8.5..18.4 m, ray hits inside, plus one band still at the
            // extent probe's empty-box sentinels and one half-filled.
            private static Job Lab()
            {
                var job = new Job { Lowest = -8f, Highest = 15f };
                foreach (var (min, max) in new[] { (-8.5f, -1f), (-1f, 8f), (8f, 18.4f), (float.MaxValue, float.MinValue), (-8.5f, float.MaxValue) })
                    job.Bands.Add(new BandWork { Source = new Band { MinY = min, MaxY = max } });
                return job;
            }

            private static bool Refused(Job job, params float[] ys)
            {
                var positions = new float[ys.Length * 3];
                for (var i = 0; i < ys.Length; i++) positions[i * 3 + 1] = ys[i];
                var candidate = new Candidate
                {
                    Renderer = new Renderer { Id = ++_ids, Path = "lab/building" + _ids },
                    Bounds = new Bounds(new Vector3(0f, 0f, 0f), new Vector3(10f, 300f, 10f))
                };
                return OutOfGroundRefused(job, candidate, positions, ys.Length);
            }

            public static void Run(T t)
            {
                t.Case("The Lab: the -997..1005 m building is refused and named", () =>
                {
                    var job = Lab();
                    t.True(Refused(job, -997f, 3f, 1005f), "kept");
                    t.Eq(-8.5f, job.GroundLow, "ground low");
                    t.Eq(18.4f, job.GroundHigh, "ground high");
                    t.Eq(1, job.OutOfGround, "refusals counted");
                    t.True(job.OutOfGroundEntries.Count == 1 && job.OutOfGroundEntries[0].Contains("y -997.0..1005.0 m"), "not named with its y range");
                });

                t.Case("The Lab: a +1000 m plane and a -1000 m plane are each refused", () =>
                {
                    var job = Lab();
                    t.True(Refused(job, 1000f, 1000.2f), "+1000 m plane kept");
                    t.True(Refused(job, -1000f, -999.8f), "-1000 m plane kept");
                });

                t.Case("a 150 m mast over the highest ground is kept", () =>
                {
                    var job = Lab();
                    t.True(!Refused(job, 18.4f, 168.4f), "mast refused");
                    t.True(!Refused(job, -8.5f, 2f), "a building on the ground refused");
                    t.Eq(0, job.OutOfGround, "refusals counted");
                });

                t.Case("float.MaxValue / MinValue bands are ignored", () =>
                {
                    t.True(!RealBand(float.MaxValue, float.MinValue), "empty box");
                    t.True(!RealBand(-8.5f, float.MaxValue), "half-filled, high");
                    t.True(!RealBand(float.MinValue, 5f), "half-filled, low");
                    t.True(!RealBand(float.NaN, 1f), "NaN");
                    t.True(!RealBand(5f, 1f), "out of order");
                    t.True(RealBand(-8.5f, 18.4f), "a real band");

                    t.True(GroundRange(-8.5f, float.MaxValue, -8f, 15f, out var low, out var high), "no ground");
                    t.Eq(-8f, low, "low from the hits alone");
                    t.Eq(15f, high, "high from the hits alone");

                    var sentinelsOnly = new Job { Lowest = -8f, Highest = 15f };
                    sentinelsOnly.Bands.Add(new BandWork { Source = new Band { MinY = -8.5f, MaxY = float.MaxValue } });
                    t.True(Refused(sentinelsOnly, 1000f, 1000.2f), "a sentinel band let the +1000 m plane through");
                    t.Eq(15f, sentinelsOnly.GroundHigh, "ground high");
                });

                t.Case("GroundRange: bands joined with the hits; nothing at all is no ground", () =>
                {
                    t.True(GroundRange(-8.5f, 18.4f, -10f, 69f, out var low, out var high), "no ground");
                    t.Eq(-10f, low, "low");
                    t.Eq(69f, high, "high");
                    t.True(!GroundRange(float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity, out _, out _), "ground from nothing");
                });

                t.Case("OutOfGroundSpan: 300 m each end, NaN own ends ignored, no ground refuses nothing", () =>
                {
                    t.True(!OutOfGroundSpan(0f, 18.4f + 300f, -8.5f, 18.4f), "exactly 300 m over");
                    t.True(OutOfGroundSpan(0f, 18.4f + 300.5f, -8.5f, 18.4f), "300.5 m over");
                    t.True(OutOfGroundSpan(-8.5f - 300.5f, 0f, -8.5f, 18.4f), "300.5 m under");
                    t.True(!OutOfGroundSpan(float.NaN, float.NaN, -8.5f, 18.4f), "NaN own range");
                    t.True(!OutOfGroundSpan(-997f, 1005f, float.NaN, 18.4f), "NaN ground");
                    t.True(!OutOfGroundSpan(-997f, 1005f, float.PositiveInfinity, float.NegativeInfinity), "no ground");
                });

                t.Case("InBox: inside and on the edge in, NaN and past the edge out", () =>
                {
                    var source = new Source { BoxMin = new Vector3(-2f, -2f, -2f), BoxMax = new Vector3(12f, 32f, 12f) };
                    t.True(InBox(new Vector3(5f, 10f, 5f), source), "inside");
                    t.True(InBox(new Vector3(-2f, 32f, 12f), source), "on the edge");
                    t.True(!InBox(new Vector3(5f, 1005f, 5f), source), "1005 m up");
                    t.True(!InBox(new Vector3(float.NaN, 10f, 5f), source), "NaN x");
                    t.True(!InBox(new Vector3(5f, float.NaN, 5f), source), "NaN y");
                    t.True(!InBox(new Vector3(5f, 10f, float.NaN), source), "NaN z");
                });
            }
        }
    }
}
