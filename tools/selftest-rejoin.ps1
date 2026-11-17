param(
    [int]$Port = 25572,
    [int]$BootWait = 70,
    [int]$ClientWait = 70,
    [int]$RejoinWait = 70
)

# Mid-game rejoin: the host runs the scripted scenario world (cards already grown),
# a client joins, that client is killed, and a fresh client joins the same host.
# The second client must receive the full grown world without the host restarting.
# All assertions are ASCII on purpose (PowerShell 5.1 reads BOM-less scripts as ANSI).

$ErrorActionPreference = "Stop"
$exe = $env:STACKLANDS_EXE
if (-not $exe) {
    . (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
    $found = Resolve-StacklandsDir
    if ($found) { $exe = Join-Path $found "Stacklands.exe" }
}
if (-not $exe -or -not (Test-Path $exe)) { throw ("Stacklands.exe not found: " + $exe + " (set STACKLANDS_EXE, or run selftest-all.ps1 -GameDir <dir>)") }
$logDir = Join-Path (Split-Path -Parent $PSScriptRoot) ".selftest"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$hostLog = Join-Path $logDir "rejoin-host.log"
$c1Log = Join-Path $logDir "rejoin-c1.log"
$c2Log = Join-Path $logDir "rejoin-c2.log"
Remove-Item $hostLog, $c1Log, $c2Log -ErrorAction SilentlyContinue

Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force

function Wait-Pattern([string]$path, [string]$needle, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) {
            $text = Get-Content -Raw -Encoding UTF8 $path -ErrorAction SilentlyContinue
            if ($text -and $text.Contains($needle)) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$failed = $false
try {
    Write-Host "starting host..."
    Start-Process -FilePath $exe -ArgumentList @("-coopHost", "$Port", "-coopScenario", "-logFile", $hostLog) | Out-Null
    if (Wait-Pattern $hostLog "scenario spawned cards=" $BootWait) {
        Write-Host "PASS  host spawned scenario cards"
    } else {
        Write-Host "FAIL  host spawned scenario cards"
        Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
        $failed = $true
    }

    $c1 = $null
    if (-not $failed) {
        Write-Host "starting first client..."
        $c1 = Start-Process -FilePath $exe -ArgumentList @("-coopJoin", "127.0.0.1:$Port", "-logFile", $c1Log) -PassThru
        if (Wait-Pattern $c1Log "packs=2" $ClientWait) {
            Write-Host "PASS  first client received the grown world"
        } else {
            Write-Host "FAIL  first client received the grown world"
            Get-Content $c1Log -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        Write-Host "killing first client..."
        if ($c1 -and -not $c1.HasExited) {
            Stop-Process -Id $c1.Id -Force
        }
        Start-Sleep -Seconds 6
    }

    if (-not $failed) {
        # A brand new process, not the auto-reconnect path: this is a mid-game join.
        Write-Host "starting second client..."
        Start-Process -FilePath $exe -ArgumentList @("-coopJoin", "127.0.0.1:$Port", "-logFile", $c2Log) | Out-Null
        if (Wait-Pattern $c2Log "snapshot applied rev=" $RejoinWait) {
            Write-Host "PASS  second client applied a snapshot"
        } else {
            Write-Host "FAIL  second client applied a snapshot"
            Get-Content $c2Log -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $c2Log "cards=6" $RejoinWait) {
            Write-Host "PASS  second client received the full grown world"
        } else {
            Write-Host "FAIL  second client received the full grown world (expect cards=6 after removal)"
            Get-Content $c2Log -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $hostLog "peer=2" 20) {
            Write-Host "PASS  host accepted a second connection"
        } else {
            Write-Host "FAIL  host accepted a second connection"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }
}
finally {
    Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($failed) {
    exit 1
}

Write-Host "REJOIN TEST PASSED"
