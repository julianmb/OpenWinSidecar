using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using OpenWinSidecar.Core.Services;
using OpenWinSidecar.Service.Capture;
using OpenWinSidecar.Service.Encoders;
using OpenWinSidecar.Service.Input;

namespace OpenWinSidecar.Service.Protocol;

public enum StreamCodec
{
    IntraTurbo = 0,
    H264 = 1,
    HEVC = 2,
    AV1 = 3
}

public class SpacedeskTcpServer : IDisposable
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

    public SpacedeskTcpServer(int port = 28252)
    {
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
                client.SendBufferSize = 524288;
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
                    await ServeHtmlViewerPageAsync(stream, token);
                }
                else if (path.StartsWith("/input"))
                {
                    // Input injection over plain HTTP must present the access password too
                    var authToken = GetRequiredAuthToken();
                    if (authToken != null && !HttpQueryMatchesToken(path, authToken))
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
            var response = Encoding.ASCII.GetBytes("SPACEDESK_OK\n");
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
        if (authToken != null && !await AuthenticateSessionAsync(stream, streamLock, authToken, token))
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
        int selectedDisplay = screens.Length > 2 ? 2 : 0;
        var initialScreen = screens.Length > 2 ? screens[2] : screens[0];

        var sink = _hub.CreateSink(stream, streamLock, initialScreen.DeviceName);
        sink.TargetWidth = 1180;
        sink.TargetHeight = 820;
        _hub.EnsureProducer(sink.DeviceName);
        Console.WriteLine($"[WebSocket] Streaming {initialScreen.DeviceName} ({initialScreen.Bounds.Width}x{initialScreen.Bounds.Height})");

        // The full input-message chain, extracted so both the auth gate and the stream
        // loop can route complete messages through it (each coalesced frame handled once).
        void HandleClientMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            {
                        if (text.StartsWith("codec:"))
                        {
                            var cStr = text.Substring(6).ToLowerInvariant();
                            // Only HEVC is genuinely encoded; every other selection receives JPEG
                            // intra frames, so the packet label must never claim h264/av1/hevc
                            sink.Codec = (cStr == "hevc" || cStr == "h265") ? StreamCodec.HEVC : StreamCodec.IntraTurbo;
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
                        else if (text.StartsWith("cursor:"))
                        {
                            var cMode = text.Substring(7).ToLowerInvariant();
                            sink.ShowHostCursor = (cMode == "host" || cMode == "show_host");
                        }
                        else if (text.StartsWith("display:"))
                        {
                            if (int.TryParse(text.Substring(8), out var d))
                            {
                                selectedDisplay = d;
                                var current = Screen.AllScreens;
                                if (d >= 0 && d < current.Length)
                                {
                                    sink.DeviceName = current[d].DeviceName;
                                    _hub.EnsureProducer(sink.DeviceName);
                                }
                            }
                        }
                        else if (text.StartsWith("quality:"))
                        {
                            if (long.TryParse(text.Substring(8), out var q)) sink.Quality = (int)Math.Clamp(q, 10, 95);
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
                                sink.TargetWidth = w;
                                sink.TargetHeight = h;
                                Console.WriteLine($"[Resolution] Target resolution set: {w}x{h}");

                                // Aspect-match the virtual display to the client's screen so the
                                // stream fills it edge-to-edge (idempotent — skips when matched)
                                var applied = DisplayResolutionManager.MatchVirtualDisplayToClient(w, h);
                                if (applied != null)
                                    Console.WriteLine($"[Resolution] Virtual display mode: {applied.Width}x{applied.Height} @ {applied.RefreshRate}Hz");
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
                        else if (text.StartsWith("input:"))
                        {
                            var parts = text.Substring(6).Split(',');
                            if (parts.Length >= 3 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var px) && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var py))
                            {
                                var action = parts[0];
                                _inputDispatcher.MoveMouseToScreen(selectedDisplay, px, py, sink.Zoom);
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
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(wsBuffer, token);
                    if (bytesRead <= 0) break;

                    assembler.OnData(wsBuffer, bytesRead, HandleClientMessage);
                }
                catch { break; }
            }
        }, token);

        // Consumer loop: the hub's display producer composes frames into the sink and
        // signals it; this loop just encodes and sends. A slow client causes the producer
        // to skip composing (drop-oldest) instead of queueing unbounded latency. On a
        // static desktop no frames are composed at all — WebSocket pings keep the
        // connection alive during those silent periods.
        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                bool signaled = await sink.WaitFrameAsync(5000, token);
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

    // ------------------------------------------------------------------
    // Access authentication
    // ------------------------------------------------------------------

    private readonly OpenWinSidecar.Core.Services.SpacedeskRegistryManager _registryManager = new();

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
    private static bool HttpQueryMatchesToken(string path, string token)
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

    /// <summary>
    /// Challenge-response authentication: each attempt sends a fresh random
    /// `authreq:&lt;challenge&gt;` and expects `auth:&lt;hex sha256(password + challenge)&gt;`,
    /// so the password itself never crosses the wire. Plaintext is still accepted as a
    /// fallback for legacy tools. Three attempts per connection (10-second window).
    /// </summary>
    private static async Task<bool> AuthenticateSessionAsync(NetworkStream stream, SemaphoreSlim streamLock, string authToken, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var buf = new byte[4096];
            var assembler = new WsTextMessageAssembler(); // Safari coalesces frames in one read
            for (int attempt = 1; attempt <= 3; attempt++)
            {
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

                // Preferred: SHA-256(password + challenge) hex. Fallback: plaintext password
                // (legacy tools) — a sniffed plaintext is useless against future challenges.
                string expectedHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(authToken + challenge)));

                bool hashMatch = FixedTimeStringEquals(presented.ToLowerInvariant(), expectedHash.ToLowerInvariant());
                bool plainMatch = FixedTimeStringEquals(presented, authToken);

                if (hashMatch || plainMatch)
                {
                    await WebCodecsFraming.SendTextAsync(stream, streamLock, "auth:ok", CancellationToken.None);
                    Console.WriteLine(plainMatch
                        ? "[WebSocket] Session authenticated (plaintext fallback — client should upgrade)."
                        : "[WebSocket] Session authenticated (challenge-response).");
                    return true;
                }

                await WebCodecsFraming.SendTextAsync(stream, streamLock, "auth:denied", CancellationToken.None);
                Console.WriteLine($"[WebSocket] Authentication attempt {attempt} failed.");
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        Console.WriteLine("[WebSocket] Authentication failed — closing session.");
        return false;
    }

    /// <summary>
    /// A buffering WebSocket text-message parser. Browsers legitimately coalesce several
    /// WebSocket frames into one TCP segment (Safari does this on the connect burst), and
    /// reads may also split a frame across segments — the old one-parse-per-read loop
    /// dropped every coalesced message after the first, losing 'codec:hevc' and friends.
    /// Feed every read; complete text messages come out in order.
    /// </summary>
    private sealed class WsTextMessageAssembler
    {
        private readonly byte[] _acc = new byte[64 * 1024];
        private int _accLen;

        public void OnData(byte[] data, int length, Action<string> onMessage)
        {
            if (_accLen + length > _acc.Length) _accLen = 0; // malformed overflow — reset

            Buffer.BlockCopy(data, 0, _acc, _accLen, length);
            _accLen += length;

            // Decode as many complete frames as are buffered
            int pos = 0;
            while (true)
            {
                if (_accLen - pos < 2) break;

                bool masked = (_acc[pos + 1] & 0x80) != 0;
                int payloadLen = _acc[pos + 1] & 0x7F;
                int headerLen = 2;

                if (payloadLen == 126)
                {
                    if (_accLen - pos < 4) break;
                    payloadLen = (_acc[pos + 2] << 8) | _acc[pos + 3];
                    headerLen = 4;
                }
                else if (payloadLen == 127)
                {
                    if (_accLen - pos < 10) break;
                    payloadLen = (int)((long)_acc[pos + 2] << 56 | (long)_acc[pos + 3] << 48 |
                                       (long)_acc[pos + 4] << 40 | (long)_acc[pos + 5] << 32 |
                                       (long)_acc[pos + 6] << 24 | (long)_acc[pos + 7] << 16 |
                                       (long)_acc[pos + 8] << 8  | _acc[pos + 9]);
                    headerLen = 10;
                }

                int maskLen = masked ? 4 : 0;
                int frameEnd = pos + headerLen + maskLen + payloadLen;
                if (frameEnd > _accLen) break; // partial frame — wait for more data

                bool isText = (_acc[pos] & 0x0F) == 0x1;
                bool isFinal = (_acc[pos] & 0x80) != 0;

                if (isText && isFinal && payloadLen > 0)
                {
                    var decoded = new byte[payloadLen];
                    int dataStart = pos + headerLen + maskLen;
                    if (masked)
                    {
                        for (int i = 0; i < payloadLen; i++)
                            decoded[i] = (byte)(_acc[dataStart + i] ^ _acc[pos + headerLen + (i & 3)]);
                    }
                    else
                    {
                        Buffer.BlockCopy(_acc, dataStart, decoded, 0, payloadLen);
                    }
                    onMessage(Encoding.UTF8.GetString(decoded));
                }

                pos = frameEnd;
            }

            // Keep any trailing partial frame for the next read
            if (pos > 0)
            {
                Buffer.BlockCopy(_acc, pos, _acc, 0, _accLen - pos);
                _accLen -= pos;
            }
        }
    }

    private static async Task ServeHtmlViewerPageAsync(NetworkStream stream, CancellationToken token)
    {
        var screens = Screen.AllScreens;
        var displayOptions = new StringBuilder();
        for (int i = 0; i < screens.Length; i++)
        {
            var isVirtual = (screens.Length > 2 && i == 2) || (!screens[i].Primary);
            var label = screens[i].Primary
                ? $"🖥️ Main Screen ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})"
                : (screens.Length > 2 && i == 2 ? $"📱 Virtual iPad Screen ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})" : $"🖥️ Display {i + 1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})");

            displayOptions.Append($"<option value='{i}' {(isVirtual ? "selected" : "")}>{label}</option>");
        }

        var html = $@"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover'>
    <meta name='apple-mobile-web-app-capable' content='yes'>
    <meta name='apple-mobile-web-app-status-bar-style' content='black-translucent'>
    <meta name='apple-mobile-web-app-title' content='OpenWinSidecar'>
    <meta name='mobile-web-app-capable' content='yes'>
    <meta name='theme-color' content='#000000'>
    <title>OpenWinSidecar Display</title>
    <style>
        * {{ margin: 0; padding: 0; box-sizing: border-box; }}
        html, body {{
            width: 100%; height: 100%;
            background: #000;
            overflow: hidden;
            touch-action: none;
            user-select: none;
            -webkit-user-select: none;
            -webkit-touch-callout: none;
            font-family: -apple-system, BlinkMacSystemFont, 'SF Pro Display', 'Segoe UI', Roboto, sans-serif;
            cursor: none !important;
        }}
        #container {{
            position: relative;
            width: 100vw;
            height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            background: #000;
            transform: translateZ(0);
            -webkit-transform: translate3d(0, 0, 0);
            backface-visibility: hidden;
            perspective: 1000px;
            will-change: transform;
            cursor: none !important;
        }}
        canvas {{
            width: 100%;
            height: 100%;
            object-fit: contain;
            cursor: none !important;
            image-rendering: -webkit-optimize-contrast;
            image-rendering: pixelated;
            transform: translateZ(0);
            -webkit-transform: translate3d(0, 0, 0);
            will-change: transform;
        }}
        #pointer-dot {{
            position: absolute;
            width: 14px;
            height: 14px;
            border-radius: 50%;
            background: rgba(59, 130, 246, 0.9);
            border: 2px solid #FFFFFF;
            box-shadow: 0 0 10px rgba(59, 130, 246, 0.8);
            pointer-events: none;
            transform: translate(-50%, -50%);
            display: none;
            z-index: 15;
            transition: transform 0.04s ease-out;
        }}
        #top-pill {{
            position: absolute;
            top: env(safe-area-inset-top, 10px);
            left: 50%;
            transform: translateX(-50%);
            background: rgba(18, 18, 24, 0.78);
            color: #fff;
            padding: 6px 16px;
            border-radius: 30px;
            font-size: 12px;
            font-weight: 600;
            backdrop-filter: blur(20px);
            -webkit-backdrop-filter: blur(20px);
            display: flex;
            align-items: center;
            gap: 8px;
            border: 1px solid rgba(255, 255, 255, 0.18);
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.45);
            z-index: 50;
            cursor: pointer;
            opacity: 0.88;
            transition: all 0.25s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        #top-pill:hover, #top-pill:active {{
            opacity: 1;
            transform: translateX(-50%) scale(1.05);
            background: rgba(28, 28, 36, 0.95);
            border-color: rgba(96, 165, 250, 0.6);
        }}
        /* State colors on the blue/yellow axis (readable under red-green color-vision
           deficiencies); every state is also distinguishable by shape or text alone. */
        .dot {{ width: 8px; height: 8px; background: #7CB7FF; border-radius: 50%; display: inline-block; animation: pulse 2s infinite; }}
        .gear {{ font-size: 14px; opacity: 0.85; }}
        
        select, button {{
            background: rgba(30, 30, 38, 0.9);
            color: #fff;
            border: 1px solid rgba(255,255,255,0.2);
            padding: 10px 14px;
            border-radius: 12px;
            font-size: 13px;
            font-weight: 600;
            cursor: pointer;
            width: 100%;
        }}
        button.action-btn {{
            background: #3B82F6;
            border-color: #60A5FA;
        }}
        #settings-modal {{
            position: fixed;
            inset: 0;
            background: rgba(0, 0, 0, 0.65);
            backdrop-filter: blur(16px);
            -webkit-backdrop-filter: blur(16px);
            display: none;
            align-items: center;
            justify-content: center;
            z-index: 100;
            animation: fadeIn 0.2s ease-out;
        }}
        #settings-modal.active {{
            display: flex;
        }}
        .modal-card {{
            width: 90%;
            max-width: 440px;
            background: rgba(22, 22, 28, 0.96);
            border: 1px solid rgba(255, 255, 255, 0.18);
            border-radius: 24px;
            padding: 24px;
            box-shadow: 0 24px 60px rgba(0, 0, 0, 0.85);
            color: #fff;
            animation: popIn 0.22s cubic-bezier(0.16, 1, 0.3, 1);
        }}
        @keyframes fadeIn {{ from {{ opacity: 0; }} to {{ opacity: 1; }} }}
        @keyframes popIn {{ from {{ opacity: 0; transform: scale(0.92); }} to {{ opacity: 1; transform: scale(1); }} }}
        .modal-header {{
            display: flex;
            align-items: center;
            justify-content: space-between;
            margin-bottom: 18px;
            padding-bottom: 14px;
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
        }}
        .modal-close {{
            background: rgba(255, 255, 255, 0.12);
            border: none;
            color: #fff;
            width: 32px;
            height: 32px;
            border-radius: 50%;
            font-size: 14px;
            font-weight: bold;
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
        }}
        .modal-body {{
            display: flex;
            flex-direction: column;
            gap: 14px;
        }}
        .setting-group {{
            display: flex;
            flex-direction: column;
            gap: 6px;
        }}
        .setting-group label {{
            font-size: 12px;
            font-weight: 600;
            color: rgba(255, 255, 255, 0.7);
        }}
        details.advanced {{
            border-top: 1px solid rgba(255, 255, 255, 0.12);
            padding-top: 12px;
            margin-top: 4px;
        }}
        details.advanced summary {{
            font-size: 12.5px;
            font-weight: 600;
            color: rgba(255, 255, 255, 0.7);
            cursor: pointer;
            padding: 4px 0 10px;
            list-style: none;
        }}
        details.advanced summary::before {{
            content: '› ';
            display: inline-block;
            transition: transform 0.15s ease;
        }}
        details.advanced[open] summary::before {{
            transform: rotate(90deg);
        }}
        details.advanced .setting-group + .setting-group {{
            margin-top: 12px;
        }}
        .btn-grid {{
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 8px;
        }}
        .btn-grid button {{
            padding: 10px 8px;
            font-size: 12px;
        }}
        .modal-footer {{
            margin-top: 18px;
            display: flex;
            justify-content: flex-end;
        }}
        .done-btn {{
            background: #3B82F6;
            border-color: #60A5FA;
            padding: 12px 24px;
            font-size: 14px;
            font-weight: 700;
            border-radius: 14px;
            width: 100%;
        }}
        @keyframes pulse {{ 0% {{ opacity: 1; transform: scale(1); }} 50% {{ opacity: 0.4; transform: scale(0.85); }} 100% {{ opacity: 1; transform: scale(1); }} }}
        #auth-overlay {{
            position: fixed; inset: 0;
            background: rgba(0,0,0,0.78);
            backdrop-filter: blur(18px);
            -webkit-backdrop-filter: blur(18px);
            display: none; align-items: center; justify-content: center;
            z-index: 200;
        }}
        #auth-overlay.active {{ display: flex; }}
        .auth-card {{
            width: 84%; max-width: 360px;
            background: rgba(22,22,28,0.97);
            border: 1px solid rgba(255,255,255,0.16);
            border-radius: 20px; padding: 28px 24px;
            text-align: center; color: #fff;
        }}
        .auth-title {{ font-size: 15px; font-weight: 700; margin: 10px 0 16px; }}
        #auth-input {{
            width: 100%; box-sizing: border-box;
            background: rgba(255,255,255,0.08);
            border: 1px solid rgba(255,255,255,0.22);
            border-radius: 12px; padding: 12px 14px;
            font-size: 15px; color: #fff; text-align: center; outline: none;
        }}
        #auth-input:focus {{ border-color: #3B82F6; }}
        #auth-error {{ min-height: 16px; font-size: 12px; color: #F2C94C; margin-top: 8px; }}
        .auth-btn {{
            width: 100%; margin-top: 10px;
            background: #3B82F6; border: none; border-radius: 12px;
            padding: 12px; font-size: 14px; font-weight: 700; color: #fff;
        }}
    </style>
</head>
<body>
    <div id='container'>
        <div id='top-pill' onclick='toggleSettingsModal()'>
            <span class='dot'></span>
            <span id='fps'>— FPS</span>
            <span class='gear'>⚙</span>
        </div>

        <div id='settings-modal' onclick='onModalBackdropClick(event)'>
            <div class='modal-card' onclick='event.stopPropagation()'>
                    <div class='modal-header'>
                        <div style='display:flex;align-items:center;gap:8px;'>
                            <span style='font-size:16px;font-weight:700;'>Settings</span>
                        </div>
                        <button class='modal-close' onclick='closeSettingsModal()'>✕</button>
                    </div>

                <div class='modal-body'>
                    <div class='setting-group'>
                        <label>Display</label>
                        <select id='display-select' onchange='changeDisplay(this.value)'>
                            {displayOptions}
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>Quality</label>
                        <select id='quality-select' onchange='changeQuality(this.value)'>
                            <option value='50'>50% — fastest</option>
                            <option value='65'>65% — balanced</option>
                            <option value='80' selected>80% — high detail</option>
                            <option value='90'>90% — ultra crisp</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>Cursor</label>
                        <select id='cursor-select' onchange='changeCursorMode(this.value)'>
                            <option value='host' selected>Streamed Windows cursor</option>
                            <option value='touch'>Hidden (touch mode)</option>
                            <option value='client'>Browser cursor</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>Actions</label>
                        <div class='btn-grid'>
                            <button class='action-btn' onclick='toggleFullscreen()'>⛶ Fullscreen</button>
                            <button id='kbd-btn' onclick='toggleVirtualKeyboard()'>⌨ Keyboard</button>
                        </div>
                    </div>

                    <details class='advanced'>
                        <summary>More options</summary>
                        <div class='setting-group'>
                            <label>Video codec</label>
                            <select id='codec-select' onchange='changeCodec(this.value)'>
                                <option value='hevc' selected>HEVC / H.265 (hardware)</option>
                                <option value='intra'>Intra JPEG (fallback)</option>
                            </select>
                        </div>

                        <div class='setting-group'>
                            <label>Resolution</label>
                            <select id='res-select' onchange='changeResolutionPreset(this.value)'>
                                <option value='auto' selected>Auto-detect this device (recommended)</option>
                                <optgroup label='iPad 10.9-inch / 11-inch Air / 10th-11th Gen'>
                                    <option value='1180x820'>1180 x 820 — @2x logical</option>
                                    <option value='2360x1640'>2360 x 1640 — native</option>
                                </optgroup>
                                <optgroup label='iPad Pro 11-inch (M4)'>
                                    <option value='1210x834'>1210 x 834 — @2x logical</option>
                                    <option value='2420x1668'>2420 x 1668 — native</option>
                                </optgroup>
                                <optgroup label='iPad Pro 11-inch (1st–4th Gen)'>
                                    <option value='1194x834'>1194 x 834 — @2x logical</option>
                                    <option value='2388x1668'>2388 x 1668 — native</option>
                                </optgroup>
                                <optgroup label='iPad Pro 13-inch (M4)'>
                                    <option value='1376x1032'>1376 x 1032 — @2x logical</option>
                                    <option value='2752x2064'>2752 x 2064 — native</option>
                                </optgroup>
                                <optgroup label='iPad Pro 12.9-inch / Air 13-inch'>
                                    <option value='1366x1024'>1366 x 1024 — @2x logical</option>
                                    <option value='2732x2048'>2732 x 2048 — native</option>
                                </optgroup>
                                <optgroup label='iPad 10.2-inch (7th–9th Gen)'>
                                    <option value='1080x810'>1080 x 810 — @2x logical</option>
                                    <option value='2160x1620'>2160 x 1620 — native</option>
                                </optgroup>
                                <optgroup label='iPad 9.7-inch & iPad mini Retina'>
                                    <option value='1024x768'>1024 x 768 — @2x logical</option>
                                    <option value='2048x1536'>2048 x 1536 — native</option>
                                </optgroup>
                                <optgroup label='iPad mini 8.3-inch (6th Gen & A17 Pro)'>
                                    <option value='1133x744'>1133 x 744 — @2x logical</option>
                                    <option value='2266x1488'>2266 x 1488 — native</option>
                                </optgroup>
                                <optgroup label='PC Standard'>
                                    <option value='1920x1080'>1920 x 1080 — Full HD</option>
                                    <option value='2560x1440'>2560 x 1440 — QHD</option>
                                </optgroup>
                            </select>
                        </div>

                        <div class='setting-group'>
                            <label>Windows display scale</label>
                            <select id='dpi-select' onchange='changeDpi(this.value)'>
                                <option value='100'>100% — native</option>
                                <option value='125'>125%</option>
                                <option value='150'>150%</option>
                                <option value='175' selected>175% — recommended</option>
                                <option value='200'>200%</option>
                                <option value='225'>225%</option>
                            </select>
                        </div>

                        <div class='setting-group'>
                            <label>UI magnification</label>
                            <select id='zoom-select' onchange='changeZoom(this.value)'>
                                <option value='1.0' selected>1.0x — full desktop</option>
                                <option value='1.25'>1.25x</option>
                                <option value='1.5'>1.5x</option>
                                <option value='1.75'>1.75x</option>
                                <option value='2.0'>2.0x</option>
                            </select>
                        </div>

                        <div class='setting-group'>
                            <label>Aspect</label>
                            <div class='btn-grid'>
                                <button id='fit-btn' onclick='toggleFitMode()'>📐 Fit / Stretch</button>
                            </div>
                        </div>
                    </details>
                </div>

                <div class='modal-footer'>
                    <button class='done-btn' onclick='closeSettingsModal()'>Done</button>
                </div>
            </div>
        </div>

        <div id='auth-overlay' onclick='onAuthBackdropClick(event)'>
            <div class='auth-card'>
                <div style='font-size:34px;'>🔒</div>
                <div class='auth-title'>This screen is password protected</div>
                <input id='auth-input' type='password' placeholder='Password' autocomplete='off'>
                <div id='auth-error'></div>
                <button class='auth-btn' onclick='submitAuth()'>Watch screen</button>
            </div>
        </div>

        <input id='hidden-kbd-input' type='text' autocomplete='off' autocorrect='off' autocapitalize='off' spellcheck='false' style='position:fixed;top:-100px;left:-100px;opacity:0;pointer-events:none;font-size:16px;'>
        <div id='pointer-dot'></div>
        <canvas id='viewport'></canvas>
    </div>

    <script>
        const canvas = document.getElementById('viewport');
        const ctx = canvas.getContext('2d', {{
            alpha: false,
            desynchronized: true,
            willReadFrequently: false
        }});
        
        const container = document.getElementById('container');
        const pointerDot = document.getElementById('pointer-dot');
        const fpsLabel = document.getElementById('fps');
        
        const resSelect = document.getElementById('res-select');
        const qualitySelect = document.getElementById('quality-select');
        const codecSelect = document.getElementById('codec-select');

        let ws;
        let frameCount = 0;
        let lastFpsUpdate = performance.now();
        let isRendering = false;
        let videoDecoder = null;
        let hevcReady = false;
        let hevcSupportKnown = false;
        let hevcSupported = false;

        // Probe WebCodecs once per device: ask the browser whether it can hardware-decode
        // HEVC. Fail-safe by design: never throws, never hangs (2s timeout), and answers
        // false on any doubt — the stream starts on JPEG and upgrades only on a clear yes.
        async function detectHevcSupportSafe() {{
            if (hevcSupportKnown) return hevcSupported;
            if (!window.VideoDecoder || !VideoDecoder.isConfigSupported) {{
                hevcSupportKnown = true;
                hevcSupported = false;
                return false;
            }}
            try {{
                // optimizeForLatency omitted: some WebKit builds reject the probe with it,
                // and its absence doesn't change the capability answer meaningfully.
                const probe = VideoDecoder.isConfigSupported({{
                    codec: 'hvc1.1.6.L93.B0',
                    hardwareAcceleration: 'prefer-hardware'
                }});
                const support = await Promise.race([
                    probe,
                    new Promise((_, rej) => setTimeout(() => rej(new Error('probe timeout')), 2000))
                ]);
                hevcSupported = !!(support && support.supported);
            }} catch (e) {{
                hevcSupported = false; // timeout/throw -> treat as unsupported, stream JPEG
            }}
            hevcSupportKnown = true;
            console.log('[WebCodecs] HEVC hardware decode ' + (hevcSupported ? 'available' : 'NOT available') + ' on this device');
            return hevcSupported;
        }}

        // Kept for compatibility with earlier call sites
        async function detectHevcSupport() {{ return detectHevcSupportSafe(); }}

        // Default codec per device capability; called after the socket opens
        async function pickDefaultCodec() {{
            const canHevc = await detectHevcSupport();
            const want = canHevc ? 'hevc' : 'intra';
            if (codecSelect) codecSelect.value = want;
            return want;
        }}

        function toggleSettingsModal() {{
            const modal = document.getElementById('settings-modal');
            modal.classList.toggle('active');
        }}

        function openSettingsModal() {{
            document.getElementById('settings-modal').classList.add('active');
        }}

        function closeSettingsModal() {{
            document.getElementById('settings-modal').classList.remove('active');
        }}

        function onModalBackdropClick(e) {{
            if (e.target && e.target.id === 'settings-modal') {{
                closeSettingsModal();
            }}
        }}

        function recordFps() {{
            frameCount++;
            const now = performance.now();
            if (now - lastFpsUpdate >= 1000) {{
                fpsLabel.innerText = Math.round((frameCount * 1000) / (now - lastFpsUpdate)) + ' FPS';
                frameCount = 0;
                lastFpsUpdate = now;
            }}
        }}

        let pendingHvcC = null;
        let pendingCodec = 'hvc1.1.6.L93.B0';

        function initHevcDecoder() {{
            if (!window.VideoDecoder) return false;
            try {{
                if (videoDecoder && videoDecoder.state !== 'closed') {{
                    videoDecoder.close();
                }}

                videoDecoder = new VideoDecoder({{
                    output: (videoFrame) => {{
                        if (canvas.width !== videoFrame.displayWidth || canvas.height !== videoFrame.displayHeight) {{
                            canvas.width = videoFrame.displayWidth;
                            canvas.height = videoFrame.displayHeight;
                        }}
                        ctx.drawImage(videoFrame, 0, 0);
                        videoFrame.close();
                        recordFps();
                    }},
                    error: (e) => {{
                        console.warn('[WebCodecs HEVC Fallback]', e);
                        // Report the failure reason to the server for diagnostics
                        if (ws && ws.readyState === WebSocket.OPEN) {{
                            const why = (e && (e.message || e.toString())) || 'unknown';
                            ws.send('decerr:' + why);
                        }}
                        changeCodec('intra');
                    }}
                }});

                const config = {{
                    codec: pendingCodec,
                    hardwareAcceleration: 'prefer-hardware',
                    optimizeForLatency: true
                }};
                if (pendingHvcC) config.description = pendingHvcC;

                videoDecoder.configure(config);

                hevcReady = true;
                console.log('[WebCodecs] Hardware HEVC VideoDecoder initialized', pendingHvcC ? 'with hvcC description' : 'Annex-B mode');
                return true;
            }} catch (err) {{
                console.warn('[WebCodecs] HEVC not supported:', err);
                return false;
            }}
        }}

        function changeCodec(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('codec:' + val);
            }}
            if (val === 'hevc') initHevcDecoder();
        }}

        function getAutoDetectedResolution() {{
            const dpr = window.devicePixelRatio || 2;
            const isLandscape = window.innerWidth >= window.innerHeight;
            const sW = isLandscape ? Math.max(window.screen.width, window.screen.height) : Math.min(window.screen.width, window.screen.height);
            const sH = isLandscape ? Math.min(window.screen.width, window.screen.height) : Math.max(window.screen.width, window.screen.height);

            let targetW = Math.round(sW);
            let targetH = Math.round(sH);

            if (!targetW || targetW <= 0) targetW = window.innerWidth || 1180;
            if (!targetH || targetH <= 0) targetH = window.innerHeight || 820;

            targetW = Math.floor(targetW / 2) * 2;
            targetH = Math.floor(targetH / 2) * 2;

            // Stream at the device's PHYSICAL pixel size when it's a Retina-class screen
            // (dpr >= 2) — a native 2360x1640 stream means compose is a 1:1 copy instead
            // of a per-frame bilinear downscale (10-14ms -> <1ms), and HEVC carries the
            // extra pixels easily. Non-Retina devices keep the CSS-point size.
            if (dpr >= 2) {{
                targetW = Math.round(targetW * dpr);
                targetH = Math.round(targetH * dpr);
            }}

            return {{ width: targetW, height: targetH, dpr: dpr, physicalW: targetW, physicalH: targetH }};
        }}

        function getOptimalResolution() {{
            const selectedVal = resSelect ? resSelect.value : 'auto';
            if (selectedVal && selectedVal !== 'auto') {{
                const [pw, ph] = selectedVal.split('x').map(Number);
                return {{ width: pw, height: ph }};
            }}
            return getAutoDetectedResolution();
        }}

        function changeCursorMode(val) {{
            if (val === 'client') {{
                canvas.style.setProperty('cursor', 'default', 'important');
                if (ws && ws.readyState === WebSocket.OPEN) ws.send('cursor:client');
            }} else if (val === 'touch') {{
                canvas.style.setProperty('cursor', 'none', 'important');
                if (ws && ws.readyState === WebSocket.OPEN) ws.send('cursor:touch');
            }} else {{ // 'host'
                canvas.style.setProperty('cursor', 'none', 'important');
                if (ws && ws.readyState === WebSocket.OPEN) ws.send('cursor:host');
            }}
        }}

        function changeQuality(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('quality:' + val);
            }}
        }}

        function changeDpi(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                console.log(`[DPI] Changing Windows Display Scale to: ${{val}}%`);
                ws.send('dpi:' + val);
            }}
        }}

        function changeZoom(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                console.log(`[Zoom] Setting UI Magnification: ${{val}}x`);
                ws.send('zoom:' + val);
            }}
        }}

        function syncResolutionNow() {{
            const opt = getOptimalResolution();
            if (ws && ws.readyState === WebSocket.OPEN) {{
                console.log(`[Sync] Adjusting to low-latency target resolution: ${{opt.width}}x${{opt.height}}`);
                ws.send(`set_res:${{opt.width}},${{opt.height}}`);
            }}
            if (codecSelect && codecSelect.value === 'hevc') {{
                initHevcDecoder();
            }}
        }}

        function changeResolutionPreset(val) {{
            syncResolutionNow();
        }}

        let wakeLock = null;
        async function requestWakeLock() {{
            try {{
                if ('wakeLock' in navigator) {{
                    wakeLock = await navigator.wakeLock.request('screen');
                    console.log('[WakeLock] Screen WakeLock active');
                }}
            }} catch (e) {{ }}
        }}

        let authenticated = true; // open servers never ask for a password
        let authChallenge = null; // current server challenge (authreq:<challenge>)

        // Pure-JS SHA-256: crypto.subtle is unavailable on http://LAN-IP (non-secure context)
        function sha256Hex(ascii) {{
            function rightRotate(v, a) {{ return (v >>> a) | (v << (32 - a)); }}
            const K = [
                0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
                0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
                0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
                0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
                0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
                0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
                0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
                0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
            ];
            const H = [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19];
            const bytes = [];
            for (let i = 0; i < ascii.length; i++) {{
                let c = ascii.charCodeAt(i);
                if (c < 128) bytes.push(c);
                else if (c < 2048) {{ bytes.push((c >> 6) | 192, (c & 63) | 128); }}
                else {{ bytes.push((c >> 12) | 224, ((c >> 6) & 63) | 128, (c & 63) | 128); }}
            }}
            const bitLen = bytes.length * 8;
            bytes.push(0x80);
            while (bytes.length % 64 !== 56) bytes.push(0);
            for (let i = 7; i >= 0; i--) bytes.push((bitLen / Math.pow(2, i * 8)) & 0xFF);
            for (let block = 0; block < bytes.length / 64; block++) {{
                const w = new Array(64);
                for (let t = 0; t < 16; t++) {{
                    const o = block * 64 + t * 4;
                    w[t] = (bytes[o] << 24) | (bytes[o + 1] << 16) | (bytes[o + 2] << 8) | bytes[o + 3];
                }}
                for (let t = 16; t < 64; t++) {{
                    const s0 = rightRotate(w[t - 15], 7) ^ rightRotate(w[t - 15], 18) ^ (w[t - 15] >>> 3);
                    const s1 = rightRotate(w[t - 2], 17) ^ rightRotate(w[t - 2], 19) ^ (w[t - 2] >>> 10);
                    w[t] = (w[t - 16] + s0 + w[t - 7] + s1) | 0;
                }}
                let a = H[0], b = H[1], c = H[2], d = H[3], e = H[4], f = H[5], g = H[6], h = H[7];
                for (let t = 0; t < 64; t++) {{
                    const S1 = rightRotate(e, 6) ^ rightRotate(e, 11) ^ rightRotate(e, 25);
                    const ch = (e & f) ^ (~e & g);
                    const temp1 = (h + S1 + ch + K[t] + w[t]) | 0;
                    const S0 = rightRotate(a, 2) ^ rightRotate(a, 13) ^ rightRotate(a, 22);
                    const maj = (a & b) ^ (a & c) ^ (b & c);
                    const temp2 = (S0 + maj) | 0;
                    h = g; g = f; f = e; e = (d + temp1) | 0;
                    d = c; c = b; b = a; a = (temp1 + temp2) | 0;
                }}
                H[0] = (H[0] + a) | 0; H[1] = (H[1] + b) | 0; H[2] = (H[2] + c) | 0; H[3] = (H[3] + d) | 0;
                H[4] = (H[4] + e) | 0; H[5] = (H[5] + f) | 0; H[6] = (H[6] + g) | 0; H[7] = (H[7] + h) | 0;
            }}
            return H.map(x => ('00000000' + ((x >>> 0).toString(16))).slice(-8)).join('');
        }}

        function syncSessionSettings() {{
            syncResolutionNow();
            const c = (codecSelect ? codecSelect.value : 'hevc');
            changeCodec(c); // explicit codec message every (re)connect — server always starts on a known path
            const z = document.getElementById('zoom-select') ? document.getElementById('zoom-select').value : '1.5';
            changeZoom(z);
            const sel = document.getElementById('display-select').value;
            if (ws && ws.readyState === WebSocket.OPEN) ws.send('display:' + sel);
        }}

        function showAuthPrompt(err) {{
            const overlay = document.getElementById('auth-overlay');
            if (!overlay) return;
            authenticated = false;
            overlay.classList.add('active');
            const errEl = document.getElementById('auth-error');
            if (errEl) errEl.innerText = err || '';
            const input = document.getElementById('auth-input');
            if (input) {{ input.value = ''; setTimeout(() => input.focus(), 150); }}
        }}

        function hideAuthPrompt() {{
            const overlay = document.getElementById('auth-overlay');
            if (overlay) overlay.classList.remove('active');
        }}

        function submitAuth() {{
            const input = document.getElementById('auth-input');
            const pw = input ? input.value : '';
            if (!pw || !ws || ws.readyState !== WebSocket.OPEN) return;

            if (authChallenge) {{
                // Challenge-response: send SHA-256(password + challenge), never the password
                const resp = sha256Hex(pw + authChallenge);
                ws.send('auth:' + resp);
            }} else {{
                ws.send('auth:' + pw); // legacy servers without a challenge
            }}
        }}

        function onAuthBackdropClick(e) {{
            if (e.target && e.target.id === 'auth-overlay') submitAuth();
        }}

        const authInput = document.getElementById('auth-input');
        if (authInput) {{
            authInput.addEventListener('keydown', (e) => {{
                e.stopPropagation();
                if (e.key === 'Enter') submitAuth();
            }});
        }}

        function connectWs() {{
            const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${{protocol}}//${{location.host}}/`);
            ws.binaryType = 'arraybuffer';

            ws.onopen = async () => {{
                console.log('[OpenWinSidecar] Stream Connected');
                requestWakeLock();
                syncResolutionNow();

                // ALWAYS send an explicit codec choice so the server starts streaming on a
                // known path. The probe refines it when it resolves (fail-safe: a hung or
                // throwing isConfigSupported on some Safari versions can never stall the
                // stream — JPEG flows immediately, HEVC upgrades it when confirmed).
                const z = document.getElementById('zoom-select') ? document.getElementById('zoom-select').value : '1.5';
                changeZoom(z);

                const sel = document.getElementById('display-select').value;
                ws.send('display:' + sel);

                let c = (codecSelect ? codecSelect.value : 'hevc');
                changeCodec(c); // start streaming NOW with the current selection (default: hevc -> server encodes; probe may switch to intra)

                if (c === 'hevc') {{
                    const canHevc = await detectHevcSupportSafe();
                    if (!canHevc) {{
                        codecSelect.value = 'intra';
                        changeCodec('intra');
                    }}
                }}
            }};

            ws.onmessage = async (e) => {{
                if (typeof e.data === 'string') {{
                    // Server-side control messages
                    if (e.data.startsWith('desc:')) {{
                        // hvcC description + codec string for Safari's WebCodecs HEVC decode
                        const spec = e.data.substring(5);
                        const sep = spec.indexOf('|');
                        if (sep > 0) {{
                            pendingCodec = spec.substring(0, sep);
                            try {{
                                const bin = atob(spec.substring(sep + 1));
                                const bytes = new Uint8Array(bin.length);
                                for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
                                pendingHvcC = bytes.buffer;
                                console.log('[WebCodecs] hvcC description received:', pendingCodec, bytes.length, 'bytes');
                            }} catch (err) {{
                                console.warn('[WebCodecs] bad hvcC payload', err);
                                pendingHvcC = null;
                            }}
                            if (codecSelect && codecSelect.value === 'hevc') initHevcDecoder();
                        }}
                        return;
                    }}
                    if (e.data === 'auth:required') {{
                        // Challenge arrives separately via authreq:<challenge>
                        showAuthPrompt('');
                        return;
                    }}
                    if (e.data.startsWith('authreq:')) {{
                        authChallenge = e.data.substring('authreq:'.length);
                        showAuthPrompt('');
                        return;
                    }}
                    if (e.data === 'auth:ok') {{
                        hideAuthPrompt();
                        syncSessionSettings();
                        return;
                    }}
                    if (e.data === 'auth:denied') {{
                        showAuthPrompt('Wrong password — try again.');
                        return;
                    }}
                    // Server-side codec fallback notification (e.g. QSV encoder unavailable)
                    if (e.data === 'codec:intra') {{
                        if (codecSelect) codecSelect.value = 'intra';
                    }}
                    return;
                }}
                if (e.data instanceof ArrayBuffer) {{
                    const headerView = new DataView(e.data, 0, 15);
                    const codecType = headerView.getUint8(0);
                    const flags = headerView.getUint8(1);
                    const isKeyframe = (flags & 0x01) !== 0;
                    const timestampUs = headerView.getBigInt64(2);

                    const payloadBytes = new Uint8Array(e.data, 15);

                    if (pointerDot) pointerDot.style.display = 'none';

                    // 1. Hardware HEVC WebCodecs decoding path
                    if (codecType === 2 && videoDecoder && videoDecoder.state === 'configured') {{
                        try {{
                            const chunk = new EncodedVideoChunk({{
                                type: isKeyframe ? 'key' : 'delta',
                                timestamp: Number(timestampUs),
                                data: payloadBytes
                            }});
                            videoDecoder.decode(chunk);
                            return;
                        }} catch (err) {{
                            console.warn('[HEVC Chunk Decode Error]', err);
                        }}
                    }}

                    // 2. Intra JPEG Fallback path
                    try {{
                        const blob = new Blob([payloadBytes], {{ type: 'image/jpeg' }});
                        let rendered = false;

                        if (window.createImageBitmap) {{
                            try {{
                                const bmp = await createImageBitmap(blob);
                                if (canvas.width !== bmp.width || canvas.height !== bmp.height) {{
                                    canvas.width = bmp.width;
                                    canvas.height = bmp.height;
                                }}
                                ctx.drawImage(bmp, 0, 0);
                                if (bmp.close) bmp.close();
                                rendered = true;
                                recordFps();
                            }} catch (e) {{ }}
                        }}

                        if (!rendered) {{
                            const url = URL.createObjectURL(blob);
                            const img = new Image();
                            img.onload = () => {{
                                if (canvas.width !== img.naturalWidth || canvas.height !== img.naturalHeight) {{
                                    canvas.width = img.naturalWidth;
                                    canvas.height = img.naturalHeight;
                                }}
                                ctx.drawImage(img, 0, 0);
                                URL.revokeObjectURL(url);
                                recordFps();
                            }};
                            img.onerror = () => {{
                                URL.revokeObjectURL(url);
                            }};
                            img.src = url;
                        }}
                    }} catch (err) {{
                        console.error('[Render Error]', err);
                    }}
                }}
            }};

            ws.onclose = () => {{
                const fps = document.getElementById('fps');
                if (fps) fps.textContent = '⟳ reconnecting';
                setTimeout(connectWs, 1000);
            }};
        }}

        function changeDisplay(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('display:' + val);
            }}
        }}

        function sendInput(action, e) {{
            const rect = canvas.getBoundingClientRect();
            let clientX = e.clientX;
            let clientY = e.clientY;

            if (e.touches && e.touches.length > 0) {{
                clientX = e.touches[0].clientX;
                clientY = e.touches[0].clientY;
            }}

            if (clientX === undefined || clientY === undefined) return;

            const normX = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
            const normY = Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));

            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send(`input:${{action}},${{normX.toFixed(4)}},${{normY.toFixed(4)}}`);
            }}
        }}

        // --- Single-finger touch / mouse (rAF Batched for 0 input lag) ---
        let isDown = false;
        let pendingMoveEvent = null;
        let moveRafScheduled = false;

        function flushPendingMove() {{
            moveRafScheduled = false;
            if (pendingMoveEvent && isDown) {{
                sendInput('move', pendingMoveEvent);
                pendingMoveEvent = null;
            }}
        }}

        container.addEventListener('pointerdown', (e) => {{
            isDown = true;
            pendingMoveEvent = null;
            sendInput('down', e);
        }}, {{ passive: true }});

        container.addEventListener('pointermove', (e) => {{
            if (isDown) {{
                pendingMoveEvent = e;
                if (!moveRafScheduled) {{
                    moveRafScheduled = true;
                    requestAnimationFrame(flushPendingMove);
                }}
            }}
        }}, {{ passive: true }});

        container.addEventListener('pointerup', (e) => {{
            isDown = false;
            pendingMoveEvent = null;
            sendInput('up', e);
        }}, {{ passive: true }});

        container.addEventListener('pointercancel', (e) => {{
            isDown = false;
            pendingMoveEvent = null;
            sendInput('up', e);
        }}, {{ passive: true }});

        // --- Two-finger scroll ---
        let lastTouchY = 0;
        let lastTouchCount = 0;
        let twoFingerStart = 0;
        container.addEventListener('touchstart', (e) => {{
            lastTouchCount = e.touches.length;
            if (e.touches.length === 2) {{
                lastTouchY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                twoFingerStart = performance.now();
                e.preventDefault();
            }}
        }}, {{ passive: false }});
        container.addEventListener('touchmove', (e) => {{
            if (e.touches.length === 2 && ws && ws.readyState === WebSocket.OPEN) {{
                const avgY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
                const delta = Math.round((lastTouchY - avgY) * 3);
                if (Math.abs(delta) > 2) {{
                    ws.send('scroll:' + delta);
                    lastTouchY = avgY;
                }}
                e.preventDefault();
            }}
        }}, {{ passive: false }});
        container.addEventListener('touchend', (e) => {{
            if (lastTouchCount === 2 && e.touches.length === 0) {{
                const elapsed = performance.now() - twoFingerStart;
                if (elapsed < 300 && ws && ws.readyState === WebSocket.OPEN) {{
                    ws.send('rightclick');
                }}
            }}
            lastTouchCount = e.touches.length;
        }}, {{ passive: true }});

        // --- Mouse wheel (desktop browser) ---
        container.addEventListener('wheel', (e) => {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('scroll:' + Math.round(-e.deltaY));
            }}
            e.preventDefault();
        }}, {{ passive: false }});

        // --- Keyboard forwarding ---
        // Physical-key (e.code) → Windows VK mapping: stable across active input layouts
        // and immune to the IME keyCode-229 problem. Falls back to e.keyCode for codes
        // not in the table (or legacy browsers without e.code).
        const CODE_TO_VK = {{
            Escape:27, Digit1:49, Digit2:50, Digit3:51, Digit4:52, Digit5:53, Digit6:54, Digit7:55, Digit8:56, Digit9:57, Digit0:48,
            Minus:189, Equal:187, Backspace:8, Tab:9,
            KeyQ:81, KeyW:87, KeyE:69, KeyR:82, KeyT:84, KeyY:89, KeyU:85, KeyI:73, KeyO:79, KeyP:80,
            BracketLeft:219, BracketRight:221, Backslash:220, CapsLock:20,
            KeyA:65, KeyS:83, KeyD:68, KeyF:70, KeyG:71, KeyH:72, KeyJ:74, KeyK:75, KeyL:76,
            Semicolon:186, Quote:222, Enter:13,
            KeyZ:90, KeyX:88, KeyC:67, KeyV:86, KeyB:66, KeyN:78, KeyM:77,
            Comma:188, Period:190, Slash:191, Space:32,
            Insert:45, Delete:46, Home:36, End:35, PageUp:33, PageDown:34,
            ArrowLeft:37, ArrowUp:38, ArrowRight:39, ArrowDown:40,
            Backquote:192,
            F1:112, F2:113, F3:114, F4:115, F5:116, F6:117, F7:118, F8:119, F9:120, F10:121, F11:122, F12:123,
            ShiftLeft:160, ShiftRight:161, ControlLeft:162, ControlRight:163, AltLeft:164, AltRight:165,
            Numpad0:96, Numpad1:97, Numpad2:98, Numpad3:99, Numpad4:100, Numpad5:101, Numpad6:102, Numpad7:103, Numpad8:104, Numpad9:105,
            NumpadMultiply:106, NumpadAdd:107, NumpadSubtract:109, NumpadDecimal:110, NumpadDivide:111
        }};
        function eventToVk(e) {{
            if (e.code && CODE_TO_VK[e.code] !== undefined) return CODE_TO_VK[e.code];
            return e.keyCode; // legacy fallback
        }}

        document.addEventListener('keydown', (e) => {{
            if (!authenticated) return; // don't forward keystrokes while the password prompt is up
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('key:down,' + eventToVk(e));
                if (!e.metaKey && !e.ctrlKey) e.preventDefault();
            }}
        }});
        document.addEventListener('keyup', (e) => {{
            if (!authenticated) return;
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('key:up,' + eventToVk(e));
                e.preventDefault();
            }}
        }});

        // --- Mobile Virtual Keyboard integration ---
        const hiddenKbd = document.getElementById('hidden-kbd-input');
        function toggleVirtualKeyboard() {{
            if (hiddenKbd) {{
                hiddenKbd.focus();
                hiddenKbd.click();
            }}
        }}

        if (hiddenKbd) {{
            hiddenKbd.addEventListener('input', (e) => {{
                if (e.data && ws && ws.readyState === WebSocket.OPEN) {{
                    ws.send('key:text,' + e.data);
                }}
                hiddenKbd.value = '';
            }});
            hiddenKbd.addEventListener('keydown', (e) => {{
                if (e.key === 'Backspace' && ws && ws.readyState === WebSocket.OPEN) {{
                    ws.send('key:down,8');
                    setTimeout(() => ws.send('key:up,8'), 30);
                }} else if (e.key === 'Enter' && ws && ws.readyState === WebSocket.OPEN) {{
                    ws.send('key:down,13');
                    setTimeout(() => ws.send('key:up,13'), 30);
                }}
            }});
        }}

        // --- Fit / Fill Stretch Mode Toggle ---
        let isFillMode = false;
        function toggleFitMode() {{
            isFillMode = !isFillMode;
            const fitBtn = document.getElementById('fit-btn');
            if (isFillMode) {{
                canvas.style.objectFit = 'fill';
                if (fitBtn) fitBtn.innerText = '📺 Stretch (100%)';
            }} else {{
                canvas.style.objectFit = 'contain';
                if (fitBtn) fitBtn.innerText = '📐 Fit (Aspect)';
            }}
        }}

        function toggleFullscreen() {{
            if (!document.fullscreenElement) {{
                document.documentElement.requestFullscreen().then(() => {{
                    setTimeout(syncResolutionNow, 150);
                }}).catch(() => {{
                    syncResolutionNow();
                }});
            }} else {{
                document.exitFullscreen().then(() => {{
                    setTimeout(syncResolutionNow, 150);
                }}).catch(() => {{}});
            }}
        }}

        // Auto-re-sync when entering/exiting fullscreen, rotating, or returning to tab
        document.addEventListener('fullscreenchange', () => setTimeout(syncResolutionNow, 200));
        window.addEventListener('resize', () => setTimeout(syncResolutionNow, 200));
        window.addEventListener('orientationchange', () => setTimeout(syncResolutionNow, 300));
        document.addEventListener('visibilitychange', () => {{
            if (document.visibilityState === 'visible') setTimeout(syncResolutionNow, 100);
            // Returning from a backgrounded tab: Safari may have evicted decoder state, so
            // ask the server to restart the encoder — the next frame is a fresh IDR and the
            // delta chain can never reference frames the decoder no longer has.
            if (document.visibilityState === 'visible' && authenticated && ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('forceidr');
            }}
        }});

        connectWs();
    </script>
</body>
</html>";

        var htmlBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
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

            _inputDispatcher.MoveMouseToScreen(display, x, y);

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

    public void Dispose()
    {
        Stop();
        _hub.Dispose();
    }
}
