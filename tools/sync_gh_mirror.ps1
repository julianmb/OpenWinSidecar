# Sync gh/ distribution mirror from src/ and installer/.
# (installer/stage and installer/output are build artifacts and stay local.)

$root = Split-Path -Parent $PSScriptRoot   # repo root (tools/ parent)

Write-Output "Mirroring $root\src -> $root\gh\src ..."
robocopy "$root\src" "$root\gh\src" /MIR /NFL /NDL /NJH /NJS /NP | Select-Object -Last 5

Write-Output "Mirroring $root\installer (script only) -> $root\gh\installer ..."
robocopy "$root\installer" "$root\gh\installer" OpenWinSidecar.iss /NFL /NDL /NJH /NJS /NP | Select-Object -Last 3
Write-Output "Mirror complete. Remember to commit both trees."
