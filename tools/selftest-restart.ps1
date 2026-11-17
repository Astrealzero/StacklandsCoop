param(
    [int]$Port = 25575,
    [int]$BootWait = 90,
    [int]$RestartWait = 200,
    [int]$SettleSeconds = 10
)

# Host throws its round away (the same thing that happens when it loads a save or restarts)
# while a client is still connected. Every card the client knew about disappears at once, so
# this covers the structural rebuild path rather than the everyday incremental one.
# All patterns stay ASCII for PowerShell 5.1.
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
$hostLog = Join-Path $dir "restart-host.log"
$clientLog = Join-Path $dir "restart-client.log"
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

function Count-Pattern($path, $pattern) {
    if (-not (Test-Path $path)) { return 0 }
    return (Select-String -Path $path -Pattern $pattern -SimpleMatch -ErrorAction SilentlyContinue).Count
}

try {
    Write-Host "== launching host (scenario world) =="
    $hostProc = Start-Process -FilePath $exe -ArgumentList "-coopScenario", "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
    Wait-Pattern $hostLog "scenario spawned cards=" $BootWait
    if ($script:fail -gt 0) {
        throw "host did not boot the scenario world"
    }

    Write-Host "== launching client =="
    $client = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-logFile", "`"$clientLog`"" -PassThru
    Wait-Pattern $clientLog "snapshot applied rev=" $BootWait
    if ($script:fail -gt 0) {
        throw "client never received a snapshot"
    }

    Write-Host "== waiting for the host to restart its round =="
    Wait-Pattern $hostLog "scenario round restarted" $RestartWait

    $before = Count-Pattern $clientLog "snapshot applied rev="
    Start-Sleep -Seconds $SettleSeconds
    $after = Count-Pattern $clientLog "snapshot applied rev="
    if ($after -gt $before) {
        Write-Host ("  PASS  client kept syncing after the restart ({0} -> {1})" -f $before, $after)
    } else {
        Write-Host ("  FAIL  client stopped syncing after the restart ({0} -> {1})" -f $before, $after)
        $script:fail++
    }

    # After a restart the client should only hold the new round's cards (the fresh starter pack),
    # not the six cards the old round had. Look at the newest applied snapshot, not the first.
    $lastLine = Select-String -Path $clientLog -Pattern "snapshot applied rev=" -ErrorAction SilentlyContinue |
        Select-Object -Last 1
    if ($lastLine -and $lastLine.Line -match "snapshot applied rev=\d+ cards=(\d+)") {
        $cards = [int]$Matches[1]
        if ($cards -le 2) {
            Write-Host ("  PASS  client rebuilt the fresh round (cards={0})" -f $cards)
        } else {
            Write-Host ("  FAIL  client still holds {0} cards after the restart" -f $cards)
            $script:fail++
        }
    } else {
        Write-Host "  WARN  no applied snapshot line to inspect"
    }
} catch {
    Write-Host ("  ERROR  {0}" -f $_.Exception.Message)
    $script:fail++
} finally {
    Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "host log:   $hostLog"
Write-Host "client log: $clientLog"
if ($script:fail -gt 0) {
    Write-Host ("RESTART TEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "RESTART TEST PASSED"
