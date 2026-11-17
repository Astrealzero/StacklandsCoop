param(
    [int]$Port = 25573,
    [int]$BootWait = 70,
    [int]$JoinWait = 90,
    [int]$PollSeconds = 90
)

# Follow-the-host boards: the host runs a real world and hops to forest; every client must
# end up on the same board and keep receiving snapshots. All patterns stay ASCII so
# PowerShell 5.1 parses this file anywhere.
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
$hostLog = Join-Path $dir "boards-host.log"
$c1Log = Join-Path $dir "boards-c1.log"
$c2Log = Join-Path $dir "boards-c2.log"
foreach ($f in @($hostLog, $c1Log, $c2Log)) { if (Test-Path $f) { Remove-Item $f -Force } }

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
    return (Select-String -Path $path -Pattern $pattern -SimpleMatch).Count
}

try {
    Write-Host "== launching host (scenario world) on $Port =="
    $hostProc = Start-Process -FilePath $exe -ArgumentList "-coopScenario", "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
    Wait-Pattern $hostLog "scenario spawned cards=" $BootWait
    if ($script:fail -gt 0) {
        throw "host did not boot the scenario world"
    }

    # Wait until the host has actually moved to forest, so the clients join a world that is
    # already on another board and have to follow it.
    Wait-Pattern $hostLog "scenario board switched" $PollSeconds
    if ($script:fail -gt 0) {
        throw "host never switched boards"
    }

    Write-Host "== launching client 1 (follows the host) =="
    $c1 = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-logFile", "`"$c1Log`"" -PassThru
    Start-Sleep -Seconds 8

    Write-Host "== launching client 2 (follows the host) =="
    $c2 = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-logFile", "`"$c2Log`"" -PassThru

    Wait-Pattern $c1Log "travel board forest" $PollSeconds
    Wait-Pattern $c1Log "board=forest" $PollSeconds
    Wait-Pattern $c2Log "board=forest" $PollSeconds
    Wait-Pattern $hostLog "player board P1 forest" $PollSeconds
    Wait-Pattern $hostLog "player board P2 forest" $PollSeconds

    # Everyone sits on the host's board and keeps syncing there.
    $before1 = Count-Pattern $c1Log "snapshot applied rev="
    $before2 = Count-Pattern $c2Log "snapshot applied rev="
    Start-Sleep -Seconds 8
    $after1 = Count-Pattern $c1Log "snapshot applied rev="
    $after2 = Count-Pattern $c2Log "snapshot applied rev="
    if ($after1 -gt $before1) {
        Write-Host "  PASS  client 1 keeps syncing on forest"
    } else {
        Write-Host "  FAIL  client 1 stopped syncing on forest"
        $script:fail++
    }
    if ($after2 -gt $before2) {
        Write-Host "  PASS  client 2 keeps syncing on forest"
    } else {
        Write-Host "  FAIL  client 2 stopped syncing on forest"
        $script:fail++
    }

    # A late arrival must land on the host's board too, not on main.
    if ((Count-Pattern $c1Log "board=main") -gt 0) {
        Write-Host "  WARN  client 1 also logged board=main (join snapshot before the follow)"
    } else {
        Write-Host "  PASS  client 1 never stayed on main"
    }
} catch {
    Write-Host ("  ERROR  {0}" -f $_.Exception.Message)
    $script:fail++
} finally {
    Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "host log:    $hostLog"
Write-Host "client logs: $c1Log / $c2Log"
if ($script:fail -gt 0) {
    Write-Host ("BOARDS TEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "BOARDS TEST PASSED"
