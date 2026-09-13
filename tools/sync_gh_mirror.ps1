# Sync gh/ distribution mirror from the working tree.
# (installer/stage, installer/output, bin/, obj/ are build artifacts and stay local.)

$root = Split-Path -Parent $PSScriptRoot   # repo root (tools/ parent)

# The published repo's winget/ manifests are maintained by the Release workflow
# (version, InstallerUrl, InstallerSha256 rewritten on every tag push). Pull them
# down FIRST so mirroring never reverts CI-authored values. winget/ is not a
# local-edit file anymore — see gh/winget/README.md.
Write-Output "Pulling winget manifests from the published repo (CI maintains them) ..."
$wingetBase = "https://raw.githubusercontent.com/julianmb/OpenWinSidecar/main/winget"
foreach ($f in @(
    "julianmb.OpenWinSidecar.yaml",
    "julianmb.OpenWinSidecar.installer.yaml",
    "julianmb.OpenWinSidecar.locale.en-US.yaml")) {
    try {
        Invoke-WebRequest "$wingetBase/$f" -OutFile (Join-Path "$root\gh\winget" $f) -UseBasicParsing
        Write-Output "  pulled $f"
    } catch {
        Write-Warning "  could not pull $f (offline?) - keeping the local copy"
    }
}

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
