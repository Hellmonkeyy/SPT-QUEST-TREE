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

Usage:  python tools/check-capture.py [captures-root] [zones-folder]
        defaults: C:\\Games\\SPT\\BepInEx\\plugins\\QuestTree\\captures
                  C:\\Games\\SPT\\SPT_Runtime\\user\\mods\\QuestTree\\zones
"""

import json
import math
import struct
import sys
from pathlib import Path

CAPTURES = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(
    r"C:\Games\SPT\BepInEx\plugins\QuestTree\captures")
ZONES = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(
    r"C:\Games\SPT\SPT_Runtime\user\mods\QuestTree\zones")

SCHEMA_VERSION = 1     # the capture-meta shape this script reads
MAX_PNG_BYTES = 48 * 1024 * 1024  # MapCapture.MaxFloorPngBytes: 0.25 m/px floors run 10-25 MB
PIXEL_TOLERANCE = 1     # px, on each axis, against ceil(span * pxPerMetre)
EDGE_TOLERANCE = 0.5    # m, on each of the four edges, against the zone file's extent
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"


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


def check_floors(meta, folder, key, extent, px_per_metre, errors):
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
    levels, pixels, total = check_floors(meta, folder, key, extent, px_per_metre, errors)

    zones, rotation = check_zone(key, extent, levels, errors, warnings)
    meta_rotation = number(meta.get("rotation"))
    if rotation is not None and meta_rotation is not None and abs(meta_rotation - rotation) > 0.01:
        warnings.append(f"{key}: rotation {meta_rotation:g} differs from the zone file's "
                        f"{rotation:g} - the picture and the extent were measured at different angles")

    floor_count = len(meta["floors"]) if isinstance(meta.get("floors"), list) else 0
    scale = f"{1 / px_per_metre:.2f} m/px" if px_per_metre else "? m/px"
    captured = meta.get("capturedAt") or "?"
    return (f"{key}: {floor_count} floor(s), {pixels} @ {scale}, {total / 1048576:.1f} MB, {zones}",
            captured)


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
