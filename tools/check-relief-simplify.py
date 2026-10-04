"""Opt B's relief simplification, checked on the SHIPPING code.

Extracts the ReliefSimplifier region (between "// BEGIN ReliefSimplifier" and "// END ReliefSimplifier") from
Source/Tarkov-QuestTree/UI/Map3DView.cs, compiles it with a small C# harness in a temporary .NET project, and runs it on a
synthetic band cut into overlapping blocks exactly as Map3DView.PrepareGroundBlocks cuts it (side quads a step, one quad of
overlap), at block sides 24, 64 and 253, with the PRODUCTION ReliefSimplifyTolerance and ReliefLeafMaxQuads parsed from
their declarations in Map3DView.cs (a tolerance of 0 or an unparsable constant fails at once). The harness unions every block's triangles (identical triangles of an overlap strip counted once) and fails on:

  - a T-junction or a crack: any drawn vertex lying strictly inside any drawn edge, in grid coordinates - within a block
    (a merged quad next to smaller ones) or across a block line (two blocks splitting their shared line differently);
  - a gap or an overlap: the union's area must equal the whole quads' area, every triangle facing up;
  - the tolerance: every grid height within the tolerance of the drawn surface over it;
  - no simplification at all (so the check cannot pass on a simplifier that does nothing).
  - at the production-like sides 64 and 253, any leaf size 1, 2, .. ReliefLeafMaxQuads that never formed (so the largest leaves are exercised);
    each side's leaf-size histogram is printed.

Then it proves it can fail: the same region with the block ring merged (ring 0: a crack at the block lines) and with the fan
switched off (a merged quad drawn as two triangles beside smaller ones: T-junctions), and the shipping code at
tolerance 0 (nothing merges), must all FAIL.

Usage: python tools/check-relief-simplify.py        (exit 0 = the shipping code passes and every broken variant fails)
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Source', 'Tarkov-QuestTree', 'UI', 'Map3DView.cs')

HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Linq;

static class Program
{
    static int Main(string[] args)
    {
        // the production constants, read from Map3DView.cs by the script and passed in
        var tol = double.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
        var maxLeaf = int.Parse(args[1]);

        const int W = 301, H = 277;          // vertices: 300 x 276 quads, not a multiple of either block side

        var h = new float[W * H];
        var rng = new Random(7);

        for (var z = 0; z < H; z++)
            for (var x = 0; x < W; x++)
            {
                double y;
                if (x < 120 && z < 120) y = 10.0;                                       // flat plateau
                else if (x >= 120 && x < 220 && z < 120) y = 10.0 + 0.05 * x + 0.02 * z;  // tilted plane
                else if (z >= 120 && z < 200) y = 12.0 + 1.5 * Math.Sin(x * 0.11) * Math.Cos(z * 0.07);  // rolling
                else y = 11.0 + rng.NextDouble() * 0.6;                                 // rough
                if (x > 240 && z > 30 && z < 90) y += 2.0;                              // a step
                if (x > 40 && x < 52 && z > 40 && z < 52) y += 0.5;                     // a bump in the plateau
                h[z * W + x] = (float)(Math.Round(y * 100.0) / 100.0);
            }

        // holes: a patch with no hit, and a scatter
        for (var z = 140; z < 150; z++) for (var x = 50; x < 57; x++) h[z * W + x] = float.NaN;
        for (var i = 0; i < 80; i++) h[rng.Next(H) * W + rng.Next(W)] = float.NaN;

        var allFailures = new List<string>();

        // PrepareGroundBlocks' side is round(128 m / cell) clamped to sqrt(65535) - 2 = 253, and cells are multiples of
        // 0.5 m, so the real sides are 253, 128, 85, 64, 51 ... - 253 (0.5 m cells) and 64 (2 m cells) are checked, both
        // with room for maxLeaf leaves inside the ring, and every leaf size up to maxLeaf must form there. Side 24 is
        // kept as before (no production side; it clips the leaves at 16).
        foreach (var side in new[] { 24, 64, 253 })
        {
            var failures = Check(h, W, H, side, tol, maxLeaf, side != 24);
            foreach (var f in failures) allFailures.Add($"side {side}: {f}");
        }

        foreach (var f in allFailures) Console.WriteLine("FAIL: " + f);
        Console.WriteLine(allFailures.Count == 0 ? "PASS" : "FAILED");
        return allFailures.Count == 0 ? 0 : 1;
    }

    static List<string> Check(float[] h, int W, int H, int side, double tol, int maxLeaf, bool needMaxLeaf)
    {
        var stride = side + 2;
        var heights = new float[stride * stride];
        var sizes = new int[(stride - 1) * (stride - 1)];
        var marks = new bool[stride * stride];
        var tris = new List<int>();

        var union = new HashSet<(long, long, long)>();
        var triangles = new List<(int, int, int)>();   // global vertex ids, union order
        var histogram = new SortedDictionary<int, long>();   // owned leaves by size (quads a side)
        long full = 0, drawn = 0;

        for (var firstRow = 0; firstRow < H - 1; firstRow += side)
        {
            var lastRow = Math.Min(H - 1, firstRow + side + 1);
            for (var firstCol = 0; firstCol < W - 1; firstCol += side)
            {
                var lastCol = Math.Min(W - 1, firstCol + side + 1);
                var qw = lastCol - firstCol;
                var qh = lastRow - firstRow;
                var width = qw + 1;

                for (var z = 0; z <= qh; z++)
                    for (var x = 0; x <= qw; x++)
                        heights[z * width + x] = h[(firstRow + z) * W + firstCol + x];

                drawn += QuestTree.UI.Map3DView.ReliefSimplifier.Build(heights, qw, qh, side, side, tol, maxLeaf, sizes, marks, tris, out var f);
                full += f;

                // the leaves this block owns (origin quad not in the overlap strip), so none is counted twice
                for (var z = 0; z < Math.Min(qh, side); z++)
                    for (var x = 0; x < Math.Min(qw, side); x++)
                    {
                        var s = sizes[z * qw + x];
                        if (s < 1) continue;
                        histogram.TryGetValue(s, out var n);
                        histogram[s] = n + 1;
                    }

                for (var t = 0; t < tris.Count; t += 3)
                {
                    int G(int s) => (firstRow + s / width) * W + firstCol + s % width;
                    var a = G(tris[t]); var b = G(tris[t + 1]); var c = G(tris[t + 2]);
                    var key = Canon(a, b, c);
                    if (union.Add(key)) triangles.Add((a, b, c));
                }
            }
        }

        var failures = new List<string>();

        // --- every drawn vertex, and every drawn edge: no vertex strictly inside an edge
        var used = new HashSet<int>();
        var edges = new HashSet<(int, int)>();
        foreach (var (a, b, c) in triangles)
        {
            used.Add(a); used.Add(b); used.Add(c);
            edges.Add((Math.Min(a, b), Math.Max(a, b)));
            edges.Add((Math.Min(b, c), Math.Max(b, c)));
            edges.Add((Math.Min(a, c), Math.Max(a, c)));
        }

        var tj = 0;
        foreach (var (p, q) in edges)
        {
            int px = p % W, pz = p / W, qx = q % W, qz = q / W;
            int dx = qx - px, dz = qz - pz;
            var g = Gcd(Math.Abs(dx), Math.Abs(dz));
            for (var k = 1; k < g; k++)
            {
                var v = (pz + dz / g * k) * W + px + dx / g * k;
                if (used.Contains(v)) { tj++; if (tj <= 3) failures.Add($"vertex ({v % W},{v / W}) inside edge ({px},{pz})-({qx},{qz})"); }
            }
        }
        if (tj > 0) failures.Add($"{tj} T-junction/crack vertex(es) on drawn edges");

        // --- area: the union covers exactly the whole quads, every triangle facing up
        long whole = 0;
        for (var z = 0; z < H - 1; z++)
            for (var x = 0; x < W - 1; x++)
                if (!float.IsNaN(h[z * W + x]) && !float.IsNaN(h[z * W + x + 1]) && !float.IsNaN(h[(z + 1) * W + x]) && !float.IsNaN(h[(z + 1) * W + x + 1]))
                    whole++;

        long twice = 0; var down = 0;
        foreach (var (a, b, c) in triangles)
        {
            long ax = a % W, az = a / W, bx = b % W, bz = b / W, cx = c % W, cz = c / W;
            // the relief's winding (a, c, b) with c = +z, b = +x: cross(b - a, c - a).y in x/z = (bz-az)(cx-ax) - (bx-ax)(cz-az) > 0
            var cross = (bz - az) * (cx - ax) - (bx - ax) * (cz - az);
            if (cross <= 0) down++;
            twice += Math.Abs(cross);
        }
        if (down > 0) failures.Add($"{down} triangle(s) not facing up");
        if (twice != 2 * whole) failures.Add($"area {twice / 2.0} quads drawn against {whole} whole quads (a gap or an overlap)");

        // --- tolerance: every grid point inside a triangle within tol of the drawn plane there
        var worst = 0.0;
        foreach (var (a, b, c) in triangles)
        {
            int ax = a % W, az = a / W, bx = b % W, bz = b / W, cx = c % W, cz = c / W;
            int x0 = Math.Min(ax, Math.Min(bx, cx)), x1 = Math.Max(ax, Math.Max(bx, cx));
            int z0 = Math.Min(az, Math.Min(bz, cz)), z1 = Math.Max(az, Math.Max(bz, cz));
            double det = (bx - ax) * (double)(cz - az) - (cx - ax) * (double)(bz - az);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    var l1 = ((x - ax) * (double)(cz - az) - (cx - ax) * (double)(z - az)) / det;
                    var l2 = ((bx - ax) * (double)(z - az) - (x - ax) * (double)(bz - az)) / det;
                    var l0 = 1 - l1 - l2;
                    if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
                    var surface = l0 * h[a] + l1 * h[b] + l2 * h[c];
                    var err = Math.Abs(surface - h[z * W + x]);
                    if (double.IsNaN(err)) { failures.Add($"a hole at ({x},{z}) under a drawn triangle"); continue; }
                    worst = Math.Max(worst, err);
                }
        }
        if (worst > tol + 1e-6) failures.Add($"height error {worst:0.000} m over the tolerance {tol} m");

        if (drawn >= full) failures.Add($"nothing simplified ({drawn} of {full} owned triangles)");

        // every leaf size up to maxLeaf must have formed, or the large-leaf paths went untested
        if (needMaxLeaf)
            for (var s = 1; s <= maxLeaf; s *= 2)
                if (!histogram.ContainsKey(s)) failures.Add($"no leaf of size {s} formed (maxLeaf {maxLeaf})");

        var hist = string.Join(", ", histogram.Select(kv => $"{kv.Key}:{kv.Value}"));
        Console.WriteLine($"side {side}, tolerance {tol}, maxLeaf {maxLeaf}: owned relief triangles {full} -> {drawn}; union {triangles.Count} triangles, {used.Count} vertices; worst error {worst:0.000} m");
        Console.WriteLine($"side {side} leaf-size histogram (size:count) {hist}");
        return failures;
    }

    static (long, long, long) Canon(int a, int b, int c)
    {
        // a rotation of the same winding is the same triangle
        if (a <= b && a <= c) return (a, b, c);
        if (b <= a && b <= c) return (b, c, a);
        return (c, a, b);
    }

    static int Gcd(int a, int b) { while (b != 0) { var t = a % b; a = b; b = t; } return a; }
}
'''

PROJECT = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>disable</Nullable>
  <ImplicitUsings>disable</ImplicitUsings><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>
</Project>
'''


def source():
    return open(SOURCE, encoding='utf-8').read()


def region(text):
    m = re.search(r'// BEGIN ReliefSimplifier[^\n]*\n(.*?)// END ReliefSimplifier', text, re.S)
    if not m:
        sys.exit('ReliefSimplifier region not found in Map3DView.cs')
    return m.group(1)


def constants(text):
    """The production ReliefSimplifyTolerance and ReliefLeafMaxQuads, parsed from their declarations. A tolerance of 0
    (the rollback) or a leaf under 2 would make the check vacuous - nothing merges - so either FAILS here, loudly."""
    m = re.findall(r'\bfloat\s+ReliefSimplifyTolerance\s*=\s*([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)[fF]?\s*;', text)
    n = re.findall(r'\bconst\s+int\s+ReliefLeafMaxQuads\s*=\s*([0-9_]+)\s*;', text)
    if len(m) != 1 or len(n) != 1:
        sys.exit('FAIL: could not parse exactly one ReliefSimplifyTolerance (%d) and ReliefLeafMaxQuads (%d) declaration '
                 'in Map3DView.cs' % (len(m), len(n)))
    tol = float(m[0])
    leaf = int(n[0].replace('_', ''))
    if not tol > 0:
        sys.exit('FAIL: production ReliefSimplifyTolerance parses as %r - the simplifier is rolled back and this check '
                 'would pass vacuously' % tol)
    if leaf < 2 or leaf & (leaf - 1):
        sys.exit('FAIL: production ReliefLeafMaxQuads = %d is not a power of two >= 2' % leaf)
    return tol, leaf


def mutate(code, marker, old, new):
    lines = code.split('\n')
    hits = [i for i, line in enumerate(lines) if marker in line]
    if len(hits) != 1 or old not in lines[hits[0]]:
        sys.exit('mutation marker %s not found as expected' % marker)
    lines[hits[0]] = lines[hits[0]].replace(old, new)
    return '\n'.join(lines)


def run(name, code, work, tol, leaf):
    folder = os.path.join(work, name)
    os.makedirs(folder)
    with open(os.path.join(folder, 'check.csproj'), 'w') as f:
        f.write(PROJECT)
    with open(os.path.join(folder, 'Program.cs'), 'w', encoding='utf-8') as f:
        f.write(HARNESS)
    with open(os.path.join(folder, 'Simplifier.cs'), 'w', encoding='utf-8') as f:
        f.write('namespace QuestTree.UI\n{\n    internal static partial class Map3DView\n    {\n' + code + '\n    }\n}\n')
    out = subprocess.run(['dotnet', 'run', '-c', 'Release', '--project', folder, '--', repr(tol), str(leaf)],
                         capture_output=True, text=True)
    print('--- %s (exit %d)' % (name, out.returncode))
    print(out.stdout.strip()[-2500:] or out.stderr.strip()[-2500:])
    return out.returncode, out.stdout


def main():
    text = source()
    code = region(text)
    tol, leaf = constants(text)
    print('production constants: ReliefSimplifyTolerance %r m, ReliefLeafMaxQuads %d' % (tol, leaf))

    work = tempfile.mkdtemp(prefix='relief-check-')
    try:
        ok, _ = run('shipping', code, work, tol, leaf)
        broken = [
            run('broken-ring0', mutate(code, 'CHECK-MUTATE-RING', '= 1;', '= 0;'), work, tol, leaf),
            run('broken-nofan', mutate(code, 'CHECK-MUTATE-FAN', 'perimeter.Count > 4', 'false'), work, tol, leaf),
            run('broken-tolerance0', code, work, 0.0, leaf),
        ]
    finally:
        shutil.rmtree(work, ignore_errors=True)

    broken_fail = all(rc == 1 and 'PASS' not in out and 'FAILED' in out for rc, out in broken)
    print()
    print('shipping code: %s; broken variants fail: %s' % ('PASS' if ok == 0 else 'FAIL', 'yes' if broken_fail else 'NO'))
    sys.exit(0 if ok == 0 and broken_fail else 1)


if __name__ == '__main__':
    main()
