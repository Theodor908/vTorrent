namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Triggers for the FileOperation state machine.
/// Must pass through None between operations.
/// </summary>
public enum FileOpTrigger
{
    StartMove,          // None → Moving
    StartRecheck,       // None → Rechecking
    Finish,             // Moving/Rechecking → None
}
