using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestTree.UI
{
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
    internal sealed class PanZoomHandler : MonoBehaviour, IDragHandler, IScrollHandler
    {
        private RectTransform _content;
        private float _minZoom;
        private float _maxZoom;
        private float _zoomSpeed;

        /// <summary>Raised whenever the view moves, with the content's scale and pan. Lets a caller
        /// that rebuilds its UI put the view back where the user left it.</summary>
        public System.Action<float, Vector2> OnViewChanged;

        /// <summary>Children to hold at a constant on-screen size, whatever the content is zoomed
        /// to. Map markers and place names use it: magnifying a pin along with the map defeats the
        /// point of zooming in, which is to separate pins that overlap when zoomed out.</summary>
        private readonly List<RectTransform> _constantScale = new();

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

        /// <summary>The camera to interpret a screen point against. Null is CORRECT for a
        /// Screen Space Overlay canvas and wrong for Screen Space Camera, so this asks the
        /// canvas rather than assuming either - and eventData's own camera can be null on a
        /// scroll that had no preceding press.</summary>
        /// <summary>The canvas above the content, found once: the parent walk ran on every drag
        /// delta and wheel notch that arrived without a camera of its own.</summary>
        private Canvas _canvas;

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
