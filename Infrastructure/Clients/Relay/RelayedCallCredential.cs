using Domain.DTOs.Tunnel;
using Domain.Security;

namespace Infrastructure.Clients.Relay;

// What a shared deployment server's every request to the relay's endpoint carries: its own name, as
// the agent dials it, and the credential its own /mcp asks for — which the agent holds to dial it, so
// the relay can check the caller with no secret of its own. The relay takes both off before the
// request goes on, and presents the household's server the credential of its tunnel instead.
public sealed class RelayedCallCredential(string caller, string credential) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(RelayedCallProtocol.CallerHeader);
        request.Headers.TryAddWithoutValidation(RelayedCallProtocol.CallerHeader, caller);
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return base.SendAsync(request, cancellationToken);
    }
}