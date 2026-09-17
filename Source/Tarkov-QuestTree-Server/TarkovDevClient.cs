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

        /// <summary>
        /// This runs during server startup, and SPT waits for it - so the budget is what a player
        /// with no internet pays on every boot, not just what a slow connection needs. Two tries of
        /// seven seconds with a short pause is at most ~15s, and a refused or unresolvable
        /// connection fails in well under one. It used to be a single 45s try, which offline was a
        /// silent 45s stall (90s with the other fetch behind it in series).
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(7);

        /// <summary>One client for the process, not one per attempt: each new HttpClient redid
        /// DNS and left a socket in TIME_WAIT. The buffer cap bounds what a slow-drip or runaway
        /// response can make this hold in memory; the real answer is a few hundred KB.</summary>
        private static readonly HttpClient Http = new()
        {
            Timeout = Timeout,
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };

        /// <summary>Remote JSON is parsed with a depth limit; the default is generous enough for
        /// a hostile document to be a stack exercise.</summary>
        private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 32 };
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1.5);
        private const int Attempts = 2;

        /// <summary>How much of an error body to put in the log. Enough to read the message
        /// ("GraphQL server unavailable. Try again later."), not enough to flood it.</summary>
        private const int LoggedBodyLength = 300;

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
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var (locations, retry) = await FetchOnceAsync(attempt, cancellationToken);
                if (locations != null) return locations;
                if (!retry || attempt == Attempts) break;

                await Task.Delay(RetryDelay, cancellationToken);
            }

            logger.Info("Quest Tracker: no tarkov.dev data - the map will show item spawns only. It will try again next start.");
            return null;
        }

        /// <summary>One try. The flag says whether another is worth making: an outage or a timeout
        /// is, a rejected query is not.</summary>
        private async Task<(List<ObjectiveLocation>? Locations, bool Retry)> FetchOnceAsync(
            int attempt, CancellationToken cancellationToken)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { query = Query });
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                // Named so tarkov.dev can see who is calling; they run this for the community and an
                // anonymous client is a rude one.
                request.Headers.TryAddWithoutValidation("User-Agent", $"SPT-QuestTree/{ModInfo.Version}");

                using var response = await Http.SendAsync(request, cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    // The body is the diagnosis. A bare "422" once hid an upstream outage
                    // ("GraphQL server unavailable") behind what looked like a bad query.
                    var status = (int)response.StatusCode;
                    logger.Info(
                        $"Quest Tracker: tarkov.dev returned {status} on attempt {attempt}/{Attempts}: " +
                        Excerpt(json));

                    return (null, IsTransient(status));
                }

                var locations = Parse(json);

                if (locations.Count == 0)
                {
                    logger.Info("Quest Tracker: tarkov.dev returned no objective locations.");
                    return (null, false);
                }

                logger.Info($"Quest Tracker: fetched {locations.Count} quest objective locations from tarkov.dev.");
                WriteCache(locations);

                return (locations, false);
            }
            catch (Exception ex)
            {
                // Offline is the normal case for a lot of SPT installs, so this is Info, not Warning.
                logger.Info(
                    $"Quest Tracker: could not reach tarkov.dev on attempt {attempt}/{Attempts} ({ex.Message}).");
                return (null, true);
            }
        }

        /// <summary>Server-side trouble and rate limiting pass; a client error is ours to fix and
        /// a retry would only repeat it. 422 is included because tarkov.dev answers an outage with
        /// it rather than a 5xx.</summary>
        private static bool IsTransient(int status) => status >= 500 || status == 422 || status == 429;

        private static string Excerpt(string body)
        {
            var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= LoggedBodyLength ? text : text[..LoggedBodyLength] + "...";
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

            using var document = JsonDocument.Parse(json, DocumentOptions);

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
