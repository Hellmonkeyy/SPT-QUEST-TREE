using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils.Json;

namespace QuestTreeServer
{
    /// <summary>
    /// Builds the complete, unfiltered quest list the Quest Tracker client mod draws its tree from.
    ///
    /// This exists because the game's own quest list cannot answer the question. /client/quest/list
    /// is served by QuestHelper.GetClientQuests, which returns a quest only if it is already in the
    /// player's profile, or else passes every one of: side check, edition blacklist, edition
    /// whitelist, seasonal-event check, player-level check, trader-exists check, AND has all of its
    /// AvailableForStart prerequisite quests already satisfied in the profile. That is exactly the
    /// set of quests a progression tree needs to see PAST, so the client has never had the data.
    ///
    /// templateTable.Quests is the same source GetClientQuests filters down from, so this serves
    /// the real thing rather than a reconstruction - deliberately with no filtering whatsoever.
    /// Level-gated, other-faction, seasonal and edition-locked quests are all included and merely
    /// flagged, leaving the client to decide how to present them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class QuestPayloadBuilder(
        ISptLogger<QuestPayloadBuilder> logger,
        TemplateTable templateTable,
        LocaleService localeService,
        SeasonalEventService seasonalEventService,
        QuestConfig questConfig,
        QuestFacts facts,
        ZoneStore zoneStore,
        WeaponStatModel weaponStatModel,
        WeaponGraph weaponGraph,
        WeaponSolver weaponSolver) : IOnLoad
    {
        /// <summary>Built while the server starts, for the reason MapMarkerPayloadBuilder gives:
        /// the client's request handler is synchronous on Unity's main thread, so paying for the
        /// first build there froze the game on the first panel open.</summary>
        /// <summary>Weapons named by a WeaponAssembly condition on this install, filled while the
        /// quests are mapped.</summary>
        private readonly HashSet<MongoId> _questWeapons = new();

        /// <summary>Every build requirement on this install, paired with the quest that states it,
        /// so the solver can be run over all of them at boot.</summary>
        private readonly List<(string Quest, WeaponBuildDto Build)> _questBuilds = new();

        public Task OnLoadAsync(CancellationToken cancellationToken)
        {
            GetPayloadJson();

            // After the payload, because the set of weapons to walk is filled while it is built.
            // Deliberately at boot: a cyclic slot graph is uncatchable at runtime, so the walk that
            // would meet one has to happen where a log line is read rather than inside a request.
            weaponGraph.Survey(_questWeapons);
            SurveySolver();

            return Task.CompletedTask;
        }

        /// <summary>Runs the solver over every build requirement on this install, once, and says
        /// how many it could satisfy.
        ///
        /// A search is either tractable on real data or it is not, and counting is the only way to
        /// find out. This runs against the FULL parts list rather than what the player can buy,
        /// deliberately: it is asking whether the search works, not whether this profile can afford
        /// the answer, and conflating the two would make a solver bug look like a poor trader level.
        ///
        /// Debug, because it is a developer's question. The one-line summary is Info.</summary>
        private void SurveySolver()
        {
            if (_questBuilds.Count == 0) return;

            var solved = 0;
            var ceiling = 0;
            var failed = new List<string>();

            // Why, not just how many. A pass rate with no cause behind it invites guessing at the
            // algorithm, and the reasons separate three very different problems: a search too weak
            // to place the parts a quest names, a search that places them and misses a number, and a
            // model that cannot see the stat at all.
            var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (questName, build) in _questBuilds)
            {
                if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) continue;

                var thresholds = build.Thresholds
                    .Select(t => (t.Field, t.Compare, t.Value))
                    .ToList();

                var mustInclude = new List<MongoId>();
                foreach (var id in build.RequiredItemIds)
                    if (id.TryParseMongoId(out var parsed)) mustInclude.Add(parsed);

                var result = weaponSolver.Solve(weapon, thresholds, mustInclude, allowed: null);

                if (result.HitCeiling) ceiling++;

                if (result.Found)
                {
                    solved++;
                    logger.Debug(
                        $"Quest Tracker: solved '{questName}' ({build.WeaponName}) with {result.Parts.Count} parts " +
                        $"in {result.NodesOpened:N0} nodes.");
                    continue;
                }

                logger.Info(
                    $"DETAIL {questName} ({build.WeaponTemplate}): " +
                    string.Join(", ", build.Thresholds.Select(t => $"{t.Field}{t.Compare}{t.Value}")) +
                    $" | required {string.Join(", ", build.RequiredItemNames)}" +
                    $" | cats {string.Join(", ", build.RequiredCategoryNames)}" +
                    $" | emptyTac {build.EmptyTacticalSlots}" +
                    $" | nodes {result.NodesOpened} ceiling {result.HitCeiling}" +
                    $" | parts {result.Parts.Count}");

                logger.Info("  BUILD " + string.Join(" ", result.Parts.Select(p => $"{p.SlotName}={p.Template}")));

                foreach (var threshold in build.Thresholds)
                {
                    var solo = weaponSolver.Solve(
                        weapon, new[] { (threshold.Field, threshold.Compare, threshold.Value) }, mustInclude, null);

                    logger.Info(
                        $"  SOLO {threshold.Field}{threshold.Compare}{threshold.Value} -> " +
                        (solo.Stats == null
                            ? "no stats"
                            : $"erg {solo.Stats.Ergonomics:0.##} rec {solo.Stats.Recoil:0.##} wt {solo.Stats.Weight:0.###} " +
                              $"mag {solo.Stats.MagazineCapacity} eff {solo.Stats.EffectiveDistance}") +
                        (solo.Found ? " MET" : $" UNMET {string.Join("; ", solo.Unmet.Take(2))}"));
                }

                // A bare template id does not say which part the search could not place, and that is
                // the only question these lines get read to answer. The DTO already carries the
                // names parallel to the ids, so the substitution costs nothing.
                failed.Add(
                    $"{questName} ({build.WeaponName}): " +
                    string.Join("; ", result.Unmet.Take(8).Select(u => Named(u, build))));

                foreach (var unmet in result.Unmet)
                {
                    // The reason's first words identify its kind; the numbers after it are per
                    // quest and would make every row unique.
                    var kind = unmet.Contains("NOT REACHABLE", StringComparison.Ordinal)
                        ? "required part not reachable (graph gap)"
                        : unmet.StartsWith("could not fit", StringComparison.OrdinalIgnoreCase)
                            ? "required part reachable but not placed (search gap)"
                            : unmet.Contains("no value", StringComparison.OrdinalIgnoreCase)
                                ? $"{unmet.Split(':')[0]}: nothing provides it"
                                : unmet.Split(' ')[0];

                    reasons[kind] = reasons.TryGetValue(kind, out var count) ? count + 1 : 1;
                }
            }

            logger.Info(
                $"Quest Tracker: weapon solver dry run - {solved} of {_questBuilds.Count} build requirement(s) " +
                $"satisfied from the full parts list" +
                (ceiling > 0 ? $", {ceiling} hit the search budget" : "") + ".");

            if (reasons.Count > 0)
                logger.Info(
                    "Quest Tracker: solver misses by - " +
                    string.Join(", ", reasons.OrderByDescending(r => r.Value).Take(6).Select(r => $"{r.Key} x{r.Value}")));

            foreach (var line in failed.Take(12))
                logger.Info($"Quest Tracker: unsolved - {line}");
        }

        /// <summary>One unmet reason with every required-part id in it replaced by the part's
        /// name.</summary>
        private static string Named(string unmet, WeaponBuildDto build)
        {
            var count = Math.Min(build.RequiredItemIds.Count, build.RequiredItemNames.Count);

            for (var i = 0; i < count; i++)
                unmet = unmet.Replace(build.RequiredItemIds[i], build.RequiredItemNames[i], StringComparison.Ordinal);

            return unmet;
        }

        /// <summary>Condition type that names another quest as a prerequisite.</summary>
        private const string QuestConditionType = "Quest";

        /// <summary>Condition type carrying the quest's player-level requirement.</summary>
        private const string LevelConditionType = "Level";

        /// <summary>Key into Quest.Rewards for the rewards paid on handing the quest in.</summary>
        private const string SuccessRewardKey = "Success";


        private readonly object _buildLock = new();
        private string? _cachedJson;

        private readonly RebuildGate _gate = new(60);

        /// <summary>Builds again now, after a harvest has taught the server where new zones are.
        ///
        /// Needed since 1.9.0 and its absence was a shipped-broken bug waiting to happen: derived
        /// locations come from the harvested zone index, so a raid that harvests a map would give a
        /// quest its PINS - the marker builder rebuilds - while its map on the client stayed empty
        /// until the next server restart. That is the "in the list with no pins, or the reverse"
        /// split the derivation exists to prevent, arriving by the back door.
        ///
        /// Built into a local and swapped, never nulled first: a GET arriving mid-rebuild would
        /// otherwise block on _buildLock, on the client's main thread, behind its 15-second cap. The
        /// cache is therefore never absent, only briefly stale.</summary>
        public void Rebuild()
        {
            lock (_buildLock)
            {
                _gate.Clear();

                try
                {
                    var payload = Build();
                    payload.ModVersion = ModInfo.Version;
                    _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
                }
                catch (Exception ex)
                {
                    // Keep serving the previous answer. A harvest is not a reason to lose the tree.
                    logger.Warning($"Quest Tracker: the quest list rebuild after a harvest failed ({ex.Message}) - serving the previous list.");
                }
            }
        }

        /// <summary>Serialized and cached. The quest database itself does not change while the
        /// server is running, but the derived locations on it do - see Rebuild.</summary>
        public string GetPayloadJson()
        {
            if (_cachedJson != null) return _cachedJson;

            lock (_buildLock)
            {
                if (_cachedJson != null) return _cachedJson;
                if (_gate.Paused) return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);

                QuestPayloadDto payload;

                try
                {
                    payload = Build();
                }
                catch (Exception ex)
                {
                    // Now built at startup, where a throw would abort SPT's boot. An empty list is
                    // a valid payload the client already degrades on - answered, not cached, so a
                    // fault at boot does not mean an empty tree until the server restarts.
                    logger.Error($"Quest Tracker: could not build the quest list - the tree will be empty: {ex}");
                    _gate.PauseThenRewarm(() => GetPayloadJson(),
                        message => logger.Warning($"Quest Tracker: the quest list re-build did not run ({message})."));
                    return JsonSerializer.Serialize(new QuestPayloadDto(), WireJson.Options);
                }

                payload.ModVersion = ModInfo.Version;
                return _cachedJson = JsonSerializer.Serialize(payload, WireJson.Options);
            }
        }

        private QuestPayloadDto Build()
        {
            var payload = new QuestPayloadDto();

            var quests = templateTable.Quests;
            if (quests == null)
            {
                logger.Error("Quest Tracker: the quest template table is empty - serving nothing.");
                return payload;
            }

            var locale = localeService.GetLocaleDb();

            // Hoisted: built once per payload rather than once per quest, like QuestsByLocation.
            var zoneToMap = zoneStore.ZoneToMap();

            foreach (var quest in quests.Values)
            {
                if (quest == null) continue;

                try
                {
                    payload.Quests.Add(MapQuest(quest, zoneToMap, locale));
                }
                catch (Exception ex)
                {
                    // One malformed quest - which in a modded install is entirely possible - must
                    // not cost the client every other quest in the game.
                    logger.Warning($"Quest Tracker: skipped quest '{quest.Id}': {ex.Message}");
                }
            }

            logger.Info($"Quest Tracker {ModInfo.Stamp}: serving {payload.Quests.Count} quests to the client mod.");
            return payload;
        }

        private QuestDto MapQuest(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            var id = quest.Id.ToString();

            return new QuestDto
            {
                Id = id,
                Name = ResolveQuestName(quest, id, locale),
                TraderId = quest.TraderId.ToString(),
                Side = ResolveSide(quest),
                Type = quest.Type.ToString(),
                Level = ResolveLevelRequirement(quest),
                LocationId = ResolveLocationName(quest, locale),
                LocationKey = ResolveLocationKey(quest),
                IsEvent = IsEventQuest(quest.Id),
                EditionRestricted = IsEditionRestricted(quest.Id),
                Prerequisites = MapPrerequisites(quest),
                DerivedLocations = DeriveLocations(quest, zoneToMap, locale),
                Objectives = MapObjectives(quest, locale),
                WeaponBuilds = MapWeaponBuilds(quest, locale),
                Rewards = MapRewards(quest, locale)
            };
        }

        /// <summary>Quest.Name is a locale key, not a display name - the real name lives under
        /// "&lt;questId&gt; name" (the same key QuestHelper.GetQuestNameFromLocale uses). Falls back
        /// to the internal QuestName rather than to a raw key, so the client never renders one.</summary>
        internal static string ResolveQuestName(Quest quest, string id, Dictionary<string, string> locale)
        {
            if (locale.TryGetValue($"{id} name", out var localized) && !string.IsNullOrWhiteSpace(localized))
                return localized;

            if (!string.IsNullOrWhiteSpace(quest.QuestName)) return quest.QuestName!;

            return string.IsNullOrWhiteSpace(quest.Name) ? id : quest.Name;
        }

        /// <summary>Most quests carry Side "Pmc" even when they are faction-locked; the actual
        /// restriction lives in the quest config's Bear/Usec-only sets, which is what
        /// QuestHelper.QuestIsForOtherSide checks.</summary>
        private string ResolveSide(Quest quest)
        {
            if (questConfig.BearOnlyQuests?.Contains(quest.Id) == true) return "Bear";
            if (questConfig.UsecOnlyQuests?.Contains(quest.Id) == true) return "Usec";
            return quest.Side ?? "";
        }

        private bool IsEventQuest(MongoId questId) =>
            seasonalEventService.IsQuestRelatedToEvent(questId, SeasonalEventType.Christmas) ||
            seasonalEventService.IsQuestRelatedToEvent(questId, SeasonalEventType.Halloween);

        /// <summary>True when the quest carries any game-edition restriction at all - an entry in
        /// the exclusive whitelist, or in any edition's inclusive blacklist.</summary>
        private bool IsEditionRestricted(MongoId questId)
        {
            if (questConfig.ProfileWhitelist?.ContainsKey(questId) == true) return true;

            return questConfig.ProfileBlacklist?.Values.Any(blacklisted => blacklisted.Contains(questId)) == true;
        }

        /// <summary>
        /// The map's internal name ("bigmap", "Labyrinth"), which is what map tools key on.
        ///
        /// Quest.Location is the location's MongoId, not its internal name, so the two never match
        /// without this. The locations table is the authority on the pairing - every location
        /// carries both forms, IdField being the MongoId a quest cites and Id the internal name -
        /// so it is read directly rather than inverting questConfig.LocationIdMap. That config was
        /// the obvious source and is the wrong one: it has no Labyrinth entry, so the Labyrinth
        /// quests resolved to a bare MongoId and matched nothing.
        ///
        /// The pairing itself lives on QuestFacts, which builds it once and hands the same table to
        /// the marker builder - the two must agree about which map a quest is on, and two copies of
        /// that is how they stop agreeing. Falls back to the raw id, which is at least stable, for a
        /// location that is not in the table at all.
        /// </summary>
        private string ResolveLocationKey(Quest quest)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase)) return location;

            // Blank rather than the raw id for a location that is not a location. Six vanilla quests
            // declare "marathon", which matches nothing in the table, and the raw-id fallback made
            // that into a map key: the locale answers "marathon Name" with "Transition", so
            // GroupByMap filed them under a phantom map of that name with no image, no floors and no
            // pins, because FindByLocationKey never matched. Blank is already skipped there.
            //
            // "any" is excepted deliberately - it is not a location id either, but it is a real
            // declaration the client names and tests for in three places.
            return facts.LocationIdToKey.TryGetValue(location, out var key) ? key : "";
        }

        /// <summary>The maps a quest is actually done on when it refuses to say.
        ///
        /// A quest whose Location is "any" can still be firmly placed: "Sanitary Investigation -
        /// Part 5" declares "any" and then names five Shoreline zones. Left alone, GroupByMap drops
        /// it and the marker builder never gives it pins, so the quest is missing from the one map
        /// it belongs to.
        ///
        /// A list, not a value: a quest can genuinely span maps - one plants at an aishi_shoreline
        /// zone and an aishi_woods zone - and collapsing that to a single map would be a different
        /// lie. Empty for hand-ins, skills and trader tasks, which have no zones and belong on no
        /// map.
        ///
        /// One line of derivation, shared with MapMarkerPayloadBuilder through QuestFacts. It is not
        /// enough for the two to agree today: the invariant is that a quest never appears in a map's
        /// list without pins, or the reverse, and that only holds if both read the same function.</summary>
        private List<DerivedLocationDto> DeriveLocations(
            Quest quest,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale)
        {
            // Only when the declaration is useless. A quest that names a REAL map is trusted, even
            // if its zones say otherwise - overriding the author would be this mod deciding it knows
            // better.
            if (!facts.IsUselessLocation(quest.Location)) return new List<DerivedLocationDto>();

            var maps = new List<DerivedLocationDto>();

            foreach (var key in facts.MapKeysOfQuest(quest, zoneToMap))
                maps.Add(new DerivedLocationDto { Key = key, Name = facts.LocationNameOf(key, locale) });

            return maps;
        }

        /// <summary>Quest.Location is a raw map id (e.g. 5704e3c2d2720bac5b8b4567), which is no use
        /// on a node subtitle. The map's display name lives in the locale table under
        /// "&lt;locationId&gt; Name". "any" is passed through untouched - the client treats it as
        /// "no specific map" and hides it.</summary>
        private string ResolveLocationName(Quest quest, Dictionary<string, string> locale)
        {
            var location = quest.Location;
            if (string.IsNullOrWhiteSpace(location)) return "";
            if (location.Equals(QuestFacts.AnyLocation, StringComparison.OrdinalIgnoreCase))
                return QuestFacts.AnyLocation;

            // Blank for a non-location, matching ResolveLocationKey: the locale WILL answer
            // "marathon Name" with "Transition", which is precisely how a phantom map got a display
            // name convincing enough to sit in the map dropdown.
            if (!facts.LocationIdToKey.ContainsKey(location)) return "";

            return locale.TryGetValue($"{location} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : location;
        }

        /// <summary>The player level gate, which lives as a "Level" condition alongside the quest
        /// prerequisites in AvailableForStart. Returns 0 when the quest has no level requirement.
        /// Takes the highest if a quest somehow declares more than one.</summary>
        private static int ResolveLevelRequirement(Quest quest)
        {
            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return 0;

            var level = 0;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, LevelConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Numbers.ToCount(condition.Value);
                if (value > level) level = value;
            }

            return level;
        }

        private static List<PrerequisiteDto> MapPrerequisites(Quest quest)
        {
            var prerequisites = new List<PrerequisiteDto>();

            var conditions = quest.Conditions?.AvailableForStart;
            if (conditions == null) return prerequisites;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, QuestConditionType, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var target in TargetIds(condition.Target))
                {
                    if (string.IsNullOrWhiteSpace(target)) continue;

                    prerequisites.Add(new PrerequisiteDto
                    {
                        Target = target,
                        Status = condition.Status?.Select(status => status.ToString()).ToList() ?? new List<string>(),
                        AvailableAfter = condition.AvailableAfter ?? 0
                    });
                }
            }

            return prerequisites;
        }

        private static List<ObjectiveDto> MapObjectives(Quest quest, Dictionary<string, string> locale)
        {
            var objectives = new List<ObjectiveDto>();

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return objectives;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;

                var conditionId = condition.Id.ToString();

                var targetItems = IsAnyItemCondition(condition.ConditionType)
                    ? TargetIds(condition.Target).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
                    : new List<string>();

                objectives.Add(new ObjectiveDto
                {
                    Id = conditionId,
                    // Objective descriptions are keyed in the locale table by the condition's own
                    // id. A modded quest may not have one, hence the condition type as a fallback.
                    Text = locale.TryGetValue(conditionId, out var text) && !string.IsNullOrWhiteSpace(text)
                        ? text
                        : condition.ConditionType ?? "",
                    IsNecessary = condition.IsNecessary ?? true,
                    ConditionType = condition.ConditionType ?? "",
                    TargetItems = targetItems,
                    TargetItemNames = targetItems.Select(template => ResolveItemName(template, locale)).ToList(),
                    Count = Numbers.ToCount(condition.Value),
                    FoundInRaid = condition.OnlyFoundInRaid ?? false,
                    ZoneIds = ZoneIdsOf(condition).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                });
            }

            return objectives;
        }

        /// <summary>Condition type stating a weapon build requirement.</summary>
        private const string WeaponAssemblyType = "WeaponAssembly";

        /// <summary>What a Gunsmith-style quest actually asks for, in words - EVERY build it asks
        /// for, not the first.
        ///
        /// Without this such a quest renders as "Handover the custom M4A1  0/1", which tells you
        /// nothing about the twelve numbers it is really checking. 56 quests on this install carry
        /// one of these conditions.
        ///
        /// A quest can carry several. "Gunsmith - Old Friend's Request" wants a T-5000M, a PP-19-01
        /// AND a Glock 17, each with its own thresholds and its own parts - and returning on the
        /// first match showed the rifle and silently dropped the other two, so the panel described a
        /// third of the task while the objectives listed all three.</summary>
        private List<WeaponBuildDto> MapWeaponBuilds(Quest quest, Dictionary<string, string> locale)
        {
            var builds = new List<WeaponBuildDto>();

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return builds;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, WeaponAssemblyType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var weapon = TargetIds(condition.Target).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                if (string.IsNullOrWhiteSpace(weapon)) continue;

                var build = new WeaponBuildDto
                {
                    WeaponTemplate = weapon!,
                    WeaponName = ResolveItemName(weapon!, locale),
                    EmptyTacticalSlots = condition.EmptyTacticalSlot?.Value
                };

                // Every threshold the condition states, by its own field name so a modded stat
                // nobody has heard of still reaches the screen.
                AddThreshold(build, "ergonomics", condition.Ergonomics);
                AddThreshold(build, "recoil", condition.Recoil);
                AddThreshold(build, "weight", condition.Weight);
                AddThreshold(build, "magazine capacity", condition.MagazineCapacity);
                AddThreshold(build, "effective distance", condition.EffectiveDistance);
                AddThreshold(build, "durability", condition.Durability);
                AddThreshold(build, "height", condition.Height);
                AddThreshold(build, "width", condition.Width);
                AddThreshold(build, "base accuracy", condition.BaseAccuracy);
                AddThreshold(build, "muzzle velocity", condition.MuzzleVelocity);

                // Ids as well as names. The names are for reading; a solver needs the ids, and the
                // old parse resolved each one straight into a locale string and dropped it.
                foreach (var item in condition.ContainsItems ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(item)) continue;

                    build.RequiredItemIds.Add(item);
                    build.RequiredItemNames.Add(ResolveItemName(item, locale));
                }

                foreach (var category in condition.HasItemFromCategory ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(category)) continue;

                    build.RequiredCategoryIds.Add(category);
                    build.RequiredCategoryNames.Add(ResolveItemName(category, locale));
                }

                build.ModelCheck = CheckModel(build);
                builds.Add(build);

                // Remembered so the slot graphs can be walked once at boot. Collected here rather
                // than re-derived later because this is the only pass that already knows which
                // weapons the installed quests actually name.
                if (weapon!.TryParseMongoId(out var weaponId)) _questWeapons.Add(weaponId);

                _questBuilds.Add((ResolveQuestName(quest, quest.Id.ToString(), locale), build));
            }

            return builds;
        }

        /// <summary>Scores the quest's own named parts on the quest's own weapon.
        ///
        /// This is the gate WeaponStatModel was written for and never received. The model shipped a
        /// release before any solver precisely so it could be proven against the running game first
        /// - and then nothing called it, so the proof never happened and the solver stayed unwritten
        /// for two releases.
        ///
        /// Partial by nature, and that is worth being plain about: a condition's ContainsItems names
        /// a few leaf parts, not a whole gun. What it can catch is a wrong COMBINING RULE - summing
        /// where the game multiplies, summing where it selects - and that is the failure that would
        /// otherwise reach a hand-in.</summary>
        private WeaponModelCheckDto? CheckModel(WeaponBuildDto build)
        {
            if (build.RequiredItemIds.Count == 0) return null;

            // TryParseMongoId, not the constructor: a modded condition can name something that is
            // not an id at all, and MongoId's constructor does not politely decline.
            var parts = new List<MongoId>();
            foreach (var id in build.RequiredItemIds)
                if (id.TryParseMongoId(out var parsed)) parts.Add(parsed);

            if (!build.WeaponTemplate.TryParseMongoId(out var weapon)) return null;

            var stats = weaponStatModel.Score(weapon, parts);
            if (stats == null) return null;

            var unfilled = weaponGraph.UnfilledRequiredSlots(weapon, parts, out var requiredSlots);

            return new WeaponModelCheckDto
            {
                UnfilledRequiredSlots = unfilled,
                RequiredSlots = requiredSlots,
                Ergonomics = stats.Ergonomics,
                Recoil = stats.Recoil,
                Weight = stats.Weight,
                MagazineCapacity = stats.MagazineCapacity,
                EffectiveDistance = stats.EffectiveDistance,
                PartsNamed = build.RequiredItemIds.Count,
                PartsScored = parts.Count,
                Clamped = stats.Clamped.ToList()
            };
        }

        /// <summary>Adds a threshold, unless it is the unconstrained default.
        ///
        /// Skipped on the VALUE being zero, never on the field name. In the first vanilla condition
        /// effectiveDistance, weight, baseAccuracy and muzzleVelocity are all ">= 0" and pure noise
        /// on screen - but height and width are "<= 1" and "<= 4" and entirely real, in 5 quests
        /// each. Writing those two off by name, which an earlier reading of one example suggested,
        /// would have dropped a genuine constraint.</summary>
        private static void AddThreshold(WeaponBuildDto build, string field, ValueCompare? compare)
        {
            var value = compare?.Value ?? 0d;

            if (value == 0d)
            {
                // Recorded rather than silently dropped. Once the row is gone, "unconstrained noise"
                // and "genuinely constrained to zero" look identical, and a solver has to tell them
                // apart - the field is only named here when the condition named it at all.
                if (compare != null) build.ZeroThresholdFields.Add(field);
                return;
            }

            build.Thresholds.Add(new WeaponBuildThresholdDto
            {
                Field = field,
                Compare = compare!.CompareMethod ?? ">=",
                Value = value
            });
        }

        private static List<RewardDto> MapRewards(Quest quest, Dictionary<string, string> locale)
        {
            var rewards = new List<RewardDto>();

            if (quest.Rewards == null) return rewards;
            if (!quest.Rewards.TryGetValue(SuccessRewardKey, out var successRewards) || successRewards == null)
                return rewards;

            foreach (var reward in successRewards)
            {
                if (reward == null) continue;
                if (reward.IsHidden == true) continue;

                var template = reward.Items?.FirstOrDefault()?.Template.ToString() ?? "";

                rewards.Add(new RewardDto
                {
                    Type = reward.Type?.ToString() ?? "",
                    Value = reward.Value ?? 0d,
                    Name = ResolveRewardName(reward, locale),
                    ShortName = ResolveShortName(template, locale),
                    Template = template,
                    // Trader names are deliberately left to the client, which resolves them from
                    // the live session and so gets modded traders right for free.
                    TraderId = reward.TraderId?.ToString() ?? ""
                });
            }

            return rewards;
        }

        /// <summary>Item rewards are the only ones whose display name the client cannot work out for
        /// itself, so the item name is resolved here. Everything else is a number, a trader the
        /// client already knows, or carries its own target id.</summary>
        private static string ResolveRewardName(Reward reward, Dictionary<string, string> locale)
        {
            var template = reward.Items?.FirstOrDefault()?.Template.ToString();

            if (!string.IsNullOrWhiteSpace(template) &&
                locale.TryGetValue($"{template} Name", out var itemName) &&
                !string.IsNullOrWhiteSpace(itemName))
            {
                return itemName;
            }

            return reward.Target ?? "";
        }

        /// <summary>The item's short name, which is a separate locale key from its name. Empty
        /// rather than falling back, so the client can tell "there is no short name" from "the short
        /// name happens to equal the long one" and choose per context.</summary>
        private static string ResolveShortName(string template, Dictionary<string, string> locale)
        {
            if (string.IsNullOrWhiteSpace(template)) return "";

            return locale.TryGetValue($"{template} ShortName", out var shortName) &&
                   !string.IsNullOrWhiteSpace(shortName)
                ? shortName
                : "";
        }

        /// <summary>Condition types that TAKE an item from you - handed to a trader, or found in
        /// raid and turned in. These and only these are what the Collector hand-in checklist is
        /// built from, so the set must stay narrow: an item you plant and leave behind is not an
        /// item Collector will accept.</summary>
        internal static bool IsItemCondition(string? conditionType) =>
            string.Equals(conditionType, "HandoverItem", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "FindItem", StringComparison.OrdinalIgnoreCase);

        /// <summary>Condition types that have you CARRY an item into a raid and leave it somewhere -
        /// a marker on a trading post, a beacon in a warehouse. The template id is in the same
        /// Target field, but the item never reaches a trader, which is why these are kept apart
        /// from IsItemCondition above.</summary>
        internal static bool IsCarriedItemCondition(string? conditionType) =>
            string.Equals(conditionType, "LeaveItemAtLocation", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(conditionType, "PlaceBeacon", StringComparison.OrdinalIgnoreCase);

        /// <summary>Every condition whose Target is item template ids rather than quest ids or zone
        /// names. This is the set an objective's TargetItems is filled from, and the set the client
        /// asks "what do I need to bring" of - which is a wider question than "what does a trader
        /// want", and getting it wrong is what left markers and beacons off that list.</summary>
        internal static bool IsAnyItemCondition(string? conditionType) =>
            IsItemCondition(conditionType) || IsCarriedItemCondition(conditionType);

        /// <summary>An item's display name from the locale table, falling back to the template id.
        /// Was a private copy in three builders.</summary>
        internal static string ResolveItemName(string template, Dictionary<string, string> locale) =>
            locale.TryGetValue($"{template} Name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : template;

        internal static IEnumerable<string> TargetIds(ListOrT<string>? target)
        {
            if (target == null) yield break;

            if (target.IsList)
            {
                foreach (var value in target.List ?? Enumerable.Empty<string>())
                    yield return value;

                yield break;
            }

            if (target.Item != null) yield return target.Item;
        }

        /// <summary>
        /// Every zone id a condition can point at. The shape follows DynamicMaps' own condition
        /// walk (QuestUtils.GetPositionsForCondition), which is the one known to line up with what
        /// the scene contains: the condition's own zoneId (LeaveItemAtLocation, PlaceBeacon), and
        /// inside a CounterCreator each sub-condition's VisitPlace target or zoneIds list (InZone,
        /// and the zoned Kills/Shots/LaunchFlare counters).
        /// </summary>
        internal static IEnumerable<string> ZoneIdsOf(QuestCondition condition)
        {
            if (!string.IsNullOrWhiteSpace(condition.ZoneId)) yield return condition.ZoneId!;

            var counters = condition.Counter?.Conditions;
            if (counters == null) yield break;

            foreach (var sub in counters)
            {
                if (sub == null) continue;

                if (string.Equals(sub.ConditionType, "VisitPlace", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var id in TargetIds(sub.Target))
                        if (!string.IsNullOrWhiteSpace(id)) yield return id;
                }

                // The JSON field is "zoneIds"; SPT's model names the property Zones.
                if (sub.Zones == null) continue;

                foreach (var id in sub.Zones)
                    if (!string.IsNullOrWhiteSpace(id)) yield return id;
            }
        }
    }
}
