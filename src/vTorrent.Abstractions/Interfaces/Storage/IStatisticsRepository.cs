using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface IStatisticsRepository
{
    Task RecordStatisticsSnapshotAsync(string? infoHash, int downloadRate, int uploadRate,
        long downloaded, long uploaded, int peers, int seeds);
    Task<List<StatisticsSnapshotRecord>> GetStatisticsHistoryAsync(string? infoHash,
        long fromTimestamp, long toTimestamp, int limit = 1000);
    Task CleanupOldStatisticsAsync(int keepDays = 7);
}
