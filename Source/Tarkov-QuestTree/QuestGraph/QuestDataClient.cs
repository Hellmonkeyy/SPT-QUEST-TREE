using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
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

            // And the post-timeout hold-offs, for the same reason the flags above go: this call IS the
            // retry, and a window that outlived it would answer the retry with the failure it was
            // granted to get past.
            ClearHoldOffs();
        }

        /// <summary>Forgets every post-timeout hold-off, so the next call to each getter asks the server
        /// again rather than being answered from <see cref="PrefetchTimeoutHoldOff"/>.
        ///
        /// For an explicit retry and nothing else. An Invalidate* deliberately does NOT come through
        /// here - it says the ANSWER changed, not that the server started answering, and a hand-in
        /// invalidating the profile four times a minute would spend the window it exists to keep. A
        /// player pressing Refresh is the other thing entirely: they are looking at a map with no pins
        /// or a cue saying nothing, and "try again now" is the whole content of the press. Whether the
        /// server has in fact come back is then measured the way it always was - on the main thread,
        /// behind the notice, once.
        ///
        /// The markers' empty-answer window goes with them, because to the player pressing Refresh on a
        /// map with no pins the two are one symptom - a server that answered "no markers" and a server
        /// that did not answer at all both leave the same bare map under the same button.
        ///
        /// Main thread only, like every write to those fields.</summary>
        public static void ClearHoldOffs()
        {
            for (var i = 0; i < _prefetchTimedOutUntil.Length; i++) _prefetchTimedOutUntil[i] = DateTime.MinValue;

            _markersRetryAt = DateTime.MinValue;
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



        // ------------------------------------------------------------ the open's other four fetches

        /// <summary>The payloads an open needs besides the quest list, in the order one worker asks
        /// for them. Also the index into <see cref="_generations"/>, so the values must stay stable
        /// within a run - nothing outside this class sees them.</summary>
        private enum EPayload
        {
            Profile,
            Kappa,
            RaidCheck,
            Markers
        }

        /// <summary>How many times each payload has been invalidated. Snapshotted when a prefetch is
        /// STARTED and compared before its result is published, so an answer asked for before an
        /// Invalidate* cannot land on top of what that invalidation was announcing: the request is
        /// already in flight and RequestHandler.GetJsonAsync takes no cancellation token, so
        /// comparing on publish is the only workable answer - the same shape the pre-raid button uses
        /// for its own re-entrancy (MatchMakerAcceptScreenPatch._generation).
        ///
        /// Interlocked because InvalidateMapMarkers is called from the harvester's pool thread - see
        /// _markersStale, which is that same situation - while every read is on the main thread.
        ///
        /// Sized from the enum rather than by a literal, because ResetSession walks every index: a
        /// fifth payload with a hand-written 4 still here would be an IndexOutOfRangeException in the
        /// one method whose job is to forget everything.</summary>
        private static readonly int[] _generations = new int[Enum.GetValues(typeof(EPayload)).Length];

        private static void Invalidated(EPayload payload) => Interlocked.Increment(ref _generations[(int)payload]);

        private static int GenerationOf(EPayload payload) => Volatile.Read(ref _generations[(int)payload]);

        /// <summary>One payload's prefetch: what came back, what each half of it cost on the worker,
        /// and the generation its cache stood at when it was asked for. The value is untyped because
        /// one worker carries all four; the publish for each payload is the only place that knows
        /// which type it is.</summary>
        private sealed class PrefetchSlot
        {
            public EPayload Payload;
            public int Generation;

            /// <summary>The parsed payload, or null when there is none - see <see cref="Failure"/>.</summary>
            public object Value;

            public long ServerMillis;
            public long ParseMillis;

            /// <summary>Why there is no value, for the line the main thread writes. Nothing is logged
            /// on the worker, exactly as in FetchQuestsOffThread, so the panel's log order holds.</summary>
            public string Failure;

            /// <summary>Whether a request for this payload was actually put to the server. False for a
            /// slot the budget skipped or cancellation stopped, which is what the fetch counters have
            /// to tell apart: FetchCount is "how many round trips this open made", and a slot nobody
            /// asked for made none.</summary>
            public bool Requested;

            /// <summary>Whether the request was still unanswered when the cap fired - the
            /// accept-then-hang server, not a refused connection or an unregistered route, both of
            /// which come back in milliseconds. The main thread turns this into that payload's
            /// hold-off, so it is a flag and not a message: a sentence in <see cref="Failure"/> is for
            /// a log line, never a fact to decide on.</summary>
            public bool TimedOut;

            /// <summary>Whether the BUDGET is why this payload was never asked for, as against
            /// cancellation, which is the other way a slot comes back unrequested. The two have to be
            /// told apart on the main thread and a <see cref="Failure"/> sentence is not a fact to
            /// decide on: a budget-skipped slot in a batch that also timed out inherits that
            /// timeout's hold-off (see <see cref="ApplyTimeoutHoldOffs"/>), where a cancelled one
            /// inherits nothing - the panel gave up on it, the server never got the chance to be
            /// slow about it, and its getter is about to ask for itself.</summary>
            public bool Skipped;
        }

        /// <summary>What one prefetched payload cost, for the open-time line. <see cref="Name"/> is
        /// the phase prefix; the caller spells the two halves ": prefetch" and ": prefetch parse", so
        /// they cannot collide with the ": server" phase the same payload's main-thread call site
        /// marks later in the same open - two entries reading "profile: server" in one line, one of
        /// them 0, is a line nobody can act on.</summary>
        public readonly struct PrefetchPhase
        {
            public PrefetchPhase(string name, long serverMillis, long parseMillis)
            {
                Name = name;
                ServerMillis = serverMillis;
                ParseMillis = parseMillis;
            }

            public string Name { get; }

            public long ServerMillis { get; }

            public long ParseMillis { get; }
        }

        /// <summary>How long the batch may keep STARTING requests. The four run in sequence, so
        /// without this a server that accepts connections and then hangs would hold the worker for
        /// four times <see cref="RequestTimeout"/> - past the forty seconds the panel waits, which
        /// would abandon the batch and send every getter back to blocking the main thread for fifteen
        /// seconds each, the exact freeze this is here to remove. Twenty seconds leaves room for one
        /// full timeout after the last request starts and still comes in under that cap.</summary>
        private static readonly TimeSpan PrefetchBudget = TimeSpan.FromSeconds(20);

        /// <summary>How long a payload whose PREFETCH timed out is answered as unavailable without a
        /// request of its own.
        ///
        /// For the server that accepts the connection and then hangs, which is the worst case this
        /// whole path has: without it the batch spends its budget proving the server does not answer,
        /// the panel gives up on it, and then each getter goes and blocks Unity's thread for another
        /// fifteen seconds proving the same thing - four times over, once per payload. The prefetch
        /// already knows, so for half a minute the getters are told instead of finding out again.
        ///
        /// All four of them, which takes the inheritance in <see cref="ApplyTimeoutHoldOffs"/> and not
        /// this window alone: <see cref="PrefetchBudget"/> is twenty seconds and
        /// <see cref="RequestTimeout"/> fifteen, and the budget is tested BEFORE each request, so a
        /// batch against a hanging server issues exactly two requests - at 0 s and at 15 s - and skips
        /// the rest at 30 s. A skipped slot never went to the server and so has no timeout of its own to
        /// report; it inherits the hold-off of the slot that ate the budget hanging. Without that, the
        /// two payloads at the back of the batch kept no hold-off and their getters blocked the frame
        /// thread for fifteen seconds each, inside the very open this exists to keep free of it.
        ///
        /// So the bound this states is: against an accept-then-hang server, the first batch costs at
        /// most two <see cref="RequestTimeout"/>s and pays all of it on the WORKER, and every getter for
        /// the next thirty seconds - all four of them, on the Maps tab, which is the open that reads the
        /// lot - answers from this array without touching the main thread. Zero seconds of freeze after
        /// the first batch, where it used to be four fifteens.
        ///
        /// Thirty seconds is two full <see cref="RequestTimeout"/>s: as long as the batch that measured
        /// it could possibly have taken, and short enough that a server which has come back is asked
        /// again within the same menu session. Not longer, deliberately - during the window the map
        /// draws no pins and the pre-raid cue says nothing, which is the wrong answer to give for long
        /// about a server that only hiccupped once.
        ///
        /// NOT a latch, and the difference is the point:
        ///   - nothing is written into any payload's cache or its "attempted" flag, so no failure is
        ///     remembered as an answer - the getter returns its documented unavailable value (null for
        ///     the profile, raid check and markers; Unreachable for Kappa) and forgets it;
        ///   - it expires by itself, so the next open past the window fetches exactly as it always did,
        ///     and <see cref="ClearHoldOffs"/> lets the player say "now" rather than wait it out;
        ///   - only a TIMEOUT sets it - either the payload's own or, for a payload the budget never
        ///     reached, the one that ate the budget in the same batch (<see cref="ApplyTimeoutHoldOffs"/>,
        ///     which is also where the two cases that inherit NOTHING are written down). A refused
        ///     connection and SPT's empty body for an unregistered route come back in milliseconds and
        ///     cost the main thread nothing worth avoiding, so those keep their existing behaviour - the
        ///     getter asks, fails fast and latches as before, which is what RetryFailedFetches undoes.
        ///
        /// Written on the main thread only (TryTakeAll). Read there too (the four getters and
        /// BeginAll's gates) with ONE exception, which is not new to this field: the pre-raid button
        /// calls GetRaidCheck on a pool thread - MatchMakerAcceptScreenPatch.ApplyVerdict - so the
        /// RaidCheck entry can be read off the main thread, unsynchronised, exactly as
        /// _raidCheckAttempted and _raidCheck already are on that same path. Left that way on purpose:
        /// a DateTime is one 64-bit field, so on the x64 client the read cannot tear, and the worst a
        /// stale read can do either way is let one request through or hold one payload back for a
        /// moment - never a wrong answer, because nothing here is cached. The prefetch worker itself
        /// never touches this array at all. Indexed by <see cref="EPayload"/> and sized from it, for
        /// _generations' reason.</summary>
        private static readonly TimeSpan PrefetchTimeoutHoldOff = TimeSpan.FromSeconds(30);

        /// <summary>When each payload may be asked for again, DateTime.MinValue meaning "now". That is
        /// what a fresh DateTime[] holds, which is the same "no hold-off" value
        /// <see cref="_markersRetryAt"/> and _buildsRetryAt use - relied on here, not a coincidence.</summary>
        private static readonly DateTime[] _prefetchTimedOutUntil = new DateTime[Enum.GetValues(typeof(EPayload)).Length];

        /// <summary>Whether this payload's prefetch timed out recently enough that neither its getter
        /// nor the next batch should go and wait on the same server again - see
        /// <see cref="PrefetchTimeoutHoldOff"/>.</summary>
        private static bool TimedOutRecently(EPayload payload) =>
            DateTime.UtcNow < _prefetchTimedOutUntil[(int)payload];

        /// <summary>The batch <see cref="BeginAll"/> started, until <see cref="TryTakeAll"/>
        /// publishes it. Main-thread only, like _fetch: the worker only ever fills and RETURNS the
        /// slots it was handed.</summary>
        private static Task<List<PrefetchSlot>> _prefetch;

        /// <summary>Cancels the batch above, one per batch, so a batch nobody is waiting for any more
        /// stops asking the server for things the getters are already asking for themselves.
        ///
        /// Without it, giving up on a batch (see <see cref="DropPrefetch"/>) left an orphan worker
        /// that went on issuing all four requests while the main thread's getters re-issued the same
        /// four - up to four duplicate round trips, one of them the raid check's whole-inventory walk,
        /// against a server already slow enough to have missed the panel's deadline.
        ///
        /// It can only stop requests not yet ISSUED: RequestHandler.GetJsonAsync in the installed
        /// spt-common takes a path and nothing else (verified against the assembly - no token
        /// overload), so the one already in flight runs to completion on the pool thread and its
        /// answer is dropped, exactly as an abandoned batch's answers always were.
        ///
        /// Never disposed, deliberately: nothing registers a callback or asks for its WaitHandle, so
        /// the source holds nothing that needs releasing, and disposing it would only put an
        /// ObjectDisposedException in the way of the worker's token reads. Main-thread only, like
        /// _prefetch itself.</summary>
        private static CancellationTokenSource _prefetchCancel;

        /// <summary>Whether the batch is still running. False the moment it has finished - finished
        /// and unpublished is not pending, it is ready.</summary>
        public static bool IsPrefetchPending => _prefetch != null && !_prefetch.IsCompleted;

        /// <summary>Lets go of the batch and tells its worker to stop at the next request boundary.
        /// Every path that stops caring about a batch goes through here, so none of them can leave a
        /// worker running requests nobody will read - see <see cref="_prefetchCancel"/>.</summary>
        private static void DropPrefetch()
        {
            _prefetch = null;

            var cancel = _prefetchCancel;
            _prefetchCancel = null;

            // A no-op on a batch that has already finished, which two of the callers have.
            cancel?.Cancel();
        }

        /// <summary>Starts everything an open fetches, off the main thread: the quest list exactly as
        /// <see cref="BeginFetchAll"/> does, plus whichever of the profile, Kappa, raid check and map
        /// marker payloads is not already cached.
        ///
        /// Written because the quest list was never the only round trip an open made. Each of those
        /// four is a synchronous GetJson at its call site - GetProfile from the gate pass and the tab
        /// strip, GetKappa from the badge pass, GetRaidCheck and GetMapMarkers from the map - under a
        /// fifteen-second cap, on the thread drawing frames. Prefetched here they are in their caches
        /// by the time those call sites run, so each one answers without a request; a payload that
        /// did not arrive is not cached at all, so its getter behaves exactly as it always did, which
        /// is the whole fallback.
        ///
        /// ONE worker, in sequence, rather than four: four concurrent requests through SPT's
        /// RequestHandler for payloads the server derives from one profile buy nothing, and a single
        /// task is one thing to wait on and one thing to abandon. Idempotent - called again while a
        /// batch is in flight it starts nothing.</summary>
        public static void BeginAll()
        {
            BeginFetchAll();

            // A batch that finished while the panel was SHUT is discarded, not published. It was
            // fetched for a stash the player has had every opportunity to change since - a closed
            // panel is exactly when items get moved - and the raid check's whole promise is "if it
            // says you have enough on you, you have enough". The generation guard cannot save it:
            // nothing bumps a generation when a magazine is packed, so a minutes-old answer would
            // pass the guard and latch as this open's. Dropped here, which leaves every gate below
            // reading "uncached" and asking again; nothing is published, and nothing is charged to
            // this open's clock.
            if (_prefetch != null && _prefetch.IsCompleted) DropPrefetch();

            if (_prefetch != null) return;

            // Consumed here as well as in GetMapMarkers, and for the same reason it is consumed
            // there: this is the main thread, and a harvest has to beat both the cache and any answer
            // asked for before it.
            if (_markersStale)
            {
                _markersStale = false;
                _markers = null;
                _markersAttempted = false;
            }

            // Each test is its own getter's cache gate, so "already cached" here means exactly what
            // "answers without a request" means there - a remembered failure included, the map
            // markers' empty-answer hold-off, and the post-timeout hold-off every one of the four now
            // has (see TimedOutRecently). That last one is the whole point of it: a payload the getter
            // is about to answer as unavailable without a request must not be asked for here either,
            // or the next open would spend another fifteen seconds of worker on the hang it just
            // measured.
            var wanted = new List<PrefetchSlot>();

            if (!_profileAttempted && !TimedOutRecently(EPayload.Profile)) wanted.Add(NewSlot(EPayload.Profile));
            if (_kappaResult == null && !TimedOutRecently(EPayload.Kappa)) wanted.Add(NewSlot(EPayload.Kappa));
            if (!_raidCheckAttempted && !TimedOutRecently(EPayload.RaidCheck)) wanted.Add(NewSlot(EPayload.RaidCheck));
            if (!_markersAttempted && DateTime.UtcNow >= _markersRetryAt && !TimedOutRecently(EPayload.Markers))
                wanted.Add(NewSlot(EPayload.Markers));

            if (wanted.Count == 0) return;

            try
            {
                // The token is taken here, on the main thread, and closed over as a VALUE: the worker
                // must never read _prefetchCancel itself, which the next batch overwrites.
                var cancel = new CancellationTokenSource();
                var token = cancel.Token;

                _prefetchCancel = cancel;
                _prefetch = Task.Run(() => FetchPayloadsOffThread(wanted, token));
            }
            catch (Exception ex)
            {
                // A pool that cannot take work at all. Left null, so TryTakeAll answers true with
                // nothing to publish and every getter fetches at its own call site, as before. The
                // source goes with it - there is no worker to stop.
                _prefetchCancel = null;

                Plugin.LogSource?.LogWarning($"QuestTree: could not start the payload prefetch ({ex.Message}).");
            }
        }

        private static PrefetchSlot NewSlot(EPayload payload) =>
            new PrefetchSlot { Payload = payload, Generation = GenerationOf(payload) };

        /// <summary>Stops waiting on the batch in flight and leaves nothing behind, so the next
        /// <see cref="BeginAll"/> starts a fresh one. <see cref="AbandonFetch"/>'s reasoning exactly:
        /// a worker that never comes back would otherwise be waited out on every later open.
        ///
        /// Cancelled as well as dropped, unlike the quest fetch: this worker still has up to three
        /// requests left to issue, and the getters are about to issue those same three on the main
        /// thread the moment this returns. See <see cref="_prefetchCancel"/> for what cancellation can
        /// and cannot stop.</summary>
        public static void AbandonAll() => DropPrefetch();

        /// <summary>Every wanted payload on one pool thread: request, deserialise, Sanitise, then the
        /// next one. No Unity API, no logging and no static state written in here - only the slots it
        /// was handed and the token it was handed, which is what makes it safe off the main thread,
        /// and the reason it cannot use GetJson: that writes the fetch counters.</summary>
        private static List<PrefetchSlot> FetchPayloadsOffThread(List<PrefetchSlot> slots, CancellationToken cancel)
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();

            foreach (var slot in slots)
            {
                // Before each request and never during one: nobody is waiting for this batch any
                // more, and every payload left in it is one the main thread is about to ask for
                // itself. Each remaining slot is still given its reason, so a batch that somehow
                // reaches a publish explains itself in the log like any other failure.
                if (cancel.IsCancellationRequested)
                {
                    slot.Failure = "the prefetch was given up on before this payload was asked for";
                    continue;
                }

                if (budget.Elapsed > PrefetchBudget)
                {
                    // Left absent rather than asked for late - see PrefetchBudget. Absent is the case
                    // every one of these getters already handles. Flagged as well as explained,
                    // because what ate the budget decides what the main thread does with this slot -
                    // see ApplyTimeoutHoldOffs.
                    slot.Skipped = true;
                    slot.Failure = "the prefetch was out of time before this payload was asked for";
                    continue;
                }

                switch (slot.Payload)
                {
                    case EPayload.Profile:
                        Fetch<ProfilePayloadDto>(slot, ProfileRoute, Sanitise);
                        break;

                    case EPayload.Kappa:
                        Fetch<KappaPayloadDto>(slot, KappaRoute, Sanitise);
                        break;

                    case EPayload.RaidCheck:
                        Fetch<RaidCheckDto>(slot, RaidCheckRoute, Sanitise);
                        break;

                    case EPayload.Markers:
                        Fetch<MapMarkerPayloadDto>(slot, MapMarkerRoute, Sanitise);
                        break;
                }
            }

            return slots;
        }

        /// <summary>One payload's request and parse on the worker, measured in halves and charged to
        /// the half that failed - FetchQuestsOffThread's shape, for the reasons written there.</summary>
        private static void Fetch<T>(PrefetchSlot slot, string route, Action<T> sanitise) where T : class
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var answered = false;
            var parsed = false;

            try
            {
                var request = RequestHandler.GetJsonAsync(route);

                // The request is out, whatever comes of it. Set after the call rather than before, so
                // a GetJsonAsync that throws on its way out is not counted as a round trip.
                slot.Requested = true;

                // The same cap GetJson applies, on a pool thread that has nothing else to do.
                if (!request.Wait(RequestTimeout))
                {
                    // Recorded on the slot as well as in the message: this is the one failure the main
                    // thread holds off on - see PrefetchTimeoutHoldOff.
                    slot.TimedOut = true;

                    throw new TimeoutException($"no answer within {RequestTimeout.TotalSeconds:0}s");
                }

                var json = request.Result;
                slot.ServerMillis = clock.ElapsedMilliseconds;
                answered = true;

                // How a route no mod registered presents itself - see FetchKappa. Each getter has its
                // own sentence for that, and says it when it goes and asks for itself.
                if (string.IsNullOrEmpty(json))
                {
                    slot.Failure = "the server returned an empty response";
                    return;
                }

                var payload = JsonConvert.DeserializeObject<T>(json);
                sanitise(payload);
                slot.ParseMillis = clock.ElapsedMilliseconds - slot.ServerMillis;
                parsed = true;

                if (payload == null) slot.Failure = "the server sent nothing usable";
                else slot.Value = payload;

                return;
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                // The real failure, not "One or more errors occurred" - it goes into a log line.
                slot.Failure = ex.InnerException.Message;
            }
            catch (Exception ex)
            {
                slot.Failure = ex.Message;
            }

            // A half that failed is still charged to itself, or it would surface as main-thread time.
            if (!answered) slot.ServerMillis = clock.ElapsedMilliseconds;
            else if (!parsed) slot.ParseMillis = clock.ElapsedMilliseconds - slot.ServerMillis;
        }

        /// <summary>Publishes a FINISHED batch into the very caches the synchronous getters read, on
        /// the main thread, and hands back what each payload cost the worker.
        ///
        /// False only while the batch is still running, which is the caller's signal to keep
        /// yielding. True with an empty list when there was nothing to fetch, nothing left to
        /// publish, or no thread to fetch on - in every one of those the getters answer exactly as
        /// they always have.
        ///
        /// Idempotent: the batch is let go of before anything is published, so a second call finds
        /// nothing and publishes nothing.</summary>
        public static bool TryTakeAll(out List<PrefetchPhase> phases)
        {
            phases = new List<PrefetchPhase>();

            var prefetch = _prefetch;

            if (prefetch == null) return true;
            if (!prefetch.IsCompleted) return false;

            // Cleared before anything below can throw, so one batch is applied exactly once. Through
            // DropPrefetch like every other let-go: the batch has finished, so the cancel is a no-op,
            // and what matters is that the source does not outlive the batch it belonged to.
            DropPrefetch();

            List<PrefetchSlot> slots;

            try
            {
                // Completed, so this cannot block; it can still throw if the pool lost the work
                // itself, which Fetch's own catches would never see.
                slots = prefetch.Result;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: the payload prefetch did not come back ({ex.Message}).");
                return true;
            }

            // Before the publish loop, because it reads the batch as a WHOLE: what one slot's timeout
            // means for a slot that was never reached cannot be decided one slot at a time.
            var heldOff = ApplyTimeoutHoldOffs(slots);

            foreach (var slot in slots)
            {
                if (slot == null) continue;

                var name = NameOf(slot.Payload);

                // Charged even when the payload failed: a request that timed out is time the player
                // waited, and none of it is the main thread's to answer for. Into the same counters
                // the synchronous fetches feed - see FetchMillis - so the open's remaining
                // main-thread ": server" splits still measure only what the main thread did.
                //
                // Only for a slot a request was actually ISSUED for, though. A slot the budget skipped
                // or cancellation stopped never went to the server: it has no milliseconds to charge,
                // and counting it made FetchCount - "how many round trips this open made" - report
                // four where one was made. Its phase pair goes with it, because ": prefetch 0" next to
                // a ": server" that is about to be the real fifteen seconds reads as the prefetch
                // having covered a payload nobody asked for.
                if (slot.Requested)
                {
                    phases.Add(new PrefetchPhase(name, slot.ServerMillis, slot.ParseMillis));
                    FetchMillis += slot.ServerMillis;
                    FetchCount++;
                }

                // The accept-then-hang server, and whatever inherited its hold-off. Placed before the
                // generation guard below, because it is a fact about the SERVER rather than about this
                // answer's freshness: an Invalidate* says the answer changed, not that the server
                // started answering. There is never a value to publish on either path, ApplyTimeoutHoldOffs
                // has already said so in one line, and the "will be fetched where it is used" line further
                // down would be a lie about a payload now held off - so nothing else in the loop applies.
                if (heldOff.Contains(slot.Payload)) continue;

                // An Invalidate* while this was in flight means the answer predates the change the
                // caller was announcing. Dropped, and nothing is cached, so the getter asks again at
                // its own call site - which is the behaviour without any of this.
                if (slot.Generation != GenerationOf(slot.Payload))
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the prefetched {name} payload was invalidated while it was in flight - dropped.");
                    continue;
                }

                // Deliberately NOT cached as a failure: each getter's own failure handling - its
                // sentence in the log, its Kappa status, its retry hold-off - is the one that has to
                // apply, and it applies by the getter finding nothing cached and asking itself.
                if (slot.Value == null)
                {
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the {name} payload could not be prefetched ({slot.Failure ?? "no reason given"}) - " +
                        "it will be fetched where it is used.");
                    continue;
                }

                // Switched on the value's TYPE rather than on the slot's payload, because the type
                // is what decides which cache this belongs in: a cast that went wrong would hand a
                // publish a null and latch "there is no profile" over a payload that arrived.
                switch (slot.Value)
                {
                    case ProfilePayloadDto profile:
                        PublishProfile(profile);
                        break;

                    case KappaPayloadDto kappa:
                        PublishKappa(kappa);
                        break;

                    case RaidCheckDto raidCheck:
                        PublishRaidCheck(raidCheck);
                        break;

                    case MapMarkerPayloadDto markers:
                        PublishMarkers(markers);
                        break;
                }
            }

            return true;
        }

        /// <summary>Turns a finished batch's timeouts into hold-offs and returns every payload that now
        /// has one, so the publish loop can leave those slots alone. One pass over the whole batch
        /// rather than a test per slot, because of what it has to decide:
        ///
        /// a payload the BUDGET never reached inherits the hold-off of a payload that timed out in the
        /// same batch. The evidence is the batch itself - a server that let one request sit for a full
        /// <see cref="RequestTimeout"/> is not a healthy server for the next twenty seconds, and the
        /// budget ran out precisely BECAUSE of that request. Without the inheritance the arithmetic
        /// defeats the whole hold-off: <see cref="PrefetchBudget"/> is twenty seconds and the cap
        /// fifteen, and the budget is tested before each request, so a batch against a hanging server
        /// issues two requests (0 s and 15 s) and skips the rest at 30 s - leaving the last two
        /// payloads with no hold-off and their getters to block the frame thread for fifteen seconds
        /// each, inside the very open this exists to keep free of that.
        ///
        /// Two things deliberately do NOT inherit:
        ///   - a CANCELLED slot (<see cref="PrefetchSlot.Skipped"/> false with nothing requested): the
        ///     panel gave up on the batch, so the server never got the chance to be slow about that
        ///     payload, and its getter is about to ask on the main thread by the caller's own choice;
        ///   - anything at all in a batch where NOTHING timed out. A budget spent on requests that all
        ///     answered says the server is slow, not silent, and a slow server still beats no answer -
        ///     those getters ask as they always did.
        ///
        /// One log line for the batch, not one per payload: four lines saying the same thing about one
        /// hanging server is how a log stops being read.</summary>
        private static HashSet<EPayload> ApplyTimeoutHoldOffs(List<PrefetchSlot> slots)
        {
            var heldOff = new HashSet<EPayload>();
            if (slots == null) return heldOff;

            var timedOut = new List<string>();
            var inherited = new List<string>();

            foreach (var slot in slots)
                if (slot != null && slot.TimedOut)
                {
                    heldOff.Add(slot.Payload);
                    timedOut.Add(NameOf(slot.Payload));
                }

            // Nothing hung, so nothing is held off - not even a slot the budget skipped.
            if (timedOut.Count == 0) return heldOff;

            foreach (var slot in slots)
                if (slot != null && slot.Skipped && !slot.TimedOut && !heldOff.Contains(slot.Payload))
                {
                    heldOff.Add(slot.Payload);
                    inherited.Add(NameOf(slot.Payload));
                }

            var until = DateTime.UtcNow + PrefetchTimeoutHoldOff;
            foreach (var payload in heldOff) _prefetchTimedOutUntil[(int)payload] = until;

            var many = timedOut.Count > 1;
            var line =
                $"QuestTree: the {string.Join(", ", timedOut)} payload{(many ? "s" : "")} did not answer within " +
                $"{RequestTimeout.TotalSeconds:0}s - treating {(many ? "them" : "it")} as unavailable for " +
                $"{PrefetchTimeoutHoldOff.TotalSeconds:0}s rather than waiting for {(many ? "them" : "it")} again on " +
                "the thread drawing frames";

            if (inherited.Count > 0)
            {
                var manyMore = inherited.Count > 1;
                line += $"; {string.Join(", ", inherited)} {(manyMore ? "were" : "was")} not asked for after that, " +
                        $"so {(manyMore ? "they are" : "it is")} held off on the same evidence";
            }

            Plugin.LogSource?.LogWarning(line + ".");
            return heldOff;
        }

        /// <summary>The phase prefix the open-time line prints for each payload, and the word the
        /// prefetch's own lines use for it.</summary>
        private static string NameOf(EPayload payload) => payload switch
        {
            EPayload.Profile => "profile",
            EPayload.Kappa => "kappa",
            EPayload.RaidCheck => "raid",
            _ => "markers"
        };

        /// <summary>GetProfile's tail without the request, so a prefetched profile is
        /// indistinguishable from a fetched one.
        ///
        /// Every publish below begins by standing down if something already answered: a getter that
        /// ran on the main thread while the worker was out fetched LATER than this did, so its answer
        /// is the fresher one even when it failed - and a latched failure is asked about again by
        /// RetryFailedFetches, which is the path that already exists for it.</summary>
        private static void PublishProfile(ProfilePayloadDto payload)
        {
            if (_profileAttempted) return;

            WarnProfileSchema(payload);
            _profile = payload;
            _profileAttempted = true;
        }

        private static void PublishRaidCheck(RaidCheckDto payload)
        {
            if (_raidCheckAttempted) return;

            WarnRaidCheckSchema(payload);
            _raidCheck = payload;
            _raidCheckAttempted = true;
        }

        /// <summary>The Kappa payload is judged here rather than on the worker, by the same method
        /// FetchKappa judges its own with: a version mismatch is a sentence in the log and a status
        /// the Kappa tab explains, and both belong on the main thread.</summary>
        private static void PublishKappa(KappaPayloadDto payload)
        {
            if (_kappaResult != null) return;

            _kappaResult = JudgeKappa(payload);
        }

        /// <summary>GetMapMarkers' tail without the request: the empty-answer hold-off, the schema
        /// note and the count line, so the log reads the same whichever path fetched it.</summary>
        private static void PublishMarkers(MapMarkerPayloadDto payload)
        {
            if (_markersAttempted) return;

            _markers = payload;

            // Empty is not a failure and must not latch - the server answers empty, uncached, for a
            // minute after a failed marker build. The getter's own hold-off, applied here too.
            if (payload?.Maps == null || payload.Maps.Count == 0)
            {
                _markersRetryAt = DateTime.UtcNow.AddSeconds(60);
                Plugin.LogSource?.LogInfo("QuestTree: the server sent no map markers - asking again in a minute.");
                return;
            }

            _markersAttempted = true;
            NoteMarkers(payload);
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

            // Any prefetch of it in flight is now answering a question that has changed - see
            // _generations. Every Invalidate* below says the same thing the same way.
            Invalidated(EPayload.RaidCheck);
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

            // The prefetch waited out the full cap on this one - and this is the payload whose request
            // is the expensive one, a walk of the whole inventory. Null is the NEUTRAL answer, so the
            // pre-raid screen shows nothing rather than a wrong "you are ready"; see
            // PrefetchTimeoutHoldOff.
            if (TimedOutRecently(EPayload.RaidCheck)) return null;

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
                WarnRaidCheckSchema(payload);

                _raidCheck = payload;
                return _raidCheck;
            }
            catch (Exception ex)
            {
                LogUnavailable(ex.Message);
                return null;
            }
        }

        /// <summary>The raid check's schema note - as WarnProfileSchema, and for the same
        /// two-paths reason.</summary>
        private static void WarnRaidCheckSchema(RaidCheckDto payload)
        {
            if (payload == null || payload.SchemaVersion == RaidCheckDto.SupportedSchemaVersion) return;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: raid check payload schema v{payload.SchemaVersion} but this client expects " +
                $"v{RaidCheckDto.SupportedSchemaVersion} (server mod {payload.ModVersion}).");
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

            // The prefetch of this payload waited out the full cap a moment ago. Answered as
            // unavailable - which is what null means here - rather than spending another fifteen
            // seconds of the frame thread learning the same thing. Nothing is cached and the flag is
            // deliberately left false: see PrefetchTimeoutHoldOff for why this is not a latch.
            if (TimedOutRecently(EPayload.Profile)) return null;

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
                WarnProfileSchema(payload);

                _profile = payload;
                return _profile;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not reach {ProfileRoute} ({ex.Message}).");
                return null;
            }
        }

        /// <summary>The profile payload's schema note. Its own method because the open now has two
        /// ways to obtain this payload - GetProfile's own request and the prefetch's - and both must
        /// say the same thing about a server half that does not match.</summary>
        private static void WarnProfileSchema(ProfilePayloadDto payload)
        {
            if (payload == null || payload.SchemaVersion == ProfilePayloadDto.SupportedSchemaVersion) return;

            Plugin.LogSource?.LogWarning(
                $"QuestTree: profile payload schema v{payload.SchemaVersion} ({SchemaNote(payload.SchemaVersion, ProfilePayloadDto.SupportedSchemaVersion)}) but this client expects " +
                $"v{ProfilePayloadDto.SupportedSchemaVersion} (server mod {payload.ModVersion}).");
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
        public static void InvalidateMapMarkers()
        {
            _markersStale = true;

            // Interlocked, because this is the one Invalidate* called off the main thread.
            Invalidated(EPayload.Markers);
        }

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

            // And the same hold-off for the OTHER reason not to ask: the prefetch of this payload timed
            // out. The line above is for a server that answered empty, this one for a server that did
            // not answer at all; both return _markers, which is null here, and the map draws no pins.
            if (TimedOutRecently(EPayload.Markers)) return _markers;

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

                NoteMarkers(_markers);
                return _markers;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not reach {MapMarkerRoute} ({ex.Message}).");
                return null;
            }
        }

        /// <summary>What a usable marker payload is worth saying about: the schema it speaks and how
        /// many pins came with it. Shared by GetMapMarkers and the prefetch's publish, so one line is
        /// written per payload whichever of them fetched it.</summary>
        private static void NoteMarkers(MapMarkerPayloadDto payload)
        {
            if (payload == null) return;

            if (payload.SchemaVersion != MapMarkerPayloadDto.SupportedSchemaVersion)
            {
                // Not fatal: the maps are the one feature this payload carries, and a version
                // that only differs in a field this client does not read still pins fine. Named
                // in the log so a mis-drawn map has a first place to look.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: map marker payload schema v{payload.SchemaVersion} ({SchemaNote(payload.SchemaVersion, MapMarkerPayloadDto.SupportedSchemaVersion)}) but this client expects " +
                    $"v{MapMarkerPayloadDto.SupportedSchemaVersion} (server mod {payload.Version}). " +
                    "Update both halves of the mod together.");
            }

            var count = 0;
            foreach (var map in payload.Maps)
                count += map?.Markers?.Count ?? 0;

            Plugin.LogSource?.LogInfo($"QuestTree: loaded {count} quest-item map markers.");
        }

        /// <summary>Drops the cached profile so the next GetProfile re-fetches. Paired with
        /// InvalidateKappa - the same events move both.</summary>
        public static void InvalidateProfile()
        {
            _profile = null;
            _profileAttempted = false;
            Invalidated(EPayload.Profile);
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

            // The prefetch of this payload timed out a moment ago. Unreachable is what the Kappa tab
            // already explains in words for a server that cannot be reached, and it is deliberately NOT
            // stored in _kappaResult: cached it would be a latched failure needing InvalidateKappa or
            // RetryFailedFetches to undo, where this expires on its own - see PrefetchTimeoutHoldOff.
            if (TimedOutRecently(EPayload.Kappa))
                return KappaFetchResult.Failed(EKappaFetchStatus.Unreachable);

            _kappaResult = FetchKappa();
            return _kappaResult;
        }

        /// <summary>Drops the cached Kappa result so the next <see cref="GetKappa"/> re-fetches.</summary>
        public static void InvalidateKappa()
        {
            _kappaResult = null;
            Invalidated(EPayload.Kappa);
        }

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

            // And the other four payloads' batch, for the same reason. Cancelled as well as dropped -
            // AbandonAll's reasoning - so its worker stops asking the server this call is forgetting for
            // payloads nobody will read. The generations move with it, so even a batch handed to a take
            // by some later path cannot publish what was fetched for the profile or server this call is
            // forgetting.
            DropPrefetch();
            for (var i = 0; i < _generations.Length; i++) Invalidated((EPayload)i);

            // Every hold-off as well: a server that hung, or that answered with no markers at all, is the
            // PREVIOUS server. A new one must be asked, not told for half a minute that the old one's
            // silence still stands, nor kept off the markers for a minute over the old one's empty answer.
            // That empty-answer window used to be cleared on its own line up above; ClearHoldOffs covers
            // both, and nothing between here and there reads either field.
            ClearHoldOffs();
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

                return JudgeKappa(payload);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not reach {KappaRoute} ({ex.Message}).");
                return KappaFetchResult.Failed(EKappaFetchStatus.Unreachable);
            }
        }

        /// <summary>What a parsed Kappa payload amounts to: the checklist, or the status the Kappa
        /// tab explains in words. Separate from the request because the prefetch parses this payload
        /// on a worker and the judgement - two log lines and a version compare - belongs on the main
        /// thread, said once, by whichever path got there first.</summary>
        private static KappaFetchResult JudgeKappa(KappaPayloadDto payload)
        {
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

        private static void LogUnavailable(string reason)
        {
            Plugin.LogSource?.LogWarning(
                $"QuestTree: could not reach the QuestTreeServer companion mod on {Route} ({reason}). " +
                "Falling back to only the quests this profile has already unlocked - install the server half " +
                "(SPT_Runtime/user/mods/QuestTree) to see the whole quest tree.");
        }
    }
}
