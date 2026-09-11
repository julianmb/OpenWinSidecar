using System.Buffers.Binary;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Wire-format tests for the 15-byte WebCodecs header and the minimal WebSocket frame header.
/// These are the contract the iPad viewer parses byte-for-byte, so a regression here corrupts
/// or desynchronizes the stream.
/// </summary>
public class WebCodecsFramingTests
{
    private const byte Hevc = 2;
    private const byte Intra = 0;

    [Fact]
    public void BuildPacket_LaysOutFifteenByteHeaderThenPayload()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        var packet = WebCodecsFraming.BuildPacket(
            Hevc, isKeyframe: true, timestampUs: 0x0102030405060708L,
            curX: 400, curY: -7, curVisible: true, payload);

        Assert.Equal(15 + payload.Length, packet.Length);
        Assert.Equal(Hevc, packet[0]);
        Assert.Equal(0x03, packet[1]); // keyframe|visible
        Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(2, 8)));
        Assert.Equal((short)400, BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(10, 2)));
        Assert.Equal((short)-7, BinaryPrimitives.ReadInt16BigEndian(packet.AsSpan(12, 2)));
        Assert.Equal(0, packet[14]);
        Assert.Equal(payload, packet[15..]);
    }

    [Fact]
    public void WriteHeaders_SmallFrame_UsesTwoByteWebSocketHeader()
    {
        int payloadLen = 50; // wsPayloadLen = 65 -> <= 125
        var buffer = NewBuffer(payloadLen);

        var (wsStart, totalLen) = WriteHeaders(buffer, payloadLen, Intra, isKeyframe: true, curVisible: false);

        Assert.Equal(WebCodecsFraming.MaxFrameHeaderBytes - 15 - 2, wsStart); // 8
        Assert.Equal(2 + 15 + payloadLen, totalLen);
        Assert.Equal(0x82, buffer[wsStart]);       // FIN + binary opcode
        Assert.Equal(15 + payloadLen, buffer[wsStart + 1]); // unmasked short length
        AssertPacketHeader(buffer, wsStart + 2, Intra, expectedFlags: 0x01);
        AssertPayloadIntact(buffer, wsStart + 2 + 15, payloadLen);
    }

    [Fact]
    public void WriteHeaders_MediumFrame_UsesExtended16BitLength()
    {
        int payloadLen = 5000; // wsPayloadLen = 5015 -> 126 form
        var buffer = NewBuffer(payloadLen);

        var (wsStart, totalLen) = WriteHeaders(buffer, payloadLen, Hevc, isKeyframe: true, curVisible: true);

        Assert.Equal(WebCodecsFraming.MaxFrameHeaderBytes - 15 - 4, wsStart); // 6
        Assert.Equal(4 + 15 + payloadLen, totalLen);
        Assert.Equal(0x82, buffer[wsStart]);
        Assert.Equal(126, buffer[wsStart + 1]);
        int wsLen = (buffer[wsStart + 2] << 8) | buffer[wsStart + 3];
        Assert.Equal(15 + payloadLen, wsLen);
        AssertPacketHeader(buffer, wsStart + 4, Hevc, expectedFlags: 0x03);
        AssertPayloadIntact(buffer, wsStart + 4 + 15, payloadLen);
    }

    [Fact]
    public void WriteHeaders_LargeFrame_UsesExtended64BitLength()
    {
        int payloadLen = 70000; // wsPayloadLen = 70015 -> 127 form
        var buffer = NewBuffer(payloadLen);

        var (wsStart, totalLen) = WriteHeaders(buffer, payloadLen, Hevc, isKeyframe: false, curVisible: false);

        Assert.Equal(0, wsStart); // exactly fills the 25-byte reserve
        Assert.Equal(10 + 15 + payloadLen, totalLen);
        Assert.Equal(0x82, buffer[0]);
        Assert.Equal(127, buffer[1]);
        Assert.Equal((long)(15 + payloadLen), BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(2, 8)));
        AssertPacketHeader(buffer, 10, Hevc, expectedFlags: 0x00);
        AssertPayloadIntact(buffer, 10 + 15, payloadLen);
    }

    [Fact]
    public void WriteHeaders_ReserveIsExactly25Bytes()
    {
        // Regression guard: the sink reserves MaxFrameHeaderBytes in front of the payload. The
        // largest (10-byte) WebSocket header plus the 15-byte WebCodecs header must fit exactly.
        Assert.Equal(25, WebCodecsFraming.MaxFrameHeaderBytes);

        int payloadLen = 1_000_000;
        var buffer = NewBuffer(payloadLen);
        var (wsStart, _) = WriteHeaders(buffer, payloadLen, Hevc, true, false);
        Assert.True(wsStart >= 0);
    }

    private static byte[] NewBuffer(int payloadLen)
    {
        var buffer = new byte[WebCodecsFraming.MaxFrameHeaderBytes + payloadLen];
        for (int i = 0; i < payloadLen; i++)
            buffer[WebCodecsFraming.MaxFrameHeaderBytes + i] = (byte)(i & 0xFF);
        return buffer;
    }

    private static (int wsStart, int totalLen) WriteHeaders(
        byte[] buffer, int payloadLen, byte codec, bool isKeyframe, bool curVisible)
        => WebCodecsFraming.WriteHeaders(
            buffer, WebCodecsFraming.MaxFrameHeaderBytes, payloadLen,
            codec, isKeyframe, 0x0102030405060708L, curX: 123, curY: -45, curVisible);

    private static void AssertPacketHeader(byte[] buffer, int packetStart, byte codec, byte expectedFlags)
    {
        Assert.Equal(codec, buffer[packetStart]);
        Assert.Equal(expectedFlags, buffer[packetStart + 1]);
        Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(packetStart + 2, 8)));
        Assert.Equal((short)123, BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(packetStart + 10, 2)));
        Assert.Equal((short)-45, BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan(packetStart + 12, 2)));
        Assert.Equal(0, buffer[packetStart + 14]);
    }

    private static void AssertPayloadIntact(byte[] buffer, int payloadStart, int payloadLen)
    {
        Assert.Equal((byte)0, buffer[payloadStart]);
        Assert.Equal((byte)((payloadLen - 1) & 0xFF), buffer[payloadStart + payloadLen - 1]);
    }
}
