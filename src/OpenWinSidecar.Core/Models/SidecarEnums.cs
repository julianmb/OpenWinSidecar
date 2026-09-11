namespace OpenWinSidecar.Core.Models;

public enum SidecarServiceState
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
    NotFound = 8
}

public enum ServerStartMode
{
    Off = 0,
    On = 1
}

public enum CompressionEncodingType
{
    Unknown = 0,
    YUV420 = 1,
    YUV444 = 2,
    RGB = 3,
    H264 = 4,
    H265 = 5
}
