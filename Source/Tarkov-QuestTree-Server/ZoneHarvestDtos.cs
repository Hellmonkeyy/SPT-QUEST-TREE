using System.Collections.Generic;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace QuestTreeServer
{
    /// <summary>
    /// The body of POST /questtree/zones: everything the client found in a loaded raid that a
    /// quest objective can point at - every TriggerWithId (the zones behind "visit", "place",
    /// "leave item", "launch flare") and every quest item lying in the world - with positions.
    ///
    /// Named with explicit JsonPropertyName because SPT's JsonUtil deserializes request bodies
    /// with no naming policy: without these, a camelCase body would land in no property at all,
    /// silently. The client mirrors this shape in QuestGraph/ZoneHarvestDto.cs.
    /// </summary>
    public sealed class ZoneHarvestRequest : IRequestData
    {
        /// <summary>The map's internal name ("bigmap"), as GameWorld reports it.</summary>
        [JsonPropertyName("map")]
        public string Map { get; set; } = "";

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "";

        [JsonPropertyName("triggers")]
        public List<HarvestedTrigger> Triggers { get; set; } = new();

        [JsonPropertyName("questItems")]
        public List<HarvestedQuestItem> QuestItems { get; set; } = new();
    }

    public sealed class HarvestedTrigger
    {
        /// <summary>The zone id - the same string a quest condition's zoneId names.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>The trigger's class name: QuestTrigger, PlaceItemTrigger, ExperienceTrigger,
        /// or whatever a mod derived. Informational.</summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }

        /// <summary>Whether the object was active in the scene when read. Inactive triggers are
        /// kept - a zone a mod enables later is still a place - but flagged.</summary>
        [JsonPropertyName("active")]
        public bool Active { get; set; }

        /// <summary>Half-extents of the trigger's collider, when it has one. Stored for a later
        /// "draw the area" feature; nothing reads them yet.</summary>
        [JsonPropertyName("ex")] public float ExtentX { get; set; }
        [JsonPropertyName("ey")] public float ExtentY { get; set; }
        [JsonPropertyName("ez")] public float ExtentZ { get; set; }
    }

    public sealed class HarvestedQuestItem
    {
        [JsonPropertyName("templateId")]
        public string TemplateId { get; set; } = "";

        [JsonPropertyName("itemId")]
        public string ItemId { get; set; } = "";

        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
    }

    /// <summary>What ZoneStore keeps on disk per map: the harvest, plus when and by which client
    /// version it was taken, so a stale file can be recognised after a game update.</summary>
    public sealed class ZoneFile
    {
        [JsonPropertyName("map")]
        public string Map { get; set; } = "";

        /// <summary>UTC, "u" format.</summary>
        [JsonPropertyName("harvestedAt")]
        public string HarvestedAt { get; set; } = "";

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "";

        [JsonPropertyName("triggers")]
        public List<HarvestedTrigger> Triggers { get; set; } = new();

        [JsonPropertyName("questItems")]
        public List<HarvestedQuestItem> QuestItems { get; set; } = new();
    }

    /// <summary>What POST /questtree/zones answers.</summary>
    public sealed class ZoneHarvestResponse
    {
        public bool Ok { get; set; }
        public int Zones { get; set; }
        public int QuestItems { get; set; }
        public string Message { get; set; } = "";
    }
}
