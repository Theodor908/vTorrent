using FluentAssertions;
using Xunit;
using vTorrent.Cli.Completion;

namespace vTorrent.Cli.Tests.Completion;

public class ShellCompletionCommandTests
{
    [Fact]
    public void Create_ReturnsCommandNamedCompletion()
    {
        var command = ShellCompletionCommand.Create();
        command.Name.Should().Be("completion");
        command.Description.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("fish")]
    [InlineData("powershell")]
    public void GenerateScript_ReturnsNonEmptyForSupportedShells(string shell)
    {
        var script = ShellCompletionCommand.GenerateScript(shell, Program.BuildRootCommand());
        script.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateScript_ReturnsNullForUnsupportedShell()
    {
        var script = ShellCompletionCommand.GenerateScript("cmd", Program.BuildRootCommand());
        script.Should().BeNull();
    }

    [Fact]
    public void GenerateScript_ContainsRegisteredCommands()
    {
        var root = Program.BuildRootCommand();
        var script = ShellCompletionCommand.GenerateScript("bash", root);
        script.Should().Contain("list");
        script.Should().Contain("add");
        script.Should().Contain("status");
        script.Should().Contain("serve");
        script.Should().Contain("login");
    }

    [Fact]
    public void PowerShell_Script_Uses_Native_Flag()
    {
        var root = Program.BuildRootCommand();
        var script = ShellCompletionCommand.GenerateScript("powershell", root);
        script.Should().Contain("-Native",
            "PowerShell completion for native executables requires -Native flag on Register-ArgumentCompleter");
    }
}
