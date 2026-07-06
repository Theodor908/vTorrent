using System;
using System.Text;
using vTorrent.Core.PeerCommunication.Extensions;

namespace vTorrent.Core.PeerCommunication.Models;

/// <summary>
/// Flags representing the current state of a peer connection.
/// Used for UI display similar to qBittorrent's peer flags column.
/// </summary>
[Flags]
public enum PeerFlags
{
    None = 0,

    // Transfer state
    /// <summary>D - Currently downloading from this peer</summary>
    Downloading = 1 << 0,
    /// <summary>U - Currently uploading to this peer</summary>
    Uploading = 1 << 1,
    /// <summary>d - Peer is interested in our data</summary>
    PeerInterested = 1 << 2,
    /// <summary>u - We are interested in peer's data</summary>
    WeInterested = 1 << 3,

    // Choking state
    /// <summary>K - Peer is unchoking us (we can request)</summary>
    PeerUnchoking = 1 << 4,
    /// <summary>? - We are unchoking peer (peer can request)</summary>
    WeUnchoking = 1 << 5,
    /// <summary>O - Optimistic unchoke slot</summary>
    OptimisticUnchoke = 1 << 6,

    // Connection state
    /// <summary>S - Snubbed (no data received for a while)</summary>
    Snubbed = 1 << 7,
    /// <summary>I - Incoming connection (peer connected to us)</summary>
    Incoming = 1 << 8,
    /// <summary>E - Encrypted connection (RC4 or similar)</summary>
    Encrypted = 1 << 9,
    /// <summary>e - Using μTP (uTP) protocol</summary>
    UTP = 1 << 10,

    // Peer source
    /// <summary>X - Discovered via Peer Exchange (PEX)</summary>
    FromPEX = 1 << 11,
    /// <summary>H - Discovered via DHT</summary>
    FromDHT = 1 << 12,
    /// <summary>T - Discovered via tracker</summary>
    FromTracker = 1 << 13,
    /// <summary>L - Discovered via Local Service Discovery (LSD)</summary>
    FromLSD = 1 << 14,

    // Special states
    /// <summary>Peer is a seed (has 100% of torrent)</summary>
    Seed = 1 << 15,
    /// <summary>P - Connection established via holepunch</summary>
    Holepunch = 1 << 16,
    /// <summary>Connection is on a local network</summary>
    LocalNetwork = 1 << 17
}

/// <summary>
/// Extension methods for PeerFlags
/// </summary>
public static class PeerFlagsExtensions
{
    /// <summary>
    /// Convert flags to a qBittorrent-style flag string (e.g., "D K E X")
    /// </summary>
    public static string ToFlagString(this PeerFlags flags)
    {
        if (flags == PeerFlags.None)
            return string.Empty;

        var sb = new StringBuilder(32);

        // Transfer state
        if (flags.HasFlag(PeerFlags.Downloading)) sb.Append("D ");
        if (flags.HasFlag(PeerFlags.Uploading)) sb.Append("U ");
        if (flags.HasFlag(PeerFlags.PeerInterested)) sb.Append("d ");
        if (flags.HasFlag(PeerFlags.WeInterested)) sb.Append("u ");

        // Choking state
        if (flags.HasFlag(PeerFlags.PeerUnchoking)) sb.Append("K ");
        if (flags.HasFlag(PeerFlags.WeUnchoking)) sb.Append("? ");
        if (flags.HasFlag(PeerFlags.OptimisticUnchoke)) sb.Append("O ");

        // Connection state
        if (flags.HasFlag(PeerFlags.Snubbed)) sb.Append("S ");
        if (flags.HasFlag(PeerFlags.Incoming)) sb.Append("I ");
        if (flags.HasFlag(PeerFlags.Encrypted)) sb.Append("E ");
        if (flags.HasFlag(PeerFlags.UTP)) sb.Append("e ");
        if (flags.HasFlag(PeerFlags.Holepunch)) sb.Append("P ");

        // Source (only show one, priority order)
        if (flags.HasFlag(PeerFlags.FromLSD)) sb.Append("L ");
        else if (flags.HasFlag(PeerFlags.FromPEX)) sb.Append("X ");
        else if (flags.HasFlag(PeerFlags.FromDHT)) sb.Append("H ");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Get a description of all active flags
    /// </summary>
    public static string ToDescription(this PeerFlags flags)
    {
        if (flags == PeerFlags.None)
            return "No flags";

        var parts = new System.Collections.Generic.List<string>();

        if (flags.HasFlag(PeerFlags.Downloading)) parts.Add("Downloading");
        if (flags.HasFlag(PeerFlags.Uploading)) parts.Add("Uploading");
        if (flags.HasFlag(PeerFlags.PeerInterested)) parts.Add("Peer interested");
        if (flags.HasFlag(PeerFlags.WeInterested)) parts.Add("We interested");
        if (flags.HasFlag(PeerFlags.PeerUnchoking)) parts.Add("Unchoked by peer");
        if (flags.HasFlag(PeerFlags.WeUnchoking)) parts.Add("We unchoking");
        if (flags.HasFlag(PeerFlags.OptimisticUnchoke)) parts.Add("Optimistic unchoke");
        if (flags.HasFlag(PeerFlags.Snubbed)) parts.Add("Snubbed");
        if (flags.HasFlag(PeerFlags.Incoming)) parts.Add("Incoming");
        if (flags.HasFlag(PeerFlags.Encrypted)) parts.Add("Encrypted");
        if (flags.HasFlag(PeerFlags.UTP)) parts.Add("μTP");
        if (flags.HasFlag(PeerFlags.FromPEX)) parts.Add("From PEX");
        if (flags.HasFlag(PeerFlags.FromDHT)) parts.Add("From DHT");
        if (flags.HasFlag(PeerFlags.FromTracker)) parts.Add("From tracker");
        if (flags.HasFlag(PeerFlags.FromLSD)) parts.Add("From LSD");
        if (flags.HasFlag(PeerFlags.Seed)) parts.Add("Seed");
        if (flags.HasFlag(PeerFlags.Holepunch)) parts.Add("Holepunch");
        if (flags.HasFlag(PeerFlags.LocalNetwork)) parts.Add("Local network");

        return string.Join(", ", parts);
    }
}

/// <summary>
/// Helper to build PeerFlags from peer connection state
/// </summary>
public static class PeerFlagsBuilder
{
    /// <summary>
    /// Build flags from peer connection state
    /// </summary>
    public static PeerFlags Build(
        bool isDownloading = false,
        bool isUploading = false,
        bool peerInterested = false,
        bool weInterested = false,
        bool peerUnchoking = false,
        bool weUnchoking = false,
        bool isOptimisticUnchoke = false,
        bool isSnubbed = false,
        bool isIncoming = false,
        bool isEncrypted = false,
        bool isUTP = false,
        bool isSeed = false,
        Extensions.PeerSource source = Extensions.PeerSource.Unknown)
    {
        var flags = PeerFlags.None;

        if (isDownloading) flags |= PeerFlags.Downloading;
        if (isUploading) flags |= PeerFlags.Uploading;
        if (peerInterested) flags |= PeerFlags.PeerInterested;
        if (weInterested) flags |= PeerFlags.WeInterested;
        if (peerUnchoking) flags |= PeerFlags.PeerUnchoking;
        if (weUnchoking) flags |= PeerFlags.WeUnchoking;
        if (isOptimisticUnchoke) flags |= PeerFlags.OptimisticUnchoke;
        if (isSnubbed) flags |= PeerFlags.Snubbed;
        if (isIncoming) flags |= PeerFlags.Incoming;
        if (isEncrypted) flags |= PeerFlags.Encrypted;
        if (isUTP) flags |= PeerFlags.UTP;
        if (isSeed) flags |= PeerFlags.Seed;

        // Add source flags (PeerSource is a flags enum, can have multiple)
        if (source.HasFlag(Extensions.PeerSource.Tracker)) flags |= PeerFlags.FromTracker;
        if (source.HasFlag(Extensions.PeerSource.Dht)) flags |= PeerFlags.FromDHT;
        if (source.HasFlag(Extensions.PeerSource.Pex)) flags |= PeerFlags.FromPEX;
        if (source.HasFlag(Extensions.PeerSource.Lsd)) flags |= PeerFlags.FromLSD;
        if (source.HasFlag(Extensions.PeerSource.Incoming)) flags |= PeerFlags.Incoming;

        return flags;
    }
}
