using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using vTorrent.Core.IO;
using Xunit;

namespace vTorrent.Tests.Unit.Core;

public sealed class DeletionWorkerTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private DeletionWorker _worker = null!;

    public DeletionWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DeletionWorkerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public Task InitializeAsync()
    {
        _worker = new DeletionWorker(NullLoggerFactory.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _worker.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private string TempFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    [Fact]
    public async Task DeleteFileAsync_DeletesFile()
    {
        var path = TempFile("delete_me.bin");

        await _worker.DeleteFileAsync(path);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFileAsync_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(_tempDir, "missing.bin");

        Func<Task> act = () => _worker.DeleteFileAsync(path);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DeleteDirectoryAsync_DeletesRecursively()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(subDir, "file.bin"), new byte[512]);

        await _worker.DeleteDirectoryAsync(subDir, recursive: true);

        Directory.Exists(subDir).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFileAsync_RespectsCancellation()
    {
        var path = TempFile("cancel_me.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _worker.DeleteFileAsync(path, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CleanEmptyDirectoriesAsync_RemovesEmptyDirs()
    {
        var root = Path.Combine(_tempDir, "clean_root");
        var nested = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(nested);

        await _worker.CleanEmptyDirectoriesAsync(root);

        Directory.Exists(root).Should().BeFalse();
    }

    [Fact]
    public async Task CleanEmptyDirectoriesAsync_PreservesNonEmptyDirs()
    {
        var root = Path.Combine(_tempDir, "preserve_root");
        var nested = Path.Combine(root, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(root, "a", "keep.txt"), new byte[1]);

        await _worker.CleanEmptyDirectoriesAsync(root);

        Directory.Exists(Path.Combine(root, "a")).Should().BeTrue();
        Directory.Exists(nested).Should().BeFalse("empty leaf should be removed");
    }

    [Fact]
    public async Task MultipleOperations_ExecuteSequentially()
    {
        var paths = new List<string>();
        for (int i = 0; i < 10; i++)
            paths.Add(TempFile($"seq_{i}.bin"));

        var tasks = new List<Task>();
        foreach (var p in paths)
            tasks.Add(_worker.DeleteFileAsync(p));

        await Task.WhenAll(tasks);

        foreach (var p in paths)
            File.Exists(p).Should().BeFalse();
    }
}
