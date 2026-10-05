// UI/Map3DLightProbe.cs: the resolution check's edge test (review B8). A pixel is on the shadow's soft edge between
// EdgeLow and EdgeHigh of the rise from the umbra floor (shade) to lit. Measured from zero, a passing umbra that reads
// 0.17 of lit (the play-test value) counted as edge, so 4096 could never show a narrower edge than 2048.
//
// @@MUTATE ShadowEdge :: var floor = Mathf.Clamp(shade, 0f, lit); :: var floor = 0f;@@
// @@MUTATE ShadowEdge :: value < floor + EdgeHigh * rise :: value < EdgeHigh * lit@@
// @@MUTATE ShadowEdge :: value > floor + EdgeLow * rise :: value > EdgeLow * lit@@
using System;
using UnityEngine;

namespace UnitTests.ShadowEdge
{
    internal static class Map3DLightProbe
    {
// @@CONST Source/Tarkov-QuestTree/UI/Map3DLightProbe.cs EdgeLow@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DLightProbe.cs EdgeHigh@@
// @@CONST Source/Tarkov-QuestTree/UI/Map3DLightProbe.cs ShadowRatioMax@@

// @@REGION ShadowEdge@@

        internal static class Tests
        {
            /// <summary>A row across a linear soft edge: <paramref name="umbra"/> pixels at shade, a ramp of
            /// <paramref name="ramp"/> pixels, the rest lit. Returns the pixels OnEdge counts.</summary>
            private static int Width(int umbra, int ramp, int litPixels, float shade, float lit)
            {
                var count = 0;
                for (var i = 0; i < umbra; i++) if (OnEdge(shade, lit, shade)) count++;
                for (var i = 0; i < ramp; i++)
                    if (OnEdge(shade + (lit - shade) * (i + 0.5f) / ramp, lit, shade)) count++;
                for (var i = 0; i < litPixels; i++) if (OnEdge(lit, lit, shade)) count++;
                return count;
            }

            public static void Run(T t)
            {
                t.Case("an umbra above EdgeLow of lit is not edge", () =>
                {
                    t.True(0.17f > EdgeLow && 0.17f <= ShadowRatioMax, "the play-test ratio no longer exercises the bug");
                    t.True(!OnEdge(0.17f * 0.8f, 0.8f, 0.17f * 0.8f), "the umbra itself counted as edge");
                    t.True(!OnEdge(0.8f, 0.8f, 0.17f * 0.8f), "lit counted as edge");
                });

                t.Case("the 10-90 % band is measured from the umbra floor", () =>
                {
                    const float lit = 1f, shade = 0.3f;
                    t.True(OnEdge(shade + 0.5f * (lit - shade), lit, shade), "the edge's midpoint is not edge");
                    t.True(!OnEdge(shade + 0.05f * (lit - shade), lit, shade), "5 % of the rise counted");
                    t.True(OnEdge(shade + 0.15f * (lit - shade), lit, shade), "15 % of the rise not counted");
                    t.True(OnEdge(shade + 0.88f * (lit - shade), lit, shade), "88 % of the rise not counted");
                    t.True(!OnEdge(shade + 0.95f * (lit - shade), lit, shade), "95 % of the rise counted");
                });

                t.Case("halving the ramp halves the width with a 0.3 umbra", () =>
                {
                    var wide = Width(20, 20, 24, 0.3f, 1f);
                    var narrow = Width(30, 10, 24, 0.3f, 1f);
                    t.True(wide >= 14 && wide <= 18, "reference width " + wide + " is not the ramp's 80 %");
                    t.True(narrow <= 0.6f * wide, "narrow " + narrow + " vs wide " + wide + " reads as clamped");
                });

                t.Case("a zero umbra matches the old measure", () =>
                {
                    t.True(OnEdge(0.5f, 1f, 0f), "midpoint");
                    t.True(!OnEdge(0.05f, 1f, 0f), "below EdgeLow");
                    t.True(!OnEdge(0.95f, 1f, 0f), "above EdgeHigh");
                });
            }
        }
    }
}
