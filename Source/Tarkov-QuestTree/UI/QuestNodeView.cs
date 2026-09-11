using System;
using System.Collections.Generic;
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

        /// <summary>How wide the trader slab is. Lives in LayoutMetrics because the text inset
        /// has to clear it.</summary>
        private static float TraderStripeWidth => LayoutMetrics.TraderStripeWidth;

        private Image _traderStripe;
        public static float Height => LayoutMetrics.NodeHeight;

        /// <summary>The size this bound node was given, for anything that has to line up with it.
        /// Boxes stopped being uniform in 1.9.0.</summary>
        public Vector2 Size => _size;

        private Vector2 _size = new Vector2(LayoutMetrics.NodeWidth, LayoutMetrics.NodeHeight);

        /// <summary>The graph the boxes are currently drawn from, so a blocked box can turn a
        /// prerequisite ID into a name.
        ///
        /// Static because it is the same object for every view in a render and threading it through
        /// Bind would put the identical reference on hundreds of calls per sweep. Set once per
        /// render by QuestGraphView; a stale value can only produce a missing name, never a wrong
        /// box, because the lookup either resolves or is skipped.</summary>
        private static QuestGraph.QuestGraphBuilder _graphContext;

        public static void SetGraphContext(QuestGraph.QuestGraphBuilder graph) => _graphContext = graph;

        // Grey / blue / amber / green / red.
        //
        // This is a ROTATION of the old palette, and the reason is that the old one put in-progress
        // and completed in the same hue. They were separated by brightness alone - the argument
        // being that completed quests are the backdrop of a mature tree and should recede - and in
        // practice that failed the one question the canvas has to answer. On a chain of forty green
        // boxes you cannot see which one you are on.
        //
        // So hue now carries status and nothing else: blue you may take, amber you are doing, green
        // you have done, red is a threshold away, grey is behind another quest. Completed still
        // recedes, but by being dimmed and struck through rather than by being a darker version of
        // the colour that means something else.
        private static readonly Color LockedColor = new(0.42f, 0.42f, 0.42f, 1f);
        private static readonly Color AvailableColor = new(0.31f, 0.64f, 0.89f, 1f);
        private static readonly Color ActiveColor = new(0.91f, 0.64f, 0.24f, 1f);

        /// <summary>Level, loyalty or standing - a wall you climb rather than one you unlock.
        /// Red because it is the only status that is about YOU rather than about the quest.</summary>
        private static readonly Color GatedColor = new(0.85f, 0.33f, 0.31f, 1f);

        // Completed keeps green - "done" is green by convention - and now it is the ONLY green, so
        // it no longer has to be a darker shade of in-progress to be told apart from it. Brightened
        // accordingly: the old value was dimmed to create that separation, and with hue doing the
        // work the dimming is free to move to where it belongs, which is the box itself (struck
        // title, reduced alpha) rather than the status colour shared with the legend and the lists.
        private static readonly Color CompletedColor = new(0.31f, 0.75f, 0.5f, 1f);

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
            ENodeStatus.Gated => FromSettings(ModSettings.ColorGated, GatedColor),
            _ => FromSettings(ModSettings.ColorLocked, LockedColor)
        };

        private static Color FromSettings(BepInEx.Configuration.ConfigEntry<string> entry, Color fallback) =>
            ModSettings.Ready ? ModSettings.ParseColor(entry, fallback) : fallback;

        /// <summary>A glyph per status, so state is not carried by colour alone.
        ///
        /// This is what the greyscale test rests on: desaturate every accent and the five states
        /// must still be tellable apart. Colour is the fast channel, the glyph is the one that
        /// still works for a colour-blind player and at the zoom where a 6px bar is a smudge.</summary>
        public static string GlyphFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => Glyphs.Completed,
            ENodeStatus.Active => Glyphs.Active,
            ENodeStatus.Available => Glyphs.Available,
            ENodeStatus.Gated => Glyphs.Gated,
            _ => Glyphs.Locked
        };

        /// <summary>The marks this tree draws, each resolved to something the font can render.
        ///
        /// Every one is CHOSEN rather than assumed. A character the font lacks does not fail loudly:
        /// TMP draws a box and keeps its layout, so it reads as a bug in the mod rather than a gap
        /// in a font - which is what the arrow on a blocked quest's reason line was doing.
        ///
        /// Resolved once and cached. The check rasterises the character into a dynamic atlas as a
        /// side effect, and these are read for every box and every row of every list. Lazily,
        /// because the font is harvested from the game and does not exist when this type is first
        /// touched.</summary>
        private static class Glyphs
        {
            private static bool _resolved;
            private static string _completed, _active, _available, _gated, _locked, _needs;

            public static string Completed { get { Resolve(); return _completed; } }
            public static string Active { get { Resolve(); return _active; } }
            public static string Available { get { Resolve(); return _available; } }
            public static string Gated { get { Resolve(); return _gated; } }
            public static string Locked { get { Resolve(); return _locked; } }

            /// <summary>The arrow on a blocked box's reason line. Allowed to come back empty -
            /// "Needs Carbines III" reads perfectly without it, and a box where an arrow should be
            /// is worse than no arrow.</summary>
            public static string Needs { get { Resolve(); return _needs; } }

            private static void Resolve()
            {
                if (_resolved) return;
                _resolved = true;

                // Preferred first, ending in a plain-ASCII last resort wherever the mark carries
                // meaning on its own.
                _completed = GameStyle.PickGlyph("✓", "+");
                _active = GameStyle.PickGlyph("▶", "▸", ">");
                _available = GameStyle.PickGlyph("◇", "○", "o");
                _gated = GameStyle.PickGlyph("▲", "▴", "^");
                _locked = GameStyle.LockGlyph(GameStyle.PickGlyph("✕", "x"));
                _needs = GameStyle.PickGlyph("↑", "▲", "");
            }
        }

        public static string NameFor(ENodeStatus status) => status switch
        {
            ENodeStatus.Completed => "Completed",
            ENodeStatus.Active => "In progress",
            ENodeStatus.Available => "Available",
            ENodeStatus.Gated => "Level gated",
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
        private TMP_Text _subtitle;
        /// <summary>The reward marks at the right-hand end of the meta row.</summary>
        private TMP_Text _rewards;

        /// <summary>The in-progress bar, and the part of it that is filled. Created on demand -
        /// most of a tree is not in progress, and two Images apiece on every pooled view is a cost
        /// paid mostly for boxes that never show one.</summary>
        private GameObject _progressTrack;
        private RectTransform _progressFill;
        private Image _progressFillImage;
        private GameObject _kappaBadge;
        private GameObject _collectorBadge;

        /// <summary>Why this node survived the current search, or null when it matched on
        /// its own name and needs no explaining. Set by the renderer, which already knows
        /// the needle - computed only for nodes that already matched, which is a handful,
        /// so it can afford to walk the fields properly.</summary>
        public string SearchReason { get; set; }

        /// <summary>Dims the whole node in one operation when another quest's chain is highlighted.
        /// A CanvasGroup is one component and one float, versus recolouring every child graphic.</summary>
        private CanvasGroup _canvasGroup;

        /// <summary>Whether this quest's title needs two lines, in which case the box is taller
        /// and the rows below it move down. Decided per Bind by measuring the title.</summary>
        private bool _tall;

        public QuestNode Node { get; private set; }
        public Action<QuestNode> OnClicked;
        public Action<QuestNode> OnHoverEnter;
        public Action<QuestNode> OnHoverExit;

        /// <summary>The box this quest wants, and whether its title needs two lines.
        ///
        /// The layout asks this once per graph build; Bind asks it again per node so the box and the
        /// text inside it are decided by the SAME arithmetic rather than by two measurements that
        /// have to agree.
        ///
        /// Sized from EstimateWidth, never from TMP. That is deliberate and it is the decision that
        /// makes a dynamic layout affordable at all: on a modded install the All tab is thousands of
        /// nodes, TMP measurement is slow at that count, and it is the single least reliable thing in
        /// this codebase - it has under-reported width twice and height once, each time shipped. The
        /// estimate is pure arithmetic over the visible character count, so it is instant, identical
        /// every run, and cannot regress.
        ///
        /// Prefers a WIDER box to a taller one, up to the clamp. "Weapon Mastery FN P90 5.7x28mm" on
        /// one readable line beats the same name cut to "Weapon Mastery FN P90 5.7x28mm Pa..." over
        /// two.</summary>
        public static Vector2 MeasureSize(QuestNode node, out bool tall)
        {
            tall = false;
            if (node == null) return new Vector2(LayoutMetrics.NodeWidth, LayoutMetrics.NodeHeight);

            var badges = (node.IsKappaRequired ? 1 : 0) + (node.IsCollectorPrerequisite ? 1 : 0);
            var badgeInset = Mathf.Max(0, badges - 1) * (LayoutMetrics.KappaBadgeSize + BadgeGap);
            var chrome = LayoutMetrics.TitleInsetX + 44f + badgeInset;

            var min = LayoutMetrics.NodeWidth;
            var max = LayoutMetrics.MaxNodeWidth;

            var name = DisplayName(node);

            // One line if the whole name fits inside the clamp.
            var oneLine = EstimateWidth(name, LayoutMetrics.TitleFontSize) + chrome;
            if (oneLine <= max)
                return new Vector2(Mathf.Max(min, oneLine), LayoutMetrics.NodeHeight);

            var (head, tail) = TitleParts(node.Name);

            // No part to break on, or tall boxes turned off: as wide as allowed, and the text
            // ellipsises. Honest rather than pretending a name fits.
            if (tail == null || !LayoutMetrics.AllowTallNodes)
                return new Vector2(max, LayoutMetrics.NodeHeight);

            tall = true;

            var wider = Mathf.Max(
                EstimateWidth(head, LayoutMetrics.TitleFontSize),
                EstimateWidth(tail, LayoutMetrics.TitleFontSize)) + chrome;

            return new Vector2(
                Mathf.Clamp(wider, min, max),
                LayoutMetrics.NodeHeight + LayoutMetrics.TallNodeExtraHeight);
        }

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
                new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero);

            view._subtitle = CreateText(rect, "Subtitle", LayoutMetrics.SubtitleFontSize, FontStyles.Normal,
                new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero);

            // The reward marks, right-aligned on the meta row. Its own element rather than part of
            // the subtitle string: one TMP line cannot be left-aligned at one end and right-aligned
            // at the other, and padding it with spaces would drift with every name length.
            var rewards = CreateText(rect, "Rewards", LayoutMetrics.RewardFontSize, FontStyles.Normal,
                new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero);
            var rewardsRect = (RectTransform)rewards.transform;
            rewardsRect.pivot = new Vector2(1f, 1f);
            rewardsRect.sizeDelta = new Vector2(70f, 16f);
            rewards.alignment = TextAlignmentOptions.TopRight;
            rewards.enableWordWrapping = false;
            rewards.overflowMode = TextOverflowModes.Overflow;
            view._rewards = rewards;

            // In front of the title, on the same line, in the status colour.
            //
            // It used to sit in the top-right corner beside the Kappa badge, which is where you look
            // last. This glyph answers the question the box exists to answer, and reading it after
            // the name rather than with it was the whole reason status felt like something you had
            // to decode. Hidden with the title once the box is only a code, where it was a smudge.
            var glyph = CreateText(rect, "StatusGlyph", LayoutMetrics.GlyphFontSize, FontStyles.Bold,
                new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero);
            var glyphRect = (RectTransform)glyph.transform;
            glyphRect.pivot = new Vector2(0f, 1f);
            glyphRect.sizeDelta = new Vector2(LayoutMetrics.GlyphSlotWidth, 22f);
            glyph.alignment = TextAlignmentOptions.TopLeft;
            view._statusGlyph = glyph;


            // The trader stripe, down the left edge.
            //
            // Its own channel rather than the box fill, which stays the STATUS colour: status is how
            // you read in-progress from available from locked at a glance, and that is worth more in
            // game than tinting whole nodes by trader the way the web trees do. This way you get
            // both - the band tells you whose chain you are in, the box still tells you where you
            // are in it.
            var stripeGo = new GameObject("TraderStripe", typeof(RectTransform), typeof(Image));
            var stripeRect = (RectTransform)stripeGo.transform;
            stripeRect.SetParent(rect, worldPositionStays: false);
            stripeRect.anchorMin = new Vector2(0f, 0f);
            stripeRect.anchorMax = new Vector2(0f, 1f);
            stripeRect.pivot = new Vector2(0f, 0.5f);
            // Where the STATUS bar ends, not at the box's edge.
            //
            // Both were drawn at x = 0 - the status bar 6px wide, the stripe 4px - and the stripe was
            // pushed to the back with SetAsFirstSibling, so the status bar covered it completely. The
            // trader colour has never actually been on screen.
            //
            // It fits in the padding that already exists between the bar and the text, so nothing
            // moves and no measurement changes: status bar 0-6, trader stripe 6-10, text from
            // TextInsetX, which is still 14.
            stripeRect.anchoredPosition = new Vector2(LayoutMetrics.StatusBarWidth, 0f);
            stripeRect.sizeDelta = new Vector2(TraderStripeWidth, 0f);

            var stripeImage = stripeGo.GetComponent<Image>();
            stripeImage.raycastTarget = false;
            view._traderStripe = stripeImage;

            // The game's own tooltip, on hover. Text is set per Bind.

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
            RefreshTraderStripe();

            // Size to the title, not the other way round: a name like "The Survivalist Path -
            // Unprotected but Dangerous" was an ellipsis at one line, and three lines over the
            // subtitle when allowed to wrap. Two lines and a taller box is the honest middle - and
            // the second line is the PART, because "The Enemy's Mind - P..." six times in a row
            // says nothing. Widths are estimated from character counts rather than measured:
            // TMP's measurement answered wrong for pooled views, and an estimate is deterministic.
            _title.fontSize = LayoutMetrics.TitleFontSize;

            // The box the layout reserved for this quest, from the same call the layout made - so
            // the text is fitted to the space that was actually allocated, rather than to a global
            // constant the box no longer has.
            var size = MeasureSize(node, out _tall);
            _size = size;

            // Glyph and badges live top-right; RefreshBadges above has just counted them.
            var titleWidth = size.x - LayoutMetrics.TitleInsetX - 44f - BadgeInset;
            var (head, tail) = TitleParts(DisplayName(node));

            // The head line only has to name the series, so it may lose its tail; the single-line
            // and episode forms keep theirs, because that is what tells one box from the next.
            _title.text = GameStyle.Safe(_tall && tail != null
                ? FitToWidth(head, titleWidth, protectTail: false) + "\n" + FitToWidth(tail, titleWidth)
                : FitToWidth(DisplayName(node), titleWidth));

            ((RectTransform)transform).sizeDelta = size;

            RefreshStatus();
            RefreshDetails();
            LayoutContents();
        }

        // ------------------------------------------------------------------ title fitting

        /// <summary>Average advance of this font's bold face, as a fraction of the font size.
        /// Bender bold sits around 0.55em; erring slightly wide only costs an early ellipsis.</summary>
        private const float AverageGlyphAdvance = 0.56f;

        private static float EstimateWidth(string text, int fontSize) =>
            (text?.Length ?? 0) * fontSize * AverageGlyphAdvance;

        /// <summary>Cuts a line to the estimated width with an ellipsis - by hand, because TMP's
        /// ellipsis only ever applies to the last line, and a two-line title needs it on the first.</summary>
        private string FitToWidth(string text, float width, bool protectTail = true)
        {
            var maxChars = Mathf.FloorToInt(width / (LayoutMetrics.TitleFontSize * AverageGlyphAdvance));
            if (text.Length <= maxChars || maxChars < 6) return text;

            // Cut the MIDDLE, not the tail.
            //
            // Cutting from the right removes the only part that distinguishes one quest in a series
            // from the next: a column of "Weapon Proficiency - Marksman..." six times over is six
            // identical boxes. The end of a quest name is where the episode, the part number and
            // the weapon live - it is the half worth keeping.
            if (!protectTail)
                return text.Substring(0, maxChars - 1).TrimEnd() + "\u2026";

            // Roughly a third to the head, the rest to the tail: the head only has to identify the
            // series, which the surrounding boxes are also saying.
            var tailChars = Mathf.Max(3, (maxChars - 1) * 2 / 3);
            var headChars = maxChars - 1 - tailChars;

            if (headChars < 2) return "\u2026" + SnapForward(text, text.Length - (maxChars - 1));

            // Snapped to word boundaries at both ends. Cutting on a raw character count produced
            // "Final\u2026onclusion": an ellipsis landing mid-word reads as a rendering fault rather
            // than an abbreviation, and it ate the capital that made the word recognisable.
            return SnapBack(text, headChars).TrimEnd() + "\u2026" +
                   SnapForward(text, text.Length - tailChars).TrimStart();
        }

        /// <summary>The longest prefix no longer than <paramref name="length"/> that ends on a word
        /// boundary. Falls back to the raw cut when one word is longer than the whole budget.</summary>
        private static string SnapBack(string text, int length)
        {
            length = Mathf.Clamp(length, 1, text.Length);

            var space = text.LastIndexOf(' ', length - 1);
            return space > 0 ? text.Substring(0, space) : text.Substring(0, length);
        }

        /// <summary>The suffix beginning at the first word boundary at or after <paramref name="from"/>,
        /// so the tail keeps whole words.</summary>
        private static string SnapForward(string text, int from)
        {
            var start = Mathf.Clamp(from, 0, text.Length - 1);

            var space = text.IndexOf(' ', start);
            return space >= 0 && space < text.Length - 1 ? text.Substring(space + 1) : text.Substring(start);
        }

        /// <summary>Series names long enough to crowd out the part that identifies the quest.
        ///
        /// Applied before anything measures a title, so the box is sized for what will actually be
        /// drawn. Deliberately short: an abbreviation only earns its place when the full form is
        /// long AND repeated down a chain, which is exactly the case that made the tree unreadable.
        /// A name not in this table is left alone.</summary>
        private static readonly Dictionary<string, string> SeriesAbbreviations =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Weapon Proficiency"] = "W. Prof.",
                ["Weapon Acquisition"] = "W. Acq.",
                ["Weapon Mastery"] = "W. Mastery",
                ["Armour Delivery"] = "Armour Del.",
                ["Sanitary Investigation"] = "Sanitary Inv.",
                ["Compensation for Damage"] = "Compensation",
                ["The Survivalist Path"] = "Survivalist",
                ["Grab That Cash, Make a Stash"] = "Grab That Cash",
                ["Physical and Medical Preparation"] = "Phys. & Med. Prep.",
                ["The Huntsman Path"] = "Huntsman"
            };

        /// <summary>The name as the box draws it: the series shortened where there is a shortening
        /// worth making, the episode untouched.
        ///
        /// Everything that measures a title goes through this - MeasureSize when the layout decides
        /// how wide the box is, Bind when the text is fitted into it. Two callers reading two
        /// different names is how a title ends up cut at a width nothing laid it out for.</summary>
        public static string DisplayName(QuestNode node)
        {
            if (node == null) return "";

            var name = node.Name;
            var (head, tail) = TitleParts(name);

            if (!SeriesAbbreviations.TryGetValue(head, out var shortHead)) return name;

            return string.IsNullOrEmpty(tail) ? shortHead : shortHead + " \u2013 " + tail;
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

        /// <summary>Re-reads only the status colours - cheap enough to call on every node whenever
        /// QuestController.OnConditionalStatusChanged fires.</summary>
        public void RefreshStatus()
        {
            var status = Node.Status;
            var color = ColorFor(status);

            // "Cannot be started", which is now two statuses rather than one. Every recede-into-the-
            // background decision below keys off THIS rather than off Locked alone - a gated quest
            // is no more takeable than a locked one, and leaving it bright would have made the
            // biggest visual difference on the tree between two states that are equally out of
            // reach today.
            var unstarted = status == ENodeStatus.Locked || status == ENodeStatus.Gated;

            if (_statusBar != null) _statusBar.color = color;
            if (_fill != null) _fill.color = unstarted ? LockedFillColor : FillColor;

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

            // Body text is the game's own colour on a dark box; the two states you cannot act on
            // step down, so those parts of the tree recede as a whole.
            //
            // Completed recedes too, but by FADING rather than by dimming the box. The obvious
            // implementation - drop the CanvasGroup alpha - would have fought the hover highlight
            // for the same channel, and ClearHighlight resets every view to 1 on mouse-out, so a
            // completed box would have brightened permanently the first time you hovered near it.
            // Fading the ink leaves that channel to the one writer that owns it.
            var completed = status == ENodeStatus.Completed;
            var ink = unstarted ? GameStyle.DimTextColor
                : completed ? Fade(GameStyle.TextColor, 0.72f)
                : GameStyle.TextColor;

            if (_title != null)
            {
                _title.color = ink;

                // Struck through when it is done, so the state survives greyscale and survives the
                // zoom at which the bar is a smudge.
                _title.fontStyle = completed
                    ? FontStyles.Bold | FontStyles.Strikethrough
                    : FontStyles.Bold;
            }

            if (_subtitle != null)
                _subtitle.color = Fade(GameStyle.TextColor, unstarted ? 0.4f : completed ? 0.42f : 0.6f);
            if (_rewards != null) _rewards.color = Fade(GameStyle.TextColor, unstarted ? 0.35f : 0.5f);

            if (_statusGlyph != null)
            {
                // Locked shows its cross again.
                //
                // It was dropped because a small grey cross in the top-right corner read as a close
                // button - a fair objection, and the wrong trade: the majority of a tree is locked,
                // so hiding its mark means most boxes answer "is this done" with nothing at all, and
                // you are back to reading a 6px bar. Bigger and in the status colour it reads as a
                // state, and the three marks now partition the tree between them.
                _statusGlyph.text = GlyphFor(status);

                // Locked's cross is faded because the grey majority of a tree does not need
                // shouting at. Gated's arrow is NOT - the box around it has just receded, and the
                // arrow is the one thing on it saying this is a wall you climb rather than a quest
                // you have not reached.
                _statusGlyph.color = status == ENodeStatus.Locked ? Fade(color, 0.75f) : color;
            }
        }

        private static Color Fade(Color color, float alpha) =>
            new(color.r, color.g, color.b, alpha);

        /// <summary>How many of this quest's necessary objectives are done.
        ///
        /// Counted from ConditionProgress, which the server already sends and the detail panel
        /// already reads through QuestSummary.TryProgress - so this is the same arithmetic the
        /// panel does, on the same numbers, and the two cannot disagree.
        ///
        /// The profile simply has no entry for an objective that has not started, so "no counter"
        /// reads as not done. A completed quest is all of them by definition, whatever the counters
        /// were left holding.</summary>
        private void ObjectiveProgress(QuestGraph.ProfilePayloadDto profile, out int done, out int total)
        {
            done = 0;
            total = 0;

            foreach (var objective in Node.NecessaryObjectives)
            {
                total++;

                if (QuestSummary.TryProgress(objective, profile, out var current, out var target) &&
                    target > 0 && current >= target)
                {
                    done++;
                }
            }

            if (Node.Status == ENodeStatus.Completed) done = total;
        }

        /// <summary>Why this box cannot be started, in the few words a meta row has room for.
        ///
        /// The server has already decided the reason - this renders it rather than re-deriving it.
        /// Null when there is nothing on file, and the caller then draws the normal meta row rather
        /// than an empty one.</summary>
        private string BlockedReason(QuestGraph.ProfilePayloadDto profile)
        {
            if (profile?.LockReasons == null) return null;
            if (!profile.LockReasons.TryGetValue(Node.Id, out var reason) || reason == null) return null;

            var hex = HexFor(Node.Status);

            switch (reason.Kind)
            {
                case "Level":
                    return $"<color=#{hex}>Lv {reason.RequiredValue}</color>";

                case "Loyalty":
                    return $"<color=#{hex}>LL{reason.RequiredValue}</color>";

                case "Standing":
                    return $"<color=#{hex}>Rep {reason.RequiredValue}</color>";

                case "Prerequisite":
                    return BlockingQuestLine(reason, hex);

                default:
                    // Faction, edition, event - a wall that never moves. The detail is already
                    // short enough to print as it stands.
                    return string.IsNullOrEmpty(reason.Detail)
                        ? null
                        : $"<color=#{hex}>{GameStyle.Safe(reason.Detail)}</color>";
            }
        }

        /// <summary>"Needs Carbines III", plus a count when more than one quest is in the way.
        ///
        /// Names the FIRST blocker rather than listing them: a meta row holds about thirty
        /// characters, and one name you can act on beats three you cannot read.</summary>
        private static string BlockingQuestLine(QuestGraph.LockReasonDto reason, string hex)
        {
            if (reason.BlockingQuestIds == null || reason.BlockingQuestIds.Count == 0) return null;
            if (_graphContext == null) return null;

            string firstName = null;

            foreach (var id in reason.BlockingQuestIds)
            {
                if (!_graphContext.NodesById.TryGetValue(id, out var blocker)) continue;

                firstName = blocker.Name;
                break;
            }

            if (string.IsNullOrEmpty(firstName)) return null;

            // The series prefix is what the blocker shares with everything around it; the episode is
            // what tells them apart. "Needs Weapon Proficiency" on a box whose neighbours are all
            // Weapon Proficiency says nothing at all.
            var (head, tail) = TitleParts(firstName);
            var shown = string.IsNullOrEmpty(tail) ? head : tail;

            var more = reason.BlockingQuestIds.Count - 1;
            var suffix = more > 0 ? $"  <color=#FFFFFF60>+{more} more</color>" : "";

            var arrow = string.IsNullOrEmpty(Glyphs.Needs) ? "" : Glyphs.Needs + " ";

            return $"<color=#{hex}>{arrow}Needs {GameStyle.Safe(shown)}</color>{suffix}";
        }

        /// <summary>A mark per kind of reward, at most three.
        ///
        /// Kinds, not amounts: the box has room to say "there is XP and an item in this", the panel
        /// has room to say how much. Glyphs come from the set this codebase already draws elsewhere,
        /// because the game's font is the one thing here that cannot be checked without launching
        /// it - every one of these is on screen somewhere today.</summary>
        private string RewardMarks()
        {
            if (Node.Rewards == null) return "";

            var marks = new List<string>();

            foreach (var reward in Node.Rewards)
            {
                var mark = RewardGlyph(reward.Type);
                if (mark == null || marks.Contains(mark)) continue;

                marks.Add(mark);
                if (marks.Count == 3) break;
            }

            return marks.Count == 0 ? "" : string.Join(" ", marks);
        }

        /// <summary>The mark for a kind of reward, for anything outside the box that wants to say
        /// the same thing the same way - the detail panel and the hover card both do.</summary>
        public static string GlyphForReward(string rewardType) => RewardGlyph(rewardType) ?? "";

        private static string RewardGlyph(string rewardType)
        {
            var mark = rewardType switch
            {
                "Experience" => GameStyle.PickGlyph("\u25c6", "\u2666", ""),       // filled diamond
                "Item" => GameStyle.PickGlyph("\u25a0", "\u25aa", ""),             // filled square
                "TraderStanding" => GameStyle.PickGlyph("\u25cf", "\u2022", ""),   // filled circle
                "TraderUnlock" => GameStyle.PickGlyph("\u25ce", "\u25c9", ""),     // bullseye
                "AssortmentUnlock" => GameStyle.PickGlyph("\u25cb", "\u25e6", ""), // hollow circle
                "Skill" => GameStyle.PickGlyph("\u25b2", "\u25b4", ""),            // up triangle
                _ => null
            };

            // Empty means none of the candidates could be drawn. Better no mark at all than a row
            // of identical boxes saying nothing except that something is broken.
            return string.IsNullOrEmpty(mark) ? null : mark;
        }

        /// <summary>Whether the bar is wanted, kept so the detail-level sweep can hide it without
        /// losing the fact that this quest is in progress.</summary>
        private bool _progressWanted;

        /// <summary>Shows, hides and fills the in-progress bar. Built the first time a box needs
        /// one; most boxes never do.</summary>
        private void ShowProgress(bool wanted, float fraction)
        {
            _progressWanted = wanted;

            if (!wanted)
            {
                if (_progressTrack != null) _progressTrack.SetActive(false);
                return;
            }

            EnsureProgressBar();

            _progressTrack.SetActive(true);
            if (_progressFillImage != null) _progressFillImage.color = ColorFor(ENodeStatus.Active);

            // The fill is an ANCHOR, not a width: the bar stretches with the box, and boxes stopped
            // being a fixed width in 1.9.0, so a width computed here would be wrong for every node
            // whose title made it wider.
            if (_progressFill != null)
                _progressFill.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        }

        private void EnsureProgressBar()
        {
            if (_progressTrack != null) return;

            var rect = (RectTransform)transform;

            _progressTrack = new GameObject("Progress", typeof(RectTransform), typeof(Image));
            var trackRect = (RectTransform)_progressTrack.transform;
            trackRect.SetParent(rect, worldPositionStays: false);
            trackRect.anchorMin = new Vector2(0f, 0f);
            trackRect.anchorMax = new Vector2(1f, 0f);
            trackRect.pivot = new Vector2(0f, 0f);
            trackRect.offsetMin = new Vector2(LayoutMetrics.TextInsetX, 4f);
            trackRect.offsetMax = new Vector2(-8f, 4f + LayoutMetrics.ProgressBarHeight);

            var track = _progressTrack.GetComponent<Image>();
            track.color = new Color(1f, 1f, 1f, 0.10f);
            track.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            _progressFill = (RectTransform)fillGo.transform;
            _progressFill.SetParent(trackRect, worldPositionStays: false);
            _progressFill.anchorMin = new Vector2(0f, 0f);
            _progressFill.anchorMax = new Vector2(0f, 1f);
            _progressFill.pivot = new Vector2(0f, 0.5f);
            _progressFill.offsetMin = Vector2.zero;
            _progressFill.offsetMax = Vector2.zero;

            _progressFillImage = fillGo.GetComponent<Image>();
            _progressFillImage.raycastTarget = false;
        }

        private void RefreshDetails()
        {
            var profile = QuestGraph.QuestDataClient.GetProfile();
            var status = Node.Status;
            var blocked = status == ENodeStatus.Locked || status == ENodeStatus.Gated;

            var level = Node.Level > 0 ? $"Lv {Node.Level}" : null;
            ObjectiveProgress(profile, out var done, out var total);
            var counted = total > 0 ? $"{done}/{total}" : null;

            // The map gave up its slot to the objective count. Of the three it was the weakest -
            // most quests are on one obvious map and the sidebar says so anyway, whereas "6/9" is
            // the only thing on the box that moves while you play.
            var parts = new[] { Node.TraderName, level, counted }.Where(p => !string.IsNullOrEmpty(p)).ToList();

            // A blocked box spends its meta row on WHY. This is the single most useful thing it can
            // say and until now it cost a click to find out - the tree drew "Scorpion · Lv 25" on a
            // quest you cannot touch, which is the one case where the trader and the level are not
            // what you wanted to know.
            var reason = blocked ? BlockedReason(profile) : null;

            _subtitle.text = reason ?? string.Join("  ·  ", parts);

            if (_rewards != null) _rewards.text = blocked ? "" : RewardMarks();

            // Only what you are actually doing gets a bar. On everything else it would either be
            // empty or full, which the count already says.
            ShowProgress(status == ENodeStatus.Active && total > 0, total > 0 ? (float)done / total : 0f);

        }

        /// <summary>
        /// Level of detail for the current zoom. Zoomed out, the subtitle and objective become
        /// unreadable smears that only add noise; hiding them and growing the title keeps the one
        /// thing worth reading readable. Cheap: toggles and one font size, on visible views only.
        /// </summary>
        /// <summary>The trader's colour, or nothing when the setting is off.
        ///
        /// Every trader has a colour including ones from mods - TraderPalette derives one from the
        /// id rather than falling back to grey, because on an install with several trader mods a
        /// grey fallback would have left most of the tree uncoloured.</summary>
        private void RefreshTraderStripe()
        {
            if (_traderStripe == null) return;

            var show = (!ModSettings.Ready || ModSettings.ShowTraderColours.Value) &&
                       Node != null && !string.IsNullOrEmpty(Node.TraderId);

            _traderStripe.enabled = show;
            if (show) _traderStripe.color = TraderPalette.For(Node.TraderId);
        }

        /// <summary>Lays the contents out inside the box.
        ///
        /// ONE path, always. There used to be three - full, title-only, and a short code - switched
        /// by a zoom threshold, and the node rendered differently depending on which had last been
        /// applied to it. Two branches wrote the same eight properties on one TMP_Text, and the
        /// level that chose between them lived on the view, so a pooled view came back wearing the
        /// level it was released at. No threshold fixes that; deleting the second branch does.
        ///
        /// Everything here is placed with offsetMin/offsetMax rather than anchoredPosition, and that
        /// is the second fix. A rect stretched between anchors at x 0 and 1 does not take
        /// anchoredPosition.x as a left inset - it takes it as an offset of the rect's PIVOT from
        /// the centre of the span. Setting it to the text inset therefore shoved every row right by
        /// that amount and pushed its right edge off the box, which is why a short title looked
        /// centred and a long one ran under the badges. offsetMin/offsetMax are literally the left
        /// and right insets, so they cannot mean anything else.</summary>
        private void LayoutContents()
        {
            RefreshBadges();

            if (_title == null) return;

            var lines = _tall ? 2 : 1;

            var titleTop = LayoutMetrics.ContentTopPad;
            var titleHeight = LayoutMetrics.TitleLineHeight * lines;
            var metaTop = titleTop + titleHeight + LayoutMetrics.RowGapY;
            var metaHeight = LayoutMetrics.MetaLineHeight;

            // The glyph sits on the title's own line, left of it, in the slot the title inset was
            // widened to leave.
            if (_statusGlyph != null)
            {
                PlaceRow((RectTransform)_statusGlyph.transform,
                    LayoutMetrics.TextInsetX,
                    _size.x - LayoutMetrics.TextInsetX - LayoutMetrics.GlyphSlotWidth,
                    titleTop,
                    LayoutMetrics.TitleLineHeight);

                _statusGlyph.alignment = TextAlignmentOptions.TopLeft;
            }

            _title.fontSize = LayoutMetrics.TitleFontSize;
            _title.alignment = TextAlignmentOptions.TopLeft;

            // The break is explicit and each line is pre-fitted, so wrapping would only second-guess
            // the fit. maxVisibleLines is the hard stop: turning wrapping off was not enough on its
            // own - the title still broke onto the meta row's line.
            _title.enableWordWrapping = false;
            _title.maxVisibleLines = lines;

            // Badge-aware on the right, like the fitting width in Bind: a rect reaching under the
            // badges put the tail of the name behind them.
            PlaceRow((RectTransform)_title.transform,
                LayoutMetrics.TitleInsetX, 8f + BadgeInset, titleTop, titleHeight);

            // The meta row keeps clear of the reward marks on its right.
            if (_subtitle != null)
                PlaceRow((RectTransform)_subtitle.transform,
                    LayoutMetrics.TextInsetX, RewardSlotWidth + 12f, metaTop, metaHeight);

            if (_rewards != null)
                PlaceRow((RectTransform)_rewards.transform,
                    _size.x - RewardSlotWidth - 8f, 8f, metaTop, metaHeight);
        }

        /// <summary>Room reserved at the right-hand end of the meta row for the reward marks.</summary>
        private const float RewardSlotWidth = 56f;

        /// <summary>Pins a row to the top of the box with explicit left and right insets.
        ///
        /// The rect stretches horizontally between the box's edges and hangs from its top, so
        /// <paramref name="top"/> and <paramref name="height"/> are distances DOWN from the top edge
        /// and read the way the box is described.</summary>
        private static void PlaceRow(RectTransform rect, float left, float right, float top, float height)
        {
            if (rect == null) return;

            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(left, -(top + height));
            rect.offsetMax = new Vector2(-right, -top);
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

            var slot = 0;

            if (kappa) PlaceBadge(EnsureBadge(ref _kappaBadge, "K", GameStyle.KappaGold), slot++, true);
            else if (_kappaBadge != null) _kappaBadge.SetActive(false);

            if (collector) PlaceBadge(EnsureBadge(ref _collectorBadge, "C", GameStyle.CollectorBlue), slot, true);
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
