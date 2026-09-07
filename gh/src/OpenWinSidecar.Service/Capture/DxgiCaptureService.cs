using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace OpenWinSidecar.Service.Capture;

/// <summary>
/// High-performance screen capture using DXGI Desktop Duplication API.
/// Captures directly from GPU VRAM — 10-50x faster than GDI BitBlt.
/// Properly handles virtual IddCx displays (e.g. DISPLAY85).
/// </summary>
public sealed class DxgiCaptureService : IDisposable
{
    private static readonly ImageCodecInfo JpegEncoder = GetJpegEncoder();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private string _currentDeviceName = "";
    private int _captureWidth;
    private int _captureHeight;
    private bool _disposed;
    private byte[]? _lastFrameBytes;
    private int _lastCurX, _lastCurY;
    private bool _lastCurVisible;

    // Cursor P/Invoke
    private const int CURSOR_SHOWING = 0x00000001;

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

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(out CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    private bool _dpiSet;

    /// <summary>
    /// Finds the DXGI adapter and output for a given device name.
    /// </summary>
    private (IDXGIAdapter1? adapter, IDXGIOutput? output) FindOutputForScreen(IDXGIFactory1 factory, string deviceName)
    {
        for (uint ai = 0; ; ai++)
        {
            var adapterResult = factory.EnumAdapters1(ai, out var adapter);
            if (adapterResult.Failure || adapter == null) break;

            for (uint oi = 0; ; oi++)
            {
                var outputResult = adapter.EnumOutputs(oi, out var output);
                if (outputResult.Failure || output == null) break;

                var desc = output.Description;
                if (desc.DeviceName.TrimEnd('\0') == deviceName)
                {
                    return (adapter, output);
                }
                output.Dispose();
            }

            adapter.Dispose();
        }

        return (null, null);
    }

    private bool _initFailLogged;

    /// <summary>
    /// Initialize or reinitialize DXGI Desktop Duplication for a specific display.
    /// </summary>
    private bool InitializeDuplication(string deviceName)
    {
        ReleaseDuplication();

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var (adapter, output) = FindOutputForScreen(factory, deviceName);

            if (adapter == null || output == null)
            {
                adapter?.Dispose();
                output?.Dispose();
                return false;
            }

            using (adapter)
            using (output)
            {
                var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out _device,
                    out _context);

                if (result.Failure || _device == null)
                    return false;

                using var output1 = output.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);

                var dupDesc = _duplication.Description;
                _captureWidth = (int)dupDesc.ModeDescription.Width;
                _captureHeight = (int)dupDesc.ModeDescription.Height;

                var texDesc = new Texture2DDescription
                {
                    Width = dupDesc.ModeDescription.Width,
                    Height = dupDesc.ModeDescription.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                };

                _stagingTexture = _device.CreateTexture2D(texDesc);
                _currentDeviceName = deviceName;

                Console.WriteLine($"[DXGI] Desktop Duplication initialized: {_captureWidth}x{_captureHeight} for {deviceName}");
                _initFailLogged = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!_initFailLogged)
            {
                Console.WriteLine($"[DXGI] Init failed: {ex.Message}");
                _initFailLogged = true;
            }
            ReleaseDuplication();
            return false;
        }
    }

    private void ReleaseDuplication()
    {
        _stagingTexture?.Dispose(); _stagingTexture = null;
        _duplication?.Dispose(); _duplication = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _currentDeviceName = "";
    }

    /// <summary>
    /// Capture a frame using DXGI Desktop Duplication.
    /// Returns JPEG bytes + cursor position, or cached previous frame on timeout.
    /// </summary>
    public (byte[]? frameData, int curX, int curY, bool curVisible) CaptureFrame(
        string deviceName,
        int screenX, int screenY,
        int screenWidth, int screenHeight,
        int targetWidth = 0, int targetHeight = 0,
        long jpegQuality = 70,
        bool drawCursor = true)
    {
        if (!_dpiSet) { SetProcessDPIAware(); _dpiSet = true; }

        // Init duplication for this screen if needed
        if (_currentDeviceName != deviceName)
        {
            if (!InitializeDuplication(deviceName))
                return (null, 0, 0, false);
        }

        if (_duplication == null || _device == null || _context == null || _stagingTexture == null)
            return (null, 0, 0, false);

        IDXGIResource? desktopResource = null;
        try
        {
            var acquireResult = _duplication.AcquireNextFrame(16, out var frameInfo, out desktopResource);

            if (acquireResult.Failure || desktopResource == null)
            {
                // DXGI_ERROR_WAIT_TIMEOUT — no desktop change since last frame
                // Return cached frame with updated cursor position
                return GetCursorOnly(screenX, screenY, screenWidth, screenHeight);
            }

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            desktopResource.Dispose();
            desktopResource = null;

            _context.CopyResource(_stagingTexture, desktopTexture);
            _duplication.ReleaseFrame();

            // Map staging texture to CPU memory
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);

            try
            {
                int width = _captureWidth;
                int height = _captureHeight;

                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer;
                    byte* dstPtr = (byte*)bmpData.Scan0;
                    int copyBytes = Math.Min(bmpData.Stride, (int)mapped.RowPitch);

                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + y * (long)mapped.RowPitch,
                            dstPtr + y * (long)bmpData.Stride,
                            bmpData.Stride,
                            copyBytes);
                    }
                }

                bmp.UnlockBits(bmpData);

                // Get cursor position & draw cursor
                int curRelX = -100, curRelY = -100;
                bool curVisible = false;

                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (drawCursor && GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                {
                    int rawRelX = ci.ptScreenPos.x - screenX;
                    int rawRelY = ci.ptScreenPos.y - screenY;

                    if (rawRelX >= -32 && rawRelX < screenWidth + 32 && rawRelY >= -32 && rawRelY < screenHeight + 32)
                    {
                        curVisible = true;
                        curRelX = rawRelX;
                        curRelY = rawRelY;

                        using var g = Graphics.FromImage(bmp);
                        var hdc = g.GetHdc();
                        try
                        {
                            var ii = new ICONINFO();
                            int drawX = rawRelX;
                            int drawY = rawRelY;
                            if (GetIconInfo(ci.hCursor, out ii))
                            {
                                drawX -= ii.xHotspot;
                                drawY -= ii.yHotspot;
                                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                            }

                            DrawIconEx(hdc, drawX, drawY, ci.hCursor, 32, 32, 0, IntPtr.Zero, 0x0003);
                        }
                        finally
                        {
                            g.ReleaseHdc(hdc);
                        }
                    }
                }

                // Scale down if requested
                Bitmap finalBmp = bmp;
                bool mustDisposeFinal = false;

                if (targetWidth > 0 && targetHeight > 0 && (targetWidth != width || targetHeight != height))
                {
                    var scaled = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(scaled);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, targetWidth, targetHeight);
                    finalBmp = scaled;
                    mustDisposeFinal = true;

                    curRelX = (int)((double)curRelX * targetWidth / width);
                    curRelY = (int)((double)curRelY * targetHeight / height);
                }

                using var ms = new MemoryStream(65536);
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(jpegQuality, 10, 95));
                finalBmp.Save(ms, JpegEncoder, encParams);

                if (mustDisposeFinal) finalBmp.Dispose();

                var frameBytes = ms.ToArray();
                _lastFrameBytes = frameBytes;
                _lastCurX = curRelX;
                _lastCurY = curRelY;
                _lastCurVisible = curVisible;

                return (frameBytes, curRelX, curRelY, curVisible);
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == unchecked((int)0x887A0026))
        {
            // DXGI_ERROR_ACCESS_LOST — mode change or UAC prompt
            Console.WriteLine("[DXGI] Access lost — reinitializing...");
            ReleaseDuplication();
            return (null, 0, 0, false);
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == unchecked((int)0x887A0001))
        {
            // DXGI_ERROR_INVALID_CALL — duplication lost
            Console.WriteLine("[DXGI] Invalid call — reinitializing...");
            ReleaseDuplication();
            return (null, 0, 0, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DXGI] Capture error: {ex.Message}");
            return (null, 0, 0, false);
        }
        finally
        {
            desktopResource?.Dispose();
        }
    }

    private byte[]? _reusableBgraBuffer;
    private Bitmap? _nativeBitmap;

    /// <summary>
    /// Captures the display at its native duplication resolution into a reusable Bitmap for
    /// the broadcast producer. The cursor is never composited — its screen-space position and
    /// handle are returned so each subscriber can stamp its own cursor state.
    /// AcquireNextFrame uses a 0ms timeout: on DXGI_ERROR_WAIT_TIMEOUT the previous bitmap
    /// content is kept (frame.Captured = false) and only cursor info is refreshed.
    /// Returns false only on hard failure (duplication unavailable / access lost).
    /// </summary>
    public bool CaptureNativeFrame(string deviceName, NativeFrame frame)
    {
        if (!_dpiSet) { SetProcessDPIAware(); _dpiSet = true; }

        if (_currentDeviceName != deviceName && !InitializeDuplication(deviceName))
            return false;

        if (_duplication == null || _device == null || _context == null || _stagingTexture == null)
            return false;

        CursorInterop.FillFrameCursor(frame);
        frame.Captured = false;

        IDXGIResource? desktopResource = null;
        try
        {
            var acquireResult = _duplication.AcquireNextFrame(0, out _, out desktopResource);
            if (acquireResult.Failure || desktopResource == null)
                return true; // no new desktop update — reuse previous bitmap content.
                             // (A static desktop never presents at all; the producer seeds
                             //  the first frame via GDI in that case.)

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            desktopResource.Dispose();
            desktopResource = null;

            _context.CopyResource(_stagingTexture, desktopTexture);
            _duplication.ReleaseFrame();

            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                int width = _captureWidth;
                int height = _captureHeight;

                if (_nativeBitmap == null || _nativeBitmap.Width != width || _nativeBitmap.Height != height)
                {
                    _nativeBitmap?.Dispose();
                    _nativeBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                }

                var bmpData = _nativeBitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer;
                    byte* dstPtr = (byte*)bmpData.Scan0;
                    int copyBytes = Math.Min(bmpData.Stride, (int)mapped.RowPitch);

                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + y * (long)mapped.RowPitch,
                            dstPtr + y * (long)bmpData.Stride,
                            bmpData.Stride,
                            copyBytes);
                    }
                }

                _nativeBitmap.UnlockBits(bmpData);

                frame.Bitmap = _nativeBitmap;
                frame.Width = width;
                frame.Height = height;
                frame.Captured = true;
                return true;
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == unchecked((int)0x887A0026) || ex.HResult == unchecked((int)0x887A0001))
        {
            // DXGI_ERROR_ACCESS_LOST / DXGI_ERROR_INVALID_CALL — mode change or UAC prompt.
            // Drop the stale bitmap too: the producer's GDI seed only re-arms when
            // Bitmap == null, and a stale pre-mode-change bitmap would block re-seeding
            // (the frozen-after-mode-change bug).
            ReleaseDuplication();
            frame.Bitmap = null!;
            frame.Width = 0;
            frame.Height = 0;
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
        }
    }

    /// <summary>
    /// Fast Direct3D11 / DXGI Desktop Duplication capture of raw BGRA bytes for hardware HEVC encoder.
    /// Bypasses Bitmap, GDI, and JPEG encoding for ultra-low latency (~0.3ms).
    /// </summary>
    public (byte[]? rawBgra, int curX, int curY, bool curVisible) CaptureRawBgraFrame(
        string deviceName,
        int screenX, int screenY,
        int screenWidth, int screenHeight,
        int targetWidth = 0, int targetHeight = 0,
        double zoom = 1.0)
    {
        if (!_dpiSet) { SetProcessDPIAware(); _dpiSet = true; }

        if (_currentDeviceName != deviceName)
        {
            if (!InitializeDuplication(deviceName))
                return (null, 0, 0, false);
        }

        if (_duplication == null || _device == null || _context == null || _stagingTexture == null)
            return (null, 0, 0, false);

        IDXGIResource? desktopResource = null;
        try
        {
            var acquireResult = _duplication.AcquireNextFrame(16, out var frameInfo, out desktopResource);
            if (acquireResult.Failure || desktopResource == null)
            {
                if (_reusableBgraBuffer != null)
                {
                    return (_reusableBgraBuffer, _lastCurX, _lastCurY, _lastCurVisible);
                }
                return (null, 0, 0, false);
            }

            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            desktopResource.Dispose();
            desktopResource = null;

            _context.CopyResource(_stagingTexture, desktopTexture);
            _duplication.ReleaseFrame();

            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                int width = _captureWidth;
                int height = _captureHeight;
                int bufferSize = width * height * 4;

                if (_reusableBgraBuffer == null || _reusableBgraBuffer.Length != bufferSize)
                {
                    _reusableBgraBuffer = new byte[bufferSize];
                }

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer;
                    fixed (byte* dstPtr = _reusableBgraBuffer)
                    {
                        int rowBytes = width * 4;
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + y * (long)mapped.RowPitch,
                                dstPtr + y * (long)rowBytes,
                                rowBytes,
                                rowBytes);
                        }
                    }
                }

                return (_reusableBgraBuffer, 0, 0, false);
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == unchecked((int)0x887A0026) || ex.HResult == unchecked((int)0x887A0001))
        {
            ReleaseDuplication();
            return (null, 0, 0, false);
        }
        catch
        {
            return (null, 0, 0, false);
        }
        finally
        {
            desktopResource?.Dispose();
        }
    }

    /// <summary>
    /// Returns cached frame bytes with fresh cursor position (used when DXGI reports no desktop change).
    /// </summary>
    private (byte[]? frameData, int curX, int curY, bool curVisible) GetCursorOnly(int screenX, int screenY, int screenWidth, int screenHeight)
    {
        int curRelX = _lastCurX;
        int curRelY = _lastCurY;
        bool curVisible = _lastCurVisible;

        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0)
        {
            curRelX = ci.ptScreenPos.x - screenX;
            curRelY = ci.ptScreenPos.y - screenY;
            curVisible = curRelX >= -32 && curRelX < screenWidth + 32 && curRelY >= -32 && curRelY < screenHeight + 32;
        }

        return (_lastFrameBytes, curRelX, curRelY, curVisible);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDuplication();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid)
            ?? ImageCodecInfo.GetImageEncoders()[0];
    }
}
