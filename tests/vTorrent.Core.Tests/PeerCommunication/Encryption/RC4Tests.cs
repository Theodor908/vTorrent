using System;
using FluentAssertions;
using vTorrent.Core.PeerCommunication.Encryption.Primitives;
using Xunit;

namespace vTorrent.Tests.Unit.PeerCommunication.Encryption;

public class RC4Tests
{
    [Fact]
    public void Process_EncryptDecrypt_RoundTrip()
    {
        var key = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var plaintext = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var original = plaintext.ToArray();

        using var encryptor = new RC4(key);
        encryptor.Process(plaintext);
        plaintext.Should().NotEqual(original, "data should be encrypted");

        using var decryptor = new RC4(key);
        decryptor.Process(plaintext);
        plaintext.Should().Equal(original, "round-trip should restore original");
    }

    [Fact]
    public void Process_RFC6229_TestVector_Key40bit()
    {
        var key = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        using var rc4 = new RC4(key);

        var expected = new byte[]
        {
            0xb2, 0x39, 0x63, 0x05, 0xf0, 0x3d, 0xc0, 0x27,
            0xcc, 0xc3, 0x52, 0x4a, 0x0a, 0x11, 0x18, 0xa8
        };

        var zeros = new byte[16];
        rc4.Process(zeros);
        zeros.Should().Equal(expected);
    }

    [Fact]
    public void Discard_SkipsKeystream()
    {
        var key = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        using var rc4Manual = new RC4(key);
        var skip = new byte[1024];
        rc4Manual.Process(skip);
        var manualByte = new byte[1];
        rc4Manual.Process(manualByte);

        using var rc4Discard = new RC4(key);
        rc4Discard.Discard(1024);
        var discardByte = new byte[1];
        rc4Discard.Process(discardByte);

        discardByte.Should().Equal(manualByte, "Discard should advance to same position");
    }

    [Fact]
    public void Dispose_ZerosState()
    {
        var key = new byte[] { 0x01, 0x02, 0x03 };
        var rc4 = new RC4(key);
        var data = new byte[10];
        rc4.Process(data);
        rc4.Dispose();

        var postDispose = new byte[10];
        rc4.Process(postDispose);
        postDispose.Should().AllSatisfy(b => b.Should().Be(0));
    }
}
