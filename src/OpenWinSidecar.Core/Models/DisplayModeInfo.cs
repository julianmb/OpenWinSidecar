namespace OpenWinSidecar.Core.Models;

public class DisplayModeInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public int BitsPerPixel { get; set; }

    public string ResolutionText => $"{Width} x {Height}";
    public string DisplayText => $"{Width} x {Height} @ {RefreshRate} Hz";

    public override string ToString() => DisplayText;
}
