#!/usr/bin/env python3
"""Compares an ACCUMULATED 3D mesh (A) with one built FROM SCRATCH (L) - WP2's invariants (PART-05 section 3).

A capture that adds to the stored mesh (MapMeshBuilder.Request.Base) must end with a mesh that is a SUPERSET of what the
old path would have built at the same stop: every building L has is in A (I1), none of them with fewer triangles while
the union does not bind (I2), the relief measured and at least as close (I3), a y range never narrower (I4), and the
tiles at least as sharp (I5). MeshVerifyLastStop writes L beside A at a campaign's last stop; a folder saved before a
raid is L for the cross-raid check.

Usage:
  python tools/compare-mesh.py captures/<key>          A = the meta's <key>-mesh.bin, L = <key>-mesh.verify.bin
  python tools/compare-mesh.py A L                     each a capture folder (its meta's mesh) or a *-mesh.bin /
                                                       *-mesh.verify.bin file; the sidecars and atlas pages beside
                                                       them are found by name
  --binding   the accumulation line said the union bound (area scale under x1.00): a building with fewer
              triangles in A is DP1's price, a WARN, not a failure

Reads the files with tools/check-capture.py's own read_mesh/read_index/read_png_planes (imported, not copied), so
the two tools cannot disagree about the format. Stdlib only.

Exit 1 on: a building of L absent from A (I1), a building with fewer triangles in A without --binding (I2), a relief
cell L measured that A lacks or holds at another height (I3), a y range of A not holding L's (I4). Tiles blurrier in A
(I5) are WARNs. The sidecars are optional: without them I1 matches by the mesh's 32-bit Key alone and I5 is skipped.
"""

import hashlib
import importlib.util
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location("check_capture", str(HERE / "check-capture.py"))
cc = importlib.util.module_from_spec(_spec)
_argv, sys.argv = sys.argv, [sys.argv[0]]      # check-capture reads its arguments at import
try:
    _spec.loader.exec_module(cc)
finally:
    sys.argv = _argv

Y_SLACK = 5.0            # MapMeshFile.YSlackMetres: a range may differ by its own slack
SHARPER_SHARE = 0.9      # I5: A is "blurrier" when its tile's mean gradient is under this share of L's


def locate(arg, verify=False):
    """(mesh path, sidecar path or None, {page: png path}) for a folder or a mesh file."""
    path = Path(arg)
    if path.is_dir():
        key = path.name
        if verify:
            mesh = path / f"{key}-mesh.verify.bin"
            return mesh, _maybe(path / f"{key}-mesh.verify.index"), _pages(path, f"{key}-verify-atlas-")
        metas = sorted(path.glob("*.map.json"))
        if len(metas) != 1:
            sys.exit(f"COMPARE FAILED: {path} has no single *.map.json")
        meta = json.loads(metas[0].read_text(encoding="utf-8"))
        block = meta.get("mesh") or {}
        if not isinstance(block.get("file"), str):
            sys.exit(f"COMPARE FAILED: {metas[0].name} names no mesh")
        pages = {}
        for entry in meta.get("atlas") or []:
            if isinstance(entry, dict) and isinstance(entry.get("page"), int) and isinstance(entry.get("file"), str):
                pages[entry["page"]] = path / entry["file"]
        return path / block["file"], _maybe(path / f"{key}{cc.INDEX_SUFFIX}"), pages
    name = path.name
    if name.endswith("-mesh.verify.bin"):
        stem = name[:-len("-mesh.verify.bin")]
        return path, _maybe(path.with_name(f"{stem}-mesh.verify.index")), _pages(path.parent, f"{stem}-verify-atlas-")
    if name.endswith(cc.MESH_SUFFIX):
        stem = name[:-len(cc.MESH_SUFFIX)]
        return path, _maybe(path.with_name(f"{stem}{cc.INDEX_SUFFIX}")), _pages(path.parent, f"{stem}-atlas-")
    sys.exit(f"COMPARE FAILED: {arg} is neither a capture folder nor a mesh file")


def _maybe(path):
    return path if path.is_file() else None


def _pages(folder, prefix):
    pages = {}
    for p in folder.glob(prefix + "*.png"):
        tail = p.name[len(prefix):-len(".png")]
        if tail.isdigit():
            pages[int(tail)] = p
    return pages


def load(arg, verify=False):
    mesh_path, index_path, pages = locate(arg, verify)
    if not mesh_path.is_file():
        sys.exit(f"COMPARE FAILED: no mesh at {mesh_path}")
    data = mesh_path.read_bytes()
    try:
        mesh = cc.read_mesh(data, keep=True)
    except cc.MeshError as exc:
        sys.exit(f"COMPARE FAILED: {mesh_path.name} {exc}")
    index = None
    if index_path is not None:
        try:
            index = cc.read_index(index_path.read_bytes())
            if index["sha"] != hashlib.sha256(data).hexdigest() or len(index["buildings"]) != len(mesh["shapes"]):
                print(f"NOTE   {index_path.name} does not describe {mesh_path.name} - compared without it")
                index = None
        except cc.MeshError as exc:
            print(f"NOTE   {index_path.name} {exc} - compared without it")
            index = None
    return {"name": mesh_path.name, "mesh": mesh, "index": index, "pages": pages, "planes": {}}


def dequantise(code, low, high):
    return low + (high - low) * code / cc.MESH_MAX_QUANTISED


def i1_buildings(a, l, errors, lines):
    """I1: every building of L is in A - by its Key, counted with multiplicity (identical renderers are stored once
    each), reported with the nearest A building of the same path hash when the sidecars are there."""
    count_a, count_l = {}, {}
    for key, _, _, _ in a["mesh"]["shapes"]:
        count_a[key] = count_a.get(key, 0) + 1
    for key, _, _, _ in l["mesh"]["shapes"]:
        count_l[key] = count_l.get(key, 0) + 1
    missing = {k: n - count_a.get(k, 0) for k, n in count_l.items() if n > count_a.get(k, 0)}
    total = sum(missing.values())
    lines.append(f"I1 buildings: L {len(l['mesh']['shapes']):,}, A {len(a['mesh']['shapes']):,}; "
                 f"{total:,} of L's missing from A")
    if not total:
        return
    notes = []
    if a["index"] is not None and l["index"] is not None:
        by_path = {}
        for row in a["index"]["buildings"]:
            by_path.setdefault(row["pathHash"], []).append(row)
        for i, (key, _, _, _) in enumerate(l["mesh"]["shapes"]):
            if key not in missing or len(notes) >= 8:
                continue
            row = l["index"]["buildings"][i]
            near = [((sum((p - q) ** 2 for p, q in zip(r["centre"], row["centre"]))) ** 0.5) for r in by_path.get(row["pathHash"], [])]
            notes.append(f"{key} (nearest A of its path: {min(near):.2f} m)" if near else f"{key} (no A building of its path)")
    else:
        notes = [str(k) for k in list(missing)[:8]]
    errors.append(f"I1: {total:,} building(s) of L are not in A - first: {', '.join(notes)}")


def i2_triangles(a, l, binding, errors, warnings, lines):
    """I2: no building of L has fewer triangles in A (unless the union bound - DP1)."""
    ka, kl = a["mesh"]["keys"], l["mesh"]["keys"]
    deltas = [(k, ka[k] - kl[k]) for k in kl if k in ka]
    fewer = [(k, d) for k, d in deltas if d < 0]
    buckets = {"< -1000": 0, "-1000..-1": 0, "0": 0, "1..1000": 0, "> 1000": 0}
    for _, d in deltas:
        buckets["< -1000" if d < -1000 else "-1000..-1" if d < 0 else "0" if d == 0 else "1..1000" if d <= 1000 else "> 1000"] += 1
    scales = ""
    if a["index"] is not None and l["index"] is not None:
        scales = f"; effective scale A x{effective_scale(a['index']):.2f}, L x{effective_scale(l['index']):.2f}"
    lines.append(f"I2 triangles: {len(deltas):,} common key(s), A - L histogram " +
                 ", ".join(f"{k} {v:,}" for k, v in buckets.items()) +
                 f"; {len(fewer):,} fewer in A ({sum(d for _, d in fewer):,} triangles){scales}")
    if fewer:
        text = (f"I2: {len(fewer):,} building(s) have fewer triangles in A than in L (first: " +
                ", ".join(f"{k} {d:+,}" for k, d in fewer[:8]) + ")")
        (warnings if binding else errors).append(text + (" - the union bound (--binding): DP1's price" if binding else ""))


def effective_scale(index):
    """The stored triangles over min(source, basis) at scale 1, summed - how far the budget scaled the map down."""
    stored = want = 0
    for row in index["buildings"]:
        basis = min(250_000, max(24, row["surface"] * 20.0))
        want += min(row["source"], basis)
        stored += row["stored"]
    return stored / want if want else 1.0


def i3_relief(a, l, errors, lines):
    """I3: wherever L measured a cell A has a height - the same code when the y ranges are equal, else within half a
    quantum of each - and A's distance byte is at most L's."""
    ma, ml = a["mesh"], l["mesh"]
    same_range = ma["yMin"] == ml["yMin"] and ma["yMax"] == ml["yMax"]
    qa = (ma["yMax"] - ma["yMin"]) / cc.MESH_MAX_QUANTISED
    ql = (ml["yMax"] - ml["yMin"]) / cc.MESH_MAX_QUANTISED
    tolerance = 0.5 * qa + 0.5 * ql + 1e-4
    lost = differ = farther = 0
    worst = 0.0
    for level, (heights_l, distance_l) in ml["grids"].items():
        grid_a = ma["grids"].get(level)
        band_a = next((b for b in ma["bands"] if b["level"] == level), None)
        band_l = next((b for b in ml["bands"] if b["level"] == level), None)
        if grid_a is None or band_a is None or band_a["width"] != band_l["width"] or band_a["height"] != band_l["height"]:
            errors.append(f"I3: band {level} of L has no band of its grid in A")
            continue
        heights_a, distance_a = grid_a
        for n, code_l in enumerate(heights_l):
            if code_l == cc.MESH_NO_HIT:
                continue
            code_a = heights_a[n]
            if code_a == cc.MESH_NO_HIT:
                lost += 1
                continue
            if same_range:
                if code_a != code_l:
                    differ += 1
            else:
                dh = abs(dequantise(code_a, ma["yMin"], ma["yMax"]) - dequantise(code_l, ml["yMin"], ml["yMax"]))
                worst = max(worst, dh)
                if dh > tolerance:
                    differ += 1
            if distance_l[n] != 255 and distance_a[n] > distance_l[n]:
                farther += 1
    lines.append(f"I3 relief: y ranges {'equal' if same_range else 'differ'}" +
                 ("" if same_range else f" (max |dh| {worst:.4f} m, tolerance {tolerance:.4f} m)") +
                 f"; {lost:,} cell(s) L measured and A lacks, {differ:,} at another height, {farther:,} farther in A")
    if lost or differ or farther:
        errors.append(f"I3: {lost:,} cell(s) lost, {differ:,} at another height, {farther:,} with a larger distance in A")


def i4_range(a, l, errors, lines):
    """I4: A's y range holds L's (to the range's own 5 m slack)."""
    ma, ml = a["mesh"], l["mesh"]
    lines.append(f"I4 y range: A {ma['yMin']:.2f}..{ma['yMax']:.2f}, L {ml['yMin']:.2f}..{ml['yMax']:.2f}")
    if ma["yMin"] > ml["yMin"] + Y_SLACK or ma["yMax"] < ml["yMax"] - Y_SLACK:
        errors.append(f"I4: A's y range {ma['yMin']:.2f}..{ma['yMax']:.2f} does not hold L's {ml['yMin']:.2f}..{ml['yMax']:.2f}")


def tile(side, page, x, y, w, h):
    """A tile's three channel planes (lists of rows) out of a page, or None when the page is not readable."""
    if page not in side["planes"]:
        path = side["pages"].get(page)
        side["planes"][page] = cc.read_png_planes(path) if path is not None and path.is_file() else None
    decoded = side["planes"][page]
    if decoded is None:
        return None
    width, height, planes = decoded
    top = height - (y + h)          # the page buffer's row 0 is the PNG's last row
    if top < 0 or x + w > width:
        return None
    return [[plane[r][x:x + w] for r in range(top, top + h)] for plane in planes]


def sharpness(planes):
    total = count = 0
    for rows in planes:
        for r in range(len(rows)):
            row = rows[r]
            for c in range(len(row) - 1):
                total += abs(row[c + 1] - row[c])
                count += 1
            if r + 1 < len(rows):
                below = rows[r + 1]
                for c in range(len(row)):
                    total += abs(below[c] - row[c])
                    count += 1
    return total / count if count else 0.0


def i5_tiles(a, l, warnings, lines):
    """I5: every tile L captured is in A at least as sharp - matched by material key through the sidecars, one per
    material."""
    if a["index"] is None or l["index"] is None:
        lines.append("I5 tiles: skipped (a sidecar is missing)")
        return
    rects_a, rects_l = {}, {}
    for side, rects in ((a, rects_a), (l, rects_l)):
        for row, shape in zip(side["index"]["buildings"], side["mesh"]["shapes"]):
            for material_key, rect in zip(row["keys"], shape[3]):
                if rect[3] > cc.INDEX_FLAT_PIXELS:          # textured tiles only; flat ones are one colour
                    rects.setdefault(material_key, rect)
    common = [k for k in rects_l if k in rects_a]
    blurrier, compared, mad_sum = [], 0, 0.0
    for key in common:
        ta, tl = tile(a, *rects_a[key]), tile(l, *rects_l[key])
        if ta is None or tl is None or len(ta[0]) != len(tl[0]) or len(ta[0][0]) != len(tl[0][0]):
            continue
        compared += 1
        diff = n = 0
        for pa, pl in zip(ta, tl):
            for ra, rl in zip(pa, pl):
                for va, vl in zip(ra, rl):
                    diff += abs(va - vl)
                    n += 1
        mad_sum += diff / n if n else 0.0
        sa, sl = sharpness(ta), sharpness(tl)
        if sa < SHARPER_SHARE * sl:
            blurrier.append((key, sa, sl))
    missing = [k for k in rects_l if k not in rects_a]
    lines.append(f"I5 tiles: {len(rects_l):,} textured material(s) in L, {len(common):,} in A too, {compared:,} compared "
                 f"(mean MAD {mad_sum / compared if compared else 0:.2f}), {len(blurrier):,} blurrier in A, "
                 f"{len(missing):,} not textured in A")
    if blurrier:
        warnings.append(f"I5: {len(blurrier):,} tile(s) are blurrier in A than in L (first: " +
                        ", ".join(f"{k:016x} {sa:.1f} < {sl:.1f}" for k, sa, sl in blurrier[:4]) + ")")
    if missing:
        warnings.append(f"I5: {len(missing):,} material(s) L textured are not textured in A (the page cap, or a late tile)")


def main(argv):
    binding = "--binding" in argv
    args = [x for x in argv if x != "--binding"]
    if len(args) == 1:
        a, l = load(args[0]), load(args[0], verify=True)
    elif len(args) == 2:
        a, l = load(args[0]), load(args[1])
    else:
        print(__doc__)
        return 2

    errors, warnings, lines = [], [], []
    i1_buildings(a, l, errors, lines)
    i2_triangles(a, l, binding, errors, warnings, lines)
    i3_relief(a, l, errors, lines)
    i4_range(a, l, errors, lines)
    i5_tiles(a, l, warnings, lines)

    print(f"A: {a['name']} ({a['mesh']['buildings']:,} building(s), {a['mesh']['triangles']:,} triangles"
          f"{', sidecar' if a['index'] else ''})")
    print(f"L: {l['name']} ({l['mesh']['buildings']:,} building(s), {l['mesh']['triangles']:,} triangles"
          f"{', sidecar' if l['index'] else ''})")
    for line in lines:
        print(f"  {line}")
    for w in warnings:
        print(f"WARN   {w}")
    for e in errors:
        print(f"ERROR  {e}")
    print(f"{len(errors)} problem(s), {len(warnings)} warning(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
