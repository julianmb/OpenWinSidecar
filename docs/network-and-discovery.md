# Network & Discovery Protocol

## 1. Port Allocation & Protocol Endpoints

OpenWinSidecar operates on standard ports `80`, `8080`, and **`28252`**:

| Protocol | Port | Service Component | Purpose |
|---|---|---|---|
| **UDP** | `28252` | `SidecarDiscoveryServer` | Broadcast Discovery & Viewer Beaconing |
| **TCP / HTTP** | `80, 8080, 28252` | `SidecarTcpServer` | HTML5 Web Viewer, HTTP Input API |
| **WebSocket** | `80, 8080, 28252` | `SidecarTcpServer` | Low-Latency Binary Video Stream & Bidirectional Control |

---

## 2. UDP Discovery Protocol (`SidecarDiscoveryServer.cs`)

Standard discovery packets across the local subnet:
1. `SidecarDiscoveryServer` binds to `0.0.0.0:28252` with `SO_REUSEADDR`.
2. Responds to discovery probes with:
   ```text
   SIDECAR_SERVER;NAME=<MachineName>;PORT=28252;VER=1.0;DISPLAYS=1\n
   ```

---

## 3. Connection Modes: Wi-Fi vs USB Cable

### A. Local Wi-Fi / Ethernet LAN
- Client connects directly to `http://<Host-IP>:8080` or `http://<Host-IP>:28252`.
- Typical latency: 8–20ms.

### B. Android ADB USB Reverse Tethering
- Enabled via `adb.exe reverse tcp:28252 tcp:28252`.
- Client opens `http://localhost:28252` in Chrome on Android with ~0ms network latency.
