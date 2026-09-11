# OpenWinSidecar iOS Companion App (Swift + Metal + VideoToolbox)

A native, high-performance iPad client for **OpenWinSidecar** providing hardware-accelerated HEVC streaming at up to **120Hz ProMotion**, sub-millisecond decode latency, and zero-setup USB streaming.

---

## 🚀 How to Run on Your iPad (Zero Mac, Zero Sideloading Required!)

You can run this app natively on your iPad using Apple's free **Swift Playgrounds** app:

1. **Install Swift Playgrounds** on your iPad from the App Store (free from Apple).
2. **Get the app bundle** (easiest: on the iPad open `http://<PC-IP>:8080/app` in Safari and tap
   **Download**, or transfer this `OpenWinSidecarClient` source folder via iCloud Drive / AirDrop /
   USB and rename the copy to `OpenWinSidecar.swiftpm`).
3. **Open in Swift Playgrounds**:
   - Unzip in Files if needed, then open the `OpenWinSidecar.swiftpm` folder in Swift Playgrounds.
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
- **Protocol**: speaks the current server protocol (`set_res` resolution sync, `desc:` hvcC descriptions, `?codec=hevc` startup, keyframe marking, auto-reconnect, FPS/bitrate telemetry) and supports the server **access password** via challenge-response (you'll be prompted in-app).
