using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Tunnel;
using Domain.Security;

namespace Infrastructure.Clients.Accounts;

// The accounts service's verdict on a tunnel's credential, asked by the relay on every open with the
// agent API's own credential (the service lists the agent as a caller by it, CALLERS__agent). A
// refusal is the service's 404 and its reason; any other answer is the service failing, thrown, so an
// outage is never mistaken for an unknown credential and a household's server is not told its
// credential is wrong when the deployment could not look.
public sealed class TunnelCredentialClient(HttpClient http, string credential)
{
    public const string ClientName = "accounts-tunnels";

    public async Task<TunnelVerdict> VerifyAsync(string tunnelCredential, string serverName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TunnelCredentialProtocol.VerifyPath.TrimStart('/'))
        {
            Content = JsonContent.Create(new TunnelCredentialQuery(tunnelCredential, serverName))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var refused = await response.Content.ReadFromJsonAsync<TunnelCredentialRefused>(ct)
                          ?? throw new HttpRequestException("The accounts service refused a tunnel without a reason.");
            return new TunnelVerdict.Refused(refused.Reason);
        }

        response.EnsureSuccessStatusCode();
        return new TunnelVerdict.Opens(await response.Content.ReadFromJsonAsync<TunnelCredentialVerified>(ct)
                                       ?? throw new HttpRequestException("The accounts service answered with no body."));
    }

    // Who holds a server of that name, for the key commands; an outage throws, never an empty list,
    // since an empty one would let a retirement strand delegations nobody was asked for.
    public async Task<IReadOnlyList<TunnelServerHolder>> HoldersAsync(string serverName, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TunnelCredentialProtocol.HoldersPath.TrimStart('/'))
        {
            Content = JsonContent.Create(new TunnelServerHoldersQuery(serverName))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<TunnelServerHolder>>(ct)
               ?? throw new HttpRequestException("The accounts service answered with no body.");
    }
}

public abstract record TunnelVerdict
{
    public sealed record Opens(TunnelCredentialVerified Credential) : TunnelVerdict;

    public sealed record Refused(TunnelCredentialRefusal Reason) : TunnelVerdict;
}