using System.Collections.Concurrent;
using System.Net;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using LemonadeForwarder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;
using Tests.Unit.Infrastructure.Agents.ChatClients;

namespace Tests.Integration.Tunnels;

// Two households whose Lemonade boxes sit behind their own routers, each reached through its own
// kit's forwarder: the agent's per-household hosts, the real relay and the real forwarders, with a
// stand-in box per household answering its model list and a streamed Responses turn as a real box
// answered one.
public class HouseholdKitLemonadeTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Box, string Path, string? Authorization)> _reached = new();
    private readonly List<WebApplication> _boxes = [];
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private string _casa = null!;
    private string _piso = null!;
    private int _casaBox;
    private int _pisoBox;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _casa = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        _piso = await _accounts.SeedHouseholdAsync("Piso");
        _keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, _keys, _metrics);
        _casaBox = await StartBoxAsync("casa");
        _pisoBox = await StartBoxAsync("piso");
    }

    public async Task DisposeAsync()
    {
        foreach (var box in _boxes)
        {
            await box.DisposeAsync();
        }

        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task EachHouseholdsKitBox_OffersItsOwnModels_AndATurnRunsOnItAlone()
    {
        await using var casa = await StartForwarderAsync(_casa, _casaBox, "casa-key");
        await using var piso = await StartForwarderAsync(_piso, _pisoBox, "piso-key");
        var hosts = Hosts();

        await hosts.RefreshAsync(CancellationToken.None);

        hosts.ModelsByHousehold[_casa].Select(m => m.Id).ShouldBe(["casa-model"]);
        hosts.ModelsByHousehold[_piso].Select(m => m.Id).ShouldBe(["piso-model"]);

        var reply = await TurnAsync(hosts.For(_casa, home: false)!, "lemonade/casa-model");

        reply.ShouldBe("ok");
        var turn = _reached.Where(r => r.Path.EndsWith("/responses")).ShouldHaveSingleItem();
        turn.Box.ShouldBe("casa");
        _reached.Where(r => r.Box == "casa").ShouldAllBe(r => r.Authorization == "Bearer casa-key");
        _reached.Where(r => r.Box == "piso").ShouldAllBe(r => r.Authorization == "Bearer piso-key");
    }

    [Fact]
    public async Task AClosedTunnel_TakesTheModelsAwayOnTheNextRefresh_AndAReopenedOneBringsThemBack()
    {
        var credential = (await _accounts.MintTunnelCredentialAsync(_casa, HouseholdServerNames.Lemonade)).Credential;
        var forwarder = await StartForwarderAsync(_casa, _casaBox, "", credential);
        var hosts = Hosts();
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.ModelsByHousehold[_casa].ShouldNotBeEmpty();

        await forwarder.DisposeAsync();
        await Eventually.Until(() => _relay.Registry.Find(_casa, HouseholdServerNames.Lemonade) is null, "the tunnel closes");
        await hosts.RefreshAsync(CancellationToken.None);

        hosts.ModelsByHousehold[_casa].ShouldBeEmpty();
        await Should.ThrowAsync<Exception>(() => TurnAsync(hosts.For(_casa, home: false)!, "lemonade/casa-model"));

        await using var reopened = await StartForwarderAsync(_casa, _casaBox, "", credential);
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.ModelsByHousehold[_casa].Select(m => m.Id).ShouldBe(["casa-model"]);
    }

    // Revoking the forwarder's credential cuts the box off from the deployment's side: its tunnel is
    // closed and refused, and the box offers nothing from the next refresh on.
    [Fact]
    public async Task ARevokedForwarderCredential_TakesTheBoxsModelsAwayOnTheNextRefresh()
    {
        var minted = await _accounts.MintTunnelCredentialAsync(_casa, HouseholdServerNames.Lemonade);
        await using var forwarder = await StartForwarderAsync(_casa, _casaBox, "", minted.Credential);
        var hosts = Hosts();
        await hosts.RefreshAsync(CancellationToken.None);
        hosts.ModelsByHousehold[_casa].ShouldNotBeEmpty();

        (await _accounts.RunAsync(global::Accounts.Commands.AccountsCommand.RevokeTunnelCredential, "--id", minted.Id)).Exit.ShouldBe(0);
        await Eventually.Until(() => _relay.Registry.Find(_casa, HouseholdServerNames.Lemonade) is null, "the revoked tunnel closes");
        await hosts.RefreshAsync(CancellationToken.None);

        hosts.ModelsByHousehold[_casa].ShouldBeEmpty();
    }

    // The forwarder carries chat, never tools: an agent that names lemonade among its servers builds
    // its session without dialling it, though its tunnel is open — dialled, its mcp tag reaches nothing
    // and the session would fail.
    [Fact]
    public async Task AnAgentNamingTheLemonadeServer_NeverDialsItAsAToolServer()
    {
        await using var forwarder = await StartForwarderAsync(_casa, _casaBox, "");
        AgentDefinition[] agents = [new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = ["lemonade"] }];
        var factory = new MultiAgentFactory(
            new ServiceCollection()
                .AddSingleton(new CallTokens(_keys, TimeProvider.System))
                .AddSingleton(_relay.OwnServers)
                .AddSingleton(new Mock<IThreadStateStore>().Object)
                .BuildServiceProvider(),
            FixedHouseholdAgents.Provider(new CustomAgentRegistry(), agents),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            Mock.Of<IDomainToolRegistry>(r =>
                r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()) == Enumerable.Empty<AIFunction>()
                && r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()) == Enumerable.Empty<PromptSection>()));

        await using var agent = await factory.CreateAsync(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", Mock.Of<IToolApprovalHandler>(), tenantId: _casa);
        var session = await agent.CreateSessionAsync();

        await agent.WarmupSessionAsync(session);
    }

    private HouseholdLemonadeHosts Hosts()
    {
        var configuration = new Mock<IHouseholdConfiguration>();
        configuration.Setup(c => c.LemonadeBoxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new HouseholdLemonadeBox(_casa, LemonadeBox.Kit), new HouseholdLemonadeBox(_piso, LemonadeBox.Kit)]);
        configuration.Setup(c => c.ForAgentAsync(HouseholdConfigProtocol.HomeHousehold, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentHouseholdConfig(_casa, true, new HouseholdSettings(), new AgentsSection(), new FeaturesSection(),
                new SpacesSection(), null));
        return new HouseholdLemonadeHosts(
            new LemonadeChatHostOptions { ApiUrl = "" }, FixedLemonadeModelSource.None, configuration.Object,
            NullLoggerFactory.Instance,
            household => _relay.Registry.StreamsTo(household, HouseholdServerNames.Lemonade, TunnelProtocol.ChatTag));
    }

    private static async Task<string> TurnAsync(LemonadeHost host, string model)
    {
        using var client = new LemonadeChatClient(host.Options, transportHandler: host.Transport);
        var updates = await client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = model }).ToListAsync();
        return updates.ToChatResponse().Text;
    }

    private async Task<WebApplication> StartForwarderAsync(string household, int boxPort, string key, string? credential = null)
    {
        credential ??= (await _accounts.MintTunnelCredentialAsync(household, HouseholdServerNames.Lemonade)).Credential;
        var forwarder = ForwarderApp.Build(new ForwarderSettings
        {
            Mcp = new McpGateSettings { Tunnel = TunnelledServer.Quick(_relay.Hub, credential, HouseholdServerNames.Lemonade) },
            Box = new BoxSettings { Url = $"http://127.0.0.1:{boxPort}/api/v1", ApiKey = key },
            Listener = new ListenerSettings { Port = TestPort.GetAvailable() }
        }, WebApplication.CreateBuilder());
        await forwarder.StartAsync();
        await Eventually.Until(
            () => _relay.Registry.Find(household, HouseholdServerNames.Lemonade) is not null, "the forwarder's tunnel opens");
        return forwarder;
    }

    private async Task<int> StartBoxAsync(string name)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var box = builder.Build();
        box.Use(async (http, next) =>
        {
            _reached.Enqueue((name, http.Request.Path, http.Request.Headers.Authorization.FirstOrDefault()));
            await next(http);
        });
        box.MapGet("/api/v1/models", () => Results.Json(new
        {
            data = new[] { new { id = $"{name}-model", labels = new[] { "chat", "tool-calling" }, downloaded = true } }
        }));
        box.MapPost("/api/v1/responses", () => Results.Text(LemonadeResponsesSseHandler.Sse, "text/event-stream"));
        await box.StartAsync();
        _boxes.Add(box);
        return new Uri(box.Urls.First()).Port;
    }
}