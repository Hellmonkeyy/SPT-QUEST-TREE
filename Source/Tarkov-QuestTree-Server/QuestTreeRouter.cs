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

        /// <summary>Declines logged this boot, and the ceiling on them. Same reasoning as
        /// MaxRejectionsLogged below: the save route is unauthenticated HTTP, so a caller in a loop must
        /// not be able to rotate the real diagnostics out of a 10 MB x 10 log. A player pressing the
        /// button never approaches this.</summary>
        private const int MaxDeclinesLogged = 256;

        private static int _declinesLogged;

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
                        Guarded(logger, url, () => SavePreset(logger, jsonUtil, profileBuilds, presetWriter, sessionId, request),
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
            ISptLogger<QuestTreeRouter> logger, JsonUtil jsonUtil, ProfileBuilds profileBuilds,
            WeaponPresetWriter presetWriter, MongoId sessionId, SavePresetRequest? request)
        {
            string Reply(SavePresetResponse r)
            {
                // EVERY decline says so in the log, and none of them used to. Eleven paths reach the
                // player through here - five in this method, four from Flatten, two more from Save itself
                // - and every one left both logs completely silent, so "the button did nothing" and "the
                // server refused, and here is why" were indistinguishable afterwards. (A twelfth reaches
                // the player without passing through here: Guarded's own fallback reply. That one was
                // never silent - Guarded logs it at Error.)
                //
                // That cost real time: a reproducible bug in this very feature had to be diagnosed from
                // the GAME's Player.log, because this mod's own logs had nothing to say about a save the
                // player had just watched fail.
                //
                // INFO, and the first version of this said Debug on the reasoning that a refusal is the
                // player's answer rather than the server's news. That reasoning was fine and the level was
                // still wrong: SPT_Runtime\sptLogger.json ships logLevel "Information" on all three sinks,
                // so a Debug line is discarded on a default install - which is every install that has not
                // been hand-edited. The line would have been written for diagnosability and then filtered
                // out of exactly the situation it was written for. A decline is one player button press,
                // so Info costs nothing.
                //
                // Worse than silent is the empty-Reason case: a response shape the client cannot read
                // arrives as Saved=false with no reason, prints nothing on screen, and would otherwise log
                // nothing either - so it is named explicitly rather than logged as a blank.
                // CLAMPED, STRIPPED AND CAPPED, because the key is the caller's text on a route every
                // Fika peer can post to unauthenticated - the same premise the harvest route states forty
                // lines below, where it already caps its own rejection logging for this reason.
                // Unbounded it fills a 10 MB x 10 rolling log and rotates the real diagnostics away;
                // with newlines in it, it forges log lines. The cap applies to the LOGGING only - the
                // player still gets every reason on screen, always.
                if (!r.Saved && System.Threading.Interlocked.Increment(ref _declinesLogged) <= MaxDeclinesLogged)
                {
                    var key = request?.Key ?? "";

                    if (key.Length > 64) key = key.Substring(0, 64) + "...";

                    key = key.Replace((char)13, ' ').Replace((char)10, ' ');

                    logger.Info(
                        $"Quest Tracker: declined to save a preset for {sessionId} - " +
                        (string.IsNullOrEmpty(r.Reason) ? "NO REASON GIVEN, which is itself a bug" : r.Reason) +
                        $" (build key '{key}')." +
                        (_declinesLogged == MaxDeclinesLogged ? " Further declines will not be logged." : ""));
                }

                return JsonSerializer.Serialize(r, WireJson.Options);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Key))
                return Reply(new SavePresetResponse { Reason = "no build was named" });

            // REFUSE a client too old to be saved for safely, and this is a data-safety gate rather than
            // version pedantry. Up to 1.13.1 the client removed the same-named build before inserting,
            // and that removal POSTs /client/builds/delete. It was harmless while the server minted a
            // fresh id per save - the id it deleted was always superseded - and it stopped being
            // harmless the moment the server started REUSING the id, because then it deletes the preset
            // written seconds earlier. Every second save, with the panel reporting success.
            //
            // Absence is the test, not a comparison: 1.13.1 and earlier send a body carrying only `key`,
            // so an empty ClientVersion identifies them exactly and no version parsing is needed. A
            // newer client always sends one. The old client renders whatever Reason comes back, so it
            // shows this sentence rather than losing the preset.
            if (string.IsNullOrWhiteSpace(request.ClientVersion))
                return Reply(new SavePresetResponse
                {
                    Reason =
                        "the Quest Tracker plugin is older than the server half - update QuestTree.dll " +
                        "in BepInEx/plugins/QuestTree to match, or saving would delete the preset it " +
                        "just wrote"
                });

            var build = profileBuilds.Find(sessionId, request.Key);

            if (build == null)
                return Reply(new SavePresetResponse
                {
                    Reason = "your builds are still being worked out - try again in a moment"
                });

            if (build.Tree == null || build.Tree.Count == 0)
                return Reply(new SavePresetResponse { Reason = "there is no build for this quest to save" });

            // Status, not just "is there a tree". A blocked answer HAS a tree - JudgeQuietly stores the
            // closest attempt so the panel can say which threshold it missed and by how much - and it
            // reaches that same line when the solver did find a build but the independent verifier rejected
            // it. Either way the parts do not satisfy the quest, and writing them as a preset means the
            // player loads a gun the trader will refuse while the panel says it saved fine.
            //
            // The same test StillObtainable uses, deliberately: "ok" or "repaired" is what this codebase
            // means by a build worth acting on.
            if (build.Status is not ("ok" or "repaired"))
                return Reply(new SavePresetResponse
                {
                    Reason = "this build does not meet the quest yet, so there is nothing worth saving"
                });

            if (!build.WeaponTemplate.TryParseMongoId(out var weapon))
                return Reply(new SavePresetResponse { Reason = "this quest's weapon could not be identified" });

            // WeaponName as well as QuestName: the preset is named after both, because a quest can ask
            // for several weapons and the game de-duplicates saved builds by name.
            var outcome = presetWriter.Save(sessionId, build.QuestName, build.WeaponName, weapon, build.Tree)
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

            // BEFORE every other check, because this is the one route that mutates shared, shipped
            // data and the checks below all assume they are reading a shape this build knows. A
            // harvest from a newer client carries fields System.Text.Json drops silently; storing
            // the rest would put a half-understood harvest into zones\ stamped with a schemaVersion
            // saying it is complete, and the harvest is fire-and-forget, so nothing downstream would
            // ever notice. Older or equal is accepted unchanged - every version so far has been the
            // same shape, and a client older than 1.8.1 sends no version at all, which deserialises
            // to 0.
            //
            // mapIsValid: false, although the map is usually fine: the map name is a field of a body
            // this build cannot read in full, so it is not trusted far enough to key a log line on
            // before IsValidMapName has looked at it.
            //
            // The claimed version goes IN the reason, which is what makes Reject's own dedup - keyed
            // on map and reason - say this once per boot per version rather than once per raid. The
            // wording is the client's to print: ZoneHarvester logs response.Message verbatim on
            // !Ok, so this is the line the player actually sees.
            if (request.SchemaVersion > ZoneHarvestRequest.SupportedSchemaVersion)
                return Reject(
                    $"harvest schema v{request.SchemaVersion} is newer than the v{ZoneHarvestRequest.SupportedSchemaVersion} " +
                    "this server reads - this server is older than the client, update the server half",
                    mapIsValid: false);

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
