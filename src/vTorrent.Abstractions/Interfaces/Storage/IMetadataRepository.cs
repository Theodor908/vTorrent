using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface IMetadataRepository
{
    Task<List<TrackerRecord>> GetTrackersAsync(string infoHash);
    Task AddTrackersAsync(string infoHash, IEnumerable<(string url, int tier)> trackers);
    Task UpdateTrackerAnnounceAsync(string infoHash, string url,
        long lastAnnounce, long nextAnnounce, int? seeders, int? leechers);
    Task UpdateTrackerErrorAsync(string infoHash, string url, string errorMessage);
    Task<List<FileRecord>> GetFilesAsync(string infoHash);
    Task AddFilesAsync(string infoHash, IEnumerable<FileRecord> files);
    Task SaveFilesAsync(string infoHash, IEnumerable<FileRecord> files);
    Task UpdateFilePriorityAsync(string infoHash, int fileIndex, int priority);
    Task UpdateFileProgressAsync(string infoHash, int fileIndex, double progress);
}
