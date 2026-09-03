using Microsoft.Win32;
using OpenWinSidecar.Core.Models;
using System.Diagnostics;

namespace OpenWinSidecar.Core.Services;

public class SpacedeskRegistryManager
{
    private const string BaseRegistryPath = @"SOFTWARE\datronicsoft\spacedesk";
    private const string SpcdskRegistryPath = @"SOFTWARE\datronicsoft\spcdsk";

    public SpacedeskSettings GetSettings()
    {
        var settings = new SpacedeskSettings();

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
                var encVal = serviceKey.GetValue("EncryptionPassword");
                if (encVal is string encStr)
                {
                    settings.EncryptionPassword = encStr;
                }

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

    public bool SaveSettings(SpacedeskSettings settings)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.CreateSubKey(BaseRegistryPath, writable: true);
            baseKey.SetValue("VideoWallDisconnectDelay", settings.VideoWallDisconnectDelay, RegistryValueKind.DWord);

            using var serverKey = Registry.LocalMachine.CreateSubKey($@"{BaseRegistryPath}\Server", writable: true);
            serverKey.SetValue("ServerStartType", (int)settings.ServerStartType, RegistryValueKind.DWord);

            using var serviceKey = Registry.LocalMachine.CreateSubKey($@"{BaseRegistryPath}\Service", writable: true);
            serviceKey.SetValue("EncryptionPassword", settings.EncryptionPassword ?? string.Empty, RegistryValueKind.String);
            serviceKey.SetValue("IosUsbControlEnabled", settings.IosUsbControlEnabled ? 1 : 0, RegistryValueKind.DWord);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // If running non-elevated, attempt elevation via PowerShell
            return SaveSettingsElevated(settings);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[RegistryManager] Failed to save settings: {ex.Message}");
            return false;
        }
    }

    private bool SaveSettingsElevated(SpacedeskSettings settings)
    {
        try
        {
            var psCommands = $"Set-ItemProperty -Path 'HKLM:\\{BaseRegistryPath}' -Name 'VideoWallDisconnectDelay' -Value {settings.VideoWallDisconnectDelay} -Type DWord; " +
                             $"Set-ItemProperty -Path 'HKLM:\\{BaseRegistryPath}\\Server' -Name 'ServerStartType' -Value {(int)settings.ServerStartType} -Type DWord; " +
                             $"Set-ItemProperty -Path 'HKLM:\\{BaseRegistryPath}\\Service' -Name 'EncryptionPassword' -Value '{settings.EncryptionPassword}' -Type String; " +
                             $"Set-ItemProperty -Path 'HKLM:\\{BaseRegistryPath}\\Service' -Name 'IosUsbControlEnabled' -Value {(settings.IosUsbControlEnabled ? 1 : 0)} -Type DWord";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommands}\"",
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

    public List<SpacedeskClientInfo> GetClients()
    {
        var clients = new List<SpacedeskClientInfo>();

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(BaseRegistryPath);
            if (baseKey == null) return clients;

            var subKeyNames = baseKey.GetSubKeyNames();
            foreach (var name in subKeyNames)
            {
                if (!name.StartsWith("{") || !name.EndsWith("}")) continue;

                var client = new SpacedeskClientInfo
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
