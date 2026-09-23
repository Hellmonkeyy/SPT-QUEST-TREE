using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Comfort.Common;
using EFT;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// THROWAWAY DIAGNOSTIC - Phase 3-0 of the low-poly 3D map work. IT IS REMOVED BEFORE RELEASE,
    /// together with <see cref="ModSettings.ProbeKey"/>, the one line in
    /// <see cref="QuestTree.Patches.GameWorldStartedPatch"/>, the install call in
    /// <see cref="Plugin"/>, the two internal Probe* accessors in <see cref="MapCapture"/>, and the
    /// README row and CHANGELOG bullet that describe the key. Nothing in the mod may come to depend
    /// on it: it writes one text file per press and, apart from the menu's own test view which it
    /// destroys again, changes nothing that outlives the frame.
    ///
    /// BUFFER TARGETS ARE NEVER WRITTEN - doing so crashed the engine on 2026-09-22
    /// (Mesh.set_vertexBufferTarget -> UnityPlayer -> d3d11). The first version of experiment 1 set
    /// `vertexBufferTarget |= Raw` on a scene mesh, read it, and assigned the original value back; the
    /// RESTORING assignment took the process down on the first mesh it reached inside Big Red on Customs,
    /// natively, with no managed exception anywhere in the stack. Nothing in this file - and nothing in the
    /// 3D feature that follows it - may assign vertexBufferTarget or indexBufferTarget on a mesh the mod
    /// does not own, and nothing may call a synchronous GetData on such a mesh's buffer either, because
    /// mapping a GPU-only buffer is plausibly the same native path. The targets are read and printed.
    ///
    /// It exists because four questions about a 3D map cannot be answered by reading code:
    ///
    ///   1. Can a scene mesh the game uploaded with isReadable = false still be read back from the
    ///      GPU? Asked now only of the buffer Unity is willing to hand over as the mesh already stands
    ///      (GetVertexBuffer, then AsyncGPUReadback on it). The buildings half of the plan depends on it
    ///      and has a relief-only fallback if not.
    ///   2. Do colliders stream out around the player the way renderers do? A 2 m raycast grid over
    ///      the whole map is the ground relief; if its coverage falls off with distance the relief
    ///      has to merge across captures the way the pictures do. Pressed once at spawn and once at
    ///      the far end, this measures it in 100 m rings.
    ///   3. Which shaders are loaded, which cameras exist and which layer has NO renderer on it -
    ///      the private layer a second camera can draw into without the hideout ever seeing it.
    ///   4. Does the display path work at all: a RawImage over a RenderTexture, filled by
    ///      Graphics.DrawMesh into a disabled camera that is then rendered by hand.
    ///
    /// Every experiment is wrapped in its own try/catch and writes "EXPERIMENT n FAILED: ..."
    /// instead of taking the raid or the menu down with it, and every heavy loop yields between
    /// batches so the game keeps drawing. Every line that is a measurement carries a number.
    /// </summary>
    internal sealed class MeshProbe : MonoBehaviour
    {
        // --- constants ---------------------------------------------------------------------------

        /// <summary>Metres around the player, in XZ, a renderer has to come within to be a readback
        /// candidate. Sixty: Big Red is about 45 m across, so standing anywhere inside it puts the
        /// whole building in the set, and a few of its neighbours with it.</summary>
        private const float ColumnRadius = 60f;

        /// <summary>How many renderers experiment 1 reads. Twenty is enough to tell "the readback
        /// works" from "the readback works on some meshes" and small enough to paste whole.</summary>
        private const int ReadbackCount = 20;

        /// <summary>How many of those are read by ALL THREE methods whatever the first one says, so
        /// the file records which paths work rather than only which path was used first.</summary>
        private const int AlwaysTryAll = 3;

        /// <summary>Metres a decoded world position may lie outside the renderer's own world bounds
        /// and still count as plausible. Half a metre: the bounds ARE the mesh's transformed box, so
        /// a correctly decoded vertex is inside it, and a mis-decoded one (wrong offset, wrong
        /// format, wrong stride) lands kilometres away or on zero. This is the check that can
        /// fail.</summary>
        private const float PlausibleSlack = 0.5f;

        /// <summary>Metres per cell of the raycast grid - the relief resolution the plan proposes.</summary>
        private const float CellSize = 2f;

        /// <summary>Rays per frame. Twenty thousand at ~2.7 us each is about 55 ms, which is a hitch
        /// but not a freeze, and it is what the plan's per-frame budget has to be measured against.</summary>
        private const int RaysPerFrame = 20000;

        /// <summary>Metres BELOW the lowest band the rays are allowed to reach, so a basement floor
        /// is hit rather than missed. Fifty, matching the slack the capture gives its top band.</summary>
        private const float RayDepthBelow = 50f;

        /// <summary>Metres per distance ring in the streaming test.</summary>
        private const float RingSize = 100f;

        /// <summary>The most cells the grid may have, so a probe on a very large map cannot allocate
        /// its way into a crash. Customs' 770x780 m is 150k; a million cells is 4 km square.</summary>
        private const int MaxCells = 1000000;

        /// <summary>The shader names the viewer may be built from, best first. Every one of them was
        /// found in a string scan of the game's globalgamemanagers, so one of them should resolve;
        /// experiment 3 says which actually do.</summary>
        internal static readonly string[] WantedShaders =
        {
            "Standard",
            "Legacy Shaders/Diffuse",
            "Legacy Shaders/VertexLit",
            "Unlit/Texture",
            "Unlit/Color",
            "UI/Default",
            "Hidden/Internal-Colored",
            "Sprites/Default"
        };

        /// <summary>The subset of <see cref="WantedShaders"/> the test view will actually try to make
        /// a material out of, in order - the four that write depth. UI/Default is ZWrite Off, so a
        /// scene drawn with it has no depth order at all.</summary>
        internal static readonly string[] ViewerShaders =
        {
            "Standard",
            "Legacy Shaders/Diffuse",
            "Legacy Shaders/VertexLit",
            "Unlit/Texture"
        };

        // --- installation ------------------------------------------------------------------------

        /// <summary>Adds the probe key's watcher to a raid, from
        /// <see cref="QuestTree.Patches.GameWorldStartedPatch"/>. Ahead of the HarvestZones gate on
        /// purpose: this is a debug key, not part of what the mod does for a player, and the raid the
        /// 3D questions get answered in may well be one with the harvest switched off. Never
        /// throws.</summary>
        /// <param name="gameWorld">The raid's world, as handed to the patch.</param>
        public static void Install(GameWorld gameWorld)
        {
            try
            {
                if (gameWorld == null) return;
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeMeshProbe");
                go.transform.SetParent(gameWorld.transform, worldPositionStays: false);

                var probe = go.AddComponent<MeshProbe>();
                probe._gameWorld = gameWorld;

                // Said out loud once per raid, at Info: a key that never fired and a watcher that was
                // never installed are indistinguishable otherwise, and the key it prints is the BOUND
                // one, so a cfg still holding an older binding is visible here too.
                Plugin.LogSource?.LogInfo($"QuestTree: mesh probe watching {BoundKeyText()}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not install the mesh probe key ({ex.Message}).");
            }
        }

        /// <summary>The bound key as a player would write it, or "no key (unbound)".</summary>
        internal static string BoundKeyText()
        {
            var key = ModSettings.Ready && ModSettings.ProbeKey != null
                ? ModSettings.KeyText(ModSettings.ProbeKey.Value, " + ")
                : "";

            return string.IsNullOrEmpty(key) ? "no key (unbound)" : key;
        }

        /// <summary>Whether the bound probe key went down this frame.
        ///
        /// ModSettings.ShortcutDown, never the shortcut's own IsDown: BepInEx refuses a press while
        /// ANY key outside the combination is held, which in a raid is almost always - W, Shift, a
        /// mouse button - and is why the first two presses of the older probe recorded nothing.</summary>
        internal static bool Pressed()
        {
            if (!ModSettings.Ready || ModSettings.ProbeKey == null) return false;

            var shortcut = ModSettings.ProbeKey.Value;
            if (shortcut.MainKey == KeyCode.None) return false;

            return ModSettings.ShortcutDown(shortcut);
        }

        // --- the raid watcher --------------------------------------------------------------------

        /// <summary>How many raid watchers are alive - 0 in the menu, 1 in a raid. The menu watcher's
        /// second, independent test for "is this a raid": whether GameWorld registers itself with
        /// Comfort's Singleton in this game version is not something this file can prove, and a menu
        /// probe that fired in a raid would put a 512 px panel over the player's screen. This watcher is
        /// created by GameWorld.OnGameStarted and dies with the GameWorld, so the count is exact.</summary>
        internal static int RaidWatchers;

        private GameWorld _gameWorld;
        private bool _warned;
        private bool _running;
        private int _press;

        private void OnEnable()
        {
            RaidWatchers++;
        }

        private void OnDisable()
        {
            if (RaidWatchers > 0) RaidWatchers--;
        }

        private void Update()
        {
            try
            {
                if (!Pressed()) return;

                if (_running)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the mesh probe ignored a press: busy with the last one.");
                    return;
                }

                _running = true;
                _press++;

                // Before any work, at Info, naming the file: a press whose experiment kills the process
                // must still have left a line saying it started and where its answers were going.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe press {_press} starting - experiment 2, then 3, then 1" +
                    $"{(MenuRunProved() ? "" : " (1 will be SKIPPED: no proven menu run yet)")}.");

                StartCoroutine(RunRaid(_press));
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning($"QuestTree: the mesh probe key failed ({ex.Message}).");
            }
        }

        /// <summary>One press in a raid: the header, then experiments 2, 3 and 1 - in that order, with each
        /// block appended to the file as it finishes, so a crash in the risky one cannot lose the answers
        /// the safe ones already produced.</summary>
        /// <param name="press">Which press of this raid this is, for the header.</param>
        private IEnumerator RunRaid(int press)
        {
            var stem = "unknown";
            var clock = Stopwatch.StartNew();

            try
            {
                var at = Vector3.zero;
                var map = "unknown";

                var header = new StringBuilder();

                Guard(header, 0, () =>
                {
                    map = MapName();
                    stem = Stem(map);

                    var player = _gameWorld != null ? _gameWorld.MainPlayer : null;
                    if (player != null) at = player.Transform.position;

                    header.AppendLine(
                        $"=== QuestTree mesh probe (THROWAWAY) - raid press {press} - {Now()} ===");
                    header.AppendLine(
                        $"mod {ModInfo.Stamp}, unity {Application.unityVersion}, map {map}, player at " +
                        $"{F(at.x)},{F(at.y)},{F(at.z)}, time in raid {F(Time.realtimeSinceStartup)} s");
                    header.AppendLine(
                        "buffer targets are never written - doing so crashed the engine on 2026-09-22 " +
                        "(Mesh.set_vertexBufferTarget -> d3d11).");
                });

                Append(stem, header.ToString());

                yield return null;

                // ORDER: 2, then 3, then 1 LAST, and every block appended as it finishes. Experiment 1 is
                // the one that has already killed the process once, and the raycast coverage and the layer
                // dump are the answers this raid is most likely to be repeated for - so they are on disk
                // before the risky one starts, not after.
                var two = new StringBuilder();
                yield return Experiment2(two, at);
                Append(stem, two.ToString());

                yield return null;

                var three = new StringBuilder();
                Guard(three, 3, () => Experiment3(three));
                Append(stem, three.ToString());

                yield return null;

                var one = new StringBuilder();

                // The gate: experiment 1 runs in a raid only once it has run to completion in the MENU,
                // proven by the marker line in that file. The crash it guards against is native, so no
                // in-memory flag could have survived to record it - only what was already written.
                if (MenuRunProved())
                {
                    yield return Experiment1(one, at);
                }
                else
                {
                    one.AppendLine("--- EXPERIMENT 1 - readback ---");
                    one.AppendLine(
                        "EXPERIMENT 1 SKIPPED: run the menu probe first (it proves the readback path cannot " +
                        "crash the engine). Press the key once in the menu, check that " +
                        $"menu.meshprobe.txt contains a \"{Experiment1Marker}\" line, then press it here again.");
                    one.AppendLine();
                }

                Append(stem, one.ToString());

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe press {press} wrote {stem}.meshprobe.txt in " +
                    $"{clock.ElapsedMilliseconds} ms (experiment 1 " +
                    $"{(MenuRunProved() ? "ran - the menu run proved it safe" : "SKIPPED - run the menu probe first")}).");
            }
            finally
            {
                _running = false;
            }
        }

        // --- experiment 1: readback ---------------------------------------------------------------

        /// <summary>One candidate renderer and everything the readback methods said about it.</summary>
        private sealed class Candidate
        {
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public float Volume;
            public bool Readable;
            public int Stream;
            public int Offset;
            public int Stride;
            public int Dimension;
            public VertexAttributeFormat Format;
            /// <summary>Whether (c) was attempted, which is not the same as whether it worked: (c) is
            /// skipped when (b) got no buffer to read from.</summary>
            public bool RanC;

            /// <summary>Whether (a) and (c) produced positions that passed <see cref="Verdict"/>. There is
            /// no OkB any more - (b) no longer decodes anything, so <see cref="HandleB"/> is all it can
            /// report.</summary>
            public bool OkA, OkC;

            /// <summary>Whether <c>GetVertexBuffer</c> handed back a buffer in (b). (c) has nothing to
            /// ask the GPU for when it did not, and says NOT RUN rather than failing.</summary>
            public bool HandleB;
        }

        /// <summary>The exact line the gate below looks for. Written at the end of experiment 1's summary
        /// and nowhere else, so its presence in menu.meshprobe.txt is proof that the whole of experiment 1
        /// ran to completion in the menu without taking the process down.</summary>
        internal const string Experiment1Marker = "EXPERIMENT 1 SUMMARY";

        /// <summary>Whether experiment 1 has ALREADY completed once in the menu, which is what earns it
        /// the right to run in a raid.
        ///
        /// The evidence is a file on disk rather than a static or a setting, and it has to be: the failure
        /// this gate exists for is a NATIVE crash, which takes the whole process down. A static would be
        /// gone, no finally would run, no setting would be saved - the only thing that survives is what was
        /// already written, so the only honest proof is the marker line in the menu's own file. A menu run
        /// that crashed leaves a file WITHOUT the marker, and this stays false for ever after.</summary>
        private static bool MenuRunProved()
        {
            try
            {
                var dir = MapCapture.ProbeCapturesRoot();
                if (dir == null) return false;

                var path = Path.Combine(dir, "menu.meshprobe.txt");
                if (!File.Exists(path)) return false;

                return File.ReadAllText(path).IndexOf(Experiment1Marker, StringComparison.Ordinal) >= 0;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe could not read its menu file ({ex.Message}).");
                return false;
            }
        }

        /// <summary>Experiment 1: whether the twenty largest meshes can be read off the GPU, and by which
        /// path. One mesh spread over several frames.
        ///
        /// NOTHING HERE WRITES vertexBufferTarget OR indexBufferTarget. The first version of this
        /// experiment did - `|= Raw` before reading, the original value back afterwards - and on
        /// 2026-09-22 the restoring setter killed the engine outright on the first mesh it reached next to
        /// Big Red on Customs: Mesh.set_vertexBufferTarget -> UnityPlayer -> d3d11, a native crash with no
        /// managed exception anywhere in it. Guard catches exceptions; nothing in managed code catches that.
        /// So the targets are READ and printed and never assigned, here or anywhere else in the 3D feature,
        /// and the question "can a non-readable mesh be read back" is asked only of the buffer Unity is
        /// willing to hand over as the mesh already stands.</summary>
        /// <param name="text">The block being built for the file.</param>
        /// <param name="at">The player's position, for the 60 m filter, or null in the menu, where there is
        /// no player and every mesh in the loaded scene is a candidate.</param>
        internal static IEnumerator Experiment1(StringBuilder text, Vector3? at)
        {
            text.AppendLine("--- EXPERIMENT 1 - readback ---");
            text.AppendLine(
                "buffer targets are never written - doing so crashed the engine on 2026-09-22 " +
                "(Mesh.set_vertexBufferTarget -> d3d11). They are read and printed only, and no synchronous " +
                "GetData is called on a scene mesh's buffer either.");

            var mask = 0;
            List<Candidate> picked = null;
            var scanned = 0;
            var considered = 0;

            if (!Guard(text, 1, () =>
                {
                    // In a raid, the layers a capture draws, so the set is the set the 3D feature cares
                    // about. In the menu there is no capture camera to copy and no reason to narrow: the
                    // question there is only whether the readback path is survivable at all.
                    mask = at.HasValue ? MapCapture.ProbeCaptureMask() : ~0;

                    var all = FindObjectsOfType<MeshRenderer>();
                    scanned = all == null ? 0 : all.Length;

                    var found = new List<Candidate>();

                    foreach (var renderer in all ?? new MeshRenderer[0])
                    {
                        if (renderer == null) continue;

                        var go = renderer.gameObject;
                        if (go == null || !go.scene.IsValid()) continue;
                        if ((mask & (1 << go.layer)) == 0) continue;

                        var bounds = renderer.bounds;
                        if (at.HasValue && !InColumn(bounds, go.transform.position, at.Value)) continue;

                        considered++;

                        var filter = renderer.GetComponent<MeshFilter>();
                        var mesh = filter != null ? filter.sharedMesh : null;
                        if (mesh == null || mesh.vertexCount == 0) continue;

                        var size = bounds.size;
                        found.Add(new Candidate
                        {
                            Renderer = renderer,
                            Mesh = mesh,
                            Volume = Mathf.Abs(size.x * size.y * size.z)
                        });
                    }

                    found.Sort((a, b) => b.Volume.CompareTo(a.Volume));
                    if (found.Count > ReadbackCount) found.RemoveRange(ReadbackCount, found.Count - ReadbackCount);
                    picked = found;

                    text.AppendLine(
                        $"scanned {scanned} MeshRenderer(s), {considered} " +
                        $"{(at.HasValue ? $"within {F(ColumnRadius)} m (XZ) of the player on the capture's layers" : "in the loaded scene (no distance filter, every layer)")}" +
                        $", {picked.Count} read (cap {ReadbackCount})");
                    text.AppendLine($"layer mask 0x{mask:X8} = [{MaskNames(mask)}]");
                })) yield break;

            if (picked == null || picked.Count == 0)
            {
                // Deliberately NOT the marker line. A run that attempted nothing has proved nothing, and
                // writing the marker here would unlock the raid side on the strength of an empty scene -
                // exactly the kind of check that cannot fail.
                text.AppendLine(
                    "EXPERIMENT 1 INCONCLUSIVE: 0 candidate(s) - nothing was attempted, so this run proves " +
                    "nothing and the raid side stays locked.");
                yield break;
            }

            for (var i = 0; i < picked.Count; i++)
            {
                var c = picked[i];
                var index = i + 1;
                var forceAll = i < AlwaysTryAll;

                var described = Guard(text, 1, () => Describe(text, index, c));

                // (a) the managed arrays, only where Unity says they exist.
                if (c.Readable)
                {
                    Guard(text, 1, () => c.OkA = MethodA(text, index, c));
                }
                else
                {
                    // "not read", not "isReadable is false": Describe is the only thing that sets
                    // c.Readable and it runs under a Guard, so a Describe that threw leaves the flag at
                    // its default and this branch must not state a fact it did not establish.
                    text.AppendLine(
                        $"  [{index:00}] (a) mesh.vertices: not read " +
                        $"(isReadable {(described ? "is false" : "was never read - Describe failed above")})");
                }

                yield return null;

                // (b) does Unity hand over the buffers at all, as the mesh stands? Always asked, because it
                // is now the cheap half of the question and it is what tells (c) whether to bother.
                Guard(text, 1, () => MethodB(text, index, c));

                yield return null;

                // (c) the async readback of that same unmodified buffer - the only path left that can
                // produce actual positions from a non-readable mesh. Every mesh gets a (c) line, including
                // the ones it was not run on: a missing line reads as a crash.
                if (!c.HandleB)
                {
                    text.AppendLine(
                        $"  [{index:00}] (c) AsyncGPUReadback: NOT RUN - (b) got no vertex buffer to read from");
                }
                else if (forceAll || !c.OkA)
                {
                    c.RanC = true;
                    yield return MethodC(text, index, c);
                }
                else
                {
                    text.AppendLine(
                        $"  [{index:00}] (c) AsyncGPUReadback: NOT RUN - (a) already read this mesh plausibly");
                }

                yield return null;
            }

            Guard(text, 1, () =>
            {
                int readable = 0, a = 0, handles = 0, cc = 0, none = 0, ranC = 0;

                foreach (var c in picked)
                {
                    if (c.Readable) readable++;
                    if (c.OkA) a++;
                    if (c.HandleB) handles++;
                    if (c.OkC) cc++;
                    if (!c.OkA && !c.OkC) none++;
                    if (c.RanC) ranC++;
                }

                var n = picked.Count;

                // (b) is counted as HANDLES, not as "worked": it no longer decodes anything, because the
                // only way it could - a synchronous GetData on a mapped buffer - is the same native path
                // that crashed the engine. So the two columns that mean "positions came back" are (a) and
                // (c), and "failed" means neither of those produced any.
                text.AppendLine(
                    $"{Experiment1Marker}: {n} mesh(es); readable {readable} ({Pct(readable, n)}); " +
                    $"(a) mesh.vertices plausible {a} ({Pct(a, n)}); (b) GetVertexBuffer handed back a buffer " +
                    $"{handles} ({Pct(handles, n)}); (c) AsyncGPUReadback attempted {ranC}, plausible {cc} " +
                    $"({Pct(cc, n)}); no positions from any path {none} ({Pct(none, n)})");
            });

            text.AppendLine();
        }

        /// <summary>The header line of one candidate: what it is and what its position attribute looks
        /// like, which is what a decode has to get right.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">Its number in the file.</param>
        /// <param name="c">The candidate, whose attribute fields this fills in.</param>
        private static void Describe(StringBuilder text, int index, Candidate c)
        {
            var mesh = c.Mesh;
            var go = c.Renderer.gameObject;

            c.Readable = mesh.isReadable;
            c.Stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            c.Offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            c.Format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
            c.Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
            c.Stride = c.Stream >= 0 ? mesh.GetVertexBufferStride(c.Stream) : 0;

            var triangles = 0L;
            var topologies = new List<string>();

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var indices = mesh.GetIndexCount(s);
                var topology = mesh.GetTopology(s);
                topologies.Add($"{topology}:{indices}");
                if (topology == MeshTopology.Triangles) triangles += indices / 3;
            }

            var bounds = c.Renderer.bounds;

            text.AppendLine(
                $"  [{index:00}] vol {F(c.Volume)} m3 box {F(bounds.size.x)}x{F(bounds.size.y)}x{F(bounds.size.z)} " +
                $"layer {LayerName(go.layer)} verts {mesh.vertexCount} tris {triangles} submeshes {mesh.subMeshCount} " +
                $"[{string.Join(" ", topologies.ToArray())}] readable {YesNo(c.Readable)} " +
                $"indexFormat {mesh.indexFormat}");
            text.AppendLine(
                $"  [{index:00}] position attribute: stream {c.Stream} offset {c.Offset} format {c.Format} " +
                $"dimension {c.Dimension} vertex stride {c.Stride} bytes; vertexBufferTarget {mesh.vertexBufferTarget} " +
                $"indexBufferTarget {mesh.indexBufferTarget}");
            text.AppendLine($"  [{index:00}] path {Path_(go.transform)}");
        }

        /// <summary>(a) The managed arrays. True when the first vertex lands inside the renderer's own
        /// world bounds.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">The candidate's number.</param>
        /// <param name="c">The candidate.</param>
        private static bool MethodA(StringBuilder text, int index, Candidate c)
        {
            var clock = Stopwatch.StartNew();

            var vertices = c.Mesh.vertices;
            var triangles = c.Mesh.triangles;

            clock.Stop();

            // Fewer than three vertices is not a mesh this experiment can judge, and it must not be
            // reported as a success: a one-vertex read that happens to be inside a box says nothing
            // about whether a readback works.
            if (vertices == null || vertices.Length < 3)
            {
                text.AppendLine(
                    $"  [{index:00}] (a) mesh.vertices: {(vertices == null ? 0 : vertices.Length)} " +
                    $"vertex/vertices - fewer than 3, nothing to judge, {Ms(clock)} ms -> NOT JUDGED");
                return false;
            }

            var matrix = c.Renderer.localToWorldMatrix;

            var points = new List<Vector3> { vertices[0], vertices[1], vertices[2] };
            var shown = new List<string>();

            foreach (var point in points)
            {
                var world = matrix.MultiplyPoint3x4(point);
                shown.Add($"({F(world.x)},{F(world.y)},{F(world.z)})");
            }

            var worked = Verdict(points, c, false, out var why);

            text.AppendLine(
                $"  [{index:00}] (a) mesh.vertices: {vertices.Length} vertex/vertices, " +
                $"{triangles.Length / 3} triangle(s), first 3 world {string.Join(" ", shown.ToArray())}, " +
                $"{Ms(clock)} ms -> {(worked ? "PLAUSIBLE" : "IMPLAUSIBLE")} ({why})");

            return worked;
        }

        /// <summary>(b) Does Unity hand over the mesh's GPU buffers AT ALL, as the mesh already stands?
        ///
        /// That is the whole of (b) now, and the reason it is so little is worth writing down. It used to
        /// add GraphicsBuffer.Target.Raw to the mesh's vertexBufferTarget and indexBufferTarget, read the
        /// head of the buffer with a synchronous GetData, and put the targets back. On 2026-09-22 that
        /// killed the game: the SECOND assignment - the one restoring the original value - went
        /// Mesh.set_vertexBufferTarget -> UnityPlayer -> d3d11 and the process died, with no managed
        /// exception for Guard to catch and nothing in the probe's file past the header. The first mesh it
        /// reached was inside Big Red on Customs.
        ///
        /// So two things are gone for good, here and everywhere else in the 3D feature:
        ///   - NO WRITE to vertexBufferTarget or indexBufferTarget on a mesh we do not own. The targets are
        ///     read and printed; they are never assigned. A mesh the game uploaded as Vertex-only stays
        ///     Vertex-only.
        ///   - NO synchronous GetData on a scene mesh's buffer. Mapping a buffer that was never created for
        ///     CPU access is plausibly the same native path, and a second crash would cost another raid to
        ///     learn nothing new. AsyncGPUReadback in (c) is the one remaining way to ask, and it is at
        ///     least an API whose failure mode is documented as a flag rather than a map.
        ///
        /// What is left still answers something: whether GetVertexBuffer/GetIndexBuffer return a handle or
        /// throw on a non-readable mesh, and what that handle says about itself (count, stride, target).
        /// A buffer that cannot even be obtained cannot be read by any means, so a "no" here settles the
        /// question for that mesh without touching the GPU at all.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">The candidate's number.</param>
        /// <param name="c">The candidate, whose <see cref="Candidate.HandleB"/> this sets.</param>
        private static void MethodB(StringBuilder text, int index, Candidate c)
        {
            var mesh = c.Mesh;
            var stream = Math.Max(0, c.Stream);

            GraphicsBuffer vb = null;
            GraphicsBuffer ib = null;

            var vertexNote = "";
            var indexNote = "";
            var clock = Stopwatch.StartNew();

            try
            {
                // Each call in its own try: a mesh can refuse one and hand over the other, and one refusal
                // must not hide the other's answer.
                try
                {
                    vb = mesh.GetVertexBuffer(stream);
                    vertexNote = vb == null
                        ? "returned null"
                        : $"ok, count {vb.count} stride {vb.stride} target {vb.target}";
                }
                catch (Exception ex)
                {
                    vertexNote = $"threw {ex.GetType().Name}: {ex.Message}";
                }

                try
                {
                    ib = mesh.GetIndexBuffer();
                    indexNote = ib == null
                        ? "returned null"
                        : $"ok, count {ib.count} stride {ib.stride} target {ib.target}";
                }
                catch (Exception ex)
                {
                    indexNote = $"threw {ex.GetType().Name}: {ex.Message}";
                }

                clock.Stop();

                c.HandleB = vb != null;

                text.AppendLine(
                    $"  [{index:00}] (b) buffer handles (targets NOT modified): GetVertexBuffer({stream}) " +
                    $"{vertexNote}; GetIndexBuffer() {indexNote}; {Ms(clock)} ms -> " +
                    $"{(c.HandleB ? "HANDLE" : "NO HANDLE")}");
            }
            finally
            {
                if (clock.IsRunning) clock.Stop();

                // Released, each on its own, and a refusal only logged: there is no restore step any more,
                // because there is nothing to restore.
                ReleaseBuffer(text, index, "(b)", "vertex", vb);
                ReleaseBuffer(text, index, "(b)", "index", ib);
            }
        }

        /// <summary>Releases one GraphicsBuffer, and says so if it will not go. Guarded on its own so that
        /// one buffer refusing cannot stop the next from being released - the leak this probe can still
        /// cause is a buffer handle, which costs memory for the raid and nothing else.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">The candidate's number.</param>
        /// <param name="method">Which method is releasing, for the line.</param>
        /// <param name="which">"vertex" or "index", for the line.</param>
        /// <param name="buffer">The buffer, or null.</param>
        private static void ReleaseBuffer(
            StringBuilder text, int index, string method, string which, GraphicsBuffer buffer)
        {
            if (buffer == null) return;

            try
            {
                buffer.Release();
            }
            catch (Exception ex)
            {
                text.AppendLine(
                    $"  [{index:00}] {method} NOTE: the {which} buffer would not release " +
                    $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>(c) The mesh's own vertex buffer through AsyncGPUReadback, yielding until it is done.
        /// A coroutine because that is the whole point of the method.
        ///
        /// The buffer is taken AS IT IS. No target is added to the mesh first - that is what crashed the
        /// engine on 2026-09-22 - so this asks the honest question: will Unity read back a buffer the game
        /// created for the GPU alone? If the answer is no, AsyncGPUReadback is documented to say so through
        /// hasError, which is a verdict this file can record. That is exactly why (b) no longer calls
        /// GetData: this is the only one of the two whose failure is a flag rather than a memory map.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">The candidate's number.</param>
        /// <param name="c">The candidate.</param>
        private static IEnumerator MethodC(StringBuilder text, int index, Candidate c)
        {
            var mesh = c.Mesh;

            GraphicsBuffer vb = null;
            var request = default(AsyncGPUReadbackRequest);
            var asked = false;

            // Declared out here so the finally can see them: a readback still in flight has to be waited
            // for BEFORE its buffer is released, and only these two say whether one is.
            var done = false;
            var clock = Stopwatch.StartNew();

            try
            {
                Guard(text, 1, () =>
                {
                    // Its own handle, because (b) released its one in a finally. Same call, same mesh, no
                    // modification of either.
                    vb = mesh.GetVertexBuffer(Math.Max(0, c.Stream));
                    if (vb == null)
                    {
                        text.AppendLine($"  [{index:00}] (c) AsyncGPUReadback: GetVertexBuffer returned null -> FAILED");
                        return;
                    }

                    request = AsyncGPUReadback.Request(vb);
                    asked = true;
                });

                if (!asked) yield break;

                var frames = 0;

                while (frames < 120)
                {
                    if (!Guard(text, 1, () => done = request.done)) break;
                    if (done) break;

                    yield return null;
                    frames++;
                }

                clock.Stop();

                if (!done)
                {
                    text.AppendLine(
                        $"  [{index:00}] (c) AsyncGPUReadback: still not done after {frames} frame(s), " +
                        $"{Ms(clock)} ms -> FAILED");
                    yield break;
                }

                Guard(text, 1, () =>
                {
                    if (request.hasError)
                    {
                        text.AppendLine(
                            $"  [{index:00}] (c) AsyncGPUReadback: done after {frames} frame(s), hasError yes, " +
                            $"{Ms(clock)} ms -> FAILED");
                        return;
                    }

                    var data = request.GetData<byte>();
                    var need = c.Offset + 2 * Math.Max(1, c.Stride) + 12;
                    var take = Math.Min(data.Length, Math.Max(need, 0));
                    var bytes = new byte[take];
                    for (var i = 0; i < take; i++) bytes[i] = data[i];

                    c.OkC = Decode(
                        text, index, $"(c) AsyncGPUReadback after {frames} frame(s)", c, bytes,
                        $"{data.Length} byte(s) returned, {take} read", vb, clock);
                });
            }
            finally
            {
                // A readback that timed out is still IN FLIGHT, and its destination is the buffer the
                // line below releases: releasing it first is a GPU write into freed memory. Waited for
                // rather than abandoned, guarded because a wait on a broken request may throw.
                if (asked && !done)
                {
                    try
                    {
                        request.WaitForCompletion();
                        text.AppendLine(
                            $"  [{index:00}] (c) NOTE: the readback was still pending, so it was waited for " +
                            $"before its buffer was released (done now {YesNo(request.done)}, hasError " +
                            $"{YesNo(request.hasError)}).");
                    }
                    catch (Exception ex)
                    {
                        text.AppendLine(
                            $"  [{index:00}] (c) NOTE: the pending readback would not be waited for " +
                            $"({ex.GetType().Name}: {ex.Message}) - its buffer is released anyway.");
                    }
                }

                ReleaseBuffer(text, index, "(c)", "vertex", vb);
            }
        }

        /// <summary>Decodes the first three positions out of a head of vertex-buffer bytes and writes
        /// the line, with the plausibility verdict. True when all three land inside the renderer's own
        /// world bounds - the check that can fail.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="index">The candidate's number.</param>
        /// <param name="method">Which method produced the bytes, for the line.</param>
        /// <param name="c">The candidate.</param>
        /// <param name="bytes">The head of the vertex buffer.</param>
        /// <param name="how">How the bytes were read, for the line.</param>
        /// <param name="vb">The buffer, for its count and stride.</param>
        /// <param name="clock">The method's clock, already stopped.</param>
        private static bool Decode(
            StringBuilder text, int index, string method, Candidate c, byte[] bytes, string how,
            GraphicsBuffer vb, Stopwatch clock)
        {
            var matrix = c.Renderer.localToWorldMatrix;
            var stride = Math.Max(1, c.Stride);

            var points = new List<Vector3>(3);
            var shown = new List<string>();
            var note = "";

            for (var v = 0; v < 3; v++)
            {
                var at = c.Offset + v * stride;

                if (!TryReadPosition(bytes, at, c.Format, c.Dimension, out var local, out var why))
                {
                    if (note.Length == 0) note = why;
                    continue;
                }

                points.Add(local);
                var world = matrix.MultiplyPoint3x4(local);
                shown.Add($"({F(world.x)},{F(world.y)},{F(world.z)})");
            }

            var worked = Verdict(points, c, true, out var verdict);

            text.AppendLine(
                $"  [{index:00}] {method}: count {(vb == null ? 0 : vb.count)} stride " +
                $"{(vb == null ? 0 : vb.stride)} target {(vb == null ? GraphicsBuffer.Target.Raw : vb.target)}; " +
                $"{how}; decoded {points.Count} of 3 as {c.Format}x{c.Dimension}" +
                $"{(note.Length == 0 ? "" : " (" + note + ")")}; world {string.Join(" ", shown.ToArray())}; " +
                $"{Ms(clock)} ms -> {(worked ? "PLAUSIBLE" : "IMPLAUSIBLE")} ({verdict})");

            return worked;
        }

        /// <summary>One position out of a vertex buffer's bytes. False with a reason for a format this
        /// probe does not decode - which is itself a finding: an optimised mesh can store positions as
        /// three Float16s, and the plan's uint16 quantisation has to know that.</summary>
        /// <param name="bytes">The buffer head.</param>
        /// <param name="at">Byte offset of this vertex's position.</param>
        /// <param name="format">The attribute's format.</param>
        /// <param name="dimension">How many components it has.</param>
        /// <param name="value">The decoded local-space position.</param>
        /// <param name="why">Why it could not be decoded.</param>
        private static bool TryReadPosition(
            byte[] bytes, int at, VertexAttributeFormat format, int dimension, out Vector3 value, out string why)
        {
            value = Vector3.zero;
            why = "";

            if (dimension < 3)
            {
                why = $"dimension {dimension} is not 3";
                return false;
            }

            int size;

            switch (format)
            {
                case VertexAttributeFormat.Float32:
                    size = 4;
                    break;
                case VertexAttributeFormat.Float16:
                    size = 2;
                    why = "Float16 positions";
                    break;
                default:
                    why = $"format {format} not decoded by this probe";
                    return false;
            }

            if (at < 0 || at + 3 * size > bytes.Length)
            {
                why = $"needs bytes {at}..{at + 3 * size} of {bytes.Length}";
                return false;
            }

            if (format == VertexAttributeFormat.Float32)
            {
                value = new Vector3(
                    BitConverter.ToSingle(bytes, at),
                    BitConverter.ToSingle(bytes, at + 4),
                    BitConverter.ToSingle(bytes, at + 8));
            }
            else
            {
                value = new Vector3(
                    Half(BitConverter.ToUInt16(bytes, at)),
                    Half(BitConverter.ToUInt16(bytes, at + 2)),
                    Half(BitConverter.ToUInt16(bytes, at + 4)));
            }

            return true;
        }

        /// <summary>An IEEE half as a float. Written out rather than reached for, because
        /// System.Half does not exist on netstandard2.1 and Mathf has no converter.</summary>
        /// <param name="bits">The sixteen bits.</param>
        private static float Half(ushort bits)
        {
            var sign = (bits >> 15) & 1;
            var exponent = (bits >> 10) & 0x1F;
            var mantissa = bits & 0x3FF;

            float value;

            if (exponent == 0)
            {
                value = mantissa == 0 ? 0f : mantissa * 5.9604645e-8f;
            }
            else if (exponent == 31)
            {
                value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            }
            else
            {
                value = (1f + mantissa / 1024f) * Mathf.Pow(2f, exponent - 15);
            }

            return sign == 1 ? -value : value;
        }

        /// <summary>Whether ONE decoded position is inside the renderer's own world bounds, with
        /// <see cref="PlausibleSlack"/> of slack. Necessary, and nowhere near sufficient - see
        /// <see cref="Verdict"/>, which is the test that decides.</summary>
        /// <param name="world">The decoded position.</param>
        /// <param name="bounds">The renderer's world bounds.</param>
        private static bool Plausible(Vector3 world, Bounds bounds)
        {
            if (float.IsNaN(world.x) || float.IsNaN(world.y) || float.IsNaN(world.z)) return false;
            if (float.IsInfinity(world.x) || float.IsInfinity(world.y) || float.IsInfinity(world.z)) return false;

            var grown = bounds;
            grown.Expand(2f * PlausibleSlack);
            return grown.Contains(world);
        }

        /// <summary>Whether three decoded positions are a real read of this mesh, and WHY not when they
        /// are not.
        ///
        /// "Inside the world bounds" alone cannot fail on the meshes this experiment picks. They are the
        /// twenty LARGEST renderers within 60 m, so their boxes are tens of metres across, and the two
        /// mis-decodes that actually happen both land inside one: a wrong offset that reads zeroes gives
        /// (0,0,0), which the transform puts at the object's own origin - the centre of its box - and a
        /// wrong stride re-reads vertex 0 three times, which is inside the box by definition. A check
        /// that cannot fail on the inputs it is given is not a check, so three more have to pass:
        ///
        ///   - every point inside the grown bounds (the weak one, kept because it catches garbage);
        ///   - the three points PAIRWISE DISTINCT by more than a centimetre, which is what kills a
        ///     stride that reads one vertex three times and an all-zeroes read;
        ///   - not all three within a centimetre of the transform's own position, which is what kills
        ///     the zeroes case on a mesh whose pivot is not its centre;
        ///   - and where the mesh is READABLE, every point within a centimetre of the same vertex out of
        ///     mesh.vertices, which is the ground truth and makes the verdict exact rather than
        ///     plausible. The largest disagreement is logged either way.
        /// </summary>
        /// <param name="points">The three decoded local-space positions, in vertex order.</param>
        /// <param name="c">The candidate, for its renderer, transform and mesh.</param>
        /// <param name="independent">Whether <paramref name="points"/> came from somewhere OTHER than
        /// mesh.vertices. False for method (a), whose points ARE mesh.vertices: comparing them against it
        /// would be comparing a thing to itself and would print a 0.0000 m agreement that means nothing.</param>
        /// <param name="why">What failed, or how it was confirmed, for the line.</param>
        private static bool Verdict(List<Vector3> points, Candidate c, bool independent, out string why)
        {
            why = "";

            if (points.Count < 3)
            {
                why = $"only {points.Count} of 3 decoded";
                return false;
            }

            var matrix = c.Renderer.localToWorldMatrix;
            var bounds = c.Renderer.bounds;
            var origin = c.Renderer.transform.position;

            var world = new List<Vector3>(3);
            foreach (var point in points) world.Add(matrix.MultiplyPoint3x4(point));

            foreach (var point in world)
            {
                if (Plausible(point, bounds)) continue;
                why = "a point is outside the renderer's world bounds";
                return false;
            }

            // Pairwise distinct. Three vertices of one triangle are never the same point.
            for (var i = 0; i < 3; i++)
            {
                for (var j = i + 1; j < 3; j++)
                {
                    if ((world[i] - world[j]).sqrMagnitude > 0.0001f) continue;
                    why = $"points {i} and {j} are the same within 1 cm (a stride or an all-zero read)";
                    return false;
                }
            }

            var atOrigin = 0;
            foreach (var point in world)
            {
                if ((point - origin).sqrMagnitude <= 0.0001f) atOrigin++;
            }

            if (atOrigin == 3)
            {
                why = "all three points are the transform's own position (a read of zeroes)";
                return false;
            }

            // The ground truth, where Unity will give it to us AND where the points did not come from it.
            if (!independent)
            {
                why = "bounds + distinct + not-at-origin (these points ARE mesh.vertices, so there is " +
                      "nothing independent to check them against)";
                return true;
            }

            if (!c.Readable)
            {
                why = "bounds + distinct + not-at-origin (the mesh is not readable, so there is nothing to compare against)";
                return true;
            }

            try
            {
                var vertices = c.Mesh.vertices;

                if (vertices == null || vertices.Length < 3)
                {
                    why = "the readable mesh has fewer than 3 vertices to compare against";
                    return false;
                }

                var worst = 0f;

                for (var i = 0; i < 3; i++)
                {
                    var diff = (points[i] - vertices[i]).magnitude;
                    if (diff > worst) worst = diff;
                }

                if (worst > 0.01f)
                {
                    why = $"mesh.vertices disagrees by up to {F(worst)} m";
                    return false;
                }

                why = $"matches mesh.vertices to within {worst.ToString("0.0000", CultureInfo.InvariantCulture)} m";
                return true;
            }
            catch (Exception ex)
            {
                why = $"bounds + distinct + not-at-origin (mesh.vertices unreadable: {ex.GetType().Name})";
                return true;
            }
        }

        // --- experiment 2: raycasts and coverage --------------------------------------------------

        /// <summary>Everything the two raycast passes share and produce.</summary>
        private sealed class Grid
        {
            public MapExtentDto Extent;
            public string Map = "";
            public double MinX, MinZ;
            public int Width, Height, Cells;
            public float CameraY, Distance;
            public Vector3 At;

            public bool[] Hit;
            public int[] Layer;
            public float[] Y;

            public int SyncDone, SyncHits;
            public int BatchDone, BatchHits;
            public readonly Stopwatch SyncClock = new Stopwatch();
            public readonly Stopwatch BatchClock = new Stopwatch();
        }

        /// <summary>Experiment 2: the whole 2 m grid, twice, and what it says about coverage.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="at">The player's position, for the distance rings.</param>
        private IEnumerator Experiment2(StringBuilder text, Vector3 at)
        {
            text.AppendLine("--- EXPERIMENT 2 - raycasts and coverage ---");

            Grid grid = null;

            if (!Guard(text, 2, () => grid = BuildGrid(text, at))) yield break;
            if (grid == null) yield break;

            // (a) the synchronous path, in chunks so the game keeps drawing. A failed chunk stops the
            // pass but NOT the experiment: the rays already cast are a measurement, and (b) and the
            // report are worth having without them.
            while (grid.SyncDone < grid.Cells)
            {
                if (!Guard(text, 2, () => SyncChunk(grid))) break;
                yield return null;
            }

            Guard(text, 2, () => text.AppendLine(
                $"(a) Physics.Raycast: {grid.SyncDone} of {grid.Cells} ray(s) in {Chunks(grid.SyncDone)} chunk(s) " +
                $"of {RaysPerFrame}, {Ms(grid.SyncClock)} ms, {grid.SyncHits} hit(s) " +
                $"({Pct(grid.SyncHits, grid.SyncDone)}), {Per(grid.SyncClock, grid.SyncDone)} us/ray"));

            // (b) the batched path, same grid, same chunk size.
            while (grid.BatchDone < grid.Cells)
            {
                if (!Guard(text, 2, () => BatchChunk(grid))) break;
                yield return null;
            }

            Guard(text, 2, () => text.AppendLine(
                $"(b) RaycastCommand.ScheduleBatch: {grid.BatchDone} of {grid.Cells} ray(s) in " +
                $"{Chunks(grid.BatchDone)} chunk(s) of {RaysPerFrame}, {Ms(grid.BatchClock)} ms, " +
                $"{grid.BatchHits} hit(s) ({Pct(grid.BatchHits, grid.BatchDone)}), " +
                $"{Per(grid.BatchClock, grid.BatchDone)} us/ray; (a) minus (b) = " +
                $"{grid.SyncHits - grid.BatchHits} hit(s)"));

            yield return null;

            Guard(text, 2, () => Report(text, grid));
            text.AppendLine();
        }

        /// <summary>The extent, the camera height rule and the arrays - everything before the first
        /// ray. Null with a line written when there is no extent to grid.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="at">The player's position.</param>
        private Grid BuildGrid(StringBuilder text, Vector3 at)
        {
            var map = MapName();

            // HasExtentFor FIRST, and the read path only. TryProbeForCapture on a MISS measures the map
            // and writes MapExtentProbe's static memo (_lastMap/_lastExtent), which outlives the raid:
            // a rectangle this probe caused to be measured - with no harvested triggers to check
            // containment against, from wherever the player happened to be standing - would then be the
            // rectangle Ctrl+F9 draws its picture to, for the rest of the session. A diagnostic may not
            // decide what the captures are aligned to. MapCampaign's auto tick asks the same way and for
            // one of the same two reasons.
            if (!MapExtentProbe.HasExtentFor(map))
            {
                text.AppendLine(
                    $"EXPERIMENT 2 SKIPPED: no harvested extent for {map} (run a raid with HarvestZones on " +
                    "first). Measuring one here would write MapExtentProbe's session memo and every later " +
                    "capture would be drawn to the probe's rectangle, so the probe never asks for one.");
                return null;
            }

            var extent = MapExtentProbe.TryProbeForCapture(map);

            if (extent == null)
            {
                text.AppendLine(
                    $"EXPERIMENT 2 SKIPPED: the memo for {map} read back null, 0 rays cast.");
                return null;
            }

            var floors = extent.Floors ?? new List<MapFloorDto>();

            var spanX = extent.MaxX - extent.MinX;
            var spanZ = extent.MaxZ - extent.MinZ;

            text.AppendLine(
                $"extent: source {extent.Source}, x {F((float)extent.MinX)}..{F((float)extent.MaxX)} " +
                $"({F((float)spanX)} m), z {F((float)extent.MinZ)}..{F((float)extent.MaxZ)} ({F((float)spanZ)} m), " +
                $"{floors.Count} band(s), rotation {F(extent.Rotation)}");

            if (spanX <= 0 || spanZ <= 0)
            {
                text.AppendLine("the extent has no area, 0 rays cast.");
                return null;
            }

            // The top band's camera height rule, read off MapCapture.BeginFloor rather than guessed:
            // the topmost band is photographed from its maxY plus TopBandCameraHeight.
            var topMaxY = float.NegativeInfinity;
            var lowMinY = float.PositiveInfinity;
            var topName = "none";

            foreach (var floor in floors)
            {
                if (floor == null) continue;
                if (floor.MaxY > topMaxY) { topMaxY = floor.MaxY; topName = floor.Name; }
                if (floor.MinY < lowMinY) lowMinY = floor.MinY;
            }

            if (!IsFinite(topMaxY) || !IsFinite(lowMinY))
            {
                text.AppendLine("no usable height band, 0 rays cast.");
                return null;
            }

            var rise = MapCapture.ProbeTopBandCameraHeight;
            var cameraY = topMaxY + rise;
            var distance = cameraY - lowMinY + RayDepthBelow;

            var width = (int)Math.Ceiling(spanX / CellSize);
            var height = (int)Math.Ceiling(spanZ / CellSize);
            var cells = width * height;

            text.AppendLine(
                $"top band \"{topName}\" maxY {F(topMaxY)} -> camera y {F(cameraY)} (maxY + {F(rise)}, " +
                $"MapCapture.BeginFloor's rule); lowest band minY {F(lowMinY)}; ray length {F(distance)} m " +
                $"(camera y - lowest minY + {F(RayDepthBelow)})");

            if (cells <= 0 || cells > MaxCells)
            {
                text.AppendLine($"grid would be {width} x {height} = {cells} cell(s), over the {MaxCells} cap - 0 rays cast.");
                return null;
            }

            text.AppendLine(
                $"grid: {F(CellSize)} m cells, {width} x {height} = {cells} cell(s), {RaysPerFrame} ray(s) per frame");

            return new Grid
            {
                Extent = extent,
                Map = map,
                MinX = extent.MinX,
                MinZ = extent.MinZ,
                Width = width,
                Height = height,
                Cells = cells,
                CameraY = cameraY,
                Distance = distance,
                At = at,
                Hit = new bool[cells],
                Layer = new int[cells],
                Y = new float[cells]
            };
        }

        /// <summary>The cell centre of one index, in world XZ.</summary>
        /// <param name="grid">The grid.</param>
        /// <param name="n">The cell index.</param>
        private static Vector3 Centre(Grid grid, int n)
        {
            var ix = n % grid.Width;
            var iz = n / grid.Width;

            return new Vector3(
                (float)(grid.MinX + (ix + 0.5) * CellSize),
                grid.CameraY,
                (float)(grid.MinZ + (iz + 0.5) * CellSize));
        }

        /// <summary>One frame's worth of synchronous rays, timed without the frame boundary in it.</summary>
        /// <param name="grid">The grid.</param>
        private static void SyncChunk(Grid grid)
        {
            var end = Math.Min(grid.Cells, grid.SyncDone + RaysPerFrame);

            grid.SyncClock.Start();

            // The clock stopped whichever way this leaves, exactly as the batched pass does: a chunk that
            // throws half way would otherwise leave the Stopwatch running across the frame boundary and
            // every later chunk's time would carry the idle frames with it - a us/ray figure ten times
            // too slow, reported as a measurement.
            try
            {
                for (var n = grid.SyncDone; n < end; n++)
                {
                    // Every layer, triggers ignored: WHICH layers the relief should use is what the
                    // histogram below is for, so the measurement cannot start by assuming an answer.
                    if (Physics.Raycast(Centre(grid, n), Vector3.down, out var hit, grid.Distance, ~0,
                            QueryTriggerInteraction.Ignore))
                    {
                        grid.Hit[n] = true;
                        grid.Layer[n] = hit.collider != null ? hit.collider.gameObject.layer : -1;
                        grid.Y[n] = hit.point.y;
                        grid.SyncHits++;
                    }
                    else
                    {
                        grid.Layer[n] = -1;
                        grid.Y[n] = float.NaN;
                    }

                    // Advanced per ray, not per chunk: a chunk that throws has still cast the rays before
                    // the throw, and the report's denominator is "cells reached".
                    grid.SyncDone = n + 1;
                }
            }
            finally
            {
                if (grid.SyncClock.IsRunning) grid.SyncClock.Stop();
            }
        }

        /// <summary>One frame's worth of batched rays. The two NativeArrays are TempJob and disposed in
        /// the finally: a leaked TempJob allocation is a console warning per frame for the rest of the
        /// session.</summary>
        /// <param name="grid">The grid.</param>
        private static void BatchChunk(Grid grid)
        {
            var count = Math.Min(RaysPerFrame, grid.Cells - grid.BatchDone);
            if (count <= 0) return;

            // The FIRST array outside the try and the second INSIDE it: if the second allocation throws
            // (TempJob is a bounded allocator and 20,000 RaycastHits is not nothing), a `results` that was
            // never created cannot be disposed, but a `commands` that was would leak - and a leaked
            // TempJob allocation is a console warning every frame for the rest of the session.
            var commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            var results = default(NativeArray<RaycastHit>);

            try
            {
                results = new NativeArray<RaycastHit>(count, Allocator.TempJob);

                grid.BatchClock.Start();

                var parameters = new QueryParameters(~0, false, QueryTriggerInteraction.Ignore, false);

                for (var i = 0; i < count; i++)
                {
                    commands[i] = new RaycastCommand(
                        Centre(grid, grid.BatchDone + i), Vector3.down, parameters, grid.Distance);
                }

                RaycastCommand.ScheduleBatch(commands, results, 256, default(JobHandle)).Complete();

                for (var i = 0; i < count; i++)
                {
                    if (results[i].collider != null) grid.BatchHits++;
                }

                grid.BatchClock.Stop();
            }
            finally
            {
                if (grid.BatchClock.IsRunning) grid.BatchClock.Stop();
                if (commands.IsCreated) commands.Dispose();
                if (results.IsCreated) results.Dispose();
            }

            grid.BatchDone += count;
        }

        /// <summary>What the grid says: the layer histogram that decides the relief's raycast mask, the
        /// y range, the per-band coverage, and the coverage by distance from the player - the streaming
        /// test, which is the whole reason the key is pressed twice in one raid.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="grid">The finished grid.</param>
        private static void Report(StringBuilder text, Grid grid)
        {
            // Only the cells the synchronous pass actually reached, never the whole grid: a pass that
            // stopped early would otherwise report every ray it never cast as a miss, which is a
            // coverage figure that reads like a streaming hole and is not one.
            var rayed = grid.SyncDone;

            if (rayed < grid.Cells)
            {
                text.AppendLine(
                    $"NOTE: the synchronous pass stopped after {rayed} of {grid.Cells} cell(s), so every " +
                    "figure below is over those cells only.");
            }

            var minY = float.PositiveInfinity;
            var maxY = float.NegativeInfinity;
            var counts = new Dictionary<int, int>();

            for (var n = 0; n < rayed; n++)
            {
                if (!grid.Hit[n]) continue;

                if (grid.Y[n] < minY) minY = grid.Y[n];
                if (grid.Y[n] > maxY) maxY = grid.Y[n];

                counts.TryGetValue(grid.Layer[n], out var already);
                counts[grid.Layer[n]] = already + 1;
            }

            // "min n/a max n/a" would be the honest output of F(+inf)/F(-inf), but a reader would take it
            // for a formatting failure rather than for "nothing was hit", which is the finding.
            text.AppendLine(grid.SyncHits == 0
                ? $"hit y: no hits over {rayed} cell(s) - nothing under the grid at all"
                : $"hit y: min {F(minY)} max {F(maxY)} over {grid.SyncHits} hit(s)");

            var ordered = new List<KeyValuePair<int, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

            var parts = new List<string>();
            foreach (var pair in ordered)
            {
                parts.Add($"{LayerName(pair.Key)} {pair.Value} ({Pct(pair.Value, grid.SyncHits)})");
            }

            text.AppendLine($"hit layers ({ordered.Count}): {(parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray()))}");

            var terrainLayer = LayerMask.NameToLayer("Terrain");
            var terrain = 0;
            if (terrainLayer >= 0) counts.TryGetValue(terrainLayer, out terrain);

            text.AppendLine(
                $"Terrain layer ({(terrainLayer < 0 ? "absent in this game version" : terrainLayer.ToString(CultureInfo.InvariantCulture))}): " +
                $"{terrain} hit(s) ({Pct(terrain, grid.SyncHits)} of hits); everything else " +
                $"{grid.SyncHits - terrain} ({Pct(grid.SyncHits - terrain, grid.SyncHits)})");

            var floors = grid.Extent.Floors ?? new List<MapFloorDto>();

            foreach (var floor in floors)
            {
                if (floor == null) continue;

                var inBand = 0;

                for (var n = 0; n < rayed; n++)
                {
                    if (!grid.Hit[n]) continue;
                    if (grid.Y[n] < floor.MinY - 0.5f || grid.Y[n] > floor.MaxY + 0.5f) continue;
                    inBand++;
                }

                text.AppendLine(
                    $"band \"{floor.Name}\" level {floor.Level} y {F(floor.MinY)}..{F(floor.MaxY)}: {inBand} of " +
                    $"{rayed} cell(s) hit inside the band ({Pct(inBand, rayed)}), {Pct(inBand, grid.SyncHits)} of all hits");
            }

            // The streaming test. A ring whose coverage is far below the innermost ring's means the
            // colliders stream out with the player and the relief has to merge across captures the way
            // the pictures already do.
            //
            // The ring COUNT comes from the true distance to the farthest extent CORNER, not from the
            // larger of the two axis distances: a player standing at one corner of a 770x780 m map is
            // 1,096 m from the opposite corner but only 780 m from the far edge on either axis, so the
            // axis rule made three rings' worth of the most distant cells - exactly the cells the
            // streaming question is about - fold into one over-full last bucket and read as if they had
            // been measured at 700 m.
            var maxX = grid.MinX + grid.Width * CellSize;
            var maxZ = grid.MinZ + grid.Height * CellSize;

            var far = 0.0;

            foreach (var corner in new[]
                     {
                         new[] { grid.MinX, grid.MinZ }, new[] { grid.MinX, maxZ },
                         new[] { maxX, grid.MinZ }, new[] { maxX, maxZ }
                     })
            {
                var cdx = corner[0] - grid.At.x;
                var cdz = corner[1] - grid.At.z;
                var d = Math.Sqrt(cdx * cdx + cdz * cdz);
                if (d > far) far = d;
            }

            var rings = Math.Max(1, Math.Min((int)Math.Ceiling(far / RingSize), 64));

            var ringCells = new int[rings];
            var ringHits = new int[rings];
            var ringEither = new int[rings];

            // The previous press's mask, when this raid already has one for the same grid. "Either
            // press" is the number the merge design actually rests on: the relief takes the nearest
            // capture per cell, so what matters is not what ONE press saw but what the union of two
            // presses from opposite ends of the map saw.
            var key = $"{grid.Map}|{grid.Width}x{grid.Height}|{grid.Cells}";
            var previous = string.Equals(_previousKey, key, StringComparison.Ordinal) &&
                           _previousHit != null && _previousHit.Length == grid.Cells
                ? _previousHit
                : null;

            for (var n = 0; n < rayed; n++)
            {
                var centre = Centre(grid, n);
                var dx = centre.x - grid.At.x;
                var dz = centre.z - grid.At.z;
                var ring = (int)(Mathf.Sqrt(dx * dx + dz * dz) / RingSize);
                if (ring < 0) ring = 0;
                if (ring >= rings) ring = rings - 1;

                ringCells[ring]++;
                if (grid.Hit[n]) ringHits[ring]++;
                if (grid.Hit[n] || (previous != null && previous[n])) ringEither[ring]++;
            }

            var ringParts = new List<string>();

            for (var r = 0; r < rings; r++)
            {
                if (ringCells[r] == 0) continue;

                // The last ring's real upper edge is the corner distance, not a round hundred.
                var upper = r == rings - 1
                    ? F((float)far)
                    : ((int)((r + 1) * RingSize)).ToString(CultureInfo.InvariantCulture);

                ringParts.Add(
                    $"{(int)(r * RingSize)}-{upper} m: {ringHits[r]} of {ringCells[r]} " +
                    $"({Pct(ringHits[r], ringCells[r])})");
            }

            text.AppendLine(
                $"coverage by distance from the player at {F(grid.At.x)},{F(grid.At.z)} (farthest extent corner " +
                $"{F((float)far)} m, {rings} ring(s)) - {string.Join("; ", ringParts.ToArray())}");

            text.AppendLine(
                "CAVEAT: a ring's cells are ALL grid cells at that distance, not only cells over playable " +
                "ground, so a ring that crosses the map's edge, a lake or an out-of-bounds corner reads low " +
                "for a reason that has nothing to do with streaming. Compare rings to each other between the " +
                "two presses of one raid, not to 100 %.");

            if (previous == null)
            {
                text.AppendLine(
                    "either-press coverage: not available - this is the first press on this grid this " +
                    "session. Press the key again from the far end of the map and this line will compare.");
            }
            else
            {
                var eitherParts = new List<string>();
                var unionTotal = 0;

                for (var r = 0; r < rings; r++)
                {
                    if (ringCells[r] == 0) continue;
                    unionTotal += ringEither[r];
                    eitherParts.Add(
                        $"{(int)(r * RingSize)} m: {Pct(ringEither[r], ringCells[r])} " +
                        $"(+{Pct(ringEither[r] - ringHits[r], ringCells[r])})");
                }

                text.AppendLine(
                    $"either-press coverage (this press OR the last one, which is what the merge would " +
                    $"keep): {unionTotal} of {rayed} cell(s) ({Pct(unionTotal, rayed)}) against " +
                    $"{Pct(grid.SyncHits, rayed)} for this press alone - per ring: " +
                    $"{string.Join("; ", eitherParts.ToArray())}");
            }

            // Kept for the next press. The union, not this press's mask, so a third press compares
            // against everything seen so far - the same rule the picture merge uses.
            var merged = new bool[grid.Cells];

            for (var n = 0; n < grid.Cells; n++)
            {
                merged[n] = (n < rayed && grid.Hit[n]) || (previous != null && previous[n]);
            }

            _previousHit = merged;
            _previousKey = key;
        }

        /// <summary>The union of every press's hit mask so far, and the grid it belongs to. Static, so
        /// the second press of a raid can say what the two presses TOGETHER saw - the question the relief's
        /// merge rule turns on. About 150 KB on Customs, freed when the grid shape or the map changes.
        /// Goes with the rest of this throwaway file.</summary>
        private static bool[] _previousHit;

        private static string _previousKey = "";

        // --- experiment 3: shaders, cameras, layers -----------------------------------------------

        /// <summary>Experiment 3, which runs in a raid AND in the menu: what is loaded, what draws, and
        /// which layer nothing at all is on. Returns the candidate private layer, or -1.</summary>
        /// <param name="text">The block being built.</param>
        internal static int Experiment3(StringBuilder text)
        {
            text.AppendLine("--- EXPERIMENT 3 - shaders, cameras, layers ---");

            var shaders = Resources.FindObjectsOfTypeAll<Shader>();
            text.AppendLine($"{(shaders == null ? 0 : shaders.Length)} Shader object(s) loaded");

            foreach (var wanted in WantedShaders)
            {
                var loaded = 0;
                var supported = 0;

                foreach (var shader in shaders ?? new Shader[0])
                {
                    if (shader == null || !string.Equals(shader.name, wanted, StringComparison.Ordinal)) continue;
                    loaded++;
                    if (shader.isSupported) supported++;
                }

                var found = Shader.Find(wanted);

                text.AppendLine(
                    $"shader \"{wanted}\": loaded {loaded}, isSupported {supported} of {loaded}, " +
                    $"Shader.Find {(found == null ? "null" : "found, isSupported " + YesNo(found.isSupported))}");
            }

            var cameras = new Dictionary<int, Camera>();
            var enabledMask = 0;

            foreach (var camera in Camera.allCameras ?? new Camera[0])
            {
                if (camera != null) cameras[camera.GetInstanceID()] = camera;
            }

            foreach (var camera in FindObjectsOfType<Camera>(true) ?? new Camera[0])
            {
                if (camera != null) cameras[camera.GetInstanceID()] = camera;
            }

            text.AppendLine($"{cameras.Count} camera(s):");

            foreach (var camera in cameras.Values)
            {
                var live = camera.enabled && camera.gameObject.activeInHierarchy;
                if (live) enabledMask |= camera.cullingMask;

                text.AppendLine(
                    $"  camera \"{camera.name}\" enabled {YesNo(camera.enabled)} inHierarchy " +
                    $"{YesNo(camera.gameObject.activeInHierarchy)} depth {F(camera.depth)} target " +
                    $"{(camera.targetTexture == null ? "none" : camera.targetTexture.width + "x" + camera.targetTexture.height)} " +
                    $"mask 0x{camera.cullingMask:X8} = [{MaskNames(camera.cullingMask)}]");
            }

            text.AppendLine($"every enabled camera's masks together: 0x{enabledMask:X8} = [{MaskNames(enabledMask)}]");

            // BOTH counts, and the candidate test uses the one that includes INACTIVE renderers. EFT
            // hides distant geometry by switching renderers and whole GameObjects off - the whole reason
            // MapCapture.ForceCulling exists - so "no active renderer on this layer" is a statement about
            // where the player is standing, not about the layer. A layer that looks free from the menu and
            // has ten thousand deactivated renderers on it is not a private layer: the moment anything
            // reactivates one, the map view would draw a piece of the hideout.
            var active = FindObjectsOfType<Renderer>(false);
            var all = FindObjectsOfType<Renderer>(true);

            var perLayerActive = new int[32];
            var perLayerAll = new int[32];

            foreach (var renderer in active ?? new Renderer[0])
            {
                if (renderer == null) continue;
                var layer = renderer.gameObject.layer;
                if (layer >= 0 && layer < 32) perLayerActive[layer]++;
            }

            foreach (var renderer in all ?? new Renderer[0])
            {
                if (renderer == null) continue;
                var layer = renderer.gameObject.layer;
                if (layer >= 0 && layer < 32) perLayerAll[layer]++;
            }

            text.AppendLine(
                $"{(active == null ? 0 : active.Length)} active Renderer(s) and " +
                $"{(all == null ? 0 : all.Length)} including inactive, over 32 layers " +
                "(the candidate test below uses the inactive-included count):");

            var candidates = new List<int>();

            for (var layer = 0; layer < 32; layer++)
            {
                var name = LayerMask.LayerToName(layer);
                var free = perLayerAll[layer] == 0 && (enabledMask & (1 << layer)) == 0;
                if (free) candidates.Add(layer);

                text.AppendLine(
                    $"  layer {layer,2} \"{(string.IsNullOrEmpty(name) ? "" : name)}\" renderers active " +
                    $"{perLayerActive[layer],6} incl. inactive {perLayerAll[layer],6} " +
                    $"in an enabled camera's mask {YesNo((enabledMask & (1 << layer)) != 0)}" +
                    $"{(free ? "  <- candidate private layer" : "")}");
            }

            var chosen = candidates.Count == 0 ? -1 : candidates[candidates.Count - 1];

            text.AppendLine(
                $"candidate private layer(s): {candidates.Count} " +
                $"[{string.Join(",", candidates.ConvertAll(l => l.ToString(CultureInfo.InvariantCulture)).ToArray())}]; " +
                $"chosen {chosen}");

            text.AppendLine(
                $"RenderSettings.fog {YesNo(RenderSettings.fog)} mode {RenderSettings.fogMode} density " +
                $"{F(RenderSettings.fogDensity)}; ambient mode {RenderSettings.ambientMode} intensity " +
                $"{F(RenderSettings.ambientIntensity)}");

            text.AppendLine();
            return chosen;
        }

        // --- output ------------------------------------------------------------------------------

        /// <summary>Runs one step and, on any exception, writes the experiment's FAILED line instead of
        /// letting it out. Returns whether it ran clean, so a caller can stop after a failed setup
        /// rather than measure nothing over and over.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="experiment">Which experiment, for the line.</param>
        /// <param name="work">The step.</param>
        internal static bool Guard(StringBuilder text, int experiment, Action work)
        {
            try
            {
                work();
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    text.AppendLine($"EXPERIMENT {experiment} FAILED: {ex.GetType().Name}: {ex.Message}");
                    text.AppendLine($"  at {OneFrame(ex)}");
                }
                catch (Exception)
                {
                    // A diagnostic that throws while writing down that it threw is out of ideas.
                }

                return false;
            }
        }

        /// <summary>The first line of a stack trace, which is the only part worth a text file.</summary>
        /// <param name="ex">The exception.</param>
        private static string OneFrame(Exception ex)
        {
            var trace = ex.StackTrace;
            if (string.IsNullOrEmpty(trace)) return "no stack trace";

            var end = trace.IndexOf('\n');
            return (end < 0 ? trace : trace.Substring(0, end)).Trim();
        }

        /// <summary>Appends a block to BepInEx/plugins/QuestTree/captures/&lt;stem&gt;.meshprobe.txt -
        /// beside the capture folders rather than inside one, so nothing that reads a capture ever sees
        /// it. Appended rather than replaced because the answers come from TWO presses of one raid,
        /// and a second press that overwrote the first would destroy the streaming measurement.</summary>
        /// <param name="stem">The map's name, sanitised, or "menu".</param>
        /// <param name="text">The block.</param>
        internal static void Append(string stem, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var dir = MapCapture.ProbeCapturesRoot();
                if (dir == null) return;

                File.AppendAllText(Path.Combine(dir, $"{stem}.meshprobe.txt"), text);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the mesh probe could not write its file ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        internal static string Stem(string map)
        {
            if (string.IsNullOrEmpty(map)) return "unknown";

            var stem = new StringBuilder(map.Length);

            foreach (var c in map)
            {
                stem.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return stem.Length == 0 ? "unknown" : stem.ToString();
        }

        private string MapName()
        {
            try
            {
                var map = _gameWorld != null && _gameWorld.MainPlayer != null ? _gameWorld.MainPlayer.Location : null;
                if (string.IsNullOrEmpty(map)) map = _gameWorld != null ? _gameWorld.LocationId : null;
                return string.IsNullOrEmpty(map) ? "unknown" : map;
            }
            catch
            {
                return "unknown";
            }
        }

        // --- small helpers -----------------------------------------------------------------------

        /// <summary>Whether a box (or, for a zero-size one, a point) is within
        /// <see cref="ColumnRadius"/> of the player in XZ.</summary>
        /// <param name="bounds">The renderer's world bounds.</param>
        /// <param name="position">Its transform position, for a box with no size.</param>
        /// <param name="at">The player's position.</param>
        private static bool InColumn(Bounds bounds, Vector3 position, Vector3 at)
        {
            float dx;
            float dz;

            if (bounds.size == Vector3.zero)
            {
                dx = position.x - at.x;
                dz = position.z - at.z;
            }
            else
            {
                dx = Mathf.Max(0f, Mathf.Abs(bounds.center.x - at.x) - bounds.extents.x);
                dz = Mathf.Max(0f, Mathf.Abs(bounds.center.z - at.z) - bounds.extents.z);
            }

            return dx * dx + dz * dz <= ColumnRadius * ColumnRadius;
        }

        internal static string MaskNames(int mask)
        {
            var names = new List<string>();

            for (var i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;

                var name = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(name) ? $"#{i}" : $"{name}({i})");
            }

            return names.Count == 0 ? "nothing" : string.Join(", ", names.ToArray());
        }

        internal static string LayerName(int layer)
        {
            if (layer < 0) return "unknown(-1)";

            var name = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(name) ? $"#{layer}" : $"{name}({layer})";
        }

        /// <summary>The full hierarchy path of a transform, root first.</summary>
        /// <param name="transform">The transform.</param>
        internal static string Path_(Transform transform)
        {
            var parts = new List<string>();
            var walk = transform;
            var depth = 0;

            while (walk != null && depth < 24)
            {
                parts.Add(walk.name);
                walk = walk.parent;
                depth++;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static int Chunks(int cells) => cells <= 0 ? 0 : (cells + RaysPerFrame - 1) / RaysPerFrame;

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        internal static string Ms(Stopwatch clock) =>
            clock.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Per(Stopwatch clock, int count) =>
            count <= 0 ? "n/a" : (clock.Elapsed.TotalMilliseconds * 1000.0 / count).ToString("0.00", CultureInfo.InvariantCulture);

        internal static string Pct(int part, int whole) =>
            whole <= 0 ? "n/a" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + " %";

        internal static string YesNo(bool value) => value ? "yes" : "no";

        internal static string F(float v) =>
            float.IsNaN(v) || float.IsInfinity(v) ? "n/a" : v.ToString("0.00", CultureInfo.InvariantCulture);

        internal static string Now() =>
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// THROWAWAY, with <see cref="MeshProbe"/>: the menu half of the same key. Installed from
    /// <see cref="Plugin"/> on a DontDestroyOnLoad object, it polls the probe key and does nothing
    /// else until it is pressed. A press in a RAID is ignored, because the raid watcher owns the key
    /// there and two probes on one press would write two files - but "in a raid" is a narrower question
    /// than "is there a GameWorld", because the HIDEOUT has one too; see <see cref="InRaid"/>, which the
    /// first menu press of this key was silently swallowed by.
    ///
    /// Every press this class declines is logged at INFO with its reason. It used to be Debug, and the
    /// result was a key that produced no file, no view and no visible line at all.
    /// </summary>
    internal sealed class MenuMeshProbe : MonoBehaviour
    {
        /// <summary>Puts the watcher on a DontDestroyOnLoad object, once. Never throws.</summary>
        public static void Install()
        {
            try
            {
                if (_instance != null) return;
                if (ModEnvironment.IsHeadlessClient) return;

                var go = new GameObject("QuestTreeMenuMeshProbe");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<MenuMeshProbe>();

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe (menu) watching {MeshProbe.BoundKeyText()}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: could not install the menu mesh probe ({ex.Message}).");
            }
        }

        private static MenuMeshProbe _instance;

        private bool _warned;
        private bool _alive;
        private bool _busy;
        private int _press;
        private MeshProbeView _view;

        private void Update() => Poll("its own Update", this);

        /// <summary>THROWAWAY DIAGNOSTIC (2026-09-23): the same poll from TrackerHotkey.Update, which is
        /// proven to run in the menu (it is what opens the tracker on Ctrl+Q), because two sessions of
        /// F9 presses reached this object's own Update not at all - no press line, no "ignored" line.
        /// Whichever caller sees the key first handles it; the other is refused for that frame.</summary>
        internal static void PollFromHotkey(MonoBehaviour host)
        {
            try { if (host != null) _instance?.Poll("TrackerHotkey", host); }
            catch (Exception) { }
        }

        /// <summary>The MonoBehaviour the menu coroutine and the test view are hosted on. The watcher's
        /// own DontDestroyOnLoad object turned out never to tick in this game's menu (its Update was
        /// never seen, and StartCoroutine on it threw), so whatever polled the key - TrackerHotkey in
        /// practice, a child of the tracker's root canvas - is what runs the work. Recorded once so a
        /// press knows where its view lives.</summary>
        private MonoBehaviour _host;

        private int _handledFrame = -1;

        private void Poll(string via, MonoBehaviour host)
        {
            if (_handledFrame == Time.frameCount) return;
            if (host == null) return;

            try
            {
                // The test view lives on a DontDestroyOnLoad object, so a raid started while it is up
                // would carry it into the raid and draw a 512 px panel over the player's screen. One
                // check per frame, and it takes the view down through the same path the key does.
                if (_view != null && !_busy && InRaid())
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: a raid started with the mesh probe's test view open - tearing it down.");

                    _busy = true;
                    _press++;
                    StartCoroutine(Run(_press));
                    return;
                }

                // THROWAWAY DIAGNOSTIC (2026-09-23): a bound F9 in the menu produced no press and no
                // "ignored" line, so the raw key is logged the frame it goes down, before the shortcut
                // rules, and the first Update proves the watcher runs at all. Removed with the file.
                if (!_alive)
                {
                    _alive = true;
                    Plugin.LogSource?.LogInfo($"QuestTree: mesh probe (menu) polling from {via}.");
                }

                var bound = ModSettings.Ready && ModSettings.ProbeKey != null
                    ? ModSettings.ProbeKey.Value.MainKey
                    : KeyCode.None;
                if (bound != KeyCode.None && Input.GetKeyDown(bound))
                {
                    var blockers = "";
                    foreach (var key in new[] { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift,
                                                KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt })
                        if (Input.GetKey(key)) blockers += (blockers.Length == 0 ? "" : ", ") + key;
                    _handledFrame = Time.frameCount;
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: mesh probe (menu) saw {bound} go down via {via}; shortcut test {(MeshProbe.Pressed() ? "passes" : "fails")}" +
                        (blockers.Length == 0 ? ", no modifier held." : $", held: {blockers}."));
                }

                if (!MeshProbe.Pressed()) return;

                // The raid watcher owns the key in a raid. Checked only on a press, so it costs
                // nothing per frame. At INFO, not Debug: the first menu press of this key did nothing at
                // all and left one Debug line nobody sees, so the session could not tell "the key never
                // reached us" from "we dropped it on purpose".
                if (InRaid())
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the menu mesh probe ignored a press: in a raid (the raid watcher owns " +
                        "the key there).");
                    return;
                }

                if (_busy)
                {
                    Plugin.LogSource?.LogInfo(
                        "QuestTree: the menu mesh probe ignored a press: busy with the last one.");
                    return;
                }

                _press++;
                _host = host;

                // The coroutine runs on the CALLER, never on this object: on 2026-09-23 this object's own
                // Update was never called in the menu and StartCoroutine on it threw (an exception with
                // an empty message), which left _busy set for the session because it was set first.
                // _busy is set only once the coroutine has actually started.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe (menu) press {_press} via {via} - own object " +
                    $"{(this == null ? "destroyed" : gameObject.activeInHierarchy ? "active" : "inactive")}, " +
                    $"running on {host.GetType().Name} '{host.gameObject.name}'.");

                host.StartCoroutine(Run(_press));
                _busy = true;
            }
            catch (Exception ex)
            {
                if (_warned) return;
                _warned = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the menu mesh probe key failed ({ex.GetType().Name}: {ex.Message}) at " +
                    $"{(ex.StackTrace ?? "").Split('\n')[0].Trim()}");
            }
        }

        /// <summary>Whether this is a RAID, which is not the same question as whether a GameWorld exists.
        ///
        /// It used to be: "Singleton&lt;GameWorld&gt;.Instantiated" alone, and the first menu press of this
        /// key was silently dropped because of it. THE HIDEOUT HAS A GameWorld TOO. Its player is an
        /// EFT.HideoutPlayer, the tracker's own menu is opened from a screen that has one, and the raid
        /// watcher's count was 0 the whole time - so the one test that mattered said "raid", the press was
        /// discarded, and no file and no visible log line were written.
        ///
        /// Asked two independent ways now, and the Singleton half has to identify what KIND of world it is:
        ///   - the raid watcher's own count, which is exact (it is created by GameWorld.OnGameStarted and
        ///     dies with the world) and depends on the game registering nothing;
        ///   - or a GameWorld whose MainPlayer exists and is not a HideoutPlayer. A null MainPlayer counts
        ///     as NOT a raid: it means a world part way through loading, and a menu press there is a press
        ///     in the menu.
        /// </summary>
        private static bool InRaid()
        {
            if (MeshProbe.RaidWatchers > 0) return true;

            try
            {
                if (!Singleton<GameWorld>.Instantiated) return false;

                var world = Singleton<GameWorld>.Instance;
                if (world == null) return false;

                var player = world.MainPlayer;
                if (player == null) return false;

                return !(player is HideoutPlayer);
            }
            catch (Exception ex)
            {
                // A test that cannot be made is a test that says "menu": the alternative is dropping every
                // press for the rest of the session with no way to tell why.
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe could not tell raid from menu ({ex.Message}).");
                return false;
            }
        }

        /// <summary>Where this half writes, for the log lines - so a press that dies can still be traced to
        /// a file. "unknown" when the plugin has no file location.</summary>
        private static string MenuFilePath()
        {
            try
            {
                var dir = MapCapture.ProbeCapturesRoot();
                return dir == null ? "unknown (the plugin has no file location)" : Path.Combine(dir, "menu.meshprobe.txt");
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>A press in the menu: run experiment 3, then 1, then 4 and show the test view - or, if it
        /// is already up, tear it down and write the leak check.</summary>
        /// <param name="press">Which press this is.</param>
        private IEnumerator Run(int press)
        {
            try
            {
                if (_view != null)
                {
                    var closing = new StringBuilder();
                    closing.AppendLine($"=== QuestTree mesh probe (THROWAWAY) - menu press {press}, teardown - {MeshProbe.Now()} ===");

                    var view = _view;

                    MeshProbe.Guard(closing, 4, () => view.Report(closing));

                    // _view is cleared only AFTER the teardown has returned, and only if it returned. A
                    // throw inside TearDown used to orphan a live view forever - the reference was already
                    // gone, so the next press could not find it to close and built a SECOND camera, light
                    // and canvas on top of the first. TearDown destroys everything in a finally, so it
                    // returning at all is enough to know the view is on its way out.
                    var torn = MeshProbe.Guard(closing, 4, () => view.TearDown(closing));
                    if (torn) _view = null;
                    else closing.AppendLine("NOTE: the teardown threw, so the view is kept - press again to retry it.");

                    // A frame, so Unity's own deferred destruction has happened before the count: a
                    // leak check run in the same frame as the Destroy calls cannot fail.
                    yield return null;

                    MeshProbe.Guard(closing, 4, () => MeshProbeView.LeakCheck(closing, view));
                    closing.AppendLine();

                    MeshProbe.Append("menu", closing.ToString());
                    Plugin.LogSource?.LogInfo("QuestTree: mesh probe (menu) tore the test view down.");
                    yield break;
                }

                // Said BEFORE any work, and at Info: a press whose experiment kills the process must still
                // have left a line saying it started and where its answers were going.
                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe (menu) press {press} starting - experiment 3, then 1, then 4, " +
                    $"writing to {MenuFilePath()}");

                var header = new StringBuilder();
                header.AppendLine($"=== QuestTree mesh probe (THROWAWAY) - menu press {press} - {MeshProbe.Now()} ===");
                header.AppendLine($"mod {ModInfo.Stamp}, unity {Application.unityVersion}, in the menu (not a raid)");
                header.AppendLine(
                    "buffer targets are never written - doing so crashed the engine on 2026-09-22 " +
                    "(Mesh.set_vertexBufferTarget -> d3d11).");
                MeshProbe.Append("menu", header.ToString());

                // Each experiment appended as it finishes, never all at the end. Experiment 1 can take the
                // process down natively - it has once - and a crash must cost only the experiment that
                // crashed.
                var three = new StringBuilder();
                var layer = -1;
                MeshProbe.Guard(three, 3, () => layer = MeshProbe.Experiment3(three));
                MeshProbe.Append("menu", three.ToString());

                yield return null;

                // Experiment 1 HERE, in the menu, before it is ever allowed to run in a raid: the hideout
                // has non-readable meshes of its own, so the question can be asked where the cost of a
                // crash is a menu rather than somebody's raid. The marker line its summary writes is what
                // unlocks the raid side.
                var one = new StringBuilder();
                yield return MeshProbe.Experiment1(one, null);
                MeshProbe.Append("menu", one.ToString());

                yield return null;

                var four = new StringBuilder();
                var chosen = layer;
                MeshProbe.Guard(four, 4, () => _view = MeshProbeView.Create(four, chosen, _host));
                four.AppendLine();
                MeshProbe.Append("menu", four.ToString());

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: mesh probe (menu) press {press} wrote {MenuFilePath()}. The 512x512 test view " +
                    "sits in the BOTTOM LEFT corner and consumes every click inside it while it is open - " +
                    "press the key again to tear it down.");
            }
            finally
            {
                _busy = false;
            }
        }
    }

    /// <summary>
    /// THROWAWAY, with <see cref="MeshProbe"/>: experiment 4, the viewer plumbing. A screen-space
    /// canvas with a RawImage over a RenderTexture, a disabled private camera, a procedural cube and
    /// heightfield, and the two ways of getting them onto the texture:
    ///
    ///   A - Graphics.DrawMesh(mesh, matrix, material, layer, camera) from LateUpdate followed by a
    ///       manual camera.Render() under MapCapture.RenderOnce's rules (fog off, own light on,
    ///       everything restored in the finally);
    ///   B - the fallback: an ENABLED camera with cullingMask 0 and a CommandBuffer.DrawMesh on
    ///       CameraEvent.AfterForwardOpaque.
    ///
    /// Path A runs for <see cref="PathSeconds"/>, then B does, and each is judged by reading the
    /// render texture's centre back and counting the pixels that differ from the clear colour. That
    /// is the check that can fail: a path that draws nothing reads back as 0 of 64.
    /// </summary>
    internal sealed class MeshProbeView : MonoBehaviour
    {
        private const int Size = 512;
        private const int SampleSize = 8;

        /// <summary>Seconds each path is given before it is judged and the next one tried.</summary>
        private const float PathSeconds = 3f;

        /// <summary>Frames the render clock is averaged over.</summary>
        private const int TimedFrames = 60;

        /// <summary>The clear colour, and what a pixel is compared against.</summary>
        private static readonly Color Clear = new Color(0.17f, 0.18f, 0.19f);

        /// <summary>Which path is drawing.</summary>
        private enum Path
        {
            DrawMeshManualRender,
            CommandBufferEnabledCamera
        }

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        private Canvas _canvas;
        private RawImage _image;
        private RenderTexture _rt;
        private Camera _camera;
        private Light _light;
        private Mesh _cube;
        private Mesh _field;
        private Material _material;
        private Texture2D _checker;
        private Texture2D _readback;
        private CommandBuffer _commands;

        private int _drawLayer;
        private int _privateMask;
        private Path _path = Path.DrawMeshManualRender;
        private float _pathStarted;
        private bool _switched;
        private bool _reported;

        private float _yaw = 45f;
        private float _pitch = 30f;
        private float _distance = 22f;
        private Vector3 _focus = new Vector3(0f, 0.5f, 0f);

        private readonly Stopwatch _clock = new Stopwatch();
        private int _frames;

        private int _litA = -1;
        private int _litB = -1;
        private double _msA = -1;
        private double _msB = -1;
        private int _framesA;
        private int _framesB;

        /// <summary>What a BLANK frame of this render texture reads back as - the clear colour after
        /// whatever colour-space conversion the texture does, measured rather than computed. Everything
        /// else is compared against this.</summary>
        private Color32 _reference;

        private bool _haveReference;
        private int _referenceFrames;

        /// <summary>How many of the blank frame's own centre pixels differed from its first one. Anything
        /// but 0 means the reference is not a flat clear and the whole pixel test is unsound, which the
        /// report says out loud.</summary>
        private int _referenceSpread;

        private bool _referenceUniform;

        private Color32 _sampleA;
        private Color32 _sampleB;

        /// <summary>Builds the whole thing and writes down what it built. Everything it creates is
        /// recorded in <see cref="_created"/>, which is what the leak check counts.</summary>
        /// <param name="text">The block being built.</param>
        /// <param name="privateLayer">The layer experiment 3 found nothing on, or -1.</param>
        /// <param name="host">The MonoBehaviour that polled the key; the view is parented beside it so it ticks.</param>
        internal static MeshProbeView Create(StringBuilder text, int privateLayer, MonoBehaviour host)
        {
            text.AppendLine("--- EXPERIMENT 4 - viewer plumbing ---");

            // A plain object of the menu scene, NOT DontDestroyOnLoad: the watcher's own DDOL object never
            // ticked in this menu (2026-09-23), and a view whose LateUpdate never runs draws nothing and
            // reports "NOT RUN" for both paths. Parented beside the MonoBehaviour that polled the key,
            // which is proven to tick; it dies with the menu scene, which for a throwaway is fine.
            var go = new GameObject("QuestTreeMeshProbeView");
            if (host != null && host.transform.parent != null) go.transform.SetParent(host.transform.parent, false);

            var view = go.AddComponent<MeshProbeView>();
            view._created.Add(go);

            try
            {
                view.Build(text, privateLayer);
            }
            catch (Exception)
            {
                // A half-built view is a leak in the menu, which is the one thing this experiment must
                // not cause. Torn down here, and the exception left to the caller's Guard to write.
                try { view.TearDown(text); }
                catch (Exception) { /* nothing further to try. */ }
                throw;
            }

            return view;
        }

        private void Build(StringBuilder text, int privateLayer)
        {
            _drawLayer = privateLayer >= 0 ? privateLayer : 0;
            _privateMask = privateLayer >= 0 ? 1 << privateLayer : 0;

            // The canvas. Its own, at a sorting order nothing competes with, and with a raycaster so
            // the RawImage can take a drag.
            var canvasGo = new GameObject("QuestTreeMeshProbeCanvas", typeof(Canvas), typeof(GraphicRaycaster));
            _created.Add(canvasGo);

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30000;

            _rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
            _rt.name = "QuestTreeMeshProbeRT";
            var rtOk = _rt.Create();
            _created.Add(_rt);

            var imageGo = new GameObject("QuestTreeMeshProbeImage", typeof(RectTransform), typeof(RawImage));
            imageGo.transform.SetParent(canvasGo.transform, worldPositionStays: false);
            _created.Add(imageGo);

            _image = imageGo.GetComponent<RawImage>();
            _image.texture = _rt;
            _image.raycastTarget = true;

            var rect = imageGo.GetComponent<RectTransform>();
            // BOTTOM LEFT, not the top right. raycastTarget has to stay on - a drag cannot be received
            // without it - so a 512 px square of the menu is dead to the mouse while the view is open, and
            // the top right is where the menu keeps the things a player reaches for (the character and
            // money panels, the close button). The bottom left corner is the emptiest part of every screen
            // this mod's panel appears over.
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.sizeDelta = new Vector2(Size, Size);
            rect.anchoredPosition = new Vector2(16f, 16f);

            var orbit = imageGo.AddComponent<MeshProbeOrbit>();
            orbit.View = this;

            // The camera. Unparented, disabled, and drawing ONLY the private layer, so nothing the
            // menu or the hideout shows can reach it and it can reach nothing of theirs.
            var cameraGo = new GameObject("QuestTreeMeshProbeCamera", typeof(Camera));
            _created.Add(cameraGo);

            _camera = cameraGo.GetComponent<Camera>();
            _camera.enabled = false;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Clear;
            _camera.orthographic = false;
            _camera.fieldOfView = 45f;
            _camera.nearClipPlane = 0.5f;
            _camera.farClipPlane = 100f;
            _camera.targetTexture = _rt;
            _camera.cullingMask = _privateMask;
            _camera.depth = -50f;
            _camera.useOcclusionCulling = false;
            _camera.allowMSAA = false;

            var lightGo = new GameObject("QuestTreeMeshProbeLight", typeof(Light));
            lightGo.layer = _drawLayer;
            _created.Add(lightGo);

            _light = lightGo.GetComponent<Light>();
            _light.type = LightType.Directional;
            _light.intensity = 1.2f;
            _light.shadows = LightShadows.None;
            _light.cullingMask = _privateMask;
            _light.enabled = false;
            lightGo.transform.rotation = Quaternion.Euler(50f, 210f, 0f);

            _checker = BuildChecker(64);
            _created.Add(_checker);

            _cube = BuildCube(3f);
            _created.Add(_cube);

            _field = BuildField(32, 16f);
            _created.Add(_field);

            var shaderName = "none";
            Shader shader = null;

            foreach (var wanted in MeshProbe.ViewerShaders)
            {
                shader = Shader.Find(wanted);
                if (shader == null) continue;
                shaderName = wanted;
                break;
            }

            if (shader != null)
            {
                _material = new Material(shader);
                _material.mainTexture = _checker;
                if (_material.HasProperty("_Glossiness")) _material.SetFloat("_Glossiness", 0f);
                _created.Add(_material);
            }

            _readback = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false);
            _created.Add(_readback);

            _commands = new CommandBuffer { name = "QuestTreeMeshProbe" };

            _pathStarted = Time.realtimeSinceStartup;
            Place();

            text.AppendLine(
                $"canvas sortingOrder {_canvas.sortingOrder}, RawImage {Size}x{Size} at the BOTTOM LEFT " +
                $"(16 px in), raycastTarget {MeshProbe.YesNo(_image.raycastTarget)} - which means every click " +
                $"inside those {Size}x{Size} pixels is CONSUMED by the probe and does not reach the menu " +
                $"underneath, for as long as the view is open. Press the probe key again to close it.");
            text.AppendLine(
                $"RenderTexture {Size}x{Size} ARGB32 depth 24: Create() {MeshProbe.YesNo(rtOk)}, IsCreated " +
                $"{MeshProbe.YesNo(_rt.IsCreated())}");
            text.AppendLine(
                $"camera enabled {MeshProbe.YesNo(_camera.enabled)} fov {MeshProbe.F(_camera.fieldOfView)} near " +
                $"{MeshProbe.F(_camera.nearClipPlane)} far {MeshProbe.F(_camera.farClipPlane)} mask " +
                $"0x{_camera.cullingMask:X8} = [{MeshProbe.MaskNames(_camera.cullingMask)}]");
            var layerNote = privateLayer < 0
                ? "NONE found - the camera's mask is 0, so path A is expected to draw nothing and the " +
                  "CommandBuffer fallback is the only path"
                : MeshProbe.LayerName(privateLayer);

            text.AppendLine($"private layer {privateLayer} ({layerNote}), meshes drawn on layer {_drawLayer}");
            text.AppendLine(
                $"light directional, intensity {MeshProbe.F(_light.intensity)}, mask 0x{_light.cullingMask:X8}, " +
                $"enabled only for the render itself");
            text.AppendLine(
                $"cube: {_cube.vertexCount} vertices, {_cube.triangles.Length / 3} triangles, indexFormat {_cube.indexFormat}");
            text.AppendLine(
                $"heightfield: 32x32 cells, {_field.vertexCount} vertices, {_field.triangles.Length / 3} triangles, " +
                $"indexFormat {_field.indexFormat}");
            text.AppendLine($"checkerboard: {_checker.width}x{_checker.height} {_checker.format}");
            text.AppendLine(
                $"material: shader \"{shaderName}\"" +
                $"{(shader == null ? " - NONE of the four resolved, so nothing can be drawn" : ", isSupported " + MeshProbe.YesNo(shader.isSupported))}");
            text.AppendLine(
                $"path A (Graphics.DrawMesh + manual Render) runs for {MeshProbe.F(PathSeconds)} s, then path B " +
                $"(enabled camera, cullingMask 0, CommandBuffer.DrawMesh on AfterForwardOpaque); each is judged by " +
                $"reading back the render texture's centre {SampleSize}x{SampleSize} and counting the pixels that " +
                $"differ from a BLANK FRAME OF THIS TEXTURE, sampled first and reported below - not from the " +
                $"clear colour as a number, which is a different value in a linear texture.");
        }

        // --- the two paths ------------------------------------------------------------------------

        private bool _ticked;

        private void LateUpdate()
        {
            try
            {
                if (!_ticked)
                {
                    _ticked = true;
                    Plugin.LogSource?.LogInfo("QuestTree: mesh probe (menu) test view LateUpdate is running.");
                }

                if (_camera == null || _material == null) return;

                Place();

                // The REFERENCE first, before anything is drawn: two blank renders, then a sample of the
                // centre. That sample is what "blank" looks like in this render texture, and it is the
                // only thing a drawn frame may be compared against.
                //
                // Comparing against the clear colour as a Color32 - (43,46,48) for (0.17,0.18,0.19) - is
                // wrong whenever the project is in linear colour space, because the render texture then
                // stores the LINEAR value of that colour, about (6,6,7), and every blank pixel differs
                // from (43,46,48) by more than the tolerance. Both paths would have read 64 of 64 lit and
                // this experiment would have reported that both of them worked without either having
                // drawn a triangle.
                if (!_haveReference)
                {
                    RenderBlank();
                    _referenceFrames++;

                    if (_referenceFrames >= 2)
                    {
                        _referenceSpread = Sample(out _reference, out var uniform);
                        _referenceUniform = uniform;
                        _haveReference = true;

                        // The clock starts when path A starts, not when the view was built.
                        _pathStarted = Time.realtimeSinceStartup;
                    }

                    return;
                }

                if (_path == Path.DrawMeshManualRender)
                {
                    RenderPathA();
                }
                else if (!Hooked)
                {
                    // Without the camera callbacks there is no frame counter for path B, and the pixel
                    // check below needs one. The render is then untimed, which the report says.
                    _frames++;

                    // And with the callbacks missing there is nothing to switch the light off again, so it
                    // is never switched on - see OnPre.
                    if (_light != null) _light.enabled = false;
                }

                // Judged once a path has had a few frames, and the switch made on the clock rather than
                // on a key, so the file records BOTH answers from one press.
                if (_path == Path.DrawMeshManualRender && _frames >= 2 && _litA < 0)
                {
                    _litA = Sample(out _sampleA, out _);
                }

                if (_path == Path.CommandBufferEnabledCamera && _frames >= 2 && _litB < 0)
                {
                    _litB = Sample(out _sampleB, out _);
                }

                if (!_switched && Time.realtimeSinceStartup - _pathStarted >= PathSeconds)
                {
                    _switched = true;
                    SwitchToB();
                    return;
                }

                // Path B has had its turn: write both verdicts down and stay on whichever drew
                // something, without waiting for the key to be pressed again.
                if (_switched && !_reported && Time.realtimeSinceStartup - _pathStarted >= PathSeconds)
                {
                    var verdict = new StringBuilder();
                    Report(verdict);
                    MeshProbe.Append("menu", verdict.ToString());
                }
            }
            catch (Exception ex)
            {
                // Once, and then the view goes quiet rather than filling the console at 60 Hz.
                if (_broke) return;
                _broke = true;
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the mesh probe view failed ({ex.GetType().Name}: {ex.Message}) - it stops drawing.");
                _material = null;
            }
        }

        private bool _broke;

        /// <summary>Path A: queue both meshes for this camera, then render it by hand under
        /// MapCapture.RenderOnce's rules. Everything the render changes is put back by the statement
        /// that changed it, in the finally.</summary>
        private void RenderPathA()
        {
            Graphics.DrawMesh(_cube, Matrix4x4.TRS(new Vector3(0f, 2f, 0f), Quaternion.identity, Vector3.one),
                _material, _drawLayer, _camera);
            Graphics.DrawMesh(_field, Matrix4x4.identity, _material, _drawLayer, _camera);

            var fog = RenderSettings.fog;

            try
            {
                RenderSettings.fog = false;
                _light.enabled = true;

                _clock.Reset();
                _clock.Start();
                _camera.Render();
                _clock.Stop();

                if (_framesA < TimedFrames)
                {
                    _msA = (_msA < 0 ? 0 : _msA) + _clock.Elapsed.TotalMilliseconds;
                    _framesA++;
                }

                _frames++;
            }
            finally
            {
                _light.enabled = false;
                RenderSettings.fog = fog;
            }
        }

        /// <summary>One render with NOTHING queued for the camera, under the same bracket as path A - the
        /// blank frame the reference is sampled from. The light is on for it too, so the reference is taken
        /// under the same lighting the drawn frames get and cannot differ from them for that reason.</summary>
        private void RenderBlank()
        {
            var fog = RenderSettings.fog;

            try
            {
                RenderSettings.fog = false;
                _light.enabled = true;
                _camera.Render();
            }
            finally
            {
                _light.enabled = false;
                RenderSettings.fog = fog;
            }
        }

        /// <summary>Path B: an enabled camera with nothing in its mask and a CommandBuffer that draws
        /// the two meshes after the opaque pass. The light stays on, because an enabled camera renders
        /// outside any code of ours that could switch it on and off around the render.</summary>
        private void SwitchToB()
        {
            _frames = 0;
            _litB = -1;
            _pathStarted = Time.realtimeSinceStartup;

            _commands.Clear();
            _commands.DrawMesh(_cube, Matrix4x4.TRS(new Vector3(0f, 2f, 0f), Quaternion.identity, Vector3.one),
                _material, 0, -1);
            _commands.DrawMesh(_field, Matrix4x4.identity, _material, 0, -1);

            _camera.RemoveAllCommandBuffers();
            _camera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, _commands);
            _camera.cullingMask = 0;
            _camera.enabled = true;

            // The light is NOT switched on here. An enabled camera renders every frame for as long as path
            // B lasts, and a light left enabled across that is a light left enabled in the MENU - and after
            // the menu, in the hideout, because this object survives the scene change. A per-light culling
            // mask is honoured in forward rendering and is not a guarantee under the deferred path this
            // game uses for those scenes, so the mask is not the safeguard: the light being off is. It goes
            // on in OnPre and off again in OnPost, for our camera only, which is the one render it is for.
            _path = Path.CommandBufferEnabledCamera;

            // An enabled camera renders inside Unity's loop, so the Stopwatch has to be started and
            // stopped by the camera's own callbacks rather than around a Render() call of ours.
            //
            // Each subscription is tracked SEPARATELY, and that is not pedantry: with one flag for both,
            // an onPostRender += that threw after onPreRender += had succeeded would leave _hooked false,
            // Unhook would return immediately, and a live delegate into this object would be called for
            // every camera in the game for the rest of the session - including after the object is
            // destroyed. Two flags, two independent removals.
            try
            {
                Camera.onPreRender += OnPre;
                _preHooked = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe could not hook onPreRender ({ex.Message}).");
            }

            try
            {
                Camera.onPostRender += OnPost;
                _postHooked = true;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe could not hook onPostRender ({ex.Message}).");
            }
        }

        private bool _preHooked;

        private bool _postHooked;

        /// <summary>Whether the frame timer is fully wired - both callbacks, not one of them.</summary>
        private bool Hooked => _preHooked && _postHooked;

        /// <summary>Our camera is about to render: start the clock and switch our light on for exactly that
        /// render. Only when BOTH callbacks are hooked - a light switched on by a callback that has no
        /// partner to switch it off again would stay on for the session.</summary>
        /// <param name="camera">The camera Unity is about to render.</param>
        private void OnPre(Camera camera)
        {
            if (camera != _camera) return;

            try
            {
                if (Hooked && _light != null) _light.enabled = true;
            }
            catch (Exception)
            {
                // An unlit test render is a fine plumbing test; a throw here is not worth a log line
                // sixty times a second.
            }

            _clock.Reset();
            _clock.Start();
        }

        /// <summary>Our camera has rendered: stop the clock and put the light back off. The light goes off
        /// FIRST and outside the counting, so no early return can skip it.</summary>
        /// <param name="camera">The camera Unity has just rendered.</param>
        private void OnPost(Camera camera)
        {
            if (camera != _camera) return;

            try
            {
                if (_light != null) _light.enabled = false;
            }
            catch (Exception)
            {
                // As above.
            }

            _clock.Stop();

            if (_framesB >= TimedFrames) return;

            _msB = (_msB < 0 ? 0 : _msB) + _clock.Elapsed.TotalMilliseconds;
            _framesB++;
            _frames++;
        }

        /// <summary>The orbit, from the drag and scroll handler.</summary>
        /// <param name="yaw">Degrees to add to the yaw.</param>
        /// <param name="pitch">Degrees to add to the pitch.</param>
        internal void Orbit(float yaw, float pitch)
        {
            _yaw += yaw;
            _pitch = Mathf.Clamp(_pitch + pitch, 5f, 85f);
        }

        /// <summary>The dolly, from the scroll handler.</summary>
        /// <param name="scroll">The wheel delta.</param>
        internal void Dolly(float scroll)
        {
            _distance = Mathf.Clamp(_distance * (1f - 0.1f * scroll), 3f, 90f);
        }

        private void Place()
        {
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            _camera.transform.position = _focus + rotation * new Vector3(0f, 0f, -_distance);
            _camera.transform.rotation = rotation;
        }

        // --- the checks that can fail --------------------------------------------------------------

        /// <summary>Reads the render texture's centre back and returns how many of its pixels differ from
        /// <see cref="_reference"/> - the measured blank frame - by more than three levels on any channel.
        /// Zero means the path drew nothing at all, which is the whole verdict experiment 4 exists for.
        ///
        /// On the reference pass itself <see cref="_haveReference"/> is still false, and the count is then
        /// taken against the frame's OWN first pixel instead, which measures whether the blank frame is
        /// flat. A blank frame that is not flat makes every later count meaningless, so it is reported.</summary>
        /// <param name="shown">The first pixel read, for the log line.</param>
        /// <param name="uniform">Whether every pixel read matched the first one.</param>
        private int Sample(out Color32 shown, out bool uniform)
        {
            var previous = RenderTexture.active;
            shown = new Color32(0, 0, 0, 0);
            uniform = true;

            try
            {
                RenderTexture.active = _rt;

                var half = SampleSize / 2;
                _readback.ReadPixels(new Rect(Size / 2 - half, Size / 2 - half, SampleSize, SampleSize), 0, 0, false);
                _readback.Apply(false);

                var pixels = _readback.GetPixels32();
                if (pixels == null || pixels.Length == 0) return 0;

                shown = pixels[0];
                var against = _haveReference ? _reference : pixels[0];

                var differ = 0;

                foreach (var pixel in pixels)
                {
                    if (Math.Abs(pixel.r - pixels[0].r) > 3 ||
                        Math.Abs(pixel.g - pixels[0].g) > 3 ||
                        Math.Abs(pixel.b - pixels[0].b) > 3)
                    {
                        uniform = false;
                    }

                    if (Math.Abs(pixel.r - against.r) <= 3 &&
                        Math.Abs(pixel.g - against.g) <= 3 &&
                        Math.Abs(pixel.b - against.b) <= 3)
                    {
                        continue;
                    }

                    differ++;
                }

                return differ;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>The two paths' verdicts, written once - by LateUpdate when path B has had its
        /// seconds, or by the teardown if the view was closed before that.</summary>
        /// <param name="text">The block being built.</param>
        internal void Report(StringBuilder text)
        {
            if (_reported)
            {
                text.AppendLine("the two paths' verdicts were written when path B finished.");
                return;
            }

            _reported = true;
            var total = SampleSize * SampleSize;

            // The reference, first, because every number under it is relative to this one.
            text.AppendLine(
                _haveReference
                    ? $"reference: a blank render of this texture reads back RGB " +
                      $"{_reference.r},{_reference.g},{_reference.b} at the centre (the camera was told to clear " +
                      $"to {(int)(Clear.r * 255)},{(int)(Clear.g * 255)},{(int)(Clear.b * 255)}, so this texture " +
                      $"is {(Math.Abs(_reference.r - (int)(Clear.r * 255)) <= 3 ? "gamma" : "linear or otherwise converted")}); " +
                      $"the blank frame was {(_referenceUniform ? "FLAT" : "NOT FLAT - " + _referenceSpread + " of " + total + " pixels differ, so the counts below are unsound")}"
                    : "reference: NOT TAKEN - no blank frame was sampled, so nothing below can be trusted.");

            text.AppendLine(
                $"path A (Graphics.DrawMesh + disabled camera, manual Render): lit " +
                $"{(_litA < 0 ? "not measured" : _litA + " of " + total)} " +
                $"({(_litA < 0 ? "n/a" : MeshProbe.Pct(_litA, total))}), centre sample RGB " +
                $"{(_litA < 0 ? "n/a" : _sampleA.r + "," + _sampleA.g + "," + _sampleA.b)}, render " +
                $"{(_framesA <= 0 ? "n/a" : (_msA / _framesA).ToString("0.000", CultureInfo.InvariantCulture) + " ms")} " +
                $"averaged over {_framesA} frame(s) -> {Verdict4(_litA)}");

            text.AppendLine(
                $"path B (enabled camera, cullingMask 0, CommandBuffer on AfterForwardOpaque): lit " +
                $"{(_litB < 0 ? "not measured" : _litB + " of " + total)} " +
                $"({(_litB < 0 ? "n/a" : MeshProbe.Pct(_litB, total))}), centre sample RGB " +
                $"{(_litB < 0 ? "n/a" : _sampleB.r + "," + _sampleB.g + "," + _sampleB.b)}, render " +
                $"{(_framesB <= 0 ? "n/a" : (_msB / _framesB).ToString("0.000", CultureInfo.InvariantCulture) + " ms")} " +
                $"averaged over {_framesB} frame(s), camera callbacks hooked pre " +
                $"{MeshProbe.YesNo(_preHooked)} post {MeshProbe.YesNo(_postHooked)} -> {Verdict4(_litB)}");

            // "neither" is not the same statement as "not measured", and a path that never ran must not be
            // reported as one that drew nothing: the first would mean DrawMesh does not reach a manual
            // Render, and the second would mean the view was closed before it had its turn.
            var keep =
                _litA > 0 ? "A" :
                _litB > 0 ? "B" :
                _litA < 0 && _litB < 0 ? "not measured" :
                _litA < 0 || _litB < 0 ? "not measured (one path never ran)" :
                "neither";

            text.AppendLine(
                keep == "A" || keep == "B"
                    ? $"staying on path {keep}"
                    : $"staying on NO path ({keep}) - the view stops rendering, so nothing of ours draws or " +
                      "lights anything until it is torn down");

            if (keep == "A") RevertToA();
            else if (keep != "B") StopRendering();
            else SettleOnB();
        }

        /// <summary>One path's verdict word. Three states, not two: a path with no measurement at all has
        /// not been shown to fail.</summary>
        /// <param name="lit">The lit-pixel count, or -1 for "never measured".</param>
        private static string Verdict4(int lit) => lit < 0 ? "NOT RUN" : lit > 0 ? "WORKED" : "DREW NOTHING";

        /// <summary>Back to the disabled camera and the manual render. The light goes off with it: on path
        /// A it is switched on only inside the render bracket.</summary>
        private void RevertToA()
        {
            try
            {
                Unhook();
                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.RemoveAllCommandBuffers();
                    _camera.cullingMask = _privateMask;
                }

                if (_light != null) _light.enabled = false;
                _path = Path.DrawMeshManualRender;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe view could not go back to path A ({ex.Message}).");
            }
        }

        /// <summary>Path B won, so the camera stays enabled - but the light does NOT stay on outside its
        /// render. A directional light left enabled in the menu is a light in the hideout: a culling mask
        /// keeps it off our geometry's neighbours in FORWARD rendering, and this game renders the hideout
        /// deferred, where per-light culling masks are not honoured. So it is switched on in
        /// <see cref="OnPre"/> and off again in <see cref="OnPost"/>, for our camera only, and left off
        /// here.</summary>
        private void SettleOnB()
        {
            try
            {
                if (_light != null) _light.enabled = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe view could not settle on path B ({ex.Message}).");
            }
        }

        /// <summary>Neither path drew anything, or neither ran: stop entirely. The camera is disabled, the
        /// light is off, the command buffers are gone and LateUpdate returns at its first line - the view
        /// becomes a still picture of whatever is in the texture until the key is pressed again.</summary>
        private void StopRendering()
        {
            try
            {
                Unhook();

                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.RemoveAllCommandBuffers();
                }

                if (_light != null) _light.enabled = false;

                // What LateUpdate tests first, so nothing below it runs again.
                _material = null;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe view could not stop rendering ({ex.Message}).");
            }
        }

        /// <summary>Removes each camera callback independently, and only clears its flag when the removal
        /// itself did not throw - so <see cref="LeakCheck"/> can assert that both are really off rather
        /// than that this method was called.</summary>
        private void Unhook()
        {
            if (_preHooked)
            {
                try
                {
                    Camera.onPreRender -= OnPre;
                    _preHooked = false;
                }
                catch (Exception ex)
                {
                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: the mesh probe could not unhook onPreRender ({ex.Message}).");
                }
            }

            if (!_postHooked) return;

            try
            {
                Camera.onPostRender -= OnPost;
                _postHooked = false;
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the mesh probe could not unhook onPostRender ({ex.Message}).");
            }
        }

        /// <summary>Camera.onPreRender and onPostRender are STATIC delegates: a subscription that
        /// outlives this component would call into a destroyed object on every camera in the game, for
        /// the rest of the session. The teardown unhooks; this is the belt for every other way a
        /// component can die.</summary>
        private void OnDestroy()
        {
            Unhook();
        }

        /// <summary>Destroys everything this view made, in the order that matters: the camera first so
        /// nothing renders into a released texture, then the texture, then the rest.</summary>
        /// <param name="text">The block being built.</param>
        internal void TearDown(StringBuilder text)
        {
            var made = _created.Count;

            // FIRST, before anything can throw: the flag LateUpdate tests at its first line. Unity calls
            // LateUpdate on every enabled component once more in the frame a Destroy is requested in -
            // Destroy is deferred to the end of the frame - and that call would find targetTexture already
            // null and render our camera straight to the BACKBUFFER, over the player's menu.
            _broke = true;
            _material = null;

            var destroyed = 0;
            var failed = 0;

            try
            {
                Unhook();

                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.RemoveAllCommandBuffers();
                    _camera.targetTexture = null;
                }

                if (_commands != null)
                {
                    _commands.Clear();
                    _commands.Release();
                    _commands = null;
                }

                if (_image != null) _image.texture = null;

                if (_rt != null) _rt.Release();
            }
            finally
            {
                // In the finally, so a throw anywhere above still destroys everything: a view that was
                // half torn down and then abandoned is the leak this whole method exists to prevent, and
                // each Destroy is guarded so one failure cannot stop the rest.
                foreach (var thing in _created)
                {
                    if (thing == null) continue;

                    try
                    {
                        Destroy(thing);
                        destroyed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Plugin.LogSource?.LogWarning(
                            $"QuestTree: the mesh probe could not destroy {thing.GetType().Name} ({ex.Message}).");
                    }
                }
            }

            text.AppendLine(
                $"teardown: {made} object(s) made, Destroy called on {destroyed}, refused on {failed}");
        }

        /// <summary>What is left of the view one frame after the teardown. This is the check that can
        /// fail, and it asks three separate questions, because the objects are only one of the three ways
        /// this view can outlive itself:
        ///
        ///   - our own objects: a missed Destroy leaves a live camera or RenderTexture in the menu;
        ///   - any GameObject still named QuestTreeMeshProbe*, which catches an object the list forgot;
        ///   - BOTH camera callbacks off. A subscription is not an object and no Destroy removes it: a
        ///     live onPreRender delegate into a destroyed component is called for every camera in the
        ///     game, every frame, for the rest of the session, and it would not show up in either count
        ///     above. Either one still on is a LEAK verdict.
        /// </summary>
        /// <param name="text">The block being built.</param>
        /// <param name="view">The torn-down view, whose list and flags survive it.</param>
        internal static void LeakCheck(StringBuilder text, MeshProbeView view)
        {
            var alive = 0;
            var names = new List<string>();

            foreach (var made in view._created)
            {
                if (made == null) continue;
                alive++;
                names.Add(made.GetType().Name);
            }

            var stray = 0;

            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null) continue;
                if (go.name.StartsWith("QuestTreeMeshProbe", StringComparison.Ordinal)) stray++;
            }

            var hooks = !view._preHooked && !view._postHooked;

            text.AppendLine(
                $"leak check: {alive} of {view._created.Count} of our objects still alive " +
                $"({(names.Count == 0 ? "none" : string.Join(", ", names.ToArray()))}); " +
                $"{stray} GameObject(s) named QuestTreeMeshProbe* left in memory; camera hooks: pre " +
                $"{(view._preHooked ? "STILL ON" : "off")}, post {(view._postHooked ? "STILL ON" : "off")} -> " +
                $"{(alive == 0 && stray == 0 && hooks ? "CLEAN" : "LEAKED")}");
        }

        // --- procedural geometry -----------------------------------------------------------------

        /// <summary>A cube of twelve triangles, with its own normals and UVs - twenty-four vertices, so
        /// each face has a normal of its own rather than a smoothed corner.</summary>
        /// <param name="size">The cube's edge length.</param>
        private static Mesh BuildCube(float size)
        {
            var h = size * 0.5f;

            var faces = new[]
            {
                new[] { new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h) },
                new[] { new Vector3(h, -h, h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(h, h, h) },
                new[] { new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(-h, h, h) },
                new[] { new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(h, h, -h) },
                new[] { new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(-h, h, h) },
                new[] { new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(-h, -h, -h) }
            };

            var vertices = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var indices = new List<int>(36);

            foreach (var face in faces)
            {
                var start = vertices.Count;

                vertices.AddRange(face);
                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                indices.Add(start);
                indices.Add(start + 2);
                indices.Add(start + 1);
                indices.Add(start);
                indices.Add(start + 3);
                indices.Add(start + 2);
            }

            var mesh = new Mesh { name = "QuestTreeMeshProbeCube", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A square grid of <paramref name="cells"/> by <paramref name="cells"/> quads with a
        /// sin/cos relief - the shape the real ground mesh will be, at a size a test can see. The index
        /// format is set to UInt32 EXPLICITLY, because the real one has ~151k vertices and a mesh left
        /// on the UInt16 default silently wraps.</summary>
        /// <param name="cells">Cells per side.</param>
        /// <param name="span">Metres per side.</param>
        private static Mesh BuildField(int cells, float span)
        {
            var verts = cells + 1;
            var step = span / cells;
            var half = span * 0.5f;

            var vertices = new Vector3[verts * verts];
            var uvs = new Vector2[verts * verts];

            for (var j = 0; j < verts; j++)
            {
                for (var i = 0; i < verts; i++)
                {
                    var x = -half + i * step;
                    var z = -half + j * step;
                    var y = Mathf.Sin(x * 0.6f) * 0.6f + Mathf.Cos(z * 0.5f) * 0.5f;

                    vertices[j * verts + i] = new Vector3(x, y, z);
                    uvs[j * verts + i] = new Vector2((float)i / cells, (float)j / cells);
                }
            }

            var indices = new int[cells * cells * 6];
            var at = 0;

            for (var j = 0; j < cells; j++)
            {
                for (var i = 0; i < cells; i++)
                {
                    var a = j * verts + i;
                    var b = a + 1;
                    var c = a + verts;
                    var d = c + 1;

                    indices[at++] = a;
                    indices[at++] = c;
                    indices[at++] = b;
                    indices[at++] = b;
                    indices[at++] = c;
                    indices[at++] = d;
                }
            }

            var mesh = new Mesh { name = "QuestTreeMeshProbeField", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A checkerboard, made here rather than shipped: the real texture will be the
        /// capture's own picture, and this only has to prove that a UV mapping arrives.</summary>
        /// <param name="size">Pixels per side.</param>
        private static Texture2D BuildChecker(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "QuestTreeMeshProbeChecker",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };

            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dark = ((x / 8) + (y / 8)) % 2 == 0;
                    pixels[y * size + x] = dark
                        ? new Color32(70, 80, 90, 255)
                        : new Color32(190, 185, 170, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }
    }

    /// <summary>
    /// THROWAWAY, with <see cref="MeshProbe"/>: drag to orbit, scroll to dolly, on the RawImage's own
    /// GameObject. IDragHandler and IScrollHandler are what stop a surrounding ScrollRect stealing the
    /// gesture - the same reason UI/PanZoomHandler implements both.
    /// </summary>
    internal sealed class MeshProbeOrbit : MonoBehaviour, IDragHandler, IScrollHandler
    {
        internal MeshProbeView View;

        public void OnDrag(PointerEventData eventData)
        {
            if (View == null || eventData == null) return;

            try
            {
                View.Orbit(eventData.delta.x * 0.4f, -eventData.delta.y * 0.3f);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe orbit failed ({ex.Message}).");
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (View == null || eventData == null) return;

            try
            {
                View.Dolly(eventData.scrollDelta.y);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug($"QuestTree: the mesh probe dolly failed ({ex.Message}).");
            }
        }
    }
}
