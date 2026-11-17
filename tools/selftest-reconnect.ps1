param(
    [int]$Port = 25567,
    [int]$BootWait = 18,
    [int]$ReconnectWait = 40
)

# Verifies the 60 s client reconnect window: host goes away mid-session, comes back
# on the same port, and the client must re-handshake without a manual rejoin.
$ErrorActionPreference = "Stop"

$exe = $env:STACKLANDS_EXE
if (-not $exe) {
    . (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
    $found = Resolve-StacklandsDir
    if ($found) { $exe = Join-Path $found "Stacklands.exe" }
}
if (-not $exe -or -not (Test-Path $exe)) { throw ("Stacklands.exe not found: " + $exe + " (set STACKLANDS_EXE, or run selftest-all.ps1 -GameDir <dir>)") }
$work = Split-Path -Parent $exe
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) ".selftest"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$hostLog = Join-Path $dir "reconnect-host.log"
$clientLog = Join-Path $dir "reconnect-client.log"
foreach ($log in @($hostLog, $clientLog)) {
    if (Test-Path $log) { Remove-Item $log -Force }
}

Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$env:SteamAppId = "1948280"

function Start-Host {
    return Start-Process -FilePath $exe -ArgumentList "-coopHost", "$Port", "-logFile", $hostLog -WorkingDirectory $work -PassThru
}

function Test-Log([string]$path, [string]$pattern) {
    if (-not (Test-Path $path)) { return $false }
    $text = Get-Content $path -Raw -Encoding UTF8
    return $text -and $text.Contains($pattern)
}

function Wait-Log([string]$path, [string]$pattern, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Log $path $pattern) { return $true }
        Start-Sleep -Milliseconds 500
    }

    return $false
}

$hostProc = Start-Host
Start-Sleep -Seconds 2
Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-logFile", $clientLog -WorkingDirectory $work

$ok = $true

if (-not (Wait-Log $clientLog "epoch=" $BootWait)) { Write-Host "  FAIL  initial handshake"; $ok = $false } else { Write-Host "  PASS  initial handshake" }
if (-not (Wait-Log $hostLog "peer=1" $BootWait)) { Write-Host "  FAIL  host saw client"; $ok = $false } else { Write-Host "  PASS  host saw client" }

Write-Host "== dropping host =="
if ($hostProc -and -not $hostProc.HasExited) {
    Stop-Process -Id $hostProc.Id -Force
}

Start-Sleep -Seconds 3
if (-not (Wait-Log $clientLog "reconnecting" 15)) { Write-Host "  FAIL  client noticed the drop"; $ok = $false } else { Write-Host "  PASS  client noticed the drop" }

Write-Host "== restarting host =="
Start-Host

if (-not (Wait-Log $clientLog "reconnected" $ReconnectWait)) { Write-Host "  FAIL  client rejoined"; $ok = $false } else { Write-Host "  PASS  client rejoined" }
if (-not (Wait-Log $clientLog "snapshot applied rev=" $ReconnectWait)) { Write-Host "  FAIL  client got snapshot after rejoin"; $ok = $false } else { Write-Host "  PASS  client got snapshot after rejoin" }

Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ""
Write-Host "host log:   $hostLog"
Write-Host "client log: $clientLog"

if (-not $ok) { exit 1 }
Write-Host "RECONNECT TEST PASSED"
