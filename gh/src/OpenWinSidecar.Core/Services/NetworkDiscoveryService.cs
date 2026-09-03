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

                result.Add(new NetworkEndpointInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    InterfaceType = ni.NetworkInterfaceType.ToString(),
                    Status = ni.OperationalStatus.ToString(),
                    IpAddresses = addresses
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[NetworkDiscoveryService] Error querying networks: {ex.Message}");
        }

        return result;
    }
}
