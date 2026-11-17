param(
    [int]$Port = 25570,
    [int]$HostWait = 20,
    [int]$TimeoutWait = 40,
    [int]$GhostKeep = 35
)

# Host-side peer timeout self-test. A real client pings every 2s, so the only way
# to prove the watchdog works is a peer that completes the handshake and then goes
# silent while keeping the socket open (tools/ghost_peer.py). Patterns stay ASCII.
$ErrorActionPreference = "Stop"

$exe = $env:STACKLANDS_EXE
if (-not $exe) {
    . (Join-Path $PSScriptRoot "resolve-stacklands.ps1")
    $found = Resolve-StacklandsDir
    if ($found) { $exe = Join-Path $found "Stacklands.exe" }
}
if (-not $exe -or -not (Test-Path $exe)) { throw ("Stacklands.exe not found: " + $exe + " (set STACKLANDS_EXE, or run selftest-all.ps1 -GameDir <dir>)") }
$python = $env:PYTHON_EXE
if (-not $python) { $cmd = Get-Command python -ErrorAction SilentlyContinue; if ($cmd) { $python = $cmd.Source } }
if (-not $python) { throw "python not found (set PYTHON_EXE)" }
$ghostScript = Join-Path $PSScriptRoot "ghost_peer.py"
$dir = Join-Path (Split-Path -Parent $PSScriptRoot) ".selftest"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$hostLog = Join-Path $dir "timeout-host.log"
foreach ($f in @($hostLog)) { if (Test-Path $f) { Remove-Item $f -Force } }

Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "== launching host on $Port =="
$hostProc = Start-Process -FilePath $exe -ArgumentList "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
Start-Sleep -Seconds $HostWait

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

Wait-Pattern $hostLog ("hosting on 0.0.0.0:" + $Port) $TimeoutWait

Write-Host "== connecting a ghost peer that never speaks again =="
$ghostProc = Start-Process -FilePath $python -ArgumentList $ghostScript, "$Port", "$GhostKeep" -PassThru -WindowStyle Hidden
Wait-Pattern $hostLog "peer=1" $TimeoutWait

Write-Host "== host should drop the silent peer =="
Wait-Pattern $hostLog "peer 1 timeout" $TimeoutWait
Wait-Pattern $hostLog "peer timeout" $TimeoutWait

foreach ($p in @($ghostProc, $hostProc)) {
    try { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force } } catch { }
}

Write-Host "host log: $hostLog"
if ($script:fail -gt 0) {
    Write-Host ("TIMEOUT TEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "TIMEOUT TEST PASSED"
