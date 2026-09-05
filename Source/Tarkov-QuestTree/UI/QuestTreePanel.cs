using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EFT;
using EFT.Quests;
using EFT.Trading;
using EFT.UI;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The Quest Tree overlay: a pannable/zoomable canvas of every quest in the game, laid out as
    /// a layered DAG (column = prerequisite depth, row = trader lane) with lines drawn from each
    /// quest to what it unlocks. Built entirely from runtime UI primitives, toggled open by the
    /// taskbar button added by <see cref="QuestTree.Patches.MenuTaskBarAwakePatch"/> and closed by
    /// the Close button in its own toolbar (see BuildToolbar) - it draws as an opaque full-screen
    /// overlay, so it cannot rely on anything outside itself to close it.
    ///
    /// Status is live: QuestController.OnConditionalStatusChanged - the same event the vanilla
    /// task list subscribes to - drives a cheap recolor pass with no polling and no rebuild of the
    /// graph structure itself.
    /// </summary>
    internal sealed class QuestTreePanel : UIElement
    {
        private const float ColumnSpacing = 260f;
        private const float RowSpacing = 100f;
        private const float MinZoom = 0.25f;
        private const float MaxZoom = 1.5f;
        private const float ZoomSpeed = 0.08f;
        private const float ToolbarHeight = 36f;

        /// <summary>Tall enough for a 28px tab plus the horizontal scrollbar beneath it - the tab
        /// row overflows the screen once every trader has a tab, so the bar is what makes it
        /// obvious the row scrolls at all.</summary>
        private const float TabRowHeight = 40f;

        private const float TabHeight = 28f;
        private const float TabScrollbarHeight = 5f;

        /// <summary>Shared by the Close button and by the Settings button that sits beside it.</summary>
        private const float CloseButtonWidth = 90f;

        /// <summary>Fallback ceiling on simultaneously-built node views, used only if settings have
        /// not initialised. Virtualization means this is normally unreachable - it exists for the
        /// fully-zoomed-out case, where the viewport can still cover a thousand-odd quests at once.
        /// The real value is <see cref="ModSettings.MaxVisibleNodes"/>.</summary>
        private const int DefaultMaxVisibleNodes = 600;

        /// <summary>Sentinel for the "All" tab - not a real trader id, so it can't collide with one.</summary>
        private const string AllTradersId = "";

        /// <summary>Sentinels for the two non-graph tabs, appended after the traders. Prefixed the
        /// same way QuestNode.NoTraderId is so they can never collide with a real trader id.</summary>
        private const string KappaTabId = "__kappa__";
        private const string SettingsTabId = "__settings__";

        /// <summary>How far outside the viewport a node is still built, so nodes are already there
        /// when they scroll in rather than popping in at the edge.</summary>
        private const float VirtualizationMargin = 240f;

        /// <summary>How many frames after a render the visibility sweep re-runs unconditionally,
        /// to cover Unity not having laid the viewport out yet. See Update.</summary>
        private const int InitialSweepFrames = 3;

        private static readonly Color SelectedTabColor = new(0.35f, 0.35f, 0.2f, 0.95f);
        private static readonly Color UnselectedTabColor = new(1f, 1f, 1f, 0.08f);

        private readonly QuestGraphBuilder _graph = new();
        private readonly Dictionary<QuestNode, QuestNodeView> _views = new();
        private readonly Dictionary<string, Image> _tabBackgrounds = new();

        /// <summary>Where every node in the current tab sits, whether or not it is currently built.
        /// Computed once per tab/search; the visible window is taken from it every time the view
        /// moves. This is what makes virtualization possible - layout is cheap, instantiation is
        /// not.</summary>
        private readonly Dictionary<QuestNode, Vector2> _layout = new();

        /// <summary>Same nodes as <see cref="_layout"/>, as a flat array - the visibility sweep runs
        /// every time the view moves, and iterating an array beats iterating a dictionary.</summary>
        private QuestNode[] _layoutOrder = Array.Empty<QuestNode>();

        /// <summary>Every edge in the current tab, with both endpoints resolved, built once per tab
        /// alongside the node layout.</summary>
        private (QuestNode From, QuestNode To, Vector2 FromPoint, Vector2 ToPoint)[] _edgeLayout =
            Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();

        /// <summary>Edges currently on screen, keyed by their layout index.</summary>
        private readonly Dictionary<int, RectTransform> _edgeViews = new();

        // Released views are deactivated and kept rather than destroyed - panning across a large
        // tree otherwise means a constant churn of Instantiate/Destroy, which is the expensive part.
        private readonly Stack<QuestNodeView> _nodePool = new();
        private readonly Stack<RectTransform> _edgePool = new();

        // Scratch collections reused by the visibility sweep so it allocates nothing per frame.
        private readonly List<QuestNode> _nodesToRelease = new();
        private readonly List<int> _edgesToRelease = new();

        private QuestController _questController;
        private IEftSession _session;
        private RectTransform _viewport;
        private RectTransform _content;
        private RectTransform _auxPanel;
        private RectTransform _auxContent;
        private RectTransform _loadingPanel;
        private TMP_Text _loadingLabel;
        private Vector2 _lastContentPosition;
        private float _lastContentScale;

        /// <summary>Frames left in the post-render re-sweep window. See Update for why.</summary>
        private int _initialSweepFrames;

        /// <summary>Guards the never-empty recovery against recursing into itself.</summary>
        private bool _recoveringView;
        private RectTransform _detailPanel;
        private TMP_Text _detailText;
        private TMP_InputField _searchField;
        private TMP_Text _renderNotice;
        private RectTransform _tabRow;
        private RectTransform _tabContent;
        private Image _settingsButtonBackground;

        /// <summary>Which tree tab to return to when Settings is toggled back off.</summary>
        private string _tabBeforeSettings = AllTradersId;
        private string _searchFilter = "";
        private string _selectedTraderId = AllTradersId;
        private bool _builtShell;
        private float _tabCursorX;
        private QuestNode _detailNode;

        public void OnAwake()
        {
            if (_builtShell) return;
            _builtShell = true;
            BuildShell();

            // Settings are editable from the in-panel tab AND from the F12 menu, so the panel
            // reacts to the value changing rather than to either editor in particular.
            ModSettings.Changed += HandleSettingsChanged;
        }

        private void HandleSettingsChanged()
        {
            if (!_builtShell || _questController == null) return;

            try
            {
                RenderSelectedTab();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to re-render after a settings change: {ex}");
            }
        }

        /// <summary>Called by the tab's ScreenVisibilityTabController every time the Quest Tree tab
        /// is selected. Cheap to call repeatedly - the graph is only rebuilt from scratch when the
        /// QuestController instance actually changes (e.g. a fresh session).</summary>
        public void Show(QuestController questController, IEftSession session)
        {
            OnAwake();
            ShowGameObject();
            GameStyle.PlaySound(EUISoundType.MenuInspectorWindowOpen);
            QuestDataClient.InvalidateKappa();
            _session = session; // kept for trader-avatar lookups on tab icons, independent of a graph rebuild

            if (!ReferenceEquals(_questController, questController))
            {
                if (_questController != null)
                    _questController.OnConditionalStatusChanged -= HandleStatusChanged;

                _questController = questController;
                if (_questController != null)
                    _questController.OnConditionalStatusChanged += HandleStatusChanged;

                // Deferred by a frame so the loading notice actually paints first. Building the
                // graph means fetching several MB, parsing it, and laying out thousands of quests,
                // all on the UI thread - done inline it reads as the game having frozen.
                ShowLoading(true);
                StartCoroutine(RebuildGraphDeferred(session));
            }
        }

        /// <summary>Waits for the loading notice to render, then does the expensive build. Unity
        /// paints between frames, so a single yield is enough for the notice to be on screen before
        /// the thread is tied up.</summary>
        private System.Collections.IEnumerator RebuildGraphDeferred(IEftSession session)
        {
            yield return null;
            yield return null;

            TryRebuildGraph(session);
            ShowLoading(false);
        }

        /// <summary>Separate from the coroutine because C# forbids yielding inside a try/catch that
        /// has a catch clause - and this call must not be allowed to throw into the taskbar button
        /// handler that started it.</summary>
        private void TryRebuildGraph(IEftSession session)
        {
            try
            {
                RebuildGraph(session);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to build the quest tree: {ex}");
                if (_loadingLabel != null)
                    _loadingLabel.text = "Could not build the quest tree - see the BepInEx log.";
            }
        }

        private void ShowLoading(bool visible)
        {
            if (_loadingPanel == null) return;

            if (visible && _loadingLabel != null)
                _loadingLabel.text = "Loading quests...";

            _loadingPanel.gameObject.SetActive(visible);
            if (visible) _loadingPanel.SetAsLastSibling();
        }

        /// <summary>Unity only calls Update on an active GameObject, so this only ever runs while
        /// the panel is actually open - no extra "is it visible" guard needed.</summary>
        public void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Layered: the detail panel is the innermost thing open, so it goes first. A second
                // press closes the tree itself.
                if (IsDetailOpen) HideDetail();
                else HideGameObject();
                return;
            }

            // Shortcuts. Deliberately only while the panel has focus and the search box does not -
            // typing "f" into search must not re-frame the view.
            if (!IsSearchFocused())
            {
                if (Input.GetKeyDown(KeyCode.F)) FrameContent();
                if (Input.GetKeyDown(KeyCode.M)) FrameMyQuests();
                if (Input.GetKeyDown(KeyCode.Slash)) FocusSearch();
            }

            // Virtualization is driven from here rather than from PanZoomHandler's callbacks so it
            // also catches zoom changes, window resizes and anything else that moves the content -
            // one check for every cause, instead of one hook per cause. Nothing happens on a frame
            // where the view did not move.
            if (_content == null || !_content.gameObject.activeInHierarchy) return;

            // The viewport's world corners are only correct once Unity has laid the panel out, which
            // has not necessarily happened on the frame the tab is built - and if the first sweep
            // measures an empty rect, nothing gets built and the tree looks blank until the user
            // happens to pan. Re-sweeping for a few frames after a render costs nothing and removes
            // the dependence on when layout settles.
            if (_initialSweepFrames > 0)
            {
                _initialSweepFrames--;
                RefreshVisibleNodes();
                return;
            }

            if (_content.anchoredPosition == _lastContentPosition &&
                Mathf.Approximately(_content.localScale.x, _lastContentScale))
            {
                return;
            }

            _lastContentPosition = _content.anchoredPosition;
            _lastContentScale = _content.localScale.x;
            RefreshVisibleNodes();
        }

        public override void Close()
        {
            if (_questController != null)
            {
                _questController.OnConditionalStatusChanged -= HandleStatusChanged;
                _questController = null;
            }

            ModSettings.Changed -= HandleSettingsChanged;

            base.Close();
        }

        private void HandleStatusChanged()
        {
            // A quest turning in can hand over Collector items, so the cached checklist is stale.
            QuestDataClient.InvalidateKappa();

            _graph.RefreshStatuses();
            foreach (var view in _views.Values)
                view.RefreshStatus();
        }

        // ------------------------------------------------------------------ shell

        private void BuildShell()
        {
            var root = (RectTransform)transform;

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            _viewport = (RectTransform)viewportGo.transform;
            var viewport = _viewport;
            viewport.SetParent(root, worldPositionStays: false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(0f, 0f);
            viewport.offsetMax = new Vector2(0f, -(ToolbarHeight + TabRowHeight)); // leaves room for the toolbar + tab row on top
            viewportGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            GameStyle.ApplyPanel(viewportGo.GetComponent<Image>());

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentGo.transform;
            _content.SetParent(viewport, worldPositionStays: false);
            _content.anchorMin = _content.anchorMax = new Vector2(0f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = new Vector2(40f, -40f);

            viewportGo.AddComponent<PanZoomHandler>().Init(_content, MinZoom, MaxZoom, ZoomSpeed);

            BuildLegend(viewport);

            BuildAuxPanel(root);
            BuildLoadingPanel(root);
            BuildToolbar(root);
            BuildTabRow(root);
            BuildDetailPanel(root);
        }

        /// <summary>
        /// Names what the node colours mean. Overlaid on the graph's bottom-left corner rather than
        /// placed in the toolbar: the toolbar is already full and laid out with a manual x-cursor,
        /// so anything added there risks colliding with the right-anchored buttons on a narrow
        /// window. Here it cannot collide with anything.
        ///
        /// Colours and glyphs come from QuestNodeView so the legend can never drift from the nodes.
        /// </summary>
        private void BuildLegend(RectTransform viewport)
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

        /// <summary>A full-panel "working on it" notice, shown while the graph is being fetched and
        /// laid out. Covers the whole panel so nothing half-built is visible behind it.</summary>
        private void BuildLoadingPanel(RectTransform root)
        {
            var panelGo = new GameObject("LoadingPanel", typeof(RectTransform), typeof(Image));
            _loadingPanel = (RectTransform)panelGo.transform;
            _loadingPanel.SetParent(root, worldPositionStays: false);
            _loadingPanel.anchorMin = Vector2.zero;
            _loadingPanel.anchorMax = Vector2.one;
            _loadingPanel.offsetMin = Vector2.zero;
            _loadingPanel.offsetMax = Vector2.zero;
            panelGo.GetComponent<Image>().color = GameStyle.ScreenColor;

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(_loadingPanel, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            _loadingLabel = labelGo.AddComponent<TextMeshProUGUI>();
            _loadingLabel.text = "Loading quests...";
            _loadingLabel.fontSize = 18;
            _loadingLabel.alignment = TextAlignmentOptions.Center;
            _loadingLabel.color = Color.white;
            GameStyle.Apply(_loadingLabel);

            _loadingPanel.gameObject.SetActive(false);
        }

        /// <summary>
        /// The scrollable surface the non-graph tabs (Kappa, Settings) draw into. Occupies the same
        /// area as the graph viewport and is swapped with it, rather than being a separate window -
        /// the toolbar and tab row stay put either way, which is what makes the two feel like tabs
        /// rather than modes.
        /// </summary>
        private void BuildAuxPanel(RectTransform root)
        {
            var panelGo = new GameObject("AuxPanel", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            _auxPanel = (RectTransform)panelGo.transform;
            _auxPanel.SetParent(root, worldPositionStays: false);
            _auxPanel.anchorMin = Vector2.zero;
            _auxPanel.anchorMax = Vector2.one;
            _auxPanel.offsetMin = Vector2.zero;
            _auxPanel.offsetMax = new Vector2(0f, -(ToolbarHeight + TabRowHeight));
            panelGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

            var contentGo = new GameObject("AuxContent", typeof(RectTransform));
            _auxContent = (RectTransform)contentGo.transform;
            _auxContent.SetParent(_auxPanel, worldPositionStays: false);
            _auxContent.anchorMin = new Vector2(0f, 1f);
            _auxContent.anchorMax = new Vector2(1f, 1f);
            _auxContent.pivot = new Vector2(0f, 1f);
            _auxContent.anchoredPosition = Vector2.zero;
            _auxContent.sizeDelta = new Vector2(0f, 0f);

            // Vertical-only drag scrolling, no scrollbar objects to build or keep in sync - the
            // lists here are long but simple.
            var scroll = panelGo.GetComponent<ScrollRect>();
            scroll.content = _auxContent;
            scroll.viewport = _auxPanel;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            _auxPanel.gameObject.SetActive(false);
        }

        /// <summary>
        /// Positions every child by hand (an x-cursor, incremented as each is placed) rather than a
        /// HorizontalLayoutGroup. Two rounds of trying to tame that component's default child-control
        /// behavior in this runtime-built hierarchy still didn't produce a clean row (see git/plan
        /// history), so this switches to the same fully-manual positioning already used successfully
        /// for the graph nodes themselves - fully deterministic, nothing implicit left to debug.
        /// </summary>
        private void BuildToolbar(RectTransform root)
        {
            var toolbarGo = new GameObject("Toolbar", typeof(RectTransform), typeof(Image));
            var toolbar = (RectTransform)toolbarGo.transform;
            toolbar.SetParent(root, worldPositionStays: false);
            toolbar.anchorMin = new Vector2(0f, 1f);
            toolbar.anchorMax = new Vector2(1f, 1f);
            toolbar.pivot = new Vector2(0f, 1f);
            toolbar.sizeDelta = new Vector2(0f, ToolbarHeight);
            toolbarGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);
            GameStyle.ApplyPanel(toolbarGo.GetComponent<Image>());

            const float padding = 8f;
            const float itemHeight = 28f;
            var itemY = -(ToolbarHeight - itemHeight) / 2f; // vertically centered within the bar

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
                if (IsAuxTab(_selectedTraderId)) return;

                // A full re-render, not a visibility pass: the search decides which nodes get
                // built at all (see RenderSelectedTab), which is what keeps a multi-thousand-quest
                // tab affordable.
                RenderSelectedTab();
            });

            // Placed between the search box and the notice, on the same manual x-cursor.
            var navX = padding + searchWidth + 10f;
            navX += BuildToolbarAction(toolbar, "My quests (M)", navX, itemY, itemHeight, 110f, FrameMyQuests);
            navX += BuildToolbarAction(toolbar, "Fit (F)", navX, itemY, itemHeight, 70f, FrameContent);

            BuildRenderNotice(toolbar, itemY, itemHeight, navX + 8f);
            BuildCloseButton(toolbar, itemY, itemHeight, padding);

            // Settings sits in the toolbar beside Close rather than in the tab row: it is not a
            // slice of the quest data the way every other tab is, and with 20 trader tabs it was
            // one of the entries falling off the scrollable end.
            BuildSettingsButton(toolbar, itemY, itemHeight, padding + CloseButtonWidth + 6f);
        }

        /// <summary>The Settings entry point. Still drives the same _selectedTraderId state as a
        /// tab would, so the aux panel and highlight logic need no special case - only its
        /// highlight is tracked separately, since it is no longer inside _tabBackgrounds.</summary>
        private void BuildSettingsButton(RectTransform toolbar, float itemY, float itemHeight, float rightOffset)
        {
            const float width = 90f;

            var buttonRect = GameStyle.CreateButton(toolbar, "Settings", () =>
                SelectTab(_selectedTraderId == SettingsTabId ? _tabBeforeSettings : SettingsTabId));
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

        /// <summary>The row of per-trader tabs (plus "All") below the toolbar - see BuildTabs,
        /// which (re)populates it whenever the graph data changes. Positioned manually (an
        /// x-cursor tracked in _tabCursorX, reset at the start of each BuildTabs pass) rather than
        /// a HorizontalLayoutGroup - see the comment on BuildToolbar for why.</summary>
        private void BuildTabRow(RectTransform root)
        {
            var tabRowGo = new GameObject(
                "TabRow", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            _tabRow = (RectTransform)tabRowGo.transform;
            _tabRow.SetParent(root, worldPositionStays: false);
            _tabRow.anchorMin = new Vector2(0f, 1f);
            _tabRow.anchorMax = new Vector2(1f, 1f);
            _tabRow.pivot = new Vector2(0f, 1f);
            _tabRow.anchoredPosition = new Vector2(0f, -ToolbarHeight);
            _tabRow.sizeDelta = new Vector2(0f, TabRowHeight);
            tabRowGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

            // The tabs live on a separate content object so the row can scroll: "All" + 19 traders
            // + Kappa + Settings comes to roughly 2200px of tabs, which runs off the end of a
            // 1920-wide screen and puts the last few tabs out of reach entirely.
            var contentGo = new GameObject("TabContent", typeof(RectTransform));
            _tabContent = (RectTransform)contentGo.transform;
            _tabContent.SetParent(_tabRow, worldPositionStays: false);
            _tabContent.anchorMin = _tabContent.anchorMax = new Vector2(0f, 1f);
            _tabContent.pivot = new Vector2(0f, 1f);
            _tabContent.anchoredPosition = Vector2.zero;
            _tabContent.sizeDelta = new Vector2(0f, TabRowHeight);

            // Horizontal-only is what makes the mouse wheel work here: ScrollRect.OnScroll copies a
            // vertical wheel delta onto the horizontal axis when horizontal is enabled and vertical
            // is not. Clamped rather than Elastic - a tab bar that springs back reads as broken.
            // Dragging across the tabs themselves also scrolls, because Button handles no drag
            // interface and the drag bubbles up to here.
            var scroll = tabRowGo.GetComponent<ScrollRect>();
            scroll.content = _tabContent;
            scroll.viewport = _tabRow;
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            // Permanent rather than auto-hiding: the point of the bar is to advertise that the row
            // scrolls at all, which a bar you only see once you have already scrolled cannot do.
            scroll.horizontalScrollbar = BuildTabScrollbar(_tabRow);
            scroll.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        /// <summary>
        /// The tab row's horizontal scrollbar, built from primitives like everything else here.
        /// A Scrollbar needs its handle to be a descendant whose rect it drives, so this is the
        /// standard bar -> sliding area -> handle chain; ScrollRect sizes and positions the handle
        /// from the content width once it is wired up as horizontalScrollbar.
        /// </summary>
        private Scrollbar BuildTabScrollbar(RectTransform parent)
        {
            var barGo = new GameObject("TabScrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var barRect = (RectTransform)barGo.transform;
            barRect.SetParent(parent, worldPositionStays: false);
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(1f, 0f);
            barRect.pivot = new Vector2(0f, 0f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(0f, TabScrollbarHeight);
            barGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.07f);

            var slidingGo = new GameObject("SlidingArea", typeof(RectTransform));
            var slidingRect = (RectTransform)slidingGo.transform;
            slidingRect.SetParent(barRect, worldPositionStays: false);
            slidingRect.anchorMin = Vector2.zero;
            slidingRect.anchorMax = Vector2.one;
            slidingRect.offsetMin = Vector2.zero;
            slidingRect.offsetMax = Vector2.zero;

            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handleRect = (RectTransform)handleGo.transform;
            handleRect.SetParent(slidingRect, worldPositionStays: false);
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.offsetMin = Vector2.zero;
            handleRect.offsetMax = Vector2.zero;

            var handleImage = handleGo.GetComponent<Image>();
            handleImage.color = new Color(0.85f, 0.65f, 0.1f, 0.9f);

            var scrollbar = barGo.GetComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.LeftToRight;
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;

            return scrollbar;
        }

        /// <summary>
        /// The panel draws as an opaque full-screen overlay (TraderScreensGroupPatch.CreatePanel),
        /// so the button that opened it is covered the moment it's open - this is the only way to
        /// close the tree back to the trader dialog. Anchored to the toolbar's top-right corner and
        /// positioned by hand (see BuildToolbar) rather than a flexible-spacer layout-group trick.
        /// </summary>
        private void BuildCloseButton(RectTransform toolbar, float itemY, float itemHeight, float padding)
        {
            const float width = CloseButtonWidth;

            // Cloned from the game's own button where possible, so it carries EFT's styling, hover
            // animation and click sound rather than looking like a mod's rectangle.
            var closeRect = GameStyle.CreateButton(toolbar, "Close", CloseTree);
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-padding, itemY);
            closeRect.sizeDelta = new Vector2(width, itemHeight);
        }

        /// <summary>Closing plays the game's own menu-escape sound, so leaving the screen feels the
        /// same as leaving any other EFT screen.</summary>
        private void CloseTree()
        {
            GameStyle.PlaySound(EUISoundType.MenuEscape);
            HideGameObject();
        }

        private void BuildDetailPanel(RectTransform root)
        {
            var panelGo = new GameObject("DetailPanel", typeof(RectTransform), typeof(Image));
            _detailPanel = (RectTransform)panelGo.transform;
            _detailPanel.SetParent(root, worldPositionStays: false);
            _detailPanel.anchorMin = new Vector2(1f, 0f);
            _detailPanel.anchorMax = new Vector2(1f, 1f);
            _detailPanel.pivot = new Vector2(1f, 0.5f);
            _detailPanel.sizeDelta = new Vector2(320f, -40f);
            _detailPanel.anchoredPosition = new Vector2(0f, -20f);
            var detailBackground = panelGo.GetComponent<Image>();
            detailBackground.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(detailBackground);

            const float wikiButtonHeight = 32f;

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(_detailPanel, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 12f + wikiButtonHeight + 8f); // leaves room for the wiki button below
            textRect.offsetMax = new Vector2(-12f, -12f);

            _detailText = textGo.AddComponent<TextMeshProUGUI>();
            _detailText.fontSize = 13;
            _detailText.color = Color.white;
            _detailText.enableWordWrapping = true;
            GameStyle.Apply(_detailText);

            BuildDetailCloseButton();
            BuildWikiButton(wikiButtonHeight);

            _detailPanel.gameObject.SetActive(false);
        }

        /// <summary>Opens the quest's page on the official EFT wiki - the same Application.OpenURL
        /// call Raid Review itself uses for its own external link. URL pattern
        /// (https://escapefromtarkov.fandom.com/wiki/&lt;Name_With_Underscores&gt;) confirmed against
        /// real wiki pages, not guessed; a handful of quest names won't map exactly (disambiguation
        /// pages, punctuation) without a full id-to-slug table, which isn't worth building for this.</summary>
        /// <summary>
        /// Dismisses the detail panel. Until this existed the panel could only ever be opened -
        /// clicking one quest cost 320px of graph for the rest of the session, which is the single
        /// most obviously unfinished thing about the UI.
        /// </summary>
        private void BuildDetailCloseButton()
        {
            const float size = 22f;

            var closeGo = new GameObject("DetailClose", typeof(RectTransform), typeof(Image), typeof(Button));
            var closeRect = (RectTransform)closeGo.transform;
            closeRect.SetParent(_detailPanel, worldPositionStays: false);
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-6f, -6f);
            closeRect.sizeDelta = new Vector2(size, size);

            var background = closeGo.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.1f);
            GameStyle.ApplyPanel(background);

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(closeRect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "X";
            label.fontSize = 12;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            GameStyle.Apply(label);

            var button = closeGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(HideDetail);
        }

        private void HideDetail()
        {
            _detailNode = null;
            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
        }

        private bool IsDetailOpen => _detailPanel != null && _detailPanel.gameObject.activeSelf;

        private void BuildWikiButton(float height)
        {
            var buttonGo = new GameObject("WikiButton", typeof(RectTransform), typeof(Image), typeof(Button));
            var buttonRect = (RectTransform)buttonGo.transform;
            buttonRect.SetParent(_detailPanel, worldPositionStays: false);
            buttonRect.anchorMin = new Vector2(0f, 0f);
            buttonRect.anchorMax = new Vector2(1f, 0f);
            buttonRect.pivot = new Vector2(0.5f, 0f);
            buttonRect.anchoredPosition = new Vector2(0f, 12f);
            buttonRect.sizeDelta = new Vector2(-24f, height);

            var background = buttonGo.GetComponent<Image>();
            background.color = new Color(0.2f, 0.35f, 0.45f, 0.9f);
            GameStyle.ApplyPanel(background);

            var labelGo = new GameObject("Text", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(buttonRect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "Open Wiki Page";
            label.fontSize = 12;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            GameStyle.Apply(label);

            var button = buttonGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() =>
            {
                if (_detailNode == null) return;

                try
                {
                    var slug = Uri.EscapeDataString(_detailNode.Name.Replace(' ', '_'));
                    Application.OpenURL($"https://escapefromtarkov.fandom.com/wiki/{slug}");
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning($"QuestTree: failed to open wiki page: {ex.Message}");
                }
            });
        }

        // ------------------------------------------------------------------ graph

        /// <summary>Rebuilds the graph DATA (called only when the QuestController instance
        /// changes) and the tab strip that depends on it, then renders whichever tab is
        /// selected - defaulting back to "All" for a fresh QuestController.</summary>
        private void RebuildGraph(IEftSession session)
        {
            _graph.Build(_questController, session);
            _selectedTraderId = AllTradersId;
            BuildTabs();
            RenderSelectedTab();
        }

        private void BuildTabs()
        {
            // The tabs' own container, not _tabRow - clearing the row itself would destroy the
            // scroll content object along with the tabs.
            foreach (Transform child in _tabContent)
                Destroy(child.gameObject);
            _tabBackgrounds.Clear();
            _tabCursorX = 8f;

            CreateTabButton("All", AllTradersId);

            foreach (var traderId in OrderedTraderIds(_graph.Nodes))
            {
                var label = _graph.TraderNames.TryGetValue(traderId, out var name) ? name : traderId;

                // Completion per trader, so the row itself says where there is work left rather
                // than requiring a click into each one.
                var total = 0;
                var done = 0;
                foreach (var node in _graph.Nodes)
                {
                    if (node.TraderId != traderId) continue;
                    total++;
                    if (node.Status == ENodeStatus.Completed) done++;
                }

                CreateTabButton($"{label}  {done}/{total}", traderId);
            }

            // Appended after the traders, not next to "All": Kappa is a destination of its own
            // rather than another slice of the same tree. Settings is NOT here - it sits in the
            // toolbar beside Close, since it is not quest data at all.
            CreateTabButton("Kappa", KappaTabId);

            // Size the scrollable width to what was actually laid out, with a trailing margin
            // matching the 8px the cursor started at, and rewind to the left so a rebuild never
            // leaves the bar parked mid-way with "All" scrolled out of view.
            _tabContent.sizeDelta = new Vector2(_tabCursorX + 8f, TabRowHeight);
            _tabContent.anchoredPosition = Vector2.zero;

            UpdateTabHighlight();
        }

        /// <summary>Distinct trader ids from `nodes`, ordered by display name - shared by BuildTabs
        /// and RenderSelectedTab's "All" branch so the two can't drift out of sync.</summary>
        private List<string> OrderedTraderIds(IEnumerable<QuestNode> nodes) =>
            nodes.Select(n => n.TraderId)
                .Distinct()
                .OrderBy(id => _graph.TraderNames.TryGetValue(id, out var name) ? name : id, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private void CreateTabButton(string label, string traderId)
        {
            // Only real trader tabs get a portrait - "All", Kappa and Settings have no trader to
            // draw, and reserving the icon gutter for them would just leave their label off-centre.
            var hasIcon = traderId != AllTradersId && traderId != KappaTabId && traderId != SettingsTabId;
            // Upper bound raised from 180 once tabs started carrying "done/total" counts - at 180
            // the longest trader names ellipsized their own count away. The row scrolls, so extra
            // width costs nothing but a little more scrolling.
            var width = Mathf.Clamp(label.Length * 8f + 24f + (hasIcon ? 20f : 0f), 70f, 240f);

            var tabGo = new GameObject($"Tab_{(traderId == AllTradersId ? "All" : label)}", typeof(RectTransform), typeof(Image), typeof(Button));
            var tabRect = (RectTransform)tabGo.transform;
            tabRect.SetParent(_tabContent, worldPositionStays: false);
            tabRect.anchorMin = tabRect.anchorMax = new Vector2(0f, 1f);
            tabRect.pivot = new Vector2(0f, 1f);
            tabRect.anchoredPosition = new Vector2(_tabCursorX, -2f);
            tabRect.sizeDelta = new Vector2(width, TabHeight);
            _tabCursorX += width + 4f;

            var background = tabGo.GetComponent<Image>();
            GameStyle.ApplyPanel(background);
            _tabBackgrounds[traderId] = background;

            if (hasIcon) CreateTabIcon(tabRect, traderId);

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(tabRect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(hasIcon ? 22f : 0f, 0f);
            // Both offsets must be set on a stretched rect: offsetMax would otherwise keep the
            // RectTransform's default sizeDelta and leave the label ~100px taller than this 28px
            // tab, floating it above the row instead of sitting beside the trader portrait.
            textRect.offsetMax = Vector2.zero;
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 11;
            text.alignment = TextAlignmentOptions.Center;
            text.text = label.ToUpperInvariant();
            GameStyle.Apply(text);

            var button = tabGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => SelectTab(traderId));
        }

        /// <summary>Puts a real trader portrait next to the tab label, matching eft.monster's
        /// per-trader row treatment - Trader.GetAndAssignAvatar is the same call the game's own
        /// trader-card UI uses, so this needs no image fetching/caching of its own.</summary>
        private void CreateTabIcon(RectTransform tabRect, string traderId)
        {
            var trader = _session?.Traders?.FirstOrDefault(t => t.Id == traderId);
            if (trader == null) return;

            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.SetParent(tabRect, worldPositionStays: false);
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(4f, 0f);
            iconRect.sizeDelta = new Vector2(20f, 20f);

            // A later BuildTabs/RebuildGraph (a new session, or a tab-row refresh) destroys this
            // icon's GameObject before this load necessarily finishes. CancelOnDestroy ties a real
            // CancellationToken to that GameObject's lifetime, so the load itself stops doing
            // wasted work the moment the icon goes away, rather than only having its eventual
            // failure observed and logged after the fact.
            var cancelOnDestroy = iconGo.AddComponent<CancelOnDestroy>();

            try
            {
                trader.GetAndAssignAvatar(iconGo.GetComponent<Image>(), cancelOnDestroy.Token)
                    .ContinueWith(
                        t => Plugin.LogSource?.LogWarning($"QuestTree: trader avatar load failed for '{traderId}': {t.Exception?.GetBaseException().Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: failed to load trader avatar for tab '{traderId}': {ex.Message}");
            }
        }

        /// <summary>Cancels a CancellationTokenSource when its GameObject is destroyed - lets
        /// CreateTabIcon tie a real cancellation to the icon's lifetime instead of only observing
        /// a load that failed after the fact.</summary>
        private sealed class CancelOnDestroy : MonoBehaviour
        {
            private readonly CancellationTokenSource _cts = new();
            public CancellationToken Token => _cts.Token;

            public void OnDestroy()
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }

        /// <summary>The tabs that are not the quest graph, and so ignore search and filters.</summary>
        private static bool IsAuxTab(string tabId) => tabId == KappaTabId || tabId == SettingsTabId;

        private void SelectTab(string traderId)
        {
            if (_selectedTraderId == traderId) return;

            // Remember the tree tab we came from so the Settings button can toggle back to it.
            if (traderId == SettingsTabId && _selectedTraderId != SettingsTabId)
                _tabBeforeSettings = _selectedTraderId;

            // Opening the Kappa tab is one of the few moments the checklist can genuinely have
            // changed since it was last read, so this is where it gets re-fetched.
            if (traderId == KappaTabId) QuestDataClient.InvalidateKappa();

            // Tabs are hand-built rather than cloned, so they play the game's click sound
            // explicitly - otherwise half this screen would be silent and half would not.
            GameStyle.PlaySound(EUISoundType.ButtonClick);

            _selectedTraderId = traderId;
            RenderSelectedTab();
            UpdateTabHighlight();
        }

        private void UpdateTabHighlight()
        {
            foreach (var (traderId, background) in _tabBackgrounds)
                background.color = traderId == _selectedTraderId ? SelectedTabColor : UnselectedTabColor;

            // Not part of _tabBackgrounds - that collection is cleared and rebuilt with the tab row,
            // whereas the Settings button is built once with the toolbar and outlives it.
            if (_settingsButtonBackground != null)
            {
                _settingsButtonBackground.color =
                    _selectedTraderId == SettingsTabId ? SelectedTabColor : UnselectedTabColor;
            }
        }

        /// <summary>
        /// Renders whichever tab is selected: "All" lays out every node at once; a specific trader
        /// tab feeds the exact same layout code a single trader's node subset, so one code path
        /// handles both. Global node.Depth (computed once in QuestGraphBuilder) keeps a quest's
        /// column consistent whether viewed from "All" or its own tab. An edge only draws when both
        /// ends are in the currently rendered set, so a cross-trader prerequisite/unlock naturally
        /// never draws a broken line off the edge of a single-trader tab - see ShowDetail for where
        /// that relationship still surfaces, as text.
        /// </summary>
        /// <summary>
        /// Lays out the selected tab, then hands off to <see cref="RefreshVisibleNodes"/> to build
        /// only what is on screen. Nothing is instantiated here: the layout for several thousand
        /// quests is cheap dictionary maths, whereas building their views is not, and that split is
        /// what lets the whole tree be browsable rather than truncated.
        ///
        /// The Kappa and Settings tabs are not graphs at all, so they short-circuit to the aux panel.
        /// </summary>
        private void RenderSelectedTab()
        {
            if (_selectedTraderId == KappaTabId || _selectedTraderId == SettingsTabId)
            {
                ShowAuxTab();
                return;
            }

            ShowGraph();
            ClearGraphViews();

            var candidates = _selectedTraderId == AllTradersId
                ? _graph.Nodes
                : _graph.Nodes.Where(n => n.TraderId == _selectedTraderId).ToList();

            // Filters and search are applied BEFORE layout, not by hiding views after the fact, so
            // they genuinely reduce the work rather than just the result.
            var matching = candidates.Where(PassesFilters).Where(MatchesSearch).ToList();

            UpdateRenderNotice(matching.Count, candidates.Count);

            if (matching.Count == 0)
            {
                _layoutOrder = Array.Empty<QuestNode>();
                _edgeLayout = Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();
                return;
            }

            var y = ComputeTreeLayout(matching);

            _layout.Clear();
            foreach (var node in matching)
                _layout[node] = new Vector2(node.Depth * ColumnSpacing, -y[node] * RowSpacing);

            _layoutOrder = matching.ToArray();
            BuildEdgeLayout(matching);

            // Put the camera on the content that was just laid out. Without this the view keeps
            // whatever position it had, so searching while panned to a far corner of a 5,000-quest
            // tree showed an empty screen even though the search had matched.
            FrameContent();

            // Force the first sweep: the content transform has not moved, so Update would not fire.
            _lastContentPosition = _content.anchoredPosition;
            _lastContentScale = _content.localScale.x;
            _initialSweepFrames = InitialSweepFrames;
            RefreshVisibleNodes();
        }

        /// <summary>Precomputes every edge's two endpoints once per tab. Nodes are created with
        /// pivot (0, 0.5) (QuestNodeView.Create), so a node's layout position is already its left
        /// edge at vertical centre - the line leaves from the right edge and arrives at the left.</summary>
        private void BuildEdgeLayout(List<QuestNode> nodes)
        {
            if (!ModSettings.Ready || !ModSettings.DrawEdges.Value)
            {
                _edgeLayout = Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();
                return;
            }

            var edges = new List<(QuestNode, QuestNode, Vector2, Vector2)>();

            foreach (var node in nodes)
            {
                var fromPoint = _layout[node] + new Vector2(QuestNodeView.Width, 0f);

                foreach (var unlocked in node.Unlocks)
                {
                    // An edge whose other end was filtered out of this tab has nothing to join to.
                    if (!_layout.TryGetValue(unlocked, out var toPoint)) continue;
                    edges.Add((node, unlocked, fromPoint, toPoint));
                }
            }

            _edgeLayout = edges.ToArray();
        }

        /// <summary>
        /// Builds views for the nodes and edges inside the viewport and releases the ones that have
        /// left it, recycling both through pools.
        ///
        /// The visible region is obtained by asking Unity to convert the viewport's world corners
        /// into content-local space, rather than deriving it from anchors, pivots and scale by hand.
        /// That keeps it correct under pan, zoom and window resize with no coordinate maths of our
        /// own - which matters, because hand-rolled rect maths in this hierarchy has already cost
        /// this project two rounds of debugging.
        /// </summary>
        private void RefreshVisibleNodes()
        {
            if (_layoutOrder.Length == 0 && _edgeViews.Count == 0) return;

            var visible = GetVisibleContentRect();
            var budget = ModSettings.Ready ? ModSettings.MaxVisibleNodes.Value : DefaultMaxVisibleNodes;

            // --- nodes ---
            _nodesToRelease.Clear();
            foreach (var (node, view) in _views)
            {
                if (!IsNodeVisible(node, visible)) _nodesToRelease.Add(node);
            }

            foreach (var node in _nodesToRelease)
            {
                ReleaseNodeView(_views[node]);
                _views.Remove(node);
            }

            foreach (var node in _layoutOrder)
            {
                if (_views.Count >= budget) break;
                if (_views.ContainsKey(node)) continue;
                if (!IsNodeVisible(node, visible)) continue;

                var view = AcquireNodeView();
                ((RectTransform)view.transform).anchoredPosition = _layout[node];
                view.Bind(node, ShowDetail);
                _views[node] = view;
            }

            // --- edges ---
            _edgesToRelease.Clear();
            foreach (var (index, _) in _edgeViews)
            {
                if (!IsEdgeVisible(index, visible)) _edgesToRelease.Add(index);
            }

            foreach (var index in _edgesToRelease)
            {
                ReleaseEdge(_edgeViews[index]);
                _edgeViews.Remove(index);
            }

            for (var index = 0; index < _edgeLayout.Length; index++)
            {
                if (_edgeViews.ContainsKey(index)) continue;
                if (!IsEdgeVisible(index, visible)) continue;

                var edge = _edgeLayout[index];
                _edgeViews[index] = AcquireEdge(edge.FromPoint, edge.ToPoint);
            }

            // The guarantee that you can never get lost: if this tab has quests laid out but not one
            // of them landed on screen, the view is somewhere useless. Rather than showing an empty
            // panel with no clue which way to drag, jump to whichever quest is closest.
            if (_views.Count == 0 && _layoutOrder.Length > 0 && !_recoveringView)
            {
                _recoveringView = true;
                try
                {
                    SnapToNearestNode(visible.center);
                }
                finally
                {
                    _recoveringView = false;
                }
            }
        }

        /// <summary>Recentres on the quest nearest a point, keeping the current zoom, then rebuilds
        /// the visible set. Used only as the never-empty recovery above.</summary>
        private void SnapToNearestNode(Vector2 target)
        {
            QuestNode nearest = null;
            var nearestDistance = float.MaxValue;

            foreach (var node in _layoutOrder)
            {
                if (!_layout.TryGetValue(node, out var position)) continue;

                var distance = (position - target).sqrMagnitude;
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = node;
            }

            if (nearest == null) return;

            var viewportSize = _viewport.rect.size;
            if (viewportSize.x <= 1f || viewportSize.y <= 1f) return;

            var position2 = _layout[nearest];
            var zoom = _content.localScale.x;

            _content.anchoredPosition = new Vector2(
                viewportSize.x * 0.5f - position2.x * zoom,
                -viewportSize.y * 0.5f - position2.y * zoom);

            _lastContentPosition = _content.anchoredPosition;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: view had drifted off the graph - snapped back to '{nearest.Name}'.");

            RefreshVisibleNodes();
        }

        /// <summary>
        /// Centres the laid-out content in the viewport at a zoom that fits it, clamped to the
        /// normal zoom range. Called after every re-layout, and by the Fit control.
        ///
        /// Works in the content's own coordinate space, which node positions already use: node x
        /// runs right from 0, node y runs down from 0 (negative), so the bounds are derived from
        /// the layout rather than from any built views - the whole point being that most nodes are
        /// not built yet at this moment.
        /// </summary>
        private void FrameContent() => FrameNodes(_layoutOrder);

        /// <summary>Frames a subset of the laid-out nodes. Used both for "fit everything" and for
        /// "show me the quests I can actually work on".</summary>
        private void FrameNodes(System.Collections.Generic.IEnumerable<QuestNode> nodes)
        {
            if (nodes == null || _viewport == null || _layoutOrder.Length == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var node in nodes)
            {
                if (!_layout.TryGetValue(node, out var position)) continue;

                minX = Mathf.Min(minX, position.x);
                maxX = Mathf.Max(maxX, position.x + QuestNodeView.Width);
                minY = Mathf.Min(minY, position.y - QuestNodeView.Height * 0.5f);
                maxY = Mathf.Max(maxY, position.y + QuestNodeView.Height * 0.5f);
            }

            if (minX > maxX || minY > maxY) return;

            var viewportSize = _viewport.rect.size;
            if (viewportSize.x <= 1f || viewportSize.y <= 1f) return;

            const float margin = 60f;
            var contentWidth = Mathf.Max(1f, maxX - minX + margin * 2f);
            var contentHeight = Mathf.Max(1f, maxY - minY + margin * 2f);

            var requiredZoom = Mathf.Min(viewportSize.x / contentWidth, viewportSize.y / contentHeight);
            var zoom = Mathf.Clamp(requiredZoom, MinZoom, MaxZoom);

            _content.localScale = new Vector3(zoom, zoom, 1f);

            // _content is anchored/pivoted top-left of the viewport, so its anchoredPosition is
            // where the content origin sits.
            if (requiredZoom < MinZoom)
            {
                // The content is too big to fit even zoomed all the way out - the "All" tab on a
                // quest-modded install is roughly half a million pixels tall. Centring on the middle
                // of something that cannot fit is what strands you in empty space with no way back,
                // so anchor to the top-left corner of the content instead: depth 0 and the first
                // rows are always populated.
                _content.anchoredPosition = new Vector2(
                    margin - minX * zoom,
                    -margin - maxY * zoom);
            }
            else
            {
                var centreX = (minX + maxX) * 0.5f;
                var centreY = (minY + maxY) * 0.5f;

                _content.anchoredPosition = new Vector2(
                    viewportSize.x * 0.5f - centreX * zoom,
                    -viewportSize.y * 0.5f - centreY * zoom);
            }

            _lastContentPosition = _content.anchoredPosition;
            _lastContentScale = zoom;
        }

        /// <summary>The viewport in content-local space - the same space node positions use -
        /// padded so nodes exist slightly before they scroll into view.</summary>
        private Rect GetVisibleContentRect()
        {
            var corners = new Vector3[4];
            _viewport.GetWorldCorners(corners);

            var bottomLeft = _content.InverseTransformPoint(corners[0]);
            var topRight = _content.InverseTransformPoint(corners[2]);

            return Rect.MinMaxRect(
                Mathf.Min(bottomLeft.x, topRight.x) - VirtualizationMargin,
                Mathf.Min(bottomLeft.y, topRight.y) - VirtualizationMargin,
                Mathf.Max(bottomLeft.x, topRight.x) + VirtualizationMargin,
                Mathf.Max(bottomLeft.y, topRight.y) + VirtualizationMargin);
        }

        private bool IsNodeVisible(QuestNode node, Rect visible)
        {
            if (!_layout.TryGetValue(node, out var position)) return false;

            // pivot (0, 0.5): x runs right from the position, y is centred on it.
            return position.x + QuestNodeView.Width >= visible.xMin
                   && position.x <= visible.xMax
                   && position.y + QuestNodeView.Height * 0.5f >= visible.yMin
                   && position.y - QuestNodeView.Height * 0.5f <= visible.yMax;
        }

        /// <summary>An edge is drawn whenever its bounding box touches the viewport, so a long line
        /// crossing the screen still shows even when both of its quests are off it.</summary>
        private bool IsEdgeVisible(int index, Rect visible)
        {
            if (index < 0 || index >= _edgeLayout.Length) return false;

            var edge = _edgeLayout[index];

            return Mathf.Max(edge.FromPoint.x, edge.ToPoint.x) >= visible.xMin
                   && Mathf.Min(edge.FromPoint.x, edge.ToPoint.x) <= visible.xMax
                   && Mathf.Max(edge.FromPoint.y, edge.ToPoint.y) >= visible.yMin
                   && Mathf.Min(edge.FromPoint.y, edge.ToPoint.y) <= visible.yMax;
        }

        private QuestNodeView AcquireNodeView()
        {
            if (_nodePool.Count == 0) return QuestNodeView.Create(_content);

            var view = _nodePool.Pop();
            view.gameObject.SetActive(true);
            return view;
        }

        private void ReleaseNodeView(QuestNodeView view)
        {
            if (view == null) return;
            view.gameObject.SetActive(false);
            _nodePool.Push(view);
        }

        private RectTransform AcquireEdge(Vector2 from, Vector2 to)
        {
            var color = new Color(1f, 1f, 1f, 0.25f);

            if (_edgePool.Count == 0) return UILineConnector.Create(_content, from, to, color, 2f);

            var line = _edgePool.Pop();
            line.gameObject.SetActive(true);
            UILineConnector.Apply(line, from, to, 2f);
            return line;
        }

        private void ReleaseEdge(RectTransform line)
        {
            if (line == null) return;
            line.gameObject.SetActive(false);
            _edgePool.Push(line);
        }

        /// <summary>Returns every built node and edge to its pool. Used when the tab or search
        /// changes - the pooled objects are reused immediately by the next layout.</summary>
        private void ClearGraphViews()
        {
            foreach (var view in _views.Values) ReleaseNodeView(view);
            _views.Clear();

            foreach (var line in _edgeViews.Values) ReleaseEdge(line);
            _edgeViews.Clear();

            _layout.Clear();
            _layoutOrder = Array.Empty<QuestNode>();
            _edgeLayout = Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();
        }

        /// <summary>The Settings-tab filters. Applied before layout so hiding a category actually
        /// makes the tree smaller rather than just sparser.</summary>
        private bool PassesFilters(QuestNode node)
        {
            if (!ModSettings.Ready) return true;

            if (ModSettings.HideUnobtainable.Value && node.UnobtainableReason != null) return false;
            if (ModSettings.HideCompleted.Value && node.Status == ENodeStatus.Completed) return false;
            if (ModSettings.HideTraderless.Value && node.TraderId == QuestNode.NoTraderId) return false;

            return true;
        }

        /// <summary>
        /// A tidy-tree (dendrogram) Y position per node, in row-slots (multiply by RowSpacing to
        /// get pixels) - not pixel-perfect Reingold-Tilford (which avoids sibling-subtree overlap
        /// more rigorously), but a real branching layout instead of the stairstep this replaced.
        ///
        /// The underlying data is a DAG (a quest can have more than one prerequisite), but this is
        /// laid out as a spanning tree: each node picks one "layout parent" (PrimaryParent, below)
        /// purely to decide vertical position. Every real prerequisite/unlock edge still gets drawn
        /// in RenderSelectedTab regardless of which one was chosen as primary - this only affects
        /// where the boxes sit, not which lines connect them.
        ///
        /// X still comes from the globally-computed node.Depth (QuestGraphBuilder), unrelated to
        /// this method.
        /// </summary>
        private Dictionary<QuestNode, float> ComputeTreeLayout(IReadOnlyList<QuestNode> nodes)
        {
            var nodeSet = new HashSet<QuestNode>(nodes);
            var children = nodes.ToDictionary(n => n, _ => new List<QuestNode>());
            var hasParent = new HashSet<QuestNode>();

            foreach (var node in nodes)
            {
                QuestNode parent = null;
                foreach (var prereqId in node.PrerequisiteIds)
                {
                    if (!_graph.NodesById.TryGetValue(prereqId, out var candidate)) continue;
                    if (!nodeSet.Contains(candidate)) continue; // outside the currently rendered tab

                    if (parent == null)
                    {
                        parent = candidate;
                        continue;
                    }

                    // Prefer a prerequisite in the same trader (keeps a trader's own chain
                    // visually together); among equally-same/different-trader candidates, prefer
                    // the shallower one.
                    var candidateSameTrader = candidate.TraderId == node.TraderId;
                    var parentSameTrader = parent.TraderId == node.TraderId;
                    if (candidateSameTrader && !parentSameTrader) parent = candidate;
                    else if (candidateSameTrader == parentSameTrader && candidate.Depth < parent.Depth) parent = candidate;
                }

                if (parent != null)
                {
                    hasParent.Add(node);
                    children[parent].Add(node);
                }
            }

            var y = new Dictionary<QuestNode, float>();
            var visiting = new HashSet<QuestNode>();
            var nextLeafSlot = 0f;

            float AssignY(QuestNode node)
            {
                if (y.TryGetValue(node, out var cached)) return cached;

                // Guards a malformed/modded cycle - must terminate even if the prerequisite data
                // doesn't form a clean DAG.
                if (!visiting.Add(node)) return nextLeafSlot;

                var kids = children[node];
                float result;
                if (kids.Count == 0)
                {
                    result = nextLeafSlot;
                    nextLeafSlot += 1f;
                }
                else
                {
                    var sum = 0f;
                    foreach (var kid in kids) sum += AssignY(kid);
                    result = sum / kids.Count;
                }

                visiting.Remove(node);
                return y[node] = result;
            }

            // Roots (nothing claimed as their primary parent) first, ordered by trader/name so
            // unrelated trees don't interleave arbitrarily; AssignY recurses into the rest.
            var roots = nodes.Where(n => !hasParent.Contains(n))
                .OrderBy(n => n.TraderName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
                AssignY(root);

            // Anything a cycle guard prevented AssignY from reaching still needs a slot rather
            // than being silently dropped from the layout.
            foreach (var node in nodes)
                if (!y.ContainsKey(node)) y[node] = nextLeafSlot++;

            return y;
        }

        /// <summary>Whether a node survives the current search box contents. Matched against the
        /// quest name and its trader, so "prapor" narrows to one trader's chain just as a quest
        /// name does.</summary>
        private bool MatchesSearch(QuestNode node)
        {
            var search = _searchFilter.Trim();
            if (search.Length == 0) return true;

            return node.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                   || (node.TraderName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        /// <summary>Reports how much of the tab the current filters and search are showing. Since
        /// virtualization removed the render cap this is a plain count rather than a truncation
        /// warning - what is laid out is what you can reach by panning.</summary>
        private void UpdateRenderNotice(int matchingCount, int tabTotal)
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

        private bool IsSearchFocused() => _searchField != null && _searchField.isFocused;

        private void FocusSearch()
        {
            if (_searchField == null) return;
            _searchField.Select();
            _searchField.ActivateInputField();
        }

        /// <summary>Frames whatever you can actually act on right now - in progress first, else
        /// available. On a 5,000-quest tree this is the difference between the tree being a
        /// reference and being usable.</summary>
        private void FrameMyQuests()
        {
            var mine = _layoutOrder.Where(n => n.Status == ENodeStatus.Active).ToList();
            if (mine.Count == 0)
                mine = _layoutOrder.Where(n => n.Status == ENodeStatus.Available).ToList();

            GameStyle.PlaySound(EUISoundType.ButtonClick);

            if (mine.Count > 0)
            {
                FrameNodes(mine);
                return;
            }

            // Nothing started and nothing available - which is the normal state on a fresh profile,
            // and was also true on a server without the companion mod where every quest reads as
            // Locked. Doing nothing there just looks broken, so fall back to the start of the tree
            // rather than leaving the player where they were.
            FrameContent();

            if (_renderNotice != null)
            {
                _renderNotice.text =
                    "<color=#D9A61A>No quests started or available - showing the start of the tree.</color>";
            }
        }

        /// <summary>Swaps the graph viewport in and the Kappa/Settings surface out.</summary>
        private void ShowGraph()
        {
            if (_auxPanel != null) _auxPanel.gameObject.SetActive(false);
            if (_viewport != null) _viewport.gameObject.SetActive(true);
        }

        /// <summary>Builds whichever non-graph tab is selected into the aux panel. Rebuilt on every
        /// selection rather than cached: the Kappa checklist reflects the current stash, so a stale
        /// one would be worse than none.</summary>
        private void ShowAuxTab()
        {
            ClearGraphViews();

            if (_viewport != null) _viewport.gameObject.SetActive(false);
            if (_auxPanel == null) return;

            _auxPanel.gameObject.SetActive(true);
            _auxContent.anchoredPosition = Vector2.zero;

            foreach (Transform child in _auxContent)
                Destroy(child.gameObject);

            var height = _selectedTraderId == KappaTabId
                ? KappaView.Build(_auxContent, _graph, () =>
                {
                    QuestDataClient.InvalidateKappa();
                    RenderSelectedTab();
                })
                : SettingsView.Build(_auxContent, () =>
                {
                    // SettingsView has already re-read the file; the flag is baked into each node
                    // at build time, so the graph needs telling before anything is redrawn.
                    _graph.RefreshKappaFlags();
                    RenderSelectedTab();
                });

            _auxContent.sizeDelta = new Vector2(0f, height);

            if (_renderNotice != null)
                _renderNotice.text = _selectedTraderId == KappaTabId ? "Kappa container progress" : "Settings";

            if (_detailPanel != null) _detailPanel.gameObject.SetActive(false);
        }

        private void ShowDetail(QuestNode node)
        {
            _detailNode = node;

            var lines = new List<string>
            {
                $"<b>{node.Name}</b>",
                node.TraderName,
                node.Level > 0 ? $"Level {node.Level}" : null,
                node.IsKappaRequired ? "<color=#D9A61A>Kappa required</color>" : null,
                // Faction- and edition-locked quests are shown rather than hidden, so this is what
                // stops one reading as a bug in the tree.
                node.UnobtainableReason != null ? $"<color=#C86464>{node.UnobtainableReason}</color>" : null,
                ""
            };

            if (node.PrerequisiteIds.Count > 0)
            {
                // Named here rather than drawn as a line, since a prerequisite from another trader
                // won't have a node in whatever tab is currently rendered (RenderSelectedTab only
                // draws edges within the current tab's node set) - this is the one place that
                // relationship still surfaces when it crosses tabs.
                lines.Add("<b>Requires</b>");
                foreach (var prereqId in node.PrerequisiteIds)
                {
                    lines.Add(_graph.NodesById.TryGetValue(prereqId, out var prereq)
                        ? $"{prereq.Name} ({prereq.TraderName})"
                        : prereqId);
                }
                lines.Add("");
            }

            // Available for a locked quest too, not just an accepted one: the objective text
            // arrives with the companion mod's payload rather than being read off a live Quest
            // instance the game only creates once the quest is unlocked.
            var objectives = node.NecessaryObjectives.Select(o => o.Text).ToList();
            if (objectives.Count > 0)
            {
                lines.Add("<b>Objectives</b>");
                lines.AddRange(objectives);
                lines.Add("");
            }

            var rewards = node.Rewards.Select(FormatReward).Where(r => !string.IsNullOrEmpty(r)).ToList();
            if (rewards.Count > 0)
            {
                lines.Add("<b>Rewards</b>");
                lines.AddRange(rewards);
                lines.Add("");
            }

            if (node.Unlocks.Count > 0)
            {
                lines.Add("<b>Unlocks</b>");
                lines.AddRange(node.Unlocks.Select(u => u.TraderId == node.TraderId ? u.Name : $"{u.Name} ({u.TraderName})"));
            }

            _detailText.text = string.Join("\n", lines.Where(l => l != null));
            _detailPanel.gameObject.SetActive(true);
        }

        /// <summary>Turns one payload reward into a display line. Trader-scoped rewards are named
        /// from the live session's trader list rather than from the payload, so a modded trader
        /// reads correctly without the server mod having to know about it.</summary>
        private string FormatReward(RewardDto reward)
        {
            if (reward == null) return null;

            var trader = !string.IsNullOrEmpty(reward.TraderId) &&
                         _graph.TraderNames.TryGetValue(reward.TraderId, out var traderName)
                ? traderName
                : reward.TraderId;

            switch (reward.Type)
            {
                case "Experience":
                    return $"+{reward.Value:N0} XP";

                case "TraderStanding":
                    return string.IsNullOrEmpty(trader)
                        ? $"Reputation {reward.Value:+0.00;-0.00}"
                        : $"{trader} Rep {reward.Value:+0.00;-0.00}";

                case "TraderUnlock":
                    return string.IsNullOrEmpty(trader) ? "Unlocks a trader" : $"Unlocks {trader}";

                case "Item":
                    if (string.IsNullOrEmpty(reward.Name)) return null;
                    return reward.Value >= 2 ? $"{reward.Value:N0}x {reward.Name}" : reward.Name;

                case "Skill":
                    return string.IsNullOrEmpty(reward.Name) ? null : $"{reward.Name} +{reward.Value:N0}";

                case "AssortmentUnlock":
                    return string.IsNullOrEmpty(trader) ? "Unlocks a new trader offer" : $"Unlocks a new {trader} offer";

                default:
                    // Unknown/rare reward types (StashRows, Achievement, ...) still say something
                    // rather than silently vanishing, but only when there is a name worth showing.
                    return string.IsNullOrEmpty(reward.Name) ? null : $"{reward.Type}: {reward.Name}";
            }
        }

        /// <summary>Drag-to-pan, scroll-to-zoom. Kept as a small nested component rather than a
        /// third-party UI package - see UILineConnector for the same reasoning.</summary>
        private sealed class PanZoomHandler : MonoBehaviour, IDragHandler, IScrollHandler
        {
            private RectTransform _content;
            private float _minZoom;
            private float _maxZoom;
            private float _zoomSpeed;

            public void Init(RectTransform content, float minZoom, float maxZoom, float zoomSpeed)
            {
                _content = content;
                _minZoom = minZoom;
                _maxZoom = maxZoom;
                _zoomSpeed = zoomSpeed;
            }

            public void OnDrag(PointerEventData eventData)
            {
                _content.anchoredPosition += eventData.delta / _content.localScale.x;
            }

            public void OnScroll(PointerEventData eventData)
            {
                var scale = Mathf.Clamp(_content.localScale.x + eventData.scrollDelta.y * _zoomSpeed, _minZoom, _maxZoom);
                _content.localScale = new Vector3(scale, scale, 1f);
            }
        }
    }
}
