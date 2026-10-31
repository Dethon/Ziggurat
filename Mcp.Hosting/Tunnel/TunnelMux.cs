using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace Mcp.Hosting.Tunnel;

// Which end of a tunnel this is. The relay numbers the streams it opens odd and the server even, so
// the two never pick the same number.
public enum TunnelEnd
{
    Relay,
    Server
}

// One tunnel's WebSocket carrying numbered streams of bytes in both directions, and nothing more.
// Either end opens a stream with a tag saying what it is for; what travels inside is whatever the
// two ends put there (HTTP/1.1 for every stream today), so nothing here knows about MCP, pages or
// WebSocket upgrades — and nothing about them needs to change to travel through it.
//
// Binary messages are streams' frames: one byte saying what the frame is, the stream's number
// (four bytes, big-endian), then the payload — the tag for an open, bytes for data, nothing for a
// close, the reason for a reset. Text messages are control messages (Domain's TunnelControl), handed
// to the end that owns them in the order they arrived among the frames, so a key sent before a call
// is held before the call is read.
//
// A close from either end ends the stream in both directions: an HTTP/1.1 exchange never half-closes
// and neither does a WebSocket upgraded inside one. A reset ends it with a reason, which the other
// end's next read throws.
public sealed class TunnelMux : IAsyncDisposable
{
    private const int HeaderLength = 5;
    private const int MaxDataPayload = 32 * 1024;

    // Bigger than any frame this end writes and any key set the relay hands over; a peer sending
    // more is not speaking this protocol.
    private const int MaxMessage = 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly Func<string, CancellationToken, Task> _control;
    private readonly Func<TunnelStream, string, Task>? _accept;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<uint, TunnelStream> _streams = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly uint _parity;
    private uint _lastOpened;

    // `accept` is handed every stream the other end opens, with its tag, and owns it from then on;
    // without one, every such stream is reset.
    public TunnelMux(
        WebSocket socket,
        TunnelEnd end,
        Func<string, CancellationToken, Task> control,
        Func<TunnelStream, string, Task>? accept = null)
    {
        _socket = socket;
        _control = control;
        _accept = accept;
        _parity = end == TunnelEnd.Relay ? 1u : 0u;
        _lastOpened = _parity == 1 ? uint.MaxValue : 0u;
    }

    public bool IsClosed => _closed.IsCancellationRequested;

    // Cancelled when the tunnel closes, for whatever reason.
    public CancellationToken Closed => _closed.Token;

    // How the WebSocket was closed, once it has been: the status and the description the other end
    // gave, which is how a refused tunnel learns its reason.
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public string? CloseStatusDescription => _socket.CloseStatusDescription;

    // Reads until the WebSocket closes or fails, then resets every stream still open. Returns
    // normally either way: a dropped tunnel is the ordinary end of one.
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (await ReceiveAsync(buffer, ct) is { } type)
            {
                if (type == WebSocketMessageType.Text)
                {
                    await _control(Encoding.UTF8.GetString(buffer.WrittenSpan), ct);
                }
                else
                {
                    await DispatchAsync(buffer.WrittenMemory, ct);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            Shut();
        }
    }

    public async Task<TunnelStream> OpenAsync(string tag, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var id = Interlocked.Add(ref _lastOpened, 2);
        var stream = new TunnelStream(this, id);
        _streams[id] = stream;
        try
        {
            await SendFrameAsync(Frame.Open, id, Encoding.UTF8.GetBytes(tag), ct);
        }
        catch
        {
            _streams.TryRemove(id, out _);
            throw;
        }

        return stream;
    }

    public async Task SendControlAsync(string json, CancellationToken ct)
    {
        await _sending.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sending.Release();
        }
    }

    // Says why and lets the other end close its side; the reader then ends. Best effort: a tunnel
    // whose socket is already gone has nobody left to tell.
    public async Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        try
        {
            await _sending.WaitAsync(ct);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(status, reason, ct);
                }
            }
            finally
            {
                _sending.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _socket.Abort();
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Abort();
        Shut();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    internal async Task SendFrameAsync(Frame frame, uint id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (IsClosed)
        {
            throw new IOException("the tunnel closed");
        }

        var length = HeaderLength + payload.Length;
        var message = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            message[0] = (byte)frame;
            BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1), id);
            payload.Span.CopyTo(message.AsSpan(HeaderLength));
            await _sending.WaitAsync(ct);
            try
            {
                await _socket.SendAsync(message.AsMemory(0, length), WebSocketMessageType.Binary, true, ct);
            }
            finally
            {
                _sending.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new IOException("the tunnel closed", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    internal async Task SendDataAsync(uint id, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        for (var offset = 0; offset < data.Length; offset += MaxDataPayload)
        {
            await SendFrameAsync(Frame.Data, id, data.Slice(offset, Math.Min(MaxDataPayload, data.Length - offset)), ct);
        }
    }

    // The local end is done with a stream: the other end is told unless it said so first.
    internal void Forget(uint id, bool tell)
    {
        if (_streams.TryRemove(id, out _) && tell && !IsClosed)
        {
            _ = SendClosingAsync(id);
        }
    }

    private async Task SendClosingAsync(uint id)
    {
        try
        {
            await SendFrameAsync(Frame.Close, id, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        }
        catch (IOException)
        {
        }
    }

    private async Task<WebSocketMessageType?> ReceiveAsync(ArrayBufferWriter<byte> buffer, CancellationToken ct)
    {
        buffer.ResetWrittenCount();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.GetMemory(16 * 1024), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Answered, so the other end's close completes rather than waiting out its timeout.
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                return null;
            }

            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaxMessage)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "a message over the tunnel's limit", CancellationToken.None);
                return null;
            }

            if (result.EndOfMessage)
            {
                return result.MessageType;
            }
        }
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        if (message.Length < HeaderLength)
        {
            return;
        }

        var frame = (Frame)message.Span[0];
        var id = BinaryPrimitives.ReadUInt32BigEndian(message.Span[1..]);
        var payload = message[HeaderLength..];
        switch (frame)
        {
            case Frame.Open:
                Accept(id, Encoding.UTF8.GetString(payload.Span));
                break;
            case Frame.Data when _streams.TryGetValue(id, out var stream):
                await stream.ReceiveAsync(payload, ct);
                break;
            case Frame.Close when _streams.TryRemove(id, out var stream):
                stream.EndedByPeer(null);
                break;
            case Frame.Reset when _streams.TryRemove(id, out var stream):
                stream.EndedByPeer(Encoding.UTF8.GetString(payload.Span));
                break;
        }
    }

    // A stream the other end opened: numbered with the other end's parity, never one already open,
    // and handed to whoever accepts this end's streams — or refused.
    private void Accept(uint id, string tag)
    {
        if (_accept is null || id % 2 == _parity || _streams.ContainsKey(id))
        {
            _ = SendResetAsync(id, $"nothing here takes a '{tag}' stream");
            return;
        }

        var stream = new TunnelStream(this, id);
        _streams[id] = stream;
        _ = AcceptAsync(stream, tag);
    }

    private async Task AcceptAsync(TunnelStream stream, string tag)
    {
        try
        {
            await _accept!(stream, tag);
        }
        catch (Exception ex)
        {
            stream.Reset(ex.Message);
        }
    }

    internal async Task SendResetAsync(uint id, string reason)
    {
        _streams.TryRemove(id, out _);
        try
        {
            await SendFrameAsync(Frame.Reset, id, Encoding.UTF8.GetBytes(reason), CancellationToken.None);
        }
        catch (IOException)
        {
        }
    }

    private void Shut()
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        _closed.Cancel();
        foreach (var id in _streams.Keys)
        {
            if (_streams.TryRemove(id, out var stream))
            {
                stream.EndedByPeer("the tunnel closed");
            }
        }
    }

    internal enum Frame : byte
    {
        Open = 1,
        Data = 2,
        Close = 3,
        Reset = 4
    }
}