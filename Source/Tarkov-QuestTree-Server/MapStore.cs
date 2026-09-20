using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;

namespace QuestTreeServer
{
    /// <summary>
    /// The captured map pictures this host holds, one folder per map under
    /// user/mods/QuestTree/maps/: the pictures a client rendered in raid, stored so every other
    /// client on the host draws the same map without shipping anything of DynamicMaps'.
    ///
    /// Three things make this different from <see cref="ZoneStore"/>, which is otherwise its
    /// sibling - same folder root, same atomic write, same "a Fika peer can post here
    /// unauthenticated" premise:
    ///
    /// ONE. A set arrives in pieces. A four-floor map at 2.5 MB a floor is a 10 MB body, so the
    /// client posts one floor at a time, and the floors of one capture are grouped by their shared
    /// CapturedAt. Pieces accumulate in <c>.incoming/</c> and only ever move into place as a whole
    /// set, so a client that drops out after two floors of four leaves the host's existing pictures
    /// untouched rather than half-replaced. There is no session to hang this on: the upload route is
    /// one POST per floor from any peer, and grouping on the meta means a restart mid-upload loses
    /// nothing but the last post.
    ///
    /// TWO. It is opt-in. A host takes uploads only with <see cref="AcceptVariable"/> set, because a
    /// picture is the one thing a peer can post that every other player then LOOKS at: an opted-out
    /// host serves the maintainer's shipped set and nothing else. The refusal is an answer, not a
    /// silence - see <see cref="MapUploadResponse.Outcome"/>.
    ///
    /// THREE. A stamp, not a timestamp, is what a client compares. <see
    /// cref="MapIndexEntryDto.Stamp"/> is sha256 over the stored meta's bytes and every picture's
    /// bytes in level order, so it changes when the set changes and not otherwise. It is computed on
    /// completion and rebuilt at boot by reading the folders, never persisted: a stamp file could
    /// disagree with the pictures beside it, and then a client would keep a stale map forever while
    /// both halves reported success.
    /// </summary>
    [Injectable(InjectionType.Singleton)]
    public class MapStore
    {
        /// <summary>The variable a host sets to take uploads. An environment variable rather than a
        /// config file for the reason <see cref="QuestLog"/> gives: it cannot be packaged into a
        /// release by accident. The user starts SPT.Server.exe from Explorer, so this needs a launcher
        /// script (tools/server-debug.cmd is the pattern) to reach the server at all.</summary>
        public const string AcceptVariable = "QUESTTREE_ACCEPT_MAPS";

        /// <summary>One picture, decoded. A 2048 px JPEG of a Tarkov map at q80 is 0.5-1.2 MB, so this
        /// is generous; it exists because the body arrives as base64 from an unauthenticated route and
        /// something has to be the ceiling.</summary>
        private const int MaxImageBytes = 2_621_440;

        /// <summary>The base64 ceiling, checked BEFORE decoding: decoding a 400 MB string to find out
        /// it is too big is the denial of service the size limit exists to prevent. 4 chars per 3
        /// bytes, plus room for padding and line breaks.</summary>
        private const int MaxEncodedChars = MaxImageBytes / 3 * 4 + 1024;

        /// <summary>The same ceiling ZoneStore puts on a map's floors, for the same reason: no Tarkov
        /// map has eight walkable layers, and each one here costs a picture.</summary>
        private const int MaxFloors = 8;

        /// <summary>The long side a capture may have. The client renders at 2048 or 4096; past this a
        /// Texture2D is a problem on the drawing side rather than here.</summary>
        private const int MaxFloorPixels = 4096;

        /// <summary>How far a floor's declared pixel size may sit from ceil(extent x pxPerMetre)
        /// before the three numbers are not describing one projection. Two, not zero: the client
        /// rounds a metre extent up to whole tiles and the two sides round in their own code.</summary>
        private const int PixelTolerance = 2;

        /// <summary>One map's ceiling - and, said plainly because a guard that cannot fire must not look
        /// like one: MaxFloors x MaxImageBytes is EXACTLY this figure, so as the constants stand today
        /// nothing can reach it. It is kept because the three numbers are set independently, and it is
        /// the one that would bite first if a later release raised the floor cap or the picture size.
        /// The store's own total below is reachable - fifteen maps at a full 20 MB pass it - but only
        /// by writing 300 MB, so neither is exercised by the stage C harness. Said here rather than
        /// discovered later.</summary>
        private const long MaxBytesPerMap = 20L * 1024 * 1024;

        /// <summary>The whole store's ceiling. ~26 floors of the 11 vanilla maps is 15-30 MB, so this
        /// is ten times a full set and still small enough that a peer cannot fill a host's disk.</summary>
        private const long MaxBytesTotal = 300L * 1024 * 1024;

        private const int MaxLabels = 200;
        private const int MaxLabelLength = 40;

        /// <summary>A floor name is a caption on a layer button; the free-text fields (capturedAt,
        /// modVersion, timeOfDay, and a file name before it is replaced) are bounded before they are
        /// parsed or printed, exactly as ZoneStore bounds ClientVersion - a 100 MB string that
        /// satisfies every other check must not reach the disk.</summary>
        private const int MaxNameLength = 32;
        private const int MaxFreeTextLength = 64;

        private const int MaxTileSize = 8192;
        private const double MaxCoordinate = 100_000d;

        /// <summary>Same cap and same reasoning as QuestTreeRouter.MaxRejectionsLogged: the route is
        /// unauthenticated HTTP on Fika, so a client in a loop must not be able to rotate the real
        /// diagnostics out of a rolling log.</summary>
        private const int MaxRejectionsLogged = 256;

        /// <summary>What a stored picture's file name may be, checked on the way back IN from disk.
        /// The name is written by this class, so a name that fails this came from a hand-edited meta
        /// or a set copied in from elsewhere - and it is about to be joined onto a folder path.</summary>
        private static readonly Regex StoredFileName =
            new(@"^[A-Za-z0-9_\-]{1,60}\.(jpg|png)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly JsonSerializerOptions FileOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        };

        private readonly ISptLogger<MapStore> _logger;

        private readonly object _lock = new();

        /// <summary>Canonical map name -> the complete set this host serves. Rebuilt at construction
        /// from the folders; replaced whole when a set completes. Never holds a partial set: a set
        /// with a picture missing is not something a client should be told about.</summary>
        private readonly Dictionary<string, StoredSet> _sets = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _rejectionsLogged = new(StringComparer.Ordinal);

        private readonly HashSet<string> _declinesLogged = new(StringComparer.OrdinalIgnoreCase);

        private bool _loaded;

        public MapStore(ISptLogger<MapStore> logger)
        {
            _logger = logger;

            // READ ONCE, at construction, like every other QUESTTREE_* flag: this is a singleton, so
            // once per boot, and a value that cannot change mid-run cannot make the index's
            // acceptsUploads disagree with what the upload route actually does.
            AcceptsUploads = QuestLog.Enabled(AcceptVariable);

            // ONE line at Information, whichever way it is set, because the mode is the answer to
            // "why did my capture not upload" and a host that never sees a line cannot tell an
            // opted-out server from a broken one.
            _logger.Info(AcceptsUploads
                ? $"Quest Tracker: map uploads from clients are accepted ({AcceptVariable}=1)."
                : $"Quest Tracker: map uploads from clients are declined ({AcceptVariable}=1 accepts them).");

            // EAGERLY, on the boot thread, and not lazily on the first request: the index and the
            // image route are answered inside SPT's synchronous request handler, which the client
            // calls on Unity's main thread. Hashing a full set of pictures there would freeze the
            // game for as long as it took. Here it costs the boot tens of milliseconds and says so.
            lock (_lock) Load();
        }

        /// <summary>Whether this host takes uploads. Read once at construction; see the constructor.</summary>
        public bool AcceptsUploads { get; }

        private static string Folder =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "user", "mods", "QuestTree", "maps");

        /// <summary>Where floors wait for the rest of their set. A dotted name so that the loader's
        /// own IsValidMapName check skips it without a special case.</summary>
        private static string IncomingFolder => System.IO.Path.Combine(Folder, ".incoming");

        /// <summary>One complete set, as this host serves it.</summary>
        private sealed class StoredSet
        {
            public string Key = "";
            public string Stamp = "";
            public long Bytes;
            public MapCaptureMetaDto Meta = new();
        }

        // ---------------------------------------------------------------------------------------
        // The three routes
        // ---------------------------------------------------------------------------------------

        /// <summary>Takes one floor of one capture, and says what became of it.
        ///
        /// <paramref name="isRealLocation"/> is QuestFacts.IsRealLocation, passed in rather than
        /// injected: this class is about files, the location table is a database read, and a store
        /// that cannot be exercised without booting a server is a store whose limits are never
        /// proven able to fail. The check itself is not optional - see where it is called.</summary>
        public MapUploadResponse Accept(MapUploadRequest? request, Func<string, bool>? isRealLocation)
        {
            if (request == null) return Reject("?", "no body");

            // BEFORE everything else, exactly as AcceptHarvest checks its own schema first: the
            // checks below all assume they are reading a shape this build knows, and a capture from a
            // newer client carries fields System.Text.Json drops silently. Storing the rest would
            // serve every other client a set stamped complete that this build only half understood.
            if (request.SchemaVersion > MapUploadRequest.SupportedSchemaVersion)
                return Reject("?",
                    $"upload schema v{request.SchemaVersion} is newer than the " +
                    $"v{MapUploadRequest.SupportedSchemaVersion} this server reads - this server is older " +
                    "than the client, update the server half");

            // Two checks, AND-ed, for the two different things they guard - the same pair, in the same
            // order, as the zone harvest route. IsValidMapName guards the FILE: the map name becomes a
            // folder name, so the regex and the reserved-name set are what stop an upload escaping
            // maps\ or naming a Windows device. IsRealLocation guards the ANSWER: on Fika every peer
            // can post here, and an invented map name would otherwise add a map to the index that
            // every client then offers to download.
            if (!ZoneStore.IsValidMapName(request.Map)) return Reject("?", "bad map name");
            if (isRealLocation != null && !isRealLocation(request.Map)) return Reject("?", "unknown map");

            // Factory night and Factory day share a scene, so they share a picture; the same for
            // Ground Zero above and below level 20. Folded here, once, so the folder, the index entry
            // and an image request all agree on which set is which.
            var key = ZoneStore.Canonical(request.Map);

            // BEFORE the base64 is decoded, which is the expensive part of an upload and the whole
            // reason a declining host answers early. Nothing is written, nothing is parsed further.
            if (!AcceptsUploads)
            {
                DeclineOnce(key);

                return new MapUploadResponse
                {
                    Outcome = "declined",
                    Reason = "this host does not accept map pictures from clients"
                };
            }

            var meta = request.Meta;

            if (meta == null) return Reject(key, "no capture meta");

            if (meta.SchemaVersion != MapCaptureMetaDto.CurrentSchemaVersion)
                return Reject(key,
                    $"the capture meta is schema v{meta.SchemaVersion}, not the " +
                    $"v{MapCaptureMetaDto.CurrentSchemaVersion} this server stores");

            if (!MetaIsUsable(key, meta, out var problem)) return Reject(key, problem);

            var floor = meta.Floors.FirstOrDefault(f => f.Level == request.Level);

            if (floor == null)
                return Reject(key, $"level {request.Level} is not one of the {meta.Floors.Count} floors the meta names");

            var format = Format(request.Format);

            if (format == null)
                return Reject(key, $"format '{Clip(request.Format ?? "", 16)}' is not jpg or png");

            var encoded = request.ImageBase64 ?? "";

            if (encoded.Length == 0) return Reject(key, "the post carries no picture");

            if (encoded.Length > MaxEncodedChars)
                return Reject(key, $"the picture is larger than the {Mb(MaxImageBytes)} MB a floor may be");

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return Reject(key, "the picture is not base64");
            }

            if (bytes.Length > MaxImageBytes)
                return Reject(key,
                    $"the picture is {bytes.Length:N0} bytes, past the {MaxImageBytes:N0} a floor may be");

            // The magic bytes, not the extension: "format" is a string a peer chose, and a host that
            // trusts it writes whatever it was sent under a name every client will try to decode.
            if (!MagicMatches(format, bytes))
                return Reject(key, $"the bytes do not start as a {format} picture does");

            var captured = ParseStamp(meta.CapturedAt);

            if (captured == DateTime.MinValue)
                return Reject(key, "capturedAt is not a timestamp, so nothing could rank this set against the one held");

            lock (_lock)
            {
                if (!_loaded) Load();

                // Against the COMPLETE set only, never against what is staged: the second floor of one
                // capture carries the same CapturedAt as the first and must not be refused as "not
                // newer". Equal is refused, which also stops a client re-posting a set the host already
                // has - the client compares stamps before uploading, so an equal CapturedAt here means
                // the set is already served.
                if (_sets.TryGetValue(key, out var held) && ParseStamp(held.Meta.CapturedAt) >= captured)
                    return Reject(key, "older than the set on the host");

                var staging = StagingFolder(key, meta.CapturedAt);
                var staged = FilesByLevel(staging);

                // What this post REPLACES, which is the only thing either budget may discount: a floor
                // posted twice overwrites its own staged copy, so counting the old one as well would
                // refuse a client that simply retried. Everything else already staged still counts,
                // which is what stops a set being walked past the budget one floor at a time.
                var mine = staged.TryGetValue(request.Level, out var already) ? SizeOf(already) : 0;
                var setBytes = staged.Sum(entry => SizeOf(entry.Value)) - mine;

                if (setBytes + bytes.Length > MaxBytesPerMap)
                    return Reject(key,
                        $"this capture would be {Mb(setBytes + bytes.Length)} MB, past the " +
                        $"{Mb(MaxBytesPerMap)} MB one map may hold");

                // The whole store, counting what is staged as well as what is served: during an upload
                // the disk really does hold both - the set being replaced is still being served - and a
                // peer that uploads and abandons sets would otherwise be bounded by nothing at all.
                var total = _sets.Values.Sum(s => s.Bytes) + IncomingBytes() - mine;

                if (total + bytes.Length > MaxBytesTotal)
                    return Reject(key,
                        $"the host already holds {Mb(total)} MB of map pictures, at the {Mb(MaxBytesTotal)} MB limit");

                try
                {
                    System.IO.Directory.CreateDirectory(staging);

                    var wanted = System.IO.Path.Combine(staging, StagedName(request.Level, format));

                    // ONE file per level, whatever format it arrives in. A floor first posted as a JPEG
                    // and then re-posted as a PNG would otherwise leave both on disk under one level, and
                    // the completion below picks by level - so half the time it would promote the stale
                    // bytes, with every log line saying the set was stored.
                    foreach (var other in System.IO.Directory.EnumerateFiles(
                                 staging, request.Level.ToString(CultureInfo.InvariantCulture) + ".*"))
                        if (!string.Equals(other, wanted, StringComparison.OrdinalIgnoreCase))
                            System.IO.File.Delete(other);

                    WriteAtomic(wanted, bytes);
                }
                catch (Exception ex)
                {
                    // Not a rejection of the capture: nothing is wrong with what was sent, the host
                    // could not write it. Said once per map per reason like any other refusal, and the
                    // client is told so it can stop rather than post the rest of the set.
                    return Reject(key, $"the host could not store the picture ({ex.Message})");
                }

                // Re-read from disk rather than adding one to a count: the staged set is the authority
                // on what has arrived, which is what makes a server restarted mid-upload resume
                // instead of starting over.
                staged = FilesByLevel(staging);

                var missing = meta.Floors.Count(f => !staged.ContainsKey(f.Level));

                if (missing > 0)
                {
                    _logger.Detail(
                        $"Quest Tracker: holding {staged.Count} of {meta.Floors.Count} floor(s) of the map " +
                        $"picture set for '{key}' captured {Clip(meta.CapturedAt, MaxFreeTextLength)}.");

                    return new MapUploadResponse
                    {
                        Outcome = "stored",
                        Reason = $"waiting for {missing} more floor(s)",
                        FloorsHeld = staged.Count
                    };
                }

                return Promote(key, meta, staged, staging, Clip(request.ClientVersion ?? "", MaxFreeTextLength));
            }
        }

        /// <summary>What this host holds, answered from memory: a client asks for this on every Maps
        /// tab open, and the answer is a few hundred bytes per map.</summary>
        public MapIndexDto Index()
        {
            var dto = new MapIndexDto
            {
                SchemaVersion = MapIndexDto.CurrentSchemaVersion,
                AcceptsUploads = AcceptsUploads
            };

            lock (_lock)
            {
                if (!_loaded) Load();

                foreach (var set in _sets.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
                    dto.Maps.Add(new MapIndexEntryDto
                    {
                        Map = set.Key,
                        Stamp = set.Stamp,
                        CapturedAt = set.Meta.CapturedAt,
                        Bytes = set.Bytes,
                        Meta = set.Meta
                    });
            }

            return dto;
        }

        /// <summary>One floor's picture, read from disk on every request and never cached: the
        /// pictures are megabytes, a client downloads each one exactly once, and the stamp in the
        /// index is what stops it asking again.</summary>
        public MapImageDto Image(MapImageRequest? request)
        {
            var dto = new MapImageDto();

            if (request == null || !ZoneStore.IsValidMapName(request.Map)) return dto;

            var key = ZoneStore.Canonical(request.Map);

            dto.Map = key;
            dto.Level = request.Level;

            lock (_lock)
            {
                if (!_loaded) Load();

                if (!_sets.TryGetValue(key, out var set)) return dto;

                var floor = set.Meta.Floors.FirstOrDefault(f => f.Level == request.Level);

                if (floor == null) return dto;

                try
                {
                    // The name was written by Promote or checked by Load, so it is a bare file name
                    // inside this map's folder. Nothing here comes from the request but the level.
                    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, key, floor.File));

                    dto.Stamp = set.Stamp;
                    dto.Format = Format(System.IO.Path.GetExtension(floor.File).TrimStart('.')) ?? "";
                    dto.ImageBase64 = Convert.ToBase64String(bytes);
                }
                catch (Exception ex)
                {
                    // An empty answer is what the client already handles - it draws the bounds-only
                    // backdrop - so a picture that cannot be read degrades rather than throwing on the
                    // game's main thread. Said once, because the client will ask again.
                    WarnOnce(key, $"could not read the picture for level {request.Level} ({ex.Message})");

                    dto.Stamp = "";
                    dto.Format = "";
                    dto.ImageBase64 = "";
                }
            }

            return dto;
        }

        // ---------------------------------------------------------------------------------------
        // Completing a set
        // ---------------------------------------------------------------------------------------

        /// <summary>Moves a complete staged set into place, and makes it the one this host serves.
        ///
        /// Order matters: pictures first, each one written beside its name and moved over it, then the
        /// files the new set does not name, and the meta LAST. The meta is what names the pictures, so
        /// this order means a failure part-way through never leaves a meta pointing at a picture that
        /// was never written - the case that would serve a client a map with a hole in it and a stamp
        /// claiming it was whole.
        ///
        /// WHAT IT DOES NOT PROMISE, said out loud because a folder is not a database: a crash in the
        /// gap between the last picture and the meta leaves the new pictures beside the old meta. The
        /// loader then skips the folder entirely if the old meta names a file the new set replaced
        /// under a different name, and otherwise reads the OLD meta over the NEW pictures - which is
        /// wrong if the extent changed between the two captures, and merely stale if it did not.
        /// Closing that gap needs a directory swap, which Windows cannot do atomically either, so the
        /// gap is accepted: it is microseconds wide, it needs a crash inside it, and the repair is one
        /// more capture of that map.
        ///
        /// Caller holds the lock.</summary>
        private MapUploadResponse Promote(
            string key, MapCaptureMetaDto meta, Dictionary<int, string> staged, string staging, string clientVersion)
        {
            var ordered = meta.Floors.OrderBy(f => f.Level).ToList();
            var target = System.IO.Path.Combine(Folder, key);
            var payloads = new List<byte[]>(ordered.Count);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;

            try
            {
                System.IO.Directory.CreateDirectory(target);

                // The CANONICAL name, so the stored file agrees with the folder it sits in and with the
                // index entry that serves it: a Factory night capture is stored as factory4_day's
                // picture, and a meta still claiming factory4_night would have package.ps1's gates and
                // a reader of the file disagreeing about which map this is.
                meta.Map = key;

                foreach (var floor in ordered)
                {
                    var source = staged[floor.Level];
                    var format = Format(System.IO.Path.GetExtension(source).TrimStart('.')) ?? "jpg";
                    var data = System.IO.File.ReadAllBytes(source);

                    // THE CLIENT'S FILE NAME IS DISCARDED HERE, always: a name from a peer is a path,
                    // and this one is joined onto a folder every client then reads from.
                    floor.File = $"{key}-{floor.Level.ToString(CultureInfo.InvariantCulture)}.{format}";

                    WriteAtomic(System.IO.Path.Combine(target, floor.File), data);

                    written.Add(floor.File);
                    payloads.Add(data);
                    bytes += data.Length;
                }

                var metaName = MetaName(key);

                // Whatever the previous set left that this one does not name - a fourth floor on a map
                // that now measures three, a .png replaced by a .jpg - goes before the meta lands, so
                // the folder never holds a picture nothing points at.
                foreach (var path in System.IO.Directory.EnumerateFiles(target))
                {
                    var name = System.IO.Path.GetFileName(path);

                    if (written.Contains(name) || name.Equals(metaName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { System.IO.File.Delete(path); } catch { /* a leftover file is not a failure */ }
                }

                var metaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, FileOptions));

                WriteAtomic(System.IO.Path.Combine(target, metaName), metaBytes);

                // Over the bytes as they were WRITTEN, in level order, so the boot that reads this
                // folder back computes the same value from the same files. That identity is what makes
                // the stamp worth comparing at all: it is never persisted, because a stamp file could
                // disagree with the pictures beside it.
                var set = new StoredSet
                {
                    Key = key,
                    Stamp = StampOf(metaBytes, payloads),
                    Bytes = bytes,
                    Meta = meta
                };

                _sets[key] = set;

                // Every staging folder for this map, not only this one: an abandoned earlier attempt is
                // exactly what the disk should not keep once a newer set is served.
                DropStaging(key);

                _logger.Info(
                    $"Quest Tracker: map picture set for '{key}' stored - {ordered.Count} floor(s), " +
                    $"{Mb(bytes)} MB, captured {Clip(meta.CapturedAt, MaxFreeTextLength)} by client " +
                    $"{(clientVersion.Length == 0 ? "unknown" : clientVersion)}.");

                return new MapUploadResponse { Outcome = "complete", FloorsHeld = ordered.Count };
            }
            catch (Exception ex)
            {
                // The set stays staged. Nothing was promised to a client, the previous set is still
                // being served - the meta is written last precisely so that is true - and the next
                // post of any floor of this capture tries the move again.
                WarnOnce(key, $"could not complete the picture set ({ex.Message})");

                return new MapUploadResponse
                {
                    Outcome = "rejected",
                    Reason = $"the host could not store the set ({ex.Message})",
                    FloorsHeld = staged.Count
                };
            }
        }

        // ---------------------------------------------------------------------------------------
        // Reading what is already here
        // ---------------------------------------------------------------------------------------

        /// <summary>Rebuilds the stamp cache from the folders. Caller holds the lock.</summary>
        private void Load()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            _sets.Clear();
            _loaded = true;

            try
            {
                if (System.IO.Directory.Exists(Folder))
                    foreach (var dir in System.IO.Directory.EnumerateDirectories(Folder))
                    {
                        var name = System.IO.Path.GetFileName(dir);

                        // Reuses the validation the upload route already ran rather than trusting the
                        // directory, which is also what skips .incoming without naming it.
                        if (!ZoneStore.IsValidMapName(name)) continue;

                        var key = ZoneStore.Canonical(name);
                        var set = ReadSet(key, dir);

                        if (set != null) _sets[key] = set;
                    }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Quest Tracker: could not read the map pictures folder ({ex.Message}) - the host serves none this boot.");
            }

            DropStaleStaging();

            _logger.Detail(
                $"Quest Tracker: the host holds {_sets.Count} map picture set(s) " +
                $"({Mb(_sets.Values.Sum(s => s.Bytes))} MB, read in {clock.ElapsedMilliseconds:N0} ms).");
        }

        /// <summary>One folder read back as a set, or null when it does not hold a complete one.
        ///
        /// Everything here is a refusal, never a repair. A folder whose meta names a picture that is
        /// not there is a set half-written or half-copied, and serving the floors that DO exist would
        /// give a client a map with a hole in it and a stamp claiming it was whole.</summary>
        private StoredSet? ReadSet(string key, string dir)
        {
            try
            {
                var metaPath = System.IO.Directory.EnumerateFiles(dir, "*.map.json").FirstOrDefault();

                if (metaPath == null) return null;

                var metaBytes = System.IO.File.ReadAllBytes(metaPath);
                var meta = JsonSerializer.Deserialize<MapCaptureMetaDto>(metaBytes, FileOptions);

                if (meta == null) return null;

                // Written by a newer server, or captured by a newer client: skipped exactly as
                // ZoneStore skips a newer zone file, and for the same reason - a v2 meta could mean
                // the picture covers something v1 never recorded, and drawing it would put every pin
                // on the map somewhere plausible and wrong.
                if (meta.SchemaVersion != MapCaptureMetaDto.CurrentSchemaVersion)
                {
                    _logger.Warning(
                        $"Quest Tracker: maps/{key}/{System.IO.Path.GetFileName(metaPath)} is schema " +
                        $"v{meta.SchemaVersion}, not the v{MapCaptureMetaDto.CurrentSchemaVersion} this server " +
                        "serves - skipped, so this host has no picture for that map.");
                    return null;
                }

                if (meta.Floors == null || meta.Floors.Count == 0) return null;

                var ordered = meta.Floors.OrderBy(f => f.Level).ToList();
                var payloads = new List<byte[]>(ordered.Count);
                long bytes = 0;

                foreach (var floor in ordered)
                {
                    // The name is about to be joined onto this folder's path. It was written by
                    // Promote, so a name that fails this was hand-edited or copied in from elsewhere -
                    // and "..\\..\\something" is what that check is for.
                    if (floor.File == null || !StoredFileName.IsMatch(floor.File))
                    {
                        _logger.Warning(
                            $"Quest Tracker: maps/{key} names a picture file this server will not read " +
                            $"('{Clip(floor.File ?? "", MaxFreeTextLength)}') - the whole set is skipped.");
                        return null;
                    }

                    var path = System.IO.Path.Combine(dir, floor.File);

                    if (!System.IO.File.Exists(path))
                    {
                        _logger.Warning(
                            $"Quest Tracker: maps/{key} is missing {floor.File}, so the set is incomplete - " +
                            "skipped. Capture the map again, or copy the whole folder across.");
                        return null;
                    }

                    var data = System.IO.File.ReadAllBytes(path);
                    payloads.Add(data);
                    bytes += data.Length;
                }

                return new StoredSet
                {
                    Key = key,
                    Stamp = StampOf(metaBytes, payloads),
                    Bytes = bytes,
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                _logger.Warning($"Quest Tracker: could not read the map pictures in maps/{key} ({ex.Message}) - skipped.");
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Validation
        // ---------------------------------------------------------------------------------------

        /// <summary>Whether a capture meta describes one real projection of one real map, and if not,
        /// the reason to refuse the post with.
        ///
        /// Drops, not repairs, with two exceptions marked at their check: a floor name and a label are
        /// captions, and clipping one changes nothing about where a pixel lands. Everything else -
        /// the rectangle, the scale, the pixel sizes - decides where a pin is drawn, and a repaired
        /// value there is a picture nobody measured with every pin confidently in the wrong place.
        ///
        /// Mutates the meta it is given, which is this request's own object and nothing else's.</summary>
        private static bool MetaIsUsable(string key, MapCaptureMetaDto meta, out string problem)
        {
            problem = "";

            if (!ZoneStore.IsValidMapName(meta.Map) ||
                !string.Equals(ZoneStore.Canonical(meta.Map), key, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"the meta names map '{Clip(meta.Map ?? "", MaxFreeTextLength)}', not '{key}'";
                return false;
            }

            var extent = meta.Extent;

            if (extent == null)
            {
                problem = "the meta has no extent, so nothing says what stretch of world the picture covers";
                return false;
            }

            if (!InWorld(extent.MinX) || !InWorld(extent.MinZ) || !InWorld(extent.MaxX) || !InWorld(extent.MaxZ))
            {
                problem = $"its corners are not finite metres inside +/-{MaxCoordinate:0} m";
                return false;
            }

            // Not <=: a rectangle with no width divides by zero on the client, and the pixel check
            // below would pass on any picture at all.
            if (extent.MinX >= extent.MaxX || extent.MinZ >= extent.MaxZ)
            {
                problem = "its minimum corner is not below its maximum corner";
                return false;
            }

            if (!Finite(meta.Rotation) || Math.Abs(meta.Rotation) > 360f)
            {
                problem = $"its rotation is {meta.Rotation}, which is not a number of degrees";
                return false;
            }

            if (!Finite(meta.PxPerMetre) || meta.PxPerMetre <= 0f)
            {
                problem = $"its pxPerMetre is {meta.PxPerMetre}, which is not a scale";
                return false;
            }

            if (meta.TileSize < 0 || meta.TileSize > MaxTileSize)
            {
                problem = $"its tileSize is {meta.TileSize}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(meta.CapturedAt) || meta.CapturedAt.Length > MaxFreeTextLength)
            {
                problem = "its capturedAt is not a timestamp";
                return false;
            }

            meta.ModVersion = Clip((meta.ModVersion ?? "").Trim(), MaxFreeTextLength);
            meta.TimeOfDay = Clip((meta.TimeOfDay ?? "").Trim(), MaxNameLength);

            meta.Floors ??= new List<MapCaptureFloorDto>();

            if (meta.Floors.Count == 0)
            {
                problem = "it names no floors";
                return false;
            }

            if (meta.Floors.Count > MaxFloors)
            {
                problem = $"it names {meta.Floors.Count} floors, past the {MaxFloors} a map may have";
                return false;
            }

            // The rectangle and the scale together say how large the picture must be. This is the one
            // check that compares two numbers a client worked out separately, which is what makes it
            // able to catch a capture rendered at one scale and described at another - the failure
            // that draws a whole map at the wrong size with no other symptom.
            //
            // Axis-aligned, because rotation is 0 in this release: a rotated capture swaps the two
            // expected sides and fails this, which is the loud answer rather than the wrong one.
            var expectedWidth = (int)Math.Ceiling((extent.MaxX - extent.MinX) * meta.PxPerMetre);
            var expectedHeight = (int)Math.Ceiling((extent.MaxZ - extent.MinZ) * meta.PxPerMetre);

            var levels = new HashSet<int>();

            foreach (var floor in meta.Floors)
            {
                if (floor == null)
                {
                    problem = "one of its floors is empty";
                    return false;
                }

                if (!levels.Add(floor.Level))
                {
                    problem = $"two of its floors are both level {floor.Level}";
                    return false;
                }

                // Clipped rather than refused, as ZoneStore clips a floor name: it is a caption on a
                // layer button and it decides nothing. Empty is still fatal - an unnamed layer is a
                // button nobody can read.
                floor.Name = Clip((floor.Name ?? "").Trim(), MaxNameLength);

                if (floor.Name.Length == 0)
                {
                    problem = $"its floor at level {floor.Level} has no name";
                    return false;
                }

                // Bounded, then thrown away by Promote. Bounded anyway because it is logged on the way
                // back in from disk, and a field that is only ever safe because of what happens later
                // is a field that stops being safe when that changes.
                floor.File = Clip((floor.File ?? "").Trim(), MaxFreeTextLength);

                if (floor.Width < 1 || floor.Height < 1 ||
                    floor.Width > MaxFloorPixels || floor.Height > MaxFloorPixels)
                {
                    problem = $"floor '{floor.Name}' is {floor.Width}x{floor.Height} px, which is not a picture " +
                              $"up to {MaxFloorPixels} px a side";
                    return false;
                }

                if (Math.Abs(floor.Width - expectedWidth) > PixelTolerance ||
                    Math.Abs(floor.Height - expectedHeight) > PixelTolerance)
                {
                    problem = $"floor '{floor.Name}' is {floor.Width}x{floor.Height} px, but its extent at " +
                              $"{meta.PxPerMetre:0.###} px/m is {expectedWidth}x{expectedHeight} px";
                    return false;
                }

                if (!InWorld(floor.MinY) || !InWorld(floor.MaxY) || floor.MinY >= floor.MaxY)
                {
                    problem = $"floor '{floor.Name}' spans {floor.MinY:0.#}..{floor.MaxY:0.#} m, which is not a height band";
                    return false;
                }
            }

            meta.Labels ??= new List<MapLabelDto>();

            if (meta.Labels.Count > MaxLabels)
            {
                problem = $"it carries {meta.Labels.Count} labels, past the {MaxLabels} a map may have";
                return false;
            }

            // Labels are captions too, so a bad one is dropped and the set is kept: an exfil name with
            // a newline in it is a forged log line, and one at NaN is a caption at the edge of the
            // world, but neither is a reason to refuse a measured picture.
            meta.Labels.RemoveAll(label =>
            {
                if (label == null) return true;

                label.Text = Clip((label.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(), MaxLabelLength);

                return label.Text.Length == 0 || !InWorld(label.X) || !InWorld(label.Z);
            });

            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Staging, names and small helpers
        // ---------------------------------------------------------------------------------------

        /// <summary>Where one capture's floors wait. Named for the map and a hash of the capture's own
        /// timestamp, so two captures of one map never share a folder and a timestamp's colons never
        /// reach a file name.</summary>
        private static string StagingFolder(string key, string capturedAt) =>
            System.IO.Path.Combine(IncomingFolder, $"{key}-{ShortHash(capturedAt ?? "")}");

        /// <summary>Hex characters ShortHash produces. Read by DropStaging, which has to tell this
        /// map's staging from that of a map whose name merely starts the same way.</summary>
        private const int StagingHashLength = 12;

        private static string StagedName(int level, string format) =>
            $"{level.ToString(CultureInfo.InvariantCulture)}.{format}";

        private static string MetaName(string key) => key + ".map.json";

        /// <summary>The staged pictures of one capture, by level. Empty when nothing is staged - which
        /// is also what a first post sees.</summary>
        private static Dictionary<int, string> FilesByLevel(string staging)
        {
            var found = new Dictionary<int, string>();

            if (!System.IO.Directory.Exists(staging)) return found;

            foreach (var path in System.IO.Directory.EnumerateFiles(staging))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var extension = System.IO.Path.GetExtension(path).TrimStart('.');

                if (Format(extension) == null) continue;

                if (int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level))
                    found[level] = path;
            }

            return found;
        }

        /// <summary>Every staging folder for this map, gone. Called once a set is served.
        ///
        /// The length test is not belt and braces: a map name may contain a hyphen (SafeName allows
        /// one), so the glob "big-*" alone would also match the staging of a map called "big-map", and
        /// completing one map's set would then throw away another map's half-finished upload.</summary>
        private static void DropStaging(string key)
        {
            if (!System.IO.Directory.Exists(IncomingFolder)) return;

            var prefix = key + "-";

            foreach (var dir in System.IO.Directory.EnumerateDirectories(IncomingFolder, prefix + "*"))
            {
                var name = System.IO.Path.GetFileName(dir);

                if (name.Length != prefix.Length + StagingHashLength) continue;

                try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* it will be reused or replaced */ }
            }
        }

        /// <summary>Staging folders nobody finished, dropped at boot.
        ///
        /// Without this a client that left the raid between two floors leaves those floors on the host
        /// for good: nothing completes them, and they count against the store's total until someone
        /// deletes the folder by hand. A day is long enough that an upload interrupted by a server
        /// restart still completes - the client re-posts the whole set anyway - and short enough that
        /// abandoned pieces do not accumulate. Caller holds the lock.</summary>
        private void DropStaleStaging()
        {
            if (!System.IO.Directory.Exists(IncomingFolder)) return;

            var dropped = 0;

            foreach (var dir in System.IO.Directory.EnumerateDirectories(IncomingFolder))
                try
                {
                    var newest = System.IO.Directory.EnumerateFiles(dir)
                        .Select(f => new System.IO.FileInfo(f).LastWriteTimeUtc)
                        .DefaultIfEmpty(System.IO.Directory.GetLastWriteTimeUtc(dir))
                        .Max();

                    if (DateTime.UtcNow - newest < TimeSpan.FromDays(1)) continue;

                    System.IO.Directory.Delete(dir, recursive: true);
                    dropped++;
                }
                catch { /* a staging folder that cannot be read is one for the next boot */ }

            if (dropped > 0)
                _logger.Detail($"Quest Tracker: dropped {dropped} unfinished map picture upload(s) older than a day.");
        }

        private static long IncomingBytes()
        {
            if (!System.IO.Directory.Exists(IncomingFolder)) return 0;

            try
            {
                return System.IO.Directory
                    .EnumerateFiles(IncomingFolder, "*", System.IO.SearchOption.AllDirectories)
                    .Sum(SizeOf);
            }
            catch
            {
                return 0;
            }
        }

        private static long SizeOf(string path)
        {
            try { return new System.IO.FileInfo(path).Length; } catch { return 0; }
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            // Written beside the file and moved over it, exactly as ZoneStore.Save writes a zone file:
            // a write cut short by a crash or a full disk leaves the previous picture intact instead of
            // a truncated one every client then fails to decode.
            var temp = path + ".tmp";
            System.IO.File.WriteAllBytes(temp, bytes);
            System.IO.File.Move(temp, path, overwrite: true);
        }

        /// <summary>sha256 over the meta's bytes and every picture's bytes in level order, hex. The
        /// order and the exact bytes are what make this reproducible from the folder at the next
        /// boot; nothing about it may depend on how a request happened to be serialised.</summary>
        private static string StampOf(byte[] metaBytes, IEnumerable<byte[]> floorsInLevelOrder)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(metaBytes);

            foreach (var floor in floorsInLevelOrder) hash.AppendData(floor);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string ShortHash(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

            return Convert.ToHexString(bytes, 0, 6).ToLowerInvariant();
        }

        /// <summary>"jpg" or "png" for anything a client or a file extension could reasonably call
        /// them, and null for anything else. One place, so the request, the staged file's extension
        /// and the served file's extension cannot disagree.</summary>
        private static string? Format(string? claimed)
        {
            var name = (claimed ?? "").Trim().ToLowerInvariant();

            return name switch
            {
                "jpg" or "jpeg" => "jpg",
                "png" => "png",
                _ => null
            };
        }

        private static bool MagicMatches(string format, byte[] bytes) => format switch
        {
            // SOI plus the first byte of the next marker, which is what every JPEG encoder writes.
            "jpg" => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "png" => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E &&
                     bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A &&
                     bytes[7] == 0x0A,
            _ => false
        };

        /// <summary>A capture's timestamp as a UTC instant, or DateTime.MinValue when it is not one -
        /// the same shape, and the same reasoning, as ZoneStore.ParseSampledAt: this is asked both of
        /// a fresh upload, where MinValue is refused, and of a stored set's meta, where a file
        /// hand-edited into nonsense should simply lose to whatever arrives next.</summary>
        private static DateTime ParseStamp(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxFreeTextLength) return DateTime.MinValue;

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : DateTime.MinValue;
        }

        private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

        private static string Mb(long bytes) => (bytes / 1_048_576d).ToString("0.0", CultureInfo.InvariantCulture);

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool InWorld(double value) =>
            Finite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;

        private static bool InWorld(float value) =>
            Finite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;

        // ---------------------------------------------------------------------------------------
        // Saying so, once
        // ---------------------------------------------------------------------------------------

        /// <summary>Refuses one post and logs the reason once per map per reason.
        ///
        /// Deduped and capped for the reason QuestTreeRouter's own rejection set states: the upload
        /// route is unauthenticated HTTP that every Fika peer can post to, so a client in a loop must
        /// not be able to fill a rolling log and rotate the real diagnostics away. WARNING, not Info:
        /// unlike a preset decline, nobody pressed a button for this - a refused picture is a capture
        /// the host will never serve, and it should be visible on a default install.</summary>
        private MapUploadResponse Reject(string map, string reason)
        {
            WarnOnce(map, reason);

            return new MapUploadResponse { Outcome = "rejected", Reason = reason };
        }

        private void WarnOnce(string map, string reason)
        {
            bool first;

            lock (_rejectionsLogged)
                first = _rejectionsLogged.Count < MaxRejectionsLogged && _rejectionsLogged.Add($"{map}|{reason}");

            if (first) _logger.Warning($"Quest Tracker: refused a map picture for '{map}' - {reason}.");
        }

        /// <summary>The decline, said once per map per session and at Detail: it is not a fault, it is
        /// this host's settled policy, and the boot line above already stated it at Information. One
        /// line per map exists so that a host wondering why a player's capture never arrived can see
        /// which maps were offered.</summary>
        private void DeclineOnce(string map)
        {
            bool first;

            lock (_declinesLogged)
                first = _declinesLogged.Count < MaxRejectionsLogged && _declinesLogged.Add(map);

            if (first)
                _logger.Detail(
                    $"Quest Tracker: declined a map picture upload for '{map}' - {AcceptVariable} is not set on this host.");
        }
    }
}
