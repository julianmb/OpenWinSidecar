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
    public static byte[] BuildPacket(
        byte codecType,
        bool isKeyframe,
        long timestampUs,
        short curX,
        short curY,
        bool curVisible,
        byte[] payload)
    {
        var packet = new byte[15 + payload.Length];
        packet[0] = codecType;
        packet[1] = (byte)((isKeyframe ? 0x01 : 0) | (curVisible ? 0x02 : 0));
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(2, 8), timestampUs);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(10, 2), curX);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(12, 2), curY);
        payload.CopyTo(packet, 15);
        return packet;
    }

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
        var packet = BuildPacket(codecType, isKeyframe, timestampUs, curX, curY, curVisible, payload);

        await streamLock.WaitAsync(token);
        try
        {
            await SendWsBinaryFrameAsync(stream, packet, token);
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

    /// <summary>
    /// Prefix bytes a caller must reserve at the front of a reusable buffer: 15 for the
    /// WebCodecs header plus up to 10 for the WebSocket length header. See
    /// <see cref="SendBufferedBinaryAsync"/>.
    /// </summary>
    public const int MaxFrameHeaderBytes = 25;

    /// <summary>
    /// Writes the 15-byte WebCodecs header and the minimal WebSocket frame header into the
    /// reserved space immediately in front of a payload that already lives at
    /// <c>buffer[payloadStart..]</c> and returns the start offset and total frame length to
    /// send. Pure (no I/O) so the wire layout is unit-testable.
    /// </summary>
    internal static (int wsStart, int totalLen) WriteHeaders(
        byte[] buffer,
        int payloadStart,
        int payloadLen,
        byte codecType,
        bool isKeyframe,
        long timestampUs,
        short curX,
        short curY,
        bool curVisible)
    {
        int packetStart = payloadStart - 15;
        buffer[packetStart] = codecType;
        buffer[packetStart + 1] = (byte)((isKeyframe ? 0x01 : 0) | (curVisible ? 0x02 : 0));
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(packetStart + 2, 8), timestampUs);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(packetStart + 10, 2), curX);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(packetStart + 12, 2), curY);
        buffer[packetStart + 14] = 0;

        // The WebSocket payload is the 15-byte WebCodecs header plus the media payload.
        int wsPayloadLen = 15 + payloadLen;
        int wsHeaderLen;
        int wsStart;
        if (wsPayloadLen <= 125)
        {
            wsHeaderLen = 2;
            wsStart = packetStart - 2;
            buffer[wsStart] = 0x82;
            buffer[wsStart + 1] = (byte)wsPayloadLen;
        }
        else if (wsPayloadLen <= 65535)
        {
            wsHeaderLen = 4;
            wsStart = packetStart - 4;
            buffer[wsStart] = 0x82;
            buffer[wsStart + 1] = 126;
            buffer[wsStart + 2] = (byte)(wsPayloadLen >> 8);
            buffer[wsStart + 3] = (byte)(wsPayloadLen & 0xFF);
        }
        else
        {
            wsHeaderLen = 10;
            wsStart = packetStart - 10;
            buffer[wsStart] = 0x82;
            buffer[wsStart + 1] = 127;
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(wsStart + 2, 8), wsPayloadLen);
        }

        return (wsStart, wsHeaderLen + wsPayloadLen);
    }

    /// <summary>
    /// Sends a binary frame whose payload already lives at
    /// <c>buffer[payloadStart .. payloadStart + payloadLen)</c>, writing the 15-byte WebCodecs
    /// header and the minimal WebSocket header into the reserved space immediately in front of
    /// it and emitting everything as one socket write. This removes the per-frame
    /// <c>BuildPacket</c> allocation and the <c>payload.CopyTo</c> that the legacy path paid for
    /// every JPEG frame. <paramref name="payloadStart"/> must be at least
    /// <see cref="MaxFrameHeaderBytes"/>.
    /// </summary>
    public static async Task SendBufferedBinaryAsync(
        NetworkStream stream,
        SemaphoreSlim streamLock,
        byte[] buffer,
        int payloadStart,
        int payloadLen,
        byte codecType,
        bool isKeyframe,
        long timestampUs,
        short curX,
        short curY,
        bool curVisible,
        CancellationToken token)
    {
        var (wsStart, totalLen) = WriteHeaders(
            buffer, payloadStart, payloadLen, codecType, isKeyframe, timestampUs, curX, curY, curVisible);

        await streamLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(buffer.AsMemory(wsStart, totalLen), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
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

        if (payload.Length <= 65535)
        {
            var combined = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, combined, 0, header.Length);
            Buffer.BlockCopy(payload, 0, combined, header.Length, payload.Length);
            await stream.WriteAsync(combined, token);
        }
        else
        {
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(payload, token);
        }
        await stream.FlushAsync(token);
    }
}
