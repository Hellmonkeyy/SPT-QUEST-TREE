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

            // A name another mod dressed in the game's own markup - a rarity colour, bold - keeps it when every tag is
            // one of the harmless few and they are balanced (Dressed); anything else is shown as literal characters.
            if (Dressed(text)) return text;

            return Open + StripCloses(text) + Close;
        }

        /// <summary>
        /// The tags a name may keep: bold, italic, and a colour by hex code or by a plain name - what mods that colour item
        /// names by rarity (through the game's locale) write, and nothing that changes size, position, sprites or fonts, so
        /// a name can never swallow the rest of its line. A closing tag needs its opening one before it, and every open
        /// tag is closed by the end.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex Allowed = new System.Text.RegularExpressions.Regex(
            @"^<(?:(?<open>b|i)|(?<open>color)=(?:#[0-9a-f]{6}(?:[0-9a-f]{2})?|[a-z]{3,12})|/(?<close>b|i|color))>$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Whether every tag in the text is an <see cref="Allowed"/> one and they balance - such a name is
        /// passed to TMP as it is. A "&lt;" that never closes, an unknown tag, a close without its open, or an open left
        /// open all mean no: the text is escaped whole, as it always was.</summary>
        private static bool Dressed(string text)
        {
            int b = 0, i = 0, colour = 0;
            var at = text.IndexOf('<');

            while (at >= 0)
            {
                var end = text.IndexOf('>', at + 1);
                if (end < 0) return false;

                var inner = text.IndexOf('<', at + 1);
                if (inner >= 0 && inner < end) return false;

                var match = Allowed.Match(text.Substring(at, end - at + 1));
                if (!match.Success) return false;

                var open = match.Groups["open"].Value.ToLowerInvariant();
                var close = match.Groups["close"].Value.ToLowerInvariant();

                if (open == "b") b++;
                else if (open == "i") i++;
                else if (open == "color") colour++;
                else if (close == "b" && --b < 0) return false;
                else if (close == "i" && --i < 0) return false;
                else if (close == "color" && --colour < 0) return false;

                at = text.IndexOf('<', end + 1);
            }

            return b == 0 && i == 0 && colour == 0;
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
