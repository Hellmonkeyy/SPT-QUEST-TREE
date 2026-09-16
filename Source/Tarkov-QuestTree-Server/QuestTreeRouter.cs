using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Common;
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
            MapMarkerPayloadBuilder markerBuilder, ZoneStore zoneStore, QuestFacts facts,
            RaidCheckPayloadBuilder raidCheckBuilder, ProfileBuilds profileBuilds,
            WeaponPresetWriter presetWriter)
            : base(jsonUtil, BuildRoutes(
                jsonUtil, logger, payloadBuilder, kappaBuilder, profileBuilder, markerBuilder, zoneStore,
                facts, raidCheckBuilder, profileBuilds, presetWriter))
        {
        }

        /// <summary>A sanity ceiling on a harvest, not a real limit: Customs has a few hundred
        /// triggers. Anything past this is not a raid, it is a bug or a prank.</summary>
        private const int MaxHarvestEntries = 20_000;

        private static IEnumerable<RouteAction> BuildRoutes(
            JsonUtil jsonUtil, ISptLogger<QuestTreeRouter> logger, QuestPayloadBuilder payloadBuilder,
            KappaPayloadBuilder kappaBuilder, ProfilePayloadBuilder profileBuilder,
            MapMarkerPayloadBuilder markerBuilder, ZoneStore zoneStore, QuestFacts facts,
            RaidCheckPayloadBuilder raidCheckBuilder, ProfileBuilds profileBuilds,
            WeaponPresetWriter presetWriter) =>
            new List<RouteAction>
            {
                // Profile-scoped: the weapon builds as THIS player can assemble them. Answered from what
                // the background pass has already worked out - never solved inside the request, which the
                // client makes synchronously on the game's main thread - and a stale or missing answer
                // says so rather than blocking.
                new RouteAction<EmptyRequestData>(
                    "/questtree/builds",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => profileBuilds.GetPayloadJson(sessionId),
                            () => new ProfileBuildsDto())),

                // The one route with a body: the client's in-raid zone harvest (see ZoneHarvester
                // in the client half). SPT deserializes the body into ZoneHarvestRequest for us.
                // Rebuilding the markers here is deliberate - the client posts fire-and-forget,
                // so this is the one request nobody is waiting on.
                new RouteAction<ZoneHarvestRequest>(
                    "/questtree/zones",
                    (url, request, sessionId, output, cancellationToken) =>
                        Guarded(logger, url,
                            () => AcceptHarvest(logger, request, zoneStore, markerBuilder, payloadBuilder, facts),
                            () => new ZoneHarvestResponse { Ok = false, Message = "failed" })),

                // Writes one solved build into the player's own saved weapon builds, so the game's
                // modding screen can load it - and offer to buy what is missing, which is the part we
                // were never going to do well ourselves.
                //
                // On demand, per quest, because the alternative is thirty-two presets appearing in a
                // list the player owns without being asked.
                new RouteAction<SavePresetRequest>(
                    "/questtree/build/save",
                    (url, request, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => SavePreset(jsonUtil, profileBuilds, presetWriter, sessionId, request),
                            () => new SavePresetResponse { Reason = "the server could not save the preset" })),

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

                // Profile-scoped and per-request, for the same reason as /questtree/profile: the
                // answer changes every time the player moves an item, which is precisely what this
                // is asked about. No request body - RouteAction<T> constrains T to IRequestData, the
                // client has no POST path outside the fire-and-forget harvest, and every map fits in
                // one small answer anyway.
                new RouteAction<EmptyRequestData>(
                    "/questtree/raidcheck",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, () => raidCheckBuilder.GetPayloadJson(sessionId),
                            () => new RaidCheckDto())),

                // Not profile-scoped: where an item spawns is a property of the map, the same for
                // everyone, so this is built once and cached like the quest list.
                new RouteAction<EmptyRequestData>(
                    "/questtree/mapmarkers",
                    (url, info, sessionId, output, cancellationToken) =>
                        Guarded(logger, url, markerBuilder.GetPayloadJson,
                            () => new MapMarkerPayloadDto { Version = ModInfo.Version }))
            };

        /// <summary>Rejections already logged this boot, by map and reason, so a client that
        /// keeps sending the same bad harvest is heard once rather than filling the log. Keyed on
        /// the canonical map only once it has passed validation, and capped: the raw string is
        /// the client's to choose, and a set keyed on it would grow with every new bad name.</summary>
        private static readonly HashSet<string> RejectionsLogged = new(StringComparer.Ordinal);
        private const int MaxRejectionsLogged = 256;

        /// <summary>Save one build as a preset, and say plainly why not when it cannot.
        ///
        /// Reads the answer the background pass already worked out rather than solving here: the tree
        /// it holds is the build this player can actually assemble, which is the one worth writing, and
        /// solving inside a request is what froze the game once already.</summary>
        private static string SavePreset(
            JsonUtil jsonUtil, ProfileBuilds profileBuilds, WeaponPresetWriter presetWriter,
            MongoId sessionId, SavePresetRequest? request)
        {
            static string Reply(SavePresetResponse r) => JsonSerializer.Serialize(r, WireJson.Options);

            if (request == null || string.IsNullOrWhiteSpace(request.Key))
                return Reply(new SavePresetResponse { Reason = "no build was named" });

            var build = profileBuilds.Find(sessionId, request.Key);

            if (build == null)
                return Reply(new SavePresetResponse
                {
                    Reason = "your builds are still being worked out - try again in a moment"
                });

            if (build.Tree == null || build.Tree.Count == 0)
                return Reply(new SavePresetResponse { Reason = "there is no build for this quest to save" });

            if (!build.WeaponTemplate.TryParseMongoId(out var weapon))
                return Reply(new SavePresetResponse { Reason = "this quest's weapon could not be identified" });

            var outcome = presetWriter.Save(sessionId, build.QuestName, weapon, build.Tree)
                .GetAwaiter().GetResult();

            var reply = new SavePresetResponse
            {
                Saved = outcome.Saved,
                Name = outcome.Name,
                Reason = outcome.Reason,
                Id = outcome.Id.ToString()
            };

            if (outcome.Saved && outcome.Items.Count > 0)
            {
                reply.Root = outcome.Items[0].Id.ToString();

                // SPT's serializer, not ours, and the distinction was worth a released bug. TWO of them,
                // stacked, which is the part worth reading before "simplifying" this back.
                //
                // The first is the ids. MongoId is a struct whose only public property is IsEmpty, and
                // its string form comes from StringToMongoIdConverter, which SPT registers in its
                // OPTIONS rather than hanging on the type. WireJson.Options registers no converters, so
                // every _id and _tpl serialised as {"isEmpty":false} - not mis-shaped, ABSENT - and the
                // client's Newtonsoft threw on the first one.
                //
                // The second was hidden behind that throw. Item pins its own wire names with
                // JsonPropertyName, so those survived any naming policy - but the nested Upd does NOT
                // pin its, so WireJson's camelCase renamed the lot: "repairable", "spawnedInSession".
                // The client hands upd through verbatim as a raw JToken, and the game reads it in
                // ItemDeserializer.CreateItem through a CASE-SENSITIVE member lookup. So even with the
                // ids fixed in place, every preset would have arrived with no durability, no fire mode
                // and no spawned-in-session flag - silently, which is the exact defect sending JSON at
                // all was meant to end. Adding one converter to WireJson.Options is therefore NOT the
                // smaller version of this fix; it is the half of it that fails quietly.
                //
                // The injected instance, never JsonUtil's public static options: those are nullable and
                // only filled once a JsonUtil has been constructed. Serializing this way also reproduces
                // what SPT itself wrote into the profile - SaveServer serialises with the same JsonUtil -
                // down to parentId being OMITTED on the root rather than null, which is the shape the
                // game's own /client/builds answer has and the shape the client's reader was built for.
                reply.ItemsJson = jsonUtil.Serialize(outcome.Items) ?? "";
            }

            return Reply(reply);
        }

        private static string AcceptHarvest(
            ISptLogger<QuestTreeRouter> logger, ZoneHarvestRequest? request, ZoneStore zoneStore,
            MapMarkerPayloadBuilder markerBuilder, QuestPayloadBuilder payloadBuilder, QuestFacts facts)
        {
            static string Reply(ZoneHarvestResponse r) => JsonSerializer.Serialize(r, WireJson.Options);

            string Reject(string reason, bool mapIsValid)
            {
                var map = mapIsValid ? ZoneStore.Canonical(request!.Map) : "?";
                var key = $"{map}|{reason}";
                bool first;
                lock (RejectionsLogged)
                    first = RejectionsLogged.Count < MaxRejectionsLogged && RejectionsLogged.Add(key);
                if (first) logger.Warning($"Quest Tracker: refused a zone harvest for '{map}' - {reason}.");
                return Reply(new ZoneHarvestResponse { Ok = false, Message = reason });
            }

            if (request == null) return Reject("no body", mapIsValid: false);

            // Two checks, AND-ed, because they guard different things. IsValidMapName guards the
            // FILE - the regex and the reserved-name set stop a harvest escaping the zones folder
            // or naming a Windows device such as NUL - and cannot be replaced by the table check,
            // since "Private Area" is a real location whose name the regex rejects.
            if (!ZoneStore.IsValidMapName(request.Map)) return Reject("bad map name", mapIsValid: false);

            // And this one guards the ANSWER. On Fika the zones route is unauthenticated HTTP that
            // every peer can post to, and the derived-location index treats harvested data as the
            // authority on which map a zone is on. A peer posting a real quest's zone ids under an
            // invented map name would otherwise add a map to that quest for everyone.
            //
            // It also bounds the store to the ~17 canonical names by construction, which is what
            // makes a file-count cap and an eviction policy unnecessary rather than merely
            // unwritten. Built from the location table's own internal names, never from
            // questConfig.LocationIdMap - that map has no Labyrinth entry, which is why
            // QuestPayloadBuilder stopped using it.
            if (!facts.IsRealLocation(request.Map)) return Reject("unknown map", mapIsValid: false);

            // Entries the file could not hold or the map could not draw go first, so the counts
            // below describe what will actually be kept.
            var dropped = ZoneStore.Sanitise(request);

            var count = (request.Triggers?.Count ?? 0) + (request.QuestItems?.Count ?? 0);
            if (count == 0) return Reject(dropped > 0 ? "nothing usable harvested" : "nothing harvested", mapIsValid: true);
            if (count > MaxHarvestEntries) return Reject("too many entries", mapIsValid: true);

            // Any harvest buffered by an earlier window whose time has come, applied first so a
            // later post is what flushes an earlier one. No timer to own, and nothing is lost to
            // the clock.
            var drained = zoneStore.DrainPending();

            var saved = zoneStore.SaveOrBuffer(request, out var added, out var buffered);

            if (saved == null && buffered)
            {
                // Truthful counts, not zeros: ZoneHarvester logs them, and a zero there reads as
                // "the harvest was rejected" in the one log file the verify pass says to read.
                var onDisk = zoneStore.TryGet(request.Map);
                var wait = Math.Max(1, zoneStore.PendingSeconds(request.Map));

                if (drained)
                {
                    markerBuilder.Rebuild();
                    payloadBuilder.Rebuild();
                }

                return Reply(new ZoneHarvestResponse
                {
                    Ok = true,
                    Zones = onDisk?.Triggers.Count ?? 0,
                    QuestItems = onDisk?.QuestItems.Count ?? 0,
                    Message = "buffered; this map was written recently",
                    RebuildInSeconds = wait
                });
            }

            if (saved == null) return Reject("map file full", mapIsValid: true);

            // Every Fika client in a raid harvests the same scene and posts it; only the first
            // has anything new, and only new positions are a reason to rebuild every map's
            // markers - the multi-second read the class comment on the builder describes.
            //
            // BOTH payloads, since 1.9.0. New zones change which map an "any"-location quest is
            // derived onto, so rebuilding only the markers would give such a quest its pins while
            // its map on the client stayed empty until the next server restart - the "in the list
            // with no pins, or the reverse" split the derivation exists to prevent.
            if (added > 0 || drained)
            {
                markerBuilder.Rebuild();
                payloadBuilder.Rebuild();
            }

            var message = added > 0 ? "saved" : "saved, nothing new";
            if (dropped > 0) message += $"; {dropped} unusable entries dropped";

            return Reply(new ZoneHarvestResponse
            {
                Ok = true,
                Zones = saved.Triggers.Count,
                QuestItems = saved.QuestItems.Count,
                Message = message
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
                return new ValueTask<string>(JsonSerializer.Serialize(fallback(), WireJson.Options));
            }
        }
    }
}
