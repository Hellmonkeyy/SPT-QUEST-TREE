using System;
using System.Reflection;
using EFT;
using EFT.UI.Matchmaker;
using HarmonyLib;
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
        private const string ButtonName = "QuestTreeRaidButton";
        private const string ButtonLabel = "QUEST TRACKER";

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

        private static void AddButton(MatchMakerAcceptScreen screen)
        {
            if (screen == null) return;

            var root = screen.transform as RectTransform;
            if (root == null) return;

            // Show runs on every entry to this screen, and the screen object is reused - so without
            // this the buttons stack up one per raid. Scoped to this screen's own children rather
            // than a scene-wide search, which would find the taskbar's button too.
            if (root.Find(ButtonName) != null) return;

            var button = GameStyle.CreateButton(root, ButtonLabel, TrackerAccess.Toggle);
            button.name = ButtonName;
            button.anchorMin = button.anchorMax = Vector2.zero;
            button.pivot = Vector2.zero;
            button.sizeDelta = ButtonSize;
            button.anchoredPosition = PositionAbove(root, screen._locationName?.rectTransform);

            GameStyle.AddTooltip(button.gameObject, "Opens the quest tracker on the map you are about to load");
        }

        /// <summary>Just above the location row, aligned to its left edge - measured through world
        /// space, since the row's own anchors and its parent's layout are not this mod's to assume.
        /// Falls back to a fixed corner position if the row is missing, which is the case a game
        /// update would produce: a button in a slightly odd place still works.</summary>
        private static Vector2 PositionAbove(RectTransform root, RectTransform row)
        {
            if (row == null) return FallbackPosition;

            var corners = new Vector3[4];
            row.GetWorldCorners(corners);

            // corners[1] is the top-left. Into the root's local space, then expressed from the
            // root's bottom-left corner, which is where an anchor of (0, 0) measures from.
            var local = root.InverseTransformPoint(corners[1]);

            return new Vector2(
                local.x - root.rect.xMin,
                local.y - root.rect.yMin + GapAboveRow);
        }
    }
}
