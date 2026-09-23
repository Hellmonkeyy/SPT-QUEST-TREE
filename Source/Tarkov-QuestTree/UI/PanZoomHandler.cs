using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestTree.UI
{
    /// <summary>
    /// What a map overlay - a place name, an extract diamond, a quest pin - needs from the thing that
    /// moves the view under it, and nothing else.
    ///
    /// Two implementations, and they work in different spaces: <see cref="PanZoomHandler"/> scales and
    /// offsets a content rect whose local units ARE map metres, so an overlay is placed once at its map
    /// coordinates and never touched again; <see cref="Map3DView"/> has no such rect - the map is
    /// geometry inside a camera - so it re-places every registered overlay from the camera each frame.
    /// The marker builders cannot tell the difference, which is the point: they create a rect at
    /// <c>anchoredPosition = (x, z)</c>, register it, and let the host decide what that means.
    ///
    /// <see cref="OnViewChanged"/> is an event rather than a settable field so both sides can raise it
    /// and neither can have its subscribers replaced by the next caller - MapView subscribes twice
    /// (the label cull, and the at-rest pin names).
    /// </summary>
    internal interface IOverlayHost
    {
        /// <summary>Registers a child to be held at a constant on-screen size, whatever the view does.
        /// See <see cref="PanZoomHandler.KeepConstantScale"/>.</summary>
        /// <param name="child">The overlay's rect, already placed at its map coordinates.</param>
        void KeepConstantScale(RectTransform child);

        /// <summary>The view's scale RIGHT NOW, in screen pixels per map metre. What decides which place
        /// names fit and which at-rest pin names collide - see MapView.LabelCull, which is handed this at
        /// build time and again on every change. In 2D it is the container's own scale; in 3D it is the
        /// pixels a metre subtends at the focus distance, which is the same quantity measured a different
        /// way.</summary>
        float Scale { get; }

        /// <summary>Where a map point is on screen, in the canvas units an overlay's anchoredPosition is
        /// in. What decides whether two plated names overlap - a question that cannot be asked in map
        /// metres, because the names are a constant size on screen while the distance between their places
        /// is not.
        ///
        /// The two hosts answer it in the only way each can: the 2D one scales (a pan moves everything
        /// together and so cannot change an overlap, which is why it is deliberately left out), the 3D one
        /// projects through its camera, where an orbit changes every answer.
        ///
        /// May be NOT FINITE for a point the view cannot place - one behind the 3D camera. Callers treat
        /// such a point as not on screen: hidden, and claiming no space.</summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        Vector2 Project(Vector2 mapXZ);

        /// <summary>
        /// A number that changes whenever the view moves in a way the SCALE does not show - what a
        /// subscriber compares to decide whether an overlap decision taken earlier still holds.
        ///
        /// In 3D that is every orbit and every pan: the scale is the distance to the focus point and
        /// does not move, while every label on screen does. In 2D it is nothing at all - a pan moves
        /// every overlay together, and a zoom is already in the scale - so the 2D host answers a
        /// constant, and every decision it makes is exactly the one it made before this existed.
        /// </summary>
        int ViewVersion { get; }

        /// <summary>Raised whenever the view moves, with the scale in SCREEN PIXELS PER MAP METRE and
        /// the content's pan (which means nothing in 3D and is passed as zero there - no subscriber
        /// reads it).</summary>
        event System.Action<float, Vector2> OnViewChanged;

        /// <summary>Centres a map point in the viewport. The scale is a request: the 2D host zooms to
        /// it, the 3D host keeps its own distance and only moves the focus.</summary>
        /// <param name="contentPoint">The point, in map coordinates.</param>
        /// <param name="scale">The scale to show it at, in the same units as
        /// <see cref="OnViewChanged"/>.</param>
        void FocusOn(Vector2 contentPoint, float scale);
    }

    /// <summary>
    /// Drag-to-pan and scroll-to-zoom-about-the-cursor for any content rect.
    ///
    /// A MonoBehaviour by necessity: it is added to a viewport GameObject so Unity's event system
    /// delivers drag and scroll to it. Kept as a small component rather than a third-party UI
    /// package - see UILineConnector for the same reasoning.
    ///
    /// Lives in its own file, shared by the quest graph and the map view. Implementing IDragHandler
    /// and IScrollHandler is also what stops a surrounding ScrollRect stealing the gesture: Unity
    /// delivers to the first handler it finds walking up, so a viewport that handles them consumes
    /// them.
    /// </summary>
    internal sealed class PanZoomHandler : MonoBehaviour, IOverlayHost, IDragHandler, IScrollHandler
    {
        private RectTransform _content;
        private float _minZoom;
        private float _maxZoom;
        private float _zoomSpeed;

        /// <summary>Raised whenever the view moves, with the content's scale and pan. Lets a caller
        /// that rebuilds its UI put the view back where the user left it.
        ///
        /// An EVENT rather than a public field, so <see cref="IOverlayHost"/> can declare it and so a
        /// second subscriber cannot silently replace the first: MapView attaches the label cull here
        /// and then the at-rest pin names, and with a field the second <c>=</c> would have dropped the
        /// first - which is exactly the bug the 3D host's own subscribers would have hit.</summary>
        public event System.Action<float, Vector2> OnViewChanged;

        /// <summary>Children to hold at a constant on-screen size, whatever the content is zoomed
        /// to. Map markers and place names use it: magnifying a pin along with the map defeats the
        /// point of zooming in, which is to separate pins that overlap when zoomed out.</summary>
        private readonly List<RectTransform> _constantScale = new();

        /// <summary>Screen pixels per map metre: the content's own scale, since its local units ARE map
        /// metres. See <see cref="IOverlayHost.Scale"/>.</summary>
        public float Scale => _content != null ? _content.localScale.x : 1f;

        /// <summary>The map point in screen units: scaled, and NOT panned. See
        /// <see cref="IOverlayHost.Project"/> - the pan is a constant offset on every overlay at once, so
        /// it cancels in every comparison this is used for, and leaving it out keeps the answer the same
        /// whatever the view is panned to.</summary>
        /// <param name="mapXZ">The point, in map coordinates.</param>
        public Vector2 Project(Vector2 mapXZ) => mapXZ * Scale;

        /// <summary>Always zero: nothing a flat view does changes an overlap without changing the scale.
        /// See <see cref="IOverlayHost.ViewVersion"/> - this constant is what keeps the 2D cull's
        /// quarter-zoom hysteresis exactly as it was.</summary>
        public int ViewVersion => 0;

        public void Init(RectTransform content, float minZoom, float maxZoom, float zoomSpeed)
        {
            _content = content;
            _minZoom = minZoom;
            _maxZoom = maxZoom;
            _zoomSpeed = zoomSpeed;
        }

        /// <summary>Registers a child to be kept at a constant on-screen size. Applied immediately as
        /// well as on every zoom, so a child added before the first scroll is already correct.</summary>
        public void KeepConstantScale(RectTransform child)
        {
            if (child == null) return;

            _constantScale.Add(child);

            // Only the newcomer needs scaling; the rest were done when they registered or on the
            // last zoom. Re-walking the list here made a map build quadratic in its pin count.
            var contentScale = _content != null ? _content.localScale.x : 1f;
            if (Mathf.Approximately(contentScale, 0f)) return;
            child.localScale = new Vector3(1f / contentScale, 1f / contentScale, 1f);
        }

        /// <summary>
        /// Centres <paramref name="contentPoint"/> - a position in the content's own coordinates -
        /// in the viewport, at <paramref name="scale"/>.
        ///
        /// The `-point * scale` is only correct for content anchored AND pivoted at its centre,
        /// which is how the map's space rect is built (see MapView.BuildMapViewport, which uses the
        /// same expression to centre a map on first open). The quest graph's content is pivoted
        /// top-left and needs the viewport's half-size added instead - QuestGraphView.FrameNodes
        /// has that variant. Do not merge the two.
        ///
        /// Raises OnViewChanged like a real gesture would, so a caller persisting the view keeps
        /// the framing across a rebuild.
        /// </summary>
        public void FocusOn(Vector2 contentPoint, float scale)
        {
            if (_content == null) return;

            scale = Mathf.Clamp(scale, _minZoom, _maxZoom);

            _content.localScale = new Vector3(scale, scale, 1f);
            _content.anchoredPosition = -contentPoint * scale;

            // Pins and place names are counter-scaled, and this changed the zoom without going
            // through OnScroll - so they would keep the previous zoom's size without this.
            ApplyConstantScale(scale);

            OnViewChanged?.Invoke(scale, _content.anchoredPosition);
        }

        private void ApplyConstantScale(float contentScale)
        {
            if (Mathf.Approximately(contentScale, 0f)) return;

            var inverse = 1f / contentScale;

            foreach (var child in _constantScale)
            {
                if (child != null) child.localScale = new Vector3(inverse, inverse, 1f);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            // The old version was `delta / localScale.x`, which is wrong twice over:
            // anchoredPosition lives in the PARENT's space (the content's own localScale scales
            // its children, not where its pivot sits), so the zoom does not belong in this
            // sum - at 0.25 zoom a 100px drag threw the graph 400px, which is a large part of
            // why it was so easy to lose your place.
            //
            // Plain `+= delta` would still be wrong wherever the canvas has a scale factor,
            // since delta is in SCREEN pixels and anchoredPosition is in canvas units. Taking
            // the difference of two converted points is exact under any canvas scaling, and
            // whatever constant offset the conversion carries cancels in the subtraction.
            var parent = _content.parent as RectTransform;
            if (parent == null)
            {
                _content.anchoredPosition += eventData.delta;
                return;
            }

            var camera = ResolveEventCamera(eventData);

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent, eventData.position - eventData.delta, camera, out var previous) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parent, eventData.position, camera, out var current))
            {
                return;
            }

            _content.anchoredPosition += current - previous;

            OnViewChanged?.Invoke(_content.localScale.x, _content.anchoredPosition);
        }

        public void OnScroll(PointerEventData eventData)
        {
            var current = _content.localScale.x;
            var scale = Mathf.Clamp(current + eventData.scrollDelta.y * _zoomSpeed, _minZoom, _maxZoom);

            if (Mathf.Approximately(scale, current)) return; // already at a limit

            // Zoom about the cursor rather than the content's corner. The content is anchored
            // and pivoted top-left, so scaling alone drags everything toward that corner and
            // whatever you were pointing at slides away.
            //
            // Measure the cursor in content-local space, scale, measure again, and correct by
            // the difference - using RectTransformUtility rather than hand-rolled maths for the
            // same reason GetVisibleContentRect does: manual rect arithmetic in this hierarchy
            // has cost this project several rounds of debugging.
            var camera = ResolveEventCamera(eventData);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _content, eventData.position, camera, out var before);

            _content.localScale = new Vector3(scale, scale, 1f);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _content, eventData.position, camera, out var after);

            // The delta is in content-local units; anchoredPosition is in parent units, hence
            // the scale factor.
            _content.anchoredPosition += (after - before) * scale;

            ApplyConstantScale(scale);

            OnViewChanged?.Invoke(scale, _content.anchoredPosition);
        }

        /// <summary>The canvas above the content, found once: the parent walk ran on every drag
        /// delta and wheel notch that arrived without a camera of its own.</summary>
        private Canvas _canvas;

        /// <summary>The camera to interpret a screen point against. Null is CORRECT for a
        /// Screen Space Overlay canvas and wrong for Screen Space Camera, so this asks the
        /// canvas rather than assuming either - and eventData's own camera can be null on a
        /// scroll that had no preceding press.</summary>
        private Camera ResolveEventCamera(PointerEventData eventData)
        {
            if (eventData.pressEventCamera != null) return eventData.pressEventCamera;
            if (eventData.enterEventCamera != null) return eventData.enterEventCamera;

            if (_canvas == null && _content != null) _canvas = _content.GetComponentInParent<Canvas>();

            return _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _canvas.worldCamera
                : null;
        }
    }
}
