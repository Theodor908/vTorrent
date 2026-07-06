using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.Utilities;

/// <summary>
/// Per David Fowler AsyncGuidance and Stephen Cleary MSDN Best Practices:
/// Fire-and-forget tasks must observe exceptions to avoid UnobservedTaskException.
/// This is the single controlled async-void point in the codebase.
/// </summary>
internal static class TaskExtensions
{
    internal static async void FireAndForget(this Task task, ILogger? logger = null,
        [CallerMemberName] string? caller = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — don't log noise
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Fire-and-forget task failed in {Caller}", caller);
        }
    }
}
