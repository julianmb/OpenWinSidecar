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
    [Fact]
    public void StreamFramerate_DefaultsTo60_AndChangesIndependentlyOfResolution()
    {
        using var h = new SinkHarness();
        Assert.Equal(60, h.Sink.TargetFramerate);
        int width = h.Sink.TargetWidth, height = h.Sink.TargetHeight;
        string device = h.Sink.DeviceName;

        foreach (int fps in new[] { 60, 30 })
        {
            Assert.True(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TrySetStreamFramerate(h.Sink, $"fps:{fps}"));
            Assert.Equal(fps, h.Sink.TargetFramerate);
            Assert.Equal(width, h.Sink.TargetWidth);
            Assert.Equal(height, h.Sink.TargetHeight);
            Assert.Equal(device, h.Sink.DeviceName);
        }
    }

    [Theory]
    [InlineData("fps:120")]
    [InlineData("fps:0")]
    [InlineData("fps:-30")]
    [InlineData("fps:31")]
    [InlineData("fps:")]
    [InlineData("fps:abc")]
    [InlineData("fps:30.0")]
    [InlineData("fps:60,120")]
    [InlineData("fps: 60")]
    [InlineData("fps:60000000000000000000")]
    [InlineData("set_res:2360,1640,120")]
    public void StreamFramerate_InvalidCommandPreservesSelection(string command)
    {
        using var h = new SinkHarness();
        foreach (int fps in new[] { 30, 60 })
        {
            h.Sink.TargetFramerate = fps;
            Assert.False(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TrySetStreamFramerate(h.Sink, command));
            Assert.Equal(fps, h.Sink.TargetFramerate);
        }
    }

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
    public async Task HevcKeepAlive_ComposesOnStaticDesktop_AfterFeedInterval()
    {
        // The QSV wake-up stall lands on the first motion frame after an idle stretch.
        // A periodic keep-alive push of the unchanged bitmap keeps the encoder hot, so
        // the static-desktop idle skip must release a compose on the keep-alive cadence.
        using var h = new SinkHarness();
        var sink = h.Sink;
        sink.TargetWidth = 64;
        sink.TargetHeight = 48;
        sink.Codec = OpenWinSidecar.Service.Protocol.StreamCodec.HEVC;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var frame = new FrameHarness(64, 48, Color.Red);

        // First compose sends the frame (and a successful HEVC push stamps the feed time;
        // JPEG fallback also counts — the goal is just a compose signal while static).
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 1000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);

        frame.Frame.Captured = false;
        frame.Frame.FullFrame = false;
        frame.Frame.DirtyRects.Clear();

        // Immediately after the last feed: still idle-skipped.
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 2000));
        Assert.False(await sink.WaitFrameAsync(0, cts.Token));

        // After the keep-alive interval the unchanged frame must be composed and sent
        // (encoder keeps warm) instead of skipped.
        await Task.Delay(600, cts.Token);
        Assert.True(sink.TryBeginCompose(frame.Frame, 0, 0, 3000));
        Assert.True(await sink.WaitFrameAsync(0, cts.Token));
        await sink.ProcessAndSendAsync(cts.Token);
    }

    [Fact]
    public void CalmUpgradeHold_PreventsScaleFlipOnBriefStillness()
    {
        // The half->full upgrade costs an encoder respawn + IDR burst; it must not fire
        // on momentary stillness, only after the hold period.
        Assert.True(ClientFrameSink.CalmUpgradeHoldMs >= 2000,
            "calm-upgrade hold below 2s lets touch-pause cycles respawn the encoder");
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
    // HEVC backpressure: manually released stages, no encoder or timing sleeps
    // ------------------------------------------------------------------

    private static TaskCompletionSource SendGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task HevcSend_PacedAndOrderedWaitersCount_AndAllPacketsDrainInOrder()
    {
        using var h = new SinkHarness();
        var pace = SendGate();
        var wire = SendGate();
        var atWire = SendGate();
        var sent = new List<int>();
        Assert.Equal(0, h.Sink.HevcSendsInFlight);
        await h.Sink.WaitForHevcSendCapacityAsync(CancellationToken.None);

        var first = h.Sink.QueueHevcSendAsync(async () =>
        {
            await pace.Task;
            atWire.SetResult();
            await wire.Task;
            sent.Add(1);
        });
        Assert.Equal(1, h.Sink.HevcSendsInFlight);
        // One outstanding AU still allows hardware encode/network pipelining.
        Assert.True(h.Sink.WaitForHevcSendCapacityAsync(CancellationToken.None).IsCompletedSuccessfully);
        var second = h.Sink.QueueHevcSendAsync(() => { sent.Add(2); return Task.CompletedTask; });
        var third = h.Sink.QueueHevcSendAsync(() => { sent.Add(3); return Task.CompletedTask; });
        // An already-buffered encoder burst can exceed the watermark, but none is hidden/dropped.
        Assert.Equal(3, h.Sink.HevcSendsInFlight);
        var capacity = h.Sink.WaitForHevcSendCapacityAsync(CancellationToken.None);
        Assert.False(capacity.IsCompleted);
        Assert.Empty(sent);

        pace.SetResult();
        await atWire.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, h.Sink.HevcSendsInFlight);
        Assert.False(capacity.IsCompleted);
        Assert.Empty(sent);

        wire.SetResult();
        await Task.WhenAll(first, second, third, capacity).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2, 3 }, sent);
        Assert.Equal(0, h.Sink.HevcSendsInFlight);
    }

    [Fact]
    public async Task HevcSend_CancellingRawInputWait_DoesNotCancelOrDropEncodedPackets()
    {
        using var h = new SinkHarness();
        using var cts = new CancellationTokenSource();
        var pace = SendGate();
        var sent = new List<int>();
        var first = h.Sink.QueueHevcSendAsync(async () => { await pace.Task; sent.Add(1); });
        var second = h.Sink.QueueHevcSendAsync(() => { sent.Add(2); return Task.CompletedTask; });
        var capacity = h.Sink.WaitForHevcSendCapacityAsync(cts.Token);
        Assert.False(capacity.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capacity);
        Assert.Equal(2, h.Sink.HevcSendsInFlight);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        pace.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, sent);
        Assert.Equal(0, h.Sink.HevcSendsInFlight);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Sink.WaitForHevcSendCapacityAsync(cts.Token));
    }

    [Theory]
    [InlineData("sync-error")]
    [InlineData("async-error")]
    [InlineData("cancelled")]
    public async Task HevcSend_FailureBalancesCount_AndDoesNotWedgeFollowingSend(string failure)
    {
        using var h = new SinkHarness();
        var gate = SendGate();
        var failed = h.Sink.QueueHevcSendAsync(() => failure == "sync-error"
            ? throw new IOException("synchronous send failure")
            : gate.Task);
        bool followingSent = false;
        var following = h.Sink.QueueHevcSendAsync(() =>
        {
            followingSent = true;
            return Task.CompletedTask;
        });
        if (failure != "sync-error")
        {
            Assert.Equal(2, h.Sink.HevcSendsInFlight);
            Assert.False(followingSent);
            if (failure == "cancelled") gate.SetCanceled();
            else gate.SetException(new IOException("asynchronous send failure"));
        }
        await Task.WhenAll(failed, following).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(followingSent);
        Assert.Equal(0, h.Sink.HevcSendsInFlight);
        Assert.True(h.Sink.WaitForHevcSendCapacityAsync(CancellationToken.None).IsCompletedSuccessfully);
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
    // Dashboard telemetry freshness (fixed clocks, no sleeps)
    // ------------------------------------------------------------------

    private static readonly DateTime StatsTime = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static ClientFrameSink.ConnectedClientSnapshot Telemetry(SinkHarness h, DateTime now)
        => Assert.Single(ClientFrameSink.Snapshot(now), c => c.RemoteAddress == h.Sink.RemoteAddress);

    [Fact]
    public void Stats_ExpireIndependentlyOfConnectionActivity_At15Seconds()
    {
        using var h = new SinkHarness();
        h.Sink.UpdateClientStats(60, 8000, 42, nowUtc: StatsTime);
        h.Sink.MarkActivity(); // sync traffic keeps the connection alive, not the stats

        Assert.True(Telemetry(h, StatsTime.AddMilliseconds(14999)).StatsFresh);
        var stale = Telemetry(h, StatsTime.AddSeconds(15));
        Assert.False(stale.StatsFresh);
        Assert.False(stale.MetricsFresh);
        Assert.Equal("Stale", stale.FpsText);
        Assert.Equal("—", stale.LatencyText);
        Assert.Equal(0, stale.Fps);
        Assert.Equal(0, stale.Kbps);
        Assert.Equal(0, stale.LatencyMs);
        Assert.Equal(StatsTime, h.Sink.LastStatsUtc);
    }

    [Fact]
    public void Stats_FrameAgeExpiresAt10Seconds_EvenBetweenReports()
    {
        using var h = new SinkHarness();
        h.Sink.UpdateClientStats(60, 8000, 42, visible: true, frameAgeMs: 9000, nowUtc: StatsTime);
        var fresh = Telemetry(h, StatsTime.AddMilliseconds(999));
        Assert.True(fresh.MetricsFresh);
        Assert.Equal("60 fps", fresh.FpsText);
        Assert.Equal("42 ms", fresh.LatencyText);

        var idle = Telemetry(h, StatsTime.AddSeconds(1));
        Assert.True(idle.StatsFresh);
        Assert.False(idle.MetricsFresh);
        Assert.Equal("No recent frames", idle.FpsText);
        Assert.Equal("—", idle.LatencyText);
        Assert.Equal(0, idle.Fps);
        Assert.Equal(0, idle.LatencyMs);
        Assert.Equal(10000d, idle.FrameAgeMs);
    }

    [Fact]
    public void Stats_NullLatencyClearsPreviousSample_AndUpdatesOtherMetrics()
    {
        using var h = new SinkHarness();
        h.Sink.UpdateClientStats(60, 8000, 42, nowUtc: StatsTime);
        Assert.True(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TryUpdateClientStats(h.Sink,
            "{\"fps\":30,\"kbps\":4000,\"latencyMs\":null,\"visible\":true,\"frameAgeMs\":1.5}", StatsTime.AddSeconds(1)));
        var current = Telemetry(h, StatsTime.AddSeconds(1));
        Assert.True(current.MetricsFresh);
        Assert.Equal(30, current.Fps);
        Assert.Equal(4000, current.Kbps);
        Assert.Equal(0, current.LatencyMs);
        Assert.Equal("—", current.LatencyText);
        Assert.Equal(StatsTime.AddSeconds(1), h.Sink.LastStatsUtc);
    }

    [Theory]
    [InlineData("false", "0", "Hidden")]
    [InlineData("true", "null", "No recent frames")]
    [InlineData("true", "10000", "No recent frames")]
    public void Stats_HiddenOrNoPaint_SuppressMetrics(string visible, string frameAge, string status)
    {
        using var h = new SinkHarness();
        Assert.True(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TryUpdateClientStats(h.Sink,
            $"{{\"fps\":60,\"kbps\":8000,\"latencyMs\":42,\"visible\":{visible},\"frameAgeMs\":{frameAge}}}", StatsTime));
        var current = Telemetry(h, StatsTime);
        Assert.True(current.StatsFresh);
        Assert.False(current.MetricsFresh);
        Assert.Equal(status, current.FpsText);
        Assert.Equal(0, current.Fps);
        Assert.Equal(0, current.LatencyMs);
        Assert.Equal("—", current.LatencyText);
    }

    [Fact]
    public void Stats_LegacyClientsRemainCompatible_ButIdleLatencyExpires()
    {
        using var h = new SinkHarness();
        Assert.True(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TryUpdateClientStats(h.Sink,
            "{\"fps\":60,\"kbps\":8000,\"latencyMs\":42}", StatsTime));
        var fresh = Telemetry(h, StatsTime);
        Assert.True(fresh.MetricsFresh);
        Assert.Null(fresh.Visible);
        Assert.Equal("42 ms", fresh.LatencyText);
        h.Sink.UpdateClientStats(0, 0, 42, nowUtc: StatsTime.AddSeconds(9));
        Assert.True(Telemetry(h, StatsTime.AddMilliseconds(9999)).MetricsFresh);
        var idle = Telemetry(h, StatsTime.AddSeconds(10));
        Assert.True(idle.StatsFresh);
        Assert.False(idle.MetricsFresh);
        Assert.Equal(0, idle.LatencyMs);
        Assert.Equal("No recent frames", idle.FpsText);
    }

    [Fact]
    public void Stats_NewReportRestoresMetrics_AndAggregatesExcludeStaleLatency()
    {
        using var old = new SinkHarness(remoteAddress: "telemetry-old");
        using var current = new SinkHarness(remoteAddress: "telemetry-current");
        old.Sink.UpdateClientStats(60, 8000, 999, nowUtc: StatsTime);
        current.Sink.UpdateClientStats(30, 4000, 25, visible: true, frameAgeMs: 0, nowUtc: StatsTime.AddSeconds(20));
        var samples = ClientFrameSink.Snapshot(StatsTime.AddSeconds(20))
            .Where(c => c.RemoteAddress.StartsWith("telemetry-"));
        Assert.Equal(25d, samples.Where(c => c.LatencyMs > 0).Average(c => c.LatencyMs));
        Assert.Equal(30, samples.Max(c => c.Fps));
        old.Sink.UpdateClientStats(60, 8000, 40, visible: true, frameAgeMs: 0, nowUtc: StatsTime.AddSeconds(21));
        Assert.True(Telemetry(old, StatsTime.AddSeconds(21)).MetricsFresh);
        Assert.Equal("40 ms", Telemetry(old, StatsTime.AddSeconds(21)).LatencyText);
    }

    [Fact]
    public void Stats_NoReportOrMalformedJson_DoesNotInventFreshMetrics()
    {
        using var h = new SinkHarness();
        Assert.False(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TryUpdateClientStats(h.Sink, "{", StatsTime));
        Assert.False(OpenWinSidecar.Service.Protocol.SidecarTcpServer.TryUpdateClientStats(h.Sink, "null", StatsTime));
        var waiting = Telemetry(h, StatsTime);
        Assert.Null(h.Sink.LastStatsUtc);
        Assert.False(waiting.MetricsFresh);
        Assert.Equal("Waiting", waiting.FpsText);
        Assert.Equal("—", waiting.LatencyText);
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
