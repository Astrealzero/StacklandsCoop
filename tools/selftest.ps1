param(
    [int]$Port = 25566,
    [int]$HostWait = 20,
    [int]$ClientWait = 30,
    [int]$PollSeconds = 40
)

# Two-instance protocol self-test: no player input needed. Launches a host and a
# client, then polls the logs for the handshake lines that prove the transport,
# protocol and session layers are alive. Patterns stay ASCII so Windows
# PowerShell 5.1 can parse this file regardless of code page.
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
$hostLog = Join-Path $dir "host.log"
$clientLog = Join-Path $dir "client.log"
foreach ($f in @($hostLog, $clientLog)) { if (Test-Path $f) { Remove-Item $f -Force } }

# Leftover instances hold the port and silently break the next run.
Get-Process -Name "Stacklands" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "== launching host on $Port =="
$hostProc = Start-Process -FilePath $exe -ArgumentList "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
Start-Sleep -Seconds $HostWait

Write-Host "== launching client =="
$clientProc = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-coopChat", "selftest_chat_line", "-logFile", "`"$clientLog`"" -PassThru
Start-Sleep -Seconds $ClientWait

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

Write-Host "== host log =="
Wait-Pattern $hostLog "codec ok" $PollSeconds
Wait-Pattern $hostLog "panel hint shown" $PollSeconds
Wait-Pattern $hostLog ("hosting on 0.0.0.0:" + $Port) $PollSeconds
Wait-Pattern $hostLog "peer=1" $PollSeconds
Wait-Pattern $hostLog "snapshot sent rev=" $PollSeconds
Wait-Pattern $hostLog "mods check " $PollSeconds
Wait-Pattern $hostLog "chat P1: selftest_chat_line" $PollSeconds

Write-Host "== client log =="
Wait-Pattern $clientLog "epoch=" $PollSeconds
Wait-Pattern $clientLog "peer=0" $PollSeconds
Wait-Pattern $clientLog "snapshot applied rev=" $PollSeconds
Wait-Pattern $clientLog "state=Playing" $PollSeconds
Wait-Pattern $clientLog "defs ok " $PollSeconds
Wait-Pattern $clientLog "ping ok" $PollSeconds

foreach ($p in @($clientProc, $hostProc)) {
    try { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force } } catch { }
}

Write-Host "host log:   $hostLog"
Write-Host "client log: $clientLog"
if ($script:fail -gt 0) {
    Write-Host ("SELFTEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "SELFTEST PASSED"
