using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace vTorrent.Core;

/// <summary>
/// Represents a parsed command-line argument that can be either a torrent file or magnet URI.
/// </summary>
public class StartupItem
{
    /// <summary>
    /// The type of startup item.
    /// </summary>
    public StartupItemType Type { get; init; }

    /// <summary>
    /// The value - either a file path or magnet URI.
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// Whether the item is valid (file exists for torrent files, valid format for magnet links).
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Error message if the item is not valid.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Types of startup items that can be passed via command line.
/// </summary>
public enum StartupItemType
{
    /// <summary>
    /// A .torrent file path.
    /// </summary>
    TorrentFile,

    /// <summary>
    /// A magnet: URI.
    /// </summary>
    MagnetUri,

    /// <summary>
    /// Unknown or invalid argument.
    /// </summary>
    Unknown
}

/// <summary>
/// Result of parsing command-line arguments.
/// </summary>
public class CommandLineResult
{
    /// <summary>
    /// Successfully parsed startup items.
    /// </summary>
    public List<StartupItem> Items { get; } = new();

    /// <summary>
    /// Items that failed validation.
    /// </summary>
    public List<StartupItem> InvalidItems { get; } = new();

    /// <summary>
    /// Whether there are any valid items to process.
    /// </summary>
    public bool HasValidItems => Items.Count > 0;

    /// <summary>
    /// Whether there are any invalid items.
    /// </summary>
    public bool HasInvalidItems => InvalidItems.Count > 0;

    /// <summary>
    /// Gets all torrent file items.
    /// </summary>
    public IEnumerable<StartupItem> TorrentFiles => Items.Where(i => i.Type == StartupItemType.TorrentFile);

    /// <summary>
    /// Gets all magnet URI items.
    /// </summary>
    public IEnumerable<StartupItem> MagnetUris => Items.Where(i => i.Type == StartupItemType.MagnetUri);
}

/// <summary>
/// Handles parsing and validation of command-line arguments for file associations and magnet links.
/// </summary>
public static class CommandLineHandler
{
    /// <summary>
    /// Parses command-line arguments and returns a structured result.
    /// </summary>
    /// <param name="args">The command-line arguments to parse.</param>
    /// <returns>A CommandLineResult containing parsed items.</returns>
    public static CommandLineResult Parse(string[] args)
    {
        var result = new CommandLineResult();

        if (args == null || args.Length == 0)
            return result;

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            var item = ParseArgument(arg.Trim());

            if (item.IsValid)
            {
                result.Items.Add(item);
            }
            else if (item.Type != StartupItemType.Unknown)
            {
                // Only add to invalid items if we could identify the type
                result.InvalidItems.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses a single argument and determines its type and validity.
    /// </summary>
    private static StartupItem ParseArgument(string arg)
    {
        // Check if it's a magnet URI
        if (IsMagnetUri(arg))
        {
            return ParseMagnetUri(arg);
        }

        // Check if it's a file path (could be .torrent)
        if (IsTorrentFilePath(arg))
        {
            return ParseTorrentFile(arg);
        }

        // Unknown argument type
        return new StartupItem
        {
            Type = StartupItemType.Unknown,
            Value = arg,
            IsValid = false,
            ErrorMessage = "Unrecognized argument type"
        };
    }

    /// <summary>
    /// Checks if the argument looks like a magnet URI.
    /// </summary>
    public static bool IsMagnetUri(string arg)
    {
        return arg.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the argument looks like a .torrent file path.
    /// </summary>
    public static bool IsTorrentFilePath(string arg)
    {
        return arg.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses and validates a magnet URI.
    /// </summary>
    private static StartupItem ParseMagnetUri(string uri)
    {
        // Basic magnet URI validation
        // Format: magnet:?xt=urn:btih:HASH&...
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            return new StartupItem
            {
                Type = StartupItemType.MagnetUri,
                Value = uri,
                IsValid = false,
                ErrorMessage = "Invalid magnet URI format"
            };
        }

        // Check for required xt (exact topic) parameter with btih (BitTorrent info hash)
        var hasInfoHash = uri.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (!hasInfoHash)
        {
            return new StartupItem
            {
                Type = StartupItemType.MagnetUri,
                Value = uri,
                IsValid = false,
                ErrorMessage = "Magnet URI missing BitTorrent info hash"
            };
        }

        return new StartupItem
        {
            Type = StartupItemType.MagnetUri,
            Value = uri,
            IsValid = true
        };
    }

    /// <summary>
    /// Parses and validates a torrent file path.
    /// </summary>
    private static StartupItem ParseTorrentFile(string path)
    {
        // Normalize the path for cross-platform compatibility
        var normalizedPath = NormalizePath(path);

        // Check if file exists
        if (!File.Exists(normalizedPath))
        {
            return new StartupItem
            {
                Type = StartupItemType.TorrentFile,
                Value = normalizedPath,
                IsValid = false,
                ErrorMessage = $"File not found: {normalizedPath}"
            };
        }

        // Validate it's actually a file we can read
        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            if (fileInfo.Length == 0)
            {
                return new StartupItem
                {
                    Type = StartupItemType.TorrentFile,
                    Value = normalizedPath,
                    IsValid = false,
                    ErrorMessage = "Torrent file is empty"
                };
            }

            // Optionally validate it's a valid torrent file format
            // For now, we'll just trust the extension and let the actual loader handle deep validation
        }
        catch (Exception ex)
        {
            return new StartupItem
            {
                Type = StartupItemType.TorrentFile,
                Value = normalizedPath,
                IsValid = false,
                ErrorMessage = $"Cannot access file: {ex.Message}"
            };
        }

        return new StartupItem
        {
            Type = StartupItemType.TorrentFile,
            Value = normalizedPath,
            IsValid = true
        };
    }

    /// <summary>
    /// Normalizes a file path for cross-platform compatibility.
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Handle quoted paths (Windows can pass these)
        path = path.Trim('"', '\'');

        // Convert to full path if relative
        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(path);
        }

        return path;
    }

    /// <summary>
    /// Extracts display information from a magnet URI for error messages.
    /// </summary>
    public static string GetMagnetDisplayName(string magnetUri)
    {
        if (string.IsNullOrEmpty(magnetUri))
            return "Unknown";

        // Try to extract dn (display name) parameter
        var dnIndex = magnetUri.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);
        if (dnIndex >= 0)
        {
            var start = dnIndex + 3;
            var end = magnetUri.IndexOf('&', start);
            var name = end >= 0
                ? magnetUri.Substring(start, end - start)
                : magnetUri.Substring(start);

            // URL decode the name
            return Uri.UnescapeDataString(name.Replace('+', ' '));
        }

        // Fall back to showing truncated info hash
        var hashIndex = magnetUri.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
        if (hashIndex >= 0)
        {
            var start = hashIndex + 5;
            var end = magnetUri.IndexOf('&', start);
            var hash = end >= 0
                ? magnetUri.Substring(start, end - start)
                : magnetUri.Substring(start);

            if (hash.Length > 8)
                return $"Torrent {hash.Substring(0, 8)}...";
            return $"Torrent {hash}";
        }

        return "Unknown Torrent";
    }
}
