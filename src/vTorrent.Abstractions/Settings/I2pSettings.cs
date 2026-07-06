namespace vTorrent.Abstractions.Settings;

public class I2pSettings
{
    public bool Enabled { get; set; } = false;
    public string SamHostname { get; set; } = "127.0.0.1";
    public int SamPort { get; set; } = 7656;
    public int InboundTunnelQuantity { get; set; } = 3;
    public int OutboundTunnelQuantity { get; set; } = 3;
    public int InboundTunnelLength { get; set; } = 3;
    public int OutboundTunnelLength { get; set; } = 3;
    public I2pDestinationMode DestinationMode { get; set; } = I2pDestinationMode.Rotating;
    public int RotationIntervalDays { get; set; } = 7;
    public bool AllowMixedMode { get; set; } = false;
    public int MaxActiveI2pTorrents { get; set; } = 3;
}

public enum I2pDestinationMode
{
    Persistent,
    Rotating,
    SessionTransient
}
