using System.Diagnostics;
using System.ServiceProcess;
using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

public class SidecarServiceController
{
    private const string ServiceName = "OpenWinSidecarService";

    public bool IsServiceInstalled()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            _ = controller.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public SidecarServiceState GetServiceState()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => SidecarServiceState.Running,
                ServiceControllerStatus.Stopped => SidecarServiceState.Stopped,
                ServiceControllerStatus.StartPending => SidecarServiceState.StartPending,
                ServiceControllerStatus.StopPending => SidecarServiceState.StopPending,
                ServiceControllerStatus.Paused => SidecarServiceState.Paused,
                ServiceControllerStatus.PausePending => SidecarServiceState.PausePending,
                ServiceControllerStatus.ContinuePending => SidecarServiceState.ContinuePending,
                _ => SidecarServiceState.Unknown
            };
        }
        catch (InvalidOperationException)
        {
            return SidecarServiceState.NotFound;
        }
        catch
        {
            return SidecarServiceState.Unknown;
        }
    }

    public async Task<bool> StartServiceAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(15);
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Running) return true;

            controller.Start();
            await Task.Run(() => controller.WaitForStatus(ServiceControllerStatus.Running, timeout.Value));
            return true;
        }
        catch (InvalidOperationException)
        {
            // Try elevated execution (SC.exe or net start)
            return await ExecuteServiceCommandElevatedAsync("start");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ServiceController] Start failed: {ex.Message}");
            return await ExecuteServiceCommandElevatedAsync("start");
        }
    }

    public async Task<bool> StopServiceAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(15);
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status == ServiceControllerStatus.Stopped) return true;

            controller.Stop();
            await Task.Run(() => controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout.Value));
            return true;
        }
        catch (InvalidOperationException)
        {
            return await ExecuteServiceCommandElevatedAsync("stop");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ServiceController] Stop failed: {ex.Message}");
            return await ExecuteServiceCommandElevatedAsync("stop");
        }
    }

    public async Task<bool> RestartServiceAsync()
    {
        var stopped = await StopServiceAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1000);
        var started = await StartServiceAsync(TimeSpan.FromSeconds(15));
        return started;
    }

    private static async Task<bool> ExecuteServiceCommandElevatedAsync(string action)
    {
        return await Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = $"{action} {ServiceName}",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using var process = Process.Start(startInfo);
                process?.WaitForExit(10000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }
}
