using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Tunnel;
using Domain.Outposts;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Topics;
using Infrastructure.Metrics;
using Infrastructure.Tunnels;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;
using Tests.Unit.Domain.Tools.FileSystem.Bridge;

namespace Tests.Integration.Tunnels;

// A household's server reaching back to the deployment, each end the real thing on a real socket:
// the server is configured with a loopback port per back-call and nothing else, its clients are the
// ones it ships with pointed at those ports, and the relay's gateway decides what gets through and
// says who asked. What a target sees is read at the target: the accounts service's own answers, the
// agent's own exec bridge, the feed the relay publishes, and stand-ins recording what reached them.
public class BackCallGatewayTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private string _home = null!;
    private string _fran = null!;
    private string _household = null!;
    private string _ana = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        var fran = await _accounts.MintOperatorAsync("Fran");
        (_home, _fran) = (fran.TenantId, fran.UserId);
        (_household, _ana) = await _accounts.SeedHouseholdAsync("Ana's", "Ana");
        var keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, keys, _metrics);
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    // A tunnelled voice holds no deployment secret: its resolver presents whatever it has (nothing),
    // and the relay shows the accounts service the tunnel's own credential, whose household it answers
    // about — that household's guest, and its people alone.
    [Fact]
    public async Task TheAccountsBackCall_AnswersAboutTheTunnelsHouseholdAlone()
    {
        var port = await VoiceOfAsync(_household, BackCallProtocol.Accounts);
        var accounts = new AccountsResolverClient(new HttpClient { BaseAddress = Loopback(port) }, "");

        var guest = await accounts.GuestAsync(ChannelIds.Voice, CancellationToken.None);
        var ana = await accounts.ResolveAsync(ChannelIds.Voice, _ana, CancellationToken.None);
        var fran = await accounts.ResolveAsync(ChannelIds.Voice, _fran, CancellationToken.None);

        guest.TenantId.ShouldBe(_household);
        ana.ShouldNotBeNull().Account.TenantId.ShouldBe(_household);
        fran.ShouldBeNull();
    }

    [Fact]
    public async Task TheTopicsBackCall_ReachesTopicMintingNamingTheHouseholdsServer()
    {
        var port = await VoiceOfAsync(_home, BackCallProtocol.Topics);
        var topics = new AgentTopicsClient(new HttpClient { BaseAddress = Loopback(port) });

        var minted = await topics.MintAsync(new TopicMintRequest("nabu", "Kitchen", "fran"), "an-assertion", CancellationToken.None);

        minted.TopicId.ShouldBe("topic-1");
        _relay.Reached.ShouldHaveSingleItem().ShouldBe(new ReachedTarget(TopicMinting.Path, _home, "voice", null));
    }

    // What the server says about who it is never reaches the target: the relay's word replaces it.
    [Fact]
    public async Task TheLemonadeBackCall_ReachesLemonadeNamingTheHouseholdsServerOverWhatTheServerClaimed()
    {
        var port = await VoiceOfAsync(_home, BackCallProtocol.Lemonade);
        using var http = new HttpClient { BaseAddress = Loopback(port) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech") { Content = new StringContent("{}") };
        request.Headers.Add(BackCallProtocol.HouseholdHeader, "someone-elses");
        request.Headers.Add(BackCallProtocol.ServerHeader, "vault");

        using var response = await http.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).ShouldBe("spoken");
        _relay.Reached.ShouldHaveSingleItem().ShouldBe(new ReachedTarget("/v1/audio/speech", _home, "voice", null));
    }

    [Fact]
    public async Task AMetricEvent_IsPublishedOntoTheFeedAsTheHouseholdsServers()
    {
        var port = await VoiceOfAsync(_home, BackCallProtocol.Metrics);
        var sink = new HttpMetricSink(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}{BackCallProtocol.MetricsPath}") });

        await sink.SendAsync(new HeartbeatEvent { Service = "mcp-channel-voice" });

        _metrics.Published.OfType<HeartbeatEvent>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Service.ShouldBe("mcp-channel-voice"),
            e => e.Origin.ShouldBe(ServerAddress.Tunnelled(_home, "voice").Name));
    }

    // The sandbox's FUSE daemon asks the bridge one operation per request, with the exec's call token
    // as its only credential; pointed at its back-call port, a command's read and write of another
    // mount reach that mount through the tunnel.
    [Fact]
    public async Task TheBridgeBackCall_ReadsAndWritesAnotherMountWithTheCallsToken()
    {
        var port = await ServerOfAsync(_home, "sandbox", BackCallProtocol.VfsBridge);
        var notes = new MemoryDisk("notes", new Dictionary<string, string> { ["todo.md"] = "milk" });
        var call = _relay.Bridge.Mint(BridgeFixtures.Registry((notes, "/notes", null)), BridgeFixtures.Everything, null, null);
        using var daemon = new HttpClient { BaseAddress = Loopback(port) };
        daemon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", call.Token);

        using var read = await daemon.PostAsync($"{BackCallTargets.BridgeRoute.TrimStart('/')}/read?path=/notes/todo.md", null);
        using var write = await daemon.PostAsync(
            $"{BackCallTargets.BridgeRoute.TrimStart('/')}/write?path=/notes/done.md&new=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes("eggs")));

        (await read.Content.ReadAsStringAsync()).ShouldBe("milk");
        write.StatusCode.ShouldBe(HttpStatusCode.OK, await write.Content.ReadAsStringAsync());
        notes.Files["done.md"].ShouldBe("eggs");
    }

    public static TheoryData<string, string> OtherPaths => new()
    {
        { BackCallProtocol.Accounts, "accounts/internal/tunnels/verify" },
        { BackCallProtocol.Accounts, "accounts/internal/telegram/start" },
        { BackCallProtocol.Lemonade, "api/v1/load" },
        { BackCallProtocol.Topics, "api/agents" }
    };

    [Theory]
    [MemberData(nameof(OtherPaths))]
    public async Task ABackCallToAPathItsTagDoesNotAllow_IsRefusedAndReported(string tag, string path)
    {
        var port = await VoiceOfAsync(_home, tag);
        using var http = new HttpClient { BaseAddress = Loopback(port) };

        using var response = await http.PostAsync(path, new StringContent("{}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _relay.Reached.ShouldBeEmpty();
        Refusals().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.ShouldContain("'voice'"), m => m.ShouldContain(_home), m => m.ShouldContain("/" + path));
    }

    // A back-call is for the server that makes it: a household's printer, vault or library — or an
    // outpost on a member's laptop — holding its own credential would otherwise mint voice assertions
    // for anybody in its household, topics as them, or speech on the deployment's Lemonade; voice would
    // reach the exec bridge; an outpost, which publishes nothing, would write onto the feed.
    public static TheoryData<string, string, string> OtherServers => new()
    {
        { "vault", BackCallProtocol.Accounts, "accounts/internal/guest" },
        { OutpostServerName.For("laptop"), BackCallProtocol.Accounts, "accounts/internal/resolve" },
        { "printer", BackCallProtocol.Topics, TopicMinting.Path.TrimStart('/') },
        { "library", BackCallProtocol.Lemonade, "v1/audio/speech" },
        { HouseholdServerNames.Voice, BackCallProtocol.VfsBridge, $"{BackCallTargets.BridgeRoute.TrimStart('/')}/read?path=/notes/todo.md" },
        { OutpostServerName.For("laptop"), BackCallProtocol.Metrics, BackCallProtocol.MetricsPath.TrimStart('/') }
    };

    [Theory]
    [MemberData(nameof(OtherServers))]
    public async Task ABackCallFromAServerItIsNotFor_IsRefusedAndReported(string serverName, string tag, string path)
    {
        var port = await ServerOfAsync(_home, serverName, tag);
        using var http = new HttpClient { BaseAddress = Loopback(port) };

        using var response = await http.PostAsync(path, new StringContent("{\"channel\":\"voice\"}"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        _relay.Reached.ShouldBeEmpty();
        Refusals().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.ShouldContain($"'{serverName}'"), m => m.ShouldContain(_home), m => m.ShouldContain($"'{tag}'"));
    }

    // A server configured with a back-call the deployment does not offer opens a stream nobody takes.
    [Fact]
    public async Task AStreamForAnythingButABackCall_IsRefusedAndReported()
    {
        var port = await VoiceOfAsync(_home, "redis");
        using var http = new HttpClient { BaseAddress = Loopback(port) };

        await Should.ThrowAsync<HttpRequestException>(() => http.PostAsync("", new StringContent("PING")));

        await Eventually.Until(() => Refusals().Count > 0, "the refusal is reported");
        Refusals().ShouldHaveSingleItem().ShouldSatisfyAllConditions(m => m.ShouldContain("'redis'"), m => m.ShouldContain(_home));
    }

    private IReadOnlyList<string> Refusals() =>
        [.. _metrics.Published.OfType<ErrorEvent>().Where(e => e.ErrorType == BackCallGateway.RefusedErrorType).Select(e => e.Message)];

    private Task<int> VoiceOfAsync(string household, string tag) => ServerOfAsync(household, "voice", tag);

    // A household's server whose tunnel is open, making one back-call at the port returned.
    private async Task<int> ServerOfAsync(string household, string serverName, string tag)
    {
        var credential = await _accounts.MintTunnelCredentialAsync(household, serverName);
        var port = TestPort.GetAvailable();
        _servers.Add(await TunnelledServer.StartAsync(
            _relay.Hub, credential.Credential, serverName, new Dictionary<string, int> { [tag] = port }));
        await Eventually.Until(() => _servers[^1].IsAdmitted, $"{serverName}'s tunnel is admitted");
        return port;
    }

    private readonly List<TunnelledServer> _servers = [];

    private static Uri Loopback(int port) => new($"http://127.0.0.1:{port}/");
}