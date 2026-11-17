# Shared helper: locate the Stacklands installation on this machine.
#
# Resolution order: explicit -GameDir, STACKLANDS_DIR, STACKLANDS_EXE (its parent),
# then the usual Steam locations on every local drive. A candidate only counts when
# the game's managed assemblies are actually there, so a stale path never wins.
#
# Dot-source it, then call:  $dir = Resolve-StacklandsDir
# Returns "" when nothing is found; callers decide how to report that.

function Resolve-StacklandsDir {
    param([string]$GameDir = "")

    $candidates = New-Object System.Collections.Generic.List[string]
    if ($GameDir) { $candidates.Add($GameDir) }
    if ($env:STACKLANDS_DIR) { $candidates.Add($env:STACKLANDS_DIR) }
    if ($env:STACKLANDS_EXE) { $candidates.Add((Split-Path -Parent $env:STACKLANDS_EXE)) }

    foreach ($drive in @("C:", "D:", "E:", "F:", "G:", "H:")) {
        if (-not (Test-Path ($drive + "\"))) { continue }
        $candidates.Add((Join-Path $drive "Program Files (x86)\Steam\steamapps\common\Stacklands"))
        $candidates.Add((Join-Path $drive "SteamLibrary\steamapps\common\Stacklands"))
        $candidates.Add((Join-Path $drive "Steam\steamapps\common\Stacklands"))
    }

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $managed = Join-Path $candidate "Stacklands_Data\Managed\GameScripts.dll"
        if (Test-Path $managed) {
            return (Resolve-Path $candidate).Path
        }
    }

    return ""
}
