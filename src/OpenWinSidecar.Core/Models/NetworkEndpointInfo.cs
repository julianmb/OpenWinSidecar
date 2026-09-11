namespace OpenWinSidecar.Core.Models;

public class NetworkEndpointInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public List<string> IpAddresses { get; set; } = new();
    public int Priority { get; set; } = 50;
    public string Category { get; set; } = string.Empty;
    public string PrimaryIpAddress => IpAddresses.FirstOrDefault(ip => !ip.Contains(':')) ?? IpAddresses.FirstOrDefault() ?? "N/A";
    public string DisplayLabel => string.IsNullOrEmpty(Category) ? $"{PrimaryIpAddress} ({Name})" : $"{Category}: {PrimaryIpAddress} ({Name})";
    public string FormattedSummary => $"{Name} ({InterfaceType}) - {string.Join(", ", IpAddresses)}";
}
