using OpenWinSidecar.Service.Capture;
using OpenWinSidecar.Service.Protocol;

namespace OpenWinSidecar.Service;

public sealed record HostStreamSettings
{
    public string? DeviceName { get; init; }
    public int Fps { get; init; } = 60;
    public int Quality { get; init; } = 80;
    public StreamCodec Codec { get; init; } = StreamCodec.HEVC;
    public int ColorDepth { get; init; } = 8;
    public double Zoom { get; init; } = 1;

    internal void Apply(ClientFrameSink sink)
    {
        sink.TargetFramerate = Fps;
        sink.Quality = Quality;
        sink.Codec = sink.HevcSupported ? Codec : StreamCodec.IntraTurbo;
        sink.ColorDepth = sink.Main10Supported ? ColorDepth : 8;
        sink.Zoom = Zoom;
    }
}
