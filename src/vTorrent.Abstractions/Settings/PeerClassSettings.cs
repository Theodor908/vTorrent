using System.Collections.Generic;

namespace vTorrent.Abstractions.Settings;

public class PeerClassSettings
{
    public bool Enabled { get; set; } = false;
    public List<PeerClassDefinition> Classes { get; set; } = new();
}

public class PeerClassDefinition
{
    public string Name { get; set; } = "";
    public int UploadLimitBytesPerSec { get; set; } = 0;
    public int DownloadLimitBytesPerSec { get; set; } = 0;
    public List<string> IpRanges { get; set; } = new();
}
