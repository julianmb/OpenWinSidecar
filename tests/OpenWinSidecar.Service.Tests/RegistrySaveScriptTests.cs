using System.Text;
using OpenWinSidecar.Core.Models;
using OpenWinSidecar.Core.Services;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Guards the fix for the command-injection in the elevated settings save: the password is passed
/// as a single-quote-escaped literal inside a Base64 <c>-EncodedCommand</c> script, never
/// interpolated into a command line.
/// </summary>
public class RegistrySaveScriptTests
{
    [Fact]
    public void Quote_DoublesEmbeddedSingleQuotes()
    {
        Assert.Equal("'abc'", RegistrySaveScript.Quote("abc"));
        Assert.Equal("'a''b'", RegistrySaveScript.Quote("a'b"));
        Assert.Equal("''''", RegistrySaveScript.Quote("'")); // a lone quote -> two escaped quotes in a literal
        Assert.Equal("''", RegistrySaveScript.Quote(null));
    }

    [Fact]
    public void Build_KeepsInjectionAttemptInsideQuotedLiteral()
    {
        var evil = "x'; Remove-Item -Recurse C:\\ -Force; '";
        var settings = new SidecarSettings { EncryptionPassword = evil };

        var script = RegistrySaveScript.Build(settings, protectedValue: null, plaintextValue: evil);

        // The raw password must never appear un-escaped; every embedded quote is doubled, which
        // keeps the injected command inside a PowerShell single-quoted string.
        Assert.Contains("x''; Remove-Item -Recurse C:\\ -Force; ''", script);
    }

    [Fact]
    public void Build_WhenPasswordProtected_ClearsLegacyPlaintextValue()
    {
        var settings = new SidecarSettings { EncryptionPassword = "hunter2" };

        var script = RegistrySaveScript.Build(settings, protectedValue: "BASE64==", plaintextValue: "hunter2");

        Assert.Contains("-Name 'EncryptionPasswordProtected' -Value 'BASE64==' -Type String", script);
        Assert.Contains("-Name 'EncryptionPassword' -Value '' -Type String", script);
        Assert.DoesNotContain("hunter2", script); // plaintext is not persisted when DPAPI succeeded
    }

    [Fact]
    public void Build_WhenProtectionUnavailable_StoresPlaintextFallback()
    {
        var settings = new SidecarSettings { EncryptionPassword = "hunter2" };

        var script = RegistrySaveScript.Build(settings, protectedValue: null, plaintextValue: "hunter2");

        Assert.Contains("-Name 'EncryptionPassword' -Value 'hunter2' -Type String", script);
    }

    [Fact]
    public void EncodeToBase64_RoundTripsAsUtf16()
    {
        var script = "Write-Output 'hi'";

        var encoded = RegistrySaveScript.EncodeToBase64(script);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Equal(script, decoded);
    }
}
