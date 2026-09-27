using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

public class SidecarManager
{
    public SidecarServiceController ServiceController { get; } = new();
    public SidecarRegistryManager RegistryManager { get; } = new();
    public NetworkDiscoveryService NetworkService { get; } = new();
    public ServiceProcessManager ProcessManager { get; } = new();

    public Func<bool>? IsStreamingActive { get; set; }
    public Action? OnStartStreaming { get; set; }
    public Action? OnStopStreaming { get; set; }

    public SidecarServiceState CurrentServiceState { get; private set; } = SidecarServiceState.Unknown;
    public SidecarSettings CurrentSettings { get; private set; } = new();
    public List<SidecarClientInfo> Clients { get; private set; } = new();
    public List<NetworkEndpointInfo> NetworkEndpoints { get; private set; } = new();
    public List<DisplayMonitorInfo> DisplayMonitors { get; private set; } = new();
    public bool IsVirtualDisplayActive { get; private set; } = false;
    public VirtualDisplayDeviceState DeviceState { get; private set; } = VirtualDisplayDeviceState.Unknown;

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

            bool streaming = IsStreamingActive != null ? IsStreamingActive() : ProcessManager.IsProcessRunning;
            DeviceState = VirtualDisplayManager.GetVirtualDisplayDeviceState();
            IsVirtualDisplayActive = streaming && DeviceState == VirtualDisplayDeviceState.Started;
        });

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Enables the virtual 3rd screen (driver) and starts streaming service.
    /// </summary>
    public (bool Success, string Message) EnableVirtualDisplayAndStartService()
    {
        bool driverOk = VirtualDisplayManager.EnableVirtualDisplay();

        if (OnStartStreaming != null)
        {
            // Start the stream regardless: a device that is already enabled needs no
            // successful enable command (devcon/pnputil both fail without elevation), and
            // the host is frequently launched unelevated. What was wrong before was not the
            // behaviour but the reporting — it returned success and set
            // IsVirtualDisplayActive unconditionally, so a missing or disabled driver was
            // presented as a working virtual display.
            OnStartStreaming.Invoke();
            IsVirtualDisplayActive = driverOk;
            return driverOk
                ? (true, "Virtual display enabled and streaming started (in-process).")
                : (false, "Streaming started, but the virtual display driver could not be enabled — " +
                          (VirtualDisplayManager.IsElevated()
                              ? "restart the driver from the display card."
                              : "restart OpenWinSidecar as administrator."));
        }

        var (srvStarted, srvMsg) = ProcessManager.StartInteractive();
        IsVirtualDisplayActive = srvStarted && driverOk;

        if (srvStarted && driverOk)
        {
            return (true, "Virtual 3rd Screen enabled and streaming service started.");
        }
        if (srvStarted)
        {
            return (false, $"Streaming service started, but the virtual display driver could not be enabled. Service: {srvMsg}");
        }
        return (false, $"Virtual 3rd Screen enabled. Service: {srvMsg}");
    }

    /// <summary>
    /// Disables the virtual 3rd screen (driver) and stops streaming service.
    /// </summary>
    public (bool Success, string Message) DisableVirtualDisplayAndStopService()
    {
        if (OnStopStreaming != null)
        {
            OnStopStreaming.Invoke();
        }
        else
        {
            ProcessManager.StopInteractive();
        }
        bool driverDisabled = VirtualDisplayManager.DisableVirtualDisplay();
        IsVirtualDisplayActive = false;
        return (true, "Virtual 3rd Screen disabled and streaming stopped.");
    }

    /// <summary>
    /// Complete shutdown of both streaming service and virtual display driver.
    /// </summary>
    public (bool Success, string Message) CompleteShutdown()
    {
        if (OnStopStreaming != null)
        {
            OnStopStreaming.Invoke();
        }
        else
        {
            ProcessManager.StopInteractive();
            ProcessManager.KillAllStaleProcesses();
        }
        bool driverDisabled = VirtualDisplayManager.DisableVirtualDisplay();
        IsVirtualDisplayActive = false;
        return (true, "Complete shutdown performed: Streaming stopped and virtual display driver disabled.");
    }
}
