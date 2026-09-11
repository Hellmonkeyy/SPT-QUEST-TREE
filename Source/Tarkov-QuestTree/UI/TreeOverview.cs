using System;
using System.Collections.Generic;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// What the tree becomes once it is too far out to read: one card per trader instead of
    /// hundreds of boxes.
    ///
    /// The problem it solves is arithmetic rather than aesthetic. Text drawn at a constant size on
    /// screen needs roughly 40px of height; the gap between two rows is 29px at half zoom and 12px
    /// at a fifth, because the gap shrinks with the zoom and the text does not. There is no zoom at
    /// which a quest name fits between two rows, which is why labelling the zoomed-out tree failed
    /// repeatedly and why reserving lanes for labels would have failed too - a lane measured in tree
    /// units shrinks on screen like everything else.
    ///
    /// So at that distance the tree stops drawing quests at all. A dozen cards have room for real
    /// text in a way eight hundred boxes never will, and the overview becomes something you navigate
    /// with - click a trader, land in their chains - rather than a picture of a wall.
    ///
    /// CARDS, NOT REGIONS. The first version drew each band as a translucent rectangle over the
    /// bounds of that trader's own quests, on the theory that a region visibly collapsing into a
    /// block reads better than a legend floating above the tree. That theory required a trader's
    /// quests to occupy a region, and they do not: prerequisite chains cross traders - Prapor's
    /// "First Step" runs straight into Aishi's - so on the All tab every trader's bounding box is
    /// very nearly the bounding box of the whole tree. Thirteen of those stacked on top of each
    /// other is a full-screen grey wash, and on a single-trader tab it is one rectangle covering
    /// everything. The same wrong assumption put the gutter portraits in a column that lined up
    /// with nothing.
    ///
    /// A card does not claim to be anywhere. It says which trader, how they stand, and takes you
    /// there when clicked - which is all the overview was ever for.
    /// </summary>
    internal sealed class TreeOverview
    {
        /// <summary>One trader's band: which quests are theirs, and how they stand.</summary>
        internal sealed class Band
        {
            public string TraderId;
            public string TraderName;
            public Rect Bounds;
            public int Active;
            public int Available;
            public int Remaining;
        }

        // Card geometry, in SCREEN pixels. Converted to content units per frame, so a card is the
        // same size on screen however far out the tree is - which is the whole point of the tier.
        private const float CardWidth = 300f;
        private const float CardHeight = 56f;
        private const float CardGap = 10f;

        /// <summary>Past this many traders the cards run in two columns rather than one. Thirteen
        /// in a single column is taller than most screens.</summary>
        private const int TwoColumnThreshold = 8;

        private readonly RectTransform _content;
        private readonly List<GameObject> _cards = new List<GameObject>();

        public TreeOverview(RectTransform content) => _content = content;

        /// <summary>Whether the collapsed tier is showing, so the caller can skip the work the
        /// detailed tier would otherwise do.</summary>
        public bool Active { get; private set; }

        /// <summary>Draws the cards, or hides them.
        ///
        /// <paramref name="centre"/> is where the tree is, so the block of cards sits over it and
        /// pans with it rather than being pinned to a corner of a canvas that may be half a million
        /// pixels tall. <paramref name="onPick"/> frames the trader's quests - which is what makes
        /// this navigation rather than decoration.</summary>
        public void Draw(IReadOnlyList<Band> bands, float zoom, bool collapsed, Vector2 centre, Action<Band> onPick)
        {
            if (_content == null) return;

            if (!collapsed || bands == null || bands.Count == 0)
            {
                if (Active) Hide();
                Active = false;
                return;
            }

            Active = true;

            if (_cards.Count != bands.Count) Rebuild(bands, onPick);

            // Screen pixels into content units. Everything below is laid out in content units so it
            // holds a constant size on screen as the zoom moves.
            var scale = zoom > 0.0001f ? 1f / zoom : 1f;

            var cardW = CardWidth * scale;
            var cardH = CardHeight * scale;
            var gap = CardGap * scale;

            var columns = bands.Count > TwoColumnThreshold ? 2 : 1;
            var rows = Mathf.CeilToInt(bands.Count / (float)columns);

            var blockW = columns * cardW + (columns - 1) * gap;
            var blockH = rows * cardH + (rows - 1) * gap;

            var left = centre.x - blockW * 0.5f;
            var top = centre.y + blockH * 0.5f;

            for (var i = 0; i < _cards.Count && i < bands.Count; i++)
            {
                var card = _cards[i];
                if (card == null) continue;

                // Down the first column, then the second - so reading order matches the sort order
                // the caller put them in.
                var column = i / rows;
                var row = i % rows;

                var rect = (RectTransform)card.transform;
                rect.sizeDelta = new Vector2(cardW, cardH);
                rect.anchoredPosition = new Vector2(
                    left + column * (cardW + gap),
                    top - row * (cardH + gap));

                // The text is a child of a rect that is already scaled up by 1/zoom, so it has to be
                // scaled back down or it would grow twice.
                foreach (var label in card.GetComponentsInChildren<TMP_Text>(includeInactive: true))
                    label.transform.localScale = Vector3.one * scale;

                foreach (var slab in card.GetComponentsInChildren<RectTransform>(includeInactive: true))
                    if (slab.name == "Slab") slab.sizeDelta = new Vector2(4f * scale, 0f);

                if (!card.activeSelf) card.SetActive(true);
            }
        }

        private void Rebuild(IReadOnlyList<Band> bands, Action<Band> onPick)
        {
            Clear();

            foreach (var band in bands)
                _cards.Add(Create(band, onPick));
        }

        private GameObject Create(Band band, Action<Band> onPick)
        {
            var colour = TraderPalette.For(band.TraderId);

            var go = new GameObject($"Band_{band.TraderId}", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_content, worldPositionStays: false);

            // The same anchor a quest box uses - Unity's default centre - so a card sits where the
            // arithmetic above says it does. Pivoted top-left, because the grid is laid out from its
            // top-left corner.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0f, 1f);

            var fill = go.GetComponent<Image>();
            fill.color = new Color(0.10f, 0.10f, 0.09f, 0.96f);
            GameStyle.ApplyPanel(fill);

            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => onPick?.Invoke(band));

            // The trader's colour down the left edge, the same signal the quest boxes carry.
            var slabGo = new GameObject("Slab", typeof(RectTransform), typeof(Image));
            var slabRect = (RectTransform)slabGo.transform;
            slabRect.SetParent(rect, worldPositionStays: false);
            slabRect.anchorMin = new Vector2(0f, 0f);
            slabRect.anchorMax = new Vector2(0f, 1f);
            slabRect.pivot = new Vector2(0f, 0.5f);
            slabRect.anchoredPosition = Vector2.zero;

            var slab = slabGo.GetComponent<Image>();
            slab.color = colour;
            slab.raycastTarget = false;

            var labelGo = new GameObject("BandLabel", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = new Vector2(0f, 0.5f);
            labelRect.anchorMax = new Vector2(0f, 0.5f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = new Vector2(14f, 0f);
            labelRect.sizeDelta = new Vector2(CardWidth - 20f, CardHeight - 8f);

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
            foreach (var card in _cards)
                if (card != null && card.activeSelf) card.SetActive(false);
        }

        /// <summary>Drops the cards, for a graph rebuild.</summary>
        public void Clear()
        {
            foreach (var card in _cards)
                if (card != null) UnityEngine.Object.Destroy(card);

            _cards.Clear();
            Active = false;
        }
    }
}
