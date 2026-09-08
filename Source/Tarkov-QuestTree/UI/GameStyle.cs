using System;
using Comfort.Common;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
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

        /// <summary>Our own outlined copy of the harvested font material - see ApplyOutlined.</summary>
        private static Material _outlinedFontMaterial;

        // Defaults chosen to match EFT's menu palette, and overridden by whatever the harvested
        // donor turns out to use. Named rather than inlined so no call site hardcodes a colour.
        public static Color TextColor { get; private set; } = new(0.78f, 0.76f, 0.71f);
        public static Color DimTextColor { get; private set; } = new(0.78f, 0.76f, 0.71f, 0.55f);
        private static readonly Color DefaultAccentColor = new(0.78f, 0.65f, 0.35f);

        /// <summary>Selection, headers, highlights. From Settings when set; EFT's own bronze otherwise.</summary>
        public static Color AccentColor =>
            ModSettings.Ready ? ModSettings.ParseColor(ModSettings.ColorAccent, DefaultAccentColor) : DefaultAccentColor;
        /// <summary>Fully opaque. It was 97%, and 3% of the main menu's white headings showing
        /// through a black panel is exactly the ghost of "ESCAPE FROM TARKOV / CHARACTER / TRADING"
        /// that made the tree look busier than it was.</summary>
        public static Color ScreenColor { get; private set; } = new(0.055f, 0.055f, 0.05f, 1f);
        public static Color PanelColor { get; private set; } = new(1f, 1f, 1f, 0.05f);

        /// <summary>Outline for node text. Black at a modest width - enough to separate glyphs from
        /// whatever is behind them without the text starting to look bold.</summary>
        private static readonly Color OutlineColor = new(0f, 0f, 0f, 0.9f);
        private const float OutlineWidth = 0.2f;

        /// <summary>Called from MenuTaskBarPatch with the taskbar entry it clones from, before that
        /// clone's own label is overwritten - a real, always-available donor.</summary>
        public static void CaptureFrom(TMP_Text donorText, Image donorPanel)
        {
            if (_font == null && donorText != null && donorText.font != null)
            {
                // A re-capture (the previous font asset was unloaded) invalidates the outlined
                // copy made from its material, which would otherwise be handed out forever.
                if (_outlinedFontMaterial != null) UnityEngine.Object.Destroy(_outlinedFontMaterial);
                _outlinedFontMaterial = null;

                _font = donorText.font;
                // fontSharedMaterial, not fontMaterial: the latter's getter instances a material
                // on the donor - EFT's own taskbar label - as a side effect of reading it.
                _fontMaterial = donorText.fontSharedMaterial;

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

        /// <summary>
        /// The game's own tooltip on hover. HoverTooltipArea finds the tooltip itself in Awake
        /// (ItemUiContext.Instance.Tooltip), so all this adds is the component and the text - but
        /// only when that context exists, because Unity swallows an exception in Awake and the
        /// component would then throw on every hover instead. Null when unavailable; callers must
        /// cope, and the UI is complete without tooltips.
        /// </summary>
        public static HoverTooltipArea AddTooltip(GameObject target, string text)
        {
            if (target == null) return null;
            if (ModSettings.Ready && !ModSettings.Tooltips.Value) return null;

            try
            {
                if (ItemUiContext.Instance == null || ItemUiContext.Instance.Tooltip == null) return null;

                var area = target.AddComponent<HoverTooltipArea>();
                area._delay = 0.35f;
                area.SetMessageText(text ?? "", rawText: true);
                return area;
            }
            catch (Exception ex)
            {
                if (!_tooltipWarned)
                {
                    _tooltipWarned = true;
                    Plugin.LogSource?.LogInfo($"QuestTree: native tooltips unavailable ({ex.Message}).");
                }

                return null;
            }
        }

        private static bool _tooltipWarned;

        public static void Apply(TMP_Text text)
        {
            if (text == null) return;

            if (_font != null)
            {
                text.font = _font;
                // Shared, for the same reason ApplyOutlined gives: assigning fontMaterial would
                // instance one material per label, and there are hundreds.
                if (_fontMaterial != null) text.fontSharedMaterial = _fontMaterial;
            }

            // Only recolour text still sitting on the plain white default - callers that chose a
            // deliberate colour (status greens, warning reds) keep it.
            if (text.color == Color.white) text.color = TextColor;
        }

        /// <summary>
        /// Like <see cref="Apply"/>, but with a dark outline behind the glyphs. Used for text on the
        /// quest nodes, which sits over status-coloured borders and whatever the game is drawing
        /// behind the panel - an outline is what keeps pale text legible on a pale node and dark
        /// text legible on a dark one.
        ///
        /// Two things here are deliberate:
        ///
        /// The harvested material is COPIED, never modified. It belongs to the game - it came off a
        /// live taskbar label - so setting outline properties on it would put an outline on EFT's
        /// own UI text as a side effect.
        ///
        /// The copy is assigned as the SHARED material rather than through TMP_Text.outlineWidth or
        /// .fontMaterial, both of which instance a material per text object. With up to several
        /// hundred nodes on screen, each carrying three or four labels, that would be hundreds of
        /// materials and a broken batch; one shared material keeps them drawing together.
        /// </summary>
        public static void ApplyOutlined(TMP_Text text)
        {
            Apply(text);

            var material = GetOutlinedFontMaterial();
            if (material != null) text.fontSharedMaterial = material;
        }

        private static Material GetOutlinedFontMaterial()
        {
            if (_outlinedFontMaterial != null) return _outlinedFontMaterial;
            if (_fontMaterial == null) return null; // nothing harvested yet; plain text is the fallback

            try
            {
                var material = new Material(_fontMaterial);
                material.EnableKeyword(ShaderUtilities.Keyword_Outline);
                material.SetColor(ShaderUtilities.ID_OutlineColor, OutlineColor);
                material.SetFloat(ShaderUtilities.ID_OutlineWidth, OutlineWidth);

                _outlinedFontMaterial = material;
                return _outlinedFontMaterial;
            }
            catch (Exception ex)
            {
                // A font whose shader has no outline pass would land here. Plain text is a perfectly
                // acceptable outcome; an unreadable panel is not.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not build an outlined font material ({ex.Message}) - using plain text.");
                return null;
            }
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

            AddHoverFeedback(go, background);
            return rect;
        }

        /// <summary>
        /// Hover and press feedback the way the game's own buttons give it - a brighter face under
        /// the pointer, the hover sound, a dip while pressed - without cloning the game's button.
        /// Harvesting the sound and painting the states ourselves is the ingredient-not-furniture
        /// rule this class is built on (see CreateButton). The colour is restored to whatever it was
        /// when the pointer arrived, so a background the panel recolours (a selected view, Focus
        /// on) keeps its meaning.
        /// </summary>
        public static void AddHoverFeedback(GameObject target, Image background)
        {
            if (target == null || background == null) return;
            var feedback = target.AddComponent<ButtonHover>();
            feedback.Background = background;
        }

        private sealed class ButtonHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
        {
            public Image Background;
            private Color _resting;
            private bool _hovering;

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (Background == null) return;
                _resting = Background.color;
                _hovering = true;
                Background.color = Lift(_resting, 0.12f);
                if (!ModSettings.Ready || ModSettings.HoverSounds.Value) PlaySound(EUISoundType.ButtonOver);
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (Background == null || !_hovering) return;
                _hovering = false;
                Background.color = _resting;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (Background == null || !_hovering) return;
                Background.color = Lift(_resting, 0.22f);
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (Background == null || !_hovering) return;
                Background.color = Lift(_resting, 0.12f);
            }

            /// <summary>Brighter and more opaque: most of these faces are a 5% white wash, where a
            /// multiplied tint would be invisible.</summary>
            private static Color Lift(Color color, float amount) =>
                new(Mathf.Min(1f, color.r + amount), Mathf.Min(1f, color.g + amount),
                    Mathf.Min(1f, color.b + amount * 0.8f), Mathf.Min(1f, color.a + amount));
        }
    }
}
