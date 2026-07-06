namespace vTorrent.Cli.Interactive;

/// <summary>
/// A notification for the REPL queue. Uses a typed flag instead of
/// string inspection to distinguish pre-formatted markup from plain text.
/// </summary>
public record ReplNotification(string Text, bool IsMarkup);
