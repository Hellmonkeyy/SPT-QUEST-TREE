using System;
using System.Collections.Generic;
using System.Linq;
using EFT.UI;
using QuestTree.QuestGraph;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// The quest graph surface itself: the pannable/zoomable viewport, the tidy-tree layout, the
    /// virtualized node/edge views and their pools, and the framing controls. Split out of
    /// <see cref="QuestTreePanel"/>, which keeps the shell around it (toolbar, tab row, aux and
    /// loading panels) and decides which nodes belong to the tab being rendered.
    ///
    /// This is a plain class, not a MonoBehaviour, deliberately. It owns GameObjects but needs no
    /// Unity lifecycle of its own: its only per-frame work is the visibility sweep, and the panel's
    /// Update already runs at exactly the right moment for it - after the keyboard shortcuts, which
    /// can themselves move the view (see <see cref="Tick"/>). A second MonoBehaviour would only add
    /// an Update whose ordering relative to the panel's is not guaranteed. The one piece that must
    /// be a component is <see cref="PanZoomHandler"/> at the bottom, which is added to the viewport
    /// GameObject in order to receive pointer events at all.
    /// </summary>
    internal sealed class QuestGraphView
    {
        // Spacing follows the layout-density setting - see UI/LayoutMetrics.cs, which holds both
        // presets so "Compact layout" is reversible rather than a one-way change.
        private const float MinZoom = 0.25f;
        private const float MaxZoom = 1.5f;
        private const float ZoomSpeed = 0.08f;

        /// <summary>Fallback ceiling on simultaneously-built node views, used only if settings have
        /// not initialised. Virtualization means this is normally unreachable - it exists for the
        /// fully-zoomed-out case, where the viewport can still cover a thousand-odd quests at once.
        /// The real value is <see cref="ModSettings.MaxVisibleNodes"/>.</summary>
        private const int DefaultMaxVisibleNodes = 600;

        /// <summary>How far outside the viewport a node is still built, so nodes are already there
        /// when they scroll in rather than popping in at the edge.</summary>
        private const float VirtualizationMargin = 240f;

        /// <summary>How many frames after a render the visibility sweep re-runs unconditionally,
        /// to cover Unity not having laid the viewport out yet. See Tick.</summary>
        private const int InitialSweepFrames = 3;

        private readonly Dictionary<QuestNode, QuestNodeView> _views = new();

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
        /// <summary>
        /// Edge colours. The resting line is quiet - at 0.25 alpha, a few hundred of them were the
        /// loudest thing on the screen - and an edge into a quest you can act on is drawn heavier
        /// and in that quest's colour (see <see cref="EdgeStyleFor"/>), so the lines that lead
        /// somewhere are the ones you see. Dimmed is everything outside the hovered chain.
        /// </summary>
        private static Color EdgeColor => new(1f, 1f, 1f, EdgeOpacityScale * 0.14f);
        private static Color EdgeToLockedColor => new(1f, 1f, 1f, EdgeOpacityScale * 0.08f);

        /// <summary>The Settings opacity as a multiplier on the resting alphas (14% is 1.0).</summary>
        private static float EdgeOpacityScale =>
            ModSettings.Ready ? ModSettings.EdgeOpacity.Value / 14f : 1f;
        private const float EdgeThickness = 2f;
        private const float EdgeToLockedThickness = 1f;

        /// <summary>
        /// The hover dimming falls off with distance from the hovered quest, radially, rather than
        /// switching every other node to one flat alpha: the boxes around the one you are reading
        /// stay legible, and the far tree recedes further than a flat dim ever could.
        ///
        /// The radii are SCREEN pixels, converted to layout units by the zoom when applied. That is
        /// what ties the effect to how far in you are: zoomed in, a 260px ring is a couple of
        /// neighbours and everything else drops away; zoomed out, the same ring spans half the
        /// tree and the overview stays readable. The dim itself also hardens with zoom - see
        /// <see cref="DimAlphas"/>.
        /// </summary>
        private const float HighlightInnerScreenRadius = 260f;
        private const float HighlightOuterScreenRadius = 1100f;

        /// <summary>The quest whose chain is lit, kept so a zoom during the hover can re-apply the
        /// falloff at the new scale.</summary>
        private QuestNode _hoveredNode;

        /// <summary>Level of detail the built views are currently drawn at (QuestNodeView.SetDetailLevel).</summary>
        private int _detailLevel;

        /// <summary>The zoom the built edges were last aimed at - see RefreshVisibleNodes.</summary>
        private float _edgeZoom = 1f;

        /// <summary>A content-space thickness that is never less than a screen pixel, and never
        /// a slab: divided by zoom, clamped so a 2px line zoomed to 0.25 draws 2px, not 8.</summary>
        private float ScreenThickness(float thickness)
        {
            var zoom = Mathf.Max(_content != null ? _content.localScale.x : 1f, 0.05f);
            return Mathf.Clamp(thickness / zoom, thickness, 8f);
        }

        /// <summary>Taken from the status palette rather than written out again: this used to be a
        /// copy of the "active" colour, which silently stopped matching the moment that colour
        /// changed. Reading it from <see cref="QuestNodeView.ColorFor"/> means the highlighted chain
        /// always looks like the quest it leads to.</summary>
        private static Color EdgeHighlightColor => QuestNodeView.ColorFor(ENodeStatus.Active);

        private readonly Dictionary<int, UILineConnector.Line> _edgeViews = new();

        /// <summary>The hovered quest's chain. Held so ClearHighlight can no-op when nothing is
        /// highlighted rather than sweeping every built view on every pointer exit.</summary>
        private readonly HashSet<QuestNode> _highlighted = new();

        // Released views are deactivated and kept rather than destroyed - panning across a large
        // tree otherwise means a constant churn of Instantiate/Destroy, which is the expensive part.
        private readonly Stack<QuestNodeView> _nodePool = new();
        private readonly Stack<UILineConnector.Line> _edgePool = new();

        /// <summary>Ceilings on the pools: the visible-node budget, since every Render releases
        /// every live view and a ceiling below what was live would destroy and rebuild the
        /// difference on every keystroke - the churn the pool exists to avoid. What the ceiling
        /// still stops is a big tree's views outliving a switch to a small one.</summary>
        private static int MaxPooledNodes =>
            Mathf.Max(256, ModSettings.Ready ? ModSettings.MaxVisibleNodes.Value : DefaultMaxVisibleNodes);
        private static int MaxPooledEdges => MaxPooledNodes * 3;

        // Scratch collections reused by the visibility sweep so it allocates nothing per frame.
        private readonly List<QuestNode> _nodesToRelease = new();
        private readonly List<int> _edgesToRelease = new();

        private QuestGraphBuilder _graph;
        private QuestToolbar _toolbar;
        private Action<QuestNode> _onNodeClicked;
        private RectTransform _viewport;
        private RectTransform _content;

        /// <summary>The readable-label overlay. Built lazily with the content, dropped with
        /// it, and drawn from the same sweep that decides which boxes exist - so panning and
        /// zooming update it without a second update path.</summary>
        private TreeLabelLayer _labels;

        /// <summary>Scratch for the label ranking, reused: this runs on every frame the view
        /// moves, and the buffers in this file exist precisely so that allocates nothing.</summary>
        private readonly List<(QuestNode Node, Vector2 Position)> _labelRanked =
            new List<(QuestNode Node, Vector2 Position)>();

        /// <summary>Every visible box, so the label layer can refuse to draw over one. Reused for
        /// the same reason the list above is.</summary>
        private readonly List<Vector2> _labelOccupied = new List<Vector2>();
        private Vector2 _lastContentPosition;
        private float _lastContentScale;

        /// <summary>Frames left in the post-render re-sweep window. See Tick for why.</summary>
        private int _initialSweepFrames;

        /// <summary>Guards the never-empty recovery against recursing into itself.</summary>
        private bool _recoveringView;

        /// <summary>The graph's own surface, which the panel shows and hides as tabs are switched
        /// and which the legend is parented to.</summary>
        public RectTransform Viewport => _viewport;

        /// <summary>
        /// Builds the viewport and the content transform every node and edge is parented to.
        /// <paramref name="topInset"/> is the height of the chrome above it (toolbar + tab row).
        /// </summary>
        public void Build(
            RectTransform root,
            float topInset,
            QuestGraphBuilder graph,
            QuestToolbar toolbar,
            Action<QuestNode> onNodeClicked)
        {
            _graph = graph;
            _toolbar = toolbar;

            // Selection is recorded here, ahead of whatever the click opens, so a box clicked
            // directly and one focused from a detail-panel link are marked the same way.
            _onNodeClicked = node =>
            {
                SetSelectedNode(node);
                onNodeClicked(node);
            };

            var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            _viewport = (RectTransform)viewportGo.transform;
            var viewport = _viewport;
            viewport.SetParent(root, worldPositionStays: false);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(0f, 0f);
            viewport.offsetMax = new Vector2(0f, -topInset); // leaves room for the toolbar + tab row on top
            viewportGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            GameStyle.ApplyPanel(viewportGo.GetComponent<Image>());

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = (RectTransform)contentGo.transform;
            _content.SetParent(viewport, worldPositionStays: false);
            _content.anchorMin = _content.anchorMax = new Vector2(0f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = new Vector2(40f, -40f);

            viewportGo.AddComponent<PanZoomHandler>().Init(_content, MinZoom, MaxZoom, ZoomSpeed);
        }

        /// <summary>Swapped out for the aux (Kappa/Settings) surface, which occupies the same
        /// area - see QuestTreePanel.ShowGraph / ShowAuxTab.</summary>
        public void SetVisible(bool visible)
        {
            if (_viewport != null) _viewport.gameObject.SetActive(visible);
        }

        /// <summary>The quest whose detail is open, kept as the node rather than the view: views
        /// are pooled and the box may not be built while it is scrolled off screen, so the mark is
        /// re-applied to whichever view next binds the node (see RefreshVisibleNodes).</summary>
        private QuestNode _selectedNode;

        /// <summary>How much of the viewport's right edge is covered by the detail panel right
        /// now, or 0 when it is closed. Asked at framing time rather than stored, since the panel
        /// opens and collapses independently of the graph.</summary>
        public Func<float> CoveredWidth;

        /// <summary>Whether the detail panel is on screen at all.</summary>
        public Func<bool> DetailOpen;

        /// <summary>Marks a box as the one the detail panel is about, or none.</summary>
        public void SetSelectedNode(QuestNode node)
        {
            if (ReferenceEquals(_selectedNode, node)) return;

            if (_selectedNode != null && _views.TryGetValue(_selectedNode, out var previous))
                previous.SetSelected(false);

            _selectedNode = node;

            if (node != null && _views.TryGetValue(node, out var view))
                view.SetSelected(true);
        }

        /// <summary>Recolours every built node after the game reports a status change. Only the
        /// built ones exist to recolour; the rest read their status when they are next built.</summary>
        public void RefreshNodeStatuses()
        {
            foreach (var view in _views.Values)
                view.RefreshStatus();
        }

        /// <summary>
        /// The per-frame virtualization pass, called from QuestTreePanel.Update.
        ///
        /// Driven from there rather than from PanZoomHandler's callbacks so it also catches zoom
        /// changes, window resizes and anything else that moves the content - one check for every
        /// cause, instead of one hook per cause. Nothing happens on a frame where the view did not
        /// move.
        /// </summary>
        public void Tick()
        {
            if (_content == null || !_content.gameObject.activeInHierarchy) return;

            if (_focusRetryFrames > 0 && TickPendingFocus()) return;

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

        /// <summary>
        /// Renders the graph for one tab, given every node that tab contains. "All" hands over every
        /// node at once; a specific trader tab feeds the exact same layout code a single trader's
        /// node subset, so one code path handles both. Global node.Depth (computed once in
        /// QuestGraphBuilder) keeps a quest's column consistent whether viewed from "All" or its own
        /// tab. An edge only draws when both ends are in the currently rendered set, so a
        /// cross-trader prerequisite/unlock naturally never draws a broken line off the edge of a
        /// single-trader tab - see QuestDetailPanel.Show for where that relationship still surfaces,
        /// as text.
        ///
        /// Nothing is instantiated here: the layout for several thousand quests is cheap dictionary
        /// maths, whereas building their views is not, and that split is what lets the whole tree be
        /// browsable rather than truncated. <see cref="RefreshVisibleNodes"/> builds what is on
        /// screen.
        /// </summary>
        public void Render(IReadOnlyList<QuestNode> candidates) => Render(candidates, frame: true);

        /// <summary>As above; with <paramref name="frame"/> off the camera stays where it is,
        /// for a re-layout the player did not ask for (a status change under a filter).</summary>
        public void Render(IReadOnlyList<QuestNode> candidates, bool frame)
        {
            ClearGraphViews();

            // Filters and search are applied BEFORE layout, not by hiding views after the fact, so
            // they genuinely reduce the work rather than just the result.
            var frontier = ModSettings.Ready && ModSettings.FocusFrontier.Value ? FrontierOf(candidates) : null;
            var matching = candidates
                .Where(node => PassesFilters(node) && (frontier == null || frontier.Contains(node)))
                .Where(_toolbar.MatchesSearch)
                .ToList();
            _lastCandidateCount = candidates.Count;
            _lastFocused = frontier != null;
            _toolbar.UpdateRenderNotice(matching.Count, candidates.Count, frontier != null);

            if (matching.Count == 0)
            {
                _layoutOrder = Array.Empty<QuestNode>();
                _edgeLayout = Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();
                return;
            }

            var y = ComputeTreeLayout(matching);

            _layout.Clear();
            foreach (var node in matching)
                _layout[node] = new Vector2(
                    node.Depth * LayoutMetrics.ColumnSpacing,
                    -y[node] * LayoutMetrics.RowSpacing);

            _layoutOrder = matching.ToArray();
            BuildEdgeLayout(matching);

            // Put the camera on the content that was just laid out. Without this the view keeps
            // whatever position it had, so searching while panned to a far corner of a 5,000-quest
            // tree showed an empty screen even though the search had matched.
            if (frame) FrameContent();

            // Force the first sweep: the content transform has not moved, so Tick would not fire.
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
        /// <summary>Ranks the nodes worth labelling and hands them to the label layer.
        ///
        /// Ranked, not arbitrary, and the ranking is the design:
        ///
        /// 1. the selected quest and its chain, so "what does this lead to" survives zooming out -
        ///    the one question only a tree can answer, and the one zoom currently destroys;
        /// 2. search matches, so finding a quest ends with it named in context rather than lost
        ///    among identical boxes;
        /// 3. quests that are actually actionable, so the overview answers "what next" at a glance.
        ///
        /// Ties inside a rank break on distance from the viewport centre, so the labels that do
        /// appear are the ones nearest what you are looking at.</summary>
        private void DrawLabels(Rect visible, float zoom)
        {
            var budget = ModSettings.Ready ? ModSettings.OverviewLabels.Value : 15;

            if (budget <= 0)
            {
                _labels?.Draw(null, null, zoom, 0);
                return;
            }

            _labels ??= new TreeLabelLayer(_content);
            _labelRanked.Clear();

            var centre = visible.center;
            var searching = _toolbar != null && _toolbar.SearchNeedle.Length > 0;

            _labelOccupied.Clear();

            foreach (var pair in _layout)
            {
                var node = pair.Key;
                var position = pair.Value;

                // Only what is on screen: a label for a node two screens away is work nobody sees.
                if (!visible.Contains(position)) continue;

                // Every visible box, so no label is placed on top of one.
                _labelOccupied.Add(position);

                var rank = RankFor(node, searching);
                if (rank < 0) continue;

                _labelRanked.Add((node, position));
                _labelRankOf[node] = rank;
            }

            // Rank first, then nearest the middle of what you are looking at.
            _labelRanked.Sort((a, b) =>
            {
                var byRank = _labelRankOf[a.Node].CompareTo(_labelRankOf[b.Node]);
                if (byRank != 0) return byRank;

                return ((a.Position - centre).sqrMagnitude).CompareTo((b.Position - centre).sqrMagnitude);
            });

            _labels.Draw(_labelRanked, _labelOccupied, zoom, budget);
            _labelRankOf.Clear();
        }

        private readonly Dictionary<QuestNode, int> _labelRankOf = new Dictionary<QuestNode, int>();

        /// <summary>Lower is more important; -1 means do not label.</summary>
        private int RankFor(QuestNode node, bool searching)
        {
            if (node == null) return -1;

            if (ReferenceEquals(node, _selectedNode)) return 0;

            if (_selectedNode != null &&
                (_selectedNode.PrerequisiteIds.Contains(node.Id) || node.PrerequisiteIds.Contains(_selectedNode.Id)))
            {
                return 1;
            }

            if (searching && _toolbar.MatchesSearch(node)) return 2;

            if (node.Status == ENodeStatus.Active) return 3;
            if (node.Status == ENodeStatus.Available) return 4;

            return -1;
        }

        private void RefreshVisibleNodes()
        {
            if (_layoutOrder.Length == 0 && _edgeViews.Count == 0) return;

            var visible = GetVisibleContentRect();
            var budget = ModSettings.Ready ? ModSettings.MaxVisibleNodes.Value : DefaultMaxVisibleNodes;

            // Zoom crossed a readability line: every built view switches detail level. Views
            // bound below pick the level up in Bind.
            var zoom = _content.localScale.x;
            var level = zoom < LayoutMetrics.BarOnlyZoom ? 2 : zoom < LayoutMetrics.DetailLevelZoom ? 1 : 0;
            if (level != _detailLevel)
            {
                _detailLevel = level;
                foreach (var built in _views.Values) built.SetDetailLevel(level);
            }

            DrawLabels(visible, zoom);

            // Edges are drawn in content space, so a 1px hairline at a quarter zoom is a quarter
            // of a screen pixel - nothing. Re-aim the built ones with a thickness that holds on
            // screen whenever the zoom moves - and, if a quest is hovered, repaint its falloff,
            // whose radii are screen pixels too.
            // The box outlines have the same problem as the edges, and the same cure.
            var outlineUnit = ScreenThickness(1f);

            if (!Mathf.Approximately(zoom, _edgeZoom))
            {
                _edgeZoom = zoom;
                foreach (var (index, line) in _edgeViews)
                {
                    if (index < 0 || index >= _edgeLayout.Length) continue;
                    var edge = _edgeLayout[index];
                    UILineConnector.Apply(line, edge.FromPoint, edge.ToPoint, ScreenThickness(EdgeStyleFor(index).Thickness));
                }

                foreach (var built in _views.Values) built.SetOutlineUnit(outlineUnit);

                if (_hoveredNode != null) ApplyHighlightFalloff();
            }

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

                // Only for what is actually on screen and only while searching, so this walks a
                // handful of nodes rather than all 830 - the filtering above is the per-node hot
                // path, and this is not part of it.
                view.SearchReason = node.MatchReason(_toolbar.SearchNeedle);

                view.Bind(node, _onNodeClicked, HighlightChain, _ => ClearHighlight());
                view.SetDetailLevel(_detailLevel);
                view.SetOutlineUnit(outlineUnit);
                view.SetSelected(ReferenceEquals(node, _selectedNode));
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

                _edgeViews[index] = AcquireEdge(index);
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
        /// <summary>
        /// Lights up a quest, everything it requires and everything it unlocks, and dims the rest -
        /// the quickest way to read a chain out of a dense graph.
        ///
        /// Only currently-built views are touched, which is exactly right: virtualization means
        /// nothing else exists, and anything scrolled in afterwards is Bind-ed fresh (and Bind
        /// clears the dim). The related set comes straight off QuestNode, so this is a set build
        /// rather than a graph walk.
        /// </summary>
        public void HighlightChain(QuestNode node)
        {
            if (node == null)
            {
                ClearHighlight();
                return;
            }

            _highlighted.Clear();
            _highlighted.Add(node);
            _hoveredNode = node;

            foreach (var prerequisiteId in node.PrerequisiteIds)
            {
                if (_graph.NodesById.TryGetValue(prerequisiteId, out var prerequisite))
                    _highlighted.Add(prerequisite);
            }

            foreach (var unlocked in node.Unlocks)
                _highlighted.Add(unlocked);

            ApplyHighlightFalloff();
        }

        /// <summary>
        /// Paints the hover dimming for the current zoom: chain members at full strength, every
        /// other built box and edge faded by its screen distance from the hovered quest. Called by
        /// HighlightChain, and again from the visibility sweep when the zoom moves mid-hover.
        /// </summary>
        private void ApplyHighlightFalloff()
        {
            if (_hoveredNode == null || _content == null) return;

            var zoom = Mathf.Max(0.05f, _content.localScale.x);
            var inner = HighlightInnerScreenRadius / zoom;
            var outer = HighlightOuterScreenRadius / zoom;
            var (near, far) = DimAlphas(zoom);

            var origin = _layout.TryGetValue(_hoveredNode, out var centre)
                ? centre + new Vector2(QuestNodeView.Width * 0.5f, 0f)
                : Vector2.zero;

            foreach (var (built, view) in _views)
            {
                if (_highlighted.Contains(built))
                {
                    view.SetDimAlpha(1f);
                    continue;
                }

                var position = _layout.TryGetValue(built, out var at) ? at + new Vector2(QuestNodeView.Width * 0.5f, 0f) : origin;
                view.SetDimAlpha(FalloffAlpha(Vector2.Distance(position, origin), inner, outer, near, far));
            }

            // An edge only counts as part of the chain when BOTH of its ends are in it, otherwise
            // every line leaving a neighbour would light up too and the chain would not read. The
            // rest fade with the same falloff as the boxes, measured at the line's midpoint.
            foreach (var (index, line) in _edgeViews)
            {
                if (index < 0 || index >= _edgeLayout.Length) continue;

                var edge = _edgeLayout[index];
                var inChain = _highlighted.Contains(edge.From) && _highlighted.Contains(edge.To);

                if (inChain)
                {
                    UILineConnector.SetColor(line, EdgeHighlightColor);
                    continue;
                }

                var midpoint = (edge.FromPoint + edge.ToPoint) * 0.5f;
                var alpha = FalloffAlpha(Vector2.Distance(midpoint, origin), inner, outer, near * 0.2f, far * 0.3f);
                UILineConnector.SetColor(line, new Color(1f, 1f, 1f, alpha));
            }
        }

        /// <summary>How hard the dim bites at this zoom. Close in, the rest of the tree is context
        /// you asked to look past, so it drops to almost nothing; zoomed out, it is the overview
        /// you are navigating by, so it only softens.</summary>
        private static (float Near, float Far) DimAlphas(float zoom)
        {
            var t = Mathf.InverseLerp(MinZoom, MaxZoom, zoom);
            var near = Mathf.Lerp(0.8f, 0.5f, t);
            var far = Mathf.Lerp(0.4f, 0.06f, t);

            // Settings scale how far below full the dim goes: 0 = no dimming, 200 = twice as deep.
            var strength = ModSettings.Ready ? ModSettings.HoverDimStrength.Value / 100f : 1f;
            return (1f - (1f - near) * strength, Mathf.Clamp01(1f - (1f - far) * strength));
        }

        /// <summary>Alpha for something <paramref name="distance"/> layout units from the hovered
        /// quest: <paramref name="near"/> inside the inner radius, <paramref name="far"/> beyond
        /// the outer one, eased between.</summary>
        private static float FalloffAlpha(float distance, float inner, float outer, float near, float far)
        {
            var t = Mathf.InverseLerp(inner, outer, distance);
            t = t * t * (3f - 2f * t); // smoothstep - a linear ramp reads as a hard ring
            return Mathf.Lerp(near, far, t);
        }

        /// <summary>Restores every built node and edge to its normal appearance.</summary>
        public void ClearHighlight()
        {
            if (_highlighted.Count == 0) return;
            _highlighted.Clear();
            _hoveredNode = null;

            foreach (var view in _views.Values)
                view.SetDimmed(false);

            foreach (var (index, line) in _edgeViews)
                UILineConnector.SetColor(line, EdgeStyleFor(index).Color);
        }

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
        public void FrameContent() => FrameNodes(_layoutOrder, reserveDetail: true);

        /// <summary>What the last Render told the toolbar, so the counts can be re-issued when
        /// statuses move underneath them without laying anything out again.</summary>
        private int _lastCandidateCount;
        private bool _lastFocused;

        /// <summary>Re-issues the toolbar's "N shown · C of T completed" line from the last
        /// render - the completed total is what a hand-in changes.</summary>
        public void RefreshNotice() =>
            _toolbar?.UpdateRenderNotice(_layoutOrder.Length, _lastCandidateCount, _lastFocused);

        /// <summary>The first quest in layout order - the top of the first column, which is the
        /// earliest match in the chain - or null when nothing is laid out.</summary>
        public QuestNode FirstMatch() => _layoutOrder.Length > 0 ? _layoutOrder[0] : null;

        /// <summary>
        /// Frames a quest with its immediate neighbours and opens its detail - what a clickable
        /// prerequisite or unlock row in the detail panel does, and what a Kappa or Do-next row
        /// does on its way in from an aux tab. Framing the neighbourhood rather than the one box
        /// keeps the zoom sane and shows what it connects to.
        ///
        /// Coming from an aux tab it usually cannot frame on this frame at all: the panel switched
        /// tabs a moment ago, so the graph viewport has only just been re-activated and Unity has
        /// not given it a rect yet. FrameNodes reads that rect, finds it empty and gives up, which
        /// is how a Kappa row used to land the player somewhere in a five-thousand-quest tree with
        /// their quest selected and nowhere in sight. So a frame that could not happen is
        /// remembered and retried from Tick - the same deferral MapView uses for its fly-to.
        /// </summary>
        public void FocusNode(QuestNode node)
        {
            if (node == null || _graph == null) return;

            _pendingFocus = null;
            _focusRetryFrames = 0;

            if (!FrameChain(node))
            {
                if (_layout.ContainsKey(node))
                {
                    _pendingFocus = node;
                    _focusRetryFrames = FocusRetryFrames;
                }
                else
                {
                    // Nothing to retry: a filter or the search box has kept this quest out of the
                    // laid-out set entirely, and no amount of waiting will put it back.
                    ReportUnframedFocus(node, filtered: true);
                }
            }

            _onNodeClicked?.Invoke(node);
        }

        /// <summary>The node a FocusNode could not frame yet, and how many frames are left to keep
        /// trying. Generous: layout normally settles within a frame or two, and each further
        /// attempt costs one rect read.</summary>
        private QuestNode _pendingFocus;
        private int _focusRetryFrames;
        private const int FocusRetryFrames = 8;

        /// <summary>One retry attempt. Returns true when this frame was spent framing, in which
        /// case Tick has nothing further to do - the sweep is run here, because FrameNodes updates
        /// the movement watermarks itself and Tick would otherwise see a still view sitting over a
        /// completely different piece of the tree.</summary>
        private bool TickPendingFocus()
        {
            _focusRetryFrames--;

            var node = _pendingFocus;
            if (node == null)
            {
                _focusRetryFrames = 0;
                return false;
            }

            if (FrameChain(node))
            {
                _pendingFocus = null;
                _focusRetryFrames = 0;
                RefreshVisibleNodes();
                return true;
            }

            if (_focusRetryFrames == 0)
            {
                _pendingFocus = null;
                ReportUnframedFocus(node, filtered: !_layout.ContainsKey(node));
            }

            return false;
        }

        /// <summary>Frames a quest with its prerequisites and unlocks. Shared by the immediate
        /// attempt and the retry, so both frame the same thing.</summary>
        private bool FrameChain(QuestNode node)
        {
            var chain = new List<QuestNode> { node };
            foreach (var prerequisiteId in node.PrerequisiteIds)
                if (_graph.NodesById.TryGetValue(prerequisiteId, out var prerequisite)) chain.Add(prerequisite);
            chain.AddRange(node.Unlocks);

            // The detail opens moments later, so it is reserved for unconditionally: asking whether
            // it is open yet would answer no.
            return FrameNodes(chain, reserveDetail: true, detailOpening: true);
        }

        /// <summary>Says why the quest is selected but not on screen. Silence here is what made the
        /// old behaviour look like a broken link rather than an active filter.</summary>
        private void ReportUnframedFocus(QuestNode node, bool filtered)
        {
            var name = RichText.Safe(node.Name);

            _toolbar?.SetNotice(filtered
                ? $"<color=#{GameStyle.WarningHex}>{name} is selected, but a filter or the search box is hiding it - clear those to see it in the tree.</color>"
                : $"<color=#{GameStyle.WarningHex}>{name} is selected. Press Fit to bring the tree back into view.</color>");
        }

        /// <summary>Frames a subset of the laid-out nodes. Used both for "fit everything" and for
        /// "show me the quests I can actually work on". With <paramref name="reserveDetail"/> the
        /// strip the detail panel covers is left out of the frame, so what is framed lands beside
        /// the panel rather than under it; <paramref name="detailOpening"/> says the panel is
        /// about to open even though it is not up yet.</summary>
        /// <returns>Whether it framed. False means the viewport has no rect yet, or nothing in
        /// <paramref name="nodes"/> is in the current layout - both of which used to be silent.</returns>
        private bool FrameNodes(
            System.Collections.Generic.IEnumerable<QuestNode> nodes,
            bool reserveDetail = false, bool detailOpening = false)
        {
            if (nodes == null || _viewport == null || _layoutOrder.Length == 0) return false;

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

            if (minX > maxX || minY > maxY) return false;

            var viewportSize = _viewport.rect.size;

            // Not laid out yet - a viewport re-activated on this same frame reads as empty. The
            // caller decides whether that is worth retrying.
            if (viewportSize.x <= 1f || viewportSize.y <= 1f) return false;

            // The panel sits over the viewport's right edge, so the usable width is what is left
            // of it. Only when there is still a sensible amount left: on a narrow window the
            // whole viewport is better than a sliver.
            if (reserveDetail && (detailOpening || (DetailOpen?.Invoke() ?? false)))
            {
                var covered = CoveredWidth?.Invoke() ?? 0f;
                if (viewportSize.x - covered >= 400f) viewportSize.x -= covered;
            }

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
            return true;
        }

        /// <summary>Scratch for GetWorldCorners, which insists on an array; one per sweep was a
        /// per-frame allocation while the view moves.</summary>
        private readonly Vector3[] _cornerBuffer = new Vector3[4];

        /// <summary>The viewport in content-local space - the same space node positions use -
        /// padded so nodes exist slightly before they scroll into view.</summary>
        private Rect GetVisibleContentRect()
        {
            var corners = _cornerBuffer;
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

            // The highlight is normally cleared by the node's OnPointerExit, which Unity never
            // delivers to a deactivated object - so a hovered node scrolled or framed (F, M) out of
            // view left every other node dimmed at 25% with nothing to un-dim them.
            if (view.Node != null && _highlighted.Contains(view.Node)) ClearHighlight();

            if (_nodePool.Count >= MaxPooledNodes)
            {
                UnityEngine.Object.Destroy(view.gameObject);
                return;
            }

            view.gameObject.SetActive(false);
            _nodePool.Push(view);
        }

        /// <summary>How an edge is drawn at rest, by where it leads. A line into a quest you can
        /// act on is heavier and in that quest's colour; a line into a locked quest is a hairline.
        /// The eye then follows the lines that go somewhere.</summary>
        private (Color Color, float Thickness) EdgeStyleFor(int index)
        {
            if (index < 0 || index >= _edgeLayout.Length) return (EdgeColor, EdgeThickness);

            var target = _edgeLayout[index].To;

            return target.Status switch
            {
                ENodeStatus.Active => (WithAlpha(QuestNodeView.ColorFor(ENodeStatus.Active), Mathf.Min(1f, 0.45f * EdgeOpacityScale)), EdgeThickness),
                ENodeStatus.Available => (WithAlpha(QuestNodeView.ColorFor(ENodeStatus.Available), Mathf.Min(1f, 0.4f * EdgeOpacityScale)), EdgeThickness),
                ENodeStatus.Completed => (EdgeColor, EdgeThickness),
                _ => (EdgeToLockedColor, EdgeToLockedThickness)
            };
        }

        private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        private UILineConnector.Line AcquireEdge(int index)
        {
            var edge = _edgeLayout[index];
            var style = EdgeStyleFor(index);

            var thickness = ScreenThickness(style.Thickness);

            if (_edgePool.Count == 0)
                return UILineConnector.Create(_content, edge.FromPoint, edge.ToPoint, style.Color, thickness);

            var line = _edgePool.Pop();
            UILineConnector.SetActive(line, true);
            UILineConnector.Apply(line, edge.FromPoint, edge.ToPoint, thickness);

            // Reset the colour on EVERY acquire, not just on create. Apply only re-aims the line,
            // so a pooled edge keeps whatever colour it last had - and once the chain highlight
            // started recolouring edges, that meant highlight colours leaking onto unrelated edges
            // as soon as one was recycled.
            UILineConnector.SetColor(line, style.Color);
            return line;
        }

        private void ReleaseEdge(UILineConnector.Line line)
        {
            if (line == null) return;

            if (_edgePool.Count >= MaxPooledEdges)
            {
                foreach (var part in line.Parts)
                    if (part != null) UnityEngine.Object.Destroy(part.gameObject);
                return;
            }

            UILineConnector.SetActive(line, false);
            _edgePool.Push(line);
        }

        /// <summary>Destroys the pooled views outright, rather than returning them for reuse. Used
        /// when something about how a node is BUILT has changed - currently only layout density.</summary>
        public void DiscardViewPools()
        {
            ClearGraphViews();

            while (_nodePool.Count > 0)
            {
                var view = _nodePool.Pop();
                // UnityEngine.Object.Destroy rather than the MonoBehaviour shorthand: this class is
                // deliberately not a component (see the class comment), so the static call is the
                // same one, just spelled out.
                if (view != null) UnityEngine.Object.Destroy(view.gameObject);
            }

            while (_edgePool.Count > 0)
            {
                var line = _edgePool.Pop();
                if (line?.Parts == null) continue;

                foreach (var part in line.Parts)
                    if (part != null) UnityEngine.Object.Destroy(part.gameObject);
            }
        }

        /// <summary>Returns every built node and edge to its pool. Used when the tab or search
        /// changes - the pooled objects are reused immediately by the next layout.</summary>
        public void ClearGraphViews()
        {
            foreach (var view in _views.Values) ReleaseNodeView(view);
            _views.Clear();

            foreach (var line in _edgeViews.Values) ReleaseEdge(line);
            _edgeViews.Clear();

            _layout.Clear();
            _layoutOrder = Array.Empty<QuestNode>();
            _edgeLayout = Array.Empty<(QuestNode, QuestNode, Vector2, Vector2)>();

            // The hovered set refers to nodes from the tab being torn down; keeping it would leave
            // ClearHighlight sweeping views that no longer relate to it.
            _highlighted.Clear();
        }

        /// <summary>
        /// The frontier: every quest in progress or available to start, plus what each one directly
        /// requires and directly unlocks. With Focus on, this is the whole tree. It is a set build
        /// over the tab's candidates rather than a graph walk, so it costs nothing noticeable even
        /// on the "All" tab.
        /// </summary>
        private HashSet<QuestNode> FrontierOf(IReadOnlyList<QuestNode> candidates)
        {
            var frontier = new HashSet<QuestNode>();

            foreach (var node in candidates)
            {
                if (node.Status != ENodeStatus.Active && node.Status != ENodeStatus.Available) continue;

                frontier.Add(node);

                foreach (var unlocked in node.Unlocks) frontier.Add(unlocked);

                foreach (var prerequisiteId in node.PrerequisiteIds)
                    if (_graph.NodesById.TryGetValue(prerequisiteId, out var prerequisite)) frontier.Add(prerequisite);
            }

            return frontier;
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
        /// in <see cref="BuildEdgeLayout"/> regardless of which one was chosen as primary - this
        /// only affects where the boxes sit, not which lines connect them.
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

        /// <summary>Frames whatever you can actually act on right now - in progress first, else
        /// available. On a 5,000-quest tree this is the difference between the tree being a
        /// reference and being usable.</summary>
        public void FrameMyQuests()
        {
            var mine = _layoutOrder.Where(n => n.Status == ENodeStatus.Active).ToList();
            if (mine.Count == 0)
                mine = _layoutOrder.Where(n => n.Status == ENodeStatus.Available).ToList();

            GameStyle.PlaySound(EUISoundType.ButtonClick);

            if (mine.Count > 0)
            {
                FrameNodes(mine, reserveDetail: true);
                return;
            }

            // Nothing started and nothing available - which is the normal state on a fresh profile,
            // and was also true on a server without the companion mod where every quest reads as
            // Locked. Doing nothing there just looks broken, so fall back to the start of the tree
            // rather than leaving the player where they were.
            FrameContent();

            _toolbar.SetNotice(
                $"<color=#{GameStyle.WarningHex}>No quests started or available - showing the start of the tree.</color>");
        }

    }
}
