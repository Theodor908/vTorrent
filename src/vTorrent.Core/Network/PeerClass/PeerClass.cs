using vTorrent.Core.Orchestration.Bandwidth;

namespace vTorrent.Core.Network.PeerClass;

public sealed class PeerClass
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int UploadLimitBytesPerSec { get; set; }
    public int DownloadLimitBytesPerSec { get; set; }
    public BandwidthChannel UploadChannel { get; }
    public BandwidthChannel DownloadChannel { get; }

    public PeerClass(int id, string name, int uploadLimit = 0, int downloadLimit = 0)
    {
        Id = id;
        Name = name;
        UploadLimitBytesPerSec = uploadLimit;
        DownloadLimitBytesPerSec = downloadLimit;
        UploadChannel = new BandwidthChannel($"class_{id}_ul");
        DownloadChannel = new BandwidthChannel($"class_{id}_dl");
        ApplyLimits();
    }

    public void ApplyLimits()
    {
        UploadChannel.Throttle = UploadLimitBytesPerSec;
        DownloadChannel.Throttle = DownloadLimitBytesPerSec;
    }
}
