# tools/ — Diagnostics & Acceptance-Test Kit

PowerShell scripts for verifying the OpenWinSidecar streaming stack end-to-end.
Run against a live service on `localhost:28252`. Use `-MTA` (sync-over-async WebSocket calls deadlock in STA):

```powershell
powershell -NoProfile -MTA -ExecutionPolicy Bypass -File tools\<script>.ps1
```

| Script | What it verifies |
|---|---|
| `ws_smoke_test.ps1` | Baseline streaming: connects (JPEG path), counts frames/FPS/bandwidth, checks codec labels and timestamp monotonicity. **Note: predates auth — set no password or extend it with the auth handshake from `ws_auth_test.ps1`.** |
| `ws_auth_test.ps1` | Authentication flow: expects `auth:required`, sends a wrong then the correct `auth:ok` password, measures frames, checks `/input` 403 without `pw=` and 200 with. **Edit the `$Password` default to the machine's actual `EncryptionPassword` (HKLM `SOFTWARE\OpenWinSidecar\Service`).** |
| `ws_wire_test.ps1` | Wire integrity: proper WebSocket message assembly (partial-message aware), distinct-timestamp/duplicate detection, codec counts, true bandwidth. |
| `ws_hevc_validate.ps1` | Hardware HEVC: requests `codec:hevc`, receives the `desc:` hvcC + length-prefixed AU chunks, reconstructs an Annex-B elementary stream, writes `hevc_test.265` for ffprobe. First-chunk bytes must be a 4-byte length (`00 00 00 21…`), not a start code. |
| `enum_vdd_modes.ps1` | Enumerates `EnumDisplayDevices`/`EnumDisplaySettings` for the virtual display (`\\.\DISPLAY8x`): which resolutions/refresh rates the driver actually advertises vs. what `vdd_settings.xml` promises. |

### Lessons encoded in these scripts
- `ClientWebSocket.ReceiveAsync` returns **partial messages** under load — always accumulate until `EndOfMessage`, or you count 16 KB fragments as frames and measure 5× phantom FPS (this false alarm cost us an hour once; don't repeat it).
- ffprobe validation of the reconstructed HEVC stream is the strongest offline check of encoder health without an iPad: expect `hevc / Main / <wxh> / yuv420p`.
- The Console app's `--screenshot <path> [--expand]` flag renders its UI to a PNG headlessly — the UI counterpart of this kit.
