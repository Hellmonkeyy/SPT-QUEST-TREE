using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The Settings page: everything about the menu that is reasonable to change, in two columns
    /// of sections - Tree and Behaviour on the left, Map and Colours on the right - with a reset
    /// link per section. Every control writes straight to a ModSettings entry; ModSettings raises
    /// Changed and the panel re-renders, which is what rebuilds this page with the new value.
    ///
    /// Same primitives as every other view. Columns are two plain containers so the existing
    /// stretch-to-parent controls (toggles, steppers) lay themselves out in half the width with no
    /// change. Dropdowns are built last within their column, because Unity draws siblings in
    /// order and an open list has to cover the rows under it.
    /// </summary>
    internal static class SettingsView
    {
        private const float MaxColumnWidth = 620f;

        /// <summary>The dropdown currently open, by name, or null. Static because the page is
        /// rebuilt on every click.</summary>
        private static string _openDropdown;

        /// <summary>What the last Reload found, shown once under the button by the rebuild it
        /// causes and then forgotten. Without it a reload that found the same file looked
        /// exactly like no click at all.</summary>
        private static string _kappaReloadResult;

        private static readonly (string Name, string Hex)[] ColourPresets =
        {
            ("Green", "#5CE82B"), ("Amber", "#D9A847"), ("Blue", "#75B9DE"), ("Red", "#E7191C"),
            ("White", "#E8E8E4"), ("Grey", "#6B6B66")
        };

        public static float Build(
            RectTransform parent, Vector2 panelSize, Action onShowIntro, Action onKappaListReloaded,
            QuestGraphBuilder graph = null)
        {
            var y = AuxLayout.Padding;

            if (!ModSettings.Ready)
            {
                AuxLayout.AddHeading(parent, ref y, "Settings");
                AuxLayout.AddText(parent, ref y,
                    $"<color=#{GameStyle.ErrorHex}>Settings failed to initialise - check the BepInEx log.</color>", 24f);
                return y + AuxLayout.Padding;
            }

            var columnWidth = Mathf.Min(MaxColumnWidth, (panelSize.x - AuxLayout.Padding * 3f) / 2f);
            var left = MakeColumn(parent, AuxLayout.Padding, columnWidth);
            var right = MakeColumn(parent, AuxLayout.Padding * 2f + columnWidth, columnWidth);

            var leftY = AuxLayout.Padding;
            var rightY = AuxLayout.Padding;
            var deferred = new List<Func<float>>();

            BuildTreeSection(left, ref leftY, columnWidth, deferred);
            AuxLayout.AddSpacer(ref leftY, 18f);
            BuildDoNextSection(left, ref leftY, columnWidth, deferred);
            AuxLayout.AddSpacer(ref leftY, 18f);
            BuildBehaviourSection(left, ref leftY, columnWidth, onShowIntro, onKappaListReloaded);

            BuildMapSection(right, ref rightY, columnWidth, deferred);
            AuxLayout.AddSpacer(ref rightY, 18f);
            BuildColourSection(right, ref rightY, columnWidth);
            AuxLayout.AddSpacer(ref rightY, 18f);
            BuildTraderColourSection(right, ref rightY, columnWidth, graph);

            // The dropdown lists, last, so they draw over whatever sits beneath them.
            var popupBottom = 0f;
            foreach (var build in deferred) popupBottom = Mathf.Max(popupBottom, build());

            left.sizeDelta = new Vector2(columnWidth, leftY);
            right.sizeDelta = new Vector2(columnWidth, rightY);

            return Mathf.Max(leftY, rightY, popupBottom) + AuxLayout.Padding;
        }

        private static RectTransform MakeColumn(RectTransform parent, float x, float width)
        {
            var go = new GameObject("SettingsColumn", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, 0f);
            rect.sizeDelta = new Vector2(width, 0f);
            return rect;
        }

        private static void Header(RectTransform column, ref float y, string title, float width)
        {
            AuxLayout.AddSectionHeader(column, ref y, title, AuxLayout.Padding, width - AuxLayout.Padding * 2f);
        }

        /// <summary>One reset per column, naming exactly the entries the column shows. The
        /// config file's sections do not match these columns, and resetting by section used to
        /// miss some rows (Compact layout, Max visible quests) and reach into the other column.</summary>
        private static void ResetLink(RectTransform column, ref float y, float width, params ConfigEntryBase[] entries)
        {
            y += 4f;
            AuxLayout.AddClickableRow(column, "<color=#FFFFFF60>Reset this section to defaults</color>",
                AuxLayout.Padding, ref y, width - AuxLayout.Padding * 2f, false,
                () => ModSettings.ResetEntries(entries), 20f);
        }

        private static void Toggle(RectTransform column, ref float y, string label, string description, ConfigEntry<bool> entry)
        {
            AuxLayout.AddToggle(column, ref y, label, description, entry.Value, value => entry.Value = value);
        }

        private static void Stepper(RectTransform column, ref float y, string label, ConfigEntry<int> entry, int step, int min, int max, string note = null)
        {
            AuxLayout.AddStepper(column, ref y, label, entry.Value, step,
                value => entry.Value = Mathf.Clamp(value, min, max));

            if (!string.IsNullOrEmpty(note))
                AuxLayout.AddText(column, ref y, $"<color=#FFFFFF80>{note}</color>", 20f, 11);
        }

        // ------------------------------------------------------------------ Tree

        private static void BuildTreeSection(
            RectTransform column, ref float y, float width, List<Func<float>> deferred)
        {
            Header(column, ref y, "Tree", width);

            Toggle(column, ref y, "Compact layout",
                "Smaller boxes packed tighter - far more of the tree on screen, minus the objective line.",
                ModSettings.CompactLayout);
            Toggle(column, ref y, "Two-line titles",
                "Let a box grow a line so \"Gunsmith - Part 3\" shows both parts instead of an ellipsis.",
                ModSettings.TallTitles);
            Toggle(column, ref y, "Show trader colours",
                "A coloured stripe per trader down the left edge of each quest. Status colours are unaffected.",
                ModSettings.ShowTraderColours);
            Toggle(column, ref y, "Draw prerequisite lines",
                "Turning this off is a noticeable speed-up on very dense trader chains.",
                ModSettings.DrawEdges);
            Toggle(column, ref y, "Focus on what you can work on",
                "Show only quests in progress or available, plus what they need and unlock. Shortcut: X on the tree.",
                ModSettings.FocusFrontier);
            Toggle(column, ref y, "Hide unobtainable quests",
                "Other faction, seasonal event, and other-edition quests.",
                ModSettings.HideUnobtainable);
            Toggle(column, ref y, "Hide completed quests",
                "Show only what is still left to do.",
                ModSettings.HideCompleted);
            Toggle(column, ref y, "Hide quests with no trader",
                "Usually scripted or leftover entries rather than anything you can pick up.",
                ModSettings.HideTraderless);

            AuxLayout.AddSpacer(ref y, 6f);
            Stepper(column, ref y, "Prerequisite line opacity %", ModSettings.EdgeOpacity, 2, 0, 60,
                "How visible the lines are at rest. Lines into a quest you can act on are drawn stronger regardless.");
            Stepper(column, ref y, "Hover dimming strength %", ModSettings.HoverDimStrength, 10, 0, 200,
                "How far the rest of the tree fades around a hovered quest. 0 is off.");
            Stepper(column, ref y, "Trader overview below zoom %", ModSettings.OverviewBelowZoom, 5, 0, 40,
                "Zoomed out past this, the quests are replaced by one readable card per trader. 0 is off.");
            Stepper(column, ref y, "Focus reach", ModSettings.FocusRadius, 1, 1, 10,
                "How many quests out from something you can work on the Focus button reaches. Further than this is dropped.");
            Stepper(column, ref y, "Max visible quests", ModSettings.MaxVisibleNodes, 100, 100, 2000,
                "Ceiling on how many quest boxes exist at once. Only reachable when zoomed right out.");

            AuxLayout.AddSpacer(ref y, 6f);
            Dropdown(column, ref y, width, deferred, "Quest badges",
                new[] { "Kappa and Collector", "Kappa only", "Collector only" },
                (int)ModSettings.QuestBadges.Value,
                index => ModSettings.QuestBadges.Value = (ModSettings.BadgeMode)index);

            ResetLink(column, ref y, width,
                ModSettings.CompactLayout, ModSettings.TallTitles,
                ModSettings.DrawEdges, ModSettings.FocusFrontier, ModSettings.HideUnobtainable,
                ModSettings.HideCompleted, ModSettings.HideTraderless, ModSettings.EdgeOpacity,
                ModSettings.HoverDimStrength, ModSettings.OverviewBelowZoom, ModSettings.FocusRadius,
                ModSettings.MaxVisibleNodes, ModSettings.QuestBadges);
        }

        // ------------------------------------------------------------------ Behaviour

        /// <summary>The Do next tab's ranking.
        ///
        /// The goal lives here as well as on the tab itself, because it is a setting in the sense
        /// that it persists and belongs in the config file - but it is also the one setting a player
        /// changes mid-session while looking at the list it reorders, which is why the tab carries
        /// its own copy of the control rather than sending anyone here.</summary>
        private static void BuildDoNextSection(
            RectTransform column, ref float y, float width, List<Func<float>> deferred)
        {
            Header(column, ref y, "Do next", width);

            AuxLayout.AddText(column, ref y,
                "<color=#FFFFFF80>What the ranking optimises for. The same quest is not equally worth doing "
                + "under all of these - one paying 200k experience and opening nothing tops Fast levelling "
                + "and sits near the bottom of Kappa path.</color>", 46f, 11);

            Dropdown(column, ref y, width, deferred, "Ranking goal", GoalLabels,
                (int)ModSettings.DoNextGoal.Value,
                index => ModSettings.DoNextGoal.Value = (RankGoal)index);

            Stepper(column, ref y, "Rows listed", ModSettings.DoNextMaxRows, 10, 10, 200,
                "How many quests the tab lists before it stops and counts the rest.");

            ResetLink(column, ref y, width, ModSettings.DoNextGoal, ModSettings.DoNextMaxRows);
        }

        /// <summary>In RankGoal order, so the selected index is the enum value.</summary>
        private static readonly string[] GoalLabels =
        {
            "Balanced",
            "Kappa path",
            "Fast levelling",
            "Trader unlocks",
            "Item hoarding"
        };

        private static void BuildBehaviourSection(
            RectTransform column, ref float y, float width, Action onShowIntro, Action onKappaListReloaded)
        {
            Header(column, ref y, "Behaviour", width);

            Toggle(column, ref y, "Open on the map",
                "Start on the Maps view; the tree is one button away either way.",
                ModSettings.OpenOnMap);
            Toggle(column, ref y, "Remember last view",
                "Open on whichever view the tracker was closed on instead.",
                ModSettings.RememberLastView);
            Toggle(column, ref y, "Tooltips",
                "The game's tooltip over quest boxes and toolbar buttons.",
                ModSettings.Tooltips);
            Toggle(column, ref y, "Hover sounds",
                "The game's hover sound over buttons and rows.",
                ModSettings.HoverSounds);
            Toggle(column, ref y, "Harvest quest zones in raid",
                "A few seconds into a raid, report the map's quest zones to the server half so objectives get pins. One raid per map is enough.",
                ModSettings.HarvestZones);

            AuxLayout.AddSpacer(ref y, 6f);
            AuxLayout.AddButton(column, ref y, "Show the controls hint", onShowIntro);

            AuxLayout.AddSpacer(ref y, 6f);
            AuxLayout.AddText(column, ref y,
                KappaQuests.Count == 0
                    ? "<color=#FFFFFF80>kappa-quests.json is empty. Add quest names to it to override the Kappa list derived from Collector.</color>"
                    : $"<color=#FFFFFF80>{KappaQuests.Count} quest names loaded from kappa-quests.json.</color>",
                32f, 11);
            AuxLayout.AddButton(column, ref y, "Reload kappa-quests.json", () =>
            {
                KappaQuests.Reload();
                _kappaReloadResult = KappaQuests.Count == 0
                    ? "Reloaded - the file is empty, so the server's Kappa list is in use."
                    : $"Reloaded - {KappaQuests.Count} quest names, badged by name.";
                onKappaListReloaded();
            });

            if (_kappaReloadResult != null)
            {
                AuxLayout.AddText(column, ref y, $"<color=#{GameStyle.WarningHex}>{_kappaReloadResult}</color>", 20f, 11);
                _kappaReloadResult = null;
            }

            ResetLink(column, ref y, width,
                ModSettings.OpenOnMap, ModSettings.RememberLastView, ModSettings.Tooltips,
                ModSettings.HoverSounds, ModSettings.HarvestZones);
        }

        // ------------------------------------------------------------------ Map

        private static void BuildMapSection(RectTransform column, ref float y, float width, List<Func<float>> deferred)
        {
            Header(column, ref y, "Map", width);

            Toggle(column, ref y, "Accepted quests only",
                "Pin and list only quests you have accepted, not every one on the map.",
                ModSettings.MarkStartedOnly);
            Toggle(column, ref y, "Show items to find",
                "The sidebar section listing quest items that spawn on the map, with what you already have.",
                ModSettings.ShowItemsSection);
            Toggle(column, ref y, "Show take with you",
                "The sidebar section listing what you must carry INTO the map, and whether it is on you.",
                ModSettings.ShowTakeWithYou);
            Toggle(column, ref y, "Count quests you have not accepted",
                "Count quests you could accept but have not when working out what to take into a raid.",
                ModSettings.CountUnacceptedQuests);
            Toggle(column, ref y, "Show map credits",
                "The map and pin-icon attributions at the bottom of the sidebar.",
                ModSettings.ShowCredits);
            Toggle(column, ref y, "Mirror map artwork",
                "Mirrors the map picture left-to-right. Markers are not mirrored.",
                ModSettings.MirrorMapArtwork);
            Toggle(column, ref y, "Show map alignment guides",
                "Diagnostic: outline the area the map's coordinates cover, and mark its origin.",
                ModSettings.ShowMapGuides);

            AuxLayout.AddSpacer(ref y, 6f);
            Stepper(column, ref y, "Do next here rows", ModSettings.DoNextRows, 1, 0, 20,
                "How many quests the map sidebar's 'Do next here' section lists. 0 hides it. The Do next view itself is not limited by this.");
            Stepper(column, ref y, "Extra map artwork rotation", ModSettings.MapArtworkRotation, 90, -270, 270,
                "Added to the rotation each map already declares. 0 is right for every shipped map.");

            AuxLayout.AddSpacer(ref y, 6f);
            Dropdown(column, ref y, width, deferred, "Pin labels",
                new[] { "Hover only", "In progress and available", "All pins" },
                (int)ModSettings.PinLabels.Value,
                index => ModSettings.PinLabels.Value = (ModSettings.PinLabelMode)index);

            Dropdown(column, ref y, width, deferred, "Sidebar width",
                new[] { "Narrow", "Normal", "Wide" },
                ModSettings.SidebarWidth.Value <= 380 ? 0 : ModSettings.SidebarWidth.Value >= 520 ? 2 : 1,
                index => ModSettings.SidebarWidth.Value = index == 0 ? 380 : index == 2 ? 520 : 440);

            ResetLink(column, ref y, width,
                ModSettings.MarkStartedOnly, ModSettings.ShowItemsSection, ModSettings.ShowTakeWithYou,
                ModSettings.CountUnacceptedQuests, ModSettings.ShowCredits,
                ModSettings.MirrorMapArtwork, ModSettings.ShowMapGuides, ModSettings.DoNextRows,
                ModSettings.MapArtworkRotation, ModSettings.PinLabels, ModSettings.SidebarWidth);
        }

        /// <summary>A labelled dropdown.
        ///
        /// The header is built here, in place, with the rows around it. Only the OPEN list is
        /// deferred, and only one dropdown is ever open, so the deferred pass builds exactly one
        /// overlay and builds it after every header on the page.
        ///
        /// Deferring the whole control instead is what broke this page in 1.8.4: the deferred pass
        /// ran header-plus-list, header-plus-list, so the second dropdown's header landed on top of
        /// the first one's open list. See AuxLayout.AddDropdownHeader.</summary>
        private static void Dropdown(
            RectTransform column, ref float y, float width, List<Func<float>> deferred, string label,
            IReadOnlyList<string> options, int selected, Action<int> onSelect)
        {
            AuxLayout.AddLabelAt(column, $"<color=#FFFFFFB0>{label}</color>", AuxLayout.Padding, ref y, 18f, 12,
                width - AuxLayout.Padding * 2f);

            var top = y;
            var open = _openDropdown == label;
            var dropdownWidth = Mathf.Min(300f, width - AuxLayout.Padding * 2f);

            AuxLayout.AddDropdownHeader(
                column, top, options, selected, open,
                toggleOpen: () =>
                {
                    _openDropdown = open ? null : label;
                    ModSettings.RequestRepaint();
                },
                width: dropdownWidth,
                x: AuxLayout.Padding);

            if (open)
            {
                deferred.Add(() => AuxLayout.AddDropdownList(
                    column, top, options, selected,
                    onSelect: index =>
                    {
                        _openDropdown = null;
                        onSelect(index);
                    },
                    width: dropdownWidth,
                    x: AuxLayout.Padding));
            }

            y += AuxLayout.DropdownHeight + 10f;
        }

        // ------------------------------------------------------------------ Colours

        private static void BuildColourSection(RectTransform column, ref float y, float width)
        {
            Header(column, ref y, "Colours", width);

            AuxLayout.AddText(column, ref y,
                "<color=#FFFFFF80>Presets here; any hex colour in the F12 configuration menu.</color>", 20f, 11);
            AuxLayout.AddSpacer(ref y, 4f);

            ColourRow(column, ref y, width, "In progress", ModSettings.ColorActive, QuestNodeView.ColorFor(ENodeStatus.Active));
            ColourRow(column, ref y, width, "Available", ModSettings.ColorAvailable, QuestNodeView.ColorFor(ENodeStatus.Available));
            ColourRow(column, ref y, width, "Completed", ModSettings.ColorCompleted, QuestNodeView.ColorFor(ENodeStatus.Completed));
            ColourRow(column, ref y, width, "Level gated", ModSettings.ColorGated, QuestNodeView.ColorFor(ENodeStatus.Gated));
            ColourRow(column, ref y, width, "Locked", ModSettings.ColorLocked, QuestNodeView.ColorFor(ENodeStatus.Locked));
            ColourRow(column, ref y, width, "Accent", ModSettings.ColorAccent, GameStyle.AccentColor);

            // The palette rotated in 1.10 so that in-progress and completed stopped sharing a hue.
            // Anyone who preferred the old set should not have to reconstruct four hex codes by
            // hand, and "reset to defaults" below now resets to the NEW defaults - which is the
            // opposite of what they would want.
            y += 4f;
            AuxLayout.AddClickableRow(column, "<color=#FFFFFF60>Restore the pre-1.10 colours</color>",
                AuxLayout.Padding, ref y, width - AuxLayout.Padding * 2f, false,
                ModSettings.RestoreLegacyColours, 20f);

            ResetLink(column, ref y, width,
                ModSettings.ColorActive, ModSettings.ColorAvailable, ModSettings.ColorCompleted,
                ModSettings.ColorLocked, ModSettings.ColorGated, ModSettings.ColorAccent);
        }

        /// <summary>One colour: a swatch of the current value, its name, and the preset chips.</summary>
        /// <summary>A colour row per trader, built from the traders the graph actually has.
        ///
        /// Generated rather than listed, which is the whole point: this install runs several trader
        /// mods and the next one is not knowable at build time, so hardcoding the vanilla eight
        /// would leave exactly the traders somebody most wants to recolour with no way to do it.
        ///
        /// Each row writes into the single packed TraderColours setting through
        /// ModSettings.SetTraderColour, so the storage stays one config entry while the UI stays one
        /// row per trader.</summary>
        private static void BuildTraderColourSection(
            RectTransform column, ref float y, float width, QuestGraphBuilder graph)
        {
            if (graph?.TraderNames == null || graph.TraderNames.Count == 0) return;

            Header(column, ref y, "Trader colours", width);

            AuxLayout.AddText(column, ref y,
                "<color=#FFFFFF80>The stripe on each quest. Traders from mods are listed here too.</color>",
                20f, 11);
            AuxLayout.AddSpacer(ref y, 4f);

            // By display name, so the list reads the way the tabs do rather than by raw id.
            var traders = new List<KeyValuePair<string, string>>(graph.TraderNames);
            traders.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase));

            var ids = new List<string>();

            foreach (var trader in traders)
            {
                if (string.IsNullOrEmpty(trader.Key)) continue;

                ids.Add(trader.Key);
                var id = trader.Key;

                TraderColourRow(
                    column, ref y, width,
                    string.IsNullOrEmpty(trader.Value) ? id : trader.Value,
                    TraderPalette.For(id),
                    hex => ModSettings.SetTraderColour(id, hex));
            }

            // Clears every override at once, rather than needing each trader set back by hand.
            var resetY = y;
            AuxLayout.AddClickableRow(column, "<color=#FFFFFF80>Reset trader colours</color>",
                AuxLayout.Padding, ref resetY, width - AuxLayout.Padding * 2f, false,
                () =>
                {
                    foreach (var id in ids) ModSettings.SetTraderColour(id, null);
                    TraderPalette.Forget();
                }, 20f);

            y = resetY;
        }

        /// <summary>As ColourRow, but writing through a callback instead of owning a ConfigEntry -
        /// traders share one packed setting rather than having one each.</summary>
        private static void TraderColourRow(
            RectTransform column, ref float y, float width, string label, Color current, Action<string> onPick)
        {
            const float rowHeight = 24f;
            var x = AuxLayout.Padding;

            var swatchGo = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
            var swatch = (RectTransform)swatchGo.transform;
            swatch.SetParent(column, worldPositionStays: false);
            swatch.anchorMin = swatch.anchorMax = new Vector2(0f, 1f);
            swatch.pivot = new Vector2(0f, 1f);
            swatch.anchoredPosition = new Vector2(x, -(y + 3f));
            swatch.sizeDelta = new Vector2(18f, 18f);

            var swatchImage = swatchGo.GetComponent<Image>();
            swatchImage.color = current;
            swatchImage.raycastTarget = false;
            GameStyle.ApplyPanel(swatchImage);
            x += 26f;

            var labelY = y;
            AuxLayout.AddLabelAt(column, label, x, ref labelY, rowHeight, 12, 110f);
            x += 116f;

            foreach (var (name, hex) in ColourPresets)
            {
                var chipY = y + 2f;
                var chipWidth = 40f;
                var preset = hex;

                var row = AuxLayout.AddClickableRow(column, "", x, ref chipY, chipWidth, false,
                    () => onPick(preset), 20f);

                var background = row.GetComponent<Image>();
                if (background != null && ColorUtility.TryParseHtmlString(hex, out var colour))
                    background.color = new Color(colour.r, colour.g, colour.b, 0.22f);

                var text = row.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    text.text = name;
                    text.fontSize = 10;
                    text.alignment = TextAlignmentOptions.Center;
                    if (ColorUtility.TryParseHtmlString(hex, out var ink)) text.color = ink;

                    chipWidth = GameStyle.MeasureWidth(text, name) + 16f;
                    row.sizeDelta = new Vector2(chipWidth, 20f);
                }

                x += chipWidth + 4f;
            }

            y += rowHeight;
        }

        private static void ColourRow(
            RectTransform column, ref float y, float width, string label, ConfigEntry<string> entry, Color current)
        {
            const float rowHeight = 24f;
            var x = AuxLayout.Padding;

            var swatchGo = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
            var swatch = (RectTransform)swatchGo.transform;
            swatch.SetParent(column, worldPositionStays: false);
            swatch.anchorMin = swatch.anchorMax = new Vector2(0f, 1f);
            swatch.pivot = new Vector2(0f, 1f);
            swatch.anchoredPosition = new Vector2(x, -(y + 3f));
            swatch.sizeDelta = new Vector2(18f, 18f);
            var swatchImage = swatchGo.GetComponent<Image>();
            swatchImage.color = current;
            swatchImage.raycastTarget = false;
            GameStyle.ApplyPanel(swatchImage);
            x += 26f;

            var labelY = y;
            AuxLayout.AddLabelAt(column, label, x, ref labelY, rowHeight, 12, 110f);
            x += 116f;

            foreach (var (name, hex) in ColourPresets)
            {
                var chipY = y + 2f;
                var chipWidth = 40f; // provisional; sized to the label below
                var preset = hex;

                var row = AuxLayout.AddClickableRow(column, "", x, ref chipY, chipWidth, false,
                    () => entry.Value = preset, 20f);

                // The chip wears its own colour, so the row is a palette rather than six words.
                var background = row.GetComponent<Image>();
                if (background != null && ColorUtility.TryParseHtmlString(hex, out var colour))
                    background.color = new Color(colour.r, colour.g, colour.b, 0.22f);

                var text = row.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    text.text = name;
                    text.fontSize = 10;
                    text.alignment = TextAlignmentOptions.Center;
                    if (ColorUtility.TryParseHtmlString(hex, out var ink)) text.color = ink;

                    chipWidth = GameStyle.MeasureWidth(text, name) + 16f;
                    row.sizeDelta = new Vector2(chipWidth, 20f);
                }

                x += chipWidth + 4f;
            }

            y += rowHeight + 2f;
        }
    }
}
