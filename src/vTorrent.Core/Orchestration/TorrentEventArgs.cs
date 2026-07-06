// This file previously contained orchestrator event args types.
// They have been consolidated into vTorrent.Core.Events namespace.
// See: src/vTorrent.Core/Events/TorrentEventArgs.cs
//      src/vTorrent.Core/Events/StatisticsUpdatedEventArgs.cs
//      src/vTorrent.Core/Events/DhtStateChangedEventArgs.cs
//      src/vTorrent.Core/Events/PeerEventArgs.cs
//      src/vTorrent.Core/Events/AlertEventArgs.cs
//
// For backward compatibility, this file re-exports the consolidated types
// into the Orchestration namespace via type aliases.

using vTorrent.Abstractions.Models;
using vTorrent.Core.Events;

namespace vTorrent.Core.Orchestration;

// Keep OrchestratorTorrentEventArgs as a thin alias pointing to the new base type,
// so any internal code referencing it still compiles.
// New code should use vTorrent.Core.Events.TorrentEventArgs directly.

/// <summary>
/// Alias kept for backward compatibility. Use <see cref="Events.StatisticsUpdatedEventArgs"/> for new code.
/// </summary>
public class SessionStatisticsUpdatedEventArgs : Events.StatisticsUpdatedEventArgs
{
    public SessionStatisticsUpdatedEventArgs() { }
    public SessionStatisticsUpdatedEventArgs(SessionStatistics statistics) : base(statistics) { }
}
