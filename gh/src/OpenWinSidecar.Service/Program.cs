using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWinSidecar.Service;
using System.Runtime.InteropServices;

TimerResolution.timeBeginPeriod(1);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "OpenWinSidecarService";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddHostedService<SpacedeskServerWorker>();

var host = builder.Build();
host.Run();

internal static class TimerResolution
{
    // Raise the Windows timer resolution so the 60 FPS producer pacing (Task.Delay ~6ms)
    // isn't quantized to the default ~15.6ms scheduler tick.
    [DllImport("winmm.dll")]
    internal static extern int timeBeginPeriod(uint msPeriod);
}
