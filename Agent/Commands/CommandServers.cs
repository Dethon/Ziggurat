using System.Net.Http.Json;
using Agent.Settings;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Client;

namespace Agent.Commands;

// The channel servers the key commands ask for the delegations they keep, read and dialled as the
// agent reads and dials them (McpServerEndpoint.Of, McpEndpointTransport). An endpoint named
// by its server name is the home household's server of that name, which exists only inside its tunnel
// and the tunnel only inside the running agent: the command, a process of its own, asks the agent which
// household is the home's (the audience its call tokens are for) and reaches the server's /mcp through
// the agent's relay (ConnectedServers.HomeMcp, McpServerEndpoint.ThroughAgent), behind the agent API's
// secret. A home server whose tunnel is not open throws on the dial, which the commands report as a
// server that could not be asked — retiring keys then refuses, as it must for a server that may keep
// delegations.
public static class CommandServers
{
    public static IReadOnlyList<Redelegation.Server> Of(
        IEnumerable<ChannelEndpoint> endpoints, McpCredentials credentials, CallTokens tokens, Uri agent,
        string agentApiSecret) =>
    [
        .. endpoints.Select(endpoint => new Redelegation.Server(endpoint.ChannelId, async ct =>
        {
            var configured = McpServerEndpoint.Of(endpoint.Endpoint, credentials.For);
            var reached = configured.NamesOwnServer
                ? await HomeAsync(configured.Address, agent, agentApiSecret, ct)
                : configured;
            return await McpClient.CreateAsync(McpEndpointTransport.For(reached, tokens), cancellationToken: ct);
        }))
    ];

    private static async Task<McpServerEndpoint> HomeAsync(
        string serverName, Uri agent, string agentApiSecret, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = agent };
        using var request = new HttpRequestMessage(HttpMethod.Get, ConnectedServers.Home(serverName));
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(agentApiSecret));
        using var answer = await http.SendAsync(request, ct);
        answer.EnsureSuccessStatusCode();
        var home = await answer.Content.ReadFromJsonAsync<HomeServer>(TunnelProtocol.SerializerOptions, ct)
                   ?? throw new InvalidOperationException($"The agent named no household for {serverName}");
        return McpServerEndpoint.ThroughAgent(
            new Uri(agent, ConnectedServers.HomeMcp(serverName)),
            ServerAddress.Tunnelled(home.TenantId, serverName),
            agentApiSecret);
    }
}