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
    /// cref="MapIndexEntryDto.Stamp"/> is sha256 over the stored meta's bytes, every picture's bytes
    /// in level order and the mesh's bytes if there is one, so it changes when the set changes and not
    /// otherwise. It is computed on completion and rebuilt at boot by reading the folders, never
    /// persisted: a stamp file could disagree with the pictures beside it, and then a client would keep
    /// a stale map forever while both halves reported success.
    ///
    /// FOUR (1.19.0). A set may carry a MESH - the map's ground relief and building shells, which the
    /// Maps tab drapes the pictures over in 3D. It arrives on its own route
    /// (<see cref="AcceptMesh"/>), because it has no floor level, is deflated binary rather than a
    /// picture, and is four times a picture's size; and a set whose meta names one is NOT SERVED until
    /// it has arrived, so the meta a client reads never names a file this host does not hold. It is
    /// optional end to end: a capture from an older client, a set from DynamicMaps' artwork and a set
    /// this host took before 1.19.0 all have none, which is why nothing about it bumps a schema
    /// version. A mesh that never arrives leaves its floors staged, and they are dropped at the first
    /// boot more than a day later - exactly what happens to a half-finished picture set, and worth
    /// saying precisely: the sweep runs at construction (<see cref="DropStaleStaging"/>), so nothing
    /// expires while a server stays up.
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

        /// <summary>One mesh file's ceiling. Customs' relief alone is ~0.3 MB and its building shells
        /// are 2-3 MB; twelve is four times the largest thing phase 3C's triangle budget can produce,
        /// and it is a number a peer on an unauthenticated route cannot walk past one post at a
        /// time - there is exactly one mesh per capture.</summary>
        private const int MaxMeshBytes = 12 * 1024 * 1024;

        /// <summary>The mesh's base64 ceiling, checked BEFORE decoding, for
        /// <see cref="MaxEncodedChars"/>' reason.</summary>
        private const int MaxEncodedMeshChars = MaxMeshBytes / 3 * 4 + 1024;

        /// <summary>The most a mesh file may INFLATE to while its header is being checked. A mesh file is
        /// one deflate block, so a 12 MB body can legitimately hold tens of megabytes of quantised grid -
        /// and a hostile one can hold a thousand times that. The header parse below stops reading at
        /// this, which is what keeps a deflate bomb to a bounded read rather than a full disk of RAM.
        ///
        /// DELIBERATELY LOWER than the format's own theoretical ceiling, which is about 145 MB (8 bands x
        /// 4 M cells x 3 bytes = 96 MB, plus 4 M vertices x 6 bytes and 2 M triangles x 12 bytes = 48
        /// MB). Those caps are sized to "no count can ask the allocator for a silly number"; this one is
        /// sized to what this mod actually writes, which is 1.5 MB inflated for Customs at 2 m cells and
        /// 2.5 MB for Interchange's five bands. 64 MB is twenty-five times the largest real file and a
        /// read a host can afford on a request thread; a file past it is refused with the number in the
        /// message, so the day a 4 km map at 1 m cells exists, the log says exactly what to raise.</summary>
        private const long MaxDecompressedMeshBytes = 64L * 1024 * 1024;

        /// <summary>The scratch buffer size for one header walk, shared by every read in it. 64 KiB
        /// divides by both 2 and 4, so a chunk never splits a uint16 or a uint32 element.</summary>
        private const int MeshChunkBytes = 64 * 1024;

        /// <summary>The mesh format this server stores, and the only one it will take: the client's
        /// MapMeshFile.Version. NOT a reference to that class - the server cannot see the Unity
        /// assembly - so this is the one number both halves must be changed for together, which is
        /// why the magic below carries the same digit and is checked as well.</summary>
        private const int MeshVersion = 1;

        /// <summary>The four bytes a mesh file starts with, inside the deflate stream
        /// (MapMeshFile.Magic).</summary>
        private const string MeshMagic = "QTM1";

        /// <summary>The caps the mesh HEADER is checked against, mirroring MapMeshFile's own
        /// (MaxBands, MaxCellsPerBand, MaxVerticesTotal, MaxTriangles, MaxBuildings). Checked here so a
        /// file that no client could read is refused by the host rather than served to every client in
        /// the group; checked BEFORE any count is trusted, so a corrupt width fails in one line.</summary>
        private const int MaxMeshBands = 8;

        private const long MaxMeshCellsPerBand = 4_000_000L;

        private const int MaxMeshBuildings = 20_000;

        private const long MaxMeshVerticesTotal = 4_000_000L;

        private const long MaxMeshTriangles = 2_000_000L;

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
        /// like one: MaxFloors x MaxImageBytes PLUS <see cref="MaxMeshBytes"/> is EXACTLY this figure
        /// (8 x 2.5 MB + 12 MB = 32 MB), so as the constants stand today nothing can reach it. It is
        /// kept because the three numbers are set independently, and it is the one that would bite
        /// first if a later release raised the floor cap, the picture size or the mesh size. It rose
        /// from 20 MB with the mesh, by exactly the mesh's ceiling. The store's own total below is
        /// reachable - ten maps at a full 32 MB pass it - but only by writing 300 MB, so neither is
        /// exercised by the harness. Said here rather than discovered later.</summary>
        private const long MaxBytesPerMap = 32L * 1024 * 1024;

        /// <summary>The whole store's ceiling. ~26 floors of the 11 vanilla maps is 15-30 MB, so this
        /// is ten times a full set and still small enough that a peer cannot fill a host's disk.</summary>
        private const long MaxBytesTotal = 300L * 1024 * 1024;

        private const int MaxLabels = 200;
        private const int MaxLabelLength = 40;

        /// <summary>The only two kinds a stored label may claim, and the default anything else becomes -
        /// see the labels loop in <see cref="MetaIsUsable"/>. The same two words the client's writer
        /// emits (MapCapture.LabelKindExfil / LabelKindZone) and its reader matches, which is what makes
        /// the field mean the same thing on every machine in a group.</summary>
        private const string ExfilLabelKind = "exfil";

        private const string ZoneLabelKind = "zone";

        /// <summary>The most captures a set may claim to be merged from. A caption's bound, not a rule:
        /// it takes one raid to add one, so a hundred is already more than anybody will see, and a peer
        /// posting int.MaxValue should cost the caption rather than the picture.</summary>
        private const int MaxCaptureCount = 100;

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

        /// <summary>What a stored MESH file's name may be, checked on the way back in from disk exactly
        /// as <see cref="StoredFileName"/> is, and SEPARATE from it on purpose: a floor that named a
        /// .bin would still be refused, and a mesh block that named a .jpg would still be refused. The
        /// shape is the client's MapMeshFile.FileNameFor - <c>&lt;key&gt;-mesh.bin</c> - which is what
        /// both halves and package.ps1's layout gate spell.</summary>
        private static readonly Regex StoredMeshFileName =
            new(@"^[A-Za-z0-9_\-]{1,60}-mesh\.bin$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A sha256 as this store will store one: 64 hex digits, lower case on the way out.
        /// Checked on the way in because it is printed into log lines and written into a served
        /// meta.</summary>
        private static readonly Regex Sha256Hex =
            new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

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

            // The mesh block, separately and NOT as a refusal - see the method.
            DropUnusableMesh(key, meta);

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

                // TWO CLIENTS, ONE CAPTURE INSTANT. The staging folder is keyed on the map and the
                // capturedAt, so two clients that captured the same map in the same second share it - and
                // the staged meta is what the mesh route matches an arriving mesh against. Without this
                // check the later poster's meta would silently replace the earlier one's, and the set
                // would be served with THIS client's mesh over BOTH clients' pictures, under a stamp
                // claiming it was one capture. Refused rather than merged: the first capture to stage a
                // map at an instant owns that instant, the second client's own upload fails loudly, and
                // its next capture has a later timestamp and its own folder.
                //
                // Only the MESH identity is compared, not the whole meta: the pictures of one map at one
                // instant are interchangeable (the extent and the scale are checked against the meta on
                // every post anyway), while the mesh is a single file the set will be served with.
                var wantedMesh = ReadStagedMeta(staging);

                if (wantedMesh != null && !SameMesh(wantedMesh.Mesh, meta.Mesh))
                    return Reject(key,
                        $"a capture of that map at that instant is already staged wanting mesh " +
                        $"{Short(wantedMesh.Mesh?.Sha256 ?? "")}, and this post names {Short(meta.Mesh?.Sha256 ?? "")} - " +
                        "two clients captured it in the same second, so the later one is refused rather than mixed");

                var staged = FilesByLevel(staging);

                // What this post REPLACES, which is the only thing either budget may discount: a floor
                // posted twice overwrites its own staged copy, so counting the old one as well would
                // refuse a client that simply retried. Everything else already staged still counts,
                // which is what stops a set being walked past the budget one floor at a time.
                var mine = staged.TryGetValue(request.Level, out var already) ? SizeOf(already) : 0;

                // The mesh counts against the map's budget as soon as it is staged, exactly as a floor
                // does: it is on the disk and it is part of this set.
                var setBytes = staged.Sum(entry => SizeOf(entry.Value)) + SizeOf(MeshPath(staging)) - mine;

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

                    // The meta, staged beside the floors on EVERY post. Two things need it there and
                    // neither can be had any other way: the mesh route (which carries no meta of its
                    // own, so the staged copy is what tells it a mesh is expected and which sha it must
                    // have), and a server restarted mid-upload, which can then complete the set from
                    // the next post of any part of it rather than needing the floors again. Its name
                    // has an extension Format() does not know, which is what keeps FilesByLevel from
                    // ever reading it as a floor.
                    WriteAtomic(
                        System.IO.Path.Combine(staging, StagedMetaName),
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, FileOptions)));
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

                // Every floor is here. If the meta names a MESH, the set is still incomplete: it is
                // promoted by whichever post brings the last piece, and for a 1.19.0 capture that is
                // the mesh route. Answered as "stored", not as a failure - the client reads the reason
                // and posts the mesh next, and a set left waiting expires with its staging after a day
                // like any other half set.
                if (meta.Mesh != null && !MeshIsStaged(staging, meta.Mesh.Sha256))
                {
                    _logger.Detail(
                        $"Quest Tracker: holding all {staged.Count} floor(s) of the map picture set for '{key}' " +
                        $"captured {Clip(meta.CapturedAt, MaxFreeTextLength)} - waiting for its mesh.");

                    return new MapUploadResponse
                    {
                        Outcome = "stored",
                        Reason = "waiting for the mesh",
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
                        Meta = set.Meta,

                        // The same object the meta carries, so the two can never disagree about
                        // whether this set has a mesh - see MapIndexEntryDto.Mesh for why it is on the
                        // entry as well. Null here is what tells a client not to ask for one.
                        Mesh = set.Meta.Mesh
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

        /// <summary>
        /// Takes one capture's MESH, and says what became of it.
        ///
        /// The narrowest route in this class, on purpose: it can neither start a set nor change one. It
        /// only ever adds a mesh to a capture whose floors are already staged and whose staged meta
        /// NAMES that exact mesh by sha256 - so a peer cannot use it to put bytes on a host that
        /// nothing will ever read, and cannot use it to alter a set somebody else uploaded.
        ///
        /// Everything it checks, in the order that makes the expensive work last: the schema, the map
        /// name (the file's guard) and <paramref name="isRealLocation"/> (the answer's guard); the
        /// opt-in; the capture's timestamp against the served set; the encoded length BEFORE decoding;
        /// the decoded length and the sender's own count of it; the sha256 of the bytes against the one
        /// claimed; the staged capture's meta against that same sha; the FILE HEADER, so a file no
        /// client could read is refused here rather than served to the whole group; and the budgets.
        ///
        /// <paramref name="isRealLocation"/> is handed in for <see cref="Accept"/>'s reason.</summary>
        public MapMeshUploadResponse AcceptMesh(MapMeshUploadRequest? request, Func<string, bool>? isRealLocation)
        {
            if (request == null) return RejectMesh("?", "no body");

            if (request.SchemaVersion > MapMeshUploadRequest.SupportedSchemaVersion)
                return RejectMesh("?",
                    $"mesh upload schema v{request.SchemaVersion} is newer than the " +
                    $"v{MapMeshUploadRequest.SupportedSchemaVersion} this server reads - this server is older " +
                    "than the client, update the server half");

            if (!ZoneStore.IsValidMapName(request.Map)) return RejectMesh("?", "bad map name");
            if (isRealLocation != null && !isRealLocation(request.Map)) return RejectMesh("?", "unknown map");

            var key = ZoneStore.Canonical(request.Map);

            if (!AcceptsUploads)
            {
                DeclineOnce(key);

                return new MapMeshUploadResponse
                {
                    Accepted = false,
                    Reason = "this host does not accept map pictures from clients"
                };
            }

            var capturedAt = Clip(request.CapturedAt ?? "", MaxFreeTextLength);
            var captured = ParseStamp(capturedAt);

            if (captured == DateTime.MinValue)
                return RejectMesh(key, "the mesh names no capture timestamp, so nothing says which set it belongs to");

            var claimed = (request.Sha256 ?? "").Trim();

            if (!Sha256Hex.IsMatch(claimed))
                return RejectMesh(key, "the mesh's sha256 is not 64 hex digits");

            // THE CHEAP GATE, BEFORE THE BYTES ARE EVEN DECODED. Nothing below this line is work a
            // stranger can make this host do: a post for a capture nothing has staged, or for a capture
            // whose meta names a different mesh, is refused here having cost one directory probe and one
            // small JSON read - no 12 MB decode, no sha over it, no inflate of the header. It needs only
            // the CLAIMED sha, and the bytes are then held to that claim below, so the chain is
            // unbroken: claim matches the staged meta, bytes match the claim.
            //
            // Under the lock, and then RELEASED for the header walk further down, which is CPU work on a
            // body from the network and must not hold a lock the index route needs on the game's main
            // thread. The gate is re-run under the lock before anything is written, because the staged
            // set can complete or expire while the walk runs.
            lock (_lock)
            {
                if (!_loaded) Load();

                if (WantedMesh(key, capturedAt, captured, claimed, out var refusal) == null)
                    return RejectMesh(key, refusal);
            }

            var encoded = request.DataBase64 ?? "";

            if (encoded.Length == 0) return RejectMesh(key, "the post carries no mesh");

            if (encoded.Length > MaxEncodedMeshChars)
                return RejectMesh(key, $"the mesh is larger than the {Mb(MaxMeshBytes)} MB a map's mesh may be");

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return RejectMesh(key, "the mesh is not base64");
            }

            if (bytes.Length == 0) return RejectMesh(key, "the mesh decodes to nothing");

            if (bytes.Length > MaxMeshBytes)
                return RejectMesh(key,
                    $"the mesh is {bytes.Length:N0} bytes, past the {MaxMeshBytes:N0} a map's mesh may be");

            // Two numbers from one machine that disagree mean the file was not read whole. Cheap, and
            // it catches a truncated read on the SENDER, which the sha below would also catch but
            // without saying what happened.
            if (request.Bytes != bytes.Length)
                return RejectMesh(key,
                    $"the mesh says it is {request.Bytes:N0} bytes but {bytes.Length:N0} arrived");

            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (!string.Equals(actual, claimed, StringComparison.OrdinalIgnoreCase))
                return RejectMesh(key, "the mesh's bytes do not hash to the sha256 it was sent with");

            // The header, before anything is written: a file whose magic, version or counts are wrong
            // is one no client could draw, and storing it would serve it to every client in the group.
            if (!MeshHeaderIsUsable(bytes, out var problem)) return RejectMesh(key, problem);

            lock (_lock)
            {
                if (!_loaded) Load();

                // AGAIN, and not as ceremony: the gate above ran outside this lock and the header walk
                // took milliseconds, during which another client's post could have completed this set or
                // a boot could have swept it away. This is the check that decides what is written.
                var staged = WantedMesh(key, capturedAt, captured, actual, out var refusal);

                if (staged == null) return RejectMesh(key, refusal);

                var staging = StagingFolder(key, capturedAt);
                var floors = FilesByLevel(staging);
                var existing = SizeOf(MeshPath(staging));
                var setBytes = floors.Sum(entry => SizeOf(entry.Value));

                if (setBytes + bytes.Length > MaxBytesPerMap)
                    return RejectMesh(key,
                        $"this capture would be {Mb(setBytes + bytes.Length)} MB, past the " +
                        $"{Mb(MaxBytesPerMap)} MB one map may hold");

                var total = _sets.Values.Sum(s => s.Bytes) + IncomingBytes() - existing;

                if (total + bytes.Length > MaxBytesTotal)
                    return RejectMesh(key,
                        $"the host already holds {Mb(total)} MB of map pictures, at the {Mb(MaxBytesTotal)} MB limit");

                try
                {
                    // The sidecar goes AFTER the mesh, and any older one goes first: between the two
                    // writes there is no sidecar, so MeshIsStaged hashes the file instead of trusting a
                    // sha that belongs to bytes that are no longer there. A crash in that gap costs one
                    // hash, never a wrong answer.
                    try { System.IO.File.Delete(MeshShaPath(staging)); } catch { /* there may be none */ }

                    WriteAtomic(MeshPath(staging), bytes);
                    WriteAtomic(MeshShaPath(staging), Encoding.UTF8.GetBytes(actual));
                }
                catch (Exception ex)
                {
                    return RejectMesh(key, $"the host could not store the mesh ({ex.Message})");
                }

                // ONE line per accepted mesh, at Information like the stored set's: a host wondering why
                // a map draws flat wants to see this, and it is one line per capture rather than per
                // floor.
                _logger.Info(
                    $"Quest Tracker: map mesh for '{key}' stored - {Mb(bytes.Length)} MB, sha {Short(actual)}, " +
                    $"captured {capturedAt} by client " +
                    $"{(string.IsNullOrEmpty(request.ClientVersion) ? "unknown" : Clip(request.ClientVersion, MaxFreeTextLength))}.");

                var missing = staged.Floors.Count(f => !floors.ContainsKey(f.Level));

                if (missing > 0)
                    return new MapMeshUploadResponse
                    {
                        Accepted = true,
                        Reason = $"waiting for {missing} more floor(s)"
                    };

                // The mesh was the last piece. Promoted from the STAGED meta rather than from anything
                // in this request - this route is never told what a set looks like.
                var promoted = Promote(
                    key, staged, floors, staging, Clip(request.ClientVersion ?? "", MaxFreeTextLength));

                return promoted.Outcome == "complete"
                    ? new MapMeshUploadResponse { Accepted = true }
                    : new MapMeshUploadResponse { Accepted = false, Reason = promoted.Reason };
            }
        }

        /// <summary>One map's mesh file, read from disk on every request and never cached, for
        /// <see cref="Image"/>'s reason: it is megabytes, a client takes it once, and the stamp in the
        /// index is what stops it asking again.
        ///
        /// An EMPTY answer - no sha, no stamp, no data - is what a map with no mesh gets, and that is
        /// the ordinary case rather than an error: the client draws the 2D picture.</summary>
        public MapMeshDto MeshFile(MapMeshRequest? request)
        {
            var dto = new MapMeshDto();

            if (request == null || !ZoneStore.IsValidMapName(request.Map)) return dto;

            var key = ZoneStore.Canonical(request.Map);

            dto.Map = key;

            lock (_lock)
            {
                if (!_loaded) Load();

                if (!_sets.TryGetValue(key, out var set) || set.Meta.Mesh == null) return dto;

                var mesh = set.Meta.Mesh;

                // The name was written by Promote or checked by Load, so it is a bare file name inside
                // this map's folder. Nothing here comes from the request but the map.
                if (string.IsNullOrEmpty(mesh.File) || !StoredMeshFileName.IsMatch(mesh.File)) return dto;

                try
                {
                    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, key, mesh.File));

                    dto.Stamp = set.Stamp;
                    dto.Sha256 = mesh.Sha256;
                    dto.Bytes = bytes.Length;
                    dto.DataBase64 = Convert.ToBase64String(bytes);
                }
                catch (Exception ex)
                {
                    // An empty answer is what the client already handles - it installs the set without
                    // a mesh and draws it flat - so a mesh that cannot be read degrades rather than
                    // throwing on the game's main thread. Said once, because the client will ask again.
                    WarnOnce(key, $"could not read the mesh ({ex.Message})");

                    dto.Stamp = "";
                    dto.Sha256 = "";
                    dto.Bytes = 0;
                    dto.DataBase64 = "";
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
        /// MESH if the set has one, then the files the new set does not name, and the meta LAST. The
        /// meta is what names the pictures and the mesh, so this order means a failure part-way through
        /// never leaves a meta pointing at a file that was never written - the case that would serve a
        /// client a map with a hole in it and a stamp claiming it was whole. The mesh goes before the
        /// sweep for the same reason a picture does: the sweep deletes everything this method did not
        /// write, so a mesh written after it would be deleted by the next promotion of the same map,
        /// and a mesh written by the sweep's own loop would be a file nothing had checked.
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

                // The mesh, if this set has one: the same treatment a picture gets, in the same phase,
                // for the reason in this method's summary. Its name is THIS SERVER'S, like a floor's -
                // a name from a peer is a path - and its bytes are hashed again here, so a staged file
                // swapped between the check on the way in and this write is refused rather than served
                // under a sha nobody will be able to verify.
                byte[]? meshBytes = null;

                if (meta.Mesh != null)
                {
                    var source = MeshPath(staging);

                    if (!System.IO.File.Exists(source))
                        throw new System.IO.FileNotFoundException(
                            "the staged mesh is gone, so the set cannot be completed", source);

                    meshBytes = System.IO.File.ReadAllBytes(source);

                    var hash = Convert.ToHexString(SHA256.HashData(meshBytes)).ToLowerInvariant();

                    if (!string.Equals(hash, meta.Mesh.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new System.IO.InvalidDataException(
                            $"the staged mesh hashes to {Short(hash)}, not the {Short(meta.Mesh.Sha256)} its meta names");

                    meta.Mesh.File = MeshName(key);
                    meta.Mesh.Sha256 = hash;
                    meta.Mesh.Bytes = meshBytes.Length;

                    WriteAtomic(System.IO.Path.Combine(target, meta.Mesh.File), meshBytes);

                    written.Add(meta.Mesh.File);
                    bytes += meshBytes.Length;
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
                    Stamp = StampOf(metaBytes, payloads, meshBytes),
                    Bytes = bytes,
                    Meta = meta
                };

                _sets[key] = set;

                // Every staging folder for this map, not only this one: an abandoned earlier attempt is
                // exactly what the disk should not keep once a newer set is served.
                DropStaging(key);

                _logger.Info(
                    $"Quest Tracker: map picture set for '{key}' stored - {ordered.Count} floor(s)" +
                    $"{(meshBytes == null ? "" : $" and a {Mb(meshBytes.Length)} MB mesh")}, " +
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

                // The mesh, read back the same way and held to its own sha256 - the one field in the
                // meta that cannot be checked by looking at the file it describes. DROPPED rather than
                // fatal, which is the opposite of how a missing PICTURE is treated above, and
                // deliberately so: the mesh is optional by construction, so a set whose .bin was not
                // copied across still draws in 2D on every client, while refusing the whole folder would
                // lose a map over a file nothing needs. The block goes with it, so no client is told
                // about a mesh this host cannot serve.
                byte[]? meshBytes = null;

                if (meta.Mesh != null)
                {
                    var why = "";
                    var file = meta.Mesh.File ?? "";

                    if (!StoredMeshFileName.IsMatch(file))
                        why = $"names a mesh file this server will not read ('{Clip(file, MaxFreeTextLength)}')";
                    else if (!Sha256Hex.IsMatch(meta.Mesh.Sha256 ?? ""))
                        why = $"names a mesh with no usable sha256 ('{Clip(meta.Mesh.Sha256 ?? "", 72)}')";
                    else if (!System.IO.File.Exists(System.IO.Path.Combine(dir, file)))
                        why = $"is missing {file}";
                    else
                    {
                        meshBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(dir, file));

                        var hash = Convert.ToHexString(SHA256.HashData(meshBytes)).ToLowerInvariant();

                        if (!string.Equals(hash, meta.Mesh.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            why = $"holds a {file} that hashes to {Short(hash)}, not the " +
                                  $"{Short(meta.Mesh.Sha256!)} its meta names";
                            meshBytes = null;
                        }
                    }

                    if (why.Length > 0)
                    {
                        _logger.Warning(
                            $"Quest Tracker: maps/{key} {why} - the pictures are served without it, so that map " +
                            "draws flat. Capture it again, or copy the whole folder across.");

                        meta.Mesh = null;
                    }
                    else
                    {
                        bytes += meshBytes!.Length;
                    }
                }

                return new StoredSet
                {
                    Key = key,
                    Stamp = StampOf(metaBytes, payloads, meshBytes),
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

            // Carried, not ranked on, and bounded because they are stored and served back: the version
            // of a set is CapturedAt alone (the check further up), and these two only decide what the
            // credit line under the map says. A firstCapturedAt that is not a timestamp is blanked
            // rather than refused - the client then falls back to CapturedAt, which is the honest
            // answer for a meta that never had the field - and a count outside the floor and the
            // ceiling is clamped rather than dropped, since "3 captures" is a caption and a peer
            // claiming two billion of them should cost the caption, not the picture.
            meta.FirstCapturedAt = Clip((meta.FirstCapturedAt ?? "").Trim(), MaxFreeTextLength);

            if (meta.FirstCapturedAt.Length > 0 && ParseStamp(meta.FirstCapturedAt) == DateTime.MinValue)
                meta.FirstCapturedAt = "";

            meta.Captures = Math.Clamp(meta.Captures, 1, MaxCaptureCount);

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

                // One of two words, and NORMALISED rather than validated: the client's reader already
                // treats anything it does not recognise as a zone (MapCatalog.ReadLabels), so storing
                // a peer's arbitrary string would serve every other client a field it will read as
                // "zone" anyway - while leaving an unbounded string in the file and in the index. The
                // quieter of the two kinds is the safe default in both directions: a misread kind
                // costs a name its prominence, where the other way round would bury the extracts.
                label.Kind = string.Equals(label.Kind?.Trim(), ExfilLabelKind, StringComparison.OrdinalIgnoreCase)
                    ? ExfilLabelKind
                    : ZoneLabelKind;

                return label.Text.Length == 0 || !InWorld(label.X) || !InWorld(label.Z);
            });

            return true;
        }

        /// <summary>
        /// Drops a mesh block that describes nothing this host could serve, and says so once.
        ///
        /// A DROP rather than a refusal, which is the opposite of how every other field of the meta is
        /// treated, and the reason is the deadlock a refusal would be instead: the block is what makes a
        /// set WAIT for a mesh, so a block naming a sha256 nothing can ever match would leave the floors
        /// staged until they expired - the whole capture lost over an optional extra. Dropped, the set
        /// completes on its floors and the map draws flat, which is what it did before meshes existed.
        /// The mesh route then answers that post with "the staged capture's meta names no mesh", so the
        /// client hears about it too.
        ///
        /// Every value here is checked against the same caps the header parse applies to the file
        /// itself, because this half of the pair is what a client reads to decide whether to ask for the
        /// other half.
        ///
        /// <c>File</c> is the one field NOT validated as a name, and that is not an oversight: this
        /// server never joins it to a path. <see cref="Promote"/> overwrites it with
        /// <see cref="MeshName"/> before the meta is written, exactly as it overwrites a floor's, so the
        /// only value that ever reaches the disk or a client is this server's own. It is CLIPPED here
        /// because it is printed into a log line if the block is dropped, and it is held to
        /// <see cref="StoredMeshFileName"/> on the way back IN from disk (<see cref="ReadSet"/>), which
        /// is where a hand-edited meta could put a path. Caller holds nothing; this touches only the
        /// request's own meta.</summary>
        /// <param name="key">The canonical map name, for the line.</param>
        /// <param name="meta">The meta being stored. Its <c>Mesh</c> is nulled when unusable.</param>
        private void DropUnusableMesh(string key, MapCaptureMetaDto meta)
        {
            var mesh = meta.Mesh;

            if (mesh == null) return;

            mesh.File = Clip((mesh.File ?? "").Trim(), MaxFreeTextLength);
            mesh.Sha256 = (mesh.Sha256 ?? "").Trim().ToLowerInvariant();

            string? why = null;

            if (mesh.Version != MeshVersion)
                why = $"is version {mesh.Version}, not the v{MeshVersion} this server stores";
            else if (!Sha256Hex.IsMatch(mesh.Sha256))
                why = "carries no usable sha256, so no upload could ever be matched to it";
            else if (mesh.Bytes <= 0 || mesh.Bytes > MaxMeshBytes)
                why = $"claims {mesh.Bytes:N0} bytes, which is not a mesh up to {MaxMeshBytes:N0}";
            else if (mesh.Cells < 0 || mesh.Cells > MaxMeshBands * MaxMeshCellsPerBand)
                why = $"claims {mesh.Cells:N0} relief cells";
            else if (mesh.Triangles < 0 || mesh.Triangles > MaxMeshTriangles)
                why = $"claims {mesh.Triangles:N0} triangles";

            if (why == null) return;

            meta.Mesh = null;

            WarnOnce(key, $"its capture meta describes a mesh this host will not take - it {why}. The " +
                          "pictures are stored without it, so that map draws flat");
        }

        /// <summary>
        /// Whether a mesh file's HEADER is one this build would read, and if not, the reason to refuse
        /// the upload with. Nothing about the geometry is understood here - the server never draws a
        /// map - only that the file says it is a mesh of a version and a size a client can read.
        ///
        /// Ported from the client's MapMeshFile rather than calling it: that class lives in the Unity
        /// assembly, which this half cannot reference, so the layout is written down twice on purpose
        /// (see <see cref="MeshVersion"/>). The layout, little-endian, inside ONE raw deflate block
        /// starting at byte 0 of the file: magic "QTM1", int32 version, 4 x float64 extent, 2 x float32
        /// y range, int32 band count; per band int32 level, float32 cell metres, int32 width, int32
        /// height, uint16[w*h] heights, uint8[w*h] distances; int32 building count; per building int32
        /// key, int32 level, int32 vertex count, 3 x uint16[v], int32 index count, uint32[i] indices.
        ///
        /// WHY IT IS WORTH DOING AT ALL, when the client checks the same things again before it draws:
        /// this host hands the file to every other client in the group. A file that no reader will take
        /// is one that makes every one of them log a failure and draw flat - and the one machine that
        /// could have said so was this one, where the bytes arrived from an unauthenticated route.
        ///
        /// HOSTILE INPUT IS THE CASE, not the exception. Every count is tested against its cap BEFORE
        /// the bytes behind it are read, the inflated read is stopped at
        /// <see cref="MaxDecompressedMeshBytes"/> so a deflate bomb costs a bounded read rather than
        /// the machine, and the whole walk allocates ONE buffer however many counts the file declares:
        /// a 45 KB body can legitimately hold 20,000 buildings, and a buffer per count was 2.5 GB of
        /// allocation for it - on an unauthenticated route, which makes an allocation per declared thing
        /// a denial of service in its own right. So the buffer below is passed to every reader here, and
        /// nothing in this method allocates per band, per building or per count.
        ///
        /// WHAT IT READS RATHER THAN SKIPS, since the two are the same bytes either way: each building's
        /// Y array and index array are CHECKED - no index may be past its own building's vertex count, no
        /// vertex Y may be the NoHit code - because those are the two rules MapMeshFile.Read enforces
        /// that a header walk could have taken on trust. A file breaking either one parses perfectly and
        /// is refused by every client that downloads it, which is precisely the failure this method
        /// exists to keep off the host. X and Z are skipped: every sixteen-bit value in them is a
        /// position the reader accepts.</summary>
        /// <param name="bytes">The file, exactly as it arrived.</param>
        /// <param name="problem">Why it was refused. Empty when it was not.</param>
        private static bool MeshHeaderIsUsable(byte[] bytes, out string problem)
        {
            problem = "";

            try
            {
                using var raw = new System.IO.MemoryStream(bytes, writable: false);
                using var inflate = new System.IO.Compression.DeflateStream(
                    raw, System.IO.Compression.CompressionMode.Decompress);

                // The cap is enforced by the stream itself rather than by a count this method
                // remembers: every read below goes through it, so no later addition can forget.
                using var bounded = new BoundedStream(inflate, MaxDecompressedMeshBytes);
                using var reader = new System.IO.BinaryReader(bounded);

                // ONE buffer for the whole walk - see the summary. 64 KiB divides by 2 and by 4, so a
                // chunk never splits a uint16 or a uint32.
                var buffer = new byte[MeshChunkBytes];

                var magic = reader.ReadBytes(4);

                if (magic.Length != 4 ||
                    magic[0] != (byte)MeshMagic[0] || magic[1] != (byte)MeshMagic[1] ||
                    magic[2] != (byte)MeshMagic[2] || magic[3] != (byte)MeshMagic[3])
                {
                    problem = "the mesh does not start as a Quest Tracker mesh file does";
                    return false;
                }

                var version = reader.ReadInt32();

                if (version != MeshVersion)
                {
                    problem = $"the mesh is format version {version}, not the v{MeshVersion} this server stores";
                    return false;
                }

                var minX = reader.ReadDouble();
                var minZ = reader.ReadDouble();
                var maxX = reader.ReadDouble();
                var maxZ = reader.ReadDouble();
                var yMin = reader.ReadSingle();
                var yMax = reader.ReadSingle();

                if (!InWorld(minX) || !InWorld(minZ) || !InWorld(maxX) || !InWorld(maxZ) ||
                    minX >= maxX || minZ >= maxZ)
                {
                    problem = "the mesh's extent is not a rectangle of world metres";
                    return false;
                }

                if (!Finite(yMin) || !Finite(yMax) || yMin >= yMax)
                {
                    problem = "the mesh's height range is empty or not finite";
                    return false;
                }

                var bands = reader.ReadInt32();

                if (bands < 0 || bands > MaxMeshBands)
                {
                    problem = $"the mesh claims {bands:N0} bands, past the {MaxMeshBands} a map may have";
                    return false;
                }

                var levels = new HashSet<int>();

                for (var i = 0; i < bands; i++)
                {
                    var level = reader.ReadInt32();
                    var cellMetres = reader.ReadSingle();
                    var width = reader.ReadInt32();
                    var height = reader.ReadInt32();

                    if (!levels.Add(level))
                    {
                        problem = $"the mesh has two level {level} bands";
                        return false;
                    }

                    if (width <= 0 || height <= 0 || !Finite(cellMetres) || cellMetres <= 0f)
                    {
                        problem = $"the mesh's level {level} band is {width}x{height} cells at {cellMetres} m";
                        return false;
                    }

                    var cells = (long)width * height;

                    // Before the skip, not after: this is the line between a corrupt width and a read
                    // that runs until the bounded stream stops it.
                    if (cells > MaxMeshCellsPerBand)
                    {
                        problem = $"the mesh's level {level} band claims {cells:N0} cells, past the " +
                                  $"{MaxMeshCellsPerBand:N0} a band may have";
                        return false;
                    }

                    // Skipped, not checked: every sixteen-bit height is a value the reader accepts -
                    // 0xFFFF included, which is the legal "no ray hit this cell" code - and so is every
                    // distance byte. Read through the shared buffer, and insisted on in full, so a file
                    // that ends inside a grid fails here rather than having its next field read out of
                    // the middle of one.
                    Skip(bounded, cells * 3, buffer);   // uint16 height + uint8 distance per cell
                }

                var buildings = reader.ReadInt32();

                if (buildings < 0 || buildings > MaxMeshBuildings)
                {
                    problem = $"the mesh claims {buildings:N0} buildings, past the {MaxMeshBuildings:N0} a map may have";
                    return false;
                }

                long vertices = 0;
                long indices = 0;

                for (var i = 0; i < buildings; i++)
                {
                    reader.ReadInt32();     // the building's stable key
                    reader.ReadInt32();     // its band level

                    var vertexCount = reader.ReadInt32();

                    if (vertexCount < 0)
                    {
                        problem = $"the mesh's building {i} claims {vertexCount:N0} vertices";
                        return false;
                    }

                    vertices += vertexCount;

                    // The RUNNING total, which is the cap the per-building one leaves a hole in:
                    // 20,000 buildings of 2 M vertices each breaks no per-building rule at all.
                    if (vertices > MaxMeshVerticesTotal)
                    {
                        problem = $"the mesh's buildings claim {vertices:N0} vertices by building {i}, past the " +
                                  $"{MaxMeshVerticesTotal:N0} a map may have";
                        return false;
                    }

                    Skip(bounded, (long)vertexCount * 2, buffer);   // uint16 x - any value is a position

                    // The Y array is READ, because one value in it is illegal: NoHit (0xFFFF) is "no
                    // height", and a vertex carrying it becomes a NaN in a vertex buffer - which draws
                    // nothing, silently, on every client. MapMeshFile.Read refuses it, so a host that
                    // stored it would be serving a file every client throws away.
                    if (!YsAreHeights(bounded, vertexCount, buffer, out var badVertex))
                    {
                        problem = $"the mesh's building {i} has a vertex ({badVertex:N0}) with no height, which " +
                                  "would be a NaN in the geometry";
                        return false;
                    }

                    Skip(bounded, (long)vertexCount * 2, buffer);   // uint16 z

                    var indexCount = reader.ReadInt32();

                    if (indexCount < 0 || indexCount % 3 != 0)
                    {
                        problem = $"the mesh's building {i} claims {indexCount:N0} triangle indices";
                        return false;
                    }

                    indices += indexCount;

                    if (indices / 3 > MaxMeshTriangles)
                    {
                        problem = $"the mesh's buildings claim {indices / 3:N0} triangles by building {i}, past the " +
                                  $"{MaxMeshTriangles:N0} a map may have";
                        return false;
                    }

                    // And the indices are READ against this building's own vertex count, the other rule
                    // MapMeshFile.Read enforces: an index past it throws out of Unity's SetTriangles, so
                    // the client refuses the whole file. Same bytes either way - they have to be walked
                    // to reach the next building - so checking them costs a comparison per index.
                    if (!IndicesAreInRange(bounded, indexCount, vertexCount, buffer, out var badIndex,
                            out var badValue))
                    {
                        problem = $"the mesh's building {i} has index {badIndex:N0} pointing at vertex " +
                                  $"{badValue:N0} of {vertexCount:N0}";
                        return false;
                    }
                }

                // Nothing may follow the last building: a file with more in it is a file this build
                // does not understand, whatever the version said.
                if (bounded.ReadByte() >= 0)
                {
                    problem = "the mesh carries more data after its last building";
                    return false;
                }

                return true;
            }
            catch (System.IO.EndOfStreamException)
            {
                problem = "the mesh ends inside its own header or one of its grids";
                return false;
            }
            catch (System.IO.InvalidDataException ex)
            {
                // Both the deflate stream's own complaint and the bounded read's, which names its
                // limit - the message is the only thing that tells those two apart.
                problem = $"the mesh is not a readable deflate stream ({ex.Message})";
                return false;
            }
            catch (Exception ex)
            {
                // Anything else this walk can produce is still one answer: the file was not readable.
                problem = $"the mesh could not be read ({ex.GetType().Name})";
                return false;
            }
        }

        /// <summary>Reads and discards exactly <paramref name="count"/> bytes, insisting on all of them:
        /// a file that ends inside a grid must fail rather than leave the walk reading the next field out
        /// of the middle of one.
        ///
        /// The buffer is the CALLER'S, one per header walk. It used to be allocated here, which made a
        /// 45 KB post declaring 20,000 buildings cost 2.5 GB of allocation - three calls each - before
        /// the post had been shown to belong to anything. Measured, not imagined: the reviewer's harness
        /// drove exactly that.</summary>
        /// <param name="stream">The bounded, inflated stream.</param>
        /// <param name="count">Bytes to consume.</param>
        /// <param name="buffer">The walk's one scratch buffer.</param>
        private static void Skip(System.IO.Stream stream, long count, byte[] buffer)
        {
            if (count < 0) throw new System.IO.InvalidDataException("a negative length");

            while (count > 0)
            {
                var want = (int)Math.Min(buffer.Length, count);
                var read = stream.Read(buffer, 0, want);

                if (read <= 0) throw new System.IO.EndOfStreamException();

                count -= read;
            }
        }

        /// <summary>Fills <paramref name="count"/> bytes of the buffer, insisting on all of them. The one
        /// place the two checking readers below get their bytes, so neither can accidentally treat a short
        /// read as data.</summary>
        private static void FillExactly(System.IO.Stream stream, byte[] buffer, int count)
        {
            var done = 0;

            while (done < count)
            {
                var read = stream.Read(buffer, done, count - done);

                if (read <= 0) throw new System.IO.EndOfStreamException();

                done += read;
            }
        }

        /// <summary>Whether every value in a building's Y array is a height rather than the NoHit code,
        /// consuming exactly the array either way. The index of the first bad vertex comes back for the
        /// message.</summary>
        /// <param name="stream">The bounded, inflated stream.</param>
        /// <param name="vertexCount">Values to read.</param>
        /// <param name="buffer">The walk's one scratch buffer.</param>
        /// <param name="bad">The first vertex with no height, when this returns false.</param>
        private static bool YsAreHeights(
            System.IO.Stream stream, int vertexCount, byte[] buffer, out int bad)
        {
            bad = -1;

            var perChunk = buffer.Length / 2;
            var done = 0;

            while (done < vertexCount)
            {
                var take = Math.Min(perChunk, vertexCount - done);

                FillExactly(stream, buffer, take * 2);

                for (var i = 0; i < take; i++)
                    if (buffer[i * 2] == 0xFF && buffer[i * 2 + 1] == 0xFF)
                    {
                        // NOT returned early: the rest of the array still has to leave the stream, or the
                        // caller's next field is read out of the middle of it. The walk refuses the file
                        // anyway, so this costs one array's read on a file that is already doomed - but
                        // "the stream position is always where the layout says" is worth more than that.
                        if (bad < 0) bad = done + i;
                    }

                done += take;
            }

            return bad < 0;
        }

        /// <summary>Whether every index in a building's triangle array points at one of its own vertices,
        /// consuming exactly the array either way.</summary>
        /// <param name="stream">The bounded, inflated stream.</param>
        /// <param name="indexCount">Indices to read.</param>
        /// <param name="vertexCount">The building's vertex count - the bound each index must be under.</param>
        /// <param name="buffer">The walk's one scratch buffer.</param>
        /// <param name="bad">The position of the first out-of-range index, when this returns false.</param>
        /// <param name="value">What that index claimed.</param>
        private static bool IndicesAreInRange(
            System.IO.Stream stream, int indexCount, int vertexCount, byte[] buffer, out int bad, out uint value)
        {
            bad = -1;
            value = 0;

            var perChunk = buffer.Length / 4;
            var done = 0;

            while (done < indexCount)
            {
                var take = Math.Min(perChunk, indexCount - done);

                FillExactly(stream, buffer, take * 4);

                for (var i = 0; i < take; i++)
                {
                    var index = BitConverter.ToUInt32(buffer, i * 4);

                    if (index < (uint)vertexCount) continue;

                    // See YsAreHeights: the array is consumed whole even once a bad value is found.
                    if (bad >= 0) continue;

                    bad = done + i;
                    value = index;
                }

                done += take;
            }

            return bad < 0;
        }

        /// <summary>A read-only wrapper that refuses to hand out more than a fixed number of bytes,
        /// which is what bounds the INFLATED size of a mesh file. Its own type rather than a counter in
        /// the walk above: a walk with a counter is a walk somebody adds a read to and forgets.</summary>
        private sealed class BoundedStream : System.IO.Stream
        {
            private readonly System.IO.Stream _inner;
            private readonly long _limit;
            private long _read;

            public BoundedStream(System.IO.Stream inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => _read;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_read >= _limit)
                {
                    // AT the limit is not over it: a file that inflates to exactly the ceiling is a legal
                    // file, and refusing it here would have refused it with a message about a bomb. So one
                    // byte is asked of the inner stream to tell the two apart - if it has nothing, this is
                    // a clean end of stream and the walk finishes; if it has more, the file really is past
                    // the ceiling and that is the refusal.
                    if (count <= 0) return 0;

                    var probe = new byte[1];

                    if (_inner.Read(probe, 0, 1) <= 0) return 0;

                    throw new System.IO.InvalidDataException(
                        $"the mesh inflates to more than the {_limit:N0} bytes this server will read");
                }

                var allowed = (int)Math.Min(count, _limit - _read);
                var read = _inner.Read(buffer, offset, allowed);
                _read += read;

                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

        /// <summary>The mesh's name in a map's folder, this server's own - the same shape the client's
        /// MapMeshFile.FileNameFor writes and package.ps1's layout gate admits.</summary>
        private static string MeshName(string key) => key + "-mesh.bin";

        /// <summary>The staged mesh, inside one capture's staging folder. A FIXED name rather than the
        /// map's, so it cannot be confused with a floor and so <see cref="FilesByLevel"/> - which parses
        /// the name as a level and only accepts picture extensions - skips it without a special
        /// case.</summary>
        private static string MeshPath(string staging) => System.IO.Path.Combine(staging, "mesh.bin");

        /// <summary>The staged meta's name, written by every floor post. Skipped by
        /// <see cref="FilesByLevel"/> for <see cref="MeshPath"/>'s reason: "meta" is not a level and
        /// ".json" is not a picture format.</summary>
        private const string StagedMetaName = "meta.json";

        /// <summary>Whether two mesh blocks name the same mesh - both absent, or both present with the
        /// same sha256. The comparison the floor route refuses a clash on: "no mesh" and "this mesh" are
        /// as different as two different meshes, because one set waits for a file and the other does
        /// not.</summary>
        /// <param name="a">One block, or null.</param>
        /// <param name="b">The other, or null.</param>
        private static bool SameMesh(MapCaptureMeshDto? a, MapCaptureMeshDto? b)
        {
            if (a == null || b == null) return a == null && b == null;

            return string.Equals(a.Sha256 ?? "", b.Sha256 ?? "", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The sidecar beside a staged mesh holding its sha256 as text - see
        /// <see cref="MeshIsStaged"/>. Its extension is not a picture format, so
        /// <see cref="FilesByLevel"/> skips it like the staged meta.</summary>
        private static string MeshShaPath(string staging) => System.IO.Path.Combine(staging, "mesh.sha");

        /// <summary>Whether this capture's mesh is already staged AND is the one its meta names.
        ///
        /// Read from the sidecar <see cref="MeshShaPath"/> rather than by hashing the file, because this
        /// is asked on EVERY floor post of a capture that already has its mesh: a four-floor map would
        /// otherwise re-hash 12 MB four times over, on the request thread, to answer a question the
        /// staging folder already knows the answer to. The sidecar is written after the mesh and deleted
        /// before it, so its absence means "hash it" rather than "no mesh"; and it is only ever a HINT -
        /// <see cref="Promote"/> re-hashes the file itself before serving it, which is the check that
        /// decides.
        ///
        /// Hashed at all, rather than merely testing the file exists, because a mesh staged by an earlier
        /// attempt at the same capture could be a different file, and a set completed with it would be
        /// served under a sha nothing matches.</summary>
        /// <param name="staging">The capture's staging folder.</param>
        /// <param name="sha256">The sha the meta names.</param>
        private static bool MeshIsStaged(string staging, string? sha256)
        {
            if (string.IsNullOrEmpty(sha256)) return false;

            try
            {
                var path = MeshPath(staging);

                if (!System.IO.File.Exists(path)) return false;

                var sidecar = MeshShaPath(staging);

                var hash = System.IO.File.Exists(sidecar)
                    ? System.IO.File.ReadAllText(sidecar).Trim()
                    : Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(path))).ToLowerInvariant();

                return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Unreadable is not staged: the set waits, which is the safe direction.
                return false;
            }
        }

        /// <summary>
        /// The staged capture that is waiting for the mesh with this sha, or null with the reason to
        /// refuse the post.
        ///
        /// One method because it is asked TWICE per mesh post and the two must agree: once before the
        /// bytes are decoded, so a post nothing is waiting for costs a stranger nothing, and once under
        /// the lock before anything is written, because the answer can change while the header is walked.
        ///
        /// Caller holds the lock.</summary>
        /// <param name="key">The canonical map name.</param>
        /// <param name="capturedAt">The capture the post claims, already clipped.</param>
        /// <param name="captured">That timestamp parsed, for the comparison with the served set.</param>
        /// <param name="sha256">The mesh's sha - the CLAIMED one on the first call, the bytes' own on the
        /// second. They are equal by then, which is what makes one method right for both.</param>
        /// <param name="problem">Why not. Empty when this returns a meta.</param>
        private MapCaptureMetaDto? WantedMesh(
            string key, string capturedAt, DateTime captured, string sha256, out string problem)
        {
            problem = "";

            if (_sets.TryGetValue(key, out var held) && ParseStamp(held.Meta.CapturedAt) >= captured)
            {
                problem = "older than the set on the host";
                return null;
            }

            var staging = StagingFolder(key, capturedAt);

            if (!System.IO.Directory.Exists(staging))
            {
                problem = "no capture of that map is waiting for a mesh on this host - post the floors first, or " +
                          "the set has already expired";
                return null;
            }

            var staged = ReadStagedMeta(staging);

            if (staged == null)
            {
                problem = "the staged capture has no readable meta, so nothing says it wants a mesh";
                return null;
            }

            if (staged.Mesh == null)
            {
                problem = "the staged capture's meta names no mesh, so this one belongs to nothing";
                return null;
            }

            if (!string.Equals(staged.Mesh.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"the staged capture names a mesh with sha {Short(staged.Mesh.Sha256)}, " +
                          $"not {Short(sha256)}";
                return null;
            }

            return staged;
        }

        /// <summary>The meta a floor post staged, re-read and re-validated, or null when there is none to
        /// read. Re-VALIDATED although this server wrote it: the file has been on disk, the caller is
        /// about to promote a set from it, and a check that is skipped because "we wrote it" is a check
        /// that stops holding the day something else writes there.</summary>
        /// <param name="staging">The capture's staging folder.</param>
        private MapCaptureMetaDto? ReadStagedMeta(string staging)
        {
            try
            {
                var path = System.IO.Path.Combine(staging, StagedMetaName);

                if (!System.IO.File.Exists(path)) return null;

                var meta = JsonSerializer.Deserialize<MapCaptureMetaDto>(
                    System.IO.File.ReadAllBytes(path), FileOptions);

                if (meta == null || meta.SchemaVersion != MapCaptureMetaDto.CurrentSchemaVersion) return null;

                if (!ZoneStore.IsValidMapName(meta.Map)) return null;

                var key = ZoneStore.Canonical(meta.Map);

                if (!MetaIsUsable(key, meta, out _)) return null;

                DropUnusableMesh(key, meta);

                return meta;
            }
            catch
            {
                return null;
            }
        }

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

        /// <summary>sha256 over the meta's bytes, every picture's bytes in level order and the mesh's
        /// bytes LAST, hex. The order and the exact bytes are what make this reproducible from the
        /// folder at the next boot; nothing about it may depend on how a request happened to be
        /// serialised.
        ///
        /// The mesh is IN it, and has to be: the stamp is the whole of how a client decides whether to
        /// download again, so a set whose mesh was replaced while its pictures stayed the same would
        /// otherwise keep its old stamp and never reach anybody. Last rather than first so that a set
        /// with no mesh hashes exactly as it did before meshes existed - which is what keeps this
        /// release from re-downloading every picture set every client already holds.</summary>
        private static string StampOf(byte[] metaBytes, IEnumerable<byte[]> floorsInLevelOrder, byte[]? meshBytes)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(metaBytes);

            foreach (var floor in floorsInLevelOrder) hash.AppendData(floor);

            if (meshBytes != null) hash.AppendData(meshBytes);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>The first twelve characters of a hash, for a log line or a refusal: enough to tell
        /// two files apart by eye, short enough that a line stays readable.</summary>
        private static string Short(string sha256) =>
            string.IsNullOrEmpty(sha256) ? "(none)" : sha256.Length <= 12 ? sha256 : sha256[..12];

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

        /// <summary>A string from a client cut to a length, with its line breaks turned into spaces.
        ///
        /// THE LINE BREAKS ARE THE POINT. Everything clipped here is text a peer chose on an
        /// unauthenticated route and every one of them is printed into a log line: the claimed format,
        /// the map the meta names, capturedAt, the client version, a floor's name. A floor called
        /// "Ground\nQuest Tracker: ..." wrote a SECOND line into the server log reading exactly like
        /// one of this mod's own - proven in a harness, where the refusal for a bad pixel size arrived
        /// split across two lines. A floor name and the two free-text fields are also STORED and shown
        /// on screen, so this is not only about the log.
        ///
        /// A space rather than a refusal, for the same reason these are clipped rather than refused:
        /// they are captions and free text, and they decide nothing about where a pixel lands. The
        /// label text below already had this treatment spelled out inline; this is the same rule, in
        /// the one place every caller goes through.</summary>
        private static string Clip(string value, int max)
        {
            var line = value.Replace('\r', ' ').Replace('\n', ' ');

            return line.Length <= max ? line : line[..max];
        }

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

        /// <summary>Refuses one mesh post, logged by the same deduped path a refused picture takes: the
        /// mesh route is as unauthenticated as the picture route, so a client in a loop must not be able
        /// to rotate the real diagnostics out of a rolling log.</summary>
        private MapMeshUploadResponse RejectMesh(string map, string reason)
        {
            WarnOnce(map, reason);

            return new MapMeshUploadResponse { Accepted = false, Reason = reason };
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
