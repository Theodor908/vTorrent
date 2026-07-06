using System;
using System.Numerics;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Encryption;

public class DiffieHellmanTests
{
    [Fact]
    public void PublicKey_IsExactly96Bytes()
    {
        var dh = new DiffieHellman();
        dh.PublicKey.Should().HaveCount(96);
    }

    [Fact]
    public void ComputeSharedSecret_BothSidesAgree()
    {
        var alice = new DiffieHellman();
        var bob = new DiffieHellman();

        var secretAlice = alice.ComputeSharedSecret(bob.PublicKey);
        var secretBob = bob.ComputeSharedSecret(alice.PublicKey);

        secretAlice.Should().Equal(secretBob);
        secretAlice.Should().HaveCount(96);
    }

    [Fact]
    public void PublicKey_DifferentInstances_DifferentKeys()
    {
        var a = new DiffieHellman();
        var b = new DiffieHellman();

        a.PublicKey.Should().NotEqual(b.PublicKey);
    }

    [Fact]
    public void ComputeSharedSecret_ZeroPaddedTo96Bytes()
    {
        var dh1 = new DiffieHellman();
        var dh2 = new DiffieHellman();
        var secret = dh1.ComputeSharedSecret(dh2.PublicKey);
        secret.Should().HaveCount(96);
    }
}
