namespace vTorrent.Abstractions.Settings.Enums;

/// <summary>
/// Seed unchoking strategy selection.
/// Moved from Core/Upload/ChokingManager.cs to Abstractions as canonical definition.
/// IMPORTANT: Enum order matches original to preserve integer-backed serialization.
/// </summary>
public enum SeedChokingAlgorithm
{
    /// <summary>Unchoke peers we can upload to fastest</summary>
    FastestUpload,

    /// <summary>Fair rotation among peers</summary>
    RoundRobin,

    /// <summary>Prefer peers at 0% or 100% progress (Improving BitTorrent paper)</summary>
    AntiLeech
}
