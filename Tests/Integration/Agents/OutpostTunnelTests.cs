using Accounts.Store;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Outposts;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Infrastructure.Tunnels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.Tunnels;
using Tests.Unit;

namespace Tests.Integration.Agents;

// An outpost as it runs now: the binary on somebody's machine, told a hub, its name and — in its
// environment — the credential the operator minted for `outpost-<name>`, opens its own tunnel to the
// real relay, which asks the real accounts service whose machine that is. The agent never dials it:
// a session built for that household while the tunnel is open mounts the machine at `outpost:<name>`
// and reaches its files and its shell through the tunnel, and once the tunnel closes the machine is
// simply not there for the next session.
public class OutpostTunnelTests(RedisFixture redis, McpVaultServerFixture vault)
    : IClassFixture<RedisFixture>, IClassFixture<McpVaultServerFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _requests = [];
    private bool _callTheMachineOnNextRequest;
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "outpost-tunnel-tests", Guid.NewGuid().ToString("N"));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private TunnelledOutposts _outposts = null!;
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
        _relay = await RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, _keys, _metrics);
        _outposts = _relay.Outposts;
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task AnOutpost_IsMountedAtItsMachineAddressThroughItsTunnel()
    {
        await using var laptop = await StartAsync("laptop");
        await File.WriteAllTextAsync(Path.Combine(laptop.WorkingDirectory, "notes.md"), "on the laptop");

        await using var session = await BuildAsync();
        var registry = session.FileSystemRegistry.ShouldNotBeNull();

        registry.GetMounts().Select(m => m.MountPoint).ShouldBe([OutpostMountPoint.For("laptop")]);
        var read = Resolve(registry, $"outpost:laptop{laptop.WorkingDirectory}/notes.md");
        (await read.Backend.ReadAsync(read.RelativePath, null, null, CancellationToken.None))
            .TryGetValue(out var file, out _).ShouldBeTrue();
        file!.Content.ShouldContain("on the laptop");
    }

    // A machine named like the vault is a separate machine at an address of its own, so it does not
    // compete with the vault for the name: both are mounted, each where it is spelled.
    [Fact]
    public async Task AnOutpostNamedLikeAMount_IsMountedBesideIt()
    {
        await using var machine = await StartAsync("vault");

        await using var session = await BuildAsync(Vault);

        session.FileSystemRegistry.ShouldNotBeNull()
            .GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault", "outpost:vault"], ignoreOrder: true);
        session.ShadowedNames.ShouldBeEmpty();
    }

    [Fact]
    public async Task AJailedOutpost_RefusesAPathOutsideItsDirectoryThroughItsTunnel()
    {
        await using var laptop = await StartAsync("laptop", jailed: true, exec: true);

        await using var session = await BuildAsync();
        var outside = Path.GetDirectoryName(laptop.WorkingDirectory);
        var result = await new VfsExecTool(session.FileSystemRegistry!).RunAsync($"outpost:laptop{outside}", "pwd");

        result.ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task AnExecutingOutpost_RunsACommandThroughItsTunnel()
    {
        await using var laptop = await StartAsync("laptop", exec: true);

        await using var session = await BuildAsync();
        var result = await new VfsExecTool(session.FileSystemRegistry!).RunAsync("outpost:laptop", "echo from-the-machine");

        result.ShouldBeOk()["stdout"]!.GetValue<string>().Trim().ShouldBe("from-the-machine");
    }

    [Fact]
    public async Task AnOutpost_IsGoneOnceItsTunnelCloses()
    {
        var laptop = await StartAsync("laptop");
        await laptop.DisposeAsync();
        await Eventually.Until(
            () => _relay.Registry.Find(_household, OutpostServerName.For("laptop")) is null, "the laptop's tunnel closes");

        await using var session = await BuildAsync(Vault);

        _outposts.Live(_household).ShouldBeEmpty();
        session.FileSystemRegistry.ShouldNotBeNull().GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault"]);
    }

    // The call token is checked like any server's, against the keys the relay handed over the tunnel:
    // holding the tunnel's client is not enough to act through the machine.
    [Fact]
    public async Task AnOutpost_RefusesAnUnsignedCall()
    {
        await using var laptop = await StartAsync("laptop");
        var endpoint = _outposts.Live(_household).ShouldHaveSingleItem().Endpoint;
        // Bound to that opening, so a call after it closes fails as the server going offline.
        endpoint.Generation.ShouldBe(_relay.Registry.Find(_household, OutpostServerName.For("laptop"))!.Generation);

        await using var unsigned = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(endpoint.Address) }, endpoint.Through!, ownsHttpClient: false));
        var refusal = await Should.ThrowAsync<McpProtocolException>(() => unsigned
            .CallToolAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/" }).AsTask());

        refusal.Message.ShouldContain(nameof(TokenRefusal.Missing));
    }

    // An outpost belongs to the household whose credential opened its tunnel: another household's
    // agent never sees it.
    [Fact]
    public async Task AnotherHouseholdsOutpost_IsNotMounted()
    {
        var neighbour = await SeedHouseholdAsync();
        var credential = await _accounts.MintTunnelCredentialAsync(neighbour, OutpostServerName.For("laptop"));
        await using var theirs = await OutpostMachine.StartAsync(_relay.Hub, credential.Credential, "laptop");
        await Eventually.Until(
            () => _relay.Registry.Find(neighbour, OutpostServerName.For("laptop")) is not null, "their laptop's tunnel opens");

        await using var session = await BuildAsync(Vault);

        session.FileSystemRegistry.ShouldNotBeNull().GetMounts().Select(m => m.MountPoint).ShouldBe(["/vault"]);
    }

    // "Was that machine up at two o'clock" is answered by the record of its tunnel opening and closing,
    // since an outpost leaves no other trace of having been there.
    [Fact]
    public async Task AnOutpostsTunnel_IsRecordedOpeningAndClosing()
    {
        var laptop = await StartAsync("laptop");
        await laptop.DisposeAsync();

        await Eventually.Until(
            () => _metrics.Published.OfType<OutpostEvent>().Any(e => e.Lifecycle == OutpostLifecycle.Closed),
            "the laptop's tunnel closing is recorded");
        _metrics.Published.OfType<OutpostEvent>().Select(e => (e.Outpost, e.TenantId, e.Lifecycle))
            .ShouldBe([("laptop", _household, OutpostLifecycle.Opened), ("laptop", _household, OutpostLifecycle.Closed)]);
    }

    // A conversation outlives the tunnels of the machines it reaches: each turn's start asks again
    // which machines are open, and one that opened since the last turn is mounted for this one.
    [Fact]
    public async Task AnOutpostThatOpensMidConversation_IsMountedOnTheNextTurn()
    {
        await using var agent = Agent();
        var conversation = await agent.CreateSessionAsync();
        await TurnAsync(agent, conversation);

        await using var laptop = await StartAsync("laptop");
        await TurnAsync(agent, conversation);

        ToolsOf(_requests[0]).ShouldNotContain(name => name.Contains("outpost-laptop"));
        ToolsOf(_requests[1]).ShouldContain(name => name.Contains("outpost-laptop"));
    }

    [Fact]
    public async Task AnOutpostThatClosesMidConversation_LeavesTheNextTurn()
    {
        var laptop = await StartAsync("laptop");
        await using var agent = Agent();
        var conversation = await agent.CreateSessionAsync();
        await TurnAsync(agent, conversation);

        await CloseAsync(laptop, "laptop");
        await TurnAsync(agent, conversation);

        ToolsOf(_requests[0]).ShouldContain(name => name.Contains("outpost-laptop"));
        ToolsOf(_requests[1]).ShouldNotContain(name => name.Contains("outpost-laptop"));
    }

    // Reopened between turns, the machine lists the same tools under the same name, but on a new
    // opening of its tunnel: the next turn reaches it there, not through the one that closed.
    [Fact]
    public async Task AnOutpostThatReopensMidConversation_IsReachedThroughItsNewTunnelOnTheNextTurn()
    {
        var before = await StartAsync("laptop");
        await using var agent = Agent();
        var conversation = await agent.CreateSessionAsync();
        await TurnAsync(agent, conversation);

        await CloseAsync(before, "laptop");
        await using var reopened = await StartAsync("laptop");
        _callTheMachineOnNextRequest = true;
        await TurnAsync(agent, conversation);

        var answer = _requests.Last().Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .ShouldHaveSingleItem();
        answer.Result?.ToString().ShouldNotContain("went offline");
    }

    // Only the home household exists in this phase, so a second one is written straight into the store.
    private async Task<string> SeedHouseholdAsync()
    {
        await using var db = await _accounts.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        var tenant = new Tenant { Id = Tokens.NewId(), Name = "Next door", CreatedAt = _accounts.Clock.GetUtcNow() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    // The deployment's own vault, so a session that mounts no machine still has a filesystem to show.
    private McpServerEndpoint Vault => McpServerEndpoint.Configured(vault.McpEndpoint, McpTestSecret.Value);

    private async Task<OutpostMachine> StartAsync(string name, bool jailed = false, bool exec = false)
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, OutpostServerName.For(name));
        var machine = await OutpostMachine.StartAsync(_relay.Hub, credential.Credential, name, jailed, exec);
        await Eventually.Until(
            () => _relay.Registry.Find(_household, OutpostServerName.For(name)) is not null, $"{name}'s tunnel opens");
        return machine;
    }

    // As an opted-in agent of the household builds its session: its configured endpoints, the live
    // outposts asked for at the moment it is built, every call signed as the agent signs it.
    private async Task<ThreadSession> BuildAsync(params McpServerEndpoint[] configured)
    {
        var composed = OutpostEndpoints.Compose(configured, _outposts, usesOutposts: true, _household);
        var session = await ThreadSession.CreateAsync(
            composed.Endpoints, "outpost-tunnel-test", "fran", "the agent under test", [],
            new HashSet<string> { "text_read", "exec" }, null, CancellationToken.None,
            callTokens: new CallTokens(_keys, TimeProvider.System));
        OutpostEndpoints.DeclareUnreachable(session.FileSystemRegistry, composed.Outposts, session.ClientManager.DialledServers);
        return session;
    }

    private async Task CloseAsync(OutpostMachine machine, string name)
    {
        await machine.DisposeAsync();
        await Eventually.Until(
            () => _relay.Registry.Find(_household, OutpostServerName.For(name)) is null, $"{name}'s tunnel closes");
    }

    // An opted-in agent of the household, its calls signed as the deployment signs them and its tool
    // calls run as a chat client runs them.
    private McpAgent Agent() => new(
        TestAgentSpec.Default with { TenantId = _household, UsesOutposts = true },
        new FunctionInvokingChatClient(ChatClient()),
        new Mock<IThreadStateStore>().Object,
        _metrics,
        TimeProvider.System,
        [],
        [],
        outposts: _outposts,
        callTokens: new CallTokens(_keys, TimeProvider.System));

    // Answers "ok" — or, once asked to, first calls the machine's fs_info on its root.
    private IChatClient ChatClient()
    {
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
            {
                _requests.Add(([.. messages], options));
                var info = options?.Tools?.FirstOrDefault(t => t.Name.Contains("outpost-laptop") && t.Name.EndsWith("__fs_info"));
                ChatResponseUpdate update = _callTheMachineOnNextRequest && info is not null
                    ? new()
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new FunctionCallContent("call-1", info.Name, new Dictionary<string, object?> { ["path"] = "/" })]
                    }
                    : new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] };
                _callTheMachineOnNextRequest = false;
                return new[] { update }.ToAsyncEnumerable();
            });
        return client.Object;
    }

    private static Task TurnAsync(McpAgent agent, Microsoft.Agents.AI.AgentSession conversation) =>
        agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "what is on my laptop?")], conversation).ToListAsync().AsTask();

    private static IReadOnlyList<string> ToolsOf((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        [.. request.Options.ShouldNotBeNull().Tools.ShouldNotBeNull().Select(t => t.Name)];

    private static FileSystemResolution Resolve(IVirtualFileSystemRegistry registry, string path) =>
        registry.Resolve(path).TryGetValue(out var resolution, out _)
            ? resolution!
            : throw new InvalidOperationException($"{path} does not resolve");
}