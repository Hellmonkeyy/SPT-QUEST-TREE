using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestTree.UI
{
    /// <summary>
    /// Drag-to-move, right-drag-to-turn and scroll-to-come-closer for the 3D map, on the same viewport
    /// object <see cref="PanZoomHandler"/> goes on in 2D - one or the other, never both.
    ///
    /// A MonoBehaviour for the same reason that one is: Unity's event system delivers drag and scroll to
    /// components, and implementing <see cref="IDragHandler"/> and <see cref="IScrollHandler"/> here is
    /// also what stops the aux panel's surrounding ScrollRect stealing the gesture - Unity delivers to
    /// the first handler it finds walking up the hierarchy, so a viewport that handles them consumes
    /// them.
    ///
    /// The gestures are LEFT-drag to slide the map under the cursor, RIGHT-drag to turn and tilt around
    /// the point being looked at, and the wheel to come closer. Left for the common one on purpose: the
    /// 2D map has always panned on a left drag, and a player who never discovers the right button still
    /// has the view they had before, from a nicer angle.
    ///
    /// All the arithmetic lives in <see cref="Map3DView"/>; this class is the input surface and nothing
    /// else, which is what makes the orbit testable by reading one file.
    /// </summary>
    internal sealed class OrbitHandler : MonoBehaviour, IDragHandler, IScrollHandler
    {
        private Map3DView _view;

        /// <summary>Points this handler at the view it drives.</summary>
        /// <param name="view">The 3D view on this same viewport.</param>
        internal void Init(Map3DView view) => _view = view;

        public void OnDrag(PointerEventData eventData)
        {
            // Unity's null: the view is destroyed with the viewport, and a drag already in flight can
            // deliver one more frame after that.
            if (_view == null || eventData == null) return;

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                // Right is the turn. Yaw follows the cursor horizontally; dragging UP tilts towards
                // looking down on the map, which is the direction that feels like lifting your head.
                _view.Orbit(
                    eventData.delta.x * Map3DView.OrbitDegreesPerPixel,
                    eventData.delta.y * Map3DView.OrbitDegreesPerPixel);

                return;
            }

            // Left (and middle, which nothing else uses here) slides the map.
            _view.PanBy(eventData.delta);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (_view == null || eventData == null) return;

            _view.Dolly(eventData.scrollDelta.y);
        }
    }
}
