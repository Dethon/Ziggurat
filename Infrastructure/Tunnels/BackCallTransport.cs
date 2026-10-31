using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using Mcp.Hosting.Tunnel;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Infrastructure.Tunnels;

// Who a back-call came from: the tunnel it arrived on, as admitted, and the tag it was opened with.
// Kestrel hands it to every request on the connection as a feature, which is the only place the
// gateway reads either from — never a header the server sent.
public sealed record BackCallOrigin(OpenTunnel Tunnel, string Tag);

// Where Kestrel listens for back-calls: no socket, only the streams household servers open in their
// tunnels, which the relay hands over one by one.
internal sealed class BackCallEndPoint : EndPoint
{
    public override string ToString() => "tunnel back-calls";
}

// The relay's back-call streams as a Kestrel transport: each stream a server opens is one connection,
// so the HTTP it carries is read by Kestrel — keep-alive, chunked bodies and all — and never by a
// parser of this repo's own. Kestrel binds it for BackCallEndPoint and for nothing else.
internal sealed class BackCallTransport : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>();

    public bool CanBind(EndPoint endpoint) => endpoint is BackCallEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IConnectionListener>(new Listener(endpoint, _accepted.Reader));

    // False once Kestrel has stopped listening: the stream is the caller's to refuse.
    public bool Offer(TunnelStream stream, BackCallOrigin origin) =>
        _accepted.Writer.TryWrite(new StreamConnection(stream, origin));

    public void Complete() => _accepted.Writer.TryComplete();

    private sealed class Listener(EndPoint endpoint, ChannelReader<ConnectionContext> accepted) : IConnectionListener
    {
        public EndPoint EndPoint => endpoint;

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await accepted.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamConnection : ConnectionContext
    {
        private readonly TunnelStream _stream;

        public StreamConnection(TunnelStream stream, BackCallOrigin origin)
        {
            _stream = stream;
            ConnectionId = $"back-call-{Guid.NewGuid():N}";
            Features.Set(origin);
            Transport = new DuplexPipe(
                PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)),
                PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
            ConnectionClosed = origin.Tunnel.Closed;
        }

        public override string ConnectionId { get; set; }

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override IDuplexPipe Transport { get; set; }

        public override void Abort(ConnectionAbortedException abortReason) => _stream.Reset(abortReason.Message);

        public override async ValueTask DisposeAsync()
        {
            await Transport.Input.CompleteAsync();
            await Transport.Output.CompleteAsync();
            await _stream.DisposeAsync();
        }

        private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;
    }
}