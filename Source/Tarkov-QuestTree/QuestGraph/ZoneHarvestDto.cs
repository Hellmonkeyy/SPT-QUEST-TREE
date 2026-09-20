using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>Mirror of the server's ZoneHarvestRequest (Source/Tarkov-QuestTree-Server/
    /// ZoneHarvestDtos.cs) - the body ZoneHarvester posts to /questtree/zones. Hand-copied for
    /// the same reason every other DTO here is: the two halves cannot share a project.</summary>
    internal sealed class ZoneHarvestRequest
    {
        /// <summary>The shape being sent. Bumped with the fields below.
        ///
        /// 2 (1.19.0) added <see cref="Extent"/>. The server's ZoneHarvestRequest.SupportedSchemaVersion
        /// carries the same number and its AcceptHarvest refuses anything higher, so a 1.19.0 client
        /// against an older server half has its harvests refused outright rather than half-read - the
        /// reason that constant lives on the receiving side.</summary>
        public const int CurrentSchemaVersion = 2;

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

        /// <summary>What the map is, geometrically: the rectangle the world occupies and the height
        /// bands the Maps tab can draw as floors. NULL on the first pass (the scene is three seconds
        /// old and half of it may not be loaded), on a harvest where MapExtentProbe's own check found
        /// the rectangle did not contain the zones it was measured against, and on any failure - the
        /// zones are the harvest's job and they are still sent.
        ///
        /// Nullable on purpose: the server tells "not measured" from "measured as nothing" by the
        /// field being absent, and keeps whatever it already holds in that case.</summary>
        [JsonProperty("extent")]
        public MapExtentDto Extent { get; set; }
    }

    /// <summary>Mirror of the server's MapExtentDto: one map's world rectangle and floors, as
    /// measured in a loaded raid by <see cref="MapExtentProbe"/>.
    ///
    /// Travels in both directions - up on <see cref="ZoneHarvestRequest"/>, back down on
    /// <see cref="MapMarkerSetDto"/> (QuestDto.cs) - which is why it is one type and not two.
    ///
    /// The rectangle is in raw game coordinates, X and Z, the same space the harvested trigger
    /// positions and MapMarkerDto.X/Z are in, so the Maps tab can use it as a layer's bounds
    /// directly. Y never appears here: height belongs to the floors.</summary>
    internal sealed class MapExtentDto
    {
        [JsonProperty("minX")] public double MinX { get; set; }
        [JsonProperty("minZ")] public double MinZ { get; set; }
        [JsonProperty("maxX")] public double MaxX { get; set; }
        [JsonProperty("maxZ")] public double MaxZ { get; set; }

        /// <summary>Where the rectangle came from, best first: "borderzone" (the invisible walls
        /// that stop a player leaving the map - the playable area as the game itself defines it),
        /// "terrain" (the heightmaps' union) or "navmesh" (the AI walkable area's bounding box,
        /// which underestimates roofs and water). The server ranks a stored extent against an
        /// incoming one by this string, so it is one of exactly those three words - lowercase, which
        /// is the casing the server normalises to and hands back on the marker set.</summary>
        [JsonProperty("source")]
        public string Source { get; set; }

        /// <summary>Degrees the map picture is rotated relative to world XZ. Always 0 from the
        /// probe - it measures an axis-aligned box - and present so a rotated capture or a
        /// hand-corrected extent needs no schema bump.</summary>
        [JsonProperty("rotation")]
        public float Rotation { get; set; }

        /// <summary>When the raid this was measured in ran, UTC, ISO-8601. The server prefers a
        /// newer extent of the same rank, so a map re-measured after a game update wins.</summary>
        [JsonProperty("sampledAt")]
        public string SampledAt { get; set; }

        /// <summary>The height bands, ground first in level order, at most eight. Never null and
        /// never empty for a valid extent: a map with no readable bands still gets one "Ground"
        /// floor spanning everything, so the Maps tab always has exactly one layer to draw on.</summary>
        [JsonProperty("floors")]
        public List<MapFloorDto> Floors { get; set; } = new List<MapFloorDto>();
    }

    /// <summary>Mirror of the server's MapFloorDto: one height band of one map.</summary>
    internal sealed class MapFloorDto
    {
        /// <summary>0 is the ground floor, positive is up, negative is down. What an objective's
        /// floor vocabulary ("First_Floor", "Basement") is mapped onto.</summary>
        [JsonProperty("level")]
        public int Level { get; set; }

        /// <summary>"Ground", "Floor 2", "Floor 3", "Basement", "Basement 2" - the layer name the
        /// Maps tab shows, and what MapMarkerDto.Floor is matched against.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>World Y the band covers, already carrying the half-metre margin the probe adds,
        /// so a pin standing just above a floor still lands on it. Bands do not overlap.</summary>
        [JsonProperty("minY")] public float MinY { get; set; }
        [JsonProperty("maxY")] public float MaxY { get; set; }
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
