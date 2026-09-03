using System.Diagnostics;
using System.Net.NetworkInformation;

namespace OpenWinSidecar.Service.Protocol;

public class UsbManager
{
    public static void EnableAdbUsbForwarding(int port = 28252)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "adb.exe",
                Arguments = $"reverse tcp:{port} tcp:{port}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            if (p?.ExitCode == 0)
            {
                Console.WriteLine($"[USB Mode] ADB USB port reverse active: localhost:{port} -> Android USB cable (0ms latency)");
            }
        }
        catch { }
    }

    public static List<string> GetUsbIpAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (desc.Contains("apple") || desc.Contains("rndis") || desc.Contains("ncm") || desc.Contains("usb") || desc.Contains("tether"))
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            result.Add(ip.Address.ToString());
                        }
                    }
                }
            }
        }
        catch { }
        return result;
    }
}
