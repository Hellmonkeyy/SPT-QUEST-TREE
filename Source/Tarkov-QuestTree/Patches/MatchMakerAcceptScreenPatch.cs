using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
using QuestTree.QuestGraph;
using QuestTree.UI;
using SPT.Reflection.Patching;
using UnityEngine;

namespace QuestTree.Patches
{
    /// <summary>
    /// Puts a Quest Tracker button on the raid ready-up screen, above the CURRENT LOCATION row.
    ///
    /// This is the one screen where the mod's normal way in is gone. Every screen controller carries
    /// a MenuChatBarVisibility, defaulting to Enabled; MatchMakerAcceptScreen and
    /// MatchmakerFinalCountdown are the only two that override it to Disabled, which reaches
    /// PreloaderUI.SetMenuTaskBarVisibility(false) and does a hard SetActive(false) on the entire
    /// taskbar - our button with it. Every other pre-raid screen, side selection and location
    /// selection included, keeps the bar and needs nothing from this file.
    ///
    /// It is also the screen where the tracker has the most to say: the map is chosen, so the panel
    /// opens knowing exactly which one you are about to load.
    ///
    /// The button is parented to the screen's own root and placed above the location row rather than
    /// inserted into it. That row's parent may be under a layout group whose arrangement is the
    /// game's business, and a mod's button appearing inside it would reflow the game's own text;
    /// positioning against the row without joining it cannot.
    /// </summary>
    internal class MatchMakerAcceptScreenPatch : ModulePatch
    {
        private const string ContainerName = "QuestTreeRaidPanel";
        private const string ButtonName = "QuestTreeRaidButton";
        private const string ButtonLabel = "QUEST TRACKER";

        /// <summary>The button's MINIMUM size. It grows to fit its label, which the readiness cue
        /// makes much longer than "QUEST TRACKER" - the three-state text runs to roughly 250px, and
        /// a fixed 150 clipped exactly the state the cue exists to deliver.</summary>
        private static readonly Vector2 ButtonSize = new(150f, 26f);

        /// <summary>How far above the location row the button sits, and how far in from the left of
        /// the screen when there is no row to measure against.</summary>
        private const float GapAboveRow = 12f;
        private static readonly Vector2 FallbackPosition = new(24f, 140f);

        /// <summary>The three-argument Show, by parameter types rather than by name: the screen
        /// carries two overloads called Show, and the other one - the controller entry point - runs
        /// BEFORE the location name and conditions are filled in. This one is where they are set,
        /// which is what the button positions itself against.</summary>
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(
                typeof(MatchMakerAcceptScreen),
                nameof(MatchMakerAcceptScreen.Show),
                new[] { typeof(IEftSession), typeof(RaidSettings), typeof(RaidSettings) });

        [PatchPostfix]
        private static void Postfix(MatchMakerAcceptScreen __instance)
        {
            // Inside the game's own Show. Anything thrown here would land in the matchmaker rather
            // than in this mod, and a mod that cannot add a button must not stop a raid starting.
            try
            {
                AddButton(__instance);
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"QuestTree: could not add the pre-raid button ({ex.Message}).");
            }
        }

        private static bool _warned;
        private static bool _barWarned;

        private static void AddButton(MatchMakerAcceptScreen screen)
        {
            if (screen == null) return;

            var root = screen.transform as RectTransform;
            if (root == null) return;

            // Find-or-create, never an early return.
            //
            // Show runs on every entry to this screen and the screen object is reused, so something
            // has to stop the buttons stacking up one per raid - but returning here meant that on
            // every re-entry the position was never recomputed and, once the readiness cue lands,
            // the verdict was never refetched. Backing out and coming back would show the verdict
            // from before you moved the item, looking entirely correct while being stale.
            var container = root.Find(ContainerName) as RectTransform ?? CreateContainer(root);

            var button = container.Find(ButtonName) as RectTransform;
            if (button == null)
            {
                button = GameStyle.CreateButton(container, ButtonLabel, TrackerAccess.Toggle);
                button.name = ButtonName;
                button.anchorMin = button.anchorMax = Vector2.zero;
                button.pivot = Vector2.zero;
                button.anchoredPosition = Vector2.zero;

                GameStyle.AddTooltip(
                    button.gameObject, "Opens the quest tracker on the map you are about to load");
            }

            SizeToLabel(button);

            // Placed now so it is never invisible, then placed again one frame later.
            //
            // GetWorldCorners at Show-postfix time is not trustworthy: Unity rebuilds layout at end
            // of frame, so a rect driven by a layout group still holds last frame's values - or, on
            // a screen's first show, its authored prefab values. Whether these rects are under a
            // layout group is prefab data nothing can read, so the answer is to measure twice.
            Place(root, screen, container);

            // Hosted on the SCREEN, the way MenuTaskBarPatch hosts its own deferred placement. The
            // coroutine then dies with the screen, which is the correct behaviour - Show refetches
            // on the next entry anyway - and nothing static has to be cleaned up. StartCoroutine
            // throws on a disabled behaviour, so it stays inside the caller's try.
            if (screen.isActiveAndEnabled)
            {
                screen.StartCoroutine(PlaceNextFrame(root, screen, container));
                screen.StartCoroutine(ApplyVerdict(screen, container, ++_generation));
            }
        }

        /// <summary>Bumped on every Show. The fetch runs off the main thread and the screen is
        /// re-entrant, so an answer that arrives after a newer request started must be dropped
        /// rather than painted over the newer one. RequestHandler.GetJsonAsync takes no
        /// cancellation token, so a generation compared on apply is the only workable answer.</summary>
        private static int _generation;

        /// <summary>Fetches the raid check off the main thread and paints the result on it.
        ///
        /// Off-thread because every fetch in this mod is otherwise synchronous behind a 15-second
        /// cap, and the ready-up screen is the worst place in the game to stall - a raid countdown
        /// is running. The button draws neutral immediately and repaints when the answer lands,
        /// which is the same shape the deferred placement above already uses, so the two cooperate
        /// rather than fighting for a frame.
        ///
        /// Two rules this must not break, both learned here: nothing may touch a RectTransform, a
        /// TMP_Text or a GameObject except on the main thread - including READING one - and a throw
        /// in the background half must never escape into Unity's thread pool unlogged.</summary>
        private static IEnumerator ApplyVerdict(
            MatchMakerAcceptScreen screen, RectTransform container, int generation)
        {
            var locationKey = TrackerAccess.CurrentRaidLocation();

            // A scav run is judged against gear the player is not taking, so the cue says nothing
            // rather than something wrong. Scav runs carry no quest objectives, so nothing useful is
            // lost, and neutral is honest where green would be a lie.
            if (string.IsNullOrEmpty(locationKey) || TrackerAccess.IsCurrentRaidScav()) yield break;

            var task = Task.Run(() =>
            {
                try
                {
                    return QuestDataClient.GetRaidCheck();
                }
                catch (Exception ex)
                {
                    // Never unlogged, and never rethrown into the pool.
                    Plugin.LogSource?.LogInfo($"QuestTree: the raid check could not be fetched ({ex.Message}).");
                    return null;
                }
            });

            var deadline = Time.realtimeSinceStartup + FetchTimeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

            if (!task.IsCompleted)
            {
                // Without this a hung server leaves the cue neutral forever with nothing in the log -
                // the one state the verify pass is told to read the log to explain.
                if (!_timeoutLogged)
                {
                    _timeoutLogged = true;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the raid check did not answer within {FetchTimeoutSeconds}s - " +
                        "the pre-raid button stays neutral.");
                }

                yield break;
            }

            // Stale answer, or the screen went away while we waited. A destroyed Unity object
            // compares equal to null rather than throwing, so these are real checks.
            if (generation != _generation || container == null) yield break;

            RaidCheckView.Verdict verdict = null;

            try
            {
                verdict = RaidCheckView.Fold(
                    task.Result, locationKey, null,
                    !ModSettings.Ready || ModSettings.CountUnacceptedQuests.Value);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not fold the raid check ({ex.Message}).");
                yield break;
            }

            Paint(container, verdict);
        }

        private static bool _timeoutLogged;
        private const float FetchTimeoutSeconds = 15f;

        /// <summary>Paints the verdict onto the button's label.
        ///
        /// The LABEL, never the background: CreateButton's background is a 5% white wash and
        /// ButtonHover caches it at pointer-enter and restores it on exit, so a background
        /// recoloured here is reverted the moment the pointer leaves. The label is plain white and
        /// untouched by hover feedback.</summary>
        private static void Paint(RectTransform container, RaidCheckView.Verdict verdict)
        {
            var button = container.Find(ButtonName) as RectTransform;
            if (button == null) return;

            var label = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (label == null) return;

            // Neutral: unchanged from what it already says. Never green merely because the answer
            // did not arrive.
            if (verdict?.State == null) return;

            label.text = LabelFor(verdict);
            SizeToLabel(button);
        }

        private static string LabelFor(RaidCheckView.Verdict verdict)
        {
            if (verdict.Empty) return ButtonLabel;

            if (verdict.State == RaidCheckView.Have.OnYou)
                return $"<color=#{GameStyle.SuccessHex}>{ButtonLabel} - READY</color>";

            // Red wins the button when both apply, and both counts appear: you can pack a stash
            // item before loading in, and you cannot conjure one you do not own.
            if (verdict.State == RaidCheckView.Have.Missing)
            {
                var tail = verdict.ToPackCount > 0 ? $" · {verdict.ToPackCount} TO PACK" : "";
                return $"<color=#{GameStyle.ErrorHex}>{ButtonLabel} - {verdict.MissingCount} MISSING{tail}</color>";
            }

            return $"<color=#{GameStyle.WarningHex}>{ButtonLabel} - {verdict.ToPackCount} TO PACK</color>";
        }

        /// <summary>One container for the button and, once the readiness cue lands, its rows.
        ///
        /// Named and reused, because root.Find only ever found the button: rows parented to the root
        /// would stack up one set per raid, which is the exact bug the idempotency guard exists to
        /// stop, recreated one level down.</summary>
        private static RectTransform CreateContainer(RectTransform root)
        {
            var go = new GameObject(ContainerName, typeof(RectTransform));
            var rect = (RectTransform)go.transform;

            rect.SetParent(root, worldPositionStays: false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.sizeDelta = ButtonSize;

            return rect;
        }

        /// <summary>Grows the button to its label, never below the minimum.
        ///
        /// Measured through GameStyle, which floors TMP's answer at a character estimate - an
        /// under-measured button clips its own text, which is the failure this is here to prevent.</summary>
        private static void SizeToLabel(RectTransform button)
        {
            var label = button.GetComponentInChildren<TMPro.TMP_Text>();
            if (label == null)
            {
                button.sizeDelta = ButtonSize;
                return;
            }

            var width = GameStyle.MeasureWidth(label, label.text) + 24f;
            button.sizeDelta = new Vector2(Mathf.Max(ButtonSize.x, width), ButtonSize.y);
        }

        /// <summary>Places again after a layout pass has run. The try stays INSIDE the loop and never
        /// spans the yield: a throw after a yield escapes the postfix's catch entirely and surfaces
        /// as an unhandled Unity error on the matchmaker screen.</summary>
        private static IEnumerator PlaceNextFrame(
            RectTransform root, MatchMakerAcceptScreen screen, RectTransform container)
        {
            yield return null;

            // The screen can be torn down between frames, and a destroyed Unity object compares
            // equal to null rather than throwing on use - so this is a real check, not a formality.
            if (root == null || container == null || screen == null) yield break;

            try
            {
                Place(root, screen, container);
            }
            catch (Exception ex)
            {
                if (_warned) yield break;
                _warned = true;
                Plugin.LogSource?.LogWarning($"QuestTree: could not place the pre-raid button ({ex.Message}).");
            }
        }

        private static void Place(RectTransform root, MatchMakerAcceptScreen screen, RectTransform container)
        {
            container.anchoredPosition = PositionAbove(root, LocationBarOf(screen), screen);
        }

        /// <summary>The bar the button sits above: the lowest common ancestor of the location name
        /// and the conditions panel, which is by construction the row containing both, whatever the
        /// prefab calls it.
        ///
        /// Derived at runtime rather than assumed. Which object is "the panel" is Unity scene data
        /// that decompiling cannot show, so naming a parent would be a guess - and the first version
        /// anchored to _locationName alone, whose rect top sits INSIDE the panel below the CURRENT
        /// LOCATION caption and whose left edge runs wider than its visible text. That is precisely
        /// what put the button over the caption and off the left side.
        ///
        /// Returns null when the answer is not plausible, which the caller reads as "measure the two
        /// rects directly instead".</summary>
        private static RectTransform LocationBarOf(MatchMakerAcceptScreen screen)
        {
            var name = screen._locationName != null ? screen._locationName.transform : null;
            var conditions = screen._conditions != null ? screen._conditions.transform : null;

            if (name == null) return null;
            if (conditions == null) return Plausible(name.parent as RectTransform, screen);

            var ancestors = new HashSet<Transform>();
            for (var t = name; t != null; t = t.parent) ancestors.Add(t);

            for (var t = conditions; t != null; t = t.parent)
                if (ancestors.Contains(t)) return Plausible(t as RectTransform, screen);

            return Plausible(name.parent as RectTransform, screen);
        }

        /// <summary>Rejects a derived bar that cannot be the location row.
        ///
        /// The lowest common ancestor of two SIBLING panels is the screen root, and feeding that to
        /// PositionAbove gives y = root.height + 12 - twelve pixels above the top edge, on a button
        /// pivoted at the root's bottom-left. The button vanishes, nothing throws, and the log says
        /// nothing. That is an unbounded failure traded for a bounded one, in a method whose whole
        /// argument is that guessing at prefab structure is how this went wrong the first time.
        ///
        /// Same shape as GameStyle.MeasureWidth's sanity band: believe a derived answer only inside a
        /// plausible range, say so once when rejecting it, and degrade to something usable.</summary>
        private static RectTransform Plausible(RectTransform bar, MatchMakerAcceptScreen screen)
        {
            if (bar == null) return null;

            var root = screen.transform as RectTransform;
            if (root == null) return bar;

            var reason =
                bar == root ? "it is the screen root"
                : bar.GetComponent<Canvas>() != null ? "it carries a Canvas"
                : bar.rect.height >= root.rect.height * 0.9f ? "it is as tall as the screen"
                : null;

            if (reason == null) return bar;

            if (!_barWarned)
            {
                _barWarned = true;
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the pre-raid location bar could not be identified ({reason}), so the " +
                    "button is placed against the location text directly. Position may be slightly off.");
            }

            return null;
        }

        /// <summary>Just above the location bar, aligned to its left edge, clamped into the screen.
        ///
        /// Returns an anchoredPosition for a container anchored and pivoted at the root's
        /// bottom-left. With no trustworthy bar it falls back to the union of the two rects actually
        /// in hand, which needs no ancestor at all, and then to a fixed corner.</summary>
        private static Vector2 PositionAbove(
            RectTransform root, RectTransform bar, MatchMakerAcceptScreen screen)
        {
            var target = TopLeftOf(root, bar) ?? UnionTopLeft(root, screen);
            if (target == null) return Clamp(root, FallbackPosition);

            return Clamp(root, new Vector2(target.Value.x, target.Value.y + GapAboveRow));
        }

        /// <summary>A rect's top-left corner, in the coordinates an anchor of (0,0) measures from.
        /// Through world space, so neither the rect's own anchors nor its parent's layout have to be
        /// assumed.</summary>
        private static Vector2? TopLeftOf(RectTransform root, RectTransform rect)
        {
            if (rect == null) return null;

            // A rect a layout pass has not reached yet reports nothing usable. QuestGraphView guards
            // the same way before trusting a viewport width.
            if (rect.rect.width <= 1f && rect.rect.height <= 1f) return null;

            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);

            // corners[1] is the top-left.
            var local = root.InverseTransformPoint(corners[1]);
            return new Vector2(local.x - root.rect.xMin, local.y - root.rect.yMin);
        }

        /// <summary>The top-left of the two rects we have typed references to, taken together. No
        /// ancestor involved, so it survives whatever the prefab does with parenting.</summary>
        private static Vector2? UnionTopLeft(RectTransform root, MatchMakerAcceptScreen screen)
        {
            var name = TopLeftOf(root, screen._locationName != null ? screen._locationName.rectTransform : null);
            var conditions = TopLeftOf(root, screen._conditions != null ? screen._conditions.transform as RectTransform : null);

            if (name == null) return conditions;
            if (conditions == null) return name;

            // Leftmost and highest of the two - the corner of the box containing both.
            return new Vector2(
                Mathf.Min(name.Value.x, conditions.Value.x),
                Mathf.Max(name.Value.y, conditions.Value.y));
        }

        /// <summary>Keeps the result inside the screen. Two lines, and they turn "the derivation was
        /// nonsense" into a slightly odd position rather than an invisible button - which is the
        /// difference between a bug someone can report and one nobody can see.</summary>
        private static Vector2 Clamp(RectTransform root, Vector2 position)
        {
            var maxX = Mathf.Max(0f, root.rect.width - ButtonSize.x);
            var maxY = Mathf.Max(0f, root.rect.height - ButtonSize.y);

            return new Vector2(Mathf.Clamp(position.x, 0f, maxX), Mathf.Clamp(position.y, 0f, maxY));
        }
    }
}
