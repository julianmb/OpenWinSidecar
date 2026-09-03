using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service;

public class SpacedeskServerWorker : BackgroundService
{
    private readonly ILogger<SpacedeskServerWorker> _logger;
    private SpacedeskDiscoveryServer? _discoveryServer;
    private SpacedeskTcpServer? _tcpServer;

    public SpacedeskServerWorker(ILogger<SpacedeskServerWorker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OpenWinSidecar Ultra-Low Latency Service starting on .NET 10 LTS...");

        // Auto-enable USB Cable Forwarding (Android / Apple USB tethering)
        UsbManager.EnableAdbUsbForwarding(28252);
        var usbIps = UsbManager.GetUsbIpAddresses();
        if (usbIps.Count > 0)
        {
            _logger.LogInformation($"[USB Cable Mode Active] USB IP: {string.Join(", ", usbIps)}");
        }

        _discoveryServer = new SpacedeskDiscoveryServer(28252);
        _discoveryServer.Start();
        _logger.LogInformation("OpenWinSidecar LAN UDP Discovery Broadcast responder started on port 28252");

        _tcpServer = new SpacedeskTcpServer(28252);
        _tcpServer.Start();
        _logger.LogInformation("OpenWinSidecar Ultra-Low Latency WebSocket/Stream Server started on port 28252");

        var tcs = new TaskCompletionSource();
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping OpenWinSidecar Streaming Service...");
            _discoveryServer?.Stop();
            _tcpServer?.Stop();
            tcs.SetResult();
        });

        return tcs.Task;
    }

    public override void Dispose()
    {
        _discoveryServer?.Dispose();
        _tcpServer?.Dispose();
        base.Dispose();
    }
}
