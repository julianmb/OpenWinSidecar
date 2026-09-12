using System.Linq;
using System.Threading;

namespace OpenWinSidecar.Core.Services;

/// <summary>
/// Process singleton guard: a second full instance would fight the first over ports
/// 80/8080/28252 and the virtual display. The OS mutex is the mechanism; this class is
/// the policy (what's exempt, which name) so it can be unit-tested without WPF.
/// </summary>
public static class SingleInstanceGuard
{
    public const string MutexName = @"Local\OpenWinSidecar-SingleInstance";

    private static Mutex? _mutex;

    /// <summary>Modes that must never be blocked by the singleton guard.</summary>
    public static bool IsExempt(string[] args) => args.Contains("--screenshot");

    /// <summary>
    /// Tries to become the singleton owner. Returns false when a live instance already
    /// holds <paramref name="name"/>. Abandoned (crashed-owner) mutexes count as free.
    /// Never throws and never blocks: a guard failure fails open so startup continues.
    /// </summary>
    public static bool TryAcquire(string name = MutexName)
    {
        try
        {
            try
            {
                using var existing = Mutex.OpenExisting(name);
                return false; // a live instance holds it
            }
            catch (WaitHandleCannotBeOpenedException) { /* free */ }
            catch (System.UnauthorizedAccessException) { return false; } // exists but ACL'd: assume live
        }
        catch { }

        try
        {
            _mutex = new Mutex(true, name, out _);
            return true;
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died without releasing; the mutex is ours now.
            try { _mutex = new Mutex(true, name, out _); } catch { }
            return true;
        }
        catch { return true; }
    }
}
