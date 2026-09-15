using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using OpenWinSidecar.Service.Capture;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Regression tests for the capture/session failure modes that caused the multi-hour black
/// screen and the wedged/kicked-session bugs:
///  - only DXGI_ERROR_WAIT_TIMEOUT may be treated as "desktop unchanged";
///  - a frame dropped under backpressure must still trigger a full refresh, even if the
///    desktop goes static immediately afterwards;
///  - a forceidr / armed HEVC retry must reach the consumer on a static desktop;
///  - kicking a client must invoke the owning session's shutdown, never touch its buffers;
///  - partial dirty-region composes must update the changed region and preserve the rest.
/// </summary>
public class CaptureAndSinkTests
{
    // ------------------------------------------------------------------
    // DXGI result classification
    // ------------------------------------------------------------------

    [Fact]
    public void DxgiTimeout_OnlyWaitTimeoutIsBenign()
    {
        Assert.True(DxgiCaptureService.IsNormalCaptureTimeout(Vortice.DXGI.ResultCode.WaitTimeout));
        Assert.False(DxgiCaptureService.IsNormalCaptureTimeout(Vortice.DXGI.ResultCode.AccessLost));
        Assert.False(DxgiCaptureService.IsNormalCaptureTimeout(Vortice.DXGI.ResultCode.InvalidCall));
        Assert.False(DxgiCaptureService.IsNormalCaptureTimeout(Vortice.DXGI.ResultCode.DeviceRemoved));
        Assert.False(DxgiCaptureService.IsNormalCaptureTimeout(new SharpGen.Runtime.Result(0)));
    }

    // ------------------------------------------------------------------
    // Static-desktop recovery
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissedFrame_ForcesFullRefresh_EvenWhenDesktopGoesStatic()
    {
        using var h = new SinkHarness();
        var sink = h.Sink;
        sink.TargetWidth = 64;
        sink.TargetHeight = 48;
        sink.Zoom = 1;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var frame = new FrameHarness(64, 48, Color.FromArgb(200, 20, 20));

        // First compose (settings changed) — consume the signal and complete the handoff.
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 1000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);

        // Truly idle: unchanged bitmap, static cursor, no settings change -> must NOT signal.
        frame.Frame.Captured = false;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 2000));
        Assert.False(await sink.WaitFrameAsync(0, cts.Token)); // idle skip confirmed

        // Desktop changes while the sink is busy: the next tick is dropped.
        frame.Fill(Color.Blue);
        frame.Frame.Captured = true;
        frame.Frame.FullFrame = true;
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 3000));   // busy = 1
        Assert.False(sink.TryBeginCompose(frame.Frame, 0, 0, 4000)); // missed -> pending refresh
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));        // drain compose signal
        await sink.ProcessAndSendAsync(cts.Token);                   // busy = 0

        // The desktop is now static again and no pixels changed since the dropped tick — but the
        // owed full refresh must still reach the consumer or the client keeps the stale frame.
        frame.Frame.Captured = false;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 5000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token)); // recovery composed, not idle-skipped
        await sink.ProcessAndSendAsync(cts.Token);
    }

    [Fact]
    public async Task ForceIdr_OnHevcSession_ForcesComposeOnStaticDesktop()
    {
        using var h = new SinkHarness();
        var sink = h.Sink;
        sink.TargetWidth = 64;
        sink.TargetHeight = 48;
        sink.Codec = OpenWinSidecar.Service.Protocol.StreamCodec.HEVC;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var frame = new FrameHarness(64, 48, Color.Red);

        // Establish a composed-once state. If this host cannot start a HEVC encoder the sink
        // transparently falls back to JPEG for this send — either way the handoff completes.
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 1000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);

        sink.ForceIdr(); // queue a fresh IDR for the next frame

        frame.Frame.Captured = false;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();
        // The HEVC restart request is pending work: a static desktop must not idle-skip it.
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 2000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
    }

    [Fact]
    public async Task ForceIdr_OnJpegSession_DoesNotForceCompose()
    {
        using var h = new SinkHarness();
        var sink = h.Sink;
        sink.TargetWidth = 64;
        sink.TargetHeight = 48; // default codec is IntraTurbo (JPEG)

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var frame = new FrameHarness(64, 48, Color.Red);

        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 1000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);

        sink.ForceIdr(); // meaningless for a JPEG session; must not cause idle busy-work

        frame.Frame.Captured = false;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 2000));
        Assert.False(await sink.WaitFrameAsync(0, cts.Token));
    }

    // ------------------------------------------------------------------
    // Kick / session shutdown
    // ------------------------------------------------------------------

    [Fact]
    public void DisconnectByAddress_InvokesSessionShutdown_NotSinkDispose()
    {
        using var h = new SinkHarness(remoteAddress: "192.168.1.50:4444");
        bool requested = false;
        h.Sink.SetDisconnectHandler(() => requested = true);

        Assert.True(ClientFrameSink.DisconnectByAddress("192.168.1.50:4444"));
        Assert.True(requested);

        Assert.False(ClientFrameSink.DisconnectByAddress("192.168.1.51:4444"));
        Assert.False(ClientFrameSink.DisconnectByAddress(""));
    }

    [Fact]
    public void Kick_WhileFrameIsPending_DoesNotThrow()
    {
        using var h = new SinkHarness();
        h.Sink.TargetWidth = 32;
        h.Sink.TargetHeight = 24;
        h.Sink.SetDisconnectHandler(() => { });

        using var frame = new FrameHarness(32, 24, Color.Red);
        Assert.True(h.Sink.TryBeginCompose(frame.Frame, 0, 0, 1)); // busy = 1, never processed
        Assert.True(ClientFrameSink.DisconnectByAddress("10.0.0.1:5555"));
    }

    [Fact]
    public void SinkDispose_IsIdempotent()
    {
        var h = new SinkHarness();
        h.Sink.Dispose();
        h.Sink.Dispose(); // must not throw
        h.Dispose();
    }

    [Fact]
    public void StaleSink_WithOldLastActivity_TriggersDisconnect()
    {
        using var h = new SinkHarness(remoteAddress: "192.168.1.99:1234");
        bool requested = false;
        h.Sink.SetDisconnectHandler(() => requested = true);

        // Simulate a client that hasn't sent anything for 2 minutes.
        h.Sink.SetLastActivityForTest(DateTime.UtcNow.AddMinutes(-2));

        // Run the sweep manually (the Timer calls the same logic).
        h.Sink.ForceStaleSweepForTest();

        Assert.True(requested, "stale sink should have been disconnected");
    }

    [Fact]
    public void ActiveSink_WithRecentLastActivity_NotDisconnected()
    {
        using var h = new SinkHarness(remoteAddress: "192.168.1.100:5678");
        bool requested = false;
        h.Sink.SetDisconnectHandler(() => requested = true);

        // MarkActivity just now — sink is fresh.
        h.Sink.MarkActivity();

        h.Sink.ForceStaleSweepForTest();

        Assert.False(requested, "active sink should not have been disconnected");
    }

    // ------------------------------------------------------------------
    // Partial compose correctness
    // ------------------------------------------------------------------

    [Fact]
    public async Task PartialDirtyUpdate_ComposesChangedRegion_AndPreservesRest()
    {
        using var h = new SinkHarness();
        var sink = h.Sink;
        sink.TargetWidth = 64;
        sink.TargetHeight = 48;
        sink.Zoom = 1;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var frame = new FrameHarness(64, 48, Color.FromArgb(200, 20, 20));

        // Full frame first.
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 1000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);
        using (var full = await ReadJpegAsync(h.Client.GetStream(), cts.Token))
        {
            AssertColor(Color.FromArgb(200, 20, 20), full.GetPixel(32, 24), 40);
        }

        // Capture updates only the top-left quadrant (25% coverage -> partial compose path).
        frame.FillRect(0, 0, 32, 24, Color.Lime);
        frame.Frame.Captured = true;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();
        frame.Frame.DirtyRects.Add(new Rectangle(0, 0, 32, 24));

        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 2000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);
        using (var partial = await ReadJpegAsync(h.Client.GetStream(), cts.Token))
        {
            AssertColor(Color.Lime, partial.GetPixel(8, 8), 40);                      // updated region
            AssertColor(Color.FromArgb(200, 20, 20), partial.GetPixel(56, 40), 40);   // untouched region
        }
    }

    // ------------------------------------------------------------------
    // GDI fallback contract
    // ------------------------------------------------------------------

    [Fact]
    public void GdiCapture_SetsFullFrameAndClearsDirtyRects()
    {
        var gdi = new ScreenCaptureService();
        using var frameHarness = new FrameHarness(2, 2, Color.Black);
        frameHarness.Frame.DirtyRects.Add(new Rectangle(0, 0, 1, 1));
        frameHarness.Frame.FullFrame = false;

        bool ok = gdi.CaptureNativeFrame(0, frameHarness.Frame);
        Assert.True(ok, "GDI capture failed — the test host may not have an interactive desktop.");
        Assert.True(frameHarness.Frame.FullFrame);
        Assert.Empty(frameHarness.Frame.DirtyRects);
        Assert.True(frameHarness.Frame.Captured);
        Assert.NotNull(frameHarness.Frame.Bitmap);
    }

    [Fact]
    public void NativeFrame_InitialProperties_AreConsistent()
    {
        var frame = new NativeFrame();
        Assert.Null(frame.Bitmap);
        Assert.Null(frame.RawBuffer);
        Assert.Equal(0, frame.Stride);
        Assert.False(frame.Captured);
        Assert.False(frame.FullFrame);
        Assert.Empty(frame.DirtyRects);
    }

    // ------------------------------------------------------------------
    // Test plumbing
    // ------------------------------------------------------------------

    private static (TcpClient server, TcpClient client) SocketPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var server = listener.AcceptTcpClient();
        listener.Stop();
        server.NoDelay = true;
        client.NoDelay = true;
        return (server, client);
    }

    private static async Task<Bitmap> ReadJpegAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, token);
        Assert.Equal(0x82, header[0]);
        long len = header[1] & 0x7F;
        if (len == 126)
        {
            var ext = new byte[2];
            await stream.ReadExactlyAsync(ext, token);
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            await stream.ReadExactlyAsync(ext, token);
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }

        var packet = new byte[len];
        await stream.ReadExactlyAsync(packet, token);
        Assert.Equal(0, packet[0]);              // IntraTurbo codec byte
        Assert.Equal(0xFF, packet[15]);          // JPEG SOI
        Assert.Equal(0xD8, packet[16]);
        using var ms = new MemoryStream(packet, 15, (int)len - 15);
        return new Bitmap(ms);
    }

    private static void AssertColor(Color expected, Color actual, int tolerance)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance &&
            Math.Abs(expected.G - actual.G) <= tolerance &&
            Math.Abs(expected.B - actual.B) <= tolerance,
            $"Expected {expected} (±{tolerance}) but got {actual}.");
    }

    /// <summary>A connected sink talking to a loopback socket, with the hub torn down on dispose.</summary>
    private sealed class SinkHarness : IDisposable
    {
        public FrameBroadcastHub Hub { get; }
        public ClientFrameSink Sink { get; }
        public TcpClient Server { get; }
        public TcpClient Client { get; }

        public SinkHarness(string remoteAddress = "10.0.0.1:5555", string device = "TEST-DISPLAY")
        {
            Hub = new FrameBroadcastHub();
            (Server, Client) = SocketPair();
            Sink = Hub.CreateSink(Server.GetStream(), new SemaphoreSlim(1, 1), device, remoteAddress);
        }

        public void Dispose()
        {
            try { Sink.Dispose(); } catch { }
            try { Hub.Dispose(); } catch { }
            try { Server.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    /// <summary>A native-frame bitmap wrapper that disposes its bitmap.</summary>
    private sealed class FrameHarness : IDisposable
    {
        public NativeFrame Frame { get; }

        public FrameHarness(int width, int height, Color color)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.Clear(color);
            Frame = new NativeFrame
            {
                Bitmap = bmp,
                RawBuffer = null,
                Width = width,
                Height = height,
                Captured = true,
                FullFrame = true,
            };
        }

        public void Fill(Color color)
        {
            using var g = Graphics.FromImage(Frame.Bitmap);
            g.Clear(color);
        }

        public void FillRect(int x, int y, int w, int h, Color color)
        {
            using var g = Graphics.FromImage(Frame.Bitmap);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, x, y, w, h);
        }

        public void Dispose() => Frame.Bitmap?.Dispose();
    }
}
