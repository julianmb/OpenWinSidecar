namespace OpenWinSidecar.Core.Models;

public class DisplayMonitorInfo
{
    public int Index { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public bool IsVirtual { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BoundsX { get; set; }
    public int BoundsY { get; set; }
    public int RefreshRate { get; set; }
    public int DpiScalePercent { get; set; } = 100;
    public List<DisplayModeInfo> SupportedModes { get; set; } = new();

    public string ResolutionText => $"{Width} x {Height}";
    public string SummaryText => $"{DisplayName} • {Width}x{Height} @ {RefreshRate}Hz ({DpiScalePercent}%)";
}
