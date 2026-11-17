param(
    [int]$Port = 25574,
    [int]$BootWait = 90,
    [int]$PollSeconds = 90,
    [int]$MonthWait = 120
)

# Ready vote -> real settlement. The host runs a playing scenario world with its own
# month forcing disabled (-coopNoScenarioMonth), so the only way the month can advance is
# the client's ready vote. All patterns stay ASCII for PowerShell 5.1.
$ErrorActionPreference = "Stop"

$exe = $env:STACKLANDS_EXE
if (-not $exe) {
    . (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
    $found = Resolve-StacklandsDir
    if ($found) { $exe = Join-Path $found "Stacklands.exe" }
}
if (-not $exe -or -not (Test-Path $exe)) { throw ("Stacklands.exe not found: " + $exe + " (set STACKLANDS_EXE, or run selftest-all.ps1 -GameDir <dir>)") }
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) ".selftest"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$hostLog = Join-Path $dir "ready-host.log"
$clientLog = Join-Path $dir "ready-client.log"
foreach ($f in @($hostLog, $clientLog)) { if (Test-Path $f) { Remove-Item $f -Force } }

Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$script:fail = 0

function Wait-Pattern($path, $pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) {
            $text = Get-Content $path -Raw -Encoding UTF8
            if ($text -and $text.Contains($pattern)) {
                Write-Host ("  PASS  {0}" -f $pattern)
                return
            }
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host ("  FAIL  {0}" -f $pattern)
    $script:fail++
}

try {
    Write-Host "== launching host (scenario world, ready vote only) =="
    $hostProc = Start-Process -FilePath $exe -ArgumentList "-coopScenario", "-coopNoScenarioMonth", "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
    Wait-Pattern $hostLog "scenario spawned cards=" $BootWait
    if ($script:fail -gt 0) {
        throw "host did not boot the scenario world"
    }

    Write-Host "== launching client (ready after 8s) =="
    $client = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-coopReady", "-logFile", "`"$clientLog`"" -PassThru

    Wait-Pattern $hostLog "ready peer=1" $PollSeconds
    Wait-Pattern $hostLog "force month end: all ready" $PollSeconds
    # The settlement runs as the game's own end-of-month routine; the observable proof is the
    # client syncing the new month number (the host-side "scenario month advanced" line is
    # unreliable because the world clock is already frozen when the vote lands).
    Wait-Pattern $clientLog "month synced" $MonthWait
} catch {
    Write-Host ("  ERROR  {0}" -f $_.Exception.Message)
    $script:fail++
} finally {
    Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "host log:   $hostLog"
Write-Host "client log: $clientLog"
if ($script:fail -gt 0) {
    Write-Host ("READY TEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "READY TEST PASSED"
