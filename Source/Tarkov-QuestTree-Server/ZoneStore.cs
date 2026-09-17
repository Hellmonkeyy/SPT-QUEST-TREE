using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// The zone positions harvested from loaded raids, one file per map under
    /// user/mods/QuestTree/zones/. This is what makes an objective's zone id - which the quest
    /// data has always carried - into a place on the map, without asking any website.
    ///
    /// Harvests of one map are unioned by zone: Factory day and night share a file but not a
    /// scene (the night scene has zones the day one lacks, and mods spawn zones per variant), so
    /// a replace-whole file lost whichever variant was raided first. A zone seen again at a new
    /// position keeps both entries until the file is deleted - a moved zone after a game update
    /// is rare and visible; a lost zone was neither. Shipped seed files are read exactly like
    /// harvested ones, so a release can carry them and a later raid still adds to them.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class ZoneStore(ISptLogger<ZoneStore> logger)
    {
        /// <summary>Maps that load the same scene, so a harvest of one serves the other. The
        /// quest data keys them separately (different location ids), the triggers are identical.</summary>
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["factory4_night"] = "factory4_day",
            ["Sandbox_high"] = "Sandbox"
        };

        /// <summary>The map name becomes a file name, so it is held to what a location id can be.</summary>
        private static readonly Regex SafeName = new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);

        /// <summary>Names the regex admits that Windows still treats as devices: "NUL.json" is the
        /// NUL device, and "CON.json" can hang a write.</summary>
        private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Bounds on one harvested entry. A zone id or a template id is a short token;
        /// anything longer is not from a raid. Coordinates must be finite: an Infinity written
        /// to the file is refused by the reader, which would cost the map its whole history.</summary>
        private const int MaxIdLength = 128;
        private const int MaxKindLength = 64;

        /// <summary>ClientVersion is copied into the file verbatim and was never bounded, so a
        /// 100 MB version string with one trigger passed every other check and landed on disk.</summary>
        private const int MaxClientVersionLength = 64;

        /// <summary>Coordinates were checked finite but not bounded, so 3.4e38 was accepted and
        /// became a pin position on every player's map. Tarkov's maps fit inside a couple of
        /// kilometres; this is generous by two orders of magnitude and still finite enough to draw.</summary>
        private const float MaxCoordinate = 100_000f;

        /// <summary>A ceiling on one map's file. Real maps hold a few hundred entries; the union
        /// never shrinks, and a client that varies positions by a metre could otherwise grow it
        /// without bound.</summary>
        private const int MaxEntriesPerMap = 50_000;

        private static readonly JsonSerializerOptions FileOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        private readonly object _lock = new();

        /// <summary>Canonical map name -> file, or null once a map is known to have no file. Read
        /// once; Save replaces the entry.</summary>
        private readonly Dictionary<string, ZoneFile?> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Zone id -> the maps it was harvested on. Built lazily, dropped by Save.</summary>
        private Dictionary<string, IReadOnlyCollection<string>>? _zoneToMap;

        /// <summary>Canonical maps with at least one usable trigger. Built beside _zoneToMap.</summary>
        private HashSet<string>? _harvestedMaps;

        private static string Folder =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "zones");

        public static string Canonical(string map) =>
            Aliases.TryGetValue(map, out var canonical) ? canonical : map;

        public static bool IsValidMapName(string? map) =>
            map != null && SafeName.IsMatch(map) && !ReservedNames.Contains(map);

        /// <summary>Drops the entries of a harvest that cannot be stored or drawn, in place, and
        /// says how many. The client only sends what it read from a scene, but a scene can hold
        /// a destroyed object's NaN position, and nothing but this stands between a hostile or
        /// buggy Fika client and the file every other player's pins are drawn from.</summary>
        public static int Sanitise(ZoneHarvestRequest request)
        {
            var dropped = 0;

            if (request.ClientVersion != null && request.ClientVersion.Length > MaxClientVersionLength)
                request.ClientVersion = request.ClientVersion[..MaxClientVersionLength];

            if (request.Triggers != null)
            {
                dropped += request.Triggers.RemoveAll(t =>
                    t == null || string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > MaxIdLength ||
                    !InWorld(t.X) || !InWorld(t.Y) || !InWorld(t.Z));

                foreach (var t in request.Triggers)
                {
                    t.Kind ??= "";
                    if (t.Kind.Length > MaxKindLength) t.Kind = t.Kind[..MaxKindLength];
                    if (!Finite(t.ExtentX) || !Finite(t.ExtentY) || !Finite(t.ExtentZ))
                        t.ExtentX = t.ExtentY = t.ExtentZ = 0f;
                }
            }

            if (request.QuestItems != null)
            {
                dropped += request.QuestItems.RemoveAll(i =>
                    i == null || string.IsNullOrWhiteSpace(i.TemplateId) || i.TemplateId.Length > MaxIdLength ||
                    (i.ItemId != null && i.ItemId.Length > MaxIdLength) ||
                    !InWorld(i.X) || !InWorld(i.Y) || !InWorld(i.Z));

                foreach (var i in request.QuestItems) i.ItemId ??= "";
            }

            return dropped;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Finite AND somewhere a map could plausibly be. Finite alone let 3.4e38 through,
        /// which draws as a pin at the edge of the world on everyone's map.</summary>
        private static bool InWorld(float value) =>
            Finite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;

        /// <summary>Zone id -> the canonical maps it was harvested on, across every map on disk.
        ///
        /// This is what lets a quest declaring "any" be placed on the map it is actually done on:
        /// its objectives name zone ids, and only the harvest knows where those are. Built from
        /// harvested data only - never from the zone's name. Ids like "Aishi_Shoreline_TGCrate_1"
        /// invite string matching, and that guess would file a modded map's quests on whichever
        /// stock map their name resembles.
        ///
        /// A SET of maps per zone, not one. Three shipped zone ids sit on more than one map with no
        /// attacker present - fuel4 on RezervBase and bigmap, exit777 on Labyrinth and bigmap,
        /// rshg_event_04_jaeger_r_point on Shoreline, Woods and bigmap - and all three are named by
        /// real quests. First-harvest-wins dropped one of them, which removes a requirement row, and
        /// an empty requirement list draws GREEN.
        ///
        /// Cached because both payload builders ask, and dropped by Save: a harvest that adds zones
        /// changes the answer, and the marker rebuild it triggers must not run against a stale
        /// index.</summary>
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> ZoneToMap()
        {
            lock (_lock)
            {
                BuildIndexes();
                return _zoneToMap!;
            }
        }

        /// <summary>The canonical maps with at least one usable trigger.
        ///
        /// Deliberately "usable trigger" rather than "a file exists": the question it stands for is
        /// "could an any-location condition have been placed here", and a file holding nothing
        /// placeable answers no. Used to keep the readiness cue neutral on an unharvested map rather
        /// than letting an empty requirement list read as "you are ready".</summary>
        public IReadOnlyCollection<string> HarvestedMaps()
        {
            lock (_lock)
            {
                BuildIndexes();
                return _harvestedMaps!;
            }
        }

        /// <summary>Both indexes from one walk. Caller holds _lock.</summary>
        private void BuildIndexes()
        {
            if (_zoneToMap != null && _harvestedMaps != null) return;

            // OrdinalIgnoreCase, matching every other zone-id comparison in this server - the marker
            // lookup, ZonesWanted, ObjectiveDto.ZoneIds. Case-sensitive here would give a quest pins
            // (found case-insensitively) and no derived location (missed case-sensitively), which is
            // the exact split this index exists to prevent.
            var index = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var maps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in CachedAndOnDisk())
            {
                if (file?.Triggers == null) continue;

                // ZoneFile.Map, never the file name: filenames carry the disk's casing (Bigmap.json)
                // and the key has to be the canonical one Save wrote.
                var map = file.Map;
                if (string.IsNullOrWhiteSpace(map)) continue;

                foreach (var trigger in file.Triggers)
                {
                    if (trigger == null || string.IsNullOrWhiteSpace(trigger.Id)) continue;

                    // Union, never first-harvest-wins. Besides being wrong on shipped data, keeping
                    // the first mapping means a legitimate harvest arriving after a bad one can
                    // never correct it.
                    if (!index.TryGetValue(trigger.Id, out var set))
                        index[trigger.Id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    set.Add(map);
                    maps.Add(map);
                }
            }

            var frozen = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index) frozen[entry.Key] = entry.Value;

            _zoneToMap = frozen;
            _harvestedMaps = maps;
        }

        /// <summary>Every zone file this server knows about: the cache first, then any file on disk
        /// not already cached.
        ///
        /// The cache first because Save keeps a harvest in memory when the disk write fails, and a
        /// folder-only listing would silently omit it - pins would appear and derivation would not.
        /// Caller holds _lock.</summary>
        private IEnumerable<ZoneFile?> CachedAndOnDisk()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _cache)
            {
                seen.Add(entry.Key);
                if (entry.Value != null) yield return entry.Value;
            }

            if (!System.IO.Directory.Exists(Folder)) yield break;

            foreach (var path in System.IO.Directory.EnumerateFiles(Folder, "*.json"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);

                // Reuses the validation already here rather than trusting the directory.
                if (!IsValidMapName(name)) continue;

                var key = Canonical(name);
                if (!seen.Add(key)) continue;

                var file = Read(key);
                _cache[key] = file;
                if (file != null) yield return file;
            }
        }

        /// <summary>The harvest for a map, or null when none has been taken.</summary>
        public ZoneFile? TryGet(string map)
        {
            if (!IsValidMapName(map)) return null;

            var key = Canonical(map);

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached)) return cached;

                var file = Read(key);
                _cache[key] = file;
                return file;
            }
        }

        /// <summary>Maps already warned about their ceiling this boot, so a client that keeps
        /// posting is heard once rather than filling the log.</summary>
        private readonly HashSet<string> OverflowReported = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drops newest-first until the union fits, and says how many went.
        ///
        /// Newest-first is the point: the entries already in the file include the shipped seed
        /// zones, which are the ones worth keeping, and the overflow is by definition what this
        /// post is trying to add.</summary>
        private static int DropOverflow(
            Dictionary<string, HarvestedTrigger> triggers,
            Dictionary<string, HarvestedQuestItem> items,
            int overflow)
        {
            var dropped = 0;

            foreach (var key in items.Keys.Reverse().Take(overflow).ToList())
            {
                items.Remove(key);
                dropped++;
            }

            var stillOver = overflow - dropped;
            if (stillOver <= 0) return dropped;

            foreach (var key in triggers.Keys.Reverse().Take(stillOver).ToList())
            {
                triggers.Remove(key);
                dropped++;
            }

            return dropped;
        }

        /// <summary>How often one map may be written. A raid harvests its map up to three times -
        /// a first pass, one after a transit, and a closing pass - so this must not be so tight
        /// that a single player's own raid is throttled.</summary>
        private static readonly TimeSpan WriteWindow = TimeSpan.FromSeconds(20);

        /// <summary>Canonical map -> when it may next be written, and what is waiting.</summary>
        private readonly Dictionary<string, DateTime> _nextWrite = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ZoneHarvestRequest> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Seconds until this map's pending harvest will be written, or 0 when the last
        /// post was applied immediately. The client waits this long before invalidating its caches:
        /// invalidating straight away would refetch the PRE-harvest payload and - because the quest
        /// list latches for the session - cache it for good.</summary>
        public int PendingSeconds(string map)
        {
            var key = Canonical(map);

            lock (_lock)
            {
                if (!_pending.ContainsKey(key)) return 0;
                if (!_nextWrite.TryGetValue(key, out var at)) return 0;

                var wait = at - DateTime.UtcNow;
                return wait <= TimeSpan.Zero ? 1 : (int)Math.Ceiling(wait.TotalSeconds);
            }
        }

        /// <summary>Applies any harvest that was buffered while a map was inside its write window,
        /// and says which maps actually changed. Called by the harvest route, so a later post is
        /// what flushes an earlier one - no timer to own, and nothing is ever lost to the clock.</summary>
        public bool DrainPending()
        {
            List<ZoneHarvestRequest> due;

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                due = _pending
                    .Where(e => !_nextWrite.TryGetValue(e.Key, out var at) || now >= at)
                    .Select(e => e.Value)
                    .ToList();

                foreach (var request in due) _pending.Remove(Canonical(request.Map));
            }

            var changed = false;
            foreach (var request in due)
            {
                var saved = Save(request, out var added);
                if (saved != null && added > 0) changed = true;
            }

            return changed;
        }

        /// <summary>Merges a harvest into whatever is already waiting for that map.
        ///
        /// Buffered rather than refused, which is the whole point: ZoneHarvester posts
        /// fire-and-forget with no retry, so a bare "too soon" throws the post away for good - and
        /// on Fika the first peer to post would win the window while every other player's harvest
        /// was discarded. Returns false only when the buffer itself is at the entry ceiling, which
        /// is the abuse case rather than the normal one.</summary>
        private bool Buffer(string key, ZoneHarvestRequest request)
        {
            if (!_pending.TryGetValue(key, out var waiting))
            {
                _pending[key] = request;
                return true;
            }

            var held = (waiting.Triggers?.Count ?? 0) + (waiting.QuestItems?.Count ?? 0);
            if (held >= MaxEntriesPerMap) return false;

            waiting.Triggers ??= new List<HarvestedTrigger>();
            waiting.QuestItems ??= new List<HarvestedQuestItem>();

            if (request.Triggers != null) waiting.Triggers.AddRange(request.Triggers);
            if (request.QuestItems != null) waiting.QuestItems.AddRange(request.QuestItems);

            return true;
        }

        /// <summary>Whether this harvest was written, or buffered for the map's next window.
        ///
        /// The gate sits above Save rather than at the top of the route: Sanitise has to run either
        /// way, because the buffer may only ever hold validated entries. What is being defended is
        /// the WRITE - Save builds two dictionaries over the whole union inside the lock and
        /// rewrites the entire file indented, which near the ceiling is tens of megabytes a post,
        /// and it holds the lock every ZoneToMap read on the quest and marker paths needs.</summary>
        public ZoneFile? SaveOrBuffer(ZoneHarvestRequest request, out int added, out bool buffered)
        {
            added = 0;
            buffered = false;

            var key = Canonical(request.Map);

            lock (_lock)
            {
                if (_nextWrite.TryGetValue(key, out var at) && DateTime.UtcNow < at)
                {
                    buffered = Buffer(key, request);
                    return null;
                }

                _nextWrite[key] = DateTime.UtcNow + WriteWindow;
            }

            return Save(request, out added);
        }

        /// <summary>Unions this harvest into the map's file, and writes it when the union changed anything.
        /// A post that adds nothing new - every Fika client in a raid sending the same scene - returns the
        /// file it already had without touching the disk.
        ///
        /// ALWAYS returns a file when it got that far - it cannot return null for a full map any more.
        /// It used to, and the summary saying so outlived the behaviour by three releases: DropOverflow sheds
        /// the excess and keeps the rest, because refusing the post bricked a map permanently. Anyone
        /// reading the router's "map file full" rejection should know it now answers only for the
        /// in-memory BUFFER being full, never the file.
        ///
        /// `added` is how many entries were new - zero when every Fika client in a raid posts the same
        /// scene, which is the case the caller must not rebuild for.</summary>
        public ZoneFile? Save(ZoneHarvestRequest request, out int added)
        {
            added = 0;
            var key = Canonical(request.Map);

            lock (_lock)
            {
                var fromCache = _cache.TryGetValue(key, out var existing);
                if (!fromCache) existing = Read(key);

                var triggers = new Dictionary<string, HarvestedTrigger>(StringComparer.Ordinal);
                var items = new Dictionary<string, HarvestedQuestItem>(StringComparer.Ordinal);

                foreach (var t in existing?.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in existing?.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                var before = triggers.Count + items.Count;

                // Newest wins on an exact match; anything new is added.
                foreach (var t in request.Triggers ?? new List<HarvestedTrigger>()) triggers[TriggerKey(t)] = t;
                foreach (var i in request.QuestItems ?? new List<HarvestedQuestItem>()) items[ItemKey(i)] = i;

                // The excess, never the whole harvest. Refusing the post once a map was full
                // BRICKED that map permanently: pad a real map to the ceiling and no legitimate
                // harvest could ever land there again. Eviction is worse, not better - a
                // HarvestedTrigger carries no timestamp, so there is nothing to evict BY, and
                // insertion order would delete the eleven shipped seed files first.
                var overflow = triggers.Count + items.Count - MaxEntriesPerMap;
                if (overflow > 0)
                {
                    var shed = DropOverflow(triggers, items, overflow);

                    // Once per map per boot, and it names the remedy: refusing quietly would mean
                    // the map can never accept a new zone again while every post still answers
                    // "saved", so the failure would look exactly like success.
                    if (OverflowReported.Add(key))
                    {
                        logger.Warning(
                            $"Quest Tracker: zones/{key}.json is at its {MaxEntriesPerMap} entry ceiling - " +
                            $"{shed} newly harvested entries were dropped. Delete that file to start it " +
                            "over; the next raid there will rebuild it.");
                    }
                }

                added = triggers.Count + items.Count - before;

                // Nothing new: the file already says all this, so it is not rewritten and the
                // caller is told to skip the marker rebuild. Only when it really is on disk and
                // in the cache, though - a harvest whose write failed is kept in memory with a
                // promise to try the disk next time, and this is next time. A re-read of the same
                // positions with changed flags is not persisted; the flags are informational.
                if (added == 0 && existing != null && fromCache && System.IO.File.Exists(ResolvePath(key)))
                    return existing;

                var file = new ZoneFile
                {
                    Map = key,
                    HarvestedAt = DateTime.UtcNow.ToString("u"),
                    ClientVersion = request.ClientVersion ?? "",
                    Triggers = new List<HarvestedTrigger>(triggers.Values),
                    QuestItems = new List<HarvestedQuestItem>(items.Values)
                };

                try
                {
                    // Written beside the file and moved over it: the file is the union of every
                    // harvest ever taken here, and a write cut short by a crash or a full disk
                    // used to leave a truncated file the reader refuses, losing all of it.
                    System.IO.Directory.CreateDirectory(Folder);
                    var path = ResolvePath(key);
                    var temp = path + ".tmp";
                    System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(file, FileOptions));
                    System.IO.File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Kept in memory regardless: this server session still benefits, and the next
                    // raid will try the disk again.
                    logger.Warning($"Quest Tracker: could not write zones/{key}.json ({ex.Message}) - kept for this session only.");
                }

                _cache[key] = file;

                // Both indexes, inside the lock that already guards the cache: a harvest that adds
                // zones changes which map a zone is on, and the marker rebuild it triggers must not
                // run against a stale answer.
                _zoneToMap = null;
                _harvestedMaps = null;

                logger.Info(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"harvested on '{key}'" + (request.Map != key ? $" (as '{request.Map}')" : "") +
                    (existing != null ? $", {added} new" : "") + ".");

                return file;
            }
        }

        /// <summary>The same keys the client de-duplicates with, so the two sides agree on what
        /// "the same zone" means: id plus rounded position, since one zone can be several volumes.</summary>
        private static string TriggerKey(HarvestedTrigger t) =>
            $"{t.Id}|{Numbers.Grid(t.X)}|{Numbers.Grid(t.Y)}|{Numbers.Grid(t.Z)}";

        private static string ItemKey(HarvestedQuestItem i) =>
            string.IsNullOrEmpty(i.ItemId) ? $"{i.TemplateId}|{Numbers.Grid(i.X)}|{Numbers.Grid(i.Y)}|{Numbers.Grid(i.Z)}" : i.ItemId;

        private static string PathFor(string key) => System.IO.Path.Combine(Folder, key + ".json");

        /// <summary>The map's file as it exists on disk, whatever its casing, else where a new one
        /// goes. The cache is case-insensitive but a filesystem may not be: a harvest posted as
        /// "Bigmap" wrote Bigmap.json, which a Linux host then never read back as bigmap.</summary>
        private static string ResolvePath(string key)
        {
            var wanted = PathFor(key);
            if (System.IO.File.Exists(wanted) || !System.IO.Directory.Exists(Folder)) return wanted;

            var options = new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive };
            foreach (var candidate in System.IO.Directory.EnumerateFiles(Folder, key + ".json", options))
                return candidate;

            return wanted;
        }

        private ZoneFile? Read(string key)
        {
            try
            {
                var path = ResolvePath(key);
                if (!System.IO.File.Exists(path)) return null;

                var file = JsonSerializer.Deserialize<ZoneFile>(System.IO.File.ReadAllText(path), FileOptions);
                if (file == null) return null;

                // Filled in rather than dereferenced. Files this server wrote always carry both arrays, but
                // a hand-edited or third-party seed with "triggers": null threw into the catch below and was
                // reported as unreadable - discarding the questItems it did have, and telling the player to
                // raid the map again to fix a file that was fine. BuildIndexes guards for exactly this
                // shape; this path did not.
                file.Triggers ??= new List<HarvestedTrigger>();
                file.QuestItems ??= new List<HarvestedQuestItem>();

                logger.Info(
                    $"Quest Tracker: {file.Triggers.Count} zones and {file.QuestItems.Count} quest items " +
                    $"known for '{key}' (harvested {file.HarvestedAt}).");

                return file;
            }
            catch (Exception ex)
            {
                // A corrupt file costs one map its harvested pins until the next raid there.
                logger.Warning($"Quest Tracker: could not read zones/{key}.json ({ex.Message}) - raid the map again to replace it.");
                return null;
            }
        }
    }
}
