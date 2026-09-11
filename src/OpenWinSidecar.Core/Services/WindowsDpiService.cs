using System;
using System.Runtime.InteropServices;

namespace OpenWinSidecar.Core.Services;

public static class WindowsDpiService
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel;
        public int curScaleRel;
        public int maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNum;
        public uint refreshRateDen;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] modeInfo;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET setPacket);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    /// <summary>
    /// Sets Windows Display Scaling (100%, 125%, 150%, 175%, 200%, 225%) for a target monitor index.
    /// </summary>
    public static bool SetMonitorDpiPercent(int displayIndex, int percent)
    {
        try
        {
            uint pathCount, modeCount;
            int status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (status != 0 || pathCount == 0) return false;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status != 0) return false;

            // Target the requested monitor or the virtual display (last path)
            int targetIdx = displayIndex;
            if (targetIdx < 0 || targetIdx >= pathCount)
            {
                targetIdx = (int)pathCount - 1;
            }

            // Map percent to Windows relative scale steps (base 100% = -1, 125% = 0, 150% = 1, 175% = 2, 200% = 3, 225% = 4)
            int relScale = percent switch
            {
                <= 100 => -1,
                <= 125 => 0,
                <= 150 => 1,
                <= 175 => 2,
                <= 200 => 3,
                _ => 4
            };

            var dpiSet = new DISPLAYCONFIG_SOURCE_DPI_SCALE_SET();
            dpiSet.header.type = DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE;
            dpiSet.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DPI_SCALE_SET));
            dpiSet.header.adapterId = paths[targetIdx].sourceInfo.adapterId;
            dpiSet.header.id = paths[targetIdx].sourceInfo.id;
            dpiSet.scaleRel = relScale;

            int setRes = DisplayConfigSetDeviceInfo(ref dpiSet);
            Console.WriteLine($"[Windows DPI] Set monitor #{targetIdx} scaling to {percent}% (rel={relScale}) -> Result: {setRes}");
            return (setRes == 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Windows DPI] Error: {ex.Message}");
            return false;
        }
    }
}
