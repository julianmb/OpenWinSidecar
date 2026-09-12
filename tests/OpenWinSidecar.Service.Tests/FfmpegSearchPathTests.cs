using OpenWinSidecar.Service.Encoders;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Pins the explicit ffmpeg.exe candidate order used after the PATH probe: an
/// app-local bundle ({app}\ffmpeg\bin) must win over the winget Links alias, and
/// both must be checked before the (untestable here) recursive Packages scan.
/// The installer and the runtime resolver rely on this precedence.
/// </summary>
public class FfmpegSearchPathTests
{
    [Fact]
    public void SearchPaths_AppLocalBundle_First()
    {
        var paths = HevcStreamEncoder.GetFfmpegSearchPaths();

        var appLocal = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        Assert.Equal(appLocal, paths[0]);
    }

    [Fact]
    public void SearchPaths_WingetLinksAlias_Second()
    {
        var paths = HevcStreamEncoder.GetFfmpegSearchPaths();

        var links = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        Assert.Equal(links, paths[1]);
    }

    [Fact]
    public void SearchPaths_AllDistinct()
    {
        var paths = HevcStreamEncoder.GetFfmpegSearchPaths();
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
