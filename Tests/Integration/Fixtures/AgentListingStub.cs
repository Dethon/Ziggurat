using System.Net;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Tests.Integration.Fixtures;

// The agent's listing of a household's connected servers (ConnectedServers.Path), answered as the
// agent answers it, behind the agent API's secret.
internal sealed class AgentListingStub(WebApplication app, Dictionary<string, List<ConnectedServer>> connected) : IAsyncDisposable
{
    public string Url => app.Urls.First();

    public static async Task<AgentListingStub> StartAsync(string sharedSecret)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var connected = new Dictionary<string, List<ConnectedServer>>();
        app.MapGet(ConnectedServers.Path + "/{tenantId}", (string tenantId, HttpContext http) =>
            SharedSecret.Matches(http.Request.Headers.Authorization.ToString(), sharedSecret)
                ? Results.Json(connected.GetValueOrDefault(tenantId) ?? [], TunnelProtocol.SerializerOptions)
                : Results.Unauthorized());
        await app.StartAsync();
        return new AgentListingStub(app, connected);
    }

    public void Connect(string tenantId, ConnectedServer server)
    {
        if (!connected.TryGetValue(tenantId, out var servers))
        {
            connected[tenantId] = servers = [];
        }

        servers.Add(server);
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}