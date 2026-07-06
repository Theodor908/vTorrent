using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Linq;
using System.Text;

namespace vTorrent.Cli.Completion;

public static class ShellCompletionCommand
{
    public static Command Create()
    {
        var shellArg = new Argument<string>("shell")
        {
            Description = "Shell type: bash, zsh, fish, powershell"
        };

        var command = new Command("completion", "Generate shell completion scripts");
        command.Arguments.Add(shellArg);

        command.SetAction(parseResult =>
        {
            var shell = parseResult.GetValue(shellArg)?.ToLowerInvariant();
            var rootCommand = Program.BuildRootCommand();
            var script = GenerateScript(shell!, rootCommand);

            if (script == null)
            {
                Console.Error.WriteLine($"Unknown shell: {shell}");
                Console.Error.WriteLine("Supported: bash, zsh, fish, powershell");
                return 1;
            }

            Console.Write(script);
            return 0;
        });

        return command;
    }

    public static string? GenerateScript(string shell, RootCommand root)
    {
        return shell?.ToLowerInvariant() switch
        {
            "bash" => GenerateBash(root),
            "zsh" => GenerateZsh(root),
            "fish" => GenerateFish(root),
            "powershell" or "pwsh" => GeneratePowerShell(root),
            _ => null
        };
    }

    private static string GenerateBash(RootCommand root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# vtorrent bash completion");
        sb.AppendLine("# Add to ~/.bashrc: eval \"$(vtorrent completion bash)\"");
        sb.AppendLine("_vtorrent_completions() {");
        sb.AppendLine("    local cur=\"${COMP_WORDS[COMP_CWORD]}\"");

        var rootCmds = string.Join(" ", root.Subcommands.Select(c => c.Name));
        sb.AppendLine($"    local commands=\"{rootCmds}\"");

        foreach (var group in root.Subcommands.Where(c => c.Subcommands.Any()))
        {
            var subs = string.Join(" ", group.Subcommands.Select(c => c.Name));
            sb.AppendLine($"    local {group.Name}_cmds=\"{subs}\"");
        }

        sb.AppendLine();
        sb.AppendLine("    case \"${COMP_WORDS[1]}\" in");
        foreach (var group in root.Subcommands.Where(c => c.Subcommands.Any()))
        {
            sb.AppendLine($"        {group.Name}) COMPREPLY=($(compgen -W \"${group.Name}_cmds\" -- \"$cur\")) ;;");
        }
        sb.AppendLine("        *) COMPREPLY=($(compgen -W \"$commands\" -- \"$cur\")) ;;");
        sb.AppendLine("    esac");
        sb.AppendLine("}");
        sb.AppendLine("complete -F _vtorrent_completions vtorrent");

        return sb.ToString();
    }

    private static string GenerateZsh(RootCommand root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# vtorrent zsh completion");
        sb.AppendLine("# Add to ~/.zshrc: eval \"$(vtorrent completion zsh)\"");
        sb.AppendLine("_vtorrent() {");
        sb.AppendLine("    local -a commands=(");

        foreach (var cmd in root.Subcommands)
        {
            var desc = cmd.Description?.Replace("'", "\\'") ?? cmd.Name;
            sb.AppendLine($"        '{cmd.Name}:{desc}'");
        }

        sb.AppendLine("    )");
        sb.AppendLine("    _describe 'command' commands");
        sb.AppendLine("}");
        sb.AppendLine("compdef _vtorrent vtorrent");

        return sb.ToString();
    }

    private static string GenerateFish(RootCommand root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# vtorrent fish completion");
        sb.AppendLine("# Save to ~/.config/fish/completions/vtorrent.fish");
        sb.AppendLine("# Or: vtorrent completion fish | source");
        sb.AppendLine();

        var rootNames = string.Join(" ", root.Subcommands.Select(c => c.Name));
        sb.AppendLine($"set -l commands {rootNames}");
        sb.AppendLine("complete -c vtorrent -f");
        sb.AppendLine();

        foreach (var cmd in root.Subcommands.Where(c => !c.Subcommands.Any()))
        {
            var desc = cmd.Description?.Replace("'", "\\'") ?? cmd.Name;
            sb.AppendLine($"complete -c vtorrent -n \"not __fish_seen_subcommand_from $commands\" -a \"{cmd.Name}\" -d \"{desc}\"");
        }

        foreach (var group in root.Subcommands.Where(c => c.Subcommands.Any()))
        {
            var desc = group.Description?.Replace("'", "\\'") ?? group.Name;
            sb.AppendLine($"complete -c vtorrent -n \"not __fish_seen_subcommand_from $commands\" -a \"{group.Name}\" -d \"{desc}\"");

            var subs = string.Join(" ", group.Subcommands.Select(c => c.Name));
            sb.AppendLine($"complete -c vtorrent -n \"__fish_seen_subcommand_from {group.Name}\" -a \"{subs}\"");
        }

        return sb.ToString();
    }

    private static string GeneratePowerShell(RootCommand root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# vtorrent PowerShell completion");
        sb.AppendLine("# Add to $PROFILE: vtorrent completion powershell | Invoke-Expression");
        sb.AppendLine("Register-ArgumentCompleter -Native -CommandName vtorrent -ScriptBlock {");
        sb.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        sb.AppendLine("    $commands = @(");

        foreach (var cmd in root.Subcommands)
        {
            var desc = cmd.Description?.Replace("'", "\\'") ?? cmd.Name;
            sb.AppendLine($"        @{{Name='{cmd.Name}'; Desc='{desc}'}}");
        }

        sb.AppendLine("    )");
        sb.AppendLine("    $commands | Where-Object { $_.Name -like \"$wordToComplete*\" } |");
        sb.AppendLine("        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_.Name, $_.Name, 'ParameterValue', $_.Desc) }");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
