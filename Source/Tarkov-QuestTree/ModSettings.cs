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

        /// <summary>Bumped by every setting change, from either editor, and by a section reset. For a
        /// view that keeps part of itself across a rebuild and has to decide whether anything it drew
        /// from a setting has moved since - see MapView's kept viewport.
        ///
        /// ONE number rather than a list of the entries that happen to matter to that view: the pin
        /// colours reach the map through QuestNodeView.ColorFor and GameStyle.AccentColor rather than
        /// through any read the map itself makes, so naming entries meant the map kept pins in the old
        /// palette after a colour was changed from the F12 menu. A counter cannot be short of an entry
        /// added later either.</summary>
        public static int Generation { get; private set; }

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

        /// <summary>Draw each single-file run of one trader's quests as one box until it is
        /// clicked open. See QuestGraphView.Render.</summary>
        public static ConfigEntry<bool> CollapseChains { get; private set; }
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

        /// <summary>The key that takes the map's picture from inside a raid - see
        /// QuestGraph/MapCapture.cs. A modifier by default, like the tracker's own shortcut, so it
        /// cannot be pressed by accident in a firefight; rebindable in the F12 menu, including to
        /// nothing at all, and shown (not edited) in the in-panel Settings tab.
        ///
        /// Tested with <see cref="ShortcutDown(KeyboardShortcut)"/>, not BepInEx's IsDown: the movement keys and mouse
        /// buttons a raid holds no longer block the press. A modifier key the shortcut does not name
        /// still does, so Ctrl+Shift+F9 is not this key.</summary>
        public static ConfigEntry<KeyboardShortcut> CaptureMapKey { get; private set; }

        /// <summary>The key that captures the WHOLE map in one press: it teleports the player across a
        /// grid of standable spots, captures at each, and puts them back - see
        /// QuestGraph/MapCampaign.cs. One modifier more than the single capture's key, because it
        /// moves the player and should be harder to hit by accident than a key that only costs a few
        /// frames.
        ///
        /// Tested with <see cref="ShortcutDown(KeyboardShortcut)"/> like the capture key, so the movement keys a raid holds
        /// no longer block it - and the extra modifier still keeps the two apart, because a MODIFIER the
        /// shortcut does not name does block: Ctrl+Shift+F9 fires this key alone (the capture's Ctrl+F9
        /// sees a Shift it never asked for), and Ctrl+F9 fires the capture alone (this key's Shift is not
        /// held). The two may go on sharing a main key, which is what makes "one modifier more" a
        /// meaningful distinction rather than a decoration.</summary>
        public static ConfigEntry<KeyboardShortcut> CampaignKey { get; private set; }

        /// <summary>Whether a raid captures the map by itself every few seconds as the player moves
        /// about it - the map-building raid's setting. Off by default: it hitches every few seconds,
        /// which is a price only somebody building a map picture is getting anything for.</summary>
        public static ConfigEntry<bool> AutoCapture { get; private set; }

        /// <summary>Seconds between automatic captures, when <see cref="AutoCapture"/> is on.</summary>
        public static ConfigEntry<int> AutoCaptureSeconds { get; private set; }

        /// <summary>Longest side, in pixels, of a captured map picture. 2048 or 4096.</summary>
        public static ConfigEntry<int> CaptureResolution { get; private set; }

        /// <summary>Where the Maps tab takes a map's PICTURE from when it has more than one to choose
        /// between: the DynamicMaps mod's hand-made artwork, or one this mod captured in raid.
        ///
        /// A choice rather than a rule, because neither is better everywhere. DynamicMaps' art is
        /// drawn by hand, clean and complete; our captures are photographs of the real world with the
        /// real buildings in the real places, and they exist for maps DynamicMaps has never shipped.
        /// The default prefers DynamicMaps for anyone who already has it - nothing about their map
        /// changes when they install this version - and falls through to our own pictures for
        /// everything it does not cover.</summary>
        public static ConfigEntry<PictureSource> MapPictureSource { get; private set; }

        /// <summary>Which place names are drawn on a CAPTURED picture (ours, or one from the host).
        /// DynamicMaps' own artwork carries its author's labels and is not affected either way.
        ///
        /// ALL of them by default. A player reading a map before a raid wants the place names on it, and
        /// the zone names are the only place names the scene has - our pictures carry no hand-drawn
        /// lettering the way DynamicMaps' artwork does. The fear behind the old default, that forty zone
        /// names would bury a big map in text, is answered by the Maps tab drawing them only from a zoom
        /// in: the wide view stays clean, and the names appear as you go looking for them. Extracts only
        /// and None remain for anyone who disagrees. An existing config that still holds the old default
        /// is moved onto this one once - see <see cref="MigrateMapLabels"/>.</summary>
        public static ConfigEntry<LabelMode> MapLabels { get; private set; }

        /// <summary>Whether a captured map opens in 3D where there is relief to draw, or stays the flat
        /// picture it was before 1.19.0.
        ///
        /// 3D by default, and only where a capture actually carries a mesh: a DynamicMaps map, a
        /// harvested rectangle and a capture taken before the relief existed have nothing to build, so
        /// they draw flat whatever this says and the Maps tab's own toggle is disabled for them. The
        /// setting is global rather than per map because it is a preference about how you like to read a
        /// map, not a property of one.</summary>
        public static ConfigEntry<MapViewMode> MapMode { get; private set; }

        /// <summary>Whether the one-time move of <see cref="MapLabels"/> off its pre-1.19 default has
        /// already run for this config file - see <see cref="MigrateMapLabels"/>. State rather than a
        /// preference, so it is kept out of <see cref="Entries"/> and shows no row in the Settings
        /// tab.</summary>
        private static ConfigEntry<bool> MapLabelsMigrated { get; set; }

        /// <summary>Whether a finished capture is offered to the host this profile plays on - see
        /// QuestGraph/MapTransfer.cs. On by default because the HOST decides: a host that does not
        /// want other people's pictures refuses them itself, and its refusal costs one line a
        /// session. Off is for the player who would rather not offer at all.</summary>
        public static ConfigEntry<bool> UploadCaptures { get; private set; }

        /// <summary>THROWAWAY. The debug key of the 3D map experiments - see QuestGraph/MeshProbe.cs,
        /// which measures in one raid and one menu visit whether a scene mesh can be read back off the
        /// GPU, whether colliders stream out with the player, which layer has no renderer on it, and
        /// whether a RenderTexture viewer draws at all. Deliberately NOT in Entries, so the in-panel
        /// Settings tab shows no row for it; it lives in the F12 menu and the cfg file only. Delete this
        /// entry, its bind and MeshProbe.cs together.
        ///
        /// A bare F10, and tested with <see cref="ShortcutDown(KeyboardShortcut)"/> so the keys a raid
        /// holds do not block it - though a bare binding means Ctrl, Shift or Alt held WILL, since those
        /// are the keys a shortcut is made of.
        ///
        /// The SECTION AND NAME CHANGED with the rename ("Roof probe key (throwaway)" -> "Mesh probe key
        /// (throwaway)"), and that is deliberate rather than incidental. Bind never overwrites a value
        /// already in the cfg file, so under the old name a cfg written by an earlier build would have kept
        /// its Ctrl+F10 - the binding whose modifier is exactly what stopped the first two presses of the
        /// old probe from firing. A new name is a new entry, so this one is written fresh as a bare F10 and
        /// the old line is left orphaned in the cfg, which BepInEx ignores. MeshProbe.Install prints the
        /// bound key at raid start and the menu watcher at plugin load, so which one is live can be read
        /// rather than assumed.</summary>
        public static ConfigEntry<KeyboardShortcut> ProbeKey { get; private set; }

        /// <summary>Which of the two quest marks the boxes wear. Kappa is the canonical list;
        /// Collector is what this install actually gates Collector behind, which a quest mod can
        /// make a very different set.</summary>
        public enum BadgeMode
        {
            Both,
            Kappa,
            Collector
        }

        /// <summary>Where a map's picture comes from. See <see cref="MapPictureSource"/>.</summary>
        public enum PictureSource
        {
            /// <summary>DynamicMaps where it has the map, our capture where it does not.</summary>
            PreferDynamicMaps,

            /// <summary>Our capture where we have one, DynamicMaps where we do not.</summary>
            PreferCaptures,

            /// <summary>Our captures only; a map with no capture shows its bounds and its pins.</summary>
            CapturesOnly
        }

        /// <summary>How a captured map is drawn. See <see cref="MapMode"/>.
        ///
        /// Named MapViewMode and not MapMode because the setting that holds it is MapMode: a nested type
        /// and a property of the same name are a duplicate member, which is why PictureSource sits behind
        /// MapPictureSource and LabelMode behind MapLabels.</summary>
        public enum MapViewMode
        {
            /// <summary>The 3D relief, where the capture has one: the picture draped over the ground's
            /// real heights, with the buildings standing on it.</summary>
            Relief,

            /// <summary>The flat picture, as every release before 1.19.0 drew it.</summary>
            Flat
        }

        /// <summary>Which labels a captured picture carries. See <see cref="MapLabels"/>.</summary>
        public enum LabelMode
        {
            /// <summary>The extraction points only.</summary>
            ExtractsOnly,

            /// <summary>Extracts and the cleaned-up bot zone names.</summary>
            All,

            /// <summary>No labels of ours at all.</summary>
            None
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

        /// <summary>
        /// A shortcut written the way a player would write it - "Ctrl + F9" - rather than the way
        /// BepInEx stores it, which is the main key first and the modifiers after it ("F9 +
        /// LeftControl"), and with the sided key names ("LeftControl") reduced to the one name a
        /// keyboard has printed on it.
        ///
        /// Here rather than in either view because two of them print the same shortcut: the
        /// Settings tab's capture note and the Maps sidebar's "capture one in raid" line. The two
        /// want different spacing - a settings paragraph reads better with spaces, a parenthesised
        /// hint inside a sentence without - so the separator is the caller's, and the naming is not.
        /// </summary>
        /// <param name="shortcut">The bound shortcut. A MainKey of None gives "".</param>
        /// <param name="separator">What to put between the keys, e.g. " + " or "+".</param>
        public static string KeyText(KeyboardShortcut shortcut, string separator)
        {
            if (shortcut.MainKey == KeyCode.None) return "";

            var parts = new List<string>();
            foreach (var modifier in shortcut.Modifiers) parts.Add(KeyName(modifier));
            parts.Add(KeyName(shortcut.MainKey));

            return string.Join(separator ?? " + ", parts.ToArray());
        }

        /// <summary>The keys that BLOCK a shortcut by being held when the shortcut does not name them -
        /// the six sided modifier keys and nothing else. Ctrl, Shift and Alt are the keys a shortcut is
        /// built out of, so one of them held is a statement about which shortcut was meant; W, a mouse
        /// button and the rest are what a player's hands are doing anyway and say nothing.
        ///
        /// Sided, and compared as the KeyCodes they are: a shortcut bound to LeftControl is not satisfied
        /// by RightControl (it never was - the modifier test needs the key it names), and holding BOTH
        /// control keys blocks it, because the second one is a held modifier the shortcut does not name.
        /// That is the same rule in both directions rather than a special case.</summary>
        private static readonly KeyCode[] ModifierBlockKeys =
        {
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt
        };

        /// <summary>
        /// Whether a bound shortcut was pressed THIS frame: the main key went down, every modifier it
        /// names is held, and no OTHER modifier key is.
        ///
        /// Here instead of BepInEx's own <c>KeyboardShortcut.IsDown</c> because that method triggers on
        /// the EXACT combination: it walks a list of block keys and refuses the press if any key outside
        /// the shortcut is held. In a raid that is almost always - W, Shift, a mouse button - so a capture
        /// key pressed while moving silently did nothing, and no modified shortcut in this mod had ever
        /// fired in a raid. The three raid keys (capture, campaign, mesh probe) use this test instead.
        ///
        /// The difference from BepInEx is exactly one thing: WHICH held keys block. Only the six sided
        /// modifier keys do (<see cref="ModifierBlockKeys"/>), so W+Ctrl+F9 fires Ctrl+F9 while
        /// Ctrl+Shift+F9 does not - the Shift is a modifier the shortcut does not name. That keeps what
        /// the modifier count was always for: the campaign key is one modifier more than the single
        /// capture's precisely so that pressing it cannot also be read as the capture, and with the block
        /// list narrowed rather than emptied, two shortcuts may still share a main key.
        ///
        /// What that costs a BARE binding, said plainly because it is the one case that can still
        /// surprise: a shortcut of one unmodified key does not fire while Ctrl, Shift or Alt is held, and
        /// Shift is sprint. A capture key rebound to a bare M is therefore silent mid-sprint and fires the
        /// moment the player stops sprinting. Nothing can be done about that without giving up the
        /// exclusivity above; anyone who wants a key that fires whatever the hands are doing should bind a
        /// function key with no modifier and press it standing still, which is what the probe key does.
        /// </summary>
        /// <param name="shortcut">The bound shortcut. A MainKey of None never fires.</param>
        public static bool ShortcutDown(KeyboardShortcut shortcut) =>
            ShortcutDown(shortcut, Input.GetKeyDown, Input.GetKey);

        /// <summary>The test itself, with the input reads handed in so it can be exercised without a
        /// running game: a few lines of reflection against the built DLL can walk the whole truth table
        /// - the raid keys with a movement key held, the two F9 shortcuts against each other, a bare key
        /// under Shift, an unbound one - which is how the rules above were checked rather than argued
        /// for. There is no such harness in tools/; it is a throwaway, and this seam is what makes
        /// writing one a five-minute job.</summary>
        /// <param name="shortcut">The bound shortcut.</param>
        /// <param name="wentDown">Whether a key went down this frame - Input.GetKeyDown in the game.</param>
        /// <param name="held">Whether a key is held - Input.GetKey in the game.</param>
        internal static bool ShortcutDown(
            KeyboardShortcut shortcut, Func<KeyCode, bool> wentDown, Func<KeyCode, bool> held)
        {
            if (wentDown == null || held == null) return false;
            if (shortcut.MainKey == KeyCode.None) return false;

            // The main key first, so nothing below runs on a frame this shortcut cannot fire on: it is
            // false for all but one frame of a press, and the enumerations after it are then never made.
            if (!wentDown(shortcut.MainKey)) return false;

            var modifiers = shortcut.Modifiers;

            foreach (var modifier in modifiers)
                if (!held(modifier)) return false;

            foreach (var key in ModifierBlockKeys)
            {
                if (!held(key)) continue;

                // A shortcut whose MAIN key is itself a modifier - a bare Shift - is not blocked by its
                // own key being held, which is what pressing it means.
                if (key == shortcut.MainKey) continue;

                var named = false;
                foreach (var modifier in modifiers)
                {
                    if (modifier != key) continue;

                    named = true;
                    break;
                }

                if (!named) return false;
            }

            return true;
        }

        /// <summary>One key as a player would name it: "Ctrl", not "LeftControl".</summary>
        /// <param name="key">The key to name.</param>
        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return "Ctrl";
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return "Shift";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return "Alt";
                default:
                    return key.ToString();
            }
        }

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
            Generation++;
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

        /// <summary>The label mode this setting defaulted to before 1.19.0, kept so the migration below can
        /// tell an untouched old default from a deliberate choice - the same test
        /// <see cref="MigrateColourScheme"/> makes on the colours.</summary>
        private const LabelMode LegacyMapLabels = LabelMode.ExtractsOnly;

        /// <summary>Moves an existing config onto the new "Map labels" default, once.
        ///
        /// 1.19.0 changed that default from extracts only to ALL names, because a captured picture carries
        /// no hand-drawn lettering and the zone names are the only place names it has - and because the
        /// Maps tab now draws them from a zoom in, so the wide view stays clean either way.
        ///
        /// WHO THIS COVERS (review F50): only configs written by the 1.19 TEST builds. "Map labels" was first
        /// bound in stage C of 1.19 and no released version (1.15-1.18.x) ever wrote it, so on a real upgrade
        /// the key is absent, Bind writes the new default, and this only sets its marker. Kept for the test-build
        /// configs, which do hold the old default explicitly.
        ///
        /// Only a value that still equals the OLD DEFAULT moves, and only once - the marker entry goes
        /// down whether anything moved or not. Someone who chose extracts only on purpose, or who chooses
        /// it again after this has run, keeps it: this is a default catching up, not a preference being
        /// overruled.</summary>
        private static void MigrateMapLabels()
        {
            if (MapLabelsMigrated == null || MapLabels == null) return;
            if (MapLabelsMigrated.Value) return;

            MapLabelsMigrated.Value = true;

            if (MapLabels.Value != LegacyMapLabels) return;

            MapLabels.Value = LabelMode.All;

            Plugin.LogSource?.LogInfo(
                "QuestTree: 'Map labels' moved from extracts only to all names, the 1.19 default - a captured " +
                "map's zone names appear as you zoom in, so the wide view stays as clean as it was. " +
                "Settings > Map > Map labels puts it back, and this will not be changed again.");
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

            CollapseChains = config.Bind(
                "Tree", "Collapse chains", true,
                "Draw a single-file run of quests from one trader (\"Gunsmith - Part 1\" through " +
                "\"Part 25\") as one box showing how many are done. Click the box to open the run " +
                "up; the - mark on its first quest closes it again. Toggle with C in the tree, or " +
                "the Chains button - off and on again closes every open chain.");

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

            // Ctrl+F9 for the same reasons the tracker's own shortcut is Ctrl+Q: a modifier so it
            // cannot be hit by accident, and a function key no raid control already uses.
            CaptureMapKey = config.Bind(
                "Map", "Capture map picture key", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
                "Pressed inside a raid, this draws the map from above - one picture per floor - and " +
                "writes it to BepInEx/plugins/QuestTree/captures/, which the Maps tab then uses instead " +
                "of another mod's artwork. It takes a second or two and costs a few frames; one raid per " +
                "map is enough, and a map you never capture simply keeps the picture it already had. " +
                "Needs 'Harvest quest zones in raid' on, since the picture is drawn to the rectangle that " +
                "harvest measures.");

            // Ctrl+Shift+F9, one modifier more than the single capture above it, and the two can still
            // share a main key. This used to credit BepInEx's exact-combination rule for that. The
            // exactness is what stopped either key working in a raid - any held movement key blocked it -
            // so both now go through ShortcutDown, which keeps the half of the rule that does the work:
            // a held MODIFIER the shortcut does not name still blocks, so Ctrl+Shift+F9 is not Ctrl+F9,
            // while W or a mouse button held blocks nothing.
            //
            // It reads back as "Shift + Ctrl + F9" wherever KeyText prints it: BepInEx sorts a
            // shortcut's modifiers by KeyCode, and LeftShift is 304 against LeftControl's 306. The
            // same combination either way round; said here so the spelling in the panel is not read
            // as a different key.
            CampaignKey = config.Bind(
                "Map", "Capture the whole map key", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl, KeyCode.LeftShift),
                "Pressed inside a raid, this captures the WHOLE map without you walking it: it teleports " +
                "you across a grid of standable spots about 120 m apart, takes a capture at each, and returns " +
                "you to where you pressed it. Start the raid with AI set to none - it does not disable bots, and " +
                "it leaves you standing still for a second and a half at every stop and for the length of each " +
                "capture. Customs is 9 x 5 cells of 115 x 100 m, about 33 stops once the cells with nowhere to " +
                "stand are dropped; the 3D model is rebuilt at every stop. The log's closing line gives the " +
                "campaign's real duration. Needs 'Harvest quest zones in raid' on, like the single capture key.");

            AutoCapture = config.Bind(
                "Map", "Capture the map automatically while I play", false,
                "Capture the map by itself, every few seconds, whenever you have moved since the last one, " +
                "so a raid spent walking about builds the picture as you go; each capture merges into the " +
                "map, nearest capture winning per pixel. This WILL hitch every few seconds - it is a " +
                "map-building tool for a raid you have set aside for it, not something to leave on while you " +
                "play for real. Needs 'Harvest quest zones in raid' on, and starts as soon as the raid has " +
                "measured the map's rectangle a few seconds in.");

            AutoCaptureSeconds = config.Bind(
                "Map", "Seconds between automatic captures", 5,
                new ConfigDescription(
                    "How long to wait after one automatic capture before another may be taken. Low numbers " +
                    "build the map fastest and hitch most; the wait is measured from when a capture starts, " +
                    "and no capture is taken at all until you have moved 15 m from the last one.",
                    new AcceptableValueRange<int>(2, 120)));

            CaptureResolution = config.Bind(
                "Map", "Capture resolution", 8192,
                new ConfigDescription(
                    "Longest side, in pixels, of a captured map picture. 8192 is the sharpest the mod " +
                    "will draw and what a map of Customs is worth - a quarter of a metre to the pixel, " +
                    "where a vehicle is 16 pixels across; 4096 halves that and 2048 quarters it, each " +
                    "step making the files four times smaller and the raid's frames cheaper, which is " +
                    "the setting for a weak machine or a capture refused for being too large. None of " +
                    "them ever stretches a map past four pixels per metre - past that there is no more " +
                    "detail in the scene to record, only a bigger file. A capture also works to a memory " +
                    "budget of 256 MiB per floor, and on a big map that budget, not this setting, decides " +
                    "the scale: Interchange comes down to about 3 pixels per metre, and the capture's header " +
                    "line in the log says so whenever the budget has lowered one. What is shared with a host or " +
                    "shipped in the release is downscaled to 2048 whatever this says.",
                    new AcceptableValueList<int>(2048, 4096, 8192)));

            MapPictureSource = config.Bind(
                "Map", "Map pictures come from", PictureSource.PreferDynamicMaps,
                "Which picture the Maps tab draws when there is a choice. DynamicMaps' artwork is drawn " +
                "by hand and covers the vanilla maps; a capture is this mod's own photograph of the map " +
                "from above, taken in raid, and is the only option for a map DynamicMaps does not ship. " +
                "'My captures only' ignores DynamicMaps entirely.");

            MapLabels = config.Bind(
                "Map", "Map labels", LabelMode.All,
                "Which place names are drawn on a CAPTURED picture: everything the capture found (the " +
                "extracts and the map's own zone names, which only appear once you zoom in, so the wide " +
                "view stays clean), the extracts alone, or none. DynamicMaps' own artwork carries its " +
                "author's labels whatever this says.");

            MapMode = config.Bind(
                "Map", "Map view", MapViewMode.Relief,
                "How a captured map is drawn. '3D relief' drapes the captured picture over the ground's " +
                "real heights and stands the buildings on it, and you drag to move, right-drag to turn " +
                "and tilt, and scroll to come closer; 'Flat picture' is the view every earlier version " +
                "had. A map with no relief captured for it draws flat whatever this says, and the Maps " +
                "tab's own 3D toggle is greyed out for it. The two picture settings - 'Mirror map " +
                "artwork' and 'Extra map artwork rotation' - DO NOT APPLY in 3D: the picture is laid onto " +
                "the ground by the captured coordinates it was measured over, so there is nothing left " +
                "for a rotation or a mirror to correct.");

            // Its own marker rather than the palette's version stamp, because the two migrations are
            // unrelated and a config that has had one may not have had the other. Advanced, and out of
            // Entries below, so the Settings tab shows no row for it: it is state, not a preference.
            MapLabelsMigrated = config.Bind(
                "Advanced", "Map labels default migrated", false,
                "Whether the one-time move of 'Map labels' from its old default (extracts only) to its new " +
                "one (all names) has already been offered to this file. Do not edit: the mod sets it once " +
                "and will not touch your choice of labels again afterwards.");

            MigrateMapLabels();

            UploadCaptures = config.Bind(
                "Map", "Share captured maps", true,
                "Offer a map picture you have just captured to the Quest Tracker server mod, so anyone " +
                "else playing on the same host gets it too. A host that does not collect map pictures " +
                "refuses the offer and nothing is sent; turn this off to not offer at all.");

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

            // THROWAWAY, to be deleted with QuestGraph/MeshProbe.cs. Its own section so it sits away
            // from the real settings, and kept out of Entries below so the Settings tab shows nothing.
            // A BARE F10, no modifier: this is a key pressed a handful of times, in a raid and a menu
            // visit set aside for the experiment. Nothing in a raid or the menu uses F10, and the probe
            // changes nothing that outlives the frame if it is hit by mistake.
            ProbeKey = config.Bind(
                // Unbound by default (review F43): a bare F10 fired a GPU readback pass in the menu and left a
                // click-eating overlay up until it was pressed again. A config that already has F10 keeps it.
                "Advanced", "Mesh probe key (throwaway)", KeyboardShortcut.Empty,
                "Debug only, and temporary. It is the diagnostic key of the 3D map experiments: pressed in a " +
                "raid it measures whether the game's meshes can be read back off the GPU and how much of the " +
                "map its colliders cover, and pressed in the menu it lists the loaded shaders, cameras and " +
                "layers and puts a small test view on screen (press again to close it). It writes " +
                "BepInEx/plugins/QuestTree/captures/<map>.meshprobe.txt and captures/menu.meshprobe.txt and " +
                "will be removed again; nothing in the mod depends on it.");

            Entries.AddRange(new ConfigEntryBase[]
            {
                HideUnobtainable, HideCompleted, HideTraderless, MarkStartedOnly, MapArtworkRotation,
                MirrorMapArtwork, ShowMapGuides, DrawEdges, FocusFrontier, CompactLayout, MaxVisibleNodes,
                OpenOnMap, HarvestZones, EdgeOpacity, HoverDimStrength, TallTitles, CollapseChains,
                OverviewBelowZoom, FocusRadius, QuestBadges, DoNextGoal, DoNextMaxRows,
                SidebarWidth, DoNextRows, ShowItemsSection,
                ShowTakeWithYou, CountUnacceptedQuests, ShowTraderColours,
                TraderColours, ShowCredits,
                PinLabels, ColorActive, ColorAvailable, ColorCompleted, ColorLocked, ColorGated, ColorFailed, ColorAccent, Tooltips,
                HoverSounds, RememberLastView, OpenTracker, CaptureMapKey, CampaignKey,
                AutoCapture, AutoCaptureSeconds, CaptureResolution,
                UploadCaptures, MapPictureSource, MapLabels, MapMode
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
            CollapseChains.SettingChanged += Raise;
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

            // The Settings tab PRINTS the capture key and the resolution, so a change made in the
            // F12 menu has to repaint the page or the row keeps naming the old key.
            CaptureMapKey.SettingChanged += Raise;
            CampaignKey.SettingChanged += Raise;
            AutoCapture.SettingChanged += Raise;
            AutoCaptureSeconds.SettingChanged += Raise;
            CaptureResolution.SettingChanged += Raise;
            UploadCaptures.SettingChanged += Raise;

            // All three change what the map DRAWS, so all three have to repaint it. MapMode also has to
            // bump Generation, which it does through Raise: the map's kept viewport carries the
            // generation in its key, and without the bump a flip from the F12 menu would leave the 2D
            // picture on screen with the toggle beside it saying 3D.
            MapPictureSource.SettingChanged += Raise;
            MapLabels.SettingChanged += Raise;
            MapMode.SettingChanged += Raise;
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
            Generation++;

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
