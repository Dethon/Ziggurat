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
// A home server the definitions name by its server name — Home Assistant's, keeping its watches'
// delegations, once it runs in the tenant kit — is reached through the running agent's relay, so its
// delegations are listed and signed again as the scheduling server's are; one whose tunnel is not
// open still cannot be asked, and retiring keys still refuses for it.
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
        var watches = new KeptDelegations(new StoredDelegation(
            "watch", "washer-done", Token: null, Description: """{"agent":"jonas"}""",
            new Principal("signalr", _fran.UserId, new Account(_fran.TenantId, _fran.UserId))));
        var credential = await _accounts.MintTunnelCredentialAsync(_fran.TenantId, "homeassistant");
        await using var server = await TunnelledServer.StartAsync(
            _relay.Hub, credential.Credential, "homeassistant", delegations: watches);
        await Eventually.Until(() => server.IsAdmitted, "the home's Home Assistant server is admitted");

        var exit = await Redelegation().RedelegateAsync(Servers(), assumeYes: true, CancellationToken.None);

        _output.ToString().ShouldNotContain("could not be asked");
        exit.ShouldBe(0);
        watches.Replaced.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Kind.ShouldBe("watch"), r => r.Id.ShouldBe("washer-done"), r => r.Token.ShouldNotBeNullOrEmpty());
    }

    [Fact]
    public async Task AHomeServerWhoseTunnelIsNotOpen_StillCannotBeAsked()
    {
        var stranded = await Redelegation().StrandedByRetiringAsync(Servers(), CancellationToken.None);

        stranded.ShouldBeNull();
        _output.ToString().ShouldContain("homeassistant: could not be asked");
    }

    private Redelegation Redelegation() =>
        new(_keys, new CallTokens(_keys, TimeProvider.System), TimeProvider.System, new TokenVerificationSettings(),
            new StringReader(""), _output);

    private IReadOnlyList<Redelegation.Server> Servers() => CommandServers.Of(
        [new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "homeassistant" }],
        new McpCredentials(new Dictionary<string, string>()), new CallTokens(_keys, TimeProvider.System),
        new Uri(_relay.Address), AgentCredential);

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