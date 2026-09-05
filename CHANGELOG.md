# OpenWinSidecar — Change Log

Every change to this project is documented here: **what** was changed, **why**, and **how it was verified**. New entries go at the top. The commit history (`git log`) carries the same explanations per commit; this file is the human-readable narrative.

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
- Password rotated on this machine (`564D7EC4` → user-set); docs and test scripts synced. Access password currently `123` — weak, rotate via Console → Preferences.

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
  - *Verified:* scripted client — required → denied → ok → frames; `/input` 403 without `pw`, 200 with. Note: this machine has password `564D7EC4` configured (pre-existing; previously ignored).
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
