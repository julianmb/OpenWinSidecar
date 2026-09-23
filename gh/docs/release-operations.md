# Release operations

How releases work, and the operational lessons learned shipping v0.2.0 — the
things that cost hours when they went wrong and minutes when documented.

## The release pipeline (what runs on a tag push)

`git tag vX.Y.Z && git push origin vX.Y.Z` triggers `.github/workflows/release.yml`:

1. Read version from `installer/OpenWinSidecar.iss` (`MyAppVersion`).
2. `dotnet test` — gate. A red suite aborts the release.
3. Publish self-contained single-file (`win-x64`).
4. Compile the Inno Setup installer.
5. Optional signing — only when the `SIGNING_CERT_THUMBPRINT` repo secret exists.
6. SHA-256 hash the installer.
7. **Smoke test**: silent-install on the runner, verify registry key, exe,
   `vdd_settings.xml`, firewall rule; then silent-uninstall. Interactive driver
   ops WAIT; silent ones run fire-and-forget (a VM GPU can't start IddCx, and a
   waiting devcon hung two winget validation runs before this was learned).
8. Rewrite `winget/*.yaml` (version, URL, hash) and **commit them to main**.
9. Create/update the GitHub Release — notes = newest `##` CHANGELOG section
   plus a "Verify your download" SHA-256 block.
10. Open the winget-pkgs submission PR (requires the `WINGET_PAT` secret).

Steps 8's commit means **main is authored by CI during releases**. Pull before
cutting a new tag or the push gets rejected as non-fast-forward.

## Lessons from shipping v0.2.0

### 1. Large files block every push (fixed by history rewrite)

`drivers/VDD/control/VDD Control.exe` (163 MB) and `gh/drivers/VDD/vdd_control.zip`
(68 MB) sat in history. GitHub rejects pushes whose pack introduces blobs over
100 MB — and after any history event that forces re-sending objects, *every*
push re-evaluated them. The installer never shipped either file (they're
excluded in the `.iss`), so both were pure bloat.

Fix applied (one-time, 2026-09-21):

```
pip install git-filter-repo
git tag pre-filter-repo-backup           # safety
git filter-repo --path "drivers/VDD/control/VDD Control.exe" --invert-paths --force
git filter-repo --path "gh/drivers/VDD/vdd_control.zip" --invert-paths --force
git filter-repo --path "gh/drivers/VDD/control/VDD Control.exe" --invert-paths --force
git remote add origin <url>              # filter-repo removes origin
git push --force --all origin && git push --force --tags origin
```

Notes: run paths one at a time (the first pass missed the `gh/` mirror copy of
the exe); repo went 68 MB → 1.1 MB. Never re-add either path — if a driver
control GUI is ever needed, ship it as a release asset, not in git.

### 2. winget manifests at repo root are CI-authored

The `winget/*.yaml` files are overwritten and committed to main by the Release
workflow on every release. The local clone predating those commits didn't have
them; force-pushing the cleaned history silently deleted them from the remote,
and the next release failed at "Update winget manifests" (`Get-ChildItem
winget/*.yaml` finding nothing). Restored from the `gh/` mirror — which is kept
in sync precisely because `tools/sync_gh_mirror.ps1` pulls winget/ from the
published repo before mirroring.

Rule: before force-pushing or re-cloning, treat repo-root `winget/`,
`CHANGELOG.md`, and `ios/OpenWinSidecar.swiftpm.zip` as possibly CI-authored.

### 3. Hardware-dependent tests fail only on CI

`HevcKeepAlive_ComposesOnStaticDesktop_AfterFeedInterval` passed locally
(FFmpeg + Intel QSV present) and failed on the runner (neither exists there):
the HEVC push fell back to JPEG, the frame went out, but `_lastHevcFeedTicks`
was never stamped, so the sink treated every static tick as keep-alive-due.
Fix: the JPEG fallback path stamps the feed time too — the frame left either
way, and encoder-equipped machines never hit that branch.

Rule: any test touching the HEVC path must assert *sink-level behavior*
(a compose/send happened), never encoder internals — the runner has no GPU,
no FFmpeg, and no QSV. Debugging CI logs without `gh` installed: the stored
git credential works read-only against the API
(`printf "protocol=https\nhost=github.com\n\n" | git credential fill`), then
`GET /repos/…/actions/runs/{id}/logs`. Job pages and artifacts need auth;
the zip of logs needs only a token with repo read access.

### 4. Silent install still needs UAC

`/SILENT` skips wizard dialogs, not elevation. A headless launch of the setup
exe blocks on the UAC consent dialog (`consent.exe` process visible) and looks
like a hang. Poll for the installer process plus the uninstall registry key;
don't kill the "stuck" installer — it's waiting for a human click.

### 5. Re-tagging a release

Deleting and re-cutting a tag (`git tag -d vX; git push origin :refs/tags/vX;
git tag vX HEAD; git push origin vX`) retriggers the release workflow cleanly.
It's safe to re-run for the same tag — the workflow re-uploads the asset and
edits the existing release (idempotent by design).

## Pre-tag checklist

1. Version bumped in BOTH `src/OpenWinSidecar/OpenWinSidecar.csproj` and
   `installer/OpenWinSidecar.iss` (`VersionConsistencyTests` enforce this).
2. Newest `##` CHANGELOG section written — it becomes the release notes.
3. `dotnet test` locally, clean.
4. `git pull origin main --ff-only` first — CI may have authored commits.
5. Tag on the exact commit you tested; CI green on that commit.
