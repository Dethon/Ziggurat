using Agent.Commands;
using Agent.Settings;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// The operator's key commands run beside the agent in a process of their own, which holds no tunnel.
// A channel server named by its server name — Home Assistant's, keeping its watches' delegations — is
// every household's server of that name, each reached through the running agent's relay, so its
// delegations are listed and signed again as the scheduling server's are, each reported under its
// household. One that is not connected is not asked, and retiring keys still refuses for it.
public class RedelegationOverTunnelTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";

    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private AccountsServer.MintedLink _fran = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _fran = await _accounts.MintOperatorAsync("Fran");
        _keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, _keys, new RecordingMetricsPublisher());
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task AHomeServerNamedByItsName_IsAskedThroughTheRunningAgent_AndItsDelegationsSignedAgain()
    {
        var watches = Watch("washer-done", _fran.TenantId, _fran.UserId);
        var credential = await _accounts.MintTunnelCredentialAsync(_fran.TenantId, "homeassistant");
        await using var server = await TunnelledServer.StartAsync(
            _relay.Hub, credential.Credential, "homeassistant", delegations: watches);
        await Eventually.Until(() => server.IsAdmitted, "the home's Home Assistant server is admitted");

        var exit = await Redelegation().RedelegateAsync(await ServersAsync(), assumeYes: true, CancellationToken.None);

        _output.ToString().ShouldNotContain("not asked");
        _output.ToString().ShouldNotContain("could not be asked");
        _output.ToString().ShouldContain($"{Home}: asked");
        exit.ShouldBe(0);
        watches.Replaced.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe("watch"), r => r.Id.ShouldBe("washer-done"), r => r.Token.ShouldNotBeNullOrEmpty());
    }

    [Fact]
    public async Task AHomeServerWhoseTunnelIsNotOpen_IsNotAsked_AndRetiringRefuses()
    {
        var stranded = await Redelegation().StrandedByRetiringAsync(await ServersAsync(), CancellationToken.None);

        stranded.ShouldBeNull();
        _output.ToString().ShouldContain($"{Home}: not asked");
    }

    [Fact]
    public async Task AnotherHouseholdsServer_IsAskedThroughItsOwnTunnel_AndItsDelegationsSignedAgain()
    {
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        var homeWatches = Watch("washer-done", _fran.TenantId, _fran.UserId);
        var casaWatches = Watch("door-open", casa, lucia);
        var homeCredential = await _accounts.MintTunnelCredentialAsync(_fran.TenantId, "homeassistant");
        var casaCredential = await _accounts.MintTunnelCredentialAsync(casa, "homeassistant");
        await using var home = await TunnelledServer.StartAsync(
            _relay.Hub, homeCredential.Credential, "homeassistant", delegations: homeWatches);
        await using var casaServer = await TunnelledServer.StartAsync(
            _relay.Hub, casaCredential.Credential, "homeassistant", delegations: casaWatches);
        await Eventually.Until(
            () => home.IsAdmitted && casaServer.IsAdmitted, "both households' Home Assistant servers are admitted");

        var exit = await Redelegation().RedelegateAsync(await ServersAsync(), assumeYes: true, CancellationToken.None);

        exit.ShouldBe(0, _output.ToString());
        _output.ToString().ShouldContain($"homeassistant of household Casa ({casa}): asked");
        _output.ToString().ShouldContain($"{Home}: asked");
        casaWatches.Replaced.ShouldHaveSingleItem().Id.ShouldBe("door-open");
        homeWatches.Replaced.ShouldHaveSingleItem().Id.ShouldBe("washer-done");
    }

    [Fact]
    public async Task AnotherHouseholdsServerThatIsNotConnected_IsListedAsNotAsked_UnderItsHousehold()
    {
        var (casa, _) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await _accounts.MintTunnelCredentialAsync(casa, "homeassistant");
        var homeCredential = await _accounts.MintTunnelCredentialAsync(_fran.TenantId, "homeassistant");
        await using var home = await TunnelledServer.StartAsync(
            _relay.Hub, homeCredential.Credential, "homeassistant",
            delegations: Watch("washer-done", _fran.TenantId, _fran.UserId));
        await Eventually.Until(() => home.IsAdmitted, "the home's Home Assistant server is admitted");

        var stranded = await Redelegation().StrandedByRetiringAsync(await ServersAsync(), CancellationToken.None);

        stranded.ShouldBeNull();
        _output.ToString().ShouldContain($"homeassistant of household Casa ({casa}): not asked");
        _output.ToString().ShouldContain($"{Home}: asked");
    }

    [Fact]
    public async Task AHouseholdHoldingNoCredentialForTheServer_IsNotListed()
    {
        var (casa, _) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        await _accounts.MintTunnelCredentialAsync(casa, "vault");

        await Redelegation().StrandedByRetiringAsync(await ServersAsync(), CancellationToken.None);

        _output.ToString().ShouldContain($"{Home}: not asked");
        _output.ToString().ShouldNotContain(casa);
    }

    [Fact]
    public async Task AnAgentThatRefusesTheListing_LeavesTheServerUnasked_AndRetiringRefuses()
    {
        var servers = await CommandServers.OfAsync(
            [new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "homeassistant" }],
            new McpCredentials(new Dictionary<string, string>()), new CallTokens(_keys, TimeProvider.System),
            new Uri(_relay.Address), "not-the-agent-api-secret", CancellationToken.None);

        var stranded = await Redelegation().StrandedByRetiringAsync(servers, CancellationToken.None);

        stranded.ShouldBeNull();
        _output.ToString().ShouldContain("homeassistant: could not be asked");
    }

    private string Home => $"homeassistant of household Home ({_fran.TenantId}, the home)";

    private Redelegation Redelegation() =>
        new(_keys, new CallTokens(_keys, TimeProvider.System), TimeProvider.System, new TokenVerificationSettings(),
            new StringReader(""), _output);

    private Task<IReadOnlyList<Redelegation.Server>> ServersAsync() => CommandServers.OfAsync(
        [new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "homeassistant" }],
        new McpCredentials(new Dictionary<string, string>()), new CallTokens(_keys, TimeProvider.System),
        new Uri(_relay.Address), AgentCredential, CancellationToken.None);

    private static KeptDelegations Watch(string id, string tenantId, string userId) =>
        new(new StoredDelegation(
            "watch", id, Token: null, Description: """{"agent":"jonas"}""",
            new Principal("signalr", userId, new Account(tenantId, userId))));

    private sealed class KeptDelegations(StoredDelegation kept) : IDelegationStore
    {
        private readonly List<(string Kind, string Id, string Token)> _replaced = [];

        public IReadOnlyList<(string Kind, string Id, string Token)> Replaced
        {
            get
            {
                lock (_replaced)
                {
                    return [.. _replaced];
                }
            }
        }

        public Task<IReadOnlyList<StoredDelegation>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredDelegation>>([kept]);

        public Task<bool> ReplaceAsync(string kind, string id, string token, CancellationToken ct)
        {
            lock (_replaced)
            {
                _replaced.Add((kind, id, token));
            }

            return Task.FromResult(kind == kept.Kind && id == kept.Id);
        }
    }
}