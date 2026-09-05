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
        {
            try { File.WriteAllText(@"C:\Users\JulianB\source\repos\OpenSpacedesk\console_crash.log", "UnhandledException: " + e.ExceptionObject?.ToString()); } catch { }
        };
        DispatcherUnhandledException += (s, e) =>
        {
            try { File.WriteAllText(@"C:\Users\JulianB\source\repos\OpenSpacedesk\console_crash.log", "DispatcherUnhandledException: " + e.Exception?.ToString()); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try { File.WriteAllText(@"C:\Users\JulianB\source\repos\OpenSpacedesk\console_crash.log", "UnobservedTaskException: " + e.Exception?.ToString()); } catch { }
        };
    }
}

