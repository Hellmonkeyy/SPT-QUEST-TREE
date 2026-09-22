using System;
using System.Text.RegularExpressions;
using Comfort.Common;
using EFT.HandBook;
using EFT.InventoryLogic;
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

        /// <summary>The two semantic text colours every view uses in rich text - "mind this" amber
        /// and "this is broken" red - and the Kappa gold the badge, chip and toggles share. Named
        /// here because they were written out in twenty-four places, which is twenty-four places
        /// to change a shade.</summary>
        public const string WarningHex = "D9A61A";
        public const string ErrorHex = "C86464";

        /// <summary>"This is settled" green, for the third state the raid-readiness cue needs.
        ///
        /// The same green the tree already paints a handed-in quest (ModSettings.ColorCompleted's
        /// default), so the colour is one the player has already been taught to read as done rather
        /// than a fourth shade to learn.</summary>
        public const string SuccessHex = "3D854D";
        public static readonly Color KappaGold = new(0.85f, 0.65f, 0.1f);

        /// <summary>The Collector mark. The game's own "task completed" blue (#75B9DE, from
        /// NotesTask's colour map), so the second badge is a colour the player already reads as
        /// quest chrome and cannot be confused with Kappa gold at a glance.</summary>
        public static readonly Color CollectorBlue = new(0.459f, 0.725f, 0.871f);

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
                EnsureSymbolFallback();
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


        // ------------------------------------------------------------------ symbol fallback

        /// <summary>Whether the fallback has been attempted. Once only, success or not.</summary>
        private static bool _symbolFallbackTried;

        private static TMP_FontAsset _symbolFont;

        /// <summary>OS fonts that carry the geometric shapes, arrows and the padlock, best first.
        ///
        /// EFT's own font is a display face chosen for Latin text; it has no reason to carry U+25CE
        /// or U+1F512, and asking it to is how a glyph becomes a tofu box. These are the Windows
        /// faces that do. Tried in order, first one that loads wins.</summary>
        private static readonly string[] SymbolFontCandidates =
        {
            "Segoe UI Symbol",
            "Segoe UI Emoji",
            "Arial Unicode MS",
            "Segoe UI",
            "Arial"
        };

        /// <summary>Hangs a symbol font off the game's font as a fallback, so a glyph the game's
        /// face does not have is drawn from one that does instead of coming out as a box.
        ///
        /// Built at RUNTIME from an installed OS font rather than shipped as an AssetBundle. A
        /// bundle would have to be compiled against this exact Unity - 2022.3.43f1 - and would stop
        /// loading the moment the game moved, for a handful of glyphs. This asks Unity for a font
        /// it already has and lets TMP rasterise characters into a dynamic atlas on demand, so
        /// there is no asset to ship, nothing to version, and no new file in the plugin folder.
        ///
        /// Adding to the game font's own fallback table is a shared mutation, and a deliberately
        /// safe one: a fallback is only consulted for characters the primary face LACKS, so no text
        /// that renders correctly today can change. Everything is guarded - if any step fails the
        /// mod carries on with the glyphs it already had.</summary>
        private static void EnsureSymbolFallback()
        {
            if (_symbolFallbackTried || _font == null) return;
            _symbolFallbackTried = true;

            try
            {
                // What Unity can actually see. The first attempt asked for five faces by name and
                // reported only that all five failed, which does not distinguish "that font is not
                // installed" from "the name is right but TMP would not build an asset from it" -
                // and those want opposite fixes.
                string[] installed;

                try
                {
                    installed = Font.GetOSInstalledFontNames() ?? System.Array.Empty<string>();
                }
                catch (System.Exception ex)
                {
                    installed = System.Array.Empty<string>();
                    Plugin.LogSource?.LogInfo($"QuestTree: cannot enumerate OS fonts ({ex.Message}).");
                }

                foreach (var candidate in Candidates(installed))
                {
                    var face = Font.CreateDynamicFontFromOSFont(candidate, 32);

                    if (face == null)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: '{candidate}' did not load as a Unity font.");
                        continue;
                    }

                    TMP_FontAsset asset;

                    try
                    {
                        asset = TMP_FontAsset.CreateFontAsset(face);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: '{candidate}' loaded but TMP refused it ({ex.Message}).");
                        continue;
                    }

                    if (asset == null)
                    {
                        Plugin.LogSource?.LogDebug($"QuestTree: '{candidate}' loaded but TMP returned no asset.");
                        continue;
                    }

                    asset.name = $"QuestTree symbols ({candidate})";

                    // Dynamic, so characters are rasterised into the atlas the first time they are
                    // asked for rather than all of Unicode up front.
                    asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;

                    _font.fallbackFontAssetTable ??= new System.Collections.Generic.List<TMP_FontAsset>();
                    if (!_font.fallbackFontAssetTable.Contains(asset))
                        _font.fallbackFontAssetTable.Add(asset);

                    _symbolFont = asset;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: symbol fallback is '{candidate}' - " +
                        $"lock={HasGlyph(Lock)} diamond={HasGlyph('\u25c7')} bullseye={HasGlyph('\u25ce')}.");
                    return;
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: no symbol font could be loaded from {installed.Length} installed face(s) - " +
                    "staying on the glyphs the game's own font has. Set the log to Debug for the per-font reason.");
            }
            catch (System.Exception ex)
            {
                // Never fatal. The glyphs already in use are ones this codebase draws elsewhere, so
                // the worst case is the status marks staying exactly as they were.
                Plugin.LogSource?.LogWarning($"QuestTree: symbol fallback unavailable ({ex.Message}).");
            }
        }

        /// <summary>The preferred faces first, then anything installed whose name suggests it covers
        /// symbols, then everything else.
        ///
        /// CreateDynamicFontFromOSFont matches on the exact family name, so a hardcoded list is only
        /// as good as the guess behind it - "Segoe UI Symbol" is not present on every Windows, and
        /// on a machine where none of five names match, a name-only list has nothing left to try.
        /// Walking what is actually installed does.</summary>
        private static System.Collections.Generic.IEnumerable<string> Candidates(string[] installed)
        {
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var name in SymbolFontCandidates)
                if (seen.Add(name))
                    yield return name;

            // Names that tend to mean broad Unicode coverage rather than a display face.
            foreach (var name in installed)
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                if (name.IndexOf("Symbol", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Unicode", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("DejaVu", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Noto", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return name;
                }
            }

            // Last resort: anything at all. A plain text face still carries the geometric shapes
            // this actually needs, even if it has no padlock.
            foreach (var name in installed)
                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                    yield return name;
        }

        /// <summary>The padlock, as a string - it is outside the basic plane, so it is a surrogate
        /// pair rather than a char and cannot be written as one.</summary>
        public const int Lock = 0x1F512;

        /// <summary>Whether anything in the chain - the game's font or our fallback - can actually
        /// draw this character.
        ///
        /// Asked before a glyph is used rather than assumed, because a missing one does not fail
        /// loudly: TMP draws a box, or nothing, and the box keeps its layout either way. This is the
        /// difference between choosing a mark and hoping for one.
        ///
        /// THE GAME'S FONT IS PROBED READ-ONLY, and that distinction is the whole of this method.
        ///
        /// The first version asked both fonts with TryAddCharacters, because it takes a string and
        /// so can be given a character outside the basic plane, and because on a dynamic atlas it
        /// tests and rasterises in one call. Convenient, and wrong: TryAddCharacters is a WRITE. On
        /// EFT's font - a static, pre-baked atlas shared by the entire game UI - asking it to add a
        /// character it does not have is not a question, it is an attempt to rebuild someone else's
        /// atlas, and it cost every bold title in the tree.
        ///
        /// So: HasCharacter for the game's font, which only looks; TryAddCharacters only for the
        /// symbol font, which is ours, dynamic, and exists precisely to be written to. A character
        /// outside the basic plane cannot be expressed as a char at all, so for those the game font
        /// is not consulted - which is correct anyway, since a display face has no astral glyphs.</summary>
        public static bool HasGlyph(int codePoint)
        {
            try
            {
                var text = char.ConvertFromUtf32(codePoint);

                // Printable ASCII is drawable by anything TMP could be using, including its own
                // default face before this class has harvested a font - and the ASCII candidates are
                // the last resort PickGlyph's chain promises. Without this, a session where
                // CaptureFrom found no font answered false to EVERY character, so every status mark
                // in the tree, the legend and the lists resolved to "" and stayed that way for the
                // session (Glyphs caches once): the one channel that is not colour, gone silently.
                // Proved by a reflection harness against the built DLL, where no font is harvested
                // and "+" came back undrawable.
                if (codePoint >= 0x20 && codePoint < 0x7F) return true;

                // Ours. Dynamic. Writing to it is the point.
                if (_symbolFont != null &&
                    _symbolFont.TryAddCharacters(text, out string missing) &&
                    string.IsNullOrEmpty(missing))
                {
                    return true;
                }

                // The game's. Look, never touch.
                return text.Length == 1 && _font != null && _font.HasCharacter(text[0]);
            }
            catch
            {
                return false;
            }
        }

        public static bool HasGlyph(char character) => HasGlyph((int)character);

        /// <summary>The padlock if it can be drawn, the given fallback otherwise.</summary>
        public static string LockGlyph(string fallback) =>
            HasGlyph(Lock) ? char.ConvertFromUtf32(Lock) : fallback;

        /// <summary>The first of these the font chain can actually draw, or "" if none of them.
        ///
        /// Every decorative character now goes through this rather than being written into a string
        /// and hoped for. A character the font lacks does not fail loudly - TMP draws a box, keeps
        /// its layout, and the result reads as a bug in the mod rather than a gap in a font, which
        /// is exactly what happened to the arrow in a blocked quest's reason line.
        ///
        /// Pass a plain-text last resort as the final candidate where a mark is load-bearing, and
        /// "" where the line reads fine without one.</summary>
        public static string PickGlyph(params string[] candidates)
        {
            if (candidates == null) return "";

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) return "";
                if (HasGlyph(char.ConvertToUtf32(candidate, 0))) return candidate;
            }

            return "";
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

        /// <summary>
        /// Recolours a button's RESTING face - the colour it returns to when the pointer leaves.
        ///
        /// Assigning <c>background.color</c> directly is not enough for a button that carries hover
        /// feedback, and the case is not exotic: the pointer is inside the button at the moment a
        /// click recolours it. <see cref="ButtonHover"/> samples the resting colour on pointer ENTER,
        /// so the pointer exit that follows the click put the pre-click colour back - the view button
        /// you just pressed, and the Focus and Chains toggles, dropped their lit state the moment the
        /// mouse moved off them, and nothing repainted it until the next tab change.
        /// </summary>
        public static void SetRestingColor(Image background, Color color)
        {
            if (background == null) return;

            var feedback = background.GetComponent<ButtonHover>();
            if (feedback != null) feedback.SetResting(color);
            else background.color = color;
        }

        private static bool _tooltipWarned;

        /// <summary>
        /// Opens the game's own item inspect window on an item template - the same window the
        /// handbook opens, with the same stats, image and right-click menu.
        ///
        /// It works for every item, not only ones the player owns, and nothing is fabricated to
        /// achieve that: the handbook holds one real Item per template, built when the profile
        /// loads, and this asks it for that item exactly as the handbook screen does. A template
        /// with no handbook entry - a modded item outside the handbook tree - simply declines.
        ///
        /// Two things must not be "fixed" here:
        ///
        /// The handbook's item is SHARED. It is never disposed or modified; Inspect is safe because
        /// the window makes and disposes a child context of its own.
        ///
        /// An item the player has never examined shows as a question mark with no description. That
        /// is correct - the handbook does the same - and marking it examined to make the window look
        /// better would write to the profile and call the server, for a cosmetic gain, from a mod
        /// that reads.
        /// </summary>
        /// <returns>Whether a window opened, so a caller can fall back to its tooltip.</returns>
        public static bool InspectItem(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return false;

            try
            {
                var context = ItemUiContext.Instance;
                if (context == null || !Singleton<Handbook>.Instantiated) return false;

                // Null-safe: HandbookNodes' indexer is a TryGetValue behind a [CanBeNull].
                var item = Singleton<Handbook>.Instance[templateId]?.Data?.Item;
                if (item == null) return false;

                var itemContext = new DefaultItemContext(item, EItemViewType.Handbook);
                context.Inspect(itemContext, new HandbookContextInteractions(itemContext, context));

                // Here rather than at each call site, so every item row in the mod is covered by
                // construction and a new one cannot forget.
                TrackerAccess.KeepOpenThroughInspect();
                return true;
            }
            catch (Exception ex)
            {
                // Same shape as AddTooltip above, and for the same reason: the inspect window wires
                // itself to the item controller unguarded, so a menu caught mid-transition throws.
                if (!_inspectWarned)
                {
                    _inspectWarned = true;
                    Plugin.LogSource?.LogInfo($"QuestTree: item inspect unavailable ({ex.Message}).");
                }

                return false;
            }
        }

        private static bool _inspectWarned;

        /// <summary>The width <paramref name="text"/> takes in <paramref name="label"/>'s font at
        /// its size, measured on that label after Apply has installed the font.
        ///
        /// This has been got wrong twice, both times shipped, and both times the symptom was the
        /// same: chips, tabs and the toolbar legend a sliver wide with their text spilling across
        /// each other. First a hidden, never-activated measuring object; then a live label asked
        /// with infinite bounds. So it is now built to be self-correcting rather than merely
        /// correct.
        ///
        /// Two rules, and neither may be dropped without a play test to replace it:
        ///
        /// The bounds passed are (0, 0), not (Infinity, Infinity). Zero means "no constraint" to
        /// TMP and is the shape AuxLayout.AddWrapped has always measured with successfully; the
        /// infinite one is what shipped in 1.8.4 and returned near-zero widths.
        ///
        /// The answer is only believed when it lands within a sane band around the character
        /// estimate. A wrong-but-plausible width has never crushed a layout - the estimate alone is
        /// what shipped for a year - whereas a width of four pixels for "Available" destroys the
        /// row. So a third TMP quirk, whatever it turns out to be, degrades to a slightly loose
        /// layout instead of an unreadable one.</summary>
        public static float MeasureWidth(TMP_Text label, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            var estimate = EstimateWidth(text, label != null ? label.fontSize : 12f);
            if (label == null) return estimate;

            try
            {
                // Zero, not Infinity - see the note above.
                var width = label.GetPreferredValues(text, 0f, 0f).x;
                if (float.IsNaN(width) || float.IsInfinity(width)) return estimate;

                // The band. Generous on the high side because a wide font legitimately outruns a
                // 0.56em-per-character guess; tight on the low side because that is the failure
                // that has actually shipped, twice.
                if (width >= estimate * 0.5f && width <= estimate * 3f) return width;

                ReportRejectedMeasurement("width", text, width, estimate);
                return estimate;
            }
            catch (Exception)
            {
                // Measuring is a nicety; the estimate is what shipped for a year.
                return estimate;
            }
        }

        /// <summary>The height <paramref name="text"/> needs when wrapped to
        /// <paramref name="width"/>.
        ///
        /// Banded like MeasureWidth, and for the same reason now proven twice: TMP under-reports
        /// here as well. The map sidebar measured each line with GetPreferredValues(text, width,
        /// 0f).y - the idiom this codebase once called "the one call that has always worked" - and a
        /// two-line objective came back one line high, so the rows drew on top of each other.
        ///
        /// The asymmetry decides the direction: overshooting costs a few pixels of gap,
        /// undershooting costs legibility. So this takes the LARGER of measured and estimated rather
        /// than trusting either.</summary>
        public static float MeasureHeight(TMP_Text label, string text, float width, float lineHeight)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            if (lineHeight <= 0f) lineHeight = LineHeightOf(label);

            var estimate = EstimateHeight(text, label != null ? label.fontSize : 12f, width, lineHeight);
            if (label == null) return estimate;

            try
            {
                var height = label.GetPreferredValues(text, width, 0f).y;
                if (float.IsNaN(height) || float.IsInfinity(height)) return estimate;

                // Not a band in both directions, unlike the width case: a measurement that is too
                // LARGE here costs a gap, which nobody has ever reported, while one that is too
                // small overlaps two lines of text, which is the reported bug. So the estimate is a
                // floor, not an alternative.
                if (height >= estimate) return height;

                ReportRejectedMeasurement("height", text, height, estimate);
                return estimate;
            }
            catch (Exception)
            {
                return estimate;
            }
        }

        /// <summary>The line box for a label, as a plain multiple of its font size.
        ///
        /// Deliberately not read off the font's FaceInfo, which lives in an assembly this project
        /// does not reference and would not earn its place here anyway: the only consumer is a
        /// character-count estimate whose job is to notice a measurement that is out by a WHOLE
        /// line. A few percent of error in the line box cannot change that answer, and the estimate
        /// is only ever used as a floor.</summary>
        public static float LineHeightOf(TMP_Text label) =>
            (label != null ? label.fontSize : 12f) * 1.2f;

        /// <summary>Visible characters divided by what fits on a line, rounded up, times the line
        /// box. Crude, and that is the point - it only has to be close enough to catch a measurement
        /// that is out by a whole line.</summary>
        public static float EstimateHeight(string text, float fontSize, float width, float lineHeight)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            if (lineHeight <= 0f) lineHeight = fontSize * 1.2f;

            var perLine = Mathf.Max(1f, width / Mathf.Max(1f, fontSize * 0.56f));
            var visible = text.IndexOf('<') >= 0 ? Regex.Replace(text, "<[^>]*>", "") : text;
            var lines = Mathf.Max(1f, Mathf.Ceil(visible.Length / perLine));

            return lines * lineHeight;
        }

        private static bool _widthReported;
        private static bool _heightReported;

        /// <summary>Said once a session per dimension, at Info, when TMP's answer is thrown away.
        /// Without this the only way to learn what TMP is really returning is another
        /// install-and-play cycle, and two have already been spent guessing.
        ///
        /// One flag per dimension, not one shared: whichever rejected first would otherwise suppress
        /// the other for the rest of the session, and for the height fix that line is the only
        /// verification instrument there is.</summary>
        private static void ReportRejectedMeasurement(string dimension, string text, float measured, float estimate)
        {
            if (dimension == "height")
            {
                if (_heightReported) return;
                _heightReported = true;
            }
            else
            {
                if (_widthReported) return;
                _widthReported = true;
            }

            var sample = text.Length > 40 ? text.Substring(0, 40) : text;
            Plugin.LogSource?.LogInfo(
                $"QuestTree: TMP measured the {dimension} of \"{sample}\" at {measured:0.#}px against " +
                $"an estimate of {estimate:0.#}px - outside the sane band, so it is on the estimate. " +
                "Layout is correct but slightly loose; this is the fallback working.");
        }

        /// <summary>The character-count estimate: glyphs only, since a tab label carries a colour
        /// tag pair around its suffix that would count as two dozen characters.</summary>
        public static float EstimateWidth(string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var visible = text.IndexOf('<') >= 0 ? Regex.Replace(text, "<[^>]*>", "") : text;
            return visible.Length * fontSize * 0.56f;
        }

        /// <summary>A quest, trader, item or objective name as literal text inside rich-text
        /// markup. TMP parses tags in every string it is handed, so a modded name containing a
        /// real tag - "&lt;b&gt;" is all it takes - used to swallow the rest of its line. Wrapped only
        /// when there is a "&lt;" to worry about; the tag pair is invisible.</summary>
        public static string Safe(string text) => RichText.Safe(text);

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

            /// <summary>Moves the colour this returns to on exit - see
            /// <see cref="GameStyle.SetRestingColor"/>. While the pointer is inside, the lifted face
            /// moves with it, so the button does not flatten under the cursor and then change again
            /// when the pointer leaves.</summary>
            public void SetResting(Color color)
            {
                _resting = color;
                if (Background == null) return;

                Background.color = _hovering ? Lift(color, 0.12f) : color;
            }

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
