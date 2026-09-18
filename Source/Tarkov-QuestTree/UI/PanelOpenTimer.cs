using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QuestTree.UI
{
    /// <summary>
    /// Where the time goes between the taskbar click and the tree being on screen, as one log line
    /// per open. Main-thread only, like everything it measures.
    ///
    /// Written because the first measurement of that wait (1.13.2) came out at 1.2 to 1.4 seconds
    /// with the server accounting for under a tenth of it - and nothing could say where the rest
    /// went. Every phase below is one <see cref="Mark"/> on the open path, so the next release can
    /// fix the largest number rather than the most plausible one. Deliberately not a profiler:
    /// eight named phases and one line, which is what a player pasting a log can carry.
    /// </summary>
    internal static class PanelOpenTimer
    {
        private static readonly Stopwatch Clock = new();
        private static readonly List<(string Name, long Millis)> Phases = new();
        private static long _lastMark;

        /// <summary>Begins an open. Any phases from a previous open are dropped.</summary>
        public static void Start()
        {
            Phases.Clear();
            _lastMark = 0;
            Clock.Restart();
        }

        /// <summary>Closes the phase that has been running since the previous mark and names it.</summary>
        public static void Mark(string phase)
        {
            var now = Clock.ElapsedMilliseconds;
            Phases.Add((phase, now - _lastMark));
            _lastMark = now;
        }

        /// <summary>As <see cref="Mark"/>, but the phase is split in two: <paramref name="first"/>
        /// took the given number of milliseconds of it, and <paramref name="rest"/> the remainder.
        /// For a fetch, where the wait on the server and the parse of what came back are one call
        /// and two very different costs.</summary>
        public static void MarkSplit(string first, long firstMillis, string rest)
        {
            var now = Clock.ElapsedMilliseconds;
            var total = now - _lastMark;
            var head = firstMillis < 0 ? 0 : firstMillis > total ? total : firstMillis;

            Phases.Add((first, head));
            Phases.Add((rest, total - head));
            _lastMark = now;
        }

        /// <summary>The whole open, in one line: the total, then every phase in order.</summary>
        public static string Report()
        {
            var text = new StringBuilder();
            text.Append("QuestTree: panel open - ").Append(Clock.ElapsedMilliseconds).Append(" ms:");

            foreach (var (name, millis) in Phases)
                text.Append(' ').Append(name).Append(' ').Append(millis).Append(',');

            if (Phases.Count > 0) text.Length--;

            return text.ToString();
        }
    }
}
