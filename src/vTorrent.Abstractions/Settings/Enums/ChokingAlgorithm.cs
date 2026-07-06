namespace vTorrent.Abstractions.Settings.Enums;

/// <summary>
/// Download unchoking strategy selection.
/// Moved from Core/Upload/ChokingManager.cs to Abstractions as canonical definition.
/// </summary>
public enum ChokingAlgorithm
{
    /// <summary>Traditional fixed slot count (libtorrent: fixed_slots_choker)</summary>
    FixedSlots,

    /// <summary>Dynamic slots based on peer upload rates (libtorrent default)</summary>
    RateBased,

    /// <summary>Game-theory: minimize upload for maximum download (deprecated in libtorrent)</summary>
    BitTyrant,

    /// <summary>vTorrent-original: composite 5-signal scoring with phase-adaptive weights</summary>
    Adaptive
}
