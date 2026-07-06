namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Triggers for the UserIntent state machine.
/// All transitions between Active/Paused/Queued are valid.
/// </summary>
public enum IntentTrigger
{
    Activate,           // → Active
    Pause,              // → Paused
    Queue,              // → Queued
}
