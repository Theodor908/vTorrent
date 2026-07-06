using System.CommandLine;

namespace vTorrent.Cli;

public static class GlobalOptions
{
    public static readonly Option<string?> Profile = new("--server") { Description = "Use named server connection" };
    public static readonly Option<string?> Host = new("--host") { Description = "Override connection host (host:port)" };
    public static readonly Option<string?> Token = new("--token") { Description = "Use this JWT directly (skips profile/token store)" };
    public static readonly Option<bool> Insecure = new("--insecure") { Description = "Skip TLS certificate validation" };
    public static readonly Option<string?> CaCert = new("--ca-cert") { Description = "Custom CA certificate path" };
    public static readonly Option<bool> Json = new("--json") { Description = "Output as JSON" };
    public static readonly Option<bool> Quiet = new("--quiet", "-q") { Description = "Minimal output" };
    public static readonly Option<bool> NoColor = new("--no-color") { Description = "Disable colored output" };
    public static readonly Option<bool> Verbose = new("--verbose", "-v") { Description = "Verbose output" };
    public static readonly Option<int> Timeout = new("--timeout") { Description = "Request timeout in seconds", DefaultValueFactory = _ => 30 };
}
