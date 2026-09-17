using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Client-side mirror of /questtree/builds: the weapon builds as THIS player can assemble them.
    /// Hand-mirrored like every other payload here, for the same reason - the two halves target
    /// different frameworks and cannot share a file.
    ///
    /// The quest payload's build is the SHARED one, solved over every part that exists; this is the
    /// per-profile answer laid over it, joined by WeaponBuildDto.Key. Where the two differ the player
    /// wants this one: it is made of parts they can actually get, or it says plainly why none is.
    /// </summary>
    internal sealed class ProfileBuildsDto
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        /// <summary>False out of game. Neutral, not a verdict.</summary>
        [JsonProperty("hasProfile")]
        public bool HasProfile { get; set; }

        /// <summary>True when Builds is current for this profile's traders and stash. False with an
        /// empty list means the server has not finished working it out; false with builds means they
        /// are stale - see Stale - and a fresh answer is on its way.</summary>
        [JsonProperty("ready")]
        public bool Ready { get; set; }

        [JsonProperty("stale")]
        public bool Stale { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("fleaAccess")]
        public bool FleaAccess { get; set; }

        [JsonProperty("fleaLevel")]
        public int FleaLevel { get; set; }

        [JsonProperty("builds")]
        public List<ProfileBuildDto> Builds { get; set; } = new List<ProfileBuildDto>();

        /// <summary>The answer for one requirement, or null when the payload has none for it - which
        /// is neutral: the shared build is shown instead, as it always was.</summary>
        public ProfileBuildDto BuildFor(string key)
        {
            if (string.IsNullOrEmpty(key) || Builds == null) return null;

            foreach (var build in Builds)
                if (build != null && string.Equals(build.Key, key, StringComparison.Ordinal))
                    return build;

            return null;
        }
    }

    internal sealed class ProfileBuildDto
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("questName")]
        public string QuestName { get; set; }

        [JsonProperty("weaponTemplate")]
        public string WeaponTemplate { get; set; }

        [JsonProperty("weaponName")]
        public string WeaponName { get; set; }

        /// <summary>"ok" - the shared build, every part obtainable; "repaired" - searched again within
        /// what this profile can get, and passed the server's independent verifier; "blocked" - no
        /// build within reach, see Unmet and Why; "unsolved" - no shared build to start from.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>For ok and repaired, the build. For blocked, the closest attempt from obtainable
        /// parts - what it misses is in Unmet.</summary>
        [JsonProperty("parts")]
        public List<ProfilePartDto> Parts { get; set; } = new List<ProfilePartDto>();

        /// <summary>Roubles at this profile's trader prices for every buyable part. Barters are
        /// counted, not priced; flea parts are estimated separately because that price moves.</summary>
        [JsonProperty("cash")]
        public long Cash { get; set; }

        [JsonProperty("barters")]
        public int Barters { get; set; }

        [JsonProperty("fleaEstimate")]
        public long FleaEstimate { get; set; }

        /// <summary>Blocked only: the thresholds the closest obtainable attempt missed, with the margin.</summary>
        [JsonProperty("unmet")]
        public List<string> Unmet { get; set; } = new List<string>();

        /// <summary>Blocked only. Starts with "trader level" (naming parts, traders and levels), "flea
        /// market" (the level that unlocks it) or "not sold".</summary>
        [JsonProperty("why")]
        public string Why { get; set; }

        /// <summary>Repaired only: the server's independent verifier passed this build.</summary>
        [JsonProperty("verified")]
        public bool Verified { get; set; }

        [JsonProperty("nodes")]
        public int Nodes { get; set; }
    }

    internal sealed class ProfilePartDto
    {
        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("template")]
        public string Template { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>"fitted" (on the weapon's default preset), "inplace" (already on a copy of the
        /// quest's weapon you own), "owned" (loose in your stash), "buyable" (a trader sells it - see
        /// Price), "barter" (a trader offers it for goods), "flea" (Price is an estimate), "absent".</summary>
        [JsonProperty("tier")]
        public string Tier { get; set; }

        /// <summary>Roubles for buyable and flea; zero for fitted, inplace and owned; null for barter
        /// and absent. Null is "no price exists", never "free".</summary>
        [JsonProperty("price")]
        public long? Price { get; set; }

        /// <summary>Absent parts only: the trader and loyalty level that would sell it, if any.</summary>
        [JsonProperty("gate")]
        public string Gate { get; set; }

        /// <summary>Where a copy you hold is fitted, when it is not loose: "fitted to your MDR",
        /// "fitted to your equipped MDR". Beside the price, never instead of it.</summary>
        [JsonProperty("where")]
        public string Where { get; set; }

        /// <summary>A part the quest itself names. No build can avoid it.</summary>
        [JsonProperty("named")]
        public bool Named { get; set; }
    }
}
