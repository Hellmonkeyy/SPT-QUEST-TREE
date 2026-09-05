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
        public static ConfigEntry<int> MaxVisibleNodes { get; private set; }

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

            DrawEdges = config.Bind(
                "Display", "Draw prerequisite lines", true,
                "Draw the lines between a quest and what it unlocks. Turning this off is a " +
                "noticeable speed-up on very dense trader chains.");

            MaxVisibleNodes = config.Bind(
                "Performance", "Max visible quests", 600,
                new ConfigDescription(
                    "Ceiling on how many quest boxes are built at once. Only reachable when zoomed " +
                    "right out on a large tree; raising it costs frame time, lowering it makes a " +
                    "zoomed-out view show fewer quests.",
                    new AcceptableValueRange<int>(100, 2000)));

            // One handler per entry rather than a single global hook, so this only fires for
            // settings this mod actually owns.
            HideUnobtainable.SettingChanged += Raise;
            HideCompleted.SettingChanged += Raise;
            HideTraderless.SettingChanged += Raise;
            DrawEdges.SettingChanged += Raise;
            MaxVisibleNodes.SettingChanged += Raise;
        }

        /// <summary>True once Init has run. Guards the panel against reading a null entry if the
        /// plugin ever fails to initialise.</summary>
        public static bool Ready => HideUnobtainable != null;

        public static void NotifyChanged() => Changed?.Invoke();

        private static void Raise(object sender, EventArgs e) => Changed?.Invoke();
    }
}
