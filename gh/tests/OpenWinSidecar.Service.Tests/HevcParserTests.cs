using System.Buffers.Binary;
using System.Reflection;
using OpenWinSidecar.Service.Encoders;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// The Annex-B parser must only emit NALs whose terminating start code it has actually seen.
/// Emitting an unterminated prefix as a whole NAL corrupts the client's decoder (Safari
/// hard-fails with "Decoder failure" and the session permanently degrades to JPEG). This pins
/// the split-read reassembly the silence-flush path depends on.
/// </summary>
public class HevcParserTests
{
    private static readonly MethodInfo ParseAnnexB = typeof(HevcStreamEncoder).GetMethod(
        "ParseAnnexB", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Feed(HevcStreamEncoder encoder, byte[] chunk)
        => ParseAnnexB.Invoke(encoder, new object[] { chunk, chunk.Length });

    [Fact]
    public void Parser_HoldsPartialNalUntilTerminatorArrives()
    {
        var packets = new List<byte[]>();
        var encoder = new HevcStreamEncoder((nalBytes, isKey) => packets.Add(nalBytes));

        // NAL1 (VCL type 19 = 0x26), split across two pipe reads.
        Feed(encoder, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80, 0xAA });
        Assert.Empty(packets); // no terminator yet — nothing may be emitted

        Feed(encoder, new byte[] { 0xBB, 0xCC, 0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80, 0xDD });
        Assert.Empty(packets); // NAL1 complete but its AU isn't — still nothing

        // NAL2 completes and a third first-slice VCL terminates it → the AU flushes whole NALs.
        Feed(encoder, new byte[] { 0xEE, 0x00, 0x00, 0x01, 0x26, 0x01, 0x80, 0xFF });
        Assert.Single(packets);

        // Length-prefixed NAL1 reassembled whole: [len=6][26 01 80 AA BB CC].
        var payload = packets[0];
        Assert.Equal(10, payload.Length);
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4)));
        Assert.Equal(new byte[] { 0x26, 0x01, 0x80, 0xAA, 0xBB, 0xCC }, payload[4..10]);
    }

    [Fact]
    public void Parser_IgnoresLeadingGarbageWithoutStartCode()
    {
        var packets = new List<byte[]>();
        var encoder = new HevcStreamEncoder((nalBytes, isKey) => packets.Add(nalBytes));

        Feed(encoder, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 });
        Assert.Empty(packets);
    }
}
