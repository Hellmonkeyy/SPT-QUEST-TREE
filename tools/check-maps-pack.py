#!/usr/bin/env python3
"""Checks the map sets that package.ps1 is about to SHIP, in the repo folder they ship from.

tools/check-capture.py checks a fresh in-raid capture: PNG floors in the client's captures\\ folder,
cross-checked against the server's zone files. This checks the other end of the pipe - the converted
JPEG set under Source\\Tarkov-QuestTree-Server\\maps\\, which is what the release zip carries into
every install - and deliberately does NOT look at zones\\: a shipped set is validated against its own
meta and against the code constant that reads it, so packaging works on a machine that has never
harvested the map. The two scripts overlap on purpose; the pipe has two ends and only one of them is
in the zip.

What it checks, per <key>\\ folder under the maps root:
  1. exactly one <key>.map.json, named for the folder, parsing as an object whose "map" is the folder
     name (case-insensitive) and whose pxPerMetre and extent are usable numbers.
  2. schemaVersion equals --schema, which package.ps1 reads out of the client's
     UI/MapCatalog.cs SupportedCaptureSchema. A set the shipped client would SKIP is not shippable.
  3. every floor: a distinct integer level, a non-empty name, a minY below its maxY, a "file" that is
     a plain name inside the folder, that file exists, starts with the JPEG SOI marker, and its SOF
     width/height equal the meta's - which in turn equal ceil(extent span * pxPerMetre) within 1 px.
  4. no orphan .jpg: an image in the folder that no floor names is dead weight in a payload measured
     in megabytes, and it is what a re-capture with fewer floors leaves behind.
  5. the MESH, when the meta names one (the block is optional and absent on every set captured before
     1.19.0, on DynamicMaps sets, and wherever the relief could not be built): the file exists, its
     byte length and sha256 are the ones the meta states, its deflated header parses, its version is
     the one the client reads, its extent is the meta's to 1e-6, its band levels are exactly the
     floors' levels, and its cell and triangle counts are the ones the meta claims. Plus no orphan
     *-mesh.bin that no meta names - a stale mesh beside a fresh meta is a mesh of a DIFFERENT extent,
     which is the one kind of wrongness in this payload that no eye can catch: it is binary, so the
     hash IS the inspection.
  6. the SIDES, when the meta names any (the oblique N/S/E/W pictures the 3D view textures walls
     with; absent on every older set): each is a distinct direction, its file is exactly
     <key>-side-<dir>.jpg and exists, it is a real JPEG whose frame size equals the entry's
     width/height, its pxPerMetre is a positive number, its forward/right/up are three unit vectors (to
     1e-3) and its origins and height range are numbers. A side's pixels are placed on walls by those
     numbers alone, so a picture of another size or a skewed basis textures every wall wrong with
     nothing else to show it. Side files the meta does not name are orphans like any other image.

Every folder it finds is checked the same way, whether it is a vanilla map or a modded one the
maintainer chose to ship.

What it does NOT check, by design:
  - the pixels. Whether the JPEG is the right map, the right way up, or a grey rectangle is what the
    eye is for; this proves only that its dimensions are the ones the meta and the extent imply. It
    does not decode the image at all - the SOF header is read out of the file's own bytes.
  - the extent against the harvested zone file (that is check-capture.py's job, and it needs an
    install), rotation, tileSize, labels, timeOfDay, modVersion, or file sizes (package.ps1 owns the
    1.5 MB per image gate and the total-payload warning, because it is the one building the zip).
  - the mesh's GEOMETRY. That the relief is the right shape, that the buildings are where the map's
    buildings are, and that a height is not a hundred metres out are what the screen is for. This
    proves the file is the one the meta describes and that its frame agrees with the pictures'.
  - WHICH maps are here. Coverage of the 11 vanilla maps is package.ps1's warning, not an error:
    DynamicMaps stays a selectable picture source, so a map with no set falls back rather than
    breaking, and the release ships whatever sets exist. This script only says whether the sets that
    ARE here are shippable.

Proven able to fail before it shipped: against generated fakes it exits 1 naming the map and the
field for a schemaVersion 2 meta, for a floor whose file is absent, for an orphan image no floor
names, for a PNG under a .jpg name, and for a meta width 3 px from the extent's arithmetic, and
exits 0 on an intact set - of 11 keys or of 4.

The mesh gate was proven the same way, 14 cases one fault at a time (the harness is in the session's
scratchpad, meshpack-harness/harness.py): a truncated .bin, a wrong sha256, a wrong byte length, a
mesh whose extent is 10 m wider than the meta's, bands at levels the floors do not have, a wrong cell
count, a wrong triangle count, a wrong version, four bytes of trailing data, an orphan *-mesh.bin, a
mesh the meta names but that is absent, and bytes that are not a deflate stream at all - each exits 1
naming the map and what was wrong; an intact set with a mesh and a set with NO mesh both exit 0.

Usage:  python tools/check-maps-pack.py <maps-root> --schema N
"""

import argparse
import hashlib
import json
import math
import struct
import sys
import zlib
from pathlib import Path

PIXEL_TOLERANCE = 1     # px, on each axis, against ceil(span * pxPerMetre)
META_SUFFIX = ".map.json"

# The mesh file, from the client's MapMeshFile: the WHOLE file is one raw deflate block (no zlib
# wrapper - hence wbits -15), magic included, and everything in it is little-endian. The caps are that
# class's own, repeated here because this script runs with no access to it.
MESH_SUFFIX = "-mesh.bin"
MESH_MAGIC = b"QTM1"
MESH_VERSION = 1                    # MapMeshFile.Version
MESH_MAX_BANDS = 8                  # MaxFloors
MESH_MAX_CELLS_PER_BAND = 4_000_000
MESH_MAX_BUILDINGS = 20_000
MESH_MAX_VERTICES_TOTAL = 4_000_000
MESH_MAX_TRIANGLES = 2_000_000
MESH_MAX_INFLATED = 64 * 1024 * 1024    # what this script will inflate before giving up
EXTENT_TOLERANCE = 1e-6                 # m, mesh header against the meta's extent

# SOFn: C0-CF except C4 (DHT), C8 (JPG extension) and CC (DAC), which are not frame headers.
SOF_MARKERS = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7,
               0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}
STANDALONE_MARKERS = {0x01} | set(range(0xD0, 0xD9))   # TEM and RST0-7 carry no length


def jpeg_size(path):
    """(width, height) from the JPEG's frame header, or a raise-free (None, reason).

    Walks the marker chain rather than trusting a fixed offset: the SOF is preceded by however many
    APPn/COM/DQT segments the encoder wrote, so its position varies by file. Nothing is decoded."""
    try:
        data = path.read_bytes()
    except OSError as exc:
        return None, f"cannot be read ({exc.strerror or exc})"
    if len(data) < 4:
        return None, f"{len(data)} bytes long - too short to be a JPEG"
    if data[:3] != b"\xff\xd8\xff":
        return None, ("does not start with the JPEG SOI marker (ff d8 ff) but with "
                      f"{' '.join(f'{b:02x}' for b in data[:3])} - a PNG or a text file under a "
                      f".jpg name is not something the client's loader will draw")

    at = 2
    while at + 1 < len(data):
        if data[at] != 0xFF:
            return None, f"byte {at} is {data[at]:02x} where a marker (ff) must start"
        marker = data[at + 1]
        if marker == 0xFF:          # fill byte before the real marker
            at += 1
            continue
        if marker in STANDALONE_MARKERS:
            at += 2
            continue
        if marker == 0xD9:
            return None, "reaches end-of-image without a frame header (SOF), so it has no size"
        if marker == 0xDA:
            return None, "starts its scan before any frame header (SOF), so it has no size"
        if at + 4 > len(data):
            return None, f"segment header at byte {at} is cut off"
        length = struct.unpack(">H", data[at + 2:at + 4])[0]
        if length < 2:
            return None, f"segment at byte {at} claims length {length}"
        if marker in SOF_MARKERS:
            if at + 9 > len(data) or length < 7:
                return None, f"frame header at byte {at} is cut off"
            _, height, width = struct.unpack(">BHH", data[at + 4:at + 9])
            if width == 0 or height == 0:
                return None, f"frame header says {width}x{height}"
            return (width, height), None
        at += 2 + length
    return None, "has no frame header (SOF) in it at all"


def mesh_header(path):
    """(header dict, None) or (None, reason) for a mesh file - MapMeshFile's layout, read whole.

    Inflated in chunks with a ceiling, because this file arrives from a capture, from a host, or from a
    hand-copy: a corrupt length must cost a bounded read rather than the machine. Every count is checked
    against its cap BEFORE the bytes behind it are skipped, exactly as both C# readers do, and a file
    that ends inside a grid is a failure rather than a short read nobody notices.

    The returned dict holds what the gates compare: version, extent, y range, bands (level, cell size,
    width, height), and the total cell, vertex and triangle counts."""
    try:
        raw = path.read_bytes()
    except OSError as exc:
        return None, f"cannot be read ({exc.strerror or exc})"
    if not raw:
        return None, "is empty"

    try:
        engine = zlib.decompressobj(-15)
        out = bytearray()
        for at in range(0, len(raw), 1 << 16):
            out += engine.decompress(raw[at:at + (1 << 16)], MESH_MAX_INFLATED - len(out) + 1)
            if len(out) > MESH_MAX_INFLATED:
                return None, (f"inflates to more than the {MESH_MAX_INFLATED:,} bytes this check will "
                              f"read - it is not a mesh this build wrote")
        out += engine.flush()
    except zlib.error as exc:
        return None, f"is not a deflate stream, or its data is corrupt ({exc})"
    if len(out) > MESH_MAX_INFLATED:
        return None, f"inflates to more than the {MESH_MAX_INFLATED:,} bytes this check will read"

    at = 0

    def take(count, what):
        """`count` bytes, or a raise-free signal that the file ends inside `what`."""
        nonlocal at
        if at + count > len(out):
            raise EOFError(what)
        chunk = out[at:at + count]
        at += count
        return chunk

    def i32(what):
        return struct.unpack("<i", take(4, what))[0]

    try:
        if bytes(take(4, "its magic")) != MESH_MAGIC:
            got = bytes(out[:4])
            return None, (f"does not start with {MESH_MAGIC.decode()} but with "
                          f"{' '.join(f'{b:02x}' for b in got)} - it is not a Quest Tracker mesh file")

        version = i32("its version")
        if version != MESH_VERSION:
            return None, (f"is mesh format version {version}, not the v{MESH_VERSION} the shipped client "
                          f"reads - that client would refuse it")

        min_x, min_z, max_x, max_z = struct.unpack("<4d", take(32, "its extent"))
        y_min, y_max = struct.unpack("<2f", take(8, "its height range"))
        if not (max_x > min_x and max_z > min_z):
            return None, f"has an empty extent: x {min_x:g}..{max_x:g}, z {min_z:g}..{max_z:g}"
        if not (y_max > y_min):
            return None, f"has an empty height range: {y_min:g}..{y_max:g}"

        band_count = i32("its band count")
        if band_count < 0 or band_count > MESH_MAX_BANDS:
            return None, f"claims {band_count:,} bands, past the {MESH_MAX_BANDS} a map may have"

        bands, cells_total = [], 0
        for index in range(band_count):
            level = i32(f"band {index}'s level")
            cell_metres = struct.unpack("<f", take(4, f"band {index}'s cell size"))[0]
            width = i32(f"band {index}'s width")
            height = i32(f"band {index}'s height")
            if width <= 0 or height <= 0 or not (cell_metres > 0):
                return None, (f"has a level {level} band of {width}x{height} cells at {cell_metres:g} m, "
                              f"which is not a grid")
            cells = width * height
            # Before the skip, not after: this is the line between a corrupt width and a read that
            # runs to the ceiling.
            if cells > MESH_MAX_CELLS_PER_BAND:
                return None, (f"has a level {level} band of {cells:,} cells, past the "
                              f"{MESH_MAX_CELLS_PER_BAND:,} a band may have")
            if any(b["level"] == level for b in bands):
                return None, f"has two level {level} bands"
            take(cells * 3, f"band {index}'s grids")   # uint16 height + uint8 distance per cell
            bands.append({"level": level, "cell": cell_metres, "width": width, "height": height})
            cells_total += cells

        building_count = i32("its building count")
        if building_count < 0 or building_count > MESH_MAX_BUILDINGS:
            return None, (f"claims {building_count:,} buildings, past the {MESH_MAX_BUILDINGS:,} a map "
                          f"may have")

        vertices, indices = 0, 0
        for index in range(building_count):
            i32(f"building {index}'s key")
            i32(f"building {index}'s level")
            vertex_count = i32(f"building {index}'s vertex count")
            if vertex_count < 0:
                return None, f"has a building claiming {vertex_count:,} vertices"
            vertices += vertex_count
            if vertices > MESH_MAX_VERTICES_TOTAL:
                return None, (f"claims {vertices:,} vertices by building {index}, past the "
                              f"{MESH_MAX_VERTICES_TOTAL:,} a map may have")
            take(vertex_count * 6, f"building {index}'s vertices")
            index_count = i32(f"building {index}'s index count")
            if index_count < 0 or index_count % 3 != 0:
                return None, f"has a building claiming {index_count:,} triangle indices"
            indices += index_count
            if indices // 3 > MESH_MAX_TRIANGLES:
                return None, (f"claims {indices // 3:,} triangles by building {index}, past the "
                              f"{MESH_MAX_TRIANGLES:,} a map may have")
            take(index_count * 4, f"building {index}'s indices")
    except EOFError as exc:
        return None, f"ends inside {exc.args[0]} - the file is truncated"

    if at != len(out):
        return None, f"carries {len(out) - at:,} byte(s) after its last building"

    return {
        "version": version,
        "extent": (min_x, min_z, max_x, max_z),
        "yMin": y_min, "yMax": y_max,
        "bands": bands,
        "cells": cells_total,
        "vertices": vertices,
        "triangles": indices // 3,
    }, None


def check_mesh(meta, folder, key, extent, levels, errors):
    """The mesh block against the file it names. Returns (its file name lower-cased or None, bytes).

    A meta with NO mesh block is the ordinary case and passes with nothing checked - the mesh is
    optional end to end, and a set without one draws flat on every client. A block that IS there is
    held to every number in it, because the file is binary: unlike a JPEG, nobody will ever notice by
    looking that it is the wrong one."""
    mesh = meta.get("mesh")
    if mesh is None:
        return None, 0
    if not isinstance(mesh, dict):
        errors.append(f"{key}: mesh is present but not an object ({mesh!r})")
        return None, 0

    where = f"{key}: mesh"

    rel = mesh.get("file")
    if not isinstance(rel, str) or not rel.strip():
        errors.append(f"{where}.file is missing or empty, so nothing says which file the mesh is")
        return None, 0
    parts = Path(rel.replace("\\", "/"))
    if parts.is_absolute() or len(parts.parts) != 1:
        errors.append(f"{where}.file {rel!r} is not a plain file name inside {folder.name}")
        return None, 0
    # The EXACT name, not merely the suffix: the host stores the file as <key>-mesh.bin and reads it back
    # through a regex that admits nothing else (MapStore.StoredMeshFileName), which a bare "-mesh.bin"
    # also fails. A set naming anything else ships and is then refused by the host it shipped to - and a
    # mesh borrowed from another map's folder is the case that matters, because it parses, its extent is
    # somebody else's, and nothing but the name says so.
    wanted = f"{key.lower()}{MESH_SUFFIX}"
    if parts.name.lower() != wanted:
        errors.append(f"{where}.file {rel!r} is not {wanted} - that exact name is what the layout gate, "
                      f"the host's store and the client all spell, and nothing else is read back")
    named = parts.name.lower()

    path = folder / parts
    if not path.is_file():
        errors.append(f"{where}.file {rel!r} does not exist in {folder} - the meta names a mesh the set "
                      f"does not carry, and the client refuses a set whose mesh is missing rather than "
                      f"drawing it flat. Re-run -RefreshMaps.")
        return named, 0

    size = path.stat().st_size
    claimed_bytes = mesh.get("bytes")
    if isinstance(claimed_bytes, bool) or not isinstance(claimed_bytes, int) or claimed_bytes <= 0:
        errors.append(f"{where}.bytes {claimed_bytes!r} is not a positive integer")
    elif claimed_bytes != size:
        errors.append(f"{where}.bytes says {claimed_bytes:,} but {rel} is {size:,} bytes on disk - the "
                      f"meta and the mesh are from different captures, or the file was truncated")

    claimed_sha = mesh.get("sha256")
    if not isinstance(claimed_sha, str) or len(claimed_sha) != 64 or \
            any(c not in "0123456789abcdefABCDEF" for c in claimed_sha):
        errors.append(f"{where}.sha256 {claimed_sha!r} is not 64 hex digits")
        claimed_sha = None
    else:
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual.lower() != claimed_sha.lower():
            errors.append(f"{where}: {rel} hashes to {actual[:16]} but the meta says "
                          f"{claimed_sha.lower()[:16]} - THIS IS THE STALE-MESH CASE: the file beside "
                          f"the meta is not the one that capture wrote, so it is a mesh of some other "
                          f"extent. Delete it and re-run -RefreshMaps.")

    header, why = mesh_header(path)
    if header is None:
        errors.append(f"{where}: {rel} {why}")
        return named, size

    claimed_version = mesh.get("version")
    if claimed_version != header["version"]:
        errors.append(f"{where}.version {claimed_version!r} is not the {header['version']} inside {rel}")

    # The extent is the whole reason a mesh can be checked at all without looking at it: the file's own
    # rectangle has to be the rectangle the pictures cover, to the double, or the relief is draped over
    # the wrong stretch of world and every building sits at an offset nothing else would reveal.
    if extent is not None:
        for name, mine, theirs in zip(("minX", "minZ", "maxX", "maxZ"), header["extent"], extent):
            if abs(mine - theirs) > EXTENT_TOLERANCE:
                errors.append(f"{where}: its {name} is {mine:.6f} but the meta's extent says "
                              f"{theirs:.6f} - the mesh and the pictures cover different rectangles")

    mesh_levels = sorted(b["level"] for b in header["bands"])
    if levels is not None and mesh_levels != sorted(levels):
        errors.append(f"{where}: its bands are levels {mesh_levels} but the meta's floors are "
                      f"{sorted(levels)} - a band with no picture cannot be textured, and a floor with "
                      f"no band has no ground to stand on")

    for field, inside in (("cells", header["cells"]), ("triangles", header["triangles"])):
        claimed = mesh.get(field)
        if isinstance(claimed, bool) or not isinstance(claimed, int):
            errors.append(f"{where}.{field} {claimed!r} is not an integer")
        elif claimed != inside:
            errors.append(f"{where}.{field} says {claimed:,} but {rel} holds {inside:,}")

    return named, size


SIDE_DIRS = ("N", "S", "E", "W")
SIDE_UNIT_TOLERANCE = 1e-3


def unit_vector(value):
    """Whether a JSON value is three numbers of length one, to SIDE_UNIT_TOLERANCE."""
    if not isinstance(value, list) or len(value) != 3:
        return False
    parts = [number(v) for v in value]
    if any(v is None for v in parts):
        return False
    return abs(math.sqrt(sum(v * v for v in parts)) - 1.0) <= SIDE_UNIT_TOLERANCE


def check_sides(meta, folder, key, errors):
    """The side pictures against their meta entries. Returns (named files lower-cased, count, bytes).

    A meta with no sides - absent, null or empty - is the ordinary case and passes with nothing
    checked: every set captured before sides existed has none, and the 3D view tints those walls."""
    sides = meta.get("sides")
    if sides is None:
        return set(), 0, 0
    if not isinstance(sides, list):
        errors.append(f"{key}: sides is present but not a list ({sides!r})")
        return set(), 0, 0

    named, seen, total = set(), set(), 0
    for index, side in enumerate(sides):
        where = f"{key}: sides[{index}]"
        if not isinstance(side, dict):
            errors.append(f"{where} is not an object")
            continue

        direction = side.get("dir")
        if direction not in SIDE_DIRS:
            errors.append(f"{where}.dir {direction!r} is not one of N, S, E, W")
            continue
        if direction in seen:
            errors.append(f"{key}: two {direction} sides - which picture textures those walls?")
            continue
        seen.add(direction)
        where = f"{key}: side {direction}"

        wanted = f"{key}-side-{direction}.jpg"
        rel = side.get("file")
        if not isinstance(rel, str) or rel != wanted:
            errors.append(f"{where}.file {rel!r} is not {wanted} - that exact name is what the host stores "
                          f"and reads back, and nothing else is")
            continue
        named.add(rel.lower())
        path = folder / rel
        if not path.is_file():
            errors.append(f"{where}.file {rel!r} does not exist in {folder} - the meta names a side the set "
                          f"does not carry. Re-run -RefreshMaps.")
            continue
        total += path.stat().st_size

        meta_w, meta_h = side.get("width"), side.get("height")
        if any(isinstance(v, bool) or not isinstance(v, int) or v <= 0 for v in (meta_w, meta_h)):
            errors.append(f"{where}: width/height {meta_w!r}x{meta_h!r} are not positive integers")
        else:
            actual, why = jpeg_size(path)
            if actual is None:
                errors.append(f"{where}: {rel} {why}")
            elif actual != (meta_w, meta_h):
                errors.append(f"{where}: {rel} is {actual[0]}x{actual[1]} px but its meta says "
                              f"{meta_w}x{meta_h} - every wall it textures would be placed a constant "
                              f"fraction off")

        px = number(side.get("pxPerMetre"))
        if px is None or px <= 0:
            errors.append(f"{where}.pxPerMetre {side.get('pxPerMetre')!r} is not a positive number")

        for axis in ("forward", "right", "up"):
            if not unit_vector(side.get(axis)):
                errors.append(f"{where}.{axis} {side.get(axis)!r} is not three numbers of length 1 "
                              f"(to {SIDE_UNIT_TOLERANCE:g}) - a skewed basis maps every wall wrong")

        for field in ("originR", "originU"):
            if number(side.get(field)) is None:
                errors.append(f"{where}.{field} is missing or not a number")

        y_min, y_max = number(side.get("yMin")), number(side.get("yMax"))
        if y_min is None or y_max is None or y_max <= y_min:
            errors.append(f"{where}: yMin/yMax {side.get('yMin')!r}..{side.get('yMax')!r} are not a height range")

    return named, len(seen), total


def load_json(path):
    """(object, None) or (None, reason). utf-8-sig: the client writes these files on Windows."""
    try:
        return json.loads(path.read_text(encoding="utf-8-sig")), None
    except OSError as exc:
        return None, f"cannot be read ({exc.strerror or exc})"
    except ValueError as exc:
        return None, f"is not valid JSON ({exc})"


def number(value):
    """A JSON number as a float, or None if absent or not a number. Rejects bool, which is an int in
    Python and would otherwise read as a perfectly good 0 or 1."""
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    return float(value)


def check_extent(meta, key, errors):
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


def check_floors(meta, folder, key, extent, px_per_metre, errors):
    """Validates every floor against its JPEG and the extent. Returns (named files, pixels, bytes,
    levels): the set of file names the meta claims, the first floor's "WxH" for the summary, total
    bytes, and the floor levels - which the mesh's bands are then held to."""
    floors = meta.get("floors")
    if not isinstance(floors, list) or not floors:
        errors.append(f"{key}: floors is missing, not a list, or empty - a set with no floor has no "
                      f"picture, so the Maps tab would fall back to the bare rectangle")
        return set(), "-", 0, []

    named, seen_levels, pixels, total = set(), [], None, 0
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
            errors.append(f"{where}: maxY {max_y:g} is not above minY {min_y:g} - the band is "
                          f"empty, so no world Y falls on this floor")

        # The image. Its name is a file BESIDE the meta; an absolute path, a subfolder or one that
        # climbs out of the key folder is refused rather than followed - the zip is assembled from
        # this folder, so a path that leaves it is a path that ships something else.
        rel = floor.get("file")
        if not isinstance(rel, str) or not rel.strip():
            errors.append(f"{where}.file is missing or empty")
            continue
        parts = Path(rel.replace("\\", "/"))
        if parts.is_absolute() or len(parts.parts) != 1:
            errors.append(f"{where}.file {rel!r} is not a plain file name inside {folder.name}")
            continue
        named.add(parts.name.lower())
        image = folder / parts
        if not image.is_file():
            errors.append(f"{where}.file {rel!r} does not exist in {folder} - the meta names a "
                          f"floor the set does not carry, so that floor draws as nothing")
            continue

        total += image.stat().st_size
        actual, why = jpeg_size(image)
        if actual is None:
            errors.append(f"{where}: {rel} {why}")
            continue
        image_w, image_h = actual
        if pixels is None:
            pixels = f"{image_w}x{image_h}"

        if meta_w is not None and (image_w, image_h) != (meta_w, meta_h):
            if image_w != meta_w:
                errors.append(f"{where}.width {meta_w} does not match {rel}'s frame width {image_w}")
            if image_h != meta_h:
                errors.append(f"{where}.height {meta_h} does not match {rel}'s frame height "
                              f"{image_h}")

        # The arithmetic the UI does in reverse: the meta's own numbers have to be the extent's
        # span at the meta's scale, or every pin sits a constant fraction off across the picture.
        if extent is not None and px_per_metre and meta_w is not None:
            min_x, min_z, max_x, max_z = extent
            want_w = math.ceil((max_x - min_x) * px_per_metre)
            want_h = math.ceil((max_z - min_z) * px_per_metre)
            if abs(meta_w - want_w) > PIXEL_TOLERANCE:
                errors.append(f"{where}: width {meta_w} px but the extent's {max_x - min_x:g} m at "
                              f"{px_per_metre:g} px/m wants {want_w}")
            if abs(meta_h - want_h) > PIXEL_TOLERANCE:
                errors.append(f"{where}: height {meta_h} px but the extent's {max_z - min_z:g} m at "
                              f"{px_per_metre:g} px/m wants {want_h}")

    return named, pixels or "-", total, seen_levels


def check_set(folder, schema, errors):
    """Validates one <key>\\ folder. Returns its summary line, or None if there was nothing to read."""
    key = folder.name
    metas = [p for p in sorted(folder.iterdir())
             if p.is_file() and p.name.lower().endswith(META_SUFFIX)]
    if not metas:
        errors.append(f"{key}: no {key}{META_SUFFIX} in {folder} - a folder of pictures with no meta "
                      f"is bytes the client cannot place on the world, so it would ship and be "
                      f"ignored")
        return None
    if len(metas) > 1:
        errors.append(f"{key}: {len(metas)} *{META_SUFFIX} files in {folder} "
                      f"({', '.join(p.name for p in metas)}) - which one is the set?")
        return None
    meta_path = metas[0]
    if meta_path.name[:-len(META_SUFFIX)].lower() != key.lower():
        errors.append(f"{key}: the meta is named {meta_path.name}, not {key}{META_SUFFIX} - the "
                      f"client looks it up by folder name")

    meta, why = load_json(meta_path)
    if meta is None:
        errors.append(f"{key}: {meta_path.name} {why}")
        return None
    if not isinstance(meta, dict):
        errors.append(f"{key}: {meta_path.name} is not a JSON object")
        return None

    # First, because every field below is read in the shape this version defines. A set stamped
    # anything else is one the shipped client SKIPS - it checks the same constant - so shipping it
    # would be shipping megabytes that draw nothing.
    stamped = meta.get("schemaVersion")
    if isinstance(stamped, bool) or stamped != schema:
        errors.append(f"{key}: schemaVersion {stamped!r} is not {schema}, the "
                      f"SupportedCaptureSchema of the client being packaged - that client skips "
                      f"this set, so nothing below has been checked either")
        return None

    name = meta.get("map")
    if not isinstance(name, str) or name.lower() != key.lower():
        errors.append(f"{key}: map {name!r} is not the folder name {key!r} - the client keys the set "
                      f"by folder, so this one would be drawn on the wrong map")

    px_per_metre = number(meta.get("pxPerMetre"))
    if px_per_metre is None or px_per_metre <= 0:
        errors.append(f"{key}: pxPerMetre {meta.get('pxPerMetre')!r} is missing or not a positive "
                      f"number - nothing can be scaled without it")
        px_per_metre = None

    extent = check_extent(meta, key, errors)
    named, pixels, total, levels = check_floors(meta, folder, key, extent, px_per_metre, errors)
    mesh_named, mesh_bytes = check_mesh(meta, folder, key, extent, levels, errors)

    # The sides' names join the floors' for the orphan rule below: a side JPEG the meta does not name is
    # dead weight exactly as an unnamed floor is, and it is what a re-capture that drew fewer sides leaves.
    side_named, side_count, side_bytes = check_sides(meta, folder, key, errors)
    named |= side_named

    orphans = sorted(p.name for p in folder.iterdir()
                     if p.is_file() and p.name.lower().endswith(".jpg")
                     and p.name.lower() not in named)
    if orphans:
        errors.append(f"{key}: {', '.join(orphans)} - image(s) no floor or side in the meta names. They "
                      f"would ship as megabytes nothing loads; a re-capture with fewer floors "
                      f"leaves exactly this behind. Delete them or re-run -RefreshMaps.")

    # The same rule for the mesh, and it bites harder: a *-mesh.bin the meta does not name is either a
    # mesh from an older capture of this map (wrong extent, and the sha256 above never sees it because
    # the meta points elsewhere) or a mesh of another map entirely. Either way it ships as megabytes
    # nothing loads.
    stray_meshes = sorted(p.name for p in folder.iterdir()
                          if p.is_file() and p.name.lower().endswith(MESH_SUFFIX)
                          and p.name.lower() != mesh_named)
    if stray_meshes:
        errors.append(f"{key}: {', '.join(stray_meshes)} - mesh file(s) the meta does not name"
                      f"{' (its meta names no mesh at all)' if mesh_named is None else ''}. A mesh from "
                      f"an earlier capture is a mesh of a different extent and nothing would draw it. "
                      f"Delete them or re-run -RefreshMaps.")

    floor_count = len(meta["floors"]) if isinstance(meta.get("floors"), list) else 0
    scale = f"{1 / px_per_metre:.2f} m/px" if px_per_metre else "? m/px"
    mesh_note = f", mesh {mesh_bytes / 1048576:.1f} MB" if mesh_named is not None else ", no mesh"
    mesh_note += f", {side_count} side(s) {side_bytes / 1048576:.1f} MB" if side_count else ", no sides"
    return (f"{key}: {floor_count} floor(s), {pixels} @ {scale}, {total / 1048576:.1f} MB{mesh_note}, "
            f"captured {meta.get('capturedAt') or '?'}")


def main():
    parser = argparse.ArgumentParser(add_help=True)
    parser.add_argument("root", help="the maps folder the release is assembled from")
    parser.add_argument("--schema", type=int, required=True,
                        help="the client's SupportedCaptureSchema, read by package.ps1")
    args = parser.parse_args()

    root = Path(args.root)
    if not root.is_dir():
        print(f"MAPS PACK CHECK FAILED: no maps folder at {root}")
        return 1

    folders = [p for p in sorted(root.iterdir()) if p.is_dir()]
    errors, lines = [], []

    for folder in folders:
        line = check_set(folder, args.schema, errors)
        if line is not None:
            lines.append(line)

    print(f"maps:   {root}")
    print(f"schema: {args.schema}")
    print("-" * 78)
    if not folders:
        print("  (no map folders - nothing to check)")
    for line in lines:
        print(f"  {line}")
    print()

    for e in errors:
        print(f"ERROR  {e}")
    if errors:
        print()

    print(f"{len(folders)} map set(s) checked, {len(errors)} problem(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
