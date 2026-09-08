using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Draws one edge between two points in the same RectTransform's local space, as thin rotated
    /// Images. No line-rendering package is referenced by this project, so this is built from
    /// plain UI Images rather than depending on one.
    ///
    /// An edge is three segments - out of the source horizontally, down or up, into the target
    /// horizontally - rather than one diagonal. Diagonals across a dense tree cross everything
    /// between their ends at every angle and cannot be followed by eye; elbows run along the
    /// column gaps, so a line reads as "this column feeds that one" even when fifty of them share
    /// a gap. The three parts are one pooled unit (see QuestGraphView's edge pool).
    /// </summary>
    internal static class UILineConnector
    {
        public const int Segments = 3;

        /// <summary>An edge whose ends are closer than this horizontally has no gap to route
        /// through and is drawn straight instead.</summary>
        private const float MinRoutedSpan = 12f;

        /// <summary>One pooled edge: its three segments, and their Images kept alongside so a
        /// recolour - every built edge, every hover-falloff frame while zooming - writes a colour
        /// rather than looking a component up three times.</summary>
        public sealed class Line
        {
            public RectTransform[] Parts;
            public Image[] Images;
        }

        public static Line Create(RectTransform parent, Vector2 from, Vector2 to, Color color, float thickness)
        {
            var line = new Line { Parts = new RectTransform[Segments], Images = new Image[Segments] };

            for (var i = 0; i < Segments; i++)
            {
                var go = new GameObject("QuestEdge", typeof(RectTransform), typeof(Image));
                var rect = (RectTransform)go.transform;
                rect.SetParent(parent, worldPositionStays: false);

                var image = go.GetComponent<Image>();
                image.color = color;
                image.raycastTarget = false;

                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                line.Parts[i] = rect;
                line.Images[i] = image;
            }

            Apply(line, from, to, thickness);
            return line;
        }

        /// <summary>Re-aims an existing edge at a new pair of points. Split out from Create so a
        /// pooled edge can be repositioned instead of destroyed and rebuilt - panning a large tree
        /// otherwise churns thousands of GameObjects.</summary>
        public static void Apply(Line line, Vector2 from, Vector2 to, float thickness)
        {
            var parts = line?.Parts;
            if (parts == null || parts.Length < Segments) return;

            // The target sits in a later column in the normal case, which leaves a gap to turn in.
            // A backwards or same-column edge (a modded quest chain can do that) has no gap, so it
            // falls back to one straight line and two collapsed parts.
            if (to.x - from.x < MinRoutedSpan)
            {
                Segment(parts[0], from, to, thickness);
                Segment(parts[1], to, to, thickness);
                Segment(parts[2], to, to, thickness);
                return;
            }

            var midX = from.x + (to.x - from.x) * 0.5f;
            var corner1 = new Vector2(midX, from.y);
            var corner2 = new Vector2(midX, to.y);

            // Each horizontal part overshoots by half the thickness so the corners are square
            // rather than notched where the vertical part meets them.
            var overshoot = new Vector2(thickness * 0.5f, 0f);

            Segment(parts[0], from, corner1 + overshoot, thickness);
            Segment(parts[1], corner1, corner2, thickness);
            Segment(parts[2], corner2 - overshoot, to, thickness);
        }

        public static void SetColor(Line line, Color color)
        {
            if (line?.Images == null) return;

            foreach (var image in line.Images)
                if (image != null) image.color = color;
        }

        public static void SetActive(Line line, bool active)
        {
            if (line?.Parts == null) return;

            foreach (var part in line.Parts)
                if (part != null) part.gameObject.SetActive(active);
        }

        private static void Segment(RectTransform rect, Vector2 from, Vector2 to, float thickness)
        {
            var delta = to - from;
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = from;
            rect.sizeDelta = new Vector2(delta.magnitude, thickness);
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        }
    }
}
