using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

public class NetworkDiscoveryService
{
    public List<NetworkEndpointInfo> GetActiveNetworkEndpoints()
    {
        var result = new List<NetworkEndpointInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ipProps = ni.GetIPProperties();
                var addresses = ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork || u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(u => u.Address.ToString())
                    .ToList();

                if (addresses.Count == 0) continue;

                string primaryIpv4 = addresses.FirstOrDefault(a => !a.Contains(':') && !a.StartsWith("127.")) ?? "";
                var (priority, category) = ClassifyEndpoint(ni.Name, ni.Description, ni.NetworkInterfaceType.ToString(), primaryIpv4);

                result.Add(new NetworkEndpointInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    InterfaceType = ni.NetworkInterfaceType.ToString(),
                    Status = ni.OperationalStatus.ToString(),
                    IpAddresses = addresses,
                    Priority = priority,
                    Category = category
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[NetworkDiscoveryService] Error querying networks: {ex.Message}");
        }

        // Return highest priority usable endpoints first (Apple USB/Tethering -> Wi-Fi -> Physical Ethernet -> ...)
        return result.OrderByDescending(e => e.Priority).ToList();
    }

    public static (int Priority, string Category) ClassifyEndpoint(string name, string desc, string ifType, string ip)
    {
        string combined = (name + " " + desc).ToLowerInvariant();

        // 1. Filter out unusable link-local APIPA addresses (169.254.x.x)
        if (ip.StartsWith("169.254.") || ip.StartsWith("127.") || string.IsNullOrEmpty(ip))
        {
            return (0, "Disabled");
        }

        // 2. Apple USB Tethering / NDIS / RNDIS / USB Ethernet (Highest priority - wired 0ms latency)
        if (combined.Contains("apple") || combined.Contains("rndis") || combined.Contains("ncm") || 
            combined.Contains("tether") || (combined.Contains("usb") && combined.Contains("ethernet")))
        {
            return (100, "⚡ USB Cable");
        }

        // 3. Physical Wi-Fi (Primary wireless interface for iPad Safari)
        if (ifType.Contains("Wireless") || combined.Contains("wi-fi") || combined.Contains("wifi") || combined.Contains("wlan"))
        {
            return (90, "📶 Wi-Fi LAN");
        }

        // 4. Virtual internal host-only switches (Hyper-V, WSL, Docker, VirtualBox, VMware)
        // 172.16-31 on vEthernet / Default Switch is internal to Windows and NOT accessible to external iPads!
        if (combined.Contains("vethernet") || combined.Contains("hyper-v") || combined.Contains("wsl") || 
            combined.Contains("docker") || combined.Contains("virtualbox") || combined.Contains("vmware") || 
            combined.Contains("host-only") || name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
        {
            return (10, "💻 Virtual Switch (Internal)");
        }

        // 5. VPN / Overlay networks (Tailscale, ZeroTier, WireGuard, TAP, TUN)
        if (combined.Contains("tailscale") || combined.Contains("zerotier") || combined.Contains("wireguard") ||
            combined.Contains("openvpn") || combined.Contains("tap") || combined.Contains("tun"))
        {
            return (30, "🔒 VPN");
        }

        // 6. Physical Wired Ethernet
        if (ifType.Contains("Ethernet"))
        {
            return (80, "🔌 Wired Ethernet");
        }

        // 7. Standard private LAN IPs (192.168.x.x or 10.x.x.x)
        if (ip.StartsWith("192.168.") || ip.StartsWith("10."))
        {
            return (70, "🌐 Local LAN");
        }

        return (40, "🌐 Network");
    }
}
