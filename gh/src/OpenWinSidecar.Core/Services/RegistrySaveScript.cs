using System.Text;
using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

/// <summary>
/// Builds the PowerShell script used to write settings to HKLM from an elevated process.
/// Extracted and <c>internal</c> so the escaping/encoding is unit-testable. The script is passed
/// to PowerShell via <c>-EncodedCommand</c> (Base64 of UTF-16LE) and every string literal is
/// single-quote-escaped, so a password containing quotes, backticks, semicolons or newlines can
/// neither terminate the literal nor inject commands. The previous inline version interpolated
/// the raw password straight into the command line.
/// </summary>
internal static class RegistrySaveScript
{
    public const string BasePath = @"SOFTWARE\OpenWinSidecar";
    public const string PasswordValueName = "EncryptionPassword";
    public const string ProtectedPasswordValueName = "EncryptionPasswordProtected";

    /// <summary>Wraps a value as a PowerShell single-quoted literal, doubling embedded quotes.</summary>
    internal static string Quote(string? value)
        => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    internal static string Build(SidecarSettings settings, string? protectedValue, string plaintextValue)
    {
        // When DPAPI succeeded (protectedValue != null) the legacy plaintext value is cleared.
        var plaintextToWrite = protectedValue != null ? string.Empty : plaintextValue;

        return string.Join("; ",
            "$ErrorActionPreference='Stop'",
            $"New-Item -Path 'HKLM:\\{BasePath}' -Force | Out-Null",
            $"Set-ItemProperty -Path 'HKLM:\\{BasePath}' -Name 'VideoWallDisconnectDelay' -Value {settings.VideoWallDisconnectDelay} -Type DWord",
            $"New-Item -Path 'HKLM:\\{BasePath}\\Server' -Force | Out-Null",
            $"Set-ItemProperty -Path 'HKLM:\\{BasePath}\\Server' -Name 'ServerStartType' -Value {(int)settings.ServerStartType} -Type DWord",
            $"New-Item -Path 'HKLM:\\{BasePath}\\Service' -Force | Out-Null",
            $"Set-ItemProperty -Path 'HKLM:\\{BasePath}\\Service' -Name '{ProtectedPasswordValueName}' -Value {Quote(protectedValue ?? string.Empty)} -Type String",
            $"Set-ItemProperty -Path 'HKLM:\\{BasePath}\\Service' -Name '{PasswordValueName}' -Value {Quote(plaintextToWrite)} -Type String",
            $"Set-ItemProperty -Path 'HKLM:\\{BasePath}\\Service' -Name 'IosUsbControlEnabled' -Value {(settings.IosUsbControlEnabled ? 1 : 0)} -Type DWord");
    }

    internal static string EncodeToBase64(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
