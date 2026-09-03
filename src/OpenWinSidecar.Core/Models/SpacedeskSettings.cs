namespace OpenWinSidecar.Core.Models;

public class SpacedeskSettings
{
    public ServerStartMode ServerStartType { get; set; } = ServerStartMode.Off;
    public string EncryptionPassword { get; set; } = string.Empty;
    public bool IosUsbControlEnabled { get; set; } = true;
    public int VideoWallDisconnectDelay { get; set; } = 0;
    public int PrimaryPort { get; set; } = 28252;
}
