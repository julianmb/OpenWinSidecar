# Future Work & Deferred Decisions

> _Last updated: 2026-09-12_

This document tracks improvements that were prototyped, evaluated, or consciously deferred —
with the reasoning, so the next contributor doesn't repeat a dead end. Items are ordered by
how actionable they are.

---

## 0. Recently resolved (2026-09-11/12) — do not re-investigate

- **120 Hz end-to-end: WORKS.** The IddCx display advertises and switches to 2360×1640 @ 120Hz;
  the hub sustained 112–119 ticks/s at capture 1.3–3.7ms. QSV saturates at ~45–60fps on
  full-motion native content, so 120Hz only pays for light motion on a 120Hz panel.
- **10-bit Main10: VALIDATED on iPad.** QSV `format=p010le,hwupload` auto-selects Main10
  (ffprobe-confirmed); viewer hvcC follows the live SPS; rejecting decoders auto-revert.
  Opt-in via viewer Color-depth select.
- **4:4:4 chroma: RULED OUT.** QSV emits Rext for 444 input; iPads fall to software decode.
- **`-async_depth 1` is load-bearing.** Removing it (intended pipelining win) cost ~25ms
  latency and 6fps presented; restored after A/B. Server-received fps ≠ presented fps.
- **Decoder-death freeze: FIXED.** A silence-flush timer could emit a truncated NAL and kill
  Safari's decoder into permanent JPEG fallback. The timer now only flushes complete AUs;
  the viewer rebuilds + resyncs on transient decode errors (JPEG only after 3/min).
- **~1s micro-stop: FIXED.** Scene-cut I-slices + unconstrained P spikes (100–162KB) stalled
  the wire 80–130ms each. Now `adaptive_i 0`, `low_delay_brc`, `mbbrc`, frame-size caps.
- **Encoder "warmup ramp": EXONERATED (isolated benchmark).** A single encoder fed at 60Hz
  outputs a steady 62.4–62.6 AUs/s from ~5s at all operating points, including noise at the
  bitrate cap. The slower settling seen on fresh live sessions is startup churn
  (warmup spawn → first-push re-init → adaptive-scale switch = 2–3 respawns + IDR bursts)
  plus client catch-up — self-resolving, no action. Do not "fix" BRC warmup; it doesn't exist.

---

## 1. WebTransport / QUIC transport (UDP) — evaluated, blocked on TLS setup friction

**Goal:** remove TCP head-of-line blocking on lossy Wi-Fi by sending video over QUIC.

**Findings:**

- **Client side is ready.** Safari/iPadOS **26.4+** supports WebTransport (Baseline March 2026).
  Availability is gated on the **OS version**, not the Safari version.
- **Server side is ready.** .NET/Kestrel ships WebTransport as a preview feature
  (`IHttpWebTransportFeature`), built on **MsQuic + HTTP/3** — we do **not** have to hand-roll
  QPACK/SETTINGS/CONNECT. Requires:
  - `<EnablePreviewFeatures>True</EnablePreviewFeatures>`
  - `<RuntimeHostConfigurationOption Include="Microsoft.AspNetCore.Server.Kestrel.Experimental.WebTransportAndH3Datagrams" Value="true" />`
  - A spike (separate scratch Kestrel app) got HTTP/3 up: the test page served over HTTPS with
    `alt-svc: h3=":4433"` and a cert-hash-pinned WebTransport session.
- **Kestrel's WebTransport has no datagrams** (its own docs say "most of draft-02, except
  datagrams"). Workaround: **one unidirectional QUIC stream per frame** — QUIC streams are
  independently recoverable, so a lost frame only blocks its own stream and the client drops
  it (timestamp-ordered, latest-wins). Gives loss isolation without datagrams.
- **Blocker — secure context.** WebTransport is `[Exposed=(Window,Worker), SecureContext]` and
  the URL scheme **must be `https`** (the constructor throws `SyntaxError` otherwise).
  `serverCertificateHashes` only relaxes *certificate trust* (allows a self-signed cert); it does
  **not** remove the HTTPS/secure-context requirement. Safari additionally **ignores
  user-installed root CAs for WebTransport**, so hash pinning is mandatory for the session.
  There is **no way to use WebTransport from a plain `http://` page.**
- **Consequence:** WebTransport is fundamentally at odds with zero-setup. The current WebSocket
  path is plain `http://<ip>:8080` with no cert. WebTransport is only realistic as an **opt-in
  "low-latency mode"** for power users, or with a real certificate on an owned domain.

**Next steps (only if pursued):**

1. Finish the iPad interop test: does Safari 26.4 connect to Kestrel's **draft-02** with a
   self-signed cert the user merely *accepts* (no trust profile)? Interop is the big unknown —
   `SETTINGS_ENABLE_WEBTRANSPORT` changed in draft-07. If yes → opt-in mode is viable; if no →
   drop the idea.
2. If viable: add a parallel transport (one unidirectional stream per encoded frame) alongside
   the WebSocket, keeping WebSocket as the default and automatic fallback.
3. Certificate UX: evaluate a real cert (owned domain + DNS) versus self-signed + hash pinning +
   accept-warning.

Related: `handover.md`, `optimizations260909.md` (transport comparison), and `CHANGELOG.md`.

---

## 2. GPU-to-GPU capture → encode — blocked by the separate-process architecture

**Goal:** eliminate the CPU staging copies (DXGI `Map` → GDI bitmap → raw buffer → ffmpeg pipe).

**Findings:**

- FFmpeg's `ddagrab` filter captures via Desktop Duplication **on the GPU**, but on this machine
  it only exposes the Intel outputs (3440×1440, 1920×1080). The **IddCx virtual display
  (2360×1640) is not visible** to it (no `output_idx` maps to it). It also emits at a fixed
  framerate with no "only on change", which would break idle-skip.
- A separate ffmpeg process **cannot ingest an externally-shared D3D11 texture over stdin**.
- Therefore true GPU-to-GPU requires **in-process encoding**: Media Foundation
  (`Vortice.MediaFoundation` is already referenced; can consume D3D11 textures via the DXGI
  device manager), Intel oneVPL, or D3D12 Video Encode. All are significant rewrites with real
  regression risk against the working QSV path.
- **Do not retry** "write straight from the locked GDI bitmap into the ffmpeg pipe": it was tried
  and reverted — holding the GDI lock across the blocking pipe write stalls the capture producer
  and made fluidity noticeably worse.

---

## 3. Wi-Fi loss resilience via closed-loop congestion control — attempted, reverted

A controller that throttled the offered frame rate from the client's reported
`latencyMs`/`latencyMaxMs`/`decodeQueuePeak` was implemented and then **reverted**:

- Latency is a **feedback-contaminated signal** — throttling changes frame pacing, which shifts
  the measured latency, which keeps it "congested" (self-reinforcing).
- Normal Wi-Fi jitter (this iPad: ~70 ms avg, transient `latMax` 165–250 ms) tripped the
  threshold and dropped the stream to **5 fps**.
- **What actually protects the stream (already in place):** TCP drop-oldest backpressure, the
  64 KB send buffer, and the bounded HEVC pipeline.
- The genuine fix for loss is UDP (section 1). Manual lever that already exists: the viewer's
  **Quality** selector (50% / 65%) lowers the HEVC bitrate to leave Wi-Fi headroom.

---

## 4. Security hardening — done (2026-09-10 batch)

- `SaveSettingsElevated` command injection fixed (escaped literals + `-EncodedCommand`).
- Password **DPAPI-protected at rest** (legacy plaintext read once, then cleared).
- **Plaintext auth fallback removed** — challenge-response only.
- **`/input` accepts `X-Access-Token` / `Authorization: Bearer`** in addition to `?pw=`.
- **Same-origin enforcement** for browser WebSocket upgrades and `/input` (foreign `Origin`
  rejected; absent `Origin` from native tools allowed).
- **Cross-connection auth backoff** per IP (exponential, cleared on success).
- Note: the service still runs **open by default** (no password) — anyone on the LAN can view the
  screen and inject input unless a password is configured. That is a product default, not a bug.

---

## 5. Multi-vendor encoder validation — code complete, unvalidated on NVIDIA/AMD

`HevcEncoderProfile` abstracts the vendor (`qsv` / `nvenc` / `amf`) and a startup probe
(`HevcEncoderProfiles.Detect`) selects the first that actually works, else JPEG. Only the **QSV**
profile is validated (Intel Arc). NVENC/AMF flags are written against FFmpeg options and are
selected **only if their full argument set passes a one-frame probe**, so they can only fall back
to JPEG — never ship a broken stream. Validate on real NVIDIA/AMD hardware when available.

---

## 6. Distribution — bundle FFmpeg: RESOLVED (2026-09-12, winget instead of bundling)

The service depends on a separately-installed FFmpeg (Gyan build), located via
`FindFfmpegExecutable`. Resolved for v0.1 by making the dependency declarative instead of
bundling: the Windows installer detects ffmpeg (same order as the runtime: PATH → WinGet
`Links` alias → `Packages` scan) and installs `Gyan.FFmpeg` via winget when missing; the
winget package itself declares `Gyan.FFmpeg` as a `PackageDependencies` entry, so
`winget install OpenWinSidecar` pulls it automatically. Bundling GPL FFmpeg binaries was
rejected: +~100 MB installer and redistribution obligations for no functional gain.
Remaining edge: machines with neither winget nor ffmpeg still degrade to JPEG (installer
warns, app logs the install command).

---

## 7. Feature roadmap (from `handover.md`)

- **Ultra-low-latency audio:** WASAPI loopback capture → Opus → WebSocket → `AudioWorklet`.
- **Apple Pencil pressure & tilt:** forward `e.pressure`/`tiltX`/`tiltY`; inject via Win32
  `InjectSyntheticPointerInput`.
- **Two-way clipboard sync:** Windows clipboard ↔ `navigator.clipboard`.
- **Multi-touch navigation gestures:** 3-finger swipe up (Task View), 3-finger left/right
  (virtual desktops), 4-finger pinch (Show Desktop).
- **Diagnostics HUD:** the glass-to-glass latency probe is already in the viewer; extend with
  live bitrate, render drops, and loss.

---

## 8. Console UI polish — done (2026-09-10/11)

The structural pass consolidated the resolution controls, moved maintenance into an
"Advanced" expander, added an open-access/password badge, masked the password, moved logs to a
separate resizable `LogWindow`, set a **4:3 window with stretching cards**, added a **live
connected-clients table** with per-client **Kick**, **window-position memory** (registry),
and a **Session** card (state, display, encoder, clients, uptime) with a **live
fps/latency sparkline**. Resolution/scale dropdowns are **immediate-apply** (no Apply buttons).
Remaining polish: richer client details (codec profile, keyframe age).
