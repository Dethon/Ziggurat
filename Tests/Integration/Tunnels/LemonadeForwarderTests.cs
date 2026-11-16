using System.Collections.Concurrent;
using System.Net;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using LemonadeForwarder;
using Mcp.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// A household's Lemonade forwarder as the kit runs it: told its tunnel and its box, it opens the
// tunnel by itself, and the agent reaches the box through it — a stand-in box here, answering its
// model list and a streamed Responses call, and recording what reached it.
public class LemonadeForwarderTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<(string Path, string? Authorization, string Host)> _reached = new();
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private WebApplication _box = null!;
    private string _household = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        _relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString,
            SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory }), _metrics);
        _box = await StartBoxAsync();
    }

    public async Task DisposeAsync()
    {
        _released.TrySetResult();
        await _box.DisposeAsync();
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task TheAgent_ReachesTheBoxThroughTheHouseholdsTunnel_AndItsAnswerStreams()
    {
        await using var forwarder = await StartForwarderAsync(key: "");
        using var agent = Agent();

        (await agent.GetStringAsync("models")).ShouldContain("box-model");

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses") { Content = new StringContent("{}") };
        using var response = await agent.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        (await reader.ReadLineAsync()).ShouldBe("data: one", "the first event arrives while the box still holds the rest");
        _released.SetResult();
        (await reader.ReadToEndAsync()).ShouldContain("data: two");
        _reached.Select(r => r.Path).ShouldBe(["/api/v1/models", "/api/v1/responses"]);
    }

    [Fact]
    public async Task TheKitsKey_IsTheOneTheBoxSees_NeverTheCallers()
    {
        await using var forwarder = await StartForwarderAsync(key: "kit-key");
        using var agent = Agent();
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer agents-key");

        (await agent.SendAsync(request)).EnsureSuccessStatusCode();

        _reached.Single().Authorization.ShouldBe("Bearer kit-key");
    }

    [Fact]
    public async Task WithNoKey_NoAuthorizationReachesTheBox()
    {
        await using var forwarder = await StartForwarderAsync(key: "");
        using var agent = Agent();
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer agents-key");

        (await agent.SendAsync(request)).EnsureSuccessStatusCode();

        _reached.Single().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task ARequestNamingAnotherHost_StillReachesOnlyTheBox()
    {
        await using var forwarder = await StartForwarderAsync(key: "");
        using var agent = Agent();
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.Host = "example.com";

        (await agent.SendAsync(request)).EnsureSuccessStatusCode();

        _reached.Single().Host.ShouldBe($"127.0.0.1:{BoxPort}");
    }

    [Fact]
    public async Task TheMcpTag_ReachesNothing()
    {
        await using var forwarder = await StartForwarderAsync(key: "");
        using var mcp = new HttpClient(_relay.Registry.StreamsTo(_household, HouseholdServerNames.Lemonade, TunnelProtocol.McpTag))
        {
            BaseAddress = new Uri("http://lemonade.tunnel/")
        };

        await Should.ThrowAsync<HttpRequestException>(() => mcp.GetStringAsync("models"));
        _reached.ShouldBeEmpty();
    }

    [Fact]
    public void WithNoBoxAddress_ItRefusesToStart_NamingTheSetting()
    {
        var refusal = Should.Throw<InvalidOperationException>(() =>
            new ConfigurationBuilder().BindSettings<ForwarderSettings>(userSecretsId: null));

        refusal.Message.ShouldContain("environment variable Box");
    }

    // What an empty LEMONADE_URL in the kit's .env binds to: present, so the binder lets it by.
    [Fact]
    public void WithAnEmptyBoxAddress_ItRefusesToStart_NamingTheSetting()
    {
        var refusal = Should.Throw<InvalidOperationException>(() => ForwarderApp.Build(new ForwarderSettings
        {
            Box = new BoxSettings { Url = "" }
        }, WebApplication.CreateBuilder()));

        refusal.Message.ShouldContain("BOX__URL");
    }

    private int BoxPort => new Uri(_box.Urls.First()).Port;

    private HttpClient Agent() =>
        new(_relay.Registry.StreamsTo(_household, HouseholdServerNames.Lemonade, TunnelProtocol.ChatTag))
        {
            BaseAddress = new Uri("http://lemonade.tunnel/")
        };

    private async Task<WebApplication> StartForwarderAsync(string key)
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, HouseholdServerNames.Lemonade);
        var builder = WebApplication.CreateBuilder();
        var forwarder = ForwarderApp.Build(new ForwarderSettings
        {
            Mcp = new McpGateSettings { Tunnel = TunnelledServer.Quick(_relay.Hub, credential.Credential, HouseholdServerNames.Lemonade) },
            Box = new BoxSettings { Url = $"http://127.0.0.1:{BoxPort}/api/v1", ApiKey = key },
            Listener = new ListenerSettings { Port = TestPort.GetAvailable() }
        }, builder);
        await forwarder.StartAsync();
        await Eventually.Until(
            () => _relay.Registry.Find(_household, HouseholdServerNames.Lemonade) is not null, "the forwarder's tunnel opens");
        return forwarder;
    }

    private async Task<WebApplication> StartBoxAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var box = builder.Build();
        box.Use(async (http, next) =>
        {
            _reached.Enqueue((http.Request.Path, http.Request.Headers.Authorization.FirstOrDefault(), http.Request.Host.Value ?? ""));
            await next(http);
        });
        box.MapGet("/api/v1/models", () => Results.Json(new { data = new[] { new { id = "box-model" } } }));
        box.MapPost("/api/v1/responses", async (HttpContext http) =>
        {
            http.Response.ContentType = "text/event-stream";
            await http.Response.WriteAsync("data: one\n\n");
            await http.Response.Body.FlushAsync();
            await _released.Task.WaitAsync(http.RequestAborted);
            await http.Response.WriteAsync("data: two\n\n");
        });
        await box.StartAsync();
        return box;
    }
}