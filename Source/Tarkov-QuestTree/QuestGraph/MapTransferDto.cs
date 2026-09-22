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
    /// </list>
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

        /// <summary>Which floor of <see cref="Meta"/> this request carries the picture for.</summary>
        [JsonProperty("level")] public int Level { get; set; }

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
}
