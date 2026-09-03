using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenWinSidecar.Service.Capture;

public class ScreenCaptureService
{
    private static readonly ImageCodecInfo JpegEncoder = GetEncoder(ImageFormat.Jpeg);

    private const uint SRCCOPY = 0x00CC0020;
    private const int COLORONCOLOR = 3;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    private const uint WINSTA_ALL_ACCESS = 0x37F;
    private const uint DESKTOP_ALL_ACCESS = 0x1FF;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

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
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(IntPtr hWinSta);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int nXOriginDest, int nYOriginDest, int nWidthDest, int nHeightDest, IntPtr hdcSrc, int nXOriginSrc, int nYOriginSrc, int nWidthSrc, int nHeightSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int nStretchMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private bool _initializedStation = false;

    public void EnsureInteractiveDesktop()
    {
        if (_initializedStation) return;
        try
        {
            SetProcessDPIAware();
            IntPtr hWinsta = OpenWindowStation("WinSta0", false, WINSTA_ALL_ACCESS);
            if (hWinsta != IntPtr.Zero) SetProcessWindowStation(hWinsta);

            IntPtr hDesktop = OpenDesktop("Default", 0, false, DESKTOP_ALL_ACCESS);
            if (hDesktop != IntPtr.Zero) SetThreadDesktop(hDesktop);
            _initializedStation = true;
        }
        catch { }
    }

    public (Screen screen, int index) ResolveScreen(int displayIndex)
    {
        EnsureInteractiveDesktop();
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
        {
            return (Screen.PrimaryScreen ?? screens[0], 0);
        }

        if (displayIndex < 0 || displayIndex >= screens.Length)
        {
            int defaultIdx = screens.Length > 2 ? 2 : (screens.Length > 1 ? 1 : 0);
            return (screens[defaultIdx], defaultIdx);
        }

        return (screens[displayIndex], displayIndex);
    }

    public (byte[]? frameData, int curX, int curY, bool curVisible) CaptureFrameWithCursor(int displayIndex = -1, int targetWidth = 0, int targetHeight = 0, long quality = 55, double zoom = 1.0, bool drawCursor = true)
    {
        EnsureInteractiveDesktop();

        var (screen, actualIndex) = ResolveScreen(displayIndex);
        int srcWidth = screen.Bounds.Width;
        int srcHeight = screen.Bounds.Height;
        int srcX = screen.Bounds.X;
        int srcY = screen.Bounds.Y;

        if (srcWidth <= 0 || srcHeight <= 0)
        {
            srcWidth = 2360;
            srcHeight = 1640;
        }

        double z = Math.Clamp(zoom, 1.0, 3.0);
        int captureW = (int)(srcWidth / z);
        int captureH = (int)(srcHeight / z);

        // Target rendering width/height (defaults to optimized low-latency 1180x820 if not specified)
        int dstWidth = (targetWidth > 0) ? targetWidth : 1180;
        int dstHeight = (targetHeight > 0) ? targetHeight : 820;

        // Preserve exact aspect ratio to eliminate non-uniform stretching
        double srcAspect = (double)captureW / captureH;
        if (srcAspect > 0)
        {
            dstHeight = (int)Math.Round(dstWidth / srcAspect);
        }

        // Ensure even pixel dimensions for hardware video encoders
        dstWidth = (dstWidth / 2) * 2;
        dstHeight = (dstHeight / 2) * 2;

        double scaleX = (double)dstWidth / captureW;
        double scaleY = (double)dstHeight / captureH;

        IntPtr srcDc = IntPtr.Zero;
        bool isDirectDeviceDc = false;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        int curRelX = -100;
        int curRelY = -100;
        bool curVisible = false;

        try
        {
            srcDc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
            if (srcDc != IntPtr.Zero)
            {
                isDirectDeviceDc = true;
            }
            else
            {
                srcDc = GetDC(IntPtr.Zero);
            }

            if (srcDc == IntPtr.Zero) return (null, 0, 0, false);

            memDc = CreateCompatibleDC(srcDc);
            hBitmap = CreateCompatibleBitmap(srcDc, dstWidth, dstHeight);
            oldObj = SelectObject(memDc, hBitmap);

            int copySrcX = isDirectDeviceDc ? 0 : srcX;
            int copySrcY = isDirectDeviceDc ? 0 : srcY;

            // Direct hardware StretchBlt downscaling / magnification in < 1ms
            if (dstWidth == captureW && dstHeight == captureH)
            {
                BitBlt(memDc, 0, 0, dstWidth, dstHeight, srcDc, copySrcX, copySrcY, SRCCOPY);
            }
            else
            {
                SetStretchBltMode(memDc, COLORONCOLOR);
                StretchBlt(memDc, 0, 0, dstWidth, dstHeight, srcDc, copySrcX, copySrcY, captureW, captureH, SRCCOPY);
            }

            // Draw Windows Mouse Cursor with Hotspot Offset & Scaled Coordinates
            CURSORINFO ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (drawCursor && GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
            {
                int rawRelX = ci.ptScreenPos.x - srcX;
                int rawRelY = ci.ptScreenPos.y - srcY;

                if (rawRelX >= -32 && rawRelX < captureW + 32 && rawRelY >= -32 && rawRelY < captureH + 32)
                {
                    curVisible = true;
                    ICONINFO ii = new ICONINFO();
                    int unscaledDrawX = rawRelX;
                    int unscaledDrawY = rawRelY;
                    if (GetIconInfo(ci.hCursor, out ii))
                    {
                        unscaledDrawX -= ii.xHotspot;
                        unscaledDrawY -= ii.yHotspot;
                        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    }

                    int drawX = (int)(unscaledDrawX * scaleX);
                    int drawY = (int)(unscaledDrawY * scaleY);
                    curRelX = (int)(rawRelX * scaleX);
                    curRelY = (int)(rawRelY * scaleY);

                    int curW = (int)Math.Max(16, 32 * scaleX);
                    int curH = (int)Math.Max(16, 32 * scaleY);

                    DrawIconEx(memDc, drawX, drawY, ci.hCursor, curW, curH, 0, IntPtr.Zero, DI_NORMAL);
                }
            }

            SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            using var bmp = Image.FromHbitmap(hBitmap);
            DeleteObject(hBitmap);
            hBitmap = IntPtr.Zero;

            // Overlay subtle workspace guide if virtual monitor is empty
            if (actualIndex == 2 || screen.DeviceName.Contains("DISPLAY85", StringComparison.OrdinalIgnoreCase))
            {
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var pen = new Pen(Color.FromArgb(16, 255, 255, 255), 1);
                for (int gx = 0; gx < dstWidth; gx += 60) g.DrawLine(pen, gx, 0, gx, dstHeight);
                for (int gy = 0; gy < dstHeight; gy += 60) g.DrawLine(pen, 0, gy, dstWidth, gy);

                using var cardBrush = new SolidBrush(Color.FromArgb(140, 18, 18, 22));
                int cardW = Math.Min(380, dstWidth - 40);
                int cardH = 80;
                int cardX = (dstWidth - cardW) / 2;
                int cardY = (dstHeight - cardH) / 2;
                g.FillRectangle(cardBrush, cardX, cardY, cardW, cardH);
                using var cardBorderPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f);
                g.DrawRectangle(cardBorderPen, cardX, cardY, cardW, cardH);

                using var fontTitle = new Font("Segoe UI", 13, FontStyle.Bold);
                using var fontSub = new Font("Segoe UI", 10, FontStyle.Regular);
                using var textBrush = new SolidBrush(Color.White);
                using var subBrush = new SolidBrush(Color.FromArgb(160, 200, 255));

                g.DrawString("📱 OpenWinSidecar Display", fontTitle, textBrush, cardX + 16, cardY + 14);
                g.DrawString($"Stream: {dstWidth}x{dstHeight} • Ultra-Low Latency Mode", fontSub, subBrush, cardX + 16, cardY + 44);
            }

            using var ms = new MemoryStream(32768);
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 10, 95));

            bmp.Save(ms, JpegEncoder, encParams);

            return (ms.ToArray(), curRelX, curRelY, curVisible);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[ScreenCapture] Error: {ex.Message}");
            return (null, 0, 0, false);
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (srcDc != IntPtr.Zero)
            {
                if (isDirectDeviceDc) DeleteDC(srcDc);
                else ReleaseDC(IntPtr.Zero, srcDc);
            }
        }
    }

    private Bitmap? _nativeBitmap;

    /// <summary>
    /// Captures the display into a reusable Bitmap for the broadcast producer. GDI reads on
    /// indirect (IddCx) displays are expensive at full resolution (~260ms for 2560-wide 1:1
    /// blits), so the working bitmap is capped at 1180px wide — the proven fallback throughput
    /// — while keeping aspect ratio. Desktop Duplication (DXGI) remains the full-res primary.
    /// The cursor is never composited — its screen-space position and handle are returned so
    /// each subscriber can stamp its own cursor state. Returns false only on hard failure.
    /// </summary>
    public bool CaptureNativeFrame(int displayIndex, NativeFrame frame)
    {
        EnsureInteractiveDesktop();

        var (screen, _) = ResolveScreen(displayIndex);
        int srcWidth = screen.Bounds.Width;
        int srcHeight = screen.Bounds.Height;
        int srcX = screen.Bounds.X;
        int srcY = screen.Bounds.Y;

        if (srcWidth <= 0 || srcHeight <= 0)
        {
            srcWidth = 2360;
            srcHeight = 1640;
        }

        // Capped working resolution (even-aligned), aspect-preserving
        int width = Math.Min(srcWidth, 1180);
        width = (width / 2) * 2;
        int height = (int)Math.Round((double)width * srcHeight / srcWidth);
        height = (height / 2) * 2;
        if (height < 2) height = 2;

        CursorInterop.FillFrameCursor(frame);

        IntPtr srcDc = IntPtr.Zero;
        bool isDirectDeviceDc = false;

        try
        {
            srcDc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
            if (srcDc != IntPtr.Zero) isDirectDeviceDc = true;
            else srcDc = GetDC(IntPtr.Zero);

            if (srcDc == IntPtr.Zero) return false;

            if (_nativeBitmap == null || _nativeBitmap.Width != width || _nativeBitmap.Height != height)
            {
                _nativeBitmap?.Dispose();
                _nativeBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            }

            // Blit through a DDB (compatible bitmap) — StretchBlt directly into a 32bpp DIB
            // section reads the indirect display at ~260ms, while the DDB route is ~10-40ms.
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;
            try
            {
                memDc = CreateCompatibleDC(srcDc);
                hBitmap = CreateCompatibleBitmap(srcDc, width, height);
                oldObj = SelectObject(memDc, hBitmap);

                int copySrcX = isDirectDeviceDc ? 0 : srcX;
                int copySrcY = isDirectDeviceDc ? 0 : srcY;

                if (width == srcWidth && height == srcHeight)
                {
                    BitBlt(memDc, 0, 0, width, height, srcDc, copySrcX, copySrcY, SRCCOPY);
                }
                else
                {
                    SetStretchBltMode(memDc, COLORONCOLOR);
                    StretchBlt(memDc, 0, 0, width, height, srcDc, copySrcX, copySrcY, srcWidth, srcHeight, SRCCOPY);
                }

                SelectObject(memDc, oldObj);
                oldObj = IntPtr.Zero;

                using var blitted = Image.FromHbitmap(hBitmap);
                using (var g = Graphics.FromImage(_nativeBitmap))
                {
                    g.DrawImage(blitted, 0, 0, width, height);
                }
            }
            finally
            {
                if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
            }

            frame.Bitmap = _nativeBitmap;
            frame.Width = width;
            frame.Height = height;
            frame.Captured = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (srcDc != IntPtr.Zero)
            {
                if (isDirectDeviceDc) DeleteDC(srcDc);
                else ReleaseDC(IntPtr.Zero, srcDc);
            }
        }
    }

    public (byte[]? rawBgra, int curX, int curY, bool curVisible) CaptureRawBgraFrame(int displayIndex = -1, int targetWidth = 0, int targetHeight = 0, double zoom = 1.0)
    {
        EnsureInteractiveDesktop();

        var (screen, actualIndex) = ResolveScreen(displayIndex);
        int srcWidth = screen.Bounds.Width;
        int srcHeight = screen.Bounds.Height;
        int srcX = screen.Bounds.X;
        int srcY = screen.Bounds.Y;

        if (srcWidth <= 0 || srcHeight <= 0)
        {
            srcWidth = 2360;
            srcHeight = 1640;
        }

        double z = Math.Clamp(zoom, 1.0, 3.0);
        int captureW = (int)(srcWidth / z);
        int captureH = (int)(srcHeight / z);

        int dstWidth = (targetWidth > 0) ? targetWidth : 1180;
        int dstHeight = (targetHeight > 0) ? targetHeight : 820;

        // Preserve exact aspect ratio to eliminate non-uniform stretching
        double srcAspect = (double)captureW / captureH;
        if (srcAspect > 0)
        {
            dstHeight = (int)Math.Round(dstWidth / srcAspect);
        }

        // Ensure even pixel dimensions for hardware video encoders
        dstWidth = (dstWidth / 2) * 2;
        dstHeight = (dstHeight / 2) * 2;

        double scaleX = (double)dstWidth / captureW;
        double scaleY = (double)dstHeight / captureH;

        IntPtr srcDc = IntPtr.Zero;
        bool isDirectDeviceDc = false;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        int curRelX = -100;
        int curRelY = -100;
        bool curVisible = false;

        try
        {
            srcDc = CreateDC("DISPLAY", screen.DeviceName, null, IntPtr.Zero);
            if (srcDc != IntPtr.Zero) isDirectDeviceDc = true;
            else srcDc = GetDC(IntPtr.Zero);

            if (srcDc == IntPtr.Zero) return (null, 0, 0, false);

            memDc = CreateCompatibleDC(srcDc);
            hBitmap = CreateCompatibleBitmap(srcDc, dstWidth, dstHeight);
            oldObj = SelectObject(memDc, hBitmap);

            int copySrcX = isDirectDeviceDc ? 0 : srcX;
            int copySrcY = isDirectDeviceDc ? 0 : srcY;

            if (dstWidth == captureW && dstHeight == captureH)
            {
                BitBlt(memDc, 0, 0, dstWidth, dstHeight, srcDc, copySrcX, copySrcY, SRCCOPY);
            }
            else
            {
                SetStretchBltMode(memDc, COLORONCOLOR);
                StretchBlt(memDc, 0, 0, dstWidth, dstHeight, srcDc, copySrcX, copySrcY, captureW, captureH, SRCCOPY);
            }

            CURSORINFO ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
            {
                int rawRelX = ci.ptScreenPos.x - srcX;
                int rawRelY = ci.ptScreenPos.y - srcY;

                if (rawRelX >= -32 && rawRelX < captureW + 32 && rawRelY >= -32 && rawRelY < captureH + 32)
                {
                    curVisible = true;
                    ICONINFO ii = new ICONINFO();
                    int unscaledDrawX = rawRelX;
                    int unscaledDrawY = rawRelY;
                    if (GetIconInfo(ci.hCursor, out ii))
                    {
                        unscaledDrawX -= ii.xHotspot;
                        unscaledDrawY -= ii.yHotspot;
                        if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                        if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    }

                    int drawX = (int)(unscaledDrawX * scaleX);
                    int drawY = (int)(unscaledDrawY * scaleY);
                    curRelX = (int)(rawRelX * scaleX);
                    curRelY = (int)(rawRelY * scaleY);

                    int curW = (int)Math.Max(16, 32 * scaleX);
                    int curH = (int)Math.Max(16, 32 * scaleY);

                    DrawIconEx(memDc, drawX, drawY, ci.hCursor, curW, curH, 0, IntPtr.Zero, DI_NORMAL);
                }
            }

            SelectObject(memDc, oldObj);
            oldObj = IntPtr.Zero;

            using var bmp = Image.FromHbitmap(hBitmap);
            DeleteObject(hBitmap);
            hBitmap = IntPtr.Zero;

            var bmpData = bmp.LockBits(
                new Rectangle(0, 0, dstWidth, dstHeight),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);

            int byteCount = dstWidth * dstHeight * 4;
            var rawBytes = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, rawBytes, 0, byteCount);
            bmp.UnlockBits(bmpData);

            return (rawBytes, curRelX, curRelY, curVisible);
        }
        catch
        {
            return (null, 0, 0, false);
        }
        finally
        {
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (srcDc != IntPtr.Zero)
            {
                if (isDirectDeviceDc) DeleteDC(srcDc);
                else ReleaseDC(IntPtr.Zero, srcDc);
            }
        }
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        return codecs.FirstOrDefault(codec => codec.FormatID == format.Guid) ?? codecs[0];
    }
}
