using System.IO;

namespace OpenWinSidecar.Console;

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

    /// <summary>
    /// Crash log lands next to the executable (appending, so earlier exceptions survive
    /// a later one) with a fallback to the temp folder when the exe directory is read-only.
    /// </summary>
    private static void WriteCrashLog(string content)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}{Environment.NewLine}";

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
}
