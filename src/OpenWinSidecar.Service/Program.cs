using System;
using System.Threading.Tasks;

namespace OpenWinSidecar.Service;

public static class ServiceProgram
{
    public static void Main(string[] args)
    {
        using var host = new StreamingServerHost();
        host.OnLog += msg => Console.WriteLine(msg);
        host.Start();
        Console.WriteLine("OpenWinSidecar Streaming Server Host active. Press Ctrl+C to exit.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        tcs.Task.Wait();
        host.Stop();
    }
}
