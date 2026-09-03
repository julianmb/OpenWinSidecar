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

    [DllImport("user32.dll")]
    private static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

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
                                 dev.DeviceName.Contains("DISPLAY85", StringComparison.OrdinalIgnoreCase) ||
                                 (!isPrimary && displayIndex >= 2);

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

    public static bool SetDisplayResolution(string deviceName, int targetWidth, int targetHeight, int refreshRate = 0)
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

            if (EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm) == 0)
            {
                if (EnumDisplaySettings(deviceName, 0, ref dm) == 0) return false;
            }

            dm.dmPelsWidth = targetWidth;
            dm.dmPelsHeight = targetHeight;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;

            if (refreshRate > 0)
            {
                dm.dmDisplayFrequency = refreshRate;
                dm.dmFields |= DM_DISPLAYFREQUENCY;
            }

            int res = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (res == DISP_CHANGE_SUCCESSFUL)
            {
                Console.WriteLine($"[DisplayManager] Changed {deviceName} resolution to {targetWidth}x{targetHeight} @ {refreshRate}Hz");
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DisplayManager] Resolution change error: {ex.Message}");
        }

        return false;
    }
}
