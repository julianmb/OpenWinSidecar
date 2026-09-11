using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenWinSidecar.Service.Input;

public class InputDispatcher
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x000C;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUT_UNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
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

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int EnumDisplaySettingsA(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private static (int x, int y, int width, int height) GetScreenPhysicalBounds(string? deviceName, int displayIndex)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsA(deviceName, -1, ref dm) != 0 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                return (dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
            }
        }

        var screens = Screen.AllScreens;
        Screen? targetScreen = null;

        if (!string.IsNullOrEmpty(deviceName))
        {
            targetScreen = screens.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        }

        if (targetScreen == null)
        {
            if (displayIndex >= 0 && displayIndex < screens.Length)
                targetScreen = screens[displayIndex];
            else
                targetScreen = screens.Length > 1 ? screens[1] : screens[0];
        }

        if (targetScreen != null)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsA(targetScreen.DeviceName, -1, ref dm) != 0 && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
            {
                return (dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
            }
            return (targetScreen.Bounds.X, targetScreen.Bounds.Y, targetScreen.Bounds.Width, targetScreen.Bounds.Height);
        }

        return (0, 0, 1920, 1080);
    }

    private int _lastPixelX;
    private int _lastPixelY;

    public void MoveMouseToScreen(int displayIndex, double normalizedX, double normalizedY, double zoom = 1.0)
    {
        MoveMouseToScreen(null, displayIndex, normalizedX, normalizedY, zoom);
    }

    public void MoveMouseToScreen(string? deviceName, int displayIndex, double normalizedX, double normalizedY, double zoom = 1.0)
    {
        var (originX, originY, width, height) = GetScreenPhysicalBounds(deviceName, displayIndex);

        double z = Math.Clamp(zoom, 1.0, 3.0);
        int activeW = Math.Min(width, (int)(width / z));
        int activeH = Math.Min(height, (int)(height / z));

        // Center crop offset matching ClientFrameSink.cs
        int cropX = (width - activeW) / 2;
        int cropY = (height - activeH) / 2;

        // Calculate exact absolute desktop pixel coordinates with zoom magnification and crop centering
        int pixelX = originX + cropX + (int)Math.Round(Math.Clamp(normalizedX, 0.0, 1.0) * activeW);
        int pixelY = originY + cropY + (int)Math.Round(Math.Clamp(normalizedY, 0.0, 1.0) * activeH);

        _lastPixelX = pixelX;
        _lastPixelY = pixelY;

        // 1. Primary multi-monitor pointer event injection via SendInput with MOUSEEVENTF_VIRTUALDESK
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vw > 1 && vh > 1)
        {
            int absX = (int)Math.Round((pixelX - vx) * 65535.0 / (vw - 1));
            int absY = (int)Math.Round((pixelY - vy) * 65535.0 / (vh - 1));

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUT_UNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }
    }

    public void MouseClick(bool left, bool down)
    {
        uint flag = left
            ? (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP)
            : (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int absX = vw > 1 ? (int)Math.Round((_lastPixelX - vx) * 65535.0 / (vw - 1)) : 0;
        int absY = vh > 1 ? (int)Math.Round((_lastPixelY - vy) * 65535.0 / (vh - 1)) : 0;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUT_UNION
            {
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void MouseScroll(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUT_UNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = MOUSEEVENTF_WHEEL,
                    mouseData = (uint)delta
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void SendKey(ushort virtualKeyCode, bool isDown)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKeyCode,
                    dwFlags = isDown ? 0 : KEYEVENTF_KEYUP
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void SendKeyWithScan(ushort scanCode, bool isDown)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wScan = scanCode,
                    dwFlags = KEYEVENTF_SCANCODE | (isDown ? 0u : KEYEVENTF_KEYUP)
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private const uint KEYEVENTF_UNICODE = 0x0004;

    public void SendUnicodeChar(char ch)
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wScan = (ushort)ch,
                    dwFlags = KEYEVENTF_UNICODE
                }
            }
        };
        inputs[1] = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUT_UNION
            {
                ki = new KEYBDINPUT
                {
                    wScan = (ushort)ch,
                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                }
            }
        };

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void SendText(string text)
    {
        foreach (char c in text)
        {
            SendUnicodeChar(c);
        }
    }
}
