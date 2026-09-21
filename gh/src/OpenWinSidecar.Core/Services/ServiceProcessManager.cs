using System.Diagnostics;
using System.IO;

namespace OpenWinSidecar.Core.Services;

public class ServiceProcessManager
{
    private Process? _serviceProcess;
    public event Action<string>? OnLogReceived;
    public event Action? OnStatusChanged;

    public bool IsProcessRunning
    {
        get
        {
            if (_serviceProcess != null && !_serviceProcess.HasExited) return true;
            return Process.GetProcessesByName("OpenWinSidecar.Service").Length > 0 ||
                   Process.GetProcessesByName("OpenWinSidecar.Service").Length > 0;
        }
    }

    public int? ProcessId
    {
        get
        {
            if (_serviceProcess != null && !_serviceProcess.HasExited) return _serviceProcess.Id;
            var procs = Process.GetProcessesByName("OpenWinSidecar.Service");
            if (procs.Length > 0) return procs[0].Id;
            var oldProcs = Process.GetProcessesByName("OpenWinSidecar.Service");
            if (oldProcs.Length > 0) return oldProcs[0].Id;
            return null;
        }
    }

    public double MemoryUsageMb
    {
        get
        {
            try
            {
                if (_serviceProcess != null && !_serviceProcess.HasExited)
                {
                    _serviceProcess.Refresh();
                    return _serviceProcess.WorkingSet64 / (1024.0 * 1024.0);
                }
                var procs = Process.GetProcessesByName("OpenWinSidecar.Service");
                if (procs.Length > 0) return procs[0].WorkingSet64 / (1024.0 * 1024.0);
                var oldProcs = Process.GetProcessesByName("OpenWinSidecar.Service");
                if (oldProcs.Length > 0) return oldProcs[0].WorkingSet64 / (1024.0 * 1024.0);
            }
            catch { }
            return 0.0;
        }
    }

    public static (string ExePath, string Arguments) FindServiceExecutable()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);

        var names = new[]
        {
            "OpenWinSidecar.Service.exe",
            "OpenWinSidecar.Service.exe",
            "OpenWinSidecar.Service.dll",
            "OpenWinSidecar.Service.dll"
        };

        var relativePaths = new[]
        {
            "",
            @"..\..\..\..\OpenWinSidecar.Service\bin\Debug\net10.0-windows",
            @"..\..\..\..\OpenWinSidecar.Service\bin\Release\net10.0-windows",
            @"..\..\..\..\OpenWinSidecar.Service\bin\Debug\net10.0-windows",
            @"..\..\..\..\OpenWinSidecar.Service\bin\Release\net10.0-windows",
            @"..\..\..\..\src\OpenWinSidecar.Service\bin\Debug\net10.0-windows",
            @"..\..\..\..\src\OpenWinSidecar.Service\bin\Release\net10.0-windows",
            @"..\..\..\..\gh\src\OpenWinSidecar.Service\bin\Debug\net10.0-windows",
            @"..\..\..\..\gh\src\OpenWinSidecar.Service\bin\Release\net10.0-windows"
        };

        // 1. Direct relative checks from BaseDirectory
        foreach (var rel in relativePaths)
        {
            var folder = Path.GetFullPath(Path.Combine(baseDir, rel));
            if (Directory.Exists(folder))
            {
                foreach (var name in names)
                {
                    var full = Path.Combine(folder, name);
                    if (File.Exists(full))
                    {
                        if (full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            return ("dotnet", $"\"{full}\"");
                        }
                        return (full, "");
                    }
                }
            }
        }

        // 2. Search upward in folder tree
        var curr = dir;
        while (curr != null)
        {
            var searchFolders = new[]
            {
                curr.FullName,
                Path.Combine(curr.FullName, "src", "OpenWinSidecar.Service", "bin", "Debug", "net10.0-windows"),
                Path.Combine(curr.FullName, "src", "OpenWinSidecar.Service", "bin", "Release", "net10.0-windows"),
                Path.Combine(curr.FullName, "src", "OpenWinSidecar.Service", "bin", "Debug", "net10.0-windows"),
                Path.Combine(curr.FullName, "src", "OpenWinSidecar.Service", "bin", "Release", "net10.0-windows"),
                Path.Combine(curr.FullName, "gh", "src", "OpenWinSidecar.Service", "bin", "Debug", "net10.0-windows"),
                Path.Combine(curr.FullName, "gh", "src", "OpenWinSidecar.Service", "bin", "Release", "net10.0-windows")
            };

            foreach (var sf in searchFolders)
            {
                if (Directory.Exists(sf))
                {
                    foreach (var name in names)
                    {
                        var candidate = Path.Combine(sf, name);
                        if (File.Exists(candidate))
                        {
                            if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                return ("dotnet", $"\"{candidate}\"");
                            }
                            return (candidate, "");
                        }
                    }
                }
            }

            curr = curr.Parent;
        }

        return ("", "");
    }

    public (bool Success, string Message) StartInteractive()
    {
        if (IsProcessRunning)
        {
            return (true, "Service is already running.");
        }

        var (exePath, args) = FindServiceExecutable();
        if (string.IsNullOrEmpty(exePath) || (exePath != "dotnet" && !File.Exists(exePath)))
        {
            var msg = "Could not locate OpenWinSidecar.Service binary. Please build the solution first.";
            OnLogReceived?.Invoke($"[Error] {msg}");
            return (false, msg);
        }

        try
        {
            string workingDir = exePath == "dotnet"
                ? Path.GetDirectoryName(args.Trim('"')) ?? AppDomain.CurrentDomain.BaseDirectory
                : Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir
            };

            _serviceProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

            _serviceProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) OnLogReceived?.Invoke(e.Data);
            };

            _serviceProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) OnLogReceived?.Invoke($"[ERR] {e.Data}");
            };

            _serviceProcess.Exited += (s, e) =>
            {
                OnLogReceived?.Invoke("[Service] Process exited.");
                OnStatusChanged?.Invoke();
            };

            bool started = _serviceProcess.Start();
            if (started)
            {
                _serviceProcess.BeginOutputReadLine();
                _serviceProcess.BeginErrorReadLine();
                OnLogReceived?.Invoke($"[Service] Started interactive process: {exePath} {args} (PID: {_serviceProcess.Id})");
                OnStatusChanged?.Invoke();
                return (true, $"Service started with PID {_serviceProcess.Id}");
            }
            return (false, "Process failed to start.");
        }
        catch (Exception ex)
        {
            OnLogReceived?.Invoke($"[Service Start Failed] {ex.Message}");
            return (false, ex.Message);
        }
    }

    public (bool Success, string Message) StartElevated()
    {
        var (exePath, args) = FindServiceExecutable();
        if (string.IsNullOrEmpty(exePath) || (exePath != "dotnet" && !File.Exists(exePath)))
        {
            var msg = "Could not locate OpenWinSidecar.Service binary. Please build the solution first.";
            OnLogReceived?.Invoke($"[Error] {msg}");
            return (false, msg);
        }

        try
        {
            string targetExe = exePath;
            string targetArgs = args;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.IsNullOrEmpty(targetArgs)
                    ? $"-NoProfile -Command \"Start-Process '{targetExe}' -Verb RunAs\""
                    : $"-NoProfile -Command \"Start-Process '{targetExe}' -ArgumentList '{targetArgs.Replace("\"", "`\"")}' -Verb RunAs\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi)?.WaitForExit(4000);
            OnLogReceived?.Invoke("[Service] Requested elevated start (Run as Administrator).");
            OnStatusChanged?.Invoke();
            return (true, "Elevated start requested.");
        }
        catch (Exception ex)
        {
            OnLogReceived?.Invoke($"[Elevated Start Failed] {ex.Message}");
            return (false, ex.Message);
        }
    }

    public void StopInteractive()
    {
        try
        {
            if (_serviceProcess != null && !_serviceProcess.HasExited)
            {
                _serviceProcess.Kill(entireProcessTree: true);
                _serviceProcess.WaitForExit(2000);
                _serviceProcess.Dispose();
                _serviceProcess = null;
            }

            KillAllStaleProcesses();
            OnLogReceived?.Invoke("[Service] Stopped successfully.");
            OnStatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            OnLogReceived?.Invoke($"[Service Stop Error] {ex.Message}");
        }
    }

    public void KillAllStaleProcesses()
    {
        try
        {
            var procs = Process.GetProcessesByName("OpenWinSidecar.Service")
                .Concat(Process.GetProcessesByName("OpenWinSidecar.Service"))
                .Concat(Process.GetProcessesByName("ffmpeg"));

            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(1000);
                }
                catch { }
            }

            OnLogReceived?.Invoke("[Cleanup] Terminated stale OpenWinSidecar/FFmpeg process instances.");
            OnStatusChanged?.Invoke();
        }
        catch { }
    }
}
