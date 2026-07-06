using System.CommandLine;
using System.Linq;
using FluentAssertions;
using vTorrent.Cli;
using Xunit;

namespace vTorrent.CLI.Tests.Commands;

public class ProgramCommandRegistrationTests
{
    [Fact]
    public void AllTorrentSubcommands_AreRegisteredAsRootShortcuts()
    {
        // Arrange
        var root = Program.BuildRootCommand();

        var torrentGroup = root.Subcommands
            .FirstOrDefault(c => c.Name == "torrent");

        torrentGroup.Should().NotBeNull("a 'torrent' command group must exist");

        var torrentSubcommandNames = torrentGroup!.Subcommands
            .Select(c => c.Name)
            .ToHashSet();

        var rootCommandNames = root.Subcommands
            .Select(c => c.Name)
            .ToHashSet();

        // Act & Assert -- every torrent subcommand should also exist at root level
        foreach (var name in torrentSubcommandNames)
        {
            rootCommandNames.Should().Contain(name,
                $"torrent subcommand '{name}' should be registered as a root-level shortcut");
        }
    }

    [Fact]
    public void RootCommand_HasExpectedGroups()
    {
        // Arrange
        var root = Program.BuildRootCommand();

        var rootCommandNames = root.Subcommands
            .Select(c => c.Name)
            .ToHashSet();

        // Act & Assert
        rootCommandNames.Should().Contain("torrent", "torrent group should exist");
        rootCommandNames.Should().Contain("session", "session group should exist");
        rootCommandNames.Should().Contain("category", "category group should exist");
        rootCommandNames.Should().Contain("tag", "tag group should exist");
        rootCommandNames.Should().Contain("dht", "dht group should exist");
        rootCommandNames.Should().Contain("profile", "profile group should exist");
    }
}
