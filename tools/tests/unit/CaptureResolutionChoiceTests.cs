// UI/SettingsView.cs: the Settings tab's Capture resolution dropdown. Every value the setting accepts has its own
// option and maps back to itself, so the 16384 default is shown as itself and re-picking the shown option does not
// lower it (review B9).
//
// @@MUTATE CaptureResolutionChoice :: value <= 8192 ? 2 : 3; :: value <= 8192 ? 2 : 2;@@
// @@MUTATE CaptureResolutionChoice :: index == 2 ? 8192 : 16384; :: index == 2 ? 8192 : 8192;@@
// @@MUTATE CaptureResolutionChoice :: value <= 4096 ? 1 : :: value < 4096 ? 1 :@@
namespace UnitTests.CaptureResolutionChoice
{
    internal static class SettingsView
    {
// @@REGION CaptureResolutionChoice@@

        internal static class Tests
        {
            private static readonly int[] Accepted = { 2048, 4096, 8192, 16384 };

            public static void Run(T t)
            {
                t.Case("every accepted value round-trips through its option", () =>
                {
                    foreach (var value in Accepted)
                        t.Eq(value, ResolutionOf(ResolutionIndex(value)), "value " + value);
                });

                t.Case("each accepted value has its own option, in order", () =>
                {
                    for (var i = 0; i < Accepted.Length; i++)
                        t.Eq(i, ResolutionIndex(Accepted[i]), "option of " + Accepted[i]);
                });

                t.Case("the 16384 default is the fourth option, not 8192's", () =>
                {
                    t.Eq(3, ResolutionIndex(16384), "option of the default");
                    t.Eq(16384, ResolutionOf(3), "value of the fourth option");
                });
            }
        }
    }
}
