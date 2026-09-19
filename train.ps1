# Launches the SPT server with weapon-build TRAINING on.
#
# Training keeps looking for cheaper weapon builds on half the machine's threads (QUESTTREE_TRAIN_THREADS
# to change that) and stops on its own after 340,000 attempts or 5 hours - the point past which the one
# fully measured run found nothing in a further 448,000 attempts. QUESTTREE_TRAIN_ATTEMPTS and
# QUESTTREE_TRAIN_HOURS move those caps, and 0 removes one: set both to 0 for the old run-until-stopped
# behaviour. Stop the server sooner if you like: every improvement is written to
# cache/weapon-builds.json the moment it is found, so nothing is lost.
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

Write-Host "Training on. The server searches for cheaper builds until 340,000 attempts or 5 hours, or until you stop it." -ForegroundColor Yellow
Write-Host "Progress appears in the server log every 20 seconds." -ForegroundColor Yellow
Write-Host ""

# From the server's own folder: SPT.Server.exe reads ./sptLogger.json relative to the working
# directory and dies with "Unable to find SPTLogger file" when launched from anywhere else - which
# is exactly what running this script from the repo did, the first time anyone ran it that way.
Push-Location $SptPath
try {
    & $exe
} finally {
    Pop-Location
}
