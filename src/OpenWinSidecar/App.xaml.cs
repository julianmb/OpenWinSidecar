using System.IO;
using System.Windows;

namespace OpenWinSidecar;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            WriteCrashLog("UnhandledException: " + e.ExceptionObject?.ToString());

        // Mark the exception handled. Without this the CLR tears the process down
        // immediately after the handler returns, so the crash log was the only artifact
        // and the user lost whatever the app was doing (streaming, a half-applied display
        // change). A UI-thread exception no longer kills the app outright; the message box
        // tells the user something went wrong instead of the window vanishing.
        DispatcherUnhandledException += (s, e) =>
        {
            WriteCrashLog("DispatcherUnhandledException: " + e.Exception?.ToString());
            e.Handled = true;
            try
            {
                System.Windows.MessageBox.Show(
                    "OpenWinSidecar hit an unexpected error and recovered from it.\n\n" +
                    "Streaming may be interrupted — the details are in the log.",
                    "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog("UnobservedTaskException: " + e.Exception?.ToString());
            // Observed, not swallowed-and-ignored: a faulted background task is still a bug,
            // but finalizer-thread rethrow would kill an otherwise healthy streaming session.
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WriteLog($"Application OnStartup: {string.Join(" ", e.Args)}");

        // A second full instance would fight the first over ports 80/8080/28252 and the
        // VDD. Diagnostic --screenshot runs are exempt (they exit without streaming).
        if (!OpenWinSidecar.Core.Services.SingleInstanceGuard.IsExempt(e.Args)
            && !OpenWinSidecar.Core.Services.SingleInstanceGuard.TryAcquire())
        {
            System.Windows.MessageBox.Show(
                "OpenWinSidecar is already running (check the system tray).",
                "OpenWinSidecar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WriteLog($"Application OnExit: code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>
    /// Crash log lands next to the executable (appending, so earlier exceptions survive
    /// a later one) with a fallback to the temp folder when the exe directory is read-only.
    /// </summary>
    public static void WriteLog(string content)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {content}{Environment.NewLine}";

            string dir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(dir, "console_crash.log");
            try { File.AppendAllText(path, line); }
            catch
            {
                path = Path.Combine(Path.GetTempPath(), "openwinsidecar_crash.log");
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }

    private static void WriteCrashLog(string content) => WriteLog(content);
}
