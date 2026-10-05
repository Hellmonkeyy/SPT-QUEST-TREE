# Quest Tree offline tests

## Running them

Run everything from the repo root with `python tools/run-tests.py`. It is tested on Python 3.12 and 3.14, with numpy and Pillow. It checks the real install, prints a table, and exits 0 only when no row is FAIL. A full run takes about 80 seconds (measured 2026-10-04). Most of that time goes to reading the large mesh files, decoding the floor pictures, and check-capture, which runs alongside and takes about 57 seconds.

`--layer NAME` runs only that layer, and the flag can be repeated. The names are `sets`, `logs`, `install`, `unit` and `tools`.

`--map KEY` restricts the run to one map, for example `--map Woods`, and can also be repeated. It narrows the set audits, and it also narrows check-capture: the runner imports check-capture and runs its `check_capture()` in-process on just the chosen maps' capture folders.

`--quick` leaves out check-capture and every check that reads a mesh: black-faces, mesh-heights, and the atlas tiles of dxt-safety. Everything else still runs, in about 6 seconds.

`--self-test` runs every layer's own must-fail fixtures instead of the real checks. Each fixture is built to break exactly one check, and the self-test fails if that check does not catch it. It takes about 6 seconds.

`--json out.json` also writes every row to a file. `--verbose` prints the PASS rows and the full details as well.

The paths default to the usual install. You can override them with `--captures`, `--host-maps`, `--zones`, `--log-output`, `--player-log`, `--server-log` and `--install-root`.

Nothing here writes under `C:\Games\SPT`. The fixtures are built in a temporary folder and deleted afterwards.

You need Python (tested on 3.12 and 3.14) with numpy and Pillow. The `unit` layer and `check-relief-simplify` also need the .NET SDK.

## What each layer covers

**sets** (`audit_sets.py`) audits every captured map set. That means the client's `BepInEx\plugins\QuestTree\captures` and the host's `user\mods\QuestTree\maps`. Backup folders (`.bak-`, `.old-`) are only counted, never audited. It reuses the readers in `tools/check-capture.py` and `tools/check-maps-pack.py`. Each mesh is read once per sha256, so a client set and its host copy share the read.

- `black-faces`: simulates the 3D viewer's current floor routing (`FloorForFace`, `FloorThatDrew`, `CaptureWindow` in `Map3DView.cs`) on every face of the stored mesh. It measures how much roof area samples a clear, near-black texel of the picture the viewer draws, which shows up as black blobs.
  - A face counts when the viewer gives it the top picture: n.y >= 0.5 when the set has side pictures (|n.y| >= 0.5 without), and not a ground skirt.
  - An atlas face counts only when the roofs come from the picture. That needs the lowest drawn density across the floors to be at least 6 px/m, where a viewing copy's density is ppm × viewWidth / width. The face must also face up, and its tile must not be cut out: a tile on an alpha page with any texel of alpha under 128, as `TileStore.CutOutTile` decides.
  - It FAILs over 5 % of the roof area and WARNs over 3 %. On 2026-10-04 the stored sets read Woods 1.42 %, RezervBase 1.15 % and Shoreline 3.86 % (WARN). The rule from before the fix gives Woods 22.4 % and RezervBase 21.4 %. The detail gives that pre-fix area as a record of the margin.
  - Host sets are skipped. They are JPEGs with no alpha, because the client flattens every clear texel onto the backdrop colour before uploading, so they cannot show this defect.
- `extent`: the extent is finite and between 10 m and 20 km on each axis. Every floor picture is ceil(span × px/m), within 1 px on the client and 2 px on the host, and its file header agrees.
- `floors`: WARN above 8 floors (the mesh format's band cap; Shoreline and Icebreaker have 8), and FAIL for a band thinner than 1 m.
- `mesh-heights`: FAIL when a building reaches more than 300 m past the ground. The ground is the floor bands joined with the relief's hits. This was the Lab's ±1000 m helper planes. It also FAILs when a building vertex sits on the y quantisation's end codes, because that means it was clamped.
- `view-copies`: a floor picture longer than 8192 px must have its viewing copy, at the size `MapCapture.ViewSize` gives, with sides that are multiples of 4.
- `dxt-safety`: every texture the viewer block-compresses must also be a multiple of 4 at mip 1. That covers floor pictures or their viewing copies, sides and atlas tiles.
  - When only mip 1 fails, the row PASSes as long as the viewer creates its textures with `ignoreMipmapLimit` at both sites. Otherwise it WARNs.
  - A separate row, `dxt-mip-limit-guard`, reads `Map3DView.cs` (the atlas tiles) and `DynamicMapsLibrary.cs` (the floor and side pictures). It FAILs if either file stops setting `ignoreMipmapLimit`, whether as the seventh `Texture2D` constructor argument, as a named `ignoreMipmapLimit: true`, or as an assignment.
  - A client picture or tile that is not 4-aligned at mip 0 is a FAIL, because it silently stays uncompressed.
- `menu-sidecars`: a set with `capturedIn: "menu"` has a distance sidecar beside every floor and side, at the picture's size. Sidecars up to 40 Mpx are decoded and must hold step-0 pixels.
- `backups`: the size of the set-aside copies per map. WARN over 5 GB in total.

**logs** (`check_logs.py`) applies signature rules to the last session of `LogOutput.log`, `Player.log` and the newest server log.

**install** (`check_install.py`) checks the installed DLLs, their versions against the repo, and leftover folders.

**unit** (`unit/run_unit.py`, rows shown under layer `C`) compiles the shipping code's marked pure-logic regions into C# harnesses and runs them. Under `--self-test` it runs mutated copies, each of which must fail.

**tools** runs the scripts that already existed, as subprocesses in parallel with the layers, with one row each:
- `check-capture.py` on the captures and zones folders. Its errors for a capture folder with no meta are reported as one WARN row, "empty capture folder", and only its other errors make it FAIL;
- `check-relief-simplify.py`;
- `mesh_parts.py --self-test`;
- `check-dtos.py`;
- `check-maps-pack.py` on `Source\Tarkov-QuestTree-Server\maps`. It is skipped when that release folder does not exist.

Under `--self-test` only the two scripts that carry their own must-fail cases run.

## Adding a check

To add a check to a layer, write a function that returns a `Result(layer, name, status, detail)`. `Result` and the PASS/FAIL/WARN/SKIP constants are in `testlib.py`. Call the function from the layer's `run(ctx)`.

Then add a fixture to the layer's `self_test(ctx)` that the new check must FAIL, or WARN if WARN is its worst outcome. Prove that the check can fail before trusting a PASS from it. In `audit_sets.py`, `make_fixture` builds a small set (pictures, a v4 mesh and a meta) that you can bend into the defect.

To add a whole layer, put a module under `tools/tests/` with `run(ctx)` and, ideally, `self_test(ctx)`, and add it to `LAYERS` in `tools/run-tests.py`.
- A row may be a `Result`, any 4-tuple in that order, or a dict with those keys.
- `ctx` is a `testlib.Context`. Read its paths (`captures`, `host_maps`, `zones`, `log_output`, `player_log`, `server_log`, `install_root`, `repo_root`) instead of hard-coding them, and honour `ctx.maps` when the layer works per map.
- A layer that returns no rows, or raises, is reported as a FAIL.
