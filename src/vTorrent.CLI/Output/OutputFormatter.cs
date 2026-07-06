// src/vTorrent.CLI/Output/OutputFormatter.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace vTorrent.Cli.Output;

public enum OutputMode
{
    Table,
    Json,
    Quiet
}

public class OutputFormatter
{
    public OutputMode Mode { get; }
    public bool NoColor { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OutputFormatter(bool json = false, bool quiet = false, bool noColor = false)
    {
        if (quiet) Mode = OutputMode.Quiet;
        else if (json) Mode = OutputMode.Json;
        else Mode = OutputMode.Table;
        NoColor = noColor;
    }

    public void WriteJson<T>(T data)
    {
        Console.WriteLine(JsonSerializer.Serialize(data, JsonOptions));
    }

    public void WriteQuiet(string value)
    {
        Console.WriteLine(value);
    }

    public void WriteQuiet(IEnumerable<string> values)
    {
        foreach (var v in values) Console.WriteLine(v);
    }

    public void WriteTable(Table table)
    {
        AnsiConsole.Write(table);
    }

    public void WriteSuccess(string message)
    {
        if (Mode == OutputMode.Quiet) return;
        if (Mode == OutputMode.Json) return;
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(message)}");
    }

    public void WriteError(string message)
    {
        var stderr = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
        stderr.MarkupLine($"[red]✗ Error:[/] {Markup.Escape(message)}");
    }

    public void WriteSummary(string message)
    {
        if (Mode == OutputMode.Quiet) return;
        if (Mode == OutputMode.Json) return;
        AnsiConsole.MarkupLine($"\n[dim]{Markup.Escape(message)}[/]");
    }
}
