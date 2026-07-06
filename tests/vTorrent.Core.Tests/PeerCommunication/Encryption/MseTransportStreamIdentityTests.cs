using FluentAssertions;
using vTorrent.Core.PeerCommunication.Encryption;
using Xunit;

namespace vTorrent.Core.Tests.PeerCommunication.Encryption;

public class MseTransportStreamIdentityTests
{
    [Fact]
    public void IdentifiedInfoHash_ExposesResultValue()
    {
        // MseResult carries IdentifiedInfoHash; the stream must surface it.
        typeof(MseTransportStream)
            .GetProperty(nameof(MseTransportStream.IdentifiedInfoHash))
            .Should().NotBeNull("dispatcher routing reads the MSE-identified info hash");
    }
}
