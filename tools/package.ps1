param(
    [string]$GameDir = "",
    [string]$OutDir = "",
    [string]$Dotnet = "",
    [string]$ShareDir = "",
    [switch]$NoSource
)

# Builds the mod and stages a folder that can be copied straight into the game's
# LocalLow Mods directory, or uploaded to the Steam Workshop through the in-game
# Mods screen. Keeps the manifest and the DLL in sync with the current build.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\StacklandsCoop\StacklandsCoop.csproj"
$props = Join-Path $root "src\StacklandsCoop\bin\Release\netstandard2.1\StacklandsCoop.dll"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

if (-not $OutDir) { $OutDir = Join-Path $root "dist" }

. (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
$GameDir = Resolve-StacklandsDir $GameDir
if (-not $GameDir) {
    throw "Stacklands install not found; pass -GameDir <dir> or set STACKLANDS_DIR"
}

# Keep the SDK caches inside the repo so a build never touches the user profile.
$caches = Join-Path $root ".build\caches"
$env:TMP = $env:TEMP = Join-Path $caches "tmp"
$env:DOTNET_CLI_HOME = Join-Path $caches "home"
$env:NUGET_PACKAGES = Join-Path $caches "nuget"
$env:APPDATA = $env:LOCALAPPDATA = Join-Path $caches "appdata"
New-Item -ItemType Directory -Force -Path $caches | Out-Null

if (-not $Dotnet) { $Dotnet = $env:DOTNET_EXE }
if (-not $Dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $Dotnet = $cmd.Source }
}
if (-not $Dotnet -or -not (Test-Path $Dotnet)) { throw "dotnet (SDK 8) not found; pass -Dotnet <path to dotnet.exe>" }

& $Dotnet build $project -c Release "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) {
    throw "build failed"
}

$stage = Join-Path $OutDir "stacklands_coop"
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $root "mod\manifest.json") (Join-Path $stage "manifest.json") -Force
Copy-Item $props (Join-Path $stage "StacklandsCoop.dll") -Force

$readme = Join-Path $root "docs\WORKSHOP.md"
if (-not (Test-Path $readme)) { $readme = Join-Path $root "README.md" }
if (Test-Path $readme) {
    Copy-Item $readme (Join-Path $stage "README.md") -Force
}

# The in-game upload screen uses icon.png as the Workshop preview image.
$icon = Join-Path $root "mod\icon.png"
if (Test-Path $icon) {
    Copy-Item $icon (Join-Path $stage "icon.png") -Force
}

# 16:9 art for the Workshop page header (uploaded by hand on the item page).
$banner = Join-Path $root "mod\workshop_banner.png"
if (Test-Path $banner) {
    Copy-Item $banner (Join-Path $stage "workshop_banner.png") -Force
}

# The source zip ships by default: subscribers get the C# sources next to the DLL
# so they can read or rebuild them. Only source + build instructions go in:
# internal design notes, backups and release flow stay out of the published zip.
# Pass -NoSource for a DLL-only package.
if (-not $NoSource) {
    $srcStage = Join-Path $env:TEMP ("coop-src-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $srcStage | Out-Null
    Copy-Item (Join-Path $root "src") (Join-Path $srcStage "src") -Recurse -Force
    foreach ($f in @("README.md", "LICENSE", "build.ps1")) {
        $p = Join-Path $root $f
        if (Test-Path $p) { Copy-Item $p (Join-Path $srcStage $f) -Force }
    }
    Get-ChildItem -Path (Join-Path $srcStage "src") -Include "bin", "obj" -Recurse -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force
    $zip = Join-Path $stage "source.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($srcStage, $zip)
    Remove-Item $srcStage -Recurse -Force
}

$manifest = Get-Content (Join-Path $stage "manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$size = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1KB, 1)
Write-Host ("package: {0} (v{1}, {2} KB, {3:N1}s)" -f $stage, $manifest.version, $size, $stopwatch.Elapsed.TotalSeconds)
Write-Host ("install: copy this folder to %USERPROFILE%\AppData\LocalLow\sokpop\Stacklands\Mods\stacklands_coop")

# Optional: also drop versioned zips into a folder other machines can reach (LAN
# share, network drive, synced directory). Off unless -ShareDir is passed.

if ($ShareDir -and (Test-Path $ShareDir)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $ShareDir ("stacklands_coop_v" + $manifest.version + ".zip")
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Zip a staging copy that already contains the folder name, so the other
    # machine extracts straight into Mods and ends up with Mods\stacklands_coop.
    $pubTmp = Join-Path $env:TEMP ("coop-pub-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $pubTmp | Out-Null
    Copy-Item $stage (Join-Path $pubTmp "stacklands_coop") -Recurse -Force
    [System.IO.Compression.ZipFile]::CreateFromDirectory($pubTmp, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item $pubTmp -Recurse -Force

    $latest = Join-Path $ShareDir "stacklands_coop_latest.zip"
    Copy-Item $zip $latest -Force

    $zsize = [math]::Round((Get-Item $zip).Length / 1KB, 1)
    Write-Host ("share: {0} ({1} KB)" -f $zip, $zsize)

    # Keep only the newest three versioned zips.
    Get-ChildItem $ShareDir -Filter "stacklands_coop_v*.zip" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip 3 |
        Remove-Item -Force
}
