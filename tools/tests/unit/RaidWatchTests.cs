// QuestGraph/RaidWatch.cs: the raid count MenuMapHost.InMenu / RaidStarting read. Live from the first enable to the
// matching disable, and an extra disable never drives the count below zero (which would make the next raid read "menu").
//
// @@MUTATE RaidWatch :: if (_alive > 0) _alive--; :: _alive--;@@
// @@MUTATE RaidWatch :: _alive > 0; :: _alive >= 0;@@
// @@MUTATE RaidWatch :: Enter() => _alive++; :: Enter() => _alive += 0;@@
namespace UnitTests.RaidWatch
{
    internal static class RaidWatch
    {
// @@REGION RaidWatch@@

        internal static class Tests
        {
            /// <summary>Back to the menu state whatever an earlier case left (the count is static, as in the mod).</summary>
            private static void Drain()
            {
                for (var i = 0; i < 100; i++) Leave();
            }

            public static void Run(T t)
            {
                t.Case("the menu: no watch, no raid", () =>
                {
                    Drain();
                    t.True(!Live, "a raid with no watch alive");
                });

                t.Case("a raid: live from its enable until its disable", () =>
                {
                    Drain();
                    Enter();
                    t.True(Live, "not live after the enable");
                    Leave();
                    t.True(!Live, "still live after the disable");
                });

                t.Case("an extra disable does not hide the next raid", () =>
                {
                    Drain();
                    Leave();
                    Leave();
                    Enter();
                    t.True(Live, "the next raid reads as the menu");
                });

                t.Case("two worlds alive: live until both are gone", () =>
                {
                    Drain();
                    Enter();
                    Enter();
                    Leave();
                    t.True(Live, "not live with one world left");
                    Leave();
                    t.True(!Live, "live with none left");
                });
            }
        }
    }
}
