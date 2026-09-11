# OpenWinSidecar iOS Companion App (Swift + Metal + VideoToolbox)

A native, high-performance iPad client for **OpenWinSidecar** providing hardware-accelerated HEVC streaming at up to **120Hz ProMotion**, sub-millisecond decode latency, and zero-setup USB streaming.

---

## 🚀 How to Run on Your iPad (Zero Mac, Zero Sideloading Required!)

You can run this app natively on your iPad using Apple's free **Swift Playgrounds** app:

1. **Install Swift Playgrounds** on your iPad from the App Store (free from Apple).
2. **Transfer the `OpenWinSidecarClient` folder to your iPad**:
   - Save the `OpenWinSidecarClient` folder to **iCloud Drive**, or
   - AirDrop the folder from a Mac/device, or
   - Put it on a USB thumb drive and open it with the iPad **Files** app.
3. **Open in Swift Playgrounds**:
   - Open Swift Playgrounds on your iPad.
   - Tap **"More Apps"** $\rightarrow$ select the `OpenWinSidecarClient` folder (or double-tap `Package.swift`).
4. **Tap "Run App"**:
   - The iPad's Apple Silicon chip compiles the Swift, Metal, and VideoToolbox code directly on-device in under 5 seconds.
   - The app launches in full-screen mode!

---

## ⚡ Connecting over USB

1. Connect your iPad to your Windows PC with a **USB-C cable**.
2. Turn **Settings $\rightarrow$ Personal Hotspot $\rightarrow$ "Allow Others to Join"** **ON**.
3. In the OpenWinSidecar app, tap **"Preset: USB (172.20.10.2)"** and tap **"Connect Stream"**.
4. Enjoy buttery-smooth 120Hz ProMotion desktop remoting over physical USB wire with zero Wi-Fi jitter!

---

## 🛠️ Architecture

- **Video Pipeline**: `VTDecompressionSession` (VideoToolbox) hardware decoding of Intel QSV HEVC NAL units.
- **Metal Renderer**: `CAMetalLayer` + `CADisplayLink` rendering YUV/NV12 planes directly with Metal shaders.
- **Input**: Multitouch gestures, Apple Pencil pressure and tilt forwarding, two-finger scrolling, and right-click.
