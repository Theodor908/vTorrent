using System.Net;
using vTorrent.Abstractions.Interfaces;

namespace vTorrent.Core.Session;

public record ExternalIpVoteRecord(string Ip, int VoteCount, long LastSeenUnix);

public class ExternalIpVoter : IExternalIpVoter
{
    private readonly object _lock = new();
    private readonly Dictionary<string, VoteEntry> _votes = new();
    private IPAddress? _currentConsensus;

    public event Action<IPAddress>? ConsensusChanged;

    public void AddVote(IPAddress ip, string source)
    {
        IPAddress? newConsensus;
        bool changed;

        lock (_lock)
        {
            var key = ip.ToString();
            if (_votes.TryGetValue(key, out var entry))
            {
                entry.VoteCount++;
                entry.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            else
            {
                _votes[key] = new VoteEntry
                {
                    Ip = ip,
                    VoteCount = 1,
                    LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };
            }

            newConsensus = ComputeConsensus();
            changed = !IpEquals(newConsensus, _currentConsensus);
            if (changed)
                _currentConsensus = newConsensus;
        }

        if (changed && newConsensus != null)
            ConsensusChanged?.Invoke(newConsensus);
    }

    public IPAddress? GetConsensusIp()
    {
        lock (_lock)
        {
            return _currentConsensus ?? ComputeConsensus();
        }
    }

    public void HydrateFromRecords(IEnumerable<ExternalIpVoteRecord> records)
    {
        lock (_lock)
        {
            foreach (var record in records)
            {
                if (IPAddress.TryParse(record.Ip, out var ip))
                {
                    _votes[record.Ip] = new VoteEntry
                    {
                        Ip = ip,
                        VoteCount = record.VoteCount,
                        LastSeenUnix = record.LastSeenUnix
                    };
                }
            }
            _currentConsensus = ComputeConsensus();
        }
    }

    public List<ExternalIpVoteRecord> ExportToRecords()
    {
        lock (_lock)
        {
            return _votes.Values
                .Select(v => new ExternalIpVoteRecord(v.Ip.ToString(), v.VoteCount, v.LastSeenUnix))
                .ToList();
        }
    }

    private IPAddress? ComputeConsensus()
    {
        VoteEntry? best = null;
        foreach (var entry in _votes.Values)
        {
            if (best == null
                || entry.VoteCount > best.VoteCount
                || (entry.VoteCount == best.VoteCount && entry.LastSeenUnix > best.LastSeenUnix))
            {
                best = entry;
            }
        }
        return best?.Ip;
    }

    private static bool IpEquals(IPAddress? a, IPAddress? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.Equals(b);
    }

    private class VoteEntry
    {
        public IPAddress Ip { get; set; } = null!;
        public int VoteCount { get; set; }
        public long LastSeenUnix { get; set; }
    }
}
