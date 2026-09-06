using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// Quest objective locations, from TarkovTracker's objective_gps.json.
    ///
    /// These positions exist nowhere in SPT and nowhere in the game's own data files, which was
    /// established by looking rather than assumed: all 1,091 files and 0.8GB of SPT_Data contain
    /// none of the 217 distinctive quest zone ids outside quests.json itself; the location bases'
    /// hundred keys hold only bot zones and spawn points; the bundle manifest's 6,570 entries have
    /// no quest data; and a map's scene preset is a four-asset config object with no GameObjects in
    /// it. The server tells the client WHICH zone an objective uses and the client learns WHERE by
    /// finding that object in the loaded scene, so the position genuinely does not exist until a
    /// raid loads. That is why every other mod that pins quests does it in raid.
    ///
    /// This file is the exception: a small static list keyed by BSG objective id - the same id SPT
    /// puts on a quest condition - carrying the map, a floor name, and the position as a percentage
    /// across and down the map image.
    ///
    /// Percentages are a happy accident of the right kind: they need no game-to-map coordinate
    /// transform, only the rectangle the map image covers, which the client already knows.
    ///
    /// FETCHED ON THE SERVER, NEVER THE CLIENT, and cached to disk - SPT's client request handler is
    /// synchronous on Unity's main thread, and a network call there freezes the game, which this mod
    /// has already done once. Nothing is redistributed: the repository carries no licence, so the
    /// file is fetched into the user's own install and credited in the view.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ObjectiveGpsClient(ISptLogger<ObjectiveGpsClient> logger)
    {
        private const string Url =
            "https://raw.githubusercontent.com/TarkovTracker/tarkovdata/master/objective_gps.json";

        private const string CacheFileName = "objective-gps.json";

        /// <summary>Generous, because this runs during server startup where nothing waits on it.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>One objective's place on a map, exactly as the file states it.</summary>
        public sealed class ObjectivePlace
        {
            /// <summary>The map's BSG id, which is also SPT's location _Id.</summary>
            [JsonPropertyName("map")]
            public string Map { get; set; } = "";

            /// <summary>How far across the map image, 0-100.</summary>
            [JsonPropertyName("leftPercent")]
            public float LeftPercent { get; set; }

            /// <summary>How far down the map image, 0-100.</summary>
            [JsonPropertyName("topPercent")]
            public float TopPercent { get; set; }

            /// <summary>A floor name such as "Ground_Level" or "Second_Floor", which lines up with
            /// the layer names the map configs use.</summary>
            [JsonPropertyName("floor")]
            public string Floor { get; set; } = "";
        }

        private Dictionary<string, ObjectivePlace>? _places;

        /// <summary>
        /// Objective id -> where it is. Empty rather than null when there is nothing to be had, so
        /// callers never have to tell "no data" from "failed".
        /// </summary>
        public async Task<IReadOnlyDictionary<string, ObjectivePlace>> GetPlacesAsync(
            CancellationToken cancellationToken)
        {
            if (_places != null) return _places;

            _places = ReadCache()
                      ?? await FetchAsync(cancellationToken)
                      ?? new Dictionary<string, ObjectivePlace>();

            return _places;
        }

        private static string CachePath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", CacheFileName);

        private Dictionary<string, ObjectivePlace>? ReadCache()
        {
            try
            {
                var path = CachePath;
                if (!System.IO.File.Exists(path)) return null;

                var cached = Parse(System.IO.File.ReadAllText(path));
                if (cached == null || cached.Count == 0) return null;

                logger.Info(
                    $"Quest Tracker: {cached.Count} objective locations from the cached " +
                    $"{CacheFileName}. Delete it to refresh.");

                return cached;
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: could not read {CacheFileName} ({ex.Message}) - refetching.");
                return null;
            }
        }

        private async Task<Dictionary<string, ObjectivePlace>?> FetchAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout };
                http.DefaultRequestHeaders.Add("User-Agent", $"SPT-QuestTree/{ModInfo.Version}");

                var response = await http.GetAsync(Url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.Info(
                        $"Quest Tracker: objective locations returned {(int)response.StatusCode} - the " +
                        "map will show item spawns only, and will try again next start.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var places = Parse(json);

                if (places == null || places.Count == 0)
                {
                    logger.Info("Quest Tracker: the objective location file held nothing usable.");
                    return null;
                }

                logger.Info($"Quest Tracker: fetched {places.Count} objective locations from tarkovdata.");
                WriteCache(json);

                return places;
            }
            catch (Exception ex)
            {
                // Offline is the normal case for a lot of SPT installs, so this is Info, not Warning.
                logger.Info(
                    $"Quest Tracker: could not fetch objective locations ({ex.Message}) - the map will " +
                    "show item spawns only.");
                return null;
            }
        }

        /// <summary>Caches the file exactly as served, rather than a re-serialised copy, so what is
        /// on disk is always something that can be diffed against the source.</summary>
        private void WriteCache(string json)
        {
            try
            {
                var path = CachePath;
                var folder = System.IO.Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

                System.IO.File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                logger.Warning($"Quest Tracker: could not write {CacheFileName} ({ex.Message}).");
            }
        }

        private static Dictionary<string, ObjectivePlace>? Parse(string json) =>
            JsonSerializer.Deserialize<Dictionary<string, ObjectivePlace>>(json, Options);
    }
}
