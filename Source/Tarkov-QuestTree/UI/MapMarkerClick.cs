using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestTree.UI
{
    /// <summary>
    /// Makes a map marker clickable without taking the pan and zoom gestures away from the map.
    ///
    /// It implements <see cref="IPointerClickHandler"/> and DELIBERATELY NOT IDragHandler or
    /// IScrollHandler. Unity delivers a gesture to the first handler it finds walking UP from
    /// whatever the pointer hit, so a marker that handles only clicks lets drag and scroll continue
    /// up to the viewport's <see cref="PanZoomHandler"/> - dragging from a pin still pans the map,
    /// and the wheel over a pin still zooms it. <see cref="QuestNodeView"/> is clickable inside the
    /// quest graph's viewport on exactly this basis; this is the same arrangement.
    ///
    /// A click is also not fired after a drag: the input module clears the press's eligibility for
    /// a click once the pointer starts moving, so panning away from a pin does not open its quest.
    ///
    /// A Button would have done as much, but it wants a Graphic to tint and a transition to
    /// configure, and the marker's colour is already carrying quest status.
    /// </summary>
    internal sealed class MapMarkerClick : MonoBehaviour, IPointerClickHandler
    {
        public Action OnClicked;

        public void OnPointerClick(PointerEventData eventData) => OnClicked?.Invoke();
    }
}
