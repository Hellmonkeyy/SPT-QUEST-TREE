using QuestTree.QuestGraph;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// A few readable labels drawn over the tree when the boxes themselves have stopped being
    /// readable.
    ///
    /// The problem this solves is structural rather than cosmetic. A box at full zoom is genuinely
    /// informative - status, title, trader, level, map, first objective, badges - but you can only
    /// see a handful of 830 at that zoom, and zooming out to see the shape drops the subtitle, then
    /// the objective, then replaces the title with a short code. So structure and information were
    /// mutually exclusive, and the OVERVIEW - the whole reason a tree exists - was the view that
    /// said least.
    ///
    /// Labels are drawn at constant SCREEN size, the same trick the map's place names use, so they
    /// stay legible however far out the zoom goes. A small ranked number of them, placed
    /// highest-priority first and skipping anything that would collide with one already placed -
    /// which is how a map draws city names. The result is landmarks rather than either silence or a
    /// wall of overlapping text.
    ///
    /// Nothing here may touch the boxes, the edges or the virtualisation. If the budget is wrong the
    /// overview is busy or sparse rather than broken; if a label is mispositioned it is one label,
    /// not a layout.
    /// </summary>
    internal sealed class TreeLabelLayer
    {
        /// <summary>Font size in SCREEN pixels, held constant by counter-scaling against the
        /// content's zoom.</summary>
        private const float FontSize = 13f;

        /// <summary>Padding around a label's measured rect when testing for collisions, so two
        /// labels never quite touch.</summary>
        private const float CollisionPadding = 4f;

        private readonly RectTransform _content;
        private readonly List<TMP_Text> _pool = new List<TMP_Text>();

        /// <summary>Scratch, reused every pass. This runs on every frame the view moves, in the one
        /// file whose buffers exist precisely so the sweep allocates nothing per frame.</summary>
        private readonly List<Rect> _placed = new List<Rect>();

        public TreeLabelLayer(RectTransform content) => _content = content;

        /// <summary>Draws the labels for this frame.
        ///
        /// <paramref name="ranked"/> is already in priority order: the selected quest and its chain
        /// first, then search matches, then quests that are actually actionable. Ties inside a rank
        /// break on distance from the viewport centre, so the labels that appear are the ones
        /// nearest what you are looking at.</summary>
        public void Draw(IReadOnlyList<(QuestNode Node, Vector2 Position)> ranked, float zoom, int budget)
        {
            if (_content == null) return;

            _placed.Clear();

            // Nothing to say at full zoom: the boxes are readable, and a second copy of the title
            // floating over them is noise.
            if (budget <= 0 || ranked == null || ranked.Count == 0 || zoom >= LayoutMetrics.DetailLevelZoom)
            {
                Hide(0);
                return;
            }

            // Counter-scaled so the text is the same size on screen at any zoom. MapView already
            // does this for place names with the shared outlined material, so the old worry about
            // font materials does not apply - that bug was per-label material INSTANCING, not
            // scaling.
            var inverse = zoom > 0.0001f ? 1f / zoom : 1f;
            var used = 0;

            for (var i = 0; i < ranked.Count && used < budget; i++)
            {
                var node = ranked[i].Node;
                if (node == null) continue;

                var text = node.Name;
                if (string.IsNullOrEmpty(text)) continue;

                var label = Acquire(used);

                // Assigned only when it changed: setting TMP text re-lays it out, and this runs
                // through every frame of a drag.
                if (label.text != text) label.text = text;

                // Measured through GameStyle, which floors TMP's answer at a character estimate. An
                // under-measured rect makes the collision test pass and two labels overlap, which
                // is the one thing this layer exists to prevent.
                var width = GameStyle.MeasureWidth(label, text) * inverse;
                var height = FontSize * 1.4f * inverse;

                var position = ranked[i].Position;
                var rect = new Rect(position.x, position.y - height * 0.5f, width, height);

                if (Collides(rect)) continue;

                _placed.Add(rect);

                var transform = (RectTransform)label.transform;
                transform.localScale = Vector3.one * inverse;
                transform.anchoredPosition = position;
                transform.sizeDelta = new Vector2(width, height);

                label.color = QuestNodeView.ColorFor(node.Status);
                if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);

                used++;
            }

            Hide(used);
        }

        /// <summary>Against PLACED labels only, never against candidates not yet considered.
        ///
        /// That ordering is the whole design: testing against everything would let a low-priority
        /// label block the selected quest's own chain, and a selected quest losing its label to an
        /// unrelated one is worse than having no labels at all.</summary>
        private bool Collides(Rect rect)
        {
            var padded = new Rect(
                rect.x - CollisionPadding, rect.y - CollisionPadding,
                rect.width + CollisionPadding * 2f, rect.height + CollisionPadding * 2f);

            for (var i = 0; i < _placed.Count; i++)
                if (padded.Overlaps(_placed[i])) return true;

            return false;
        }

        /// <summary>Pooled and reused by index, surplus deactivated rather than destroyed. Building
        /// GameObjects per pass would allocate through every frame of a drag.</summary>
        private TMP_Text Acquire(int index)
        {
            while (_pool.Count <= index)
            {
                var go = new GameObject($"TreeLabel{_pool.Count}", typeof(RectTransform));
                var rect = (RectTransform)go.transform;

                rect.SetParent(_content, worldPositionStays: false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0f, 0.5f);

                var label = go.AddComponent<TextMeshProUGUI>();
                label.fontSize = FontSize;
                label.alignment = TextAlignmentOptions.Left;
                label.enableWordWrapping = false;
                label.overflowMode = TextOverflowModes.Overflow;

                // Or the layer eats drags over the viewport, which would make the tree unpannable
                // wherever a label happens to sit.
                label.raycastTarget = false;

                GameStyle.ApplyOutlined(label);
                _pool.Add(label);
            }

            return _pool[index];
        }

        private void Hide(int from)
        {
            for (var i = from; i < _pool.Count; i++)
                if (_pool[i] != null && _pool[i].gameObject.activeSelf) _pool[i].gameObject.SetActive(false);
        }

        /// <summary>Drops the pool, for a graph rebuild - the labels belong to the content and would
        /// otherwise outlive the nodes they name.</summary>
        public void Clear()
        {
            foreach (var label in _pool)
                if (label != null) UnityEngine.Object.Destroy(label.gameObject);

            _pool.Clear();
            _placed.Clear();
        }
    }
}
