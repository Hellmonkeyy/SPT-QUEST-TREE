#!/usr/bin/env python3
"""The one entry point for every offline check of the mod. See tools/tests/README.md.

    python tools/run-tests.py                     everything, on the real install
    python tools/run-tests.py --layer sets --map Woods
    python tools/run-tests.py --self-test         every layer's must-fail fixtures
    python tools/run-tests.py --json out.json     every row, also as JSON

Layers are modules found under tools/tests/ (audit_sets.py, check_logs.py, check_install.py, unit/run_unit.py - any
that is missing is skipped), each exposing run(ctx) -> rows and, optionally, self_test(ctx) -> rows. The existing
scripts (check-capture, check-relief-simplify, mesh_parts, check-dtos, check-maps-pack) run as subprocesses in
parallel with the layers, one row each. Exit 0 only when no row is FAIL. Nothing under the install is written.
"""

import argparse
import importlib.util
import json
import re
import subprocess
import sys
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
REPO = TOOLS.parent
TESTS = TOOLS / "tests"
sys.path.insert(0, str(TESTS))
from testlib import FAIL, PASS, SKIP, STATUSES, WARN, Context, Result, load_tool  # noqa: E402

# name -> path under tools/tests; the name is what --layer takes
LAYERS = {
    "sets": TESTS / "audit_sets.py",
    "logs": TESTS / "check_logs.py",
    "install": TESTS / "check_install.py",
    "unit": TESTS / "unit" / "run_unit.py",
}
# the layers run on a worker thread rather than the main one (they mostly wait on a dotnet build)
THREADED_LAYERS = {"unit"}
# the layer column a layer's rows carry (unit/run_unit.py reports as "C"); the runner's own rows for a layer use it too
LAYER_LABELS = {"unit": "C"}

# check-capture's ERROR for a capture folder with no meta - an empty folder a refused or abandoned capture leaves
EMPTY_FOLDER = re.compile(r"^ERROR\s+(\S+): no \S+\.map\.json in .* a capture folder without its meta is a half-written")

TOOL_TIMEOUT = 600   # s, per existing script


# --- the existing scripts ------------------------------------------------------------------------------------------------------

def _schema():
    """MapCatalog.SupportedCaptureSchema, as package.ps1 reads it for check-maps-pack."""
    text = (REPO / "Source" / "Tarkov-QuestTree" / "UI" / "MapCatalog.cs").read_text(encoding="utf-8", errors="replace")
    match = re.search(r"SupportedCaptureSchema\s*=\s*(\d+)", text)
    return match.group(1) if match else None


IN_PROCESS = "in-process"   # existing_tools' argv for check-capture under --map


def check_capture_in_process(ctx):
    """check-capture on just the --map folders, in this process: check-capture takes a captures ROOT, not a map, so this
    calls its own check_capture(folder, errors, warnings) per chosen folder - the same function its main() calls per
    folder, with the same backup skip - and its zones folder set as the script's ZONES. Its output is rebuilt in main()'s
    WARN/ERROR line format and judged by capture_rows, exactly like the subprocess run."""
    started = time.time()
    try:
        cc = load_tool("check-capture.py")
        cc.ZONES = Path(ctx.zones)
        folders = [p for p in sorted(ctx.captures.iterdir())
                   if p.is_dir() and p.name.lower() in ctx.maps and cc.SET_ASIDE_INFIX not in p.name.lower()]
        errors, warnings = [], []
        for folder in folders:
            cc.check_capture(folder, errors, warnings)
    except Exception as exc:
        return [Result("tools", "check-capture", FAIL, f"in-process run raised {type(exc).__name__}: {exc}")]
    lines = [f"WARN   {w}" for w in warnings] + [f"ERROR  {e}" for e in errors]
    lines.append(f"{len(folders)} capture(s) checked ({', '.join(p.name for p in folders) or 'none matched --map'}), "
                 f"{len(errors)} problem(s)" + (f", {len(warnings)} warning(s)" if warnings else ""))
    rows = capture_rows(1 if errors else 0, "\n".join(lines), time.time() - started)
    return [Result(r.layer, r.name, r.status, r.detail.replace("exit ", "in-process, exit ", 1)) for r in rows]


def existing_tools(ctx, self_test, quick=False):
    """[(name, argv or None, why skipped)] for the scripts that already exist in tools/. Under --self-test only the ones
    that carry their own must-fail cases run; --quick leaves out check-capture (it reads every mesh)."""
    py = sys.executable
    tools = []
    if self_test:
        tools.append(("mesh_parts --self-test", [py, str(TOOLS / "mesh_parts.py"), "--self-test"], None))
        tools.append(("check-relief-simplify", [py, str(TOOLS / "check-relief-simplify.py")], None))
        for name in ("check-capture", "check-dtos", "check-maps-pack"):
            tools.append((name, None, "has no must-fail mode of its own"))
        return tools
    if quick:
        tools.append(("check-capture", None, "--quick: it reads every mesh"))
    elif not ctx.captures.is_dir():
        tools.append(("check-capture", None, f"no captures folder {ctx.captures}"))
    elif ctx.maps:
        tools.append(("check-capture", IN_PROCESS, None))
    else:
        tools.append(("check-capture", [py, str(TOOLS / "check-capture.py"), str(ctx.captures), str(ctx.zones)], None))
    tools.append(("check-relief-simplify", [py, str(TOOLS / "check-relief-simplify.py")], None))
    tools.append(("mesh_parts --self-test", [py, str(TOOLS / "mesh_parts.py"), "--self-test"], None))
    tools.append(("check-dtos", [py, str(TOOLS / "check-dtos.py")], None))
    # check-maps-pack checks the RELEASE folder package.ps1 fills; it is offline, but there is nothing to check without it
    pack = REPO / "Source" / "Tarkov-QuestTree-Server" / "maps"
    schema = _schema()
    if not pack.is_dir():
        tools.append(("check-maps-pack", None, f"no release maps folder {pack} (package.ps1 -RefreshMaps fills it)"))
    elif schema is None:
        tools.append(("check-maps-pack", None, "SupportedCaptureSchema not found in MapCatalog.cs"))
    else:
        tools.append(("check-maps-pack", [py, str(TOOLS / "check-maps-pack.py"), str(pack), "--schema", schema], None))
    return tools


def summary_of(output):
    """A one-line detail: the script's last non-empty line, plus its first ERROR lines."""
    lines = [l.rstrip() for l in output.splitlines() if l.strip()]
    last = lines[-1] if lines else "(no output)"
    errors = [l for l in lines if l.lstrip().startswith(("ERROR", "FAIL"))]
    if errors and errors[0] != last:
        return f"{last} | first: {errors[0].strip()[:200]}"
    return last


def run_tool(name, argv):
    started = time.time()
    try:
        done = subprocess.run(argv, cwd=str(REPO), capture_output=True, text=True, timeout=TOOL_TIMEOUT,
                              encoding="utf-8", errors="replace")
    except subprocess.TimeoutExpired:
        return [Result("tools", name, FAIL, f"timed out after {TOOL_TIMEOUT} s")], ""
    except OSError as exc:
        return [Result("tools", name, FAIL, f"could not start: {exc}")], ""
    output = (done.stdout or "") + (done.stderr or "")
    seconds = time.time() - started
    if name == "check-capture":
        return capture_rows(done.returncode, output, seconds), output
    status = PASS if done.returncode == 0 else FAIL
    detail = f"exit {done.returncode}, {seconds:.0f} s: {summary_of(output)}"
    return [Result("tools", name, status, detail)], output


def capture_rows(code, output, seconds):
    """check-capture's run as rows: its no-meta ERRORs (the empty folder a refused or abandoned capture leaves) as one WARN
    "empty capture folder", and FAIL only for the ERRORs left - or for a non-zero exit with no ERROR line to explain it."""
    lines = output.splitlines()
    errors = [l.strip() for l in lines if l.lstrip().startswith("ERROR")]
    empty = [m.group(1) for m in (EMPTY_FOLDER.match(e) for e in errors) if m]
    others = [e for e in errors if not EMPTY_FOLDER.match(e)]
    last = next((l.strip() for l in reversed(lines) if l.strip()), "(no output)")
    rows = []
    if empty:
        rows.append(Result("tools", "check-capture empty capture folder", WARN,
                           f"{len(empty)} folder(s) with no meta: {', '.join(empty)}"))
    if others or (code != 0 and not empty):
        first = others[0][:200] if others else "no ERROR line"
        rows.append(Result("tools", "check-capture", FAIL,
                           f"exit {code}, {seconds:.0f} s: {len(others)} error(s) besides empty folders - {last} | "
                           f"first: {first}"))
    else:
        rows.append(Result("tools", "check-capture", PASS, f"exit {code}, {seconds:.0f} s: {last}"
                           + (" (its only errors are the empty folders)" if empty else "")))
    return rows


# --- the layers ------------------------------------------------------------------------------------------------------------------

def load_layer(name, path):
    spec = importlib.util.spec_from_file_location(f"qt_layer_{name}", path)
    module = importlib.util.module_from_spec(spec)
    sys.path.insert(0, str(path.parent))
    spec.loader.exec_module(module)
    return module


def normalise(layer, rows):
    """Any row shape the layers return - Result, a namedtuple of the same fields, a 4-tuple, a dict - as a Result."""
    out = []
    for row in rows or []:
        if isinstance(row, dict):
            r = Result(row.get("layer", layer), row.get("name", "?"), row.get("status", FAIL), row.get("detail", ""))
        else:
            r = Result(*tuple(row)[:4])
        status = str(r.status).upper()
        if status not in STATUSES:
            r = Result(r.layer, r.name, FAIL, f"unknown status {r.status!r}: {r.detail}")
        else:
            r = Result(str(r.layer or layer), str(r.name), status, str(r.detail))
        out.append(r)
    return out


def run_layer(name, path, ctx, self_test):
    started = time.time()
    label = LAYER_LABELS.get(name, name)
    rows = _run_layer(name, label, path, ctx, self_test)
    # one column per layer: every row of this layer under its label, whatever the module wrote
    rows = [Result(label, r.name, r.status, r.detail) for r in rows]
    if ctx.verbose:
        print(f"  [{label}] {len(rows)} row(s) in {time.time() - started:.0f} s", flush=True)
    return rows


def _run_layer(name, label, path, ctx, self_test):
    if not path.is_file():
        return [Result(label, "layer", SKIP, f"{path.relative_to(REPO)} is not there")]
    try:
        module = load_layer(name, path)
        if self_test and hasattr(module, "self_test"):
            rows = module.self_test(ctx)
        elif self_test and name == "unit":
            rows = module.run(ctx)          # run_unit reads ctx.self_test
        elif self_test:
            return [Result(label, "layer", SKIP, "has no self_test()")]
        else:
            rows = module.run(ctx)
        rows = normalise(label, rows)
    except Exception as exc:
        tb = traceback.format_exc().strip().splitlines()
        return [Result(label, "layer", FAIL, f"raised {type(exc).__name__}: {exc} ({tb[-3].strip() if len(tb) > 2 else ''})")]
    if not rows:
        rows = [Result(label, "layer", FAIL, "returned no rows - a layer that checks nothing cannot pass")]
    return rows


# --- the table -------------------------------------------------------------------------------------------------------------------

def print_table(rows, verbose):
    """Every non-PASS row, and per layer a count of its PASS rows (all of them with --verbose)."""
    width = min(48, max((len(r.name) for r in rows), default=10))
    by_layer = {}
    for r in rows:
        by_layer.setdefault(r.layer, []).append(r)
    order = {FAIL: 0, WARN: 1, SKIP: 2, PASS: 3}
    for layer, items in by_layer.items():
        counts = {s: sum(1 for r in items if r.status == s) for s in STATUSES}
        print(f"\n== {layer}: " + ", ".join(f"{counts[s]} {s}" for s in STATUSES if counts[s]))
        for r in sorted(items, key=lambda r: order[r.status]):
            if r.status == PASS and not verbose:
                continue
            detail = r.detail if verbose or len(r.detail) <= 160 else r.detail[:157] + "..."
            print(f"  {r.status:4}  {r.name:{width}}  {detail}")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Runs every offline check of the Quest Tree mod.")
    parser.add_argument("--layer", action="append", default=[],
                        help=f"only these (repeatable): {', '.join(LAYERS)}, tools")
    parser.add_argument("--map", action="append", default=[], help="only this map key (repeatable; layer 'sets')")
    parser.add_argument("--json", metavar="OUT", help="write every row to this JSON file")
    parser.add_argument("--self-test", action="store_true", help="run every layer's must-fail fixtures instead")
    parser.add_argument("--verbose", "-v", action="store_true", help="print PASS rows and full details too")
    parser.add_argument("--quick", action="store_true",
                        help="leave out check-capture and every check that reads a mesh (well under 15 s)")
    parser.add_argument("--captures", help="the client captures folder")
    parser.add_argument("--host-maps", help="the host's maps folder")
    parser.add_argument("--zones", help="the host's zones folder (check-capture's cross-check)")
    parser.add_argument("--log-output", help="BepInEx LogOutput.log")
    parser.add_argument("--player-log", help="the game's Player.log")
    parser.add_argument("--server-log", help="the server log (default: the newest in user/logs/spt)")
    parser.add_argument("--install-root", help="the SPT install (default C:\\Games\\SPT)")
    args = parser.parse_args(argv)

    known = set(LAYERS) | {"tools"}
    wanted = set(args.layer) or known
    unknown = wanted - known
    if unknown:
        parser.error(f"unknown layer(s) {', '.join(sorted(unknown))}; known: {', '.join(sorted(known))}")

    ctx = Context(captures=args.captures, host_maps=args.host_maps, zones=args.zones, log_output=args.log_output,
                  player_log=args.player_log, server_log=args.server_log, maps=args.map, verbose=args.verbose,
                  install_root=args.install_root, self_test=args.self_test)
    ctx.quick = args.quick
    started = time.time()
    print(f"Quest Tree tests{' (self-test)' if args.self_test else ''}{' (quick)' if args.quick else ''}: "
          f"layers {', '.join(sorted(wanted))}"
          + (f", maps {', '.join(args.map)}" if args.map else ""), flush=True)

    rows = []
    pool = ThreadPoolExecutor(max_workers=6)
    futures = []
    if "tools" in wanted:
        for name, argv_, why in existing_tools(ctx, args.self_test, args.quick):
            if argv_ is None:
                rows.append(Result("tools", name, SKIP, why))
            else:
                futures.append(pool.submit(check_capture_in_process, ctx) if argv_ == IN_PROCESS
                               else pool.submit(lambda n=name, a=argv_: run_tool(n, a)[0]))
    for name, path in LAYERS.items():
        if name in wanted and name in THREADED_LAYERS:
            futures.append(pool.submit(run_layer, name, path, ctx, args.self_test))
    for name, path in LAYERS.items():
        if name in wanted and name not in THREADED_LAYERS:
            rows.extend(run_layer(name, path, ctx, args.self_test))
    for f in futures:
        rows.extend(f.result())
    pool.shutdown()

    print_table(rows, args.verbose)
    fails = sum(1 for r in rows if r.status == FAIL)
    warns = sum(1 for r in rows if r.status == WARN)
    elapsed = time.time() - started
    print(f"\n{len(rows)} row(s): {fails} FAIL, {warns} WARN, "
          f"{sum(1 for r in rows if r.status == SKIP)} SKIP, {sum(1 for r in rows if r.status == PASS)} PASS "
          f"in {elapsed:.0f} s - {'FAILED' if fails else 'OK'}")

    if args.json:
        Path(args.json).write_text(json.dumps({
            "selfTest": args.self_test, "seconds": round(elapsed, 1), "fails": fails, "warns": warns,
            "rows": [r._asdict() for r in rows]}, indent=1), encoding="utf-8")
        print(f"rows written to {args.json}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
