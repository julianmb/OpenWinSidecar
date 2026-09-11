# Publishing OpenWinSidecar to winget

These manifests are the submission source of truth. To publish a new version:

1. Build the installer (`installer/OpenWinSidecar.iss` via Inno Setup) and attach
   `OpenWinSidecar-Setup-<ver>.exe` to the matching GitHub Release.
2. Fill in the real values in `julianmb.OpenWinSidecar.installer.yaml`:
   - `InstallerUrl` must match the release asset URL exactly
   - `InstallerSha256`: `Get-FileHash installer/output/OpenWinSidecar-Setup-<ver>.exe -Algorithm SHA256`
   - bump `PackageVersion` in all three files for a new release
3. Validate locally if the winget client is installed:
   `winget validate --manifest <file>` for each file.
4. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), copy these
   three files to `manifests/j/julianmb/OpenWinSidecar/<ver>/`, open a PR.
   wingetbot validates automatically; fix whatever it flags and users get
   `winget install julianmb.OpenWinSidecar`.
