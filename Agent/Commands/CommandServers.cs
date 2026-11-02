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
// agent reads and dials them (McpServerEndpoint.Of, McpEndpointTransport). An endpoint named by its
// server name is every household's server of that name, each inside its own tunnel and the tunnel only
// inside the running agent: the command, a process of its own, asks the agent which households hold
// one (ConnectedServers.Holders — the accounts service's live credentials, and the home always, as the
// channel endpoints have always named its servers) and whether each is connected now, and reaches each
// connected one's /mcp through the agent's relay down that household's tunnel (ConnectedServers.Mcp,
// McpServerEndpoint.ThroughAgent), behind the agent API's secret, its calls signed for that household's
// synthetic address. One not connected is listed as not asked, under its household; an agent that
// cannot say who holds the server leaves the endpoint as a server that could not be asked. Either way
// retiring keys then refuses, as it must while a server that may keep delegations went unasked.
public static class CommandServers
{
    public static async Task<IReadOnlyList<Redelegation.Server>> OfAsync(
        IEnumerable<ChannelEndpoint> endpoints, McpCredentials credentials, CallTokens tokens, Uri agent,
        string agentApiSecret, CancellationToken ct)
    {
        var servers = await Task.WhenAll(endpoints.Select(async endpoint =>
        {
            var configured = McpServerEndpoint.Of(endpoint.Endpoint, credentials.For);
            return configured.NamesOwnServer
                ? await EveryHouseholdsAsync(endpoint.ChannelId, configured.Address, tokens, agent, agentApiSecret, ct)
                : [Dialled(endpoint.ChannelId, configured, tokens)];
        }));
        return [.. servers.SelectMany(s => s)];
    }

    private static async Task<IReadOnlyList<Redelegation.Server>> EveryHouseholdsAsync(
        string channelId, string serverName, CallTokens tokens, Uri agent, string agentApiSecret, CancellationToken ct)
    {
        if (await HoldersAsync(serverName, agent, agentApiSecret, ct) is not { } holders)
        {
            return [new Redelegation.Server(channelId, _ => throw new InvalidOperationException(
                $"The agent at {agent} did not say which households hold {serverName}"))];
        }

        return
        [
            .. holders.Select(holder => holder switch
            {
                { Connected: true } => Dialled(NameOf(holder), McpServerEndpoint.ThroughAgent(
                    new Uri(agent, ConnectedServers.Mcp(holder.TenantId, serverName)),
                    ServerAddress.Tunnelled(holder.TenantId, serverName),
                    agentApiSecret), tokens),
                { Suspended: true } => Redelegation.Server.NotConnected(NameOf(holder), "its household is suspended"),
                _ => Redelegation.Server.NotConnected(NameOf(holder), "its tunnel is not open")
            })
        ];
    }

    private static Redelegation.Server Dialled(string name, McpServerEndpoint endpoint, CallTokens tokens) =>
        new(name, ct => McpClient.CreateAsync(McpEndpointTransport.For(endpoint, tokens), cancellationToken: ct));

    private static string NameOf(HouseholdServer server) =>
        $"{server.ServerName} of household {server.Household} ({server.TenantId}{(server.Home ? ", the home" : "")})";

    // Null when the agent could not say: never an empty list, which would ask nobody and let a
    // retirement through.
    private static async Task<IReadOnlyList<HouseholdServer>?> HoldersAsync(
        string serverName, Uri agent, string agentApiSecret, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = agent };
            using var request = new HttpRequestMessage(HttpMethod.Get, ConnectedServers.Holders(serverName));
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(agentApiSecret));
            using var answer = await http.SendAsync(request, ct);
            answer.EnsureSuccessStatusCode();
            return await answer.Content.ReadFromJsonAsync<IReadOnlyList<HouseholdServer>>(TunnelProtocol.SerializerOptions, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}