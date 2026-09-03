# Screen Capture and Hardware Video Streaming Guide

## 1. Video Capture Pipeline

OpenWinSidecar implements a dual-mode capture pipeline:

### 1.1 Fast Path: Direct3D 11 Desktop Duplication (`DxgiCaptureService`)
- **API:** Windows Desktop Duplication (`IDXGIOutputDuplication`)
- **Latency:** **< 0.5 ms**
- **VRAM Access:** Directly maps the GPU staging texture in Intel Arc VRAM.
- **Buffer Management:** Pre-allocated, pinned byte buffer (`_reusableBgraBuffer`) with SIMD row copying.

### 1.2 Fallback Path: Direct Device Context GDI (`ScreenCaptureService`)
- **API:** Win32 GDI `CreateDC("DISPLAY", deviceName, ...)`
- **Latency:** ~2.5 – 4.5 ms
- **Purpose:** Used when DXGI access is temporarily restricted (e.g. UAC secure desktop prompts, mode switches).

---

## 2. Hardware HEVC (H.265) Encoding

OpenWinSidecar leverages host GPU hardware encoders via Intel QuickSync Video (`hevc_qsv`):

```powershell
ffmpeg -hide_banner -v error -f rawvideo -pix_fmt bgra -s:v {width}x{height} -r 60 -i - -c:v hevc_qsv -preset veryfast -b:v {bitrate}k -maxrate {bitrate*1.2}k -bufsize {bitrate*0.5}k -g 60 -bf 0 -tune zerolatency -f hevc -
```

### 2.1 Dynamic Bitrate Presets

| Quality Preset | Target Bitrate | Peak Bitrate | Typical Bandwidth | Intended Use |
|---|---|---|---|---|
| **50% Quality (Fastest)** | `3,000 kbps` | `3,600 kbps` | ~0.35 MB/s | Congested Wi-Fi networks |
| **65% Quality (Balanced)** | `5,000 kbps` | `6,000 kbps` | ~0.60 MB/s | Standard Wi-Fi |
| **80% Quality (Default)** | `8,000 kbps` | `9,600 kbps` | ~1.00 MB/s | Ultra-sharp text & 60 FPS motion |
| **90% Quality (Ultra Crisp)** | `12,000 kbps` | `14,400 kbps` | ~1.50 MB/s | Studio quality & design work |

---

## 3. Zero-Duplicate Cursor & Pointer Subsystem

To eliminate the common issue of double/overlapping mouse cursors when streaming to web clients:

1. **Browser Cursor Suppression**: The client CSS enforces `html, body, #container, canvas { cursor: none !important; }`, preventing the browser from drawing its own cursor over the video canvas.
2. **Host Hardware Cursor Rendering**: The server captures the exact Windows cursor with `GetCursorInfo`, retrieves hotspot offsets via `GetIconInfo`, and renders it directly into the frame using `DrawIconEx`.
3. **Cursor Modes**:
   - **🖥️ Host Windows Cursor (Default)**: Embedded into the video frame with sub-pixel alignment and zero duplicate artifacts.
   - **📱 Touch Tablet Mode**: Suppresses mouse cursor drawing for pure touch-driven iPad experiences.
   - **💻 Browser Native Cursor**: Hides the streamed host cursor and lets the client browser render its native pointer.

---

## 4. Mathematical Aspect Ratio Lock

To prevent squishing or stretching when a monitor is streamed to an iPad (e.g. 1.439:1 aspect ratio), the server enforces:

```csharp
double srcAspect = (double)captureW / captureH;
int dstWidth = (targetWidth > 0) ? targetWidth : 1180;
int dstHeight = (int)Math.Round(dstWidth / srcAspect);

// Ensure even pixel dimensions for hardware video encoders
dstWidth = (dstWidth / 2) * 2;
dstHeight = (dstHeight / 2) * 2;
```
This guarantees `scaleX == scaleY` across all resolutions.
