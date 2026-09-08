using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>Mirror of the server's ZoneHarvestRequest (Source/Tarkov-QuestTree-Server/
    /// ZoneHarvestDtos.cs) - the body ZoneHarvester posts to /questtree/zones. Hand-copied for
    /// the same reason every other DTO here is: the two halves cannot share a project.</summary>
    internal sealed class ZoneHarvestRequest
    {
        /// <summary>The shape being sent. Bumped with the fields below.</summary>
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("map")]
        public string Map { get; set; }

        [JsonProperty("clientVersion")]
        public string ClientVersion { get; set; }

        [JsonProperty("triggers")]
        public List<HarvestedTrigger> Triggers { get; set; } = new List<HarvestedTrigger>();

        [JsonProperty("questItems")]
        public List<HarvestedQuestItem> QuestItems { get; set; } = new List<HarvestedQuestItem>();
    }

    internal sealed class HarvestedTrigger
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
        [JsonProperty("z")] public float Z { get; set; }
        [JsonProperty("active")] public bool Active { get; set; }
        [JsonProperty("ex")] public float ExtentX { get; set; }
        [JsonProperty("ey")] public float ExtentY { get; set; }
        [JsonProperty("ez")] public float ExtentZ { get; set; }
    }

    internal sealed class HarvestedQuestItem
    {
        [JsonProperty("templateId")] public string TemplateId { get; set; }
        [JsonProperty("itemId")] public string ItemId { get; set; }
        [JsonProperty("x")] public float X { get; set; }
        [JsonProperty("y")] public float Y { get; set; }
        [JsonProperty("z")] public float Z { get; set; }
    }

    /// <summary>Mirror of the server's ZoneHarvestResponse. Until 1.8.1 the client logged the
    /// raw reply and treated any answer as a success, so a refused harvest looked accepted.</summary>
    internal sealed class ZoneHarvestResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("zones")] public int Zones { get; set; }
        [JsonProperty("questItems")] public int QuestItems { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
