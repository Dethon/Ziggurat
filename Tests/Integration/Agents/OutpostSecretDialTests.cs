using System.Net;
using Domain.Outposts;
using Infrastructure.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// The second half of the shared secret, and the one that stops a stranger. An outpost listens on
// every interface on somebody's own computer and offers their whole filesystem — and, where they
// asked for it, a shell. Without the agent presenting the secret and the machine demanding it,
// all of that costs nothing but knowing the URL.
public sealed class OutpostSecretDialTests : IAsyncLifetime
{
    private const string Secret = "s3cret";

    private IHost _machine = null!;
    private string _endpoint = null!;

    // The gate the outpost's own Program.cs installs, in front of the MCP endpoint, comparing with
    // the rule both ends share.
    public async Task InitializeAsync()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "outpost-under-test",
                Version = "1.0.0"
            })
            .WithHttpTransport()
            // A server with no tools advertises no tools/list, which the session build asks for;
            // one is enough, and this test is about who gets to ask.
            .WithTools<TestEchoTool>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!OutpostSecret.Matches(context.Request.Headers.Authorization.ToString(), Secret))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
        app.MapMcp("/mcp");

        _machine = app;
        await _machine.StartAsync();
        _endpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _machine.StopAsync();
        _machine.Dispose();
    }

    [Fact]
    public async Task AnEndpointCarryingTheSecret_IsDialled()
    {
        await using var session = await BuildAsync(McpServerEndpoint.Dynamic(_endpoint, Secret));

        session.ClientManager.Clients.Count.ShouldBe(1);
    }

    // Dropped, not thrown: to the dial an outpost refusing it is indistinguishable from one that is
    // asleep, and both cost only that mount (ADR 0027). What matters here is that it does not get in.
    [Theory]
    [InlineData(null)]
    [InlineData("the-wrong-secret")]
    public async Task AnEndpointWithoutTheRightSecret_NeverGetsIn(string? secret)
    {
        await using var session = await BuildAsync(McpServerEndpoint.Dynamic(_endpoint, secret));

        session.ClientManager.Clients.ShouldBeEmpty();
    }

    private static Task<ThreadSession> BuildAsync(McpServerEndpoint endpoint) =>
        ThreadSession.CreateAsync(
            [endpoint],
            "outpost-secret-test",
            "test-user",
            "the agent under test",
            [],
            new HashSet<string>(),
            null,
            CancellationToken.None);
}