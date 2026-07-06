namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Orthogonal condition axis for I2P availability on a torrent.
/// Independent of torrent state (a torrent can be Downloading + I2pUnavailable).
/// </summary>
public enum I2pAvailability
{
    /// <summary>Torrent doesn't use I2P.</summary>
    NotApplicable,

    /// <summary>SAM session active, I2P peers can connect.</summary>
    Available,

    /// <summary>SAM bridge down, I2P peers suspended.</summary>
    Unavailable,

    /// <summary>Attempting to re-establish SAM session.</summary>
    Reconnecting
}
