using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using OpenWinSidecar.Service.Encoders;
using OpenWinSidecar.Service.Protocol;

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
    public volatile int TargetWidth = 1180;
    public volatile int TargetHeight = 820;
    public volatile int Quality = 80;

    private double _zoom = 1.0;
    public double Zoom
    {
        get => Volatile.Read(ref _zoom);
        set => Volatile.Write(ref _zoom, value);
    }

    public volatile bool ShowHostCursor = true;
    public volatile StreamCodec Codec = StreamCodec.IntraTurbo;

    private readonly FrameBroadcastHub _hub;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _streamLock;

    // ---- producer-composed frame state (guarded by _busy handoff) ----
    private Bitmap? _composeBitmap;
    private byte[]? _rawBuffer;
    private long _timestampUs;
    private int _cursorX, _cursorY;
    private bool _cursorVisible;

    private int _busy;
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private int _composeLogged;
    private int _sendLogged;
    private volatile bool _hevcRestartRequested;

    // ---- idle detection (written by the producer thread between composes) ----
    private bool _hasComposedOnce;
    private string _lastDeviceName = "";
    private int _lastCursorX, _lastCursorY;
    private bool _lastCursorVisible;
    private int _lastTargetW;
    private double _lastZoom;
    private bool _lastShowHostCursor;
    private int _lastQuality;
    private StreamCodec _lastCodec;

    // ---- HEVC encoder state (owned by the consumer tenure) ----
    private HevcQsvStreamEncoder? _hevcEncoder;
    private int _encoderWidth, _encoderHeight, _encoderBitrate;
    private readonly ConcurrentQueue<long> _hevcTimestamps = new();
    private long _hevcFrameIndex;

    private static readonly ImageCodecInfo JpegEncoder = GetJpegEncoder();

    public ClientFrameSink(FrameBroadcastHub hub, NetworkStream stream, SemaphoreSlim streamLock, string deviceName)
    {
        _hub = hub;
        _stream = stream;
        _streamLock = streamLock;
        DeviceName = deviceName;
        _hub.RegisterSink(this);
    }

    /// <summary>
    /// Requests an encoder restart so the next HEVC frame is a fresh IDR. Called from the
    /// WebSocket input thread when the client tab becomes visible again — Safari may have
    /// evicted decoded-frame state, and delta frames would then reference frames the new
    /// decoder never had. The actual restart happens on the consumer thread.
    /// </summary>
    internal void ForceIdr() => _hevcRestartRequested = true;

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
        bool settingsChanged = !_hasComposedOnce
            || DeviceName != _lastDeviceName
            || TargetWidth != _lastTargetW
            || Math.Abs(Zoom - _lastZoom) > 1e-9
            || ShowHostCursor != _lastShowHostCursor
            || Quality != _lastQuality
            || Codec != _lastCodec;

        if (!frame.Captured && !cursorChanged && !settingsChanged)
            return true; // idle — nothing new to send, not an error

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;

        try
        {
            int nativeW = frame.Width;
            int nativeH = frame.Height;

            // Even, aspect-preserving target size derived from the native frame
            int dstW = TargetWidth > 0 ? TargetWidth : 1180;
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

            if (_composeBitmap == null || _composeBitmap.Width != dstW || _composeBitmap.Height != dstH)
            {
                _composeBitmap?.Dispose();
                _composeBitmap = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb);
            }

            using (var g = Graphics.FromImage(_composeBitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(
                    frame.Bitmap,
                    new Rectangle(0, 0, dstW, dstH),
                    new Rectangle(cropX, cropY, cropW, cropH),
                    GraphicsUnit.Pixel);

                _cursorVisible = false;
                if (ShowHostCursor && frame.CursorVisible)
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

                        IntPtr hdc = g.GetHdc();
                        try
                        {
                            CursorInterop.DrawIconEx(
                                hdc, drawX, drawY, frame.CursorHandle,
                                Math.Max(16, (int)(32 * scaleX)),
                                Math.Max(16, (int)(32 * scaleY)),
                                0, IntPtr.Zero, CursorInterop.DI_NORMAL);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
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
            _lastZoom = Zoom;
            _lastShowHostCursor = ShowHostCursor;
            _lastQuality = Quality;
            _lastCodec = Codec;

            if (Interlocked.Exchange(ref _composeLogged, 1) == 0)
                Console.WriteLine($"[Sink] First frame composed {dstW}x{dstH} for {DeviceName}");

            // Signal the consumer only if it is not already pending (coalesces ticks)
            if (_frameSignal.CurrentCount == 0)
            {
                try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
            }

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
            Interlocked.Exchange(ref _busy, 0);

            _statFrames++;
            _statMs += (_hub.NowUs() - stageStartUs) / 1000.0;
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

    private async Task SendJpegAsync(CancellationToken token)
    {
        using var ms = new MemoryStream(32768);
        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(Quality, 10, 95));
        _composeBitmap!.Save(ms, JpegEncoder, encParams);

        var frameBytes = ms.ToArray();
        long ts = _timestampUs;
        short cx = (short)Math.Clamp(_cursorX, short.MinValue, short.MaxValue);
        short cy = (short)Math.Clamp(_cursorY, short.MinValue, short.MaxValue);
        bool curVis = _cursorVisible;

        if (Interlocked.Exchange(ref _sendLogged, 1) == 0)
            Console.WriteLine($"[Sink] First JPEG frame sent: {frameBytes.Length} bytes, ts={ts}us");

        _sendCount++;
        if (_sendCount % 300 == 0)
            Console.WriteLine($"[Sink] send #{_sendCount} ({_sendCount / Math.Max((DateTime.UtcNow - _sendStartUtc).TotalSeconds, 1):F0}/s)");

        // JPEG payload must always be labeled IntraTurbo — the payload is never H.264/AV1/HEVC
        await WebCodecsFraming.SendPacketAsync(
            _stream, _streamLock, (byte)StreamCodec.IntraTurbo,
            true, ts, cx, cy, curVis, frameBytes, token);
    }

    /// <summary>Returns false when the HEVC path is unavailable (caller falls back to JPEG).</summary>
    private async Task<bool> PushToHevcAsync(CancellationToken token)
    {
        var bitmap = _composeBitmap!;
        int width = bitmap.Width;
        int height = bitmap.Height;
        int bitrate = Quality switch
        {
            <= 50 => 3000,
            <= 65 => 5000,
            <= 80 => 8000,
            _ => 12000
        };

        // Client requested a clean reference state (tab visible again): restart the encoder
        // so the next frame is a fresh IDR
        if (_hevcRestartRequested)
        {
            _hevcRestartRequested = false;
            _hevcEncoder?.Shutdown();
            _hevcEncoder = null;
            _encoderWidth = _encoderHeight = _encoderBitrate = 0;
            Console.WriteLine($"[Sink] {DeviceName}: HEVC encoder restarted for clean IDR");
        }

        if (_hevcEncoder == null || _encoderWidth != width || _encoderHeight != height || _encoderBitrate != bitrate)
        {
            _hevcEncoder?.Shutdown();
            _hevcEncoder = null;

            var encoder = new HevcQsvStreamEncoder((nalBytes, isKeyframe) =>
            {
                // -bf 0 preserves input order, so one queued timestamp per NAL packet
                long ts = _hevcTimestamps.TryDequeue(out var queued)
                    ? queued
                    : Interlocked.Increment(ref _hevcFrameIndex) * 16666;
                _ = SendHevcPacketAsync(nalBytes, isKeyframe, ts);
            },
            (codecString, hvcC) =>
            {
                // One-shot hvcC description for Safari's WebCodecs — must arrive before the first chunk
                _ = SendHvcCDescriptionAsync(codecString, hvcC);
            });

            if (encoder.Initialize(width, height, bitrate))
            {
                _hevcEncoder = encoder;
                _encoderWidth = width;
                _encoderHeight = height;
                _encoderBitrate = bitrate;
            }
            else
            {
                encoder.Shutdown();
                Codec = StreamCodec.IntraTurbo;
                var reason = HevcQsvStreamEncoder.ActiveEncoderCount >= 4 ? "encoder limit reached" : "encoder failed to start";
                Console.WriteLine($"[Sink] {DeviceName}: HEVC unavailable ({reason}) — falling back to JPEG");
                try
                {
                    await WebCodecsFraming.SendTextAsync(_stream, _streamLock, "codec:intra", token);
                }
                catch { }
                return false;
            }
        }

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
            bitmap.UnlockBits(bmpData);
        }

        _hevcTimestamps.Enqueue(_timestampUs);
        return _hevcEncoder.PushRawFrame(_rawBuffer);
    }

    private async Task SendHevcPacketAsync(byte[] nalBytes, bool isKeyframe, long timestampUs)
    {
        try
        {
            await WebCodecsFraming.SendPacketAsync(
                _stream, _streamLock, (byte)StreamCodec.HEVC,
                isKeyframe, timestampUs, 0, 0, false, nalBytes, CancellationToken.None);
        }
        catch { }
    }

    private async Task SendHvcCDescriptionAsync(string codecString, byte[] hvcC)
    {
        try
        {
            await WebCodecsFraming.SendTextAsync(
                _stream, _streamLock, $"desc:{codecString}|{Convert.ToBase64String(hvcC)}", CancellationToken.None);
        }
        catch { }
    }

    public void Dispose()
    {
        _hub.UnregisterSink(this);
        _hevcEncoder?.Shutdown();
        _composeBitmap?.Dispose();
        _composeBitmap = null;
        _frameSignal.Dispose();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
            ?? ImageCodecInfo.GetImageEncoders()[0];
    }
}
