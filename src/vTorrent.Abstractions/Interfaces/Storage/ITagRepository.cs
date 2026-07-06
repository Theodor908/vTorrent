using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface ITagRepository
{
    Task<List<Tag>> GetAllTagsAsync();
    Task<Tag?> GetTagAsync(int id);
    Task<Tag?> GetTagByNameAsync(string name);
    Task<Tag> CreateTagAsync(string name, string? color = null);
    Task UpdateTagAsync(int id, string name, string? color);
    Task DeleteTagAsync(int id);
    Task<int> GetTorrentCountByTagAsync(int tagId);
    Task<List<TorrentRecord>> GetTorrentsByTagAsync(int tagId);
    Task<List<Tag>> GetTorrentTagsAsync(string infoHash);

    /// <summary>
    /// Gets all torrent-tag mappings in a single query, keyed by info hash.
    /// Used for batch loading during startup restoration.
    /// </summary>
    Task<Dictionary<string, List<Tag>>> GetAllTorrentTagsMappingAsync();

    Task AddTorrentTagAsync(string infoHash, int tagId);
    Task RemoveTorrentTagAsync(string infoHash, int tagId);
    Task SetTorrentTagsAsync(string infoHash, IEnumerable<int> tagIds);
}
