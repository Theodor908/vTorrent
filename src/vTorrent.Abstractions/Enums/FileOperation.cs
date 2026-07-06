namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Is a file-level operation in progress? Orthogonal to transfer phase.
/// </summary>
public enum FileOperation
{
    None,
    Moving,                // Relocating storage to new path
    Rechecking             // User-initiated force recheck
}
