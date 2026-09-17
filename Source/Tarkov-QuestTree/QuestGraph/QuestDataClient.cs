using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Fetches the full quest list from the QuestTreeServer companion mod.
    ///
    /// This exists because the game client has never been sent the full quest list: the server's
    /// /client/quest/list only returns quests already in the profile plus those whose every
    /// prerequisite is already satisfied (QuestHelper.GetClientQuests), which is precisely the set a
    /// progression tree needs to see beyond. The companion mod serves the unfiltered database.
    ///
    /// The companion mod is optional. When it is absent the fetch fails, this returns null, and
    /// QuestGraphBuilder falls back to building from the quests the client does know about - a
    /// smaller tree, but a working mod.
    /// </summary>
    internal static class QuestDataClient
    {
        private const string Route = "/questtree/quests";
        private const string KappaRoute = "/questtree/kappa";
        private const string ProfileRoute = "/questtree/profile";
        private const string MapMarkerRoute = "/questtree/mapmarkers";
        private const string RaidCheckRoute = "/questtree/raidcheck";
        private const string BuildsRoute = "/questtree/builds";

        /// <summary>
        /// How long a request may hold the game. Every fetch here is synchronous on Unity's main
        /// thread - by design, see TryFetchAll - and SPT's own GetJson has no limit of its own, so
        /// a server that accepted the connection and then hung froze the game with no frames and
        /// no way out. This is a cap, not a cure: the thread is still blocked until it fires.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        /// <summary>What the server said about saving a preset.</summary>
        public sealed class SavePresetResult
        {
            [JsonProperty("saved")]
            public bool Saved { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("reason")]
            public string Reason { get; set; }

            /// <summary>The preset's id, and the build as actually written - the ids are SPT's own,
            /// minted inside the save, so inserting these into the game's list keeps the client's copy
            /// and the profile in agreement.</summary>
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("root")]
            public string Root { get; set; }

            /// <summary>The items as raw JSON in the game's own shape, deserialised by the game's own
            /// converters rather than rebuilt by hand - see the server-side comment on ItemsJson.</summary>
            [JsonProperty("itemsJson")]
            public string ItemsJson { get; set; }
        }

        /// <summary>Ask the server to write one solved build into the player's saved weapon builds.
        ///
        /// Synchronous, like every other call here, and for the same reason: the player pressed a
        /// button and is waiting to be told what happened. Never throws - a failed save is a sentence
        /// on screen, not an exception into a UI rebuild.</summary>
        public static SavePresetResult SavePreset(string key)
        {
            if (string.IsNullOrEmpty(key))
                return new SavePresetResult { Reason = "no build was named" };

            try
            {
                var body = JsonConvert.SerializeObject(new { key });

                var task = Task.Run(() => RequestHandler.PostJsonAsync("/questtree/build/save", body));

                if (!task.Wait(RequestTimeout))
                    return new SavePresetResult { Reason = "the server did not answer in time" };

                var reply = JsonConvert.DeserializeObject<SavePresetResult>(task.Result);

                return reply ?? new SavePresetResult { Reason = "the server sent nothing back" };
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not save the weapon preset - {ex.Message}");

                return new SavePresetResult { Reason = "the server could not be reached" };
            }
        }

        /// <summary>RequestHandler.GetJson with a deadline. Same mechanism SPT uses (the async call
        /// on a pool thread, waited on here), plus the wait having a limit.</summary>
        private static string GetJson(string route)
        {
            var task = Task.Run(() => RequestHandler.GetJsonAsync(route));

            try
            {
                if (!task.Wait(RequestTimeout))
                    throw new TimeoutException($"no answer within {RequestTimeout.TotalSeconds:0}s");
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                // The real failure, not "One or more errors occurred" - it goes into a log line.
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }

            return task.Result;
        }

        /// <summary>
        /// Lets a fetch that failed be tried again. Failures are cached on purpose (see GetKappa for
        /// the retry storm that caching prevents), but cached for the whole session they made one
        /// unlucky fetch at startup - the server still coming up, say - a degraded tree until the
        /// game was restarted. Called when the panel is reopened; a success is never retried.
        /// </summary>
        public static void RetryFailedFetches()
        {
            if (_attempted && _cached == null) _attempted = false;
            if (_raidCheckAttempted && _raidCheck == null) _raidCheckAttempted = false;
            if (_markersAttempted && _markers == null) _markersAttempted = false;
            if (_profileAttempted && _profile == null) _profileAttempted = false;
            if (_buildsAttempted && _builds == null) _buildsAttempted = false;
        }

        private static ProfileBuildsDto _builds;
        private static bool _buildsAttempted;

        /// <summary>The weapon builds as this player can assemble them, or null when the server half
        /// is missing, older than this route, or had no profile to read - in which case the shared
        /// build is shown, as it always was. Null is neutral.
        ///
        /// Cached with the profile's discipline and invalidated with it: trader progress and the stash
        /// both move it, and both move the profile. Answered from what the server already has - it
        /// never solves inside the request - so a not-ready answer is cheap to ask again for, which
        /// InvalidateBuilds is for.</summary>
        public static ProfileBuildsDto GetBuilds()
        {
            if (_buildsAttempted) return _builds;
            _buildsAttempted = true;

            try
            {
                var json = GetJson(BuildsRoute);
                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {BuildsRoute} returned nothing - the server half is missing or predates " +
                        "this route, so builds are shown as the shared answer.");
                    return null;
                }

                var payload = JsonConvert.DeserializeObject<ProfileBuildsDto>(json);
                Sanitise(payload);

                if (payload != null && payload.SchemaVersion != ProfileBuildsDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: builds payload schema v{payload.SchemaVersion} ({SchemaNote(payload.SchemaVersion, ProfileBuildsDto.SupportedSchemaVersion)}) but this client expects " +
                        $"v{ProfileBuildsDto.SupportedSchemaVersion} (server mod {payload.ModVersion}).");
                }

                // Not ready is not a failure and must not latch: the server is still working it out,
                // and the next panel open should ask again rather than show the shared build all session.
                if (payload != null && payload.HasProfile && !payload.Ready) _buildsAttempted = false;

                _builds = payload;
                return _builds;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not reach {BuildsRoute} ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Drops the cached builds so the next GetBuilds re-fetches. Paired with
        /// InvalidateProfile - the same events move both.</summary>
        public static void InvalidateBuilds()
        {
            _builds = null;
            _buildsAttempted = false;
        }

        /// <summary>Every name here comes from the locale table and is rendered inside markup.</summary>
        private static void Sanitise(ProfileBuildsDto payload)
        {
            if (payload?.Builds == null) return;

            foreach (var build in payload.Builds)
            {
                if (build == null) continue;

                build.QuestName = RichText.Safe(build.QuestName);
                build.WeaponName = RichText.Safe(build.WeaponName);
                build.Why = RichText.Safe(build.Why);

                if (build.Unmet != null)
                    for (var i = 0; i < build.Unmet.Count; i++) build.Unmet[i] = RichText.Safe(build.Unmet[i]);

                if (build.Parts == null) continue;

                foreach (var part in build.Parts)
                {
                    if (part == null) continue;

                    part.Name = RichText.Safe(part.Name);
                    part.Gate = RichText.Safe(part.Gate);
                    part.Where = RichText.Safe(part.Where);
                }
            }
        }

        private static List<QuestDto> _cached;
        private static bool _attempted;

        /// <summary>Set off the main thread by InvalidateQuests, read and cleared on the main thread by
        /// TryFetchAll. The same shape as _markersStale, for the same reason and one release late.
        ///
        /// InvalidateQuests is called from the harvester's pool thread, one line after
        /// InvalidateMapMarkers, and used to write the two fields above directly. Neither is volatile, so
        /// the main thread could go on seeing _attempted true and keep serving the pre-harvest quest list
        /// for the rest of the session - the harvested map gaining its pins, because those go through the
        /// volatile flag, and never gaining its derived map entry. That is the exact in-the-list-without-
        /// pins split derived locations exist to prevent. It could also tear outright, with _cached nulled
        /// while the main thread was inside TryFetchAll reading it.</summary>
        private static volatile bool _questsStale;

        /// <summary>Drops the cached quest list so the next fetch asks again.
        ///
        /// Needed since 1.9.0, and the comment it replaces was made false by the same change: the
        /// quest DATABASE still cannot change while the server runs, but the derived locations now
        /// carried on each quest can - a raid that harvests a map teaches the server where that
        /// map's zones are, and the server rebuilds its quest payload accordingly. Without this the
        /// client would keep serving the pre-harvest answer for the rest of the session, so the
        /// quest would gain its pins and never gain its map.</summary>
        public static void InvalidateQuests() => _questsStale = true;

        /// <summary>The full quest list, or null when the companion server mod is not installed or
        /// did not answer. Fetched once per game session and after a harvest - see
        /// <see cref="InvalidateQuests"/> - or on connecting somewhere else, which is what
        /// <see cref="ResetSession"/> is for.</summary>
        public static List<QuestDto> TryFetchAll()
        {
            // Cleared here rather than by the caller that asked for it: this is the main thread, which is
            // the only thread allowed to touch the pair below.
            if (_questsStale)
            {
                _questsStale = false;
                _attempted = false;
                _cached = null;
            }

            if (_attempted) return _cached;
            _attempted = true;

            try
            {
                // Synchronous by design: this is called from the panel's own open path, once, and
                // the tree cannot be drawn before the data arrives anyway.
                var json = GetJson(Route);

                if (string.IsNullOrEmpty(json))
                {
                    LogUnavailable("the server returned an empty response");
                    return null;
                }

                var payload = JsonConvert.DeserializeObject<QuestPayloadDto>(json);
                Sanitise(payload);

                if (payload?.Quests == null || payload.Quests.Count == 0)
                {
                    LogUnavailable("the server returned no quests");
                    return null;
                }

                if (payload.SchemaVersion != QuestPayloadDto.SupportedSchemaVersion)
                {
                    var server = string.IsNullOrEmpty(payload.ModVersion) ? "older than 1.8.1" : payload.ModVersion;
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the QuestTreeServer mod ({server}, {SchemaNote(payload.SchemaVersion, QuestPayloadDto.SupportedSchemaVersion)}) speaks payload schema v{payload.SchemaVersion} but this " +
                        $"client ({ModInfo.Version}) expects v{QuestPayloadDto.SupportedSchemaVersion}. Update both halves of " +
                        "the mod to the same version. Continuing anyway - some fields may be missing.");
                }

                Plugin.LogSource?.LogInfo($"QuestTree: loaded {payload.Quests.Count} quests from the QuestTreeServer mod.");
                _cached = payload.Quests;
                return _cached;
            }
            catch (Exception ex)
            {
                LogUnavailable(ex.Message);
                return null;
            }
        }


        private static RaidCheckDto _raidCheck;
        private static bool _raidCheckAttempted;

        /// <summary>Drops the cached raid check so the next ask is answered fresh.
        ///
        /// It matters more here than for the other payloads: the whole promise is "if it says you
        /// have enough on you, you have enough", and the way that gets tested is moving an item and
        /// looking again. Called from the panel refresh link and on raid end - deliberately NOT from
        /// MapView.Build, which would walk a four-thousand-item inventory on every map click.</summary>
        public static void InvalidateRaidCheck()
        {
            _raidCheckAttempted = false;
            _raidCheck = null;
        }

        /// <summary>What you must be carrying, per map - or null when the server half is missing,
        /// older than this route, or had no profile to read. Null is the NEUTRAL case and must never
        /// be drawn as "you are ready".
        ///
        /// Cached per menu session like the profile payload. The server bypasses its own inventory
        /// memo for this route, so a fresh fetch really is a fresh answer.</summary>
        public static RaidCheckDto GetRaidCheck()
        {
            if (_raidCheckAttempted) return _raidCheck;
            _raidCheckAttempted = true;

            try
            {
                var json = GetJson(RaidCheckRoute);
                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {RaidCheckRoute} returned nothing - the server half is missing or predates " +
                        "this route, so the raid check stays neutral.");
                    return null;
                }

                var payload = JsonConvert.DeserializeObject<RaidCheckDto>(json);
                Sanitise(payload);

                if (payload != null && payload.SchemaVersion != RaidCheckDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: raid check payload schema v{payload.SchemaVersion} but this client expects " +
                        $"v{RaidCheckDto.SupportedSchemaVersion} (server mod {payload.ModVersion}).");
                }

                _raidCheck = payload;
                return _raidCheck;
            }
            catch (Exception ex)
            {
                LogUnavailable(ex.Message);
                return null;
            }
        }

        /// <summary>Every name in the raid check comes from the locale table and is rendered inside
        /// markup - and the stacked list above the ready-up button is the worst sink in this mod,
        /// since a size tag there draws over the matchmaker itself.</summary>
        private static void Sanitise(RaidCheckDto payload)
        {
            if (payload == null) return;

            if (payload.ItemNames != null)
            {
                var templates = new List<string>(payload.ItemNames.Keys);
                foreach (var template in templates)
                    payload.ItemNames[template] = RichText.Safe(payload.ItemNames[template]);
            }

            if (payload.Maps == null) return;

            foreach (var map in payload.Maps)
            {
                if (map == null) continue;

                map.Name = RichText.Safe(map.Name);
                if (map.Requirements == null) continue;

                foreach (var requirement in map.Requirements)
                {
                    if (requirement == null) continue;

                    requirement.Name = RichText.Safe(requirement.Name);
                    requirement.QuestName = RichText.Safe(requirement.QuestName);
                }
            }
        }

        private static ProfilePayloadDto _profile;
        private static bool _profileAttempted;

        /// <summary>
        /// This player's level, trader state, objective counters and lock reasons - or null when the
        /// server half is unavailable, in which case everything built on it simply is not shown.
        ///
        /// Cached with the same discipline as the Kappa payload, failure included: it is read on
        /// every panel render, so an uncached fetch would repeat the whole-database lock-reason
        /// sweep on every keystroke.
        /// </summary>
        public static ProfilePayloadDto GetProfile()
        {
            if (_profileAttempted) return _profile;
            _profileAttempted = true;

            try
            {
                var json = GetJson(ProfileRoute);
                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {ProfileRoute} returned nothing - the server half is missing or predates this route.");
                    return null;
                }

                var payload = JsonConvert.DeserializeObject<ProfilePayloadDto>(json);
                Sanitise(payload);

                if (payload != null && payload.SchemaVersion != ProfilePayloadDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: profile payload schema v{payload.SchemaVersion} ({SchemaNote(payload.SchemaVersion, ProfilePayloadDto.SupportedSchemaVersion)}) but this client expects " +
                        $"v{ProfilePayloadDto.SupportedSchemaVersion} (server mod {payload.ModVersion}).");
                }

                _profile = payload;
                return _profile;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not reach {ProfileRoute} ({ex.Message}).");
                return null;
            }
        }

        private static MapMarkerPayloadDto _markers;
        private static bool _markersAttempted;

        /// <summary>Set from the harvester's pool thread, read and cleared on the main thread by
        /// GetMapMarkers. One of TWO fields in this class touched off the main thread - see _questsStale,
        /// which was the same situation going unnoticed - and the pair above is only ever written on the
        /// main thread.</summary>
        private static volatile bool _markersStale;

        /// <summary>When an empty answer may be asked about again. Since 1.8.1 the server answers
        /// empty, uncached, for a minute after a failed marker build; keeping that for the session
        /// would mean no pins after the server had healed.</summary>
        private static DateTime _markersRetryAt = DateTime.MinValue;

        /// <summary>Drops the cached markers so the next Maps tab build re-fetches. Called by the
        /// zone harvester once the server has accepted a raid's zones and rebuilt its markers -
        /// the one event that changes them while the server is up. Safe from any thread.</summary>
        public static void InvalidateMapMarkers() => _markersStale = true;

        /// <summary>
        /// Quest-item spawn markers, keyed by map.
        ///
        /// Fetched once and kept for the session, unlike the profile and Kappa payloads: where an
        /// item spawns is a property of the map, identical for every player, and cannot change
        /// while the server is up. The server builds it once too - the first request reads every
        /// map's loot table off disk and takes a few seconds.
        /// </summary>
        public static MapMarkerPayloadDto GetMapMarkers()
        {
            if (_markersStale)
            {
                _markersStale = false;
                _markers = null;
                _markersAttempted = false;
            }

            if (_markersAttempted) return _markers;
            if (DateTime.UtcNow < _markersRetryAt) return _markers;
            _markersAttempted = true;

            try
            {
                var json = GetJson(MapMarkerRoute);

                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {MapMarkerRoute} returned nothing - the server half is missing or " +
                        "predates this route, so the map will show no markers.");
                    return null;
                }

                _markers = JsonConvert.DeserializeObject<MapMarkerPayloadDto>(json);
                Sanitise(_markers);

                if (_markers?.Maps == null || _markers.Maps.Count == 0)
                {
                    _markersAttempted = false;
                    _markersRetryAt = DateTime.UtcNow.AddSeconds(60);
                    Plugin.LogSource?.LogInfo("QuestTree: the server sent no map markers - asking again in a minute.");
                    return _markers;
                }

                if (_markers.SchemaVersion != MapMarkerPayloadDto.SupportedSchemaVersion)
                {
                    // Not fatal: the maps are the one feature this payload carries, and a version
                    // that only differs in a field this client does not read still pins fine. Named
                    // in the log so a mis-drawn map has a first place to look.
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: map marker payload schema v{_markers.SchemaVersion} ({SchemaNote(_markers.SchemaVersion, MapMarkerPayloadDto.SupportedSchemaVersion)}) but this client expects " +
                        $"v{MapMarkerPayloadDto.SupportedSchemaVersion} (server mod {_markers.Version}). " +
                        "Update both halves of the mod together.");
                }

                var count = 0;
                foreach (var map in _markers.Maps)
                    count += map?.Markers?.Count ?? 0;

                Plugin.LogSource?.LogInfo($"QuestTree: loaded {count} quest-item map markers.");
                return _markers;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not reach {MapMarkerRoute} ({ex.Message}).");
                return null;
            }
        }

        /// <summary>Drops the cached profile so the next GetProfile re-fetches. Paired with
        /// InvalidateKappa - the same events move both.</summary>
        public static void InvalidateProfile()
        {
            _profile = null;
            _profileAttempted = false;
        }

        private static KappaFetchResult _kappaResult;

        /// <summary>
        /// The Kappa/Collector checklist for the current profile.
        ///
        /// Cached, including failures. It reports the live stash, so it must not be cached
        /// *forever* - but it was previously re-fetched on every panel re-render, and
        /// QuestTreePanel re-renders on every search keystroke and settings toggle.
        /// With the Kappa tab open that meant a blocking HTTP request per keystroke, each of which
        /// walks the whole profile inventory server-side. Caching the failure matters just as much:
        /// an unregistered route fails fast, so a missing server half produced the tightest retry
        /// loop of all - observed as four back-to-back [UNHANDLED] errors in a server console.
        ///
        /// Call <see cref="InvalidateKappa"/> for the events that can genuinely change it: opening
        /// the Kappa tab, reopening the panel, a quest status change, or the explicit Refresh button.
        /// </summary>
        public static KappaFetchResult GetKappa()
        {
            if (_kappaResult != null) return _kappaResult;

            _kappaResult = FetchKappa();
            return _kappaResult;
        }

        /// <summary>Drops the cached Kappa result so the next <see cref="GetKappa"/> re-fetches.</summary>
        public static void InvalidateKappa() => _kappaResult = null;

        /// <summary>
        /// Forgets everything fetched for the previous profile/server, so the next request starts
        /// from scratch.
        ///
        /// The caches above are static, which means they outlive the game session rather than the
        /// process: connect to a server without the companion mod once and _attempted latches, so
        /// reconnecting to a server that DOES have it still showed only the already-unlocked quests
        /// until the game was restarted. That happened in the wild. The quest list is also
        /// per-server and the Kappa result per-profile, so neither may survive a change of either.
        /// </summary>
        public static void ResetSession()
        {
            _markers = null;
            _markersAttempted = false;
            _cached = null;
            _attempted = false;
            _raidCheck = null;
            _raidCheckAttempted = false;
            _kappaResult = null;
            _profile = null;
            _profileAttempted = false;
            _builds = null;
            _buildsAttempted = false;

            // Both off-thread flags too, for consistency with the pairs above rather than to fix a known
            // ordering: every field they guard is nulled here, and each flag is consumed at the top of the
            // first fetch after this, before anything has been fetched to throw away. Cleared so the flags
            // cannot outlive the state they describe.
            _questsStale = false;
            _markersStale = false;
        }

        // Every name the views will put inside rich text, made literal once here - see RichText.
        private static void Sanitise(QuestPayloadDto payload)
        {
            if (payload?.Quests == null) return;
            foreach (var quest in payload.Quests)
            {
                if (quest == null) continue;
                quest.Name = RichText.Safe(quest.Name);
                quest.LocationId = RichText.Safe(quest.LocationId);

                // Derived map names are locale text like every other name here, and they render
                // inside markup - the sidebar header and the map dropdown both.
                if (quest.DerivedLocations != null)
                    foreach (var derived in quest.DerivedLocations)
                        if (derived != null) derived.Name = RichText.Safe(derived.Name);

                // Reward names. Interpolated raw into rich text by QuestSummary.FormatReward
                // since the Rewards block existed, so any quest or item mod ON THE HOST could
                // swallow that block with a "<" or blow it up with <size=400%>.
                //
                // Type is deliberately NOT wrapped here: FormatReward switches on it against string
                // literals, and a wrapped "Item" would match nothing and drop every modded reward
                // type into the default branch. It is wrapped at the one place it is printed.
                if (quest.Rewards != null)
                    foreach (var reward in quest.Rewards)
                        if (reward != null) reward.Name = RichText.Safe(reward.Name);

                // Objective target item names, rendered by QuestSummary and - since 1.9.0 - fed
                // into the search haystack as well. Locale text, mod-controlled, and never
                // sanitised until now: a name carrying <size=400%> swallowed its whole block.
                if (quest.Objectives != null)
                    foreach (var objective in quest.Objectives)
                    {
                        if (objective?.TargetItemNames == null) continue;

                        for (var i = 0; i < objective.TargetItemNames.Count; i++)
                            objective.TargetItemNames[i] = RichText.Safe(objective.TargetItemNames[i]);
                    }
                if (quest.Objectives == null) continue;
                foreach (var objective in quest.Objectives)
                    if (objective != null) objective.Text = RichText.Safe(objective.Text);
            }
        }

        private static void Sanitise(ProfilePayloadDto payload)
        {
            if (payload?.LockReasons == null) return;
            foreach (var reason in payload.LockReasons.Values)
                if (reason != null) reason.Detail = RichText.Safe(reason.Detail);
        }

        private static void Sanitise(MapMarkerPayloadDto payload)
        {
            if (payload?.Maps == null) return;
            foreach (var map in payload.Maps)
            {
                if (map?.Markers == null) continue;
                foreach (var marker in map.Markers)
                {
                    if (marker == null) continue;
                    marker.ItemName = RichText.Safe(marker.ItemName);
                    if (marker.Quests == null) continue;
                    for (var i = 0; i < marker.Quests.Count; i++) marker.Quests[i] = RichText.Safe(marker.Quests[i]);
                }
            }
        }

        private static void Sanitise(KappaPayloadDto payload)
        {
            if (payload?.Items == null) return;
            foreach (var item in payload.Items)
                if (item != null) item.Name = RichText.Safe(item.Name);
        }

        /// <summary>Whether a schema the client did not expect is an older or a newer server's -
        /// the four mismatch warnings used to treat both the same, and "update both halves" is
        /// the wrong advice when it is the client that is behind.</summary>
        private static string SchemaNote(int actual, int expected) =>
            actual < expected ? "an older server half" : "a newer server half";

        private static KappaFetchResult FetchKappa()
        {
            try
            {
                var json = GetJson(KappaRoute);

                // SPT answers a route no mod registered by logging [UNHANDLED] and returning an
                // empty body, so "empty" is how a missing or outdated server half presents itself.
                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {KappaRoute} returned nothing - the server half is missing or predates this route.");
                    return KappaFetchResult.Failed(EKappaFetchStatus.ServerHalfMissing);
                }

                var payload = JsonConvert.DeserializeObject<KappaPayloadDto>(json);
                Sanitise(payload);
                if (payload == null)
                    return KappaFetchResult.Failed(EKappaFetchStatus.ServerHalfMissing);

                // Two halves from different downloads is the case the Kappa tab explains in words,
                // and the server's own version number is the fact that says so. A server too old
                // to send one is judged by its schema, as before. With the versions equal, a schema
                // difference cannot occur; the warning below is for a build stamp lying.
                var differentDownload = string.IsNullOrEmpty(payload.ModVersion)
                    ? payload.SchemaVersion != KappaPayloadDto.SupportedSchemaVersion
                    : payload.ModVersion != ModInfo.Version;

                if (differentDownload)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the server half is {(string.IsNullOrEmpty(payload.ModVersion) ? "older than 1.8.1" : payload.ModVersion)} " +
                        $"and this client is {ModInfo.Version} - reinstall both halves from the same download.");
                    return KappaFetchResult.Failed(EKappaFetchStatus.VersionMismatch, payload.ModVersion);
                }

                if (payload.SchemaVersion != KappaPayloadDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: Kappa payload schema v{payload.SchemaVersion} ({SchemaNote(payload.SchemaVersion, KappaPayloadDto.SupportedSchemaVersion)}) " +
                        $"but this client expects v{KappaPayloadDto.SupportedSchemaVersion}. Continuing anyway - some fields may be missing.");
                }

                return KappaFetchResult.Ok(payload);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not reach {KappaRoute} ({ex.Message}).");
                return KappaFetchResult.Failed(EKappaFetchStatus.Unreachable);
            }
        }

        private static void LogUnavailable(string reason)
        {
            Plugin.LogSource?.LogWarning(
                $"QuestTree: could not reach the QuestTreeServer companion mod on {Route} ({reason}). " +
                "Falling back to only the quests this profile has already unlocked - install the server half " +
                "(SPT_Runtime/user/mods/QuestTree) to see the whole quest tree.");
        }
    }
}
