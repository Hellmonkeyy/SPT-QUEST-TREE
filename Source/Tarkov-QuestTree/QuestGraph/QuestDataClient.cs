using System;
using System.Collections.Generic;
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

        private static List<QuestDto> _cached;
        private static bool _attempted;

        /// <summary>The full quest list, or null when the companion server mod is not installed or
        /// did not answer. Fetched once per game session - the quest database cannot change while
        /// the server is running, so there is nothing to invalidate.</summary>
        public static List<QuestDto> TryFetchAll()
        {
            if (_attempted) return _cached;
            _attempted = true;

            try
            {
                // Synchronous by design: this is called from the panel's own open path, once, and
                // the tree cannot be drawn before the data arrives anyway.
                var json = RequestHandler.GetJson(Route);

                if (string.IsNullOrEmpty(json))
                {
                    LogUnavailable("the server returned an empty response");
                    return null;
                }

                var payload = JsonConvert.DeserializeObject<QuestPayloadDto>(json);

                if (payload?.Quests == null || payload.Quests.Count == 0)
                {
                    LogUnavailable("the server returned no quests");
                    return null;
                }

                if (payload.SchemaVersion != QuestPayloadDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the QuestTreeServer mod speaks payload schema v{payload.SchemaVersion} but this " +
                        $"client expects v{QuestPayloadDto.SupportedSchemaVersion}. Update both halves of the mod to " +
                        "the same version. Continuing anyway - some fields may be missing.");
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

        private static KappaFetchResult _kappaResult;

        /// <summary>
        /// The Kappa/Collector checklist for the current profile.
        ///
        /// Cached, including failures. It reports the live stash, so it must not be cached
        /// *forever* - but it was previously re-fetched on every panel re-render, and
        /// <see cref="QuestTreePanel"/> re-renders on every search keystroke and settings toggle.
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

        private static KappaFetchResult FetchKappa()
        {
            try
            {
                var json = RequestHandler.GetJson(KappaRoute);

                // SPT answers a route no mod registered by logging [UNHANDLED] and returning an
                // empty body, so "empty" is how a missing or outdated server half presents itself.
                if (string.IsNullOrEmpty(json))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {KappaRoute} returned nothing - the server half is missing or predates this route.");
                    return KappaFetchResult.Failed(EKappaFetchStatus.ServerHalfMissing);
                }

                var payload = JsonConvert.DeserializeObject<KappaPayloadDto>(json);
                if (payload == null)
                    return KappaFetchResult.Failed(EKappaFetchStatus.ServerHalfMissing);

                if (payload.SchemaVersion != KappaPayloadDto.SupportedSchemaVersion)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: Kappa payload schema v{payload.SchemaVersion} but this client expects " +
                        $"v{KappaPayloadDto.SupportedSchemaVersion} (server mod {payload.ModVersion}). " +
                        "Reinstall both halves from the same download.");
                    return KappaFetchResult.Failed(EKappaFetchStatus.VersionMismatch, payload.ModVersion);
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
