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
            MapMarkerPayloadBuilder markerBuilder)
            : base(jsonUtil, BuildRoutes(logger, payloadBuilder, kappaBuilder, profileBuilder, markerBuilder))
        {
        }

        private static readonly JsonSerializerOptions FallbackOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static IEnumerable<RouteAction> BuildRoutes(
            ISptLogger<QuestTreeRouter> logger, QuestPayloadBuilder payloadBuilder,
            KappaPayloadBuilder kappaBuilder, ProfilePayloadBuilder profileBuilder,
            MapMarkerPayloadBuilder markerBuilder) =>
            new List<RouteAction>
            {
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
