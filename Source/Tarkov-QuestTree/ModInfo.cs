namespace QuestTree
{
    /// <summary>
    /// One place the client half's version is written down, so a mismatch message can name both
    /// sides. Keep in step with QuestTreeServer.ModInfo.Version in the server half - they are
    /// shipped together and are meant to match.
    ///
    /// This is the source of truth for every version the client reports, BepInPlugin included.
    /// Only QuestTree.csproj's AssemblyVersion has to be edited alongside it, because MSBuild
    /// cannot read a C# constant.
    /// </summary>
    internal static class ModInfo
    {
        public const string Version = "1.3.0";
    }
}
