param(
    [int]$Port = 25568,
    [int]$BootWait = 22,
    [int]$ClientWait = 26,
    [int]$PollSeconds = 40
)

# Three-instance relay test: host + two clients. Proves the host relays a message
# from one client to the other, which the two-instance test cannot cover.
# All patterns stay ASCII so Windows PowerShell 5.1 parses this file on any code page.
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
$hostLog = Join-Path $dir "multi-host.log"
$c1Log = Join-Path $dir "multi-c1.log"
$c2Log = Join-Path $dir "multi-c2.log"
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

Write-Host "== launching host on $Port =="
$hostProc = Start-Process -FilePath $exe -ArgumentList "-coopHost", "$Port", "-logFile", "`"$hostLog`"" -PassThru
Start-Sleep -Seconds $BootWait

Write-Host "== launching client 1 =="
$c1 = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-coopChat", "multi_hi", "-coopReady", "-logFile", "`"$c1Log`"" -PassThru
Start-Sleep -Seconds 12

Write-Host "== launching client 2 =="
$c2 = Start-Process -FilePath $exe -ArgumentList "-coopJoin", "127.0.0.1:$Port", "-coopChat", "multi_hi2", "-coopReady", "-logFile", "`"$c2Log`"" -PassThru
Start-Sleep -Seconds $ClientWait

Write-Host "== host log =="
Wait-Pattern $hostLog "peer=2" $PollSeconds
Wait-Pattern $hostLog "chat P1: multi_hi" $PollSeconds
Wait-Pattern $hostLog "ready peer=1" $PollSeconds
Wait-Pattern $hostLog "ready peer=2" $PollSeconds
Wait-Pattern $hostLog "(2/2)" $PollSeconds
Wait-Pattern $hostLog "force month end" $PollSeconds

Write-Host "== client 1 log (must receive the relayed line from client 2) =="
Wait-Pattern $c1Log "snapshot applied rev=" $PollSeconds
Wait-Pattern $c1Log "chat P2: multi_hi2" $PollSeconds

Write-Host "== client 2 log =="
Wait-Pattern $c2Log "snapshot applied rev=" $PollSeconds

foreach ($p in @($c2, $c1, $hostProc)) {
    try { if ($p -and -not $p.HasExited) { $p | Stop-Process -Force } } catch { }
}

Write-Host "host log:   $hostLog"
Write-Host "client logs: $c1Log / $c2Log"
if ($script:fail -gt 0) {
    Write-Host ("MULTI TEST FAILED ({0} check(s))" -f $script:fail)
    exit 1
}
Write-Host "MULTI TEST PASSED"
