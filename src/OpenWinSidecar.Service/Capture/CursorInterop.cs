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

    /// <summary>True when the bitmap content was refreshed on the latest tick.</summary>
    public bool Captured;

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

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    /// <summary>Returns true when a visible cursor handle is available.</summary>
    public static bool TryGetCursor(out CURSORINFO ci)
    {
        ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(out ci)
            && (ci.flags & CURSOR_SHOWING) != 0
            && ci.hCursor != IntPtr.Zero;
    }

    public static void FillFrameCursor(NativeFrame frame)
    {
        frame.CursorVisible = false;
        frame.CursorHandle = IntPtr.Zero;

        if (TryGetCursor(out var ci))
        {
            frame.CursorGlobalX = ci.ptScreenPos.x;
            frame.CursorGlobalY = ci.ptScreenPos.y;
            frame.CursorHandle = ci.hCursor;
            frame.CursorVisible = true;

            if (GetIconInfo(ci.hCursor, out var ii))
            {
                frame.CursorHotspotX = ii.xHotspot;
                frame.CursorHotspotY = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
        }
    }
}
