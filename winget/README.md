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
   git tag v0.1.1
   git push origin v0.1.1
   ```
3. The workflow builds, tests, compiles the installer, publishes the GitHub Release
   (asset + hash), and updates these three files. When it's green, the release is live.

## Submitting to winget-pkgs (the one manual step)

1. Validate locally if the winget client is installed — copy the three `.yaml` files to a
   temp folder **without** this README (winget tries to parse every file in the folder):
   `winget validate --manifest <temp-folder>`.
2. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), copy the three
   files to `manifests/j/julianmb/OpenWinSidecar/<ver>/`, and open a PR titled
   `New package: julianmb.OpenWinSidecar version <ver>` (or add the folder to your
   existing PR for a version bump). wingetbot validates automatically; sign the CLA when
   the policy bot asks.
