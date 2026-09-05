namespace QuestTree.UI
{
    /// <summary>
    /// Every dimension the quest graph is drawn with, in one place, selected by the "Compact
    /// layout" setting.
    ///
    /// This exists so the compact layout is a reversible preset rather than a rewrite: Comfortable
    /// holds exactly the numbers the graph used before compaction was added, so switching back in
    /// Settings restores the previous look precisely. Nothing outside this class should hardcode a
    /// node size or a spacing again.
    ///
    /// Why compaction matters here: the layout gives every leaf its own row, so on a quest-modded
    /// install the "All" tab is ~5,400 rows. At Comfortable spacing that is a canvas around half a
    /// million pixels tall, which is what made it so easy to get lost in.
    /// </summary>
    internal static class LayoutMetrics
    {
        private static bool Compact => ModSettings.Ready && ModSettings.CompactLayout.Value;

        // --- node box ---
        public static float NodeWidth => Compact ? 170f : 220f;
        public static float NodeHeight => Compact ? 52f : 84f;

        // --- graph spacing ---
        // Row spacing must stay above NodeHeight or rows touch; the gap is deliberately smaller in
        // compact mode because that vertical space is what dominates the canvas height.
        public static float ColumnSpacing => Compact ? 195f : 260f;
        public static float RowSpacing => Compact ? 62f : 100f;

        // --- text inside a node ---
        public static int TitleFontSize => Compact ? 11 : 14;
        public static int SubtitleFontSize => Compact ? 9 : 10;
        public static int ObjectiveFontSize => Compact ? 8 : 9;

        public static float TitleOffsetY => Compact ? -12f : -18f;
        public static float SubtitleOffsetY => Compact ? -26f : -36f;
        public static float ObjectiveOffsetY => Compact ? -38f : -52f;
        public static float StatusGlyphOffsetY => Compact ? -38f : -66f;

        /// <summary>The objective preview is the first thing to go when space is tight - the title
        /// and trader identify a quest, the objective line is detail you can click through for.</summary>
        public static bool ShowObjectivePreview => !Compact;

        public static float KappaBadgeSize => Compact ? 14f : 18f;
    }
}
