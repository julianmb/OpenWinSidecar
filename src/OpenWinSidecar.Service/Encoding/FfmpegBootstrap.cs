using System.Diagnostics;

namespace OpenWinSidecar.Service.Encoders;

/// <summary>
/// One-click FFmpeg acquisition for the dashboard: locates winget (the WindowsApps
/// execution alias, then PATH) and installs the Gyan full build the HEVC encoder expects.
/// Gyan.FFmpeg is a portable winget package, so this needs no elevation. Mirrors the
/// installer's winget fallback and the documented manual command.
/// </summary>
public static class FfmpegBootstrap
{
    public static string? FindWingetExecutable()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "winget.exe"))) return Path.Combine(dir, "winget.exe");
            }
            catch { /* ignore malformed PATH entries */ }
        }
        return null;
    }

    /// <summary>
    /// Runs `winget install --id Gyan.FFmpeg` to completion. Returns winget's exit code,
    /// or null when winget is unavailable / could not be launched.
    /// </summary>
    public static async Task<int?> InstallFfmpegViaWingetAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var winget = FindWingetExecutable();
        if (winget == null)
        {
            log?.Invoke("[FFmpeg] winget not found — install manually: winget install --id Gyan.FFmpeg -e (full build)");
            return null;
        }

        log?.Invoke("[FFmpeg] Installing Gyan.FFmpeg via winget (~170 MB download, a few minutes)...");
        var psi = new ProcessStartInfo
        {
            FileName = winget,
            Arguments = "install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements --disable-interactivity --silent",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null)
        {
            log?.Invoke("[FFmpeg] could not launch winget — install manually: winget install --id Gyan.FFmpeg -e");
            return null;
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        log?.Invoke($"[FFmpeg] winget finished with exit code {p.ExitCode}.");
        return p.ExitCode;
    }
}
