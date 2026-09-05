using System;
using Comfort.Common;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Makes this mod's runtime-built UI look and sound like part of the game rather than an
    /// overlay bolted onto it.
    ///
    /// The font, panel sprite and text colour are HARVESTED from UI the game has already
    /// instantiated (see CaptureFrom), which is what makes this mod's text and panels match the
    /// game instead of rendering in TMP's fallback font on flat rectangles.
    ///
    /// Whole controls are NOT cloned - see the note on CreateButton for what happened when they
    /// were. Harvesting the ingredients works; cloning the finished furniture does not.
    ///
    /// Every part of this degrades. If nothing has been captured yet, Apply/ApplyPanel no-op and
    /// the UI still builds. Nothing in this class may ever stop the mod working.
    /// </summary>
    internal static class GameStyle
    {
        private static TMP_FontAsset _font;
        private static Material _fontMaterial;
        private static Sprite _panelSprite;
        private static Image.Type _panelSpriteType = Image.Type.Sliced;

        // Defaults chosen to match EFT's menu palette, and overridden by whatever the harvested
        // donor turns out to use. Named rather than inlined so no call site hardcodes a colour.
        public static Color TextColor { get; private set; } = new(0.78f, 0.76f, 0.71f);
        public static Color DimTextColor { get; private set; } = new(0.78f, 0.76f, 0.71f, 0.55f);
        public static Color AccentColor { get; private set; } = new(0.78f, 0.65f, 0.35f);
        public static Color ScreenColor { get; private set; } = new(0.055f, 0.055f, 0.05f, 0.97f);
        public static Color PanelColor { get; private set; } = new(1f, 1f, 1f, 0.05f);

        /// <summary>Called from MenuTaskBarPatch with the taskbar entry it clones from, before that
        /// clone's own label is overwritten - a real, always-available donor.</summary>
        public static void CaptureFrom(TMP_Text donorText, Image donorPanel)
        {
            if (_font == null && donorText != null && donorText.font != null)
            {
                _font = donorText.font;
                _fontMaterial = donorText.fontMaterial;

                // The taskbar label is the game telling us what its own body text looks like.
                TextColor = donorText.color;
                DimTextColor = new Color(TextColor.r, TextColor.g, TextColor.b, 0.55f);
            }

            if (_panelSprite == null && donorPanel != null && donorPanel.sprite != null)
            {
                _panelSprite = donorPanel.sprite;
                _panelSpriteType = donorPanel.type;
            }
        }

        public static void Apply(TMP_Text text)
        {
            if (text == null) return;

            if (_font != null)
            {
                text.font = _font;
                if (_fontMaterial != null) text.fontMaterial = _fontMaterial;
            }

            // Only recolour text still sitting on the plain white default - callers that chose a
            // deliberate colour (status greens, warning reds) keep it.
            if (text.color == Color.white) text.color = TextColor;
        }

        public static void ApplyPanel(Image image)
        {
            if (image == null || _panelSprite == null) return;
            image.sprite = _panelSprite;
            image.type = _panelSpriteType;
        }

        /// <summary>Plays one of the game's own UI sounds. A large part of why a screen feels native
        /// is that it sounds native; this costs nothing and needs no assets of our own.</summary>
        public static void PlaySound(EUISoundType sound)
        {
            try
            {
                if (Singleton<GUISounds>.Instantiated)
                    Singleton<GUISounds>.Instance.PlayUISound(sound);
            }
            catch (Exception)
            {
                // Audio is decoration; never let it break an interaction.
            }
        }

        /// <summary>
        /// A button styled to match the game: the harvested font, the harvested panel sprite, the
        /// sampled text colour, and EFT's own click sound.
        ///
        /// It is built rather than cloned, deliberately. An earlier version cloned
        /// EFT.UI.DefaultUIButton to inherit real chrome for free, but every DefaultUIButton in the
        /// menu is a full-size menu entry - the donor it found was the main menu's 'NextButton' -
        /// so the clones arrived with 24px+ content-sized labels and no background, and the toolbar
        /// buttons rendered as giant overlapping text. The hand-built version below is what the tab
        /// row already uses, and that reads correctly in-game. Do not "improve" this back into a
        /// DefaultUIButton clone without solving the donor-size problem first.
        /// </summary>
        public static RectTransform CreateButton(RectTransform parent, string label, Action onClick)
        {
            var go = new GameObject("QuestTreeButton", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, worldPositionStays: false);

            var background = go.GetComponent<Image>();
            background.color = PanelColor;
            ApplyPanel(background);

            var textGo = new GameObject("Text", typeof(RectTransform));
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
            Apply(text);

            var button = go.GetComponent<Button>();
            button.targetGraphic = background;

            // Built rather than cloned, so it has no ButtonFeedback of its own and plays the game's
            // click sound directly - otherwise half this screen would be silent and half would not.
            button.onClick.AddListener(() =>
            {
                PlaySound(EUISoundType.ButtonClick);
                onClick();
            });

            return rect;
        }
    }
}
