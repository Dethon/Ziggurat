using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
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
        AgentDefinition[] agents = [new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = ["vault"] }];
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
            new AgentKey("conv-1", "jonas"), "fran", "jonas", Mock.Of<IToolApprovalHandler>(), tenantId: _household);
        var session = await agent.CreateSessionAsync();

        await agent.WarmupSessionAsync(session);
    }

    // The household's machine goes away while the agent waits on one of its tools: the call ends,
    // and says the server went offline rather than leaving the turn to wait or to guess at a socket
    // error.
    [Fact]
    public async Task ACallInFlightWhenTheTunnelDrops_FailsSayingTheServerWentOffline()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await using var agent = await DialAsync(_relay, "vault");
        var call = Guid.NewGuid().ToString("N");
        var wait = agent.Tools.OfType<AIFunction>().Single(t => t.Name == "mcp__vault__wait");

        var answer = wait.InvokeAsync(new AIFunctionArguments { ["call"] = call }).AsTask();
        await Eventually.Until(() => TunnelledServer.WaitTools.IsWaiting(call), "the call reaches the server");
        await _relay.Registry.Find(_household, "vault")!.CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "gone");

        var error = ToolErrorResult.FromEnvelope(JsonNode.Parse((await answer.WaitAsync(TimeSpan.FromSeconds(10)))!.ToString()));
        error.ShouldNotBeNull().ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Message.ShouldContain("vault went offline");
    }

    // The server's tunnel drops between two turns and the server opens a new one: the second turn is
    // a new generation's, so it is built again and reaches the server through the tunnel open now,
    // never through the closed one its first turn used.
    [Fact]
    public async Task AServerThatReconnectsBetweenTurns_IsReachedThroughItsNewTunnelOnTheNextTurn()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, credential.Credential, "vault");
        await ConnectedAsync(_relay, "vault");
        var first = _relay.Registry.Find(_household, "vault")!;
        var offered = new List<IList<AITool>>();
        await using var agent = TurnTakingAgent(offered);
        var session = await agent.CreateSessionAsync();
        await TakeTurnAsync(agent, session);

        await first.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "gone");
        await Eventually.Until(
            () => _relay.Registry.Find(_household, "vault") is { IsOpen: true } again && again.Generation > first.Generation,
            "the server opens its tunnel again");
        await TakeTurnAsync(agent, session);

        var who = offered[1].OfType<AIFunction>().Single(t => t.Name == "mcp__vault__who");
        var answer = (await who.InvokeAsync(new AIFunctionArguments()))!.ToString()!;
        ToolErrorResult.FromEnvelope(JsonNode.Parse(answer)).ShouldBeNull(answer);
    }

    // A deployment behind a certificate authority of its own — a local stack's proxy on Caddy's CA —
    // is one a server trusts only when told that authority: the system's roots do not vouch for it,
    // so the tunnel is never opened and the relay never hears from the server.
    [Fact]
    public async Task AHubOnACertificateNoTrustedRootIssued_IsNeverJoined()
    {
        using var authority = LocalAuthority.Create();
        using var certificate = authority.Issue("localhost");
        await using var relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, _keys, _metrics, certificate: certificate);
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");

        await using var server = await TunnelledServer.StartAsync(relay.Hub, credential.Credential, "vault");
        await Eventually.Settle();

        relay.Hub.ShouldStartWith("wss://");
        relay.Registry.Find(_household, "vault").ShouldBeNull();
        server.IsAdmitted.ShouldBeFalse();
    }

    // Told the authority (MCP__TUNNEL__HUBCERTIFICATEAUTHORITY), the same server opens its tunnel and
    // is called through it as any other.
    [Fact]
    public async Task AHubOnTheCertificateAuthorityTheServerWasToldOf_AdmitsItsTunnel()
    {
        using var authority = LocalAuthority.Create();
        using var certificate = authority.Issue("localhost");
        await using var relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, _keys, _metrics, certificate: certificate);
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");

        await using var server = await TunnelledServer.StartAsync(
            relay.Hub, credential.Credential, "vault", hubAuthority: authority.CertificateFile);
        await using var agent = await DialAsync(relay, "vault");

        (await WhoAsync(agent))["account"]!["tenantId"]!.GetValue<string>().ShouldBe(_household);
    }

    // The authority vouches for the names it issued and no other: a hub whose certificate names
    // another host is refused, told the authority or not.
    [Fact]
    public async Task AHubWhoseCertificateNamesAnotherHost_IsNeverJoined_EvenOnTheAuthorityItWasToldOf()
    {
        using var authority = LocalAuthority.Create();
        using var certificate = authority.Issue("assistants.example.com");
        await using var relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, _keys, _metrics, certificate: certificate);
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "vault");

        await using var server = await TunnelledServer.StartAsync(
            relay.Hub, credential.Credential, "vault", hubAuthority: authority.CertificateFile);
        await Eventually.Settle();

        relay.Registry.Find(_household, "vault").ShouldBeNull();
    }

    // The operator asks the running agent which of a household's servers are connected: each one
    // open, since when, and the tools it offers through its tunnel now — and nothing of another
    // household's.
    [Fact]
    public async Task TheListHouseholdServersCommand_ListsEachConnectedServerSinceWhenAndItsTools()
    {
        var vault = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, vault.Credential, "vault");
        var other = await _accounts.SeedHouseholdAsync("Ana");
        var library = await _accounts.MintTunnelCredentialAsync(other, "library");
        await using var elsewhere = await TunnelledServer.StartAsync(_relay.Hub, library.Credential, "library");
        await ConnectedAsync(_relay, "vault");
        await Eventually.Until(() => _relay.Registry.Find(other, "library") is not null, "library's tunnel opens");
        var opened = _relay.Registry.Find(_household, "vault")!.OpenedAt;
        var output = new StringWriter();

        var exit = await global::Agent.Commands.AgentCommand.RunAsync(
            [global::Agent.Commands.AgentCommand.ListHouseholdServers, "--household", _household, "--agent", _relay.Address],
            AgentSettings(), TimeProvider.System, TextReader.Null, output, CancellationToken.None);

        exit.ShouldBe(0, output.ToString());
        output.ToString().ShouldContain($"vault  connected since {opened:u}");
        output.ToString().ShouldContain("tools: wait, who");
        output.ToString().ShouldNotContain("library");
    }

    [Fact]
    public async Task TheListHouseholdServersCommand_SaysSoWhenNoneIsConnected()
    {
        var output = new StringWriter();

        var exit = await global::Agent.Commands.AgentCommand.RunAsync(
            [global::Agent.Commands.AgentCommand.ListHouseholdServers, "--household", _household, "--agent", _relay.Address],
            AgentSettings(), TimeProvider.System, TextReader.Null, output, CancellationToken.None);

        exit.ShouldBe(0, output.ToString());
        output.ToString().ShouldContain($"No server of household {_household} is connected.");
    }

    // The listing is the agent's own: asked without the agent API's secret, it says nothing.
    [Fact]
    public async Task TheListHouseholdServersCommand_WithoutTheAgentsSecret_IsRefused()
    {
        var output = new StringWriter();

        var exit = await global::Agent.Commands.AgentCommand.RunAsync(
            [global::Agent.Commands.AgentCommand.ListHouseholdServers, "--household", _household, "--agent", _relay.Address],
            AgentSettings() with { AgentApi = new global::Agent.Settings.AgentApiConfiguration { SharedSecret = "wrong" } },
            TimeProvider.System, TextReader.Null, output, CancellationToken.None);

        exit.ShouldBe(1, output.ToString());
        output.ToString().ShouldContain("refused");
    }

    private static global::Agent.Settings.AgentSettings AgentSettings() => new()
    {
        OpenRouter = new global::Agent.Settings.OpenRouterConfiguration { ApiUrl = "http://test", ApiKey = "test" },
        Redis = new global::Agent.Settings.RedisConfiguration { ConnectionString = "localhost" },
        BackCalls = new() { LemonadeUrl = "http://lemonade:13305" },
        AgentApi = new global::Agent.Settings.AgentApiConfiguration { SharedSecret = AgentCredential }
    };

    // A credential the accounts service knows is a household's, and so is its refusal: a revoked vault
    // that kept knocking every minute was the deployment's error, naming nobody, so the operator could
    // not tell whose it was. One nobody minted is nobody's.
    public static TheoryData<string, TunnelCredentialRefusal, bool> Refusals => new()
    {
        { "unknown", TunnelCredentialRefusal.Unknown, false },
        { "revoked", TunnelCredentialRefusal.Revoked, true },
        { "another server's", TunnelCredentialRefusal.AnotherServer, true }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ACredentialThatDoesNotOpenTheServersTunnel_IsRefusedWithItsReasonAndReported(
        string credential, TunnelCredentialRefusal reason, bool householdKnown)
    {
        await using var server = await TunnelledServer.StartAsync(_relay.Hub, await CredentialAsync(credential), "vault");

        await Eventually.Until(
            () => _metrics.Published.OfType<ErrorEvent>().Any(e => e.ErrorType == TunnelRelay.RefusedErrorType),
            "the refusal is reported");
        _metrics.Published.OfType<ErrorEvent>().ShouldContain(e =>
            e.ErrorType == TunnelRelay.RefusedErrorType && e.Service == "agent" && e.Message.Contains(reason.ToString()));
        var refused = _metrics.Published.OfType<ErrorEvent>().First(e => e.ErrorType == TunnelRelay.RefusedErrorType);
        refused.TenantId.ShouldBe(householdKnown ? _household : null);
        refused.Message.Contains(_household, StringComparison.Ordinal).ShouldBe(householdKnown, refused.Message);
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

    // The operator suspends a household: every tunnel of it the relay holds is closed, its servers'
    // next opens are refused as Suspended though their credentials are live, and another household's
    // stay open.
    [Fact]
    public async Task SuspendingAHousehold_ClosesItsTunnels_RefusesTheirNextOpen_AndLeavesOthersOpen()
    {
        var (casa, _) = await _accounts.SeedHouseholdAsync("Casa", "Ana");
        var operatorAdmin = _accounts.Admin(await _accounts.MintSessionAsync((await _accounts.MintOperatorAsync("Fran")).UserId));
        var casaVault = await _accounts.MintTunnelCredentialAsync(casa, "vault");
        var homeVault = await _accounts.MintTunnelCredentialAsync(_household, "vault");
        await using var suspendedServer = await TunnelledServer.StartAsync(_relay.Hub, casaVault.Credential, "vault");
        await using var homeServer = await TunnelledServer.StartAsync(_relay.Hub, homeVault.Credential, "vault");
        await Eventually.Until(() => _relay.Registry.Open.Count == 2, "both households' vaults to be connected");
        using var watching = new CancellationTokenSource();
        var closed = FirstClosedAsync(_relay.Registry, watching.Token);

        (await operatorAdmin.PutAsync(AdminProtocol.Suspension(casa), new HouseholdSuspension(true))).StatusCode
            .ShouldBe(System.Net.HttpStatusCode.OK);

        var gone = await closed.WaitAsync(TimeSpan.FromSeconds(10));
        (gone.Tunnel.TenantId, gone.Reason).ShouldBe((casa, TunnelProtocol.Suspended));
        await Eventually.Until(
            () => _metrics.Published.OfType<ErrorEvent>().Any(e => e.Message.Contains(nameof(TunnelCredentialRefusal.Suspended))),
            "the server's next open is refused as suspended");
        _relay.Registry.Find(casa, "vault").ShouldBeNull();
        _relay.Registry.Find(_household, "vault").ShouldNotBeNull();
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
        var port = await _relay.StopKeepingItsPortAsync();
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

    // An agent of Fran's household naming its vault, whose model answers every turn at once and
    // records the tools each turn was offered.
    private McpAgent TurnTakingAgent(List<IList<AITool>> offered)
    {
        var model = new Mock<IChatClient>();
        model
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (_, options, _) => offered.Add(options?.Tools ?? []))
            .Returns(() => new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());
        return new McpAgent(
            TestAgentSpec.Default with { TenantId = _household, McpServerEndpoints = [McpServerEndpoint.OwnServer("vault")] },
            model.Object,
            Mock.Of<IThreadStateStore>(),
            _metrics,
            TimeProvider.System,
            [],
            [],
            callTokens: new CallTokens(_keys, TimeProvider.System),
            ownServers: _relay.OwnServers);
    }

    private static Task TakeTurnAsync(McpAgent agent, Microsoft.Agents.AI.AgentSession session) =>
        agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "what is in my vault?")], session).ToListAsync().AsTask();

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