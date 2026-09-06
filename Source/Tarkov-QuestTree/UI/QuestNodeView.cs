using System;
using System.Linq;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>Compact visual for one QuestNode: a status-colored box with title, a
    /// trader/level/map subtitle, a one-line objective preview, and a Kappa badge. Built entirely
    /// from runtime UI primitives - this project ships no AssetBundle, so there is no authored
    /// prefab to instantiate instead.</summary>
    internal sealed class QuestNodeView : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Node size now follows the layout-density setting - see UI/LayoutMetrics.cs.
        /// Kept under these names so every existing call site (framing, visibility, edge maths)
        /// reads unchanged.</summary>
        /// <summary>How far a node fades when it is not part of the hovered quest's chain. Low
        /// enough to recede, high enough that the shape of the rest of the tree is still readable.</summary>
        private const float DimmedAlpha = 0.25f;

        public static float Width => LayoutMetrics.NodeWidth;
        public static float Height => LayoutMetrics.NodeHeight;

        // Matches the vanilla quest-status convention (Tasks screen): grey/white/amber/green,
        // rather than the arbitrary blue "available" this started with.
        private static readonly Color LockedColor = new(0.35f, 0.35f, 0.35f, 0.9f);
        private static readonly Color AvailableColor = new(0.82f, 0.82f, 0.8f, 0.9f);
        private static readonly Color ActiveColor = new(0.95f, 0.8f, 0.25f, 0.95f);
        private static readonly Color CompletedColor = new(0.35f, 0.85f, 0.4f, 0.95f);

        /// <summary>Shared with the legend so the two can never drift apart.</summary>
        public static Color ColorFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => CompletedColor,
            ENodeStatus.Active => ActiveColor,
            ENodeStatus.Available => AvailableColor,
            _ => LockedColor
        };

        /// <summary>A glyph per status, so state is not carried by colour alone - Available
        /// (near-white) and Locked (grey) are otherwise easy to confuse, especially zoomed out.</summary>
        public static string GlyphFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => "✓",   // check
            ENodeStatus.Active => "▶",      // play
            ENodeStatus.Available => "○",   // hollow circle
            _ => "✕"                        // cross
        };

        public static string NameFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => "Completed",
            ENodeStatus.Active => "In progress",
            ENodeStatus.Available => "Available",
            _ => "Locked"
        };

        private Image _border;
        private TMP_Text _statusGlyph;
        private TMP_Text _title;
        private TMP_Text _subtitle;
        private TMP_Text _objectivePreview;
        private GameObject _kappaBadge;

        /// <summary>Dims the whole node in one operation when another quest's chain is highlighted.
        /// A CanvasGroup is one component and one float, versus recolouring every child graphic.</summary>
        private CanvasGroup _canvasGroup;

        public QuestNode Node { get; private set; }
        public Action<QuestNode> OnClicked;
        public Action<QuestNode> OnHoverEnter;
        public Action<QuestNode> OnHoverExit;

        public static QuestNodeView Create(RectTransform parent)
        {
            var go = new GameObject("QuestNode", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.sizeDelta = new Vector2(Width, Height);
            rect.pivot = new Vector2(0f, 0.5f);

            var border = go.GetComponent<Image>();
            border.raycastTarget = true;
            GameStyle.ApplyPanel(border);

            var view = go.AddComponent<QuestNodeView>();
            view._canvasGroup = go.AddComponent<CanvasGroup>();
            // Dimming must never make a node unclickable - hovering a neighbour dims this one, and
            // it still has to accept the click that would select it.
            view._canvasGroup.blocksRaycasts = true;
            view._canvasGroup.interactable = true;
            view._border = border;
            view._title = CreateText(rect, "Title", LayoutMetrics.TitleFontSize, FontStyles.Bold,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(6f, LayoutMetrics.TitleOffsetY));
            view._subtitle = CreateText(rect, "Subtitle", LayoutMetrics.SubtitleFontSize, FontStyles.Normal,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(6f, LayoutMetrics.SubtitleOffsetY));
            // Top-pinned (0,1)-(1,1), matching Title/Subtitle above - was (0,0)-(1,1) (full
            // vertical stretch), which combined with sizeDelta.y=16 computed to ~100px tall inside
            // this 84px-tall node and overlapped the subtitle band instead of sitting as a clean
            // line near the bottom.
            // Dropped entirely in compact mode - there is no room for it, and the title plus
            // trader is what identifies a quest.
            if (LayoutMetrics.ShowObjectivePreview)
            {
                view._objectivePreview = CreateText(rect, "Objective", LayoutMetrics.ObjectiveFontSize,
                    FontStyles.Italic, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(6f, LayoutMetrics.ObjectiveOffsetY));
                view._objectivePreview.color = new Color(1f, 1f, 1f, 0.75f);
            }

            // Bottom band. The title/subtitle/objective rows occupy y -18/-36/-52, so this sits
            // below them rather than overlapping the title - and CreateText's rects are
            // horizontally stretched, so it has to live in a band of its own rather than a corner.
            var glyph = CreateText(rect, "StatusGlyph", LayoutMetrics.SubtitleFontSize + 2, FontStyles.Bold,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(6f, LayoutMetrics.StatusGlyphOffsetY));
            glyph.alignment = TextAlignmentOptions.TopLeft;
            view._statusGlyph = glyph;

            var badgeGo = new GameObject("KappaBadge", typeof(RectTransform), typeof(Image));
            var badgeRect = (RectTransform)badgeGo.transform;
            badgeRect.SetParent(rect, worldPositionStays: false);
            badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(1f, 1f);
            badgeRect.pivot = new Vector2(1f, 1f);
            badgeRect.anchoredPosition = new Vector2(-4f, -4f);
            badgeRect.sizeDelta = new Vector2(LayoutMetrics.KappaBadgeSize, LayoutMetrics.KappaBadgeSize);
            badgeGo.GetComponent<Image>().color = new Color(0.85f, 0.65f, 0.1f);
            var badgeText = CreateText(badgeRect, "K", 11, FontStyles.Bold, Vector2.zero, Vector2.one, Vector2.zero);
            badgeText.alignment = TextAlignmentOptions.Center;
            badgeText.text = "K";
            view._kappaBadge = badgeGo;

            return view;
        }

        private static TMP_Text CreateText(RectTransform parent, string name, int size, FontStyles style,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(-12f, 16f);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.overflowMode = TextOverflowModes.Ellipsis;

            // Outlined rather than plain: node labels sit over status-coloured borders and whatever
            // the game draws behind the panel, so they need separating from the background.
            GameStyle.ApplyOutlined(text);
            return text;
        }

        public void Bind(QuestNode node, Action<QuestNode> onClicked,
            Action<QuestNode> onHoverEnter = null, Action<QuestNode> onHoverExit = null)
        {
            Node = node;
            OnClicked = onClicked;
            OnHoverEnter = onHoverEnter;
            OnHoverExit = onHoverExit;

            // Views are pooled, so a recycled one arrives carrying whatever dim the highlight left
            // on it. Reset here for the same reason RefreshStatus resets the border colour.
            SetDimmed(false);

            _title.text = node.Name;
            _kappaBadge.SetActive(node.IsKappaRequired);
            RefreshStatus();
            RefreshDetails();
        }

        /// <summary>Re-reads only the status color - cheap enough to call on every node whenever
        /// QuestController.OnConditionalStatusChanged fires.</summary>
        public void RefreshStatus()
        {
            var color = ColorFor(Node.Status);
            _border.color = color;

            if (_statusGlyph != null)
            {
                _statusGlyph.text = GlyphFor(Node.Status);
                _statusGlyph.color = color;
            }
        }

        private void RefreshDetails()
        {
            var level = Node.Level > 0 ? $"Lvl {Node.Level}" : null;
            var map = string.IsNullOrEmpty(Node.LocationId) || Node.LocationId == "any"
                ? null
                : Node.LocationId;

            var parts = new[] { Node.TraderName, level, map }.Where(p => !string.IsNullOrEmpty(p));
            _subtitle.text = string.Join("  •  ", parts);

            if (_objectivePreview != null)
            {
                var firstObjective = Node.NecessaryObjectives.FirstOrDefault();
                _objectivePreview.text = firstObjective != null ? firstObjective.Text : "";
            }
        }

        /// <summary>Dim state for the chain highlight. Alpha only - the node keeps its layout,
        /// its position and its ability to be clicked.</summary>
        public void SetDimmed(bool dimmed)
        {
            if (_canvasGroup != null) _canvasGroup.alpha = dimmed ? DimmedAlpha : 1f;
        }

        public void OnPointerClick(PointerEventData eventData) => OnClicked?.Invoke(Node);

        public void OnPointerEnter(PointerEventData eventData) => OnHoverEnter?.Invoke(Node);

        public void OnPointerExit(PointerEventData eventData) => OnHoverExit?.Invoke(Node);
    }
}
