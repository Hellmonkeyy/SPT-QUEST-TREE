using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// Quest objective locations from tarkov.dev.
    ///
    /// These coordinates exist nowhere in SPT's own data. Quest conditions carry a zoneId - 149
    /// LeaveItemAtLocation and 96 PlaceBeacon conditions across 223 distinct zones - but nothing
    /// offline turns one into a position: every one of Customs' 28 zone ids is absent from its
    /// base, statics, staticContainers, allExtracts and all 42MB of its looseLoot. The positions
    /// live in the Unity map bundles, which is why DynamicMaps can only place quest markers while a
    /// raid is loaded, reading the scene's own trigger objects.
    ///
    /// tarkov.dev publishes them, and its map data is the same data the map images are drawn from,
    /// so the coordinates land in the frame we already position against.
    ///
    /// FETCHED ON THE SERVER, NEVER THE CLIENT. SPT's client request handler is synchronous on
    /// Unity's main thread; a network call there freezes the game, which this mod has already done
    /// once with a five second payload build.
    ///
    /// CACHED TO DISK, so the fetch happens once and the mod is offline from then on. Every failure
    /// - no internet, API down, timeout, malformed reply - is non-fatal and simply leaves the map
    /// with its item-spawn markers.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class TarkovDevClient(ISptLogger<TarkovDevClient> logger)
    {
        private const string Endpoint = "https://api.tarkov.dev/graphql";
        private const string CacheFileName = "tarkovdev-quests.json";

        /// <summary>Generous, because this runs during server startup where nothing is waiting on
        /// it, and mean-spirited timeouts are how a slow connection turns into no data at all.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Only what is needed to place a pin: the task, its objectives, and each objective's zones
        /// and possible item locations. Asking for less than the schema offers keeps the response
        /// small and means a change to a field we do not read cannot break the fetch.
        /// </summary>
        private const string Query = """
        {
          tasks {
            id
            name
            objectives {
              id
              type
              description
              maps { id name normalizedName }
              ... on TaskObjectiveQuestItem {
                possibleLocations { map { id } positions { x y z } }
              }
              ... on TaskObjectiveBasic { zones { map { id } position { x y z } } }
              ... on TaskObjectiveItem { zones { map { id } position { x y z } } }
              ... on TaskObjectiveMark { zones { map { id } position { x y z } } }
              ... on TaskObjectiveShoot { zones { map { id } position { x y z } } }
              ... on TaskObjectiveUseItem { zones { map { id } position { x y z } } }
            }
          }
        }
        """;

        private static readonly JsonSerializerOptions CacheOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false
        };

        /// <summary>One objective's location: which map, and where on it. Positions are the raw game
        /// coordinates, the same frame the item-spawn markers use.</summary>
        public sealed class ObjectiveLocation
        {
            public string QuestId { get; set; } = "";
            public string QuestName { get; set; } = "";
            public string Description { get; set; } = "";

            /// <summary>tarkov.dev's own map id. Resolved to an SPT location by the caller.</summary>
            public string MapId { get; set; } = "";

            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
        }

        private List<ObjectiveLocation>? _locations;

        /// <summary>
        /// Every objective location known, from the disk cache if there is one and from the network
        /// otherwise. Returns an empty list rather than null when there are none to be had, so
        /// callers never have to distinguish "no data" from "failed".
        /// </summary>
        public async Task<IReadOnlyList<ObjectiveLocation>> GetLocationsAsync(
            CancellationToken cancellationToken)
        {
            if (_locations != null) return _locations;

            _locations = ReadCache() ?? await FetchAsync(cancellationToken) ?? new List<ObjectiveLocation>();
            return _locations;
        }

        private static string CachePath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", CacheFileName);

        private List<ObjectiveLocation>? ReadCache()
        {
            try
            {
                var path = CachePath;
                if (!System.IO.File.Exists(path)) return null;

                var cached = JsonSerializer.Deserialize<List<ObjectiveLocation>>(
                    System.IO.File.ReadAllText(path), CacheOptions);

                if (cached == null || cached.Count == 0) return null;

                logger.Info(
                    $"Quest Tracker: {cached.Count} quest objective locations from the cached " +
                    $"tarkov.dev data. Delete {CacheFileName} to refresh it.");

                return cached;
            }
            catch (Exception ex)
            {
                // A corrupt cache should cost a re-fetch, not the whole feature.
                logger.Warning($"Quest Tracker: could not read {CacheFileName} ({ex.Message}) - refetching.");
                return null;
            }
        }

        private void WriteCache(List<ObjectiveLocation> locations)
        {
            try
            {
                var path = CachePath;
                var folder = System.IO.Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);

                System.IO.File.WriteAllText(path, JsonSerializer.Serialize(locations, CacheOptions));
            }
            catch (Exception ex)
            {
                // Failing to cache costs a fetch next start, nothing more.
                logger.Warning($"Quest Tracker: could not write {CacheFileName} ({ex.Message}).");
            }
        }

        private async Task<List<ObjectiveLocation>?> FetchAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var http = new HttpClient { Timeout = Timeout };

                // Named so tarkov.dev can see who is calling; they run this for the community and an
                // anonymous client is a rude one.
                http.DefaultRequestHeaders.Add("User-Agent", $"SPT-QuestTree/{ModInfo.Version}");

                var body = JsonSerializer.Serialize(new { query = Query });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await http.PostAsync(Endpoint, content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.Info(
                        $"Quest Tracker: tarkov.dev returned {(int)response.StatusCode} - the map will " +
                        "show item spawns only. It will try again next start.");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var locations = Parse(json);

                if (locations.Count == 0)
                {
                    logger.Info("Quest Tracker: tarkov.dev returned no objective locations.");
                    return null;
                }

                logger.Info($"Quest Tracker: fetched {locations.Count} quest objective locations from tarkov.dev.");
                WriteCache(locations);

                return locations;
            }
            catch (Exception ex)
            {
                // Offline is the normal case for a lot of SPT installs, so this is Info, not Warning.
                logger.Info(
                    $"Quest Tracker: could not reach tarkov.dev ({ex.Message}) - the map will show " +
                    "item spawns only.");
                return null;
            }
        }

        /// <summary>
        /// Walks the reply with the DOM reader rather than mapping it to a type per objective shape.
        /// The union has fifteen members and we want one field from five of them; a reader that
        /// simply looks for "zones" and "possibleLocations" wherever they appear is both shorter and
        /// unbothered by tarkov.dev adding a sixteenth.
        /// </summary>
        private List<ObjectiveLocation> Parse(string json)
        {
            var locations = new List<ObjectiveLocation>();

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("tasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array)
            {
                return locations;
            }

            foreach (var task in tasks.EnumerateArray())
            {
                var questId = GetString(task, "id");
                if (string.IsNullOrEmpty(questId)) continue;

                var questName = GetString(task, "name");

                if (!task.TryGetProperty("objectives", out var objectives) ||
                    objectives.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var objective in objectives.EnumerateArray())
                {
                    var description = GetString(objective, "description");

                    if (objective.TryGetProperty("zones", out var zones) &&
                        zones.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var zone in zones.EnumerateArray())
                        {
                            AddPoint(locations, questId, questName, description,
                                MapIdOf(zone), Child(zone, "position"));
                        }
                    }

                    if (objective.TryGetProperty("possibleLocations", out var possible) &&
                        possible.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var place in possible.EnumerateArray())
                        {
                            var mapId = MapIdOf(place);

                            if (!place.TryGetProperty("positions", out var positions) ||
                                positions.ValueKind != JsonValueKind.Array)
                            {
                                continue;
                            }

                            foreach (var position in positions.EnumerateArray())
                                AddPoint(locations, questId, questName, description, mapId, position);
                        }
                    }
                }
            }

            return locations;
        }

        private static void AddPoint(
            List<ObjectiveLocation> into, string questId, string questName, string description,
            string mapId, JsonElement? position)
        {
            if (position == null || string.IsNullOrEmpty(mapId)) return;

            var point = position.Value;

            if (!point.TryGetProperty("x", out var x) ||
                !point.TryGetProperty("y", out var y) ||
                !point.TryGetProperty("z", out var z))
            {
                return;
            }

            into.Add(new ObjectiveLocation
            {
                QuestId = questId,
                QuestName = questName,
                Description = description,
                MapId = mapId,
                X = (float)x.GetDouble(),
                Y = (float)y.GetDouble(),
                Z = (float)z.GetDouble()
            });
        }

        private static string MapIdOf(JsonElement element) =>
            element.TryGetProperty("map", out var map) ? GetString(map, "id") : "";

        private static JsonElement? Child(JsonElement element, string name) =>
            element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
                ? child
                : null;

        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
    }
}
