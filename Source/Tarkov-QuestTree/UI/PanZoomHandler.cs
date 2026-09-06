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

        public void Init(RectTransform content, float minZoom, float maxZoom, float zoomSpeed)
        {
            _content = content;
            _minZoom = minZoom;
            _maxZoom = maxZoom;
            _zoomSpeed = zoomSpeed;
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
        }

        /// <summary>The camera to interpret a screen point against. Null is CORRECT for a
        /// Screen Space Overlay canvas and wrong for Screen Space Camera, so this asks the
        /// canvas rather than assuming either - and eventData's own camera can be null on a
        /// scroll that had no preceding press.</summary>
        private Camera ResolveEventCamera(PointerEventData eventData)
        {
            if (eventData.pressEventCamera != null) return eventData.pressEventCamera;
            if (eventData.enterEventCamera != null) return eventData.enterEventCamera;

            var canvas = _content != null ? _content.GetComponentInParent<Canvas>() : null;
            return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
        }
    }
}
