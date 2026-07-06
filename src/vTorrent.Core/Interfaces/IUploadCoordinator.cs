using System;

using System.Threading;

using System.Threading.Tasks;

using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.Engine;

namespace vTorrent.Core.Interfaces;

/// <summary>

/// Handles upload requests from peers.

/// Validates requests, reads from disk, and sends blocks to requesting peers.

/// </summary>

public interface IUploadCoordinator : IMessageHandler, IDisposable

{

    /// <summary>

    /// Total bytes uploaded this session.

    /// </summary>

    long BytesUploaded { get; }

    /// <summary>

    /// Number of currently active upload transfers.

    /// </summary>

    int ActiveUploads { get; }

    /// <summary>

    /// Maximum concurrent upload operations.

    /// </summary>

    int MaxConcurrentUploads { get; }

    /// <summary>

    /// Current upload rate in bytes per second.

    /// </summary>

    double UploadRate { get; }

    /// <summary>

    /// Start accepting upload requests.

    /// </summary>

    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>

    /// Stop accepting new upload requests and wait for current uploads to complete.

    /// </summary>

    Task StopAsync();

    /// <summary>

    /// Fired when a block has been uploaded to a peer.

    /// </summary>

    event EventHandler<BlockUploadedEventArgs> BlockUploaded;

}

/// <summary>

/// Event args for block upload completion.

/// </summary>

public class BlockUploadedEventArgs : EventArgs

{

    public IPeerConnection Peer { get; }

    public int PieceIndex { get; }

    public int Begin { get; }

    public int Length { get; }

    public BlockUploadedEventArgs(IPeerConnection peer, int pieceIndex, int begin, int length)

    {

        Peer = peer;

        PieceIndex = pieceIndex;

        Begin = begin;

        Length = length;

    }

}

/// <summary>
/// Event args for disk read failures during upload. libtorrent parity: file_error_alert.
/// </summary>
public class FileReadFailedEventArgs : EventArgs
{
    public int PieceIndex { get; }
    public string ErrorMessage { get; }

    public FileReadFailedEventArgs(int pieceIndex, string errorMessage)
    {
        PieceIndex = pieceIndex;
        ErrorMessage = errorMessage;
    }
}
