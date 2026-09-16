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
    /// So the server echoes back the build exactly as it wrote it, with the ids SPT minted rather than
    /// the ones we proposed, and this inserts the same build into the storage the game is already
    /// reading from. The client's copy and the profile therefore agree by construction rather than by
    /// hope.</summary>
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

                // A repeat save would otherwise leave two entries with the same name in the in-memory
                // list, where the profile keeps one - the game de-duplicates on its way in, and this
                // list does not.
                var existing = storage.FindByName(saved.Name);
                if (existing != null) storage.RemoveBuild(existing.Id);

                storage.InsertBuild(new WeaponBuild(
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
