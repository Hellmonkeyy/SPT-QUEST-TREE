using System;
using System.Collections.Generic;
using System.Linq;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace QuestTreeServer
{
    /// <summary>
    /// The facts every payload builder needs about quests, and the one place each is derived.
    ///
    /// It exists because four builders had grown private copies of the same questions - what state
    /// is this quest in, which of its conditions are done, which map does it happen on, how many
    /// does it want - and they cannot see each other. A second copy of "what does accepted mean" is
    /// how two screens come to disagree about whether you are ready for a raid.
    ///
    /// A singleton rather than a static class: ResolveLockReason has to ask SPT's own QuestHelper
    /// rather than re-implementing the rules, and that is injected.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class QuestFacts(
        TemplateTable templateTable,
        LocationTable locationTable,
        QuestHelper questHelper)
    {
        /// <summary>The literal a quest uses when it declines to name a map. Kept apart from the
        /// "unknown location" case on purpose: the client tests for this exact string in three
        /// places and is right to, whereas an unrecognised id means nothing to anyone.</summary>
        public const string AnyLocation = "any";

        private Dictionary<string, string>? _locationIdToKey;
        private Dictionary<string, string>? _locationKeyToId;
        private Dictionary<string, List<string>>? _keysByCanonical;

        // ---- profile state -------------------------------------------------------------------

        /// <summary>One pass over the profile's quest list, so nothing below has to scan it again.
        ///
        /// The builders this replaces called FirstOrDefault per quest, which is 835 x 439
        /// comparisons per request on the reference profile - fine for the Collector checklist,
        /// which asks about one quest, and not fine for a route fired from the ready-up screen.
        /// </summary>
        public Progress IndexProgress(PmcData? profile)
        {
            var entries = new Dictionary<MongoId, QuestStatus>();
            var succeeded = new HashSet<string>();

            foreach (var entry in profile?.Quests ?? Enumerable.Empty<QuestStatus>())
            {
                if (entry == null) continue;

                entries[entry.QId] = entry;
                if (entry.Status == QuestStatusEnum.Success) succeeded.Add(entry.QId.ToString());
            }

            return new Progress(entries, succeeded);
        }

        /// <summary>The quest's state as the client should read it.
        ///
        /// An entry in the profile is authoritative. A quest with NO entry has not been touched,
        /// which is not the same as locked, and reporting it as Locked is what made the setting
        /// "count quests you have not accepted" mean nothing: the reference profile is level 51
        /// with 343 completions and carries just three entries at AvailableForStart, because the
        /// other ~390 available quests have no entry at all.
        /// </summary>
        public string StatusOf(Progress progress, Quest? quest, PmcData? profile)
        {
            if (quest == null) return QuestStatusEnum.Locked.ToString();

            if (progress.Entries.TryGetValue(quest.Id, out var entry) && entry != null)
                return entry.Status.ToString();

            // No profile means nothing can be evaluated, so the honest answer is the conservative
            // one. This is the out-of-game request, where the Kappa checklist is still worth
            // serving with nothing owned.
            if (profile == null) return QuestStatusEnum.Locked.ToString();

            return ResolveLockReason(quest, profile, progress.Succeeded) == null
                ? QuestStatusEnum.AvailableForStart.ToString()
                : QuestStatusEnum.Locked.ToString();
        }

        /// <summary>Condition ids the player has already satisfied. Empty for a quest with no entry,
        /// which is the normal case for most of a wipe.</summary>
        public HashSet<string> CompletedConditionsOf(Progress progress, MongoId questId)
        {
            if (!progress.Entries.TryGetValue(questId, out var entry) || entry?.CompletedConditions == null)
                return new HashSet<string>();

            return new HashSet<string>(entry.CompletedConditions);
        }

        /// <summary>Null when nothing is blocking the quest.
        ///
        /// The lock rules are not re-implemented here. SPT already decides who may see what in
        /// QuestHelper.GetClientQuests and every check it uses is public, so this calls the same
        /// methods and reports the reason instead of dropping the quest.
        ///
        /// Checked in the order the player would act on them: things that can never change for this
        /// character first (faction, edition, event), then the ones they can move (level, loyalty,
        /// standing), then prerequisites. Only the FIRST blocker is reported - a list of five
        /// reasons is not more useful than the one thing to go and do.
        /// </summary>
        public LockReasonDto? ResolveLockReason(Quest quest, PmcData profile, HashSet<string> succeeded)
        {
            if (questHelper.QuestIsForOtherSide(profile.Info?.Side, quest.Id))
                return new LockReasonDto { Kind = "OtherFaction", Detail = "For the other faction" };

            var gameVersion = profile.Info?.GameVersion;

            if (questHelper.QuestIsProfileBlacklisted(gameVersion, quest.Id) ||
                !questHelper.QuestIsProfileWhitelisted(gameVersion, quest.Id))
            {
                return new LockReasonDto { Kind = "Edition", Detail = "Not available in your game edition" };
            }

            if (!questHelper.ShowEventQuestToPlayer(quest.Id))
                return new LockReasonDto { Kind = "Event", Detail = "Seasonal event quest, not currently active" };

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return null;

            var playerLevel = profile.Info?.Level ?? 0;

            foreach (var condition in conditions.GetLevelConditions())
            {
                if (questHelper.DoesPlayerLevelFulfilCondition(playerLevel, condition)) continue;

                var required = Numbers.ToCount(condition.Value);
                return new LockReasonDto
                {
                    Kind = "Level",
                    Detail = $"Requires level {required}",
                    RequiredValue = required,
                    CurrentValue = playerLevel
                };
            }

            foreach (var condition in conditions.GetLoyaltyConditions())
            {
                if (questHelper.TraderLoyaltyLevelRequirementCheck(condition, profile)) continue;

                return new LockReasonDto
                {
                    Kind = "Loyalty",
                    Detail = $"Requires loyalty level {Numbers.ToCount(condition.Value)}",
                    RequiredValue = Numbers.ToCount(condition.Value),
                    TraderId = ResolveConditionTrader(condition)
                };
            }

            foreach (var condition in conditions.GetStandingConditions())
            {
                if (questHelper.TraderStandingRequirementCheck(condition, profile)) continue;

                return new LockReasonDto
                {
                    Kind = "Standing",
                    Detail = "Requires higher trader standing",
                    TraderId = ResolveConditionTrader(condition)
                };
            }

            // Prerequisites last: they are the common case, and the client can already name the
            // quest from its own graph, so this only has to say that one is outstanding.
            var outstanding = conditions.GetQuestConditions()
                .SelectMany(c => QuestPayloadBuilder.TargetIds(c.Target))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Where(id => !succeeded.Contains(id))
                .ToList();

            if (outstanding.Count > 0)
            {
                return new LockReasonDto
                {
                    Kind = "Prerequisite",
                    Detail = outstanding.Count == 1
                        ? "Requires an earlier quest"
                        : $"Requires {outstanding.Count} earlier quests",
                    BlockingQuestIds = outstanding
                };
            }

            return null;
        }

        private static string ResolveConditionTrader(QuestCondition condition) =>
            condition.Target != null && !condition.Target.IsList
                ? condition.Target.Item ?? ""
                : condition.Target?.List?.FirstOrDefault() ?? "";

        // ---- the quest table -----------------------------------------------------------------

        /// <summary>Every quest in the database, null-safe. One place knows it is a nullable
        /// dictionary.</summary>
        public IEnumerable<Quest> Quests() =>
            templateTable.Quests?.Values.Where(q => q != null)! ?? Enumerable.Empty<Quest>();

        /// <summary>How many of a thing a condition asks for, clamped to at least one.
        ///
        /// Two idioms existed: ObjectiveDto.Count floors at 0 and the Kappa checklist clamps to 1.
        /// All 245 vanilla carry conditions carry a value of 1 or more (235 of them exactly 1), so
        /// the two agree on every one of them and this is purely defensive - it decides only what a
        /// modded condition with a missing or zero value means, and "the quest wants none of it" is
        /// not a useful reading.
        /// </summary>
        public static int RequiredCount(QuestCondition condition) =>
            Math.Max(1, Numbers.ToCount(condition?.Value, 1));

        // ---- locations -----------------------------------------------------------------------

        /// <summary>Location MongoId -> internal name ("bigmap", "Labyrinth").
        ///
        /// Quest.Location is the id, not the internal name, so the two never match without this.
        /// The locations table is the authority - every location carries both forms, IdField being
        /// the MongoId a quest cites and Id the internal name - so it is read directly rather than
        /// by inverting questConfig.LocationIdMap. That config was the obvious source and is the
        /// wrong one: it has no Labyrinth entry, so the Labyrinth quests resolved to a bare MongoId
        /// and matched nothing.
        /// </summary>
        public IReadOnlyDictionary<string, string> LocationIdToKey => BuildLocationLookups().idToKey;

        /// <summary>Internal name -> location MongoId, for the marker builder's per-location
        /// buckets.</summary>
        public IReadOnlyDictionary<string, string> LocationIdsByKey() => BuildLocationLookups().keyToId;

        /// <summary>Every real location's internal name - QuestDto.LocationKey's keyspace, which is
        /// NOT the canonical one: factory4_night and Sandbox_high are separate entries here and
        /// fold away under ZoneStore.Canonical.</summary>
        public IReadOnlyList<string> AllLocationKeys() => BuildLocationLookups().keyToId.Keys.ToList();

        /// <summary>The display name for a map, by its internal name. The locale table keys these
        /// by location ID rather than by internal name, which is the step that makes this worth
        /// having in one place.</summary>
        public string LocationNameOf(string key, Dictionary<string, string> locale)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            if (!BuildLocationLookups().keyToId.TryGetValue(key, out var id)) return key;

            return locale != null && locale.TryGetValue($"{id} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : key;
        }

        /// <summary>Every real location whose Canonical form is the given map - the alias expansion,
        /// in one place.
        ///
        /// factory4_day yields both Factory keys and Sandbox yields both Ground Zero keys. Derived
        /// data is canonical and QuestDto.LocationKey is not, so emitting only the canonical name
        /// loses Factory night its sidebar entry and its pins - and Ground Zero above level 20 is
        /// most players.
        /// </summary>
        public IReadOnlyList<string> LocationKeysFor(string canonical)
        {
            if (string.IsNullOrWhiteSpace(canonical)) return Array.Empty<string>();

            _keysByCanonical ??= BuildKeysByCanonical();

            return _keysByCanonical.TryGetValue(canonical, out var keys)
                ? keys
                : new List<string> { canonical };
        }

        /// <summary>Whether a name is a real location's internal name - "bigmap", "Labyrinth",
        /// "factory4_night".
        ///
        /// The gate on what a peer may write. Keyed on internal names rather than location ids
        /// because that is what a harvest posts, and case-insensitively because a client's casing
        /// is its own business.</summary>
        public bool IsRealLocation(string? map) =>
            !string.IsNullOrWhiteSpace(map) && BuildLocationLookups().keyToId.ContainsKey(map);

        /// <summary>Whether a quest's declared location tells us nothing, so its zones may speak
        /// instead.
        ///
        /// Three cases, not two. Blank and "any" are the obvious ones. The third was measured: six
        /// vanilla quests declare "marathon", which matches no location id, and two of them carry
        /// carry-conditions. Trusting that declaration files them under a bucket no locationId ever
        /// matches, which costs them their list entry AND their pins.
        /// </summary>
        public bool IsUselessLocation(string? location) =>
            string.IsNullOrWhiteSpace(location) ||
            location.Equals(AnyLocation, StringComparison.OrdinalIgnoreCase) ||
            !LocationIdToKey.ContainsKey(location);

        // ---- placing a quest on a map --------------------------------------------------------

        /// <summary>The maps one condition actually happens on, as alias-expanded location keys.
        ///
        /// Per condition, never per quest: "Is This a Reference" wants 27 WI-FI cameras spread over
        /// eight maps, and scoping them by the quest would put all 27 on Factory.
        ///
        /// Empty when the condition names no zone, or names one nothing has harvested - which is
        /// the honest answer rather than a guess. Zone ids often contain a map name and matching on
        /// that string would quietly mis-file a modded map's quests.
        /// </summary>
        public IReadOnlyList<string> MapKeysOfCondition(
            QuestCondition condition,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap)
        {
            if (condition == null || zoneToMap == null || zoneToMap.Count == 0) return Array.Empty<string>();

            List<string>? keys = null;

            foreach (var zoneId in QuestPayloadBuilder.ZoneIdsOf(condition))
            {
                if (string.IsNullOrWhiteSpace(zoneId)) continue;

                // A SET of canonical maps: three shipped zone ids sit on more than one map with no
                // attacker present, and dropping one of them removes a requirement row - which the
                // cue draws as green.
                if (!zoneToMap.TryGetValue(zoneId, out var canonicalMaps)) continue;

                foreach (var canonical in canonicalMaps)
                {
                    foreach (var key in LocationKeysFor(canonical))
                    {
                        keys ??= new List<string>();
                        if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
                    }
                }
            }

            return (IReadOnlyList<string>?)keys ?? Array.Empty<string>();
        }

        /// <summary>The maps a quest belongs to: its own declaration when that is worth anything,
        /// otherwise the union of its conditions' zones.
        ///
        /// This is the one derivation, shared by the quest payload and the marker builder. It is not
        /// enough for the two to agree today - the invariant is that a quest never appears in a
        /// map's list without pins, or the reverse, and that only holds if both read this.
        /// </summary>
        public IReadOnlyList<string> MapKeysOfQuest(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap)
        {
            if (quest == null) return Array.Empty<string>();

            if (!IsUselessLocation(quest.Location))
            {
                // A quest that names a real map is trusted, even if its zones say otherwise -
                // overriding the author would be this mod deciding it knows better. Alias-expanded
                // so a quest declared on factory4_day is reachable from Factory night too.
                return LocationIdToKey.TryGetValue(quest.Location!, out var key)
                    ? LocationKeysFor(ZoneStore.Canonical(key))
                    : Array.Empty<string>();
            }

            List<string>? keys = null;

            foreach (var condition in quest.Conditions?.AvailableForFinish ?? Enumerable.Empty<QuestCondition>())
            {
                foreach (var key in MapKeysOfCondition(condition, zoneToMap))
                {
                    keys ??= new List<string>();
                    if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
                }
            }

            return (IReadOnlyList<string>?)keys ?? Array.Empty<string>();
        }

        // ---- lookups -------------------------------------------------------------------------

        private (Dictionary<string, string> idToKey, Dictionary<string, string> keyToId) BuildLocationLookups()
        {
            if (_locationIdToKey != null && _locationKeyToId != null)
                return (_locationIdToKey, _locationKeyToId);

            var idToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keyToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var location in locationTable.GetDictionary().Values)
            {
                var internalName = location?.Base?.Id;
                var locationId = location?.Base?.IdField.ToString();

                if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(locationId))
                    continue;

                idToKey[locationId] = internalName;
                keyToId[internalName] = locationId;
            }

            _locationIdToKey = idToKey;
            _locationKeyToId = keyToId;

            return (idToKey, keyToId);
        }

        private Dictionary<string, List<string>> BuildKeysByCanonical()
        {
            var byCanonical = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in BuildLocationLookups().keyToId.Keys)
            {
                var canonical = ZoneStore.Canonical(key);

                if (!byCanonical.TryGetValue(canonical, out var keys))
                    byCanonical[canonical] = keys = new List<string>();

                keys.Add(key);
            }

            return byCanonical;
        }

        /// <summary>The profile's quest list, indexed once.</summary>
        public sealed class Progress(Dictionary<MongoId, QuestStatus> entries, HashSet<string> succeeded)
        {
            /// <summary>Quest id -> the profile's entry for it. Absent means the quest has never
            /// been touched, which is NOT the same as locked.</summary>
            public Dictionary<MongoId, QuestStatus> Entries { get; } = entries;

            /// <summary>Ids of the quests already handed in, for the prerequisite check.</summary>
            public HashSet<string> Succeeded { get; } = succeeded;
        }
    }
}
