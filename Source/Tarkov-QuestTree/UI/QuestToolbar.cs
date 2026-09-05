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
        private Image _settingsButtonBackground;

        private string _searchFilter = "";

        /// <summary>Toggles the Settings tab on and off - supplied by the panel, which owns tab
        /// selection.</summary>
        private Action _toggleSettings;

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
            Action frameMyQuests,
            Action frameContent,
            Action toggleSettings,
            Action closeTree,
            Action showIntro)
        {
            _graph = graph;
            _toggleSettings = toggleSettings;

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

            var searchTextGo = new GameObject("Text", typeof(RectTransform));
            var searchTextRect = (RectTransform)searchTextGo.transform;
            searchTextRect.SetParent(searchRect, worldPositionStays: false);
            searchTextRect.anchorMin = Vector2.zero;
            searchTextRect.anchorMax = Vector2.one;
            searchTextRect.offsetMin = new Vector2(8f, 2f);
            searchTextRect.offsetMax = new Vector2(-8f, -2f);
            var searchText = searchTextGo.AddComponent<TextMeshProUGUI>();
            searchText.fontSize = 12;
            searchText.color = Color.white;
            GameStyle.Apply(searchText);

            // Without a placeholder the box reads as an empty grey rectangle - there is nothing on
            // screen otherwise to say it is a search field at all.
            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            var placeholderRect = (RectTransform)placeholderGo.transform;
            placeholderRect.SetParent(searchRect, worldPositionStays: false);
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(8f, 2f);
            placeholderRect.offsetMax = new Vector2(-8f, -2f);

            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholder.text = "Search quests or traders  ( / )";
            placeholder.fontSize = 12;
            placeholder.color = GameStyle.DimTextColor;
            GameStyle.Apply(placeholder);

            _searchField = searchGo.GetComponent<TMP_InputField>();
            _searchField.textComponent = searchText;
            _searchField.placeholder = placeholder;
            _searchField.onValueChanged.AddListener(value =>
            {
                _searchFilter = value ?? "";

                // The Kappa and Settings tabs ignore the search box entirely, and rebuilding them
                // here used to re-fetch the Kappa payload on EVERY keystroke - a blocking request
                // that walks the whole profile inventory server-side. Only the graph cares.
                if (isAuxTabSelected()) return;

                // A full re-render, not a visibility pass: the search decides which nodes get
                // built at all (see QuestGraphView.Render), which is what keeps a
                // multi-thousand-quest tab affordable.
                onSearchChanged();
            });

            // Placed between the search box and the notice, on the same manual x-cursor.
            var navX = padding + searchWidth + 10f;
            navX += BuildToolbarAction(toolbar, "My quests (M)", navX, itemY, itemHeight, 110f, frameMyQuests);
            navX += BuildToolbarAction(toolbar, "Fit (F)", navX, itemY, itemHeight, 70f, frameContent);

            // The controls hint is shown once and then never again on its own, which would make it
            // useless to anyone who dismissed it before they knew what it was for. This is how you
            // get it back.
            navX += BuildToolbarAction(toolbar, "?", navX, itemY, itemHeight, 30f, showIntro);

            BuildRenderNotice(toolbar, itemY, itemHeight, navX + 8f);
            BuildCloseButton(toolbar, itemY, itemHeight, padding, closeTree);

            // Settings sits in the toolbar beside Close rather than in the tab row: it is not a
            // slice of the quest data the way every other tab is, and with 20 trader tabs it was
            // one of the entries falling off the scrollable end.
            BuildSettingsButton(toolbar, itemY, itemHeight, padding + CloseButtonWidth + 6f);
        }

        /// <summary>The Settings entry point. Still drives the same selected-tab state as a tab
        /// would, so the aux panel and highlight logic need no special case - only its highlight is
        /// tracked separately, since it is no longer inside the tab row's backgrounds.</summary>
        private void BuildSettingsButton(RectTransform toolbar, float itemY, float itemHeight, float rightOffset)
        {
            const float width = 90f;

            var buttonRect = GameStyle.CreateButton(toolbar, "Settings", () => _toggleSettings());
            buttonRect.anchorMin = buttonRect.anchorMax = new Vector2(1f, 1f);
            buttonRect.pivot = new Vector2(1f, 1f);
            buttonRect.anchoredPosition = new Vector2(-rightOffset, itemY);
            buttonRect.sizeDelta = new Vector2(width, itemHeight);

            // A cloned native button brings its own background; only the fallback rectangle has one
            // of ours to tint for the selected state.
            _settingsButtonBackground = buttonRect.GetComponent<Image>();

        }

        /// <summary>One toolbar action button. Returns the width it consumed so the caller can keep
        /// advancing its cursor - zoom and framing are otherwise invisible features you have to
        /// already know the wheel and keyboard do.</summary>
        private float BuildToolbarAction(
            RectTransform toolbar, string label, float x, float itemY, float itemHeight, float width, Action onClick)
        {
            var rect = GameStyle.CreateButton(toolbar, label, onClick);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, itemY);
            rect.sizeDelta = new Vector2(width, itemHeight);

            return width + 6f;
        }

        /// <summary>The "showing N of M" line, sitting immediately right of the search box on the
        /// same manual x-cursor. Placed here rather than over the graph so a truncated tab is
        /// stated up front instead of being something you have to notice.</summary>
        private void BuildRenderNotice(RectTransform toolbar, float itemY, float itemHeight, float x)
        {
            var noticeGo = new GameObject("RenderNotice", typeof(RectTransform));
            var noticeRect = (RectTransform)noticeGo.transform;
            noticeRect.SetParent(toolbar, worldPositionStays: false);
            noticeRect.anchorMin = noticeRect.anchorMax = new Vector2(0f, 1f);
            noticeRect.pivot = new Vector2(0f, 1f);
            noticeRect.anchoredPosition = new Vector2(x, itemY);
            noticeRect.sizeDelta = new Vector2(640f, itemHeight);

            _renderNotice = noticeGo.AddComponent<TextMeshProUGUI>();
            _renderNotice.fontSize = 12;
            _renderNotice.alignment = TextAlignmentOptions.Left;
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
        public void BuildLegend(RectTransform viewport)
        {
            const float rowHeight = 18f;
            var statuses = new[]
            {
                ENodeStatus.Completed, ENodeStatus.Active, ENodeStatus.Available, ENodeStatus.Locked
            };

            var legendGo = new GameObject("Legend", typeof(RectTransform), typeof(Image));
            var legend = (RectTransform)legendGo.transform;
            legend.SetParent(viewport, worldPositionStays: false);
            legend.anchorMin = legend.anchorMax = new Vector2(0f, 0f);
            legend.pivot = new Vector2(0f, 0f);
            legend.anchoredPosition = new Vector2(10f, 10f);
            legend.sizeDelta = new Vector2(150f, rowHeight * statuses.Length + 12f);

            var background = legendGo.GetComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.55f);
            GameStyle.ApplyPanel(background);
            background.raycastTarget = false; // must never eat a drag meant for panning

            for (var index = 0; index < statuses.Length; index++)
            {
                var status = statuses[index];

                var rowGo = new GameObject($"Legend_{status}", typeof(RectTransform));
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.SetParent(legend, worldPositionStays: false);
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(1f, 1f);
                rowRect.pivot = new Vector2(0f, 1f);
                rowRect.anchoredPosition = new Vector2(8f, -(6f + index * rowHeight));
                rowRect.sizeDelta = new Vector2(-16f, rowHeight);

                var text = rowGo.AddComponent<TextMeshProUGUI>();
                var hex = ColorUtility.ToHtmlStringRGB(QuestNodeView.ColorFor(status));
                text.text = $"<color=#{hex}>{QuestNodeView.GlyphFor(status)}</color>  {QuestNodeView.NameFor(status)}";
                text.fontSize = 11;
                text.alignment = TextAlignmentOptions.Left;
                text.color = Color.white;
                text.raycastTarget = false;
                GameStyle.Apply(text);
            }
        }

        /// <summary>Tints the Settings button for the selected state. The panel supplies the colour
        /// because it owns the same pair the tab row is highlighted with.</summary>
        public void SetSettingsHighlight(Color color)
        {
            // Not part of the tab row's backgrounds - that collection is cleared and rebuilt with
            // the tab row, whereas the Settings button is built once with the toolbar and outlives
            // it.
            if (_settingsButtonBackground != null)
                _settingsButtonBackground.color = color;
        }

        /// <summary>Whether a node survives the current search box contents. Matched against the
        /// quest name and its trader, so "prapor" narrows to one trader's chain just as a quest
        /// name does.</summary>
        public bool MatchesSearch(QuestNode node)
        {
            var search = _searchFilter.Trim();
            if (search.Length == 0) return true;

            return node.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                   || (node.TraderName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        /// <summary>Reports how much of the tab the current filters and search are showing. Since
        /// virtualization removed the render cap this is a plain count rather than a truncation
        /// warning - what is laid out is what you can reach by panning.</summary>
        public void UpdateRenderNotice(int matchingCount, int tabTotal)
        {
            if (_renderNotice == null) return;

            // Says so in the UI, not just the log: without the companion server mod the tree can
            // only ever show quests already unlocked, which otherwise just looks like a short tree.
            var source = _graph.HasFullQuestList ? "" : "  <color=#C86464>(unlocked only - server mod not found)</color>";

            if (matchingCount == 0)
            {
                _renderNotice.text = (tabTotal == 0
                    ? "No quests in this tab"
                    : $"No matches in {tabTotal:N0} quests") + source;
                return;
            }

            var filtered = matchingCount < tabTotal
                ? $"{matchingCount:N0} of {tabTotal:N0} quests"
                : $"{matchingCount:N0} quest{(matchingCount == 1 ? "" : "s")}";

            // Overall progress across the whole game, not just this tab - the one number most
            // people actually want from a quest tracker.
            var completed = 0;
            foreach (var node in _graph.Nodes)
                if (node.Status == ENodeStatus.Completed) completed++;

            var overall = _graph.Nodes.Count > 0
                ? $"   <color=#FFFFFF80>{completed:N0} / {_graph.Nodes.Count:N0} completed overall</color>"
                : "";

            _renderNotice.text = filtered + overall + source;
        }

        /// <summary>Puts an arbitrary line in the notice - the aux tabs and the "nothing to frame"
        /// fallback say something other than a quest count.</summary>
        public void SetNotice(string text)
        {
            if (_renderNotice == null) return;
            _renderNotice.text = text;
        }

        public bool IsSearchFocused() => _searchField != null && _searchField.isFocused;

        public void FocusSearch()
        {
            if (_searchField == null) return;
            _searchField.Select();
            _searchField.ActivateInputField();
        }
    }
}
