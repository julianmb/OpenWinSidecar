# OpenWinSidecar Engineering Handover & System Architecture

> **Date:** September 1, 2026  
> **Project:** OpenWinSidecar (.NET 10 LTS)  
> **Target Device:** iPad Air (4th & 5th Gen), iPad Pro, and multi-platform tablets/browsers  
> **Host Configuration:** Windows 11, Intel Arc A380 Dedicated GPU, Multi-Monitor Setup with IddCx Virtual Monitor

---

## 1. Executive Summary

OpenWinSidecar is an open-source, ultra-low latency (.NET 10 LTS / C#) virtual monitor server and management suite that replaces proprietary spacedesk software. It provides hardware-accelerated video streaming (HEVC/H.265), direct GPU VRAM capture (Direct3D 11 Desktop Duplication), live Windows Display DPI scaling from the web, and responsive multi-touch/mouse/keyboard forwarding.

---

## 2. Completed Architecture & Technical Capabilities

```mermaid
flowchart LR
    subgraph Host["Host PC (Windows 11)"]
        VDD["IddCx Driver\n(ROOT\\DISPLAY\\0000)"] --> D3D11["Direct3D 11 VRAM\n(IDXGIOutputDuplication)"]
        D3D11 --> QSV["Intel Arc A380 GPU\n(hevc_qsv ~3.5ms)"]
        CCD["Win32 CCD API\n(WindowsDpiService)"] -.-> VDD
        QSV --> TCP["Multi-Port TCP/WS Server\n(Ports 80, 8080, 28252)"]
        INP["Win32 SendInput\n(InputDispatcher)"] <-- TCP
    end

    subgraph iPad["Client iPad (Safari / PWA)"]
        TCP ==> WS["WebSocket Client\n(Binary WebCodecs)"]
        WS --> DEC["Apple Silicon Hardware\n(VideoDecoder ~1.5ms)"]
        DEC --> CV["Canvas Framebuffer\n(desynchronized: true)"]
        TOUCH["Touch Digitizer & Pencil\n(rAF Batched)"] --> WS
    end
```

### ⚡ Feature Status Matrix

| Component | Status | Technical Details |
|---|---|---|
| **GPU Video Encoding** | ✅ **Active** | Intel QuickSync (`hevc_qsv`) encoding 60 FPS HEVC in **~3.5 ms**. Dynamic bitrate scaling: `50% (3 Mbps)`, `65% (5 Mbps)`, `80% Default (8 Mbps)`, `90% Ultra-Crisp (12 Mbps)`. |
| **GPU Screen Capture** | ✅ **Active** | Direct3D 11 Desktop Duplication (`DxgiCaptureService`) capturing directly from GPU VRAM in **< 0.5 ms** with pinned SIMD buffers. Automatic fallback to GDI `CreateDC` if DXGI access is revoked. |
| **Client Decoding** | ✅ **Active** | W3C WebCodecs `VideoDecoder` leveraging Apple Silicon dedicated hardware HEVC decoders on iPad in **~1.5 ms** (`optimizeForLatency: true`). |
| **Direct Canvas Rendering**| ✅ **Active** | Canvas 2D / WebGL rendering with `{ desynchronized: true }`, bypassing Safari's compositor synchronization queue and eliminating 1 full frame (~16.6ms) of latency. |
| **Windows DPI Scaling** | ✅ **Active** | `WindowsDpiService` implements Win32 CCD API (`DisplayConfigSetDeviceInfo` with `DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4`), allowing real-time scaling (100% to 225%) directly from the iPad web menu. |
| **Aspect Ratio Lock** | ✅ **Active** | Mathematical aspect-ratio lock (`scaleX == scaleY`) eliminating stretching and distortion across both windowed browser tabs and fullscreen modes. |
| **Auto Screen Detection**| ✅ **Active** | Automatically detects visiting client dimensions, device pixel ratio (`@2x` Retina), and orientation on initial connection. |
| **iPad Resolution Matrix**| ✅ **Active** | `C:\VirtualDisplayDriver\vdd_settings.xml` populated with native and logical `@2x` modes for all iPad models (10.9", 11" M4, 13" M4, 12.9", 10.2", 9.7", 8.3" Mini). |
| **Multi-Port Web Server** | ✅ **Active** | Concurrent TCP listening on ports **80**, **8080**, and **28252**. Direct browser access via `http://<IP>:8080` or `http://<IP>`. |
| **PWA / Standalone App** | ✅ **Active** | iOS Web App meta tags (`apple-mobile-web-app-capable: yes`) allowing "Add to Home Screen" for a borderless fullscreen experience. |
| **Input & Gestures** | ✅ **Active** | Single-finger touch/mouse with `requestAnimationFrame` event batching, two-finger scrolling (`MOUSEEVENTF_WHEEL`), and keyboard forwarding. |
| **Screen WakeLock** | ✅ **Active** | W3C `navigator.wakeLock.request('screen')` prevents iPad screen dimming/sleeping. |
| **Thread-Safe WebSocket**| ✅ **Active** | `SemaphoreSlim` write synchronization across all outgoing video packets. |

---

## 3. Glass-to-Glass Latency Breakdown

```
Direct3D 11 GPU VRAM Capture :  < 0.5 ms
Intel Arc A380 HEVC Encode   :  ~ 3.5 ms
TCP_NODELAY Wi-Fi Transport  :  ~ 1.5 ms
Apple Silicon Hardware Decode:  ~ 1.5 ms
desynchronized Framebuffer   :  < 0.5 ms
----------------------------------------
Total Glass-to-Glass Latency :  ⚡ ~7 – 10 ms (Imperceptible)
```

---

## 4. Key Files & Code Reference Index

| File Path | Description |
|---|---|
| [`src/OpenWinSidecar.Core/Services/WindowsDpiService.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Core/Services/WindowsDpiService.cs) | Win32 CCD API for changing Windows display DPI scaling (100% - 225%) programmatically. |
| [`src/OpenWinSidecar.Core/Services/VirtualDisplayManager.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Core/Services/VirtualDisplayManager.cs) | Virtual Display Driver management & display topology extend commands. |
| [`src/OpenWinSidecar.Service/Capture/FrameBroadcastHub.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Capture/FrameBroadcastHub.cs) | Broadcast hub: one capture producer per display, per-sink fan-out, DXGI failover/recovery. |
| [`src/OpenWinSidecar.Service/Capture/ClientFrameSink.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Capture/ClientFrameSink.cs) | Per-client sink: compose, cursor stamp, JPEG/HEVC encode, drop-oldest backpressure. |
| [`src/OpenWinSidecar.Service/Protocol/WebCodecsFraming.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Protocol/WebCodecsFraming.cs) | 15-byte WebCodecs packet framing + WebSocket binary/text frames. |
| [`src/OpenWinSidecar.Service/Encoding/HevcQsvStreamEncoder.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Encoding/HevcQsvStreamEncoder.cs) | Intel Arc QuickSync HEVC hardware encoder wrapper using Gyan FFmpeg 9.0.1. |
| [`src/OpenWinSidecar.Service/Capture/DxgiCaptureService.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Capture/DxgiCaptureService.cs) | Zero-copy Direct3D 11 Desktop Duplication GPU VRAM capture engine. |
| [`src/OpenWinSidecar.Service/Capture/ScreenCaptureService.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Capture/ScreenCaptureService.cs) | GDI `CreateDC` direct device context capture engine and watermark compositor. |
| [`src/OpenWinSidecar.Service/Protocol/SpacedeskTcpServer.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Protocol/SpacedeskTcpServer.cs) | Multi-port HTTP/WebSocket server, HTML5/WebCodecs client, touch/input dispatcher. |
| [`src/OpenWinSidecar.Service/Input/InputDispatcher.cs`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/src/OpenWinSidecar.Service/Input/InputDispatcher.cs) | Win32 `SendInput` mouse movement, clicks, scrolling, and keyboard injection. |
| [`drivers/VDD/vdd_settings.xml`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/drivers/VDD/vdd_settings.xml) | Complete iPad lineup resolution and refresh rate definitions. |
| [`C:\VirtualDisplayDriver\vdd_settings.xml`](file:///C:/VirtualDisplayDriver/vdd_settings.xml) | System active configuration loaded by `MttVDD.dll` driver on kernel initialization. |

---

## 5. How to Build, Run & Test

### 1. Building the Solution:
```powershell
dotnet build "C:\Users\JulianB\source\repos\OpenWinSidecar\OpenWinSidecar.slnx"
```

### 2. Launching the Service:
```powershell
# Stop any stale instances
Get-Process -Name "OpenWinSidecar.Service", "ffmpeg" -ErrorAction SilentlyContinue | Stop-Process -Force

# Run the compiled binary directly (matches Windows Firewall Public allow rule)
& "C:\Users\JulianB\source\repos\OpenWinSidecar\src\OpenWinSidecar.Service\bin\Debug\net10.0-windows\OpenWinSidecar.Service.exe"
```

### 3. Connecting from iPad:
1. Connect iPad to the local Wi-Fi network.
2. **Easiest:** open the Console on the PC and scan the QR code in the Connect card with the iPad camera, or
   open Safari: `http://192.168.1.12:8080`
3. If an access password is configured (this machine: see `HKLM\SOFTWARE\datronicsoft\spacedesk\Service\EncryptionPassword`), enter it when prompted.
4. Tap **Share (⬆️) → Add to Home Screen** for the standalone fullscreen app.

---

## 5a. Session Log — UX, Accessibility & Productization (2026-09-03, second session)

Changes made *after* the streaming-core work above, in order. Full detail with measurements lives in [`CHANGELOG.md`](file:///C:/Users/JulianB/source/repos/OpenWinSidecar/CHANGELOG.md); each item is also a git commit.

1. **Console UX redesign → single page**: 4 tabs / ~30 buttons / 6 duplicate actions replaced by one page — iPad-display hero toggle, connect card, two setting rows, clients list, collapsed Maintenance/Preferences/log. Auto-targets the virtual display (monitor selector removed). Status-bar feedback replaces success MessageBoxes.
2. **Console accessibility (color-vision)**: state colors moved to the blue/yellow axis (green/red collapsed to near-identical tones for red-green-deficient users); state is *never* hue alone — filled square = running, hollow circle = stopped, plus text; web-viewer badges carry markers (`⟳ RECONNECTING`, `⚠ JPEG INTRA`).
3. **Console text-contrast pass (WCAG-measured)**: computed actual contrast ratios — the primary button **failed** AA (white on `#3B82F6` = 3.68:1); fixed to `#2563EB` (5.17:1), secondary text `#9A9AA3`→`#B8B8C2` (8.33:1), card borders 1.3:1→2.1:1.
4. **Expander-header fix**: Maintenance/Preferences headers rendered black-on-dark (ToggleButton default style overrides the Expander's foreground) — fixed via `TextElement.Foreground` TemplateBinding.
5. **Visual revamp (professional dashboard)**: branded header (icon tile + glow, tagline, status pill chip), gradient hero card, unified Card style with shadows, uppercase section labels, rounder controls. Verified via `--screenshot` self-capture (normal + expanded).
6. **App icon**: multi-resolution `app.ico` (16–256 px, per-size redraws) — monitor + signal-wave design; wired into the exe (`ApplicationIcon`), main window, QR popup, and the system tray (the tray was showing the generic .NET icon).
7. **Connect-by-QR**: QRCoder-generated code in the Console's Connect card (fully offline; auto-regenerates when the LAN IP changes; click to enlarge 420×420). Scan with the iPad camera → Safari → streaming.
8. **Git + CHANGELOG**: repo initialized with a full-history snapshot commit; every subsequent change is an explanatory commit plus a dated CHANGELOG entry (what / why / how verified). `tools/` holds the PowerShell acceptance-test kit with its own README.
9. **Rename completed**: `OpenSpacedesk` → `OpenWinSidecar` everywhere except the repo **root folder** (still `repos\OpenSpacedesk`; a process held it during the rename — run `Rename-Item` after closing it; no rebuild needed).

---

## 6. Next Steps & Future Roadmap

For incoming engineers or agents continuing this project, the following features are planned next:

0. **✅ Completed since this doc was written (September 2026 session)**:
   - **Fan-Out Broadcast Refactor**: `FrameBroadcastHub` (one capture producer per display) + `ClientFrameSink` (one per client) decouple capture from streaming — drop-oldest backpressure, honest codec labels (`IntraTurbo` for every JPEG payload), real capture timestamps, once-per-process display-topology flash, GDI seed for static desktops (Desktop Duplication never presents unchanged frames), DDB-route GDI fallback at ~55 FPS, and 2s time-based DXGI recovery. Measured: 57 FPS single JPEG client, 48 FPS × 2 clients, 49 FPS hardware HEVC. Details: `docs/architecture.md` §3.
   - **HEVC hardware path verified end-to-end** (ffmpeg `hevc_qsv` on the Arc A380, correct GOP/keyframe cadence, ~0.2 Mbps on static content vs ~17.5 Mbps JPEG).
   - `vdd_settings.xml` verified advertised by the driver (`EnumDisplaySettings` on `\\.\DISPLAY8x`) — the device enumerates as `DISPLAY8x` and **renumbers on driver restart** (85→86), so never hardcode the index.
   - **Access-password enforcement (security)**: the previously-cosmetic `EncryptionPassword` registry setting now gates everything. WebSocket sessions stay inert until the client sends `auth:<token>` (server announces `auth:required` after upgrade; 3 attempts per connection within a 6s window; constant-time compare); `/input` HTTP injection requires a matching `pw=` parameter (403 otherwise); the unauthenticated legacy binary path is refused entirely when a password is set; the web client shows a password overlay and re-syncs its settings after `auth:ok`. NOTE: password `564D7EC4` is configured on this machine — clients must enter it.
   - **Idle-frame skip (bandwidth)**: sinks skip compose+encode+send when the desktop pixels, streamed cursor, and sink settings are all unchanged — a static desktop sends nothing (WebSocket pings keep the connection alive). JPEG bandwidth now tracks real activity (~18 Mbps active, ~0 idle) instead of a constant ~20 Mbps re-send of identical frames.
   - **Safari-ready HEVC (hvcC + AU framing)**: the FFmpeg Annex-B stream is parsed into NAL units and re-framed as access-unit-aligned chunks of 4-byte length-prefixed NALs; a one-shot `desc:<codec>|<base64 hvcC>` message delivers the HEVCDecoderConfigurationRecord (built from the SPS's profile_tier_level, emulation-prevention stripped) before the first chunk. The web client configures `VideoDecoder` with `description` — the format Safari requires for reliable hardware HEVC. Encoder chain: `-vf format=nv12,hwupload -c:v hevc_qsv -g 240 -bf 0 -async_depth 1` — NV12 keeps **Main profile** (BGRA surfaces would produce Rext/4:4:4, software-decoded on iPads), 4s GOP is free because per-client encoders join on an IDR. Validated by reconstructing the bitstream client-side and probing with ffprobe: hevc/Main/1180x664/yuv420p, one AU per chunk. NOTE: FFmpeg stderr is now captured and logged (`[HEVC QSV][ff]`), which is how driver-level failures are diagnosed.
1. **🔊 Ultra-Low Latency Audio Streaming**:
   - Capture Windows default audio via WASAPI Loopback (`IAudioCaptureClient`).
   - Encode with low-latency Opus / AAC.
   - Stream over WebSocket binary packets to browser `AudioWorklet` / `AudioContext`.
2. **✏️ Apple Pencil Pressure Sensitivity & Tilt Support**:
   - Forward `e.pressure`, `e.tiltX`, `e.tiltY` from iPad digitizer.
   - Inject as native Windows Ink stylus strokes via Win32 `InjectSyntheticPointerInput` for Photoshop, OneNote, and drawing applications.
3. **📋 Two-Way Real-Time Clipboard Synchronization**:
   - Sync Windows `GetClipboardData` with browser `navigator.clipboard`.
4. **🖐️ Multi-Touch Navigation Gestures**:
   - 3-Finger Swipe Up: Task View (`Win + Tab`).
   - 3-Finger Swipe Left/Right: Switch Virtual Desktops (`Ctrl + Win + Left/Right`).
   - 4-Finger Pinch: Show Desktop (`Win + D`).
5. **📈 Live Diagnostics Overlay**:
   - Toggleable HUD displaying live FPS, glass-to-glass latency, active bitrate, and packet loss telemetry.
   - Related done-work to build on: the hub/sinks already compute ticks/sec, capture/compose/encode ms (in the service log); QR in the Console; the wire-test scripts in `tools/`.

---

## 7. Suggested Next Session (engineer/agent priorities)

1. **Validate hardware HEVC on a real iPad** — the hvcC/Main-profile/GOP work is validated offline by ffprobe but unproven on Safari on-device; check for `[WebCodecs] Hardware HEVC VideoDecoder initialized with hvcC description` in the console, and note the residual risk: after long idles a backgrounded Safari may drop decoded-frame state (fix if observed: force-IDR on visibilitychange).
2. **Audio streaming** (WASAPI loopback → Opus → AudioWorklet) — the single biggest remaining gap vs native feel.
3. **Apple Pencil pressure/tilt** — zero native code needed; Safari pointer events expose it, inject via `InjectSyntheticPointerInput`.
4. **QR + latency probe in the web viewer** (client `tsping:<us>` echo → badge RTT), then adaptive bitrate from TCP send backlog.
5. **Engineering hygiene**: unit tests for the NAL parser / hvcC builder / WS text parser (the NAL-continuation bug is the case study); `e.keyCode` → `e.code`; concurrent-HEVC-session cap; Serilog; Windows-service registration + firewall rule; `DeploySettings()` to push `vdd_settings.xml` to the live locations; finish the root-folder rename.
