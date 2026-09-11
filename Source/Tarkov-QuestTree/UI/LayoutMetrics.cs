using UnityEngine;

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
        public static float RowSpacing => Compact ? 62f : 112f;

        /// <summary>The widest a box may grow to fit its own title.
        ///
        /// A clamp, not a target: one pathological modded quest name would otherwise create a
        /// 2,000px box and drag its whole column out with it, since a column is as wide as its
        /// widest member. Anything past this still ellipsises, which is the right answer for a name
        /// nobody can read anyway.</summary>
        public static float MaxNodeWidth => NodeWidth * 2f;

        /// <summary>Space between one column's widest box and the next column's left edge. Replaces
        /// the fixed ColumnSpacing once columns size themselves.</summary>
        public static float ColumnGap => ColumnSpacing - NodeWidth;

        /// <summary>Space between two stacked boxes. Replaces the fixed RowSpacing.</summary>
        public static float RowGap => RowSpacing - NodeHeight;

        /// <summary>A node whose title needs two lines grows by this much (comfortable layout
        /// only - compact has no room). RowSpacing leaves a gap for it: 84 + 16 = 100 &lt; 112.</summary>
        public static float TallNodeExtraHeight => 16f;
        public static bool AllowTallNodes => !Compact && (!ModSettings.Ready || ModSettings.TallTitles.Value);

        // --- the status bar down the left edge, and where text starts to its right ---
        public static float StatusBarWidth => 6f;

        /// <summary>The trader slab, immediately right of the status bar.
        ///
        /// Sized from what actually survives a zoom-out rather than from taste. A stripe scales with
        /// its box, so at the 0.35 zoom where a tree is worth looking at, 5px is under two pixels -
        /// present, and reported as "not working", correctly. Nine is four pixels there and reads as
        /// a colour. It does not need to survive further out than that, because further out the
        /// tree collapses to trader bands that are nothing BUT the colour and the name.</summary>
        public static float TraderStripeWidth => 9f;

        /// <summary>Where text starts: past the status bar, past the trader slab, plus padding.</summary>
        public static float TextInsetX => StatusBarWidth + TraderStripeWidth + 7f;

        /// <summary>Below this zoom the subtitle and objective are hidden and the title grows -
        /// see QuestNodeView.SetDetailLevel. 0.55 is where a 15px title stops being readable.</summary>
        public static float DetailLevelZoom =>
            ModSettings.Ready ? ModSettings.TitleOnlyBelowZoom.Value / 100f : 0.55f;

        /// <summary>Below this zoom the title goes too: a 20px title at a quarter scale is 5px.</summary>
        public static float BarOnlyZoom =>
            ModSettings.Ready ? Mathf.Min(ModSettings.CodesBelowZoom.Value / 100f, DetailLevelZoom - 0.05f) : 0.35f;

        // --- text inside a node ---
        public static int TitleFontSize => Compact ? 12 : 15;
        public static int ZoomedOutTitleFontSize => Compact ? 16 : 20;

        /// <summary>The zoomed-right-out code (QuestNodeView.Abbreviate). Big, because it is
        /// drawn at a quarter scale: 36px in the box is 9px on screen.</summary>
        public static int AbbreviationFontSize => Compact ? 26 : 36;
        // Big enough to be the thing you read, not a decoration in the corner: this and the
        // status bar are what answer "is this one done" at a glance, and the bar alone was doing it
        // badly once boxes stopped being uniform.
        public static int GlyphFontSize => Compact ? 15 : 18;
        public static int SubtitleFontSize => Compact ? 9 : 10;
        public static int ObjectiveFontSize => Compact ? 8 : 9;

        public static float TitleOffsetY => Compact ? -12f : -18f;
        public static float SubtitleOffsetY => Compact ? -26f : -36f;
        public static float ObjectiveOffsetY => Compact ? -38f : -52f;

        /// <summary>The objective preview is the first thing to go when space is tight - the title
        /// and trader identify a quest, the objective line is detail you can click through for.</summary>
        public static bool ShowObjectivePreview => !Compact;

        public static float KappaBadgeSize => Compact ? 14f : 18f;
    }
}
