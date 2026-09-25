using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// The on-disk shape of <c>captures/&lt;key&gt;/&lt;key&gt;-mesh.bin</c>: a map's ground relief as
    /// one height grid per floor band, plus the building shells read off the GPU, quantised to
    /// sixteen bits and deflated.
    ///
    /// Why a file of its own rather than more fields in the meta: this is bulk data - Customs' ground
    /// alone is 559 x 270 cells a band - and the meta is JSON that the Maps tab, the host and
    /// tools/check-capture.py all parse in full on every read. It is also OPTIONAL: a set captured
    /// before this existed, a set from DynamicMaps' art folder and a set synced from an old host all
    /// have no mesh, and every reader has to carry on drawing the 2D picture when it is absent. A
    /// separate file is what makes "absent" cost nothing.
    ///
    /// Why this class is the format rather than each half reading the bytes its own way: the writer is
    /// in-raid (MapMeshBuilder, phase 3A/3C) and the reader is in the menu (UI/Map3DView, phase 3B),
    /// which are different code paths that never run in the same frame and are written by different
    /// hands. One class that both call is the only way the two agree by construction. So the layout
    /// below is FROZEN at <see cref="Version"/> 3 (stage W added the atlas: a page count in the header and per
    /// building its UVs and page ranges; stage X made the textures WRAP: each range carries its material's tile
    /// rect on its page and the raw-UV bounds its vertices' U/V are quantised over, and every vertex belongs to at
    /// most one range); a change to it is a new version number, and
    /// <see cref="Read(Stream)"/> refuses a version it does not know rather than mis-reading it.
    ///
    /// Why quantised: a float32 x/y/z per vertex and a float32 per relief cell doubles the file for
    /// precision nothing can see. Over Customs' 559 m span sixteen bits is 8.5 mm a step, and over the
    /// measured y range (-22..74 m on Customs) 1.5 mm - both far under the 2 m cell the relief is
    /// sampled at and under the size of the pixels the result is drawn into. <see cref="NoHit"/> is
    /// the one reserved code: 0xFFFF means "no value here", which is why a real value saturates at
    /// <see cref="MaxQuantised"/> and never reaches 0xFFFF.
    ///
    /// Why the extent and the y range live in the header and every coordinate is relative to them: the
    /// quantisation is meaningless without its range, and a file that carried the range anywhere else
    /// could be paired with the wrong one. The extent here is the SAME rectangle, to the same doubles,
    /// that the pictures and the meta carry (see MapCapture's geometry contract), so a mesh and a
    /// picture of the same map are in the same space with nothing having to agree twice; a reader that
    /// finds them disagreeing refuses the mesh rather than drawing it in the wrong place.
    ///
    /// Why deflate rather than a raw dump: the relief is mostly smooth ground, so neighbouring cells
    /// share their high bytes and deflate takes 3-5x off it for a few milliseconds of CPU. The magic
    /// and the version are INSIDE the compressed stream - the whole file is one deflate block, which
    /// keeps the writer and the reader to one bracket each. A tool that wants the header inflates the
    /// first bytes (Python: <c>zlib.decompressobj(-15)</c>); it does not get to read the version
    /// without inflating, and that is accepted because the only consumers are this class and
    /// tools/check-capture.py.
    ///
    /// Hostile and truncated input is a first-class case, not an afterthought: this file arrives from
    /// a HOST over the mesh route as well as from this machine's own captures folder, so every count
    /// in it is checked against the caps below BEFORE anything is allocated, every array length is
    /// derived from a checked count rather than trusted, and every triangle index is checked against
    /// its building's vertex count. Every failure is an <see cref="InvalidDataException"/> that names
    /// what was wrong - never an <see cref="IndexOutOfRangeException"/>, an
    /// <see cref="OutOfMemoryException"/> or a 240 GB allocation attempt.
    /// </summary>
    internal sealed class MapMeshFile
    {
        // --- the format's constants --------------------------------------------------------------

        /// <summary>The layout version, written into the header and checked on read. Bumped by ANY
        /// change to the byte layout, including an added field: readers refuse what they do not
        /// know.</summary>
        internal const int Version = 3;

        /// <summary>A tile's largest side in pixels (stage X): each material's texture is captured at min(its size,
        /// this), rounded down to a multiple of <see cref="TileAlign"/>.</summary>
        internal const int AtlasTileMax = 256;

        /// <summary>A tile's sides are multiples of this (and at least this): the viewer compresses each tile it cuts
        /// out of its page to DXT1, which works in 4 x 4 blocks.</summary>
        internal const int TileAlign = 4;

        /// <summary>Atlas pages a file may name (stage W): <c>&lt;key&gt;-atlas-&lt;n&gt;.png</c>, n below this.</summary>
        internal const int MaxAtlasPages = 8;

        /// <summary>An atlas page's side in pixels (stage W): every page is AtlasPageSize x AtlasPageSize RGBA.</summary>
        internal const int AtlasPageSize = 4096;

        /// <summary>An atlas page's file name: <c>&lt;key&gt;-atlas-&lt;n&gt;.png</c>, beside the mesh.</summary>
        /// <param name="mapKey">The map's key.</param>
        /// <param name="page">The page.</param>
        internal static string AtlasFileNameFor(string mapKey, int page) =>
            $"{mapKey}-atlas-{page.ToString(CultureInfo.InvariantCulture)}.png";

        /// <summary>Ranges one building may carry - one per MATERIAL it uses (stage X), and the bound on what a
        /// hostile file can make a reader allocate.</summary>
        internal const int MaxRangesPerBuilding = 64;

        /// <summary>The UV code for the top of a range's bounds (stage X): a stored u is
        /// UMin + code / MaxUv * (UMax - UMin) of the range that uses the vertex - a RAW material UV in repeats of
        /// its tile, which the viewer draws with wrapMode Repeat. v = 0 is the tile's bottom row (Unity's texture
        /// convention, which Texture2D.LoadImage keeps for the page).</summary>
        internal const ushort MaxUv = 0xFFFF;

        /// <summary>The four bytes a mesh file starts with, as text. The digit is part of it - a
        /// mistaken pairing of an old file with a new reader is then caught by the magic as well as by
        /// the version, which is one more chance to catch it before the numbers are read as
        /// geometry.</summary>
        internal const string Magic = "QTM1";

        /// <summary>The quantised value that means "nothing here": a relief cell no ray hit, and the
        /// code a writer uses for a y it could not measure. Reserved, so a real value never reaches
        /// it - see <see cref="MaxQuantised"/>.</summary>
        internal const ushort NoHit = 0xFFFF;

        /// <summary>The largest quantised value a real coordinate takes. One below
        /// <see cref="NoHit"/>, so 0xFFFF is unambiguous in EVERY quantised array in the file - x and
        /// z as well as y. One scale for all three axes rather than a wider one for the axes with no
        /// sentinel: the 8.5 mm-a-step it costs on Customs is invisible, and a reader that has to
        /// remember which axis uses which divisor is a reader that will get it wrong.</summary>
        internal const ushort MaxQuantised = 0xFFFE;

        /// <summary>Metres per step of the per-cell distance byte, the same four metres the picture's
        /// distance sidecar uses (MapCapture.DistanceStepMetres). The same rule on purpose: both
        /// answer the same question - "how far was the player from what this cell records" - and a
        /// merge that mixed two scales would pick the wrong capture's cells.</summary>
        internal const float DistanceStepMetres = 4f;

        /// <summary>The distance byte for a cell nothing has measured. 255 is reserved for it, so a
        /// real cell saturates at <see cref="DistanceMax"/> (1016 m) and can never be mistaken for an
        /// empty one.</summary>
        internal const byte DistanceEmpty = 255;

        /// <summary>The largest distance a cell records, one below <see cref="DistanceEmpty"/>.</summary>
        internal const byte DistanceMax = 254;

        /// <summary>Metres of slack added each side of the measured y range before quantising - see
        /// <see cref="SetYRange"/>. Five metres, because the range is measured from the data this
        /// capture happened to sample and the NEXT capture of the same map may find a roof a little
        /// higher or a pit a little deeper; the slack costs 0.15 mm a step and saves a merge from
        /// clamping.</summary>
        internal const float YSlackMetres = 5f;

        // --- the caps -----------------------------------------------------------------------------
        //
        // Every one of these is checked on WRITE (a builder bug must not produce a file no reader will
        // take) and again on READ, BEFORE the array behind the count is allocated. They are sized to
        // the largest thing this mod can legitimately produce with a comfortable margin, not to the
        // largest thing imaginable: their job is to make a corrupt or hostile count fail in one line
        // instead of in the allocator.

        /// <summary>Bands in one file. MapCapture's MaxFloors is 8 and a band is a floor, so 8.</summary>
        internal const int MaxBands = 8;

        /// <summary>Cells in one band. Customs at 2 m is 151k; 4 M is a 4 km map at 1 m cells, well
        /// past anything the capture will attempt, and 8 bands of it is 96 MB read - inside the
        /// capture's 256 MiB working budget.</summary>
        internal const int MaxCellsPerBand = 4_000_000;

        /// <summary>Buildings in one file. Phase 3C keeps a few hundred per map by the triangle
        /// budget; 20,000 is room for a map made entirely of sheds.</summary>
        internal const int MaxBuildings = 20_000;

        /// <summary>Vertices in one building.</summary>
        internal const int MaxVerticesPerBuilding = 2_000_000;

        /// <summary>Vertices across every building in the file. NOT redundant with
        /// <see cref="MaxVerticesPerBuilding"/> and <see cref="MaxTriangles"/>, which between them
        /// leave a hole a hostile file walks straight through: 20,000 buildings each declaring 2 M
        /// vertices and NO triangles breaks neither of those caps and asks for 240 GB. Twelve million is
        /// twice the triangle cap, which is more vertices than a triangle soup that size can use.
        ///
        /// Raised from 4 M with <see cref="MaxTriangles"/> at stage V, when the building budget went from
        /// 300,000 to 3,000,000 triangles. The format's VERSION is unchanged - the byte layout is - but a
        /// reader built before stage V carries the old caps and refuses a stage-V file with more than 2 M
        /// triangles as over them. Accepted: nothing has been released with the old caps, and the refusal
        /// is a named InvalidDataException, not a misread. A 12 M-vertex file is 72 MB of quantised
        /// arrays read, inside the capture's working budget.</summary>
        internal const int MaxVerticesTotal = 12_000_000;

        /// <summary>Triangles across every building in the file - stage V's building budget
        /// (MapMeshBuilder.MaxBuildingTriangles, 3 M) with headroom, and the bound that keeps the index
        /// arrays to 72 MB read. Was 2 M before stage V - see <see cref="MaxVerticesTotal"/> for what the
        /// raise means for older readers.</summary>
        internal const int MaxTriangles = 6_000_000;

        /// <summary>The suffix that makes a file name a mesh file. Shared by
        /// <see cref="FileNameFor"/> and <see cref="IsMeshFileName"/> so the writer's name and the
        /// sweeps' recogniser cannot drift apart.</summary>
        private const string Suffix = "-mesh.bin";

        /// <summary>Bytes read or written in one pass over a quantised array. 64 KiB is a multiple of
        /// both element sizes, so a chunk never splits an element, and it keeps a 24 MB index array
        /// from needing a 24 MB temporary beside it.</summary>
        private const int ChunkBytes = 64 * 1024;

        // --- the header ---------------------------------------------------------------------------

        /// <summary>The capture's extent: the rectangle in world XZ this file's coordinates are
        /// quantised over, and the same rectangle - to the same doubles - that the meta and the
        /// pictures carry. A reader that finds it disagreeing with the meta's refuses the mesh.</summary>
        internal double MinX;

        /// <summary>The extent's low z edge. Relief row 0 is this edge.</summary>
        internal double MinZ;

        /// <summary>The extent's high x edge.</summary>
        internal double MaxX;

        /// <summary>The extent's high z edge.</summary>
        internal double MaxZ;

        /// <summary>The low end of the quantisation range for EVERY y in the file - relief heights and
        /// building vertices alike. Measured from the data by the writer, with
        /// <see cref="YSlackMetres"/> of slack (<see cref="SetYRange"/>), NOT taken from the band's
        /// own y range: a band on Customs spans -3.5..8.5 m while the rays under it hit ground and
        /// roofs from -17 to 69 m, so the band's range would clamp most of what was measured.</summary>
        internal float YMin = float.NaN;

        /// <summary>The high end of the y quantisation range. See <see cref="YMin"/>.</summary>
        internal float YMax = float.NaN;

        /// <summary>Atlas pages the buildings' ranges may name (0..<see cref="MaxAtlasPages"/>): page n is
        /// the meta's atlas entry n, <c>&lt;key&gt;-atlas-&lt;n&gt;.png</c>. 0 means no texture anywhere.</summary>
        internal int AtlasPages;

        /// <summary>The ground relief, one band per captured floor. Ordered as written; levels are
        /// distinct.</summary>
        internal List<ReliefBand> Bands = new List<ReliefBand>();

        /// <summary>The building shells. Empty in a phase 3A file - the section is written either way
        /// so the format is complete from the first release and a 3A file and a 3C file are the same
        /// file with a different count.</summary>
        internal List<Building> Buildings = new List<Building>();

        // --- names ---------------------------------------------------------------------------------

        /// <summary>The mesh file's name for a map, beside that map's pictures and meta. Deliberately
        /// NOT matched by MapCapture's stale-picture sweep, whose glob is <c>{key}-*.png</c>.</summary>
        /// <param name="mapKey">The map's key, as the capture folder and the pictures use it.</param>
        internal static string FileNameFor(string mapKey) => $"{mapKey}{Suffix}";

        /// <summary>Whether a file name is a mesh file, for the sweeps and the packaging gates. A bare
        /// name, not a path: a value with a directory separator in it is refused rather than half
        /// understood, because every caller here works inside one capture folder.</summary>
        /// <param name="name">The file name to test.</param>
        internal static bool IsMeshFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0) return false;

            // Longer than the suffix, not equal to it: "-mesh.bin" with no key in front of it is not a
            // map's mesh.
            return name.Length > Suffix.Length &&
                   name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }

        // --- the quantisation ----------------------------------------------------------------------

        /// <summary>Sets <see cref="YMin"/> and <see cref="YMax"/> from the lowest and highest y the
        /// writer MEASURED, adding <see cref="YSlackMetres"/> each side. Called once, before anything
        /// is quantised: every ushort in the file is relative to this range, so a range changed
        /// afterwards silently moves geometry already stored.
        ///
        /// A degenerate measurement - one sample, or a perfectly flat map - would leave a zero span and
        /// a division by zero in <see cref="QuantiseHeight"/>, so the span is held to one metre.</summary>
        /// <param name="lowestSeen">The lowest y measured, in metres.</param>
        /// <param name="highestSeen">The highest y measured, in metres.</param>
        internal void SetYRange(float lowestSeen, float highestSeen)
        {
            if (!IsFinite(lowestSeen) || !IsFinite(highestSeen))
                throw new ArgumentException("the measured y range is not finite", nameof(lowestSeen));

            var low = Math.Min(lowestSeen, highestSeen) - YSlackMetres;
            var high = Math.Max(lowestSeen, highestSeen) + YSlackMetres;

            if (high - low < 1f) high = low + 1f;

            YMin = low;
            YMax = high;
        }

        /// <summary>A y in metres as the sixteen-bit code this file stores. A value outside
        /// [<see cref="YMin"/>, <see cref="YMax"/>] CLAMPS rather than wrapping - the slack makes that
        /// vanishingly rare, and a clamped roof is a roof at the wrong height while a wrapped one is a
        /// roof in the basement. A non-finite y becomes <see cref="NoHit"/>, because the one honest
        /// answer for "no measurement" is the code that means it.</summary>
        /// <param name="y">The height in metres.</param>
        internal ushort QuantiseHeight(float y) => IsFinite(y) ? Quantise(y, YMin, YMax) : NoHit;

        /// <summary>A stored height code back in metres, or <see cref="float.NaN"/> for
        /// <see cref="NoHit"/> - the caller must test for it, and NaN is the value that makes a caller
        /// that forgot fail visibly rather than draw ground at zero.</summary>
        /// <param name="code">The stored code.</param>
        internal float HeightOf(ushort code) =>
            code == NoHit ? float.NaN : (float)Dequantise(code, YMin, YMax);

        /// <summary>A world x as the code this file stores, quantised over the extent's x span.</summary>
        /// <param name="x">The world x in metres.</param>
        internal ushort QuantiseX(float x) => Quantise(x, MinX, MaxX);

        /// <summary>A world z as the code this file stores, quantised over the extent's z span.</summary>
        /// <param name="z">The world z in metres.</param>
        internal ushort QuantiseZ(float z) => Quantise(z, MinZ, MaxZ);

        /// <summary>A stored x code back in world metres.</summary>
        /// <param name="code">The stored code.</param>
        internal float XOf(ushort code) => (float)Dequantise(code, MinX, MaxX);

        /// <summary>A stored z code back in world metres.</summary>
        /// <param name="code">The stored code.</param>
        internal float ZOf(ushort code) => (float)Dequantise(code, MinZ, MaxZ);

        /// <summary>Metres from the capturing player as the byte a cell stores: the same
        /// round-to-nearest-step, saturate-at-254 rule the picture's distance sidecar uses
        /// (MapCapture.Steps), so the two can be compared and merged by the same test.</summary>
        /// <param name="metres">Distance from the capturing player.</param>
        internal static byte DistanceStep(float metres)
        {
            if (!IsFinite(metres) || metres <= 0f) return 0;

            var steps = (int)(metres / DistanceStepMetres + 0.5f);

            return steps >= DistanceMax ? DistanceMax : (byte)steps;
        }

        /// <summary>A stored distance byte back in metres, or <see cref="float.NaN"/> for
        /// <see cref="DistanceEmpty"/>.</summary>
        /// <param name="step">The stored byte.</param>
        internal static float DistanceMetres(byte step) =>
            step == DistanceEmpty ? float.NaN : step * DistanceStepMetres;

        /// <summary>The one quantiser. Rounds to nearest and clamps into
        /// [0, <see cref="MaxQuantised"/>] so <see cref="NoHit"/> stays reserved.</summary>
        /// <param name="value">The value to store.</param>
        /// <param name="min">The low end of its range.</param>
        /// <param name="max">The high end of its range.</param>
        private static ushort Quantise(double value, double min, double max)
        {
            var span = max - min;
            if (!(span > 0d)) return 0;

            var t = (value - min) / span;
            if (!(t > 0d)) return 0;            // also catches NaN, which fails every comparison
            if (t >= 1d) return MaxQuantised;

            var code = (int)(t * MaxQuantised + 0.5d);

            return code >= MaxQuantised ? MaxQuantised : (ushort)code;
        }

        /// <summary>The one dequantiser, exactly the inverse scale of <see cref="Quantise"/>.</summary>
        /// <param name="code">The stored code.</param>
        /// <param name="min">The low end of its range.</param>
        /// <param name="max">The high end of its range.</param>
        private static double Dequantise(ushort code, double min, double max) =>
            min + (max - min) * code / MaxQuantised;

        // --- reading the contents ------------------------------------------------------------------

        /// <summary>The band with this level, or null. The floor peel and a building's home band are
        /// both found by level rather than by list position, because nothing promises the bands are
        /// written in level order.</summary>
        /// <param name="level">The floor level, 0 being the ground floor.</param>
        internal ReliefBand Band(int level)
        {
            if (Bands == null) return null;

            for (var i = 0; i < Bands.Count; i++)
                if (Bands[i] != null && Bands[i].Level == level) return Bands[i];

            return null;
        }

        /// <summary>Points every band and building at this file, which is where they read the extent
        /// and the y range their coordinates are relative to. Called by <see cref="Write"/> and by
        /// <see cref="Read(Stream)"/>, so a file that has been through either is bound; a BUILDER that
        /// fills the lists by hand calls it before using <see cref="Building.VertexAt"/> or
        /// <see cref="ReliefBand.TryHeightAt"/>.
        ///
        /// Why a back-pointer instead of copying the six range numbers into every band and building:
        /// two copies of one number are two numbers that can disagree, and the one thing this format
        /// cannot survive is geometry quantised over one range and read back over another.</summary>
        internal void Bind()
        {
            if (Bands != null)
                for (var i = 0; i < Bands.Count; i++)
                    Bands[i]?.BindTo(this);

            if (Buildings != null)
                for (var i = 0; i < Buildings.Count; i++)
                    Buildings[i]?.BindTo(this);
        }

        /// <summary>Cells across every band.</summary>
        internal long CellCount()
        {
            var total = 0L;
            if (Bands == null) return 0L;

            for (var i = 0; i < Bands.Count; i++)
                if (Bands[i] != null) total += (long)Bands[i].Width * Bands[i].Height;

            return total;
        }

        /// <summary>Triangles across every building.</summary>
        internal long TriangleCount()
        {
            var total = 0L;
            if (Buildings == null) return 0L;

            for (var i = 0; i < Buildings.Count; i++)
                if (Buildings[i] != null) total += Buildings[i].TriangleCount;

            return total;
        }

        /// <summary>What this file costs in memory once loaded, in bytes: the quantised arrays plus a
        /// nominal allowance per object. For the log line and for the viewer's budget, NOT for the
        /// file's size on disk - deflate takes 3-5x off the arrays.</summary>
        internal long ApproximateBytes()
        {
            var total = 0L;

            if (Bands != null)
                foreach (var band in Bands)
                {
                    if (band == null) continue;
                    total += 64;
                    total += (band.Heights?.Length ?? 0) * 2L;
                    total += band.Distance?.Length ?? 0;
                }

            if (Buildings != null)
                foreach (var building in Buildings)
                {
                    if (building == null) continue;
                    total += 64;
                    total += (building.X?.Length ?? 0) * 2L;
                    total += (building.Y?.Length ?? 0) * 2L;
                    total += (building.Z?.Length ?? 0) * 2L;
                    total += (building.Indices?.Length ?? 0) * 4L;
                    total += ((building.U?.Length ?? 0) + (building.V?.Length ?? 0)) * 2L;
                    total += (building.Ranges?.Count ?? 0) * 36L;
                }

            return total;
        }

        /// <summary>One line for a log: what is in this file and what it costs. The numbers a capture's
        /// or a load's line is read for.</summary>
        internal string Describe()
        {
            var bands = Bands?.Count ?? 0;
            var buildings = Buildings?.Count ?? 0;
            var mb = ApproximateBytes() / (1024d * 1024d);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} band(s), {1:#,##0} cells, {2:#,##0} building(s), {3:#,##0} triangles, {4:0.00} MB",
                bands, CellCount(), buildings, TriangleCount(), mb);
        }

        // --- one band of relief ---------------------------------------------------------------------

        /// <summary>One floor's ground: a height and a distance per cell of a regular grid over the
        /// file's extent.
        ///
        /// The grid is ROW-MAJOR with row 0 at the extent's MinZ edge and column 0 at MinX - world
        /// order, NOT the picture's. The pictures put world +z at image TOP (MapCapture's geometry
        /// contract), so a band and its picture are flipped in z relative to each other. Stated here
        /// because it is the one thing a viewer gets wrong: a relief drawn from these rows with the
        /// picture's UVs comes out mirrored, and mirrored ground looks plausible until a landmark is
        /// checked.</summary>
        internal sealed class ReliefBand
        {
            /// <summary>The floor this band is the ground of, 0 being the ground floor, positive up -
            /// the same numbering the meta's floors and the zone file's floors carry.</summary>
            internal int Level;

            /// <summary>The cell size in metres, 2 m as phase 3-0 settled it. Per band rather than per
            /// file so a future capture can sample a small map finer without a version bump.</summary>
            internal float CellMetres;

            /// <summary>Columns, along +x. <c>ceil(span / CellMetres)</c>, so the grid covers the
            /// extent and may overhang its far edge by up to one cell.</summary>
            internal int Width;

            /// <summary>Rows, along +z.</summary>
            internal int Height;

            /// <summary>The height of each cell's centre, quantised over the file's y range,
            /// <see cref="NoHit"/> where no ray hit. Row-major, <c>row * Width + col</c>, length
            /// <c>Width * Height</c>.</summary>
            internal ushort[] Heights;

            /// <summary>How far the capturing player was from each cell, in
            /// <see cref="DistanceStepMetres"/> steps, <see cref="DistanceEmpty"/> for a cell with no
            /// height. Same layout and length as <see cref="Heights"/>.
            ///
            /// Phase 3-0 measured colliders NOT streaming out - 100 % of cells hit from either end of
            /// Customs - so nothing needs this to merge today. It is written anyway: it costs one byte
            /// a cell against a two-byte height, it is the only evidence available if a future map
            /// does stream, and a merge rule that exists in the format from version 1 is a merge rule
            /// that never needs a version bump.</summary>
            internal byte[] Distance;

            private MapMeshFile _file;

            /// <summary>Cells in this band.</summary>
            internal long CellCount => (long)Width * Height;

            /// <summary>Points this band at the file whose extent its cells are laid out over. See
            /// <see cref="MapMeshFile.Bind"/>.</summary>
            /// <param name="file">The file this band belongs to.</param>
            internal void BindTo(MapMeshFile file) => _file = file;

            /// <summary>The world x of a column's cell CENTRE - the ray that measured it was cast
            /// through the centre, so the centre is where its height is true.</summary>
            /// <param name="col">The column, 0 at the extent's MinX edge.</param>
            internal float CellCentreX(int col) =>
                (float)(Owner().MinX + (col + 0.5) * CellMetres);

            /// <summary>The world z of a row's cell centre.</summary>
            /// <param name="row">The row, 0 at the extent's MinZ edge.</param>
            internal float CellCentreZ(int row) =>
                (float)(Owner().MinZ + (row + 0.5) * CellMetres);

            /// <summary>The height code at a cell, or <see cref="NoHit"/> outside the grid.</summary>
            /// <param name="col">The column.</param>
            /// <param name="row">The row.</param>
            internal ushort CodeAt(int col, int row)
            {
                if (Heights == null || col < 0 || row < 0 || col >= Width || row >= Height) return NoHit;

                return Heights[row * Width + col];
            }

            /// <summary>The distance byte at a cell, or <see cref="DistanceEmpty"/> outside the
            /// grid.</summary>
            /// <param name="col">The column.</param>
            /// <param name="row">The row.</param>
            internal byte DistanceAt(int col, int row)
            {
                if (Distance == null || col < 0 || row < 0 || col >= Width || row >= Height)
                    return DistanceEmpty;

                return Distance[row * Width + col];
            }

            /// <summary>The ground height at an arbitrary world point, bilinear over the four cell
            /// centres around it. False when the point is outside the grid or all four of those cells
            /// are <see cref="NoHit"/> - which is why this is a Try: the relief has holes, and a caller
            /// that needs a height for a pin has to decide what to do about one rather than be handed
            /// a zero.
            ///
            /// Cells that are <see cref="NoHit"/> are dropped and the remaining weights
            /// RENORMALISED, rather than counted as zero: one missing corner beside a hole would
            /// otherwise pull the ground down towards y = <see cref="YMin"/> for the three cells
            /// around it, which reads as a crater. Sampling in cell-centre space means the half-cell
            /// border around the grid has only one row of centres to interpolate from; there the
            /// nearest centres win, which is the same answer clamping would give and is exact at the
            /// centres themselves.</summary>
            /// <param name="x">World x in metres.</param>
            /// <param name="z">World z in metres.</param>
            /// <param name="y">The height in metres, when there is one.</param>
            internal bool TryHeightAt(float x, float z, out float y)
            {
                y = float.NaN;

                if (Heights == null || Width <= 0 || Height <= 0 || !(CellMetres > 0f)) return false;
                if (!IsFinite(x) || !IsFinite(z)) return false;

                var file = Owner();

                // Position in cell-centre units: 0 is the first cell's CENTRE, not the extent's edge.
                var fx = (x - file.MinX) / CellMetres - 0.5d;
                var fz = (z - file.MinZ) / CellMetres - 0.5d;

                if (fx < -0.5d || fx > Width - 0.5d || fz < -0.5d || fz > Height - 0.5d) return false;

                var col0 = (int)Math.Floor(fx);
                var row0 = (int)Math.Floor(fz);
                var tx = fx - col0;
                var tz = fz - row0;

                var col1 = col0 + 1;
                var row1 = row0 + 1;

                if (col0 < 0) { col0 = 0; }
                if (row0 < 0) { row0 = 0; }
                if (col1 > Width - 1) { col1 = Width - 1; }
                if (row1 > Height - 1) { row1 = Height - 1; }
                if (col0 > Width - 1) { col0 = Width - 1; }
                if (row0 > Height - 1) { row0 = Height - 1; }

                var sum = 0d;
                var weight = 0d;
                var anyCode = NoHit;

                Accumulate(col0, row0, (1d - tx) * (1d - tz), ref sum, ref weight, ref anyCode);
                Accumulate(col1, row0, tx * (1d - tz), ref sum, ref weight, ref anyCode);
                Accumulate(col0, row1, (1d - tx) * tz, ref sum, ref weight, ref anyCode);
                Accumulate(col1, row1, tx * tz, ref sum, ref weight, ref anyCode);

                if (anyCode == NoHit) return false;

                // Exactly on a cell centre with only the diagonal corner measured, the four weights
                // that survive can all be zero. There is still an answer - the one cell that has a
                // height - and returning false there would punch a hole in the middle of measured
                // ground.
                y = weight > 1e-9d ? (float)(sum / weight) : _file.HeightOf(anyCode);

                return true;
            }

            /// <summary>One corner of the bilinear sample, ignored when the cell has no height.</summary>
            /// <param name="col">The column.</param>
            /// <param name="row">The row.</param>
            /// <param name="w">Its bilinear weight.</param>
            /// <param name="sum">The running weighted sum of heights.</param>
            /// <param name="weight">The running sum of the weights that counted.</param>
            /// <param name="anyCode">Any measured code seen, for the all-weights-zero case.</param>
            private void Accumulate(int col, int row, double w, ref double sum, ref double weight,
                ref ushort anyCode)
            {
                var code = CodeAt(col, row);
                if (code == NoHit) return;

                if (anyCode == NoHit) anyCode = code;

                sum += _file.HeightOf(code) * w;
                weight += w;
            }

            /// <summary>The file this band's coordinates are relative to, or a failure that says which
            /// call was missed. An unbound band would otherwise dequantise against a null reference,
            /// and a NullReferenceException from inside a bilinear sample says nothing about why.</summary>
            private MapMeshFile Owner() =>
                _file ?? throw new InvalidOperationException(
                    "this relief band is not bound to a MapMeshFile - call MapMeshFile.Bind() after " +
                    "filling Bands by hand (Read and Write bind for you)");
        }

        // --- one atlas range -------------------------------------------------------------------------

        /// <summary>Triangles of one building drawn with one material's tile (stage X): indices
        /// [First, First + Count) of its <see cref="Building.Indices"/>, the tile's pixel rect on its atlas page
        /// (TileX/TileY from the page's BOTTOM-LEFT, W and H multiples of <see cref="TileAlign"/>), and the raw-UV
        /// bounds its vertices' U/V codes are quantised over. Byte order: int Page, First, Count; ushort TileX,
        /// TileY, TileW, TileH; float UMin, UMax, VMin, VMax.</summary>
        internal struct AtlasRange
        {
            /// <summary>The atlas page, below the file's <see cref="AtlasPages"/>.</summary>
            internal int Page;

            /// <summary>The first index (a multiple of 3).</summary>
            internal int First;

            /// <summary>Indices in the range (a positive multiple of 3).</summary>
            internal int Count;

            /// <summary>The tile's rect on its page, pixels from the page's bottom-left.</summary>
            internal ushort TileX;

            internal ushort TileY;
            internal ushort TileW;
            internal ushort TileH;

            /// <summary>The raw-UV bounds (in repeats of the tile) the range's vertices are quantised over.</summary>
            internal float UMin;

            internal float UMax;
            internal float VMin;
            internal float VMax;

            /// <summary>A stored u code as this range's raw u.</summary>
            /// <param name="code">The code.</param>
            internal float U(ushort code) => UMin + code / (float)MaxUv * (UMax - UMin);

            /// <summary>A stored v code as this range's raw v.</summary>
            /// <param name="code">The code.</param>
            internal float V(ushort code) => VMin + code / (float)MaxUv * (VMax - VMin);
        }

        // --- one building ----------------------------------------------------------------------------

        /// <summary>One building's shell: a quantised triangle soup in WORLD space, already
        /// transformed out of its renderer's local space by the builder.
        ///
        /// World space rather than local plus a matrix, deliberately: the viewer draws these as one
        /// mesh per band with no scene graph behind them, a matrix would be 64 more bytes and another
        /// thing to get wrong, and the quantisation has to be over the file's extent anyway - which is
        /// a world-space range. The cost is that a building cannot be re-instanced; nothing wants
        /// to.</summary>
        internal sealed class Building
        {
            /// <summary>A stable identity for this building, so captures UNION instead of duplicating:
            /// a second capture of the same map re-reads the same renderers, and without a key the
            /// merge would either stack two copies of Big Red or have to compare geometry. Computed by
            /// the builder from the renderer's hierarchy path and its rounded world bounds centre -
            /// see <see cref="KeyFor"/>, which is the frozen rule both the builder and any future
            /// merge use.</summary>
            internal int Key;

            /// <summary>The band this building belongs to: the one whose y range holds its centroid,
            /// or the nearest when none does. What the floor peel reads - a building is drawn when its
            /// band is drawn. A reader that finds a level with no band should fall back to the nearest
            /// band rather than dropping the building; the writer never produces one, but a file from a
            /// future builder might.</summary>
            internal int Level;

            /// <summary>Vertex x, quantised over the extent's x span. Same length as
            /// <see cref="Y"/> and <see cref="Z"/>.</summary>
            internal ushort[] X;

            /// <summary>Vertex y, quantised over the file's y range - the same scale the relief
            /// heights use, so a wall's foot and the ground under it dequantise to the same
            /// number.</summary>
            internal ushort[] Y;

            /// <summary>Vertex z, quantised over the extent's z span.</summary>
            internal ushort[] Z;

            /// <summary>Triangle indices, three per triangle, each less than the vertex count. uint
            /// rather than ushort because a building past 65k vertices is ordinary and Unity's
            /// IndexFormat.UInt32 takes these as they are.</summary>
            internal uint[] Indices;

            /// <summary>Per-vertex texture codes (stage X), quantised over the bounds of the ONE range whose triangles
            /// use the vertex (<see cref="AtlasRange.U"/>), or null when no triangle of this building is textured.
            /// Same length as <see cref="X"/> when present; a vertex used by no range carries 0.</summary>
            internal ushort[] U;

            /// <summary>See <see cref="U"/>.</summary>
            internal ushort[] V;

            /// <summary>The textured triangles, as ranges of <see cref="Indices"/> drawn with one material's tile
            /// each: ascending, not overlapping, each a whole number of triangles, and no vertex used by two of
            /// them. A triangle in no range has no captured texture and is drawn the stage U/V way (side views,
            /// top picture, tint).</summary>
            internal List<AtlasRange> Ranges = new List<AtlasRange>();

            private MapMeshFile _file;

            /// <summary>Vertex i's CODE as a fraction in [0, 1] - the stage W reading, kept only so a viewer still on
            /// v2 semantics compiles while it moves to <see cref="UOf(int, AtlasRange)"/>; in v3 this is NOT a page
            /// coordinate.</summary>
            /// <param name="i">The vertex index.</param>
            internal float UOf(int i) => U == null ? 0f : U[i] / (float)MaxUv;

            /// <summary>See <see cref="UOf(int)"/>.</summary>
            /// <param name="i">The vertex index.</param>
            internal float VOf(int i) => V == null ? 0f : V[i] / (float)MaxUv;

            /// <summary>Vertex i's raw u, over the bounds of the range that uses it.</summary>
            /// <param name="i">The vertex index.</param>
            /// <param name="range">The range whose triangles use it.</param>
            internal float UOf(int i, AtlasRange range) => U == null ? 0f : range.U(U[i]);

            /// <summary>Vertex i's raw v, over the bounds of the range that uses it.</summary>
            /// <param name="i">The vertex index.</param>
            /// <param name="range">The range whose triangles use it.</param>
            internal float VOf(int i, AtlasRange range) => V == null ? 0f : range.V(V[i]);

            /// <summary>Vertices in this building.</summary>
            internal int VertexCount => X?.Length ?? 0;

            /// <summary>Triangles in this building.</summary>
            internal int TriangleCount => (Indices?.Length ?? 0) / 3;

            /// <summary>Points this building at the file whose ranges its vertices are quantised over.
            /// See <see cref="MapMeshFile.Bind"/>.</summary>
            /// <param name="file">The file this building belongs to.</param>
            internal void BindTo(MapMeshFile file) => _file = file;

            /// <summary>One vertex back in world metres, ready for a Mesh's vertex buffer.</summary>
            /// <param name="i">The vertex index.</param>
            internal Vector3 VertexAt(int i)
            {
                var file = _file ?? throw new InvalidOperationException(
                    "this building is not bound to a MapMeshFile - call MapMeshFile.Bind() after " +
                    "filling Buildings by hand (Read and Write bind for you)");

                if (X == null || Y == null || Z == null || i < 0 || i >= X.Length)
                    throw new ArgumentOutOfRangeException(nameof(i),
                        $"vertex {i} of {VertexCount} in building {Key}");

                return new Vector3(file.XOf(X[i]), file.HeightOf(Y[i]), file.ZOf(Z[i]));
            }

            /// <summary>The frozen rule for <see cref="Key"/>: an FNV-1a hash of the renderer's
            /// hierarchy path and its world bounds centre rounded to half a metre.
            ///
            /// Why the path AND the centre: EFT's scenes are full of identically named objects under
            /// identically named parents (every "karkas" under every "building"), so the path alone
            /// collides; and a path can differ between two loads of the same map if the scene builds
            /// its hierarchy in a different order, so the centre alone is what actually identifies the
            /// thing. Half a metre of rounding absorbs the float wobble between two loads without
            /// merging two real neighbours.
            ///
            /// Why FNV-1a and not string.GetHashCode: GetHashCode is not stable across runtimes or
            /// runs, and this key is written to a file that a LATER session merges against.</summary>
            /// <param name="hierarchyPath">The renderer's path in the scene, parents first.</param>
            /// <param name="boundsCentre">Its world bounds centre.</param>
            internal static int KeyFor(string hierarchyPath, Vector3 boundsCentre)
            {
                unchecked
                {
                    const uint prime = 16777619u;
                    var hash = 2166136261u;

                    if (hierarchyPath != null)
                        for (var i = 0; i < hierarchyPath.Length; i++)
                        {
                            hash = (hash ^ hierarchyPath[i]) * prime;
                            hash = (hash ^ (uint)(hierarchyPath[i] >> 8)) * prime;
                        }

                    hash = Mix(hash, boundsCentre.x);
                    hash = Mix(hash, boundsCentre.y);
                    hash = Mix(hash, boundsCentre.z);

                    return (int)hash;
                }
            }

            /// <summary>One coordinate into a key hash, rounded to half a metre first.</summary>
            /// <param name="hash">The hash so far.</param>
            /// <param name="value">The coordinate.</param>
            private static uint Mix(uint hash, float value)
            {
                unchecked
                {
                    const uint prime = 16777619u;

                    var rounded = IsFinite(value) ? (int)Math.Round(value * 2d, MidpointRounding.AwayFromZero) : 0;
                    var bits = (uint)rounded;

                    for (var shift = 0; shift < 32; shift += 8)
                        hash = (hash ^ ((bits >> shift) & 0xFFu)) * prime;

                    return hash;
                }
            }
        }

        // --- writing -----------------------------------------------------------------------------------

        /// <summary>
        /// Writes a mesh file into a stream: one deflate block holding the header, the bands and the
        /// buildings, little-endian throughout.
        ///
        /// THE LAYOUT (version 1), in order, all integers signed 32-bit little-endian unless said
        /// otherwise:
        /// <code>
        ///   magic         4 bytes   'Q' 'T' 'M' '1'
        ///   version       int32     1
        ///   minX          float64   the extent, the same doubles the meta carries
        ///   minZ          float64
        ///   maxX          float64
        ///   maxZ          float64
        ///   yMin          float32   the y quantisation range, measured with 5 m slack
        ///   yMax          float32
        ///   bandCount     int32     0..8
        ///   per band:
        ///     level       int32
        ///     cellMetres  float32
        ///     width       int32     columns, along +x
        ///     height      int32     rows, along +z
        ///     heights     uint16 * width * height   row-major, row 0 = minZ, col 0 = minX, 0xFFFF = no hit
        ///     distance    uint8  * width * height   same layout, 255 = none, else metres / 4
        ///   buildingCount int32     0..20000
        ///   per building:
        ///     key         int32     stable identity, for the union across captures
        ///     level       int32     the band it is drawn with
        ///     vertexCount int32
        ///     x           uint16 * vertexCount   over [minX, maxX]
        ///     y           uint16 * vertexCount   over [yMin, yMax]
        ///     z           uint16 * vertexCount   over [minZ, maxZ]
        ///     indexCount  int32     a multiple of 3
        ///     indices     uint32 * indexCount    each &lt; vertexCount
        /// </code>
        ///
        /// The whole of that is inside ONE <see cref="DeflateStream"/>, magic included; the stream is
        /// left open for the caller, who is usually staging bytes into a MemoryStream for
        /// MapCapture's Stage/Commit pair.
        ///
        /// The caps are checked HERE and not only on read. A builder bug that produced a file no
        /// reader would accept would otherwise ship a capture that silently never draws - and the
        /// write is where the numbers can still be blamed on the code that made them.
        /// </summary>
        /// <param name="file">The mesh to write.</param>
        /// <param name="stream">Where to write it. Left open.</param>
        internal static void Write(MapMeshFile file, Stream stream)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            file.Validate();
            file.Bind();

            // System.IO.Compression.CompressionLevel spelled out: UnityEngine has a CompressionLevel
            // of its own (for AssetBundles), and with both namespaces in scope the bare name is
            // ambiguous - the compiler says so, but only once this file exists, so it is worth a line.
            using (var deflate = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Optimal,
                       leaveOpen: true))
            using (var w = new BinaryWriter(deflate))
            {
                w.Write((byte)Magic[0]);
                w.Write((byte)Magic[1]);
                w.Write((byte)Magic[2]);
                w.Write((byte)Magic[3]);
                w.Write(Version);

                w.Write(file.MinX);
                w.Write(file.MinZ);
                w.Write(file.MaxX);
                w.Write(file.MaxZ);
                w.Write(file.YMin);
                w.Write(file.YMax);
                w.Write(file.AtlasPages);

                w.Write(file.Bands.Count);

                foreach (var band in file.Bands)
                {
                    w.Write(band.Level);
                    w.Write(band.CellMetres);
                    w.Write(band.Width);
                    w.Write(band.Height);
                    WriteUShorts(w, band.Heights);
                    w.Write(band.Distance, 0, band.Distance.Length);
                }

                w.Write(file.Buildings.Count);

                foreach (var building in file.Buildings)
                {
                    w.Write(building.Key);
                    w.Write(building.Level);
                    w.Write(building.VertexCount);
                    WriteUShorts(w, building.X);
                    WriteUShorts(w, building.Y);
                    WriteUShorts(w, building.Z);
                    w.Write(building.Indices.Length);
                    WriteUInts(w, building.Indices);

                    var uvs = building.U == null ? 0 : building.U.Length;
                    w.Write(uvs);

                    if (uvs > 0)
                    {
                        WriteUShorts(w, building.U);
                        WriteUShorts(w, building.V);
                    }

                    var ranges = building.Ranges?.Count ?? 0;
                    w.Write(ranges);

                    for (var k = 0; k < ranges; k++)
                    {
                        var range = building.Ranges[k];
                        w.Write(range.Page);
                        w.Write(range.First);
                        w.Write(range.Count);
                        w.Write(range.TileX);
                        w.Write(range.TileY);
                        w.Write(range.TileW);
                        w.Write(range.TileH);
                        w.Write(range.UMin);
                        w.Write(range.UMax);
                        w.Write(range.VMin);
                        w.Write(range.VMax);
                    }
                }
            }
        }

        /// <summary>The whole file as bytes, for a caller that stages them (MapCapture's
        /// <c>Stage</c>) or posts them.</summary>
        /// <param name="file">The mesh to write.</param>
        internal static byte[] ToBytes(MapMeshFile file)
        {
            using (var buffer = new MemoryStream())
            {
                Write(file, buffer);

                return buffer.ToArray();
            }
        }

        /// <summary>Every rule the format promises a reader, checked before a byte is written: the
        /// ranges are finite and ordered, the grids' arrays are the length their width and height say,
        /// the levels are distinct, the indices are in range, and nothing is over a cap. Throws
        /// <see cref="InvalidDataException"/> naming the first thing that is wrong - the same exception
        /// the reader throws, because "this file is not valid" is one condition whichever side finds
        /// it.</summary>
        private void Validate()
        {
            if (!IsFinite(MinX) || !IsFinite(MinZ) || !IsFinite(MaxX) || !IsFinite(MaxZ))
                throw new InvalidDataException("the mesh extent is not finite");

            if (!(MaxX > MinX) || !(MaxZ > MinZ))
                throw new InvalidDataException(
                    $"the mesh extent is empty or inverted: x {F(MinX)}..{F(MaxX)}, z {F(MinZ)}..{F(MaxZ)}");

            if (!IsFinite(YMin) || !IsFinite(YMax) || !(YMax > YMin))
                throw new InvalidDataException(
                    $"the mesh y range is empty or not finite: {F(YMin)}..{F(YMax)} - call SetYRange first");

            if (AtlasPages < 0 || AtlasPages > MaxAtlasPages)
                throw new InvalidDataException($"{AtlasPages} atlas pages is outside 0..{MaxAtlasPages}");

            if (Bands == null) throw new InvalidDataException("the mesh has no band list");
            if (Buildings == null) throw new InvalidDataException("the mesh has no building list");

            if (Bands.Count > MaxBands)
                throw new InvalidDataException($"{Bands.Count} bands is over the cap of {MaxBands}");

            for (var i = 0; i < Bands.Count; i++)
            {
                var band = Bands[i];

                if (band == null) throw new InvalidDataException($"band {i} is null");

                if (band.Width <= 0 || band.Height <= 0)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) is {band.Width}x{band.Height} cells");

                if (!IsFinite(band.CellMetres) || band.CellMetres <= 0f)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) has a cell size of {F(band.CellMetres)} m");

                var cells = (long)band.Width * band.Height;

                if (cells > MaxCellsPerBand)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) has {cells:#,##0} cells, over the cap of " +
                        $"{MaxCellsPerBand:#,##0}");

                if (band.Heights == null || band.Heights.Length != cells)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) has {band.Heights?.Length ?? -1} heights for " +
                        $"{band.Width}x{band.Height} cells");

                if (band.Distance == null || band.Distance.Length != cells)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) has {band.Distance?.Length ?? -1} distance " +
                        $"bytes for {band.Width}x{band.Height} cells");

                for (var j = 0; j < i; j++)
                    if (Bands[j].Level == band.Level)
                        throw new InvalidDataException(
                            $"bands {j} and {i} are both level {band.Level} - a level names a band");
            }

            if (Buildings.Count > MaxBuildings)
                throw new InvalidDataException(
                    $"{Buildings.Count:#,##0} buildings is over the cap of {MaxBuildings:#,##0}");

            var vertices = 0L;
            var triangles = 0L;

            for (var i = 0; i < Buildings.Count; i++)
            {
                var building = Buildings[i];

                if (building == null) throw new InvalidDataException($"building {i} is null");

                if (building.X == null || building.Y == null || building.Z == null)
                    throw new InvalidDataException($"building {i} (key {building.Key}) has no vertices");

                if (building.Y.Length != building.X.Length || building.Z.Length != building.X.Length)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) has {building.X.Length} x, " +
                        $"{building.Y.Length} y and {building.Z.Length} z");

                if (building.X.Length > MaxVerticesPerBuilding)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) has {building.X.Length:#,##0} vertices, over " +
                        $"the cap of {MaxVerticesPerBuilding:#,##0}");

                if (building.Indices == null)
                    throw new InvalidDataException($"building {i} (key {building.Key}) has no indices");

                if (building.Indices.Length % 3 != 0)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) has {building.Indices.Length} indices, not a " +
                        "multiple of 3");

                for (var j = 0; j < building.Indices.Length; j++)
                    if (building.Indices[j] >= (uint)building.X.Length)
                        throw new InvalidDataException(
                            $"building {i} (key {building.Key}) index {j} is {building.Indices[j]}, past its " +
                            $"{building.X.Length} vertices");

                // NoHit in a vertex's y is not a hole, it is a NaN: QuantiseHeight returns it for a
                // non-finite value, and a builder that quantised a NaN vertex position would put a NaN
                // into the viewer's vertex buffer, where Unity draws nothing and says nothing. The
                // builder's job is to DROP a renderer with a non-finite vertex; this is the check that
                // tells it to, while the numbers can still be blamed on the renderer they came from.
                for (var j = 0; j < building.Y.Length; j++)
                    if (building.Y[j] == NoHit)
                        throw new InvalidDataException(
                            $"building {i} (key {building.Key}) vertex {j} has no height (NoHit) - a " +
                            "non-finite vertex position; drop the building instead");

                if (Bands.Count > 0 && Band(building.Level) == null)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) is on level {building.Level}, which no band is");

                CheckAtlas(i, building.Key, building.X.Length, building.Indices,
                    building.U?.Length ?? 0, building.V?.Length ?? 0, building.Ranges, AtlasPages);

                vertices += building.X.Length;
                triangles += building.Indices.Length / 3;
            }

            if (vertices > MaxVerticesTotal)
                throw new InvalidDataException(
                    $"{vertices:#,##0} vertices in total is over the cap of {MaxVerticesTotal:#,##0}");

            if (triangles > MaxTriangles)
                throw new InvalidDataException(
                    $"{triangles:#,##0} triangles in total is over the cap of {MaxTriangles:#,##0}");
        }

        /// <summary>The atlas rules for one building, the writer's and the reader's alike: UVs absent or one
        /// per vertex, no range without them, and every range a positive whole number of triangles inside
        /// the building's indices, on a page the file has, ascending and not overlapping; its tile rect inside the
        /// page with sides that are multiples of <see cref="TileAlign"/> in TileAlign..AtlasTileMax; its bounds
        /// finite with max >= min; and no vertex used by two ranges (one pass over the indices, a byte a
        /// vertex) - a vertex's code is quantised over ONE range's bounds.</summary>
        /// <param name="i">The building's position, for the message.</param>
        /// <param name="key">Its key, for the message.</param>
        /// <param name="vertices">Its vertex count.</param>
        /// <param name="indexArray">Its indices.</param>
        /// <param name="us">Its u count.</param>
        /// <param name="vs">Its v count.</param>
        /// <param name="ranges">Its ranges.</param>
        /// <param name="pages">The file's page count.</param>
        private static void CheckAtlas(int i, int key, int vertices, uint[] indexArray, int us, int vs,
            List<AtlasRange> ranges, int pages)
        {
            var indices = indexArray?.Length ?? 0;

            if (us != vs || (us != 0 && us != vertices))
                throw new InvalidDataException(
                    $"building {i} (key {key}) has {us} u and {vs} v for {vertices} vertices");

            var count = ranges?.Count ?? 0;

            if (count > MaxRangesPerBuilding)
                throw new InvalidDataException(
                    $"building {i} (key {key}) has {count} atlas ranges; the cap is {MaxRangesPerBuilding}");

            if (count > 0 && us == 0)
                throw new InvalidDataException($"building {i} (key {key}) has atlas ranges and no UVs");

            var end = 0;

            for (var k = 0; k < count; k++)
            {
                var range = ranges[k];

                if (range.Page < 0 || range.Page >= pages)
                    throw new InvalidDataException(
                        $"building {i} (key {key}) range {k} is on page {range.Page}; the file has {pages}");

                if (range.First < end || range.First % 3 != 0 || range.Count <= 0 || range.Count % 3 != 0 ||
                    (long)range.First + range.Count > indices)
                    throw new InvalidDataException(
                        $"building {i} (key {key}) range {k} is indices {range.First}+{range.Count} of {indices} " +
                        $"(after {end}) - ranges are ascending whole triangles inside the building");

                if (range.TileW < TileAlign || range.TileH < TileAlign || range.TileW > AtlasTileMax ||
                    range.TileH > AtlasTileMax || range.TileW % TileAlign != 0 || range.TileH % TileAlign != 0 ||
                    range.TileX + range.TileW > AtlasPageSize || range.TileY + range.TileH > AtlasPageSize)
                    throw new InvalidDataException(
                        $"building {i} (key {key}) range {k}'s tile is {range.TileW}x{range.TileH} at " +
                        $"({range.TileX}, {range.TileY}) - a tile is {TileAlign}..{AtlasTileMax} px a side in steps of " +
                        $"{TileAlign}, inside its {AtlasPageSize} px page");

                if (!IsFinite(range.UMin) || !IsFinite(range.UMax) || !IsFinite(range.VMin) || !IsFinite(range.VMax) ||
                    range.UMax < range.UMin || range.VMax < range.VMin)
                    throw new InvalidDataException(
                        $"building {i} (key {key}) range {k}'s UV bounds are u {range.UMin}..{range.UMax}, " +
                        $"v {range.VMin}..{range.VMax} - they must be finite with max >= min");

                end = range.First + range.Count;
            }

            // one range per vertex: a byte a vertex, 0 = unused, else the range's number + 1
            if (count > 0 && indexArray != null)
            {
                var owner = new byte[vertices];

                for (var k = 0; k < count; k++)
                {
                    var tag = (byte)(k + 1);
                    var range = ranges[k];

                    for (var j = range.First; j < range.First + range.Count; j++)
                    {
                        var v = indexArray[j];
                        if (v >= (uint)vertices) continue;          // the index check names this one

                        if (owner[v] != 0 && owner[v] != tag)
                            throw new InvalidDataException(
                                $"building {i} (key {key}) vertex {v} is used by ranges {owner[v] - 1} and {k} - a " +
                                "vertex's UV code belongs to one range's bounds");

                        owner[v] = tag;
                    }
                }
            }
        }

        /// <summary>A quantised array's raw little-endian bytes. <see cref="Buffer.BlockCopy"/> on the
        /// machines this runs on (x86/x64 Mono) IS the little-endian encoding, so the fast path is a
        /// memcpy in 64 KiB chunks - no 8 MB temporary beside an 8 MB grid. The big-endian path exists
        /// so the format stays little-endian by definition rather than by accident; it is never taken
        /// by the game.</summary>
        /// <param name="w">The writer.</param>
        /// <param name="values">The values to write.</param>
        private static void WriteUShorts(BinaryWriter w, ushort[] values)
        {
            if (!BitConverter.IsLittleEndian)
            {
                foreach (var value in values) w.Write(value);

                return;
            }

            WriteRaw(w, values, sizeof(ushort), values.Length);
        }

        /// <summary>The index array's raw little-endian bytes. See <see cref="WriteUShorts"/>.</summary>
        /// <param name="w">The writer.</param>
        /// <param name="values">The values to write.</param>
        private static void WriteUInts(BinaryWriter w, uint[] values)
        {
            if (!BitConverter.IsLittleEndian)
            {
                foreach (var value in values) w.Write(value);

                return;
            }

            WriteRaw(w, values, sizeof(uint), values.Length);
        }

        /// <summary>A blittable array straight down the stream in chunks, on a little-endian
        /// machine.</summary>
        /// <param name="w">The writer.</param>
        /// <param name="values">The array.</param>
        /// <param name="elementBytes">Bytes per element; 64 KiB is a multiple of both sizes used, so a
        /// chunk never splits one.</param>
        /// <param name="count">Elements to write.</param>
        private static void WriteRaw(BinaryWriter w, Array values, int elementBytes, int count)
        {
            var total = (long)count * elementBytes;
            if (total <= 0) return;

            var chunk = new byte[(int)Math.Min(total, ChunkBytes)];
            var done = 0L;

            while (done < total)
            {
                var want = (int)Math.Min(chunk.Length, total - done);
                Buffer.BlockCopy(values, (int)done, chunk, 0, want);
                w.Write(chunk, 0, want);
                done += want;
            }
        }

        // --- reading -------------------------------------------------------------------------------------

        /// <summary>A mesh file from bytes - the download's body, or a file read in one go.</summary>
        /// <param name="deflated">The file's bytes, as <see cref="Write"/> produced them.</param>
        internal static MapMeshFile Read(byte[] deflated)
        {
            if (deflated == null || deflated.Length == 0)
                throw new InvalidDataException("the mesh file is empty");

            using (var buffer = new MemoryStream(deflated, writable: false))
            {
                return Read(buffer);
            }
        }

        /// <summary>
        /// Reads a mesh file, checking everything before it trusts it. The inverse of
        /// <see cref="Write"/>, and the ONLY reader: see that method's doc comment for the layout.
        ///
        /// What "checking before trusting" means here, since this is the half that faces a hostile or
        /// truncated file:
        ///   - the magic and the version first, and a version that is not <see cref="Version"/> is
        ///     refused with the number in the message rather than read as version 1;
        ///   - every count is tested against its cap BEFORE the array it sizes is allocated, so a
        ///     header claiming a billion cells throws in one line rather than asking for 2 GB;
        ///   - a negative count is a corrupt count, not a clever one;
        ///   - every array is filled by a read that insists on the exact byte count, so a file that
        ///     ends inside a grid throws instead of leaving half a grid of zeros - which would look
        ///     like flat ground;
        ///   - every index is checked against its building's vertex count, so the viewer's
        ///     <c>SetTriangles</c> cannot be handed one that would throw from inside Unity;
        ///   - and the whole of it runs inside one catch that turns a short read or a broken deflate
        ///     block into an <see cref="InvalidDataException"/> too, so a caller has exactly one
        ///     exception type to handle.
        /// </summary>
        /// <param name="stream">The file's bytes. Left open.</param>
        internal static MapMeshFile Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            try
            {
                using (var deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true))
                using (var r = new BinaryReader(deflate))
                {
                    return ReadBody(r, deflate);
                }
            }
            catch (EndOfStreamException ex)
            {
                // BinaryReader's own end-of-stream, from a file truncated inside a scalar. Same
                // condition as a short array read, so it gets the same exception type.
                throw new InvalidDataException("the mesh file ends inside its header or a count", ex);
            }
            catch (IOException ex)
            {
                // Mono's DeflateStream reports a corrupt or truncated deflate block as an IOException
                // (EndOfStreamException above derives from it, so this arm must come second). The
                // class's own refusals are InvalidDataException, which is not an IOException, so they
                // pass through untouched - and the doc's promise that every broken file becomes one
                // holds for the deflate layer too.
                throw new InvalidDataException("the mesh file's deflate stream is broken or truncated", ex);
            }
        }

        /// <summary>The body of <see cref="Read(Stream)"/>, once the deflate bracket is open.</summary>
        /// <param name="r">Reads the scalars. Never reads ahead, so the raw arrays below can come
        /// straight off the same stream.</param>
        /// <param name="raw">The inflated stream, for the quantised arrays.</param>
        private static MapMeshFile ReadBody(BinaryReader r, Stream raw)
        {
            var magic = r.ReadBytes(4);

            if (magic.Length != 4 ||
                magic[0] != (byte)Magic[0] || magic[1] != (byte)Magic[1] ||
                magic[2] != (byte)Magic[2] || magic[3] != (byte)Magic[3])
                throw new InvalidDataException(
                    $"this is not a QuestTree mesh file: it starts {Printable(magic)}, not \"{Magic}\"");

            var version = r.ReadInt32();

            if (version != Version)
                throw new InvalidDataException(
                    $"this mesh file is version {version}; this build reads version {Version}");

            var file = new MapMeshFile
            {
                MinX = r.ReadDouble(),
                MinZ = r.ReadDouble(),
                MaxX = r.ReadDouble(),
                MaxZ = r.ReadDouble(),
                YMin = r.ReadSingle(),
                YMax = r.ReadSingle(),
                AtlasPages = r.ReadInt32()
            };

            if (file.AtlasPages < 0 || file.AtlasPages > MaxAtlasPages)
                throw new InvalidDataException(
                    $"the mesh file claims {file.AtlasPages} atlas pages; the cap is {MaxAtlasPages}");

            if (!IsFinite(file.MinX) || !IsFinite(file.MinZ) || !IsFinite(file.MaxX) || !IsFinite(file.MaxZ))
                throw new InvalidDataException("the mesh file's extent is not finite");

            if (!(file.MaxX > file.MinX) || !(file.MaxZ > file.MinZ))
                throw new InvalidDataException(
                    $"the mesh file's extent is empty or inverted: x {F(file.MinX)}..{F(file.MaxX)}, " +
                    $"z {F(file.MinZ)}..{F(file.MaxZ)}");

            if (!IsFinite(file.YMin) || !IsFinite(file.YMax) || !(file.YMax > file.YMin))
                throw new InvalidDataException(
                    $"the mesh file's y range is empty or not finite: {F(file.YMin)}..{F(file.YMax)}");

            var bandCount = r.ReadInt32();

            if (bandCount < 0 || bandCount > MaxBands)
                throw new InvalidDataException(
                    $"the mesh file claims {bandCount:#,##0} bands; the cap is {MaxBands}");

            for (var i = 0; i < bandCount; i++)
            {
                var band = new ReliefBand
                {
                    Level = r.ReadInt32(),
                    CellMetres = r.ReadSingle(),
                    Width = r.ReadInt32(),
                    Height = r.ReadInt32()
                };

                if (band.Width <= 0 || band.Height <= 0)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) claims {band.Width}x{band.Height} cells");

                if (!IsFinite(band.CellMetres) || band.CellMetres <= 0f)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) claims a cell size of {F(band.CellMetres)} m");

                var cells = (long)band.Width * band.Height;

                // Before the allocation, not after: this is the line that stands between a corrupt
                // width and a 2 GB request.
                if (cells > MaxCellsPerBand)
                    throw new InvalidDataException(
                        $"band {i} (level {band.Level}) claims {band.Width}x{band.Height} = {cells:#,##0} " +
                        $"cells; the cap is {MaxCellsPerBand:#,##0}");

                for (var j = 0; j < i; j++)
                    if (file.Bands[j].Level == band.Level)
                        throw new InvalidDataException(
                            $"the mesh file has two level {band.Level} bands ({j} and {i})");

                band.Heights = ReadUShorts(raw, (int)cells, $"band {i} (level {band.Level}) heights");
                band.Distance = new byte[cells];
                ReadExactly(raw, band.Distance, (int)cells, $"band {i} (level {band.Level}) distances");

                file.Bands.Add(band);
            }

            var buildingCount = r.ReadInt32();

            if (buildingCount < 0 || buildingCount > MaxBuildings)
                throw new InvalidDataException(
                    $"the mesh file claims {buildingCount:#,##0} buildings; the cap is {MaxBuildings:#,##0}");

            var verticesSoFar = 0L;
            var trianglesSoFar = 0L;

            for (var i = 0; i < buildingCount; i++)
            {
                var building = new Building
                {
                    Key = r.ReadInt32(),
                    Level = r.ReadInt32()
                };

                var vertexCount = r.ReadInt32();

                if (vertexCount < 0 || vertexCount > MaxVerticesPerBuilding)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) claims {vertexCount:#,##0} vertices; the cap is " +
                        $"{MaxVerticesPerBuilding:#,##0}");

                verticesSoFar += vertexCount;

                // The running total, checked as it grows: the per-building cap alone lets 20,000
                // buildings of 2 M vertices through, which is 240 GB of allocation.
                if (verticesSoFar > MaxVerticesTotal)
                    throw new InvalidDataException(
                        $"the mesh file's buildings claim {verticesSoFar:#,##0} vertices by building {i}; the " +
                        $"cap is {MaxVerticesTotal:#,##0}");

                building.X = ReadUShorts(raw, vertexCount, $"building {i} (key {building.Key}) x");
                building.Y = ReadUShorts(raw, vertexCount, $"building {i} (key {building.Key}) y");
                building.Z = ReadUShorts(raw, vertexCount, $"building {i} (key {building.Key}) z");

                var indexCount = r.ReadInt32();

                if (indexCount < 0 || indexCount % 3 != 0)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) claims {indexCount:#,##0} indices, which is not a " +
                        "non-negative multiple of 3");

                trianglesSoFar += indexCount / 3;

                if (trianglesSoFar > MaxTriangles)
                    throw new InvalidDataException(
                        $"the mesh file claims {trianglesSoFar:#,##0} triangles by building {i}; the cap is " +
                        $"{MaxTriangles:#,##0}");

                building.Indices = ReadUInts(raw, indexCount, $"building {i} (key {building.Key}) indices");

                for (var j = 0; j < building.Indices.Length; j++)
                    if (building.Indices[j] >= (uint)vertexCount)
                        throw new InvalidDataException(
                            $"building {i} (key {building.Key}) index {j} is {building.Indices[j]}, past its " +
                            $"{vertexCount:#,##0} vertices");

                var uvCount = r.ReadInt32();

                if (uvCount != 0 && uvCount != vertexCount)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) claims {uvCount:#,##0} UVs for {vertexCount:#,##0} vertices");

                if (uvCount > 0)
                {
                    building.U = ReadUShorts(raw, uvCount, $"building {i} (key {building.Key}) u");
                    building.V = ReadUShorts(raw, uvCount, $"building {i} (key {building.Key}) v");
                }

                var rangeCount = r.ReadInt32();

                if (rangeCount < 0 || rangeCount > MaxRangesPerBuilding)
                    throw new InvalidDataException(
                        $"building {i} (key {building.Key}) claims {rangeCount} atlas ranges; the cap is " +
                        $"{MaxRangesPerBuilding}");

                for (var k = 0; k < rangeCount; k++)
                    building.Ranges.Add(new AtlasRange
                    {
                        Page = r.ReadInt32(),
                        First = r.ReadInt32(),
                        Count = r.ReadInt32(),
                        TileX = r.ReadUInt16(),
                        TileY = r.ReadUInt16(),
                        TileW = r.ReadUInt16(),
                        TileH = r.ReadUInt16(),
                        UMin = r.ReadSingle(),
                        UMax = r.ReadSingle(),
                        VMin = r.ReadSingle(),
                        VMax = r.ReadSingle(),
                    });

                CheckAtlas(i, building.Key, vertexCount, building.Indices, uvCount, uvCount, building.Ranges,
                    file.AtlasPages);

                // See Validate: NoHit in a vertex's y is a NaN, and a NaN in a vertex buffer draws
                // nothing without a word of complaint.
                for (var j = 0; j < building.Y.Length; j++)
                    if (building.Y[j] == NoHit)
                        throw new InvalidDataException(
                            $"building {i} (key {building.Key}) vertex {j} has no height (NoHit), which " +
                            "would be a NaN vertex");

                file.Buildings.Add(building);
            }

            // Nothing may follow the last building. A file with a tail is a file whose length disagrees
            // with its contents, which means either a writer that changed or bytes that were corrupted
            // - and the second one is exactly the case that otherwise reads as valid geometry, because
            // raw deflate carries no checksum for a broken bitstream to fail.
            if (raw.ReadByte() >= 0)
                throw new InvalidDataException(
                    "the mesh file carries bytes after its last building - it is corrupt, or was " +
                    "written by something else");

            file.Bind();

            return file;
        }

        /// <summary>A quantised ushort array off the inflated stream. Allocates only after the caller
        /// has checked the count against its cap.</summary>
        /// <param name="stream">The inflated stream.</param>
        /// <param name="count">Elements to read.</param>
        /// <param name="what">What this array is, for the exception.</param>
        private static ushort[] ReadUShorts(Stream stream, int count, string what)
        {
            var values = new ushort[count];
            if (count == 0) return values;

            if (!BitConverter.IsLittleEndian)
            {
                var pair = new byte[2];

                for (var i = 0; i < count; i++)
                {
                    ReadExactly(stream, pair, 2, what);
                    values[i] = (ushort)(pair[0] | (pair[1] << 8));
                }

                return values;
            }

            ReadRaw(stream, values, sizeof(ushort), count, what);

            return values;
        }

        /// <summary>An index array off the inflated stream. See <see cref="ReadUShorts"/>.</summary>
        /// <param name="stream">The inflated stream.</param>
        /// <param name="count">Elements to read.</param>
        /// <param name="what">What this array is, for the exception.</param>
        private static uint[] ReadUInts(Stream stream, int count, string what)
        {
            var values = new uint[count];
            if (count == 0) return values;

            if (!BitConverter.IsLittleEndian)
            {
                var quad = new byte[4];

                for (var i = 0; i < count; i++)
                {
                    ReadExactly(stream, quad, 4, what);
                    values[i] = (uint)(quad[0] | (quad[1] << 8) | (quad[2] << 16) | (quad[3] << 24));
                }

                return values;
            }

            ReadRaw(stream, values, sizeof(uint), count, what);

            return values;
        }

        /// <summary>A blittable array filled from the stream in 64 KiB chunks, on a little-endian
        /// machine.</summary>
        /// <param name="stream">The inflated stream.</param>
        /// <param name="values">The array to fill.</param>
        /// <param name="elementBytes">Bytes per element.</param>
        /// <param name="count">Elements to read.</param>
        /// <param name="what">What this array is, for the exception.</param>
        private static void ReadRaw(Stream stream, Array values, int elementBytes, int count, string what)
        {
            var total = (long)count * elementBytes;
            var chunk = new byte[(int)Math.Min(total, ChunkBytes)];
            var done = 0L;

            while (done < total)
            {
                var want = (int)Math.Min(chunk.Length, total - done);

                // done and total, not want: a message about the 64 KiB chunk the read happened to fail
                // in says nothing about how far into the array the file ran out.
                ReadExactly(stream, chunk, want, what, done, total);
                Buffer.BlockCopy(chunk, 0, values, (int)done, want);
                done += want;
            }
        }

        /// <summary>Exactly this many bytes, or an <see cref="InvalidDataException"/> saying where the
        /// file ran out. A <see cref="Stream.Read(byte[],int,int)"/> is allowed to return fewer bytes
        /// than asked for, and a <see cref="DeflateStream"/> regularly does - so every array read in
        /// this class goes through here, and a truncated file can only end in a named
        /// failure.</summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="buffer">Where to put the bytes.</param>
        /// <param name="count">How many to read.</param>
        /// <param name="what">What is being read, for the exception.</param>
        /// <param name="alreadyDone">Bytes of <paramref name="what"/> read before this call, when this
        /// is one chunk of a larger array. For the message only.</param>
        /// <param name="totalWanted">Bytes <paramref name="what"/> needs in total, when this is one
        /// chunk of a larger array; zero means <paramref name="count"/> is the whole of it. For the
        /// message only.</param>
        private static void ReadExactly(Stream stream, byte[] buffer, int count, string what,
            long alreadyDone = 0, long totalWanted = 0)
        {
            var done = 0;

            while (done < count)
            {
                var read = stream.Read(buffer, done, count - done);

                if (read <= 0)
                    throw new InvalidDataException(
                        $"the mesh file ends inside {what}: {alreadyDone + done:#,##0} of " +
                        $"{(totalWanted > 0 ? totalWanted : count):#,##0} bytes");

                done += read;
            }
        }

        // --- small helpers ---------------------------------------------------------------------------------

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static string F(double v) =>
            IsFinite(v) ? v.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

        /// <summary>The magic that was actually there, for the exception: readable characters as
        /// themselves and everything else as hex, so a log line tells a text file from a PNG from a
        /// truncated mesh.</summary>
        /// <param name="bytes">The bytes read where the magic should have been.</param>
        private static string Printable(byte[] bytes)
        {
            var text = new System.Text.StringBuilder("\"");

            foreach (var b in bytes)
                text.Append(b >= 0x20 && b < 0x7F
                    ? ((char)b).ToString()
                    : "\\x" + b.ToString("x2", CultureInfo.InvariantCulture));

            return text.Append('"').ToString();
        }
    }
}
