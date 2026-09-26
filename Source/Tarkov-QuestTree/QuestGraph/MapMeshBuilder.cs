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
    /// its budget) and at the map's triangle cap - derived per build from what the buildings need and
    /// what this machine's memory holds (<see cref="ApplyBudget"/>) - and says in its log line what it
    /// cut.
    ///
    /// MEMORY. The working set is deliberately small: the two NativeArrays a ray chunk needs are
    /// <see cref="Allocator.TempJob"/>, sized to one chunk, and disposed in a finally per chunk (a
    /// leaked TempJob allocation is a console warning every frame for the rest of the session); a
    /// band holds one float per cell while the y range is being measured and drops it the moment the
    /// cells are quantised; and a building's vertices go into ushort lists as they are read rather
    /// than being kept as floats. The peak estimate is in the info line at the end.
    /// </summary>
    internal static class MapMeshBuilder
    {
        // --- the constants phase 3-0 settled ------------------------------------------------------

        /// <summary>The relief cell every extent gets when it fits the format's band cap at it: one metre
        /// (WP7; was a fixed 2 m). Rollback: 2f.</summary>
        internal const float PreferredReliefCellMetres = 1f;

        /// <summary>The step the cell grows by when an extent does not fit <see cref="MapMeshFile.MaxCellsPerBand"/>
        /// at the preferred cell: half a metre at a time, so a larger map degrades by as little as it must.</summary>
        internal const float ReliefCellStepMetres = 0.5f;

        /// <summary>
        /// The relief cell for an extent, in metres - derived, never a per-map number:
        /// max(preferred, sqrt(area / band cap) rounded up to the step), then one step more while
        /// ceil(w / c) x ceil(h / c) is still over the band cap (the ceil on each axis can push the product past
        /// area / c^2). Worked examples: 1118x539 m -> 1 m; 2.5x2.5 km -> 1.5 m; 4x4 km -> 2 m. Unity-free, so the
        /// harness and tools/check-capture.py hold the same rule. A span that is not a positive number gets the
        /// preferred cell, and Prepare refuses the grid as before.
        /// </summary>
        /// <param name="spanX">The extent's width, metres.</param>
        /// <param name="spanZ">The extent's depth, metres.</param>
        internal static float ReliefCellFor(double spanX, double spanZ)
        {
            if (!(spanX > 0d) || !(spanZ > 0d) || double.IsInfinity(spanX) || double.IsInfinity(spanZ))
                return PreferredReliefCellMetres;

            var area = spanX * spanZ;
            var c = Math.Max(PreferredReliefCellMetres,
                Math.Ceiling(Math.Sqrt(area / MapMeshFile.MaxCellsPerBand) / ReliefCellStepMetres) * ReliefCellStepMetres);

            while (Math.Ceiling(spanX / c) * Math.Ceiling(spanZ / c) > MapMeshFile.MaxCellsPerBand)
                c += ReliefCellStepMetres;

            return (float)c;
        }

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

        /// <summary>The size over which an untextured Standard/Unlit renderer is a helper volume, metres.</summary>
        private const float HiddenVolumeMetres = 10f;

        /// <summary>What a helper volume's name says.</summary>
        private static readonly string[] HelperNameMarks = { "Cube", "Portal", "Stencil", "Volume" };

        /// <summary>Hidden renderers' paths logged per scene root, and the roots sampled (WP8 D6: per root rather than
        /// the first ten, which were all IndoorTrigger volumes because of FindObjectsOfType's order).</summary>
        private const int HiddenSamplesPerRoot = 2;

        private const int HiddenSampleRoots = 8;

        /// <summary>WP8 (D6) rollback: a switched-off renderer that a runtime culling system owns (Request.GameCulled)
        /// is read like any other. False restores the pre-WP8 filter, where every switched-off renderer was hidden.
        /// Static readonly so the choice is not a constant the compiler folds.</summary>
        internal static readonly bool IncludeGameCulled = true;

        /// <summary>LOD groups mapped a frame (screen defect 4's duplicates).</summary>
        private const int LodGroupsPerFrame = 2_000;

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

        /// <summary>
        /// The most triangles any build stores, whatever the machine: half of <see cref="MapMeshFile.MaxTriangles"/>
        /// (40 M), so the file's own bound is never what refuses a capture. The map's cap is derived per build
        /// (<see cref="ApplyBudget"/>): min(ceil(demand / <see cref="BudgetShare"/>), <see cref="MemoryCeiling()"/>,
        /// this). Rollback: 3,000,000 (the pre-WP7 fixed cap).
        /// </summary>
        internal const long BuilderAbsoluteTriangles = 20_000_000;

        /// <summary>The least the memory ceiling ever is: the pre-WP7 fixed cap, so no machine gets less than
        /// before. Rollback: 3,000,000.</summary>
        internal const long MemoryFloorTriangles = 3_000_000;

        /// <summary>Bytes a stored triangle costs the build in the raid: 12 of indices and 4 of pending material,
        /// ~16 of vertex (x, z, float height, float UV at about one vertex a triangle), ~24 of atlas split copies
        /// and ~10 serialised - about 64.</summary>
        internal const long BytesPerTriangleInRaid = 64;

        /// <summary>Bytes a triangle costs the 3D view on the GPU: 12 of indices plus ~1.2 vertices at 32 bytes,
        /// ~50, 64 with margin.</summary>
        internal const long BytesPerTriangleOnGpu = 64;

        /// <summary>The share of RAM the build may fill (a sixteenth - the raid holds most of it) and of VRAM
        /// the menu view may (an eighth). The only tuning numbers of the memory ceiling.</summary>
        internal const long RamShare = 16;
        internal const long VramShare = 8;

        /// <summary>MAIN THREAD (SystemInfo). The most triangles this machine builds and draws. See
        /// <see cref="MemoryCeiling(long, long)"/>.</summary>
        internal static long MemoryCeiling() =>
            MemoryCeiling(SystemInfo.systemMemorySize, SystemInfo.graphicsMemorySize);

        /// <summary>
        /// The memory ceiling from the machine's RAM and VRAM in MB: max(<see cref="MemoryFloorTriangles"/>,
        /// min(RAM / 16 / 64 B, VRAM / 8 / 64 B)), the VRAM term dropped when the card reports none. 16 GB / 8 GB
        /// -> 16,777,216; 8 GB / 4 GB -> 8,388,608; a card reporting 512 MB -> the floor. Unity-free.
        /// </summary>
        /// <param name="ramMb">SystemInfo.systemMemorySize.</param>
        /// <param name="vramMb">SystemInfo.graphicsMemorySize; zero or less when unknown.</param>
        internal static long MemoryCeiling(long ramMb, long vramMb)
        {
            var ram = Math.Max(0L, ramMb) << 20;
            var vram = vramMb > 0 ? vramMb << 20 : 0L;
            var byRam = ram / RamShare / BytesPerTriangleInRaid;
            var byVram = vram > 0 ? vram / VramShare / BytesPerTriangleOnGpu : long.MaxValue;

            return Math.Max(MemoryFloorTriangles, Math.Min(byRam, byVram));
        }

        /// <summary>The map's triangle cap (D6): min(ceil(demand / <see cref="BudgetShare"/>), the memory ceiling,
        /// <see cref="BuilderAbsoluteTriangles"/>) - with a FLOOR (PART-03 review): the headroom the pre-WP7 build
        /// had. Before WP7 the cap was a fixed 3 M and the headroom 3 M less what the old rule reserved; the
        /// derived cap alone leaves a tenth of the demand, which on a small map (demand under ~2.7 M) is LESS
        /// than before, and a source over the decimation guard with no coarse level is stored as it is only from
        /// headroom - so it was clustered where the old build kept it whole (Q2 broken, and the 4c rollback not
        /// the old cap). The floor is <see cref="MemoryFloorTriangles"/> plus (demand - what the old rule would
        /// reserve), which makes the starting headroom never smaller than the old build's; it never lowers a
        /// target (0.9 x cap >= demand still), never passes the memory ceiling (the floor is 3 M, which the
        /// ceiling is at least), and costs only unused ledger room, since stored triangles never exceed the
        /// sources. Unity-free.</summary>
        /// <param name="demand">What the buildings need (<see cref="AreaBudget.Demand"/>).</param>
        /// <param name="legacyReserved">What the pre-WP7 rule would reserve on the same list: the sum over the
        /// buildings of min(source, legacy target).</param>
        /// <param name="memoryCeiling">This machine's <see cref="MemoryCeiling()"/>.</param>
        internal static long CapFor(long demand, long legacyReserved, long memoryCeiling)
        {
            var d = Math.Max(0L, demand);
            var derived = (long)Math.Ceiling(d / BudgetShare);
            var floor = MemoryFloorTriangles + Math.Max(0L, d - Math.Max(0L, legacyReserved));
            return Math.Min(Math.Max(derived, floor), Math.Min(memoryCeiling, BuilderAbsoluteTriangles));
        }

        /// <summary>CapFor with the old rule reserving the whole demand: the plain floor of 3 M.</summary>
        internal static long CapFor(long demand, long memoryCeiling) => CapFor(demand, demand, memoryCeiling);

        /// <summary>The share of the map's cap the area budget plans with. The rest is
        /// never reserved: it is the headroom a decimation's overshoot (up to its hard limit), a group's
        /// coarse fallback or a source stored as it is is paid from, so the buildings at the end of the list
        /// keep what the budget promised them.</summary>
        internal const double BudgetShare = 0.9;

        /// <summary>The most triangles a building's MOST DETAILED level may have and still be the source
        /// (stage V; user, 2026-09-23). Above it the building is read from the game's coarse level instead
        /// - the stage-U rule - and counted in the log line as over the source guard.</summary>
        internal const int MaxSourceTriangles = 1_000_000;

        /// <summary>WP8 (D3) rollback: the LEVEL LADDER - a building that cannot be stored within its limit takes the
        /// strict decimation over budget, then its source as it is, then its group's NEXT level (LOD1 before LOD2),
        /// never straight to the coarsest. False restores the pre-WP8 pair (level 0 and the coarsest real level) and
        /// Apply's pre-WP8 order. Static readonly so the choice is not a constant the compiler folds.</summary>
        internal static readonly bool LevelLadder = true;

        /// <summary>WP8 (D3): the most a strict decimation over its limit, or a source stored as it is before a
        /// coarser level, may have: this times the building's limit.</summary>
        internal const int OverBudgetMaxFactor = 4;

        /// <summary>WP8 (D3): how a building was stored, the low part of its GRADE (see <see cref="GradeFor"/>):
        /// within its limit (decimated or not), a strict decimation over its limit from the pool, its source as it
        /// is over its limit, or clustered.</summary>
        internal const int GradeWithin = 0;

        internal const int GradeOverBudget = 1;
        internal const int GradeAsIs = 2;
        internal const int GradeClustered = 3;

        /// <summary>
        /// WP8 (D3): a stored building's GRADE - lower is better: <c>sub</c> (0..3, see <see cref="GradeWithin"/>) for a
        /// building read from its group's level 0 (or with no group), <c>10 + 4 x lod + sub</c> for one read from level
        /// <c>lod</c> &gt;= 1. Held in memory on <see cref="MapMeshFile.Building.Grade"/> beside
        /// <see cref="MapMeshFile.Building.GroupKey"/>; not serialised (v3 is frozen). THE RULE WP2's accumulation must
        /// keep: within one campaign, for each GroupKey, the stops' buildings with the LOWEST grade win WHOLESALE and
        /// every building of that group from a worse-graded stop is removed - so two levels of one group are never
        /// stored together (their Keys differ, because their renderers do). Unity-free.
        /// </summary>
        /// <param name="lod">The LOD level the building was read from (0 for none).</param>
        /// <param name="sub">How it was stored.</param>
        internal static byte GradeFor(int lod, int sub)
        {
            sub = Math.Max(0, Math.Min(3, sub));
            return (byte)(lod <= 0 ? sub : Math.Min(255, 10 + 4 * lod + sub));
        }

        /// <summary>
        /// WP8 (D3): the level a group moves to when its current source cannot be stored - the decision of
        /// EnqueueNextLevel with the Unity half (reading the level's renderers) behind two callbacks, so the harness
        /// can hold it to its rules. -1 when the group may not move: something of the current level is stored or in
        /// flight (Committed, the "never both levels" invariant), or the failing candidate is not its group's
        /// current source. Levels are tried finest first after the current one; a level that holds the failing
        /// renderer itself stops the walk (review F01: the shared-renderer authoring, where it is claimed and would
        /// be lost with every sibling), and a level none of whose renderers would pass the read gate is skipped
        /// (F01's size checks). Unity-free.
        /// </summary>
        /// <param name="current">The group's current level (an index into its ladder).</param>
        /// <param name="levels">The ladder's length.</param>
        /// <param name="committed">Renderers of the current level stored or in flight.</param>
        /// <param name="currentSource">Whether the failing candidate is of the current level.</param>
        /// <param name="holdsFailing">Whether ladder level k lists the failing renderer.</param>
        /// <param name="readable">How many of ladder level k's renderers would pass the read gate.</param>
        internal static int NextLevel(int current, int levels, int committed, bool currentSource, Func<int, bool> holdsFailing,
            Func<int, int> readable)
        {
            if (committed > 0 || !currentSource) return -1;

            for (var next = current + 1; next < levels; next++)
            {
                if (holdsFailing(next)) return -1;
                if (readable(next) > 0) return next;
            }

            return -1;
        }

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

        /// <summary>Milliseconds a cluster flight may take at the least.</summary>
        internal const double ClusterMs = 200d;

        /// <summary>A cluster flight's cap for a source of this size (review F20): at least ClusterMs, and 2
        /// microseconds a source triangle a pass for several passes above it - a flat 200 ms dropped a 600 k
        /// source whose first pass did not fit, which "never dropped" promised it would not.</summary>
        /// <param name="triangles">The source's triangles.</param>
        internal static double ClusterCapMs(long triangles) => Math.Max(ClusterMs, triangles * 0.002 * 6);

        /// <summary>Seconds past the hard cap the queued coarse levels are still read before they are
        /// abandoned - the bound on the post-cap drain.</summary>
        internal const double DrainSeconds = 8d;

        /// <summary>Milliseconds of main-thread work the building loop does before it yields a frame.</summary>
        private const double FrameBudgetMs = 8d;

        /// <summary>Seconds the capture plans for: floors, relief, buildings and side views together (second
        /// review, H1) - a planning budget, not the campaign's wait (MapCapture.WorstCaseSeconds). The building
        /// phase's soft cap is what the floors' and relief's measured seconds and the sides' estimate leave of
        /// it, never under
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

        /// <summary>Stage W's atlas: a repeat's largest side, the most repeats a tiled use may take per axis and
        /// the pixels those repeats may add up to, the border each tile keeps, the flat tile's side, and the
        /// slack a UV may have past a whole repeat before it needs another.</summary>
        private const int AtlasTileMax = MapMeshFile.AtlasTileMax;

        /// <summary>Seconds the capture sets aside for the atlas out of its budget (taken off the building
        /// phase's), and the ONE cap over the whole atlas phase - measuring, packing, capturing, filling, handing
        /// pages to the encoders and mapping the buildings (stage W review, H1). Past it the atlas is abandoned.</summary>
        internal const double AtlasSecondsReserve = 40d;

        internal const double AtlasSecondsCap = AtlasSecondsReserve;

        /// <summary>The share of the atlas cap textures are captured in; past it the tiles still to capture take
        /// their material's flat colour, leaving the rest of the cap for the flat tiles, the last pages and the
        /// buildings' UVs.</summary>
        private const double AtlasCaptureShare = 0.75;

        /// <summary>Block rows copied into a page a step.</summary>
        private const int AtlasRowsPerStep = 128;

        /// <summary>A static-batch member's UVs are read when its own vertex range is at least 1/this of the batch.</summary>
        private const int StaticBatchUvShare = 4;

        /// <summary>The gutter round every atlas tile, filled with copies of the tile's edge texels. 2 px (stage X):
        /// the viewer cuts each tile out of its page into a texture of its own with wrapMode Repeat and its own
        /// mips, so no mip level of the page is ever sampled across tiles - the 16 px gutter stage W's
        /// mipmapped pages needed is gone; 2 px covers bilinear filtering at the cut.</summary>
        internal const int AtlasPadding = 2;
        private const int AtlasFlatPixels = 4;
        private const int AtlasAveragePixels = 8;
        private const float AtlasTileSlack = 0.05f;

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

            /// <summary>Stage W: the file atlas page n is streamed to by its encoder (a worker) - the capture's
            /// staged name plus ".part", renamed on the main thread once the encode is done. Null: no atlas.</summary>
            internal Func<int, string> AtlasPartPath;

            /// <summary>Whether the capture's culling scan worked, so "switched off" after the hold means hidden
            /// (stage W review, M2). False keeps the old behaviour: no renderer is skipped for being off.</summary>
            internal bool CullingKnown = true;

            /// <summary>WP8 (D6): the baked-LOD PROXIES - every ScreenDistanceSwitcher's merged, auto-simplified hull of
            /// its area (GetBakedLodRenderers). Never candidates, whatever their state: their detail is read instead.
            /// Null for none.</summary>
            internal HashSet<Renderer> ProxyRenderers;

            /// <summary>WP8 (D6): renderers a runtime culling system owns - a switcher's content (proxies excluded) and
            /// every Perfect Culling bake group's renderers. Switched off, they are hidden FROM THE PLAYER'S POSITION
            /// (occlusion or distance), not from the map, so they are read like any other. Null for none.</summary>
            internal HashSet<Renderer> GameCulled;

            /// <summary>WP8 (D6): the part of <see cref="GameCulled"/> that came from the occlusion bake groups, for the
            /// hidden line's split. Null for none.</summary>
            internal HashSet<Renderer> OcclusionCulled;
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

            /// <summary>Stage W: the atlas pages whose encodes were started, in page order - still running, possibly,
            /// when the build returns. The capture waits for them after releasing the scene and settles them
            /// (SettleAtlas) before it serialises the mesh. Null or empty: no atlas.</summary>
            internal List<AtlasPageJob> AtlasPages;
        }

        /// <summary>One atlas page handed to its encoder: the page, its tiles, the file it streams to, and the
        /// encode.</summary>
        internal sealed class AtlasPageJob
        {
            internal int Page;
            internal int Tiles;
            internal string PartPath;
            internal Task<AtlasPng.Encoded> Encode;
        }

        /// <summary>One atlas page that finished: its file and what the meta records.</summary>
        internal sealed class AtlasPageDone
        {
            internal int Page;
            internal int Tiles;
            internal string PartPath;
            internal long Bytes;
            internal string Sha256;
        }

        /// <summary>One building's atlas mapping, held beside it until the phase completes (ApplyAtlas).</summary>
        private sealed class AtlasMapped
        {
            internal uint[] Indices;
            internal ushort[] X;
            internal ushort[] Z;
            internal float[] YMetres;
            internal ushort[] U;
            internal ushort[] V;
            internal List<MapMeshFile.AtlasRange> Ranges;
            internal long Triangles;
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

                // Every LOD group's levels, mapped renderer -> group, BEFORE the budget asks which group a
                // renderer is in: a group is not always an ancestor of its renderers (a sibling holds it),
                // and a child under a group is not always one of its levels (always drawn). The nearest-
                // parent lookup got both wrong - the second review of Customs found 1,243 stacks of
                // same-box buildings, 540 k triangles, many of them LOD levels of one object stored
                // together.
                if (Step(job, "the LOD groups", () => job.LodGroups = UnityEngine.Object.FindObjectsOfType<LODGroup>(true)))
                {
                    yield return null;

                    var mapped = true;

                    while (job.LodGroups != null && job.LodMapped < job.LodGroups.Length)
                    {
                        if (!Step(job, "the LOD map", () => MapLods(job)))
                        {
                            mapped = false;
                            break;
                        }

                        yield return null;
                    }

                    job.LodMapComplete = mapped && job.LodGroups != null;
                    job.LodGroups = null;
                }
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
                        if (job.DecimationStopped &&
                            candidate.SourceTriangles > TargetFor(job, candidate) &&
                            EnqueueNextLevel(job, candidate))
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

                        // M1: a read takes frames, and a sibling's fallback may have moved the group to a
                        // coarser level meanwhile (WP8: any level, queued ones too) - a read that is no longer its
                        // group's source is discarded, or the building would be stored twice.
                        if (source != null && !IsSource(job, candidate))
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
                Step(job, "the hidden renderers' line", () => ReportHidden(job));
            }

            // --- stage W: the atlas -----------------------------------------------------------------------
            //
            // After the buildings (it maps what was stored) and inside the same hold (the textures are the
            // scene's). A few tiles a frame; each page encoded on a worker.

            if (job.WantsBuildings && job.File != null && job.File.Buildings.Count > 0 && job.Request.AtlasPartPath != null &&
                !job.Request.Abort)
            {
                job.FrameClock.Restart();
                var atlasRun = BuildAtlas(job, result);

                try
                {
                    while (true)
                    {
                        var more = false;
                        if (!Step(job, "the atlas", () => more = atlasRun.MoveNext()) || !more) break;

                        yield return atlasRun.Current;
                        job.FrameClock.Restart();

                        if (job.Request.Abort) break;
                    }
                }
                finally
                {
                    (atlasRun as IDisposable)?.Dispose();

                    if (job.AtlasScratch != null)
                    {
                        UnityEngine.Object.Destroy(job.AtlasScratch);
                        job.AtlasScratch = null;
                    }
                }

                Step(job, "the textures' log line", () => ReportAtlas(job));
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

            /// <summary>The map's triangle budget - see <see cref="BudgetLedger"/>. Replaced by ApplyBudget with one over
            /// the derived cap; until then (and if the budget step fails) the pre-WP7 floor.</summary>
            internal BudgetLedger Ledger = new BudgetLedger(MemoryFloorTriangles);

            /// <summary>The map's triangle cap, derived by ApplyBudget (D6): what StoreWorld holds the total to.</summary>
            internal long Cap = MemoryFloorTriangles;

            /// <summary>What the budget's sources need (D4), this machine's memory ceiling (D5) and what it came
            /// from, for the building line.</summary>
            internal long Demand;
            internal long MemoryCeiling;
            internal int RamMb;
            internal int VramMb;

            /// <summary>The pre-WP7 rule's scale on the same list - the floor every later target keeps (D3).</summary>
            internal double LegacyScale = 1d;

            /// <summary>Budgeted buildings whose target is their pre-WP7 floor rather than their surface target.</summary>
            internal int HeldAtFloor;

            /// <summary>Stored buildings' world-box surface and their measured triangle area, m2 - the evidence
            /// for whether the box basis over- or under-states real surfaces on a map.</summary>
            internal double BoxSurface;
            internal double MeasuredSurface;

            /// <summary>Buildings left untextured because the atlas split would have passed a vertex cap.</summary>
            internal int SplitOverCap;

            /// <summary>The relief cell this build derived from its extent (<see cref="ReliefCellFor"/>).</summary>
            internal float CellMetres = PreferredReliefCellMetres;

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

            /// <summary>Stage W: the materials registry, each stored building's material-space UVs and triangle
            /// materials until the atlas maps them, and its uses.</summary>
            internal readonly List<AtlasMaterial> Materials = new List<AtlasMaterial>();

            internal readonly Dictionary<Material, int> MaterialIds = new Dictionary<Material, int>();
            internal readonly List<float[]> PendingUV = new List<float[]>();
            internal readonly List<int[]> PendingTriMat = new List<int[]>();
            internal long PendingUVBytes;
            internal long PendingTriMatBytes;
            internal List<AtlasUse>[] Uses;
            internal Texture2D AtlasScratch;

            internal int AtlasPageCount;
            internal long PeakAtlasBytes;
            internal Stopwatch AtlasClock;
            internal AtlasMapped[] Mapped;
            internal bool AtlasAbandoned;
            internal string AtlasAbandonWhy;
            internal bool AtlasApplied;
            internal double AtlasSeconds;
            internal int TransparentMaterials;

            /// <summary>WP8 (D4 commit 1): every material the atlas leaves out - by render queue (2450 and up) or for
            /// having no main texture (flat) - with what its shader exposes and how much geometry it draws, for the
            /// materials line (ReportAtlas).</summary>
            internal readonly Dictionary<Material, MaterialDiag> MaterialDiags = new Dictionary<Material, MaterialDiag>();

            internal int UvElsewhere;
            internal int FlatUses;
            internal int TilesUnplaced;
            internal int FlatNoTexture;
            internal int FlatCaptureFailed;
            internal int RangesWritten;
            internal int SplitVertices;
            internal int SeamsRelaxed;
            internal int ClusteredTextureless;
            internal int TilesLate;
            internal int TexturesCaptured;
            internal int TexturesFailed;
            internal int TexturedBuildings;
            internal int UntexturedBuildings;
            internal long TexturedTriangles;

            /// <summary>Renderers skipped as never seen: switched off by nothing the game culls with, shadow-only,
            /// helper volumes.</summary>
            internal int HiddenSkipped;

            /// <summary>WP8 (D6): the switched-off split by how (enabled false, or forceRenderingOff); switched-off
            /// renderers read because a culling system owns them, split by which; baked-LOD proxies excluded.</summary>
            internal int HiddenDisabled;

            internal int HiddenForceOff;
            internal int GameCulledBySwitcher;
            internal int GameCulledByOcclusion;
            internal int ProxySkipped;

            internal int ShadowOnlySkipped;

            internal int VolumeSkipped;

            /// <summary>The LOD map: every group's levels, and renderer -> the group that lists it.</summary>
            internal LODGroup[] LodGroups;

            internal int LodMapped;

            internal readonly Dictionary<Renderer, LODGroup> LodOf = new Dictionary<Renderer, LODGroup>();

            internal readonly Dictionary<LODGroup, LOD[]> LodsOf = new Dictionary<LODGroup, LOD[]>();

            /// <summary>Candidates under a group that none of its levels lists (now their own building),
            /// candidates whose group is not their ancestor, and renderers two groups list.</summary>
            internal int LodUnmanaged;

            internal int LodNotAncestor;

            internal int LodShared;

            /// <summary>Whether the LOD map finished; candidates that fell back to the nearest-parent rule; groups
            /// skipped for being inactive.</summary>
            internal bool LodMapComplete;

            internal int LodFallback;
            internal int LodInactive;

            /// <summary>Paths of hidden renderers skipped, the first few per scene root (M2; WP8 D6), their count by
            /// root, and static-batch members whose UVs were not read (M5).</summary>
            internal readonly Dictionary<string, List<string>> HiddenSamples = new Dictionary<string, List<string>>();
            internal readonly Dictionary<string, int> HiddenRoots = new Dictionary<string, int>();

            internal int StaticBatchUvSkipped;

            /// <summary>Why decimation stopped, for the log line.</summary>
            internal string DecimationStoppedWhy;

            /// <summary>WP8: the building-quality line's counts (ReportQuality): decimations run, reaching their target,
            /// stopped at the error limit, relaxed (rollback only); refusals by kind; pinned corners; results not used
            /// for their slivers or a hole; area and sliver area of the decimated sources and of what was stored from
            /// them; strict decimations stored over their limit from the over-budget pool; and fall-backs by the level
            /// they fell back to (index 1, 2, 3 = LOD1, LOD2, LOD3 and coarser).</summary>
            internal int DecimationRuns;

            internal int DecimationsToTarget;
            internal int StoppedAtError;
            internal int RelaxedPasses;
            internal long RefusedPlacement;
            internal long RefusedFans;
            internal long RefusedDistance;
            internal long RefusedFlips;
            internal long RefusedEdgeGrowth;
            internal long RefusedSliver;
            internal long PinnedCorners;
            internal int SliversReverted;
            internal int AreaLost;
            internal double DecimatedSourceArea;
            internal double DecimatedSourceSliverArea;
            internal double StoredDecimatedArea;
            internal double StoredDecimatedSliverArea;

            /// <summary>WP8 (D3): strict decimations stored over their limit, the triangles they added, and the pool that
            /// pays for them - half of the unreserved share of the cap, set by ApplyBudget.</summary>
            internal int StoredOverBudget;

            internal long OverBudgetExtra;
            internal long OverBudgetPool;
            internal readonly int[] FellBackTo = new int[4];

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

            /// <summary>BUILDINGS (not renderers) that fell back to a coarser level of their group (WP8: the next one):
            /// a decimation that could not finish or fit, or an over-target building past the soft cap.</summary>
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

            /// <summary>The TexCoord0 attribute's stream, offset, format and dimension (stage W), or a stream of
            /// -1 when the mesh has none. Decoded on the GPU path only from the position's own stream.</summary>
            internal int UvStream = -1;

            internal int UvOffset;
            internal int UvDimension;
            internal VertexAttributeFormat UvFormat;

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

            /// <summary>Its world bounds' footprint, x times z, in square metres. The pre-WP7 floor's basis.</summary>
            internal double Footprint;

            /// <summary>Its world bounds' surface, 2(wh + wd + hd), in square metres - the budget's basis (WP7).</summary>
            internal double Surface;

            /// <summary>The source's measured triangle area, m2, once a worker has placed it (0 until then).</summary>
            internal double MeasuredSurface;

            /// <summary>The triangles it may be stored with (stage V's area budget); 0 until decided.</summary>
            internal int Target;

            /// <summary>What the ledger holds reserved for it until it is reached.</summary>
            internal long Reserved;

            /// <summary>Queued by a group's fallback (WP8 D3): the ladder level it was queued for, or -1 for a
            /// candidate from the list.</summary>
            internal int QueuedLevel = -1;

            /// <summary>Launched as its group's current source - counted in GroupState.Committed.</summary>
            internal bool AsSource;

            /// <summary>The LOD level it was read from (0 with no group), for its grade.</summary>
            internal int ReadLod;

            /// <summary>The other transform worth trying when <see cref="Matrix"/> does not put the
            /// vertices inside the renderer's bounds, or null. Set only for a static batch, where the
            /// two candidates are world space and the renderer's own.</summary>
            internal Matrix4x4? Fallback;
        }

        /// <summary>
        /// One usable level of a LOD group (WP8 D3): its LOD index, its renderers as a set and in order, and their
        /// triangles.
        /// </summary>
        private sealed class LevelSet
        {
            internal int Lod;
            internal HashSet<Renderer> Set;
            internal List<Renderer> List;
            internal long Triangles;
        }

        /// <summary>
        /// A LOD group's LADDER (WP8 D3), decided once: every level that is real geometry - not an impostor, at least
        /// <see cref="MinLodTriangles"/>, level 0 no more than <see cref="MaxSourceTriangles"/> - finest first, a level
        /// with the same renderers as the one before it skipped. The group reads <see cref="Current"/>; a building of
        /// it that cannot be stored moves the group to the NEXT level - once per step, and only while nothing of the
        /// current level has been stored or is on a worker (<see cref="Committed"/>), so a building never arrives
        /// twice. Under the <see cref="LevelLadder"/> rollback the ladder is the pre-WP8 pair: level 0 and the
        /// coarsest real level.
        /// </summary>
        private sealed class GroupState
        {
            internal readonly List<LevelSet> Levels = new List<LevelSet>();

            /// <summary>The level being read, an index into <see cref="Levels"/>.</summary>
            internal int Current;

            /// <summary>Renderers of the current level stored or on a worker. The group may move only while it is 0.</summary>
            internal int Committed;

            /// <summary>The group's identity for WP2's grade rule: KeyFor(the group's path, its reference point).</summary>
            internal int GroupKey;

            /// <summary>Whether the group reads a coarser level than its first, for the log's wording.</summary>
            internal bool UsingCoarse => Current > 0;
        }

        /// <summary>A source mesh in WORLD space, ready to store or decimate: x, y, z per vertex and three
        /// indices per triangle, wound the way the game draws it (a mirrored transform already swapped).
        /// Plain arrays, so a worker can decimate it without touching a Unity object.</summary>
        private sealed class WorldMesh
        {
            internal float[] P;
            internal int[] T;
            internal bool Mirrored;

            /// <summary>Stage W: u, v per vertex in its material's texture space (the material's scale and
            /// offset applied), and each triangle's material id - both null when nothing was textured.</summary>
            internal float[] UV;

            internal int[] TriMat;

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

            /// <summary>Each part's slot (readable path), each GPU range's slot (SubSlot), and per slot the
            /// material's registry id and its texture's scale and offset (su, sv, ou, ov) - stage W.</summary>
            internal List<int> PartSlot;

            internal int[] SubSlot;
            internal int[] SlotMaterial;
            internal float[] SlotST;

            /// <summary>TexCoord0 per vertex (readable path), or null.</summary>
            internal Vector2[] LocalUV;

            /// <summary>The GPU path's TexCoord0 in the position's stream: offset and element size (4 or 2), or
            /// a size of 0 for none.</summary>
            internal int UvOffset;

            internal int UvSize;

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
                var bytes = (Local?.Length ?? 0) * 12L + (LocalUV?.Length ?? 0) * 8L + (VertexBytes?.Length ?? 0) +
                            (IndexBytes?.Length ?? 0);
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

            /// <summary>WP8 (D3): the strict decimation when it is over its limit by no more than
            /// <see cref="OverBudgetMaxFactor"/> - offered to Apply's over-budget pool; null otherwise.</summary>
            internal WorldMesh OverBudget;

            internal bool Decimated;
            internal bool Undecodable;
            internal bool Implausible;
            internal bool TimedOut;
            internal bool OverLimit;
            internal bool SeamsRelaxed;
            internal long SourceTriangles;

            /// <summary>WP8: the decimation's own account, for the building-quality line - whether one ran, reached
            /// its target, stopped at the error limit or went relaxed (rollback only); its refusals and pinned
            /// corners; its source's and output's area and sliver area; and why a result inside its limit was not
            /// used: more slivers than its source (SliversReverted) or a hole (AreaLost, under
            /// <see cref="MeshDecimator.AreaKept"/> of the source's area).</summary>
            internal bool DecimationRan;

            internal bool ReachedTarget;
            internal bool StoppedByError;
            internal bool Relaxed;
            internal bool SliversReverted;
            internal bool AreaLost;
            internal int RefusedPlacement;
            internal int RefusedFans;
            internal int RefusedDistance;
            internal int RefusedFlips;
            internal int RefusedEdgeGrowth;
            internal int RefusedSliver;
            internal int PinnedCorners;
            internal double SourceArea;
            internal double SourceSliverArea;
            internal double OutputArea;
            internal double OutputSliverArea;

            /// <summary>The placed source's summed triangle area, m2 (WP7: the measured-surface evidence).</summary>
            internal double SurfaceArea;

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

            /// <summary>Stage W: the slot of each triangle in Indices, the material a kept vertex was kept for,
            /// and the kept vertices' UVs and triangles' materials.</summary>
            internal readonly List<int> TriSlot = new List<int>();

            internal int[] RemapMat = new int[0];
            internal readonly List<float> UV = new List<float>();
            internal readonly List<int> TriMat = new List<int>();

            /// <summary>What this lane holds, bytes: the decimator's arrays and the lists' capacities.</summary>
            internal long Bytes() =>
                Decimator.Bytes() + (Indices.Capacity + P.Capacity + T.Capacity + (long)Remap.Length +
                                     TriSlot.Capacity + RemapMat.Length + UV.Capacity + TriMat.Capacity) * 4L;
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

            // Derived from the extent (D8): 1 m wherever it fits the band cap, coarser by half a metre only
            // where it does not - so no extent can throw here and lose the whole mesh.
            job.CellMetres = ReliefCellFor(spanX, spanZ);

            var width = (int)Math.Ceiling(spanX / job.CellMetres);
            var height = (int)Math.Ceiling(spanZ / job.CellMetres);

            if (width <= 0 || height <= 0)
                throw new InvalidOperationException(
                    $"the extent is {spanX:0.0}x{spanZ:0.0} m, which is no grid at {job.CellMetres} m cells");

            // An assertion now: ReliefCellFor chose the cell so this cannot fire.
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
            (float)(job.Request.MinX + (col + 0.5d) * job.CellMetres);

        /// <summary>The world z of a cell row's centre.</summary>
        /// <param name="job">The build.</param>
        /// <param name="row">The row, 0 at the extent's MinZ edge.</param>
        private static float CellCentreZ(Job job, int row) =>
            (float)(job.Request.MinZ + (row + 0.5d) * job.CellMetres);

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
                $"{job.CellMetres.ToString("0.0#", CultureInfo.InvariantCulture)} m (derived from the {N(job.Request.MaxX - job.Request.MinX)} x " +
                $"{N(job.Request.MaxZ - job.Request.MinZ)} m extent and the {N(MapMeshFile.MaxCellsPerBand)}-cell band cap), " +
                $"{N(job.Rays)} rays in {N(job.ReliefClock.Elapsed.TotalMilliseconds)} ms, " +
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
                CellMetres = job.CellMetres,
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

                if (HiddenVolume(job, renderer, size)) continue;

                var centre = bounds.center;
                if (!IsFinite(centre.x) || !IsFinite(centre.y) || !IsFinite(centre.z)) continue;
                if (centre.x < minX || centre.x > maxX || centre.z < minZ || centre.z > maxZ) continue;

                // AFTER the size and extent tests, so the hidden counts are of renderers that would otherwise
                // have been buildings - not of every pooled weapon part in the scene.
                if (Invisible(job, renderer)) continue;

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
        /// Whether a renderer is something the player never sees (screen defect 1, 2026-09-24): a baked-LOD
        /// proxy, switched off by nothing the game culls with, or drawing only into the shadow map. Counted.
        ///
        /// The rule (WP8 D6), decided per renderer from what owns it, never from a map:
        /// - a PROXY (a ScreenDistanceSwitcher's merged hull of its area, Request.ProxyRenderers) is never a
        ///   candidate: its detail is read in its place;
        /// - a renderer switched off (enabled false or forceRenderingOff) that a runtime culling system OWNS - a
        ///   switcher's content or a Perfect Culling bake group's renderer (Request.GameCulled) - is hidden from
        ///   the player's position, not from the map, and is read (<see cref="IncludeGameCulled"/>);
        /// - one switched off that no system owns is a helper - the IndoorTrigger volumes, the portal cubes
        ///   (SBG_Custom_Portals/Tamozhnya/ambient_portal (N)/Cube, 17-35 m blank boxes) - or a designer-disabled
        ///   variant, and is skipped as before; tamozhnya/shadow (ShadowsOnly) is skipped too.
        /// FindObjectsOfType never returns a renderer on an inactive object, so the old activeInHierarchy clause
        /// could not fire and is gone.
        /// </summary>
        /// <param name="job">The build, for the counts.</param>
        /// <param name="renderer">The renderer.</param>
        private static bool Invisible(Job job, Renderer renderer)
        {
            var request = job.Request;

            if (request.ProxyRenderers != null && request.ProxyRenderers.Contains(renderer))
            {
                job.ProxySkipped++;
                return true;
            }

            if (request.CullingKnown && (!renderer.enabled || renderer.forceRenderingOff))
            {
                if (IncludeGameCulled && request.GameCulled != null && request.GameCulled.Contains(renderer))
                {
                    // Hidden from where the player stands, not from the map: read like any other.
                    if (request.OcclusionCulled != null && request.OcclusionCulled.Contains(renderer))
                        job.GameCulledByOcclusion++;
                    else
                        job.GameCulledBySwitcher++;
                }
                else
                {
                    job.HiddenSkipped++;
                    if (renderer.enabled) job.HiddenForceOff++;
                    else job.HiddenDisabled++;

                    // by the top of its hierarchy - the scene group it belongs to - and a couple of paths from each
                    var root = renderer.transform.root != null ? renderer.transform.root.name : "?";
                    job.HiddenRoots[root] = job.HiddenRoots.TryGetValue(root, out var seen) ? seen + 1 : 1;

                    if (!job.HiddenSamples.TryGetValue(root, out var samples))
                        job.HiddenSamples[root] = samples = new List<string>();
                    if (samples.Count < HiddenSamplesPerRoot) samples.Add(HierarchyPath(renderer.transform));

                    return true;
                }
            }

            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
            {
                job.ShadowOnlySkipped++;
                return true;
            }

            return false;
        }

        /// <summary>Whether a renderer is a helper volume the game draws nothing useful with: every
        /// material a plain Standard or Unlit one with no main texture, and bounds over
        /// <see cref="HiddenVolumeMetres"/> - portal cubes, stencils, trigger shells that happen to be
        /// switched on. Asked only of renderers that passed every cheaper test, because sharedMaterials
        /// allocates.</summary>
        /// <param name="job">The build, for the count.</param>
        /// <param name="renderer">The renderer.</param>
        /// <param name="size">Its world bounds' size.</param>
        private static bool HiddenVolume(Job job, Renderer renderer, Vector3 size)
        {
            if (Math.Max(size.x, Math.Max(size.y, size.z)) <= HiddenVolumeMetres) return false;

            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return false;

            var coloured = false;

            foreach (var material in materials)
            {
                if (material == null) continue;

                var shader = material.shader != null ? material.shader.name : "";
                var plain = shader.StartsWith("Standard", StringComparison.Ordinal) ||
                            shader.StartsWith("Unlit/", StringComparison.Ordinal);

                if (!plain) return false;
                if (material.HasProperty("_MainTex") && material.mainTexture != null) return false;

                if (material.HasProperty("_Color"))
                {
                    var c = material.color;
                    if (c.r < 0.95f || c.g < 0.95f || c.b < 0.95f) coloured = true;
                }
            }

            // Both signals (stage W review, LOW): an untextured plain box painted a real colour is geometry
            // someone meant to be seen, unless its name says it is a helper.
            if (coloured && !HelperName(renderer.transform)) return false;

            job.VolumeSkipped++;
            return true;
        }

        /// <summary>Whether a renderer's name or one of its three nearest parents' names says it is a helper
        /// volume: Cube, Portal, Stencil or Volume.</summary>
        /// <param name="transform">The renderer's transform.</param>
        private static bool HelperName(Transform transform)
        {
            var walk = transform;

            for (var depth = 0; walk != null && depth < 4; depth++, walk = walk.parent)
            {
                var name = walk.name ?? "";

                foreach (var mark in HelperNameMarks)
                    if (name.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
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
                UvLayout(candidate);

                Placement(candidate);
            }
        }

        /// <summary>Where the mesh keeps TexCoord0, if it has one (stage W).</summary>
        /// <param name="candidate">The candidate to fill in.</param>
        private static void UvLayout(Candidate candidate)
        {
            var mesh = candidate.Mesh;
            candidate.UvStream = -1;

            if (!mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) return;

            candidate.UvStream = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
            candidate.UvOffset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0);
            candidate.UvFormat = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
            candidate.UvDimension = mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0);
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

            // Queued levels too (WP8 D3): a level queued before its group moved on again is no longer its source.
            if (!IsSource(job, candidate)) return false;

            return Decodable(candidate);
        }

        /// <summary>Whether a candidate's source can be decoded at all: some triangles, not more than
        /// MaxSourceTriangles, and a vertex count the format can take. Wanted's size half, shared with
        /// EnqueueNextLevel so a fallback never queues what the read gate would refuse (review F01).</summary>
        /// <param name="candidate">The candidate.</param>
        private static bool Decodable(Candidate candidate) =>
            candidate.SourceTriangles > 0 && candidate.SourceTriangles <= MaxSourceTriangles &&
            candidate.Mesh != null && candidate.Mesh.vertexCount <= MapMeshFile.MaxVerticesPerBuilding;

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

        /// <summary>Whether this renderer is its group's source right now: a member of the level the group reads (WP8:
        /// its ladder's current level). A renderer under no group is its own source.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static bool IsSource(Job job, Candidate candidate)
        {
            if (candidate.Group == null) return true;
            if (!job.Groups.TryGetValue(candidate.Group, out var state) || state == null) return false;

            return state.Current < state.Levels.Count && state.Levels[state.Current].Set.Contains(candidate.Renderer);
        }

        /// <summary>A group's state, decided the first time the group is met (WP8 D3): its LADDER - every level that is
        /// real geometry, finest first (see <see cref="GroupState"/>) - read from level 0 when that is usable, else
        /// from the finest that is; and its GroupKey for the grade rule.</summary>
        /// <param name="job">The build.</param>
        /// <param name="group">The LOD group.</param>
        private static GroupState StateOf(Job job, LODGroup group)
        {
            if (job.Groups.TryGetValue(group, out var state)) return state;

            state = new GroupState();

            // The levels MapLods read - GetLODs allocates the whole LOD array every call.
            if (!job.LodsOf.TryGetValue(group, out var lods)) lods = group.GetLODs();

            var ladder = new List<LevelSet>();

            for (var k = 0; lods != null && k < lods.Length; k++)
            {
                var renderers = lods[k].renderers;
                if (renderers == null) continue;

                var triangles = LevelTriangles(renderers, out var impostor, out var any);
                if (!any) continue;

                // Counted apart, because they are different findings: an impostor level is a card with a picture of
                // the building on it (the game ships AmplifyImpostors), while a thin level is real geometry reduced
                // to a box.
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

                if (k == 0 && triangles > MaxSourceTriangles)
                {
                    job.InputGuarded++;
                    continue;
                }

                var list = new List<Renderer>();
                foreach (var renderer in renderers)
                    if (renderer != null) list.Add(renderer);

                var set = new HashSet<Renderer>(list);

                // the one-level case: a level with the same renderers as the one before it is not a step
                if (ladder.Count > 0 && ladder[ladder.Count - 1].Set.SetEquals(set)) continue;

                ladder.Add(new LevelSet { Lod = k, Set = set, List = list, Triangles = triangles });
            }

            // The rollback: the pre-WP8 pair - level 0 when usable, and the coarsest real level.
            if (!LevelLadder && ladder.Count > 1)
            {
                var first = ladder[0];
                var last = ladder[ladder.Count - 1];
                ladder.Clear();
                if (first.Lod == 0) ladder.Add(first);
                ladder.Add(last);
            }

            state.Levels.AddRange(ladder);
            foreach (var level in ladder) job.LevelRenderers += level.Set.Count;

            state.GroupKey = MapMeshFile.Building.KeyFor(HierarchyPath(group.transform),
                group.transform.TransformPoint(group.localReferencePoint));

            job.Groups[group] = state;

            return state;
        }

        /// <summary>Triangles across a level's renderers, and whether any of them draws with an impostor
        /// shader.</summary>
        /// <param name="renderers">The level's renderers.</param>
        /// <param name="impostor">Whether any material's shader is an impostor.</param>
        private static long LevelTriangles(Renderer[] renderers, out bool impostor) =>
            LevelTriangles(renderers, out impostor, out _);

        /// <summary>A level's triangles - each renderer's OWN share (review F16: a static-batch member's mesh is
        /// the whole batch, and counting it whole counted the batch once per member) - whether any material is an
        /// impostor, and whether the level has any renderer at all. The one place a level is counted (review
        /// F05).</summary>
        /// <param name="renderers">The level's renderers.</param>
        /// <param name="impostor">Whether any material's shader is an impostor.</param>
        /// <param name="any">Whether any renderer is non-null.</param>
        private static long LevelTriangles(Renderer[] renderers, out bool impostor, out bool any)
        {
            impostor = false;
            any = false;
            var triangles = 0L;

            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                any = true;
                triangles += OwnTriangles(renderer);

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

        /// <summary>The triangles a renderer itself draws: its submeshes, or - for a static-batch member - only its
        /// share of the batch's combined mesh, subMeshStartIndex onwards, one submesh per material (the same range
        /// Placement reads), and 0 when that share cannot be told.</summary>
        /// <param name="renderer">The renderer.</param>
        private static long OwnTriangles(Renderer renderer)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return 0L;

            int first = 0, end = mesh.subMeshCount;

            if (renderer is MeshRenderer batched && batched.isPartOfStaticBatch)
            {
                var materials = renderer.sharedMaterials;
                first = batched.subMeshStartIndex;
                var count = materials == null ? 0 : materials.Length;

                if (first < 0 || first >= mesh.subMeshCount || count <= 0) return 0L;
                end = Math.Min(mesh.subMeshCount, first + count);
            }

            var triangles = 0L;
            for (var s = first; s < end; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;

            return triangles;
        }

        /// <summary>One frame's worth of the LOD map: each group's levels read once, and every renderer
        /// they list mapped to the group (the first group wins; a renderer two groups list is counted).</summary>
        /// <param name="job">The build.</param>
        private static void MapLods(Job job)
        {
            var groups = job.LodGroups;
            var end = Math.Min(groups.Length, job.LodMapped + LodGroupsPerFrame);

            for (var i = job.LodMapped; i < end; i++)
            {
                job.LodMapped = i + 1;

                var group = groups[i];
                if (group == null) continue;

                // A group on an inactive object culls nothing (stage W review, M3).
                if (!group.gameObject.activeInHierarchy)
                {
                    job.LodInactive++;
                    continue;
                }

                var lods = group.GetLODs();
                job.LodsOf[group] = lods;

                if (lods == null) continue;

                foreach (var lod in lods)
                {
                    if (lod.renderers == null) continue;

                    foreach (var renderer in lod.renderers)
                    {
                        if (renderer == null) continue;

                        if (job.LodOf.TryGetValue(renderer, out var other))
                        {
                            if (other == group) continue;

                            // Listed by two groups: the one that is its ancestor, if either is.
                            job.LodShared++;
                            if (!renderer.transform.IsChildOf(other.transform) && renderer.transform.IsChildOf(group.transform))
                                job.LodOf[renderer] = group;
                            continue;
                        }

                        job.LodOf[renderer] = group;
                    }
                }
            }
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
                    // The group whose LEVELS list this renderer (MapLods), not the nearest parent with a
                    // group on it. A renderer under a group that none of its levels lists is always drawn,
                    // so it is its own building; one listed by a group that is not its ancestor belongs to
                    // that group. Both are counted, against the old nearest-parent answer.
                    job.LodOf.TryGetValue(candidate.Renderer, out var group);
                    var parent = candidate.Renderer.GetComponentInParent<LODGroup>(true);

                    // The map is trusted only when it finished and found something; otherwise the nearest-parent
                    // rule, as before (stage W review, M3).
                    if (!job.LodMapComplete || (job.LodsOf.Count == 0 && parent != null))
                    {
                        group = parent;
                        job.LodFallback++;
                    }
                    else if (group == null && parent != null) job.LodUnmanaged++;
                    else if (group != null && group != parent) job.LodNotAncestor++;

                    candidate.Group = group;
                    if (candidate.Group != null) StateOf(job, candidate.Group);

                    candidate.SourceTriangles = SubmeshTriangles(candidate);
                    candidate.Footprint = Math.Abs((double)candidate.Bounds.size.x * candidate.Bounds.size.z);
                    candidate.Surface = BoxSurface(candidate.Bounds.size);

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

        /// <summary>A world box's surface, 2(wh + wd + hd), in square metres.</summary>
        /// <param name="s">The box's size.</param>
        internal static double BoxSurface(Vector3 s) => BoxSurface(s.x, s.y, s.z);

        /// <summary>A box's surface from its three sides. Unity-free.</summary>
        internal static double BoxSurface(double w, double h, double d) =>
            2d * (Math.Abs(w * h) + Math.Abs(w * d) + Math.Abs(h * d));

        /// <summary>The area budget over every candidate that is its group's source now: the targets at 20 per m2
        /// of box surface with the pre-WP7 target as a floor, the map's cap DERIVED from what they need and what
        /// this machine holds (D4-D6), scaled by one factor when the planned total would pass
        /// <see cref="BudgetShare"/> of it, and each one RESERVED in the ledger. The share keeps the rest
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

            var surfaces = new double[sources.Count];
            var footprints = new double[sources.Count];
            var triangles = new long[sources.Count];

            for (var i = 0; i < sources.Count; i++)
            {
                surfaces[i] = sources[i].Surface;
                footprints[i] = sources[i].Footprint;
                triangles[i] = sources[i].SourceTriangles;
            }

            // D4-D6: what the buildings need at scale 1, what this machine holds, and the cap from both.
            var legacy = AreaBudget.LegacyTargets(footprints, triangles, out _);
            var demand = AreaBudget.Demand(surfaces, footprints, legacy, triangles);

            job.RamMb = SystemInfo.systemMemorySize;
            job.VramMb = SystemInfo.graphicsMemorySize;
            job.MemoryCeiling = MemoryCeiling(job.RamMb, job.VramMb);
            job.Demand = demand;

            // What the OLD rule would have reserved on this list, for the cap's headroom floor (see CapFor).
            var legacyReserved = 0L;
            for (var i = 0; i < sources.Count; i++) legacyReserved += Math.Min(triangles[i], legacy[i]);

            var cap = CapFor(demand, legacyReserved, job.MemoryCeiling);
            job.Cap = cap;

            // Nothing is reserved yet (the budget runs before the pipeline), so the ledger is replaced whole.
            job.Ledger = new BudgetLedger(cap);

            var targets = AreaBudget.Targets(surfaces, footprints, triangles, (long)(cap * BudgetShare), out var scale,
                out var floors, out var legacyScale);

            for (var i = 0; i < sources.Count; i++)
            {
                sources[i].Target = targets[i];
                sources[i].Reserved = Math.Min(triangles[i], targets[i]);
                job.Ledger.Reserve(sources[i].Reserved);

                if (floors[i] > AreaBudget.Scaled(AreaBudget.Basis(surfaces[i], footprints[i]), scale)) job.HeldAtFloor++;
            }

            job.BudgetScale = scale;
            job.LegacyScale = legacyScale;

            // WP8 (D3): strict decimations over their limit are paid from half of the unreserved share; the other
            // half stays for sources stored as they are and for the levels.
            job.OverBudgetPool = (long)(job.Cap * (1 - BudgetShare) * 0.5);
            job.Budgeted = job.Candidates.Count;
        }

        /// <summary>A candidate's target: the one the budget gave it, or - for a renderer that became a
        /// source after the budget was set (a group switched to its coarse level) - its box surface at the
        /// same density and scale, floored by the pre-WP7 rule on its footprint at that rule's scale.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static int TargetFor(Job job, Candidate candidate)
        {
            if (candidate.Target > 0) return candidate.Target;

            candidate.Target = AreaBudget.Target(AreaBudget.Basis(candidate.Surface, candidate.Footprint), job.BudgetScale,
                AreaBudget.LegacyTarget(candidate.Footprint, job.LegacyScale));

            return candidate.Target;
        }

        /// <summary>Whether this frame's budget of main-thread work is spent.</summary>
        /// <param name="job">The build.</param>
        private static bool FrameSpent(Job job) => job.FrameClock.Elapsed.TotalMilliseconds > FrameBudgetMs;

        /// <summary>
        /// The building's fallback to its group's NEXT level (WP8 D3; the stage-U path went straight to the coarsest):
        /// the group moves one step down its ladder and that level's renderers are QUEUED, ahead of the rest of the
        /// list, to be read, decimated and stored through the same pipeline and frame budget as everything else.
        /// False when the group may not move (see <see cref="NextLevel"/>): no group, something of the current level
        /// stored or in flight, the failing renderer on the next level itself (review F01), or no coarser level with
        /// a renderer the read gate would take - the caller then stores the source itself (as it is or clustered),
        /// never nothing.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate that could not be stored.</param>
        private static bool EnqueueNextLevel(Job job, Candidate candidate)
        {
            if (candidate.Group == null || !job.Groups.TryGetValue(candidate.Group, out var state) || state == null ||
                state.Current >= state.Levels.Count)
                return false;

            var current = candidate.QueuedLevel >= 0
                ? candidate.QueuedLevel == state.Current
                : state.Levels[state.Current].Set.Contains(candidate.Renderer);

            var made = new Dictionary<int, List<Candidate>>();
            var group = candidate.Group;

            var next = NextLevel(state.Current, state.Levels.Count, state.Committed, current,
                k => state.Levels[k].Set.Contains(candidate.Renderer),
                k => (made[k] = MakeLevel(job, state.Levels[k], group, k)).Count);

            if (next < 0) return false;

            state.Current = next;
            job.FellBack++;
            job.FellBackTo[Math.Max(1, Math.Min(3, state.Levels[next].Lod))]++;

            foreach (var c in made[next]) job.Extra.Enqueue(c);

            return true;
        }

        /// <summary>A ladder level's renderers as queued candidates: each not yet claimed, through MakeCandidate's
        /// filters and the read gate's size checks (review F01: a renderer the gate would refuse must not move the
        /// group and cost it its current level).</summary>
        /// <param name="job">The build.</param>
        /// <param name="level">The level.</param>
        /// <param name="group">Its group.</param>
        /// <param name="index">Its index in the ladder.</param>
        private static List<Candidate> MakeLevel(Job job, LevelSet level, LODGroup group, int index)
        {
            var made = new List<Candidate>();

            foreach (var renderer in level.List)
            {
                if (renderer == null || job.Claimed.Contains(renderer)) continue;

                Candidate c = null;
                Step(job, "a LOD level", () => c = MakeCandidate(job, renderer));
                if (c == null || !Decodable(c)) continue;

                c.Group = group;
                c.QueuedLevel = index;
                made.Add(c);
            }

            return made;
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
            if (Invisible(job, renderer)) return null;

            var bounds = renderer.bounds;
            var size = bounds.size;

            if (SizeVerdict(size.x, size.y, size.z, job.Request.MaxX - job.Request.MinX,
                    job.Request.MaxZ - job.Request.MinZ) != SizeOk)
                return null;

            if (HiddenVolume(job, renderer, size)) return null;

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
                Surface = BoxSurface(size),
            };

            candidate.Stride = candidate.Stream >= 0 ? mesh.GetVertexBufferStride(candidate.Stream) : 0;
            UvLayout(candidate);
            Placement(candidate);
            candidate.SourceTriangles = SubmeshTriangles(candidate);

            return candidate;
        }

        // --- reading a building ---------------------------------------------------------------------------

        // --- the pipeline: main thread reads, workers place and reduce ------------------------------------

        /// <summary>What <see cref="StoreWorld"/> answers: stored, or refused (the reason is counted by StoreWorld itself - the vertex caps in RefusedBuildingVertices / RefusedFileVertices).</summary>
        private const int Stored = 0;

        private const int Refused = 1;

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

            // Every level counts now (WP8 D3): the group may move on only while nothing of its current level is stored
            // or in flight.
            if (candidate.Group != null && job.Groups.TryGetValue(candidate.Group, out var state) && state != null &&
                state.Current < state.Levels.Count)
            {
                state.Committed++;
                candidate.AsSource = true;
                candidate.ReadLod = state.Levels[candidate.QueuedLevel >= 0 && candidate.QueuedLevel < state.Levels.Count
                    ? candidate.QueuedLevel
                    : state.Current].Lod;
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
                    var result = MeshDecimator.Cluster(world.P, world.T, limit, ClusterCapMs(world.Triangles), world.UV,
                        VertexMaterials(world));
                    var outcome = new Outcome { SourceTriangles = world.Triangles, TimedOut = result.TimedOut };

                    if (!result.TimedOut && result.Triangles != null && result.Triangles.Length >= 3)
                        outcome.Mesh = new WorldMesh
                        {
                            P = result.Positions, T = result.Triangles, Mirrored = world.Mirrored,
                            UV = result.UV, TriMat = result.UV != null ? result.TriangleMaterial : null,
                        };

                    return outcome;
                }),
            });
        }

        /// <summary>
        /// A finished flight, on the main thread: its counts, then the building - NEVER dropped for a decimation that
        /// did not work (H3). WP8 (D3), before the hard cap: the worker's mesh when it is within the building's limit;
        /// else the strict decimation OVER its limit (at most <see cref="OverBudgetMaxFactor"/> x) when the headroom
        /// AND the over-budget pool hold its overshoot; else the source as it is, bounded the same way and by the
        /// headroom; else the group's NEXT level (LOD1 before LOD2, never straight to the coarsest); else the source
        /// as it is when the headroom holds it at any size; else a cluster flight to the limit. Past the hard cap the
        /// same order without the cluster, then ABANDONED and counted. Under the <see cref="LevelLadder"/> rollback,
        /// the pre-WP8 order: the coarse level, the source as it is when the headroom holds it, the cluster.
        ///
        /// The ledger is settled in a finally (L1) - a renderer destroyed under us throws from StoreWorld, and that
        /// must not leak its pending limit - unless the limit was handed to a cluster flight; every store over the
        /// limit is checked against the headroom first (the Settle doc). The group's Committed counts renderers of its
        /// current level stored or in flight (L3): this flight's is taken off on entry and put back only when it is
        /// stored or handed on - which is what keeps a group from moving while a level of it is stored. Every store
        /// carries its GRADE (<see cref="GradeFor"/>).
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
            if (candidate.AsSource && state != null) state.Committed--;

            // Stores one mesh with its grade and settles for it; a refusal for the vertex caps is counted by StoreWorld.
            int Store(WorldMesh mesh, int how)
            {
                var code = StoreWorld(job, candidate, mesh, GradeFor(candidate.ReadLod, how), state?.GroupKey);
                job.Ledger.Settle(limit, code == Stored ? mesh.Triangles : 0);
                settled = true;

                if (code == Stored)
                {
                    committed = true;
                    job.BoxSurface += candidate.Surface;
                    job.MeasuredSurface += candidate.MeasuredSurface;
                }

                return code;
            }

            // The source as it is, counted.
            void AsIs(WorldMesh source)
            {
                if (Store(source, GradeAsIs) == Stored) job.StoredUndecimated++;
                else job.Unstored++;
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
                    if (outcome?.Mesh != null && outcome.Mesh.Triangles <= limit && Store(outcome.Mesh, GradeClustered) == Stored)
                    {
                        job.ClusteredStored++;
                        if (outcome.Mesh.UV == null) job.ClusteredTextureless++;
                    }
                    else if (outcome != null && outcome.TimedOut) job.ClusterTimedOut++;
                    else if (outcome != null) job.Unstored++;   // a worker that threw is Failed only (review F04)

                    return;
                }

                if (outcome != null && outcome.SurfaceArea > 0d) candidate.MeasuredSurface = outcome.SurfaceArea;

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
                    if (outcome.SeamsRelaxed) job.SeamsRelaxed++;

                    if (outcome.DecimationRan)
                    {
                        job.DecimationRuns++;
                        if (outcome.ReachedTarget) job.DecimationsToTarget++;
                        if (outcome.StoppedByError) job.StoppedAtError++;
                        if (outcome.Relaxed) job.RelaxedPasses++;
                        if (outcome.SliversReverted) job.SliversReverted++;
                        if (outcome.AreaLost) job.AreaLost++;
                        job.RefusedPlacement += outcome.RefusedPlacement;
                        job.RefusedFans += outcome.RefusedFans;
                        job.RefusedDistance += outcome.RefusedDistance;
                        job.RefusedFlips += outcome.RefusedFlips;
                        job.RefusedEdgeGrowth += outcome.RefusedEdgeGrowth;
                        job.RefusedSliver += outcome.RefusedSliver;
                        job.PinnedCorners += outcome.PinnedCorners;
                        job.DecimatedSourceArea += outcome.SourceArea;
                        job.DecimatedSourceSliverArea += outcome.SourceSliverArea;
                    }

                    if (outcome.TimedOut) job.TimedOut++;
                    if (outcome.OverLimit) job.OverLimit++;
                }

                // 1. what the worker made, inside the limit
                if (outcome?.Mesh != null)
                {
                    var code = Store(outcome.Mesh, GradeWithin);

                    if (code == Stored && outcome.Decimated)
                    {
                        job.Decimated++;
                        job.SourceDecimated += outcome.SourceTriangles;
                        job.StoredDecimatedArea += outcome.OutputArea;
                        job.StoredDecimatedSliverArea += outcome.OutputSliverArea;
                    }

                    // A refusal here (the vertex caps, the building cap) is a plain refusal (review F18): the vertex
                    // caps cannot trip for a mesh inside its limit - every stored vertex is used, so a building is
                    // at most 3 x 75 k vertices and the file at most 9 M of 12 M - and clustering a mesh already
                    // inside its limit would hand back the same mesh.
                    if (code != Stored) job.Unstored++;

                    return;
                }

                // Nothing to store at all - no geometry, no transform that fits: not a decimation failure.
                if (outcome != null && outcome.Source == null) return;

                // A worker that threw is counted as Failed and nothing else (review F04): its next level is still
                // tried, but it is not a second time "a building with no path left".
                if (outcome == null)
                {
                    EnqueueNextLevel(job, candidate);
                    return;
                }

                var source = outcome.Source;
                var fits = source.Triangles - (long)limit <= job.Ledger.Headroom;

                if (!LevelLadder)
                {
                    // The pre-WP8 order (rollback).
                    if (!job.PastHard)
                    {
                        if (EnqueueNextLevel(job, candidate)) return;

                        if (fits)
                        {
                            AsIs(source);
                            return;
                        }

                        if (!Cluster(source)) job.Unstored++;
                        return;
                    }

                    if (fits && Store(source, GradeAsIs) == Stored)
                    {
                        job.StoredUndecimated++;
                        return;
                    }

                    if (EnqueueNextLevel(job, candidate)) return;

                    job.AbandonedAtHard++;
                    return;
                }

                // 2. the strict decimation over its limit, paid from the over-budget pool and the headroom
                var over = outcome.OverBudget;
                if (over != null)
                {
                    var extra = over.Triangles - (long)limit;

                    if (extra <= job.Ledger.Headroom && extra <= job.OverBudgetPool)
                    {
                        if (Store(over, GradeOverBudget) == Stored)
                        {
                            job.StoredOverBudget++;
                            job.OverBudgetExtra += extra;
                            job.OverBudgetPool -= extra;
                            job.Decimated++;
                            job.SourceDecimated += outcome.SourceTriangles;
                            job.StoredDecimatedArea += outcome.OutputArea;
                            job.StoredDecimatedSliverArea += outcome.OutputSliverArea;
                        }
                        else
                        {
                            job.Unstored++;
                        }

                        return;
                    }
                }

                // 3. the source as it is, BEFORE any coarser level - bounded by the factor and the headroom
                if (fits && source.Triangles <= (long)limit * OverBudgetMaxFactor)
                {
                    AsIs(source);
                    return;
                }

                // 4. the group's NEXT level
                if (EnqueueNextLevel(job, candidate)) return;

                // 5. too big for the factor but inside the headroom: as it is, rather than a cluster
                if (fits)
                {
                    AsIs(source);
                    return;
                }

                // 6. the source clustered to its limit, on a worker - before the hard cap only; else abandoned
                if (!job.PastHard)
                {
                    if (!Cluster(source)) job.Unstored++;
                    return;
                }

                job.AbandonedAtHard++;
            }
            finally
            {
                if (!settled) job.Ledger.Settle(limit, 0);
                if (candidate.AsSource && state != null && committed) state.Committed++;
            }
        }

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
            var partSlots = new List<int>();

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
                    if (indices == null || indices.Length < 3) return;

                    parts.Add(indices);
                    partSlots.Add(sub - candidate.SubFirst);
                });
                job.PeakReadableMs = Math.Max(job.PeakReadableMs, clock.Elapsed.TotalMilliseconds);
            }

            if (parts.Count == 0) yield break;

            // TexCoord0, when the mesh has one per vertex (stage W).
            Vector2[] uvs = null;
            Step(job, "a readable building's UVs", () =>
            {
                if (candidate.UvStream < 0) return;

                // A static-batch member's mesh is the whole batch: its UV array is read only when this member's
                // own vertex range is a real share of it (stage W review, M5).
                if (candidate.Renderer.isPartOfStaticBatch)
                {
                    int lo = int.MaxValue, hi = -1;
                    foreach (var part in parts)
                        foreach (var index in part)
                        {
                            if (index < lo) lo = index;
                            if (index > hi) hi = index;
                        }

                    if (hi < lo || (long)(hi - lo + 1) * StaticBatchUvShare < local.Length)
                    {
                        job.StaticBatchUvSkipped++;
                        return;
                    }
                }

                var read = mesh.uv;
                if (read != null && read.Length == local.Length) uvs = read;
            });

            Step(job, "a readable building's placement", () =>
            {
                var source = NewSource(job, candidate);
                source.Local = local;
                source.Parts = parts;
                source.PartSlot = partSlots;
                source.LocalUV = uvs;
                Slots(job, candidate, source);
                job.Captured = source;
            });
        }

        /// <summary>Stage W: each of the candidate's submesh slots' material, registered, with its texture's
        /// scale and offset. Slot j is submesh SubFirst + j and sharedMaterials[j] (the last material repeats,
        /// as Unity draws it).</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="source">The source to fill.</param>
        private static void Slots(Job job, Candidate candidate, Source source)
        {
            var count = Math.Max(0, candidate.SubEnd - candidate.SubFirst);
            var materials = candidate.Renderer.sharedMaterials;

            source.SlotMaterial = new int[count];
            source.SlotST = new float[count * 4];
            HashSet<MaterialDiag> counted = null;

            for (var j = 0; j < count; j++)
            {
                var material = materials == null || materials.Length == 0 ? null : materials[Math.Min(j, materials.Length - 1)];
                var id = MaterialId(job, material);
                source.SlotMaterial[j] = id;

                // WP8 (D4 commit 1): the triangles a left-out material draws, and the buildings it draws them in.
                if (material != null && job.MaterialDiags.TryGetValue(material, out var diag))
                {
                    var submesh = candidate.SubFirst + j;
                    if (candidate.Mesh != null && submesh < candidate.Mesh.subMeshCount &&
                        candidate.Mesh.GetTopology(submesh) == MeshTopology.Triangles)
                        diag.Triangles += candidate.Mesh.GetIndexCount(submesh) / 3;

                    if ((counted ??= new HashSet<MaterialDiag>()).Add(diag)) diag.Buildings++;
                }

                var info = id >= 0 ? job.Materials[id] : null;
                source.SlotST[j * 4] = info?.ScaleU ?? 1f;
                source.SlotST[j * 4 + 1] = info?.ScaleV ?? 1f;
                source.SlotST[j * 4 + 2] = info?.OffsetU ?? 0f;
                source.SlotST[j * 4 + 3] = info?.OffsetV ?? 0f;
            }
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
            var slots = new List<int>();

            for (var s = candidate.SubFirst; s < candidate.SubEnd; s++)
            {
                var sub = mesh.GetSubMesh(s);
                if (sub.topology != MeshTopology.Triangles) continue;

                starts.Add(sub.indexStart);
                counts.Add(sub.indexCount);
                bases.Add(sub.baseVertex);
                slots.Add(s - candidate.SubFirst);
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
            source.SubSlot = slots.ToArray();
            Slots(job, candidate, source);

            // TexCoord0 from the SAME buffer the positions came from - a UV in another stream would be a
            // second readback, and the building then simply has no texture (counted).
            if (candidate.UvStream == candidate.Stream && candidate.UvDimension >= 2 &&
                (candidate.UvFormat == VertexAttributeFormat.Float32 || candidate.UvFormat == VertexAttributeFormat.Float16))
            {
                source.UvOffset = candidate.UvOffset;
                source.UvSize = candidate.UvFormat == VertexAttributeFormat.Float32 ? 4 : 2;
            }
            else if (candidate.UvStream >= 0)
            {
                job.UvElsewhere++;
            }

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
            lane.TriSlot.Clear();

            Vector3[] local;
            Vector2[] uv;

            if (source.Local != null)
            {
                local = source.Local;
                uv = source.LocalUV;

                for (var p = 0; p < source.Parts.Count; p++)
                {
                    var part = source.Parts[p];
                    triangles.AddRange(part);

                    var slot = source.PartSlot != null && p < source.PartSlot.Count ? source.PartSlot[p] : -1;
                    for (var t = 0; t + 2 < part.Length; t += 3) lane.TriSlot.Add(slot);
                }
            }
            else if (!Decode(source, triangles, lane.TriSlot, out local, out uv))
            {
                outcome.Undecodable = true;
                return outcome;
            }
            else
            {
                outcome.DecodedBytes = local.Length * 12L + (uv?.Length ?? 0) * 8L;
            }

            // The arrays out of Unity are not needed past this point; the source object lives until the
            // flight is applied, so they are let go of here.
            source.Local = null;
            source.Parts = null;
            source.LocalUV = null;
            source.VertexBytes = null;
            source.IndexBytes = null;

            var world = Place(source, local, uv, triangles, lane, outcome);
            if (world == null) return outcome;

            outcome.SourceTriangles = world.Triangles;
            outcome.SurfaceArea = MeshDecimator.Area(world.P, world.T);

            if (world.Triangles <= source.Limit)
            {
                outcome.Mesh = world;
                return outcome;
            }

            if (source.MayDecimate && world.Triangles <= MaxDecimatedSource)
            {
                var result = MeshDecimator.DecimateTextured(world.P, world.T, source.Target, source.Limit,
                    DecimateBuildingMs, lane.Decimator, world.UV, VertexMaterials(world));

                if (result.SeamsRelaxed) outcome.SeamsRelaxed = true;

                outcome.WorkerMs = result.Milliseconds;
                outcome.WorkspaceBytes = result.WorkspaceBytes;
                outcome.TimedOut = result.TimedOut;
                outcome.OverLimit = result.OverLimit;
                Account(outcome, result, source.Target);

                if (!result.TimedOut && result.Triangles != null && result.Triangles.Length >= 3)
                {
                    var mesh = new WorldMesh
                    {
                        P = result.Positions, T = result.Triangles, Mirrored = world.Mirrored,
                        UV = world.UV != null ? result.UV : null, TriMat = world.UV != null ? result.TriangleMaterial : null,
                    };
                    var n = result.Triangles.Length / 3;

                    // WP8: a result with more slivers than its source, or a hole, is not stored however well it fits -
                    // the building takes its next path (the area test was the seams-relaxed retry's trigger). One over
                    // its limit by no more than OverBudgetMaxFactor is OFFERED to Apply's over-budget pool (D3).
                    if (result.SliversReverted) outcome.SliversReverted = true;
                    else if (!MeshDecimator.AllowSeamRelaxedRetry && result.AreaShare < MeshDecimator.AreaKept) outcome.AreaLost = true;
                    else if (n <= source.Limit)
                    {
                        outcome.Mesh = mesh;
                        outcome.Decimated = true;
                        return outcome;
                    }
                    else if (n <= (long)source.Limit * OverBudgetMaxFactor) outcome.OverBudget = mesh;
                }
            }

            // Not clustered here (M4): the main thread may take the coarse level or store it as it is, and
            // a cluster is started as its own flight only when that path is the one chosen.
            outcome.Source = world;

            return outcome;
        }

        /// <summary>WP8: a decimation's account copied onto the outcome, for the building-quality line. Worker-safe.</summary>
        /// <param name="outcome">The outcome.</param>
        /// <param name="result">The decimation.</param>
        /// <param name="target">The target it was run to.</param>
        private static void Account(Outcome outcome, MeshDecimator.Result result, int target)
        {
            outcome.DecimationRan = true;
            outcome.ReachedTarget = !result.TimedOut && result.Triangles != null && result.Triangles.Length / 3 <= target;
            outcome.StoppedByError = result.StoppedByError;
            outcome.Relaxed = result.Relaxed;
            outcome.RefusedPlacement = result.RejectedPlacement;
            outcome.RefusedFans = result.RejectedFans;
            outcome.RefusedDistance = result.RejectedDistance;
            outcome.RefusedFlips = result.RejectedFlips;
            outcome.RefusedEdgeGrowth = result.RejectedEdgeGrowth;
            outcome.RefusedSliver = result.RejectedSliver;
            outcome.PinnedCorners = result.PinnedCorners;
            outcome.SourceArea = result.SourceArea;
            outcome.SourceSliverArea = result.SourceSliverArea;
            outcome.OutputArea = result.OutputArea;
            outcome.OutputSliverArea = result.OutputSliverArea;
        }

        /// <summary>A GPU source's vertex and index bytes as the (positions, triangles) pair the readable
        /// path has. False when the buffers do not hold what the layout says - fewer bytes than the vertex
        /// count needs, or no triangles at all. Worker-safe: plain arrays and numbers only.</summary>
        /// <param name="source">The source.</param>
        /// <param name="triangles">Filled with the indices, base vertex applied.</param>
        /// <param name="triSlot">Filled with each triangle's slot (stage W).</param>
        /// <param name="local">The decoded positions.</param>
        /// <param name="uv">The decoded TexCoord0, or null when the layout has none in this stream.</param>
        private static bool Decode(Source source, List<int> triangles, List<int> triSlot, out Vector3[] local,
            out Vector2[] uv)
        {
            local = null;
            uv = null;

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

            // TexCoord0 from the same bytes (stage W), when the layout says it is there and fits.
            var uvSize = source.UvSize;
            if (uvSize > 0 && (long)source.UvOffset + (long)(count - 1) * stride + 2L * uvSize <= vertexBytes.Length)
                uv = new Vector2[count];

            for (var v = 0; v < count; v++)
            {
                var at = source.Offset + v * stride;

                if (uv != null)
                {
                    var ut = source.UvOffset + v * stride;
                    uv[v] = uvSize == 4
                        ? new Vector2(BitConverter.ToSingle(vertexBytes, ut), BitConverter.ToSingle(vertexBytes, ut + 4))
                        : new Vector2(Half(BitConverter.ToUInt16(vertexBytes, ut)), Half(BitConverter.ToUInt16(vertexBytes, ut + 2)));
                }

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

                var slot = source.SubSlot != null && s < source.SubSlot.Length ? source.SubSlot[s] : -1;
                for (var i = 0L; i + 2 < indices; i += 3) triSlot.Add(slot);

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
        /// <param name="uv">Its TexCoord0 per vertex, or null (stage W).</param>
        /// <param name="triangles">Its triangle indices into <paramref name="local"/>.</param>
        /// <param name="lane">The worker's reused lists.</param>
        /// <param name="outcome">Where the implausible flag and the dropped count go.</param>
        private static WorldMesh Place(Source source, Vector3[] local, Vector2[] uv, List<int> triangles, Lane lane,
            Outcome outcome)
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
            lane.UV.Clear();
            lane.TriMat.Clear();

            if (lane.Remap.Length < local.Length) lane.Remap = new int[local.Length];
            if (lane.RemapMat.Length < local.Length) lane.RemapMat = new int[local.Length];

            var map = lane.Remap;
            for (var i = 0; i < local.Length; i++) map[i] = -1;

            // Stage W: textured only when there are UVs for every vertex; a triangle's material is its slot's.
            var textured = uv != null && uv.Length >= local.Length && source.SlotMaterial != null;

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

                var slot = t / 3 < lane.TriSlot.Count ? lane.TriSlot[t / 3] : -1;
                var material = textured && slot >= 0 && slot < source.SlotMaterial.Length ? source.SlotMaterial[slot] : -1;

                lane.T.Add(Keep(lane, map, a, pa, uv, material, source, slot));
                lane.T.Add(Keep(lane, map, b, pb, uv, material, source, slot));
                lane.T.Add(Keep(lane, map, c, pc, uv, material, source, slot));
                lane.TriMat.Add(material);
            }

            outcome.Dropped += dropped;

            if (lane.T.Count < 3) return null;

            return new WorldMesh
            {
                P = lane.P.ToArray(), T = lane.T.ToArray(), Mirrored = mirrored,
                UV = textured ? lane.UV.ToArray() : null, TriMat = textured ? lane.TriMat.ToArray() : null,
            };
        }

        /// <summary>Each vertex's material, from the triangles that use it - a vertex is kept once per
        /// material (Keep), so every triangle using it agrees. Null when the mesh is untextured.</summary>
        /// <param name="world">The mesh.</param>
        private static int[] VertexMaterials(WorldMesh world)
        {
            if (world.UV == null || world.TriMat == null) return null;

            var materials = new int[world.P.Length / 3];
            for (var i = 0; i < materials.Length; i++) materials[i] = -1;

            for (var t = 0; t < world.TriMat.Length; t++)
                for (var k = 0; k < 3; k++)
                    materials[world.T[t * 3 + k]] = world.TriMat[t];

            return materials;
        }

        /// <summary>One vertex kept in the world-space source, or the index it already has - once per
        /// MATERIAL (stage W): a vertex two materials share becomes two, because its UV is transformed by each
        /// material's own scale and offset and the atlas gives each material its own tile.</summary>
        /// <param name="lane">The worker's lists.</param>
        /// <param name="map">Local index to kept index, -1 for a vertex not yet kept.</param>
        /// <param name="index">The local vertex index.</param>
        /// <param name="world">Its world position.</param>
        /// <param name="uv">The local UVs, or null.</param>
        /// <param name="material">The triangle's material id, or -1.</param>
        /// <param name="source">The source, for the slot's texture scale and offset.</param>
        /// <param name="slot">The triangle's slot.</param>
        private static int Keep(Lane lane, int[] map, int index, Vector3 world, Vector2[] uv, int material,
            Source source, int slot)
        {
            if (map[index] >= 0 && lane.RemapMat[index] == material) return map[index];

            var at = lane.P.Count / 3;

            lane.P.Add(world.x);
            lane.P.Add(world.y);
            lane.P.Add(world.z);

            if (uv != null)
            {
                float u = uv[index].x, v = uv[index].y;

                if (slot >= 0 && source.SlotST != null && slot * 4 + 3 < source.SlotST.Length)
                {
                    u = u * source.SlotST[slot * 4] + source.SlotST[slot * 4 + 2];
                    v = v * source.SlotST[slot * 4 + 1] + source.SlotST[slot * 4 + 3];
                }

                lane.UV.Add(IsFinite(u) ? u : 0f);
                lane.UV.Add(IsFinite(v) ? v : 0f);
            }

            map[index] = at;
            lane.RemapMat[index] = material;

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
        /// <param name="grade">Its grade (<see cref="GradeFor"/>), held in memory on the building.</param>
        /// <param name="groupKey">Its LOD group's key, or null with no group (the building's own Key then).</param>
        private static int StoreWorld(Job job, Candidate candidate, WorldMesh world, byte grade, int? groupKey)
        {
            if (world?.P == null || world.T == null) return Refused;

            var vertices = world.P.Length / 3;
            var kept = world.T.Length / 3;
            var file = job.File;

            if (kept == 0 || vertices == 0) return Refused;

            if (vertices > MapMeshFile.MaxVerticesPerBuilding)
            {
                job.RefusedBuildingVertices++;
                return Refused;
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
            if (job.Triangles + kept > job.Cap)
            {
                job.OverBudget++;
                return Refused;
            }

            if (job.Vertices + vertices > MapMeshFile.MaxVerticesTotal)
            {
                job.RefusedFileVertices++;
                return Refused;
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
            var key = MapMeshFile.Building.KeyFor(HierarchyPath(candidate.Renderer.transform), candidate.Bounds.center);

            job.File.Buildings.Add(new MapMeshFile.Building
            {
                Key = key,
                Grade = grade,
                GroupKey = groupKey ?? key,
                Level = 0,
                X = x,
                Y = new ushort[0],
                Z = z,
                Indices = indices
            });

            job.PendingY.Add(heights);
            job.Centroids.Add((float)(heightSum / vertices));

            // Stage W: the material-space UVs and triangle materials wait for the atlas (BuildAtlas).
            var textured = world.UV != null && world.TriMat != null && world.UV.Length == vertices * 2 &&
                           world.TriMat.Length == kept;
            job.PendingUV.Add(textured ? world.UV : null);
            job.PendingTriMat.Add(textured ? world.TriMat : null);

            if (textured)
            {
                job.PendingUVBytes += world.UV.Length * 4L;
                job.PendingTriMatBytes += world.TriMat.Length * 4L;
            }

            job.Kept++;
            job.Triangles += kept;
            job.Vertices += vertices;
            job.Emitted.Add(candidate.Renderer);

            if (world.Mirrored) job.Mirrored++;

            return Stored;
        }

        // --- stage W: the atlas ------------------------------------------------------------------------

        /// <summary>
        /// A material stored buildings are drawn with, as the atlas sees it (stage W): its main texture, tint
        /// and tiling, how many repeats its uses need, and where its tiles landed. Registered on the main
        /// thread the first time a building with it is read (MaterialId).
        /// </summary>
        private sealed class AtlasMaterial
        {
            internal Material Material;
            internal Texture Texture;
            internal Color Tint = Color.white;
            internal float ScaleU = 1f;
            internal float ScaleV = 1f;
            internal float OffsetU;
            internal float OffsetV;

            /// <summary>Whether any use is textured / flat (a span over the repeat cap, or no texture).</summary>
            internal bool Textured;

            internal bool Flat;

            /// <summary>The textured block (all repeats): page, inner origin and one repeat's size in pixels;
            /// Page -1 when it has none or it did not fit.</summary>
            internal int Page = -1;

            internal int X;
            internal int Y;
            internal int TileW;
            internal int TileH;

            /// <summary>The flat-colour tile: page and inner origin (AtlasFlatPixels square).</summary>
            internal int FlatPage = -1;

            internal int FlatX;
            internal int FlatY;

            /// <summary>Whether the texture was captured, and the material's average colour, tint applied (the
            /// tint alone without a texture) - measured before packing, see Average.</summary>
            internal bool Captured;

            internal byte AvgR = 255;
            internal byte AvgG = 255;
            internal byte AvgB = 255;
        }

        /// <summary>WP8 (D4 commit 1): what the atlas left out, one material at a time - its shader, render queue,
        /// whether it is alpha-tested and at what cutoff, every texture property its shader exposes (a star marks
        /// one that holds a texture), and the buildings and source triangles read with it. Diagnostics only: the
        /// materials line settles whether the queue filter drops the common wall and roof materials.</summary>
        private sealed class MaterialDiag
        {
            internal string Shader;
            internal int Queue;
            internal bool AlphaTest;
            internal float Cutoff = float.NaN;
            internal string Properties;
            internal bool ByQueue;
            internal int Buildings;
            internal long Triangles;
        }

        /// <summary>Entries the materials line lists, most triangles first.</summary>
        private const int MaterialDiagEntries = 20;

        /// <summary>WP8 (D4 commit 1): records a material the atlas leaves out. Never throws.</summary>
        /// <param name="job">The build.</param>
        /// <param name="material">The material.</param>
        /// <param name="byQueue">Left out by its render queue (true) or for having no main texture (false).</param>
        private static void Diagnose(Job job, Material material, bool byQueue)
        {
            if (material == null || job.MaterialDiags.ContainsKey(material)) return;

            var diag = new MaterialDiag { ByQueue = byQueue };

            try
            {
                diag.Shader = material.shader != null ? material.shader.name : "?";
                diag.Queue = material.renderQueue;
                diag.AlphaTest = material.IsKeywordEnabled("_ALPHATEST_ON");
                if (material.HasProperty("_Cutoff")) diag.Cutoff = material.GetFloat("_Cutoff");

                var names = material.GetTexturePropertyNames();
                var listed = new List<string>();

                if (names != null)
                    foreach (var name in names)
                    {
                        if (listed.Count >= 12)
                        {
                            listed.Add("...");
                            break;
                        }

                        listed.Add(material.GetTexture(name) != null ? name + "*" : name);
                    }

                diag.Properties = listed.Count > 0 ? string.Join(" ", listed.ToArray()) : "none";
            }
            catch (Exception ex)
            {
                diag.Properties = $"unreadable ({ex.GetType().Name})";
            }

            job.MaterialDiags[material] = diag;
        }

        /// <summary>One building's use of one material: its UV bounds and the integer shift and flat verdict
        /// the atlas decided for it.</summary>
        private sealed class AtlasUse
        {
            internal int Material;
            internal float MinU = float.PositiveInfinity;
            internal float MinV = float.PositiveInfinity;
            internal float MaxU = float.NegativeInfinity;
            internal float MaxV = float.NegativeInfinity;
            internal bool Flat;
        }

        /// <summary>The shader properties a main texture is looked for under, in order.</summary>
        private static readonly string[] TextureProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };

        /// <summary>The shader properties a tint is looked for under, in order.</summary>
        private static readonly string[] TintProperties = { "_Color", "_BaseColor" };

        /// <summary>A material's registry id, registering it the first time: -1 for none, and for a
        /// transparent or alpha-tested one (render queue 2450 and up) - those keep the stage U/V fallback, since
        /// the atlas is drawn opaque and a leaf card or a window would come out a solid square.</summary>
        /// <param name="job">The build.</param>
        /// <param name="material">The material.</param>
        private static int MaterialId(Job job, Material material)
        {
            if (material == null) return -1;
            if (job.MaterialIds.TryGetValue(material, out var id)) return id;

            id = -1;

            try
            {
                if (material.renderQueue >= 2450)
                {
                    job.TransparentMaterials++;
                    Diagnose(job, material, true);
                }
                else
                {
                    var info = new AtlasMaterial { Material = material };

                    foreach (var name in TextureProperties)
                    {
                        if (!material.HasProperty(name)) continue;

                        var texture = material.GetTexture(name);
                        if (texture == null) continue;

                        info.Texture = texture;

                        var scale = material.GetTextureScale(name);
                        var offset = material.GetTextureOffset(name);
                        info.ScaleU = IsFinite(scale.x) ? scale.x : 1f;
                        info.ScaleV = IsFinite(scale.y) ? scale.y : 1f;
                        info.OffsetU = IsFinite(offset.x) ? offset.x : 0f;
                        info.OffsetV = IsFinite(offset.y) ? offset.y : 0f;
                        break;
                    }

                    foreach (var name in TintProperties)
                    {
                        if (!material.HasProperty(name)) continue;

                        info.Tint = material.GetColor(name);
                        break;
                    }

                    id = job.Materials.Count;
                    job.Materials.Add(info);

                    if (info.Texture == null) Diagnose(job, material, false);
                }
            }
            catch (Exception ex)
            {
                job.Note("a building's material", ex);
                id = -1;
            }

            job.MaterialIds[material] = id;
            return id;
        }

        /// <summary>
        /// The atlas (stage W), after the buildings and inside the same hold, under ONE cap over the whole
        /// phase (<see cref="AtlasSecondsCap"/>, the same seconds the capture reserves for it). In order: every
        /// stored building's use of every material; the tiles packed (textured blocks, then the small flat-colour
        /// tiles after them on a page no earlier than any textured one); the pages filled into two alternating
        /// 64 MB buffers, a few tiles a frame - Graphics.Blit into a temporary RenderTexture at min(size,
        /// AtlasTileMax), ReadPixels back, tint multiplied in, the average taken from the tile itself, the
        /// repeats copied a slice of rows at a time - each page handed to a worker that streams it to its staged
        /// file as a PNG while the next page fills; then every building's page UVs and ranges worked out beside
        /// it. Only when all of that is done inside the cap are the ranges written into the buildings and the page
        /// count into the file; the encodes still running are the capture's to finish after the hold is released
        /// (<see cref="Result.AtlasPages"/>). Past three quarters of the cap no more textures are captured (their
        /// tiles take the flat colour); past the cap, or on a throw or the capture's abort, the atlas is
        /// ABANDONED: no ranges, every page file deleted, and a line says so.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="result">Where the page encodes are handed over.</param>
        private static IEnumerator BuildAtlas(Job job, Result result)
        {
            var clock = Stopwatch.StartNew();
            var file = job.File;
            var jobs = new List<AtlasPageJob>();
            var completed = false;

            job.AtlasClock = clock;
            job.Uses = new List<AtlasUse>[file.Buildings.Count];
            job.Mapped = new AtlasMapped[file.Buildings.Count];

            try
            {
                // 1. the uses
                for (var i = 0; i < file.Buildings.Count && i < job.PendingUV.Count; i++)
                {
                    var building = i;
                    Step(job, "a building's texture use", () => MeasureUse(job, building));

                    if (FrameSpent(job))
                    {
                        if (OverCap(job)) yield break;
                        yield return null;
                        job.FrameClock.Restart();
                    }
                }

                // 2. the tiles, packed
                var requests = new List<int[]>();       // material, kind (0 textured, 1 flat), w, h, page, x, y
                Step(job, "the atlas layout", () => Layout(job, requests));

                if (job.AtlasPageCount == 0)
                {
                    completed = true;
                    yield break;
                }

                // textured tiles first on every page, so a page's flat tiles are filled with averages measured
                requests.Sort((a, b) => a[4] != b[4] ? a[4].CompareTo(b[4]) : a[1].CompareTo(b[1]));

                // 3. the pages, double-buffered: page p fills buffer p % 2 while page p - 1 encodes
                var size = MapMeshFile.AtlasPageSize;
                var buffers = new[] { new byte[size * size * 4], new byte[size * size * 4] };
                var busy = new Task[2];
                job.PeakAtlasBytes = 2L * size * size * 4;

                for (var page = 0; page < job.AtlasPageCount; page++)
                {
                    var b = page % 2;

                    // the buffer this page fills must be free: its last encode has written and cleared it
                    while (busy[b] != null && !busy[b].IsCompleted)
                    {
                        if (OverCap(job)) yield break;
                        yield return null;
                        job.FrameClock.Restart();
                    }

                    if (busy[b] != null && busy[b].IsFaulted)
                    {
                        job.AtlasAbandonWhy = $"page {page - 2}'s encode failed ({Describe(busy[b].Exception)})";
                        yield break;
                    }

                    var pixels = buffers[b];
                    var tiles = 0;

                    foreach (var request in requests)
                    {
                        if (request[4] != page) continue;

                        var info = job.Materials[request[0]];
                        var flat = request[1] == 1;
                        var r = request;
                        byte[] tile = null;
                        int tw = 0, th = 0, rx = 1, ry = 1, x = 0, y = 0;

                        Step(job, "an atlas tile", () =>
                        {
                            if (flat)
                            {
                                if (!info.Captured) Average(job, info);
                                tile = FlatTile(info);
                                tw = th = AtlasFlatPixels;
                                x = info.FlatX;
                                y = info.FlatY;
                            }
                            else if (clock.Elapsed.TotalSeconds < AtlasSecondsCap * AtlasCaptureShare)
                            {
                                tile = CaptureTile(job, info, r[2], r[3]);
                                tw = r[2];
                                th = r[3];
                                x = info.X;
                                y = info.Y;
                            }
                            else
                            {
                                job.TilesLate++;
                            }
                        });

                        tiles++;

                        // the copy into the page, a slice of rows a step: a 1024 x 1024 block is a million texels
                        if (tile != null)
                        {
                            var rows = th * ry + AtlasPadding * 2;

                            for (var from = 0; from < rows; from += AtlasRowsPerStep)
                            {
                                var start = from;
                                Step(job, "an atlas tile's rows", () => AtlasPacker.BlitRows(pixels, size, tile, tw, th, x, y,
                                    rx, ry, AtlasPadding, start - AtlasPadding, Math.Min(rows, start + AtlasRowsPerStep) - AtlasPadding));

                                if (FrameSpent(job))
                                {
                                    if (OverCap(job)) yield break;
                                    yield return null;
                                    job.FrameClock.Restart();
                                }
                            }
                        }

                        if (FrameSpent(job))
                        {
                            if (OverCap(job)) yield break;
                            yield return null;
                            job.FrameClock.Restart();
                        }
                    }

                    // handed to a worker: streamed to its staged file, hashed, then the buffer cleared for reuse
                    var path = job.Request.AtlasPartPath?.Invoke(page);
                    if (string.IsNullOrEmpty(path))
                    {
                        job.AtlasAbandonWhy = $"the capture gave no file for page {page}";
                        yield break;
                    }

                    // Started through a METHOD, so the worker's closure holds that method's own parameters. The
                    // first version captured this iterator's locals and then nulled them "to let the 64 MB go":
                    // an iterator's locals are fields the lambda shares, so the worker read a null buffer, every
                    // encode faulted, and no page ever reached the disk (Customs, 2026-09-25: "0 of 2 page(s)
                    // encoded and staged", then "abandoned - 2 page(s) dropped" once a third page waited on the
                    // faulted buffer).
                    var encode = StartEncode(path, pixels, size);

                    busy[b] = encode;
                    jobs.Add(new AtlasPageJob { Page = page, Tiles = tiles, PartPath = path, Encode = encode });
                }

                buffers = null;

                // 4. the buildings' UVs and ranges, worked out beside them
                for (var i = 0; i < file.Buildings.Count && i < job.PendingUV.Count; i++)
                {
                    var building = i;
                    Step(job, "a building's atlas UVs", () => MapBuilding(job, building));

                    if (FrameSpent(job))
                    {
                        if (OverCap(job)) yield break;
                        yield return null;
                        job.FrameClock.Restart();
                    }
                }

                if (OverCap(job)) yield break;

                // 5. done inside the cap: the ranges go in, the page count with them
                Step(job, "the atlas's ranges", () => ApplyAtlas(job));
                completed = job.AtlasApplied;
                if (!completed) job.AtlasAbandonWhy = "the ranges could not be written (the warning above names why)";
                if (completed) result.AtlasPages = jobs;
            }
            finally
            {
                job.AtlasSeconds = clock.Elapsed.TotalSeconds;

                if (!completed) AbandonAtlas(job, jobs);
            }
        }

        /// <summary>Whether the atlas phase must stop: the capture's abort, or the atlas's OWN clock - started
        /// when BuildAtlas starts, not with the mesh phase - past <see cref="AtlasSecondsCap"/>. Records why.</summary>
        /// <param name="job">The build.</param>
        private static bool OverCap(Job job)
        {
            if (job.Request.Abort)
            {
                job.AtlasAbandonWhy = "the capture's watchdog stopped the build";
                return true;
            }

            var seconds = job.AtlasClock?.Elapsed.TotalSeconds ?? 0d;
            if (!PastAtlasCap(seconds)) return false;

            job.AtlasAbandonWhy = $"past the {N(AtlasSecondsCap)} s atlas cap ({seconds.ToString("0.0", CultureInfo.InvariantCulture)} s on the atlas's own clock)";
            return true;
        }

        /// <summary>The cap rule on its own, for the harness: seconds on the atlas's own clock past the cap.</summary>
        /// <param name="atlasSeconds">Seconds since the atlas phase started.</param>
        internal static bool PastAtlasCap(double atlasSeconds) => atlasSeconds > AtlasSecondsCap;

        /// <summary>Starts one page's encode on a worker: streamed to <paramref name="path"/>, hashed, then the
        /// buffer cleared for reuse. A method, so the closure holds these parameters and nothing the caller does
        /// to its own variables afterwards can reach the worker.</summary>
        /// <param name="path">The .part file.</param>
        /// <param name="buffer">The page's pixels.</param>
        /// <param name="size">The page's side.</param>
        internal static Task<AtlasPng.Encoded> StartEncode(string path, byte[] buffer, int size) =>
            Task.Run(() =>
            {
                try
                {
                    using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write,
                               System.IO.FileShare.None, 1 << 20))
                        return AtlasPng.EncodeTo(stream, buffer, size, size);
                }
                finally
                {
                    Array.Clear(buffer, 0, buffer.Length);
                }
            });

        /// <summary>An exception as "Type: message at first frame", for a log line.</summary>
        /// <param name="ex">The exception (an AggregateException is unwrapped).</param>
        internal static string Describe(Exception ex)
        {
            if (ex == null) return "no exception recorded";

            var inner = ex is AggregateException aggregate && aggregate.InnerException != null ? aggregate.GetBaseException() : ex;
            var stack = inner.StackTrace ?? "";
            var first = stack.Split('\n')[0].Trim();

            return $"{inner.GetType().Name}: {inner.Message}" + (first.Length > 0 ? $" {first}" : "");
        }

        /// <summary>An abandoned atlas: no ranges, the page count 0, every page file deleted as its encode ends
        /// (an encode still writing cannot be stopped, so its file is removed when it finishes), and one line.</summary>
        /// <param name="job">The build.</param>
        /// <param name="jobs">The pages started.</param>
        private static void AbandonAtlas(Job job, List<AtlasPageJob> jobs)
        {
            job.AtlasAbandoned = true;
            job.Mapped = null;

            // No range may outlive the pages - an ApplyAtlas that threw half way would otherwise leave ranges on a
            // file that names no page, which Write refuses whole.
            TruncateAtlas(job.File, 0);

            foreach (var page in jobs)
            {
                var path = page.PartPath;
                page.Encode?.ContinueWith(_ =>
                {
                    try
                    {
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    }
                    catch
                    {
                        // a staged temporary nothing names; the capture's cleanup sweeps .part files too
                    }
                });
            }

            Plugin.LogSource?.LogWarning(
                $"QuestTree: the texture atlas of {job.Request.Map} was abandoned - {N(jobs.Count)} page(s) dropped: " +
                $"{job.AtlasAbandonWhy ?? "an exception in the atlas phase (the warning above names it)"}, after " +
                $"{job.AtlasClock?.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s; every building keeps " +
                "the side views.");
        }

        /// <summary>One building's uses: per material its UV bounds, then its integer shift (so a wall whose
        /// UVs run 7.2..9.8 uses repeats 7..10 of its tile as 0..3) and whether it fits the repeat cap.</summary>
        /// <param name="job">The build.</param>
        /// <param name="i">The building.</param>
        private static void MeasureUse(Job job, int i)
        {
            var uv = job.PendingUV[i];
            var mats = job.PendingTriMat[i];
            if (uv == null || mats == null) return;

            var indices = job.File.Buildings[i].Indices;
            var uses = new List<AtlasUse>();

            for (var t = 0; t < mats.Length; t++)
            {
                var m = mats[t];
                if (m < 0) continue;

                AtlasUse use = null;
                foreach (var u in uses)
                    if (u.Material == m) { use = u; break; }

                if (use == null)
                {
                    use = new AtlasUse { Material = m };
                    uses.Add(use);
                }

                for (var k = 0; k < 3; k++)
                {
                    var v = (int)indices[t * 3 + k];
                    float uu = uv[v * 2], vv = uv[v * 2 + 1];

                    if (uu < use.MinU) use.MinU = uu;
                    if (uu > use.MaxU) use.MaxU = uu;
                    if (vv < use.MinV) use.MinV = vv;
                    if (vv > use.MaxV) use.MaxV = vv;
                }
            }

            foreach (var use in uses)
            {
                var info = job.Materials[use.Material];

                // Stage X: the viewer REPEATS the tile, so a use's span no longer matters - it is flat only for
                // having no texture (a capture that fails is decided when the building is mapped).
                use.Flat = info.Texture == null;

                if (use.Flat)
                {
                    info.Flat = true;
                    job.FlatUses++;

                    job.FlatNoTexture++;
                }
                else
                {
                    info.Textured = true;
                }
            }

            job.Uses[i] = uses;
        }

        /// <summary>Every tile's size, packed (AtlasPacker): a textured block per material with a textured use -
        /// one repeat at TileSide(texture) - in group 0, limited to one
        /// page fewer than the cap when there are flat tiles; and a flat tile per material in use at all (the
        /// fallback for its flat uses and for a texture never captured) in group 1, packed after them into what is
        /// left - so every flat tile is on a page no earlier than any textured tile, and a full atlas costs
        /// textures, never colours.</summary>
        /// <param name="job">The build.</param>
        /// <param name="requests">Filled: material, kind, one repeat's w and h, page, x, y.</param>
        private static void Layout(Job job, List<int[]> requests)
        {
            for (var m = 0; m < job.Materials.Count; m++)
            {
                var info = job.Materials[m];
                if (!info.Textured && !info.Flat) continue;

                if (info.Textured && info.Texture != null && info.Texture.dimension == TextureDimension.Tex2D)
                {
                    var w = TileSide(info.Texture.width);
                    var h = TileSide(info.Texture.height);
                    requests.Add(new[] { m, 0, w, h, -1, 0, 0 });
                }

                requests.Add(new[] { m, 1, AtlasFlatPixels, AtlasFlatPixels, -1, 0, 0 });
            }

            var n = requests.Count;
            var widths = new int[n];
            var heights = new int[n];
            var groups = new int[n];
            var flats = 0;

            for (var k = 0; k < n; k++)
            {
                var info = job.Materials[requests[k][0]];
                var textured = requests[k][1] == 0;
                widths[k] = requests[k][2];
                heights[k] = requests[k][3];
                groups[k] = textured ? 0 : 1;
                if (!textured) flats++;
            }

            var pages = new int[n];
            var xs = new int[n];
            var ys = new int[n];
            var limits = new[] { flats > 0 ? MapMeshFile.MaxAtlasPages - 1 : MapMeshFile.MaxAtlasPages, MapMeshFile.MaxAtlasPages };

            job.AtlasPageCount = AtlasPacker.Pack(widths, heights, MapMeshFile.AtlasPageSize, MapMeshFile.MaxAtlasPages,
                AtlasPadding, pages, xs, ys, groups, limits);

            for (var k = 0; k < n; k++)
            {
                var r = requests[k];
                r[4] = pages[k];
                r[5] = xs[k];
                r[6] = ys[k];

                var info = job.Materials[r[0]];

                if (pages[k] < 0)
                {
                    job.TilesUnplaced++;
                    continue;
                }

                if (r[1] == 0)
                {
                    info.Page = pages[k];
                    info.X = xs[k];
                    info.Y = ys[k];
                    info.TileW = r[2];
                    info.TileH = r[3];
                }
                else
                {
                    info.FlatPage = pages[k];
                    info.FlatX = xs[k];
                    info.FlatY = ys[k];
                }
            }
        }

        /// <summary>A tile's side for a texture side (stage X): min(it, AtlasTileMax), rounded DOWN to a multiple of
        /// <see cref="MapMeshFile.TileAlign"/>, at least that - the viewer compresses each tile to DXT1, in 4 x 4
        /// blocks.</summary>
        /// <param name="texture">The texture's side in pixels.</param>
        internal static int TileSide(int texture) =>
            Math.Max(MapMeshFile.TileAlign, Math.Min(texture, AtlasTileMax) / MapMeshFile.TileAlign * MapMeshFile.TileAlign);

        /// <summary>One material's texture as one repeat's pixels: Blit to a temporary RenderTexture at that size,
        /// ReadPixels into the scratch texture, the tint multiplied in, and the material's average colour taken
        /// from the same pixels. Null (and the material left uncaptured) when it fails. Main thread; the
        /// RenderTexture is released and the active one restored however it goes.</summary>
        /// <param name="job">The build.</param>
        /// <param name="info">The material.</param>
        /// <param name="w">One repeat's width.</param>
        /// <param name="h">One repeat's height.</param>
        private static byte[] CaptureTile(Job job, AtlasMaterial info, int w, int h)
        {
            if (info.Texture == null || info.Page < 0) return null;

            var tile = ReadTexture(job, info, w, h);
            if (tile == null)
            {
                job.TexturesFailed++;
                return null;
            }

            long sr = 0, sg = 0, sb = 0;
            for (var o = 0; o < tile.Length; o += 4)
            {
                sr += tile[o];
                sg += tile[o + 1];
                sb += tile[o + 2];
            }

            var n = Math.Max(1, w * h);
            info.AvgR = (byte)(sr / n);
            info.AvgG = (byte)(sg / n);
            info.AvgB = (byte)(sb / n);
            info.Captured = true;
            job.TexturesCaptured++;

            return tile;
        }

        /// <summary>A texture read back at w x h, tint applied, RGBA with row 0 at the bottom - or null.</summary>
        /// <param name="job">The build.</param>
        /// <param name="info">The material.</param>
        /// <param name="w">The width.</param>
        /// <param name="h">The height.</param>
        private static byte[] ReadTexture(Job job, AtlasMaterial info, int w, int h)
        {
            if (info.Texture == null || info.Texture.dimension != TextureDimension.Tex2D) return null;

            var previous = RenderTexture.active;
            RenderTexture rt = null;

            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(info.Texture, rt);

                if (job.AtlasScratch == null)
                    job.AtlasScratch = new Texture2D(AtlasTileMax, AtlasTileMax, TextureFormat.RGBA32, false, false);

                RenderTexture.active = rt;
                job.AtlasScratch.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);

                // A view of the scratch texture's own bytes - no 256 KB array a tile.
                var read = job.AtlasScratch.GetRawTextureData<Color32>();
                var stride = job.AtlasScratch.width;
                var tile = new byte[w * h * 4];

                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        var c = read[y * stride + x];
                        var o = (y * w + x) * 4;

                        tile[o] = (byte)Math.Min(255f, c.r * info.Tint.r);
                        tile[o + 1] = (byte)Math.Min(255f, c.g * info.Tint.g);
                        tile[o + 2] = (byte)Math.Min(255f, c.b * info.Tint.b);
                        tile[o + 3] = 255;
                    }

                return tile;
            }
            catch (Exception ex)
            {
                job.Note("a building's texture", ex);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>The average colour of a material whose texture was never captured: an AtlasAveragePixels Blit
        /// when the capture share of the cap has time left, else its tint alone (already the default).</summary>
        /// <param name="job">The build.</param>
        /// <param name="info">The material.</param>
        private static void Average(Job job, AtlasMaterial info)
        {
            info.AvgR = (byte)(Mathf.Clamp01(info.Tint.r) * 255f);
            info.AvgG = (byte)(Mathf.Clamp01(info.Tint.g) * 255f);
            info.AvgB = (byte)(Mathf.Clamp01(info.Tint.b) * 255f);

            if (job.AtlasClock == null || job.AtlasClock.Elapsed.TotalSeconds >= AtlasSecondsCap * AtlasCaptureShare) return;

            var tile = ReadTexture(job, info, AtlasAveragePixels, AtlasAveragePixels);
            if (tile == null) return;

            long r = 0, g = 0, b = 0;
            for (var o = 0; o < tile.Length; o += 4)
            {
                r += tile[o];
                g += tile[o + 1];
                b += tile[o + 2];
            }

            const int n = AtlasAveragePixels * AtlasAveragePixels;
            info.AvgR = (byte)(r / n);
            info.AvgG = (byte)(g / n);
            info.AvgB = (byte)(b / n);
        }

        /// <summary>A material's flat tile, in its average colour.</summary>
        /// <param name="info">The material.</param>
        private static byte[] FlatTile(AtlasMaterial info)
        {
            if (info.FlatPage < 0) return null;

            var tile = new byte[AtlasFlatPixels * AtlasFlatPixels * 4];
            for (var o = 0; o < tile.Length; o += 4)
            {
                tile[o] = info.AvgR;
                tile[o + 1] = info.AvgG;
                tile[o + 2] = info.AvgB;
                tile[o + 3] = 255;
            }

            return tile;
        }

        /// <summary>
        /// One building's atlas mapping, worked out BESIDE it (stage X): one range per MATERIAL it uses, its
        /// triangles grouped by material; each range drawn with its material's tile (or, for a material with no
        /// texture or one that would not capture, its flat 4 x 4 tile), the raw material UVs of its vertices
        /// quantised over the range's own bounds; and every vertex that two ranges use DUPLICATED, one copy per
        /// range, so the one-range-per-vertex rule holds by construction (SplitByRange). Written into the
        /// building only by ApplyAtlas, when the phase completes.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="i">The building.</param>
        private static void MapBuilding(Job job, int i)
        {
            var uv = job.PendingUV[i];
            var mats = job.PendingTriMat[i];
            var uses = job.Uses[i];
            var building = job.File.Buildings[i];

            if (uv == null || mats == null || uses == null) return;

            var pages = job.AtlasPageCount;
            var triangles = mats.Length;

            // each triangle's range: one per material that has a tile to draw it with
            var rangeOf = new Dictionary<int, int>();
            var rangeUse = new List<AtlasUse>();
            var rangeTile = new List<int[]>();       // page, x, y, w, h
            var triRange = new int[triangles];

            for (var t = 0; t < triangles; t++)
            {
                triRange[t] = -1;

                var m = mats[t];
                if (m < 0) continue;

                if (!rangeOf.TryGetValue(m, out var r))
                {
                    r = -1;
                    AtlasUse use = null;
                    foreach (var candidate in uses)
                        if (candidate.Material == m) { use = candidate; break; }

                    var info = job.Materials[m];
                    var textured = use != null && !use.Flat && info.Captured && info.Page >= 0 && info.Page < pages;
                    var flat = !textured && info.FlatPage >= 0 && info.FlatPage < pages;

                    if (use != null && !use.Flat && !textured) job.FlatCaptureFailed++;

                    if (use != null && (textured || flat))
                    {
                        r = rangeUse.Count;
                        rangeUse.Add(use);
                        rangeTile.Add(textured
                            ? new[] { info.Page, info.X, info.Y, info.TileW, info.TileH }
                            : new[] { info.FlatPage, info.FlatX, info.FlatY, AtlasFlatPixels, AtlasFlatPixels });
                    }

                    rangeOf[m] = r;
                }

                triRange[t] = r;
            }

            if (rangeUse.Count == 0 || rangeUse.Count > MapMeshFile.MaxRangesPerBuilding)
            {
                job.UntexturedBuildings++;
                return;
            }

            // grouped by range, vertices split per range
            var split = SplitByRange(building.Indices, building.X.Length, triRange, rangeUse.Count);
            var source = split.Source;
            var n = source.Length;

            // The split adds vertices, and nothing else re-checks the caps before Validate would refuse the WHOLE
            // file at write: past either, this building stays untextured (its projected textures) instead. The
            // file's running total counts the splits already mapped, which ApplyAtlas adds all at once.
            if (n > MapMeshFile.MaxVerticesPerBuilding ||
                job.Vertices + job.SplitVertices + (n - (long)building.X.Length) > MapMeshFile.MaxVerticesTotal)
            {
                job.UntexturedBuildings++;
                job.SplitOverCap++;
                return;
            }

            var x = new ushort[n];
            var z = new ushort[n];
            var y = new float[n];
            var u = new ushort[n];
            var v = new ushort[n];
            var heights = job.PendingY[i];

            for (var k = 0; k < n; k++)
            {
                var from = source[k];
                x[k] = building.X[from];
                z[k] = building.Z[from];
                y[k] = heights != null && from < heights.Length ? heights[from] : 0f;
            }

            var ranges = new List<MapMeshFile.AtlasRange>(rangeUse.Count);

            for (var r = 0; r < rangeUse.Count; r++)
            {
                if (split.Count[r] == 0) continue;

                var use = rangeUse[r];
                var tile = rangeTile[r];

                var range = new MapMeshFile.AtlasRange
                {
                    Page = tile[0],
                    First = split.First[r],
                    Count = split.Count[r],
                    TileX = (ushort)tile[1],
                    TileY = (ushort)tile[2],
                    TileW = (ushort)tile[3],
                    TileH = (ushort)tile[4],
                    UMin = use.MinU,
                    UMax = use.MaxU,
                    VMin = use.MinV,
                    VMax = use.MaxV,
                };

                for (var j = range.First; j < range.First + range.Count; j++)
                {
                    var k = (int)split.Indices[j];
                    var from = source[k];
                    u[k] = UvCodeFor(uv[from * 2], range.UMin, range.UMax);
                    v[k] = UvCodeFor(uv[from * 2 + 1], range.VMin, range.VMax);
                }

                ranges.Add(range);
            }

            job.SplitVertices += n - building.X.Length;
            job.RangesWritten += ranges.Count;

            job.Mapped[i] = new AtlasMapped
            {
                Indices = split.Indices, X = x, Z = z, YMetres = y, U = u, V = v, Ranges = ranges,
                Triangles = split.Textured,
            };
        }

        /// <summary>A raw UV as its code over a range's bounds (stage X): 0..MaxUv across [min, max], 0 for a
        /// zero span (no division).</summary>
        /// <param name="value">The raw UV.</param>
        /// <param name="min">The range's minimum.</param>
        /// <param name="max">The range's maximum.</param>
        internal static ushort UvCodeFor(float value, float min, float max)
        {
            var span = (double)max - min;
            if (!(span > 0d)) return 0;

            var t = (value - (double)min) / span;
            return (ushort)Math.Round(Math.Max(0d, Math.Min(1d, t)) * MapMeshFile.MaxUv);
        }

        /// <summary>What SplitByRange hands back: the new index list (range 0's triangles, then range 1's, ... then
        /// the triangles in no range), each new vertex's source vertex, and each range's first index and index
        /// count; Textured is the triangles in any range.</summary>
        internal sealed class RangeSplit
        {
            internal uint[] Indices;
            internal int[] Source;
            internal int[] First;
            internal int[] Count;
            internal long Textured;
        }

        /// <summary>
        /// Groups a building's triangles by range and gives every range its OWN vertices (stage X): a vertex two
        /// ranges use becomes one vertex per range, so a vertex's UV code is quantised over exactly one range's
        /// bounds by construction - the invariant MapMeshFile's reader checks. Triangles in no range (-1) come
        /// last and share whichever copy of a vertex exists, since they carry no UV. Unity-free, for the harness.
        /// </summary>
        /// <param name="indices">The building's indices.</param>
        /// <param name="vertices">Its vertex count.</param>
        /// <param name="triRange">Each triangle's range, or -1.</param>
        /// <param name="ranges">The range count.</param>
        internal static RangeSplit SplitByRange(uint[] indices, int vertices, int[] triRange, int ranges)
        {
            var triangles = indices.Length / 3;
            var result = new RangeSplit { Indices = new uint[indices.Length], First = new int[ranges], Count = new int[ranges] };
            var source = new List<int>(vertices + vertices / 8);

            var map = new int[vertices];      // old vertex -> new, for the range being written
            var stamp = new int[vertices];    // which range map[] is for (range + 1)
            var any = new int[vertices];      // old vertex -> any new copy, for the untextured triangles
            for (var k = 0; k < vertices; k++) any[k] = -1;

            var at = 0;

            for (var r = -1; r < ranges; r++)
            {
                // ranges first (0..ranges-1), the untextured last: -1 is visited after the loop below
                if (r < 0) continue;

                result.First[r] = at;

                for (var t = 0; t < triangles; t++)
                {
                    if (triRange[t] != r) continue;

                    for (var c = 0; c < 3; c++)
                    {
                        var old = (int)indices[t * 3 + c];

                        if (stamp[old] != r + 1)
                        {
                            stamp[old] = r + 1;
                            map[old] = source.Count;
                            source.Add(old);
                            if (any[old] < 0) any[old] = map[old];
                        }

                        result.Indices[at++] = (uint)map[old];
                    }
                }

                result.Count[r] = at - result.First[r];
                result.Textured += result.Count[r] / 3;
            }

            for (var t = 0; t < triangles; t++)
            {
                if (triRange[t] >= 0 && triRange[t] < ranges) continue;

                for (var c = 0; c < 3; c++)
                {
                    var old = (int)indices[t * 3 + c];

                    if (any[old] < 0)
                    {
                        any[old] = source.Count;
                        source.Add(old);
                    }

                    result.Indices[at++] = (uint)any[old];
                }
            }

            result.Source = source.ToArray();
            return result;
        }

        /// <summary>The completed atlas into the file: every mapped building's indices, UVs and ranges, and the page
        /// count - all at once, so a file never names pages whose ranges were only half written.</summary>
        /// <param name="job">The build.</param>
        private static void ApplyAtlas(Job job)
        {
            var file = job.File;

            for (var i = 0; i < job.Mapped.Length && i < file.Buildings.Count; i++)
            {
                var mapped = job.Mapped[i];
                if (mapped == null) continue;

                var building = file.Buildings[i];
                building.Indices = mapped.Indices;
                building.X = mapped.X;
                building.Z = mapped.Z;
                building.U = mapped.U;
                building.V = mapped.V;
                building.Ranges = mapped.Ranges;

                // the heights in metres travel with the split vertices until QuantiseBuildings
                job.PendingY[i] = mapped.YMetres;
                job.Vertices += mapped.X.Length - (job.PendingUV[i] != null ? job.PendingUV[i].Length / 2 : mapped.X.Length);

                job.TexturedBuildings++;
                job.TexturedTriangles += mapped.Triangles;
            }

            file.AtlasPages = job.AtlasPageCount;
            job.AtlasApplied = true;

            for (var i = 0; i < job.PendingUV.Count; i++)
            {
                job.PendingUV[i] = null;
                job.PendingTriMat[i] = null;
            }

            job.Mapped = null;
        }

        /// <summary>
        /// The capture's end of the atlas (stage W review, H1): an atlas's pages were STARTED inside the hold and
        /// finish encoding on workers after it; the capture waits for them after releasing the scene and hands
        /// the list to this. Every page whose encode finished becomes pages 0..n-1 in order; the first that did not
        /// ends the atlas there - the file's page count and any range on a later page are cut to match (the
        /// buildings on them fall back), and those later pages' files are deleted.
        /// </summary>
        /// <param name="file">The mesh the pages belong to.</param>
        /// <param name="pages">The builder's page jobs, all finished.</param>
        /// <returns>The pages that are good, in order.</returns>
        internal static List<AtlasPageDone> SettleAtlas(MapMeshFile file, List<AtlasPageJob> pages)
        {
            var good = new List<AtlasPageDone>();
            if (pages == null) return good;

            foreach (var page in pages)
            {
                var ok = page.Encode != null && page.Encode.Status == TaskStatus.RanToCompletion &&
                         page.Encode.Result != null && good.Count == page.Page;

                if (!ok)
                {
                    // Said, every time: this is the line whose absence hid a null buffer for a whole test run.
                    var why = page.Encode == null ? "no encode was started"
                        : page.Encode.IsFaulted ? $"its encode failed ({Describe(page.Encode.Exception)})"
                        : !page.Encode.IsCompleted ? "its encode was still running when the capture stopped waiting"
                        : page.Encode.IsCanceled ? "its encode was cancelled"
                        : $"it arrived as page {page.Page} where page {good.Count} was due";

                    Plugin.LogSource?.LogWarning(
                        $"QuestTree: atlas page {page.Page} was not kept - {why}; it and every later page are dropped, " +
                        "and their buildings keep the side views.");
                    break;
                }

                good.Add(new AtlasPageDone
                {
                    Page = page.Page, Tiles = page.Tiles, PartPath = page.PartPath,
                    Bytes = page.Encode.Result.Length, Sha256 = page.Encode.Result.Sha256,
                });
            }

            for (var i = good.Count; i < pages.Count; i++)
            {
                var path = pages[i].PartPath;

                void Delete()
                {
                    try
                    {
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                    }
                    catch
                    {
                        // swept by the capture's cleanup
                    }
                }

                // One still writing is removed when it finishes; one that finished, now.
                if (pages[i].Encode != null && !pages[i].Encode.IsCompleted) pages[i].Encode.ContinueWith(_ => Delete());
                else Delete();
            }

            if (file != null && good.Count < file.AtlasPages) TruncateAtlas(file, good.Count);

            return good;
        }

        /// <summary>Cuts a file's atlas to its first <paramref name="pages"/> pages: the count, and every range on a
        /// later page dropped (those triangles fall back). UVs stay; a building left with no range keeps them
        /// harmlessly.</summary>
        /// <param name="file">The mesh.</param>
        /// <param name="pages">Pages kept.</param>
        internal static void TruncateAtlas(MapMeshFile file, int pages)
        {
            file.AtlasPages = Math.Max(0, pages);

            foreach (var building in file.Buildings)
                building.Ranges?.RemoveAll(r => r.Page >= file.AtlasPages);
        }

        /// <summary>A page coordinate in [0, 1] as the file's UV code.</summary>
        /// <param name="t">The coordinate.</param>
        private static ushort UvCode(double t) =>
            (ushort)Math.Round(Math.Max(0d, Math.Min(1d, t)) * MapMeshFile.MaxUv);

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
                $"candidates kept, {N(job.Triangles)} triangles stored of the {N(job.Cap)} cap (demand {N(job.Demand)}, " +
                $"memory ceiling {N(job.MemoryCeiling)} from RAM {N(job.RamMb)} MB / VRAM {N(job.VramMb)} MB, absolute " +
                $"{N(BuilderAbsoluteTriangles)}); {AreaBudget.TrianglesPerSquareMetre.ToString("0.0", f1)}/m2 of box surface, " +
                $"scaled x{job.BudgetScale.ToString("0.00", f1)}, {N(job.HeldAtFloor)} building(s) held at their pre-WP7 floor; " +
                $"surface: box {N(job.BoxSurface)} m2, triangles {N(job.MeasuredSurface)} m2 (ratio " +
                $"{(job.BoxSurface > 0d ? job.MeasuredSurface / job.BoxSurface : 0d).ToString("0.00", f1)}), stored density " +
                $"{(job.MeasuredSurface > 0d ? job.Triangles / job.MeasuredSurface : 0d).ToString("0.0", f1)} per m2" +
                (job.SplitOverCap > 0 ? $", {N(job.SplitOverCap)} left untextured over a vertex cap after the atlas split" : "") +
                $"; {Millions(job.SourceDecimated)} source triangles decimated ({N(job.Decimated)} buildings) in " +
                $"{(job.WorkerMs / 1000d).ToString("0.0", f1)} s on up to {N(job.PeakWorkers)} worker(s), peak workspace " +
                $"{(job.PeakWorkspaceBytes / (1024d * 1024d)).ToString("0.0", f1)} MB; " +
                $"{N(job.FellBack)} building(s) fell back to the game's LOD, {N(job.StoredUndecimated)} stored " +
                $"undecimated, {N(job.ClusteredStored)} clustered" +
                (job.TimedOut + job.OverLimit > 0
                    ? $" ({N(job.TimedOut)} decimation(s) out of time, {N(job.OverLimit)} over their limit)"
                    : "") +
                (job.DecimationStopped ? $"; decimation stopped at {job.DecimationStoppedWhy}" : "") +
                $"; {N(job.InputGuarded)} over the {Millions(MaxSourceTriangles)} source guard, " +
                $"{N(job.Oversized)} oversized, {N(job.HiddenSkipped + job.ShadowOnlySkipped + job.VolumeSkipped)} hidden " +
                $"volumes skipped ({N(job.HiddenSkipped)} switched off, {N(job.ShadowOnlySkipped)} shadow-only, " +
                $"{N(job.VolumeSkipped)} untextured helper volumes), LOD map {N(job.LodsOf.Count)} group(s): " +
                $"{N(job.LodUnmanaged)} candidate(s) under a group that lists none of them, {N(job.LodNotAncestor)} listed by a " +
                $"group that is not their parent, {N(job.LodShared)} renderer(s) in two groups, {N(job.LodInactive)} inactive " +
                $"group(s) skipped" + (job.LodFallback > 0 ? $", {N(job.LodFallback)} on the nearest-parent rule (the LOD map did not complete)" : "") +
                "; " +
                $"{N(job.GpuRead)} read back off the GPU, " +
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
                (job.SwitchedMidRead > 0 ? $" {N(job.SwitchedMidRead)} read(s) discarded - the group moved to a coarser level." : "") +
                (job.RefusedBuildingVertices + job.RefusedFileVertices > 0
                    ? $" {N(job.RefusedBuildingVertices)} refused over the per-building vertex cap, {N(job.RefusedFileVertices)} over " +
                      "the file's (not stored; counted in 'no path left')."
                    : "") +
                (job.ClusterTimedOut > 0 ? $" {N(job.ClusterTimedOut)} cluster(s) out of time." : "") +
                (job.Request.Abort ? " ABORTED by the capture's watchdog - finished with what was stored." : "") +
                (job.OverBudget > 0
                    ? $" {N(job.OverBudget)} did not fit the {N(job.Cap)}-triangle budget."
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

            ReportQuality(job);
        }

        /// <summary>WP8 (V.2): the building-quality line, one a build - how the decimations ended, what they refused,
        /// what fell back where, and the sliver share of the decimated sources against what was stored from them.
        /// Relaxed passes and seams crossed are 0 unless a rollback switch is on.</summary>
        /// <param name="job">The build.</param>
        private static void ReportQuality(Job job)
        {
            var f1 = CultureInfo.InvariantCulture;
            var s0 = job.DecimatedSourceArea > 0d ? job.DecimatedSourceSliverArea / job.DecimatedSourceArea * 100d : 0d;
            var s1 = job.StoredDecimatedArea > 0d ? job.StoredDecimatedSliverArea / job.StoredDecimatedArea * 100d : 0d;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: building quality for {job.Request.Map} - decimations {N(job.DecimationRuns)}: to target " +
                $"{N(job.DecimationsToTarget)}, stopped at the error limit {N(job.StoppedAtError)} (stored over budget " +
                $"{N(job.StoredOverBudget)}, +{N(job.OverBudgetExtra)} triangles, pool {N(job.OverBudgetPool)} left), relaxed " +
                $"passes {N(job.RelaxedPasses)}, seams crossed {N(job.SeamsRelaxed)}; refused: placement clamped " +
                $"{N(job.RefusedPlacement)}, fan {N(job.RefusedFans)}, distance {N(job.RefusedDistance)}, flip " +
                $"{N(job.RefusedFlips)}, edge growth {N(job.RefusedEdgeGrowth)}, new sliver {N(job.RefusedSliver)}; pinned " +
                $"corners {N(job.PinnedCorners)}; slivers reverted {N(job.SliversReverted)}, area lost {N(job.AreaLost)}; " +
                $"fell back: as-is {N(job.StoredUndecimated)}, LOD1 {N(job.FellBackTo[1])}, LOD2 {N(job.FellBackTo[2])}, " +
                $"LOD3+ {N(job.FellBackTo[3])}, clustered {N(job.ClusteredStored)}; sliver area {s0.ToString("0.0", f1)} % in " +
                $"the sources, {s1.ToString("0.0", f1)} % stored.");
        }

        /// <summary>The hidden renderers, once a capture - what the "switched off" filter dropped, by scene root with
        /// a couple of paths from each of the largest roots, and what it did NOT drop (WP8 D6): switched-off renderers
        /// a culling system owns, read as game-culled, and the baked-LOD proxies excluded in their favour.</summary>
        /// <param name="job">The build.</param>
        private static void ReportHidden(Job job)
        {
            var included = job.GameCulledBySwitcher + job.GameCulledByOcclusion;
            if (job.HiddenSkipped == 0 && included == 0 && job.ProxySkipped == 0) return;

            var roots = new List<KeyValuePair<string, int>>(job.HiddenRoots);
            roots.Sort((a, b) => b.Value.CompareTo(a.Value));

            var top = new List<string>();
            for (var i = 0; i < roots.Count && i < 12; i++) top.Add($"{roots[i].Key} {N(roots[i].Value)}");

            var samples = new List<string>();
            for (var i = 0; i < roots.Count && i < HiddenSampleRoots; i++)
                if (job.HiddenSamples.TryGetValue(roots[i].Key, out var paths))
                    samples.AddRange(paths);

            Plugin.LogSource?.LogInfo(
                $"QuestTree: hidden renderers skipped on {job.Request.Map} - {N(job.HiddenSkipped)} that were building-sized " +
                $"and inside the extent ({N(job.HiddenDisabled)} disabled, {N(job.HiddenForceOff)} forceRenderingOff), by " +
                $"scene root: {(top.Count > 0 ? string.Join(", ", top.ToArray()) : "none")}; included as game-culled " +
                $"{N(included)} (switcher content {N(job.GameCulledBySwitcher)}, occlusion groups {N(job.GameCulledByOcclusion)}), " +
                $"proxies excluded {N(job.ProxySkipped)}" + (IncludeGameCulled ? "" : " (game-culled inclusion rolled back)") +
                (samples.Count > 0
                    ? $"; samples ({HiddenSamplesPerRoot} a root, top {HiddenSampleRoots} roots): {string.Join(" | ", samples.ToArray())}"
                    : ""));
        }

        /// <summary>Stage W's log line: what the atlas captured, where it went and what fell back.</summary>
        /// <param name="job">The build.</param>
        private static void ReportAtlas(Job job)
        {
            var f1 = CultureInfo.InvariantCulture;
            var used = 0;

            foreach (var m in job.Materials)
                if (m.Textured || m.Flat) used++;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: textures for {job.Request.Map} - {N(job.TexturesCaptured)} material(s) captured into " +
                $"{N(job.File.AtlasPages)} atlas page(s) ({MapMeshFile.AtlasPageSize}, encoding on workers), " +
                $"{N(job.FlatNoTexture + job.FlatCaptureFailed)} use(s) on a flat colour ({N(job.FlatNoTexture)} without a " +
                $"texture, {N(job.FlatCaptureFailed)} capture failed), {N(job.RangesWritten)} range(s), " +
                $"{N(job.TexturesCaptured)} tile(s) on {N(job.File.AtlasPages)} page(s), {N(job.SplitVertices)} vertices split " +
                "between ranges, " +
                $"{job.AtlasSeconds.ToString("0.0", f1)} s; " +
                $"{N(used)} material(s) in use, {N(job.TexturesFailed)} texture(s) would not capture, " +
                $"{N(job.TilesUnplaced)} tile(s) over the {MapMeshFile.MaxAtlasPages}-page cap, " +
                $"{N(job.TilesLate)} left flat past {N(AtlasSecondsCap * AtlasCaptureShare)} s of the {N(AtlasSecondsCap)} s atlas cap, " +
                $"{N(job.TransparentMaterials)} transparent material(s) left to the side views, " +
                $"{N(job.UvElsewhere)} GPU mesh(es) with UVs in another stream, {N(job.StaticBatchUvSkipped)} static-batch " +
                "member(s) with their UVs not read; " +
                $"{N(job.TexturedBuildings)} building(s) textured ({Millions(job.TexturedTriangles)} triangles), " +
                $"{N(job.UntexturedBuildings)} with UVs but no tile; {N(job.SeamsRelaxed)} decimated with their seams " +
                $"relaxed, {N(job.ClusteredTextureless)} clustered without a texture" +
                (job.AtlasAbandoned ? " - ABANDONED, no page kept." : "."));

            ReportMaterialDiags(job);
        }

        /// <summary>WP8 (D4 commit 1): the materials the atlas left out, by render queue and flat, the
        /// <see cref="MaterialDiagEntries"/> drawing the most triangles listed with what their shaders expose -
        /// the evidence for (or against) the AlphaTest-queue walls and roofs before anything depends on it.</summary>
        /// <param name="job">The build.</param>
        private static void ReportMaterialDiags(Job job)
        {
            if (job.MaterialDiags.Count == 0) return;

            var f1 = CultureInfo.InvariantCulture;
            var all = new List<MaterialDiag>(job.MaterialDiags.Values);
            all.Sort((a, b) => b.Triangles.CompareTo(a.Triangles));

            int byQueue = 0, flat = 0;
            long queueTriangles = 0, flatTriangles = 0;

            foreach (var d in all)
            {
                if (d.ByQueue)
                {
                    byQueue++;
                    queueTriangles += d.Triangles;
                }
                else
                {
                    flat++;
                    flatTriangles += d.Triangles;
                }
            }

            var entries = new List<string>();
            for (var i = 0; i < all.Count && i < MaterialDiagEntries; i++)
            {
                var d = all[i];
                entries.Add($"{(d.ByQueue ? "queue" : "flat")}: {d.Shader} | {d.Queue} | " +
                            $"{(d.AlphaTest ? "alphatest" : "-")} | " +
                            $"{(float.IsNaN(d.Cutoff) ? "-" : d.Cutoff.ToString("0.00", f1))} | {d.Properties} | " +
                            $"{N(d.Buildings)} | {N(d.Triangles)}");
            }

            Plugin.LogSource?.LogInfo(
                $"QuestTree: materials left out of the atlas on {job.Request.Map} - {N(byQueue)} by render queue >= 2450 " +
                $"({N(queueTriangles)} source triangles), {N(flat)} flat without a main texture ({N(flatTriangles)} source " +
                $"triangles); the {entries.Count} drawing the most (kind: shader | renderQueue | _ALPHATEST_ON | _Cutoff | " +
                $"texture properties, * = set | buildings | triangles): {string.Join(" ;; ", entries.ToArray())}");
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
            // Stage W: the two page buffers and every building's material-space UVs, triangle materials and
            // heights in metres, all alive together during the atlas (stage W review, H3).
            var pending = 0L;
            foreach (var y in job.PendingY) pending += (y?.Length ?? 0) * 4L;
            pending += job.PendingUVBytes + job.PendingTriMatBytes;

            var buffers = job.PeakPipelineBytes + job.PeakDecodedBytes + job.PeakAtlasBytes + pending;

            var peak = relief + floats + job.RendererCount * 8L + job.PeakReadbackBytes +
                       result.BuildingBytes + candidates + buffers;

            // WP7: said at Info, against the memory ceiling it rests on (D5: M triangles at 64 B, a sixteenth of
            // RAM) - the check that the 64 B/triangle estimate holds on a real build.
            var ceilingBytes = job.MemoryCeiling * BytesPerTriangleInRaid;

            Plugin.LogSource?.LogInfo(
                $"QuestTree: {job.Request.Map}'s mesh holds {file.Describe()}; peak working set about " +
                $"{(peak / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB " +
                $"of the memory ceiling's {N(job.MemoryCeiling)} triangles x {N(BytesPerTriangleInRaid)} B = " +
                $"{(ceilingBytes / (1024d * 1024d)).ToString("0", CultureInfo.InvariantCulture)} MB " +
                $"({(ceilingBytes > 0 ? 100d * peak / ceilingBytes : 0d).ToString("0", CultureInfo.InvariantCulture)} % used) " +
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
    ///      absorbed, a mean squared distance in square metres - at the optimal point (the 3x3 solve) when it
    ///      lies within <see cref="MaxPlacementFactor"/> edge lengths of the edge (WP8: an unbounded optimum
    ///      slid metres along a crease, where neither the error nor the distance test can see it), else the
    ///      best of midpoint, a and b (midpoint first, so a flat region's ties stay at the MIDPOINT and no one
    ///      vertex grows a fan of hundreds of faces - an n96 flat box took five seconds). A CORNER (a boundary
    ///      vertex whose boundary turns by more than 30 degrees, or an end or a junction) never moves, and a
    ///      boundary vertex never moves off its boundary onto an interior point: the other end moves onto it;
    ///   5. collapse the cheapest VALID edge: both ends alive and unchanged; the link condition (no more
    ///      shared neighbours than shared faces) and its boundary form (two boundary vertices joined by
    ///      an INTERIOR edge would bridge an opening); the surviving vertex's fan no larger than
    ///      <see cref="MaxFan"/>; no surviving face degenerate; no face normal turned by more than 60
    ///      degrees (the flip test, which catches near-folds as well as inversions); and the new point
    ///      within 2 x the stop distance of every plane around it - the mean error alone can hide one
    ///      feature moved a long way; no surviving edge longer than <see cref="EdgeGrowth"/> x the longest
    ///      edge of the two fans before it (the spike guard); and no NEW sliver - a face thinner than
    ///      <see cref="SliverAspect"/> - unless the fans already held one as thin (WP8);
    ///   6. stop at the target, or when the cheapest collapse's mean error passes
    ///      (<see cref="MaxRelativeError"/> x bounds diagonal) squared - over the hard limit or not (WP8). The
    ///      pre-WP8 RELAXED pass, which lifted the error, distance and fan limits until the count was within
    ///      the HARD LIMIT, made the spikes and melted blobs and survives only as the
    ///      <see cref="AllowRelaxedPass"/> rollback. A mesh over its limit is reported (OverLimit), with its
    ///      sliver and area shares against its source's, for the caller's next path;
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

        /// <summary>Two corners at one position whose texture coordinates differ by more than this (either
        /// axis) are two vertices - a UV SEAM (stage W). A seam's edges then belong to one face on each side,
        /// which makes them boundary edges, and the boundary planes keep them where they are: a textured
        /// cube keeps each face's UV square.</summary>
        internal const double UvSeam = 1d / 64d;

        /// <summary>The heap's tie-break weight on an edge's squared length - see Push.</summary>
        private const double TieBreak = 1e-9;

        /// <summary>WP8 rollback: the pre-WP8 decimator - the RELAXED pass (error, distance and fan limits lifted
        /// until the hard limit), with none of WP8's guards (bounded placement, pinned corners, edge growth, new
        /// slivers, the sliver post-pass). Static rather than const so the harness can prove the old behaviour and
        /// the new side by side, the reason <c>rejectFlips</c> exists. Read once per run.</summary>
        internal static bool AllowRelaxedPass = false;

        /// <summary>WP8 rollback: the pre-WP8 seams-relaxed retry in <see cref="DecimateTextured"/>, which welded
        /// across UV seams and material borders and smeared the atlas.</summary>
        internal static bool AllowSeamRelaxedRetry = false;

        /// <summary>A solved optimum further than this many edge lengths from the segment a-b is not believed.</summary>
        internal const double MaxPlacementFactor = 0.5;

        /// <summary>A collapse may not make any surviving edge longer than this times the longest edge of the two
        /// fans before it.</summary>
        internal const double EdgeGrowth = 2.0;

        /// <summary>A face is a sliver when e_max^2 / (2 x area) exceeds this (= its longest edge over that edge's
        /// altitude).</summary>
        internal const double SliverAspect = 20d;

        /// <summary>A collapse may create a sliver only when its fans already held one at least as thin (x this
        /// factor). 1.0, not more: any factor above 1 compounds over a chain of collapses (20 -> 30 -> 45 ...). At
        /// 1.0, by induction, no face is ever thinner than the thinnest SOURCE face in its region.</summary>
        internal const double SliverWorsening = 1.0;

        /// <summary>Faces with a longest edge under this, metres, are never called slivers by the collapse guard
        /// (invisible at map scale).</summary>
        internal const double SliverMinEdge = 0.25;

        /// <summary>The post-pass's and the checker's sliver: longest edge at least this many metres.</summary>
        internal const double SliverMetricMinEdge = 1.0;

        /// <summary>A boundary vertex whose two boundary edges turn by more than this (cosine 0.866 = 30 degrees)
        /// is a CORNER and never moves.</summary>
        internal const double CornerCosine = 0.866;

        /// <summary>Post-pass: a result whose sliver-area share exceeds its source's by more than this is
        /// rejected (Result.SliversReverted) and the caller takes its next path.</summary>
        internal const double SliverPostSlack = 0.03;

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

            /// <summary>Collapses refused because the two ends belong to different materials (stage W).</summary>
            internal int RejectedSeam;

            /// <summary>WP8: solved optima further than <see cref="MaxPlacementFactor"/> edge lengths from their edge -
            /// counted per EVALUATION (Cost, which every push calls), not per refusal: "placements clamped".</summary>
            internal int RejectedPlacement;

            /// <summary>WP8: collapses refused by the spike guard (<see cref="EdgeGrowth"/>) and for a new sliver.</summary>
            internal int RejectedEdgeGrowth;

            internal int RejectedSliver;

            /// <summary>WP8: boundary corners, ends and junctions held in place.</summary>
            internal int PinnedCorners;

            /// <summary>WP8's post-pass: the source's and the output's area, their sliver area (faces thinner than
            /// <see cref="SliverAspect"/> with a longest edge of at least <see cref="SliverMetricMinEdge"/>), the shares,
            /// and whether the output's sliver share passed the source's by more than <see cref="SliverPostSlack"/> -
            /// a result the caller must not store.</summary>
            internal double SourceArea;

            internal double SourceSliverArea;
            internal double OutputArea;
            internal double OutputSliverArea;
            internal double SourceSliverShare;
            internal double OutputSliverShare;
            internal double AreaShare;
            internal bool SliversReverted;

            /// <summary>Whether this result came from the RELAXED retry (DecimateTextured): seams and material
            /// borders were not boundaries, and a survivor kept its own material and UV.</summary>
            internal bool SeamsRelaxed;

            /// <summary>u, v per output vertex (the survivor's UV follows its position), or null when the input
            /// carried none.</summary>
            internal float[] UV;

            /// <summary>Each output triangle's material, or null when the input carried none.</summary>
            internal int[] TriangleMaterial;

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

            /// <summary>WP8: each boundary vertex's first two boundary neighbours and its boundary-edge count, and
            /// whether it is pinned (a corner, an end or a junction).</summary>
            internal int[] BNb0 = new int[0];
            internal int[] BNb1 = new int[0];
            internal byte[] BCount = new byte[0];
            internal bool[] Pinned = new bool[0];
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
            internal double[] SubUV = new double[0];
            internal int[] SubMat = new int[0];
            internal double[] UV = new double[0];
            internal int[] VMat = new int[0];

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
                b += (SubUV.Length + UV.Length) * 8L + (SubMat.Length + VMat.Length) * 4L;
                b += (SubId.Length + Next.Length + Rep.Length + F.Length + Stamp.Length + Mark.Length + Map.Length +
                      Count.Length + LastFace.Length + BNb0.Length + BNb1.Length) * 4L;
                b += FaceOk.Length + NormalState.Length + FaceDead.Length + Dead.Length + Boundary.Length + CornerGroup.Length +
                     BCount.Length + Pinned.Length;
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
        /// <param name="uvs">u, v per input vertex, or null (stage W).</param>
        /// <param name="vertexMaterial">Each input vertex's material, or null. Corners of different materials
        /// never weld and their vertices never collapse into each other.</param>
        /// <param name="relaxSeams">The retry's mode (DecimateTextured): seams and borders are not boundaries.</param>
        internal static Result DecimateWith(float[] positions, int[] triangles, int target, int hardLimit,
            double timeCapMs, bool rejectFlips, Workspace workspace, float[] uvs = null, int[] vertexMaterial = null,
            bool relaxSeams = false)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result { SourceTriangles = triangles == null ? 0 : triangles.Length / 3 };
            var ws = workspace ?? new Workspace();

            try
            {
                new Work(positions, triangles, target, Math.Max(target, hardLimit), timeCapMs, rejectFlips, clock,
                    result, ws, uvs, vertexMaterial, relaxSeams).Run();
                result.SeamsRelaxed = relaxSeams;
            }
            finally
            {
                result.Milliseconds = clock.Elapsed.TotalMilliseconds;
                result.WorkspaceBytes = ws.Bytes();
            }

            return result;
        }

        /// <summary>
        /// A textured decimation with its one retry (stage W review, H2): first with UV seams and material
        /// borders as boundaries - which keeps every texture exact - and, when that cannot reach the hard limit
        /// (a shed of ten materials cannot get to 24 triangles with every border fixed), once more with them
        /// RELAXED, in what is left of the time cap: corners weld across a seam, collapses cross a border, and
        /// the survivor keeps its own material and UV. A visible seam is the price; the alternative was the
        /// cluster, which used to carry no texture at all.
        /// </summary>
        /// <param name="positions">x, y, z per vertex.</param>
        /// <param name="triangles">Three indices per triangle.</param>
        /// <param name="target">The target.</param>
        /// <param name="hardLimit">The hard limit.</param>
        /// <param name="timeCapMs">Milliseconds for both runs together.</param>
        /// <param name="workspace">Scratch to reuse, or null.</param>
        /// <param name="uvs">u, v per vertex, or null (then this is DecimateWith).</param>
        /// <param name="vertexMaterial">Each vertex's material, or null.</param>
        internal static Result DecimateTextured(float[] positions, int[] triangles, int target, int hardLimit,
            double timeCapMs, Workspace workspace, float[] uvs, int[] vertexMaterial)
        {
            var clock = Stopwatch.StartNew();
            var strict = DecimateWith(positions, triangles, target, hardLimit, timeCapMs, true, workspace, uvs, vertexMaterial);

            // WP8: strict only. The retry welded across UV seams and material borders - a face bridging two charts
            // is a streak - and its area test now lives in MapMeshBuilder.Process, where a hole is a reason to try the
            // building's next path instead of a reason to relax. Kept below for the rollback.
            if (!AllowSeamRelaxedRetry || uvs == null || strict.TimedOut) return strict;

            // The strict run either could not reach the limit, or reached it by letting whole material regions
            // collapse away - a region bounded by fixed borders shrinks to nothing in the relaxed phase and leaves
            // a HOLE (measured: a box of 24 quarter-face regions kept 3.75 of its 6 m2). Either way, retry.
            var before = Area(positions, triangles);
            var kept = Area(strict.Positions, strict.Triangles);

            if (!strict.OverLimit && kept >= before * AreaKept) return strict;

            var left = timeCapMs - clock.Elapsed.TotalMilliseconds;
            if (left <= 0) return strict;

            var relaxed = DecimateWith(positions, triangles, target, hardLimit, left, true, workspace, uvs, vertexMaterial,
                relaxSeams: true);
            relaxed.RejectedSeam = strict.RejectedSeam;
            relaxed.Milliseconds += strict.Milliseconds;

            if (relaxed.TimedOut || relaxed.Triangles == null) return strict;

            // the relaxed run is taken when it fits where the strict one did not, or keeps more of the surface
            if (strict.OverLimit && !relaxed.OverLimit) return relaxed;
            return Area(relaxed.Positions, relaxed.Triangles) > kept ? relaxed : strict;
        }

        /// <summary>The share of its surface a decimation must keep before its result is trusted: under it
        /// MapMeshBuilder.Process takes the building's next path (WP8; under the rollback, the seams-relaxed retry
        /// runs).</summary>
        internal const double AreaKept = 0.97;

        /// <summary>WP8: a mesh's total area and the area of its SLIVERS - faces whose e_max^2 / (2 x area) passes
        /// <see cref="SliverAspect"/> with a longest edge of at least <see cref="SliverMetricMinEdge"/> m, the
        /// checker's definition. O(n).</summary>
        /// <param name="p">x, y, z per vertex.</param>
        /// <param name="t">Three indices per triangle.</param>
        /// <param name="total">The area, m2.</param>
        /// <param name="slivers">The slivers' area, m2.</param>
        internal static void SliverArea(float[] p, int[] t, out double total, out double slivers)
        {
            total = 0d;
            slivers = 0d;
            if (p == null || t == null) return;

            var n = p.Length / 3;
            var minEdge2 = SliverMetricMinEdge * SliverMetricMinEdge;

            for (var k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;

                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                double wx = vx - ux, wy = vy - uy, wz = vz - uz;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                var cross = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var e2 = Math.Max(ux * ux + uy * uy + uz * uz, Math.Max(vx * vx + vy * vy + vz * vz, wx * wx + wy * wy + wz * wz));

                total += 0.5 * cross;
                if (e2 >= minEdge2 && e2 > SliverAspect * cross) slivers += 0.5 * cross;
            }
        }

        /// <summary>WP8: the share of a mesh's area in slivers (see <see cref="SliverArea"/>), 0 for no area.</summary>
        internal static double SliverAreaShare(float[] p, int[] t)
        {
            SliverArea(p, t, out var total, out var slivers);
            return total > 0d ? slivers / total : 0d;
        }

        /// <summary>A mesh's surface area. Internal: MapMeshBuilder.Process measures each placed source with it.</summary>
        internal static double Area(float[] p, int[] t)
        {
            if (p == null || t == null) return 0d;

            var area = 0d;
            var n = p.Length / 3;

            for (var k = 0; k + 2 < t.Length; k += 3)
            {
                int a = t[k], b = t[k + 1], c = t[k + 2];
                if (a < 0 || b < 0 || c < 0 || a >= n || b >= n || c >= n) continue;

                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            }

            return area;
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
        /// <param name="uvs">u, v per vertex, or null (stage W review, H2): a cell's UV is the mean of its
        /// dominant material's vertices, and a triangle takes its first corner's cell's material.</param>
        /// <param name="vertexMaterial">Each vertex's material, or null.</param>
        internal static Result Cluster(float[] positions, int[] triangles, int limit, double timeCapMs = double.MaxValue,
            float[] uvs = null, int[] vertexMaterial = null)
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
                    // EXACT cell keys (review F19): the XOR hash Key() collides, and a collision merged two cells
                    // metres apart into one vertex. Three 21-bit indices; a cell index past that is clamped - it
                    // needs a building over 2,000 km across at the smallest cell.
                    var key = CellKey((long)Math.Floor((positions[v * 3] - minX) / cell),
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

                    if (uvs != null && vertexMaterial != null && uvs.Length >= n * 2 && vertexMaterial.Length >= n)
                        ClusterUVs(result, map, id, n, uvs, vertexMaterial, counts.Count);

                    break;
                }

                cell *= 1.3;
            }

            result.Milliseconds = clock.Elapsed.TotalMilliseconds;

            return result;
        }

        /// <summary>A cluster's UVs: per cell its dominant material (majority vote) and the mean UV of that
        /// material's vertices in it; each output triangle takes its first corner's material. Crude - a tiled
        /// texture's UVs average across repeats - but a coloured blob beats a textureless one.</summary>
        private static void ClusterUVs(Result result, int[] map, int[] id, int n, float[] uvs, int[] vertexMaterial,
            int cellCount)
        {
            var vote = new int[cellCount];
            var votes = new int[cellCount];

            for (var v = 0; v < n; v++)
            {
                var k = id[v];
                if (votes[k] == 0) { vote[k] = vertexMaterial[v]; votes[k] = 1; }
                else if (vote[k] == vertexMaterial[v]) votes[k]++;
                else votes[k]--;
            }

            var su = new double[cellCount];
            var sv = new double[cellCount];
            var sn = new int[cellCount];

            for (var v = 0; v < n; v++)
            {
                var k = id[v];
                if (vertexMaterial[v] != vote[k]) continue;
                su[k] += uvs[v * 2];
                sv[k] += uvs[v * 2 + 1];
                sn[k]++;
            }

            var outCount = result.Positions.Length / 3;
            var uv = new float[outCount * 2];
            var cellOf = new int[outCount];

            for (var k = 0; k < map.Length; k++)
            {
                var o = map[k];
                if (o < 0) continue;
                cellOf[o] = k;
                uv[o * 2] = sn[k] > 0 ? (float)(su[k] / sn[k]) : 0f;
                uv[o * 2 + 1] = sn[k] > 0 ? (float)(sv[k] / sn[k]) : 0f;
            }

            var mats = new int[result.Triangles.Length / 3];
            for (var t = 0; t < mats.Length; t++) mats[t] = vote[cellOf[result.Triangles[t * 3]]];

            result.UV = uv;
            result.TriangleMaterial = mats;
        }

        private static long Key(long x, long y, long z) => unchecked(x * 73856093L ^ y * 19349663L ^ z * 83492791L);

        /// <summary>A cell's exact identity: three indices of 21 bits each, clamped to [0, 2^21).</summary>
        internal static long CellKey(long x, long y, long z)
        {
            const long max = (1L << 21) - 1;
            x = Math.Max(0, Math.Min(max, x));
            y = Math.Max(0, Math.Min(max, y));
            z = Math.Max(0, Math.Min(max, z));
            return (x << 42) | (y << 21) | z;
        }

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
            private readonly float[] _uvIn;
            private readonly int[] _matIn;
            private readonly bool _relaxSeams;
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

            /// <summary>The pre-WP8 decimator (<see cref="AllowRelaxedPass"/>), read once for the run.</summary>
            private readonly bool _legacy = AllowRelaxedPass;

            private readonly double[] _sum = new double[10];

            internal Work(float[] positions, int[] triangles, int target, int limit, double cap, bool rejectFlips,
                Stopwatch clock, Result result, Workspace ws, float[] uvs = null, int[] vertexMaterial = null,
                bool relaxSeams = false)
            {
                _relaxSeams = relaxSeams;
                _in = positions;
                _tris = triangles;
                _uvIn = uvs != null && positions != null && uvs.Length >= positions.Length / 3 * 2 ? uvs : null;
                _matIn = vertexMaterial != null && positions != null && vertexMaterial.Length >= positions.Length / 3
                    ? vertexMaterial : null;
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
                PostPass();
            }

            /// <summary>WP8's building-level post-pass: the source's and the output's area and sliver area, and the
            /// verdict - an output whose sliver share passes its source's by more than <see cref="SliverPostSlack"/>
            /// is not to be stored (off under the rollback).</summary>
            private void PostPass()
            {
                SliverArea(_in, _tris, out var sourceArea, out var sourceSlivers);
                SliverArea(_result.Positions, _result.Triangles, out var outputArea, out var outputSlivers);

                _result.SourceArea = sourceArea;
                _result.SourceSliverArea = sourceSlivers;
                _result.OutputArea = outputArea;
                _result.OutputSliverArea = outputSlivers;
                _result.SourceSliverShare = sourceArea > 0d ? sourceSlivers / sourceArea : 0d;
                _result.OutputSliverShare = outputArea > 0d ? outputSlivers / outputArea : 0d;
                _result.AreaShare = sourceArea > 0d ? outputArea / sourceArea : 1d;
                _result.SliversReverted = !_legacy && _result.OutputSliverShare > _result.SourceSliverShare + SliverPostSlack;
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
                ws.SubUV = Workspace.Grow(ws.SubUV, subs * 2);
                ws.SubMat = Workspace.Grow(ws.SubMat, subs);
                ws.UV = Workspace.Grow(ws.UV, subs * 2);
                ws.VMat = Workspace.Grow(ws.VMat, subs);
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

                        ws.SubUV[id * 2] = _uvIn != null ? _uvIn[v * 2] : 0d;
                        ws.SubUV[id * 2 + 1] = _uvIn != null ? _uvIn[v * 2 + 1] : 0d;
                        ws.SubMat[id] = _matIn != null ? _matIn[v] : -1;
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
                    double su = ws.SubUV[i * 2], sv = ws.SubUV[i * 2 + 1];
                    var sm = ws.SubMat[i];

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

                            // A UV seam or a material border is two vertices at one position (stage W).
                            if (!_relaxSeams && (ws.VMat[j] != sm || Math.Abs(ws.UV[j * 2] - su) > UvSeam ||
                                                 Math.Abs(ws.UV[j * 2 + 1] - sv) > UvSeam))
                                continue;

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
                    ws.UV[w * 2] = su; ws.UV[w * 2 + 1] = sv;
                    ws.VMat[w] = sm;

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
                ws.BNb0 = Workspace.Grow(ws.BNb0, m);
                ws.BNb1 = Workspace.Grow(ws.BNb1, m);
                ws.BCount = Workspace.Grow(ws.BCount, m);
                ws.Pinned = Workspace.Grow(ws.Pinned, m);
                ws.Stamp = Workspace.Grow(ws.Stamp, m);
                ws.Mark = Workspace.Grow(ws.Mark, m);

                for (var v = 0; v < m; v++)
                {
                    ws.Dead[v] = false;
                    ws.Boundary[v] = false;
                    ws.BCount[v] = 0;
                    ws.Pinned[v] = false;
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

                // WP8: the corners. A boundary vertex with other than two boundary edges is an end or a non-manifold
                // junction; one whose two boundary edges turn by more than 30 degrees is a corner. Neither ever moves
                // (Cost), so silhouettes and openings keep their shape. Before the pushes: Cost reads Pinned.
                if (!_legacy)
                    for (var v = 0; v < m; v++)
                    {
                        if (!ws.Boundary[v]) continue;

                        if (ws.BCount[v] != 2)
                        {
                            ws.Pinned[v] = true;
                            _result.PinnedCorners++;
                            continue;
                        }

                        int n0 = ws.BNb0[v], n1 = ws.BNb1[v];
                        double d1x = ws.P[v * 3] - ws.P[n0 * 3], d1y = ws.P[v * 3 + 1] - ws.P[n0 * 3 + 1], d1z = ws.P[v * 3 + 2] - ws.P[n0 * 3 + 2];
                        double d2x = ws.P[n1 * 3] - ws.P[v * 3], d2y = ws.P[n1 * 3 + 1] - ws.P[v * 3 + 1], d2z = ws.P[n1 * 3 + 2] - ws.P[v * 3 + 2];
                        var l1 = Math.Sqrt(d1x * d1x + d1y * d1y + d1z * d1z);
                        var l2 = Math.Sqrt(d2x * d2x + d2y * d2y + d2z * d2z);

                        if (l1 < 1e-12 || l2 < 1e-12 || (d1x * d2x + d1y * d2y + d1z * d2z) / (l1 * l2) < CornerCosine)
                        {
                            ws.Pinned[v] = true;
                            _result.PinnedCorners++;
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

                // WP8: each end's boundary neighbours (the first two) and its boundary-edge count, for the corners.
                if (ws.BCount[a] == 0) ws.BNb0[a] = b;
                else if (ws.BCount[a] == 1) ws.BNb1[a] = b;
                if (ws.BCount[a] < byte.MaxValue) ws.BCount[a]++;

                if (ws.BCount[b] == 0) ws.BNb0[b] = a;
                else if (ws.BCount[b] == 1) ws.BNb1[b] = a;
                if (ws.BCount[b] < byte.MaxValue) ws.BCount[b]++;

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

            /// <summary>The normalised cost of collapsing a-b and where the survivor goes: the quadric's optimal point
            /// when it is solvable and (WP8) lies within <see cref="MaxPlacementFactor"/> edge lengths of the segment a-b,
            /// else the best of the midpoint, a and b (the midpoint on a tie). A pinned end, or a boundary end whose other
            /// end is interior, does not move: the survivor goes to IT. Two ends that must both stay cost +infinity and
            /// never enter the heap. Under the rollback, the pre-WP8 rule: the optimum, unbounded, else the midpoint.</summary>
            private double Cost(int a, int b, out double x, out double y, out double z)
            {
                var q = _sum;
                for (var i = 0; i < 10; i++) q[i] = _ws.Q[a * 10 + i] + _ws.Q[b * 10 + i];

                double a11 = q[0], a12 = q[1], a13 = q[2], a22 = q[4], a23 = q[5], a33 = q[7];
                double b1 = -q[3], b2 = -q[6], b3 = -q[8];

                var det = a11 * (a22 * a33 - a23 * a23) - a12 * (a12 * a33 - a23 * a13) + a13 * (a12 * a23 - a22 * a13);
                var scale = Math.Abs(a11) + Math.Abs(a22) + Math.Abs(a33);
                var p = _ws.P;
                var solved = scale > 0 && Math.Abs(det) > 1e-9 * scale * scale * scale;

                if (solved)
                {
                    x = (b1 * (a22 * a33 - a23 * a23) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a23 - a22 * b3)) / det;
                    y = (a11 * (b2 * a33 - a23 * b3) - b1 * (a12 * a33 - a23 * a13) + a13 * (a12 * b3 - b2 * a13)) / det;
                    z = (a11 * (a22 * b3 - b2 * a23) - a12 * (a12 * b3 - b2 * a13) + b1 * (a12 * a23 - a22 * a13)) / det;
                }
                else
                {
                    x = (p[a * 3] + p[b * 3]) * 0.5;
                    y = (p[a * 3 + 1] + p[b * 3 + 1]) * 0.5;
                    z = (p[a * 3 + 2] + p[b * 3 + 2]) * 0.5;
                }

                if (!_legacy)
                {
                    if (solved)
                    {
                        // The optimum's distance from the segment a-b: near-rank-2 quadrics (a crease plus planes that
                        // are almost parallel) put it metres along the crease at almost no cost.
                        double abx = p[b * 3] - p[a * 3], aby = p[b * 3 + 1] - p[a * 3 + 1], abz = p[b * 3 + 2] - p[a * 3 + 2];
                        var l2 = abx * abx + aby * aby + abz * abz;
                        var t = l2 > 1e-18
                            ? Math.Max(0d, Math.Min(1d, ((x - p[a * 3]) * abx + (y - p[a * 3 + 1]) * aby + (z - p[a * 3 + 2]) * abz) / l2))
                            : 0d;
                        double dx = x - (p[a * 3] + t * abx), dy = y - (p[a * 3 + 1] + t * aby), dz = z - (p[a * 3 + 2] + t * abz);

                        if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > MaxPlacementFactor * Math.Sqrt(l2) + 1e-6)
                        {
                            solved = false;
                            _result.RejectedPlacement++;
                        }
                    }

                    var ws = _ws;
                    var stayA = ws.Pinned[a] || (ws.Boundary[a] && !ws.Boundary[b]);
                    var stayB = ws.Pinned[b] || (ws.Boundary[b] && !ws.Boundary[a]);

                    if (stayA && stayB)
                    {
                        x = p[a * 3];
                        y = p[a * 3 + 1];
                        z = p[a * 3 + 2];
                        return double.PositiveInfinity;
                    }

                    if (stayA)
                    {
                        x = p[a * 3];
                        y = p[a * 3 + 1];
                        z = p[a * 3 + 2];
                    }
                    else if (stayB)
                    {
                        x = p[b * 3];
                        y = p[b * 3 + 1];
                        z = p[b * 3 + 2];
                    }
                    else if (!solved)
                    {
                        // midpoint FIRST, so an exact tie keeps the midpoint (the anti-fan rationale in step 4)
                        x = (p[a * 3] + p[b * 3]) * 0.5;
                        y = (p[a * 3 + 1] + p[b * 3 + 1]) * 0.5;
                        z = (p[a * 3 + 2] + p[b * 3 + 2]) * 0.5;
                        var best = Error(q, x, y, z);

                        var ea = Error(q, p[a * 3], p[a * 3 + 1], p[a * 3 + 2]);
                        if (ea < best)
                        {
                            best = ea;
                            x = p[a * 3];
                            y = p[a * 3 + 1];
                            z = p[a * 3 + 2];
                        }

                        var eb = Error(q, p[b * 3], p[b * 3 + 1], p[b * 3 + 2]);
                        if (eb < best)
                        {
                            x = p[b * 3];
                            y = p[b * 3 + 1];
                            z = p[b * 3 + 2];
                        }
                    }
                }

                var error = Error(q, x, y, z);
                var weight = _ws.W[a] + _ws.W[b];

                return weight > 0d ? Math.Max(0d, error) / weight : Math.Max(0d, error);
            }

            /// <summary>A summed quadric's error at a point.</summary>
            private static double Error(double[] q, double x, double y, double z) =>
                q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x +
                q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y + q[7] * z * z + 2 * q[8] * z + q[9];

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
                var cost = Cost(a, b, out var x, out var y, out var z);

                // WP8: an edge whose two ends must both stay never collapses, so it never enters the heap.
                if (double.IsPositiveInfinity(cost)) return;

                cost += TieBreak * (ex * ex + ey * ey + ez * ez);

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
                        if (_legacy)
                        {
                            var goal = _relaxed ? _limit : _target;

                            // A round with no collapse while still over the HARD limit and not yet relaxed (every
                            // cheap edge blocked by the fan or distance tests, which the relaxed pass lifts) is not
                            // the end: go relaxed rather than stop over the limit (review F17). Rollback only.
                            if (!_relaxed && sinceRefill == 0 && _live > _limit)
                            {
                                _relaxed = true;
                                _result.Relaxed = true;
                                sinceRefill = 1;
                                Refill();
                                if (_hCount == 0) break;
                                continue;
                            }

                            if (_live <= goal || sinceRefill == 0) break;
                        }
                        else if (_live <= _target || sinceRefill == 0)
                        {
                            break;
                        }

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

                        // WP8: the error limit is where decimation stops, over the hard limit or not - the caller
                        // takes the building's next path. Only the rollback carries on relaxed.
                        if (!_legacy || _live <= _limit) break;

                        // The target is a budget: past the error limit, carry on - cheapest first still -
                        // until the hard limit. Rejected edges were dropped from the heap; every live edge
                        // goes back in so the relaxed pass can reach them.
                        _relaxed = true;
                        _result.Relaxed = true;
                        Refill();
                        sinceRefill = 1;
                        continue;
                    }

                    if (!_relaxSeams && ws.VMat[a] != ws.VMat[b])
                    {
                        _result.RejectedSeam++;
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

                    // WP8: the two fans' longest edge and thinnest face BEFORE the collapse, for the spike and sliver
                    // guards.
                    double maxEdge2Before = 0d, worstAspectBefore = 0d;
                    if (!_legacy) FanShape(a, b, out maxEdge2Before, out worstAspectBefore);

                    // One pass over each fan for the distance limit, the spike and sliver guards and the flip test,
                    // each face's normal computed once.
                    var verdict = Keeps(a, b, a, x, y, z, maxEdge2Before, worstAspectBefore);
                    if (verdict == 0) verdict = Keeps(a, b, b, x, y, z, maxEdge2Before, worstAspectBefore);

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

                    if (verdict == 3)
                    {
                        _result.RejectedEdgeGrowth++;
                        continue;
                    }

                    if (verdict == 4)
                    {
                        _result.RejectedSliver++;
                        continue;
                    }

                    // WP8: Merge keeps a; a pinned b must survive (its position is already b's - Cost put it there).
                    if (!_legacy && ws.Pinned[b] && !ws.Pinned[a])
                    {
                        var swap = a;
                        a = b;
                        b = swap;
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

            /// <summary>WP8: the longest edge (squared) and the thinnest face (e_max^2 / |cross|) among the live faces of
            /// a's and b's fans, before their collapse.</summary>
            private void FanShape(int a, int b, out double maxEdge2, out double worstAspect)
            {
                var ws = _ws;
                var p = ws.P;
                maxEdge2 = 0d;
                worstAspect = 0d;

                for (var side = 0; side < 2; side++)
                    foreach (var f in ws.Vf[side == 0 ? a : b])
                    {
                        if (ws.FaceDead[f]) continue;

                        int c0 = ws.F[f * 3], c1 = ws.F[f * 3 + 1], c2 = ws.F[f * 3 + 2];
                        double ux = p[c1 * 3] - p[c0 * 3], uy = p[c1 * 3 + 1] - p[c0 * 3 + 1], uz = p[c1 * 3 + 2] - p[c0 * 3 + 2];
                        double vx = p[c2 * 3] - p[c0 * 3], vy = p[c2 * 3 + 1] - p[c0 * 3 + 1], vz = p[c2 * 3 + 2] - p[c0 * 3 + 2];
                        double wx = vx - ux, wy = vy - uy, wz = vz - uz;
                        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                        var e2 = Math.Max(ux * ux + uy * uy + uz * uz, Math.Max(vx * vx + vy * vy + vz * vz, wx * wx + wy * wy + wz * wz));
                        var cross = Math.Sqrt(nx * nx + ny * ny + nz * nz);

                        if (e2 > maxEdge2) maxEdge2 = e2;

                        var aspect = cross > 1e-12 ? e2 / cross : double.PositiveInfinity;
                        if (aspect > worstAspect) worstAspect = aspect;
                    }
            }

            /// <summary>
            /// Whether moving <paramref name="moving"/> to (x, y, z) keeps every surviving face around it good:
            /// 0 when it does, 1 when a face degenerates, turns by more than 60 degrees from its current normal
            /// or by more than 90 from its ORIGINAL one (the flip test, only while it is on - see Decimate - bar
            /// the degenerate case, refused always), 2 when the new point is further than the distance limit
            /// from a face's plane (only while the error limit is in force: the mean error's blind spot, one
            /// feature moved a long way); WP8, bar the rollback: 3 when a face's longest edge passes
            /// <see cref="EdgeGrowth"/> x the fans' longest before (a spike), 4 when a face becomes a sliver thinner
            /// than <see cref="SliverAspect"/> and thinner than the fans' thinnest before. ONE pass over the fan,
            /// each face's normal computed once, no allocation.
            /// </summary>
            private int Keeps(int a, int b, int moving, double x, double y, double z, double maxEdge2Before,
                double worstAspectBefore)
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

                    if (!_legacy)
                    {
                        double wx = p2x - p1x, wy = p2y - p1y, wz = p2z - p1z;
                        var e2 = Math.Max(ux * ux + uy * uy + uz * uz, Math.Max(vx * vx + vy * vy + vz * vz, wx * wx + wy * wy + wz * wz));

                        // the spike guard: no single step more than doubles the local edge length
                        if (e2 > EdgeGrowth * EdgeGrowth * maxEdge2Before) return 3;

                        // no NEW sliver: one as thin as the fans already held is the source's own geometry
                        var aspect = e2 / length;
                        if (e2 >= SliverMinEdge * SliverMinEdge && aspect > SliverAspect &&
                            aspect > SliverWorsening * worstAspectBefore)
                            return 4;
                    }

                    if (!_rejectFlips) continue;

                    if ((nx * ox + ny * oy + nz * oz) / length < FlipCosine) return 1;
                    if (nx * ws.F0[f * 3] + ny * ws.F0[f * 3 + 1] + nz * ws.F0[f * 3 + 2] <= 0d) return 1;
                }

                return 0;
            }

            private void Merge(int a, int b, double x, double y, double z)
            {
                var ws = _ws;

                // The survivor's UV follows its position along the edge (stage W): an end's UV at that end, the
                // midpoint's at the midpoint, the quadric's point projected onto the edge.
                double ex = ws.P[b * 3] - ws.P[a * 3], ey = ws.P[b * 3 + 1] - ws.P[a * 3 + 1], ez = ws.P[b * 3 + 2] - ws.P[a * 3 + 2];
                var e2 = ex * ex + ey * ey + ez * ez;
                var t = e2 > 1e-18
                    ? ((x - ws.P[a * 3]) * ex + (y - ws.P[a * 3 + 1]) * ey + (z - ws.P[a * 3 + 2]) * ez) / e2
                    : 0d;
                t = Math.Max(0d, Math.Min(1d, t));

                // Across a border (relaxed retry only) the two UVs are in different textures: the survivor keeps
                // its own.
                if (ws.VMat[a] != ws.VMat[b]) t = 0d;

                ws.UV[a * 2] += (ws.UV[b * 2] - ws.UV[a * 2]) * t;
                ws.UV[a * 2 + 1] += (ws.UV[b * 2 + 1] - ws.UV[a * 2 + 1]) * t;

                ws.P[a * 3] = x; ws.P[a * 3 + 1] = y; ws.P[a * 3 + 2] = z;

                for (var i = 0; i < 10; i++) ws.Q[a * 10 + i] += ws.Q[b * 10 + i];
                ws.W[a] += ws.W[b];
                ws.Boundary[a] |= ws.Boundary[b];
                ws.Pinned[a] |= ws.Pinned[b];

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
                var uvs = _uvIn != null ? new List<float>() : null;
                var mats = _matIn != null ? new List<int>(_live) : null;

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

                            if (uvs != null)
                            {
                                uvs.Add((float)ws.UV[v * 2]);
                                uvs.Add((float)ws.UV[v * 2 + 1]);
                            }
                        }

                        triangles.Add(ws.Map[v]);
                    }

                    mats?.Add(ws.VMat[ws.F[f * 3]]);
                }

                _result.Positions = positions.ToArray();
                _result.Triangles = triangles.ToArray();
                _result.UV = uvs?.ToArray();
                _result.TriangleMaterial = mats?.ToArray();
            }
        }
    }

    /// <summary>
    /// The atlas's shelf packing (stage W), Unity-free so the harness proves no two tiles overlap: tiles taken
    /// by group, then tallest first, placed left to right along a shelf as tall as the first tile on it, a new
    /// shelf above when the row is full or a taller tile starts one, a new page when the page is full; each tile
    /// keeps a border of <c>padding</c> pixels that no other tile's border overlaps. A group may be held to fewer
    /// pages than the cap; a tile that fits no page its group may use is left unplaced (page -1) WITHOUT moving
    /// the packing on, so the smaller tiles after it - and the next group - still fill what is left.
    /// </summary>
    internal static class AtlasPacker
    {
        /// <summary>Packs the tiles. Returns the pages used.</summary>
        /// <param name="widths">Each tile's inner width.</param>
        /// <param name="heights">Each tile's inner height.</param>
        /// <param name="pageSize">A page's side.</param>
        /// <param name="maxPages">Pages allowed.</param>
        /// <param name="padding">The border each tile keeps, pixels.</param>
        /// <param name="pages">Filled: each tile's page, or -1.</param>
        /// <param name="xs">Filled: each tile's inner x.</param>
        /// <param name="ys">Filled: each tile's inner y.</param>
        /// <param name="groups">Each tile's group, or null: lower groups are packed first.</param>
        /// <param name="groupPageLimits">Pages each group may use (indexed by group), or null for maxPages.</param>
        internal static int Pack(int[] widths, int[] heights, int pageSize, int maxPages, int padding, int[] pages,
            int[] xs, int[] ys, int[] groups = null, int[] groupPageLimits = null)
        {
            var n = widths.Length;
            var order = new int[n];
            for (var i = 0; i < n; i++)
            {
                order[i] = i;
                pages[i] = -1;
            }

            Array.Sort(order, (a, b) =>
            {
                var g = groups == null ? 0 : groups[a].CompareTo(groups[b]);
                if (g != 0) return g;

                var c = heights[b].CompareTo(heights[a]);
                if (c != 0) return c;
                c = widths[b].CompareTo(widths[a]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var page = 0;
            var shelfY = 0;         // the current shelf's bottom
            var shelfH = 0;         // its height (outer)
            var cursor = 0;         // the next free x on it
            var used = 0;

            foreach (var i in order)
            {
                var ow = widths[i] + padding * 2;
                var oh = heights[i] + padding * 2;

                if (ow > pageSize || oh > pageSize || widths[i] <= 0 || heights[i] <= 0) continue;

                var group = groups == null ? 0 : groups[i];
                var limit = Math.Min(maxPages,
                    groupPageLimits != null && group >= 0 && group < groupPageLimits.Length ? groupPageLimits[group] : maxPages);

                // a tentative placement, committed only if it lands on a page this group may use
                int tPage = page, tShelfY = shelfY, tShelfH = shelfH, tCursor = cursor;

                if (tCursor + ow > pageSize || (tCursor > 0 && oh > tShelfH))
                {
                    tShelfY += tShelfH;
                    tShelfH = 0;
                    tCursor = 0;
                }

                if (tShelfY + Math.Max(tShelfH, oh) > pageSize)
                {
                    tPage++;
                    tShelfY = 0;
                    tShelfH = 0;
                    tCursor = 0;
                }

                if (tPage >= limit) continue;

                page = tPage;
                shelfY = tShelfY;
                shelfH = Math.Max(tShelfH, oh);
                cursor = tCursor + ow;

                pages[i] = page;
                xs[i] = tCursor + padding;
                ys[i] = tShelfY + padding;
                used = Math.Max(used, page + 1);
            }

            return used;
        }

        /// <summary>Copies rows [rowFrom, rowTo) of a block - the tile repeated rx x ry times, with a border of
        /// <c>padding</c> filled by copies of the block's edge texels (row -padding is the bottom of the gutter) -
        /// into a page at (x, y). RGBA bytes, row 0 at the bottom, in both arrays. Called a slice at a time so a
        /// 1024 x 1024 block is several short steps.</summary>
        /// <param name="page">The page's pixels.</param>
        /// <param name="pageSize">The page's side.</param>
        /// <param name="tile">One repeat's pixels.</param>
        /// <param name="w">Its width.</param>
        /// <param name="h">Its height.</param>
        /// <param name="x">The block's inner x.</param>
        /// <param name="y">The block's inner y.</param>
        /// <param name="rx">Repeats across.</param>
        /// <param name="ry">Repeats up.</param>
        /// <param name="padding">The border.</param>
        /// <param name="rowFrom">First block row, from -padding.</param>
        /// <param name="rowTo">One past the last block row, up to block height + padding.</param>
        internal static void BlitRows(byte[] page, int pageSize, byte[] tile, int w, int h, int x, int y, int rx, int ry,
            int padding, int rowFrom, int rowTo)
        {
            var bw = w * rx;
            var bh = h * ry;

            for (var py = Math.Max(-padding, rowFrom); py < Math.Min(bh + padding, rowTo); py++)
            {
                var ty = y + py;
                if (ty < 0 || ty >= pageSize) continue;

                var sy = Math.Max(0, Math.Min(bh - 1, py)) % h;

                for (var px = -padding; px < bw + padding; px++)
                {
                    var tx = x + px;
                    if (tx < 0 || tx >= pageSize) continue;

                    var sx = Math.Max(0, Math.Min(bw - 1, px)) % w;
                    var o = (ty * pageSize + tx) * 4;
                    var s = (sy * w + sx) * 4;

                    page[o] = tile[s];
                    page[o + 1] = tile[s + 1];
                    page[o + 2] = tile[s + 2];
                    page[o + 3] = tile[s + 3];
                }
            }
        }

        /// <summary>The whole block at once - BlitRows over every row.</summary>
        internal static void Blit(byte[] page, int pageSize, byte[] tile, int w, int h, int x, int y, int rx, int ry,
            int padding) =>
            BlitRows(page, pageSize, tile, w, h, x, y, rx, ry, padding, -padding, h * ry + padding);
    }

    /// <summary>A PNG encoder for the atlas pages (stage W), Unity-free so it runs on a worker. RGBA8, the "Sub"
    /// filter on every row, one zlib stream STREAMED into IDAT chunks of at most <see cref="ChunkBytes"/> - no
    /// whole-file buffer anywhere: the rows go through deflate into a fixed 1 MB chunk buffer that is written out
    /// as each IDAT fills, and every byte written is hashed on its way out. Rows written top first, so row 0 of
    /// the input (the bottom, Unity's texture order) is the PNG's last row and the picture reads the right way
    /// up.</summary>
    internal static class AtlasPng
    {
        /// <summary>The largest IDAT chunk, and the only buffer the encoder holds beside one row.</summary>
        internal const int ChunkBytes = 1 << 20;

        /// <summary>An encoded page: its length, its SHA-256 (lower-case hex), and its bytes when encoded to
        /// memory (Encode - the harness).</summary>
        internal sealed class Encoded
        {
            internal long Length;
            internal string Sha256;
            internal byte[] Bytes;
        }

        private static readonly uint[] Crc = MakeCrc();

        /// <summary>Encodes to memory (the harness's entry).</summary>
        /// <param name="rgba">The pixels, row 0 at the bottom.</param>
        /// <param name="width">The width.</param>
        /// <param name="height">The height.</param>
        internal static Encoded Encode(byte[] rgba, int width, int height)
        {
            using (var memory = new System.IO.MemoryStream())
            {
                var encoded = EncodeTo(memory, rgba, width, height);
                encoded.Bytes = memory.ToArray();
                return encoded;
            }
        }

        /// <summary>Encodes RGBA pixels (row 0 at the bottom) as a PNG straight into a stream, hashing as it
        /// writes.</summary>
        /// <param name="output">Where the PNG goes. Left open.</param>
        /// <param name="rgba">The pixels.</param>
        /// <param name="width">The width.</param>
        /// <param name="height">The height.</param>
        internal static Encoded EncodeTo(System.IO.Stream output, byte[] rgba, int width, int height)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var sink = new Hashed(output, sha);

                sink.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var header = new byte[13];
                BigEndian(header, 0, (uint)width);
                BigEndian(header, 4, (uint)height);
                header[8] = 8;      // bit depth
                header[9] = 6;      // RGBA
                Chunk(sink, "IHDR", header, header.Length);

                var idat = new Idat(sink);
                idat.Write(new byte[] { 0x78, 0x01 }, 0, 2);   // zlib header: deflate, fastest-level hint

                uint a = 1, b = 0;

                using (var deflate = new System.IO.Compression.DeflateStream(idat, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    var row = new byte[width * 4 + 1];

                    for (var y = height - 1; y >= 0; y--)
                    {
                        var start = y * width * 4;
                        row[0] = 1;     // Sub

                        for (var i = 0; i < width * 4; i++)
                        {
                            var left = i >= 4 ? rgba[start + i - 4] : (byte)0;
                            row[i + 1] = (byte)(rgba[start + i] - left);
                        }

                        for (var i = 0; i < row.Length; i++)
                        {
                            a = (a + row[i]) % 65521;
                            b = (b + a) % 65521;
                        }

                        deflate.Write(row, 0, row.Length);
                    }
                }

                var adler = new byte[4];
                BigEndian(adler, 0, (b << 16) | a);
                idat.Write(adler, 0, 4);
                idat.Flush();

                Chunk(sink, "IEND", new byte[0], 0);

                sha.TransformFinalBlock(new byte[0], 0, 0);
                var hex = new System.Text.StringBuilder(64);
                foreach (var h in sha.Hash) hex.Append(h.ToString("x2", CultureInfo.InvariantCulture));

                return new Encoded { Length = sink.Written, Sha256 = hex.ToString() };
            }
        }

        /// <summary>Everything written to the output, hashed and counted on the way.</summary>
        private sealed class Hashed
        {
            private readonly System.IO.Stream _out;
            private readonly System.Security.Cryptography.HashAlgorithm _sha;
            internal long Written;

            internal Hashed(System.IO.Stream output, System.Security.Cryptography.HashAlgorithm sha)
            {
                _out = output;
                _sha = sha;
            }

            internal void Write(byte[] data, int offset, int count)
            {
                if (count <= 0) return;
                _sha.TransformBlock(data, offset, count, null, 0);
                _out.Write(data, offset, count);
                Written += count;
            }
        }

        /// <summary>The zlib stream's sink: bytes collect in one ChunkBytes buffer and go out as an IDAT chunk
        /// each time it fills, and once more on Flush.</summary>
        private sealed class Idat : System.IO.Stream
        {
            private readonly Hashed _sink;
            private readonly byte[] _buffer = new byte[ChunkBytes];
            private int _count;

            internal Idat(Hashed sink)
            {
                _sink = sink;
            }

            public override void Write(byte[] data, int offset, int count)
            {
                while (count > 0)
                {
                    var take = Math.Min(count, _buffer.Length - _count);
                    Buffer.BlockCopy(data, offset, _buffer, _count, take);
                    _count += take;
                    offset += take;
                    count -= take;

                    if (_count == _buffer.Length) Flush();
                }
            }

            public override void Flush()
            {
                if (_count == 0) return;
                Chunk(_sink, "IDAT", _buffer, _count);
                _count = 0;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private static void Chunk(Hashed sink, string type, byte[] data, int length)
        {
            var head = new byte[8];
            BigEndian(head, 0, (uint)length);
            for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];

            var crc = 0xFFFFFFFFu;
            for (var i = 4; i < 8; i++) crc = Crc[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
            for (var i = 0; i < length; i++) crc = Crc[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

            var tail = new byte[4];
            BigEndian(tail, 0, crc ^ 0xFFFFFFFFu);

            sink.Write(head, 0, 8);
            sink.Write(data, 0, length);
            sink.Write(tail, 0, 4);
        }

        private static void BigEndian(byte[] buffer, int at, uint value)
        {
            buffer[at] = (byte)(value >> 24);
            buffer[at + 1] = (byte)(value >> 16);
            buffer[at + 2] = (byte)(value >> 8);
            buffer[at + 3] = (byte)value;
        }

        private static uint[] MakeCrc()
        {
            var table = new uint[256];

            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }

            return table;
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
    /// The triangle budget (WP7): each building's basis is its world box's SURFACE times
    /// <see cref="TrianglesPerSquareMetre"/>, held to [<see cref="MinTriangles"/>, <see cref="MaxTrianglesPerBuilding"/>];
    /// its target is that basis scaled by ONE factor over the whole map, and never below the target the pre-WP7 rule
    /// (6 per m2 of footprint, 60,000 a building, its own scale against 2,700,000) gives the same building on the same
    /// list - so no building's target goes down, on any machine, on any map (Q1). What the map would store - each
    /// building's target, or its source when that is smaller - is held to the cap by the largest factor that fits,
    /// found by bisection. One factor rather than largest-first, because largest-first is what kept 276 of Customs'
    /// 12,906 candidates and dropped every mid-size structure.
    ///
    /// Why surface: a 62 m pylon on a 5x15 m footprint was stored with 288 triangles by the footprint rule; its box
    /// surface is 2,630 m2, a basis of 52,600. Unity-free so the harness proves the rules on the shipped assembly.
    /// </summary>
    internal static class AreaBudget
    {
        /// <summary>What the basis is measured over. Rollback: <see cref="BasisArea.Footprint"/> with
        /// TrianglesPerSquareMetre 6.0 and MaxTrianglesPerBuilding 60,000.</summary>
        internal enum BasisArea
        {
            Surface,
            Footprint
        }

        /// <summary>The basis in use. Static readonly so the choice is not a constant the compiler folds.</summary>
        internal static readonly BasisArea BudgetBasis = BasisArea.Surface;

        /// <summary>Triangles per m2 of box SURFACE (was 6.0 of footprint). Rollback: 6.0.</summary>
        internal const double TrianglesPerSquareMetre = 20.0;

        internal const int MinTriangles = 24;

        /// <summary>A building's largest basis (was 60,000). Rollback: 60,000.</summary>
        internal const int MaxTrianglesPerBuilding = 250_000;

        /// <summary>The pre-WP7 rule, kept ONLY as each building's floor (never a cap).</summary>
        internal const double LegacyTrianglesPerSquareMetre = 6.0;
        internal const int LegacyMaxTrianglesPerBuilding = 60_000;

        /// <summary>The pre-WP7 rule's planned cap: 0.9 x the old fixed 3,000,000.</summary>
        internal const long LegacyPlannedCap = 2_700_000;

        /// <summary>A building's unscaled basis (D2): clamp(20 x surface, 24, 250,000) - or the footprint under the
        /// rollback basis.</summary>
        /// <param name="surface">Its world box's surface, m2.</param>
        /// <param name="footprint">Its world box's footprint, m2.</param>
        internal static double Basis(double surface, double footprint)
        {
            var area = BudgetBasis == BasisArea.Surface ? surface : footprint;

            return Math.Min(MaxTrianglesPerBuilding, Math.Max(MinTriangles, Math.Max(0d, area) * TrianglesPerSquareMetre));
        }

        /// <summary>The pre-WP7 basis: clamp(6 x footprint, 24, 60,000).</summary>
        /// <param name="footprint">Its world box's footprint, m2.</param>
        internal static double LegacyBasis(double footprint) =>
            Math.Min(LegacyMaxTrianglesPerBuilding,
                Math.Max(MinTriangles, Math.Max(0d, footprint) * LegacyTrianglesPerSquareMetre));

        /// <summary>The pre-WP7 target at that rule's scale (D3).</summary>
        internal static int LegacyTarget(double footprint, double legacyScale) => Scaled(LegacyBasis(footprint), legacyScale);

        /// <summary>A basis at a scale, never under <see cref="MinTriangles"/>.</summary>
        internal static int Scaled(double basis, double scale) => Math.Max(MinTriangles, (int)(basis * scale));

        /// <summary>A target: the scaled basis, never under the building's floor.</summary>
        internal static int Target(double basis, double scale, int floor) => Math.Max(floor, Scaled(basis, scale));

        /// <summary>The pre-WP7 targets on this list (D3): the old rule, its constants and its own bisection against
        /// <see cref="LegacyPlannedCap"/> - exactly what the build before WP7 would have given each building.</summary>
        /// <param name="footprints">Each building's footprint, m2.</param>
        /// <param name="sources">Each building's source triangles.</param>
        /// <param name="legacyScale">The old rule's factor on this list.</param>
        internal static int[] LegacyTargets(double[] footprints, long[] sources, out double legacyScale)
        {
            var n = footprints.Length;
            var basis = new double[n];

            for (var i = 0; i < n; i++) basis[i] = LegacyBasis(footprints[i]);

            legacyScale = Scale(basis, null, sources, LegacyPlannedCap);

            var targets = new int[n];
            for (var i = 0; i < n; i++) targets[i] = Scaled(basis[i], legacyScale);

            return targets;
        }

        /// <summary>
        /// The targets with floors (D7): t_i(s) = max(floor_i, max(24, (int)(basis_i x s))), s the largest in [0, 1]
        /// with the sum of min(src_i, t_i(s)) at most the cap. Monotone in s, and t_i(0) = floor_i, so a solution
        /// exists whenever the floors alone fit - which the caller's cap guarantees (at least 0.9 x 3 M, or the whole
        /// demand).
        /// </summary>
        /// <param name="surfaces">Each building's box surface, m2.</param>
        /// <param name="footprints">Each building's footprint, m2.</param>
        /// <param name="sources">Each building's source triangles.</param>
        /// <param name="cap">The planned cap (the map's cap x <see cref="MapMeshBuilder.BudgetShare"/>).</param>
        /// <param name="scale">The factor applied to every basis (1 when the map fits).</param>
        /// <param name="floors">Each building's pre-WP7 target.</param>
        /// <param name="legacyScale">The pre-WP7 rule's factor on this list.</param>
        internal static int[] Targets(double[] surfaces, double[] footprints, long[] sources, long cap, out double scale,
            out int[] floors, out double legacyScale)
        {
            floors = LegacyTargets(footprints, sources, out legacyScale);

            var n = surfaces.Length;
            var basis = new double[n];

            for (var i = 0; i < n; i++) basis[i] = Basis(surfaces[i], footprints[i]);

            scale = Scale(basis, floors, sources, cap);

            var targets = new int[n];
            for (var i = 0; i < n; i++) targets[i] = Target(basis[i], scale, floors[i]);

            return targets;
        }

        /// <summary>What the buildings need (D4): the sum of min(source, max(floor, basis)) - the stored total at
        /// scale 1, and never more than the sources.</summary>
        internal static long Demand(double[] surfaces, double[] footprints, int[] floors, long[] sources)
        {
            var total = 0L;

            for (var i = 0; i < sources.Length; i++)
                total += Math.Min(sources[i], Math.Max(floors[i], (long)Basis(surfaces[i], footprints[i])));

            return total;
        }

        /// <summary>The largest factor in [0, 1] at which the stored total fits the cap: 1 when it fits unscaled,
        /// else by 50 steps of bisection.</summary>
        private static double Scale(double[] basis, int[] floors, long[] sources, long cap)
        {
            if (Stored(basis, floors, sources, 1d) <= cap) return 1d;

            double lo = 0d, hi = 1d;

            for (var step = 0; step < 50; step++)
            {
                var mid = (lo + hi) * 0.5;
                if (Stored(basis, floors, sources, mid) <= cap) lo = mid;
                else hi = mid;
            }

            return lo;
        }

        /// <summary>What the map stores at a scale: each building's target (floored when floors are given), or
        /// its source when that is smaller.</summary>
        /// <param name="basis">The unscaled bases.</param>
        /// <param name="floors">Each building's floor, or null for none.</param>
        /// <param name="sources">The source triangle counts.</param>
        /// <param name="scale">The factor.</param>
        internal static long Stored(double[] basis, int[] floors, long[] sources, double scale)
        {
            var total = 0L;

            for (var i = 0; i < basis.Length; i++)
                total += Math.Min(sources[i], floors != null ? Target(basis[i], scale, floors[i]) : Scaled(basis[i], scale));

            return total;
        }
    }
}
