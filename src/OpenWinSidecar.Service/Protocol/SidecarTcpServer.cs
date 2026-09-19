using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using OpenWinSidecar.Core.Services;
using OpenWinSidecar.Service.Capture;
using OpenWinSidecar.Service.Encoders;
using OpenWinSidecar.Service.Input;
using SIPSorcery.Net;

namespace OpenWinSidecar.Service.Protocol;

public enum StreamCodec
{
    IntraTurbo = 0,
    H264 = 1,
    HEVC = 2,
    AV1 = 3
}

public class SidecarTcpServer : IDisposable
{
    private static bool _topologyEnsured; // Extend topology is flashed once per process, not per session

    private readonly int _port;
    private readonly FrameBroadcastHub _hub = new();
    private readonly ScreenCaptureService _legacyGdiCapture = new(); // legacy SDFR binary clients only
    private readonly InputDispatcher _inputDispatcher = new();
    private readonly int[] _ports;
    private readonly List<TcpListener> _listeners = new();
    private CancellationTokenSource? _cts;
    private readonly List<TcpClient> _activeClients = new();

    private readonly object _settingsLock = new();
    private HostStreamSettings _settings;

    public SidecarTcpServer(int port = 28252, HostStreamSettings? settings = null)
    {
        _settings = settings ?? new HostStreamSettings();
        _port = port;
        _ports = new[] { port, 80, 8080 };
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        foreach (var p in _ports)
        {
            var portNum = p;
            Task.Run(() => AcceptLoopAsync(portNum, _cts.Token));
        }
    }

    private async Task AcceptLoopAsync(int port, CancellationToken token)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            lock (_listeners) { _listeners.Add(listener); }

            Console.WriteLine($"[TCP Server] OpenWinSidecar Server listening on 0.0.0.0:{port}");

            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                // Keep the kernel send buffer small so a slow Wi-Fi leg applies backpressure
                // (producer drops frames — see ClientFrameSink _busy handoff) instead of
                // silently buffering ~0.5s of video that the client will render late.
                client.SendBufferSize = 65536;
                // Enable TCP keepalive so the OS detects dead peers (iPad asleep, Wi-Fi
                // lost, process killed) within ~30s instead of holding a half-open
                // connection for minutes-to-hours. This complements the app-level
                // stale-client sweep that evicts sinks with no recent activity.
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                lock (_activeClients) { _activeClients.Add(client); }
                Console.WriteLine($"[TCP Server] Client connected on port {port} from {client.Client.RemoteEndPoint}");

                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP Server] Listener on port {port} error: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        var buffer = new byte[8192];

        try
        {
            var readCount = await stream.ReadAsync(buffer, token);
            if (readCount <= 0) return;

            var requestText = Encoding.UTF8.GetString(buffer, 0, readCount);

            if (requestText.StartsWith("GET ") || requestText.StartsWith("POST "))
            {
                var firstLine = requestText.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                var path = parts.Length > 1 ? parts[1] : "/";

                if (requestText.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketConnectionAsync(client, stream, requestText, token);
                    return;
                }

                if (path == "/" || path.StartsWith("/?"))
                {
                    await ServeHtmlViewerPageAsync(stream, (client.Client.RemoteEndPoint as IPEndPoint)?.Address, token);
                }
                else if (path == "/app" || path == "/app/" || path.StartsWith("/app?") || path.StartsWith("/app/"))
                {
                    await ServeIosAppLandingPageAsync(stream, token);
                }
                else if (path == "/ios-app.zip" || path == "/ios-app" || path == "/OpenWinSidecar.swiftpm.zip")
                {
                    await ServeIosAppZipAsync(stream, token);
                }
                else if (path == "/apple-touch-icon.png" || path == "/apple-touch-icon-precomposed.png" || path == "/favicon.ico")
                {
                    await ServeAppleTouchIconAsync(stream, token);
                }
                else if (path == "/manifest.json" || path == "/manifest.webmanifest")
                {
                    await ServeManifestAsync(stream, token);
                }
                else if (path.StartsWith("/input"))
                {
                    if (!IsOriginAllowed(requestText))
                    {
                        var forbidden = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(forbidden), token);
                        return;
                    }

                    // Input injection over plain HTTP must present the access password too —
                    // prefer the X-Access-Token / Authorization header over the URL query.
                    var authToken = GetRequiredAuthToken();
                    if (authToken != null && !HttpHeaderMatchesToken(requestText, authToken) && !HttpQueryMatchesToken(path, authToken))
                    {
                        var forbidden = "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(forbidden), token);
                        return;
                    }

                    HandleHttpInput(path);
                    var okResp = "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 2\r\n\r\nOK";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(okResp), token);
                }
                else
                {
                    var notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(notFound), token);
                }
                return;
            }

            // The legacy raw-binary streaming path has no authentication mechanism at all —
            // refuse it entirely whenever an access password is configured.
            if (GetRequiredAuthToken() != null)
                return;

            // Native Binary fallback
            var response = Encoding.ASCII.GetBytes("SIDECAR_OK\n");
            await stream.WriteAsync(response, token);

            var frameHeader = new byte[8];
            while (!token.IsCancellationRequested && client.Connected)
            {
                var (frameBytes, _, _, _) = _legacyGdiCapture.CaptureFrameWithCursor(-1, 0, 0, 65);
                if (frameBytes != null && frameBytes.Length > 0)
                {
                    frameHeader[0] = (byte)'S';
                    frameHeader[1] = (byte)'D';
                    frameHeader[2] = (byte)'F';
                    frameHeader[3] = (byte)'R';
                    BinaryPrimitives.WriteInt32BigEndian(frameHeader.AsSpan(4, 4), frameBytes.Length);

                    await stream.WriteAsync(frameHeader, token);
                    await stream.WriteAsync(frameBytes, token);
                    await stream.FlushAsync(token);
                }

                await Task.Delay(8, token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TCP Server] Client session closed: {ex.Message}");
        }
        finally
        {
            lock (_activeClients) { _activeClients.Remove(client); }
            client.Dispose();
        }
    }

    private async Task HandleWebSocketConnectionAsync(TcpClient client, NetworkStream stream, string request, CancellationToken token)
    {
        var keyLine = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
        if (keyLine == null) return;
        var key = keyLine.Substring(18).Trim();

        // The desktop app owns the codec setting; a device without HEVC still sends
        // codec:intra before its first frame, so no per-session codec query exists.

        // Browsers send Origin on upgrades; a foreign origin must never drive this PC.
        if (!IsOriginAllowed(request))
        {
            Console.WriteLine("[WebSocket] Rejected cross-origin upgrade.");
            return;
        }

        var acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                       "Upgrade: websocket\r\n" +
                       "Connection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(response), token);
        Console.WriteLine("[WebSocket] OpenWinSidecar Session established.");

        using var streamLock = new SemaphoreSlim(1, 1);

        var authToken = GetRequiredAuthToken();

        // Tell the client up front whether it must authenticate (the challenge itself
        // arrives per-attempt inside AuthenticateSessionAsync)
        if (authToken != null)
        {
            await WebCodecsFraming.SendTextAsync(stream, streamLock, "auth:required", token);
        }

        // Authentication gate: when an access password is configured, the session stays
        // completely inert (no stream, no input processing) until the client presents
        // `auth:<token>` as its first WebSocket text message. Without a configured
        // password the server runs open, as before.
        if (authToken != null && !await AuthenticateSessionAsync(stream, streamLock, authToken, RemoteIp(client), token))
        {
            return;
        }

        // Re-flashing the display topology on every session invalidates active Desktop
        // Duplication handles (and flickers all monitors) — ensure it once per process
        if (!_topologyEnsured)
        {
            VirtualDisplayManager.EnableExtendMode();
            _topologyEnsured = true;
        }

        var screens = Screen.AllScreens;
        int selectedDisplay = 0;
        var initialScreen = screens[0];

        // Find the virtual monitor by device name or description
        var virtualMon = DisplayResolutionManager.GetAllMonitorsDetailed().FirstOrDefault(m => m.IsVirtual);
        if (virtualMon != null)
        {
            for (int i = 0; i < screens.Length; i++)
            {
                if (string.Equals(screens[i].DeviceName, virtualMon.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedDisplay = i;
                    initialScreen = screens[i];
                    break;
                }
            }
        }
        else if (screens.Length > 1)
        {
            for (int i = 0; i < screens.Length; i++)
            {
                if (!screens[i].Primary)
                {
                    selectedDisplay = i;
                    initialScreen = screens[i];
                    break;
                }
            }
        }

        var hostDevice = _settings.DeviceName;
        var hostIndex = Array.FindIndex(screens, s => string.Equals(s.DeviceName, hostDevice, StringComparison.OrdinalIgnoreCase));
        if (hostIndex >= 0) { selectedDisplay = hostIndex; initialScreen = screens[hostIndex]; }

        var (originX, originY, physW, physH, hz) = CursorInterop.GetPhysicalScreenBounds(initialScreen.DeviceName);

        // Per-session cancellation. A Console kick (or any forced disconnect) cancels this token
        // and closes the transport, unblocking the input reader and the consumer loop; the
        // consumer's finally then disposes the sink exactly once.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = sessionCts.Token;

        var sink = _hub.CreateSink(stream, streamLock, initialScreen.DeviceName, client.Client.RemoteEndPoint?.ToString() ?? "");
        sink.SetDisconnectHandler(() =>
        {
            try { sessionCts.Cancel(); } catch { }
            try { client.Close(); } catch { }
        });
        lock (_settingsLock)
        {
            _settings.Apply(sink);
            sink.TargetWidth = physW > 0 ? physW : 2360;
            sink.TargetHeight = physH > 0 ? physH : 1640;
        }
        _hub.EnsureProducer(sink.DeviceName);
        // Start the HEVC encoder now so its ~1 s spawn + init overlaps the client handshake
        // instead of delaying the first frame. Adopts the current targets; a later settings
        // change transparently re-inits on first push.
        if (sink.Codec == StreamCodec.HEVC)
            sink.WarmupHevc();
        Console.WriteLine($"[WebSocket] Streaming {initialScreen.DeviceName} ({initialScreen.Bounds.Width}x{initialScreen.Bounds.Height})");

        // The full input-message chain, extracted so both the auth gate and the stream
        // loop can route complete messages through it (each coalesced frame handled once).
        DateTime lastStatsLog = DateTime.MinValue;
        void HandleClientMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (IsHostSettingCommand(text)) return;
            {
                        if (text.StartsWith("codec:"))
                        {
                            var cStr = text.Substring(6).ToLowerInvariant();
                            // Only HEVC is genuinely encoded; every other selection receives JPEG
                            // intra frames, so the packet label must never claim h264/av1/hevc
                            sink.HevcSupported = cStr is "hevc" or "h265";
                            sink.Codec = sink.HevcSupported ? _settings.Codec : StreamCodec.IntraTurbo;
                            Console.WriteLine($"[WebSocket] Client requested codec: {sink.Codec}");
                        }
                        else if (text.StartsWith("decerr:"))
                        {
                            // Client-side WebCodecs decoder failure report — the primary signal
                            // for diagnosing Safari-specific HEVC support issues
                            Console.WriteLine($"[WebSocket] Client decoder error: {text.Substring(7)}");
                        }
                        else if (text == "forceidr")
                        {
                            // Tab became visible again: restart the client's encoder so the next
                            // frame is a fresh IDR (protects against decoder state Safari evicted)
                            sink.ForceIdr();
                        }
                        else if (text.StartsWith("sync:"))
                        {
                            // Latency probe. Echo the client's clock reading back with our own
                            // monotonic timestamp so the client can estimate the clock offset and
                            // compute true glass-to-glass latency (its local now minus the
                            // capture timestamp carried in each frame). Not throttled.
                            var ack = $"syncack:{text.Substring(5)},{_hub.NowUs()}";
                            _ = WebCodecsFraming.SendTextAsync(stream, streamLock, ack, sessionToken);
                        }
                        else if (text.StartsWith("stats:"))
                        {
                            var statsJson = text.Substring(6);

                            // Keep the Console's live clients view current (fps/bitrate/latency).
                            TryUpdateClientStats(sink, statsJson);

                            // Log at most once per 5s per client.
                            var statsNow = DateTime.UtcNow;
                            if ((statsNow - lastStatsLog).TotalSeconds >= 5)
                            {
                                lastStatsLog = statsNow;
                                Console.WriteLine($"[Stats] {sink.DeviceName}: {statsJson}");
                            }
                        }
                        else if (text.StartsWith("cursor:"))
                        {
                            var cMode = text.Substring(7).ToLowerInvariant();
                            sink.ShowHostCursor = (cMode == "host" || cMode == "show_host");
                        }
                        else if (text.StartsWith("display:"))
                        {
                            if (int.TryParse(text.Substring(8), out var d))
                            {
                                var current = Screen.AllScreens;
                                if (d >= 0 && d < current.Length)
                                {
                                    selectedDisplay = d;
                                    sink.DeviceName = current[d].DeviceName;
                                    _hub.EnsureProducer(sink.DeviceName);
                                }
                                else
                                {
                                    Console.WriteLine($"[WebSocket] Ignoring invalid display index {d} (have {current.Length}).");
                                }
                            }
                        }
                        else if (text.StartsWith("quality:"))
                        {
                            if (long.TryParse(text.Substring(8), out var q)) sink.Quality = (int)Math.Clamp(q, 10, 95);
                        }
                        else if (text.StartsWith("fps:"))
                        {
                            TrySetStreamFramerate(sink, text);
                        }
                        else if (text.StartsWith("mode:extend"))
                        {
                            VirtualDisplayManager.EnableExtendMode();
                        }
                        else if (text.StartsWith("mode:mirror"))
                        {
                            VirtualDisplayManager.EnableMirrorMode();
                        }
                        else if (text.StartsWith("set_res:"))
                        {
                            var parts = text.Substring(8).Split(',');
                            if (parts.Length >= 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                            {
                                int requestedHz = 60;
                                if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedHz)) requestedHz = parsedHz;
                                if (requestedHz <= 0) requestedHz = 60;

                                sink.TargetWidth = w;
                                sink.TargetHeight = h;
                                // Monitor refresh is independent of the per-client stream FPS.
                                Console.WriteLine($"[Resolution] Target resolution set: {w}x{h}{(requestedHz > 0 ? $" @ {requestedHz}Hz" : "")}");

                                // Aspect-match the virtual display to the client's screen so the
                                // stream fills it edge-to-edge (idempotent — skips when matched)
                                var applied = DisplayResolutionManager.MatchVirtualDisplayToClient(w, h, requestedHz);
                                if (applied != null)
                                {
                                    Console.WriteLine($"[Resolution] Virtual display mode: {applied.Width}x{applied.Height} @ {applied.RefreshRate}Hz");
                                }
                            }
                        }
                        else if (text.StartsWith("dpi:"))
                        {
                            if (int.TryParse(text.Substring(4), out var dpiPercent))
                            {
                                WindowsDpiService.SetMonitorDpiPercent(selectedDisplay, dpiPercent);
                            }
                        }
                        else if (text.StartsWith("zoom:"))
                        {
                            if (double.TryParse(text.Substring(5), System.Globalization.CultureInfo.InvariantCulture, out var z))
                            {
                                sink.Zoom = Math.Clamp(z, 1.0, 3.0);
                                Console.WriteLine($"[Magnification] UI Zoom set to: {sink.Zoom:F2}x");
                            }
                        }
                        else if (text.StartsWith("depth:"))
                        {
                            // 10-bit Main10 smooths gradients (banding); falls back to 8-bit if the
                            // encoder or decoder rejects it — the encoder restart + fresh hvcC make
                            // the switch seamless, and a failed switch auto-reverts client-side.
                            if (int.TryParse(text.Substring(6), out var depthBits))
                            {
                                sink.Main10Supported = depthBits == 10;
                                sink.ColorDepth = sink.Main10Supported ? _settings.ColorDepth : 8;
                                Console.WriteLine($"[Color] Target color depth set: {sink.ColorDepth}-bit");
                            }
                        }
                        else if (text.StartsWith("input:"))
                        {
                            var parts = text.Substring(6).Split(',');
                            if (parts.Length >= 3 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var px) && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var py))
                            {
                                var action = parts[0];
                                _inputDispatcher.MoveMouseToScreen(sink.DeviceName, selectedDisplay, px, py, sink.Zoom);
                                if (action == "down") _inputDispatcher.MouseClick(true, true);
                                else if (action == "up") _inputDispatcher.MouseClick(true, false);
                            }
                        }
                        else if (text.StartsWith("key:"))
                        {
                            var parts = text.Substring(4).Split(',');
                            if (parts[0] == "text" && parts.Length >= 2)
                            {
                                var rawText = text.Substring(9);
                                _inputDispatcher.SendText(rawText);
                            }
                            else if (parts[0] == "char" && parts.Length >= 2 && parts[1].Length > 0)
                            {
                                _inputDispatcher.SendUnicodeChar(parts[1][0]);
                            }
                            else if (parts.Length >= 2 && ushort.TryParse(parts[1], out var vk))
                            {
                                bool isDown = parts[0] == "down";
                                _inputDispatcher.SendKey(vk, isDown);
                            }
                        }
                        else if (text.StartsWith("scroll:"))
                        {
                            if (int.TryParse(text.Substring(7), out var delta))
                            {
                                _inputDispatcher.MouseScroll(delta);
                            }
                        }
                        else if (text.StartsWith("rightclick"))
                        {
                            _inputDispatcher.MouseClick(false, true);
                            _inputDispatcher.MouseClick(false, false);
                        }
            }
        }

        _ = Task.Run(async () =>
        {
            // Browsers coalesce several WebSocket frames into one TCP segment (Safari does
            // this on the connect burst), and reads can split frames — the assembler yields
            // every complete text message in order. The old one-parse-per-read loop dropped
            // all messages after the first, which is how 'codec:hevc' was being lost.
            var wsBuffer = new byte[4096];
            var assembler = new WsTextMessageAssembler();
            while (!sessionToken.IsCancellationRequested && client.Connected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(wsBuffer, sessionToken);
                    if (bytesRead <= 0) break;

                    sink.MarkActivity();
                    assembler.OnData(wsBuffer, bytesRead, HandleClientMessage);
                }
                catch { break; }
            }
            // The input loop exited — the client is gone (half-close, RST, or dead peer).
            // Cancel the session so the consumer loop stops sending into a dead socket and
            // the sink gets disposed, removing it from the active-clients list. Without this
            // the consumer loop keeps running and the stale client lingers in the UI forever.
            try { sessionCts.Cancel(); } catch { }
        }, token);

        // Consumer loop: the hub's display producer composes frames into the sink and
        // signals it; this loop just encodes and sends. A slow client causes the producer
        // to skip composing (drop-oldest) instead of queueing unbounded latency. On a
        // static desktop no frames are composed at all — WebSocket pings keep the
        // connection alive during those silent periods.
        try
        {
            while (!sessionToken.IsCancellationRequested && client.Connected)
            {
                bool signaled = await sink.WaitFrameAsync(5000, sessionToken);
                if (signaled)
                {
                    await sink.ProcessAndSendAsync(token);
                }
                else
                {
                    await WebCodecsFraming.SendPingAsync(stream, streamLock, token);
                }
            }
        }
        finally
        {
            sink.Dispose();
        }
    }

    // Only explicit supported stream rates are accepted; never change the monitor mode here.
    internal static bool TrySetStreamFramerate(ClientFrameSink sink, string command)
    {
        int fps = command switch { "fps:30" => 30, "fps:60" => 60, _ => 0 };
        if (fps == 0) return false;
        sink.TargetFramerate = fps;
        return true;
    }

    /// <summary>Accepts legacy stats and nullable viewer telemetry without retaining old samples.</summary>
    internal static bool TryUpdateClientStats(ClientFrameSink sink, string json, DateTime? nowUtc = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            int? ReadInt(string name) => root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
                    ? number : null;
            bool? visible = root.TryGetProperty("visible", out var v)
                && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            bool hasFrameAge = root.TryGetProperty("frameAgeMs", out var a);
            double? frameAgeMs = hasFrameAge && a.ValueKind == JsonValueKind.Number
                && a.TryGetDouble(out var age) && double.IsFinite(age) && age >= 0 ? age : null;
            sink.UpdateClientStats(ReadInt("fps") ?? 0, ReadInt("kbps") ?? 0, ReadInt("latencyMs"),
                visible, frameAgeMs, hasFrameAge, nowUtc);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // ------------------------------------------------------------------
    // Access authentication
    // ------------------------------------------------------------------

    private readonly OpenWinSidecar.Core.Services.SidecarRegistryManager _registryManager = new();

    /// <summary>The configured access password, or null when the server runs open.</summary>
    private string? GetRequiredAuthToken()
    {
        try
        {
            var pw = _registryManager.GetSettings().EncryptionPassword;
            return string.IsNullOrWhiteSpace(pw) ? null : pw;
        }
        catch
        {
            return null;
        }
    }

    private static bool FixedTimeStringEquals(string a, string b)
    {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));
    }

    /// <summary>True when the query string carries pw=&lt;token&gt; matching the access password.</summary>
    internal static bool HttpQueryMatchesToken(string path, string token)
    {
        var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            if (p.Length == 2 && p[0] == "pw")
                return FixedTimeStringEquals(Uri.UnescapeDataString(p[1]), token);
        }
        return false;
    }

    private static string? GetHeaderValue(string request, string name)
    {
        foreach (var line in request.Split("\r\n"))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Same-origin policy for browser clients. Browsers always send <c>Origin</c> on WebSocket
    /// upgrades and cross-origin fetches; native tools and scripts typically send none and are
    /// allowed through (they still face the password gate when one is configured). A present
    /// Origin must match the request Host, so a hostile web page cannot drive this PC.
    /// </summary>
    internal static bool IsOriginAllowed(string request)
    {
        var origin = GetHeaderValue(request, "Origin");
        if (string.IsNullOrEmpty(origin)) return true;

        var host = GetHeaderValue(request, "Host");
        if (string.IsNullOrEmpty(host)) return false;

        try
        {
            return string.Equals(new Uri(origin).Authority, host, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// True when the access password is presented in an HTTP header — preferred over the URL
    /// query, which leaks into history/proxies. Accepts <c>X-Access-Token</c> or
    /// <c>Authorization: Bearer</c>. The legacy <c>?pw=</c> query still works.
    /// </summary>
    internal static bool HttpHeaderMatchesToken(string request, string token)
    {
        var direct = GetHeaderValue(request, "X-Access-Token");
        if (!string.IsNullOrEmpty(direct) && FixedTimeStringEquals(direct, token))
            return true;

        var auth = GetHeaderValue(request, "Authorization");
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = auth["Bearer ".Length..].Trim();
            if (bearer.Length > 0 && FixedTimeStringEquals(bearer, token))
                return true;
        }
        return false;
    }

    private static string? RemoteIp(TcpClient client)
    {
        try
        {
            if (client.Client.RemoteEndPoint is System.Net.IPEndPoint ipEp)
                return ipEp.Address.ToString();
        }
        catch { }
        return null;
    }

    // Cross-connection auth backoff: each failed password attempt pushes the client's next
    // allowed attempt out exponentially (2s, 4s, 8s, … capped), so reconnect-loop guessing is
    // throttled while a legitimate typo only costs a couple of seconds. Cleared on success.
    private static readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset BlockedUntil)> _authBackoff = new();

    /// <summary>True when the client must be denied without further attempts right now.</summary>
    private static async Task<bool> WaitForAuthRateLimitAsync(string? clientIp, CancellationToken token)
    {
        if (string.IsNullOrEmpty(clientIp)) return false;
        if (_authBackoff.TryGetValue(clientIp, out var state) && DateTimeOffset.UtcNow < state.BlockedUntil)
        {
            try { await Task.Delay(state.BlockedUntil - DateTimeOffset.UtcNow, token); }
            catch (OperationCanceledException) { return true; }
            if (_authBackoff.TryGetValue(clientIp, out var again) && DateTimeOffset.UtcNow < again.BlockedUntil)
                return true;
        }
        return false;
    }

    private static void RecordAuthFailure(string? clientIp)
    {
        if (string.IsNullOrEmpty(clientIp)) return;
        _authBackoff.AddOrUpdate(clientIp,
            _ => (1, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2)),
            (_, s) =>
            {
                int failures = Math.Min(s.Failures + 1, 10);
                return (failures, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Pow(2, failures)));
            });
    }

    private static void RecordAuthSuccess(string? clientIp)
    {
        if (!string.IsNullOrEmpty(clientIp)) _authBackoff.TryRemove(clientIp, out _);
    }

    /// <summary>
    /// Challenge-response authentication: each attempt sends a fresh random
    /// `authreq:&lt;challenge&gt;` and expects `auth:&lt;hex sha256(password + challenge)&gt;`,
    /// so the password itself never crosses the wire. Three attempts per connection (10-second
    /// window), plus exponential cross-connection backoff per client IP.
    /// </summary>
    private static async Task<bool> AuthenticateSessionAsync(NetworkStream stream, SemaphoreSlim streamLock, string authToken, string? clientIp, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var buf = new byte[4096];
            var assembler = new WsTextMessageAssembler(); // Safari coalesces frames in one read
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                if (await WaitForAuthRateLimitAsync(clientIp, token)) return false;

                string challenge = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
                await WebCodecsFraming.SendTextAsync(stream, streamLock, $"authreq:{challenge}", CancellationToken.None);

                string? presented = null;
                while (presented == null)
                {
                    int n = await stream.ReadAsync(buf, timeoutCts.Token);
                    if (n <= 0) return false;

                    assembler.OnData(buf, n, msg =>
                    {
                        if (msg.StartsWith("auth:", StringComparison.Ordinal))
                            presented = msg["auth:".Length..];
                    });
                }

                string expectedHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(authToken + challenge)));

                if (FixedTimeStringEquals(presented.ToLowerInvariant(), expectedHash.ToLowerInvariant()))
                {
                    await WebCodecsFraming.SendTextAsync(stream, streamLock, "auth:ok", CancellationToken.None);
                    Console.WriteLine("[WebSocket] Session authenticated (challenge-response).");
                    RecordAuthSuccess(clientIp);
                    return true;
                }

                RecordAuthFailure(clientIp);
                await WebCodecsFraming.SendTextAsync(stream, streamLock, "auth:denied", CancellationToken.None);
                Console.WriteLine($"[WebSocket] Authentication attempt {attempt} failed.");
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        Console.WriteLine("[WebSocket] Authentication failed — closing session.");
        return false;
    }

    private static async Task ServeIosAppZipAsync(NetworkStream stream, CancellationToken token)
    {
        string? zipPath = null;
        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OpenWinSidecar.swiftpm.zip"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "ios", "OpenWinSidecar.swiftpm.zip"),
            @"C:\Users\JulianB\source\repos\OpenWinSidecar\ios\OpenWinSidecar.swiftpm.zip"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) { zipPath = c; break; }
        }

        if (zipPath != null && File.Exists(zipPath))
        {
            var zipBytes = await File.ReadAllBytesAsync(zipPath, token);
            var header = $"HTTP/1.1 200 OK\r\n" +
                         $"Content-Type: application/zip\r\n" +
                         $"Content-Disposition: attachment; filename=\"OpenWinSidecar.swiftpm.zip\"\r\n" +
                         $"Content-Length: {zipBytes.Length}\r\n" +
                         $"Access-Control-Allow-Origin: *\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token);
            await stream.WriteAsync(zipBytes, token);
        }
        else
        {
            var notFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(notFound), token);
        }
    }

    private static byte[]? _cachedIconBytes;
    private static async Task ServeAppleTouchIconAsync(NetworkStream stream, CancellationToken token)
    {
        if (_cachedIconBytes == null)
        {
            using var bmp = new System.Drawing.Bitmap(180, 180);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 180, 180),
                System.Drawing.Color.FromArgb(30, 32, 45),
                System.Drawing.Color.FromArgb(12, 14, 20),
                45f))
            {
                g.FillRectangle(bgBrush, 0, 0, 180, 180);
            }

            using (var bluePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(59, 130, 246), 6))
            {
                g.DrawRectangle(bluePen, 30, 36, 120, 84);
            }

            using (var glowBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(34, 40, 112, 76),
                System.Drawing.Color.FromArgb(70, 59, 130, 246),
                System.Drawing.Color.FromArgb(15, 15, 23, 42),
                90f))
            {
                g.FillRectangle(glowBrush, 34, 40, 112, 76);
            }

            using (var standBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(148, 163, 184)))
            {
                g.FillRectangle(standBrush, 83, 122, 14, 18);
                g.FillRectangle(standBrush, 66, 138, 48, 6);
            }

            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            _cachedIconBytes = ms.ToArray();
        }

        var header = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nCache-Control: max-age=86400\r\nContent-Length: {_cachedIconBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token);
        await stream.WriteAsync(_cachedIconBytes, token);
    }

    private static async Task ServeManifestAsync(NetworkStream stream, CancellationToken token)
    {
        var json = @"{
  ""name"": ""OpenWinSidecar"",
  ""short_name"": ""Sidecar"",
  ""start_url"": ""/"",
  ""display"": ""standalone"",
  ""background_color"": ""#000000"",
  ""theme_color"": ""#000000"",
  ""orientation"": ""landscape"",
  ""icons"": [
    {
      ""src"": ""/apple-touch-icon.png"",
      ""sizes"": ""180x180"",
      ""type"": ""image/png"",
      ""purpose"": ""any maskable""
    }
  ]
}";
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/manifest+json\r\nCache-Control: max-age=86400\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task ServeIosAppLandingPageAsync(NetworkStream stream, CancellationToken token)
    {
        var html = @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover'>
    <title>OpenWinSidecar for iPad</title>
    <style>
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body {
            background: #000;
            color: #fff;
            font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Display', 'Segoe UI', Roboto, sans-serif;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 24px 16px;
        }
        .container {
            max-width: 580px;
            width: 100%;
            background: rgba(22, 22, 28, 0.95);
            border: 1px solid rgba(255, 255, 255, 0.16);
            border-radius: 28px;
            padding: 36px 28px;
            box-shadow: 0 24px 70px rgba(0, 0, 0, 0.85);
            text-align: center;
        }
        .app-icon {
            font-size: 54px;
            margin-bottom: 12px;
            display: inline-block;
        }
        h1 {
            font-size: 26px;
            font-weight: 800;
            letter-spacing: -0.5px;
            margin-bottom: 6px;
        }
        .subtitle {
            font-size: 14px;
            color: rgba(255, 255, 255, 0.65);
            margin-bottom: 22px;
        }
        .badges {
            display: flex;
            gap: 8px;
            justify-content: center;
            flex-wrap: wrap;
            margin-bottom: 26px;
        }
        .badge {
            background: rgba(255, 255, 255, 0.08);
            border: 1px solid rgba(255, 255, 255, 0.14);
            border-radius: 20px;
            padding: 5px 12px;
            font-size: 11.5px;
            font-weight: 600;
            color: #93C5FD;
        }
        .btn-download {
            display: flex;
            align-items: center;
            justify-content: center;
            gap: 10px;
            background: #2563EB;
            color: #fff;
            text-decoration: none;
            padding: 16px 24px;
            border-radius: 16px;
            font-size: 16px;
            font-weight: 700;
            box-shadow: 0 8px 24px rgba(37, 99, 235, 0.4);
            transition: transform 0.15s ease, background 0.15s ease;
            margin-bottom: 28px;
        }
        .btn-download:active {
            transform: scale(0.98);
            background: #1D4ED8;
        }
        .steps {
            text-align: left;
            background: rgba(255, 255, 255, 0.04);
            border: 1px solid rgba(255, 255, 255, 0.08);
            border-radius: 20px;
            padding: 20px 22px;
            margin-bottom: 24px;
        }
        .step {
            display: flex;
            gap: 14px;
            margin-bottom: 16px;
        }
        .step:last-child {
            margin-bottom: 0;
        }
        .step-num {
            width: 26px;
            height: 26px;
            border-radius: 50%;
            background: #3B82F6;
            color: #fff;
            font-size: 12px;
            font-weight: bold;
            display: flex;
            align-items: center;
            justify-content: center;
            flex-shrink: 0;
            margin-top: 2px;
        }
        .step-body h3 {
            font-size: 14px;
            font-weight: 700;
            margin-bottom: 3px;
        }
        .step-body p {
            font-size: 12.5px;
            color: rgba(255, 255, 255, 0.65);
            line-height: 1.45;
        }
        .step-body a {
            color: #60A5FA;
            text-decoration: none;
        }
        .footer-link {
            font-size: 13px;
            color: rgba(255, 255, 255, 0.5);
            text-decoration: none;
            transition: color 0.15s ease;
        }
        .footer-link:hover {
            color: #93C5FD;
        }
    </style>
</head>
<body>
    <div class='container'>
        <div class='app-icon'>📱</div>
        <h1>OpenWinSidecar for iPad</h1>
        <div class='subtitle'>Native Hardware HEVC & 120Hz ProMotion Companion App</div>

        <div class='badges'>
            <span class='badge'>⚡ 120Hz ProMotion</span>
            <span class='badge'>🚀 VideoToolbox Hardware Decode</span>
            <span class='badge'>🔌 Sub-1ms USB Cable</span>
            <span class='badge'>🍏 Zero Mac Required</span>
        </div>

        <a href='/OpenWinSidecar.swiftpm.zip' download class='btn-download'>
            <span>📥</span><span>Download iPad App (.swiftpm)</span>
        </a>

        <div class='steps'>
            <div class='step'>
                <div class='step-num'>1</div>
                <div class='step-body'>
                    <h3>Install Apple Swift Playgrounds</h3>
                    <p>Install <a href='https://apps.apple.com/app/swift-playgrounds/id908519492' target='_blank'>Swift Playgrounds</a> (free from Apple on the iPad App Store).</p>
                </div>
            </div>
            <div class='step'>
                <div class='step-num'>2</div>
                <div class='step-body'>
                    <h3>Tap Download Above</h3>
                    <p>Safari will save <code>OpenWinSidecar.swiftpm.zip</code> to your iPad's <b>Files &rarr; Downloads</b> folder. Tap it to unzip.</p>
                </div>
            </div>
            <div class='step'>
                <div class='step-num'>3</div>
                <div class='step-body'>
                    <h3>Open in Swift Playgrounds</h3>
                    <p>In the Files app, tap the unzipped <b><code>OpenWinSidecar.swiftpm</code></b> folder. iPadOS opens it directly in Swift Playgrounds.</p>
                </div>
            </div>
            <div class='step'>
                <div class='step-num'>4</div>
                <div class='step-body'>
                    <h3>Tap 'Run App' (Play button)</h3>
                    <p>The app compiles on your iPad's Apple Silicon in ~3 seconds and launches fullscreen! Tap <b>Preset: USB</b> or enter your Wi-Fi IP to stream.</p>
                </div>
            </div>
        </div>

        <div>
            <a href='/' class='footer-link'>&larr; Open Web Browser Viewer Instead</a>
        </div>
    </div>
</body>
</html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Length: {bytes.Length}\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token);
        await stream.WriteAsync(bytes, token);
    }

    private static string? _viewerTemplate;

    /// <summary>
    /// Loads the embedded WebCodecs viewer page (<c>wwwroot/index.html</c>) once per process.
    /// The page lives as a real HTML/CSS/JS file so it can be edited, linted and diffed; only the
    /// display &lt;option&gt; list and local-preview flag are substituted per request.
    /// </summary>
    private static string GetViewerTemplate()
    {
        if (_viewerTemplate != null) return _viewerTemplate;

        var asm = typeof(SidecarTcpServer).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            throw new InvalidOperationException("Embedded viewer resource 'index.html' was not found.");

        using var resource = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(resource, Encoding.UTF8);
        _viewerTemplate = reader.ReadToEnd();
        return _viewerTemplate;
    }

    // An accidental-preview UX hint, not an authorization boundary. Compare the socket peer
    // with assigned addresses, not Host/DNS or forwarded headers, and publish only a boolean.
    internal static bool RequiresLocalPreview(IPAddress? peerAddress, IEnumerable<IPAddress>? hostAddresses = null)
    {
        if (peerAddress == null) return false;
        static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var peer = Normalize(peerAddress);
        if (IPAddress.IsLoopback(peer)) return true;

        try
        {
            hostAddresses ??= NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address);
            return hostAddresses.Any(address => Normalize(address).Equals(peer));
        }
        catch (NetworkInformationException)
        {
            // Keep serving when adapter enumeration fails; the viewer also checks loopback.
            return false;
        }
    }

    internal static string InjectLocalPreviewRequired(string html, bool required) =>
        html.Replace("/*LOCAL_PREVIEW_REQUIRED*/false",
            required ? "/*LOCAL_PREVIEW_REQUIRED*/true" : "/*LOCAL_PREVIEW_REQUIRED*/false",
            StringComparison.Ordinal);

    private static async Task ServeHtmlViewerPageAsync(NetworkStream stream, IPAddress? peerAddress, CancellationToken token)
    {
        var screens = Screen.AllScreens;
        var virtualMon = DisplayResolutionManager.GetAllMonitorsDetailed().FirstOrDefault(m => m.IsVirtual);
        var displayOptions = new StringBuilder();
        for (int i = 0; i < screens.Length; i++)
        {
            bool isVirtual = virtualMon != null
                ? string.Equals(screens[i].DeviceName, virtualMon.DeviceName, StringComparison.OrdinalIgnoreCase)
                : (!screens[i].Primary);
            var label = screens[i].Primary
                ? $"🖥️ Main Screen ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})"
                : (isVirtual ? $"📱 Virtual iPad Screen ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})" : $"🖥️ Display {i + 1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})");

            displayOptions.Append($"<option value='{i}' {(isVirtual ? "selected" : "")}>{label}</option>");
        }

        var html = GetViewerTemplate().Replace("<!--DISPLAY_OPTIONS-->", displayOptions.ToString());
        html = InjectLocalPreviewRequired(html, RequiresLocalPreview(peerAddress));

        var htmlBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {htmlBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), token);
        await stream.WriteAsync(htmlBytes, token);
        await stream.FlushAsync(token);
    }

    private void HandleHttpInput(string path)
    {
        try
        {
            var query = path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "";
            var kvs = query.Split('&');
            string action = "";
            double x = 0, y = 0;
            int display = 2;

            foreach (var kv in kvs)
            {
                var p = kv.Split('=');
                if (p.Length != 2) continue;
                if (p[0] == "action") action = p[1];
                if (p[0] == "display" && int.TryParse(p[1], out var d)) display = d;
                if (p[0] == "x" && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx)) x = dx;
                if (p[0] == "y" && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy)) y = dy;
            }

            string? deviceName = null;
            var screens = Screen.AllScreens;
            if (display >= 0 && display < screens.Length) deviceName = screens[display].DeviceName;
            _inputDispatcher.MoveMouseToScreen(deviceName, display, x, y);

            if (action == "down") _inputDispatcher.MouseClick(true, true);
            else if (action == "up") _inputDispatcher.MouseClick(true, false);
        }
        catch { }
    }

    public void Stop()
    {
        _cts?.Cancel();
        lock (_listeners)
        {
            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { }
            }
            _listeners.Clear();
        }
        lock (_activeClients)
        {
            foreach (var client in _activeClients)
            {
                client.Dispose();
            }
            _activeClients.Clear();
        }
    }

    /// <summary>Broadcasts a text message to all connected clients.</summary>
    internal static bool IsHostSettingCommand(string text) =>
        new[] { "display:", "fps:", "quality:", "set_res:", "dpi:", "zoom:", "mode:" }
            .Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));

    public void ApplyStreamSettings(HostStreamSettings settings)
    {
        lock (_settingsLock)
        {
            _settings = settings;
            foreach (var sink in _hub.GetAllSinks())
            {
                if (settings.DeviceName != null && !string.Equals(settings.DeviceName, sink.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    // Reconnect drains the old display session and rebuilds input mapping together.
                    sink.RequestDisconnect();
                    continue;
                }
                settings.Apply(sink);
                var (_, _, width, height, _) = CursorInterop.GetPhysicalScreenBounds(sink.DeviceName);
                if (width > 0 && height > 0) { sink.TargetWidth = width; sink.TargetHeight = height; }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _hub.Dispose();
    }
}
