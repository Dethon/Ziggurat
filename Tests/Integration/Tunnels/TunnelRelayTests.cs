using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Infrastructure.Tunnels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServerTests;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// A household's server and the deployment's relay, each the real thing on a real socket: the server
// is told a hub, a credential and its name and opens its tunnel by itself; the relay asks the real
// accounts service whose server that is. What the agent can do through the tunnel — and what a
// forged, revoked or misnamed credential cannot — is read the way the agent reads it, through its
// own client manager.
public class TunnelRelayTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string Fran = "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private string _household = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        _keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await StartRelayAsync();
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task ASignedToolCall_IsAnsweredThroughTheTunnel()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");

        await using var agent = await DialAsync(_relay, "vault");
        var who = await WhoAsync(agent);

        who["account"]!["userId"]!.GetValue<string>().ShouldBe(Fran);
        who["account"]!["tenantId"]!.GetValue<string>().ShouldBe(_household);
    }

    [Fact]
    public async Task AnUnsignedToolCall_IsRefusedThroughTheTunnel()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        var endpoint = await ConnectedAsync(_relay, "vault");

        await using var unsigned = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(endpoint.Address) }, endpoint.Through!, ownsHttpClient: false));
        var refusal = await Should.ThrowAsync<McpProtocolException>(() => unsigned.CallToolAsync("who").AsTask());

        refusal.Message.ShouldContain(nameof(TokenRefusal.Missing));
    }

    // The agent as a deployment builds it: a definition naming the household's server by its name,
    // resolved against the turn's household to that server's open tunnel when the session is built.
    [Fact]
    public async Task AnAgentNamingAHouseholdServer_BuildsItsSessionThroughTheServersTunnel()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await ConnectedAsync(_relay, "vault");
        var definitions = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        definitions.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions
        {
            Agents = [new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = ["vault"] }]
        });
        var factory = new MultiAgentFactory(
            new ServiceCollection()
                .AddSingleton(new CallTokens(_keys, TimeProvider.System))
                .AddSingleton(_relay.OwnServers)
                .AddSingleton(new Mock<IThreadStateStore>().Object)
                .BuildServiceProvider(),
            new AgentDefinitionProvider(definitions.Object, new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            Mock.Of<IDomainToolRegistry>(r =>
                r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()) == Enumerable.Empty<AIFunction>()
                && r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()) == Enumerable.Empty<PromptSection>()));

        await using var agent = factory.Create(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", Mock.Of<IToolApprovalHandler>(), tenantId: _household);
        var session = await agent.CreateSessionAsync();

        await agent.WarmupSessionAsync(session);
    }

    public static TheoryData<string, TunnelCredentialRefusal> Refusals => new()
    {
        { "unknown", TunnelCredentialRefusal.Unknown },
        { "revoked", TunnelCredentialRefusal.Revoked },
        { "another server's", TunnelCredentialRefusal.AnotherServer }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ACredentialThatDoesNotOpenTheServersTunnel_IsRefusedWithItsReasonAndReported(
        string credential, TunnelCredentialRefusal reason)
    {
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, await CredentialAsync(credential), "vault");

        await Eventually.Until(
            () => _metrics.Published.OfType<ErrorEvent>().Any(e => e.ErrorType == TunnelRelay.RefusedErrorType),
            "the refusal is reported");
        _metrics.Published.OfType<ErrorEvent>().ShouldContain(e =>
            e.ErrorType == TunnelRelay.RefusedErrorType && e.Service == "agent" && e.Message.Contains(reason.ToString()));
        _relay.Registry.Open.ShouldBeEmpty();
    }

    [Fact]
    public async Task RevokingACredential_ClosesTheTunnelItOpened()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await ConnectedAsync(_relay, "vault");
        using var watching = new CancellationTokenSource();
        var closed = FirstClosedAsync(_relay.Registry, watching.Token);

        var (exit, output) = await _accounts.RunAsync(global::Accounts.Commands.AccountsCommand.RevokeTunnelCredential, "--id", credential.Id);

        exit.ShouldBe(0, output);
        (await closed.WaitAsync(TimeSpan.FromSeconds(10))).Reason.ShouldBe(TunnelProtocol.Revoked);
        await Eventually.Until(
            () => _metrics.Published.OfType<ErrorEvent>().Any(e => e.Message.Contains(nameof(TunnelCredentialRefusal.Revoked))),
            "the server's next open is refused as revoked");
        _relay.Registry.Open.ShouldBeEmpty();
        await watching.CancelAsync();
    }

    // A deploy restarts the agent; the household's server rides it out alone and is reachable again
    // through the relay that came back.
    [Fact]
    public async Task ATunnel_ReopensAfterTheRelayRestarts()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await ConnectedAsync(_relay, "vault");
        var port = _relay.Port;

        await _relay.DisposeAsync();
        _relay = await StartRelayAsync(port);
        await using var agent = await DialAsync(_relay, "vault");

        (await WhoAsync(agent))["account"]!["userId"]!.GetValue<string>().ShouldBe(Fran);
    }

    // The agent's key rotates while the tunnel is open: the server is handed the new keys over the
    // tunnel before the first call signed with the new key, and takes it.
    [Fact]
    public async Task ACallSignedWithARotatedKey_IsTakenOnceTheKeysArriveOverTheTunnel()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await using var agent = await DialAsync(_relay, "vault");
        await WhoAsync(agent);
        var before = _keys.CurrentKeyId;

        // As the rotate command does: a store opened beside the running agent's, over its directory.
        SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory }).Rotate();
        await using var rotated = await DialAsync(_relay, "vault", new CallTokens(_keys, TimeProvider.System));

        _keys.CurrentKeyId.ShouldNotBe(before);
        (await WhoAsync(rotated))["account"]!["userId"]!.GetValue<string>().ShouldBe(Fran);
    }

    private async Task<string> CredentialAsync(string kind)
    {
        switch (kind)
        {
            case "unknown":
                return Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            case "revoked":
                var revoked = await _accounts.MintTunnelCredentialAsync(_household, "vault");
                (await _accounts.RunAsync(global::Accounts.Commands.AccountsCommand.RevokeTunnelCredential, "--id", revoked.Id)).Exit.ShouldBe(0);
                return revoked.Credential;
            default:
                return (await _accounts.MintTunnelCredentialAsync(_household, "library")).Credential;
        }
    }

    private static async Task<TunnelClosed> FirstClosedAsync(TunnelRegistry registry, CancellationToken ct)
    {
        await foreach (var change in registry.WatchAsync(ct))
        {
            if (change is TunnelClosed closed)
            {
                return closed;
            }
        }

        throw new InvalidOperationException("the watch ended before any tunnel closed");
    }

    private Task<RelayHost> StartRelayAsync(int? port = null) =>
        RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, _keys, _metrics, port);

    // The household's server as the agent resolves it, once its tunnel is open.
    private async Task<McpServerEndpoint> ConnectedAsync(RelayHost relay, string serverName)
    {
        await Eventually.Until(() => relay.Registry.Find(_household, serverName) is not null, $"{serverName}'s tunnel opens");
        return relay.OwnServers.Resolve(_household, serverName)!;
    }

    // Dialled as the agent dials every server: its client manager, signing each call for whoever the
    // call names — nobody, here.
    private async Task<McpClientManager> DialAsync(RelayHost relay, string serverName, CallTokens? tokens = null) =>
        await McpClientManager.CreateAsync(
            "jonas", "fran", "test", [await ConnectedAsync(relay, serverName)], new McpClientHandlers(),
            callTokens: tokens ?? new CallTokens(_keys, TimeProvider.System));

    // A turn of Fran's, as her conversation group verified it: the agent signs the call for her.
    private async Task<JsonObject> WhoAsync(McpClientManager agent)
    {
        var turn = WebIdentityServerHarness.FromChatClient(new Account(_household, Fran)) with
        {
            Identity = IdentityClaims.Person(Fran, _household, ChannelIds.SignalR, [])
        };
        var result = await agent.Clients.Single().CallToolAsync(new CallToolRequestParams
        {
            Name = "who",
            Meta = WebIdentityServerHarness.MetaFor(turn)
        });
        return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!.AsObject();
    }
}