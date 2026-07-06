using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using vTorrent.Abstractions.Interfaces.Transport;

namespace vTorrent.Core.PeerCommunication.Transport;

/// <summary>
/// Adapts an ITransportStream to a System.IO.Stream for use with legacy code
/// that expects NetworkStream-style APIs (byte[], offset, count overloads).
/// </summary>
public sealed class TransportStreamAdapter : Stream
{
    private readonly ITransportStream _inner;

    public TransportStreamAdapter(ITransportStream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await _inner.ReadAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Use ReadAsync");

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Use WriteAsync");

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
