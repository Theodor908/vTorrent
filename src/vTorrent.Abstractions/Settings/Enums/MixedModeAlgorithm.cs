namespace vTorrent.Abstractions.Settings.Enums;

/// <summary>
/// TCP/uTP bandwidth sharing strategy.
/// libtorrent has PreferTcp and PeerProportional. PreferUtp is vTorrent-original.
/// </summary>
public enum MixedModeAlgorithm
{
    /// <summary>TCP gets priority, uTP gets leftovers</summary>
    PreferTcp,

    /// <summary>Split by peer count ratio (libtorrent default)</summary>
    PeerProportional,

    /// <summary>uTP gets priority, TCP gets leftovers (vTorrent-original, for capped connections)</summary>
    PreferUtp
}
