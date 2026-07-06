namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Per-peer MSE support state. Session-only, not persisted.
/// </summary>
public enum MsePeerEncryptionSupport : byte
{
    Unknown = 0,
    Supported = 1,
    Unsupported = 2
}
