using System.Reflection;

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
        public const string Version = "1.8.3";

        private static string _stamp;

        /// <summary>The version plus the git hash the build came from ("1.7.1+0c50f09", with
        /// "-dirty" when built from uncommitted changes) - the csproj writes it into the assembly's
        /// informational version. For log lines; the mismatch check compares Version only.</summary>
        public static string Stamp => _stamp ??=
            typeof(ModInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Version;
    }
}
