using System.Net;
using FluentAssertions;
using vTorrent.Core.Session;
using Xunit;

namespace vTorrent.Core.Tests.Session;

public class ExternalIpVoterTests
{
    [Fact]
    public void GetConsensusIp_NoVotes_ReturnsNull()
    {
        new ExternalIpVoter().GetConsensusIp().Should().BeNull();
    }

    [Fact]
    public void GetConsensusIp_SingleVote_ReturnsThatIp()
    {
        var voter = new ExternalIpVoter();
        var ip = IPAddress.Parse("1.2.3.4");
        voter.AddVote(ip, "tracker");
        voter.GetConsensusIp().Should().Be(ip);
    }

    [Fact]
    public void GetConsensusIp_MultipleSourcesSameIp_AggregatesVotes()
    {
        var voter = new ExternalIpVoter();
        var ip = IPAddress.Parse("1.2.3.4");
        voter.AddVote(ip, "tracker");
        voter.AddVote(ip, "peer_extension");
        voter.GetConsensusIp().Should().Be(ip);
    }

    [Fact]
    public void GetConsensusIp_HighestVoteCountWins()
    {
        var voter = new ExternalIpVoter();
        var ip1 = IPAddress.Parse("1.2.3.4");
        var ip2 = IPAddress.Parse("5.6.7.8");
        voter.AddVote(ip1, "tracker");
        voter.AddVote(ip2, "peer");
        voter.AddVote(ip2, "peer");
        voter.GetConsensusIp().Should().Be(ip2);
    }

    [Fact]
    public void GetConsensusIp_TieBreaksOnMostRecentlySeen()
    {
        var voter = new ExternalIpVoter();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        voter.HydrateFromRecords(new[]
        {
            new ExternalIpVoteRecord("1.2.3.4", 1, now - 10),
            new ExternalIpVoteRecord("5.6.7.8", 1, now)
        });
        voter.GetConsensusIp().Should().Be(IPAddress.Parse("5.6.7.8"));
    }

    [Fact]
    public void ConsensusChanged_FiresWhenConsensusChanges()
    {
        var voter = new ExternalIpVoter();
        IPAddress? firedIp = null;
        voter.ConsensusChanged += ip => firedIp = ip;
        voter.AddVote(IPAddress.Parse("1.2.3.4"), "tracker");
        firedIp.Should().Be(IPAddress.Parse("1.2.3.4"));
    }

    [Fact]
    public void ConsensusChanged_DoesNotFireWhenSameConsensus()
    {
        var voter = new ExternalIpVoter();
        voter.AddVote(IPAddress.Parse("1.2.3.4"), "tracker");
        int fireCount = 0;
        voter.ConsensusChanged += _ => fireCount++;
        voter.AddVote(IPAddress.Parse("1.2.3.4"), "peer");
        fireCount.Should().Be(0);
    }

    [Fact]
    public void HydrateFromRecords_RestoresPreviousVotes()
    {
        var voter = new ExternalIpVoter();
        voter.HydrateFromRecords(new[]
        {
            new ExternalIpVoteRecord("1.2.3.4", 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            new ExternalIpVoteRecord("5.6.7.8", 3, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100)
        });
        voter.GetConsensusIp().Should().Be(IPAddress.Parse("1.2.3.4"));
    }

    [Fact]
    public void ExportToRecords_SerializesCurrentState()
    {
        var voter = new ExternalIpVoter();
        voter.AddVote(IPAddress.Parse("1.2.3.4"), "tracker");
        voter.AddVote(IPAddress.Parse("1.2.3.4"), "peer");
        var records = voter.ExportToRecords();
        records.Should().HaveCount(1);
        records[0].Ip.Should().Be("1.2.3.4");
        records[0].VoteCount.Should().Be(2);
    }

    [Fact]
    public void AddVote_IPv6_WorksCorrectly()
    {
        var voter = new ExternalIpVoter();
        var ipv6 = IPAddress.Parse("2001:db8::1");
        voter.AddVote(ipv6, "peer");
        voter.GetConsensusIp().Should().Be(ipv6);
    }
}
