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
}
