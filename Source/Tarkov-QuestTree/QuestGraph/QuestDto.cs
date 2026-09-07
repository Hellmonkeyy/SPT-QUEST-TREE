using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Client-side mirror of the payload served by the QuestTreeServer companion mod on
    /// /questtree/quests.
    ///
    /// This duplicates Source/Tarkov-QuestTree-Server/QuestTreeDtos.cs on purpose - the two halves
    /// target different frameworks (netstandard2.1 here for Unity, net10.0 for the server), so they
    /// cannot share a source file. Every type below, KappaPayloadDto included, is a hand-mirror of
    /// one in that file: change either and change the other in the same commit. SchemaVersion
    /// exists to make a mismatch that slips through say so out loud instead of silently parsing to
    /// nulls.
    ///
    /// The server sends camelCase, which Newtonsoft matches case-insensitively by default, so no
    /// per-property attributes are needed beyond the naming below.
    /// </summary>
    internal sealed class QuestPayloadDto
    {
        /// <summary>Schema this client understands. Compared against the server's on fetch.</summary>
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("quests")]
        public List<QuestDto> Quests { get; set; }
    }

    internal sealed class QuestDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Already localized by the server - never a raw locale key.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("traderId")]
        public string TraderId { get; set; }

        /// <summary>"Pmc", "Bear", "Usec". Used only to mark a quest this faction can never take.</summary>
        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Player level required, or 0 for none.</summary>
        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("locationId")]
        public string LocationId { get; set; }

        /// <summary>The map's raw internal id ("bigmap"), for matching against other tools' map
        /// data - a localized display name cannot be matched against those.</summary>
        [JsonProperty("locationKey")]
        public string LocationKey { get; set; }

        [JsonProperty("isEvent")]
        public bool IsEvent { get; set; }

        [JsonProperty("editionRestricted")]
        public bool EditionRestricted { get; set; }

        [JsonProperty("prerequisites")]
        public List<PrerequisiteDto> Prerequisites { get; set; }

        [JsonProperty("objectives")]
        public List<ObjectiveDto> Objectives { get; set; }

        [JsonProperty("rewards")]
        public List<RewardDto> Rewards { get; set; }
    }

    internal sealed class PrerequisiteDto
    {
        [JsonProperty("target")]
        public string Target { get; set; }

        [JsonProperty("status")]
        public List<string> Status { get; set; }

        [JsonProperty("availableAfter")]
        public int AvailableAfter { get; set; }
    }

    internal sealed class ObjectiveDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("isNecessary")]
        public bool IsNecessary { get; set; }

        /// <summary>Raw condition type (HandoverItem, FindItem, CounterCreator, ...).</summary>
        [JsonProperty("conditionType")]
        public string ConditionType { get; set; }

        /// <summary>Item template ids, populated only for item-shaped conditions. This is what the
        /// Collector hand-in checklist is built from.</summary>
        [JsonProperty("targetItems")]
        public List<string> TargetItems { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>The zone ids this objective happens in; empty when it has no place. Resolved
        /// against harvested zones server-side - here it is only for saying which zone.</summary>
        [JsonProperty("zoneIds")]
        public List<string> ZoneIds { get; set; }
    }

    internal sealed class RewardDto
    {
        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("traderId")]
        public string TraderId { get; set; }
    }

    /// <summary>Mirror of the server's KappaPayloadDto - the Collector hand-in checklist paired
    /// with what this profile holds. Re-fetched every time the Kappa tab is opened, because the
    /// stash changes as you play.</summary>
    internal sealed class KappaPayloadDto
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        /// <summary>Version of the server half, so a mismatch message can name both sides. Empty
        /// from server halves older than this field.</summary>
        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        [JsonProperty("collectorFound")]
        public bool CollectorFound { get; set; }

        [JsonProperty("collectorQuestId")]
        public string CollectorQuestId { get; set; }

        [JsonProperty("collectorStatus")]
        public string CollectorStatus { get; set; }

        [JsonProperty("items")]
        public List<KappaItemDto> Items { get; set; }

        /// <summary>The Kappa quest list - every quest Collector requires, from Collector's own
        /// start conditions in SPT's shipped database.</summary>
        [JsonProperty("kappaQuestIds")]
        public List<string> KappaQuestIds { get; set; }

        /// <summary>"database" (the shipped quests.json) or "live" (the in-memory table, used only
        /// when the file could not be read).</summary>
        [JsonProperty("kappaSource")]
        public string KappaSource { get; set; }

        /// <summary>How many prerequisites the live Collector actually has. When this differs from
        /// KappaQuestIds a mod has changed the requirement on this install, which is worth saying.</summary>
        [JsonProperty("liveCollectorPrerequisiteCount")]
        public int LiveCollectorPrerequisiteCount { get; set; }
    }

    internal sealed class KappaItemDto
    {
        [JsonProperty("conditionId")]
        public string ConditionId { get; set; }

        [JsonProperty("template")]
        public string Template { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("required")]
        public int Required { get; set; }

        /// <summary>Found-in-raid copies held. Collector accepts only these.</summary>
        [JsonProperty("ownedFoundInRaid")]
        public int OwnedFoundInRaid { get; set; }

        /// <summary>Total copies held, FiR or not - lets the UI distinguish "you have one but it is
        /// not found-in-raid" from "you do not have one".</summary>
        [JsonProperty("ownedTotal")]
        public int OwnedTotal { get; set; }

        [JsonProperty("handedIn")]
        public bool HandedIn { get; set; }

        /// <summary>True when this item is done as far as Collector is concerned.</summary>
        public bool IsSatisfied => HandedIn || OwnedFoundInRaid >= Required;
    }

    /// <summary>Mirror of the server's ProfilePayloadDto - what this player is, rather than what the
    /// quest database says. Re-fetched whenever it can have moved (panel open, quest status change),
    /// because level, loyalty and objective counters all change as you play.</summary>
    internal sealed class ProfilePayloadDto
    {
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        /// <summary>False when the server had no profile to read (an out-of-game request).</summary>
        [JsonProperty("hasProfile")]
        public bool HasProfile { get; set; }

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("side")]
        public string Side { get; set; }

        [JsonProperty("gameVersion")]
        public string GameVersion { get; set; }

        [JsonProperty("traders")]
        public List<TraderStateDto> Traders { get; set; }

        /// <summary>Condition id -> current count, keyed to match ObjectiveDto.Id.</summary>
        [JsonProperty("conditionProgress")]
        public Dictionary<string, double> ConditionProgress { get; set; }

        /// <summary>Quest id -> the single gate blocking it. Absent means not blocked.</summary>
        [JsonProperty("lockReasons")]
        public Dictionary<string, LockReasonDto> LockReasons { get; set; }

        /// <summary>Item template -> how many the profile holds, for items some quest asks for.
        /// Absent means none held.</summary>
        [JsonProperty("itemsOwned")]
        public Dictionary<string, HeldItemDto> ItemsOwned { get; set; }
    }

    /// <summary>How many of an item the profile holds. Found-in-raid is tracked separately because
    /// most quest hand-ins only accept found-in-raid copies.</summary>
    internal sealed class HeldItemDto
    {
        [JsonProperty("foundInRaid")]
        public int FoundInRaid { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }
    }

    internal sealed class TraderStateDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("loyaltyLevel")]
        public int LoyaltyLevel { get; set; }

        [JsonProperty("standing")]
        public double Standing { get; set; }

        [JsonProperty("unlocked")]
        public bool Unlocked { get; set; }
    }

    internal sealed class LockReasonDto
    {
        /// <summary>OtherFaction | Edition | Event | Level | Loyalty | Standing | Prerequisite</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; }

        [JsonProperty("requiredValue")]
        public int RequiredValue { get; set; }

        [JsonProperty("currentValue")]
        public int CurrentValue { get; set; }

        /// <summary>Set for trader-scoped gates; the client names the trader from its own session
        /// data rather than the server guessing at a display name.</summary>
        [JsonProperty("traderId")]
        public string TraderId { get; set; }

        /// <summary>Set for Prerequisite - the client can name these from its own graph.</summary>
        [JsonProperty("blockingQuestIds")]
        public List<string> BlockingQuestIds { get; set; }
    }

    /// <summary>Mirror of the server's MapMarkerPayloadDto. Hand-copied rather than shared: the two
    /// halves target different frameworks (netstandard2.1 and net10.0), so there is no project both
    /// can reference.</summary>
    internal sealed class MapMarkerPayloadDto
    {
        public const int SupportedSchemaVersion = 3;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("maps")]
        public List<MapMarkerSetDto> Maps { get; set; }
    }

    internal sealed class MapMarkerSetDto
    {
        /// <summary>The map's internal name ("bigmap"), matching QuestDto.LocationKey and the
        /// names DynamicMaps keys its own maps on.</summary>
        [JsonProperty("locationKey")]
        public string LocationKey { get; set; }

        [JsonProperty("markers")]
        public List<MapMarkerDto> Markers { get; set; }

        /// <summary>Distinct zone ids this map's quests reference, and how many a harvest has
        /// placed. What the "raid this map once" line is built from.</summary>
        [JsonProperty("zonesWanted")]
        public int ZonesWanted { get; set; }

        [JsonProperty("zonesKnown")]
        public int ZonesKnown { get; set; }

        /// <summary>When this map was last harvested (UTC), or empty if never.</summary>
        [JsonProperty("harvestedAt")]
        public string HarvestedAt { get; set; }
    }

    internal sealed class MapMarkerDto
    {
        [JsonProperty("itemName")]
        public string ItemName { get; set; }

        /// <summary>The quests that want this item, by display name.</summary>
        [JsonProperty("quests")]
        public List<string> Quests { get; set; }

        /// <summary>The same quests by id, for looking their live status up in the graph.</summary>
        [JsonProperty("questIds")]
        public List<string> QuestIds { get; set; }

        /// <summary>"item" for a place the thing you need spawns, "objective" for a place the quest
        /// itself happens.</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        /// <summary>How many separate places this item can spawn on this map.</summary>
        [JsonProperty("alternatives")]
        public int Alternatives { get; set; }

        /// <summary>Item markers only: the item's template id, to join the pin to the stash.</summary>
        [JsonProperty("template")]
        public string Template { get; set; }

        /// <summary>Objective markers only: position as a percentage across and down the map image,
        /// which the view turns into map coordinates using the layer's own bounds.</summary>
        [JsonProperty("leftPercent")]
        public float LeftPercent { get; set; }

        [JsonProperty("topPercent")]
        public float TopPercent { get; set; }

        /// <summary>The floor named by the source, matched against the map's layer names.</summary>
        [JsonProperty("floor")]
        public string Floor { get; set; }

        /// <summary>World coordinates. Y is the height, which is what decides the floor: a map's
        /// layers each declare the height band they cover.</summary>
        [JsonProperty("x")]
        public float X { get; set; }

        [JsonProperty("y")]
        public float Y { get; set; }

        [JsonProperty("z")]
        public float Z { get; set; }
    }
}
