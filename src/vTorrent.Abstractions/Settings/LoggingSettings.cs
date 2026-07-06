namespace vTorrent.Abstractions.Settings;

/// <summary>
/// Logging settings
/// </summary>
public class LoggingSettings
{
    /// <summary>
    /// Minimum log level (Trace, Debug, Information, Warning, Error, Critical)
    /// </summary>
    public string Level { get; set; } = "Information";

    /// <summary>
    /// Write logs to file
    /// </summary>
    public bool LogToFile { get; set; } = false;

    /// <summary>
    /// Log file path
    /// </summary>
    public string LogFilePath { get; set; } = "";

    /// <summary>
    /// Maximum log file size before rotation (bytes)
    /// </summary>
    public long MaxLogFileSize { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Maximum number of log files to keep
    /// </summary>
    public int MaxLogFiles { get; set; } = 5;
}
