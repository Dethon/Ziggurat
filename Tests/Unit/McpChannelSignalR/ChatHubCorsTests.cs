using Infrastructure.Extensions;
using Mcp.Hosting;
using McpChannelSignalR.Hubs;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// The hub is dialled from a browser with a visitor's credentials, so only an origin the server's
// own settings list may negotiate a connection from a page — read through the binder every server
// reads by, under the section the compose file sets. A preflight never builds the hub, so none of
// its collaborators are needed to answer one.
public class ChatHubCorsTests
{
    private const string Ours = "https://assistants.example.com";

    [Theory]
    [InlineData(Ours, true)]
    [InlineData("https://evil.example.com", false)]
    public async Task ANegotiatePreflight_IsAdmittedOnlyFromAnOriginTheSettingsList(string origin, bool admitted)
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RedisConnectionString"] = "redis:6379",
                ["Cors:AllowedOrigins:0"] = Ours
            })
            .BindSettings<ChannelSettings>(userSecretsId: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddDeploymentCors(settings.Cors);
        await using var app = builder.Build();
        app.UseCors();
        app.MapHub<ChatHub>("/hubs/chat");
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/hubs/chat/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "x-requested-with,x-signalr-user-agent");
        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBe(admitted);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBe(admitted);
    }
}