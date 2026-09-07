using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuestTree.UI
{
    /// <summary>
    /// Makes a map marker clickable and hoverable without taking the pan and zoom gestures away
    /// from the map.
    ///
    /// It implements the pointer click/enter/exit handlers and DELIBERATELY NOT IDragHandler or
    /// IScrollHandler. Unity delivers a gesture to the first handler it finds walking UP from
    /// whatever the pointer hit, so a marker that handles only clicks and hovers lets drag and
    /// scroll continue up to the viewport's <see cref="PanZoomHandler"/> - dragging from a pin
    /// still pans the map, and the wheel over a pin still zooms it. <see cref="QuestNodeView"/> is
    /// clickable inside the quest graph's viewport on exactly this basis; this is the same
    /// arrangement.
    ///
    /// A click is also not fired after a drag: the input module clears the press's eligibility for
    /// a click once the pointer starts moving, so panning away from a pin does not open its quest.
    ///
    /// Hover is what shows a pin's name: with every name drawn at once, a cluster of objectives
    /// was a block of overlapping text, and the pin you wanted was the one you could not read.
    /// </summary>
    internal sealed class MapMarkerClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public Action OnClicked;
        public Action<bool> OnHover;

        public void OnPointerClick(PointerEventData eventData) => OnClicked?.Invoke();
        public void OnPointerEnter(PointerEventData eventData) => OnHover?.Invoke(true);
        public void OnPointerExit(PointerEventData eventData) => OnHover?.Invoke(false);
    }
}
