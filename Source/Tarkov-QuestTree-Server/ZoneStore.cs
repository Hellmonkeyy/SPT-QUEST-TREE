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
    /// A file is a snapshot of one raid's scene and is replaced whole by the next harvest of that
    /// map: zones do not accumulate across game versions, they move. Shipped seed files are read
    /// exactly like harvested ones, so a release can carry them and a later raid still wins.
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

        /// <summary>Replaces the map's file with this harvest. Returns what was written.</summary>
        public ZoneFile Save(ZoneHarvestRequest request)
        {
            var key = Canonical(request.Map);

            var file = new ZoneFile
            {
                Map = key,
                HarvestedAt = DateTime.UtcNow.ToString("u"),
                ClientVersion = request.ClientVersion ?? "",
                Triggers = request.Triggers ?? new List<HarvestedTrigger>(),
                QuestItems = request.QuestItems ?? new List<HarvestedQuestItem>()
            };

            lock (_lock)
            {
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
            }

            logger.Info(
                $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                $"harvested on '{key}'" + (request.Map != key ? $" (as '{request.Map}')" : "") + ".");

            return file;
        }

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
