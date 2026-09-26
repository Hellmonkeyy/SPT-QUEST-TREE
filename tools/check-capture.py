#!/usr/bin/env python3
"""Checks an in-raid map capture against its own meta and against the server's zone file.

A capture is three things that have to agree and are written by two different processes in two
different places: the PNG pixels the client renders in-raid, the <key>.map.json beside them that says
how big those pixels are and what world rectangle they cover, and the extent the zone harvester
measured and the server stored under zones\\<key>.json. Nothing downstream notices when they do not
agree - the UI simply scales the picture to whatever rectangle the meta claims, so an extent that is
three metres off, or a PNG one row short of its meta, draws every pin slightly wrong and looks like a
picture, not like an error. That is the failure this script exists to catch before a capture is
shipped or seeded.

What it checks, per capture folder <key>/:
  1. <key>.map.json parses, is schemaVersion 1, and its "map" is the folder name (case-insensitive).
  2. every floor's PNG exists; the IHDR width/height read out of the file's first chunk equal the
     meta's width/height; those in turn equal ceil(extent span * pxPerMetre) on each axis within 1
     px; the file is under 48 MB; floor levels are distinct and names non-empty.
  3. if zones\\<key>.json carries a v2 extent, the capture's four edges match it within 0.5 m and the
     floor LEVELS are the same set, and the zone file's source/sampledAt are printed beside the
     capture's capturedAt. A v1 zone file (every shipped seed) or a missing one is a WARN, not a
     failure: it means this capture cannot be cross-checked yet, which is the normal state until the
     map has been raided with a v2 client.
  4. the 3D mesh, when the meta carries a "mesh" block (it is OPTIONAL - a capture taken before the
     feature existed, or one whose mesh phase failed, has none and is perfectly valid): the file
     exists and is exactly meta.mesh.bytes long, its SHA-256 is meta.mesh.sha256, it inflates as one
     raw-deflate block with nothing before or after it, its magic is QTM1 and its version is
     meta.mesh.version, its extent equals the meta's to the bit, its band levels are the meta's floor
     levels, every band is ceil(span / cellMetres) cells on each axis, the cell and triangle totals
     are meta.mesh.cells and meta.mesh.triangles, and every triangle index is inside its own
     building's vertex count. The file's y range - which every height in it is quantised over - is a
     WARN over 500 m and an ERROR over 2000 m. A <key>-mesh.bin on disk that the meta does not name is
     a WARN - it is what an older capture leaves when a later one builds no mesh, and nothing reads it.
  5. the side views (plan, stage U), when the meta lists "sides" (OPTIONAL like the mesh): each dir is
     N/S/E/W and appears once, its file is <key>-side-<dir>.png and exists with the IHDR size the
     meta gives, pxPerMetre is in (0, 2], forward/right/up are unit length and orthogonal (1e-3),
     forward is the contract's vector for that dir, right is normalize(cross(forward, world up)) and
     up is cross(right, forward),
     originR/originU are the minima of dot(right,.)/dot(up,.) over the 8 corners of
     extent x [yMin, yMax] (1e-3), and width/height are ceil(span * pxPerMetre) within 1 px. Its
     distance sidecar <key>-side-<dir>.dist.png, when present, is the side's size (absent is a WARN:
     the next capture cannot merge into that side). A <key>-side-*.png the meta does not list is a
     WARN.

What it does NOT check, by design:
  - the pixels. Whether the PNG is the right map, drawn the right way up, or blank, is exactly what
    the eye is for; this only proves its dimensions are the ones the meta and the extent imply.
  - rotation beyond reporting a disagreement as a WARN (both sides are 0 in this release), tileSize,
    labels, timeOfDay, modVersion, and whether the client can actually load the file.
  - the extent itself. If the harvest measured a NavMesh box reaching under the map, capture, meta
    and zone file will all agree on the wrong rectangle; the source column is there to make that
    visible to a reader.

Proven able to fail before it shipped: against generated fakes it exits 1 naming the map and the
field for a PNG one row short of its meta, for an extent 3 m from the zone file's, and for a
schemaVersion 2 meta, and exits 0 on the valid one; see the report in the commit that added it.

The mesh gate was proven the same way, against eleven synthetic captures written from Python to
MapMeshFile's byte table: it exits 0 on the valid one and on a mesh file the meta does not name (with
the WARN), and exits 1 naming the fault for a truncated deflate stream, a meta sha256 that is not the
file's, a mesh extent 3 m from the meta's, a band level no floor has, a band that is not
ceil(span / cell) cells, a triangle index past its building's vertices, a meta cell count that is not
the file's, a meta byte count that is not the file's, and a meta naming a mesh that is not there.
The side-view gate likewise, against side entries computed by the shipped C# MapSideView: exit 0 on
those, exit 1 for an originR 0.5 m off, a non-orthogonal basis, a PNG one row taller than its meta, a
missing side file, N carrying S's forward vector, an up vector negated (unit and orthogonal, but
upside down), and a size that is not ceil(span * ppm).

WP7 (dynamic budgets): the format's hard bounds are 40 M triangles / 80 M vertices, a building is at most
1 M triangles (the host's rule), a file over the builder's absolute 20 M triangles or over 512 MiB is an
ERROR (no build writes it, no host takes it), the inflate bound is computed from the meta's declared
cells and triangles (the host's D14 rule, capped at 1 GiB), and every band's cell must be
relief_cell_for(extent) - the builder's ReliefCellFor - except a 2 m band where the rule gives 1 m,
which is a WARN "captured before WP7 at 2 m". With --compare OLD_ROOT, each map's buildings are matched
by key against the same map's mesh under OLD_ROOT, and a key OLD has that NEW lacks, or a key NEW
stores with fewer triangles, is an ERROR (WP7's Q1/Q2: no building loses detail).

Usage:  python tools/check-capture.py [captures-root] [zones-folder] [--compare OLD_ROOT]
        defaults: C:\\Games\\SPT\\BepInEx\\plugins\\QuestTree\\captures
                  C:\\Games\\SPT\\SPT_Runtime\\user\\mods\\QuestTree\\zones
"""

import hashlib
import json
import math
import struct
import sys
import zlib
from pathlib import Path

def _arguments(argv):
    """(positional arguments, --compare root or None). Kept positional for the two roots every caller
    already passes; --compare OLD_ROOT may stand anywhere."""
    positional, compare, k = [], None, 0
    while k < len(argv):
        if argv[k] == "--compare":
            if k + 1 >= len(argv):
                print("CAPTURE CHECK FAILED: --compare needs the OLD captures root after it")
                sys.exit(1)
            compare = Path(argv[k + 1])
            k += 2
            continue
        positional.append(argv[k])
        k += 1
    return positional, compare


_POSITIONAL, COMPARE = _arguments(sys.argv[1:])
CAPTURES = Path(_POSITIONAL[0]) if len(_POSITIONAL) > 0 else Path(
    r"C:\Games\SPT\BepInEx\plugins\QuestTree\captures")
ZONES = Path(_POSITIONAL[1]) if len(_POSITIONAL) > 1 else Path(
    r"C:\Games\SPT\SPT_Runtime\user\mods\QuestTree\zones")

SCHEMA_VERSION = 1     # the capture-meta shape this script reads
MAX_PNG_BYTES = 48 * 1024 * 1024  # MapCapture.MaxFloorPngBytes: 0.25 m/px floors run 10-25 MB
PIXEL_TOLERANCE = 1     # px, on each axis, against ceil(span * pxPerMetre)
EDGE_TOLERANCE = 0.5    # m, on each of the four edges, against the zone file's extent
SIDECAR_STALE_SECONDS = 15  # s: a picture and its distance sidecar are staged one frame apart (review F09)
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"

# The mesh file's format, from Source\Tarkov-QuestTree\QuestGraph\MapMeshFile.cs - that class's
# Write() doc comment is the byte table these constants and read_mesh() below follow. The caps are
# ITS caps: a file this script accepts and the client refuses would be a check that cannot fail.
MESH_MAGIC = b"QTM1"
MESH_VERSION = 3            # MapMeshFile.Version (stage W: 2 added the atlas; stage X: 3 wraps the tiles)
MESH_NO_HIT = 0xFFFF        # MapMeshFile.NoHit
MESH_MAX_BANDS = 8
MESH_MAX_CELLS_PER_BAND = 4_000_000
MESH_MAX_BUILDINGS = 20_000
MESH_MAX_VERTICES_PER_BUILDING = 2_000_000
MESH_MAX_VERTICES_TOTAL = 80_000_000   # WP7: was 12 M (stage V: 4 M) - a hard bound, 2 x the triangle bound
MESH_MAX_TRIANGLES = 40_000_000        # WP7: was 6 M - a hard bound, 2 x the builder's absolute
MESH_BUILDER_ABSOLUTE = 20_000_000     # MapMeshBuilder.BuilderAbsoluteTriangles: no build stores more
MESH_MAX_TRIANGLES_PER_BUILDING = 1_000_000  # MapMeshFile.MaxTrianglesPerBuilding, the host's per-building rule
MESH_MAX_FILE_BYTES = 512 * 1024 * 1024      # the protocol absolute (MapStore.MeshAbsolute): no host takes more
MESH_MAX_ATLAS_PAGES = 8               # MapMeshFile.MaxAtlasPages
MESH_MAX_RANGES_PER_BUILDING = 64      # MapMeshFile.MaxRangesPerBuilding
ATLAS_PAGE_SIZE = 4096                 # MapMeshFile.AtlasPageSize
ATLAS_TILE_MAX = 256                   # MapMeshFile.AtlasTileMax
ATLAS_TILE_ALIGN = 4                   # MapMeshFile.TileAlign (the viewer's DXT1 blocks)
# What the file may inflate to: the host's D14 rule from the meta's DECLARED counts - 3 B a cell, 42 B a
# triangle (12 of indices, at most 3 vertices x 10 B), 2,328 B a building slot (counts and 64 ranges) for
# up to 20,000 of them, plus 64 of header - capped at 1 GiB. The bound exists so a corrupt or hostile
# deflate stream cannot be expanded until this process dies; a file that under-declares is refused by
# the cell and triangle checks against the meta.
MESH_MAX_INFLATED_BYTES = 1 << 30


def inflate_bound(cells, triangles):
    """The host's D14 inflate bound (MapStore) from a meta's declared cells and triangles."""
    return min(MESH_MAX_INFLATED_BYTES, 64 + 3 * cells + 42 * triangles + 20_000 * 2_328)


# The relief cell rule, identical to MapMeshBuilder.ReliefCellFor: 1 m wherever the extent fits
# 4,000,000 cells a band at it, coarser by half a metre at a time only when it does not.
RELIEF_PREFERRED_CELL = 1.0
RELIEF_CELL_STEP = 0.5
RELIEF_PRE_WP7_CELL = 2.0


def relief_cell_for(span_x, span_z):
    """The cell MapMeshBuilder.ReliefCellFor derives for an extent of span_x by span_z metres."""
    if not (span_x > 0 and span_z > 0) or math.isinf(span_x) or math.isinf(span_z):
        return RELIEF_PREFERRED_CELL
    area = span_x * span_z
    c = max(RELIEF_PREFERRED_CELL,
            math.ceil(math.sqrt(area / MESH_MAX_CELLS_PER_BAND) / RELIEF_CELL_STEP) * RELIEF_CELL_STEP)
    while math.ceil(span_x / c) * math.ceil(span_z / c) > MESH_MAX_CELLS_PER_BAND:
        c += RELIEF_CELL_STEP
    return c
MESH_SUFFIX = "-mesh.bin"   # MapMeshFile.FileNameFor
# The file's y range, which every height and vertex in it is quantised over. MapMeshBuilder takes it
# from the ground's ray hits and lets buildings widen it by at most 100 m each way (YRangeMarginMetres),
# so a real map spans a few hundred metres at most - Customs is -22..74 with the 5 m slack. A span of
# 3.4e38 is what EFT's rain volumes did to the first build: every height the same code, a flat sheet
# that passes every other check here. Over the warning figure is a map worth looking at; over the error
# figure the heights are sixteen-bit steps of more than three centimetres and something is wrong.
MESH_WARN_Y_SPAN = 500.0
MESH_MAX_Y_SPAN = 2000.0


class MeshError(Exception):
    """A mesh file that cannot be trusted, with the reason in its message. One exception type, like
    the client's InvalidDataException, so every failure reads the same way."""


def fail_hard(message):
    print(f"CAPTURE CHECK FAILED: {message}")
    sys.exit(1)


def png_size(path):
    """(width, height) from the PNG's IHDR, or a raise-free (None, reason). Reads the header only:
    the 8-byte signature, then the first chunk, which the format REQUIRES to be IHDR - so a file
    whose first chunk is anything else is not a PNG this can trust, however well it opens."""
    with open(path, "rb") as fh:
        head = fh.read(33)
    if len(head) < 33:
        return None, f"{len(head)} bytes long - too short to hold a PNG header"
    if head[:8] != PNG_MAGIC:
        return None, "does not start with the PNG signature"
    length, kind = struct.unpack(">I4s", head[8:16])
    if kind != b"IHDR":
        return None, f"first chunk is {kind.decode('latin-1')!r}, not IHDR"
    if length != 13:
        return None, f"IHDR is {length} bytes, not 13"
    width, height = struct.unpack(">II", head[16:24])
    if width == 0 or height == 0:
        return None, f"IHDR says {width}x{height}"
    return (width, height), None


def load_json(path):
    """(object, None) or (None, reason). utf-8-sig: the client writes these files on Windows."""
    try:
        return json.loads(path.read_text(encoding="utf-8-sig")), None
    except OSError as exc:
        return None, f"cannot be read ({exc.strerror or exc})"
    except ValueError as exc:
        return None, f"is not valid JSON ({exc})"


def number(value):
    """A JSON number as a float, or None if the field is absent or not a number. Rejects bool, which
    is an int in Python and would otherwise read as a perfectly good 0 or 1."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return float(value)


def zone_for(key):
    """The zone file whose stem matches this capture key, ignoring case: ZoneStore names files from
    ZoneFile.Map, so the disk carries whatever casing the client sent (Bigmap.json for bigmap)."""
    if not ZONES.is_dir():
        return None
    for path in sorted(ZONES.glob("*.json")):
        if path.stem.lower() == key.lower():
            return path
    return None


def check_extent(meta, errors, key):
    """The meta's extent as (minX, minZ, maxX, maxZ), or None with the errors recorded."""
    extent = meta.get("extent")
    if not isinstance(extent, dict):
        errors.append(f"{key}: extent is missing or not an object")
        return None
    edges = {}
    for field in ("minX", "minZ", "maxX", "maxZ"):
        edges[field] = number(extent.get(field))
        if edges[field] is None:
            errors.append(f"{key}: extent.{field} is missing or not a number")
    if any(v is None for v in edges.values()):
        return None
    if edges["maxX"] <= edges["minX"]:
        errors.append(f"{key}: extent.maxX {edges['maxX']:g} is not greater than extent.minX "
                      f"{edges['minX']:g} - the rectangle has no width")
        return None
    if edges["maxZ"] <= edges["minZ"]:
        errors.append(f"{key}: extent.maxZ {edges['maxZ']:g} is not greater than extent.minZ "
                      f"{edges['minZ']:g} - the rectangle has no depth")
        return None
    return edges["minX"], edges["minZ"], edges["maxX"], edges["maxZ"]


def check_floors(meta, folder, key, extent, px_per_metre, errors, warnings):
    """Validates every floor against its PNG and the extent. Returns (levels, pixels, bytes):
    the set of floor levels, the first floor's "WxH" for the summary line, total PNG bytes."""
    floors = meta.get("floors")
    if not isinstance(floors, list) or not floors:
        errors.append(f"{key}: floors is missing, not a list, or empty - a capture with no floor "
                      f"has no picture")
        return set(), "-", 0

    levels, seen_levels, pixels, total = set(), [], None, 0
    for index, floor in enumerate(floors):
        where = f"{key}: floors[{index}]"
        if not isinstance(floor, dict):
            errors.append(f"{where} is not an object")
            continue

        level = floor.get("level")
        if isinstance(level, bool) or not isinstance(level, int):
            errors.append(f"{where}.level is missing or not an integer")
        else:
            if level in seen_levels:
                errors.append(f"{where}.level {level} is a DUPLICATE - two floors claim the same "
                              f"height band, so a pin's floor is decided by whichever is read last")
            seen_levels.append(level)
            levels.add(level)
            where = f"{key}: floor {level}"

        name = floor.get("name")
        if not isinstance(name, str) or not name.strip():
            errors.append(f"{where}.name is missing or empty - nothing to show in the floor picker")

        meta_w, meta_h = floor.get("width"), floor.get("height")
        for field, value in (("width", meta_w), ("height", meta_h)):
            if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
                errors.append(f"{where}.{field} is missing or not a positive integer")
                meta_w = meta_h = None

        min_y, max_y = number(floor.get("minY")), number(floor.get("maxY"))
        if min_y is None or max_y is None:
            errors.append(f"{where}: minY/maxY are missing or not numbers")
        elif max_y <= min_y:
            errors.append(f"{where}: maxY {max_y:g} is not above minY {min_y:g} - the band is empty, "
                          f"so no world Y falls on this floor")

        # The PNG. Its name is a file BESIDE the meta; an absolute path or one that climbs out of the
        # capture folder is refused rather than followed.
        rel = floor.get("file")
        if not isinstance(rel, str) or not rel.strip():
            errors.append(f"{where}.file is missing or empty")
            continue
        parts = Path(rel.replace("\\", "/"))
        if parts.is_absolute() or ".." in parts.parts:
            errors.append(f"{where}.file {rel!r} is not a path inside the capture folder")
            continue
        png = folder / parts
        if not png.is_file():
            errors.append(f"{where}.file {rel!r} does not exist in {folder}")
            continue

        size = png.stat().st_size
        total += size
        if size > MAX_PNG_BYTES:
            errors.append(f"{where}: {rel} is {size / 1048576:.1f} MB, over the "
                          f"{MAX_PNG_BYTES // 1048576} MB cap")

        actual, why = png_size(png)
        if actual is None:
            errors.append(f"{where}: {rel} {why}")
            continue
        png_w, png_h = actual
        if pixels is None:
            pixels = f"{png_w}x{png_h}"

        if meta_w is not None and (png_w, png_h) != (meta_w, meta_h):
            if png_w != meta_w:
                errors.append(f"{where}.width {meta_w} does not match {rel}'s IHDR width {png_w}")
            if png_h != meta_h:
                errors.append(f"{where}.height {meta_h} does not match {rel}'s IHDR height {png_h}")

        if extent is not None and px_per_metre:
            min_x, min_z, max_x, max_z = extent
            want_w = math.ceil((max_x - min_x) * px_per_metre)
            want_h = math.ceil((max_z - min_z) * px_per_metre)
            if abs(png_w - want_w) > PIXEL_TOLERANCE:
                errors.append(f"{where}: {rel} is {png_w} px wide but the extent's "
                              f"{max_x - min_x:g} m at {px_per_metre:g} px/m wants {want_w}")
            if abs(png_h - want_h) > PIXEL_TOLERANCE:
                errors.append(f"{where}: {rel} is {png_h} px high but the extent's "
                              f"{max_z - min_z:g} m at {px_per_metre:g} px/m wants {want_h}")

        # The floor's distance sidecar, when there is one, has to be from the same capture as its picture
        # (review F09): a sidecar the capture could not write is deleted at commit, never left behind.
        if isinstance(level, int) and not isinstance(level, bool):
            dist_name = f"{key}-{level}.dist.png"
            dist_path = folder / dist_name
            if dist_path.is_file() and dist_path.stat().st_mtime < png.stat().st_mtime - SIDECAR_STALE_SECONDS:
                warnings.append(f"{where}: {dist_name} is {png.stat().st_mtime - dist_path.stat().st_mtime:.0f} s "
                                f"older than its picture - a sidecar from an earlier capture (review F09)")

    return levels, pixels or "-", total


def check_zone(key, extent, levels, errors, warnings):
    """Cross-checks the capture against the server's zone file. Returns (column, rotation): the
    summary line's zones column - what matched, or why nothing could be compared - and the zone
    file's own rotation when there was one to read."""
    path = zone_for(key)
    if path is None:
        warnings.append(f"{key}: no zone file in {ZONES} - the capture's extent cannot be "
                        f"cross-checked until this map is harvested")
        return "no zone file", None

    zone, why = load_json(path)
    if zone is None:
        errors.append(f"{key}: zone file {path.name} {why}")
        return f"{path.name} unreadable", None

    zextent = zone.get("extent")
    if not isinstance(zextent, dict):
        warnings.append(f"{key}: {path.name} has no extent (schema version "
                        f"{zone.get('schemaVersion', 1)}) - the capture cannot be cross-checked "
                        f"until this map is harvested by a v2 client")
        return "zone extent absent", None

    source = zextent.get("source") or "?"
    sampled = zextent.get("sampledAt") or "?"
    tag = f"({source}, {sampled})"

    ok = True
    if extent is not None:
        for index, field in enumerate(("minX", "minZ", "maxX", "maxZ")):
            theirs = number(zextent.get(field))
            if theirs is None:
                errors.append(f"{key}: {path.name}'s extent.{field} is missing or not a number")
                ok = False
                continue
            drift = abs(extent[index] - theirs)
            if drift > EDGE_TOLERANCE:
                ok = False
                errors.append(f"{key}: extent.{field} {extent[index]:g} is {drift:.2f} m from the "
                              f"zone file's {theirs:g} (tolerance {EDGE_TOLERANCE} m) - the picture "
                              f"and the world disagree about where this map's edge is")
    else:
        ok = False  # already reported by check_extent

    zfloors = zextent.get("floors")
    if not isinstance(zfloors, list):
        errors.append(f"{key}: {path.name}'s extent.floors is missing or not a list")
        ok = False
    else:
        zlevels = {f.get("level") for f in zfloors
                   if isinstance(f, dict) and isinstance(f.get("level"), int)
                   and not isinstance(f.get("level"), bool)}
        if zlevels != levels:
            ok = False
            missing = sorted(zlevels - levels)
            extra = sorted(levels - zlevels)
            detail = []
            if missing:
                detail.append(f"the zone file has {missing} the capture does not")
            if extra:
                detail.append(f"the capture has {extra} the zone file does not")
            errors.append(f"{key}: floors levels {sorted(levels)} do not match the zone file's "
                          f"{sorted(zlevels)} - " + "; ".join(detail))

    column = ("extent matches zones " if ok else "extent DISAGREES with zones ") + tag
    return column, number(zextent.get("rotation"))


def inflate_mesh(data, bound=MESH_MAX_INFLATED_BYTES):
    """The mesh file's bytes inflated, or a MeshError. The whole file is ONE raw-deflate block with
    the magic inside it (MapMeshFile.Write), so there is no header to read without inflating and
    zlib.decompressobj(-15) is the only way in.

    A truncated file is the case this function exists for, and raw deflate carries no checksum for it
    to fail: zlib simply hands back the bytes it managed and leaves eof False. So eof is checked, and
    a stream that never ended is a truncation whatever its contents looked like."""
    un = zlib.decompressobj(-15)
    try:
        out = un.decompress(data, bound + 1)
    except zlib.error as exc:
        raise MeshError(f"is not a deflate stream ({exc})")
    if len(out) > bound:
        raise MeshError(f"inflates to more than {bound} bytes - the bound its meta's declared cells and "
                        f"triangles allow")
    if not un.eof:
        raise MeshError(f"is a TRUNCATED deflate stream - it inflated {len(out)} byte(s) and then "
                        f"ran out mid-block")
    if un.unused_data:
        raise MeshError(f"carries {len(un.unused_data)} byte(s) after the end of its deflate stream")
    return out


class MeshCursor:
    """Reads fixed-size little-endian fields out of the inflated bytes, and refuses to run past the
    end - so a file that stops inside a grid says where it stopped instead of handing back a short
    array that would read as flat ground."""

    def __init__(self, data):
        self.data = data
        self.at = 0

    def take(self, count, what):
        if self.at + count > len(self.data):
            raise MeshError(f"ends inside {what}: {len(self.data) - self.at} of {count} byte(s) left")
        chunk = self.data[self.at:self.at + count]
        self.at += count
        return chunk

    def i32(self, what):
        return struct.unpack("<i", self.take(4, what))[0]

    def f32(self, what):
        return struct.unpack("<f", self.take(4, what))[0]

    def f64(self, what):
        return struct.unpack("<d", self.take(8, what))[0]

    def u16s(self, count, what):
        return struct.unpack(f"<{count}H", self.take(2 * count, what)) if count else ()

    def u32s(self, count, what):
        return struct.unpack(f"<{count}I", self.take(4 * count, what)) if count else ()


def read_mesh(data, bound=MESH_MAX_INFLATED_BYTES):
    """The mesh file as a dict, or a MeshError naming the first thing that is wrong. Follows
    MapMeshFile.Write's byte table exactly, and checks every count against the format's own cap
    BEFORE it reads the array behind it. "keys" maps each building key to its triangles (summed if a
    key repeats), for --compare."""
    body = inflate_mesh(data, bound)
    cur = MeshCursor(body)

    magic = cur.take(4, "the magic")
    if magic != MESH_MAGIC:
        raise MeshError(f"starts {magic!r}, not {MESH_MAGIC!r} - it is not a QuestTree mesh file")

    version = cur.i32("the version")

    mesh = {
        "version": version,
        "minX": cur.f64("the extent"),
        "minZ": cur.f64("the extent"),
        "maxX": cur.f64("the extent"),
        "maxZ": cur.f64("the extent"),
        "yMin": cur.f32("the y range"),
        "yMax": cur.f32("the y range"),
        "atlasPages": cur.i32("the atlas page count"),
        "bands": [],
        "textured": 0,
        "ranges": 0,
        "buildings": 0,
        "cells": 0,
        "triangles": 0,
        "maxBuilding": 0,
        "keys": {},
    }

    if version != MESH_VERSION:
        raise MeshError(f"is version {version}; this script reads version {MESH_VERSION}")
    if not mesh["maxX"] > mesh["minX"] or not mesh["maxZ"] > mesh["minZ"]:
        raise MeshError(f"has an empty or inverted extent: x {mesh['minX']:g}..{mesh['maxX']:g}, "
                        f"z {mesh['minZ']:g}..{mesh['maxZ']:g}")
    if not mesh["yMax"] > mesh["yMin"]:
        raise MeshError(f"has an empty y range: {mesh['yMin']:g}..{mesh['yMax']:g}")
    if not 0 <= mesh["atlasPages"] <= MESH_MAX_ATLAS_PAGES:
        raise MeshError(f"claims {mesh['atlasPages']} atlas pages; the cap is {MESH_MAX_ATLAS_PAGES}")

    bands = cur.i32("the band count")
    if bands < 0 or bands > MESH_MAX_BANDS:
        raise MeshError(f"claims {bands} bands; the cap is {MESH_MAX_BANDS}")

    for index in range(bands):
        where = f"band {index}"
        level = cur.i32(f"{where}'s level")
        cell = cur.f32(f"{where}'s cell size")
        width = cur.i32(f"{where}'s width")
        height = cur.i32(f"{where}'s height")

        if width <= 0 or height <= 0:
            raise MeshError(f"{where} (level {level}) claims {width}x{height} cells")
        if not cell > 0:
            raise MeshError(f"{where} (level {level}) claims a cell size of {cell:g} m")
        if width * height > MESH_MAX_CELLS_PER_BAND:
            raise MeshError(f"{where} (level {level}) claims {width * height} cells; the cap is "
                            f"{MESH_MAX_CELLS_PER_BAND}")
        if any(band["level"] == level for band in mesh["bands"]):
            raise MeshError(f"has two level {level} bands - a level names a band")

        cells = width * height
        heights = cur.u16s(cells, f"{where}'s heights")
        distance = cur.take(cells, f"{where}'s distances")

        mesh["bands"].append({
            "level": level,
            "cell": cell,
            "width": width,
            "height": height,
            "hit": sum(1 for code in heights if code != MESH_NO_HIT),
            "distance": len(distance),
        })
        mesh["cells"] += cells

    buildings = cur.i32("the building count")
    if buildings < 0 or buildings > MESH_MAX_BUILDINGS:
        raise MeshError(f"claims {buildings} buildings; the cap is {MESH_MAX_BUILDINGS}")
    mesh["buildings"] = buildings

    vertices_total = 0
    levels = {band["level"] for band in mesh["bands"]}

    for index in range(buildings):
        where = f"building {index}"
        key = cur.i32(f"{where}'s key")
        level = cur.i32(f"{where}'s level")
        count = cur.i32(f"{where}'s vertex count")

        if count < 0 or count > MESH_MAX_VERTICES_PER_BUILDING:
            raise MeshError(f"{where} (key {key}) claims {count} vertices; the cap is "
                            f"{MESH_MAX_VERTICES_PER_BUILDING}")
        vertices_total += count
        if vertices_total > MESH_MAX_VERTICES_TOTAL:
            raise MeshError(f"claims {vertices_total} vertices by {where}; the cap is "
                            f"{MESH_MAX_VERTICES_TOTAL}")

        cur.take(2 * count, f"{where}'s x")
        ys = cur.u16s(count, f"{where}'s y")
        cur.take(2 * count, f"{where}'s z")

        indices = cur.i32(f"{where}'s index count")
        if indices < 0 or indices % 3 != 0:
            raise MeshError(f"{where} (key {key}) claims {indices} indices, which is not a "
                            f"non-negative multiple of 3")
        if indices // 3 > MESH_MAX_TRIANGLES_PER_BUILDING:
            raise MeshError(f"{where} (key {key}) claims {indices // 3} triangles; the cap is "
                            f"{MESH_MAX_TRIANGLES_PER_BUILDING} a building (the host refuses it)")
        mesh["triangles"] += indices // 3
        mesh["maxBuilding"] = max(mesh["maxBuilding"], indices // 3)
        mesh["keys"][key] = mesh["keys"].get(key, 0) + indices // 3
        if mesh["triangles"] > MESH_MAX_TRIANGLES:
            raise MeshError(f"claims {mesh['triangles']} triangles by {where}; the cap is "
                            f"{MESH_MAX_TRIANGLES}")

        # The index check, per building and against ITS vertex count: an index past it is what would
        # throw from inside the viewer's SetTriangles, where nothing could say which building it was.
        index_values = cur.u32s(indices, f"{where}'s indices")
        for position, value in enumerate(index_values):
            if value >= count:
                raise MeshError(f"{where} (key {key}) index {position} is {value}, past its "
                                f"{count} vertices")

        # A vertex height of NoHit is a non-finite position the writer should have dropped, and it
        # would be a NaN in the viewer's vertex buffer - which draws nothing and says nothing.
        for position, value in enumerate(ys):
            if value == MESH_NO_HIT:
                raise MeshError(f"{where} (key {key}) vertex {position} has no height (NoHit), "
                                f"which would be a NaN vertex")

        # Stage W/X: UVs (none, or one per vertex), then the ranges - ascending whole triangles inside this
        # building's indices, each on a page the file has, its tile rect inside the page in 4-px steps, its raw-UV
        # bounds finite and ordered, and no vertex used by two ranges (its code is quantised over ONE range).
        uvs = cur.i32(f"{where}'s UV count")
        if uvs not in (0, count):
            raise MeshError(f"{where} (key {key}) claims {uvs} UVs for {count} vertices")
        if uvs:
            cur.take(4 * uvs, f"{where}'s UVs")

        ranges = cur.i32(f"{where}'s atlas range count")
        if ranges < 0 or ranges > MESH_MAX_RANGES_PER_BUILDING:
            raise MeshError(f"{where} (key {key}) claims {ranges} atlas ranges; the cap is "
                            f"{MESH_MAX_RANGES_PER_BUILDING}")
        if ranges and not uvs:
            raise MeshError(f"{where} (key {key}) has atlas ranges and no UVs")

        end = 0
        owner = bytearray(count) if ranges else None
        for k in range(ranges):
            page = cur.i32(f"{where}'s range {k} page")
            first = cur.i32(f"{where}'s range {k} first index")
            span = cur.i32(f"{where}'s range {k} index count")
            tx, ty, tw, th = struct.unpack("<4H", cur.take(8, f"{where}'s range {k} tile rect"))
            u_min, u_max, v_min, v_max = struct.unpack("<4f", cur.take(16, f"{where}'s range {k} UV bounds"))
            if not (ATLAS_TILE_ALIGN <= tw <= ATLAS_TILE_MAX and ATLAS_TILE_ALIGN <= th <= ATLAS_TILE_MAX
                    and tw % ATLAS_TILE_ALIGN == 0 and th % ATLAS_TILE_ALIGN == 0
                    and tx + tw <= ATLAS_PAGE_SIZE and ty + th <= ATLAS_PAGE_SIZE):
                raise MeshError(f"{where} (key {key}) range {k}'s tile is {tw}x{th} at ({tx}, {ty}) - a tile is "
                                f"{ATLAS_TILE_ALIGN}..{ATLAS_TILE_MAX} px a side in steps of {ATLAS_TILE_ALIGN}, "
                                f"inside its {ATLAS_PAGE_SIZE} px page")
            if not all(math.isfinite(b) for b in (u_min, u_max, v_min, v_max)) or u_max < u_min or v_max < v_min:
                raise MeshError(f"{where} (key {key}) range {k}'s UV bounds are u {u_min}..{u_max}, "
                                f"v {v_min}..{v_max} - they must be finite with max >= min")
            if not 0 <= page < mesh["atlasPages"]:
                raise MeshError(f"{where} (key {key}) range {k} is on page {page}; the file has "
                                f"{mesh['atlasPages']}")
            if first < end or first % 3 or span <= 0 or span % 3 or first + span > indices:
                raise MeshError(f"{where} (key {key}) range {k} is indices {first}+{span} of {indices} "
                                f"(after {end}) - ranges are ascending whole triangles inside the building")
            end = first + span
            mesh["textured"] += span // 3
            tag = k + 1
            for j in range(first, first + span):
                vertex = index_values[j]
                if owner[vertex] not in (0, tag):
                    raise MeshError(f"{where} (key {key}) vertex {vertex} is used by ranges {owner[vertex] - 1} and "
                                    f"{k} - a vertex's UV code belongs to one range's bounds")
                owner[vertex] = tag
        mesh["ranges"] += ranges

        if levels and level not in levels:
            raise MeshError(f"{where} (key {key}) is on level {level}, which no band is")

    if cur.at != len(body):
        raise MeshError(f"carries {len(body) - cur.at} byte(s) after its last building")

    return mesh


def check_mesh(meta, folder, key, extent, levels, errors, warnings):
    """The 3D mesh beside the pictures, when the meta names one. Returns the summary line's mesh
    column."""
    block = meta.get("mesh")
    orphans = sorted(p.name for p in folder.iterdir()
                     if p.is_file() and p.name.lower().endswith(MESH_SUFFIX))

    if block is None:
        if orphans:
            warnings.append(f"{key}: {', '.join(orphans)} is on disk but the meta names no mesh - it "
                            f"is what an older capture leaves behind, and nothing reads it")
        return "no mesh"

    if not isinstance(block, dict):
        errors.append(f"{key}: mesh is not an object")
        return "mesh UNREADABLE"

    rel = block.get("file")
    if not isinstance(rel, str) or not rel.strip():
        errors.append(f"{key}: mesh.file is missing or empty")
        return "mesh UNREADABLE"

    parts = Path(rel.replace("\\", "/"))
    if parts.is_absolute() or ".." in parts.parts:
        errors.append(f"{key}: mesh.file {rel!r} is not a path inside the capture folder")
        return "mesh UNREADABLE"

    path = folder / parts
    if not path.is_file():
        errors.append(f"{key}: mesh.file {rel!r} does not exist in {folder} - the meta promises "
                      f"geometry that is not there")
        return "mesh MISSING"

    for name in orphans:
        if name.lower() != parts.name.lower():
            warnings.append(f"{key}: {name} is on disk but the meta names {parts.name} - it is what "
                            f"an older capture left behind, and nothing reads it")

    claimed_bytes = block.get("bytes")
    claimed_version = block.get("version")
    claimed_cells = block.get("cells")
    claimed_triangles = block.get("triangles")
    sha = block.get("sha256")

    for field, value in (("bytes", claimed_bytes), ("version", claimed_version),
                         ("cells", claimed_cells), ("triangles", claimed_triangles)):
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            errors.append(f"{key}: mesh.{field} {value!r} is missing or not a non-negative integer")
            return "mesh UNREADABLE"

    if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
        errors.append(f"{key}: mesh.sha256 {sha!r} is not 64 lower-case hex digits")
        return "mesh UNREADABLE"

    if claimed_bytes > MESH_MAX_FILE_BYTES:
        errors.append(f"{key}: mesh.bytes {claimed_bytes} is over {MESH_MAX_FILE_BYTES // 1048576} MiB - no "
                      f"host takes a mesh that large (the one-body download's absolute)")
        return "mesh TOO LARGE"

    data = path.read_bytes()

    if len(data) != claimed_bytes:
        errors.append(f"{key}: {rel} is {len(data)} bytes but mesh.bytes says {claimed_bytes} - the "
                      f"meta and the file on disk are from different captures")

    actual_sha = hashlib.sha256(data).hexdigest()
    if actual_sha != sha:
        errors.append(f"{key}: {rel}'s sha256 is {actual_sha[:16]}... but mesh.sha256 says "
                      f"{sha[:16]}... - this is not the mesh this meta describes")

    try:
        mesh = read_mesh(data, inflate_bound(claimed_cells, claimed_triangles))
    except MeshError as exc:
        errors.append(f"{key}: {rel} {exc}")
        return "mesh BROKEN"

    meta["_meshKeys"] = mesh["keys"]

    if mesh["triangles"] > MESH_BUILDER_ABSOLUTE:
        errors.append(f"{key}: {rel} holds {mesh['triangles']} triangles, over the builder's absolute "
                      f"{MESH_BUILDER_ABSOLUTE} - not a file this build wrote")

    # The relief cell: derived from the extent by one rule, which this script holds too.
    derived = relief_cell_for(mesh["maxX"] - mesh["minX"], mesh["maxZ"] - mesh["minZ"])
    for band in mesh["bands"]:
        if abs(band["cell"] - derived) <= 1e-6:
            continue
        if abs(band["cell"] - RELIEF_PRE_WP7_CELL) <= 1e-6 and abs(derived - RELIEF_PREFERRED_CELL) <= 1e-6:
            warnings.append(f"{key}: {rel} band {band['level']} was captured before WP7 at 2 m - the rule now "
                            f"gives {derived:g} m for this extent; the next capture samples it finer")
        else:
            errors.append(f"{key}: {rel} band {band['level']} has a {band['cell']:g} m cell but "
                          f"relief_cell_for({mesh['maxX'] - mesh['minX']:g}, {mesh['maxZ'] - mesh['minZ']:g}) "
                          f"is {derived:g} m - not what the builder derives for this extent")

    y_span = mesh["yMax"] - mesh["yMin"]
    if y_span > MESH_MAX_Y_SPAN:
        errors.append(f"{key}: {rel}'s y range is {mesh['yMin']:.4g}..{mesh['yMax']:.4g} m ({y_span:.4g} m) - "
                      f"over {MESH_MAX_Y_SPAN:g} m every height is quantised into steps too coarse to "
                      f"draw, which is what a renderer with bounds of 3.4e38 made of the whole map")
    elif y_span > MESH_WARN_Y_SPAN:
        warnings.append(f"{key}: {rel}'s y range is {mesh['yMin']:.4g}..{mesh['yMax']:.4g} m "
                        f"({y_span:.4g} m), over {MESH_WARN_Y_SPAN:g} m - no EFT map is that tall; "
                        f"check the building log line for an oversized renderer")

    if mesh["version"] != claimed_version:
        errors.append(f"{key}: {rel} is version {mesh['version']} but mesh.version says "
                      f"{claimed_version}")

    if extent is not None:
        min_x, min_z, max_x, max_z = extent
        # Exactly, not within a tolerance: both sides are the same doubles the harvest measured, and
        # a mesh quantised over a different rectangle draws every building in the wrong place.
        for field, mine, theirs in (("minX", mesh["minX"], min_x), ("minZ", mesh["minZ"], min_z),
                                    ("maxX", mesh["maxX"], max_x), ("maxZ", mesh["maxZ"], max_z)):
            if mine != theirs:
                errors.append(f"{key}: {rel}'s extent.{field} {mine!r} is not the meta's {theirs!r} "
                              f"- the mesh is quantised over a different rectangle")

        for band in mesh["bands"]:
            want_w = math.ceil((max_x - min_x) / band["cell"])
            want_h = math.ceil((max_z - min_z) / band["cell"])
            if band["width"] != want_w or band["height"] != want_h:
                errors.append(f"{key}: {rel} band {band['level']} is {band['width']}x"
                              f"{band['height']} cells but the extent's "
                              f"{max_x - min_x:g}x{max_z - min_z:g} m at {band['cell']:g} m wants "
                              f"{want_w}x{want_h}")

    mesh_levels = {band["level"] for band in mesh["bands"]}
    if mesh_levels != levels:
        errors.append(f"{key}: {rel} has bands {sorted(mesh_levels)} but the meta's floors are "
                      f"{sorted(levels)} - a band the picture has no floor for cannot be drawn, and "
                      f"a floor with no band has no ground")

    if mesh["cells"] != claimed_cells:
        errors.append(f"{key}: {rel} holds {mesh['cells']} cells but mesh.cells says {claimed_cells}")
    if mesh["triangles"] != claimed_triangles:
        errors.append(f"{key}: {rel} holds {mesh['triangles']} triangles but mesh.triangles says "
                      f"{claimed_triangles}")

    hit = sum(band["hit"] for band in mesh["bands"])
    cell_text = ", ".join(sorted({f"{band['cell']:g}" for band in mesh["bands"]})) or "-"
    meta["_meshAtlasPages"] = mesh["atlasPages"]

    return (f"mesh {len(data) / 1048576:.2f} MB, {len(mesh['bands'])} band(s), "
            f"{mesh['cells']} cells ({(100 * hit / mesh['cells']) if mesh['cells'] else 0:.0f} % "
            f"hit), {mesh['buildings']} building(s), "
            f"{mesh['triangles']} triangles, {mesh['textured']} textured in {mesh['ranges']} range(s) "
            f"over {mesh['atlasPages']} atlas page(s), per-building max {mesh['maxBuilding']} triangles, "
            f"cell {cell_text} m")


def png_is(path, width, height):
    """(ok, actual size) for a PNG that must be width x height."""
    size, why = png_size(path)
    return size == (width, height), size if size is not None else why


def check_atlas(meta, folder, key, errors, warnings):
    """Stage W's atlas pages beside the mesh: page n is <key>-atlas-<n>.png, 4096x4096, with the length and
    sha256 the meta records; the mesh may not name more pages than the meta lists. Returns the summary
    line's atlas column."""
    block = meta.get("atlas")
    pages_in_mesh = meta.pop("_meshAtlasPages", None)
    on_disk = sorted(p.name for p in folder.iterdir()
                     if p.is_file() and p.name.lower().startswith(f"{key.lower()}-atlas-")
                     and p.name.lower().endswith(".png"))

    if block is None:
        for name in on_disk:
            warnings.append(f"{key}: {name} is on disk but the meta names no atlas - nothing reads it")
        if pages_in_mesh:
            warnings.append(f"{key}: the mesh's ranges name {pages_in_mesh} atlas page(s) but the meta lists none - "
                            f"those buildings fall back to the side views")
        return "no atlas"

    if not isinstance(block, list) or len(block) > MESH_MAX_ATLAS_PAGES:
        errors.append(f"{key}: atlas is not a list of at most {MESH_MAX_ATLAS_PAGES} pages")
        return "atlas UNREADABLE"

    if meta.get("mesh") is None:
        errors.append(f"{key}: the meta lists an atlas but no mesh - an atlas belongs to a mesh")

    named = set()
    total = 0

    for n, page in enumerate(block):
        if not isinstance(page, dict):
            errors.append(f"{key}: atlas[{n}] is not an object")
            continue

        want = f"{key}-atlas-{n}.png"
        name = page.get("file")
        if name != want:
            errors.append(f"{key}: atlas[{n}].file is {name!r}, not {want!r} - page n is <key>-atlas-<n>.png")
            continue
        named.add(name.lower())

        if page.get("page") != n:
            errors.append(f"{key}: atlas[{n}].page is {page.get('page')!r}, not {n}")

        width, height = page.get("width"), page.get("height")
        if width != ATLAS_PAGE_SIZE or height != ATLAS_PAGE_SIZE:
            errors.append(f"{key}: atlas[{n}] is {width!r}x{height!r}; a page is {ATLAS_PAGE_SIZE}x{ATLAS_PAGE_SIZE}")

        tiles = page.get("tiles")
        if isinstance(tiles, bool) or not isinstance(tiles, int) or tiles < 0:
            errors.append(f"{key}: atlas[{n}].tiles {tiles!r} is not a non-negative integer")

        path = folder / name
        if not path.is_file():
            errors.append(f"{key}: atlas page {name} does not exist in {folder}")
            continue

        data = path.read_bytes()
        total += len(data)

        claimed = page.get("bytes")
        if claimed is not None and claimed != len(data):
            errors.append(f"{key}: {name} is {len(data)} bytes but atlas[{n}].bytes says {claimed}")

        sha = page.get("sha256")
        if not isinstance(sha, str) or hashlib.sha256(data).hexdigest() != sha:
            errors.append(f"{key}: {name}'s sha256 is not atlas[{n}].sha256 - not the page this meta describes")

        ok, size = png_is(path, width if isinstance(width, int) else -1, height if isinstance(height, int) else -1)
        if not ok:
            errors.append(f"{key}: {name} is {size} but atlas[{n}] says {width}x{height}")

    for name in on_disk:
        if name.lower() not in named:
            warnings.append(f"{key}: {name} is on disk but the meta's atlas does not name it - nothing reads it")

    if pages_in_mesh is not None and pages_in_mesh > len(block):
        # The documented degraded state (a page commit failed after the mesh was written): the viewer draws
        # those ranges the stage U/V way. A warning, not an error (stage W review, M4).
        warnings.append(f"{key}: the mesh's ranges may name {pages_in_mesh} atlas page(s) but the meta lists "
                        f"{len(block)} - the buildings on the missing pages fall back to the side views")

    return f"atlas {len(block)} page(s), {total / 1048576:.1f} MB"


SIDE_DIRS = ("N", "S", "E", "W")
SIDE_MAX_PPM = 2.0          # the contract's pxPerMetre ceiling (MapCapture.SidePixelsPerMetre)
SIDE_VECTOR_TOLERANCE = 1e-3
SIDE_ORIGIN_TOLERANCE = 1e-3


def side_forward(direction):
    """The contract's forward vector for a side: normalize(toCentreXZ + (0,-1,0))."""
    to_centre = {"N": (0.0, -1.0), "S": (0.0, 1.0), "E": (-1.0, 0.0), "W": (1.0, 0.0)}[direction]
    s = 1.0 / math.sqrt(2.0)
    return (to_centre[0] * s, -s, to_centre[1] * s)


def vector3(value):
    """A [x, y, z] list of three finite numbers, or None."""
    if not isinstance(value, list) or len(value) != 3:
        return None
    out = []
    for item in value:
        n = number(item)
        if n is None or not math.isfinite(n):
            return None
        out.append(n)
    return tuple(out)


def dot3(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def check_sides(meta, folder, key, extent, errors, warnings):
    """The four oblique side views (plan, stage U - a frozen contract) when the meta lists them.
    Returns the summary line's sides column.

    Per entry: dir is one of N/S/E/W and appears once; the file is <key>-side-<dir>.png and exists;
    its IHDR size is the entry's width x height; pxPerMetre is in (0, 2]; forward/right/up are unit
    length and mutually orthogonal (1e-3), forward is the contract's vector for that dir and right is
    normalize(cross(forward, up-world)); originR/originU are the minima of dot(right,.)/dot(up,.) over
    the 8 corners of extent x [yMin, yMax] (1e-3); width/height are ceil(span * pxPerMetre) within
    1 px. A <key>-side-*.png the meta does not name is a WARN."""
    sides = meta.get("sides")
    on_disk = sorted(p.name for p in folder.iterdir()
                     if p.is_file() and p.name.lower().startswith(f"{key.lower()}-side-")
                     and p.name.lower().endswith(".png") and not p.name.lower().endswith(".dist.png"))

    if sides is None or sides == []:
        for name in on_disk:
            warnings.append(f"{key}: {name} is on disk but the meta lists no side views - it is what an "
                            f"older capture leaves behind, and nothing reads it")
        return "no sides"

    if not isinstance(sides, list):
        errors.append(f"{key}: sides is not a list")
        return "sides UNREADABLE"

    named, seen, sizes, ranges = set(), set(), [], set()

    for index, side in enumerate(sides):
        where = f"{key}: sides[{index}]"
        if not isinstance(side, dict):
            errors.append(f"{where} is not an object")
            continue

        direction = side.get("dir")
        if direction not in SIDE_DIRS:
            errors.append(f"{where}.dir {direction!r} is not one of {', '.join(SIDE_DIRS)}")
            continue
        where = f"{key}: side {direction}"
        if direction in seen:
            errors.append(f"{where} is listed twice")
        seen.add(direction)

        rel = side.get("file")
        want_name = f"{key}-side-{direction}.png"
        if not isinstance(rel, str) or rel.lower() != want_name.lower():
            errors.append(f"{where}.file {rel!r} is not {want_name!r}")
            continue
        named.add(rel.lower())

        path = folder / rel
        if not path.is_file():
            errors.append(f"{where}.file {rel!r} does not exist in {folder}")
            continue

        width, height = side.get("width"), side.get("height")
        if (isinstance(width, bool) or not isinstance(width, int) or width <= 0 or
                isinstance(height, bool) or not isinstance(height, int) or height <= 0):
            errors.append(f"{where}: width/height {width!r}x{height!r} are not positive integers")
            continue

        actual, why = png_size(path)
        if actual is None:
            errors.append(f"{where}: {rel} {why}")
        elif actual != (width, height):
            errors.append(f"{where}: {rel} is {actual[0]}x{actual[1]} but the meta says {width}x{height}")

        # The side's distance sidecar - what the next capture merges this side against. Optional (a side
        # written before sides merged has none, and the next capture then takes its own pixels
        # everywhere), but when present it has to be the side's size, or LoadPicture refuses it and every
        # later merge of this side is silently a replacement.
        dist_name = f"{key}-side-{direction}.dist.png"
        dist_path = folder / dist_name
        if not dist_path.is_file():
            warnings.append(f"{where}: no {dist_name} - the next capture of this side cannot merge into it and "
                            f"takes its own pixels everywhere")
        else:
            dist_size, dist_why = png_size(dist_path)
            if dist_size is None:
                errors.append(f"{where}: {dist_name} {dist_why}")
            elif dist_size != (width, height):
                errors.append(f"{where}: {dist_name} is {dist_size[0]}x{dist_size[1]} but the side is "
                              f"{width}x{height} - the merge would refuse it")
            elif dist_path.stat().st_mtime < path.stat().st_mtime - SIDECAR_STALE_SECONDS:
                warnings.append(f"{where}: {dist_name} is {path.stat().st_mtime - dist_path.stat().st_mtime:.0f} s older "
                                f"than its picture - a sidecar from an earlier capture (review F09)")

        ppm = number(side.get("pxPerMetre"))
        if ppm is None or not (0 < ppm <= SIDE_MAX_PPM + 1e-6):
            errors.append(f"{where}.pxPerMetre {side.get('pxPerMetre')!r} is not in (0, {SIDE_MAX_PPM:g}]")
            continue

        f, r, u = vector3(side.get("forward")), vector3(side.get("right")), vector3(side.get("up"))
        if f is None or r is None or u is None:
            errors.append(f"{where}: forward/right/up are not three finite numbers each")
            continue

        basis_ok = True
        for label, v in (("forward", f), ("right", r), ("up", u)):
            length = math.sqrt(dot3(v, v))
            if abs(length - 1.0) > SIDE_VECTOR_TOLERANCE:
                errors.append(f"{where}.{label} has length {length:.6f}, not 1")
                basis_ok = False
        for label, a, b in (("forward.right", f, r), ("forward.up", f, u), ("right.up", r, u)):
            d = dot3(a, b)
            if abs(d) > SIDE_VECTOR_TOLERANCE:
                errors.append(f"{where}: {label} = {d:.6f} - the basis is not orthogonal")
                basis_ok = False

        want_f = side_forward(direction)
        if max(abs(a - b) for a, b in zip(f, want_f)) > SIDE_VECTOR_TOLERANCE:
            errors.append(f"{where}.forward {f} is not the contract's {tuple(round(c, 4) for c in want_f)} "
                          f"for {direction}")
            basis_ok = False

        # right = normalize(cross(forward, (0,1,0))) = normalize((-f.z, 0, f.x))
        rr = (-f[2], 0.0, f[0])
        rl = math.sqrt(dot3(rr, rr)) or 1.0
        want_r = (rr[0] / rl, rr[1] / rl, rr[2] / rl)
        if max(abs(a - b) for a, b in zip(r, want_r)) > SIDE_VECTOR_TOLERANCE:
            errors.append(f"{where}.right {r} is not normalize(cross(forward, up)) = "
                          f"{tuple(round(c, 4) for c in want_r)}")
            basis_ok = False

        # up = cross(right, forward). Unit length and orthogonality both hold for -up as well, so this
        # is the one test that tells an upside-down picture from a right one.
        want_u = (r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2], r[0] * f[1] - r[1] * f[0])
        if max(abs(a - b) for a, b in zip(u, want_u)) > SIDE_VECTOR_TOLERANCE:
            errors.append(f"{where}.up {u} is not cross(right, forward) = "
                          f"{tuple(round(c, 4) for c in want_u)} - the picture would be upside down")
            basis_ok = False

        y_min, y_max = number(side.get("yMin")), number(side.get("yMax"))
        origin_r, origin_u = number(side.get("originR")), number(side.get("originU"))
        if None in (y_min, y_max, origin_r, origin_u) or not y_max > y_min:
            errors.append(f"{where}: yMin/yMax/originR/originU are missing, not numbers, or yMax <= yMin")
            continue
        ranges.add((y_min, y_max))

        if extent is None or not basis_ok:
            continue

        min_x, min_z, max_x, max_z = extent
        corners = [(x, y, z) for x in (min_x, max_x) for y in (y_min, y_max) for z in (min_z, max_z)]
        along_r = [dot3(r, c) for c in corners]
        along_u = [dot3(u, c) for c in corners]

        if abs(min(along_r) - origin_r) > SIDE_ORIGIN_TOLERANCE:
            errors.append(f"{where}.originR {origin_r!r} is not the box's minimum along right, "
                          f"{min(along_r)!r} - every wall textured from this side would be shifted")
        if abs(min(along_u) - origin_u) > SIDE_ORIGIN_TOLERANCE:
            errors.append(f"{where}.originU {origin_u!r} is not the box's minimum along up, "
                          f"{min(along_u)!r}")

        want_w = math.ceil((max(along_r) - min(along_r)) * ppm)
        want_h = math.ceil((max(along_u) - min(along_u)) * ppm)
        if abs(width - want_w) > PIXEL_TOLERANCE or abs(height - want_h) > PIXEL_TOLERANCE:
            errors.append(f"{where} is {width}x{height} but the box at {ppm:g} px/m wants {want_w}x{want_h}")

        sizes.append(f"{direction} {width}x{height}")

    for name in on_disk:
        if name.lower() not in named:
            warnings.append(f"{key}: {name} is on disk but the meta does not list it - it is what an older "
                            f"capture leaves behind, and nothing reads it")

    # One box for all four (review F13): the capture frames every side it renders on one y range, which only
    # grows. A legacy mix is legal until the next capture that renders all four, which reframes them.
    if len(ranges) > 1:
        n = len(ranges)
        warnings.append(f"{key}: the side views are framed on {n} different y ranges - the next capture reframes them (review F13)")

    return f"sides {', '.join(sizes) if sizes else 'none valid'}"


def check_capture(folder, errors, warnings):
    """Validates one capture folder. Returns its summary line, or None if there was nothing to read."""
    key = folder.name
    metas = [p for p in sorted(folder.iterdir()) if p.is_file() and p.name.lower().endswith(".map.json")]
    if not metas:
        errors.append(f"{key}: no {key}.map.json in {folder} - a capture folder without its meta is "
                      f"a half-written capture, not a map")
        return None
    if len(metas) > 1:
        errors.append(f"{key}: {len(metas)} *.map.json files in {folder} "
                      f"({', '.join(p.name for p in metas)}) - which one is the capture?")
        return None
    meta_path = metas[0]
    if meta_path.name[:-len(".map.json")].lower() != key.lower():
        errors.append(f"{key}: the meta is named {meta_path.name}, not {key}.map.json - the client "
                      f"looks it up by folder name")

    meta, why = load_json(meta_path)
    if meta is None:
        errors.append(f"{key}: {meta_path.name} {why}")
        return None
    if not isinstance(meta, dict):
        errors.append(f"{key}: {meta_path.name} is not a JSON object")
        return None

    schema = meta.get("schemaVersion")
    if schema != SCHEMA_VERSION or isinstance(schema, bool):
        errors.append(f"{key}: schemaVersion {schema!r} is not {SCHEMA_VERSION} - this check reads "
                      f"schema {SCHEMA_VERSION} captures only, so nothing below has been verified")
        return None

    name = meta.get("map")
    if not isinstance(name, str) or name.lower() != key.lower():
        errors.append(f"{key}: map {name!r} is not the folder name {key!r} - the client keys the "
                      f"capture by folder, so this one would be served under the wrong map")

    px_per_metre = number(meta.get("pxPerMetre"))
    if px_per_metre is None or px_per_metre <= 0:
        errors.append(f"{key}: pxPerMetre {meta.get('pxPerMetre')!r} is missing or not a positive "
                      f"number - nothing can be scaled without it")
        px_per_metre = None

    extent = check_extent(meta, errors, key)
    levels, pixels, total = check_floors(meta, folder, key, extent, px_per_metre, errors, warnings)

    mesh = check_mesh(meta, folder, key, extent, levels, errors, warnings)
    atlas = check_atlas(meta, folder, key, errors, warnings)
    sides = check_sides(meta, folder, key, extent, errors, warnings)

    zones, rotation = check_zone(key, extent, levels, errors, warnings)
    meta_rotation = number(meta.get("rotation"))
    if rotation is not None and meta_rotation is not None and abs(meta_rotation - rotation) > 0.01:
        warnings.append(f"{key}: rotation {meta_rotation:g} differs from the zone file's "
                        f"{rotation:g} - the picture and the extent were measured at different angles")

    floor_count = len(meta["floors"]) if isinstance(meta.get("floors"), list) else 0
    scale = f"{1 / px_per_metre:.2f} m/px" if px_per_metre else "? m/px"
    captured = meta.get("capturedAt") or "?"
    return (f"{key}: {floor_count} floor(s), {pixels} @ {scale}, {total / 1048576:.1f} MB, {zones}\n"
            f"    {mesh}; {atlas}; {sides}",
            captured)


def mesh_keys(folder):
    """(keys -> triangles, None) for a capture folder's mesh, or (None, why)."""
    metas = [p for p in sorted(folder.iterdir()) if p.is_file() and p.name.lower().endswith(".map.json")]
    if len(metas) != 1:
        return None, "no single meta"
    meta, why = load_json(metas[0])
    if not isinstance(meta, dict):
        return None, why or "the meta is not an object"
    block = meta.get("mesh")
    if not isinstance(block, dict) or not isinstance(block.get("file"), str):
        return None, "no mesh"
    parts = Path(block["file"].replace("\\", "/"))
    if parts.is_absolute() or ".." in parts.parts or not (folder / parts).is_file():
        return None, f"mesh file {block['file']!r} not found"
    cells, triangles = block.get("cells"), block.get("triangles")
    bound = (inflate_bound(cells, triangles)
             if isinstance(cells, int) and isinstance(triangles, int) and cells >= 0 and triangles >= 0
             else MESH_MAX_INFLATED_BYTES)
    try:
        return read_mesh((folder / parts).read_bytes(), bound)["keys"], None
    except MeshError as exc:
        return None, str(exc)


def compare(new_root, old_root, errors, warnings):
    """--compare: per map present in both roots, every building key of OLD must be in NEW with at least
    as many triangles (WP7 Q1/Q2). Returns the summary lines."""
    lines = []
    if not old_root.is_dir():
        errors.append(f"--compare: no OLD captures folder at {old_root}")
        return lines
    for folder in sorted(p for p in new_root.iterdir() if p.is_dir()):
        old_folder = old_root / folder.name
        if not old_folder.is_dir():
            warnings.append(f"{folder.name}: --compare has no {folder.name} under {old_root} - not compared")
            continue
        new_keys, why_new = mesh_keys(folder)
        old_keys, why_old = mesh_keys(old_folder)
        if old_keys is None:
            warnings.append(f"{folder.name}: --compare cannot read the OLD mesh ({why_old}) - not compared")
            continue
        if new_keys is None:
            errors.append(f"{folder.name}: --compare: OLD has a mesh and NEW's cannot be read ({why_new})")
            continue
        missing = sorted(k for k in old_keys if k not in new_keys)
        fewer = sorted((k, old_keys[k], new_keys[k]) for k in old_keys
                       if k in new_keys and new_keys[k] < old_keys[k])
        if missing:
            errors.append(f"{folder.name}: --compare: {len(missing)} building key(s) in OLD are absent from NEW "
                          f"(first: {', '.join(str(k) for k in missing[:8])})")
        if fewer:
            errors.append(f"{folder.name}: --compare: {len(fewer)} building key(s) have fewer triangles in NEW "
                          f"(first: {', '.join(f'{k} {o}->{n}' for k, o, n in fewer[:8])})")
        lines.append(f"{folder.name}: compared {len(old_keys)} OLD key(s) with {len(new_keys)} NEW: "
                     f"{len(missing)} missing, {len(fewer)} with fewer triangles, "
                     f"{sum(new_keys.values())} vs {sum(old_keys.values())} triangles")
    return lines


def main():
    if not CAPTURES.is_dir():
        fail_hard(f"no captures folder at {CAPTURES} - pass one as the first argument")

    folders = [p for p in sorted(CAPTURES.iterdir()) if p.is_dir()]
    errors, warnings, lines = [], [], []

    for folder in folders:
        result = check_capture(folder, errors, warnings)
        if result is not None:
            lines.append(result)

    print(f"captures: {CAPTURES}")
    print(f"zones:    {ZONES}")
    print("-" * 78)
    if not folders:
        print("  (no capture folders - nothing to check)")
    for line, captured in lines:
        print(f"  {line}")
        print(f"{'':>4}captured {captured}")
    print()

    if COMPARE is not None:
        print(f"compare:  NEW {CAPTURES} against OLD {COMPARE}")
        for line in compare(CAPTURES, COMPARE, errors, warnings):
            print(f"  {line}")
        print()

    for w in warnings:
        print(f"WARN   {w}")
    for e in errors:
        print(f"ERROR  {e}")
    if warnings or errors:
        print()

    print(f"{len(folders)} capture(s) checked, {len(errors)} problem(s)"
          + (f", {len(warnings)} warning(s)" if warnings else ""))
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
