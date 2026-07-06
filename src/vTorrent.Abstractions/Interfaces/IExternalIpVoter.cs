using System.Net;

namespace vTorrent.Abstractions.Interfaces;

/// <summary>
/// Multi-source external IP consensus voter.
/// Aggregates IP reports from trackers (BEP 24), peers (BEP 10 yourip),
/// and DHT to determine our most likely public IP address.
/// </summary>
public interface IExternalIpVoter
{
    void AddVote(IPAddress ip, string source);
    IPAddress? GetConsensusIp();
    event Action<IPAddress>? ConsensusChanged;
}
