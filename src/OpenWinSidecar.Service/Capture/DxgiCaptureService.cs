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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private string _currentDeviceName = "";
    private int _captureWidth;
    private int _captureHeight;
    private bool _disposed;

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    private bool _dpiSet;

    /// <summary>
    /// True only for DXGI_ERROR_WAIT_TIMEOUT — "the desktop has not changed" — which is the sole
    /// benign acquisition result. Every other failure (ACCESS_LOST, device reset, invalid call,
    /// E_ACCESSDENIED) means the duplication object is dead and must be recreated; treating those
    /// as timeouts kept serving a stale black frame forever (the multi-hour black-screen bug).
    /// </summary>
    internal static bool IsNormalCaptureTimeout(SharpGen.Runtime.Result result)
        => result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code;

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
                _needsFullRefresh = true;

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
        _nativeBitmap?.Dispose(); _nativeBitmap = null;
        if (_rawNativePin.IsAllocated) _rawNativePin.Free();
        _rawNativeBuffer = null;
        _currentDeviceName = "";
    }

    private byte[]? _rawNativeBuffer;
    private GCHandle _rawNativePin;
    private Bitmap? _nativeBitmap;
    private int _failLoggedCount;

    // Present-metadata scratch (producer thread only) and a flag forcing the next successful
    // capture to refresh the whole bitmap (new duplication object, resized bitmap).
    private readonly Vortice.RawRect[] _dirtyScratch = new Vortice.RawRect[64];
    private readonly OutduplMoveRect[] _moveScratch = new OutduplMoveRect[64];
    private bool _needsFullRefresh = true;

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
        frame.DirtyRects.Clear();
        frame.FullFrame = false;

        IDXGIResource? desktopResource = null;
        try
        {
            var acquireResult = _duplication.AcquireNextFrame(0, out var frameInfo, out desktopResource);
            if (IsNormalCaptureTimeout(acquireResult))
            {
                // Normal timeout: no new desktop update this tick — keep previous bitmap content
                return true;
            }

            if (acquireResult.Failure || desktopResource == null)
            {
                // Hard failure: ACCESS_LOST, mode change, device reset, or permission loss.
                if (_failLoggedCount < 5)
                {
                    _failLoggedCount++;
                    Console.WriteLine($"[DXGI] AcquireNextFrame failed on {_currentDeviceName}: {acquireResult.Code} (0x{acquireResult.Code:X8})");
                }
                ReleaseDuplication();
                _needsFullRefresh = true;
                frame.Bitmap = null!;
                frame.RawBuffer = null;
                frame.Width = 0;
                frame.Height = 0;
                return false;
            }

            // Read the present metadata BEFORE ReleaseFrame. Copy only dirty regions; any
            // ambiguity (move rects, rect overflow, metadata errors, empty region list) falls
            // back to a full copy — correctness over savings.
            bool full = _needsFullRefresh;
            _needsFullRefresh = false;

            if (!full)
            {
                try
                {
                    int rectSize = Marshal.SizeOf<Vortice.RawRect>();
                    _duplication.GetFrameDirtyRects((uint)(_dirtyScratch.Length * rectSize), _dirtyScratch, out uint dirtyRequired);
                    int dirtyCount = (int)Math.Min(dirtyRequired / (uint)rectSize, (uint)_dirtyScratch.Length);
                    if (dirtyRequired > (uint)(_dirtyScratch.Length * rectSize))
                    {
                        full = true;
                    }
                    else
                    {
                        for (int i = 0; i < dirtyCount; i++)
                        {
                            var r = _dirtyScratch[i];
                            int x0 = Math.Clamp(r.Left, 0, _captureWidth);
                            int y0 = Math.Clamp(r.Top, 0, _captureHeight);
                            int x1 = Math.Clamp(r.Right, 0, _captureWidth);
                            int y1 = Math.Clamp(r.Bottom, 0, _captureHeight);
                            if (x1 > x0 && y1 > y0)
                                frame.DirtyRects.Add(new Rectangle(x0, y0, x1 - x0, y1 - y0));
                        }

                        int moveSize = Marshal.SizeOf<OutduplMoveRect>();
                        _duplication.GetFrameMoveRects((uint)(_moveScratch.Length * moveSize), _moveScratch, out uint moveRequired);
                        if (moveRequired > 0) full = true;
                    }
                }
                catch
                {
                    full = true;
                }
            }

            if (!full && frame.DirtyRects.Count == 0)
                full = true;

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
                int stride = width * 4;
                int bufferSize = stride * height;

                if (_rawNativeBuffer == null || _rawNativeBuffer.Length != bufferSize || _nativeBitmap == null)
                {
                    if (_rawNativePin.IsAllocated) _rawNativePin.Free();
                    _nativeBitmap?.Dispose();
                    _rawNativeBuffer = new byte[bufferSize];
                    _rawNativePin = GCHandle.Alloc(_rawNativeBuffer, GCHandleType.Pinned);
                    _nativeBitmap = new Bitmap(width, height, stride, PixelFormat.Format32bppArgb, _rawNativePin.AddrOfPinnedObject());
                    full = true;
                    frame.DirtyRects.Clear();
                }

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer;
                    fixed (byte* dstPtr = _rawNativeBuffer)
                    {
                        if (full)
                        {
                            int copyBytes = Math.Min(stride, (int)mapped.RowPitch);
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(
                                    srcPtr + y * (long)mapped.RowPitch,
                                    dstPtr + y * (long)stride,
                                    stride,
                                    copyBytes);
                            }
                            frame.DirtyRects.Clear();
                        }
                        else
                        {
                            foreach (var r in frame.DirtyRects)
                            {
                                int rowBytes = r.Width * 4;
                                for (int y = 0; y < r.Height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        srcPtr + (long)(r.Y + y) * mapped.RowPitch + (long)r.X * 4,
                                        dstPtr + (long)(r.Y + y) * stride + (long)r.X * 4,
                                        rowBytes,
                                        rowBytes);
                                }
                            }
                        }
                    }
                }

                frame.Bitmap = _nativeBitmap;
                frame.RawBuffer = _rawNativeBuffer;
                frame.Stride = stride;
                frame.Width = width;
                frame.Height = height;
                frame.FullFrame = full;
                frame.Captured = true;
                return true;
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
        catch (Exception ex)
        {
            // DXGI_ERROR_ACCESS_LOST / DXGI_ERROR_INVALID_CALL / E_ACCESSDENIED — mode change, resolution change, or UAC prompt.
            if (_failLoggedCount < 5)
            {
                _failLoggedCount++;
                Console.WriteLine($"[DXGI] CaptureNativeFrame failed: {ex.GetType().Name} - {ex.Message} (0x{ex.HResult:X8})");
            }
            ReleaseDuplication();
            frame.Bitmap = null!;
            frame.Width = 0;
            frame.Height = 0;
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
        }
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDuplication();
    }
}
