using System;
using System.Threading.Tasks;

namespace vTorrent.Abstractions.Interfaces.Storage;

/// <summary>
/// Unified database interface — inherits all repository sub-interfaces.
/// Existing consumers continue using ITorrentDatabase unchanged.
/// New consumers can depend on only the sub-interface they need.
/// </summary>
public interface ITorrentDatabase : ITorrentRepository, IMetadataRepository,
    IPeerCacheRepository, ICategoryRepository, ITagRepository,
    IQueueRepository, IStatisticsRepository, IAsyncDisposable
{
    Task InitializeAsync();
}
