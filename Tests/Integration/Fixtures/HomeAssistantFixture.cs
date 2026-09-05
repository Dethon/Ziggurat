using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Infrastructure.Clients.HomeAssistant;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

// Boots a real Home Assistant container with a pre-seeded /config volume (see HomeAssistantSeed) so
// the REST API is reachable without HA's interactive onboarding. HA cold-starts in ~30-60s on a fresh
// /config; the readiness loop polls `/api/` with the bearer token until 200.
//
// It also owns the far end of the watch bridge: a listener on the host that the seeded
// `rest_command.assistant_watch_fired` posts to (through the container's host gateway), recording
// every payload, so a test can prove a state change in the home reaches the callback.
public class HomeAssistantFixture : IAsyncLifetime
{
    private IContainer _container = null!;
    private string _configDir = null!;
    private IHost _listener = null!;
    private readonly ConcurrentQueue<WatchCallback> _fires = new();

    public string BaseUrl { get; private set; } = null!;
    public string Token { get; private set; } = null!;
    public const string TestEntityId = HomeAssistantSeed.TestEntityId;
    public const string CalendarEntityId = HomeAssistantSeed.CalendarEntityId;

    // Every POST the home made to the watch callback, oldest first: the token it presented and the
    // payload it composed.
    public IReadOnlyList<WatchCallback> Fires => [.. _fires];

    public sealed record WatchCallback(string? Token, JsonObject Payload);

    public async Task InitializeAsync()
    {
        // Bound before the container starts, because the seed writes the url into configuration.yaml
        // and a rest_command is only read at Home Assistant's start. On all interfaces: the
        // container reaches the host through its gateway, not through loopback.
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, port));
        var app = builder.Build();
        app.MapPost("/api/homeassistant/watch-fired", async (HttpContext ctx) =>
        {
            var body = await JsonNode.ParseAsync(ctx.Request.Body);
            _fires.Enqueue(new WatchCallback(
                ctx.Request.Headers["X-Announce-Token"].FirstOrDefault(),
                body as JsonObject ?? new JsonObject()));
            return Results.Accepted();
        });
        await app.StartAsync();
        _listener = app;

        _configDir = Path.Combine(Path.GetTempPath(), $"ha-test-{Guid.NewGuid():N}");
        Token = HomeAssistantSeed.WriteConfig(
            _configDir, watchCallbackUrl: $"http://host.docker.internal:{port}/api/homeassistant/watch-fired");

        _container = TestContainers.Container(HomeAssistantSeed.ContainerImage)
            .WithPortBinding(HomeAssistantSeed.Port, true)
            .WithBindMount(_configDir, "/config")
            .WithEnvironment("TZ", "UTC")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(HomeAssistantSeed.Port))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var mapped = _container.GetMappedPublicPort(HomeAssistantSeed.Port);
        BaseUrl = $"http://{host}:{mapped}";

        await HomeAssistantSeed.WaitForApiReadyAsync(_container, BaseUrl, Token);
    }

    public HomeAssistantClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/") };
        return new HomeAssistantClient(http, Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _container.DisposeAsync();
        }
        finally
        {
            await _listener.StopAsync();
            _listener.Dispose();
            if (_configDir is not null && Directory.Exists(_configDir))
            {
                try
                { Directory.Delete(_configDir, recursive: true); }
                catch { /* best effort — container may still hold handles momentarily */ }
            }
        }
    }
}