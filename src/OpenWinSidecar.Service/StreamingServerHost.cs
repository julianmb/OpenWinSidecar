using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service;

public sealed class StreamingServerHost : IDisposable
{
    private SidecarDiscoveryServer? _discoveryServer;
    private SidecarTcpServer? _tcpServer;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public int ActivePort { get; private set; } = 28252;

    public event Action<string>? OnLog;
    public event Action<bool>? OnRunningStateChanged;

    public StreamingServerHost()
    {
        // Intercept console output so all internal components' logs are surfaced
        ConsoleLogForwarder.EnsureInitialized();
        ConsoleLogForwarder.OnLogLine += line => OnLog?.Invoke(line);
    }

    public void Start(int port = 28252)
    {
        lock (_lock)
        {
            if (IsRunning) return;

            ActivePort = port;
            TimerResolution.timeBeginPeriod(1);
            DpiAwareness.EnablePerMonitorDpi();

            // Auto-enable USB Cable Forwarding (Android / Apple USB tethering)
            UsbManager.EnableAdbUsbForwarding(port);
            var usbIps = UsbManager.GetUsbIpAddresses();
            if (usbIps.Count > 0)
            {
                OnLog?.Invoke($"[USB Mode] USB IP active: {string.Join(", ", usbIps)}");
            }

            _discoveryServer = new SidecarDiscoveryServer(port);
            _discoveryServer.Start();

            _tcpServer = new SidecarTcpServer(port);
            _tcpServer.Start();

            IsRunning = true;
            OnLog?.Invoke($"[Streaming Engine] In-process server active on ports 80, 8080 and {port}");
            OnRunningStateChanged?.Invoke(true);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsRunning) return;

            try { _discoveryServer?.Stop(); } catch { }
            try { _discoveryServer?.Dispose(); } catch { }
            _discoveryServer = null;

            try { _tcpServer?.Stop(); } catch { }
            try { _tcpServer?.Dispose(); } catch { }
            _tcpServer = null;

            TimerResolution.timeEndPeriod(1);

            IsRunning = false;
            OnLog?.Invoke("[Streaming Engine] In-process server stopped.");
            OnRunningStateChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

internal static class TimerResolution
{
    [DllImport("winmm.dll")]
    internal static extern int timeBeginPeriod(uint msPeriod);

    [DllImport("winmm.dll")]
    internal static extern int timeEndPeriod(uint msPeriod);
}

internal static class DpiAwareness
{
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    internal static void EnablePerMonitorDpi()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                SetProcessDPIAware();
            }
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { }
        }
    }
}

public static class ConsoleLogForwarder
{
    private static bool _initialized;
    private static readonly object _initLock = new();
    public static event Action<string>? OnLogLine;

    public static void EnsureInitialized()
    {
        lock (_initLock)
        {
            if (_initialized) return;
            _initialized = true;

            var originalOut = Console.Out;
            Console.SetOut(new ForwardingTextWriter(originalOut, line => OnLogLine?.Invoke(line)));
        }
    }

    private sealed class ForwardingTextWriter : System.IO.TextWriter
    {
        private readonly System.IO.TextWriter _underlying;
        private readonly Action<string> _callback;

        // Reentrancy guard: a log subscriber that writes back to Console (e.g. the headless
        // ServiceProgram's `OnLog += Console.WriteLine`) would otherwise recurse through this
        // writer forever and crash with a stack overflow.
        [ThreadStatic] private static bool _forwarding;

        public ForwardingTextWriter(System.IO.TextWriter underlying, Action<string> callback)
        {
            _underlying = underlying;
            _callback = callback;
        }

        public override System.Text.Encoding Encoding => _underlying.Encoding;

        public override void WriteLine(string? value)
        {
            _underlying.WriteLine(value);
            if (_forwarding || string.IsNullOrEmpty(value))
                return;

            try
            {
                _forwarding = true;
                _callback(value);
            }
            catch { }
            finally
            {
                _forwarding = false;
            }
        }

        public override void Write(char value) => _underlying.Write(value);
        public override void Write(string? value) => _underlying.Write(value);
    }
}
