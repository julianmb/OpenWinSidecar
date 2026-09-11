# OpenWinSidecar — Change Log

Every change to this project is documented here: **what** was changed, **why**, and **how it was verified**. New entries go at the top. The commit history (`git log`) carries the same explanations per commit; this file is the human-readable narrative.

---

## 2026-09-11 — Server-side pacing + adaptive half-res motion encode

### Added — paced AU release (closes the received→painted gap)
- `SendHevcPacketAsync` releases access units on an even TargetFramerate cadence (leaky-bucket: late frames go immediately, early ones wait for their slot). Bursty QSV completion no longer wastes client vsyncs two-at-once-then-starve. Pacer waits don't count toward network backpressure.
- Measured on cursor motion: painted **44–46fps (new best, was 40–43)**, presentation-wait ~4ms (was ~6ms), latency +~10ms (expected pacing cost; fluidity-first call stands).

### Added — adaptive half-res encode during sustained motion
- Leaky motion score over capture activity: ~150ms of continuous pixel change engages half size (1180×820), ~250ms calm releases to full for crisp text. Cursor-only motion never engages (no pixel updates). Hysteresis bounds switches; each switch restarts the encoder once with a fresh IDR.
- `SIDECAR_ADAPTIVE_SCALE=0` pins full-res (escape hatch + A/B).
- **Coalescing bug found by measurement and fixed:** bitrate pinning applied across geometry changes, so the switch ran half-res at the full-res bitrate for 30s then restarted a second time. Now geometry always applies immediately with the correct bitrate; pure bitrate changes go down immediately (congestion relief urgent) and hold upward ones 30s.

### Measured (including a bisect)
- Adaptive OFF: 57fps sustained loopback, iPad painted up to 49fps, zero stalls.
- Adaptive ON steady state: **perfect 60fps windows, ~19ms gaps, zero >40ms stalls on both sinks**; zero AUs over 100KB (micro-stop fix holds); iPad 43–51fps received at ~75–90ms.
- Honest caveat: a post-restart BRC warmup transient was observed (fast at generous operating points, up to ~85s at tight half-res on synthetic full-frame torture content). Self-resolving, no flapping, no mid-run restarts; desktop-typical motion is far easier than the test pattern. Watched, not yet fully explained — revisit if real use shows slow settling.

---

## 2026-09-11 — Killed the ~1s micro-stop; quality-constant controls; Main10 opt-in; 4:4:4 ruled out

### Diagnosed (Phase 0, measured)
- New timestamped logs (`[HEVC] keyframe AU #n`, `[Sink] large AU …`, harness `wall=` gap lines) showed **large non-key P-frames (100–162KB, key=False)** during sustained motion — QSV scene-cut I-slices coded as TRAIL plus unconstrained P spikes. At ~10 Mbps each occupies the wire 80–130ms and stalls everything behind it: the ~1s-periodic mini-stop. Only 2 true keyframes per session (10-min GOP healthy); the stops were never IDRs.

### Fixed — encoder smoothing (Phase 1)
- QSV `RateControlArgs`: `-low_delay_brc 1 -mbbrc 1 -adaptive_i 0` (strict frame-size obedience, macroblock rate control, no scene-cut I insertion).
- Worst-case airtime caps (QSV only): `-max_frame_size_p 6x` / `-max_frame_size_i 20x` the average frame (session IDRs ~140–210KB pass untouched).

### Changed — quality-constant floor (Phase 2.1)
- QSV: `-max_qp_i 30 -max_qp_p 38 -scenario 1` (display-remoting hint). Loose backstop so capped frames degrade gracefully instead of collapsing into blocks; no `global_quality` exists in this ffmpeg build, so ICQ-as-designed was replaced with CBR+clamps.

### Changed — adaptive ceiling, no flap (Phase 2.2)
- Bitrate floor lifts only after 15 consecutive healthy 2s windows (30s slow-up); bitrate changes coalesce to one encoder restart per 30s (a respawn + IDR each).

### Added — Main10 opt-in, validated on iPad (Phase 2.3)
- Viewer "Color depth" select (8-bit default) → `depth:` message → QSV `format=p010le,hwupload` (Main10 auto-selected, verified headless: ffprobe `profile=Main 10`). hvcC/codec-string follow the live SPS automatically; a rejecting decoder auto-reverts to 8-bit.
- **Validated on-device 2026-09-11:** server logged `10-bit` restart with `hvc1.2.20000000` description, zero `decerr`s, stream healthy — user reports visibly smoother gradients. Note: early windows show higher latency (160–220ms vs ~75ms on 8-bit); content-dependent, watch it — bgra→p010 CPU conversion + heavier decode are the suspects if it persists under identical motion.

### Ruled out (Phase 2.4)
- **4:4:4 chroma**: QSV accepts yuv444p input but emits **Rext profile**, which iPads software-decode (single-digit fps at native res). Confirmed via ffmpeg probe. No code change; not pursuing.

### Verified
- 70s full-frame animation: **zero AUs over 100KB** (was ~9/80s); loopback 58–60fps windows, max gap ≤35ms, zero >40ms stalls, `hevcBad=0`.
- iPad during same motion: fps climbs 33→41, received 58–60, **latency 72–78ms, max 129–152ms** (was 126–132 with 220ms starvation dips). No dips.
- 45/45 .NET + 8/8 viewer tests pass. App left running; mirror synced.

---

## 2026-09-11 — Fluidity pass: bitrate trim kept, `async_depth 2` rejected by measurement

### Changed (kept)
- **HEVC bitrate tier trimmed toward ~10 Mbps** (`ClientFrameSink.TierHevcBitrate`, quality ≤80 tier 5500→4000; ≈9,970 kbps at 2360×1640). Less Wi-Fi airtime and send latency on heavy content; encoder confirmed starting at 9970 kbps. Fluidity preferred over sharpness per user call.

### Tested and reverted
- **`async_depth 2` A/B (identical 20s motion, iPad telemetry): server throughput rose (received 50–58/s vs 43–56/s) but presented fps FELL (35–37 vs 41–43) and latency ROSE (103–119ms vs 78–90ms)** — the extra buffered frame arrives as burstier delivery (drops +32/25s vs +1/25s). Reverted to `-async_depth 1`. Lesson recorded: server-received fps is not presented fps; tune to the panel.

### Verified
- Final state (async_depth 1 + ~10 Mbps cap), standardized motion: presented **40–43fps**, latency **71–73ms**, `receivedFps` 55–57, `rafFps` 60, `decodeErrors` 0, encoder 9970 kbps. App left running.

---

## 2026-09-11 — Restored `-async_depth 1` (25fps/200ms regression → 41fps/85ms)

### Root cause (measured A/B on identical motion)
- After the decoder-death fix the iPad reported fps 20–35 at latencyMs 150–250ms, vs ~40fps/~80ms the day before. Server-side capture/compose/pipe-write were all fast (2–5ms), decode queue stayed 0–2, paint ~1ms — so the age accumulated inside QSV.
- Cause: removing `-async_depth 1` restored QSV's default 4-frame internal buffer ≈ 100ms+ of encoder latency at 30–40fps input. The flag was dropped on a flawed "pipeline parallelism" theory; `PushRawFrame` is a synchronous stdin write, so async_depth only ever added output buffering, never overlap.
- Fix: `HevcEncoderProfile.Qsv.LowLatencyArgs` is `-async_depth 1 -flags +low_delay` again. The `_hevcSendsInFlight >= 2` pipelined-send gate stays (neutral-or-better vs the old always-await: it only engages when the network is genuinely behind).

### Verified (standardized 20s cursor-sweep, iPad telemetry)
- Before: presented 20–35fps, latency 150–250ms (max 850ms).
- After: presented **41–43fps sustained**, latency **78–90ms** (max ~175ms), `receivedFps` 43–56, `rafFps` 60, `decodeErrors` 0, drops nearly frozen (+1/25s). Matches the previous good session.

---

## 2026-09-11 — Decoder-death freeze: truncated-NAL race + permanent JPEG fallback

### Root cause (confirmed in the live log)
- The iPad froze at ~20fps on 30–57 Mbps with stale 142ms latency because its WebCodecs decoder hit `Decoder failure` mid-stream (`decerr:` in the server log, immediately followed by `codec:intra`) and **permanently downgraded to JPEG**. JPEG at 2360×1640 is ~400KB/frame — Wi-Fi congestion, frozen-feeling video, adaptive quality driven to the 40% floor. The server was healthy; it was streaming exactly what the client asked for.
- The corrupt chunk came from the server's silence-flush timer: when the desktop went static for ≥120ms with a NAL split across ffmpeg stdout reads, the timer force-completed the **unterminated prefix as a whole NAL**. One truncated NAL kills Safari's decoder. Normal streaming (fps 33, healthy) right up to the failure confirms it was a single corrupt chunk, not encoder degradation.

### Fixed — server no longer emits unterminated NALs
- **`HevcStreamEncoder` silence timer now only flushes complete access units** — the `OnNal(completeNal)` force-complete of `_acc` bytes is removed. A NAL is emitted only after its terminating start code is witnessed. The final pre-silence frame is held until its terminator arrives (at most one frame stale after motion stops — invisible; a dead decoder is not).
- Cleaned up the now-unused `_lastDataTickMs` tracking.

### Fixed — one decoder error no longer kills the session
- **Viewer rebuilds the decoder and resyncs on a fresh keyframe** (`forceidr`) on decoder error, staying on HEVC. JPEG fallback now happens only after 3+ errors within 60s. Counters reset per minute (`hevcErrorCount`/`hevcErrorWindowStart`).

### Verified
- New `HevcParserTests` (split-read reassembly: nothing emitted before the terminator; whole length-prefixed NALs after) + 2 new viewer runtime tests (error→rebuild+`forceidr` staying on HEVC; 4 errors→`intra`). **45/45 .NET + 8/8 JS tests pass.**
- Live after restart: HEVC clean (113 AUs, `hevcBad=0`, single keyframe). The stuck tab predates the fix — **reload the iPad page once** to pick up the recovering viewer and a fresh HEVC session.

---

## 2026-09-11 — Recovery reliability pass (static desktop, backend state, kick) + 60/120 Hz benchmark

### Fixed — recovery could be starved by a static desktop
- **Missed frames now force a refresh even when nothing changes afterwards.** If the sink dropped the final changed frame under backpressure and the desktop then went static, the idle-skip returned before the owed full refresh could ever be composed — the client kept the stale frame forever. `TryBeginCompose` now treats `_missedFrames` as pending work.
- **`forceidr` and armed HEVC retries also force a compose.** The encoder restart / retry timer was only honored when another frame happened to arrive; on a static desktop that never happened. Pending HEVC work now bypasses both idle skips (the outer idle check and the "nothing in the crop" early return). JPEG sessions are unaffected (a stray `forceidr` cannot cause idle busy-work).
- **`_hevcRetryPending`** tracks an armed retry so the retry actually runs at its deadline; it is cleared on success and on permanent JPEG fallback.

### Fixed — DXGI/GDI capture state was shared
- **The producer now captures GDI fallbacks into a scratch `NativeFrame` and publishes (swaps) it only on success.** Previously DXGI and GDI wrote the same frame object: a DXGI timeout cleared `Captured`/`FullFrame` on the valid GDI frame, and a hard failure nulled its bitmap. DXGI recovery probes also run into the scratch, so a timeout during recovery can no longer erase the frame currently being served.

### Fixed — "Kick" was an unsafe race, not a session shutdown
- **Per-session cancellation.** Each WebSocket session now owns a linked `CancellationTokenSource`; the Console Kick button (via `ClientFrameSink.DisconnectByAddress`) invokes the session's disconnect handler, which cancels the token and closes the transport. It never touches the sink's buffers from the UI thread.
- **`ClientFrameSink.Dispose` is idempotent** and shuts the HEVC encoder under `_encoderLock`; the consumer's `finally` performs the single real cleanup.
- The input reader, consumer loop, and `syncack` sends now use the session token.

### Tuned — client presentation telemetry
- **`replacedRenderCount`**: the viewer now counts decoded frames that were replaced between vsyncs (latest-wins staging), included in the 5 s stats window. This is the true cost of server over-production on a slower display — the decode queue alone (peak 0–2) hides it.

### Verified — 60 Hz vs 120 Hz end-to-end (animated full-frame content, iPad + raw client)
- **Fix validated live**: the log captured `DXGI AcquireNextFrame failed … 0x887A0026 (ACCESS_LOST)` during a 120→60 Hz switch and recovered with `hevcBad=0` — the exact failure mode that previously produced the multi-hour frozen black frame.
- **120 Hz is real**: the IddCx display advertises and switches to `2360×1640 @ 120Hz`; the hub sustained **112–119 ticks/s** with `capture=1.3–3.7 ms`, `compose=0.3–0.8 ms` (1 sink), **1.4–1.7 ms capture with 2 sinks** at 120 Hz.
- **Client drops essentially stopped**: iPad `droppedRenderCount` grew by 8 over ~40 s of sustained animation (previously hundreds–thousands), `decodeQueuePeak` 0–2.
- **Bottleneck identified**: Intel QSV saturates at ~45–60 fps for native 2360×1640 when *every* pixel changes each frame (full-screen video case); 60 Hz request gave 49–54 fps windows at ~58 ms latency, 120 Hz request ~61 fps at ~73 ms. Desktop-typical dirty-rect motion stays far below this. This is encoder throughput, not transport or client decode.
- 43/43 tests pass (7 new: timeout-vs-ACCESS_LOST classification, static-desktop missed-frame recovery, forceidr on HEVC vs JPEG, kick plumbing while a frame is pending, idempotent dispose, partial dirty compose with color assertions).

---

## 2026-09-11 — Black screen root-cause fix, capture performance, and reliability pass

### Fixed (critical screen visibility / black screen)
- **DXGI `AcquireNextFrame` result handling fixed.** Previously, `acquireResult.Failure || desktopResource == null` was treated as "no desktop change", returning `true`. When Windows slept, locked, or switched display modes, `AcquireNextFrame` returned `DXGI_ERROR_ACCESS_LOST` (0x887A0026). The code swallowed the failure and returned `true` without ever recreating the duplication handle, permanently serving an outdated black bitmap for hours. Now, only `ResultCode.WaitTimeout` is treated as a normal timeout; any actual failure or null resource immediately releases duplication, forces `_needsFullRefresh = true`, and reports `false` so the hub can fall back and reinitialize.
- **Fixed `_busy` leak in `ClientFrameSink`.** When changes were outside the crop region and the cursor was static, `TryBeginCompose` returned `true` without resetting `_busy = 0`, permanently wedging the sink from accepting future frames.
- **Fixed backpressure dirty rect loss.** When a client dropped frames under backpressure, subsequent partial composes could miss dirty rects that occurred during skipped frames. Added `_missedFrames` tracking to guarantee a full refresh on the first compose following backpressure.
- **GDI fallback full-frame flag.** `ScreenCaptureService.CaptureNativeFrame` now sets `FullFrame = true` and clears `DirtyRects`, ensuring fallback frames are never ignored or mis-composed.

### Changed (performance & capture cost)
- **Direct-buffer capture (~6 ms → ~1.9 ms).** `DxgiCaptureService` now copies directly into a pinned memory buffer backing `NativeFrame.RawBuffer` and `_nativeBitmap`, completely eliminating `Bitmap.LockBits` and `UnlockBits` from both the DXGI capture thread and the fast compose path in `ClientFrameSink`. Total capture + compose overhead reduced to ~2.3 ms.
- **Instant GDI failover & fast 1s DXGI retry.** If DXGI encounters an error on any tick, GDI captures seamlessly on that exact same tick so zero frames are dropped. DXGI re-initialization retries every 1 second (previously 5s).
- **120 Hz precision pacing.** Added sub-millisecond spin-wait pacing in `FrameBroadcastHub` (`Thread.Sleep` for coarse sleep + `Thread.SpinWait` for the sub-millisecond remainder) to eliminate Windows 15.6ms timer quantum capping.
- **Client render-drop tuning.** Relaxed `onVideoFrameDecoded` queue drop threshold from `> 1` to `> 4`. Normal decoder pipeline buffering no longer prematurely drops frames or triggers spurious `forceidr` feedback loops, recovering full framerate.
- **Clock offset re-sync on RTT shift.** In `index.html`, latency estimation now maintains a rolling minimum RTT baseline and adapts clock offset from low-jitter probes, preventing latency HUD drift and artificial spikes.
- **Transient QSV recovery.** If FFmpeg exits or encounters a broken pipe, `ClientFrameSink` temporarily serves JPEG for that frame and automatically attempts to restart the hardware HEVC encoder after 2 seconds, rather than permanently locking the session onto JPEG.

### Added (Console UI)
- **Per-client kick.** Added a "Kick" button to each client in the Connected Clients list; clicking it calls `ClientFrameSink.DisconnectByAddress()` to immediately close the client's session and clean up resources.
- **Window position memory.** Console window saves `WindowLeft` and `WindowTop` to the registry on exit and restores them on launch if within active desktop bounds.
- **Live Performance Sparkline.** Added a real-time sparkline graph inside the Session Card displaying rolling FPS (green) and Latency (amber) with a 60fps reference guideline.

### Verified
All 36/36 unit tests pass. Live stream verified via harness: `streamprobe` confirms real full-color 2360×1640 desktop wallpaper pixels streamed (meanRGB=82.75, 590 KB/frame, 0 black pixels). Live HEVC stream verified (5.6 Mbps, `hevcBad=0`, 0 dropped chunks). Console UI verified via live snapshot. App left running stable.

---

## 2026-09-10 — Dirty-region compose, cursor hotspot cache, WebGL viewer

### Changed (capture/compose)
- **DXGI dirty-rect capture.** `CaptureNativeFrame` now reads present metadata (`GetFrameDirtyRects` / `GetFrameMoveRects`) and copies only dirty regions into the reusable bitmap. Moves, rect overflow, metadata errors, or empty region lists safely fall back to a full copy. `NativeFrame` carries `DirtyRects` + `FullFrame`.
- **Dirty-region compose.** The fast path copies only the output-space dirty set (capture regions scaled to the target, plus the previous and new cursor areas so a moved cursor leaves no ghost). Coverage ≥50%, settings changes, fresh bitmaps, and capture-full all fall back to full. Measured `compose` **~9 ms → ~2 ms** (2 sinks); the producer now ticks a steady **60/s** (was 56–58).
- **Cursor hotspot cached by handle.** `FillFrameCursor` no longer calls `GetIconInfo` + GDI alloc/delete on every tick; hotspots are cached (bounded, locked).

### Changed (viewer)
- **WebGL2 texture upload** (`texImage2D` of `VideoFrame`/`ImageBitmap` onto a fullscreen quad) with automatic Canvas2D fallback chosen once at startup. Same vsync `renderLoop`; context-loss listener attached.

### Verified
Build + 15/15 tests clean; harness: 60/s ticks, ~57 fps received, `hevcBad=0`/`jpegBad=0`, no "already locked", GOP holds (`keys=1`).

### Needs on-device confirmation (reload required)
- No stale-pixel ghosts or cursor trails from the dirty compose (sweep the mouse over static content and watch the trail/edges).
- WebGL color/smoothness vs Canvas2D — the console log line says which renderer is active; if anything looks off it will have fallen back.

---

## 2026-09-10 — iOS companion app finished (protocol, auth, single source)

### Fixed — the app could not work against the current server
- **Resolution sync used a dead message.** It sent `resolution:<w>x<h>@<hz>`, which the server never handled. Now sends `set_res:<w>,<h>,<hz>` (native pixels + refresh) so the virtual display aspect-matches, and announces `?codec=hevc` in the WebSocket URL to skip the JPEG startup flash.
- **Decoder never configured (black screen).** It waited for `hevc:description:<codec>:<base64>`, which the server never sends. Now parses the real `desc:<codec>|<base64-hvcC>` one-shot description. Handles the server `codec:intra` fallback notice instead of showing a black screen silently.
- **No authentication.** It ignored `auth:required`/`authreq:`, so password-protected servers hung forever. Now shows an in-app password prompt and answers with SHA-256(password + challenge) (CryptoKit), matching the server challenge-response; re-syncs settings on `auth:ok`.

### Added (robustness + integration)
- **Auto-reconnect** with capped exponential backoff (1–15 s) on close/failure.
- **Keyframe marking** (`kCMSampleAttachmentKey_NotSync`) from the packet flags for clean resync.
- **`forceidr` on foreground return** (mirrors the web viewer; prevents reference-frame corruption).
- **FPS/bitrate telemetry** (`stats:` every 5 s) so the Console's live clients table shows the iPad.
- **Password prompt UI**, working **FPS pill** (was hardwired to 0 — `onFpsUpdate` was never subscribed), **Quality selector** (50/65/80/90) in Settings, and a transient encoder-busy notice.

### Changed (repo)
- **Single source of truth.** Deleted the byte-identical `ios/OpenWinSidecar.swiftpm/` source folder; `ios/OpenWinSidecarClient/` is canonical. CI builds it and **regenerates + auto-commits `ios/OpenWinSidecar.swiftpm.zip`** (the file the server serves), so the distributable can never go stale.

### Verified
Careful review (no Swift toolchain on this machine); correctness rests on `ios-ci` (macOS) compiling and the on-device test. Server side unchanged except serving the refreshed zip.

---

## 2026-09-10 — Autonomous hardening batch (security, protocol, cleanup, UI)

### Added (security)
- **Same-origin policy for browser clients.** WebSocket upgrades and `/input` now reject a present-but-foreign `Origin` (must match the request Host). Native tools/scripts send no `Origin` and are unaffected (they still face the password gate).
- **Cross-connection auth backoff.** Failed password attempts push the client's next try out exponentially (2 s → capped) per IP; cleared on success. The 3-attempts-per-connection limit stays.
- **`/input` accepts the password via header** (`X-Access-Token` or `Authorization: Bearer`) in addition to the legacy `?pw=` query.
- **Plaintext auth fallback removed.** Only SHA-256(password + challenge) is accepted; the web viewer and iOS app already use challenge-response, and the auth test script does too.

### Fixed (protocol robustness)
- **`WsTextMessageAssembler` now reassembles fragmented text** (FIN=0 + continuations, RFC 6455 §5.4) and skips interleaved control frames without disturbing reassembly. Moved to its own internal file. Previously any fragmented message was silently lost.
- **`display:` validates the index** (logs and ignores out-of-range instead of half-applying).
- **HEVC send failures are logged** (first 3 per sink) instead of swallowed.

### Changed (cleanup + UI)
- **Removed ~330 lines of dead capture code** (`DxgiCaptureService.CaptureFrame`, `CaptureRawBgraFrame`, cursor P/Invoke block, JPEG helper) — the broadcast path only ever used `CaptureNativeFrame`.
- **Synced the `gh/` mirror** (`src/` → `gh/src/`).
- **Immediate-apply dropdowns** in the Console (Target resolution, Windows display scale) — the Apply buttons are gone, matching the web viewer. Handlers attach after initial population so startup doesn't trigger a change.

### Verified
33/33 unit tests pass (incl. new Origin/header/query + fragmentation tests); full solution builds clean (0 warnings); live HEVC stream unaffected; cross-origin upgrade rejected end-to-end (connection closed, no 101).

---

## 2026-09-10 — VSync-paced client presentation (micro-cut fix)

### Changed
- The viewer no longer paints on decode. Decoded frames (HEVC `VideoFrame`, JPEG bitmaps) are **staged and painted on `requestAnimationFrame`** (the display's vsync); the loop always shows the newest staged frame and paints nothing on a vsync with nothing new. Measured network pacing was bursty (~30 gaps >25 ms per 2 s on localhost), and drawing on arrival turned that jitter into visible cuts — vsync presentation decouples the two. Ownership transfers to the loop, which closes each drawable after paint (no leaks).

### Verified
Served page contains the loop; HEVC stream unaffected (`jpegFrames=0`, `hevcBad=0`). Needs an iPad page reload + on-device confirmation that the ~1 s micro-cuts are gone.

---

## 2026-09-10 — No periodic IDR hitch; encoder pre-warm; SkiaSharp JPEG

### Changed
- **GOP 4 s → 10 min (`fps * 600`).** The 4-second GOP emitted a ~200 KB keyframe every 4 s (~150 ms of Wi-Fi transmit time) — a periodic hitch. This is safe because the sink drops *input* frames (drop-oldest) rather than encoded access units, so the delta chain is always complete and a long-running client never needs periodic keyframes. IDRs now occur only at encoder start and on explicit client resync (tab-visible/backlog).
  - *Measured:* a 20 s HEVC session emits `keys=1` (was ~5 spaced 4 s apart); per-window `latMax` no longer spikes on a 4 s cadence.
- **HEVC encoder pre-warmed on connect.** `ClientFrameSink.WarmupHevc()` starts the client's encoder during the handshake via the same locked `EnsureHevcEncoder` path the consumer uses, so the ffmpeg spawn + QSV init overlap the handshake instead of the first frame. The first push adopts the warmed instance when targets match; a later settings change still re-inits transparently.
  - *Measured:* exactly one "Encoder started" per session (adopted, not re-spawned).
- **JPEG fallback now encodes with SkiaSharp (libjpeg-turbo)**, GDI+ kept as the fallback if Skia can't load. `SendJpegAsync` wraps the locked compose bitmap in an `SKPixmap` (zero-copy) and encodes.
  - *Measured:* JPEG `encode+send` **~10–12 ms → 3.5 ms**; wire format validated (`jpegBad=0`, SOI/EOI intact) across 253 frames.

### Verified
HEVC: single keyframe per 20 s session, single encoder start, steady ~48 ms glass-to-glass. JPEG: valid frames at ~3.5 ms. Full solution builds clean (0 warnings); 15/15 unit tests pass.

---

## 2026-09-10 — Smoother capture (cursor sprite) + 4:3 Console

### Fixed (performance — the remaining micro-lag / low-fps cause)
- **Cursor compositing is now a cached sprite blit.** The streamed host cursor was drawn every frame with GDI+ (`Graphics.GetHdc` + `DrawIconEx`), which measured **~3.7 ms/frame/sink** — enough to push the capture producer below 60 fps (56–58 ticks/s). The cursor is now rendered **once per shape/size** into an ARGB sprite and alpha-blended into the already-locked compose buffer. Measured: `compose` **~9 ms → 2.8 ms** (2 sinks), producer ticks a steady **60/s**, received fps **52–53 → 55–57**, latency **64 → 56 ms**.
- **Stable pacing.** When a capture tick overruns its slot, the producer now starts a fresh period instead of bursting to catch up — removing the catch-up judder. (The temporary compose timing instrumentation used to locate the cost was removed.)

### Changed (UI)
- Console window set to a **4:3 aspect ratio (1000×750)**; both columns became grids whose cards stretch to fill the height; QR enlarged to 108 px.
- **Live connected-clients table.** The Console now lists real clients — remote address, display, resolution, codec, fps, bitrate and latency — fed by a new `ClientFrameSink.Snapshot()` telemetry registry in the service (registered on connect, updated from the viewer's `stats:` message, removed on dispose). Previously it read legacy registry entries and always showed "No clients". The empty state is a 3-step getting-started guide. The clients card sits in the left column under the singular **Session** card and scrolls when several are connected.
- **Session card** (left column): state, display, selected hardware encoder, client count, and uptime — fills the previously empty column.

### Verified
Instrumented breakdown confirmed the cursor draw was the cost (copy ~1.8 ms, cursor ~3.7 ms per sink before the fix); builds clean; HEVC stream unaffected (`jpegFrames=0`, `hevcBad=0`).

---

## 2026-09-10 — Console UI restructure (structural pass)

### Changed
- **Consolidated the two duplicate resolution controls.** The raw `Custom W × H @ Hz` row moved out of the left column into a collapsed **"Custom resolution"** expander inside the Display card; the card's labels were renamed to **"Target resolution"** and **"Windows display scale"** (was "Resolution" / "UI scaling").
- **Maintenance moved out of the daily-use path.** The seven buttons (Restart driver, Extend, Mirror, Start/Stop service, Clean stale, Restart as administrator) now live in a collapsed **"Advanced / maintenance"** expander; `Start elevated` renamed to **"Restart as administrator"**.
- **Status chip simplified** to `Running` / `Stopped` (dropped the in-process/PID/memory readout that duplicated the hero state).
- **Access-state badge** added to the Connect card: amber **"Open access — anyone on this network can connect"** or blue **"Password protected"**, driven by the saved setting — makes the security posture visible (no password is still the default and remains supported).
- **Password field is now a masked `PasswordBox`.**
- Title bar de-duplicated (`OpenWinSidecar`; the version lives in the header), and **Preferences merged into a permanently visible Advanced / maintenance section** — one place for maintenance buttons, startup/tray/USB options, and the password.

### Follow-up (same pass) — 4:3, log preview, alignment
- Stretched cards **justify their content**: Connect content is vertically centered, Session rows distribute evenly across the card, and the clients empty state reads as an intentional 3-step guide.
- Client rows: address + live stats (`fps · Mbps · ms`) share the top line; display · resolution · codec sit on a second full-width line.
- **Window is 1000×750 (4:3).** Both columns became stretch grids whose middle cards fill the height (Connect left, log preview right), so the shape holds with no dead band.
- **Inline live log preview** in the right column (header + View logs button + scrollable pane fed from the same buffer, capped at 1500 visual lines). The separate resizable `LogWindow` remains for the full view with Copy/Clear.

### Verified
- Clean build (0 warnings); `--screenshot` confirms the restructured layout (`MainWindow.xaml`); live HEVC stream unaffected (`jpegFrames=0`, `hevcBad=0`).

### Deferred (see `docs/future-work.md` §8)
- A live **connected-clients table** with per-client stats (IP, resolution, codec, FPS, bitrate, latency) — needs client telemetry plumbed from the service to the Console.
- **Immediate-apply** for the resolution/scale dropdowns (drop the remaining Apply buttons) to match the web viewer.

---

## 2026-09-10 — Security: settings-save command injection fixed; password encrypted at rest

### Fixed
- **PowerShell command injection in `SidecarRegistryManager.SaveSettingsElevated`.** Settings (including the access password) were interpolated into a PowerShell command line, so a password containing `'`, `;`, or backticks could break or inject commands. The script is now built with **single-quote escaping** (`RegistrySaveScript.Quote`) and handed to PowerShell via **`-EncodedCommand`** (Base64 of UTF-16LE), so no value is ever parsed as command syntax. Script construction was extracted to a small testable `RegistrySaveScript`.
- **Access password is DPAPI-protected at rest.** It is stored as `EncryptionPasswordProtected` (machine-scope DPAPI with an app-specific entropy) instead of plaintext `EncryptionPassword`. The legacy plaintext value is still read as a fallback for pre-existing installs and is **cleared on the next save**; if DPAPI is unavailable it falls back to plaintext with a logged warning. Adds the `System.Security.Cryptography.ProtectedData` package.

### Added (tests)
- `RegistrySaveScriptTests` — 5 tests: quote escaping (including a full injection attempt), plaintext-clear when protected, plaintext fallback when DPAPI is unavailable, and Base64/UTF-16 round-trip. **15/15 tests pass.**

### Verified
Solution builds clean (0 warnings); live HEVC stream is unaffected (`jpegFrames=0`, `hevcBad=0`).

### Still outstanding (deferred: see `docs/future-work.md` §4)
- The permanent **plaintext auth fallback** in `AuthenticateSessionAsync`.
- `/input` carrying the password in the **URL query**.
- No **`Origin` check**; `Access-Control-Allow-Origin: *`; no cross-connection auth throttling.

---

## 2026-09-10 — WebTransport evaluated; deferred work documented

### Added
- **`docs/future-work.md`** (and the `gh/docs/` mirror): a roadmap of prototyped/evaluated/deferred work with the reasoning so it isn't re-attempted blindly — WebTransport/QUIC, GPU-to-GPU, shared encoder, security hardening, multi-vendor encoder validation, FFmpeg bundling, and the feature roadmap.

### Investigated
- **WebTransport over HTTP/3.** Kestrel ships WebTransport as a preview feature on MsQuic + HTTP/3, so the protocol layer is handled. A spike confirmed the server side works (`alt-svc: h3`, a cert-hash-pinned session). It is blocked on the browser's **secure-context/HTTPS requirement** — a plain `http://` page cannot use WebTransport, and `serverCertificateHashes` relaxes cert *trust* only, not the HTTPS requirement — plus cert friction for non-technical users. Kestrel also lacks datagrams (workaround: one unidirectional stream per frame). Full write-up: `docs/future-work.md` §1.

### Reverted
- **Closed-loop Wi-Fi congestion control.** A frame-rate throttle driven by client-reported latency was implemented and then reverted: latency is feedback-contaminated and normal Wi-Fi jitter tripped it, dropping the iPad to 5 fps in a self-reinforcing loop. TCP drop-oldest backpressure + the 64 KB send buffer remain the safe open-loop responses. Details: `docs/future-work.md` §3.

---

## 2026-09-10 — Vendor-agnostic hardware HEVC encoder (multi-GPU groundwork)

### Changed
- **`HevcQsvStreamEncoder` → `HevcStreamEncoder`**, with the vendor-specific FFmpeg arguments moved into `HevcEncoderProfile` (QSV / NVENC / AMF). Adding NVIDIA or AMD support is now *selecting a profile*, not integrating a different SDK — FFmpeg already exposes every vendor behind `-c:v hevc_<vendor>`, and the Annex-B/hvcC parsing is vendor-agnostic and unchanged.
- **Startup probe** (`HevcEncoderProfiles.Detect`, once per process) runs a one-frame encode against each candidate in priority order (**QSV → NVENC → AMF**) and picks the first that actually works; if none do, the sink falls back to JPEG intra exactly as before. Software HEVC (libx265) is deliberately *not* offered — it cannot keep up at native resolution, so JPEG is the better fallback.
  - Profile args: QSV `format=nv12,hwupload` + `-preset veryfast -async_depth 1 -flags +low_delay` (validated); NVENC `format=nv12` + `-preset p1 -tune ll -rc cbr` (unvalidated); AMF `format=nv12` + `-quality speed -usage ultralowlatency -rc cbr` (unvalidated). An unvalidated profile whose full argument set fails the probe can only fall back to JPEG — it can never ship a broken stream.
- Log prefix `[HEVC QSV]` → `[HEVC]`; the encoder-start line now names the selected vendor.

### Verified
On the Intel Arc machine the probe selects `Intel QuickSync (hevc_qsv)`; the HEVC stream is unchanged (`jpegFrames=0`, `hevcBad=0`, ~55–70 ms including cold start). Solution builds clean and the unit tests pass. NVENC/AMF paths remain unvalidated pending that hardware.

---

## 2026-09-10 — Viewer extracted to an embedded file + unit tests + CI

### Changed
- **The WebCodecs viewer is no longer a C# string literal.** The ~1,500-line HTML/CSS/JS page moved from `SidecarTcpServer.cs` to `src/OpenWinSidecar.Service/wwwroot/index.html`, embedded into the assembly (`EmbeddedResource`) and loaded once per process by `GetViewerTemplate()`. Only the display `<option>` list is substituted per request, via a `<!--DISPLAY_OPTIONS-->` token. `SidecarTcpServer.cs` shrank from **2,654 → ~1,080 lines**.
  - *Why:* the page was impossible to lint, format, or review as a diff, and every JS edit risked the C# brace-escaping (`{{`/`}}`). It is now a normal file with syntax highlighting; the extraction converted the escaped braces back to literal and preserved the rest of the file byte-for-byte.
- **`WebCodecsFraming.WriteHeaders` split out of `SendBufferedBinaryAsync`** as a pure function (no I/O), and **`WsTextMessageAssembler` moved to its own `internal` file** — both so the wire logic is reachable from tests (`InternalsVisibleTo`).

### Added
- **`tests/OpenWinSidecar.Service.Tests`** (xUnit) — 10 tests pinning the parts most likely to silently corrupt a stream:
  - `WebCodecsFraming`: the 15-byte WebCodecs header layout (`BuildPacket`) and `WriteHeaders` for all three WebSocket length forms (2-byte, 16-bit extended, 64-bit extended), including the reserved-25-byte front space and payload integrity.
  - `WsTextMessageAssembler`: coalesced frames in one read, a frame split across per-byte reads, extended-length text, and a binary frame followed by text (desync guard).
- **`.github/workflows/dotnet-ci.yml`** — restores, builds `Release`, and runs the tests on `windows-latest` (previously only `ios-ci.yml` existed; there was no .NET CI at all).

### Verified
`dotnet test` — **10/10 pass** in Debug and Release; full solution builds `Release` clean (0 warnings). Live smoke test after the refactor: HEVC still `jpegFrames=0` / `hevcBad=0`, and the viewer is served from the embedded resource with the display options substituted.

---

## 2026-09-10 — Streaming hot-path allocation elimination (performance)

### Changed
- **JPEG send path is now zero-copy/zero-alloc per frame.** The composed frame is encoded into a reused buffer with 25 bytes reserved in front for the WebCodecs (15 B) + WebSocket (≤10 B) headers; the headers are filled in place and the entire slice goes out in a single socket write (`WebCodecsFraming.SendBufferedBinaryAsync`). This removes the per-frame `MemoryStream.ToArray()` and `BuildPacket` allocations/copies the old path paid for every JPEG frame.
  - *Why:* a 2360×1640 desktop produces ~440 KB intra frames; at 60 fps the old path allocated/short-lived ~3 buffers per frame (~50 MB/s of Gen0 churn) on the latency-critical path.
  - *Measured:* steady-state managed allocation dropped from **~37 MB/s to ~0.05 MB/s** (single JPEG client); wire framing verified byte-shape-correct against a raw client with **zero** malformed JPEG SOI/EOI, including two concurrent clients.
- **`FrameBroadcastHub` capture loop no longer allocates per tick.** The producer reuses one `List<ClientFrameSink>` scratch buffer (`FillSinksFor`) instead of allocating up to four lists per tick at 60 Hz and re-scanning the sink set several times per frame.
- **Hardened GDI+ lock discipline in `ClientFrameSink.TryBeginCompose`.** It locked the native capture bitmap and then the compose bitmap; if the second `LockBits` threw, the native lock was never released and every subsequent `CaptureNativeFrame` failed with *"Bitmap region is already locked"* — permanently wedging capture. Locks are now nested `try/finally` so the source is always released.
- **HEVC "first VCL NAL" log now fires once per encoder** instead of once per access unit (~60 console writes/s during video).

### Fixed (found while debugging the above)
- **`ConsoleLogForwarder` infinite recursion / stack overflow.** A log subscriber that writes back to `Console` (the headless `ServiceProgram.Main` path does exactly this: `host.OnLog += Console.WriteLine`) re-entered the forwarding writer forever. Added a thread-static reentrancy guard; a callback-triggered console write no longer re-raises the callback.

### Verified
Headless harness (in-process server) + raw TCP WebSocket client: JPEG path streams hundreds of frames with no corruption (single and two concurrent clients), HEVC path delivers the `desc:` hvcC, a keyframe access unit starting with a VPS (NAL type 32) and then small delta AUs. Full solution builds clean (0 warnings). HEVC decoder ingest still uses a managed staging buffer (direct-from-locked-bitmap write was prototyped but reverted: holding the GDI+ lock across the blocking FFmpeg pipe write widened the lock-ordering bug above).

### Fixed — mouse-move lag / latency buildup (live iPad session)
User report: *"very laggy, not fluid, especially when I move the mouse."* Three compounding causes, all fixed:

- **`forceidr` feedback loop.** The viewer counted *render* drops (an intentional latest-wins behaviour, not corruption) and requested a fresh IDR on >30 drops/5 s. Mouse movement raised the drop count, so the server **tore down and respawned the FFmpeg/QSV encoder every ~5 s** — a 1–2 s freeze plus a ~200 KB keyframe, repeatedly.
  - *Fix:* the viewer now requests an IDR only on a genuine decoder backlog (`decodeQueueSize` peak > 24 ≈ 0.4 s) and at most every 10 s; `ClientFrameSink.ForceIdr()` is additionally rate-limited to once per 8 s server-side.
  - *Fix:* the viewer renders through one queued frame (`decodeQueueSize > 1`) instead of dropping on any non-zero queue, so normal jitter no longer costs a rendered frame.
- **HEVC had no backpressure.** For JPEG the send is synchronous inside the sink, so the producer's drop-oldest policy engages under network backpressure. For HEVC the access-unit send ran fire-and-forget on the encoder reader thread, so the sink never reported "busy": the producer kept feeding FFmpeg at 60 fps while the unsent backlog piled up in the FFmpeg pipe, the TCP buffers, and the client decoder queue. Measured **`decodeQueuePeak` up to 16 frames (~266 ms)** with `encode+send` spiking to 110–162 ms/frame.
  - *Fix:* `PushToHevcAsync` now awaits the previous access unit's asynchronous send before pushing the next raw frame (`_hevcSendTask`), so a slow client keeps the sink busy and the producer drops input frames (already-encoded AUs are never dropped, so the delta chain stays intact). `decodeQueuePeak` fell to **1–3** and encoder restarts to **0** during a scripted 60 Hz cursor sweep of the virtual display.
- **Oversized TCP send buffer.** `SendBufferSize` was 512 KB, letting ~0.5 s of video sit in the kernel send buffer that the client would render late. Reduced to 64 KB so a slow Wi-Fi leg applies drop-oldest backpressure instead.
- **Spurious-wakeup concurrency bug in the sink handoff (session killer).** `ProcessAndSendAsync`'s finally "re-armed" the frame semaphore whenever `_composeWitnessed` was set — but the producer sets that flag for the very compose that started the send, so the consumer always performed a **second `ProcessAndSendAsync` with `_busy == 0`**. During that extra send the producer was free to `LockBits`/draw into the same `_composeBitmap` the consumer was encoding, so GDI+ threw *"Bitmap region is already locked"*, the exception unwound the consumer loop, and the WebSocket session was torn down. The iPad was silently reconnecting on every mouse-driven frame burst (observed: 15 sessions, 15 `already locked` closes).
  - *Fix:* removed the re-arm and `_composeWitnessed`. The `_busy` flag is set by the producer before signalling and cleared only after the send completes, which already prevents overlap; a counter race can only overwrite the pending bitmap (drop-oldest), never lose a wakeup.

*Verified live:* scripted cursor sweep over `\\.\DISPLAY5` (60 moves/s, 15 s) with a real iPad client — **0** `already locked` closes, **0** session reconnects, **0** encoder restarts, `decodeQueuePeak` 0–1, render drops ~4/window, 49–54 fps. Input injection itself measured fast (0.30 ms/move, `InputDispatcher`), so the lag was entirely the video backpressure/handoff path.

### Added — real glass-to-glass latency measurement
- **Latency probe + HUD.** The viewer sends `sync:<clientMs>` every 2 s; the server answers `syncack:<clientMs>,<serverUs>` with its monotonic clock. From the round-trip the client derives the clock offset (`offset = serverMs − clientMs`) and measures every rendered frame as `now − (frameTimestamp/1000 − offset)`. The average shows as a small `· NNms` next to the FPS in the status pill and is included in the `stats:` telemetry. This is what turned "feels laggy" into a number.

### Fixed — encoder buffering (the big remaining latency term)
- **`-async_depth 1` was commented but never actually passed to FFmpeg.** The code comment promised it; the argument string omitted it, so QSV ran with the default `async_depth 4` and queued four frames internally. Added `-async_depth 1 -flags +low_delay`.
  - *Measured server-side capture→send age:* **~125 ms → ~50 ms**.
  - *Measured glass-to-glass (client):* **~133 ms → ~62 ms** at 2360×1640 HEVC, 60 Hz cursor sweep.
  - Flags and the `async_depth` range were verified against the installed Gyan FFmpeg 9.0.1 + Intel Arc driver before shipping; `-bufsize` and `-fps_mode passthrough` were tested and made no difference (kept out).

### Changed — no more per-connect JPEG flash
- **Codec is announced in the WebSocket URL** (`/?codec=hevc`); the server applies it before composing the first frame, instead of waiting for the `codec:hevc` text message. Raw-client verification: `jpegFrames=0` on an HEVC session (previously always exactly one ~440 KB JPEG frame per connection).

---

## 2026-09-06 — Minimal viewer status pill + decluttered settings menu (iPad)

### Changed
User feedback: the status pill carried too much (FPS + codec + resolution), and the settings menu had too many options.

- **Status pill slimmed to `[connection dot] FPS ⚙`** — codec and resolution indicators removed from the pill (the codec state is visible in the menu, resolution is now auto-matched by the server anyway). The FPS slot doubles as the connection indicator: `⟳ reconnecting` while the socket is down.
- **Settings menu reorganized** into essentials + collapsible advanced:
  - Always visible: **Display** (target monitor), **Quality**, **Cursor** mode, and Actions (**Fullscreen**, **Keyboard**).
  - Moved under a collapsible **"More options"** section: Resolution presets, Video codec, Windows display scale, UI magnification, Fit/Stretch.
  - Removed the redundant "Apply Res" button (the resolution select applies immediately).
  - Emoji stripped from labels; codec options renamed ("HEVC / H.265 (hardware)", "Intra JPEG (fallback)").
- The pill still opens the settings menu on tap, and the dot pulses as the live indicator.
- *Verified:* served viewer HTML contains the new pill + advanced-details structure with zero stale references; streaming smoke test passes after the change.

---

## 2026-09-06 — Full-screen on iPad: virtual display aspect-matches the client

### Fixed
- **The stream letterboxed on iPads** (user: "not using the full iPad screen"). Root cause: the virtual display ran 2560×1440 (16:9) while iPads are ~3:2 — the viewer letterboxed with black bars no matter which preset was chosen, because `set_res` only resized the *stream*, never the *display mode*. Physically impossible to fill.

### Fixed / Added
- **`set_res` now aspect-matches the display**: `DisplayResolutionManager.MatchVirtualDisplayToClient` picks the supported VDD mode closest to the client's screen aspect (tie-break: largest area → native-class sharpness) and applies it. All iPad modes were already advertised by the driver.
- **Idempotent**: skips when the current aspect already matches within 0.5% — repeated client syncs (fullscreen, resize, tab-visible) cannot cause mode-change churn.
- **Mode transitions recover cleanly**: DXGI duplication invalidates on mode change → parser/producer reinit + GDI seed handle it; encoder restarts at the new resolution automatically.
- *Verified live:* client `set_res:1180,820` → driver switched to **2360×1640 @ 120 Hz**, stream recomposed at the client aspect, repeated syncs caused no churn.
- *Known limitation:* two iPads with different aspects on the same virtual display will fight over the mode (last wins).

---

## 2026-09-06 — Light professional theme (Console)

### Changed
- **Full light-theme reskin of the Console** per user feedback ("less darker"): white cards on a soft gray window (`#F2F3F6`), dark slate text, same accent blue. The two-column no-scroll layout, all control names, handlers, and behaviors are unchanged — a reskin, not a restructure.
- **Contrast re-verified for light backgrounds** (WCAG ≥4.5:1 text): primary `#1B1E26` on white 15.5:1; secondary `#5C6270` on white 6.1:1; white on accent `#2563EB` 5.2:1; state text `#1D5CC0` on `#E3EDFC` 5.3:1 and `#8A6300` on `#FCF3D9` 4.9:1; section labels `#676D79` on window 4.6:1.
- **State colors restated for light backgrounds**: running/connected = blue `#1D5CC0` on light-blue `#E3EDFC` with `#B9CDF0` border; stopped/warning = dark amber `#8A6300` on `#FCF3D9` with `#E3D49E` border. Shape coding (filled square / hollow circle) and the blue/yellow axis are preserved.
- **Soft shadows tuned for light UI**: neutral blue-gray (`#33415C` at 14% opacity, downward) on cards; blue-tinted glow on the hero card; QR plate keeps white with a visible border.
- **Header**: white bar, blue gradient icon tile showing the app icon image (loaded via the existing code-behind icon path — deliberately *not* a XAML pack-URI, see the 2026-09-03 crash note), status pill, Open Viewer / Refresh.
- **Log pane** flipped to light: `#F7F8FA` background, dark slate text.
- *Verified:* clean build; self-screenshot shows the full light theme rendering (header chip "Running · PID", hero with red Turn-off DangerButton, endpoint combo with friendly Wi-Fi label, QR, tabs).

---

## 2026-09-05 — Autonomous improvement batch (robustness, auth, keyboard, autostart)

Ran while the user was away; every item is a previously identified improvement, implemented and verified locally. iPad HEVC validation is still the outstanding on-device test (service instrumented and ready — see the 2026-09-05 entry below).

### Added
- **Working "Start with Windows"**: the Console preference previously only wrote a registry flag nothing read. Now saving Preferences with it checked creates an HKCU Run key (`OpenWinSidecar` → `OpenWinSidecar.Console.exe --autostart`); at sign-in the Console brings the virtual display and streaming service up automatically (2.5 s settle delay first). Unchecking removes the key.
- **Auth hardening — SHA-256 challenge-response**: the handshake previously sent the plaintext password over the WebSocket. Now each attempt gets a fresh random challenge (`authreq:<challenge>`) and the client answers with `SHA-256(password + challenge)` hex — a network sniffer only ever sees single-use hashes. Plaintext still accepted as a legacy-tools fallback (useless for replay); the match type is logged. The web viewer embeds a pure-JS SHA-256 (crypto.subtle is unavailable on non-secure `http://LAN-IP` contexts — this is why WSS wasn't the fix). Self-test verified against known vectors via node (`tools/sha256_test.js`).
- **Force-IDR on tab-visible (HEVC robustness)**: Safari may evict decoded-frame state while the iPad tab is backgrounded; the next delta would then reference frames the fresh decoder never had → corruption until the next IDR (up to 4 s at GOP 240). The client now sends `forceidr` on `visibilitychange`, and the sink restarts its encoder (cross-thread-safe via a flag consumed on the consumer thread) so the next frame is a fresh IDR.
- **HEVC concurrent session cap (4)**: each client encoder is an ffmpeg process holding QSV GPU surfaces; overflow clients get JPEG with a logged reason. Static counter with a per-instance counted flag (a naive decrement would have corrupted the shared count — Initialize calls Shutdown internally).
- **tools/ additions**: `contrast_audit.ps1` + `contrast_candidates.ps1` (the WCAG measurement scripts used for the contrast passes), `make_icon.ps1` (regenerates `app.ico` per-size), `qsv_pace_test.ps1` (proves QSV output flows with paced input), `sha256_test.js`, `sync_gh_mirror.ps1` (robocopy `src/` → `gh/src/` — the GitHub mirror must be synced manually or it drifts).

### Fixed
- **Endpoint classifier false positives**: VPN tokens `tap`/`tun` were substring matches — an adapter named "Desktop…" would classify as VPN. Now word-boundary token matching for the ambiguous tokens.
- **Crash-log path** was hardcoded to `C:\Users\JulianB\…`; now appends next to the executable with a temp-folder fallback, so earlier exceptions survive later ones.
- **Keyboard forwarding** now sends physical-key (`e.code`) → Windows VK mappings instead of the deprecated `e.keyCode` (which breaks under IME input with 229 and shifts with active layouts). Falls back to `keyCode` for unmapped codes.

### Verified
Clean build; challenge-response auth end-to-end (wrong hash → denied, correct hash → ok, "challenge-response" match logged, plaintext fallback path still open for legacy tools); `/input` 403/200 guards; wire-integrity test clean.

---

## 2026-09-05 — HEVC on-device test instrumentation

### Added
- **Client decoder-error reporting**: Safari's `VideoDecoder` error handler now sends `decerr:<message>` back to the server, which logs it — the primary diagnostic for whether iPads hardware-decode the HEVC stream (previously a failure was silent: the client just fell back to JPEG with no reason recorded server-side).
- **Codec-request logging**: the server logs which codec the client requests per session.
- Password rotated on this machine (`[REDACTED — see note]` → user-set); docs and test scripts synced. Access password managed via Console → Preferences.

### Verified
Local HEVC regression after the changes: authenticated client receives the Main-profile IDR (93 KB, hvcC `hvc1.1.40000000.L120.90`), idle-skip behavior normal. Ready for on-device iPad validation.

---

## 2026-09-05 — Two-column no-scroll layout + network endpoint intelligence (external agent session, audited & committed)

Work performed in a parallel agent session, found **uncommitted** in the working tree afterward; audited, fixed, verified, and committed here.

### Added (by the external agent)
- **No-scroll two-column Console layout** (`MainWindow.xaml` rewrite): left column = iPad-display hero + Connect (URL/QR/endpoint) + Maintenance; right column = Display tuning + Connected clients + a Preferences/Service-Log segmented toggle. Everything fits without scrolling.
- **Network endpoint intelligence** (`NetworkDiscoveryService.cs` + `NetworkEndpointInfo.cs` + adapter dropdown in the Connect card): interfaces are classified and prioritized — Apple USB tethering (100) → Wi-Fi LAN (90) → wired Ethernet (80) → VPN/overlay (30) → internal virtual switches (10, deprioritized). Fixes the bug where the connect URL defaulted to the **Hyper-V vEthernet `172.26.192.1`** address, which external devices cannot reach; the URL/QR now default to the real Wi-Fi IP.
- **Power-toggle state fix** (`SpacedeskManager.cs`, `VirtualDisplayManager.cs`, `MainWindow.xaml.cs`): the toggle previously stuck on "Turn off" because refresh considered the driver node "Started" even with the service down. Now `IsVirtualDisplayActive = serviceRunning && driverEnabled`, stop/start set the flag immediately, and the button decides from content + operational state. Also: `devcon` instance syntax corrected (`@ROOT\DISPLAY\0000`), and driver-state detection checks `Disabled` status and the monitor enumeration.
- **Startup-crash fix for the app icon** (`App.xaml.cs`, `MainWindow.xaml`): `Icon="app.ico"` in XAML resolved as a `pack://application:` URI, which only finds *Resource* items — `app.ico` is deployed as Content, so startup threw an unhandled `XamlParseException` and the window never appeared. The XAML attribute is removed; the icon is applied programmatically in the constructor via `LoadAppIcon()`. Global unhandled-exception logging to `console_crash.log` was added alongside.
- **`run_console.bat`** quick launcher; `run_service_admin.bat` uses `%~dp0` instead of a hardcoded path; `System.ServiceProcess.ServiceController` package added; both `src/` and the `gh/` distribution mirror kept in sync.

### Fixed (this audit)
- **Adapter combo showed the raw type name** (`OpenWinSidecar.Core.Models.NetworkEndpointInfo`) instead of a friendly label: `DisplayMemberPath` doesn't render through the custom ComboBox template's selection-box ContentPresenter. Replaced with an explicit `ItemTemplate` binding `DisplayLabel`.
- **Tofu glyphs in maintenance buttons** (variation-selector emoji rendering as boxes at button size): stripped to plain text labels, consistent with the emoji-free convention.
- Minor: `ClassifyEndpoint`'s substring `tap`/`tun` VPN match can false-positive on names containing e.g. "Desktop" — left as-is (cosmetic category label only), noted for future cleanup.

### Verified
Clean build; self-screenshot shows the two-column layout, the friendly "📶 Wi-Fi LAN: 192.168.1.12 (WiFi BE200)" combo entry, correct `http://192.168.1.12:8080` URL/QR, clean button labels; service streams with the display on.

---

## 2026-09-03 — Visual revamp (professional dashboard polish)

### Changed
Full visual pass over the Console, building strictly on top of the WCAG-measured palette and the fixed templates from the accessibility/contrast work — no color or template regressions.

- **Branded header**: 42 px rounded icon tile (gradient + glow, using the app icon), product name with tagline ("Windows virtual displays, streamed to Apple devices"), status now a **pill chip** (bordered, colored background) carrying the shape-coded state marker inside — more legible than the old bare dot.
- **Hero card**: the iPad-display toggle is now a visual anchor — subtle blue-tinted gradient, larger 24 px state text, glow shadow. It's the one primary control, so it now looks like one.
- **Cards**: unified `Card` style (10 px radius, soft drop shadows, brighter border), uppercase section labels (CONNECT / DISPLAY SETTINGS / CONNECTED CLIENTS / ADVANCED) grouping them into a scannable hierarchy.
- **Controls**: rounder buttons (8 px) with clearer hover/pressed states, ComboBox dropdowns with shadow + fade, focus-friendly paddings, generous spacing rhythm throughout.
- *Verified:* self-screenshots (normal + expanded) show every element rendering: header chip, hero, QR card, expanders with light headers. Two build bugs caught and fixed en route: a duplicate `CardBorder` resource key (brush vs. style) and a double-set `Background` on the hero card.

---

## 2026-09-03 — Text-contrast pass (WCAG-measured)

### Changed
Follow-up to the color-vision pass after feedback that text still read badly. Measured every text/background pair with a WCAG contrast-ratio script (`tools/contrast pairs` logic in the session log; thresholds: ≥4.5:1 text, ≥3:1 non-text) instead of eyeballing.

- **Accent button failed AA**: white on `#3B82F6` measured **3.68:1**. Accent darkened to `#2563EB` → **5.17:1**. This is the primary "Turn on/off iPad display" toggle and every Apply button.
- **Secondary text brightened**: `#9A9AA3` (5.87:1 — passed AA but was the dimmest text in the app) → `#B8B8C2` (**8.33:1**, AAA). Secondary text carries most of the UI's explanatory copy, so this is the largest perceived fix.
- **Card borders were 1.3:1** — cards barely separated from the window background, making the whole window read as one low-contrast mass. Borders raised to `#4A4A58` (~2.1:1; the practical ceiling before borders stop looking like borders on this background) and every template grey (button backgrounds/hovers, ComboBox borders, hover states) raised one step to match.
- *Verified:* clean build; self-screenshot shows outlined cards, brighter secondary text, deeper accent button.

---

## 2026-09-03 — Color-vision accessibility pass (Console + web viewer)

### Changed
- **State colors moved to the blue/yellow axis** (Console `Good`/`Bad` brushes and every web-viewer badge/dot/error color).
  - *Why:* the UI encoded state by hue alone in red/green — invisible to the ~8% of men with red-green color-vision deficiencies. Running/connected was green (`#34D399`), stopped/error red (`#F87171`/`#EF4444`) — two hues that collapse into near-identical olive/brown under deuteranopia/protanopia.
  - *Palette:* running/connected/OK → light blue `#7CB7FF` on navy `#1B4A75`/`#1B3A5C`; stopped/warning/fallback → yellow `#F2C94C` on dark olive `#4A3F14`. Blue and yellow stay distinct under *all* common deficiencies.
- **Shape + text redundancy, never hue alone:**
  - Console service indicator: filled **square** (rounded 2px) = running, hollow **circle** = stopped, plus the "Running · PID…" / "Stopped" text.
  - Web-viewer badges carry distinguishing prefixes: `⟳ RECONNECTING`, `⚠ JPEG INTRA` (fallback), plain `HEVC GPU`/`JPEG INTRA` (normal) — the icon/marker makes states readable with color entirely absent.
  - Auth error text keeps its "Wrong password — try again." message (text carries the meaning; color now high-contrast yellow instead of dark red).
  - Connected-clients badge: was dark-green text on dark-green chip (illegible even for full color vision) — now outlined blue chip on navy.
- *Verified:* self-screenshot of the Console shows the filled blue square + Running text in the header and the outlined connected chip; solution builds clean. Remaining hardcoded colors audited — greys on dark backgrounds, all within contrast range.

---

## 2026-09-03 — App icon for window, exe, and tray

### Added
- **Custom application icon** (`src/OpenWinSidecar.Console/app.ico`) — a rounded dark plate with the accent-blue monitor, white signal dot + wave arcs, and stand: a remote display being watched from elsewhere, matching the Console theme.
  - *Why:* the window, taskbar, Alt-Tab, and tray all showed the generic .NET executable icon; the tray in particular is where this app lives, so the default `SystemIcons.Application` was the most visible placeholder in the product.
  - *How:* generated programmatically (GDI+ script, per-size redraws at 16–256 px rather than scaling, solid fills for 16-px legibility; PNG-compressed entries in one multi-resolution ICO). Wired in three places: `<ApplicationIcon>` in the csproj (embeds in the exe → window/taskbar), `Icon="app.ico"` on the main Window, and the tray `NotifyIcon` loads it via `LoadAppIcon()` with base-directory/source-tree fallbacks so it works both from `dotnet run` and a published exe. The QR popup window inherits the main window's icon. The csproj also copies `app.ico` beside the exe (`Content` + `CopyToOutputDirectory`) because `ApplicationIcon` alone only embeds, it doesn't deploy the file the tray needs.
  - *Verified:* `ExtractAssociatedIcon` on the built exe returns the custom icon; the deployed `app.ico` loads as a valid `System.Drawing.Icon`; build clean.

---

## 2026-09-03 — Connect-by-QR + project history under git

### Added
- **QR connect code in the Console** (`MainWindow.xaml` / `MainWindow.xaml.cs`).
  - *Why:* typing `http://192.168.1.x:8080` on an iPad keyboard is the single worst first-run experience. The QR encodes exactly the URL already shown in the Connect card (auto-regenerated when the LAN endpoint changes during the 3s poll), so camera-scan → Safari → streaming with zero typing.
  - *How:* `QRCoder` NuGet package (pure managed, fully offline — no third-party QR service ever sees the URL). Level Q error correction, dark modules on a white card (QR needs light-on-dark contrast even on the dark theme). Click the code for a 420×420 across-the-room popup.
  - *Verified:* self-screenshot (`--screenshot` diagnostics flag) shows the card rendering in the Connect panel.
- **`tools/` diagnostics kit** — the PowerShell test clients used to verify the streaming protocol moved from temp folders into the repo (`ws_smoke_test.ps1`, `ws_auth_test.ps1`, `ws_wire_test.ps1`, `ws_hevc_validate.ps1`, `enum_vdd_modes.ps1`).
  - *Why:* these are the project's acceptance tests; they caught every regression listed below and belong with the code.
- **Git repository initialized** with the full project history committed in explanatory commits (see below).

### Changed
- `.gitignore` extended: `*.log`, `scratch_screen_*.jpg`, `vdd_control.zip`/`vdd_x64.zip` (71 MB of driver archives — the extracted files under `drivers/VDD/` are what the code actually uses).

---

## 2026-09-02 — Safari-ready hardware HEVC (hvcC + Main profile + long GOP)

### Fixed / Added
- **NAL continuation bug in the Annex-B parser** (`HevcQsvStreamEncoder.cs`).
  - *Symptom:* headers (VPS/SPS/PPS) parsed but zero video frames delivered.
  - *Cause:* any NAL spanning two pipe reads was silently discarded — after buffer compaction the next scan pass forgot it was inside a NAL, so the ~90 KB IDR slice (spanning 3 reads of 32 KB) was lost. Small NALs contained in a single read worked, which is why headers still appeared.
  - *Fix:* explicit `_inNalContinuation` state carried across passes.
- **Idle-starved access-unit stall.** The last picture of a quiet desktop never flushed because an AU is only complete when the *next* one's first-slice NAL arrives. With the idle-skip (#09-02) starving the encoder on static desktops, that meant the one and only frame never shipped.
  - *Fix:* silence-based force-complete — if no encoder bytes arrive for 120 ms, the accumulated NAL is treated as complete (safe: ffmpeg writes AUs atomically) and the pending AU flushes.
- **4:4:4 → Main profile** (`-vf format=nv12,hwupload`).
  - *Why:* BGRA→QSV surfaces default to yuv444p/Rext, which iPads can only software-decode. NV12 yields Main profile (yuv420p) → hardware decode on every Apple device. Learned via ffprobe on the reconstructed stream; also learned `extra_hw_frames` breaks the D3D11 pool on this driver (E_INVALIDARG texture creation, caught by the new stderr logging).
- **hvcC description + length-prefixed AU framing.**
  - *What:* the encoder's Annex-B output is parsed into NALs, re-framed as access-unit-aligned chunks of 4-byte length-prefixed NAL units, and a one-shot `desc:<codec>|<base64 hvcC>` message (HEVCDecoderConfigurationRecord built from the SPS profile_tier_level, emulation-prevention stripped) is delivered before the first chunk. Web client configures `VideoDecoder` with `description` + the true codec string instead of a hardcoded guess.
  - *Why:* Safari's WebCodecs reliably hardware-decodes HEVC only with the out-of-band description; bare Annex-B with in-band parameter sets is Chrome-tolerated, Safari-unreliable.
  - *Bug fixed on the way:* SPS parsing must read the RBSP with emulation-prevention bytes removed (`00 00 03` escaping shifts profile_tier_level offsets) — the codec string was garbage before that.
- **GOP 60 → 240 (4 s), `-async_depth 1`.**
  - *Why:* every client owns its encoder instance and therefore joins on an IDR, so frequent keyframes only cost bitrate/quality. 1-second keyframes were burning quality for no join benefit; 4 s is free. `-async_depth 1` minimizes encoder buffering latency.
- **FFmpeg stderr capture** (`[HEVC QSV][ff]` log lines).
  - *Why:* the driver-level texture-pool failure above was invisible before — the encoder died silently after headers. Now every ffmpeg failure surfaces in the service log.

### Verified
Client-side reconstruction of the received chunks back to Annex-B, probed with ffprobe: `hevc / Main / 1180×664 / yuv420p`, one AU per chunk, keyframe flag correct, 88–113 byte delta frames on static content. JPEG path regression clean.

---

## 2026-09-02 — Access-password enforcement + idle-frame skip

### Fixed / Added
- **Authentication (security).** The `EncryptionPassword` registry setting existed but *nothing enforced it* — anyone on the LAN could watch the screen and inject keystrokes.
  - WebSocket: server announces `auth:required` after upgrade; session stays completely inert (no video, no display changes) until the client sends `auth:<token>`. 3 attempts per connection in a 6 s window, constant-time compare, `auth:ok` / `auth:denied` replies.
  - `/input` HTTP endpoint: requires matching `pw=` query parameter, else 403.
  - Legacy raw-binary path (no auth mechanism exists): refused entirely when a password is configured.
  - Web client: password overlay on `auth:required`, re-prompt on denial, hides on success, re-syncs settings after `auth:ok`, and suspends keystroke forwarding while the prompt is up (so typing the password doesn't type into Windows).
  - *Verified:* scripted client — required → denied → ok → frames; `/input` 403 without `pw`, 200 with. Note: a password was configured on this machine at the time (since cleared; default is no password).
- **Idle-frame skip (bandwidth).**
  - *Why:* intra-only JPEG re-sent an identical ~43 KB frame 60×/s — ~20 Mbps of nothing on a static desktop.
  - *What:* the sink skips compose+encode+send when desktop pixels, streamed cursor, and sink settings are all unchanged. WebSocket pings every 5 s keep the connection alive during silence.
  - *Verified:* properly-assembled client measurement: 26 fps while active (tracking real desktop changes), ~0.2 Mbps idle, zero duplicate timestamps on the wire.
  - *Latency effect:* zero during activity; *improved* idle→active transition (the TCP pipe is empty when activity resumes, no stale-frame queue) and no reconnect penalty after long idles.

---

## 2026-09-01/02 — Project rename: OpenSpacedesk → OpenWinSidecar

- Renamed solution (`OpenWinSidecar.slnx`), all four project folders and `.csproj` files, every namespace (`OpenWinSidecar.*`), assembly names, process-name strings in `ServiceProcessManager`, docs, and scripts.
- `VirtualDisplayManager`'s hardcoded driver paths had already been made repo-relative (walk-up `FindProjectFile`) — no action needed there.
- *Outstanding:* the **repo root folder** is still `C:\Users\JulianB\source\repos\OpenSpacedesk` — held open by a process that couldn't be safely killed (session/editor workspace binding). Rename manually after closing whatever holds it: `Rename-Item 'C:\Users\JulianB\source\repos\OpenSpacedesk' 'OpenWinSidecar'`. No rebuild needed afterward.

---

## 2026-09-01 — Console UX redesign

### Changed
- **Single-page redesign** replacing 4 tabs / ~30 buttons / 6 duplicate actions. Hierarchy: iPad-display toggle card (the one primary control) → connect card → resolution+scaling rows → clients list → collapsed Maintenance / Preferences / log expander. Auto-targets the virtual display (no monitor selector). Emoji-free labels, no marketing badges, status-bar feedback instead of success MessageBoxes (errors still get dialogs). System tray menu kept with plain labels.
- **Dark theme completion:** proper dark ComboBox template (the stock light one was unreadable), chevron Expander template (fixed a `ContentSource="Header"` crash found via screenshot testing).
- **`--screenshot <path> [--expand]` diagnostics flag:** renders the window (or full content tree) to PNG and exits — used for all UI verification since native screen capture is unavailable.

---

## 2026-08-31 — Fan-out broadcast refactor (streaming core)

### Changed
Before: every WebSocket client ran its own capture loop over shared capture services — concurrent `AcquireNextFrame` calls on one duplication object (invalid DXGI), and a slow client accumulated unbounded latency in its TCP buffer.

- **`FrameBroadcastHub`** — one capture producer per display device; composes into per-client `ClientFrameSink`s. Drop-oldest backpressure via a busy-flag handoff (slow clients drop frames, never queue latency).
- **Real timestamps** from a shared `Stopwatch` (was fabricated `frameIndex * 16666`, which broke WebCodecs pacing on drops). HEVC NAL packets dequeue their capture timestamp (`-bf 0` preserves order).
- **Honest codec labels:** JPEG payloads are always labeled `IntraTurbo` even if the client asked for h264/av1 (they were mislabeled before — the client's `VideoDecoder` errored on every frame and silently fell back); server pushes a `codec:intra` notice when the QSV encoder can't start so the client UI syncs.
- **Static-desktop GDI seeding:** Desktop Duplication never presents unchanged frames (DWM skips static outputs), so a newly-created virtual display delivered nothing. The producer seeds the first frame via GDI until DXGI presents.
- **GDI fallback at speed:** full-res 1:1 BitBlt into a DIB measured ~260 ms/frame on the indirect display; the DDB-route blit (the pattern the original fast path used) at 1180-wide runs ~55 fps.
- **Time-based DXGI recovery (2 s):** a just-retired producer's duplication handle makes `DuplicateOutput` fail with `E_INVALIDARG` for a few seconds; sticky failover left sessions on GDI. Producers also self-retire after ~2 s without subscribers.
- **`timeBeginPeriod(1)`** at startup — without it, `Task.Delay` pacing quantizes to the 15.6 ms scheduler tick and caps the loop at ~30 fps.
- **Once-per-process `EnableExtendMode()`:** re-flashing display topology on every session invalidated live duplication handles and flickered all monitors.

### Measured
57 FPS single JPEG client (was 15–25), 48 FPS × 2 clients (was broken), 49 FPS hardware HEVC. Capture tick: 0.1–3.5 ms (DXGI) / ~35 ms (GDI-DDB).

---

## 2026-08-31 — Virtual display resolutions + verification tooling

- `vdd_settings.xml`: completed the iPad lineup across **all five** live/template copies (`C:\IddDriver`, `%APPDATA%\VirtualDisplayDriver`, `C:\VirtualDisplayDriver`, `drivers/VDD/`, `drivers/VDD/control/`). Driver restarted via elevated `pnputil`; verified with `EnumDisplaySettings` that `2048×1536` and `2732×2048` (plus every other iPad mode at 60/120 Hz) are advertised.
- Key learning: the VDD device enumerates as `\\.\DISPLAY8x` and **renumbers on driver restart** (85 → 86) — never hardcode it. The duplicate `<g_refresh_rate>` entries turned out to be valid schema (global list cross-multiplied per resolution).
- Driver architecture documented: UMDF/IddCx (`WUDFRd` + `IndirectKmd`), package lives in the Windows DriverStore (`mttvdd.inf_amd64_…`), which is why the driver can't simply "move into the project folder" — repo copies are templates; deployment targets are the live paths.

---

## Pre-history (before 2026-08-31)

Initial build-out by earlier sessions: IddCx virtual display via MttVDD driver, GDI + DXGI capture paths with fallback, JPEG-intra WebSocket streaming with 15-byte WebCodecs framing, `SendInput` input injection with multi-touch gestures, UDP discovery on 28252, USB/ADB forwarding, WPF Console, ffmpeg `hevc_qsv` encoder wrapper (with a raw-frame sizing bug later fixed by the fan-out compose), documentation suite under `docs/`.
