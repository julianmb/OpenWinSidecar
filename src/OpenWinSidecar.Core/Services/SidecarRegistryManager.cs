using Microsoft.Win32;
using OpenWinSidecar.Core.Models;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OpenWinSidecar.Core.Services;

public class SidecarRegistryManager
{
    private const string BaseRegistryPath = @"SOFTWARE\OpenWinSidecar";

    // The password is stored DPAPI-protected (machine scope) under the *Protected* value. The
    // legacy plaintext value is still read for pre-DPAPI installs and cleared on the next save.
    private const string PasswordValueName = RegistrySaveScript.PasswordValueName;
    private const string ProtectedPasswordValueName = RegistrySaveScript.ProtectedPasswordValueName;
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("OpenWinSidecar.AccessPassword.v1");

    public SidecarSettings GetSettings()
    {
        var settings = new SidecarSettings();

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(BaseRegistryPath);
            if (baseKey != null)
            {
                var delayVal = baseKey.GetValue("VideoWallDisconnectDelay");
                if (delayVal is int delayInt)
                {
                    settings.VideoWallDisconnectDelay = delayInt;
                }
            }

            using var serverKey = Registry.LocalMachine.OpenSubKey($@"{BaseRegistryPath}\Server");
            if (serverKey != null)
            {
                var startVal = serverKey.GetValue("ServerStartType");
                if (startVal is int startInt)
                {
                    settings.ServerStartType = (ServerStartMode)startInt;
                }
            }

            using var serviceKey = Registry.LocalMachine.OpenSubKey($@"{BaseRegistryPath}\Service");
            if (serviceKey != null)
            {
                settings.EncryptionPassword = ReadPassword(serviceKey);

                var iosVal = serviceKey.GetValue("IosUsbControlEnabled");
                if (iosVal is int iosInt)
                {
                    settings.IosUsbControlEnabled = iosInt != 0;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RegistryManager] Error reading settings: {ex.Message}");
        }

        return settings;
    }

    public bool SaveSettings(SidecarSettings settings)
    {
        var (protectedValue, plaintextValue) = ProtectPassword(settings.EncryptionPassword);

        try
        {
            using var baseKey = Registry.LocalMachine.CreateSubKey(BaseRegistryPath, writable: true);
            baseKey.SetValue("VideoWallDisconnectDelay", settings.VideoWallDisconnectDelay, RegistryValueKind.DWord);

            using var serverKey = Registry.LocalMachine.CreateSubKey($@"{BaseRegistryPath}\Server", writable: true);
            serverKey.SetValue("ServerStartType", (int)settings.ServerStartType, RegistryValueKind.DWord);

            using var serviceKey = Registry.LocalMachine.CreateSubKey($@"{BaseRegistryPath}\Service", writable: true);
            serviceKey.SetValue(ProtectedPasswordValueName, protectedValue ?? string.Empty, RegistryValueKind.String);
            // Never keep the plaintext when DPAPI succeeded; only fall back to it if it did not.
            serviceKey.SetValue(PasswordValueName, protectedValue != null ? string.Empty : plaintextValue, RegistryValueKind.String);
            serviceKey.SetValue("IosUsbControlEnabled", settings.IosUsbControlEnabled ? 1 : 0, RegistryValueKind.DWord);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // If running non-elevated, attempt elevation via PowerShell.
            return SaveSettingsElevated(protectedValue, plaintextValue, settings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RegistryManager] Failed to save settings: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes the settings to HKLM from an elevated PowerShell process. Values are passed inside a
    /// Base64-encoded script (<c>-EncodedCommand</c>) with PowerShell single-quote escaping, so a
    /// password containing quotes/backticks/semicolons can neither break the command nor inject
    /// commands — the previous version interpolated the raw password into the command line.
    /// </summary>
    private bool SaveSettingsElevated(string? protectedValue, string plaintextValue, SidecarSettings settings)
    {
        try
        {
            var script = RegistrySaveScript.Build(settings, protectedValue, plaintextValue);
            var encoded = RegistrySaveScript.EncodeToBase64(script);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DPAPI-protects the password; returns (null, plaintext) if protection is unavailable.</summary>
    private static (string? protectedB64, string plaintext) ProtectPassword(string? password)
    {
        var pw = password ?? string.Empty;
        if (pw.Length == 0) return (string.Empty, string.Empty);

        try
        {
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(pw), PasswordEntropy, DataProtectionScope.LocalMachine);
            return (Convert.ToBase64String(enc), pw);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RegistryManager] DPAPI protect failed, storing plaintext fallback: {ex.Message}");
            return (null, pw);
        }
    }

    /// <summary>Reads the DPAPI-protected password, falling back to the legacy plaintext value.</summary>
    private static string ReadPassword(RegistryKey serviceKey)
    {
        if (serviceKey.GetValue(ProtectedPasswordValueName) is string b64 && !string.IsNullOrEmpty(b64))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(b64), PasswordEntropy, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[RegistryManager] DPAPI unprotect failed: {ex.Message}");
            }
        }

        return serviceKey.GetValue(PasswordValueName) as string ?? string.Empty;
    }

    public List<SidecarClientInfo> GetClients()
    {
        var clients = new List<SidecarClientInfo>();

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(BaseRegistryPath);
            if (baseKey == null) return clients;

            var subKeyNames = baseKey.GetSubKeyNames();
            foreach (var name in subKeyNames)
            {
                if (!name.StartsWith("{") || !name.EndsWith("}")) continue;

                var client = new SidecarClientInfo
                {
                    DeviceGuid = name,
                    DisplayName = $"Client {name[..8]}..."
                };

                using var clientKey = baseKey.OpenSubKey(name);
                if (clientKey != null)
                {
                    client.Width = Convert.ToInt32(clientKey.GetValue("Width", 0));
                    client.Height = Convert.ToInt32(clientKey.GetValue("Height", 0));
                    client.OriginX = Convert.ToInt32(clientKey.GetValue("OriginX", 0));
                    client.OriginY = Convert.ToInt32(clientKey.GetValue("OriginY", 0));
                    client.CompressionQuality = Convert.ToInt32(clientKey.GetValue("CompressionQuality", 70));
                    client.EncodingType = (CompressionEncodingType)Convert.ToInt32(clientKey.GetValue("EncodingType", 0));

                    using var volatileKey = clientKey.OpenSubKey("Volatile");
                    if (volatileKey != null)
                    {
                        var connectedVal = volatileKey.GetValue("ClientConnected", 0);
                        client.IsConnected = Convert.ToInt32(connectedVal) != 0;
                        client.VideoPresentTargetId = Convert.ToInt32(volatileKey.GetValue("VideoPresentTargetID", 0));
                        client.AdapterLuid = Convert.ToString(volatileKey.GetValue("AdapterLUID", string.Empty)) ?? string.Empty;
                    }
                }

                clients.Add(client);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RegistryManager] Error enumerating clients: {ex.Message}");
        }

        return clients;
    }
}
