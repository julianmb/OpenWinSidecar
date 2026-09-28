namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// The product version lives in two places that must agree: &lt;Version&gt; in
/// src/OpenWinSidecar/OpenWinSidecar.csproj (stamps the assembly — the dashboard's
/// "v…" title) and #define MyAppVersion in installer/OpenWinSidecar.iss (the
/// installer, the setup exe name, and the version the release workflow reads).
/// The dashboard used to hardcode "v0.1" while the installer shipped 0.1.1; this
/// test makes that drift a build failure instead of a stale UI.
/// </summary>
public class VersionConsistencyTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OpenWinSidecar.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(segments).ToArray());
        Assert.True(File.Exists(path), $"expected repo file not found: {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void InstallerDefine_Matches_CsprojVersion()
    {
        var iss = ReadRepoFile("installer", "OpenWinSidecar.iss");
        var issMatch = System.Text.RegularExpressions.Regex.Match(
            iss, "(?m)^\\s*#define MyAppVersion \"([^\"]+)\"");
        Assert.True(issMatch.Success, "installer/OpenWinSidecar.iss has no #define MyAppVersion \"...\"");

        var csproj = ReadRepoFile("src", "OpenWinSidecar", "OpenWinSidecar.csproj");
        var csprojMatch = System.Text.RegularExpressions.Regex.Match(
            csproj, "(?m)^\\s*<Version>([^<]+)</Version>");
        Assert.True(csprojMatch.Success, "OpenWinSidecar.csproj has no <Version>");

        Assert.Equal(
            csprojMatch.Groups[1].Value.Trim(),
            issMatch.Groups[1].Value.Trim());
    }

    /// <summary>
    /// The Core, Service and CLI assemblies carried no version at all, so they reported
    /// 1.0.0 in About boxes, crash logs and winget diagnostics while the app said 0.2.0.
    /// They are stamped from the same value so they cannot quietly disagree again.
    /// </summary>
    [Theory]
    [InlineData("OpenWinSidecar.Core")]
    [InlineData("OpenWinSidecar.Service")]
    [InlineData("OpenWinSidecar.Cli")]
    public void EveryAssemblyCarriesTheSameVersion(string projectName)
    {
        var csproj = ReadRepoFile("src", projectName, projectName + ".csproj");
        var match = System.Text.RegularExpressions.Regex.Match(csproj, "(?m)^\\s*<Version>([^<]+)</Version>");
        Assert.True(match.Success, $"{projectName}.csproj has no <Version>, so its assembly reports 1.0.0");

        var iss = ReadRepoFile("installer", "OpenWinSidecar.iss");
        var issMatch = System.Text.RegularExpressions.Regex.Match(iss, "(?m)^\\s*#define MyAppVersion \"([^\"]+)\"");
        Assert.Equal(issMatch.Groups[1].Value.Trim(), match.Groups[1].Value.Trim());
    }
}
