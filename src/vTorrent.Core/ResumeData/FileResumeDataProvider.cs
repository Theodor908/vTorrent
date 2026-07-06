using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using vTorrent.Core.PeerCommunication.Utilities;

namespace vTorrent.Core.ResumeData;

/// <summary>
/// File-based implementation of IResumeDataProvider using the new ResumeData system.
/// Bridges the existing TorrentEngine interface with the bencoded resume file format.
/// </summary>
public class FileResumeDataProvider : IResumeDataProvider
{
    private readonly string _resumeFilePath;
    private readonly string _infoHash;
    private readonly int _pieceCount;
    private readonly ILogger? _logger;

    private TorrentResumeData? _cachedResumeData;
    private bool _isDirty;
    private DateTime _lastLoadTime;

    public FileResumeDataProvider(string resumeDirectory, string infoHash, int pieceCount, ILogger? logger = null)
    {
        _resumeFilePath = Path.Combine(resumeDirectory, $"{infoHash}.resume");
        _infoHash = infoHash;
        _pieceCount = pieceCount;
        _logger = logger;
    }

    /// <summary>
    /// Load have-pieces bitfield from storage (what pieces exist on disk).
    /// </summary>
    public async Task<Bitfield?> LoadHavePiecesAsync()
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData?.HavePieces == null || _cachedResumeData.HavePieces.Length == 0)
        {
            _logger?.LogDebug("No have-pieces found in resume data");
            return null;
        }

        try
        {
            var bitfield = new Bitfield(_pieceCount);
            var bitArray = _cachedResumeData.GetHavePiecesBitArray();

            for (int i = 0; i < Math.Min(bitArray.Length, _pieceCount); i++)
            {
                if (bitArray[i])
                    bitfield.SetPiece(i, true);
            }

            _logger?.LogInformation("Loaded {Count}/{Total} have-pieces from resume data",
                bitfield.CompletePieces, _pieceCount);

            return bitfield;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse have-pieces from resume data");
            return null;
        }
    }

    /// <summary>
    /// Load verified-pieces bitfield from storage (seed-mode lazy tracker).
    /// Returns null for non-seed-mode torrents (VerifiedPieces not populated).
    /// </summary>
    public async Task<Bitfield?> LoadVerifiedPiecesAsync()
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData?.VerifiedPieces == null || _cachedResumeData.VerifiedPieces.Length == 0)
        {
            _logger?.LogDebug("No verified-pieces found in resume data");
            return null;
        }

        try
        {
            var bitfield = new Bitfield(_pieceCount);
            var bitArray = TorrentResumeData.BytesToBitArrayMsbFirst(
                _cachedResumeData.VerifiedPieces, _pieceCount);

            for (int i = 0; i < Math.Min(bitArray.Length, _pieceCount); i++)
            {
                if (bitArray[i])
                    bitfield.SetPiece(i, true);
            }

            _logger?.LogInformation("Loaded {Count}/{Total} verified-pieces from resume data",
                bitfield.CompletePieces, _pieceCount);

            return bitfield;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse verified-pieces from resume data");
            return null;
        }
    }

    /// <summary>
    /// Save verified pieces bitfield to storage
    /// </summary>
    public async Task SaveVerifiedPiecesAsync(Bitfield bitfield)
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
        {
            _cachedResumeData = new TorrentResumeData
            {
                InfoHash = _infoHash,
                PieceCount = _pieceCount
            };
        }

        // Bitfield.Data is already MSB-first (protocol-compatible) — no conversion needed
        _cachedResumeData.HavePieces = (byte[])bitfield.Data.Clone();
        _cachedResumeData.VerifiedPieces = (byte[])bitfield.Data.Clone();
        _isDirty = true;

        await SaveResumeDataAsync();

        _logger?.LogDebug("Saved {Count} verified pieces to resume data", bitfield.CompletePieces);
    }

    /// <summary>
    /// Load saved peer list from storage
    /// </summary>
    public async Task<List<SavedPeerInfo>> LoadSavedPeersAsync()
    {
        await EnsureLoadedAsync();

        var peers = new List<SavedPeerInfo>();

        if (_cachedResumeData == null)
            return peers;

        // Load IPv4 peers
        if (_cachedResumeData.Peers != null && _cachedResumeData.Peers.Length > 0)
        {
            var ipv4Peers = ResumeDataSerializer.DeserializePeersCompact(_cachedResumeData.Peers, isIPv6: false);
            peers.AddRange(ipv4Peers);
        }

        // Load IPv6 peers
        if (_cachedResumeData.Peers6 != null && _cachedResumeData.Peers6.Length > 0)
        {
            var ipv6Peers = ResumeDataSerializer.DeserializePeersCompact(_cachedResumeData.Peers6, isIPv6: true);
            peers.AddRange(ipv6Peers);
        }

        _logger?.LogDebug("Loaded {Count} saved peers from resume data", peers.Count);
        return peers;
    }

    /// <summary>
    /// Save peer list to storage
    /// </summary>
    public async Task SavePeersAsync(List<SavedPeerInfo> peers)
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
        {
            _cachedResumeData = new TorrentResumeData
            {
                InfoHash = _infoHash,
                PieceCount = _pieceCount
            };
        }

        // Serialize to compact format
        _cachedResumeData.Peers = ResumeDataSerializer.SerializePeersCompact(peers);
        _cachedResumeData.Peers6 = ResumeDataSerializer.SerializePeers6Compact(peers);
        _isDirty = true;

        await SaveResumeDataAsync();

        _logger?.LogDebug("Saved {Count} peers to resume data", peers.Count);
    }

    /// <summary>
    /// Get the timestamp when the torrent was last active
    /// </summary>
    public async Task<DateTime> GetLastActiveTimeAsync()
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData?.LastSaved > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(_cachedResumeData.LastSaved).DateTime;
        }

        return DateTime.MinValue;
    }

    /// <summary>
    /// Update the last active timestamp
    /// </summary>
    public async Task UpdateLastActiveTimeAsync(DateTime timestamp)
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
        {
            _cachedResumeData = new TorrentResumeData
            {
                InfoHash = _infoHash,
                PieceCount = _pieceCount
            };
        }

        _cachedResumeData.LastSaved = new DateTimeOffset(timestamp).ToUnixTimeSeconds();
        _isDirty = true;

        await SaveResumeDataAsync();
    }

    /// <summary>
    /// Get torrent flags from resume data (seed mode, no verify, etc.)
    /// </summary>
    public async Task<TorrentFlags> GetFlagsAsync()
    {
        await EnsureLoadedAsync();
        return _cachedResumeData?.Flags ?? TorrentFlags.DefaultFlags;
    }

    /// <summary>
    /// Check if crash recovery is needed for this torrent
    /// Returns true if files may have been modified externally
    /// </summary>
    public async Task<bool> NeedsCrashRecoveryAsync()
    {
        await EnsureLoadedAsync();

        // If no resume data exists, we need full verification
        if (_cachedResumeData == null)
            return true;

        // If no last saved timestamp, assume we need verification
        if (_cachedResumeData.LastSaved == 0)
            return true;

        return false;
    }

    #region Additional Methods

    /// <summary>
    /// Get statistics from resume data
    /// </summary>
    public async Task<(long downloaded, long uploaded, TimeSpan activeTime)> GetStatisticsAsync()
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
            return (0, 0, TimeSpan.Zero);

        return (
            _cachedResumeData.TotalDownloaded,
            _cachedResumeData.TotalUploaded,
            TimeSpan.FromSeconds(_cachedResumeData.ActiveTimeSeconds)
        );
    }

    /// <summary>
    /// Update statistics in resume data
    /// </summary>
    public async Task UpdateStatisticsAsync(long downloaded, long uploaded, TimeSpan activeTime)
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
        {
            _cachedResumeData = new TorrentResumeData
            {
                InfoHash = _infoHash,
                PieceCount = _pieceCount
            };
        }

        _cachedResumeData.TotalDownloaded = downloaded;
        _cachedResumeData.TotalUploaded = uploaded;
        _cachedResumeData.ActiveTimeSeconds = (long)activeTime.TotalSeconds;
        _isDirty = true;
    }

    /// <summary>
    /// Get unfinished piece states for partial resume
    /// </summary>
    public async Task<Dictionary<int, UnfinishedPieceState>?> GetUnfinishedPiecesAsync()
    {
        await EnsureLoadedAsync();
        return _cachedResumeData?.UnfinishedPieces;
    }

    /// <summary>
    /// Save unfinished piece states
    /// </summary>
    public async Task SaveUnfinishedPiecesAsync(Dictionary<int, UnfinishedPieceState> unfinished)
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData == null)
        {
            _cachedResumeData = new TorrentResumeData
            {
                InfoHash = _infoHash,
                PieceCount = _pieceCount
            };
        }

        _cachedResumeData.UnfinishedPieces = unfinished;
        _isDirty = true;
    }

    /// <summary>
    /// Mark download as completed
    /// </summary>
    public async Task MarkCompletedAsync()
    {
        await EnsureLoadedAsync();

        if (_cachedResumeData != null)
        {
            _cachedResumeData.CompletedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _isDirty = true;
            await SaveResumeDataAsync();
        }
    }

    /// <summary>
    /// Force save all pending changes
    /// </summary>
    public async Task FlushAsync()
    {
        if (_isDirty)
        {
            await SaveResumeDataAsync();
        }
    }

    #endregion

    #region Private Methods

    private async Task EnsureLoadedAsync()
    {
        // Cache resume data with 1 second refresh interval
        if (_cachedResumeData != null && (DateTime.UtcNow - _lastLoadTime).TotalSeconds < 1)
            return;

        if (File.Exists(_resumeFilePath))
        {
            try
            {
                _cachedResumeData = await ResumeDataSerializer.LoadAsync(_resumeFilePath);
                _lastLoadTime = DateTime.UtcNow;
                _logger?.LogTrace("Loaded resume data from {Path}", _resumeFilePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load resume data from {Path}", _resumeFilePath);
                _cachedResumeData = null;
            }
        }
    }

    private async Task SaveResumeDataAsync()
    {
        if (_cachedResumeData == null)
            return;

        try
        {
            await ResumeDataSerializer.SaveAsync(_resumeFilePath, _cachedResumeData);
            _isDirty = false;
            _lastLoadTime = DateTime.UtcNow;
            _logger?.LogTrace("Saved resume data to {Path}", _resumeFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save resume data to {Path}", _resumeFilePath);
        }
    }

    #endregion
}
