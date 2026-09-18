using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI.Builds;
using Newtonsoft.Json;
using QuestTree.QuestGraph;

namespace QuestTree.UI
{
    /// <summary>
    /// Asks the game itself whether a build would be accepted at the trader.
    ///
    /// On SPT the whole hand-in gate is one client-side call: Inventory.IsWeaponFitsCondition
    /// (the server checks only that the weapon's template is on the quest's list). Every other
    /// verdict this mod gives about a build - the stat model, the server's verifier - is the mod
    /// checking its own arithmetic. This is the trader's decision, computed by the trader's code,
    /// on a Weapon assembled from the exact item list "Save as preset" would write. A one-off
    /// spike proved the path on 6 of 6 presets (ledger, 17 September 2026); this is the
    /// production version, keyed on the requirement rather than the preset name.
    ///
    /// Only for quests the profile HOLDS: the condition object comes from QuestController.Quests,
    /// which never contains a quest the player has not accepted. For the rest the answer is "not
    /// yet", said plainly, never a guess.
    /// </summary>
    internal static class GameGate
    {
        /// <summary>Set by the panel on every Show; the source of the condition objects.</summary>
        public static QuestController QuestController { get; set; }

        internal enum Kind { Accepted, Refused, Unknown }

        internal sealed class Verdict
        {
            public Kind Kind;
            public string Detail = "";
        }

        /// <summary>One answer per build, re-asked only when the server sends different items.
        /// Assembling a Weapon is not free and the panel repaints often.</summary>
        private static readonly Dictionary<string, (string ItemsJson, Verdict Verdict)> Cache = new();

        public static void Forget() => Cache.Clear();

        public static Verdict Check(string questId, WeaponBuildDto requirement, ProfileBuildDto build)
        {
            if (requirement == null || build == null) return Unknown("there is no build to check");

            var key = build.Key ?? "";
            var itemsJson = build.ItemsJson ?? "";

            if (Cache.TryGetValue(key, out var cached) && cached.ItemsJson == itemsJson) return cached.Verdict;

            var verdict = Compute(questId, requirement, build, itemsJson);
            Cache[key] = (itemsJson, verdict);
            return verdict;
        }

        private static Verdict Compute(string questId, WeaponBuildDto requirement, ProfileBuildDto build, string itemsJson)
        {
            try
            {
                var controller = QuestController;
                if (controller?.Quests == null) return Unknown("the quest list is not loaded");

                var quest = controller.Quests.FirstOrDefault(q => q?.Template?.Id == questId);
                if (quest == null) return Unknown("checkable once the quest is accepted");

                var condition = ConditionFor(quest, requirement.WeaponTemplate);
                if (condition == null) return Unknown("the quest states no build for this weapon");

                if (string.IsNullOrEmpty(itemsJson) || string.IsNullOrEmpty(build.Root))
                    return Unknown("the server sent no items for this build - both halves must be 1.14.0 or newer");

                var items = JsonConvert.DeserializeObject<JsonType.FlatItem[]>(itemsJson);
                if (items == null || items.Length == 0) return Unknown("the build's items did not deserialise");

                // The build screen's own assembly: WeaponBuild's constructor seats every child
                // through FlatItemsToTree. Never stored - this build exists to be asked about.
                var assembled = new WeaponBuild(new MongoID(build.Root), "QT check", new MongoID(build.Root), items);

                if (!(assembled.Item is Weapon weapon)) return Unknown("the items did not assemble into a weapon");

                // Before the gate, the way the hand-in window does it: a gun with a vital slot empty
                // is never listed as a candidate, so IsWeaponFitsCondition never sees it. Asking the
                // gate alone would call such a gun accepted.
                var vital = weapon.MissingVitalParts.Select(slot => slot.ID).ToList();
                if (vital.Count > 0)
                    return new Verdict { Kind = Kind.Refused, Detail = $"vital slot(s) empty: {string.Join(", ", vital)}" };

                var accepted = Inventory.IsWeaponFitsCondition(weapon, condition, displayLog: false);

                var verdict = accepted
                    ? new Verdict { Kind = Kind.Accepted, Detail = Summary(weapon) }
                    : new Verdict { Kind = Kind.Refused, Detail = WhyRefused(weapon, condition) };

                Plugin.LogSource?.LogInfo(
                    $"QuestTree gate: '{requirement.WeaponName}' for '{quest.Template.Name}' - " +
                    $"{(accepted ? "the game ACCEPTS" : "the game REFUSES")} ({verdict.Detail}).");

                return verdict;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree gate: could not check '{requirement.WeaponName}' - {ex.Message}");
                return Unknown("the check threw - see the BepInEx log");
            }
        }

        /// <summary>The condition for this weapon on this quest. A quest can state several builds
        /// (Old Friend's Request wants three); the weapon template is what tells them apart, which
        /// is why this keys on it rather than on a name.</summary>
        private static ConditionWeaponAssembly ConditionFor(Quest quest, string weaponTemplate)
        {
            var conditions = quest.Template?.Conditions;
            if (conditions == null || string.IsNullOrEmpty(weaponTemplate)) return null;
            if (!conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var list) || list == null) return null;

            foreach (var condition in list)
            {
                if (!(condition is ConditionWeaponAssembly assembly) || assembly.target == null) continue;
                if (assembly.target.Any(t => string.Equals(t, weaponTemplate, StringComparison.Ordinal))) return assembly;
            }

            return null;
        }

        /// <summary>The first test the game's gate fails, in its own numbers, using the game's own
        /// comparison so the reason cannot disagree with the verdict. When every threshold passes
        /// the refusal is a part: a vital slot empty, or a named part or category absent.</summary>
        private static string WhyRefused(Weapon weapon, ConditionWeaponAssembly condition)
        {
            var tests = new (string Name, float Current, WeaponAssemblyParameter Parameter)[]
            {
                ("ergonomics", weapon.ErgonomicsTotal, condition.ergonomics),
                ("recoil", weapon.RecoilTotal, condition.recoil),
                ("weight", weapon.TotalWeight, condition.weight),
                ("magazine capacity", weapon.GetMaxMagazineCount(), condition.magazineCapacity),
                ("effective distance", weapon.GetSightingRange(), condition.effectiveDistance),
                ("empty tactical slots", weapon.EmptyTacticalSlotCount, condition.emptyTacticalSlot),
                ("durability", weapon.Repairable.Durability, condition.durability),
                ("accuracy", weapon.TotalAccuracy, condition.baseAccuracy),
                ("muzzle velocity", weapon.TotalVelocity, condition.muzzleVelocity)
            };

            foreach (var (name, current, parameter) in tests)
            {
                if (parameter == null) continue;
                if (Inventory.Test(current, parameter, weapon, name, displayLog: false)) continue;

                return $"{name} {current:0.##} where the quest wants {Symbol(parameter.compareMethod)} {parameter.value:0.##}";
            }

            // The gate tests the assembled grid after the stats. Nine of the game's 32 build
            // conditions carry a size limit, so this is not a corner.
            var size = weapon.CalculateCellSize();
            if (condition.width != null && !Inventory.Test(size.X, condition.width, weapon, "width", displayLog: false))
                return $"width {size.X} cells where the quest wants {Symbol(condition.width.compareMethod)} {condition.width.value:0}";
            if (condition.height != null && !Inventory.Test(size.Y, condition.height, weapon, "height", displayLog: false))
                return $"height {size.Y} cells where the quest wants {Symbol(condition.height.compareMethod)} {condition.height.value:0}";

            // What is left of the gate: every named part on the gun, and one part from every named
            // category. Vital slots were tested before the gate was asked.
            return "a required part or category is not on the gun";
        }

        /// <summary>The numbers the gate read, with the two things the assembled check cannot know:
        /// the check gun is unloaded and at full durability, and the trader weighs the real one
        /// loaded and tests its real durability.</summary>
        private static string Summary(Weapon weapon) =>
            $"ergonomics {weapon.ErgonomicsTotal:0.#}, recoil {weapon.RecoilTotal:0.#}, weight {weapon.TotalWeight:0.##} kg unloaded, at full durability";

        private static string Symbol(ECompareMethod method) => method switch
        {
            ECompareMethod.MoreOrEqual => "≥",
            ECompareMethod.More => ">",
            ECompareMethod.LessOrEqual => "≤",
            ECompareMethod.Less => "<",
            ECompareMethod.Equal => "=",
            ECompareMethod.NotEqual => "≠",
            _ => "?"
        };

        private static Verdict Unknown(string detail) => new() { Kind = Kind.Unknown, Detail = detail };
    }
}
