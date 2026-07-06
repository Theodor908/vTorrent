using System;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Encryption;

public class MseKeyDerivationTests
{
    private readonly byte[] _testInfoHash = SHA1.HashData(
        System.Text.Encoding.ASCII.GetBytes("test-info-hash"));

    [Fact]
    public void Hash_WithPrefix_ReturnsSHA1()
    {
        var data = new byte[] { 1, 2, 3 };
        var result = MseKeyDerivation.Hash("req1", data);
        result.Should().HaveCount(20);
    }

    [Fact]
    public void Hash_DifferentPrefixes_DifferentResults()
    {
        var data = new byte[] { 1, 2, 3 };
        var a = MseKeyDerivation.Hash("req1", data);
        var b = MseKeyDerivation.Hash("req2", data);
        a.Should().NotEqual(b);
    }

    [Fact]
    public void ComputeReq2Hash_IsSHA1OfReq2PlusInfoHash()
    {
        var result = MseKeyDerivation.ComputeReq2Hash(_testInfoHash);
        result.Should().HaveCount(20);

        var prefix = System.Text.Encoding.ASCII.GetBytes("req2");
        var input = prefix.Concat(_testInfoHash).ToArray();
        var expected = SHA1.HashData(input);
        result.Should().Equal(expected);
    }

    [Fact]
    public void ComputeTrackerObfuscatedHash_IsSHA1OfInfoHash()
    {
        var result = MseKeyDerivation.ComputeTrackerObfuscatedHash(_testInfoHash);
        var expected = SHA1.HashData(_testInfoHash);
        result.Should().Equal(expected);
    }

    [Fact]
    public void CreateRC4Pair_ProducesTwoDifferentCiphers()
    {
        var S = new byte[96];
        RandomNumberGenerator.Fill(S);

        var (outgoing, incoming) = MseKeyDerivation.CreateRC4Pair(S, _testInfoHash);

        outgoing.Should().NotBeNull();
        incoming.Should().NotBeNull();

        var dataA = new byte[16];
        var dataB = new byte[16];
        outgoing.Process(dataA);
        incoming.Process(dataB);
        dataA.Should().NotEqual(dataB);

        outgoing.Dispose();
        incoming.Dispose();
    }
}
