using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

public class SpacedeskManager
{
    public SpacedeskServiceController ServiceController { get; } = new();
    public SpacedeskRegistryManager RegistryManager { get; } = new();
    public NetworkDiscoveryService NetworkService { get; } = new();
    public ServiceProcessManager ProcessManager { get; } = new();

    public SpacedeskServiceState CurrentServiceState { get; private set; } = SpacedeskServiceState.Unknown;
    public SpacedeskSettings CurrentSettings { get; private set; } = new();
    public List<SpacedeskClientInfo> Clients { get; private set; } = new();
    public List<NetworkEndpointInfo> NetworkEndpoints { get; private set; } = new();
    public List<DisplayMonitorInfo> DisplayMonitors { get; private set; } = new();
    public bool IsVirtualDisplayActive { get; private set; } = false;

    public event EventHandler? StateChanged;

    public async Task RefreshStateAsync()
    {
        await Task.Run(() =>
        {
            CurrentServiceState = ServiceController.GetServiceState();
            CurrentSettings = RegistryManager.GetSettings();
            Clients = RegistryManager.GetClients();
            NetworkEndpoints = NetworkService.GetActiveNetworkEndpoints();
            DisplayMonitors = DisplayResolutionManager.GetAllMonitorsDetailed();
            IsVirtualDisplayActive = VirtualDisplayManager.IsVirtualDisplayEnabled();
        });

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Enables the virtual 3rd screen (driver) and starts streaming service.
    /// </summary>
    public (bool Success, string Message) EnableVirtualDisplayAndStartService()
    {
        bool driverOk = VirtualDisplayManager.EnableVirtualDisplay();
        var (srvStarted, srvMsg) = ProcessManager.StartInteractive();
        
        if (srvStarted)
        {
            return (true, "Virtual 3rd Screen enabled and streaming service started.");
        }
        return (driverOk, $"Virtual 3rd Screen enabled. Service: {srvMsg}");
    }

    /// <summary>
    /// Disables the virtual 3rd screen (driver) and stops streaming service.
    /// </summary>
    public (bool Success, string Message) DisableVirtualDisplayAndStopService()
    {
        ProcessManager.StopInteractive();
        bool driverDisabled = VirtualDisplayManager.DisableVirtualDisplay();
        return (true, "Virtual 3rd Screen disabled and streaming service stopped.");
    }

    /// <summary>
    /// Complete shutdown of both streaming service and virtual display driver.
    /// </summary>
    public (bool Success, string Message) CompleteShutdown()
    {
        ProcessManager.StopInteractive();
        ProcessManager.KillAllStaleProcesses();
        bool driverDisabled = VirtualDisplayManager.DisableVirtualDisplay();
        return (true, "Complete shutdown performed: Service terminated, stale processes cleaned, virtual display driver disabled.");
    }
}
