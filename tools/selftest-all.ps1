param(
    [switch]$Quick,
    [string]$GameDir = ""
)

# Runs every automated suite in sequence (they all drive the real game, so they cannot run in
# parallel) and prints one summary line each. ASCII only: PowerShell 5.1 reads BOM-less scripts
# as ANSI, so Chinese patterns would break the parser.
$ErrorActionPreference = "Continue"

$root = Split-Path -Parent $PSScriptRoot

# Single place that resolves the game executable for every suite: -GameDir wins, then
# STACKLANDS_EXE, then the default Steam location. The suites read STACKLANDS_EXE.
. (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
$gameDir = Resolve-StacklandsDir $GameDir
if (-not $gameDir) {
    Write-Host "Stacklands install not found."
    Write-Host "Pass -GameDir <install dir>, or set STACKLANDS_DIR / STACKLANDS_EXE."
    exit 1
}
$env:STACKLANDS_EXE = Join-Path $gameDir "Stacklands.exe"
Write-Host ("game: " + $env:STACKLANDS_EXE)
$suites = @(
    @{ Name = "selftest";          File = "selftest.ps1" },
    @{ Name = "multi";             File = "selftest-multi.ps1" },
    @{ Name = "reject";            File = "selftest-reject.ps1" },
    @{ Name = "ready";             File = "selftest-ready.ps1" },
    @{ Name = "scenario";          File = "selftest-scenario.ps1" },
    @{ Name = "boards";            File = "selftest-boards.ps1" },
    @{ Name = "timeout";           File = "selftest-timeout.ps1" },
    @{ Name = "endpoint";          File = "selftest-endpoint.ps1" },
    @{ Name = "reconnect";         File = "selftest-reconnect.ps1" },
    @{ Name = "rejoin";            File = "selftest-rejoin.ps1" },
    @{ Name = "restart";           File = "selftest-restart.ps1" }
)

if ($Quick) {
    $suites = @(
        @{ Name = "selftest";  File = "selftest.ps1" },
        @{ Name = "reject";    File = "selftest-reject.ps1" }
    )
}

$results = @()
$failed = 0

foreach ($suite in $suites) {
    $path = Join-Path (Join-Path $root "tools") $suite.File
    if (-not (Test-Path $path)) {
        Write-Host ("SKIP  {0} (missing {1})" -f $suite.Name, $suite.File)
        continue
    }

    Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Host ("== {0} ==" -f $suite.Name)
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $path 2>&1
    $exit = $LASTEXITCODE
    $output | Where-Object { $_ -match "PASS|FAIL|WARN|ERROR|TEST PASSED|TEST FAILED" } |
        ForEach-Object { Write-Host ("   {0}" -f $_) }

    if ($exit -eq 0) {
        Write-Host ("OK    {0}" -f $suite.Name)
        $results += [pscustomobject]@{ Name = $suite.Name; Ok = $true; Detail = "passed" }
    } else {
        Write-Host ("FAILED {0} (exit {1})" -f $suite.Name, $exit)
        $results += [pscustomobject]@{ Name = $suite.Name; Ok = $false; Detail = "exit $exit" }
        $failed++
    }
}

Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "== summary =="
$results | ForEach-Object { Write-Host ("  {0}  {1}" -f $(if ($_.Ok) { "OK  " } else { "FAIL" }), $_.Name) }

if ($failed -gt 0) {
    Write-Host ("ALL TESTS FAILED ({0} suite(s))" -f $failed)
    exit 1
}

Write-Host "ALL TESTS PASSED"
