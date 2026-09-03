Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class DisplayApi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplayDevices(IntPtr lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    public static extern bool EnumDisplaySettingsEx(string DeviceName, uint iModeNum, ref DEVMODE lpDevMode, uint dwFlags);
}
"@

Write-Output "=== Display Adapter Devices ==="
$devices = @()
for ($i = 0; $i -lt 10; $i++) {
    $dd = New-Object DisplayApi+DISPLAY_DEVICE
    $dd.cb = [System.Runtime.InteropServices.Marshal]::SizeOf($dd)
    if (-not [DisplayApi]::EnumDisplayDevices([IntPtr]::Zero, [uint32]$i, [ref]$dd, 0)) { break }
    if ($dd.DeviceName) {
        Write-Output ("[{0}] {1}  '{2}'  ID={3}  Key={4}" -f $i, $dd.DeviceName, $dd.DeviceString, $dd.DeviceID, $dd.DeviceKey)
        $devices += $dd
    }
}

$vdd = $devices | Where-Object { $_.DeviceID -like '*ROOT\DISPLAY*' } | Select-Object -First 1
if (-not $vdd) {
    $vdd = $devices | Where-Object { $_.DeviceString -match 'MTT|Virtual|Indirect' } | Select-Object -First 1
}

if ($vdd) {
    Write-Output ""
    Write-Output ("=== Raw modes for VDD device: {0} ({1}) ===" -f $vdd.DeviceName, $vdd.DeviceString)
    $modes = @()
    for ($m = 0; $m -lt 500; $m++) {
        $dm = New-Object DisplayApi+DEVMODE
        $dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)
        if (-not [DisplayApi]::EnumDisplaySettingsEx($vdd.DeviceName, [uint32]$m, [ref]$dm, 2)) { break }
        $modes += ("{0}x{1}@{2}" -f $dm.dmPelsWidth, $dm.dmPelsHeight, $dm.dmDisplayFrequency)
    }
    $modes | Sort-Object -Unique | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Output ("2048x1536@60 advertised: {0}" -f ($modes -contains '2048x1536@60'))
    Write-Output ("2732x2048@60 advertised: {0}" -f ($modes -contains '2732x2048@60'))
} else {
    Write-Output "NO VDD DEVICE FOUND"
}
