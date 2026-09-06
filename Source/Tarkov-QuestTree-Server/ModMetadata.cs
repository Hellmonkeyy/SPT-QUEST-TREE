using System.Collections.Generic;
using SemanticVersioning;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace QuestTreeServer
{
    /// <summary>
    /// Describes this mod to the SPT server. Exactly one implementation per mod is required, and
    /// every property has to be assigned - null for the optional ones we do not use.
    ///
    /// Shares the "com.takov.questtree" GUID family with the BepInEx client half deliberately: the
    /// two are one mod shipped in two halves, and the server checks GUIDs only for uniqueness among
    /// server mods.
    /// </summary>
    public record ModMetadata : IModMetadata
    {
        public string ModGuid { get; init; } = "com.takov.questtree.server";

        public string Name { get; init; } = "Quest Tracker Server";

        public string Author { get; init; } = "Takov";

        public List<string>? Contributors { get; init; }

        /// <summary>Built from <see cref="ModInfo.Version"/> so the version the server logs is the
        /// same one the payloads carry - they drifted apart while both were written out by hand.</summary>
        public Version Version { get; init; } = new(ModInfo.Version);

        public Range SptVersion { get; init; } = new("~4.1.0");

        public List<string>? Incompatibilities { get; init; }

        public Dictionary<string, Range>? ModDependencies { get; init; }

        /// <summary>Shown in the launcher's mod list. Left null deliberately - there is no mod page
        /// yet, and a placeholder URL is worse than none. Set it when there is somewhere real to
        /// point at.</summary>
        public string? Url { get; init; }

        public bool HasPrepatcher { get; init; }

        public string License { get; init; } = "MIT";
    }
}
