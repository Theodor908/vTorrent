using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace vTorrent.Core;

/// <summary>
/// Ensures only one instance of vTorrent runs at a time.
/// Uses a named Mutex for detection and a named pipe to forward
/// command-line arguments (torrent files, magnet links) to the
/// already-running instance.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "vTorrent_SingleInstance_7A3F2E1D";
    private const string PipeName = "vTorrent_IPC_7A3F2E1D";

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    /// <summary>
    /// Fired on the existing instance when another instance sends arguments.
    /// The string contains the raw arguments separated by newlines.
    /// </summary>
    public event Action<string[]>? ArgumentsReceived;

    /// <summary>
    /// Attempts to acquire the single-instance lock.
    /// Returns true if this is the first instance.
    /// Returns false if another instance is already running (args have been forwarded).
    /// </summary>
    public bool TryAcquire(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool isNew);

        if (isNew)
        {
            // We are the first instance — start listening for forwarded args
            StartPipeServer();
            return true;
        }

        // Another instance exists — forward our args and exit
        try
        {
            SendArgumentsToExistingInstance(args);
        }
        catch
        {
            // If pipe send fails, the other instance may have just closed.
            // Let this instance proceed as the new primary.
            try
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            catch { }

            _mutex = new Mutex(true, MutexName, out isNew);
            if (isNew)
            {
                StartPipeServer();
                return true;
            }
        }

        return false;
    }

    private void SendArgumentsToExistingInstance(string[] args)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        client.Connect(3000); // 3 second timeout

        using var writer = new StreamWriter(client);
        // Send arg count, then each arg on a separate line
        writer.WriteLine(args.Length.ToString());
        foreach (var arg in args)
        {
            writer.WriteLine(arg);
        }
        writer.Flush();
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var ct = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(server);
                    var countLine = await reader.ReadLineAsync();
                    if (int.TryParse(countLine, out int count) && count > 0)
                    {
                        var args = new string[count];
                        for (int i = 0; i < count; i++)
                        {
                            args[i] = await reader.ReadLineAsync() ?? string.Empty;
                        }
                        ArgumentsReceived?.Invoke(args);
                    }
                    else
                    {
                        // No args — just activate the window
                        ArgumentsReceived?.Invoke(Array.Empty<string>());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Pipe error — wait briefly and retry
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _pipeCts = null;

        if (_mutex != null)
        {
            try { _mutex.ReleaseMutex(); }
            catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
