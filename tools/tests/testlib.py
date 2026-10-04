"""Shared pieces for the layers under tools/tests/: the Result row, the run context, and a loader for the
hyphen-named tools (check-capture.py and friends) so a layer reuses their readers instead of copying them.

A layer is a module with run(ctx) -> list of results and, optionally, self_test(ctx) -> list of results. A result
is a Result, or any 4-tuple / dict with the same fields; tools/run-tests.py normalises all three."""

import importlib.util
import os
import sys
from collections import namedtuple
from pathlib import Path

PASS, FAIL, WARN, SKIP = "PASS", "FAIL", "WARN", "SKIP"
STATUSES = (PASS, FAIL, WARN, SKIP)

Result = namedtuple("Result", "layer name status detail")

REPO = Path(__file__).resolve().parent.parent.parent
TOOLS = REPO / "tools"

DEFAULT_INSTALL = Path(r"C:\Games\SPT")
DEFAULT_CAPTURES = Path(r"C:\Games\SPT\BepInEx\plugins\QuestTree\captures")
DEFAULT_HOST_MAPS = Path(r"C:\Games\SPT\SPT_Runtime\user\mods\QuestTree\maps")
DEFAULT_ZONES = Path(r"C:\Games\SPT\SPT_Runtime\user\mods\QuestTree\zones")
DEFAULT_LOG_OUTPUT = Path(r"C:\Games\SPT\BepInEx\LogOutput.log")
DEFAULT_PLAYER_LOG = Path(os.path.expandvars(r"%USERPROFILE%\AppData\LocalLow\Battlestate Games\EscapeFromTarkov\Player.log"))
DEFAULT_SERVER_LOG_DIR = Path(r"C:\Games\SPT\SPT_Runtime\user\logs\spt")


def newest_server_log(folder):
    """The newest *.log under the server's log folder, or None."""
    try:
        logs = [p for p in Path(folder).glob("*.log") if p.is_file()]
    except OSError:
        return None
    return max(logs, key=lambda p: p.stat().st_mtime) if logs else None


class Context:
    """What every layer is handed. Paths are Path objects (None when not found); maps is a list of lower-case map
    keys to restrict to (empty = all). self_test is True under --self-test (unit/run_unit.py reads it instead of having a
    self_test function); relief is False because the runner runs tools/check-relief-simplify.py itself, so the unit layer
    need not run it twice."""

    def __init__(self, captures=None, host_maps=None, zones=None, log_output=None, player_log=None, server_log=None,
                 maps=None, verbose=False, install_root=None, self_test=False, player_prev_log=None):
        self.captures = Path(captures) if captures else DEFAULT_CAPTURES
        self.host_maps = Path(host_maps) if host_maps else DEFAULT_HOST_MAPS
        self.zones = Path(zones) if zones else DEFAULT_ZONES
        self.log_output = Path(log_output) if log_output else DEFAULT_LOG_OUTPUT
        self.player_log = Path(player_log) if player_log else DEFAULT_PLAYER_LOG
        self.player_prev_log = Path(player_prev_log) if player_prev_log else self.player_log.parent / "Player-prev.log"
        self.server_log_dir = DEFAULT_SERVER_LOG_DIR
        self.server_log = Path(server_log) if server_log else newest_server_log(self.server_log_dir)
        self.install_root = Path(install_root) if install_root else DEFAULT_INSTALL
        self.maps = [m.lower() for m in (maps or [])]
        self.verbose = verbose
        self.self_test = self_test
        self.relief = False
        self.repo = REPO
        self.repo_root = REPO
        self.tools = TOOLS

    def wants(self, key):
        """Whether a map key is selected by --map (case-insensitive; a set-aside copy such as Woods.bak-x counts as
        its map)."""
        if not self.maps:
            return True
        base = key.lower().split(".bak-")[0]
        return base in self.maps or key.lower() in self.maps


_TOOL_CACHE = {}


def load_tool(file_name):
    """Imports tools/<file_name> (a hyphenated script) as a module, once. check-capture.py parses sys.argv at import,
    so the import sees a bare argv and the caller's is put back."""
    if file_name in _TOOL_CACHE:
        return _TOOL_CACHE[file_name]
    path = TOOLS / file_name
    saved = sys.argv
    sys.argv = [str(path)]
    try:
        spec = importlib.util.spec_from_file_location(file_name.replace("-", "_").replace(".py", ""), path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    finally:
        sys.argv = saved
    _TOOL_CACHE[file_name] = module
    return module
