using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface IPeerCacheRepository
{
    Task<List<KnownPeerRecord>> GetKnownPeersAsync(string infoHash, int limit = 200);
    Task SaveKnownPeersAsync(string infoHash, IEnumerable<KnownPeerRecord> peers);
    Task<List<KnownPeerRecord>> GetKnownPeersForRestoreAsync(string infoHash, int limit = 500);
    Task PruneStaleKnownPeersAsync(string infoHash, int maxAgeDays = 7);
    Task IncrementPeerFailCountAsync(string infoHash, string ip, int port);
    Task BanPeerAsync(string ip, string? reason = null);
    Task<bool> IsPeerBannedAsync(string ip);
    Task<List<BannedPeerRecord>> GetBannedPeersAsync();
    Task UnbanPeerAsync(string ip);
    Task SaveDhtNodesAsync(IEnumerable<DhtNodeRecord> nodes);
    Task<List<DhtNodeRecord>> GetDhtNodesAsync(int limit = 400, int maxAgeDays = 7);
    Task PruneStaleDhtNodesAsync(int maxAgeDays = 7);
    Task SaveDhtStateAsync(string key, string value);
    Task<string?> GetDhtStateAsync(string key);
}
