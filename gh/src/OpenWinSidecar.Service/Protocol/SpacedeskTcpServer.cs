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
    private readonly int _port;
    private readonly DxgiCaptureService _dxgiCapture = new();
    private readonly ScreenCaptureService _gdiCapture = new();
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

            // Native Binary fallback
            var response = Encoding.ASCII.GetBytes("SPACEDESK_OK\n");
            await stream.WriteAsync(response, token);

            var frameHeader = new byte[8];
            while (!token.IsCancellationRequested && client.Connected)
            {
                var (frameBytes, _, _, _) = _gdiCapture.CaptureFrameWithCursor(-1, 0, 0, 65);
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

        VirtualDisplayManager.EnableExtendMode();

        var screens = Screen.AllScreens;
        int selectedDisplay = screens.Length > 2 ? 2 : (screens.Length > 1 ? 0 : 0);
        int reqWidth = 1180;
        int reqHeight = 820;
        long reqQuality = 80;
        StreamCodec activeCodec = StreamCodec.IntraTurbo;
        bool useDxgi = true; // Try DXGI first, fall back to GDI
        bool dxgiLogged = false;
        double reqZoom = 1.0;

        bool showHostCursor = true;

        _ = Task.Run(async () =>
        {
            var wsBuffer = new byte[4096];
            while (!token.IsCancellationRequested && client.Connected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(wsBuffer, token);
                    if (bytesRead <= 0) break;

                    var text = ParseWsTextMessage(wsBuffer, bytesRead);
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (text.StartsWith("codec:"))
                        {
                            var cStr = text.Substring(6).ToLowerInvariant();
                            activeCodec = cStr switch
                            {
                                "hevc" or "h265" => StreamCodec.HEVC,
                                "av1" => StreamCodec.AV1,
                                "h264" => StreamCodec.H264,
                                "intra" => StreamCodec.IntraTurbo,
                                _ => StreamCodec.IntraTurbo
                            };
                        }
                        else if (text.StartsWith("cursor:"))
                        {
                            var cMode = text.Substring(7).ToLowerInvariant();
                            showHostCursor = (cMode == "host" || cMode == "show_host");
                        }
                        else if (text.StartsWith("display:"))
                        {
                            if (int.TryParse(text.Substring(8), out var d)) selectedDisplay = d;
                        }
                        else if (text.StartsWith("quality:"))
                        {
                            if (long.TryParse(text.Substring(8), out var q)) reqQuality = Math.Clamp(q, 10, 95);
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
                                reqWidth = w;
                                reqHeight = h;
                                Console.WriteLine($"[Resolution] Target resolution set: {w}x{h}");
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
                                reqZoom = Math.Clamp(z, 1.0, 3.0);
                                Console.WriteLine($"[Magnification] UI Zoom set to: {reqZoom:F2}x");
                            }
                        }
                        else if (text.StartsWith("input:"))
                        {
                            var parts = text.Substring(6).Split(',');
                            if (parts.Length >= 3 && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var px) && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var py))
                            {
                                var action = parts[0];
                                _inputDispatcher.MoveMouseToScreen(selectedDisplay, px, py, reqZoom);
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
                            await Task.Delay(30, token);
                            _inputDispatcher.MouseClick(false, false);
                        }
                    }
                }
                catch { break; }
            }
        }, token);

        long frameIndex = 0;
        var sw = Stopwatch.StartNew();
        int dxgiFailCount = 0;
        using var streamLock = new SemaphoreSlim(1, 1);
        HevcQsvStreamEncoder? hevcEncoder = null;
        int lastEncoderW = 0;
        int lastEncoderH = 0;
        int lastEncoderBitrate = 0;

        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                var captureStart = sw.ElapsedMilliseconds;

                if (activeCodec == StreamCodec.HEVC)
                {
                    int targetBitrate = (int)(reqQuality switch
                    {
                        <= 50 => 3000,
                        <= 65 => 5000,
                        <= 80 => 8000,
                        _ => 12000
                    });

                    if (hevcEncoder == null || lastEncoderW != reqWidth || lastEncoderH != reqHeight || lastEncoderBitrate != targetBitrate)
                    {
                        hevcEncoder?.Shutdown();
                        hevcEncoder = new HevcQsvStreamEncoder(async (nalBytes, isKey) =>
                        {
                            try
                            {
                                frameIndex++;
                                await SendWebCodecsPacketAsync(stream, streamLock, (byte)StreamCodec.HEVC, isKey, frameIndex * 16666, 0, 0, false, nalBytes, token);
                            }
                            catch { }
                        });

                        if (hevcEncoder.Initialize(reqWidth, reqHeight, targetBitrate))
                        {
                            lastEncoderW = reqWidth;
                            lastEncoderH = reqHeight;
                            lastEncoderBitrate = targetBitrate;
                        }
                        else
                        {
                            hevcEncoder = null;
                            activeCodec = StreamCodec.IntraTurbo;
                        }
                    }

                    if (hevcEncoder != null)
                    {
                        byte[]? rawBytes = null;
                        int curX = 0, curY = 0;
                        bool curVis = false;

                        var currentScreens = Screen.AllScreens;
                        Screen? targetScreen = (selectedDisplay >= 0 && selectedDisplay < currentScreens.Length) ? currentScreens[selectedDisplay] : currentScreens[^1];

                        if (useDxgi && targetScreen != null)
                        {
                            (rawBytes, curX, curY, curVis) = _dxgiCapture.CaptureRawBgraFrame(
                                targetScreen.DeviceName,
                                targetScreen.Bounds.X, targetScreen.Bounds.Y,
                                targetScreen.Bounds.Width, targetScreen.Bounds.Height,
                                reqWidth, reqHeight, reqZoom);
                        }

                        if (rawBytes == null || rawBytes.Length == 0)
                        {
                            (rawBytes, curX, curY, curVis) = _gdiCapture.CaptureRawBgraFrame(selectedDisplay, reqWidth, reqHeight, reqZoom);
                        }

                        if (rawBytes != null && rawBytes.Length > 0)
                        {
                            hevcEncoder.PushRawFrame(rawBytes);
                        }
                    }
                }
                else
                {
                    byte[]? frame = null;
                    int curX = 0, curY = 0;
                    bool curVis = false;

                    var currentScreens = Screen.AllScreens;
                    Screen? targetScreen = null;
                    if (selectedDisplay >= 0 && selectedDisplay < currentScreens.Length)
                        targetScreen = currentScreens[selectedDisplay];
                    else if (currentScreens.Length > 2)
                        targetScreen = currentScreens[2];
                    else
                        targetScreen = currentScreens[^1];

                    // Try DXGI first
                    if (useDxgi)
                    {
                        (frame, curX, curY, curVis) = _dxgiCapture.CaptureFrame(
                            targetScreen.DeviceName,
                            targetScreen.Bounds.X, targetScreen.Bounds.Y,
                            targetScreen.Bounds.Width, targetScreen.Bounds.Height,
                            reqWidth, reqHeight, reqQuality, showHostCursor);

                        if (frame == null)
                        {
                            dxgiFailCount++;
                            if (dxgiFailCount > 5)
                            {
                                if (!dxgiLogged) { Console.WriteLine("[Capture] DXGI unavailable — using GDI capture"); dxgiLogged = true; }
                                useDxgi = false;
                            }
                        }
                        else
                        {
                            dxgiFailCount = 0;
                        }
                    }

                    // GDI fallback
                    if (frame == null)
                    {
                        (frame, curX, curY, curVis) = _gdiCapture.CaptureFrameWithCursor(selectedDisplay, reqWidth, reqHeight, reqQuality, reqZoom, showHostCursor);
                    }

                    if (frame != null && frame.Length > 0)
                    {
                        frameIndex++;
                        await SendWebCodecsPacketAsync(stream, streamLock, (byte)activeCodec, true, frameIndex * 16666, (short)curX, (short)curY, curVis, frame, token);
                    }
                }

                var elapsed = sw.ElapsedMilliseconds - captureStart;
                int targetDelay = (int)Math.Max(1, 16 - elapsed);
                await Task.Delay(targetDelay, token);
            }
        }
        finally
        {
            hevcEncoder?.Shutdown();
        }
    }

    private static async Task SendWebCodecsPacketAsync(NetworkStream stream, SemaphoreSlim streamLock, byte codecType, bool isKeyframe, long timestampUs, short curX, short curY, bool curVisible, byte[] payload, CancellationToken token)
    {
        var header = new byte[15 + payload.Length];
        header[0] = codecType;
        header[1] = (byte)((isKeyframe ? 0x01 : 0) | (curVisible ? 0x02 : 0));
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(2, 8), timestampUs);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(10, 2), curX);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(12, 2), curY);
        payload.CopyTo(header, 15);

        await streamLock.WaitAsync(token);
        try
        {
            await SendWsBinaryFrameAsync(stream, header, token);
        }
        finally
        {
            streamLock.Release();
        }
    }

    private static string? ParseWsTextMessage(byte[] buffer, int length)
    {
        if (length < 6) return null;
        bool masked = (buffer[1] & 0x80) != 0;
        int payloadLen = buffer[1] & 0x7F;
        int offset = 2;

        if (payloadLen == 126) offset = 4;
        else if (payloadLen == 127) offset = 10;

        if (!masked) return Encoding.UTF8.GetString(buffer, offset, length - offset);

        var mask = buffer.AsSpan(offset, 4);
        offset += 4;
        int dataLen = length - offset;
        var decoded = new byte[dataLen];

        for (int i = 0; i < dataLen; i++)
        {
            decoded[i] = (byte)(buffer[offset + i] ^ mask[i % 4]);
        }

        return Encoding.UTF8.GetString(decoded);
    }

    private static async Task SendWsBinaryFrameAsync(NetworkStream stream, byte[] payload, CancellationToken token)
    {
        byte[] header;
        if (payload.Length <= 125)
        {
            header = new byte[] { 0x82, (byte)payload.Length };
        }
        else if (payload.Length <= 65535)
        {
            header = new byte[] { 0x82, 126, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF) };
        }
        else
        {
            header = new byte[10];
            header[0] = 0x82;
            header[1] = 127;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(2, 8), payload.Length);
        }

        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
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
        .dot {{ width: 8px; height: 8px; background: #10B981; border-radius: 50%; display: inline-block; animation: pulse 2s infinite; }}
        .codec-tag {{ background: #10B981; color: #fff; font-size: 10px; font-weight: 800; padding: 2px 6px; border-radius: 4px; letter-spacing: 0.5px; }}
        
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
    </style>
</head>
<body>
    <div id='container'>
        <div id='top-pill' onclick='toggleSettingsModal()'>
            <span class='dot'></span>
            <span id='fps'>60 FPS</span>
            <span id='codec-badge' class='codec-tag'>HEVC GPU</span>
            <span id='res-label'>1180x820</span>
            <span style='font-size:13px;opacity:0.85;margin-left:2px;'>⚙️</span>
        </div>

        <div id='settings-modal' onclick='onModalBackdropClick(event)'>
            <div class='modal-card' onclick='event.stopPropagation()'>
                <div class='modal-header'>
                    <div style='display:flex;align-items:center;gap:8px;'>
                        <span style='font-size:18px;'>⚙️</span>
                        <span style='font-size:16px;font-weight:700;'>OpenWinSidecar Settings</span>
                    </div>
                    <button class='modal-close' onclick='closeSettingsModal()'>✕</button>
                </div>

                <div class='modal-body'>
                    <div class='setting-group'>
                        <label>🖥️ Target Display</label>
                        <select id='display-select' onchange='changeDisplay(this.value)'>
                            {displayOptions}
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>🚀 Video Codec</label>
                        <select id='codec-select' onchange='changeCodec(this.value)'>
                            <option value='hevc' selected>🚀 HEVC / H.265 (Intel Arc GPU Accelerated)</option>
                            <option value='intra'>🖼️ Intra JPEG (Universal Fallback)</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>📱 Screen & Device Resolution</label>
                        <select id='res-select' onchange='changeResolutionPreset(this.value)'>
                            <option value='auto' selected>✨ Auto-Detect My Device Screen (Recommended)</option>
                            <optgroup label='📱 iPad 10.9-inch / 11-inch Air / 10th-11th Gen (59:41)'>
                                <option value='1180x820'>1180 x 820 — @2x Logical (Low Latency / Crisp UI)</option>
                                <option value='2360x1640'>2360 x 1640 — Native 2K Retina</option>
                            </optgroup>
                            <optgroup label='🚀 iPad Pro 11-inch (M4)'>
                                <option value='1210x834'>1210 x 834 — @2x Logical</option>
                                <option value='2420x1668'>2420 x 1668 — Native Retina</option>
                            </optgroup>
                            <optgroup label='🚀 iPad Pro 11-inch (1st–4th Gen)'>
                                <option value='1194x834'>1194 x 834 — @2x Logical</option>
                                <option value='2388x1668'>2388 x 1668 — Native Retina</option>
                            </optgroup>
                            <optgroup label='👑 iPad Pro 13-inch (M4)'>
                                <option value='1376x1032'>1376 x 1032 — @2x Logical</option>
                                <option value='2752x2064'>2752 x 2064 — Native 3K Retina</option>
                            </optgroup>
                            <optgroup label='👑 iPad Pro 12.9-inch / Air 13-inch (4:3)'>
                                <option value='1366x1024'>1366 x 1024 — @2x Logical</option>
                                <option value='2732x2048'>2732 x 2048 — Native 3K Retina</option>
                            </optgroup>
                            <optgroup label='📱 iPad 10.2-inch (7th–9th Gen) (4:3)'>
                                <option value='1080x810'>1080 x 810 — @2x Logical</option>
                                <option value='2160x1620'>2160 x 1620 — Native Retina</option>
                            </optgroup>
                            <optgroup label='📱 iPad 9.7-inch & iPad mini Retina (4:3)'>
                                <option value='1024x768'>1024 x 768 — @2x Logical</option>
                                <option value='2048x1536'>2048 x 1536 — Native Retina</option>
                            </optgroup>
                            <optgroup label='📱 iPad mini 8.3-inch (6th Gen & A17 Pro)'>
                                <option value='1133x744'>1133 x 744 — @2x Logical</option>
                                <option value='2266x1488'>2266 x 1488 — Native Retina</option>
                            </optgroup>
                            <optgroup label='💻 PC Standard (16:9 / 16:10)'>
                                <option value='1920x1080'>1920 x 1080 — 1080p Full HD</option>
                                <option value='2560x1440'>2560 x 1440 — 1440p QHD</option>
                            </optgroup>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>🖥️ Windows Display Scale (Text & Icons Size)</label>
                        <select id='dpi-select' onchange='changeDpi(this.value)'>
                            <option value='100'>100% (Native / Smallest)</option>
                            <option value='125'>125% (Comfortable)</option>
                            <option value='150'>150% (Large Text & UI)</option>
                            <option value='175' selected>175% (Recommended for iPad)</option>
                            <option value='200'>200% (Extra Large / Touch Friendly)</option>
                            <option value='225'>225% (Huge UI)</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>🔍 UI Magnification (Zoom Viewport)</label>
                        <select id='zoom-select' onchange='changeZoom(this.value)'>
                            <option value='1.0' selected>🖥️ 1.0x (100% Full Desktop)</option>
                            <option value='1.25'>📱 1.25x (125% Comfortable)</option>
                            <option value='1.5'>🔎 1.5x (150% Large UI)</option>
                            <option value='1.75'>✨ 1.75x (175% Extra Large)</option>
                            <option value='2.0'>🔍 2.0x (200% Huge Touch UI)</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>🖱️ Mouse Cursor Mode</label>
                        <select id='cursor-select' onchange='changeCursorMode(this.value)'>
                            <option value='host' selected>🖥️ Streamed Windows Cursor (Default - Zero Duplicate)</option>
                            <option value='touch'>📱 Touch Tablet (Hide All Cursors)</option>
                            <option value='client'>💻 Browser Cursor (Native OS)</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>💎 Quality Preset</label>
                        <select id='quality-select' onchange='changeQuality(this.value)'>
                            <option value='50'>⚡ 50% Quality (Fastest)</option>
                            <option value='65'>⚖️ 65% Quality (Balanced)</option>
                            <option value='80' selected>💎 80% Quality (High Detail - Default)</option>
                            <option value='90'>👑 90% Quality (Ultra Crisp)</option>
                        </select>
                    </div>

                    <div class='setting-group'>
                        <label>🛠️ Quick Actions</label>
                        <div class='btn-grid'>
                            <button class='action-btn' onclick='syncResolutionNow()'>🎯 Apply Res</button>
                            <button id='fit-btn' onclick='toggleFitMode()'>📐 Fit / Stretch</button>
                            <button id='kbd-btn' onclick='toggleVirtualKeyboard()'>⌨️ Keyboard</button>
                            <button id='fullscreen-btn' onclick='toggleFullscreen()'>⛶ Fullscreen</button>
                        </div>
                    </div>
                </div>

                <div class='modal-footer'>
                    <button class='done-btn' onclick='closeSettingsModal()'>Done</button>
                </div>
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
        const resLabel = document.getElementById('res-label');
        const resSelect = document.getElementById('res-select');
        const qualitySelect = document.getElementById('quality-select');
        const codecSelect = document.getElementById('codec-select');

        let ws;
        let frameCount = 0;
        let lastFpsUpdate = performance.now();
        let isRendering = false;
        let videoDecoder = null;
        let hevcReady = false;

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
                        changeCodec('intra');
                    }}
                }});

                videoDecoder.configure({{
                    codec: 'hvc1.1.6.L93.B0',
                    codedWidth: 1180,
                    codedHeight: 820,
                    hardwareAcceleration: 'prefer-hardware',
                    optimizeForLatency: true
                }});

                hevcReady = true;
                console.log('[WebCodecs] Hardware HEVC VideoDecoder initialized');
                return true;
            }} catch (err) {{
                console.warn('[WebCodecs] HEVC not supported:', err);
                return false;
            }}
        }}

        function changeCodec(val) {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('codec:' + val);
                const badge = document.getElementById('codec-badge');
                if (badge) badge.innerText = (val === 'hevc' ? 'HEVC GPU' : 'JPEG INTRA');
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

            return {{ width: targetW, height: targetH, dpr: dpr, physicalW: Math.round(targetW * dpr), physicalH: Math.round(targetH * dpr) }};
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
            resLabel.innerText = `${{opt.width}}x${{opt.height}}`;
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

        function connectWs() {{
            const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
            ws = new WebSocket(`${{protocol}}//${{location.host}}/`);
            ws.binaryType = 'arraybuffer';

            ws.onopen = async () => {{
                console.log('[OpenWinSidecar] Stream Connected');
                requestWakeLock();
                syncResolutionNow();
                
                const c = (codecSelect ? codecSelect.value : 'hevc');
                changeCodec(c);

                const z = document.getElementById('zoom-select') ? document.getElementById('zoom-select').value : '1.5';
                changeZoom(z);

                const sel = document.getElementById('display-select').value;
                ws.send('display:' + sel);

                const badge = document.getElementById('codec-badge');
                if (badge) {{
                    badge.innerText = (c === 'hevc' ? 'HEVC GPU' : 'JPEG INTRA');
                    badge.style.background = '#10B981';
                }}
            }};

            ws.onmessage = async (e) => {{
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
                const badge = document.getElementById('codec-badge');
                if (badge) {{
                    badge.innerText = 'RECONNECTING...';
                    badge.style.background = '#EF4444';
                }}
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
        document.addEventListener('keydown', (e) => {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('key:down,' + e.keyCode);
                if (!e.metaKey && !e.ctrlKey) e.preventDefault();
            }}
        }});
        document.addEventListener('keyup', (e) => {{
            if (ws && ws.readyState === WebSocket.OPEN) {{
                ws.send('key:up,' + e.keyCode);
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
        _dxgiCapture.Dispose();
    }
}
