using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QuestTree.UI
{
    /// <summary>
    /// Where the time goes between the taskbar click and the tree being on screen, as one log line
    /// per open. Called on the main thread only, which is also where all but two of the phases are
    /// spent: the quest fetch's request and parse are measured on the worker that does them and
    /// handed to <see cref="Add"/> here - see QuestDataClient.BeginFetchAll.
    ///
    /// Written because the first measurement of that wait (1.13.2) came out at 1.2 to 1.4 seconds
    /// with the server accounting for under a tenth of it - and nothing could say where the rest
    /// went. Every phase is one <see cref="Mark"/>, <see cref="MarkSplit"/> or <see cref="Add"/> on
    /// the open path, so the next release can fix the largest number rather than the most plausible
    /// one. Deliberately not a profiler: named phases and one line, which is what a player pasting a
    /// log can carry.
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

        /// <summary>Names a phase whose length was measured somewhere else - the worker that fetches
        /// and parses the quest list measures its own two halves - and charges it against the
        /// interval since the last mark, exactly as <see cref="MarkSplit"/> charges its head. What is
        /// left of that interval stays open for the next <see cref="Mark"/>, so the phases still sum
        /// to the total this line prints.
        ///
        /// Clamped to what is left, which is what makes that true: work that finished BEFORE this
        /// open's clock started - a fetch that completed while the panel was shut - reports as the
        /// 0 ms this open actually waited for it, rather than as time the line cannot account for.</summary>
        public static void Add(string phase, long millis)
        {
            var now = Clock.ElapsedMilliseconds;
            var available = now - _lastMark;
            var taken = millis < 0 ? 0 : millis > available ? available : millis;

            Phases.Add((phase, taken));
            _lastMark += taken;
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
