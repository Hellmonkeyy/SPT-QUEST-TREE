using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        /// <summary>The longest side, in pixels, a picture may have on the wire. A capture is taken
        /// at up to 4096 for this machine's own screen; what is shared is the smaller copy, because
        /// the difference between 2048 and 4096 is four times the bytes for detail that only shows at
        /// a zoom the map view rarely reaches.</summary>
        private const int MaxLongSide = 2048;

        /// <summary>JPEG quality for an uploaded floor. 80 is the plan's measured compromise: a
        /// 2048-side floor lands at 0.5-1.2 MB, and the artefacts are invisible under the pins.</summary>
        private const int JpegQuality = 80;

        /// <summary>The most one encoded floor may weigh before this side declines to offer it.
        /// Mirrors the host's own per-floor ceiling: a floor over it would be rejected, and a
        /// rejection stops the whole upload, so the client skips the floor instead and still offers
        /// the rest.</summary>
        private const int MaxFloorUploadBytes = 2500 * 1024;

        /// <summary>The most one floor may weigh coming DOWN, decoded. Nothing this mod uploads comes
        /// near it; it is a guard against a host - or something answering as one - filling the disk.</summary>
        private const int MaxFloorDownloadBytes = 8 * 1024 * 1024;

        /// <summary>The most one map's pictures may weigh coming down, decoded. The plan's per-map
        /// ceiling on the host side, checked again here.</summary>
        private const long MaxMapDownloadBytes = 20L * 1024 * 1024;

        /// <summary>The most a session will download in total. A player who joins a host holding
        /// thirty maps gets what fits and the rest on the next start, rather than a quarter of an hour
        /// of a worker on the first Maps tab open.</summary>
        private const long MaxSessionDownloadBytes = 60L * 1024 * 1024;

        /// <summary>The most floors of one map to take from a host, matching the harvested band
        /// ceiling the zone file enforces.</summary>
        private const int MaxFloors = 8;

        /// <summary>How long any one request may take. The same cap QuestDataClient applies, doubled:
        /// these bodies are a megabyte of base64 rather than a few kilobytes of JSON.</summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The upload's own per-request wait, in seconds, for the coroutine that polls it.
        /// The same number as <see cref="RequestTimeout"/> - both are the deadline on one post, one
        /// measured by the worker and one by the frames.</summary>
        private const float RequestSeconds = 30f;

        /// <summary>How long the whole download may take before it gives up and leaves the rest for
        /// the next session. A worker that never returns is one the session can never retry.</summary>
        private static readonly TimeSpan SyncBudget = TimeSpan.FromMinutes(3);

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
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: an upload is already running, so the capture of {key} is not offered now - " +
                        "it goes up after the next capture, or when the host is next asked for its maps.");
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
                if (!ReadCapture(key, out var meta, out var floors)) yield break;

                // BEFORE the first post, and for every floor at once - see DescribeWire. The meta
                // travels with every post and the host checks EVERY floor in it against the meta's
                // own scale, so rewriting one floor at a time would make the first post describe no
                // single projection and be refused outright.
                if (!DescribeWire(key, meta, floors)) yield break;

                var posted = 0;
                long bytes = 0;

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
                        continue;
                    }

                    var task = StartPost(key, meta, floor);
                    if (task == null) yield break;

                    var deadline = Time.realtimeSinceStartup + RequestSeconds;
                    while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;

                    if (!task.IsCompleted)
                    {
                        Plugin.LogSource?.LogInfo(
                            $"QuestTree: the host did not answer within {RequestSeconds:0}s while {key} " +
                            $"\"{floor.Name}\" was being offered - the rest of the capture is not sent.");
                        yield break;
                    }

                    var verdict = Judge(key, floor, task, out var held);
                    if (verdict == Verdict.Stop) yield break;

                    posted++;
                    bytes += floor.Bytes;

                    if (verdict == Verdict.Complete)
                    {
                        Done(key, Math.Max(posted, held), bytes, clock);
                        yield break;
                    }
                }

                // The loop ran out with the host never calling the set complete, so it is still
                // waiting for a floor. Since DescribeWire names exactly the floors that will be
                // offered, the only way here is a floor that failed to ENCODE after an earlier post
                // had already named it - the host then holds pieces it drops after a day. Said as
                // what it is rather than as a success: the player's next question is why the map never
                // appeared on the other machine, and "uploaded to the host" would answer it wrongly.
                if (posted > 0)
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {posted} floor(s) of {key} reached the host but it never had the whole set - a " +
                        "floor could not be encoded after the others had been sent, so the host discards them. " +
                        "Capture the map again.");
            }
            finally
            {
                _uploading = false;
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

            // Derived from the rewritten width, so the three numbers the host compares - the extent,
            // the scale and the pixel size - describe one projection.
            var metres = meta.Extent != null ? meta.Extent.MaxX - meta.Extent.MinX : 0d;
            if (metres > 0d) meta.PxPerMetre = (float)(width / metres);

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {key} is offered as {width}x{height} px at {meta.PxPerMetre:0.###} px/m, " +
                $"{floors.Count} floor(s).");

            return true;
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

                source = new Texture2D(2, 2, TextureFormat.RGB24, mipChain: false);

                // Readable on purpose (no markNonReadable): EncodeToJPG and the orientation check
                // below both read pixels back.
                if (!source.LoadImage(bytes))
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" could not be decoded from disk " +
                        $"({bytes.Length} bytes) and is not offered to the host.");
                    return false;
                }

                ScaleTo(source.width, source.height, MaxLongSide, out var width, out var height);

                var encodeFrom = source;

                if (width != source.width || height != source.height)
                {
                    render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);

                    // The GPU does the filtering. The alternative - GetPixels32 and a bilinear loop -
                    // is 58 MB of managed array and a few hundred milliseconds on the thread drawing
                    // frames, for the same picture.
                    Graphics.Blit(source, render);

                    RenderTexture.active = render;

                    scaled = new Texture2D(width, height, TextureFormat.RGB24, mipChain: false);
                    scaled.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                    scaled.Apply(updateMipmaps: false);

                    encodeFrom = scaled;

                    CheckOrientation(key, floor, source, scaled);
                }

                // The check that can fail, and the one that keeps the shared meta honest: every post
                // carries the same meta, whose floors DescribeWire has already measured, so a picture
                // that came out a different size cannot be described - it can only be left out.
                if (encodeFrom.width != floor.Entry.Width || encodeFrom.height != floor.Entry.Height)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" came out {encodeFrom.width}x{encodeFrom.height} px " +
                        $"where its meta says {floor.Entry.Width}x{floor.Entry.Height} - the capture's meta does " +
                        "not describe its own pictures, so this floor is not offered. Capture the map again.");
                    return false;
                }

                var jpg = encodeFrom.EncodeToJPG(JpegQuality);

                if (jpg == null || jpg.Length == 0)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: {key} \"{floor.Name}\" encoded to nothing and is not offered to the host.");
                    return false;
                }

                if (jpg.Length > MaxFloorUploadBytes)
                {
                    // Skipped rather than sent: the host would reject it, and a rejection stops the
                    // whole upload - see MaxFloorUploadBytes.
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {key} \"{floor.Name}\" is {Mb(jpg.Length)} MB as a JPEG, over the " +
                        $"{Mb(MaxFloorUploadBytes)} MB a host takes per floor - it is not offered. The other " +
                        "floors still are.");
                    return false;
                }

                floor.Base64 = Convert.ToBase64String(jpg);
                floor.Bytes = jpg.Length;

                Plugin.LogSource?.LogDebug(
                    $"QuestTree: {key} \"{floor.Name}\" {source.width}x{source.height} -> " +
                    $"{encodeFrom.width}x{encodeFrom.height} JPEG q{JpegQuality}, {Mb(jpg.Length)} MB.");

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
        internal static void ScaleTo(
            int width, int height, int maxLongSide, out int scaledWidth, out int scaledHeight)
        {
            scaledWidth = width;
            scaledHeight = height;

            if (width <= 0 || height <= 0 || maxLongSide <= 0) return;
            if (width <= maxLongSide && height <= maxLongSide) return;

            if (width >= height)
            {
                scaledWidth = maxLongSide;
                scaledHeight = Math.Max(1, (int)Math.Round(height * (double)maxLongSide / width, MidpointRounding.AwayFromZero));
            }
            else
            {
                scaledHeight = maxLongSide;
                scaledWidth = Math.Max(1, (int)Math.Round(width * (double)maxLongSide / height, MidpointRounding.AwayFromZero));
            }
        }

        /// <summary>The post of one floor, issued on a pool thread, or null when the pool would not
        /// take it. The serialisation goes on the worker too: the body is a megabyte of base64 and
        /// building it is a frame.</summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="meta">The meta to send with this floor - see MapUploadRequest.Meta.</param>
        /// <param name="floor">The floor whose picture is ready.</param>
        private static Task<string> StartPost(string key, MapCaptureMetaDto meta, FloorUpload floor)
        {
            var request = new MapUploadRequest
            {
                SchemaVersion = MapUploadRequest.CurrentSchemaVersion,
                Map = key,
                ClientVersion = ModInfo.Version,
                Meta = meta,
                Level = floor.Level,
                Format = "jpg",
                ImageBase64 = floor.Base64
            };

            try
            {
                // The meta object is shared by every post of this capture and is rewritten between
                // them (see Encode), which is safe for exactly one reason: the coroutine waits for
                // each post to finish before the next floor is touched, so no worker is ever reading
                // it while the main thread writes it.
                return Task.Run(async () =>
                {
                    var json = JsonConvert.SerializeObject(request);
                    return await RequestHandler.PostJsonAsync(UploadRoute, json);
                });
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
        private static Verdict Judge(string key, FloorUpload floor, Task<string> task, out int floorsHeld)
        {
            floorsHeld = 0;

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
        /// <param name="bytes">What was sent.</param>
        /// <param name="clock">Running since the upload started.</param>
        private static void Done(string key, int floors, long bytes, System.Diagnostics.Stopwatch clock)
        {
            Plugin.LogSource?.LogInfo(
                $"QuestTree: capture of {key} uploaded to the host - {floors} floor(s), {Mb(bytes)} MB.");

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {key} was uploaded in {clock.ElapsedMilliseconds} ms.");
        }

        /// <summary>One floor on its way up.</summary>
        private sealed class FloorUpload
        {
            public int Level;
            public string Name = "";
            public string Path = "";

            /// <summary>This floor's entry in the meta being sent, rewritten by <see cref="Encode"/>
            /// to describe the picture actually going up rather than the one on disk.</summary>
            public MapCaptureFloorDto Entry;

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

                foreach (var entry in index.Maps)
                {
                    if (clock.Elapsed > SyncBudget)
                    {
                        result.Debug.Add(
                            "QuestTree: the host's map pictures took longer than this session allows - the rest " +
                            "are fetched on the next start.");
                        break;
                    }

                    if (entry == null || !IsUsableKey(entry.Map)) continue;

                    var key = entry.Map.Trim();

                    if (string.IsNullOrEmpty(entry.Stamp)) continue;
                    if (entry.Meta == null || entry.Meta.Floors == null || entry.Meta.Floors.Count == 0) continue;

                    // Already have exactly this set.
                    if (string.Equals(HeldStamp(root, key), entry.Stamp, StringComparison.Ordinal)) continue;

                    if (LocalCaptureIsNewer(key, entry, result)) continue;

                    if (budget + Math.Max(entry.Bytes, 0L) > MaxSessionDownloadBytes)
                    {
                        result.Debug.Add(
                            $"QuestTree: {Mb(MaxSessionDownloadBytes)} MB of the host's map pictures is all this " +
                            $"session takes, so {key} waits for the next one.");
                        break;
                    }

                    var bytes = Download(root, key, entry, result);
                    if (bytes <= 0) continue;

                    budget += bytes;
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

                    var picture = Fetch(key, floor.Level, entry.Stamp, result, out var name, out var replaced);

                    // The set was replaced on the host while it was being fetched. ABANDONED, not
                    // continued: the floors already in hand are the old set's and the ones left are
                    // the new set's, so carrying on would swap a subset of the old set over whatever
                    // this machine has - replacing a complete four-floor map with two floors of it.
                    // Nothing has been moved out of the staging folder yet, so returning here leaves
                    // the map exactly as it was, and the stamp this machine holds still differs from
                    // the host's new one, which is what makes the next session take the whole set.
                    if (replaced) return 0;

                    if (picture == null) continue;

                    if (bytes + picture.Length > MaxMapDownloadBytes)
                    {
                        result.Debug.Add(
                            $"QuestTree: the host's pictures of {key} are over the {Mb(MaxMapDownloadBytes)} MB a map " +
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

                // The meta written out names exactly the floors whose picture is in the staging
                // folder. A floor the host could not send is dropped rather than named: the reader
                // would drop it anyway, and a meta naming a file that is not there is how a set
                // comes to look half-written.
                meta.Map = key;
                meta.Floors = floors;

                File.WriteAllText(
                    Path.Combine(staging, key + MetaSuffix),
                    JsonConvert.SerializeObject(meta, Formatting.Indented));

                if (!Swap(root, key, staging, floors, entry.Stamp, result)) return 0;

                result.Info.Add(
                    $"QuestTree: map picture set for {key} received from the host - {floors.Count} floor(s), " +
                    $"{Mb(bytes)} MB.");

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
        /// <param name="stamp">The host's name for this set, written last of all.</param>
        /// <param name="result">Where a failure's line goes.</param>
        private static bool Swap(
            string root, string key, string staging, List<MapCaptureFloorDto> floors, string stamp,
            SyncResult result)
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
                WriteStamp(root, key, stamp, result);

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
        private static void WriteStamp(string root, string key, string stamp, SyncResult result)
        {
            try
            {
                var path = Path.Combine(Path.Combine(root, key), StampFile);
                var temp = path + ".tmp";

                File.WriteAllText(temp, stamp);
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
        private static byte[] Fetch(
            string key, int level, string stamp, SyncResult result, out string name, out bool replaced)
        {
            name = null;
            replaced = false;

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
                    // The host does not have that floor. Not an error: the set lands without it and
                    // the reader simply has one fewer storey.
                    result.Debug.Add($"QuestTree: the host has no picture of {key} floor {level}.");
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
                result.Debug.Add(
                    $"QuestTree: the host's picture of {key} floor {level} could not be taken ({ex.Message}).");
                return null;
            }
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
                return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            }
            catch
            {
                return "";
            }
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
        /// </summary>
        /// <param name="key">The map's internal id.</param>
        /// <param name="entry">The host's entry.</param>
        /// <param name="result">Where the line goes.</param>
        private static bool LocalCaptureIsNewer(string key, MapIndexEntryDto entry, SyncResult result)
        {
            try
            {
                var dir = CaptureDir(key);
                if (dir == null) return false;

                var path = Path.Combine(dir, key + MetaSuffix);
                if (!File.Exists(path)) return false;

                var mine = JsonConvert.DeserializeObject<MapCaptureMetaDto>(File.ReadAllText(path));

                var ours = Timestamp(mine?.CapturedAt);
                var theirs = Timestamp(entry.CapturedAt ?? entry.Meta?.CapturedAt);

                if (!ours.HasValue) return false;
                if (!theirs.HasValue || ours.Value > theirs.Value)
                {
                    result.Debug.Add(
                        $"QuestTree: this machine's own capture of {key} is the newer one, so the host's is not taken.");
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

        /// <summary>A GET on this thread, with a deadline. For the worker only - it blocks.</summary>
        /// <param name="route">The route to ask.</param>
        private static string Get(string route)
        {
            var request = RequestHandler.GetJsonAsync(route);

            if (!request.Wait(RequestTimeout))
                throw new TimeoutException($"no answer from {route} within {RequestTimeout.TotalSeconds:0}s");

            return request.Result;
        }

        /// <summary>A POST on this thread, with a deadline. For the worker only - it blocks.</summary>
        /// <param name="route">The route to ask.</param>
        /// <param name="body">The JSON body.</param>
        private static string Post(string route, string body)
        {
            var request = RequestHandler.PostJsonAsync(route, body);

            if (!request.Wait(RequestTimeout))
                throw new TimeoutException($"no answer from {route} within {RequestTimeout.TotalSeconds:0}s");

            return request.Result;
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
