using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using EFT;
using EFT.Interactive;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Reads, from a loaded raid, where every quest zone and quest item on the map actually is,
    /// and hands it to the server half to keep.
    ///
    /// This is the only place those positions exist. The quest data names a zone ("place_flash_
    /// drive_1"); the scene has a TriggerWithId with that id at a transform. No file on disk holds
    /// the two together, which is why the map's objective pins used to come from websites - one of
    /// which is down and the other three years stale. One raid on a map fills in every zone-shaped
    /// objective on it, for every quest, vanilla or modded, at the game version actually running.
    ///
    /// Everything on the map is read, not just the player's own open quests: the server matches
    /// zones to quests, and a zone a locked quest will want next month is a place today.
    ///
    /// Two passes. Other mods (WTT's quest packs, for one) spawn their own zones at raid load
    /// through patches of their own, and Harmony gives no reliable order between two mods'
    /// postfixes - so one read right after OnGameStarted can land before theirs. The second pass
    /// catches what arrived late; only what is new is posted again, and it is logged by id so the
    /// delay can be judged from evidence.
    /// </summary>
    internal static class ZoneHarvester
    {
        /// <summary>A ceiling on how long the harvester will sit waiting for the server's write
        /// window. The server's own window is far shorter; this only bounds a bad answer.</summary>
        private const int MaxRebuildWait = 120;

        private const string Route = "/questtree/zones";

        private const float FirstPassDelay = 3f;
        private const float SecondPassDelay = 27f;

        /// <summary>Run by the GameWorld itself (it is a MonoBehaviour), so it dies with the raid.</summary>
        public static IEnumerator HarvestCoroutine(GameWorld gameWorld)
        {
            yield return new WaitForSeconds(FirstPassDelay);

            var request = TryCollect(gameWorld, previous: null, out var map);
            if (request == null) yield break;

            Post(request, "first pass");

            yield return new WaitForSeconds(SecondPassDelay);

            var second = TryCollect(gameWorld, previous: request, out var secondMap);
            if (second == null) yield break;

            // Defensive: the notes record that a transit loads a fresh GameWorld, whose own
            // OnGameStarted starts a new harvest while this coroutine dies with the old world, so
            // the map should never change between passes. Should it ever, TryCollect has started
            // over on the new map and what it holds is that map's first pass, not a delta.
            if (!string.Equals(secondMap, map, StringComparison.OrdinalIgnoreCase))
            {
                Post(second, "first pass after a transit");
                yield break;
            }

            var newZones = second.Triggers.Count - request.Triggers.Count;
            var newItems = second.QuestItems.Count - request.QuestItems.Count;

            if (newZones <= 0 && newItems <= 0)
            {
                Plugin.LogSource?.LogInfo($"QuestTree: second pass on {map} found nothing new.");
                yield break;
            }

            Post(second, $"second pass, +{newZones} zones, +{newItems} quest items");
        }

        /// <summary>One read of the scene, unioned with an earlier one when given. Null when
        /// there is nothing to say - no map name, or nothing found. Never throws.</summary>
        private static ZoneHarvestRequest TryCollect(GameWorld gameWorld, ZoneHarvestRequest previous, out string map)
        {
            map = null;

            try
            {
                map = gameWorld?.MainPlayer?.Location;
                if (string.IsNullOrEmpty(map)) map = gameWorld?.LocationId;

                if (string.IsNullOrEmpty(map))
                {
                    Plugin.LogSource?.LogInfo("QuestTree: no map name on this GameWorld - zones not harvested.");
                    return null;
                }

                var triggers = new Dictionary<string, HarvestedTrigger>(StringComparer.Ordinal);
                var items = new Dictionary<string, HarvestedQuestItem>(StringComparer.Ordinal);

                // The earlier pass belongs to the map that was loaded then. Not expected to
                // happen (see HarvestCoroutine), but unioning it into a read of a different map
                // would file one map's zones under another's name on the server, where harvested
                // positions outrank every other source - cheap to rule out.
                if (previous != null && !string.Equals(previous.Map, map, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.LogSource?.LogInfo($"QuestTree: map changed from {previous.Map} to {map} between passes - starting over.");
                    previous = null;
                }

                if (previous != null)
                {
                    foreach (var t in previous.Triggers) triggers[TriggerKey(t)] = t;
                    foreach (var i in previous.QuestItems) items[ItemKey(i)] = i;
                }

                var before = (triggers.Count, items.Count);
                var added = new List<string>();

                CollectTriggers(triggers, added);
                CollectQuestItems(gameWorld, items, added);

                if (triggers.Count == 0 && items.Count == 0)
                {
                    Plugin.LogSource?.LogInfo($"QuestTree: no quest zones or quest items found on {map}.");
                    return null;
                }

                if (previous != null && added.Count > 0)
                {
                    // The evidence for whether the second pass is needed at all - see the class comment.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {added.Count} zone(s)/item(s) appeared after the first pass on {map}: " +
                        string.Join(", ", added));
                }

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: harvested {triggers.Count} zones and {items.Count} quest items on {map}" +
                    (previous != null ? $" (was {before.Item1}/{before.Item2})" : "") + ".");

                return new ZoneHarvestRequest
                {
                    Map = map,
                    ClientVersion = ModInfo.Version,
                    Triggers = new List<HarvestedTrigger>(triggers.Values),
                    QuestItems = new List<HarvestedQuestItem>(items.Values)
                };
            }
            catch (Exception ex)
            {
                // A raid must never pay for a harvest going wrong.
                Plugin.LogSource?.LogWarning($"QuestTree: zone harvest failed ({ex.Message}) - the map will use what it already has.");
                return null;
            }
        }

        private static void CollectTriggers(Dictionary<string, HarvestedTrigger> into, List<string> added)
        {
            // includeInactive: a zone a mod enables later, or one the game keeps off until an event,
            // is still a place. Flagged rather than dropped.
            var found = UnityEngine.Object.FindObjectsOfType(typeof(TriggerWithId), true);

            foreach (var obj in found)
            {
                if (!(obj is TriggerWithId trigger) || string.IsNullOrEmpty(trigger.Id)) continue;

                var position = trigger.transform.position;
                if (!IsFinite(position)) continue; // a NaN would fault the server's JSON reader before any guard

                var collider = trigger.GetComponent<Collider>();
                var extents = collider != null && IsFinite(collider.bounds.extents) ? collider.bounds.extents : Vector3.zero;

                var harvested = new HarvestedTrigger
                {
                    Id = trigger.Id,
                    Kind = trigger.GetType().Name,
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    Active = trigger.gameObject.activeInHierarchy,
                    ExtentX = extents.x,
                    ExtentY = extents.y,
                    ExtentZ = extents.z
                };

                var key = TriggerKey(harvested);
                if (!into.ContainsKey(key)) added.Add(harvested.Id);
                into[key] = harvested;
            }
        }

        private static void CollectQuestItems(GameWorld gameWorld, Dictionary<string, HarvestedQuestItem> into, List<string> added)
        {
            // _iteration is the public backing list in 4.1.5; DynamicMaps reaches it by reflection
            // from older builds, which is not needed here.
            var loot = gameWorld?.LootItems?._iteration;
            if (loot == null) return;

            foreach (var lootItem in loot)
            {
                if (lootItem == null || lootItem.Item == null || !lootItem.Item.QuestItem) continue;
                if (string.IsNullOrEmpty(lootItem.TemplateId)) continue;

                var position = lootItem.transform.position;
                if (!IsFinite(position)) continue;

                var harvested = new HarvestedQuestItem
                {
                    TemplateId = lootItem.TemplateId,
                    ItemId = lootItem.ItemId ?? "",
                    X = position.x,
                    Y = position.y,
                    Z = position.z
                };

                var key = ItemKey(harvested);
                if (!into.ContainsKey(key)) added.Add(lootItem.TemplateId);
                into[key] = harvested;
            }
        }

        /// <summary>A position a destroyed or exploded object can report. Newtonsoft writes NaN
        /// and Infinity as strings, and the server's System.Text.Json refuses those in a float,
        /// throwing inside SPT's deserializer where none of this mod's guards can reach.</summary>
        private static bool IsFinite(Vector3 p) =>
            !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z) &&
            !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);

        /// <summary>Several triggers can share an id (a zone made of more than one volume), so the
        /// key is id plus rounded position - the same de-duplication the server draws with.</summary>
        private static string TriggerKey(HarvestedTrigger t) => $"{t.Id}|{Grid(t.X)}|{Grid(t.Y)}|{Grid(t.Z)}";

        private static string ItemKey(HarvestedQuestItem i) =>
            string.IsNullOrEmpty(i.ItemId) ? $"{i.TemplateId}|{Grid(i.X)}|{Grid(i.Y)}|{Grid(i.Z)}" : i.ItemId;

        /// <summary>A coordinate rounded to the metre as an integer, invariant: the server draws
        /// the same key (Numbers.Grid). Rounded to an int rather than formatted "F0", which gave
        /// "-0" on one runtime and "0" on the other for the same value; and a locale with its own
        /// minus sign would have made the halves disagree about what "the same zone" is.</summary>
        private static string Grid(float value) =>
            ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

        /// <summary>Fire-and-forget on a pool thread: the raid does not wait on the network, and
        /// RequestHandler's synchronous calls would block Unity's main thread if used here.</summary>
        private static void Post(ZoneHarvestRequest request, string label)
        {
            var map = request.Map;

            Task.Run(async () =>
            {
                try
                {
                    // Serialised here, off the main thread: a few hundred KB of JSON is a frame
                    // hitch in a raid, and the request object is not touched again after this.
                    var json = JsonConvert.SerializeObject(request);
                    var reply = await RequestHandler.PostJsonAsync(Route, json);

                    var response = string.IsNullOrEmpty(reply) ? null : JsonConvert.DeserializeObject<ZoneHarvestResponse>(reply);
                    if (response == null)
                    {
                        Plugin.LogSource?.LogInfo($"QuestTree: zones for {map} sent ({label}), but the reply was not the server half's - {Excerpt(reply)}");
                        return;
                    }

                    if (!response.Ok)
                    {
                        // Said at Warning: the raid's zones were read and thrown away, and the
                        // reason is the server's to give.
                        Plugin.LogSource?.LogWarning($"QuestTree: the server refused the zones for {map} ({label}): {response.Message}");
                        return;
                    }

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: zones for {map} sent to the server ({label}) - it now holds {response.Zones} zones and " +
                        $"{response.QuestItems} quest items for it ({response.Message}).");

                    // The server rebuilt both payloads; the next Maps tab build must ask again.
                    //
                    // The quest list too, since 1.9.0: new zones change which map an
                    // "any"-location quest is derived onto, and TryFetchAll latches for the whole
                    // session. Without this the quest gains its pins from the marker payload and
                    // never gains its map entry - the exact split the derivation exists to prevent,
                    // arriving from the client side instead.
                    //
                    // Waited out first when the server says so. A buffered harvest has not been
                    // written yet, so invalidating now would refetch the PRE-harvest payload and
                    // latch THAT for the session - strictly worse than not invalidating at all. We
                    // are in a raid; nobody is looking at the Maps tab.
                    var wait = response.RebuildInSeconds;
                    if (wait > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 1, MaxRebuildWait)));
                    }

                    QuestDataClient.InvalidateMapMarkers();
                    QuestDataClient.InvalidateQuests();
                }
                catch (Exception ex)
                {
                    // Missing or outdated server half, or no connection: the harvest is simply lost
                    // and the next raid tries again. Info, not Warning - the server half is optional.
                    Plugin.LogSource?.LogInfo($"QuestTree: could not send zones for {map} to the server ({ex.Message}).");
                }
            });
        }

        private static string Excerpt(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return "(empty reply - server half missing or predates this route)";
            return reply.Length <= 120 ? reply : reply.Substring(0, 120) + "...";
        }
    }
}
