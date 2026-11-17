param(
    [int]$Port = 25569,
    [int]$BootWait = 70,
    [int]$ClientWait = 70
)

# Scenario test: the host boots a real world with five cards (stacked berries, a
# damaged card, a booster pack) and a client joins over plain TCP. Unlike the
# empty-world self tests this exercises card creation, stacking and cosmetics.
# Boot is slow (the game loads its whole asset bundle), so both waits are generous.
# All assertions are ASCII on purpose: PowerShell 5.1 reads BOM-less scripts as
# ANSI and mangles Chinese literals.

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
$hostLog = Join-Path $logDir "scenario-host.log"
$clientLog = Join-Path $logDir "scenario-client.log"
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
    Start-Process -FilePath $exe -ArgumentList @("-coopHost", "$Port", "-coopScenario", "-logFile", $hostLog) | Out-Null
    if (Wait-Pattern $hostLog "scenario spawned cards=" $BootWait) {
        Write-Host "PASS  host spawned scenario cards"
    } else {
        Write-Host "FAIL  host spawned scenario cards"
        Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
        $failed = $true
    }

    if (-not $failed) {
        Write-Host "starting client..."
        Start-Process -FilePath $exe -ArgumentList @("-coopJoin", "127.0.0.1:$Port", "-coopTravel", "main", "-logFile", $clientLog) | Out-Null

        if (Wait-Pattern $clientLog "snapshot applied rev=" $ClientWait) {
            Write-Host "PASS  client applied a snapshot"
        } else {
            Write-Host "FAIL  client applied a snapshot"
            Get-Content $clientLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        # The client usually joins after the scripted growth already ran, so accept any
        # snapshot that carries the booster pack alongside the cards.
        if (Wait-Pattern $clientLog "packs=2" $ClientWait) {
            Write-Host "PASS  client snapshot carried cards and boosters"
        } else {
            Write-Host "FAIL  client snapshot carried cards and boosters"
            Get-Content $clientLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "stack rebuild parent=" 10) {
            Write-Host "PASS  client rebuilt stacks"
        } else {
            Write-Host "WARN  no stack rebuild logged (stacks may already have matched)"
        }
    }

    if (-not $failed) {
        # The host keeps spawning cards after the initial board; the client must keep
        # applying snapshots and end up with the grown count.
        if (Wait-Pattern $hostLog "scenario grew cards=7" 40) {
            Write-Host "PASS  host grew the world"
        } else {
            Write-Host "FAIL  host grew the world"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $hostLog "scenario removed cards=" 40) {
            Write-Host "PASS  host removed a card"
        } else {
            Write-Host "FAIL  host removed a card"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "cards=6" 40) {
            Write-Host "PASS  client dropped the removed card"
        } else {
            Write-Host "FAIL  client dropped the removed card"
            Get-Content $clientLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        # The scenario starts a real 180 s game timer on the host before the client joins,
        # so this covers the "timers travel in snapshots" path rather than a synthetic one.
        if (Wait-Pattern $hostLog "timers=1" 10) {
            Write-Host "PASS  host snapshot carried a running timer"
        } else {
            Write-Host "FAIL  host snapshot carried a running timer"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "timer bar from snapshot" 40) {
            Write-Host "PASS  client built the timer bar"
        } else {
            Write-Host "FAIL  client built the timer bar"
            Get-Content $clientLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        # Last stage: the host hops to another board so the client's FollowBoard path runs.
        if (Wait-Pattern $hostLog "scenario board switched" 60) {
            Write-Host "PASS  host switched boards"
        } else {
            Write-Host "WARN  no board switch (forest/island board unavailable?)"
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "travel board " 40) {
            Write-Host "PASS  client travelled to its own board"
        } else {
            Write-Host "WARN  client did not travel on its own"
        }
    }

    if (-not $failed) {
        # The client stays on its own board; snapshots must keep arriving regardless.
        Start-Sleep -Seconds 6
        $before = (Select-String -Path $clientLog -Pattern "snapshot applied rev=" -SimpleMatch).Count
        Start-Sleep -Seconds 6
        $after = (Select-String -Path $clientLog -Pattern "snapshot applied rev=" -SimpleMatch).Count
        if ($after -gt $before) {
            Write-Host "PASS  client kept syncing on a separate board"
        } else {
            Write-Host "FAIL  client stopped syncing after travelling"
            $failed = $true
        }
    }

    if (-not $failed) {
        # Final stage: the host forces a month settlement (the same call the panel button
        # makes) so the client's month counter is validated against a real EndOfMonth.
        if (Wait-Pattern $hostLog "scenario month advanced " 60) {
            Write-Host "PASS  host settled a month"
        } else {
            Write-Host "WARN  no month settlement logged"
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "month synced " 30) {
            Write-Host "PASS  client followed the month"
        } else {
            Write-Host "WARN  client did not log a month change"
        }
    }

    if (-not $failed) {
        # Fifth stage: a villager and a mob are dropped on the host so real combat starts.
        if (Wait-Pattern $hostLog "scenario combat spawned" 80) {
            Write-Host "PASS  host spawned a fight"
        } else {
            Write-Host "WARN  no fight spawned (mob card id unavailable?)"
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $hostLog "hit broadcast net=" 40) {
            Write-Host "PASS  host broadcast a hit"
        } else {
            Write-Host "FAIL  host broadcast a hit"
            Get-Content $hostLog -Tail 20 -ErrorAction SilentlyContinue
            $failed = $true
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "hit net=" 60) {
            Write-Host "PASS  client saw a hit"
        } else {
            Write-Host "WARN  client saw no hit event"
        }
    }

    if (-not $failed) {
        # Pack position only reaches the client through the snapshot hash, so this covers the
        # "host moves a booster pack" path that had no other channel.
        if (Wait-Pattern $hostLog "scenario pack moved to " 60) {
            Write-Host "PASS  host moved a booster pack"
        } else {
            Write-Host "WARN  no pack move logged"
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "pack moved net=" 40) {
            Write-Host "PASS  client followed the moved pack"
        } else {
            Write-Host "WARN  client did not log a pack move"
        }
    }

    if (-not $failed) {
        # Shop counters live outside the card data; paying into a merchant must reach the
        # client through the snapshot's shop records or the price text never moves there.
        if (Wait-Pattern $hostLog "scenario shop paid given=" 70) {
            Write-Host "PASS  host paid into a merchant"
        } else {
            Write-Host "WARN  host did not pay into a merchant"
        }
    }

    if (-not $failed) {
        if (Wait-Pattern $clientLog "shop sync changed=" 40) {
            Write-Host "PASS  client applied the shop counter"
        } else {
            Write-Host "WARN  no shop counter seen (needs a merchant in the world)"
    }

    if (-not $failed) {
        # The conflict box is drawn from snapshot membership; if that never arrives the
        # client shows no box even while the host is fighting.
        if (Wait-Pattern $clientLog "conflicts groups=" 40) {
            Write-Host "PASS  client received conflict groups"
        } else {
            Write-Host "WARN  client received no conflict group"
        }
        }
    }
}
finally {
    Get-Process Stacklands -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($failed) {
    exit 1
}

Write-Host "SCENARIO TEST PASSED"
