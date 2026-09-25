using System.Collections.Generic;
using Newtonsoft.Json;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// The wire types for moving captured map pictures between a client and its host - mirrors of
    /// the server half's declarations in QuestTreeDtos.cs, hand-copied for the reason every other
    /// DTO here is: the two halves target different frameworks and cannot share a source file.
    ///
    /// Three conversations, all of them the client's to start (see <see cref="MapTransfer"/>):
    /// <list type="bullet">
    /// <item>POST /questtree/maps/upload - one floor of one capture at a time
    /// (<see cref="MapUploadRequest"/> / <see cref="MapUploadResponse"/>).</item>
    /// <item>GET /questtree/maps - what the host holds and whether it takes uploads at all
    /// (<see cref="MapIndexDto"/>).</item>
    /// <item>POST /questtree/maps/image - one floor's picture back down
    /// (<see cref="MapImageRequest"/> / <see cref="MapImageDto"/>).</item>
    /// <item>POST /questtree/maps/mesh - the capture's 3D mesh up, once, after its floors
    /// (<see cref="MapMeshUploadRequest"/> / <see cref="MapMeshUploadResponse"/>).</item>
    /// <item>POST /questtree/maps/meshfile - a map's mesh back down
    /// (<see cref="MapMeshRequest"/> / <see cref="MapMeshDto"/>).</item>
    /// </list>
    ///
    /// The mesh conversations are ADDITIVE and unversioned on purpose: an older host answers both with
    /// SPT's own HTML, which reads here as "this host has no mesh" and costs nothing
    /// (<see cref="MapTransfer.NotOurs{T}"/>), and an older client never asks. A schema bump would have
    /// refused those hosts outright for a feature that is optional by design.
    ///
    /// tools/check-dtos.py compares every type here against the server's copy by wire name.
    /// </summary>
    internal sealed class MapRectDto
    {
        [JsonProperty("minX")] public double MinX { get; set; }
        [JsonProperty("minZ")] public double MinZ { get; set; }
        [JsonProperty("maxX")] public double MaxX { get; set; }
        [JsonProperty("maxZ")] public double MaxZ { get; set; }
    }

    /// <summary>One floor of a capture, as its meta file records it: which storey, what the picture
    /// beside the meta is called, how big that picture is, and the height band it was drawn for.
    ///
    /// <see cref="File"/>, <see cref="Width"/> and <see cref="Height"/> are the three fields an
    /// upload REWRITES: the capture on disk is a PNG at full capture resolution, and what is
    /// uploaded is a JPEG scaled to fit the transport's long side - see
    /// <see cref="MapTransfer.UploadCapture"/>. A meta describing the PNG beside a JPEG picture is
    /// exactly the disagreement MapCatalog.CheckPictureSize warns about.</summary>
    internal sealed class MapCaptureFloorDto
    {
        /// <summary>0 is the ground floor, positive up, negative down - the numbering the harvested
        /// bands carry, and part of the picture's file name.</summary>
        [JsonProperty("level")] public int Level { get; set; }

        [JsonProperty("name")] public string Name { get; set; }

        /// <summary>The picture's file name, beside the meta. A bare name, never a path: the reader
        /// refuses anything else (MapCatalog.ReadFloors).</summary>
        [JsonProperty("file")] public string File { get; set; }

        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }

        [JsonProperty("minY")] public float MinY { get; set; }
        [JsonProperty("maxY")] public float MaxY { get; set; }
    }

    /// <summary>A place name drawn on a captured map: an exfil or a cleaned bot-zone name, at a
    /// ground position with no height.</summary>
    internal sealed class MapLabelDto
    {
        [JsonProperty("text")] public string Text { get; set; }

        /// <summary>"exfil" or "zone" - where the name came from, which is the whole of how
        /// prominently it is drawn: an extract wears the accent, gets a diamond
        /// (MapView.BuildExtractMarkers) and is drawn at every zoom, a zone name is white and yields.
        /// On the wire because without it every borrowed map lost its extract marks, drew its
        /// extracts in plain white, culled them with the zone names, and - for a player whose Map
        /// labels setting is "extracts only" - drew no place names at all. The host normalises this to
        /// one of the two words (MapStore's labels loop); the reader treats anything else as a zone,
        /// which is the quieter of the two.</summary>
        [JsonProperty("kind")] public string Kind { get; set; }

        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("z")] public double Z { get; set; }
    }

    /// <summary>The mesh beside a set's pictures, as its meta describes it: the map's ground relief and
    /// building shells in MapMeshFile's format, in <c>&lt;key&gt;-mesh.bin</c>.
    ///
    /// Described here and carried on its own route, because it is megabytes of deflated binary and
    /// every reader of this meta parses the whole of it. OPTIONAL everywhere: a capture taken before
    /// 1.19.0, a DynamicMaps set and a set from an older host all have none, and the Maps tab draws the
    /// flat picture when it is absent - which is why this needed no schema bump.
    ///
    /// <see cref="Sha256"/> is the field everything turns on. An upload will not send a local mesh
    /// whose bytes do not hash to it (the meta would be describing a file this machine no longer has),
    /// the host matches an arriving mesh to a staged capture by it, and a download checks what it
    /// received against it before writing a file the 3D view will then read as geometry.</summary>
    internal sealed class MapCaptureMeshDto
    {
        /// <summary>The file's name beside the pictures. A bare name, never a path.</summary>
        [JsonProperty("file")] public string File { get; set; }

        [JsonProperty("bytes")] public long Bytes { get; set; }

        /// <summary>MapMeshFile.Version, so a client can tell "a mesh I cannot read" from "no mesh"
        /// without downloading it.</summary>
        [JsonProperty("version")] public int Version { get; set; }

        [JsonProperty("cells")] public long Cells { get; set; }

        [JsonProperty("triangles")] public long Triangles { get; set; }

        /// <summary>sha256 of the file's bytes, hex, lower case.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    /// <summary>One oblique side picture of a capture: an orthographic render from the N, S, E or W side
    /// of the map, pitched 45 degrees down, which the 3D view uses to texture building walls. A world
    /// point p lands at px = (dot(right, p) - originR) x pxPerMetre, py = height - (dot(up, p) - originU)
    /// x pxPerMetre, row 0 at the top. Optional: absent on older sets, and a side a host cannot use is
    /// dropped by it while the rest of the set is served.
    ///
    /// <see cref="Width"/>, <see cref="Height"/> and <see cref="PxPerMetre"/> are rewritten by an upload
    /// for the size the JPEG goes up at, exactly as a floor's are (MapTransfer.DescribeWire); the basis
    /// and the origins are sizes in the world and do not change.</summary>
    internal sealed class MapCaptureSideDto
    {
        [JsonProperty("dir")] public string Dir { get; set; }

        /// <summary>The picture's file name beside the meta. A bare name, never a path.
        ///
        /// On an UPLOAD it may still be the capture's own <c>&lt;key&gt;-side-&lt;dir&gt;.png</c>: unlike a floor's,
        /// the upload does not rewrite it, because the host throws a client's name away and writes
        /// <c>&lt;key&gt;-side-&lt;dir&gt;.jpg</c> itself (MapStore.PrepareSet) - which is the name every
        /// downloaded set and every shipped one carries.</summary>
        [JsonProperty("file")] public string File { get; set; }

        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }

        [JsonProperty("pxPerMetre")] public float PxPerMetre { get; set; }

        [JsonProperty("forward")] public float[] Forward { get; set; }
        [JsonProperty("right")] public float[] Right { get; set; }
        [JsonProperty("up")] public float[] Up { get; set; }

        [JsonProperty("originR")] public double OriginR { get; set; }
        [JsonProperty("originU")] public double OriginU { get; set; }

        [JsonProperty("yMin")] public float YMin { get; set; }
        [JsonProperty("yMax")] public float YMax { get; set; }
    }

    /// <summary>One atlas page of a capture: a 4096 px sheet of the game's own building textures, which the 3D
    /// view drapes on the buildings by the mesh file's UVs. Optional: absent on older sets, and a page a host
    /// cannot use is dropped by it while the rest of the set is served (those buildings fall back).
    ///
    /// <see cref="Sha256"/> is the capture's own PNG's on this machine and the served JPEG's once a host has
    /// stored the set - the host rewrites it, and a download is held to the host's.</summary>
    internal sealed class MapCaptureAtlasDto
    {
        /// <summary>The page file's name beside the meta. A bare name, never a path. On an upload it may
        /// still be the capture's own <c>.png</c>; the host names the file it stores itself.</summary>
        [JsonProperty("file")] public string File { get; set; }

        [JsonProperty("page")] public int Page { get; set; }
        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }
        [JsonProperty("tiles")] public int Tiles { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
    }

    /// <summary>
    /// A capture's meta, field for field the same shape as the <c>&lt;key&gt;.map.json</c> that
    /// MapCapture.WriteMeta writes and MapCatalog.ReadMeta reads, but for the two fields named below.
    ///
    /// Deliberately the same shape and not a second format: an upload reads the file the capture
    /// just wrote and sends it as it stands, and a download writes what the host sends straight back
    /// out as that same file, so the reader needs no knowledge of where a picture set came from. The
    /// one thing that differs by design is which pictures the floors name - see
    /// <see cref="MapCaptureFloorDto"/>.
    ///
    /// A round trip through a host is a deserialise into this type and a serialise back out of it, so
    /// a field neither mirror declares is silently dropped by both - which is why every field of the
    /// file is here except <c>render</c> and the floors' <c>exposure</c>. Those two are left out ON
    /// PURPOSE: each exists only to decide whether a LATER capture may be merged pixel by pixel into
    /// the one on disk (MapCapture.LoadPrevious), and a merge only ever happens against this machine's
    /// own captures folder, never against a borrowed set. Everything a READER uses - down to a label's
    /// kind and the count in the credit line - travels, and the day one of them stops travelling is
    /// the day a borrowed map quietly draws differently from the machine that captured it.
    ///
    /// MapCapture keeps its own private writer types rather than serialising this one: the meta is a
    /// file this mod owns end to end, this is a mirror of a type on the wire, and the day they have
    /// to differ (a field the host must not be told, say) is the day sharing one class would be the
    /// bug. tools/check-capture.py holds them to the same names.
    /// </summary>
    internal sealed class MapCaptureMetaDto
    {
        /// <summary>The capture meta shape. MapCatalog.SupportedCaptureSchema is the client's own
        /// ceiling and the one number that decides whether a set is read at all.</summary>
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }

        [JsonProperty("map")] public string Map { get; set; }

        /// <summary>The world rectangle the pictures cover, exactly. Everything the server places
        /// inside it lands on them.</summary>
        [JsonProperty("extent")] public MapRectDto Extent { get; set; }

        /// <summary>Degrees the pictures are turned from world XZ. 0 from every capture this mod
        /// takes; present so a hand-corrected set needs no schema bump.</summary>
        [JsonProperty("rotation")] public float Rotation { get; set; }

        [JsonProperty("pxPerMetre")] public float PxPerMetre { get; set; }

        /// <summary>The render tile the capture was assembled from. Diagnostic only.</summary>
        [JsonProperty("tileSize")] public int TileSize { get; set; }

        /// <summary>UTC, ISO-8601. What decides between two sets of one map: a local capture newer
        /// than the host's copy is never overwritten by it.</summary>
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }

        /// <summary>When the FIRST capture of this set was taken - what the credit line under the map
        /// means by the date, since a set is built over several raids and <see cref="CapturedAt"/> is
        /// only the latest of them. Never ranked on, by either half: the version of a set is
        /// <see cref="CapturedAt"/> alone. Empty, and then MapCatalog.Attribution reads
        /// <see cref="CapturedAt"/> instead, which is the right answer for a capture written before
        /// this field existed.</summary>
        [JsonProperty("firstCapturedAt")] public string FirstCapturedAt { get; set; }

        /// <summary>How many captures are merged into this set; 1 for a fresh one. In the credit line
        /// ("3 captures since 2026-09-19"), and on the wire because a borrowed set is exactly the case
        /// where somebody else did that work - without it every downloaded map claimed to be a single
        /// capture taken on the day of the last raid that improved it. The host clamps it.</summary>
        [JsonProperty("captures")] public int Captures { get; set; }

        [JsonProperty("modVersion")] public string ModVersion { get; set; }

        /// <summary>The raid's own clock, "HH:mm", or "". A set captured at 03:00 is a dark set.</summary>
        [JsonProperty("timeOfDay")] public string TimeOfDay { get; set; }

        [JsonProperty("floors")] public List<MapCaptureFloorDto> Floors { get; set; } = new List<MapCaptureFloorDto>();

        [JsonProperty("labels")] public List<MapLabelDto> Labels { get; set; } = new List<MapLabelDto>();

        /// <summary>The 3D mesh this set carries, or null when it has none - the ordinary case for every
        /// capture taken before 1.19.0 and for every borrowed set from an older host. An upload STRIPS
        /// this block when the file it names is not on this disk or does not hash to what it claims: a
        /// host told to expect a mesh waits for one before it serves the set, so a block that cannot be
        /// honoured would cost the whole capture rather than the mesh.</summary>
        [JsonProperty("mesh")] public MapCaptureMeshDto Mesh { get; set; }

        /// <summary>The oblique side pictures, or null for none (every set captured before sides
        /// existed). An upload names only the sides whose picture is on this disk; a side that then
        /// fails to encode is posted EMPTY, which tells the host to drop it rather than wait.</summary>
        [JsonProperty("sides")] public List<MapCaptureSideDto> Sides { get; set; }

        /// <summary>The atlas pages, or null for none. An upload names only the pages whose picture is on this
        /// disk; a page that then fails to encode is posted EMPTY, which tells the host to drop it.</summary>
        [JsonProperty("atlas")] public List<MapCaptureAtlasDto> Atlas { get; set; }
    }

    /// <summary>
    /// One floor of one capture, offered to the host. The body of POST /questtree/maps/upload.
    ///
    /// One floor per request on purpose: a whole capture is 2-6 base64 pictures and would be a
    /// request big enough to be worth a host refusing outright, while a floor at a time lets the
    /// host stop the client after the first one (see <see cref="MapUploadResponse.Outcome"/>) and
    /// costs one small frame of encoding each.
    ///
    /// <see cref="Meta"/> travels with EVERY floor rather than once: the posts are separate requests
    /// and the host may be answering several clients, so nothing may depend on it having kept state
    /// between two of them.
    /// </summary>
    internal sealed class MapUploadRequest
    {
        /// <summary>The upload shape. The server refuses anything it does not know, which is what
        /// keeps a newer client from half-filling an older host's store.</summary>
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("map")] public string Map { get; set; }

        [JsonProperty("clientVersion")] public string ClientVersion { get; set; }

        /// <summary>The whole capture's meta, with this floor's picture named and measured as the
        /// upload sends it - see <see cref="MapCaptureFloorDto"/>.</summary>
        [JsonProperty("meta")] public MapCaptureMetaDto Meta { get; set; }

        /// <summary>Which floor of <see cref="Meta"/> this request carries the picture for. For a side,
        /// int.MinValue - a level no floor has, so a host too old to know <see cref="Side"/> refuses the
        /// post instead of filing the side picture as a floor.</summary>
        [JsonProperty("level")] public int Level { get; set; }

        /// <summary>"N"/"S"/"E"/"W" for a side picture; null for a floor. Omitted from the JSON when
        /// null, so a floor post is byte-for-byte what it was before sides existed.</summary>
        [JsonProperty("side", NullValueHandling = NullValueHandling.Ignore)] public string Side { get; set; }

        /// <summary>The atlas page this post carries, or null for a floor or a side (and then left out of the
        /// JSON). Sent with SideLevel for the side's reason: a host too old to know it refuses the post.</summary>
        [JsonProperty("atlas", NullValueHandling = NullValueHandling.Ignore)] public int? Atlas { get; set; }

        /// <summary>"jpg". The one format an upload sends; the host checks the bytes' magic against
        /// it rather than trusting this.</summary>
        [JsonProperty("format")] public string Format { get; set; }

        [JsonProperty("imageBase64")] public string ImageBase64 { get; set; }
    }

    /// <summary>What the host did with one uploaded floor.</summary>
    internal sealed class MapUploadResponse
    {
        /// <summary>One of "stored" (send the next floor), "complete" (the host has the whole set),
        /// "declined" (this host does not take uploads at all - stop, and say so once) or "rejected"
        /// (this set is not acceptable - stop, and say why). Anything else is treated as a reply
        /// that is not the server half's.</summary>
        [JsonProperty("outcome")] public string Outcome { get; set; }

        /// <summary>The host's own words for a decline or a rejection. Shown as given.</summary>
        [JsonProperty("reason")] public string Reason { get; set; }

        /// <summary>How many floors of this map the host now holds, for the finishing line.</summary>
        [JsonProperty("floorsHeld")] public int FloorsHeld { get; set; }
    }

    /// <summary>One map the host holds a picture set for.</summary>
    internal sealed class MapIndexEntryDto
    {
        [JsonProperty("map")] public string Map { get; set; }

        /// <summary>The host's name for the exact set it holds. Compared against the
        /// <c>maps/&lt;key&gt;/stamp</c> file, and a difference is the whole trigger for a
        /// download - the client never compares timestamps for that, only for whether its OWN
        /// capture is newer.</summary>
        [JsonProperty("stamp")] public string Stamp { get; set; }

        /// <summary>When the raid that made this set ran, UTC, ISO-8601 - from the meta it came
        /// with. A local capture newer than this keeps the map.</summary>
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }

        /// <summary>What the whole set weighs on the host, for the download budget.</summary>
        [JsonProperty("bytes")] public long Bytes { get; set; }

        /// <summary>The set's meta, which is what a download writes out as
        /// <c>&lt;key&gt;.map.json</c>. Its floors say which levels to ask for.</summary>
        [JsonProperty("meta")] public MapCaptureMetaDto Meta { get; set; }

        /// <summary>The mesh the host holds for this set, or null. Read INSTEAD of reaching into
        /// <see cref="Meta"/> for it, because this is the level the decision is made at: the download
        /// asks for a mesh only when this is here, and checks what arrives against this block's
        /// sha256.</summary>
        [JsonProperty("mesh")] public MapCaptureMeshDto Mesh { get; set; }
    }

    /// <summary>The answer to GET /questtree/maps: everything the host has, and whether it would
    /// take an upload.</summary>
    internal sealed class MapIndexDto
    {
        /// <summary>The highest index shape this client reads. An index above it is IGNORED with one
        /// warning rather than read optimistically - the fields a later shape adds are the ones that
        /// would decide where a picture goes.</summary>
        public const int SupportedSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }

        /// <summary>Whether this host takes uploads. Read for the log line only: the authority is
        /// the upload's own answer, because the flag can change between this index and a post.</summary>
        [JsonProperty("acceptsUploads")] public bool AcceptsUploads { get; set; }

        [JsonProperty("maps")] public List<MapIndexEntryDto> Maps { get; set; } = new List<MapIndexEntryDto>();
    }

    /// <summary>Which picture to send down. The body of POST /questtree/maps/image - a POST rather
    /// than a path, so the map name and floor never have to survive URL escaping.</summary>
    internal sealed class MapImageRequest
    {
        [JsonProperty("map")] public string Map { get; set; }
        [JsonProperty("level")] public int Level { get; set; }

        /// <summary>A side's direction to fetch that side picture instead of a floor; null for a floor,
        /// and then left out of the JSON entirely.</summary>
        [JsonProperty("side", NullValueHandling = NullValueHandling.Ignore)] public string Side { get; set; }

        /// <summary>An atlas page number to fetch that page instead of a floor; null for a floor or a side.</summary>
        [JsonProperty("atlas", NullValueHandling = NullValueHandling.Ignore)] public int? Atlas { get; set; }
    }

    /// <summary>One floor's picture from the host. An empty <see cref="ImageBase64"/> with an empty
    /// <see cref="Stamp"/> means the host does not have that floor - not an error, and the rest of
    /// the set still lands (a floor with no picture is simply dropped by the reader).</summary>
    internal sealed class MapImageDto
    {
        [JsonProperty("map")] public string Map { get; set; }
        [JsonProperty("level")] public int Level { get; set; }

        /// <summary>The set this picture belongs to, for checking it against the index entry the
        /// download started from: a host re-captured mid-download has a different stamp, and half of
        /// each set is worse than neither.</summary>
        [JsonProperty("stamp")] public string Stamp { get; set; }

        /// <summary>"jpg" or "png" - what the bytes are, which is what names the file written.</summary>
        [JsonProperty("format")] public string Format { get; set; }

        [JsonProperty("imageBase64")] public string ImageBase64 { get; set; }
    }

    /// <summary>
    /// One capture's whole mesh file, offered to the host. The body of POST /questtree/maps/mesh.
    ///
    /// Sent AFTER the floors of the same capture, and it is the piece that completes the set on a host
    /// that knows about meshes: such a host answers the last floor with "waiting for the mesh" rather
    /// than "complete", precisely so a set's meta never names a mesh the host does not hold. An older
    /// host has already completed the set by then and answers this route with HTML, which costs one
    /// Debug line - see <see cref="MapTransfer"/>.
    ///
    /// No meta here: the capture is identified by <see cref="CapturedAt"/>, which its floors carried, so
    /// this route can neither start a set nor change one.
    /// </summary>
    internal sealed class MapMeshUploadRequest
    {
        /// <summary>The mesh upload shape. Its own number, and 1: nothing about an optional addition may
        /// refuse an older half of anything.</summary>
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("map")] public string Map { get; set; }

        [JsonProperty("clientVersion")] public string ClientVersion { get; set; }

        /// <summary>The capture this mesh belongs to - the same capturedAt the floors were posted
        /// with.</summary>
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }

        /// <summary>sha256 of <see cref="DataBase64"/>'s bytes, hex. The host checks the bytes against
        /// it and it against the staged capture's meta.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }

        /// <summary>The decoded length of the WHOLE mesh, for the host to check against what actually
        /// arrives (or what the parts add up to).</summary>
        [JsonProperty("bytes")] public long Bytes { get; set; }

        /// <summary>Which part of the mesh this post carries, from 0, when it goes up in
        /// <see cref="Parts"/> parts.</summary>
        [JsonProperty("part")] public int Part { get; set; }

        /// <summary>How many parts the mesh goes up in; 0 for a mesh in one post. Several because one
        /// post to a stock SPT host cannot carry more than 30,000,000 bytes (MapTransfer.MeshPartBytes);
        /// the host joins them and checks the whole as it checks a one-post mesh.</summary>
        [JsonProperty("parts")] public int Parts { get; set; }

        [JsonProperty("dataBase64")] public string DataBase64 { get; set; }
    }

    /// <summary>What the host did with the mesh. <see cref="Accepted"/> true with a reason is progress -
    /// the mesh is held while the set waits for a floor; false is a refusal whose reason is printed as
    /// given.</summary>
    internal sealed class MapMeshUploadResponse
    {
        [JsonProperty("accepted")] public bool Accepted { get; set; }

        /// <summary>Whether the host now serves a set for this capture (or a newer one) with or without
        /// the mesh - nothing of it is held waiting. See MapTransfer.JudgeMesh for the three sentences it
        /// chooses between.</summary>
        [JsonProperty("served")] public bool Served { get; set; }

        [JsonProperty("reason")] public string Reason { get; set; }
    }

    /// <summary>Which map's mesh to send down. The body of POST /questtree/maps/meshfile.</summary>
    internal sealed class MapMeshRequest
    {
        [JsonProperty("map")] public string Map { get; set; }
    }

    /// <summary>One map's mesh from the host. An empty <see cref="DataBase64"/> means the host has no
    /// mesh for that map - not an error: the set still lands and draws flat.</summary>
    internal sealed class MapMeshDto
    {
        [JsonProperty("map")] public string Map { get; set; }

        /// <summary>The set this mesh belongs to, checked against the index entry the download started
        /// from exactly as a picture's stamp is.</summary>
        [JsonProperty("stamp")] public string Stamp { get; set; }

        /// <summary>sha256 of the bytes, hex. Checked against the INDEX ENTRY's rather than trusted:
        /// this payload is the one nothing human ever looks at, so nothing else would notice it
        /// arriving wrong.</summary>
        [JsonProperty("sha256")] public string Sha256 { get; set; }

        [JsonProperty("bytes")] public long Bytes { get; set; }

        [JsonProperty("dataBase64")] public string DataBase64 { get; set; }
    }
}
