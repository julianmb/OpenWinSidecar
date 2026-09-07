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
    private bool _disposed;

    internal long NowUs() => _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    public ClientFrameSink CreateSink(NetworkStream stream, SemaphoreSlim streamLock, string deviceName)
    {
        return new ClientFrameSink(this, stream, streamLock, deviceName);
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

    internal List<ClientFrameSink> GetSinksFor(string deviceName)
    {
        lock (_sync)
        {
            List<ClientFrameSink>? matches = null;
            foreach (var sink in _sinks)
            {
                if (string.Equals(sink.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    matches ??= new List<ClientFrameSink>();
                    matches.Add(sink);
                }
            }
            return matches ?? new List<ClientFrameSink>();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

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
    private const int DxgiFailoverThreshold = 5;
    private const int IdleTicksBeforeRetire = 120;   // ~2s with no subscribers

    private readonly FrameBroadcastHub _hub;
    private readonly string _deviceName;
    private readonly DxgiCaptureService _dxgi = new();
    private readonly ScreenCaptureService _gdi = new();
    private readonly NativeFrame _frame = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

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

    public string DeviceName => _deviceName;

    public DisplayCaptureProducer(FrameBroadcastHub hub, string deviceName)
    {
        _hub = hub;
        _deviceName = deviceName;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        long statWindowStartUs = _hub.NowUs();
        int statTicks = 0;
        double statCaptureMs = 0;
        double statComposeMs = 0;

        while (!token.IsCancellationRequested)
        {
            long tickStartUs = _hub.NowUs();

            long captureUs = 0, composeUs = 0;
            try
            {
                long stageStartUs = _hub.NowUs();
                CaptureTick();
                captureUs = _hub.NowUs() - stageStartUs;

                stageStartUs = _hub.NowUs();
                ComposeTick();
                composeUs = _hub.NowUs() - stageStartUs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hub] {_deviceName} capture tick error: {ex.Message}");
            }

            if (HasNoSubscribers())
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

            int elapsedMs = (int)((_hub.NowUs() - tickStartUs) / 1000);
            int delayMs = Math.Max(1, 16 - elapsedMs);
            await Task.Delay(delayMs, token);

            statTicks++;
            statCaptureMs += captureUs / 1000.0;
            statComposeMs += composeUs / 1000.0;
            if ((_hub.NowUs() - statWindowStartUs) >= 3_000_000)
            {
                var sinks = _hub.GetSinksFor(_deviceName);
                Console.WriteLine($"[Hub] {_deviceName}: ticks={statTicks} ({statTicks / 3.0:F0}/s) capture={statCaptureMs / Math.Max(statTicks, 1):F1}ms compose={statComposeMs / Math.Max(statTicks, 1):F1}ms sinks={sinks.Count}");
                statWindowStartUs = _hub.NowUs();
                statTicks = 0;
                statCaptureMs = 0;
                statComposeMs = 0;
            }
        }
    }

    private bool HasNoSubscribers()
    {
        var sinks = _hub.GetSinksFor(_deviceName);
        return sinks.Count == 0;
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

        return _screen != null;
    }
    private void CaptureTick()
    {
        if (!RefreshScreenIfNeeded()) return;

        bool captured = false;

        if (_useDxgi)
        {
            captured = _dxgi.CaptureNativeFrame(_deviceName, _frame);
            if (!captured)
            {
                _dxgiFailCount++;
                if (_dxgiFailCount > DxgiFailoverThreshold)
                {
                    if (!_dxgiFailLogged)
                    {
                        Console.WriteLine($"[Hub] {_deviceName}: DXGI unavailable (access denied or unsupported) — using GDI capture");
                        _dxgiFailLogged = true;
                    }
                    _useDxgi = false;
                }
            }
            else
            {
                _dxgiFailCount = 0;

                // A static desktop never presents frames (DWM skips unchanged outputs), so
                // Desktop Duplication can stay empty indefinitely — seed the bitmap via GDI
                // every ~200ms until DXGI delivers its first real frame.
                if (_frame.Bitmap == null && ++_dxgiNoFrameTicks >= 12)
                {
                    _dxgiNoFrameTicks = 0;
                    if (_gdi.CaptureNativeFrame(_screenIndex, _frame))
                    {
                        if (!_gdiSeedLogged)
                        {
                            Console.WriteLine($"[Hub] {_deviceName}: static desktop — seeding first frame via GDI until DXGI presents");
                            _gdiSeedLogged = true;
                        }
                    }
                }
            }
        }

        if (!_useDxgi)
        {
            captured = _gdi.CaptureNativeFrame(_screenIndex, _frame);

            // Retry Desktop Duplication on a time basis — a just-retired producer's
            // duplication handle or a topology flash can make DuplicateOutput fail for
            // a few seconds (E_INVALIDARG), after which it works again.
            if (captured && DateTime.UtcNow >= _nextDxgiRetryUtc)
            {
                _nextDxgiRetryUtc = DateTime.UtcNow.AddSeconds(2);
                if (_dxgi.CaptureNativeFrame(_deviceName, _frame))
                {
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

    private void ComposeTick()
    {
        if (_frame.Bitmap == null) return;

        int screenX = _screen?.Bounds.X ?? 0;
        int screenY = _screen?.Bounds.Y ?? 0;
        long timestampUs = _hub.NowUs();

        var sinks = _hub.GetSinksFor(_deviceName);
        foreach (var sink in sinks)
        {
            sink.TryBeginCompose(_frame, screenX, screenY, timestampUs);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(500); } catch { }
        _dxgi.Dispose();
    }
}
