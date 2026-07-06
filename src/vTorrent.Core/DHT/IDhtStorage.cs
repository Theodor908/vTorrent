using System.Collections.Generic;
using System.Net;

namespace vTorrent.Core.DHT;

/// <summary>
/// DHT storage interface following libtorrent's dht_storage_interface pattern.
/// Supports peer storage (BEP 5/33), infohash sampling (BEP 51),
/// and will later support mutable/immutable items (BEP 44).
/// </summary>
public interface IDhtStorage
{
    // === Peer methods (BEP 5/33) ===
    List<IPEndPoint> GetPeers(byte[] infoHash, int maxPeers = 0);
    bool HasPeers(byte[] infoHash);
    bool AnnouncePeer(byte[] infoHash, IPEndPoint peer, bool isSeed = false);
    BloomFilter GetSeedBloomFilter(byte[] infoHash);
    BloomFilter GetPeerBloomFilter(byte[] infoHash);

    // === BEP 51: Infohash sampling ===
    DhtSampleResult GetInfohashesSample();

    // === Housekeeping ===
    void Cleanup();
    void Clear();
    DhtStorageStats GetStats();
    int InfoHashCount { get; }
    int TotalPeerCount { get; }
}
