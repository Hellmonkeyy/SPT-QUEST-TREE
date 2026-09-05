using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>Draws one straight edge between two points in the same RectTransform's local
    /// space, as a thin rotated Image. No line-rendering package is referenced by this project, so
    /// this is built from a plain UI Image rather than depending on one.</summary>
    internal static class UILineConnector
    {
        public static RectTransform Create(RectTransform parent, Vector2 from, Vector2 to, Color color, float thickness)
        {
            var go = new GameObject("QuestEdge", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);

            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);

            Apply(rect, from, to, thickness);
            return rect;
        }

        /// <summary>Re-aims an existing line at a new pair of points. Split out from Create so a
        /// pooled edge can be repositioned instead of destroyed and rebuilt - panning a large tree
        /// otherwise churns thousands of GameObjects.</summary>
        public static void Apply(RectTransform rect, Vector2 from, Vector2 to, float thickness)
        {
            var delta = to - from;

            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;
            rect.sizeDelta = new Vector2(delta.magnitude, thickness);
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
    }
}
