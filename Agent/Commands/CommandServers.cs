using System.Net.Http.Json;
using Agent.Settings;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;

namespace Agent.Commands;

// The channel servers the key commands ask for the delegations they keep, dialled as the agent dials
// them. A URL endpoint is dialled at its URL with the credential the deployment holds for it. An
// endpoint named by its server name is the home household's server of that name, which exists only
// inside its tunnel and the tunnel only inside the running agent: the command, a process of its own,
// asks the agent which household is the home's (the audience its call tokens are for) and reaches the
// server's /mcp through the agent's relay (ConnectedServers.HomeMcp), behind the agent API's secret.
// A home server whose tunnel is not open throws on the dial, which the commands report as a server that
// could not be asked — retiring keys then refuses, as it must for a server that may keep delegations.
public static class CommandServers
{
    public static IReadOnlyList<Redelegation.Server> Of(
        IEnumerable<ChannelEndpoint> endpoints, McpCredentials credentials, CallTokens tokens, Uri agent,
        string agentApiSecret) =>
    [
        .. endpoints.Select(endpoint => new Redelegation.Server(endpoint.ChannelId, ct =>
            TunnelCredentialProtocol.IsServerName(endpoint.Endpoint)
                ? HomeAsync(endpoint.Endpoint, tokens, agent, agentApiSecret, ct)
                : DialledAsync(endpoint.Endpoint, credentials, tokens, ct)))
    ];

    private static Task<McpClient> DialledAsync(string url, McpCredentials credentials, CallTokens tokens, CancellationToken ct)
    {
        var server = ServerAddress.FromUrl(url)
                     ?? throw new UriFormatException($"The channel endpoint '{url}' is not an absolute URL");
        return McpClient.CreateAsync(new SigningClientTransport(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            AdditionalHeaders = credentials.For(server) is { } secret
                ? new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(secret) }
                : null
        }), tokens, TokenAudiences.CallTo(server)), cancellationToken: ct);
    }

    private static async Task<McpClient> HomeAsync(
        string serverName, CallTokens tokens, Uri agent, string agentApiSecret, CancellationToken ct)
    {
        var authorization = new Dictionary<string, string> { ["Authorization"] = SharedSecret.Header(agentApiSecret) };
        using var http = new HttpClient { BaseAddress = agent };
        using var request = new HttpRequestMessage(HttpMethod.Get, ConnectedServers.Home(serverName));
        request.Headers.TryAddWithoutValidation("Authorization", authorization["Authorization"]);
        using var answer = await http.SendAsync(request, ct);
        answer.EnsureSuccessStatusCode();
        var home = await answer.Content.ReadFromJsonAsync<HomeServer>(TunnelProtocol.SerializerOptions, ct)
                   ?? throw new InvalidOperationException($"The agent named no household for {serverName}");
        return await McpClient.CreateAsync(new SigningClientTransport(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(agent, ConnectedServers.HomeMcp(serverName)),
            AdditionalHeaders = authorization
        }), tokens, TokenAudiences.CallTo(ServerAddress.Tunnelled(home.TenantId, serverName))), cancellationToken: ct);
    }
}