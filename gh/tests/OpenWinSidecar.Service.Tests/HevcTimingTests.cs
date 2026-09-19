using System.Net;
using System.Net.Sockets;
using OpenWinSidecar.Service.Capture;

namespace OpenWinSidecar.Service.Tests;

public class HevcTimingTests
{
    [Fact]
    public async Task RetiredCallbacksCannotConsumeNewInputs_AndAcceptedOldPacketsDrainBeforeNewDescription()
    {
        using var hub = new FrameBroadcastHub();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        using var server = listener.AcceptTcpClient();
        using var streamLock = new SemaphoreSlim(1, 1);
        using var sink = hub.CreateSink(server.GetStream(), streamLock, "");
        var old = new HevcGeneration();
        var next = new HevcGeneration();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<string>();
        var blocker = sink.QueueHevcSendAsync(() => gate.Task);
        Task oldDesc = Task.CompletedTask, oldPacket = Task.CompletedTask;
        Task newDesc = Task.CompletedTask, newPacket = Task.CompletedTask;
        old.AcceptDescription(() => oldDesc = sink.QueueHevcSendAsync(() =>
        {
            sent.Add("old description");
            return Task.CompletedTask;
        }));
        var oldInput = old.BeginPush(100, 200)!;
        old.CompletePush(oldInput, true);
        Assert.True(old.AcceptOutput(input => oldPacket = sink.QueueHevcSendAsync(async () =>
        {
            Assert.Equal(100L, await HevcGeneration.MatchedTimestampAsync(input, 300));
            sent.Add("old packet");
        })));
        old.Retire();
        var newInput = next.BeginPush(400, 500)!;
        next.CompletePush(newInput, true);
        Assert.False(old.AcceptOutput(_ => throw new Exception("late output admitted")));
        Assert.False(old.AcceptDescription(() => throw new Exception("late description admitted")));
        next.AcceptDescription(() => newDesc = sink.QueueHevcSendAsync(() =>
        {
            sent.Add("new description");
            return Task.CompletedTask;
        }));
        next.AcceptOutput(input => newPacket = sink.QueueHevcSendAsync(async () =>
        {
            Assert.Same(newInput, input);
            Assert.Equal(400L, await HevcGeneration.MatchedTimestampAsync(input, 600));
            sent.Add("new packet");
        }));
        Assert.Empty(sent);
        Assert.Equal(5, sink.HevcSendsInFlight);
        var capacity = sink.WaitForHevcSendCapacityAsync(CancellationToken.None);
        Assert.False(capacity.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(blocker, oldDesc, oldPacket, newDesc, newPacket, capacity).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "old description", "old packet", "new description", "new packet" }, sent);
        Assert.Equal(0, sink.HevcSendsInFlight);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputBeforePushReturns_WaitsForConfirmation_AndFailureIsNotMeasured(bool success)
    {
        var generation = new HevcGeneration();
        var input = generation.BeginPush(100, 200)!;
        Task<long?>? timestamp = null;
        generation.AcceptOutput(matched => timestamp = HevcGeneration.MatchedTimestampAsync(matched, 300));
        Assert.NotNull(timestamp);
        Assert.False(timestamp.IsCompleted);
        generation.CompletePush(input, success);
        Assert.Equal(success ? 100L : (long?)null, await timestamp);
        if (!success)
        {
            Assert.Null(generation.BeginPush(400, 500));
            Assert.False(generation.AcceptOutput(_ => throw new Exception("failed generation reused")));
        }
    }

    [Fact]
    public async Task FailedPushRetiresAllPendingEntries_WithoutShiftingAssociations()
    {
        var generation = new HevcGeneration();
        var first = generation.BeginPush(100, 200)!;
        generation.CompletePush(first, true);
        var failed = generation.BeginPush(300, 400)!;
        generation.CompletePush(failed, false);
        Assert.False(generation.AcceptOutput(_ => throw new Exception("retired output")));
        Assert.Null(await HevcGeneration.MatchedTimestampAsync(failed, 500));
        Assert.Null(generation.BeginPush(600, 700));
    }

    [Fact]
    public void TimestampCapacityDoesNotEvictAndMislabelOldestInput()
    {
        var generation = new HevcGeneration();
        var first = generation.BeginPush(1, 2)!;
        for (int i = 1; i < HevcGeneration.Capacity; i++)
            Assert.NotNull(generation.BeginPush(i + 1, i + 2));
        Assert.Null(generation.BeginPush(1000, 1001));
        generation.AcceptOutput(input => Assert.Same(first, input));
        generation.Retire();
    }

    [Fact]
    public async Task MissingAndInvalidPairsAreNeverFabricated()
    {
        Assert.Null(await HevcGeneration.MatchedTimestampAsync(null, 300));
        foreach (var pair in new[] { (0L, 200L, 300L), (250L, 200L, 300L), (100L, 200L, 150L) })
        {
            var generation = new HevcGeneration();
            var input = generation.BeginPush(pair.Item1, pair.Item2)!;
            generation.CompletePush(input, true);
            Assert.Null(await HevcGeneration.MatchedTimestampAsync(input, pair.Item3));
        }
    }

    [Fact]
    public void DiagnosticsFlushAtFiveSeconds_ResetAndRemainRateLimitedAfterIdle()
    {
        var diagnostics = new SinkStageDiagnostics(0);
        foreach (var stage in Enum.GetValues<SinkStageDiagnostics.Stage>())
        {
            diagnostics.Sample(stage, 1000, 3000);
            diagnostics.Sample(stage, 3000, 6000);
            diagnostics.Sample(stage, 4000, 3000); // ignored, not a fabricated zero sample
        }
        diagnostics.Pending(3);
        diagnostics.Pending(1);
        diagnostics.MissingTimestamp();
        diagnostics.PushFailed();
        Assert.Null(diagnostics.Flush(4_999_999, "test"));
        string line = diagnostics.Flush(5_000_000, "test")!;
        Assert.Contains("n=2", line);
        Assert.Contains("pending=1 max=3", line);
        Assert.Contains("timestampMissing=1; pushFailed=1", line);
        Assert.Contains("includes compose/raw waiting; not pure encoder", line);
        Assert.Null(diagnostics.Flush(5_000_001, "test"));
        var empty = diagnostics.Flush(60_000_000, "test")!;
        Assert.Contains("rawPush n=0", empty);
        Assert.Contains("pending=1 max=1", empty);
        Assert.Contains("timestampMissing=0; pushFailed=0", empty);
        diagnostics.MissingTimestamp();
        Assert.Null(diagnostics.Flush(60_000_001, "test"));
        Assert.NotNull(diagnostics.Flush(65_000_000, "test"));
    }
}
