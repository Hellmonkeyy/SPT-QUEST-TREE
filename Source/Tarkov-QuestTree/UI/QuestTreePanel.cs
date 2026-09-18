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

        /// <summary>The view button that returns to the graph. Not a tab: it stands for whichever
        /// trader tab was last selected.</summary>
        private const string TreeTabId = "__tree__";

        private static readonly Color SelectedTabColor = new(0.35f, 0.35f, 0.2f, 0.95f);
        private static readonly Color UnselectedTabColor = new(1f, 1f, 1f, 0.08f);

        private readonly QuestGraphBuilder _graph = new();
        private readonly Dictionary<string, Image> _tabBackgrounds = new();

        /// <summary>What a tab changes when selected: its underline and its text colour. The
        /// background stays clear either way - the Tasks screen's tabs are text with an underline,
        /// not boxes.</summary>
        private readonly Dictionary<string, (Image Underline, TMP_Text Text)> _tabStyles = new();

        /// <summary>Trader id -> loyalty level as of the last tab build. Kept so a label refresh
        /// inside the game's status event can rewrite the tabs without a fetch.</summary>
        private Dictionary<string, int> _loyaltyByTrader = new();

        /// <summary>Set by a status change; cleared by the refresh that acts on it. A hidden panel
        /// cannot run the deferred refresh (no coroutines on an inactive GameObject), so Show
        /// checks this and refreshes on the spot.</summary>
        private bool _statusDirty;

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

        /// <summary>Where each detail-link jump came from - the tab and the quest whose detail was
        /// open - so the Back row can retrace them. An aux tab is recorded with whatever node the
        /// hidden detail still held, which GoBack ignores: returning to Do next means the list,
        /// not a quest. Bounded so a long session cannot grow it without limit; nobody walks back
        /// two dozen links.</summary>
        private readonly List<(string TabId, QuestNode Node)> _history = new();
        private const int HistoryDepth = 24;

        /// <summary>The view the panel was last on, kept across opens for the "remember last view"
        /// setting. Static: the panel itself is torn down with the menu after a raid.</summary>
        private static string _lastView;

        /// <summary>The raid location the map was last pointed at. Per raid rather than per open:
        /// the map is turned to the matchmaker's pick once, and a map the player then chooses by
        /// hand stays chosen until the matchmaker says something different. Static for the same
        /// reason as <see cref="_lastView"/>. Latched only when the pick resolved to a map with
        /// quests on it, so a map with nothing to do is tried again next time rather than
        /// remembered as done.</summary>
        private static string _lastRaidPreselect;

        /// <summary>The profile the map view's statics belong to. Static like the rest: the panel
        /// is torn down with the menu, the map's memory is not, and a different character must
        /// not inherit it.</summary>
        private static string _profileId;

        /// <summary>A raid location waiting for the graph to exist. Set by Show when a rebuild is
        /// about to happen and consumed by RebuildGraph once the nodes are there to look in.</summary>
        private string _pendingRaidLocation;
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

        /// <summary>A settings change that arrived while the panel was hidden (the F12 menu),
        /// applied on the next Show. A hidden panel used to rebuild its whole view - the map's
        /// thousand objects - for every toggle nobody could see.</summary>
        private bool _settingsDirty;
        private bool _settingsDirtyLayout;

        private void HandleSettingsChanged(bool affectsLayout)
        {
            if (!_builtShell || _questController == null) return;

            if (!gameObject.activeInHierarchy)
            {
                _settingsDirty = true;
                _settingsDirtyLayout |= affectsLayout;
                return;
            }

            _settingsDirty = false;
            _settingsDirtyLayout = false;

            try
            {
                // Pooled views carry the geometry they were built with, so a density change has to
                // throw them away rather than recycle them - otherwise half the tree would render
                // at the old size. Only a density change, though: the Maps toggles come through
                // this same event, and destroying every pooled node per click of "Accepted quests
                // only" was exactly the churn the pool exists to avoid.
                if (affectsLayout) _graphView.DiscardViewPools();
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
        public void Show(QuestController questController, IEftSession session, string raidLocation = null)
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

            // All three are profile-scoped and all can have moved while the panel was shut.
            QuestDataClient.InvalidateKappa();
            QuestDataClient.InvalidateProfile();
            QuestDataClient.InvalidateBuilds();
            _session = session; // kept for trader-avatar lookups on tab icons, independent of a graph rebuild

            // The tree's chain markers need the same avatars and have no session of their own.
            TraderAvatars.Session = session;

            // A different profile on the same client: the map view's remembered map, quest and
            // view belong to the last character. Read defensively for the same JIT reason as the
            // raid location in MenuTaskBarPatch.
            string profileId = null;
            try
            {
                profileId = ProfileIdOf(session);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: could not read the profile id ({ex.GetType().Name}).");
            }

            if (profileId != null && !string.Equals(profileId, _profileId, StringComparison.Ordinal))
            {
                // Also on the first sighting: if the previous character's id could not be read,
                // its map memory is still here to inherit, and a reset on an untouched view
                // costs nothing.
                MapView.ResetSession();
                _lastRaidPreselect = null;
                _profileId = profileId;
            }

            // The matchmaker's pick, when there is one and it is not the one already acted on.
            var raidChanged = !string.IsNullOrEmpty(raidLocation) &&
                              !string.Equals(raidLocation, _lastRaidPreselect, StringComparison.OrdinalIgnoreCase);

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
                _pendingRaidLocation = raidChanged ? raidLocation : null;
                ShowLoading(true);
                StartCoroutine(RebuildGraphDeferred(questController, session));
            }
            else if (!_graph.HasFullQuestList)
            {
                // Same controller, but the tree was built without the server half. That may have
                // been one unlucky fetch - the server still starting - so each reopen is another
                // chance at the full list; and even without one, the client's own list has grown
                // by whatever was unlocked meanwhile. Cheap in the failing case: a refused or
                // unregistered route answers at once.
                QuestDataClient.RetryFailedFetches();
                _pendingRaidLocation = raidChanged ? raidLocation : null;
                ShowLoading(true);
                StartCoroutine(RebuildGraphDeferred(questController, session));
            }
            else
            {
                // Nothing to rebuild, so the panel comes back exactly as it was left - except for
                // a setting or a hand-in changed while it was shut (each flag is its deferred
                // refresh's message to this path), and a raid picked since, which is the one thing
                // worth turning to.
                if (_settingsDirty) HandleSettingsChanged(_settingsDirtyLayout);
                if (_statusDirty) RefreshAfterStatusChange();
                if (raidChanged) PreselectRaidMap(raidLocation, showNow: true);
            }
        }

        private static string ProfileIdOf(IEftSession session) => session?.Profile?.Id;

        /// <summary>Turns the map view to the raid's map. With <paramref name="showNow"/> the map
        /// is also brought on screen if the panel would open on it anyway (the open-on-map
        /// setting); a panel deliberately left on the tree keeps the tree, with the map ready
        /// underneath for when it is opened.</summary>
        private void PreselectRaidMap(string raidLocation, bool showNow)
        {
            if (!MapView.PreselectLocation(raidLocation, _graph)) return;
            _lastRaidPreselect = raidLocation;

            if (!showNow) return;
            if (_selectedTraderId == MapsTabId) RenderSelectedTab();
            else if (ModSettings.Ready && ModSettings.OpenOnMap.Value) SelectTab(MapsTabId);
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

            // Adopted only now - see Show for why. Unsubscribed first: a rebuild for a controller
            // already adopted (the fallback-tree path in Show) must not add a second handler.
            _questController = questController;
            if (_questController != null)
            {
                _questController.OnConditionalStatusChanged -= HandleStatusChanged;
                _questController.OnConditionalStatusChanged += HandleStatusChanged;
            }

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
                // Measured by phase, because it is the one wait a player feels: the first
                // measurement (1.13.2) put the whole open at 1.2 to 1.4 s with the server under a
                // tenth of it, and could not say where the rest went. Every fetch in here still
                // blocks the main thread behind the loading notice; the "server" entries are that.
                QuestDataClient.ResetFetchClock();
                PanelOpenTimer.Start();

                RebuildGraph(questController, session);

                Plugin.LogSource?.LogInfo(PanelOpenTimer.Report());
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

        /// <summary>Whether the search box has the keyboard. Asked from outside by the open-tracker
        /// shortcut, which must not fire on a key the player is typing into the box.</summary>
        public bool IsTyping => _toolbar != null && _toolbar.IsSearchFocused();

        /// <summary>Unity only calls Update on an active GameObject, so this only ever runs while
        /// the panel is actually open - no extra "is it visible" guard needed.</summary>
        public void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // While typing, Escape leaves the search box - it used to fall through to the
                // layers below and close the detail or the whole panel mid-search. The blur-frame
                // test covers TMP having already taken focus away on this same press.
                if (_toolbar.IsSearchFocused() || _toolbar.SearchBlurredThisFrame)
                {
                    _toolbar.ClearAndBlurSearch();
                    return;
                }

                // Layered, outermost last: the controls hint sits over everything, then the quest
                // detail, then the tree itself. Each press peels off one layer.
                if (_introPanel != null && _introPanel.gameObject.activeSelf) ShowIntro(false);
                else if (_detail.IsOpen) _detail.Hide();
                else HideGameObject();
                return;
            }

            // Shortcuts. Deliberately only while the panel has focus and the search box does not -
            // typing "f" into search must not re-frame the view. Split by view: on the map, F fits
            // the map and the brackets change floor; the tree's keys used to fire there too, where
            // F and M did nothing visible and X silently flipped a tree-only setting.
            if (!_toolbar.IsSearchFocused())
            {
                if (_selectedTraderId == MapsTabId)
                {
                    if (Input.GetKeyDown(KeyCode.F))
                    {
                        MapView.ResetView();
                        RenderSelectedTab();
                    }
                    if (Input.GetKeyDown(KeyCode.RightBracket) && MapView.StepFloor(+1)) RenderSelectedTab();
                    if (Input.GetKeyDown(KeyCode.LeftBracket) && MapView.StepFloor(-1)) RenderSelectedTab();
                }
                else if (!IsAuxTab(_selectedTraderId))
                {
                    if (Input.GetKeyDown(KeyCode.F)) _graphView.FrameContent();
                    if (Input.GetKeyDown(KeyCode.M)) _graphView.FrameMyQuests();
                    if (Input.GetKeyDown(KeyCode.X)) _toolbar.ToggleFocus();
                    if (Input.GetKeyDown(KeyCode.Slash)) _toolbar.FocusSearch();
                }
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

            // The cached ranking holds this panel's graph and profile; the panel dies with the
            // menu after every raid, and nothing else would drop it until the next tracker open.
            DoNextView.Forget();
            QuestBody.Forget();
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
                // standing, objective counters and what is still locked - so all three caches are stale.
                QuestDataClient.InvalidateKappa();
                QuestDataClient.InvalidateProfile();
                QuestDataClient.InvalidateBuilds();

                if (_graph.HasFullQuestList)
                {
                    _graph.RefreshStatuses();
                    _graphView.RefreshNodeStatuses();

                    // The rest - the open detail, the tab counts, the toolbar total, the filters
                    // that depend on status - a frame later, outside the game's invocation list;
                    // or on the next Show when the panel is hidden (see _statusDirty).
                    _statusDirty = true;
                    if (gameObject.activeInHierarchy) StartCoroutine(RefreshAfterStatusChangeDeferred());
                }
                else if (gameObject.activeInHierarchy)
                {
                    // Without the server half the graph holds only what the client had unlocked
                    // when it was built, and a refresh only recolours what is there - so a quest
                    // this hand-in just unlocked would never appear. Rebuild instead. A hidden
                    // panel is left alone; it rebuilds on Show (see there).
                    DoNextView.Forget();
                    QuestBody.Forget();
                    _graph.Build(_questController, _session);
                    BuildTabs();
                    RenderSelectedTab();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to refresh quest statuses: {ex}");
            }
        }

        private System.Collections.IEnumerator RefreshAfterStatusChangeDeferred()
        {
            yield return null;
            RefreshAfterStatusChange();
        }

        /// <summary>Everything a status change moves besides the box colours. A list view is
        /// rebuilt outright (they read the profile as they build); under a status-dependent
        /// filter the tree is laid out again with the camera kept; otherwise only the labels,
        /// the toolbar total and the open detail are rewritten.</summary>
        private void RefreshAfterStatusChange()
        {
            if (!_statusDirty) return;
            _statusDirty = false;
            if (_graph == null || !_graph.HasFullQuestList) return;

            try
            {
                // Here rather than in the event handler: this pass reads the profile, and the
                // handler invalidated that cache a frame ago, so asking for it there would have
                // fetched synchronously inside the game's invocation list. See ApplyLockGates.
                _graph.ApplyLockGates();
                _graphView?.RefreshNodeStatuses();

                if (IsAuxTab(_selectedTraderId))
                {
                    RenderSelectedTab();
                    return;
                }

                var statusDecidesTheSet = ModSettings.Ready &&
                                          (ModSettings.HideCompleted.Value || ModSettings.FocusFrontier.Value);
                if (statusDecidesTheSet) RenderSelectedTab(frame: false);
                else _graphView.RefreshNotice();

                RefreshTabLabels();
                _detail.Refresh();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to refresh after a status change: {ex}");
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

            BuildAuxPanel(root);
            BuildLoadingPanel(root);

            _toolbar.Build(
                root,
                _graph,
                isAuxTabSelected: () => IsAuxTab(_selectedTraderId),
                onSearchChanged: () => _graphView.RefreshSearch(),
                onSearchSubmitted: () =>
                {
                    var first = _graphView.FirstMatch();
                    if (first != null) FocusNode(first);
                },
                frameMyQuests: _graphView.FrameMyQuests,
                frameContent: _graphView.FrameContent,
                viewButtons: ViewButtons,
                onViewSelected: SelectView,
                closeTree: CloseTree,
                showIntro: () => ShowIntro(true));

            BuildTabRow(root);
            _detail.Build(root, _graph, FocusNode, ShowOnMap, () => _session);

            // The preset loader needs the live session to reach the game's build storage, and it is
            // called from a click handler too deep to be given one.
            WeaponPresetLoader.Session = () => _session;
            _detail.OnHidden = () => _graphView.SetSelectedNode(null);
            _detail.CanGoBack = () => _history.Count > 0;
            _detail.GoBack = GoBack;
            _graphView.DetailOpen = () => _detail.IsOpen;
            _graphView.CoveredWidth = () => _detail.CoveredWidth;
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
            _introPanel.sizeDelta = new Vector2(470f, 410f);
            _introPanel.anchoredPosition = Vector2.zero;

            var background = panelGo.GetComponent<Image>();
            background.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(background);

            var y = AuxLayout.Padding;
            AuxLayout.AddHeading(_introPanel, ref y, "Getting around");
            AuxLayout.AddText(_introPanel, ref y, "Drag to pan  ·  mouse wheel to zoom", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "The trader tabs scroll - wheel or drag them too", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "Click a quest for its detail; its links walk the chain, Back retraces", 20f, 12);
            AuxLayout.AddSpacer(ref y, 8f);
            AuxLayout.AddText(_introPanel, ref y, "<b>F</b>  fit the whole tab on screen (on the map: fit the floor)", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>M</b>  jump to the quests you can work on", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>X</b>  focus: hide everything you cannot work on yet", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>/</b>  search - Enter opens the first match, Esc leaves the box", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>[ ]</b>  on the map: the floor below or above", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y, "<b>Esc</b>  close the quest detail, then the tree", 20f, 12);
            AuxLayout.AddSpacer(ref y, 8f);

            // The views are the least discoverable thing here - they are buttons in the corner, and
            // nothing about the tree suggests an item watchlist exists at all.
            AuxLayout.AddText(_introPanel, ref y,
                "<b>Top right:</b> Tree, Maps, Do next, Items, Kappa, Settings", 20f, 12);
            AuxLayout.AddText(_introPanel, ref y,
                "<color=#FFFFFF80>The tracker opens on the map; Tree is the full quest graph.</color>", 18f, 11);
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
            handleImage.color = new Color(GameStyle.KappaGold.r, GameStyle.KappaGold.g, GameStyle.KappaGold.b, 0.9f);

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

        /// <summary>Badges the nodes with the server's Kappa list. One blocking fetch per graph
        /// build - Show has just invalidated the Kappa cache, and a missing route answers at
        /// once - and never from a render or a keystroke. Without the server half the nodes
        /// simply carry no badge, which the Kappa tab already explains in words.</summary>
        private void ApplyServerKappaList()
        {
            var result = QuestDataClient.GetKappa();
            var payload = result != null && result.IsOk ? result.Payload : null;

            if (payload?.KappaQuestIds != null)
                _graph.ApplyServerKappaIds(payload.KappaQuestIds);

            // Unconditional, unlike the list above: the closure is walked in the graph, so it
            // works without the server half too - the payload only supplies a better id than the
            // built-in constant when a mod has moved Collector.
            _graph.ApplyCollectorClosure(payload?.CollectorQuestId);
        }

        /// <summary>Rebuilds the graph DATA (called only when the QuestController instance
        /// changes) and the tab strip that depends on it, then renders whichever tab is
        /// selected - defaulting back to "All" for a fresh QuestController.</summary>
        private void RebuildGraph(QuestController questController, IEftSession session)
        {
            _graph.Build(questController, session);

            DoNextView.Forget();
            QuestBody.Forget();

            // The nodes the history pointed at belong to the graph just replaced. A pending
            // settings change is moot for the same reason, except that pooled boxes built to the
            // old geometry must still go.
            _history.Clear();
            if (_settingsDirtyLayout) _graphView.DiscardViewPools();
            _settingsDirty = false;
            _settingsDirtyLayout = false;
            PanelOpenTimer.Mark("reset");

            var waited = QuestDataClient.FetchMillis;
            ApplyServerKappaList();
            PanelOpenTimer.MarkSplit("kappa: server", QuestDataClient.FetchMillis - waited, "kappa: apply");

            // The tab below is chosen by setting; this only decides which map that tab shows.
            if (_pendingRaidLocation != null)
            {
                PreselectRaidMap(_pendingRaidLocation, showNow: false);
                _pendingRaidLocation = null;
            }

            // Land on the map by default: it is the view most opens are for. The graph is the Tree
            // button away, and "All" is where that button goes. Or, by setting, wherever the
            // panel was last - if that is still a real tab.
            _tabBeforeSettings = AllTradersId;
            _selectedTraderId = ModSettings.Ready && ModSettings.OpenOnMap.Value ? MapsTabId : AllTradersId;

            if (ModSettings.Ready && ModSettings.RememberLastView.Value && _lastView != null &&
                (IsAuxTab(_lastView) || _lastView == AllTradersId || _graph.TraderNames.ContainsKey(_lastView)))
            {
                _selectedTraderId = _lastView;
            }

            BuildTabs();
            PanelOpenTimer.Mark("tabs");

            waited = QuestDataClient.FetchMillis;
            RenderSelectedTab();

            // An aux view marks its own phases as it builds (MapView does); what is left here is
            // whatever came after its last mark. The tree has no inner marks, so its split is the
            // whole render.
            if (IsAuxTab(_selectedTraderId))
                PanelOpenTimer.Mark($"{_selectedTraderId.Trim('_')}: rest");
            else
                PanelOpenTimer.MarkSplit("tree: server", QuestDataClient.FetchMillis - waited, "tree: layout+paint");

            UpdateTabHighlight();
        }

        private void BuildTabs()
        {
            // The tabs' own container, not _tabRow - clearing the row itself would destroy the
            // scroll content object along with the tabs.
            AuxLayout.ClearChildren(_tabContent);
            _tabBackgrounds.Clear();
            _tabStyles.Clear();
            _tabCursorX = 8f;

            CreateTabButton("All", "", AllTradersId);

            // Loyalty comes from the profile payload, which the client was fetching and ignoring.
            // It is what makes a "requires LL3" lock reason mean something - LL3 is meaningless
            // without knowing you are LL2.
            _loyaltyByTrader = BuildLoyaltyLookup();

            foreach (var traderId in OrderedTraderIds(_graph.Nodes))
                CreateTabButton(TraderLabel(traderId), TraderSuffix(traderId), traderId);

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

        private string TraderLabel(string traderId) =>
            _graph.TraderNames.TryGetValue(traderId, out var name) ? name : traderId;

        /// <summary>Completion per trader, so the row itself says where there is work left rather
        /// than requiring a click into each one - plus the loyalty level when known.
        ///
        /// Read from a tally built in ONE pass over the nodes, cached until the graph changes. This walked
        /// every node per trader and BuildTabs calls it per trader, so a tab rebuild was O(traders x nodes) -
        /// about 8,300 iterations - and RefreshTabLabels repeats it after every hand-in.</summary>
        private string TraderSuffix(string traderId)
        {
            var tally = TraderTallies();
            var counts = tally.TryGetValue(traderId, out var found) ? found : (Done: 0, Total: 0);

            var total = counts.Total;
            var done = counts.Done;

            var loyaltySuffix = _loyaltyByTrader.TryGetValue(traderId, out var level) && level > 0
                ? $"  LL{level}"
                : "";

            return $"{done}/{total}{loyaltySuffix}";
        }

        /// <summary>Done/total per trader id, in one pass, cached until the graph is rebuilt.
        ///
        /// Keyed on QuestGraphBuilder.Version, which Build bumps and nothing else does - so a hand-in that
        /// re-runs the graph invalidates this, and a repaint that does not cannot see a stale number.</summary>
        private Dictionary<string, (int Done, int Total)> TraderTallies()
        {
            if (_traderTallies != null && _traderTallyVersion == _graph.Version) return _traderTallies;

            var tallies = new Dictionary<string, (int Done, int Total)>(StringComparer.Ordinal);

            foreach (var node in _graph.Nodes)
            {
                tallies.TryGetValue(node.TraderId, out var counts);

                counts.Total++;
                if (node.Status == ENodeStatus.Completed) counts.Done++;

                tallies[node.TraderId] = counts;
            }

            _traderTallyVersion = _graph.Version;
            return _traderTallies = tallies;
        }

        private Dictionary<string, (int Done, int Total)> _traderTallies;
        private int _traderTallyVersion = -1;

        /// <summary>Rewrites the trader tabs' done/total in place - what a hand-in changes -
        /// without rebuilding the tab objects. Loyalty is the lookup BuildTabs made, not a fresh
        /// fetch: this runs a frame after the game's own status event, where a blocking request
        /// has no place.</summary>
        private void RefreshTabLabels()
        {
            foreach (var (traderId, style) in _tabStyles)
            {
                if (traderId == AllTradersId || style.Text == null) continue;
                style.Text.text = TabLabel(TraderLabel(traderId), TraderSuffix(traderId));
            }
        }

        private static string TabLabel(string name, string suffix) =>
            string.IsNullOrEmpty(suffix) ? name : $"{name}  <color=#FFFFFF60>{suffix}</color>";

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

        /// <summary>Distinct trader ids from `nodes` - shared by BuildTabs and RenderSelectedTab's
        /// "All" branch so the two cannot drift out of sync.
        ///
        /// Traders with something to do come first, so the useful tabs are on screen without scrolling
        /// the row; alphabetical by display name within that.</summary>
        private List<string> OrderedTraderIds(IEnumerable<QuestNode> nodes)
        {
            var list = nodes.ToList();
            var actionable = new Dictionary<string, int>();

            foreach (var node in list)
            {
                if (node.Status != ENodeStatus.Active && node.Status != ENodeStatus.Available) continue;
                actionable.TryGetValue(node.TraderId, out var count);
                actionable[node.TraderId] = count + 1;
            }

            return list.Select(n => n.TraderId)
                .Distinct()
                .OrderByDescending(id => actionable.TryGetValue(id, out var count) ? count : 0)
                .ThenBy(id => _graph.TraderNames.TryGetValue(id, out var name) ? name : id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>One tab: the trader's portrait, their name as written, and the count in a
        /// quieter colour. Selected is an accent underline and full-strength text; unselected is
        /// dim text on nothing - the Tasks screen's own tab treatment, and far more legible than
        /// the boxed uppercase-11px this replaced.</summary>
        private void CreateTabButton(string name, string suffix, string traderId)
        {
            var hasIcon = traderId != AllTradersId && !IsAuxTab(traderId);

            // Sized after its label exists: the width is measured on the label itself, below.
            var tabGo = new GameObject($"Tab_{(traderId == AllTradersId ? "All" : name)}", typeof(RectTransform), typeof(Image), typeof(Button));
            var tabRect = (RectTransform)tabGo.transform;
            tabRect.SetParent(_tabContent, worldPositionStays: false);
            tabRect.anchorMin = tabRect.anchorMax = new Vector2(0f, 1f);
            tabRect.pivot = new Vector2(0f, 1f);
            tabRect.anchoredPosition = new Vector2(_tabCursorX, -2f);

            // Clear, but still a raycast target so the Button receives the click.
            var background = tabGo.GetComponent<Image>();
            background.color = Color.clear;
            _tabBackgrounds[traderId] = background;

            if (hasIcon) CreateTabIcon(tabRect, traderId);

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(tabRect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(hasIcon ? 30f : 8f, 0f);
            textRect.offsetMax = new Vector2(-6f, 0f);

            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 12;
            text.alignment = TextAlignmentOptions.Left;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.text = TabLabel(name, suffix);
            text.raycastTarget = false;
            GameStyle.Apply(text);

            var width = Mathf.Clamp(GameStyle.MeasureWidth(text, text.text) + 14f + (hasIcon ? 24f : 0f), 60f, 260f);
            tabRect.sizeDelta = new Vector2(width, TabHeight);
            _tabCursorX += width + 4f;

            var underlineGo = new GameObject("Underline", typeof(RectTransform), typeof(Image));
            var underline = (RectTransform)underlineGo.transform;
            underline.SetParent(tabRect, worldPositionStays: false);
            underline.anchorMin = new Vector2(0f, 0f);
            underline.anchorMax = new Vector2(1f, 0f);
            underline.pivot = new Vector2(0.5f, 0f);
            underline.anchoredPosition = Vector2.zero;
            underline.sizeDelta = new Vector2(-8f, 2f);
            var underlineImage = underlineGo.GetComponent<Image>();
            underlineImage.color = GameStyle.AccentColor;
            underlineImage.raycastTarget = false;
            underlineGo.SetActive(false);

            _tabStyles[traderId] = (underlineImage, text);

            var button = tabGo.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => SelectTab(traderId));
            GameStyle.AddHoverFeedback(tabGo, background);
        }

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

        /// <summary>Selects and frames a quest in the graph, switching to a tab that contains it
        /// first if the current one does not - a prerequisite from another trader is not in that
        /// trader's tab, and the map is not a tab at all. The tab chosen is the narrowest that
        /// holds the quest: from a trader tab, the other trader's tab rather than All, so a
        /// trader-by-trader reading of the tree survives following a link out of it.</summary>
        private void FocusNode(QuestNode node) => FocusNode(node, pushHistory: true);

        /// <summary>The non-graph views, in the order they appear right-to-left from Close. Kappa
        /// sits innermost because it is the one you open most often.</summary>
        private void FocusNode(QuestNode node, bool pushHistory)
        {
            if (node == null) return;

            if (pushHistory) PushHistory();

            if (IsAuxTab(_selectedTraderId))
                SelectTab(TabHolding(node, _tabBeforeSettings));
            else if (_selectedTraderId != AllTradersId && node.TraderId != _selectedTraderId)
                SelectTab(TabHolding(node, node.TraderId));

            _graphView.FocusNode(node);
        }

        /// <summary>The preferred tree tab when it is a trader tab that holds the quest, else All.</summary>
        private string TabHolding(QuestNode node, string preferred) =>
            preferred != null && preferred == node.TraderId && _tabStyles.ContainsKey(preferred)
                ? preferred
                : AllTradersId;

        /// <summary>Records where a jump is leaving from, when there is anything to come back to:
        /// a tree tab with no detail open is where the session started, not a place.</summary>
        private void PushHistory()
        {
            var current = _detail.CurrentNode;
            if (current == null && !IsAuxTab(_selectedTraderId)) return;

            _history.Add((_selectedTraderId, current));
            if (_history.Count > HistoryDepth) _history.RemoveAt(0);
        }

        /// <summary>The Back row: the previous tab, and the previous quest's detail if that tab is
        /// the tree. Focusing does not push, or Back would undo itself.</summary>
        private void GoBack()
        {
            if (_history.Count == 0) return;

            var (tabId, node) = _history[_history.Count - 1];
            _history.RemoveAt(_history.Count - 1);

            if (IsAuxTab(tabId) || (tabId != AllTradersId && !_tabStyles.ContainsKey(tabId)))
            {
                SelectTab(IsAuxTab(tabId) ? tabId : AllTradersId);
                return;
            }

            SelectTab(tabId);
            if (node != null) FocusNode(node, pushHistory: false);
        }

        /// <summary>Opens the map on a quest: the detail panel's "show on the map" row.</summary>
        private void ShowOnMap(QuestNode node)
        {
            if (node == null) return;

            MapView.ShowQuest(node);

            if (_selectedTraderId == MapsTabId) RenderSelectedTab();
            else SelectTab(MapsTabId);
        }

        private static readonly (string TabId, string Label)[] ViewButtons =
        {
            (SettingsTabId, "Settings"),
            (KappaTabId, "Kappa"),
            (ItemsTabId, "Items"),
            (DoNextTabId, "Do next"),
            (MapsTabId, "Maps"),
            (TreeTabId, "Tree")
        };

        private void SelectView(string tabId)
        {
            if (tabId == TreeTabId)
            {
                SelectTab(IsAuxTab(_tabBeforeSettings) ? AllTradersId : _tabBeforeSettings);
                return;
            }

            SelectTab(_selectedTraderId == tabId ? _tabBeforeSettings : tabId);
        }

        /// <summary>The tabs that are not the quest graph, and so ignore search and filters.</summary>
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

            // All three read the stash, which moves every raid, so entering them re-reads rather
            // than showing whatever was cached when the panel opened. The map's "items to find
            // here" counts were the one place still showing the stale copy.
            if (traderId == ItemsTabId || traderId == DoNextTabId || traderId == MapsTabId)
                QuestDataClient.InvalidateProfile();

            // Tabs are hand-built rather than cloned, so they play the game's click sound
            // explicitly - otherwise half this screen would be silent and half would not.
            GameStyle.PlaySound(EUISoundType.ButtonClick);

            _selectedTraderId = traderId;
            _lastView = traderId;
            RenderSelectedTab();
            UpdateTabHighlight();
        }

        private void UpdateTabHighlight()
        {
            foreach (var (traderId, style) in _tabStyles)
            {
                var selected = traderId == _selectedTraderId;
                if (style.Underline != null) style.Underline.gameObject.SetActive(selected);
                if (style.Text != null) style.Text.color = selected ? GameStyle.TextColor : GameStyle.DimTextColor;
            }

            // The Tree button lights whenever a graph tab is up, whichever trader it is.
            _toolbar.SetViewHighlight(
                IsAuxTab(_selectedTraderId) ? _selectedTraderId : TreeTabId, SelectedTabColor, UnselectedTabColor);
        }

        /// <summary>
        /// The trader tab row only means something over the graph. On the map and the lists it is
        /// hidden and the view takes its space - the map in particular wants every pixel.
        /// </summary>
        private void SetTabRowVisible(bool visible)
        {
            if (_tabRow != null) _tabRow.gameObject.SetActive(visible);
            if (_auxPanel != null)
                _auxPanel.offsetMax = new Vector2(0f, -(QuestToolbar.Height + (visible ? TabRowHeight : 0f)));
        }

        /// <summary>The area a whole-screen view may fill, in the aux panel's own units. Falls back
        /// to the panel's rect when the aux panel has not been laid out yet (first open).</summary>
        private Vector2 AuxViewportSize()
        {
            var rect = _auxPanel != null ? _auxPanel.rect : Rect.zero;
            if (rect.width > 100f && rect.height > 100f) return rect.size;

            var own = ((RectTransform)transform).rect;
            var width = own.width > 100f ? own.width : Screen.width;
            var height = (own.height > 100f ? own.height : Screen.height) - QuestToolbar.Height;
            return new Vector2(width, height);
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
        private void RenderSelectedTab() => RenderSelectedTab(frame: true);

        private void RenderSelectedTab(bool frame)
        {
            var aux = IsAuxTab(_selectedTraderId);
            _toolbar.SetTreeControlsVisible(!aux);
            SetTabRowVisible(!aux);

            if (aux)
            {
                ShowAuxTab();
                return;
            }

            ShowGraph();

            var candidates = _selectedTraderId == AllTradersId
                ? _graph.Nodes
                : _graph.Nodes.Where(n => n.TraderId == _selectedTraderId).ToList();

            _graphView.Render(candidates, frame);
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

            // Detached before destroying (see AuxLayout.ClearChildren): that was invisible when
            // this ran once per tab click, but the Maps dropdown rebuilds the view on every open,
            // close and select, where the ghost frame reads as flicker.
            AuxLayout.ClearChildren(_auxContent);

            var size = AuxViewportSize();

            var height = _selectedTraderId == DoNextTabId
                ? DoNextView.Build(_auxContent, _graph, size, FocusNode, () =>
                {
                    QuestDataClient.InvalidateProfile();
                    RenderSelectedTab();
                }, ShowOnMap)
                : _selectedTraderId == MapsTabId
                ? MapView.Build(_auxContent, _graph, RenderSelectedTab, () =>
                {
                    QuestDataClient.InvalidateProfile();

                    // The raid check too: "Take with you" reads the same stash, and the whole
                    // promise of that section is that moving an item and looking again tells you
                    // the truth. Only on the refresh link - NOT on every render, which would walk a
                    // four-thousand-item inventory on every map click.
                    QuestDataClient.InvalidateRaidCheck();
                    RenderSelectedTab();
                }, size)
                : _selectedTraderId == ItemsTabId
                ? ItemWatchlistView.Build(_auxContent, _graph, size, FocusNode, () =>
                {
                    QuestDataClient.InvalidateProfile();
                    RenderSelectedTab();
                })
                : _selectedTraderId == KappaTabId
                ? KappaView.Build(_auxContent, _graph, size, FocusNode, () =>
                {
                    QuestDataClient.InvalidateKappa();
                    QuestDataClient.InvalidateProfile();
                    RenderSelectedTab();
                }, RenderSelectedTab)
                : SettingsView.Build(_auxContent, size, () => ShowIntro(true), graph: _graph, onKappaListReloaded: () =>
                {
                    // SettingsView has already re-read the file; the flag is baked into each node
                    // at build time, so the graph needs telling before anything is redrawn.
                    _graph.RefreshKappaFlags();
                    RenderSelectedTab();
                });

            _auxContent.sizeDelta = new Vector2(0f, height);

            // A view that expanded something in place asks to be scrolled back to it. The position
            // was zeroed before the rebuild, and the clamp needs the finished height, so this is the
            // only point where it can be honoured. Only scrolls when the row would otherwise be off
            // screen, and leaves a good part of the viewport for the body underneath it.
            // Only for the tab that arms it. This runs for every aux tab, and consume-once means
            // whichever build gets here first takes the offset - so without the guard, a row click that
            // somehow did not reach its own rebuild would scroll Maps or Settings to a Do-next row's
            // position. _auxPanel is not re-tested: ShowAuxTab returned on null long before here.
            var pendingScroll = _selectedTraderId == DoNextTabId
                ? DoNextView.TakePendingScroll()
                : null;

            if (pendingScroll is { } rowY)
            {
                var viewport = _auxPanel.rect.height;
                var reveal = viewport * 0.4f;

                if (rowY + reveal > viewport)
                {
                    var maxScroll = Mathf.Max(0f, height - viewport);
                    _auxContent.anchoredPosition =
                        new Vector2(0f, Mathf.Clamp(rowY - reveal, 0f, maxScroll));
                }
            }

            _toolbar.SetNotice(NoticeForView(_selectedTraderId));

            _detail.HideForTabSwitch();
        }
    }
}
