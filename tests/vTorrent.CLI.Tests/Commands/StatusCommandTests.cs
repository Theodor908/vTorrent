using FluentAssertions;
using Xunit;

namespace vTorrent.Cli.Tests.Commands;

public class StatusCommandTests
{
    [Fact]
    public void Create_ReturnsCommandNamedStatus()
    {
        var command = vTorrent.Cli.Commands.StatusCommand.Create();
        command.Name.Should().Be("status");
        command.Description.Should().NotBeNullOrEmpty();
    }
}
