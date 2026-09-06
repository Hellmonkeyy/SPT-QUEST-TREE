using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.UI;
using HarmonyLib;
using QuestTree.UI;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.Patches
{
    /// <summary>
    /// Adds a "Quest Tracker" button to the persistent bottom taskbar (EFT.UI.MenuTaskBar) - the
    /// row with MAIN MENU / HIDEOUT / etc. - by cloning an existing entry, the technique used by
    /// every mod that adds a taskbar button in this game version.
    ///
    /// Two things about cloning a taskbar entry are load-bearing, and both were learned the hard
    /// way (see the comments on Heal and FindVanillaTaskBarEntry):
    ///
    /// 1. The clone SOURCE must be a vanilla entry, not another mod's button. An earlier version
    ///    matched any ToggleGroup whose name contained "raid" and ended up cloning
    ///    maschine-RaidReviewOverlay's own clone, which made this mod's button depend on another
    ///    mod's internal construction timing. The vanilla Hideout entry is now taken straight out
    ///    of MenuTaskBar._toggleButtons, and Raid Review's button is used only for POSITIONING.
    /// 2. A freshly instantiated taskbar entry renders its label invisible, so assigning .text is
    ///    not enough on its own - hence Heal(). That is not a guess: the installed
    ///    maschine-RaidReviewOverlay.dll hit the same thing and carries the same workaround.
    /// </summary>
    internal class MenuTaskBarAwakePatch : ModulePatch
    {
        private const string ButtonName = "QuestTree";
        private const string ButtonLabel = "QUEST TRACKER";
        private const string TabsParentName = "Tabs";

        private const float PollIntervalSeconds = 0.5f;
        private const int MaxPollAttempts = 10; // 5 seconds total

        /// <summary>Re-heal points, in seconds after the button is built. The toggle's Animator
        /// re-drives the label's alpha from its own default ("not selected") state after we have
        /// already set it, so a single pass at build time does not survive - these are the two
        /// delays maschine-RaidReviewOverlay settled on for the same problem.</summary>
        private const float FirstHealDelay = 0.2f;
        private const float SecondHealDelay = 1.5f;

        /// <summary>Taskbar chrome gold. Keeps the cloned Hideout icon's shape but makes it read as
        /// a distinct entry rather than a duplicate.</summary>
        private static readonly Color IconTint = new(0.76f, 0.68f, 0.43f);

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(MenuTaskBar), "Awake");

        [PatchPostfix]
        private static void Postfix(MenuTaskBar __instance)
        {
            // Runs inside the game's own Awake. StartCoroutine throws on a disabled behaviour, and
            // anything thrown here lands in MenuTaskBar, not in this mod - so it is caught here.
            try
            {
                Plugin.LogSource?.LogInfo("QuestTree: MenuTaskBar.Awake fired, scheduling Quest Tracker button...");
                __instance.StartCoroutine(AddQuestTreeButtonDeferred(__instance));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: could not schedule the Quest Tracker button: {ex}");
            }
        }

        private static IEnumerator AddQuestTreeButtonDeferred(MenuTaskBar menuTaskBar)
        {
            // _toggleButtons is populated during Awake; let the rest of the frame finish before
            // reading it.
            yield return null;

            var state = TryBuildButton(menuTaskBar);
            if (state == null) yield break;

            menuTaskBar.StartCoroutine(HealLater(state));
            menuTaskBar.StartCoroutine(PlaceNextToRaidReviewLater(state));
        }

        /// <summary>Re-applies the label fix at the two points where the Animator has been observed
        /// to have undone it. On the second pass, if the label is STILL measuring invisible, the
        /// Animator is switched off outright - by then it is the only thing that could still be
        /// driving the alpha down, and this button has no animated states worth keeping.</summary>
        private static IEnumerator HealLater(ButtonState state)
        {
            yield return new WaitForSeconds(FirstHealDelay);
            TryHeal(state, disableAnimatorIfStillHidden: false);

            yield return new WaitForSeconds(SecondHealDelay - FirstHealDelay);
            TryHeal(state, disableAnimatorIfStillHidden: true);

            Plugin.LogSource?.LogInfo(
                $"QuestTree diag: post-heal labelInvisible={(state.Label == null || IsInvisible(state.Label))} " +
                $"text='{state.Label?.text}' rectWidth={state.Label?.rectTransform.rect.width}");
        }

        /// <summary>Position only - the button is already built and visible by this point. Polls
        /// rather than assuming a fixed delay, because when Raid Review inserts its own button is
        /// not something this mod can know.</summary>
        private static IEnumerator PlaceNextToRaidReviewLater(ButtonState state)
        {
            for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                if (state.Clone == null) yield break;
                if (TryPlaceNextToRaidReview(state)) yield break;
                yield return new WaitForSeconds(PollIntervalSeconds);
            }

            Plugin.LogSource?.LogInfo(
                "QuestTree: no Raid Review taskbar button found - leaving the Quest Tracker button next to Hideout.");
        }

        private static ButtonState TryBuildButton(MenuTaskBar menuTaskBar)
        {
            try
            {
                if (UnityEngine.Object.FindObjectsOfType<ToggleGroup>().Any(g => g.name == ButtonName))
                {
                    Plugin.LogSource?.LogInfo("QuestTree: button already present, skipping.");
                    return null;
                }

                var source = FindVanillaTaskBarEntry(menuTaskBar);
                if (source == null)
                {
                    Plugin.LogSource?.LogError(
                        "QuestTree: no vanilla Hideout taskbar entry found to clone - taskbar layout changed.");
                    return null;
                }

                // Captured off the still-pristine source, before anything is cloned or recolored,
                // so every TMP_Text/Image this mod creates elsewhere can borrow the real game font
                // and panel sprite. See UI/GameStyle.cs.
                GameStyle.CaptureFrom(
                    source.GetComponentInChildren<TMP_Text>(includeInactive: true),
                    source.GetComponentInChildren<Image>(includeInactive: true));

                var clone = UnityEngine.Object.Instantiate(source.gameObject, source.parent);
                clone.name = ButtonName;
                clone.transform.SetSiblingIndex(source.GetSiblingIndex() + 1);

                var newInformation = clone.transform.Find("NewInformation");
                if (newInformation != null)
                    UnityEngine.Object.Destroy(newInformation.gameObject); // Hideout's "new items" badge

                UnlockClone(clone);

                // Resolved through LocalizedText first: that component sits on the GameObject which
                // actually carries the visible label, so it identifies the right TMP_Text even when
                // the clone contains more than one. It is then destroyed, otherwise it rewrites our
                // text back to its own locale key.
                var localized = clone.GetComponentInChildren<LocalizedText>(includeInactive: true);
                var label = localized != null
                    ? localized.GetComponent<TMP_Text>()
                    : clone.GetComponentInChildren<TMP_Text>(includeInactive: true);

                foreach (var extra in clone.GetComponentsInChildren<LocalizedText>(includeInactive: true))
                {
                    extra.enabled = false;
                    UnityEngine.Object.Destroy(extra);
                }

                var toggle = clone.GetComponentInChildren<AnimatedToggle>(includeInactive: true);

                var state = new ButtonState { Clone = clone, Label = label, Toggle = toggle };
                Heal(state, disableAnimatorIfStillHidden: false);
                TintIcon(clone);

                var tooltip = clone.GetComponentInChildren<HoverTooltipArea>(includeInactive: true);
                if (tooltip != null)
                {
                    tooltip.SetUnlockStatus(true);
                    tooltip.SetMessageText("Opens the quest tracker", true);
                }

                var panel = CreatePanel(menuTaskBar);

                if (toggle != null)
                {
                    toggle.ToggleSilent(false);
                    toggle.onValueChanged.AddListener(isOn =>
                    {
                        if (!isOn) return;

                        try
                        {
                            if (panel.gameObject.activeSelf) panel.HideGameObject();
                            else ShowPanel(panel);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogSource?.LogError($"QuestTree: the Quest Tracker button failed: {ex}");
                        }
                        finally
                        {
                            // Momentary trigger, not a real screen-select toggle like the vanilla
                            // taskbar buttons - there is no persistent "Quest Tracker screen" for
                            // MenuTaskBar to track. ToggleSilent rather than isOn, so resetting the
                            // button cannot re-enter this handler. In a finally because if the
                            // reset is skipped the toggle stays latched on, and every later click
                            // stops at the isOn check above - a dead button with no log line.
                            toggle.ToggleSilent(false);
                        }
                    });
                }
                else
                {
                    Plugin.LogSource?.LogError("QuestTree: cloned taskbar entry has no AnimatedToggle - button will be inert.");
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree diag: clonedFrom='{source.name}' labelFound={label != null} " +
                    $"parent='{clone.transform.parent?.name}' siblingIndex={clone.transform.GetSiblingIndex()} " +
                    $"activeInHierarchy={clone.activeInHierarchy}");

                return state;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogError($"QuestTree: failed to add the Quest Tracker button: {ex}");
                return null;
            }
        }

        /// <summary>The vanilla Hideout entry, taken from MenuTaskBar's own public
        /// Dictionary&lt;EMenuType, AnimatedToggle&gt; rather than by searching for a GameObject by
        /// name - it is a real typed field in 4.1.5, so this cannot silently miss the way a lookup
        /// by string name can. Walks up from the toggle to whichever ancestor is a direct child of
        /// "Tabs", which is the object representing one whole taskbar entry.</summary>
        private static Transform FindVanillaTaskBarEntry(MenuTaskBar menuTaskBar)
        {
            if (menuTaskBar?._toggleButtons == null) return null;
            if (!menuTaskBar._toggleButtons.TryGetValue(EMenuType.Hideout, out var hideoutToggle)) return null;
            if (hideoutToggle == null) return null;

            var entry = hideoutToggle.transform;
            while (entry.parent != null && entry.parent.name != TabsParentName)
                entry = entry.parent;

            return entry.parent != null ? entry : hideoutToggle.transform;
        }

        private static bool TryPlaceNextToRaidReview(ButtonState state)
        {
            var ourParent = state.Clone.transform.parent;
            if (ourParent == null) return false;

            var raidReview = UnityEngine.Object.FindObjectsOfType<ToggleGroup>()
                .FirstOrDefault(g => g.name != ButtonName &&
                                     g.name.IndexOf("raid", StringComparison.OrdinalIgnoreCase) >= 0);
            if (raidReview == null) return false;

            // Walk up to whichever ancestor is our own sibling, so SetSiblingIndex is comparing
            // like with like however deeply that mod nests its toggle.
            var entry = raidReview.transform;
            while (entry.parent != null && entry.parent != ourParent)
                entry = entry.parent;

            if (entry.parent != ourParent) return false;

            state.Clone.transform.SetSiblingIndex(entry.GetSiblingIndex() + 1);
            Plugin.LogSource?.LogInfo(
                $"QuestTree: placed next to '{raidReview.name}' at siblingIndex={state.Clone.transform.GetSiblingIndex()}.");
            return true;
        }

        private static void TryHeal(ButtonState state, bool disableAnimatorIfStillHidden)
        {
            try
            {
                if (state.Clone == null) return;
                Heal(state, disableAnimatorIfStillHidden);
                TintIcon(state.Clone);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: label heal pass failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Undoes every way a cloned taskbar label comes out invisible. Assigning .text alone is
        /// not enough and never was: the label's GameObject (or an ancestor) can clone in
        /// deactivated, the component can clone in disabled, and the alpha can be zero on either
        /// the Graphic colour OR the CanvasRenderer - the latter being invisible to a colour check,
        /// which is exactly what made this look like the text was never written at all.
        /// </summary>
        private static void Heal(ButtonState state, bool disableAnimatorIfStillHidden)
        {
            var label = state.Label;
            if (label == null) return;

            if (!state.Clone.activeSelf) state.Clone.SetActive(true);

            for (var t = label.transform; t != null && t != state.Clone.transform; t = t.parent)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            }

            UnlockClone(state.Clone);

            label.enabled = true;

            var color = label.color;
            if (color.a < 0.9f)
            {
                color.a = 1f;
                label.color = color;
            }

            if (label.canvasRenderer.GetAlpha() < 0.9f) label.canvasRenderer.SetAlpha(1f);

            if (label.text != ButtonLabel) label.text = ButtonLabel;
            label.SetAllDirty();

            if (!disableAnimatorIfStillHidden || !IsInvisible(label)) return;

            var animator = state.Toggle != null ? state.Toggle.GetComponent<Animator>() : null;
            if (animator == null || !animator.enabled) return;

            animator.enabled = false;
            Plugin.LogSource?.LogInfo(
                "QuestTree: label still invisible after the second heal - disabled the toggle's Animator.");

            // The Animator was holding the alpha down, so re-apply now that it cannot.
            Heal(state, disableAnimatorIfStillHidden: false);
        }

        /// <summary>A CanvasGroup on a vanilla entry carries the "this menu is unavailable" dimming,
        /// and a clone inherits whatever state that group happened to be in.</summary>
        private static void UnlockClone(GameObject clone)
        {
            foreach (var group in clone.GetComponentsInChildren<CanvasGroup>(includeInactive: true))
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
        }

        private static bool IsInvisible(TMP_Text label)
        {
            if (label == null) return true;
            if (!label.gameObject.activeInHierarchy) return true;
            if (!label.enabled) return true;
            if (label.color.a < 0.9f) return true;
            if (label.canvasRenderer.GetAlpha() < 0.9f) return true;
            if (label.rectTransform.rect.width < 5f) return true;
            return label.transform.lossyScale.x < 0.01f;
        }

        /// <summary>Recolors the cloned entry's icon so it does not read as a second Hideout button.
        /// The icon Image is found by name because that is the only way it is identifiable on a
        /// runtime clone - there is no typed reference to it.</summary>
        private static void TintIcon(GameObject clone)
        {
            foreach (var image in clone.GetComponentsInChildren<Image>(includeInactive: true))
            {
                if (image == null) continue;
                if (image.gameObject.name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var tint = IconTint;
                tint.a = image.color.a;
                image.color = tint;
                return;
            }
        }

        private static QuestTreePanel CreatePanel(MenuTaskBar menuTaskBar)
        {
            // Reachable from anywhere in the main menu (not just a trader dialog), so this needs
            // to render above whatever screen happens to be showing - parented under the outermost
            // Canvas ancestor of the taskbar itself, found live rather than guessed at via any
            // particular singleton.
            var rootCanvas = menuTaskBar.GetComponentInParent<Canvas>()?.rootCanvas;
            var parent = rootCanvas != null ? rootCanvas.transform : menuTaskBar.transform;

            var panelGo = new GameObject("QuestTreePanel", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)panelGo.transform;
            rect.SetParent(parent, worldPositionStays: false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            panelGo.GetComponent<Image>().color = GameStyle.ScreenColor;
            panelGo.SetActive(false);

            return panelGo.AddComponent<QuestTreePanel>();
        }

        private static void ShowPanel(QuestTreePanel panel)
        {
            var app = ClientAppUtils.GetMainApp();
            var mainMenu = Compat.Get<MainMenuShowOperation>(app);

            if (mainMenu?.QuestController == null || mainMenu.iEftSession == null)
            {
                Plugin.LogSource?.LogWarning("QuestTree: session/quest data not available yet.");
                return;
            }

            panel.Show(mainMenu.QuestController, mainMenu.iEftSession);
            panel.transform.SetAsLastSibling();
        }

        /// <summary>The pieces of the built button that the deferred heal/placement passes need to
        /// keep hold of, so neither has to re-find anything by name after the fact.</summary>
        private sealed class ButtonState
        {
            public GameObject Clone;
            public TMP_Text Label;
            public AnimatedToggle Toggle;
        }
    }
}
