using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core.Network.IpFilter;

public static class IpFilterLoader
{
    public static async Task<(int loaded, int skipped)> LoadAsync(
        IpFilter filter, string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return (0, 0);

        Stream stream = File.OpenRead(filePath);
        try
        {
            if (filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                stream = new GZipStream(stream, CompressionMode.Decompress);

            using var reader = new StreamReader(stream);

            var format = DetectFormat(filePath);
            if (format == FilterFormat.Unknown)
            {
                string? peekLine = null;
                while ((peekLine = await reader.ReadLineAsync(ct)) != null)
                {
                    if (string.IsNullOrWhiteSpace(peekLine) || peekLine.StartsWith('#'))
                        continue;
                    break;
                }
                if (peekLine == null) return (0, 0);

                format = peekLine.Contains(',') ? FilterFormat.Dat : FilterFormat.P2p;

                var (ok, _) = format == FilterFormat.Dat
                    ? ParseDatLine(peekLine, filter)
                    : ParseP2pLine(peekLine, filter);

                int loaded = ok ? 1 : 0;
                int skipped = ok ? 0 : 1;

                var (l, s) = await ProcessLinesAsync(reader, filter, format, ct);
                return (loaded + l, skipped + s);
            }

            return await ProcessLinesAsync(reader, filter, format, ct);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }

    private static async Task<(int loaded, int skipped)> ProcessLinesAsync(
        StreamReader reader, IpFilter filter, FilterFormat format, CancellationToken ct)
    {
        int loaded = 0, skipped = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var (ok, _) = format == FilterFormat.Dat
                ? ParseDatLine(line, filter)
                : ParseP2pLine(line, filter);

            if (ok) loaded++;
            else skipped++;
        }
        return (loaded, skipped);
    }

    private static (bool success, string? error) ParseDatLine(string line, IpFilter filter)
    {
        var parts = line.Split(',');
        if (parts.Length < 2) return (false, "too few fields");

        var ipRange = parts[0].Trim();
        if (!int.TryParse(parts[1].Trim(), out var accessLevel))
            return (false, "invalid access level");

        var ips = ipRange.Split('-');
        if (ips.Length != 2) return (false, "invalid range");

        if (!IPAddress.TryParse(ips[0].Trim(), out var first) ||
            !IPAddress.TryParse(ips[1].Trim(), out var last))
            return (false, "invalid IP");

        var flags = accessLevel >= 100 ? AccessFlags.Blocked : AccessFlags.Allowed;
        filter.AddRule(first, last, flags);
        return (true, null);
    }

    private static (bool success, string? error) ParseP2pLine(string line, IpFilter filter)
    {
        var colonIdx = line.LastIndexOf(':');
        if (colonIdx < 0) return (false, "no colon");

        var ipRange = line[(colonIdx + 1)..].Trim();
        var ips = ipRange.Split('-');
        if (ips.Length != 2) return (false, "invalid range");

        if (!IPAddress.TryParse(ips[0].Trim(), out var first) ||
            !IPAddress.TryParse(ips[1].Trim(), out var last))
            return (false, "invalid IP");

        filter.AddRule(first, last, AccessFlags.Blocked);
        return (true, null);
    }

    private static FilterFormat DetectFormat(string filePath)
    {
        var name = filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? filePath[..^3] : filePath;

        if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return FilterFormat.Dat;
        if (name.EndsWith(".p2p", StringComparison.OrdinalIgnoreCase)) return FilterFormat.P2p;
        return FilterFormat.Unknown;
    }

    private enum FilterFormat { Unknown, Dat, P2p }
}
