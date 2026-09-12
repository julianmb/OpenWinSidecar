using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using OpenWinSidecar.Service.Encoders;
using OpenWinSidecar.Service.Protocol;
using SkiaSharp;

namespace OpenWinSidecar.Service.Capture;

/// <summary>
/// Per-client frame sink subscribed to the FrameBroadcastHub.
///
/// Lifecycle per tick: the display producer composes the latest native frame into this
/// sink's own bitmap (only when the sink is idle — that IS the backpressure policy, a
/// slow client drops frames instead of accumulating latency), then signals the sink's
/// consumer loop, which encodes (JPEG intra or HEVC via the client's own QSV encoder
/// process) and sends over the client's socket.
///
/// All mutable state is handed off between producer and consumer through the _busy
/// flag + semaphore, so neither side ever touches the other's data concurrently.
/// </summary>
public sealed class ClientFrameSink : IDisposable
{
    // ---- client-controlled configuration (volatile; written by the WebSocket input loop) ----
    public volatile string DeviceName;
    public volatile int TargetWidth = 2360;
    public volatile int TargetHeight = 1640;
    public volatile int TargetFramerate = 60;
    public volatile int Quality = 80;

    private double _zoom = 1.0;
    public double Zoom
    {
        get => Volatile.Read(ref _zoom);
        set => Volatile.Write(ref _zoom, value);
    }

    public volatile bool ShowHostCursor = true;
    public volatile StreamCodec Codec = StreamCodec.IntraTurbo;
    public volatile int ColorDepth = 8; // 8 or 10 (Main10); 10-bit smooths gradients, costs a CPU pixel conversion

    private readonly FrameBroadcastHub _hub;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _streamLock;

    // ---- producer-composed frame state (guarded by _busy handoff) ----
    private Bitmap? _composeBitmap;
    private Graphics? _composeGraphics; // cached GDI+ context (fallback compose path only)
    private byte[]? _rawBuffer;

    // Cursor sprite cache: rendered once per (shape, size) so the per-frame cursor composite is a
    // tiny memory blit rather than GDI+ GetHdc/DrawIconEx (measured ~3.7 ms/frame/sink, enough to
    // push the capture producer below 60 fps).
    private IntPtr _spriteHandle = IntPtr.Zero;
    private int _spriteW, _spriteH;
    private uint[]? _spritePixels;

    // Partial-composite state (producer thread only): the output-space rect of the cursor drawn
    // into _composeBitmap on the previous compose (empty when none), so a dirty-region compose can
    // erase the old cursor by refreshing that area from the clean native frame. Plus a scratch
    // list reused every tick to avoid allocating on the 60 Hz hot path.
    private Rectangle _lastDrawnCursorRect = Rectangle.Empty;
    private readonly List<Rectangle> _sinkDirtyScratch = new(16);

    // ---- live telemetry surfaced to the Console (clients table + session card) ----
    private static readonly ConcurrentDictionary<ClientFrameSink, byte> ActiveSinks = new();
    private readonly DateTime _connectedUtc = DateTime.UtcNow;
    private volatile string _remoteAddress = "";
    private int _lastFps, _lastKbps, _lastLatencyMs;

    public string RemoteAddress { get => _remoteAddress; set => _remoteAddress = value; }

    private volatile Action? _disconnectHandler;

    /// <summary>
    /// Registers the session-shutdown callback (cancel the session token, close the transport).
    /// Set once by the owning session before streaming starts.
    /// </summary>
    internal void SetDisconnectHandler(Action handler) => _disconnectHandler = handler;

    /// <summary>
    /// Asks the owning session to shut down (kick / forced disconnect). Runs the session's
    /// shutdown action — it never touches the sink's buffers directly, so this is safe to call
    /// from any thread while the consumer is composing or encoding.
    /// </summary>
    internal void RequestDisconnect()
    {
        try { _disconnectHandler?.Invoke(); } catch { }
    }

    /// <summary>
    /// Kicks the session whose remote endpoint matches <paramref name="remoteAddress"/>
    /// (e.g. "192.168.1.16:54167"). Returns false when no such client is connected.
    /// </summary>
    public static bool DisconnectByAddress(string remoteAddress)
    {
        if (string.IsNullOrEmpty(remoteAddress)) return false;
        foreach (var sink in ActiveSinks.Keys)
        {
            if (string.Equals(sink.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase))
            {
                sink.RequestDisconnect();
                return true;
            }
        }
        return false;
    }

    internal void UpdateClientStats(int fps, int kbps, int latencyMs)
    {
        _lastFps = fps;
        _lastKbps = kbps;
        _lastLatencyMs = latencyMs;
    }

    /// <summary>A point-in-time view of one connected client, for the Console.</summary>
    public sealed class ConnectedClientSnapshot
    {
        public string DeviceName { get; init; } = "";
        public string RemoteAddress { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public string Codec { get; init; } = "";
        public int Fps { get; init; }
        public int Kbps { get; init; }
        public int LatencyMs { get; init; }
        public double UptimeSeconds { get; init; }

        public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";
        public string FpsText => Fps > 0 ? $"{Fps} fps" : "";
        public string LatencyText => LatencyMs > 0 ? $"{LatencyMs} ms" : "";
        public string BitrateText => Kbps > 0 ? $"{Kbps / 1000.0:F1} Mbps" : "";
    }

    public static List<ConnectedClientSnapshot> Snapshot()
    {
        var list = new List<ConnectedClientSnapshot>();
        foreach (var sink in ActiveSinks.Keys)
        {
            list.Add(new ConnectedClientSnapshot
            {
                DeviceName = sink.DeviceName,
                RemoteAddress = sink._remoteAddress,
                Width = sink.TargetWidth,
                Height = sink.TargetHeight,
                Codec = sink.Codec == StreamCodec.HEVC ? "HEVC" : "JPEG",
                Fps = sink._lastFps,
                Kbps = sink._lastKbps,
                LatencyMs = sink._lastLatencyMs,
                UptimeSeconds = (DateTime.UtcNow - sink._connectedUtc).TotalSeconds,
            });
        }
        return list;
    }
    private long _timestampUs;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;

    private int _busy;
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private int _composeLogged;
    private int _sendLogged;
    private volatile bool _hevcRestartRequested;
    private Task? _descSendTask;
    private Task? _hevcSendTask; // in-flight asynchronous send of the last HEVC access unit
    private int _hevcSendsInFlight;
    private readonly object _encoderLock = new(); // serializes consumer inits with setup-time warmup

    // ---- adaptive encode scale (fluidity-first): sustained pixel motion encodes at half
    // size, calm returns to full size for crisp text. Leaky score with split thresholds gives
    // hysteresis: ~150ms of continuous change to engage, ~250ms calm to release. Cursor-only
    // motion never engages (DXGI reports no pixel update for cursor moves).
    // Kill-switch: SIDECAR_ADAPTIVE_SCALE=0 pins full-res (A/B measurement or escape hatch).
    internal static readonly bool AdaptiveScaleEnabled =
        Environment.GetEnvironmentVariable("SIDECAR_ADAPTIVE_SCALE") != "0";
    private double _motionScore;
    private double _encodeScale = 1.0;
    private double _lastEncodeScale = 1.0;

    // ---- idle detection (written by the producer thread between composes) ----
    private bool _hasComposedOnce;
    private bool _missedFrames;
    private string _lastDeviceName = "";
    private int _lastCursorX, _lastCursorY;
    private bool _lastCursorVisible;
    private int _lastTargetW;
    private int _lastTargetFramerate;
    private double _lastZoom;
    private bool _lastShowHostCursor;
    private int _lastQuality;
    private StreamCodec _lastCodec;

    // ---- HEVC encoder state (owned by the consumer tenure) ----
    private HevcStreamEncoder? _hevcEncoder;
    private int _encoderWidth, _encoderHeight, _encoderBitrate, _encoderFramerate, _encoderDepth;
    private readonly ConcurrentQueue<long> _hevcTimestamps = new();
    private long _hevcFrameIndex;
    private DateTime _nextHevcAttemptUtc = DateTime.MinValue;
    private bool _hevcRetryPending;
    private int _hevcFailConsecutive = 0;
    private int _lastColorDepth = 8;

    private static readonly ImageCodecInfo JpegEncoder = GetJpegEncoder();

    public ClientFrameSink(FrameBroadcastHub hub, NetworkStream stream, SemaphoreSlim streamLock, string deviceName)
    {
        _hub = hub;
        _stream = stream;
        _streamLock = streamLock;
        DeviceName = deviceName;
        ActiveSinks[this] = 0;
        _hub.RegisterSink(this);
    }

    private long _lastForceIdrTicks;

    /// <summary>
    /// Requests an encoder restart so the next HEVC frame is a fresh IDR. Called from the
    /// WebSocket input thread when the client tab becomes visible again — Safari may have
    /// evicted decoded-frame state, and delta frames would then reference frames the new
    /// decoder never had. The actual restart happens on the consumer thread.
    ///
    /// Rate-limited: an encoder restart tears down and respawns the FFmpeg/QSV process and
    /// emits a full keyframe, so a client stuck in a drop→IDR→drop feedback loop could
    /// otherwise freeze the stream every few seconds.
    /// </summary>
    internal void ForceIdr()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastForceIdrTicks);
        if (now - last < 8000) return;
        Interlocked.Exchange(ref _lastForceIdrTicks, now);
        _hevcRestartRequested = true;
    }

    /// <summary>
    /// Called by the display producer on each tick. Returns false when the sink is still
    /// processing the previous frame (frame dropped — drop-oldest backpressure).
    /// </summary>
    internal bool TryBeginCompose(NativeFrame frame, int screenX, int screenY, long timestampUs)
    {
        if (frame.Bitmap == null)
            return false;

        // Idle skip: when the desktop pixels are unchanged (nothing captured this tick), the
        // cursor did not move or toggle, and this sink's settings are unchanged, the client
        // already holds this exact frame — re-sending it would burn ~20 Mbps of identical
        // JPEGs on a static desktop. The consumer loop sends pings to keep the socket alive.
        bool effectiveCursorVisible = ShowHostCursor && frame.CursorVisible;
        bool cursorChanged = effectiveCursorVisible != _lastCursorVisible
            || (effectiveCursorVisible && (frame.CursorGlobalX != _lastCursorX || frame.CursorGlobalY != _lastCursorY));

        // Score every tick (including idle ones) so the adaptive scale tracks real capture
        // activity and releases promptly when motion stops. Must run before settingsChanged
        // so a scale flip forces a compose on this exact tick (including calm upgrades).
        _motionScore = _motionScore * 0.9 + (frame.Captured ? 1.0 : 0.0);
        double wantScale = _encodeScale;
        if (!AdaptiveScaleEnabled) wantScale = 1.0;
        else if (_motionScore > 5.0) wantScale = 0.5;
        else if (_motionScore < 2.0) wantScale = 1.0;
        if (wantScale != _encodeScale)
        {
            _encodeScale = wantScale;
            Console.WriteLine($"[Sink] {DeviceName}: encode scale -> {(wantScale == 1.0 ? "full" : "half")} (motion score {_motionScore:F1})");
        }

        bool settingsChanged = !_hasComposedOnce
            || DeviceName != _lastDeviceName
            || TargetWidth != _lastTargetW
            || TargetFramerate != _lastTargetFramerate
            || Math.Abs(Zoom - _lastZoom) > 1e-9
            || ShowHostCursor != _lastShowHostCursor
            || Quality != _lastQuality
            || Codec != _lastCodec
            || ColorDepth != _lastColorDepth
            || _encodeScale != _lastEncodeScale;

        // Pending recovery work must reach the consumer even when the desktop is static:
        // a missed frame owes a full refresh; a forceidr or an armed HEVC retry owes an
        // encoder restart. Without this, a static desktop leaves the sink idle forever and
        // the recovery never runs (the exact failure mode behind stuck black screens).
        bool pendingHevcWork = Codec == StreamCodec.HEVC
            && (_hevcRestartRequested
                || (_hevcRetryPending && DateTime.UtcNow >= _nextHevcAttemptUtc));
        bool pendingRecovery = _missedFrames || pendingHevcWork;

        if (!frame.Captured && !cursorChanged && !settingsChanged && !pendingRecovery)
            return true; // idle — nothing new to send, not an error

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _missedFrames = true;
            return false;
        }

        try
        {
            int nativeW = frame.Width;
            int nativeH = frame.Height;

            // Even, aspect-preserving target size derived from the native frame, scaled by
            // the adaptive encode scale (half size during sustained motion, full when calm).
            int dstW = (int)((TargetWidth > 0 ? TargetWidth : 2360) * _encodeScale);
            dstW = (dstW / 2) * 2;
            if (dstW < 2) dstW = 2;
            int dstH = (int)Math.Round((double)dstW * nativeH / nativeW);
            dstH = (dstH / 2) * 2;
            if (dstH < 2) dstH = 2;

            // UI magnification crops a centered sub-region of the native frame
            double zoom = Math.Clamp(Zoom, 1.0, 3.0);
            int cropW = Math.Min(nativeW, (int)(nativeW / zoom));
            int cropH = Math.Min(nativeH, (int)(nativeH / zoom));
            int cropX = (nativeW - cropW) / 2;
            int cropY = (nativeH - cropH) / 2;


            // A (re)created bitmap is empty and must be filled fully even if the capture and the
            // cursor report no change (e.g. the display mode changed size without any sink setting
            // changing).
            bool bitmapFresh = false;
            if (_composeBitmap == null || _composeBitmap.Width != dstW || _composeBitmap.Height != dstH)
            {
                _composeGraphics?.Dispose();
                _composeBitmap?.Dispose();
                _composeBitmap = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
                // Cache the GDI+ context: Graphics.FromImage is expensive (~ms) and the cursor
                // is drawn every moving frame, which alone pushed the producer below 60 fps.
                _composeGraphics = Graphics.FromImage(_composeBitmap);
                bitmapFresh = true;
                _lastDrawnCursorRect = Rectangle.Empty;
            }


            // Fast compose path: direct memory copy for 1:1 native streaming (stepX=1, <0.3ms),
            // or integer stride decimation for 2:1 Retina downscale (~1ms).
            // Avoids slow GDI+ DrawImage (which took 20-30ms for 2360x1640 native frames).
            int stepX = cropW / dstW;
            int stepY = cropH / dstH;
            if (stepX >= 1 && stepY >= 1 && cropW == dstW * stepX && cropH == dstH * stepY)
            {
                double scaleX = (double)dstW / cropW;
                double scaleY = (double)dstH / cropH;

                // Resolve the streamed-cursor position and prepare its sprite BEFORE locking the
                // buffers, so the destination is only held for the copy + tiny sprite blit.
                _cursorVisible = false;
                int spriteX = 0, spriteY = 0, spriteW = 0, spriteH = 0;
                bool drawSprite = false;
                Rectangle newCursorRect = Rectangle.Empty;
                if (frame.CursorVisible)
                {
                    int relX = frame.CursorGlobalX - screenX - cropX;
                    int relY = frame.CursorGlobalY - screenY - cropY;

                    if (relX >= -32 && relX < cropW + 32 && relY >= -32 && relY < cropH + 32)
                    {
                        _cursorX = (int)(relX * scaleX);
                        _cursorY = (int)(relY * scaleY);
                        _cursorVisible = true;

                        if (ShowHostCursor)
                        {
                            spriteW = Math.Max(28, (int)(56 * scaleX));
                            spriteH = Math.Max(28, (int)(56 * scaleY));
                            spriteX = (int)((relX - frame.CursorHotspotX) * scaleX);
                            spriteY = (int)((relY - frame.CursorHotspotY) * scaleY);
                            EnsureCursorSprite(frame.CursorHandle, spriteW, spriteH);
                            drawSprite = _spritePixels != null;
                            if (drawSprite)
                                newCursorRect = ClipRect(spriteX, spriteY, spriteW, spriteH, dstW, dstH);
                        }
                    }
                }

                // Full refresh on settings/fresh-bitmap/capture-full/backpressure-recovery; otherwise
                // compose only the output-space dirty set.
                bool doFull = settingsChanged || bitmapFresh || frame.FullFrame || _missedFrames;
                if (doFull) _missedFrames = false;

                List<Rectangle>? dirty = null;
                if (!doFull)
                {
                    _sinkDirtyScratch.Clear();

                    foreach (var r in frame.DirtyRects)
                    {
                        int x0 = Math.Max(r.Left - cropX, 0);
                        int y0 = Math.Max(r.Top - cropY, 0);
                        int x1 = Math.Min(r.Right - cropX, cropW);
                        int y1 = Math.Min(r.Bottom - cropY, cropH);
                        if (x1 <= x0 || y1 <= y0) continue;

                        int dx0 = x0 / stepX;
                        int dy0 = y0 / stepY;
                        int dx1 = Math.Min((x1 + stepX - 1) / stepX, dstW);
                        int dy1 = Math.Min((y1 + stepY - 1) / stepY, dstH);
                        if (dx1 > dx0 && dy1 > dy0)
                            _sinkDirtyScratch.Add(new Rectangle(dx0, dy0, dx1 - dx0, dy1 - dy0));
                    }

                    if (_lastDrawnCursorRect.Width > 0)
                        AddClippedRect(_sinkDirtyScratch, _lastDrawnCursorRect, dstW, dstH);
                    if (newCursorRect.Width > 0)
                        AddClippedRect(_sinkDirtyScratch, newCursorRect, dstW, dstH);

                    if (_sinkDirtyScratch.Count == 0 && !cursorChanged && !pendingHevcWork)
                    {
                        Interlocked.Exchange(ref _busy, 0);
                        return true; // change lies outside the crop and the cursor is static
                    }

                    long dirtyArea = 0;
                    foreach (var r in _sinkDirtyScratch) dirtyArea += (long)r.Width * r.Height;
                    if (dirtyArea >= (long)dstW * dstH / 2) doFull = true;
                    else dirty = _sinkDirtyScratch;
                }

                BitmapData? srcData = null;
                if (frame.RawBuffer == null)
                {
                    srcData = frame.Bitmap.LockBits(
                        new Rectangle(cropX, cropY, cropW, cropH),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                }

                try
                {
                    var dstData = _composeBitmap!.LockBits(
                        new Rectangle(0, 0, dstW, dstH),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        unsafe
                        {
                            fixed (byte* rawBase = frame.RawBuffer)
                            {
                                byte* src = frame.RawBuffer != null
                                    ? (rawBase + (long)cropY * frame.Stride + (long)cropX * 4)
                                    : (byte*)srcData!.Scan0;
                                int srcStride = frame.RawBuffer != null ? frame.Stride : srcData!.Stride;
                                byte* dst = (byte*)dstData.Scan0;

                                if (doFull)
                                {
                                    if (stepX == 1 && stepY == 1)
                                    {
                                        long bytesPerRow = (long)dstW * 4;
                                        if (srcStride == dstData.Stride && srcStride == bytesPerRow)
                                        {
                                            Buffer.MemoryCopy(src, dst, (long)dstH * bytesPerRow, (long)dstH * bytesPerRow);
                                        }
                                        else
                                        {
                                            for (int y = 0; y < dstH; y++)
                                            {
                                                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstData.Stride, bytesPerRow, bytesPerRow);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        for (int y = 0; y < dstH; y++)
                                        {
                                            uint* s = (uint*)(src + (long)y * stepY * srcStride);
                                            uint* d = (uint*)(dst + (long)y * dstData.Stride);
                                            for (int x = 0; x < dstW; x++)
                                                d[x] = s[x * stepX];
                                        }
                                    }
                                }
                                else if (dirty != null)
                                {
                                    if (stepX == 1 && stepY == 1)
                                    {
                                        foreach (var r in dirty)
                                            CopyRect1to1(src, srcStride, dst, dstData.Stride, r);
                                    }
                                    else
                                    {
                                        foreach (var r in dirty)
                                            CopyRectDecimated(src, srcStride, dst, dstData.Stride, r, stepX, stepY);
                                    }
                                }
                                // else: cursor-only header update — pixels already current, no copy needed

                                if (drawSprite)
                                {
                                    BlitCursorSprite(dst, dstData.Stride / 4, dstW, dstH,
                                        _spritePixels!, spriteW, spriteH, spriteX, spriteY);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _composeBitmap.UnlockBits(dstData);
                    }
                }
                finally
                {
                    if (srcData != null)
                        frame.Bitmap.UnlockBits(srcData);
                }

                _lastDrawnCursorRect = drawSprite ? newCursorRect : Rectangle.Empty;
            }
            else
            {
                // Non-integer scale or crop: GDI+ path (rarer; zoom presets etc.)
                var g = _composeGraphics!;
                {
                bool integerScale = stepX >= 1 && stepY >= 1 && cropW % dstW == 0 && cropH % dstH == 0;
                g.InterpolationMode = integerScale
                    ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
                    : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(
                    frame.Bitmap,
                    new Rectangle(0, 0, dstW, dstH),
                    new Rectangle(cropX, cropY, cropW, cropH),
                    GraphicsUnit.Pixel);

                _cursorVisible = false;
                if (frame.CursorVisible)
                {
                    int relX = frame.CursorGlobalX - screenX - cropX;
                    int relY = frame.CursorGlobalY - screenY - cropY;

                    if (relX >= -32 && relX < cropW + 32 && relY >= -32 && relY < cropH + 32)
                    {
                        double scaleX = (double)dstW / cropW;
                        double scaleY = (double)dstH / cropH;

                        int drawX = (int)((relX - frame.CursorHotspotX) * scaleX);
                        int drawY = (int)((relY - frame.CursorHotspotY) * scaleY);
                        _cursorX = (int)(relX * scaleX);
                        _cursorY = (int)(relY * scaleY);
                        _cursorVisible = true;

                        if (ShowHostCursor)
                        {
                            IntPtr hdc = g.GetHdc();
                            try
                            {
                                int curW = Math.Max(28, (int)(56 * scaleX));
                                int curH = Math.Max(28, (int)(56 * scaleY));
                                CursorInterop.DrawIconEx(
                                    hdc, drawX, drawY, frame.CursorHandle,
                                    curW, curH,
                                    0, IntPtr.Zero, CursorInterop.DI_NORMAL);
                            }
                            finally
                            {
                                g.ReleaseHdc(hdc);
                            }
                        }
                    }
                }
                }
            }

            _timestampUs = timestampUs;

            // Record what this frame contained so subsequent identical ticks can be skipped
            _hasComposedOnce = true;
            _lastDeviceName = DeviceName;
            _lastCursorX = frame.CursorGlobalX;
            _lastCursorY = frame.CursorGlobalY;
            _lastCursorVisible = ShowHostCursor && frame.CursorVisible;
            _lastTargetW = TargetWidth;
            _lastTargetFramerate = TargetFramerate;
            _lastZoom = Zoom;
            _lastShowHostCursor = ShowHostCursor;
            _lastQuality = Quality;
            _lastCodec = Codec;
            _lastColorDepth = ColorDepth;
            _lastEncodeScale = _encodeScale;

            if (Interlocked.Exchange(ref _composeLogged, 1) == 0)
                Console.WriteLine($"[Sink] First frame composed {dstW}x{dstH} for {DeviceName}");

            // Signal exactly once per compose. The semaphore capacity is 1, so at most one
            // wakeup is ever pending, and _busy (set above) is held until the consumer's
            // send finishes — so the producer cannot compose again mid-send. If a newer
            // compose raced with a still-pending signal, Release throws and is caught; the
            // consumer simply sends the newest bitmap (drop-oldest), never a lost wakeup.
            try { _frameSignal.Release(); } catch (SemaphoreFullException) { }

            return true;
        }
        catch
        {
            Interlocked.Exchange(ref _busy, 0);
            return false;
        }
    }

    /// <summary>
    /// Waits until the producer has composed a fresh frame into this sink.
    /// Returns false on timeout (caller may send a keepalive ping).
    /// </summary>
    public async Task<bool> WaitFrameAsync(int timeoutMs, CancellationToken token)
        => await _frameSignal.WaitAsync(timeoutMs, token);

    /// <summary>
    /// Encodes the composed frame (HEVC hardware or JPEG intra) and sends it to the client.
    /// The frame is dropped silently when the HEVC encoder cannot be initialized, with a
    /// one-time "codec:intra" notice so the client UI can sync its codec selection.
    /// </summary>
    public async Task ProcessAndSendAsync(CancellationToken token)
    {
        long stageStartUs = _hub.NowUs();
        try
        {
            if (_composeBitmap == null) return;

            if (Codec == StreamCodec.HEVC)
            {
                if (!await PushToHevcAsync(token))
                    await SendJpegAsync(token);
            }
            else
            {
                await SendJpegAsync(token);
            }
        }
        finally
        {
            // Release the handoff. The producer set _busy before signalling this frame and
            // cannot compose while it is set, so no re-arm is needed (the previous
            // _composeWitnessed re-arm released a spurious second wakeup for the very compose
            // that started this send — the consumer then re-entered ProcessAndSendAsync with
            // _busy == 0, letting the producer LockBits the same bitmap and killing the
            // session with "Bitmap region is already locked").
            Interlocked.Exchange(ref _busy, 0);

            _statFrames++;
            double frameMs = (_hub.NowUs() - stageStartUs) / 1000.0;
            _statMs += frameMs;

            // Adaptive JPEG quality: crisp native-resolution text compresses 3-4x worse
            // than the old downscaled frames, so a fixed quality can saturate the network
            // and stall fluidity. Track the encode+send window and step quality down
            // while over budget, recovering slowly when healthy. The client's Quality
            // setting is the ceiling and 55 is the floor (below that, text is unreadable —
            // prefer dropping to HEVC-or-nothing over mush).
            if (Codec == StreamCodec.IntraTurbo)
            {
                if (frameMs > AdaptiveTargetMs)
                {
                    _adaptiveStrikes++;
                    _adaptiveRecovery = 0;
                    if (_adaptiveStrikes >= 8 && _adaptiveQuality > 55)
                    {
                        _adaptiveQuality = Math.Max(55, _adaptiveQuality - 10);
                        _adaptiveStrikes = 0;
                        Console.WriteLine($"[Sink] {DeviceName}: JPEG quality -> {_adaptiveQuality}% (frame {frameMs:F0}ms > {AdaptiveTargetMs:F0}ms target)");
                    }
                }
                else
                {
                    _adaptiveStrikes = 0;
                    if (++_adaptiveRecovery >= 180 && _adaptiveQuality < Quality)
                    {
                        _adaptiveQuality = Math.Min(Quality, _adaptiveQuality + 10);
                        _adaptiveRecovery = 0;
                        Console.WriteLine($"[Sink] {DeviceName}: JPEG quality recovered -> {_adaptiveQuality}%");
                    }
                }
            }

            if (_statMs >= 1500)
            {
                Console.WriteLine($"[Sink] {DeviceName}: encode+send {_statMs / _statFrames:F1}ms/frame over {_statFrames} frames ({_statFrames / (_statMs / 1000.0):F0}/s)");
                _statFrames = 0;
                _statMs = 0;
            }
        }
    }

    private int _statFrames;
    private double _statMs;
    private int _sendCount;
    private DateTime _sendStartUtc = DateTime.UtcNow;

    // Adaptive JPEG quality state (IntraTurbo path)
    private const double AdaptiveTargetMs = 14;   // keep encode+send within one 60fps slot
    private int _adaptiveQuality = 80;             // effective quality; capped by the client's Quality setting
    private int _adaptiveStrikes;                  // consecutive over-budget frames
    private int _adaptiveRecovery;                 // consecutive healthy frames

    // Reused JPEG encode buffer. The JPEG is written directly after a reserved header prefix,
    // then the WebCodecs/WebSocket headers are filled in-place and the whole slice is sent in a
    // single socket write — no per-frame ToArray()/BuildPacket() allocations or copies.
    private MemoryStream? _jpegStream;
    private static readonly byte[] JpegHeaderPad = new byte[WebCodecsFraming.MaxFrameHeaderBytes];

    /// <summary>
    /// Encodes the compose bitmap to JPEG with SkiaSharp (libjpeg-turbo), appending the bytes to
    /// <paramref name="ms"/> after the reserved header prefix. Returns the JPEG payload length, or
    /// -1 on failure. The GDI+ bitmap stays locked only while Skia reads it.
    /// </summary>
    private int EncodeJpegSkia(MemoryStream ms, int quality)
    {
        try
        {
            var bitmap = _composeBitmap!;
            int w = bitmap.Width, h = bitmap.Height;

            var bmpData = bitmap.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                using var pixmap = new SKPixmap(
                    new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque),
                    bmpData.Scan0, bmpData.Stride);
                using var image = SKImage.FromPixels(pixmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (encoded == null || encoded.Size == 0) return -1;

                var span = encoded.AsSpan();
                ms.SetLength(WebCodecsFraming.MaxFrameHeaderBytes + span.Length);
                span.CopyTo(ms.GetBuffer().AsSpan(WebCodecsFraming.MaxFrameHeaderBytes));
                return span.Length;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
        catch
        {
            return -1;
        }
    }

    private int EncodeJpegGdi(MemoryStream ms, int quality)
    {
        try
        {
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            _composeBitmap!.Save(ms, JpegEncoder, encParams);
            return (int)ms.Length - WebCodecsFraming.MaxFrameHeaderBytes;
        }
        catch
        {
            return -1;
        }
    }

    private static bool _skiaFailed;
    private static int _skiaUnavailableLogged;

    private async Task SendJpegAsync(CancellationToken token)
    {
        int quality = Math.Clamp(Math.Min(_adaptiveQuality, Quality), 10, 95);

        var ms = _jpegStream ??= new MemoryStream(96 * 1024);
        ms.SetLength(0);
        ms.Position = 0;
        ms.Write(JpegHeaderPad, 0, WebCodecsFraming.MaxFrameHeaderBytes);

        // SkiaSharp (libjpeg-turbo) is ~4-5x faster than GDI+ at native resolution. GDI+ stays
        // as the fallback if Skia's native library cannot load.
        int payloadLen = _skiaFailed ? EncodeJpegGdi(ms, quality) : EncodeJpegSkia(ms, quality);
        if (payloadLen < 0 && !_skiaFailed)
        {
            _skiaFailed = true;
            if (Interlocked.Exchange(ref _skiaUnavailableLogged, 1) == 0)
                Console.WriteLine($"[Sink] {DeviceName}: SkiaSharp unavailable — using GDI+ JPEG fallback");
            ms.SetLength(WebCodecsFraming.MaxFrameHeaderBytes);
            ms.Position = WebCodecsFraming.MaxFrameHeaderBytes;
            payloadLen = EncodeJpegGdi(ms, quality);
        }
        if (payloadLen < 0) return; // encode failed — skip the frame, the next one will come

        byte[] buffer = ms.GetBuffer();
        int payloadStart = WebCodecsFraming.MaxFrameHeaderBytes;

        // Bandwidth-driven quality learning: crisp native-resolution text frames can
        // exceed 150KB (40+ Mbps at 60fps — beyond Wi-Fi). Heavy frames count as strikes
        // in the adaptive controller (no second encode: encode time is the latency-critical path).
        if (payloadLen > 52_000) _adaptiveStrikes++;

        long ts = _timestampUs;
        short cx = (short)Math.Clamp(_cursorX, short.MinValue, short.MaxValue);
        short cy = (short)Math.Clamp(_cursorY, short.MinValue, short.MaxValue);
        bool curVis = _cursorVisible;

        if (Interlocked.Exchange(ref _sendLogged, 1) == 0)
            Console.WriteLine($"[Sink] First JPEG frame sent: {payloadLen} bytes, ts={ts}us");

        _sendCount++;
        if (_sendCount % 300 == 0)
            Console.WriteLine($"[Sink] send #{_sendCount} ({_sendCount / Math.Max((DateTime.UtcNow - _sendStartUtc).TotalSeconds, 1):F0}/s)");

        await WebCodecsFraming.SendBufferedBinaryAsync(
            _stream, _streamLock, buffer, payloadStart, payloadLen,
            (byte)StreamCodec.IntraTurbo, true, ts, cx, cy, curVis, token);
    }

    /// <summary>
    /// Resolution-aware bitrate tier (before the bandwidth floor). Quality tiers are tuned for the
    /// ~1180x820 logical baseline (~1MP); larger frames scale UP sub-linearly. Shared by the
    /// consumer path and the setup-time warmup so both compute the same value.
    /// </summary>
    private int TierHevcBitrate(int width, int height)
    {
        double megapixels = (double)width * height / 1_000_000;
        double scale = Math.Clamp(Math.Pow(Math.Max(megapixels, 1.0) / 0.97, 0.66), 1.0, 2.6);
        return (int)(Quality switch
        {
            <= 50 => 2500,
            <= 65 => 4000,
            <= 80 => 4000, // fluidity-first: ~10 Mbps at 2360x1640 instead of ~13.7 (less airtime/send latency)
            _ => 9000
        } * scale);
    }

    private HevcStreamEncoder? CreateHevcEncoder(int width, int height, int bitrate, int framerate, int depth)
    {
        var encoder = new HevcStreamEncoder((nalBytes, isKeyframe) =>
        {
            // -bf 0 preserves input order, so one queued timestamp per NAL packet
            long frameIntervalUs = 1_000_000 / framerate;
            long ts = _hevcTimestamps.TryDequeue(out var queued)
                ? queued
                : Interlocked.Increment(ref _hevcFrameIndex) * frameIntervalUs;
            // Track the in-flight send so PushToHevcAsync can apply backpressure
            Volatile.Write(ref _hevcSendTask, SendHevcPacketAsync(nalBytes, isKeyframe, ts));
        },
        (codecString, hvcC) =>
        {
            // One-shot hvcC description for Safari's WebCodecs — must arrive before the first chunk
            _descSendTask = SendHvcCDescriptionAsync(codecString, hvcC);
        });

        if (!encoder.Initialize(width, height, bitrate, framerate, depth))
        {
            encoder.Shutdown();
            return null;
        }
        return encoder;
    }

    /// <summary>
    /// Ensures a running encoder matching (width, height, bitrate, framerate), creating it if needed.
    /// Serialized so the setup-time warmup and the consumer cannot race. Returns the instance, or
    /// null when no hardware encoder is available (the caller falls back to JPEG); never touches
    /// Codec itself.
    /// </summary>
    private HevcStreamEncoder? EnsureHevcEncoder(int width, int height, int bitrate, int framerate, int depth)
    {
        lock (_encoderLock)
        {
            if (_hevcEncoder != null && _encoderWidth == width && _encoderHeight == height && _encoderBitrate == bitrate && _encoderFramerate == framerate && _encoderDepth == depth)
                return _hevcEncoder;

            _hevcEncoder?.Shutdown();
            _hevcEncoder = null;
            _descSendTask = null;
            Volatile.Write(ref _hevcSendTask, null);

            var encoder = CreateHevcEncoder(width, height, bitrate, framerate, depth);
            if (encoder == null) return null;

            _hevcEncoder = encoder;
            _encoderWidth = width;
            _encoderHeight = height;
            _encoderBitrate = bitrate;
            _encoderFramerate = framerate;
            _encoderDepth = depth;
            return encoder;
        }
    }

    /// <summary>
    /// Starts the client's HEVC encoder early (on the connection-setup path) so the ffmpeg spawn +
    /// QSV init overlap the handshake instead of sitting on the first frame's critical path. Uses
    /// the sink's current targets; if the client's settings differ, the first push transparently
    /// re-inits. Safe to call once per connection; a no-op unless HEVC is selected. Best-effort: if
    /// it cannot start, the first push falls back to JPEG exactly as before.
    /// </summary>
    internal void WarmupHevc()
    {
        if (Codec != StreamCodec.HEVC) return;

        int w = TargetWidth > 0 ? (TargetWidth / 2) * 2 : 2360;
        if (w < 2) w = 2;
        int h = TargetHeight > 0 ? (TargetHeight / 2) * 2 : 1640;
        if (h < 2) h = 2;
        int fps = TargetFramerate > 0 ? TargetFramerate : 60;
        int bitrate = TierHevcBitrate(w, h);
        int floor = _hevcBitrateFloor;
        if (floor > 0) bitrate = Math.Min(bitrate, floor);
        int depth = ColorDepth == 10 ? 10 : 8;

        _ = Task.Run(() =>
        {
            try { EnsureHevcEncoder(w, h, bitrate, fps, depth); }
            catch { }
        });
    }

    /// <summary>Returns false when the HEVC path is unavailable (caller falls back to JPEG).</summary>
    private async Task<bool> PushToHevcAsync(CancellationToken token)
    {
        var bitmap = _composeBitmap!;
        int width = bitmap.Width;
        int height = bitmap.Height;

        int bitrate = TierHevcBitrate(width, height);

        // Bandwidth feedback: a 2-second rolling window of HEVC wire bytes. While the
        // produced rate exceeds the ceiling, step the bitrate floor down; when it fits
        // (and quality/resolution haven't changed), ease the floor back up.
        if (_hevcWindowStartTicks == 0) _hevcWindowStartTicks = Environment.TickCount64;
        double windowSec = (Environment.TickCount64 - _hevcWindowStartTicks) / 1000.0;
        if (windowSec >= 2.0)
        {
            double bps = _hevcBytesWindow * 8.0 / windowSec;
            if (bps > HevcBandwidthCeiling)
            {
                _hevcBitrateFloor = Math.Max(2000, (_hevcBitrateFloor == 0 ? bitrate : _hevcBitrateFloor) - 2000);
                _healthyWindows = 0;
                Console.WriteLine($"[Sink] {DeviceName}: HEVC wire rate {bps / 1e6:F1} Mbps > ceiling — bitrate floor -> {_hevcBitrateFloor} kbps");
            }
            else if (_hevcBitrateFloor > 0 && bps < HevcBandwidthCeiling * 0.6)
            {
                // Slow up: only lift the floor after 15 consecutive healthy windows (30s).
                // Instant lift used to flap the bitrate on every transient dip, and every
                // change costs a full encoder respawn + IDR.
                if (++_healthyWindows >= 15)
                {
                    _hevcBitrateFloor = 0;
                    _healthyWindows = 0;
                    Console.WriteLine($"[Sink] {DeviceName}: wire healthy for 30s — bitrate floor lifted");
                }
            }
            else
            {
                _healthyWindows = 0;
            }
            _hevcBytesWindow = 0;
            _hevcWindowStartTicks = Environment.TickCount64;
        }
        if (_hevcBitrateFloor > 0) bitrate = Math.Min(bitrate, _hevcBitrateFloor);

        // Client requested a clean reference state (tab visible again): restart the encoder
        // so the next frame is a fresh IDR
        if (_hevcRestartRequested)
        {
            _hevcRestartRequested = false;
            lock (_encoderLock)
            {
                _descSendTask = null;
                Volatile.Write(ref _hevcSendTask, null);
                _hevcEncoder?.Shutdown();
                _hevcEncoder = null;
                _encoderWidth = _encoderHeight = _encoderBitrate = _encoderFramerate = _encoderDepth = 0;
            }
            Console.WriteLine($"[Sink] {DeviceName}: HEVC encoder restarted for clean IDR");
        }

        int targetFramerate = TargetFramerate > 0 ? TargetFramerate : 60;
        if (DateTime.UtcNow < _nextHevcAttemptUtc)
        {
            // Transient cooldown after encoder failure — serve JPEG for this frame without permanently downgrading
            return false;
        }

        int depth = ColorDepth == 10 ? 10 : 8;

        // Restart coalescing with urgency asymmetry. Geometry (size/fps/depth) changes always
        // apply immediately — the encoder cannot keep running at the wrong size anyway (this
        // also fixes the adaptive-scale switch, which must take the new size's bitrate at once
        // instead of running half-res at the full-res bitrate for 30s and restarting twice).
        // Pure bitrate changes: downward applies immediately (congestion relief is urgent);
        // upward holds 30s (quality restoration can wait out a flap). Every change costs a
        // respawn + IDR either way.
        bool geometryChanged = _encoderBitrate == 0
            || width != _encoderWidth || height != _encoderHeight
            || targetFramerate != _encoderFramerate || depth != _encoderDepth;
        if (!geometryChanged && bitrate != _encoderBitrate
            && bitrate > _encoderBitrate
            && Environment.TickCount64 - _lastBitrateChangeTicks < 30_000)
        {
            bitrate = _encoderBitrate; // hold; upward flap will pass
        }
        else if (bitrate != _encoderBitrate || geometryChanged)
        {
            _lastBitrateChangeTicks = Environment.TickCount64;
        }

        var activeEncoder = EnsureHevcEncoder(width, height, bitrate, targetFramerate, depth);
        if (activeEncoder == null)
        {
            _hevcFailConsecutive++;
            _nextHevcAttemptUtc = DateTime.UtcNow.AddSeconds(2);
            _hevcRetryPending = true;
            if (_hevcFailConsecutive >= 5)
            {
                Codec = StreamCodec.IntraTurbo;
                _hevcRetryPending = false;
                _hevcRestartRequested = false;
                var reason = HevcStreamEncoder.ActiveEncoderCount >= 4 ? "encoder limit reached" : "encoder failed to start";
                Console.WriteLine($"[Sink] {DeviceName}: HEVC permanently unavailable ({reason}) — falling back to JPEG");
                try
                {
                    await WebCodecsFraming.SendTextAsync(_stream, _streamLock, "codec:intra", token);
                }
                catch { }
            }
            else
            {
                Console.WriteLine($"[Sink] {DeviceName}: HEVC start failed (attempt {_hevcFailConsecutive}/5) — will retry in 2s, using JPEG for now");
            }
            return false;
        }
        _hevcFailConsecutive = 0;
        _hevcRetryPending = false;

        int byteCount = width * height * 4;
        if (_rawBuffer == null || _rawBuffer.Length != byteCount)
            _rawBuffer = new byte[byteCount];

        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bmpData.Scan0, _rawBuffer, 0, byteCount);
        }
        finally
        {
            // Release GDI+ before writing to FFmpeg: the pipe write blocks under backpressure, and
            // holding the bitmap lock across it stalls the capture producer (cursor/frame updates),
            // which measured as much worse fluidity than the copy it saved.
            bitmap.UnlockBits(bmpData);
        }

        // Pipelined backpressure. Allow exactly 1 packet in flight on the network wire so
        // hardware encode and network transmission pipeline concurrently. If network
        // transmission falls behind (>= 2 packets queued/in-flight), pause here to keep _busy
        // set so the producer drops old frames instead of accumulating latency.
        if (Volatile.Read(ref _hevcSendsInFlight) >= 2)
        {
            var priorSend = Volatile.Read(ref _hevcSendTask);
            if (priorSend is { IsCompleted: false })
                await priorSend.WaitAsync(token);
        }

        _hevcTimestamps.Enqueue(_timestampUs);
        bool pushed = activeEncoder.PushRawFrame(_rawBuffer);
        if (!pushed)
        {
            Console.WriteLine($"[Sink] {DeviceName}: HEVC frame push failed (ffmpeg died) — resetting encoder for retry in 2s");
            lock (_encoderLock)
            {
                _descSendTask = null;
                Volatile.Write(ref _hevcSendTask, null);
                _hevcEncoder?.Shutdown();
                _hevcEncoder = null;
                _encoderWidth = _encoderHeight = _encoderBitrate = _encoderFramerate = _encoderDepth = 0;
            }
            _nextHevcAttemptUtc = DateTime.UtcNow.AddSeconds(2);
            _hevcRetryPending = true;
            return false;
        }
        return true;
    }

    private readonly object _paceLock = new();
    private long _nextSendSlotUs; // pacing grid: AUs go out on an even cadence, not as-they-complete

    private async Task SendHevcPacketAsync(byte[] nalBytes, bool isKeyframe, long timestampUs)
    {
        // Paced send: QSV completes frames in bursts (keyframe + backlog flush together), and
        // bursty arrivals waste client vsyncs (two frames in one interval = one discarded, the
        // next interval starves). Releasing AUs on an even TargetFramerate cadence converts
        // arrivals into paintings ~1:1. Late frames go immediately; only early ones wait, so
        // this can only smooth, never stall, the stream. Runs before the in-flight count so
        // pacer waits don't trip network backpressure.
        long intervalUs = 1_000_000L / Math.Max(1, TargetFramerate);
        long delayUs;
        lock (_paceLock)
        {
            long nowUs = _hub.NowUs();
            long slot = _nextSendSlotUs <= 0 ? nowUs : _nextSendSlotUs;
            if (slot > nowUs)
            {
                delayUs = slot - nowUs;
                _nextSendSlotUs = slot + intervalUs;
            }
            else
            {
                delayUs = 0;
                _nextSendSlotUs = nowUs + intervalUs;
            }
        }
        if (delayUs > 0)
            await Task.Delay(TimeSpan.FromMicroseconds(delayUs), CancellationToken.None);

        Interlocked.Increment(ref _hevcSendsInFlight);
        try
        {
            var dt = _descSendTask;
            if (dt != null)
            {
                await dt;
            }

            // Oversized-frame log for stall correlation: any AU over ~100KB occupies the
            // wire for 50ms+ at current bitrates and stalls everything behind it.
            if (nalBytes.Length > 100_000)
                Console.WriteLine($"[Sink] {DeviceName}: large AU {nalBytes.Length} bytes key={isKeyframe} at {DateTime.UtcNow:HH:mm:ss.fff}");

            var packet = WebCodecsFraming.BuildPacket((byte)StreamCodec.HEVC, isKeyframe, timestampUs, 0, 0, false, nalBytes);
            await _streamLock.WaitAsync(CancellationToken.None);
            try { await WebCodecsFraming.SendWsBinaryFrameAsync(_stream, packet, CancellationToken.None); }
            finally { _streamLock.Release(); }
            _hevcBytesWindow += nalBytes.Length; // single-threaded consumer — no interlocking needed
        }
        catch (Exception ex)
        {
            // Surface the first few failures (a dead socket usually ends the session right after).
            if (Interlocked.Increment(ref _hevcSendErrorCount) <= 3)
                Console.WriteLine($"[Sink] {DeviceName}: HEVC packet send failed ({ex.GetType().Name}): {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _hevcSendsInFlight);
        }
    }

    private int _hevcSendErrorCount;

    // HEVC bandwidth feedback (consumer-thread state; simple window like the JPEG controller)
    private const long HevcBandwidthCeiling = 20_000_000; // healthy Wi-Fi ceiling
    private long _hevcBytesWindow;
    private long _hevcWindowStartTicks;
    private int _hevcBitrateFloor; // 0 = unset; steps down while the wire rate exceeds the ceiling
    private int _healthyWindows; // consecutive healthy 2s windows (15 = 30s) before lifting the floor
    private long _lastBitrateChangeTicks; // last applied bitrate change; restarts coalesced to 30s

    private async Task SendHvcCDescriptionAsync(string codecString, byte[] hvcC)
    {
        try
        {
            await WebCodecsFraming.SendTextAsync(
                _stream, _streamLock, $"desc:{codecString}|{Convert.ToBase64String(hvcC)}", CancellationToken.None);
        }
        catch { }
    }

    /// <summary>
    /// Renders the OS cursor into a cached ARGB sprite (one per shape/size). The GDI+ cost is paid
    /// only when the cursor changes; the per-frame path is then a small memory blit.
    /// </summary>
    private void EnsureCursorSprite(IntPtr handle, int w, int h)
    {
        if (handle == _spriteHandle && w == _spriteW && h == _spriteH && _spritePixels != null)
            return;

        var pixels = new uint[w * h];
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                IntPtr hdc = g.GetHdc();
                try
                {
                    CursorInterop.DrawIconEx(hdc, 0, 0, handle, w, h, 0, IntPtr.Zero, CursorInterop.DI_NORMAL);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        uint* row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                        int offset = y * w;
                        for (int x = 0; x < w; x++)
                            pixels[offset + x] = row[x];
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        _spritePixels = pixels;
        _spriteW = w;
        _spriteH = h;
        _spriteHandle = handle;
    }

    private static void AddClippedRect(List<Rectangle> list, Rectangle rect, int maxW, int maxH)
    {
        int x0 = Math.Clamp(rect.X, 0, maxW);
        int y0 = Math.Clamp(rect.Y, 0, maxH);
        int x1 = Math.Clamp(rect.Right, 0, maxW);
        int y1 = Math.Clamp(rect.Bottom, 0, maxH);
        if (x1 > x0 && y1 > y0)
            list.Add(new Rectangle(x0, y0, x1 - x0, y1 - y0));
    }

    private static Rectangle ClipRect(int x, int y, int w, int h, int maxW, int maxH)
    {
        int x0 = Math.Clamp(x, 0, maxW);
        int y0 = Math.Clamp(y, 0, maxH);
        int x1 = Math.Clamp(x + w, 0, maxW);
        int y1 = Math.Clamp(y + h, 0, maxH);
        return x1 > x0 && y1 > y0 ? new Rectangle(x0, y0, x1 - x0, y1 - y0) : Rectangle.Empty;
    }

    private static unsafe void CopyRect1to1(byte* srcBase, int srcStride, byte* dstBase, int dstStride, Rectangle rect)
    {
        int rowBytes = rect.Width * 4;
        for (int y = 0; y < rect.Height; y++)
        {
            Buffer.MemoryCopy(
                srcBase + (long)(rect.Y + y) * srcStride + (long)rect.X * 4,
                dstBase + (long)(rect.Y + y) * dstStride + (long)rect.X * 4,
                rowBytes, rowBytes);
        }
    }

    private static unsafe void CopyRectDecimated(
        byte* srcBase, int srcStride, byte* dstBase, int dstStride,
        Rectangle rect, int stepX, int stepY)
    {
        for (int y = 0; y < rect.Height; y++)
        {
            uint* s = (uint*)(srcBase + (long)(rect.Y + y) * stepY * srcStride);
            uint* d = (uint*)(dstBase + (long)(rect.Y + y) * dstStride);
            for (int x = 0; x < rect.Width; x++)
                d[rect.X + x] = s[(rect.X + x) * stepX];
        }
    }

    /// <summary>Alpha-blends the cached cursor sprite into a locked 32bpp BGRA destination.</summary>
    private static unsafe void BlitCursorSprite(
        byte* dst, int dstStridePx, int dstW, int dstH,
        uint[] sprite, int spriteW, int spriteH, int x, int y)
    {
        for (int sy = 0; sy < spriteH; sy++)
        {
            int dy = y + sy;
            if ((uint)dy >= (uint)dstH) continue;

            uint* drow = (uint*)dst + (long)dy * dstStridePx;
            int spriteRow = sy * spriteW;
            for (int sx = 0; sx < spriteW; sx++)
            {
                int dx = x + sx;
                if ((uint)dx >= (uint)dstW) continue;

                uint s = sprite[spriteRow + sx];
                uint sa = s >> 24;
                if (sa == 0) continue;
                if (sa == 255)
                {
                    drow[dx] = s;
                    continue;
                }

                uint d = drow[dx];
                uint ia = 255 - sa;
                uint r = (((s >> 16) & 0xFF) * sa + ((d >> 16) & 0xFF) * ia) / 255;
                uint g = (((s >> 8) & 0xFF) * sa + ((d >> 8) & 0xFF) * ia) / 255;
                uint b = ((s & 0xFF) * sa + (d & 0xFF) * ia) / 255;
                drow[dx] = 0xFF000000u | (r << 16) | (g << 8) | b;
            }
        }
    }

    public void Dispose()
    {
        // Idempotent: a kick cancels the session and the consumer's finally disposes the sink,
        // while server shutdown may race the same path.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ActiveSinks.TryRemove(this, out _);
        _hub.UnregisterSink(this);
        lock (_encoderLock)
        {
            _hevcEncoder?.Shutdown();
            _hevcEncoder = null;
        }
        _composeGraphics?.Dispose();
        _composeGraphics = null;
        _composeBitmap?.Dispose();
        _composeBitmap = null;
        _frameSignal.Dispose();
    }

    private int _disposed;

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
            ?? ImageCodecInfo.GetImageEncoders()[0];
    }
}
