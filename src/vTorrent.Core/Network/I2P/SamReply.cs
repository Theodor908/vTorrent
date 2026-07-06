// src/vTorrent.Core/Network/I2P/SamReply.cs
using System;
using System.Collections.Generic;

namespace vTorrent.Core.Network.I2P;

/// <summary>
/// Parses SAM protocol reply lines into key-value pairs.
/// Example: "HELLO REPLY RESULT=OK VERSION=3.3"
/// </summary>
public sealed class SamReply
{
    public string Command { get; }
    public string SubCommand { get; }
    public IReadOnlyDictionary<string, string> Values { get; }

    private SamReply(string command, string subCommand, Dictionary<string, string> values)
    {
        Command = command;
        SubCommand = subCommand;
        Values = values;
    }

    public string Result => Values.GetValueOrDefault("RESULT") ?? "";
    public bool IsOk => Result.Equals("OK", StringComparison.OrdinalIgnoreCase);

    public string GetValue(string key) =>
        Values.TryGetValue(key, out var val) ? val : throw new KeyNotFoundException($"SAM reply missing key: {key}");

    public string? GetValueOrDefault(string key) =>
        Values.GetValueOrDefault(key);

    public static SamReply Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            throw new FormatException("Empty SAM reply");

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException($"Invalid SAM reply: {line}");

        var command = parts[0];
        var subCommand = parts[1];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 2; i < parts.Length; i++)
        {
            var eqIdx = parts[i].IndexOf('=');
            if (eqIdx > 0)
            {
                var key = parts[i][..eqIdx];
                var value = parts[i][(eqIdx + 1)..];
                values[key] = value;
            }
        }

        return new SamReply(command, subCommand, values);
    }
}
