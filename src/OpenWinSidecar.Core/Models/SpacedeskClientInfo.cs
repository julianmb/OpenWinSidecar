namespace OpenWinSidecar.Core.Models;

public class SpacedeskClientInfo
{
    public string DeviceGuid { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public int CompressionQuality { get; set; }
    public CompressionEncodingType EncodingType { get; set; } = CompressionEncodingType.Unknown;
    
    public bool IsConnected { get; set; }
    public int VideoPresentTargetId { get; set; }
    public string AdapterLuid { get; set; } = string.Empty;

    public string ResolutionText => $"{Width} x {Height}";
    public string PositionText => $"({OriginX}, {OriginY})";
    public string StatusText => IsConnected ? "Connected" : "Disconnected";
    public string EncodingText => EncodingType switch
    {
        CompressionEncodingType.H264 => "H.264 / AVC",
        CompressionEncodingType.H265 => "H.265 / HEVC",
        CompressionEncodingType.YUV420 => "YUV 4:2:0",
        CompressionEncodingType.YUV444 => "YUV 4:4:4",
        CompressionEncodingType.RGB => "RGB",
        _ => "Auto / Default"
    };
}
