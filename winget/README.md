# Publishing OpenWinSidecar to winget

These manifests are the submission source of truth — **and they are maintained by CI**:
the [Release workflow](../.github/workflows/release.yml) rewrites `PackageVersion`,
`InstallerUrl`, and `InstallerSha256` on every tag push (so they always describe the
attached release asset). Don't edit them by hand.

## Cutting a release

1. Bump `MyAppVersion` in `installer/OpenWinSidecar.iss` (the single version source) and
   write the CHANGELOG section (it becomes the release notes).
2. Test locally, commit, then tag and push the tag:
   ```powershell
   git tag v0.1.2
   git push origin v0.1.2
   ```
3. The workflow builds, tests, compiles the installer, **smoke-tests the silent install
   on the runner** (catches hangs/dialogs before shipping), publishes the GitHub Release
   (asset + hash), updates these three files, and **opens the winget-pkgs PR
   automatically**. When it's green, the release is live and the submission is in review.

## winget-pkgs PR automation

The winget-pkgs submission PR is opened automatically by the release workflow using a
PAT secret named **`WINGET_PAT`**. To set it up (one-time):

1. Create a fine-grained PAT at
   [GitHub Settings → Developer settings → Personal access tokens](https://github.com/settings/personal-access-tokens/new):
   - **Resource owner:** your account (`julianmb`)
   - **Repository access:** `Public Repositories` (or select `microsoft/winget-pkgs` if you've forked it)
   - **Permissions → Repository permissions:**
     - `Contents: Read and Write` (to push to your fork)
     - `Pull requests: Write` (to open the PR to microsoft/winget-pkgs)
2. Add it as a repository secret named `WINGET_PAT` in
   [julianmb/OpenWinSidecar → Settings → Secrets and variables → Actions](https://github.com/julianmb/OpenWinSidecar/settings/secrets/actions).

Without `WINGET_PAT`, the release still publishes — the winget PR is skipped with a
warning in the Actions job summary. The manifests are still committed to `main`, so you
can open the PR manually (see below).

## Manual fallback (if `WINGET_PAT` is not set)

1. Validate locally if the winget client is installed — copy the three `.yaml` files to a
   temp folder **without** this README (winget tries to parse every file in the folder):
   `winget validate --manifest <temp-folder>`.
2. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), copy the three
   files to `manifests/j/julianmb/OpenWinSidecar/<ver>/`, and open a PR titled
   `New package: julianmb.OpenWinSidecar version <ver>`. wingetbot validates
   automatically; sign the CLA when the policy bot asks.
