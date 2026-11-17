param(
    [string]$Configuration = "Release",
    [switch]$Install,
    [string]$Dotnet = "",
    [string]$GameDir = ""
)

# Builds the mod. $Dotnet may point at a specific dotnet.exe; otherwise DOTNET_EXE
# is used, and PATH is the last resort.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSCommandPath
$proj = Join-Path $root "src\StacklandsCoop\StacklandsCoop.csproj"
$caches = Join-Path $root ".build\caches"

if ([string]::IsNullOrEmpty($Dotnet)) {
    $Dotnet = $env:DOTNET_EXE
}

if ([string]::IsNullOrEmpty($Dotnet)) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) {
        throw "dotnet not found on PATH; pass -Dotnet <path to dotnet.exe>"
    }
    $Dotnet = $cmd.Source
}

# The project references the game's managed assemblies, so the install location has
# to be known. -GameDir wins, then STACKLANDS_DIR / STACKLANDS_EXE, then common locations.
if ([string]::IsNullOrEmpty($GameDir)) {
    . (Join-Path $root "tools\resolve-stacklands.ps1")
    $GameDir = Resolve-StacklandsDir
}

if ([string]::IsNullOrEmpty($GameDir)) {
    throw "Stacklands install not found; pass -GameDir <dir> or set STACKLANDS_DIR"
}

# Keep the SDK caches inside the repo so the build does not touch the user profile.
$env:DOTNET_CLI_HOME = Join-Path $caches "home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = Join-Path $caches "nuget"
$env:APPDATA = Join-Path $caches "appdata"
$env:LOCALAPPDATA = Join-Path $caches "appdata"
$env:TMP = $env:TEMP = Join-Path $caches "tmp"

New-Item -ItemType Directory -Force -Path $caches | Out-Null

$buildArgs = @("build", $proj, "-c", $Configuration)
if (-not [string]::IsNullOrEmpty($GameDir)) {
    $buildArgs += "-p:GameDir=$GameDir"
}

& $Dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "build failed"
}

$out = Join-Path $root "src\StacklandsCoop\bin\$Configuration\netstandard2.1\StacklandsCoop.dll"
if (-not (Test-Path $out)) {
    throw "output missing: $out"
}

if ($Install) {
    $staged = Join-Path $root "dist\stacklands_coop"
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    Copy-Item $out (Join-Path $staged "StacklandsCoop.dll") -Force
    Copy-Item (Join-Path $root "mod\manifest.json") (Join-Path $staged "manifest.json") -Force
    Write-Host "staged to $staged (copy the folder to the game's Mods directory)"
}
