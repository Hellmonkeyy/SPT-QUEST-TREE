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
    /// It goes through SPT's own BuildController rather than writing the profile directly, so ids are
    /// minted and de-duplication happens exactly as the game does it.</summary>
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

        public sealed class Outcome
        {
            public bool Saved { get; init; }
            public string Name { get; init; } = "";
            public string Reason { get; init; } = "";

            /// <summary>The preset's id and the items as actually written - SPT mints fresh ids inside
            /// SaveWeaponBuild, so these are read back after the call rather than the ones proposed.</summary>
            public MongoId Id { get; init; }
            public IReadOnlyList<Item> Items { get; init; } = Array.Empty<Item>();

            public static Outcome No(string reason) => new() { Reason = reason };
        }

        /// <summary>Turn one solved build into a saved preset.</summary>
        public async Task<Outcome> Save(
            MongoId sessionId, string questName, MongoId weapon, IReadOnlyList<WeaponSolver.FittedPart> tree)
        {
            if (tree == null || tree.Count == 0) return Outcome.No("there is no build to save yet");

            var items = Flatten(weapon, tree, out var why);
            if (items == null) return Outcome.No(why);

            var name = Prefix + (string.IsNullOrWhiteSpace(questName) ? "build" : questName.Trim());

            var presetId = new MongoId();

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

                logger.Info($"Quest Tracker: saved the weapon preset '{name}' for {sessionId}.");

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
