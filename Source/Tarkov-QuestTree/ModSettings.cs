using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using QuestTree.QuestGraph;
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
        public static ConfigEntry<int> OverviewBelowZoom { get; private set; }
        public static ConfigEntry<int> FocusRadius { get; private set; }
        public static ConfigEntry<BadgeMode> QuestBadges { get; private set; }

        /// <summary>Read by the boxes; both true before Init has run, which is what the palette
        /// and every other look-and-feel default does.</summary>
        public static bool ShowKappaBadge =>
            !Ready || QuestBadges.Value == BadgeMode.Both || QuestBadges.Value == BadgeMode.Kappa;

        public static bool ShowCollectorBadge =>
            !Ready || QuestBadges.Value == BadgeMode.Both || QuestBadges.Value == BadgeMode.Collector;

        // --- Do next ---
        public static ConfigEntry<RankGoal> DoNextGoal { get; private set; }
        public static ConfigEntry<int> DoNextMaxRows { get; private set; }

        // --- Map look ---
        public static ConfigEntry<int> SidebarWidth { get; private set; }
        public static ConfigEntry<int> DoNextRows { get; private set; }
        public static ConfigEntry<bool> ShowItemsSection { get; private set; }
        public static ConfigEntry<bool> ShowTakeWithYou { get; private set; }
        public static ConfigEntry<bool> ShowTraderColours { get; private set; }
        public static ConfigEntry<string> TraderColours { get; private set; }

        /// <summary>Sets one trader's colour inside the packed override string, or clears it when
        /// <paramref name="hex"/> is null.
        ///
        /// One config entry holding "id=#RRGGBB" pairs rather than one entry per trader, because the
        /// set of traders is not known at build time - this install alone adds several - and a mod
        /// that grows a setting every time somebody installs a trader is a mod with an unusable F12
        /// page. The in-game Settings tab writes through this, so nobody has to type the format.</summary>
        public static void SetTraderColour(string traderId, string hex)
        {
            if (!Ready || string.IsNullOrEmpty(traderId)) return;

            var kept = new List<string>();

            foreach (var pair in (TraderColours.Value ?? "").Split(';'))
            {
                var trimmed = pair.Trim();
                if (trimmed.Length == 0) continue;

                var at = trimmed.IndexOf('=');
                if (at <= 0) continue;

                // Drop any existing entry for this trader; whatever is being set replaces it.
                if (string.Equals(trimmed.Substring(0, at).Trim(), traderId, StringComparison.OrdinalIgnoreCase))
                    continue;

                kept.Add(trimmed);
            }

            if (!string.IsNullOrEmpty(hex)) kept.Add($"{traderId}={hex}");

            TraderColours.Value = string.Join(";", kept.ToArray());
        }
        public static ConfigEntry<bool> CountUnacceptedQuests { get; private set; }
        public static ConfigEntry<bool> ShowCredits { get; private set; }
        public static ConfigEntry<PinLabelMode> PinLabels { get; private set; }

        // --- Colours, as hex strings: BepInEx has no built-in converter for UnityEngine.Color ---
        public static ConfigEntry<string> ColorActive { get; private set; }
        public static ConfigEntry<string> ColorAvailable { get; private set; }
        public static ConfigEntry<string> ColorCompleted { get; private set; }
        public static ConfigEntry<string> ColorLocked { get; private set; }
        public static ConfigEntry<string> ColorGated { get; private set; }
        public static ConfigEntry<string> ColorFailed { get; private set; }
        public static ConfigEntry<string> ColorAccent { get; private set; }

        /// <summary>Which generation of the status palette this config holds.
        ///
        /// Exists because BepInEx's Bind NEVER overwrites a value that is already in the .cfg - it
        /// only supplies a default for a key that is absent. So changing a default ships the change
        /// to new installs and to nobody who has ever run the mod, which for a palette fix is the
        /// one group that needed it. Without this the rotation below would have been invisible on
        /// every existing install.</summary>
        public static ConfigEntry<int> ColourScheme { get; private set; }

        /// <summary>The palette before 1.10.0, kept so "Restore previous colours" is one click and
        /// so the migration can tell an untouched old default from a deliberate choice.</summary>
        public static readonly (string Active, string Available, string Completed, string Locked) LegacyColours =
            ("#5CE82B", "#D9A847", "#3D854D", "#6B6B66");

        // --- Behaviour ---
        public static ConfigEntry<bool> Tooltips { get; private set; }
        public static ConfigEntry<bool> HoverSounds { get; private set; }
        public static ConfigEntry<bool> RememberLastView { get; private set; }

        /// <summary>The key that opens and closes the tracker from anywhere in the menu. The only
        /// way in on the two matchmaker screens, which hide the taskbar and our button with it -
        /// see TrackerHotkey.</summary>
        public static ConfigEntry<KeyboardShortcut> OpenTracker { get; private set; }

        /// <summary>Which of the two quest marks the boxes wear. Kappa is the canonical list;
        /// Collector is what this install actually gates Collector behind, which a quest mod can
        /// make a very different set.</summary>
        public enum BadgeMode
        {
            Both,
            Kappa,
            Collector
        }

        /// <summary>Which map pins carry their name at rest. Hover always shows a name.</summary>
        public enum PinLabelMode
        {
            HoverOnly,
            Actionable,
            All
        }

        /// <summary>Every entry, in bind order, so a section can be reset to its defaults.</summary>
        private static readonly List<ConfigEntryBase> Entries = new();

        /// <summary>Depth of ResetEntries calls in progress - set while a whole section is being
        /// reset, so the per-entry change events do not each re-render the panel and one Changed
        /// follows. A counter, not a flag, so a nested
        /// reset (a Changed handler resetting something) cannot clear the guard from under the
        /// outer one and let its remaining entries fire Changed one by one.</summary>
        private static int _resetDepth;

        /// <summary>Init runs once. A second call would bind every entry again and subscribe
        /// Raise a second time, so every change would fire Changed twice.</summary>
        private static bool _initStarted;

        /// <summary>Asks the panel to redraw without any setting having changed - the Settings page
        /// uses it to open and close its dropdowns, which are part of the page it rebuilds.</summary>
        public static void RequestRepaint() => Changed?.Invoke(false);

        /// <summary>Parsed once per change rather than per read. ColorFor runs for every box and
        /// every edge on every zoom step, and parsing the hex string each time allocated a trimmed
        /// copy per call - six hundred of them a frame. Cleared whenever any setting changes;
        /// re-parsing five strings then is nothing.</summary>
        private static readonly Dictionary<ConfigEntry<string>, Color> ColorCache = new();

        public static Color ParseColor(ConfigEntry<string> entry, Color fallback)
        {
            if (entry == null) return fallback;
            if (ColorCache.TryGetValue(entry, out var cached)) return cached;

            var value = entry.Value;
            var color = !string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value.Trim(), out var parsed)
                ? parsed
                : fallback;

            ColorCache[entry] = color;
            return color;
        }

        /// <summary>Puts the given entries back to their defaults, then re-renders once. The
        /// Settings page groups entries by what they affect, which is not the config file's
        /// sections - "Compact layout" is filed under Display in the file but sits in the Tree
        /// column - so a column's reset names its entries rather than a section.</summary>
        public static void ResetEntries(params ConfigEntryBase[] entries)
        {
            if (!Ready || entries == null) return;

            _resetDepth++;
            try
            {
                foreach (var entry in entries)
                    if (entry != null) entry.BoxedValue = entry.DefaultValue;
            }
            finally
            {
                _resetDepth--;
            }

            ColorCache.Clear();
            Changed?.Invoke(true);
        }

        /// <summary>Remembered rather than reset each time, because it is a working preference -
        /// someone who wants the graph wide wants it wide every time they open the tree.</summary>
        public static ConfigEntry<bool> DetailPanelCollapsed { get; private set; }

        /// <summary>Cleared once the intro has been dismissed. Not shown in the Settings tab - it is
        /// state, not a preference; the "?" button in the toolbar is how you get the hint back.</summary>
        public static ConfigEntry<bool> HasSeenIntro { get; private set; }

        /// <summary>The current palette generation. Bump when the defaults change again.</summary>
        private const int CurrentColourScheme = 2;

        /// <summary>Moves an existing config onto the new palette, once.
        ///
        /// 1.10.0 rotated the status colours because the old set gave in-progress and completed the
        /// same hue, separated only by brightness - which failed the one question the tree exists to
        /// answer. Shipping that as a default change alone would have fixed it for new installs and
        /// for nobody else, since Bind leaves an existing value alone.
        ///
        /// Only values that still match the OLD DEFAULT are rewritten. Someone who picked their own
        /// colours picked them on purpose, and having the mod overwrite a deliberate choice is worse
        /// than leaving them on a palette they can change - so a customised entry is left exactly as
        /// it is, and only the untouched ones move. The version stamp goes down either way, so this
        /// runs once and never again.</summary>
        private static void MigrateColourScheme()
        {
            if (ColourScheme.Value >= CurrentColourScheme) return;

            var moved = 0;

            moved += AdoptNewDefault(ColorActive, LegacyColours.Active);
            moved += AdoptNewDefault(ColorAvailable, LegacyColours.Available);
            moved += AdoptNewDefault(ColorCompleted, LegacyColours.Completed);
            moved += AdoptNewDefault(ColorLocked, LegacyColours.Locked);

            // Scheme 2 moved the two detail thresholds to "never". They have since been deleted
            // outright - a box has one appearance now - so there is nothing left to migrate. Their
            // keys STAY in an old config file: BepInEx preserves entries nothing binds, so a config
            // from before 1.10 carries four dead lines under [Tree] and [Tree look]. Harmless, and
            // not worth reflecting into BepInEx's orphan list to remove. (This comment used to say
            // BepInEx drops them; the live file says otherwise.) The version still advances so this
            // never runs twice.
            ColourScheme.Value = CurrentColourScheme;

            if (moved > 0)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: moved {moved} setting(s) onto the 1.10 defaults - in-progress is amber and " +
                    "available is blue so they no longer share a hue with completed, and a box keeps its " +
                    "detail row at every zoom. Settings > Colours > Restore previous colours puts the old " +
                    "palette back.");
            }
        }

        /// <summary>Rewrites one entry to its new default, but only if it still holds the old one.
        /// Returns 1 when it moved, so the caller can report how much it changed.</summary>
        private static int AdoptNewDefault(ConfigEntry<string> entry, string legacyValue)
        {
            if (entry == null) return 0;
            if (!string.Equals(entry.Value?.Trim(), legacyValue, StringComparison.OrdinalIgnoreCase)) return 0;

            entry.Value = (string)entry.DefaultValue;
            return 1;
        }

        /// <summary>Puts the pre-1.10 status colours back, for anyone who preferred them.
        /// Wired to a Settings row rather than left as a .cfg edit.</summary>
        public static void RestoreLegacyColours()
        {
            ColorActive.Value = LegacyColours.Active;
            ColorAvailable.Value = LegacyColours.Available;
            ColorCompleted.Value = LegacyColours.Completed;
            ColorLocked.Value = LegacyColours.Locked;
        }

        public static void Init(ConfigFile config)
        {
            if (_initStarted)
            {
                Plugin.LogSource?.LogWarning("QuestTree: ModSettings.Init called twice - the second call is ignored.");
                return;
            }
            _initStarted = true;

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
                new ConfigDescription(
                    "Turns the map picture (not the markers) by this many degrees on top of the " +
                    "rotation the map's own data declares. Leave at 0 unless a map is drawn wrong.",
                    new AcceptableValueRange<int>(-270, 270)));

            MirrorMapArtwork = config.Bind(
                "Display", "Mirror map artwork", false,
                "Mirrors the map picture (not the markers) left-to-right.");

            ShowMapGuides = config.Bind(
                "Display", "Show map alignment guides", false,
                "Diagnostic. Outlines the area the map's coordinates cover and marks the map " +
                "origin, so a misaligned map picture is visible rather than a matter of opinion.");

            FocusFrontier = config.Bind(
                "Display", "Focus on what you can work on", false,
                "Show only quests within reach of something you can work on now - see Focus reach. " +
                "Toggle with X in the tree, or the Focus button.");

            FocusRadius = config.Bind(
                "Display", "Focus reach", 4,
                new ConfigDescription(
                    "How many quests out from something you can work on Focus reaches. 1 is just what " +
                    "each one needs and unlocks; 4 shows the run either side of it. Anything further " +
                    "away is dropped from the tree entirely while Focus is on.",
                    new AcceptableValueRange<int>(1, 10)));

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

            ShowTraderColours = config.Bind(
                "Tree look", "Show trader colours", true,
                "A coloured stripe down the left edge of each quest, one colour per trader. The box " +
                "itself keeps showing quest status - the stripe is a second, independent signal.");

            TraderColours = config.Bind(
                "Tree look", "Trader colours", "",
                "Override the colour picked for a trader: \"traderId=#RRGGBB\", several separated by " +
                "semicolons. Leave empty for the built-in colours. Traders this mod does not know - " +
                "any trader from a mod - are given a colour derived from their id, so they are " +
                "coloured and distinct without being listed here.");

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

            OverviewBelowZoom = config.Bind(
                "Tree", "Trader overview below zoom", 0,
                new ConfigDescription(
                    "Below this zoom (percent) the quests are replaced by one card per trader, which " +
                    "stays readable at any distance. 0 is off - the tree keeps drawing quests however " +
                    "far out you go.",
                    new AcceptableValueRange<int>(0, 40)));

            QuestBadges = config.Bind(
                "Tree", "Quest badges", BadgeMode.Both,
                "Which marks a quest box wears. Kappa is the canonical Kappa list; Collector is " +
                "what Collector actually requires on this install, which a quest mod can change. " +
                "Both shows K and C; the detail panel names them either way.");

            SidebarWidth = config.Bind(
                "Map", "Sidebar width", 440,
                new ConfigDescription("Width of the quest column beside the map, in pixels.",
                    new AcceptableValueRange<int>(380, 520)));

            DoNextGoal = config.Bind(
                "Do next", "Ranking goal", RankGoal.Balanced,
                "What the Do next list ranks for. The same quest is not equally worth doing under all "
                + "of these: one paying 200k experience and opening nothing tops Fast levelling and sits "
                + "near the bottom of Kappa path.");

            DoNextMaxRows = config.Bind(
                "Do next", "Rows listed", 40,
                new ConfigDescription("How many quests the Do next tab lists before it stops and counts the rest.",
                    new AcceptableValueRange<int>(10, 200)));

            DoNextRows = config.Bind(
                "Map", "Do next rows", 8,
                new ConfigDescription("How many quests the map sidebar's 'Do next here' section lists. 0 hides the section.",
                    new AcceptableValueRange<int>(0, 20)));

            ShowItemsSection = config.Bind(
                "Map", "Show items to find", true,
                "Show the sidebar section listing the quest items that spawn on the map and whether you already have them.");

            ShowTakeWithYou = config.Bind(
                "Map", "Show take with you", true,
                "Show the sidebar section listing what you must CARRY INTO the map to finish its quests, " +
                "and whether it is on you, in your stash, or not owned at all.");

            CountUnacceptedQuests = config.Bind(
                "Map", "Count quests you have not accepted", true,
                "Count quests you could accept but have not, as well as accepted ones, when deciding what to " +
                "take into a raid. Turn this off if the list is noisy - with a mod that unlocks everything, " +
                "'available' is most of the game.");

            ShowCredits = config.Bind(
                "Map", "Show map credits", true,
                "Show the map and pin-icon attributions at the bottom of the sidebar.");

            PinLabels = config.Bind(
                "Map", "Pin labels", PinLabelMode.HoverOnly,
                "Which pins carry their name at rest. Hovering a pin always shows its name. HoverOnly keeps clusters readable; Actionable names in-progress and available quests; All names every pin.");

            ColorActive = config.Bind("Colours", "In progress", "#E8A33D", "Hex colour for quests you have accepted. The F12 menu has a picker; the in-game Settings tab has presets.");
            ColorAvailable = config.Bind("Colours", "Available", "#4FA3E3", "Hex colour for quests you can accept now.");
            ColorCompleted = config.Bind("Colours", "Completed", "#4FBF7F", "Hex colour for quests handed in.");
            ColorLocked = config.Bind("Colours", "Locked", "#6B6B6B", "Hex colour for quests behind another quest.");
            ColorGated = config.Bind("Colours", "Level gated", "#D9534F", "Hex colour for quests whose prerequisites are done but whose level, loyalty or standing requirement is not.");
            ColorFailed = config.Bind("Colours", "Failed", "#9E5C9E", "Hex colour for quests the game has failed or expired.");
            ColorAccent = config.Bind("Colours", "Accent", "#C7A659", "Hex colour for selection, headers and highlights.");

            ColourScheme = config.Bind(
                "Colours", "Scheme version", 0,
                "Which generation of the status palette this file holds. Do not edit: the mod uses it " +
                "to apply a one-time palette update and will not touch your colours again afterwards.");

            MigrateColourScheme();

            Tooltips = config.Bind(
                "Behaviour", "Tooltips", true,
                "Show the game's tooltip when hovering quest boxes and toolbar buttons.");

            HoverSounds = config.Bind(
                "Behaviour", "Hover sounds", true,
                "Play the game's hover sound over buttons and rows.");

            RememberLastView = config.Bind(
                "Behaviour", "Remember last view", false,
                "Open the tracker on whichever view it was closed on, instead of always the map (or the tree).");

            // A modifier by default rather than a bare key, so it cannot be typed into the panel's
            // own search box, and one that no menu screen already uses. Rebindable in the F12 menu,
            // including to nothing at all.
            OpenTracker = config.Bind(
                "Behaviour", "Open tracker shortcut", new KeyboardShortcut(KeyCode.Q, KeyCode.LeftControl),
                "Opens and closes the tracker anywhere in the menu, including the raid ready-up screen where the taskbar is hidden.");

            Entries.AddRange(new ConfigEntryBase[]
            {
                HideUnobtainable, HideCompleted, HideTraderless, MarkStartedOnly, MapArtworkRotation,
                MirrorMapArtwork, ShowMapGuides, DrawEdges, FocusFrontier, CompactLayout, MaxVisibleNodes,
                OpenOnMap, HarvestZones, EdgeOpacity, HoverDimStrength, TallTitles,
                OverviewBelowZoom, FocusRadius, QuestBadges, DoNextGoal, DoNextMaxRows,
                SidebarWidth, DoNextRows, ShowItemsSection,
                ShowTakeWithYou, CountUnacceptedQuests, ShowTraderColours,
                TraderColours, ShowCredits,
                PinLabels, ColorActive, ColorAvailable, ColorCompleted, ColorLocked, ColorGated, ColorFailed, ColorAccent, Tooltips,
                HoverSounds, RememberLastView, OpenTracker
            });

            // One handler per entry rather than a single global hook, so this only fires for
            // settings this mod actually owns.
            DoNextGoal.SettingChanged += Raise;
            DoNextMaxRows.SettingChanged += Raise;
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
            OverviewBelowZoom.SettingChanged += Raise;
            FocusRadius.SettingChanged += Raise;
            QuestBadges.SettingChanged += Raise;
            SidebarWidth.SettingChanged += Raise;
            DoNextRows.SettingChanged += Raise;
            ShowItemsSection.SettingChanged += Raise;
            ShowTakeWithYou.SettingChanged += Raise;
            ShowTraderColours.SettingChanged += Raise;
            TraderColours.SettingChanged += Raise;
            CountUnacceptedQuests.SettingChanged += Raise;
            ShowCredits.SettingChanged += Raise;
            PinLabels.SettingChanged += Raise;
            ColorActive.SettingChanged += Raise;
            ColorAvailable.SettingChanged += Raise;
            ColorCompleted.SettingChanged += Raise;
            ColorLocked.SettingChanged += Raise;
            ColorGated.SettingChanged += Raise;
            ColorFailed.SettingChanged += Raise;
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
            if (_resetDepth > 0) return;

            ColorCache.Clear();

            // Node geometry changes with density, the node budget, and whether titles may take
            // two lines; those need the pooled views thrown away. Tooltips too: the component is
            // added when a box is created, so a pooled box built with tooltips off never gains
            // one. Everything else is a repaint.
            var layout = ReferenceEquals(sender, CompactLayout) || ReferenceEquals(sender, MaxVisibleNodes) ||
                         ReferenceEquals(sender, TallTitles) || ReferenceEquals(sender, Tooltips);
            Changed?.Invoke(layout);
        }
    }
}
