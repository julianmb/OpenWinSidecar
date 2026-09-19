using System.Net;
using System.Net.NetworkInformation;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service.Tests;

public class LocalPreviewTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.1.2")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_RequiresPreviewWithoutEnumeratingAdapters(string peer)
    {
        Assert.True(SidecarTcpServer.RequiresLocalPreview(IPAddress.Parse(peer), UnavailableAddresses()));
    }

    [Theory]
    [InlineData("192.168.1.12", "192.168.1.12")]
    [InlineData("::ffff:192.168.1.12", "192.168.1.12")]
    [InlineData("192.168.1.12", "::ffff:192.168.1.12")]
    [InlineData("2001:db8::12", "2001:db8::12")]
    [InlineData("fe80::12%3", "fe80::12%3")]
    public void AssignedAddress_RequiresPreview(string peer, string assigned)
    {
        Assert.True(SidecarTcpServer.RequiresLocalPreview(IPAddress.Parse(peer),
            new[] { IPAddress.Parse("10.0.0.2"), IPAddress.Parse(assigned) }));
    }

    [Theory]
    [InlineData("192.168.1.13")]
    [InlineData("::ffff:192.168.1.13")]
    [InlineData("2001:db8::13")]
    [InlineData("203.0.113.1")]
    public void OtherPeer_DoesNotRequirePreview(string peer)
    {
        Assert.False(SidecarTcpServer.RequiresLocalPreview(IPAddress.Parse(peer),
            new[] { IPAddress.Parse("192.168.1.12"), IPAddress.Parse("2001:db8::12") }));
    }

    [Fact]
    public void MissingPeer_DoesNotEnumerateAdapters()
    {
        Assert.False(SidecarTcpServer.RequiresLocalPreview(null, UnavailableAddresses()));
    }

    [Fact]
    public void AdapterEnumerationFailure_DoesNotPreventServing()
    {
        Assert.False(SidecarTcpServer.RequiresLocalPreview(IPAddress.Parse("192.168.1.12"), UnavailableAddresses()));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Injection_ChangesOnlyMarkedBoolean(bool required, string literal)
    {
        const string template = "<script>const other = false; const localPreviewRequired = /*LOCAL_PREVIEW_REQUIRED*/false || loopback;</script>";
        var result = SidecarTcpServer.InjectLocalPreviewRequired(template, required);
        Assert.Equal("<script>const other = false; const localPreviewRequired = /*LOCAL_PREVIEW_REQUIRED*/" + literal + " || loopback;</script>", result);
        // The shared template must not retain one request's classification for the next peer.
        Assert.Equal(template, SidecarTcpServer.InjectLocalPreviewRequired(template, false));
    }

    [Fact]
    public void Injection_MissingMarker_LeavesPageUnchanged()
    {
        const string template = "<script>const other = false;</script>";
        Assert.Equal(template, SidecarTcpServer.InjectLocalPreviewRequired(template, true));
    }

    private static IEnumerable<IPAddress> UnavailableAddresses()
    {
        yield return ThrowUnavailable();
    }

    private static IPAddress ThrowUnavailable() => throw new NetworkInformationException();
}
