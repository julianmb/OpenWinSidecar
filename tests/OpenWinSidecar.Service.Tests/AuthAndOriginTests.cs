using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Guards the authentication and origin gates. A regression here either locks out legitimate
/// clients (native tools send no Origin) or, worse, lets a hostile web page drive the PC.
/// </summary>
public class AuthAndOriginTests
{
    private const string Token = "s3cr3t-pw";

    private static string Request(string path, params string[] headers)
        => $"GET {path} HTTP/1.1\r\nHost: 192.168.1.12:8080\r\n" +
           string.Join("", headers.Select(h => h + "\r\n")) + "\r\n";

    // ---- Origin ----

    [Fact]
    public void Origin_Missing_AllowsNativeClients()
    {
        Assert.True(SidecarTcpServer.IsOriginAllowed(Request("/input")));
    }

    [Fact]
    public void Origin_SameHostAndPort_Allows()
    {
        var req = Request("/input", "Origin: http://192.168.1.12:8080");
        Assert.True(SidecarTcpServer.IsOriginAllowed(req));
    }

    [Fact]
    public void Origin_ForeignHost_Denies()
    {
        var req = Request("/input", "Origin: http://evil.example");
        Assert.False(SidecarTcpServer.IsOriginAllowed(req));
    }

    [Fact]
    public void Origin_SameHostDifferentPort_Denies()
    {
        var req = Request("/input", "Origin: http://192.168.1.12:9999");
        Assert.False(SidecarTcpServer.IsOriginAllowed(req));
    }

    [Fact]
    public void Origin_WithoutHostHeader_Denies()
    {
        const string req = "GET /input HTTP/1.1\r\nOrigin: http://192.168.1.12:8080\r\n\r\n";
        Assert.False(SidecarTcpServer.IsOriginAllowed(req));
    }

    [Fact]
    public void Origin_Malformed_Denies()
    {
        var req = Request("/input", "Origin: :::not-a-uri:::");
        Assert.False(SidecarTcpServer.IsOriginAllowed(req));
    }

    // ---- HTTP header token ----

    [Fact]
    public void Header_XAccessToken_Match_Allows()
    {
        var req = Request("/input", "X-Access-Token: s3cr3t-pw");
        Assert.True(SidecarTcpServer.HttpHeaderMatchesToken(req, Token));
    }

    [Fact]
    public void Header_XAccessToken_Mismatch_Denies()
    {
        var req = Request("/input", "X-Access-Token: wrong");
        Assert.False(SidecarTcpServer.HttpHeaderMatchesToken(req, Token));
    }

    [Fact]
    public void Header_Bearer_Match_Allows()
    {
        var req = Request("/input", "Authorization: Bearer s3cr3t-pw");
        Assert.True(SidecarTcpServer.HttpHeaderMatchesToken(req, Token));
    }

    [Fact]
    public void Header_Bearer_Mismatch_Denies()
    {
        var req = Request("/input", "Authorization: Bearer wrong");
        Assert.False(SidecarTcpServer.HttpHeaderMatchesToken(req, Token));
    }

    [Fact]
    public void Header_Absent_Denies()
    {
        Assert.False(SidecarTcpServer.HttpHeaderMatchesToken(Request("/input"), Token));
    }

    // ---- URL query token (legacy, kept for back-compat) ----

    [Fact]
    public void Query_Pw_Match_Allows()
    {
        Assert.True(SidecarTcpServer.HttpQueryMatchesToken("/input?pw=s3cr3t-pw", Token));
    }

    [Fact]
    public void Query_Pw_Mismatch_Denies()
    {
        Assert.False(SidecarTcpServer.HttpQueryMatchesToken("/input?pw=wrong", Token));
    }

    [Fact]
    public void Query_Pw_UrlDecoded()
    {
        Assert.True(SidecarTcpServer.HttpQueryMatchesToken("/input?pw=a%20b", "a b"));
    }

    [Fact]
    public void Query_NoPw_Denies()
    {
        Assert.False(SidecarTcpServer.HttpQueryMatchesToken("/input?action=move", Token));
    }
}
