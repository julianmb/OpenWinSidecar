using OpenWinSidecar.Core.Services;
using Xunit;

namespace OpenWinSidecar.Service.Tests;

public class AppleUsbSupportTests
{
    [Fact]
    public void ClassifyEndpoint_MatchesAppleMobileDeviceEthernet_AsUsbCable()
    {
        var (priority, category) = NetworkDiscoveryService.ClassifyEndpoint(
            "Apple Mobile Device Ethernet", "Apple Mobile Device Ethernet Adapter", "Ethernet", "172.20.10.2");

        Assert.Equal(100, priority);
        Assert.Contains("USB", category);
    }

    [Fact]
    public void ClassifyEndpoint_DoesNotTreatPlainUsbAdapters_AsAppleCable()
    {
        // A generic "USB 10/100/1000 LAN" adapter without any Apple/RNDIS/NCM/tether token
        // must fall through to the wired-ethernet rule, not claim the USB-cable priority.
        var (priority, category) = NetworkDiscoveryService.ClassifyEndpoint(
            "Ethernet 2", "Realtek USB GbE Family Controller", "Ethernet", "192.168.1.2");

        Assert.Equal(80, priority);
        Assert.DoesNotContain("USB", category);
    }

    [Fact]
    public void GetDriverState_MatchesDeviceAttachment()
    {
        // Hardware-dependent: whatever the registry probe says about an attached Apple
        // device, GetDriverState must agree with it (NoDevice when absent, and never
        // Ready without an adapter when no device is attached).
        var attached = AppleUsbSupport.IsAppleMobileDeviceAttached();
        var state = AppleUsbSupport.GetDriverState();

        Assert.Equal(attached, state != AppleUsbDriverState.NoDevice);
        if (!attached) Assert.NotEqual(AppleUsbDriverState.Ready, state);
    }

    [Fact]
    public void StoreProductId_IsWellFormed()
    {
        // Microsoft Store product ids are 12 characters; a typo here silently breaks
        // the deep link, so pin the format.
        Assert.Matches("^[0-9A-Z]{12}$", AppleUsbSupport.AppleDevicesStoreProductId);
        Assert.Equal("9NP83LWLPZ9K", AppleUsbSupport.AppleDevicesStoreProductId);
    }
}
