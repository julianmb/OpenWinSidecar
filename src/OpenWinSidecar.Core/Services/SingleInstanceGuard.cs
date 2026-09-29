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
        // Acquisition is atomic. The previous implementation probed with OpenExisting and
        // then constructed the mutex, discarding `createdNew` - a textbook time-of-check /
        // time-of-use race in which two processes starting together both pass the probe and
        // both proceed as the singleton, then fight over ports 80/8080/28252 and the virtual
        // display. `createdNew` is the kernel's own answer to "did I create it", so one
        // call decides it and the probe is unnecessary.
        try
        {
            bool createdNew;
            // Keep the handle in a local first. A loser must never touch the shared static:
            // clearing it would orphan the winner's handle, and once that handle is
            // finalised the OS releases the mutex, letting a third process create a fresh
            // one and also believe it is the singleton.
            var handle = new Mutex(initiallyOwned: true, name, out createdNew);
            if (!createdNew)
            {
                // Someone else owns it. Dropping our extra handle is correct and does not
                // affect their ownership.
                handle.Dispose();
                return false;
            }
            _mutex = handle;
            return true;
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing. Constructing with
            // initiallyOwned still hands us ownership of an abandoned mutex, but the
            // abandoned state is only surfaced on a Wait, so re-construct and trust it.
            try
            {
                bool createdNew;
                _mutex = new Mutex(initiallyOwned: true, name, out createdNew);
                return true;
            }
            catch { return true; }
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but this process may not open it - another user or a
            // higher-integrity instance owns it. Treat as live rather than starting a
            // second copy that will collide on the ports.
            return false;
        }
        catch { return true; }
    }
}
