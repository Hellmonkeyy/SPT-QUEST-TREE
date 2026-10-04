#!/usr/bin/env python3
"""Checks the SPT install the mod runs from. READ-ONLY: nothing under the install is written.

    python tools/tests/check_install.py              # C:\\Games\\SPT
    python tools/tests/check_install.py --self-test  # fake install trees in a temp dir

Module contract (tools/run-tests.py): run(ctx) -> list[Result], Result = (layer, name, status, detail).
INFO results are PASS with a name starting "info:". ctx is a testlib.Context (a dict also works), keys:
    install_root  C:\\Games\\SPT
    repo_root     this repo (two folders up from this file)
    head          the repo HEAD short hash; read with `git rev-parse --short HEAD` when absent

Checks:
  - FAIL: any QuestTree*.dll other than QuestTreeServer.dll in SPT_Runtime\\user\\mods\\QuestTree\\ (a stray client
    DLL there made the server refuse the whole mod: "Mod `QuestTree` was built for a different version ... not loaded").
  - FAIL: QuestTree.dll missing from BepInEx\\plugins\\QuestTree\\ (and QuestTreeServer.dll from the server folder).
  - each installed DLL's sha256 and AssemblyInformationalVersion ("1.19.0+5360921", UTF-8 or UTF-16 in the file);
    FAIL on a stamp check_logs.stamp_problem rejects ("fatal", "not a git repository", no +commit), WARN when the id is not HEAD or "-dirty"; INFO against the repo's bin\\Release outputs.
  - WARN: empty plugin folders under BepInEx\\plugins, naming any BepInEx\\config\\*.cfg that still mentions them.
  - INFO (as a WARN, context for the mip-limit trap): SD-mode flags on in SPT_Runtime\\user\\sptSettings\\Graphics.ini.
"""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
from testlib import Context, Result, PASS, FAIL, WARN, SKIP  # noqa: E402
from check_logs import stamp_problem  # noqa: E402  one stamp rule for the DLLs and the log lines

LAYER = "install"

REPO = os.path.dirname(os.path.dirname(HERE))

CLIENT_DIR = ("BepInEx", "plugins", "QuestTree")
SERVER_DIR = ("SPT_Runtime", "user", "mods", "QuestTree")
GRAPHICS_INI = ("SPT_Runtime", "user", "sptSettings", "Graphics.ini")
REPO_CLIENT_OUT = ("Source", "Tarkov-QuestTree", "bin", "Release", "netstandard2.1", "QuestTree.dll")
REPO_SERVER_OUT = ("Source", "Tarkov-QuestTree-Server", "bin", "Release", "net10.0", "QuestTreeServer.dll")


def _get(ctx, key, default=None):
    if ctx is None:
        return default
    v = ctx.get(key) if isinstance(ctx, dict) else getattr(ctx, key, None)
    return default if v is None else v


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# "1.19.0+5360921", "1.19.0+66bc437-dirty", "1.19.0+fatal: not a git repository ...", or a bare "1.19.0".
_VER8 = re.compile(rb"(\d+\.\d+\.\d+)(\+[\x20-\x7e]{1,120})?")


def informational_version(path):
    """The AssemblyInformationalVersion: the custom-attribute blob holds it UTF-8 (a length byte, then the text),
    the version resource UTF-16LE. The longest "N.N.N+..." wins; without any "+", the bare version is returned."""
    with open(path, "rb") as f:
        data = f.read()
    found = []
    for m in _VER8.finditer(data):
        found.append(m.group(0).decode("ascii", "replace"))
    try:
        text16 = data.decode("utf-16-le", errors="ignore")
        found += [m.group(0) for m in re.finditer(r"(\d+\.\d+\.\d+)(\+[\x20-\x7e]{1,120})?", text16)]
    except Exception:
        pass
    plus = [v for v in found if "+" in v]
    if plus:
        # a UTF-8 blob may run into the next string's bytes; cut at the first char a stamp never holds after the id
        cleaned = []
        for v in plus:
            ver, rest = v.split("+", 1)
            if rest.startswith("fatal"):
                cleaned.append(v)
            else:
                cleaned.append(ver + "+" + re.match(r"[0-9a-zA-Z.\-]*", rest).group(0))
        return max(cleaned, key=len)
    bare = [v for v in found if re.fullmatch(r"\d+\.\d+\.\d+", v) and not v.startswith("0.")]
    return max(bare, key=lambda s: tuple(int(x) for x in s.split("."))) if bare else None


def commit_of(version):
    """(commit, dirty) from "1.19.0+abc1234[-dirty]"; (None, False) when the version carries no commit id."""
    if not version or "+" not in version:
        return None, False
    rest = version.split("+", 1)[1]
    m = re.fullmatch(r"([0-9a-f]{7,40})(-dirty)?", rest)
    return (m.group(1), bool(m.group(2))) if m else (None, False)


def repo_head(repo):
    try:
        out = subprocess.run(["git", "-C", repo, "rev-parse", "--short", "HEAD"], capture_output=True, text=True, timeout=20)
        return out.stdout.strip() or None if out.returncode == 0 else None
    except Exception:
        return None


def _version_results(label, path, head, repo_out):
    res = []
    ver = informational_version(path)
    res.append(Result(LAYER, f"info: {label} sha256", "PASS", f"{sha256(path)}  {path}"))
    problem = stamp_problem(ver)
    commit, dirty = commit_of(ver)
    if problem or commit is None:
        res.append(Result(LAYER, f"{label} version", FAIL, f"'{ver}': {problem or 'no commit id'}"))
    elif head and not (commit.startswith(head) or head.startswith(commit)):
        res.append(Result(LAYER, f"{label} version", "WARN", f"{ver} - built from {commit}, repo HEAD is {head}"))
    elif dirty:
        res.append(Result(LAYER, f"{label} version", "WARN", f"{ver} - built from uncommitted changes (HEAD {head})"))
    else:
        res.append(Result(LAYER, f"{label} version", "PASS", f"{ver} (HEAD {head or 'unknown'})"))
    if repo_out and os.path.isfile(repo_out):
        same = sha256(repo_out) == sha256(path)
        res.append(Result(LAYER, f"info: {label} vs bin\\Release", "PASS",
                          ("identical" if same else f"differs - bin\\Release is {informational_version(repo_out)}")
                          + f" ({repo_out})"))
    return res


def _sd_flags(path):
    """Keys of Graphics.ini (JSON) that turn SD mode on: "SDMode" non-null/true and any "Sd<Map>": true."""
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        data = json.load(f)
    on = []
    for k, v in data.items():
        if k == "SDMode" and v not in (None, False, 0, "Off", "off", ""):
            on.append(f"{k}={v}")
        elif re.fullmatch(r"Sd[A-Z]\w*", k) and v is True:
            on.append(k)
    return on


def _tokens(name):
    parts = re.split(r"[-_. ]+|(?<=[a-z])(?=[A-Z])", name)
    return [p.lower() for p in parts if len(p) >= 4]


def run(ctx=None):
    if ctx is None:
        ctx = Context()
    root = str(_get(ctx, "install_root", r"C:\Games\SPT"))
    repo = str(_get(ctx, "repo_root", REPO))
    head = _get(ctx, "head") or repo_head(repo)
    res = []
    if not os.path.isdir(root):
        return [Result(LAYER, "install root", "SKIP", f"not found: {root}")]

    client_dir = os.path.join(root, *CLIENT_DIR)
    server_dir = os.path.join(root, *SERVER_DIR)

    # stray client DLLs in the server mod folder
    if os.path.isdir(server_dir):
        stray = sorted(f for f in os.listdir(server_dir)
                       if f.lower().startswith("questtree") and f.lower().endswith(".dll") and f.lower() != "questtreeserver.dll")
        res.append(Result(LAYER, "server folder holds only QuestTreeServer.dll", "FAIL" if stray else "PASS",
                          (f"stray {', '.join(stray)} in {server_dir} - SPT refuses the whole mod" if stray else server_dir)))
    else:
        res.append(Result(LAYER, "server folder holds only QuestTreeServer.dll", "FAIL", f"no folder {server_dir}"))

    repo_c = os.path.join(repo, *REPO_CLIENT_OUT) if repo else None
    repo_s = os.path.join(repo, *REPO_SERVER_OUT) if repo else None
    for label, folder, dll, out in (("client QuestTree.dll", client_dir, "QuestTree.dll", repo_c),
                                     ("server QuestTreeServer.dll", server_dir, "QuestTreeServer.dll", repo_s)):
        path = os.path.join(folder, dll)
        if not os.path.isfile(path):
            res.append(Result(LAYER, f"{label} present", "FAIL", f"missing: {path}"))
            continue
        res.append(Result(LAYER, f"{label} present", "PASS", path))
        res += _version_results(label, path, head, out)

    # empty plugin folders, and the configs that still name them
    plugins = os.path.join(root, "BepInEx", "plugins")
    config = os.path.join(root, "BepInEx", "config")
    cfgs = {}
    if os.path.isdir(config):
        for f in os.listdir(config):
            if f.lower().endswith(".cfg"):
                try:
                    with open(os.path.join(config, f), "r", encoding="utf-8", errors="replace") as h:
                        cfgs[f] = h.read().lower()
                except OSError:
                    pass
    empties = []
    if os.path.isdir(plugins):
        for d in sorted(os.listdir(plugins)):
            p = os.path.join(plugins, d)
            if os.path.isdir(p) and not os.listdir(p):
                toks = _tokens(d)
                named = [f for f, text in cfgs.items() if toks and all(t in text for t in toks)]
                empties.append(f"{d}" + (f" (named by {', '.join(sorted(named))})" if named else ""))
    res.append(Result(LAYER, "no empty plugin folders", "WARN" if empties else "PASS",
                      ("leftover mod(s): " + "; ".join(empties)) if empties else plugins))

    # SD mode, context for the mip-limit trap
    ini = os.path.join(root, *GRAPHICS_INI)
    if os.path.isfile(ini):
        try:
            on = _sd_flags(ini)
            res.append(Result(LAYER, "info: Graphics.ini SD mode", "WARN" if on else "PASS",
                              (f"on: {', '.join(on)} - SD mode raises the texture mip limit (the menu-capture mip trap)"
                               if on else "SD flags off")))
        except Exception as ex:
            res.append(Result(LAYER, "info: Graphics.ini SD mode", "SKIP", f"unreadable ({ex})"))
    else:
        res.append(Result(LAYER, "info: Graphics.ini SD mode", "SKIP", f"not found: {ini}"))
    return res


# --- self-test -----------------------------------------------------------------------------------------------------

def _fake_dll(path, stamp, utf16=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    body = stamp.encode("utf-16-le") if utf16 else bytes([len(stamp)]) + stamp.encode("ascii")
    with open(path, "wb") as f:
        f.write(b"MZ\x90\x00" + b"\x00" * 64 + b"\x01\x00" + body + b"\x00\x00" + b"netstandard\x00" + b"\x00" * 32)


def _tree(base, client="1.19.0+5360921", server="1.19.0+5360921", stray=False, empty=False, sd=False, utf16=False):
    if client:
        _fake_dll(os.path.join(base, *CLIENT_DIR, "QuestTree.dll"), client, utf16)
    else:
        os.makedirs(os.path.join(base, *CLIENT_DIR), exist_ok=True)
    _fake_dll(os.path.join(base, *SERVER_DIR, "QuestTreeServer.dll"), server)
    if stray:
        _fake_dll(os.path.join(base, *SERVER_DIR, "QuestTree.dll"), client or "1.19.0+5360921")
    os.makedirs(os.path.join(base, "BepInEx", "config"), exist_ok=True)
    if empty:
        os.makedirs(os.path.join(base, "BepInEx", "plugins", "ManimalInterchange"), exist_ok=True)
        with open(os.path.join(base, "BepInEx", "config", "com.example.mapvariants.cfg"), "w") as f:
            f.write("[Interchange]\nDefaultSelection = Manimal\n")
    ini = os.path.join(base, *GRAPHICS_INI)
    os.makedirs(os.path.dirname(ini), exist_ok=True)
    with open(ini, "w") as f:
        json.dump({"SDMode": None, "SdTarkovStreets": sd, "TextureQuality": 2}, f)


def self_test(ctx=None):
    out = []

    def say(good, msg):
        out.append(Result(LAYER, "self-test", "PASS" if good else "FAIL", msg))
    cases = [
        # name, tree kwargs, {result name: expected status}
        ("clean", {}, {"server folder holds only QuestTreeServer.dll": "PASS", "client QuestTree.dll present": "PASS",
                       "client QuestTree.dll version": "PASS", "server QuestTreeServer.dll version": "PASS",
                       "no empty plugin folders": "PASS", "info: Graphics.ini SD mode": "PASS"}),
        ("clean UTF-16 stamp", {"utf16": True}, {"client QuestTree.dll version": "PASS"}),
        ("stray client DLL", {"stray": True}, {"server folder holds only QuestTreeServer.dll": "FAIL"}),
        ("missing client DLL", {"client": None}, {"client QuestTree.dll present": "FAIL"}),
        ("bad version (no commit)", {"client": "1.19.0"}, {"client QuestTree.dll version": "FAIL"}),
        ("bad version (fatal)", {"client": "1.19.0+fatal: not a git repository"}, {"client QuestTree.dll version": "FAIL"}),
        ("other commit", {"server": "1.19.0+acbdd88"}, {"server QuestTreeServer.dll version": "WARN"}),
        ("dirty build", {"client": "1.19.0+5360921-dirty"}, {"client QuestTree.dll version": "WARN"}),
        ("empty plugin folder", {"empty": True}, {"no empty plugin folders": "WARN"}),
        ("SD mode on", {"sd": True}, {"info: Graphics.ini SD mode": "WARN"}),
    ]
    for name, kw, want in cases:
        base = tempfile.mkdtemp(prefix="qt-install-")
        try:
            _tree(base, **kw)
            got = {r.name: r for r in run({"install_root": base, "repo_root": base, "head": "5360921"})}
            for key, status in want.items():
                r = got.get(key)
                if r is None or r.status != status:
                    say(False, f"{name}: {key} expected {status}, got {r.status if r else None} ({r.detail if r else ''})")
                else:
                    say(True, f"{name}: {key} -> {status}" + (f" ({r.detail[:80]})" if status != "PASS" else ""))
        finally:
            shutil.rmtree(base, ignore_errors=True)
    return out


def main(argv):
    if "--self-test" in argv:
        out = self_test()
        for r in out:
            print(f"{'ok  ' if r.status == 'PASS' else 'FAIL'} {r.detail}")
        bad = sum(r.status != "PASS" for r in out)
        print(f"SELF-TEST {'PASSED' if not bad else 'FAILED'} ({len(out)} check(s), {bad} failed)")
        return 1 if bad else 0
    results = run(None)
    for r in results:
        print(f"{r.status:4}  {r.name}: {r.detail}")
    return 1 if any(r.status == "FAIL" for r in results) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
