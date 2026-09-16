using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using OpenWinSidecar.Core.Models;

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

    /// <summary>
    /// True if the current process is running with administrator privileges.
    /// Used to decide whether enabling a disabled VDD device node can be
    /// attempted directly or needs an elevated restart.
    /// </summary>
    public static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Queries the PnP device node ROOT\DISPLAY\0000 and returns its state.
    /// Unlike <see cref="IsVirtualDisplayEnabled"/>, this distinguishes
    /// "disabled" (needs elevation to enable) from "missing" (needs reinstall)
    /// from "problem" (needs driver restart) so the UI can tell the user
    /// exactly what's wrong instead of a generic "Off".
    /// </summary>
    public static VirtualDisplayDeviceState GetVirtualDisplayDeviceState()
    {
        try
        {
            // Fast path: if the VDD is attached to the desktop as a virtual
            // monitor, it's Started regardless of what pnputil reports.
            var monitors = DisplayResolutionManager.GetAllMonitorsDetailed();
            if (monitors.Any(m => m.IsVirtual))
                return VirtualDisplayDeviceState.Started;
        }
        catch { }

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
            if (p == null) return VirtualDisplayDeviceState.Unknown;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return ParsePnputilDeviceStatus(output);
        }
        catch
        {
            return VirtualDisplayDeviceState.Unknown;
        }
    }

    /// <summary>
    /// Pure parser for `pnputil /enum-devices /instanceid ROOT\DISPLAY\0000` output —
    /// extracted so the device-state classification is unit-testable against captured
    /// real outputs (the fixed-width string matching it replaced was exactly the kind
    /// of code that breaks silently when Windows reformats its tooling).
    /// </summary>
    internal static VirtualDisplayDeviceState ParsePnputilDeviceStatus(string output)
    {
        // pnputil prints nothing for an unknown instance ID — device not installed.
        if (string.IsNullOrWhiteSpace(output))
            return VirtualDisplayDeviceState.Missing;

        // Find the "Status:" line (trim-compare, not fixed-width — the old parser
        // hard-coded the exact column spacing and broke when it shifted).
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = trimmed["Status:".Length..].Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (value.Equals("Started", StringComparison.OrdinalIgnoreCase))
                return VirtualDisplayDeviceState.Started;
            if (value.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                return VirtualDisplayDeviceState.Disabled;
            if (value.Equals("Problem", StringComparison.OrdinalIgnoreCase))
                return VirtualDisplayDeviceState.Problem;
            // Any other status (e.g. "UnKnown") — treat as Problem.
            return VirtualDisplayDeviceState.Problem;
        }

        // "Status:" line not found in output — device exists but pnputil
        // format changed. Fall back to checking if the output mentions
        // the instance ID at all.
        if (output.Contains("ROOT\\DISPLAY\\0000", StringComparison.OrdinalIgnoreCase))
            return VirtualDisplayDeviceState.Problem;
        return VirtualDisplayDeviceState.Missing;
    }

    public static bool IsVirtualDisplayEnabled()
    {
        return GetVirtualDisplayDeviceState() == VirtualDisplayDeviceState.Started;
    }

    public static bool DisableVirtualDisplay()
    {
        bool success = false;
        var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");

        try
        {
            if (File.Exists(devconPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "disable \"@ROOT\\DISPLAY\\0000\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) success = true;
            }
        }
        catch { }

        if (!success)
        {
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
        }

        if (!success && File.Exists(devconPath))
        {
            try
            {
                var psiElevated = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "disable \"@ROOT\\DISPLAY\\0000\"",
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
        var devconPath = FindProjectFile(@"drivers\VDD\control\Dependencies\devcon.exe");

        try
        {
            if (File.Exists(devconPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "enable \"@ROOT\\DISPLAY\\0000\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) success = true;
            }
        }
        catch { }

        if (!success)
        {
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
        }

        if (!success && File.Exists(devconPath))
        {
            try
            {
                var psiElevated = new ProcessStartInfo
                {
                    FileName = devconPath,
                    Arguments = "enable \"@ROOT\\DISPLAY\\0000\"",
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
                    Arguments = "restart \"@ROOT\\DISPLAY\\0000\"",
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
