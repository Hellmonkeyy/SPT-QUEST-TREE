# Builds both halves in Release and assembles the release zip from an ALLOWLIST.
#
# Every release before 1.13.2 was put together by hand, and the zones folder was copied out of the
# live install - one directory below objective-gps.json and tarkovdev-quests.json, two third-party
# files with no licence to redistribute. Seventeen zips were clean because one person
# remembered. This script does not read the install at all except under the three -Refresh switches,
# and each of those reads ONE named thing and nothing else: -RefreshZones copies zones\*.json,
# -RefreshMaps copies maps\<key>\*.jpg|*.map.json|*-mesh.bin, and -RefreshBuilds copies the single file
# cache\weapon-builds.json. None of the three globs can recurse or reach the parent directory, which
# is where objective-gps.json and tarkovdev-quests.json live. -RefreshMaps and -RefreshBuilds both
# REFUSE while SPT.Server.exe is up, because the server writes both of those things as it runs - a map
# set is promoted file by file and the build cache is rewritten as it trains - so a copy taken then can
# be half a set or half a file.
#
# It assembles exactly the files named below, and then checks the
# staging folder and the zip against that list: an extra file, a missing file, or any name matching
# objective-gps / tarkovdev / .bak is a non-zero exit. It also refuses when the four version strings
# (two ModInfo.cs, two csproj) disagree, or when the built DLLs do not carry that version.
#
# It also runs tools/check-dtos.py, which compares the server's wire DTOs against the client's
# hand-mirrored copies of them. The two halves cannot share a source file, the mirrors are copied by
# hand, and a dropped field reads as a default rather than as an error - the 1.12.2 ids fault and
# several silently-missing fields all came from that gap. Its own header says what it does not catch.
#
# And it checks the PAPERWORK once the archive is real: the changelog's first line must name the
# version being packaged, and the release notes must be more than a stub that mentions it. 1.15.0 was
# built, versioned and zipped and then never published, and nothing here noticed, because every other
# gate is about the code or the archive. It then records the built archive's sha256 as the last line
# of that version's release notes, because re-running this script re-zips and a fresh zip is not
# byte-identical to the one already published as the release asset.
#
# From 1.19.0 it also gates the MAP SETS - the captured JPEG floors and their metas under
# Source\Tarkov-QuestTree-Server\maps\ - because they are the first thing this mod ships that is
# measured in megabytes: a zip with no map sets is ~190 KB, one carrying all 11 vanilla maps is 20-60
# MB (JPEG and the deflated meshes do not compress further, so the archive is roughly the sum of the
# files), and the release ships WHATEVER SETS EXIST - anything from none to all 11. That payload is
# machine-written and copied twice, and every way it goes wrong looks like a working release from here:
# see the six gates under "the map pictures" below. How many maps are covered is a WARNING there, and
# from 1.19.0 so is the payload's total size, not a gate, because
# DynamicMaps stays a selectable picture source and a map with no set falls back instead of breaking.
#
# Proven able to fail before it shipped: a planted objective-gps.json in the staging folder, a
# mismatched version constant, and (for the DTO check) a bogus property added to the client's
# RewardDto, each made it exit non-zero. The two paperwork gates were proven the same way: a
# CHANGELOG.md whose first line named 1.18.0 while the constants said 1.18.1, and a release-notes
# file cut to 120 bytes, each made it exit non-zero and name what it wanted. The map gates were
# proven against a planted fake set of 11 keys, one fault at a time: a raw .png left in a key folder
# and a .incoming\ folder inside one (layout gate), a 2 MB floor image (per-image gate), a meta stamped
# schemaVersion 2 (schema gate), a floor's image deleted, an orphan image no floor names, and a meta
# width moved 3 px off the extent's arithmetic (all the pack check), each made it exit non-zero and name
# the map and the field; the intact set passed. The MESH gate was proven the same way in 14 cases - a
# truncated .bin, a wrong sha256 or byte length, a mesh 10 m wider than the meta's extent, bands at
# levels the floors do not have, wrong cell/triangle/version numbers, trailing bytes, an orphan
# *-mesh.bin, a named mesh that is absent, and bytes that are not a deflate stream - while an intact set
# WITH a mesh and a set with NO mesh both passed; and the total-size gate is deliberately no longer able
# to fail (see GATE 3).
# The two things that are NOT gates were proven to warn and carry on instead: a four-key set named the
# seven missing maps and still zipped, and with maps\ absent the run zipped 17 files with no map
# entries on the allowlist.
#
# Usage:  .\package.ps1                 build, stage, zip, verify
#         .\package.ps1 -RefreshZones   first copy zones\*.json from the install into the repo
#         .\package.ps1 -RefreshMaps    first copy maps\<key>\*.jpg, *.map.json and *-mesh.bin from
#                                       the install (refuses while SPT.Server.exe is up)
#         .\package.ps1 -RefreshBuilds  first copy the trained cache\weapon-builds.json over the seed
#                                       (refuses while SPT.Server.exe is up, and prints the seed's
#                                       stamp, build count and trader/flea/unpriced split either side
#                                       of the copy so a worse training run is visible)
#         .\package.ps1 -SkipBuild      stage and verify what bin\ already holds

param(
    [string]$SptPath = "C:\Games\SPT",
    [switch]$RefreshZones,
    [switch]$RefreshMaps,
    [switch]$RefreshBuilds,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$client = Join-Path $repo "Source\Tarkov-QuestTree"
$server = Join-Path $repo "Source\Tarkov-QuestTree-Server"
$sptVersion = "4.1.6"

function Fail($message) {
    Write-Host ""
    Write-Host "PACKAGE FAILED: $message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- the version, from all four copies
function ReadVersion($path, $pattern) {
    $text = Get-Content -Raw $path
    $m = [regex]::Match($text, $pattern)
    if (-not $m.Success) { Fail "no version found in $path" }
    return $m.Groups[1].Value
}

$versions = @{
    "client ModInfo.cs"      = ReadVersion (Join-Path $client "ModInfo.cs") 'Version = "([0-9]+\.[0-9]+\.[0-9]+)"'
    "server ModInfo.cs"      = ReadVersion (Join-Path $server "ModInfo.cs") 'Version = "([0-9]+\.[0-9]+\.[0-9]+)"'
    "QuestTree.csproj"       = ReadVersion (Join-Path $client "QuestTree.csproj") '<AssemblyVersion>([0-9]+\.[0-9]+\.[0-9]+)</AssemblyVersion>'
    "QuestTreeServer.csproj" = ReadVersion (Join-Path $server "QuestTreeServer.csproj") '<AssemblyVersion>([0-9]+\.[0-9]+\.[0-9]+)</AssemblyVersion>'
}

$distinct = $versions.Values | Sort-Object -Unique
if (@($distinct).Count -ne 1) {
    $versions.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-24} {1}" -f $_.Key, $_.Value) }
    Fail "the four version strings disagree"
}
$version = $distinct
Write-Host "Version $version in all four places." -ForegroundColor Green

# ---------------------------------------------------------------- the two halves' DTOs must agree
# Before the build, not after: a mirror that has drifted is not something a successful compile says
# anything about, and there is no point spending two builds to find out. Before the release-notes
# check too, so that the one failure here that is about the CODE is reported ahead of the one that is
# about the paperwork - and so this gate is reachable on a version whose notes are not written yet.
$dtoCheck = Join-Path $repo "tools/check-dtos.py"
if (-not (Test-Path $dtoCheck)) { Fail "missing $dtoCheck - the DTO drift check is not optional" }
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Fail "python is not on PATH, so check-dtos.py cannot run - install Python 3 rather than packaging unchecked"
}

& python $dtoCheck
if ($LASTEXITCODE -ne 0) {
    Fail "the server DTOs and the client mirrors disagree (see above) - fix the mirror, in the same commit as the change that moved it"
}
Write-Host "Server DTOs and client mirrors agree." -ForegroundColor Green

# ---------------------------------------------------------------- the build seed, optionally refreshed
# The solver is TRAINED between releases: a server started by tools\train.ps1 or
# tools\train-all-threads.cmd keeps looking for cheaper builds for hours and writes every improvement
# straight into the install's cache\weapon-builds.json. That file is the seed the release ships, and
# until now it reached the repo by hand - the same hand-copy the zones folder used to need, with the
# same failure mode: a release that quietly ships the PREVIOUS training run, since nothing else here
# reads the install. The gate below catches a seed from a different SOLVER; it cannot tell one training
# run from a newer one.
function SeedFacts($path) {
    # count-seed-sources.py prints the stamp, the build count, the instance count and the split of
    # every part instance into trader-priced / flea-only / unpriced, all in one line.
    #
    # The database is -SptPath's own (review F54: the script read a hard-coded C:\Games\SPT, so on any
    # other install every call failed), and the call runs with $ErrorActionPreference = 'Continue' so
    # Windows PowerShell 5.1 does not turn the script's redirected stderr into a terminating error: a
    # figure that cannot be counted is a line in the summary, never the end of the run.
    $database = Join-Path $SptPath "SPT_Runtime\SPT_Data\database"
    $ErrorActionPreference = 'Continue'
    $out = & python (Join-Path $repo "tools/count-seed-sources.py") $path $database 2>&1
    if ($LASTEXITCODE -ne 0) { return "count-seed-sources.py exited $LASTEXITCODE - $($out -join ' ')" }
    return ($out -join " ")
}

if ($RefreshBuilds) {
    $seedPath = Join-Path $server "weapon-builds.json"
    $installSeed = Join-Path $SptPath "SPT_Runtime\user\mods\QuestTree\cache\weapon-builds.json"
    if (-not (Test-Path $installSeed)) { Fail "no build cache at $installSeed" }

    # NOT while the server is up. A training server rewrites this file the moment it finds a cheaper
    # build, so a copy taken mid-run can be a half-written file - and the user may be in a raid on that
    # server, which is never something this script interrupts. It refuses and says so; stopping the
    # server is the user's call, not this script's.
    $serverRunning = @(Get-Process -Name "SPT.Server" -ErrorAction SilentlyContinue)
    if ($serverRunning.Count -gt 0) {
        Fail "SPT.Server.exe is running (pid $(($serverRunning | ForEach-Object { $_.Id }) -join ', ')) - it rewrites cache\weapon-builds.json as it trains, so a copy taken now can be half-written. Stop the server yourself and re-run; nothing has been copied."
    }

    # Before AND after, for both files, because the interesting failure is not a copy that breaks - it
    # is a copy that works and ships a WORSE seed. Training optimises price, and it can do that by
    # choosing parts no trader sells for cash; that shows up here as flea-only rising, which is exactly
    # the number 1.18.0's objective change was measured against. A drop in builds is the other one: a
    # cache from a server that had not finished its first boot carries fewer than the 60 models.
    Write-Host "  repo seed before:  $(SeedFacts $seedPath)"
    Write-Host "  install seed:      $(SeedFacts $installSeed)"
    Copy-Item $installSeed $seedPath -Force
    Write-Host "  repo seed after:   $(SeedFacts $seedPath)" -ForegroundColor Yellow
    Write-Host "Refreshed weapon-builds.json from the install. Compare the three lines above before committing it." -ForegroundColor Yellow
}

# ---------------------------------------------------------------- the shipped build history must be from the current solver
# The cache's SolverVersion is what decides whether a boot trusts the shipped builds or re-opens
# every one of them ("carried over from a different solver"). 1.13.2 shipped a seed still stamped 9
# after the bump to 10, and every install paid the carry-over on every boot until it was noticed in
# a log. The seed is a plain JSON file and the constants are plain source: compare them here.
$cacheSource = Get-Content -Raw (Join-Path $server "WeaponBuildCache.cs")
$seedText = Get-Content -Raw (Join-Path $server "weapon-builds.json")
$solverConst = [regex]::Match($cacheSource, 'private const int CurrentSolver = ([0-9]+);').Groups[1].Value
$schemaConst = [regex]::Match($cacheSource, 'private const int CurrentSchema = ([0-9]+);').Groups[1].Value
$solverSeed = [regex]::Match($seedText, '"SolverVersion":\s*([0-9]+)').Groups[1].Value
$schemaSeed = [regex]::Match($seedText, '"SchemaVersion":\s*([0-9]+)').Groups[1].Value
if ($solverConst -eq "" -or $solverSeed -eq "") { Fail "could not read CurrentSolver from WeaponBuildCache.cs or SolverVersion from weapon-builds.json" }
if ($solverConst -ne $solverSeed -or $schemaConst -ne $schemaSeed) {
    Fail "the shipped weapon-builds.json is stamped solver $solverSeed / schema $schemaSeed but the code is $solverConst / $schemaConst - every install would re-open all its builds on every boot. Boot a server on the new code once and copy its cache over the seed."
}
Write-Host "Shipped build history is stamped solver $solverSeed / schema $schemaSeed, matching the code." -ForegroundColor Green

$notes = Join-Path $repo "Releases\RELEASE-NOTES-$version.md"
if (-not (Test-Path $notes)) { Fail "write Releases\RELEASE-NOTES-$version.md first - a release without notes is not a release" }

# ---------------------------------------------------------------- zones, optionally refreshed from the install
$zonesDir = Join-Path $server "zones"
if ($RefreshZones) {
    $installZones = Join-Path $SptPath "SPT_Runtime\user\mods\QuestTree\zones"
    if (-not (Test-Path $installZones)) { Fail "no zones folder at $installZones" }
    New-Item -ItemType Directory -Force $zonesDir | Out-Null
    # Only *.json and only from the zones folder: the glob cannot reach the parent, which is
    # where the files that must never ship live.
    Copy-Item (Join-Path $installZones "*.json") $zonesDir -Force
    Write-Host "Refreshed zones\ from the install: $((Get-ChildItem $zonesDir -Filter *.json).Count) file(s)." -ForegroundColor Yellow
}

$zoneFiles = @(Get-ChildItem $zonesDir -Filter *.json -ErrorAction SilentlyContinue)
if ($zoneFiles.Count -eq 0) { Fail "no zone files in $zonesDir - run with -RefreshZones once" }

# ---------------------------------------------------------------- the map pictures, optionally refreshed
# The captured map sets are the payload that changed this script's scale: a zip that was ~190 KB of
# DLLs and JSON becomes up to 15-30 MB of JPEG, ~26 floors across 11 maps. Nothing about that payload
# can be eyeballed - it is written by the client in-raid, converted, uploaded, stored by the server and
# only then copied in here - and every way it goes wrong looks exactly like a working release from the
# packager's chair: a raw .png the converter never reached (ships, draws nothing), a floor left at q95
# (ships, doubles the download), a meta whose width no longer matches its picture (ships, and every pin
# on that map sits a constant fraction off), and a mesh is worse - it is binary nobody can even open to
# look at, so a stale one beside a fresh meta is invisible without a hash. So: six gates, and none of
# them is about the code. What
# is NOT gated is how many maps are covered - see the warning further down.
$mapsDir = Join-Path $server "maps"

if ($RefreshMaps) {
    $installMaps = Join-Path $SptPath "SPT_Runtime\user\mods\QuestTree\maps"
    if (-not (Test-Path $installMaps)) { Fail "no maps folder at $installMaps" }

    # NOT while the server is up, the same refusal -RefreshBuilds makes and for a sharper reason. A
    # running host writes into this very folder: it promotes a staged set by writing each picture, then
    # the mesh, then the meta, and it sweeps the files the new meta does not name. A copy taken in the
    # middle of that carries an old meta beside new pictures, or a meta naming a mesh whose bytes are
    # still arriving - and the sha256 in the meta is what would then fail the pack check, if the copy
    # happened to be caught at all. The user may also be in a raid on that server, which this script
    # never interrupts. It refuses; stopping the server is the user's call.
    $serverRunning = @(Get-Process -Name "SPT.Server" -ErrorAction SilentlyContinue)
    if ($serverRunning.Count -gt 0) {
        Fail "SPT.Server.exe is running (pid $(($serverRunning | ForEach-Object { $_.Id }) -join ', ')) - it writes map sets into $installMaps as clients upload them, so a copy taken now can hold one capture's meta beside another's pictures or mesh. Stop the server yourself and re-run; nothing has been copied."
    }
    New-Item -ItemType Directory -Force $mapsDir | Out-Null
    # One key at a time, and only the three globs below. The install's folder also holds what the transport
    # leaves there - a ".incoming" staging directory mid-upload, and on a client machine the raw .png
    # captures the JPEGs were converted from - and neither is something a release carries. A directory
    # whose name is not a plain map key is skipped for that reason: that is what ".incoming" looks
    # like from here. The globs cannot recurse and cannot reach the parent, which is where
    # objective-gps.json lives.
    $refreshed = 0
    foreach ($src in @(Get-ChildItem $installMaps -Directory -ErrorAction SilentlyContinue)) {
        if ($src.Name -notmatch "^[A-Za-z0-9_\-]{1,64}$") {
            Write-Host "  skipped $($src.Name)\ - not a map key" -ForegroundColor DarkGray
            continue
        }
        # `*.jpg` takes the side pictures (<key>-side-<dir>.jpg) and the atlas pages (<key>-atlas-<n>.jpg)
        # with the floors - they are the same kind of file, and GATE 1 below holds all three to their exact
        # names.
        # Three globs, not two: `*-mesh.bin` is the 3D mesh the same capture wrote beside its pictures
        # (MapMeshFile), and a set refreshed without it would ship a meta naming a mesh the zip does not
        # carry - which the mesh gate below then fails, loudly, rather than shipping.
        $picked = @(Get-ChildItem $src.FullName -File |
            Where-Object { $_.Name -like "*.jpg" -or $_.Name -like "*.map.json" -or $_.Name -like "*-mesh.bin" })
        if ($picked.Count -eq 0) {
            Write-Host "  skipped $($src.Name)\ - no .jpg, .map.json or -mesh.bin in it" -ForegroundColor DarkGray
            continue
        }
        $dst = Join-Path $mapsDir $src.Name
        New-Item -ItemType Directory -Force $dst | Out-Null
        # REPLACED per key, not merged: a re-capture with fewer floors would otherwise leave the old
        # floor's picture in the repo, and it would ship - which is what the pack check's orphan rule
        # catches when it happens anyway. The mesh is deleted with them for the same reason and a
        # sharper one: a stale .bin beside a fresh meta is a mesh of a DIFFERENT extent, and the gate
        # that catches it is a sha256, not an eye. Only these globs are deleted, so anything else in
        # there survives to be named by the layout gate below rather than silently thrown away.
        Get-ChildItem $dst -File |
            Where-Object { $_.Name -like "*.jpg" -or $_.Name -like "*.map.json" -or $_.Name -like "*-mesh.bin" } |
            Remove-Item -Force
        foreach ($file in $picked) { Copy-Item $file.FullName (Join-Path $dst $file.Name) -Force }
        $refreshed++
        Write-Host ("  {0,-16} {1} file(s)" -f $src.Name, $picked.Count) -ForegroundColor Yellow
    }
    Write-Host "Refreshed maps\ from the install: $refreshed key(s)." -ForegroundColor Yellow
}

# The 11 vanilla keys, spelled as the zone files are (Source\Tarkov-QuestTree-Server\zones\<key>.json)
# because both names come from the same client location id. This list is what the COVERAGE WARNING
# counts against, nothing more. factory4_night and Sandbox_high are not in it and must not be:
# ZoneStore.Aliases folds them onto factory4_day and Sandbox, so a set stored under either name would
# never be looked up, and naming them here would report two maps as permanently missing. Hard-coded
# rather than read from zones\, so harvesting a modded map on the user's host cannot quietly add a
# map to the tally.
$vanillaMaps = @("bigmap", "factory4_day", "Interchange", "laboratory", "Labyrinth", "Lighthouse",
                 "RezervBase", "Sandbox", "Shoreline", "TarkovStreets", "Woods")

# COVERAGE IS A WARNING, NOT A GATE, and so is having no map sets at all: DynamicMaps stays a
# selectable picture source, so a map with no captured set is a map that falls back rather than a map
# that is broken, and the release ships whatever sets exist. What is still a failure is a set that is
# PRESENT AND WRONG - everything below - because that one does not fall back: the client loads it and
# draws it in the wrong place. The warning names the missing keys so that the capture campaign's state
# is visible in the packaging log rather than only in whoever's memory.
$mapsPresent = Test-Path $mapsDir
$mapFiles = @()
if ($mapsPresent) {
    $mapFiles = @(Get-ChildItem $mapsDir -Recurse -File)
} else {
    Write-Host "No maps folder at $mapsDir - the zip ships no map pictures; DynamicMaps or in-raid captures supply them." -ForegroundColor Yellow
}
# With no map sets the three gates below have nothing to iterate and pass on an empty list, which is
# the point: they are about sets that exist, not about whether any do.

# GATE 1 - LAYOUT. maps\ holds nothing but the floors <key>\<key>-<level>.jpg, the oblique side
# pictures <key>\<key>-side-<N|S|E|W>.jpg, the atlas pages <key>\<key>-atlas-<0..7>.jpg, <key>\*.map.json
# and the one file <key>\<key>-mesh.bin. The allowlist below is BUILT from this folder, so anything else in here is a
# file the zip carries: a raw .png capture, a ".incoming" directory a refresh skipped but a hand-copy
# did not, an editor's .bak, a stray .svg from the DynamicMaps era. The depth test is what catches the
# directories - a file two levels down is not in a key folder, it is in something nested inside one -
# and it is also the whole of what confines a .bin to maps\<key>\: nothing else in the release ships
# one, and the forbidden regex further down is deliberately unchanged because a .bin outside this
# folder is caught by the allowlist rather than by a name.
#
# The mesh is matched by its EXACT name and not by *-mesh.bin, which is the difference between a gate
# that agrees with the code and one that merely looks like it: the server stores the file as
# <key>-mesh.bin and reads it back through a regex that admits nothing else (MapStore's
# StoredMeshFileName), so "bigmap\weird-mesh.bin" would pass a glob here, ship, and then be refused by
# the very host it was shipped to - and a bare "-mesh.bin" fails that regex too. A mesh from another
# map's folder is the case that matters: it parses, its extent is somebody else's, and nothing but the
# name says so.
#
# The JPEGs are matched by the three names the host writes and nothing looser, for the mesh's reason: a
# floor is <key>-<level>.jpg (the level can be negative, hence "Interchange--1.jpg"), a side is
# <key>-side-<dir>.jpg for one of the four directions, and an atlas page is <key>-atlas-<n>.jpg for page
# 0 to 7 (MapStore's StoredAtlasFileName). Any other .jpg is a file the host would never have written
# and the client never reads by that name - a side of a direction that does not exist, a ninth page, the
# capture's own .png page, or a picture from another map's folder - and it would ship as megabytes
# nothing loads.
$strayMapFiles = @($mapFiles | ForEach-Object {
    $rel = $_.FullName.Substring($mapsDir.Length + 1)
    $parts = @($rel -split "\\")
    $named = $false
    if ($parts.Count -eq 2) {
        $keyPattern = [regex]::Escape($parts[0])
        $named = $_.Name -match "^$keyPattern-(-?[0-9]+|side-[NSEW]|atlas-[0-7])\.jpg$" -or
                 $_.Name -like "*.map.json" -or $_.Name -eq "$($parts[0])-mesh.bin"
    }
    if (-not $named) { $rel }
})
if ($strayMapFiles.Count -gt 0) {
    Fail "maps\ holds $($strayMapFiles.Count) file(s) that are not <key>\<key>-<level>.jpg, <key>\<key>-side-<N|S|E|W>.jpg, <key>\<key>-atlas-<0..7>.jpg, <key>\*.map.json or <key>\<key>-mesh.bin, and the allowlist is built from this folder: $($strayMapFiles -join ', ')"
}

# GATE 2 - PER IMAGE, floors and side pictures alike (both are *.jpg). A floor over 1.5 MB is a capture that came out at a resolution or a quality the
# release cannot afford: the budget is the whole set, and one 4 MB floor is three normal ones. The cap
# is a ceiling on the capture settings, not a guess about content.
#
# ATLAS PAGES have their OWN gate, 6 MB, and are left out of the 1.5 MB one: a page is a 4096 px sheet of
# building textures stored at q85 and never downscaled (MapTransfer.MaxAtlasPixels), which is 2-4 MB by
# design - the 1.5 MB picture gate would fail every real page. Six is the host's and the client's own cap
# on one page (MapStore.MaxAtlasPageBytes, MapTransfer.MaxAtlasPageBytes), so a page past it here is one no
# host would have stored and no client would download. Matched by GATE 1's exact name, so only a file
# that gate admitted as a page is judged as one.
$maxImageBytes = 1.5MB
$maxAtlasPageBytes = 6MB
$isAtlasPage = { param($file) $file.Name -match "-atlas-[0-7]\.jpg$" }
$fatImages = @($mapFiles | Where-Object { $_.Name -like "*.jpg" -and -not (& $isAtlasPage $_) -and $_.Length -gt $maxImageBytes } |
    ForEach-Object { "{0} ({1:N1} MB)" -f $_.FullName.Substring($mapsDir.Length + 1), ($_.Length / 1MB) })
if ($fatImages.Count -gt 0) {
    Fail "map image(s) over $($maxImageBytes / 1MB) MB. Every upload is already 2048 px at JPEG q80, so no capture setting shrinks it - look at the picture (a busy floor photographs large), and either leave that map out of this release or raise this packaging gate knowingly (the host takes up to 2.5 MB): $($fatImages -join ', ')"
}
$fatPages = @($mapFiles | Where-Object { (& $isAtlasPage $_) -and $_.Length -gt $maxAtlasPageBytes } |
    ForEach-Object { "{0} ({1:N1} MB)" -f $_.FullName.Substring($mapsDir.Length + 1), ($_.Length / 1MB) })
if ($fatPages.Count -gt 0) {
    Fail "atlas page(s) over $($maxAtlasPageBytes / 1MB) MB, the most a host stores or a client downloads per page: $($fatPages -join ', ')"
}

# GATE 3 - TOTAL, and it is a WARNING rather than a failure, which is the user's own decision on the
# 3D maps: the meshes are the payload nobody can trade away at packaging time (a mesh is the map's
# geometry - there is no "lower quality" setting that keeps it usable), so a hard cap here would mean a
# release that cannot be built at all rather than one that is large. Since stage V the warning is
# EXPECTED to fire: a map's building shells can hold up to 3,000,000 triangles and its mesh file up to
# 48 MB, so eleven maps of 1-4 floors at ~1 MB plus 10-45 MB of mesh each is well past 80 MB - and since
# stage W each map adds up to eight 2-4 MB atlas pages of building textures. It still prints on every
# run, with the 3D share beside the total, because the one thing that must not happen is the payload
# growing unnoticed - and the share is what says whether it grew for the expected reason: meshes and
# atlas pages most of it is stages V and W working; floor and side pictures most of it is something to
# look at.
$warnMapsBytes = 80MB
$mapsBytes = ($mapFiles | Measure-Object -Property Length -Sum).Sum
$mapsMeshBytes = (@($mapFiles | Where-Object { $_.Name -like "*-mesh.bin" -or $_.Name -match "-atlas-[0-7]\.jpg$" }) | Measure-Object -Property Length -Sum).Sum
if ($null -eq $mapsBytes) { $mapsBytes = 0 }
if ($null -eq $mapsMeshBytes) { $mapsMeshBytes = 0 }
$meshShare = if ($mapsBytes -gt 0) { [math]::Round(100 * $mapsMeshBytes / $mapsBytes) } else { 0 }
$mapsSizeLine = "Map payload: $("{0:N1}" -f ($mapsBytes / 1MB)) MB in maps\ ($("{0:N1}" -f ($mapsMeshBytes / 1MB)) MB of it 3D meshes and atlas pages, $meshShare %)"
if ($mapsBytes -gt $warnMapsBytes) {
    $meshNote = if ($meshShare -ge 50) {
        "the 3D meshes and atlas pages are $meshShare % of it, which is the expected reason since stages V and W (up to 48 MB of mesh and 48 MB of pages a map)"
    } else {
        "the 3D meshes and atlas pages are only $meshShare % of it - the PICTURES grew, which is not the expected reason; look at them before publishing"
    }
    Write-Host "$mapsSizeLine - over $($warnMapsBytes / 1MB) MB, and NOT a failure (the user's call: a mesh cannot be shrunk without losing the map): $meshNote." -ForegroundColor Yellow
} else {
    Write-Host $mapsSizeLine -ForegroundColor Green
}

# GATES 4, 5 and 6 - the METAS and the MESHES, in tools/check-maps-pack.py, for the same reason check-dtos.py is not
# written in PowerShell: it reads JSON, walks JPEG marker chains for each floor's real pixel size and
# does ceil() arithmetic per axis, and the readable version of that is 80 lines of Python rather than
# 80 lines of Join-Path. It is handed the one fact only this script knows - what schema the CLIENT
# BEING PACKAGED reads - and gates, for every set that is there: (4) each meta parses, names its own
# folder, and every floor file it names exists, with no orphan image no floor names; (5) each meta's
# schemaVersion equals the client's SupportedCaptureSchema, so a set the shipped client would skip
# cannot ship, and each floor's image is a real JPEG whose frame size equals its meta's width/height,
# which equal ceil(extent span * pxPerMetre) within 1 px; (6) a set whose meta names a mesh carries that
# file, at the byte length and the sha256 the meta states, and its deflated header parses with the
# meta's own extent and the floors' own band levels in it - plus no orphan *-mesh.bin that no meta
# names. A set with NO mesh passes (6) untouched: the mesh is optional end to end. It does NOT look at
# zones\ - a shipped set is
# checked against its own meta, so packaging works on a machine that never harvested the map - and it
# has no opinion about WHICH maps are present, which is the warning below.
if ($mapsPresent) {
    $mapCheck = Join-Path $repo "tools/check-maps-pack.py"
    if (-not (Test-Path $mapCheck)) { Fail "missing $mapCheck - the map pack check is not optional" }

    # Regexed out of the source, like the seed's solver stamp above: the constant is what the shipped
    # client compares each meta against, so a bump there has to reach this gate without an edit here.
    $catalogSource = Get-Content -Raw (Join-Path $client "UI\MapCatalog.cs")
    $captureSchema = [regex]::Match($catalogSource, 'SupportedCaptureSchema = ([0-9]+);').Groups[1].Value
    if ($captureSchema -eq "") {
        Fail "could not read SupportedCaptureSchema from UI\MapCatalog.cs - that constant is what decides whether the shipped client reads the shipped map sets at all"
    }

    & python $mapCheck $mapsDir --schema $captureSchema
    if ($LASTEXITCODE -ne 0) {
        Fail "the map sets in maps\ are not shippable (see above) - re-capture or re-run -RefreshMaps rather than shipping a set the client cannot place on the world"
    }

    # COVERAGE, reported and not gated (see the top of this section). -notcontains is
    # case-insensitive, which is what is wanted: the folder carries whatever casing the client sent.
    $mapKeys = @(Get-ChildItem $mapsDir -Directory | ForEach-Object { $_.Name })
    $missingMaps = @($vanillaMaps | Where-Object { $mapKeys -notcontains $_ })
    $mapTail = "($($mapKeys.Count) set(s), $("{0:N1}" -f ($mapsBytes / 1MB)) MB, schema $captureSchema)"
    if ($missingMaps.Count -gt 0) {
        Write-Host "Map sets: $($vanillaMaps.Count - $missingMaps.Count) of $($vanillaMaps.Count) vanilla maps present; missing: $($missingMaps -join ', ') - those maps fall back to DynamicMaps or a player's own captures $mapTail" -ForegroundColor Yellow
    } else {
        Write-Host "Map sets: $($vanillaMaps.Count) of $($vanillaMaps.Count) vanilla maps present $mapTail" -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- build
if (-not $SkipBuild) {
    foreach ($project in @($client, $server)) {
        Write-Host "Building $(Split-Path $project -Leaf) (Release)..."
        & dotnet build $project -c Release -v q --nologo -p:SptPath="$SptPath"
        if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed for $project" }
    }
}

# ---------------------------------------------------------------- the allowlist: source -> path inside the zip
$allow = New-Object System.Collections.Generic.List[object]
function Allow($source, $dest) { $allow.Add([pscustomobject]@{ Source = $source; Dest = $dest }) }

Allow (Join-Path $client "bin\Release\netstandard2.1\QuestTree.dll")     "BepInEx\plugins\QuestTree\QuestTree.dll"
Allow (Join-Path $client "kappa-quests.json")                             "BepInEx\plugins\QuestTree\kappa-quests.json"
Allow (Join-Path $server "bin\Release\net10.0\QuestTreeServer.dll")       "SPT_Runtime\user\mods\QuestTree\QuestTreeServer.dll"
Allow (Join-Path $server "weapon-builds.json")                            "SPT_Runtime\user\mods\QuestTree\cache\weapon-builds.json"
foreach ($zone in $zoneFiles) {
    Allow $zone.FullName ("SPT_Runtime\user\mods\QuestTree\zones\" + $zone.Name)
}
# The map sets, into the same folder the server stores uploads in, so a fresh install already has the
# maintainer's pictures and a host that later captures its own overwrites them in place. Every file
# here has been through the six gates above, and the relative path is <key>\<file> by GATE 1. With no
# maps\ folder $mapFiles is empty and the loop adds nothing, so the zip simply carries no pictures.
foreach ($mapFile in $mapFiles) {
    Allow $mapFile.FullName ("SPT_Runtime\user\mods\QuestTree\maps\" + $mapFile.FullName.Substring($mapsDir.Length + 1))
}
Allow (Join-Path $repo "README.md")                                       "README.md"
Allow $notes                                                              ("RELEASE-NOTES-$version.md")

foreach ($entry in $allow) {
    if (-not (Test-Path $entry.Source)) { Fail "missing source file: $($entry.Source)" }
}

# ---------------------------------------------------------------- the built DLLs must carry the version
foreach ($dll in @($allow | Where-Object { $_.Dest -like "*.dll" })) {
    $built = [System.Reflection.AssemblyName]::GetAssemblyName($dll.Source).Version
    $builtText = "{0}.{1}.{2}" -f $built.Major, $built.Minor, $built.Build
    if ($builtText -ne $version) { Fail "$($dll.Source) is version $builtText, not $version - rebuild" }
}

# ---------------------------------------------------------------- stage
$stagingName = "QuestTracker-$version-SPT-$sptVersion"
$staging = Join-Path $repo "Releases\$stagingName"
$zip = "$staging.zip"

New-Item -ItemType Directory -Force $staging | Out-Null
foreach ($entry in $allow) {
    $target = Join-Path $staging $entry.Dest
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $entry.Source $target -Force
}

# THE STAGED NOTES MUST NOT CARRY A HASH LINE. The repo copy gains one at the end of every run (see
# the bottom of this file), so from the SECOND run onwards the copy being zipped would assert a
# sha256 that is the PREVIOUS archive's - a number that cannot match the archive holding it, which
# is worse than no number at all. Stripped from the staged copy only; the repo copy is where the
# line lives and where a published hash is checked from.
$stagedNotes = Join-Path $staging ("RELEASE-NOTES-$version.md")
#
# Cast to [string] first: Get-Content -Raw hands back $null for an empty file, and -replace over
# $null yields an empty ARRAY, which has no TrimEnd - so a zero-byte notes file would die here with
# a method-not-found instead of reaching the stub gate below that has the sentence for it.
$stagedBody = ([string](Get-Content -Raw $stagedNotes)) -replace "(?m)^Archive sha256: [0-9a-fA-F]+[ \t]*\r?\n?", ""
[System.IO.File]::WriteAllText(
    $stagedNotes, (($stagedBody.TrimEnd(([char[]]"`r`n `t")) -replace "`r`n", "`n") + "`n"),
    (New-Object System.Text.UTF8Encoding($false)))

# The staging folder is NOT wiped first, on purpose: anything already in it that is not on the
# list - a file left by an earlier hand-assembly, or one planted to test this check - fails the
# run rather than being silently discarded.
#
# The last four are about the map payload, checked here as well as at the source folder because this
# list is applied to the STAGING FOLDER and the ZIP - so it also catches a file a hand-copy, an
# interrupted run, or a stale staging folder put in the archive after the maps gates had passed. A
# .png in maps\ is a raw capture that never got converted: it would ship at several times the JPEG's
# size and the loader would not draw it. `dynamicmaps` and `.svg` are the map artwork of the mod this
# release replaces - third-party files with no licence to redistribute, exactly the objective-gps
# mistake in a new folder. `.jsonc` would be a hand-edited meta with comments in it, which the client's
# JSON reader refuses. None of them can match what the release already carries: the zone files and
# kappa-quests.json end in `.json`, which `\.jsonc$` does not match, and no allowlisted path contains
# "dynamicmaps" (the match is case-insensitive, so "DynamicMaps.dll" is caught too).
$forbidden = "objective-gps|tarkovdev|\.bak$|\.svg$|dynamicmaps|\.jsonc$|\.png$"
$expected = @($allow | ForEach-Object { $_.Dest })
$found = @(Get-ChildItem $staging -Recurse -File | ForEach-Object { $_.FullName.Substring($staging.Length + 1) })

function VerifySet($label, $actual) {
    $bad = @($actual | Where-Object { $_ -match $forbidden })
    if ($bad.Count -gt 0) { Fail "$label contains a file that must never ship: $($bad -join ', ')" }
    $extra = @($actual | Where-Object { $expected -notcontains $_ })
    if ($extra.Count -gt 0) { Fail "$label contains file(s) not on the allowlist: $($extra -join ', ')" }
    $missing = @($expected | Where-Object { $actual -notcontains $_ })
    if ($missing.Count -gt 0) { Fail "$label is missing: $($missing -join ', ')" }
    if ($actual.Count -ne $expected.Count) { Fail "$label holds $($actual.Count) file(s); the allowlist has $($expected.Count)" }
}

VerifySet "the staging folder" $found

# ---------------------------------------------------------------- zip, then verify the zip itself
# -CompressionLevel Optimal is kept for the DLLs and the JSON; it buys nothing on the map JPEGs, which
# are already compressed, so the archive is roughly the size of the payload (15-30 MB with the map
# sets in it, ~190 KB before them) and this step takes seconds rather than being instant.
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    # Directory entries have an empty Name (Compress-Archive writes them with a trailing backslash,
    # not a slash, so testing the FullName's last character is the wrong check).
    $entries = @($archive.Entries | Where-Object { $_.Name -ne "" } | ForEach-Object { $_.FullName -replace "/", "\" })
} finally {
    $archive.Dispose()
}

VerifySet "the zip" $entries

# ---------------------------------------------------------------- the paperwork, now the zip is real
# 1.15.0 fell out of the process here. It was built, the four constants agreed, the zip verified - and
# it was never published, because nothing in this script has an opinion about whether the release was
# WRITTEN UP. The changelog's first line is the cheapest possible check that it was: it names the
# newest release, so if it still names the previous one, the version being packaged has no entry.
$changelog = Join-Path $repo "CHANGELOG.md"
if (-not (Test-Path $changelog)) { Fail "missing CHANGELOG.md - the changelog is not optional" }

$changelogHead = @(Get-Content $changelog -TotalCount 1)[0]
$wantHead = "# Quest Tracker $version"
if ($changelogHead -ne $wantHead) {
    Fail "CHANGELOG.md's first line is '$changelogHead', not '$wantHead' - write this version's entry at the top before packaging it"
}
Write-Host "CHANGELOG.md's first line is '$wantHead'." -ForegroundColor Green

# And the notes have to say something about THIS version. The Test-Path above only proves a file of
# that name exists, which is exactly what a rushed release produces; both halves of this check the
# ways it goes wrong. Size, because a one-line stub ships as readily as real notes. The version
# string, because last release's notes copied to a new filename pass every size check and then sit
# in the archive describing the wrong build.
$notesBytes = (Get-Item $notes).Length
if ($notesBytes -le 300) {
    Fail "Releases\RELEASE-NOTES-$version.md is $notesBytes byte(s) - write the notes rather than shipping a stub"
}
if ((Get-Content -Raw $notes) -notmatch [regex]::Escape($version)) {
    Fail "Releases\RELEASE-NOTES-$version.md never mentions $version - it looks like an earlier release's notes under a new name"
}
Write-Host "Release notes: $notesBytes bytes, and they name $version." -ForegroundColor Green

# ---------------------------------------------------------------- the shipped archive's hash, on record
# Compress-Archive writes each entry's timestamp, so two runs over identical inputs produce different
# bytes. Whichever zip was uploaded as the release asset is therefore the only one whose hash means
# anything, and a later run silently replaces the file on disk with one that hashes differently. This
# writes the hash of the archive JUST BUILT as the last line of the notes that ship beside it, so the
# published bytes are on record and a re-run's divergence is visible in git rather than invisible.
#
# Replaced, not appended, on a re-run: two "Archive sha256:" lines are two answers to one question.
# Written with LF and no BOM, as every other file in the repo is.
#
# WHAT THIS LINE IS NOT: the copy of the notes INSIDE the archive cannot carry it, because the notes
# are staged and zipped before the zip exists to hash - which is why the staged copy has any such
# line STRIPPED above rather than being left with the previous run's. The line lives in the repo,
# which is where a published hash is checked from, and it is one commit behind the archive by
# construction.
$hashLine = "Archive sha256: " + (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$body = ((Get-Content -Raw $notes) -replace "(?m)^Archive sha256: [0-9a-fA-F]+[ \t]*\r?\n?", "")
$body = $body.TrimEnd(([char[]]"`r`n `t"))
[System.IO.File]::WriteAllText(
    $notes, ($body -replace "`r`n", "`n") + "`n`n" + $hashLine + "`n",
    (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Recorded in RELEASE-NOTES-$version.md: $hashLine" -ForegroundColor Green

Write-Host ""
Write-Host "$zip" -ForegroundColor Green
$entries | Sort-Object | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "$($entries.Count) file(s), all on the allowlist. Version $version." -ForegroundColor Green
exit 0
