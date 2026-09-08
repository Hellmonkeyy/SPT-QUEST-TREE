using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Utils;

namespace QuestTreeServer
{
    /// <summary>
    /// Serves the full quest list to the Quest Tracker client mod on /questtree/quests.
    ///
    /// TypePriority is OnLoadOrder.Routers + 1 because this is a brand new URL rather than an
    /// override of one SPT already answers - per the wiki, a custom route always sits above the
    /// built-in routers (which all sit on OnLoadOrder.Routers exactly), since there is nothing of
    /// SPT's to order against.
    ///
    /// The route list has to be handed to the base constructor, so the payload builder is passed
    /// through a static helper rather than captured from an instance field - the same shape
    /// mpstark-dynamicmaps uses for its own /dynamicmaps/load route.
    /// </summary>
    [Injectable(TypePriority = OnLoadOrder.Routers + 1)]
    public class QuestTreeRouter : StaticRouter
    {
        public QuestTreeRouter(
            JsonUtil jsonUtil, ISptLogger<QuestTreeRouter> logger, QuestPayloadBuilder payloadBuilder,
            KappaPayloadBuilder kappaBuilder, ProfilePayloadBuilder profileBuilder,
            MapMarkerPayloadBuilder markerBuilder, ZoneStore zoneStore)
            : base(jsonUtil, BuildRoutes(logger, payloadBuilder, kappaBuilder, profileBuilder, markerBuilder, zoneStore))
        {
        }

        /// <summary>A sanity ceiling on a harvest, not a real limit: Customs has a few hundred
        /// triggers. Anything past this is not a raid, it is a bug or a prank.</summary>
        private const int MaxHarvestEntries = 20_000;

        private static readonly JsonSerializerOptions FallbackOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static IEnumerable<RouteAction> BuildRoutes(
            ISptLogger<QuestTreeRouter> logger, QuestPayloadBuilder payloadBuilder,
            KappaPayloadBuilder kappaBuilder, ProfilePayloadBuilder profileBuilder,
            MapMarkerPayloadBuilder markerBuilder, ZoneStore zoneStore) =>
            new List<RouteAction>
            {
                // The one route with a body: the client's in-raid zone harvest (see ZoneHarvester
                // in the client half). SPT deserializes the body into ZoneHarvestRequest for us.
                // Rebuilding the markers here is deliberate - the client posts fire-and-forget,
                // so this is the one request nobody is waiting on.
                new RouteAction<ZoneHarvestRequest>(
                    "/questtree/zones",
                    (url, request, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => AcceptHarvest(logger, request, zoneStore, markerBuilder),
                            () => new ZoneHarvestResponse { Ok = false, Message = "failed" })),

                new RouteAction<EmptyRequestData>(
                    "/questtree/quests",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, payloadBuilder.GetPayloadJson, () => new QuestPayloadDto())),

                // Profile-scoped and rebuilt per request - the stash changes every raid, so unlike
                // the quest list there is nothing here worth caching.
                new RouteAction<EmptyRequestData>(
                    "/questtree/kappa",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => kappaBuilder.GetPayloadJson(sessionId), () => new KappaPayloadDto())),

                // Also profile-scoped and per-request: level, loyalty and objective counters all
                // move as the player plays, so there is nothing here worth caching server-side.
                new RouteAction<EmptyRequestData>(
                    "/questtree/profile",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => profileBuilder.GetPayloadJson(sessionId), () => new ProfilePayloadDto())),

                // Not profile-scoped: where an item spawns is a property of the map, the same for
                // everyone, so this is built once and cached like the quest list.
                new RouteAction<EmptyRequestData>(
                    "/questtree/mapmarkers",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, markerBuilder.GetPayloadJson,
                            () => new MapMarkerPayloadDto { Version = ModInfo.Version }))
            };

        /// <summary>Rejections already logged this boot, by map and reason, so a client that
        /// keeps sending the same bad harvest is heard once rather than filling the log.</summary>
        private static readonly HashSet<string> RejectionsLogged = new(StringComparer.Ordinal);

        private static string AcceptHarvest(
            ISptLogger<QuestTreeRouter> logger, ZoneHarvestRequest? request, ZoneStore zoneStore,
            MapMarkerPayloadBuilder markerBuilder)
        {
            static string Reply(ZoneHarvestResponse r) => JsonSerializer.Serialize(r, FallbackOptions);

            string Reject(string reason)
            {
                var key = $"{request?.Map ?? "?"}|{reason}";
                bool first;
                lock (RejectionsLogged) first = RejectionsLogged.Add(key);
                if (first) logger.Warning($"Quest Tracker: refused a zone harvest for '{request?.Map ?? "?"}' - {reason}.");
                return Reply(new ZoneHarvestResponse { Ok = false, Message = reason });
            }

            if (request == null) return Reject("no body");
            if (!ZoneStore.IsValidMapName(request.Map)) return Reject("bad map name");

            // Entries the file could not hold or the map could not draw go first, so the counts
            // below describe what will actually be kept.
            var dropped = ZoneStore.Sanitise(request);

            var count = (request.Triggers?.Count ?? 0) + (request.QuestItems?.Count ?? 0);
            if (count == 0) return Reject(dropped > 0 ? "nothing usable harvested" : "nothing harvested");
            if (count > MaxHarvestEntries) return Reject("too many entries");

            var saved = zoneStore.Save(request);
            if (saved == null) return Reject("map file full");

            markerBuilder.Rebuild();

            return Reply(new ZoneHarvestResponse
            {
                Ok = true,
                Zones = saved.Triggers.Count,
                QuestItems = saved.QuestItems.Count,
                Message = dropped > 0 ? $"saved; {dropped} unusable entries dropped" : "saved"
            });
        }

        /// <summary>
        /// The builders guard their own loops, so a throw reaching here is one nobody predicted -
        /// and SPT's request pipeline has no guard of its own, so it would surface as an unhandled
        /// error on the client's synchronous fetch. Answering with the route's empty-but-valid
        /// shape instead lets the client degrade the way it already does for a missing server.
        /// </summary>
        private static ValueTask<string> Guarded<T>(
            ISptLogger<QuestTreeRouter> logger, string route, Func<string> build, Func<T> fallback)
        {
            try
            {
                return new ValueTask<string>(build());
            }
            catch (Exception ex)
            {
                logger.Error($"Quest Tracker: {route} failed - answering with an empty payload: {ex}");
                return new ValueTask<string>(JsonSerializer.Serialize(fallback(), FallbackOptions));
            }
        }
    }
}
