using Domain.Security;

namespace Infrastructure.Tunnels;

// What every exchange the agent sends down a tunnel passes through before its bytes do. A tunnel that
// has closed refuses here, with an error that says so, rather than reaching a connection pool whose
// streams have all been reset. One that is open is first handed any key the agent has started
// signing with — ahead of the request on the same WebSocket, so the server holds the key before it
// reads the call — and the request then presents the credential the tunnel was opened with, which
// is what the server's /mcp asks for.
internal sealed class RelayHandler(OpenTunnel tunnel, HttpMessageHandler connections) : DelegatingHandler(connections)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!tunnel.IsOpen)
        {
            throw new HttpRequestException($"{tunnel.ServerName} went offline: its tunnel closed");
        }

        await tunnel.SendKeysIfChangedAsync(cancellationToken);
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(tunnel.Presented));
        return await base.SendAsync(request, cancellationToken);
    }
}