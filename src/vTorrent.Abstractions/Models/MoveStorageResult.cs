using System;

namespace vTorrent.Abstractions.Models;

/// <summary>
/// Result of a move_storage operation.
/// </summary>
public sealed class MoveStorageResult
{
    /// <summary>
    /// Whether the move operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The new path after a successful move.
    /// </summary>
    public string NewPath { get; }

    /// <summary>
    /// Error message if the move failed.
    /// </summary>
    public string ErrorMessage { get; }

    /// <summary>
    /// Exception if the move failed due to an error.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Whether files need to be re-verified after the move.
    /// This can happen during cross-volume moves.
    /// </summary>
    public bool NeedsRecheck { get; }

    private MoveStorageResult(bool success, string newPath, string errorMessage, Exception exception, bool needsRecheck)
    {
        IsSuccess = success;
        NewPath = newPath;
        ErrorMessage = errorMessage;
        Exception = exception;
        NeedsRecheck = needsRecheck;
    }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static MoveStorageResult Success(string newPath, bool needsRecheck = false)
        => new(true, newPath, null, null, needsRecheck);

    /// <summary>
    /// Creates a failure result with an error message.
    /// </summary>
    public static MoveStorageResult Failed(string errorMessage)
        => new(false, null, errorMessage, null, false);

    /// <summary>
    /// Creates a failure result with an exception.
    /// </summary>
    public static MoveStorageResult Failed(string errorMessage, Exception exception)
        => new(false, null, errorMessage, exception, false);

    public override string ToString()
    {
        return IsSuccess
            ? $"MoveStorageResult: Success -> {NewPath}"
            : $"MoveStorageResult: Failed - {ErrorMessage}";
    }
}
