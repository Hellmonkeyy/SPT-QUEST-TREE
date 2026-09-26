#!/usr/bin/env python3
"""Compares two snapshots of one capture folder pixel by pixel - the offline check of WP1's tile skip.

    python tools/compare-captures.py SNAP_A SNAP_B [--skipped "FILE:i,j,...;FILE2:k,..."]
                                                   [--outside "FILE:i,j,..."] [--side DIR i,j,...]

SNAP_A and SNAP_B are copies of a captures folder (or of one map's folder under it) taken before and after a
capture. Every picture a meta (<key>.map.json) names - floors and side views - is compared with its namesake
in the other snapshot, and so is its distance sidecar (<picture>.dist.png). Per file it prints whether the two
files are byte-identical, how many pixels differ (at SNAP_B's alpha above 0 and at 0), and how many sidecar
bytes differ (in all, and at alpha above 0).

The tile lists are the ones the capture's Debug line names ("tile plan: ... owned by closer captures [i,j],
... outside the walkable mask [k]"):
  --skipped FILE:i,j  tiles skipped as OwnedByCloser. Every pixel and every sidecar byte inside them must be
                      identical between the snapshots - the byte-identity WP1 claims. A difference fails.
  --outside FILE:k    tiles skipped as OutsideMask (only with TileSkipOutsideMask on). Pixels there must be
                      identical wherever SNAP_B's alpha is above 0.
  --side DIR i,j      --skipped for side view DIR's picture (<key>-side-<DIR>.png).
FILE is the picture's file name as the meta names it (e.g. customs-0.png). Several FILE:list entries are
separated by ';', and each option may be given more than once. Everything outside the listed tiles is
reported as a magnitude only (share of differing pixels, mean |dRGB|), since two renders of one stop are
never byte-identical.

The tile rects are MapCapture.TileRect exactly: the meta's tileSize is in SAMPLES, SupersampleFactor 2 of
them to an output pixel, tiles row-major from the top-left, the last column and row clipped. A side's tiles
are laid out over the camera's picture, which is the contract's mirrored, so its tile columns are mirrored
here: contract column = width - 1 - camera column.

Stdlib only: 8-bit RGB/RGBA non-interlaced PNGs, as Unity's EncodeToPNG writes them (IHDR, the IDAT stream
inflated, all five row filters including Paeth). Exit 0 when every listed tile holds, 1 when one does not,
2 on a usage error.
"""

import json
import struct
import sys
import zlib
from itertools import accumulate
from pathlib import Path

PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
SUPERSAMPLE = 2          # MapCapture.SupersampleFactor
DEFAULT_TILE = 2048      # MapCapture.TileSize, when a meta does not say


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else (b if pb <= pc else c)


_BYTE = (0xFF).__and__


def decode_png(path):
    """(width, height, channels, rows) with rows[0] the TOP row as bytes of width x channels, or raises
    ValueError for anything that is not an 8-bit non-interlaced RGB or RGBA PNG."""
    data = Path(path).read_bytes()
    if data[:8] != PNG_MAGIC:
        raise ValueError("not a PNG")
    at, width, height, depth, colour, interlace, idat = 8, 0, 0, 0, 0, 0, []
    while at + 8 <= len(data):
        length = struct.unpack(">I", data[at:at + 4])[0]
        kind = data[at + 4:at + 8]
        body = data[at + 8:at + 8 + length]
        if kind == b"IHDR":
            width, height, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", body[:13])
        elif kind == b"IDAT":
            idat.append(body)
        elif kind == b"IEND":
            break
        at += 12 + length
    if depth != 8 or colour not in (2, 6) or interlace != 0:
        raise ValueError(f"depth {depth}, colour type {colour}, interlace {interlace} - only 8-bit RGB/RGBA")
    bpp = 4 if colour == 6 else 3
    raw = zlib.decompress(b"".join(idat))
    stride = width * bpp
    if len(raw) < height * (stride + 1):
        raise ValueError("the image data is short")
    rows, prior = [], bytes(stride)
    for y in range(height):
        base = y * (stride + 1)
        kind = raw[base]
        row = raw[base + 1:base + 1 + stride]
        if kind == 0:
            pass
        elif kind == 1:                                   # Sub: a running sum per channel
            full = bytearray(stride)
            for c in range(bpp):
                full[c::bpp] = bytes(map(_BYTE, accumulate(row[c::bpp])))
            row = bytes(full)
        elif kind == 2:                                   # Up
            row = bytes(map(_BYTE, map(int.__add__, row, prior)))
        elif kind in (3, 4):                              # Average, Paeth
            out = bytearray(row)
            for i in range(stride):
                left = out[i - bpp] if i >= bpp else 0
                up = prior[i]
                if kind == 3:
                    out[i] = (out[i] + ((left + up) >> 1)) & 0xFF
                else:
                    corner = prior[i - bpp] if i >= bpp else 0
                    out[i] = (out[i] + _paeth(left, up, corner)) & 0xFF
            row = bytes(out)
        else:
            raise ValueError(f"row {y} has filter type {kind}")
        rows.append(row)
        prior = row
    return width, height, bpp, rows


def tile_rect(width, height, tile_samples, tile, side):
    """MapCapture.TileRect in PNG coordinates: (col0, cols, top, rows) with top counted from the picture's
    TOP row, contract columns for a side."""
    tiles_x = (width * SUPERSAMPLE + tile_samples - 1) // tile_samples
    px0 = tile % tiles_x * tile_samples
    py0 = tile // tiles_x * tile_samples
    tw = max(0, min(tile_samples, width * SUPERSAMPLE - px0))
    th = max(0, min(tile_samples, height * SUPERSAMPLE - py0))
    cols, rows = tw // SUPERSAMPLE, th // SUPERSAMPLE
    row0 = (height * SUPERSAMPLE - py0 - th) // SUPERSAMPLE      # texture row, 0 at the BOTTOM
    top = height - row0 - rows                                  # the same rows counted from the top
    col0 = px0 // SUPERSAMPLE
    if side:
        col0 = width - col0 - cols
    return col0, cols, top, rows


def tile_count(width, height, tile_samples):
    return (((width * SUPERSAMPLE + tile_samples - 1) // tile_samples) *
            ((height * SUPERSAMPLE + tile_samples - 1) // tile_samples))


def pictures(root):
    """{file name: (meta tileSize, is side, key)} for every floor and side every meta under root names."""
    out = {}
    for meta_path in sorted(Path(root).rglob("*.map.json")):
        try:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        tile = meta.get("tileSize") if isinstance(meta.get("tileSize"), int) and meta.get("tileSize") > 0 else DEFAULT_TILE
        key = meta_path.name[:-len(".map.json")]
        for floor in meta.get("floors") or []:
            if isinstance(floor, dict) and isinstance(floor.get("file"), str):
                out[floor["file"]] = (meta_path.parent, tile, False, key)
        for side in meta.get("sides") or []:
            if isinstance(side, dict) and isinstance(side.get("file"), str):
                out[side["file"]] = (meta_path.parent, tile, True, key)
    return out


def parse_lists(values, into):
    for value in values:
        for entry in value.split(";"):
            entry = entry.strip()
            if not entry:
                continue
            name, _, tiles = entry.rpartition(":")
            if not name:
                raise ValueError(f"{entry!r} is not FILE:i,j,...")
            into.setdefault(name, set()).update(int(t) for t in tiles.split(",") if t.strip())


def arguments(argv):
    positional, skipped, outside, sides = [], [], [], []
    k = 0
    while k < len(argv):
        a = argv[k]
        if a in ("--skipped", "--outside"):
            if k + 1 >= len(argv):
                raise ValueError(f"{a} needs FILE:i,j,... after it")
            (skipped if a == "--skipped" else outside).append(argv[k + 1])
            k += 2
        elif a == "--side":
            if k + 2 >= len(argv):
                raise ValueError("--side needs a direction and a tile list after it")
            sides.append((argv[k + 1], argv[k + 2]))
            k += 3
        elif a.startswith("--"):
            raise ValueError(f"unknown option {a}")
        else:
            positional.append(a)
            k += 1
    if len(positional) != 2:
        raise ValueError("two snapshot folders are needed")
    return positional, skipped, outside, sides


def compare_file(name, a_dir, b_dir, tile_samples, side, owned, outside):
    """The lines for one picture and its sidecar, and whether every listed tile held."""
    lines, ok = [], True
    a_path, b_path = a_dir / name, b_dir / name
    if not a_path.is_file() or not b_path.is_file():
        return [f"{name}: missing in {'SNAP_A' if not a_path.is_file() else 'SNAP_B'} - not compared"], not (owned or outside)

    same_bytes = a_path.read_bytes() == b_path.read_bytes()
    wa, ha, ca, ra = decode_png(a_path)
    wb, hb, cb, rb = decode_png(b_path)
    if (wa, ha) != (wb, hb):
        return [f"{name}: {wa}x{ha} in SNAP_A but {wb}x{hb} in SNAP_B - not the same picture"], not (owned or outside)
    width, height = wa, ha

    dist_name = name[:-4] + ".dist.png" if name.lower().endswith(".png") else name + ".dist.png"
    da = db = None
    if (a_dir / dist_name).is_file() and (b_dir / dist_name).is_file():
        _, _, dca, da = decode_png(a_dir / dist_name)
        _, _, dcb, db = decode_png(b_dir / dist_name)
        dist_same_bytes = (a_dir / dist_name).read_bytes() == (b_dir / dist_name).read_bytes()
    else:
        dca = dcb = 3
        dist_same_bytes = None

    # Which listed tile each pixel is in: 1 owned, 2 outside, 0 none.
    zone = [bytearray(width) for _ in range(height)]
    for mark, tiles in ((1, owned), (2, outside)):
        for t in sorted(tiles):
            if t < 0 or t >= tile_count(width, height, tile_samples):
                lines.append(f"  tile {t} is not a tile of this {width}x{height} picture - ignored")
                ok = False
                continue
            c0, n, top, m = tile_rect(width, height, tile_samples, t, side)
            for y in range(top, top + m):
                zone[y][c0:c0 + n] = bytes([mark]) * n

    diff = diff_alpha = diff_clear = 0
    dist_diff = dist_diff_alpha = 0
    owned_px = owned_dist = outside_visible = 0
    else_px = else_total = 0
    else_sum = 0
    for y in range(height):
        row_a, row_b = ra[y], rb[y]
        dra = da[y] if da is not None else None
        drb = db[y] if db is not None else None
        zr = zone[y]
        if row_a == row_b and (dra is None or dra == drb):
            else_total += width - (zr.count(1) + zr.count(2))
            continue
        for x in range(width):
            pa = row_a[x * ca:x * ca + 3]
            pb = row_b[x * cb:x * cb + 3]
            alpha_a = row_a[x * ca + 3] if ca == 4 else 255
            alpha_b = row_b[x * cb + 3] if cb == 4 else 255
            pixel_differs = pa != pb or alpha_a != alpha_b
            dist_differs = dra is not None and dra[x * dca] != drb[x * dcb]
            z = zr[x]
            if z == 0:
                else_total += 1
            if pixel_differs:
                diff += 1
                if alpha_b > 0:
                    diff_alpha += 1
                else:
                    diff_clear += 1
                if z == 1:
                    owned_px += 1
                elif z == 2 and alpha_b > 0:
                    outside_visible += 1
                elif z == 0:
                    else_px += 1
                    else_sum += sum(abs(pa[c] - pb[c]) for c in range(3))
            if dist_differs:
                dist_diff += 1
                if alpha_b > 0:
                    dist_diff_alpha += 1
                if z == 1:
                    owned_dist += 1

    lines.append(
        f"{name}: byte-identical {'yes' if same_bytes else 'no'}; pixels differing {diff:,} of {width * height:,} "
        f"(alpha>0: {diff_alpha:,}, alpha==0: {diff_clear:,}); sidecar "
        + ("absent in one snapshot" if dist_same_bytes is None else
           f"byte-identical {'yes' if dist_same_bytes else 'no'}, bytes differing {dist_diff:,} "
           f"(at alpha>0: {dist_diff_alpha:,})"))
    if owned:
        held = owned_px == 0 and owned_dist == 0
        ok = ok and held
        lines.append(f"  skipped (owned) tiles [{','.join(map(str, sorted(owned)))}]{' mirrored' if side else ''}: "
                     f"pixels differing {owned_px:,}, sidecar bytes differing {owned_dist:,} - "
                     f"{'IDENTICAL' if held else 'DIFFER - FAIL'}")
    if outside:
        held = outside_visible == 0
        ok = ok and held
        lines.append(f"  skipped (outside the mask) tiles [{','.join(map(str, sorted(outside)))}]: pixels differing "
                     f"where SNAP_B's alpha > 0: {outside_visible:,} - {'IDENTICAL' if held else 'DIFFER - FAIL'}")
    share = 100.0 * else_px / else_total if else_total else 0.0
    mean = else_sum / (3.0 * else_px) if else_px else 0.0
    lines.append(f"  outside the listed tiles: {else_px:,} of {else_total:,} pixels differ ({share:.2f} %), "
                 f"mean |dRGB| {mean:.2f}")
    return lines, ok


def main(argv):
    try:
        (a_root, b_root), skipped_args, outside_args, side_args = arguments(argv)
        owned, outside = {}, {}
        parse_lists(skipped_args, owned)
        parse_lists(outside_args, outside)
    except ValueError as exc:
        print(f"compare-captures: {exc}\n")
        print(__doc__)
        return 2

    a_root, b_root = Path(a_root), Path(b_root)
    if not a_root.is_dir() or not b_root.is_dir():
        print(f"compare-captures: {a_root if not a_root.is_dir() else b_root} is not a folder")
        return 2

    known = pictures(b_root) or pictures(a_root)
    for direction, tiles in side_args:
        names = [n for n, (_, _, is_side, key) in known.items() if is_side and n == f"{key}-side-{direction}.png"]
        if not names:
            print(f"compare-captures: no side view {direction} in the snapshots' metas")
            return 2
        for n in names:
            owned.setdefault(n, set()).update(int(t) for t in tiles.split(",") if t.strip())

    for name in list(owned) + list(outside):
        if name not in known:
            print(f"compare-captures: {name} is not a picture any meta in {b_root} names")
            return 2

    all_ok = True
    print(f"SNAP_A {a_root}\nSNAP_B {b_root}\n")
    for name in sorted(known):
        folder, tile_samples, side, _ = known[name]
        rel = folder.relative_to(b_root) if folder.is_relative_to(b_root) else folder.relative_to(a_root)
        try:
            lines, ok = compare_file(name, a_root / rel, b_root / rel, tile_samples, side,
                                     owned.get(name, set()), outside.get(name, set()))
        except (OSError, ValueError, zlib.error) as exc:
            lines, ok = [f"{name}: could not be compared ({exc})"], not (owned.get(name) or outside.get(name))
        all_ok = all_ok and ok
        for line in lines:
            print(line)
    print()
    listed = sum(len(v) for v in owned.values()) + sum(len(v) for v in outside.values())
    print(f"{len(known)} picture(s) compared, {listed} listed tile(s): "
          f"{'every listed tile identical' if all_ok else 'A LISTED TILE DIFFERS'}")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
