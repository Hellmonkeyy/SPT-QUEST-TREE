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
# Proven able to fail before it shipped: a planted objective-gps.json in the staging folder, and a
# mismatched version constant, each made it exit non-zero.
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

Write-Host ""
Write-Host "$zip" -ForegroundColor Green
$entries | Sort-Object | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "$($entries.Count) file(s), all on the allowlist. Version $version." -ForegroundColor Green
exit 0
