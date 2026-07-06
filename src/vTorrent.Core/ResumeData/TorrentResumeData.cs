using System;
using System.Collections;
using System.Collections.Generic;

namespace vTorrent.Core.ResumeData;

/// <summary>
/// Torrent state flags following libtorrent's torrent_flags_t model.
/// Used for state persistence and restoration across sessions.
/// </summary>
[Flags]
public enum TorrentFlags : ulong
{
    /// <summary>No flags set</summary>
    None = 0,

    /// <summary>
    /// Torrent is in seed mode - all pieces assumed valid, verify on demand.
    /// Matches libtorrent's seed_mode flag.
    /// </summary>
    SeedMode = 1UL << 0,

    /// <summary>
    /// Upload-only mode - don't request any pieces.
    /// Typically set after I/O errors or explicitly by user.
    /// Matches libtorrent's upload_mode flag.
    /// </summary>
    UploadMode = 1UL << 1,

    /// <summary>
    /// Share mode - never download more than uploaded.
    /// Matches libtorrent's share_mode flag.
    /// </summary>
    ShareMode = 1UL << 2,

    /// <summary>
    /// Apply global IP filter to this torrent.
    /// Matches libtorrent's apply_ip_filter flag.
    /// </summary>
    ApplyIpFilter = 1UL << 3,

    /// <summary>
    /// Torrent is paused.
    /// Matches libtorrent's paused flag.
    /// </summary>
    Paused = 1UL << 4,

    /// <summary>
    /// Torrent is auto-managed by the queue system.
    /// Matches libtorrent's auto_managed flag.
    /// </summary>
    AutoManaged = 1UL << 5,

    /// <summary>
    /// Duplicate torrent is an error (don't allow adding same info hash twice).
    /// Matches libtorrent's duplicate_is_error flag.
    /// </summary>
    DuplicateIsError = 1UL << 6,

    /// <summary>
    /// Super seeding mode - optimized initial seeding.
    /// Matches libtorrent's super_seeding flag.
    /// </summary>
    SuperSeeding = 1UL << 8,

    /// <summary>
    /// Sequential download mode.
    /// Matches libtorrent's sequential_download flag.
    /// </summary>
    SequentialDownload = 1UL << 9,

    /// <summary>
    /// First and last pieces of each file are downloaded with highest priority.
    /// </summary>
    FirstLastPiecePriority = 1UL << 11,

    /// <summary>
    /// Stop when ready (transitioning to seeding state).
    /// Matches libtorrent's stop_when_ready flag.
    /// </summary>
    StopWhenReady = 1UL << 10,

    /// <summary>
    /// Disable DHT for this torrent.
    /// Matches libtorrent's disable_dht flag.
    /// </summary>
    DisableDht = 1UL << 19,

    /// <summary>
    /// Disable Local Service Discovery for this torrent.
    /// Matches libtorrent's disable_lsd flag.
    /// </summary>
    DisableLsd = 1UL << 20,

    /// <summary>
    /// Disable Peer Exchange for this torrent.
    /// Matches libtorrent's disable_pex flag.
    /// </summary>
    DisablePex = 1UL << 21,

    /// <summary>
    /// Don't verify files - trust resume data without hash checks.
    /// Matches libtorrent's no_verify_files flag.
    /// </summary>
    NoVerifyFiles = 1UL << 22,

    /// <summary>
    /// Default flags for new torrents.
    /// </summary>
    DefaultFlags = AutoManaged | ApplyIpFilter,
}

/// <summary>
/// Storage allocation mode for torrent files.
/// </summary>
public enum StorageMode
{
    /// <summary>
    /// Sparse file allocation - only allocate space as data is written.
    /// Most efficient for incomplete downloads.
    /// </summary>
    Sparse = 0,

    /// <summary>
    /// Full allocation - pre-allocate all space at torrent start.
    /// Avoids fragmentation but uses full disk space immediately.
    /// </summary>
    Allocate = 1,

    /// <summary>
    /// Read-only mode - files are not modified.
    /// </summary>
    ReadOnly = 2,
}

/// <summary>
/// Complete resume data for a torrent following libtorrent's add_torrent_params model.
/// Contains all state needed to resume a torrent without re-verification.
/// </summary>
public class TorrentResumeData
{
    #region Identity

    /// <summary>
    /// Info hash of the torrent (40-char hex string)
    /// </summary>
    public string InfoHash { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the torrent
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Comment from torrent metadata
    /// </summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Creator of the torrent file
    /// </summary>
    public string? CreatedBy { get; set; }

    #endregion

    #region Piece State

    /// <summary>
    /// Bitfield of completed pieces (1 = have, 0 = missing)
    /// Stored as byte[] for serialization
    /// </summary>
    public byte[]? HavePieces { get; set; }

    /// <summary>
    /// Bitfield of verified pieces (passed hash check)
    /// Used for seed mode verification
    /// </summary>
    public byte[]? VerifiedPieces { get; set; }

    /// <summary>
    /// Unfinished pieces with partial block state
    /// Key: piece index, Value: block bitfield (which blocks are downloaded)
    /// </summary>
    public Dictionary<int, UnfinishedPieceState>? UnfinishedPieces { get; set; }

    /// <summary>
    /// Total number of pieces in the torrent
    /// </summary>
    public int PieceCount { get; set; }

    /// <summary>
    /// Size of each piece in bytes
    /// </summary>
    public int PieceLength { get; set; }

    /// <summary>
    /// Block size used (typically 16KB)
    /// </summary>
    public int BlockSize { get; set; } = 16384;

    /// <summary>
    /// Last fully verified piece index during an interrupted recheck.
    /// null = no checkpoint (start from piece 0). Set during pause/cancel.
    /// Cleared on successful completion or fresh recheck.
    /// </summary>
    public int? CheckingCheckpoint { get; set; }

    #endregion

    #region Statistics (Persistent)

    /// <summary>
    /// All-time bytes uploaded
    /// </summary>
    public long TotalUploaded { get; set; }

    /// <summary>
    /// All-time bytes downloaded
    /// </summary>
    public long TotalDownloaded { get; set; }

    /// <summary>
    /// Total time the torrent has been active (seconds)
    /// </summary>
    public long ActiveTimeSeconds { get; set; }

    /// <summary>
    /// Total time in finished state (seconds)
    /// </summary>
    public long FinishedTimeSeconds { get; set; }

    /// <summary>
    /// Total time seeding (seconds)
    /// </summary>
    public long SeedingTimeSeconds { get; set; }

    #endregion

    #region Timestamps

    /// <summary>
    /// When the torrent was added (Unix timestamp)
    /// </summary>
    public long AddedTime { get; set; }

    /// <summary>
    /// When the download completed (Unix timestamp, 0 if not finished)
    /// </summary>
    public long CompletedTime { get; set; }

    /// <summary>
    /// Last time a complete copy was seen in swarm (Unix timestamp)
    /// </summary>
    public long LastSeenComplete { get; set; }

    /// <summary>
    /// Last download activity (Unix timestamp)
    /// </summary>
    public long LastDownload { get; set; }

    /// <summary>
    /// Last upload activity (Unix timestamp)
    /// </summary>
    public long LastUpload { get; set; }

    /// <summary>
    /// When this resume data was saved (Unix timestamp)
    /// </summary>
    public long LastSaved { get; set; }

    /// <summary>
    /// Last time the torrent was active (for resume provider)
    /// </summary>
    public DateTime LastActiveTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the torrent needs crash recovery (was active when app closed unexpectedly)
    /// </summary>
    public bool NeedsCrashRecovery { get; set; }

    #endregion

    #region Swarm Data (from tracker)

    /// <summary>
    /// Number of seeders reported by tracker
    /// </summary>
    public int NumComplete { get; set; }

    /// <summary>
    /// Number of leechers reported by tracker
    /// </summary>
    public int NumIncomplete { get; set; }

    /// <summary>
    /// Total completed downloads reported by tracker (scrape)
    /// </summary>
    public int NumDownloaded { get; set; }

    #endregion

    #region Configuration

    /// <summary>
    /// Path where torrent files are saved
    /// </summary>
    public string SavePath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the original .torrent file (optional)
    /// </summary>
    public string? TorrentFilePath { get; set; }

    /// <summary>
    /// Per-file download priorities (index -> priority)
    /// Priority: 0=skip, 1-7=priority levels (4=normal)
    /// </summary>
    public Dictionary<int, int>? FilePriorities { get; set; }

    /// <summary>
    /// Per-piece priorities (for streaming/prioritization)
    /// </summary>
    public byte[]? PiecePriorities { get; set; }

    /// <summary>
    /// Renamed files (original index -> new path)
    /// </summary>
    public Dictionary<int, string>? RenamedFiles { get; set; }

    #endregion

    #region State Flags

    /// <summary>
    /// Torrent state flags (libtorrent compatible).
    /// Replaces individual boolean properties for cleaner state management.
    /// </summary>
    public TorrentFlags Flags { get; set; } = TorrentFlags.DefaultFlags;

    /// <summary>
    /// Storage allocation mode for this torrent.
    /// </summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Sparse;

    /// <summary>
    /// Whether the torrent is paused.
    /// Convenience property that reads/writes the Paused flag.
    /// </summary>
    public bool IsPaused
    {
        get => Flags.HasFlag(TorrentFlags.Paused);
        set => Flags = value ? (Flags | TorrentFlags.Paused) : (Flags & ~TorrentFlags.Paused);
    }

    /// <summary>
    /// Whether user explicitly paused (vs auto-paused by queue manager).
    /// Stored separately as it's not a libtorrent flag.
    /// </summary>
    public bool UserPaused { get; set; }

    /// <summary>
    /// Whether sequential download is enabled.
    /// Convenience property that reads/writes the SequentialDownload flag.
    /// </summary>
    public bool SequentialDownload
    {
        get => Flags.HasFlag(TorrentFlags.SequentialDownload);
        set => Flags = value ? (Flags | TorrentFlags.SequentialDownload) : (Flags & ~TorrentFlags.SequentialDownload);
    }

    /// <summary>
    /// Whether first/last piece priority is enabled.
    /// Convenience property that reads/writes the FirstLastPiecePriority flag.
    /// </summary>
    public bool FirstLastPiecePriority
    {
        get => Flags.HasFlag(TorrentFlags.FirstLastPiecePriority);
        set => Flags = value ? (Flags | TorrentFlags.FirstLastPiecePriority) : (Flags & ~TorrentFlags.FirstLastPiecePriority);
    }

    /// <summary>
    /// Whether torrent is auto-managed by the queue system.
    /// Convenience property that reads/writes the AutoManaged flag.
    /// </summary>
    public bool AutoManaged
    {
        get => Flags.HasFlag(TorrentFlags.AutoManaged);
        set => Flags = value ? (Flags | TorrentFlags.AutoManaged) : (Flags & ~TorrentFlags.AutoManaged);
    }

    /// <summary>
    /// Whether seed mode is enabled (assume all pieces valid).
    /// Convenience property that reads/writes the SeedMode flag.
    /// </summary>
    public bool SeedMode
    {
        get => Flags.HasFlag(TorrentFlags.SeedMode);
        set => Flags = value ? (Flags | TorrentFlags.SeedMode) : (Flags & ~TorrentFlags.SeedMode);
    }

    /// <summary>
    /// Whether upload-only mode is enabled.
    /// Convenience property that reads/writes the UploadMode flag.
    /// </summary>
    public bool UploadMode
    {
        get => Flags.HasFlag(TorrentFlags.UploadMode);
        set => Flags = value ? (Flags | TorrentFlags.UploadMode) : (Flags & ~TorrentFlags.UploadMode);
    }

    /// <summary>
    /// Whether super seeding is enabled.
    /// Convenience property that reads/writes the SuperSeeding flag.
    /// </summary>
    public bool SuperSeeding
    {
        get => Flags.HasFlag(TorrentFlags.SuperSeeding);
        set => Flags = value ? (Flags | TorrentFlags.SuperSeeding) : (Flags & ~TorrentFlags.SuperSeeding);
    }

    /// <summary>
    /// Whether DHT is disabled for this torrent.
    /// </summary>
    public bool DisableDht
    {
        get => Flags.HasFlag(TorrentFlags.DisableDht);
        set => Flags = value ? (Flags | TorrentFlags.DisableDht) : (Flags & ~TorrentFlags.DisableDht);
    }

    /// <summary>
    /// Whether LSD (Local Service Discovery) is disabled for this torrent.
    /// </summary>
    public bool DisableLsd
    {
        get => Flags.HasFlag(TorrentFlags.DisableLsd);
        set => Flags = value ? (Flags | TorrentFlags.DisableLsd) : (Flags & ~TorrentFlags.DisableLsd);
    }

    /// <summary>
    /// Whether PEX (Peer Exchange) is disabled for this torrent.
    /// </summary>
    public bool DisablePex
    {
        get => Flags.HasFlag(TorrentFlags.DisablePex);
        set => Flags = value ? (Flags | TorrentFlags.DisablePex) : (Flags & ~TorrentFlags.DisablePex);
    }

    #endregion

    #region Per-Torrent Limits

    /// <summary>
    /// Maximum upload slots (-1 = use global)
    /// </summary>
    public int MaxUploads { get; set; } = -1;

    /// <summary>
    /// Maximum connections (-1 = use global)
    /// </summary>
    public int MaxConnections { get; set; } = -1;

    /// <summary>
    /// Upload rate limit in bytes/s (-1 = use global, 0 = unlimited)
    /// </summary>
    public int UploadLimit { get; set; } = -1;

    /// <summary>
    /// Download rate limit in bytes/s (-1 = use global, 0 = unlimited)
    /// </summary>
    public int DownloadLimit { get; set; } = -1;

    #endregion

    #region Peer Data

    /// <summary>
    /// Tracker URLs grouped by tiers
    /// </summary>
    public List<List<string>>? Trackers { get; set; }

    /// <summary>
    /// DHT bootstrap nodes (host:port pairs)
    /// </summary>
    public List<string>? DhtNodes { get; set; }

    /// <summary>
    /// Known IPv4 peers (compact format: 6 bytes per peer)
    /// </summary>
    public byte[]? Peers { get; set; }

    /// <summary>
    /// Known IPv6 peers (compact format: 18 bytes per peer)
    /// </summary>
    public byte[]? Peers6 { get; set; }

    /// <summary>
    /// Banned peers (compact format)
    /// </summary>
    public byte[]? BannedPeers { get; set; }

    /// <summary>
    /// HTTP seed URLs
    /// </summary>
    public List<string>? HttpSeeds { get; set; }

    /// <summary>
    /// Web seed URLs
    /// </summary>
    public List<string>? UrlSeeds { get; set; }

    /// <summary>
    /// Maximum size of .torrent file bytes to embed in resume data.
    /// Prevents bloating periodic saves for torrents with huge metadata.
    /// </summary>
    public const int MaxEmbedTorrentFileSize = 1_048_576; // 1 MB

    /// <summary>
    /// Raw .torrent file bytes embedded in resume data.
    /// Eliminates separate .torrent file read on startup (libtorrent parity:
    /// write_resume_data.cpp stores info dict as preformatted bencode).
    /// We store the full .torrent file since TorrentParser.FromBDictionary
    /// needs announce, announce-list, url-list etc. beyond just the info dict.
    /// </summary>
    public byte[]? TorrentFileBytes { get; set; }

    #endregion

    #region Queue Position

    /// <summary>
    /// Position in download/seed queue
    /// </summary>
    public int QueuePosition { get; set; }

    #endregion

    #region Methods

    /// <summary>
    /// Converts HavePieces byte array to BitArray using MSB-first ordering (libtorrent format).
    ///
    /// IMPORTANT: This uses MSB-first bit ordering to match libtorrent's format.
    /// In MSB-first, bit 0 of piece index 0 is in the highest bit (0x80) of byte 0.
    /// .NET's BitArray uses LSB-first, so we must manually convert.
    /// </summary>
    public BitArray GetHavePiecesBitArray()
    {
        if (HavePieces == null || HavePieces.Length == 0)
            return new BitArray(PieceCount, false);

        return BytesToBitArrayMsbFirst(HavePieces, PieceCount);
    }

    /// <summary>
    /// Converts VerifiedPieces byte array to BitArray using MSB-first ordering.
    /// </summary>
    public BitArray GetVerifiedPiecesBitArray()
    {
        if (VerifiedPieces == null || VerifiedPieces.Length == 0)
            return new BitArray(PieceCount, false);

        return BytesToBitArrayMsbFirst(VerifiedPieces, PieceCount);
    }

    /// <summary>
    /// Sets HavePieces from a BitArray using MSB-first ordering (libtorrent format).
    /// </summary>
    public void SetHavePieces(BitArray bitfield)
    {
        HavePieces = BitArrayToBytesMsbFirst(bitfield);
    }

    /// <summary>
    /// Sets VerifiedPieces from a BitArray using MSB-first ordering.
    /// </summary>
    public void SetVerifiedPieces(BitArray bitfield)
    {
        VerifiedPieces = BitArrayToBytesMsbFirst(bitfield);
    }

    /// <summary>
    /// Gets number of completed pieces from HavePieces bitfield.
    /// </summary>
    public int GetCompletedPieceCount()
    {
        if (HavePieces == null)
            return 0;

        int count = 0;
        int maxPiece = Math.Min(PieceCount, HavePieces.Length * 8);

        for (int i = 0; i < maxPiece; i++)
        {
            // MSB-first: bit i is at byte[i/8], bit position (7 - (i % 8))
            int byteIndex = i / 8;
            int bitPosition = 7 - (i % 8);

            if ((HavePieces[byteIndex] & (1 << bitPosition)) != 0)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Calculate progress (0.0 to 1.0)
    /// </summary>
    public float GetProgress()
    {
        if (PieceCount <= 0)
            return 0f;
        return (float)GetCompletedPieceCount() / PieceCount;
    }

    #endregion

    #region Static Helpers for MSB-First Bit Ordering

    /// <summary>
    /// Converts a byte array with MSB-first bit ordering to a BitArray.
    ///
    /// In MSB-first (libtorrent/BitTorrent format):
    /// - Bit 0 is stored in byte 0, bit 7 (0x80)
    /// - Bit 1 is stored in byte 0, bit 6 (0x40)
    /// - Bit 7 is stored in byte 0, bit 0 (0x01)
    /// - Bit 8 is stored in byte 1, bit 7 (0x80)
    ///
    /// .NET BitArray uses LSB-first, so direct conversion is wrong.
    /// </summary>
    /// <param name="bytes">Byte array with MSB-first bit ordering</param>
    /// <param name="bitCount">Number of valid bits (piece count)</param>
    /// <returns>BitArray with correct piece assignments</returns>
    public static BitArray BytesToBitArrayMsbFirst(byte[] bytes, int bitCount)
    {
        var result = new BitArray(bitCount, false);
        int maxBit = Math.Min(bitCount, bytes.Length * 8);

        for (int i = 0; i < maxBit; i++)
        {
            int byteIndex = i / 8;
            int bitPosition = 7 - (i % 8);  // MSB-first: bit 0 at position 7

            if ((bytes[byteIndex] & (1 << bitPosition)) != 0)
            {
                result[i] = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a BitArray to a byte array with MSB-first bit ordering (libtorrent format).
    /// </summary>
    /// <param name="bitfield">BitArray to convert</param>
    /// <returns>Byte array with MSB-first bit ordering</returns>
    public static byte[] BitArrayToBytesMsbFirst(BitArray bitfield)
    {
        var bytes = new byte[(bitfield.Length + 7) / 8];

        for (int i = 0; i < bitfield.Length; i++)
        {
            if (bitfield[i])
            {
                int byteIndex = i / 8;
                int bitPosition = 7 - (i % 8);  // MSB-first: bit 0 at position 7
                bytes[byteIndex] |= (byte)(1 << bitPosition);
            }
        }

        return bytes;
    }

    #endregion
}

/// <summary>
/// State of an unfinished (partially downloaded) piece.
/// Block bitfields use MSB-first ordering to match libtorrent format.
/// </summary>
public class UnfinishedPieceState
{
    /// <summary>
    /// Piece index
    /// </summary>
    public int PieceIndex { get; set; }

    /// <summary>
    /// Bitfield of blocks that have been downloaded (MSB-first format)
    /// </summary>
    public byte[]? HaveBlocks { get; set; }

    /// <summary>
    /// Block size for this piece (usually 16KB)
    /// </summary>
    public int BlockSize { get; set; } = 16384;

    /// <summary>
    /// Number of blocks in this piece
    /// </summary>
    public int BlockCount { get; set; }

    /// <summary>
    /// Bytes downloaded in this piece
    /// </summary>
    public int BytesDownloaded { get; set; }

    /// <summary>
    /// Gets which blocks are completed using MSB-first ordering (libtorrent format).
    /// </summary>
    public BitArray GetHaveBlocksBitArray()
    {
        if (HaveBlocks == null || HaveBlocks.Length == 0)
            return new BitArray(BlockCount, false);

        // Use MSB-first ordering to match libtorrent format
        return TorrentResumeData.BytesToBitArrayMsbFirst(HaveBlocks, BlockCount);
    }

    /// <summary>
    /// Sets HaveBlocks from a BitArray using MSB-first ordering (libtorrent format).
    /// </summary>
    public void SetHaveBlocks(BitArray blockBits)
    {
        // Use MSB-first ordering to match libtorrent format
        HaveBlocks = TorrentResumeData.BitArrayToBytesMsbFirst(blockBits);
    }
}
