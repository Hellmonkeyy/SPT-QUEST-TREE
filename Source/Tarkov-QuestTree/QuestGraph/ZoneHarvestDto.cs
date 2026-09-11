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

        /// <summary>Seconds to wait before asking for the rebuilt payloads, or 0 to ask at once.
        ///
        /// Non-zero when this harvest was buffered because its map is inside its write window. The
        /// client must honour it: invalidating immediately would refetch the PRE-harvest answer,
        /// and since the quest list latches for the whole session it would then keep serving that
        /// answer until the game restarts - which is the bug the derived locations exist to fix,
        /// recreated by the throttle meant to protect the host.
        ///
        /// An older client reads an absent field as 0 and invalidates at once, which is exactly
        /// today's behaviour, so the halves stay compatible.</summary>
        [JsonProperty("rebuildInSeconds")] public int RebuildInSeconds { get; set; }
    }
}
