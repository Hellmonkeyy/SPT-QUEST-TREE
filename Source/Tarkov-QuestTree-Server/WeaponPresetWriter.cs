using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.PresetBuild;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;

namespace QuestTreeServer
{
    /// <summary>Writes a solved build into the player's saved weapon builds, so the game's own modding
    /// screen can load it onto a gun in one click.
    ///
    /// The mod worked out the build and printed a parts list; fitting it meant reading that list back
    /// and forth at the workbench. The game already has the mechanism that makes this unnecessary, and
    /// it does something we were never going to do well ourselves: its preset screen offers to BUY the
    /// parts you are missing. So the build stays ours and the delivery becomes the game's, which also
    /// avoids a server-side purchase that would desync the profile.
    ///
    /// It goes through SPT's own BuildController rather than writing the profile directly, so the
    /// profile is mutated the way SPT mutates it. Two things this used to say and should not:
    ///
    /// Not "so ids are minted" - SaveWeaponBuild stores `Id = request.Id` verbatim and mints nothing, so
    /// the id is ours to choose; see the note in Save about choosing it consistently. And not
    /// "de-duplication exactly as the game does it" - SPT de-duplicates on `Name == || Id ==`, while the
    /// GAME de-duplicates on id alone. The name half is the half this mod leans on, and the difference
    /// is why ExistingId has to check both.</summary>
    [Injectable(InjectionType.Singleton)]
    public class WeaponPresetWriter(
        ISptLogger<WeaponPresetWriter> logger,
        BuildController buildController,
        SaveServer saveServer,
        TemplateTable templateTable)
    {
        /// <summary>Every preset this mod writes is prefixed, and that is a data-safety measure rather
        /// than a badge.
        ///
        /// BuildController.SaveWeaponBuild de-duplicates by NAME or id, and this profile already
        /// contains a build the player made called "Gunsmith". Saving one under that name would have
        /// silently deleted theirs. The prefix also means re-saving replaces OUR previous preset for a
        /// quest rather than piling up, and makes the mod's presets obvious in a list they own.</summary>
        private const string Prefix = "QT: ";

        /// <summary>The preset's name: the prefix, the quest, and THE WEAPON.
        ///
        /// The weapon is not decoration. SaveWeaponBuild de-duplicates on name, and a quest can ask for
        /// more than one - Gunsmith - Part 21 wants two, Old Friend's Request wants three - so naming a
        /// preset after the quest alone meant saving the second weapon silently deleted the first, while
        /// the panel said "it is in the game's build list now". The build key is per weapon; the name has
        /// to be too, or re-saving one weapon and replacing another are the same operation.
        ///
        /// Only appended when it adds something: for the overwhelming majority of quests, which name one
        /// weapon, "QT: Gunsmith - Part 6" reads better than the same with a rifle bolted on. So the
        /// weapon is included whenever it is known, and the QUEST alone is never the whole name for a
        /// multi-weapon quest, which is the case that mattered.</summary>
        private static string NameFor(string questName, string weaponName)
        {
            var quest = string.IsNullOrWhiteSpace(questName) ? "build" : questName.Trim();
            var gun = weaponName?.Trim();

            return string.IsNullOrEmpty(gun) ? Prefix + quest : $"{Prefix}{quest} - {gun}";
        }

        public sealed class Outcome
        {
            public bool Saved { get; init; }
            public string Name { get; init; } = "";
            public string Reason { get; init; } = "";

            /// <summary>The preset's id, and the items as actually written.
            ///
            /// The items really are read back: ReplaceIDs inside SaveWeaponBuild mutates the list in
            /// place, so after the call `Items` holds the ids that were written.
            ///
            /// The ID is not, and this comment used to claim it was. SaveWeaponBuild stores
            /// `Id = request.Id` verbatim - it mints nothing - so the id here is the one proposed, and it
            /// matches the profile because we chose it rather than because we re-read it. Which id to
            /// propose is the whole of the fix in Save; see the note there.</summary>
            public MongoId Id { get; init; }
            public IReadOnlyList<Item> Items { get; init; } = Array.Empty<Item>();

            public static Outcome No(string reason) => new() { Reason = reason };
        }

        /// <summary>Turn one solved build into a saved preset.</summary>
        public async Task<Outcome> Save(
            MongoId sessionId, string questName, string weaponName, MongoId weapon,
            IReadOnlyList<WeaponSolver.FittedPart> tree)
        {
            if (tree == null || tree.Count == 0) return Outcome.No("there is no build to save yet");

            var items = Flatten(weapon, tree, out var why);
            if (items == null) return Outcome.No(why);

            var name = NameFor(questName, weaponName);

            // REUSE the id a preset of this name already has, and only mint one for a genuinely new name.
            //
            // NOT the fix for the empty BUILD SELECTION window, and it was written believing it was. That
            // crash is entirely client-side - WeaponPresetLoader inserted a handbook node without the
            // dictionary entry its id resolves through - and it is fixed there, with OverrideWeaponBuild.
            // Re-read this file's history before treating a stable id as the cure for anything.
            //
            // Kept because it is independently right. SaveWeaponBuild matches on
            // `build.Name == request.Name || build.Id == request.Id` and, on a match, removes the old
            // build and adds the new one under the id we passed - so a fresh id per save meant re-saving
            // the same quest's preset changed that preset's identity every time. Nothing in the profile
            // broke, but every consumer of an id got a new one for what the player sees as one preset:
            // the in-memory storage keyed it afresh, the handbook node was rebuilt under a new key, and
            // anything that had remembered the old id was left holding a stale one.
            //
            // Re-saving a preset is an edit of that preset, not a different preset. The id should say so.
            var existing = ExistingId(sessionId, name);
            var presetId = existing ?? new MongoId();

            try
            {
                // ReplaceIDs inside this call mutates the list in place and re-parents as it goes, so
                // after it returns `items` holds the ids that were actually written - which is what the
                // client needs to insert the same build into the game's in-memory list.
                buildController.SaveWeaponBuild(sessionId, new PresetBuildActionRequestData
                {
                    Id = presetId,
                    Name = name,
                    Root = items[0].Id,
                    Items = items
                });

                // The controller only mutates the in-memory profile. SPT's own autosave would land it
                // within a minute, but a player who presses a button and then quits should not lose it.
                await saveServer.SaveProfileAsync(sessionId).ConfigureAwait(false);

                // Which of the two it was, because it is the one thing about a save that the server can
                // see and nobody else records. It is NOT evidence about the preset window: an id change
                // strands nothing, since OverrideWeaponBuild writes the dictionary before it makes the
                // node and never drops the old entry, so both ids keep resolving. An earlier version of
                // this comment claimed the opposite and would have sent the next reader looking in the
                // one place the crash cannot be.
                logger.Info(
                    $"Quest Tracker: saved the weapon preset '{name}' for {sessionId} - " +
                    (existing == null ? $"new, id {presetId}" : $"replaced in place, keeping id {presetId}") +
                    $", {items.Count} item(s).");

                return new Outcome { Saved = true, Name = name, Id = presetId, Items = items };
            }
            catch (Exception ex)
            {
                // Never throw out of this. It is the player's save file, and a failed preset is worth
                // a sentence on screen rather than a broken request.
                logger.Error($"Quest Tracker: could not save the weapon preset '{name}': {ex}");

                return Outcome.No("the server could not write the preset - see the server log");
            }
        }

        /// <summary>The id the profile already holds for a preset of this name, or null when there is
        /// none and a new one has to be minted.
        ///
        /// Matched on NAME, because the name is the half of SaveWeaponBuild's test we can answer before
        /// choosing an id. Its full test is `build.Name == request.Name || build.Id == request.Id`, and
        /// the id half is circular here - it is the very thing being decided - so the rule this has to
        /// satisfy is "find whatever SaveWeaponBuild would replace", and on a name match that is the
        /// FIRST one. Ordinal, since the name is one we built from a fixed prefix and the game compares
        /// it with `==`, which is ordinal too.
        ///
        /// Never throws. A profile that cannot be read is answered as "no existing preset", which mints a
        /// new id and behaves exactly as this did before - the old behaviour is the safe fallback rather
        /// than a reason to fail the save.</summary>
        private MongoId? ExistingId(MongoId sessionId, string name)
        {
            try
            {
                var builds = saveServer.GetProfile(sessionId)?.UserBuildData?.WeaponBuilds;

                if (builds == null) return null;

                foreach (var build in builds)
                {
                    if (!string.Equals(build?.Name, name, StringComparison.Ordinal)) continue;

                    // An EMPTY id is not an id to reuse. UserBuild.Id is a non-nullable struct, so a
                    // profile can hold `default`, and `existing ?? new MongoId()` would keep it - the ??
                    // fires on null, not on empty. SaveWeaponBuild then writes it verbatim, MongoId
                    // renders it as "", and next session the game parses the build list through
                    // `new MongoID(string)`, which throws ArgumentOutOfRangeException on anything not 24
                    // characters - inside Newtonsoft deserialising the WHOLE response. That is the "takes
                    // down the player's entire build list" failure Flatten below refuses to risk, reached
                    // by a different road. (This one really is inside the parse. The DUPLICATE-ID failure
                    // below is not: BuildsResponse.WeaponBuilds is a List, so Newtonsoft never sees a key
                    // collision - the throw comes afterwards, from ToDictionary in RequestBuilds.)
                    //
                    // RETURN, not continue, and the difference is a third road to the same disaster. This
                    // scan has to find whatever SaveWeaponBuild would REPLACE, and that is its FIRST name
                    // match - it has no empty-id check of its own. Skipping on to a later match and
                    // returning ITS id means SPT removes the first build and adds ours under the second's
                    // id, leaving two builds sharing one id; the game then deserialises the list into a
                    // Dictionary<MongoID, WeaponBuild>, hits the duplicate key, and throws while parsing
                    // the whole response. Stopping here mints a fresh id and lets SaveWeaponBuild replace
                    // the empty-id entry by name, which is the outcome that keeps the profile consistent.
                    if (build!.Id.IsEmpty) return null;

                    // AND NOT AN ID SOMETHING ELSE ALSO HOLDS, or reusing it destroys the player's own
                    // build. SaveWeaponBuild matches `Name == request.Name || Id == request.Id` and takes
                    // the FIRST hit: propose an id that a differently-named build carries and it matches
                    // THAT one, on the id half, before it ever reaches ours. It removes their build,
                    // appends ours under the same id, and the profile ends up holding two builds with one
                    // id - which next session makes RequestBuilds' ToDictionary(x => x.Id) throw
                    // ArgumentException and takes the whole build list with it.
                    //
                    // Minting fresh instead costs nothing: SaveWeaponBuild still finds ours by NAME and
                    // replaces it. This only declines to reuse an id, never to save.
                    foreach (var other in builds)
                        if (other != null
                            && other.Id == build.Id
                            && !string.Equals(other.Name, name, StringComparison.Ordinal))
                            return null;

                    return build.Id;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The solver's tree as the flat, parented item list a saved build wants.
        ///
        /// Returns null rather than a half-built list when anything does not add up, because of what
        /// the game does with a bad one: EFT.UI.Builds.WeaponBuild's constructor indexes the rebuilt
        /// dictionary by the root id WITHOUT checking it is there, inside Newtonsoft deserialisation of
        /// the whole builds response. A root that names no item does not break one preset, it plausibly
        /// takes down the player's entire build list. Nothing else here validates - not the save, and
        /// not the client, which loads parts with AddWithoutRestrictions and silently drops what it
        /// cannot place - so these checks are the only ones there are.</summary>
        private List<Item>? Flatten(MongoId weapon, IReadOnlyList<WeaponSolver.FittedPart> tree, out string why)
        {
            why = "";

            var templates = templateTable.Items;

            if (templates == null || !templates.ContainsKey(weapon))
            {
                why = "this install has no such weapon template";
                return null;
            }

            var items = new List<Item>(tree.Count + 1);

            // The weapon FIRST and with no parent or slot of its own: SaveWeaponBuild takes Root from
            // Items[0], and the game expects the root item to be unparented.
            var rootId = new MongoId();

            items.Add(new Item
            {
                Id = rootId,
                Template = weapon,
                Upd = new Upd
                {
                    SpawnedInSession = true,
                    Repairable = new UpdRepairable { Durability = 100, MaxDurability = 100 }
                }
            });

            // Parent-before-child ordering is guaranteed by the solver's own walk, so one forward pass
            // is enough: a part's parent always already has an id by the time it is reached.
            var ids = new MongoId[tree.Count];

            for (var i = 0; i < tree.Count; i++)
            {
                var part = tree[i];

                if (!templates.ContainsKey(part.Template))
                {
                    why = "the build uses a part this install does not have";
                    return null;
                }

                if (string.IsNullOrEmpty(part.SlotName))
                {
                    why = "a part in the build does not say which slot it goes in";
                    return null;
                }

                if (part.Parent >= i)
                {
                    why = "the build's parts are not in parent-before-child order";
                    return null;
                }

                ids[i] = new MongoId();

                items.Add(new Item
                {
                    Id = ids[i],
                    Template = part.Template,
                    ParentId = part.Parent < 0 ? rootId.ToString() : ids[part.Parent].ToString(),
                    SlotId = part.SlotName,
                    Upd = new Upd { SpawnedInSession = true }
                });
            }

            return items;
        }
    }
}
