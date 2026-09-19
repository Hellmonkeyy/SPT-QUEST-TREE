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
        /// How long a request may hold the thread that made it. Every fetch here but the quest list
        /// is synchronous on Unity's main thread - see TryFetchAll - and SPT's own GetJson has no
        /// limit of its own, so a server that accepted the connection and then hung froze the game
        /// with no frames and no way out. This is a cap, not a cure: the thread is still blocked
        /// until it fires. The quest list is fetched on a worker since 1.17.0 (BeginFetchAll) and
        /// keeps the same cap there, where it blocks a pool thread instead of the game.
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

            /// <summary>The preset's id, and the build as actually written. The ITEM and ROOT ids are
            /// SPT's own, minted by ReplaceIDs inside the save; the PRESET id is one the SERVER chose,
            /// and this said otherwise for a long time. Inserting these into the game's list keeps the
            /// client's copy
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
                // The version travels with the save, and a server that reuses preset ids refuses any
                // client that does not send it - see SavePresetRequest.ClientVersion. Anything before
                // 1.13.2 would delete the preset the server had just written.
                var body = JsonConvert.SerializeObject(new { key, clientVersion = ModInfo.Version });

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

        /// <summary>Time spent waiting on the server since the last <see cref="ResetFetchClock"/>,
        /// and how many requests that was: every synchronous GetJson, plus the quest fetch's own
        /// server wait, which <see cref="TryTakeFetched"/> adds on the main thread once the worker
        /// has measured it. Read by the panel's open-time line. Not synchronised: the pre-raid
        /// screen already fetches its raid check on a pool thread, so this has never been more than
        /// an accounting number - never a fact anything decides on.</summary>
        public static long FetchMillis { get; private set; }

        public static int FetchCount { get; private set; }

        public static void ResetFetchClock()
        {
            FetchMillis = 0;
            FetchCount = 0;
        }

        /// <summary>RequestHandler.GetJson with a deadline. Same mechanism SPT uses (the async call
        /// on a pool thread, waited on here), plus the wait having a limit.</summary>
        private static string GetJson(string route)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
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
            finally
            {
                // Counted on failure too: a timeout is fifteen seconds the player waited.
                FetchMillis += clock.ElapsedMilliseconds;
                FetchCount++;
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

            // A finished-but-untaken quest fetch that came back with no list goes too, or it would
            // eat the retry the line above just granted: the panel hidden mid-fetch leaves the result
            // sitting there, and the next open would take THAT failure and latch it for the session
            // instead of asking again. A finished fetch that HAS a list is kept - it is the answer.
            if (_fetch != null && _fetch.IsCompleted && !Succeeded(_fetch)) _fetch = null;
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

            // A not-ready answer is re-asked for, but not on every call. Un-latching alone meant the next
            // caller went straight back out to the server, and AddSolution calls this once per weapon-build
            // it draws - three for Old Friend's Request - each one a GetJson that blocks Unity's main thread
            // for up to fifteen seconds. Three sequential round trips inside one panel open, repeated on
            // every repaint, for an answer that cannot have changed between them.
            //
            // The same shape as the marker retry above, and the same reason: the server answers a not-ready
            // build cheaply, so asking again is right - just not thousands of times.
            if (DateTime.UtcNow < _buildsRetryAt) return _builds;

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
                // Held off for a moment rather than released immediately - see the note at the top.
                if (payload != null && payload.HasProfile && !payload.Ready)
                {
                    _buildsAttempted = false;
                    _buildsRetryAt = DateTime.UtcNow + NotReadyRetry;
                }

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

            // And the not-ready hold-off, or an explicit invalidation would wait out a timer it has no
            // reason to respect: the caller is saying the answer HAS changed.
            _buildsRetryAt = DateTime.MinValue;
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
        /// whichever fetch runs first - BeginFetchAll on the panel's open path, TryFetchAll for the
        /// callers that cannot yield. The same shape as _markersStale, for the same reason and one
        /// release late.
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

        /// <summary>One off-thread quest fetch: what it got, how long each half took, and what the
        /// main thread must say about it. Nothing is logged on the worker - the lines are carried
        /// here and written by <see cref="Complete"/> - so they keep the panel's log order.</summary>
        private sealed class FetchedQuests
        {
            /// <summary>The list, or null when there is none - see <see cref="Unavailable"/>.</summary>
            public List<QuestDto> Quests;

            public long ServerMillis;
            public long ParseMillis;

            /// <summary>The schema-mismatch warning, already worded, or null.</summary>
            public string Warning;

            /// <summary>Why there is no list, for LogUnavailable. Null when there is one.</summary>
            public string Unavailable;
        }

        /// <summary>The fetch <see cref="BeginFetchAll"/> started, until <see cref="TryTakeFetched"/>
        /// consumes it. Main-thread only, like the cache pair above: the worker only ever RETURNS a
        /// value, so nothing off the main thread touches any field in this class along this path.</summary>
        private static Task<FetchedQuests> _fetch;

        /// <summary>Whether a quest fetch is still running. False the moment one has finished -
        /// finished and untaken is not pending, it is ready.</summary>
        public static bool IsFetchPending => _fetch != null && !_fetch.IsCompleted;

        /// <summary>Starts the quest fetch on a pool thread, or does nothing because the answer is
        /// already here or already coming.
        ///
        /// Off the main thread because the parse is the expensive half: of a measured 453 ms panel
        /// open, 233 ms was DeserializeObject plus Sanitise and 29 ms was the server. Both halves
        /// are plain string and object work with no Unity API in them, and the caller yields frames
        /// until <see cref="IsFetchPending"/> goes false with the loading notice already up, so that
        /// quarter-second is now frames the game draws instead of a frozen client.
        ///
        /// The cache semantics are <see cref="TryFetchAll"/>'s exactly, stale flag included: a
        /// remembered failure counts as an answer, and RetryFailedFetches is what clears it.
        /// Idempotent - called again while a fetch is in flight, or on one finished and not yet
        /// taken, it starts nothing.</summary>
        public static void BeginFetchAll()
        {
            // The same consume-on-the-main-thread as TryFetchAll, and it has to come first: a
            // harvest has to beat both the cache and an answer asked for before it.
            if (_questsStale)
            {
                _questsStale = false;
                _attempted = false;
                _cached = null;

                // Whatever is in flight was asked before the harvest, so its answer is the one the
                // stale flag exists to reject. Dropped, not cancelled: the orphan finishes into a
                // value nobody reads, and it can never write the cache itself.
                _fetch = null;
            }

            if (_fetch != null) return;
            if (_attempted) return;

            try
            {
                _fetch = Task.Run(FetchQuestsOffThread);
            }
            catch (Exception ex)
            {
                // A pool that cannot take work at all. Left null, so TryTakeFetched answers from the
                // cache - empty here - and the tree is built from the client's own quest list.
                Plugin.LogSource?.LogWarning($"QuestTree: could not start the quest fetch ({ex.Message}).");
            }
        }

        /// <summary>Stops waiting on the fetch in flight and leaves nothing behind, so the next
        /// <see cref="BeginFetchAll"/> starts a fresh one.
        ///
        /// For the caller that gave up: without it a worker that never came back would be waited out
        /// again on every subsequent open - the task stays un-completed forever, so IsFetchPending
        /// stays true - and the player would never get a tree again, not even the unlocked-only one.
        /// The orphan is dropped, not cancelled: it can only ever return a value nobody reads.</summary>
        public static void AbandonFetch() => _fetch = null;

        /// <summary>Applies a FINISHED fetch to the cache and writes its log lines, on the main
        /// thread. Shared because either path can be the one that finds it done: the open's
        /// <see cref="TryTakeFetched"/> normally, or <see cref="TryFetchAll"/> when a status change
        /// rebuilds mid-wait.</summary>
        private static void Complete(Task<FetchedQuests> fetch, out long serverMillis, out long parseMillis)
        {
            serverMillis = 0;
            parseMillis = 0;

            // Cleared before anything below can throw, so one fetch is applied exactly once.
            _fetch = null;
            _attempted = true;

            FetchedQuests result;

            try
            {
                // Completed, so this cannot block; it can still throw if the pool lost the work
                // itself, which FetchQuestsOffThread's own catches would never see.
                result = fetch.Result;
            }
            catch (Exception ex)
            {
                LogUnavailable(ex.Message);
                return;
            }

            serverMillis = result.ServerMillis;
            parseMillis = result.ParseMillis;

            // Into the same counters the synchronous fetches feed, so "what this open spent waiting
            // on the server" still means that - see FetchMillis.
            FetchMillis += result.ServerMillis;
            FetchCount++;

            if (!string.IsNullOrEmpty(result.Warning)) Plugin.LogSource?.LogWarning(result.Warning);

            if (result.Quests == null)
            {
                LogUnavailable(result.Unavailable ?? "the server sent no quest list");
                return;
            }

            Plugin.LogSource?.LogInfo($"QuestTree: loaded {result.Quests.Count} quests from the QuestTreeServer mod.");
            _cached = result.Quests;
        }

        /// <summary>Whether a finished fetch came back with a list. Read without consuming it, for
        /// RetryFailedFetches; a fetch the pool lost counts as a failure like any other.</summary>
        private static bool Succeeded(Task<FetchedQuests> fetch)
        {
            try
            {
                return fetch.Result?.Quests != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The whole fetch on a pool thread: the request, the deserialise, the Sanitise
        /// pass. No Unity API, no logging and no static state written in here - only the DTOs it
        /// builds and returns, which is what makes it safe off the main thread.</summary>
        private static FetchedQuests FetchQuestsOffThread()
        {
            var result = new FetchedQuests();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Whether each half finished, so a failure below is charged to the right phase: a
            // request that timed out is server time, a parse that threw is parse time, and neither
            // may end up in the main thread's remainder. Flags rather than zero tests, because a
            // server on the same machine really can answer inside a millisecond.
            var answered = false;
            var parsed = false;

            try
            {
                var request = RequestHandler.GetJsonAsync(Route);

                // The same cap GetJson applies, for the same reason - a server that accepts the
                // connection and then hangs - except the thread it blocks is this pool thread,
                // which has nothing else to do, rather than the one drawing frames.
                if (!request.Wait(RequestTimeout))
                    throw new TimeoutException($"no answer within {RequestTimeout.TotalSeconds:0}s");

                var json = request.Result;
                result.ServerMillis = clock.ElapsedMilliseconds;
                answered = true;

                if (string.IsNullOrEmpty(json))
                {
                    result.Unavailable = "the server returned an empty response";
                    return result;
                }

                var payload = JsonConvert.DeserializeObject<QuestPayloadDto>(json);
                Sanitise(payload);
                result.ParseMillis = clock.ElapsedMilliseconds - result.ServerMillis;
                parsed = true;

                if (payload?.Quests == null || payload.Quests.Count == 0)
                {
                    result.Unavailable = "the server returned no quests";
                    return result;
                }

                if (payload.SchemaVersion != QuestPayloadDto.SupportedSchemaVersion)
                {
                    var server = string.IsNullOrEmpty(payload.ModVersion) ? "older than 1.8.1" : payload.ModVersion;
                    result.Warning =
                        $"QuestTree: the QuestTreeServer mod ({server}, {SchemaNote(payload.SchemaVersion, QuestPayloadDto.SupportedSchemaVersion)}) speaks payload schema v{payload.SchemaVersion} but this " +
                        $"client ({ModInfo.Version}) expects v{QuestPayloadDto.SupportedSchemaVersion}. Update both halves of " +
                        "the mod to the same version. Continuing anyway - some fields may be missing.";
                }

                result.Quests = payload.Quests;
                return result;
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                // The real failure, not "One or more errors occurred" - it goes into a log line.
                result.Unavailable = ex.InnerException.Message;
            }
            catch (Exception ex)
            {
                result.Unavailable = ex.Message;
            }

            // A half that failed is still charged to itself: a timeout is fifteen seconds the player
            // waited, and a parse that threw on malformed JSON spent real time doing it. Left
            // unaccounted they would both surface as "quests: main", which is the one number that is
            // supposed to mean work the main thread did.
            if (!answered) result.ServerMillis = clock.ElapsedMilliseconds;
            else if (!parsed) result.ParseMillis = clock.ElapsedMilliseconds - result.ServerMillis;

            return result;
        }

        /// <summary>Completes the fetch <see cref="BeginFetchAll"/> started, on the main thread: the
        /// cache is set here, the fetch's log lines are written here so they land in the panel's own
        /// order, and the worker's two measurements come back for the open-time line.
        ///
        /// False only when there is nothing to complete AND nothing cached - a fetch still running,
        /// or a BeginFetchAll that never got a thread. <paramref name="quests"/> null means the
        /// fetch failed, which is the caller's signal to build from the client's own quest list.
        ///
        /// Idempotent: a second call finds no task and answers from the cache with zero timings, so
        /// a coroutine that dies between the take and the build refetches nothing.</summary>
        public static bool TryTakeFetched(out List<QuestDto> quests, out long serverMillis, out long parseMillis)
        {
            quests = null;
            serverMillis = 0;
            parseMillis = 0;

            var fetch = _fetch;

            if (fetch == null)
            {
                // Either BeginFetchAll answered from the cache - a list, or a remembered failure -
                // or a take has already happened.
                quests = _cached;
                return _attempted;
            }

            if (!fetch.IsCompleted) return false;

            Complete(fetch, out serverMillis, out parseMillis);
            quests = _cached;
            return true;
        }

        /// <summary>The full quest list, or null when the companion server mod is not installed or
        /// did not answer. Fetched once per game session and after a harvest - see
        /// <see cref="InvalidateQuests"/> - or on connecting somewhere else, which is what
        /// <see cref="ResetSession"/> is for.
        ///
        /// Synchronous, and the panel's open path no longer uses it: that goes through
        /// <see cref="BeginFetchAll"/> and <see cref="TryTakeFetched"/>, which do the same work on a
        /// worker. This remains for the callers that are not an open and cannot yield - the
        /// status-change rebuild of a tree built without the server half, and any later one.
        ///
        /// It CAN be reached while a worker's fetch is in flight: that rebuild runs off
        /// QuestController's event, which stays subscribed through the twenty-odd frames the open
        /// spends waiting. So it never starts a rival request for the same list - see the top of the
        /// body for what it does instead.</summary>
        public static List<QuestDto> TryFetchAll()
        {
            // Cleared here rather than by the caller that asked for it: this is the main thread, which is
            // the only thread allowed to touch the pair below.
            if (_questsStale)
            {
                _questsStale = false;
                _attempted = false;
                _cached = null;

                // And any answer asked for before the harvest, exactly as BeginFetchAll does.
                _fetch = null;
            }

            var fetch = _fetch;

            if (fetch != null)
            {
                // Done: apply it rather than fetch the same thing again, and the open's own take
                // afterwards is then the idempotent second one.
                if (fetch.IsCompleted)
                {
                    Complete(fetch, out _, out _);
                    return _cached;
                }

                // Still running. This caller gets what is known now - nothing, which is the same "no
                // full list" a missing server half gives, and the tree it builds is the unlocked-only
                // one it would have built anyway. Deliberately WITHOUT latching _attempted: the
                // worker's result is still to be taken, and latching here would make the open take
                // it for a second one and throw it away.
                return _cached;
            }

            if (_attempted) return _cached;
            _attempted = true;

            try
            {
                // Synchronous because this caller cannot yield: it is inside a rebuild, not a
                // coroutine. The open path, which can, no longer comes through here.
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

        /// <summary>How long a not-ready builds answer stands before this asks again. Long enough that one
        /// panel open cannot make several round trips, short enough that a build finishing is noticed within
        /// a couple of repaints - the panel repaints on any settings change or search keystroke anyway, and
        /// InvalidateBuilds bypasses this entirely for the events that really move the answer.</summary>
        private static readonly TimeSpan NotReadyRetry = TimeSpan.FromSeconds(2);

        private static DateTime _buildsRetryAt = DateTime.MinValue;

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
            _buildsRetryAt = DateTime.MinValue;

            // An in-flight or finished-but-untaken quest fetch too: it was asked of the server this
            // call is saying to forget. Dropped rather than cancelled, like the stale path above -
            // the orphan finishes into a value nobody can read.
            _fetch = null;
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
