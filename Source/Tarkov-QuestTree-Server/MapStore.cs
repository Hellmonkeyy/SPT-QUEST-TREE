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
    /// otherwise. It is computed on completion and rebuilt at boot from the folders. A stamp that
    /// disagreed with the pictures beside it would keep a client on a stale map forever while both halves
    /// reported success, so the one thing persisted beside a set is a CACHE of it (<c>&lt;key&gt;.stamp-cache.json</c>,
    /// stage W): the stamp and each file's sha256, keyed on every file's name, size and write time and the
    /// meta's - trusted only while ALL of those still match, and rebuilt from the bytes otherwise. It exists
    /// because a host at its store ceiling (1.5 to 32 GB) would otherwise hash all of it under the lock on the first
    /// index request after every boot.
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

        /// <summary>
        /// The store's, a map's and a mesh's ceilings (WP7), DERIVED once at boot from the free disk of the
        /// volume the maps live on (<see cref="SizeCeilings"/>) rather than fixed: a quarter of the free disk,
        /// never under <see cref="StoreFloor"/> (the pre-WP7 total) nor over <see cref="StoreTop"/>; an eighth of
        /// that a map; the map's pictures and pages at their own caps taken off for the mesh, never over
        /// <see cref="MeshAbsolute"/>. Each upload is then held to its meta's DECLARED mesh size, accepted up to
        /// the mesh ceiling (<see cref="DropUnusableMesh"/>). Fixed for the process: a host that frees disk picks
        /// it up at its next start.
        /// </summary>
        private long _storeCeiling = StoreFloor;

        private long _mapCeiling = StoreFloor / 8;

        private long _meshCeiling = Math.Min(StoreFloor / 8 - PicturesAndPagesPerMap, MeshAbsolute);

        private bool _ceilingsSized;

        /// <summary>The least the store ceiling is: the pre-WP7 fixed total, so no host holds less than before.
        /// Rollback (with <see cref="StoreTop"/>): 1.5 GiB and 1.5 GiB.</summary>
        private const long StoreFloor = 1536L << 20;

        /// <summary>The most the store ceiling is, however much disk is free. Rollback: 1.5 GiB.</summary>
        private const long StoreTop = 32L << 30;

        /// <summary>
        /// The largest mesh any host takes: the one-body download WP7 does not redesign. <see cref="MeshFile"/>
        /// base64s the whole mesh into one string - 512 MiB is 716 M characters, under .NET's ~1.07 G-character
        /// string limit (768 MiB would be at it) - and a client holds ~6.3x the mesh while it decodes it.
        /// Rollback: 48 MiB (the pre-WP7 fixed mesh ceiling).
        /// </summary>
        private const long MeshAbsolute = 512L << 20;

        /// <summary>What a map's pictures and atlas pages may weigh at their own caps: 12 pictures (8 floors, 4
        /// sides) and 8 pages - 78 MiB, taken off the map's ceiling for the mesh.</summary>
        private const long PicturesAndPagesPerMap = 12L * MaxImageBytes + 8L * MaxAtlasPageBytes;

        /// <summary>The most one PART of a mesh may weigh, decoded. The client sends 16 MiB parts; this is
        /// the host's own ceiling on one, set by what a post can carry at all: Kestrel's default request
        /// limit is 30,000,000 bytes on the wire, and SPT sends a body zlib-compressed, which brings the
        /// base64 of an already-deflated mesh back to about 1.03 times its size - so 24 MiB of mesh is
        /// ~25.9 MB on the wire, under the limit with room, while 28 MiB would not be.</summary>
        private const int MaxMeshPartBytes = 24 * 1024 * 1024;

        /// <summary>A part's base64 ceiling, checked before decoding.</summary>
        private const int MaxEncodedMeshPartChars = MaxMeshPartBytes / 3 * 4 + 1024;

        /// <summary>The most parts one mesh may come in: 64, which at the client's 16 MiB parts is 1 GiB - twice
        /// <see cref="MeshAbsolute"/> - and a bound on how many files one upload can make a host hold. A mesh
        /// must also come in at least ceil(bytes / <see cref="MaxMeshPartBytes"/>) parts, so no part is ever
        /// asked to carry more than one post can. Rollback: 8.</summary>
        private const int MaxMeshPartsCeiling = 64;

        /// <summary>
        /// The most a mesh file may INFLATE to while its header is being checked, from the meta's DECLARED counts
        /// (D14): 64 bytes of header, 3 a relief cell (height and distance), 42 a triangle (12 of indices and at
        /// most three vertices of x, y, z, u, v at 10 bytes), and 2,328 a building slot (24 of counts and 64
        /// ranges of 36) for up to 20,000 of them - capped at 1 GiB. A mesh file is one deflate block, so a
        /// hostile one can hold a thousand times its size; the walk stops reading at this, which keeps a deflate
        /// bomb to a bounded read. A file that under-declares is refused anyway: the walk's totals are held to the
        /// meta (<see cref="MeshFitsMeta"/>). Customs at 2.4 M cells and 6.8 M triangles: about 340 MB.
        /// Replaces the fixed 256 MB of stage X.
        /// </summary>
        /// <param name="cells">The meta's declared relief cells.</param>
        /// <param name="triangles">The meta's declared triangles.</param>
        internal static long MeshInflateBound(long cells, long triangles) =>
            Math.Min(MaxInflatedMeshBytes,
                64L + 3L * Math.Max(0L, cells) + 42L * Math.Max(0L, triangles) + (long)MaxMeshBuildings * 2_328L);

        /// <summary>The inflate bound's cap: 1 GiB.</summary>
        private const long MaxInflatedMeshBytes = 1L << 30;

        /// <summary>The scratch buffer size for one header walk, shared by every read in it. 64 KiB
        /// divides by both 2 and 4, so a chunk never splits a uint16 or a uint32 element.</summary>
        private const int MeshChunkBytes = 64 * 1024;

        /// <summary>The mesh format this server stores, and the only one it will take: the client's
        /// MapMeshFile.Version. NOT a reference to that class - the server cannot see the Unity
        /// assembly - so this is the one number both halves must be changed for together, which is
        /// why the magic below carries the same digit and is checked as well. 2 since stage W: the header
        /// gained the atlas page count and every building its UVs and atlas ranges - a v1 file is refused by
        /// name, since no stage W client reads one. 3 since stage X (wrapped textures): each range also carries
        /// its tile's pixel rect on its page and the raw-UV bounds its vertices are quantised over, and a
        /// vertex may belong to one range only. v2 and v1 are refused by name.</summary>
        private const int MeshVersion = 3;

        /// <summary>A range's tile side, in pixels: a multiple of 4 from 4 to 256 (stage X - one repeat of a
        /// material, at most 256 px, cut out of its page by the viewer).</summary>
        private const int MinTileSide = 4;

        private const int MaxTileSide = 256;

        /// <summary>The atlas page's side, which a tile rect must lie inside (MapMeshFile.AtlasPageSize).</summary>
        private const int AtlasPageSide = 4096;

        /// <summary>MapMeshFile.MaxRangesPerBuilding: the most atlas ranges one building may carry.</summary>
        private const int MaxMeshRangesPerBuilding = 64;

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

        /// <summary>MapMeshFile.MaxVerticesTotal (WP7: was 12 M) - a hard bound, twice the triangle bound.</summary>
        private const long MaxMeshVerticesTotal = 80_000_000L;

        /// <summary>MapMeshFile.MaxVerticesPerBuilding - the per-building cap the client's reader
        /// enforces as well as the total.</summary>
        private const int MaxMeshVerticesPerBuilding = 2_000_000;

        /// <summary>How far the mesh header's extent may sit from the meta's before the two are not the
        /// same rectangle. The same 1e-6 m tools/check-maps-pack.py holds a shipped set to: both are the
        /// capture's own doubles, written by one process, so any real difference is a different
        /// capture.</summary>
        private const double MeshExtentTolerance = 1e-6;

        /// <summary>MapMeshFile.MaxTriangles (WP7: was 6 M) - a hard bound, twice the builder's absolute 20 M.</summary>
        private const long MaxMeshTriangles = 40_000_000L;

        /// <summary>MapMeshFile.MaxTrianglesPerBuilding: the builder's source guard, which no stored building
        /// passes. Checked BEFORE the walk grows its index buffer to a building's index count - without it the
        /// 40 M total would let one hostile building make this host allocate 120 M uints (480 MB); with it, 12 MB.</summary>
        private const long MaxMeshTrianglesPerBuilding = 1_000_000L;

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

        // One map's ceiling is _mapCeiling: an eighth of the store's (WP7, see _storeCeiling). It replaced the
        // fixed 132 MB (8 floors + 4 sides at 2.5 MB, a 48 MB mesh, 8 pages at 6 MB, and margin).

        /// <summary>The most atlas pages one set may carry (MapCaptureAtlasDto.Page is 0 to this less one),
        /// the builder's own ceiling.</summary>
        private const int MaxAtlasPages = 8;

        /// <summary>One atlas page's ceiling, decoded. A page is a 4096 px sheet of building textures, sent
        /// at its full size as a JPEG at quality 85 - never downscaled, since a texel lost here is a blurred
        /// wall on every client - and a sheet of dense brick and signage at that quality measures 2-4 MB.
        /// Six is that with room, and still far under what one post can carry (a 6 MB page is ~8.3 MB of
        /// base64). The client holds a page to the same six before it posts it
        /// (MapTransfer.MaxAtlasPageBytes).</summary>
        private const int MaxAtlasPageBytes = 6 * 1024 * 1024;

        /// <summary>A page's base64 ceiling, checked BEFORE decoding, for
        /// <see cref="MaxEncodedChars"/>' reason.</summary>
        private const int MaxEncodedAtlasChars = MaxAtlasPageBytes / 3 * 4 + 1024;

        /// <summary>The four sides a set may carry an oblique picture from, in the one order every
        /// reader uses - the staging, the promotion, the stamp and the boot read all walk them in this
        /// order, so the stamp is the same on every path.</summary>
        private static readonly string[] SideDirs = { "N", "S", "E", "W" };

        /// <summary>How far a side's origin may sit from the projection of the capture box's corners
        /// before the side is not describing that box. A few centimetres: both are the same arithmetic
        /// over the same doubles, and the only difference is float rounding in the writer.</summary>
        private const double SideOriginTolerance = 0.05;

        /// <summary>How far a side's pixel size may sit from the capture box's projected span at its scale.
        /// Four, where a floor gets two (PixelTolerance): a side is rescaled for the wire from its OWN
        /// rendered size, which is itself a ceil of a span in a basis with irrational components, so the
        /// two roundings stack where a floor's do not - the stage U sweep measured up to 2 px with the
        /// short side ceiled (MapTransfer.ScaleTo), which is exactly the floor tolerance and leaves no
        /// headroom at all. Four is still a small fraction of any real side and catches a side described
        /// at one scale and sent at another, which is off by hundreds.</summary>
        private const int SidePixelTolerance = 4;

        /// <summary>How far a side's basis vector may be from unit length. The same 1e-3 the pack gate
        /// holds a shipped set to.</summary>
        private const double SideUnitTolerance = 1e-3;

        // The whole store's ceiling, counting what is staged with what is served, is _storeCeiling: a quarter of
        // the free disk at boot, 1.5 GiB (the stage W total) to 32 GiB (WP7). Still a bound a peer cannot pass -
        // and uploads are refused by default (AcceptVariable), so only a host that opted in can be asked to hold it.

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
        /// The stem is up to 76 characters since the review (F40): a key may be 64 (ZoneStore.SafeName) and a
        /// floor's name is the key, a hyphen and its level - up to eleven characters for an int. At 60 a
        /// 59-character modded map key was stored, served, and then skipped whole at the next boot. The
        /// mesh, side and page rules below take the 64-character key plus their fixed suffix for the same
        /// reason.
        private static readonly Regex StoredFileName =
            new(@"^[A-Za-z0-9_\-]{1,76}\.(jpg|png)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>What a stored MESH file's name may be, checked on the way back in from disk exactly
        /// as <see cref="StoredFileName"/> is, and SEPARATE from it on purpose: a floor that named a
        /// .bin would still be refused, and a mesh block that named a .jpg would still be refused. The
        /// shape is the client's MapMeshFile.FileNameFor - <c>&lt;key&gt;-mesh.bin</c> - which is what
        /// both halves and package.ps1's layout gate spell.</summary>
        private static readonly Regex StoredMeshFileName =
            new(@"^[A-Za-z0-9_\-]{1,64}-mesh\.bin$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>What a stored SIDE picture's file name may be - <c>&lt;key&gt;-side-&lt;dir&gt;.jpg</c>, the
        /// name <see cref="SideName"/> writes and package.ps1's gates admit - checked on the way back in
        /// from disk like the others. JPEG only: a side is always uploaded as one.</summary>
        private static readonly Regex StoredSideFileName =
            new(@"^[A-Za-z0-9_\-]{1,64}-side-[NSEW]\.jpg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>What a stored ATLAS page's file name may be - <c>&lt;key&gt;-atlas-&lt;n&gt;.jpg</c>, n 0 to 7, the
        /// name <see cref="AtlasName"/> writes and package.ps1's gates admit - checked on the way back in from
        /// disk like the others.</summary>
        private static readonly Regex StoredAtlasFileName =
            new(@"^[A-Za-z0-9_\-]{1,64}-atlas-[0-7]\.jpg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        /// <summary>
        /// Staging folders whose set is being completed RIGHT NOW - prepared outside the lock (see
        /// <see cref="CompleteSet"/>) and not yet committed. Guarded by <see cref="_lock"/>.
        ///
        /// Why it exists: the preparation reads the staged files with no lock held, and Windows will not
        /// replace a file while any handle on it is open - with or without delete sharing, which was
        /// tried and measured. So a floor of the same capture re-posted during that read, whose write
        /// moves a new file over the old one, failed with "Access to the path is denied" and its client
        /// was told the host could not store the picture: 3 to 4 rounds in 100 of the harness's
        /// two-thread case. Nothing writes into a folder listed here; a post that would is answered
        /// without writing, and the completion in flight is what stores the set.
        /// </summary>
        private readonly HashSet<string> _completing = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When the stale staging was last swept (<see cref="SweepStaleStagingIfDue"/>). Guarded by
        /// <see cref="_lock"/>.</summary>
        private DateTime _lastStaleSweep = DateTime.MinValue;

        /// <summary>How often an upload post may trigger the stale-staging sweep. The sweep only lists the
        /// staging folders and their files' times, so ten minutes is a bound on work nobody would notice
        /// rather than a saving anyone needs - it exists so a host taking a burst of posts does not list
        /// .incoming for every one of them.</summary>
        private static readonly TimeSpan StaleSweepEvery = TimeSpan.FromMinutes(10);

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

            // Whether the client DECLARED a mesh, before anything here can drop it - so a floor or side post
            // that completes the set can say whether the set it completed kept that mesh (see below).
            var declaredMesh = meta.Mesh != null;

            // The mesh block, separately and NOT as a refusal - see the method. The sides and the atlas
            // pages likewise, the pages AFTER the mesh: a set whose mesh is dropped has nothing to drape
            // them on, so they go with it.
            DropUnusableMesh(key, meta);
            DropUnusableSides(key, meta);
            DropUnusableAtlas(key, meta);

            // A FLOOR post or a SIDE post. The two are the same picture checks with one difference in
            // what a failure costs: a floor that fails refuses the post, as it always has, while a side
            // that fails is DROPPED - taken out of the set's sides - and the post carries on, because a
            // side picture only textures some walls and is never worth the map. So a side's problem is
            // collected here rather than returned, and acted on under the lock.
            //
            // An ATLAS page post is a side post in every respect but its checks and its name: the same
            // level (int.MinValue, which is what makes an older host refuse it rather than file it), the
            // same drop-never-refuse rule, the same markers. pageNo is the page when it is one this host
            // could stage, null otherwise - a page 99 is dropped with no marker, as a side 'Q' is.
            var isSide = !string.IsNullOrWhiteSpace(request.Side);
            var sideDir = isSide ? NormaliseSide(request.Side) : null;
            string? sideRefusal = null;
            var isAtlas = request.Atlas != null;
            int? pageNo = isAtlas && request.Atlas >= 0 && request.Atlas < MaxAtlasPages ? request.Atlas : null;
            string? pageRefusal = null;
            var format = "jpg";
            var bytes = Array.Empty<byte>();

            // A post that claims to be both is a client bug, not a piece of anything: refused, since no
            // answer about one of the two would be true of the post.
            if (isSide && isAtlas) return Reject(key, "the post names both a side and an atlas page");

            if (isSide)
            {
                if (sideDir == null)
                    sideRefusal = $"'{Clip(request.Side ?? "", 8)}' is not a side (N, S, E or W)";
                else if (meta.Sides == null || !meta.Sides.Any(s => s.Dir == sideDir))
                    sideRefusal = $"the capture's meta names no {sideDir} side";
                else
                    sideRefusal = DecodeSide(request, out bytes);
            }
            else if (isAtlas)
            {
                var entry = meta.Atlas?.FirstOrDefault(p => p.Page == pageNo);

                if (pageNo == null)
                    pageRefusal = $"{request.Atlas} is not an atlas page (0 to {MaxAtlasPages - 1})";
                else if (entry == null)
                    pageRefusal = meta.Mesh == null
                        ? "the set carries no 3D mesh for it to texture"
                        : $"the capture's meta names no atlas page {pageNo}";
                else
                    pageRefusal = DecodeAtlasPage(request, entry, out bytes);
            }
            else
            {
                var floor = meta.Floors.FirstOrDefault(f => f.Level == request.Level);

                if (floor == null)
                    return Reject(key, $"level {request.Level} is not one of the {meta.Floors.Count} floors the meta names",
                        MapUploadResponse.CodeUnknownLevel);

                var claimedFormat = Format(request.Format);

                if (claimedFormat == null)
                    return Reject(key, $"format '{Clip(request.Format ?? "", 16)}' is not jpg or png");

                format = claimedFormat;

                var encoded = request.ImageBase64 ?? "";

                if (encoded.Length == 0) return Reject(key, "the post carries no picture");

                if (encoded.Length > MaxEncodedChars)
                    return Reject(key, $"the picture is larger than the {Mb(MaxImageBytes)} MB a floor may be");

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

                // The picture's OWN size, from its header - review F49: the meta's numbers are capped, the
                // bytes were not, and a small file can declare an enormous picture that every client would
                // then try to decode. The client refuses past 8192 px too (DynamicMapsLibrary).
                var sizeProblem = PictureSizeProblem(format, bytes);

                if (sizeProblem != null) return Reject(key, $"the picture {sizeProblem}");
            }

            var captured = ParseStamp(meta.CapturedAt);

            if (captured == DateTime.MinValue)
                return Reject(key, "capturedAt is not a timestamp, so nothing could rank this set against the one held");

            // Set under the lock, used after it: the folder this capture is staged in, and - only once
            // every piece is here - the meta to promote. The promotion itself reads and hashes up to
            // a map's ceiling (hundreds of MB), so it is PREPARED outside the lock and only committed under it (see CompleteSet).
            string staging;
            MapCaptureMetaDto ready;

            lock (_lock)
            {
                if (!_loaded) Load();

                // Abandoned uploads, swept on the way in - see SweepStaleStagingIfDue.
                SweepStaleStagingIfDue();

                // Against the COMPLETE set only, never against what is staged: the second floor of one
                // capture carries the same CapturedAt as the first and must not be refused as "not
                // newer". Equal is refused, which also stops a client re-posting a set the host already
                // has - the client compares stamps before uploading, so an equal CapturedAt here means
                // the set is already served.
                if (_sets.TryGetValue(key, out var held) && ParseStamp(held.Meta.CapturedAt) >= captured)
                {
                    // A SIDE of a capture the host already serves (or has superseded) is not a fault and
                    // not a reason to stop: answered as a drop, so the client goes on to its mesh and hears
                    // "already served" there. No marker: the capture's staging went when its set was
                    // promoted, and writing one would only make a folder for a capture that is finished.
                    if (isSide)
                        return new MapUploadResponse
                        {
                            Outcome = "stored",
                            Reason = SideNote(sideDir ?? SideLabel(request.Side), "older than the set on the host", ""),
                            Dropped = true
                        };

                    // An atlas page likewise, for the same reason.
                    if (isAtlas)
                        return new MapUploadResponse
                        {
                            Outcome = "stored",
                            Reason = PageNote(PageLabel(request.Atlas), "older than the set on the host", ""),
                            Dropped = true
                        };

                    return Reject(key, "older than the set on the host");
                }

                // CLIPPED, exactly as the mesh route clips its own capturedAt before hashing it: the two
                // routes must name the same folder for the same capture, and a timestamp with a line
                // break in it would otherwise hash one way here and another way there.
                staging = StagingFolder(key, Clip(meta.CapturedAt, MaxFreeTextLength));

                // Another post of this capture is completing it right now, outside the lock. Writing here
                // would race its reads - see _completing - and nothing this post carries is needed: the
                // set is being stored from the files already staged. "stored" rather than "complete",
                // because the completion in flight has not succeeded yet.
                if (_completing.Contains(staging))
                    return new MapUploadResponse
                    {
                        Outcome = "stored",
                        Reason = "this capture is being completed right now",
                        FloorsHeld = FilesByLevel(staging).Count
                    };

                // A capture whose mesh this host has already REFUSED for good (see FlattenStaged) is
                // served flat, so a floor of it still naming that mesh is brought into line with the
                // staged meta here rather than refused as a clash below. Only for that exact sha, which
                // only the genuine file could have produced.
                var refused = RefusedMeshSha(staging);

                if (meta.Mesh != null && refused != null &&
                    string.Equals(meta.Mesh.Sha256, refused, StringComparison.OrdinalIgnoreCase))
                    meta.Mesh = null;

                // And a set served flat has no buildings to drape an atlas page on, so its pages go with the
                // mesh - out of the meta the completion waits on, exactly as FlattenStaged took them out of
                // the staged one.
                if (meta.Mesh == null) meta.Atlas = null;

                // The same for SIDES this host has already dropped for this capture: every floor post
                // re-stages the meta, and one that still named a dropped side would put it back on the
                // list the completion waits for - which it would then wait for until the staging expired.
                StripDroppedSides(staging, meta);

                // A side this capture has ALREADY dropped, posted again - a retry, or a second machine's
                // copy - is dropped again rather than staged: StripDroppedSides has just taken it out of
                // the meta, so its picture would sit in the staging as a file nothing names.
                if (isSide && sideRefusal == null && sideDir != null &&
                    (meta.Sides == null || !meta.Sides.Any(sd => sd.Dir == sideDir)))
                    sideRefusal = "it was already dropped for this capture";

                // And this post's own side, when it is being dropped: out of the meta BEFORE the meta is
                // staged or the completion counts what is missing.
                if (sideRefusal != null && sideDir != null)
                    meta.Sides?.RemoveAll(s => s.Dir == sideDir);

                // The same three steps for ATLAS pages: the ones already dropped for this capture out of the
                // meta, a page posted again after it was dropped (or after its set went flat) dropped again,
                // and this post's own page out of the meta when it is being dropped.
                StripDroppedPages(staging, meta);

                if (isAtlas && pageRefusal == null && pageNo != null &&
                    (meta.Atlas == null || !meta.Atlas.Any(p => p.Page == pageNo)))
                    pageRefusal = meta.Mesh == null
                        ? "the set is served without its 3D mesh, which is all a page textures"
                        : "it was already dropped for this capture";

                if (pageRefusal != null && pageNo != null)
                {
                    meta.Atlas?.RemoveAll(p => p.Page == pageNo);

                    if (meta.Atlas != null && meta.Atlas.Count == 0) meta.Atlas = null;
                }

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
                var stagedSides = SidesByDir(staging);
                var stagedPages = PagesByNumber(staging);

                // What this post REPLACES, which is the only thing either budget may discount: a floor
                // or side posted twice overwrites its own staged copy, so counting the old one as well
                // would refuse a client that simply retried. Everything else already staged still counts,
                // which is what stops a set being walked past the budget one picture at a time.
                var mine = isSide
                    ? (sideDir != null && stagedSides.TryGetValue(sideDir, out var mySide) ? SizeOf(mySide) : 0)
                    : isAtlas
                        ? (pageNo != null && stagedPages.TryGetValue(pageNo.Value, out var myPage) ? SizeOf(myPage) : 0)
                        : (staged.TryGetValue(request.Level, out var already) ? SizeOf(already) : 0);

                // The sides the meta names that are not staged yet, other than this post's own: each is
                // reserved at the most a picture may weigh, because a side carries no declared size and
                // the point - as for the mesh below - is that a capture that cannot fit is refused on its
                // FIRST post, before anything is staged, rather than after its floors are all in.
                var pendingSides = (meta.Sides ?? new List<MapCaptureSideDto>())
                    .Count(sd => sd.Dir != sideDir && !stagedSides.ContainsKey(sd.Dir)) * (long)MaxImageBytes;

                // The atlas pages likewise, each at the most a PAGE may weigh.
                var pendingPages = (meta.Atlas ?? new List<MapCaptureAtlasDto>())
                    .Count(p => p.Page != pageNo && !stagedPages.ContainsKey(p.Page)) * (long)MaxAtlasPageBytes;

                // The mesh counts against the map's budget from the FIRST floor: staged, at its size on
                // disk; not yet staged, at the size the meta declares for it (bounded to the mesh ceiling by
                // DropUnusableMesh). Counting only what is on disk meant a capture that could never fit
                // was refused on the mesh, after every floor had been staged - and a refusal there is one
                // the floors then wait out for a day. Refused here, nothing is staged at all.
                var meshBytes = System.IO.File.Exists(MeshPath(staging))
                    ? SizeOf(MeshPath(staging))
                    : Math.Max(meta.Mesh?.Bytes ?? 0L, 0L);

                var setBytes = staged.Sum(entry => SizeOf(entry.Value)) +
                               stagedSides.Sum(entry => SizeOf(entry.Value)) + pendingSides +
                               stagedPages.Sum(entry => SizeOf(entry.Value)) + pendingPages + meshBytes - mine;

                // A SIDE that does not fit is DROPPED rather than refusing the post, for the reason every
                // other side problem is: a refusal would leave the side named in the staged meta, and the
                // set would then wait for it until the staging expired - with the client having gone on to
                // post the whole mesh into a set that can never complete. A floor still refuses.
                string? overBudget = null;

                if (setBytes + bytes.Length > _mapCeiling)
                    overBudget = $"this capture would be {Mb(setBytes + bytes.Length)} MB, past the " +
                                 $"{Mb(_mapCeiling)} MB one map may hold";

                // The whole store, counting what is staged as well as what is served: during an upload
                // the disk really does hold both - the set being replaced is still being served - and a
                // peer that uploads and abandons sets would otherwise be bounded by nothing at all.
                // The declared-but-not-staged mesh again, for the same reason: it is coming, and the
                // store has to have room for it when it does.
                var pendingMesh = System.IO.File.Exists(MeshPath(staging)) ? 0L : Math.Max(meta.Mesh?.Bytes ?? 0L, 0L);
                var pending = pendingMesh + pendingSides + pendingPages;
                var total = _sets.Values.Sum(s => s.Bytes) + IncomingBytes() - mine;

                // Two sentences, because "holds" has to stay true: what is on the disk, and - only when
                // it is what tipped the balance - the mesh, sides and pages this capture says are still to
                // come. Named for what is actually pending, so the sentence is true of this capture.
                var pendingWhat = Listed(
                    pendingMesh > 0 ? "mesh" : null,
                    pendingSides > 0 ? "sides" : null,
                    pendingPages > 0 ? "atlas pages" : null);

                if (overBudget == null && total + pending + bytes.Length > _storeCeiling)
                    overBudget = pending > 0 && total + bytes.Length <= _storeCeiling
                        ? $"the host holds {Mb(total)} MB of map pictures, and the {Mb(pending)} MB this capture's " +
                          $"{pendingWhat} may still need would take it past the {Mb(_storeCeiling)} MB limit"
                        : $"the host already holds {Mb(total)} MB of map pictures, at the {Mb(_storeCeiling)} MB limit";

                if (overBudget != null)
                {
                    // A side or page already being dropped writes nothing but a marker, so a budget cannot be
                    // what stops it; a side or page that would be staged is dropped instead; a floor refuses,
                    // as a floor always has.
                    if (isSide && sideDir != null && sideRefusal == null)
                    {
                        sideRefusal = overBudget;
                        bytes = Array.Empty<byte>();
                        meta.Sides?.RemoveAll(sd => sd.Dir == sideDir);
                    }
                    else if (isAtlas && pageNo != null && pageRefusal == null)
                    {
                        pageRefusal = overBudget;
                        bytes = Array.Empty<byte>();
                        meta.Atlas?.RemoveAll(p => p.Page == pageNo);

                        if (meta.Atlas != null && meta.Atlas.Count == 0) meta.Atlas = null;
                    }
                    else if (!isSide && !isAtlas)
                    {
                        return Reject(key, overBudget);
                    }
                }

                try
                {
                    System.IO.Directory.CreateDirectory(staging);

                    if (isAtlas)
                    {
                        // A page staged, or - dropped - its marker, with any copy an earlier attempt staged
                        // deleted so it cannot be promoted. Exactly the side's two cases below.
                        if (pageNo != null && pageRefusal == null)
                        {
                            WriteAtomic(System.IO.Path.Combine(staging, StagedPageName(pageNo.Value)), bytes);
                        }
                        else if (pageNo != null)
                        {
                            WriteAtomic(System.IO.Path.Combine(staging, DroppedPageName(pageNo.Value)),
                                Encoding.UTF8.GetBytes(pageRefusal!));

                            try { System.IO.File.Delete(System.IO.Path.Combine(staging, StagedPageName(pageNo.Value))); }
                            catch { /* there may be none */ }
                        }
                    }
                    else if (!isSide)
                    {
                        var wanted = System.IO.Path.Combine(staging, StagedName(request.Level, format));

                        // ONE file per level, whatever format it arrives in. A floor first posted as a
                        // JPEG and then re-posted as a PNG would otherwise leave both on disk under one
                        // level, and the completion below picks by level - so half the time it would
                        // promote the stale bytes, with every log line saying the set was stored.
                        foreach (var other in System.IO.Directory.EnumerateFiles(
                                     staging, request.Level.ToString(CultureInfo.InvariantCulture) + ".*"))
                            if (!string.Equals(other, wanted, StringComparison.OrdinalIgnoreCase))
                                System.IO.File.Delete(other);

                        WriteAtomic(wanted, bytes);
                    }
                    else if (sideRefusal == null)
                    {
                        WriteAtomic(System.IO.Path.Combine(staging, StagedSideName(sideDir!)), bytes);
                    }
                    else if (sideDir != null)
                    {
                        // Dropped for good for this capture - a marker, so the later posts' metas (which
                        // still name it) are brought into line by StripDroppedSides - and any copy an
                        // earlier attempt staged goes too, so it cannot be promoted.
                        WriteAtomic(System.IO.Path.Combine(staging, DroppedSideName(sideDir)), Encoding.UTF8.GetBytes(sideRefusal));

                        try { System.IO.File.Delete(System.IO.Path.Combine(staging, StagedSideName(sideDir))); }
                        catch { /* there may be none */ }
                    }

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

                // Said once, at Warning - a side dropped is a picture the host will never serve - but it is
                // not a refusal of the POST: the answer below is the ordinary one, with the drop in its
                // reason.
                if (sideRefusal != null)
                    NoteSideDropped(key, sideDir ?? SideLabel(request.Side), meta.CapturedAt, sideRefusal);

                if (pageRefusal != null)
                    NoteAtlasDropped(key, PageLabel(request.Atlas), meta.CapturedAt, pageRefusal);

                // Re-read from disk rather than adding one to a count: the staged set is the authority
                // on what has arrived, which is what makes a server restarted mid-upload resume
                // instead of starting over.
                staged = FilesByLevel(staging);

                var missing = Missing(meta, staging, out var waitingFor);

                if (missing > 0)
                {
                    _logger.Detail(
                        $"Quest Tracker: holding {staged.Count} of {meta.Floors.Count} floor(s) of the map " +
                        $"picture set for '{key}' captured {Clip(meta.CapturedAt, MaxFreeTextLength)} - waiting for " +
                        $"{waitingFor}.");

                    return Piece(new MapUploadResponse
                    {
                        Outcome = "stored",
                        Reason = Note($"waiting for {waitingFor}"),
                        FloorsHeld = staged.Count
                    });
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

                    return Piece(new MapUploadResponse
                    {
                        Outcome = "stored",
                        Reason = Note("waiting for the mesh"),
                        FloorsHeld = staged.Count
                    });
                }

                // Claimed under the lock, released by CompleteSet under the lock: from here until the
                // commit, nothing else writes into this folder.
                _completing.Add(staging);
                ready = meta;
            }

            var completed = CompleteSet(key, ready, staging, Clip(request.ClientVersion ?? "", MaxFreeTextLength));

            // A floor or side post that COMPLETES a set whose client declared a mesh says what became of the
            // mesh, because from the client's side "complete" before its mesh post is ambiguous: this host
            // may have had the mesh staged already (a retry - the set is served in 3D), or may have dropped it
            // at the meta or refused it for good earlier (served flat). Only the second is news the player
            // should hear, and the client can only tell them apart by being told.
            if (completed.Outcome == "complete")
            {
                var meshNote = !declaredMesh ? ""
                    : ready.Mesh != null ? "stored with its 3D mesh"
                    : "stored without its 3D mesh - this host could not take it (its own log says why)";

                // A dropped side or page that happened to be the last piece still says it was dropped.
                completed.Reason = Note(meshNote);

                // The same decision as a field (review F02): the words above are for a person, and a client
                // branches on this. Null when the meta declared no mesh, as the words are then silent too.
                completed.MeshKept = declaredMesh ? ready.Mesh != null : (bool?)null;
            }

            return Piece(completed);

            // The answer's reason with this post's own drop in front of it, when it was one - a side's or a
            // page's, in the words the client reads for each.
            string Note(string rest) => isAtlas
                ? PageNote(PageLabel(request.Atlas), pageRefusal, rest)
                : SideNote(sideDir, sideRefusal, rest);

            // The drop Note puts into words, as a field (review F02): set on every side or page answer that
            // carries a Note, left null on a floor post's.
            MapUploadResponse Piece(MapUploadResponse r)
            {
                if (isAtlas) r.Dropped = pageRefusal != null;
                else if (isSide) r.Dropped = sideRefusal != null;
                return r;
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

                // A SIDE picture when the request names one, the floor at its level otherwise. Either
                // way the file name comes from the stored meta - written by CommitSet or checked by Load
                // - and nothing from the request but the level or the direction reaches a path.
                string? file;

                if (!string.IsNullOrWhiteSpace(request.Side))
                {
                    var dir = NormaliseSide(request.Side);
                    var side = dir == null ? null : set.Meta.Sides?.FirstOrDefault(s => s.Dir == dir);

                    file = side != null && StoredSideFileName.IsMatch(side.File ?? "") ? side.File : null;
                }
                else if (request.Atlas != null)
                {
                    // An ATLAS page by its number, the name again from the stored meta and held to the
                    // stored-page rule. A page this set does not carry answers empty, as a side does.
                    var page = set.Meta.Atlas?.FirstOrDefault(p => p.Page == request.Atlas);

                    file = page != null && StoredAtlasFileName.IsMatch(page.File ?? "") ? page.File : null;
                }
                else
                {
                    file = set.Meta.Floors.FirstOrDefault(f => f.Level == request.Level)?.File;
                }

                if (string.IsNullOrEmpty(file)) return dto;

                try
                {
                    var bytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, key, file));

                    dto.Stamp = set.Stamp;
                    dto.Format = Format(System.IO.Path.GetExtension(file).TrimStart('.')) ?? "";
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
            // small JSON read - no decode of up to 512 MiB, no sha over it, no inflate of the header. It needs only
            // the CLAIMED sha, and the bytes are then held to that claim below, so the chain is
            // unbroken: claim matches the staged meta, bytes match the claim.
            //
            // Under the lock, and then RELEASED for the header walk further down, which is CPU work on a
            // body from the network and must not hold a lock the index route needs on the game's main
            // thread. The gate is re-run under the lock before anything is written, because the staged
            // set can complete or expire while the walk runs.
            MapCaptureMetaDto? wanted;

            lock (_lock)
            {
                if (!_loaded) Load();

                // Abandoned uploads, swept on the way in - see SweepStaleStagingIfDue.
                SweepStaleStagingIfDue();

                wanted = WantedMesh(key, capturedAt, captured, claimed, out var refusal, out var older);

                if (wanted == null) return older ? AlreadyServed(key, refusal) : RejectMesh(key, refusal);
            }

            byte[] bytes;

            // ONE post or SEVERAL. A mesh past what one HTTP body can carry to a stock SPT host (see
            // MaxMeshPartBytes) arrives in parts, each held in the staging until the last one lands; the
            // whole mesh is then put back together and goes through EXACTLY the checks a single-post mesh
            // does below - its length against the declared total, its sha256 against the claim, the header
            // walk, the fit and the budgets. So a part is only ever trusted as far as "it is a slice of the
            // mesh the staged capture named", and that claim is proven on the whole before anything of it
            // is used.
            var parts = Math.Max(request.Parts, 1);

            if (parts > 1)
            {
                var answer = HoldMeshPart(key, request, capturedAt, captured, claimed, parts, out var assembled);

                if (assembled == null) return answer!;

                bytes = assembled;
            }
            else
            {
                var encoded = request.DataBase64 ?? "";

                if (encoded.Length == 0) return RejectMesh(key, "the post carries no mesh");

                // One post can never carry more than a part: a mesh past that comes in parts (the ceil rule).
                if (encoded.Length > MaxEncodedMeshPartChars)
                    return RejectMesh(key, $"the mesh is larger than the {Mb(MaxMeshPartBytes)} MB one post may carry");

                try
                {
                    bytes = Convert.FromBase64String(encoded);
                }
                catch (FormatException)
                {
                    return RejectMesh(key, "the mesh is not base64");
                }
            }

            if (bytes.Length == 0) return RejectMesh(key, "the mesh decodes to nothing");

            if (bytes.Length > _meshCeiling)
                return RejectMesh(key,
                    $"the mesh is {bytes.Length:N0} bytes, past the {_meshCeiling:N0} a map's mesh may be on this host");

            // The whole upload is held to the size the STAGED capture declared (D12): a mesh of any other size
            // is not the one the capture described, whatever it hashes to.
            if (wanted.Mesh != null && request.Bytes != wanted.Mesh.Bytes)
                return RejectMesh(key,
                    $"the mesh says it is {request.Bytes:N0} bytes, but the capture's meta says {wanted.Mesh.Bytes:N0}");

            // Two numbers from one machine that disagree mean the file was not read whole. Cheap, and
            // it catches a truncated read on the SENDER, which the sha below would also catch but
            // without saying what happened.
            if (request.Bytes != bytes.Length)
                return RejectMesh(key,
                    $"the mesh says it is {request.Bytes:N0} bytes but {bytes.Length:N0} arrived");

            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            if (!string.Equals(actual, claimed, StringComparison.OrdinalIgnoreCase))
                return RejectMesh(key, "the mesh's bytes do not hash to the sha256 it was sent with");

            // FROM HERE ON, EVERY REFUSAL IS OF THE GENUINE FILE - the bytes hash to the sha the staged
            // capture named - and that is what makes the next distinction safe to draw. A refusal that
            // no retry could change (the file is unreadable, does not fit its own pictures, or does not
            // fit the budget) serves the set FLAT instead of leaving its floors staged for a day: the
            // rule everywhere else in the transport is that a mesh problem costs the mesh and never the
            // map. None of these can be triggered by a stranger, because a stranger cannot produce bytes
            // with that sha - which is also why the corrupt-transfer refusals ABOVE stay plain refusals:
            // bytes that do not match their sha could be anybody's, and letting them flatten a capture
            // would hand any peer a way to strip the geometry off somebody else's map.
            //
            // A transient failure - the host could not WRITE the file - is the other kind: the floors
            // keep waiting, as they always have, because nothing about the capture is wrong.
            string? permanent = null;

            // The header, before anything is written: a file whose magic, version or counts are wrong
            // is one no client could draw, and storing it would serve it to every client in the group.
            if (!MeshHeaderIsUsable(bytes, MeshInflateBound(wanted.Mesh?.Cells ?? 0L, wanted.Mesh?.Triangles ?? 0L),
                    out var problem, out var facts))
                permanent = problem;
            else if (!MeshFitsMeta(facts, wanted, out var misfit))
                permanent = misfit;

            string staging;
            MapCaptureMetaDto ready;
            var flattened = "";

            lock (_lock)
            {
                if (!_loaded) Load();

                // AGAIN, and not as ceremony: the gate above ran outside this lock and the header walk
                // took milliseconds, during which another client's post could have completed this set or
                // a boot could have swept it away. This is the check that decides what is written.
                var staged = WantedMesh(key, capturedAt, captured, actual, out var refusal, out var older);

                if (staged == null) return older ? AlreadyServed(key, refusal) : RejectMesh(key, refusal);

                staging = StagingFolder(key, capturedAt);

                // A completion of this capture is in flight. WantedMesh has just shown the capture wants
                // THIS mesh, so the only way it can be completing is with this mesh already staged - in
                // which case the set is being stored in 3D and there is nothing to write. The other branch
                // is kept for honesty rather than expected.
                if (_completing.Contains(staging))
                    return MeshIsStaged(staging, actual)
                        ? new MapMeshUploadResponse { Accepted = true, Served = true }
                        : new MapMeshUploadResponse
                        {
                            Accepted = false,
                            Reason = "this capture is being completed right now - offer the mesh again"
                        };

                var floors = FilesByLevel(staging);
                var existing = SizeOf(MeshPath(staging));

                if (permanent == null)
                {
                    // Everything STAGED counts, the sides with the floors - they are on the disk and part of
                    // this set, which counting the floors alone forgot. Sides NOT yet staged are deliberately
                    // not reserved here, unlike on the floor route: the mesh is the 3D map and a side is one
                    // wall texture, so a budget squeeze is settled in the mesh's favour - a side that then
                    // does not fit is dropped when it arrives (Accept), and the set completes without it.
                    //
                    // The staged ATLAS pages count for the same reason - they are posted before the mesh, so by
                    // now they are on the disk - and pages still to come are not reserved, for the sides'.
                    var stagedSides = SidesByDir(staging);

                    var setBytes = floors.Sum(entry => SizeOf(entry.Value)) +
                                   stagedSides.Sum(entry => SizeOf(entry.Value)) +
                                   PagesByNumber(staging).Sum(entry => SizeOf(entry.Value));

                    if (setBytes + bytes.Length > _mapCeiling)
                        permanent = $"this capture would be {Mb(setBytes + bytes.Length)} MB with its mesh, past the " +
                                    $"{Mb(_mapCeiling)} MB one map may hold";
                }

                if (permanent == null)
                {
                    // IncomingBytes already holds every staged side; sides still to come are not reserved,
                    // for the reason above.
                    var total = _sets.Values.Sum(s => s.Bytes) + IncomingBytes() - existing;

                    if (total + bytes.Length > _storeCeiling)
                        permanent = $"the host already holds {Mb(total)} MB of map pictures, at the " +
                                    $"{Mb(_storeCeiling)} MB limit";
                }

                if (permanent != null)
                {
                    // Served flat - or, if a floor is still to come, staged to be served flat when it
                    // does. Either way the floors stop waiting for a mesh that is never coming.
                    if (!FlattenStaged(key, staging, staged, actual, permanent))
                        return RejectMesh(key, permanent);

                    var stillMissing = Missing(staged, staging, out var stillWaiting);

                    if (stillMissing > 0)
                        return new MapMeshUploadResponse
                        {
                            Accepted = false,
                            Reason = $"{permanent} - the pictures will be served without it, and are waiting for " +
                                     $"{stillWaiting}"
                        };

                    flattened = permanent;
                    _completing.Add(staging);
                    ready = staged;
                }
                else
                {
                    try
                    {
                        // The sidecar goes AFTER the mesh, and any older one goes first: between the two
                        // writes there is no sidecar, so MeshIsStaged hashes the file instead of trusting
                        // a sha that belongs to bytes that are no longer there. A crash in that gap costs
                        // one hash, never a wrong answer.
                        try { System.IO.File.Delete(MeshShaPath(staging)); } catch { /* there may be none */ }

                        WriteAtomic(MeshPath(staging), bytes);
                        WriteAtomic(MeshShaPath(staging), Encoding.UTF8.GetBytes(actual));
                    }
                    catch (Exception ex)
                    {
                        // TRANSIENT: nothing is wrong with the mesh, this host could not write it. The
                        // floors keep waiting, and the refusal says why.
                        return RejectMesh(key, $"the host could not store the mesh ({ex.Message})");
                    }

                    // ONE line per accepted mesh, at Information like the stored set's: a host wondering
                    // why a map draws flat wants to see this, and it is one line per capture rather than
                    // per floor.
                    _logger.Info(
                        $"Quest Tracker: map mesh for '{key}' stored - {Mb(bytes.Length)} MB, sha {Short(actual)}, " +
                        $"captured {capturedAt} by client " +
                        $"{(string.IsNullOrEmpty(request.ClientVersion) ? "unknown" : Clip(request.ClientVersion, MaxFreeTextLength))}.");

                    var missing = Missing(staged, staging, out var waitingFor);

                    if (missing > 0)
                        return new MapMeshUploadResponse
                        {
                            Accepted = true,
                            Reason = $"waiting for {waitingFor}"
                        };

                    // The mesh was the last piece. Promoted from the STAGED meta rather than from
                    // anything in this request - this route is never told what a set looks like.
                    _completing.Add(staging);
                    ready = staged;
                }
            }

            var promoted = CompleteSet(key, ready, staging, Clip(request.ClientVersion ?? "", MaxFreeTextLength));

            if (promoted.Outcome != "complete")
                return new MapMeshUploadResponse { Accepted = false, Reason = promoted.Reason };

            // Complete. Flattened, the client is told the pictures are served WITHOUT its mesh - which
            // is a different sentence from "refused": nothing of the capture is being held, so the
            // player has nothing to wait for and nothing to redo but the geometry.
            return flattened.Length == 0
                ? new MapMeshUploadResponse { Accepted = true, Served = true }
                : new MapMeshUploadResponse
                {
                    Accepted = false,
                    Served = true,
                    Reason = $"{flattened} - the pictures are served without it"
                };
        }

        /// <summary>
        /// One PART of a mesh that arrives in several, held in the staging until every part has landed.
        /// Returns the answer to send for this post - or null, with <paramref name="assembled"/> set to the
        /// whole mesh, when this was the part that completed it; the caller then treats it exactly as a
        /// mesh that came in one post.
        ///
        /// A part is refused plainly, never by flattening anything: until the parts are joined and the
        /// whole hashes to the claim, nothing here is known to be the genuine file. The same cheap gate as a
        /// one-post mesh comes first - is a capture waiting for a mesh with this sha - so a stranger's parts
        /// cost nothing either.
        ///
        /// The parts are named for the whole mesh's sha and the number of parts, so two attempts at one
        /// capture that split it differently, or a different mesh, can never be joined into one file.
        /// Joining is done OUTSIDE the lock, as a set's completion is: under it the parts are only RENAMED to
        /// a name no other post uses, so nothing else can touch them while up to the mesh ceiling (at most 512 MiB) is read back.
        /// </summary>
        /// <param name="key">The canonical map name.</param>
        /// <param name="request">The post, carrying one part.</param>
        /// <param name="capturedAt">The capture it belongs to, clipped.</param>
        /// <param name="captured">That timestamp parsed.</param>
        /// <param name="claimed">The WHOLE mesh's sha256, as claimed.</param>
        /// <param name="parts">How many parts the whole comes in; more than one.</param>
        /// <param name="assembled">The whole mesh, when this post completed it; otherwise null.</param>
        private MapMeshUploadResponse? HoldMeshPart(
            string key, MapMeshUploadRequest request, string capturedAt, DateTime captured, string claimed,
            int parts, out byte[]? assembled)
        {
            assembled = null;

            if (parts > MaxMeshPartsCeiling)
                return RejectMesh(key, $"the mesh comes in {parts} parts, past the {MaxMeshPartsCeiling} one mesh may");

            if (request.Part < 0 || request.Part >= parts)
                return RejectMesh(key, $"part {request.Part} is not one of the mesh's {parts} parts");

            if (request.Bytes <= 0 || request.Bytes > _meshCeiling)
                return RejectMesh(key,
                    $"the mesh says it is {request.Bytes:N0} bytes, which is not a mesh up to {_meshCeiling:N0} on this host");

            // The ceil rule (D13): at least ceil(bytes / the part ceiling) parts, so no part carries more than
            // one post can.
            if (parts < MinMeshParts(request.Bytes))
                return RejectMesh(key,
                    $"a mesh of {request.Bytes:N0} bytes comes in at least {MinMeshParts(request.Bytes)} parts of up to " +
                    $"{MaxMeshPartBytes:N0}, not {parts}");

            var encoded = request.DataBase64 ?? "";

            if (encoded.Length == 0) return RejectMesh(key, "the post carries no mesh part");

            if (encoded.Length > MaxEncodedMeshPartChars)
                return RejectMesh(key, $"a mesh part is larger than the {Mb(MaxMeshPartBytes)} MB one may be");

            byte[] part;

            try
            {
                part = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return RejectMesh(key, "the mesh part is not base64");
            }

            if (part.Length == 0 || part.Length > MaxMeshPartBytes)
                return RejectMesh(key, $"a mesh part of {part.Length:N0} bytes is not one up to {MaxMeshPartBytes:N0}");

            string[] claimedFiles;
            string joining;

            // Set when a BUDGET refusal of this mesh flattened the staged set and every floor and side is
            // in: the set is then completed flat, outside the lock, exactly as AcceptMesh does it.
            MapCaptureMetaDto? flatReady = null;
            var flatReason = "";

            lock (_lock)
            {
                if (!_loaded) Load();

                SweepStaleStagingIfDue();

                var staged = WantedMesh(key, capturedAt, captured, claimed, out var refusal, out var older);

                if (staged == null) return older ? AlreadyServed(key, refusal) : RejectMesh(key, refusal);

                var staging = StagingFolder(key, capturedAt);

                // A completion - or another part's JOIN - of this capture is in flight: see the same guard
                // in AcceptMesh. The join claims the folder too, so nothing writes a part into it or sweeps
                // it while up to the mesh ceiling of parts is being read back outside the lock.
                if (_completing.Contains(staging))
                    return MeshIsStaged(staging, claimed)
                        ? new MapMeshUploadResponse { Accepted = true, Served = true }
                        : new MapMeshUploadResponse
                        {
                            Accepted = false,
                            Reason = "this capture is being completed right now - offer the mesh again"
                        };

                // The whole the parts claim to be must be the mesh the staged capture described - the same
                // size its meta declared - or the budgets below would be judged on a number a sender chose.
                if (staged.Mesh != null && request.Bytes != staged.Mesh.Bytes)
                    return RejectMesh(key,
                        $"the mesh's parts say it is {request.Bytes:N0} bytes, but the capture's meta says " +
                        $"{staged.Mesh.Bytes:N0}");

                var paths = Enumerable.Range(0, parts).Select(i => MeshPartPath(staging, claimed, i, parts)).ToArray();
                var others = paths.Where((_, i) => i != request.Part).Sum(SizeOf);

                // The parts may never add up to more than the whole they claim to be - which is also what
                // bounds how much one upload can make this host hold before its sha is known.
                if (others + part.Length > request.Bytes)
                    return RejectMesh(key,
                        $"the mesh's parts add up to more than the {request.Bytes:N0} bytes it says it is");

                // THE BUDGETS, judged on the WHOLE mesh at its FIRST part, and a failure is PERMANENT - it
                // flattens the staged set exactly as a one-post mesh's budget failure does (AcceptMesh), rather
                // than refusing the part and leaving the floors, the sides and any held parts staged for a day.
                // Safe to act on before the parts are joined and hashed, for a reason worth writing down: the
                // verdict uses no byte of the part - only the whole's declared size, which has just been held
                // to the staged capture's own meta, and this host's own totals - and reaching it at all took
                // the sha the staged capture named.
                string? permanent = null;

                var pictures = FilesByLevel(staging).Sum(e => SizeOf(e.Value)) + SidesByDir(staging).Sum(e => SizeOf(e.Value)) +
                               PagesByNumber(staging).Sum(e => SizeOf(e.Value));

                if (pictures + request.Bytes > _mapCeiling)
                    permanent = $"this capture would be {Mb(pictures + request.Bytes)} MB with its mesh, past the " +
                                $"{Mb(_mapCeiling)} MB one map may hold";

                var held = paths.Sum(SizeOf);
                var total = _sets.Values.Sum(s => s.Bytes) + IncomingBytes() - held;

                if (permanent == null && total + request.Bytes > _storeCeiling)
                    permanent = $"the host holds {Mb(total)} MB of map pictures, and this {Mb(request.Bytes)} MB mesh " +
                                $"would take it past the {Mb(_storeCeiling)} MB limit";

                if (permanent != null)
                {
                    // Every part this capture holds goes with it - none of them will ever be joined.
                    foreach (var path in paths)
                        try { System.IO.File.Delete(path); } catch { /* the stale sweep takes it */ }

                    if (!FlattenStaged(key, staging, staged, claimed, permanent)) return RejectMesh(key, permanent);

                    var stillMissing = Missing(staged, staging, out var stillWaiting);

                    if (stillMissing > 0)
                        return new MapMeshUploadResponse
                        {
                            Accepted = false,
                            Reason = $"{permanent} - the pictures will be served without it, and are waiting for " +
                                     $"{stillWaiting}"
                        };

                    _completing.Add(staging);
                    flatReady = staged;
                    flatReason = permanent;
                    claimedFiles = Array.Empty<string>();
                    joining = staging;
                }
                else
                {
                    try
                    {
                        WriteAtomic(paths[request.Part], part);
                    }
                    catch (Exception ex)
                    {
                        return RejectMesh(key, $"the host could not store the mesh part ({ex.Message})");
                    }

                    var count = paths.Count(System.IO.File.Exists);

                    if (count < parts)
                        return new MapMeshUploadResponse
                        {
                            Accepted = true,
                            Reason = $"holding part {count} of {parts}",
                            PartsHeld = count,
                            Parts = parts
                        };

                    // Every part is here. The folder is CLAIMED for the join - so no other post writes into it
                    // and no sweep or DropStaging removes it while the parts are read back outside the lock -
                    // and the parts are taken out of every other post's reach by a rename, one name per join.
                    _completing.Add(staging);
                    joining = staging;

                    var join = Guid.NewGuid().ToString("N").Substring(0, 12);

                    claimedFiles = new string[parts];

                    var moved = 0;

                    try
                    {
                        for (; moved < parts; moved++)
                        {
                            claimedFiles[moved] = paths[moved] + ".join-" + join;
                            System.IO.File.Move(paths[moved], claimedFiles[moved]);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Put back what was moved, so a failure here leaves the parts where the next post of
                        // the last part will find them - and never a stray .join-* file nothing will read and
                        // the store's total would count until the next boot.
                        for (var i = 0; i < moved; i++)
                        {
                            try { System.IO.File.Move(claimedFiles[i], paths[i]); }
                            catch { try { System.IO.File.Delete(claimedFiles[i]); } catch { /* the sweep takes it */ } }
                        }

                        _completing.Remove(staging);

                        return RejectMesh(key, $"the host could not gather the mesh's parts ({ex.Message})");
                    }
                }
            }

            if (flatReady != null)
            {
                var promoted = CompleteSet(key, flatReady, joining, Clip(request.ClientVersion ?? "", MaxFreeTextLength));

                return promoted.Outcome == "complete"
                    ? new MapMeshUploadResponse
                    {
                        Accepted = false,
                        Served = true,
                        Reason = $"{flatReason} - the pictures are served without it"
                    }
                    : new MapMeshUploadResponse { Accepted = false, Reason = promoted.Reason };
            }

            // Outside the lock: read the parts back in order, into one array of the declared size, and let
            // them go. The whole is then checked by the caller as if it had come in one post.
            try
            {
                var whole = new byte[request.Bytes];
                var at = 0;

                foreach (var file in claimedFiles)
                {
                    var data = System.IO.File.ReadAllBytes(file);

                    if (at + data.Length > whole.Length)
                        return RejectMesh(key, $"the mesh's parts add up to more than the {request.Bytes:N0} bytes it says it is");

                    Buffer.BlockCopy(data, 0, whole, at, data.Length);
                    at += data.Length;
                }

                if (at != whole.Length)
                    return RejectMesh(key, $"the mesh's parts add up to {at:N0} bytes, not the {request.Bytes:N0} it says it is");

                assembled = whole;
                return null;
            }
            catch (Exception ex)
            {
                return RejectMesh(key, $"the host could not read the mesh's parts back ({ex.Message})");
            }
            finally
            {
                foreach (var file in claimedFiles)
                    try { System.IO.File.Delete(file); } catch { /* the staging sweep takes it */ }

                // The join is over; the caller takes the folder again, under the lock, when it completes the
                // set. Released here rather than there so the caller's own "being completed" guard does not
                // meet this claim and turn the mesh away.
                lock (_lock) _completing.Remove(joining);
            }
        }

        /// <summary>The fewest parts a mesh of this many bytes may come in: ceil(bytes / <see cref="MaxMeshPartBytes"/>).</summary>
        internal static long MinMeshParts(long bytes) => (Math.Max(0L, bytes) + MaxMeshPartBytes - 1) / MaxMeshPartBytes;

        /// <summary>Where one part of a mesh waits, named for the whole mesh's sha and the number of parts -
        /// see <see cref="HoldMeshPart"/>. Not a picture extension, so FilesByLevel never reads it as one.</summary>
        private static string MeshPartPath(string staging, string sha256, int index, int parts) =>
            System.IO.Path.Combine(staging,
                $"mesh-{sha256.Substring(0, 12).ToLowerInvariant()}-of{parts.ToString(CultureInfo.InvariantCulture)}" +
                $".part{index.ToString(CultureInfo.InvariantCulture)}");

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

            byte[]? meshBytes = null;

            lock (_lock)
            {
                if (!_loaded) Load();

                if (!_sets.TryGetValue(key, out var set) || set.Meta.Mesh == null) return dto;

                var mesh = set.Meta.Mesh;

                // The name was written by CommitSet or checked by Load, so it is a bare file name inside
                // this map's folder. Nothing here comes from the request but the map.
                if (string.IsNullOrEmpty(mesh.File) || !StoredMeshFileName.IsMatch(mesh.File)) return dto;

                try
                {
                    // The READ stays under the lock - it must not meet CommitSet replacing the file, which
                    // Windows refuses while a handle is open - but the base64 of up to 512 MiB (MeshAbsolute: a 716 M-
                    // character string, under .NET's string limit) is built after it is released (review F39): every other route, the index and
                    // image routes the game calls on its main thread among them, waits on this lock.
                    meshBytes = System.IO.File.ReadAllBytes(System.IO.Path.Combine(Folder, key, mesh.File));

                    dto.Stamp = set.Stamp;
                    dto.Sha256 = mesh.Sha256;
                    dto.Bytes = meshBytes.Length;
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
                    meshBytes = null;
                }
            }

            if (meshBytes != null) dto.DataBase64 = Convert.ToBase64String(meshBytes);

            return dto;
        }

        // ---------------------------------------------------------------------------------------
        // Completing a set
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Completes a set whose every piece is staged: PREPARED outside the lock, COMMITTED under it.
        ///
        /// Why two phases. A set is up to a map's ceiling (an eighth of the store's) - eight floors, four sides, eight atlas pages and a mesh -
        /// and completing it
        /// means reading all of it, hashing the mesh again and hashing the whole of it for the stamp. Done
        /// under <see cref="_lock"/>, as it once was, that is well over 100 MB of reading and hashing while the index
        /// and image routes - which the game calls on its MAIN THREAD - waited for the same lock. So
        /// <see cref="PrepareSet"/> does every read and every hash with no lock held, and
        /// <see cref="CommitSet"/> takes the lock only to check nothing moved and to write.
        ///
        /// What stays under the lock, said plainly: writing the set into the map's folder (pictures,
        /// mesh, the sweep of what the new meta does not name, the meta last), swapping it into
        /// <see cref="_sets"/>, and dropping the staging. Those have to be one step as far as any other
        /// request can see - an index answered between the meta landing and the swap would name a set the
        /// image route does not yet serve - and a directory swap is not atomic on Windows either, so the
        /// lock is what makes them one. It is disk writing, not hashing, and it is the same writing the
        /// old single-phase version did.
        /// </summary>
        /// <param name="key">The canonical map name.</param>
        /// <param name="meta">The meta to promote - the floor post's own, or the staged one.</param>
        /// <param name="staging">The capture's staging folder.</param>
        /// <param name="clientVersion">For the stored-set line.</param>
        private MapUploadResponse CompleteSet(string key, MapCaptureMetaDto meta, string staging, string clientVersion)
        {
            try
            {
                var prepared = PrepareSet(key, meta, staging);

                lock (_lock)
                {
                    try
                    {
                        if (!_loaded) Load();

                        return CommitSet(key, prepared, staging, clientVersion);
                    }
                    finally
                    {
                        // Released in the SAME lock as the commit, so no post can see the set committed and
                        // the folder still claimed.
                        _completing.Remove(staging);
                    }
                }
            }
            finally
            {
                // And again if anything above threw before reaching that lock: a folder left in
                // _completing would turn every later post of that capture into "being completed right
                // now" for the life of the process. Remove is idempotent.
                lock (_lock) _completing.Remove(staging);
            }
        }

        /// <summary>Everything a promotion reads and hashes, done before the lock is taken - see
        /// <see cref="CompleteSet"/>. The sizes and write times are what <see cref="CommitSet"/> checks
        /// again under the lock, so a file that changed between the two is noticed rather than
        /// served.</summary>
        private sealed class PreparedSet
        {
            public MapCaptureMetaDto Meta = new();
            public string CapturedAt = "";
            public readonly List<PreparedFile> Floors = new();

            /// <summary>The side pictures, in <see cref="SideDirs"/> order.</summary>
            public readonly List<PreparedFile> Sides = new();

            /// <summary>The atlas pages, in page order.</summary>
            public readonly List<PreparedFile> Atlas = new();
            public PreparedFile? Mesh;
            public byte[] MetaBytes = Array.Empty<byte>();
            public string Stamp = "";
            public long Bytes;

            /// <summary>Why the staged files could not be prepared, or null. The set then stays staged,
            /// exactly as a failed write left it before.</summary>
            public string? Problem;
        }

        /// <summary>One staged file as it was read: where it was, what it weighed and when it was last
        /// written, the name the SERVER gives it, and its bytes.</summary>
        private sealed class PreparedFile
        {
            public string Source = "";
            public long Size;
            public DateTime Written;
            public string Name = "";
            public byte[] Data = Array.Empty<byte>();

            /// <summary>The file's sha256, hashed in PrepareSet (outside the lock) for the stamp cache.</summary>
            public string Sha = "";
        }

        /// <summary>
        /// Reads a complete staged set, hashes it, and works out the meta and the stamp it will be served
        /// under - all with NO lock held (see <see cref="CompleteSet"/>). Touches nothing but the
        /// request's own meta object, which it rewrites as the stored set will name itself.
        ///
        /// Every name here is THE SERVER'S, never the client's: a name from a peer is a path, and these
        /// are joined onto a folder every client then reads from. The mesh's bytes are hashed again
        /// against the sha its meta names, so a staged file swapped since it was checked on the way in is
        /// refused rather than served under a sha nobody could verify.
        /// </summary>
        private static PreparedSet PrepareSet(string key, MapCaptureMetaDto meta, string staging)
        {
            var prepared = new PreparedSet { Meta = meta, CapturedAt = meta.CapturedAt ?? "" };

            try
            {
                var staged = FilesByLevel(staging);

                // The CANONICAL name, so the stored file agrees with the folder it sits in and with the
                // index entry that serves it: a Factory night capture is stored as factory4_day's
                // picture, and a meta still claiming factory4_night would have package.ps1's gates and a
                // reader of the file disagreeing about which map this is.
                meta.Map = key;

                foreach (var floor in meta.Floors.OrderBy(f => f.Level))
                {
                    if (!staged.TryGetValue(floor.Level, out var source))
                    {
                        prepared.Problem = $"floor {floor.Level} is no longer staged";
                        return prepared;
                    }

                    var file = Read(source);
                    var format = Format(System.IO.Path.GetExtension(source).TrimStart('.')) ?? "jpg";

                    file.Name = $"{key}-{floor.Level.ToString(CultureInfo.InvariantCulture)}.{format}";
                    floor.File = file.Name;

                    file.Sha = HashOf(file.Data);
                    prepared.Floors.Add(file);
                    prepared.Bytes += file.Data.Length;
                }

                // The sides the meta still names - a dropped one was taken out of it on the way in - in
                // the fixed order, each under the SERVER'S name for it, exactly as a floor.
                if (meta.Sides != null && meta.Sides.Count > 0)
                {
                    var stagedSides = SidesByDir(staging);

                    foreach (var dir in SideDirs)
                    {
                        var side = meta.Sides.FirstOrDefault(sd => sd.Dir == dir);

                        if (side == null) continue;

                        if (!stagedSides.TryGetValue(dir, out var source))
                        {
                            prepared.Problem = $"the {dir} side is no longer staged";
                            return prepared;
                        }

                        var file = Read(source);

                        file.Name = SideName(key, dir);
                        side.File = file.Name;

                        file.Sha = HashOf(file.Data);
                        prepared.Sides.Add(file);
                        prepared.Bytes += file.Data.Length;
                    }

                    // Stored in the fixed order too, so the meta written reads the same on every path.
                    meta.Sides = SideDirs.Select(d => meta.Sides.FirstOrDefault(sd => sd.Dir == d))
                        .Where(sd => sd != null).Select(sd => sd!).ToList();
                }
                else
                {
                    meta.Sides = null;
                }

                // The atlas pages, in page order, under the SERVER'S names - and with the sha256 the meta
                // will be served with REWRITTEN to the hash of the stored JPEG. The meta arrived naming the
                // capture's own PNG, which no machine but the capturer's has; from here on the sha is the
                // served file's, and that is what a downloader and the packaging gate hold a page to. Only
                // when the set keeps a mesh: pages drape buildings, and a set promoted flat has none.
                if (meta.Mesh != null && meta.Atlas != null && meta.Atlas.Count > 0)
                {
                    var stagedPages = PagesByNumber(staging);
                    var ordered = meta.Atlas.OrderBy(p => p.Page).ToList();

                    foreach (var page in ordered)
                    {
                        if (!stagedPages.TryGetValue(page.Page, out var source))
                        {
                            prepared.Problem = $"atlas page {page.Page} is no longer staged";
                            return prepared;
                        }

                        var file = Read(source);

                        file.Name = AtlasName(key, page.Page);
                        page.File = file.Name;
                        page.Sha256 = Convert.ToHexString(SHA256.HashData(file.Data)).ToLowerInvariant();

                        file.Sha = page.Sha256;
                        prepared.Atlas.Add(file);
                        prepared.Bytes += file.Data.Length;
                    }

                    meta.Atlas = ordered;
                }
                else
                {
                    meta.Atlas = null;
                }

                if (meta.Mesh != null)
                {
                    var source = MeshPath(staging);

                    if (!System.IO.File.Exists(source))
                    {
                        prepared.Problem = "the staged mesh is gone";
                        return prepared;
                    }

                    var mesh = Read(source);
                    var hash = Convert.ToHexString(SHA256.HashData(mesh.Data)).ToLowerInvariant();

                    if (!string.Equals(hash, meta.Mesh.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        prepared.Problem =
                            $"the staged mesh hashes to {Short(hash)}, not the {Short(meta.Mesh.Sha256)} its meta names";
                        return prepared;
                    }

                    mesh.Name = MeshName(key);

                    meta.Mesh.File = mesh.Name;
                    meta.Mesh.Sha256 = hash;
                    meta.Mesh.Bytes = mesh.Data.Length;

                    mesh.Sha = hash;
                    prepared.Mesh = mesh;
                    prepared.Bytes += mesh.Data.Length;
                }

                prepared.MetaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, FileOptions));

                // Over the bytes as they WILL BE written - the floors in level order, then the sides in
                // SideDirs order, then the atlas pages in page order, then the mesh - so the boot that reads
                // this folder back computes the
                // same value from the same files. That identity is what makes the stamp worth comparing
                // at all. It is cached beside the set (WriteStampCache) but never trusted over a file that
                // changed - see the class comment.
                prepared.Stamp = StampOf(
                    prepared.MetaBytes,
                    prepared.Floors.Select(f => f.Data).Concat(prepared.Sides.Select(sd => sd.Data))
                        .Concat(prepared.Atlas.Select(p => p.Data)),
                    prepared.Mesh?.Data);

                return prepared;
            }
            catch (Exception ex)
            {
                prepared.Problem = ex.Message;
                return prepared;
            }
        }

        /// <summary>A staged file, read with the size and write time <see cref="CommitSet"/> will compare
        /// against. The time is taken BEFORE the read, so a write that lands during it changes the time
        /// and is caught.
        ///
        /// Read with no lock held, and safe only because nothing writes into this folder meanwhile - see
        /// <see cref="_completing"/>. Windows refuses to replace a file that has any open handle, and
        /// opening this one with delete sharing does NOT change that (tried, and measured in the
        /// two-thread harness case: the same 3-4 failures in 100 either way), so the guard is the
        /// only thing that makes the read and a concurrent atomic replace unable to meet.</summary>
        private static PreparedFile Read(string path)
        {
            var info = new System.IO.FileInfo(path);
            var written = info.LastWriteTimeUtc;
            var data = System.IO.File.ReadAllBytes(path);

            return new PreparedFile { Source = path, Size = data.Length, Written = written, Data = data };
        }

        /// <summary>Whether a prepared file is still exactly what is staged: there, the same size, and not
        /// written since it was read.</summary>
        private static bool Unchanged(PreparedFile file)
        {
            try
            {
                var info = new System.IO.FileInfo(file.Source);

                return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.Written;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Moves a prepared set into place and makes it the one this host serves. Caller holds the lock.
        ///
        /// First, whether it may: another request can have completed this capture while it was being
        /// prepared (then it is simply complete), a newer set can have been promoted (then this one is
        /// older, as the floor route would have said), and a staged file can have been re-posted or swept
        /// (then this preparation is stale, and the post that changed it completes the set itself).
        ///
        /// Then the writes, in the order that matters: pictures first, each one written beside its name
        /// and moved over it, then the MESH if the set has one, then the files the new set does not name,
        /// and the meta LAST. The meta is what names the pictures and the mesh, so a failure part-way
        /// through never leaves a meta pointing at a file that was never written - the case that would
        /// serve a client a map with a hole in it and a stamp claiming it was whole. The mesh goes before
        /// the sweep for the reason a picture does: the sweep deletes everything this method did not
        /// write.
        ///
        /// WHAT IT DOES NOT PROMISE, said out loud because a folder is not a database: a crash in the gap
        /// between the last picture and the meta leaves the new pictures beside the old meta. The loader
        /// then skips the folder if the old meta names a file the new set replaced under a different
        /// name, and otherwise reads the OLD meta over the NEW pictures - wrong if the extent changed,
        /// merely stale if not. Closing that gap needs a directory swap, which Windows cannot do
        /// atomically either, so the gap is accepted: it is microseconds wide, it needs a crash inside
        /// it, and the repair is one more capture of that map.
        /// </summary>
        private MapUploadResponse CommitSet(string key, PreparedSet prepared, string staging, string clientVersion)
        {
            var captured = ParseStamp(prepared.CapturedAt);

            if (_sets.TryGetValue(key, out var held))
            {
                var heldAt = ParseStamp(held.Meta.CapturedAt);

                // Completed by a request that got here first - the last floor and the mesh can arrive
                // together. Not a failure: the set this post was part of is served.
                if (heldAt == captured)
                    return new MapUploadResponse { Outcome = "complete", FloorsHeld = held.Meta.Floors.Count };

                if (heldAt > captured) return Reject(key, "older than the set on the host");
            }

            if (prepared.Problem != null)
            {
                // The set stays staged. Nothing was promised to a client and the previous set is still
                // being served; the next post of any part of this capture tries again.
                WarnOnce(key, $"could not complete the picture set ({prepared.Problem})");

                return new MapUploadResponse
                {
                    Outcome = "rejected",
                    Reason = $"the host could not store the set ({prepared.Problem})",
                    FloorsHeld = prepared.Floors.Count
                };
            }

            if (!prepared.Floors.All(Unchanged) || !prepared.Sides.All(Unchanged) || !prepared.Atlas.All(Unchanged) ||
                (prepared.Mesh != null && !Unchanged(prepared.Mesh)))
                return new MapUploadResponse
                {
                    Outcome = "stored",
                    Reason = "the staged set changed while it was being completed - the post that changed it completes it",
                    FloorsHeld = prepared.Floors.Count
                };

            var meta = prepared.Meta;
            var target = System.IO.Path.Combine(Folder, key);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                System.IO.Directory.CreateDirectory(target);

                foreach (var floor in prepared.Floors)
                {
                    WriteAtomic(System.IO.Path.Combine(target, floor.Name), floor.Data);
                    written.Add(floor.Name);
                }

                // The sides with the pictures, before the sweep - so a side this set carries is kept, and
                // a side the PREVIOUS set carried and this one does not is swept like a stale floor.
                foreach (var side in prepared.Sides)
                {
                    WriteAtomic(System.IO.Path.Combine(target, side.Name), side.Data);
                    written.Add(side.Name);
                }

                // The atlas pages the same way: a page this set carries is kept, a page the previous set
                // carried past this one's last is swept.
                foreach (var page in prepared.Atlas)
                {
                    WriteAtomic(System.IO.Path.Combine(target, page.Name), page.Data);
                    written.Add(page.Name);
                }

                if (prepared.Mesh != null)
                {
                    WriteAtomic(System.IO.Path.Combine(target, prepared.Mesh.Name), prepared.Mesh.Data);
                    written.Add(prepared.Mesh.Name);
                }

                var metaName = MetaName(key);

                // Whatever the previous set left that this one does not name - a fourth floor on a map
                // that now measures three, a .png replaced by a .jpg, the mesh of a capture this one
                // replaces without one - goes before the meta lands, so the folder never holds a file
                // nothing points at.
                foreach (var path in System.IO.Directory.EnumerateFiles(target))
                {
                    var name = System.IO.Path.GetFileName(path);

                    if (written.Contains(name) || name.Equals(metaName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try { System.IO.File.Delete(path); } catch { /* a leftover file is not a failure */ }
                }

                WriteAtomic(System.IO.Path.Combine(target, metaName), prepared.MetaBytes);

                // The stamp cache, AFTER the meta, so the boot that reads this folder back does not hash it
                // again. Best effort: a cache that failed to write costs one hash at the next boot.
                WriteStampCache(target, key, prepared.Stamp,
                    prepared.Floors.Concat(prepared.Sides).Concat(prepared.Atlas)
                        .Concat(prepared.Mesh == null ? Array.Empty<PreparedFile>() : new[] { prepared.Mesh })
                        .Select(f => (f.Name, f.Sha)).ToList());

                _sets[key] = new StoredSet
                {
                    Key = key,
                    Stamp = prepared.Stamp,
                    Bytes = prepared.Bytes,
                    Meta = meta
                };

                // Every staging folder for this map, not only this one: an abandoned earlier attempt is
                // exactly what the disk should not keep once a newer set is served. And the stale sweep over
                // EVERY map's staging, once per completed set - cheap, and the one moment a host that is never
                // restarted is sure to reach.
                DropStaging(key, committing: staging, committedAt: captured);
                SweepStaleStagingIfDue(now: true);

                _logger.Info(
                    $"Quest Tracker: map picture set for '{key}' stored - {prepared.Floors.Count} floor(s)" +
                    $"{(prepared.Sides.Count == 0 ? "" : $", {prepared.Sides.Count} side(s)")}" +
                    $"{(prepared.Atlas.Count == 0 ? "" : $", {prepared.Atlas.Count} atlas page(s)")}" +
                    $"{(prepared.Mesh == null ? "" : $" and a {Mb(prepared.Mesh.Data.Length)} MB mesh")}, " +
                    $"{Mb(prepared.Bytes)} MB, captured {Clip(meta.CapturedAt, MaxFreeTextLength)} by client " +
                    $"{(clientVersion.Length == 0 ? "unknown" : clientVersion)}.");

                return new MapUploadResponse { Outcome = "complete", FloorsHeld = prepared.Floors.Count };
            }
            catch (Exception ex)
            {
                // The set stays staged. The previous set is still being served - the meta is written last
                // precisely so that is true - and the next post of any part of this capture tries again.
                WarnOnce(key, $"could not complete the picture set ({ex.Message})");

                return new MapUploadResponse
                {
                    Outcome = "rejected",
                    Reason = $"the host could not store the set ({ex.Message})",
                    FloorsHeld = prepared.Floors.Count
                };
            }
        }

        /// <summary>
        /// Gives up on a capture's mesh for good and makes the capture's floors stop waiting for it - the
        /// "a mesh problem costs the mesh, never the map" rule, for the refusals no retry could change.
        /// Caller holds the lock. False when the staging could not be rewritten, and then the caller
        /// refuses the post as before and the floors wait.
        ///
        /// Three writes: a MARKER naming the refused sha (so a later floor of this capture that still
        /// names that mesh is brought into line rather than refused as a clash with the staged meta), the
        /// staged meta WITHOUT its mesh block (so the completion check stops waiting and the set is
        /// promoted flat), and the staged mesh and its sidecar deleted if an earlier attempt left them.
        /// One Warning, naming the reason, because a host wondering why a map draws flat on every client
        /// wants to see exactly this.
        /// </summary>
        private bool FlattenStaged(string key, string staging, MapCaptureMetaDto staged, string sha, string reason)
        {
            try
            {
                WriteAtomic(RefusedMarkerPath(staging), Encoding.UTF8.GetBytes(sha.ToLowerInvariant()));

                staged.Mesh = null;

                // Its atlas pages with it: they texture the mesh's buildings and nothing else, so the flat
                // set neither waits for them nor serves them. Any already staged go with the staging.
                staged.Atlas = null;

                WriteAtomic(
                    System.IO.Path.Combine(staging, StagedMetaName),
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(staged, FileOptions)));

                try { System.IO.File.Delete(MeshPath(staging)); } catch { /* there may be none */ }
                try { System.IO.File.Delete(MeshShaPath(staging)); } catch { /* there may be none */ }
            }
            catch (Exception ex)
            {
                WarnOnce(key, $"could not mark the refused mesh ({ex.Message})");
                return false;
            }

            // Its own line rather than WarnOnce's "refused a map picture" - no picture was refused, and the
            // sentence a host reads should say what happened. Deduped through the same capped set.
            bool first;

            lock (_rejectionsLogged)
                first = _rejectionsLogged.Count < MaxRejectionsLogged && _rejectionsLogged.Add($"{key}|flat|{reason}");

            if (first)
                _logger.Warning(
                    $"Quest Tracker: the map mesh for '{key}' was refused - {reason} - so its pictures are served " +
                    "without it and that map draws flat on every client.");

            return true;
        }

        /// <summary>The marker <see cref="FlattenStaged"/> writes: the sha of a mesh this host refused for
        /// good, beside the capture's staged floors. Its extension is not a picture format, so
        /// <see cref="FilesByLevel"/> skips it; it goes with the staging folder when the set is
        /// promoted.</summary>
        private static string RefusedMarkerPath(string staging) => System.IO.Path.Combine(staging, "mesh.refused");

        /// <summary>The refused mesh's sha, or null when none was refused for this capture.</summary>
        private static string? RefusedMeshSha(string staging)
        {
            try
            {
                var path = RefusedMarkerPath(staging);

                return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Reading what is already here
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The store's, a map's and a mesh's ceilings from the free disk of the maps' volume (D9-D11), once per
        /// process: a quarter of the free disk, clamped to 1.5..32 GiB; an eighth of that a map; that less the
        /// map's pictures and pages at their caps for the mesh, never over 512 MiB. 200 GB free -> 32 GiB, 4 GiB,
        /// 512 MiB; 10 GB -> 2.5 GiB, 320 MiB, 242 MiB; 2 GB -> the 1.5 GiB floor, 192 MiB, 114 MiB. A disk that
        /// cannot be measured gets the floor. Caller holds the lock.
        /// </summary>
        private void SizeCeilings()
        {
            _ceilingsSized = true;

            long free;

            try
            {
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Folder));
                free = string.IsNullOrEmpty(root) ? 0L : new System.IO.DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                free = 0L;
            }

            var ceilings = CeilingsFor(free);
            _storeCeiling = ceilings.Store;
            _mapCeiling = ceilings.Map;
            _meshCeiling = ceilings.Mesh;

            var line = $"Quest Tracker: maps: store ceiling {Mb(_storeCeiling)} MB ({Mb(free)} MB free at boot), " +
                       $"a map {Mb(_mapCeiling)} MB, a mesh {Mb(_meshCeiling)} MB.";

            // At Information on a host that takes uploads - the ceilings are what an upload is judged on - and
            // a detail line otherwise, where they only bound the sets already on disk.
            if (AcceptsUploads) _logger.Info(line);
            else _logger.Detail(line);
        }

        /// <summary>The three ceilings for this much free disk (D9-D11). Pure, for the harness.</summary>
        /// <param name="free">Free bytes on the maps' volume; zero or less when unknown.</param>
        internal static (long Store, long Map, long Mesh) CeilingsFor(long free)
        {
            var store = Math.Clamp(Math.Max(0L, free) / 4, StoreFloor, StoreTop);
            var map = store / 8;
            var mesh = Math.Min(map - PicturesAndPagesPerMap, MeshAbsolute);

            return (store, map, mesh);
        }

        /// <summary>This host's mesh ceiling, as sized at boot (<see cref="SizeCeilings"/>).</summary>
        internal long MeshCeiling => _meshCeiling;

        /// <summary>Rebuilds the stamp cache from the folders. Caller holds the lock.</summary>
        private void Load()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Before the sets are read: the ceilings gate what is STAGED from now on (DropUnusableMesh on the
            // upload paths); a set already stored is served whatever the disk has become since.
            if (!_ceilingsSized) SizeCeilings();

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

                // The stamp cache (see the class comment): each file's sha256 is taken from it while the
                // file's size and write time still match, and hashed from the bytes otherwise.
                var cache = ReadStampCache(dir, key);
                var cached = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in cache?.Files ?? new List<CachedFile>())
                    if (entry?.Name != null && !cached.ContainsKey(entry.Name)) cached[entry.Name] = entry;

                var shas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                string ShaOf(string name)
                {
                    if (shas.TryGetValue(name, out var known)) return known;

                    var info = new System.IO.FileInfo(System.IO.Path.Combine(dir, name));
                    var sha = cached.TryGetValue(name, out var entry) && Matches(entry, info) && Sha256Hex.IsMatch(entry.Sha ?? "")
                        ? entry.Sha!.ToLowerInvariant()
                        : HashFile(info.FullName);

                    shas[name] = sha;
                    return sha;
                }

                // The files the stamp covers, in its order: floors by level, sides in SideDirs order, atlas
                // pages by page, and the mesh last.
                var order = new List<string>();

                foreach (var floor in meta.Floors.OrderBy(f => f.Level))
                {
                    // The name is about to be joined onto this folder's path. It was written by
                    // CommitSet, so a name that fails this was hand-edited or copied in from elsewhere -
                    // and "..\\..\\something" is what that check is for.
                    if (floor.File == null || !StoredFileName.IsMatch(floor.File))
                    {
                        _logger.Warning(
                            $"Quest Tracker: maps/{key} names a picture file this server will not read " +
                            $"('{Clip(floor.File ?? "", MaxFreeTextLength)}') - the whole set is skipped.");
                        return null;
                    }

                    if (!System.IO.File.Exists(System.IO.Path.Combine(dir, floor.File)))
                    {
                        _logger.Warning(
                            $"Quest Tracker: maps/{key} is missing {floor.File}, so the set is incomplete - " +
                            "skipped. Capture the map again, or copy the whole folder across.");
                        return null;
                    }

                    order.Add(floor.File);
                }

                // The sides, in the fixed order, each held to the stored-name rule and required to be on
                // disk. DROPPED one by one rather than fatal, for the mesh's reason below: a side textures
                // some walls, the set is whole without it, and refusing the folder would lose a map over
                // it. What is dropped leaves the meta, so no client asks for a side this host cannot send.
                if (meta.Sides != null && meta.Sides.Count > 0)
                {
                    var kept = new List<MapCaptureSideDto>();

                    foreach (var sideDir in SideDirs)
                    {
                        var side = meta.Sides.FirstOrDefault(sd => sd != null && sd.Dir == sideDir);

                        if (side == null) continue;

                        var sideFile = side.File ?? "";

                        // The name is joined onto this folder, so it is held to the stored-name rule first -
                        // exactly as a floor's is above.
                        if (!StoredSideFileName.IsMatch(sideFile) ||
                            !System.IO.File.Exists(System.IO.Path.Combine(dir, sideFile)))
                        {
                            NoteSideDropped(key, sideDir, meta.CapturedAt,
                                $"maps/{key} does not hold '{Clip(sideFile, MaxFreeTextLength)}'");
                            continue;
                        }

                        order.Add(sideFile);
                        kept.Add(side);
                    }

                    meta.Sides = kept.Count == 0 ? null : kept;
                }

                // The mesh, held to its own sha256 - the one field in the meta that cannot be checked by
                // looking at the file it describes. DROPPED rather than fatal, which is the opposite of how a
                // missing PICTURE is treated above, and deliberately so: the mesh is optional by
                // construction, so a set whose .bin was not copied across still draws in 2D on every client,
                // while refusing the whole folder would lose a map over a file nothing needs. The block goes
                // with it, so no client is told about a mesh this host cannot serve.
                string? meshFile = null;

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
                        var hash = ShaOf(file);

                        if (!string.Equals(hash, meta.Mesh.Sha256, StringComparison.OrdinalIgnoreCase))
                            why = $"holds a {file} that hashes to {Short(hash)}, not the " +
                                  $"{Short(meta.Mesh.Sha256!)} its meta names";
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
                        meshFile = file;
                    }
                }

                // The atlas pages, in page order, AFTER the sides in the stamp - PrepareSet's order. Each
                // held to the stored-name rule, required on disk and to hash to the sha the meta names (which
                // this server wrote: the served JPEG's). DROPPED one by one rather than fatal, for the sides'
                // reason - the buildings on a missing page fall back in the viewer. All of them go when the
                // set has no mesh to drape them on.
                if (meta.Atlas != null && meta.Atlas.Count > 0)
                {
                    var kept = new List<MapCaptureAtlasDto>();

                    if (meta.Mesh != null)
                    {
                        foreach (var page in meta.Atlas.Where(p => p != null).OrderBy(p => p.Page))
                        {
                            var pageFile = page.File ?? "";
                            string? why = null;

                            if (page.Page < 0 || page.Page >= MaxAtlasPages || kept.Any(k => k.Page == page.Page))
                                why = "it is not one page of eight";
                            else if (!StoredAtlasFileName.IsMatch(pageFile) || !System.IO.File.Exists(System.IO.Path.Combine(dir, pageFile)))
                                why = $"maps/{key} does not hold '{Clip(pageFile, MaxFreeTextLength)}'";
                            else
                            {
                                var hash = ShaOf(pageFile);

                                if (!string.Equals(hash, page.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                                    why = $"maps/{key}/{pageFile} hashes to {Short(hash)}, not the " +
                                          $"{Short(page.Sha256 ?? "")} its meta names";
                            }

                            if (why != null)
                            {
                                NoteAtlasDropped(key, PageLabel(page.Page), meta.CapturedAt, why);
                                continue;
                            }

                            order.Add(pageFile);
                            kept.Add(page);
                        }
                    }

                    meta.Atlas = kept.Count == 0 ? null : kept;
                }

                if (meshFile != null) order.Add(meshFile);

                // The stamp: the cached one when the meta and EVERY file it covers - the same names, in the
                // same order, at the same sizes and write times - are exactly as they were when it was
                // cached; otherwise streamed from the bytes, one file at a time, and cached again.
                var infos = order.Select(name => new System.IO.FileInfo(System.IO.Path.Combine(dir, name))).ToList();
                var metaInfo = new System.IO.FileInfo(metaPath);
                string stamp;

                if (cache != null && Sha256Hex.IsMatch(cache.Stamp ?? "") &&
                    string.Equals(cache.Meta?.Name, metaInfo.Name, StringComparison.OrdinalIgnoreCase) &&
                    Matches(cache.Meta, metaInfo) && cache.Meta!.Size == metaBytes.Length &&
                    cache.Files != null && cache.Files.Count == order.Count &&
                    order.Select((name, i) => cache.Files[i] != null &&
                                              string.Equals(cache.Files[i].Name, name, StringComparison.OrdinalIgnoreCase) &&
                                              Matches(cache.Files[i], infos[i])).All(ok => ok))
                {
                    stamp = cache.Stamp!.ToLowerInvariant();
                }
                else
                {
                    stamp = StreamStamp(metaBytes, infos, shas);
                    WriteStampCache(dir, key, stamp, order.Select(name => (name, shas[name])).ToList());
                }

                return new StoredSet
                {
                    Key = key,
                    Stamp = stamp,
                    Bytes = infos.Sum(info => info.Length),
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

                // Bounded, then thrown away by PrepareSet. Bounded anyway because it is logged on the way
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
        /// server never joins it to a path. <see cref="PrepareSet"/> overwrites it with
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
            else if (mesh.Bytes <= 0 || mesh.Bytes > _meshCeiling)
                why = $"claims {mesh.Bytes:N0} bytes, which is not a mesh up to this host's ceiling of {_meshCeiling:N0} " +
                      $"({Mb(_meshCeiling)} MB, from its free disk at boot)";
            else if (mesh.Cells < 0 || mesh.Cells > MaxMeshBands * MaxMeshCellsPerBand)
                why = $"claims {mesh.Cells:N0} relief cells";
            else if (mesh.Triangles < 0 || mesh.Triangles > MaxMeshTriangles)
                why = $"claims {mesh.Triangles:N0} triangles";

            if (why == null) return;

            meta.Mesh = null;

            WarnOnce(key, $"its capture meta describes a mesh this host will not take - it {why}. The " +
                          "pictures are stored without it, so that map draws flat");
        }

        // ---------------------------------------------------------------------------------------
        // Sides
        // ---------------------------------------------------------------------------------------

        /// <summary>"N", "S", "E" or "W" for anything a client could reasonably send for one, or null.
        /// One place, so the upload, the staging names, the image route and the meta all agree on the
        /// spelling of a direction.</summary>
        private static string? NormaliseSide(string? claimed)
        {
            var dir = (claimed ?? "").Trim().ToUpperInvariant();

            return SideDirs.Contains(dir) ? dir : null;
        }

        /// <summary>A side post's picture, decoded and checked, or the reason it is DROPPED. The same
        /// checks a floor gets - JPEG only, the size cap before and after decoding, the magic - with an
        /// empty picture meaning "the client could not encode this side after naming it", which is how an
        /// upload tells the host not to wait for it.</summary>
        private static string? DecodeSide(MapUploadRequest request, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();

            if (Format(request.Format) != "jpg") return "a side picture must be a JPEG";

            var encoded = request.ImageBase64 ?? "";

            if (encoded.Length == 0) return "the client could not encode it";

            if (encoded.Length > MaxEncodedChars)
                return $"it is larger than the {Mb(MaxImageBytes)} MB a picture may be";

            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                bytes = Array.Empty<byte>();
                return "it is not base64";
            }

            if (bytes.Length > MaxImageBytes)
                return $"it is {bytes.Length:N0} bytes, past the {MaxImageBytes:N0} a picture may be";

            if (!MagicMatches("jpg", bytes)) return "its bytes do not start as a JPEG does";

            // The header's own size - see PictureSizeProblem (review F49).
            var sizeProblem = PictureSizeProblem("jpg", bytes);

            if (sizeProblem != null)
            {
                bytes = Array.Empty<byte>();
                return sizeProblem;
            }

            return null;
        }

        /// <summary>
        /// Drops every side in a meta this host could not serve, each with one line - never refusing the
        /// capture over one, which is the whole rule for sides.
        ///
        /// A side's numbers are held to the capture box they claim to be a view of: the three basis
        /// vectors unit length, the origins equal to the lowest projections of the box's eight corners
        /// (the meta's extent by the side's own y range), and the pixel size equal to the projected span
        /// at the side's scale within the same two pixels a floor gets. That last one is the check that
        /// compares two numbers worked out separately - the upload rescales a side for the wire, and a
        /// side described at one scale and rendered at another would texture every wall a constant
        /// fraction off, with no other symptom. Caller holds nothing; this touches only the given meta.
        /// </summary>
        private void DropUnusableSides(string key, MapCaptureMetaDto meta)
        {
            if (meta.Sides == null) return;

            var seen = new HashSet<string>();
            var kept = new List<MapCaptureSideDto>();

            foreach (var side in meta.Sides)
            {
                if (side == null) continue;

                var dir = NormaliseSide(side.Dir);
                var why = dir == null ? $"'{Clip(side.Dir ?? "", 8)}' is not a side" : SideProblem(meta, side);

                if (why == null && !seen.Add(dir!)) why = $"there are two {dir} sides";

                if (why != null)
                {
                    NoteSideDropped(key, dir ?? SideLabel(side.Dir), meta.CapturedAt, why);
                    continue;
                }

                side.Dir = dir!;
                side.File = Clip((side.File ?? "").Trim(), MaxFreeTextLength);
                kept.Add(side);
            }

            meta.Sides = kept.Count == 0 ? null : kept;
        }

        /// <summary>Why a side does not describe a view of this meta's capture box, or null when it
        /// does. See <see cref="DropUnusableSides"/>.</summary>
        private static string? SideProblem(MapCaptureMetaDto meta, MapCaptureSideDto side)
        {
            var extent = meta.Extent;

            if (extent == null) return "the capture has no extent";

            if (side.Width < 1 || side.Height < 1 || side.Width > MaxFloorPixels || side.Height > MaxFloorPixels)
                return $"it is {side.Width}x{side.Height} px, which is not a picture up to {MaxFloorPixels} px a side";

            if (!Finite(side.PxPerMetre) || side.PxPerMetre <= 0f) return $"its pxPerMetre is {side.PxPerMetre}";

            if (!InWorld(side.YMin) || !InWorld(side.YMax) || side.YMin >= side.YMax)
                return $"its height range {side.YMin:0.#}..{side.YMax:0.#} m is not one";

            if (!Finite(side.OriginR) || !Finite(side.OriginU)) return "its origins are not numbers";

            if (!UnitVector(side.Forward) || !UnitVector(side.Right) || !UnitVector(side.Up))
                return "its forward, right and up are not three unit vectors";

            // The eight corners of the capture box, projected on the side's own axes.
            double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue;

            foreach (var x in new[] { extent.MinX, extent.MaxX })
            foreach (var y in new[] { (double)side.YMin, side.YMax })
            foreach (var z in new[] { extent.MinZ, extent.MaxZ })
            {
                var r = x * side.Right![0] + y * side.Right[1] + z * side.Right[2];
                var u = x * side.Up![0] + y * side.Up[1] + z * side.Up[2];

                minR = Math.Min(minR, r);
                maxR = Math.Max(maxR, r);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
            }

            if (Math.Abs(side.OriginR - minR) > SideOriginTolerance || Math.Abs(side.OriginU - minU) > SideOriginTolerance)
                return $"its origins ({side.OriginR:0.##}, {side.OriginU:0.##}) are not the capture box's " +
                       $"({minR:0.##}, {minU:0.##})";

            var wantW = (int)Math.Ceiling((maxR - minR) * side.PxPerMetre);
            var wantH = (int)Math.Ceiling((maxU - minU) * side.PxPerMetre);

            if (Math.Abs(side.Width - wantW) > SidePixelTolerance || Math.Abs(side.Height - wantH) > SidePixelTolerance)
                return $"it is {side.Width}x{side.Height} px, but the capture box at {side.PxPerMetre:0.###} px/m is " +
                       $"{wantW}x{wantH} px";

            return null;
        }

        /// <summary>Three finite numbers of length one, to <see cref="SideUnitTolerance"/>.</summary>
        private static bool UnitVector(float[]? v)
        {
            if (v == null || v.Length != 3 || !Finite(v[0]) || !Finite(v[1]) || !Finite(v[2])) return false;

            var length = Math.Sqrt((double)v[0] * v[0] + (double)v[1] * v[1] + (double)v[2] * v[2]);

            return Math.Abs(length - 1d) <= SideUnitTolerance;
        }

        /// <summary>Takes out of a meta every side this host has already dropped for this capture - the
        /// markers a dropped side post leaves in the staging (see Accept). Without it the next floor
        /// post, whose meta still names the side, would put it back on the list the set waits for.</summary>
        private static void StripDroppedSides(string staging, MapCaptureMetaDto meta)
        {
            if (meta.Sides == null) return;

            meta.Sides.RemoveAll(side => System.IO.File.Exists(System.IO.Path.Combine(staging, DroppedSideName(side.Dir))));

            if (meta.Sides.Count == 0) meta.Sides = null;
        }

        /// <summary>The staged side pictures of one capture, by direction.</summary>
        private static Dictionary<string, string> SidesByDir(string staging)
        {
            var found = new Dictionary<string, string>();

            if (!System.IO.Directory.Exists(staging)) return found;

            foreach (var dir in SideDirs)
            {
                var path = System.IO.Path.Combine(staging, StagedSideName(dir));

                if (System.IO.File.Exists(path)) found[dir] = path;
            }

            return found;
        }

        /// <summary>How many pieces a staged capture still lacks - floors, then sides, then (asked
        /// separately by the callers) the mesh - with the words for it. "1 more floor(s)" stays exactly
        /// what it always said when there are no sides, because a client of an older build reads it.</summary>
        private static int Missing(MapCaptureMetaDto meta, string staging, out string words)
        {
            var floors = FilesByLevel(staging);
            var sides = SidesByDir(staging);
            var pages = PagesByNumber(staging);

            var floorsMissing = meta.Floors.Count(f => !floors.ContainsKey(f.Level));
            var sidesMissing = (meta.Sides ?? new List<MapCaptureSideDto>()).Count(sd => !sides.ContainsKey(sd.Dir));

            // Pages only while the set keeps its mesh - a flat set is promoted without them (PrepareSet),
            // so waiting for one would hold it for nothing.
            var pagesMissing = meta.Mesh == null ? 0
                : (meta.Atlas ?? new List<MapCaptureAtlasDto>()).Count(p => !pages.ContainsKey(p.Page));

            // "more" on the first item only, which keeps the words every older answer used exactly as they
            // were: "2 more floor(s)", "2 more floor(s) and 1 side(s)", "1 more side(s)".
            var first = true;

            string Item(int count, string what)
            {
                if (count <= 0) return "";

                var said = first ? $"{count} more {what}" : $"{count} {what}";
                first = false;
                return said;
            }

            words = Listed(Item(floorsMissing, "floor(s)"), Item(sidesMissing, "side(s)"), Item(pagesMissing, "atlas page(s)"));

            if (words.Length == 0) words = "0 more floor(s)";

            return floorsMissing + sidesMissing + pagesMissing;
        }

        /// <summary>A side post's answer, with the drop in front when its side was dropped - so the
        /// client can say so without a field of its own for it.</summary>
        private static string SideNote(string? dir, string? refusal, string rest)
        {
            if (refusal == null) return rest;

            var note = $"the {dir ?? "unnamed"} side was dropped ({refusal})";

            return rest.Length == 0 ? note : $"{note} - {rest}";
        }

        /// <summary>
        /// The ONE line a dropped side gets: "the E side picture of 'bigmap' was dropped - why". Deduped on
        /// the map, the side and the capture rather than on the reason, because one side can be turned
        /// away on more than one ground in one upload - the meta check drops it, and then the post of it
        /// finds it no longer named - and a host reading its log wants to hear about a side once, not once
        /// per ground. Capped with the other refusals, for their reason.
        /// </summary>
        private void NoteSideDropped(string key, string dir, string? capturedAt, string why)
        {
            bool first;

            lock (_rejectionsLogged)
                first = _rejectionsLogged.Count < MaxRejectionsLogged &&
                        _rejectionsLogged.Add($"{key}|side|{dir}|{Clip(capturedAt ?? "", MaxFreeTextLength)}");

            if (first)
                _logger.Warning(
                    $"Quest Tracker: the {dir} side picture of '{key}' was dropped - {why} - and the set is served " +
                    "without that side.");
        }

        /// <summary>A direction a client sent that is not one of the four, fit to print: quoted and
        /// clipped, since it is a peer's text on its way into a log line.</summary>
        private static string SideLabel(string? claimed) => $"'{Clip(claimed ?? "", 8)}'";

        /// <summary>A side's name in the staging folder.</summary>
        private static string StagedSideName(string dir) => $"side-{dir}.jpg";

        /// <summary>The marker a dropped side leaves in the staging folder. Not a picture extension, so
        /// nothing reads it as one; it goes with the staging when the set is promoted.</summary>
        private static string DroppedSideName(string dir) => $"side-{dir}.dropped";

        /// <summary>A side's name in the map's folder - this server's own, never the client's.</summary>
        private static string SideName(string key, string dir) => $"{key}-side-{dir}.jpg";

        /// <summary>Words joined as a sentence lists them - "a", "a and b", "a, b and c" - skipping the
        /// empty ones. For the answers that name what a capture still lacks.</summary>
        private static string Listed(params string?[] items)
        {
            var said = items.Where(i => !string.IsNullOrEmpty(i)).Select(i => i!).ToList();

            return said.Count <= 1
                ? string.Concat(said)
                : string.Join(", ", said.Take(said.Count - 1)) + " and " + said[^1];
        }

        // ---------------------------------------------------------------------------------------
        // Atlas pages
        // ---------------------------------------------------------------------------------------

        /// <summary>An atlas page post's picture, decoded and checked, or the reason it is DROPPED. A
        /// side's checks with a page's size cap, plus one a side does not get: the JPEG's own frame must be
        /// the size the meta names, because the mesh's UVs address the page as that many texels, and
        /// package.ps1's gate holds a shipped page to exactly this. Empty means "the client could not
        /// encode this page after naming it".</summary>
        private static string? DecodeAtlasPage(MapUploadRequest request, MapCaptureAtlasDto entry, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();

            if (Format(request.Format) != "jpg") return "an atlas page must be a JPEG";

            var encoded = request.ImageBase64 ?? "";

            if (encoded.Length == 0) return "the client could not encode it";

            if (encoded.Length > MaxEncodedAtlasChars)
                return $"it is larger than the {Mb(MaxAtlasPageBytes)} MB a page may be";

            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                bytes = Array.Empty<byte>();
                return "it is not base64";
            }

            if (bytes.Length > MaxAtlasPageBytes)
                return $"it is {bytes.Length:N0} bytes, past the {MaxAtlasPageBytes:N0} a page may be";

            if (!MagicMatches("jpg", bytes)) return "its bytes do not start as a JPEG does";

            if (!JpegSize(bytes, out var width, out var height))
                return "its JPEG frame header could not be read";

            if (width != entry.Width || height != entry.Height)
                return $"it is {width}x{height} px, not the {entry.Width}x{entry.Height} its meta names";

            return null;
        }

        /// <summary>
        /// Drops every atlas page in a meta this host could not serve, each with one line - never refusing
        /// the capture over one, the sides' rule. ALL of them, silently, when the meta has no mesh: a page
        /// textures the mesh's buildings and nothing else, and the mesh's own drop has already been said.
        /// Otherwise a page must be one of <see cref="MaxAtlasPages"/>, named once, and a picture up to
        /// <see cref="MaxFloorPixels"/> a side. Its file name and sha are bounded here and rewritten at
        /// promotion (PrepareSet), so neither is trusted further.
        /// </summary>
        private void DropUnusableAtlas(string key, MapCaptureMetaDto meta)
        {
            if (meta.Atlas == null) return;

            if (meta.Mesh == null)
            {
                meta.Atlas = null;
                return;
            }

            var kept = new List<MapCaptureAtlasDto>();

            foreach (var page in meta.Atlas)
            {
                if (page == null) continue;

                string? why = null;

                if (page.Page < 0 || page.Page >= MaxAtlasPages)
                    why = $"page {page.Page} is not one of the {MaxAtlasPages} a set may carry";
                else if (kept.Any(k => k.Page == page.Page))
                    why = $"the meta names page {page.Page} twice";
                else if (page.Width < 1 || page.Height < 1 || page.Width > MaxFloorPixels || page.Height > MaxFloorPixels)
                    why = $"it is {page.Width}x{page.Height} px, which is not a picture up to {MaxFloorPixels} px a side";
                else if (page.Tiles < 0)
                    why = $"it claims {page.Tiles} tiles";

                if (why != null)
                {
                    NoteAtlasDropped(key, PageLabel(page.Page), meta.CapturedAt, why);
                    continue;
                }

                page.File = Clip((page.File ?? "").Trim(), MaxFreeTextLength);
                page.Sha256 = Clip((page.Sha256 ?? "").Trim(), 64);
                kept.Add(page);
            }

            meta.Atlas = kept.Count == 0 ? null : kept;
        }

        /// <summary>Takes out of a meta every atlas page this host has already dropped for this capture -
        /// <see cref="StripDroppedSides"/> for pages.</summary>
        private static void StripDroppedPages(string staging, MapCaptureMetaDto meta)
        {
            if (meta.Atlas == null) return;

            meta.Atlas.RemoveAll(p => p.Page >= 0 && p.Page < MaxAtlasPages &&
                                      System.IO.File.Exists(System.IO.Path.Combine(staging, DroppedPageName(p.Page))));

            if (meta.Atlas.Count == 0) meta.Atlas = null;
        }

        /// <summary>The staged atlas pages of one capture, by page number.</summary>
        private static Dictionary<int, string> PagesByNumber(string staging)
        {
            var found = new Dictionary<int, string>();

            if (!System.IO.Directory.Exists(staging)) return found;

            for (var page = 0; page < MaxAtlasPages; page++)
            {
                var path = System.IO.Path.Combine(staging, StagedPageName(page));

                if (System.IO.File.Exists(path)) found[page] = path;
            }

            return found;
        }

        /// <summary>A page post's answer, with the drop in front when its page was dropped -
        /// <see cref="SideNote"/>'s shape, "atlas page 3 was dropped (why)".</summary>
        private static string PageNote(string label, string? refusal, string rest)
        {
            if (refusal == null) return rest;

            var note = $"{label} was dropped ({refusal})";

            return rest.Length == 0 ? note : $"{note} - {rest}";
        }

        /// <summary>
        /// The ONE line a dropped atlas page gets: "atlas page 3 of 'bigmap' was dropped - why". Deduped on
        /// the map, the page and the capture, for <see cref="NoteSideDropped"/>'s reason, and capped with the
        /// other refusals.
        /// </summary>
        private void NoteAtlasDropped(string key, string label, string? capturedAt, string why)
        {
            bool first;

            lock (_rejectionsLogged)
                first = _rejectionsLogged.Count < MaxRejectionsLogged &&
                        _rejectionsLogged.Add($"{key}|atlas|{label}|{Clip(capturedAt ?? "", MaxFreeTextLength)}");

            if (first)
                _logger.Warning(
                    $"Quest Tracker: {label} of '{key}' was dropped - {why} - and the buildings textured from it " +
                    "are drawn without it.");
        }

        /// <summary>"atlas page 3", fit to print - a number is all a client can send, so nothing to clip.</summary>
        private static string PageLabel(int? page) => $"atlas page {page?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

        /// <summary>A page's name in the staging folder. Not a number, so FilesByLevel never reads it as a
        /// floor.</summary>
        private static string StagedPageName(int page) => $"atlas-{page.ToString(CultureInfo.InvariantCulture)}.jpg";

        /// <summary>The marker a dropped page leaves in the staging folder.</summary>
        private static string DroppedPageName(int page) => $"atlas-{page.ToString(CultureInfo.InvariantCulture)}.dropped";

        /// <summary>A page's name in the map's folder - this server's own, never the client's.</summary>
        private static string AtlasName(string key, int page) => $"{key}-atlas-{page.ToString(CultureInfo.InvariantCulture)}.jpg";

        /// <summary>The most pixels a stored picture may have on a side, READ FROM ITS HEADER - the client's own
        /// ceiling on what it will decode (DynamicMapsLibrary, review F49). Twice MaxFloorPixels, so it never
        /// bites a picture the meta could describe; it exists for the bytes, which nothing else measured.</summary>
        private const int MaxHeaderPixels = 8192;

        /// <summary>Why a picture's header is not one this host will store - unreadable, or past
        /// <see cref="MaxHeaderPixels"/> a side, with the numbers - or null. A JPEG by its frame header, a PNG
        /// by its IHDR. Decodes nothing.</summary>
        private static string? PictureSizeProblem(string format, byte[] bytes)
        {
            int width, height;

            var read = format == "png" ? PngSize(bytes, out width, out height) : JpegSize(bytes, out width, out height);

            if (!read) return $"has a {(format == "png" ? "PNG" : "JPEG")} header whose size could not be read";

            if (width > MaxHeaderPixels || height > MaxHeaderPixels)
                return $"is {width}x{height} px by its header, past the {MaxHeaderPixels} px a side a picture may be";

            return null;
        }

        /// <summary>A PNG's width and height from its IHDR chunk, which the format puts first: the 8-byte
        /// signature, a 4-byte length of 13, "IHDR", then width and height as big-endian uint32.</summary>
        private static bool PngSize(byte[] data, out int width, out int height)
        {
            width = height = 0;

            if (data == null || data.Length < 24) return false;

            if (data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R') return false;

            var w = ((uint)data[16] << 24) | ((uint)data[17] << 16) | ((uint)data[18] << 8) | data[19];
            var h = ((uint)data[20] << 24) | ((uint)data[21] << 16) | ((uint)data[22] << 8) | data[23];

            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;

            width = (int)w;
            height = (int)h;
            return true;
        }

        /// <summary>A JPEG's width and height from its frame header, or false - the client's
        /// MapTransfer.JpegSize, the same marker walk tools/check-maps-pack.py makes, decoding nothing.</summary>
        private static bool JpegSize(byte[] data, out int width, out int height)
        {
            width = height = 0;

            if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return false;

            var at = 2;

            while (at + 3 < data.Length)
            {
                if (data[at] != 0xFF) return false;

                var marker = data[at + 1];

                if (marker == 0xFF) { at++; continue; }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { at += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return false;

                var length = (data[at + 2] << 8) | data[at + 3];
                if (length < 2) return false;

                // SOF0-15 except DHT (C4), JPG (C8) and DAC (CC), which are not frame headers.
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    if (at + 9 > data.Length) return false;

                    height = (data[at + 5] << 8) | data[at + 6];
                    width = (data[at + 7] << 8) | data[at + 8];

                    return width > 0 && height > 0;
                }

                at += 2 + length;
            }

            return false;
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
        /// y range, int32 atlas page count (v2, 0..8), int32 band count; per band int32 level, float32
        /// cell metres, int32 width, int32 height, uint16[w*h] heights, uint8[w*h] distances; int32
        /// building count; per building int32 key, int32 level, int32 vertex count, 3 x uint16[v], int32
        /// index count, uint32[i] indices, then (v2) int32 uv count (0 or the vertex count), uint16[uv] U,
        /// uint16[uv] V, int32 range count (0..64) and per range int32 page, int32 first, int32 count, then
        /// (v3) uint16 tileX, tileY, tileW, tileH and float32 uMin, uMax, vMin, vMax.
        ///
        /// WHY IT IS WORTH DOING AT ALL, when the client checks the same things again before it draws:
        /// this host hands the file to every other client in the group. A file that no reader will take
        /// is one that makes every one of them log a failure and draw flat - and the one machine that
        /// could have said so was this one, where the bytes arrived from an unauthenticated route.
        ///
        /// HOSTILE INPUT IS THE CASE, not the exception. Every count is tested against its cap BEFORE
        /// the bytes behind it are read, the inflated read is stopped at the bound the meta's declared counts
        /// allow (<see cref="MeshInflateBound"/>) so a deflate bomb costs a bounded read rather than
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
        /// <param name="inflateBound">The most it may inflate to (<see cref="MeshInflateBound"/>).</param>
        /// <param name="problem">Why it was refused. Empty when it was not.</param>
        /// <param name="facts">What the walk found - the extent, the band levels and the totals - for
        /// <see cref="MeshFitsMeta"/> to hold against the capture's meta. Partly filled on a refusal.</param>
        private static bool MeshHeaderIsUsable(byte[] bytes, long inflateBound, out string problem, out MeshFacts facts)
        {
            problem = "";
            facts = new MeshFacts();

            try
            {
                using var raw = new System.IO.MemoryStream(bytes, writable: false);
                using var inflate = new System.IO.Compression.DeflateStream(
                    raw, System.IO.Compression.CompressionMode.Decompress);

                // The cap is enforced by the stream itself rather than by a count this method
                // remembers: every read below goes through it, so no later addition can forget.
                using var bounded = new BoundedStream(inflate, inflateBound);
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

                // v2: the atlas page count, which every building's ranges are held to below.
                var atlasPages = reader.ReadInt32();

                if (atlasPages < 0 || atlasPages > MaxAtlasPages)
                {
                    problem = $"the mesh claims {atlasPages:N0} atlas pages, past the {MaxAtlasPages} a map may have";
                    return false;
                }

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

                facts.MinX = minX;
                facts.MinZ = minZ;
                facts.MaxX = maxX;
                facts.MaxZ = maxZ;

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

                    facts.Levels.Add(level);
                    facts.Cells += cells;
                }

                var buildings = reader.ReadInt32();

                if (buildings < 0 || buildings > MaxMeshBuildings)
                {
                    problem = $"the mesh claims {buildings:N0} buildings, past the {MaxMeshBuildings:N0} a map may have";
                    return false;
                }

                long vertices = 0;
                long indices = 0;

                // v3: the one-range-per-vertex rule needs a building's indices after its ranges are read, so
                // they are kept - in ONE array for the whole walk, grown to the largest building and never
                // per building (the summary's allocation rule), with one owner byte a vertex beside it. At the
                // caps that is 18 M indices (72 MB) and 2 M bytes, allocated once.
                var indexStore = Array.Empty<uint>();
                var owner = Array.Empty<byte>();

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

                    // The PER-BUILDING cap as well as the running total below: MapMeshFile.Read refuses a
                    // building past MaxVerticesPerBuilding however few the others have, so a file with one
                    // 2.5 M-vertex building and nothing else passes the total and is still a file every
                    // client throws away.
                    if (vertexCount > MaxMeshVerticesPerBuilding)
                    {
                        problem = $"the mesh's building {i} claims {vertexCount:N0} vertices, past the " +
                                  $"{MaxMeshVerticesPerBuilding:N0} one building may have";
                        return false;
                    }

                    vertices += vertexCount;

                    // The RUNNING total, which is the cap the per-building one above leaves a hole in:
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

                    // One building's own bound FIRST (S4): the index buffer below grows to a building's index
                    // count, and the total alone would let one building ask for 120 M of them.
                    if (indexCount / 3 > MaxMeshTrianglesPerBuilding)
                    {
                        problem = $"the mesh's building {i} claims {indexCount / 3:N0} triangles, past the " +
                                  $"{MaxMeshTrianglesPerBuilding:N0} one building may have";
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
                    if (indexStore.Length < indexCount) indexStore = new uint[indexCount];

                    if (!IndicesAreInRange(bounded, indexCount, vertexCount, buffer, out var badIndex,
                            out var badValue, indexStore))
                    {
                        problem = $"the mesh's building {i} has index {badIndex:N0} pointing at vertex " +
                                  $"{badValue:N0} of {vertexCount:N0}";
                        return false;
                    }

                    // v2: the UVs - none, or exactly one U and one V per vertex (MapMeshFile.CheckAtlas).
                    // Any sixteen-bit value is a texture coordinate, so they are skipped, in full.
                    var uvCount = reader.ReadInt32();

                    if (uvCount != 0 && uvCount != vertexCount)
                    {
                        problem = $"the mesh's building {i} claims {uvCount:N0} UVs for {vertexCount:N0} vertices";
                        return false;
                    }

                    Skip(bounded, (long)uvCount * 4, buffer);   // uint16 U[uv], uint16 V[uv]

                    // v2: the atlas ranges - the reader's own rules, so a file every client would throw away
                    // is refused here: at most 64, only with UVs, each on a page the file has, each a positive
                    // whole number of triangles inside this building's indices, ascending, not overlapping.
                    var rangeCount = reader.ReadInt32();

                    if (rangeCount < 0 || rangeCount > MaxMeshRangesPerBuilding)
                    {
                        problem = $"the mesh's building {i} claims {rangeCount:N0} atlas ranges, past the " +
                                  $"{MaxMeshRangesPerBuilding} one building may have";
                        return false;
                    }

                    if (rangeCount > 0 && uvCount == 0)
                    {
                        problem = $"the mesh's building {i} has atlas ranges and no UVs";
                        return false;
                    }

                    var end = 0L;

                    for (var k = 0; k < rangeCount; k++)
                    {
                        var page = reader.ReadInt32();
                        var first = reader.ReadInt32();
                        var count = reader.ReadInt32();

                        if (page < 0 || page >= atlasPages)
                        {
                            problem = $"the mesh's building {i} range {k} is on atlas page {page}, and the file has {atlasPages}";
                            return false;
                        }

                        if (first < end || first % 3 != 0 || count <= 0 || count % 3 != 0 || (long)first + count > indexCount)
                        {
                            problem = $"the mesh's building {i} range {k} is indices {first}+{count} of {indexCount} " +
                                      $"(after {end}) - ranges are ascending whole triangles inside the building";
                            return false;
                        }

                        // v3: the tile's pixel rect on its page - sides a multiple of 4 from 4 to 256, inside
                        // the 4096 px page - and the raw-UV bounds the range's vertices are quantised over.
                        int tileX = reader.ReadUInt16(), tileY = reader.ReadUInt16();
                        int tileW = reader.ReadUInt16(), tileH = reader.ReadUInt16();

                        if (tileW < MinTileSide || tileW > MaxTileSide || tileW % 4 != 0 ||
                            tileH < MinTileSide || tileH > MaxTileSide || tileH % 4 != 0)
                        {
                            problem = $"the mesh's building {i} range {k} has a {tileW}x{tileH} px tile - a tile's sides " +
                                      $"are multiples of 4 from {MinTileSide} to {MaxTileSide}";
                            return false;
                        }

                        if (tileX + tileW > AtlasPageSide || tileY + tileH > AtlasPageSide)
                        {
                            problem = $"the mesh's building {i} range {k} has its tile at {tileX},{tileY} {tileW}x{tileH}, " +
                                      $"past the {AtlasPageSide} px page";
                            return false;
                        }

                        var uMin = reader.ReadSingle();
                        var uMax = reader.ReadSingle();
                        var vMin = reader.ReadSingle();
                        var vMax = reader.ReadSingle();

                        if (!Finite(uMin) || !Finite(uMax) || !Finite(vMin) || !Finite(vMax) || uMax < uMin || vMax < vMin)
                        {
                            problem = $"the mesh's building {i} range {k} has UV bounds u {uMin}..{uMax}, v {vMin}..{vMax} - " +
                                      "they must be finite, each max at least its min";
                            return false;
                        }

                        end = (long)first + count;

                        // v3: every vertex belongs to at most one range - its U and V are quantised over THAT
                        // range's bounds, so a vertex two ranges index would be read against the wrong one by
                        // either. One pass over this range's indices with a byte a vertex.
                        if (k == 0)
                        {
                            if (owner.Length < vertexCount) owner = new byte[vertexCount];
                            Array.Clear(owner, 0, vertexCount);
                        }

                        for (var at = first; at < first + count; at++)
                        {
                            var vertex = (int)indexStore[at];
                            var mine = (byte)(k + 1);

                            if (owner[vertex] == 0) owner[vertex] = mine;
                            else if (owner[vertex] != mine)
                            {
                                problem = $"the mesh's building {i} vertex {vertex} is used by ranges {owner[vertex] - 1} " +
                                          $"and {k} - a vertex belongs to one range, whose UV bounds it is quantised over";
                                return false;
                            }
                        }
                    }
                }

                // Nothing may follow the last building: a file with more in it is a file this build
                // does not understand, whatever the version said.
                if (bounded.ReadByte() >= 0)
                {
                    problem = "the mesh carries more data after its last building";
                    return false;
                }

                facts.Triangles = indices / 3;

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

        /// <summary>What the header walk found, for <see cref="MeshFitsMeta"/> to hold against the
        /// capture's own meta: the extent, the band levels, and the two totals the meta states.</summary>
        private sealed class MeshFacts
        {
            public double MinX;
            public double MinZ;
            public double MaxX;
            public double MaxZ;
            public readonly List<int> Levels = new();
            public long Cells;
            public long Triangles;
        }

        /// <summary>
        /// Whether a mesh that is a VALID FILE is also THIS CAPTURE'S mesh, and if not, why.
        ///
        /// The header walk proves the file is one a client could read; this proves it belongs beside
        /// these pictures - the same four things tools/check-maps-pack.py refuses a shipped set on, so a
        /// host can no longer store a set that its own maintainer's -RefreshMaps then refuses to package:
        /// the extent to the metre's millionth (a mesh over another rectangle drapes every picture in the
        /// wrong place), the band levels exactly the floors' (a band with no picture has no texture, a
        /// floor with no band no ground), and the cell and triangle counts the meta states (two numbers
        /// the writer derived from the same file, so a difference is a different file).
        ///
        /// Reachable by an honest client, which is why it matters: a floor that fails to encode before
        /// the last post is taken out of the meta, while the mesh built in the raid still has its band.
        /// That mesh is refused here and the set is served flat - see <see cref="AcceptMesh"/>.</summary>
        private static bool MeshFitsMeta(MeshFacts facts, MapCaptureMetaDto meta, out string problem)
        {
            problem = "";

            var extent = meta.Extent;

            if (extent == null ||
                Math.Abs(facts.MinX - extent.MinX) > MeshExtentTolerance ||
                Math.Abs(facts.MinZ - extent.MinZ) > MeshExtentTolerance ||
                Math.Abs(facts.MaxX - extent.MaxX) > MeshExtentTolerance ||
                Math.Abs(facts.MaxZ - extent.MaxZ) > MeshExtentTolerance)
            {
                problem = "the mesh covers a different rectangle from the capture's pictures";
                return false;
            }

            var bands = facts.Levels.OrderBy(l => l).ToList();
            var floors = meta.Floors.Select(f => f.Level).OrderBy(l => l).ToList();

            if (!bands.SequenceEqual(floors))
            {
                problem = $"the mesh's bands are levels [{string.Join(", ", bands)}] but the capture's floors are " +
                          $"[{string.Join(", ", floors)}]";
                return false;
            }

            var mesh = meta.Mesh;

            if (mesh != null && (mesh.Cells != facts.Cells || mesh.Triangles != facts.Triangles))
            {
                problem = $"the mesh holds {facts.Cells:N0} cells and {facts.Triangles:N0} triangles but the capture's " +
                          $"meta says {mesh.Cells:N0} and {mesh.Triangles:N0}";
                return false;
            }

            return true;
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
        /// <param name="store">Where the indices are kept, at least indexCount long (v3's vertex rule reads them
        /// again once the building's ranges are known).</param>
        private static bool IndicesAreInRange(
            System.IO.Stream stream, int indexCount, int vertexCount, byte[] buffer, out int bad, out uint value,
            uint[] store)
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

                    store[done + i] = index;

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
        /// otherwise re-hash the mesh four times over, on the request thread, to answer a question the
        /// staging folder already knows the answer to. The sidecar is written after the mesh and deleted
        /// before it, so its absence means "hash it" rather than "no mesh"; and it is only ever a HINT -
        /// <see cref="PrepareSet"/> re-hashes the file itself before serving it, which is the check that
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
        /// <param name="older">True when the refusal is that the host already SERVES this capture or a
        /// newer one - not a fault, and answered as such (see <see cref="AlreadyServed"/>).</param>
        private MapCaptureMetaDto? WantedMesh(
            string key, string capturedAt, DateTime captured, string sha256, out string problem, out bool older)
        {
            problem = "";
            older = false;

            if (_sets.TryGetValue(key, out var held) && ParseStamp(held.Meta.CapturedAt) >= captured)
            {
                problem = "older than the set on the host";
                older = true;
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
                DropUnusableSides(key, meta);
                DropUnusableAtlas(key, meta);
                StripDroppedSides(staging, meta);
                StripDroppedPages(staging, meta);

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
        /// <param name="key">The map whose staging goes.</param>
        /// <param name="committing">The staging folder of the set being committed right now - claimed in
        /// <see cref="_completing"/> by that very commit, and the one folder this must NOT skip for being
        /// claimed. Skipping it too was a bug the harness caught at once: every completed set left its own
        /// staging behind.</param>
        /// <param name="committedAt">The capture instant of the set just committed. A staging folder whose
        /// staged meta is NEWER than it is kept (review F36): on a shared host two players can capture one map
        /// seconds apart, and the older capture completing first used to delete the newer one's half-finished
        /// upload - its later posts then restaged a single floor and its capture never reached the host. The
        /// newer staging completes on its own posts or expires with the day-old sweep. MinValue drops them
        /// all, as before.</param>
        private void DropStaging(string key, string? committing = null, DateTime committedAt = default)
        {
            if (!System.IO.Directory.Exists(IncomingFolder)) return;

            var prefix = key + "-";

            foreach (var dir in System.IO.Directory.EnumerateDirectories(IncomingFolder, prefix + "*"))
            {
                var name = System.IO.Path.GetFileName(dir);

                if (name.Length != prefix.Length + StagingHashLength) continue;

                // Never a folder ANOTHER post is completing or joining parts in right now: it is being read
                // outside the lock. It goes with the next sweep once that post is done with it. The folder of
                // the set being committed is claimed too - by this commit - and it is the one that must go.
                if (_completing.Contains(dir) &&
                    !string.Equals(dir, committing, StringComparison.OrdinalIgnoreCase)) continue;

                // A newer capture's upload in progress is not this set's leftovers - see committedAt. A folder
                // whose staged meta cannot be read is treated as leftovers, as every folder was before.
                if (committedAt != default && !string.Equals(dir, committing, StringComparison.OrdinalIgnoreCase) &&
                    StagedCapturedAt(dir) > committedAt) continue;

                try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* it will be reused or replaced */ }
            }
        }

        /// <summary>The capture instant a staging folder's meta names, or MinValue when it has none that can be
        /// read. A light read - the staged meta is a few kilobytes - for <see cref="DropStaging"/>.</summary>
        private static DateTime StagedCapturedAt(string staging)
        {
            try
            {
                var path = System.IO.Path.Combine(staging, StagedMetaName);

                if (!System.IO.File.Exists(path)) return DateTime.MinValue;

                var meta = JsonSerializer.Deserialize<MapCaptureMetaDto>(System.IO.File.ReadAllBytes(path), FileOptions);

                return ParseStamp(meta?.CapturedAt);
            }
            catch
            {
                return DateTime.MinValue;
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
                    // Not a folder being completed or joined right now - see DropStaging.
                    if (_completing.Contains(dir)) continue;

                    // Leftovers of a join that died between its rename and its delete: a .join-* file is only
                    // ever alive for the length of one post, so one an hour old is nobody's, and it would
                    // otherwise count against the store's total until the folder itself expired.
                    foreach (var stray in System.IO.Directory.EnumerateFiles(dir, "*.join-*"))
                        if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(stray) > TimeSpan.FromHours(1))
                            try { System.IO.File.Delete(stray); } catch { /* next sweep */ }

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

        /// <summary>
        /// The stale-staging sweep, run by upload posts at most every <see cref="StaleSweepEvery"/> and by
        /// every completed set - not only at boot, as it used to be. A host that is never restarted would
        /// otherwise keep every abandoned upload - floors, sides, mesh parts, a join cut short - counted
        /// against its total for good, and a busy host would one day refuse every upload for space
        /// taken by uploads nobody finished. Caller holds the lock.
        /// </summary>
        private void SweepStaleStagingIfDue(bool now = false)
        {
            if (!now && DateTime.UtcNow - _lastStaleSweep < StaleSweepEvery) return;

            _lastStaleSweep = DateTime.UtcNow;

            DropStaleStaging();
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

        /// <summary>StampOf over files on disk rather than arrays in memory - the same bytes in the same order,
        /// so the same value - read one megabyte at a time, and each file's own sha256 into
        /// <paramref name="shas"/> on the same pass, for the cache.</summary>
        private static string StreamStamp(byte[] metaBytes, List<System.IO.FileInfo> files, Dictionary<string, string> shas)
        {
            using var stamp = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];

            stamp.AppendData(metaBytes);

            foreach (var info in files)
            {
                using var own = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var stream = new System.IO.FileStream(info.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                    System.IO.FileShare.Read);

                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stamp.AppendData(buffer, 0, read);
                    own.AppendData(buffer, 0, read);
                }

                shas[info.Name] = Convert.ToHexString(own.GetHashAndReset()).ToLowerInvariant();
            }

            return Convert.ToHexString(stamp.GetHashAndReset()).ToLowerInvariant();
        }

        /// <summary>A byte array's sha256, lower-case hex.</summary>
        private static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        /// <summary>A file's sha256, lower-case hex, streamed.</summary>
        private static string HashFile(string path)
        {
            using var stream = System.IO.File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        /// <summary>The stamp cache beside a stored set - see the class comment.</summary>
        private sealed class StampCache
        {
            [System.Text.Json.Serialization.JsonPropertyName("stamp")] public string? Stamp { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("meta")] public CachedFile? Meta { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("files")] public List<CachedFile>? Files { get; set; }
        }

        /// <summary>One file as the cache last saw it: its name, size, write time and sha256.</summary>
        private sealed class CachedFile
        {
            [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("size")] public long Size { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("ticks")] public long Ticks { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("sha256")] public string? Sha { get; set; }
        }

        /// <summary>The cache's file name in a map's folder. Not a picture, a meta or a mesh, so no reader
        /// takes it for one; package.ps1 -RefreshMaps does not copy it, and CommitSet's sweep replaces it.</summary>
        private static string StampCacheName(string key) => key + ".stamp-cache.json";

        /// <summary>Whether a cached entry still describes this file: the same size and the same write time,
        /// to the tick.</summary>
        private static bool Matches(CachedFile? entry, System.IO.FileInfo info) =>
            entry != null && info.Exists && entry.Size == info.Length && entry.Ticks == info.LastWriteTimeUtc.Ticks;

        /// <summary>The cache beside a set, or null when there is none or it cannot be read.</summary>
        private static StampCache? ReadStampCache(string dir, string key)
        {
            try
            {
                var path = System.IO.Path.Combine(dir, StampCacheName(key));

                return System.IO.File.Exists(path)
                    ? JsonSerializer.Deserialize<StampCache>(System.IO.File.ReadAllBytes(path))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Writes the cache beside a set: the stamp, the meta as it is on disk now, and each file
        /// the stamp covers, in the stamp's order, with the sha256 it was hashed to. Best effort - a cache
        /// that fails to write costs one hash at the next boot, nothing else.</summary>
        private static void WriteStampCache(string dir, string key, string stamp, List<(string Name, string Sha)> files)
        {
            try
            {
                var meta = new System.IO.FileInfo(System.IO.Path.Combine(dir, MetaName(key)));

                if (!meta.Exists) return;

                var cache = new StampCache
                {
                    Stamp = stamp,
                    Meta = new CachedFile { Name = meta.Name, Size = meta.Length, Ticks = meta.LastWriteTimeUtc.Ticks },
                    Files = files.Select(f =>
                    {
                        var info = new System.IO.FileInfo(System.IO.Path.Combine(dir, f.Name));

                        return new CachedFile { Name = f.Name, Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Sha = f.Sha };
                    }).ToList()
                };

                WriteAtomic(System.IO.Path.Combine(dir, StampCacheName(key)), JsonSerializer.SerializeToUtf8Bytes(cache));
            }
            catch
            {
                // A cache, not a record.
            }
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
        private MapUploadResponse Reject(string map, string reason, string code = "")
        {
            WarnOnce(map, reason);

            return new MapUploadResponse { Outcome = "rejected", Reason = reason, Code = code };
        }

        /// <summary>Refuses one mesh post, logged by the same deduped path a refused picture takes: the
        /// mesh route is as unauthenticated as the picture route, so a client in a loop must not be able
        /// to rotate the real diagnostics out of a rolling log.</summary>
        private MapMeshUploadResponse RejectMesh(string map, string reason)
        {
            WarnOnce(map, reason);

            return new MapMeshUploadResponse { Accepted = false, Reason = reason };
        }

        /// <summary>
        /// A mesh post for a capture this host already serves, or one older than what it serves. NOT a
        /// fault: a retry after a reply that was lost, or a second machine offering the same set - so it
        /// is noted once at Information rather than warned about, and the client is told the set is
        /// SERVED, which is the fact it needs (nothing of this capture is waiting on it).
        /// </summary>
        private MapMeshUploadResponse AlreadyServed(string map, string reason)
        {
            bool first;

            lock (_rejectionsLogged)
                first = _rejectionsLogged.Count < MaxRejectionsLogged && _rejectionsLogged.Add($"{map}|served|{reason}");

            if (first)
                _logger.Info($"Quest Tracker: a map mesh for '{map}' was offered again - {reason}; nothing to do.");

            return new MapMeshUploadResponse { Accepted = false, Served = true, Reason = reason };
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
