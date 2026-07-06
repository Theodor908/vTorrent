using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface ITorrentRepository
{
    Task<List<TorrentRecord>> GetAllTorrentsAsync();
    Task<List<TorrentRecord>> GetTorrentsByIntentAsync(string intent);
    Task<TorrentRecord?> GetTorrentAsync(string infoHash);
    Task<bool> TorrentExistsAsync(string infoHash);
    Task InsertTorrentAsync(TorrentRecord torrent);
    Task<bool> TryInsertTorrentAsync(TorrentRecord torrent);
    Task UpdateTorrentIntentAsync(string infoHash, string intent, string? errorMessage = null);
    Task UpdateTorrentProgressAsync(string infoHash, double progress, bool isFinished, bool isSeed);
    Task UpdateStatsAsync(string infoHash, long uploaded, long downloaded);
    Task UpdateTorrentStatsAsync(string infoHash, TorrentStatsUpdate stats);
    Task UpdateTorrentOnShutdownAsync(string infoHash, TorrentShutdownData data);
    Task MarkTorrentCompletedAsync(string infoHash);
    Task UpdateTorrentMetadataAsync(string infoHash, string name, long totalSize, int pieceCount,
        int pieceSize, int fileCount, string? torrentFilePath);
    Task DeleteTorrentAsync(string infoHash);
    Task UpdateTorrentSettingsAsync(string infoHash, int maxConnections, int maxUploads,
        int downloadLimit, int uploadLimit, bool sequentialDownload, bool firstLastPiecePriority = false);
    Task UpdateSavePathAsync(string infoHash, string newSavePath);
}
