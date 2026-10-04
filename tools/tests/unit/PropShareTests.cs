// (f) MapMeshBuilder.PropShareFor (review 2026-10-01): the props' share is min(0.40 x cap, room), room =
// 0.9 x ceiling - building demand, clamped at 0; the ceiling is max(3 M, min(memory ceiling, 20 M, size bound)).
//
// @@MUTATE PropShare :: return Math.Min(PropShareOf(cap), Math.Max(0L, room)); :: return Math.Min(PropShareOf(cap), room);@@
// @@MUTATE PropShare :: (long)(ceiling * BudgetShare) :: ceiling@@
// @@MUTATE PropShare :: if (job.SizeBound > 0) ceiling = Math.Min(ceiling, job.SizeBound); :: @@
using System;

namespace UnitTests.PropShare
{
    internal static class MapMeshBuilder
    {
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs PropShareOfCap@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs BudgetShare@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs MemoryFloorTriangles@@
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapMeshBuilder.cs BuilderAbsoluteTriangles@@

        internal sealed class Job
        {
            internal long MemoryCeiling;
            internal long SizeBound;
        }

// @@REGION PropShare@@

        internal static class Tests
        {
            private const long M = 1_000_000;

            private static long Share(long memoryCeiling, long sizeBound, long cap, long demand) =>
                PropShareFor(new Job { MemoryCeiling = memoryCeiling, SizeBound = sizeBound }, cap, demand);

            public static void Run(T t)
            {
                t.Case("the constants are the reviewed ones", () =>
                {
                    t.Eq(0.9, BudgetShare, "BudgetShare");
                    t.Eq(0.40, PropShareOfCap, "PropShareOfCap");
                    t.Eq(3 * M, MemoryFloorTriangles, "MemoryFloorTriangles");
                    t.Eq(20 * M, BuilderAbsoluteTriangles, "BuilderAbsoluteTriangles");
                });

                t.Case("Customs: the ceiling does not bind, the share is 0.40 x cap", () =>
                    t.Eq(7_600_000L, Share(40 * M, 0, 19 * M, 7_400_000), "share"));

                t.Case("a binding ceiling: room = 0.9 x 20 M - 17 M = 1 M", () =>
                    t.Eq(1 * M, Share(40 * M, 0, 19 * M, 17 * M), "share"));

                t.Case("demand over 0.9 x ceiling: room clamped to 0", () =>
                {
                    t.Eq(0L, Share(40 * M, 0, 19 * M, 19 * M), "share at 19 M");
                    t.Eq(0L, Share(40 * M, 0, 19 * M, 18 * M), "share at exactly 0.9 x ceiling");
                });

                t.Case("the size bound lowers the ceiling: 0.9 x 10 M - 5 M = 4 M", () =>
                    t.Eq(4 * M, Share(40 * M, 10 * M, 19 * M, 5 * M), "share"));

                t.Case("the memory floor: a 1 M ceiling counts as 3 M", () =>
                    t.Eq(1_700_000L, Share(1 * M, 0, 10 * M, 1 * M), "share"));

                t.Case("a negative demand counts as 0", () =>
                    t.Eq(2_700_000L, Share(3 * M, 0, 100 * M, -5), "share"));
            }
        }
    }
}
