using System;
using System.Collections.Generic;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// What the tree becomes once it is too far out to read: one block per trader instead of
    /// hundreds of boxes.
    ///
    /// The problem it solves is arithmetic rather than aesthetic. Text drawn at a constant size on
    /// screen needs roughly 40px of height; the gap between two rows is 29px at half zoom and 12px
    /// at a fifth, because the gap shrinks with the zoom and the text does not. There is no zoom at
    /// which a quest name fits between two rows, which is why labelling the zoomed-out tree failed
    /// repeatedly and why reserving lanes for labels would have failed too - a lane measured in tree
    /// units shrinks on screen like everything else.
    ///
    /// So at that distance the tree stops drawing quests at all. A dozen blocks have room for real
    /// text in a way eight hundred boxes never will, and the overview becomes something you navigate
    /// with - click a trader, land in their chains - rather than a picture of a wall.
    /// </summary>
    internal sealed class TreeOverview
    {
        /// <summary>One trader's band: where it is, and what is in it.</summary>
        internal sealed class Band
        {
            public string TraderId;
            public string TraderName;
            public Rect Bounds;
            public int Active;
            public int Available;
            public int Remaining;
        }

        private readonly RectTransform _content;
        private readonly List<GameObject> _blocks = new List<GameObject>();

        public TreeOverview(RectTransform content) => _content = content;

        /// <summary>Whether the collapsed tier is showing, so the caller can skip the work the
        /// detailed tier would otherwise do.</summary>
        public bool Active { get; private set; }

        /// <summary>Draws the bands, or hides them.
        ///
        /// <paramref name="onPick"/> frames a band's bounds - which is what makes this navigation
        /// rather than decoration.</summary>
        public void Draw(IReadOnlyList<Band> bands, float zoom, bool collapsed, Action<Band> onPick)
        {
            if (_content == null) return;

            if (!collapsed || bands == null || bands.Count == 0)
            {
                if (Active) Hide();
                Active = false;
                return;
            }

            Active = true;

            // Rebuilt only when the set changes; the per-frame work is the counter-scale below.
            if (_blocks.Count != bands.Count) Rebuild(bands, onPick);

            // Constant size on screen, like the trader portraits. Affordable here precisely because
            // there are a dozen of these rather than a tree's worth.
            var inverse = zoom > 0.0001f ? 1f / zoom : 1f;

            for (var i = 0; i < _blocks.Count && i < bands.Count; i++)
            {
                var block = _blocks[i];
                if (block == null) continue;

                var rect = (RectTransform)block.transform;
                var bounds = bands[i].Bounds;

                // Positioned over the band it stands for, so it reads as that region collapsing
                // rather than as a legend floating above the tree.
                rect.anchoredPosition = new Vector2(bounds.xMin, bounds.center.y);

                var label = block.GetComponentInChildren<TMP_Text>();
                if (label != null) label.transform.localScale = Vector3.one * inverse;

                if (!block.activeSelf) block.SetActive(true);
            }
        }

        private void Rebuild(IReadOnlyList<Band> bands, Action<Band> onPick)
        {
            Hide();

            foreach (var block in _blocks)
                if (block != null) UnityEngine.Object.Destroy(block);

            _blocks.Clear();

            foreach (var band in bands)
                _blocks.Add(Create(band, onPick));
        }

        private GameObject Create(Band band, Action<Band> onPick)
        {
            var colour = TraderPalette.For(band.TraderId);

            var go = new GameObject($"Band_{band.TraderId}", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_content, worldPositionStays: false);

            // The same anchor a quest box uses - Unity's default centre - so a band sits where the
            // layout says it does. Getting this wrong is what put the trader portraits off centre.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(Mathf.Max(band.Bounds.width, 1f), Mathf.Max(band.Bounds.height, 1f));

            var fill = go.GetComponent<Image>();
            fill.color = new Color(colour.r, colour.g, colour.b, 0.16f);
            GameStyle.ApplyPanel(fill);

            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => onPick?.Invoke(band));

            // One counter-scaled label, anchored to the band's left so it stays put as the band
            // changes size with the layout.
            var labelGo = new GameObject("BandLabel", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = labelRect.anchorMax = new Vector2(0f, 0.5f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = new Vector2(12f, 0f);
            labelRect.sizeDelta = new Vector2(320f, 64f);

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.fontSize = 15;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            GameStyle.ApplyOutlined(label);

            var counts = band.Active > 0 || band.Available > 0
                ? $"<size=11><color=#{QuestNodeView.HexFor(ENodeStatus.Active)}>{band.Active} in progress</color>" +
                  $"   <color=#{QuestNodeView.HexFor(ENodeStatus.Available)}>{band.Available} available</color>" +
                  $"   <color=#FFFFFF80>{band.Remaining} left</color></size>"
                : $"<size=11><color=#FFFFFF80>{band.Remaining} left</color></size>";

            label.text = $"<b>{GameStyle.Safe(band.TraderName)}</b>\n{counts}";
            label.color = colour;

            return go;
        }

        private void Hide()
        {
            foreach (var block in _blocks)
                if (block != null && block.activeSelf) block.SetActive(false);
        }

        /// <summary>Drops the blocks, for a graph rebuild.</summary>
        public void Clear()
        {
            foreach (var block in _blocks)
                if (block != null) UnityEngine.Object.Destroy(block);

            _blocks.Clear();
            Active = false;
        }
    }
}
