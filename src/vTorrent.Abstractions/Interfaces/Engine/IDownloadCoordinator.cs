using System;
using vTorrent.Abstractions.Events;

namespace vTorrent.Abstractions.Interfaces.Engine;

public interface IDownloadCoordinator : IDisposable
{
    public event EventHandler<PieceCompletedEventArgs> PieceCompleted;
}