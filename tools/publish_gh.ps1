# Publish the gh/ mirror to github.com/julianmb/OpenWinSidecar.
#
# The dev repo (this tree, including the gh/ subdirectory) is local-only. The published
# repo has its own commit history built from the gh/ content. This script does the whole
# dance in one command: clone the published repo into a temp dir, overlay gh/ on top
# (preserving .git), commit, push, clean up.
#
# Usage:
#   powershell tools/publish_gh.ps1                          # uses a default commit message
#   powershell tools/publish_gh.ps1 -Message "My change"     # explicit message
#
# Steps before running: edit sources, run tools/sync_gh_mirror.ps1, commit the dev repo.
# This script does NOT touch the dev repo — it only publishes gh/ as it currently stands.

param(
    [string]$Message = "Publish gh/ mirror"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (tools/ parent)
$gh = Join-Path $root "gh"
$tmp = Join-Path $root "owi_publish_tmp"

if (-not (Test-Path (Join-Path $gh "OpenWinSidecar.slnx"))) {
    throw "gh/ mirror looks wrong (no OpenWinSidecar.slnx) — run tools/sync_gh_mirror.ps1 first"
}

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

Write-Output "Cloning published repo ..."
git clone --depth 5 https://github.com/julianmb/OpenWinSidecar.git $tmp
if ($LASTEXITCODE -ne 0) { throw "git clone failed" }

Write-Output "Overlaying gh/ -> temp clone ..."
# /E (not /MIR): copy everything over, but never delete the clone's .git directory.
robocopy $gh $tmp /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

Push-Location $tmp
try {
    git add -A
    if ($LASTEXITCODE -ne 0) { throw "git add failed" }

    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Output "Published repo already up to date — nothing to push."
    } else {
        git commit -m $Message
        if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
        git push origin main
        if ($LASTEXITCODE -ne 0) { throw "git push failed" }
        Write-Output "Published: $(git rev-parse --short HEAD) -> github.com/julianmb/OpenWinSidecar main"
    }
}
finally {
    Pop-Location
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
