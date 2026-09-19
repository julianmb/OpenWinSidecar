using System.Diagnostics;
using System.Net.Sockets;

namespace OpenWinSidecar.Service.Capture;

/// <summary>
/// Broadcast hub that decouples capture from streaming.
///
/// One <see cref="DisplayCaptureProducer"/> runs per distinct display device: exactly one
/// Desktop Duplication (or GDI) capture loop per display regardless of how many clients are
/// connected. Each producer composes frames into the per-client <see cref="ClientFrameSink"/>
/// instances that target its display, so two iPads can watch the same (or different) displays
/// without racing over a shared duplication object.
/// </summary>
public sealed class FrameBroadcastHub : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private readonly Dictionary<string, DisplayCaptureProducer> _producers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ClientFrameSink> _sinks = new();
    private readonly System.Threading.Timer _watchdog;
    private bool _disposed;

    internal long NowUs() => _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Ms without a producer tick before the watchdog recreates it (sinks attached).</summary>
    internal const long ProducerStallThresholdMs = 15000;

    public FrameBroadcastHub()
    {
        _watchdog = new System.Threading.Timer(_ => CheckProducers(), null, 5000, 5000);
    }

    public ClientFrameSink CreateSink(NetworkStream stream, SemaphoreSlim streamLock, string deviceName, string remoteAddress = "")
    {
        return new ClientFrameSink(this, stream, streamLock, deviceName) { RemoteAddress = remoteAddress };
    }

    public void RegisterSink(ClientFrameSink sink)
    {
        lock (_sync)
        {
            _sinks.Add(sink);
            EnsureProducerUnsafe(sink.DeviceName);
        }
    }

    public void UnregisterSink(ClientFrameSink sink)
    {
        lock (_sync)
        {
            _sinks.Remove(sink);
        }
    }

    /// <summary>Makes sure a capture producer exists for the given display device.</summary>
    public void EnsureProducer(string deviceName)
    {
        lock (_sync)
        {
            EnsureProducerUnsafe(deviceName);
        }
    }

    private void EnsureProducerUnsafe(string deviceName)
    {
        if (_disposed || string.IsNullOrEmpty(deviceName)) return;
        if (!_producers.TryGetValue(deviceName, out var producer))
        {
            producer = new DisplayCaptureProducer(this, deviceName);
            _producers[deviceName] = producer;
            producer.Start();
        }
    }

    internal void RemoveProducer(DisplayCaptureProducer producer)
    {
        lock (_sync)
        {
            if (_producers.TryGetValue(producer.DeviceName, out var current) && ReferenceEquals(current, producer))
            {
                _producers.Remove(producer.DeviceName);
            }
        }
    }

    /// <summary>
    /// Pure stall decision (unit-testable): a producer with attached sinks that hasn't ticked
    /// within the threshold is dead (its thread is gone or wedged inside a capture call).
    /// Unchecked subtraction keeps TickCount64 wrap-around correct.
    /// </summary>
    internal static bool IsProducerStalled(long lastTickMs, long nowMs, bool hasSinks, long thresholdMs = ProducerStallThresholdMs)
        => hasSinks && unchecked(nowMs - lastTickMs) > thresholdMs;

    private void CheckProducers()
    {
        List<DisplayCaptureProducer>? stalled = null;
        lock (_sync)
        {
            if (_disposed) return;
            long now = Environment.TickCount64;
            foreach (var producer in _producers.Values)
            {
                bool hasSinks = false;
                foreach (var sink in _sinks)
                {
                    if (string.Equals(sink.DeviceName, producer.DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        hasSinks = true;
                        break;
                    }
                }
                if (!IsProducerStalled(producer.LastTickMs, now, hasSinks))
                    continue;
                Console.WriteLine($"[Hub] {producer.DeviceName}: capture stall detected, recreating producer");
                if (_producers.TryGetValue(producer.DeviceName, out var current) && ReferenceEquals(current, producer))
                    _producers.Remove(producer.DeviceName);
                (stalled ??= new List<DisplayCaptureProducer>()).Add(producer);
            }
        }
        // Dispose off-lock: Dispose joins the producer thread, which may itself be waiting
        // on _sync (idle retire path) — joining under the lock would deadlock for 500ms.
        if (stalled != null)
        {
            foreach (var producer in stalled)
            {
                try { producer.Dispose(); } catch { }
                EnsureProducer(producer.DeviceName);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with the sinks targeting <paramref name="deviceName"/>.
    /// The producer reuses one buffer per tick, so the 60 Hz capture loop allocates nothing
    /// for sink enumeration (the previous <c>GetSinksFor</c> allocated up to four lists/tick).
    /// </summary>
    internal void FillSinksFor(string deviceName, List<ClientFrameSink> buffer)
    {
        buffer.Clear();
        lock (_sync)
        {
            foreach (var sink in _sinks)
            {
                if (string.Equals(sink.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    buffer.Add(sink);
            }
        }
    }

    /// <summary>Returns a snapshot of all currently connected client sinks.</summary>
    public List<ClientFrameSink> GetAllSinks()
    {
        var list = new List<ClientFrameSink>();
        lock (_sync)
        {
            list.AddRange(_sinks);
        }
        return list;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _watchdog.Dispose(); } catch { }

            foreach (var producer in _producers.Values)
            {
                producer.Dispose();
            }
            _producers.Clear();
        }
    }
}

/// <summary>
/// Single capture loop for one display device. Captures at native resolution once per tick
/// (DXGI Desktop Duplication, falling back to GDI with periodic DXGI retries), draws the
/// virtual-display watermark, and composes per-client output into every matching sink.
/// Retires itself when no sink targets its display for a few seconds.
/// </summary>
internal sealed class DisplayCaptureProducer : IDisposable
{
    private const int DxgiFailoverThreshold = 3;
    private const int IdleTicksBeforeRetire = 120;   // ~2s with no subscribers

    private readonly FrameBroadcastHub _hub;
    private readonly string _deviceName;
    private readonly DxgiCaptureService _dxgi = new();
    private readonly ScreenCaptureService _gdi = new();

    // The published frame (what ComposeTick hands to sinks, producer thread only) plus one
    // scratch wrapper. GDI captures write into the scratch and are swapped in only on success,
    // so a failed/timed-out capture can never clear or half-overwrite the frame being served.
    private NativeFrame _frame = new();
    private NativeFrame _gdiFrame = new();
    private readonly List<ClientFrameSink> _sinkScratch = new();
    private CancellationTokenSource? _cts;

    private Screen? _screen;
    private int _screenIndex;
    private DateTime _nextScreenRefreshUtc = DateTime.MinValue;
    private bool _useDxgi = true;
    private bool _dxgiFailLogged;
    private bool _gdiSeedLogged;
    private int _dxgiFailCount;
    private int _dxgiNoFrameTicks;
    private DateTime _nextDxgiRetryUtc = DateTime.MinValue;
    private int _idleTicks;
    private int _screenRefreshRate = 60;

    public string DeviceName => _deviceName;

    /// <summary>Last Loop iteration (ms, TickCount64); the hub watchdog recreates the producer if this goes stale while sinks are attached.</summary>
    public long LastTickMs { get; private set; } = Environment.TickCount64;

    public DisplayCaptureProducer(FrameBroadcastHub hub, string deviceName)
    {
        _hub = hub;
        _deviceName = deviceName;
    }

    private Thread? _thread;
    private int _screenOriginX;
    private int _screenOriginY;

    public void Start()
    {
        if (_thread != null) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token))
        {
            Name = $"DisplayCapture-{_deviceName}",
            IsBackground = true
        };
        _thread.Start();
    }

    private void Loop(CancellationToken token)
    {
        CursorInterop.EnsureInputDesktopAttached();
        long statWindowStartUs = _hub.NowUs();
        int statTicks = 0;
        double statCaptureMs = 0;
        double statComposeMs = 0;
        long nextTickUs = _hub.NowUs();

        while (!token.IsCancellationRequested)
        {
            LastTickMs = Environment.TickCount64;
            long captureUs = 0, composeUs = 0;
            try
            {
                long stageStartUs = _hub.NowUs();
                CaptureTick();
                captureUs = _hub.NowUs() - stageStartUs;

                _hub.FillSinksFor(_deviceName, _sinkScratch);
                stageStartUs = _hub.NowUs();
                ComposeTick(_sinkScratch);
                composeUs = _hub.NowUs() - stageStartUs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hub] {_deviceName} capture tick error: {ex.Message}");
            }

            if (_sinkScratch.Count == 0)
            {
                if (++_idleTicks >= IdleTicksBeforeRetire)
                {
                    _hub.RemoveProducer(this);
                    Dispose();
                    return;
                }
            }
            else
            {
                _idleTicks = 0;
            }

            int maxSinkHz = 60;
            for (int i = 0; i < _sinkScratch.Count; i++)
            {
                if (_sinkScratch[i].TargetFramerate > maxSinkHz) maxSinkHz = _sinkScratch[i].TargetFramerate;
            }
            int effectiveHz = Math.Max(_screenRefreshRate, maxSinkHz);
            long targetIntervalUs = (long)(1_000_000.0 / effectiveHz);

            nextTickUs += targetIntervalUs;
            long nowUs = _hub.NowUs();
            if (nowUs > nextTickUs)
            {
                // Overran this slot: start a fresh period instead of bursting to catch up. A
                // steady cadence (even slightly below the target) reads smoother than catch-up
                // judder, and the sink's drop-oldest policy already discards late frames.
                nextTickUs = nowUs + targetIntervalUs;
            }
            else
            {
                long waitUs = nextTickUs - nowUs;
                if (waitUs > 2000)
                    Thread.Sleep((int)((waitUs - 1000) / 1000));
                while (_hub.NowUs() < nextTickUs)
                {
                    Thread.SpinWait(8);
                }
            }

            statTicks++;
            statCaptureMs += captureUs / 1000.0;
            statComposeMs += composeUs / 1000.0;
            if ((_hub.NowUs() - statWindowStartUs) >= 3_000_000)
            {
                Console.WriteLine($"[Hub] {_deviceName}: ticks={statTicks} ({statTicks / 3.0:F0}/s, target {effectiveHz}Hz) capture={statCaptureMs / Math.Max(statTicks, 1):F1}ms compose={statComposeMs / Math.Max(statTicks, 1):F1}ms sinks={_sinkScratch.Count}");
                statWindowStartUs = _hub.NowUs();
                statTicks = 0;
                statCaptureMs = 0;
                statComposeMs = 0;
            }
        }
    }

    private bool RefreshScreenIfNeeded(bool force = false)
    {
        if (!force && _screen != null && DateTime.UtcNow < _nextScreenRefreshUtc)
            return true;

        _nextScreenRefreshUtc = DateTime.UtcNow.AddSeconds(2);
        var screens = Screen.AllScreens;
        _screen = null;
        _screenIndex = -1;

        for (int i = 0; i < screens.Length; i++)
        {
            if (string.Equals(screens[i].DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase))
            {
                _screen = screens[i];
                _screenIndex = i;
                break;
            }
        }

        var (originX, originY, _, _, hz) = CursorInterop.GetPhysicalScreenBounds(_deviceName);
        _screenOriginX = originX;
        _screenOriginY = originY;
        if (hz > 0) _screenRefreshRate = hz;

        return _screen != null;
    }
    private void CaptureTick()
    {
        if (!RefreshScreenIfNeeded()) return;

        bool captured = false;

        if (_useDxgi)
        {
            captured = _dxgi.CaptureNativeFrame(_deviceName, _frame);
            if (captured)
            {
                _dxgiFailCount = 0;

                if (_frame.Captured)
                {
                    _dxgiNoFrameTicks = 0;
                }
                else if (_frame.Bitmap == null)
                {
                    // A static desktop never presents frames (DWM skips unchanged outputs), so
                    // Desktop Duplication can stay empty indefinitely — seed the published frame
                    // via GDI every ~200ms until DXGI delivers its first real frame.
                    if (++_dxgiNoFrameTicks >= 12)
                    {
                        _dxgiNoFrameTicks = 0;
                        if (AdoptGdiFrame() && !_gdiSeedLogged)
                        {
                            Console.WriteLine($"[Hub] {_deviceName}: static desktop — seeding first frame via GDI until DXGI presents");
                            _gdiSeedLogged = true;
                        }
                    }
                }
            }
            else
            {
                // Instant GDI fallback on the same tick so frame delivery is seamless during
                // mode switches/UAC. The capture lands in the scratch frame and is published
                // only on success — the failed DXGI call never damages the served frame.
                captured = AdoptGdiFrame();

                _dxgiFailCount++;
                if (_dxgiFailCount >= DxgiFailoverThreshold)
                {
                    if (!_dxgiFailLogged)
                    {
                        Console.WriteLine($"[Hub] {_deviceName}: DXGI unavailable (access denied or unsupported) — using GDI capture");
                        _dxgiFailLogged = true;
                    }
                    _useDxgi = false;
                    _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(1); // fast 1s retry
                }
            }
        }
        else
        {
            captured = AdoptGdiFrame();

            // Fast retry of Desktop Duplication (every 1s instead of 5s). The probe runs into
            // the scratch frame: a DXGI timeout (static desktop, nothing published) must not
            // erase the GDI frame currently being served.
            if (DateTime.UtcNow >= _nextDxgiRetryUtc)
            {
                _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(1);
                if (_dxgi.CaptureNativeFrame(_deviceName, _gdiFrame))
                {
                    if (_gdiFrame.Captured)
                        PublishFrame(_gdiFrame);

                    _useDxgi = true;
                    _dxgiFailCount = 0;
                    _dxgiFailLogged = false;
                    _dxgiNoFrameTicks = 0;
                    Console.WriteLine($"[Hub] {_deviceName}: DXGI Desktop Duplication recovered");
                }
            }
        }

        if (!captured)
        {
            // Display may have been renamed/removed (e.g. driver restart: DISPLAY85 -> DISPLAY86)
            RefreshScreenIfNeeded(force: true);
        }
    }

    /// <summary>Captures via GDI into the scratch wrapper; publishes (swaps in) only on success.</summary>
    private bool AdoptGdiFrame()
    {
        if (!_gdi.CaptureNativeFrame(_screenIndex, _gdiFrame))
            return false;
        PublishFrame(_gdiFrame);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="frame"/> the published frame and recycles the previous wrapper as
    /// scratch. Producer thread only — called between CaptureTick and ComposeTick, so no sink
    /// observes the swap mid-compose.
    /// </summary>
    private void PublishFrame(NativeFrame frame)
    {
        var previous = _frame;
        _frame = frame;
        _gdiFrame = previous;
    }

    private void ComposeTick(List<ClientFrameSink> sinks)
    {
        if (_frame.Bitmap == null) return;

        int screenX = _screenOriginX;
        int screenY = _screenOriginY;
        long timestampUs = _hub.NowUs();

        for (int i = 0; i < sinks.Count; i++)
        {
            sinks[i].TryBeginCompose(_frame, screenX, screenY, timestampUs);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _thread?.Join(500); } catch { }
        _dxgi.Dispose();
    }
}
