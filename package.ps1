# Builds both halves in Release and assembles the release zip from an ALLOWLIST.
#
# Every release before 1.13.2 was put together by hand, and the zones folder was copied out of the
# live install - one directory below objective-gps.json and tarkovdev-quests.json, two third-party
# files with no licence to redistribute. Seventeen zips were clean because one person
# remembered. This script does not read the install at all (except with -RefreshZones, which copies
# only zones\*.json into the repo), assembles exactly the files named below, and then checks the
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
# Proven able to fail before it shipped: a planted objective-gps.json in the staging folder, a
# mismatched version constant, and (for the DTO check) a bogus property added to the client's
# RewardDto, each made it exit non-zero. The two paperwork gates were proven the same way: a
# CHANGELOG.md whose first line named 1.18.0 while the constants said 1.18.1, and a release-notes
# file cut to 120 bytes, each made it exit non-zero and name what it wanted.
#
# Usage:  .\package.ps1                 build, stage, zip, verify
#         .\package.ps1 -RefreshZones   first copy zones\*.json from the install into the repo
#         .\package.ps1 -SkipBuild      stage and verify what bin\ already holds

param(
    [string]$SptPath = "C:\Games\SPT",
    [switch]$RefreshZones,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$client = Join-Path $repo "Source\Tarkov-QuestTree"
$server = Join-Path $repo "Source\Tarkov-QuestTree-Server"
$sptVersion = "4.1.5"

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
$forbidden = "objective-gps|tarkovdev|\.bak$"
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
