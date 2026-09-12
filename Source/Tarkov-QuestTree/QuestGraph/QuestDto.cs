using System;
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
        /// <summary>Schema this client understands. Compared against the server's on fetch. A
        /// mismatch is a logged warning, not a refusal: v1 (1.7.1) payloads only lack
        /// ObjectiveDto.FoundInRaid, which the readers fall back from. v4 (1.9.0) adds
        /// QuestDto.DerivedLocations, which a v3 server simply never sends. v5 (1.10.1) adds
        /// RewardDto.ShortName and Template; without them a reward row keeps its full name
        /// and stops being clickable, which is exactly how it read before they existed.</summary>
        public const int SupportedSchemaVersion = 9;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        /// <summary>The server half's version; empty from a server older than 1.8.1.</summary>
        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        [JsonProperty("quests")]
        public List<QuestDto> Quests { get; set; }
    }

    /// <summary>A map a quest was placed on by its objectives rather than by its own Location field
    /// (schema v4). Empty for a quest that names a real map, and for one with no harvested zones.</summary>
    internal sealed class DerivedLocationDto
    {
        /// <summary>Internal name, in the same keyspace as QuestDto.LocationKey.</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>Display name, matching QuestDto.LocationId.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }
    }


    /// <summary>The requirement a weapon-build quest states, in the terms the game compares
    /// against. Null for a quest that states none.</summary>
    internal sealed class WeaponBuildDto
    {
        [JsonProperty("weaponTemplate")]
        public string WeaponTemplate { get; set; }

        [JsonProperty("weaponName")]
        public string WeaponName { get; set; }

        [JsonProperty("thresholds")]
        public List<WeaponBuildThresholdDto> Thresholds { get; set; } = new List<WeaponBuildThresholdDto>();

        [JsonProperty("requiredItemNames")]
        public List<string> RequiredItemNames { get; set; } = new List<string>();

        [JsonProperty("requiredCategoryNames")]
        public List<string> RequiredCategoryNames { get; set; } = new List<string>();

        /// <summary>The same requirements as template ids, parallel to the name lists. The names are
        /// for reading; the ids are what a row needs to open the inspect window.</summary>
        [JsonProperty("requiredItemIds")]
        public List<string> RequiredItemIds { get; set; } = new List<string>();

        [JsonProperty("requiredCategoryIds")]
        public List<string> RequiredCategoryIds { get; set; } = new List<string>();

        /// <summary>A slot the build must leave empty.</summary>
        [JsonProperty("emptyTacticalSlots")]
        public double? EmptyTacticalSlots { get; set; }

        /// <summary>Fields the condition named with a value of zero, so not in Thresholds.</summary>
        [JsonProperty("zeroThresholdFields")]
        public List<string> ZeroThresholdFields { get; set; } = new List<string>();

        /// <summary>What the server's stat model says the quest's own example parts score.</summary>
        [JsonProperty("modelCheck")]
        public WeaponModelCheckDto ModelCheck { get; set; }

        /// <summary>A build that satisfies this quest, worked out on the server. Null when none was
        /// found, which is not the same as none existing - see HitBudget.</summary>
        [JsonProperty("solution")]
        public SolvedBuildDto Solution { get; set; }
    }

    /// <summary>A worked-out build: the parts, what it scores, and what about it is unverified.</summary>
    internal sealed class SolvedBuildDto
    {
        /// <summary>Every threshold the model can score is met, every required slot is filled, and
        /// every named part and category is present.
        ///
        /// NOT "this will be accepted". Height and width are real constraints in five vanilla quests
        /// and the model scores neither, so a build can be complete by every measure available and
        /// still be refused for its assembled size.</summary>
        [JsonProperty("satisfies")]
        public bool Satisfies { get; set; }

        /// <summary>False when the quest constrains something nothing on the server can score -
        /// height or width, in five of the vanilla quests.
        ///
        /// Defaults TRUE so a payload from an older server, which does not send this, reads as fully
        /// checked rather than flagging every build as doubtful. The server has always sent Unchecked
        /// alongside, so the honest case degrades to the wordier one rather than to a false alarm.</summary>
        [JsonProperty("fullyChecked")]
        public bool FullyChecked { get; set; } = true;

        [JsonProperty("hitBudget")]
        public bool HitBudget { get; set; }

        [JsonProperty("parts")]
        public List<SolvedPartDto> Parts { get; set; } = new List<SolvedPartDto>();

        [JsonProperty("scores")]
        public List<string> Scores { get; set; } = new List<string>();

        [JsonProperty("unmet")]
        public List<string> Unmet { get; set; } = new List<string>();

        [JsonProperty("unchecked")]
        public List<string> Unchecked { get; set; } = new List<string>();
    }

    internal sealed class SolvedPartDto
    {
        [JsonProperty("slot")]
        public string Slot { get; set; }

        [JsonProperty("template")]
        public string Template { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    /// <summary>What the stat model believes about the exact parts a quest names.
    ///
    /// Shown so it can be read against the game's own inspect screen. The model has to be proven
    /// right before anything generates builds from it - a solver on a wrong model produces builds
    /// that look right, pass our own check, and fail at the hand-in.</summary>
    internal sealed class WeaponModelCheckDto
    {
        [JsonProperty("ergonomics")]
        public double Ergonomics { get; set; }

        [JsonProperty("recoil")]
        public double Recoil { get; set; }

        [JsonProperty("weight")]
        public double Weight { get; set; }

        [JsonProperty("magazineCapacity")]
        public int? MagazineCapacity { get; set; }

        [JsonProperty("effectiveDistance")]
        public double? EffectiveDistance { get; set; }

        [JsonProperty("partsScored")]
        public int PartsScored { get; set; }

        [JsonProperty("partsNamed")]
        public int PartsNamed { get; set; }

        [JsonProperty("clamped")]
        public List<string> Clamped { get; set; } = new List<string>();

        /// <summary>Required slots the named parts leave empty. When this is above zero the scores
        /// are a floor, not a prediction - the missing parts carry stats of their own.</summary>
        [JsonProperty("unfilledRequiredSlots")]
        public int UnfilledRequiredSlots { get; set; }

        [JsonProperty("requiredSlots")]
        public int RequiredSlots { get; set; }
    }

    internal sealed class WeaponBuildThresholdDto
    {
        [JsonProperty("field")]
        public string Field { get; set; }

        [JsonProperty("compare")]
        public string Compare { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }
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

        /// <summary>The maps this quest was placed on by its objectives, when its own
        /// declaration said nothing useful. Never null - QuestGraphBuilder synthesises DTOs
        /// when the server half is absent, and a null list there would be a crash rather than
        /// an empty map list.</summary>
        [JsonProperty("derivedLocations")]
        public List<DerivedLocationDto> DerivedLocations { get; set; } = new List<DerivedLocationDto>();

        [JsonProperty("isEvent")]
        public bool IsEvent { get; set; }

        [JsonProperty("editionRestricted")]
        public bool EditionRestricted { get; set; }

        [JsonProperty("prerequisites")]
        public List<PrerequisiteDto> Prerequisites { get; set; }

        /// <summary>Every weapon-build requirement this quest states. Schema v4; a LIST since v7,
        /// because a quest can ask for several.</summary>
        [JsonProperty("weaponBuilds")]
        public List<WeaponBuildDto> WeaponBuilds { get; set; } = new List<WeaponBuildDto>();

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

        /// <summary>Item template ids, populated only for item-shaped conditions - handing an item
        /// over, finding one in raid, or carrying one in to leave somewhere. This is what the
        /// Collector hand-in checklist and the "items to bring" list are built from.</summary>
        [JsonProperty("targetItems")]
        public List<string> TargetItems { get; set; }

        /// <summary>Display names for TargetItems, index for index (schema v3). Null or short from
        /// an older server, in which case the readers fall back to guessing a name out of the
        /// objective sentence - see QuestSummary.ItemName.</summary>
        [JsonProperty("targetItemNames")]
        public List<string> TargetItemNames { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>The condition's own found-in-raid flag (schema v2). False from a 1.7.1
        /// server, where the readers fall back to the objective sentence.</summary>
        [JsonProperty("foundInRaid")]
        public bool FoundInRaid { get; set; }

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

        /// <summary>The item's short name where the locale has one - "AFAK" rather than "AFAK
        /// tactical individual first aid kit". Empty when there is none.</summary>
        [JsonProperty("shortName")]
        public string ShortName { get; set; }

        /// <summary>The item template, so a reward row can open the game's inspect window.</summary>
        [JsonProperty("template")]
        public string Template { get; set; }

        [JsonProperty("traderId")]
        public string TraderId { get; set; }

        /// <summary>The name to print in a LIST, where every row competes for the same width.
        /// Prefers the short name; falls back to the full one when there is no short name.</summary>
        public string ListName => string.IsNullOrEmpty(ShortName) ? Name : ShortName;
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
        /// <summary>2 (1.9.0): the per-location counts on HeldItemDto, and InventoryLocationsKnown.</summary>
        public const int SupportedSchemaVersion = 2;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("modVersion")]
        public string ModVersion { get; set; }

        /// <summary>False when the server had no profile to read (an out-of-game request).</summary>
        [JsonProperty("hasProfile")]
        public bool HasProfile { get; set; }

        /// <summary>Whether the inventory roots resolved, so HeldItemDto's per-location counts mean
        /// anything. False means unknown, NOT zero - and zero reads exactly like "carrying nothing"
        /// on a full rig, so anything reading OnPerson must show its unknown state instead.</summary>
        [JsonProperty("inventoryLocationsKnown")]
        public bool InventoryLocationsKnown { get; set; }

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

        /// <summary>Copies on the character, including the secure container. Schema v2.
        ///
        /// Read this ONLY behind SchemaVersion >= 2 and InventoryLocationsKnown. Newtonsoft lands an
        /// absent property as 0 and the server emits 0 for a genuinely absent item, so the two are
        /// indistinguishable per item - the payload version is the only discriminator there is.</summary>
        [JsonProperty("onPerson")]
        public int OnPerson { get; set; }

        /// <summary>The found-in-raid subset of OnPerson. Schema v2.</summary>
        [JsonProperty("onPersonFoundInRaid")]
        public int OnPersonFoundInRaid { get; set; }

        /// <summary>Copies in the stash, for the "1 in stash" hint. Schema v2.</summary>
        [JsonProperty("inStash")]
        public int InStash { get; set; }

        /// <summary>Copies that are neither on the character nor in the stash - a hideout area
        /// stash, the sorting table, a quest stash. 330 of them on the reference profile, so the
        /// row says "1 elsewhere" rather than implying the item has to be bought.</summary>
        public int Elsewhere => Math.Max(0, Total - OnPerson - InStash);
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
