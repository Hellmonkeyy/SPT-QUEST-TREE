"""Opt B's relief simplification, checked on the SHIPPING code.

Extracts the ReliefSimplifier region (between "// BEGIN ReliefSimplifier" and "// END ReliefSimplifier") from
Source/Tarkov-QuestTree/UI/Map3DView.cs, compiles it with a small C# harness in a temporary .NET project, and runs it on a
synthetic band cut into overlapping blocks exactly as Map3DView.PrepareGroundBlocks cuts it (side quads a step, one quad of
overlap). The harness unions every block's triangles (identical triangles of an overlap strip counted once) and fails on:

  - a T-junction or a crack: any drawn vertex lying strictly inside any drawn edge, in grid coordinates - within a block
    (a merged quad next to smaller ones) or across a block line (two blocks splitting their shared line differently);
  - a gap or an overlap: the union's area must equal the whole quads' area, every triangle facing up;
  - the tolerance: every grid height within the tolerance of the drawn surface over it;
  - no simplification at all (so the check cannot pass on a simplifier that does nothing).

Then it proves it can fail: the same region with the block ring merged (ring 0: a crack at the block lines) and with the fan
switched off (a merged quad drawn as two triangles beside smaller ones: T-junctions) must both FAIL.

Usage: python tools/check-relief-simplify.py        (exit 0 = the shipping code passes and both broken variants fail)
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

static class Program
{
    static int Main()
    {
        const int W = 181, H = 157;          // vertices: 180 x 156 quads, not a multiple of the block side
        const int side = 24;                  // quads a block steps by (PrepareGroundBlocks: 128 m / cell, clamped)
        const double tol = 0.15;
        const int maxLeaf = 8;

        var h = new float[W * H];
        var rng = new Random(7);

        for (var z = 0; z < H; z++)
            for (var x = 0; x < W; x++)
            {
                double y;
                if (x < 60 && z < 60) y = 10.0;                                   // flat plateau
                else if (x >= 60 && x < 120 && z < 60) y = 10.0 + 0.05 * x + 0.02 * z;  // tilted plane
                else if (z >= 60 && z < 110) y = 12.0 + 1.5 * Math.Sin(x * 0.11) * Math.Cos(z * 0.07);  // rolling
                else y = 11.0 + rng.NextDouble() * 0.6;                           // rough
                if (x > 140 && z > 20 && z < 50) y += 2.0;                        // a step
                h[z * W + x] = (float)(Math.Round(y * 100.0) / 100.0);
            }

        // holes: a patch with no hit, and a scatter
        for (var z = 70; z < 80; z++) for (var x = 30; x < 37; x++) h[z * W + x] = float.NaN;
        for (var i = 0; i < 40; i++) h[rng.Next(H) * W + rng.Next(W)] = float.NaN;

        var stride = side + 2;
        var heights = new float[stride * stride];
        var sizes = new int[(stride - 1) * (stride - 1)];
        var marks = new bool[stride * stride];
        var tris = new List<int>();

        var union = new HashSet<(long, long, long)>();
        var triangles = new List<(int, int, int)>();   // global vertex ids, union order
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

        Console.WriteLine($"owned relief triangles {full} -> {drawn}; union {triangles.Count} triangles, {used.Count} vertices; worst error {worst:0.000} m");
        foreach (var f in failures) Console.WriteLine("FAIL: " + f);
        Console.WriteLine(failures.Count == 0 ? "PASS" : "FAILED");
        return failures.Count == 0 ? 0 : 1;
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


def region():
    text = open(SOURCE, encoding='utf-8').read()
    m = re.search(r'// BEGIN ReliefSimplifier[^\n]*\n(.*?)// END ReliefSimplifier', text, re.S)
    if not m:
        sys.exit('ReliefSimplifier region not found in Map3DView.cs')
    return m.group(1)


def mutate(code, marker, old, new):
    lines = code.split('\n')
    hits = [i for i, line in enumerate(lines) if marker in line]
    if len(hits) != 1 or old not in lines[hits[0]]:
        sys.exit('mutation marker %s not found as expected' % marker)
    lines[hits[0]] = lines[hits[0]].replace(old, new)
    return '\n'.join(lines)


def run(name, code, work):
    folder = os.path.join(work, name)
    os.makedirs(folder)
    with open(os.path.join(folder, 'check.csproj'), 'w') as f:
        f.write(PROJECT)
    with open(os.path.join(folder, 'Program.cs'), 'w', encoding='utf-8') as f:
        f.write(HARNESS)
    with open(os.path.join(folder, 'Simplifier.cs'), 'w', encoding='utf-8') as f:
        f.write('namespace QuestTree.UI\n{\n    internal static partial class Map3DView\n    {\n' + code + '\n    }\n}\n')
    out = subprocess.run(['dotnet', 'run', '-c', 'Release', '--project', folder], capture_output=True, text=True)
    print('--- %s (exit %d)' % (name, out.returncode))
    print(out.stdout.strip()[-1500:] or out.stderr.strip()[-1500:])
    return out.returncode, out.stdout


def main():
    code = region()
    work = tempfile.mkdtemp(prefix='relief-check-')
    try:
        ok, _ = run('shipping', code, work)
        crack, out1 = run('broken-ring0', mutate(code, 'CHECK-MUTATE-RING', '= 1;', '= 0;'), work)
        tee, out2 = run('broken-nofan', mutate(code, 'CHECK-MUTATE-FAN', 'perimeter.Count > 4', 'false'), work)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    broken_fail = crack == 1 and tee == 1 and 'PASS' not in out1 and 'PASS' not in out2
    print()
    print('shipping code: %s; broken variants fail: %s' % ('PASS' if ok == 0 else 'FAIL', 'yes' if broken_fail else 'NO'))
    sys.exit(0 if ok == 0 and broken_fail else 1)


if __name__ == '__main__':
    main()
