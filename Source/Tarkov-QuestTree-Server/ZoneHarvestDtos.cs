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
        /// <summary>The newest harvest shape this server understands. The client mirror
        /// (QuestGraph/ZoneHarvestDto.cs) declares the same number as its own CurrentSchemaVersion:
        /// change either and change the other in the same commit. Named like the client's other
        /// supported-version constants because it does their job here - this is the one payload the
        /// SERVER receives, so the version check the client runs on every other payload has to live
        /// on this side.
        ///
        /// 2 (1.19.0): Extent - the map's world rectangle and floor bands, measured in the raid the
        /// harvest came from. A v1 client sends no extent and is still accepted, because the field is
        /// additive: an absent extent deserialises to null, which every reader below treats as "this
        /// map has no measured rectangle yet" rather than as a rectangle of zeroes.</summary>
        public const int SupportedSchemaVersion = 2;

        /// <summary>The shape the client believes it is sending. 0 from a client older than
        /// 1.8.1, which sent no version at all - the one payload in the system that did not.
        ///
        /// READ by QuestTreeRouter.AcceptHarvest, which refuses a harvest claiming a shape newer
        /// than SupportedSchemaVersion and accepts older or equal. Older is safe because every
        /// version so far has been the same shape and an absent field arrives as 0; newer is not,
        /// because this is the one route that mutates shipped, shared data, and fields this build
        /// cannot see would be dropped by the deserializer and then written into zones\ as though
        /// the harvest had been understood in full.</summary>
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        /// <summary>The map's internal name ("bigmap"), as GameWorld reports it.</summary>
        [JsonPropertyName("map")]
        public string Map { get; set; } = "";

        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "";

        [JsonPropertyName("triggers")]
        public List<HarvestedTrigger> Triggers { get; set; } = new();

        [JsonPropertyName("questItems")]
        public List<HarvestedQuestItem> QuestItems { get; set; } = new();

        /// <summary>The map's world rectangle and floor bands as this raid measured them, or null
        /// from a v1 client - and from a v2 client whose own containment check failed, which sends
        /// the triggers without an extent rather than a rectangle it does not believe.
        ///
        /// Validated and possibly dropped by ZoneStore.Sanitise; merged by ZoneStore.Save, where a
        /// null never erases a rectangle already on disk.</summary>
        [JsonPropertyName("extent")]
        public MapExtentDto? Extent { get; set; }
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
        /// <summary>The newest zone-file shape this server can read, and the one it writes.
        ///
        /// 2 (1.19.0): Extent. A v1 file on disk - every shipped seed among them - stays valid and is
        /// read unchanged; it simply has no extent, and the first v2 harvest of that map adds one.
        /// Raised in step with ZoneHarvestRequest.SupportedSchemaVersion because the two numbers
        /// describe the same new field arriving by the same route.</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>The shape of this file. ABSENT from files written before 1.8.2, the shipped seeds
        /// among them; those are the same shape as version 1 and deserialise to this initializer, not
        /// to 0, which is why the reader below needs no special case for them.
        ///
        /// READ by ZoneStore.Read, which SKIPS a file stamped higher than CurrentSchemaVersion with
        /// one warning naming it - so a file left behind by a newer server is recognised as being
        /// from the future rather than parsed as though its unknown fields did not matter, and is not
        /// reported as corrupt either. Equal or lower is read exactly as before.</summary>
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

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

        /// <summary>The best rectangle any harvest of this map has produced, or null on a v1 file and
        /// on a map nobody has raided with a v2 client. Not simply the newest: ZoneStore.Save keeps
        /// the better-ranked source - NavMesh first, then terrain, then BorderZone, the client's own
        /// order - so one raid that measured the NavMesh is not undone by a later one that could only
        /// read the terrain. The ranking was BorderZone-first until 2026-09-23, which on Interchange kept
        /// an older 1073x1033 m terrain rectangle over the 965x925 m NavMesh one the pictures are drawn
        /// over; see ZoneStore.ExtentSources.</summary>
        [JsonPropertyName("extent")]
        public MapExtentDto? Extent { get; set; }
    }

    /// <summary>
    /// The rectangle of world a map occupies, in game metres on the x and z axes, plus the height
    /// bands its floors sit in. Measured in a loaded raid because it cannot be measured anywhere
    /// else: the server's location table holds the loot and the quests, not the scene's geometry.
    ///
    /// What it is FOR. A picture of a map is only a picture until something says which stretch of
    /// world it covers. The client draws a pin at the objective's raw game (x, z) and stretches the
    /// picture over this rectangle, so these four numbers are what make the two agree. They are also
    /// what lets a map with no picture at all still draw its pins on a plain backdrop.
    ///
    /// Rotation is ALWAYS 0 here, and is stored rather than assumed. The in-raid capture points its
    /// camera straight down with the image's right at +x and its up at +z, so picture and world share
    /// an orientation and no transform is needed. The field exists because the one thing a later
    /// release might change is that camera, and a stored 0 makes a future non-zero value readable as
    /// a deliberate difference instead of a silent one. This build stores 0 whatever arrived: a
    /// client that invented a rotation would otherwise move every pin on the map.
    ///
    /// Floors are HEIGHT BANDS, not storeys. The only thing the client can ask about a pin is its
    /// world Y, so a floor has to be an interval of Y to be of any use - "which building is this in"
    /// is not a question a coordinate can answer. The bands come from where NavMesh vertex heights
    /// cluster, which is why Interchange's car park separates from its shop floor while two shops on
    /// one level do not.
    ///
    /// Declared here, beside the harvest that produces it, and REUSED by MapMarkerSetDto rather than
    /// copied: the rectangle going out is the rectangle that came in, and two declarations would
    /// drift the moment one gained a field. The explicit JsonPropertyName names are also the names
    /// WireJson's camelCase policy produces, so this class serialises identically down either path -
    /// see the note at the top of this file for why the harvest side cannot rely on a policy.
    /// </summary>
    public sealed class MapExtentDto
    {
        [JsonPropertyName("minX")] public double MinX { get; set; }
        [JsonPropertyName("minZ")] public double MinZ { get; set; }
        [JsonPropertyName("maxX")] public double MaxX { get; set; }
        [JsonPropertyName("maxZ")] public double MaxZ { get; set; }

        /// <summary>Where the rectangle came from: "borderzone" (the scene's own play-area
        /// colliders), "terrain" (the union of Unity terrains) or "navmesh" (the walkable
        /// triangulation's bounding box), best first. Kept because it RANKS one harvest against
        /// another - ZoneStore.Save prefers a better-ranked rectangle to a newer one - and because it
        /// is the first thing to look at when a map draws too large: a navmesh extent on an indoor
        /// map reaches wherever a bot could walk.</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = "";

        /// <summary>Degrees the picture is rotated relative to the world. See the class summary:
        /// always 0 in this release, stored so that a later one can differ out loud.</summary>
        [JsonPropertyName("rotation")] public float Rotation { get; set; }

        /// <summary>When the raid this was measured in ran, ISO-8601 UTC. The tie-break when two
        /// harvests name the same source, and the only way to tell a rectangle measured before a game
        /// update from one measured after it.</summary>
        [JsonPropertyName("sampledAt")] public string SampledAt { get; set; } = "";

        [JsonPropertyName("floors")] public List<MapFloorDto> Floors { get; set; } = new();
    }

    /// <summary>One height band of a map. Level 0 is the ground the spawns are on, positive levels
    /// are above it and negative ones below, so a pin's floor is decided by comparing its world Y
    /// against MinY..MaxY - the only floor test a coordinate supports.</summary>
    public sealed class MapFloorDto
    {
        [JsonPropertyName("level")] public int Level { get; set; }

        /// <summary>What to show for this band: "Ground", "Floor 2", "Basement". A label, not an
        /// identifier - nothing joins on it.</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        [JsonPropertyName("minY")] public float MinY { get; set; }
        [JsonPropertyName("maxY")] public float MaxY { get; set; }
    }

    /// <summary>What POST /questtree/zones answers. Named explicitly like the request, for the
    /// reason at the top of the file; it only ever escaped that rule by being serialized with
    /// the camelCase options.</summary>
    public sealed class ZoneHarvestResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("zones")]
        public int Zones { get; set; }

        [JsonPropertyName("questItems")]
        public int QuestItems { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

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
        [JsonPropertyName("rebuildInSeconds")]
        public int RebuildInSeconds { get; set; }
    }
}
