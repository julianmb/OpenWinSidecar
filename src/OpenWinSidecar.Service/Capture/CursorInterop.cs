using System.Runtime.InteropServices;

namespace OpenWinSidecar.Service.Capture;

/// <summary>
/// A desktop frame captured at the display's own resolution.
/// The Bitmap is owned and reused by the capture source: consumers must finish
/// composing from it before the source overwrites it on a later tick, which the
/// producer/sink busy-handoff guarantees.
/// </summary>
public sealed class NativeFrame
{
    public Bitmap Bitmap = null!;
    public int Width;
    public int Height;

    /// <summary>Direct pointer or raw byte buffer backing the frame (avoids LockBits overhead when available).</summary>
    public byte[]? RawBuffer;
    public int Stride;

    /// <summary>True when the bitmap content was refreshed on the latest tick.</summary>
    public bool Captured;

    /// <summary>
    /// Native-pixel dirty regions refreshed by the latest capture (empty when none or when
    /// <see cref="FullFrame"/> is set). Consumers copy only these instead of the whole frame.
    /// </summary>
    public List<Rectangle> DirtyRects = new();

    /// <summary>True when the whole bitmap was refreshed (first frame, move rects, fallback).</summary>
    public bool FullFrame;

    /// <summary>Screen-space cursor position (per-tick, always fresh).</summary>
    public int CursorGlobalX;
    public int CursorGlobalY;
    public bool CursorVisible;
    public IntPtr CursorHandle;
    public int CursorHotspotX;
    public int CursorHotspotY;
}

/// <summary>
/// Shared Win32 cursor queries used by capture sources and the broadcast producer.
/// </summary>
public static class CursorInterop
{
    public const int CURSOR_SHOWING = 0x00000001;
    public const uint DI_NORMAL = 0x0003;
    private const uint DESKTOP_ACCESS_ALL = 0x01FF;
    private static readonly IntPtr IdcArrow = (IntPtr)32512;
    private static IntPtr _defaultArrowCursor = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
        public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation;
        public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution;
        public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern int EnumDisplaySettingsA(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    /// <summary>
    /// Attaches the calling thread to the interactive desktop (default desktop where user inputs/cursors live).
    /// Required for background worker threads to call GetCursorInfo/GetCursorPos without ERROR_ACCESS_DENIED (5).
    /// </summary>
    public static bool EnsureInputDesktopAttached()
    {
        try
        {
            IntPtr hDesk = OpenInputDesktop(0, false, DESKTOP_ACCESS_ALL);
            if (hDesk != IntPtr.Zero)
            {
                return SetThreadDesktop(hDesk);
            }
        }
        catch { }
        return false;
    }

    public static IntPtr GetDefaultArrowCursor()
    {
        if (_defaultArrowCursor == IntPtr.Zero)
        {
            try { _defaultArrowCursor = LoadCursor(IntPtr.Zero, IdcArrow); } catch { }
        }
        return _defaultArrowCursor;
    }

    // Hotspot cache: GetIconInfo allocates two GDI bitmaps per call (deleted immediately), so doing
    // it 60x/sec per display adds jitter. Cursor shapes are few and stable — cache by handle.
    private static readonly Dictionary<IntPtr, (int xHot, int yHot)> _hotspotCache = new();
    private static readonly object _hotspotLock = new();
    private const int MaxHotspotCacheEntries = 32;

    private static bool TryGetCachedHotspot(IntPtr cursor, out int xHot, out int yHot)
    {
        lock (_hotspotLock)
        {
            if (_hotspotCache.TryGetValue(cursor, out var hit))
            {
                xHot = hit.xHot; yHot = hit.yHot;
                return true;
            }
        }
        xHot = 0; yHot = 0;
        return false;
    }

    private static void CacheHotspot(IntPtr cursor, int xHot, int yHot)
    {
        if (cursor == IntPtr.Zero) return;
        lock (_hotspotLock)
        {
            if (_hotspotCache.Count >= MaxHotspotCacheEntries)
                _hotspotCache.Clear();
            _hotspotCache[cursor] = (xHot, yHot);
        }
    }

    /// <summary>Returns true when a visible cursor handle is available.</summary>
    public static bool TryGetCursor(out CURSORINFO ci)
    {
        ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (GetCursorInfo(ref ci))
        {
            if ((ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                return true;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 5) // ERROR_ACCESS_DENIED
            {
                EnsureInputDesktopAttached();
                ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                    return true;
            }
        }

        return false;
    }

    public static void FillFrameCursor(NativeFrame frame)
    {
        frame.CursorVisible = false;
        frame.CursorHandle = IntPtr.Zero;

        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        bool gotInfo = GetCursorInfo(ref ci);
        if (!gotInfo)
        {
            EnsureInputDesktopAttached();
            gotInfo = GetCursorInfo(ref ci);
        }

        if (gotInfo)
        {
            frame.CursorGlobalX = ci.ptScreenPos.x;
            frame.CursorGlobalY = ci.ptScreenPos.y;
            frame.CursorHandle = ((ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                ? ci.hCursor
                : GetDefaultArrowCursor();
            frame.CursorVisible = frame.CursorHandle != IntPtr.Zero;

            if (!TryGetCachedHotspot(frame.CursorHandle, out var hotX, out var hotY))
            {
                if (GetIconInfo(frame.CursorHandle, out var ii))
                {
                    hotX = ii.xHotspot;
                    hotY = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    CacheHotspot(frame.CursorHandle, hotX, hotY);
                }
            }
            frame.CursorHotspotX = hotX;
            frame.CursorHotspotY = hotY;
        }
        else if (GetCursorPos(out var pt))
        {
            frame.CursorGlobalX = pt.x;
            frame.CursorGlobalY = pt.y;
            frame.CursorHandle = GetDefaultArrowCursor();
            frame.CursorVisible = frame.CursorHandle != IntPtr.Zero;
            frame.CursorHotspotX = 0;
            frame.CursorHotspotY = 0;
        }
    }

    public static (int x, int y, int width, int height, int refreshRate) GetPhysicalScreenBounds(string? deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsA(deviceName, -1, ref dm) != 0 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                int hz = dm.dmDisplayFrequency > 0 ? dm.dmDisplayFrequency : 60;
                return (dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight, hz);
            }
        }
        return (0, 0, 1920, 1080, 60);
    }
}
