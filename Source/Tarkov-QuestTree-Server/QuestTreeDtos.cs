using System.Collections.Generic;

namespace QuestTreeServer
{
    /// <summary>
    /// The wire shape returned by /questtree/quests. Kept deliberately flat and pre-resolved: every
    /// name and description here is already a display string, so the client never has to reach for
    /// a locale table it may not have an entry in for a quest it has never unlocked.
    ///
    /// The client mod hand-mirrors every type in this file, KappaPayloadDto included, in
    /// Source/Tarkov-QuestTree/QuestGraph/QuestDto.cs. The two must stay in step - they cannot
    /// share a file, because the two halves target different frameworks (net10.0 here,
    /// netstandard2.1 in the Unity plugin) - so change either and change the other in the same
    /// commit.
    /// </summary>
    public sealed class QuestPayloadDto
    {
        /// <summary>Bumped whenever the shape below changes, so an old client paired with a new
        /// server (or the reverse) can say so plainly instead of silently mis-parsing.
        /// v2 (1.8.0): ObjectiveDto.FoundInRaid. v4 (1.9.0): QuestDto.DerivedLocations.
        /// v5 (1.10.1): RewardDto.ShortName and RewardDto.Template.</summary>
        public int SchemaVersion { get; set; } = 5;

        /// <summary>The server half's version, so a mismatch warning on the client can name it -
        /// the other three payloads already did.</summary>
        public string ModVersion { get; set; } = "";

        public List<QuestDto> Quests { get; set; } = new();
    }

    /// <summary>A map a quest was placed on by its objectives rather than by its own Location field
    /// (schema v4). Empty for a quest that names a real map, and for one with no harvested zones.</summary>
    public sealed class DerivedLocationDto
    {
        /// <summary>Internal name, in the SAME keyspace as QuestDto.LocationKey - which is NOT the
        /// canonical one. ZoneStore.Canonical folds factory4_night into factory4_day and Sandbox_high
        /// into Sandbox, while LocationKey keeps them apart, so emitting the canonical name here
        /// would file a derived quest under a key that Factory night and Ground Zero above level 20
        /// never match. Alias-expanded instead: one entry per real location whose Canonical form is
        /// the harvested map.</summary>
        public string Key { get; set; } = "";

        /// <summary>Display name, matching QuestDto.LocationId.</summary>
        public string Name { get; set; } = "";
    }

    public sealed class QuestDto
    {
        public string Id { get; set; } = "";

        /// <summary>Display name, already resolved through the locale table. Falls back to the raw
        /// internal QuestName when the locale has no entry, never to a bare key.</summary>
        public string Name { get; set; } = "";

        public string TraderId { get; set; } = "";

        /// <summary>"Pmc", "Usec", "Bear" etc. straight off the quest. Sent so the client can mark a
        /// quest that this profile's faction can never take; never used to filter here.</summary>
        public string Side { get; set; } = "";

        /// <summary>Quest type as the game classifies it (Standard, Elimination, PickUp, ...).</summary>
        public string Type { get; set; } = "";

        /// <summary>Player level the quest requires, from its AvailableForStart "Level" condition;
        /// 0 when it has none. This is the single most useful thing to show on a quest you have not
        /// unlocked, since a level gate is the most common reason it is out of reach.</summary>
        public int Level { get; set; }

        /// <summary>Map the quest takes place on, or "any". Shown on the node subtitle.</summary>
        public string LocationId { get; set; } = "";

        /// <summary>The map's raw internal id ("bigmap", "factory4_day"). Sent alongside the display
        /// name because that is what other tools key on - DynamicMaps' map configs list internal
        /// names, and a localized display name cannot be matched against them.</summary>
        public string LocationKey { get; set; } = "";

        /// <summary>The maps this quest was placed on by its objectives, when its own
        /// Location field said nothing useful - "any", blank, or a string that is not a
        /// location id at all. A LIST because a quest can genuinely span maps: one plants at
        /// an aishi_shoreline zone and an aishi_woods zone, and collapsing that to a single
        /// map would be a different lie. Empty for hand-ins, skills and trader tasks, which
        /// have no zones and belong on no map.</summary>
        public List<DerivedLocationDto> DerivedLocations { get; set; } = new();

        /// <summary>True when the quest is gated behind a seasonal/holiday event, so it reads as
        /// intentionally unavailable rather than as a bug in the tree.</summary>
        public bool IsEvent { get; set; }

        /// <summary>True when the quest is restricted to game editions other than nothing - i.e. it
        /// carries an edition whitelist or blacklist at all. The client uses it only as a label.</summary>
        public bool EditionRestricted { get; set; }

        public List<PrerequisiteDto> Prerequisites { get; set; } = new();

        public List<ObjectiveDto> Objectives { get; set; } = new();

        /// <summary>The weapon-build requirement, when this quest states one. Null for the
        /// other 774 quests. Schema v4.</summary>
        public WeaponBuildDto? WeaponBuild { get; set; }

        public List<RewardDto> Rewards { get; set; } = new();
    }

    /// <summary>One AvailableForStart quest condition - "this other quest must be in one of these
    /// statuses". This is what the tree draws its edges from.</summary>
    public sealed class PrerequisiteDto
    {
        public string Target { get; set; } = "";

        /// <summary>Quest statuses that satisfy the condition, as their enum names
        /// (Success, Started, ...).</summary>
        public List<string> Status { get; set; } = new();

        /// <summary>Seconds that must pass after the prerequisite before this quest unlocks
        /// (the wiki's quest sheet: "seconds that must have passed since completing the quest
        /// target"). 0 for the overwhelming majority. An earlier comment here said hours.</summary>
        public int AvailableAfter { get; set; }
    }

    public sealed class ObjectiveDto
    {
        public string Id { get; set; } = "";

        /// <summary>Human-readable objective text from the locale table, e.g. "Eliminate 15 Scavs
        /// on Customs". Falls back to the condition type when the locale has no entry.</summary>
        public string Text { get; set; } = "";

        public bool IsNecessary { get; set; }

        /// <summary>The condition's raw type (HandoverItem, FindItem, CounterCreator, ...).</summary>
        public string ConditionType { get; set; } = "";

        /// <summary>Item template ids this objective refers to, for every condition shaped around
        /// an item: handing one over, finding one in raid, and carrying one in to leave or plant
        /// somewhere. Empty for everything else. This is what lets the client build the Collector
        /// hand-in checklist, and the "items to bring" list, from live data rather than a
        /// hardcoded one.</summary>
        public List<string> TargetItems { get; set; } = new();

        /// <summary>Display names for TargetItems, index for index (schema v3).
        ///
        /// Without this the client had to guess a name out of the objective sentence, taking
        /// whatever followed the last colon - so "Mark the first trading post with an MS2000 Marker
        /// on Shoreline", which has no colon at all, was shown to the player as the item's name.
        /// The locale table is right here on the server; guessing was never necessary.</summary>
        public List<string> TargetItemNames { get; set; } = new();

        /// <summary>How many of the target item the objective needs.</summary>
        public int Count { get; set; }

        /// <summary>Whether the items must be found in raid - the condition's own flag. The
        /// client used to infer this from the English objective sentence, which a localised
        /// install or a modded quest can word any way it likes.</summary>
        public bool FoundInRaid { get; set; }

        /// <summary>The zone ids this objective happens in - the condition's own zoneId, or the
        /// VisitPlace/InZone targets inside a CounterCreator. Empty for objectives with no place
        /// (hand-ins, plain kill counts, skills). The map resolves these against harvested zones.</summary>
        public List<string> ZoneIds { get; set; } = new();
    }

    public sealed class RewardDto
    {
        /// <summary>RewardType enum name: Experience, TraderStanding, Item, Skill, ...</summary>
        public string Type { get; set; } = "";

        public double Value { get; set; }

        /// <summary>Already-resolved display name for the thing being rewarded - an item name, a
        /// skill name, a trader name - or empty for rewards that are just a number (Experience).</summary>
        public string Name { get; set; } = "";

        /// <summary>The item's SHORT name where the locale has one - "AFAK" for "AFAK tactical
        /// individual first aid kit". Empty when there is none, and the caller falls back to Name.
        ///
        /// Sent as well as Name rather than instead of it: a short name is what a list wants and a
        /// full name is what a single row wants, and only the locale knows either.</summary>
        public string ShortName { get; set; } = "";

        /// <summary>The item template this reward hands over or unlocks, where it is an item at all.
        /// Lets the client open the game's own inspect window on it.</summary>
        public string Template { get; set; } = "";

        /// <summary>Set for trader-scoped rewards (standing, unlocks, assort unlocks).</summary>
        public string TraderId { get; set; } = "";
    }

    /// <summary>The Kappa container checklist returned by /questtree/kappa - the Collector quest's
    /// hand-in items paired with what the profile actually holds. Rebuilt per request, since the
    /// stash changes as the player plays.</summary>
    public sealed class KappaPayloadDto
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Version of the server half. Sent so the client can name both versions when they
        /// disagree, rather than just saying something went wrong.</summary>
        public string ModVersion { get; set; } = ModInfo.Version;

        /// <summary>False when no Collector quest exists at all (a heavily modded install), so the
        /// client can say so rather than showing a confusing empty list.</summary>
        public bool CollectorFound { get; set; }

        public string CollectorQuestId { get; set; } = "";

        /// <summary>QuestStatusEnum name - Locked until the player accepts Collector.</summary>
        public string CollectorStatus { get; set; } = "";

        public List<KappaItemDto> Items { get; set; } = new();

        /// <summary>
        /// The Kappa quest list: every quest Collector requires, taken from Collector's own
        /// AvailableForStart conditions. Read from SPT's shipped quests.json rather than the live
        /// quest table, because a quest mod can rewrite Collector in memory - on the install this
        /// was built against, the live copy carries 4 prerequisites where the database has 136.
        /// </summary>
        public List<string> KappaQuestIds { get; set; } = new();

        /// <summary>"database" when the list above came from the shipped quests.json, "live" when
        /// that file could not be read and the in-memory quest table was used instead.</summary>
        public string KappaSource { get; set; } = "";

        /// <summary>How many prerequisites the LIVE Collector actually has. Compared against
        /// KappaQuestIds by the client to warn when a mod has changed the requirement.</summary>
        public int LiveCollectorPrerequisiteCount { get; set; }
    }

    public sealed class KappaItemDto
    {
        /// <summary>The Collector condition this item satisfies; also the hand-in progress key.</summary>
        public string ConditionId { get; set; } = "";

        public string Template { get; set; } = "";

        public string Name { get; set; } = "";

        public int Required { get; set; }

        /// <summary>How many found-in-raid copies are in the profile. Collector accepts only these.</summary>
        public int OwnedFoundInRaid { get; set; }

        /// <summary>Total copies held, found-in-raid or not - so the client can distinguish "you
        /// have one but it is not FiR" from "you do not have one".</summary>
        public int OwnedTotal { get; set; }

        public bool HandedIn { get; set; }
    }

    /// <summary>What the client needs to reason about THIS player: level, faction, trader state,
    /// live objective counters, and why each locked quest is locked. Rebuilt per request - all of
    /// it moves as you play.</summary>
    public sealed class ProfilePayloadDto
    {
        /// <summary>2 (1.9.0): HeldItemDto.OnPerson/OnPersonFoundInRaid/InStash, and
        /// InventoryLocationsKnown beside them.</summary>
        public int SchemaVersion { get; set; } = 2;

        public string ModVersion { get; set; } = ModInfo.Version;

        /// <summary>False for an out-of-game request, where there is no profile to read. The shape
        /// is still valid so the client degrades instead of failing.</summary>
        public bool HasProfile { get; set; }

        public int Level { get; set; }

        public string Side { get; set; } = "";

        public string GameVersion { get; set; } = "";

        public List<TraderStateDto> Traders { get; set; } = new();

        /// <summary>Condition id -> current count. Keyed to match ObjectiveDto.Id, so the client can
        /// pair "eliminate 15 Scavs" with "you have 7".</summary>
        public Dictionary<string, double> ConditionProgress { get; set; } = new();

        /// <summary>Quest id -> the single gate currently blocking it. Absent means not blocked (or
        /// already started).</summary>
        public Dictionary<string, LockReasonDto> LockReasons { get; set; } = new();

        /// <summary>Whether the inventory roots resolved, so the per-location counts on HeldItemDto
        /// mean anything. False means UNKNOWN, not zero - and zero reads exactly like "carrying
        /// nothing" on a full rig, which is why this is on the wire rather than inferred. Every
        /// other degraded path in this server half names itself the same way: HasProfile,
        /// CollectorFound, KappaSource, ZonesKnown.</summary>
        public bool InventoryLocationsKnown { get; set; }

        /// <summary>Item template -> how many the profile holds, for items some quest asks for.
        /// Absent means none held.</summary>
        public Dictionary<string, HeldItemDto> ItemsOwned { get; set; } = new();
    }

    /// <summary>How many of an item the profile holds. Found-in-raid is separate because most
    /// quest hand-ins only accept found-in-raid copies.</summary>
    /// <summary>How many of an item the profile holds. Found-in-raid is separate because most quest
    /// hand-ins only accept found-in-raid copies; location is separate (schema v2) because a pre-raid
    /// check has to know what is on your character rather than what you own.</summary>
    public sealed class HeldItemDto
    {
        public int FoundInRaid { get; set; }

        public int Total { get; set; }

        /// <summary>Copies on the character, including the secure container. Schema v2. Only
        /// meaningful when ProfilePayloadDto.InventoryLocationsKnown is true.</summary>
        public int OnPerson { get; set; }

        /// <summary>The found-in-raid subset of OnPerson. Schema v2.</summary>
        public int OnPersonFoundInRaid { get; set; }

        /// <summary>Copies in the stash, for the "1 in stash" hint. Schema v2.</summary>
        public int InStash { get; set; }
    }

    public sealed class TraderStateDto
    {
        public string Id { get; set; } = "";

        public int LoyaltyLevel { get; set; }

        public double Standing { get; set; }

        public bool Unlocked { get; set; }
    }

    /// <summary>Why a quest cannot be started. Only the first blocker is reported - a list of five
    /// reasons is no more useful than the one thing to go and do.</summary>
    public sealed class LockReasonDto
    {
        /// <summary>OtherFaction | Edition | Event | Level | Loyalty | Standing | Prerequisite</summary>
        public string Kind { get; set; } = "";

        /// <summary>A short player-facing phrase, e.g. "Requires level 15".</summary>
        public string Detail { get; set; } = "";

        public int RequiredValue { get; set; }

        public int CurrentValue { get; set; }

        /// <summary>Set for trader-scoped gates so the client can name the trader from its own
        /// session data rather than the server guessing at a display name.</summary>
        public string TraderId { get; set; } = "";

        /// <summary>Set for Prerequisite: the outstanding quests, which the client can already name
        /// from its own graph.</summary>
        public List<string> BlockingQuestIds { get; set; } = new();
    }


    /// <summary>What you must be CARRYING to finish the quests on each map.
    ///
    /// Deliberately narrow: only the carry-in conditions count. A hand-in is done at a trader and a
    /// find-in-raid is found there, so neither is something to bring, and including them would turn
    /// a useful verdict into a shopping list nobody can satisfy.
    ///
    /// No verdict is reached here. The server reports facts - what each condition wants, where it
    /// happens, what the profile holds, what state the quest is in - and the client folds them,
    /// because only the client knows the "count unaccepted quests" setting and only the client has
    /// ENodeStatus where it has a graph at all.
    /// </summary>

    /// <summary>The requirement a weapon-build quest states, in the terms the game compares against.
    ///
    /// Present only for WeaponAssembly conditions. 56 quests on the reference install carry one - 32
    /// vanilla across 29 quests, the rest from mods - and until now every one of them rendered as
    /// "Handover the custom M4A1  0/1", which says nothing at all.</summary>
    public sealed class WeaponBuildDto
    {
        /// <summary>The base weapon. Every vanilla condition names exactly one, which is measured
        /// rather than assumed.</summary>
        public string WeaponTemplate { get; set; } = "";

        public string WeaponName { get; set; } = "";

        /// <summary>Thresholds actually in force, already filtered. Keyed by the condition's own
        /// field name so the client needs no enum to stay in step with modded data.</summary>
        public List<WeaponBuildThresholdDto> Thresholds { get; set; } = new();

        /// <summary>Specific mods the build must contain, resolved to names. 23 of the 32 vanilla
        /// conditions have these, which is a majority rather than an edge case.</summary>
        public List<string> RequiredItemNames { get; set; } = new();

        /// <summary>Categories one fitted part must come from - "Comb. tact. device" and the like.
        /// 16 of 32.</summary>
        public List<string> RequiredCategoryNames { get; set; } = new();
    }

    public sealed class WeaponBuildThresholdDto
    {
        /// <summary>The condition's own field name: "ergonomics", "recoil", "weight".</summary>
        public string Field { get; set; } = "";

        /// <summary>"&gt;=", "&lt;=" and so on, as the quest data writes it.</summary>
        public string Compare { get; set; } = "";

        public double Value { get; set; }
    }

    public sealed class RaidCheckDto
    {
        /// <summary>1 (1.9.0): first version. The client mirror carries SupportedSchemaVersion = 1
        /// and warns on a mismatch, exactly as the other four payloads do - an unversioned payload
        /// is how a mismatched pair gets to disagree silently about whether you are ready.</summary>
        public int SchemaVersion { get; set; } = 1;

        public string ModVersion { get; set; } = ModInfo.Version;

        /// <summary>False out of game, where there is no profile to read - the client shows its
        /// neutral state rather than a verdict.</summary>
        public bool HasProfile { get; set; }

        /// <summary>False when the inventory roots could not be read, so on-person counts are
        /// unknown rather than zero. The cue must stay neutral on this, never green and never a
        /// bare "missing".</summary>
        public bool InventoryLocationsKnown { get; set; }

        /// <summary>Carry conditions with no zone of their own AND no map for their quest either,
        /// so they belong nowhere. Reported and logged; it gates NOTHING.
        ///
        /// Named differently from the per-map UnplaceableConditions on purpose: that one gates its
        /// map, this one gates nothing, and two fields with the same name and opposite rules told
        /// apart only by which object they hang on is how the wrong one gets read. On a fully
        /// harvested install this is 1 - "Friend from Norvinsk - Part 5" names zoneId "1" - and
        /// gating on it would leave every map permanently neutral.</summary>
        public int ConditionsWithNoMap { get; set; }

        public List<RaidCheckMapDto> Maps { get; set; } = new();

        /// <summary>Template -> what the profile holds, for every template mentioned above. Reuses
        /// the profile payload's own type rather than restating five counts per row: an item wanted
        /// by six conditions is held exactly once.</summary>
        public Dictionary<string, HeldItemDto> Held { get; set; } = new();

        /// <summary>Template -> resolved display name, for every template mentioned above.
        ///
        /// Required by the client rule that a row is named after the template actually HELD, which
        /// cannot be executed without it: a requirement carries template ids and one Name taken from
        /// the first of them, and the client has no locale table - and on the ready-up screen no
        /// quest graph either - so it cannot turn "the F-1 is the one you are carrying" into the
        /// words "F-1 hand grenade".
        ///
        /// On the envelope beside Held for the same reason Held is: the six grenades Confidential
        /// Info accepts are named once, not once per condition that wants them.</summary>
        public Dictionary<string, string> ItemNames { get; set; } = new();
    }

    public sealed class RaidCheckMapDto
    {
        /// <summary>Internal name in QuestDto.LocationKey's keyspace, which is NOT the canonical one
        /// - emitted one row per real location, alias-expanded. ZoneStore.Canonical folds
        /// factory4_night into factory4_day and Sandbox_high into Sandbox, and the client hands over
        /// the un-folded SelectedLocation.Id, so a canonical key here would mean Factory night and
        /// Ground Zero above level 20 match no row at all - and no row renders green.</summary>
        public string LocationKey { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>Conditions listed on this map by assumption rather than by a harvested zone of
        /// their own - the declared or derived map of the quest was used as the fallback. The row
        /// still appears, since over-listing costs a false amber and that is the safe direction, but
        /// the map may not go green: the assumption may have put it here instead of where it
        /// belongs.</summary>
        public int UnplaceableConditions { get; set; }

        /// <summary>Whether this map has harvested zones at all. Green requires positive evidence:
        /// with no harvest a map cannot place an "any"-location carry condition, so it may inform
        /// but never reassure. Per map, so a fresh install greys out exactly what it should.</summary>
        public bool ZonesHarvested { get; set; }

        public List<RaidCheckRequirementDto> Requirements { get; set; } = new();
    }

    /// <summary>One carry condition, on the map that condition actually happens on.
    ///
    /// Per condition, not per quest, and that is the whole point. "Is This a Reference" wants 27
    /// WI-FI cameras spread over eight maps; summed per quest it would tell you to carry all 27 onto
    /// Factory and read MISSING 20 whatever you brought. Sixteen vanilla quests have this shape.
    /// </summary>
    public sealed class RaidCheckRequirementDto
    {
        public string QuestId { get; set; } = "";

        public string QuestName { get; set; } = "";

        /// <summary>Every template the condition accepts - the second condition of Hot Zone takes
        /// any of nine ballistic plates. Held counts are summed across ALL of them, and the client
        /// picks the display name from whichever is actually held.</summary>
        public List<string> Templates { get; set; } = new();

        /// <summary>The canonical name, from the first template. A fallback for the red case: when
        /// nothing is held there is no "what you are carrying" to name it after.</summary>
        public string Name { get; set; } = "";

        public int Needed { get; set; }

        /// <summary>Whether the condition demands found-in-raid copies. No vanilla carry condition
        /// sets it - all 149 LeaveItemAtLocation say false and all 96 PlaceBeacon omit it - so this
        /// ships unexercised. Kept because a quest mod can set it, not because it is verified.</summary>
        public bool FoundInRaid { get; set; }

        /// <summary>The status of the quest as a string, the way KappaPayloadDto.CollectorStatus
        /// already does it. Sent as DATA, not as a verdict: the client decides what counts, but it
        /// cannot decide from nothing, and on the ready-up screen it has no quest graph to consult.
        ///
        /// Named Status, NOT QuestStatus: the SPT type of that name is what each entry in
        /// profile.Quests is, and a property of that name shadows it. A never-touched quest reads
        /// AvailableForStart here rather than Locked - see QuestFacts.StatusOf, which is the whole
        /// reason this cannot be a bare read of the profile entry.</summary>
        public string Status { get; set; } = "";
    }

    /// <summary>Quest-item spawn markers, grouped by map. See MapMarkerPayloadBuilder for why the
    /// positions come from the loot table rather than from the quests.</summary>
    public class MapMarkerPayloadDto
    {
        /// <summary>2: per-map zone coverage fields on MapMarkerSetDto (1.3.0). 3: Template on
        /// item markers (1.5.0).</summary>
        public int SchemaVersion { get; set; } = 3;

        public string Version { get; set; } = "";
        public List<MapMarkerSetDto> Maps { get; set; } = new();
    }

    public class MapMarkerSetDto
    {
        /// <summary>The map's internal name ("bigmap"), matching QuestDto.LocationKey.</summary>
        public string LocationKey { get; set; } = "";

        public List<MapMarkerDto> Markers { get; set; } = new();

        /// <summary>How many distinct zone ids this map's quests reference, and how many of those a
        /// harvest has placed. Lets the view say "raid this map once" instead of silently missing
        /// pins. Both zero when no quest here has a zone-shaped objective.</summary>
        public int ZonesWanted { get; set; }

        public int ZonesKnown { get; set; }

        /// <summary>When this map was last harvested (UTC), or empty if never.</summary>
        public string HarvestedAt { get; set; } = "";
    }

    public class MapMarkerDto
    {
        public string ItemName { get; set; } = "";

        /// <summary>The quests that want this item, by display name.</summary>
        public List<string> Quests { get; set; } = new();

        /// <summary>The same quests by id, so the client can look up each one's live status in its
        /// own graph - which is where status belongs, since the server does not track what the
        /// player has started.</summary>
        public List<string> QuestIds { get; set; } = new();

        /// <summary>"item" for a place the thing you need spawns, "objective" for a place the quest
        /// itself happens. See MapMarkerPayloadBuilder for where each comes from.</summary>
        public string Kind { get; set; } = "";

        /// <summary>How many separate places this item can spawn on this map. More than one means
        /// the item is at ONE of them per raid, and the view says so rather than implying every
        /// pin holds a copy.</summary>
        public int Alternatives { get; set; }

        /// <summary>Item markers only: the item's template id, so the client can join a pin to the
        /// stash count it already has for that item. Empty for objective markers.</summary>
        public string Template { get; set; } = "";

        /// <summary>Objective markers only: where the point sits as a percentage across and down the
        /// map image. Sent instead of world coordinates because that is how the source states it,
        /// and because percentages need no coordinate transform - only the rectangle the image
        /// covers, which the client knows and the server does not.</summary>
        public float LeftPercent { get; set; }

        public float TopPercent { get; set; }

        /// <summary>The floor the source names, such as "Ground_Level" - the same shape as the map
        /// layers' own names.</summary>
        public string Floor { get; set; } = "";

        /// <summary>World coordinates. Y is the height, which is what decides the floor: a map's
        /// layers each declare the height band they cover.</summary>
        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }
    }
}
