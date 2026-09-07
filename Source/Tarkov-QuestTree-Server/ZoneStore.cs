using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// The zone positions harvested from loaded raids, one file per map under
    /// user/mods/QuestTree/zones/. This is what makes an objective's zone id - which the quest
    /// data has always carried - into a place on the map, without asking any website.
    ///
    /// Harvests of one map are unioned by zone: Factory day and night share a file but not a
    /// scene (the night scene has zones the day one lacks, and mods spawn zones per variant), so
    /// a replace-whole file lost whichever variant was raided first. A zone seen again at a new
    /// position keeps both entries until the file is deleted - a moved zone after a game update
    /// is rare and visible; a lost zone was neither. Shipped seed files are read exactly like
    /// harvested ones, so a release can carry them and a later raid still adds to them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ZoneStore(ISptLogger<ZoneStore> logger)
    {
        /// <summary>Maps that load the same scene, so a harvest of one serves the other. The
        /// quest data keys them separately (different location ids), the triggers are identical.</summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["factory4_night"] = "factory4_day",
            ["Sandbox_high"] = "Sandbox"
        };

        /// <summary>The map name becomes a file name, so it is held to what a location id can be.</summary>
        private static readonly Regex SafeName = new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions FileOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        private readonly object _lock = new();

        /// <summary>Canonical map name -> file, or null once a map is known to have no file. Read
        /// once; Save replaces the entry.</summary>
        private readonly Dictionary<string, ZoneFile?> _cache = new(StringComparer.OrdinalIgnoreCase);

        private static string Folder =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "zones");

        public static string Canonical(string map) =>
            Aliases.TryGetValue(map, out var canonical) ? canonical : map;

        public static bool IsValidMapName(string? map) => map != null && SafeName.IsMatch(map);

        /// <summary>The harvest for a map, or null when none has been taken.</summary>
        public ZoneFile? TryGet(string map)
        {
            if (!IsValidMapName(map)) return null;

            var key = Canonical(map);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;

                var file = Read(key);
                _cache[key] = file;
                return file;
            }
        }

        /// <summary>Unions this harvest into the map's file. Returns what was written.</summary>
        public ZoneFile Save(ZoneHarvestRequest request)
        {
            var key = Canonical(request.Map);

            lock (_lock)
            {
                if (!_cache.TryGetValue(key, out var existing))
                    existing = Read(key);

                var triggers = new Dictionary<string, HarvestedTrigger>(StringComparer.Ordinal);
                var items = new Dictionary<string, HarvestedQuestItem>(StringComparer.Ordinal);

                foreach (var t in existing?.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in existing?.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                // Newest wins on an exact match; anything new is added.
                foreach (var t in request.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in request.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                var file = new ZoneFile
                {
                    Map = key,
                    HarvestedAt = DateTime.UtcNow.ToString("u"),
                    ClientVersion = request.ClientVersion ?? "",
                    Triggers = new List<HarvestedTrigger>(triggers.Values),
                    QuestItems = new List<HarvestedQuestItem>(items.Values)
                };

                try
                {
                    System.IO.Directory.CreateDirectory(Folder);
                    System.IO.File.WriteAllText(PathFor(key), JsonSerializer.Serialize(file, FileOptions));
                }
                catch (Exception ex)
                {
                    // Kept in memory regardless: this server session still benefits, and the next
                    // raid will try the disk again.
                    logger.Warning($"Quest Tracker: could not write zones/{key}.json ({ex.Message}) - kept for this session only.");
                }

                _cache[key] = file;

                var added = file.Triggers.Count - (existing?.Triggers.Count ?? 0);
                logger.Info(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"harvested on '{key}'" + (request.Map != key ? $" (as '{request.Map}')" : "") +
                    (existing != null ? $", {added} new" : "") + ".");

                return file;
            }
        }

        /// <summary>The same keys the client de-duplicates with, so the two sides agree on what
        /// "the same zone" means: id plus rounded position, since one zone can be several volumes.</summary>
        private static string TriggerKey(HarvestedTrigger t) => $"{t.Id}|{t.X:F0}|{t.Y:F0}|{t.Z:F0}";

        private static string ItemKey(HarvestedQuestItem i) =>
            string.IsNullOrEmpty(i.ItemId) ? $"{i.TemplateId}|{i.X:F0}|{i.Y:F0}|{i.Z:F0}" : i.ItemId;

        private static string PathFor(string key) => System.IO.Path.Combine(Folder, key + ".json");

        private ZoneFile? Read(string key)
        {
            try
            {
                var path = PathFor(key);
                if (!System.IO.File.Exists(path)) return null;

                var file = JsonSerializer.Deserialize<ZoneFile>(System.IO.File.ReadAllText(path), FileOptions);
                if (file == null) return null;

                logger.Info(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"known for '{key}' (harvested {file.HarvestedAt}).");

                return file;
            }
            catch (Exception ex)
            {
                // A corrupt file costs one map its harvested pins until the next raid there.
                logger.Warning($"Quest Tracker: could not read zones/{key}.json ({ex.Message}) - raid the map again to replace it.");
                return null;
            }
        }
    }
}
