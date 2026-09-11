using System.Runtime.InteropServices;
using OpenWinSidecar.Core.Models;

namespace OpenWinSidecar.Core.Services;

public class DisplayResolutionManager
{
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int CDS_TEST = 0x00000002;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private const int DM_POSITION = 0x00000020;
    private const int CDS_NORESET = 0x10000000;

    [DllImport("user32.dll")]
    private static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    public static List<DisplayMonitorInfo> GetAllMonitorsDetailed()
    {
        var monitors = new List<DisplayMonitorInfo>();
        var dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        uint devNum = 0;
        int displayIndex = 0;

        while (EnumDisplayDevices(null, devNum, ref dev, 0))
        {
            const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
            const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

            if ((dev.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                var curMode = GetCurrentDisplayMode(dev.DeviceName);
                var supportedModes = GetSupportedDisplayModes(dev.DeviceName);
                bool isPrimary = (dev.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
                bool isVirtual = dev.DeviceString.Contains("Idd", StringComparison.OrdinalIgnoreCase) ||
                                 dev.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                                 dev.DeviceID.Contains("MttVDD", StringComparison.OrdinalIgnoreCase) ||
                                 dev.DeviceName.Contains("DISPLAY85", StringComparison.OrdinalIgnoreCase) ||
                                 dev.DeviceName.Contains("DISPLAY86", StringComparison.OrdinalIgnoreCase);

                string friendlyName = isPrimary ? "🖥️ Primary Monitor" : (isVirtual ? "📱 Virtual iPad Display" : $"🖥️ Secondary Monitor ({dev.DeviceName})");

                monitors.Add(new DisplayMonitorInfo
                {
                    Index = displayIndex,
                    DeviceName = dev.DeviceName,
                    DisplayName = friendlyName,
                    IsPrimary = isPrimary,
                    IsVirtual = isVirtual,
                    Width = curMode?.Width ?? 1920,
                    Height = curMode?.Height ?? 1080,
                    RefreshRate = curMode?.RefreshRate ?? 60,
                    SupportedModes = supportedModes
                });

                displayIndex++;
            }

            devNum++;
            dev = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return monitors;
    }

    public static DisplayModeInfo? GetCurrentDisplayMode(string deviceName)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) != 0)
        {
            return new DisplayModeInfo
            {
                Width = dm.dmPelsWidth,
                Height = dm.dmPelsHeight,
                RefreshRate = dm.dmDisplayFrequency,
                BitsPerPixel = dm.dmBitsPerPel
            };
        }
        return null;
    }

    public static List<DisplayModeInfo> GetSupportedDisplayModes(string deviceName)
    {
        var modes = new List<DisplayModeInfo>();
        var seen = new HashSet<string>();
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        int modeNum = 0;

        while (EnumDisplaySettings(deviceName, modeNum, ref dm) != 0)
        {
            if (dm.dmBitsPerPel >= 32)
            {
                string key = $"{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}";
                if (!seen.Contains(key))
                {
                    seen.Add(key);
                    modes.Add(new DisplayModeInfo
                    {
                        Width = dm.dmPelsWidth,
                        Height = dm.dmPelsHeight,
                        RefreshRate = dm.dmDisplayFrequency,
                        BitsPerPixel = dm.dmBitsPerPel
                    });
                }
            }
            modeNum++;
            dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        }

        return modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.RefreshRate).ToList();
    }

    /// <summary>
    /// Switches the virtual display to the supported mode whose aspect ratio best matches
    /// the connecting client's screen, so the stream fills the iPad edge-to-edge with no
    /// letterbox bars. Idempotent: if the current aspect already matches (within 0.5%),
    /// nothing happens — repeated set_res messages cannot cause mode-change churn.
    /// Returns the applied mode, or null when no virtual display/mode was available.
    /// </summary>
    public static DisplayModeInfo? MatchVirtualDisplayToClient(int clientWidth, int clientHeight, int targetRefreshRate = 0)
    {
        try
        {
            if (clientWidth <= 0 || clientHeight <= 0) return null;

            var virtualMonitor = GetAllMonitorsDetailed().FirstOrDefault(m => m.IsVirtual);
            if (virtualMonitor == null || virtualMonitor.SupportedModes.Count == 0) return null;

            double clientAspect = (double)clientWidth / clientHeight;

            // Pick the best supported mode matching aspect ratio, preferring requested refresh rate, then native resolution
            var best = virtualMonitor.SupportedModes
                .OrderBy(m => Math.Abs((double)m.Width / m.Height - clientAspect) / clientAspect)
                .ThenBy(m => targetRefreshRate > 0 ? Math.Abs(m.RefreshRate - targetRefreshRate) : 0)
                .ThenByDescending(m => m.Width * m.Height)
                .ThenByDescending(m => m.RefreshRate)
                .FirstOrDefault();

            if (best == null) return null;

            if (virtualMonitor.Width == best.Width && virtualMonitor.Height == best.Height &&
                (targetRefreshRate <= 0 || virtualMonitor.RefreshRate == best.RefreshRate))
            {
                return new DisplayModeInfo
                {
                    Width = virtualMonitor.Width,
                    Height = virtualMonitor.Height,
                    RefreshRate = virtualMonitor.RefreshRate
                };
            }

            if (SetDisplayResolution(virtualMonitor.DeviceName, best.Width, best.Height, best.RefreshRate))
            {
                Console.WriteLine($"[DisplayManager] Virtual display matched to client: {best.Width}x{best.Height} @ {best.RefreshRate}Hz (client {clientWidth}x{clientHeight})");
                return best;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DisplayManager] MatchVirtualDisplayToClient error: {ex.Message}");
        }
        return null;
    }

    public static bool SetDisplayResolution(string deviceName, int targetWidth, int targetHeight, int refreshRate = 0)
    {
        try
        {
            // Ensure thread is attached to input desktop without closing the desktop handle
            try
            {
                IntPtr hDesk = OpenInputDesktop(0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    SetThreadDesktop(hDesk);
                }
            }
            catch { }

            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) == 0)
            {
                if (EnumDisplaySettings(deviceName, 0, ref dm) == 0) return false;
            }

            // If the monitor is already at the requested resolution and refresh rate, return success (idempotent)
            if (dm.dmPelsWidth == targetWidth && dm.dmPelsHeight == targetHeight &&
                (refreshRate <= 0 || dm.dmDisplayFrequency == refreshRate))
            {
                Console.WriteLine($"[DisplayManager] {deviceName} is already at {targetWidth}x{targetHeight} @ {dm.dmDisplayFrequency}Hz");
                return true;
            }

            dm.dmPelsWidth = targetWidth;
            dm.dmPelsHeight = targetHeight;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

            if (refreshRate > 0)
            {
                dm.dmDisplayFrequency = refreshRate;
                dm.dmFields |= DM_DISPLAYFREQUENCY;
            }

            // Attempt 1: Direct update registry without position change
            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"[DisplayManager] Changed {deviceName} resolution to {targetWidth}x{targetHeight} @ {refreshRate}Hz");
                return true;
            }

            // Attempt 2: Multi-monitor two-step commit (CDS_UPDATEREGISTRY | CDS_NORESET, then global commit)
            res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                Console.WriteLine($"[DisplayManager] Changed {deviceName} resolution (two-step) to {targetWidth}x{targetHeight} @ {refreshRate}Hz");
                return true;
            }

            // Attempt 3: Multi-monitor position-adjusted two-step commit if monitor has negative offset
            if (dm.dmPositionX < 0)
            {
                dm.dmPositionX = -targetWidth;
                dm.dmFields |= DM_POSITION;
                res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                if (res == DISP_CHANGE_SUCCESSFUL)
                {
                    ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
                    Console.WriteLine($"[DisplayManager] Changed {deviceName} resolution (position-adjusted) to {targetWidth}x{targetHeight} @ {refreshRate}Hz");
                    return true;
                }
            }

            // Attempt 4: Dynamic on-the-fly change without updating registry
            res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"[DisplayManager] Changed {deviceName} resolution (dynamic) to {targetWidth}x{targetHeight} @ {refreshRate}Hz");
                return true;
            }

            Console.WriteLine($"[DisplayManager] ChangeDisplaySettingsEx returned {res} for {deviceName} {targetWidth}x{targetHeight} @ {refreshRate}Hz");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DisplayManager] Resolution change error: {ex.Message}");
        }

        return false;
    }
}
