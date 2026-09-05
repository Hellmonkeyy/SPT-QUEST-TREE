using System.Collections.Generic;
using System.Threading.Tasks;
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
            JsonUtil jsonUtil, QuestPayloadBuilder payloadBuilder, KappaPayloadBuilder kappaBuilder)
            : base(jsonUtil, BuildRoutes(payloadBuilder, kappaBuilder))
        {
        }

        private static IEnumerable<RouteAction> BuildRoutes(
            QuestPayloadBuilder payloadBuilder, KappaPayloadBuilder kappaBuilder) =>
            new List<RouteAction>
            {
                new RouteAction<EmptyRequestData>(
                    "/questtree/quests",
                    (url, info, sessionId, output, cancellationToken) =>
                        new ValueTask<string>(payloadBuilder.GetPayloadJson())),

                // Profile-scoped and rebuilt per request - the stash changes every raid, so unlike
                // the quest list there is nothing here worth caching.
                new RouteAction<EmptyRequestData>(
                    "/questtree/kappa",
                    (url, info, sessionId, output, cancellationToken) =>
                        new ValueTask<string>(kappaBuilder.GetPayloadJson(sessionId)))
            };
    }
}
