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

Every folder it finds is checked the same way, whether it is a vanilla map or a modded one the
maintainer chose to ship.

What it does NOT check, by design:
  - the pixels. Whether the JPEG is the right map, the right way up, or a grey rectangle is what the
    eye is for; this proves only that its dimensions are the ones the meta and the extent imply. It
    does not decode the image at all - the SOF header is read out of the file's own bytes.
  - the extent against the harvested zone file (that is check-capture.py's job, and it needs an
    install), rotation, tileSize, labels, timeOfDay, modVersion, or file sizes (package.ps1 owns the
    1.5 MB per image and 40 MB total gates, because it is the one building the zip).
  - WHICH maps are here. Coverage of the 11 vanilla maps is package.ps1's warning, not an error:
    DynamicMaps stays a selectable picture source, so a map with no set falls back rather than
    breaking, and the release ships whatever sets exist. This script only says whether the sets that
    ARE here are shippable.

Proven able to fail before it shipped: against generated fakes it exits 1 naming the map and the
field for a schemaVersion 2 meta, for a floor whose file is absent, for an orphan image no floor
names, for a PNG under a .jpg name, and for a meta width 3 px from the extent's arithmetic, and
exits 0 on an intact set - of 11 keys or of 4.

Usage:  python tools/check-maps-pack.py <maps-root> --schema N
"""

import argparse
import json
import math
import struct
import sys
from pathlib import Path

PIXEL_TOLERANCE = 1     # px, on each axis, against ceil(span * pxPerMetre)
META_SUFFIX = ".map.json"

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
    """Validates every floor against its JPEG and the extent. Returns (named files, pixels, bytes):
    the set of file names the meta claims, the first floor's "WxH" for the summary, total bytes."""
    floors = meta.get("floors")
    if not isinstance(floors, list) or not floors:
        errors.append(f"{key}: floors is missing, not a list, or empty - a set with no floor has no "
                      f"picture, so the Maps tab would fall back to the bare rectangle")
        return set(), "-", 0

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

    return named, pixels or "-", total


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
    named, pixels, total = check_floors(meta, folder, key, extent, px_per_metre, errors)

    orphans = sorted(p.name for p in folder.iterdir()
                     if p.is_file() and p.name.lower().endswith(".jpg")
                     and p.name.lower() not in named)
    if orphans:
        errors.append(f"{key}: {', '.join(orphans)} - image(s) no floor in the meta names. They "
                      f"would ship as megabytes nothing loads; a re-capture with fewer floors "
                      f"leaves exactly this behind. Delete them or re-run -RefreshMaps.")

    floor_count = len(meta["floors"]) if isinstance(meta.get("floors"), list) else 0
    scale = f"{1 / px_per_metre:.2f} m/px" if px_per_metre else "? m/px"
    return (f"{key}: {floor_count} floor(s), {pixels} @ {scale}, {total / 1048576:.1f} MB, "
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
