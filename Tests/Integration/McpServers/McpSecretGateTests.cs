using System.Net;
using System.Text;
using Domain.Security;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServers;

// The deployment secret in front of every server's /mcp, over real HTTP. A forged conversation
// context in a call's _meta is believed by whichever tool reads it, so the only thing standing
// between "anyone on the network" and "acting as somebody's conversation" is this gate.
//
// One transport, because the servers serve one: streamable HTTP at /mcp, whose three verbs are all
// asked here. The legacy SSE endpoints are off in this SDK unless a server turns them on, and none
// does; their paths sit under /mcp, so they would be gated by the same rule if one ever did.
public class McpSecretGateTests : IAsyncLifetime
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0.0"}}}""";

    private WebApplication? _app;
    private string _base = "";

    private sealed record GatedSettings(McpGateSettings Mcp) : IMcpHostSettings;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    public static TheoryData<string, string?, string> Refusals =>
        new[] { "POST", "GET", "DELETE" }.Aggregate(
            new TheoryData<string, string?, string>(), (data, verb) =>
            {
                data.Add(verb, null, McpTestSecret.Value);
                data.Add(verb, SharedSecret.Header("wrong"), McpTestSecret.Value);
                data.Add(verb, McpTestSecret.Value, McpTestSecret.Value);
                // A deployment that never set the secret: the right-looking header still fails,
                // because an unset secret has to mean "nobody" rather than "anybody".
                data.Add(verb, SharedSecret.Header(""), "");
                data.Add(verb, SharedSecret.Header(McpTestSecret.Value), "");
                return data;
            });

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ACallToMcpWithoutTheConfiguredSecret_IsUnauthorized(
        string verb, string? authorization, string configured)
    {
        await StartAsync(configured);
        using var http = new HttpClient();

        using var response = await http.SendAsync(Request(verb, "/mcp", authorization));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheLegacySsePaths_AreGatedToo()
    {
        await StartAsync(McpTestSecret.Value);
        using var http = new HttpClient();

        using var sse = await http.SendAsync(Request("GET", "/mcp/sse", null));
        using var message = await http.SendAsync(Request("POST", "/mcp/message", null));

        sse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        message.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AClientPresentingTheSecret_ConnectsAndCallsTools()
    {
        await StartAsync(McpTestSecret.Value);

        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport($"{_base}/mcp"));
        var result = await client.CallToolAsync("throws");

        result.IsError.ShouldBe(true);
    }

    // A server's other endpoints are called by browsers, Home Assistant and satellites, none of
    // which hold the deployment secret, and the ones that need a gate carry their own token. The
    // gate is about /mcp and nothing else.
    [Fact]
    public async Task AServersOtherEndpoints_AreNotGated()
    {
        await StartAsync(McpTestSecret.Value);
        using var http = new HttpClient();

        using var other = await http.SendAsync(Request("GET", "/other", null));
        using var lookalike = await http.SendAsync(Request("GET", "/mcpish", null));

        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        lookalike.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task StartAsync(string secret)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddToolServer(new GatedSettings(new McpGateSettings { SharedSecret = secret }))
            .WithTools<FailingTools>();

        _app = builder.Build();
        _app.MapMcp("/mcp");
        _app.MapGet("/other", () => Results.Ok());
        _app.MapGet("/mcpish", () => Results.Ok());
        await _app.StartAsync();
        _base = $"http://localhost:{port}";
    }

    private HttpRequestMessage Request(string verb, string path, string? authorization)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), $"{_base}{path}");
        if (verb == "POST")
        {
            request.Content = new StringContent(Initialize, Encoding.UTF8, "application/json");
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }
}