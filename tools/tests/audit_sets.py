"""Layer A: offline audits of the captured map sets - the client's captures folder and the host's maps folder.

Every check here catches a defect that was found in testing and that no other check looked for. The readers are
check-capture.py's (read_mesh, png_size) and check-maps-pack.py's (jpeg_size); nothing is re-implemented. Nothing under
the install is ever written: --self-test builds its fixtures in a temp folder.

  black-faces     (a) the area of building faces the 3D view textures from a floor picture at a texel that is clear and
                  near-black - drawn as black blobs, since the roof material does not clip. The viewer's CURRENT routing
                  (Map3DView FloorForFace / FloorThatDrew / CaptureWindow, mirrored below) is simulated face by face on the
                  stored mesh and the picture the viewer draws (the viewing copy when there is one). FAIL over
                  BLACK_FACE_SHARE of the roof area. The detail also gives the pre-fix rule's area (in-band nearest, else
                  the filed floor), so the margin the fix bought stays on record. Host JPEG sets are skipped (no alpha).
  extent          (b) the extent is finite and non-degenerate, and each floor picture is ceil(span x px/m) within the
                  tolerance check-capture (client, 1 px) and check-maps-pack (host, 2 px) use, its file header agreeing.
  floors          (c) at most WARN_FLOORS floors (WARN above); no floor band thinner than MIN_BAND_METRES (FAIL).
  mesh-heights    (d) no building's vertices reach more than OUT_OF_GROUND_METRES past the ground (every band's
                  minY..maxY joined with the relief's hits - MapMeshBuilder.OutOfGroundMetres), and no building vertex sits
                  on the y quantisation's end codes (0 or 0xFFFE): the range has 5 m slack, so a vertex there was clamped.
  view-copies     (e) a floor picture longer than VIEW_PICTURE_SIDE has its viewing copy, of MapCapture.ViewSize's size,
                  sides multiples of 4.
  dxt-safety      (f) every texture the viewer block-compresses (floor or viewing copy, side, atlas tile - compressed only
                  when both sides are multiples of 4) is a multiple of 4 at mip 1 too, or a global mip limit drops into a
                  size D3D11 refuses (E_INVALIDARG, then a native crash). Only mip 1 failing PASSes while both texture
                  sites set ignoreMipmapLimit (the dxt-mip-limit-guard row, a source grep that FAILs when one stops), else
                  WARNs; a client picture or tile that is not 4-aligned at mip 0 is a FAIL (the capture aligns every one,
                  so it would silently stay uncompressed). Host copies are not aligned by design and are noted.
  menu-sidecars   (g) a menu set (capturedIn "menu") has the distance sidecar beside every floor and side - the step-0
                  pixels a raid merge must lose to - at the picture's size; the small ones are decoded and must hold
                  step-0 pixels. check-capture only warns about a missing side sidecar and never looks at a floor's.
  backups         (h) the size of the set-aside copies (<key>.bak-<time>, <key>.old-*) per map. WARN over BACKUP_WARN_BYTES.

Speed: each distinct mesh (by the meta's sha256) is read once, for the client and the host set alike. Nothing is sampled:
every triangle of every mesh is classified. The full run is mostly the mesh reads and the picture decodes.
"""

import json
import math
import os
import shutil
import struct
import sys
import tempfile
import time
import zlib
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from testlib import FAIL, PASS, SKIP, WARN, Context, Result, load_tool  # noqa: E402

LAYER = "sets"

# --- thresholds ----------------------------------------------------------------------------------------------------
# (a) Share of the roof area (faces the viewer textures from a floor picture) allowed to sample a clear, near-black texel.
# Measured 2026-10-04 on the stored client sets with the current rule (viewer-faithful: drawn-copy density, cut-out tiles):
# Woods 1.42 %, RezervBase 1.15 %, factory 1.97 %, Sandbox 0.98 %, Shoreline 3.86 %, the rest under 0.1 %; with the
# pre-fix rule Woods 22.4 %, RezervBase 21.4 %. FAIL over 5 % (pre-fix Woods and Reserve fail by 4x, the current sets pass),
# WARN over 3 % (Shoreline: 1,800 m2 of off-band roofs on clear black). The reviewer's 3,535 / 48,483 m2 for Woods is the
# same rule over non-atlas, off-band faces with |n.y| >= 0.5 on the full picture: the viewing copy (what is drawn) gives
# 3,202 / 48,014, dropping the downward faces (tinted when there are side pictures) 1,569 / 34,653, adding the in-band
# faces 2,243 / 35,327 - this check's figures.
BLACK_FACE_SHARE = 0.05
BLACK_FACE_WARN_SHARE = 0.03
BLACK_RGB_MAX = 20            # a texel is near-black when max(R, G, B) < this (the reviewer's simulation)
HOST_BACKDROP = (43, 46, 48)  # MapTransfer.BackdropFill: what a host JPEG holds where the capture was clear
HOST_BACKDROP_TOLERANCE = 6   # per channel, for the JPEG's noise
# (b)
MIN_SPAN_METRES = 10.0
MAX_SPAN_METRES = 20000.0
CLIENT_PIXEL_TOLERANCE = 1    # check-capture PIXEL_TOLERANCE
HOST_PIXEL_TOLERANCE = 2      # check-maps-pack PIXEL_TOLERANCE (MapStore.PixelTolerance)
# (c)
WARN_FLOORS = 8               # MapMeshFile's band cap (MESH_MAX_BANDS); Shoreline and Icebreaker have 8
MIN_BAND_METRES = 1.0         # the thinnest real band is 1.5 m (Icebreaker, Shoreline)
# (d)
OUT_OF_GROUND_METRES = 300.0  # MapMeshBuilder.OutOfGroundMetres
# (e)
VIEW_PICTURE_SIDE = 8192      # MapCapture.ViewPictureSide
# (h)
BACKUP_WARN_BYTES = 5 * 1024 ** 3

# Viewer constants mirrored from Map3DView.cs (change with it)
FLOOR_FACE_SLACK = 0.5        # FloorFaceSlack
CAPTURE_TOP_CAMERA_HEIGHT = 300.0
CAPTURE_CEILING_CLEARANCE = 0.5
CAPTURE_FAR_CLIP_SLACK = 1.0
CAPTURE_TOP_DEPTH_BELOW = 50.0
ROOF_NORMAL_Y = 0.5           # RoofNormalY
ROOF_PICTURE_MIN_PPM = 6.0    # RoofPictureMinPpm
CLIP_ALPHA = 128              # TileStore.ClipAlpha
GROUND_SKIRT_RISE = 1.0
GROUND_SKIRT_TOLERANCE = 0.3
PLACEHOLDER_Y = 1000.0        # MeasureFloorRanges drops a band not inside +-1000 m
NO_PICTURE = -10 ** 6         # Prep.NoPictureFloor
MAX_QUANTISED = 0xFFFE
NO_HIT = 0xFFFF
DIST_DECODE_MAX_PIXELS = 40_000_000   # (g) decode a sidecar's content only up to this many pixels


def _np():
    import numpy
    return numpy


def _pil():
    from PIL import Image
    Image.MAX_IMAGE_PIXELS = None
    return Image


# --- discovery -------------------------------------------------------------------------------------------------------

def is_backup(name):
    return ".bak-" in name or ".old-" in name


def find_sets(root, ctx):
    """(sets, empty): sets as (key, folder, meta path) for every folder under root holding a <key>.map.json, backups and
    dot-folders excluded; empty is the folders with no meta."""
    sets, empty = [], []
    if root is None or not Path(root).is_dir():
        return sets, empty
    for folder in sorted(Path(root).iterdir(), key=lambda p: p.name.lower()):
        if not folder.is_dir() or folder.name.startswith(".") or is_backup(folder.name):
            continue
        if not ctx.wants(folder.name):
            continue
        meta = folder / f"{folder.name}.map.json"
        if not meta.is_file():
            metas = sorted(folder.glob("*.map.json"))
            meta = metas[0] if metas else None
        if meta is None:
            empty.append(folder.name)
        else:
            sets.append((folder.name, folder, meta))
    return sets, empty


class MapSet:
    def __init__(self, side, key, folder, meta_path):
        self.side, self.key, self.folder, self.meta_path = side, key, Path(folder), Path(meta_path)
        self.where = f"{side}/{key}"
        self.meta = None
        self.error = None
        try:
            self.meta = json.loads(self.meta_path.read_text(encoding="utf-8-sig"))
            if not isinstance(self.meta, dict):
                raise ValueError("not a JSON object")
        except (OSError, ValueError) as exc:
            self.error = f"{self.meta_path.name} does not parse: {exc}"

    @property
    def host(self):
        return self.side == "host"

    def floors(self):
        floors = self.meta.get("floors") if self.meta else None
        return [f for f in floors if isinstance(f, dict)] if isinstance(floors, list) else []

    def sides(self):
        sides = self.meta.get("sides") if self.meta else None
        return [s for s in sides if isinstance(s, dict)] if isinstance(sides, list) else []

    def mesh_entry(self):
        mesh = self.meta.get("mesh") if self.meta else None
        return mesh if isinstance(mesh, dict) and isinstance(mesh.get("file"), str) else None

    def mesh_id(self):
        entry = self.mesh_entry()
        if entry is None:
            return None
        return entry.get("sha256") or str(self.folder / entry["file"])

    def extent(self):
        e = self.meta.get("extent") if self.meta else None
        if not isinstance(e, dict):
            return None
        try:
            values = tuple(float(e[k]) for k in ("minX", "minZ", "maxX", "maxZ"))
        except (KeyError, TypeError, ValueError):
            return None
        return values

    def ppm(self):
        try:
            return float(self.meta.get("pxPerMetre"))
        except (TypeError, ValueError):
            return None


def picture_size(path):
    """(width, height) from a PNG's IHDR or a JPEG's SOF, or None."""
    path = Path(path)
    if not path.is_file():
        return None
    if path.suffix.lower() == ".png":
        size, _why = load_tool("check-capture.py").png_size(path)
        return size
    try:
        size, _why = load_tool("check-maps-pack.py").jpeg_size(path)
        return size
    except Exception:
        return None


# --- (a) the viewer's floor routing, mirrored ---------------------------------------------------------------------------

class Routing:
    """Map3DView.Prep's FloorRanges, capture windows and FloorForFace, for one set and one mesh's bands."""

    def __init__(self, floors, band_levels):
        self.ranges = []   # (level, low, high): the declared band plus the slack, as MeasureFloorRanges
        for f in floors:
            level = f.get("level")
            if level not in band_levels:
                continue
            try:
                lo, hi = float(f["minY"]), float(f["maxY"])
            except (KeyError, TypeError, ValueError):
                continue
            if not (lo > -PLACEHOLDER_Y and hi < PLACEHOLDER_Y and hi >= lo):
                continue
            self.ranges.append((level, lo - FLOOR_FACE_SLACK, hi + FLOOR_FACE_SLACK))
        self.windows = []
        self.top = -1
        for i, (level, low, high) in enumerate(self.ranges):
            min_y = low + FLOOR_FACE_SLACK
            next_min = math.inf
            for j, (_l, low_j, _h) in enumerate(self.ranges):
                lj = low_j + FLOOR_FACE_SLACK
                if j != i and min_y < lj < next_min:
                    next_min = lj
            self.windows.append(capture_window(min_y, high - FLOOR_FACE_SLACK, next_min))
            if self.top < 0 or low > self.ranges[self.top][1] or (low == self.ranges[self.top][1] and level > self.ranges[self.top][0]):
                self.top = i

    def floor_for(self, y, filed, prefix=False):
        """Vectorised FloorForFace over arrays y and filed. prefix=True is the rule before 5360921: on no floor's band the
        filed floor, whatever its camera drew."""
        np = _np()
        best = filed.astype(np.int64).copy()
        best_d = np.full(y.shape, np.inf)
        for level, low, high in self.ranges:
            inside = (y >= low) & (y <= high)
            d = np.maximum(0.0, np.maximum(low + FLOOR_FACE_SLACK - y, y - (high - FLOOR_FACE_SLACK)))
            better = inside & ((d < best_d) | ((np.abs(d - best_d) < 1e-6) & (level > best)))
            best = np.where(better, level, best)
            best_d = np.where(better, d, best_d)
        off = ~np.isfinite(best_d)
        if prefix or not off.any() or self.top < 0:
            return best
        out = best
        sub_y, sub_f = y[off], filed[off]
        result = sub_f.astype(np.int64).copy()
        decided = np.zeros(sub_y.shape, bool)
        own_index = np.full(sub_y.shape, -1)
        for i, (level, _lo, _hi) in enumerate(self.ranges):
            own_index[sub_f == level] = i
        decided |= own_index < 0                      # no range to judge by: the filed floor
        for i, (level, _lo, _hi) in enumerate(self.ranges):
            lo, hi = self.windows[i]
            mine = (own_index == i) & ~decided & (sub_y >= lo) & (sub_y <= hi)
            decided |= mine                           # its own camera drew it
        top_level = self.ranges[self.top][0]
        tlo, thi = self.windows[self.top]
        to_top = ~decided & (own_index != self.top) & (sub_y >= tlo) & (sub_y <= thi)
        result[to_top] = top_level
        decided |= to_top
        # else the nearest other floor whose window holds it (distance to its declared band; ties to the higher floor)
        levels = np.array([r[0] for r in self.ranges])
        best_i = np.full(sub_y.shape, -1)
        best_dd = np.full(sub_y.shape, np.inf)
        for i, (level, low, high) in enumerate(self.ranges):
            lo, hi = self.windows[i]
            if i == self.top:
                continue
            ok = ~decided & (own_index != i) & (sub_y >= lo) & (sub_y <= hi)
            d = np.maximum(0.0, np.maximum(low + FLOOR_FACE_SLACK - sub_y, sub_y - (high - FLOOR_FACE_SLACK)))
            best_level = np.where(best_i >= 0, levels[np.maximum(best_i, 0)], -10 ** 9)
            better = ok & ((best_i < 0) | (d < best_dd) | ((np.abs(d - best_dd) < 1e-6) & (level > best_level)))
            best_i = np.where(better, i, best_i)
            best_dd = np.where(better, d, best_dd)
        nearest = ~decided & (best_i >= 0)
        result[nearest] = levels[best_i[nearest]]
        result[~decided & (best_i < 0)] = NO_PICTURE
        out = out.copy()
        out[off] = result
        return out


def capture_window(min_y, max_y, next_min_y):
    """Map3DView.CaptureWindow: the heights a floor's picture camera drew."""
    top = math.isinf(next_min_y) or math.isnan(next_min_y)
    high = max_y + CAPTURE_TOP_CAMERA_HEIGHT if top else max(next_min_y - CAPTURE_CEILING_CLEARANCE, max_y + CAPTURE_CEILING_CLEARANCE)
    low = min_y - CAPTURE_FAR_CLIP_SLACK - (CAPTURE_TOP_DEPTH_BELOW if top else 0.0)
    return low, high


# --- the mesh, read once per sha ------------------------------------------------------------------------------------------

class MeshFaces:
    """What the checks need of a mesh: the faces that may sample a floor picture (|n.y| >= 0.5) as flat arrays, each
    building's vertex code range, the relief's height range, and the atlas tiles."""

    def __init__(self, data):
        np = _np()
        cc = load_tool("check-capture.py")
        m = cc.read_mesh(data, bound=1 << 34, keep=True)
        self.version = m["version"]
        self.minX, self.minZ, self.maxX, self.maxZ = m["minX"], m["minZ"], m["maxX"], m["maxZ"]
        self.yMin, self.yMax = m["yMin"], m["yMax"]
        self.alpha_pages = m["alphaPages"]
        self.atlas_pages = m["atlasPages"]
        self.triangles = m["triangles"]
        self.bands = {b["level"]: b for b in m["bands"]}
        sx, sz, sy = self.maxX - self.minX, self.maxZ - self.minZ, self.yMax - self.yMin
        self.relief_lo = self.relief_hi = None
        grids = {}
        for level, (heights, _dist) in m["grids"].items():
            band = self.bands[level]
            h = np.asarray(heights, dtype=np.uint16).reshape(band["height"], band["width"])
            grids[level] = h
            hit = h[h != NO_HIT]
            if hit.size:
                lo = self.yMin + sy * float(hit.min()) / MAX_QUANTISED
                hi = self.yMin + sy * float(hit.max()) / MAX_QUANTISED
                self.relief_lo = lo if self.relief_lo is None else min(self.relief_lo, lo)
                self.relief_hi = hi if self.relief_hi is None else max(self.relief_hi, hi)
        band_levels = sorted(self.bands)

        def band_for(level):
            if level in self.bands:
                return level
            return min(band_levels, key=lambda b: (abs(b - level), b)) if band_levels else level

        self.building_ranges = []   # (key, level, ycode min, ycode max, vertices)
        self.tile_list = []         # (page, x, y, w, h) per tile id; a face's "tile" indexes it (-1: no atlas range)
        tile_ids = {}
        cols = {k: [] for k in ("cx", "cy", "cz", "area", "ny", "tile", "filed", "skirt")}
        for (key, level, _tris, shape), k in zip(m["shapes"], m["kept"]):
            for rect in shape:
                if rect not in tile_ids:
                    tile_ids[rect] = len(self.tile_list)
                    self.tile_list.append(rect)
            ycodes = np.asarray(k["y"], dtype=np.uint16)
            if ycodes.size == 0:
                continue
            self.building_ranges.append((key, level, int(ycodes.min()), int(ycodes.max()), int(ycodes.size)))
            idx = np.asarray(k["indices"], dtype=np.int64).reshape(-1, 3)
            if idx.size == 0:
                continue
            x = self.minX + sx * np.asarray(k["x"], dtype=np.float64) / MAX_QUANTISED
            y = self.yMin + sy * ycodes.astype(np.float64) / MAX_QUANTISED
            z = self.minZ + sz * np.asarray(k["z"], dtype=np.float64) / MAX_QUANTISED
            P = [np.stack([x[idx[:, j]], y[idx[:, j]], z[idx[:, j]]], 1) for j in range(3)]
            n = np.cross(P[1] - P[0], P[2] - P[0])
            length = np.linalg.norm(n, axis=1)
            ny = np.where(length > 1e-6, n[:, 1] / np.maximum(length, 1e-12), 1.0)   # degenerate: TopView
            keep = np.abs(ny) >= ROOF_NORMAL_Y
            if not keep.any():
                continue
            tile = np.full(len(idx), -1, dtype=np.int32)
            for (pg, first, span, tx, ty, tw, th, *_rest) in k["ranges"]:
                tile[first // 3:(first + span) // 3] = tile_ids[(pg, tx, ty, tw, th)]
            c = (P[0] + P[1] + P[2]) / 3.0
            filed = band_for(level)
            # GroundSkirt: near-horizontal, within 1 m of the building's foot and 0.3 m of its filed band's relief (the
            # nearest cell here, the viewer interpolates)
            skirt = np.zeros(len(idx), bool)
            grid = grids.get(filed)
            if grid is not None:
                band = self.bands[filed]
                cell = band["cell"]
                col = np.clip(np.round((c[:, 0] - self.minX) / cell - 0.5).astype(np.int64), 0, band["width"] - 1)
                row = np.clip(np.round((c[:, 2] - self.minZ) / cell - 0.5).astype(np.int64), 0, band["height"] - 1)
                code = grid[row, col]
                ground = self.yMin + sy * code.astype(np.float64) / MAX_QUANTISED
                skirt = (code != NO_HIT) & (c[:, 1] <= y.min() + GROUND_SKIRT_RISE) & (np.abs(c[:, 1] - ground) <= GROUND_SKIRT_TOLERANCE)
            cols["cx"].append(c[keep, 0].astype(np.float32))
            cols["cy"].append(c[keep, 1].astype(np.float32))
            cols["cz"].append(c[keep, 2].astype(np.float32))
            cols["area"].append((length[keep] / 2.0).astype(np.float32))
            cols["ny"].append(ny[keep].astype(np.float32))
            cols["tile"].append(tile[keep])
            cols["filed"].append(np.full(int(keep.sum()), filed, dtype=np.int32))
            cols["skirt"].append(skirt[keep])
        for name, parts in cols.items():
            dtype = {"tile": np.int32, "filed": np.int32, "skirt": bool}.get(name, np.float32)
            setattr(self, name, np.concatenate(parts) if parts else np.zeros(0, dtype))
        self.tiles = set(self.tile_list)
        del m


def load_mesh(mapset):
    entry = mapset.mesh_entry()
    path = mapset.folder / entry["file"]
    return MeshFaces(path.read_bytes())


# --- (a) black faces ---------------------------------------------------------------------------------------------------------

def drawn_picture(mapset, floor):
    """The file the viewer draws for a floor: its viewing copy when it names a usable one, else the picture."""
    view = floor.get("viewFile")
    if isinstance(view, str) and view and (mapset.folder / view).is_file():
        return mapset.folder / view
    return mapset.folder / str(floor.get("file", ""))


def black_mask(path, host):
    """A bool array, True where the texel is clear and near-black (a host JPEG has no alpha: near-black alone)."""
    np = _np()
    image = _pil().open(path)
    image.load()
    if host:
        # a host JPEG carries no alpha: the client flattened every clear texel onto BackdropFill before the upload
        a = np.asarray(image.convert("RGB")).astype(np.int16)
        return (np.abs(a - np.array(HOST_BACKDROP, np.int16)) <= HOST_BACKDROP_TOLERANCE).all(axis=2)
    if image.mode not in ("RGBA", "LA"):
        a = np.asarray(image.convert("RGB"))
        return a.max(axis=2) < BLACK_RGB_MAX
    a = np.asarray(image if image.mode == "RGBA" else image.convert("RGBA"))
    return (a[:, :, 3] == 0) & (a[:, :, :3].max(axis=2) < BLACK_RGB_MAX)


def picture_ppm(mapset, band_levels):
    """Map3DView.PicturePpm: the LOWEST density among the floors the mesh has bands for, each the density of the picture
    actually drawn - a viewing copy's ppm x viewWidth / width (MapCatalog.cs:869-874) - or 0 when one is unknown."""
    ppm = mapset.ppm() or 0.0
    lowest = math.inf
    for f in mapset.floors():
        if f.get("level") not in band_levels:
            continue
        drawn = ppm
        view, vw, w = f.get("viewFile"), f.get("viewWidth"), f.get("width")
        if isinstance(view, str) and view and (mapset.folder / view).is_file() and isinstance(vw, int) and vw > 0:
            drawn = ppm * vw / w if isinstance(w, int) and w > 0 else 0.0
        if not drawn > 0:
            return 0.0
        lowest = min(lowest, drawn)
    return 0.0 if math.isinf(lowest) else lowest


def cut_out_tiles(mapset, faces):
    """Map3DView.TileStore.CutOutTile per tile id: a tile on an ALPHA page (MapMeshFile.AlphaPages) with any texel of
    alpha under ClipAlpha (128) in its rect - the rect counted from the page's bottom, so PNG rows rows-y-h .. rows-y-1
    (MeasurePage). A tile on an alpha page whose file cannot be read stays cut out, as the viewer keeps it; a tile on an
    opaque page never is."""
    np = _np()
    cut = np.zeros(len(faces.tile_list), bool)
    files = {}
    for entry in mapset.meta.get("atlas") or []:
        if isinstance(entry, dict) and isinstance(entry.get("page"), int) and isinstance(entry.get("file"), str):
            files[entry["page"]] = mapset.folder / entry["file"]
    for page in range(8):
        if not faces.alpha_pages >> page & 1:
            continue
        on_page = [i for i, t in enumerate(faces.tile_list) if t[0] == page]
        if not on_page:
            continue
        alpha, readable = None, False
        path = files.get(page)
        if path is not None and path.is_file():
            try:
                image = _pil().open(path)
                readable = True
                if "A" in image.getbands():
                    alpha = np.asarray(image.getchannel("A"))
            except Exception:
                readable = False
        for i in on_page:
            _p, x, y, w, h = faces.tile_list[i]
            if not readable:
                cut[i] = True
            elif alpha is not None:
                rows = alpha.shape[0]
                cut[i] = bool((alpha[rows - y - h:rows - y, x:x + w] < CLIP_ALPHA).any())
    return cut


def black_faces(mapset, faces, masks):
    """(black area current, black area pre-fix, roof area current, tinted area, moved area, black area off every band,
    whether atlas roofs come from the picture). The faces the viewer textures from a floor picture: Prep.ViewFor's
    TopView (n.y >= 0.5 with side pictures, |n.y| >= 0.5 without), ground skirts left out, and an atlas face only when
    RoofOnPicture holds - the roofs come from the picture (the drawn pictures' lowest density at least
    RoofPictureMinPpm), it faces up, and its tile is not cut out."""
    np = _np()
    has_sides = bool(mapset.sides())
    atlas_listed = bool(mapset.meta.get("atlas"))
    roofs_from_picture = (atlas_listed and faces.atlas_pages > 0 and
                          picture_ppm(mapset, set(faces.bands)) >= ROOF_PICTURE_MIN_PPM)
    up = faces.ny >= ROOF_NORMAL_Y if has_sides else np.abs(faces.ny) >= ROOF_NORMAL_Y
    on_atlas = faces.tile >= 0
    if not atlas_listed:
        atlas_ok = np.ones(len(faces.tile), bool)          # no atlas drawn: every range falls through to ViewFor
    elif not roofs_from_picture:
        atlas_ok = ~on_atlas                               # every atlas face keeps its tile
    else:
        cut = cut_out_tiles(mapset, faces)
        tile_cut = np.zeros(len(faces.tile), bool)
        tile_cut[on_atlas] = cut[faces.tile[on_atlas]]
        atlas_ok = ~on_atlas | ((faces.ny >= ROOF_NORMAL_Y) & ~tile_cut)
    sel = up & atlas_ok & ~faces.skirt
    y, filed = faces.cy[sel].astype(np.float64), faces.filed[sel]
    routing = Routing(mapset.floors(), set(faces.bands))
    now = routing.floor_for(y, filed)
    before = routing.floor_for(y, filed, prefix=True)
    area = faces.area[sel].astype(np.float64)
    u = np.clip((faces.cx[sel] - faces.minX) / (faces.maxX - faces.minX), 0.0, 1.0)
    v = np.clip((faces.cz[sel] - faces.minZ) / (faces.maxZ - faces.minZ), 0.0, 1.0)

    off_band = np.ones(len(y), bool)
    for _level, low, high in routing.ranges:
        off_band &= ~((y >= low) & (y <= high))

    def black_area(levels):
        total_black, total_roof, total_off = 0.0, 0.0, 0.0
        for level, mask in masks.items():
            on = levels == level
            if not on.any():
                continue
            h, w = mask.shape
            col = np.minimum((u[on] * w).astype(np.int64), w - 1)
            row = np.minimum(((1.0 - v[on]) * h).astype(np.int64), h - 1)
            hit = mask[row, col]
            total_black += float(area[on][hit].sum())
            total_off += float(area[on][hit & off_band[on]].sum())
            total_roof += float(area[on].sum())
        return total_black, total_roof, total_off

    black_now, roof_now, off_now = black_area(now)
    black_before, _, _ = black_area(before)
    tint = float(area[now == NO_PICTURE].sum())
    moved = float(area[(now != before) & (now != NO_PICTURE)].sum())
    return black_now, black_before, roof_now, tint, moved, off_now, roofs_from_picture


def check_black_faces(mapset, faces, masks):
    name = f"black-faces {mapset.where}"
    if mapset.host:
        return Result(LAYER, name, SKIP, "host JPEG set: no alpha (clear texels are flattened onto the backdrop), so it "
                                         "cannot show the clear-black defect")
    if faces is None:
        return Result(LAYER, name, SKIP, "no mesh")
    black, before, roof, tint, moved, off, picture_roofs = black_faces(mapset, faces, masks)
    if roof <= 0:
        return Result(LAYER, name, SKIP, "no face samples a floor picture")
    share = black / roof
    status = FAIL if share > BLACK_FACE_SHARE else WARN if share > BLACK_FACE_WARN_SHARE else PASS
    return Result(LAYER, name, status,
                  f"{black:,.0f} m2 clear near-black of {roof:,.0f} m2 roof = {share:.2%} (FAIL > {BLACK_FACE_SHARE:.0%}, "
                  f"WARN > {BLACK_FACE_WARN_SHARE:.0%}), {off:,.0f} m2 of it off every band; pre-fix rule {before:,.0f} m2 "
                  f"= {before / roof:.2%}; atlas roofs {'on the picture' if picture_roofs else 'kept on the atlas'}; "
                  f"{moved:,.0f} m2 rerouted, {tint:,.0f} m2 tinted")


# --- (b) extent ------------------------------------------------------------------------------------------------------------

def check_extent(mapset):
    name = f"extent {mapset.where}"
    extent, ppm = mapset.extent(), mapset.ppm()
    if extent is None:
        return Result(LAYER, name, FAIL, "extent is missing or not four numbers")
    min_x, min_z, max_x, max_z = extent
    problems = []
    if not all(math.isfinite(v) for v in extent):
        return Result(LAYER, name, FAIL, f"extent is not finite: {extent}")
    span_x, span_z = max_x - min_x, max_z - min_z
    for axis, span in (("x", span_x), ("z", span_z)):
        if not MIN_SPAN_METRES <= span <= MAX_SPAN_METRES:
            problems.append(f"{axis} span {span:g} m outside {MIN_SPAN_METRES:g}..{MAX_SPAN_METRES:g}")
    if ppm is None or not math.isfinite(ppm) or ppm <= 0:
        problems.append(f"pxPerMetre {mapset.meta.get('pxPerMetre')!r} is not a positive number")
    if problems:
        return Result(LAYER, name, FAIL, "; ".join(problems))
    tolerance = HOST_PIXEL_TOLERANCE if mapset.host else CLIENT_PIXEL_TOLERANCE
    want_w, want_h = math.ceil(span_x * ppm), math.ceil(span_z * ppm)
    for f in mapset.floors():
        label = f"floor {f.get('level')}"
        w, h = f.get("width"), f.get("height")
        if not isinstance(w, int) or not isinstance(h, int):
            problems.append(f"{label} has no integer width/height")
            continue
        if abs(w - want_w) > tolerance or abs(h - want_h) > tolerance:
            problems.append(f"{label} is {w}x{h}, the extent at {ppm:g} px/m wants {want_w}x{want_h} (+-{tolerance})")
        size = picture_size(mapset.folder / str(f.get("file", "")))
        if size is None:
            problems.append(f"{label}: {f.get('file')} is missing or unreadable")
        elif tuple(size) != (w, h):
            problems.append(f"{label}: {f.get('file')} is {size[0]}x{size[1]}, the meta says {w}x{h}")
    if problems:
        return Result(LAYER, name, FAIL, "; ".join(problems))
    return Result(LAYER, name, PASS, f"{span_x:,.0f} x {span_z:,.0f} m at {ppm:.3f} px/m, {len(mapset.floors())} floor(s) "
                                     f"{want_w}x{want_h} (+-{tolerance})")


# --- (c) floors -----------------------------------------------------------------------------------------------------------

def check_floors(mapset):
    name = f"floors {mapset.where}"
    floors = mapset.floors()
    if not floors:
        return Result(LAYER, name, FAIL, "no floors")
    thin, bands = [], []
    for f in floors:
        try:
            lo, hi = float(f["minY"]), float(f["maxY"])
        except (KeyError, TypeError, ValueError):
            thin.append(f"floor {f.get('level')} has no numeric minY/maxY")
            continue
        bands.append(hi - lo)
        if hi - lo < MIN_BAND_METRES:
            thin.append(f"floor {f.get('level')} band {lo:g}..{hi:g} is {hi - lo:g} m (< {MIN_BAND_METRES:g})")
    count = len(floors)
    detail = f"{count} floor(s), thinnest band {min(bands):g} m" if bands else f"{count} floor(s)"
    if thin:
        return Result(LAYER, name, FAIL, "; ".join(thin))
    if count > WARN_FLOORS:
        return Result(LAYER, name, WARN, f"{detail} - over {WARN_FLOORS} floors")
    return Result(LAYER, name, PASS, detail)


# --- (d) mesh heights -------------------------------------------------------------------------------------------------------

def check_mesh_heights(mapset, faces):
    name = f"mesh-heights {mapset.where}"
    if faces is None:
        return Result(LAYER, name, SKIP, "no mesh")
    lows, highs = [], []
    for f in mapset.floors():
        try:
            lo, hi = float(f["minY"]), float(f["maxY"])
        except (KeyError, TypeError, ValueError):
            continue
        if lo > -PLACEHOLDER_Y and hi < PLACEHOLDER_Y:
            lows.append(lo)
            highs.append(hi)
    if faces.relief_lo is not None:
        lows.append(faces.relief_lo)
        highs.append(faces.relief_hi)
    if not lows:
        return Result(LAYER, name, SKIP, "no finite ground range")
    ground_lo, ground_hi = min(lows), max(highs)
    span = faces.yMax - faces.yMin
    far, clamped = [], []
    for key, level, cmin, cmax, count in faces.building_ranges:
        ylo = faces.yMin + span * cmin / MAX_QUANTISED
        yhi = faces.yMin + span * cmax / MAX_QUANTISED
        if ylo < ground_lo - OUT_OF_GROUND_METRES or yhi > ground_hi + OUT_OF_GROUND_METRES:
            far.append(f"key {key} y {ylo:.0f}..{yhi:.0f}")
        if cmin == 0 or cmax >= MAX_QUANTISED:
            clamped.append(f"key {key} y {ylo:.1f}..{yhi:.1f}")
    problems = []
    if far:
        problems.append(f"{len(far)} building(s) over {OUT_OF_GROUND_METRES:g} m past the ground {ground_lo:.1f}..{ground_hi:.1f}: "
                        + ", ".join(far[:3]))
    if clamped:
        problems.append(f"{len(clamped)} building(s) with a vertex on the y range's end ({faces.yMin:.1f}/{faces.yMax:.1f} - "
                        f"clamped): " + ", ".join(clamped[:3]))
    if problems:
        return Result(LAYER, name, FAIL, "; ".join(problems))
    top = max((faces.yMin + span * r[3] / MAX_QUANTISED for r in faces.building_ranges), default=float("nan"))
    bottom = min((faces.yMin + span * r[2] / MAX_QUANTISED for r in faces.building_ranges), default=float("nan"))
    return Result(LAYER, name, PASS, f"{len(faces.building_ranges):,} buildings y {bottom:.1f}..{top:.1f}, ground "
                                     f"{ground_lo:.1f}..{ground_hi:.1f}, file range {faces.yMin:.1f}..{faces.yMax:.1f}")


# --- (e) viewing copies -------------------------------------------------------------------------------------------------------

def view_size(width, height):
    """MapCapture.ViewSize: (w, h) of the copy, or None when the picture needs none."""
    long_side = max(width, height)
    if width < 1 or height < 1 or long_side <= VIEW_PICTURE_SIDE:
        return None

    def scaled(side):
        if side == long_side:
            return VIEW_PICTURE_SIDE
        value = side * VIEW_PICTURE_SIDE / long_side / 4.0
        rounded = int(math.floor(value + 0.5)) if value >= 0 else -int(math.floor(-value + 0.5))  # AwayFromZero
        return max(4, min(side, rounded * 4))

    vw, vh = scaled(width), scaled(height)
    return (vw, vh) if vw < width or vh < height else None


def check_view_copies(mapset):
    name = f"view-copies {mapset.where}"
    problems, copies, need = [], 0, 0
    for f in mapset.floors():
        w, h = f.get("width"), f.get("height")
        if not isinstance(w, int) or not isinstance(h, int):
            continue
        want = view_size(w, h)
        if want is None:
            continue
        need += 1
        label = f"floor {f.get('level')} ({w}x{h})"
        view = f.get("viewFile")
        if not isinstance(view, str) or not view:
            problems.append(f"{label} has no viewing copy")
            continue
        size = picture_size(mapset.folder / view)
        if size is None:
            problems.append(f"{label}: viewing copy {view} is missing or unreadable")
            continue
        copies += 1
        if size[0] % 4 or size[1] % 4:
            problems.append(f"{label}: viewing copy is {size[0]}x{size[1]}, not multiples of 4")
        if tuple(size) != want or (f.get("viewWidth"), f.get("viewHeight")) != want:
            problems.append(f"{label}: viewing copy is {size[0]}x{size[1]} (meta {f.get('viewWidth')}x{f.get('viewHeight')}), "
                            f"ViewSize wants {want[0]}x{want[1]}")
    if problems:
        return Result(LAYER, name, FAIL, "; ".join(problems))
    if need == 0:
        return Result(LAYER, name, PASS, f"no floor over {VIEW_PICTURE_SIDE} px")
    return Result(LAYER, name, PASS, f"{copies} of {need} floor(s) over {VIEW_PICTURE_SIDE} px have a 4-aligned copy")


# --- (f) compressed-texture safety ---------------------------------------------------------------------------------------------

def mip1_ok(w, h):
    return (max(1, w >> 1)) % 4 == 0 and (max(1, h >> 1)) % 4 == 0


# The two sites that make the textures the viewer compresses; each must create them with ignoreMipmapLimit on, or a global
# mip limit (texture quality, EFT's SD mode) drops mip 0 and a DXT mip 1 that is not whole 4x4 blocks is refused.
MIP_LIMIT_SITES = (("Source/Tarkov-QuestTree/UI/Map3DView.cs", "atlas tiles (TileStore)"),
                   ("Source/Tarkov-QuestTree/UI/DynamicMapsLibrary.cs", "floor and side pictures (BuildRasterSprite)"))
_CTOR = None
_GUARD_CACHE = {}


def _top_level_args(text, start):
    """The comma-separated arguments of the call whose '(' is at text[start], split at depth 0."""
    depth, args, current = 0, [], []
    for ch in text[start + 1:]:
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                args.append("".join(current).strip())
                return args
            depth -= 1
        if ch == "," and depth == 0:
            args.append("".join(current).strip())
            current = []
        else:
            current.append(ch)
    return args


def file_sets_mip_limit_opt_out(text):
    """Whether C# source creates a Texture2D with ignoreMipmapLimit on: a constructor whose 7th argument (Unity's
    ignoreMipmapLimit) is true or that names it ': true', or a '.ignoreMipmapLimit = true' assignment."""
    import re
    if re.search(r"ignoreMipmapLimit\s*:\s*true\b", text) or re.search(r"\.ignoreMipmapLimit\s*=\s*true\b", text):
        return True
    for match in re.finditer(r"new\s+Texture2D\s*\(", text):
        args = _top_level_args(text, match.end() - 1)
        if len(args) >= 7 and args[6] == "true":
            return True
    return False


def mip_limit_guard(repo):
    """(ok, detail): every MIP_LIMIT_SITES file still opts its textures out of the global mip limit."""
    repo = Path(repo)
    if repo in _GUARD_CACHE:
        return _GUARD_CACHE[repo]
    missing, read = [], 0
    for rel, what in MIP_LIMIT_SITES:
        path = repo / rel
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            missing.append(f"{rel} unreadable")
            continue
        read += 1
        if not file_sets_mip_limit_opt_out(text):
            missing.append(f"{rel} ({what}) no longer creates its textures with ignoreMipmapLimit")
    result = (not missing, "; ".join(missing) if missing else f"ignoreMipmapLimit set at both sites ({read} files)")
    _GUARD_CACHE[repo] = result
    return result


def check_mip_limit_guard(repo):
    ok, detail = mip_limit_guard(repo)
    return Result(LAYER, "dxt-mip-limit-guard source", PASS if ok else FAIL, detail)


def check_dxt(mapset, faces, guard_ok=True):
    name = f"dxt-safety {mapset.where}"
    textures = []   # (label, w, h, must_align)
    for f in mapset.floors():
        path = drawn_picture(mapset, f)
        size = picture_size(path)
        if size:
            textures.append((path.name, size[0], size[1], not mapset.host))
    for s in mapset.sides():
        size = picture_size(mapset.folder / str(s.get("file", "")))
        if size:
            textures.append((str(s.get("file")), size[0], size[1], not mapset.host))
    tiles = sorted({(t[3], t[4]) for t in faces.tiles}) if faces is not None else []
    tile_count = len(faces.tiles) if faces is not None else 0
    fail, mip1, uncompressed = [], [], []
    for label, w, h, must in textures:
        if w % 4 or h % 4:
            (fail if must else uncompressed).append(f"{label} {w}x{h}")
        elif not mip1_ok(w, h):
            mip1.append(f"{label} {w}x{h}->{w >> 1}x{h >> 1}")
    tile_mip1 = 0
    for page, tx, ty, tw, th in (faces.tiles if faces is not None else ()):
        if tw % 4 or th % 4:
            fail.append(f"tile {tw}x{th} on page {page}")
        elif not mip1_ok(tw, th):
            tile_mip1 += 1
    notes = []
    if uncompressed:
        notes.append(f"{len(uncompressed)} host picture(s) not 4-aligned stay uncompressed")
    if fail:
        return Result(LAYER, name, FAIL, f"not 4-aligned at mip 0 (left uncompressed): {', '.join(fail[:4])}"
                                         + (f" (+{len(fail) - 4})" if len(fail) > 4 else ""))
    tiles_note = "" if faces is not None else ", tiles not read"
    if mip1 or tile_mip1:
        parts = []
        if mip1:
            parts.append(f"mip 1 not 4-aligned: {len(mip1)} picture(s) ({', '.join(mip1[:2])}"
                         + (f", +{len(mip1) - 2}" if len(mip1) > 2 else "") + ")")
        if tile_mip1:
            parts.append(f"{tile_mip1} of {tile_count} atlas tiles")
        if guard_ok:
            # the viewer creates every compressed texture with ignoreMipmapLimit (dxt-mip-limit-guard): mip 0 is never
            # dropped, so a misaligned mip 1 is never the top level D3D11 is handed
            return Result(LAYER, name, PASS, "; ".join(parts + notes) + f"{tiles_note} - safe: ignoreMipmapLimit is set")
        return Result(LAYER, name, WARN, "; ".join(parts + notes) + f"{tiles_note} - and ignoreMipmapLimit is NOT set")
    return Result(LAYER, name, PASS, f"{len(textures)} picture(s), {tile_count} tile(s) 4-aligned at mips 0 and 1"
                                     + (f"; {notes[0]}" if notes else "") + tiles_note)


# --- (g) menu sidecars --------------------------------------------------------------------------------------------------------

def sidecar_of(picture_name):
    stem = picture_name[:-4] if picture_name.lower().endswith(".png") else picture_name
    return stem + ".dist.png"


def check_menu_sidecars(mapset):
    name = f"menu-sidecars {mapset.where}"
    if mapset.host:
        return Result(LAYER, name, SKIP, "a host stores no sidecars")
    if mapset.meta.get("capturedIn") != "menu":
        return Result(LAYER, name, SKIP, f"capturedIn {mapset.meta.get('capturedIn')!r}, not a menu set")
    problems, checked, decoded, zero_share = [], 0, 0, []
    np = None
    pictures = [(f"floor {f.get('level')}", str(f.get("file", "")), f.get("width"), f.get("height")) for f in mapset.floors()]
    pictures += [(f"side {s.get('dir')}", str(s.get("file", "")), s.get("width"), s.get("height")) for s in mapset.sides()]
    for label, file, w, h in pictures:
        dist = mapset.folder / sidecar_of(file)
        if not dist.is_file():
            problems.append(f"{label}: no {dist.name}")
            continue
        size = picture_size(dist)
        if size is None or tuple(size) != (w, h):
            problems.append(f"{label}: {dist.name} is {size}, the picture {w}x{h}")
            continue
        checked += 1
        if w * h <= DIST_DECODE_MAX_PIXELS:
            np = np or _np()
            plane = np.asarray(_pil().open(dist).convert("L"))
            decoded += 1
            drawn = plane != 255
            zeros = int((plane == 0).sum())
            if zeros == 0:
                problems.append(f"{label}: {dist.name} holds no step-0 pixel")
            elif drawn.any():
                zero_share.append(zeros / int(drawn.sum()))
    if problems:
        return Result(LAYER, name, FAIL, "; ".join(problems))
    share = f", step 0 on at least {min(zero_share):.0%} of each one's drawn pixels" if zero_share else ""
    return Result(LAYER, name, PASS, f"{checked} sidecar(s) present at their picture's size, {decoded} decoded{share}")


# --- (h) backups --------------------------------------------------------------------------------------------------------------

def tree_bytes(path):
    total = 0
    for root, _dirs, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def check_backups(side, root, ctx, limit=None):
    limit = BACKUP_WARN_BYTES if limit is None else limit
    name = f"backups {side}"
    if root is None or not Path(root).is_dir():
        return Result(LAYER, name, SKIP, f"no folder {root}")
    per_map = {}
    for folder in Path(root).iterdir():
        if folder.is_dir() and is_backup(folder.name) and ctx.wants(folder.name.split(".")[0]):
            key = folder.name.split(".bak-")[0].split(".old-")[0]
            count, size = per_map.get(key, (0, 0))
            per_map[key] = (count + 1, size + tree_bytes(folder))
    total = sum(s for _c, s in per_map.values())
    detail = ", ".join(f"{k} {s / 1048576:,.0f} MB ({c})" for k, (c, s) in sorted(per_map.items(), key=lambda kv: -kv[1][1]))
    detail = f"total {total / 1048576:,.0f} MB" + (f": {detail}" if detail else ", none")
    if total > limit:
        return Result(LAYER, name, WARN, f"{detail} - over the {limit / 1048576:,.3g} MB limit")
    return Result(LAYER, name, PASS, detail)


# --- the run --------------------------------------------------------------------------------------------------------------------

def audit(ctx, roots, backup_limit=None, quick=False, repo=None):
    """Every check over every set under roots ([(side, folder)]). Returns results. quick leaves out everything that reads
    a mesh; repo is the source tree the mip-limit guard reads (the context's)."""
    repo = repo or getattr(ctx, "repo_root", None) or Path(__file__).resolve().parent.parent.parent
    results = []
    sets = []
    for side, root in roots:
        found, empty = find_sets(root, ctx)
        if root is None or not Path(root).is_dir():
            results.append(Result(LAYER, f"sets {side}", SKIP, f"no folder {root}"))
            continue
        if empty:
            results.append(Result(LAYER, f"sets {side}", SKIP, f"no meta in: {', '.join(empty)}"))
        for key, folder, meta in found:
            sets.append(MapSet(side, key, folder, meta))
    good = []
    for s in sets:
        if s.error:
            results.append(Result(LAYER, f"meta {s.where}", FAIL, s.error))
        else:
            good.append(s)

    guard_ok, _detail = mip_limit_guard(repo)
    results.append(check_mip_limit_guard(repo))
    for s in good:
        results.append(check_extent(s))
        results.append(check_floors(s))
        results.append(check_view_copies(s))
        results.append(check_menu_sidecars(s))

    if quick:
        for s in good:
            results.append(check_dxt(s, None, guard_ok))
        results.append(Result(LAYER, "mesh-backed checks", SKIP, "--quick: black-faces, mesh-heights and the atlas tiles "
                                                                 "of dxt-safety not run"))
        return _with_backups(results, roots, ctx, backup_limit)

    # mesh-backed checks, one mesh read per sha, with the pictures decoded on threads meanwhile
    groups = {}
    for s in good:
        groups.setdefault(s.mesh_id(), []).append(s)
    pool = ThreadPoolExecutor(max_workers=4)
    for mesh_id, members in groups.items():
        mask_jobs = {}
        for s in members:
            if s.host or mesh_id is None:
                continue        # black-faces skips host sets: their pictures are not decoded
            for f in s.floors():
                if isinstance(f.get("level"), int):
                    path = drawn_picture(s, f)
                    if path.is_file():
                        mask_jobs[(s.where, f["level"])] = pool.submit(black_mask, path, s.host)
        faces, mesh_error = None, None
        if mesh_id is not None:
            try:
                faces = load_mesh(members[0])
            except Exception as exc:   # check-capture's MeshError and I/O alike: the set's mesh cannot be read
                mesh_error = f"{type(exc).__name__}: {exc}"
        for s in members:
            if mesh_error:
                for check in ("black-faces", "mesh-heights"):
                    results.append(Result(LAYER, f"{check} {s.where}", FAIL, f"mesh unreadable: {mesh_error}"))
                results.append(check_dxt(s, None, guard_ok))
                continue
            masks = {}
            for (where, level), job in list(mask_jobs.items()):
                if where == s.where:
                    try:
                        masks[level] = job.result()
                    except Exception as exc:
                        results.append(Result(LAYER, f"picture {s.where} floor {level}", FAIL, f"does not decode: {exc}"))
            results.append(check_black_faces(s, faces, masks))
            results.append(check_mesh_heights(s, faces))
            results.append(check_dxt(s, faces, guard_ok))
            masks.clear()
        mask_jobs.clear()
        del faces
    pool.shutdown()
    return _with_backups(results, roots, ctx, backup_limit)


def _with_backups(results, roots, ctx, backup_limit):
    for side, root in roots:
        if side == "client":
            results.append(check_backups(side, root, ctx, backup_limit))
    return results


def run(ctx):
    started = time.time()
    results = audit(ctx, [("client", ctx.captures), ("host", ctx.host_maps)], quick=bool(getattr(ctx, "quick", False)))
    order = {"meta": 0, "extent": 1, "floors": 2, "mesh-heights": 3, "black-faces": 4, "view-copies": 5,
             "dxt-mip-limit-guard": 6, "dxt-safety": 7, "menu-sidecars": 8, "backups": 9, "sets": 10, "picture": 11,
             "mesh-backed": 12}
    results.sort(key=lambda r: (order.get(r.name.split(" ")[0], 99), r.name.lower()))
    results.append(Result(LAYER, "audit time", PASS, f"{time.time() - started:.1f} s"))
    return results


# --- self-test: synthetic sets, each check made to fail ----------------------------------------------------------------------

def _png(path, rgba):
    """Writes a numpy uint8 HxWx4 (or HxWx3, or HxW grey) array as a PNG with PIL."""
    Image = _pil()
    np = _np()
    a = np.ascontiguousarray(rgba)
    mode = {4: "RGBA", 3: "RGB"}.get(a.shape[2] if a.ndim == 3 else 0, "L")
    Image.fromarray(a, mode).save(path)


def write_mesh(path, extent, y_range, bands, buildings):
    """A MapMeshFile v4 (the byte table check-capture's read_mesh follows), raw-deflated. bands: [(level, cell, w, h,
    heights)] with heights a list of u16 codes; buildings: [(key, level, [(x,y,z) world], [i...])] - quantised here."""
    min_x, min_z, max_x, max_z = extent
    y_min, y_max = y_range
    out = bytearray(b"QTM1")
    out += struct.pack("<i4d2fi", 4, min_x, min_z, max_x, max_z, y_min, y_max, 0)
    out += bytes([0])                      # alpha-page mask
    out += struct.pack("<i", len(bands))
    for level, cell, w, h, heights in bands:
        out += struct.pack("<ifii", level, cell, w, h)
        out += struct.pack(f"<{w * h}H", *heights)
        out += bytes([0]) * (w * h)

    def q(value, lo, hi):
        t = (value - lo) / (hi - lo)
        return 0 if t <= 0 else MAX_QUANTISED if t >= 1 else int(t * MAX_QUANTISED + 0.5)

    out += struct.pack("<i", len(buildings))
    for key, level, verts, indices in buildings:
        out += struct.pack("<iii", key, level, len(verts))
        out += struct.pack(f"<{len(verts)}H", *[q(v[0], min_x, max_x) for v in verts])
        out += struct.pack(f"<{len(verts)}H", *[q(v[1], y_min, y_max) for v in verts])
        out += struct.pack(f"<{len(verts)}H", *[q(v[2], min_z, max_z) for v in verts])
        out += struct.pack("<i", len(indices)) + struct.pack(f"<{len(indices)}I", *indices)
        out += struct.pack("<i", 0)        # UVs
        out += struct.pack("<i", 0)        # ranges
    c = zlib.compressobj(9, zlib.DEFLATED, -15)
    data = c.compress(bytes(out)) + c.flush()
    Path(path).write_bytes(data)
    return data


def quad(x0, z0, x1, z1, y):
    """Two upward triangles (counter-clockwise seen from above gives n.y > 0 with Unity's left-handed cross? We check
    n.y from the vertex order the reader sees: (b-a) x (c-a) with a=(x0,z0), b=(x0,z1), c=(x1,z1) points +y)."""
    verts = [(x0, y, z0), (x0, y, z1), (x1, y, z1), (x1, y, z0)]
    return verts, [0, 1, 2, 0, 2, 3]


def make_fixture(root, key="Fixture", side="client", *, floors=None, black_floor=None, lake_y=-12.0,
                 lake_filed=-1, menu=True, extra_building=None, picture_px=256, ppm=1.0, viewcopy=None,
                 sidecars=True, sidecar_zero=True, y_range=(-60.0, 140.0)):
    """A small set: a 256 m square, floors [(level, minY, maxY)], a relief band per floor (flat at minY), one big lake-bank
    quad filed under lake_filed at lake_y, a ground quad on floor 0. Pictures are opaque grey except black_floor's (alpha-0
    black everywhere). Returns the folder."""
    np = _np()
    floors = floors or [(-1, -4.5, 1.0), (0, 6.5, 19.0), (1, 20.5, 25.5)]
    folder = Path(root) / key
    folder.mkdir(parents=True, exist_ok=True)
    span = picture_px / ppm
    extent = (0.0, 0.0, span, span)
    meta = {"schemaVersion": 1, "map": key, "extent": {"minX": 0.0, "minZ": 0.0, "maxX": span, "maxZ": span},
            "rotation": 0.0, "pxPerMetre": ppm, "floors": [], "sides": []}
    if menu:
        meta["capturedIn"] = "menu"
    ext = ".png" if side == "client" else ".jpg"
    for level, lo, hi in floors:
        file = f"{key}-{level}{ext}"
        img = np.zeros((picture_px, picture_px, 4), np.uint8)
        if level != black_floor:
            img[:, :, :3] = 128
            img[:, :, 3] = 255
        if ext == ".png":
            _png(folder / file, img)
        else:
            _pil().fromarray(img[:, :, :3], "RGB").save(folder / file, quality=90)
        entry = {"level": level, "name": f"F{level}", "file": file, "width": picture_px, "height": picture_px,
                 "minY": lo, "maxY": hi}
        if viewcopy:
            vw, vh = viewcopy
            vfile = f"{key}-{level}.view.png"
            _png(folder / vfile, np.full((vh, vw, 4), 128, np.uint8))
            entry.update(viewFile=vfile, viewWidth=vw, viewHeight=vh)
        meta["floors"].append(entry)
        if sidecars and ext == ".png":
            dist = np.full((picture_px, picture_px, 3), 0 if sidecar_zero else 7, np.uint8)
            _png(folder / f"{key}-{level}.dist.png", dist)
    bands = []
    for level, lo, hi in floors[:8]:          # the format holds 8 bands; a ninth floor has a picture and no band
        code = int((lo - y_range[0]) / (y_range[1] - y_range[0]) * MAX_QUANTISED + 0.5)
        bands.append((level, 16.0, int(math.ceil(span / 16)), int(math.ceil(span / 16)),
                      [code] * int(math.ceil(span / 16)) ** 2))
    v1, i1 = quad(10, 10, 200, 200, lake_y)
    v2, i2 = quad(210, 210, 250, 250, floors[min(1, len(floors) - 1)][1] + 3.0)
    buildings = [(1, lake_filed, v1, i1), (2, floors[min(1, len(floors) - 1)][0], v2, i2)]
    # a tall tower so the lake quad is not its building's foot (no ground skirt)
    if extra_building:
        buildings.append(extra_building)
    data = write_mesh(folder / f"{key}-mesh.bin", extent, y_range, bands, buildings)
    import hashlib
    meta["mesh"] = {"file": f"{key}-mesh.bin", "bytes": len(data), "version": 4, "sha256": hashlib.sha256(data).hexdigest()}
    (folder / f"{key}.map.json").write_text(json.dumps(meta, indent=1), encoding="utf-8")
    return folder


def self_test(ctx):
    """Each check on a fixture built to fail it, plus a clean fixture every check passes. A check that does not fail on
    its fixture is itself a FAIL here."""
    results = []
    tmp = Path(tempfile.mkdtemp(prefix="qt-audit-sets-"))
    try:
        def one(label, build, check, want, backup_limit=None, repo=None):
            root = tmp / label
            root.mkdir()
            build(root)
            sub = Context(captures=root, host_maps=tmp / "none", maps=[])
            got = [r for r in audit(sub, [("client", root)], backup_limit=backup_limit, repo=repo)
                   if r.name.startswith(check)]
            statuses = {r.status for r in got}
            ok = want in statuses
            detail = "; ".join(f"{r.status} {r.detail}" for r in got)[:300]
            results.append(Result(LAYER, f"self-test {label}", PASS if ok else FAIL,
                                  f"{check} -> {'/'.join(sorted(statuses)) or 'nothing'} (wanted {want}): {detail}"))

        # the clean fixture: the lake bank under the basement, which its camera never drew - the top floor's did. Every
        # check must pass on it (or skip), so a check that fails everything is caught here
        root = tmp / "clean"
        root.mkdir()
        make_fixture(root, black_floor=-1)
        rows = audit(Context(captures=root, host_maps=tmp / "none"), [("client", root)])
        bad = [f"{r.status} {r.name}: {r.detail}" for r in rows if r.status in (FAIL, WARN)]
        results.append(Result(LAYER, "self-test clean-fixture", FAIL if bad else PASS,
                              "; ".join(bad)[:300] if bad else f"{len(rows)} checks, none FAIL or WARN"))
        # (a) the PRE-FIX rule would sample the basement here: prove the simulation sees it (fixture passes now, the
        #     pre-fix figure in the detail must be large) - and a set where the top picture is black too must FAIL
        one("a-black-faces", lambda r: make_fixture(r, black_floor=1, lake_y=-12.0), "black-faces", FAIL)
        root = tmp / "a-prefix"
        root.mkdir()
        make_fixture(root, black_floor=-1)
        s = MapSet("client", "Fixture", root / "Fixture", root / "Fixture" / "Fixture.map.json")
        faces = load_mesh(s)
        masks = {f["level"]: black_mask(drawn_picture(s, f), False) for f in s.floors()}
        black, before, roof, _t, _m, _o, _r = black_faces(s, faces, masks)
        ok = before / roof > BLACK_FACE_SHARE and black / roof <= BLACK_FACE_SHARE
        results.append(Result(LAYER, "self-test a-prefix-rule", PASS if ok else FAIL,
                              f"pre-fix rule {before / roof:.1%} black (must FAIL > {BLACK_FACE_SHARE:.0%}), current "
                              f"{black / roof:.1%} (must PASS)"))
        # (b) a picture one row short of its extent
        def short_picture(r):
            folder = make_fixture(r)
            meta = json.loads((folder / "Fixture.map.json").read_text())
            meta["extent"]["maxZ"] += 3.0
            (folder / "Fixture.map.json").write_text(json.dumps(meta))
        one("b-extent", short_picture, "extent", FAIL)
        def nan_extent(r):
            folder = make_fixture(r)
            text = (folder / "Fixture.map.json").read_text()
            meta = json.loads(text)
            meta["extent"]["maxX"] = meta["extent"]["minX"]
            (folder / "Fixture.map.json").write_text(json.dumps(meta))
        one("b-extent-degenerate", nan_extent, "extent", FAIL)
        # (c) nine floors (WARN) and a 0.5 m band (FAIL)
        nine = [(i, i * 5.0, i * 5.0 + 2.0) for i in range(9)]
        one("c-floors-count", lambda r: make_fixture(r, floors=nine, lake_filed=0, lake_y=-12.0), "floors", WARN)
        one("c-floors-thin", lambda r: make_fixture(r, floors=[(0, 0.0, 0.5), (1, 6.5, 9.0)], lake_filed=0), "floors", FAIL)
        # (d) a helper plane 1000 m below (the Lab defect): it reaches past the ground and clamps at the y range's end
        plane = quad(0, 0, 50, 50, -1000.0)
        one("d-mesh-heights-clamped", lambda r: make_fixture(r, extra_building=(9, 0, plane[0], plane[1])), "mesh-heights", FAIL)
        # the same plane in a file whose y range holds it: stored unclamped, but 1000 m under the ground
        one("d-mesh-heights-far", lambda r: make_fixture(r, extra_building=(9, 0, plane[0], plane[1]), y_range=(-1100.0, 140.0)),
            "mesh-heights", FAIL)
        # (e) a 9000 px picture with no viewing copy - written as a real 9000x4 PNG to keep it small
        def big_no_copy(r):
            folder = make_fixture(r)
            np = _np()
            meta = json.loads((folder / "Fixture.map.json").read_text())
            for f in meta["floors"]:
                _png(folder / f["file"], np.full((4, 9000, 4), 128, np.uint8))
                f["width"], f["height"] = 9000, 4
            (folder / "Fixture.map.json").write_text(json.dumps(meta))
        one("e-view-copies", big_no_copy, "view-copies", FAIL)
        # (f) a viewing copy whose mip 1 is not 4-aligned: PASS while the source opts out of the mip limit, WARN on a copy
        #     of the source where it does not; the guard row FAILs on each site's mutant alone. And a client picture not
        #     4-aligned at mip 0 (FAIL)
        repo = Path(getattr(ctx, "repo_root", None) or Path(__file__).resolve().parent.parent.parent)
        one("f-dxt-mip1-guarded", lambda r: make_fixture(r, viewcopy=(244, 244)), "dxt-safety", PASS, repo=repo)
        mutants = {}
        for k, (rel, _what) in enumerate(MIP_LIMIT_SITES):
            mutant = tmp / f"repo-mutant-{k}"
            for rel2, _w in MIP_LIMIT_SITES:
                text = (repo / rel2).read_text(encoding="utf-8", errors="replace")
                if rel2 == rel:
                    changed = text.replace("false, false, true, null)", "false, false, false, null)")
                    changed = changed.replace("ignoreMipmapLimit = true", "ignoreMipmapLimit = false")
                    changed = changed.replace("ignoreMipmapLimit: true", "ignoreMipmapLimit: false")
                    if changed == text:
                        results.append(Result(LAYER, f"self-test f-guard-mutant {rel}", FAIL,
                                              "the mutation found nothing to change - the guard's patterns are stale"))
                    text = changed
                (mutant / rel2).parent.mkdir(parents=True, exist_ok=True)
                (mutant / rel2).write_text(text, encoding="utf-8")
            mutants[rel] = mutant
            ok, detail = mip_limit_guard(mutant)
            results.append(Result(LAYER, f"self-test f-guard-mutant {Path(rel).name}", FAIL if ok else PASS,
                                  f"dxt-mip-limit-guard -> {'PASS' if ok else 'FAIL'} (wanted FAIL): {detail}"))
        ok, detail = mip_limit_guard(repo)
        results.append(Result(LAYER, "self-test f-guard-real-source", PASS if ok else FAIL, f"wanted PASS: {detail}"))
        one("f-dxt-mip1-unguarded", lambda r: make_fixture(r, viewcopy=(244, 244)), "dxt-safety", WARN,
            repo=mutants[MIP_LIMIT_SITES[1][0]])
        one("f-dxt-mip0", lambda r: make_fixture(r, picture_px=254, ppm=254 / 256), "dxt-safety", FAIL)
        # (g) a menu set with a floor sidecar missing, and one whose sidecars hold no step 0
        def no_sidecar(r):
            folder = make_fixture(r)
            (folder / "Fixture-0.dist.png").unlink()
        one("g-menu-sidecar-missing", no_sidecar, "menu-sidecars", FAIL)
        one("g-menu-sidecar-no-step0", lambda r: make_fixture(r, sidecar_zero=False), "menu-sidecars", FAIL)
        # (h) a set-aside copy over a (lowered) limit
        def backup(r):
            folder = make_fixture(r)
            shutil.copytree(folder, r / "Fixture.bak-20261004-000000")
        one("h-backups", backup, "backups", WARN, backup_limit=1024)

        # (a) TileStore.CutOutTile mirrored: a 64 px alpha page, tile A (0,0 from the bottom) opaque, tile B with ONE clear
        #     texel, tile C on an opaque page; and the rect counted from the page's bottom (a clear texel in tile A's rows
        #     read top-down must not cut A)
        np = _np()
        folder = tmp / "tiles"
        folder.mkdir()
        page = np.full((64, 64, 4), 200, np.uint8)
        page[63 - 5, 20, 3] = 0          # PNG row 58 = 5 px up from the bottom: inside tile B (x 16..31, y 0..15)
        page[5, 2, 3] = 0                # PNG row 5 = 58 px up: NOT inside tile A (x 0..15, y 0..15)
        _png(folder / "p0.png", page)
        fake = type("F", (), {})()
        fake.tile_list = [(0, 0, 0, 16, 16), (0, 16, 0, 16, 16), (1, 0, 0, 16, 16)]
        fake.alpha_pages = 0b01
        set_ = type("S", (), {})()
        set_.meta = {"atlas": [{"page": 0, "file": "p0.png"}, {"page": 1, "file": "p1.png"}]}
        set_.folder = folder
        got = [bool(x) for x in cut_out_tiles(set_, fake)]
        results.append(Result(LAYER, "self-test a-cut-out-tiles", PASS if got == [False, True, False] else FAIL,
                              f"cut out per tile {got} (wanted [False, True, False])"))

        # (a) the reviewer's mutation on a real set: a COPY of the client's Labyrinth with its floor picture made fully
        #     clear-black must FAIL, and the unmutated copy must not
        source = Path(getattr(ctx, "captures", "") or "") / "Labyrinth"
        if not (source / "Labyrinth.map.json").is_file():
            results.append(Result(LAYER, "self-test a-labyrinth-clear-black", SKIP, f"no Labyrinth set at {source}"))
        else:
            for label, mutate, want in (("a-labyrinth-copy", False, PASS), ("a-labyrinth-clear-black", True, FAIL)):
                def build(r, mutate=mutate):
                    shutil.copytree(source, r / "Labyrinth")
                    if mutate:
                        meta = json.loads((r / "Labyrinth" / "Labyrinth.map.json").read_text(encoding="utf-8-sig"))
                        for f in meta["floors"]:
                            _png(r / "Labyrinth" / f["file"], np.zeros((f["height"], f["width"], 4), np.uint8))
                one(label, build, "black-faces", want)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    return results


if __name__ == "__main__":
    import argparse
    p = argparse.ArgumentParser(description="Layer A alone: audits of the captured map sets.")
    p.add_argument("--map", action="append", default=[])
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    c = Context(maps=a.map)
    rows = self_test(c) if a.self_test else run(c)
    for r in rows:
        print(f"{r.status:4}  {r.name:40}  {r.detail}")
    sys.exit(1 if any(r.status == FAIL for r in rows) else 0)
