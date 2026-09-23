using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// at <see cref="BuildingSecondsCap"/> seconds of frames and <see cref="MaxBuildingTriangles"/>
    /// triangles, largest building first, and says in its log line what it cut.
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

        /// <summary>Triangles kept across every building of one map. 300,000 is the budget the plan
        /// fixed: it draws in one mesh per band without an index-format problem and is a fifth of
        /// <see cref="MapMeshFile.MaxTriangles"/>, so the file's own caps are never the thing that
        /// refuses a capture.</summary>
        internal const int MaxBuildingTriangles = 300_000;

        /// <summary>Seconds of frames the whole building phase may take before it stops with what it
        /// has. The relief is 45 ms; the renderer walk and the readbacks are the cost, and a capture
        /// is a key the player pressed mid-raid.</summary>
        private const double BuildingSecondsCap = 20d;

        /// <summary>The cap above, for the one line MapCapture prints before the scene is held: the
        /// player is told how long their view may show the map's hidden geometry, and the number they
        /// are told is the number the loop enforces rather than a second copy of it.</summary>
        internal static double SecondsCap => BuildingSecondsCap;

        /// <summary>Frames a readback is waited for before the mesh is given up as unreadable. The
        /// probe measured two frames for a real one; 120 is two seconds of being wrong.</summary>
        private const int ReadbackFrameCap = 120;

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
        private static readonly string[] NotBuildingLayerNames = { "Foliage", "Grass" };

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

            // --- the y range, and the relief quantised over it -------------------------------------

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

            // --- the buildings ---------------------------------------------------------------------

            if (job.WantsBuildings && job.Candidates.Count > 0)
            {
                var examined = 0;

                for (var i = 0; i < job.Candidates.Count; i++)
                {
                    var candidate = job.Candidates[i];
                    var take = false;

                    if (!Step(job, "a building's size", () => take = Wanted(job, candidate, i))) continue;
                    if (job.Stopped) break;

                    examined++;

                    if (!take)
                    {
                        // A frame for every few hundred candidates REJECTED, not only for every one
                        // read: once the budget is full the rest of an ordered list is walked without a
                        // single read, and a thousand of those in one frame is a visible hitch for work
                        // that produces nothing.
                        if (examined % CandidatesPerFrame == 0) yield return null;

                        continue;
                    }

                    if (candidate.Readable)
                    {
                        Step(job, "a readable building", () => ReadReadable(job, candidate));
                        yield return null;
                    }
                    else
                    {
                        var read = ReadFromGpu(job, candidate);

                        // Disposed however this leaves - including when MoveNext throws and when Unity
                        // or Cleanup disposes THIS iterator mid-readback. Disposing an iterator runs its
                        // finally blocks, and that inner finally is what waits for a readback in flight
                        // and releases its buffers; without this line those two never happen.
                        try
                        {
                            while (read.MoveNext()) yield return read.Current;
                        }
                        finally
                        {
                            (read as IDisposable)?.Dispose();
                        }
                    }
                }

                Step(job, "the buildings' log line", () => ReportBuildings(job));
            }

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
            internal bool Stopped;
            internal string StoppedWhy;

            /// <summary>The largest single vertex-buffer readback this build asked for, for the peak
            /// memory line.</summary>
            internal long PeakReadbackBytes;

            /// <summary>The largest decoded vertex array one building cost, for the same line.</summary>
            internal long PeakDecodedBytes;

            /// <summary>The quantised vertices of the building being assembled, reused for every one
            /// of them so a map of four hundred buildings is not four hundred allocations of each.</summary>
            internal readonly List<ushort> X = new List<ushort>();

            internal readonly List<ushort> Y = new List<ushort>();
            internal readonly List<ushort> Z = new List<ushort>();
            internal readonly List<uint> Indices = new List<uint>();

            /// <summary>The triangle indices of the mesh being read, reused the same way.</summary>
            internal readonly List<int> Triangles3 = new List<int>();

            /// <summary>Local vertex index to stored vertex index for the building being assembled.
            /// Grown to the largest mesh seen and reset per building - only the first vertexCount
            /// entries are ever read, so a reset is that many writes rather than an allocation.</summary>
            internal int[] Remap;

            /// <summary>The chosen LOD level per group, so a group with forty renderers under it is
            /// decided once.</summary>
            internal readonly Dictionary<LODGroup, int> Levels = new Dictionary<LODGroup, int>();

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

            var commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
            var results = default(NativeArray<RaycastHit>);

            try
            {
                results = new NativeArray<RaycastHit>(count, Allocator.TempJob);

                job.ReliefClock.Start();

                // hitMultipleFaces false and one result per command: the nearest hit going down is the
                // answer, and asking for more would cost a wider results array for nothing. Triggers
                // ignored (a trigger volume is not ground) and backfaces not hit (the underside of a
                // roof is not its top).
                var parameters = new QueryParameters(job.RayMask, false, QueryTriggerInteraction.Ignore, false);

                for (var i = 0; i < count; i++)
                {
                    var n = band.Done + i;
                    var col = n % band.Width;
                    var row = n / band.Width;

                    var origin = new Vector3(CellCentreX(job, col), source.CameraY, CellCentreZ(job, row));

                    commands[i] = new RaycastCommand(origin, Vector3.down, parameters, distance);
                }

                RaycastCommand.ScheduleBatch(commands, results, RaysPerJob, default(JobHandle)).Complete();

                for (var i = 0; i < count; i++)
                {
                    var n = band.Done + i;
                    var hit = results[i];

                    // collider, not distance: a RaycastCommand that hit nothing leaves a default
                    // RaycastHit, whose distance is zero - which reads as a hit at the ray's origin.
                    if (hit.collider == null || !IsFinite(hit.point.y) || hit.point.y < floor)
                    {
                        band.Metres[n] = float.NaN;
                        band.Distance[n] = MapMeshFile.DistanceEmpty;
                        continue;
                    }

                    band.Metres[n] = hit.point.y;
                    band.Hits++;

                    var dx = hit.point.x - from.x;
                    var dz = hit.point.z - from.y;

                    band.Distance[n] = MapMeshFile.DistanceStep(Mathf.Sqrt(dx * dx + dz * dz));

                    if (hit.point.y < job.Lowest) job.Lowest = hit.point.y;
                    if (hit.point.y > job.Highest) job.Highest = hit.point.y;
                }

                job.ReliefClock.Stop();
            }
            finally
            {
                if (job.ReliefClock.IsRunning) job.ReliefClock.Stop();
                if (commands.IsCreated) commands.Dispose();
                if (results.IsCreated) results.Dispose();
            }

            band.Done += count;
            job.Rays += count;
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
                $"{Pct(job.Hits, job.Rays)} hit.");

            // A true statement only because the grids are NaN/DistanceEmpty from the moment they are
            // allocated (see Prepare) and FinishBand checks it: an unmeasured cell is empty in the file,
            // not ground at zero.
            if (cells != job.Rays)
                Plugin.LogSource?.LogWarning(
                    $"QuestTree: {job.Request.Map}'s relief cast {N(job.Rays)} of {N(cells)} cell(s) - a chunk " +
                    $"failed (the warning above says why), and the {N(cells - job.Rays)} cell(s) it never " +
                    "reached are written as empty, so a later capture can fill them.");
        }

        // --- the y range and the quantisation -----------------------------------------------------------

        /// <summary>Fixes the file's y range from everything this build will store: every ray that hit
        /// and the world bounds of every building candidate. The candidates go in BEFORE their meshes
        /// are read, which is the point - a renderer's bounds contain its vertices, so a range that
        /// covers the bounds cannot clamp a wall, and the alternative (reading every building first
        /// and keeping its vertices as floats) is the same numbers at three times the memory.
        ///
        /// A map where nothing was hit and nothing was found falls back to the bands' own edges, so
        /// the file is still valid - every cell in it is <see cref="MapMeshFile.NoHit"/>.</summary>
        /// <param name="job">The build.</param>
        private static void SetRange(Job job)
        {
            var low = job.Lowest;
            var high = job.Highest;

            if (!IsFinite(low) || !IsFinite(high) || high < low)
            {
                low = float.PositiveInfinity;
                high = float.NegativeInfinity;

                foreach (var band in job.Bands)
                {
                    if (band.Source.MinY < low) low = band.Source.MinY;
                    if (band.Source.MaxY > high) high = band.Source.MaxY;
                }
            }

            if (!IsFinite(low) || !IsFinite(high) || high < low)
                throw new InvalidOperationException("nothing measurable was found to quantise heights over");

            job.File.SetYRange(low, high);
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
        /// last, and every candidate's bounds folded into the y range the quantisation will use.</summary>
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

                if (!IsFinite(size.x) || !IsFinite(size.y) || !IsFinite(size.z)) continue;
                if (size.y < MinBuildingHeight) continue;
                if (Math.Max(size.x, size.z) < MinBuildingLongSide) continue;

                var centre = bounds.center;
                if (!IsFinite(centre.x) || !IsFinite(centre.y) || !IsFinite(centre.z)) continue;
                if (centre.x < minX || centre.x > maxX || centre.z < minZ || centre.z > maxZ) continue;

                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || mesh.vertexCount < 3) continue;

                job.Candidates.Add(new Candidate
                {
                    Renderer = renderer,
                    Mesh = mesh,
                    Bounds = bounds,
                    Volume = Math.Abs(size.x * size.y * size.z)
                });

                // The bounds, not the vertices: this is what lets the y range be fixed before a single
                // building is read. See SetRange.
                var top = bounds.max.y;
                var bottom = bounds.min.y;

                if (bottom < job.Lowest) job.Lowest = bottom;
                if (top > job.Highest) job.Highest = top;
            }

            if (job.Scanned >= job.RendererCount) job.Renderers = null;
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
            }
        }

        // --- which candidates are taken ------------------------------------------------------------------

        /// <summary>Whether this candidate is read at all: the time cap, the triangle budget, the LOD
        /// choice and the format's own caps, in that order, so the expensive question (is its level
        /// the one to take) is asked only of a candidate there is room for.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="index">Its position in the ordered list, for the "cut" count.</param>
        private static bool Wanted(Job job, Candidate candidate, int index)
        {
            if (job.BuildingClock.Elapsed.TotalSeconds > BuildingSecondsCap)
            {
                job.Stopped = true;
                job.StoppedWhy = $"{N(BuildingSecondsCap)} s spent, {N(job.Candidates.Count - index)} " +
                                 "candidate(s) not looked at";
                return false;
            }

            if (job.File.Buildings.Count >= MapMeshFile.MaxBuildings)
            {
                job.Stopped = true;
                job.StoppedWhy = $"the format's cap of {N(MapMeshFile.MaxBuildings)} buildings was reached";
                return false;
            }

            var mesh = candidate.Mesh;
            var triangles = 0L;

            for (var s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;

            if (triangles <= 0) return false;

            // Before the mesh is read, not after: a candidate the budget has no room for costs
            // nothing at all this way, which is what lets the loop go on looking for smaller ones
            // that still fit.
            if (job.Triangles + triangles > MaxBuildingTriangles)
            {
                job.OverBudget++;
                return false;
            }

            if (mesh.vertexCount > MapMeshFile.MaxVerticesPerBuilding) return false;

            // The format's OTHER total, which the triangle budget does not imply: MaxVerticesTotal is
            // the running sum across every building, and a file over it is one MapMeshFile.Write refuses
            // whole. The mesh's own vertex count is the upper bound on what this building can add (the
            // remap only ever stores fewer), so this is the cheap test that keeps the write safe.
            if (job.Vertices + mesh.vertexCount > MapMeshFile.MaxVerticesTotal)
            {
                job.Stopped = true;
                job.StoppedWhy = $"the format's cap of {N(MapMeshFile.MaxVerticesTotal)} vertices in one file " +
                                 "was reached";
                return false;
            }

            // LAST, because it is the expensive question - GetComponentInParent walks the hierarchy and
            // GetLODs allocates - and every test above can answer without asking it.
            return InChosenLevel(job, candidate);
        }

        /// <summary>Whether this renderer belongs to the LOD level worth keeping, when it is under a
        /// <see cref="LODGroup"/> at all.
        ///
        /// The rule: the LAST level whose renderers' meshes hold at least
        /// <see cref="MinLodTriangles"/> triangles between them and whose materials' shaders are not
        /// impostors - EFT's groups often end in an AmplifyImpostors card, which is a quad with a
        /// picture of the building on it and is worse than nothing in a 3D map. The last such level is
        /// the cheapest honest geometry the game itself is willing to draw, which is exactly what this
        /// feature wants. A renderer on any other level is skipped, so a building does not arrive
        /// three times over.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static bool InChosenLevel(Job job, Candidate candidate)
        {
            // includeInactive, because the capture's hold has just switched a great many culled objects
            // on and the group above a renderer can still be sitting on an inactive parent - and the
            // default overload skips those, which would hand back "no group" and let every level of one
            // building through.
            var group = candidate.Renderer.GetComponentInParent<LODGroup>(true);
            if (group == null) return true;

            if (!job.Levels.TryGetValue(group, out var chosen))
            {
                chosen = ChooseLevel(job, group);
                job.Levels[group] = chosen;
            }

            if (chosen < 0) return false;

            var lods = group.GetLODs();
            if (lods == null || chosen >= lods.Length) return false;

            var renderers = lods[chosen].renderers;
            if (renderers == null) return false;

            foreach (var renderer in renderers)
                if (ReferenceEquals(renderer, candidate.Renderer)) return true;

            return false;
        }

        /// <summary>The level <see cref="InChosenLevel"/> keeps, or -1 when no level of the group is
        /// geometry. Counts the impostor levels it walked past, for the log line.</summary>
        /// <param name="job">The build.</param>
        /// <param name="group">The LOD group.</param>
        private static int ChooseLevel(Job job, LODGroup group)
        {
            var lods = group.GetLODs();
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

        /// <summary>A readable mesh, through the managed arrays Unity is willing to hand over. The
        /// path 80-95 % of the meshes near the player take.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        private static void ReadReadable(Job job, Candidate candidate)
        {
            var mesh = candidate.Mesh;
            var local = mesh.vertices;

            if (local == null || local.Length < 3) return;

            // The job's own list, cleared rather than allocated: one a building over a few hundred
            // buildings of tens of thousands of triangles each is megabytes of garbage for nothing.
            // (mesh.vertices above cannot be reused - Unity allocates that array itself.)
            job.Triangles3.Clear();

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;

                // GetTriangles applies the submesh's base vertex for us, so these index the array
                // above directly.
                var indices = mesh.GetTriangles(s);
                if (indices != null) job.Triangles3.AddRange(indices);
            }

            Assemble(job, candidate, local, job.Triangles3);
        }

        /// <summary>
        /// A mesh the GPU alone holds, through <see cref="AsyncGPUReadback"/> on the buffers
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

            var decoded = false;

            Step(job, "decoding a building's buffers", () =>
                decoded = Decode(job, candidate, vertexBytes, indexBytes));

            if (decoded) job.GpuRead++;
            else job.Unreadable++;

            yield return null;
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

        /// <summary>Turns a vertex stream's and an index buffer's raw bytes into the same
        /// (positions, triangles) pair the readable path produces, then assembles a building out of
        /// them. False when the buffers do not hold what the mesh says they do - a format this does
        /// not decode, or fewer bytes than the vertex count needs.</summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate.</param>
        /// <param name="vertexBytes">The position stream's bytes.</param>
        /// <param name="indexBytes">The index buffer's bytes.</param>
        private static bool Decode(Job job, Candidate candidate, byte[] vertexBytes, byte[] indexBytes)
        {
            var mesh = candidate.Mesh;
            var stride = candidate.Stride;
            var count = mesh.vertexCount;

            if (candidate.Dimension < 3 || stride <= 0 || count < 3) return false;

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
                    // Not a failure of the readback: a normalised or integer position format is one
                    // this build does not decode, and a wrong decode would put a building in the sea.
                    Plugin.LogSource?.LogDebug(
                        $"QuestTree: a building's positions are {candidate.Format}, which the mesh builder does " +
                        "not decode - it is left out.");
                    return false;
            }

            var need = (long)candidate.Offset + (long)(count - 1) * stride + 3L * size;
            if (need > vertexBytes.Length) return false;

            var local = new Vector3[count];

            if (count * 12L > job.PeakDecodedBytes) job.PeakDecodedBytes = count * 12L;

            for (var v = 0; v < count; v++)
            {
                var at = candidate.Offset + v * stride;

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

            var wide = mesh.indexFormat == IndexFormat.UInt32;
            var indexSize = wide ? 4 : 2;

            // The job's reused list, like the readable path's - see ReadReadable.
            var triangles = job.Triangles3;
            triangles.Clear();

            for (var s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = mesh.GetSubMesh(s);
                if (sub.topology != MeshTopology.Triangles) continue;

                var start = (long)sub.indexStart;
                var indices = (long)sub.indexCount;

                if (start < 0 || indices < 0) continue;
                if ((start + indices) * indexSize > indexBytes.Length) continue;

                for (var i = 0L; i < indices; i++)
                {
                    var at = (int)((start + i) * indexSize);

                    // baseVertex by hand: the index buffer holds a submesh-relative index and the
                    // readable path's GetTriangles is the only thing that adds it for you.
                    var index = (wide
                        ? (int)BitConverter.ToUInt32(indexBytes, at)
                        : BitConverter.ToUInt16(indexBytes, at)) + sub.baseVertex;

                    triangles.Add(index);
                }
            }

            if (triangles.Count < 3) return false;

            Assemble(job, candidate, local, triangles);

            return true;
        }

        /// <summary>An IEEE half as a float, written out because System.Half does not exist on
        /// netstandard2.1 and Mathf has no converter.</summary>
        /// <param name="bits">The sixteen bits.</param>
        private static float Half(ushort bits)
        {
            var sign = (bits >> 15) & 1;
            var exponent = (bits >> 10) & 0x1F;
            var mantissa = bits & 0x3FF;

            float value;

            if (exponent == 0) value = mantissa == 0 ? 0f : mantissa * 5.9604645e-8f;
            else if (exponent == 31) value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            else value = (1f + mantissa / 1024f) * Mathf.Pow(2f, exponent - 15);

            return sign == 1 ? -value : value;
        }

        // --- turning a mesh into a building ------------------------------------------------------------------

        /// <summary>
        /// One mesh, in its own local space, as a quantised world-space building in the file.
        ///
        /// What happens to a triangle: its three vertices go through the renderer's
        /// <c>localToWorldMatrix</c>, and the triangle is DROPPED when any of them is not finite or
        /// sits more than <see cref="VertexSlack"/> outside the extent - the quantisation has no room
        /// outside the extent, so keeping it would squash a fence onto the map's edge. A vertex is
        /// quantised the first time a kept triangle uses it and remembered, so the shared vertices of
        /// a building are stored once; the dropped triangles' vertices are never stored at all, which
        /// is what keeps the file to the geometry that is actually on the map.
        ///
        /// Everything the format refuses is checked here rather than at the write, because the write is
        /// one file for the whole map: a building over a cap has to be DROPPED, or one bad renderer
        /// costs the capture its entire mesh. The two per-building rules Write also enforces - no
        /// vertex height of <see cref="MapMeshFile.NoHit"/> and no index past the vertex count - cannot
        /// be broken from here by construction rather than by a check: <see cref="Inside"/> rejects
        /// every non-finite position before it can be quantised (NoHit is what QuantiseHeight answers
        /// for a NaN and nothing else), and an index only ever comes back from <see cref="Store"/>,
        /// which returns a position in the very lists that are about to be handed over. A check for
        /// either would be one that cannot fail.
        /// </summary>
        /// <param name="job">The build.</param>
        /// <param name="candidate">The candidate the mesh came from.</param>
        /// <param name="local">Its vertex positions, in the renderer's local space.</param>
        /// <param name="triangles">Its triangle indices into <paramref name="local"/>. The job's own
        /// reused list, so it is valid only for this call.</param>
        private static void Assemble(Job job, Candidate candidate, Vector3[] local, List<int> triangles)
        {
            if (local == null || triangles == null || triangles.Count < 3) return;

            var file = job.File;
            var matrix = candidate.Renderer.localToWorldMatrix;

            var minX = (float)(file.MinX - VertexSlack);
            var maxX = (float)(file.MaxX + VertexSlack);
            var minZ = (float)(file.MinZ - VertexSlack);
            var maxZ = (float)(file.MaxZ + VertexSlack);

            job.X.Clear();
            job.Y.Clear();
            job.Z.Clear();
            job.Indices.Clear();

            // The remap, GROWN rather than reallocated per building: only the first local.Length entries
            // are ever read, so clearing that many to -1 is all the reset a reused buffer needs.
            if (job.Remap == null || job.Remap.Length < local.Length) job.Remap = new int[local.Length];

            var map = job.Remap;
            for (var i = 0; i < local.Length; i++) map[i] = -1;

            var heightSum = 0d;
            var dropped = 0;

            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var a = triangles[t];
                var b = triangles[t + 1];
                var c = triangles[t + 2];

                if (a < 0 || b < 0 || c < 0 || a >= local.Length || b >= local.Length || c >= local.Length)
                {
                    dropped++;
                    continue;
                }

                var pa = matrix.MultiplyPoint3x4(local[a]);
                var pb = matrix.MultiplyPoint3x4(local[b]);
                var pc = matrix.MultiplyPoint3x4(local[c]);

                if (!Inside(pa, minX, maxX, minZ, maxZ) ||
                    !Inside(pb, minX, maxX, minZ, maxZ) ||
                    !Inside(pc, minX, maxX, minZ, maxZ))
                {
                    dropped++;
                    continue;
                }

                job.Indices.Add((uint)Store(job, map, a, pa, ref heightSum));
                job.Indices.Add((uint)Store(job, map, b, pb, ref heightSum));
                job.Indices.Add((uint)Store(job, map, c, pc, ref heightSum));
            }

            job.DroppedTriangles += dropped;

            var vertices = job.X.Count;
            var kept = job.Indices.Count / 3;

            if (kept == 0 || vertices == 0) return;

            if (vertices > MapMeshFile.MaxVerticesPerBuilding) return;

            // The budget again, against the triangles that actually survived rather than the estimate
            // Wanted checked: a mesh whose triangles were mostly outside the extent is smaller than it
            // looked, and one that is not can still be the one that crosses the line.
            if (job.Triangles + kept > MaxBuildingTriangles)
            {
                job.OverBudget++;

                return;
            }

            var centroid = vertices > 0 ? (float)(heightSum / vertices) : candidate.Bounds.center.y;

            var building = new MapMeshFile.Building
            {
                Key = MapMeshFile.Building.KeyFor(HierarchyPath(candidate.Renderer.transform),
                    candidate.Bounds.center),
                Level = LevelFor(job, centroid),
                X = job.X.ToArray(),
                Y = job.Y.ToArray(),
                Z = job.Z.ToArray(),
                Indices = job.Indices.ToArray()
            };

            // No scan of Y for NoHit and none of Indices for an out-of-range value: see this method's
            // summary - Inside and Store make both impossible here, and a loop over every vertex of
            // every building to prove something that cannot happen is exactly the check this project
            // does not write. MapMeshFile.Write checks them anyway, on the file, where a value that got
            // in by some route nobody thought of is still caught before it is written.
            job.File.Buildings.Add(building);
            job.Kept++;
            job.Triangles += kept;
            job.Vertices += vertices;
        }

        /// <summary>Whether a world position is inside the extent with <see cref="VertexSlack"/> of
        /// slack, and finite.</summary>
        /// <param name="p">The world position.</param>
        /// <param name="minX">The low x bound.</param>
        /// <param name="maxX">The high x bound.</param>
        /// <param name="minZ">The low z bound.</param>
        /// <param name="maxZ">The high z bound.</param>
        private static bool Inside(Vector3 p, float minX, float maxX, float minZ, float maxZ)
        {
            if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z)) return false;

            return p.x >= minX && p.x <= maxX && p.z >= minZ && p.z <= maxZ;
        }

        /// <summary>One vertex quantised into the building being assembled, or the index it already
        /// has. The remap is what keeps a building's shared vertices to one copy and leaves the
        /// vertices of dropped triangles out of the file altogether.</summary>
        /// <param name="job">The build.</param>
        /// <param name="map">Local index to stored index, -1 for a vertex not yet stored.</param>
        /// <param name="index">The local vertex index.</param>
        /// <param name="world">Its world position.</param>
        /// <param name="heightSum">The running sum of stored heights, for the centroid.</param>
        private static int Store(Job job, int[] map, int index, Vector3 world, ref double heightSum)
        {
            if (map[index] >= 0) return map[index];

            var file = job.File;
            var at = job.X.Count;

            job.X.Add(file.QuantiseX(world.x));
            job.Y.Add(file.QuantiseHeight(world.y));
            job.Z.Add(file.QuantiseZ(world.z));

            heightSum += world.y;
            map[index] = at;

            return at;
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

        /// <summary>The building phase's one log line: what was kept, how it was read, and what was
        /// cut.</summary>
        /// <param name="job">The build.</param>
        private static void ReportBuildings(Job job)
        {
            job.BuildingClock.Stop();

            Plugin.LogSource?.LogInfo(
                $"QuestTree: buildings for {job.Request.Map} - {N(job.Kept)} of {N(job.Candidates.Count)} " +
                $"candidates kept ({N(job.Triangles)} triangles), {N(job.GpuRead)} read back off the GPU, " +
                $"{N(job.Unreadable)} unreadable, {N(job.ImpostorSkipped)} impostor LODs skipped, " +
                $"{N(job.ThinSkipped)} thin LODs skipped, " +
                $"{job.BuildingClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s." +
                (job.OverBudget > 0
                    ? $" {N(job.OverBudget)} did not fit the {N(MaxBuildingTriangles)}-triangle budget."
                    : "") +
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

            // The peak is the largest things this build held at once: every band's quantised grids,
            // one band's float heights beside them, the scene's renderer array, the candidate list and
            // its LOD decisions, the largest single readback and its decoded vertices. Everything in it
            // is counted - a "peak" with a term left out is a number that reads as a budget and is not
            // one. What it cannot see is the arrays Unity hands back from mesh.vertices and
            // GetTriangles, which are the game's allocations, not this build's.
            var floats = 0L;

            foreach (var band in file.Bands)
                floats = Math.Max(floats, band.CellCount * 4L);

            // A Candidate is ten fields and a header; the LOD dictionary is one entry per group seen.
            var candidates = job.Candidates.Count * 72L + job.Levels.Count * 32L;
            var buffers = (job.Remap?.Length ?? 0) * 4L + job.Triangles3.Capacity * 4L +
                          job.PeakDecodedBytes;

            var peak = relief + floats + job.RendererCount * 8L + job.PeakReadbackBytes +
                       result.BuildingBytes + candidates + buffers;

            Plugin.LogSource?.LogDebug(
                $"QuestTree: {job.Request.Map}'s mesh holds {file.Describe()}; peak working set about " +
                $"{(peak / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture)} MB " +
                $"(grids {N(relief + floats)} B, {N(job.RendererCount)} renderer(s) scanned, " +
                $"{N(job.Candidates.Count)} candidate(s) held, largest readback " +
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
}
