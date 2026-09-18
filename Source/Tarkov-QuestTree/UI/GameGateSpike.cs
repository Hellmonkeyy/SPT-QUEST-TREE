using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI.Builds;

namespace QuestTree.UI
{
    /// <summary>SPIKE, not a feature. Answers one question and is then either promoted or deleted:
    /// can this plugin ask the GAME whether a build satisfies a Gunsmith condition, instead of asking
    /// our own reimplementation of the game?
    ///
    /// The question matters because on SPT <c>Inventory.IsWeaponFitsCondition</c> is the entire
    /// hand-in gate - the server checks nothing but the weapon's template id - and it is a public
    /// static method in Assembly-CSharp. If it answers correctly for a weapon we assembled off-profile,
    /// every threshold, seating and conflict check in the server-side verifier is an approximation of
    /// a call we could simply make.
    ///
    /// The cheapest possible experiment: every preset this mod has saved is already an assembled
    /// <c>Weapon</c>, because <c>WeaponBuild</c>'s JSON constructor runs <c>FlatItemsToTree</c>, which
    /// seats children into slots via <c>AddWithoutRestrictions</c>. So no fake stash, no
    /// <c>ItemManipulator.Move</c> - take each QT preset out of the storage, find its quest's condition,
    /// and ask the gate. With <c>displayLog: true</c> the game writes each failing test to Player.log
    /// itself, which is the evidence.
    ///
    /// Runs once per profile, on panel open, and never throws into the panel. Gated by a constant
    /// rather than a setting so it adds nothing to the F12 page; flip it off, or delete the file, once
    /// it has answered.</summary>
    internal static class GameGateSpike
    {
        // readonly, not const: a const true makes the guard below "unreachable code" and the build is
        // kept warning-clean. Same effect, no compiler opinion.
        private static readonly bool Enabled = true;

        private static string _ranForProfile;

        internal static void Run(QuestController questController, IEftSession session)
        {
            if (!Enabled) return;

            try
            {
                RunInner(questController, session);
            }
            catch (Exception ex)
            {
                // A spike that takes the panel down with it answers nothing.
                Plugin.LogSource?.LogWarning($"QuestTree gate spike: threw - {ex}");
            }
        }

        private static void RunInner(QuestController questController, IEftSession session)
        {
            var profile = session?.Profile?.Id;
            if (string.IsNullOrEmpty(profile) || profile == _ranForProfile) return;
            _ranForProfile = profile;

            var builds = session.WeaponBuildsStorage?.WeaponBuilds;
            if (builds == null)
            {
                Plugin.LogSource?.LogInfo("QuestTree gate spike: no WeaponBuildsStorage on this session.");
                return;
            }

            // Every WeaponAssembly condition the client knows about, with the quest it belongs to.
            var conditions = new List<(string QuestName, string QuestId, ConditionWeaponAssembly Condition)>();

            if (questController?.Quests != null)
                foreach (var quest in questController.Quests)
                {
                    var template = quest?.Template;
                    if (template?.Conditions == null) continue;
                    if (!template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var list)) continue;

                    foreach (var condition in list)
                        if (condition is ConditionWeaponAssembly assembly)
                            conditions.Add((template.Name ?? template.Id, template.Id, assembly));
                }

            var ours = builds.Values
                .Where(b => b?.HandbookName != null && b.HandbookName.StartsWith("QT:", StringComparison.Ordinal))
                .ToList();

            var report = new StringBuilder();
            report.AppendLine(
                $"QuestTree gate spike: {conditions.Count} WeaponAssembly condition(s) on the client, " +
                $"{ours.Count} QT preset(s) in the build storage, {builds.Count} preset(s) in total.");

            var evaluated = 0;

            foreach (var build in ours)
            {
                if (!(build.Item is Weapon weapon))
                {
                    report.AppendLine($"  '{build.HandbookName}': Item is {build.Item?.GetType().Name ?? "null"}, not a Weapon - skipped.");
                    continue;
                }

                var weaponTemplate = weapon.TemplateId.ToString();

                // Same weapon template as the condition targets, preferring the quest whose name the
                // preset carries - a quest can have several conditions and a weapon can serve several
                // quests.
                var candidates = conditions
                    .Where(c => c.Condition.target != null && c.Condition.target.Length > 0
                                && string.Equals(c.Condition.target[0], weaponTemplate, StringComparison.Ordinal))
                    .OrderByDescending(c => !string.IsNullOrEmpty(c.QuestName)
                                            && build.HandbookName.IndexOf(c.QuestName, StringComparison.Ordinal) >= 0)
                    .ToList();

                if (candidates.Count == 0)
                {
                    report.AppendLine($"  '{build.HandbookName}': no condition targets weapon {weaponTemplate} - skipped.");
                    continue;
                }

                var (questName, questId, condition) = candidates[0];

                // Everything the gate reads, captured BEFORE asking it, so the numbers stand beside the
                // verdict whichever way it goes.
                var size = weapon.CalculateCellSize();
                var vital = weapon.MissingVitalParts.ToList();

                var fits = Inventory.IsWeaponFitsCondition(weapon, condition, displayLog: true);
                evaluated++;

                report.AppendLine(
                    $"  '{build.HandbookName}' vs '{questName}' ({questId}): " +
                    (fits ? "GAME ACCEPTS" : "GAME REFUSES") +
                    $" | ergo {weapon.ErgonomicsTotal:0.##} recoil {weapon.RecoilTotal:0.##} weight {weapon.TotalWeight:0.###} " +
                    $"mag {weapon.GetMaxMagazineCount()} sight {weapon.GetSightingRange():0.#} " +
                    $"emptyTactical {weapon.EmptyTacticalSlotCount} size {size.X}x{size.Y} " +
                    $"missingVital {vital.Count} mods {weapon.Mods.Count()}" +
                    (candidates.Count > 1 ? $" (of {candidates.Count} candidate conditions)" : ""));
            }

            report.Append($"QuestTree gate spike: evaluated {evaluated} preset(s). ")
                  .Append("Each REFUSES has the game's own reason in Player.log (displayLog).");

            Plugin.LogSource?.LogInfo(report.ToString());
        }
    }
}
