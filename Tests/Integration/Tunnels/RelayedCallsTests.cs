using System.Net;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Tunnel;
using Domain.DTOs.Voice;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Timers;
using McpChannelVoice.Services;
using McpServerTimers.Modules;
using McpServerTimers.Services;
using McpServerTimers.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// A shared deployment server reaching a household's own: the timers server, wired as it ships with
// its hub address the relay's endpoint on the agent, rings, dismisses and validates satellites on the
// household's voice server, which is real and reached through its tunnel alone. Both ends present
// only what they already hold — the timers server its own /mcp credential, which the agent holds to
// dial it; the relay the voice server's tunnel credential — and the relay lets the caller reach that
// API and nothing else.
public class RelayedCallsTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string Timers = "mcp-timers";
    private const string TimersCredential = "timers-mcp-secret";

    // TimerFireService's own poll cadence.
    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(1);

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly ArmedClock _clock = new();
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
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
            SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory }), _metrics,
            sharedServers: new Dictionary<string, string> { [Timers] = TimersCredential });
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task ATimerForTheHousehold_IsValidatedRungAndDismissedThroughTheRelay()
    {
        await using var voice = await StartVoiceAsync();
        await using var timers = TimersServer();
        var files = timers.GetRequiredService<TimerFileSystem>();
        var ringing = new TimerFireService(
            timers.GetRequiredService<ITimerStore>(), timers.GetRequiredService<IInsistentAnnouncer>(), _clock,
            NullLogger<TimerFireService>.Instance);
        await ringing.StartAsync(CancellationToken.None);

        var created = await files.CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "text": "pasta is ready", "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        await AdvanceUntilAsync(() => voice.Rings > 0, "the timer to ring on the kitchen's satellite");
        var dismissed = (await files.ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        dismissed.Stdout.ShouldContain("timer \"pasta is ready\"");
        voice.Alerts.Acknowledge("kitchen-01").ShouldBeEmpty();
        await ringing.StopAsync(CancellationToken.None);
    }

    // A target the household's satellites do not answer to is refused at create, as before: the
    // satellite roster and the resolution both come from the voice server through the relay.
    [Fact]
    public async Task ATimerForARoomWithNoSatellite_IsRefusedNamingTheHouseholdsSatellites()
    {
        await using var voice = await StartVoiceAsync();
        await using var timers = TimersServer();

        var created = await timers.GetRequiredService<TimerFileSystem>().CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "target": {"room": "Attic"}}""", false, true, CancellationToken.None);

        var error = created.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        error.Message.ShouldContain("kitchen-01");
    }

    [Fact]
    public async Task ATimerCreatedWhileTheVoiceServerIsOffline_FailsSayingTheHubIsUnreachable()
    {
        await using var timers = TimersServer();

        var created = await timers.GetRequiredService<TimerFileSystem>().CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        var error = created.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Message.ShouldContain("unreachable");
        (await timers.GetRequiredService<ITimerStore>().ListAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    public static TheoryData<string, string?, string, string, HttpStatusCode> Refused => new()
    {
        { "no credential", null, HouseholdServerNames.Voice, "api/voice/dismiss", HttpStatusCode.Unauthorized },
        { "another server's credential", "not-the-timers-secret", HouseholdServerNames.Voice, "api/voice/dismiss", HttpStatusCode.Unauthorized },
        { "another household server", TimersCredential, "vault", "api/voice/dismiss", HttpStatusCode.Forbidden },
        { "the voice server's mcp", TimersCredential, HouseholdServerNames.Voice, "mcp", HttpStatusCode.Forbidden },
        { "a path out of the api", TimersCredential, HouseholdServerNames.Voice, "api/voice/../../mcp", HttpStatusCode.Forbidden }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ACallTheRelayDoesNotLetThrough_IsRefusedAndNeverReachesTheServer(
        string what, string? credential, string server, string path, HttpStatusCode refusal)
    {
        await using var voice = await StartVoiceAsync();
        voice.Alerts.Register(new AlertHandle(new CancellationTokenSource(), ["kitchen-01"], "pasta", AnnounceKind.Timer));
        using var caller = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_relay.Address}{RelayedCallProtocol.For(server)}/{path}");
        request.Headers.Add(RelayedCallProtocol.CallerHeader, Timers);
        if (credential is not null)
        {
            request.Headers.Add("Authorization", SharedSecret.Header(credential));
        }

        var answer = await caller.SendAsync(request);

        answer.StatusCode.ShouldBe(refusal, what);
        voice.Alerts.Acknowledge("kitchen-01").ShouldHaveSingleItem();
    }

    private async Task<TunnelledVoiceHub> StartVoiceAsync()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, HouseholdServerNames.Voice);
        var voice = await TunnelledVoiceHub.StartAsync(_relay.Hub, credential.Credential, HouseholdServerNames.Voice);
        await Eventually.Until(
            () => _relay.Registry.Find(_household, HouseholdServerNames.Voice) is not null, "voice's tunnel opens");
        return voice;
    }

    // The timers server as it ships, told its hub is the relay's endpoint for the voice server, its
    // own credential and the name the agent dials it at; on a clock the test drives.
    private ServiceProvider TimersServer()
    {
        var services = new ServiceCollection().AddLogging();
        services.ConfigureTimers(new TimerSettings
        {
            Mcp = new McpGateSettings { SharedSecret = TimersCredential, Audience = Timers },
            VoiceHub = new() { BaseUrl = $"{_relay.Address}{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/" }
        });
        services.AddSingleton<TimeProvider>(_clock);
        return services.BuildServiceProvider();
    }

    // The fire loop's PeriodicTimer arms once and is reused, so the clock is driven an interval at a
    // time until the ring lands rather than advanced onto one wait.
    private async Task AdvanceUntilAsync(Func<bool> rung, string because)
    {
        await _clock.WaitForLiveAsync(_poll);
        await Eventually.Until(() =>
        {
            if (!rung())
            {
                _clock.Advance(_poll);
            }

            return rung();
        }, because);
    }
}