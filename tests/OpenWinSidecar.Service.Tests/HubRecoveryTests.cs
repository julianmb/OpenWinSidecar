using System;
using System.IO;
using System.Linq;
using OpenWinSidecar.Core.Services;
using OpenWinSidecar.Service;
using OpenWinSidecar.Service.Capture;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Recovery infrastructure: watchdog stall decisions, capped file logging, and the
/// process singleton guard. All three are pure, deterministic logic by design so a
/// frozen-stream regression can be pinned without timing-flaky tests.
/// </summary>
public class HubRecoveryTests
{
    [Fact]
    public void Watchdog_StallDecision()
    {
        const long T = FrameBroadcastHub.ProducerStallThresholdMs;
        // Exactly at the threshold counts as healthy; 1ms over counts as stalled.
        Assert.False(FrameBroadcastHub.IsProducerStalled(1000, 1000 + T, hasSinks: true));
        Assert.True(FrameBroadcastHub.IsProducerStalled(1000, 1000 + T + 1, hasSinks: true));
        // No attached sinks: never recreate (the producer retires itself when idle).
        Assert.False(FrameBroadcastHub.IsProducerStalled(1000, 1000 + T + 1, hasSinks: false));
        Assert.False(FrameBroadcastHub.IsProducerStalled(5000, 6000, hasSinks: true));
        // TickCount64 wrap: 11ms after wrap must not read as a 292M-year stall.
        Assert.False(FrameBroadcastHub.IsProducerStalled(long.MaxValue - 5, long.MinValue + 5, hasSinks: true));
        // Ancient tick with waiting sinks: recreate.
        Assert.True(FrameBroadcastHub.IsProducerStalled(0, T + 1, hasSinks: true));
    }

    [Fact]
    public void FileLogSink_RotatesPreservingOrderAndTail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ows_test_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sidecar.log");
        try
        {
            FileLogSink.ConfigureForTest(path, maxBytes: 1024);
            const int count = 300;
            for (int i = 0; i < count; i++)
                FileLogSink.WriteLine($"line-{i:D4}-abcdefghijklmnopqrstuvwxyz");

            var backup = path + ".1";
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(backup), "expected at least one rollover at 1KB cap");

            // Read with ReadWrite sharing: the sink keeps the live file open, and tailing
            // it while the app runs is the whole point of the file log.
            var all = ReadSharedLines(backup).Concat(ReadSharedLines(path)).ToList();
            Assert.True(all.Count > 0 && all.Count <= count);
            int prev = -1;
            foreach (var line in all)
            {
                int at = line.IndexOf("line-", StringComparison.Ordinal);
                Assert.True(at >= 0);
                int n = int.Parse(line.Substring(at + 5, 4));
                Assert.True(n > prev, "log order violated across rollover");
                prev = n;
            }
            Assert.Contains("line-0299", all[^1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static List<string> ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    [Fact]
    public void SingletonGuard_ExemptsDiagnosticsOnly()
    {
        Assert.True(SingleInstanceGuard.IsExempt(new[] { "--screenshot", "out.png" }));
        Assert.False(SingleInstanceGuard.IsExempt(Array.Empty<string>()));
        Assert.False(SingleInstanceGuard.IsExempt(new[] { "--autostart" }));
    }

    [Fact]
    public void SingletonGuard_SecondAcquireInProcessRefused()
    {
        string name = @"Local\OpenWinSidecar-Test-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstanceGuard.TryAcquire(name));
        Assert.False(SingleInstanceGuard.TryAcquire(name)); // we hold it -> refused
    }
}
