namespace QuestTree.QuestGraph
{
    /// <summary>Why a Kappa fetch did not produce usable data. The distinction matters to the
    /// player: "you installed half the mod" and "the server is down" need completely different
    /// fixes, and an unhelpful "the server did not answer" led to exactly that confusion in
    /// testing.</summary>
    internal enum EKappaFetchStatus
    {
        Ok,

        /// <summary>The route answered with nothing. SPT logs `[UNHANDLED][/questtree/kappa]` and
        /// returns an empty body for a route no mod has registered, so an empty response is the
        /// signal that the server half is absent or predates this route.</summary>
        ServerHalfMissing,

        /// <summary>Both halves are present but were built from different versions of the mod.</summary>
        VersionMismatch,

        /// <summary>The request itself failed - server not running, or not reachable.</summary>
        Unreachable
    }

    /// <summary>One Kappa fetch: the payload when it worked, and why not when it did not.</summary>
    internal sealed class KappaFetchResult
    {
        public EKappaFetchStatus Status { get; private set; }
        public KappaPayloadDto Payload { get; private set; }

        /// <summary>Server-reported mod version, when it told us one. Empty for older server halves
        /// that predate the field, which is itself a useful signal.</summary>
        public string ServerVersion { get; private set; } = "";

        public bool IsOk => Status == EKappaFetchStatus.Ok && Payload != null;

        public static KappaFetchResult Ok(KappaPayloadDto payload) =>
            new() { Status = EKappaFetchStatus.Ok, Payload = payload, ServerVersion = payload?.ModVersion ?? "" };

        public static KappaFetchResult Failed(EKappaFetchStatus status, string serverVersion = "") =>
            new() { Status = status, ServerVersion = serverVersion ?? "" };

        /// <summary>A player-facing explanation with the actual fix, not just a symptom.</summary>
        public string Explain(string clientVersion) => Status switch
        {
            EKappaFetchStatus.ServerHalfMissing =>
                "The server half of this mod is missing, or is older than the client half. " +
                "Copy <b>SPT_Runtime\\user\\mods\\QuestTree</b> from the same download as your " +
                "BepInEx plugin, then restart the server.",

            EKappaFetchStatus.VersionMismatch =>
                $"Both halves are installed but from different versions - server {ServerVersion}, " +
                $"client {clientVersion}. Reinstall both from the same download.",

            EKappaFetchStatus.Unreachable =>
                "Could not reach the SPT server. If you are playing on someone else's Fika server, " +
                "they need the server half installed too.",

            _ => ""
        };
    }
}
