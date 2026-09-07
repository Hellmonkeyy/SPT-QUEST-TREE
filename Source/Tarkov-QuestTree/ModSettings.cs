using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

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
        /// re-render without each toggle having to know who is listening. The argument is whether
        /// the change alters node geometry (density, node budget) - the one case where the panel
        /// has to throw its pooled views away rather than just repaint.</summary>
        public static event Action<bool> Changed;

        public static ConfigEntry<bool> HideUnobtainable { get; private set; }
        public static ConfigEntry<bool> HideCompleted { get; private set; }
        public static ConfigEntry<bool> HideTraderless { get; private set; }
        public static ConfigEntry<bool> DrawEdges { get; private set; }

        /// <summary>Show only the frontier: quests in progress or available, plus what they need
        /// and what they unlock. The tree with the 700 locked boxes taken out of the way.</summary>
        public static ConfigEntry<bool> FocusFrontier { get; private set; }
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

        /// <summary>Whether a raid reports the map's quest zones to the server half. The off
        /// switch for the one thing this mod does during a raid.</summary>
        public static ConfigEntry<bool> HarvestZones { get; private set; }

        /// <summary>Whether the panel opens on the map rather than the tree.</summary>
        public static ConfigEntry<bool> OpenOnMap { get; private set; }

        // --- Tree look ---
        public static ConfigEntry<int> EdgeOpacity { get; private set; }
        public static ConfigEntry<int> HoverDimStrength { get; private set; }
        public static ConfigEntry<bool> TallTitles { get; private set; }
        public static ConfigEntry<bool> AbbreviateWhenZoomedOut { get; private set; }
        public static ConfigEntry<int> TitleOnlyBelowZoom { get; private set; }
        public static ConfigEntry<int> CodesBelowZoom { get; private set; }

        // --- Map look ---
        public static ConfigEntry<int> SidebarWidth { get; private set; }
        public static ConfigEntry<int> DoNextRows { get; private set; }
        public static ConfigEntry<bool> ShowItemsSection { get; private set; }
        public static ConfigEntry<bool> ShowCredits { get; private set; }
        public static ConfigEntry<PinLabelMode> PinLabels { get; private set; }

        // --- Colours, as hex strings: BepInEx has no built-in converter for UnityEngine.Color ---
        public static ConfigEntry<string> ColorActive { get; private set; }
        public static ConfigEntry<string> ColorAvailable { get; private set; }
        public static ConfigEntry<string> ColorCompleted { get; private set; }
        public static ConfigEntry<string> ColorLocked { get; private set; }
        public static ConfigEntry<string> ColorAccent { get; private set; }

        // --- Behaviour ---
        public static ConfigEntry<bool> Tooltips { get; private set; }
        public static ConfigEntry<bool> HoverSounds { get; private set; }
        public static ConfigEntry<bool> RememberLastView { get; private set; }

        /// <summary>Which map pins carry their name at rest. Hover always shows a name.</summary>
        public enum PinLabelMode
        {
            HoverOnly,
            Actionable,
            All
        }

        /// <summary>Every entry, in bind order, so a section can be reset to its defaults.</summary>
        private static readonly List<ConfigEntryBase> Entries = new();

        /// <summary>Set while a whole section is being reset, so the per-entry change events do
        /// not each re-render the panel; one Changed follows.</summary>
        private static bool _resetting;

        /// <summary>Asks the panel to redraw without any setting having changed - the Settings page
        /// uses it to open and close its dropdowns, which are part of the page it rebuilds.</summary>
        public static void RequestRepaint() => Changed?.Invoke(false);

        public static Color ParseColor(ConfigEntry<string> entry, Color fallback)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Value)) return fallback;
            return ColorUtility.TryParseHtmlString(entry.Value.Trim(), out var color) ? color : fallback;
        }

        /// <summary>Puts every entry in a config-file section back to its default, then
        /// re-renders once.</summary>
        public static void ResetSection(string section)
        {
            var matching = new List<ConfigEntryBase>();
            foreach (var entry in Entries)
                if (string.Equals(entry.Definition.Section, section, StringComparison.Ordinal)) matching.Add(entry);

            ResetEntries(matching.ToArray());
        }

        /// <summary>Puts the given entries back to their defaults, then re-renders once. The
        /// Settings page groups entries by what they affect, which is not the config file's
        /// sections - "Compact layout" is filed under Display in the file but sits in the Tree
        /// column - so a column's reset names its entries rather than a section.</summary>
        public static void ResetEntries(params ConfigEntryBase[] entries)
        {
            if (!Ready || entries == null) return;

            _resetting = true;
            try
            {
                foreach (var entry in entries)
                    if (entry != null) entry.BoxedValue = entry.DefaultValue;
            }
            finally
            {
                _resetting = false;
            }

            Changed?.Invoke(true);
        }

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

            FocusFrontier = config.Bind(
                "Display", "Focus on what you can work on", false,
                "Show only quests in progress or available to start, plus what each one needs and " +
                "what it unlocks. Toggle with X in the tree, or the Focus button.");

            DrawEdges = config.Bind(
                "Display", "Draw prerequisite lines", true,
                "Draw the lines between a quest and what it unlocks. Turning this off is a " +
                "noticeable speed-up on very dense trader chains.");

            CompactLayout = config.Bind(
                "Display", "Compact layout", false,
                "Draw smaller quest boxes packed more tightly together. Fits far more of the tree " +
                "on screen at once, at the cost of the objective line on each box. Off restores " +
                "the original, roomier layout exactly.");

            OpenOnMap = config.Bind(
                "Behaviour", "Open on the map", true,
                "Open the tracker on the Maps view. Off opens it on the quest tree. Either way the " +
                "other is one button away.");

            HarvestZones = config.Bind(
                "Behaviour", "Harvest quest zones in raid", true,
                "A few seconds into a raid, read where every quest zone and quest item on the map " +
                "is and send it to the Quest Tracker server mod, which uses it to pin objectives " +
                "on the Maps tab. One raid per map is enough. Off means the map keeps only what " +
                "it already has.");

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

            EdgeOpacity = config.Bind(
                "Tree", "Prerequisite line opacity", 14,
                new ConfigDescription("How visible the lines between quests are at rest, in percent. Lines into a quest you can act on are drawn stronger than this regardless.",
                    new AcceptableValueRange<int>(0, 60)));

            HoverDimStrength = config.Bind(
                "Tree", "Hover dimming strength", 100,
                new ConfigDescription("How far the rest of the tree fades when a quest is hovered, in percent of the default. 0 turns the effect off.",
                    new AcceptableValueRange<int>(0, 200)));

            TallTitles = config.Bind(
                "Tree", "Two-line titles", true,
                "Let a quest box grow a line so a series/episode title (\"Gunsmith - Part 3\") shows both parts instead of an ellipsis.");

            AbbreviateWhenZoomedOut = config.Bind(
                "Tree", "Codes when zoomed right out", true,
                "Show a short code (EM-4, GUN-3) in each box when zoomed too far out to read a title. Off shows the title only, however small.");

            TitleOnlyBelowZoom = config.Bind(
                "Tree", "Title-only below zoom", 55,
                new ConfigDescription("Below this zoom (percent) a box shows only its title, larger.",
                    new AcceptableValueRange<int>(30, 80)));

            CodesBelowZoom = config.Bind(
                "Tree", "Code-only below zoom", 35,
                new ConfigDescription("Below this zoom (percent) a box shows only its status bar and code.",
                    new AcceptableValueRange<int>(15, 60)));

            SidebarWidth = config.Bind(
                "Map", "Sidebar width", 440,
                new ConfigDescription("Width of the quest column beside the map, in pixels.",
                    new AcceptableValueRange<int>(380, 520)));

            DoNextRows = config.Bind(
                "Map", "Do next rows", 8,
                new ConfigDescription("How many quests the map sidebar's 'Do next here' section lists. 0 hides the section.",
                    new AcceptableValueRange<int>(0, 20)));

            ShowItemsSection = config.Bind(
                "Map", "Show items to find", true,
                "Show the sidebar section listing the quest items that spawn on the map and whether you already have them.");

            ShowCredits = config.Bind(
                "Map", "Show map credits", true,
                "Show the map and pin-icon attributions at the bottom of the sidebar.");

            PinLabels = config.Bind(
                "Map", "Pin labels", PinLabelMode.HoverOnly,
                "Which pins carry their name at rest. Hovering a pin always shows its name. HoverOnly keeps clusters readable; Actionable names in-progress and available quests; All names every pin.");

            ColorActive = config.Bind("Colours", "In progress", "#5CE82B", "Hex colour for quests you have accepted. The F12 menu has a picker; the in-game Settings tab has presets.");
            ColorAvailable = config.Bind("Colours", "Available", "#D9A847", "Hex colour for quests you can accept now.");
            ColorCompleted = config.Bind("Colours", "Completed", "#3D854D", "Hex colour for quests handed in.");
            ColorLocked = config.Bind("Colours", "Locked", "#6B6B66", "Hex colour for quests still gated.");
            ColorAccent = config.Bind("Colours", "Accent", "#C7A659", "Hex colour for selection, headers and highlights.");

            Tooltips = config.Bind(
                "Behaviour", "Tooltips", true,
                "Show the game's tooltip when hovering quest boxes and toolbar buttons.");

            HoverSounds = config.Bind(
                "Behaviour", "Hover sounds", true,
                "Play the game's hover sound over buttons and rows.");

            RememberLastView = config.Bind(
                "Behaviour", "Remember last view", false,
                "Open the tracker on whichever view it was closed on, instead of always the map (or the tree).");

            Entries.AddRange(new ConfigEntryBase[]
            {
                HideUnobtainable, HideCompleted, HideTraderless, MarkStartedOnly, MapArtworkRotation,
                MirrorMapArtwork, ShowMapGuides, DrawEdges, FocusFrontier, CompactLayout, MaxVisibleNodes,
                OpenOnMap, HarvestZones, EdgeOpacity, HoverDimStrength, TallTitles, AbbreviateWhenZoomedOut,
                TitleOnlyBelowZoom, CodesBelowZoom, SidebarWidth, DoNextRows, ShowItemsSection, ShowCredits,
                PinLabels, ColorActive, ColorAvailable, ColorCompleted, ColorLocked, ColorAccent, Tooltips,
                HoverSounds, RememberLastView
            });

            // One handler per entry rather than a single global hook, so this only fires for
            // settings this mod actually owns.
            HideUnobtainable.SettingChanged += Raise;
            HideCompleted.SettingChanged += Raise;
            HideTraderless.SettingChanged += Raise;
            FocusFrontier.SettingChanged += Raise;
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

            EdgeOpacity.SettingChanged += Raise;
            HoverDimStrength.SettingChanged += Raise;
            TallTitles.SettingChanged += Raise;
            AbbreviateWhenZoomedOut.SettingChanged += Raise;
            TitleOnlyBelowZoom.SettingChanged += Raise;
            CodesBelowZoom.SettingChanged += Raise;
            SidebarWidth.SettingChanged += Raise;
            DoNextRows.SettingChanged += Raise;
            ShowItemsSection.SettingChanged += Raise;
            ShowCredits.SettingChanged += Raise;
            PinLabels.SettingChanged += Raise;
            ColorActive.SettingChanged += Raise;
            ColorAvailable.SettingChanged += Raise;
            ColorCompleted.SettingChanged += Raise;
            ColorLocked.SettingChanged += Raise;
            ColorAccent.SettingChanged += Raise;
            Tooltips.SettingChanged += Raise;
            HoverSounds.SettingChanged += Raise;

            // The three Behaviour entries that were bound and never hooked up - the same gap the
            // Maps settings had above. Nothing on screen reads them live, so the miss was
            // invisible from the panel; from the F12 menu it left the in-panel toggle stale.
            OpenOnMap.SettingChanged += Raise;
            HarvestZones.SettingChanged += Raise;
            RememberLastView.SettingChanged += Raise;

            // Last, so a throw anywhere above leaves this false. Testing the first entry instead
            // reported ready after a partial Init, with every later entry still null - the exact
            // case this exists to catch.
            _ready = true;
        }

        private static bool _ready;

        /// <summary>True once Init has run to completion. Guards the panel against reading a null
        /// entry if the plugin ever fails to initialise.</summary>
        public static bool Ready => _ready;

        private static void Raise(object sender, EventArgs e)
        {
            if (_resetting) return;

            // Node geometry changes with density, the node budget, and whether titles may take
            // two lines; those need the pooled views thrown away. Everything else is a repaint.
            var layout = ReferenceEquals(sender, CompactLayout) || ReferenceEquals(sender, MaxVisibleNodes) ||
                         ReferenceEquals(sender, TallTitles);
            Changed?.Invoke(layout);
        }
    }
}
