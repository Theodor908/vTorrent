namespace vTorrent.Abstractions.Enums;

/// <summary>
/// Triggers for the TransferPhase state machine.
/// Each trigger maps to one or more declared transitions.
/// </summary>
public enum PhaseTrigger
{
    Allocate,           // Idle → Allocating
    CheckResume,        // Idle → CheckingResumeData
    FetchMetadata,      // Idle → FetchingMetadata
    CheckFiles,         // Allocating/CheckingResumeData → CheckingFiles
    Connect,            // CheckingFiles/CheckingResumeData → Connecting
    MetadataReceived,   // FetchingMetadata → Allocating (guard: has resume → CheckResume)
    StartDownloading,   // Connecting → Downloading (guard: !IsComplete)
    StartSeeding,       // Connecting → Seeding (guard: IsComplete)
    Complete,           // Downloading → Seeding
    Uncomplete,         // Seeding → Downloading (file priority change)
    Stop,               // Any active → Stopping
    Stopped,            // Stopping → Idle
    Reset,              // Any → Idle (error recovery, pause)
}
