using System;
using QuestTree.QuestGraph;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>
    /// The Settings tab. Every control here writes straight to the matching
    /// <see cref="ModSettings"/> entry, which is a BepInEx config value - so a change persists to
    /// BepInEx/config, is visible in the F12 menu, and raises ModSettings.Changed for the panel to
    /// re-render from. There is deliberately no separate copy of the state held here.
    /// </summary>
    internal static class SettingsView
    {
        /// <summary>Builds the tab into <paramref name="parent"/> and returns its height.
        /// <paramref name="onKappaListReloaded"/> is invoked after the curated Kappa list is
        /// re-read from disk.</summary>
        public static float Build(RectTransform parent, Action onKappaListReloaded)
        {
            var y = AuxLayout.Padding;

            if (!ModSettings.Ready)
            {
                AuxLayout.AddHeading(parent, ref y, "Settings");
                AuxLayout.AddText(parent, ref y,
                    "<color=#C86464>Settings failed to initialise - check the BepInEx log.</color>", 24f);
                return y + AuxLayout.Padding;
            }

            AuxLayout.AddHeading(parent, ref y, "Filters");

            AuxLayout.AddToggle(parent, ref y,
                "Hide unobtainable quests",
                "Other faction, seasonal event, and other-edition quests.",
                ModSettings.HideUnobtainable.Value,
                value => ModSettings.HideUnobtainable.Value = value);

            AuxLayout.AddToggle(parent, ref y,
                "Hide completed quests",
                "Show only what is still left to do.",
                ModSettings.HideCompleted.Value,
                value => ModSettings.HideCompleted.Value = value);

            AuxLayout.AddToggle(parent, ref y,
                "Hide quests with no trader",
                "Usually scripted or leftover entries rather than anything you can pick up.",
                ModSettings.HideTraderless.Value,
                value => ModSettings.HideTraderless.Value = value);

            AuxLayout.AddSpacer(ref y, 14f);
            AuxLayout.AddHeading(parent, ref y, "Display");

            AuxLayout.AddToggle(parent, ref y,
                "Draw prerequisite lines",
                "Turning this off is a noticeable speed-up on very dense trader chains.",
                ModSettings.DrawEdges.Value,
                value => ModSettings.DrawEdges.Value = value);

            AuxLayout.AddSpacer(ref y, 14f);
            AuxLayout.AddHeading(parent, ref y, "Performance");

            AuxLayout.AddStepper(parent, ref y, "Max visible quests",
                ModSettings.MaxVisibleNodes.Value, 100,
                value => ModSettings.MaxVisibleNodes.Value = Mathf.Clamp(value, 100, 2000));
            AuxLayout.AddText(parent, ref y,
                "<color=#FFFFFF80>Ceiling on how many quest boxes exist at once. Only reachable when " +
                "zoomed right out.</color>", 32f, 11);

            AuxLayout.AddSpacer(ref y, 14f);
            AuxLayout.AddHeading(parent, ref y, "Kappa list");

            AuxLayout.AddText(parent, ref y,
                KappaQuests.Count == 0
                    ? "<color=#FFFFFF80>kappa-quests.json is empty. Add quest names to it to enable the " +
                      "curated Kappa section.</color>"
                    : $"<color=#FFFFFF80>{KappaQuests.Count} quest names loaded from kappa-quests.json.</color>",
                32f, 12);

            AuxLayout.AddButton(parent, ref y, "Reload kappa-quests.json", () =>
            {
                KappaQuests.Reload();
                onKappaListReloaded();
            });

            return y + AuxLayout.Padding;
        }
    }
}
