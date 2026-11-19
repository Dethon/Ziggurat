using System.Net;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.DTOs.Tunnel;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools;
using Domain.Tools.Sugary.Watches;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Timers;
using McpChannelVoice.Services;
using McpServerSugary.Glucose;
using McpServerSugary.Modules;
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
// voice server of the household a timer was armed for, which is real and reached through its tunnel
// alone. Both ends present only what they already hold — the timers server its own /mcp credential,
// which the agent holds to dial it; the relay the voice server's tunnel credential — and the relay
// lets the caller reach that API, of the household the request names, and nothing else.
public class RelayedCallsTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string Timers = "mcp-timers";
    private const string TimersCredential = "timers-mcp-secret";
    private const string Sugary = "mcp-sugary";
    private const string SugaryCredential = "sugary-mcp-secret";

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
            sharedServers: new Dictionary<string, string> { [Timers] = TimersCredential, [Sugary] = SugaryCredential });
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
        var files = As(timers, _household);
        var ringing = FireLoop(timers);
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

        var created = await As(timers, _household).CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "target": {"room": "Attic"}}""", false, true, CancellationToken.None);

        var error = created.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        error.Message.ShouldContain("kitchen-01");
    }

    [Fact]
    public async Task ATimerCreatedWhileTheVoiceServerIsOffline_FailsSayingTheHubIsUnreachable()
    {
        await using var timers = TimersServer();

        var created = await As(timers, _household).CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        var error = created.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Message.ShouldContain("unreachable");
        (await timers.GetRequiredService<ITimerStore>().ListAsync(_household, CancellationToken.None)).ShouldBeEmpty();
    }

    public static TheoryData<string, string?, string, string, bool, HttpStatusCode> Refused => new()
    {
        { "no credential", null, HouseholdServerNames.Voice, "api/voice/dismiss", true, HttpStatusCode.Unauthorized },
        { "another server's credential", "not-the-timers-secret", HouseholdServerNames.Voice, "api/voice/dismiss", true, HttpStatusCode.Unauthorized },
        { "another household server", TimersCredential, "vault", "api/voice/dismiss", true, HttpStatusCode.Forbidden },
        { "the voice server's mcp", TimersCredential, HouseholdServerNames.Voice, "mcp", true, HttpStatusCode.Forbidden },
        { "a path out of the api", TimersCredential, HouseholdServerNames.Voice, "api/voice/../../mcp", true, HttpStatusCode.Forbidden },
        // A household's server is always one household's, so a call that does not say whose is for
        // nobody's — never the home's by default.
        { "no household named", TimersCredential, HouseholdServerNames.Voice, "api/voice/dismiss", false, HttpStatusCode.Forbidden }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task ACallTheRelayDoesNotLetThrough_IsRefusedAndNeverReachesTheServer(
        string what, string? credential, string server, string path, bool namesTheHousehold, HttpStatusCode refusal)
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

        if (namesTheHousehold)
        {
            request.Headers.Add(RelayedCallProtocol.HouseholdHeader, _household);
        }

        var answer = await caller.SendAsync(request);

        answer.StatusCode.ShouldBe(refusal, what);
        voice.Alerts.Acknowledge("kitchen-01").ShouldHaveSingleItem();
    }

    // A timer is its household's, and rings on that household's voice server: with two households'
    // servers of the same name connected, each timer reaches its own and never the other.
    [Fact]
    public async Task ATimer_RingsOnItsOwnHouseholdsVoiceServerAndNoOther()
    {
        var neighbours = await _accounts.SeedHouseholdAsync("Neighbours");
        await using var home = await StartVoiceAsync();
        await using var elsewhere = await StartVoiceAsync(neighbours);
        await using var timers = TimersServer();
        var ringing = FireLoop(timers);
        await ringing.StartAsync(CancellationToken.None);

        (await As(timers, neighbours).CreateAsync("/pasta/timer.json",
                """{"durationSeconds": 2, "text": "pasta is ready", "target": {"room": "Kitchen"}}""", false, true,
                CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        await AdvanceUntilAsync(() => elsewhere.Rings > 0, "the neighbours' timer to ring on their kitchen's satellite");
        var dismissed = (await As(timers, neighbours).ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        dismissed.Stdout.ShouldContain("timer \"pasta is ready\"");
        home.Rings.ShouldBe(0);
        await ringing.StopAsync(CancellationToken.None);
    }

    // The household's own voice server being offline is the household's alone: another household's
    // server of the same name, connected, is never reached instead.
    [Fact]
    public async Task ATimerWithOnlyAnotherHouseholdsVoiceConnected_FailsSayingTheHubIsUnreachable()
    {
        await using var elsewhere = await StartVoiceAsync(await _accounts.SeedHouseholdAsync("Neighbours"));
        await using var timers = TimersServer();

        var created = await As(timers, _household).CreateAsync("/pasta/timer.json",
            """{"durationSeconds": 2, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        created.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
    }

    // Sugary rings a glucose alarm on the household's satellites as the timers server rings a timer:
    // through the relay, on its own credential, at the household it names. The hub's dismiss stops
    // whatever rings there, whoever raised it, so "stop the timer" silences Sugary's alarm too.
    [Fact]
    public async Task SugarysAlarm_RingsOnTheHouseholdsVoiceServerThroughTheRelay_AndTheTimersDismissStopsIt()
    {
        await using var voice = await StartVoiceAsync();
        await using var sugary = SugaryServices();
        await using var timers = TimersServer();

        var rung = await sugary.GetRequiredService<IHouseholdVoices>().For(_household).Announcer.StartAsync(new AnnounceRequest
        {
            Target = new AnnounceTarget { Room = "Kitchen" },
            Text = "Glucose is low",
            Kind = AnnounceKind.Alarm,
            Insistent = new InsistentOptions { GapSeconds = 10, MaxRepeats = 12 }
        }, CancellationToken.None);

        rung.Satellites.ShouldHaveSingleItem().Id.ShouldBe("kitchen-01");
        await Eventually.Until(() => voice.Rings > 0, "the alarm to ring on the kitchen's satellite");
        var dismissed = (await As(timers, _household).ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        dismissed.Stdout.ShouldContain("alarm \"Glucose is low\"");
        voice.Alerts.Acknowledge("kitchen-01").ShouldBeEmpty();
    }

    // Sugary's alarm is its household's, and with only another household's voice server connected it
    // reaches none: the relay never sends it to another's of the same name.
    [Fact]
    public async Task SugarysAlarmWithOnlyAnotherHouseholdsVoiceConnected_SaysTheHubIsUnreachable()
    {
        await using var elsewhere = await StartVoiceAsync(await _accounts.SeedHouseholdAsync("Neighbours"));
        await using var sugary = SugaryServices();

        await Should.ThrowAsync<VoiceHubUnavailableException>(() => sugary.GetRequiredService<IHouseholdVoices>()
            .For(_household).Announcer.StartAsync(new AnnounceRequest
            {
                Target = new AnnounceTarget { Room = "Kitchen" }, Text = "Glucose is low", Kind = AnnounceKind.Alarm
            }, CancellationToken.None));
        elsewhere.Rings.ShouldBe(0);
    }

    // A glucose watch's announcement, as Sugary's container builds it, rings on the satellites of the
    // household whose sensor it watches, through the relay — insistently when the watch gives no
    // options, so the voice server holds it as ringing and the household's dismiss stops it.
    [Fact]
    public async Task AGlucoseWatchsAnnouncement_RingsAnAlarmOnItsHouseholdsSatelliteThroughTheRelay()
    {
        await using var voice = await StartVoiceAsync();
        await using var sugary = SugaryServices();

        var rang = await sugary.GetRequiredService<IGlucoseAnnounceEffect>().RingAsync(
            LowWatch(new Account(_household, "user-her")),
            new GlucoseAnnounceEffect("Glucose is low", new JsonObject { ["room"] = "Kitchen" }, null),
            LowReport(), CancellationToken.None);

        rang.ShouldBeTrue();
        await Eventually.Until(() => voice.Rings > 0, "the alarm to ring on the kitchen's satellite");
        var stopped = voice.Alerts.Acknowledge("kitchen-01").ShouldHaveSingleItem();
        stopped.Text.ShouldBe("Glucose is low");
        stopped.Kind.ShouldBe(AnnounceKind.Alarm);
    }

    // With only another household's voice server connected, a watch's alarm reaches none of its
    // satellites: the relay never sends it to another's. Nothing listens on Sugary's channel here, so
    // the fallback to channels rings nothing either.
    [Fact]
    public async Task AGlucoseWatchsAnnouncementWithOnlyAnotherHouseholdsVoiceConnected_RingsNoneOfItsSatellites()
    {
        await using var elsewhere = await StartVoiceAsync(await _accounts.SeedHouseholdAsync("Neighbours"));
        await using var sugary = SugaryServices();

        var rang = await sugary.GetRequiredService<IGlucoseAnnounceEffect>().RingAsync(
            LowWatch(new Account(_household, "user-her")),
            new GlucoseAnnounceEffect("Glucose is low", new JsonObject { ["room"] = "Kitchen" }, null),
            LowReport(), CancellationToken.None);

        rang.ShouldBeFalse();
        await Eventually.Settle();
        elsewhere.Rings.ShouldBe(0);
    }

    private static FiredGlucoseWatch LowWatch(Account subject)
    {
        var spec = new GlucoseWatchSpec
        {
            Name = "low",
            Triggers = [new GlucoseLevelTrigger(GlucoseBound.Below, 70)],
            Effects = [new GlucoseAnnounceEffect("Glucose is low", new JsonObject { ["room"] = "Kitchen" }, null)]
        };
        return new FiredGlucoseWatch(subject, new StoredGlucoseWatch
        {
            Id = "low",
            SubjectUserId = subject.UserId,
            WriterUserId = subject.UserId,
            AgentId = "jonas",
            Spec = spec.ToJson()
        }, spec);
    }

    private static GlucoseTriggerFire LowReport() => new(
        0, new GlucoseLevelTrigger(GlucoseBound.Below, 70), DateTimeOffset.UtcNow,
        new SensorReading(DateTimeOffset.UtcNow, 62, GlucoseTrend.Falling), null, null, DateTimeOffset.UtcNow, null);

    public static TheoryData<string, string, string, bool, HttpStatusCode> RefusedForSugary => new()
    {
        { "the timers server's credential", TimersCredential, "api/voice/dismiss", true, HttpStatusCode.Unauthorized },
        { "the voice server's mcp", SugaryCredential, "mcp", true, HttpStatusCode.Forbidden },
        { "no household named", SugaryCredential, "api/voice/dismiss", false, HttpStatusCode.Forbidden }
    };

    [Theory]
    [MemberData(nameof(RefusedForSugary))]
    public async Task ACallFromSugaryTheRelayDoesNotLetThrough_IsRefusedAndNeverReachesTheServer(
        string what, string credential, string path, bool namesTheHousehold, HttpStatusCode refusal)
    {
        await using var voice = await StartVoiceAsync();
        voice.Alerts.Register(new AlertHandle(new CancellationTokenSource(), ["kitchen-01"], "pasta", AnnounceKind.Timer));
        using var caller = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_relay.Address}{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/{path}");
        request.Headers.Add(RelayedCallProtocol.CallerHeader, Sugary);
        request.Headers.Add("Authorization", SharedSecret.Header(credential));
        if (namesTheHousehold)
        {
            request.Headers.Add(RelayedCallProtocol.HouseholdHeader, _household);
        }

        var answer = await caller.SendAsync(request);

        answer.StatusCode.ShouldBe(refusal, what);
        voice.Alerts.Acknowledge("kitchen-01").ShouldHaveSingleItem();
    }

    private async Task<TunnelledVoiceHub> StartVoiceAsync(string? household = null)
    {
        household ??= _household;
        var credential = await _accounts.MintTunnelCredentialAsync(household, HouseholdServerNames.Voice);
        var voice = await TunnelledVoiceHub.StartAsync(_relay.Hub, credential.Credential, HouseholdServerNames.Voice);
        await Eventually.Until(
            () => _relay.Registry.Find(household, HouseholdServerNames.Voice) is not null, "voice's tunnel opens");
        return voice;
    }

    // The mount as a person of the household sees it, which is how the registrar hands it every call.
    private static TimerFileSystem As(ServiceProvider timers, string household) =>
        (TimerFileSystem)timers.GetRequiredService<TimerFileSystem>().For(new FileSystemCaller(
            new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"),
                Principal: new Principal("signalr", "f1", new Account(household, "f1")),
                Identity: IdentityClaims.Person("f1", household, "signalr", [])),
            null));

    private TimerFireService FireLoop(ServiceProvider timers) => new(
        timers.GetRequiredService<ITimerStore>(), timers.GetRequiredService<IHouseholdVoices>(),
        timers.GetRequiredService<IChannelAlerts>(), _clock, NullLogger<TimerFireService>.Instance);

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

    // Sugary as it ships, told its hub is the relay's endpoint for the voice server, its own
    // credential and the name the agent dials it at.
    private ServiceProvider SugaryServices()
    {
        var services = new ServiceCollection().AddLogging();
        services.ConfigureSugary(new McpServerSugary.Settings.SugarySettings
        {
            Mcp = new McpGateSettings { SharedSecret = SugaryCredential, Audience = Sugary },
            VoiceHub = new() { BaseUrl = $"{_relay.Address}{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/" }
        });
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