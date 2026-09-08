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

        /// <summary>The text as literal characters when it carries a "&lt;", else unchanged.
        /// noparse does not nest, so a closing tag inside the text is broken up.</summary>
        public static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
            if (text.StartsWith(Open, System.StringComparison.Ordinal)) return text;

            return Open + text.Replace(Close, "<\u200B/noparse>") + Close;
        }
    }
}
