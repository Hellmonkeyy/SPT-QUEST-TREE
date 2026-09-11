using System.Collections.Generic;
using System;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The bar across the top of the Quest Tree: the search box, the "showing N of M" notice, the
    /// My quests / Fit / Settings / Close buttons, and the legend that sits over the graph's
    /// bottom-left corner.
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

        private QuestGraphBuilder _graph;
        private TMP_InputField _searchField;
        private TMP_Text _renderNotice;
        private Image _focusBackground;

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

                // A full re-render, not a visibility pass: the search decides which nodes get
                // built at all (see QuestGraphView.Render), which is what keeps a
                // multi-thousand-quest tab affordable.
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
                "Show only what you can work on, plus what it needs and unlocks", out _focusBackground, treeOnly: true);
            RefreshFocusState();

            // The controls hint is shown once and then never again on its own, which would make it
            // useless to anyone who dismissed it before they knew what it was for. This is how you
            // get it back.
            navX += BuildToolbarAction(toolbar, "?", navX, itemY, itemHeight, 30f, showIntro, "Controls");

            // The legend lives here, in the bar, as the same bar-and-name the nodes wear - it used
            // to be a box in the corner of the graph, over whatever was drawn there.
            navX += BuildLegendChips(toolbar, navX + 6f, itemY, itemHeight);

            BuildCloseButton(toolbar, itemY, itemHeight, padding, closeTree);

            // Maps / Items / Kappa / Settings live together at the right, next to Close: they are
            // whole views rather than a slice of the quest graph, so grouping them apart from the
            // trader tabs says which is which. Laid out right-to-left from Close so the cluster
            // stays put whatever the window width.
            var rightOffset = padding + CloseButtonWidth + 6f;
            foreach (var (tabId, label) in viewButtons)
            {
                rightOffset += BuildViewButton(toolbar, tabId, label, rightOffset, itemY, itemHeight);
            }

            // Stretched between the nav buttons and that cluster rather than given a fixed width,
            // so it cannot collide with them on a narrow window.
            BuildRenderNotice(toolbar, itemY, itemHeight, navX + 8f, rightOffset + 8f);
        }

        /// <summary>One of the non-graph views. Returns the width consumed so the caller can keep
        /// walking leftwards from Close.</summary>
        private float BuildViewButton(
            RectTransform toolbar, string tabId, string label, float rightOffset, float itemY, float itemHeight)
        {
            var buttonRect = GameStyle.CreateButton(toolbar, label, () => _onViewSelected(tabId));
            var width = Mathf.Clamp(GameStyle.MeasureWidth(buttonRect.GetComponentInChildren<TMP_Text>(), label) + 24f, 70f, 110f);
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

        /// <summary>The four statuses as they look on a node - a bar in the status colour and the
        /// name - laid out inline. Returns the width consumed.</summary>
        private float BuildLegendChips(RectTransform toolbar, float x, float itemY, float itemHeight)
        {
            var statuses = new[]
            {
                ENodeStatus.Active, ENodeStatus.Available, ENodeStatus.Completed, ENodeStatus.Locked
            };

            var cursor = x;

            foreach (var status in statuses)
            {
                var barGo = new GameObject($"Legend_{status}", typeof(RectTransform), typeof(Image));
                var barRect = (RectTransform)barGo.transform;
                barRect.SetParent(toolbar, worldPositionStays: false);
                barRect.anchorMin = barRect.anchorMax = new Vector2(0f, 1f);
                barRect.pivot = new Vector2(0f, 1f);
                barRect.anchoredPosition = new Vector2(cursor, itemY - (itemHeight - 14f) / 2f);
                barRect.sizeDelta = new Vector2(LayoutMetrics.StatusBarWidth, 14f);
                var bar = barGo.GetComponent<Image>();
                bar.color = QuestNodeView.ColorFor(status);
                bar.raycastTarget = false;
                _treeOnly.Add(barGo);

                var name = QuestNodeView.NameFor(status);

                var textGo = new GameObject("Label", typeof(RectTransform));
                var textRect = (RectTransform)textGo.transform;
                textRect.SetParent(toolbar, worldPositionStays: false);
                textRect.anchorMin = textRect.anchorMax = new Vector2(0f, 1f);
                textRect.pivot = new Vector2(0f, 1f);
                textRect.anchoredPosition = new Vector2(cursor + LayoutMetrics.StatusBarWidth + 4f, itemY);
                var text = textGo.AddComponent<TextMeshProUGUI>();
                text.text = name;
                text.fontSize = 11;
                text.alignment = TextAlignmentOptions.Left;
                text.color = GameStyle.DimTextColor;
                text.raycastTarget = false;
                GameStyle.Apply(text);
                _treeOnly.Add(textGo);

                var width = GameStyle.MeasureWidth(text, name) + 8f;
                textRect.sizeDelta = new Vector2(width, itemHeight);

                cursor += LayoutMetrics.StatusBarWidth + 4f + width + 10f;
            }

            return cursor - x;
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

        /// <summary>
        /// Names what the node colours mean. Overlaid on the graph's bottom-left corner rather than
        /// placed in the toolbar: the toolbar is already full and laid out with a manual x-cursor,
        /// so anything added there risks colliding with the right-anchored buttons on a narrow
        /// window. Here it cannot collide with anything.
        ///
        /// Colours and glyphs come from QuestNodeView so the legend can never drift from the nodes.
        /// </summary>
        /// <summary>Tints the Settings button for the selected state. The panel supplies the colour
        /// because it owns the same pair the tab row is highlighted with.</summary>
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

        /// <summary>Reports how much of the tab the current filters and search are showing. Since
        /// virtualization removed the render cap this is a plain count rather than a truncation
        /// warning - what is laid out is what you can reach by panning.</summary>
        public void UpdateRenderNotice(int matchingCount, int tabTotal, bool focused = false)
        {
            RefreshFocusState();

            if (_renderNotice == null) return;

            // Says so in the UI, not just the log: without the companion server mod the tree can
            // only ever show quests already unlocked, which otherwise just looks like a short tree.
            var source = _graph.HasFullQuestList ? "" : $"  <color=#{GameStyle.ErrorHex}>(unlocked only - server mod not found)</color>";

            if (matchingCount == 0)
            {
                _renderNotice.text = (tabTotal == 0
                    ? "No quests in this tab"
                    : $"No matches in {tabTotal:N0} quests") + source;
                return;
            }

            // One sentence, not two numbers side by side: "787 of 812 quests   5 / 812 completed"
            // read as a contradiction. Overall progress is across the whole game, not just this
            // tab - the one number most people actually want from a quest tracker.
            var completed = 0;
            foreach (var node in _graph.Nodes)
                if (node.Status == ENodeStatus.Completed) completed++;

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
