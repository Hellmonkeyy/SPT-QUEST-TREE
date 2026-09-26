using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Moves captured map pictures between this client and the host it plays on: up, right after a
    /// capture, and down, once a session, for the maps somebody else captured.
    ///
    /// Why it exists. A capture costs one raid on one map, and a Fika group has one host and several
    /// players: without this, every player would have to raid every map to see a picture of it, and a
    /// map captured by the person who plays Interchange would never reach the person who does not.
    /// The host is the natural keeper - it is the machine every raid goes through, it already holds
    /// the harvested zones, and it is the one thing in the group that does not come and go.
    ///
    /// What is deliberate here:
    ///
    /// The host decides whether it takes uploads at all, and its refusal is one line, not a retry.
    /// A host that does not want a hundred megabytes of other people's pictures says so once per
    /// session and is never asked again (<see cref="_uploadsDeclined"/>).
    ///
    /// Nothing waits on the network on Unity's thread. Encoding is Texture2D work and cannot leave
    /// the main thread, so it is spread one floor per frame; every request is issued on a pool thread
    /// and polled from a coroutine (upload) or done wholesale on one worker (download), which is the
    /// same begin/poll/take shape <see cref="QuestDataClient.BeginAll"/> uses.
    ///
    /// A download never leaves a HALF set on disk. The reader's gate is the meta file - a picture
    /// nothing names is never drawn - so a set is assembled in a staging folder, the old meta is
    /// removed before the new pictures land, and the meta goes in last. Every instant in between has
    /// either the previous complete set or no set at all for that map, and no set is ever drawn with
    /// another set's meta. The stamp file, which is what decides whether to download again, is
    /// written last of all: a download killed anywhere is simply repeated.
    ///
    /// An older host answers these routes with SPT's own HTML, and a newer one can answer with an
    /// index shape this build does not know. Both are silent fallbacks to what the client already
    /// had - see <see cref="NotOurs"/> and <see cref="MapIndexDto.SupportedSchemaVersion"/>.
    /// </summary>
    internal static class MapTransfer
    {
        private const string UploadRoute = "/questtree/maps/upload";
        private const string IndexRoute = "/questtree/maps";
        private const string ImageRoute = "/questtree/maps/image";

        /// <summary>Where a capture's 3D mesh goes up, once, after its floors - see
        /// <see cref="MapMeshUploadRequest"/> for why it is not a floor of the upload route.</summary>
        private const string MeshRoute = "/questtree/maps/mesh";

        /// <summary>Where a map's mesh comes down. Asked only when the index entry carries a mesh
        /// block, so a host that has none is never asked and an OLD host is asked only by a client
        /// whose index it could not have answered anyway.</summary>
        private const string MeshFileRoute = "/questtree/maps/meshfile";

        /// <summary>The longest side, in pixels, a picture may have on the wire. A capture is taken
        /// at up to 4096 for this machine's own screen; what is shared is the smaller copy, because
        /// the difference between 2048 and 4096 is four times the bytes for detail that only shows at
        /// a zoom the map view rarely reaches.</summary>
        private const int MaxLongSide = 2048;

        /// <summary>JPEG quality for an uploaded floor. 80 is the plan's measured compromise: a
        /// 2048-side floor lands at 0.5-1.2 MB, and the artefacts are invisible under the pins.</summary>
        private const int JpegQuality = 80;

        /// <summary>What the transparent part of a capture is flattened onto before it becomes a JPEG.
        ///
        /// A capture's picture is RGBA now: the walkable mask is its alpha, so everything outside the
        /// playable world - and every chunk the game had streamed out - is transparent, and the Maps tab
        /// shows its own backdrop through it (MapCapture.ReachIsAlpha). A JPEG cannot carry alpha at all,
        /// so a host copy has to be flattened onto SOMETHING, and that something has to be what the local
        /// picture looks like against the tab it is drawn in or the two will not match.
        ///
        /// The SLAB, (0.17, 0.18, 0.19) - 43, 46, 48 in eight bits - and not the black the first version
        /// used. UI/MapView.cs:153 (MapView.BackdropColor) draws that slab over a floor's whole bounds
        /// wherever there is no picture of it, and the local RGBA picture is drawn on top of it; the
        /// viewport's black backing plate is behind the slab, not behind the picture. So the skirt of a
        /// local capture reads as the slab, and a host JPEG flattened onto black came back a visibly
        /// darker rectangle than the same capture looks like at home - two machines showing the same map
        /// in two different tones, which is exactly what this constant exists to prevent.
        ///
        /// The slab's own 95 % alpha is not carried into this. Composited over the black plate it would be
        /// 41, 44, 46 instead of 43, 46, 48 - two levels in eight bits, under what a q80 JPEG preserves
        /// anyway - and a JPEG has no alpha to record it in.</summary>
        private static readonly Color32 BackdropFill = new Color32(43, 46, 48, 255);

        /// <summary>The most one encoded floor may weigh before this side declines to offer it.
        /// Mirrors the host's own per-floor ceiling: a floor over it would be rejected, and a
        /// rejection stops the whole upload, so the client skips the floor instead and still offers
        /// the rest.</summary>
        private const int MaxFloorUploadBytes = 2500 * 1024;

        /// <summary>The most one floor may weigh coming DOWN, decoded. Nothing this mod uploads comes
        /// near it; it is a guard against a host - or something answering as one - filling the disk.</summary>
        private const int MaxFloorDownloadBytes = 8 * 1024 * 1024;

        /// <summary>
        /// The largest mesh ANY host takes, and so the largest this side offers: the protocol's absolute
        /// (MapStore.MeshAbsolute), set by the one-body download - the host base64s the whole mesh into one
        /// string. A host with less disk takes less (its ceiling is derived from its own free disk) and drops the
        /// mesh at the first post, keeping the pictures. Past <see cref="MeshPartBytes"/> a mesh goes up in parts.
        /// Also the most <see cref="ClientMeshCeiling"/> ever is. Rollback: 48 MiB (the pre-WP7 fixed ceiling).
        /// </summary>
        private const long ClientMeshAbsolute = 512L << 20;

        /// <summary>The share of RAM one mesh download may be: the one-body download holds the response string
        /// (base64 in UTF-16, 2.67 bytes a mesh byte), the DTO's copy (2.67) and the decoded bytes (1) - about 6.3
        /// times the mesh - and that is allowed an eighth of RAM, so a fiftieth of RAM per mesh byte.</summary>
        private const long ClientRamShare = 50;

        /// <summary>The host-picture cache's ceiling is a quarter of the free disk, never under this (the
        /// pre-WP7 fixed 2 GiB) nor over <see cref="HostCacheTop"/>.</summary>
        private const long HostCacheFloor = 2L << 30;

        /// <summary>The most the host-picture cache holds, however much disk is free. Rollback: 2 GiB.</summary>
        private const long HostCacheTop = 32L << 30;

        /// <summary>The host's own cap on one picture (MapStore.MaxImageBytes): 2.5 MiB. The per-map download
        /// bound counts twelve of them (eight floors, four sides).</summary>
        private const long HostPictureBytes = 2_621_440;

        /// <summary>This machine's mesh ceiling (D15) and host-cache ceiling (D17), sized ONCE per session on the
        /// main thread by <see cref="SizeSessionLimits"/> (SystemInfo is Unity's) before the download worker
        /// starts, which only reads them. Until then the pre-WP7 numbers.</summary>
        private static long _clientMeshCeiling = 48L << 20;

        private static long _hostCacheCeiling = HostCacheFloor;

        private static bool _limitsSized;

        /// <summary>The largest mesh this machine downloads (D15): min(512 MiB, RAM / 50) - 16 GB -> 328 MiB,
        /// 8 GB -> 164 MiB, 32 GB -> 512 MiB.</summary>
        internal static long ClientMeshCeiling => _clientMeshCeiling;

        /// <summary>The most one map may weigh coming down (D16): twelve pictures at the host's cap, eight atlas
        /// pages at theirs, and <see cref="ClientMeshCeiling"/>.</summary>
        internal static long MaxMapDownload => 12L * HostPictureBytes + 8L * MaxAtlasPageBytes + ClientMeshCeiling;

        /// <summary>The most the host-picture cache holds on this disk (D17): clamp(free / 4, 2 GiB, 32 GiB).</summary>
        internal static long HostCacheCeiling => _hostCacheCeiling;

        /// <summary>The mesh ceiling for this much RAM (D15). Pure, for the harness.</summary>
        /// <param name="ramMb">SystemInfo.systemMemorySize.</param>
        internal static long ClientMeshCeilingFor(long ramMb) =>
            Math.Min(ClientMeshAbsolute, (Math.Max(0L, ramMb) << 20) / ClientRamShare);

        /// <summary>The host-cache ceiling for this much free disk (D17). Pure, for the harness.</summary>
        /// <param name="free">Free bytes on the maps' volume; zero or less when unknown (the floor).</param>
        internal static long HostCacheCeilingFor(long free) =>
            Math.Min(HostCacheTop, Math.Max(HostCacheFloor, Math.Max(0L, free) / 4));

        /// <summary>The longest a mesh download may take (D18), in seconds.</summary>
        private const double MeshDownloadMaxSeconds = 1800d;

        /// <summary>The slowest link a mesh download is sized for, bytes a second: 512 KiB/s.</summary>
        private const double MeshDownloadBytesPerSecond = 512d * 1024d;

        /// <summary>
        /// A mesh download's deadline for its size (D18): 60 s plus the transfer of 4/3 of the mesh (its base64)
        /// at 512 KiB/s, never under <see cref="MeshRequestTimeout"/> nor over 30 minutes - 48 MiB -> 240 s,
        /// 160 MiB -> 487 s, 512 MiB -> 1,425 s. Needs the dedicated transfer client (one request, one deadline);
        /// its HttpClient backstop is above the longest of these.
        /// </summary>
        /// <param name="bytes">The mesh's size, as the index declares it.</param>
        internal static double MeshDownloadSecondsFor(long bytes) =>
            Math.Min(MeshDownloadMaxSeconds,
                Math.Max(MeshRequestTimeout.TotalSeconds, 60d + Math.Max(0L, bytes) * 4d / 3d / MeshDownloadBytesPerSecond));

        /// <summary>
        /// MAIN THREAD, once per session, before the download worker starts: this machine's mesh and host-cache
        /// ceilings from its RAM and the free disk under the maps folder, said once.
        /// </summary>
        private static void SizeSessionLimits()
        {
            if (_limitsSized) return;
            _limitsSized = true;

            var ramMb = 0L;
            try { ramMb = SystemInfo.systemMemorySize; } catch (Exception) { ramMb = 0L; }

            if (ramMb > 0) _clientMeshCeiling = ClientMeshCeilingFor(ramMb);

            var free = 0L;

            try
            {
                var root = MapsRoot();
                var drive = string.IsNullOrEmpty(root) ? null : Path.GetPathRoot(Path.GetFullPath(root));
                if (!string.IsNullOrEmpty(drive)) free = new DriveInfo(drive).AvailableFreeSpace;
            }
            catch (Exception)
            {
                free = 0L;
            }

            _hostCacheCeiling = HostCacheCeilingFor(free);

            Plugin.LogSource?.LogInfo(
                $"QuestTree: this machine takes host meshes up to {Mb(_clientMeshCeiling)} MB (RAM {ramMb:N0} MB), keeps " +
                $"up to {Mb(_hostCacheCeiling)} MB of host maps ({Mb(free)} MB free).");
        }

        /// <summary>
        /// How much of a mesh one upload post carries. A stock SPT host runs on Kestrel with its default
        /// request limit of 30,000,000 bytes, and nothing in SPT raises it - measured on a real Kestrel
        /// (scratchpad kestrel-limit): a body of 32.5 MB is refused with "Request body too large", one of
        /// 23.5 MB goes through. Every body is zlib-compressed on the way (TransferHttp sends what SPT's
        /// RequestHandler would, at the same level), which brings the base64
        /// of an already-deflated mesh back to about 1.03 times the mesh, so ONE post can carry a mesh of
        /// roughly 28 MB and no more - and a mesh may be up to 512 MiB. A mesh past this size is therefore sent in
        /// ceil(bytes / 16 MiB) parts (MapStore.HoldMeshPart joins them and checks the whole exactly as it checks
        /// a mesh sent in one post; it takes up to 64). 16 MiB is ~17 MB on the wire: over 40 % under the limit,
        /// so a less compressible body than any measured still fits. The host's 24 MiB part ceiling is postable
        /// only compressed, which is why this side does not move to 24 MiB parts to save posts.
        /// </summary>
        private const int MeshPartBytes = 16 * 1024 * 1024;

        /// <summary>The most atlas pages one capture carries - the builder's own cap, and the host's
        /// (MapStore.MaxAtlasPages).</summary>
        private const int MaxAtlasPages = 8;

        /// <summary>The long side an atlas page may have: the builder packs 4096 px pages, and a page is
        /// sent at its FULL size - never scaled to <see cref="MaxLongSide"/> as a floor is, because the mesh's
        /// UVs address it texel by texel and a halved page is every wall blurred.</summary>
        private const int MaxAtlasPixels = 4096;

        /// <summary>The JPEG quality an atlas page goes up at. Higher than a floor's
        /// <see cref="JpegQuality"/>: a floor is a map seen from above, a page is brick, signage and window
        /// frames seen up close in the 3D view, where blocking shows - and a tile's edge sits against its
        /// neighbour's, so ringing across a JPEG block bleeds one texture into the next unless the builder's
        /// 16 px tile padding holds it. A page over <see cref="MaxAtlasPageBytes"/> at this quality is encoded
        /// ONCE more at <see cref="AtlasRetryJpegQuality"/> before it is given up on.</summary>
        private const int AtlasJpegQuality = 90;

        /// <summary>The second and last quality a page is tried at when the first came out over the cap.</summary>
        private const int AtlasRetryJpegQuality = 80;

        /// <summary>The most one atlas page may weigh as a JPEG, in either direction - 6 MB, the host's own
        /// cap (MapStore.MaxAtlasPageBytes). A 4096 px page of building textures at q90 measures 3-5 MB; a page
        /// past six at q90 is encoded again at q80, and one past six even then is not offered but still POSTED
        /// empty, so the host drops it rather than waiting for it,
        /// and the buildings drawn from it fall back to the sides and tints they had before pages.</summary>
        private const int MaxAtlasPageBytes = 6 * 1024 * 1024;

        /// <summary>The four sides a capture may carry an oblique picture from, in the order they are
        /// posted and fetched - the host's own order (MapStore.SideDirs), so both halves walk them
        /// alike.</summary>
        private static readonly string[] SideDirs = { "N", "S", "E", "W" };

        /// <summary>The level a SIDE post carries. No floor has it, which is the point: a host that
        /// predates <see cref="MapUploadRequest.Side"/> ignores that field and reads the post as a floor
        /// of this level - and refuses it as one its meta does not name, instead of storing a side
        /// picture over a real floor. A host that knows sides ignores the level.</summary>
        private const int SideLevel = int.MinValue;

        /// <summary>The most floors of one map to take from a host, matching the harvested band
        /// ceiling the zone file enforces.</summary>
        private const int MaxFloors = 8;

        /// <summary>Rollback for review F57: true sends every map transfer through <see cref="TransferHttp"/>,
        /// false puts them back on SPT's RequestHandler (its retries and its 100 s timeout included).</summary>
        private const bool DedicatedTransferClient = true;

        /// <summary>How long a picture request may take, end to end - a floor or side up or down, and it is
        /// ENFORCED: the request is aborted at it (TransferHttp), never retried.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The deadline on a MESH-SIZED request - one mesh part up, one atlas page up or down - aborted at
        /// it like every other, and the least a whole mesh coming down gets (<see cref="MeshDownloadSecondsFor"/>
        /// scales that one with its size). 240 s: a 16 MiB part at 2 MB/s is 8 s of transfer, and a remote
        /// host's slow uplink is the whole point of the transport. Before review F57 this was not what a
        /// request actually got: SPT's own client cut every attempt at 100 s and sent it again up to three
        /// times, so nothing needing more than 100 s ever landed and a failure took ~400 s to be reported.</summary>
        private static readonly TimeSpan MeshRequestTimeout = TimeSpan.FromSeconds(240);

        /// <summary>How far past its own deadline a blocking caller waits before it gives up on a request that
        /// has not ended - a backstop only; the request aborts itself at the deadline.</summary>
        private static readonly TimeSpan AbortGrace = TimeSpan.FromSeconds(10);

        /// <summary>How long the whole download may take before it gives up and leaves the rest for
        /// the next session. A worker that never returns is one the session can never retry.
        ///
        /// A MINIMUM since stage W, not a flat limit: past it the session keeps going for as long as it is
        /// still receiving at <see cref="MinSyncBytesPerSecond"/> or better on average, and while what it takes
        /// fits <see cref="HostCacheCeiling"/> (WP7 removed the fixed 600 MB a session). A flat three minutes took a
        /// third of a 3D map a session on a slow link and all of them on a fast one.</summary>
        private static readonly TimeSpan SyncBudget = TimeSpan.FromMinutes(3);

        /// <summary>The average rate, over the whole session, a download must be keeping up past
        /// <see cref="SyncBudget"/> to go on to the next map. 1 MB/s.</summary>
        private const double MinSyncBytesPerSecond = 1024d * 1024d;

        /// <summary>Where a download stages a set before it replaces the one in place. Under the maps
        /// folder, so it is on the same volume as its destination and a move cannot become a copy;
        /// named with a leading dot, which is what the reader skips (MapCatalog.ScanFolder).</summary>
        internal const string IncomingFolder = ".incoming";

        /// <summary>The file holding the host's name for the set in a map's folder. Not JSON and not
        /// read by the map reader at all: it exists so the next session can tell "I have this set"
        /// from "I have some set", and it is written after everything it describes.</summary>
        private const string StampFile = "stamp";

        /// <summary>Said once per session and then never again - the host has made its position
        /// clear, and a line per capture would be noise about something the player cannot change
        /// from here.</summary>
        private static bool _uploadsDeclined;

        /// <summary>Whether an upload is running. One at a time: two would fight for the frames and
        /// the host would interleave two maps' floors.</summary>
        private static bool _uploading;

        /// <summary>Captures that finished while another upload was running, offered one after another as each
        /// upload ends (<see cref="StartNextPending"/>). Main thread only, like everything upload-side.</summary>
        private static readonly HashSet<string> _pendingUploads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Offers the next queued capture, if any. Called as an upload ends.</summary>
        private static void StartNextPending()
        {
            try
            {
                var next = _pendingUploads.FirstOrDefault();

                if (next == null) return;

                _pendingUploads.Remove(next);

                // A host that declined takes nothing more this session - the queue goes with it.
                if (_uploadsDeclined)
                {
                    _pendingUploads.Clear();
                    return;
                }

                UploadCapture(next);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: a queued capture could not be offered ({ex.Message}).");
            }
        }

        // ------------------------------------------------------------------ upload

        /// <summary>
        /// Offers the capture of one map to the host, floor by floor, as JPEGs scaled to fit
        /// <see cref="MaxLongSide"/>.
        ///
        /// Called by <see cref="MapCapture"/> the moment a capture's meta is safely on disk - never
        /// before, because what is uploaded is read back from those files, and a meta that failed to
        /// write is a capture that does not exist.
        ///
        /// Returns at once: the work is a coroutine on the plugin object, which outlives the raid the
        /// capture was taken in, so an upload that is still going when the player extracts finishes
        /// rather than dying with the GameWorld. Never throws.
        /// </summary>
        /// <param name="key">The map's internal id, which is also its capture folder's name.</param>
        internal static void UploadCapture(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;

                // The player's choice, and the default is on: a host that does not want uploads
                // refuses them itself, so the setting is for the player who does not want to offer
                // even that. Read defensively - a failed config bind leaves the entry null.
                if (ModSettings.Ready && ModSettings.UploadCaptures != null && !ModSettings.UploadCaptures.Value) return;

                // Already said no. Silent from here - see _uploadsDeclined.
                if (_uploadsDeclined) return;

                if (_uploading)
                {
                    // QUEUED, and offered the moment the running upload ends (review F32: this line used to
                    // promise a later offer that nothing made). One entry per map - a later capture of the
                    // same map replaces the earlier one on disk anyway.
                    _pendingUploads.Add(key);

                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: an upload is already running, so the capture of {key} waits and goes up when it ends.");
                    return;
                }

                var host = Plugin.Instance;
                if (host == null)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: no plugin object to run the upload of {key} on - the capture stays on this machine.");
                    return;
                }

                _uploading = true;
                host.StartCoroutine(UploadRoutine(key));
            }
            catch (Exception ex)
            {
                _uploading = false;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the capture of {key} could not be offered to the host ({ex.GetType().Name}: {ex.Message}) " +
                    "- it is on this machine either way.");
            }
        }

        /// <summary>One capture's floors, encoded a frame apart and posted one at a time.
        ///
        /// Written as guarded steps returning a bool, like MapCapture.Run and for the same reason: C#
        /// forbids a yield inside a try that has a catch, so every step carries its own.</summary>
        /// <param name="key">The map's internal id.</param>
        private static IEnumerator UploadRoutine(string key)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // One frame first (review F34): StartCoroutine runs this synchronously up to its first yield,
                // and the caller is the frame that just finished writing the capture - so without it the read
                // and hash of the mesh below (tens of MB, up to 512 MiB) landed in that same frame.
                yield return null;

                if (!ReadCapture(key, out var meta, out var floors)) yield break;

                // BEFORE the first post, and for every floor at once - see DescribeWire. The meta
                // travels with every post and the host checks EVERY floor in it against the meta's
                // own scale, so rewriting one floor at a time would make the first post describe no
                // single projection and be refused outright.
                if (!DescribeWire(key, meta, floors)) yield break;

                // The SIDES, read and described for the wire before the first post for the reason the
                // floors are: every post carries the meta, and the host checks each side in it against
                // its own scale. A side whose picture is not on this disk is taken out of the meta here,
                // so the host is never told to wait for it.
                var sides = ReadSides(key, meta);

                DescribeSidesForWire(key, meta, sides);

                // The MESH, read and checked BEFORE the first post. Before, because the meta travels
                // with every floor and a host that sees a mesh block HOLDS THE WHOLE SET until the file
                // arrives - so a block this machine cannot honour has to be out of the meta before the
                // meta is sent, or the capture is lost to a wait that never ends. Null with the block
                // already stripped when there is nothing to offer.
                var mesh = PrepareMesh(key, meta);

                // The ATLAS pages, read AFTER the mesh is settled and before the first post: a page drapes
                // the mesh's buildings and nothing else, so with no mesh to offer the meta names no pages
                // (a host told about a page waits for it), and a page not on this disk is taken out of the
                // meta here for the sides' reason.
                var pages = ReadAtlas(key, meta, mesh != null);

                var posted = 0;
                long bytes = 0;

                // Floors that failed to encode SINCE THE LAST SUCCESSFUL POST - not since the start.
                // The meta travels with every post, and a drop takes the floor out of it, so the host's
                // staged meta is the one the LAST post carried: it names exactly the floors known at that
                // moment. A floor dropped before that post is a floor the host was never told about and
                // is not waiting for; a floor dropped after it is one the host will wait for forever.
                // Only the second kind can stop the set completing, and the first version of this counted
                // both - which meant one unencodable interior floor cost the whole capture its mesh, and
                // with it the whole set, where before this release the rest of the map was shared.
                var droppedSincePost = 0;
                FloorUpload lastPosted = null;

                foreach (var floor in floors)
                {
                    // A fresh frame per floor: the encode below is a LoadImage, a blit, a readback
                    // and an EncodeToJPG of a picture up to 4096 px on a side, which is the one part
                    // of this that cannot leave Unity's thread.
                    yield return null;

                    if (!Encode(key, floor))
                    {
                        // Taken out of the meta the LATER floors carry, so the host is never told
                        // about a picture it is not going to be sent. The posts already made named
                        // it, which no reader can be hurt by: a floor with no picture is dropped by
                        // the reader and by the download that writes a set out.
                        meta.Floors.Remove(floor.Entry);
                        droppedSincePost++;
                        continue;
                    }

                    var task = StartPost(key, meta, floor, RequestTimeout);
                    if (task == null) yield break;

                    // Bounded: the request aborts itself at RequestTimeout (review F57), so _uploading is held for
                    // about that and never for SPT's retry chain. The backstop is for the one case the token does
                    // not cover on Mono - a body that stalls after its headers runs to the socket's read timeout -
                    // where the routine gives the request up and takes it as the deadline (GiveUp).
                    var until = Time.realtimeSinceStartup + (float)(RequestTimeout + AbortGrace).TotalSeconds;
                    while (!task.IsCompleted && Time.realtimeSinceStartup < until) yield return null;
                    task = GiveUp(task, UploadRoute, RequestTimeout);

                    if (TimedOut(task))
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: the host did not answer within {RequestTimeout.TotalSeconds:0}s while {key} " +
                            $"\"{floor.Name}\" was being offered - the rest of the capture is not sent.");
                        Observe(task);
                        yield break;
                    }

                    var verdict = Judge(key, floor, task, out var held, out var completeReason, out var meshKept);
                    if (verdict == Verdict.Stop) yield break;

                    posted++;
                    bytes += floor.Bytes;
                    lastPosted = floor;

                    // The host has just been handed a meta naming exactly the floors that are left, so
                    // whatever was dropped before now is not something it is waiting for.
                    droppedSincePost = 0;

                    if (verdict == Verdict.Complete)
                    {
                        // The host has the set and wants nothing more. Either this capture has no mesh,
                        // or the host dropped the mesh (an older host drops one past 12 MB at the meta;
                        // any host drops one its meta cannot describe), or it already had the mesh staged
                        // from an earlier attempt of the same capture - in all three, sending the mesh now
                        // would be megabytes the host has no place for. Only the middle one is news.
                        SayIfMeshWasNotKept(key, mesh, meshKept, completeReason);
                        Done(key, Math.Max(posted, held), bytes, 0, clock);
                        yield break;
                    }
                }

                // Review F31: floors that failed to encode AFTER the last post leave the host holding a meta that
                // still names them. One more post of the last good floor carries the trimmed meta - the host
                // restages its meta on every post and counts what is missing from it - so the set can
                // complete after all, where this used to give up and ask for a new capture. Its picture is
                // still on its FloorUpload, so nothing is encoded again.
                if (posted > 0 && droppedSincePost > 0 && lastPosted != null && !string.IsNullOrEmpty(lastPosted.Base64))
                {
                    var again = StartPost(key, meta, lastPosted, RequestTimeout);
                    if (again == null) yield break;

                    // Bounded by the request's own deadline, backstopped - see the floor loop.
                    var untilAgain = Time.realtimeSinceStartup + (float)(RequestTimeout + AbortGrace).TotalSeconds;
                    while (!again.IsCompleted && Time.realtimeSinceStartup < untilAgain) yield return null;
                    again = GiveUp(again, UploadRoute, RequestTimeout);

                    if (TimedOut(again))
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: the host did not answer within {RequestTimeout.TotalSeconds:0}s while {key}'s trimmed set " +
                            "was being offered - the rest of the capture is not sent.");
                        Observe(again);
                        yield break;
                    }

                    var verdict = Judge(key, lastPosted, again, out var held, out var completeReason, out var meshKept);
                    if (verdict == Verdict.Stop) yield break;

                    droppedSincePost = 0;

                    if (verdict == Verdict.Complete)
                    {
                        SayIfMeshWasNotKept(key, mesh, meshKept, completeReason);
                        Done(key, Math.Max(posted, held), bytes, 0, clock);
                        yield break;
                    }
                }

                // The SIDES, after the floors and before the mesh - and only when the host still holds a
                // meta it can complete (nothing dropped since the last post): otherwise it is waiting for a
                // floor that will never arrive, and nothing more is worth sending.
                var sidesPosted = 0;
                var hostPredatesSides = false;

                if (posted > 0 && droppedSincePost == 0 && sides.Count > 0)
                {
                    foreach (var side in sides)
                    {
                        yield return null;

                        // A side that cannot be encoded is still POSTED - empty. The host has already been
                        // told to expect it (every floor's meta named it), and an empty side post is how it
                        // is told to stop expecting it; leaving it unsent would hold the whole set until the
                        // host's next start a day later.
                        var encoded = Encode(key, side);

                        if (!encoded)
                        {
                            side.Base64 = "";
                            side.Bytes = 0;

                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: {key}'s {side.Side} side picture could not be prepared, so the host is told " +
                                "to go on without it.");
                        }

                        var task = StartPost(key, meta, side, RequestTimeout);
                        if (task == null) yield break;

                        // Bounded by the request's own deadline, backstopped - see the floor loop.
                        var untilSide = Time.realtimeSinceStartup + (float)(RequestTimeout + AbortGrace).TotalSeconds;
                        while (!task.IsCompleted && Time.realtimeSinceStartup < untilSide) yield return null;
                        task = GiveUp(task, UploadRoute, RequestTimeout);

                        if (TimedOut(task))
                        {
                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: the host did not answer within {RequestTimeout.TotalSeconds:0}s while {key}'s " +
                                $"{side.Side} side was being offered - the rest of the capture is not sent.");
                            Observe(task);
                            yield break;
                        }

                        // Out of the meta the later posts carry once it is dealt with either way: the host
                        // now has it or has dropped it, and nothing after this needs to name it again.
                        if (!encoded) meta.Sides?.Remove(side.SideEntry);

                        var verdict = JudgeSide(key, side, task, out var sideReason, out var sideMeshKept);

                        if (verdict == SideVerdict.Stop) yield break;

                        // A host that takes no sides - one from before they existed - has already been
                        // shown every floor, and may still be waiting for the mesh. Sides stop; the mesh
                        // goes - and no atlas page is offered, since a host older than sides is older than
                        // pages too.
                        if (verdict == SideVerdict.NoSides)
                        {
                            hostPredatesSides = true;
                            break;
                        }

                        if (encoded)
                        {
                            sidesPosted++;
                            bytes += side.Bytes;
                        }

                        if (verdict == SideVerdict.Complete)
                        {
                            SayIfMeshWasNotKept(key, mesh, sideMeshKept, sideReason);
                            Done(key, posted, bytes, 0, clock, sidesPosted);
                            yield break;
                        }
                    }
                }

                // The ATLAS PAGES, after the sides and before the mesh, on exactly the sides' terms: each
                // page encoded a frame apart at its full size, one that cannot be encoded POSTED EMPTY so the
                // host stops waiting for it, a drop logged and passed over, a real refusal the end of the
                // upload. The one difference is what an old host means: a host from before pages (stage W)
                // reads a page post as a floor of SideLevel and refuses it by level, and it never read the
                // meta's atlas either - so it is not waiting for pages, and the mesh still goes.
                var pagesPosted = 0;

                if (posted > 0 && droppedSincePost == 0 && !hostPredatesSides && pages.Count > 0)
                {
                    foreach (var page in pages)
                    {
                        yield return null;

                        var encoded = Encode(key, page);

                        if (!encoded)
                        {
                            page.Base64 = "";
                            page.Bytes = 0;

                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: {key}'s {page.Name} could not be prepared, so the host is told to go on " +
                                "without it - the buildings textured from it draw without it.");
                        }

                        // The MESH's deadline, not a picture's: a page is up to 6 MB, 8 MB of base64, and at
                        // 30 s a link under ~2 Mbit/s would time out every page - and since review F57 it is the
                        // deadline the request really gets, aborted at it rather than cut by SPT's client at
                        // 100 s and sent again. And a page that does not get
                        // through is DROPPED rather than ending the upload - the post is made again EMPTY,
                        // which tells the host to stop waiting for that page - because ending it here would
                        // leave the host holding the whole set, mesh unsent, until its stale sweep a day
                        // later: a whole map lost over one sheet of wall textures. At most two posts a page.
                        Task<string> task = null;

                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            task = StartPost(key, meta, page, MeshRequestTimeout);
                            if (task == null) break;

                            // Bounded: the request ends by MeshRequestTimeout (review F57), backstopped as the
                            // floor loop is. A timeout is a fault like any other here - the line below names it -
                            // and the page is posted again empty.
                            var untilPage = Time.realtimeSinceStartup + (float)(MeshRequestTimeout + AbortGrace).TotalSeconds;
                            while (!task.IsCompleted && Time.realtimeSinceStartup < untilPage) yield return null;
                            task = GiveUp(task, UploadRoute, MeshRequestTimeout);

                            // Answered, or already the empty post: JudgeSide takes it from here.
                            if (!(task.IsFaulted || task.IsCanceled) || !encoded) break;

                            Plugin.LogSource?.LogInfo(
                                $"QuestTree: {key}'s {page.Name} did not get through to the host " +
                                $"({task.Exception?.GetBaseException().Message ?? "cancelled"}) - the host is told to go on " +
                                "without it, and the rest of the capture is still sent.");

                            encoded = false;
                            page.Base64 = "";
                            page.Bytes = 0;
                        }

                        if (task == null) yield break;

                        if (!encoded) meta.Atlas?.Remove(page.AtlasEntry);

                        var verdict = JudgeSide(key, page, task, out var pageReason, out var pageMeshKept);

                        if (verdict == SideVerdict.Stop) yield break;

                        // A host from before pages: stop offering them; the mesh goes.
                        if (verdict == SideVerdict.NoSides) break;

                        if (encoded)
                        {
                            pagesPosted++;
                            bytes += page.Bytes;
                        }

                        if (verdict == SideVerdict.Complete)
                        {
                            SayIfMeshWasNotKept(key, mesh, pageMeshKept, pageReason);
                            Done(key, posted, bytes, 0, clock, sidesPosted, pagesPosted);
                            yield break;
                        }
                    }
                }

                // Past the loop, so the host never called the set complete. With a mesh to offer that is
                // the EXPECTED state - a 1.19.0 host answers the last floor "waiting for the mesh" - so
                // the mesh goes now, and it is the post that completes the set.
                //
                // Not when a floor was dropped SINCE THE LAST POST, though: the host is then waiting for
                // a picture that will never arrive, so it would hold the mesh with the rest and discard
                // the lot. A mesh's worth of posts to a host that cannot use them is worth skipping.
                if (posted > 0 && droppedSincePost == 0 && mesh != null)
                {
                    // In PARTS when the mesh is past what one post can carry to a stock host - see
                    // MeshPartBytes. Each part is its own post under its own deadline; every part but the
                    // last must come back "holding part k of n", and anything else - a refusal, an old host,
                    // a dropped connection - is judged exactly as a one-post mesh's answer would be, and ends
                    // the upload there. The LAST part's answer is the mesh's answer.
                    var parts = MeshPartCount(mesh.Length);
                    Task<string> task = null;

                    for (var part = 0; part < parts; part++)
                    {
                        task = StartMeshPost(key, meta, mesh, part, parts);

                        // Its own exit, so the line below cannot blame an unencodable floor for a thread
                        // pool that would not take the work. StartMeshPost has already said what happened.
                        if (task == null) yield break;

                        // Bounded: the part's request ends by MeshRequestTimeout (review F57) - landed, refused,
                        // or aborted at the deadline - backstopped as the floor loop is. A part that timed out is
                        // not "held", so the loop ends and JudgeMesh says why ("no answer from ... within 240s").
                        var untilPart = Time.realtimeSinceStartup + (float)(MeshRequestTimeout + AbortGrace).TotalSeconds;
                        while (!task.IsCompleted && Time.realtimeSinceStartup < untilPart) yield return null;
                        task = GiveUp(task, MeshRoute, MeshRequestTimeout);

                        if (part < parts - 1 && MeshPartHeld(task)) continue;

                        break;
                    }

                    switch (JudgeMesh(key, mesh, task))
                    {
                        case MeshVerdict.Stored:
                            Done(key, posted, bytes, mesh.Length, clock, sidesPosted, pagesPosted);
                            break;

                        case MeshVerdict.ServedFlat:
                            // The pictures ARE on the host - the warning above said the mesh is not - so
                            // the upload's own line is still true, without the mesh in it. Nor the pages:
                            // a set served flat is served without them.
                            Done(key, posted, bytes, 0, clock, sidesPosted);
                            break;
                    }

                    // Every other outcome has had its own line from JudgeMesh, which is why nothing
                    // follows this.
                    yield break;
                }

                // Nothing reached the host at all. Encode or Judge has already said why, for the floor it
                // happened on, so this adds nothing.
                if (posted == 0) yield break;

                if (droppedSincePost > 0)
                    // The host holds a meta naming a floor that then failed to encode, so it can never
                    // call the set complete. Said as what it is rather than as a success: the player's
                    // next question is why the map never appeared on the other machine, and "uploaded to
                    // the host" would answer it wrongly.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {posted} floor(s) of {key} reached the host but it never had the whole set - a " +
                        "floor could not be encoded after the others had been sent, so the host drops them at its " +
                        $"next start a day later{(mesh == null ? "" : ", and its 3D mesh was not offered")}. " +
                        "Capture the map again.");
                else
                    // Every floor the host was told about was sent, nothing was dropped, and there is no
                    // mesh to finish with - so the host is waiting for something this client does not know
                    // it wants. Nothing here can put that right, and a line that guessed at the cause
                    // would be worse than one that says exactly this.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: every floor of {key} was sent ({posted}) and the host has not called the set " +
                        "complete - it is waiting for something this build did not offer. The capture is on this " +
                        "machine either way; the host's own log says what it is holding.");
            }
            finally
            {
                _uploading = false;

                // The next capture that finished while this one was going up (review F32).
                StartNextPending();
            }
        }

        /// <summary>What the host's answer to one floor means for the rest of the upload.</summary>
        private enum Verdict
        {
            /// <summary>Accepted; send the next floor.</summary>
            Continue,

            /// <summary>Accepted, and the host has the whole set.</summary>
            Complete,

            /// <summary>Send nothing more. The reason has already been logged.</summary>
            Stop
        }

        /// <summary>The capture on disk, ready to upload: its meta (with the fields an upload
        /// rewrites already rewritten for the pictures it will send) and the floors whose picture is
        /// actually there. False - quietly, at Debug - when there is nothing to offer.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The capture's meta, or null.</param>
        /// <param name="floors">The floors to post, in level order. Never null when this returns true.</param>
        private static bool ReadCapture(string key, out MapCaptureMetaDto meta, out List<FloorUpload> floors)
        {
            meta = null;
            floors = new List<FloorUpload>();

            try
            {
                var dir = CaptureDir(key);
                if (dir == null || !Directory.Exists(dir)) return false;

                var path = Path.Combine(dir, key + MetaSuffix);
                if (!File.Exists(path)) return false;

                meta = JsonConvert.DeserializeObject<MapCaptureMetaDto>(File.ReadAllText(path));

                if (meta == null || meta.Extent == null || meta.Floors == null || meta.Floors.Count == 0)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: the capture meta for {key} has no floors to offer the host.");
                    return false;
                }

                // The folder is the authority on which map this is - the same fallback the reader
                // applies - so a meta whose map field was lost is not filed under nothing.
                if (string.IsNullOrEmpty(meta.Map)) meta.Map = key;

                var seen = new HashSet<int>();

                foreach (var floor in meta.Floors)
                {
                    if (floor == null) continue;
                    if (string.IsNullOrEmpty(floor.File)) continue;
                    if (!seen.Add(floor.Level)) continue;

                    // The meta is a file on this disk and this name becomes a path: a bare file name
                    // in the capture's own folder, exactly as MapCatalog.ReadFloors demands.
                    if (!string.Equals(floor.File, Path.GetFileName(floor.File), StringComparison.Ordinal)) continue;

                    var picture = Path.Combine(dir, floor.File);
                    if (!File.Exists(picture)) continue;

                    floors.Add(new FloorUpload
                    {
                        Level = floor.Level,
                        Name = floor.Name ?? "",
                        Path = picture,
                        Entry = floor
                    });
                }

                floors.Sort((a, b) => a.Level.CompareTo(b.Level));
                return floors.Count > 0;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the capture of {key} could not be read back for upload ({ex.Message}).");
                meta = null;
                floors = new List<FloorUpload>();
                return false;
            }
        }

        /// <summary>
        /// Rewrites the meta to describe the pictures the UPLOAD will send rather than the ones on
        /// disk: every floor's file name and pixel size, and the scale the two imply.
        ///
        /// ALL of it before the first post, which is the whole reason this is not done inside
        /// <see cref="Encode"/> as each floor is made. The meta travels with every post
        /// (<see cref="MapUploadRequest.Meta"/>) and the host validates EVERY floor in it against the
        /// meta's own pxPerMetre, so a meta whose scale had been rescaled for floor one while floors
        /// two and three still carried the capture's full-size dimensions describes no single
        /// projection. The host refuses the first post of every multi-floor map with "floor 'Ground'
        /// is 2360x2040 px, but its extent at 1.736 px/m is 2048x1771 px", and a rejection stops the
        /// upload - so nothing of that map is ever shared. Customs at the default 4096 setting is
        /// exactly that case.
        ///
        /// One size for every floor, taken from the first floor that declares a usable one: the
        /// floors of one capture are rendered to one rectangle at one scale and are therefore the
        /// same size. A picture that turns out not to be is dropped by <see cref="Encode"/>, which
        /// checks what it actually produced against what this promised, rather than described wrongly
        /// here.
        ///
        /// False, with a line, when the capture's meta declares no usable picture size at all -
        /// nothing here can be said about a set like that, and MapCatalog.CheckPictureSize is already
        /// warning about it on this machine.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta being sent, rewritten in place.</param>
        /// <param name="floors">The floors to be posted, in level order.</param>
        private static bool DescribeWire(string key, MapCaptureMetaDto meta, List<FloorUpload> floors)
        {
            var width = 0;
            var height = 0;

            foreach (var floor in floors)
            {
                if (floor.Entry == null || floor.Entry.Width <= 0 || floor.Entry.Height <= 0) continue;

                ScaleTo(floor.Entry.Width, floor.Entry.Height, MaxLongSide, out width, out height);
                break;
            }

            if (width <= 0 || height <= 0)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the capture of {key} does not say how large its pictures are, so nothing about " +
                    "it could be described to the host - it is not offered.");
                return false;
            }

            // The meta is REBUILT from the floors that are going to be offered, not merely edited in
            // place: ReadCapture leaves a floor whose picture is missing from the capture folder in
            // meta.Floors while refusing to post it, and such a floor would still be measured at the
            // capture's own size (failing the host's check on every post) and still be waited for
            // (the host completes a set when every floor its meta names has arrived).
            var offered = new List<MapCaptureFloorDto>(floors.Count);

            foreach (var floor in floors)
            {
                if (floor.Entry == null) continue;

                // The client's own name for the picture going up. The host discards it and names the
                // file itself, but the meta it stores is this one, and a meta naming a .png beside a
                // JPEG is the disagreement MapCatalog.CheckPictureSize exists to catch.
                floor.Entry.File = $"{key}-{floor.Level.ToString(CultureInfo.InvariantCulture)}.jpg";
                floor.Entry.Width = width;
                floor.Entry.Height = height;

                offered.Add(floor.Entry);
            }

            meta.Floors = offered;

            // Derived from the side ScaleTo PINNED - the longer one - and not always from the width,
            // so the three numbers the host compares (the extent, the scale and the pixel size)
            // describe one projection whatever shape the map is.
            //
            // Why the pinned side is the only honest one. ScaleTo sets the long side to MaxLongSide
            // exactly and ROUNDS the short one, so only the long side's scale is exact. Taking the
            // scale off a rounded short side multiplies that half-pixel by the aspect ratio when the
            // other axis is computed back from it: on a portrait extent the declared height and
            // ceil(heightM x pxPerMetre) then part company by about half the aspect ratio in pixels,
            // which is past the two pixels BOTH MapStore.MetaIsUsable (PixelTolerance) and
            // MapCatalog.CheckPictureSize allow - and the host's answer to that is "rejected", which
            // stops the upload, so the map is never shared at all and the log blames the capture's
            // own meta. Swept against the real ScaleTo over 4,000,000 extents from 20 to 3020 m a
            // side: from the width, 13 px at worst and past the tolerance in 24 % of the aspect
            // space; from the pinned side, 2 px at worst and never past it. Identical arithmetic for
            // a landscape extent, which is what every vanilla map is, so no map that uploads today
            // is described differently tomorrow.
            var metresX = meta.Extent != null ? meta.Extent.MaxX - meta.Extent.MinX : 0d;
            var metresZ = meta.Extent != null ? meta.Extent.MaxZ - meta.Extent.MinZ : 0d;

            if (height >= width)
            {
                if (metresZ > 0d) meta.PxPerMetre = (float)(height / metresZ);
            }
            else if (metresX > 0d)
            {
                meta.PxPerMetre = (float)(width / metresX);
            }

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {key} is offered as {width}x{height} px at {meta.PxPerMetre:0.###} px/m, " +
                $"{floors.Count} floor(s).");

            return true;
        }

        /// <summary>
        /// The capture's side pictures that are actually on this disk, ready to post, in
        /// <see cref="SideDirs"/> order. A side the meta names whose picture is missing, whose name is
        /// not a bare file name, or whose direction is not one of the four is taken OUT of the meta
        /// here, before the first post - a host told about a side waits for it, and this machine cannot
        /// send one it does not have. Never throws; on any failure the capture goes up without sides.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta being offered; its <c>Sides</c> is trimmed to what will be sent.</param>
        private static List<FloorUpload> ReadSides(string key, MapCaptureMetaDto meta)
        {
            var sides = new List<FloorUpload>();

            try
            {
                if (meta?.Sides == null || meta.Sides.Count == 0)
                {
                    if (meta != null) meta.Sides = null;
                    return sides;
                }

                var dir = CaptureDir(key);
                var kept = new List<MapCaptureSideDto>();

                foreach (var dirName in SideDirs)
                {
                    var side = meta.Sides.Find(sd => sd != null &&
                                                     string.Equals(sd.Dir, dirName, StringComparison.OrdinalIgnoreCase));

                    if (side == null || dir == null || string.IsNullOrEmpty(side.File)) continue;
                    if (!string.Equals(side.File, Path.GetFileName(side.File), StringComparison.Ordinal)) continue;

                    var picture = Path.Combine(dir, side.File);
                    if (!File.Exists(picture)) continue;

                    side.Dir = dirName;
                    kept.Add(side);

                    sides.Add(new FloorUpload
                    {
                        Level = SideLevel,
                        Name = $"{dirName} side",
                        Path = picture,
                        Side = dirName,
                        SideEntry = side
                    });
                }

                meta.Sides = kept.Count == 0 ? null : kept;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the side pictures of {key} could not be read back for upload ({ex.Message}) - " +
                    "the capture goes up without them.");

                meta.Sides = null;
                sides.Clear();
            }

            return sides;
        }

        /// <summary>
        /// Rewrites each side's entry for the size it goes up at - width, height and pxPerMetre - as
        /// <see cref="DescribeWire"/> does for the floors, and for the same reason: the host checks the
        /// three against each other (MapStore.SideProblem), and a side described at the capture's size
        /// but sent at 2048 would be dropped there.
        ///
        /// Per side rather than once for all of them, because the four are not the same shape: the N and
        /// S views span the map's width, the E and W views its depth. The scale comes off the side
        /// ScaleTo PINS, as for the floors, and the span it is divided by is the capture box projected on
        /// the side's own axes - the box's eight corners, the meta's extent by the side's height range -
        /// which is the same arithmetic the host checks with, so the two cannot disagree by more than the
        /// rounding of the short side. The basis and the origins are distances in the world and do not
        /// change with the picture's size.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta being offered, for its extent.</param>
        /// <param name="sides">The sides to be posted.</param>
        private static void DescribeSidesForWire(string key, MapCaptureMetaDto meta, List<FloorUpload> sides)
        {
            if (meta?.Extent == null) return;

            foreach (var upload in sides)
            {
                var side = upload.SideEntry;

                if (side == null || side.Width <= 0 || side.Height <= 0) continue;

                // The short side CEILED, as the encoder will produce it - see ScaleTo.
                ScaleTo(side.Width, side.Height, MaxLongSide, out var width, out var height, ceilShort: true);

                if (width == side.Width && height == side.Height) continue;

                if (!SideSpans(meta.Extent, side, out var spanR, out var spanU)) continue;

                side.PxPerMetre = height >= width
                    ? (float)(height / spanU)
                    : (float)(width / spanR);

                side.Width = width;
                side.Height = height;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {key}'s {upload.Side} side is offered as {width}x{height} px at " +
                    $"{side.PxPerMetre:0.###} px/m.");
            }
        }

        /// <summary>The capture box - the extent by the side's height range - projected on a side's right
        /// and up axes: how many metres the picture spans each way. False when the basis is unusable, and
        /// then the side is left as it is for the host to judge.</summary>
        private static bool SideSpans(MapRectDto extent, MapCaptureSideDto side, out double spanR, out double spanU)
        {
            spanR = spanU = 0d;

            if (side.Right == null || side.Right.Length != 3 || side.Up == null || side.Up.Length != 3) return false;

            double minR = double.MaxValue, maxR = double.MinValue, minU = double.MaxValue, maxU = double.MinValue;

            foreach (var x in new[] { extent.MinX, extent.MaxX })
            foreach (var y in new[] { (double)side.YMin, side.YMax })
            foreach (var z in new[] { extent.MinZ, extent.MaxZ })
            {
                var r = x * side.Right[0] + y * side.Right[1] + z * side.Right[2];
                var u = x * side.Up[0] + y * side.Up[1] + z * side.Up[2];

                minR = Math.Min(minR, r);
                maxR = Math.Max(maxR, r);
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
            }

            spanR = maxR - minR;
            spanU = maxU - minU;

            return spanR > 0d && spanU > 0d;
        }

        /// <summary>
        /// The capture's atlas pages that are actually on this disk, ready to post, in page order - or none,
        /// with the meta's atlas removed, when there is no mesh to offer (<paramref name="hasMesh"/>): a
        /// page textures the mesh's buildings and nothing else, and a host told about a page waits for it.
        /// A page the meta names whose picture is missing, whose name is not a bare file name, whose number
        /// is not 0..7 or named twice, or which is not a picture up to <see cref="MaxAtlasPixels"/> a side
        /// is taken out of the meta here, before the first post, for <see cref="ReadSides"/>' reason. Its
        /// width and height are NOT rewritten, unlike a side's: a page goes up at its own size. Never throws;
        /// on any failure the capture goes up without pages.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta being offered; its <c>Atlas</c> is trimmed to what will be sent.</param>
        /// <param name="hasMesh">Whether a mesh will be offered with it.</param>
        private static List<FloorUpload> ReadAtlas(string key, MapCaptureMetaDto meta, bool hasMesh)
        {
            var pages = new List<FloorUpload>();

            try
            {
                if (meta?.Atlas == null || meta.Atlas.Count == 0 || !hasMesh)
                {
                    if (meta != null) meta.Atlas = null;
                    return pages;
                }

                var dir = CaptureDir(key);
                var kept = new List<MapCaptureAtlasDto>();

                foreach (var page in meta.Atlas.Where(p => p != null).OrderBy(p => p.Page))
                {
                    if (dir == null || page.Page < 0 || page.Page >= MaxAtlasPages) continue;
                    if (kept.Any(k => k.Page == page.Page)) continue;
                    if (page.Width <= 0 || page.Height <= 0 || page.Width > MaxAtlasPixels || page.Height > MaxAtlasPixels) continue;
                    if (string.IsNullOrEmpty(page.File)) continue;
                    if (!string.Equals(page.File, Path.GetFileName(page.File), StringComparison.Ordinal)) continue;

                    var picture = Path.Combine(dir, page.File);
                    if (!File.Exists(picture)) continue;

                    kept.Add(page);

                    pages.Add(new FloorUpload
                    {
                        Level = SideLevel,
                        Name = $"atlas page {page.Page.ToString(CultureInfo.InvariantCulture)}",
                        Path = picture,
                        Atlas = page.Page,
                        AtlasEntry = page
                    });
                }

                if (kept.Count < meta.Atlas.Count)
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {meta.Atlas.Count - kept.Count} of {key}'s {meta.Atlas.Count} atlas page(s) are " +
                        "not on this disk as the meta describes them - the capture goes up without those.");

                meta.Atlas = kept.Count == 0 ? null : kept;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the atlas pages of {key} could not be read back for upload ({ex.Message}) - " +
                    "the capture goes up without them.");

                meta.Atlas = null;
                pages.Clear();
            }

            return pages;
        }

        /// <summary>What the host's answer to one side post means for the rest of the upload.</summary>
        internal enum SideVerdict
        {
            /// <summary>Taken or dropped; send the next side.</summary>
            Continue,

            /// <summary>The side was the last piece and the host has the whole set.</summary>
            Complete,

            /// <summary>This host does not take sides at all. Stop sending them; the mesh still goes.</summary>
            NoSides,

            /// <summary>Send nothing more. The reason has been logged.</summary>
            Stop,

            /// <summary>This host does not take uploads at all - <see cref="JudgeSide"/> turns it into
            /// <see cref="Stop"/> after saying so and remembering it for the session.</summary>
            Declined
        }

        /// <summary>
        /// The host's answer to one side post, and at most one line about it.
        ///
        /// A host that knows sides never refuses a post over its SIDE - it drops the side, says so in the
        /// reason (logged here at Info), and answers as usual. So a refusal of a side post means one of two
        /// different things, and <see cref="ClassifySide"/> tells them apart by the host's code (review F02), or by its
        /// own words from a host too old to send one:
        ///
        /// - the level refusal ("level -2147483648 is not one of the N floors the meta names") is a host
        ///   from before sides, reading the post as a floor of a level no floor has (see SideLevel). Debug,
        ///   and the sides stop but the mesh still goes - such a host may be waiting for one;
        /// - ANY other refusal is a real one - the capture, not the side - and it stops the upload exactly
        ///   as it would have stopped at a floor, with one Warning. Treating it as "an old host" was the
        ///   first version's mistake: the client then posted the whole mesh into a set the host would never
        ///   complete.
        /// </summary>
        private static SideVerdict JudgeSide(
            string key, FloorUpload side, Task<string> task, out string reason, out bool? meshKept)
        {
            reason = "";
            meshKept = null;

            string reply;

            try
            {
                reply = task.Result;
            }
            catch (Exception ex)
            {
                var message = ex is AggregateException aggregate && aggregate.InnerException != null
                    ? aggregate.InnerException.Message
                    : ex.Message;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the host could not be offered {key}'s {What(side)} ({message}) - the rest of the " +
                    "capture is not sent.");
                return SideVerdict.Stop;
            }

            var response = NotOurs<MapUploadResponse>(reply, out var excerpt);
            var verdict = ClassifySide(response);

            reason = response?.Reason ?? "";
            meshKept = response?.MeshKept;

            switch (verdict)
            {
                case SideVerdict.NoSides:
                    Plugin.LogSource?.LogDebug(
                        response == null || string.IsNullOrEmpty(response.Outcome)
                            ? $"QuestTree: the reply to {key}'s {What(side)} was not the server half's - {excerpt}"
                            : $"QuestTree: the host did not take {key}'s {What(side)}{Because(response.Reason)} - it " +
                              $"predates {(side.Atlas != null ? "atlas pages" : "side pictures")}, so the rest of the " +
                              "capture goes up without them.");
                    return verdict;

                case SideVerdict.Declined:
                    _uploadsDeclined = true;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: this host does not accept map pictures{Because(response.Reason)} - captures " +
                        "stay on this machine, and nothing more is offered this session.");
                    return SideVerdict.Stop;

                case SideVerdict.Stop:
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the host refused the capture of {key} at its {What(side)}" +
                        $"{Because(response.Reason)} - the rest of it is not sent.");
                    return verdict;

                default:
                    // The host dropped this side or page and went on - the one thing about either worth a
                    // line. Its own field when it sent one (review F02); from a host before the field, its
                    // words: "the E side was dropped (...)", "atlas page 3 was dropped (...)".
                    var dropped = response.Dropped ??
                                  (response.Reason != null &&
                                   response.Reason.IndexOf(side.Atlas != null ? "page " : "side was dropped",
                                       StringComparison.OrdinalIgnoreCase) >= 0 &&
                                   response.Reason.IndexOf("was dropped", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (dropped)
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: the host went on without {key}'s {What(side)}{Because(response.Reason)}.");

                    return verdict;
            }
        }

        /// <summary>A side or page upload's name for a log line: "E side picture", "atlas page 3".</summary>
        private static string What(FloorUpload upload) =>
            upload.Atlas != null ? upload.Name : $"{upload.Side} side picture";

        /// <summary>The host's refusal of a floor whose level its meta does not name - the sentence a host
        /// from before sides answers a side post with, because it reads the post as a floor of SideLevel.
        /// MapStore.Accept's own words; the one refusal of a side post that is not about the capture.</summary>
        private const string LevelRefusal = "is not one of";

        /// <summary>
        /// What one side post's answer means, with no logging and no Unity - the decision JudgeSide acts
        /// on, kept pure so it can be checked from outside the game (the Stage U client harness loads this
        /// assembly and calls it). Null or outcome-less is not the server half's reply: an older host with
        /// no map routes at all.
        /// </summary>
        internal static SideVerdict ClassifySide(MapUploadResponse response)
        {
            if (response == null || string.IsNullOrEmpty(response.Outcome)) return SideVerdict.NoSides;

            switch (response.Outcome.Trim().ToLowerInvariant())
            {
                case "stored":
                    return SideVerdict.Continue;

                case "complete":
                    return SideVerdict.Complete;

                case "declined":
                    return SideVerdict.Declined;

                case "rejected":
                    // The code when the host sends one (review F02); only a host from before codes gets its
                    // words read. In practice only a host from before SIDES gives the level refusal - a current
                    // host routes a side post to its side branch before it looks at levels - so the text path
                    // stays for as long as 1.19 test-build hosts matter.
                    if (response.Code != null)
                        return response.Code == MapUploadResponse.CodeUnknownLevel ? SideVerdict.NoSides : SideVerdict.Stop;

                    return (response.Reason ?? "").IndexOf(LevelRefusal, StringComparison.OrdinalIgnoreCase) >= 0
                        ? SideVerdict.NoSides
                        : SideVerdict.Stop;

                default:
                    // An outcome this build does not know is not evidence of an OLD host - an old one has
                    // the four outcomes - so it stops, as an unknown answer to a floor does.
                    return SideVerdict.Stop;
            }
        }

        /// <summary>Flattens a picture's transparency onto <see cref="BackdropFill"/>, in place, so what
        /// a host receives looks like what the capturing player sees. Straight source-over: the colour is
        /// already premultiplied by nothing, so it is c*a + fill*(1-a), and every pixel comes out opaque.
        ///
        /// Called on the FULL-SIZE picture, before the downscale - see <see cref="Encode"/> for why the
        /// order is load-bearing and what the full-size pass costs.
        ///
        /// Guarded like everything else on this path: a picture that cannot be read back is sent as it is
        /// rather than not sent, because a JPEG with a black skirt is a worse picture and no picture is a
        /// worse map.</summary>
        /// <param name="picture">The texture to flatten, which is changed in place and applied.</param>
        private static void Composite(Texture2D picture)
        {
            try
            {
                var pixels = picture.GetPixels32();
                var flattened = 0;

                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    if (pixel.a == 255) continue;

                    flattened++;

                    if (pixel.a == 0)
                    {
                        pixels[i] = BackdropFill;
                        continue;
                    }

                    var alpha = pixel.a;
                    var rest = 255 - alpha;

                    pixels[i] = new Color32(
                        (byte)((pixel.r * alpha + BackdropFill.r * rest) / 255),
                        (byte)((pixel.g * alpha + BackdropFill.g * rest) / 255),
                        (byte)((pixel.b * alpha + BackdropFill.b * rest) / 255),
                        255);
                }

                if (flattened == 0) return;

                picture.SetPixels32(pixels);
                picture.Apply(updateMipmaps: false);

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {flattened} of {pixels.Length} pixel(s) of this floor were transparent and are " +
                    "flattened onto the map backdrop for the host copy.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: a floor's transparency could not be flattened ({ex.GetType().Name}: " +
                    $"{ex.Message}) - it is offered as it is.");
            }
        }

        /// <summary>
        /// One floor's picture, scaled to fit <see cref="MaxLongSide"/> and encoded as a JPEG, as
        /// base64 on the floor. MAIN THREAD - every line of it is Texture2D work.
        ///
        /// What it produced is CHECKED against what <see cref="DescribeWire"/> already told the meta
        /// this floor would be, and a floor that does not match is dropped rather than sent: the meta
        /// is shared by every post and cannot be bent to one floor - see DescribeWire.
        ///
        /// False, with a line, when this floor cannot be sent; the others still go.
        /// </summary>
        /// <param name="key">The map's internal id, for the log lines.</param>
        /// <param name="floor">The floor to encode.</param>
        private static bool Encode(string key, FloorUpload floor)
        {
            Texture2D source = null;
            Texture2D scaled = null;
            RenderTexture render = null;
            var previous = RenderTexture.active;

            try
            {
                var bytes = File.ReadAllBytes(floor.Path);

                // The frame size from the header BEFORE the decode (review F49), as the Maps tab and the 3D view
                // already check it: this decode is READABLE - twice the memory - on the main thread, so a small, very
                // compressible file hand-copied into the capture folder must not become a gigabyte in one frame. A
                // page is the builder's size; a floor or side at most a capture's own ceiling.
                var sideLimit = floor.Atlas != null ? MaxAtlasPixels : QuestTree.UI.DynamicMapsLibrary.MaxPictureSide;

                if (!QuestTree.UI.DynamicMapsLibrary.PictureSize(bytes, out var declaredWidth, out var declaredHeight) ||
                    declaredWidth > sideLimit || declaredHeight > sideLimit)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" is not a PNG or JPEG of at most {sideLimit} px a side " +
                        $"({declaredWidth}x{declaredHeight}) and is not offered to the host.");
                    return false;
                }

                // RGBA, not RGB: the picture on disk carries the walkable mask in its alpha and the
                // composite below needs it. LoadImage reformats to suit the PNG anyway; asking for RGBA
                // is what stops the alpha being dropped on the way in.
                source = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);

                // Readable on purpose (no markNonReadable): EncodeToJPG and the orientation check
                // below both read pixels back.
                if (!source.LoadImage(bytes))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" could not be decoded from disk " +
                        $"({bytes.Length} bytes) and is not offered to the host.");
                    return false;
                }

                // A side is scaled with its short side ceiled, exactly as DescribeSidesForWire described
                // it; a floor with the rounding it has always had. The two MUST match, or the size check
                // below drops every rescaled side.
                //
                // An ATLAS page is not scaled at all - see MaxAtlasPixels - so it comes out at the size its
                // meta names or not at all.
                int width, height;

                if (floor.Atlas != null)
                {
                    width = source.width;
                    height = source.height;
                }
                else
                {
                    ScaleTo(source.width, source.height, MaxLongSide, out width, out height,
                        ceilShort: floor.Side != null);
                }

                // The alpha flattened onto the tab's own backdrop BEFORE the downscale, because a JPEG
                // has none - see BackdropFill.
                //
                // Before and not after, which is where this used to be: Graphics.Blit filters RGBA
                // straight, un-premultiplied, so along the cut-out edge it averaged the colour of opaque
                // ground with the colour BEHIND a transparent pixel - and where nothing was drawn at all
                // (a streamed-out chunk, the world outside the playable area) the capture leaves that
                // colour black. The alpha the filter produced was a clean ramp and the colour was a dark
                // fringe, and compositing afterwards then kept that fringe and only removed the ramp:
                // every capture offered to a host had a dark outline around the playable area and around
                // every hole in it. Flattened first, there is no transparency left for the filter to
                // average against and every pixel it mixes is a colour somebody can see.
                //
                // The price is that this runs on the FULL-SIZE picture: GetPixels32 of a 4472x2156
                // Customs floor is a 38 MB managed array (32 MB for a 4096x2048 one), allocated for the
                // length of the flatten and gone before the blit. Paid on an upload of a finished
                // capture, outside a raid frame, and it is the same array this method already allocates
                // for the encode further down.
                Composite(source);

                var encodeFrom = source;

                if (width != source.width || height != source.height)
                {
                    render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);

                    // The GPU does the filtering. The alternative - GetPixels32 and a bilinear loop -
                    // is the same 38 MB of managed array the flatten above pays for, plus a few hundred
                    // milliseconds on the thread drawing frames, for the same picture.
                    Graphics.Blit(source, render);

                    RenderTexture.active = render;

                    scaled = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
                    scaled.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                    scaled.Apply(updateMipmaps: false);

                    encodeFrom = scaled;

                    CheckOrientation(key, floor, source, scaled);
                }

                // The check that can fail, and the one that keeps the shared meta honest: every post
                // carries the same meta, whose floors DescribeWire has already measured, so a picture
                // that came out a different size cannot be described - it can only be left out.
                if (encodeFrom.width != floor.WantWidth || encodeFrom.height != floor.WantHeight)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" came out {encodeFrom.width}x{encodeFrom.height} px " +
                        $"where its meta says {floor.WantWidth}x{floor.WantHeight} - the capture's meta does " +
                        (floor.Atlas != null
                            ? "not describe its own atlas page, so the host is told to go on without this page."
                            : floor.Side != null
                            ? "not describe its own side picture, so the host is told to go on without this side."
                            : "not describe its own pictures, so this floor is not offered. Capture the map again."));
                    return false;
                }

                var quality = floor.Atlas != null ? AtlasJpegQuality : JpegQuality;
                var jpg = encodeFrom.EncodeToJPG(quality);

                // A page over its cap gets ONE more try, at the lower quality - see AtlasJpegQuality.
                if (floor.Atlas != null && jpg != null && jpg.Length > MaxAtlasPageBytes)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: {key} \"{floor.Name}\" is {Mb(jpg.Length)} MB at q{quality}, over the " +
                        $"{Mb(MaxAtlasPageBytes)} MB a page may be - encoded again at q{AtlasRetryJpegQuality}.");

                    quality = AtlasRetryJpegQuality;
                    jpg = encodeFrom.EncodeToJPG(quality);
                }

                if (jpg == null || jpg.Length == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" encoded to nothing and is not offered to the host.");
                    return false;
                }

                // A page's own cap - see MaxAtlasPageBytes - and a floor's (and a side's) otherwise.
                var cap = floor.Atlas != null ? MaxAtlasPageBytes : MaxFloorUploadBytes;

                if (jpg.Length > cap)
                {
                    // Skipped rather than sent: the host would reject it, and a rejection stops the
                    // whole upload - see MaxFloorUploadBytes. (A side or page skipped here is still posted
                    // empty by the caller, which is how the host is told to stop waiting for it.)
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {key} \"{floor.Name}\" is {Mb(jpg.Length)} MB as a JPEG, over the " +
                        $"{Mb(cap)} MB a host takes per {(floor.Atlas != null ? "atlas page" : "picture")} - it is " +
                        "not offered. The rest of the capture still is.");
                    return false;
                }

                floor.Base64 = Convert.ToBase64String(jpg);
                floor.Bytes = jpg.Length;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {key} \"{floor.Name}\" {source.width}x{source.height} -> " +
                    $"{encodeFrom.width}x{encodeFrom.height} JPEG q{quality}, {Mb(jpg.Length)} MB.");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {key} \"{floor.Name}\" could not be prepared for upload " +
                    $"({ex.GetType().Name}: {ex.Message}) - it is not offered.");
                return false;
            }
            finally
            {
                // Native allocations, every one of them, and a 4096-side floor is 50 MB.
                RenderTexture.active = previous;
                if (render != null) RenderTexture.ReleaseTemporary(render);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
                if (source != null) UnityEngine.Object.Destroy(source);
            }
        }

        /// <summary>
        /// Says so when the scaled copy came out upside down.
        ///
        /// A check that can fail, and the one thing about the downscale that cannot be proven by
        /// arithmetic: whether a blit into a RenderTexture and a ReadPixels back out of it agree
        /// about which end of the picture is the top depends on the graphics API, and a vertically
        /// flipped map is a picture that looks perfectly fine and puts every pin on the wrong side of
        /// the map. Sixteen bilinear samples, so it costs nothing per floor.
        ///
        /// Judged only when the picture has a top and a bottom that differ: a map that is uniformly
        /// dark carries no evidence either way, and a guess there would be a warning about nothing.
        /// Warned, never corrected - a correction based on a coincidence would be worse than a line
        /// asking for the picture to be looked at.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="floor">The floor being scaled, for the line.</param>
        /// <param name="source">The picture as it came off disk.</param>
        /// <param name="scaled">The scaled copy.</param>
        private static void CheckOrientation(string key, FloorUpload floor, Texture2D source, Texture2D scaled)
        {
            try
            {
                // Four rows of four samples, away from the very edges where a capture is often
                // background on both sides.
                var us = new[] { 0.2f, 0.4f, 0.6f, 0.8f };
                var vs = new[] { 0.125f, 0.375f, 0.625f, 0.875f };

                var same = 0f;
                var flipped = 0f;
                var spread = 0f;

                var rows = new float[vs.Length];

                for (var r = 0; r < vs.Length; r++)
                {
                    var row = 0f;
                    for (var c = 0; c < us.Length; c++) row += Luminance(source.GetPixelBilinear(us[c], vs[r]));
                    rows[r] = row / us.Length;
                }

                for (var r = 0; r < vs.Length; r++)
                {
                    var row = 0f;
                    for (var c = 0; c < us.Length; c++) row += Luminance(scaled.GetPixelBilinear(us[c], vs[r]));
                    row /= us.Length;

                    same += Math.Abs(row - rows[r]);
                    flipped += Math.Abs(row - rows[vs.Length - 1 - r]);
                }

                for (var r = 0; r < rows.Length; r++)
                for (var s = r + 1; s < rows.Length; s++)
                    spread = Math.Max(spread, Math.Abs(rows[r] - rows[s]));

                // No usable evidence: the rows of this picture are all much the same brightness, so
                // both orders fit and neither proves anything.
                if (spread < 0.06f) return;

                // A clear margin, not a hair: bilinear sampling of two different scales never agrees
                // exactly, and the wrong order has to fit markedly BETTER to be believed.
                if (flipped * 1.5f >= same) return;

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the scaled copy of {key} \"{floor.Name}\" looks vertically flipped " +
                    $"(rows fit the reversed order {same / Math.Max(flipped, 0.0001f):0.0}x better) - it is " +
                    "offered to the host anyway, but the host's copy of this map should be checked.");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the orientation of {key} \"{floor.Name}\" could not be checked ({ex.Message}).");
            }
        }

        private static float Luminance(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

        /// <summary>
        /// The size a picture is scaled to so that its long side is at most
        /// <paramref name="maxLongSide"/>, keeping the aspect ratio.
        ///
        /// The long side is set to the cap EXACTLY rather than computed, so it can never come out one
        /// pixel over; the short side is the only rounded quantity. Worked examples, which
        /// tools/scale-check (see the report for stage C) runs against this method's own text:
        ///
        ///   2360x2040, cap 2048: 2360 &gt; 2048, wide, so width = 2048 and
        ///   height = round(2040 * 2048 / 2360) = round(1770.34) = 1770 -> 2048x1770.
        ///   The aspect is 1.15686 against the original's 1.15686.
        ///
        ///   1500x1200, cap 2048: the long side is already under the cap, so nothing is touched and
        ///   the picture is encoded as it is - 1500x1200.
        ///
        /// A degenerate size (zero or negative, which a broken decode could report) is handed back
        /// unchanged for the caller's own guards to refuse.
        /// </summary>
        /// <param name="width">The picture's width in pixels.</param>
        /// <param name="height">The picture's height in pixels.</param>
        /// <param name="maxLongSide">The most either side may be.</param>
        /// <param name="scaledWidth">The width to scale to.</param>
        /// <param name="scaledHeight">The height to scale to.</param>
        /// <param name="ceilShort">True for a SIDE picture: the short side is rounded UP rather than to the
        /// nearest pixel. The host checks a side's size against ceil(span x pxPerMetre) - a ceil, as the
        /// capture itself sizes the picture - and a side's span is in a basis with irrational components,
        /// so its rendered size is already a ceil of a non-integer; rounding the scaled short side to the
        /// nearest pixel then lands on the wrong side of the host's ceil half the time. Floors keep the
        /// rounding they have always had (their sweep is in DescribeWire), so no floor that uploads today
        /// is described differently tomorrow.</param>
        internal static void ScaleTo(
            int width, int height, int maxLongSide, out int scaledWidth, out int scaledHeight, bool ceilShort = false)
        {
            scaledWidth = width;
            scaledHeight = height;

            if (width <= 0 || height <= 0 || maxLongSide <= 0) return;
            if (width <= maxLongSide && height <= maxLongSide) return;

            double Short(double value) => ceilShort
                ? Math.Ceiling(value - 1e-9)
                : Math.Round(value, MidpointRounding.AwayFromZero);

            if (width >= height)
            {
                scaledWidth = maxLongSide;
                scaledHeight = Math.Max(1, (int)Short(height * (double)maxLongSide / width));
            }
            else
            {
                scaledHeight = maxLongSide;
                scaledWidth = Math.Max(1, (int)Short(width * (double)maxLongSide / height));
            }
        }

        /// <summary>The post of one floor, issued on a pool thread, or null when the pool would not
        /// take it. The serialisation goes on the worker too: the body is a megabyte of base64 and
        /// building it is a frame.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta to send with this floor - see MapUploadRequest.Meta.</param>
        /// <param name="floor">The floor whose picture is ready.</param>
        /// <param name="deadline">When the request is aborted - <see cref="RequestTimeout"/> for a picture,
        /// <see cref="MeshRequestTimeout"/> for an atlas page (review F57).</param>
        private static Task<string> StartPost(string key, MapCaptureMetaDto meta, FloorUpload floor, TimeSpan deadline)
        {
            var request = new MapUploadRequest
            {
                SchemaVersion = MapUploadRequest.CurrentSchemaVersion,
                Map = key,
                ClientVersion = ModInfo.Version,
                Meta = meta,

                // A side carries a level no floor has - see SideLevel - and its direction; an atlas page
                // the same level, and its number.
                Level = floor.Side != null || floor.Atlas != null ? SideLevel : floor.Level,
                Side = floor.Side,
                Atlas = floor.Atlas,
                Format = "jpg",
                ImageBase64 = floor.Base64 ?? ""
            };

            try
            {
                // The meta object is shared by every post of this capture and is rewritten between
                // them (see Encode), which is safe for exactly one reason: the coroutine waits for
                // each post to finish before the next floor is touched, so no worker is ever reading
                // it while the main thread writes it.
                return Task.Run(() => Send(UploadRoute, JsonConvert.SerializeObject(request), deadline));
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the upload of {key} \"{floor.Name}\" could not be started ({ex.Message}).");
                return null;
            }
        }

        /// <summary>What the host said about one floor, turned into what to do next and a log line.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="floor">The floor that was posted.</param>
        /// <param name="task">The finished post.</param>
        /// <param name="floorsHeld">How many floors of this map the host says it now holds.</param>
        /// <param name="reason">The host's reason text, for the caller's log line ("" when none).</param>
        /// <param name="meshKept">On a completing answer, whether the host says it kept the declared mesh;
        /// null when it did not say (review F02).</param>
        private static Verdict Judge(
            string key, FloorUpload floor, Task<string> task, out int floorsHeld, out string reason,
            out bool? meshKept)
        {
            floorsHeld = 0;
            reason = "";
            meshKept = null;

            string reply;

            try
            {
                reply = task.Result;
            }
            catch (Exception ex)
            {
                var message = ex is AggregateException aggregate && aggregate.InnerException != null
                    ? aggregate.InnerException.Message
                    : ex.Message;

                // No server half, no connection, a route that faulted: the capture is on this
                // machine and nothing is lost. Info, not Warning - the server half is optional.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the host could not be offered {key} \"{floor.Name}\" ({message}).");
                return Verdict.Stop;
            }

            var response = NotOurs<MapUploadResponse>(reply, out var excerpt);

            if (response == null || string.IsNullOrEmpty(response.Outcome))
            {
                // An older host answers an unregistered route with SPT's own HTML. Debug, because
                // this is the normal state of every host that has not been updated.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the reply to the upload of {key} was not the server half's - {excerpt}");
                return Verdict.Stop;
            }

            floorsHeld = response.FloorsHeld;
            reason = response.Reason ?? "";
            meshKept = response.MeshKept;

            switch (response.Outcome.Trim().ToLowerInvariant())
            {
                case "stored":
                    return Verdict.Continue;

                case "complete":
                    return Verdict.Complete;

                case "declined":
                    // Once per session, and then nothing - see _uploadsDeclined.
                    _uploadsDeclined = true;

                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: this host does not accept map pictures{Because(response.Reason)} - captures " +
                        "stay on this machine, and nothing more is offered this session.");
                    return Verdict.Stop;

                case "rejected":
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the host refused the capture of {key}{Because(response.Reason)} - the rest of " +
                        "it is not sent.");
                    return Verdict.Stop;

                default:
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: the host answered the upload of {key} with \"{response.Outcome}\", which this " +
                        "build does not know - nothing more is sent.");
                    return Verdict.Stop;
            }
        }

        /// <summary>The one line a finished upload writes.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="floors">How many floors the host has of it.</param>
        /// <param name="bytes">What the pictures weighed.</param>
        /// <param name="meshBytes">What the mesh weighed, or 0 when none was sent.</param>
        /// <param name="clock">Running since the upload started.</param>
        /// <param name="sides">How many side pictures went up with it.</param>
        /// <param name="pages">How many atlas pages went up with it.</param>
        private static void Done(
            string key, int floors, long bytes, long meshBytes, System.Diagnostics.Stopwatch clock, int sides = 0,
            int pages = 0)
        {
            Plugin.LogSource?.LogInfo(
                $"QuestTree: capture of {key} uploaded to the host - {floors} floor(s)" +
                $"{(sides > 0 ? $", {sides} side(s)" : "")}" +
                $"{(pages > 0 ? $", {pages} atlas page(s)" : "")}" +
                $"{(meshBytes > 0 ? $" and a {Mb(meshBytes)} MB mesh" : "")}, {Mb(bytes + meshBytes)} MB.");

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {key} was uploaded in {clock.ElapsedMilliseconds} ms.");
        }

        /// <summary>
        /// The mesh file this capture's meta names, read off this disk and CHECKED against what the meta
        /// says it is - or null, with the meta's mesh block stripped, when there is nothing to offer.
        ///
        /// Stripping is the load-bearing half. A host that sees a mesh block holds the whole set until
        /// the file arrives (MapStore's mesh route), so offering a meta whose mesh cannot be sent does
        /// not cost the mesh - it costs the CAPTURE, which sits staged on the host until it expires a
        /// day later while every log line here says the floors went up. So every reason this can fail
        /// ends the same way: the block comes out of the meta before the first post, and the set is
        /// offered as the flat picture set it effectively is.
        ///
        /// The sha256 is the check that can fail, and it is not ceremony: the meta and the .bin are two
        /// files written in sequence by a capture that can be interrupted, a merge can leave an older
        /// mesh beside a newer meta, and a hand-copied folder can hold either half. Reading and hashing
        /// is ~30 ms per 48 MB (WP7 meshes run 50-90 MB on a stock map, up to 512 MiB), paid once per upload - on the main thread, in the raid when the capture was taken in one, but a frame after the capture finished (UploadRoutine yields first) and hashed once (review F34).
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta about to be offered. Its mesh block is stripped on any failure.</param>
        private static byte[] PrepareMesh(string key, MapCaptureMetaDto meta)
        {
            var mesh = meta?.Mesh;

            if (meta == null || mesh == null) return null;

            string why;

            try
            {
                var dir = CaptureDir(key);

                if (dir == null || string.IsNullOrEmpty(mesh.File))
                {
                    why = "its meta names no mesh file";
                }
                else if (!string.Equals(mesh.File, Path.GetFileName(mesh.File), StringComparison.Ordinal))
                {
                    // A name in a meta becomes a path. A bare name in the capture's own folder, exactly
                    // as the floors are held to.
                    why = $"its mesh file name '{mesh.File}' is not a plain name in the capture folder";
                }
                else if (!File.Exists(Path.Combine(dir, mesh.File)))
                {
                    why = $"{mesh.File} is not in the capture folder";
                }
                else
                {
                    var bytes = File.ReadAllBytes(Path.Combine(dir, mesh.File));
                    string hash = null;

                    if (bytes.Length == 0)
                    {
                        why = $"{mesh.File} is empty";
                    }
                    else if (bytes.Length > ClientMeshAbsolute)
                    {
                        // No host would take it (the protocol's absolute), and a refusal after the floors have
                        // gone up leaves the set staged there - so it is not offered at all. A host whose own
                        // ceiling is lower drops the mesh at the first post and keeps the pictures.
                        why = $"{mesh.File} is {Mb(bytes.Length)} MB, over the {Mb(ClientMeshAbsolute)} MB any host takes";
                    }
                    else if (!string.Equals(hash = Sha256(bytes), (mesh.Sha256 ?? "").Trim(),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        why = $"{mesh.File} does not hash to the sha256 its meta names, so the two are from " +
                              "different captures";
                    }
                    else
                    {
                        // The hash above, once (review F34: it was computed a second time for this line).
                        Plugin.LogSource?.LogDebug(
                            $"QuestTree: {key}'s mesh is offered as {Mb(bytes.Length)} MB, sha " +
                            $"{hash.Substring(0, 12)}, {mesh.Cells:N0} cell(s) and {mesh.Triangles:N0} " +
                            "triangle(s).");

                        return bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                why = $"its mesh could not be read ({ex.GetType().Name}: {ex.Message})";
            }

            meta.Mesh = null;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: the capture of {key} is offered to the host WITHOUT its 3D mesh - {why}. The pictures " +
                "still go up, and the map draws flat on the other machines until it is captured again.");

            return null;
        }

        /// <summary>How many posts a mesh of this size takes: one up to <see cref="MeshPartBytes"/>, then one
        /// per part of that size. Pure, so the client harness checks it.</summary>
        internal static int MeshPartCount(int bytes) =>
            bytes <= MeshPartBytes ? 1 : (bytes + MeshPartBytes - 1) / MeshPartBytes;

        /// <summary>Whether a part post came back as one the host is holding - "accepted, not served,
        /// holding part k of n" - which is the only answer that lets the next part go. Pure, so the client
        /// harness can hold it to the host's real answer. The host's numbers when it sends them (review F02);
        /// its words only from a host before them.</summary>
        internal static bool IsMeshPartHeld(MapMeshUploadResponse response)
        {
            if (response == null || !response.Accepted || response.Served) return false;

            // A host that says it in numbers (review F02).
            if (response.Parts > 0) return response.PartsHeld > 0 && response.PartsHeld < response.Parts;

            // A 1.19 test-build host from before the numbers: its words.
            return (response.Reason ?? "").StartsWith("holding part", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary><see cref="IsMeshPartHeld"/> over a finished post. False for anything that is not the
        /// server half's reply - an old host's HTML, a failed request - which the caller then hands to
        /// JudgeMesh for its line.</summary>
        private static bool MeshPartHeld(Task<string> task)
        {
            try
            {
                return IsMeshPartHeld(NotOurs<MapMeshUploadResponse>(task.Result, out _));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>One mesh post, issued on a pool thread, or null when the pool would not take it. The
        /// base64 and the serialisation go on the worker: a part is a 21 MB string, and building it on the
        /// main thread is a visible stall.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta whose mesh block this file belongs to.</param>
        /// <param name="bytes">The WHOLE mesh file's bytes, already checked by <see cref="PrepareMesh"/>.</param>
        /// <param name="part">Which part this post carries, from 0.</param>
        /// <param name="parts">How many parts the mesh goes up in; 1 for a mesh in one post.</param>
        private static Task<string> StartMeshPost(string key, MapCaptureMetaDto meta, byte[] bytes, int part, int parts)
        {
            try
            {
                // Everything the worker needs, captured now: nothing it reads may be touched by the
                // main thread while it runs, which is the discipline every request here keeps.
                var capturedAt = meta.CapturedAt;
                var sha = meta.Mesh != null ? meta.Mesh.Sha256 : Sha256(bytes);

                return Task.Run(async () =>
                {
                    // The slice this post carries. The sha and the length stay the WHOLE mesh's: the host
                    // checks the parts against them once it has joined them.
                    var offset = parts > 1 ? part * MeshPartBytes : 0;
                    var length = parts > 1 ? Math.Min(MeshPartBytes, bytes.Length - offset) : bytes.Length;

                    var request = new MapMeshUploadRequest
                    {
                        SchemaVersion = MapMeshUploadRequest.CurrentSchemaVersion,
                        Map = key,
                        ClientVersion = ModInfo.Version,
                        CapturedAt = capturedAt,
                        Sha256 = sha,
                        Bytes = bytes.Length,
                        Part = parts > 1 ? part : 0,
                        Parts = parts > 1 ? parts : 0,
                        DataBase64 = Convert.ToBase64String(bytes, offset, length)
                    };

                    return await Send(MeshRoute, JsonConvert.SerializeObject(request), MeshRequestTimeout);
                });
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the upload of {key}'s mesh could not be started ({ex.Message}).");
                return null;
            }
        }

        /// <summary>What became of a mesh post, for the upload's last line.</summary>
        private enum MeshVerdict
        {
            /// <summary>The host took it and serves the whole set in 3D.</summary>
            Stored,

            /// <summary>The host could never use this mesh (unreadable, does not fit its own pictures,
            /// over a budget) and serves the pictures WITHOUT it - the capture is shared, flat.</summary>
            ServedFlat,

            /// <summary>The host already serves this capture, or a newer one. Nothing to do.</summary>
            AlreadyServed,

            /// <summary>Nothing is served for this capture: refused, unreachable, an old host, or still
            /// waiting for a floor. The line has been written.</summary>
            NotServed
        }

        /// <summary>
        /// What the host did with the mesh, and the ONE line that says so. Four outcomes, told apart by
        /// the answer's <c>served</c> flag rather than by reading its words, because the difference that
        /// matters to the player is whether anything of the capture is still being held: served means the
        /// map is on the host (in 3D, flat, or already there from before), not served means the floors
        /// wait there and are dropped at the host's next start a day later.
        ///
        /// An old host answers with SPT's own HTML - Debug, because that is the normal state of a host
        /// nobody has updated, and such a host completed the set on its floors anyway.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="bytes">What was sent, for the line.</param>
        /// <param name="task">The finished post.</param>
        private static MeshVerdict JudgeMesh(string key, byte[] bytes, Task<string> task)
        {
            string reply;

            try
            {
                reply = task.Result;
            }
            catch (Exception ex)
            {
                var message = ex is AggregateException aggregate && aggregate.InnerException != null
                    ? aggregate.InnerException.Message
                    : ex.Message;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the host could not be offered {key}'s mesh ({message}) - it holds the floors and " +
                    "drops them at its next start a day later, so capture the map again once the host is reachable.");
                return MeshVerdict.NotServed;
            }

            var response = NotOurs<MapMeshUploadResponse>(reply, out var excerpt);

            if (response == null)
            {
                // An older host answers an unregistered route with SPT's own HTML. It has already stored
                // the pictures (it never saw the mesh block), so nothing is lost but the geometry.
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: this host takes no 3D meshes, so {key}'s stays on this machine - {excerpt}");
                return MeshVerdict.NotServed;
            }

            if (response.Accepted && response.Served) return MeshVerdict.Stored;

            if (response.Served)
            {
                if ((response.Reason ?? "").StartsWith(MeshAlreadyHeldReason, StringComparison.OrdinalIgnoreCase))
                {
                    // A duplicate post, a retry after a reply that was lost, or a second machine's copy of
                    // the same set. Info, because nothing needs doing.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: the host already has this capture of {key}, or a newer one - its mesh is not " +
                        "needed.");
                    return MeshVerdict.AlreadyServed;
                }

                // A Warning, because the 3D view of this map is lost on every other machine - but a
                // warning that says the pictures DID go up, which is the part the old sentence got wrong.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the host did not take {key}'s 3D mesh{Because(response.Reason)}. The pictures are " +
                    "shared without it, so that map draws flat on the other machines until it is captured again.");
                return MeshVerdict.ServedFlat;
            }

            if (response.Accepted)
            {
                // Taken, but the set is not complete: the host is missing a floor this client never
                // managed to encode. The mesh is held with them and goes with them.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the host took {key}'s {Mb(bytes.Length)} MB mesh but has not got the whole set " +
                    $"yet{Because(response.Reason)} - capture the map again.");
                return MeshVerdict.NotServed;
            }

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the host did not take {key}'s 3D mesh{Because(response.Reason)} - it holds the floors " +
                "and drops them at its next start a day later, so that map has no host picture until it is captured " +
                "again.");
            return MeshVerdict.NotServed;
        }

        /// <summary>The start of the host's reason when the mesh is not needed because the host already
        /// serves that capture or a newer one (MapStore's "older than the set on the host", the words the
        /// picture route has always used). The <c>served</c> flag says the capture is on the host; this
        /// prefix only picks the sentence and the level between "already there" and "shared flat".</summary>
        private const string MeshAlreadyHeldReason = "older than the set on the host";

        /// <summary>
        /// One Info line when the host COMPLETED a capture before its mesh was posted and did not keep the
        /// mesh - so "uploaded" is not the only thing the player hears about a map that then draws flat on
        /// every other machine. A host of this build says which in its answer ("stored with its 3D mesh"
        /// when it already had it staged, and nothing is said then); a host from before stage V says
        /// nothing, and it is exactly the host that drops a mesh past its 12 MB at the meta - so an answer
        /// with no "with its 3D mesh" in it, while a mesh was waiting to go, is reported as not kept.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="mesh">The mesh this upload was holding for its last post, or null.</param>
        /// <param name="meshKept">The completing answer's own field, null from a host that did not send it
        /// (review F02).</param>
        /// <param name="reason">The completing answer's reason.</param>
        private static void SayIfMeshWasNotKept(string key, byte[] mesh, bool? meshKept, string reason)
        {
            if (!MeshWasNotKept(mesh?.Length ?? 0, meshKept, reason)) return;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: the host stored the capture of {key} without its 3D mesh{Because(reason)} - it never " +
                "asked for it, so that map draws flat on the other machines. A host older than this build drops a " +
                "mesh past 12 MB this way; update the server half there to share the 3D map.");
        }

        /// <summary>Whether a completing answer means the host did not keep a mesh this upload was holding -
        /// the decision <see cref="SayIfMeshWasNotKept"/> logs on. Pure, so the client harness checks it
        /// against the host's real answers.</summary>
        internal static bool MeshWasNotKept(bool meshPending, string reason) =>
            meshPending && (reason ?? "").IndexOf("with its 3D mesh", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>With the host's own field when it sent one; its words when it did not; and for a host from
        /// before stage V, which says neither, the one thing its behaviour decides: it dropped a mesh past its
        /// 12 MB at the meta and kept one under it (review F02).</summary>
        /// <param name="meshBytes">The size of the mesh this upload was holding, 0 when none.</param>
        /// <param name="meshKept">The completing answer's field, null when the host did not send it.</param>
        /// <param name="reason">The completing answer's reason.</param>
        internal static bool MeshWasNotKept(long meshBytes, bool? meshKept, string reason)
        {
            if (meshBytes <= 0) return false;
            if (meshKept.HasValue) return !meshKept.Value;

            var text = reason ?? "";
            if (text.IndexOf("with its 3D mesh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("without its 3D mesh", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (text.IndexOf("without its 3D mesh", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return meshBytes > PreStageVMeshBytes;
        }

        /// <summary>The mesh size past which a host from before stage V dropped a mesh at the meta - what
        /// <see cref="MeshWasNotKept(long, bool?, string)"/> falls back on when the host says nothing.</summary>
        private const int PreStageVMeshBytes = 12 * 1024 * 1024;

        /// <summary>A byte array's SHA-256 as lower-case hex - the same value MapCapture.Sha256 wrote
        /// into the meta, computed here rather than shared because that one is private to the writer and
        /// this one answers a different question: whether the file on disk is still the one described.</summary>
        /// <param name="bytes">The bytes to hash.</param>
        private static string Sha256(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                var text = new System.Text.StringBuilder(hash.Length * 2);

                foreach (var b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));

                return text.ToString();
            }
        }

        /// <summary>One floor on its way up.</summary>
        private sealed class FloorUpload
        {
            public int Level;
            public string Name = "";
            public string Path = "";

            /// <summary>This floor's entry in the meta being sent, rewritten by <see cref="Encode"/>
            /// to describe the picture actually going up rather than the one on disk. Null for a side.</summary>
            public MapCaptureFloorDto Entry;

            /// <summary>"N"/"S"/"E"/"W" when this is a SIDE picture going up through the floor route,
            /// with its entry in the meta; null for a floor. A side is encoded exactly as a floor is -
            /// composited on the backdrop, scaled to fit the long side, a JPEG at the same quality and
            /// under the same cap - which is why it is the same type rather than a second one.</summary>
            public string Side;

            public MapCaptureSideDto SideEntry;

            /// <summary>The page number when this is an ATLAS page going up through the floor route, with
            /// its entry in the meta; null otherwise. Encoded as a side is, but at its own size and quality
            /// and under its own cap (see <see cref="Encode"/>).</summary>
            public int? Atlas;

            public MapCaptureAtlasDto AtlasEntry;

            /// <summary>The size the meta promises this picture goes up at - the check
            /// <see cref="Encode"/> holds what it produced to.</summary>
            public int WantWidth => AtlasEntry != null ? AtlasEntry.Width : SideEntry != null ? SideEntry.Width : Entry.Width;

            public int WantHeight => AtlasEntry != null ? AtlasEntry.Height : SideEntry != null ? SideEntry.Height : Entry.Height;

            public string Base64;
            public long Bytes;
        }

        // ------------------------------------------------------------------ download

        /// <summary>The sync <see cref="BeginSync"/> started, until <see cref="TryTakeSync"/> applies
        /// it. Main-thread only: the worker fills and RETURNS its result and touches nothing static,
        /// which is what makes it safe - the same discipline QuestDataClient's prefetch keeps.</summary>
        private static Task<SyncResult> _sync;

        /// <summary>Once per session. The host's pictures change when somebody raids a map, which is
        /// minutes at best, and the tab is not a reason to ask again every time it opens.</summary>
        private static bool _syncStarted;

        /// <summary>Whether a sync is still running, for a caller that wants to wait it out.</summary>
        internal static bool IsSyncPending => _sync != null && !_sync.IsCompleted;

        /// <summary>
        /// Asks the host which map pictures it has and takes the ones this machine does not.
        ///
        /// Called from the first host-cache scan (UI/MapCatalog.HostCache), which is the first time
        /// anything looks at a map at all - so a session that never opens the Maps tab never asks.
        /// Main thread, once per session, returns at once. Never throws.
        ///
        /// The whole exchange is one worker: an index, then a picture at a time, then the files. None
        /// of it needs Unity - it is requests, base64 and file writes - so unlike the upload there is
        /// nothing to spread over frames. What DOES need the main thread is the end of it, telling the
        /// map reader to look again, and that is <see cref="TryTakeSync"/>.
        /// </summary>
        internal static void BeginSync()
        {
            if (_syncStarted) return;
            _syncStarted = true;

            // On this (main) thread, before the worker that reads them starts.
            try { SizeSessionLimits(); }
            catch (Exception ex) { Plugin.LogSource?.LogDebug($"QuestTree: the transfer limits stay at their defaults ({ex.Message})."); }

            try
            {
                _sync = Task.Run(() => SyncOffThread());
            }
            catch (Exception ex)
            {
                _sync = null;

                // A pool that cannot take work. Every map falls back to what this machine has, which
                // is what it had before this existed.
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not start the map picture sync ({ex.Message}).");
                return;
            }

            try
            {
                // A main-thread watcher, because the result has to be applied on this thread and
                // nothing else polls for it: the Maps tab does not repaint by itself, so a set that
                // landed while the player looked at the map would otherwise sit on disk unseen until
                // the next thing that happened to rebuild the view.
                Plugin.Instance?.StartCoroutine(WatchSync());
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the map picture sync has no watcher ({ex.Message}) - whatever it downloads is " +
                    "drawn from the next game start.");
            }
        }

        /// <summary>Waits out the sync and applies it, on the main thread.</summary>
        private static IEnumerator WatchSync()
        {
            while (IsSyncPending) yield return null;

            TryTakeSync();
        }

        /// <summary>
        /// Applies a FINISHED sync on the main thread: its log lines, and - when a set landed - the
        /// re-scan that makes the map reader see it.
        ///
        /// False only while the sync is still running, which is a caller's signal to keep waiting.
        /// True with nothing done when there was no sync, or it is already applied: the task is let
        /// go of before anything below can throw, so one sync is applied exactly once.
        /// </summary>
        internal static bool TryTakeSync()
        {
            var sync = _sync;

            if (sync == null) return true;
            if (!sync.IsCompleted) return false;

            _sync = null;

            SyncResult result;

            try
            {
                // Completed, so this cannot block; it can still throw if the pool lost the work.
                result = sync.Result;
            }
            catch (Exception ex)
            {
                var message = ex is AggregateException aggregate && aggregate.InnerException != null
                    ? aggregate.InnerException.Message
                    : ex.Message;

                Plugin.LogSource?.LogInfo($"QuestTree: the host's map pictures could not be fetched ({message}).");
                return true;
            }

            if (result == null) return true;

            // The worker writes no log lines of its own: a ManualLogSource from a pool thread
            // interleaves with the frame it lands in, and every line here is one the player reads
            // against something they just did.
            foreach (var line in result.Debug) Plugin.LogSource?.LogDebug(line);
            foreach (var line in result.Info) Plugin.LogSource?.LogInfo(line);
            foreach (var line in result.Warnings) Plugin.LogSource?.LogWarning(line);

            if (result.Landed <= 0) return true;

            try
            {
                // The reader caches what it found for the whole session, so it has to be told. On
                // this thread, which is what makes freeing the pictures it replaces safe - see
                // MapCatalog.InvalidateCaptures.
                UI.MapCatalog.InvalidateCaptures();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: the Maps tab could not be told about the host's pictures ({ex.Message}) - it " +
                    "finds them on the next game start.");
            }

            return true;
        }

        /// <summary>What one sync did, for the main thread to apply. Log lines rather than logging -
        /// see <see cref="TryTakeSync"/>.</summary>
        private sealed class SyncResult
        {
            public readonly List<string> Info = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Debug = new List<string>();

            /// <summary>How many maps' picture sets were written. Anything above zero means the
            /// reader has to look again.</summary>
            public int Landed;
        }

        /// <summary>
        /// The whole download, on one pool thread. NO Unity API in here, and nothing static written:
        /// requests, base64, and files.
        /// </summary>
        private static SyncResult SyncOffThread()
        {
            var result = new SyncResult();
            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var root = MapsRoot();
                if (root == null) return result;

                var index = FetchIndex(result);
                if (index == null) return result;

                long budget = 0;
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in index.Maps)
                {
                    if (!MaySyncGoOn(clock.Elapsed, budget))
                    {
                        result.Debug.Add(
                            $"QuestTree: the host's map pictures took longer than this session allows " +
                            $"({clock.Elapsed.TotalSeconds:0}s for {Mb(budget)} MB) - the rest are fetched on the next start.");
                        break;
                    }

                    if (entry == null || !IsUsableKey(entry.Map)) continue;

                    var key = entry.Map.Trim();

                    if (string.IsNullOrEmpty(entry.Stamp)) continue;
                    if (entry.Meta == null || entry.Meta.Floors == null || entry.Meta.Floors.Count == 0) continue;

                    // Already have exactly this set - but perhaps not every page of it: a page whose fetch
                    // failed for a reason a retry could change (a timeout, a dropped connection) is noted in
                    // the stamp file and fetched on its own now, rather than lost under a stamp that says the
                    // set is here.
                    if (string.Equals(HeldStamp(root, key), entry.Stamp, StringComparison.Ordinal))
                    {
                        var owed = HeldMissingPages(root, key);
                        var owedSides = HeldMissingSides(root, key);

                        if (owed.Count > 0 || owedSides.Count > 0)
                        {
                            var got = RefetchPages(root, key, entry, owed, owedSides, result);

                            if (got > 0)
                            {
                                budget += got;
                                result.Landed++;
                            }
                        }

                        continue;
                    }

                    if (LocalCaptureIsNewer(key, entry, result)) continue;

                    // The cache's own cap, BEFORE the download: room is made by evicting the sets written
                    // longest ago (never one taken this session), and a set that cannot fit waits.
                    if (!MakeRoom(root, key, Math.Max(entry.Bytes, 0L), installed, result)) continue;

                    var bytes = Download(root, key, entry, result);
                    if (bytes <= 0) continue;

                    budget += bytes;
                    installed.Add(key);
                    result.Landed++;
                }

                return result;
            }
            catch (Exception ex)
            {
                // Nothing here is allowed to surface as anything but a line: every map falls back to
                // what this machine already has.
                result.Debug.Add($"QuestTree: the host's map pictures could not be fetched ({ex.GetType().Name}: {ex.Message}).");
                return result;
            }
        }

        /// <summary>The host's index, or null when there is none to read - an older host, a host with
        /// nothing, or an index shape this build does not know.</summary>
        /// <param name="result">Where the reason goes.</param>
        private static MapIndexDto FetchIndex(SyncResult result)
        {
            var reply = Get(IndexRoute);

            var index = NotOurs<MapIndexDto>(reply, out var excerpt);

            if (index == null)
            {
                // The normal state of a host that has not been updated - see the class comment.
                result.Debug.Add($"QuestTree: the host has no map picture index - {excerpt}");
                return null;
            }

            if (index.SchemaVersion > MapIndexDto.SupportedSchemaVersion)
            {
                result.Warnings.Add(
                    $"QuestTree: the host's map picture index is schema {index.SchemaVersion} and this build reads " +
                    $"{MapIndexDto.SupportedSchemaVersion} - its pictures are ignored. Update Quest Tracker to use them.");
                return null;
            }

            if (index.Maps == null || index.Maps.Count == 0)
            {
                result.Debug.Add(
                    $"QuestTree: the host holds no map pictures yet (it {(index.AcceptsUploads ? "does" : "does not")} " +
                    "accept uploads).");
                return null;
            }

            result.Debug.Add(
                $"QuestTree: the host holds pictures for {index.Maps.Count} map(s) and " +
                $"{(index.AcceptsUploads ? "accepts" : "does not accept")} uploads.");

            return index;
        }

        /// <summary>
        /// One map's picture set, downloaded into a staging folder and then swapped into place. The
        /// bytes written, or 0 when nothing was.
        ///
        /// The ORDER is the whole point of this method - see the class comment. Nothing is moved into
        /// the map's own folder until every picture is in hand; the meta that makes a set readable is
        /// removed before those pictures land and put back only when they all have; and the stamp
        /// that would stop this being tried again is written after everything else.
        /// </summary>
        /// <param name="root">The maps folder.</param>
        /// <param name="key">The map's internal id.</param>
        /// <param name="entry">The index entry being taken.</param>
        /// <param name="result">Where the lines go.</param>
        private static long Download(string root, string key, MapIndexEntryDto entry, SyncResult result)
        {
            var staging = Path.Combine(Path.Combine(root, IncomingFolder), key);

            try
            {
                Wipe(staging);
                Directory.CreateDirectory(staging);

                var meta = entry.Meta;
                var floors = new List<MapCaptureFloorDto>();
                var seen = new HashSet<int>();
                long bytes = 0;

                foreach (var floor in meta.Floors)
                {
                    if (floor == null || floors.Count >= MaxFloors) continue;
                    if (!seen.Add(floor.Level)) continue;

                    var picture = Fetch(key, floor.Level, entry.Stamp, result, out var name, out var replaced,
                        out var floorTransient);

                    // The set was replaced on the host while it was being fetched. ABANDONED, not
                    // continued: the floors already in hand are the old set's and the ones left are
                    // the new set's, so carrying on would swap a subset of the old set over whatever
                    // this machine has - replacing a complete four-floor map with two floors of it.
                    // Nothing has been moved out of the staging folder yet, so returning here leaves
                    // the map exactly as it was, and the stamp this machine holds still differs from
                    // the host's new one, which is what makes the next session take the whole set.
                    if (replaced) return 0;

                    // Review F29: a floor that did not ARRIVE - a timeout, a dropped connection, or an empty
                    // answer for a level the host's own meta names, which is a host that could not read its
                    // file - is not "the host has none". Installing the rest would write the host's stamp over
                    // a set with a hole in it, and the stamp is what stops this machine asking again. So the
                    // whole map is left as it was for this session, with no stamp, and taken again next time -
                    // FetchMesh's rule for the same case.
                    if (picture == null && floorTransient)
                    {
                        result.Debug.Add(
                            $"QuestTree: floor {floor.Level} of the host's {key} did not arrive - that map is taken " +
                            "again, whole, next session.");
                        return 0;
                    }

                    if (picture == null) continue;

                    if (bytes + picture.Length > MaxMapDownload)
                    {
                        result.Debug.Add(
                            $"QuestTree: the host's pictures of {key} are over the {Mb(MaxMapDownload)} MB a map " +
                            "may take - the floors past that are left.");
                        break;
                    }

                    File.WriteAllBytes(Path.Combine(staging, name), picture);

                    // The name and size the meta will carry are the ones actually written, so the
                    // set on disk describes itself - the reader refuses anything else.
                    floor.File = name;
                    floors.Add(floor);
                    bytes += picture.Length;
                }

                if (floors.Count == 0)
                {
                    result.Debug.Add($"QuestTree: the host sent no usable picture of {key}.");
                    return 0;
                }

                // The SIDES, after the floors, through the same image route with the side's direction.
                // Only those the host's meta names, and each held to the size that meta states before it
                // is written: a side the host cannot send, that is not a JPEG of that size, or that does
                // not fit the map's budget is left out of the meta rather than named beside a file that
                // is not there - the 3D view then textures those walls with a flat tint, as it does for
                // every set captured before sides existed.
                var sideNames = new List<string>();
                var keptSides = new List<MapCaptureSideDto>();
                var owedSides = new List<string>();

                if (meta.Sides != null)
                {
                    foreach (var dirName in SideDirs)
                    {
                        var side = meta.Sides.Find(sd => sd != null &&
                                                         string.Equals(sd.Dir, dirName, StringComparison.OrdinalIgnoreCase));

                        if (side == null) continue;

                        var picture = FetchSide(key, dirName, side, entry.Stamp, result, out var sideReplaced,
                            out var sideTransient);

                        // As for a floor: the host now holds a different set, and half of each is worse
                        // than either - nothing has left the staging, so the map stays as it was.
                        if (sideReplaced) return 0;

                        // Review F29: a side that did not ARRIVE is OWED - named in the stamp file and fetched
                        // alone next session, as an atlas page is - rather than lost under the host's stamp.
                        if (picture == null && sideTransient) owedSides.Add(dirName);

                        if (picture == null) continue;

                        if (bytes + picture.Length > MaxMapDownload)
                        {
                            result.Debug.Add(
                                $"QuestTree: {key}'s {dirName} side would take the map past the " +
                                $"{Mb(MaxMapDownload)} MB it may weigh - it is left out.");
                            continue;
                        }

                        var sideName = SideFileName(key, dirName);

                        File.WriteAllBytes(Path.Combine(staging, sideName), picture);

                        side.Dir = dirName;
                        side.File = sideName;
                        keptSides.Add(side);
                        sideNames.Add(sideName);
                        bytes += picture.Length;
                    }
                }

                meta.Sides = keptSides.Count == 0 ? null : keptSides;

                // The MESH, after the floors and only when the index said there is one - so an older
                // host is never asked, and a newer one is asked exactly once per set. A PERMANENT failure
                // (a sha that does not match, a format or size this build refuses) installs the set
                // WITHOUT it and with the meta's block removed, which is the state every reader already
                // handles; a TRANSIENT one (a timeout, a dropped connection, a set replaced mid-fetch)
                // abandons the whole map for this session with no stamp written - see FetchMesh.
                string meshName = null;

                if (entry.Mesh == null)
                {
                    // The host's own index is the authority on whether this set has a mesh: a meta that
                    // claims one while the entry does not would leave the block in place with no file
                    // beside it, and the 3D view would refuse the set on every open.
                    meta.Mesh = null;
                }
                else
                {
                    var mesh = FetchMesh(key, entry, bytes, result, out var abandonMap);

                    // Nothing has moved out of staging yet, so returning here leaves the map exactly as it
                    // was and the held stamp still differs from the host's - which is what makes the next
                    // session try the whole set again.
                    if (abandonMap) return 0;

                    if (mesh == null)
                    {
                        meta.Mesh = null;
                    }
                    else
                    {
                        meshName = MeshFileName(key);

                        File.WriteAllBytes(Path.Combine(staging, meshName), mesh);

                        // A COPY of the index entry's block, not the block itself. From the entry because
                        // that is what this download was decided on - and because a host whose entry
                        // carries a mesh while its meta does not would otherwise be a null here. Copied
                        // because the entry belongs to the index this worker is still walking: writing the
                        // local file name into it would leave the NEXT map's decisions reading a block
                        // that had been edited to describe a file on this disk.
                        meta.Mesh = new MapCaptureMeshDto
                        {
                            // The two fields that describe what actually landed, exactly as a floor's file
                            // and size are rewritten: the set on disk has to describe itself.
                            File = meshName,
                            Bytes = mesh.Length,

                            Version = entry.Mesh.Version,
                            Cells = entry.Mesh.Cells,
                            Triangles = entry.Mesh.Triangles,
                            Sha256 = entry.Mesh.Sha256
                        };

                        bytes += mesh.Length;
                    }
                }

                // The ATLAS PAGES, after the mesh and only when one landed: a page drapes the mesh's
                // buildings and nothing else. Each through the image route by its number and held to what
                // the host's index says of it - a JPEG, of the width and height named, hashing to the sha256
                // named (the host's own, rewritten when it stored the set). A page that fails any of that,
                // or would take the map past its budget, is left out of the meta rather than named beside a
                // file that is not there; the 3D view then draws those buildings as it did before pages.
                var pageNames = new List<string>();
                var keptPages = new List<MapCaptureAtlasDto>();
                var owedPages = new List<int>();

                if (meshName != null && meta.Atlas != null)
                {
                    foreach (var page in meta.Atlas.Where(p => p != null).OrderBy(p => p.Page))
                    {
                        if (page.Page < 0 || page.Page >= MaxAtlasPages || keptPages.Any(k => k.Page == page.Page)) continue;

                        var picture = FetchAtlasPage(key, page, entry.Stamp, result, out var pageReplaced, out var transient);

                        // As for a floor: half of two sets is worse than either.
                        if (pageReplaced) return 0;

                        // A page that did not ARRIVE (as opposed to one that arrived wrong) is owed: the stamp
                        // file names it, and the next session fetches it alone - see HeldMissingPages.
                        if (picture == null && transient) owedPages.Add(page.Page);

                        if (picture == null) continue;

                        if (bytes + picture.Length > MaxMapDownload)
                        {
                            result.Debug.Add(
                                $"QuestTree: {key}'s atlas page {page.Page} would take the map past the " +
                                $"{Mb(MaxMapDownload)} MB it may weigh - it is left out.");
                            continue;
                        }

                        var pageName = AtlasFileName(key, page.Page);

                        File.WriteAllBytes(Path.Combine(staging, pageName), picture);

                        page.File = pageName;
                        keptPages.Add(page);
                        pageNames.Add(pageName);
                        bytes += picture.Length;
                    }
                }

                meta.Atlas = keptPages.Count == 0 ? null : keptPages;

                // The meta written out names exactly the floors whose picture is in the staging
                // folder. A floor the host could not send is dropped rather than named: the reader
                // would drop it anyway, and a meta naming a file that is not there is how a set
                // comes to look half-written.
                meta.Map = key;
                meta.Floors = floors;

                File.WriteAllText(
                    Path.Combine(staging, key + MetaSuffix),
                    JsonConvert.SerializeObject(meta, Formatting.Indented));

                if (!Swap(root, key, staging, floors, sideNames.Concat(pageNames).ToList(), meshName, entry.Stamp, result,
                        owedPages, owedSides))
                    return 0;

                result.Info.Add(
                    $"QuestTree: map picture set for {key} received from the host - {floors.Count} floor(s)" +
                    $"{(sideNames.Count == 0 ? "" : $", {sideNames.Count} side(s)")}" +
                    $"{(pageNames.Count == 0 ? "" : $", {pageNames.Count} atlas page(s)")}" +
                    $"{(meshName == null ? "" : " and a 3D mesh")}, {Mb(bytes)} MB.");

                return bytes;
            }
            catch (Exception ex)
            {
                result.Debug.Add(
                    $"QuestTree: the host's pictures of {key} could not be taken ({ex.GetType().Name}: {ex.Message}) - " +
                    "whatever was there before is untouched.");
                return 0;
            }
            finally
            {
                Wipe(staging);
            }
        }

        /// <summary>
        /// Puts a fully downloaded set into the map's own folder, in the one order that cannot leave
        /// a readable half set:
        ///
        /// 1. the OLD meta goes first, which makes the map unreadable rather than wrong - the reader
        ///    finds no meta and the map falls back to whatever else it has;
        /// 2. then the pictures, replacing whatever was there;
        /// 3. then the new meta, one rename, which is the instant the new set becomes readable;
        /// 4. then the pictures the new meta does not name, which nothing was reading anyway;
        /// 5. then the stamp, so a swap interrupted anywhere above is simply done again next session.
        ///
        /// Killed between any two of those, a reader sees either the previous complete set (before 1)
        /// or no set for this map (between 1 and 3) or the new complete set (after 3). It never sees
        /// one set's meta beside another set's pictures.
        /// </summary>
        /// <param name="root">The maps folder.</param>
        /// <param name="key">The map's internal id.</param>
        /// <param name="staging">The folder holding the downloaded set.</param>
        /// <param name="floors">The floors the new meta names.</param>
        /// <param name="mesh">The mesh file's name in the staging folder, or null when this set has
        /// none. Moved with the pictures and KEPT by step 4's sweep - a mesh deleted there would leave
        /// the meta naming a file that is not beside it, which is exactly the half-written set this
        /// method exists to prevent.</param>
        /// <param name="stamp">The host's name for this set, written last of all.</param>
        /// <param name="result">Where a failure's line goes.</param>
        /// <param name="sides">The side pictures' AND atlas pages' names in the staging folder - moved with
        /// the floors and kept by step 4, which sweeps any side or page an older set of this map had and this
        /// one does not.</param>
        /// <param name="owedPages">Atlas pages that did not arrive for a reason a retry could change, written
        /// into the stamp file for the next session to fetch (see <see cref="HeldMissingPages"/>).</param>
        /// <param name="owedSides">Side pictures owed the same way (see <see cref="HeldMissingSides"/>).</param>
        private static bool Swap(
            string root, string key, string staging, List<MapCaptureFloorDto> floors, List<string> sides,
            string mesh, string stamp, SyncResult result, List<int> owedPages = null, List<string> owedSides = null)
        {
            var folder = Path.Combine(root, key);

            try
            {
                Directory.CreateDirectory(folder);

                var meta = Path.Combine(folder, key + MetaSuffix);

                // (1) Unreadable before it is wrong. EVERY meta in the folder, not only the one this
                // writes: the reader reads every *.map.json it finds there (MapCatalog.ScanFolder),
                // so a second one - hand-copied in, or left by a map that was renamed - would keep
                // naming the pictures this is about to replace.
                foreach (var stale in Directory.GetFiles(folder, "*" + MetaSuffix)) File.Delete(stale);

                // (2) The pictures.
                var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var floor in floors)
                {
                    var from = Path.Combine(staging, floor.File);
                    var to = Path.Combine(folder, floor.File);

                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);

                    kept.Add(floor.File);
                }

                // (2a) The side pictures, with the floors and for the same reason: the meta names them.
                foreach (var side in sides ?? new List<string>())
                {
                    var from = Path.Combine(staging, side);
                    var to = Path.Combine(folder, side);

                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);

                    kept.Add(side);
                }

                // (2b) The mesh, with the pictures and before the meta, for the same reason they are:
                // the meta is what names it, so it has to be in place before anything can read that
                // name. Added to `kept` so step 4 leaves it alone.
                if (mesh != null)
                {
                    var from = Path.Combine(staging, mesh);
                    var to = Path.Combine(folder, mesh);

                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);

                    kept.Add(mesh);
                }

                // (3) The meta, last of the set. One rename inside one volume, so no reader can see
                // half of this file.
                File.Move(Path.Combine(staging, key + MetaSuffix), meta);

                // (4) Pictures of an older set of this map that the new meta has no floor for.
                foreach (var file in Directory.GetFiles(folder))
                {
                    var name = Path.GetFileName(file);

                    if (string.Equals(name, StampFile, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (kept.Contains(name)) continue;

                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        // Harmless: nothing names it, so nothing reads it.
                        result.Debug.Add($"QuestTree: {name} is left in {key}'s folder ({ex.Message}).");
                    }
                }

                // (5) And only now the record that says "this machine has that set", so a swap
                // interrupted at any step above is simply done again on the next start.
                WriteStamp(root, key, stamp, result, owedPages, owedSides);

                return true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"QuestTree: the host's pictures of {key} were downloaded but could not be put in place " +
                    $"({ex.GetType().Name}: {ex.Message}) - that map has no host picture until this is tried again.");
                return false;
            }
        }

        /// <summary>The stamp file, written after everything it describes - see
        /// <see cref="Swap"/>, step 5. Its own method because a failure here must not undo a set that
        /// is already in place: the worst it costs is one more download next session.</summary>
        /// <param name="root">The maps folder.</param>
        /// <param name="key">The map's internal id.</param>
        /// <param name="stamp">The host's name for the set now in place.</param>
        /// <param name="result">Where a failure's line goes.</param>
        /// <param name="owedPages">Atlas pages still owed, written as a second line - see
        /// <see cref="HeldMissingPages"/>. None, and the file is the stamp alone, as it always was.</param>
        /// <param name="owedSides">Side pictures still owed (review F29), a line of their own - see
        /// <see cref="OwedSidesPrefix"/>.</param>
        private static void WriteStamp(string root, string key, string stamp, SyncResult result, List<int> owedPages = null,
            List<string> owedSides = null)
        {
            try
            {
                var path = Path.Combine(Path.Combine(root, key), StampFile);
                var temp = path + ".tmp";
                var text = stamp;

                if (owedPages != null && owedPages.Count > 0)
                    text += "\n" + OwedPrefix + string.Join(",",
                        owedPages.Distinct().OrderBy(p => p).Select(p => p.ToString(CultureInfo.InvariantCulture)));

                if (owedSides != null && owedSides.Count > 0)
                    text += "\n" + OwedSidesPrefix + string.Join(",", SideDirs.Where(d => owedSides.Contains(d)));

                File.WriteAllText(temp, text);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                result.Debug.Add(
                    $"QuestTree: {key}'s picture set is in place but its stamp could not be written " +
                    $"({ex.Message}) - it will be downloaded again next session.");
            }
        }

        /// <summary>One floor's picture from the host, decoded, or null when there is none to have.
        /// The file name to write it under comes back too, built from the map and level rather than
        /// from anything the host sent - a name is a path here.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="level">Which floor.</param>
        /// <param name="stamp">The set this download belongs to.</param>
        /// <param name="result">Where the lines go.</param>
        /// <param name="name">The file name to write, or null.</param>
        /// <param name="replaced">True when the host answered with a DIFFERENT set's stamp, which is
        /// the one failure the caller must not treat as "this floor is missing": see Download.</param>
        /// <param name="transient">True when the floor did not ARRIVE - the request failed or timed out, or the host
        /// answered empty for a level its own meta names - which a later session may change (review F29).</param>
        private static byte[] Fetch(
            string key, int level, string stamp, SyncResult result, out string name, out bool replaced,
            out bool transient)
        {
            name = null;
            replaced = false;
            transient = false;

            try
            {
                var body = JsonConvert.SerializeObject(new MapImageRequest { Map = key, Level = level });

                var reply = Post(ImageRoute, body);
                var image = NotOurs<MapImageDto>(reply, out var excerpt);

                if (image == null)
                {
                    result.Debug.Add(
                        $"QuestTree: the reply for {key} floor {level} was not the server half's - {excerpt}");
                    return null;
                }

                if (string.IsNullOrEmpty(image.ImageBase64))
                {
                    // Every level the index names is backed by a stored file (the host serves from its meta),
                    // so an empty answer is the host failing to READ it - its own log says so - not a floor it
                    // lacks. TRANSIENT (review F29).
                    transient = true;
                    result.Debug.Add($"QuestTree: the host could not send {key} floor {level} this time.");
                    return null;
                }

                // A host re-captured between the index and this request holds a different set now,
                // and half of each is worse than either.
                if (!string.IsNullOrEmpty(image.Stamp) && !string.Equals(image.Stamp, stamp, StringComparison.Ordinal))
                {
                    // Said out to the caller rather than swallowed as a missing floor: the whole map
                    // has to be abandoned, because the floors already fetched are the old set's.
                    replaced = true;

                    result.Debug.Add(
                        $"QuestTree: the host's pictures of {key} changed while they were being fetched - the whole " +
                        "set is taken again next session rather than half of each.");
                    return null;
                }

                var bytes = Convert.FromBase64String(image.ImageBase64);

                if (bytes.Length == 0 || bytes.Length > MaxFloorDownloadBytes)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's picture of {key} floor {level} is {bytes.Length} bytes, which is " +
                        "not a picture this build will write.");
                    return null;
                }

                var extension = Extension(bytes);

                if (extension == null)
                {
                    // The magic, not the format field: a host - or whatever answered as one - saying
                    // "jpg" over something else is the one case worth refusing outright.
                    result.Debug.Add(
                        $"QuestTree: the host's picture of {key} floor {level} is neither a JPEG nor a PNG and " +
                        "was not written.");
                    return null;
                }

                name = $"{key}-{level.ToString(CultureInfo.InvariantCulture)}{extension}";
                return bytes;
            }
            catch (Exception ex)
            {
                // A timeout, a dropped connection, a reply cut short: TRANSIENT (review F29).
                transient = true;
                result.Debug.Add(
                    $"QuestTree: the host's picture of {key} floor {level} could not be taken ({ex.Message}).");
                return null;
            }
        }

        /// <summary>
        /// One map's mesh from the host, decoded and verified, or null when there is none to have.
        ///
        /// Two kinds of failure, and the difference is whether trying again could ever help - because
        /// the caller writes the host's STAMP after installing a set, and a stamp that matches is what
        /// stops this machine ever asking for that set again:
        ///
        /// PERMANENT - a mesh that does not hash to what the index promised, is a format this build
        /// cannot read, is over a cap, is not base64, or that the host says it does not have. Asking
        /// again next session gets the same bytes and the same answer, so these return null and the
        /// caller installs the pictures FLAT with the meta's mesh block removed - the map draws as it
        /// does on every client that never had a mesh - and writes the stamp.
        ///
        /// TRANSIENT - a timeout, a dropped connection, anything thrown by the request itself, and the
        /// host replacing the set mid-download. These set <paramref name="abandon"/>, and the caller
        /// takes NOTHING of the map this session and writes no stamp, exactly as a mid-download
        /// replacement always did. Installing flat here would have been the worst of both: a mesh lost
        /// to a slow link, and a stamp saying this machine already has the set, so the mesh would never
        /// be fetched again until somebody re-captured the map.
        ///
        /// The sha256 is checked against the INDEX ENTRY rather than against the answer's own field: the
        /// answer's is what the host says about what it just sent, the entry's is what the client
        /// decided to download on, and only the second one is evidence. This is the one payload in the
        /// transport that no human ever looks at, so nothing downstream would notice it arriving wrong -
        /// it would simply be geometry in the wrong places.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="entry">The index entry being taken. Its <c>Mesh</c> is not null.</param>
        /// <param name="soFar">What this map's pictures already weigh, for the per-map budget.</param>
        /// <param name="result">Where the lines go.</param>
        /// <param name="abandon">True for a TRANSIENT failure - see the summary. The caller abandons the
        /// whole map for this session and writes no stamp.</param>
        private static byte[] FetchMesh(
            string key, MapIndexEntryDto entry, long soFar, SyncResult result, out bool abandon)
        {
            abandon = false;

            try
            {
                var promised = (entry.Mesh.Sha256 ?? "").Trim();

                if (promised.Length != 64)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's index describes {key}'s mesh with no usable sha256, so it is not " +
                        "taken - the pictures are.");
                    return null;
                }

                // The version, BEFORE the download rather than after - which is the whole reason the
                // index carries it. A host updated past this client holds a mesh in a format this build's
                // reader refuses (MapMeshFile.Read), so fetching it would be megabytes spent to write a
                // file the Maps tab then declines to draw.
                if (entry.Mesh.Version != MapMeshFile.Version)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's mesh for {key} is format version {entry.Mesh.Version} and this " +
                        $"build reads {MapMeshFile.Version} - the pictures are taken without it. Update Quest " +
                        "Tracker to use the host's 3D maps.");
                    return null;
                }

                if (entry.Mesh.Bytes > ClientMeshCeiling)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's mesh for {key} is {Mb(entry.Mesh.Bytes)} MB, over the " +
                        $"{Mb(ClientMeshCeiling)} MB this machine takes - the pictures are taken without it.");
                    return null;
                }

                if (soFar + Math.Max(entry.Mesh.Bytes, 0L) > MaxMapDownload)
                {
                    result.Debug.Add(
                        $"QuestTree: {key}'s pictures and mesh together are over the {Mb(MaxMapDownload)} MB a " +
                        "map may take - the mesh is left.");
                    return null;
                }

                var body = JsonConvert.SerializeObject(new MapMeshRequest { Map = key });

                string reply;

                // The deadline for THIS mesh's size (D18): the whole mesh is one body coming down, so it scales
                // with it. Post waits that plus AbortGrace as its backstop.
                var deadline = TimeSpan.FromSeconds(MeshDownloadSecondsFor(entry.Mesh.Bytes));
                var took = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    reply = Post(MeshFileRoute, body, deadline);
                }
                catch (Exception ex)
                {
                    // TRANSIENT, the whole reason for the abandon: a timeout or a broken connection says
                    // nothing about the mesh, and the next session's attempt may well succeed.
                    abandon = true;

                    var message = ex is AggregateException aggregate && aggregate.InnerException != null
                        ? aggregate.InnerException.Message
                        : ex.Message;

                    result.Debug.Add(
                        $"QuestTree: the host's 3D mesh for {key} did not arrive ({message}) - nothing of that map " +
                        "is taken this session, and the whole set is fetched again on the next start rather than " +
                        "installed flat for good.");
                    return null;
                }

                var dto = NotOurs<MapMeshDto>(reply, out var excerpt);

                if (dto == null)
                {
                    // An older host answers this route with SPT's own HTML. Silent, at Debug: it is the
                    // normal state of a host that has not been updated, and the pictures still land.
                    result.Debug.Add($"QuestTree: this host serves no 3D mesh for {key} - {excerpt}");
                    return null;
                }

                if (string.IsNullOrEmpty(dto.DataBase64))
                {
                    result.Debug.Add($"QuestTree: the host has no 3D mesh for {key} after all.");
                    return null;
                }

                // A host re-captured between the index and this request holds a different set now, and
                // half of each is worse than either - the same rule the pictures follow.
                if (!string.IsNullOrEmpty(dto.Stamp) &&
                    !string.Equals(dto.Stamp, entry.Stamp, StringComparison.Ordinal))
                {
                    abandon = true;

                    result.Debug.Add(
                        $"QuestTree: the host's set for {key} changed while its mesh was being fetched - the whole " +
                        "set is taken again next session rather than half of each.");
                    return null;
                }

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(dto.DataBase64);
                }
                catch (FormatException)
                {
                    // PERMANENT: the host answered, and what it answered is not base64. It will answer the
                    // same next time.
                    result.Debug.Add(
                        $"QuestTree: the host's 3D mesh for {key} is not base64 - the pictures are taken without it.");
                    return null;
                }

                if (bytes.Length == 0 || bytes.Length > ClientMeshCeiling)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's mesh for {key} is {bytes.Length:N0} bytes, which is not a mesh this " +
                        "build will write - the pictures are taken without it.");
                    return null;
                }

                // The per-map budget again, against what DECODED rather than what the index claimed: the
                // check above trusted the host's own number, and a host - or something answering as one -
                // that under-states it would otherwise walk a map past its ceiling.
                if (soFar + bytes.Length > MaxMapDownload)
                {
                    result.Debug.Add(
                        $"QuestTree: {key}'s mesh decoded to {Mb(bytes.Length)} MB, which with its pictures is over " +
                        $"the {Mb(MaxMapDownload)} MB a map may take - the mesh is left.");
                    return null;
                }

                var hash = Sha256(bytes);

                if (!string.Equals(hash, promised, StringComparison.OrdinalIgnoreCase))
                {
                    // Warning, not Debug: the pictures of this set are fine and are installed, but the
                    // host is serving a mesh that is not the one it describes, and that is worth seeing.
                    result.Warnings.Add(
                        $"QuestTree: the host's 3D mesh for {key} hashes to {hash.Substring(0, 12)} where its index " +
                        $"promised {promised.Substring(0, 12)} - it is NOT installed and the map draws flat. The " +
                        "pictures are unaffected.");
                    return null;
                }

                result.Info.Add(
                    $"QuestTree: the host's 3D mesh for {key} ({Mb(bytes.Length)} MB) arrived in " +
                    $"{took.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s of its " +
                    $"{deadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s deadline.");

                return bytes;
            }
            catch (Exception ex)
            {
                // Anything else is this code failing rather than the host answering, which is not a fact
                // about the mesh - so it is treated as transient: nothing of the map this session, no
                // stamp, and the whole set again next start.
                abandon = true;

                result.Debug.Add(
                    $"QuestTree: the host's 3D mesh for {key} could not be taken ({ex.GetType().Name}: " +
                    $"{ex.Message}) - nothing of that map is taken this session.");
                return null;
            }
        }

        /// <summary>The mesh's file name in a map's folder - MapMeshFile's own, so the downloaded set is
        /// named by the same method the capture writer and the format's recogniser use rather than by a
        /// literal that could drift from them. Built from the KEY rather than from anything the host sent:
        /// a name on the wire is a path.</summary>
        /// <param name="key">The map's internal id.</param>
        private static string MeshFileName(string key) => MapMeshFile.FileNameFor(key);

        /// <summary>
        /// One side picture from the host, checked, or null when there is none to have. The same route a
        /// floor comes down (the image route, with the side's direction), the same caps and the same
        /// stamp check - and one more check than a floor gets: the JPEG's own frame size must be the
        /// width and height the meta states. A side's pixels are mapped onto walls by those two numbers
        /// and nothing else, so a picture of any other size would texture every wall a constant fraction
        /// off with no other symptom.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="dir">The side's direction.</param>
        /// <param name="side">Its entry in the host's meta.</param>
        /// <param name="stamp">The set this download belongs to.</param>
        /// <param name="result">Where the lines go.</param>
        /// <param name="replaced">True when the host answered with a DIFFERENT set's stamp.</param>
        /// <param name="transient">True when the side did not ARRIVE - see <see cref="Fetch"/>'s (review F29).</param>
        private static byte[] FetchSide(
            string key, string dir, MapCaptureSideDto side, string stamp, SyncResult result, out bool replaced,
            out bool transient)
        {
            replaced = false;
            transient = false;

            try
            {
                var body = JsonConvert.SerializeObject(new MapImageRequest { Map = key, Side = dir });
                var image = NotOurs<MapImageDto>(Post(ImageRoute, body), out var excerpt);

                if (image == null)
                {
                    result.Debug.Add($"QuestTree: the reply for {key}'s {dir} side was not the server half's - {excerpt}");
                    return null;
                }

                if (string.IsNullOrEmpty(image.ImageBase64))
                {
                    // Named in the host's own meta, so an empty answer is a read the host failed (review F29).
                    transient = true;
                    result.Debug.Add($"QuestTree: the host could not send the {dir} side picture of {key} this time.");
                    return null;
                }

                if (!string.IsNullOrEmpty(image.Stamp) && !string.Equals(image.Stamp, stamp, StringComparison.Ordinal))
                {
                    replaced = true;

                    result.Debug.Add(
                        $"QuestTree: the host's pictures of {key} changed while they were being fetched - the whole " +
                        "set is taken again next session rather than half of each.");
                    return null;
                }

                var bytes = Convert.FromBase64String(image.ImageBase64);

                if (bytes.Length == 0 || bytes.Length > MaxFloorDownloadBytes || Extension(bytes) != ".jpg")
                {
                    result.Debug.Add(
                        $"QuestTree: the host's {dir} side picture of {key} is not a JPEG this build will write - " +
                        "it is left out.");
                    return null;
                }

                if (!JpegSize(bytes, out var width, out var height) || width != side.Width || height != side.Height)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's {dir} side picture of {key} is {width}x{height} px where its meta says " +
                        $"{side.Width}x{side.Height} - it is left out rather than drawn misplaced.");
                    return null;
                }

                return bytes;
            }
            catch (Exception ex)
            {
                transient = true;
                result.Debug.Add($"QuestTree: the host's {dir} side picture of {key} could not be taken ({ex.Message}).");
                return null;
            }
        }

        /// <summary>A side picture's file name in a map's folder - the host's own name for it. Built from
        /// the key and the direction, never from anything the host sent.</summary>
        private static string SideFileName(string key, string dir) => $"{key}-side-{dir}.jpg";

        /// <summary>
        /// One atlas page from the host, checked, or null when there is none to have - <see cref="FetchSide"/>
        /// for a page, with one check more: the bytes must hash to the sha256 the host's index names for the
        /// page. A page's texels are addressed by the mesh's UVs and nothing else, so a wrong page is every
        /// building wearing another building's walls with no other symptom, and the sha is the one field
        /// that can say so. The request carries SideLevel as its level, so a host that does not know pages
        /// answers "no floor there" rather than sending floor 0.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="page">Its entry in the host's meta.</param>
        /// <param name="stamp">The set this download belongs to.</param>
        /// <param name="result">Where the lines go.</param>
        /// <param name="replaced">True when the host answered with a DIFFERENT set's stamp.</param>
        /// <param name="transient">True when the page did not ARRIVE - the request failed or timed out - which a
        /// later session may change; false when it arrived wrong or the host has none, which it will not.</param>
        private static byte[] FetchAtlasPage(
            string key, MapCaptureAtlasDto page, string stamp, SyncResult result, out bool replaced, out bool transient)
        {
            replaced = false;
            transient = false;

            string reply;

            try
            {
                // The MESH's deadline: a page is up to 6 MB, 8 MB of base64, which a slow link does not bring
                // down in a picture's 30 s.
                var body = JsonConvert.SerializeObject(new MapImageRequest { Map = key, Level = SideLevel, Atlas = page.Page });
                reply = Post(ImageRoute, body, MeshRequestTimeout);
            }
            catch (Exception ex)
            {
                transient = true;

                result.Debug.Add(
                    $"QuestTree: the host's atlas page {page.Page} of {key} did not arrive ({ex.GetBaseException().Message}) - " +
                    "it is fetched again next session.");
                return null;
            }

            try
            {
                var image = NotOurs<MapImageDto>(reply, out var excerpt);

                if (image == null)
                {
                    result.Debug.Add($"QuestTree: the reply for {key}'s atlas page {page.Page} was not the server half's - {excerpt}");
                    return null;
                }

                if (string.IsNullOrEmpty(image.ImageBase64))
                {
                    // Named in the host's own meta: a read it failed, owed rather than lost (review F29's rule).
                    transient = true;
                    result.Debug.Add($"QuestTree: the host could not send atlas page {page.Page} of {key} this time.");
                    return null;
                }

                if (!string.IsNullOrEmpty(image.Stamp) && !string.Equals(image.Stamp, stamp, StringComparison.Ordinal))
                {
                    replaced = true;

                    result.Debug.Add(
                        $"QuestTree: the host's pictures of {key} changed while they were being fetched - the whole " +
                        "set is taken again next session rather than half of each.");
                    return null;
                }

                var bytes = Convert.FromBase64String(image.ImageBase64);

                if (bytes.Length == 0 || bytes.Length > MaxAtlasPageBytes || Extension(bytes) != ".jpg")
                {
                    result.Debug.Add(
                        $"QuestTree: the host's atlas page {page.Page} of {key} is not a JPEG of up to " +
                        $"{Mb(MaxAtlasPageBytes)} MB - it is left out.");
                    return null;
                }

                if (!JpegSize(bytes, out var width, out var height) || width != page.Width || height != page.Height)
                {
                    result.Debug.Add(
                        $"QuestTree: the host's atlas page {page.Page} of {key} is {width}x{height} px where its meta " +
                        $"says {page.Width}x{page.Height} - it is left out rather than drawn misplaced.");
                    return null;
                }

                var hash = Sha256(bytes);

                if (!string.Equals(hash, page.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    result.Debug.Add(
                        $"QuestTree: the host's atlas page {page.Page} of {key} hashes to {hash.Substring(0, 12)}, not the " +
                        "sha256 its index names - it is left out rather than drawn on the wrong buildings.");
                    return null;
                }

                return bytes;
            }
            catch (Exception ex)
            {
                result.Debug.Add($"QuestTree: the host's atlas page {page.Page} of {key} could not be taken ({ex.Message}).");
                return null;
            }
        }

        /// <summary>An atlas page's file name in a map's folder - the host's own name for it, built from the
        /// key and the page number, never from anything the host sent.</summary>
        private static string AtlasFileName(string key, int page) =>
            $"{key}-atlas-{page.ToString(CultureInfo.InvariantCulture)}.jpg";

        /// <summary>A JPEG's width and height from its frame header, or false. Walks the marker chain
        /// rather than trusting an offset - the frame header comes after however many other segments the
        /// encoder wrote - and decodes nothing: the same walk tools/check-maps-pack.py makes.</summary>
        internal static bool JpegSize(byte[] data, out int width, out int height)
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

        /// <summary>".jpg", ".png", or null when these bytes are neither. Read from the bytes
        /// themselves - the two magics - because the file name is what the reader will trust.</summary>
        /// <param name="bytes">The decoded picture.</param>
        private static string Extension(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";

            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return ".png";

            return null;
        }

        /// <summary>The host's name for the set already in this map's folder, or "" when there is
        /// none. Trimmed, because it is a text file and an editor may have added a newline.</summary>
        /// <param name="root">The maps folder.</param>
        /// <param name="key">The map's internal id.</param>
        private static string HeldStamp(string root, string key)
        {
            try
            {
                var path = Path.Combine(Path.Combine(root, key), StampFile);

                // The FIRST line: a second one, when there is one, lists the atlas pages still owed.
                return File.Exists(path) ? (File.ReadAllLines(path).FirstOrDefault() ?? "").Trim() : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>The stamp file's second line, when a download left atlas pages owed: "owed-atlas: 1,4".
        /// Written by <see cref="WriteStamp"/> for a page whose fetch failed for a reason a retry could change -
        /// a timeout, a dropped connection - and never for one that arrived WRONG (another size, another
        /// sha), which the next session would get wrong in the same way.</summary>
        private const string OwedPrefix = "owed-atlas: ";

        /// <summary>The stamp file's line of side pictures still owed: "owed-sides: N,E" (review F29).</summary>
        private const string OwedSidesPrefix = "owed-sides: ";

        /// <summary>The side pictures the set in this map's folder is still owed - see <see cref="OwedSidesPrefix"/>.</summary>
        private static List<string> HeldMissingSides(string root, string key)
        {
            var owed = new List<string>();

            try
            {
                var path = Path.Combine(Path.Combine(root, key), StampFile);
                if (!File.Exists(path)) return owed;

                foreach (var line in File.ReadAllLines(path).Skip(1))
                {
                    if (!line.StartsWith(OwedSidesPrefix, StringComparison.Ordinal)) continue;

                    foreach (var part in line.Substring(OwedSidesPrefix.Length).Split(','))
                    {
                        var dir = part.Trim().ToUpperInvariant();

                        if (SideDirs.Contains(dir) && !owed.Contains(dir)) owed.Add(dir);
                    }
                }
            }
            catch
            {
                owed.Clear();
            }

            return owed;
        }

        /// <summary>The atlas pages the set in this map's folder is still owed - see <see cref="OwedPrefix"/>.
        /// Empty when none are, or the stamp file has no second line, or it cannot be read.</summary>
        private static List<int> HeldMissingPages(string root, string key)
        {
            var owed = new List<int>();

            try
            {
                var path = Path.Combine(Path.Combine(root, key), StampFile);
                if (!File.Exists(path)) return owed;

                foreach (var line in File.ReadAllLines(path).Skip(1))
                {
                    if (!line.StartsWith(OwedPrefix, StringComparison.Ordinal)) continue;

                    foreach (var part in line.Substring(OwedPrefix.Length).Split(','))
                        if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) &&
                            page >= 0 && page < MaxAtlasPages && !owed.Contains(page))
                            owed.Add(page);
                }
            }
            catch
            {
                owed.Clear();
            }

            return owed;
        }

        /// <summary>
        /// Fetches the atlas pages a held set is still owed, on their own - the set is otherwise exactly the
        /// host's, so nothing else is asked for - and writes each into the map's folder and its meta. Pages that
        /// fail AGAIN for a reason a retry could change stay owed; a page that arrives wrong, or that the host's
        /// meta no longer names, stops being owed. Returns the bytes written.
        ///
        /// The meta is rewritten beside itself and REPLACED in one step, so a reader sees the old meta or the new
        /// one; the page file lands before the meta names it.
        /// </summary>
        private static long RefetchPages(string root, string key, MapIndexEntryDto entry, List<int> owed,
            List<string> owedSides, SyncResult result)
        {
            long written = 0;
            var still = new List<int>();
            var stillSides = new List<string>();

            try
            {
                var folder = Path.Combine(root, key);
                var metaPath = Path.Combine(folder, key + MetaSuffix);
                var local = File.Exists(metaPath)
                    ? JsonConvert.DeserializeObject<MapCaptureMetaDto>(File.ReadAllText(metaPath))
                    : null;

                if (local == null)
                {
                    WriteStamp(root, key, entry.Stamp, result);
                    return 0;
                }

                // Review F29: the SIDES owed, each fetched alone and held to what the host's meta says of it.
                var sides = local.Sides ?? new List<MapCaptureSideDto>();

                foreach (var dir in owedSides)
                {
                    var hostSide = entry.Meta?.Sides?.Find(sd => sd != null &&
                                                                  string.Equals(sd.Dir, dir, StringComparison.OrdinalIgnoreCase));

                    if (hostSide == null || sides.Exists(sd => sd != null && string.Equals(sd.Dir, dir, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var picture = FetchSide(key, dir, hostSide, entry.Stamp, result, out var sideReplaced, out var sideTransient);

                    if (sideReplaced) return written;

                    if (picture == null)
                    {
                        if (sideTransient) stillSides.Add(dir);
                        continue;
                    }

                    var name = SideFileName(key, dir);

                    File.WriteAllBytes(Path.Combine(folder, name), picture);

                    hostSide.Dir = dir;
                    hostSide.File = name;
                    sides.Add(hostSide);
                    written += picture.Length;
                }

                local.Sides = sides.Count == 0 ? null : SideDirs
                    .Select(d => sides.Find(sd => sd != null && string.Equals(sd.Dir, d, StringComparison.OrdinalIgnoreCase)))
                    .Where(sd => sd != null).ToList();

                // The PAGES owed - only while the set in place has its mesh, which is all a page dresses.
                var pages = local.Atlas ?? new List<MapCaptureAtlasDto>();

                foreach (var number in local.Mesh != null && entry.Mesh != null && entry.Meta?.Atlas != null ? owed : new List<int>())
                {
                    var hostPage = entry.Meta.Atlas.Find(p => p != null && p.Page == number);

                    if (hostPage == null || pages.Exists(p => p != null && p.Page == number)) continue;

                    var picture = FetchAtlasPage(key, hostPage, entry.Stamp, result, out var replaced, out var transient);

                    // The host's set changed under us: the whole set is taken again, so nothing is owed here.
                    if (replaced) return written;

                    if (picture == null)
                    {
                        if (transient) still.Add(number);
                        continue;
                    }

                    var name = AtlasFileName(key, number);

                    File.WriteAllBytes(Path.Combine(folder, name), picture);

                    pages.Add(new MapCaptureAtlasDto
                    {
                        File = name,
                        Page = hostPage.Page,
                        Width = hostPage.Width,
                        Height = hostPage.Height,
                        Tiles = hostPage.Tiles,
                        Sha256 = hostPage.Sha256
                    });

                    written += picture.Length;
                }

                if (written > 0)
                {
                    local.Atlas = pages.Count == 0 ? null : pages.Where(p => p != null).OrderBy(p => p.Page).ToList();

                    var temp = metaPath + ".tmp";

                    File.WriteAllText(temp, JsonConvert.SerializeObject(local, Formatting.Indented));
                    File.Replace(temp, metaPath, null);

                    result.Info.Add(
                        $"QuestTree: {key}'s missing picture(s) arrived from the host - {Mb(written)} MB" +
                        $"{(still.Count + stillSides.Count == 0 ? "" : $", {still.Count + stillSides.Count} still owed")}.");
                }

                WriteStamp(root, key, entry.Stamp, result, still, stillSides);
            }
            catch (Exception ex)
            {
                // The set in place is untouched or has the pages that landed; what is still owed stays owed.
                result.Debug.Add($"QuestTree: {key}'s missing atlas pages could not be taken ({ex.GetType().Name}: {ex.Message}).");
            }

            return written;
        }

        /// <summary>Whether the sync may start on another map: always within <see cref="SyncBudget"/>, and past
        /// it only while the session's average is still <see cref="MinSyncBytesPerSecond"/> or better.
        /// Internal and pure so the client harness can check it.</summary>
        internal static bool MaySyncGoOn(TimeSpan elapsed, long bytesSoFar) =>
            elapsed <= SyncBudget ||
            (elapsed.TotalSeconds > 0 && bytesSoFar / elapsed.TotalSeconds >= MinSyncBytesPerSecond);

        /// <summary>
        /// Makes room in the host-picture cache for a set of <paramref name="incoming"/> bytes that will replace
        /// whatever <paramref name="key"/>'s folder holds now - see <see cref="HostCacheCeiling"/>. Evicts the
        /// sets whose stamp was written longest ago, never one in <paramref name="keep"/> (taken this session)
        /// and never this map's own. False, with a line, when the set cannot fit even then.
        /// </summary>
        private static bool MakeRoom(string root, string key, long incoming, HashSet<string> keep, SyncResult result)
        {
            try
            {
                var sets = HostSets(root);
                var total = sets.Where(s => !string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Bytes);

                if (total + incoming <= HostCacheCeiling) return true;

                foreach (var set in sets.Where(s => !string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase) &&
                                                   !keep.Contains(s.Key))
                             .OrderBy(s => s.Written))
                {
                    if (total + incoming <= HostCacheCeiling) break;

                    Wipe(set.Folder);

                    if (Directory.Exists(set.Folder)) continue;

                    total -= set.Bytes;
                    result.Info.Add(
                        $"QuestTree: the host's pictures of {set.Key} were removed from this machine ({Mb(set.Bytes)} MB) " +
                        $"to keep the host-picture cache under {Mb(HostCacheCeiling)} MB - they come back the next time " +
                        "there is room.");
                }

                if (total + incoming <= HostCacheCeiling) return true;

                result.Debug.Add(
                    $"QuestTree: the host's {key} ({Mb(incoming)} MB) does not fit in the {Mb(HostCacheCeiling)} MB " +
                    "host-picture cache - it waits.");
                return false;
            }
            catch (Exception ex)
            {
                // Cannot measure: take it, as a machine with no cap would.
                result.Debug.Add($"QuestTree: the host-picture cache could not be measured ({ex.Message}).");
                return true;
            }
        }

        /// <summary>The host sets in the cache - folders with a stamp file - with what each weighs and when its
        /// stamp was written.</summary>
        private static List<(string Key, string Folder, long Bytes, DateTime Written)> HostSets(string root)
        {
            var sets = new List<(string, string, long, DateTime)>();

            if (!Directory.Exists(root)) return sets;

            foreach (var folder in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(folder);
                var stamp = Path.Combine(folder, StampFile);

                if (name.StartsWith(".", StringComparison.Ordinal) || !File.Exists(stamp)) continue;

                var bytes = Directory.GetFiles(folder).Sum(f => new FileInfo(f).Length);

                sets.Add((name, folder, bytes, File.GetLastWriteTimeUtc(stamp)));
            }

            return sets;
        }

        /// <summary>
        /// Whether this machine's OWN capture of a map is newer than the host's, in which case the
        /// host's copy is not taken.
        ///
        /// A capture from this install is of this install's version of the map, and the player who
        /// took it did so deliberately. The host's set is for the maps nobody here has raided. The
        /// comparison is by capturedAt and nothing else: a host's set with no timestamp, or one this
        /// side cannot parse, loses - which errs towards keeping the picture that is already being
        /// drawn.
        ///
        /// The ALIASED id is tried second, exactly as the reader tries it (MapCatalog.EntryFor): the
        /// host folds the two ids of a pair onto one and offers the set back under that one, while
        /// the capture on this disk is filed under whichever id the raid had - see
        /// <see cref="AliasOf"/>, where the download that costs is written down.
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="entry">The host's entry.</param>
        /// <param name="result">Where the line goes.</param>
        private static bool LocalCaptureIsNewer(string key, MapIndexEntryDto entry, SyncResult result)
        {
            try
            {
                var path = OwnCaptureMeta(key) ?? OwnCaptureMeta(AliasOf(key));
                if (path == null) return false;

                var mine = JsonConvert.DeserializeObject<MapCaptureMetaDto>(File.ReadAllText(path));

                var ours = Timestamp(mine?.CapturedAt);
                var theirs = Timestamp(entry.CapturedAt ?? entry.Meta?.CapturedAt);

                if (!ours.HasValue) return false;

                // EQUAL counts as ours: the host's set with this machine's capture instant is, in every case
                // that happens, the set this machine uploaded - taking it back would be up to a map's whole ceiling of our own
                // pictures down the wire, to be drawn second to the local capture anyway.
                if (!theirs.HasValue || ours.Value >= theirs.Value)
                {
                    result.Debug.Add(
                        ours.Value == theirs
                            ? $"QuestTree: the host's set of {key} is this machine's own capture, so it is not taken back."
                            : $"QuestTree: this machine's own capture of {key} is the newer one, so the host's is not taken.");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                // Cannot tell: take the host's, which is the behaviour of a machine that has no
                // capture at all, and the meta that could not be read was not going to draw anything.
                result.Debug.Add($"QuestTree: could not compare the captures of {key} ({ex.Message}).");
                return false;
            }
        }

        /// <summary>This machine's own capture meta for a map, or null when there is none to read.</summary>
        /// <param name="key">The map's internal id, or null.</param>
        private static string OwnCaptureMeta(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var dir = CaptureDir(key);
            if (dir == null) return null;

            var path = Path.Combine(dir, key + MetaSuffix);
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The other id of the pair that loads the same scene as this one, or null when this map has no
        /// twin - the same lookup MapCatalog.AliasOf makes, over the same table.
        ///
        /// Needed because a capture is written under the location id the RAID had - factory4_night,
        /// Sandbox_high - while the host FOLDS an upload onto the canonical id of the pair
        /// (ZoneStore.Canonical) and offers it back under that. So a player who captures Factory at
        /// night and offers it up is then told the host holds "factory4_day", finds no
        /// captures/factory4_day folder, concludes this machine has no capture of that map and
        /// downloads its own pictures back over the wire. Nothing is drawn wrongly - the reader tries
        /// the alias too and prefers the local capture either way - but the download is pure waste,
        /// and this method's own sentence ("whether this machine's OWN capture is newer") was false
        /// for two of the eleven vanilla maps.
        ///
        /// The table is MapView's, read rather than copied for the reason MapCatalog gives: a third
        /// copy is a third thing to keep in step with the server's. Safe from the sync worker although
        /// it is a UI type: the pairs are a plain managed array and every static beside them is a
        /// string, a bool or a Color struct, and the sync is started from the Maps tab's own first
        /// resolve (MapCatalog.HostCache), so MapView's statics are already initialised before this
        /// thread exists.
        /// </summary>
        /// <param name="key">The map's internal id, or null.</param>
        private static string AliasOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            foreach (var (a, b) in UI.MapView.SceneAliases)
            {
                if (string.Equals(a, key, StringComparison.OrdinalIgnoreCase)) return b;
                if (string.Equals(b, key, StringComparison.OrdinalIgnoreCase)) return a;
            }

            return null;
        }

        /// <summary>An ISO UTC timestamp, or null. Invariant and round-tripped: these are written
        /// with a Z and have to mean the same instant on every machine that reads them.</summary>
        /// <param name="value">The timestamp text.</param>
        private static DateTime? Timestamp(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        /// <summary>Removes a staging folder and everything in it. Never throws: a staging folder
        /// left behind costs one folder, and the next download wipes it first.</summary>
        /// <param name="folder">The folder to remove.</param>
        private static void Wipe(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Deliberately silent - see the summary.
            }
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>The meta file's ending, the same one MapCapture writes and MapCatalog reads.
        /// Named after the map so a folder copied somewhere else still says what it is.</summary>
        private const string MetaSuffix = ".map.json";

        /// <summary>
        /// BepInEx/plugins/QuestTree/maps/, where the HOST's picture sets are kept - one folder per
        /// map, holding the pictures, the meta and the stamp.
        ///
        /// Beside captures/ rather than inside it, because the two are different things: captures/ is
        /// what this machine photographed and is what package.ps1 ships, maps/ is a cache of someone
        /// else's work that this mod may replace wholesale. The reader reads both (MapCatalog) and
        /// takes the local one first.
        ///
        /// Created on demand by the writers below; null when the plugin has no file location, the
        /// case KappaQuests and MapCapture guard the same way.
        /// </summary>
        internal static string MapsRoot()
        {
            try
            {
                var modPath = Path.GetDirectoryName(typeof(MapTransfer).Assembly.Location);
                return string.IsNullOrEmpty(modPath) ? null : Path.Combine(modPath, "maps");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>BepInEx/plugins/QuestTree/captures/(key)/ - this machine's own capture of a map,
        /// which is what an upload reads and what a download is measured against. The third copy of
        /// this rule, with MapCapture.CaptureDir (which creates it) and MapCatalog.CapturesRoot
        /// (which reads it); NOT created here, for MapCatalog's reason - a reader that makes the
        /// folder makes a missing capture look like a failed one.</summary>
        /// <param name="key">The map's internal id.</param>
        private static string CaptureDir(string key)
        {
            try
            {
                var modPath = Path.GetDirectoryName(typeof(MapTransfer).Assembly.Location);
                return string.IsNullOrEmpty(modPath)
                    ? null
                    : Path.Combine(Path.Combine(modPath, "captures"), key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether a name from the host can be a folder on this disk: the same rule
        /// MapCapture.IsUsableKey applies to a map name before it writes one, because a key from the
        /// network becomes a path here. Letters, digits, dash and underscore, 1 to 64 of them.</summary>
        /// <param name="key">The map name as the host spells it.</param>
        private static bool IsUsableKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            var trimmed = key.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 64) return false;

            foreach (var c in trimmed)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                         c == '-' || c == '_';
                if (!ok) return false;
            }

            return true;
        }

        /// <summary>Marks a request this code has stopped waiting for as observed, so a fault it ends with later
        /// is not an UnobservedTaskException on the finalizer thread (review F33) - the job QuestDataClient.Abandon
        /// does for its own requests.</summary>
        private static void Observe(Task task)
        {
            try
            {
                if (task == null) return;

                if (task.IsCompleted)
                {
                    var ignored = task.Exception;
                    return;
                }

                task.ContinueWith(t => { var ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            catch
            {
                // Observing is best effort.
            }
        }

        /// <summary>A GET on this thread, with a deadline. For the worker only - it blocks.</summary>
        /// <param name="route">The route to ask.</param>
        private static string Get(string route)
        {
            var request = RequestHandler.GetJsonAsync(route);

            if (!request.Wait(RequestTimeout))
            {
                Observe(request);
                throw new TimeoutException($"no answer from {route} within {RequestTimeout.TotalSeconds:0}s");
            }

            return request.Result;
        }

        /// <summary>
        /// A stand-in for the host, for the client harness only: when set, <see cref="Post"/> hands the route and
        /// body to it instead of the transfer client, which cannot run outside the game. It is how the download's
        /// failure rules (review F29: a floor or side that did not ARRIVE) are proven against answers the harness
        /// chooses - one floor served, the next timing out. Nothing in the mod sets it.
        /// </summary>
        internal static Func<string, string, string> PostForTests { get; set; }

        /// <summary>A POST on this thread, with a deadline. For the worker only - it blocks.</summary>
        /// <param name="route">The route to ask.</param>
        /// <param name="body">The JSON body.</param>
        /// <param name="timeout">The request's deadline. <see cref="RequestTimeout"/> unless the answer is
        /// megabytes, as a mesh's or an atlas page's is.</param>
        private static string Post(string route, string body, TimeSpan? timeout = null)
        {
            // The test seam - see PostForTests. Null in the game, always.
            var seam = PostForTests;
            if (seam != null) return seam(route, body);

            var deadline = timeout ?? RequestTimeout;
            var request = Send(route, body, deadline);

            // A backstop only: the request aborts itself at the deadline (review F57), so this wait ends with it.
            if (!request.Wait(deadline + AbortGrace))
            {
                Observe(request);
                throw new TimeoutException($"no answer from {route} within {deadline.TotalSeconds:0}s");
            }

            return request.Result;
        }

        /// <summary>Every map-transfer POST (review F57). The task ends by the deadline - completed, faulted, or
        /// faulted with TimeoutException - unless DedicatedTransferClient is off, with one exception the callers
        /// backstop: on Mono the deadline's token aborts the request up to and including its headers, but a body
        /// that stalls after them is read under the socket's own read timeout (300 s), which the token does not
        /// cut short. Every wait on one of these tasks therefore gives up at the deadline plus AbortGrace
        /// (<see cref="GiveUp"/>, and Post's Wait) rather than trusting the task to end.</summary>
        /// <param name="route">The route to post to.</param>
        /// <param name="json">The JSON body.</param>
        /// <param name="deadline">When the request is aborted.</param>
        private static Task<string> Send(string route, string json, TimeSpan deadline) =>
            DedicatedTransferClient
                ? TransferHttp.PostJsonAsync(route, json, deadline)
                : RequestHandler.PostJsonAsync(route, json);

        /// <summary>A request still running past its deadline and grace, given up: the late task is observed
        /// (its eventual fault is nobody's), and the caller gets a faulted task shaped exactly like a deadline
        /// abort, so every path after it - the log line, JudgeMesh, the empty re-post - reads it as the timeout it
        /// is. A task that has ended is returned as it is.</summary>
        /// <param name="task">The request.</param>
        /// <param name="route">Its route, for the message.</param>
        /// <param name="deadline">Its deadline, for the message.</param>
        private static Task<string> GiveUp(Task<string> task, string route, TimeSpan deadline)
        {
            if (task == null || task.IsCompleted) return task;
            Observe(task);
            return Task.FromException<string>(new TimeoutException($"no answer from {route} within {deadline.TotalSeconds:0}s"));
        }

        /// <summary>Whether a finished request ended at its deadline.</summary>
        /// <param name="task">The finished request.</param>
        private static bool TimedOut(Task task) =>
            task != null && task.IsFaulted && task.Exception?.GetBaseException() is TimeoutException;

        /// <summary>
        /// The HTTP client every map transfer goes through (review F57). Built the way SPT's own Client is - no
        /// cookie container and the PHPSESSID header by hand, every certificate accepted (an SPT host's is
        /// self-signed), the body a zlib stream at SPT's own level and the answer inflated only when it sniffs
        /// as zlib - so the host sees exactly what RequestHandler would have sent. Two differences, and they
        /// are the point: NO retry, and a deadline per request that ABORTS it. SPT's client re-sends any failed
        /// request up to three more times, each under a 100 s default timeout, which made a 240 s transfer
        /// impossible, a failure ~400 s long, and every request this class stopped waiting for a zombie that
        /// kept the host reading and encoding in the background.
        ///
        /// Built on first use, never in MapTransfer's type initialiser: RequestHandler's static constructor
        /// reads the game's command line, and the client harness loads MapTransfer with no game.
        ///
        /// No per-request log line, deliberately: the download worker must not touch statics beyond what it
        /// returns, and every caller already logs every outcome.
        /// </summary>
        private static class TransferHttp
        {
            private static readonly Lazy<HttpClient> Client =
                new Lazy<HttpClient>(Build, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

            private static HttpClient Build()
            {
                var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true
                };

                // Set ONCE, here - HttpClient refuses a change after its first request. A backstop above the
                // longest per-request deadline - a whole mesh coming down, up to MeshDownloadMaxSeconds (WP7) -
                // and the deadline that actually applies is each request's own.
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(MeshDownloadMaxSeconds + 30d) };
            }

            /// <summary>POSTs a JSON body and returns the answer's text, or throws. A TimeoutException naming the
            /// route and the deadline when the deadline passed - the request is aborted then, not left running.</summary>
            /// <param name="route">The route, appended to SPT's backend address.</param>
            /// <param name="json">The JSON body.</param>
            /// <param name="deadline">When the request is aborted. It starts after the compression, so it
            /// measures the network alone.</param>
            internal static async Task<string> PostJsonAsync(string route, string json, TimeSpan deadline)
            {
                var host = RequestHandler.Host;
                if (string.IsNullOrEmpty(host)) throw new InvalidOperationException("SPT gave no backend address");

                // SPT's own body: UTF-8, zlib at SPT's level (Maximum) - what SPT's Client sends, with no content
                // type and no header saying it is compressed, so nothing depends on how the host's listener
                // decides; and the level the MeshPartBytes arithmetic was measured on.
                var body = SPT.Common.Utils.Zlib.Compress(
                    System.Text.Encoding.UTF8.GetBytes(json), SPT.Common.Utils.ZlibCompression.Maximum);

                using (var cts = new System.Threading.CancellationTokenSource(deadline))
                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(host + route)))
                {
                    request.Headers.Add("Cookie", "PHPSESSID=" + RequestHandler.SessionId);

                    // A fresh connection per transfer: a pooled one the host has already closed fails on first
                    // use, and SPT's retry is what used to hide that. A TCP/TLS handshake is nothing next to
                    // megabytes.
                    request.Headers.ConnectionClose = true;
                    request.Content = new ByteArrayContent(body);

                    try
                    {
                        // ResponseContentRead: the token covers reading the body too, which is then read from
                        // the buffer.
                        using (var response = await Client.Value
                                   .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                                   .ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                                throw new HttpRequestException($"Http response status code: {response.StatusCode}");

                            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            if (SPT.Common.Utils.Zlib.IsCompressed(bytes)) bytes = SPT.Common.Utils.Zlib.Decompress(bytes);
                            return bytes == null ? "" : System.Text.Encoding.UTF8.GetString(bytes);
                        }
                    }
                    catch (Exception) when (cts.IsCancellationRequested)
                    {
                        // Whatever the handler threw once the deadline fired - Mono's reports an abort as a
                        // cancellation or as a WebException(RequestCanceled) depending on where it was - it is
                        // this deadline, and it is reported as one.
                        throw new TimeoutException($"no answer from {route} within {deadline.TotalSeconds:0}s");
                    }
                }
            }
        }

        /// <summary>
        /// A reply deserialised, or null when it is not this mod's to read - the check every route
        /// here needs, because SPT answers a route no mod registered with the launcher's own HTML and
        /// an empty body, neither of which is a failure worth a warning.
        ///
        /// The excerpt is for the log line: a hundred and twenty characters of whatever came back is
        /// what tells a missing server half from a wrong route from a real error.
        /// </summary>
        /// <typeparam name="T">The mirror to read it as.</typeparam>
        /// <param name="reply">Whatever came back.</param>
        /// <param name="excerpt">A phrase describing the reply, for the log.</param>
        private static T NotOurs<T>(string reply, out string excerpt) where T : class
        {
            if (string.IsNullOrEmpty(reply))
            {
                excerpt = "(empty reply - server half missing or predates this route)";
                return null;
            }

            excerpt = reply.Length <= 120 ? reply : reply.Substring(0, 120) + "...";

            try
            {
                return JsonConvert.DeserializeObject<T>(reply);
            }
            catch (Exception ex)
            {
                excerpt = $"{excerpt} ({ex.GetType().Name})";
                return null;
            }
        }

        /// <summary>" (the host's words)", or "" when it gave none.</summary>
        /// <param name="reason">What the host said.</param>
        private static string Because(string reason) =>
            string.IsNullOrEmpty(reason) ? "" : $" ({reason.Trim()})";

        /// <summary>Bytes as megabytes to one place, invariant - these go in log lines a player
        /// reads.</summary>
        /// <param name="bytes">A byte count.</param>
        private static string Mb(long bytes) =>
            (bytes / (1024f * 1024f)).ToString("0.0", CultureInfo.InvariantCulture);
    }
}
