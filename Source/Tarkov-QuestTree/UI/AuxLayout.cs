using System.Collections.Generic;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Small builders shared by every aux view - Do next, Items, Kappa, Maps and Settings - plus the
    /// quest body, the hover card and the intro panel. (It said "the Kappa and Settings tabs" when there
    /// were two.) All are plain vertical stacks, laid out with a running y-cursor rather than a
    /// VerticalLayoutGroup - the same fully-manual approach the rest of this panel settled on after
    /// Unity's automatic layout components cost two rounds of debugging here.
    ///
    /// Every method returns the height it consumed, so a caller can keep its own cursor without
    /// having to know how any individual row is built.
    /// </summary>
    internal static class AuxLayout
    {
        public const float Padding = 16f;

        /// <summary>Removes every child of a rebuilt container. Detached before the deferred
        /// Destroy, so the outgoing rows do not draw over the incoming ones for a frame; walked
        /// backwards because detaching shifts every later sibling down. Was three copies.</summary>
        public static void ClearChildren(Transform parent)
        {
            if (parent == null) return;

            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                child.SetParent(null);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        /// <summary>The list views stop stretching their rows past this - a 960px row of 12px text
        /// is already more than a line should be. Was a private copy in three views.</summary>
        public const float MaxContentWidth = 960f;
        public const float RowHeight = 22f;
        public const float HeadingHeight = 34f;
        public const float ToggleSize = 18f;

        public static float AddHeading(RectTransform parent, ref float y, string text)
        {
            var label = AddText(parent, ref y, text, HeadingHeight, 15, FontStyles.Bold);
            label.color = new Color(0.85f, 0.78f, 0.55f);
            return HeadingHeight;
        }

        /// <summary>A section title the way the game's own screens draw them: small capitals in the
        /// accent colour with a hairline under. Positioned at an explicit x and width rather than
        /// stretched to the parent, so it works inside a column as well as a page.</summary>
        public static float AddSectionHeader(RectTransform parent, ref float y, string text, float x, float width)
        {
            const float height = 24f;

            var go = new GameObject("SectionHeader", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text.ToUpperInvariant();
            label.fontSize = 11;
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 4f;
            label.color = GameStyle.AccentColor;
            label.alignment = TextAlignmentOptions.BottomLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            GameStyle.Apply(label);
            label.color = GameStyle.AccentColor;

            var ruleGo = new GameObject("Rule", typeof(RectTransform), typeof(Image));
            var rule = (RectTransform)ruleGo.transform;
            rule.SetParent(parent, worldPositionStays: false);
            rule.anchorMin = rule.anchorMax = new Vector2(0f, 1f);
            rule.pivot = new Vector2(0f, 1f);
            rule.anchoredPosition = new Vector2(x, -(y + height + 2f));
            rule.sizeDelta = new Vector2(width, 1f);
            var ruleImage = ruleGo.GetComponent<Image>();
            ruleImage.color = new Color(GameStyle.AccentColor.r, GameStyle.AccentColor.g, GameStyle.AccentColor.b, 0.35f);
            ruleImage.raycastTarget = false;

            y += height + 8f;
            return height + 8f;
        }

        public static TMP_Text AddText(
            RectTransform parent, ref float y, string text, float height = RowHeight,
            int fontSize = 13, FontStyles style = FontStyles.Normal, float indent = 0f)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(Padding + indent, 0f);
            rect.offsetMax = new Vector2(-Padding, 0f);
            rect.anchoredPosition = new Vector2(Padding + indent, -y);
            rect.sizeDelta = new Vector2(-(Padding * 2f + indent), height);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = style;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false; // plain text must not eat the wheel meant for the list beneath it
            GameStyle.Apply(label);

            y += height;
            return label;
        }

        /// <summary>A labelled on/off row. Uses a Button plus a box that fills when on, rather than
        /// Unity's Toggle component, so there is no toggle-group or graphic-swap behaviour to
        /// configure - it is one click handler and one colour.</summary>
        public static float AddToggle(
            RectTransform parent, ref float y, string label, string description, bool value,
            System.Action<bool> onChanged)
        {
            var height = string.IsNullOrEmpty(description) ? 30f : 46f;

            // Stretched to the full width of the column, inset by the padding on both sides -
            // hence the anchor at x=1 and the negative width, which is an inset, not a size.
            BuildToggle(parent, label, description, value, onChanged,
                new Vector2(Padding, -y), new Vector2(-(Padding * 2f), height), stretchWidth: true);

            y += height;
            return height;
        }

        /// <summary>The same control placed at an absolute position instead of on the running
        /// <c>y</c> cursor, for a header laid out in columns rather than as a stack - the Maps tab
        /// sits its toggle beside the map and floor dropdowns, which are placed the same way
        /// (see <see cref="AddDropdownHeader"/>).</summary>
        public static float AddToggleAt(
            RectTransform parent, float x, float top, string label, bool value,
            System.Action<bool> onChanged, float width = 240f)
        {
            const float height = DropdownHeight;

            BuildToggle(parent, label, description: null, value: value, onChanged: onChanged,
                position: new Vector2(x, -top), size: new Vector2(width, height), stretchWidth: false);

            return height;
        }

        /// <summary>Shared construction for both toggle placements. A Button plus a box that fills
        /// when on, rather than Unity's Toggle component, so there is no toggle-group or
        /// graphic-swap behaviour to configure - it is one click handler and one colour.
        ///
        /// <paramref name="stretchWidth"/> decides how <paramref name="size"/>'s x is read: stretched
        /// it is an inset from the parent's right edge (so it is negative), fixed it is a width.</summary>
        private static void BuildToggle(
            RectTransform parent, string label, string description, bool value,
            System.Action<bool> onChanged, Vector2 position, Vector2 size, bool stretchWidth)
        {
            var go = new GameObject("Toggle", typeof(RectTransform), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(stretchWidth ? 1f : 0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var boxRect = (RectTransform)boxGo.transform;
            boxRect.SetParent(rect, worldPositionStays: false);
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(0f, 1f);
            boxRect.pivot = new Vector2(0f, 1f);
            boxRect.anchoredPosition = new Vector2(0f, -6f);
            boxRect.sizeDelta = new Vector2(ToggleSize, ToggleSize);

            var box = boxGo.GetComponent<Image>();
            box.color = value ? GameStyle.KappaGold : new Color(1f, 1f, 1f, 0.15f);
            GameStyle.ApplyPanel(box);

            var textGo = new GameObject("Label", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(ToggleSize + 10f, 0f);
            textRect.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = string.IsNullOrEmpty(description)
                ? label
                : $"{label}\n<size=11><color=#FFFFFF80>{description}</color></size>";
            text.fontSize = 13;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.color = Color.white;
            GameStyle.Apply(text);

            var current = value;
            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                current = !current;
                box.color = current ? GameStyle.KappaGold : new Color(1f, 1f, 1f, 0.15f);
                onChanged(current);
            });
        }

        public static float AddButton(RectTransform parent, ref float y, string label, System.Action onClick)
        {
            const float height = 30f;

            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(Padding, -y);
            rect.sizeDelta = new Vector2(220f, height);

            var background = go.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.12f);
            GameStyle.ApplyPanel(background);

            var textGo = new GameObject("Label", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.fontSize = 12;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            GameStyle.Apply(text);

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => onClick());

            y += height + 6f;
            return height + 6f;
        }

        /// <summary>A numeric row with - and + buttons. Used instead of a slider because these are
        /// coarse values where an exact number matters more than dragging to it.</summary>
        public static float AddStepper(
            RectTransform parent, ref float y, string label, int value, int step,
            System.Action<int> onChanged)
        {
            const float height = 30f;
            const float buttonSize = 26f;

            var rowY = y;
            var text = AddText(parent, ref y, $"{label}: <b>{value}</b>", height, 13);

            // Buttons are positioned against the row that AddText just consumed, so the label and
            // the controls share a line rather than stacking.
            AddStepButton(parent, rowY, "-", Padding + 260f, buttonSize, () => onChanged(value - step));
            AddStepButton(parent, rowY, "+", Padding + 260f + buttonSize + 6f, buttonSize, () => onChanged(value + step));

            return height;
        }

        private static void AddStepButton(
            RectTransform parent, float y, string glyph, float x, float size, System.Action onClick)
        {
            var go = new GameObject($"Step{glyph}", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(size, size);

            var background = go.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.12f);
            GameStyle.ApplyPanel(background);

            var textGo = new GameObject("Text", typeof(RectTransform));
            var textRect = (RectTransform)textGo.transform;
            textRect.SetParent(rect, worldPositionStays: false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = glyph;
            text.fontSize = 14;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            GameStyle.Apply(text);

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() => onClick());
        }

        /// <summary>A single line at an explicit x and width, ellipsised - a label, not prose.</summary>
        public static TMP_Text AddLabelAt(
            RectTransform parent, string text, float x, ref float y, float height, int fontSize, float width)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            GameStyle.Apply(label);

            y += height;
            return label;
        }

        /// <summary>Prose: wraps at the width, and takes the height it needs.
        ///
        /// Sized from TMP's own preferred height, because wrapped text has no height until measured -
        /// otherwise every line after a wrapped one is drawn on top of it. Empty text is spacing.
        ///
        /// The one wrapped-text row in this mod. MapView carried a byte-identical copy
        /// called AddDetailLine until 1.9.0; two copies of a layout rule is how the two sidebars
        /// came to need the same fix twice.</summary>
        public static void AddWrapped(
            RectTransform parent, string text, float x, ref float y, float width, int fontSize = 12)
        {
            if (text == null) return;

            if (text.Length == 0)
            {
                y += 6f;
                return;
            }

            var go = new GameObject("Detail", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);

            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.enableWordWrapping = true;
            label.raycastTarget = false;
            GameStyle.Apply(label);

            // Measured after Apply, since the font it installs decides the height - and through
            // GameStyle, which floors TMP's answer at a character-count estimate. TMP under-reports
            // a wrapped line as one line high, which drew every following row on top of it.
            var height = Mathf.Max(16f, GameStyle.MeasureHeight(label, text, width, 0f));
            rect.sizeDelta = new Vector2(width, height);
            y += height + 2f;
        }

        /// <summary>Wrapped text you can click.
        ///
        /// AddClickableRow is a single ellipsised line, which is right for a quest name and wrong
        /// for an objective - "Hand over the found in raid item: Corrugated hose 0/2" is a sentence,
        /// and cutting it at the panel's edge would hide the count that makes it worth reading.
        ///
        /// So the text wraps exactly as AddWrapped's does, and a transparent Image is laid behind
        /// the finished block to catch the pointer. Behind rather than around: a TextMeshProUGUI has
        /// no raycast target of its own, so without it there is nothing to click, and Unity raycasts
        /// a zero-alpha Graphic perfectly well.</summary>
        public static void AddClickableWrapped(
            RectTransform parent, string text, float x, ref float y, float width,
            System.Action onClick, int fontSize = 12)
        {
            if (string.IsNullOrEmpty(text) || onClick == null)
            {
                AddWrapped(parent, text, x, ref y, width, fontSize);
                return;
            }

            var top = y;

            var hitGo = new GameObject("ClickWrapped", typeof(RectTransform), typeof(Image), typeof(Button));
            var hit = (RectTransform)hitGo.transform;
            hit.SetParent(parent, worldPositionStays: false);
            hit.anchorMin = hit.anchorMax = new Vector2(0f, 1f);
            hit.pivot = new Vector2(0f, 1f);
            hit.anchoredPosition = new Vector2(x, -top);

            var background = hitGo.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0f);

            var button = hitGo.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() =>
            {
                GameStyle.PlaySound(EUISoundType.ButtonClick);
                onClick();
            });

            GameStyle.AddHoverFeedback(hitGo, background);

            // The text is added AFTER, so it is the later sibling and draws over the hit area
            // without any reordering.
            AddWrapped(parent, text, x, ref y, width, fontSize);

            // Sized to whatever the text turned out to need, which AddWrapped only knows once it
            // has measured it.
            hit.sizeDelta = new Vector2(width, Mathf.Max(16f, y - top - 2f));
        }

        /// <summary>
        /// One clickable line. Built by hand rather than as text alone because a bare TextMeshProUGUI
        /// has no raycast target at all; the Image is what makes the row hit-testable. It is fully
        /// transparent when unselected, and Unity still raycasts a zero-alpha Graphic.
        /// </summary>
        public static RectTransform AddClickableRow(
            RectTransform parent, string text, float x, ref float y, float width, bool selected,
            System.Action onClick, float height = RowHeight, int fontSize = 12)
        {
            var go = new GameObject("ClickRow", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);

            var background = go.GetComponent<Image>();
            var accent = GameStyle.AccentColor;
            background.color = selected ? new Color(accent.r, accent.g, accent.b, 0.22f) : Color.clear;

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;
            button.onClick.AddListener(() =>
            {
                GameStyle.PlaySound(EUISoundType.ButtonClick);
                onClick?.Invoke();
            });
            GameStyle.AddHoverFeedback(go, background);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(4f, 0f);
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            GameStyle.Apply(label);

            y += height;
            return rect;
        }

        /// <summary>A small tag - status, level, map - tinted with its colour. Advances x.</summary>
        public static void AddChip(RectTransform parent, string text, Color color, ref float x, float y, float height = 18f)
        {
            var go = new GameObject("Chip", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);

            var background = go.GetComponent<Image>();
            background.color = new Color(color.r, color.g, color.b, 0.18f);
            background.raycastTarget = false;
            GameStyle.ApplyPanel(background);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            var labelRect = (RectTransform)labelGo.transform;
            labelRect.SetParent(rect, worldPositionStays: false);
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = 10;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false;
            label.raycastTarget = false;
            GameStyle.Apply(label);
            label.color = new Color(color.r, color.g, color.b, 1f);

            // Sized to the label once the font is on it - see GameStyle.MeasureWidth.
            var width = GameStyle.MeasureWidth(label, text) + 14f;
            rect.sizeDelta = new Vector2(width, height);

            x += width + 6f;
        }

        /// <summary>
        /// A row of tabs across the top of an aux page, for a page whose sections are long enough
        /// that stacking them buries the last one. Only the selected section is then built, so the
        /// page costs less to draw as well as less to read.
        ///
        /// Each tab is sized to its own label rather than to a fixed width - a row of equal boxes
        /// wastes half its width on "Items" to fit "To unlock Collector" - and the row wraps rather
        /// than running off the edge of a narrow panel.
        /// </summary>
        /// <returns>The height the row consumed, y already advanced past it.</returns>
        public static float AddTabRow(
            RectTransform parent, ref float y, float x, float width,
            IReadOnlyList<string> labels, int selected, System.Action<int> onSelect)
        {
            const float height = 26f;
            const float gap = 6f;
            const float labelInset = 24f;

            var top = y;
            var cursor = x;

            for (var i = 0; i < labels.Count; i++)
            {
                var index = i;
                var tab = GameStyle.CreateButton(parent, labels[i], () => onSelect(index));
                var label = tab.GetComponentInChildren<TMP_Text>();

                // The selected tab is bold, and bold is wider - so it is styled BEFORE it is
                // measured, or the selected tab is the one that clips its own label.
                if (index == selected && label != null)
                {
                    label.color = GameStyle.AccentColor;
                    label.fontStyle = FontStyles.Bold;
                }

                // Measured off the button's own label, after CreateButton has put the harvested
                // font on it - the width is meaningless before that.
                var tabWidth = Mathf.Max(60f, GameStyle.MeasureWidth(label, labels[i]) + labelInset);

                if (cursor > x && cursor + tabWidth > x + width)
                {
                    cursor = x;
                    y += height + gap;
                }

                tab.anchorMin = tab.anchorMax = new Vector2(0f, 1f);
                tab.pivot = new Vector2(0f, 1f);
                tab.anchoredPosition = new Vector2(cursor, -y);
                tab.sizeDelta = new Vector2(tabWidth, height);

                if (index == selected)
                {
                    var accent = GameStyle.AccentColor;
                    var background = tab.GetComponent<Image>();
                    if (background != null)
                        background.color = new Color(accent.r, accent.g, accent.b, 0.3f);
                }

                cursor += tabWidth + gap;
            }

            y += height + 10f;
            return y - top;
        }

        /// <summary>A thin bar showing a fraction done - an objective's counter, say.</summary>
        public static void AddProgressBar(RectTransform parent, float fraction, float x, ref float y, float width, Color fill)
        {
            const float height = 4f;
            fraction = Mathf.Clamp01(fraction);

            var trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
            var track = (RectTransform)trackGo.transform;
            track.SetParent(parent, worldPositionStays: false);
            track.anchorMin = track.anchorMax = new Vector2(0f, 1f);
            track.pivot = new Vector2(0f, 1f);
            track.anchoredPosition = new Vector2(x, -y);
            track.sizeDelta = new Vector2(width, height);
            var trackImage = trackGo.GetComponent<Image>();
            trackImage.color = new Color(1f, 1f, 1f, 0.12f);
            trackImage.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            var fillRect = (RectTransform)fillGo.transform;
            fillRect.SetParent(track, worldPositionStays: false);
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = new Vector2(width * fraction, 0f);
            var fillImage = fillGo.GetComponent<Image>();
            fillImage.color = fill;
            fillImage.raycastTarget = false;

            y += height + 6f;
        }

        public static float AddSpacer(ref float y, float height = 12f)
        {
            y += height;
            return height;
        }

        public const float DropdownHeight = 28f;
        public const float DropdownRowHeight = 26f;

        /// <summary>
        /// The closed part of a dropdown: the button showing the current value. Split from the list
        /// deliberately, and they must stay split.
        ///
        /// Sibling order is draw order in Unity UI, and a dropdown list is an overlay - it covers
        /// the rows beneath it and belongs to no layout. When one method built both, the second
        /// dropdown on a page drew its HEADER over the first one's open LIST, which is what made the
        /// Settings dropdowns unusable in 1.8.4. Every header first, then the single open list last,
        /// is the only order that cannot do that.
        /// </summary>
        /// <returns>The height the header occupies, which is all a caller's y-cursor should
        /// advance by: the list is an overlay and contributes no layout height.</returns>
        public static float AddDropdownHeader(
            RectTransform parent, float top, IReadOnlyList<string> options, int selectedIndex,
            bool open, System.Action toggleOpen, float width = 300f, float x = Padding)
        {
            var selectedLabel = selectedIndex >= 0 && selectedIndex < options.Count
                ? options[selectedIndex]
                : "Select";

            var header = GameStyle.CreateButton(
                parent, $"{selectedLabel}   {(open ? "▲" : "▼")}", toggleOpen);
            header.anchorMin = header.anchorMax = new Vector2(0f, 1f);
            header.pivot = new Vector2(0f, 1f);
            header.anchoredPosition = new Vector2(x, -top);
            header.sizeDelta = new Vector2(width, DropdownHeight);

            return DropdownHeight;
        }

        /// <summary>
        /// The open part: the plate and the rows, drawn under the header at the same
        /// <paramref name="top"/> that header was given. Call this LAST on the page, and only for
        /// the one dropdown that is open - see the note on <see cref="AddDropdownHeader"/>.
        /// </summary>
        /// <returns>The y the list reaches, so the page can be made tall enough to scroll to the
        /// bottom of it. It is not layout height; nothing may be stacked under it.</returns>
        public static float AddDropdownList(
            RectTransform parent, float top, IReadOnlyList<string> options, int selectedIndex,
            System.Action<int> onSelect, float width = 300f, float x = Padding)
        {
            var listTop = top + DropdownHeight;

            // A backing plate behind the rows, so the map underneath cannot show between them.
            // Created before the rows so that it draws behind them.
            var plateGo = new GameObject("DropdownPlate", typeof(RectTransform), typeof(Image));
            var plate = (RectTransform)plateGo.transform;
            plate.SetParent(parent, worldPositionStays: false);
            plate.anchorMin = plate.anchorMax = new Vector2(0f, 1f);
            plate.pivot = new Vector2(0f, 1f);
            plate.anchoredPosition = new Vector2(x, -listTop);
            plate.sizeDelta = new Vector2(width, options.Count * DropdownRowHeight);

            var plateImage = plateGo.GetComponent<Image>();
            plateImage.color = GameStyle.ScreenColor;
            GameStyle.ApplyPanel(plateImage);

            for (var i = 0; i < options.Count; i++)
            {
                var index = i;

                var row = GameStyle.CreateButton(parent, options[i], () => onSelect(index));
                row.anchorMin = row.anchorMax = new Vector2(0f, 1f);
                row.pivot = new Vector2(0f, 1f);
                row.anchoredPosition = new Vector2(x, -(listTop + i * DropdownRowHeight));
                row.sizeDelta = new Vector2(width, DropdownRowHeight);

                if (index == selectedIndex)
                {
                    var background = row.GetComponent<Image>();
                    if (background != null) background.color = GameStyle.AccentColor;
                }
            }

            return listTop + options.Count * DropdownRowHeight + Padding;
        }
    }
}
