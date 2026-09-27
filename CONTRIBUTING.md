# Contributing

Thanks for looking. Issues and pull requests are welcome, and reports from hardware I do not
have are especially valuable — I test on one machine (Windows 11, Intel GPU, iPad Air) and
cannot cover the space alone.

## Reporting something that does not work

The single most useful thing you can include is the viewer's own telemetry. Open the browser
devtools console on the iPad (or Safari on the desktop) and look for the `[Stats]` line, which
is posted every five seconds and contains the active codec, frame rates, decode-queue depth,
latency and the renderer state. Paste that line and the problem is usually obvious.

Also useful: the app's log file at
`%LOCALAPPDATA%\OpenWinSidecar\logs\sidecar.log` (the **Log** panel has a *Save log…* button).

## Before you open a pull request

```powershell
dotnet build OpenWinSidecar.slnx
dotnet test tests/OpenWinSidecar.Service.Tests/OpenWinSidecar.Service.Tests.csproj
node --test tests/viewer.runtime.test.cjs
```

All three run in CI. The viewer suite executes the real `index.html` in a sandboxed VM with a
mocked WebSocket and codec, so changes to the render loop, decode queue or input mapping are
covered without a device.

Two rules that have bitten this project before:

- **Measure, don't assume.** Server-received fps is not presented fps; the panel decides. If you
  claim a performance number, say what was measured and how.
- **Don't claim features that don't exist.** A README line about a gesture or a latency figure
  that nobody verified is worse than no line at all.

## Cutting a release

The [Release workflow](.github/workflows/release.yml) automates everything after the tag. The
version lives in `src/OpenWinSidecar/OpenWinSidecar.csproj` (`<Version>`, which stamps the app
assembly and the dashboard title) and in `installer/OpenWinSidecar.iss` (`MyAppVersion`, which
drives the installer) — `VersionConsistencyTests` fails the build if they drift apart. The
`winget/` manifests carry the previous release's version until the workflow rewrites them.

1. Bump both versions (e.g. `"0.3.0"`) and write the CHANGELOG section. The section whose
   heading mentions the tag becomes the release notes automatically.
2. Verify locally: the three commands above, plus one elevated install/uninstall cycle of a
   locally compiled installer.
3. Commit, then tag:
   ```powershell
   git tag v0.3.0
   git push origin v0.3.0
   ```
4. The workflow runs the tests, publishes the self-contained single file, compiles the installer
   with Inno Setup, **smoke-tests the silent install on the runner** (this is what caught the
   winget-pkgs validation failures — see the CHANGELOG), creates the GitHub Release with the
   asset and its SHA-256, rewrites the `winget/` manifests, and **opens the winget-pkgs
   submission PR automatically** (needs the `WINGET_PAT` secret — see [winget/README.md](winget/README.md)).
5. The tag and the version must agree; the workflow fails early and tells you which one to
   change if they do not.
6. Re-running for the same tag is mostly idempotent — it re-uploads the asset, re-runs the smoke
   test and updates the existing winget PR. Note that it rebuilds the installer, so the SHA-256
   changes and any already-merged winget manifest becomes stale.

## 🔏 Code signing

The installer is **not** code-signed, so SmartScreen may warn. The signing step activates
automatically once a `SIGNING_CERT_THUMBPRINT` secret is configured, and the release notes
publish the hash either way:

```powershell
Get-FileHash .\OpenWinSidecar-Setup-0.3.0.exe -Algorithm SHA256
```

## Security

Please do not open a public issue for a security problem. See [SECURITY.md](SECURITY.md) for
how to report one and for the threat model — in particular, the server is unauthenticated
unless you set an access password.

## License

By contributing you agree that your contribution is licensed under the
[AGPL-3.0](LICENSE), the same terms as the project.
