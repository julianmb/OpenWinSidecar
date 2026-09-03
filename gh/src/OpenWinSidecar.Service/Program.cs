using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWinSidecar.Service;

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
