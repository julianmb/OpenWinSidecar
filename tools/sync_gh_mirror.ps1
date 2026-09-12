# Sync gh/ distribution mirror from the working tree.
# (installer/stage, installer/output, bin/, obj/ are build artifacts and stay local.)

$root = Split-Path -Parent $PSScriptRoot   # repo root (tools/ parent)

Write-Output "Mirroring $root\src -> $root\gh\src ..."
robocopy "$root\src" "$root\gh\src" /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Select-Object -Last 5

Write-Output "Mirroring $root\tests -> $root\gh\tests ..."
robocopy "$root\tests" "$root\gh\tests" /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Select-Object -Last 5

Write-Output "Mirroring $root\ios\OpenWinSidecarClient -> $root\gh\ios\OpenWinSidecarClient ..."
robocopy "$root\ios\OpenWinSidecarClient" "$root\gh\ios\OpenWinSidecarClient" /MIR /NFL /NDL /NJH /NJS /NP | Select-Object -Last 5

Write-Output "Mirroring $root\installer (script only) -> $root\gh\installer ..."
robocopy "$root\installer" "$root\gh\installer" OpenWinSidecar.iss /NFL /NDL /NJH /NJS /NP | Select-Object -Last 3

foreach ($f in @("CHANGELOG.md", "docs\future-work.md")) {
    Copy-Item (Join-Path $root $f) -Destination (Join-Path "$root\gh" $f) -Force
    Write-Output "Copied $f"
}
Write-Output "Mirror complete. Remember to commit both trees."
