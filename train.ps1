# Launches the SPT server with weapon-build TRAINING on.
#
# Training keeps looking for smaller weapon builds for as long as the server runs, using a core to do
# it, and does not stop on its own. Stop the server when you have had enough: every improvement is
# written to cache/weapon-builds.json the moment it is found, so nothing is lost by stopping.
#
# A normal launch does one twenty-second round in the background instead, which is why this is a
# separate script rather than a setting. The flag is an environment variable and not a file on purpose
# - a file survives being copied, and a release zipped from a machine that had been training would
# make every user who installed it train forever without asking.

param(
    [string]$SptPath = "C:\Games\SPT\SPT_Runtime"
)

$exe = Join-Path $SptPath "SPT.Server.exe"

if (-not (Test-Path $exe)) {
    Write-Error "No SPT.Server.exe at $exe. Pass -SptPath if your install is elsewhere."
    exit 1
}

$env:QUESTTREE_TRAIN = "1"

Write-Host "Training on. The server will keep searching for smaller builds until you stop it." -ForegroundColor Yellow
Write-Host "Progress appears in the server log every 10 rounds." -ForegroundColor Yellow
Write-Host ""

& $exe
