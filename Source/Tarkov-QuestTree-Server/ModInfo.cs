using System.Reflection;

namespace QuestTreeServer
{
    /// <summary>
    /// One place the server half's version is written down. It goes into the mod metadata AND into
    /// every payload, so when a player has mismatched halves the client can name both versions
    /// instead of reporting a vague failure - which is exactly the confusion that turned up when
    /// the mod was first shared and a client hit a server without the Kappa route.
    ///
    /// Keep in step with QuestTree.ModInfo.Version in the client half.
    ///
    /// This is the source of truth for every version the server reports, ModMetadata included.
    /// Only QuestTreeServer.csproj's AssemblyVersion has to be edited alongside it, because
    /// MSBuild cannot read a C# constant.
    /// </summary>
    public static class ModInfo
    {
        public const string Version = "1.12.1";

        private static string? _stamp;

        /// <summary>The version plus the git hash the build came from ("1.7.1+0c50f09", with
        /// "-dirty" when built from uncommitted changes) - the csproj writes it into the assembly's
        /// informational version. For log lines; the mismatch check compares Version only.</summary>
        public static string Stamp => _stamp ??=
            typeof(ModInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Version;
    }
}
