param(
    [int]$Port = 25576,
    [int]$BootWait = 70,
    [int]$PollSeconds = 60
)

# Checks the "host:port" join form and the language switch.
# Host speaks English, client joins with an embedded port and Chinese.

$ErrorActionPreference = "Stop"
$game = $env:STACKLANDS_EXE
if (-not $game) {
    . (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
    $found = Resolve-StacklandsDir
    if ($found) { $game = Join-Path $found "Stacklands.exe" }
}
if (-not $game -or -not (Test-Path $game)) { throw ("Stacklands.exe not found: " + $game + " (set STACKLANDS_EXE, or run selftest-all.ps1 -GameDir <dir>)") }
$logDir = Join-Path (Split-Path -Parent $PSScriptRoot) ".selftest"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$hostLog = Join-Path $logDir "endpoint-host.log"
$clientLog = Join-Path $logDir "endpoint-client.log"
Remove-Item $hostLog, $clientLog -ErrorAction SilentlyContinue

function Start-Game([string[]]$extra, [string]$log) {
    $args = @($extra) + @("-logFile", $log)
    Start-Process -FilePath $game -ArgumentList $args -PassThru
}

function Wait-Text([string]$path, [string]$needle, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) {
            $text = Get-Content $path -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($text -and $text.Contains($needle)) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$fail = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Host "PASS  $name" } else { Write-Host "FAIL  $name"; $script:fail++ }
}

Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$procs = @()
try {
    $procs += Start-Game @("-coopHost", "$Port", "-coopLang", "en") $hostLog
    Check "host booted and forced english" (Wait-Text $hostLog "loc forced: en" $BootWait)
    Check "host reports english" (Wait-Text $hostLog "loc en (forced)" $BootWait)
    Check "host listening" (Wait-Text $hostLog "hosting on 0.0.0.0:$Port" $BootWait)

    $procs += Start-Game @("-coopJoin", "127.0.0.1:$Port") $clientLog
    Check "client dialled host:port" (Wait-Text $clientLog "dialing 127.0.0.1:$Port" $BootWait)
    Check "handshake completed" (Wait-Text $clientLog "epoch=" $PollSeconds)
    Check "client applied a snapshot" (Wait-Text $clientLog "snapshot applied rev=" $PollSeconds)
    Check "client runs in chinese" (Wait-Text $clientLog "loc zh" $PollSeconds)
}
finally {
    foreach ($p in $procs) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

if ($fail -eq 0) { Write-Host "ENDPOINT TEST PASSED" } else { Write-Host "ENDPOINT TEST FAILED"; exit 1 }
