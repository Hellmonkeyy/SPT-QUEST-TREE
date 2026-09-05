namespace QuestTree
{
    /// <summary>
    /// One place the client half's version is written down, so a mismatch message can name both
    /// sides. Keep in step with QuestTreeServer.ModInfo.Version in the server half - they are
    /// shipped together and are meant to match.
    /// </summary>
    internal static class ModInfo
    {
        public const string Version = "1.1.0";
    }
}
