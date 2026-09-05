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
}
