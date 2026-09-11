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
        DispatcherUnhandledException += (s, e) =>
            WriteCrashLog("DispatcherUnhandledException: " + e.Exception?.ToString());
        TaskScheduler.UnobservedTaskException += (s, e) =>
            WriteCrashLog("UnobservedTaskException: " + e.Exception?.ToString());
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WriteLog($"Application OnStartup: {string.Join(" ", e.Args)}");
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
