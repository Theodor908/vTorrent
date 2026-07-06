using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Core.Network;
using vTorrent.Core.Registration;
using Xunit;

namespace vTorrent.Tests.Unit.Core.Registration;

public class CoreServiceRegistrationTests
{
    [Fact]
    public void AddVTorrentCore_RegistersUdpSocketManagerAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddVTorrentCore(NullLoggerFactory.Instance);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetService<UdpSocketManager>();
        var second = provider.GetService<UdpSocketManager>();

        first.Should().NotBeNull("the shared UDP socket must be wired in production");
        first.Should().BeSameAs(second, "it is session-scoped → a singleton");
    }
}
