using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Client-side mirror of /questtree/raidcheck. Hand-mirrored like every other payload here: the
    /// two halves target different frameworks, so there is no project both can reference.
    ///
    /// What you must be CARRYING to finish the quests on each map. The server reports facts and
    /// reaches no verdict - only the client knows the "count quests you have not accepted" setting,
    /// and only the client has ENodeStatus where it has a graph at all.
    /// </summary>
    internal sealed class RaidCheckDto
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        /// <summary>False out of game, where there is no profile to read. Neutral, not a verdict.</summary>
        [JsonProperty("hasProfile")]
        public bool HasProfile { get; set; }

        /// <summary>False when the inventory roots could not be read, so on-person counts are
        /// unknown rather than zero. Neutral - never green, and never a bare "missing".</summary>
        [JsonProperty("inventoryLocationsKnown")]
        public bool InventoryLocationsKnown { get; set; }

        /// <summary>Carry conditions that belong to no map at all. Reported and logged; it gates
        /// NOTHING - on a fully harvested install it is 1, and gating on it would leave every map
        /// permanently neutral.</summary>
        [JsonProperty("conditionsWithNoMap")]
        public int ConditionsWithNoMap { get; set; }

        [JsonProperty("maps")]
        public List<RaidCheckMapDto> Maps { get; set; } = new List<RaidCheckMapDto>();

        /// <summary>Template -> what the profile holds. An item wanted by six conditions is held
        /// exactly once, so the counts live here rather than on every row.</summary>
        [JsonProperty("held")]
        public Dictionary<string, HeldItemDto> Held { get; set; } = new Dictionary<string, HeldItemDto>();

        /// <summary>Template -> resolved display name. Needed because a row is named after the
        /// template actually HELD, and the client has no locale table to turn an id into words.</summary>
        [JsonProperty("itemNames")]
        public Dictionary<string, string> ItemNames { get; set; } = new Dictionary<string, string>();

        /// <summary>The row for a map, or null when the payload has none - which is its own neutral
        /// case and must NOT be read as "nothing to bring here". The server emits a row for every
        /// real location, so a miss means a map it does not recognise.</summary>
        public RaidCheckMapDto MapFor(string locationKey)
        {
            if (string.IsNullOrEmpty(locationKey) || Maps == null) return null;

            foreach (var map in Maps)
                if (map != null && locationKey.Equals(map.LocationKey, System.StringComparison.OrdinalIgnoreCase))
                    return map;

            return null;
        }
    }

    internal sealed class RaidCheckMapDto
    {
        /// <summary>Internal name, in QuestDto.LocationKey's keyspace - not the canonical one, so
        /// Factory night and Ground Zero above level 20 each have their own row.</summary>
        [JsonProperty("locationKey")]
        public string LocationKey { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Conditions listed here by assumption rather than by a harvested zone. The rows
        /// still show, but the map may not go green.</summary>
        [JsonProperty("unplaceableConditions")]
        public int UnplaceableConditions { get; set; }

        /// <summary>Whether this map has harvested zones at all. Green requires positive evidence.</summary>
        [JsonProperty("zonesHarvested")]
        public bool ZonesHarvested { get; set; }

        [JsonProperty("requirements")]
        public List<RaidCheckRequirementDto> Requirements { get; set; } = new List<RaidCheckRequirementDto>();
    }

    /// <summary>One carry condition, on the map it actually happens on. Per condition, not per
    /// quest: "Is This a Reference" wants 27 cameras over eight maps, and per-quest totals would
    /// demand all 27 on each.</summary>
    internal sealed class RaidCheckRequirementDto
    {
        [JsonProperty("questId")]
        public string QuestId { get; set; }

        [JsonProperty("questName")]
        public string QuestName { get; set; }

        /// <summary>Every template the condition accepts. Held counts sum across all of them.</summary>
        [JsonProperty("templates")]
        public List<string> Templates { get; set; } = new List<string>();

        /// <summary>The canonical name, from the first template - the fallback for the red case,
        /// where nothing is held and there is no carried item to name the row after.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("needed")]
        public int Needed { get; set; }

        /// <summary>Whether found-in-raid copies are demanded. No vanilla carry condition sets it,
        /// so this branch ships unexercised - written down so a quest mod that does set it gets the
        /// right answer rather than a confident wrong one.</summary>
        [JsonProperty("foundInRaid")]
        public bool FoundInRaid { get; set; }

        /// <summary>The quest's state, as data rather than a verdict. A never-touched quest reads
        /// AvailableForStart rather than Locked, which is what makes "count quests you have not
        /// accepted" mean anything.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }
    }
}
