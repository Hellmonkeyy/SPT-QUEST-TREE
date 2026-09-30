using System;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// Play-test 2026-09-29 (the Customs smokestacks, the Scav Base hall): a morphological TOP-HAT over a relief band. The
    /// relief is cast from above, and where a column has no terrain hit (a mesh floor, an interior map) the highest collider
    /// is taken as the ground - a stack's cap at 60-69 m, a roof panel at 17-20 m in a hall with a 1 m floor. The ground
    /// picture is then draped over a pillar of cells that is not ground: fat bluish cylinders round the stacks, white spikes
    /// in the hall. Nothing that stands up out of the ground by more than <see cref="DespikeRiseMetres"/> over less than
    /// <see cref="DespikeWindowMetres"/> of width is ground, whatever its layer, so the cells that do are lowered to the
    /// grey-scale OPENING of the band (erode, then dilate, over a square window): the highest surface a
    /// window-sized square can be pushed up under. A hill, a ridge or a slope is wider than the window and passes through
    /// the opening unchanged; a pillar narrower than it is cut to the ground around it.
    ///
    /// On bigmap's stored band this moves 7.5 k of 2.4 M cells, every one at a man-made object (the stacks, the boiler
    /// house, the Snipe Tower block, the hall, the pylons) - measured by the read-only investigation, and again by the
    /// harness on this code. A cell whose column met the terrain is never moved; with <see cref="DespikeRequiresCover"/>
    /// (off) a cell no stored mesh reaches the ground over is kept too.
    ///
    /// Pure array code: no Unity type, no allocation per cell or per line (two grid-sized scratch arrays and one index
    /// line a call), so it runs on a worker thread and in a console harness as it is. A cell is only ever moved DOWN, a
    /// hole (NaN, an infinity, or <see cref="MapMeshFile.NoHit"/>) is neither moved nor counted as ground, and a protected
    /// cell is never moved. Idempotent: a band despiked once has no residual over the rise, so a second pass moves nothing.
    /// </summary>
    internal static class ReliefDespike
    {
        /// <summary>The opening's square window, in metres on a side: wider than a smokestack or a pylon (4-8 m) and than
        /// the roof panels of a hall, narrower than any hill or embankment a map is built on. Turned into cells by
        /// <see cref="RadiusCells"/>.</summary>
        internal const float DespikeWindowMetres = 10f;

        /// <summary>How far a cell must stand over its opening to be lowered to it. Six metres is two storeys: a kerb, a
        /// wall, a car, a container stays (they are the ground's business elsewhere - TerrainAnchoredGround and the
        /// under-buildings pass), a stack, a tower or a roof panel over a low floor goes.</summary>
        internal const float DespikeRiseMetres = 6f;

        /// <summary>
        /// Rollback switch (play-test 2026-09-29, the Snipe Tower block on Customs): true protects every cell no stored mesh
        /// reaches the ground over (<see cref="CoverFootprint"/>), so a structure with colliders and no stored mesh keeps its
        /// draped column. That left the Snipe Tower block as two picture-draped cylinders with grassy tops (382 cells on
        /// bigmap); a flattened spot reads better than a column of stretched ground picture, so false flattens every spike
        /// and only a cell whose column met the terrain is protected (<see cref="ProtectMask"/>). Static readonly, not
        /// const, so the branch on it compiles either way without an unreachable-code warning.
        /// </summary>
        internal static readonly bool DespikeRequiresCover = false;

        /// <summary>
        /// The despike's protected cells: every cell whose ground bits hold <paramref name="terrainBit"/> (its column met the
        /// terrain, which is the ground by definition), and, when <paramref name="requireCover"/>, every cell the
        /// <paramref name="cover"/> mask gives no <see cref="CoverFootprint"/>. Null when nothing is protected (no ground
        /// bits and no cover asked - the viewer on a stored file), which the despike reads as "move any spike".
        /// </summary>
        /// <param name="cells">The band's cell count.</param>
        /// <param name="cover">The cover mask, or null (then every cell counts as uncovered).</param>
        /// <param name="groundBits">Per-cell ground bits (MapMeshBuilder's BandWork.Ground), or null when unknown.</param>
        /// <param name="terrainBit">The bit of <paramref name="groundBits"/> that says the column met the terrain.</param>
        /// <param name="requireCover">Protect the cells whose <paramref name="cover"/> lacks <paramref name="coverBit"/>
        /// (<see cref="DespikeRequiresCover"/> in the builder; always in the viewer, which has no ground bits).</param>
        /// <param name="coverBit">The cover bit a cell needs to be movable: <see cref="CoverFootprint"/> (a mesh reaches
        /// the ground) or <see cref="CoverAnyHeight"/> (a mesh lies over it at all).</param>
        internal static bool[] ProtectMask(int cells, byte[] cover, byte[] groundBits, byte terrainBit, bool requireCover,
            byte coverBit = CoverFootprint)
        {
            if (cells < 1 || (!requireCover && groundBits == null)) return null;

            var protect = new bool[cells];

            for (var n = 0; n < cells; n++)
                protect[n] = (groundBits != null && n < groundBits.Length && (groundBits[n] & terrainBit) != 0) ||
                             (requireCover && (cover == null || n >= cover.Length || (cover[n] & coverBit) == 0));

            return protect;
        }

        /// <summary>
        /// The top-hat over a band in METRES: every cell more than <see cref="DespikeRiseMetres"/> over the band's opening
        /// with a <see cref="DespikeWindowMetres"/> window takes the opening's height.
        /// </summary>
        /// <param name="heights">Row-major <paramref name="w"/> x <paramref name="h"/> heights, NaN (or an infinity) where
        /// no ray hit. Written in place.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="cellMetres">A cell's side in metres.</param>
        /// <param name="protect">Cells never to move (a terrain hit is the ground by definition), or null.</param>
        /// <returns>The cells moved.</returns>
        internal static int Despike(float[] heights, int w, int h, float cellMetres, bool[] protect = null) =>
            Despike(heights, w, h, cellMetres, DespikeWindowMetres, DespikeRiseMetres, protect);

        /// <summary>
        /// <see cref="Despike(float[], int, int, float, bool[])"/> with the window and the rise given - for the harness,
        /// whose check that can fail runs this with a rise nothing reaches.
        /// </summary>
        internal static int Despike(float[] heights, int w, int h, float cellMetres, float windowMetres, float riseMetres,
            bool[] protect = null)
        {
            if (!Valid(heights?.Length ?? 0, w, h, cellMetres, windowMetres, riseMetres, protect)) return 0;

            var n = w * h;
            var a = new float[n];
            Array.Copy(heights, a, n);

            var opened = Open(a, w, h, RadiusCells(windowMetres, cellMetres));
            var moved = 0;

            for (var i = 0; i < n; i++)
            {
                var y = heights[i];
                var o = opened[i];

                // a hole stays a hole; a NaN opening (nothing measured anywhere near) knows no ground to lower to
                if (!IsFinite(y) || !IsFinite(o) || (protect != null && protect[i])) continue;
                if (!(y - o > riseMetres)) continue;

                heights[i] = o;   // the opening is anti-extensive, so this is always down
                moved++;
            }

            return moved;
        }

        /// <summary>
        /// The top-hat over a band as the FILE stores it: sixteen-bit codes over the file's y range,
        /// <see cref="MapMeshFile.NoHit"/> where no ray hit. Works on the codes themselves (a code is an exact float), so a
        /// moved cell takes the code of a real neighbour exactly and no height is requantised.
        /// </summary>
        /// <param name="codes">A band's <c>Heights</c>. Written in place.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="cellMetres">A cell's side in metres.</param>
        /// <param name="yMin">The file's YMin - what code 0 is.</param>
        /// <param name="yMax">The file's YMax - what <see cref="MapMeshFile.MaxQuantised"/> is.</param>
        /// <param name="protect">Cells never to move, or null.</param>
        /// <returns>The cells moved.</returns>
        internal static int Despike(ushort[] codes, int w, int h, float cellMetres, float yMin, float yMax, bool[] protect = null) =>
            Despike(codes, w, h, cellMetres, yMin, yMax, DespikeWindowMetres, DespikeRiseMetres, protect);

        /// <summary>
        /// <see cref="Despike(ushort[], int, int, float, float, float, bool[])"/> with the window and the rise given.
        /// </summary>
        internal static int Despike(ushort[] codes, int w, int h, float cellMetres, float yMin, float yMax, float windowMetres,
            float riseMetres, bool[] protect = null)
        {
            if (!Valid(codes?.Length ?? 0, w, h, cellMetres, windowMetres, riseMetres, protect)) return 0;

            return DespikeCodes(codes, OpenedCodes(codes, w, h, cellMetres, windowMetres), w, h, yMin, yMax, riseMetres, protect);
        }

        /// <summary>
        /// The band's grey-scale opening (<see cref="Open"/>) in its own codes, <see cref="MapMeshFile.NoHit"/> where the
        /// window held nothing measured - the ground a window-sized square rests on. Taken once and shared: the despike
        /// compares against it, and the cover mask (MapMeshBuilder.CoverMask) judges an underground mesh against it.
        /// </summary>
        /// <param name="codes">A band's <c>Heights</c>; not written.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="cellMetres">A cell's side in metres.</param>
        /// <param name="windowMetres">The window's side.</param>
        internal static ushort[] OpenedCodes(ushort[] codes, int w, int h, float cellMetres, float windowMetres = DespikeWindowMetres)
        {
            if (!Valid(codes?.Length ?? 0, w, h, cellMetres, windowMetres, 0f, null)) return null;

            var n = w * h;
            var a = new float[n];
            for (var i = 0; i < n; i++) a[i] = codes[i] == MapMeshFile.NoHit ? float.NaN : codes[i];

            var opened = Open(a, w, h, RadiusCells(windowMetres, cellMetres));
            var result = new ushort[n];

            // each value is one of the band's own codes: min and max pick, they never blend
            for (var i = 0; i < n; i++) result[i] = IsFinite(opened[i]) && opened[i] >= 0f ? (ushort)opened[i] : MapMeshFile.NoHit;

            return result;
        }

        /// <summary>
        /// The top-hat's move against an opening already taken (<see cref="OpenedCodes"/>): a cell more than
        /// <paramref name="riseMetres"/> over its opening takes the opening's code; holes, cells with no opening and
        /// protected cells stay.
        /// </summary>
        /// <param name="codes">A band's <c>Heights</c>. Written in place.</param>
        /// <param name="opened">Its opening, from <see cref="OpenedCodes"/> on these codes.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="yMin">The file's YMin.</param>
        /// <param name="yMax">The file's YMax.</param>
        /// <param name="riseMetres">The rise.</param>
        /// <param name="protect">Cells never to move, or null.</param>
        /// <returns>The cells moved.</returns>
        internal static int DespikeCodes(ushort[] codes, ushort[] opened, int w, int h, float yMin, float yMax,
            float riseMetres = DespikeRiseMetres, bool[] protect = null)
        {
            var n = w * h;
            if (codes == null || opened == null || w < 1 || h < 1 || codes.Length < n || opened.Length < n) return 0;
            if (float.IsNaN(riseMetres) || riseMetres < 0f) return 0;
            if (protect != null && protect.Length < n)
                throw new ArgumentException($"protect holds {protect.Length} cells for a {w} x {h} grid", nameof(protect));

            var span = (double)yMax - yMin;
            if (!(span > 0d) || double.IsInfinity(span)) return 0;

            // the rise in codes, the same linear map MapMeshFile.Quantise uses
            var riseCodes = riseMetres * MapMeshFile.MaxQuantised / span;
            var moved = 0;

            for (var i = 0; i < n; i++)
            {
                var c = codes[i];
                var o = opened[i];

                if (c == MapMeshFile.NoHit || o == MapMeshFile.NoHit || (protect != null && protect[i])) continue;
                if (!(c - o > riseCodes)) continue;

                codes[i] = o;
                moved++;
            }

            return moved;
        }

        /// <summary>The window's half-width in cells, at least one: the window is a square of <c>2r + 1</c> cells, the
        /// odd count nearest the window (10 m on a 0.5 m grid is 21 cells, 10.5 m).</summary>
        /// <param name="windowMetres">The window's side.</param>
        /// <param name="cellMetres">A cell's side.</param>
        internal static int RadiusCells(float windowMetres, float cellMetres) =>
            Math.Max(1, (int)Math.Round(windowMetres / cellMetres / 2d, MidpointRounding.AwayFromZero));

        /// <summary>
        /// The grey-scale opening of <paramref name="a"/> with a square of <c>2r + 1</c> cells, separable: min along the
        /// rows, min along the columns (the erosion), then max along the rows and the columns (the dilation), each a
        /// sliding window over one line (<see cref="Slide"/>) - O(w x h) whatever the window, against O(w x h x r^2) for
        /// the direct square. Ping-pongs between <paramref name="a"/> and one scratch array; the answer is left in
        /// <paramref name="a"/> and returned. A non-finite value is a hole: it never wins a min or a max, and a window
        /// with nothing but holes answers NaN.
        /// </summary>
        private static float[] Open(float[] a, int w, int h, int r)
        {
            var b = new float[a.Length];
            var line = new int[Math.Max(w, h)];

            for (var row = 0; row < h; row++) Slide(a, b, row * w, 1, w, r, false, line);
            for (var col = 0; col < w; col++) Slide(b, a, col, w, h, r, false, line);
            for (var row = 0; row < h; row++) Slide(a, b, row * w, 1, w, r, true, line);
            for (var col = 0; col < w; col++) Slide(b, a, col, w, h, r, true, line);

            return a;
        }

        /// <summary>
        /// One line's sliding min (or max) over <c>[i - r, i + r]</c>, clipped at the line's ends, by a monotonic deque of
        /// positions: each position is pushed once and popped at most once, so a line costs O(count) whatever r is (the
        /// same bound as van Herk/Gil-Werman, with no padding and no block arrays). Holes are never pushed.
        /// </summary>
        /// <param name="src">The values in.</param>
        /// <param name="dst">The values out - never <paramref name="src"/>.</param>
        /// <param name="start">The line's first element.</param>
        /// <param name="stride">Between the line's elements: 1 for a row, the width for a column.</param>
        /// <param name="count">The line's length.</param>
        /// <param name="r">The window's half-width.</param>
        /// <param name="max">Max (dilation) rather than min (erosion).</param>
        /// <param name="dq">The deque's storage, at least <paramref name="count"/> long.</param>
        private static void Slide(float[] src, float[] dst, int start, int stride, int count, int r, bool max, int[] dq)
        {
            var head = 0;
            var tail = 0;
            var next = 0;

            for (var i = 0; i < count; i++)
            {
                var reach = Math.Min(count - 1, i + r);

                for (; next <= reach; next++)
                {
                    var v = src[start + next * stride];
                    if (!IsFinite(v)) continue;

                    // drop what v beats: it is newer, so it outlives them in every later window
                    if (max)
                        while (tail > head && src[start + dq[tail - 1] * stride] <= v) tail--;
                    else
                        while (tail > head && src[start + dq[tail - 1] * stride] >= v) tail--;

                    dq[tail++] = next;
                }

                while (tail > head && dq[head] < i - r) head++;

                dst[start + i * stride] = tail > head ? src[start + dq[head] * stride] : float.NaN;
            }
        }

        /// <summary>A cover-mask bit: a building's triangle lies over the cell (<see cref="CoverTriangles"/>).</summary>
        internal const byte CoverBuilding = 1;

        /// <summary>A cover-mask bit: a prop's triangle lies over the cell.</summary>
        internal const byte CoverProp = 2;

        /// <summary>A cover-mask bit: a building's or a prop's triangle lies over the cell's centre and reaches the GROUND
        /// there (the band's opening, less the slack) - what the despike asks (review 2026-09-29): a pillar of collider
        /// standing over a lower stored mesh (a stack's cap over its decimated shell) is exactly what pokes through the
        /// drawn mesh, while the at-the-hit rule of <see cref="CoverBuilding"/> calls it uncovered (5,721 of bigmap's
        /// pillar cells against 7,301); a basement or a tunnel under the ground is drawn over nothing and does not count.</summary>
        internal const byte CoverFootprint = 4;

        /// <summary>A cover-mask bit: a building's or a prop's triangle lies over the cell's centre AT ANY HEIGHT (review
        /// 2026-09-29): the viewer's guard, which has no terrain bits - a cell no stored mesh lies over at all (a narrow
        /// terrain spur) is never despiked on load. Widened by <see cref="CoverReachMetres"/> (<see cref="DilateBit"/>)
        /// before it is read. No other pass reads it.</summary>
        internal const byte CoverAnyHeight = 8;

        /// <summary>How far, horizontally, the viewer's <see cref="CoverAnyHeight"/> guard reaches past a stored triangle
        /// (review 2026-09-29): the Snipe Tower's collider top is 3-5.5 m wider than its stored shafts, so a spike within
        /// this of any stored mesh is movable on load; rock farther from every stored mesh stays protected.</summary>
        internal const float CoverReachMetres = 6f;

        /// <summary>
        /// <paramref name="bit"/> of <paramref name="mask"/> dilated in place by a square of <c>2r + 1</c> cells: a cell
        /// takes the bit when any cell within <paramref name="r"/> cells on both axes has it. Separable (rows, then
        /// columns), each line a running count over the window, so O(w x h) whatever r is; other bits are untouched.
        /// </summary>
        /// <param name="mask">Row-major <paramref name="w"/> x <paramref name="h"/> bits, written in place.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="bit">The bit to dilate.</param>
        /// <param name="r">The half-width in cells; 0 or less leaves the mask as it is.</param>
        /// <returns>The cells that hold the bit afterwards.</returns>
        internal static int DilateBit(byte[] mask, int w, int h, byte bit, int r)
        {
            if (mask == null || w < 1 || h < 1 || mask.Length < w * h) return 0;

            var n = w * h;
            var set = 0;

            if (r > 0)
            {
                var rows = new bool[n];

                for (var row = 0; row < h; row++)
                {
                    var start = row * w;
                    var count = 0;

                    // the window [col - r, col + r]: primed with [0, r - 1], each step adds col + r and drops col - r - 1
                    for (var c = 0; c < Math.Min(r, w); c++) if ((mask[start + c] & bit) != 0) count++;

                    for (var col = 0; col < w; col++)
                    {
                        var add = col + r;
                        if (add < w && (mask[start + add] & bit) != 0) count++;
                        var drop = col - r - 1;
                        if (drop >= 0 && (mask[start + drop] & bit) != 0) count--;
                        rows[start + col] = count > 0;
                    }
                }

                for (var col = 0; col < w; col++)
                {
                    var count = 0;
                    for (var rr = 0; rr < Math.Min(r, h); rr++) if (rows[rr * w + col]) count++;

                    for (var row = 0; row < h; row++)
                    {
                        var add = row + r;
                        if (add < h && rows[add * w + col]) count++;
                        var drop = row - r - 1;
                        if (drop >= 0 && rows[drop * w + col]) count--;
                        if (count > 0) mask[row * w + col] |= bit;
                    }
                }
            }

            for (var i = 0; i < n; i++) if ((mask[i] & bit) != 0) set++;

            return set;
        }

        /// <summary>The bits the under-buildings flood counts as covered: a triangle at or above the hit.</summary>
        internal const byte CoverAtHit = CoverBuilding | CoverProp;

        /// <summary>
        /// One stored mesh's triangles rasterised onto a band's grid from above: every non-vertical triangle marks the cells
        /// whose centre it holds - the cells a ray from above would have met it on - that have a hit and are not flagged in
        /// <paramref name="skip"/>, with <see cref="CoverFootprint"/> where it reaches the ground (<paramref name="ground"/>)
        /// less the slack, and with <paramref name="bit"/>
        /// where it lies AT OR ABOVE the cell's hit less <paramref name="slackCodes"/> (a bunker or a car park under the
        /// ground is under the hit and covers nothing for the flood). Everything in the file's quantised codes, so a stored
        /// file needs no decode. Pure arrays.
        /// </summary>
        /// <param name="x">The mesh's quantised x codes.</param>
        /// <param name="y">Its quantised y codes, on the band's height scale.</param>
        /// <param name="z">Its quantised z codes.</param>
        /// <param name="indices">Three per triangle.</param>
        /// <param name="sx">A quantised x code to a column, fractional.</param>
        /// <param name="sz">A quantised z code to a row.</param>
        /// <param name="heights">The band's height codes.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="slackCodes">How far under the hit a triangle may lie and still cover, in codes.</param>
        /// <param name="bit">The bit to set.</param>
        /// <param name="cover">The mask, <paramref name="w"/> x <paramref name="h"/>.</param>
        /// <param name="skip">Per-cell bits of cells this mesh must not cover, or null.</param>
        /// <param name="skipBit">Which bit of <paramref name="skip"/> says so.</param>
        /// <param name="ground">The ground <see cref="CoverFootprint"/> is judged against (the band's opening,
        /// <see cref="OpenedCodes"/>), or null to judge it against the hit as <paramref name="bit"/> is.</param>
        /// <returns>Whether any cell was marked with <paramref name="bit"/>.</returns>
        internal static bool CoverTriangles(ushort[] x, ushort[] y, ushort[] z, uint[] indices, double sx, double sz, ushort[] heights,
            int w, int h, int slackCodes, byte bit, byte[] cover, byte[] skip = null, byte skipBit = 0, ushort[] ground = null)
        {
            if (x == null || y == null || z == null || indices == null || heights == null || cover == null) return false;

            var touched = false;
            var vertices = Math.Min(x.Length, Math.Min(y.Length, z.Length));

            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var v0 = indices[t];
                var v1 = indices[t + 1];
                var v2 = indices[t + 2];
                if (v0 >= vertices || v1 >= vertices || v2 >= vertices) continue;

                double x0 = x[v0] * sx, z0 = z[v0] * sz;
                double x1 = x[v1] * sx, z1 = z[v1] * sz;
                double x2 = x[v2] * sx, z2 = z[v2] * sz;

                // the triangle's footprint (twice its area in cells): a vertical face covers nothing
                var area2 = (x1 - x0) * (z2 - z0) - (x2 - x0) * (z1 - z0);
                if (Math.Abs(area2) < 1e-3) continue;

                var topCode = Math.Max(y[v0], Math.Max(y[v1], y[v2]));

                var minCol = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
                var maxCol = Math.Min(w - 1, (int)Math.Floor(Math.Max(x0, Math.Max(x1, x2))));
                var minRow = Math.Max(0, (int)Math.Floor(Math.Min(z0, Math.Min(z1, z2))));
                var maxRow = Math.Min(h - 1, (int)Math.Floor(Math.Max(z0, Math.Max(z1, z2))));
                if (minCol > maxCol || minRow > maxRow) continue;

                var inv = 1d / area2;

                for (var row = minRow; row <= maxRow; row++)
                {
                    var pz = row + 0.5;

                    for (var col = minCol; col <= maxCol; col++)
                    {
                        var px = col + 0.5;

                        // barycentric edge functions, sign-normalised by the area
                        var e0 = ((x1 - x0) * (pz - z0) - (px - x0) * (z1 - z0)) * inv;
                        var e1 = ((x2 - x1) * (pz - z1) - (px - x1) * (z2 - z1)) * inv;
                        var e2 = ((x0 - x2) * (pz - z2) - (px - x2) * (z0 - z2)) * inv;

                        if (e0 < 0d || e1 < 0d || e2 < 0d) continue;

                        var n = row * w + col;
                        var hit = heights[n];
                        if (hit == MapMeshFile.NoHit) continue;   // no ground
                        if (skip != null && (skip[n] & skipBit) != 0) continue;

                        cover[n] |= CoverAnyHeight;

                        // review 2026-09-29 (F3): the footprint only from a mesh that reaches the GROUND - a basement or a tunnel
                        // under a collider-only structure is no drawn thing over it. The ground is the opening, not the hit: a
                        // stack's cap collider stands over its stored shell's top, and the shell is what is drawn there
                        var reference = ground != null ? ground[n] : hit;
                        if (reference != MapMeshFile.NoHit && topCode + slackCodes >= reference) cover[n] |= CoverFootprint;

                        if (topCode + slackCodes < hit) continue;   // under the ground

                        cover[n] |= bit;
                        touched = true;
                    }
                }
            }

            return touched;
        }

        /// <summary>
        /// The ground under the covered cells, flooded in from their edges (MapMeshBuilder.LowerReliefUnderBuildings): every
        /// uncovered measured cell next to a covered one is a seed carrying its own code, and the covered cells a seed
        /// reaches through covered cells take its code. Two fills:
        /// <list type="bullet">
        /// <item>NEAREST - one breadth-first flood from every seed at once, the nearest edge winning: the old rule, which
        /// carries a slope under a building.</item>
        /// <item>LOWEST (<paramref name="lowestFirst"/>) - the seeds lowest first, each drained whole before the next, so a
        /// covered cell takes the lowest edge that reaches it: an uncovered high cell (a stack's cap) no longer fills its
        /// neighbours with its own height.</item>
        /// </list>
        /// A covered cell takes the lowest fill when that is at most <paramref name="maxDropCodes"/> under its own cast
        /// height (a bridge deck over a valley is not dug to the valley floor), otherwise the nearest fill (review
        /// 2026-09-29, F1: a 30 m building on a mesh floor still goes to its edge, as it did before the lowest fill). Either
        /// way only DOWN, and never under its <paramref name="floor"/> - the terrain its column met: the bank under a
        /// bridge's end is the terrain, whatever the river beside it. Pure arrays, O(cells + seeds log seeds).
        /// </summary>
        /// <param name="heights">The band's codes, written in place.</param>
        /// <param name="cover">The cover mask: a cell with any of <see cref="CoverAtHit"/> is covered.</param>
        /// <param name="w">Columns.</param>
        /// <param name="h">Rows.</param>
        /// <param name="lowestFirst">Try the lowest fill first; false is the nearest fill alone.</param>
        /// <param name="maxDropCodes">The deepest the lowest fill may lower a cell, in codes.</param>
        /// <param name="floor">Per cell, the lowest code it may take (<see cref="MapMeshFile.NoHit"/>: none), or null.</param>
        /// <param name="coveredCells">The covered cells.</param>
        /// <param name="keptLower">Covered cells the cast already measured at or under both fills (a sunken yard).</param>
        /// <param name="nearest">Cells lowered to the nearest fill because the lowest lay deeper than the bound.</param>
        /// <param name="leftAsCast">Cells whose lowest fill lay deeper than the bound and whose nearest fill was not lower -
        /// kept as cast (a bridge's bank).</param>
        /// <returns>The cells lowered.</returns>
        internal static int Flood(ushort[] heights, byte[] cover, int w, int h, bool lowestFirst, double maxDropCodes, ushort[] floor,
            out int coveredCells, out int keptLower, out int nearest, out int leftAsCast)
        {
            coveredCells = 0;
            keptLower = 0;
            nearest = 0;
            leftAsCast = 0;

            var n0 = w * h;
            if (heights == null || cover == null || w < 1 || h < 1 || heights.Length < n0 || cover.Length < n0) return 0;
            if (floor != null && floor.Length < n0) throw new ArgumentException("floor is shorter than the grid", nameof(floor));

            // the seeds: uncovered measured cells with a covered neighbour
            var seedList = new System.Collections.Generic.List<int>();

            for (var n = 0; n < n0; n++)
            {
                if ((cover[n] & CoverAtHit) != 0 || heights[n] == MapMeshFile.NoHit) continue;

                var col = n % w;
                var row = n / w;
                if ((col > 0 && (cover[n - 1] & CoverAtHit) != 0) || (col + 1 < w && (cover[n + 1] & CoverAtHit) != 0) ||
                    (row > 0 && (cover[n - w] & CoverAtHit) != 0) || (row + 1 < h && (cover[n + w] & CoverAtHit) != 0))
                    seedList.Add(n);
            }

            var seeds = seedList.ToArray();
            var queue = new int[n0];

            var nearFill = new ushort[n0];
            var nearReached = new bool[n0];
            FillFrom(heights, cover, w, h, seeds, false, nearFill, nearReached, queue);

            ushort[] lowFill = null;
            bool[] lowReached = null;

            if (lowestFirst)
            {
                // lowest first; seeds of equal height fill alike, so the unstable sort changes nothing
                var sorted = (int[])seeds.Clone();
                var keys = new ushort[sorted.Length];
                for (var k = 0; k < sorted.Length; k++) keys[k] = heights[sorted[k]];
                Array.Sort(keys, sorted);

                lowFill = new ushort[n0];
                lowReached = new bool[n0];
                FillFrom(heights, cover, w, h, sorted, true, lowFill, lowReached, queue);
            }

            var lowered = 0;

            for (var n = 0; n < n0; n++)
            {
                if ((cover[n] & CoverAtHit) == 0) continue;
                coveredCells++;

                var hit = heights[n];
                if (hit == MapMeshFile.NoHit || !nearReached[n]) continue;   // both floods reach the same cells

                var bottom = floor != null ? floor[n] : MapMeshFile.NoHit;

                if (lowFill != null)
                {
                    var low = Clamp(lowFill[n], bottom);

                    if (low < hit && hit - low <= maxDropCodes)
                    {
                        heights[n] = low;
                        lowered++;
                        continue;
                    }

                    var near = Clamp(nearFill[n], bottom);

                    if (low < hit)
                    {
                        // the lowest edge lay too deep: the nearest one, as before the lowest fill, or the cast
                        if (near < hit)
                        {
                            heights[n] = near;
                            lowered++;
                            nearest++;
                        }
                        else
                        {
                            leftAsCast++;
                        }

                        continue;
                    }

                    if (lowFill[n] > hit) keptLower++;
                    continue;
                }

                var only = Clamp(nearFill[n], bottom);

                // only ever DOWN: a cell the cast already measured lower than the edge (a sunken yard) keeps its hit
                if (only < hit)
                {
                    heights[n] = only;
                    lowered++;
                }
                else if (nearFill[n] > hit)
                {
                    keptLower++;
                }
            }

            return lowered;
        }

        /// <summary>A fill held at a floor (<see cref="MapMeshFile.NoHit"/>: none).</summary>
        private static ushort Clamp(ushort fill, ushort floor) => floor != MapMeshFile.NoHit && fill < floor ? floor : fill;

        /// <summary>
        /// One flood of <see cref="Flood"/>: the seeds carry their own codes into the covered cells they reach through covered
        /// cells, breadth-first. <paramref name="oneAtATime"/> drains each seed whole before the next (the order of
        /// <paramref name="seeds"/> then decides); otherwise all start together and the nearest wins. Every cell enters
        /// <paramref name="queue"/> at most once (reached guards it, seeds included), so a flat array never wraps.
        /// </summary>
        private static void FillFrom(ushort[] heights, byte[] cover, int w, int h, int[] seeds, bool oneAtATime, ushort[] fill,
            bool[] reached, int[] queue)
        {
            foreach (var s in seeds)
            {
                reached[s] = true;
                fill[s] = heights[s];
            }

            var head = 0;
            var tail = 0;

            if (!oneAtATime)
            {
                foreach (var s in seeds) queue[tail++] = s;
                Drain();
                return;
            }

            foreach (var s in seeds)
            {
                queue[tail++] = s;
                Drain();
            }

            void Drain()
            {
                while (head < tail)
                {
                    var n = queue[head++];
                    var col = n % w;
                    var row = n / w;
                    var code = fill[n];

                    if (col > 0) Visit(n - 1, code);
                    if (col + 1 < w) Visit(n + 1, code);
                    if (row > 0) Visit(n - w, code);
                    if (row + 1 < h) Visit(n + w, code);
                }
            }

            void Visit(int m, ushort code)
            {
                if (reached[m] || (cover[m] & CoverAtHit) == 0) return;
                reached[m] = true;
                fill[m] = code;
                queue[tail++] = m;
            }
        }

        /// <summary>Whether the arguments describe a grid this can work on; a protect array of the wrong length is the
        /// caller's bug and throws rather than silently protecting nothing.</summary>
        private static bool Valid(int length, int w, int h, float cellMetres, float windowMetres, float riseMetres, bool[] protect)
        {
            if (w < 1 || h < 1 || (long)w * h > length) return false;
            if (!(cellMetres > 0f) || !(windowMetres > 0f) || !IsFinite(cellMetres) || !IsFinite(windowMetres)) return false;
            if (float.IsNaN(riseMetres) || riseMetres < 0f) return false;

            if (protect != null && protect.Length < w * h)
                throw new ArgumentException($"protect holds {protect.Length} cells for a {w} x {h} grid", nameof(protect));

            return true;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
