# Drivers & Virtual Monitors Architecture

## 1. Virtual Display Driver (VDD / IddCx)

Windows 10 (version 1607+) and Windows 11 introduce the **Indirect Display Driver (IddCx)** framework. Unlike legacy Kernel-Mode Display Drivers (XDDM/WDDM), IddCx drivers run in User Mode (`UMDF`) with high stability and hardware acceleration support from the system DWM compositor.

OpenWinSidecar bundles and integrates the signed 64-bit `Virtual-Display-Driver` (MttVDD):
- **INF File**: `drivers\VDD\MttVDD.inf`
- **Driver DLL**: `drivers\VDD\MttVDD.dll`
- **Catalog Certificate**: `drivers\VDD\mttvdd.cat`
- **Hardware ID**: `Root\MttVDD`
- **Device Instance Node**: `ROOT\DISPLAY\0000`

---

## 2. Smart 3rd Screen Lifecycle Management

OpenWinSidecar automates the power lifecycle of the virtual monitor device node to prevent phantom desktop regions:

### 2.1 Enablement (`⚡ 3rd Screen ON`)
When turning on the virtual display:
1. **Device Enablement**: Executes `pnputil.exe /enable-device "ROOT\DISPLAY\0000"` (or `devcon.exe enable "ROOT\DISPLAY\0000"`).
2. **Desktop Extension**: Invokes `SetDisplayConfig` with `SDC_TOPOLOGY_EXTEND` and `displayswitch.exe /extend` to seamlessly attach the virtual monitor into Windows desktop geometry.
3. **Service Launch**: Automatically launches the streaming service (`OpenWinSidecar.Service`).

### 2.2 Disablement (`🔌 3rd Screen OFF`)
When turning off the virtual display:
1. **Service Stop**: Gracefully stops the streaming service and terminates video encoder pipelines.
2. **Device Disablement**: Executes `pnputil.exe /disable-device "ROOT\DISPLAY\0000"` (or `devcon.exe disable "ROOT\DISPLAY\0000"`). Windows automatically shifts open application windows back onto primary displays, preventing lost windows or mouse cursors.

### 2.3 Complete Shutdown
1-click full shutdown that closes active sessions, terminates orphaned processes, and disables the virtual display driver.

---

## 3. Dynamic Resolution Switching (`DisplayResolutionManager`)

In addition to static INF modes, OpenWinSidecar uses Win32 display APIs (`EnumDisplayDevices`, `EnumDisplaySettings`, `ChangeDisplaySettingsEx`) to dynamically switch resolutions on the virtual monitor without requiring a driver reboot:

```csharp
DEVMODE mode = new DEVMODE();
mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();
mode.dmPelsWidth = width;
mode.dmPelsHeight = height;
mode.dmDisplayFrequency = refreshRate;
mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

int result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
```

### Curated iPad Resolution Presets:
- **iPad Air / 10th-11th Gen**: `1180 x 820` (@2x) / `2360 x 1640` (Native 2K)
- **iPad Pro 11" M4**: `1210 x 834` (@2x) / `2420 x 1668` (Native Retina)
- **iPad Pro 13" M4**: `1376 x 1032` (@2x) / `2752 x 2064` (Native 3K)
- **iPad Pro 12.9"**: `1366 x 1024` (@2x) / `2732 x 2048` (Native 3K)
- **iPad 10.2"**: `1080 x 810` (@2x) / `2160 x 1620` (Native Retina)
- **iPad mini 8.3"**: `1133 x 744` (@2x) / `2266 x 1488` (Native Retina)

---

## 4. Driver Installation & Recovery

### Installation via Devcon
```bat
drivers\VDD\control\Dependencies\devcon.exe install "drivers\VDD\control\SignedDrivers\x86\VDD\MttVDD.inf" Root\MttVDD
```

### Driver Restart & Recovery
If the virtual display becomes detached or inactive:
- `devcon.exe restart "ROOT\DISPLAY\0000"` power-cycles the virtual adapter.
- `pnputil.exe /restart-device "ROOT\DISPLAY\0000"` restarts the device node.
- Followed by an automatic re-trigger of `EnableExtendMode()`.
