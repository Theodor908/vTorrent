using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.IO;

/// <summary>
/// Processes file and directory deletion on a dedicated long-running thread
/// to prevent thread pool starvation and UI freezing.
/// Mirrors libtorrent's disk thread pattern for async_delete_files.
/// </summary>
public sealed class DeletionWorker : IAsyncDisposable
{
    private readonly Channel<DeletionJob> _channel;
    private readonly Task _workerTask;
    private readonly ILogger<DeletionWorker> _logger;

    public DeletionWorker(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<DeletionWorker>();
        _channel = Channel.CreateUnbounded<DeletionJob>(new UnboundedChannelOptions
        {
            SingleReader = true
        });

        _workerTask = Task.Factory.StartNew(
            ProcessJobsAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Delete a single file on the dedicated I/O thread.
    /// </summary>
    public Task DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DeletionJob(DeletionJobType.DeleteFile, path, false, ct, tcs);
        if (!_channel.Writer.TryWrite(job))
            tcs.SetException(new ObjectDisposedException(nameof(DeletionWorker)));
        return tcs.Task;
    }

    /// <summary>
    /// Delete a directory on the dedicated I/O thread.
    /// </summary>
    public Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DeletionJob(DeletionJobType.DeleteDirectory, path, recursive, ct, tcs);
        if (!_channel.Writer.TryWrite(job))
            tcs.SetException(new ObjectDisposedException(nameof(DeletionWorker)));
        return tcs.Task;
    }

    /// <summary>
    /// Recursively remove empty directories bottom-up on the dedicated I/O thread.
    /// </summary>
    public Task CleanEmptyDirectoriesAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new DeletionJob(DeletionJobType.CleanEmptyDirectories, path, false, ct, tcs);
        if (!_channel.Writer.TryWrite(job))
            tcs.SetException(new ObjectDisposedException(nameof(DeletionWorker)));
        return tcs.Task;
    }

    private async Task ProcessJobsAsync()
    {
        await foreach (var job in _channel.Reader.ReadAllAsync())
        {
            try
            {
                if (job.CancellationToken.IsCancellationRequested)
                {
                    job.Completion.SetCanceled(job.CancellationToken);
                    continue;
                }

                switch (job.Type)
                {
                    case DeletionJobType.DeleteFile:
                        ExecuteDeleteFile(job.Path);
                        break;
                    case DeletionJobType.DeleteDirectory:
                        ExecuteDeleteDirectory(job.Path, job.Recursive);
                        break;
                    case DeletionJobType.CleanEmptyDirectories:
                        ExecuteCleanEmptyDirectories(job.Path, job.CancellationToken);
                        break;
                }

                job.Completion.SetResult();
            }
            catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
            {
                job.Completion.SetCanceled(job.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deletion job failed for {Path}", job.Path);
                job.Completion.SetException(ex);
            }
        }
    }

    private void ExecuteDeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void ExecuteDeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive);
    }

    private void ExecuteCleanEmptyDirectories(string directory, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var subDir in Directory.GetDirectories(directory))
        {
            ct.ThrowIfCancellationRequested();
            ExecuteCleanEmptyDirectories(subDir, ct);
        }

        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _workerTask.ConfigureAwait(false);
    }

    private enum DeletionJobType
    {
        DeleteFile,
        DeleteDirectory,
        CleanEmptyDirectories
    }

    private readonly record struct DeletionJob(
        DeletionJobType Type,
        string Path,
        bool Recursive,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);
}
