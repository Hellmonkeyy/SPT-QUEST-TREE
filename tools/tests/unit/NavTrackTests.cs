// (e) MenuMapHost.NavTrack's sampling rule (bd0dc8c): the _AI scene and the last scene are always sampled, even after
// the cheap empty-mesh samples have spent the 40-sample cap - on Streets the NavMesh arrives at scene 245 of 245.
//
// @@MUTATE NavTrack.Rule :: var must = force || index == count; :: var must = index == count;@@
// @@MUTATE NavTrack.Rule :: samples >= MaxSamples :: samples > MaxSamples@@
// @@MUTATE NavTrack.Scenes :: StringComparison.OrdinalIgnoreCase :: StringComparison.Ordinal@@
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.NavTrack
{
    internal static class MenuMapHost
    {
// @@REGION NavTrack.Scenes@@

        internal sealed class NavTrack
        {
// @@REGION NavTrack.Rule@@
        }
    }

    internal static class Tests
    {
        public static void Run(T t)
        {
            t.Case("Streets: the _AI scene at 245/245 is sampled after the 40-sample cap", () =>
            {
                const int count = 245;
                var samples = 1;   // the sample before loading (force: true) counts, as in Sample
                var sampled = new List<int>();

                for (var index = 1; index <= count; index++)
                {
                    var name = index == count ? "city_preset_AI" : $"city_scene_{index}";
                    // empty NavMesh until the _AI scene: dense (cheap) the whole way
                    if (MenuMapHost.NavTrack.Skips(index, count, MenuMapHost.IsKeptScene(name), samples, dense: true)) continue;
                    samples++;
                    sampled.Add(index);
                }

                t.True(sampled.Contains(count), "scene 245/245 (_AI) not sampled");
                t.Eq(39, sampled.Count(i => i < count), "dense samples before the cap");
                t.True(!sampled.Contains(100), "scene 100 sampled past the cap");
                t.Eq(41, samples, "samples in all (cap + the forced _AI)");
            });

            t.Case("an _AI scene mid-run is sampled past the cap, dense or not", () =>
            {
                t.True(!MenuMapHost.NavTrack.Skips(120, 245, MenuMapHost.IsKeptScene("Shoreline_AI"), 40, dense: false), "sparse");
                t.True(!MenuMapHost.NavTrack.Skips(120, 245, MenuMapHost.IsKeptScene("Shoreline_AI"), 400, dense: true), "dense");
            });

            t.Case("the last scene is sampled past the cap even when not _AI", () =>
                t.True(!MenuMapHost.NavTrack.Skips(245, 245, false, 40, dense: false), "skipped"));

            t.Case("past the cap an ordinary scene is skipped", () =>
            {
                t.True(MenuMapHost.NavTrack.Skips(100, 245, false, 40, dense: true), "100/245 at 40 samples, dense");
                t.True(MenuMapHost.NavTrack.Skips(50, 245, false, 40, dense: false), "50/245 at 40 samples, sparse");
            });

            t.Case("under the cap: dense samples every scene, sparse every 25th", () =>
            {
                t.True(!MenuMapHost.NavTrack.Skips(51, 245, false, 39, dense: true), "dense 51");
                t.True(!MenuMapHost.NavTrack.Skips(50, 245, false, 10, dense: false), "sparse 50");
                t.True(MenuMapHost.NavTrack.Skips(51, 245, false, 10, dense: false), "sparse 51");
            });

            t.Case("kept scenes: _AI in any case, nothing else", () =>
            {
                t.True(MenuMapHost.IsKeptScene("Woods_AI"), "Woods_AI");
                t.True(MenuMapHost.IsKeptScene("streets_ai"), "streets_ai");
                t.True(!MenuMapHost.IsKeptScene("Woods_AI_old"), "Woods_AI_old");
                t.True(!MenuMapHost.IsKeptScene("Woods_Lighting"), "Woods_Lighting");
            });
        }
    }
}
