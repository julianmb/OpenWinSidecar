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

    public void MoveMouseToScreen(int displayIndex, double normalizedX, double normalizedY, double zoom = 1.0)
    {
        var screens = Screen.AllScreens;
        Rectangle bounds;
        if (displayIndex >= 0 && displayIndex < screens.Length)
        {
            bounds = screens[displayIndex].Bounds;
        }
        else
        {
            bounds = screens.Length > 1 ? screens[1].Bounds : screens[0].Bounds;
        }

        double z = Math.Clamp(zoom, 1.0, 3.0);
        int activeW = (int)(bounds.Width / z);
        int activeH = (int)(bounds.Height / z);

        // Calculate exact absolute desktop pixel coordinates with zoom magnification
        int pixelX = bounds.X + (int)(Math.Clamp(normalizedX, 0.0, 1.0) * activeW);
        int pixelY = bounds.Y + (int)(Math.Clamp(normalizedY, 0.0, 1.0) * activeH);

        // SetCursorPos provides zero-rounding, pixel-perfect cursor positioning
        SetCursorPos(pixelX, pixelY);
    }

    public void MouseClick(bool left, bool down)
    {
        uint flag = left
            ? (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP)
            : (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUT_UNION
            {
                mi = new MOUSEINPUT { dwFlags = flag }
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
