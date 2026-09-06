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
        /// <summary>Edge colours. Normal is the resting line; highlighted is a chain member;
        /// dimmed is everything outside the hovered chain.</summary>
        private static readonly Color EdgeColor = new(1f, 1f, 1f, 0.25f);
        private static readonly Color EdgeDimmedColor = new(1f, 1f, 1f, 0.06f);

        /// <summary>Taken from the status palette rather than written out again: this used to be a
        /// copy of the "active" colour, which silently stopped matching the moment that colour
        /// changed. Reading it from <see cref="QuestNodeView.ColorFor"/> means the highlighted chain
        /// always looks like the quest it leads to.</summary>
        private static Color EdgeHighlightColor => QuestNodeView.ColorFor(ENodeStatus.Active);

        private readonly Dictionary<int, RectTransform> _edgeViews = new();

        /// <summary>The hovered quest's chain. Held so ClearHighlight can no-op when nothing is
        /// highlighted rather than sweeping every built view on every pointer exit.</summary>
        private readonly HashSet<QuestNode> _highlighted = new();

        // Released views are deactivated and kept rather than destroyed - panning across a large
        // tree otherwise means a constant churn of Instantiate/Destroy, which is the expensive part.
        private readonly Stack<QuestNodeView> _nodePool = new();
        private readonly Stack<RectTransform> _edgePool = new();

        // Scratch collections reused by the visibility sweep so it allocates nothing per frame.
        private readonly List<QuestNode> _nodesToRelease = new();
        private readonly List<int> _edgesToRelease = new();

        private QuestGraphBuilder _graph;
        private QuestToolbar _toolbar;
        private Action<QuestNode> _onNodeClicked;
        private RectTransform _viewport;
        private RectTransform _content;
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
            _onNodeClicked = onNodeClicked;

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
        public void Render(IReadOnlyList<QuestNode> candidates)
        {
            ClearGraphViews();

            // Filters and search are applied BEFORE layout, not by hiding views after the fact, so
            // they genuinely reduce the work rather than just the result.
            var matching = candidates.Where(PassesFilters).Where(_toolbar.MatchesSearch).ToList();

            _toolbar.UpdateRenderNotice(matching.Count, candidates.Count);

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
            FrameContent();

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
                view.Bind(node, _onNodeClicked, HighlightChain, _ => ClearHighlight());
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

            foreach (var prerequisiteId in node.PrerequisiteIds)
            {
                if (_graph.NodesById.TryGetValue(prerequisiteId, out var prerequisite))
                    _highlighted.Add(prerequisite);
            }

            foreach (var unlocked in node.Unlocks)
                _highlighted.Add(unlocked);

            foreach (var (built, view) in _views)
                view.SetDimmed(!_highlighted.Contains(built));

            // An edge only counts as part of the chain when BOTH of its ends are in it, otherwise
            // every line leaving a neighbour would light up too and the chain would not read.
            foreach (var (index, line) in _edgeViews)
            {
                if (index < 0 || index >= _edgeLayout.Length) continue;

                var edge = _edgeLayout[index];
                var inChain = _highlighted.Contains(edge.From) && _highlighted.Contains(edge.To);
                SetEdgeColor(line, inChain ? EdgeHighlightColor : EdgeDimmedColor);
            }
        }

        /// <summary>Restores every built node and edge to its normal appearance.</summary>
        public void ClearHighlight()
        {
            if (_highlighted.Count == 0) return;
            _highlighted.Clear();

            foreach (var view in _views.Values)
                view.SetDimmed(false);

            foreach (var line in _edgeViews.Values)
                SetEdgeColor(line, EdgeColor);
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
        public void FrameContent() => FrameNodes(_layoutOrder);

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

            // The highlight is normally cleared by the node's OnPointerExit, which Unity never
            // delivers to a deactivated object - so a hovered node scrolled or framed (F, M) out of
            // view left every other node dimmed at 25% with nothing to un-dim them.
            if (view.Node != null && _highlighted.Contains(view.Node)) ClearHighlight();

            view.gameObject.SetActive(false);
            _nodePool.Push(view);
        }

        private RectTransform AcquireEdge(Vector2 from, Vector2 to)
        {
            if (_edgePool.Count == 0) return UILineConnector.Create(_content, from, to, EdgeColor, 2f);

            var line = _edgePool.Pop();
            line.gameObject.SetActive(true);
            UILineConnector.Apply(line, from, to, 2f);

            // Reset the colour on EVERY acquire, not just on create. UILineConnector.Apply only
            // re-aims the line, so a pooled edge keeps whatever colour it last had - and once the
            // chain highlight started recolouring edges, that meant highlight colours leaking onto
            // unrelated edges as soon as one was recycled.
            SetEdgeColor(line, EdgeColor);
            return line;
        }

        private static void SetEdgeColor(RectTransform line, Color color)
        {
            var image = line != null ? line.GetComponent<Image>() : null;
            if (image != null) image.color = color;
        }

        private void ReleaseEdge(RectTransform line)
        {
            if (line == null) return;
            line.gameObject.SetActive(false);
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
                if (line != null) UnityEngine.Object.Destroy(line.gameObject);
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
                FrameNodes(mine);
                return;
            }

            // Nothing started and nothing available - which is the normal state on a fresh profile,
            // and was also true on a server without the companion mod where every quest reads as
            // Locked. Doing nothing there just looks broken, so fall back to the start of the tree
            // rather than leaving the player where they were.
            FrameContent();

            _toolbar.SetNotice(
                "<color=#D9A61A>No quests started or available - showing the start of the tree.</color>");
        }

    }
}
