using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Builds the 3D map's geometry inside a raid: the ground as one raycast height grid per floor
    /// band, and the buildings as the low-detail shells their own renderers are drawing, both
    /// quantised into a <see cref="MapMeshFile"/> for the Maps tab to draw in the menu.
    ///
    /// It runs as a coroutine driven by <c>MapCapture.Run</c> after the last floor's picture and
    /// before the meta, inside one scene hold, and it is ADDITIONAL to the picture in the strictest
    /// sense: every step is guarded on its own, a phase that fails is logged and left out, and the
    /// worst case this class can produce is a capture with no mesh in it. That is deliberate - the
    /// picture is the feature that works, the mesh is the upgrade - and it is why there is no single
    /// try/catch around the whole of it but a guard per step.
    ///
    /// WHY RAYCASTS FOR THE GROUND. Phase 3-0 measured the alternatives in a live Customs raid:
    /// <c>RaycastCommand.ScheduleBatch</c> casts the whole 559x270 grid - 151k rays - in 45 ms
    /// against 290 ms for the same rays through <c>Physics.Raycast</c>, and it hits something
    /// in 100 % of cells from either end of the map, at every distance ring. That last number is the
    /// important one: COLLIDERS DO NOT STREAM OUT with the player the way the rendered chunks do, so
    /// the relief needs none of the merge machinery the pictures need. The per-cell distance byte is
    /// written anyway (<see cref="MapMeshFile.ReliefBand.Distance"/> says why), but no capture has to
    /// be repeated to fill the ground in.
    ///
    /// WHY THE BUILDINGS ARE READ THE WAY THEY ARE, and the one hard rule this file exists under.
    /// 80-95 % of the meshes near the player answer <see cref="Mesh.isReadable"/> true and can simply
    /// be asked for their <c>vertices</c>. The rest are GPU-only, and the one path that reads them is
    /// <see cref="AsyncGPUReadback"/> on the buffer <see cref="Mesh.GetVertexBuffer"/> hands over AS
    /// IT IS. On 2026-09-22 assigning <c>Mesh.vertexBufferTarget</c> on a scene mesh - to add
    /// <c>Raw</c> so a synchronous <c>GetData</c> would work, and again to put the value back - killed
    /// the process natively inside d3d11, with no managed exception for any catch in this mod to see.
    /// So, for ever: NO WRITE to <c>vertexBufferTarget</c> or <c>indexBufferTarget</c> on a mesh this
    /// mod does not own, and NO <c>GraphicsBuffer.GetData</c> on a scene mesh's buffer. A readback
    /// that does not finish inside <see cref="ReadbackFrameCap"/> frames is waited for with
    /// <c>WaitForCompletion</c> BEFORE its buffer is released, because the readback's destination is
    /// that buffer and releasing it first is a GPU write into freed memory.
    ///
    /// COST, and why every loop here is chunked. Customs has ~184,000 MeshRenderers; the scan that
    /// finds the few hundred worth keeping is the expensive half of this class, not the reading. So
    /// the renderer walk goes <see cref="CandidateScanPerFrame"/> at a time, the rays
    /// <see cref="RaysPerFrame"/> at a time, the quantisation <see cref="QuantisePerFrame"/> cells at
    /// a time and the buildings one renderer to a frame, each sized so no frame of ours is much over
    /// 30 ms - the player is standing in a raid while this runs. The whole building phase is capped
    /// at the request's <see cref="Request.BuildingSeconds"/> (what the capture's own clock leaves of
    /// its budget) and <see cref="MaxBuildingTriangles"/> triangles, and says in its log line what it
    /// cut.
    ///
    /// MEMORY. The working set is deliberately small: the two NativeArrays a ray chunk needs are
    /// <see cref="Allocator.TempJob"/>, sized to one chunk, and disposed in a finally per chunk (a
    /// leaked TempJob allocation is a console warning every frame for the rest of the session); a
    /// band holds one float per cell while the y range is being measured and drops it the moment the
    /// cells are quantised; and a building's vertices go into ushort lists as they are read rather
    /// than being kept as floats. The peak estimate is in the debug line at the end.
    /// </summary>
    internal static class MapMeshBuilder
    {
        // --- the constants phase 3-0 settled ------------------------------------------------------

        /// <summary>Metres a relief cell covers. Two, as the probe settled it: 151k rays for Customs
        /// in 45 ms, and a 2 m ground grid is finer than the 3-4 px/m the picture draped over it.</summary>
        internal const float ReliefCellMetres = 2f;

        /// <summary>Rays cast in one frame. The probe's chunk size, measured at 45 ms for eight of
        /// them, so a chunk is single-digit milliseconds.</summary>
        private const int RaysPerFrame = 20_000;

        /// <summary>Hits asked for per ray on an INTERIOR band: enough to go through a shelf's top, a
        /// shelf's middle board, a counter and a pallet and still reach the floor. When all of them are
        /// used and the lowest is still well above the floor the cell is counted as SATURATED in the
        /// relief line, which is the evidence that says whether this number is enough. Eight rather than
        /// six after review: the cost is per interior band only (the results array is count x 8 for one
        /// 20k-ray chunk at a time), and a shelving aisle is a shelf top, two boards, a crate and a
        /// counter lip before the floor. The topmost band asks for one - its first hit is the answer,
        /// and one is eight times cheaper.</summary>
        internal const int InteriorMaxHits = 8;

        /// <summary>Metres above a band's minY a cell's chosen height may be and still count as the
        /// floor, for the saturation test. ONE metre, not three: Goshan's and IDEA's shelving - the case
        /// this exists for - is 1.8 to 2.5 m tall, so a three-metre test would neither have fixed those
        /// cells nor counted them. A floor is within half a metre of minY by the band's own margin, so a
        /// metre above it is already not the floor.</summary>
        internal const float SaturatedAboveMetres = 1f;

        /// <summary>Metres BELOW an interior band's minY a picked height may sit before it counts as
        /// suspicious - see <see cref="BelowShareToNote"/>.</summary>
        internal const float BelowFloorMetres = 0.2f;

        /// <summary>The share of an interior band's hit cells picked under its floor by more than
        /// <see cref="BelowFloorMetres"/> that earns a note in the relief line. Taking the LOWEST hit can
        /// reach through a raised floor to the terrain under it; five percent of a band doing that is
        /// worth a raid's look, and nothing is changed because of it.</summary>
        internal const float BelowShareToNote = 0.05f;

        /// <summary>Commands per job for <c>RaycastCommand.ScheduleBatch</c> - the probe's
        /// value, which is what the 45 ms was measured with.</summary>
        private const int RaysPerJob = 256;

        /// <summary>Metres a band's rays are cast PAST its lower edge, so a floor sampled exactly at
        /// its own minY is still hit.</summary>
        private const float RayDepthBelow = 1f;

        /// <summary>Metres below a band's minY a hit still counts as that band's ground. The same half
        /// metre the harvest's bands already carry as their margin: a hit under it is the floor below
        /// seen through a hole, and <see cref="MapMeshFile.NoHit"/> is the honest answer for this
        /// band.</summary>
        private const float BandFloorSlack = 0.5f;

        /// <summary>Cells quantised in one frame once the y range is known. 200k is one pass over the
        /// whole of Customs' band, under a millisecond.</summary>
        private const int QuantisePerFrame = 200_000;

        /// <summary>Renderers the candidate filter walks in one frame. Reading
        /// <see cref="Renderer.bounds"/> is the cost here, and Customs has 184,000 of them.</summary>
        private const int CandidateScanPerFrame = 20_000;

        /// <summary>Candidates the building phase may EXAMINE in one frame without reading any of them.
        /// A candidate that is over budget or on the wrong LOD level costs a few submesh queries, but a
        /// thousand of those in a row is still a frame the player feels for no geometry at all.</summary>
        private const int CandidatesPerFrame = 500;

        /// <summary>The longest side a renderer's world bounds must have, in metres, to be a
        /// building. Six metres keeps sheds and containers and drops the crates, railings and pipes
        /// that would be most of the triangles and none of the shape.</summary>
        private const float MinBuildingLongSide = 6f;

        /// <summary>Metres tall a renderer's world bounds must be. A 30 m wide car park deck is not a
        /// building to look at from above; a 2.5 m wall is.</summary>
        private const float MinBuildingHeight = 2.5f;

        /// <summary>Metres outside the extent a renderer's bounds CENTRE may still sit and be kept.
        /// Twenty, because a hangar straddling the extent's edge is half of the map's skyline and its
        /// centre can easily be outside the rectangle the harvest measured.</summary>
        private const float CentreMargin = 20f;

        /// <summary>Metres outside the extent a single VERTEX may sit before its triangle is dropped.
        /// One metre: the quantisation has no room outside the extent, so a triangle reaching past it
        /// would be squashed onto the edge, and a fence stretching to the horizon is what that looks
        /// like.</summary>
        private const float VertexSlack = 1f;

        /// <summary>Triangles a LOD level's meshes must add up to for that level to be worth taking.
        /// Under twelve is a billboard or a box stand-in, which is what AmplifyImpostors leaves at the
        /// end of an EFT LOD group.</summary>
        private const int MinLodTriangles = 12;

        /// <summary>Part of a shader's name that marks a level as an impostor card rather than
        /// geometry. The game ships AmplifyImpostors and uses it for the last level of many
        /// groups.</summary>
        private const string ImpostorShaderMark = "Impostor";

        /// <summary>Triangles kept across every building of one map: stage V's cap (user, 2026-09-23). It
        /// is half of <see cref="MapMeshFile.MaxTriangles"/> (6 M), so the file's own caps are never the
        /// thing that refuses a capture; the per-building limits and the ledger (<see cref="BudgetLedger"/>)
        /// are what hold the total under it.</summary>
        internal const int MaxBuildingTriangles = 3_000_000;

        /// <summary>The share of <see cref="MaxBuildingTriangles"/> the area budget plans with. The rest is
        /// never reserved: it is the headroom a decimation's overshoot (up to its hard limit), a group's
        /// coarse fallback or a source stored as it is is paid from, so the buildings at the end of the list
        /// keep what the budget promised them.</summary>
        internal const double BudgetShare = 0.9;

        /// <summary>The most triangles a building's MOST DETAILED level may have and still be the source
        /// (stage V; user, 2026-09-23). Above it the building is read from the game's coarse level instead
        /// - the stage-U rule - and counted in the log line as over the source guard.</summary>
        internal const int MaxSourceTriangles = 1_000_000;

        /// <summary>The largest source the quadric decimation is run on. Its workspace is ~470 bytes a
        /// source triangle (measured: 51.8 MB for 110,592) and it runs at 1-2 microseconds a triangle, so
        /// 120,000 is ~56 MB and well under the per-building cap; a larger source goes to its coarse level, or is stored as it is, or clustered -
        /// never dropped.</summary>
        internal const int MaxDecimatedSource = 120_000;

        /// <summary>Milliseconds of WORKER time one building's decimation may take before it is abandoned
        /// for the building's next path (coarse level, as it is, clustered). Twice what the largest source
        /// the decimator is given needs at the measured rate.</summary>
        internal const double DecimateBuildingMs = 600d;

        /// <summary>Buildings on workers at once. Two (second review, H2): the memory budget is 256 MiB and
        /// a decimating flight is ~60 MB at MaxDecimatedSource.</summary>
        internal const int MaxWorkers = 2;

        /// <summary>Source triangles on workers at once, across all of them - what bounds the phase's
        /// memory: a flight holds its source, its placed copy and a workspace grown to its size.</summary>
        internal const long MaxInFlightTriangles = 300_000;

        /// <summary>The largest lane - decimator workspace AND the placement's lists - kept in the pool
        /// between buildings, bytes. A larger one is dropped, which is its trim.</summary>
        private const long MaxPooledWorkspaceBytes = 32L * 1024 * 1024;

        /// <summary>Bytes a worker's lists cost per source triangle (indices, placed positions and
        /// triangles, remap), for the peak line.</summary>
        private const long WorkerBytesPerTriangle = 48;

        /// <summary>Bytes the decimator's workspace costs per source triangle (measured 470), for the peak
        /// line.</summary>
        private const long DecimatorBytesPerTriangle = 480;

        /// <summary>Milliseconds a cluster flight may take.</summary>
        internal const double ClusterMs = 200d;

        /// <summary>Seconds past the hard cap the queued coarse levels are still read before they are
        /// abandoned - the bound on the post-cap drain.</summary>
        internal const double DrainSeconds = 8d;

        /// <summary>Milliseconds of main-thread work the building loop does before it yields a frame.</summary>
        private const double FrameBudgetMs = 8d;

        /// <summary>Seconds of the campaign's 180 s stop the capture plans for: floors, relief, buildings and
        /// side views together (second review, H1). The building phase's soft cap is what the floors' and
        /// relief's measured seconds and the sides' estimate leave of it, never under
        /// <see cref="MinBuildingSeconds"/>.</summary>
        internal const double CaptureSecondsBudget = 140d;

        /// <summary>The least the building phase is given, however long the floors took.</summary>
        internal const double MinBuildingSeconds = 20d;

        /// <summary>The building phase's HARD cap for a given soft cap: ten seconds more. Past the soft cap
        /// nothing more is decimated; past this nothing new is taken from the list, and the drain after it
        /// is at most <see cref="DrainSeconds"/>.</summary>
        /// <param name="soft">The soft cap, seconds.</param>
        internal static double HardSecondsFor(double soft) => soft + 10d;

        /// <summary>Frames a readback is waited for before the mesh is given up as unreadable. The
        /// probe measured two frames for a real one; 120 is two seconds of being wrong.</summary>
        private const int ReadbackFrameCap = 120;

        /// <summary>Building vertices quantised a frame.</summary>
        private const long QuantisePerFrameVertices = 500_000;

        /// <summary>Failures written to the log per build. A map with 1,500 candidates can produce
        /// 1,500 of the same complaint, and the phase's own summary line carries the counts.</summary>
        private const int MaxLoggedFailures = 5;

        /// <summary>The layers a relief ray may hit, by NAME - the numbering is not the same in every
        /// game version. Measured in phase 3-0 from a full grid cast against every layer: Grass 51 %,
        /// Terrain 23 %, HighPolyCollider 12 %, LowPolyCollider 7 %, Water 3 %, with Default and
        /// Interactive present but rare.
        ///
        /// What is deliberately NOT here is the interesting half: Ignore Raycast (2 % - by definition
        /// not geometry), LevelBorder (1.4 % - the invisible walls around the map, which would be a
        /// ceiling over the whole relief), Triggers and TransparentCollider. A mask is a number nobody
        /// can check by eye, so the names are logged once with the numbers this game version gave
        /// them.</summary>
        private static readonly string[] ReliefLayerNames =
        {
            "Grass", "Terrain", "HighPolyCollider", "LowPolyCollider", "Water", "Default", "Interactive"
        };

        /// <summary>Layers whose renderers are never buildings, whatever their bounds say. Foliage and
        /// grass produce huge bounds around a scatter of leaves.</summary>
        private static readonly string[] NotBuildingLayerNames =
        {
            "Foliage", "Grass",

            // The map's invisible walls. Both are in the RENDER mask on Customs (the probe's mask line
            // lists LevelBorder(29)), because a renderer on them draws nothing the eye can see - but a
            // 3D map that read them as geometry would wall the map in with opaque slabs.
            "LevelBorder", "TransparentCollider"
        };

        /// <summary>The tallest a building's world bounds may be, in metres. Nothing on any EFT map is
        /// three hundred metres tall; what IS that tall is a renderer whose bounds mean "everywhere" -
        /// see <see cref="SizeVerdict"/>.</summary>
        private const float MaxBuildingHeight = 300f;

        /// <summary>How far above or below the GROUND's measured heights the file's y range may reach
        /// to hold a building, in metres. The range is otherwise taken from what is stored, and one
        /// wild vertex would spread every height in the file over a range it could not use - the
        /// failure the rain's 3.4e38 bounds produced. A hundred metres is a thirty-storey roof over the
        /// highest ground hit; Customs' buildings top out under 70 m. A vertex past it clamps, which is
        /// a roof at the wrong height rather than a map at the wrong height.</summary>
        internal const float YRangeMarginMetres = 100f;

        /// <summary>World-bounds slack, in metres, for the check that a building's transformed
        /// vertices land where its renderer says it is - see <see cref="Place"/>.</summary>
        private const float PlausibleSlack = 2f;

        /// <summary>The share of sampled vertices that must land inside the renderer's bounds for a
        /// transform to be believed.</summary>
        private const float PlausibleShare = 0.9f;

        /// <summary>Vertices sampled for that check. Enough to catch a wrong matrix (which moves ALL of
        /// them) at the cost of a few dozen multiplies a building.</summary>
        private const int PlausibleSamples = 32;

        /// <summary>What <see cref="SizeVerdict"/> answers for a building-sized renderer.</summary>
        internal const int SizeOk = 0;

        /// <summary>What <see cref="SizeVerdict"/> answers for a crate, a railing or a pipe.</summary>
        internal const int SizeSmall = 1;

        /// <summary>What <see cref="SizeVerdict"/> answers for bounds bigger than the map itself.</summary>
        internal const int SizeOversized = 2;

        /// <summary>What <see cref="SizeVerdict"/> answers for bounds that are not numbers.</summary>
        internal const int SizeNotFinite = 3;

        /// <summary>Whether the relief mask's layer names have been logged this session.</summary>
        private static bool _loggedMask;

        // --- what the caller passes in and gets back -----------------------------------------------

        /// <summary>One floor band to build the relief of: the same band the picture was taken of, and
        /// photographed from the same height.</summary>
        internal sealed class Band
        {
            /// <summary>The floor level, 0 being the ground floor - the meta's numbering.</summary>
            internal int Level;

            /// <summary>The band's name, for the log lines only.</summary>
            internal string Name;

            /// <summary>The band's lower edge in world metres.</summary>
            internal float MinY;

            /// <summary>The band's upper edge in world metres.</summary>
            internal float MaxY;

            /// <summary>Where the rays start: the height the picture's camera photographed this band
            /// from (MapCapture's BandCameraY - the top band from over the roofs, an interior band
            /// from just under the floor above).</summary>
            internal float CameraY;

            /// <summary>Metres BELOW <see cref="MinY"/> that still count as this band's ground, and
            /// that the ray is allowed to travel to reach them. Filled by MapCapture from the same
            /// constant the picture's far clip uses (TopBandDepthBelow), so it is 50 m for the topmost
            /// band and 0 for an interior one.
            ///
            /// Why the topmost band needs it: a band is the WALKABLE range the harvest measured -
            /// -3.5..8.5 m on Customs - while the ground under it reaches far lower. Phase 3-0's grid
            /// hit y values from -17 m (the river bed and the trenches) on exactly that band, and the
            /// picture deliberately renders 50 m below the band for the same reason. Without this the
            /// 3D ground would have a hole wherever the picture has terrain, which is the one
            /// disagreement between the two that a viewer cannot explain.</summary>
            internal float DepthBelow;

            /// <summary>Whether another band lies above this one. An interior band's relief is its
            /// FLOOR, and its rays start just under the band above - so the first thing a ray meets on
            /// the way down is the top of a shelf, a rack or a counter, and Interchange's Goshan and IDEA
            /// came back as a field of bumps with dark aisles between them. An interior band therefore
            /// asks for up to <see cref="InteriorMaxHits"/> hits a ray and keeps the LOWEST one that is
            /// still this band's (see <see cref="PickHit"/>); the topmost band keeps the first hit,
            /// because there the first thing met is a roof and a roof is what its relief is for.</summary>
            internal bool Interior;
        }

        /// <summary>Everything one build works from. Filled by MapCapture out of its own plan, so this
        /// class never reaches into the capture's private state.</summary>
        internal sealed class Request
        {
            /// <summary>The map's key, for the log lines.</summary>
            internal string Map;

            /// <summary>The extent's low x edge - the same double the meta and the pictures carry.</summary>
            internal double MinX;

            /// <summary>The extent's low z edge.</summary>
            internal double MinZ;

            /// <summary>The extent's high x edge.</summary>
            internal double MaxX;

            /// <summary>The extent's high z edge.</summary>
            internal double MaxZ;

            /// <summary>The bands to build, lowest first.</summary>
            internal List<Band> Bands = new List<Band>();

            /// <summary>Where the capturing player stood, in world XZ. Every cell's distance from
            /// here becomes its distance byte.</summary>
            internal Vector2 From;

            /// <summary>The culling mask the PICTURE was drawn with - MapCapture's own, so a building
            /// in the mesh is a building in the picture. Big Red's shell is on HighPolyCollider, which
            /// is why this is the render mask and not the raycast one.</summary>
            internal int RenderMask;

            /// <summary>Whether the building phase runs at all. False builds a relief-only file,
            /// which is a complete file.</summary>
            internal bool WantsBuildings = true;

            /// <summary>Seconds the capture leaves for relief and buildings: <see cref="CaptureSecondsBudget"/>
            /// less the floors' measured seconds and the side views' estimate. The builder takes the relief's
            /// MEASURED seconds off it too and holds the rest to <see cref="MinBuildingSeconds"/> - the building
            /// phase's soft cap; <see cref="HardSecondsFor"/> of that is the hard one.</summary>
            internal double BuildingSeconds = 90d;

            /// <summary>Set by the capture's watchdog: the build stops reading buildings at once, abandons
            /// what is queued or in flight, and finishes with what it has.</summary>
            internal bool Abort;
        }

        /// <summary>What a build produced. <see cref="File"/> is null when nothing usable was
        /// built.</summary>
        internal sealed class Result
        {
            /// <summary>The mesh, ready to be written, or null.</summary>
            internal MapMeshFile File;

            /// <summary>Cells across every band.</summary>
            internal long Cells;

            /// <summary>Triangles across every building.</summary>
            internal long Triangles;

            /// <summary>Buildings kept.</summary>
            internal int Buildings;

            /// <summary>Bytes the relief's arrays occupy before deflate.</summary>
            internal long ReliefBytes;

            /// <summary>Bytes the buildings' arrays occupy before deflate.</summary>
            internal long BuildingBytes;
        }

        // --- the build ------------------------------------------------------------------------------

        /// <summary>
        /// Builds the mesh, a chunk to a frame. Never throws and never yields anything but null: the
        /// caller drives it with MoveNext and yields what it gets, and a failure inside any phase
        /// leaves <see cref="Result.File"/> holding whatever WAS built - a relief-only file is valid,
        /// and so is no file at all.
        /// </summary>
        /// <param name="request">What to build.</param>
        /// <param name="result">Filled as the build goes. Never null.</param>
        internal static IEnumerator Build(Request request, Result result)
        {
            if (request == null || result == null) yield break;

            var job = new Job(request);
            var wall = Stopwatch.StartNew();

            if (!Step(job, "the mesh's header", () => Prepare(job))) yield break;

            // --- the relief -----------------------------------------------------------------------
            //
            // Heights are kept as floats until every band has been cast, because the quantisation is
            // over the y range MEASURED from the data (roofs sit far above a band's own maxY: -17..69 m
            // on Customs for a band of -3.5..8.5) and a range changed after a value was quantised
            // silently moves it.

            for (var i = 0; i < job.Bands.Count; i++)
            {
                var band = job.Bands[i];

                while (band.Done < band.Cells)
                {
                    if (!Step(job, $"the relief of \"{band.Source.Name}\"", () => CastChunk(job, band))) break;

                    yield return null;
                }
            }

            Step(job, "the relief's log line", () => ReportRelief(job));

            // --- the candidates -------------------------------------------------------------------
            //
            // One scan of the scene, then a cheap filter over it: FindObjectsOfType is the single
            // most expensive call in this class (184,000 renderers on Customs), so it happens once,
            // in a frame of its own, and the array is dropped the moment the filter is done.

            if (job.WantsBuildings)
            {
                // The soft cap, now that the relief's own seconds are measured.
                job.ReliefSeconds = wall.Elapsed.TotalSeconds;
                job.SoftSeconds = Math.Max(MinBuildingSeconds,
                    (double.IsNaN(request.BuildingSeconds) ? 0d : request.BuildingSeconds) - job.ReliefSeconds);
                job.HardSeconds = HardSecondsFor(job.SoftSeconds);

                job.BuildingClock.Start();

                if (Step(job, "the scene's renderers", () => Scan(job)))
                {
                    yield return null;

                    while (job.Scanned < job.RendererCount)
                    {
                        if (!Step(job, "the building candidates", () => FilterChunk(job))) break;

                        yield return null;
                    }
                }

                Step(job, "the candidate order", () => SortCandidates(job));
            }

            // --- the area budget (stage V) -------------------------------------------------------------
            //
            // Every candidate's source level, source size and footprint first, so the targets can be
            // scaled by ONE factor over the whole map before anything is read - see AreaBudget - and every
            // target RESERVED in the ledger, so the tail of the list is never starved by the head's
            // overshoot. Chunked like every other pass: GetComponentInParent and GetLODs are the cost.

            if (job.WantsBuildings && job.Candidates.Count > 0)
            {
                while (job.Budgeted < job.Candidates.Count)
                {
                    Step(job, "the building budget", () => BudgetChunk(job));

                    yield return null;
                }

                Step(job, "the area budget", () => ApplyBudget(job));
            }

            // --- the buildings ---------------------------------------------------------------------
            //
            // A pipeline, not a loop of one: the main thread reads each source's arrays out of Unity (the
            // only part that must be on it) and hands them to one of MaxWorkers workers, which decode,
            // place, transform and decimate on plain arrays; every frame the main thread applies what has
            // finished - in the order it finished, at least one and then only while the frame's budget
            // lasts - then submits more until its frame budget, the workers or the in-flight cap are spent,
            // then yields.
            //
            // WHEN IT ENDS is BuildingLoop's, a Unity-free rule the harness drives: past the soft cap
            // nothing is decimated; past the hard cap nothing new is taken from the list, but the coarse
            // levels a fallback queued ARE still read (they are what keeps a building from vanishing) until
            // the drain deadline; past that - or at a format cap, or on the capture's abort - whatever is
            // still queued is abandoned and counted, so the loop can never wait on a queue nothing drains.

            if (job.WantsBuildings && job.Candidates.Count > 0)
            {
                job.FrameClock.Restart();

                var next = 0;
                var flights = job.Flights;

                while (true)
                {
                    // 1. what has finished, applied
                    Poll(job, flights);

                    // 2. the clock
                    var seconds = job.BuildingClock.Elapsed.TotalSeconds;

                    if (!job.DecimationStopped && seconds > job.SoftSeconds)
                    {
                        job.DecimationStopped = true;
                        job.DecimationStoppedWhy = $"the {N(job.SoftSeconds)} s soft cap";
                    }

                    if (!job.PastHard && seconds > job.HardSeconds)
                    {
                        job.PastHard = true;
                        job.CutAtHardCap = job.Candidates.Count - next;

                        // L5: what the unreached candidates had reserved goes back to the headroom now, so
                        // the fallbacks still in flight or queued can use it.
                        for (var k = next; k < job.Candidates.Count; k++)
                        {
                            job.Ledger.Release(job.Candidates[k].Reserved);
                            job.Candidates[k].Reserved = 0;
                        }
                    }

                    if (!job.PastDrain && seconds > job.HardSeconds + DrainSeconds) job.PastDrain = true;

                    if (BuildingLoop.Abandon(job.Stopped, job.PastDrain, job.Request.Abort))
                        while (job.Extra.Count > 0)
                        {
                            var dropped = job.Extra.Dequeue();
                            job.Ledger.Release(dropped.Reserved);
                            job.Abandoned++;
                        }

                    if (BuildingLoop.Done(flights.Count, job.Extra.Count, next, job.Candidates.Count, job.Stopped,
                            job.PastHard, job.Request.Abort))
                        break;

                    // 3. submit, while there is a worker, frame budget and something to read that fits
                    while (flights.Count < MaxWorkers && !FrameSpent(job))
                    {
                        var from = BuildingLoop.Next(job.Stopped, job.PastHard, job.PastDrain || job.Request.Abort,
                            job.Extra.Count, next, job.Candidates.Count);

                        if (from == BuildingLoop.None) break;

                        var candidate = from == BuildingLoop.FromExtra ? job.Extra.Peek() : job.Candidates[next];

                        // H2: the in-flight cap counts the source about to go; a source larger than the
                        // cap on its own goes only when nothing else is in the air.
                        if (!BuildingLoop.Fits(job.InFlightTriangles, candidate.SourceTriangles, flights.Count,
                                MaxInFlightTriangles))
                            break;

                        if (from == BuildingLoop.FromExtra) job.Extra.Dequeue();
                        else next++;

                        // Its reservation is its own from here: released into the headroom it may now use.
                        job.Ledger.Release(candidate.Reserved);
                        candidate.Reserved = 0;

                        var take = false;
                        Step(job, "a building's size", () => take = Wanted(job, candidate));

                        if (job.Stopped || !take) continue;

                        // Past the soft cap a detail source over its target is not read at all: its group
                        // takes the coarse path, as it is, now.
                        if (job.DecimationStopped && !candidate.Coarse &&
                            candidate.SourceTriangles > TargetFor(job, candidate) &&
                            EnqueueCoarse(job, candidate))
                            continue;

                        var limit = 0;
                        Step(job, "a building's admission", () => limit = Admit(job, candidate));

                        if (limit <= 0)
                        {
                            job.OverBudget++;
                            continue;
                        }

                        // The source's arrays out of Unity - the one part that must be on this thread.
                        job.Captured = null;

                        var read = candidate.Readable ? CaptureReadable(job, candidate) : ReadFromGpu(job, candidate);

                        // Disposed however this leaves - including when MoveNext throws, when the capture's
                        // abort breaks out of it, and when Unity or Cleanup disposes THIS iterator
                        // mid-readback: disposing an iterator runs its finally blocks, and ReadFromGpu's is
                        // what waits for a readback in flight and releases its buffers. The flights are
                        // polled in every frame the read waits.
                        try
                        {
                            while (!job.Request.Abort && read.MoveNext())
                            {
                                yield return read.Current;
                                job.FrameClock.Restart();
                                Poll(job, flights);
                            }
                        }
                        finally
                        {
                            (read as IDisposable)?.Dispose();
                        }

                        var source = job.Captured;
                        job.Captured = null;

                        // M1: a read takes frames, and a sibling's fallback may have switched the group to
                        // its coarse level meanwhile - a detail read that is no longer its group's source is
                        // discarded, or the building would be stored twice.
                        if (source != null && !candidate.Coarse && !IsSource(job, candidate))
                        {
                            source = null;
                            job.SwitchedMidRead++;
                        }

                        if (source == null || job.Request.Abort)
                        {
                            job.Ledger.Settle(limit, 0);
                            continue;
                        }

                        Flight started = null;
                        Step(job, "starting a building", () => started = Launch(job, candidate, source, limit));

                        if (started == null)
                        {
                            job.Ledger.Settle(limit, 0);
                            continue;
                        }

                        flights.Add(started);
                        if (flights.Count > job.PeakWorkers) job.PeakWorkers = flights.Count;
                        Track(job, flights);
                    }

                    yield return null;
                    job.FrameClock.Restart();
                }

                if (job.Request.Abort && flights.Count > 0)
                {
                    // The workers touch no Unity object and nothing of the job's but their own flight, so
                    // leaving them to finish on their own is safe; their buildings are simply not stored.
                    job.Abandoned += flights.Count;
                    flights.Clear();
                }

                Step(job, "the buildings' log line", () => ReportBuildings(job));
            }

            // --- the y range, and everything quantised over it -------------------------------------
            //
            // AFTER the buildings, not before them. The range is measured from what this file will
            // actually store - the rays that hit and the kept buildings' own decoded vertices - and
            // never from a renderer's bounds: EFT's rain (Weather/DepthPhoto/RainFall, Default layer,
            // centred on the player) reports bounds of 3.4e38 on every axis, which are FINITE, and one
            // such number in the range quantised every height on Customs into one flat sheet. So the
            // buildings keep their y as floats until here (x and z are quantised at once - the extent
            // is known from the start), and the range is held to the ground's own heights plus or
            // minus YRangeMarginMetres whatever a building claims. See YRange.

            if (!Step(job, "the mesh's y range", () => SetRange(job))) yield break;

            for (var i = 0; i < job.Bands.Count; i++)
            {
                var band = job.Bands[i];

                while (band.Quantised < band.Cells)
                {
                    if (!Step(job, $"quantising \"{band.Source.Name}\"", () => QuantiseChunk(job, band))) break;

                    yield return null;
                }

                Step(job, $"finishing \"{band.Source.Name}\"", () => FinishBand(job, band));
            }

            // The buildings' heights, and their bands - which can only be chosen now, because a band
            // is a band once FinishBand has put it in the file and not before.
            while (job.QuantisedUpTo < job.PendingY.Count)
            {
                if (!Step(job, "the buildings' heights", () => QuantiseBuildings(job))) break;

                yield return null;
            }

            job.PendingY.Clear();

            Step(job, "the mesh", () => Finish(job, result));
        }

        // --- the state ------------------------------------------------------------------------------

        /// <summary>Everything one build carries between its frames. Private: the caller sees the
        /// <see cref="Request"/> and the <see cref="Result"/>, nothing else.</summary>
        private sealed class Job
        {
            /// <summary>Builds a job from its request.</summary>
            /// <param name="request">What to build.</param>
            internal Job(Request request)
            {
                Request = request;
                WantsBuildings = request.WantsBuildings;

                SoftSeconds = Math.Max(MinBuildingSeconds, request.BuildingSeconds);
                HardSeconds = HardSecondsFor(SoftSeconds);
            }

            internal readonly Request Request;
            internal readonly bool WantsBuildings;

            internal MapMeshFile File;
            internal int RayMask;

            internal readonly List<BandWork> Bands = new List<BandWork>();
            internal readonly List<Candidate> Candidates = new List<Candidate>();

            /// <summary>The scene's renderers, held only between <see cref="Scan"/> and the end of the
            /// filter. 184,000 references is 1.5 MB, and it is the largest thing this class holds.</summary>
            internal MeshRenderer[] Renderers;

            internal int RendererCount;
            internal int Scanned;

            /// <summary>The lowest and highest RAY HIT, in metres - the ground, which is what the y range
            /// is measured from. See YRange.</summary>
            internal float Lowest = float.PositiveInfinity;
            internal float Highest = float.NegativeInfinity;

            internal int Rays;
            internal int Hits;
            internal readonly Stopwatch ReliefClock = new Stopwatch();
            internal readonly Stopwatch BuildingClock = new Stopwatch();

            internal int Kept;
            internal long Triangles;

            /// <summary>Vertices STORED so far across every building - the running total
            /// <see cref="MapMeshFile.MaxVerticesTotal"/> is checked against.</summary>
            internal long Vertices;

            internal int GpuRead;
            internal int Unreadable;
            internal int ImpostorSkipped;

            /// <summary>LOD levels passed over for being a box rather than a building - counted apart
            /// from the impostor cards, because they are different findings.</summary>
            internal int ThinSkipped;

            internal int OverBudget;
            internal int DroppedTriangles;

            /// <summary>Renderers refused for bounds bigger than the map (<see cref="SizeVerdict"/>) -
            /// the rain volumes, on Customs. Counted, because a map where this is in the thousands is a
            /// map whose filter wants looking at.</summary>
            internal int Oversized;

            /// <summary>Buildings refused because no transform put their vertices inside their own
            /// renderer's bounds - see <see cref="Place"/>.</summary>
            internal int Implausible;

            /// <summary>Buildings whose transform mirrors them (negative determinant), whose triangles
            /// were stored with their winding swapped so they are not drawn inside out.</summary>
            internal int Mirrored;

            /// <summary>The lowest and highest y of every vertex KEPT, in metres - what the y range is
            /// widened by, within the ground's margin. See <see cref="YRange"/>.</summary>
            internal float VertexLow = float.PositiveInfinity;

            internal float VertexHigh = float.NegativeInfinity;

            /// <summary>The kept buildings' vertex heights in METRES, one array per building in the
            /// order of <see cref="MapMeshFile.Buildings"/>, held only until the y range is known and
            /// then quantised into each building's Y by <see cref="QuantiseBuildings"/>.</summary>
            internal readonly List<float[]> PendingY = new List<float[]>();

            /// <summary>Each kept building's mean vertex height, same order - its band is chosen from
            /// this once the bands are in the file.</summary>
            internal readonly List<float> Centroids = new List<float>();

            /// <summary>The building being assembled's vertex heights in metres, reused.</summary>
            internal readonly List<float> YMetres = new List<float>();
            internal bool Stopped;
            internal string StoppedWhy;

            /// <summary>The largest single vertex-buffer readback this build asked for, for the peak
            /// memory line.</summary>
            internal long PeakReadbackBytes;

            /// <summary>The largest decoded vertex array one building cost, for the same line.</summary>
            internal long PeakDecodedBytes;

            /// <summary>One ray's hit heights, reused for every ray - see PickHit.</summary>
            internal float[] HitYs;

            /// <summary>Each LOD group's two candidate levels and which is in use - see
            /// <see cref="GroupState"/>. Decided once per group; a group with forty renderers under it is
            /// then answered by a set lookup, not by another GetLODs() allocation.</summary>
            internal readonly Dictionary<LODGroup, GroupState> Groups = new Dictionary<LODGroup, GroupState>();

            /// <summary>Renderers across every cached level set, for the peak-memory line.</summary>
            internal int LevelRenderers;

            /// <summary>Renderers already stored.</summary>
            internal readonly HashSet<Renderer> Emitted = new HashSet<Renderer>();

            /// <summary>Renderers handed to a worker (stored or not), so a building reached twice - once
            /// through its own candidate and once through a group's fallback, possibly while the first is
            /// still in flight - is read once.</summary>
            internal readonly HashSet<Renderer> Claimed = new HashSet<Renderer>();

            /// <summary>The map's triangle budget - see <see cref="BudgetLedger"/>.</summary>
            internal readonly BudgetLedger Ledger = new BudgetLedger(MaxBuildingTriangles);

            /// <summary>The source just captured on the main thread, waiting to be launched.</summary>
            internal Source Captured;

            /// <summary>The workers' pooled scratch.</summary>
            internal readonly Stack<Lane> Workspaces = new Stack<Lane>();

            /// <summary>Source triangles on workers right now.</summary>
            internal long InFlightTriangles;

            /// <summary>Coarse-level candidates queued by a fallback, read before the rest of the list.</summary>
            internal readonly Queue<Candidate> Extra = new Queue<Candidate>();

            /// <summary>The building phase's soft and hard caps, seconds - see Request.BuildingSeconds; set
            /// again once the relief's seconds are measured.</summary>
            internal double SoftSeconds;

            internal double HardSeconds;

            /// <summary>The relief phase's wall seconds.</summary>
            internal double ReliefSeconds;

            /// <summary>Past the hard cap (nothing more from the list) and past the drain deadline (the
            /// queue abandoned).</summary>
            internal bool PastHard;

            internal bool PastDrain;

            /// <summary>Candidates never reached when the hard cap passed.</summary>
            internal int CutAtHardCap;

            /// <summary>Queued coarse levels abandoned at the drain deadline, a format cap or an abort, and
            /// flights left to finish unapplied on an abort.</summary>
            internal int Abandoned;

            /// <summary>Buildings past the hard cap with no as-is room and no coarse level.</summary>
            internal int AbandonedAtHard;

            /// <summary>Detail reads discarded because the group switched to coarse while they read.</summary>
            internal int SwitchedMidRead;

            /// <summary>The flights in the air - here rather than in the loop, because Apply starts cluster
            /// flights.</summary>
            internal readonly List<Flight> Flights = new List<Flight>();

            /// <summary>Stores refused for the per-building and the file's vertex caps.</summary>
            internal int RefusedBuildingVertices;

            internal int RefusedFileVertices;

            /// <summary>Cluster flights over their time cap.</summary>
            internal int ClusterTimedOut;

            /// <summary>Lanes dropped instead of pooled for their size.</summary>
            internal int LanesDropped;

            /// <summary>The longest single main-thread read of a readable mesh (mesh.vertices, or one
            /// submesh's GetTriangles), ms.</summary>
            internal double PeakReadableMs;

            /// <summary>The pipeline's largest memory at once: flights plus pooled lanes, bytes.</summary>
            internal long PeakPipelineBytes;

            /// <summary>How far the sliced height quantisation has got.</summary>
            internal int QuantisedUpTo;

            /// <summary>Why decimation stopped, for the log line.</summary>
            internal string DecimationStoppedWhy;

            /// <summary>Stored as the source, past the limit, from unreserved headroom (H3).</summary>
            internal int StoredUndecimated;

            /// <summary>Stored clustered to their limit, the last resort.</summary>
            internal int ClusteredStored;

            /// <summary>Workers that threw, and buildings with no path left that stored nothing.</summary>
            internal int Failed;

            internal int Unstored;

            /// <summary>Decimations that ran out of time, and ones that could not reach their limit.</summary>
            internal int TimedOut;

            internal int OverLimit;

            /// <summary>The largest decimator workspace seen, bytes, and the most flights at once.</summary>
            internal long PeakWorkspaceBytes;

            internal int PeakWorkers;

            /// <summary>How far the area-budget pass has got through the candidates.</summary>
            internal int Budgeted;

            /// <summary>The one factor every area target was scaled by (1 when the map fits the cap).</summary>
            internal double BudgetScale = 1d;

            /// <summary>The frame's own clock: the loop yields when it passes FrameBudgetMs.</summary>
            internal readonly Stopwatch FrameClock = new Stopwatch();

            /// <summary>Worker milliseconds spent decimating, summed as each worker reports them.</summary>
            internal double WorkerMs;

            /// <summary>Set when the soft cap passes: every later over-target building takes the coarse
            /// level as it is.</summary>
            internal bool DecimationStopped;

            /// <summary>Buildings decimated, and the source triangles they came from.</summary>
            internal int Decimated;

            internal long SourceDecimated;

            /// <summary>BUILDINGS (not renderers) that fell back to the game's coarse level: a decimation
            /// that could not finish or fit, or an over-target building past the soft cap.</summary>
            internal int FellBack;

            /// <summary>Groups whose most detailed level was over MaxSourceTriangles, plus plain renderers
            /// over it.</summary>
            internal int InputGuarded;

            internal int Logged;

            /// <summary>Records a step that failed, at most <see cref="MaxLoggedFailures"/> times.</summary>
            /// <param name="what">What was being done.</param>
            /// <param name="ex">Why it did not happen.</param>
            internal void Note(string what, Exception ex)
            {
                if (Logged >= MaxLoggedFailures)
                {
                    Logged++;
                    return;
                }

                Logged++;

                Plugin.LogSource?.LogWarning(
                    $"QuestTree: the 3D mesh of {Request.Map} - {what} failed ({ex.GetType().Name}: " +
                    $"{ex.Message}); the capture's pictures are unaffected.");
            }
        }

        /// <summary>One band while it is being built: its grid, how far the rays have got, and the
        /// float heights waiting for the y range.</summary>
        private sealed class BandWork
        {
            internal Band Source;
            internal int Width;
            internal int Height;
            internal int Done;
            internal int Quantised;
            internal int Hits;

            /// <summary>Interior cells whose every hit slot was used with the lowest still more than
            /// SaturatedAboveMetres over the band's floor - see PickHit. Counted, not fixed: it is the
            /// number that says whether InteriorMaxHits is enough.</summary>
            internal int Saturated;

            /// <summary>Hit cells whose picked height is more than BelowFloorMetres under the band's
            /// minY - on an interior band, terrain reached through a raised floor. Reported, not
            /// corrected.</summary>
            internal int BelowFloor;

            /// <summary>The measured height of each cell in METRES, NaN where no ray hit. Dropped by
            /// <see cref="FinishBand"/> as soon as the cells are quantised.</summary>
            internal float[] Metres;

            internal ushort[] Codes;
            internal byte[] Distance;

            internal int Cells => Width * Height;
        }

        /// <summary>One renderer that might be a building, and what was found out about it.</summary>
        private sealed class Candidate
        {
            internal MeshRenderer Renderer;
            internal Mesh Mesh;
            internal Bounds Bounds;
            internal float Volume;
            internal bool Readable;

            /// <summary>The position attribute's stream, offset, stride, format and dimension - what a
            /// GPU readback has to get right, read from the mesh rather than assumed. Phase 3-0 saw
            /// Float32x3 everywhere with strides of 32 to 56 bytes, but an optimised mesh may store
            /// Float16, so both are decoded.</summary>
            internal int Stream;

            internal int Offset;
            internal int Stride;
            internal int Dimension;
            internal VertexAttributeFormat Format;

            /// <summary>The submeshes that are THIS renderer's: [SubFirst, SubEnd). All of them for an
            /// ordinary renderer. For one that is part of a static batch, <c>sharedMesh</c> is the
            /// batch's combined mesh - every renderer in the batch holds the same one - and this
            /// renderer's share is <c>subMeshStartIndex</c> onwards, one submesh per material. Reading
            /// every submesh stored the whole batch once per renderer, each copy under a different
            /// key.</summary>
            internal int SubFirst;

            internal int SubEnd;

            /// <summary>The transform the mesh's vertices are believed to be in. The renderer's own
            /// localToWorldMatrix normally; for a static batch, whose combined mesh Unity builds in the
            /// space of the batch's root (world space when there is none), the identity - the root is
            /// not public in this Unity. Whichever it is, <see cref="Place"/> holds it to the
            /// renderer's bounds before believing it - see <see cref="Fallback"/>.</summary>
            internal Matrix4x4 Matrix;

            /// <summary>The LOD group above this renderer, or null.</summary>
            internal LODGroup Group;

            /// <summary>Triangles in this renderer's share of its mesh - the source's size.</summary>
            internal long SourceTriangles;

            /// <summary>Its world bounds' footprint, x times z, in square metres.</summary>
            internal double Footprint;

            /// <summary>The triangles it may be stored with (stage V's area budget); 0 until decided.</summary>
            internal int Target;

            /// <summary>What the ledger holds reserved for it until it is reached.</summary>
            internal long Reserved;

            /// <summary>Queued by a group's fallback: its group's coarse level, read as it is.</summary>
            internal bool Coarse;

            /// <summary>Launched as its group's detail level - counted in GroupState.DetailCommitted.</summary>
            internal bool AsDetail;

            /// <summary>The other transform worth trying when <see cref="Matrix"/> does not put the
            /// vertices inside the renderer's bounds, or null. Set only for a static batch, where the
            /// two candidates are world space and the renderer's own.</summary>
            internal Matrix4x4? Fallback;
        }

        /// <summary>
        /// A LOD group's two ways of being read, decided once. DETAIL is the group's most detailed level -
        /// stage V's source, decimated to the area budget - when that level is real geometry and holds no
        /// more than <see cref="MaxSourceTriangles"/>; COARSE is the stage-U rule, the last level that is
        /// real geometry. A group starts on detail when it has one; a decimation that runs out of time, or
        /// the phase cap, switches it to coarse - once, and only while none of its detail renderers has
        /// been stored or is on a worker, so a building never arrives twice.
        /// </summary>
        private sealed class GroupState
        {
            internal HashSet<Renderer> Detail;
            internal HashSet<Renderer> Coarse;
            internal List<Renderer> CoarseList;
            internal bool UsingCoarse;

            /// <summary>Detail renderers stored or on a worker. A group may switch only while it is 0.</summary>
            internal int DetailCommitted;
        }

        /// <summary>A source mesh in WORLD space, ready to store or decimate: x, y, z per vertex and three
        /// indices per triangle, wound the way the game draws it (a mirrored transform already swapped).
        /// Plain arrays, so a worker can decimate it without touching a Unity object.</summary>
        private sealed class WorldMesh
        {
            internal float[] P;
            internal int[] T;
            internal bool Mirrored;

            internal int Triangles => T == null ? 0 : T.Length / 3;
        }

        /// <summary>A building's source as the main thread hands it to a worker: the arrays out of Unity
        /// (a readable mesh's vertices and triangles, or a readback's bytes and the layout to decode them
        /// with) and every number the worker needs, so the worker never touches a Unity object.</summary>
        private sealed class Source
        {
            internal long SourceTriangles;

            // the readable path
            internal Vector3[] Local;
            internal List<int[]> Parts;

            // the GPU path
            internal bool FromGpu;
            internal byte[] VertexBytes;
            internal byte[] IndexBytes;
            internal int VertexCount;
            internal int Offset;
            internal int Stride;
            internal int Dimension;
            internal int FormatSize;
            internal bool Wide;
            internal int[] SubStart;
            internal int[] SubCount;
            internal int[] SubBase;

            // placement
            internal Matrix4x4 Matrix;
            internal Matrix4x4? Fallback;
            internal Vector3 BoxMin;
            internal Vector3 BoxMax;
            internal float MinX;
            internal float MaxX;
            internal float MinZ;
            internal float MaxZ;

            // the reduction
            internal int Target;
            internal int Limit;
            internal bool MayDecimate;

            /// <summary>The arrays out of Unity, bytes.</summary>
            internal long Bytes()
            {
                var bytes = (Local?.Length ?? 0) * 12L + (VertexBytes?.Length ?? 0) + (IndexBytes?.Length ?? 0);
                if (Parts != null) foreach (var part in Parts) bytes += part.Length * 4L;
                return bytes;
            }
        }

        /// <summary>What a worker made of a source. <see cref="Mesh"/> is the building to store when it fits
        /// its limit; otherwise <see cref="Source"/> is the placed source, for the main thread to store as it
        /// is or hand to a cluster flight.</summary>
        private sealed class Outcome
        {
            internal WorldMesh Mesh;
            internal WorldMesh Source;
            internal bool Decimated;
            internal bool Undecodable;
            internal bool Implausible;
            internal bool TimedOut;
            internal bool OverLimit;
            internal long SourceTriangles;
            internal int Dropped;
            internal long DecodedBytes;
            internal double WorkerMs;
            internal long WorkspaceBytes;
        }

        /// <summary>One worker's reused scratch: the decimator's workspace and the placement's lists. Pooled
        /// on the job - at most <see cref="MaxWorkers"/> ever exist, and one is never used by two flights at
        /// once.</summary>
        private sealed class Lane
        {
            internal readonly MeshDecimator.Workspace Decimator = new MeshDecimator.Workspace();
            internal readonly List<int> Indices = new List<int>();
            internal readonly List<float> P = new List<float>();
            internal readonly List<int> T = new List<int>();
            internal int[] Remap = new int[0];

            /// <summary>What this lane holds, bytes: the decimator's arrays and the lists' capacities.</summary>
            internal long Bytes() =>
                Decimator.Bytes() + (Indices.Capacity + P.Capacity + T.Capacity + (long)Remap.Length) * 4L;
        }

        /// <summary>A building on a worker: the task, what it was given, and the limit pending for it in the
        /// ledger.</summary>
        private sealed class Flight
        {
            internal Task<Outcome> Task;
            internal Candidate Candidate;
            internal Source Source;
            internal Lane Workspace;
            internal int Limit;

            /// <summary>A cluster flight (Apply's last resort) rather than a building's first.</summary>
            internal bool Clustering;

            /// <summary>Triangles it counts in flight, and its memory estimate.</summary>
            internal long Triangles;

            internal long Bytes;
        }

        // --- the header ------------------------------------------------------------------------------

        /// <summary>The file's extent, the raycast mask and one grid per band - everything before the
        /// first ray.</summary>
        /// <param name="job">The build.</param>
        private static void Prepare(Job job)
        {
            var request = job.Request;

            job.File = new MapMeshFile
            {
                MinX = request.MinX,
                MinZ = request.MinZ,
                MaxX = request.MaxX,
                MaxZ = request.MaxZ
            };

            job.RayMask = ReliefMask();

            var spanX = request.MaxX - request.MinX;
            var spanZ = request.MaxZ - request.MinZ;

            var width = (int)Math.Ceiling(spanX / ReliefCellMetres);
            var height = (int)Math.Ceiling(spanZ / ReliefCellMetres);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException(
                    $"the extent is {spanX:0.0}x{spanZ:0.0} m, which is no grid at {ReliefCellMetres} m cells");

            if ((long)width * height > MapMeshFile.MaxCellsPerBand)
                throw new InvalidOperationException(
                    $"{width}x{height} cells is over the format's cap of {MapMeshFile.MaxCellsPerBand:#,##0}");

            foreach (var band in request.Bands)
            {
                if (band == null) continue;
                if (job.Bands.Count >= MapMeshFile.MaxBands) break;

                // A band whose camera is not above its own floor would cast its rays upwards. It is
                // the picture's own rule that decides the height, so this can only happen to a band
                // whose height band the harvest measured wrongly - and such a band gets no relief
                // rather than a grid of nonsense.
                if (!IsFinite(band.MinY) || !IsFinite(band.MaxY) || !IsFinite(band.CameraY) ||
                    band.CameraY <= band.MinY)
                {
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: no relief for {request.Map} \"{band.Name}\" - its band is " +
                        $"{F(band.MinY)}..{F(band.MaxY)} m seen from y={F(band.CameraY)}.");
                    continue;
                }

                var duplicate = false;

                // A level names a band in this format, and Write refuses two of one level.
                foreach (var already in job.Bands)
                    if (already.Source.Level == band.Level) duplicate = true;

                if (duplicate) continue;

                var work = new BandWork
                {
                    Source = band,
                    Width = width,
                    Height = height,
                    Metres = new float[width * height],
                    Codes = new ushort[width * height],
                    Distance = new byte[width * height]
                };

                // EMPTY, not zero. A new float[] is full of zeros, and zero is a perfectly good height
                // - so a chunk of rays that failed, or a band the build gave up on half way, would have
                // quantised its unmeasured cells into a flat plane at y = 0 across the map while the log
                // said they were left empty. NaN is the value QuantiseHeight answers NoHit for, and
                // DistanceEmpty is the byte for a cell nothing measured; filling them here means every
                // path out of the cast loop leaves the truth behind it. 151k cells is a quarter of a
                // millisecond.
                for (var n = 0; n < work.Metres.Length; n++)
                {
                    work.Metres[n] = float.NaN;
                    work.Distance[n] = MapMeshFile.DistanceEmpty;
                }

                job.Bands.Add(work);
            }

            if (job.Bands.Count == 0)
                throw new InvalidOperationException("no band of this map can carry a relief grid");
        }

        /// <summary>The layers a relief ray may hit: <see cref="ReliefLayerNames"/> as this game
        /// version numbers them, through <see cref="LayerMask.GetMask"/>, with the names it does not
        /// have left out and said so once. A name GetMask does not know contributes nothing, which
        /// would be a silently narrower mask - so the names are filtered first and the result is
        /// logged with its numbers.</summary>
        private static int ReliefMask()
        {
            var present = new List<string>();
            var missing = new List<string>();
            var numbered = new List<string>();

            foreach (var name in ReliefLayerNames)
            {
                var layer = LayerMask.NameToLayer(name);

                if (layer < 0)
                {
                    missing.Add(name);
                    continue;
                }

                present.Add(name);
                numbered.Add($"{name}({layer})");
            }

            var mask = present.Count == 0 ? 0 : LayerMask.GetMask(present.ToArray());

            if (!_loggedMask)
            {
                _loggedMask = true;

                Plugin.LogSource?.LogInfo(
                    $"QuestTree: the 3D relief casts against [{string.Join(", ", numbered.ToArray())}]" +
                    (missing.Count == 0
                        ? "."
                        : $"; this game version has no layer named {string.Join(", ", missing.ToArray())}."));
            }

            if (mask == 0)
                throw new InvalidOperationException(
                    "none of the relief's layer names exists in this game version, so every ray would miss");

            return mask;
        }

        // --- the relief --------------------------------------------------------------------------------

        /// <summary>One frame's rays for one band, straight down from the band's camera height.
        ///
        /// The two NativeArrays are per chunk and disposed in the finally, the first allocated OUTSIDE
        /// the try so a throw from the second cannot leave the first behind: a leaked TempJob
        /// allocation is a console warning every frame for the rest of the session.</summary>
        /// <param name="job">The build.</param>
        /// <param name="band">The band being cast.</param>
        private static void CastChunk(Job job, BandWork band)
        {
            var count = Math.Min(RaysPerFrame, band.Cells - band.Done);
            if (count <= 0) return;

            var source = band.Source;
            var floor = RayFloorFor(source);
            var distance = RayDistanceFor(source);
            var from = job.Request.From;

            // One hit a ray on the topmost band, several on an interior one - see Band.Interior. The
            // results array is count * maxHits: command i's hits are i*maxHits .. i*maxHits+maxHits-1,
            // and the first with no collider ends its list.
            var maxHits = source.Interior ? InteriorMaxHits : 1;

            if (job.HitYs == null || job.HitYs.Length < maxHits) job.HitYs = new float[maxHits];

            var ys = job.HitYs;

            var commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            var results = default(NativeArray<RaycastHit>);

            // Cells whose result has been WRITTEN, and the only thing Done advances by - in the finally,
            // so a throw half way through the results leaves Done at exactly the last cell that holds a
            // measurement. It used to advance by the whole chunk after the try, which a throw skipped:
            // the cells already written then sat past Done and FinishBand called them a bug.
            var written = 0;

            try
            {
                results = new NativeArray<RaycastHit>(count * maxHits, Allocator.TempJob);

                job.ReliefClock.Start();

                // Triggers ignored (a trigger volume is not ground) and backfaces not hit (the underside
                // of a roof is not its top). hitMultipleFaces only on an INTERIOR band, and deliberately:
                // EFT bakes a building's interior into large merged LowPolyCollider meshes, so a shelf
                // and the floor under it can be faces of ONE collider - and with hitMultipleFaces off a
                // collider answers once, with its nearest face, which is the shelf top this change exists
                // to see past. The saturation count in the relief line is what says whether six slots
                // then fill up with a single collider's faces before the floor is reached.
                var parameters = new QueryParameters(job.RayMask, source.Interior, QueryTriggerInteraction.Ignore, false);

                for (var i = 0; i < count; i++)
                {
                    var n = band.Done + i;
                    var col = n % band.Width;
                    var row = n / band.Width;

                    var origin = new Vector3(CellCentreX(job, col), source.CameraY, CellCentreZ(job, row));

                    commands[i] = new RaycastCommand(origin, Vector3.down, parameters, distance);
                }

                RaycastCommand.ScheduleBatch(commands, results, RaysPerJob, maxHits, default(JobHandle)).Complete();

                for (var i = 0; i < count; i++)
                {
                    var n = band.Done + i;
                    var first = i * maxHits;

                    // This command's hits, up to the first empty slot: collider, not distance - a slot
                    // nothing hit is a default RaycastHit, whose distance is zero and reads as a hit at
                    // the ray's origin.
                    var used = 0;

                    for (var k = 0; k < maxHits; k++)
                    {
                        var candidate = results[first + k];
                        if (candidate.collider == null) break;

                        ys[k] = candidate.point.y;
                        used = k + 1;
                    }

                    var chosen = PickHit(ys, used, maxHits, floor, source.MinY, source.Interior, out var saturated);

                    if (saturated) band.Saturated++;

                    if (chosen < 0)
                    {
                        band.Metres[n] = float.NaN;
                        band.Distance[n] = MapMeshFile.DistanceEmpty;
                        written++;
                        continue;
                    }

                    var hit = results[first + chosen];

                    if (source.Interior && hit.point.y < source.MinY - BelowFloorMetres) band.BelowFloor++;

                    band.Metres[n] = hit.point.y;
                    band.Hits++;

                    var dx = hit.point.x - from.x;
                    var dz = hit.point.z - from.y;

                    band.Distance[n] = MapMeshFile.DistanceStep(Mathf.Sqrt(dx * dx + dz * dz));

                    if (hit.point.y < job.Lowest) job.Lowest = hit.point.y;
                    if (hit.point.y > job.Highest) job.Highest = hit.point.y;

                    written++;
                }

                job.ReliefClock.Stop();
            }
            finally
            {
                if (job.ReliefClock.IsRunning) job.ReliefClock.Stop();
                if (commands.IsCreated) commands.Dispose();
                if (results.IsCreated) results.Dispose();

                band.Done += written;
                job.Rays += written;
            }
        }

        /// <summary>
        /// Which of a ray's hits is the cell's height: its index in <paramref name="ys"/>, or -1 for
        /// none.
        ///
        /// The topmost band takes the FIRST hit when it is this band's (at or above
        /// <paramref name="floorY"/>): the ray comes down from over the roofs and the first thing it
        /// meets is the roof. An interior band takes the LOWEST hit that is still this band's: its ray
        /// starts just under the floor above, meets the tops of shelves and counters on the way down,
        /// and the floor is the last of them - which is Interchange's Goshan drawn as a floor instead of
        /// as a field of bumps. Order in <paramref name="ys"/> is not relied on for the interior case,
        /// only the values.
        ///
        /// <paramref name="saturated"/> says an interior ray used every slot and its lowest qualifying
        /// hit is still more than <see cref="SaturatedAboveMetres"/> over the band's minY: the floor may
        /// be further down than <see cref="InteriorMaxHits"/> hits could reach, and the relief line
        /// counts such cells per band.
        ///
        /// Floats in, an int out and no Unity type, so the harness calls it on the shipped assembly
        /// with a synthetic column - a shelf top at floor + 1.8 m over the floor at 0.
        /// </summary>
        /// <param name="ys">The hits' world y, in the order the query returned them.</param>
        /// <param name="used">How many of <paramref name="ys"/> are hits.</param>
        /// <param name="maxHits">How many the query was allowed.</param>
        /// <param name="floorY">The lowest y that is still this band's - RayFloorFor.</param>
        /// <param name="minY">The band's minY, for the saturation test.</param>
        /// <param name="interior">Whether the band is an interior one.</param>
        /// <param name="saturated">See above.</param>
        internal static int PickHit(float[] ys, int used, int maxHits, float floorY, float minY, bool interior,
            out bool saturated)
        {
            saturated = false;

            if (ys == null || used <= 0) return -1;

            if (!interior)
            {
                var y = ys[0];

                return IsFinite(y) && y >= floorY ? 0 : -1;
            }

            var best = -1;

            for (var k = 0; k < used && k < ys.Length; k++)
            {
                var y = ys[k];
                if (!IsFinite(y) || y < floorY) continue;

                if (best < 0 || y < ys[best]) best = k;
            }

            saturated = best >= 0 && used >= maxHits && ys[best] > minY + SaturatedAboveMetres;

            return best;
        }

        /// <summary>The lowest world y a hit still counts as this band's ground: half a metre under the
        /// band (the margin the harvest's bands already carry) and, on the topmost band, the fifty
        /// metres of depth the picture is also rendered through.
        ///
        /// A method rather than two lines inside the cast loop so that it can be CHECKED: it touches no
        /// Unity type, so the harness calls it on the shipped assembly and holds it to real numbers -
        /// Customs' river bed at -17 m is this band's ground, and the same y under an interior band is
        /// the floor below seen through a hole.</summary>
        /// <param name="band">The band being cast.</param>
        internal static float RayFloorFor(Band band) =>
            band.MinY - BandFloorSlack - Math.Max(0f, band.DepthBelow);

        /// <summary>How far a ray travels: from the band's camera height to
        /// <see cref="RayFloorFor"/>'s floor, plus a metre so a surface exactly at the floor is
        /// hit.</summary>
        /// <param name="band">The band being cast.</param>
        internal static float RayDistanceFor(Band band) =>
            band.CameraY - band.MinY + RayDepthBelow + Math.Max(0f, band.DepthBelow);

        /// <summary>The world x of a cell column's centre - where its ray was cast.</summary>
        /// <param name="job">The build.</param>
        /// <param name="col">The column, 0 at the extent's MinX edge.</param>
        private static float CellCentreX(Job job, int col) =>
            (float)(job.Request.MinX + (col + 0.5d) * ReliefCellMetres);

        /// <summary>The world z of a cell row's centre.</summary>
        /// <param name="job">The build.</param>
        /// <param name="row">The row, 0 at the extent's MinZ edge.</param>
        private static float CellCentreZ(Job job, int row) =>
            (float)(job.Request.MinZ + (row + 0.5d) * ReliefCellMetres);

        /// <summary>The relief's one log line: the grid, the rays, the time and the hit rate. The hit
        /// rate is the number to read - phase 3-0 measured 100 % on Customs from either end, and
        /// anything well under that on a later map is a mask or a band that wants looking at.</summary>
        /// <param name="job">The build.</param>
        private static void ReportRelief(Job job)
        {
            var hits = 0;
            var cells = 0;

            foreach (var band in job.Bands)
            {
                hits += band.Hits;
                cells += band.Cells;
            }

            job.Hits = hits;

            var first = job.Bands.Count > 0 ? job.Bands[0] : null;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: relief for {job.Request.Map} - {job.Bands.Count} band(s), " +
                $"{(first == null ? 0 : first.Width)}x{(first == null ? 0 : first.Height)} cells at " +
                $"{N(ReliefCellMetres)} m, {N(job.Rays)} rays in {N(job.ReliefClock.Elapsed.TotalMilliseconds)} ms, " +
                $"{Pct(job.Hits, job.Rays)} hit; bands: {BandShares(job)}{BelowNotes(job)}.");

            // Said only when it happened, and per band: the evidence for whether InteriorMaxHits is
            // enough, which only an interior band full of shelving can produce.
            foreach (var band in job.Bands)
                if (band.Saturated > 0)
                    Plugin.LogSource?.LogInfo(
                        $"QuestTree: {job.Request.Map} \"{band.Source.Name}\" - {N(band.Saturated)} cell(s) used all " +
                        $"{InteriorMaxHits} hits and still stopped more than {N(SaturatedAboveMetres)} m above the " +
                        "floor; the relief there may be a shelf top, not the floor.");

            // A true statement only because the grids are NaN/DistanceEmpty from the moment they are
            // allocated (see Prepare) and FinishBand checks it: an unmeasured cell is empty in the file,
            // not ground at zero.
            if (cells != job.Rays)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {job.Request.Map}'s relief cast {N(job.Rays)} of {N(cells)} cell(s) - a chunk " +
                    $"failed (the warning above says why), and the {N(cells - job.Rays)} cell(s) it never " +
                    "reached are written as empty, so a later capture can fill them.");
        }

        /// <summary>The relief line's note on interior bands whose picked heights lie under the band's
        /// floor in more than <see cref="BelowShareToNote"/> of their hit cells - the lowest-hit rule
        /// reaching terrain under a raised floor. Empty when no band does; nothing is changed either way,
        /// the note is for a raid to verify.</summary>
        /// <param name="job">The build.</param>
        private static string BelowNotes(Job job)
        {
            var notes = new List<string>();

            foreach (var band in job.Bands)
            {
                if (!band.Source.Interior || band.Hits <= 0) continue;
                if (band.BelowFloor <= band.Hits * BelowShareToNote) continue;

                notes.Add($"band {band.Source.Level.ToString(CultureInfo.InvariantCulture)} picked " +
                          $"{Pct(band.BelowFloor, band.Hits)} of its heights more than " +
                          $"{BelowFloorMetres.ToString("0.0", CultureInfo.InvariantCulture)} m under its floor " +
                          "(terrain under a raised floor?)");
            }

            return notes.Count == 0 ? "" : "; " + string.Join("; ", notes.ToArray());
        }

        /// <summary>"-1 12 %, 0 100 %, 1 31 %": each band's level and the share of ITS rays that hit,
        /// lowest band first - one grey basement cannot hide behind a map-wide 97 %.</summary>
        /// <param name="job">The build.</param>
        private static string BandShares(Job job)
        {
            var parts = new List<string>();

            foreach (var band in job.Bands)
                parts.Add($"{band.Source.Level.ToString(CultureInfo.InvariantCulture)} {Pct(band.Hits, band.Done)}" +
                          (band.Source.Interior ? "" : " (top)"));

            return string.Join(", ", parts.ToArray());
        }

        // --- the y range and the quantisation -----------------------------------------------------------

        /// <summary>Fixes the file's y range from what the file will actually STORE - the rays that hit
        /// and the kept buildings' decoded vertices - through <see cref="YRange"/>, and says so when a
        /// building had to be clamped into it.
        ///
        /// Not from candidates' bounds any more, which is what this used to do so the range could be
        /// fixed before any building was read: the rain's bounds of 3.4e38 went straight into it. The
        /// price is that a building's y is kept as a float until this runs (x and z are quantised at
        /// once) - four bytes a vertex for at most ~900k vertices, under 4 MB.</summary>
        /// <param name="job">The build.</param>
        private static void SetRange(Job job)
        {
            var bandLow = float.PositiveInfinity;
            var bandHigh = float.NegativeInfinity;

            foreach (var band in job.Bands)
            {
                if (band.Source.MinY < bandLow) bandLow = band.Source.MinY;
                if (band.Source.MaxY > bandHigh) bandHigh = band.Source.MaxY;
            }

            if (!YRange(job.Lowest, job.Highest, job.VertexLow, job.VertexHigh, bandLow, bandHigh,
                    out var low, out var high, out var clamped))
                throw new InvalidOperationException("nothing measurable was found to quantise heights over");

            if (clamped)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: a building on {job.Request.Map} reaches {F(job.VertexLow)}..{F(job.VertexHigh)} m, " +
                    $"past the ground's {F(job.Lowest)}..{F(job.Highest)} m by more than " +
                    $"{N(YRangeMarginMetres)} m - its heights are clamped to {F(low)}..{F(high)} m so the rest " +
                    "of the map keeps its resolution.");

            job.File.SetYRange(low, high);
        }

        /// <summary>
        /// The y range a file stores its heights over, from what it stores. The GROUND decides it -
        /// the ray hits, or the bands' own edges when nothing was hit - and the kept buildings may widen
        /// it by at most <see cref="YRangeMarginMetres"/> each way. So a vertex from a mesh nobody
        /// anticipated (a rain volume, a skybox, a sentinel position) can move a roof, and can no longer
        /// flatten a map: with the old rule a single 1.7e38 made every other height in the file the same
        /// sixteen-bit code.
        ///
        /// Floats in, floats out, no Unity type - the harness calls it on the shipped assembly with the
        /// rain's own numbers.
        /// </summary>
        /// <param name="hitLow">The lowest ray hit, or +infinity when there was none.</param>
        /// <param name="hitHigh">The highest ray hit, or -infinity.</param>
        /// <param name="vertexLow">The lowest kept building vertex, or +infinity when none was kept.</param>
        /// <param name="vertexHigh">The highest kept building vertex, or -infinity.</param>
        /// <param name="bandLow">The lowest band's minY - the reference when no ray hit.</param>
        /// <param name="bandHigh">The highest band's maxY.</param>
        /// <param name="low">The range's low end, before MapMeshFile adds its own slack.</param>
        /// <param name="high">The range's high end.</param>
        /// <param name="clamped">Whether a building reached past the margin and was cut to it.</param>
        internal static bool YRange(float hitLow, float hitHigh, float vertexLow, float vertexHigh,
            float bandLow, float bandHigh, out float low, out float high, out bool clamped)
        {
            clamped = false;

            var reference = IsFinite(hitLow) && IsFinite(hitHigh) && hitHigh >= hitLow;

            low = reference ? hitLow : bandLow;
            high = reference ? hitHigh : bandHigh;

            if (!IsFinite(low) || !IsFinite(high) || high < low) return false;

            var floor = low - YRangeMarginMetres;
            var ceiling = high + YRangeMarginMetres;

            if (IsFinite(vertexLow) && vertexLow < low)
            {
                if (vertexLow < floor) clamped = true;
                low = Math.Max(vertexLow, floor);
            }

            if (IsFinite(vertexHigh) && vertexHigh > high)
            {
                if (vertexHigh > ceiling) clamped = true;
                high = Math.Min(vertexHigh, ceiling);
            }

            return true;
        }

        /// <summary>One frame's worth of a band's cells turned from metres into the file's sixteen-bit
        /// codes.</summary>
        /// <param name="job">The build.</param>
        /// <param name="band">The band being quantised.</param>
        private static void QuantiseChunk(Job job, BandWork band)
        {
            var end = Math.Min(band.Cells, band.Quantised + QuantisePerFrame);

            for (var n = band.Quantised; n < end; n++)
            {
                // QuantiseHeight answers NoHit for a NaN, which is exactly what an unhit cell holds.
                band.Codes[n] = job.File.QuantiseHeight(band.Metres[n]);
            }

            band.Quantised = end;
        }

        /// <summary>Adds a finished band to the file and drops the float heights it no longer
        /// needs.</summary>
        /// <param name="job">The build.</param>
        /// <param name="band">The band that is done.</param>
        private static void FinishBand(Job job, BandWork band)
        {
            if (band.Quantised < band.Cells)
            {
                // Whatever was never quantised is still zero, which would read as ground at YMin -
                // a floor across the map. NoHit is the honest value for a cell this build did not
                // finish.
                for (var n = band.Quantised; n < band.Cells; n++)
                {
                    band.Codes[n] = MapMeshFile.NoHit;
                    band.Distance[n] = MapMeshFile.DistanceEmpty;
                }
            }

            // The check that can fail, and the one that would have caught the zero-filled grids this
            // code shipped with for an afternoon: every cell the rays never reached must be EMPTY in
            // the file. It reads only the cells past the last one cast, so it costs nothing on the
            // ordinary path where that is none of them.
            var wrong = 0;

            for (var n = band.Done; n < band.Cells && n < band.Codes.Length; n++)
                if (band.Codes[n] != MapMeshFile.NoHit) wrong++;

            if (wrong > 0)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {N(wrong)} cell(s) of {job.Request.Map} \"{band.Source.Name}\" carry a height " +
                    "no ray measured - the band's grid was not initialised empty. The 3D ground will show a " +
                    "flat plane there; this is a bug in MapMeshBuilder, not in the capture.");

            job.File.Bands.Add(new MapMeshFile.ReliefBand
            {
                Level = band.Source.Level,
                CellMetres = ReliefCellMetres,
                Width = band.Width,
                Height = band.Height,
                Heights = band.Codes,
                Distance = band.Distance
            });

            band.Metres = null;
        }

        // --- the candidates ------------------------------------------------------------------------------

        /// <summary>The one scan of the scene. Everything after this is a filter over the array it
        /// leaves.</summary>
        /// <param name="job">The build.</param>
        private static void Scan(Job job)
        {
            job.Renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            job.RendererCount = job.Renderers == null ? 0 : job.Renderers.Length;
            job.Scanned = 0;
        }

        /// <summary>One frame's worth of the candidate filter: the cheap tests first, the bounds read
        /// last. Nothing here touches the y range any more - see SizeVerdict and YRange for why.</summary>
        /// <param name="job">The build.</param>
        private static void FilterChunk(Job job)
        {
            var renderers = job.Renderers;
            if (renderers == null) return;

            var end = Math.Min(job.RendererCount, job.Scanned + CandidateScanPerFrame);
            var mask = job.Request.RenderMask;
            var notBuilding = NotBuildingMask();

            var minX = job.Request.MinX - CentreMargin;
            var maxX = job.Request.MaxX + CentreMargin;
            var minZ = job.Request.MinZ - CentreMargin;
            var maxZ = job.Request.MaxZ + CentreMargin;

            for (var i = job.Scanned; i < end; i++)
            {
                job.Scanned = i + 1;

                var renderer = renderers[i];
                if (renderer == null) continue;

                // No skinned-renderer test, deliberately, and this is the note that says why rather
                // than a test the compiler calls unreachable: FindObjectsOfType<MeshRenderer> cannot
                // return a SkinnedMeshRenderer at all - the two are siblings under Renderer, not
                // parent and child - so the scan's TYPE is what excludes a skinned mesh, whose bind
                // pose would have been meaningless here anyway.
                var go = renderer.gameObject;
                if (go == null || !go.scene.IsValid()) continue;

                var layer = go.layer;
                if ((mask & (1 << layer)) == 0) continue;
                if ((notBuilding & (1 << layer)) != 0) continue;

                var bounds = renderer.bounds;
                var size = bounds.size;

                var verdict = SizeVerdict(size.x, size.y, size.z,
                    job.Request.MaxX - job.Request.MinX, job.Request.MaxZ - job.Request.MinZ);

                if (verdict == SizeOversized) job.Oversized++;
                if (verdict != SizeOk) continue;

                var centre = bounds.center;
                if (!IsFinite(centre.x) || !IsFinite(centre.y) || !IsFinite(centre.z)) continue;
                if (centre.x < minX || centre.x > maxX || centre.z < minZ || centre.z > maxZ) continue;

                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.vertexCount < 3) continue;

                // NOT folded into the y range. The range used to be taken from candidates' bounds so it
                // could be fixed before any building was read, and one renderer with bounds of 3.4e38 -
                // EFT's rain - made it three hundred undecillion metres tall and every stored height the
                // same number. It is now taken from what is actually stored; see SetRange and YRange.
                job.Candidates.Add(new Candidate
                {
                    Renderer = renderer,
                    Mesh = mesh,
                    Bounds = bounds,
                    Volume = Math.Abs(size.x * size.y * size.z)
                });
            }

            if (job.Scanned >= job.RendererCount) job.Renderers = null;
        }

        /// <summary>
        /// Whether a renderer's world bounds are the size of a building: <see cref="SizeOk"/>, or why
        /// not.
        ///
        /// The upper limits are the finding of the second review, read straight off the probe's file:
        /// ~30 MeshRenderers under Weather/DepthPhoto/RainFall on the Default layer - which IS in the
        /// render mask - report bounds of 3.4e38 m on every axis, centred on the player. Those numbers
        /// are FINITE, so a finiteness test passes them; they are longer than six metres and taller than
        /// two and a half; their centre is inside the extent. Every test the filter had said "building".
        /// Nothing real on a map is wider than the map or taller than <see cref="MaxBuildingHeight"/>,
        /// so bounds that big mean "no bounds", and the renderer is not a building.
        ///
        /// Floats in, an int out and no Unity type anywhere, so the harness can call it on the shipped
        /// assembly and hold it to the rain's own numbers.
        /// </summary>
        /// <param name="sizeX">The bounds' size along x, in metres.</param>
        /// <param name="sizeY">Along y.</param>
        /// <param name="sizeZ">Along z.</param>
        /// <param name="spanX">The extent's width, in metres.</param>
        /// <param name="spanZ">The extent's depth, in metres.</param>
        internal static int SizeVerdict(float sizeX, float sizeY, float sizeZ, double spanX, double spanZ)
        {
            if (!IsFinite(sizeX) || !IsFinite(sizeY) || !IsFinite(sizeZ)) return SizeNotFinite;

            if (sizeX > spanX || sizeZ > spanZ || sizeY > MaxBuildingHeight) return SizeOversized;

            if (sizeY < MinBuildingHeight) return SizeSmall;
            if (Math.Max(sizeX, sizeZ) < MinBuildingLongSide) return SizeSmall;

            return SizeOk;
        }

        /// <summary>The layers of <see cref="NotBuildingLayerNames"/> as a mask, or 0 for the names
        /// this game version does not have.</summary>
        private static int NotBuildingMask()
        {
            var mask = 0;

            foreach (var name in NotBuildingLayerNames)
            {
                var layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask |= 1 << layer;
            }

            return mask;
        }

        /// <summary>Largest bounds volume first, and the attribute layout read for each: the budget
        /// spends its triangles on the buildings that make a skyline, and a candidate's stream,
        /// offset, stride and format are what a GPU readback has to get right.</summary>
        /// <param name="job">The build.</param>
        private static void SortCandidates(Job job)
        {
            job.Renderers = null;
            job.Candidates.Sort((a, b) => b.Volume.CompareTo(a.Volume));

            foreach (var candidate in job.Candidates)
            {
                var mesh = candidate.Mesh;

                candidate.Readable = mesh.isReadable;
                candidate.Stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                candidate.Offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                candidate.Format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
                candidate.Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
                candidate.Stride = candidate.Stream >= 0 ? mesh.GetVertexBufferStride(candidate.Stream) : 0;

                Placement(candidate);
            }
        }

        /// <summary>Which submeshes are this renderer's and what space their vertices are in - the
        /// two things static batching changes. See <see cref="Candidate.SubFirst"/> and
        /// <see cref="Candidate.Matrix"/>.</summary>
        /// <param name="candidate">The candidate to fill in.</param>
        private static void Placement(Candidate candidate)
        {
            var renderer = candidate.Renderer;
            var mesh = candidate.Mesh;
            var own = renderer.localToWorldMatrix;

            candidate.SubFirst = 0;
            candidate.SubEnd = mesh.subMeshCount;
            candidate.Matrix = own;
            candidate.Fallback = null;

            if (!renderer.isPartOfStaticBatch) return;

            var materials = renderer.sharedMaterials;
            var first = renderer.subMeshStartIndex;
            var count = materials == null ? 0 : materials.Length;

            if (first < 0 || first >= mesh.subMeshCount || count <= 0)
            {
                // A batch whose share cannot be told is read as nothing rather than as the whole
                // batch: the empty range makes Wanted count zero triangles and pass the renderer by.
                candidate.SubFirst = 0;
                candidate.SubEnd = 0;
                return;
            }

            candidate.SubFirst = first;
            candidate.SubEnd = Math.Min(mesh.subMeshCount, first + count);

            // Unity builds a static batch's combined mesh in WORLD space - or in the space of the
            // batch's root, when StaticBatchingUtility.Combine was given one - so the identity is the
            // first transform to try. The root itself is not public in this Unity (the compiler says
            // Renderer has no staticBatchRootTransform), so a rooted batch cannot be placed by name:
            // the renderer's own matrix is the one fallback, and Plausible refuses whichever of the two
            // does not put the vertices inside the renderer's bounds. A rooted batch that neither fits
            // is counted as implausible in the log line rather than drawn in the wrong place.
            candidate.Matrix = Matrix4x4.identity;
            candidate.Fallback = own;
        }

        // --- which candidates are taken ------------------------------------------------------------------

        /// <summary>Whether this candidate is read at all: the format's building cap, whether it is still
        /// its group's source (a fallback can switch a group) and not already stored, and whether its
        /// source can be decoded. Every question here is answered from what the budget pass recorded, so a
        /// rejected candidate costs a few comparisons. The triangle budget is <see cref="Admit"/>'s.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static bool Wanted(Job job, Candidate candidate)
        {
            if (job.File.Buildings.Count >= MapMeshFile.MaxBuildings)
            {
                job.Stopped = true;
                job.StoppedWhy = $"the format's cap of {N(MapMeshFile.MaxBuildings)} buildings was reached";
                return false;
            }

            if (job.Claimed.Contains(candidate.Renderer)) return false;
            if (!candidate.Coarse && !IsSource(job, candidate)) return false;
            if (candidate.SourceTriangles <= 0 || candidate.SourceTriangles > MaxSourceTriangles) return false;

            // Too many vertices to DECODE, whatever it is reduced to afterwards.
            return candidate.Mesh.vertexCount <= MapMeshFile.MaxVerticesPerBuilding;
        }

        /// <summary>
        /// The most triangles this building may be stored with - its HARD LIMIT - taken from the ledger
        /// as pending, or 0 when the budget has no room for it at all.
        ///
        /// A source at or under its target asks for the source; one over it asks for target x
        /// <see cref="MeshDecimator.HardLimitFactor"/> - the decimator's overshoot allowance - but gets
        /// only what the UNRESERVED headroom holds, and never less than its target when the headroom holds
        /// that. Its own reservation was released into the headroom a moment before, so a building can
        /// always have at least what the budget promised it; the overshoot is paid only from what nobody
        /// was promised. That is the whole of H4: the largest buildings' overshoot used to be paid by the
        /// smallest buildings at the tail of the list.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static int Admit(Job job, Candidate candidate)
        {
            var limit = BudgetLedger.Limit(candidate.SourceTriangles, TargetFor(job, candidate), job.Ledger.Headroom,
                AreaBudget.MinTriangles, MeshDecimator.HardLimitFactor);

            if (limit <= 0 || !job.Ledger.Admit(limit)) return 0;

            return limit;
        }

        /// <summary>Whether this renderer is its group's source right now: a member of the detail level
        /// while the group reads detail, of the coarse level once it has switched. A renderer under no
        /// group is its own source.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static bool IsSource(Job job, Candidate candidate)
        {
            if (candidate.Group == null) return true;
            if (!job.Groups.TryGetValue(candidate.Group, out var state) || state == null) return false;

            var set = state.UsingCoarse ? state.Coarse : state.Detail;

            return set != null && set.Contains(candidate.Renderer);
        }

        /// <summary>A group's state, decided the first time the group is met: its detail level when that
        /// level is real geometry within <see cref="MaxSourceTriangles"/>, its coarse level by the stage-U
        /// rule, and which of the two it starts on.</summary>
        /// <param name="job">The build.</param>
        /// <param name="group">The LOD group.</param>
        private static GroupState StateOf(Job job, LODGroup group)
        {
            if (job.Groups.TryGetValue(group, out var state)) return state;

            state = new GroupState();

            // GetLODs once per GROUP - it allocates the whole LOD array every call.
            var lods = group.GetLODs();
            var coarse = ChooseLevel(job, lods);

            if (coarse >= 0 && lods[coarse].renderers != null)
            {
                state.CoarseList = new List<Renderer>();

                foreach (var renderer in lods[coarse].renderers)
                    if (renderer != null) state.CoarseList.Add(renderer);

                state.Coarse = new HashSet<Renderer>(state.CoarseList);
            }

            // Level 0 as the source when it is real geometry and small enough. When level 0 IS the coarse
            // level (a one-level group), there is nothing coarser to fall back to - EnqueueCoarse sees the two
            // sets are the same renderers.
            if (lods != null && lods.Length > 0 && lods[0].renderers != null)
            {
                var triangles = LevelTriangles(lods[0].renderers, out var impostor);

                if (!impostor && triangles >= MinLodTriangles)
                {
                    if (triangles <= MaxSourceTriangles)
                    {
                        state.Detail = new HashSet<Renderer>();
                        foreach (var renderer in lods[0].renderers)
                            if (renderer != null) state.Detail.Add(renderer);
                    }
                    else
                    {
                        job.InputGuarded++;
                    }
                }
            }

            state.UsingCoarse = state.Detail == null;
            job.LevelRenderers += (state.Detail?.Count ?? 0) + (state.Coarse?.Count ?? 0);
            job.Groups[group] = state;

            return state;
        }

        /// <summary>Triangles across a level's renderers, and whether any of them draws with an impostor
        /// shader.</summary>
        /// <param name="renderers">The level's renderers.</param>
        /// <param name="impostor">Whether any material's shader is an impostor.</param>
        private static long LevelTriangles(Renderer[] renderers, out bool impostor)
        {
            impostor = false;
            var triangles = 0L;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;

                if (mesh != null)
                    for (var s = 0; s < mesh.subMeshCount; s++)
                        if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;

                var materials = renderer.sharedMaterials;

                if (materials != null)
                    foreach (var material in materials)
                    {
                        var shader = material != null && material.shader != null ? material.shader.name : null;

                        if (!string.IsNullOrEmpty(shader) &&
                            shader.IndexOf(ImpostorShaderMark, StringComparison.OrdinalIgnoreCase) >= 0)
                            impostor = true;
                    }
            }

            return triangles;
        }

        /// <summary>One frame's worth of the budget pass: each candidate's group state, its source size
        /// (its own submeshes - a static batch's share), and its footprint. Each candidate in its OWN
        /// guard: a renderer that throws (destroyed under us, a mesh that will not answer) costs that
        /// candidate, not the rest of the pass - one bad renderer used to end the pass and leave every
        /// later candidate with no size, which is to say out of the map.</summary>
        /// <param name="job">The build.</param>
        private static void BudgetChunk(Job job)
        {
            var end = Math.Min(job.Candidates.Count, job.Budgeted + CandidatesPerFrame);

            for (var i = job.Budgeted; i < end; i++)
            {
                job.Budgeted = i + 1;

                var candidate = job.Candidates[i];

                try
                {
                    // includeInactive: the hold has just switched culled objects on, and a group can still sit
                    // on an inactive parent - the default overload would answer "no group" and let every level
                    // of one building through.
                    candidate.Group = candidate.Renderer.GetComponentInParent<LODGroup>(true);
                    if (candidate.Group != null) StateOf(job, candidate.Group);

                    candidate.SourceTriangles = SubmeshTriangles(candidate);
                    candidate.Footprint = Math.Abs((double)candidate.Bounds.size.x * candidate.Bounds.size.z);

                    if (candidate.Group == null && candidate.SourceTriangles > MaxSourceTriangles) job.InputGuarded++;
                }
                catch (Exception ex)
                {
                    candidate.SourceTriangles = 0;
                    job.Note("a building's size", ex);
                }
            }
        }

        /// <summary>Triangles in a candidate's own submeshes - a static batch's share of its mesh.</summary>
        /// <param name="candidate">The candidate.</param>
        private static long SubmeshTriangles(Candidate candidate)
        {
            var mesh = candidate.Mesh;
            var triangles = 0L;

            for (var s = candidate.SubFirst; s < candidate.SubEnd; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;

            return triangles;
        }

        /// <summary>The area budget over every candidate that is its group's source now: the targets,
        /// scaled by one factor when the map's stored total would pass <see cref="BudgetShare"/> of
        /// <see cref="MaxBuildingTriangles"/>, and each one RESERVED in the ledger. The share keeps the rest
        /// of the cap unreserved - the headroom a decimation's overshoot, a fallback's coarse level or an
        /// undecimated source is paid from. See <see cref="AreaBudget"/> and <see cref="BudgetLedger"/>.</summary>
        /// <param name="job">The build.</param>
        private static void ApplyBudget(Job job)
        {
            var sources = new List<Candidate>();

            foreach (var candidate in job.Candidates)
                if (candidate.SourceTriangles > 0 && candidate.SourceTriangles <= MaxSourceTriangles &&
                    IsSource(job, candidate))
                    sources.Add(candidate);

            var footprints = new double[sources.Count];
            var triangles = new long[sources.Count];

            for (var i = 0; i < sources.Count; i++)
            {
                footprints[i] = sources[i].Footprint;
                triangles[i] = sources[i].SourceTriangles;
            }

            var cap = (long)(MaxBuildingTriangles * BudgetShare);
            var targets = AreaBudget.Targets(footprints, triangles, cap, out var scale);

            for (var i = 0; i < sources.Count; i++)
            {
                sources[i].Target = targets[i];
                sources[i].Reserved = Math.Min(triangles[i], targets[i]);
                job.Ledger.Reserve(sources[i].Reserved);
            }

            job.BudgetScale = scale;
            job.Budgeted = job.Candidates.Count;
        }

        /// <summary>A candidate's target: the one the budget gave it, or - for a renderer that became a
        /// source after the budget was set (a group switched to its coarse level) - its footprint at the
        /// same density and scale.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static int TargetFor(Job job, Candidate candidate)
        {
            if (candidate.Target > 0) return candidate.Target;

            var basis = Math.Min(AreaBudget.MaxTrianglesPerBuilding,
                Math.Max(AreaBudget.MinTriangles, candidate.Footprint * AreaBudget.TrianglesPerSquareMetre));

            candidate.Target = Math.Max(AreaBudget.MinTriangles, (int)(basis * job.BudgetScale));

            return candidate.Target;
        }

        /// <summary>Whether this frame's budget of main-thread work is spent.</summary>
        /// <param name="job">The build.</param>
        private static bool FrameSpent(Job job) => job.FrameClock.Elapsed.TotalMilliseconds > FrameBudgetMs;

        /// <summary>
        /// The building's fallback to the game's own coarse level of detail - the stage-U path: its group
        /// switches to the coarse level and those renderers are QUEUED, ahead of the rest of the list, to be
        /// read and stored as they are, through the same pipeline and the same frame budget as everything
        /// else (they may sit earlier in the ordered list, already passed by as non-members). False when
        /// there is nothing coarser that would not duplicate or reproduce this source: no group, a group
        /// with a detail renderer already stored or still on a worker, or a group whose coarse level is its
        /// detail level - the caller
        /// then stores the source itself (undecimated or clustered), never nothing.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate that could not be decimated.</param>
        private static bool EnqueueCoarse(Job job, Candidate candidate)
        {
            if (candidate.Coarse || candidate.Group == null ||
                !job.Groups.TryGetValue(candidate.Group, out var state) ||
                state.UsingCoarse || state.DetailCommitted > 0 || state.CoarseList == null ||
                (state.Detail != null && state.Detail.SetEquals(state.Coarse)))
                return false;

            job.FellBack++;
            state.UsingCoarse = true;

            foreach (var renderer in state.CoarseList)
            {
                if (renderer == null || job.Claimed.Contains(renderer)) continue;

                Candidate coarse = null;
                Step(job, "a coarse level", () => coarse = MakeCandidate(job, renderer));
                if (coarse == null) continue;

                coarse.Group = candidate.Group;
                coarse.Coarse = true;
                job.Extra.Enqueue(coarse);
            }

            return true;
        }

        /// <summary>A candidate built on the spot for a group's coarse-level renderer, through the SAME
        /// filters <see cref="FilterChunk"/> applies to every renderer - the render mask, the not-building
        /// layers, the size verdict and the extent - so a fallback cannot bring in what the filter would
        /// have kept out. Null for one that fails them or has no mesh.</summary>
        /// <param name="job">The build.</param>
        /// <param name="renderer">The renderer.</param>
        private static Candidate MakeCandidate(Job job, Renderer renderer)
        {
            if (!(renderer is MeshRenderer meshRenderer)) return null;

            var go = renderer.gameObject;
            if (go == null || !go.scene.IsValid()) return null;

            var layer = go.layer;
            if ((job.Request.RenderMask & (1 << layer)) == 0) return null;
            if ((NotBuildingMask() & (1 << layer)) != 0) return null;

            var bounds = renderer.bounds;
            var size = bounds.size;

            if (SizeVerdict(size.x, size.y, size.z, job.Request.MaxX - job.Request.MinX,
                    job.Request.MaxZ - job.Request.MinZ) != SizeOk)
                return null;

            var centre = bounds.center;
            if (!IsFinite(centre.x) || !IsFinite(centre.y) || !IsFinite(centre.z)) return null;
            if (centre.x < job.Request.MinX - CentreMargin || centre.x > job.Request.MaxX + CentreMargin ||
                centre.z < job.Request.MinZ - CentreMargin || centre.z > job.Request.MaxZ + CentreMargin)
                return null;

            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || mesh.vertexCount < 3) return null;

            var candidate = new Candidate
            {
                Renderer = meshRenderer,
                Mesh = mesh,
                Bounds = bounds,
                Volume = Math.Abs(size.x * size.y * size.z),
                Readable = mesh.isReadable,
                Stream = mesh.GetVertexAttributeStream(VertexAttribute.Position),
                Offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position),
                Format = mesh.GetVertexAttributeFormat(VertexAttribute.Position),
                Dimension = mesh.GetVertexAttributeDimension(VertexAttribute.Position),
                Footprint = Math.Abs((double)size.x * size.z),
            };

            candidate.Stride = candidate.Stream >= 0 ? mesh.GetVertexBufferStride(candidate.Stream) : 0;
            Placement(candidate);
            candidate.SourceTriangles = SubmeshTriangles(candidate);

            return candidate;
        }

        /// <summary>The COARSE level - stage U's rule, and stage V's fallback - or -1 when no level of the group is
        /// geometry. Counts the impostor levels it walked past, for the log line.</summary>
        /// <param name="job">The build.</param>
        /// <param name="lods">The group's levels, as GetLODs returned them.</param>
        private static int ChooseLevel(Job job, LOD[] lods)
        {
            if (lods == null || lods.Length == 0) return -1;

            for (var level = lods.Length - 1; level >= 0; level--)
            {
                var renderers = lods[level].renderers;
                if (renderers == null) continue;

                var triangles = 0L;
                var impostor = false;
                var any = false;

                foreach (var renderer in renderers)
                {
                    if (renderer == null) continue;

                    any = true;

                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = filter != null ? filter.sharedMesh : null;

                    if (mesh != null)
                        for (var s = 0; s < mesh.subMeshCount; s++)
                            if (mesh.GetTopology(s) == MeshTopology.Triangles)
                                triangles += mesh.GetIndexCount(s) / 3;

                    var materials = renderer.sharedMaterials;

                    if (materials != null)
                        foreach (var material in materials)
                        {
                            var shader = material != null && material.shader != null ? material.shader.name : null;

                            if (!string.IsNullOrEmpty(shader) &&
                                shader.IndexOf(ImpostorShaderMark, StringComparison.OrdinalIgnoreCase) >= 0)
                                impostor = true;
                        }
                }

                if (!any) continue;

                // Counted apart, because they are different findings: an impostor level is a card with
                // a picture of the building on it (the game ships AmplifyImpostors), while a thin level
                // is real geometry that has been reduced to a box. A log line that added them up would
                // report "18 impostor LODs" on a map that has none.
                if (impostor)
                {
                    job.ImpostorSkipped++;
                    continue;
                }

                if (triangles < MinLodTriangles)
                {
                    job.ThinSkipped++;
                    continue;
                }

                return level;
            }

            return -1;
        }

        // --- reading a building ---------------------------------------------------------------------------

        // --- the pipeline: main thread reads, workers place and reduce ------------------------------------

        /// <summary>What <see cref="StoreWorld"/> answers: stored; refused for a reason no other mesh of the
        /// building would change; refused for one of the vertex caps - which a cluster can fix.</summary>
        private const int Stored = 0;

        private const int Refused = 1;

        private const int RefusedVertices = 2;

        /// <summary>Applies the finished flights in the order they are found finished - at least one a
        /// frame, and more only while the frame's budget lasts - and hands their workspaces back. Called once
        /// a frame by the building loop, and once a frame while a GPU readback is being waited for.</summary>
        /// <param name="job">The build.</param>
        /// <param name="flights">The flights in the air (job.Flights).</param>
        private static void Poll(Job job, List<Flight> flights)
        {
            var applied = 0;

            for (var k = 0; k < flights.Count; k++)
            {
                if (applied > 0 && FrameSpent(job)) break;

                var flight = flights[k];
                if (!flight.Task.IsCompleted) continue;

                flights.RemoveAt(k--);
                applied++;
                job.InFlightTriangles -= flight.Triangles;

                // Pooled only while it is of an ordinary building's size, lists and decimator together: a
                // lane grown by a large source is dropped - garbage, not held for the rest of the phase.
                var lane = flight.Workspace;
                if (lane != null)
                {
                    if (lane.Bytes() <= MaxPooledWorkspaceBytes) job.Workspaces.Push(lane);
                    else job.LanesDropped++;
                }

                Step(job, "a finished building", () => Apply(job, flight));
            }
        }

        /// <summary>The pipeline's memory right now - every flight's estimate plus the pooled lanes - kept
        /// as a peak for the peak-memory line.</summary>
        /// <param name="job">The build.</param>
        /// <param name="flights">The flights in the air.</param>
        private static void Track(Job job, List<Flight> flights)
        {
            var bytes = 0L;

            foreach (var flight in flights) bytes += flight.Bytes;
            foreach (var lane in job.Workspaces) bytes += lane.Bytes();

            if (bytes > job.PeakPipelineBytes) job.PeakPipelineBytes = bytes;
        }

        /// <summary>Hands a captured source to a worker with a workspace of its own - one from the pool, or
        /// a new one (at most <see cref="MaxWorkers"/> are ever in use). The renderer is claimed here, so a
        /// coarse level queued while this one is still in flight is not read a second time.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="source">Its arrays, out of Unity.</param>
        /// <param name="limit">Its hard limit, already pending in the ledger.</param>
        private static Flight Launch(Job job, Candidate candidate, Source source, int limit)
        {
            source.Limit = limit;
            source.Target = Math.Max(1, Math.Min(TargetFor(job, candidate), limit));
            source.MayDecimate = !job.DecimationStopped;

            var lane = job.Workspaces.Count > 0 ? job.Workspaces.Pop() : new Lane();

            job.Claimed.Add(candidate.Renderer);
            job.InFlightTriangles += source.SourceTriangles;

            if (candidate.Group != null && job.Groups.TryGetValue(candidate.Group, out var state) && state != null &&
                !state.UsingCoarse && !candidate.Coarse)
            {
                state.DetailCommitted++;
                candidate.AsDetail = true;
            }

            // The estimate the peak line adds up: the source's own arrays, the worker's lists over it, and
            // a decimator workspace when it will decimate.
            var decimates = source.MayDecimate && source.SourceTriangles > limit &&
                            source.SourceTriangles <= MaxDecimatedSource;

            return new Flight
            {
                Candidate = candidate,
                Source = source,
                Workspace = lane,
                Limit = limit,
                Triangles = source.SourceTriangles,
                Bytes = source.Bytes() + source.SourceTriangles * WorkerBytesPerTriangle +
                        (decimates ? source.SourceTriangles * DecimatorBytesPerTriangle : 0L),
                Task = Task.Run(() => Process(source, lane)),
            };
        }

        /// <summary>The last resort as a flight of its own (M4): the source clustered to its limit on a
        /// worker, under <see cref="ClusterMs"/>. Started only when that path is actually chosen, never past
        /// the hard cap; the limit stays pending in the ledger until it is applied.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="world">The placed source.</param>
        /// <param name="limit">Its limit.</param>
        private static void LaunchCluster(Job job, Candidate candidate, WorldMesh world, int limit)
        {
            job.InFlightTriangles += world.Triangles;

            job.Flights.Add(new Flight
            {
                Candidate = candidate,
                Limit = limit,
                Clustering = true,
                Triangles = world.Triangles,
                Bytes = (world.P.Length + world.T.Length) * 4L + world.Triangles * WorkerBytesPerTriangle,
                Task = Task.Run(() =>
                {
                    var result = MeshDecimator.Cluster(world.P, world.T, limit, ClusterMs);
                    var outcome = new Outcome { SourceTriangles = world.Triangles, TimedOut = result.TimedOut };

                    if (!result.TimedOut && result.Triangles != null && result.Triangles.Length >= 3)
                        outcome.Mesh = new WorldMesh { P = result.Positions, T = result.Triangles, Mirrored = world.Mirrored };

                    return outcome;
                }),
            });
        }

        /// <summary>
        /// A finished flight, on the main thread: its counts, then the building - NEVER dropped for a
        /// decimation that did not work (H3). Before the hard cap: the worker's mesh when it fits the
        /// building's limit; else the game's coarse level when the group has one to switch to; else the
        /// source as it is when the unreserved headroom holds its overshoot ("stored undecimated"); else a
        /// cluster flight to the limit. Past the hard cap the drain is bounded: the source as it is if the
        /// headroom holds it, else the coarse level, else ABANDONED and counted - no cluster is started.
        ///
        /// The ledger is settled in a finally (L1) - a renderer destroyed under us throws from StoreWorld,
        /// and that must not leak its pending limit - unless the limit was handed to a cluster flight. The
        /// group's DetailCommitted counts detail renderers stored or in flight (L3): this flight's is taken
        /// off on entry and put back only when it is stored or handed on.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="flight">The finished flight.</param>
        private static void Apply(Job job, Flight flight)
        {
            var candidate = flight.Candidate;
            var limit = flight.Limit;
            var settled = false;
            var committed = false;

            var state = candidate.Group != null && job.Groups.TryGetValue(candidate.Group, out var s) ? s : null;
            if (candidate.AsDetail && state != null) state.DetailCommitted--;

            // Stores one mesh and settles for it; a refusal for the vertex caps is counted by StoreWorld.
            int Store(WorldMesh mesh)
            {
                var code = StoreWorld(job, candidate, mesh);
                job.Ledger.Settle(limit, code == Stored ? mesh.Triangles : 0);
                settled = true;
                if (code == Stored) committed = true;
                return code;
            }

            // The cluster path, once: never past the hard cap or on the capture's abort.
            bool Cluster(WorldMesh world)
            {
                if (flight.Clustering || job.PastHard || job.Request.Abort || world == null) return false;

                LaunchCluster(job, candidate, world, limit);
                settled = true;         // handed on: the cluster flight settles it
                committed = true;       // still in flight
                return true;
            }

            try
            {
                Outcome outcome = null;

                if (flight.Task.IsFaulted || flight.Task.IsCanceled)
                {
                    job.Failed++;
                    job.Note("a building's worker",
                        flight.Task.Exception?.GetBaseException() ?? new InvalidOperationException("the worker failed"));
                }
                else
                {
                    outcome = flight.Task.Result;
                }

                if (flight.Clustering)
                {
                    if (outcome?.Mesh != null && outcome.Mesh.Triangles <= limit && Store(outcome.Mesh) == Stored)
                        job.ClusteredStored++;
                    else if (outcome != null && outcome.TimedOut) job.ClusterTimedOut++;
                    else job.Unstored++;

                    return;
                }

                if (outcome != null)
                {
                    job.WorkerMs += outcome.WorkerMs;
                    job.DroppedTriangles += outcome.Dropped;

                    if (outcome.WorkspaceBytes > job.PeakWorkspaceBytes) job.PeakWorkspaceBytes = outcome.WorkspaceBytes;
                    if (outcome.DecodedBytes > job.PeakDecodedBytes) job.PeakDecodedBytes = outcome.DecodedBytes;

                    if (flight.Source != null && flight.Source.FromGpu)
                    {
                        if (outcome.Undecodable) job.Unreadable++;
                        else job.GpuRead++;
                    }

                    if (outcome.Implausible) job.Implausible++;
                    if (outcome.TimedOut) job.TimedOut++;
                    if (outcome.OverLimit) job.OverLimit++;
                }

                // 1. what the worker made, inside the limit
                if (outcome?.Mesh != null)
                {
                    var code = Store(outcome.Mesh);

                    if (code == Stored && outcome.Decimated)
                    {
                        job.Decimated++;
                        job.SourceDecimated += outcome.SourceTriangles;
                    }

                    // L2: refused for its vertices - fewer vertices is exactly what the cluster makes.
                    if (code == RefusedVertices)
                    {
                        settled = false;
                        Retake(job, limit);
                        if (!Cluster(outcome.Mesh)) job.Unstored++;
                    }

                    return;
                }

                // Nothing to store at all - no geometry, no transform that fits: not a decimation failure.
                if (outcome != null && outcome.Source == null) return;

                var source = outcome?.Source;
                var fits = source != null && source.Triangles - (long)limit <= job.Ledger.Headroom;

                if (!job.PastHard)
                {
                    // 2. the game's coarse level
                    if (EnqueueCoarse(job, candidate)) return;

                    // 3. the source as it is, paid from what nobody was promised
                    if (fits)
                    {
                        var code = Store(source);

                        if (code == Stored) job.StoredUndecimated++;
                        else if (code == RefusedVertices)
                        {
                            settled = false;
                            Retake(job, limit);
                            if (!Cluster(source)) job.Unstored++;
                        }
                        else job.Unstored++;

                        return;
                    }

                    // 4. the source clustered to its limit, on a worker
                    if (!Cluster(source)) job.Unstored++;
                    return;
                }

                // Past the hard cap: bounded - as it is, else coarse, else abandoned.
                if (fits && Store(source) == Stored)
                {
                    job.StoredUndecimated++;
                    return;
                }

                if (EnqueueCoarse(job, candidate)) return;

                job.AbandonedAtHard++;
            }
            finally
            {
                if (!settled) job.Ledger.Settle(limit, 0);
                if (candidate.AsDetail && state != null && committed) state.DetailCommitted++;
            }
        }

        /// <summary>Takes a limit back as pending after a store that settled it was refused, so the cluster
        /// flight that follows owns it again. Always fits: the same amount was just returned.</summary>
        /// <param name="job">The build.</param>
        /// <param name="limit">The limit.</param>
        private static void Retake(Job job, int limit) => job.Ledger.Pending += limit;

        /// <summary>
        /// A readable mesh's arrays out of Unity, into job.Captured: its vertices in one step and each of its
        /// submeshes' triangles in a step of its own, yielding between them whenever the frame's budget is
        /// spent - so a million-triangle source is several short frames rather than one long one. Nothing
        /// is decoded or transformed here; that is the worker's.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static IEnumerator CaptureReadable(Job job, Candidate candidate)
        {
            var mesh = candidate.Mesh;
            Vector3[] local = null;

            // One call, however large the mesh - it cannot be split - so its milliseconds are logged.
            var clock = Stopwatch.StartNew();
            Step(job, "a readable building", () => local = mesh.vertices);
            job.PeakReadableMs = Math.Max(job.PeakReadableMs, clock.Elapsed.TotalMilliseconds);

            if (local == null || local.Length < 3) yield break;

            var parts = new List<int[]>();

            for (var s = candidate.SubFirst; s < candidate.SubEnd; s++)
            {
                if (FrameSpent(job))
                {
                    yield return null;
                    job.FrameClock.Restart();
                }

                var sub = s;

                // GetTriangles applies the submesh's base vertex for us, so these index the array above.
                clock.Restart();
                Step(job, "a readable building's triangles", () =>
                {
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) return;

                    var indices = mesh.GetTriangles(sub);
                    if (indices != null && indices.Length >= 3) parts.Add(indices);
                });
                job.PeakReadableMs = Math.Max(job.PeakReadableMs, clock.Elapsed.TotalMilliseconds);
            }

            if (parts.Count == 0) yield break;

            Step(job, "a readable building's placement", () =>
            {
                var source = NewSource(job, candidate);
                source.Local = local;
                source.Parts = parts;
                job.Captured = source;
            });
        }

        /// <summary>A source with everything the worker needs that only the main thread may read: the
        /// transforms, the renderer's bounds as plain floats, the extent, the size.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static Source NewSource(Job job, Candidate candidate)
        {
            var file = job.File;
            var min = candidate.Bounds.min;
            var max = candidate.Bounds.max;

            return new Source
            {
                SourceTriangles = candidate.SourceTriangles,
                Matrix = candidate.Matrix,
                Fallback = candidate.Fallback,
                BoxMin = new Vector3(min.x - PlausibleSlack, min.y - PlausibleSlack, min.z - PlausibleSlack),
                BoxMax = new Vector3(max.x + PlausibleSlack, max.y + PlausibleSlack, max.z + PlausibleSlack),
                MinX = (float)(file.MinX - VertexSlack),
                MaxX = (float)(file.MaxX + VertexSlack),
                MinZ = (float)(file.MinZ - VertexSlack),
                MaxZ = (float)(file.MaxZ + VertexSlack),
            };
        }

        /// <summary>A GPU readback's bytes as a source: the layout the decode needs - vertex count, stride,
        /// offset, format, index width and this renderer's submesh ranges - read off the mesh here, on the
        /// main thread, so the worker decodes from plain numbers. Null for a position format this build does
        /// not decode (a wrong decode would put a building in the sea).</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="vertexBytes">The position stream's bytes.</param>
        /// <param name="indexBytes">The index buffer's bytes.</param>
        private static Source GpuSource(Job job, Candidate candidate, byte[] vertexBytes, byte[] indexBytes)
        {
            var mesh = candidate.Mesh;
            int size;

            switch (candidate.Format)
            {
                case VertexAttributeFormat.Float32:
                    size = 4;
                    break;
                case VertexAttributeFormat.Float16:
                    size = 2;
                    break;
                default:
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: a building's positions are {candidate.Format}, which the mesh builder does " +
                        "not decode - it is left out.");
                    return null;
            }

            var starts = new List<int>();
            var counts = new List<int>();
            var bases = new List<int>();

            for (var s = candidate.SubFirst; s < candidate.SubEnd; s++)
            {
                var sub = mesh.GetSubMesh(s);
                if (sub.topology != MeshTopology.Triangles) continue;

                starts.Add(sub.indexStart);
                counts.Add(sub.indexCount);
                bases.Add(sub.baseVertex);
            }

            var source = NewSource(job, candidate);

            source.FromGpu = true;
            source.VertexBytes = vertexBytes;
            source.IndexBytes = indexBytes;
            source.VertexCount = mesh.vertexCount;
            source.Offset = candidate.Offset;
            source.Stride = candidate.Stride;
            source.Dimension = candidate.Dimension;
            source.FormatSize = size;
            source.Wide = mesh.indexFormat == IndexFormat.UInt32;
            source.SubStart = starts.ToArray();
            source.SubCount = counts.ToArray();
            source.SubBase = bases.ToArray();

            return source;
        }

        /// <summary>
        /// The WORKER's part, on plain arrays and nothing else - no Unity call, no job field: decode (a GPU
        /// source), choose the transform and place the vertices in world space (<see cref="Place"/>), then
        /// reduce. A source inside its limit is handed back as it is. One over it is decimated to its target
        /// with its limit as the HARD limit, in the lane's reused workspace, when decimation is still allowed
        /// and the source is no larger than <see cref="MaxDecimatedSource"/>; a decimation that times out or
        /// cannot reach the limit is not used. Whatever is not reduced comes back as the placed source.
        /// </summary>
        /// <param name="source">The source.</param>
        /// <param name="lane">This worker's scratch, not shared while it runs.</param>
        private static Outcome Process(Source source, Lane lane)
        {
            var outcome = new Outcome { SourceTriangles = source.SourceTriangles };
            var triangles = lane.Indices;
            triangles.Clear();

            Vector3[] local;

            if (source.Local != null)
            {
                local = source.Local;
                foreach (var part in source.Parts) triangles.AddRange(part);
            }
            else if (!Decode(source, triangles, out local))
            {
                outcome.Undecodable = true;
                return outcome;
            }
            else
            {
                outcome.DecodedBytes = local.Length * 12L;
            }

            // The arrays out of Unity are not needed past this point; the source object lives until the
            // flight is applied, so they are let go of here.
            source.Local = null;
            source.Parts = null;
            source.VertexBytes = null;
            source.IndexBytes = null;

            var world = Place(source, local, triangles, lane, outcome);
            if (world == null) return outcome;

            outcome.SourceTriangles = world.Triangles;

            if (world.Triangles <= source.Limit)
            {
                outcome.Mesh = world;
                return outcome;
            }

            if (source.MayDecimate && world.Triangles <= MaxDecimatedSource)
            {
                var result = MeshDecimator.DecimateWith(world.P, world.T, source.Target, source.Limit,
                    DecimateBuildingMs, true, lane.Decimator);

                outcome.WorkerMs = result.Milliseconds;
                outcome.WorkspaceBytes = result.WorkspaceBytes;
                outcome.TimedOut = result.TimedOut;
                outcome.OverLimit = result.OverLimit;

                if (!result.TimedOut && result.Triangles != null && result.Triangles.Length >= 3 &&
                    result.Triangles.Length / 3 <= source.Limit)
                {
                    outcome.Mesh = new WorldMesh { P = result.Positions, T = result.Triangles, Mirrored = world.Mirrored };
                    outcome.Decimated = true;
                    return outcome;
                }
            }

            // Not clustered here (M4): the main thread may take the coarse level or store it as it is, and
            // a cluster is started as its own flight only when that path is the one chosen.
            outcome.Source = world;

            return outcome;
        }

        /// <summary>A GPU source's vertex and index bytes as the (positions, triangles) pair the readable
        /// path has. False when the buffers do not hold what the layout says - fewer bytes than the vertex
        /// count needs, or no triangles at all. Worker-safe: plain arrays and numbers only.</summary>
        /// <param name="source">The source.</param>
        /// <param name="triangles">Filled with the indices, base vertex applied.</param>
        /// <param name="local">The decoded positions.</param>
        private static bool Decode(Source source, List<int> triangles, out Vector3[] local)
        {
            local = null;

            var stride = source.Stride;
            var count = source.VertexCount;
            var size = source.FormatSize;
            var vertexBytes = source.VertexBytes;
            var indexBytes = source.IndexBytes;

            if (vertexBytes == null || indexBytes == null) return false;
            if (source.Dimension < 3 || stride <= 0 || count < 3 || (size != 2 && size != 4)) return false;

            var need = (long)source.Offset + (long)(count - 1) * stride + 3L * size;
            if (need > vertexBytes.Length) return false;

            local = new Vector3[count];

            for (var v = 0; v < count; v++)
            {
                var at = source.Offset + v * stride;

                local[v] = size == 4
                    ? new Vector3(
                        BitConverter.ToSingle(vertexBytes, at),
                        BitConverter.ToSingle(vertexBytes, at + 4),
                        BitConverter.ToSingle(vertexBytes, at + 8))
                    : new Vector3(
                        Half(BitConverter.ToUInt16(vertexBytes, at)),
                        Half(BitConverter.ToUInt16(vertexBytes, at + 2)),
                        Half(BitConverter.ToUInt16(vertexBytes, at + 4)));
            }

            var indexSize = source.Wide ? 4 : 2;

            for (var s = 0; s < source.SubStart.Length; s++)
            {
                var start = (long)source.SubStart[s];
                var indices = (long)source.SubCount[s];

                if (start < 0 || indices < 0) continue;
                if ((start + indices) * indexSize > indexBytes.Length) continue;

                for (var i = 0L; i < indices; i++)
                {
                    var at = (int)((start + i) * indexSize);

                    // baseVertex by hand: the index buffer holds a submesh-relative index and the
                    // readable path's GetTriangles is the only thing that adds it for you.
                    var index = (source.Wide
                        ? (int)BitConverter.ToUInt32(indexBytes, at)
                        : BitConverter.ToUInt16(indexBytes, at)) + source.SubBase[s];

                    triangles.Add(index);
                }
            }

            return triangles.Count >= 3;
        }

        /// <summary>An IEEE half as a float, written out because System.Half does not exist on
        /// netstandard2.1. Math.Pow, not Mathf's - this runs on a worker.</summary>
        /// <param name="bits">The sixteen bits.</param>
        private static float Half(ushort bits)
        {
            var sign = (bits >> 15) & 1;
            var exponent = (bits >> 10) & 0x1F;
            var mantissa = bits & 0x3FF;

            float value;

            if (exponent == 0) value = mantissa == 0 ? 0f : mantissa * 5.9604645e-8f;
            else if (exponent == 31) value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            else value = (float)((1d + mantissa / 1024d) * Math.Pow(2d, exponent - 15));

            return sign == 1 ? -value : value;
        }

        // --- turning a mesh into a building ------------------------------------------------------------------

        /// <summary>
        /// One mesh, in its own local space, as a WORLD-SPACE source ready to store or decimate: its corners
        /// through the chosen transform, its winding the game's, and nothing outside the extent. Runs on the
        /// worker, so it touches no Unity object and calls no Unity native method: the transform is applied
        /// by hand from the matrix's fields, its determinant is the 3x3 one written out, and the bounds test
        /// is against plain floats.
        ///
        /// A triangle is DROPPED when any of its vertices is not finite or sits more than
        /// <see cref="VertexSlack"/> outside the extent - the quantisation has no room outside the extent. A
        /// vertex is kept the first time a kept triangle uses it. WHICH transform: <see cref="Source.Matrix"/>
        /// held to the renderer's own world bounds on a sample of the vertices this building uses, then
        /// <see cref="Source.Fallback"/>; a building no transform places inside its own bounds is refused
        /// (counted as implausible). WHICH WAY ROUND: a transform with a negative determinant mirrors the
        /// mesh, so its triangles have their second and third corners swapped here, before anything else
        /// sees them; the decimator never reorders a face, so the winding it hands back is this one.
        /// </summary>
        /// <param name="source">The source, for its transforms, bounds and extent.</param>
        /// <param name="local">Its vertex positions, in the mesh's own space.</param>
        /// <param name="triangles">Its triangle indices into <paramref name="local"/>.</param>
        /// <param name="lane">The worker's reused lists.</param>
        /// <param name="outcome">Where the implausible flag and the dropped count go.</param>
        private static WorldMesh Place(Source source, Vector3[] local, List<int> triangles, Lane lane, Outcome outcome)
        {
            if (local == null || triangles == null || triangles.Count < 3) return null;

            if (!ChooseTransform(source, local, triangles, out var matrix))
            {
                outcome.Implausible = true;
                return null;
            }

            var mirrored = Determinant3(matrix) < 0f;

            lane.P.Clear();
            lane.T.Clear();

            if (lane.Remap.Length < local.Length) lane.Remap = new int[local.Length];

            var map = lane.Remap;
            for (var i = 0; i < local.Length; i++) map[i] = -1;

            var dropped = 0;

            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var a = triangles[t];
                var b = mirrored ? triangles[t + 2] : triangles[t + 1];
                var c = mirrored ? triangles[t + 1] : triangles[t + 2];

                if (a < 0 || b < 0 || c < 0 || a >= local.Length || b >= local.Length || c >= local.Length)
                {
                    dropped++;
                    continue;
                }

                var pa = Mul(matrix, local[a]);
                var pb = Mul(matrix, local[b]);
                var pc = Mul(matrix, local[c]);

                if (!Inside(pa, source) || !Inside(pb, source) || !Inside(pc, source))
                {
                    dropped++;
                    continue;
                }

                lane.T.Add(Keep(lane, map, a, pa));
                lane.T.Add(Keep(lane, map, b, pb));
                lane.T.Add(Keep(lane, map, c, pc));
            }

            outcome.Dropped += dropped;

            if (lane.T.Count < 3) return null;

            return new WorldMesh { P = lane.P.ToArray(), T = lane.T.ToArray(), Mirrored = mirrored };
        }

        /// <summary>One vertex kept in the world-space source, or the index it already has.</summary>
        /// <param name="lane">The worker's lists.</param>
        /// <param name="map">Local index to kept index, -1 for a vertex not yet kept.</param>
        /// <param name="index">The local vertex index.</param>
        /// <param name="world">Its world position.</param>
        private static int Keep(Lane lane, int[] map, int index, Vector3 world)
        {
            if (map[index] >= 0) return map[index];

            var at = lane.P.Count / 3;

            lane.P.Add(world.x);
            lane.P.Add(world.y);
            lane.P.Add(world.z);

            map[index] = at;

            return at;
        }

        /// <summary>A point through a transform's upper 3x4, from its fields - worker-safe.</summary>
        /// <param name="m">The transform.</param>
        /// <param name="p">The point.</param>
        private static Vector3 Mul(Matrix4x4 m, Vector3 p) => new Vector3(
            m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
            m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
            m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);

        /// <summary>The determinant of a transform's 3x3 part - its sign is whether it mirrors. Written out
        /// because Matrix4x4.determinant is a native call and this runs on a worker.</summary>
        /// <param name="m">The transform.</param>
        internal static float Determinant3(Matrix4x4 m) =>
            m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) -
            m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) +
            m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        /// <summary>Picks the transform that puts this building where its renderer says it is: the source's
        /// <see cref="Source.Matrix"/>, or its <see cref="Source.Fallback"/> when the first does not. False
        /// when neither does - the building is then refused.</summary>
        /// <param name="source">The source.</param>
        /// <param name="local">Its vertices, in the mesh's space.</param>
        /// <param name="triangles">The indices this building uses - the sample is drawn from these, not from
        /// the whole vertex array, because a static batch's array is every building in the batch.</param>
        /// <param name="matrix">The transform to use.</param>
        private static bool ChooseTransform(Source source, Vector3[] local, List<int> triangles, out Matrix4x4 matrix)
        {
            matrix = source.Matrix;

            if (Plausible(source.Matrix, source, local, triangles)) return true;

            if (source.Fallback.HasValue && Plausible(source.Fallback.Value, source, local, triangles))
            {
                matrix = source.Fallback.Value;
                return true;
            }

            return false;
        }

        /// <summary>Whether at least <see cref="PlausibleShare"/> of a sample of the building's vertices land
        /// inside the renderer's world bounds (already widened by <see cref="PlausibleSlack"/>) under this
        /// transform. The bounds are Unity's own, computed from where it DRAWS the renderer, so a transform
        /// that disagrees with them is the wrong one.</summary>
        /// <param name="matrix">The transform being tested.</param>
        /// <param name="source">The source, for its bounds.</param>
        /// <param name="local">The mesh's vertices.</param>
        /// <param name="triangles">The indices this building uses.</param>
        private static bool Plausible(Matrix4x4 matrix, Source source, Vector3[] local, List<int> triangles)
        {
            var step = Math.Max(1, triangles.Count / PlausibleSamples);
            var tried = 0;
            var inside = 0;

            for (var i = 0; i < triangles.Count; i += step)
            {
                var index = triangles[i];
                if (index < 0 || index >= local.Length) continue;

                tried++;

                var p = Mul(matrix, local[index]);

                if (p.x >= source.BoxMin.x && p.x <= source.BoxMax.x &&
                    p.y >= source.BoxMin.y && p.y <= source.BoxMax.y &&
                    p.z >= source.BoxMin.z && p.z <= source.BoxMax.z)
                    inside++;
            }

            return tried > 0 && inside >= tried * PlausibleShare;
        }

        /// <summary>Whether a world position is finite and inside the extent with <see cref="VertexSlack"/>
        /// of slack.</summary>
        /// <param name="p">The world position.</param>
        /// <param name="source">The source, for the extent.</param>
        private static bool Inside(Vector3 p, Source source)
        {
            if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z)) return false;

            return p.x >= source.MinX && p.x <= source.MaxX && p.z >= source.MinZ && p.z <= source.MaxZ;
        }

        /// <summary>
        /// A mesh the GPU alone holds, into job.Captured as bytes, through <see cref="AsyncGPUReadback"/> on the buffers
        /// <see cref="Mesh.GetVertexBuffer"/> and <see cref="Mesh.GetIndexBuffer"/> hand over AS THEY
        /// ARE.
        ///
        /// Nothing here assigns <c>vertexBufferTarget</c> or <c>indexBufferTarget</c> and nothing
        /// calls <c>GetData</c>: that pair killed the process natively on 2026-09-22 (see this class's
        /// summary), and the readback below is the one path phase 3-0 proved returns real world
        /// positions - Big Red's non-readable shell in 8 ms and two frames. Both requests are in
        /// flight together, they are frame-capped, and a request still pending when the cap runs out
        /// is WAITED FOR before its buffer is released, because that buffer is where it is writing.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static IEnumerator ReadFromGpu(Job job, Candidate candidate)
        {
            GraphicsBuffer vertexBuffer = null;
            GraphicsBuffer indexBuffer = null;

            var vertexRequest = default(AsyncGPUReadbackRequest);
            var indexRequest = default(AsyncGPUReadbackRequest);

            // ONE FLAG PER REQUEST, and each is set the instant that request is accepted. A single
            // "asked" set after both calls is a native crash waiting to happen: if the second Request
            // throws, the first is already in flight, the flag is still false, and the finally below
            // would release a buffer the GPU is writing into - the same family of fault as writing a
            // buffer target, and the same lack of a managed exception to catch afterwards.
            var askedVertex = false;
            var askedIndex = false;
            var settled = false;
            byte[] vertexBytes = null;
            byte[] indexBytes = null;

            try
            {
                Step(job, "asking the GPU for a building", () =>
                {
                    if (candidate.Stream < 0) return;

                    vertexBuffer = candidate.Mesh.GetVertexBuffer(candidate.Stream);
                    if (vertexBuffer == null) return;

                    indexBuffer = candidate.Mesh.GetIndexBuffer();
                    if (indexBuffer == null) return;

                    vertexRequest = AsyncGPUReadback.Request(vertexBuffer);
                    askedVertex = true;

                    indexRequest = AsyncGPUReadback.Request(indexBuffer);
                    askedIndex = true;
                });

                if (!askedVertex || !askedIndex)
                {
                    job.Unreadable++;
                    yield break;
                }

                var frames = 0;

                while (frames < ReadbackFrameCap)
                {
                    var ready = false;

                    if (!Step(job, "a readback's state", () => ready = vertexRequest.done && indexRequest.done))
                        break;

                    if (ready)
                    {
                        settled = true;
                        break;
                    }

                    yield return null;
                    frames++;
                }

                if (!settled)
                {
                    job.Unreadable++;
                    yield break;
                }

                Step(job, "a building off the GPU", () =>
                {
                    if (vertexRequest.hasError || indexRequest.hasError) return;

                    vertexBytes = Copy(vertexRequest);
                    indexBytes = Copy(indexRequest);

                    if (vertexBytes != null && vertexBytes.Length > job.PeakReadbackBytes)
                        job.PeakReadbackBytes = vertexBytes.Length;
                });
            }
            finally
            {
                // A readback that never finished is still IN FLIGHT and its destination is the buffer
                // released below, so it is waited for rather than abandoned - PER REQUEST, because the
                // two are accepted one at a time and either one of them can be the only one alive.
                if (!settled)
                {
                    if (askedVertex) Await(ref vertexRequest, "vertex");
                    if (askedIndex) Await(ref indexRequest, "index");
                }

                Release(vertexBuffer);
                Release(indexBuffer);
            }

            if (vertexBytes == null || indexBytes == null)
            {
                job.Unreadable++;
                yield break;
            }

            // The bytes and the layout to decode them with, into job.Captured; the decode is the worker's.
            Step(job, "a building's layout", () => job.Captured = GpuSource(job, candidate, vertexBytes, indexBytes));

            if (job.Captured == null) job.Unreadable++;
        }

        /// <summary>Waits for one accepted readback before its buffer is released. Guarded on its own:
        /// a wait on a broken request may throw, and the buffer has to be released either way - but it
        /// may only be released once nothing is writing into it.</summary>
        /// <param name="request">The request, by reference so the wait is on the request itself.</param>
        /// <param name="which">"vertex" or "index", for the line.</param>
        private static void Await(ref AsyncGPUReadbackRequest request, string which)
        {
            try
            {
                request.WaitForCompletion();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: a pending {which} readback would not be waited for ({ex.GetType().Name}: " +
                    $"{ex.Message}) - its buffer is released anyway.");
            }
        }

        /// <summary>A finished readback's bytes as a managed array.</summary>
        /// <param name="request">The finished request.</param>
        private static byte[] Copy(AsyncGPUReadbackRequest request)
        {
            var data = request.GetData<byte>();
            var bytes = new byte[data.Length];

            data.CopyTo(bytes);

            return bytes;
        }

        /// <summary>Releases one GraphicsBuffer, guarded on its own so one refusing cannot keep the
        /// other from going back.</summary>
        /// <param name="buffer">The buffer, or null.</param>
        private static void Release(GraphicsBuffer buffer)
        {
            if (buffer == null) return;

            try
            {
                buffer.Release();
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogDebug(
                    $"QuestTree: a mesh buffer would not release ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        /// <summary>
        /// A world-space mesh - a source stored as it is, or a decimation's result - into the file: x and
        /// z quantised at once, y held in metres until the y range is known (QuantiseBuildings), the stable
        /// key, and the running totals. Everything the format refuses is checked here rather than at the
        /// write, because the write is one file for the whole map: a building over a cap is DROPPED, or one
        /// bad renderer costs the capture its entire mesh. The TRIANGLE budget is the ledger's (Apply). The two per-building rules Write also enforces -
        /// no vertex height of <see cref="MapMeshFile.NoHit"/> and no index past the vertex count - hold
        /// by construction: every position was finite when Place kept it (the decimator's are
        /// combinations of finite ones), and every index is into the arrays handed over with it.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate it is for.</param>
        /// <param name="world">The mesh, in world space.</param>
        private static int StoreWorld(Job job, Candidate candidate, WorldMesh world)
        {
            if (world?.P == null || world.T == null) return Refused;

            var vertices = world.P.Length / 3;
            var kept = world.T.Length / 3;
            var file = job.File;

            if (kept == 0 || vertices == 0) return Refused;

            if (vertices > MapMeshFile.MaxVerticesPerBuilding)
            {
                job.RefusedBuildingVertices++;
                return RefusedVertices;
            }

            // The format's building cap (M1): stop, rather than build a file Write refuses whole.
            if (file.Buildings.Count >= MapMeshFile.MaxBuildings)
            {
                job.Stopped = true;
                job.StoppedWhy = $"the format's cap of {N(MapMeshFile.MaxBuildings)} buildings was reached";
                return Refused;
            }

            // The ledger admitted this building and Apply checked any overshoot against the headroom, so
            // this cannot trip - it is the last line under the map's cap, not the budget itself.
            if (job.Triangles + kept > MaxBuildingTriangles)
            {
                job.OverBudget++;
                return Refused;
            }

            if (job.Vertices + vertices > MapMeshFile.MaxVerticesTotal)
            {
                job.RefusedFileVertices++;
                return RefusedVertices;
            }

            var x = new ushort[vertices];
            var z = new ushort[vertices];
            var heights = new float[vertices];
            var heightSum = 0d;

            for (var v = 0; v < vertices; v++)
            {
                x[v] = file.QuantiseX(world.P[v * 3]);
                heights[v] = world.P[v * 3 + 1];
                z[v] = file.QuantiseZ(world.P[v * 3 + 2]);

                heightSum += heights[v];

                if (heights[v] < job.VertexLow) job.VertexLow = heights[v];
                if (heights[v] > job.VertexHigh) job.VertexHigh = heights[v];
            }

            var indices = new uint[world.T.Length];
            for (var i = 0; i < indices.Length; i++) indices[i] = (uint)world.T[i];

            // Level and Y are filled by QuantiseBuildings once the y range and the file's bands exist; Y
            // is an empty array until then so nothing can mistake it for a quantised one.
            job.File.Buildings.Add(new MapMeshFile.Building
            {
                Key = MapMeshFile.Building.KeyFor(HierarchyPath(candidate.Renderer.transform), candidate.Bounds.center),
                Level = 0,
                X = x,
                Y = new ushort[0],
                Z = z,
                Indices = indices
            });

            job.PendingY.Add(heights);
            job.Centroids.Add((float)(heightSum / vertices));

            job.Kept++;
            job.Triangles += kept;
            job.Vertices += vertices;
            job.Emitted.Add(candidate.Renderer);

            if (world.Mirrored) job.Mirrored++;

            return Stored;
        }

        /// <summary>Quantises the kept buildings' heights over the file's y range, now that there is one, and
        /// gives each its band, now that the bands are in the file - <see cref="QuantisePerFrameVertices"/>
        /// vertices a call (M3: 3-9 million in one step was 15-45 ms). The height arrays in metres are dropped
        /// as they are used.</summary>
        /// <param name="job">The build.</param>
        private static void QuantiseBuildings(Job job)
        {
            var file = job.File;
            var done = 0L;

            while (job.QuantisedUpTo < file.Buildings.Count && job.QuantisedUpTo < job.PendingY.Count &&
                   done < QuantisePerFrameVertices)
            {
                var i = job.QuantisedUpTo++;
                var metres = job.PendingY[i];
                var codes = new ushort[metres.Length];

                for (var v = 0; v < metres.Length; v++) codes[v] = file.QuantiseHeight(metres[v]);

                file.Buildings[i].Y = codes;
                file.Buildings[i].Level = LevelFor(job, job.Centroids[i]);

                job.PendingY[i] = null;
                done += metres.Length;
            }

            // Nothing left that this loop can reach (more heights than buildings would be a bug): finished.
            if (job.QuantisedUpTo >= file.Buildings.Count) job.QuantisedUpTo = job.PendingY.Count;
        }

        /// <summary>The band a building is drawn with: the one whose height range holds its centroid,
        /// or the nearest when none does. Never a level no band has - Write refuses that, and the
        /// floor peel has nothing to draw it with.</summary>
        /// <param name="job">The build.</param>
        /// <param name="centroid">The building's mean vertex height.</param>
        private static int LevelFor(Job job, float centroid)
        {
            var bands = job.File.Bands;
            if (bands.Count == 0) return 0;

            var best = bands[0].Level;
            var bestGap = float.PositiveInfinity;

            foreach (var work in job.Bands)
            {
                var band = work.Source;

                // Only a level the FILE has. A band whose grid could not be finished never reached
                // file.Bands, and a building pointed at it would be the one thing that makes Write
                // refuse the whole mesh.
                if (job.File.Band(band.Level) == null) continue;

                if (centroid >= band.MinY && centroid <= band.MaxY) return band.Level;

                var gap = centroid < band.MinY ? band.MinY - centroid : centroid - band.MaxY;

                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = band.Level;
                }
            }

            return best;
        }

        /// <summary>A transform's path in the scene, parents first - half of a building's stable key.
        /// Depth-capped, because a cycle in a hierarchy is not this method's problem to solve.</summary>
        /// <param name="transform">The renderer's transform.</param>
        private static string HierarchyPath(Transform transform)
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

        // --- the log lines and the result ---------------------------------------------------------------------

        /// <summary>The building phase's one log line: what was kept, how it was reduced, what took another
        /// path and what was cut.</summary>
        /// <param name="job">The build.</param>
        private static void ReportBuildings(Job job)
        {
            job.BuildingClock.Stop();

            var f1 = CultureInfo.InvariantCulture;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: buildings for {job.Request.Map} - {N(job.Kept)} of {N(job.Candidates.Count)} " +
                $"candidates kept, {N(job.Triangles)} triangles stored of the {N(MaxBuildingTriangles)} cap; " +
                $"{Millions(job.SourceDecimated)} source triangles decimated ({N(job.Decimated)} buildings, " +
                $"{AreaBudget.TrianglesPerSquareMetre.ToString("0.0", f1)}/m2, scaled x{job.BudgetScale.ToString("0.00", f1)}) in " +
                $"{(job.WorkerMs / 1000d).ToString("0.0", f1)} s on up to {N(job.PeakWorkers)} worker(s), peak workspace " +
                $"{(job.PeakWorkspaceBytes / (1024d * 1024d)).ToString("0.0", f1)} MB; " +
                $"{N(job.FellBack)} building(s) fell back to the game's LOD, {N(job.StoredUndecimated)} stored " +
                $"undecimated, {N(job.ClusteredStored)} clustered" +
                (job.TimedOut + job.OverLimit > 0
                    ? $" ({N(job.TimedOut)} decimation(s) out of time, {N(job.OverLimit)} over their limit)"
                    : "") +
                (job.DecimationStopped ? $"; decimation stopped at {job.DecimationStoppedWhy}" : "") +
                $"; {N(job.InputGuarded)} over the {Millions(MaxSourceTriangles)} source guard, " +
                $"{N(job.Oversized)} oversized, {N(job.GpuRead)} read back off the GPU, " +
                $"{N(job.Unreadable)} unreadable, {N(job.ImpostorSkipped)} impostor LODs skipped, " +
                $"{N(job.ThinSkipped)} thin LODs skipped, " +
                $"{job.BuildingClock.Elapsed.TotalSeconds.ToString("0.0", f1)} s (soft cap {N(job.SoftSeconds)} s, hard " +
                $"{N(job.HardSeconds)} s, relief {job.ReliefSeconds.ToString("0.0", f1)} s), longest readable read " +
                $"{job.PeakReadableMs.ToString("0", f1)} ms." +
                (job.PastHard ? $" Hard cap reached with {N(job.CutAtHardCap)} candidate(s) not looked at." : "") +
                (job.Abandoned + job.AbandonedAtHard > 0
                    ? $" {N(job.Abandoned)} coarse fallback(s) abandoned at the drain deadline, a cap or an abort; " +
                      $"{N(job.AbandonedAtHard)} building(s) past the hard cap had no as-is room and no coarse level."
                    : "") +
                (job.SwitchedMidRead > 0 ? $" {N(job.SwitchedMidRead)} detail read(s) discarded - the group switched to coarse." : "") +
                (job.RefusedBuildingVertices + job.RefusedFileVertices > 0
                    ? $" {N(job.RefusedBuildingVertices)} over the per-building vertex cap, {N(job.RefusedFileVertices)} over " +
                      "the file's (each tried as a cluster)."
                    : "") +
                (job.ClusterTimedOut > 0 ? $" {N(job.ClusterTimedOut)} cluster(s) out of time." : "") +
                (job.Request.Abort ? " ABORTED by the capture's watchdog - finished with what was stored." : "") +
                (job.OverBudget > 0
                    ? $" {N(job.OverBudget)} did not fit the {N(MaxBuildingTriangles)}-triangle budget."
                    : "") +
                (job.Failed + job.Unstored > 0
                    ? $" {N(job.Failed)} worker(s) failed, {N(job.Unstored)} building(s) had no path left."
                    : "") +
                (job.Implausible > 0
                    ? $" {N(job.Implausible)} refused because no transform put them inside their own bounds."
                    : "") +
                (job.Mirrored > 0 ? $" {N(job.Mirrored)} mirrored, stored with their winding swapped." : "") +
                (job.Stopped ? $" Stopped early: {job.StoppedWhy}." : "") +
                (job.DroppedTriangles > 0
                    ? $" {N(job.DroppedTriangles)} triangle(s) reached outside the extent and were dropped."
                    : ""));
        }

        /// <summary>Binds the file, fills the result's counts and says what the build cost in
        /// memory.</summary>
        /// <param name="job">The build.</param>
        /// <param name="result">The result to fill.</param>
        private static void Finish(Job job, Result result)
        {
            var file = job.File;

            // A file with no relief at all is not worth writing, whatever buildings came back: the
            // viewer draws buildings ON the ground, tools/check-capture.py holds a mesh's band levels
            // against the meta's floors, and "no bands" would pass every cap and fail that.
            if (file.Bands.Count == 0)
            {
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: no 3D mesh was written for {job.Request.Map} - not one band's relief could be " +
                    "finished. The warnings above say why.");
                return;
            }

            // A building whose heights were never quantised - QuantiseBuildings failed part way - has an
            // empty Y beside a full X, which Write refuses for the WHOLE file. Dropping those keeps the
            // relief and every building that did finish; it is the same "one bad renderer must not cost
            // the map its mesh" rule StoreWorld follows.
            var unfinished = file.Buildings.RemoveAll(b => b.Y == null || b.Y.Length != b.X.Length);

            if (unfinished > 0)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {N(unfinished)} building(s) of {job.Request.Map} never had their heights " +
                    "quantised and were left out; the relief and the rest are written.");

            // Bands and buildings were filled by hand, so they are not bound to the file whose ranges
            // they are quantised over until this runs. Write binds too; a caller that reads the file
            // before writing it would otherwise get an InvalidOperationException.
            file.Bind();

            result.File = file;
            result.Cells = file.CellCount();
            result.Triangles = file.TriangleCount();
            result.Buildings = file.Buildings.Count;

            var relief = 0L;

            // Two bytes of height and one of distance a cell, plus the same per-object allowance
            // ApproximateBytes makes, so the two numbers below add up to what it reports.
            foreach (var band in file.Bands) relief += band.CellCount * 3L + 64L;

            result.ReliefBytes = relief;
            result.BuildingBytes = file.ApproximateBytes() - relief;

            // The peak is the largest things this build held at once: every band's quantised grids and
            // EVERY band's float heights beside them (Prepare allocates them all up front, and they live
            // until their band is finished - which is now after the buildings), the kept buildings'
            // heights in metres, the scene's renderer array, the candidate list and its LOD decisions,
            // the largest single readback and its decoded vertices. What it cannot see is the arrays
            // Unity hands back from mesh.vertices and GetTriangles, which are the game's allocations,
            // not this build's.
            var floats = 0L;

            foreach (var band in file.Bands)
                floats += band.CellCount * 4L;

            floats += job.Vertices * 4L;

            // A Candidate is a dozen fields, two matrices and a header; the LOD cache is one set per
            // group plus one entry per renderer in it.
            var candidates = job.Candidates.Count * 240L + job.Groups.Count * 96L + job.LevelRenderers * 16L + job.Emitted.Count * 16L;
            // The pipeline's measured peak - flights (sources, worker lists, decimator workspaces) plus the
            // pooled lanes - and the largest decoded source.
            var buffers = job.PeakPipelineBytes + job.PeakDecodedBytes;

            var peak = relief + floats + job.RendererCount * 8L + job.PeakReadbackBytes +
                       result.BuildingBytes + candidates + buffers;

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {job.Request.Map}'s mesh holds {file.Describe()}; peak working set about " +
                $"{(peak / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB " +
                $"(grids {N(relief + floats)} B, {N(job.RendererCount)} renderer(s) scanned, " +
                $"{N(job.Candidates.Count)} candidate(s) held, stored buildings {N(result.BuildingBytes)} B, " +
                $"pipeline peak {N(job.PeakPipelineBytes)} B over up to {N(job.PeakWorkers)} worker(s), " +
                $"{N(job.LanesDropped)} lane(s) dropped for size, largest readback " +
                $"{(job.PeakReadbackBytes / 1024d).ToString("0", CultureInfo.InvariantCulture)} KB), " +
                "excluding the arrays Unity allocates for mesh.vertices and GetTriangles.");
        }

        // --- small helpers -------------------------------------------------------------------------------------

        /// <summary>One guarded step of the build. The pattern the whole class is written in, because
        /// C# forbids a yield inside a try that has a catch and because a mesh is an upgrade to a
        /// capture and may never take one down: a step that throws is logged and the build goes on
        /// with what it has.</summary>
        /// <param name="job">The build, for the log.</param>
        /// <param name="what">What is being done, for the log.</param>
        /// <param name="work">The step.</param>
        private static bool Step(Job job, string what, Action work)
        {
            try
            {
                work();

                return true;
            }
            catch (Exception ex)
            {
                job.Note(what, ex);

                return false;
            }
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>A count in millions, "6.3 M", for the building line's source triangles.</summary>
        /// <param name="value">The count.</param>
        private static string Millions(long value) =>
            (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " M";

        /// <summary>A number with thousands separators, the way every count in this mod's log lines is
        /// written.</summary>
        /// <param name="value">The number.</param>
        private static string N(double value) =>
            value.ToString("#,##0", CultureInfo.InvariantCulture);

        /// <summary>A whole-number percentage, or 0 when there was nothing to take a share of.</summary>
        /// <param name="part">The part.</param>
        /// <param name="whole">The whole.</param>
        private static string Pct(long part, long whole) =>
            whole <= 0
                ? "0 %"
                : (part * 100d / whole).ToString("0", CultureInfo.InvariantCulture) + " %";

        private static string F(float v) =>
            IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
    }
    /// <summary>
    /// The arithmetic of the four oblique side views (plan, stage U - the contract is frozen): which
    /// way each camera looks, and where a world point lands in each picture. MapCapture renders by it,
    /// the viewer and tools/check-capture.py recompute it, and the harness holds it to real numbers.
    ///
    /// Why it lives HERE rather than in MapCapture: MapCapture is a MonoBehaviour, so its type cannot
    /// even be loaded without UnityEngine, and a check that has to run the game to run is a check that
    /// is not run. This class is doubles and arrays and nothing else - the harness calls it on the
    /// shipped assembly by reflection, exactly as it does <see cref="MapMeshBuilder.SizeVerdict"/>.
    ///
    /// THE CONTRACT, restated so the code can be read against it:
    ///   - dir in N, S, E, W; the camera stands on that side of the map looking toward the centre,
    ///     pitched 45 degrees down: forward f = normalize(toCentreXZ + (0,-1,0)) with |toCentreXZ| = 1,
    ///     so N: (0,-.7071,-.7071), S: (0,-.7071,+.7071), E: (-.7071,-.7071,0), W: (+.7071,-.7071,0);
    ///   - right r = normalize(cross(f, (0,1,0))); up u = cross(r, f);
    ///   - px = (dot(r,p) - originR) * ppm, py = height - (dot(u,p) - originU) * ppm, row 0 at the top;
    ///   - originR/originU = the minimum of dot(r,.)/dot(u,.) over the 8 corners of
    ///     extent x [yMin, yMax]; width/height = ceil(span * ppm).
    ///
    /// ONE CONSEQUENCE THE CONTRACT DOES NOT SAY, and MapCapture has to act on: a Unity camera rotated
    /// by LookRotation(f, u) has its screen-right along cross(u, f), which is exactly -r for any f
    /// (cross(cross(r, f), f) = -r when f is a unit vector perpendicular to r). So the camera's picture
    /// is the contract's picture MIRRORED left to right - the contract's frame (r, u, f) is
    /// left-handed where Unity's (right, up, forward) is not. <see cref="CameraRight"/> computes it and
    /// the harness checks the identity; MapCapture renders in the camera's order and flips the columns
    /// once before developing. Its self-check fails if the camera is not the contract's mirror; that
    /// the flip is actually called is asserted by the harness from the source.
    /// </summary>
    internal static class MapSideView
    {
        /// <summary>The four sides, in the order they are rendered and listed in the meta.</summary>
        internal static readonly string[] Directions = { "N", "S", "E", "W" };

        /// <summary>
        /// The side's basis, each vector ROUNDED TO FLOAT - the precision the meta stores them at - so
        /// every number derived here (the origins, the spans, the self-check's expectation) is derived
        /// from exactly the vectors a reader of the meta will have. False for a dir that is not one of
        /// the four.
        /// </summary>
        /// <param name="dir">"N", "S", "E" or "W".</param>
        /// <param name="forward">f, the way the camera looks.</param>
        /// <param name="right">r, the picture's +x.</param>
        /// <param name="up">u, the picture's +y (towards row 0).</param>
        internal static bool Basis(string dir, out double[] forward, out double[] right, out double[] up)
        {
            forward = right = up = null;

            double tx, tz;

            switch (dir)
            {
                case "N": tx = 0; tz = -1; break;
                case "S": tx = 0; tz = 1; break;
                case "E": tx = -1; tz = 0; break;
                case "W": tx = 1; tz = 0; break;
                default: return false;
            }

            var f = Normalise(new[] { tx, -1d, tz });

            // cross(f, (0,1,0)) = (-f.z, 0, f.x)
            var r = Normalise(new[] { -f[2], 0d, f[0] });

            // u = cross(r, f)
            var u = Cross(r, f);

            forward = ToFloat(f);
            right = ToFloat(r);
            up = ToFloat(u);

            return true;
        }

        /// <summary>The camera's screen-right under Quaternion.LookRotation(f, u): Unity's
        /// right = cross(up, forward). Always -r - see the class summary for why that matters.</summary>
        /// <param name="forward">f.</param>
        /// <param name="up">u.</param>
        internal static double[] CameraRight(double[] forward, double[] up) => Cross(up, forward);

        /// <summary>The picture's frame: the minimum and the span of dot(r,.) and dot(u,.) over the 8
        /// corners of the box, and the min and max of dot(f,.) - the depth the camera's near and far
        /// planes have to cover.</summary>
        /// <param name="forward">f.</param>
        /// <param name="right">r.</param>
        /// <param name="up">u.</param>
        /// <param name="minX">The extent's low x.</param>
        /// <param name="minZ">The extent's low z.</param>
        /// <param name="maxX">The extent's high x.</param>
        /// <param name="maxZ">The extent's high z.</param>
        /// <param name="yMin">The box's low y.</param>
        /// <param name="yMax">The box's high y.</param>
        /// <param name="frame">originR, spanR, originU, spanU, minF, maxF, in that order.</param>
        internal static void Frame(double[] forward, double[] right, double[] up,
            double minX, double minZ, double maxX, double maxZ, double yMin, double yMax, out double[] frame)
        {
            double loR = double.PositiveInfinity, hiR = double.NegativeInfinity;
            double loU = double.PositiveInfinity, hiU = double.NegativeInfinity;
            double loF = double.PositiveInfinity, hiF = double.NegativeInfinity;

            for (var corner = 0; corner < 8; corner++)
            {
                var x = (corner & 1) == 0 ? minX : maxX;
                var y = (corner & 2) == 0 ? yMin : yMax;
                var z = (corner & 4) == 0 ? minZ : maxZ;

                var dr = Dot(right, x, y, z);
                var du = Dot(up, x, y, z);
                var df = Dot(forward, x, y, z);

                if (dr < loR) loR = dr;
                if (dr > hiR) hiR = dr;
                if (du < loU) loU = du;
                if (du > hiU) hiU = du;
                if (df < loF) loF = df;
                if (df > hiF) hiF = df;
            }

            frame = new[] { loR, hiR - loR, loU, hiU - loU, loF, hiF };
        }

        /// <summary>A span in metres as the picture's size in pixels: ceil(span * ppm), at least 1.</summary>
        /// <param name="span">The span in metres.</param>
        /// <param name="ppm">Pixels per metre.</param>
        internal static int Size(double span, float ppm) => Math.Max(1, (int)Math.Ceiling(span * ppm));

        /// <summary>Where a world point lands in the contract's picture, in (fractional) pixels: px from
        /// the left, py from the TOP.</summary>
        /// <param name="right">r.</param>
        /// <param name="up">u.</param>
        /// <param name="originR">The picture's originR.</param>
        /// <param name="originU">The picture's originU.</param>
        /// <param name="ppm">Its pixels per metre.</param>
        /// <param name="height">Its height in pixels.</param>
        /// <param name="x">World x.</param>
        /// <param name="y">World y.</param>
        /// <param name="z">World z.</param>
        /// <param name="pixel">px, py.</param>
        internal static void Pixel(double[] right, double[] up, double originR, double originU, float ppm,
            int height, double x, double y, double z, out double[] pixel)
        {
            var px = (Dot(right, x, y, z) - originR) * ppm;
            var py = height - (Dot(up, x, y, z) - originU) * ppm;

            pixel = new[] { px, py };
        }

        /// <summary>
        /// The inverse of <see cref="Pixel"/> on the GROUND plane y = <paramref name="yMin"/>: the world
        /// x, z whose point at that height lands on picture pixel (px, py). What a side's merge measures
        /// its distance to - the XZ distance from the capturing player to the ground a pixel looks at -
        /// so two captures of a side are compared by the same rule the top picture's pixels are.
        ///
        /// Two equations, dot(r, p) = originR + px / ppm and dot(u, p) = originU + (height - py) / ppm,
        /// with p.y fixed; solved by Cramer's rule in x and z. For N and S, r is along x and the u
        /// equation gives z; for E and W, r is along z and the u equation gives x - one solver covers
        /// both, since the determinant r.x u.z - r.z u.x is 0.707 in magnitude for all four sides.
        ///
        /// Out doubles rather than an array, because the merge calls it for every drawn pixel of a side
        /// - two million on Customs - and an allocation each would be two million of them.
        /// </summary>
        /// <param name="right">r.</param>
        /// <param name="up">u.</param>
        /// <param name="originR">The picture's originR.</param>
        /// <param name="originU">The picture's originU.</param>
        /// <param name="ppm">Its pixels per metre.</param>
        /// <param name="height">Its height in pixels.</param>
        /// <param name="yMin">The ground plane's y - the side's yMin.</param>
        /// <param name="px">The pixel, from the left (fractional; a pixel's centre is col + 0.5).</param>
        /// <param name="py">The pixel, from the TOP.</param>
        /// <param name="x">World x of the ground point.</param>
        /// <param name="z">World z of the ground point.</param>
        internal static void GroundPointOf(double[] right, double[] up, double originR, double originU, float ppm,
            int height, double yMin, double px, double py, out double x, out double z)
        {
            var sr = originR + px / ppm - right[1] * yMin;
            var su = originU + (height - py) / ppm - up[1] * yMin;
            var det = right[0] * up[2] - right[2] * up[0];

            x = (sr * up[2] - right[2] * su) / det;
            z = (right[0] * su - sr * up[0]) / det;
        }

        /// <summary>
        /// Why an earlier picture of a side cannot be merged into this capture's, or null when it can:
        /// the two must be the same PIXELS - the same width, height and pixels per metre, the same
        /// originR and originU and the same basis, each to 1e-4. A different y range moves originU and
        /// the height, so it is caught here too. The geometric half of MapCapture's SidePrevious (the
        /// exposure and the file name are the other half, and need the meta); here so the harness can
        /// hold it to real numbers on the shipped assembly.
        /// </summary>
        /// <param name="oldWidth">The earlier side's width.</param>
        /// <param name="oldHeight">Its height.</param>
        /// <param name="oldPpm">Its pixels per metre.</param>
        /// <param name="oldOriginR">Its originR.</param>
        /// <param name="oldOriginU">Its originU.</param>
        /// <param name="oldForward">Its forward, as the meta stores it.</param>
        /// <param name="oldRight">Its right.</param>
        /// <param name="oldUp">Its up.</param>
        /// <param name="width">This capture's width.</param>
        /// <param name="height">This capture's height.</param>
        /// <param name="ppm">This capture's pixels per metre.</param>
        /// <param name="originR">This capture's originR.</param>
        /// <param name="originU">This capture's originU.</param>
        /// <param name="forward">This capture's forward.</param>
        /// <param name="right">This capture's right.</param>
        /// <param name="up">This capture's up.</param>
        internal static string Mismatch(int oldWidth, int oldHeight, float oldPpm, double oldOriginR, double oldOriginU,
            float[] oldForward, float[] oldRight, float[] oldUp, int width, int height, float ppm, double originR,
            double originU, double[] forward, double[] right, double[] up)
        {
            if (oldWidth != width || oldHeight != height)
                return $"it is {oldWidth}x{oldHeight} px and this one is {width}x{height}";

            if (Math.Abs(oldPpm - ppm) > 1e-4f) return "it was taken at another scale";

            if (Math.Abs(oldOriginR - originR) > 1e-4 || Math.Abs(oldOriginU - originU) > 1e-4)
                return "its origins differ (the box's y range moved)";

            if (!Same(oldForward, forward) || !Same(oldRight, right) || !Same(oldUp, up))
                return "its basis differs";

            return null;
        }

        private static bool Same(float[] stored, double[] v) =>
            stored != null && stored.Length == 3 && v != null && v.Length == 3 &&
            Math.Abs(stored[0] - v[0]) <= 1e-4 && Math.Abs(stored[1] - v[1]) <= 1e-4 && Math.Abs(stored[2] - v[2]) <= 1e-4;

        /// <summary>dot(v, (x, y, z)).</summary>
        /// <param name="v">The vector.</param>
        /// <param name="x">x.</param>
        /// <param name="y">y.</param>
        /// <param name="z">z.</param>
        internal static double Dot(double[] v, double x, double y, double z) => v[0] * x + v[1] * y + v[2] * z;

        private static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]
        };

        private static double[] Normalise(double[] v)
        {
            var length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        private static double[] ToFloat(double[] v) => new double[] { (float)v[0], (float)v[1], (float)v[2] };
    }

    /// <summary>
    /// The one rule the pictures merge by - floors and side views alike: this capture's pixel is TAKEN
    /// when it drew one and either nothing is there yet or it was seen from closer. Best-of by distance,
    /// not newest-wins: a pixel rendered from 900 m away is drawn at the game's lowest level of detail,
    /// so newest-wins would make a map worse the more often it was captured.
    ///
    /// A class of its own and Unity-free for one reason: MapCapture.DevelopBand calls it for every pixel
    /// of every floor and every side, and the harness calls the same method on the shipped assembly -
    /// so the rule the harness proves is the rule the capture runs, not a copy of it.
    /// </summary>
    internal static class CaptureMerge
    {
        /// <summary>Whether this capture's pixel replaces what is on disk.</summary>
        /// <param name="drawn">Whether this capture drew the pixel.</param>
        /// <param name="distance">Its distance step from the capturing player.</param>
        /// <param name="oldDrawn">Whether the picture on disk has a pixel there.</param>
        /// <param name="oldDistance">That pixel's recorded distance step.</param>
        internal static bool Takes(bool drawn, byte distance, bool oldDrawn, byte oldDistance) =>
            drawn && (!oldDrawn || distance < oldDistance);
    }
    /// <summary>
    /// Quadric edge-collapse decimation (Garland and Heckbert) on plain arrays - stage V's own reduction
    /// of the game's most detailed building meshes to an area budget.
    ///
    /// UNITY-FREE ON PURPOSE, twice over. It runs on worker threads, where touching a Unity object is
    /// undefined, so it takes positions and indices as float and int arrays. And the harness calls it on
    /// the shipped assembly by reflection, so every property below is proven against the code the
    /// capture runs, not a copy of it.
    ///
    /// The algorithm, in the order it runs:
    ///   1. WELD by corner, not by vertex: two corners weld when they are within <see cref="WeldMetres"/>
    ///      AND their faces' normals are less than 120 degrees apart. By position alone the weld folded a
    ///      two-sided plate - or a plate 5 mm from its twin - into one sheet whose every edge looked
    ///      interior, and the result kept half the area. A vertex index shared by faces facing opposite
    ///      ways is split into two for the same reason;
    ///   2. drop degenerate triangles and DEDUPE identical ones (same corners, same winding);
    ///   3. a QUADRIC per vertex from its face planes, plus, for every BOUNDARY edge - used by one face,
    ///      or by more than two (a non-manifold junction is a boundary too) - a plane through the edge
    ///      perpendicular to its face, weighted <see cref="BoundaryWeight"/> times, so openings and
    ///      silhouettes are the last things to go;
    ///   4. every edge into a min-HEAP by its NORMALISED cost - the quadric error over the plane weight
    ///      absorbed, a mean squared distance in square metres - at the optimal point (the 3x3 solve), or
    ///      at the MIDPOINT when the quadric is singular. Never an end: on a flat or ruled region every
    ///      candidate costs nothing, ties went to one end, and that one vertex grew a fan of hundreds of
    ///      faces that every later collapse walked - an n96 flat box took five seconds;
    ///   5. collapse the cheapest VALID edge: both ends alive and unchanged; the link condition (no more
    ///      shared neighbours than shared faces) and its boundary form (two boundary vertices joined by
    ///      an INTERIOR edge would bridge an opening); the surviving vertex's fan no larger than
    ///      <see cref="MaxFan"/>; no surviving face degenerate; no face normal turned by more than 60
    ///      degrees (the flip test, which catches near-folds as well as inversions); and the new point
    ///      within 2 x the stop distance of every plane around it - the mean error alone can hide one
    ///      feature moved a long way;
    ///   6. stop at the target, or when the cheapest collapse's mean error passes
    ///      (<see cref="MaxRelativeError"/> x bounds diagonal) squared - and then, because a target is a
    ///      BUDGET the smallest buildings pay for, carry on with the error and distance limits relaxed
    ///      until the count is within the HARD LIMIT (target x <see cref="HardLimitFactor"/> by default).
    ///      A mesh that still cannot get there is reported so the caller can cluster it;
    ///   7. a TIME CAP measured on the worker.
    ///
    /// Winding is preserved: a collapse only moves corners and removes faces, never reorders a face.
    /// </summary>
    internal static class MeshDecimator
    {
        /// <summary>Corners closer than this, in metres, may weld.</summary>
        internal const double WeldMetres = 0.01;

        /// <summary>Two corners whose faces' normals have a cosine under this - more than 120 degrees
        /// apart - never weld: they are the two sides of something thin.</summary>
        internal const double OpposedCosine = -0.5;

        /// <summary>How much a boundary edge's constraint plane weighs against a face plane.</summary>
        internal const double BoundaryWeight = 10d;

        /// <summary>The stop rule's share of the bounds diagonal.</summary>
        internal const double MaxRelativeError = 0.005;

        /// <summary>A collapse may not put the new point further than this many stop distances from any
        /// plane around it (while the error limit is in force).</summary>
        internal const double MaxDistanceFactor = 2d;

        /// <summary>A face normal may turn by at most this cosine's angle - 60 degrees.</summary>
        internal const double FlipCosine = 0.5;

        /// <summary>The most faces the surviving vertex may have after a collapse, while the error limit is
        /// in force.</summary>
        internal const int MaxFan = 32;

        /// <summary>The default hard limit, as a multiple of the target.</summary>
        internal const double HardLimitFactor = 1.25;

        /// <summary>The heap's tie-break weight on an edge's squared length - see Push.</summary>
        private const double TieBreak = 1e-9;

        /// <summary>What one decimation produced and what it cost.</summary>
        internal sealed class Result
        {
            /// <summary>x, y, z per vertex; null when the run timed out.</summary>
            internal float[] Positions;

            /// <summary>Three indices per triangle, in the input's winding; null when timed out.</summary>
            internal int[] Triangles;

            internal bool TimedOut;
            internal bool StoppedByError;
            internal bool Relaxed;
            internal bool OverLimit;
            internal int SourceTriangles;
            internal int WeldedVertices;
            internal int Deduplicated;
            internal int Collapses;
            internal int RejectedFlips;
            internal int RejectedFans;
            internal int RejectedDistance;
            internal int RejectedLink;
            internal double Milliseconds;

            /// <summary>Where the time went: the weld, the adjacency and quadrics, the collapses.</summary>
            internal double WeldMs;

            internal double SetupMs;
            internal double CollapseMs;
            internal long WorkspaceBytes;
        }

        /// <summary>
        /// A decimation's scratch memory, reused across buildings: every array only ever grows, so a
        /// worker that has decimated one large building decimates the next without allocating. One per
        /// concurrent worker - a workspace is never shared between two decimations at once.
        /// </summary>
        internal sealed class Workspace
        {
            internal double[] FaceN = new double[0];
            internal bool[] FaceOk = new bool[0];
            internal double[] FirstN = new double[0];
            internal double[] OppN = new double[0];
            internal byte[] NormalState = new byte[0];
            internal int[] SubId = new int[0];
            internal double[] SubP = new double[0];
            internal double[] SubN = new double[0];
            internal int[] Next = new int[0];
            internal int[] Rep = new int[0];
            internal double[] P = new double[0];
            internal double[] N = new double[0];
            internal int[] F = new int[0];
            internal double[] F0 = new double[0];
            internal bool[] FaceDead = new bool[0];
            internal double[] Q = new double[0];
            internal double[] W = new double[0];
            internal List<int>[] Vf = new List<int>[0];
            internal bool[] Dead = new bool[0];
            internal bool[] Boundary = new bool[0];
            internal int[] Stamp = new int[0];
            internal int[] Mark = new int[0];
            internal int Generation;
            internal Entry[] Heap = new Entry[1024];
            internal int[] Count = new int[0];
            internal int[] LastFace = new int[0];
            internal byte[] CornerGroup = new byte[0];
            internal readonly Dictionary<long, int> Head = new Dictionary<long, int>(Mixed.Instance);
            internal readonly HashSet<long> Seen = new HashSet<long>(Mixed.Instance);
            internal readonly List<int> Faces = new List<int>();
            internal int[] Map = new int[0];

            /// <summary>One heap entry: the collapse's cost, its two ends and their stamps when it was pushed, and
            /// where the survivor goes - valid for as long as both stamps are.</summary>
            internal struct Entry
            {
                internal double Cost;
                internal double X, Y, Z;
                internal int A, B, StampA, StampB;
            }

            internal static T[] Grow<T>(T[] array, int size) =>
                array.Length >= size ? array : new T[Math.Max(size, array.Length + array.Length / 2)];

            /// <summary>Bytes the arrays hold, for the peak line.</summary>
            internal long Bytes()
            {
                long b = 0;
                b += (FaceN.Length + FirstN.Length + OppN.Length + SubP.Length + SubN.Length + P.Length + N.Length +
                      Q.Length + W.Length + F0.Length) * 8L;
                b += Heap.Length * 48L;
                b += (SubId.Length + Next.Length + Rep.Length + F.Length + Stamp.Length + Mark.Length + Map.Length +
                      Count.Length + LastFace.Length) * 4L;
                b += FaceOk.Length + NormalState.Length + FaceDead.Length + Dead.Length + Boundary.Length + CornerGroup.Length;
                b += Vf.Length * 48L + (Head.Count + Seen.Count) * 24L + Faces.Count * 4L;
                return b;
            }
        }

        /// <summary>
        /// Reduces a triangle mesh toward <paramref name="target"/> triangles, with the default hard limit
        /// of target x <see cref="HardLimitFactor"/> and a fresh workspace. The harness's entry point; the
        /// capture calls <see cref="DecimateWith"/>.
        /// </summary>
        /// <param name="positions">x, y, z per vertex, in metres.</param>
        /// <param name="triangles">Three indices per triangle.</param>
        /// <param name="target">The triangle count to stop at.</param>
        /// <param name="timeCapMs">Milliseconds of worker time allowed.</param>
        /// <param name="rejectFlips">The flip test. Always true in the capture; false exists only so the
        /// harness can prove the test is what keeps normals from inverting.</param>
        internal static Result Decimate(float[] positions, int[] triangles, int target, double timeCapMs,
            bool rejectFlips = true) =>
            DecimateWith(positions, triangles, target, (int)Math.Ceiling(Math.Max(0, target) * HardLimitFactor),
                timeCapMs, rejectFlips, null);

        /// <summary>The decimation, with an explicit hard limit and a reused workspace.</summary>
        /// <param name="positions">x, y, z per vertex, in metres.</param>
        /// <param name="triangles">Three indices per triangle.</param>
        /// <param name="target">The triangle count to stop at while the error limit holds.</param>
        /// <param name="hardLimit">The count to reach whatever the error: the building's budget.</param>
        /// <param name="timeCapMs">Milliseconds of worker time allowed.</param>
        /// <param name="rejectFlips">The flip test; see Decimate.</param>
        /// <param name="workspace">Scratch memory to reuse, or null for a fresh one.</param>
        internal static Result DecimateWith(float[] positions, int[] triangles, int target, int hardLimit,
            double timeCapMs, bool rejectFlips, Workspace workspace)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result { SourceTriangles = triangles == null ? 0 : triangles.Length / 3 };
            var ws = workspace ?? new Workspace();

            try
            {
                new Work(positions, triangles, target, Math.Max(target, hardLimit), timeCapMs, rejectFlips, clock,
                    result, ws).Run();
            }
            finally
            {
                result.Milliseconds = clock.Elapsed.TotalMilliseconds;
                result.WorkspaceBytes = ws.Bytes();
            }

            return result;
        }

        /// <summary>
        /// The LAST RESORT: vertex clustering to at most <paramref name="limit"/> triangles - every vertex
        /// in one grid cell merged to the cell's mean, degenerate and duplicate faces dropped, the cell
        /// grown until the count fits. O(n) a pass, so it always finishes; crude, so it is used only when
        /// the quadric decimation timed out or could not reach the building's hard limit and there is no
        /// coarser level and no budget to store the source as it is - the one alternative left to dropping
        /// the building, which stage V does not do.
        /// </summary>
        /// <param name="positions">x, y, z per vertex.</param>
        /// <param name="triangles">Three indices per triangle.</param>
        /// <param name="limit">The most triangles the result may have.</param>
        /// <param name="timeCapMs">Milliseconds allowed; past them the result is TimedOut with no mesh.</param>
        internal static Result Cluster(float[] positions, int[] triangles, int limit, double timeCapMs = double.MaxValue)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result { SourceTriangles = triangles == null ? 0 : triangles.Length / 3 };

            if (positions == null || triangles == null || positions.Length < 9) return result;

            var n = positions.Length / 3;

            if (triangles.Length / 3 <= limit)
            {
                result.Positions = (float[])positions.Clone();
                result.Triangles = (int[])triangles.Clone();
                return result;
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var area = 0d;

            for (var v = 0; v < n; v++)
            {
                minX = Math.Min(minX, positions[v * 3]); maxX = Math.Max(maxX, positions[v * 3]);
                minY = Math.Min(minY, positions[v * 3 + 1]); maxY = Math.Max(maxY, positions[v * 3 + 1]);
                minZ = Math.Min(minZ, positions[v * 3 + 2]); maxZ = Math.Max(maxZ, positions[v * 3 + 2]);
            }

            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;

                double ux = positions[b * 3] - positions[a * 3], uy = positions[b * 3 + 1] - positions[a * 3 + 1], uz = positions[b * 3 + 2] - positions[a * 3 + 2];
                double vx = positions[c * 3] - positions[a * 3], vy = positions[c * 3 + 1] - positions[a * 3 + 1], vz = positions[c * 3 + 2] - positions[a * 3 + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;

                area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            }

            var diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
            var cell = Math.Max(1e-3, Math.Sqrt(area / Math.Max(1d, limit / 2d)));

            var cells = new Dictionary<long, int>(n, Mixed.Instance);
            var id = new int[n];
            var sums = new List<double>();
            var counts = new List<int>();
            var faces = new List<int>();
            var seen = new HashSet<long>(Mixed.Instance);

            for (var pass = 0; pass < 40; pass++)
            {
                if (clock.Elapsed.TotalMilliseconds > timeCapMs)
                {
                    result.TimedOut = true;
                    break;
                }

                cells.Clear();
                sums.Clear();
                counts.Clear();
                faces.Clear();
                seen.Clear();

                for (var v = 0; v < n; v++)
                {
                    var key = Key((long)Math.Floor((positions[v * 3] - minX) / cell),
                        (long)Math.Floor((positions[v * 3 + 1] - minY) / cell),
                        (long)Math.Floor((positions[v * 3 + 2] - minZ) / cell));

                    if (!cells.TryGetValue(key, out var k))
                    {
                        k = counts.Count;
                        cells[key] = k;
                        counts.Add(0);
                        sums.Add(0); sums.Add(0); sums.Add(0);
                    }

                    id[v] = k;
                    counts[k]++;
                    sums[k * 3] += positions[v * 3];
                    sums[k * 3 + 1] += positions[v * 3 + 1];
                    sums[k * 3 + 2] += positions[v * 3 + 2];
                }

                for (var t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;

                    a = id[a]; b = id[b]; c = id[c];
                    if (a == b || b == c || a == c) continue;
                    if (!seen.Add(TriangleKey(a, b, c))) continue;

                    faces.Add(a); faces.Add(b); faces.Add(c);
                }

                if (faces.Count / 3 <= limit || cell > diagonal * 2)
                {
                    var map = new int[counts.Count];
                    for (var i = 0; i < map.Length; i++) map[i] = -1;

                    var outP = new List<float>();
                    var outT = new List<int>(faces.Count);

                    foreach (var k in faces)
                    {
                        if (map[k] < 0)
                        {
                            map[k] = outP.Count / 3;
                            outP.Add((float)(sums[k * 3] / counts[k]));
                            outP.Add((float)(sums[k * 3 + 1] / counts[k]));
                            outP.Add((float)(sums[k * 3 + 2] / counts[k]));
                        }

                        outT.Add(map[k]);
                    }

                    // Past twice the diagonal everything is one cell; a limit under what is left is honoured by
                    // cutting the list, which only a limit of a handful of triangles can reach.
                    if (outT.Count / 3 > limit) outT.RemoveRange(limit * 3, outT.Count - limit * 3);

                    result.Positions = outP.ToArray();
                    result.Triangles = outT.ToArray();
                    break;
                }

                cell *= 1.3;
            }

            result.Milliseconds = clock.Elapsed.TotalMilliseconds;

            return result;
        }

        private static long Key(long x, long y, long z) => unchecked(x * 73856093L ^ y * 19349663L ^ z * 83492791L);

        /// <summary>A triangle's identity for the dedupe: its corners rotated so the smallest leads, the
        /// winding kept - a triangle and its reverse are two faces.</summary>
        private static long TriangleKey(int a, int b, int c)
        {
            if (b < a && b < c) { var t = a; a = b; b = c; c = t; }
            else if (c < a && c < b) { var t = a; a = c; c = b; b = t; }

            return ((long)a << 42) | ((long)(b & 0x1FFFFF) << 21) | (long)(c & 0x1FFFFF);
        }

        /// <summary>
        /// A 64-bit key's hash, MIXED. long.GetHashCode() is (int)key ^ (int)(key >> 32), and for an edge key
        /// (lo &lt;&lt; 32) | hi that is lo ^ hi - neighbouring vertex indices XOR to a handful of small values,
        /// the edge table collapsed into a few long chains and decimation went QUADRATIC (9 s for 200k
        /// triangles in the harness). The splitmix64 finaliser spreads every bit of the key over the hash.
        /// </summary>
        private sealed class Mixed : IEqualityComparer<long>
        {
            internal static readonly Mixed Instance = new Mixed();

            public bool Equals(long x, long y) => x == y;

            public int GetHashCode(long key)
            {
                unchecked
                {
                    var z = (ulong)key + 0x9E3779B97F4A7C15UL;
                    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                    z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                    z ^= z >> 31;
                    return (int)z ^ (int)(z >> 32);
                }
            }
        }

        /// <summary>One run's state over a workspace.</summary>
        private sealed class Work
        {
            private readonly float[] _in;
            private readonly int[] _tris;
            private readonly int _target;
            private readonly int _limit;
            private readonly double _cap;
            private readonly bool _rejectFlips;
            private readonly Stopwatch _clock;
            private readonly Result _result;
            private readonly Workspace _ws;

            private int _vertices;
            private int _faces;
            private int _live;
            private int _hCount;
            private double _maxError;
            private double _maxDistance;
            private bool _relaxed;
            private readonly double[] _sum = new double[10];

            internal Work(float[] positions, int[] triangles, int target, int limit, double cap, bool rejectFlips,
                Stopwatch clock, Result result, Workspace ws)
            {
                _in = positions;
                _tris = triangles;
                _target = Math.Max(0, target);
                _limit = Math.Max(_target, limit);
                _cap = cap;
                _rejectFlips = rejectFlips;
                _clock = clock;
                _result = result;
                _ws = ws;
            }

            private bool OutOfTime() => _clock.Elapsed.TotalMilliseconds > _cap;

            internal void Run()
            {
                if (_in == null || _tris == null || _in.Length < 9 || _tris.Length < 3) return;

                var mark = _clock.Elapsed.TotalMilliseconds;

                if (!Weld() || OutOfTime()) { _result.TimedOut = true; return; }

                _result.WeldMs = _clock.Elapsed.TotalMilliseconds - mark;
                mark = _clock.Elapsed.TotalMilliseconds;

                Adjacency();
                if (OutOfTime()) { _result.TimedOut = true; return; }

                Quadrics();
                if (OutOfTime()) { _result.TimedOut = true; return; }

                _result.SetupMs = _clock.Elapsed.TotalMilliseconds - mark;
                mark = _clock.Elapsed.TotalMilliseconds;

                if (!Collapse()) { _result.TimedOut = true; return; }

                _result.CollapseMs = _clock.Elapsed.TotalMilliseconds - mark;

                _result.OverLimit = _live > _limit;

                Output();
            }

            // --- 1. weld by corner, 2. degenerate and duplicate faces --------------------------------------

            private bool Weld()
            {
                var ws = _ws;
                var n = _in.Length / 3;
                var t = _tris.Length / 3;

                ws.FaceN = Workspace.Grow(ws.FaceN, t * 3);
                ws.FaceOk = Workspace.Grow(ws.FaceOk, t);
                ws.FirstN = Workspace.Grow(ws.FirstN, n * 3);
                ws.OppN = Workspace.Grow(ws.OppN, n * 3);
                ws.NormalState = Workspace.Grow(ws.NormalState, n);
                ws.SubId = Workspace.Grow(ws.SubId, n * 2);
                ws.CornerGroup = Workspace.Grow(ws.CornerGroup, t * 3);

                for (var v = 0; v < n; v++) ws.NormalState[v] = 0;
                for (var s = 0; s < n * 2; s++) ws.SubId[s] = -1;

                // face normals, and each vertex's first and first-opposed normal
                for (var f = 0; f < t; f++)
                {
                    int a = _tris[f * 3], b = _tris[f * 3 + 1], c = _tris[f * 3 + 2];
                    ws.FaceOk[f] = false;

                    if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;

                    double ux = _in[b * 3] - _in[a * 3], uy = _in[b * 3 + 1] - _in[a * 3 + 1], uz = _in[b * 3 + 2] - _in[a * 3 + 2];
                    double vx = _in[c * 3] - _in[a * 3], vy = _in[c * 3 + 1] - _in[a * 3 + 1], vz = _in[c * 3 + 2] - _in[a * 3 + 2];
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);

                    if (length < 1e-12) continue;

                    ws.FaceOk[f] = true;
                    ws.FaceN[f * 3] = nx / length;
                    ws.FaceN[f * 3 + 1] = ny / length;
                    ws.FaceN[f * 3 + 2] = nz / length;
                }

                var subs = 0;

                for (var f = 0; f < t; f++)
                {
                    if (!ws.FaceOk[f]) continue;

                    for (var k = 0; k < 3; k++)
                    {
                        var v = _tris[f * 3 + k];
                        var group = Group(v, f);
                        var s = v * 2 + group;

                        ws.CornerGroup[f * 3 + k] = (byte)group;
                        if (ws.SubId[s] < 0) ws.SubId[s] = subs++;
                    }
                }

                ws.SubP = Workspace.Grow(ws.SubP, subs * 3);
                ws.SubN = Workspace.Grow(ws.SubN, subs * 3);
                ws.Next = Workspace.Grow(ws.Next, subs);
                ws.Rep = Workspace.Grow(ws.Rep, subs);
                ws.P = Workspace.Grow(ws.P, subs * 3);
                ws.N = Workspace.Grow(ws.N, subs * 3);

                for (var v = 0; v < n; v++)
                    for (var group = 0; group < 2; group++)
                    {
                        var id = ws.SubId[v * 2 + group];
                        if (id < 0) continue;

                        ws.SubP[id * 3] = _in[v * 3];
                        ws.SubP[id * 3 + 1] = _in[v * 3 + 1];
                        ws.SubP[id * 3 + 2] = _in[v * 3 + 2];

                        var source = group == 0 ? ws.FirstN : ws.OppN;
                        ws.SubN[id * 3] = source[v * 3];
                        ws.SubN[id * 3 + 1] = source[v * 3 + 1];
                        ws.SubN[id * 3 + 2] = source[v * 3 + 2];
                    }

                // the spatial weld of the sub-vertices: 2x2x2 cells nearest the point, normals under 120 deg
                var cell = WeldMetres * 2d;
                var eps2 = WeldMetres * WeldMetres;
                var head = ws.Head;
                head.Clear();

                for (var i = 0; i < subs; i++)
                {
                    if ((i & 4095) == 0 && OutOfTime()) return false;

                    double x = ws.SubP[i * 3], y = ws.SubP[i * 3 + 1], z = ws.SubP[i * 3 + 2];
                    double sx = ws.SubN[i * 3], sy = ws.SubN[i * 3 + 1], sz = ws.SubN[i * 3 + 2];

                    var gx = x / cell; var gy = y / cell; var gz = z / cell;
                    var cx = (long)Math.Floor(gx); var cy = (long)Math.Floor(gy); var cz = (long)Math.Floor(gz);
                    var ox = gx - cx < 0.5 ? -1 : 1; var oy = gy - cy < 0.5 ? -1 : 1; var oz = gz - cz < 0.5 ? -1 : 1;

                    var found = -1;

                    for (var k = 0; k < 8 && found < 0; k++)
                    {
                        var key = Key(cx + ((k & 1) != 0 ? ox : 0), cy + ((k & 2) != 0 ? oy : 0), cz + ((k & 4) != 0 ? oz : 0));
                        if (!head.TryGetValue(key, out var j)) continue;

                        for (; j >= 0; j = ws.Next[j])
                        {
                            var dx = ws.P[j * 3] - x; var dy = ws.P[j * 3 + 1] - y; var dz = ws.P[j * 3 + 2] - z;
                            if (dx * dx + dy * dy + dz * dz > eps2) continue;

                            var dot = ws.N[j * 3] * sx + ws.N[j * 3 + 1] * sy + ws.N[j * 3 + 2] * sz;
                            if (dot < OpposedCosine) continue;

                            found = j;
                            break;
                        }
                    }

                    if (found >= 0)
                    {
                        ws.Rep[i] = found;
                        continue;
                    }

                    var w = _vertices++;
                    ws.P[w * 3] = x; ws.P[w * 3 + 1] = y; ws.P[w * 3 + 2] = z;
                    ws.N[w * 3] = sx; ws.N[w * 3 + 1] = sy; ws.N[w * 3 + 2] = sz;

                    var own = Key(cx, cy, cz);
                    ws.Next[w] = head.TryGetValue(own, out var first) ? first : -1;
                    head[own] = w;
                    ws.Rep[i] = w;
                }

                _result.WeldedVertices = subs - _vertices;

                // the faces, re-pointed at the welded vertices; degenerate and duplicate ones dropped
                var faces = ws.Faces;
                faces.Clear();
                ws.Seen.Clear();

                for (var f = 0; f < t; f++)
                {
                    if (!ws.FaceOk[f]) continue;

                    var a = ws.Rep[ws.SubId[_tris[f * 3] * 2 + ws.CornerGroup[f * 3]]];
                    var b = ws.Rep[ws.SubId[_tris[f * 3 + 1] * 2 + ws.CornerGroup[f * 3 + 1]]];
                    var c = ws.Rep[ws.SubId[_tris[f * 3 + 2] * 2 + ws.CornerGroup[f * 3 + 2]]];

                    if (a == b || b == c || a == c) continue;

                    if (!ws.Seen.Add(TriangleKey(a, b, c)))
                    {
                        _result.Deduplicated++;
                        continue;
                    }

                    faces.Add(a); faces.Add(b); faces.Add(c);
                }

                _faces = faces.Count / 3;
                ws.F = Workspace.Grow(ws.F, faces.Count);
                faces.CopyTo(ws.F);

                return true;
            }

            /// <summary>Which of a vertex's two sides a face corner belongs to: 0 when the face's normal is
            /// within 120 degrees of the first face the vertex was seen in, 1 when it opposes it. Records the
            /// first and first-opposed normals as it goes, so the answer is the same for every corner of a
            /// face asked twice.</summary>
            private int Group(int v, int f)
            {
                var ws = _ws;
                double nx = ws.FaceN[f * 3], ny = ws.FaceN[f * 3 + 1], nz = ws.FaceN[f * 3 + 2];

                if (ws.NormalState[v] == 0)
                {
                    ws.NormalState[v] = 1;
                    ws.FirstN[v * 3] = nx; ws.FirstN[v * 3 + 1] = ny; ws.FirstN[v * 3 + 2] = nz;
                    return 0;
                }

                var dot = ws.FirstN[v * 3] * nx + ws.FirstN[v * 3 + 1] * ny + ws.FirstN[v * 3 + 2] * nz;
                if (dot >= OpposedCosine) return 0;

                if (ws.NormalState[v] == 1)
                {
                    ws.NormalState[v] = 2;
                    ws.OppN[v * 3] = nx; ws.OppN[v * 3 + 1] = ny; ws.OppN[v * 3 + 2] = nz;
                }

                return 1;
            }

            // --- adjacency -------------------------------------------------------------------------------------

            private void Adjacency()
            {
                var ws = _ws;
                var m = _vertices;

                ws.FaceDead = Workspace.Grow(ws.FaceDead, _faces);
                for (var f = 0; f < _faces; f++) ws.FaceDead[f] = false;
                _live = _faces;

                if (ws.Vf.Length < m)
                {
                    var grown = new List<int>[Math.Max(m, ws.Vf.Length + ws.Vf.Length / 2)];
                    Array.Copy(ws.Vf, grown, ws.Vf.Length);
                    ws.Vf = grown;
                }

                for (var v = 0; v < m; v++)
                {
                    if (ws.Vf[v] == null) ws.Vf[v] = new List<int>(8);
                    else ws.Vf[v].Clear();
                }

                ws.Dead = Workspace.Grow(ws.Dead, m);
                ws.Boundary = Workspace.Grow(ws.Boundary, m);
                ws.Stamp = Workspace.Grow(ws.Stamp, m);
                ws.Mark = Workspace.Grow(ws.Mark, m);

                for (var v = 0; v < m; v++)
                {
                    ws.Dead[v] = false;
                    ws.Boundary[v] = false;
                    ws.Stamp[v] = 0;
                    ws.Mark[v] = 0;
                }

                ws.Generation = 0;

                ws.F0 = Workspace.Grow(ws.F0, _faces * 3);

                for (var f = 0; f < _faces; f++)
                {
                    ws.Vf[ws.F[f * 3]].Add(f);
                    ws.Vf[ws.F[f * 3 + 1]].Add(f);
                    ws.Vf[ws.F[f * 3 + 2]].Add(f);

                    // Each face's ORIGINAL normal, which the flip test holds it to as well as to its current
                    // one: a turn of 50 degrees four times over passes a per-step test and ends upside down.
                    Normal(ws.F[f * 3], ws.F[f * 3 + 1], ws.F[f * 3 + 2], out var nx, out var ny, out var nz);
                    ws.F0[f * 3] = nx;
                    ws.F0[f * 3 + 1] = ny;
                    ws.F0[f * 3 + 2] = nz;
                }

                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

                for (var v = 0; v < m; v++)
                {
                    minX = Math.Min(minX, ws.P[v * 3]); maxX = Math.Max(maxX, ws.P[v * 3]);
                    minY = Math.Min(minY, ws.P[v * 3 + 1]); maxY = Math.Max(maxY, ws.P[v * 3 + 1]);
                    minZ = Math.Min(minZ, ws.P[v * 3 + 2]); maxZ = Math.Max(maxZ, ws.P[v * 3 + 2]);
                }

                var diagonal = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) +
                                         (maxZ - minZ) * (maxZ - minZ));
                var allowed = MaxRelativeError * diagonal;

                _maxError = allowed * allowed;
                _maxDistance = MaxDistanceFactor * allowed;
            }

            // --- 3. quadrics, and 4. the heap -------------------------------------------------------------------

            private void Quadrics()
            {
                var ws = _ws;
                var m = _vertices;

                ws.Q = Workspace.Grow(ws.Q, m * 10);
                ws.W = Workspace.Grow(ws.W, m);
                for (var i = 0; i < m * 10; i++) ws.Q[i] = 0;
                for (var i = 0; i < m; i++) ws.W[i] = 0;

                for (var f = 0; f < _faces; f++)
                {
                    int a = ws.F[f * 3], b = ws.F[f * 3 + 1], c = ws.F[f * 3 + 2];

                    if (!Normal(a, b, c, out var nx, out var ny, out var nz)) continue;

                    var d = -(nx * ws.P[a * 3] + ny * ws.P[a * 3 + 1] + nz * ws.P[a * 3 + 2]);
                    AddPlane(a, nx, ny, nz, d, 1d);
                    AddPlane(b, nx, ny, nz, d, 1d);
                    AddPlane(c, nx, ny, nz, d, 1d);
                }

                // The edges, from each vertex's fan: for vertex v, every neighbour u > v and how many of v's faces
                // use the edge v-u. A generation-stamped counter per vertex instead of a dictionary of every edge
                // - the dictionary was a third of the set-up. One face is a boundary; MORE than two is a
                // non-manifold junction, a boundary too.
                ws.Count = Workspace.Grow(ws.Count, m);
                ws.LastFace = Workspace.Grow(ws.LastFace, m);

                var edges = 0;

                for (var v = 0; v < m; v++)
                {
                    ws.Generation++;
                    var gen = ws.Generation;

                    foreach (var f in ws.Vf[v])
                        for (var k = 0; k < 3; k++)
                        {
                            var u = ws.F[f * 3 + k];
                            if (u <= v) continue;

                            if (ws.Mark[u] != gen)
                            {
                                ws.Mark[u] = gen;
                                ws.Count[u] = 0;
                            }

                            ws.Count[u]++;
                            ws.LastFace[u] = f;
                        }

                    foreach (var f in ws.Vf[v])
                        for (var k = 0; k < 3; k++)
                        {
                            var u = ws.F[f * 3 + k];
                            if (u <= v || ws.Mark[u] != gen) continue;

                            // visited: reuse the mark so the edge is handled once
                            ws.Mark[u] = gen - 1;
                            edges++;

                            if (ws.Count[u] != 2) Boundary(v, u, ws.LastFace[u]);
                        }
                }

                // every edge into the heap, sized for them and the pushes to come
                if (ws.Heap.Length < edges + 1024) ws.Heap = new Workspace.Entry[edges + edges / 2 + 1024];

                _hCount = 0;

                for (var v = 0; v < m; v++)
                {
                    ws.Generation++;
                    var gen = ws.Generation;

                    foreach (var f in ws.Vf[v])
                        for (var k = 0; k < 3; k++)
                        {
                            var u = ws.F[f * 3 + k];
                            if (u <= v || ws.Mark[u] == gen) continue;

                            ws.Mark[u] = gen;
                            Push(v, u);
                        }
                }
            }

            /// <summary>A boundary edge's constraint plane: through the edge, perpendicular to its face, weighted
            /// <see cref="BoundaryWeight"/> times; both ends marked as boundary vertices.</summary>
            private void Boundary(int a, int b, int face)
            {
                var ws = _ws;

                ws.Boundary[a] = true;
                ws.Boundary[b] = true;

                if (!Normal(ws.F[face * 3], ws.F[face * 3 + 1], ws.F[face * 3 + 2], out var nx, out var ny, out var nz)) return;

                double ex = ws.P[b * 3] - ws.P[a * 3], ey = ws.P[b * 3 + 1] - ws.P[a * 3 + 1], ez = ws.P[b * 3 + 2] - ws.P[a * 3 + 2];
                double px = ey * nz - ez * ny, py = ez * nx - ex * nz, pz = ex * ny - ey * nx;
                var length = Math.Sqrt(px * px + py * py + pz * pz);
                if (length < 1e-12) return;

                px /= length; py /= length; pz /= length;

                var d = -(px * ws.P[a * 3] + py * ws.P[a * 3 + 1] + pz * ws.P[a * 3 + 2]);
                AddPlane(a, px, py, pz, d, BoundaryWeight);
                AddPlane(b, px, py, pz, d, BoundaryWeight);
            }

            private bool Normal(int a, int b, int c, out double nx, out double ny, out double nz)
            {
                var p = _ws.P;
                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];

                nx = uy * vz - uz * vy;
                ny = uz * vx - ux * vz;
                nz = ux * vy - uy * vx;

                var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12) return false;

                nx /= length; ny /= length; nz /= length;
                return true;
            }

            private void AddPlane(int v, double a, double b, double c, double d, double w)
            {
                var q = _ws.Q;
                var i = v * 10;

                q[i] += w * a * a; q[i + 1] += w * a * b; q[i + 2] += w * a * c; q[i + 3] += w * a * d;
                q[i + 4] += w * b * b; q[i + 5] += w * b * c; q[i + 6] += w * b * d;
                q[i + 7] += w * c * c; q[i + 8] += w * c * d; q[i + 9] += w * d * d;
                _ws.W[v] += w;
            }

            /// <summary>The normalised cost of collapsing a-b and where the survivor goes: the quadric's
            /// optimal point, or the MIDPOINT when it is singular.</summary>
            private double Cost(int a, int b, out double x, out double y, out double z)
            {
                var q = _sum;
                for (var i = 0; i < 10; i++) q[i] = _ws.Q[a * 10 + i] + _ws.Q[b * 10 + i];

                double a11 = q[0], a12 = q[1], a13 = q[2], a22 = q[4], a23 = q[5], a33 = q[7];
                double b1 = -q[3], b2 = -q[6], b3 = -q[8];

                var det = a11 * (a22 * a33 - a23 * a23) - a12 * (a12 * a33 - a23 * a13) + a13 * (a12 * a23 - a22 * a13);
                var scale = Math.Abs(a11) + Math.Abs(a22) + Math.Abs(a33);

                if (scale > 0 && Math.Abs(det) > 1e-9 * scale * scale * scale)
                {
                    x = (b1 * (a22 * a33 - a23 * a23) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a23 - a22 * b3)) / det;
                    y = (a11 * (b2 * a33 - a23 * b3) - b1 * (a12 * a33 - a23 * a13) + a13 * (a12 * b3 - b2 * a13)) / det;
                    z = (a11 * (a22 * b3 - b2 * a23) - a12 * (a12 * b3 - b2 * a13) + b1 * (a12 * a23 - a22 * a13)) / det;
                }
                else
                {
                    var p = _ws.P;
                    x = (p[a * 3] + p[b * 3]) * 0.5;
                    y = (p[a * 3 + 1] + p[b * 3 + 1]) * 0.5;
                    z = (p[a * 3 + 2] + p[b * 3 + 2]) * 0.5;
                }

                var error = q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x +
                            q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9];
                var weight = _ws.W[a] + _ws.W[b];

                return weight > 0d ? Math.Max(0d, error) / weight : Math.Max(0d, error);
            }

            private void Push(int a, int b)
            {
                var ws = _ws;

                // The TIE-BREAK: on a flat or ruled region every candidate costs nothing, and the heap then
                // pops zero-cost edges in whatever order it holds them - long ones among them, whose midpoint
                // folds the thin triangles around it, so most of the pops were rejected and re-rejected. A
                // hair of the edge's squared length orders the ties shortest first, which coarsens a flat
                // surface evenly. 1e-9 of a square metre per square metre of edge is far under any stop
                // threshold this class uses (a 60 m edge adds 3.6e-6 m2 against a 1 m cube's 7.5e-5).
                var p = ws.P;
                double ex = p[a * 3] - p[b * 3], ey = p[a * 3 + 1] - p[b * 3 + 1], ez = p[a * 3 + 2] - p[b * 3 + 2];
                var cost = Cost(a, b, out var x, out var y, out var z) + TieBreak * (ex * ex + ey * ey + ez * ez);

                if (_hCount == ws.Heap.Length) Array.Resize(ref ws.Heap, ws.Heap.Length * 2);

                // The position goes in with the cost: it is valid for exactly as long as the two stamps are, so
                // a pop that finds the entry current does not solve the quadric a second time.
                var entry = new Workspace.Entry
                {
                    Cost = cost, X = x, Y = y, Z = z, A = a, B = b, StampA = ws.Stamp[a], StampB = ws.Stamp[b]
                };

                var heap = ws.Heap;
                var i = _hCount++;

                // sift up by moving parents down, then write the entry once
                while (i > 0)
                {
                    var parent = (i - 1) >> 1;
                    if (heap[parent].Cost <= cost) break;
                    heap[i] = heap[parent];
                    i = parent;
                }

                heap[i] = entry;
            }

            private void PopTop()
            {
                var heap = _ws.Heap;
                _hCount--;
                if (_hCount == 0) return;

                // sift the last entry down from the top by moving smaller children up, then write it once
                var last = heap[_hCount];
                var i = 0;

                while (true)
                {
                    var left = i * 2 + 1;
                    if (left >= _hCount) break;

                    var smallest = left + 1 < _hCount && heap[left + 1].Cost < heap[left].Cost ? left + 1 : left;
                    if (last.Cost <= heap[smallest].Cost) break;

                    heap[i] = heap[smallest];
                    i = smallest;
                }

                heap[i] = last;
            }

            // --- 5. and 6. collapse -------------------------------------------------------------------------------

            private bool Collapse()
            {
                var ws = _ws;
                var steps = 0;
                var sinceRefill = 0;

                while (true)
                {
                    if (_hCount == 0)
                    {
                        // An edge rejected once (the link condition, a fold, the fan) is dropped from the heap
                        // and only comes back when a neighbour collapses - so the heap can run dry short of the
                        // target with collapses still possible: a 32 x 32 cube stopped at 14 triangles. Refill
                        // from the live edges while the last round made progress; a round with none is done.
                        var goal = _relaxed ? _limit : _target;
                        if (_live <= goal || sinceRefill == 0) break;

                        sinceRefill = 0;
                        Refill();

                        if (_hCount == 0) break;
                    }

                    if (!_relaxed && _live <= _target) break;
                    if (_relaxed && _live <= _limit) break;

                    if ((++steps & 63) == 0 && OutOfTime()) return false;

                    var top = ws.Heap[0];
                    var a = top.A;
                    var b = top.B;
                    var stale = ws.Dead[a] || ws.Dead[b] || top.StampA != ws.Stamp[a] || top.StampB != ws.Stamp[b];
                    var queued = top.Cost;
                    double x = top.X, y = top.Y, z = top.Z;

                    PopTop();

                    if (stale) continue;

                    if (!_relaxed && queued > _maxError)
                    {
                        _result.StoppedByError = true;

                        if (_live <= _limit) break;

                        // The target is a budget: past the error limit, carry on - cheapest first still -
                        // until the hard limit. Rejected edges were dropped from the heap; every live edge
                        // goes back in so the relaxed pass can reach them.
                        _relaxed = true;
                        _result.Relaxed = true;
                        Refill();
                        sinceRefill = 1;
                        continue;
                    }

                    if (!LinkCondition(a, b, out var shared, out var fan))
                    {
                        _result.RejectedLink++;
                        continue;
                    }

                    if (!_relaxed && fan - 2 * shared > MaxFan)
                    {
                        _result.RejectedFans++;
                        continue;
                    }

                    // One pass over each fan for both the distance limit and the flip test, each face's normal
                    // computed once.
                    var verdict = Keeps(a, b, a, x, y, z);
                    if (verdict == 0) verdict = Keeps(a, b, b, x, y, z);

                    if (verdict == 1)
                    {
                        _result.RejectedFlips++;
                        continue;
                    }

                    if (verdict == 2)
                    {
                        _result.RejectedDistance++;
                        continue;
                    }

                    Merge(a, b, x, y, z);
                    _result.Collapses++;
                    sinceRefill++;

                    // Stale entries pile up - every collapse re-pushes the survivor's edges and strands the old
                    // ones - so the heap is rebuilt from the live edges when it holds several times as many
                    // entries as there are faces.
                    if (_hCount > 6 * _live + 4096) Refill();
                }

                return true;
            }

            /// <summary>Every live edge back into the heap, for the relaxed pass.</summary>
            private void Refill()
            {
                var ws = _ws;
                _hCount = 0;

                for (var v = 0; v < _vertices; v++)
                {
                    if (ws.Dead[v]) continue;

                    ws.Generation++;

                    foreach (var f in ws.Vf[v])
                    {
                        if (ws.FaceDead[f]) continue;

                        for (var k = 0; k < 3; k++)
                        {
                            var u = ws.F[f * 3 + k];
                            if (u <= v || ws.Mark[u] == ws.Generation) continue;

                            ws.Mark[u] = ws.Generation;
                            Push(v, u);
                        }
                    }
                }
            }

            /// <summary>The link condition, with its boundary form. a and b may share only as many
            /// neighbours as they share faces - more, and the collapse pinches the surface into a fin. And two
            /// BOUNDARY vertices joined by an INTERIOR edge may not collapse: that edge crosses the surface
            /// between two openings (or an opening and the outline), and collapsing it bridges them - the thin
            /// strip between a door and a window is exactly such an edge.</summary>
            private bool LinkCondition(int a, int b, out int shared, out int fan)
            {
                var ws = _ws;
                shared = 0;
                fan = 0;

                ws.Generation++;
                var marked = ws.Generation;

                foreach (var f in ws.Vf[a])
                {
                    if (ws.FaceDead[f]) continue;

                    fan++;
                    for (var k = 0; k < 3; k++) ws.Mark[ws.F[f * 3 + k]] = marked;
                }

                foreach (var f in ws.Vf[b])
                {
                    if (ws.FaceDead[f]) continue;

                    fan++;
                    if (ws.F[f * 3] == a || ws.F[f * 3 + 1] == a || ws.F[f * 3 + 2] == a) shared++;
                }

                if (shared == 2 && ws.Boundary[a] && ws.Boundary[b]) return false;

                ws.Generation++;
                var counted = ws.Generation;
                var common = 0;

                foreach (var f in ws.Vf[b])
                {
                    if (ws.FaceDead[f]) continue;

                    for (var k = 0; k < 3; k++)
                    {
                        var v = ws.F[f * 3 + k];
                        if (v == a || v == b) continue;

                        if (ws.Mark[v] == marked)
                        {
                            ws.Mark[v] = counted;
                            common++;
                        }
                    }
                }

                return common <= shared;
            }

            /// <summary>
            /// Whether moving <paramref name="moving"/> to (x, y, z) keeps every surviving face around it good:
            /// 0 when it does, 1 when a face degenerates, turns by more than 60 degrees from its current normal
            /// or by more than 90 from its ORIGINAL one (the flip test, only while it is on - see Decimate - bar
            /// the degenerate case, refused always), 2 when the new point is further than the distance limit
            /// from a face's plane (only while the error limit is in force: the mean error's blind spot, one
            /// feature moved a long way). ONE pass over the fan, each face's normal computed once, no
            /// allocation.
            /// </summary>
            private int Keeps(int a, int b, int moving, double x, double y, double z)
            {
                var ws = _ws;
                var p = ws.P;

                foreach (var f in ws.Vf[moving])
                {
                    if (ws.FaceDead[f]) continue;

                    int c0 = ws.F[f * 3], c1 = ws.F[f * 3 + 1], c2 = ws.F[f * 3 + 2];

                    var hasA = c0 == a || c1 == a || c2 == a;
                    var hasB = c0 == b || c1 == b || c2 == b;
                    if (hasA && hasB) continue;

                    if (!Normal(c0, c1, c2, out var ox, out var oy, out var oz)) continue;

                    if (!_relaxed)
                    {
                        var d = -(ox * p[c0 * 3] + oy * p[c0 * 3 + 1] + oz * p[c0 * 3 + 2]);
                        if (Math.Abs(ox * x + oy * y + oz * z + d) > _maxDistance) return 2;
                    }

                    double p0x = c0 == moving ? x : p[c0 * 3], p0y = c0 == moving ? y : p[c0 * 3 + 1], p0z = c0 == moving ? z : p[c0 * 3 + 2];
                    double p1x = c1 == moving ? x : p[c1 * 3], p1y = c1 == moving ? y : p[c1 * 3 + 1], p1z = c1 == moving ? z : p[c1 * 3 + 2];
                    double p2x = c2 == moving ? x : p[c2 * 3], p2y = c2 == moving ? y : p[c2 * 3 + 1], p2z = c2 == moving ? z : p[c2 * 3 + 2];

                    double ux = p1x - p0x, uy = p1y - p0y, uz = p1z - p0z;
                    double vx = p2x - p0x, vy = p2y - p0y, vz = p2z - p0z;
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;

                    var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (length < 1e-12) return 1;

                    if (!_rejectFlips) continue;

                    if ((nx * ox + ny * oy + nz * oz) / length < FlipCosine) return 1;
                    if (nx * ws.F0[f * 3] + ny * ws.F0[f * 3 + 1] + nz * ws.F0[f * 3 + 2] <= 0d) return 1;
                }

                return 0;
            }

            private void Merge(int a, int b, double x, double y, double z)
            {
                var ws = _ws;

                ws.P[a * 3] = x; ws.P[a * 3 + 1] = y; ws.P[a * 3 + 2] = z;

                for (var i = 0; i < 10; i++) ws.Q[a * 10 + i] += ws.Q[b * 10 + i];
                ws.W[a] += ws.W[b];
                ws.Boundary[a] |= ws.Boundary[b];

                var fa = ws.Vf[a];

                foreach (var f in ws.Vf[b])
                {
                    if (ws.FaceDead[f]) continue;

                    var hasA = ws.F[f * 3] == a || ws.F[f * 3 + 1] == a || ws.F[f * 3 + 2] == a;

                    if (hasA)
                    {
                        ws.FaceDead[f] = true;
                        _live--;
                        continue;
                    }

                    for (var k = 0; k < 3; k++)
                        if (ws.F[f * 3 + k] == b) ws.F[f * 3 + k] = a;

                    fa.Add(f);
                }

                ws.Dead[b] = true;
                ws.Vf[b].Clear();
                ws.Stamp[a]++;
                ws.Stamp[b]++;

                // a's own list without the faces that died - by hand, no delegate
                var write = 0;
                for (var read = 0; read < fa.Count; read++)
                    if (!ws.FaceDead[fa[read]]) fa[write++] = fa[read];
                fa.RemoveRange(write, fa.Count - write);

                ws.Generation++;

                foreach (var f in fa)
                    for (var k = 0; k < 3; k++)
                    {
                        var v = ws.F[f * 3 + k];
                        if (v == a || ws.Mark[v] == ws.Generation) continue;

                        ws.Mark[v] = ws.Generation;
                        Push(Math.Min(a, v), Math.Max(a, v));
                    }
            }

            // --- output ---------------------------------------------------------------------------------------------

            private void Output()
            {
                var ws = _ws;
                ws.Map = Workspace.Grow(ws.Map, _vertices);
                for (var i = 0; i < _vertices; i++) ws.Map[i] = -1;

                var positions = new List<float>();
                var triangles = new List<int>(_live * 3);

                for (var f = 0; f < _faces; f++)
                {
                    if (ws.FaceDead[f]) continue;

                    for (var k = 0; k < 3; k++)
                    {
                        var v = ws.F[f * 3 + k];

                        if (ws.Map[v] < 0)
                        {
                            ws.Map[v] = positions.Count / 3;
                            positions.Add((float)ws.P[v * 3]);
                            positions.Add((float)ws.P[v * 3 + 1]);
                            positions.Add((float)ws.P[v * 3 + 2]);
                        }

                        triangles.Add(ws.Map[v]);
                    }
                }

                _result.Positions = positions.ToArray();
                _result.Triangles = triangles.ToArray();
            }
        }
    }

    /// <summary>
    /// The building loop's control rules, Unity-free so the harness can drive a model of the loop through
    /// the SHIPPED predicates (second review, C1). The rule the old loop broke: once it was stopped, it
    /// took nothing more - not even the coarse levels a fallback had queued - and yet waited for that queue
    /// to empty before it would end, so a fallback queued after the hard cap held the scene until the raid
    /// ended. Now: the queue is drained past the hard cap until the drain deadline, abandoned (and counted)
    /// at the deadline, at a format cap or on the capture's abort, and the exit never waits on it once it is
    /// abandoned.
    /// </summary>
    internal static class BuildingLoop
    {
        /// <summary>Nothing to take.</summary>
        internal const int None = 0;

        /// <summary>The next queued coarse level.</summary>
        internal const int FromExtra = 1;

        /// <summary>The next candidate of the ordered list.</summary>
        internal const int FromList = 2;

        /// <summary>Where the next candidate comes from: the fallback queue first, while it is not
        /// abandoned - past the hard cap too; the list only before the hard cap.</summary>
        /// <param name="stopped">A format cap was reached.</param>
        /// <param name="pastHard">The hard cap has passed.</param>
        /// <param name="abandoned">The drain deadline has passed, or the capture asked to abort.</param>
        /// <param name="extra">Coarse levels queued.</param>
        /// <param name="next">The list's next index.</param>
        /// <param name="count">The list's length.</param>
        internal static int Next(bool stopped, bool pastHard, bool abandoned, int extra, int next, int count)
        {
            if (stopped || abandoned) return None;
            if (extra > 0) return FromExtra;
            if (!pastHard && next < count) return FromList;
            return None;
        }

        /// <summary>Whether the queue must be emptied, uncounted work abandoned, now.</summary>
        /// <param name="stopped">A format cap was reached.</param>
        /// <param name="pastDrain">The drain deadline has passed.</param>
        /// <param name="abort">The capture asked the build to stop.</param>
        internal static bool Abandon(bool stopped, bool pastDrain, bool abort) => stopped || pastDrain || abort;

        /// <summary>Whether the loop is finished: on abort at once; otherwise when nothing is in flight,
        /// nothing is queued, and the list is done or may no longer be read.</summary>
        /// <param name="flights">Flights in the air.</param>
        /// <param name="extra">Coarse levels queued.</param>
        /// <param name="next">The list's next index.</param>
        /// <param name="count">The list's length.</param>
        /// <param name="stopped">A format cap was reached.</param>
        /// <param name="pastHard">The hard cap has passed.</param>
        /// <param name="abort">The capture asked the build to stop.</param>
        internal static bool Done(int flights, int extra, int next, int count, bool stopped, bool pastHard, bool abort) =>
            abort || (flights == 0 && extra == 0 && (next >= count || stopped || pastHard));

        /// <summary>Whether a source may go now (second review, H2): the in-flight total counts it, and one
        /// larger than the cap on its own goes only when nothing else is in the air.</summary>
        /// <param name="inFlight">Source triangles in the air.</param>
        /// <param name="source">This source's triangles.</param>
        /// <param name="flights">Flights in the air.</param>
        /// <param name="cap">The in-flight cap.</param>
        internal static bool Fits(long inFlight, long source, int flights, long cap) =>
            flights == 0 || inFlight + source <= cap;
    }

    /// <summary>
    /// The map's triangle budget as a ledger (stage V review, H4): what is STORED, what is RESERVED for the
    /// buildings the area budget planned and has not reached yet, and what is PENDING for buildings on a
    /// worker - and the headroom, the cap minus all three, which is the only thing an overshoot may be paid
    /// from. A building releases its own reservation when it is reached, is admitted with a limit no larger
    /// than the headroom (so its own target is always there, and anything past it is unreserved budget),
    /// and settles with what it actually stored. Stored + reserved + pending never passes the cap while
    /// every settle is at most its limit plus the headroom at the time - which is what Apply checks before
    /// storing a source as it is.
    ///
    /// Unity-free so the harness can run a Customs-sized list through it.
    /// </summary>
    internal sealed class BudgetLedger
    {
        /// <summary>A ledger over this many triangles.</summary>
        /// <param name="cap">The map's cap.</param>
        internal BudgetLedger(long cap)
        {
            Cap = cap;
        }

        internal readonly long Cap;
        internal long Stored;
        internal long Reserved;
        internal long Pending;

        /// <summary>What nobody was promised: the cap less everything stored, reserved and pending.</summary>
        internal long Headroom => Cap - Stored - Reserved - Pending;

        /// <summary>Sets aside a planned building's share.</summary>
        /// <param name="triangles">Its target, or its source when that is smaller.</param>
        internal void Reserve(long triangles)
        {
            if (triangles > 0) Reserved += triangles;
        }

        /// <summary>Hands a building's reservation back to the headroom, the moment it is reached.</summary>
        /// <param name="triangles">What was reserved for it.</param>
        internal void Release(long triangles)
        {
            if (triangles > 0) Reserved = Math.Max(0, Reserved - triangles);
        }

        /// <summary>Takes a limit from the headroom as pending. False, taking nothing, when it does not fit.</summary>
        /// <param name="limit">The building's hard limit.</param>
        internal bool Admit(long limit)
        {
            if (limit <= 0 || limit > Headroom) return false;

            Pending += limit;
            return true;
        }

        /// <summary>Closes a building: its pending limit off, what it stored on. A store over the limit is
        /// the caller's to have checked against <see cref="Headroom"/> first.</summary>
        /// <param name="limit">The limit it was admitted with.</param>
        /// <param name="stored">Triangles it stored (0 for none).</param>
        internal void Settle(long limit, long stored)
        {
            Pending = Math.Max(0, Pending - limit);
            if (stored > 0) Stored += stored;
        }

        /// <summary>
        /// The hard limit a building is admitted with: its source when that is inside its target; else
        /// target x <paramref name="factor"/> (the decimator's overshoot allowance), capped by its source -
        /// either one cut to the headroom when the headroom is smaller, and 0 when the headroom does not
        /// hold even the smaller of its source and <paramref name="least"/>.
        /// </summary>
        /// <param name="source">Its source triangles.</param>
        /// <param name="target">Its area target.</param>
        /// <param name="headroom">The ledger's headroom, its own reservation already released into it.</param>
        /// <param name="least">The smallest building worth storing.</param>
        /// <param name="factor">The overshoot allowance.</param>
        internal static int Limit(long source, int target, long headroom, int least, double factor)
        {
            if (source <= 0 || headroom <= 0) return 0;

            var want = source <= target ? source : Math.Min(source, (long)Math.Ceiling(Math.Max(1, target) * factor));

            if (want <= headroom) return (int)Math.Min(int.MaxValue, want);

            return headroom >= Math.Min(source, least) ? (int)Math.Min(int.MaxValue, headroom) : 0;
        }
    }

    /// <summary>
    /// Stage V's triangle budget, by AREA: each building's target is its footprint times
    /// <see cref="TrianglesPerSquareMetre"/>, held to [<see cref="MinTriangles"/>,
    /// <see cref="MaxTrianglesPerBuilding"/>]; and when what the map would store - each building's
    /// target, or its source when that is smaller, since a source under its target is stored as it is -
    /// adds up to more than the cap, every target is scaled by ONE factor (floored at MinTriangles),
    /// the largest factor that fits, found by bisection. One factor rather than largest-first, because
    /// largest-first is what kept 276 of Customs' 12,906 candidates and dropped every mid-size structure.
    ///
    /// Unity-free so the harness proves the scaling sums to the cap on the shipped assembly.
    /// </summary>
    internal static class AreaBudget
    {
        internal const double TrianglesPerSquareMetre = 6.0;
        internal const int MinTriangles = 24;
        internal const int MaxTrianglesPerBuilding = 60_000;

        /// <summary>The targets, and the factor they were scaled by (1 when the map fits).</summary>
        /// <param name="footprints">Each building's footprint in square metres.</param>
        /// <param name="sources">Each building's source triangle count.</param>
        /// <param name="cap">The map's triangle cap.</param>
        /// <param name="scale">The factor applied to every target.</param>
        internal static int[] Targets(double[] footprints, long[] sources, long cap, out double scale)
        {
            var n = footprints.Length;
            var basis = new double[n];

            for (var i = 0; i < n; i++)
                basis[i] = Math.Min(MaxTrianglesPerBuilding,
                    Math.Max(MinTriangles, Math.Max(0d, footprints[i]) * TrianglesPerSquareMetre));

            scale = 1d;

            if (Stored(basis, sources, 1d) > cap)
            {
                double lo = 0d, hi = 1d;

                for (var step = 0; step < 50; step++)
                {
                    var mid = (lo + hi) * 0.5;
                    if (Stored(basis, sources, mid) <= cap) lo = mid;
                    else hi = mid;
                }

                scale = lo;
            }

            var targets = new int[n];
            for (var i = 0; i < n; i++) targets[i] = Target(basis[i], scale);

            return targets;
        }

        /// <summary>What the map stores at a scale: each building's scaled target, or its source when
        /// that is smaller.</summary>
        /// <param name="basis">The unscaled targets.</param>
        /// <param name="sources">The source triangle counts.</param>
        /// <param name="scale">The factor.</param>
        internal static long Stored(double[] basis, long[] sources, double scale)
        {
            var total = 0L;

            for (var i = 0; i < basis.Length; i++) total += Math.Min(sources[i], Target(basis[i], scale));

            return total;
        }

        private static int Target(double basis, double scale) => Math.Max(MinTriangles, (int)(basis * scale));
    }
}
