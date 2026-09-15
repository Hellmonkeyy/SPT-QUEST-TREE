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
        /// cost is that it will not be visible until the game next asks for its builds.</summary>
        internal static bool Insert(QuestDataClient.SavePresetResult saved)
        {
            if (saved == null || !saved.Saved) return false;
            if (string.IsNullOrEmpty(saved.ItemsJson)) return false;
            if (string.IsNullOrEmpty(saved.Root) || string.IsNullOrEmpty(saved.Id)) return false;

            try
            {
                var storage = Session?.Invoke()?.WeaponBuildsStorage;
                if (storage == null) return false;

                // Deserialised, never hand-built. The fields we could not fill by hand - upd and
                // location, both UnparsedData wrapping a raw JToken - are exactly the ones the build
                // screen needs, and only Newtonsoft can fill them.
                var items = JsonConvert.DeserializeObject<JsonType.FlatItem[]>(saved.ItemsJson);

                if (items == null || items.Length == 0) return false;

                // A repeat save would otherwise leave two entries with the same name in the in-memory
                // list, where the profile keeps one - the game de-duplicates on its way in, and this
                // list does not.
                var existing = storage.FindByName(saved.Name);
                if (existing != null) storage.RemoveBuild(existing.Id);

                storage.InsertBuild(new WeaponBuild(
                    new MongoID(saved.Id), saved.Name, new MongoID(saved.Root), items));

                return true;
            }
            catch (Exception ex)
            {
                // Deliberately swallowed. The build is already written and safe; all that is lost is
                // the convenience, and a throw here would land inside a UI rebuild.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: saved the preset but could not show it without a reload - {ex.Message}");

                return false;
            }
        }
    }
}
