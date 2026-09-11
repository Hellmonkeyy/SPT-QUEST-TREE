using System.Collections.Generic;
using System.Linq;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// What a quest box says when you rest on it, without a click.
    ///
    /// The box itself is small on purpose - small enough that hundreds fit on a screen - and there
    /// is a hard limit to what it can carry. This is the other half of that bargain: the box says
    /// which quest, whose, and roughly where you are; the card says what the quest actually wants.
    /// Reaching that by clicking was the single largest cost in using the tree, because a click
    /// also moves the detail panel off whatever you were comparing against.
    ///
    /// Drawn OUTSIDE the scrolling content, so it holds a constant size and is never clipped by the
    /// viewport mask. It reads only what the detail panel already reads, through the same helpers -
    /// QuestSummary.TryProgress and FormatReward - so the two cannot drift apart.
    /// </summary>
    internal sealed class QuestHoverCard
    {
        private const float Width = 340f;
        private const float Pad = 12f;
        private const float RowHeight = 17f;

        /// <summary>How long the pointer has to rest before the card appears.
        ///
        /// Long enough that sweeping the mouse across a dense tree does not strobe a card per box,
        /// short enough that resting on one feels like it answered rather than like it loaded.</summary>
        public const float Delay = 0.25f;

        private readonly RectTransform _root;
        private RectTransform _card;
        private RectTransform _body;
        private Image _background;

        public QuestHoverCard(RectTransform root) => _root = root;

        public bool Visible { get; private set; }

        /// <summary>Builds the card for a quest and places it near the pointer.</summary>
        public void Show(QuestNode node, ProfilePayloadDto profile, QuestGraphBuilder graph,
            string searchReason, Vector2 screenPoint)
        {
            if (_root == null || node == null) return;

            EnsureCard();
            Rebuild(node, profile, graph, searchReason);

            Visible = true;
            _card.gameObject.SetActive(true);

            Place(screenPoint);
        }

        public void Hide()
        {
            Visible = false;
            if (_card != null && _card.gameObject.activeSelf) _card.gameObject.SetActive(false);
        }

        /// <summary>Drops the card entirely, for a rebuild of the graph.</summary>
        public void Clear()
        {
            if (_card != null) Object.Destroy(_card.gameObject);

            _card = null;
            _body = null;
            _background = null;
            Visible = false;
        }

        // ------------------------------------------------------------------ placement

        /// <summary>Beside the pointer, flipped and clamped so the whole card stays on screen.
        ///
        /// Offset rather than centred, so the card never sits under the cursor and covers the box
        /// you are pointing at.</summary>
        private void Place(Vector2 screenPoint)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _root, screenPoint, null, out var local))
            {
                return;
            }

            var height = _card.sizeDelta.y;
            var bounds = _root.rect;

            var x = local.x + 18f;
            var y = local.y - 12f;

            // Flip to the other side of the pointer rather than letting the card run off the edge.
            if (x + Width > bounds.xMax) x = local.x - 18f - Width;
            if (y - height < bounds.yMin) y = local.y + 12f + height;

            _card.anchoredPosition = new Vector2(
                Mathf.Clamp(x, bounds.xMin + 4f, bounds.xMax - Width - 4f),
                Mathf.Clamp(y, bounds.yMin + height + 4f, bounds.yMax - 4f));
        }

        // ------------------------------------------------------------------ construction

        private void EnsureCard()
        {
            if (_card != null) return;

            var go = new GameObject("QuestHoverCard", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            _card = (RectTransform)go.transform;
            _card.SetParent(_root, worldPositionStays: false);
            _card.anchorMin = _card.anchorMax = new Vector2(0f, 0f);
            _card.pivot = new Vector2(0f, 1f);

            _background = go.GetComponent<Image>();
            _background.color = new Color(0.07f, 0.07f, 0.065f, 0.97f);
            GameStyle.ApplyPanel(_background);

            // Never eats a click or a hover: the card is a readout, and swallowing the pointer
            // would make the box under it unhoverable and so make the card flicker.
            var group = go.GetComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            _body = (RectTransform)new GameObject("Body", typeof(RectTransform)).transform;
            _body.SetParent(_card, worldPositionStays: false);
            _body.anchorMin = new Vector2(0f, 1f);
            _body.anchorMax = new Vector2(1f, 1f);
            _body.pivot = new Vector2(0f, 1f);
            _body.offsetMin = new Vector2(0f, 0f);
            _body.offsetMax = new Vector2(0f, 0f);
        }

        private void Rebuild(QuestNode node, ProfilePayloadDto profile, QuestGraphBuilder graph,
            string searchReason)
        {
            for (var i = _body.childCount - 1; i >= 0; i--)
                Object.Destroy(_body.GetChild(i).gameObject);

            var inner = Width - Pad * 2f;
            var y = Pad;

            // --- title, with the level gate as a badge on the same line
            var levelText = node.Level > 0 ? $"Lv {node.Level}" : null;
            var titleWidth = string.IsNullOrEmpty(levelText) ? inner : inner - 54f;

            AuxLayout.AddWrapped(_body, $"<b>{GameStyle.Safe(node.Name)}</b>", Pad, ref y, titleWidth, 15);

            if (!string.IsNullOrEmpty(levelText))
            {
                var badgeY = Pad + 1f;
                var badgeX = Width - Pad - 50f;
                AuxLayout.AddChip(_body, levelText, QuestNodeView.ColorFor(node.Status), ref badgeX, badgeY);
            }

            // --- who, how far in
            var parts = new List<string> { GameStyle.Safe(node.TraderName) };

            var loyalty = LoyaltyOf(node.TraderId, profile);
            if (loyalty > 0) parts.Add($"LL{loyalty}");

            if (node.Depth > 0) parts.Add($"{node.Depth} quests deep on this branch");

            // The map, which the box gave up when its meta row took the objective count.
            if (!string.IsNullOrEmpty(node.LocationId) && node.LocationId != QuestNode.AnyLocation)
                parts.Add(GameStyle.Safe(node.LocationId));

            AuxLayout.AddLabelAt(_body, $"<color=#FFFFFF80>{string.Join("  ·  ", parts)}</color>",
                Pad, ref y, RowHeight, 11, inner);

            y += 6f;

            // --- objectives, with the counter where the profile has one
            var objectives = node.NecessaryObjectives.Take(8).ToList();

            if (objectives.Count > 0)
            {
                foreach (var objective in objectives)
                {
                    var progress = QuestSummary.TryProgress(objective, profile, out var current, out var target) && target > 0
                        ? $"<color=#{QuestNodeView.HexFor(current >= target ? ENodeStatus.Completed : ENodeStatus.Active)}>{current}/{target}</color>"
                        : "<color=#FFFFFF40>—</color>";

                    var rowY = y;
                    AuxLayout.AddWrapped(_body, GameStyle.Safe(objective.Text), Pad, ref y, inner - 46f, 12);

                    // Right-aligned against the same row the text started on, so a wrapped
                    // objective keeps its counter level with its first line.
                    var counter = AuxLayout.AddLabelAt(_body, progress, Width - Pad - 40f, ref rowY, RowHeight, 12, 40f);
                    if (counter != null) counter.alignment = TextAlignmentOptions.TopRight;
                }

                y += 6f;
            }

            // --- rewards, in the vocabulary the boxes use
            var rewards = node.Rewards
                .Select(r => QuestSummary.FormatReward(r, graph))
                .Where(r => !string.IsNullOrEmpty(r))
                .Take(4)
                .ToList();

            if (rewards.Count > 0)
            {
                AuxLayout.AddWrapped(_body, $"<color=#FFFFFFA0>{string.Join("   ", rewards)}</color>",
                    Pad, ref y, inner, 11);
            }

            // Why a search matched this box, when the reason is not its own name. A quest called
            // "Debut" matching "PL-15" explains nothing on its own, and the box has no room to
            // explain itself.
            if (!string.IsNullOrEmpty(searchReason))
            {
                y += 4f;
                AuxLayout.AddWrapped(_body,
                    $"<color=#{GameStyle.WarningHex}>matched on {GameStyle.Safe(searchReason)}</color>",
                    Pad, ref y, inner, 11);
            }

            _card.sizeDelta = new Vector2(Width, y + Pad);
        }

        private static int LoyaltyOf(string traderId, ProfilePayloadDto profile)
        {
            if (string.IsNullOrEmpty(traderId) || profile?.Traders == null) return 0;

            foreach (var trader in profile.Traders)
                if (trader != null && trader.Id == traderId) return trader.LoyaltyLevel;

            return 0;
        }
    }
}
