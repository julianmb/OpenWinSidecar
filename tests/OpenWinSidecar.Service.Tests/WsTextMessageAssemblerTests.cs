using System.Text;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// The assembler is what fixed "Safari silently lost codec:hevc": browsers coalesce several
/// WebSocket frames into one TCP read and can split a frame across reads. These tests lock in
/// that both cases are handled, and that non-text frames don't desync the parser.
/// </summary>
public class WsTextMessageAssemblerTests
{
    private static readonly byte[] Mask = { 0x11, 0x22, 0x33, 0x44 };

    [Fact]
    public void CoalescedFrames_AllMessagesEmitted()
    {
        var assembler = new WsTextMessageAssembler();
        var data = Concat(Frame("codec:hevc"), Frame("display:2"), Frame("quality:80"));

        var got = new List<string>();
        assembler.OnData(data, data.Length, got.Add);

        Assert.Equal(new[] { "codec:hevc", "display:2", "quality:80" }, got);
    }

    [Fact]
    public void FragmentedRead_MessageReassembled()
    {
        var assembler = new WsTextMessageAssembler();
        var frame = Frame("codec:hevc");

        var got = new List<string>();
        for (int i = 0; i < frame.Length; i++)
            assembler.OnData(new[] { frame[i] }, 1, got.Add);

        Assert.Equal(new[] { "codec:hevc" }, got);
    }

    [Fact]
    public void ExtendedLengthFrame_Parsed()
    {
        var assembler = new WsTextMessageAssembler();
        var text = new string('x', 300); // forces the 16-bit length form
        var data = Frame(text);

        var got = new List<string>();
        assembler.OnData(data, data.Length, got.Add);

        Assert.Equal(new[] { text }, got);
    }

    [Fact]
    public void BinaryFrameIgnored_TextAfterStillParsed()
    {
        var assembler = new WsTextMessageAssembler();
        var data = Concat(Frame("first", opcode: 0x2), Frame("second"));

        var got = new List<string>();
        assembler.OnData(data, data.Length, got.Add);

        Assert.Equal(new[] { "second" }, got);
    }

    [Fact]
    public void EmptyTextFrame_YieldsNothing()
    {
        var assembler = new WsTextMessageAssembler();
        var data = Frame("");

        var got = new List<string>();
        assembler.OnData(data, data.Length, got.Add);

        Assert.Empty(got);
    }

    [Fact]
    public void FragmentedText_Reassembled()
    {
        var assembler = new WsTextMessageAssembler();
        var part1 = RawFrame(Encoding.UTF8.GetBytes("codec:"), opcode: 0x1, fin: false);
        var part2 = RawFrame(Encoding.UTF8.GetBytes("hevc"), opcode: 0x0, fin: true);

        var got = new List<string>();
        assembler.OnData(Concat(part1, part2), part1.Length + part2.Length, got.Add);

        Assert.Equal(new[] { "codec:hevc" }, got);
    }

    [Fact]
    public void FragmentedText_SplitAcrossReads_Reassembled()
    {
        var assembler = new WsTextMessageAssembler();
        var full = Concat(
            RawFrame(Encoding.UTF8.GetBytes("display:"), opcode: 0x1, fin: false),
            RawFrame(Encoding.UTF8.GetBytes("2"), opcode: 0x0, fin: true));

        var got = new List<string>();
        for (int i = 0; i < full.Length; i++)
            assembler.OnData(new[] { full[i] }, 1, got.Add);

        Assert.Equal(new[] { "display:2" }, got);
    }

    [Fact]
    public void PingInterleavedInFragment_Ignored()
    {
        var assembler = new WsTextMessageAssembler();
        var part1 = RawFrame(Encoding.UTF8.GetBytes("a"), opcode: 0x1, fin: false);
        var ping = RawFrame(Array.Empty<byte>(), opcode: 0x9, fin: true);
        var part2 = RawFrame(Encoding.UTF8.GetBytes("b"), opcode: 0x0, fin: true);

        var got = new List<string>();
        assembler.OnData(Concat(part1, ping, part2), part1.Length + ping.Length + part2.Length, got.Add);

        Assert.Equal(new[] { "ab" }, got);
    }

    // --- Size ceilings ---------------------------------------------------------
    // The 64 KB accumulator bounds a single frame, but a fragmented message accumulates
    // in a separate list. Before these guards a client could send endless FIN=0 frames
    // and grow the host's heap until it died.

    [Fact]
    public void ContinuationFrames_CannotGrowBeyondTheMessageCeiling()
    {
        var assembler = new WsTextMessageAssembler();
        var chunk = new byte[4096];

        // First frame opens a fragmented message; every later frame is a continuation.
        var opener = RawFrame(chunk, opcode: 0x1, fin: false);
        assembler.OnData(opener, opener.Length, _ => { });

        WsProtocolException? caught = null;
        for (int i = 0; i < 20 && caught == null; i++)
        {
            var cont = RawFrame(chunk, opcode: 0x0, fin: false);
            try { assembler.OnData(cont, cont.Length, _ => { }); }
            catch (WsProtocolException ex) { caught = ex; }
        }

        Assert.NotNull(caught);
        Assert.Contains("fragmented message", caught.Message);
    }

    [Fact]
    public void SingleOversizedFrame_IsRejectedRatherThanAllocated()
    {
        var assembler = new WsTextMessageAssembler();
        // Claim a 32 KB payload in the 16-bit length form without sending the bytes.
        var frame = new byte[4];
        frame[0] = 0x81;              // FIN + text
        frame[1] = 0x80 | 126;        // masked, 16-bit extended length
        frame[2] = 0x80;              // 32768
        frame[3] = 0x00;

        Assert.Throws<WsProtocolException>(() => assembler.OnData(frame, frame.Length, _ => { }));
    }

    [Fact]
    public void SixtyFourBitLengthWithHighBitSet_IsRejectedNotCastNegative()
    {
        var assembler = new WsTextMessageAssembler();
        // 0xFFFF_FFFF_FFFF_FFFF as a 64-bit length. An unchecked cast to int yields -1,
        // which used to make frameEnd negative, slip past the partial-frame guard and
        // then throw on `new byte[-1]` — killing the session with a confusing error.
        var frame = new byte[14];
        frame[0] = 0x81;
        frame[1] = 0x80 | 127;
        for (int i = 0; i < 8; i++) frame[2 + i] = 0xFF;

        Assert.Throws<WsProtocolException>(() => assembler.OnData(frame, frame.Length, _ => { }));
    }

    [Fact]
    public void MessageAtExactlyTheCeiling_IsAccepted()
    {
        var assembler = new WsTextMessageAssembler();
        var text = new string('x', WsTextMessageAssembler.MaxMessageBytes);
        var frame = Frame(text);

        var got = new List<string>();
        assembler.OnData(frame, frame.Length, got.Add);

        Assert.Single(got);
        Assert.Equal(WsTextMessageAssembler.MaxMessageBytes, got[0].Length);
    }

    [Fact]
    public void HostSettingCommands_AreRecognisedAsHostOwned()
    {
        // The desktop app owns these; the server refuses them and answers `hostset`.
        foreach (var cmd in new[] { "display:1", "fps:60", "quality:80", "set_res:1920,1080", "dpi:150", "zoom:2", "mode:extend" })
            Assert.True(SidecarTcpServer.IsHostSettingCommand(cmd), cmd + " is host-owned");

        // Client-side messages must NOT be classified as host settings.
        foreach (var msg in new[] { "codec:hevc", "input:move,0.5,0.5", "key:down,65", "stats:{}", "decerr:boom", "depth:10", "forceidr", "sync:123" })
            Assert.False(SidecarTcpServer.IsHostSettingCommand(msg), msg + " is client-side");
    }

    private static byte[] RawFrame(byte[] payload, byte opcode, bool fin)
    {
        // RFC 6455: 7-bit length for <=125 bytes, otherwise the 16-bit extended form.
        // Truncating a long payload into the 7-bit field silently produced zero-length
        // frames, which made "sends a huge fragmented message" tests pass vacuously.
        bool extended = payload.Length > 125;
        int headerLen = extended ? 4 : 2;
        var frame = new byte[headerLen + Mask.Length + payload.Length];
        frame[0] = (byte)((fin ? 0x80 : 0) | opcode);
        if (extended)
        {
            frame[1] = (byte)(0x80 | 126);
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            frame[1] = (byte)(0x80 | payload.Length);
        }
        Mask.CopyTo(frame, headerLen);
        for (int i = 0; i < payload.Length; i++)
            frame[headerLen + Mask.Length + i] = (byte)(payload[i] ^ Mask[i & 3]);
        return frame;
    }

    private static byte[] Frame(string text, byte opcode = 0x1)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        int headerLen = payload.Length <= 125 ? 2 : payload.Length <= 65535 ? 4 : 10;
        var frame = new byte[headerLen + Mask.Length + payload.Length];

        frame[0] = (byte)(0x80 | opcode); // FIN + opcode
        if (headerLen == 2)
        {
            frame[1] = (byte)(0x80 | payload.Length);
        }
        else if (headerLen == 4)
        {
            frame[1] = 0x80 | 126;
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            frame[1] = 0x80 | 127;
            for (int i = 0; i < 8; i++)
                frame[2 + i] = (byte)((long)payload.Length >> (56 - 8 * i));
        }

        Mask.CopyTo(frame, headerLen);
        int dataStart = headerLen + Mask.Length;
        for (int i = 0; i < payload.Length; i++)
            frame[dataStart + i] = (byte)(payload[i] ^ Mask[i & 3]);

        return frame;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }
}
