# In-game self-test report

Written by `Source/Tarkov-QuestTree/QuestGraph/SelfTest.cs`. To start it, switch on F12 > Advanced > "Run self-test (main menu)" in the main menu.

- **File:** `<SPT>/BepInEx/plugins/QuestTree/selftest/selftest-<yyyyMMdd-HHmmss>.json`, one file per run. The time is local. A refused run writes one too.
- **Log line:** `QuestTree: self-test: N passed, M failed - PASS|FAIL|CANCELLED|REFUSED (reason) in S s; report <path>; failed: step/map: check | ...` (in LogOutput.log).
- **Player.log markers:** `QUESTTREE-SELFTEST-BEGIN <runId>` and `QUESTTREE-SELFTEST-END <runId>`, logged through Unity. The Player.log lines between them belong to the run.

## d3d11 errors: what the in-game counter can and cannot see

The counter listens on `Application.logMessageReceivedThreaded`, so it sees messages from every thread, the render thread included. The `logcanary` step proves this: it logs a warning from a worker thread and fails with "error counter not receiving threaded logs" if the handler never sees it.

Unity's NATIVE d3d11 messages may be written only to Player.log and never raised to managed callbacks. The in-game counter cannot see those. **`check_logs.py` must scan Player.log between the BEGIN and END markers**, or between `startedLocal` and `finishedLocal`, and treat those lines as the authority for d3d11 errors.

## Top level

| field | type | meaning |
|---|---|---|
| `schema` | int | `1` |
| `kind` | string | `"questtree-selftest"` |
| `runId` | string | 12 hex chars; also in the markers |
| `overall` | string | `pass`, `fail`, `cancelled`, `refused` (preconditions failed, nothing run) |
| `passed`, `failed` | int | counts of every check, across steps and maps |
| `cancelReason` | string/null | why it was cancelled, refused or died |
| `startedUtc`, `finishedUtc` | ISO 8601 UTC | |
| `startedLocal`, `finishedLocal` | `yyyy-MM-dd HH:mm:ss.fff` | the machine's local time, for matching log files |
| `beginMarker`, `endMarker` | string/null | the exact marker lines (null on a refused run) |
| `seconds` | number | the whole run |
| `versions` | object | `mod`, `build` (version+git hash), `game`, `unity`, `gpu`, `graphicsApi`, `gpuMemoryMb`, `systemMemoryMb`, `os` |
| `steps` | array of Part | in order: `preconditions`, `logcanary`, `capture`, `sweep3d`, `sweep2d`, `cleanup` (only `preconditions` when refused) |
| `messages` | object | Unity's messages during the run (threaded handler): `exceptions`, `errors` (Error and Assert), `gpuErrors`, `handler`, `first` (up to 30 distinct Error/Exception/Assert lines, `"<LogType> [<phase>]: <text>"`) |
| `notes` | array of string | guarded side actions that threw |

`gpuErrors` counts ONLY Error, Exception and Assert messages whose text is a d3d11/d3d12/DXGI failure (with "fail", "error", "invalid", "could not", "unable to", "E_INVALIDARG" or "E_OUTOFMEMORY") or a texture that "failed to create" or "could not create". Info lines such as "D3D11 device created for Microsoft Media Foundation..." never count.

## Part (a step, or one map inside a sweep)

| field | type | meaning |
|---|---|---|
| `name` | string | step name, or the map's set key in `maps` |
| `source` | string | maps only: `local` (captures/) or `host` (the host's sets) |
| `result` | string | `pass`, `fail`, `skipped` (after a cancel) |
| `seconds` | number | |
| `error` | string/null | the exception that ended it |
| `checks` | array | `{ "name": string, "pass": bool, "detail": string }` |
| `measurements` | object | name -> number/string/bool |
| `maps` | array of Part | `sweep3d` and `sweep2d` only |

## Every check, by step

Any step or map can also carry `ran without throwing` (fails when it threw).

**preconditions**
- `main menu only, no capture running, hideout not loaded`
- `the delete guard refuses a real set's key`
- `the delete guard refuses a path escape`
- `the delete guard refuses a key that is not this run's`
- `the delete guard accepts this run's test key`
- `an existing test set refuses the capture`
- `the GPU-error filter counts a d3d11 texture failure`
- `the GPU-error filter ignores the Media Foundation info line`

**logcanary**
- `error counter receives threaded logs`
- measurement: `token`

**capture**
- `the menu host is free`
- `a map to capture`: fails if every capturable map already has a `-menu` set
- `writes the test key, never the real set`
- `the test key is never uploaded`
- `no test set of that key exists already`: fails as "a test set <key> already exists; delete it or pick another map"
- `the capture started`
- `the capture wrote a set`
- `floors written`
- `mesh written`
- `sides written`
- `the host's restore matches`
- `texture mip limit and SD flag back to baseline`
- `NavMesh vertices back to 0`
- `no d3d11 or texture-creation errors`
- measurements: `candidates`, `map`, `mapName`, `testKey`, `seconds`, `vramBeforeMb`, `vramAfterMb`, `mipLimitBefore`, `sdModeBefore`, `navMeshVerticesBefore`, `unityExceptions`, `unityErrors`

**sweep3d**
- step: `the menu host is free`, `the catalog has a set with a 3D mesh`; measurements `sets`, `setsWithMesh`
- per map:
  - `in the main menu`
  - `no map hosted in the menu`
  - `the view opened`
  - `first frame drawn`
  - `still drawing after it settled`
  - `its meshes and textures were destroyed`
  - `VRAM back near its before-value`: tolerance 256 MB; fails when VRAM is unread
  - `no d3d11 or texture-creation errors and no exceptions`
- per-map measurements: `level`, `attachMs`, `firstFrameMs`, `drawCalls`, `trianglesSubmitted`, `trianglesInView`, `renderMs`, `settledSeconds`, `idle`, `framesRendered`, `vramBeforeMb`, `vramOpenMb`, `vramAfterMb`, `leftoverObjects`

**sweep2d**
- step: `the menu host is free`, `the catalog has a set`; measurement `sets`
- per map:
  - `in the main menu`
  - `no map hosted in the menu`
  - `floor<L>: the viewing copy is used`: only when `<key>-<L>.view.png` is on disk
  - `floor<L>: the picture decodes`
  - `no d3d11 or texture-creation errors and no exceptions`
- per-map measurements: `floors`, `floor<L>.viewCopy`, `floor<L>.decodeMs`, `floor<L>.size`

**cleanup** (always runs)
- `the test capture has ended`
- `test set removed`: passes as "nothing to remove" when no capture started or none wrote a folder
- `real sets untouched`
- `texture mip limit and SD flag at the run's baseline`
- `NavMesh vertices at the run's baseline`
- measurements: `testKey`, `testFolderDeleted`
- a dead run (its coroutine stopped) instead gets `ran without dying` and `test set removed`

VRAM values are in MB, and `-1` means unread.
