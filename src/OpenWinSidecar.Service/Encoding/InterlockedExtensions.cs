using System.Threading;

namespace OpenWinSidecar.Service.Encoders;

/// <summary>Max helpers for Interlocked aggregation in diagnostics windows.</summary>
internal static class InterlockedExtensions
{
    public static void Max(ref long location, long value)
    {
        long current = Volatile.Read(ref location);
        while (value > current)
        {
            long prior = Interlocked.CompareExchange(ref location, value, current);
            if (prior == current) return;
            current = prior;
        }
    }
}
