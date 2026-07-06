using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using vTorrent.Core.ResumeData;
using vTorrent.Core.PeerCommunication.Models;
using vTorrent.Core.PeerCommunication.Utilities;

namespace vTorrent.Core.Orchestration;

/// <summary>
/// Bridges ManagedTorrent resume data to IResumeDataProvider interface.
/// Allows TorrentEngine to use orchestrator-managed resume data.
/// </summary>
internal class ManagedTorrentResumeProvider : IResumeDataProvider
{
    private readonly ManagedTorrent _managed;

    public ManagedTorrentResumeProvider(ManagedTorrent managed)
    {
        _managed = managed ?? throw new ArgumentNullException(nameof(managed));
    }

    /// <summary>
    /// Load have-pieces bitfield from resume data.
    /// Reads exclusively from HavePieces (what pieces exist on disk).
    /// Used by all non-seed-mode resume paths.
    /// </summary>
    public Task<Bitfield?> LoadHavePiecesAsync()
    {
        var resumeData = _managed.ResumeData;
        var pieceBytes = resumeData.HavePieces;

        if (pieceBytes == null || pieceBytes.Length == 0)
            return Task.FromResult<Bitfield?>(null);

        var bitfield = new Bitfield(resumeData.PieceCount);
        var bitArray = TorrentResumeData.BytesToBitArrayMsbFirst(pieceBytes, resumeData.PieceCount);

        for (int i = 0; i < Math.Min(bitArray.Length, resumeData.PieceCount); i++)
        {
            if (bitArray[i])
                bitfield.SetPiece(i);
        }

        return Task.FromResult<Bitfield?>(bitfield);
    }

    /// <summary>
    /// Load verified-pieces bitfield from resume data.
    /// Reads exclusively from VerifiedPieces (seed-mode lazy verification tracker).
    /// Only meaningful for seed-mode torrents.
    /// </summary>
    public Task<Bitfield?> LoadVerifiedPiecesAsync()
    {
        var resumeData = _managed.ResumeData;
        var pieceBytes = resumeData.VerifiedPieces;

        if (pieceBytes == null || pieceBytes.Length == 0)
            return Task.FromResult<Bitfield?>(null);

        var bitfield = new Bitfield(resumeData.PieceCount);
        var bitArray = TorrentResumeData.BytesToBitArrayMsbFirst(pieceBytes, resumeData.PieceCount);

        for (int i = 0; i < Math.Min(bitArray.Length, resumeData.PieceCount); i++)
        {
            if (bitArray[i])
                bitfield.SetPiece(i);
        }

        return Task.FromResult<Bitfield?>(bitfield);
    }

    /// <summary>
    /// Save verified pieces bitfield to resume data.
    ///
    /// Uses MSB-first bit ordering to match libtorrent's format.
    /// </summary>
    public Task SaveVerifiedPiecesAsync(Bitfield bitfield)
    {
        if (bitfield == null)
            return Task.CompletedTask;

        // Convert Bitfield to BitArray first
        var bitArray = new BitArray(bitfield.PieceCount);
        for (int i = 0; i < bitfield.PieceCount; i++)
        {
            bitArray[i] = bitfield.HasPiece(i);
        }

        // Convert to bytes using MSB-first encoding (libtorrent format)
        var bytes = TorrentResumeData.BitArrayToBytesMsbFirst(bitArray);

        _managed.ResumeData.VerifiedPieces = bytes;
        _managed.ResumeData.HavePieces = bytes;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Load saved peers from resume data
    /// </summary>
    public Task<List<SavedPeerInfo>> LoadSavedPeersAsync()
    {
        var result = new List<SavedPeerInfo>();
        var resumeData = _managed.ResumeData;

        // Parse IPv4 peers (6 bytes per peer: 4 IP + 2 port)
        if (resumeData.Peers != null && resumeData.Peers.Length >= 6)
        {
            for (int i = 0; i + 6 <= resumeData.Peers.Length; i += 6)
            {
                var ip = new IPAddress(new ReadOnlySpan<byte>(resumeData.Peers, i, 4));
                int port = (resumeData.Peers[i + 4] << 8) | resumeData.Peers[i + 5];

                result.Add(new SavedPeerInfo
                {
                    IpAddress = ip.ToString(),
                    Port = port,
                    Source = "Resume",
                    LastSeen = DateTime.UtcNow
                });
            }
        }

        // Parse IPv6 peers (18 bytes per peer: 16 IP + 2 port)
        if (resumeData.Peers6 != null && resumeData.Peers6.Length >= 18)
        {
            for (int i = 0; i + 18 <= resumeData.Peers6.Length; i += 18)
            {
                var ip = new IPAddress(new ReadOnlySpan<byte>(resumeData.Peers6, i, 16));
                int port = (resumeData.Peers6[i + 16] << 8) | resumeData.Peers6[i + 17];

                result.Add(new SavedPeerInfo
                {
                    IpAddress = ip.ToString(),
                    Port = port,
                    Source = "Resume",
                    LastSeen = DateTime.UtcNow
                });
            }
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Save peers to resume data
    /// </summary>
    public Task SavePeersAsync(List<SavedPeerInfo> peers)
    {
        if (peers == null || peers.Count == 0)
            return Task.CompletedTask;

        var ipv4Peers = new List<byte>();
        var ipv6Peers = new List<byte>();

        foreach (var peer in peers)
        {
            if (!IPAddress.TryParse(peer.IpAddress, out var ip))
                continue;

            var portBytes = new byte[]
            {
                (byte)(peer.Port >> 8),
                (byte)(peer.Port & 0xFF)
            };

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                ipv4Peers.AddRange(ip.GetAddressBytes());
                ipv4Peers.AddRange(portBytes);
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                ipv6Peers.AddRange(ip.GetAddressBytes());
                ipv6Peers.AddRange(portBytes);
            }
        }

        if (ipv4Peers.Count > 0)
            _managed.ResumeData.Peers = ipv4Peers.ToArray();

        if (ipv6Peers.Count > 0)
            _managed.ResumeData.Peers6 = ipv6Peers.ToArray();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get last active time for smart verification
    /// </summary>
    public Task<DateTime> GetLastActiveTimeAsync()
    {
        // Use last active time if available, otherwise added time
        var lastActive = _managed.LastActiveTime ?? _managed.AddedTime;
        return Task.FromResult(lastActive);
    }

    /// <summary>
    /// Update last active timestamp
    /// </summary>
    public Task UpdateLastActiveTimeAsync(DateTime timestamp)
    {
        _managed.LastActiveTime = timestamp;
        _managed.ResumeData.LastSaved = new DateTimeOffset(timestamp).ToUnixTimeSeconds();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get torrent flags from resume data
    /// </summary>
    public Task<TorrentFlags> GetFlagsAsync()
    {
        return Task.FromResult(_managed.ResumeData.Flags);
    }

    public Task<bool> NeedsCrashRecoveryAsync()
    {
        // libtorrent alignment: resume-data AGE never forces re-verification. Only a torrent
        // that was never properly persisted (no LastSaved timestamp) is verified on load.
        // File integrity is checked separately by ValidateFilesExist (size) and CheckFilesModifiedAsync (mtime).
        return Task.FromResult(_managed.ResumeData.LastSaved <= 0);
    }
}
