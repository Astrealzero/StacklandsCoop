param(
    [int]$Port = 25571,
    [int]$BootWait = 70,
    [int]$ClientWait = 60
)

# Reject path test: the client announces a bogus mods hash via -coopFakeMods, so the
# host must refuse the handshake and the client must report the refusal instead of
# drifting into a broken session. Assertions stay ASCII for the ANSI PS 5.1 reader.

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
$hostLog = Join-Path $logDir "reject-host.log"
$clientLog = Join-Path $logDir "reject-client.log"
Remove-Item $hostLog, $clientLog -ErrorAction SilentlyContinue

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
    Start-Process -FilePath $exe -ArgumentList @("-coopHost", "$Port", "-logFile", $hostLog) | Out-Null
    if (Wait-Pattern $hostLog "found 1 mods" $BootWait) {
        Write-Host "PASS  host loaded the mod"
    } else {
        Write-Host "FAIL  host loaded the mod"
        Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
        $failed = $true
    }

    if (-not $failed) {
        Write-Host "starting mismatched client..."
        Start-Process -FilePath $exe -ArgumentList @("-coopJoin", "127.0.0.1:$Port", "-coopFakeMods", "deadbeef", "-logFile", $clientLog) | Out-Null

        if (Wait-Pattern $hostLog "reject mods mismatch" $ClientWait) {
            Write-Host "PASS  host refused the mismatched client"
        } else {
            Write-Host "FAIL  host refused the mismatched client"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "deadbeef" 30) {
            Write-Host "PASS  client reported the rejected fingerprint"
        } else {
            Write-Host "FAIL  client reported the rejected fingerprint"
            Get-Content $clientLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "snapshot applied" 10) {
            Write-Host "FAIL  client should not have received a snapshot"
            $failed = $true
        } else {
            Write-Host "PASS  no snapshot after rejection"
        }
    }
}
finally {
    Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($failed) {
    exit 1
}

Write-Host "REJECT TEST PASSED"
