using System.Collections.Generic;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

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
        ///
        /// v3, v8 and v9 have no entry below and cannot get one: they were bumped without being written
        /// down, which is the failure this comment's own contract exists to prevent, and the shapes are no
        /// longer recoverable. Said out loud rather than left as a gap a reader might take for a numbering
        /// quirk. (v1 needs no entry - it is the original shape, not a bump.)
        ///
        /// v2 (1.8.0): ObjectiveDto.FoundInRaid. v4 (1.9.0): QuestDto.DerivedLocations.
        /// v5 (1.10.1): RewardDto.ShortName and RewardDto.Template. v6 (1.10.2): the weapon build
        /// keeps its template ids, records dropped zero thresholds and empty-slot counts, and carries
        /// the stat model's own numbers for the quest's example parts. v7 (1.10.3): a quest carries
        /// EVERY weapon build it asks for rather than only the first. v10 (1.12.0): a solved build is a
        /// DIFF against the weapon's default preset - every part says whether it is already fitted, a
        /// swap or an addition, and the build carries the number of changes. v11: RewardDto gains
        /// RoubleValue, IsCurrency, LoyaltyLevel, FoundInRaid, and a TraderId resolved per reward type
        /// instead of read from one field.
        ///
        /// The release numbers above are approximate and at least two are wrong - v10 and v11 both landed
        /// between the 1.11.0 and 1.12.0 archives, so the labels make the list read as though v11 came
        /// first. They are kept because they are roughly useful and removing them loses the only dating
        /// there is; trust the ORDER of the entries, not the bracketed version, and check git for a date
        /// that matters. New entries go without a release number until the release is cut.</summary>
        public int SchemaVersion { get; set; } = 11;

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

        /// <summary>Every weapon-build requirement this quest states. Empty for the other 774
        /// quests. Schema v4; a LIST since v7, because a quest can ask for several - "Old Friend's
        /// Request" wants a T-5000M, a PP-19-01 and a Glock 17.</summary>
        public List<WeaponBuildDto> WeaponBuilds { get; set; } = new();

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

        /// <summary>The condition's own isNecessary flag, defaulted to TRUE when absent - and a field with
        /// no positive signal at all, so nothing may filter on it.
        ///
        /// Measured on the shipped database: across 1,606 finish conditions it is absent 1,080 times,
        /// explicitly false 526 times, and true ZERO times. The client filtered on it once and hid the
        /// objectives of 173 vanilla and 38 modded quests - 42% of them showing requirements, rewards and
        /// unlocks with no what-to-do and no where-to-go. The client mirror carries the warning; this is the
        /// end that produces the value, so it carries it too.</summary>
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

        /// <summary>Set for trader-scoped rewards (standing, unlocks, assort unlocks).
        ///
        /// Standing and trader-unlock rewards name their trader in Target, not TraderId, which is why
        /// this is resolved per type rather than read from one field. Reading TraderId for all of them
        /// left every one of the 532 standing rewards without a trader, and the client rendered them
        /// as a bare "Reputation +0.02". (508 was the figure here for two releases; it is the Experience
        /// reward count, borrowed from a line further down.)</summary>
        public string TraderId { get; set; } = "";

        /// <summary>What this reward is worth in roubles, or null when that cannot be known.
        ///
        /// Null and zero are different answers and the ranking needs to tell them apart: a reward
        /// whose item has no handbook price is unvalued, and scoring it as worthless would push every
        /// modded reward to the bottom for a reason that is about our data rather than the reward.</summary>
        public long? RoubleValue { get; set; }

        /// <summary>True when the reward IS money rather than an item worth money. 392 of the 1,472
        /// success-phase item rewards name a currency, and a player weighs the two differently - roubles
        /// are fungible and gear is not. (455 was the figure here; it was never the cash count.)</summary>
        public bool IsCurrency { get; set; }

        /// <summary>The loyalty level an unlocked offer appears at, for AssortmentUnlock and
        /// ProductionScheme. Zero when the reward is not an unlock. An offer unlocked at LL4 is worth
        /// less to someone at LL2 than one they can buy today.</summary>
        public int LoyaltyLevel { get; set; }

        /// <summary>Whether an item reward arrives flagged found-in-raid. Matters because a
        /// found-in-raid item can settle a quest requirement that a bought one cannot.</summary>
        public bool FoundInRaid { get; set; }
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
        /// InventoryLocationsKnown beside them. HeldItemDto.InTaskItems (1.12.1) was added WITHIN v2
        /// rather than bumping to 3 - see its own comment for why - so v2 is not a fixed field list and
        /// a reader finding it absent is looking at an older server, not a broken one.</summary>
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

        /// <summary>Copies in the task-item containers, which the game's own screen calls "Task items
        /// on character" and "Task items in stash". A SUBSET of OnPerson, never a separate place.
        ///
        /// Additive, and the profile schema stays at 2 on purpose. No v2 field changes meaning, so an
        /// old server paired with a new client sends 0 here and the wording falls back to "on you" -
        /// still the right answer. Bumping would instead fail the client's >= SupportedSchemaVersion
        /// gate and collapse InventoryLocationsKnown, costing the whole location split to say one
        /// word differently.</summary>
        public int InTaskItems { get; set; }
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


    /// <summary>The requirement a weapon-build quest states, in the terms the game compares against.
    ///
    /// Present only for WeaponAssembly conditions. 56 quests on the reference install carry one - 32
    /// vanilla across 29 quests, the rest from mods - and until now every one of them rendered as
    /// "Handover the custom M4A1  0/1", which says nothing at all.</summary>
    public sealed class WeaponBuildDto
    {
        /// <summary>Identifies this requirement across payloads: the same hash the build history is keyed
        /// on, so the per-profile answer on /questtree/builds can be joined to this row. Two quests
        /// stating the same requirement share it, which is correct - they share the answer too.</summary>
        public string Key { get; set; } = "";

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

        /// <summary>The same requirements as TEMPLATE IDS, parallel to the name lists above.
        ///
        /// The names are for reading and the ids are for working with, and until now only the names
        /// survived: the parse resolved each id straight into a locale string and dropped it. That
        /// is fine for a display and useless for anything that has to reason about the parts, which
        /// is why a build generator could not be written against this DTO.</summary>
        public List<string> RequiredItemIds { get; set; } = new();

        public List<string> RequiredCategoryIds { get; set; } = new();

        /// <summary>A slot the build must leave EMPTY - the condition's EmptyTacticalSlot. Never
        /// read before, and Gunsmith quests use it.</summary>
        public double? EmptyTacticalSlots { get; set; }

        /// <summary>Threshold fields the condition named but whose value was zero, and which are
        /// therefore not in Thresholds.
        ///
        /// Kept because "dropped as unconstrained noise" and "genuinely constrained to zero" look
        /// identical once the row is gone, and a solver has to be able to tell them apart.</summary>
        public List<string> ZeroThresholdFields { get; set; } = new();

        /// <summary>A build that satisfies this quest, worked out from the parts that exist, or
        /// null when none was found.
        ///
        /// Solved once while the payload is built rather than per request: the answer does not
        /// depend on the profile, sixty of them take about 200ms, and the client's request handler
        /// is synchronous on Unity's main thread. When this learns to restrict itself to parts the
        /// player can buy it stops being profile-independent and moves to a route of its own.</summary>
        public SolvedBuildDto? Solution { get; set; }

        /// <summary>What this mod's own stat model says the quest's OWN example parts score on this
        /// weapon, or null when the condition names no parts.
        ///
        /// THE VERIFICATION INSTRUMENT. WeaponStatModel shipped a release before any solver
        /// deliberately, to be proven against the running game first - but nothing ever called it,
        /// so the check was never performed. Fit exactly the named parts to this weapon, open the
        /// game's inspect screen, and compare these numbers. If they disagree the model is wrong and
        /// no build generator may be written on it.</summary>
        public WeaponModelCheckDto? ModelCheck { get; set; }
    }

    /// <summary>What the stat model believes about an exact, named set of parts.
    ///
    /// Partial by nature: it scores the weapon plus whatever the quest's condition happens to name,
    /// which is usually a few leaf parts rather than a complete gun. That is enough to catch a wrong
    /// COMBINING RULE - a sum where the game multiplies, a sum where it selects - which is the
    /// failure this is arranged to prevent. It is not a proof that a finished build scores right.</summary>
    public sealed class WeaponModelCheckDto
    {
        public double Ergonomics { get; set; }
        public double Recoil { get; set; }
        public double Weight { get; set; }

        /// <summary>Null means the model does not claim to know, which must not be read as a pass.</summary>
        public int? MagazineCapacity { get; set; }

        public double? EffectiveDistance { get; set; }

        /// <summary>How many of the named parts the model could actually price. A part missing from
        /// the template table contributes nothing and would otherwise skew the comparison silently.</summary>
        public int PartsScored { get; set; }

        public int PartsNamed { get; set; }

        /// <summary>Stats clamped on the way in, from WeaponStatModel. A clamp here means the
        /// numbers below are not the game's and the comparison is void.</summary>
        public List<string> Clamped { get; set; } = new();

        /// <summary>Required slots on the weapon that the named parts leave empty, and how many
        /// required slots it has.
        ///
        /// A quest names parts the build must CONTAIN, which on some weapons is close to a whole
        /// gun and on others is a handful of attachments. When slots are left empty these numbers
        /// are a floor rather than a prediction, and saying so is the difference between a reader
        /// trusting the instrument and concluding it is broken.</summary>
        public int UnfilledRequiredSlots { get; set; }

        public int RequiredSlots { get; set; }
    }

    /// <summary>A worked-out build: the parts, what it scores, and - said plainly - what about it
    /// has not been checked.</summary>
    public sealed class SolvedBuildDto
    {
        /// <summary>Whether every threshold the model can score is met, every required slot is
        /// filled, and every named part and category is present.
        ///
        /// Not the same as "this will be accepted". Height and width are real constraints in five
        /// vanilla quests and the model cannot score either, so a build can be complete by every
        /// measure available here and still be refused for its assembled size.</summary>
        public bool Satisfies { get; set; }

        /// <summary>False when the condition constrains something nothing here can score - height or
        /// width, in five of the vanilla quests.
        ///
        /// Satisfies on its own asserts more than it checks on those five. It means "everything that
        /// CAN be checked is met", the difference is a constraint nobody verified, and a flag is the
        /// only way to say so that does not depend on the reader noticing a list further down. A green
        /// light that might be wrong has to look different from one that cannot be.</summary>
        public bool FullyChecked { get; set; } = true;

        /// <summary>Set when the search ran out of budget. "No build found within the budget" is a
        /// different statement from "no build exists" and the client must not merge them.</summary>
        public bool HitBudget { get; set; }

        public List<SolvedPartDto> Parts { get; set; } = new();

        /// <summary>Parts the player has to fit that the weapon does not already wear - swaps plus
        /// additions. THE NUMBER TO LEAD WITH: it is what every community guide is implicitly counting, and
        /// it is what this search now minimises.
        ///
        /// Meaningless without HasDefaults, which is why they travel together: zero changes on a weapon with
        /// no preset means "nothing to compare against", not "nothing to buy".</summary>
        public int Changes { get; set; }

        /// <summary>Whether the game ships a default preset for this weapon, and therefore whether the
        /// statuses and the change count mean anything. False for most modded weapons.</summary>
        public bool HasDefaults { get; set; }

        /// <summary>What the build scores, in the same terms the thresholds are written in.</summary>
        public List<string> Scores { get; set; } = new();

        /// <summary>Thresholds this build does not meet, with the margin.</summary>
        public List<string> Unmet { get; set; } = new();

        /// <summary>Constraints the model cannot score at all - height and width. Listed so a
        /// player knows to eyeball the grid rather than assuming a green light.</summary>
        public List<string> Unchecked { get; set; } = new();
    }

    public sealed class SolvedPartDto
    {
        /// <summary>The slot it goes in, as the game names it - "mod_muzzle", "mod_stock".</summary>
        public string Slot { get; set; } = "";

        public string Template { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>What the player has to do about this part: "fitted" (the weapon already wears it),
        /// "swap" (something else is in that slot) or "add" (the slot is empty on the default).
        ///
        /// EMPTY when the weapon has no default preset - most modded weapons - and an empty status is the
        /// honest answer there rather than a guess at what the gun ships with. A client seeing empty
        /// statuses shows the build the way it always did.</summary>
        public string Status { get; set; } = "";

        /// <summary>For a swap, the name of the part being taken off. Empty otherwise.</summary>
        public string Replaces { get; set; } = "";
    }

    public sealed class WeaponBuildThresholdDto
    {
        /// <summary>The condition's own field name: "ergonomics", "recoil", "weight".</summary>
        public string Field { get; set; } = "";

        /// <summary>"&gt;=", "&lt;=" and so on, as the quest data writes it.</summary>
        public string Compare { get; set; } = "";

        public double Value { get; set; }
    }

    /// <summary>The weapon builds as one profile can assemble them. See ProfileBuilds.</summary>
    public sealed class ProfileBuildsDto
    {
        /// <summary>1: first version. 2 (1.14.0): ItemsJson and Root on each build. An older client ignores them.</summary>
        public int SchemaVersion { get; set; } = 2;

        public string ModVersion { get; set; } = ModInfo.Version;

        /// <summary>False for an out-of-game request. The shape stays valid.</summary>
        public bool HasProfile { get; set; }

        /// <summary>True when Builds is current for this profile's traders and stash. False with an empty
        /// list means the pass has not run yet; false with builds means they are STALE - see Stale - and a
        /// recompute is queued.</summary>
        public bool Ready { get; set; }

        public bool Stale { get; set; }

        public int Level { get; set; }

        /// <summary>Whether this profile may use the flea market, and the level that gates it.</summary>
        public bool FleaAccess { get; set; }

        public int FleaLevel { get; set; }

        public List<ProfileBuildDto> Builds { get; set; } = new();
    }

    /// <summary>Asks for one solved build to be written into the player's saved weapon builds.</summary>
    public sealed class SavePresetRequest : IRequestData
    {
        /// <summary>Joins to WeaponBuildDto.Key - which requirement to save.</summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = "";

        /// <summary>The client half's version, and ABSENT means a client older than 1.13.2.
        ///
        /// That absence is the whole point. Up to 1.13.1 the client saved a preset by removing the
        /// same-named build and inserting a new one, and the removal is a POST to /client/builds/delete
        /// that deletes from the profile. It was harmless only because the server minted a fresh id
        /// every save, so the id the old client deleted was always one the profile had already
        /// superseded. From 1.13.2 the server REUSES the id - which turns that same call into "delete
        /// the preset the server just wrote", every second save, silently.
        ///
        /// The old client cannot be fixed; it is already published. But it does send this route a body
        /// with no version in it, and it renders whatever Reason comes back - so the server can
        /// recognise it exactly and answer with a sentence instead of destroying the preset.</summary>
        [JsonPropertyName("clientVersion")]
        public string ClientVersion { get; set; } = "";
    }

    public sealed class SavePresetResponse
    {
        public bool Saved { get; set; }

        /// <summary>The build's items as JSON, in the game's own on-the-wire shape, echoed back so
        /// the client can insert the build into the game's in-memory list at once.
        ///
        /// A STRING rather than a structured list, and that is the fix for a real bug. The client used
        /// to rebuild these by hand and could only fill the fields it knew - `_id`, `_tpl`, `parentId`,
        /// `slotId` - leaving `upd` and `location` null, because both are UnparsedData wrapping a raw
        /// JToken that only Newtonsoft fills. So every item in the in-memory build had no durability,
        /// no fire mode, no spawned-in-session flag, while the copy written to the profile had all of
        /// them: well-formed on disk and malformed in memory, which is what the build screen choked on.
        ///
        /// Sending the JSON lets the client deserialise it exactly as the game does when it loads
        /// builds at session start, so our in-memory build is indistinguishable from one the game
        /// read itself.</summary>
        public string ItemsJson { get; set; } = "";

        /// <summary>The id of the weapon item, which is the build's root.</summary>
        public string Root { get; set; } = "";

        /// <summary>The preset's id.</summary>
        public string Id { get; set; } = "";

        /// <summary>The preset's name as written, so the client can tell the player what to look for.</summary>
        public string Name { get; set; } = "";

        /// <summary>Why not, in a sentence a player can act on. Empty when it saved.</summary>
        public string Reason { get; set; } = "";
    }

    public sealed class ProfileBuildDto
    {
        /// <summary>The fitted parts as a TREE, kept for writing a weapon preset and deliberately not
        /// sent to the client.
        ///
        /// ProfilePartDto carries a slot and a template but no parent, so the rows on the wire cannot
        /// be reassembled into the nested item list a saved build needs - a handguard and the sight
        /// mounted on it are two flat rows there. The solver's own output does carry the parent, so
        /// it is held here and the preset is built server-side from it.</summary>
        [JsonIgnore]
        public IReadOnlyList<WeaponSolver.FittedPart>? Tree { get; set; }

        /// <summary>Joins to WeaponBuildDto.Key on the quest payload.</summary>
        public string Key { get; set; } = "";

        public string QuestName { get; set; } = "";

        public string WeaponTemplate { get; set; } = "";

        public string WeaponName { get; set; } = "";

        /// <summary>"ok" - the shared build, every part obtainable; "repaired" - searched again within what
        /// this profile can get, and passed the verifier; "blocked" - no build within reach, see Unmet and
        /// Why; "unsolved" - no shared build exists to start from.</summary>
        public string Status { get; set; } = "";

        /// <summary>For ok and repaired, the build. For a build blocked by trader level, the build that
        /// trader progress would unlock, with the gated parts marked absent and their gate named.</summary>
        public List<ProfilePartDto> Parts { get; set; } = new();

        /// <summary>Roubles at this profile's trader prices for every buyable part. Barters are counted, not
        /// priced; flea parts are estimated separately because that price moves.</summary>
        public long Cash { get; set; }

        public int Barters { get; set; }

        public long FleaEstimate { get; set; }

        /// <summary>Blocked only: the thresholds the closest obtainable attempt missed, with the margin.</summary>
        public List<string> Unmet { get; set; } = new();

        /// <summary>Blocked only. Starts with "trader level" (naming the parts, traders and levels that would
        /// unlock it), "flea market" (the level that unlocks it) or "not sold".</summary>
        public string Why { get; set; } = "";

        /// <summary>Repaired only: the independent verifier passed this build. Never true otherwise.</summary>
        public bool Verified { get; set; }

        /// <summary>Search nodes spent on this profile for this requirement. Zero for a build served as it
        /// was.</summary>
        public int Nodes { get; set; }

        /// <summary>The build as the game's flat item list, serialised by SPT's own JsonUtil - never
        /// by WireJson, whose options lack the MongoId converters and would ship every id as an
        /// empty object (the 1.12.2 fault). Empty when there is no build or it could not be
        /// flattened. The client assembles a Weapon from it and asks Inventory.IsWeaponFitsCondition,
        /// which on SPT is the entire hand-in gate; since 1.14.0 (schema 2).</summary>
        public string ItemsJson { get; set; } = "";

        /// <summary>The weapon's item id inside ItemsJson, which WeaponBuild's constructor needs.</summary>
        public string Root { get; set; } = "";
    }

    public sealed class ProfilePartDto
    {
        public string Slot { get; set; } = "";

        public string Template { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>"fitted" (on the default preset), "inplace" (already fitted to a copy of the quest's
        /// weapon this profile owns), "owned" (loose in the stash), "buyable" (a trader sells it for cash,
        /// see Price), "barter" (a trader offers it for goods - no price exists), "flea" (listable and the
        /// profile has access - Price is the server's flea price, an estimate), "absent".</summary>
        public string Tier { get; set; } = "";

        /// <summary>Where a copy the profile holds is FITTED, when it is not loose: "fitted to your
        /// &lt;weapon&gt;" or "fitted to your equipped &lt;weapon&gt;". Set alongside a priced tier, never instead of one - a
        /// part on a gun in use is priced as a purchase, because stripping a working weapon is the
        /// player's call and never the mod's assumption. Empty when there is no such copy.</summary>
        public string Where { get; set; } = "";

        /// <summary>True for a part the quest itself names. Its tier says whether the player can get it, but
        /// no search can avoid it - the quest demands it - so a build is not "blocked" by it.</summary>
        public bool Named { get; set; }

        /// <summary>Roubles for buyable and flea; zero for fitted and owned; null for barter and absent. A
        /// null is "no price exists", never "free".</summary>
        public long? Price { get; set; }

        /// <summary>Absent parts only: the trader and loyalty level that would sell it, if any does.</summary>
        public string Gate { get; set; } = "";
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
        /// the profile payload's own type rather than restating its six counts per row: an item wanted
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
        /// item markers (1.5.0).
        ///
        /// NOT raised for Extent in 1.19.0, although that added a field. The client compares this
        /// number against its own and warns on any difference (QuestDataClient.NoteMarkers), so the
        /// two halves have to move in the same commit; Extent is additive - an older client reads the
        /// unknown field as absent and pins exactly as before - so raising it would buy a log line
        /// saying "update both halves" for players whose halves are fine. The next CHANGE to an
        /// existing field takes 4.</summary>
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

        /// <summary>The map's measured world rectangle and floor bands, or null on a map no v2
        /// harvest has reached. Copied straight from the zone file by MapMarkerPayloadBuilder.Build.
        ///
        /// THE SAME CLASS the harvest arrives as (ZoneHarvestDtos.cs), deliberately: what goes out
        /// is what came in, and a second declaration for the payload side would drift the moment one
        /// of them gained a field - the two halves would then disagree about a rectangle while every
        /// log line on both sides said the map was measured. It is the one DTO in this file carrying
        /// JsonPropertyName attributes, and they spell exactly what the camelCase policy here would
        /// have produced, so this payload's JSON is unchanged by the reuse.
        ///
        /// This is how the client learns which stretch of world a map covers: without it a captured
        /// picture cannot be placed and pins have nothing to be drawn against.</summary>
        public MapExtentDto? Extent { get; set; }
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

    /// <summary>One rectangle of world, in metres, on the XZ plane. The picture a capture produces
    /// covers exactly this, so the client can turn a world position into a pixel by dividing.
    ///
    /// A SEPARATE type from MapExtentDto (ZoneHarvestDtos.cs), which carries the same four corners
    /// plus how they were measured. This one is the geometry only: a captured picture's rectangle is
    /// a statement about the IMAGE - what it was rendered to cover - and it must not gain a source
    /// rank or a sample time that something might then use to prefer one picture over another. The
    /// zone file's extent stays the authority on where the map is; a capture merely records the
    /// rectangle it was taken with, so a mismatch between the two is visible rather than merged.</summary>
    public sealed class MapRectDto
    {
        [JsonPropertyName("minX")] public double MinX { get; set; }
        [JsonPropertyName("minZ")] public double MinZ { get; set; }
        [JsonPropertyName("maxX")] public double MaxX { get; set; }
        [JsonPropertyName("maxZ")] public double MaxZ { get; set; }
    }

    /// <summary>One captured floor: which height band it is, and the picture that was rendered for
    /// it.</summary>
    public sealed class MapCaptureFloorDto
    {
        /// <summary>The same level numbering the zone file's floors use - 0 is the ground the spawns
        /// are on - so a pin already assigned a floor needs no second rule to find its picture.</summary>
        [JsonPropertyName("level")] public int Level { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>The picture's file name, no directory part. REWRITTEN by the server when a set is
        /// stored, to the name the server itself gave the file (<c>&lt;key&gt;-&lt;level&gt;.jpg</c>) -
        /// never the client's own name for it. A file name arriving from a Fika peer is a path, and a
        /// path from a peer is how a stored set escapes its folder.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = "";

        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }

        /// <summary>The height band this floor's camera covered, copied from the zone file's floors so
        /// a picture can be matched to the band whose pins belong on it.</summary>
        [JsonPropertyName("minY")] public float MinY { get; set; }
        [JsonPropertyName("maxY")] public float MaxY { get; set; }
    }

    /// <summary>A place name drawn on the picture - an exfil, a named bot zone - which is what
    /// replaces DynamicMaps' hand-placed labels. World coordinates, never pixels: the client already
    /// knows the rectangle, and a pixel would be wrong the moment the capture resolution changed.</summary>
    public sealed class MapLabelDto
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";

        /// <summary>Where the name came from: "exfil" for an extraction point, "zone" for a bot zone's
        /// cleaned-up name. It decides how prominently the client draws it - an extract wears the
        /// accent and a diamond and is drawn at every zoom, a zone name is white and yields - so
        /// without it on the wire every borrowed map lost its extract marks. NORMALISED by MapStore to
        /// one of those two words, anything else becoming "zone", because it reaches a text mesh on
        /// every other client in the group.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";

        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
    }

    /// <summary>The 3D mesh beside a set's pictures - the map's ground relief and building shells, in
    /// the client's own MapMeshFile format (<c>&lt;key&gt;-mesh.bin</c>), described rather than
    /// carried: the bytes travel on their own route (POST /questtree/maps/mesh) because they are
    /// megabytes of deflated binary and every other reader of this meta parses it in full.
    ///
    /// OPTIONAL, and that is the whole design: a set captured before meshes existed, a set from
    /// DynamicMaps' artwork and a set synced from an older host all have none, and nothing about the
    /// 2D picture depends on it. So this is null on those sets, an older server drops it silently
    /// (which is why no schema version is bumped for it), and an older client ignores it.
    ///
    /// The server does not parse the geometry - it validates the file's HEADER on the way in (see
    /// MapStore's mesh route) and stores the bytes - but it does check the three numbers here that a
    /// reader would otherwise have to trust: <see cref="Sha256"/> against the bytes it was sent,
    /// <see cref="Bytes"/> against their length, and <see cref="File"/> is REWRITTEN to the name the
    /// server itself gave the file, like a floor's.</summary>
    public sealed class MapCaptureMeshDto
    {
        /// <summary>The mesh file's name beside the pictures, <c>&lt;key&gt;-mesh.bin</c>. Rewritten by
        /// the server when a set is stored - a file name from a Fika peer is a path.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = "";

        /// <summary>The file's size in bytes, as stored. What a client uses to say what a download
        /// costs before starting it.</summary>
        [JsonPropertyName("bytes")] public long Bytes { get; set; }

        /// <summary>The mesh format's version (MapMeshFile.Version). Carried so a client can tell
        /// "this host has a mesh I cannot read" from "this host has no mesh" without downloading
        /// megabytes to find out.</summary>
        [JsonPropertyName("version")] public int Version { get; set; }

        /// <summary>Relief cells across every band, and triangles across every building: the two
        /// numbers a log line and a settings row quote. Informational on this half.</summary>
        [JsonPropertyName("cells")] public long Cells { get; set; }

        [JsonPropertyName("triangles")] public long Triangles { get; set; }

        /// <summary>sha256 of the file's bytes, hex, lower case. The one field that makes the mesh
        /// route safe to serve from: the uploader's bytes must hash to this, and a downloader checks
        /// what it received against it before writing it beside a set it will then draw.</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    /// <summary>One OBLIQUE picture of a map: an orthographic render from one side, pitched 45 degrees
    /// down, which the 3D view uses to texture the building walls that face that way. Up to four per
    /// set, one per compass side.
    ///
    /// Everything a reader needs to put a world point on the picture is here, so nothing has to agree
    /// twice: a point p lands at px = (dot(right, p) - originR) x pxPerMetre and
    /// py = height - (dot(up, p) - originU) x pxPerMetre, row 0 at the top. The basis is three unit
    /// vectors in world x/y/z; originR/originU are the lowest projections of the capture box's eight
    /// corners, whose height range is yMin..yMax.
    ///
    /// OPTIONAL, like the mesh and for the same reasons: absent on every set captured before sides
    /// existed, dropped by an older host, ignored by an older client - so no schema version moves for
    /// it. And a side is never worth a map: a side this host cannot use is dropped from the set's
    /// sides and the rest of the set is served (MapStore.DropSide).</summary>
    public sealed class MapCaptureSideDto
    {
        /// <summary>"N", "S", "E" or "W" - the side of the map the camera stood on.</summary>
        [JsonPropertyName("dir")] public string Dir { get; set; } = "";

        /// <summary>The picture's file name. REWRITTEN by the server to <c>&lt;key&gt;-side-&lt;dir&gt;.jpg</c>,
        /// as a floor's is - a name from a peer is a path. On the way in it may still be the capture's own
        /// <c>&lt;key&gt;-side-&lt;dir&gt;.png</c>: the client does not rewrite it, since this server discards
        /// it anyway.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = "";

        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }

        [JsonPropertyName("pxPerMetre")] public float PxPerMetre { get; set; }

        /// <summary>Where the camera looked, as a world-space unit vector.</summary>
        [JsonPropertyName("forward")] public float[]? Forward { get; set; }

        /// <summary>The picture's rightward axis in world space, a unit vector.</summary>
        [JsonPropertyName("right")] public float[]? Right { get; set; }

        /// <summary>The picture's upward axis in world space, a unit vector.</summary>
        [JsonPropertyName("up")] public float[]? Up { get; set; }

        /// <summary>The smallest dot(right, corner) over the capture box's corners, in metres - the
        /// world position of the picture's left edge along <see cref="Right"/>.</summary>
        [JsonPropertyName("originR")] public double OriginR { get; set; }

        /// <summary>The smallest dot(up, corner), in metres - the picture's bottom edge along
        /// <see cref="Up"/>.</summary>
        [JsonPropertyName("originU")] public double OriginU { get; set; }

        /// <summary>The height range of the capture box the origins were taken over.</summary>
        [JsonPropertyName("yMin")] public float YMin { get; set; }
        [JsonPropertyName("yMax")] public float YMax { get; set; }
    }

    /// <summary>One ATLAS page of a map: a 4096 px sheet of the game's own building textures, packed as
    /// tiles, which the 3D view drapes on the buildings by the mesh file's own UVs (stage W). Up to eight per
    /// set, numbered by <see cref="Page"/>.
    ///
    /// OPTIONAL, like the sides and the mesh: absent on older sets, dropped by an older host, ignored by an
    /// older client - no schema version moves for it. And a page is never worth a map: a page this host
    /// cannot use is dropped from the set's atlas and the rest is served, and the buildings drawn from it
    /// fall back in the viewer to the sides and tints they used before pages existed.</summary>
    public sealed class MapCaptureAtlasDto
    {
        /// <summary>The page's file name. REWRITTEN by the server to <c>&lt;key&gt;-atlas-&lt;page&gt;.jpg</c>,
        /// as a floor's is - a name from a peer is a path. On the way in it may still be the capture's own
        /// <c>.png</c>: the client does not rewrite it, since this server discards it anyway.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = "";

        /// <summary>Which page, 0 to 7 - the number the mesh file's per-building ranges refer to.</summary>
        [JsonPropertyName("page")] public int Page { get; set; }

        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }

        /// <summary>How many texture tiles are packed on the page. Informational.</summary>
        [JsonPropertyName("tiles")] public int Tiles { get; set; }

        /// <summary>sha256 of the page file, hex. On the way IN it describes the capture's own PNG, which
        /// the upload re-encodes as a JPEG - so the server does not check it, and REWRITES it when the set is
        /// stored to the hash of the JPEG it serves. From then on it is what a downloader and the packaging
        /// gate hold the page to.</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    /// <summary>Everything about one map's captured picture set except the pictures: exactly the
    /// client's <c>&lt;key&gt;.map.json</c>, field for field.
    ///
    /// The same class is the file on disk, the body of an upload and an entry in the index, and that
    /// is deliberate: the sentence "what the client captured is what the host serves" is only true if
    /// nothing re-describes it on the way. A second declaration for the index side would drift the
    /// moment one of them gained a field, and the drift would show up as a picture drawn against the
    /// wrong rectangle while every log line on both sides said the set was stored.</summary>
    public sealed class MapCaptureMetaDto
    {
        /// <summary>The only capture shape this server reads. The client mirror declares the same
        /// number: change either and change the other in the same commit.</summary>
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }

        /// <summary>The map's internal name ("bigmap"), as GameWorld reports it.</summary>
        [JsonPropertyName("map")] public string Map { get; set; } = "";

        /// <summary>The world rectangle every floor's picture covers.</summary>
        [JsonPropertyName("extent")] public MapRectDto? Extent { get; set; }

        /// <summary>Degrees the picture is rotated relative to the world; 0 in this release, stored so
        /// a later one can differ out loud rather than silently.</summary>
        [JsonPropertyName("rotation")] public float Rotation { get; set; }

        /// <summary>Pixels per metre the capture was rendered at. Checked against each floor's
        /// width and height: the three numbers describe one projection, so disagreement means the
        /// picture does not cover the rectangle it claims.</summary>
        [JsonPropertyName("pxPerMetre")] public float PxPerMetre { get; set; }

        /// <summary>The tile the capture was rendered in, one tile per frame. Informational.</summary>
        [JsonPropertyName("tileSize")] public int TileSize { get; set; }

        /// <summary>When the raid this was captured in ran, ISO-8601 UTC. THE VERSION of a set: the
        /// host accepts a newer one and refuses an older or equal one, and the floors of one upload
        /// are grouped by it while they arrive one post at a time.</summary>
        [JsonPropertyName("capturedAt")] public string CapturedAt { get; set; } = "";

        /// <summary>When the FIRST capture of this set was taken, which is what the credit line under
        /// the map means by the date - a set is built over several raids and
        /// <see cref="CapturedAt"/> is only the latest of them. Carried but never ranked on: the
        /// version of a set is <see cref="CapturedAt"/> and nothing else. Empty when the capturing
        /// client wrote no such field, and then the two are the same instant.</summary>
        [JsonPropertyName("firstCapturedAt")] public string FirstCapturedAt { get; set; } = "";

        /// <summary>How many captures are merged into this set; 1 for a fresh one. The number a player
        /// watches go up while they fill in a map's holes, so it has to survive the host or every
        /// borrowed map claims to be somebody's first attempt. Carried, bounded, and read by nothing
        /// here.</summary>
        [JsonPropertyName("captures")] public int Captures { get; set; }

        /// <summary>The Quest Tracker version that took the capture, for the credits line and for
        /// telling a re-capture after a game update from the picture it replaced.</summary>
        [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "";

        /// <summary>"day" or "night" - recorded, never changed: a capture takes the raid's light as it
        /// finds it, and a night Factory picture is still a Factory picture.</summary>
        [JsonPropertyName("timeOfDay")] public string TimeOfDay { get; set; } = "";

        [JsonPropertyName("floors")] public List<MapCaptureFloorDto> Floors { get; set; } = new();

        [JsonPropertyName("labels")] public List<MapLabelDto> Labels { get; set; } = new();

        /// <summary>The 3D mesh this set carries, or null when it has none - see
        /// <see cref="MapCaptureMeshDto"/>. NULL IS THE ORDINARY CASE, not a fault: nothing about the
        /// pictures depends on it, which is what lets it be added without a schema bump.
        ///
        /// When it is set, the set is not served until the mesh named here has arrived as well (the
        /// mesh route), so the meta a client reads never names a file the host does not hold.</summary>
        [JsonPropertyName("mesh")] public MapCaptureMeshDto? Mesh { get; set; }

        /// <summary>The oblique side pictures this set carries, or null/empty for none - see
        /// <see cref="MapCaptureSideDto"/>. When set, the set is not served until every side named here
        /// has arrived or been dropped, so a meta never names a side picture the host does not hold.</summary>
        [JsonPropertyName("sides")] public List<MapCaptureSideDto>? Sides { get; set; }

        /// <summary>The atlas pages this set carries, or null/empty for none - see
        /// <see cref="MapCaptureAtlasDto"/>. When set, the set is not served until every page named here has
        /// arrived or been dropped, as for the sides.</summary>
        [JsonPropertyName("atlas")] public List<MapCaptureAtlasDto>? Atlas { get; set; }
    }

    /// <summary>The body of POST /questtree/maps/upload: ONE floor's picture, with the whole set's
    /// meta repeated on every post.
    ///
    /// One floor per post because a four-floor map at 2.5 MB a floor is a 10 MB body, and the meta
    /// repeats because that is what lets the host group the posts without a session: the floors of one
    /// capture share a CapturedAt, and the set completes when every level the meta names has arrived.
    /// A post whose meta names a different CapturedAt is a different set and never completes this one.
    ///
    /// Named with explicit JsonPropertyName because SPT's JsonUtil deserializes request bodies with
    /// no naming policy - without these a camelCase body lands in no property at all, silently. The
    /// client mirrors this shape in QuestGraph/MapCaptureDto.cs.</summary>
    public sealed class MapUploadRequest : IRequestData
    {
        /// <summary>The newest upload shape this server understands. An upload claiming a newer shape
        /// is refused rather than stored: the deserializer drops fields this build cannot see, and a
        /// half-understood capture written into maps\ would be served to every other client as
        /// complete. The client mirror declares the same number.</summary>
        public const int SupportedSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }

        /// <summary>The map's internal name, as GameWorld reports it. Folded to its canonical name by
        /// the store, so a Factory night capture serves Factory day as well.</summary>
        [JsonPropertyName("map")] public string Map { get; set; } = "";

        [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = "";

        [JsonPropertyName("meta")] public MapCaptureMetaDto? Meta { get; set; }

        /// <summary>Which of the meta's floors this post carries. Must be one of them; nothing else
        /// says which picture these bytes are. IGNORED when <see cref="Side"/> is set.</summary>
        [JsonPropertyName("level")] public int Level { get; set; }

        /// <summary>"N", "S", "E" or "W" when this post carries one of the capture's oblique SIDE
        /// pictures rather than a floor; null or empty for a floor. A side rides this route rather than
        /// one of its own because it IS a picture - the same format, the same magic, the same size cap
        /// and the same staging - and differs from a floor only in what it is completed against: the
        /// meta's <c>sides</c> instead of its levels. See MapStore.Accept.
        ///
        /// A client sends a side with a level no floor can have (int.MinValue), so a host that
        /// predates this field reads the post as a floor it does not know and refuses it, rather than
        /// storing a side picture over floor 0.</summary>
        [JsonPropertyName("side")] public string? Side { get; set; }

        /// <summary>The ATLAS page this post carries, 0 to 7; null for a floor or a side. Rides this route
        /// for the side's reason - it is a JPEG picture - and with the side's level (int.MinValue), so a host
        /// that predates this field reads it as a floor it does not know and refuses it rather than filing a
        /// texture sheet as floor 0. Its size cap is its own (MapStore.MaxAtlasPageBytes): a page is sent at
        /// its full 4096 px.</summary>
        [JsonPropertyName("atlas")] public int? Atlas { get; set; }

        /// <summary>"jpg" or "png", and the bytes must actually start with that format's magic
        /// numbers - the extension a client claims decides nothing. A side or a page must be "jpg".</summary>
        [JsonPropertyName("format")] public string Format { get; set; } = "";

        /// <summary>The picture. For a side, EMPTY is meaningful: it is how a client that could not
        /// encode a side it had already named tells the host to drop it, so the set does not wait for
        /// a picture that will never come.</summary>
        [JsonPropertyName("imageBase64")] public string ImageBase64 { get; set; } = "";
    }

    /// <summary>What POST /questtree/maps/upload answers.</summary>
    public sealed class MapUploadResponse
    {
        /// <summary>"stored" (this floor is held, the set is waiting on others), "complete" (the set
        /// is now the one this host serves), "declined" (this host does not take uploads at all) or
        /// "rejected" (this post will never be accepted as sent).
        ///
        /// Four outcomes rather than a bool because the client says a different sentence for each:
        /// "declined" is the host's settled policy and stops it trying again this session, "rejected"
        /// names a fault in the capture, and "stored" is progress.</summary>
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";

        /// <summary>Why, in words the client prints verbatim. Empty on success.</summary>
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";

        /// <summary>How many floors of this set the host is holding, so the client can show progress
        /// and tell a lost post from a slow one.</summary>
        [JsonPropertyName("floorsHeld")] public int FloorsHeld { get; set; }

        /// <summary>The refusal as a code the client branches on, "" when there is none - the reason stays the
        /// words a person reads (review F02). Always written, so a client can tell "no code" from a host too old
        /// to send one (which omits the field).</summary>
        [JsonPropertyName("code")] public string Code { get; set; } = "";

        /// <summary>On an answer that COMPLETES a set whose meta declared a mesh: whether the set is served with
        /// it. Null otherwise.</summary>
        [JsonPropertyName("meshKept")] public bool? MeshKept { get; set; }

        /// <summary>On a side or atlas-page post's answer: whether that piece was dropped and the set goes on
        /// without it. Null for a floor post.</summary>
        [JsonPropertyName("dropped")] public bool? Dropped { get; set; }

        /// <summary>The level a floor post named is not in its own meta - what a host from before sides answers
        /// a side post with.</summary>
        public const string CodeUnknownLevel = "unknown-level";
    }

    /// <summary>One complete set the host holds.</summary>
    public sealed class MapIndexEntryDto
    {
        /// <summary>The CANONICAL map name - factory4_night folded to factory4_day, Sandbox_high to
        /// Sandbox - because the two share a scene and therefore a picture. A client asking for either
        /// name gets this set.</summary>
        [JsonPropertyName("map")] public string Map { get; set; } = "";

        /// <summary>sha256 over the stored meta's bytes and every floor's bytes in level order: the
        /// one value a client compares against what it already downloaded. It changes when any part of
        /// the set changes and not otherwise, so a cached set is never re-downloaded and a replaced one
        /// always is. Computed by the host, never sent by a client.</summary>
        [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";

        [JsonPropertyName("capturedAt")] public string CapturedAt { get; set; } = "";

        /// <summary>The pictures' total size on disk, plus the mesh when there is one, so a client can
        /// say what a download will cost before starting it.</summary>
        [JsonPropertyName("bytes")] public long Bytes { get; set; }

        [JsonPropertyName("meta")] public MapCaptureMetaDto? Meta { get; set; }

        /// <summary>The 3D mesh this set carries, or null. The SAME block as
        /// <see cref="MapCaptureMetaDto.Mesh"/>, repeated here for one reason: this is the level a
        /// client decides at. It walks the index, compares stamps and then decides per map whether to
        /// ask for a mesh at all, and a field it has to reach into the meta for is a field a later
        /// index shape could drop while the meta kept it.</summary>
        [JsonPropertyName("mesh")] public MapCaptureMeshDto? Mesh { get; set; }
    }

    /// <summary>What GET /questtree/maps answers: everything a client needs to decide what to
    /// download, and nothing it has to download to find out.</summary>
    public sealed class MapIndexDto
    {
        public const int CurrentSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Whether this host takes uploads at all. Sent so a client can say "this host keeps
        /// its own pictures" once instead of posting a 10 MB set to be refused floor by floor.</summary>
        [JsonPropertyName("acceptsUploads")] public bool AcceptsUploads { get; set; }

        [JsonPropertyName("maps")] public List<MapIndexEntryDto> Maps { get; set; } = new();
    }

    /// <summary>The body of POST /questtree/maps/image: one floor of one map.
    ///
    /// A POST with a body rather than a path such as /questtree/maps/bigmap/0 because SPT's static
    /// routers match a URL exactly - a path parameter needs a dynamic router, whose prefix matching
    /// would then answer for every URL starting with ours.</summary>
    public sealed class MapImageRequest : IRequestData
    {
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("level")] public int Level { get; set; }

        /// <summary>"N", "S", "E" or "W" to ask for that side picture instead of a floor; null or empty
        /// for the floor at <see cref="Level"/>. An older host ignores it and answers with a floor, which
        /// is why the client only ever asks for a side the index said the set has.</summary>
        [JsonPropertyName("side")] public string? Side { get; set; }

        /// <summary>An atlas page number to ask for that page instead of a floor; null for a floor or side.</summary>
        [JsonPropertyName("atlas")] public int? Atlas { get; set; }
    }

    /// <summary>One floor's picture, base64-encoded.
    ///
    /// ImageBase64 and Stamp are both EMPTY when the host has no such picture, rather than an error:
    /// asking for a floor is how a client finds out, and every caller already draws the bounds-only
    /// backdrop when there is no picture.</summary>
    public sealed class MapImageDto
    {
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("level")] public int Level { get; set; }

        /// <summary>The whole SET's stamp, not this floor's - it is what the client stores beside the
        /// downloaded files and compares against the index, and a per-floor value would let a
        /// half-downloaded set look current.</summary>
        [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";

        [JsonPropertyName("format")] public string Format { get; set; } = "";

        [JsonPropertyName("imageBase64")] public string ImageBase64 { get; set; } = "";
    }

    /// <summary>The body of POST /questtree/maps/mesh: one capture's whole mesh file.
    ///
    /// A ROUTE OF ITS OWN rather than a "floor" of the picture upload, and not because the bytes are
    /// bigger: the picture route checks image magic, caps a body at 2.5 MB and completes a set when
    /// every LEVEL the meta names has arrived. A mesh is none of those things - it has no level, it is
    /// deflated binary, and it is up to 48 MB (in parts - see Parts) - so riding it on that route would mean loosening every
    /// one of those checks for every picture as well.
    ///
    /// No meta here, unlike the picture route: the mesh belongs to the capture identified by
    /// <see cref="CapturedAt"/>, whose meta arrived with its floors and is what says a mesh is
    /// expected at all. So this route can neither start a set nor change one - it can only complete
    /// the set the floors already staged, which is the smallest thing it could be allowed to do.</summary>
    public sealed class MapMeshUploadRequest : IRequestData
    {
        /// <summary>The newest mesh upload shape this server understands. Deliberately its own number
        /// and deliberately still 1: the mesh is an OPTIONAL addition, so nothing about it may refuse
        /// an older client's pictures or an older host's sets.</summary>
        public const int SupportedSchemaVersion = 1;

        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }

        [JsonPropertyName("map")] public string Map { get; set; } = "";

        [JsonPropertyName("clientVersion")] public string ClientVersion { get; set; } = "";

        /// <summary>The capture this mesh belongs to - the same capturedAt its floors carried, which is
        /// what pairs the two on a host answering several clients at once.</summary>
        [JsonPropertyName("capturedAt")] public string CapturedAt { get; set; } = "";

        /// <summary>sha256 of the bytes, hex. Checked against the bytes themselves AND against what the
        /// staged capture's meta said its mesh would be: a mesh whose hash matches neither is not this
        /// capture's mesh.</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

        /// <summary>The decoded length the sender claims - of the WHOLE mesh, when it comes in parts. Checked
        /// against what actually decodes (or what the parts add up to) - two numbers from one machine that
        /// disagree mean the file was not read whole.</summary>
        [JsonPropertyName("bytes")] public long Bytes { get; set; }

        /// <summary>Which part of the mesh <see cref="DataBase64"/> carries, from 0, when it comes in
        /// <see cref="Parts"/> parts.</summary>
        [JsonPropertyName("part")] public int Part { get; set; }

        /// <summary>How many parts the whole mesh comes in; 0 or 1 for a mesh in one post. Several, because
        /// one HTTP body to a stock SPT host cannot carry more than 30,000,000 bytes (Kestrel's default,
        /// measured), and a stage V mesh can be 48 MB: the client sends 16 MiB parts, and the host joins
        /// them and checks the whole exactly as it checks a mesh that came in one post. A host from before
        /// stage V never SEES a part: its meta check drops any mesh past its own 12 MB cap when the floors
        /// arrive, so the set completes flat on its floors (or sides) before the client would post one - and
        /// the client, holding a mesh the host then did not ask for, says so at Info
        /// (MapTransfer.SayIfMeshWasNotKept).</summary>
        [JsonPropertyName("parts")] public int Parts { get; set; }

        /// <summary>The mesh, or this post's part of it.</summary>
        [JsonPropertyName("dataBase64")] public string DataBase64 { get; set; } = "";
    }

    /// <summary>What POST /questtree/maps/mesh answers.
    ///
    /// A bool and a reason rather than the picture route's four outcomes: a mesh is one post, so there
    /// is no "send the next one", and a host that declines uploads or refuses this file says so in the
    /// reason. <see cref="Accepted"/> true with the set still waiting on floors is a normal answer -
    /// the mesh is held, exactly as a floor is.</summary>
    public sealed class MapMeshUploadResponse
    {
        [JsonPropertyName("accepted")] public bool Accepted { get; set; }

        /// <summary>Whether the host now SERVES a set for this capture (or a newer one), with or without
        /// this mesh - i.e. whether anything of the capture is still being held waiting. The field that
        /// tells the client's three sentences apart: accepted and served (the 3D map is shared), refused
        /// but served (the mesh could never be used, so the pictures went up flat - or the host already
        /// had this capture), and refused and NOT served (the floors wait on the host and expire).
        /// False from an older host that never sets it, which is the safe reading.</summary>
        [JsonPropertyName("served")] public bool Served { get; set; }

        /// <summary>Why not, or what is still missing. Printed by the client as given.</summary>
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";

        /// <summary>On a part the host is holding: how many parts it holds and how many the mesh comes in. Both 0
        /// on every other answer, and from a host too old to send them (review F02).</summary>
        [JsonPropertyName("partsHeld")] public int PartsHeld { get; set; }

        /// <summary>How many parts the mesh comes in - see <see cref="PartsHeld"/>.</summary>
        [JsonPropertyName("parts")] public int Parts { get; set; }
    }

    /// <summary>Which map's mesh to send down. The body of POST /questtree/maps/meshfile - a POST for
    /// the reason <see cref="MapImageRequest"/> gives.</summary>
    public sealed class MapMeshRequest : IRequestData
    {
        [JsonPropertyName("map")] public string Map { get; set; } = "";
    }

    /// <summary>One map's mesh file, base64-encoded.
    ///
    /// EMPTY - no data, no sha, no stamp - when this host has no mesh for that map, which is the
    /// ordinary answer rather than an error: asking is how a client finds out, and every caller draws
    /// the 2D picture when there is no mesh to drape it on.</summary>
    public sealed class MapMeshDto
    {
        [JsonPropertyName("map")] public string Map { get; set; } = "";

        /// <summary>The whole SET's stamp, as <see cref="MapImageDto.Stamp"/> - so a client that
        /// fetched the pictures of one set and the mesh of another can tell.</summary>
        [JsonPropertyName("stamp")] public string Stamp { get; set; } = "";

        /// <summary>sha256 of the bytes below, hex. The client checks it against the index entry's
        /// before writing the file: this is the one payload here that is never looked at by a human,
        /// so nothing else would notice it arriving corrupt.</summary>
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

        [JsonPropertyName("bytes")] public long Bytes { get; set; }

        [JsonPropertyName("dataBase64")] public string DataBase64 { get; set; } = "";
    }
}
