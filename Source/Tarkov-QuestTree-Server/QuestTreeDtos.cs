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
        /// server (or the reverse) can say so plainly instead of silently mis-parsing.</summary>
        public int SchemaVersion { get; set; } = 1;

        public List<QuestDto> Quests { get; set; } = new();
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

        /// <summary>True when the quest is gated behind a seasonal/holiday event, so it reads as
        /// intentionally unavailable rather than as a bug in the tree.</summary>
        public bool IsEvent { get; set; }

        /// <summary>True when the quest is restricted to game editions other than nothing - i.e. it
        /// carries an edition whitelist or blacklist at all. The client uses it only as a label.</summary>
        public bool EditionRestricted { get; set; }

        public List<PrerequisiteDto> Prerequisites { get; set; } = new();

        public List<ObjectiveDto> Objectives { get; set; } = new();

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

        /// <summary>Hours that must pass after the prerequisite before this quest unlocks. 0 for
        /// the overwhelming majority.</summary>
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

        /// <summary>Item template ids this objective refers to, for the item-shaped conditions
        /// (hand over / find in raid). Empty for everything else. This is what lets the client
        /// build the Collector hand-in checklist from live data instead of a hardcoded list.</summary>
        public List<string> TargetItems { get; set; } = new();

        /// <summary>How many of the target item the objective needs.</summary>
        public int Count { get; set; }
    }

    public sealed class RewardDto
    {
        /// <summary>RewardType enum name: Experience, TraderStanding, Item, Skill, ...</summary>
        public string Type { get; set; } = "";

        public double Value { get; set; }

        /// <summary>Already-resolved display name for the thing being rewarded - an item name, a
        /// skill name, a trader name - or empty for rewards that are just a number (Experience).</summary>
        public string Name { get; set; } = "";

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
        public int SchemaVersion { get; set; } = 1;

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

        /// <summary>Item template -> how many the profile holds, for items some quest asks for.
        /// Absent means none held.</summary>
        public Dictionary<string, HeldItemDto> ItemsOwned { get; set; } = new();
    }

    /// <summary>How many of an item the profile holds. Found-in-raid is separate because most
    /// quest hand-ins only accept found-in-raid copies.</summary>
    public sealed class HeldItemDto
    {
        public int FoundInRaid { get; set; }

        public int Total { get; set; }
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

    /// <summary>Quest-item spawn markers, grouped by map. See MapMarkerPayloadBuilder for why the
    /// positions come from the loot table rather than from the quests.</summary>
    public class MapMarkerPayloadDto
    {
        public string Version { get; set; } = "";
        public List<MapMarkerSetDto> Maps { get; set; } = new();
    }

    public class MapMarkerSetDto
    {
        /// <summary>The map's internal name ("bigmap"), matching QuestDto.LocationKey.</summary>
        public string LocationKey { get; set; } = "";

        public List<MapMarkerDto> Markers { get; set; } = new();
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
