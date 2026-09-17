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
        /// <summary>1: first version.</summary>
        public int SchemaVersion { get; set; } = 1;

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
    }

    /// <summary>One item of a saved preset, in the shape the GAME's own build type wants, so the
    /// client can hand it straight to WeaponBuildsStorage without rebuilding the tree itself.</summary>
    public sealed class PresetItemDto
    {
        public string Id { get; set; } = "";
        public string Tpl { get; set; } = "";

        /// <summary>Empty for the weapon itself, which has no parent.</summary>
        public string ParentId { get; set; } = "";

        /// <summary>Empty for the weapon itself, which sits in no slot.</summary>
        public string SlotId { get; set; } = "";
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
