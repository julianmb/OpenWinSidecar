# OpenWinSidecar (.NET 10 LTS)

An open-source, ultra-low latency, hardware-accelerated virtual monitor server, management dashboard, and CLI replacement for **spacedesk** designed for iPads, tablets, and remote displays.

---

## 🚀 Core Features

- **⚡ Intel Arc GPU Hardware HEVC (H.265) Encoding**:
  - Encodes 60 FPS video via Intel QuickSync (`hevc_qsv`) in **~3.5 ms**.
  - High fidelity dynamic bitrate scaling: **80% Default (8 Mbps)** and **90% Ultra-Crisp (12 Mbps)**.
  - Low bandwidth (~1 MB/s), saving over 90% network traffic compared to JPEG intra-frames.
- **🎮 Zero-Copy Direct3D 11 GPU Capture**:
  - DirectX Desktop Duplication (`IDXGIOutputDuplication`) captures directly from Intel Arc GPU VRAM in **< 0.5 ms**.
  - Zero GDI overhead, zero CPU memory thrashing, and automatic GDI fallback.
- **📱 Native Apple Silicon WebCodecs Decoding & Zero-Buffer Latency**:
  - Decoded in hardware by iPad Apple Silicon (`VideoDecoder`, `optimizeForLatency: true`) in **~1.5 ms**.
  - Direct canvas framebuffer presentation with `desynchronized: true` (bypasses Safari compositor lag).
  - Total glass-to-glass latency: ⚡ **~7 – 10 ms**.
- **🖥️ Live Windows Display DPI Scaling from the Web**:
  - Integrated Win32 CCD API (`DisplayConfigSetDeviceInfo`) allows changing Windows Display Scale (**100% to 225%**) directly from the iPad browser settings menu.
  - Native ClearType sub-pixel font rendering with zero distortion.
- **📐 Mathematical Aspect-Ratio Lock & Auto Device Detection**:
  - Auto-detects device screen resolution, orientation, and `@2x` Retina device pixel ratio.
  - Locks aspect ratio (`scaleX == scaleY`) to eliminate stretching across both browser windowed and fullscreen modes.
  - Complete matrix of native and `@2x` logical resolutions for every iPad generation (10.9", 11" M4, 13" M4, 12.9", 10.2", 9.7", 8.3" Mini).
- **🌐 Multi-Port Web Server (Ports 80, 8080, 28252)**:
  - Serves the WebCodecs HTML5 client directly over HTTP / WebSockets.
  - Simply open `http://<HOST_IP>:8080` or `http://<HOST_IP>` in iPad Safari.
- **📱 PWA & Standalone Fullscreen Web App**:
  - Supports iOS "Add to Home Screen" for a borderless, native app experience without browser navigation bars.
  - Screen WakeLock integration prevents iPad display from dimming or sleeping.
- **✍️ Ultra-Smooth Input & Gestures**:
  - Single-finger mouse/touch with `requestAnimationFrame` event batching for zero input jitter.
  - Two-finger scrolling and pinch gestures.
  - Full hardware keyboard forwarding and mobile virtual keyboard support.

---

## 📦 Solution Architecture

```
OpenWinSidecar/
├── OpenWinSidecar.slnx
├── src/
│   ├── OpenWinSidecar.Core/          # Windows Display API, CCD DPI Engine, Virtual Display Manager
│   │   ├── Services/WindowsDpiService.cs       # Win32 DisplayConfigSetDeviceInfo DPI engine
│   │   └── Services/VirtualDisplayManager.cs   # IddCx driver manager & topology extension
│   ├── OpenWinSidecar.Service/       # High-performance Streaming Server & Capture Engine
│   │   ├── Capture/DxgiCaptureService.cs       # Direct3D 11 Desktop Duplication GPU capture
│   │   ├── Capture/ScreenCaptureService.cs     # GDI fallback & watermark compositor
│   │   ├── Encoding/HevcStreamEncoder.cs       # Vendor-abstracted HEVC encoder (Intel QSV / NVIDIA NVENC / AMD AMF via FFmpeg)
│   │   ├── Input/InputDispatcher.cs            # Win32 SendInput mouse & keyboard dispatcher
│   │   └── Protocol/SidecarTcpServer.cs        # Multi-port HTTP/WebSocket server & HTML5 client
│   ├── OpenWinSidecar/               # WPF Management Dashboard & System Tray
│   └── OpenWinSidecar.Cli/           # Command-Line Automation Tool
├── drivers/VDD/                     # Signed IddCx Virtual Display Driver (ROOT\DISPLAY\0000)
│   └── vdd_settings.xml             # Complete iPad native and logical resolution matrix
├── docs/                            # Comprehensive technical documentation & handovers
└── README.md
```

---

## 🏃 Running the Server

### 1. Interactively / Development Mode:
```powershell
# Build and run OpenWinSidecar Service
dotnet build "src/OpenWinSidecar.Service/OpenWinSidecar.Service.csproj"
& "src/OpenWinSidecar.Service/bin/Debug/net10.0-windows/OpenWinSidecar.Service.exe"
```

### 2. Connect from iPad:
1. Ensure your iPad is on the same local Wi-Fi network.
2. Open Safari and navigate to:
   ```
   http://192.168.1.12:8080
   ```
   *(or `http://192.168.1.12`)*
3. Tap **Share (⬆️) → Add to Home Screen** to install OpenWinSidecar as a borderless, full-screen app.

---

## ⚙️ Stream Settings

Stream settings live in the **Windows dashboard** (desktop app), which is
authoritative: display target, stream FPS (30/60, default 60), quality, codec
(HEVC or JPEG), color depth (8/10-bit), and magnification. The iPad viewer keeps
only iPad-local controls (fullscreen, keyboard, cursor mode, aspect fit) and a
settings modal with the AGPL source link. Dashboard changes reconnect viewers
automatically.

---

## 🛠️ CLI Management

```powershell
# Check service status, active connections, and network endpoints
dotnet run --project src/OpenWinSidecar.Cli -- status

# List connected client screens and display parameters
dotnet run --project src/OpenWinSidecar.Cli -- clients

# Manage service lifecycle
dotnet run --project src/OpenWinSidecar.Cli -- start
dotnet run --project src/OpenWinSidecar.Cli -- stop
dotnet run --project src/OpenWinSidecar.Cli -- restart
```

---

## 📄 License

GNU Affero General Public License v3.0 (see `gh/LICENSE` — the `gh/` tree is the published project).
