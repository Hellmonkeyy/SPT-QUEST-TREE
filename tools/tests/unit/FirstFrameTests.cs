// UI/Map3DView.cs: which rendered frame is the build's FIRST frame - the one its first-frame log lines and the self-test's
// counts describe. The first on which every shown floor was drawn, or can never be (its picture failed or it has none);
// a frame rendered while a picture is still decoding draws part of the map or none of it (Sandbox 2026-10-05: 0 draw
// calls) and is not it. Said once per build.
//
// @@MUTATE FirstFrame :: if (shown[i] == FloorFrame.Waiting) return false; :: if (shown[i] == FloorFrame.Failed) return false;@@
// @@MUTATE FirstFrame :: if (announced) return false; :: @@
// @@MUTATE FirstFrame :: for (var i = 0; i < shown.Count; i++) :: for (var i = 1; i < shown.Count; i++)@@
// @@MUTATE FirstFrame :: return pictureCanArrive ? FloorFrame.Waiting : FloorFrame.Failed; :: return FloorFrame.Waiting;@@
// @@MUTATE FirstFrame :: if (pictured) return FloorFrame.Drawn; :: @@
// @@MUTATE FirstFrame :: if (!materials) return FloorFrame.Failed; :: @@
using System.Collections.Generic;

namespace UnitTests.FirstFrame
{
    internal static class Map3DView
    {
// @@REGION FirstFrame@@

        internal static class Tests
        {
            private static bool Complete(bool announced, params FloorFrame[] shown) =>
                FirstFrameComplete(new List<FloorFrame>(shown), announced);

            public static void Run(T t)
            {
                t.Case("Sandbox 2026-10-05: two shown floors still decoding is not the first frame", () =>
                {
                    t.True(!Complete(false, FloorFrame.Waiting, FloorFrame.Waiting), "both waiting");
                    t.True(!Complete(false, FloorFrame.Drawn, FloorFrame.Waiting), "the active floor still waiting");
                    t.True(Complete(false, FloorFrame.Drawn, FloorFrame.Drawn), "both drawn");
                    t.True(!Complete(false, FloorFrame.Waiting, FloorFrame.Drawn), "only the first floor waiting");
                    t.True(!Complete(false, FloorFrame.Waiting), "the one floor waiting");
                });

                t.Case("a failed picture never holds the first frame back", () =>
                {
                    t.True(Complete(false, FloorFrame.Failed, FloorFrame.Drawn), "one failed, one drawn");
                    t.True(Complete(false, FloorFrame.Failed), "every shown floor failed: still said");
                    t.True(Complete(false), "no shown floor");
                });

                t.Case("said once per build", () =>
                {
                    t.True(!Complete(true, FloorFrame.Drawn), "already said");
                    t.True(!Complete(true), "already said, no floor");
                });

                t.Case("a floor's state from the draw loop", () =>
                {
                    t.Eq(FloorFrame.Drawn, FrameOfFloor(true, true, true), "picture on: drawn");
                    t.Eq(FloorFrame.Drawn, FrameOfFloor(true, true, false), "picture on though the layer says failed: drawn");
                    t.Eq(FloorFrame.Waiting, FrameOfFloor(true, false, true), "decoding: waiting");
                    t.Eq(FloorFrame.Failed, FrameOfFloor(true, false, false), "failed or no artwork: failed");
                    t.Eq(FloorFrame.Failed, FrameOfFloor(false, true, true), "no materials: never drawable");
                });
            }
        }
    }
}
