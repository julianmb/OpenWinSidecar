# 🚀 OpenWinSidecar (.NET 10 LTS)

<div align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10.0%20LTS-purple.svg)](https://dotnet.microsoft.com/)
[![Direct3D 11](https://img.shields.io/badge/Capture-Direct3D%2011%20DXGI-green.svg)]()
[![Hardware Codec](https://img.shields.io/badge/Encoder-Intel%20Arc%20HEVC-orange.svg)]()
[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%7C%20iPadOS%20%7C%20Android-blue.svg)]()

**Open-source, ultra-low latency, hardware-accelerated Windows virtual monitor server, dashboard, and WebCodecs streamer.**  
Turn your **iPad**, tablet, or laptop into a fluid **60 FPS second or third monitor** with native touch, gestures, zero-duplicate cursors, and live Windows display scaling.

[Core Features](#-core-features) • [Smart 3rd Screen Workflow](#-smart-3rd-screen-lifecycle) • [Architecture](#-architecture) • [Quick Start](#-quick-start) • [iPad Setup](#-ipad-safari-setup) • [CLI Commands](#-cli-management) • [Documentation](#-documentation)

</div>

---

## 🌟 Why OpenWinSidecar?

Proprietary solutions like spacedesk or Duet often suffer from CPU overhead, compression artifacts, laggy touch responsiveness, or paid subscription barriers. **OpenWinSidecar** delivers an Apple Sidecar-like experience for Windows PCs:

- ⚡ **Imperceptible Latency (~7–10 ms glass-to-glass)**
- 🎮 **Direct GPU VRAM Capture (< 0.5 ms via Direct3D 11 Desktop Duplication)**
- 🚀 **Intel Arc GPU Hardware HEVC (H.265) Encoding (~3.5 ms)**
- 📱 **Zero-Client Install**: Runs straight in Safari using Apple Silicon hardware **WebCodecs** decoding.
- 🔄 **Smart 3rd Screen Power Lifecycle**: Turn on 3rd screen to auto-stream; turn off to cleanly detach the virtual display and stop streaming.
- 🖱️ **Zero-Duplicate Cursor**: Hardware-accurate mouse pointer rendering with zero overlapping client pointers.
- 🖥️ **Live Windows Display DPI Scaling (100%–225%)** directly from the web client.
- 📐 **True Aspect-Ratio Preservation**: Never stretches or squishes your display.

---

## 📊 Glass-to-Glass Latency Budget

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

## 🏗️ System Architecture

```mermaid
flowchart LR
    subgraph Host["Host PC (Windows 11)"]
        VDD["IddCx Driver\n(ROOT\\DISPLAY\\0000)"] --> D3D11["Direct3D 11 VRAM\n(IDXGIOutputDuplication)"]
        D3D11 --> QSV["Intel Arc GPU\n(hevc_qsv ~3.5ms)"]
        CCD["Win32 CCD DPI API\n(WindowsDpiService)"] -.-> VDD
        RES["Win32 Display Switcher\n(DisplayResolutionManager)"] -.-> VDD
        QSV --> TCP["Multi-Port Server\n(Ports 80, 8080, 28252)"]
        INP["Win32 SendInput\n(InputDispatcher)"] <-- TCP
    end

    subgraph iPad["Client iPad (Safari / PWA)"]
        TCP ==> WS["WebSocket Client\n(Binary WebCodecs)"]
        WS --> DEC["Apple Silicon Hardware\n(VideoDecoder ~1.5ms)"]
        DEC --> CV["Canvas Framebuffer\n(desynchronized: true)"]
        TOUCH["Touch & Gestures\n(rAF Batched)"] --> WS
    end
```

---

## 🔄 Smart 3rd Screen Lifecycle

OpenWinSidecar provides an intuitive 1-click power switch for your virtual display:

1. **⚡ Turn ON 3rd Screen (`⚡ 3rd Screen ON`)**:
   - Enables the signed Virtual Display Driver (`ROOT\DISPLAY\0000` via `pnputil` / `devcon`).
   - Automatically extends Windows desktop onto the 3rd iPad monitor.
   - Starts the streaming service.
   - Live badge turns **`[🟢 3RD SCREEN ACTIVE & STREAMING]`**.

2. **🔌 Turn OFF 3rd Screen (`🔌 3rd Screen OFF`)**:
   - Stops the streaming service.
   - Disables the Virtual Display Driver, cleanly detaching the monitor so Windows never loses your mouse cursor or app windows off-screen.
   - Live badge turns **`[⚫ 3RD SCREEN DISABLED / OFFLINE]`**.

3. **🛑 Complete Shutdown (Service + Driver)**:
   - 1-click complete shutdown of the streaming service, cleanup of background processes, and disabling of the display driver.

---

## 🚀 Core Features

### ⚡ Direct3D 11 GPU VRAM Capture (`DxgiCaptureService`)
- Acquires desktop frames directly from GPU VRAM via `IDXGIOutputDuplication` in **< 0.5 ms**.
- Pinned SIMD native memory buffers bypass GDI CPU overhead.
- Automatic graceful fallback to GDI `CreateDC` if DXGI access is revoked during secure desktop prompts.

### 🎬 Intel Arc QuickSync HEVC (H.265) Encoding
- Encodes 60 FPS video via Intel QuickSync (`hevc_qsv`) in **~3.5 ms**.
- Dynamic bitrate scaling presets:
  - **50% Quality (3 Mbps)** — Congested Wi-Fi networks
  - **65% Quality (5 Mbps)** — Standard Wi-Fi
  - **80% Quality (8 Mbps - Default)** — Crisp text & 60 FPS motion
  - **90% Quality (12 Mbps)** — Ultra-sharp design/retina detail

### 🖱️ Zero-Duplicate Cursor & Pointer Modes
- Eliminates overlapping dual mouse pointers by suppressing browser-side cursors (`cursor: none !important`) and streaming a single, hardware-accurate Windows host cursor with sub-pixel hotspot correction.
- Choose between **Streamed Host Cursor (Default)**, **Touch Tablet Mode (Hide Cursor)**, or **Browser Native Cursor**.

### 📱 Native WebCodecs Decoding on iPadOS
- Decoded directly on Apple Silicon dedicated media engines using W3C `VideoDecoder` (`optimizeForLatency: true`).
- Rendered to HTML5 Canvas with `{ desynchronized: true }`, bypassing Safari's compositor queue and saving 1 full frame (~16.6ms) of latency.

### 🖥️ Dynamic Win32 CCD DPI Scaling & Resolution Switcher
- Change Windows Display Scale (**100% to 225%**) directly from the web client or WPF console.
- Apply curated iPad resolution presets (Air, Pro 11"/13", 12.9", 10.2", mini), custom $W \times H @ \text{Hz}$, or driver-supported modes in real-time via `DisplayResolutionManager`.

### ✍️ Smooth Touch, Gestures & Keyboard
- Single-finger touch and mouse drag with `requestAnimationFrame` event batching for zero input jitter.
- Two-finger scrolling (`MOUSEEVENTF_WHEEL`) and two-finger tap (Right Click).
- Hardware keyboard forwarding and mobile virtual keyboard support.

---

## 🏃 Quick Start

### 1. Prerequisites
- Windows 10 (1607+) or Windows 11
- [.NET 10 LTS SDK](https://dotnet.microsoft.com/download)
- FFmpeg on PATH (or via WinGet: `winget install Gyan.FFmpeg`)

### 2. Clone and Build
```powershell
git clone https://github.com/YourUsername/OpenWinSidecar.git
cd OpenWinSidecar
dotnet build OpenWinSidecar.slnx
```

### 3. Launch Dashboard & Service
```powershell
# Run the WPF Management Console (with tray icon & resolution manager)
dotnet run --project src/OpenWinSidecar.Console

# Or run the service directly with Administrator privileges (recommended for DXGI capture)
.\run_service_admin.bat
```

---

## 📱 iPad Safari Setup

1. Connect your iPad to the same Wi-Fi network (or connect via USB cable).
2. Open Safari and navigate to:
   ```
   http://<YOUR_PC_IP>:8080
   ```
   *(e.g., `http://192.168.1.12:8080` or `http://192.168.1.12:28252`)*
3. **For Borderless Fullscreen App Experience:**
   - Tap **Share (⬆️)** in Safari.
   - Select **Add to Home Screen**.
   - Launch **OpenWinSidecar** from your home screen as a standalone, distraction-free app.

---

## ⚙️ Web Settings Reference

Tap the top pill button (`🟢 60 FPS • HEVC GPU • ⚙️`) in the web viewer to adjust:

- **Target Display:** Select Primary, Secondary, or Virtual 3rd Display.
- **Video Codec:** `🚀 HEVC / H.265 (Intel Arc GPU Accelerated)` or `🖼️ Intra JPEG`.
- **Screen Resolution:** `✨ Auto-Detect My Device Screen` or choose your specific iPad model.
- **Windows Display Scale:** `100%`, `125%`, `150%`, `175% (Recommended for iPad)`, `200%`, `225%`.
- **Mouse Cursor Mode:** `🖥️ Host Windows Cursor (Default)`, `📱 Touch Tablet (Hide Cursor)`, `💻 Browser Native Cursor`.
- **UI Magnification:** `1.0x` (Full Desktop) up to `2.0x` (Large Touch Targets).
- **Quality Preset:** `50% (Fastest)` to `90% (Ultra Crisp)`.

---

## 🛠️ CLI Management

OpenWinSidecar includes a full CLI tool for automation and headless environments:

```powershell
# Smart 3rd Screen Power Switch
dotnet run --project src/OpenWinSidecar.Cli -- screen on      # Enable driver, extend desktop & start streaming
dotnet run --project src/OpenWinSidecar.Cli -- screen off     # Stop streaming & disable virtual display driver
dotnet run --project src/OpenWinSidecar.Cli -- shutdown       # Complete shutdown (service + driver)

# Service Lifecycle
dotnet run --project src/OpenWinSidecar.Cli -- status         # Show service, driver, and network status
dotnet run --project src/OpenWinSidecar.Cli -- start          # Start streaming service
dotnet run --project src/OpenWinSidecar.Cli -- stop           # Stop streaming service
dotnet run --project src/OpenWinSidecar.Cli -- restart        # Restart streaming service
dotnet run --project src/OpenWinSidecar.Cli -- driver restart # Restart display driver and re-extend

# Inspection & Configuration
dotnet run --project src/OpenWinSidecar.Cli -- clients        # List connected client displays
dotnet run --project src/OpenWinSidecar.Cli -- network        # List active network adapters & endpoints
dotnet run --project src/OpenWinSidecar.Cli -- config         # View/edit configuration settings
```

---

## 📦 Project Structure

```
OpenWinSidecar/
├── OpenWinSidecar.slnx              # Solution file (.NET 10 LTS)
├── run_service_admin.bat            # Administrator launcher
├── src/
│   ├── OpenWinSidecar.Core/         # Win32 CCD API, DisplayResolutionManager, VirtualDisplayManager
│   ├── OpenWinSidecar.Service/      # DXGI/GDI Capture, HEVC QSV Encoder, Input Dispatcher, HTTP/WS
│   ├── OpenWinSidecar.Console/      # WPF Management Dashboard & System Tray
│   └── OpenWinSidecar.Cli/          # CLI Automation Tool
├── drivers/VDD/                     # Signed IddCx Virtual Display Driver
└── docs/                            # Comprehensive technical documentation & research
```

---

## 📚 Documentation

- [Subsystem Architecture](docs/architecture.md)
- [Drivers & Virtual Display Setup](docs/drivers-and-virtual-monitors.md)
- [Screen Capture & Streaming Guide](docs/screen-capture-and-streaming.md)
- [Input & Multi-Touch Gestures](docs/input-and-gestures.md)
- [Network Protocols & USB Tethering](docs/network-and-discovery.md)
- [GPU Streaming Research & Latency Benchmarks](docs/gpu-streaming-research.md)
- [Troubleshooting & Diagnostics](docs/troubleshooting-and-faq.md)

---

## 📄 License

OpenWinSidecar is licensed under the [MIT License](LICENSE).
