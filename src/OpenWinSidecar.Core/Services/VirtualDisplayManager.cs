using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenWinSidecar.Core.Services;

public class VirtualDisplayManager
{
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    private const uint SDC_ALLOW_CHANGES = 0x00000400;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    public static void EnableExtendMode()
    {
        try
        {
            SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND | SDC_ALLOW_CHANGES);
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "displayswitch.exe",
                Arguments = "/extend",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(2000);
        }
        catch { }
    }

    public static void EnableMirrorMode()
    {
        try
        {
            SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_CLONE | SDC_ALLOW_CHANGES);
        }
        catch { }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "displayswitch.exe",
                Arguments = "/clone",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(2000);
        }
        catch { }
    }

    private static string FindProjectFile(string relativePath)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate = Path.Combine(baseDir, relativePath);
        if (File.Exists(candidate)) return candidate;

        // Walk up to find repository root containing drivers/
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "drivers", "VDD", relativePath);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "gh", "drivers", "VDD", relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }

    public static bool IsVirtualDisplayEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-devices /instanceid \"ROOT\\DISPLAY\\0000\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                if (output.Contains("Status:                     Started", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        try
        {
            var monitors = DisplayResolutionManager.GetAllMonitorsDetailed();
            return monitors.Any(m => m.IsVirtual || m.Index >= 2);
        }
        catch
        {
            return false;
        }
    }

    public static bool DisableVirtualDisplay()
    {
        bool success = false;
        try
        {
            var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");
            if (File.Exists(devconPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "disable \"ROOT\\DISPLAY\\0000\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) success = true;
            }
        }
        catch { }

        try
        {
            var psiPnp = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/disable-device \"ROOT\\DISPLAY\\0000\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pPnp = Process.Start(psiPnp);
            pPnp?.WaitForExit(3000);
            if (pPnp?.ExitCode == 0) success = true;
        }
        catch { }

        if (!success)
        {
            try
            {
                var psiElevated = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"pnputil /disable-device 'ROOT\\DISPLAY\\0000'\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var pElevated = Process.Start(psiElevated);
                pElevated?.WaitForExit(4000);
                success = (pElevated?.ExitCode == 0);
            }
            catch { }
        }

        Thread.Sleep(400);
        return success;
    }

    public static bool EnableVirtualDisplay()
    {
        bool success = false;
        try
        {
            var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");
            if (File.Exists(devconPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "enable \"ROOT\\DISPLAY\\0000\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) success = true;
            }
        }
        catch { }

        try
        {
            var psiPnp = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enable-device \"ROOT\\DISPLAY\\0000\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pPnp = Process.Start(psiPnp);
            pPnp?.WaitForExit(3000);
            if (pPnp?.ExitCode == 0) success = true;
        }
        catch { }

        if (!success)
        {
            try
            {
                var psiElevated = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"pnputil /enable-device 'ROOT\\DISPLAY\\0000'\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var pElevated = Process.Start(psiElevated);
                pElevated?.WaitForExit(4000);
                success = (pElevated?.ExitCode == 0);
            }
            catch { }
        }

        Thread.Sleep(500);
        EnableExtendMode();
        return success;
    }

    public static bool RestartVirtualDisplayDriver()
    {
        bool success = false;
        try
        {
            var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");
            if (File.Exists(devconPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "restart \"ROOT\\DISPLAY\\0000\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(4000);
                success = (p?.ExitCode == 0);
            }
        }
        catch { }

        // Also try pnputil /restart-device
        try
        {
            var psiPnp = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/restart-device \"ROOT\\DISPLAY\\0000\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pPnp = Process.Start(psiPnp);
            pPnp?.WaitForExit(4000);
            if (pPnp?.ExitCode == 0) success = true;
        }
        catch { }

        // Re-extend desktop immediately after driver restart
        Thread.Sleep(500);
        EnableExtendMode();
        return success;
    }

    public static bool InstallVirtualDisplayDriver()
    {
        try
        {
            var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");
            var infPath = FindProjectFile(@"drivers\VDD\control\SignedDrivers\x86\VDD\MttVDD.inf");

            if (!File.Exists(devconPath) || !File.Exists(infPath)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = devconPath,
                Arguments = $"install \"{infPath}\" Root\\MttVDD",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            EnableExtendMode();
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
