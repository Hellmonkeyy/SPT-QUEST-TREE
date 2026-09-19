using System.Collections.Generic;
using System;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The bar across the top of the Quest Tree: the search box, the My quests / Fit / Focus /
    /// Chains / ? buttons, the status legend, the "showing N of M" notice, and at the right the
    /// view buttons (Tree, Maps, Do next, Items, Kappa, Settings) and Close.
    ///
    /// A plain class, not a MonoBehaviour: it builds GameObjects and holds references to the few
    /// controls that are read back later, but it has nothing of its own to hook into Unity's
    /// lifecycle for. The panel drives it - it calls IsSearchFocused() each frame to decide whether
    /// a keypress is a shortcut or typing, and MatchesSearch/UpdateRenderNotice from the render
    /// path - but those are cheap reads, not work this class needs an Update of its own to do.
    /// It owns the search text because the search box is what produces it - the graph
    /// asks this class whether a node matches (<see cref="MatchesSearch"/>) rather than the string
    /// being passed around.
    /// </summary>
    internal sealed class QuestToolbar
    {
        /// <summary>The bar's own height, which the panel also needs: the tab row hangs directly
        /// below it and the graph/aux surfaces start below both.</summary>
        public const float Height = 36f;

        /// <summary>Shared by the Close button and by the Settings button that sits beside it.</summary>
        private const float CloseButtonWidth = 90f;

        /// <summary>The band a view button's width is clamped into. The minimum is also what every
        /// view button drops to when the bar does not fit.</summary>
        private const float MinViewButtonWidth = 70f;
        private const float MaxViewButtonWidth = 110f;

        /// <summary>Between the last view button and the notice, and between the legend and the
        /// notice: the same gap either side, so the notice reads as sitting between the two.</summary>
        private const float NoticeGap = 8f;

        /// <summary>The legend's font is a point smaller than the buttons' so it reads as a key,
        /// not a row of more buttons. Named because the fit arithmetic must use the same size.</summary>
        private const float LegendFontSize = 11f;

        /// <summary>The word the six chips collapse into when the bar does not fit; the glyphs
        /// follow it at build time.</summary>
        private const string CollapsedLegendLabel = "Legend";

        /// <summary>The shortest count the notice shows in full. The wide layout only counts as
        /// fitting when the notice keeps this much: a bar that fits its buttons but ellipsises
        /// the one line that says how many quests you are looking at has not fit.</summary>
        private static readonly float MinNoticeWidth = GameStyle.EstimateWidth("0,000 of 0,000 shown", 12f);

        /// <summary>Every status the legend names. Ordered the way the tree is read - what you are
        /// doing, what you could take, what is done, what is behind a wall, what is behind a quest.
        /// Hardcoded and therefore easy to forget: a status left out here simply never appears in
        /// the legend or its tooltip, with nothing to catch it.</summary>
        private static readonly ENodeStatus[] LegendStatuses =
        {
            ENodeStatus.Active, ENodeStatus.Available, ENodeStatus.Completed,
            ENodeStatus.Gated, ENodeStatus.Locked, ENodeStatus.Failed
        };

        private QuestGraphBuilder _graph;
        private TMP_InputField _searchField;
        private TMP_Text _renderNotice;
        private Image _focusBackground;
        private Image _chainsBackground;

        /// <summary>Everything that only means something over the quest graph - search, framing,
        /// Focus, the legend - hidden while a whole-screen view (map, lists, settings) is up.</summary>
        private readonly List<GameObject> _treeOnly = new();
        /// <summary>Backgrounds of the non-graph view buttons, keyed by the tab id they select, so
        /// the selected one can be lit the same way a trader tab is.</summary>
        private readonly Dictionary<string, Image> _viewButtonBackgrounds = new();

        private string _searchFilter = "";
        private string _searchNeedle = "";

        /// <summary>Toggles the Settings tab on and off - supplied by the panel, which owns tab
        /// selection.</summary>
        private Action<string> _onViewSelected;

        /// <summary>
        /// Positions every child by hand (an x-cursor, incremented as each is placed) rather than a
        /// HorizontalLayoutGroup. Two rounds of trying to tame that component's default child-control
        /// behavior in this runtime-built hierarchy still didn't produce a clean row (see git/plan
        /// history), so this switches to the same fully-manual positioning already used successfully
        /// for the graph nodes themselves - fully deterministic, nothing implicit left to debug.
        /// </summary>
        public void Build(
            RectTransform root,
            QuestGraphBuilder graph,
            Func<bool> isAuxTabSelected,
            Action onSearchChanged,
            Action onSearchSubmitted,
            Action frameMyQuests,
            Action frameContent,
            IReadOnlyList<(string TabId, string Label)> viewButtons,
            Action<string> onViewSelected,
            Action closeTree,
            Action showIntro)
        {
            _graph = graph;
            _onViewSelected = onViewSelected;

            var toolbarGo = new GameObject("Toolbar", typeof(RectTransform), typeof(Image));
            var toolbar = (RectTransform)toolbarGo.transform;
            toolbar.SetParent(root, worldPositionStays: false);
            toolbar.anchorMin = new Vector2(0f, 1f);
            toolbar.anchorMax = new Vector2(1f, 1f);
            toolbar.pivot = new Vector2(0f, 1f);
            toolbar.sizeDelta = new Vector2(0f, Height);
            toolbarGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);
            GameStyle.ApplyPanel(toolbarGo.GetComponent<Image>());

            const float padding = 8f;
            const float itemHeight = 28f;
            var itemY = -(Height - itemHeight) / 2f; // vertically centered within the bar

            var searchGo = new GameObject("Search", typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
            _treeOnly.Add(searchGo);
            var searchRect = (RectTransform)searchGo.transform;
            searchRect.SetParent(toolbar, worldPositionStays: false);
            searchRect.anchorMin = searchRect.anchorMax = new Vector2(0f, 1f);
            searchRect.pivot = new Vector2(0f, 1f);
            searchRect.anchoredPosition = new Vector2(padding, itemY);
            const float searchWidth = 220f;
            searchRect.sizeDelta = new Vector2(searchWidth, itemHeight);
            var searchBackground = searchGo.GetComponent<Image>();
            searchBackground.color = new Color(1f, 1f, 1f, 0.08f);
            GameStyle.ApplyPanel(searchBackground);

            // The text and placeholder sit inside a masked viewport, not directly in the box:
            // TMP_InputField clips and scrolls its text only through textViewport, and without
            // one a long search ran straight out of the box and across the toolbar buttons.
            var viewportGo = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            var viewportRect = (RectTransform)viewportGo.transform;
            viewportRect.SetParent(searchRect, worldPositionStays: false);
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(8f, 2f);
            viewportRect.offsetMax = new Vector2(-8f, -2f);

            var searchTextGo = new GameObject("Text", typeof(RectTransform));
            var searchTextRect = (RectTransform)searchTextGo.transform;
            searchTextRect.SetParent(viewportRect, worldPositionStays: false);
            searchTextRect.anchorMin = Vector2.zero;
            searchTextRect.anchorMax = Vector2.one;
            searchTextRect.offsetMin = Vector2.zero;
            searchTextRect.offsetMax = Vector2.zero;
            var searchText = searchTextGo.AddComponent<TextMeshProUGUI>();
            searchText.fontSize = 12;
            searchText.color = Color.white;
            GameStyle.Apply(searchText);

            // Without a placeholder the box reads as an empty grey rectangle - there is nothing on
            // screen otherwise to say it is a search field at all.
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            var placeholderRect = (RectTransform)placeholderGo.transform;
            placeholderRect.SetParent(viewportRect, worldPositionStays: false);
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;

            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Search quests or traders  ( / )";
            placeholder.fontSize = 12;
            placeholder.color = GameStyle.DimTextColor;
            GameStyle.Apply(placeholder);

            _searchField = searchGo.GetComponent<TMP_InputField>();
            _searchField.textViewport = viewportRect;
            _searchField.textComponent = searchText;
            _searchField.placeholder = placeholder;
            _searchField.onValueChanged.AddListener(value =>
            {
                _searchFilter = value ?? "";

                // Lowercased here, once per keystroke, rather than once per node per keystroke.
                // The haystack on each node is already lowercase, so the comparison can be ordinal.
                _searchNeedle = _searchFilter.Trim().ToLowerInvariant();

                // The Kappa and Settings tabs ignore the search box entirely, and rebuilding them
                // here used to re-fetch the Kappa payload on EVERY keystroke - a blocking request
                // that walks the whole profile inventory server-side. Only the graph cares.
                if (isAuxTabSelected()) return;

                // A visibility pass over the boxes already built, not a re-render: the search
                // lights matches in place and dims the rest (QuestGraphView.RefreshSearch), so the
                // layout - and the edges you were tracing - stays put while you type. It used to
                // re-render and drop non-matches out of the layout entirely; this comment outlived
                // that by a release.
                onSearchChanged();
            });

            // Enter goes to the first match - typing a name and pressing Enter is what a search
            // box is for, and until now Enter did nothing at all.
            _searchField.onSubmit.AddListener(_ =>
            {
                if (isAuxTabSelected() || _searchFilter.Trim().Length == 0) return;
                onSearchSubmitted();
            });

            // Stamped so the panel can tell an Escape that TMP has already used to leave the box
            // from one meant for the panel - see QuestTreePanel.Update.
            _searchField.onEndEdit.AddListener(_ => _searchBlurFrame = Time.frameCount);

            // Placed between the search box and the notice, on the same manual x-cursor.
            var navX = padding + searchWidth + 10f;
            navX += BuildToolbarAction(toolbar, "My quests (M)", navX, itemY, itemHeight, 110f, frameMyQuests,
                "Jump to the quests you can work on now", treeOnly: true);
            navX += BuildToolbarAction(toolbar, "Fit (F)", navX, itemY, itemHeight, 70f, frameContent,
                "Fit the whole tab on screen", treeOnly: true);

            // Focus is a toggle, so its background says whether it is on - the tree itself looking
            // sparse is not enough of a clue.
            navX += BuildToolbarAction(toolbar, "Focus (X)", navX, itemY, itemHeight, 90f, ToggleFocus,
                "Show only what you can work on and the quests within reach of it - Settings sets the reach",
                out _focusBackground, treeOnly: true);
            RefreshFocusState();

            // Chains is the same shape: a persisted setting, lit while on.
            navX += BuildToolbarAction(toolbar, "Chains", navX, itemY, itemHeight, 70f, ToggleChains,
                "Draw each single-file run of one trader's quests as one box - click a box to open it, " +
                "the - mark on its first quest closes it. Off and on again closes every open run",
                out _chainsBackground, treeOnly: true);
            RefreshChainsState();

            // The controls hint is shown once and then never again on its own, which would make it
            // useless to anyone who dismissed it before they knew what it was for. This is how you
            // get it back.
            navX += BuildToolbarAction(toolbar, "?", navX, itemY, itemHeight, 30f, showIntro, "Controls");

            // Everything from here on is placed from the left cursor or from the right edge, and
            // the two meet in the middle: six named legend chips plus the view cluster need more
            // than many common window widths have, and where the width runs out they overlap. So
            // the bar is asked whether its fixed content fits and, if not, the legend collapses to
            // one chip and the view buttons drop to their minimum. Decided once, here: the shell
            // is built on the first Show and never rebuilt, so this is the only look at the width.
            const float legendGap = 6f;
            var rightOffset = padding + CloseButtonWidth + 6f;
            var compact = !FitsWide(navX + legendGap, rightOffset, viewButtons, AvailableWidth(toolbar, root));

            // The legend lives here, in the bar, as the same bar-and-name the nodes wear - it used
            // to be a box in the corner of the graph, over whatever was drawn there.
            navX += compact
                ? BuildCollapsedLegend(toolbar, navX + legendGap, itemY, itemHeight)
                : BuildLegendChips(toolbar, navX + legendGap, itemY, itemHeight);

            BuildCloseButton(toolbar, itemY, itemHeight, padding, closeTree);

            // Maps / Items / Kappa / Settings live together at the right, next to Close: they are
            // whole views rather than a slice of the quest graph, so grouping them apart from the
            // trader tabs says which is which. Laid out right-to-left from Close so the cluster
            // stays put whatever the window width.
            foreach (var (tabId, label) in viewButtons)
            {
                rightOffset += BuildViewButton(toolbar, tabId, label, rightOffset, itemY, itemHeight, compact);
            }

            // Stretched between the nav buttons and that cluster rather than given a fixed width,
            // so it cannot collide with them on a narrow window.
            BuildRenderNotice(toolbar, itemY, itemHeight, navX + NoticeGap, rightOffset + NoticeGap);
        }

        /// <summary>The width the bar has to lay out in. Its own rect, which stretches with the
        /// panel; the panel's rect when its own has not been computed; the screen when neither
        /// has. The same fallback chain the panel uses for the aux views, and for the same reason -
        /// on the very first open the hierarchy may not have been laid out yet.</summary>
        private static float AvailableWidth(RectTransform toolbar, RectTransform root)
        {
            var width = toolbar.rect.width;
            if (width > 100f) return width;

            width = root.rect.width;
            return width > 100f ? width : Screen.width;
        }

        /// <summary>Whether the six named chips and the naturally-sized view buttons both fit.
        ///
        /// The same sums the layout makes, only with the character estimate in place of the TMP
        /// measurement: nothing has been created to measure yet, and the measurement is banded
        /// around this estimate anyway (see GameStyle.MeasureWidth), so this is the width the
        /// layout is guaranteed to stay near. The notice stretches to whatever is left, so it is
        /// counted at the least it can say something with (MinNoticeWidth), not at full width.</summary>
        private static bool FitsWide(
            float legendX, float rightOffset, IReadOnlyList<(string TabId, string Label)> viewButtons, float available)
        {
            var leftNeed = legendX;
            foreach (var status in LegendStatuses)
                leftNeed += LegendChipWidth(GameStyle.EstimateWidth(QuestNodeView.NameFor(status), LegendFontSize));

            var rightNeed = rightOffset;
            foreach (var (_, label) in viewButtons)
                rightNeed += ViewButtonWidth(GameStyle.EstimateWidth(label, 12f), compact: false) + 1f;

            return leftNeed + NoticeGap + MinNoticeWidth + NoticeGap + rightNeed <= available;
        }

        /// <summary>A legend chip's footprint for a label of the given width: the bar, its gap,
        /// the padded label, and the gap to the next chip. One formula for the layout and the fit
        /// arithmetic, so they cannot disagree.</summary>
        private static float LegendChipWidth(float labelWidth) =>
            LayoutMetrics.StatusBarWidth + 4f + (labelWidth + 8f) + 10f;

        /// <summary>A view button's width for a label of the given width: sized to the label
        /// inside the clamp band when the bar fits, the band's minimum when it does not.</summary>
        private static float ViewButtonWidth(float labelWidth, bool compact) =>
            compact ? MinViewButtonWidth : Mathf.Clamp(labelWidth + 24f, MinViewButtonWidth, MaxViewButtonWidth);

        /// <summary>One of the non-graph views. Returns the width consumed so the caller can keep
        /// walking leftwards from Close.</summary>
        private float BuildViewButton(
            RectTransform toolbar, string tabId, string label, float rightOffset, float itemY, float itemHeight, bool compact)
        {
            var buttonRect = GameStyle.CreateButton(toolbar, label, () => _onViewSelected(tabId));
            var width = ViewButtonWidth(GameStyle.MeasureWidth(buttonRect.GetComponentInChildren<TMP_Text>(), label), compact);
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.pivot = new Vector2(1f, 1f);
            buttonRect.anchoredPosition = new Vector2(-rightOffset, itemY);
            buttonRect.sizeDelta = new Vector2(width, itemHeight);

            var background = buttonRect.GetComponent<Image>();
            if (background != null) _viewButtonBackgrounds[tabId] = background;

            // Adjacent, not spaced: the views are one control with one selection, and a row of
            // touching segments says that where six separate buttons did not.
            return width + 1f;
        }

        /// <summary>One toolbar action button. Returns the width it consumed so the caller can keep
        /// advancing its cursor - zoom and framing are otherwise invisible features you have to
        /// already know the wheel and keyboard do.</summary>
        private float BuildToolbarAction(
            RectTransform toolbar, string label, float x, float itemY, float itemHeight, float width, Action onClick,
            string tooltip = null, bool treeOnly = false)
        {
            return BuildToolbarAction(toolbar, label, x, itemY, itemHeight, width, onClick, tooltip, out _, treeOnly);
        }

        private float BuildToolbarAction(
            RectTransform toolbar, string label, float x, float itemY, float itemHeight, float width, Action onClick,
            string tooltip, out Image background, bool treeOnly = false)
        {
            var rect = GameStyle.CreateButton(toolbar, label, onClick);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, itemY);
            rect.sizeDelta = new Vector2(width, itemHeight);
            background = rect.GetComponent<Image>();

            if (!string.IsNullOrEmpty(tooltip)) GameStyle.AddTooltip(rect.gameObject, tooltip);
            if (treeOnly) _treeOnly.Add(rect.gameObject);

            return width + 6f;
        }

        /// <summary>Shows or hides the graph-only controls. The panel calls this as views change.</summary>
        public void SetTreeControlsVisible(bool visible)
        {
            foreach (var go in _treeOnly)
                if (go != null) go.SetActive(visible);
        }

        /// <summary>The Focus toggle. The setting is the state; flipping it raises
        /// ModSettings.Changed, which is what re-renders the tree.</summary>
        public void ToggleFocus()
        {
            if (!ModSettings.Ready) return;
            ModSettings.FocusFrontier.Value = !ModSettings.FocusFrontier.Value;
            RefreshFocusState();
        }

        private void RefreshFocusState()
        {
            if (_focusBackground == null) return;

            var on = ModSettings.Ready && ModSettings.FocusFrontier.Value;
            _focusBackground.color = on
                ? new Color(GameStyle.AccentColor.r, GameStyle.AccentColor.g, GameStyle.AccentColor.b, 0.35f)
                : GameStyle.PanelColor;
        }

        /// <summary>The Chains toggle, wired like Focus: the setting is the state, and the change
        /// event re-renders.</summary>
        public void ToggleChains()
        {
            if (!ModSettings.Ready) return;
            ModSettings.CollapseChains.Value = !ModSettings.CollapseChains.Value;
            RefreshChainsState();
        }

        private void RefreshChainsState()
        {
            if (_chainsBackground == null) return;

            var on = ModSettings.Ready && ModSettings.CollapseChains.Value;
            _chainsBackground.color = on
                ? new Color(GameStyle.AccentColor.r, GameStyle.AccentColor.g, GameStyle.AccentColor.b, 0.35f)
                : GameStyle.PanelColor;
        }

        /// <summary>The statuses as they look on a node - a bar in the status colour and the
        /// name - laid out inline. Returns the width consumed.</summary>
        private float BuildLegendChips(RectTransform toolbar, float x, float itemY, float itemHeight)
        {
            var cursor = x;
            foreach (var status in LegendStatuses)
            {
                cursor += BuildLegendChip(
                    toolbar, $"Legend_{status}", cursor, itemY, itemHeight,
                    QuestNodeView.ColorFor(status), QuestNodeView.NameFor(status), _treeOnly);
            }

            return cursor - x;
        }

        /// <summary>The whole legend as one chip for a bar too narrow to name the six states in a
        /// row: an accent bar, the word "Legend", then each state's glyph in its own colour. The
        /// glyph-and-colour pairing is the legend - it is what the boxes wear - so the chip reads
        /// on its own; the names are in a tooltip when tooltips exist, which they need not (the
        /// setting can be off, or the game's tooltip context absent). Glyphs, colours and names
        /// all come from the QuestNodeView tables the boxes draw from, so they cannot drift.
        /// Returns the width consumed.</summary>
        private float BuildCollapsedLegend(RectTransform toolbar, float x, float itemY, float itemHeight)
        {
            // The bar and label are children of one hover area rather than siblings in the bar:
            // the tooltip attaches to a single object, and it has to cover both. The host's Image
            // is invisible; whether it catches the pointer is decided below, once it is known
            // whether there is a tooltip to show.
            var hostGo = new GameObject("Legend", typeof(RectTransform), typeof(Image));
            var hostRect = (RectTransform)hostGo.transform;
            hostRect.SetParent(toolbar, worldPositionStays: false);
            hostRect.anchorMin = hostRect.anchorMax = new Vector2(0f, 1f);
            hostRect.pivot = new Vector2(0f, 1f);
            hostRect.anchoredPosition = new Vector2(x, itemY);
            var host = hostGo.GetComponent<Image>();
            host.color = Color.clear;
            _treeOnly.Add(hostGo);

            var label = CollapsedLegendLabel;
            var lines = new List<string>(LegendStatuses.Length);
            foreach (var status in LegendStatuses)
            {
                var glyph = $"<color=#{QuestNodeView.HexFor(status)}>{QuestNodeView.GlyphFor(status)}</color>";
                label += " " + glyph;
                lines.Add(glyph + "  " + QuestNodeView.NameFor(status));
            }

            var width = BuildLegendChip(
                hostRect, "Chip", 0f, 0f, itemHeight, GameStyle.AccentColor, label, treeOnly: null);
            hostRect.sizeDelta = new Vector2(width, itemHeight);

            // A raycast target only when there is a tooltip for it to raise: AddTooltip attaches
            // nothing with tooltips off or the game's tooltip context absent, and an invisible
            // pointer-catcher with nothing behind it would only swallow hovers.
            host.raycastTarget = GameStyle.AddTooltip(hostGo, string.Join("\n", lines)) != null;
            return width;
        }

        /// <summary>One bar-and-name chip at <paramref name="x"/> under <paramref name="parent"/>.
        /// Returns the width consumed, which is LegendChipWidth of the measured label - the one
        /// formula the fit arithmetic also uses. The pieces go into <paramref name="treeOnly"/>
        /// when the caller wants them hidden on the aux tabs individually; null when the parent
        /// is hidden as a whole.</summary>
        private static float BuildLegendChip(
            RectTransform parent, string name, float x, float itemY, float itemHeight,
            Color barColor, string label, List<GameObject> treeOnly)
        {
            var barGo = new GameObject(name, typeof(RectTransform), typeof(Image));
            var barRect = (RectTransform)barGo.transform;
            barRect.SetParent(parent, worldPositionStays: false);
            barRect.anchorMin = barRect.anchorMax = new Vector2(0f, 1f);
            barRect.pivot = new Vector2(0f, 1f);
            barRect.anchoredPosition = new Vector2(x, itemY - (itemHeight - 14f) / 2f);
            barRect.sizeDelta = new Vector2(LayoutMetrics.StatusBarWidth, 14f);
            var bar = barGo.GetComponent<Image>();
            bar.color = barColor;
            bar.raycastTarget = false;
            treeOnly?.Add(barGo);

            var textGo = new GameObject("Label", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(parent, worldPositionStays: false);
            textRect.anchorMin = textRect.anchorMax = new Vector2(0f, 1f);
            textRect.pivot = new Vector2(0f, 1f);
            textRect.anchoredPosition = new Vector2(x + LayoutMetrics.StatusBarWidth + 4f, itemY);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = LegendFontSize;
            text.alignment = TextAlignmentOptions.Left;
            text.color = GameStyle.DimTextColor;
            text.raycastTarget = false;
            GameStyle.Apply(text);
            treeOnly?.Add(textGo);

            var labelWidth = GameStyle.MeasureWidth(text, label);
            textRect.sizeDelta = new Vector2(labelWidth + 8f, itemHeight);

            return LegendChipWidth(labelWidth);
        }

        /// <summary>The "showing N of M" line, sitting immediately right of the search box on the
        /// same manual x-cursor. Placed here rather than over the graph so a truncated tab is
        /// stated up front instead of being something you have to notice.</summary>
        private void BuildRenderNotice(
            RectTransform toolbar, float itemY, float itemHeight, float x, float rightInset)
        {
            var noticeGo = new GameObject("RenderNotice", typeof(RectTransform));
            var noticeRect = (RectTransform)noticeGo.transform;
            noticeRect.SetParent(toolbar, worldPositionStays: false);
            noticeRect.anchorMin = new Vector2(0f, 1f);
            noticeRect.anchorMax = new Vector2(1f, 1f);
            noticeRect.pivot = new Vector2(0f, 1f);
            noticeRect.anchoredPosition = new Vector2(x, itemY);
            // Stretched: width follows the window, so the notice yields to the button cluster
            // instead of running underneath it on a narrow screen.
            noticeRect.sizeDelta = new Vector2(-(x + rightInset), itemHeight);

            _renderNotice = noticeGo.AddComponent<TextMeshProUGUI>();
            _renderNotice.fontSize = 12;
            _renderNotice.alignment = TextAlignmentOptions.Left;
            // Ellipsised: in a window too narrow for the whole line it used to wrap under the
            // buttons, or vanish when the stretch went negative.
            _renderNotice.enableWordWrapping = false;
            _renderNotice.overflowMode = TextOverflowModes.Ellipsis;
            _renderNotice.color = new Color(1f, 1f, 1f, 0.7f);
            GameStyle.Apply(_renderNotice);
        }

        /// <summary>
        /// The panel draws as an opaque full-screen overlay (MenuTaskBarAwakePatch.CreatePanel, in
        /// Patches/MenuTaskBarPatch.cs),
        /// so the button that opened it is covered the moment it's open - this is the only way to
        /// close the tree back to the trader dialog. Anchored to the toolbar's top-right corner and
        /// positioned by hand (see Build) rather than a flexible-spacer layout-group trick.
        /// </summary>
        private void BuildCloseButton(
            RectTransform toolbar, float itemY, float itemHeight, float padding, Action closeTree)
        {
            const float width = CloseButtonWidth;

            // Cloned from the game's own button where possible, so it carries EFT's styling, hover
            // animation and click sound rather than looking like a mod's rectangle.
            var closeRect = GameStyle.CreateButton(toolbar, "Close", closeTree);
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-padding, itemY);
            closeRect.sizeDelta = new Vector2(width, itemHeight);
        }

        /// <summary>Lights whichever view button is selected. These are not part of the tab row's
        /// backgrounds - that collection is cleared and rebuilt with the tab row, whereas these are
        /// built once with the toolbar and outlive it.</summary>
        public void SetViewHighlight(string selectedTabId, Color selected, Color unselected)
        {
            foreach (var (tabId, background) in _viewButtonBackgrounds)
            {
                if (background != null)
                    background.color = tabId == selectedTabId ? selected : unselected;
            }
        }

        /// <summary>Whether a node survives the current search box contents.
        ///
        /// Matched against everything the quest can be found by - its name, its trader, the items
        /// it rewards, the trader offers it unlocks, the items it wants you to bring, and the maps
        /// it happens on - against a haystack the graph flattened once, so this is ONE ordinal
        /// IndexOf per node rather than fifteen comparisons. That is cheaper than the two it
        /// replaced, not more expensive, which is what makes searching fifteen fields affordable at
        /// all.
        ///
        /// The needle is lowercased once when the box changes, never per node: doing it here would
        /// allocate a fresh string for each of 830 nodes on every keystroke, which is exactly the
        /// stutter this design exists to avoid.</summary>
        public bool MatchesSearch(QuestNode node)
        {
            if (_searchNeedle.Length == 0) return true;
            if (node == null) return false;

            // Ordinal, not the bare IndexOf(string) overload: that one is culture-sensitive, slower
            // than this, and on some runtimes ignores zero-weight characters and matches things
            // nobody typed.
            return node.SearchText.IndexOf(_searchNeedle, StringComparison.Ordinal) >= 0;
        }

        /// <summary>The trimmed, lowercased search text - the needle every node is tested against.</summary>
        public string SearchNeedle => _searchNeedle;

        /// <summary>Quests completed across the whole game, counted once per graph rather than once per
        /// notice.
        ///
        /// This ran over all ~830 nodes on every notice update, and the notice updates from the render path
        /// - so it was a full scan per repaint for a number that can only change when the graph does.
        /// Version moves on QuestGraphBuilder.Build and on every RefreshStatuses, which are exactly the
        /// events that can move it.</summary>
        private int CompletedCount()
        {
            if (_graph == null) return 0;
            if (_completedVersion == _graph.Version) return _completed;

            var completed = 0;
            foreach (var node in _graph.Nodes)
                if (node.Status == ENodeStatus.Completed) completed++;

            _completedVersion = _graph.Version;
            return _completed = completed;
        }

        private int _completed;
        private int _completedVersion = -1;

        /// <summary>Reports how much of the tab the current filters and search are showing. Since
        /// virtualization removed the render cap this is a plain count rather than a truncation
        /// warning - what is laid out is what you can reach by panning.</summary>
        /// <remarks>searchMatches is how many of the laid-out quests the search matched, or -1 when
        /// nothing is being searched for. Its own number because search no longer removes anything from
        /// the layout: "805 of 830 shown" described a tree with 25 quests missing, and there is no longer
        /// such a tree to describe.</remarks>
        public void UpdateRenderNotice(int matchingCount, int tabTotal, bool focused = false, int searchMatches = -1)
        {
            RefreshFocusState();
            RefreshChainsState();

            if (_renderNotice == null) return;

            // Says so in the UI, not just the log: without the companion server mod the tree can
            // only ever show quests already unlocked, which otherwise just looks like a short tree.
            var source = _graph.HasFullQuestList ? "" : $"  <color=#{GameStyle.ErrorHex}>(unlocked only - server mod not found)</color>";

            if (matchingCount == 0)
            {
                _renderNotice.text = (tabTotal == 0
                    ? "No quests in this tab"
                    : $"No quests pass the filters in {tabTotal:N0}") + source;
                return;
            }

            if (searchMatches >= 0)
            {
                var found = searchMatches == 0
                    ? $"<color=#{GameStyle.WarningHex}>No matches</color>"
                    : $"{searchMatches:N0} match{(searchMatches == 1 ? "" : "es")}";

                _renderNotice.text = $"{found}  <color=#FFFFFF80>·  {matchingCount:N0} quests shown</color>" + source;
                return;
            }

            // One sentence, not two numbers side by side: "787 of 812 quests   5 / 812 completed"
            // read as a contradiction. Overall progress is across the whole game, not just this
            // tab - the one number most people actually want from a quest tracker.
            var completed = CompletedCount();

            var shown = matchingCount < tabTotal
                ? $"{matchingCount:N0} of {tabTotal:N0} shown"
                : $"{matchingCount:N0} shown";

            var prefix = focused ? "<color=#FFFFFF80>Focus:</color> " : "";
            var overall = _graph.Nodes.Count > 0
                ? $"  <color=#FFFFFF80>·  {completed:N0} of {_graph.Nodes.Count:N0} completed</color>"
                : "";

            _renderNotice.text = prefix + shown + overall + source;
        }

        /// <summary>Puts an arbitrary line in the notice - the aux tabs and the "nothing to frame"
        /// fallback say something other than a quest count.</summary>
        public void SetNotice(string text)
        {
            if (_renderNotice == null) return;
            _renderNotice.text = text;
        }

        public bool IsSearchFocused() => _searchField != null && _searchField.isFocused;

        /// <summary>The frame on which the search box last lost focus. TMP_InputField handles
        /// Escape itself by deactivating, and whether that runs before or after the panel's
        /// Update on the same frame is script-order luck - so the panel treats an Escape on the
        /// frame the box blurred as the box's, not the panel's.</summary>
        private int _searchBlurFrame = -1;
        public bool SearchBlurredThisFrame => _searchBlurFrame == Time.frameCount;

        /// <summary>Empties the box and takes focus off it - what Escape means while typing.
        /// Setting the text re-renders through onValueChanged like any other edit.</summary>
        public void ClearAndBlurSearch()
        {
            if (_searchField == null) return;
            _searchField.text = "";
            _searchField.DeactivateInputField();
        }

        public void FocusSearch()
        {
            if (_searchField == null) return;
            _searchField.Select();
            _searchField.ActivateInputField();
        }
    }
}
