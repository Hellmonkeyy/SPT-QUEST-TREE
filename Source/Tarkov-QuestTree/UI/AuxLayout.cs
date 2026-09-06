using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Small builders shared by the Kappa and Settings tabs. Both are plain vertical stacks, laid
    /// out with a running y-cursor rather than a VerticalLayoutGroup - the same fully-manual
    /// approach the rest of this panel settled on after Unity's automatic layout components cost
    /// two rounds of debugging here.
    ///
    /// Every method returns the height it consumed, so a caller can keep its own cursor without
    /// having to know how any individual row is built.
    /// </summary>
    internal static class AuxLayout
    {
        public const float Padding = 16f;
        public const float RowHeight = 22f;
        public const float HeadingHeight = 34f;
        public const float ToggleSize = 18f;

        public static float AddHeading(RectTransform parent, ref float y, string text)
        {
            var label = AddText(parent, ref y, text, HeadingHeight, 15, FontStyles.Bold);
            label.color = new Color(0.85f, 0.78f, 0.55f);
            return HeadingHeight;
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

            var go = new GameObject("Toggle", typeof(RectTransform), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(Padding, -y);
            rect.sizeDelta = new Vector2(-(Padding * 2f), height);

            var boxGo = new GameObject("Box", typeof(RectTransform), typeof(Image));
            var boxRect = (RectTransform)boxGo.transform;
            boxRect.SetParent(rect, worldPositionStays: false);
            boxRect.anchorMin = boxRect.anchorMax = new Vector2(0f, 1f);
            boxRect.pivot = new Vector2(0f, 1f);
            boxRect.anchoredPosition = new Vector2(0f, -6f);
            boxRect.sizeDelta = new Vector2(ToggleSize, ToggleSize);

            var box = boxGo.GetComponent<Image>();
            box.color = value ? new Color(0.85f, 0.65f, 0.1f) : new Color(1f, 1f, 1f, 0.15f);
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
                box.color = current ? new Color(0.85f, 0.65f, 0.1f) : new Color(1f, 1f, 1f, 0.15f);
                onChanged(current);
            });

            y += height;
            return height;
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

        public static float AddSpacer(ref float y, float height = 12f)
        {
            y += height;
            return height;
        }

        public const float DropdownHeight = 28f;
        public const float DropdownRowHeight = 26f;

        /// <summary>
        /// A dropdown: a header showing the current choice, and - when open - the options drawn
        /// over whatever is beneath it.
        ///
        /// The open state and the selection both belong to the caller rather than to this method.
        /// Every aux view is rebuilt from scratch on each render, so anything held here would be
        /// discarded the moment the list was clicked; the caller keeps it in a static the same way
        /// MapView already keeps its selected map.
        ///
        /// Positioned at an explicit <paramref name="top"/> rather than off a running cursor, so it
        /// can be built *last* while still appearing at the top of the view. That ordering is the
        /// whole trick: Unity UI draws siblings in order, so an overlay has to be created after the
        /// content it covers. Returns the bottom of the open list so the caller can size its panel
        /// to reach it.
        ///
        /// Not TMP_Dropdown: that needs a runtime-built template hierarchy and would have to escape
        /// the aux panel's RectMask2D to overlay properly. Hand-built matches every other control
        /// here, and cloning the game's own UI has gone badly in this project before.
        /// </summary>
        public static float AddDropdown(
            RectTransform parent, float top, IReadOnlyList<string> options, int selectedIndex,
            bool open, System.Action toggleOpen, System.Action<int> onSelect, float width = 300f,
            float x = Padding)
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

            if (!open) return 0f;

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
