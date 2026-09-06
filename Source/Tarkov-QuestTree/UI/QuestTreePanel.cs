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
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The Quest Tree overlay: a pannable/zoomable canvas of every quest in the game, laid out as
    /// a layered DAG (column = prerequisite depth, row = trader lane) with lines drawn from each
    /// quest to what it unlocks. Built entirely from runtime UI primitives, toggled open by the
    /// taskbar button added by <see cref="QuestTree.Patches.MenuTaskBarAwakePatch"/> and closed by
    /// the Close button in its own toolbar (see <see cref="QuestToolbar"/>) - it draws as an opaque
    /// full-screen overlay, so it cannot rely on anything outside itself to close it.
    ///
    /// Status is live: QuestController.OnConditionalStatusChanged - the same event the vanilla
    /// task list subscribes to - drives a cheap recolor pass with no polling and no rebuild of the
    /// graph structure itself.
    ///
    /// This class is the shell only. The graph surface, the toolbar and the detail panel each live
    /// in their own file (<see cref="QuestGraphView"/>, <see cref="QuestToolbar"/>,
    /// <see cref="QuestDetailPanel"/>); what remains here is lifecycle, the tab row, the aux
    /// (Kappa/Settings) surface, the loading notice, and the coordination between the three.
    /// </summary>
    internal sealed class QuestTreePanel : UIElement
    {
        /// <summary>Tall enough for a 28px tab plus the horizontal scrollbar beneath it - the tab
        /// row overflows the screen once every trader has a tab, so the bar is what makes it
        /// obvious the row scrolls at all.</summary>
        private const float TabRowHeight = 40f;

        private const float TabHeight = 28f;
        private const float TabScrollbarHeight = 5f;

        /// <summary>Sentinel for the "All" tab - not a real trader id, so it can't collide with one.</summary>
        private const string AllTradersId = "";

        /// <summary>Sentinels for the two non-graph tabs, appended after the traders. Prefixed the
        /// same way QuestNode.NoTraderId is so they can never collide with a real trader id.</summary>
        private const string KappaTabId = "__kappa__";
        private const string ItemsTabId = "__items__";
        private const string MapsTabId = "__maps__";
        private const string DoNextTabId = "__donext__";
        private const string SettingsTabId = "__settings__";

        private static readonly Color SelectedTabColor = new(0.35f, 0.35f, 0.2f, 0.95f);
        private static readonly Color UnselectedTabColor = new(1f, 1f, 1f, 0.08f);

        private readonly QuestGraphBuilder _graph = new();
        private readonly Dictionary<string, Image> _tabBackgrounds = new();

        // The three pieces this shell coordinates. All plain classes rather than MonoBehaviours -
        // see each one's class comment for why - so they are created with the panel and wired up in
        // BuildShell. Each guards its own not-yet-built state, which is what lets Update call into
        // them without caring whether BuildShell has run.
        private readonly QuestToolbar _toolbar = new();
        private readonly QuestGraphView _graphView = new();
        private readonly QuestDetailPanel _detail = new();

        private QuestController _questController;
        private IEftSession _session;
        private RectTransform _auxPanel;
        private RectTransform _auxContent;
        private RectTransform _loadingPanel;
        private RectTransform _introPanel;
        private TMP_Text _loadingLabel;
        private RectTransform _tabRow;
        private RectTransform _tabContent;

        /// <summary>Which tree tab to return to when Settings is toggled back off.</summary>
        private string _tabBeforeSettings = AllTradersId;
        private string _selectedTraderId = AllTradersId;
        private bool _builtShell;

        /// <summary>Set if BuildShell threw. The shell is then half-built, and Destroy is
        /// end-of-frame so it cannot be cleanly torn down and retried in the same click - the
        /// panel refuses to open instead of stacking a second shell on the first.</summary>
        private bool _shellBroken;
        private float _tabCursorX;

        public void OnAwake()
        {
            if (_builtShell || _shellBroken) return;

            try
            {
                BuildShell();
                _builtShell = true; // only once the whole shell exists
            }
            catch
            {
                _shellBroken = true;
                throw;
            }
        }

        private void HandleSettingsChanged()
        {
            if (!_builtShell || _questController == null) return;

            try
            {
                // Pooled views carry the geometry they were built with, so a density change has to
                // throw them away rather than recycle them - otherwise half the tree would render
                // at the old size.
                _graphView.DiscardViewPools();
                RenderSelectedTab();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to re-render after a settings change: {ex}");
            }
        }

        /// <summary>Called by the taskbar toggle handler
        /// (<see cref="QuestTree.Patches.MenuTaskBarAwakePatch"/>.ShowPanel, in
        /// Patches/MenuTaskBarPatch.cs) every time the Quest Tree button is used to open the panel.
        /// Cheap to call repeatedly - the graph is only rebuilt from scratch when the
        /// QuestController instance actually changes (e.g. a fresh session).</summary>
        public void Show(QuestController questController, IEftSession session)
        {
            OnAwake();

            if (_shellBroken)
            {
                Plugin.LogSource?.LogWarning("QuestTree: the panel failed to build earlier and will not open - see the error above.");
                return;
            }

            ShowGameObject();
            GameStyle.PlaySound(EUISoundType.MenuInspectorWindowOpen);

            // Settings are editable from the in-panel Settings tab AND from BepInEx's F12 menu, so
            // the panel reacts to the value changing rather than to either editor in particular.
            //
            // Subscribed here rather than in OnAwake, paired with the unsubscribe in Close. OnAwake
            // runs once and then early-returns forever on _builtShell, so subscribing there left an
            // asymmetric pair: Close would unsubscribe and nothing would ever re-subscribe. In
            // practice that is currently unreachable - nothing in this mod calls Close (the Close
            // button and the taskbar toggle both use HideGameObject), so it was a latent trap for
            // whoever wired Close up rather than a bug anyone could hit. Fixed on that basis.
            //
            // Unsubscribe first: += on an already-subscribed delegate would invoke it twice.
            ModSettings.Changed -= HandleSettingsChanged;
            ModSettings.Changed += HandleSettingsChanged;

            // Both are profile-scoped and both can have moved while the panel was shut.
            QuestDataClient.InvalidateKappa();
            QuestDataClient.InvalidateProfile();
            _session = session; // kept for trader-avatar lookups on tab icons, independent of a graph rebuild

            // Reads as "a different QuestController OR the first Show after a full teardown":
            // Close nulls _questController, so this is also true the next time the panel is opened
            // after that. Benign today, because Close is UIElement's destroy path and neither the
            // Close button nor the taskbar toggle uses it (both go through HideGameObject) - but
            // this is not strictly "a new profile", and the refetch below fires in both cases.
            if (!ReferenceEquals(_questController, questController))
            {
                // The new controller is adopted by RebuildGraphDeferred once the build has
                // actually succeeded, not here. Hiding the panel deactivates its GameObject, which
                // kills the coroutine for good - adopted up front, a panel hidden mid-load came
                // back "already built" for this controller and sat on "Loading quests..." forever.
                // Left unadopted, the next Show simply starts the build again.
                //
                // A second Show cannot race the pending build: the taskbar toggle only calls Show
                // on a hidden panel, and hiding is what kills the build.
                if (_questController != null)
                    _questController.OnConditionalStatusChanged -= HandleStatusChanged;
                _questController = null;

                // Anything fetched for the previous controller has to go. Those caches are static
                // and were outliving the game session: connecting once to a server without the
                // companion mod latched the "already tried" flag, and reconnecting to a server that
                // DID have it still showed only already-unlocked quests until the game was
                // restarted. Must happen before the rebuild below, which is what re-fetches.
                //
                // Note the cost: a new QuestController is not necessarily a new profile or server -
                // the game plausibly creates one on every return from a raid - so this can force a
                // multi-MB refetch and reparse more often than "you changed servers" suggests. That
                // is accepted deliberately: it is on the UI thread but behind the loading notice,
                // and the graph rebuild it accompanies was happening anyway.
                QuestDataClient.ResetSession();

                // Deferred by a frame so the loading notice actually paints first. Building the
                // graph means fetching several MB, parsing it, and laying out thousands of quests,
                // all on the UI thread - done inline it reads as the game having frozen.
                ShowLoading(true);
                StartCoroutine(RebuildGraphDeferred(questController, session));
            }
        }

        /// <summary>Waits for the loading notice to render, then does the expensive build. Unity
        /// paints between frames, so a single yield is enough for the notice to be on screen before
        /// the thread is tied up.</summary>
        private System.Collections.IEnumerator RebuildGraphDeferred(
            QuestController questController, IEftSession session)
        {
            yield return null;
            yield return null;

            // A failed build leaves the loading surface up: it is where the error message was just
            // written, and hiding it showed an empty tree with no explanation.
            if (!TryRebuildGraph(questController, session)) yield break;

            // Adopted only now - see Show for why.
            _questController = questController;
            if (_questController != null)
                _questController.OnConditionalStatusChanged += HandleStatusChanged;

            ShowLoading(false);

            // Only after the tree is actually up - showing the controls hint over a loading screen
            // would explain how to drive something that is not there yet.
            if (ModSettings.Ready && !ModSettings.HasSeenIntro.Value) ShowIntro(true);
        }

        /// <summary>Separate from the coroutine because C# forbids yielding inside a try/catch that
        /// has a catch clause - and this call must not be allowed to throw into the taskbar button
        /// handler that started it.</summary>
        private bool TryRebuildGraph(QuestController questController, IEftSession session)
        {
            try
            {
                RebuildGraph(questController, session);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to build the quest tree: {ex}");
                if (_loadingLabel != null)
                    _loadingLabel.text = "Could not build the quest tree - see the BepInEx log.";
                return false;
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
                // Layered, outermost last: the controls hint sits over everything, then the quest
                // detail, then the tree itself. Each press peels off one layer.
                if (_introPanel != null && _introPanel.gameObject.activeSelf) ShowIntro(false);
                else if (_detail.IsOpen) _detail.Hide();
                else HideGameObject();
                return;
            }

            // Shortcuts. Deliberately only while the panel has focus and the search box does not -
            // typing "f" into search must not re-frame the view.
            if (!_toolbar.IsSearchFocused())
            {
                if (Input.GetKeyDown(KeyCode.F)) _graphView.FrameContent();
                if (Input.GetKeyDown(KeyCode.M)) _graphView.FrameMyQuests();
                if (Input.GetKeyDown(KeyCode.Slash)) _toolbar.FocusSearch();
            }

            // Virtualization is driven from this Update rather than from the graph view's own
            // component, so it runs after the shortcuts above (which can themselves move the view)
            // and in a defined order. See QuestGraphView.Tick.
            _graphView.Tick();
        }

        public override void Close()
        {
            Unsubscribe();
            base.Close();
        }

        /// <summary>Unity's own teardown. Close is UIElement's destroy path and nothing in this mod
        /// calls it (see Show), so the menu being torn down after a raid destroyed this panel with
        /// both subscriptions still live: QuestController went on invoking HandleStatusChanged on a
        /// dead panel, and the static ModSettings.Changed pinned the panel and its whole graph for
        /// the life of the process, one more per menu visit.</summary>
        public void OnDestroy()
        {
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (_questController != null)
            {
                _questController.OnConditionalStatusChanged -= HandleStatusChanged;
                _questController = null;
            }

            ModSettings.Changed -= HandleSettingsChanged;
        }

        private void HandleStatusChanged()
        {
            // Invoked straight off QuestController's event, in the same invocation list as the
            // vanilla task list - an exception here aborts every subscriber after this one, in the
            // middle of a hand-in. Nothing that can go wrong in a recolor is worth that.
            try
            {
                // A quest turning in can hand over Collector items, and it also moves level, trader
                // standing, objective counters and what is still locked - so both caches are stale.
                QuestDataClient.InvalidateKappa();
                QuestDataClient.InvalidateProfile();

                _graph.RefreshStatuses();
                _graphView.RefreshNodeStatuses();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to refresh quest statuses: {ex}");
            }
        }

        // ------------------------------------------------------------------ shell

        private void BuildShell()
        {
            var root = (RectTransform)transform;

            // Build order is also sibling order, which is what decides what draws over what: the
            // graph first, then the aux and loading surfaces over it, then the chrome, and the
            // detail panel last of all.
            _graphView.Build(root, QuestToolbar.Height + TabRowHeight, _graph, _toolbar, _detail.Show);
            _toolbar.BuildLegend(_graphView.Viewport);

            BuildAuxPanel(root);
            BuildLoadingPanel(root);

            _toolbar.Build(
                root,
                _graph,
                isAuxTabSelected: () => IsAuxTab(_selectedTraderId),
                onSearchChanged: RenderSelectedTab,
                frameMyQuests: _graphView.FrameMyQuests,
                frameContent: _graphView.FrameContent,
                viewButtons: ViewButtons,
                onViewSelected: SelectView,
                closeTree: CloseTree,
                showIntro: () => ShowIntro(true));

            BuildTabRow(root);
            _detail.Build(root, _graph);
            BuildIntroPanel(root);
        }

        /// <summary>
        /// The controls hint. Nothing else on screen says the graph pans, that the tab row scrolls,
        /// or that there are shortcuts at all - none of which is discoverable by looking.
        ///
        /// Shown once per install (ModSettings.HasSeenIntro) and thereafter only on demand from the
        /// "?" button, so it informs the first time without nagging afterwards.
        /// </summary>
        private void BuildIntroPanel(RectTransform root)
        {
            var panelGo = new GameObject("IntroPanel", typeof(RectTransform), typeof(Image));
            _introPanel = (RectTransform)panelGo.transform;
            _introPanel.SetParent(root, worldPositionStays: false);
            _introPanel.anchorMin = _introPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _introPanel.pivot = new Vector2(0.5f, 0.5f);
            _introPanel.sizeDelta = new Vector2(470f, 320f);
            _introPanel.anchoredPosition = Vector2.zero;

            var background = panelGo.GetComponent<Image>();
            background.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(background);

            var y = AuxLayout.Padding;
            AuxLayout.AddHeading(_introPanel, ref y, "Getting around");
            AuxLayout.AddText(_introPanel, ref y, "Drag to pan  ·  mouse wheel to zoom", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "The trader tabs scroll - wheel or drag them too", 20f, 12);
            AuxLayout.AddSpacer(ref y, 8f);
            AuxLayout.AddText(_introPanel, ref y, "<b>F</b>  fit the whole tab on screen", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>M</b>  jump to the quests you can work on", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>/</b>  search quests and traders", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>Esc</b>  close the quest detail, then the tree", 20f, 12);
            AuxLayout.AddSpacer(ref y, 8f);

            // The views are the least discoverable thing here - they are buttons in the corner, and
            // nothing about the tree suggests an item watchlist exists at all.
            AuxLayout.AddText(_introPanel, ref y,
                "<b>Top right:</b> Do next, Maps, Items, Kappa, Settings", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y,
                "<color=#FFFFFF80>Do next ranks what you are closest to finishing;</color>", 18f, 11);
            AuxLayout.AddText(_introPanel, ref y,
                "<color=#FFFFFF80>Items is the \"do not sell that\" list</color>", 18f, 11);

            AuxLayout.AddSpacer(ref y, 10f);
            AuxLayout.AddButton(_introPanel, ref y, "Got it", () => ShowIntro(false));

            _introPanel.gameObject.SetActive(false);
        }

        /// <summary>Shows or dismisses the hint. Dismissing records it, so it does not reappear on
        /// its own; the "?" button reopens it without clearing that.</summary>
        private void ShowIntro(bool visible)
        {
            if (_introPanel == null) return;

            if (!visible && ModSettings.Ready) ModSettings.HasSeenIntro.Value = true;

            _introPanel.gameObject.SetActive(visible);
            if (visible) _introPanel.SetAsLastSibling();
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
            _auxPanel.offsetMax = new Vector2(0f, -(QuestToolbar.Height + TabRowHeight));
            // Opaque, not the 25% black the graph viewport uses. ShowAuxTab hides the graph, so
            // there is nothing behind this but the game's own stash screen - at a quarter alpha the
            // inventory grid and weight readout showed straight through the map.
            var panelBackground = panelGo.GetComponent<Image>();
            panelBackground.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(panelBackground);

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

        /// <summary>The row of per-trader tabs (plus "All") below the toolbar - see BuildTabs,
        /// which (re)populates it whenever the graph data changes. Positioned manually (an
        /// x-cursor tracked in _tabCursorX, reset at the start of each BuildTabs pass) rather than
        /// a HorizontalLayoutGroup - see the comment on QuestToolbar.Build for why.</summary>
        private void BuildTabRow(RectTransform root)
        {
            var tabRowGo = new GameObject(
                "TabRow", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            _tabRow = (RectTransform)tabRowGo.transform;
            _tabRow.SetParent(root, worldPositionStays: false);
            _tabRow.anchorMin = new Vector2(0f, 1f);
            _tabRow.anchorMax = new Vector2(1f, 1f);
            _tabRow.pivot = new Vector2(0f, 1f);
            _tabRow.anchoredPosition = new Vector2(0f, -QuestToolbar.Height);
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

        /// <summary>Closing plays the game's own menu-escape sound, so leaving the screen feels the
        /// same as leaving any other EFT screen.</summary>
        private void CloseTree()
        {
            GameStyle.PlaySound(EUISoundType.MenuEscape);
            HideGameObject();
        }

        // ------------------------------------------------------------------ graph

        /// <summary>Rebuilds the graph DATA (called only when the QuestController instance
        /// changes) and the tab strip that depends on it, then renders whichever tab is
        /// selected - defaulting back to "All" for a fresh QuestController.</summary>
        private void RebuildGraph(QuestController questController, IEftSession session)
        {
            _graph.Build(questController, session);
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

            // Loyalty comes from the profile payload, which the client was fetching and ignoring.
            // It is what makes a "requires LL3" lock reason mean something - LL3 is meaningless
            // without knowing you are LL2.
            var loyalty = BuildLoyaltyLookup();

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

                var loyaltySuffix = loyalty.TryGetValue(traderId, out var level) && level > 0
                    ? $"  LL{level}"
                    : "";

                CreateTabButton($"{label}  {done}/{total}{loyaltySuffix}", traderId);
            }

            // Maps / Items / Kappa / Settings are NOT here - they are whole views rather than a
            // slice of the quest graph, so they live together in the toolbar beside Close. The tab
            // row is only ever "which quests am I looking at".

            // Size the scrollable width to what was actually laid out, with a trailing margin
            // matching the 8px the cursor started at, and rewind to the left so a rebuild never
            // leaves the bar parked mid-way with "All" scrolled out of view.
            _tabContent.sizeDelta = new Vector2(_tabCursorX + 8f, TabRowHeight);
            _tabContent.anchoredPosition = Vector2.zero;

            UpdateTabHighlight();
        }

        /// <summary>Trader id -> loyalty level, or empty when the server half is unavailable. Built
        /// once per tab rebuild rather than per trader, since it is one cached fetch either way.</summary>
        private static Dictionary<string, int> BuildLoyaltyLookup()
        {
            var lookup = new Dictionary<string, int>();

            var profile = QuestDataClient.GetProfile();
            if (profile?.Traders == null) return lookup;

            foreach (var trader in profile.Traders)
            {
                if (trader == null || string.IsNullOrEmpty(trader.Id)) continue;
                lookup[trader.Id] = trader.LoyaltyLevel;
            }

            return lookup;
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
            var hasIcon = traderId != AllTradersId && !IsAuxTab(traderId);
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
        /// <summary>The non-graph views, in the order they appear right-to-left from Close. Kappa
        /// sits innermost because it is the one you open most often.</summary>
        private static readonly (string TabId, string Label)[] ViewButtons =
        {
            (SettingsTabId, "Settings"),
            (KappaTabId, "Kappa"),
            (ItemsTabId, "Items"),
            (MapsTabId, "Maps"),
            (DoNextTabId, "Do next")
        };

        /// <summary>Selecting a whole view toggles: clicking the one you are already on returns you
        /// to the tree tab you came from, rather than stranding you with no way back.</summary>
        private void SelectView(string tabId) =>
            SelectTab(_selectedTraderId == tabId ? _tabBeforeSettings : tabId);

        private static bool IsAuxTab(string tabId) =>
            tabId == KappaTabId || tabId == SettingsTabId || tabId == ItemsTabId ||
            tabId == MapsTabId || tabId == DoNextTabId;

        /// <summary>What the toolbar says while a view is open. Previously this was a two-way test
        /// that labelled everything that was not Kappa as "Settings" - which was three of the five
        /// views.</summary>
        private static string NoticeForView(string tabId) => tabId switch
        {
            KappaTabId => "Kappa container progress",
            ItemsTabId => "Items your quests still want",
            MapsTabId => "What you can do on each map",
            DoNextTabId => "Closest to finishing first",
            _ => "Settings"
        };

        private void SelectTab(string traderId)
        {
            if (_selectedTraderId == traderId) return;

            // Remember the tree tab we came from so toggling a view button off returns there.
            if (IsAuxTab(traderId) && !IsAuxTab(_selectedTraderId))
                _tabBeforeSettings = _selectedTraderId;

            // Opening the Kappa tab is one of the few moments the checklist can genuinely have
            // changed since it was last read, so this is where it gets re-fetched.
            if (traderId == KappaTabId) QuestDataClient.InvalidateKappa();

            // The watchlist reads the stash, which moves every raid, so entering it re-reads rather
            // than showing whatever was cached when the panel opened.
            // Both read the stash, which moves every raid, so entering them re-reads rather than
            // showing whatever was cached when the panel opened.
            if (traderId == ItemsTabId || traderId == DoNextTabId) QuestDataClient.InvalidateProfile();

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

            _toolbar.SetViewHighlight(_selectedTraderId, SelectedTabColor, UnselectedTabColor);
        }

        /// <summary>
        /// Picks the nodes the selected tab covers and hands them to the graph view to lay out:
        /// "All" passes every node, a specific trader tab passes that trader's subset, and the same
        /// layout code handles both.
        ///
        /// The views are not graphs at all, so they short-circuit to the aux panel. That test asks
        /// IsAuxTab rather than naming ids: it used to name Kappa and Settings directly, and when
        /// Do next, Maps and Items were added nobody updated it - so selecting one of them rendered
        /// a graph filtered to a trader id no quest has, and drew an empty panel. One predicate, so
        /// a sixth view cannot bring that back.
        /// </summary>
        private void RenderSelectedTab()
        {
            if (IsAuxTab(_selectedTraderId))
            {
                ShowAuxTab();
                return;
            }

            ShowGraph();

            var candidates = _selectedTraderId == AllTradersId
                ? _graph.Nodes
                : _graph.Nodes.Where(n => n.TraderId == _selectedTraderId).ToList();

            _graphView.Render(candidates);
        }

        /// <summary>Swaps the graph viewport in and the Kappa/Settings surface out.</summary>
        private void ShowGraph()
        {
            if (_auxPanel != null) _auxPanel.gameObject.SetActive(false);
            _graphView.SetVisible(true);
        }

        /// <summary>Builds whichever non-graph tab is selected into the aux panel. Rebuilt on every
        /// selection rather than cached: the Kappa checklist reflects the current stash, so a stale
        /// one would be worse than none.</summary>
        private void ShowAuxTab()
        {
            _graphView.ClearGraphViews();
            _graphView.SetVisible(false);

            if (_auxPanel == null) return;

            _auxPanel.gameObject.SetActive(true);
            _auxContent.anchoredPosition = Vector2.zero;

            // Detached before destroying, not just destroyed: Destroy is deferred to the end of the
            // frame, so the outgoing rows would otherwise draw over the incoming ones for a frame.
            // That was invisible when this ran once per tab click, but the Maps dropdown rebuilds
            // the view on every open, close and select, where the ghost frame reads as flicker.
            // Walked backwards by index rather than with foreach: detaching a child shifts every
            // later sibling down, so enumerating forwards would skip every other row.
            for (var i = _auxContent.childCount - 1; i >= 0; i--)
            {
                var child = _auxContent.GetChild(i);
                child.SetParent(null);
                Destroy(child.gameObject);
            }

            var height = _selectedTraderId == DoNextTabId
                ? DoNextView.Build(_auxContent, _graph, () =>
                {
                    QuestDataClient.InvalidateProfile();
                    RenderSelectedTab();
                })
                : _selectedTraderId == MapsTabId
                ? MapView.Build(_auxContent, _graph, RenderSelectedTab)
                : _selectedTraderId == ItemsTabId
                ? ItemWatchlistView.Build(_auxContent, _graph, () =>
                {
                    QuestDataClient.InvalidateProfile();
                    RenderSelectedTab();
                })
                : _selectedTraderId == KappaTabId
                ? KappaView.Build(_auxContent, _graph, () =>
                {
                    QuestDataClient.InvalidateKappa();
                    QuestDataClient.InvalidateProfile();
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

            // Only now that the content has its real height can a scroll be clamped correctly - the
            // rewind to the top above happens before the view is even built. The Maps tab uses this
            // to keep a quest opened from its map pin on screen, which it would otherwise not be
            // whenever the quest sits far enough down the list.
            if (_selectedTraderId == MapsTabId && MapView.TryConsumePendingScroll(out var scrollTo))
            {
                var maxScroll = Mathf.Max(0f, height - _auxPanel.rect.height);
                _auxContent.anchoredPosition = new Vector2(0f, Mathf.Clamp(scrollTo, 0f, maxScroll));
            }

            _toolbar.SetNotice(NoticeForView(_selectedTraderId));

            _detail.HideForTabSwitch();
        }
    }
}
