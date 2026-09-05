# Sync gh/ distribution mirror from src/

# The gh/ folder is a GitHub-facing mirror of the source tree. It must match src/
# or the two drift apart (both build independently). Run this after editing src/.

$root = Split-Path -Parent $PSScriptRoot   # repo root (tools/ parent)

Write-Output "Mirroring $root\src -> $root\gh\src ..."
robocopy "$root\src" "$root\gh\src" /MIR /NFL /NDL /NJH /NJS /NP | Select-Object -Last 5
Write-Output "Mirror complete. Remember to commit both trees."
