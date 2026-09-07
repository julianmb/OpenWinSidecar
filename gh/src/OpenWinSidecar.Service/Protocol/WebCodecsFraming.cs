using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace OpenWinSidecar.Service.Protocol;

/// <summary>
/// WebCodecs binary packet framing: 15-byte header + payload, wrapped in a WebSocket
/// binary frame. Header: [0]=codec, [1]=flags (bit0 keyframe, bit1 cursor visible),
/// [2..9]=timestamp µs (big-endian), [10..11]=cursor X, [12..13]=cursor Y, [14]=reserved.
/// </summary>
public static class WebCodecsFraming
{
    public static async Task SendPacketAsync(
        NetworkStream stream,
        SemaphoreSlim streamLock,
        byte codecType,
        bool isKeyframe,
        long timestampUs,
        short curX,
        short curY,
        bool curVisible,
        byte[] payload,
        CancellationToken token)
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

    public static async Task SendTextAsync(NetworkStream stream, SemaphoreSlim streamLock, string text, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await streamLock.WaitAsync(token);
        try
        {
            byte[] frame;
            if (payload.Length <= 125)
            {
                frame = new byte[2 + payload.Length];
                frame[0] = 0x81;
                frame[1] = (byte)payload.Length;
                payload.CopyTo(frame, 2);
            }
            else if (payload.Length <= 65535)
            {
                frame = new byte[4 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
                payload.CopyTo(frame, 4);
            }
            else
            {
                frame = new byte[10 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 127;
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(2, 8), payload.Length);
                payload.CopyTo(frame, 10);
            }

            await stream.WriteAsync(frame, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            streamLock.Release();
        }
    }

    /// <summary>
    /// WebSocket protocol-level ping (opcode 0x09, empty payload). Browsers answer these
    /// automatically, keeping NAT mappings and mobile Safari's idle timeout alive while the
    /// stream is silent (static desktop, no frames).
    /// </summary>
    public static async Task SendPingAsync(NetworkStream stream, SemaphoreSlim streamLock, CancellationToken token)
    {
        await streamLock.WaitAsync(token);
        try
        {
            await stream.WriteAsync(new byte[] { 0x89, 0x00 }, token);
            await stream.FlushAsync(token);
        }
        finally
        {
            streamLock.Release();
        }
    }

    public static async Task SendWsBinaryFrameAsync(NetworkStream stream, byte[] payload, CancellationToken token)
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
}
