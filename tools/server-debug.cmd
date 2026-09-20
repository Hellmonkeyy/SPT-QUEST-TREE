@echo off
rem The SPT server with Quest Tracker's DIAGNOSTIC log lines turned on, in a visible console.
rem Double-click it.
rem
rem A normal boot prints about five Quest Tracker lines. Everything else the mod measures - the
rem per-profile bill, the pricing coverage, the flea audit, the solver's dry run, one line per weapon
rem build, the per-map zone counts, the timings - is a Detail line: Information with QUESTTREE_DEBUG
rem set, Debug without it, and sptLogger.json ships at Information, so without this they are invisible.
rem This is what to run before quoting the log in a bug report.
rem
rem Runs from the server's own folder, because SPT.Server.exe reads .\sptLogger.json from there,
rem and calls the exe by full path, because cmd resolves a bare name against PATH, not the folder.
set QUESTTREE_DEBUG=1
cd /d C:\Games\SPT\SPT_Runtime
echo Quest Tracker diagnostics ON. Close this window to stop the server.
"C:\Games\SPT\SPT_Runtime\SPT.Server.exe"
