using System.CommandLine;
using FluentAssertions;
using Xunit;

namespace vTorrent.Cli.Tests.Help;

public class HelpConfiguratorTests
{
    [Fact]
    public void Subcommand_Help_DoesNotShow_InheritedOptions()
    {
        var root = Program.BuildRootCommand();
        vTorrent.Cli.Help.HelpConfigurator.Configure(root);

        var helpText = GetHelpText(root, "pause");

        // Should NOT contain global options
        helpText.Should().NotContain("--server");
        helpText.Should().NotContain("--token");
        helpText.Should().NotContain("--json");
        helpText.Should().NotContain("--timeout");
        helpText.Should().NotContain("--insecure");
        helpText.Should().NotContain("--ca-cert");
        helpText.Should().NotContain("--no-color");
        helpText.Should().NotContain("--verbose");

        // Should still contain its own argument and help
        helpText.Should().Contain("<hash>");
        helpText.Should().Contain("--help");
    }

    [Fact]
    public void Subcommand_Help_StillShows_LocalOptions()
    {
        var root = Program.BuildRootCommand();
        vTorrent.Cli.Help.HelpConfigurator.Configure(root);

        var helpText = GetHelpText(root, "list");

        // Local options should be present
        helpText.Should().Contain("--phase");
        helpText.Should().Contain("--follow");
        helpText.Should().Contain("--sort");

        // Globals should NOT be present
        helpText.Should().NotContain("--server");
        helpText.Should().NotContain("--token");
        helpText.Should().NotContain("--json");
    }

    [Fact]
    public void Root_Help_StillShows_GlobalOptions()
    {
        var root = Program.BuildRootCommand();
        vTorrent.Cli.Help.HelpConfigurator.Configure(root);

        var helpText = GetHelpText(root, null);

        // Root should still show global options
        helpText.Should().Contain("--server");
        helpText.Should().Contain("--json");
        helpText.Should().Contain("--timeout");
    }

    private static string GetHelpText(RootCommand root, string? subcommandName)
    {
        var sw = new System.IO.StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            if (subcommandName == null)
                root.Parse(new[] { "--help" }).Invoke();
            else
                root.Parse(new[] { subcommandName, "--help" }).Invoke();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return sw.ToString();
    }
}
