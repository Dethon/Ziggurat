using System.Threading.Channels;

namespace Mcp.Hosting.Tunnel;

// One stream inside a tunnel, read and written like a socket: what the other end writes is read
// here in order, and a write here reaches the other end as data. Reading ends at the other end's
// close and throws at its reset or the tunnel's end.
//
// What arrives waits until it is read, up to a bound past which the tunnel stops reading for every
// stream: a stream nobody reads holds up its own tunnel and no other.
public sealed class TunnelStream : Stream
{
    private const int BufferedFrames = 64;

    private readonly TunnelMux _mux;
    private readonly uint _id;

    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(BufferedFrames) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private ReadOnlyMemory<byte> _unread;
    private int _ended;

    internal TunnelStream(TunnelMux mux, uint id)
    {
        _mux = mux;
        _id = id;
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

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_unread.IsEmpty)
        {
            if (_inbound.Reader.TryRead(out var next))
            {
                _unread = next;
                continue;
            }

            if (!await WaitToReadAsync(cancellationToken))
            {
                return 0;
            }
        }

        var taken = Math.Min(buffer.Length, _unread.Length);
        _unread[..taken].CopyTo(buffer);
        _unread = _unread[taken..];
        return taken;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _ended) != 0)
        {
            throw new IOException("the stream was closed");
        }

        await _mux.SendDataAsync(_id, buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // Refused or failed on this end: the other end's next read throws the reason.
    public void Reset(string reason)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _inbound.Writer.TryComplete(new IOException(reason));
            _ = _mux.SendResetAsync(_id, reason);
        }
    }

    internal async Task ReceiveAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        try
        {
            // Copied: the tunnel reads every message into one buffer it reuses.
            await _inbound.Writer.WriteAsync(data.ToArray(), ct);
        }
        catch (ChannelClosedException)
        {
        }
    }

    // A close (no reason) lets what already arrived be read before the end; a reset or the tunnel's
    // end throws at the next read.
    internal void EndedByPeer(string? reason)
    {
        Interlocked.Exchange(ref _ended, 1);
        _inbound.Writer.TryComplete(reason is null ? null : new IOException(reason));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            _mux.Forget(_id, tell: true);
        }

        base.Dispose(disposing);
    }

    private async Task<bool> WaitToReadAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.WaitToReadAsync(ct);
        }
        catch (ChannelClosedException closed)
        {
            throw new IOException(closed.InnerException?.Message ?? "the stream was reset", closed);
        }
    }
}