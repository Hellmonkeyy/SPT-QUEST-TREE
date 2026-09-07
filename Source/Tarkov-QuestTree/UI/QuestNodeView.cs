using System;
using System.Linq;
using EFT.UI;
using QuestTree.QuestGraph;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Compact visual for one QuestNode: a dark box with a status-coloured bar down its left edge,
    /// title, a trader/level/map subtitle, a one-line objective preview, and a Kappa badge. Built
    /// entirely from runtime UI primitives - this project ships no AssetBundle, so there is no
    /// authored prefab to instantiate instead.
    ///
    /// Status is carried by the BAR and the glyph, not by filling the whole box. A box filled with
    /// the status colour made every locked quest a grey slab on a near-black screen, made
    /// Available and Locked two greys apart, and forced the text colour to be chosen per status
    /// against whatever fill it landed on. With a dark box the text is always the game's own body
    /// colour, and the bar is the one thing that changes - which is what the eye should be
    /// reading for anyway.
    /// </summary>
    internal sealed class QuestNodeView : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>How far a node fades when it is not part of the hovered quest's chain. Low
        /// enough to recede, high enough that the shape of the rest of the tree is still readable.</summary>
        private const float DimmedAlpha = 0.25f;

        /// <summary>Node size follows the layout-density setting - see UI/LayoutMetrics.cs. Kept
        /// under these names so every existing call site (framing, visibility, edge maths) reads
        /// unchanged.</summary>
        public static float Width => LayoutMetrics.NodeWidth;
        public static float Height => LayoutMetrics.NodeHeight;

        // Grey / amber / green / dark green. Available is amber rather than the near-white it used
        // to be: white and grey are the two colours that do not survive a zoomed-out tree, and the
        // game's own Tasks screen says "you can take this" in amber. The one quest you are actually
        // doing stays the brightest thing on the screen, in a vivid green.
        private static readonly Color LockedColor = new(0.42f, 0.42f, 0.4f, 1f);
        private static readonly Color AvailableColor = new(0.85f, 0.66f, 0.28f, 1f);
        private static readonly Color ActiveColor = new(0.36f, 0.91f, 0.17f, 1f);

        // Completed is also green - "done" is green by convention and that is worth keeping - so the
        // two are separated by BRIGHTNESS rather than hue: this is 43% darker than Active. Hue alone
        // does not survive the 11px glyphs in the legend and the lists, and it survives the 25%
        // chain-dimming even less. Completed quests are the backdrop of a mature tree; they should
        // recede, not compete.
        private static readonly Color CompletedColor = new(0.24f, 0.52f, 0.3f, 1f);

        /// <summary>The box itself. Slightly lighter than the panel so the box has an edge, and
        /// opaque so an edge passing behind it stops at it.</summary>
        private static readonly Color FillColor = new(0.11f, 0.11f, 0.1f, 1f);
        private static readonly Color LockedFillColor = new(0.085f, 0.085f, 0.08f, 1f);

        /// <summary>ColorFor as the hex a rich-text colour tag takes. ToHtmlStringRGB drops the
        /// alpha, which is fine here: the lists separate states by hue and brightness, never by
        /// alpha.</summary>
        public static string HexFor(ENodeStatus status) => ColorUtility.ToHtmlStringRGB(ColorFor(status));

        /// <summary>Shared with the legend, the lists and the map so they can never drift apart.</summary>
        public static Color ColorFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => CompletedColor,
            ENodeStatus.Active => ActiveColor,
            ENodeStatus.Available => AvailableColor,
            _ => LockedColor
        };

        /// <summary>A glyph per status, so state is not carried by colour alone.</summary>
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

        private Image _fill;
        private Outline _outline;
        private Image _statusBar;
        private TMP_Text _statusGlyph;
        private TMP_Text _title;
        private TMP_Text _subtitle;
        private TMP_Text _objectivePreview;
        private GameObject _kappaBadge;
        private HoverTooltipArea _tooltip;

        /// <summary>Dims the whole node in one operation when another quest's chain is highlighted.
        /// A CanvasGroup is one component and one float, versus recolouring every child graphic.</summary>
        private CanvasGroup _canvasGroup;

        /// <summary>0 = everything; 1 = title only, larger. See <see cref="SetDetailLevel"/>.</summary>
        private int _detailLevel;

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

            var fill = go.GetComponent<Image>();
            fill.raycastTarget = true;
            fill.color = FillColor;
            GameStyle.ApplyPanel(fill);

            var view = go.AddComponent<QuestNodeView>();
            view._canvasGroup = go.AddComponent<CanvasGroup>();
            // Dimming must never make a node unclickable - hovering a neighbour dims this one, and
            // it still has to accept the click that would select it.
            view._canvasGroup.blocksRaycasts = true;
            view._canvasGroup.interactable = true;
            view._fill = fill;

            // A one-pixel edge in the status colour, so a box reads as a box against its neighbours
            // and the status is visible even where the bar is hidden behind an edge line.
            view._outline = go.AddComponent<Outline>();
            view._outline.effectDistance = new Vector2(1f, -1f);
            view._outline.useGraphicAlpha = false;

            // The bar: full height, pinned to the left edge.
            var barGo = new GameObject("StatusBar", typeof(RectTransform), typeof(Image));
            var barRect = (RectTransform)barGo.transform;
            barRect.SetParent(rect, worldPositionStays: false);
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(0f, 1f);
            barRect.pivot = new Vector2(0f, 0.5f);
            barRect.anchoredPosition = Vector2.zero;
            barRect.sizeDelta = new Vector2(LayoutMetrics.StatusBarWidth, 0f);
            view._statusBar = barGo.GetComponent<Image>();
            view._statusBar.raycastTarget = false;

            var textX = LayoutMetrics.TextInsetX;

            view._title = CreateText(rect, "Title", LayoutMetrics.TitleFontSize, FontStyles.Bold,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(textX, LayoutMetrics.TitleOffsetY));

            view._subtitle = CreateText(rect, "Subtitle", LayoutMetrics.SubtitleFontSize, FontStyles.Normal,
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(textX, LayoutMetrics.SubtitleOffsetY));

            // Dropped entirely in compact mode - there is no room for it, and the title plus
            // trader is what identifies a quest.
            if (LayoutMetrics.ShowObjectivePreview)
            {
                view._objectivePreview = CreateText(rect, "Objective", LayoutMetrics.ObjectiveFontSize,
                    FontStyles.Italic, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(textX, LayoutMetrics.ObjectiveOffsetY));
            }

            // Top-right corner, next to the Kappa badge, in the status colour: the one mark that
            // says the status at any zoom, since the bar is a sliver and the title is words.
            var glyph = CreateText(rect, "StatusGlyph", LayoutMetrics.GlyphFontSize, FontStyles.Bold,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-6f, -4f));
            var glyphRect = (RectTransform)glyph.transform;
            glyphRect.pivot = new Vector2(1f, 1f);
            glyphRect.sizeDelta = new Vector2(16f, 16f);
            glyph.alignment = TextAlignmentOptions.TopRight;
            view._statusGlyph = glyph;

            var badgeGo = new GameObject("KappaBadge", typeof(RectTransform), typeof(Image));
            var badgeRect = (RectTransform)badgeGo.transform;
            badgeRect.SetParent(rect, worldPositionStays: false);
            badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(1f, 1f);
            badgeRect.pivot = new Vector2(1f, 1f);
            badgeRect.anchoredPosition = new Vector2(-24f, -4f);
            badgeRect.sizeDelta = new Vector2(LayoutMetrics.KappaBadgeSize, LayoutMetrics.KappaBadgeSize);
            badgeGo.GetComponent<Image>().color = new Color(0.85f, 0.65f, 0.1f);
            badgeGo.GetComponent<Image>().raycastTarget = false;
            var badgeText = CreateText(badgeRect, "K", 11, FontStyles.Bold, Vector2.zero, Vector2.one, Vector2.zero);
            badgeText.alignment = TextAlignmentOptions.Center;
            badgeText.text = "K";
            badgeText.color = new Color(0.06f, 0.07f, 0.05f);
            view._kappaBadge = badgeGo;

            // The game's own tooltip, on hover. Text is set per Bind.
            view._tooltip = GameStyle.AddTooltip(go, "");

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
            rect.sizeDelta = new Vector2(-(anchoredPosition.x + 6f), 16f);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = size;
            text.fontStyle = style;
            text.color = Color.white;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.raycastTarget = false;
            GameStyle.Apply(text);
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
            // on it. Reset here for the same reason RefreshStatus resets the colours.
            SetDimmed(false);

            _title.text = node.Name;
            _kappaBadge.SetActive(node.IsKappaRequired);
            RefreshStatus();
            RefreshDetails();
            ApplyDetailLevel();
        }

        /// <summary>Re-reads only the status colours - cheap enough to call on every node whenever
        /// QuestController.OnConditionalStatusChanged fires.</summary>
        public void RefreshStatus()
        {
            var status = Node.Status;
            var color = ColorFor(status);
            var locked = status == ENodeStatus.Locked;

            if (_statusBar != null) _statusBar.color = color;
            if (_fill != null) _fill.color = locked ? LockedFillColor : FillColor;

            if (_outline != null)
            {
                // Active gets the full edge; everything else a quieter one, so the quest you are on
                // is boxed in its own colour and the rest merely tinted.
                _outline.effectColor = Fade(color, status == ENodeStatus.Active ? 0.9f : 0.45f);
            }

            // Body text is the game's own colour on a dark box, whatever the status; only Locked
            // steps down, so the part of the tree you cannot touch yet recedes as a whole.
            var ink = locked ? GameStyle.DimTextColor : GameStyle.TextColor;
            if (_title != null) _title.color = ink;
            if (_subtitle != null) _subtitle.color = Fade(GameStyle.TextColor, locked ? 0.4f : 0.6f);
            if (_objectivePreview != null) _objectivePreview.color = Fade(GameStyle.TextColor, locked ? 0.35f : 0.55f);

            if (_statusGlyph != null)
            {
                _statusGlyph.text = GlyphFor(status);
                _statusGlyph.color = color;
            }
        }

        private static Color Fade(Color color, float alpha) =>
            new(color.r, color.g, color.b, alpha);

        private void RefreshDetails()
        {
            var level = Node.Level > 0 ? $"Lv {Node.Level}" : null;
            var map = string.IsNullOrEmpty(Node.LocationId) || Node.LocationId == "any"
                ? null
                : Node.LocationId;

            var parts = new[] { Node.TraderName, level, map }.Where(p => !string.IsNullOrEmpty(p)).ToList();
            _subtitle.text = string.Join("  ·  ", parts);

            var firstObjective = Node.NecessaryObjectives.FirstOrDefault();
            var objectiveText = firstObjective != null ? firstObjective.Text : "";

            if (_objectivePreview != null) _objectivePreview.text = objectiveText;

            if (_tooltip != null)
            {
                // The same facts the box shows, at a size that can be read - so a zoomed-out tree
                // still answers "what is this one" on hover without opening the detail.
                var line = string.Join("  ·  ", parts);
                var body = string.IsNullOrEmpty(objectiveText) ? "" : "\n" + objectiveText;
                _tooltip.SetMessageText($"<b>{Node.Name}</b>\n{NameFor(Node.Status)}  ·  {line}{body}", rawText: true);
            }
        }

        /// <summary>
        /// Level of detail for the current zoom. Zoomed out, the subtitle and objective become
        /// unreadable smears that only add noise; hiding them and growing the title keeps the one
        /// thing worth reading readable. Cheap: toggles and one font size, on visible views only.
        /// </summary>
        public void SetDetailLevel(int level)
        {
            if (_detailLevel == level) return;
            _detailLevel = level;
            ApplyDetailLevel();
        }

        private void ApplyDetailLevel()
        {
            var zoomedOut = _detailLevel > 0;

            if (_subtitle != null) _subtitle.gameObject.SetActive(!zoomedOut);
            if (_objectivePreview != null) _objectivePreview.gameObject.SetActive(!zoomedOut);

            if (_title == null) return;

            var titleRect = (RectTransform)_title.transform;

            if (zoomedOut)
            {
                _title.fontSize = LayoutMetrics.ZoomedOutTitleFontSize;
                titleRect.anchorMin = new Vector2(0f, 0f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.anchoredPosition = new Vector2(LayoutMetrics.TextInsetX, 0f);
                titleRect.sizeDelta = new Vector2(-(LayoutMetrics.TextInsetX + 24f), 0f);
                _title.alignment = TextAlignmentOptions.Left;
                _title.enableWordWrapping = true;
            }
            else
            {
                _title.fontSize = LayoutMetrics.TitleFontSize;
                titleRect.anchorMin = new Vector2(0f, 1f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.anchoredPosition = new Vector2(LayoutMetrics.TextInsetX, LayoutMetrics.TitleOffsetY);
                titleRect.sizeDelta = new Vector2(-(LayoutMetrics.TextInsetX + 6f), 16f);
                _title.alignment = TextAlignmentOptions.TopLeft;
                _title.enableWordWrapping = false;
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
