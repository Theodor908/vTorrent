using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.Linq;
using System.Reflection;

namespace vTorrent.Cli.Help;

/// <summary>
/// Configures help output so that inherited global options are hidden
/// from subcommand help, while remaining visible in root help.
/// </summary>
public static class HelpConfigurator
{
    /// <summary>
    /// Walks all subcommands of <paramref name="root"/> and hides inherited
    /// global options from their help output. Root help is unaffected.
    /// </summary>
    public static void Configure(RootCommand root)
    {
        try
        {
            // Collect global option names defined directly on root (skip HelpOption/VersionOption)
            var globalOptions = root.Options
                .Where(o => o is not HelpOption && o.GetType().Name != "VersionOption")
                .ToList();

            if (globalOptions.Count == 0)
                return;

            // Resolve the reflection targets once
            var builderProp = typeof(HelpAction).GetProperty(
                "Builder",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (builderProp == null)
                return; // Graceful degradation: reflection target not found

            ConfigureSubcommands(root.Subcommands, globalOptions, builderProp);
        }
        catch (Exception)
        {
            // Graceful degradation: if anything goes wrong with reflection,
            // help simply shows the default (verbose) output.
        }
    }

    private static void ConfigureSubcommands(
        IEnumerable<Command> commands,
        List<Option> globalOptions,
        PropertyInfo builderProp)
    {
        foreach (var command in commands)
        {
            try
            {
                ConfigureCommand(command, globalOptions, builderProp);
            }
            catch (Exception)
            {
                // Skip this command; others still get configured.
            }

            // Recurse into nested subcommands
            if (command.Subcommands.Count > 0)
            {
                ConfigureSubcommands(command.Subcommands, globalOptions, builderProp);
            }
        }
    }

    private static void ConfigureCommand(
        Command command,
        List<Option> globalOptions,
        PropertyInfo builderProp)
    {
        // Give this command its own HelpOption so it gets an independent
        // HelpAction + HelpBuilder (the root's HelpOption is shared).
        var ownHelp = new HelpOption();
        command.Options.Add(ownHelp);

        // Access the HelpAction that HelpOption creates
        var helpAction = ownHelp.Action as HelpAction;
        if (helpAction == null)
            return;

        // Force lazy initialization of the HelpBuilder via the property getter
        var builder = builderProp.GetValue(helpAction);
        if (builder == null)
            return;

        // Find CustomizeSymbol(Symbol, string, string, string) on HelpBuilder
        var builderType = builder.GetType();
        var customizeMethod = builderType.GetMethod(
            "CustomizeSymbol",
            new[] { typeof(Symbol), typeof(string), typeof(string), typeof(string) });

        if (customizeMethod == null)
            return;

        // Hide each global option from this command's help
        foreach (var option in globalOptions)
        {
            customizeMethod.Invoke(builder, new object[] { option, "", "", "" });
        }
    }
}
