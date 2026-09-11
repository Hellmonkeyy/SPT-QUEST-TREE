using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>Shared with the legend, the lists and the map so they can never drift apart.
        /// The user's colours from Settings when there are any; the palette above otherwise.</summary>
        public static Color ColorFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => FromSettings(ModSettings.ColorCompleted, CompletedColor),
            ENodeStatus.Active => FromSettings(ModSettings.ColorActive, ActiveColor),
            ENodeStatus.Available => FromSettings(ModSettings.ColorAvailable, AvailableColor),
            _ => FromSettings(ModSettings.ColorLocked, LockedColor)
        };

        private static Color FromSettings(BepInEx.Configuration.ConfigEntry<string> entry, Color fallback) =>
            ModSettings.Ready ? ModSettings.ParseColor(entry, fallback) : fallback;

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

        /// <summary>Whether this is the box the detail panel is about. Cleared on Bind, because a
        /// pooled view arrives wearing whatever it last showed.</summary>
        private bool _selected;

        /// <summary>One screen pixel in content units, set by the graph's sweep from the zoom.
        /// The outline is drawn in content space, so a fixed 1-unit edge at a third zoom was a
        /// third of a pixel - drawn on some sides and not others. Selected boxes get twice this.</summary>
        private float _outlineUnit = 1f;
        private Image _statusBar;
        private TMP_Text _statusGlyph;
        private TMP_Text _title;
        private TMP_Text _abbreviation;
        private TMP_Text _subtitle;
        private TMP_Text _objectivePreview;
        private GameObject _kappaBadge;
        private GameObject _collectorBadge;
        private HoverTooltipArea _tooltip;

        /// <summary>Why this node survived the current search, or null when it matched on
        /// its own name and needs no explaining. Set by the renderer, which already knows
        /// the needle - computed only for nodes that already matched, which is a handful,
        /// so it can afford to walk the fields properly.</summary>
        public string SearchReason { get; set; }

        /// <summary>Dims the whole node in one operation when another quest's chain is highlighted.
        /// A CanvasGroup is one component and one float, versus recolouring every child graphic.</summary>
        private CanvasGroup _canvasGroup;

        /// <summary>0 = everything; 1 = title only, larger; 2 = bar and glyph only. See
        /// <see cref="SetDetailLevel"/>.</summary>
        private int _detailLevel;

        /// <summary>Whether this quest's title needs two lines, in which case the box is taller
        /// and the rows below it move down. Decided per Bind by measuring the title.</summary>
        private bool _tall;

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

            // The zoomed-right-out label: a code for the title, sized to fill the box. Hidden until
            // the detail level asks for it.
            var abbreviation = CreateText(rect, "Abbreviation", LayoutMetrics.AbbreviationFontSize, FontStyles.Bold,
                new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(LayoutMetrics.TextInsetX, 0f));
            var abbreviationRect = (RectTransform)abbreviation.transform;
            abbreviationRect.sizeDelta = new Vector2(-(LayoutMetrics.TextInsetX + 4f), 0f);
            abbreviation.alignment = TextAlignmentOptions.Center;
            abbreviation.overflowMode = TextOverflowModes.Overflow;
            abbreviation.enableWordWrapping = false;
            abbreviation.gameObject.SetActive(false);
            view._abbreviation = abbreviation;

            // Top-right corner, next to the Kappa badge, in the status colour. Only for the three
            // statuses worth a mark: a locked box - the grey majority - showed a small cross here
            // that read as a close button at any zoom, when the grey bar and dim text already
            // say locked. Hidden with the title once the box is a code, where it was a smudge.
            var glyph = CreateText(rect, "StatusGlyph", LayoutMetrics.GlyphFontSize, FontStyles.Bold,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-6f, -3f));
            var glyphRect = (RectTransform)glyph.transform;
            glyphRect.pivot = new Vector2(1f, 1f);
            glyphRect.sizeDelta = new Vector2(20f, 20f);
            glyph.alignment = TextAlignmentOptions.TopRight;
            view._statusGlyph = glyph;


            // The game's own tooltip, on hover. Text is set per Bind.
            view._tooltip = GameStyle.AddTooltip(go, "");

            return view;
        }

        /// <summary>A corner mark: a small filled square with one dark letter in it. Where it
        /// sits is decided per bind by RefreshBadges, since that depends on how many show.</summary>
        private static GameObject CreateBadge(RectTransform parent, string letter, Color color)
        {
            var badgeGo = new GameObject($"{letter}Badge", typeof(RectTransform), typeof(Image));
            var badgeRect = (RectTransform)badgeGo.transform;
            badgeRect.SetParent(parent, worldPositionStays: false);
            badgeRect.anchorMin = badgeRect.anchorMax = new Vector2(1f, 1f);
            badgeRect.pivot = new Vector2(1f, 1f);
            badgeRect.sizeDelta = new Vector2(LayoutMetrics.KappaBadgeSize, LayoutMetrics.KappaBadgeSize);

            var image = badgeGo.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            var badgeText = CreateText(badgeRect, letter, 11, FontStyles.Bold, Vector2.zero, Vector2.one, Vector2.zero);
            badgeText.alignment = TextAlignmentOptions.Center;
            badgeText.text = letter;
            badgeText.color = new Color(0.06f, 0.07f, 0.05f);

            return badgeGo;
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
            _selected = false;

            RefreshBadges();

            // Size to the title, not the other way round: a name like "The Survivalist Path -
            // Unprotected but Dangerous" was an ellipsis at one line, and three lines over the
            // subtitle when allowed to wrap. Two lines and a taller box is the honest middle - and
            // the second line is the PART, because "The Enemy's Mind - P..." six times in a row
            // says nothing. Widths are estimated from character counts rather than measured:
            // TMP's measurement answered wrong for pooled views, and an estimate is deterministic.
            _title.fontSize = LayoutMetrics.TitleFontSize;
            // Glyph and badges live top-right; RefreshBadges above has just counted them.
            var titleWidth = Width - LayoutMetrics.TextInsetX - 44f - BadgeInset;
            var (head, tail) = TitleParts(node.Name);
            _tall = LayoutMetrics.AllowTallNodes && (tail != null || EstimateWidth(head, LayoutMetrics.TitleFontSize) > titleWidth);

            _title.text = GameStyle.Safe(_tall
                ? FitToWidth(head, titleWidth) + (tail != null ? "\n" + FitToWidth(tail, titleWidth) : "")
                : node.Name);

            ((RectTransform)transform).sizeDelta = new Vector2(Width, Height + (_tall ? LayoutMetrics.TallNodeExtraHeight : 0f));

            if (_abbreviation != null)
            {
                // With codes off, the zoomed-right-out box shows its title instead - the user's
                // call; a title at that size is a smear, but some would rather have the smear.
                var codes = !ModSettings.Ready || ModSettings.AbbreviateWhenZoomedOut.Value;
                _abbreviation.text = codes ? Abbreviate(node.Name) : GameStyle.Safe(node.Name);
                _abbreviation.fontSize = codes ? LayoutMetrics.AbbreviationFontSize : LayoutMetrics.ZoomedOutTitleFontSize;
                _abbreviation.enableWordWrapping = !codes;
                _abbreviation.overflowMode = codes ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            }

            RefreshStatus();
            RefreshDetails();
            ApplyDetailLevel();
        }

        // ------------------------------------------------------------------ title fitting

        /// <summary>Average advance of this font's bold face, as a fraction of the font size.
        /// Bender bold sits around 0.55em; erring slightly wide only costs an early ellipsis.</summary>
        private const float AverageGlyphAdvance = 0.56f;

        private static float EstimateWidth(string text, int fontSize) =>
            (text?.Length ?? 0) * fontSize * AverageGlyphAdvance;

        /// <summary>Cuts a line to the estimated width with an ellipsis - by hand, because TMP's
        /// ellipsis only ever applies to the last line, and a two-line title needs it on the first.</summary>
        private string FitToWidth(string text, float width)
        {
            var maxChars = Mathf.FloorToInt(width / (LayoutMetrics.TitleFontSize * AverageGlyphAdvance));
            if (text.Length <= maxChars || maxChars < 4) return text;
            return text.Substring(0, maxChars - 1).TrimEnd() + "\u2026";
        }

        /// <summary>The separators quest names use between a series and its episode: "Gunsmith -
        /// Part 3", "The Survivalist Path - Thrifty", "Textile - Part 1 - USEC".</summary>
        private static readonly string[] Separators = { " \u2013 ", " - ", ": " };

        /// <summary>Splits "Series - Episode" at the FIRST separator: head is the series, tail
        /// everything after. Tail is null for a name with no separator.</summary>
        private static (string Head, string Tail) TitleParts(string name)
        {
            if (string.IsNullOrEmpty(name)) return ("", null);

            foreach (var separator in Separators)
            {
                var at = name.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0 && at + separator.Length < name.Length)
                    return (name.Substring(0, at).Trim(), name.Substring(at + separator.Length).Trim());
            }

            return (name.Trim(), null);
        }

        private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "in", "and", "for", "to", "on", "with", "at", "from", "by", "or", "is", "it"
        };

        private static readonly Regex PartNumber = new(@"^(?:part|pt\.?|chapter|episode)\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A short code for a title, for the zoomed-right-out box: initials of the series' words
        /// that carry meaning, then the part - "The Enemy's Mind - Part 4" is EM-4, "Compensation
        /// for Damage - Barkeep" is CD-B, "Ambulance" is AMB. Not unique, not meant to be: it is
        /// enough to tell one row of a chain from the next and to find a quest you already know.
        /// </summary>
        public static string Abbreviate(string name)
        {
            var (head, tail) = TitleParts(name);
            var code = Initials(head, 3);

            if (code.Length < 2)
            {
                // One meaningful word ("The Punisher", "Ambulance"): the first letters of THAT
                // word, not of the article in front of it - "THE-3" nine times over told nobody
                // anything.
                var word = head.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim('(', ')', '"', ',', '.').Replace("'", ""))
                    .FirstOrDefault(w => w.Length > 0 && !Stopwords.Contains(w)) ?? head.Replace("'", "");
                code = word.Length <= 3 ? word.ToUpperInvariant() : word.Substring(0, 3).ToUpperInvariant();
            }

            if (tail == null) return code;

            var part = PartNumber.Match(tail);
            var suffix = part.Success ? part.Groups[1].Value : Initials(tail, 2);
            return suffix.Length > 0 ? $"{code}-{suffix}" : code;
        }

        private static string Initials(string text, int max)
        {
            var letters = new System.Text.StringBuilder();

            foreach (var raw in text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Trim('(', ')', '"', '\'', ',', '.');
                if (word.Length == 0 || Stopwords.Contains(word)) continue;
                if (!char.IsLetterOrDigit(word[0])) continue;

                letters.Append(char.ToUpperInvariant(word[0]));
                if (letters.Length >= max) break;
            }

            return letters.ToString();
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
                if (_selected)
                {
                    // The box the detail panel is about: the accent at full strength on a heavier
                    // edge, so it reads as chosen rather than as a fifth status colour.
                    _outline.effectColor = GameStyle.AccentColor;
                    _outline.effectDistance = new Vector2(2f * _outlineUnit, -2f * _outlineUnit);
                }
                else
                {
                    // Active gets the full edge; everything else a quieter one, so the quest you
                    // are on is boxed in its own colour and the rest merely tinted.
                    _outline.effectColor = Fade(color, status == ENodeStatus.Active ? 0.9f : 0.45f);
                    _outline.effectDistance = new Vector2(_outlineUnit, -_outlineUnit);
                }
            }

            // Body text is the game's own colour on a dark box, whatever the status; only Locked
            // steps down, so the part of the tree you cannot touch yet recedes as a whole.
            var ink = locked ? GameStyle.DimTextColor : GameStyle.TextColor;
            if (_title != null) _title.color = ink;
            if (_abbreviation != null) _abbreviation.color = ink;
            if (_subtitle != null) _subtitle.color = Fade(GameStyle.TextColor, locked ? 0.4f : 0.6f);
            if (_objectivePreview != null) _objectivePreview.color = Fade(GameStyle.TextColor, locked ? 0.35f : 0.55f);

            if (_statusGlyph != null)
            {
                _statusGlyph.text = locked ? "" : GlyphFor(status);
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
            var objectiveText = firstObjective != null ? GameStyle.Safe(firstObjective.Text) : "";

            if (_objectivePreview != null) _objectivePreview.text = objectiveText;

            if (_tooltip != null)
            {
                // The same facts the box shows, at a size that can be read - so a zoomed-out tree
                // still answers "what is this one" on hover without opening the detail.
                var line = string.Join("  ·  ", parts);
                if (Node.IsKappaRequired) line += "  ·  Kappa";
                if (Node.IsCollectorPrerequisite) line += "  ·  unlocks Collector";
                var body = string.IsNullOrEmpty(objectiveText) ? "" : "\n" + objectiveText;
                // Why it matched the search, when that is not its own name. A box titled
                // "Debut" matching "PL-15" explains nothing on its own, and there is no room in
                // the box - so it explains itself on hover, without leaving the tree.
                var why = string.IsNullOrEmpty(SearchReason)
                    ? ""
                    : $"\n<color=#{GameStyle.WarningHex}>matched on {GameStyle.Safe(SearchReason)}</color>";

                _tooltip.SetMessageText(
                    $"<b>{GameStyle.Safe(Node.Name)}</b>\n{NameFor(Node.Status)}  ·  {line}{body}{why}",
                    rawText: true);
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
            var barOnly = _detailLevel > 1;

            if (_subtitle != null) _subtitle.gameObject.SetActive(!zoomedOut);
            if (_objectivePreview != null) _objectivePreview.gameObject.SetActive(!zoomedOut);
            RefreshBadges();
            if (_statusGlyph != null) _statusGlyph.gameObject.SetActive(!barOnly);

            if (_title == null) return;

            // Past the point where even a large title is a smear, the box shows its code instead
            // (see Abbreviate) - a wrapped 5px title only added noise, and an empty box said
            // nothing at all.
            _title.gameObject.SetActive(!barOnly);
            if (_abbreviation != null) _abbreviation.gameObject.SetActive(barOnly);
            if (barOnly) return;

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
                _title.maxVisibleLines = 3;
            }
            else
            {
                var lines = _tall ? 2 : 1;
                var extra = _tall ? LayoutMetrics.TallNodeExtraHeight : 0f;

                _title.fontSize = LayoutMetrics.TitleFontSize;
                titleRect.anchorMin = new Vector2(0f, 1f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.anchoredPosition = new Vector2(LayoutMetrics.TextInsetX, LayoutMetrics.TitleOffsetY);
                // Badge-aware, like the fitting width in Bind: a single-line title is handed to TMP
                // whole and ellipsised at this rect's edge, so a rect reaching under the badges put
                // the tail of the name behind them.
                titleRect.sizeDelta = new Vector2(-(LayoutMetrics.TextInsetX + 30f + BadgeInset), 16f * lines);
                _title.alignment = TextAlignmentOptions.TopLeft;
                _title.enableWordWrapping = false; // the break is explicit, and each line is pre-fitted

                // The hard stop. Turning wrapping off was not enough on its own: the title still
                // broke onto the subtitle's line. As many lines as the box was sized for, ellipsis
                // for the rest.
                _title.maxVisibleLines = lines;

                // The rows beneath move down by the extra line, so a two-line title never sits on
                // its own subtitle.
                if (_subtitle != null)
                    ((RectTransform)_subtitle.transform).anchoredPosition =
                        new Vector2(LayoutMetrics.TextInsetX, LayoutMetrics.SubtitleOffsetY - extra);
                if (_objectivePreview != null)
                    ((RectTransform)_objectivePreview.transform).anchoredPosition =
                        new Vector2(LayoutMetrics.TextInsetX, LayoutMetrics.ObjectiveOffsetY - extra);
            }
        }

        /// <summary>Dim state for the chain highlight. Alpha only - the node keeps its layout,
        /// its position and its ability to be clicked.</summary>
        /// <summary>Sets how thick one screen pixel is in content units at the current zoom - the
        /// graph's sweep calls this on every built view whenever the zoom moves. Goes through
        /// RefreshStatus so the outline keeps its one writer.</summary>
        public void SetOutlineUnit(float unit)
        {
            if (Mathf.Approximately(_outlineUnit, unit)) return;
            _outlineUnit = unit;
            RefreshStatus();
        }

        /// <summary>Marks or unmarks this box as the one the detail panel is showing. Goes through
        /// RefreshStatus so the outline has exactly one writer and a status change cannot paint
        /// over the selection.</summary>
        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            RefreshStatus();
        }

        /// <summary>Space between two badges when the box wears both.</summary>
        private const float BadgeGap = 2f;

        /// <summary>How many marks this box is wearing, counted by RefreshBadges and read by the
        /// title's width in two places - the fit in Bind and the rect in ApplyDetailLevel. Counted
        /// from the data and the settings only, not from the zoom: the title is hidden at the zoom
        /// where the badges are, so a width that changed with it would only churn.</summary>
        private int _badgeSlots;

        /// <summary>Extra width the title gives up for a SECOND badge; one badge fits in the inset
        /// the box has always reserved.</summary>
        private float BadgeInset => Mathf.Max(0, _badgeSlots - 1) * (LayoutMetrics.KappaBadgeSize + BadgeGap);

        /// <summary>
        /// Which marks this box wears, and where. Kappa gold for a quest on the canonical list,
        /// Collector blue for one Collector cannot be accepted without on this install; either can
        /// be switched off in Settings, and both go with the title once the box is only a code,
        /// where a 14px square is a smudge.
        ///
        /// The first visible mark takes the inner slot, so a box wearing only C puts it exactly
        /// where a box wearing only K puts that - the corner reads the same either way.
        /// </summary>
        private void RefreshBadges()
        {
            var kappa = Node != null && Node.IsKappaRequired && ModSettings.ShowKappaBadge;
            var collector = Node != null && Node.IsCollectorPrerequisite && ModSettings.ShowCollectorBadge;
            _badgeSlots = (kappa ? 1 : 0) + (collector ? 1 : 0);

            // Hidden with the title once the box is only a code, where a 14px square is a smudge.
            var barOnly = _detailLevel > 1;
            var slot = 0;

            if (kappa) PlaceBadge(EnsureBadge(ref _kappaBadge, "K", GameStyle.KappaGold), slot++, !barOnly);
            else if (_kappaBadge != null) _kappaBadge.SetActive(false);

            if (collector) PlaceBadge(EnsureBadge(ref _collectorBadge, "C", GameStyle.CollectorBlue), slot, !barOnly);
            else if (_collectorBadge != null) _collectorBadge.SetActive(false);
        }

        /// <summary>Builds a mark the first time this box needs one. Most quests wear neither, and
        /// a view is pooled and rebound many times - two GameObjects apiece, eagerly, on up to two
        /// thousand live views is a cost paid mostly for boxes that never show a badge.</summary>
        private GameObject EnsureBadge(ref GameObject badge, string letter, Color color)
        {
            badge ??= CreateBadge((RectTransform)transform, letter, color);
            return badge;
        }

        private static void PlaceBadge(GameObject badge, int slot, bool visible)
        {
            badge.SetActive(visible);
            ((RectTransform)badge.transform).anchoredPosition =
                new Vector2(-24f - slot * (LayoutMetrics.KappaBadgeSize + BadgeGap), -4f);
        }

        public void SetDimmed(bool dimmed) => SetDimAlpha(dimmed ? DimmedAlpha : 1f);

        /// <summary>A specific alpha, for the distance-based dimming around a hovered quest.</summary>
        public void SetDimAlpha(float alpha)
        {
            if (_canvasGroup == null) return;

            // Writing the same alpha still dirties the group's whole subtree; the falloff pass
            // asks every built box every zoom frame, and most of them have not changed.
            var clamped = Mathf.Clamp01(alpha);
            if (Mathf.Approximately(_canvasGroup.alpha, clamped)) return;
            _canvasGroup.alpha = clamped;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            // The boxes are hand-built like the tabs and rows, which all play this themselves;
            // without it the one control the screen is made of was the only silent one.
            GameStyle.PlaySound(EUISoundType.ButtonClick);
            OnClicked?.Invoke(Node);
        }
        public void OnPointerEnter(PointerEventData eventData) => OnHoverEnter?.Invoke(Node);
        public void OnPointerExit(PointerEventData eventData) => OnHoverExit?.Invoke(Node);
    }
}
