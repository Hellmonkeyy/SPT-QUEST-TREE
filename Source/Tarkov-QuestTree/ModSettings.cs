using System;
using BepInEx.Configuration;

namespace QuestTree
{
    /// <summary>
    /// The mod's user-facing settings, bound to BepInEx's config system so they persist to
    /// BepInEx/config/com.takov.questtree.cfg and are also editable from the F12 menu. The in-panel
    /// Settings tab is just a second editor over these same entries - there is deliberately only
    /// one place the values actually live.
    /// </summary>
    internal static class ModSettings
    {
        /// <summary>Raised whenever any setting changes, from either editor, so the panel can
        /// re-render without each toggle having to know who is listening.</summary>
        public static event Action Changed;

        public static ConfigEntry<bool> HideUnobtainable { get; private set; }
        public static ConfigEntry<bool> HideCompleted { get; private set; }
        public static ConfigEntry<bool> HideTraderless { get; private set; }
        public static ConfigEntry<bool> DrawEdges { get; private set; }
        public static ConfigEntry<bool> CompactLayout { get; private set; }

        /// <summary>Whether the Maps view pins only quests you have accepted.</summary>
        public static ConfigEntry<bool> MarkStartedOnly { get; private set; }

        /// <summary>Diagnostic: outline the rectangle the map's coordinates cover.</summary>
        public static ConfigEntry<bool> ShowMapGuides { get; private set; }

        /// <summary>Extra turn applied to the map PICTURE on top of the rotation its own data
        /// declares. Zero is correct for every shipped map; it exists so a map whose data is wrong
        /// can be corrected without a code change.</summary>
        public static ConfigEntry<int> MapArtworkRotation { get; private set; }

        public static ConfigEntry<bool> MirrorMapArtwork { get; private set; }
        public static ConfigEntry<int> MaxVisibleNodes { get; private set; }

        /// <summary>Remembered rather than reset each time, because it is a working preference -
        /// someone who wants the graph wide wants it wide every time they open the tree.</summary>
        public static ConfigEntry<bool> DetailPanelCollapsed { get; private set; }

        /// <summary>Cleared once the intro has been dismissed. Not shown in the Settings tab - it is
        /// state, not a preference; the "?" button in the toolbar is how you get the hint back.</summary>
        public static ConfigEntry<bool> HasSeenIntro { get; private set; }

        public static void Init(ConfigFile config)
        {
            HideUnobtainable = config.Bind(
                "Filters", "Hide unobtainable quests", false,
                "Hide quests this profile can never complete - the other faction's quests, seasonal " +
                "event quests, and quests locked to a different game edition.");

            HideCompleted = config.Bind(
                "Filters", "Hide completed quests", false,
                "Hide quests already handed in, so the tree shows only what is left.");

            HideTraderless = config.Bind(
                "Filters", "Hide quests with no trader", false,
                "Hide quests that are not attached to any trader. These are usually scripted or " +
                "leftover entries rather than anything you can pick up.");

            MarkStartedOnly = config.Bind(
                "Display", "Only mark started quests", false,
                "On the Maps view, show markers only for quests you have actually accepted. The " +
                "shortest way from a map covered in pins to the few that matter today.");

            MapArtworkRotation = config.Bind(
                "Display", "Extra map artwork rotation", 0,
                "Turns the map picture (not the markers) by this many degrees on top of the " +
                "rotation the map's own data declares. Leave at 0 unless a map is drawn wrong.");

            MirrorMapArtwork = config.Bind(
                "Display", "Mirror map artwork", false,
                "Mirrors the map picture (not the markers) left-to-right.");

            ShowMapGuides = config.Bind(
                "Display", "Show map alignment guides", false,
                "Diagnostic. Outlines the area the map's coordinates cover and marks the map " +
                "origin, so a misaligned map picture is visible rather than a matter of opinion.");

            DrawEdges = config.Bind(
                "Display", "Draw prerequisite lines", true,
                "Draw the lines between a quest and what it unlocks. Turning this off is a " +
                "noticeable speed-up on very dense trader chains.");

            CompactLayout = config.Bind(
                "Display", "Compact layout", false,
                "Draw smaller quest boxes packed more tightly together. Fits far more of the tree " +
                "on screen at once, at the cost of the objective line on each box. Off restores " +
                "the original, roomier layout exactly.");

            MaxVisibleNodes = config.Bind(
                "Performance", "Max visible quests", 600,
                new ConfigDescription(
                    "Ceiling on how many quest boxes are built at once. Only reachable when zoomed " +
                    "right out on a large tree; raising it costs frame time, lowering it makes a " +
                    "zoomed-out view show fewer quests.",
                    new AcceptableValueRange<int>(100, 2000)));

            DetailPanelCollapsed = config.Bind(
                "State", "Detail panel collapsed", false,
                "Remembers whether the quest detail panel was left collapsed.");

            HasSeenIntro = config.Bind(
                "State", "Intro shown", false,
                "Set once the first-run controls hint has been dismissed. Clear it to see the hint again.");

            // One handler per entry rather than a single global hook, so this only fires for
            // settings this mod actually owns.
            HideUnobtainable.SettingChanged += Raise;
            HideCompleted.SettingChanged += Raise;
            HideTraderless.SettingChanged += Raise;
            DrawEdges.SettingChanged += Raise;
            CompactLayout.SettingChanged += Raise;
            MaxVisibleNodes.SettingChanged += Raise;

            // The Maps settings were bound but never hooked up, so changing one from the F12 menu
            // raised nothing and the map kept its old markers until something else forced a
            // re-render. The in-panel controls happened to work only because they repaint the view
            // themselves.
            MarkStartedOnly.SettingChanged += Raise;
            MapArtworkRotation.SettingChanged += Raise;
            MirrorMapArtwork.SettingChanged += Raise;
            ShowMapGuides.SettingChanged += Raise;
        }

        /// <summary>True once Init has run. Guards the panel against reading a null entry if the
        /// plugin ever fails to initialise.</summary>
        public static bool Ready => HideUnobtainable != null;

        public static void NotifyChanged() => Changed?.Invoke();

        private static void Raise(object sender, EventArgs e) => Changed?.Invoke();
    }
}
