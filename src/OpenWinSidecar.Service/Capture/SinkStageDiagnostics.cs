using System.Text;

namespace OpenWinSidecar.Service.Capture;

/// <summary>Fixed-size aggregates; all readings use the hub's monotonic microsecond clock.</summary>
internal sealed class SinkStageDiagnostics(long windowStartUs)
{
    internal enum Stage { RawPush, OrderedWait, PacingWait, SocketLock, SocketWrite, TimestampToOutput }
    private readonly object _sync = new();
    private readonly long[] _counts = new long[6];
    private readonly double[] _totalsUs = new double[6];
    private readonly long[] _maxUs = new long[6];
    private long _windowStartUs = windowStartUs;
    private long _missing, _pushFailures;
    private int _pending, _pendingMax;
    internal const long WindowUs = 5_000_000;

    internal void Sample(Stage stage, long startUs, long endUs)
    {
        // Invalid clock/order pairs are not measured samples.
        if (startUs < 0 || endUs < startUs) return;
        lock (_sync)
        {
            int i = (int)stage;
            long elapsed = endUs - startUs;
            _counts[i]++;
            _totalsUs[i] += elapsed;
            _maxUs[i] = Math.Max(_maxUs[i], elapsed);
        }
    }

    internal void MissingTimestamp() { lock (_sync) _missing++; }
    internal void PushFailed() { lock (_sync) _pushFailures++; }
    internal void Pending(int count)
    {
        lock (_sync)
        {
            _pending = count;
            _pendingMax = Math.Max(_pendingMax, count);
        }
    }

    internal string? Flush(long nowUs, string label)
    {
        lock (_sync)
        {
            if (nowUs - _windowStartUs < WindowUs) return null;
            string[] names = ["rawPush", "outputToOrderedSend", "pacingWait", "socketLock", "socketWrite",
                "captureTimestampToOutput(includes compose/raw waiting; not pure encoder)"];
            var text = new StringBuilder($"[Sink] {label}: HEVC stages ({(nowUs - _windowStartUs) / 1_000_000.0:F1}s)");
            for (int i = 0; i < _counts.Length; i++)
            {
                long n = _counts[i];
                text.Append(n == 0 ? $"; {names[i]} n=0"
                    : $"; {names[i]} avg={_totalsUs[i] / n / 1000:F1}ms max={_maxUs[i] / 1000.0:F1}ms n={n}");
            }
            text.Append($"; pending={_pending} max={_pendingMax}; timestampMissing={_missing}; pushFailed={_pushFailures}");
            Array.Clear(_counts);
            Array.Clear(_totalsUs);
            Array.Clear(_maxUs);
            _missing = _pushFailures = 0;
            _pendingMax = _pending;
            // Anchor to the flush, not the next sample: no burst logs after an idle interval.
            _windowStartUs = nowUs;
            return text.ToString();
        }
    }
}

/// <summary>
/// One instance per encoder. Admission and retirement are serialized with callback queueing,
/// so the sink's ordered tail contains every accepted old callback before any new description.
/// No locks are held across pipe writes or async sends. -bf 0 gives FIFO AU/input association.
/// </summary>
internal sealed class HevcGeneration
{
    internal sealed class Input(long timestampUs, long pushStartUs)
    {
        internal long TimestampUs { get; } = timestampUs;
        internal long PushStartUs { get; } = pushStartUs;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal const int Capacity = 64;
    private readonly object _sync = new();
    private readonly Queue<Input> _inputs = new(Capacity);
    private bool _retired;

    internal Input? BeginPush(long timestampUs, long pushStartUs)
    {
        lock (_sync)
        {
            // Never evict the oldest entry: that would shift every subsequent association.
            if (_retired || _inputs.Count >= Capacity) return null;
            var input = new Input(timestampUs, pushStartUs);
            _inputs.Enqueue(input);
            return input;
        }
    }

    internal void CompletePush(Input input, bool success)
    {
        lock (_sync)
        {
            input.Completion.TrySetResult(success);
            // A failed write may have been partial, or even produced output before failing.
            // Invalidate the entire generation, not a guessed FIFO position.
            if (!success) Retire();
        }
    }

    internal bool AcceptOutput(Action<Input?> enqueue)
    {
        lock (_sync)
        {
            if (_retired) return false;
            _inputs.TryDequeue(out var input);
            enqueue(input);
            return true;
        }
    }

    internal bool AcceptDescription(Action enqueue)
    {
        lock (_sync)
        {
            if (_retired) return false;
            enqueue();
            return true;
        }
    }

    internal void Retire()
    {
        lock (_sync)
        {
            _retired = true;
            while (_inputs.TryDequeue(out var input)) input.Completion.TrySetResult(false);
        }
    }

    internal static async Task<long?> MatchedTimestampAsync(Input? input, long outputUs)
    {
        if (input == null || !await input.Completion.Task.ConfigureAwait(false)) return null;
        return input.TimestampUs > 0 && input.TimestampUs <= input.PushStartUs && input.PushStartUs <= outputUs
            ? input.TimestampUs : null;
    }
}
