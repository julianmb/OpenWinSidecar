# OpenWinSidecar Streaming Optimizations & Low Latency Roadmap
*Date: 2026-09-09*

---

## 1. Pipeline Breakdown & Latency Profile

| Stage | Mechanism | Current Latency | Jitter Source |
|---|---|---|---|
| **Capture** | DXGI Desktop Duplication (`AcquireNextFrame(0)`) | **< 0.5 ms** | CPU staging texture memory read (`Map` / copy) |
| **Encoder Ingest** | Raw BGRA piped to FFmpeg stdin via anonymous pipe | **~1.5 - 2.0 ms** | RAM-to-pipe-to-RAM buffer copies & process context switches |
| **Encode** | Intel QSV `hevc_qsv` (`-async_depth 1`) | **~3.5 ms** | Software VBV rate control fluctuations & bitrate spikes |
| **Transport** | WebSocket over TCP (Ports 28252 / 80 / 8080) | **~1 - 3 ms (Wi-Fi)** / **< 0.5 ms (USB)** | **TCP Head-of-line blocking** on Wi-Fi packet drops |
| **Client Decode** | WebCodecs `VideoDecoder` (Apple Silicon Hardware) | **~1.5 ms** | Keyframe bursts & VSync frame pacing |
| **Render** | HTML5 Canvas 2D (`desynchronized: true`) | **~1 - 2 ms** | iPadOS display refresh synchronization |

---

## 2. Identified High-Impact Improvements

### A. Eliminate TCP Head-of-Line Blocking (Transport Layer)
* **The Root Cause**: WebSocket runs over TCP. If a single packet is dropped over Wi-Fi, TCP pauses all incoming data while retransmitting, causing a sudden 50–150ms freeze followed by a rapid burst of queued frames.
* **Wired USB Tethering**: Connecting via USB cable + Personal Hotspot provides a direct virtual network adapter with <0.5ms RTT and 0% packet loss.
* **UDP / Datagram Transport**: Transitioning to WebTransport (QUIC/UDP) or WebRTC MediaStream allows dropped delta frames to be discarded immediately rather than stalling the stream.

### B. Direct GPU-to-GPU Pipeline (Zero CPU RAM Copy)
* **Current Flow**: Direct3D 11 VRAM → CPU Map/Staging → Anonymous Pipe → FFmpeg stdin → QSV GPU VRAM (`hwupload`).
* **Optimized Flow**: Direct3D 11 shared texture handle passed directly into hardware encoder via Direct3D 11 device sharing (`-init_hw_device d3d11va`).
* **Gain**: Eliminates ~15 MB/frame of PCIe/RAM bandwidth at 2360×1640, saving 1.5–2.5 ms of end-to-end latency and lowering host CPU usage.

### C. Client Rendering via WebGL2 / WebGPU
* **Current**: Canvas 2D `ctx.drawImage(videoFrame, 0, 0)`.
* **Optimized**: Render `VideoFrame` directly into a WebGL2 or WebGPU texture with `powerPreference: "high-performance"` and `desynchronized: true`.
* **Gain**: Bypasses internal WebKit 2D canvas texture conversions and reduces compositor jitter on iPadOS.

### D. FFmpeg QSV Tuning for Zero-Jitter Pacing
* **Strict CBR**: Replace `-maxrate 1.5x` with `-b:v {bitrate}k -minrate {bitrate}k -maxrate {bitrate}k` to prevent network burst buffers.
* **Multi-Slice Encoding**: Add `-slices 4` to allow parallel client decode and incremental packet transmission.

### E. Decouple Cursor from Video Stream
* Utilize the built-in `cursor:client` mode to render the cursor client-side on the iPad with 0ms visual delay, rather than compositing it into the video stream before encoding.

### F. 120Hz ProMotion Mode
* At 120Hz, frame intervals drop from 16.6ms to 8.3ms, cutting maximum display wait jitter in half.

---

## 3. WebTransport (QUIC/UDP) vs WebRTC MediaStream Feasibility Analysis

### Option 1: WebTransport (QUIC over UDP)
* **How it Works**: W3C standard providing bidirectional, low-latency, unreliable datagrams and reliable streams over HTTP/3 (QUIC / UDP).
* **Browser / iPad Support**:
  * Safari on iPadOS 17+ supports WebTransport.
* **Server Implementation (.NET 10)**:
  * .NET natively supports `QuicListener` and HTTP/3 via `System.Net.Quic` (MSQuic).
* **Pros**:
  * Fits the current WebCodecs architecture directly: send raw NALs / Access Units over unbuffered datagrams or un-ordered QUIC streams.
  * No complex SDP negotiation or ICE/STUN/TURN machinery needed.
  * Much lighter weight than WebRTC.
* **Cons / Caveats**:
  * Requires valid TLS / SSL certificates (QUIC requires TLS 1.3). Connecting to an IP address (`https://192.168.1.x`) requires a custom self-signed certificate with matching SHA-256 fingerprint passed to `new WebTransport(url, { serverCertificateHashes: [...] })`.

### Option 2: WebRTC MediaStream / DataChannel
* **How it Works**: Native peer-to-peer audio/video streaming protocol built into all web browsers.
* **Browser / iPad Support**:
  * Universal support across all iOS / iPadOS Safari versions.
* **Server Implementation (.NET 10)**:
  * Requires a native WebRTC C# library (e.g., `SIPSorcery` or `Microsoft.MixedReality.WebRTC`) or wrapping FFmpeg to feed an RTP/SRTP stream.
* **Pros**:
  * Built-in congestion control (GCC / SCReAM), automatic bitrate adaptation, jitter buffer, and native UDP transport.
  * Native hardware decoding directly inside Safari `<video>` element (no manual chunk reassembly in JS).
* **Cons / Caveats**:
  * WebRTC's default jitter buffer often introduces a mandatory 30–50ms buffer delay to smooth out packet jitter (unless explicitly tuned for low-latency mode).
  * Significantly more complex signaling (SDP offer/answer exchange, ICE candidates).
