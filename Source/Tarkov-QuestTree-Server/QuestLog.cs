using System;
using SPTarkov.Common.Models.Logging;

namespace QuestTreeServer
{
    /// <summary>
    /// The one switch that decides whether a log line is a line a PLAYER needs or a line a
    /// DEVELOPER needs.
    ///
    /// Why it exists: a normal boot wrote 109 "Quest Tracker" lines, every one of them at
    /// Information, and SPT's shipped sptLogger.json runs both the file and the console at
    /// Information. So the reports that were written to be read while working on the solver - the
    /// per-profile bill, the pricing coverage, the flea audit, one line per weapon build - were
    /// being read by nobody and were burying the five lines that actually say whether the mod is
    /// working. Debug was no answer on its own, because a Debug line is INVISIBLE unless the user
    /// edits sptLogger.json, and telling someone to edit an SPT file before they can send a log is
    /// a worse support experience than the spam.
    ///
    /// So: <see cref="Detail"/> logs at Information when diagnostics are asked for and at Debug
    /// otherwise. The call site does not change, the line's TEXT does not change (notes and the
    /// README grep for some of them), and the only thing that moves is the level.
    ///
    /// Read ONCE, at first use, like <see cref="PartPrices.FleaOnlyMultiple"/> and the other
    /// QUESTTREE_* variables: an environment variable cannot be packaged into a release by
    /// accident, and a value that cannot change mid-run cannot make two halves of one boot disagree
    /// about what they are printing.
    /// </summary>
    public static class QuestLog
    {
        /// <summary>The variable a player is told about in the README and in tools/server-debug.cmd.</summary>
        public const string Variable = "QUESTTREE_DEBUG";

        /// <summary>Training's variable. It implies diagnostics: training exists to be WATCHED, and a
        /// training console with the progress lines hidden would be a blank window for five hours.</summary>
        public const string TrainingVariable = "QUESTTREE_TRAIN";

        private static bool? _diagnostics;

        private static string _why = Variable;

        /// <summary>Whether the diagnostic lines are being printed at Information.</summary>
        public static bool Diagnostics => _diagnostics ??= Asked();

        /// <summary>The sentence the boot's stamp line carries, so the quiet console explains its own
        /// quietness rather than looking like a mod that failed to load.</summary>
        public static string Mode => Diagnostics
            ? $"Diagnostics on ({_why})."
            : $"Diagnostics off ({Variable}=1 turns them on).";

        /// <summary>
        /// A line for whoever is working on the mod, not for the player: Information when diagnostics
        /// are on, Debug when they are not.
        /// </summary>
        public static void Detail<T>(this ISptLogger<T> logger, string line)
        {
            if (Diagnostics) logger.Info(line);
            else logger.Debug(line);
        }

        /// <summary>
        /// Whether a QUESTTREE_* flag is set to something that means yes. Tolerant of case and of the
        /// four spellings people actually type; anything else - including "0", "false" and an empty
        /// value - means no, so `set QUESTTREE_DEBUG=` turns it back off.
        /// </summary>
        public static bool Enabled(string variable)
        {
            var asked = Environment.GetEnvironmentVariable(variable)?.Trim();

            if (string.IsNullOrEmpty(asked)) return false;

            return asked.Equals("1", StringComparison.Ordinal)
                || asked.Equals("true", StringComparison.OrdinalIgnoreCase)
                || asked.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || asked.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Asked()
        {
            if (Enabled(Variable)) return true;

            if (Enabled(TrainingVariable))
            {
                _why = TrainingVariable;
                return true;
            }

            return false;
        }
    }
}
