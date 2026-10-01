"""Splits a shipped 3D mesh into parts for the repo, and joins them back.

GitHub refuses a file over 100 MB in a push, and a stored mesh (<key>-mesh.bin) may now be up to 180 MiB
(MapMeshBuilder.ShippedMeshBytes). So package.ps1 -RefreshMaps writes any mesh over SHIP_PART_BYTES into
Source\\Tarkov-QuestTree-Server\\maps\\<key>\\ as

    <key>-mesh.bin.part00, <key>-mesh.bin.part01, ...   each at most SHIP_PART_BYTES
    <key>-mesh.bin.parts.json                           the manifest

and never the whole file. The manifest names the whole file, its byte count and sha256 (which equal the meta's
mesh.bytes and mesh.sha256), and each part's name and byte count in order. The host joins the parts back into
<key>-mesh.bin the first time it reads the set (MapStore.JoinShippedMesh), verifying the same sha256; a mesh
that fits in one part stays a single file exactly as before.

The part names are DERIVED (<file>.partNN, NN from 00), never taken from the manifest: a manifest naming
"..\\other" or a part out of order is refused, here and on the host alike.

Usage:
  python tools/mesh_parts.py split <mesh.bin> <dest-dir> [--meta META] [--part-bytes N]
  python tools/mesh_parts.py check <dest-dir> <file-name>     (join in memory and verify)
  python tools/mesh_parts.py --self-test
"""

import argparse
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

# 90 MiB: under GitHub's 100,000,000-byte refusal with room. package.ps1's $shipPartBytes and MapStore's
# ShippedMeshPartBytes are the same number; change all three together.
SHIP_PART_BYTES = 90 * 1024 * 1024
# Two digits in the name, so at most 100 parts; at 90 MiB that is far past the 512 MiB any host takes.
MAX_PARTS = 100
MANIFEST_SUFFIX = ".parts.json"


def part_name(whole_name, index):
    return f"{whole_name}.part{index:02d}"


def manifest_name(whole_name):
    return whole_name + MANIFEST_SUFFIX


def _write_atomic(path, data):
    temp = path.with_name(path.name + ".tmp")
    temp.write_bytes(data)
    os.replace(temp, path)


def split(src, dest_dir, part_bytes=SHIP_PART_BYTES, expect_sha=None, expect_bytes=None):
    """Writes src as parts plus a manifest into dest_dir. Returns the manifest dict; raises ValueError when the
    whole file is not the one expected or the written parts do not join back to it."""
    src, dest_dir = Path(src), Path(dest_dir)
    if part_bytes <= 0:
        raise ValueError(f"part size {part_bytes} is not positive")
    data = src.read_bytes()
    if not data:
        raise ValueError(f"{src} is empty")
    sha = hashlib.sha256(data).hexdigest()
    if expect_sha is not None and sha != str(expect_sha).lower():
        raise ValueError(f"{src.name} hashes to {sha[:16]}, not the {str(expect_sha).lower()[:16]} its meta names")
    if expect_bytes is not None and len(data) != expect_bytes:
        raise ValueError(f"{src.name} is {len(data):,} bytes, not the {expect_bytes:,} its meta names")
    count = (len(data) + part_bytes - 1) // part_bytes
    if count > MAX_PARTS:
        raise ValueError(f"{src.name} would take {count} parts, past the {MAX_PARTS} a name can number")

    whole = src.name
    parts = []
    for index in range(count):
        chunk = data[index * part_bytes:(index + 1) * part_bytes]
        name = part_name(whole, index)
        _write_atomic(dest_dir / name, chunk)
        parts.append({"file": name, "bytes": len(chunk)})

    manifest = {"file": whole, "bytes": len(data), "sha256": sha, "parts": parts}
    _write_atomic(dest_dir / manifest_name(whole),
                  (json.dumps(manifest, indent=2) + "\n").encode("utf-8"))

    # Read back from disk, not from memory: the check is that what was WRITTEN joins to the mesh.
    joined, _, why = join_verified(dest_dir, whole, part_bytes)
    if joined is None:
        raise ValueError(f"the parts written for {whole} do not join back: {why}")
    return manifest


def join_verified(folder, whole_name, part_bytes=SHIP_PART_BYTES):
    """(bytes, manifest, None) or (None, manifest-or-None, reason): the parts named by <whole>.parts.json in
    folder, joined in order and held to every number in the manifest. The part names are derived, not read."""
    folder = Path(folder)
    path = folder / manifest_name(whole_name)
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        return None, None, f"{path.name} cannot be read as JSON ({exc})"
    if not isinstance(manifest, dict):
        return None, None, f"{path.name} is not a JSON object"

    if manifest.get("file") != whole_name:
        return None, manifest, f"{path.name} names {manifest.get('file')!r}, not {whole_name}"
    total = manifest.get("bytes")
    if isinstance(total, bool) or not isinstance(total, int) or total <= 0:
        return None, manifest, f"{path.name} bytes {total!r} is not a positive integer"
    sha = manifest.get("sha256")
    if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdefABCDEF" for c in sha):
        return None, manifest, f"{path.name} sha256 {sha!r} is not 64 hex digits"
    parts = manifest.get("parts")
    if not isinstance(parts, list) or not parts:
        return None, manifest, f"{path.name} names no parts"
    if len(parts) > MAX_PARTS:
        return None, manifest, f"{path.name} names {len(parts)} parts, past the {MAX_PARTS} a name can number"

    out = bytearray()
    for index, part in enumerate(parts):
        wanted = part_name(whole_name, index)
        if not isinstance(part, dict) or part.get("file") != wanted:
            return None, manifest, f"{path.name} part {index} is {part!r}, not {wanted}"
        size = part.get("bytes")
        if isinstance(size, bool) or not isinstance(size, int) or size <= 0 or size > part_bytes:
            return None, manifest, f"{wanted} is declared at {size!r} bytes, not 1..{part_bytes:,}"
        part_path = folder / wanted
        if not part_path.is_file():
            return None, manifest, f"{wanted} is missing"
        chunk = part_path.read_bytes()
        if len(chunk) != size:
            return None, manifest, f"{wanted} is {len(chunk):,} bytes on disk, not the {size:,} {path.name} names"
        out += chunk

    if len(out) != total:
        return None, manifest, f"the parts join to {len(out):,} bytes, not the {total:,} {path.name} names"
    actual = hashlib.sha256(out).hexdigest()
    if actual != sha.lower():
        return None, manifest, f"the parts join to sha256 {actual[:16]}, not the {sha.lower()[:16]} {path.name} names"
    return bytes(out), manifest, None


def self_test():
    """Split, join, compare the sha - and prove each refusal can fire. Returns the number of failures."""
    failures = 0

    def expect(ok, what):
        nonlocal failures
        print(f"  {'ok  ' if ok else 'FAIL'} {what}")
        if not ok:
            failures += 1

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)

        def fresh(name, data):
            folder = tmp / name
            folder.mkdir()
            src = tmp / f"{name}-src"
            src.mkdir()
            whole = src / "testmap-mesh.bin"
            whole.write_bytes(data)
            return folder, whole

        # Round trips at a small part size: uneven, exact multiple, one part, one byte.
        for label, size, part in (("uneven", 3_500, 1_000), ("exact", 4_000, 1_000),
                                  ("one part", 999, 1_000), ("one byte", 1, 1_000)):
            data = os.urandom(size)
            folder, whole = fresh(label.replace(" ", "-"), data)
            manifest = split(whole, folder, part, hashlib.sha256(data).hexdigest(), size)
            joined, _, why = join_verified(folder, whole.name, part)
            expect(joined is not None and hashlib.sha256(joined).hexdigest() == hashlib.sha256(data).hexdigest()
                   and len(manifest["parts"]) == (size + part - 1) // part
                   and all((folder / p["file"]).stat().st_size <= part for p in manifest["parts"]),
                   f"round trip, {label}: {size:,} bytes in {len(manifest['parts'])} part(s) of <= {part:,} ({why or 'sha equal'})")

        # At the real size: a 180 MiB mesh in 90 MiB parts.
        size = 180 * 1024 * 1024 + 12_345
        data = os.urandom(size)
        folder, whole = fresh("real", data)
        manifest = split(whole, folder, SHIP_PART_BYTES)
        joined, _, why = join_verified(folder, whole.name)
        expect(joined is not None and hashlib.sha256(joined).hexdigest() == manifest["sha256"] == hashlib.sha256(data).hexdigest()
               and len(manifest["parts"]) == 3
               and max(p["bytes"] for p in manifest["parts"]) <= SHIP_PART_BYTES < 100_000_000,
               f"round trip, real size: {size:,} bytes in {len(manifest['parts'])} parts of <= {SHIP_PART_BYTES:,} ({why or 'sha equal'})")
        del data, joined

        # Each refusal, one fault at a time, on a fresh small split.
        def faulted(label, fault, expect_text):
            data = os.urandom(2_500)
            folder, whole = fresh(f"fault-{label}", data)
            split(whole, folder, 1_000)
            fault(folder, whole.name)
            joined, _, why = join_verified(folder, whole.name, 1_000)
            expect(joined is None and why is not None and expect_text in why, f"refuses {label}: {why}")

        def flip(folder, name):
            p = folder / part_name(name, 1)
            b = bytearray(p.read_bytes())
            b[0] ^= 0xFF
            p.write_bytes(bytes(b))

        def edit(change):
            def run(folder, name):
                p = folder / manifest_name(name)
                m = json.loads(p.read_text(encoding="utf-8"))
                change(m)
                p.write_text(json.dumps(m), encoding="utf-8")
            return run

        def grow(m):
            m["parts"][0]["bytes"] = 2_000

        def rename(m):
            m["parts"][0]["file"] = "..\\other-mesh.bin.part00"

        def drop_last(m):
            m["parts"].pop()

        def bad_sha(m):
            m["sha256"] = "0" * 64

        faulted("a corrupted part", flip, "sha256")
        faulted("a missing part", lambda f, n: (f / part_name(n, 2)).unlink(), "missing")
        faulted("a truncated part", lambda f, n: (f / part_name(n, 0)).write_bytes(b"x"), "on disk")
        faulted("a part over the part size", edit(grow), "not 1..")
        faulted("a part name not derived", edit(rename), "not testmap-mesh.bin.part00")
        faulted("a dropped part", edit(drop_last), "join to")
        faulted("a wrong sha in the manifest", edit(bad_sha), "sha256")

        # And the split's own refusal of a whole file that is not the meta's.
        data = os.urandom(1_500)
        folder, whole = fresh("meta-mismatch", data)
        try:
            split(whole, folder, 1_000, expect_sha="1" * 64)
            expect(False, "refuses a whole file whose sha is not the meta's")
        except ValueError as exc:
            expect("meta names" in str(exc) and not any(folder.iterdir()),
                   f"refuses a whole file whose sha is not the meta's, writing nothing: {exc}")

    print(f"mesh_parts self-test: {'PASSED' if failures == 0 else f'{failures} FAILURE(S)'}")
    return failures


def main():
    if len(sys.argv) == 2 and sys.argv[1] == "--self-test":
        return 1 if self_test() else 0

    parser = argparse.ArgumentParser(add_help=True)
    sub = parser.add_subparsers(dest="command", required=True)
    s = sub.add_parser("split", help="write a mesh as parts plus a manifest")
    s.add_argument("mesh")
    s.add_argument("dest")
    s.add_argument("--meta", default=None, help="the set's .map.json: the whole file must be its mesh.sha256 and mesh.bytes")
    s.add_argument("--part-bytes", type=int, default=SHIP_PART_BYTES)
    c = sub.add_parser("check", help="join a manifest's parts in memory and verify them")
    c.add_argument("dest")
    c.add_argument("file")
    c.add_argument("--part-bytes", type=int, default=SHIP_PART_BYTES)
    args = parser.parse_args()

    if args.command == "split":
        expect_sha = expect_bytes = None
        if args.meta is not None:
            meta = json.loads(Path(args.meta).read_text(encoding="utf-8-sig"))
            mesh = meta.get("mesh") if isinstance(meta, dict) else None
            if not isinstance(mesh, dict) or mesh.get("file") != Path(args.mesh).name:
                print(f"SPLIT REFUSED: {args.meta} does not name {Path(args.mesh).name} as its mesh")
                return 1
            expect_sha, expect_bytes = mesh.get("sha256"), mesh.get("bytes")
            if not isinstance(expect_sha, str) or not isinstance(expect_bytes, int):
                print(f"SPLIT REFUSED: {args.meta} has no usable mesh.sha256 and mesh.bytes")
                return 1
        try:
            manifest = split(args.mesh, args.dest, args.part_bytes, expect_sha, expect_bytes)
        except (OSError, ValueError) as exc:
            print(f"SPLIT REFUSED: {exc}")
            return 1
        print(f"  split {manifest['file']} ({manifest['bytes']:,} bytes) into {len(manifest['parts'])} part(s), "
              f"sha256 {manifest['sha256'][:16]} verified")
        return 0

    joined, _, why = join_verified(args.dest, args.file, args.part_bytes)
    if joined is None:
        print(f"JOIN FAILED: {why}")
        return 1
    print(f"  {args.file}: {len(joined):,} bytes, sha256 {hashlib.sha256(joined).hexdigest()[:16]} - the parts join")
    return 0


if __name__ == "__main__":
    sys.exit(main())
