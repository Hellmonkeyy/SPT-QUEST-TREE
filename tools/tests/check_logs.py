#!/usr/bin/env python3
"""Signature rules over the game and server logs, for ONE session.

    python tools/tests/check_logs.py              # the real logs (paths below)
    python tools/tests/check_logs.py --self-test  # every rule fires on its fixture, none on the clean ones

Layer contract (tools/tests/testlib.py): run(ctx) -> list[Result], self_test(ctx) -> list[Result]. Summary ("INFO")
results are PASS rows whose name starts "info:". ctx is a testlib.Context (a dict or None also works):
    log_output       C:\\Games\\SPT\\BepInEx\\LogOutput.log
    player_log       %USERPROFILE%\\AppData\\LocalLow\\Battlestate Games\\EscapeFromTarkov\\Player.log
    player_prev_log  Player-prev.log beside player_log
    server_log       newest *.log in C:\\Games\\SPT\\SPT_Runtime\\user\\logs\\spt
    repo_root        the repo, for the rule-anchor check over Source/**/*.cs

Sessions: a client log is read from its LAST Unity start ("Mono path[0] =") or, without one, its last BepInEx
header; the server log from its last ModLoader start. Player-prev.log is the previous launch as a whole: what a
FAIL rule finds there is reported as WARN ("previous launch"), since the current launch did not produce it.

Stale server log: the user plays on a remote host, so the local server log can be hours older than the game
session. When its last line is more than STALE_HOURS before the client session started, the server rules are
SKIPped with a WARN saying so. The client start is the client session's "session start YYYY-MM-DD HH:MM:SS" line
when one is logged (SptCompatFixes writes it), else Player-prev.log's last write (Unity rotates it at launch).

Rule anchors: every rule over our own messages names literal fragments of the C# that writes them; the anchor
check FAILs "rule X no longer matches any source text" when a fragment is gone from Source/**/*.cs, so a reworded
message cannot leave a rule silently matching nothing. Rules over Unity / SPT text have no anchors.

Every rule cites the C# line that writes its text (working tree of 2026-10-04, HEAD 5360921 + uncommitted edits);
the regex keys on the text, so a line moving does not break it. Paths are read, never written.
"""

import datetime
import json
import os
import re
import sys
from collections import namedtuple

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
from testlib import Context, Result, PASS, FAIL, WARN, SKIP  # noqa: E402

LAYER = "logs"
FIXTURES = os.path.join(HERE, "fixtures", "logs")
REPO = os.path.dirname(os.path.dirname(HERE))

# Thresholds for the WARN parsers.
FIRST_FRAME_MS = 1000.0
DRAW_CALLS = 3000
VRAM_SHARE = 0.85
STALE_HOURS = 1.0


def _get(ctx, key, default=None):
    if ctx is None:
        return default
    v = ctx.get(key) if isinstance(ctx, dict) else getattr(ctx, key, None)
    return default if v is None else v


def default_paths(ctx=None):
    if ctx is None:
        ctx = Context()
    player = str(_get(ctx, "player_log", ""))
    server = _get(ctx, "server_log")
    return {
        "logoutput": str(_get(ctx, "log_output", _get(ctx, "logoutput", r"C:\Games\SPT\BepInEx\LogOutput.log"))),
        "player_log": player,
        "player_prev_log": str(_get(ctx, "player_prev_log", os.path.join(os.path.dirname(player), "Player-prev.log"))),
        "server_log": str(server) if server else None,
        "repo_root": str(_get(ctx, "repo_root", REPO)),
    }


# --- version stamps (shared with check_install) --------------------------------------------------------------------

def stamp_problem(version):
    """Why a "1.19.0+5360921"-style stamp is bad, or None. Bad: built outside git ("fatal", "not a git
    repository"), or a bare version with no "+commit"."""
    if not version:
        return "no version"
    if re.search(r"fatal|not a git repository", version):
        return "built outside git (the stamp holds git's error)"
    if "+" not in version:
        return "no +commit in the stamp"
    if not re.fullmatch(r"[0-9a-f]{7,40}(-dirty)?", version.split("+", 1)[1]):
        return "the stamp's commit id is not a hash"
    return None


# --- sessions ------------------------------------------------------------------------------------------------------

UNITY_START = re.compile(r"^Mono path\[0\] = ")
BEPINEX_HEADER = re.compile(r"^\[Message:\s*BepInEx\] BepInEx \d+\.\d+")
SERVER_START = re.compile(r"\]\[SPTarkov\.Server\.Modding\.ModValidator\] ModLoader: loading: \d+ server mods")
SERVER_MODDING = re.compile(r"\]\[SPTarkov\.Server\.Modding\.")
SERVER_TIME = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")
CLIENT_SESSION_START = re.compile(r"session start (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)")


def read_lines(path):
    with open(path, "rb") as f:
        return f.read().decode("utf-8", errors="replace").splitlines()


def client_session(lines, whole=False):
    """From the last Unity start, else the last BepInEx header, to the end."""
    if whole:
        return lines
    for pat in (UNITY_START, BEPINEX_HEADER):
        idx = [i for i, l in enumerate(lines) if pat.search(l)]
        if idx:
            return lines[idx[-1]:]
    return lines


def server_session(lines):
    """From the last ModLoader start: its "loading: N server mods" line, walked back over the ModLoader lines
    just before it (the "built for a different version ... not loaded" line comes BEFORE that line)."""
    idx = [i for i, l in enumerate(lines) if SERVER_START.search(l)]
    if not idx:
        return lines
    i = idx[-1]
    while i > 0 and SERVER_MODDING.search(lines[i - 1]):
        i -= 1
    return lines[i:]


def _ts(text):
    return datetime.datetime.strptime(text, "%Y-%m-%d %H:%M:%S")


def server_span(lines):
    times = [m.group(1) for l in lines if (m := SERVER_TIME.match(l))]
    return (_ts(times[0]), _ts(times[-1])) if times else (None, None)


def client_start(lines, prev_path):
    """(datetime, how) for the client session's start, or (None, why)."""
    for l in lines:
        m = CLIENT_SESSION_START.search(l)
        if m:
            return _ts(m.group(1)), "its 'session start' line"
    try:
        if prev_path and os.path.isfile(prev_path):
            return (datetime.datetime.fromtimestamp(os.path.getmtime(prev_path)).replace(microsecond=0),
                    "Player-prev.log's last write")
    except OSError:
        pass
    return None, "no session start line and no Player-prev.log"


# --- rules ---------------------------------------------------------------------------------------------------------
# A rule: id, status, scope ("client"/"server"), a matcher, the source it cites, and the anchors - literal C# text
# the anchor check requires in Source/**/*.cs (empty for Unity / SPT text).

Rule = namedtuple("Rule", "id status scope match source anchors")


def rx(pattern, flags=0, unless=None):
    c = re.compile(pattern, flags)
    u = re.compile(unless) if unless else None
    return lambda lines: [l for l in lines if c.search(l) and not (u and u.search(l))]


def p_exceptions(lines):
    """Unity/Mono exception blocks whose stack mentions the QuestTree namespace. A block is the header line and
    the stack lines after it, up to the next BepInEx log line or two blank lines (cap 80)."""
    head = re.compile(r"^(?:\[(?:Error|Fatal)\s*:[^\]]*\]\s*)?(?:Rethrow as )?[A-Za-z_][\w.`]*Exception\b[^:]*:")
    hits = []
    i = 0
    while i < len(lines):
        if head.search(lines[i]) and not re.search(r"\]\s*QuestTree:", lines[i]):
            block = [lines[i]]
            blanks = 0
            j = i + 1
            while j < len(lines) and j - i < 80:
                l = lines[j]
                if l.startswith("[") and not l.startswith("[0x"):
                    break
                blanks = blanks + 1 if not l.strip() else 0
                if blanks >= 2:
                    break
                block.append(l)
                j += 1
            if any(re.search(r"\bQuestTree\.", b) for b in block):
                hits.append(lines[i].strip() + "  [stack mentions QuestTree]")
            i = j
        else:
            i += 1
    return hits


FIRST_FRAME = re.compile(r"QuestTree: 3D map for (\S+) - first frame drawn in ([\d.]+) ms, render [\d.]+ ms, (\d+) draw call")


def p_first_frame(lines):
    return [l for l in lines if (m := FIRST_FRAME.search(l)) and float(m.group(2)) > FIRST_FRAME_MS]


def p_draw_calls(lines):
    return [l for l in lines if (m := FIRST_FRAME.search(l)) and int(m.group(3)) > DRAW_CALLS]


VRAM = re.compile(r"VRAM ([\d,]+) of ([\d,]+) MB budget")


def p_vram(lines):
    hits = []
    for l in lines:
        m = VRAM.search(l)
        if m:
            used, budget = (int(g.replace(",", "")) for g in m.groups())
            if budget > 0 and used > budget * VRAM_SHARE:
                hits.append(l)
    return hits


BASELINE = re.compile(r": baseline: \d+ scene\(s\)")
AFTER = re.compile(r": after: \d+ scene\(s\)")
NAV = re.compile(r"; NavMesh vertices (\d+);")
MIP = re.compile(r"; texture mip limit (\S+?)( \(SD mode on\))?;")


def p_baseline_navmesh(lines):
    return [l for l in lines if BASELINE.search(l) and (m := NAV.search(l)) and int(m.group(1)) > 0]


def p_mip_changed(lines):
    """An "after:" line whose texture mip limit (and SD flag) differs from the baseline before it."""
    hits = []
    base = None
    for l in lines:
        if BASELINE.search(l):
            base = MIP.search(l)
        elif AFTER.search(l):
            now = MIP.search(l)
            if base is not None and now is not None and base.groups() != now.groups():
                hits.append(f"{l.strip()}  [baseline mip {base.group(1)}{base.group(2) or ''}]")
            base = None
    return hits


CLIENT_VERSION = re.compile(r"\] QuestTree (\d+\.\d+\.\d+.*?): loaded\.")
SERVER_VERSION = re.compile(r"\] Quest Tracker (\d+\.\d+\.\d+.*?): serving \d+ quests")


def p_stamp(pattern):
    return lambda lines: [f"{l.strip()}  [{stamp_problem(m.group(1))}]" for l in lines
                          if (m := pattern.search(l)) and stamp_problem(m.group(1))]


HOST_TOO_OLD = r"this server is older than the client"

RULES = [
    # ---- client FAIL ----
    Rule("d3d11_failed_create", FAIL, "client", rx(r"d3d11: failed to create"),
         "Unity (Player.log) - the mip-limit crash", []),
    Rule("unity_crash", FAIL, "client", rx(r"Crash!!!"), "Unity (Player.log) crash handler", []),
    Rule("questtree_exception", FAIL, "client", p_exceptions, "Unity/Mono exception block, stack in QuestTree.*", []),
    Rule("questtree_error_line", FAIL, "client", rx(r"^\[Error\s*:\s*QuestTree\]"),
         "BepInEx Plugin.LogSource.LogError (any)", []),
    Rule("route_unreachable", FAIL, "client",
         rx(r"QuestTree: could not reach (?:the QuestTreeServer companion mod on )?/questtree/"),
         "QuestDataClient.cs:290,1540,1645,1878,1921",
         ["QuestTree: could not reach {ProfileRoute}", "could not reach the QuestTreeServer companion mod on {Route}"]),
    Rule("restore_mismatch", FAIL, "client", rx(r"RESTART THE GAME ADVISED"),
         "MenuMapHost.cs:1020 (verdict when ctx.Trouble is non-empty), 1458 (emergency unload)",
         ["; RESTART THE GAME ADVISED: ", "not waited for. RESTART THE GAME ADVISED."]),
    Rule("restart_before_raid", FAIL, "client", rx(r"Restart the game before your next raid", re.I),
         "MenuCaptureRunner.cs:371 (UI twin MapView.cs:255)", [" Restart the game before your next raid ("]),
    Rule("capture_failed", FAIL, "client",
         rx(r"QuestTree: menu capture: .*(?:was not captured: its write failed|the capture failed: )"),
         "MenuCaptureRunner.cs:346 write failed; 341/357 with Problem 'the capture failed:' from MenuMapHost.cs:1010",
         ["was not captured: its write failed (", "the capture failed: {ctx.WorkFailure}"]),
    Rule("mip_limit_changed", FAIL, "client", p_mip_changed,
         "MenuMapHost.cs:930 baseline / 1395 after, MenuState.Describe 3035",
         ['Log($"baseline: {', 'Log($"after: {', "texture mip limit {MipLimit}"]),
    Rule("baseline_navmesh_left", FAIL, "client", p_baseline_navmesh,
         "MenuMapHost.cs:930, MenuState.Describe 3032 'NavMesh vertices N'", ["NavMesh vertices {NavVertices}; "]),
    Rule("upload_failed", FAIL, "client", rx(
        r"QuestTree: (?:the capture of \S+ could not be offered to the host"
        r"|the host refused the capture of "
        r"|.* could not be prepared for upload "
        r"|.* is not a PNG or JPEG of at most \d+ px a side"
        r"|.* could not be decoded from disk "
        r"|.* encoded to nothing and is not offered"
        r"|the host did not take \S+ 3D mesh"
        r"|could not start the map picture sync)", unless=HOST_TOO_OLD),
        "MapTransfer.cs LogWarning 973,2312,3465,2472,2591,3048,3107,3217,2487,3013,2502,3023,2645,3758,3774,3940",
        ["could not be offered to the host (", "the host refused the capture of {key}", "could not be prepared for upload ",
         "is not a PNG or JPEG of at most {sideLimit} px a side", "could not be decoded from disk ",
         "encoded to nothing and is not offered", "the host did not take {key}'s 3D mesh", "could not start the map picture sync"]),
    Rule("client_version_bad", FAIL, "client", p_stamp(CLIENT_VERSION),
         "Plugin.cs:98 'QuestTree {ModInfo.Stamp}: loaded.'", ["QuestTree {ModInfo.Stamp}: loaded."]),
    # ---- server FAIL ----
    Rule("server_mod_not_loaded", FAIL, "server",
         rx(r"Mod `QuestTree` (?:was built for a different version|.*not loaded)"), "SPT ModLoader (not our source)", []),
    Rule("server_unhandled_route", FAIL, "server", rx(r"\[UNHANDLED\]\[/questtree/"),
         "SPT SptLoggerMiddleware (route no mod registered)", []),
    Rule("server_version_bad", FAIL, "server", p_stamp(SERVER_VERSION),
         "QuestPayloadBuilder.cs:578 'Quest Tracker {ModInfo.Stamp}: serving N quests'",
         ["Quest Tracker {ModInfo.Stamp}: serving "]),
    # ---- WARN ----
    Rule("readbacks_failed", WARN, "client", rx(r"asynchronous tile readbacks failed"), "MapCapture.cs:5832",
         ["asynchronous tile readbacks failed in this capture"]),
    Rule("extent_refused", WARN, "client", rx(r"leaves \d+ of \d+ zones and spawn points outside"),
         "MapExtentProbe.cs:406-407", ["zones and spawn points outside it "]),
    Rule("nothing_captured", WARN, "client", rx(r"QuestTree: nothing was captured on "),
         "MapCapture.cs:4851,4860,4871,4897,4937,4964,4999,7622,7733", ["QuestTree: nothing was captured on {key}"]),
    Rule("buildings_past_ground", WARN, "client", rx(r"QuestTree: buildings past the ground on "),
         "MapMeshBuilder.cs:8851", ["QuestTree: buildings past the ground on {job.Request.Map}"]),
    Rule("heights_clamped", WARN, "client", rx(r"past the ground's .* its heights are clamped to "),
         "MapMeshBuilder.cs:4272-4274", ["its heights are clamped to {F(low)}"]),
    Rule("first_frame_slow", WARN, "client", p_first_frame,
         f"Map3DView.cs:9808 'first frame drawn in N ms' > {FIRST_FRAME_MS:.0f}",
         ["first frame drawn in {1:0.0} ms, render {2:0.0} ms, {3} draw call(s)"]),
    Rule("draw_calls_high", WARN, "client", p_draw_calls, f"Map3DView.cs:9808 'N draw call(s)' > {DRAW_CALLS}",
         ["first frame drawn in {1:0.0} ms, render {2:0.0} ms, {3} draw call(s)"]),
    Rule("vram_high", WARN, "client", p_vram, f"VramProbe.cs:46 'VRAM a of b MB budget' a > {VRAM_SHARE:.0%} of b",
         ["VRAM {0:#,##0} of {1:#,##0} MB budget"]),
    Rule("capture_refused", WARN, "client", rx(r"QuestTree: menu capture: .* was not captured - refused"),
         "MenuCaptureRunner.cs:357 (Problem 'refused: ...' from MenuMapHost.cs:1008)",
         ["was not captured - {outcome.Problem}", "refused: {ctx.Refusal}"]),
    Rule("upload_refused_host_old", WARN, "client",
         rx(r"QuestTree: the host (?:refused the capture of|did not take) .*" + HOST_TOO_OLD),
         "MapTransfer.cs:2312,3465,3758,3774 with the host's reason from MapStore.cs:492,1206",
         ["the host refused the capture of {key}", HOST_TOO_OLD.replace("than the client", "")]),
    Rule("upload_soft_failed", WARN, "client", rx(
        r"QuestTree: (?:the host could not be offered "
        r"|the upload of .* could not be started"
        r"|the upload of \S+ stopped with its host"
        r"|\d+ floor\(s\) of \S+ reached the host but it never had the whole set"
        r"|.* looks vertically flipped"
        r"|.* came out \d+x\d+ px where its meta says)"),
        "MapTransfer.cs 2280,3391,3427,3673,3724,1443 (LogInfo: 'the server half is optional'); 404,3285,2611 LogWarning",
        ["the host could not be offered {key}", "could not be started ({ex.Message})", "stopped with its host",
         "reached the host but it never had the whole set", "looks vertically flipped", "where its meta says"]),
    Rule("schema_newer_than_server", WARN, "client",
         rx(r"QuestTree: the server refused the zones for .*is newer than the v\d+ this server reads"),
         "ZoneHarvester.cs:295 with the reason from QuestTreeRouter.cs:393",
         ["the server refused the zones for {map}", "this server is older than the client, update the server half"]),
    Rule("server_schema_skipped", WARN, "server", rx(r"newer than the v\d+ this server reads"),
         "ZoneStore.cs:942; MapStore.cs:492,1206", ["this server reads - skipped"]),
    Rule("server_questtree_error", WARN, "server", rx(r"\]\[Error\]\[QuestTreeServer\."),
         "QuestTreeServer logger.LogError (any)", []),
]

PASS_MATCHES = re.compile(r"every hosted scene is unloaded and the compared state matches")  # MenuMapHost.cs:1017
CAPTURED = re.compile(r"QuestTree: menu capture: captured (.+?) from game files: .*\(([\d:]+)\)\s*$")  # MenuCaptureRunner.cs:339,373
UPLOADED = re.compile(r"QuestTree: capture of (\S+) uploaded to the host - .*?, ([\d.,]+) MB\.")  # MapTransfer.cs:3492
SUMMARY_ANCHORS = {  # the summary parsers' texts, held to the source like the rules'
    "info: restore verdict": ["every hosted scene is unloaded and the compared state matches"],
    "info: captured": ["captured {name} from game files: ", 'Tag = "QuestTree: menu capture: "'],
    "info: uploaded": ["QuestTree: capture of {key} uploaded to the host - "],
    "info: host tag": ['HostTag = "QuestTree: menu map host: "'],
    "selftest_report": ['Tag = "QuestTree: self-test: "', "{report.Passed} passed, {report.Failed} failed, {report.Warnings} warning(s) - ",
                        "; report {path ?? \"NOT WRITTEN\"}", '"qt-selftest-canary-"'],
}

# The in-game self-test's canary (SelfTest.cs:1372, logged at Warning or Error on purpose, "not a game error"): no
# FAIL/WARN rule may count a line holding it (tools/tests/README_selftest.md).
CANARY = "qt-selftest-canary-"

# SelfTest.cs:1729-1732: "QuestTree: self-test: N passed, M failed, W warning(s) - PASS (reason) in S s; report <path>
# [; failed: ...]." - the warnings part is absent from older builds.
SELFTEST_LINE = re.compile(r"QuestTree: self-test: (\d+) passed, (\d+) failed(?:, (\d+) warning\(s\))? - (\w+)"
                           r"(?: \((.*?)\))? in [\d.]+ s; report (.+?)(?:; failed: (.*))?\.\s*$")


def _walk_checks(part):
    for c in part.get("checks") or []:
        yield part.get("name", "?"), c
    for m in part.get("maps") or []:
        for name, c in _walk_checks(m):
            yield f"{part.get('name', '?')}/{name}", c


def selftest_report(lines, resolve=None):
    """The LAST self-test summary line of a session, judged by its report JSON when that reads, else by the line.
    FAIL: a check failed (pass false, not warn) or the run's overall is fail; WARN: only warnings, or the run was
    cancelled/refused; PASS (info) otherwise. None when the session holds no summary line."""
    m = None
    for l in lines:
        m = SELFTEST_LINE.search(l) or m
    if m is None:
        return None
    passed, failed, warns = int(m.group(1)), int(m.group(2)), int(m.group(3) or 0)
    overall, path, named = m.group(4).lower(), m.group(6).strip(), m.group(7)
    source = "the summary line"
    failing, warning = [], []
    target = resolve(path) if resolve else path
    try:
        with open(target, "r", encoding="utf-8-sig") as f:
            report = json.load(f)
        for step in report.get("steps") or []:
            for where, c in _walk_checks(step):
                if c.get("warn"):
                    warning.append(f"{where}: {c.get('name')}")
                elif not c.get("pass", True):
                    failing.append(f"{where}: {c.get('name')}")
        overall = str(report.get("overall", overall)).lower()
        passed = int(report.get("passed", passed))
        failed, warns = len(failing), len(warning)
        source = f"report {os.path.basename(target)}"
    except (OSError, ValueError, TypeError, AttributeError) as ex:
        source = f"the summary line (report unread: {type(ex).__name__})"
    if failed > 0 or overall == "fail":
        status = FAIL
    elif warns > 0 or overall in ("cancelled", "refused"):
        status = WARN
    else:
        status = PASS
    what = "; ".join(failing[:5] or warning[:5]) or (named or "")
    return Result(LAYER, "selftest_report", status,
                  _clip(f"{passed} passed, {failed} failed, {warns} warning(s), overall {overall} (from {source})"
                        + (f"; {what}" if what else "") + f"; report {path}"))


# --- rule anchors --------------------------------------------------------------------------------------------------

def source_text(repo_root):
    """All C# under Source/, minus obj/ and bin/, as one string; None when there is no Source folder."""
    src = os.path.join(repo_root, "Source")
    if not os.path.isdir(src):
        return None
    parts = []
    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in ("obj", "bin")]
        for f in files:
            if f.endswith(".cs"):
                try:
                    with open(os.path.join(root, f), "r", encoding="utf-8-sig", errors="replace") as h:
                        parts.append(h.read())
                except OSError:
                    pass
    return "\n".join(parts)


def anchor_results(repo_root, rules=RULES, summaries=SUMMARY_ANCHORS):
    text = source_text(repo_root)
    if text is None:
        return [Result(LAYER, "rule anchors", SKIP, f"no Source folder under {repo_root}")]
    out, checked = [], 0
    items = [(r.id, r.anchors) for r in rules] + list(summaries.items())
    for rid, anchors in items:
        missing = [a for a in anchors if a not in text]
        checked += len(anchors)
        if missing:
            out.append(Result(LAYER, f"rule anchor {rid}", FAIL,
                              f"rule {rid} no longer matches any source text: {', '.join(repr(m) for m in missing)}"))
    external = [rid for rid, a in items if not a]
    out.append(Result(LAYER, "rule anchors", FAIL if out else PASS,
                      f"{checked} anchor(s) over {len(items) - len(external)} rule(s); "
                      f"{len(external)} rule(s) over Unity/SPT text have none ({', '.join(external)})"))
    return out


# --- engine --------------------------------------------------------------------------------------------------------

def load_sessions(paths):
    """([(label, scope, lines, is_prev)], [Result SKIP for a missing or unreadable log])."""
    out, skips = [], []
    spec = [("LogOutput.log", "client", paths.get("logoutput"), False),
            ("Player.log", "client", paths.get("player_log"), False),
            ("Player-prev.log", "client", paths.get("player_prev_log"), True),
            ("server " + os.path.basename(paths.get("server_log") or "spt*.log"), "server", paths.get("server_log"), False)]
    for label, scope, path, prev in spec:
        if not path or not os.path.isfile(path):
            skips.append(Result(LAYER, f"source {label}", SKIP, f"not found: {path}"))
            continue
        try:
            lines = read_lines(path)
        except OSError as ex:  # PermissionError included: a log held open exclusively
            skips.append(Result(LAYER, f"source {label}", SKIP, f"unreadable: {type(ex).__name__}: {ex}"))
            continue
        lines = server_session(lines) if scope == "server" else client_session(lines, whole=prev)
        out.append((label, scope, lines, prev))
    return out, skips


def evaluate(sessions, rules=RULES):
    """{rule.id: {"status", "count", "per", "first", "prev_only"}} for rules that hit."""
    found = {}
    for rule in rules:
        per, first, current = {}, None, False
        for label, scope, lines, prev in sessions:
            if scope != rule.scope:
                continue
            hits = rule.match([l for l in lines if CANARY not in l])
            if hits:
                per[label] = len(hits)
                if first is None or (not prev and not current):
                    first = f"{label}: {hits[0].strip()}"
                current = current or not prev
        if per:
            status = rule.status if current or rule.status != FAIL else WARN
            found[rule.id] = {"status": status, "count": sum(per.values()), "per": per, "first": first,
                              "prev_only": not current}
    return found


def _clip(s, n=300):
    return s if len(s) <= n else s[:n] + "..."


def staleness(sessions, prev_path):
    """(Result, stale) for the server session against the client session's start."""
    server = [s for l, sc, s, p in sessions if sc == "server"]
    client = [s for l, sc, s, p in sessions if sc == "client" and not p]
    if not server:
        return None, False
    first, last = server_span(server[0])
    if last is None:
        return Result(LAYER, "server session time", WARN, "no timestamped line in the server session"), False
    span = f"server session {first:%Y-%m-%d %H:%M:%S} .. {last:%Y-%m-%d %H:%M:%S}"
    start, how = client_start(client[0] if client else [], prev_path)
    if start is None:
        return Result(LAYER, "server session time", PASS, f"{span}; client start unknown ({how})"), False
    gap = (start - last).total_seconds() / 3600.0
    if gap > STALE_HOURS:
        return Result(LAYER, "server session time", WARN,
                      f"{span}; it ended {gap:.1f} h before the client session started ({start:%Y-%m-%d %H:%M:%S}, "
                      f"from {how}) - the game ran against another server (remote host?), so the server rules are "
                      f"skipped"), True
    return Result(LAYER, "server session time", PASS,
                  f"{span}; client session started {start:%Y-%m-%d %H:%M:%S} (from {how})"), False


def run(ctx=None):
    paths = default_paths(ctx)
    sessions, results = load_sessions(paths)
    results = list(results)
    for label, scope, lines, prev in sessions:
        results.append(Result(LAYER, f"info: session {label}", PASS,
                              f"{len(lines)} line(s){' (previous launch, whole file)' if prev else ''}"))
    stale_result, stale = staleness(sessions, paths.get("player_prev_log"))
    if stale_result:
        results.append(stale_result)

    found = evaluate(sessions) if sessions else {}
    have = {sc for _, sc, _, _ in sessions}
    for rule in RULES:
        if rule.scope not in have:
            results.append(Result(LAYER, rule.id, SKIP, f"no {rule.scope} log read"))
            continue
        if rule.scope == "server" and stale:
            results.append(Result(LAYER, rule.id, SKIP, "server log is stale (see 'server session time')"))
            continue
        f = found.get(rule.id)
        if f is None:
            results.append(Result(LAYER, rule.id, PASS, f"no match ({rule.source})"))
            continue
        per = ", ".join(f"{k} {v}" for k, v in f["per"].items())
        prev = " - previous launch only" if f["prev_only"] and rule.status == FAIL else ""
        results.append(Result(LAYER, rule.id, f["status"],
                              _clip(f"{f['count']} hit(s) [{per}]{prev}; first: {f['first']}  <{rule.source}>")))

    results += anchor_results(paths["repo_root"])

    for label, sc, s, p in sessions:  # the in-game self-test's verdict, from the first current client log holding one
        if sc == "client" and not p:
            r = selftest_report(s)
            if r is not None:
                results.append(r._replace(detail=f"{r.detail} ({label})"))
                break

    current_client = [(l, s) for l, sc, s, p in sessions if sc == "client" and not p]
    ok = sum(1 for _, s in current_client for l in s if PASS_MATCHES.search(l))
    if ok:
        results.append(Result(LAYER, "info: menu host restore matched", PASS,
                              f"{ok} 'compared state matches' verdict(s) (MenuMapHost.cs:1017)"))
    # Summaries from the first current client source holding any (LogOutput and Player.log carry the same lines).
    for label, s in current_client:
        caps = [(m.group(1), m.group(2)) for l in s if (m := CAPTURED.search(l))]
        ups = [(m.group(1), m.group(2)) for l in s if (m := UPLOADED.search(l))]
        vers = [m.group(1) for l in s if (m := CLIENT_VERSION.search(l))]
        if caps or ups or vers:
            for name, t in caps:
                results.append(Result(LAYER, f"info: captured {name}", PASS, f"from game files in {t} ({label})"))
            for key, mb in ups:
                results.append(Result(LAYER, f"info: uploaded {key}", PASS, f"{mb} MB to the host ({label})"))
            for v in vers[-1:]:  # a bad stamp FAILs as client_version_bad; not counted twice here
                results.append(Result(LAYER, "info: client version line", PASS, f"QuestTree {v} ({label})"))
            break
    if not stale:
        for label, sc, s, p in sessions:
            if sc == "server":
                vers = [m.group(1) for l in s if (m := SERVER_VERSION.search(l))]
                if vers:
                    results.append(Result(LAYER, "info: server version line", PASS, f"Quest Tracker {vers[-1]} ({label})"))
    return results


# --- self-test -----------------------------------------------------------------------------------------------------

def self_test(ctx=None):
    """Each FAIL/WARN rule fires on fixtures/logs/<rule>.log and nothing fires on clean_client.log /
    clean_server.log; plus the session cut, the previous-launch downgrade, the stamp checks, the staleness check, a
    locked log, and the anchor check (real anchors pass, a fake one FAILs)."""
    out = []

    def say(good, msg):
        out.append(Result(LAYER, "self-test", PASS if good else FAIL, msg))

    def fixture(name, scope):
        lines = read_lines(os.path.join(FIXTURES, name + ".log"))
        return server_session(lines) if scope == "server" else client_session(lines)

    for scope in ("client", "server"):
        lines = fixture("clean_" + scope, scope)
        hits = evaluate([("clean", scope, lines, False)])
        say(not hits, f"clean_{scope}.log: " + (f"rules fired: {sorted(hits)}" if hits else f"no rule fires ({len(lines)} lines)"))
        if scope == "client":
            say(any(PASS_MATCHES.search(l) for l in lines) and any(CAPTURED.search(l) for l in lines),
                "clean_client: the pass verdict and capture summary parse")
    for rule in RULES:
        path = os.path.join(FIXTURES, rule.id + ".log")
        if not os.path.isfile(path):
            say(False, f"{rule.id}: no fixture {path}")
            continue
        hits = evaluate([("fixture", rule.scope, fixture(rule.id, rule.scope), False)], [rule])
        st = hits.get(rule.id, {}).get("status")
        say(st == rule.status, f"{rule.id}: expected {rule.status}, got {st}" +
            (f" x{hits[rule.id]['count']}" if st else ""))
    # a host too old for the upload is upload_refused_host_old, never upload_failed
    hits = evaluate([("f", "client", fixture("upload_refused_host_old", "client"), False)])
    say("upload_failed" not in hits, "host-too-old refusal does not FAIL upload_failed")
    # stamps: fatal and bare both bad, a hash good (check_install uses the same function)
    say(bool(stamp_problem("1.19.0+fatal: not a git repository")) and bool(stamp_problem("1.19.0"))
        and stamp_problem("1.19.0+5360921") is None and stamp_problem("1.19.0+66bc437-dirty") is None,
        "stamp_problem: fatal and bare stamps bad, hash and -dirty good")
    for rid in ("client_version_bad", "server_version_bad"):
        f = evaluate([("f", "client" if rid.startswith("client") else "server",
                       fixture(rid, "client" if rid.startswith("client") else "server"), False)])
        say(f.get(rid, {}).get("count") == 2, f"{rid}: both the fatal and the bare stamp fire")
    # previous-launch downgrade
    st = evaluate([("prev", "client", read_lines(os.path.join(FIXTURES, "unity_crash.log")), True)],
                  [r for r in RULES if r.id == "unity_crash"])
    say(st.get("unity_crash", {}).get("status") == WARN, "prev-launch downgrade: a FAIL rule in Player-prev.log reports WARN")
    # session cut
    lines = read_lines(os.path.join(FIXTURES, "session_cut.log"))
    say(not evaluate([("cut", "client", client_session(lines), False)]) and bool(evaluate([("cut", "client", lines, False)])),
        "session cut: a hit before the last BepInEx header is ignored (and is there to ignore)")
    srv = read_lines(os.path.join(FIXTURES, "server_mod_not_loaded.log"))
    say(any("was built for a different version" in l for l in server_session(srv)),
        "server session walk-back keeps the ModLoader line before 'loading: N server mods'")
    # staleness: the clean server log (2026-10-04 01:02) against a client started at 13:34 is stale, at 01:30 not
    srv = ("server", "server", fixture("clean_server", "server"), False)
    late = ("c", "client", ["[Info   :SptCompatFixes] ==================== session start 2026-10-04 13:34:57 ===="], False)
    near = ("c", "client", ["[Info   :SptCompatFixes] ==================== session start 2026-10-04 01:30:00 ===="], False)
    r1, s1 = staleness([srv, late], None)
    r2, s2 = staleness([srv, near], None)
    say(s1 and r1.status == WARN and not s2 and r2.status == PASS, "stale server log: 12.5 h gap WARNs, 0.5 h does not")
    # a log that cannot be read is a SKIP, not a crash
    clean = os.path.join(FIXTURES, "clean_client.log")
    readable, _ = load_sessions({"logoutput": clean})
    orig = globals()["read_lines"]

    def locked(path):
        raise PermissionError(13, "The process cannot access the file because it is being used by another process", path)
    globals()["read_lines"] = locked
    try:
        none_read, skips = load_sessions({"logoutput": clean})
    finally:
        globals()["read_lines"] = orig
    say(len(readable) == 1 and not none_read and any(r.status == SKIP and "PermissionError" in r.detail for r in skips),
        "locked log: PermissionError gives SKIP with the reason")
    # anchors: the real ones are in the source; a fake one FAILs
    repo = str(_get(ctx, "repo_root", REPO))
    real = anchor_results(repo)
    say(all(r.status != FAIL for r in real), "rule anchors: every rule's anchors are in Source/**/*.cs" +
        "".join(f"; {r.detail}" for r in real if r.status == FAIL))
    fake = Rule("fake_rule", FAIL, "client", rx("x"), "none", ["QuestTree: this text is in no source file 0xDEADBEEF"])
    got = anchor_results(repo, [fake], {})
    say(any(r.status == FAIL and "rule fake_rule no longer matches any source text" in r.detail for r in got),
        "rule anchors: a fake anchor FAILs 'rule fake_rule no longer matches any source text'")
    # the in-game self-test's canary: ignored by every rule, while a real QuestTree Error beside it still FAILs
    hits = evaluate([("f", "client", fixture("selftest_canary", "client"), False)])
    say(set(hits) == {"questtree_error_line"} and hits["questtree_error_line"]["count"] == 1,
        f"canary Error line ignored, the real QuestTree Error beside it flagged once (fired: {sorted(hits)})")
    # the self-test report: JSON authoritative when it reads, the summary line otherwise
    resolve = lambda path: os.path.join(FIXTURES, os.path.basename(path.replace("\\", "/")))
    for name, want in (("selftest_pass", PASS), ("selftest_fail", FAIL), ("selftest_warn_only", WARN),
                       ("selftest_fail_line_warn_json", WARN), ("selftest_no_json_fail", FAIL),
                       ("selftest_bad_json_pass", PASS)):
        r = selftest_report(fixture(name, "client"), resolve)
        say(r is not None and r.status == want, f"{name}: expected {want}, got {r.status if r else None}"
            + (f" ({r.detail[:110]})" if r else ""))
    say(selftest_report(fixture("clean_client", "client"), resolve) is None, "no self-test line: no selftest_report result")
    return out


def main(argv):
    if "--self-test" in argv:
        out = self_test()
        for r in out:
            print(f"{'ok  ' if r.status == PASS else 'FAIL'} {r.detail}")
        bad = sum(r.status != PASS for r in out)
        print(f"SELF-TEST {'PASSED' if not bad else 'FAILED'} ({len(out)} check(s), {bad} failed)")
        return 1 if bad else 0
    results = run(None)
    for r in results:
        print(f"{r.status:4}  {r.name}: {r.detail}")
    return 1 if any(r.status == FAIL for r in results) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
