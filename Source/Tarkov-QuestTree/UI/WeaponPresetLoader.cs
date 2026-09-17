using System;
using EFT;
using EFT.UI.Builds;
using Newtonsoft.Json;
using QuestTree.QuestGraph;

namespace QuestTree.UI
{
    /// <summary>Puts a freshly saved preset into the game's in-memory build list, so it appears without
    /// a trip back to profile select.
    ///
    /// The game asks the server for its builds ONCE, at session start, and caches the answer. Nothing
    /// asks again while you play - there is no refresh call on the session, only the storage itself -
    /// so a preset written mid-session is real in the profile and invisible on screen. Pressing a
    /// button and being told to restart is not an answer.
    ///
    /// So the server echoes back the build exactly as it wrote it, and this inserts the same build into
    /// the storage the game is already reading from. The client's copy and the profile therefore agree
    /// by construction rather than by hope.
    ///
    /// "With the ids SPT minted rather than the ones we proposed" stood here for a long time and is
    /// only half true. The ITEM and ROOT ids are SPT's, minted by ReplaceIDs inside SaveWeaponBuild. The
    /// PRESET id never was: SaveWeaponBuild stores `Id = request.Id` verbatim, so it is one the server
    /// chose - deliberately the id an earlier save already used, where there is one.
    ///
    /// The insert is a convenience, not the record: what makes a preset durable is the profile write.
    /// But it survives more than it looks. Handbook.UpdateProfile does rebuild WeaponNodesWithParent
    /// from scratch and discard every node added mid-session - and its only caller follows with Init(),
    /// whose AddItemPresets re-inserts a node for every entry in WeaponBuilds. Since OverrideWeaponBuild
    /// put ours in that dictionary, the node comes back on its own.</summary>
    internal static class WeaponPresetLoader
    {
        /// <summary>The live session, supplied by the panel that owns it. Static because this is called
        /// from a click handler deep in a view that is rebuilt on every repaint.</summary>
        internal static Func<IEftSession> Session;

        /// <summary>Insert the saved build. Returns false when it could not be done, which is not a
        /// failure worth shouting about - the preset is already safely in the profile, and the only
        /// cost is that it will not be visible until the game next asks for its builds.
        ///
        /// Every false that means something went wrong says why, and that is the lesson of the bug
        /// this comment outlived. FOUR of these guards used to return silently, so the only reason the
        /// real failure was diagnosable at all is that it happened to land in the one branch that
        /// logged. "Not worth shouting about" is an argument for Warning over Error, never for saying
        /// nothing: the player has already been told to go back to profile select, and the log is the
        /// only place the reason can live.
        ///
        /// The first guard is the exception and stays silent on purpose. !saved.Saved means the SERVER
        /// declined and already supplied a reason the panel is showing; it is unreachable from the one
        /// call site, which tests the same thing first.</summary>
        internal static bool Insert(QuestDataClient.SavePresetResult saved)
        {
            if (saved == null || !saved.Saved) return false;

            if (string.IsNullOrEmpty(saved.ItemsJson)) return Gave("the server sent no items back");

            if (string.IsNullOrEmpty(saved.Root) || string.IsNullOrEmpty(saved.Id))
                return Gave("the server sent no preset id");

            try
            {
                var storage = Session?.Invoke()?.WeaponBuildsStorage;
                if (storage == null) return Gave("there is no weapon build storage on this session");

                // Deserialised, never hand-built. The fields we could not fill by hand - upd and
                // location, both UnparsedData wrapping a raw JToken - are exactly the ones the build
                // screen needs, and only Newtonsoft can fill them.
                var items = JsonConvert.DeserializeObject<JsonType.FlatItem[]>(saved.ItemsJson);

                if (items == null || items.Length == 0) return Gave("the items did not deserialise");

                // De-duplication is by ID here, and that is a real change from what this used to do.
                //
                // It used to find the same-named build and remove it first, because a repeat save would
                // otherwise leave two rows with one name in a list that does not de-duplicate. That is
                // gone with the remove, and OverrideWeaponBuild replaces on id and never on name - so
                // one name meaning one row now rests entirely on the SERVER reusing a preset's id
                // instead of minting a new one each save (WeaponPresetWriter.ExistingId).
                //
                // Where that guarantee does not hold - a profile edited between sessions, another mod
                // writing a build under our name - the player gets two identically named rows rather
                // than a crash. A tolerable failure, and a visible one, which the old arrangement's
                // network delete was not.

                // OverrideWeaponBuild is the call the GAME uses to save a build, and it is the whole of
                // this fix. InsertBuild alone was wrong in a way nothing here could see:
                //
                //   public void InsertBuild(WeaponBuild build)   // decompiled
                //   {
                //       HandbookNode node = NodeFromBuild(build);        // Data.Id = build.Id
                //       _handbook.WeaponNodesWithParent.AddVirtual(node.Data.Id, node);
                //       ... reparent ...
                //   }                                            // WeaponBuilds is never touched
                //
                // It adds a handbook NODE and never writes the WeaponBuilds dictionary the node's id is
                // supposed to resolve through. Only the storage's constructor, AddItemPresets and
                // OverrideWeaponBuild populate that dictionary. So every preset this mod inserted left a
                // node carrying an id the dictionary had never heard of.
                //
                // Tyfon's UI Fixes patches BuildsCategoriesPanel.Show and filters with
                // `!originalChild.Data.FromBuild || !weaponBuildsStorage[originalChild.Data.Id].FromPreset`
                // - an unchecked indexer on exactly that id. So the first save of a session threw
                // KeyNotFoundException inside a Harmony prefix, the prefix died, and the BUILD SELECTION
                // window came up empty with every preset the player owned missing from it.
                //
                //   KeyNotFoundException: The given key '6aac6130a7310b54e06c71a3' was not present ...
                //     at EFT.UI.Builds.WeaponBuildsStorage.get_Item (EFT.MongoID id)
                //     at UIFixes.FilterStockPresetsPatches+BuildsCategoriesPanelPatch.CloneAndFilter (...)
                //
                // That id is not a leftover: MongoId writes the Unix time big-endian into its first four
                // bytes, and 0x6AAC6130 is 21:52:48 on the day of the crash, four minutes into the session
                // that crashed. It was the id this method had just inserted.
                //
                // OverrideWeaponBuild does the dictionary and the node together:
                //
                //   if (WeaponBuilds.ContainsKey(id)) RemoveBuildFromHandbook(id);
                //   WeaponBuilds[id] = build;
                //   InsertBuild(build);
                //
                // and it makes NO network call, which the remove-then-insert it replaces could not claim:
                // WeaponBuildsStorage.RemoveBuild is `async Task<IResult>` and awaits _session.RemoveBuild,
                // a POST to /client/builds/delete that removes the build from the profile, fire-and-forget
                // from a UI click handler.
                //
                // Being precise about when that was dangerous, because the first version of this comment
                // was not. It was HARMLESS before: FindByName searches WeaponBuilds, which InsertBuild
                // never populated, so it could only ever return a build loaded at session start under the
                // id the server had already superseded - the delete hit nothing. It becomes destructive
                // only once the server REUSES the id, which the same change introduced. So it is a hazard
                // this change creates and then removes, not one it inherits. Dropping the call would be
                // right regardless: a discarded Task doing a network write from a click handler is not
                // something to keep.
                // One new line in Player.log comes from this, and it is NOT the old crash returning. If a
                // profile refresh has rebuilt the node tree, the dictionary entry survives it, so
                // OverrideWeaponBuild takes its ContainsKey branch, RemoveBuildFromHandbook finds no node
                // and writes Debug.LogError("Node not found: <id>"), returns, and InsertBuild then puts
                // the node back. Red text, correct outcome. Worth knowing before anyone reads it as a
                // relapse of the KeyNotFoundException above.
                storage.OverrideWeaponBuild(new WeaponBuild(
                    new MongoID(saved.Id), saved.Name, new MongoID(saved.Root), items));

                // Said on success too, so the log distinguishes "inserted" from "fell back to a
                // restart" without anyone having to watch the screen while pressing the button.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: inserted the preset \"{saved.Name}\" ({items.Length} items) into the " +
                    "game's build list - no reload needed.");

                return true;
            }
            catch (Exception ex)
            {
                // Deliberately swallowed. The build is already written and safe; all that is lost is
                // the convenience, and a throw here would land inside a UI rebuild. Through Gave for the
                // wording, so the sentence lives in one place and cannot drift between the guards and the
                // exception.
                return Gave(ex.Message);
            }
        }

        /// <summary>Log the reason and answer no, in one expression, so a guard can stay one line.</summary>
        private static bool Gave(string reason)
        {
            Plugin.LogSource?.LogWarning(
                $"QuestTree: saved the preset but could not show it without a reload - {reason}.");

            return false;
        }
    }
}
