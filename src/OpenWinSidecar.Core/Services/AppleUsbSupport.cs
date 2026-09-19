using System.Diagnostics;
using Microsoft.Win32;

namespace OpenWinSidecar.Core.Services;

/// <summary>Result of probing the PC for iPhone/iPad USB network support.</summary>
public enum AppleUsbDriverState
{
    /// <summary>No iPhone/iPad is attached over USB right now — nothing to check.</summary>
    NoDevice,
    /// <summary>Device attached and the "Apple Mobile Device Ethernet" adapter is up with an IPv4.</summary>
    Ready,
    /// <summary>Device attached but Windows has no network function for it — the Apple driver is missing.</summary>
    DriverMissing,
}

/// <summary>
/// Detects whether an iPhone/iPad is attached over USB and whether the driver that
/// provides network-over-USB ("Apple Mobile Device Ethernet", installed by the Apple
/// Devices app or iTunes) is present. When it is, the iPad can stream over the cable
/// instead of Wi-Fi.
/// </summary>
public static class AppleUsbSupport
{
    /// <summary>Microsoft Store product id of the "Apple Devices" app.</summary>
    public const string AppleDevicesStoreProductId = "9NP83LWLPZ9K";

    private const string StoreDeepLink = "ms-windows-store://pdp/?ProductId=" + AppleDevicesStoreProductId;
    private const string AppleDevicesUrl = "https://apps.microsoft.com/detail/" + AppleDevicesStoreProductId;

    public static AppleUsbDriverState GetDriverState()
    {
        if (!IsAppleMobileDeviceAttached()) return AppleUsbDriverState.NoDevice;

        // The adapter only exists once the driver is installed; its presence with an
        // IPv4 means the cable can carry the stream.
        var endpoints = new NetworkDiscoveryService().GetActiveNetworkEndpoints();
        if (endpoints.Any(e => e.Category.Contains("USB", StringComparison.Ordinal)))
            return AppleUsbDriverState.Ready;

        // Fall back to a raw interface scan — the classifier may legitimately skip
        // the adapter while it has no address yet.
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                var combined = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (combined.Contains("apple mobile device ethernet") ||
                    (combined.Contains("apple") && combined.Contains("ethernet")))
                    return AppleUsbDriverState.Ready;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AppleUsbSupport] interface scan failed: {ex.Message}");
        }

        // Heuristic: iTunes-era driver packages install "Apple Mobile Device
        // Support" even when no adapter is currently bound, and the Apple Devices
        // appx registers under the same USB driver. Treated as Ready-with-no-address;
        // surfaced as Ready only when an adapter is found, so a stale install with a
        // genuinely missing driver still shows the install hint.
        return AppleUsbDriverState.DriverMissing;
    }

    /// <summary>True when an iPhone/iPad (Apple VID 0x05AC) is attached over USB.</summary>
    public static bool IsAppleMobileDeviceAttached()
    {
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return false;
            foreach (var name in usb.GetSubKeyNames())
            {
                if (name.StartsWith("VID_05AC", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AppleUsbSupport] USB registry probe failed: {ex.Message}");
        }
        return false;
    }

    /// <summary>Opens the Microsoft Store page for the Apple Devices app.</summary>
    public static void OpenStorePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(StoreDeepLink) { UseShellExecute = true });
        }
        catch
        {
            // Deep link can fail if the Store app was removed; fall back to the web page.
            Process.Start(new ProcessStartInfo(AppleDevicesUrl) { UseShellExecute = true });
        }
    }
}
