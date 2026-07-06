namespace vTorrent.Abstractions.Interfaces.Engine;

/// <summary>
/// Write-path for all-time transfer counters.
/// Implemented by TorrentStatistics on ManagedTorrent, injected into engine's
/// session TorrentStatistics so RecordDownload/RecordUpload accumulate to the
/// persistent all-time counters in real time.
///
/// Read properties expose all-time payload totals for tracker announces (BEP 3).
/// </summary>
public interface ITransferAccumulator
{
    void AddDownload(int bytes);
    void AddUpload(int bytes);
    void AddPayloadDownload(int bytes);
    void AddPayloadUpload(int bytes);

    /// <summary>All-time payload bytes downloaded (for tracker announces).</summary>
    long TotalPayloadDownloaded { get; }

    /// <summary>All-time payload bytes uploaded (for tracker announces).</summary>
    long TotalPayloadUploaded { get; }
}
