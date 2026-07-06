using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;

namespace vTorrent.Core.ResumeData;

/// <summary>
/// Provides resume data for torrents (verified pieces, peers, last active time)
/// Used by TorrentEngine for fast resume and smart verification
/// </summary>
public interface IResumeDataProvider
{
    /// <summary>
    /// Load verified pieces bitfield from storage
    /// </summary>
    Task<Bitfield?> LoadVerifiedPiecesAsync();

    /// <summary>
    /// Load have-pieces bitfield from storage (what pieces exist on disk).
    /// Used by all non-seed-mode resume paths. Reads HavePieces exclusively.
    /// </summary>
    Task<Bitfield?> LoadHavePiecesAsync();

    /// <summary>
    /// Save verified pieces bitfield to storage
    /// </summary>
    Task SaveVerifiedPiecesAsync(Bitfield bitfield);

    /// <summary>
    /// Load saved peer list from storage
    /// </summary>
    Task<List<SavedPeerInfo>> LoadSavedPeersAsync();

    /// <summary>
    /// Save peer list to storage
    /// </summary>
    Task SavePeersAsync(List<SavedPeerInfo> peers);

    /// <summary>
    /// Get the timestamp when the torrent was last active
    /// Used for smart verification (detect if files were modified externally)
    /// </summary>
    Task<DateTime> GetLastActiveTimeAsync();

    /// <summary>
    /// Update the last active timestamp
    /// Should be called on pause/stop
    /// </summary>
    Task UpdateLastActiveTimeAsync(DateTime timestamp);

    /// <summary>
    /// Get torrent flags from resume data (seed mode, no verify, etc.)
    /// Used for fast resume decision making
    /// </summary>
    Task<TorrentFlags> GetFlagsAsync();

    /// <summary>
    /// Check if crash recovery is needed for this torrent
    /// Returns true if files may have been modified externally
    /// </summary>
    Task<bool> NeedsCrashRecoveryAsync();
}

/// <summary>
/// Saved peer information for resume data
/// </summary>
public class SavedPeerInfo
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Source { get; set; } = "Tracker"; // "Tracker", "PEX", "DHT"
    public DateTime LastSeen { get; set; }
    public double Score { get; set; } // 0.0 - 1.0 (performance metric)
    public long BytesDownloaded { get; set; }
    public long BytesUploaded { get; set; }
}
