using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Locales;

namespace QuestTreeServer
{
    /// <summary>
    /// Builds the Kappa container checklist: the Collector quest's hand-in items, each paired with
    /// what the player's profile actually holds.
    ///
    /// Every part of this is read from live data rather than a hardcoded list, so it stays correct
    /// across wipes and with quest mods installed. Only the Collector quest ID is a constant, and
    /// even that falls back to a name match.
    ///
    /// Deliberately NOT cached, unlike <see cref="QuestPayloadBuilder"/>: the quest database is
    /// fixed for the life of the server, but the player's stash changes every raid.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class KappaPayloadBuilder(
        ISptLogger<KappaPayloadBuilder> logger,
        TemplateTable templateTable,
        LocaleService localeService,
        ProfileHelper profileHelper)
    {
        /// <summary>The Collector quest, whose completion awards the Kappa container. Constant
        /// because it is the same id in every vanilla install; a name match covers the case where a
        /// mod has replaced it.</summary>
        private const string CollectorQuestId = "5c51aac186f77432ea65c552";

        private const string CollectorQuestName = "Collector";

        /// <summary>Condition type naming another quest as a prerequisite.</summary>
        private const string QuestConditionType = "Quest";


        /// <summary>Guards the two fields below. This is a DI singleton serving concurrent HTTP
        /// requests, and while the unguarded race was benign - two callers would at worst each read
        /// the same file once and reach the same answer - it read as an oversight next to
        /// <see cref="QuestPayloadBuilder"/>, which locks the identical pattern. Consistency, not a
        /// fix for an observed bug.</summary>
        private readonly object _canonicalLock = new();

        /// <summary>The canonical Kappa list, cached after the first successful read. The shipped
        /// database cannot change while the server runs.</summary>
        private List<string>? _canonicalKappaQuestIds;

        /// <summary>Set once the database read has failed, so a missing or malformed file is not
        /// re-read and re-logged on every Kappa request.</summary>
        private bool _canonicalReadFailed;

        public string GetPayloadJson(MongoId sessionId) =>
            JsonSerializer.Serialize(Build(sessionId), WireJson.Options);

        private KappaPayloadDto Build(MongoId sessionId)
        {
            var payload = new KappaPayloadDto();

            var collector = FindCollectorQuest();
            if (collector == null)
            {
                logger.Warning("Quest Tracker: no Collector quest found - the Kappa checklist will be empty.");
                return payload;
            }

            payload.CollectorFound = true;
            payload.CollectorQuestId = collector.Id.ToString();

            payload.LiveCollectorPrerequisiteCount = CountLivePrerequisites(collector);

            var canonical = GetCanonicalKappaQuestIds(collector.Id.ToString());
            if (canonical != null)
            {
                payload.KappaQuestIds = canonical;
                payload.KappaSource = "database";
            }
            else
            {
                // Falling back to the live table means the list may have been trimmed by another
                // mod; the client is told which source it got so it can say so rather than
                // presenting a short list as if it were the real Kappa requirement.
                payload.KappaQuestIds = CollectLivePrerequisiteIds(collector);
                payload.KappaSource = "live";
            }

            var locale = localeService.GetLocaleDb();
            var profile = TryGetProfile(sessionId);

            // A missing profile is not fatal - the checklist itself is still worth returning, just
            // with nothing owned.
            var owned = ProfileInventory.CountByTemplate(profile);
            var completedConditions = GetCompletedConditions(profile, collector.Id);
            payload.CollectorStatus = GetQuestStatus(profile, collector.Id);

            var conditions = collector.Conditions?.AvailableForFinish;
            if (conditions == null) return payload;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;

                // Per condition: one malformed Collector condition used to empty the whole
                // checklist, which the client then reported as "server half missing".
                try
                {
                    if (!QuestPayloadBuilder.IsItemCondition(condition.ConditionType)) continue;

                    var templates = QuestPayloadBuilder.TargetIds(condition.Target).ToList();
                    if (templates.Count == 0) continue;

                    // A condition can accept any one of several templates (rare, but it happens);
                    // ownership is the sum across all of them, and the name comes from the first.
                    var foundInRaid = 0;
                    var total = 0;

                    foreach (var template in templates)
                    {
                        if (!owned.TryGetValue(template, out var counts)) continue;
                        foundInRaid += counts.FoundInRaid;
                        total += counts.Total;
                    }

                    payload.Items.Add(new KappaItemDto
                    {
                        ConditionId = condition.Id.ToString(),
                        Template = templates[0],
                        Name = QuestPayloadBuilder.ResolveItemName(templates[0], locale),
                        Required = Math.Max(1, Numbers.ToCount(condition.Value, 1)),
                        OwnedFoundInRaid = foundInRaid,
                        OwnedTotal = total,
                        HandedIn = completedConditions.Contains(condition.Id.ToString())
                    });
                }
                catch (Exception ex)
                {
                    logger.Warning($"Quest Tracker: skipped a Collector condition ({condition.Id}): {ex.Message}");
                }
            }

            logger.Debug(
                $"Quest Tracker: Kappa checklist built - {payload.Items.Count} items, " +
                $"{payload.Items.Count(i => i.HandedIn)} already handed in.");

            return payload;
        }

        /// <summary>The Kappa quest list read from SPT's shipped database, or null when that file
        /// cannot be read. Cached after the first read - the file cannot change while the server is
        /// running, and this is only reached from the Kappa route.</summary>
        private List<string>? GetCanonicalKappaQuestIds(string collectorId)
        {
            if (_canonicalKappaQuestIds != null) return _canonicalKappaQuestIds;
            if (_canonicalReadFailed) return null;

            lock (_canonicalLock)
            {
                if (_canonicalKappaQuestIds != null) return _canonicalKappaQuestIds;
                if (_canonicalReadFailed) return null;

                _canonicalKappaQuestIds = ReadCanonicalKappaQuestIds(collectorId);
                _canonicalReadFailed = _canonicalKappaQuestIds == null;
                return _canonicalKappaQuestIds;
            }
        }

        /// <summary>The one read of the shipped database, or null when it cannot be used. Caching
        /// and its flags are the caller's job, so this stays a plain read with nothing to unwind if
        /// it throws part way through.</summary>
        private List<string>? ReadCanonicalKappaQuestIds(string collectorId)
        {
            try
            {
                // AppContext.BaseDirectory is the server's own folder, so this resolves without any
                // path being hardcoded to a particular install.
                // Fully qualified: SPT has its own Path model type in Models.Eft.Common.Tables.
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "SPT_Data", "database", "templates", "quests.json");

                if (!File.Exists(path))
                {
                    logger.Warning($"Quest Tracker: {path} not found - falling back to the live Collector prerequisites for the Kappa list.");
                    return null;
                }

                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);

                if (!document.RootElement.TryGetProperty(collectorId, out var collector) ||
                    !collector.TryGetProperty("conditions", out var conditions) ||
                    !conditions.TryGetProperty("AvailableForStart", out var availableForStart))
                {
                    logger.Warning("Quest Tracker: the shipped quests.json has no Collector start conditions - falling back to the live table.");
                    return null;
                }

                var ids = new List<string>();

                foreach (var condition in availableForStart.EnumerateArray())
                {
                    if (!condition.TryGetProperty("conditionType", out var type)) continue;
                    if (!string.Equals(type.GetString(), QuestConditionType, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!condition.TryGetProperty("target", out var target)) continue;

                    // target is a single id here, but the schema allows a list - handle both rather
                    // than assuming, since this is the same shape TargetIds deals with elsewhere.
                    if (target.ValueKind == JsonValueKind.String)
                    {
                        var id = target.GetString();
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
                    else if (target.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in target.EnumerateArray())
                        {
                            var id = entry.GetString();
                            if (!string.IsNullOrEmpty(id)) ids.Add(id);
                        }
                    }
                }

                logger.Info($"Quest Tracker: Kappa quest list read from the shipped database - {ids.Count} quests.");
                return ids;
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: could not read the Kappa quest list from the shipped database ({ex.Message}) - using the live table instead.");
                return null;
            }
        }

        private static int CountLivePrerequisites(Quest collector) => CollectLivePrerequisiteIds(collector).Count;

        private static List<string> CollectLivePrerequisiteIds(Quest collector)
        {
            var ids = new List<string>();

            var conditions = collector.Conditions?.AvailableForStart;
            if (conditions == null) return ids;

            foreach (var condition in conditions)
            {
                if (condition == null) continue;
                if (!string.Equals(condition.ConditionType, QuestConditionType, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var target in QuestPayloadBuilder.TargetIds(condition.Target))
                {
                    if (!string.IsNullOrWhiteSpace(target)) ids.Add(target);
                }
            }

            return ids;
        }

        /// <summary>
        /// The player's profile, or null if there isn't one to read.
        ///
        /// ProfileHelper.GetPmcProfile THROWS on an empty session id ("session id provided was
        /// empty, did you restart the server while the game was running?") rather than returning
        /// null, and an empty id is exactly what arrives for a request made outside a game session.
        /// The checklist is still worth serving in that case, just with nothing marked as owned, so
        /// this degrades instead of failing the whole route.
        /// </summary>
        private BotBase? TryGetProfile(MongoId sessionId)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionId.ToString())) return null;
                return profileHelper.GetPmcProfile(sessionId);
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: no profile for this session ({ex.Message}) - item ownership will read as none.");
                return null;
            }
        }

        private Quest? FindCollectorQuest()
        {
            var quests = templateTable.Quests;
            if (quests == null) return null;

            if (quests.TryGetValue(new MongoId(CollectorQuestId), out var byId) && byId != null) return byId;

            return quests.Values.FirstOrDefault(q =>
                q != null &&
                string.Equals(q.QuestName, CollectorQuestName, StringComparison.OrdinalIgnoreCase));
        }


        /// <summary>Condition ids the player has already satisfied on the Collector quest. Empty
        /// when the quest has not been accepted, which is the normal case for most of a wipe.</summary>
        private static HashSet<string> GetCompletedConditions(BotBase? profile, MongoId questId)
        {
            var status = profile?.Quests?.FirstOrDefault(q => q.QId == questId);

            return status?.CompletedConditions == null
                ? new HashSet<string>()
                : new HashSet<string>(status.CompletedConditions);
        }

        private static string GetQuestStatus(BotBase? profile, MongoId questId)
        {
            var status = profile?.Quests?.FirstOrDefault(q => q.QId == questId);
            return status == null ? QuestStatusEnum.Locked.ToString() : status.Status.ToString();
        }
    }
}
