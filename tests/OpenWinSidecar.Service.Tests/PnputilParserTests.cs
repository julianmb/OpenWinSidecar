using OpenWinSidecar.Core.Models;
using OpenWinSidecar.Core.Services;

namespace OpenWinSidecar.Service.Tests;

/// <summary>
/// Pins the classification of `pnputil /enum-devices /instanceid ROOT\DISPLAY\0000`
/// output into <see cref="VirtualDisplayDeviceState"/>. The parser replaced a
/// fixed-width string match ("Status:" + 21 spaces) that broke silently whenever
/// Windows reformatted the output — these tests capture the real output shapes so a
/// format change fails loudly here instead of showing a wrong dashboard state.
/// </summary>
public class PnputilParserTests
{
    // Real pnputil output shape (whitespace captured from Windows 11):
    private const string StartedOutput = """
        Instance ID:                ROOT\DISPLAY\0000\0
        Device Description:         Virtual Display Driver
        Class Name:                 Display
        Class GUID:                 {4d36e968-e325-11ce-bfc1-08002be10318}
        Manufacturer:               MikeTheTech
        Status:                     Started
        Driver Name:                oem41.inf
        """;

    private const string DisabledOutput = """
        Instance ID:                ROOT\DISPLAY\0000\0
        Device Description:         Virtual Display Driver
        Class Name:                 Display
        Status:                     Disabled
        Driver Name:                oem41.inf
        """;

    private const string ProblemOutput = """
        Instance ID:                ROOT\DISPLAY\0000\0
        Device Description:         Virtual Display Driver
        Class Name:                 Display
        Status:                     Problem
        Problem Code:               22
        Driver Name:                oem41.inf
        """;

    [Theory]
    [InlineData(StartedOutput, VirtualDisplayDeviceState.Started)]
    [InlineData(DisabledOutput, VirtualDisplayDeviceState.Disabled)]
    [InlineData(ProblemOutput, VirtualDisplayDeviceState.Problem)]
    public void ParsesRealStatusLines(string output, VirtualDisplayDeviceState expected)
        => Assert.Equal(expected, VirtualDisplayManager.ParsePnputilDeviceStatus(output));

    [Fact]
    public void EmptyOutput_MeansDeviceMissing()
        // pnputil prints nothing on stdout for an unknown instance ID.
        => Assert.Equal(VirtualDisplayDeviceState.Missing,
            VirtualDisplayManager.ParsePnputilDeviceStatus(""));

    [Fact]
    public void WhitespaceOnlyOutput_MeansDeviceMissing()
        => Assert.Equal(VirtualDisplayDeviceState.Missing,
            VirtualDisplayManager.ParsePnputilDeviceStatus("   \r\n  "));

    [Fact]
    public void StatusLine_IsColumnAgnostic()
        // The old parser matched "Status:" + exactly 21 spaces; a Windows update that
        // shifts the column must not change the classification.
        => Assert.Equal(VirtualDisplayDeviceState.Disabled,
            VirtualDisplayManager.ParsePnputilDeviceStatus("Status: Disabled"));

    [Fact]
    public void UnknownStatusValue_ClassifiedAsProblem()
        // e.g. "UnKnown" or any future status word — safer to surface as an error
        // state than to pretend the display is fine or missing.
        => Assert.Equal(VirtualDisplayDeviceState.Problem,
            VirtualDisplayManager.ParsePnputilDeviceStatus("Status: UnKnown"));

    [Fact]
    public void NoStatusLine_ButInstanceIdPresent_ClassifiedAsProblem()
        // pnputil format change: device listed without a Status line — it exists,
        // so Missing would be wrong; Problem prompts a driver restart.
        => Assert.Equal(VirtualDisplayDeviceState.Problem,
            VirtualDisplayManager.ParsePnputilDeviceStatus(
                "Instance ID:                ROOT\\DISPLAY\\0000\r\nDevice Description:         Virtual Display Driver"));

    [Fact]
    public void UnrelatedOutput_NoStatusLine_MeansDeviceMissing()
        // Output for some other instance ID, no Status line at all and no
        // ROOT\DISPLAY\0000 mention — nothing about the VDD is present.
        => Assert.Equal(VirtualDisplayDeviceState.Missing,
            VirtualDisplayManager.ParsePnputilDeviceStatus(
                "Instance ID:                USB\\VID_045E&PID_00DB\\6&870CE29&0&1\r\nDevice Description:         USB Input Device"));
}
