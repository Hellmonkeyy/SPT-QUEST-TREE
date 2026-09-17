using System;

namespace QuestTree
{
    /// <summary>
    /// Names from the server and the game are shown inside TMP rich text, which parses tags in
    /// every string it is handed: a modded quest or trader named with a real tag used to swallow
    /// the rest of its line. Sanitised once where the data enters (QuestDataClient, the trader
    /// list) rather than at each of the dozens of places a name is drawn, so no sink is missed;
    /// the per-site wrapper in GameStyle.Safe remains and is a no-op on an already-wrapped name.
    /// </summary>
    internal static class RichText
    {
        private const string Open = "<noparse>";
        private const string Close = "</noparse>";

        /// <summary>Zero-width space, as an escape rather than an invisible literal in the source -
        /// a character nobody can see in a diff is a character nobody can review.</summary>
        private static readonly string ZeroWidth = char.ConvertFromUtf32(0x200B);

        /// <summary>The text as literal characters when it carries a "&lt;", else unchanged.
        /// noparse does not nest, so a closing tag inside the text is broken up.
        ///
        /// The idempotence test is BOTH ends plus no interior close, not StartsWith(Open) alone.
        /// The cheaper test was bypassable: a name beginning "&lt;noparse&gt;&lt;/noparse&gt;" looked
        /// already-wrapped, was returned untouched, and everything after it parsed - the whole point
        /// of the guard, defeated by a nine-character prefix. The early-out itself has to stay,
        /// because GameStyle.Safe is called at 25 sites whose input was already sanitised at ingest
        /// and dropping it would print literal "&lt;noparse&gt;" on screen.
        ///
        /// It remains idempotent: a string this method wrapped has had its interior closes rewritten,
        /// so its first close IS the trailing one and the test passes.</summary>
        public static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            if (AlreadyWrapped(text)) return text;

            return Open + StripCloses(text) + Close;
        }

        /// <summary>Whether this string is one Safe produced: opens with the tag, ends with it, and
        /// carries no other close in between.</summary>
        private static bool AlreadyWrapped(string text)
        {
            if (!text.StartsWith(Open, StringComparison.Ordinal)) return false;
            if (!text.EndsWith(Close, StringComparison.Ordinal)) return false;
            if (text.Length < Open.Length + Close.Length) return false;

            // The first close must be the trailing one. Case-insensitively, for the reason below.
            var first = text.IndexOf(Close, Open.Length, StringComparison.OrdinalIgnoreCase);
            return first == text.Length - Close.Length;
        }

        /// <summary>Breaks up every closing tag in the text with a zero-width space.
        ///
        /// Case-INSENSITIVE, which string.Replace is not. TMP matches tag names without regard to
        /// case, so a name containing "&lt;/NOPARSE&gt;" closed the wrapper inside TMP while an
        /// ordinal Replace left it alone - and everything after it parsed, which is the full
        /// "&lt;size=400%&gt;" outcome straight through the guard that was supposed to stop it.</summary>
        private static string StripCloses(string text)
        {
            var at = text.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;

            var builder = new System.Text.StringBuilder(text.Length + 16);
            var from = 0;

            while (at >= 0)
            {
                builder.Append(text, from, at - from);

                // The original casing is preserved so the name still reads as its author wrote it;
                // only the tag is broken, by a zero-width space after the slash-less "<".
                builder.Append("<" + ZeroWidth).Append(text, at + 1, Close.Length - 1);

                from = at + Close.Length;
                at = from <= text.Length - Close.Length
                    ? text.IndexOf(Close, from, StringComparison.OrdinalIgnoreCase)
                    : -1;
            }

            builder.Append(text, from, text.Length - from);
            return builder.ToString();
        }
    }
}
