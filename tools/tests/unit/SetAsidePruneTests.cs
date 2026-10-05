// QuestGraph/MapCapture.cs: which Replace backups a prune deletes (review B15). The newest SetAsideKept folders that hold
// something are kept; an empty .bak folder left by a failed set-aside is neither counted nor deleted, so it never takes
// a real backup's slot.
//
// @@MUTATE SetAsidePrune :: newestFirst.Where(dir => !isEmpty(dir)) :: newestFirst.Where(dir => true)@@
// @@MUTATE SetAsidePrune :: .Skip(Math.Max(0, kept)) :: .Skip(Math.Max(0, kept - 1))@@
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.SetAsidePrune
{
    internal static class MapCapture
    {
// @@CONST Source/Tarkov-QuestTree/QuestGraph/MapCapture.cs SetAsideKept@@

// @@REGION SetAsidePrune@@

        internal static class Tests
        {
            private static List<string> Prune(string[] newestFirst, params string[] empty) =>
                SetAsideToDelete(newestFirst, dir => empty.Contains(dir), SetAsideKept);

            public static void Run(T t)
            {
                t.Case("kept is two, as the review's case assumes", () => t.Eq(2, SetAsideKept, "SetAsideKept"));

                t.Case("three real backups: the oldest goes", () =>
                {
                    var gone = Prune(new[] { "m.bak-3", "m.bak-2", "m.bak-1" });
                    t.Eq("m.bak-1", string.Join(",", gone), "deleted");
                });

                t.Case("an empty newest folder does not take a real backup's slot", () =>
                {
                    var gone = Prune(new[] { "m.bak-4", "m.bak-3", "m.bak-2" }, "m.bak-4");
                    t.Eq(0, gone.Count, "real backups deleted: " + string.Join(",", gone));
                });

                t.Case("an empty folder is never deleted, a third real one still is", () =>
                {
                    var gone = Prune(new[] { "m.bak-5", "m.bak-4", "m.bak-3", "m.bak-2" }, "m.bak-5");
                    t.Eq("m.bak-2", string.Join(",", gone), "deleted");
                });

                t.Case("nothing to prune with fewer than kept", () =>
                {
                    t.Eq(0, Prune(new[] { "m.bak-1" }).Count, "deleted");
                    t.Eq(0, Prune(new string[0]).Count, "deleted from none");
                });
            }
        }
    }
}
