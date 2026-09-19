using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace QuestTreeServer
{
    /// <summary>
    /// What you must be carrying to finish the quests on each map.
    ///
    /// One request answers both the ready-up cue and the map sidebar, so the two can never disagree
    /// about whether you are ready. Every map in one answer rather than one map per request: the
    /// cue fires from a screen that has no POST path and the whole vanilla database holds 245 carry
    /// conditions, so the payload is small and the round trip is one.
    ///
    /// Profile-scoped and computed per request. The answer changes every time the player moves an
    /// item, which is exactly what this is asked about.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class RaidCheckPayloadBuilder(
        ISptLogger<RaidCheckPayloadBuilder> logger,
        LocaleService localeService,
        ProfileHelper profileHelper,
        ZoneStore zoneStore,
        QuestFacts facts)
    {
        public string GetPayloadJson(MongoId sessionId)
        {
            var json = Serialise(sessionId, out var phases);

            // ONCE at Info, and the PHASES rather than the total. The first answer after a boot cost 745 ms
            // against 10 and 6 ms for the two after it, on a route the client now refetches on every
            // matchmaker show - synchronously, on Unity's main thread, where 745 ms is a freeze rather than a
            // slow request. A total says only that it was slow; the split says which phase to warm, and it
            // stays here at Debug so a regression says so too.
            if (!_phased)
            {
                _phased = true;
                logger.Info($"Quest Tracker: raid check phases, first request - {phases}.");
            }
            else logger.Debug($"Quest Tracker: raid check phases - {phases}.");

            return json;
        }

        /// <summary>One answer, built and serialised, with the split of where its time went.
        ///
        /// The serialise is timed HERE rather than inside Build because on the first call it is not free: the
        /// serialiser builds its reflection metadata for the whole DTO graph on first use, which is one-time
        /// cost that reads as a slow request.</summary>
        private string Serialise(MongoId sessionId, out string phases)
        {
            var payload = Build(sessionId, warming: false, out var built);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var json = JsonSerializer.Serialize(payload, WireJson.Options);

            phases = $"{built}, serialise {clock.Elapsed.TotalMilliseconds:0.0} ms";

            return json;
        }

        /// <summary>Builds one full answer at boot, off the boot path, so the first request from the game pays
        /// for none of the one-time work behind it.
        ///
        /// WHAT IS ONE-TIME, measured: the first request cost 745 ms and the next two 10 and 6, so about
        /// 730 ms of it was work that happens once per process - the locale table, the zone index, the
        /// location lookups, this path's JIT, the serialiser's metadata for the DTO graph - and none of it is
        /// per-request. That is why warming is the whole fix rather than a partial one: the phases the first
        /// request would otherwise pay for are the same ones the steady-state 6 ms is made of.
        ///
        /// Every id is tried until one has a profile behind it, because an id with none builds an empty
        /// payload and warms nothing at all. It never throws: this is an optimisation nobody is waiting on,
        /// running on a thread where an exception would be lost in silence.</summary>
        public void Warm(IEnumerable<MongoId> sessions)
        {
            try
            {
                foreach (var session in sessions)
                {
                    var payload = Build(session, warming: true, out var built);

                    // No profile behind this id: nothing was walked, so nothing was warmed. Try the next one.
                    if (!payload.HasProfile) continue;

                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var json = JsonSerializer.Serialize(payload, WireJson.Options);

                    logger.Info(
                        $"Quest Tracker: raid check warmed off the boot path - {built}, serialise " +
                        $"{clock.Elapsed.TotalMilliseconds:0.0} ms, {json.Length:N0} bytes. The first request " +
                        "from the game pays for none of it.");

                    return;
                }

                logger.Info(
                    "Quest Tracker: no profile to warm the raid check with - the first request will pay for it.");
            }
            catch (Exception ex)
            {
                logger.Warning(
                    $"Quest Tracker: could not warm the raid check ({ex.Message}) - the first request will pay " +
                    "for it. The answer itself is unaffected.");
            }
        }

        /// <summary>See ProfilePayloadBuilder._timed: first request and slow ones at Info.</summary>
        private bool _timed;

        /// <summary>Whether the per-phase split has been reported at Info yet. Separate from _timed so a
        /// warm-up pass cannot spend the one line the first real request is meant to print.</summary>
        private bool _phased;

        /// <summary>One answer. <paramref name="warming"/> when it is the boot-path warm-up rather than a
        /// request, which is what keeps the tally line below at Debug for it: _timed spends its single Info
        /// line on the first caller, and the caller that line is FOR is the first real request. Warm's own
        /// Info line reports the warm-up.</summary>
        private RaidCheckDto Build(MongoId sessionId, bool warming, out string phases)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var at = 0d;

            // Measured on EVERY request and not only the first. A split taken once cannot show a regression,
            // which is the other half of what this line is for; after the first it is Debug.
            double Split()
            {
                var now = clock.Elapsed.TotalMilliseconds;
                var span = now - at;
                at = now;

                return span;
            }

            var payload = new RaidCheckDto();

            var profile = TryGetProfile(sessionId);
            var profileMs = Split();

            if (profile == null)
            {
                phases = $"profile {profileMs:0.0} ms, no profile";
                return payload;   // HasProfile false -> the cue stays neutral
            }

            payload.HasProfile = true;

            // Bypasses the three-second memo on purpose: "move the marker and look again" is the
            // test this feature is judged by, and backing out of the ready-up screen and re-entering
            // within three seconds would answer from the pre-move snapshot - failing the test while
            // looking entirely correct.
            var owned = ProfileInventory.CountFresh(profile, out var locationsKnown);
            payload.InventoryLocationsKnown = locationsKnown;
            var inventoryMs = Split();

            var locale = localeService.GetLocaleDb();
            var zoneToMap = zoneStore.ZoneToMap();
            var harvested = zoneStore.HarvestedMaps();
            var lookupsMs = Split();

            // One pass over the quest list of the profile rather than a scan per quest: the builders
            // this replaces used FirstOrDefault, which is 835 x 439 comparisons a request on the
            // reference profile - on a route fired from the ready-up screen.
            var progress = facts.IndexProgress(profile);
            var progressMs = Split();

            // Every real location gets a row up front, whether or not anything is wanted there.
            //
            // A map with no row is indistinguishable from a map with nothing to bring, and the
            // client draws the second as green - which is the aliased-map false green, and Ground
            // Zero above level 20 is most players. Emitting the row unconditionally is what lets the
            // client treat "no row" as its neutral signal.
            var maps = new Dictionary<string, RaidCheckMapDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in facts.AllLocationKeys())
            {
                maps[key] = new RaidCheckMapDto
                {
                    LocationKey = key,
                    Name = facts.LocationNameOf(key, locale),

                    // Green requires positive evidence. A map with no harvested zones cannot place
                    // an "any"-location carry condition at all, so it may inform but never reassure.
                    ZonesHarvested = harvested.Contains(ZoneStore.Canonical(key))
                };
            }

            var mapsMs = Split();

            foreach (var quest in facts.Quests())
            {
                try
                {
                    Collect(quest, profile, progress, zoneToMap, locale, owned, maps, payload);
                }
                catch (Exception ex)
                {
                    // Per quest, as every other builder here guards: one malformed modded quest
                    // costs its own rows, not the whole verdict.
                    logger.Warning($"Quest Tracker: skipped a quest in the raid check ({quest?.Id}): {ex.Message}");
                }
            }

            var questsMs = Split();

            payload.Maps = maps.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

            phases =
                $"profile {profileMs:0.0} ms, inventory walk {inventoryMs:0.0} ms, locale and zone index " +
                $"{lookupsMs:0.0} ms, quest progress {progressMs:0.0} ms, map rows {mapsMs:0.0} ms, quest walk " +
                $"{questsMs:0.0} ms";

            // The task-item tally is here rather than in a test because it is only ever interesting
            // against a real profile: it is what tells you whether a green row went green for the right
            // reason, on an install whose quest mods put items in those containers.
            var line =
                $"Quest Tracker: raid check - {payload.Maps.Sum(m => m.Requirements.Count)} carry conditions " +
                $"across {payload.Maps.Count(m => m.Requirements.Count > 0)} maps, " +
                $"{payload.ConditionsWithNoMap} placeable nowhere, " +
                $"{payload.Held.Values.Count(h => h.InTaskItems > 0)} of {payload.Held.Count} items in the " +
                $"task-item containers, in {clock.ElapsedMilliseconds} ms.";

            if (!warming && (!_timed || clock.ElapsedMilliseconds > ProfilePayloadBuilder.SlowRequestMs))
            {
                _timed = true;
                logger.Info(line);
            }
            else logger.Debug(line);

            return payload;
        }

        private void Collect(
            Quest? quest,
            PmcData profile,
            QuestFacts.Progress progress,
            IReadOnlyDictionary<string, IReadOnlyCollection<string>> zoneToMap,
            Dictionary<string, string> locale,
            Dictionary<string, ProfileInventory.Held> owned,
            Dictionary<string, RaidCheckMapDto> maps,
            RaidCheckDto payload)
        {
            if (quest == null) return;

            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null) return;

            // Cheap early-out before any status or locale work: 89 of the 558 vanilla quests reach
            // past it (830 is the modded total on this install, and its extras are not counted here).
            if (!conditions.Any(c => c != null && QuestPayloadBuilder.IsCarriedItemCondition(c.ConditionType)))
                return;

            var status = facts.StatusOf(progress, quest, profile);
            var completed = facts.CompletedConditionsOf(progress, quest.Id);
            var questName = QuestPayloadBuilder.ResolveQuestName(quest, quest.Id.ToString(), locale);

            // The quest's own map - declared, or derived from its zones - alias-expanded. Used only
            // as the fallback for a condition that names no zone of its own.
            var questKeys = facts.MapKeysOfQuest(quest, zoneToMap);

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!QuestPayloadBuilder.IsCarriedItemCondition(condition.ConditionType)) continue;

                // Already planted. Dropped HERE rather than flagged and sent: a flag is a second
                // thing the client has to remember to honour, and on the reference profile this
                // removes three of twenty-six rows - the difference between the measured "23
                // outstanding" and a list that still asks for beacons already in the ground.
                if (completed.Contains(condition.Id.ToString())) continue;

                var templates = QuestPayloadBuilder.TargetIds(condition.Target)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (templates.Count == 0) continue;

                // Scoped by THIS condition's zones, never by the location of the quest. Per-quest
                // scoping is what would put all 27 of "Is This a Reference"'s WI-FI cameras on
                // Factory and read MISSING 20 whatever you carried.
                var keys = facts.MapKeysOfCondition(condition, zoneToMap);
                var byEvidence = keys.Count > 0;
                if (!byEvidence) keys = questKeys;

                var row = new RaidCheckRequirementDto
                {
                    QuestId = quest.Id.ToString(),
                    QuestName = questName,
                    Templates = templates,
                    Name = QuestPayloadBuilder.ResolveItemName(templates[0], locale),
                    Needed = QuestFacts.RequiredCount(condition),
                    FoundInRaid = condition.OnlyFoundInRaid ?? false,
                    Status = status
                };

                foreach (var template in templates)
                {
                    if (!payload.Held.ContainsKey(template))
                        payload.Held[template] = HeldOf(owned, template);

                    // The names too, or the client cannot name a row after the template it holds.
                    if (!payload.ItemNames.ContainsKey(template))
                        payload.ItemNames[template] = QuestPayloadBuilder.ResolveItemName(template, locale);
                }

                if (keys.Count == 0)
                {
                    // No zone of its own and no map for its quest either. Reported, not attributed:
                    // exactly one vanilla condition is in this state on a fully harvested install,
                    // and using it as a gate would grey out every map permanently.
                    payload.ConditionsWithNoMap++;
                    continue;
                }

                foreach (var key in keys)
                {
                    if (!maps.TryGetValue(key, out var map)) continue;

                    map.Requirements.Add(row);

                    // Placed by assumption rather than by a harvested zone. The row still appears -
                    // over-listing costs a false amber, which is the safe direction - but the map
                    // can no longer go green, because the assumption may have put it on the wrong
                    // map.
                    if (!byEvidence) map.UnplaceableConditions++;
                }
            }
        }

        /// <summary>What the profile holds of one template, as an all-zero row when it holds none -
        /// so the client never has to tell "absent from the dictionary" from "holds none".</summary>
        private static HeldItemDto HeldOf(Dictionary<string, ProfileInventory.Held> owned, string template)
        {
            owned.TryGetValue(template, out var held);

            return new HeldItemDto
            {
                FoundInRaid = held.FoundInRaid,
                Total = held.Total,
                OnPerson = held.OnPerson,
                OnPersonFoundInRaid = held.OnPersonFoundInRaid,
                InStash = held.InStash,
                InTaskItems = held.InTaskItems
            };
        }

        /// <summary>GetPmcProfile throws rather than returning null on an empty session id, which is
        /// what an out-of-game request carries. Copied from ProfilePayloadBuilder specifically, not
        /// from the Kappa builder: that one returns BotBase, and QuestFacts needs PmcData.</summary>
        private PmcData? TryGetProfile(MongoId sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId.ToString())) return null;
                return profileHelper.GetPmcProfile(sessionId);
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: no profile for this session ({ex.Message}) - the raid check will be empty.");
                return null;
            }
        }
    }
}
