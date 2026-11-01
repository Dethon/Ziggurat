using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Security.Signing;
using Domain.Tools;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Timers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Timers.Vfs;

public class TimerFileSystemJourneyTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private sealed class FakeDismisser : IAlertDismisser
    {
        public List<DismissedAlert> Ringing { get; } = [];
        public Task<IReadOnlyList<DismissedAlert>> DismissAllAsync(CancellationToken ct)
        {
            var result = Ringing.ToList();
            Ringing.Clear();
            return Task.FromResult<IReadOnlyList<DismissedAlert>>(result);
        }
    }

    private sealed class FakeSatelliteCatalog : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SatelliteDescriptor>>([new("kitchen-01", "Kitchen")]);

        public async Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct)
        {
            // Yield so a caller that forgets to await this cross-process resolve is caught by the tests.
            await Task.Yield();
            static bool exists(string id) => id == "kitchen-01";
            if (target.SatelliteIds is { Count: > 0 })
            {
                return target.SatelliteIds.Where(id => id is not null && exists(id)).ToList();
            }
            if (target.SatelliteId is not null)
            {
                return exists(target.SatelliteId) ? [target.SatelliteId] : [];
            }
            if (target.Room is not null)
            {
                return target.Room.Equals("Kitchen", StringComparison.OrdinalIgnoreCase) ? ["kitchen-01"] : [];
            }
            return target.All == true ? ["kitchen-01"] : [];
        }
    }

    private sealed class UnreachableCatalog : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) =>
            throw new VoiceHubUnavailableException("connection refused");

        public Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct) =>
            throw new VoiceHubUnavailableException("connection refused");
    }

    private sealed class UnreachableDismisser : IAlertDismisser
    {
        public Task<IReadOnlyList<DismissedAlert>> DismissAllAsync(CancellationToken ct) =>
            throw new VoiceHubUnavailableException("connection refused");
    }

    private sealed class EmptyCatalog : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SatelliteDescriptor>>([]);

        public Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoAnnouncer : IInsistentAnnouncer
    {
        public Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("the mount never rings; the fire loop does");
    }

    // Each household's own voice server, as the relay reaches it by naming the household; one not
    // listed is a household whose voice server is not connected.
    private sealed class FakeVoices : IHouseholdVoices
    {
        public Dictionary<string, (IAlertDismisser Dismisser, ISatelliteCatalog Satellites)> Households { get; } = [];

        public HouseholdVoice For(string tenantId) =>
            Households.TryGetValue(tenantId, out var voice)
                ? new HouseholdVoice(new NoAnnouncer(), voice.Dismisser, voice.Satellites)
                : new HouseholdVoice(new NoAnnouncer(), new UnreachableDismisser(), new UnreachableCatalog());
    }

    // What rings on people's channels, by household, as the timers server holds it.
    private sealed class FakeChannelAlerts : IChannelAlerts
    {
        public Dictionary<string, List<DismissedAlert>> Ringing { get; } = [];

        public Task<bool> RingAsync(ArmedTimer timer, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<DismissedAlert>> StopAllAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DismissedAlert>>(Ringing.Remove(tenantId, out var ringing) ? ringing : []);
    }

    private const string Home = "0a1b";
    private const string Neighbours = "0c2d";
    private static readonly Account _fran = new(Home, "f1");
    private static readonly Account _ana = new(Neighbours, "a1");

    // A call as the registrar hands it to the mount: the token's identity and the token's principal.
    private static FileSystemCaller PersonOf(Account account) =>
        new(new ConversationContext("jonas", "conv-1", account.UserId, new ReplyTarget("signalr", "conv-1"),
            Principal: new Principal("signalr", account.UserId, account),
            Identity: IdentityClaims.Person(account.UserId, account.TenantId, "signalr", [])), null);

    private static FileSystemCaller GuestOf(string household) =>
        new(new ConversationContext("nabu", "conv-2", "household", new ReplyTarget("voice", "conv-2"),
            Principal: new Principal("voice", IdentityClaims.GuestSubject),
            Identity: IdentityClaims.Guest(household, "voice")), null);

    private sealed record Mount(
        TimerFileSystem Server, InMemoryTimerStore Store, FakeTimeProvider Time, FakeVoices Voices, FakeChannelAlerts Alerts)
    {
        public TimerFileSystem As(FileSystemCaller caller) => (TimerFileSystem)Server.For(caller);
    }

    private static Mount BuildMount(TimeSpan? regexMatchTimeout = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var store = new InMemoryTimerStore();
        var voices = new FakeVoices();
        voices.Households[Home] = (new FakeDismisser(), new FakeSatelliteCatalog());
        voices.Households[Neighbours] = (new FakeDismisser(), new FakeSatelliteCatalog());
        var alerts = new FakeChannelAlerts();
        return new Mount(new TimerFileSystem(store, time, voices, alerts, regexMatchTimeout), store, time, voices, alerts);
    }

    // Fran's view of the mount, which is what every journey below drives unless it says otherwise.
    private static (TimerFileSystem Fs, InMemoryTimerStore Store, FakeTimeProvider Time, FakeDismisser Dismisser) Build()
    {
        var mount = BuildMount();
        return (mount.As(PersonOf(_fran)), mount.Store, mount.Time, (FakeDismisser)mount.Voices.Households[Home].Dismisser);
    }

    private const string PastaSpec = """
        {"durationSeconds": 300, "text": "pasta is ready", "target": {"room": "Kitchen"}}
        """;

    // A timer directory holds one writable file, so a body written to /<id> can only mean it —
    // the same leniency the schedules mount has, for the same reason: the refusal only bought the
    // same call with the file name on.
    [Fact]
    public async Task Create_AtTheTimerDirectory_IsTheTimerFile()
    {
        var (fs, store, _, _) = Build();

        var created = await fs.CreateAsync("/pasta", PastaSpec, false, true, CancellationToken.None);

        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>().Value.FilePath.ShouldEndWith("/pasta/timer.json");
        (await store.GetAsync(Home, "pasta", CancellationToken.None)).ShouldNotBeNull();
    }

    // A household away from the server's zone reads when its timer fires on its own clock: the call's
    // household names its zone, as the schedules mount reads its schedules in it.
    [Fact]
    public async Task AStatus_ShowsWhenTheTimerFires_InTheCallingHouseholdsZone()
    {
        var mount = BuildMount();
        var fs = mount.As(PersonOf(_fran) with { Household = new ZonedHousehold(Home, "America/New_York") });
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var status = (await fs.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        status.Content.ShouldContain("-04:00"); // EDT on 2 July, not Madrid's +02:00
    }

    private sealed class ZonedHousehold(string id, string zone) : ICallHousehold
    {
        public string Id => id;

        public Task<HouseholdSettings> SettingsAsync(CancellationToken ct) =>
            Task.FromResult(new HouseholdSettings { Name = "Casa", TimeZone = zone });

        public Task<IdealistaKeys?> IdealistaKeysAsync(CancellationToken ct) => Task.FromResult<IdealistaKeys?>(null);

        public Task<HouseholdAgentNames> AgentNamesAsync(CancellationToken ct) =>
            Task.FromResult(new HouseholdAgentNames(null, []));
    }

    [Fact]
    public async Task CreateReadStatusCancel_FullJourney()
    {
        var (fs, _, time, _) = Build();

        var created = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var glob = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldBe(["/dismiss", "/pasta/", "/pasta/status.json", "/pasta/timer.json", "/satellites.json"]);
        glob.Executables.ShouldBe(["/dismiss"]);

        var spec = (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        spec.Content.ShouldContain("\"durationSeconds\": 300");
        spec.Content.ShouldContain("pasta is ready");

        time.Advance(TimeSpan.FromSeconds(100));
        var status = (await fs.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("\"remainingSeconds\": 200");
        status.Content.ShouldContain("+02:00"); // firesAt rendered in the operating zone (CEST)

        var deleted = await fs.DeleteAsync("/pasta", CancellationToken.None);
        deleted.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        (await fs.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();
    }

    [Fact]
    public async Task Create_InvalidDuration_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 0, "target": {"room": "Kitchen"}}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("durationSeconds");
    }

    [Fact]
    public async Task Create_DurationAboveCeiling_IsRejectedTowardAlarmsCalendar()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/roast/timer.json",
            """{"durationSeconds": 14401, "target": {"room": "Kitchen"}}""",
            false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("4 hours");
        err.Error.Message.ShouldContain("alarms calendar");
    }

    [Fact]
    public async Task Create_DurationAtCeiling_IsAccepted()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/roast/timer.json",
            """{"durationSeconds": 14400, "target": {"room": "Kitchen"}}""",
            false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
    }

    // With no satellite named, a timer rings on its owner's channels — their chat client and their
    // Telegram — which is how a household with no satellites has timers at all.
    [Fact]
    public async Task Create_WithNoTarget_ArmsATimerThatRingsOnItsOwnersChannels()
    {
        var (fs, store, _, _) = Build();

        var result = await fs.CreateAsync(
            "/pasta/timer.json", """{"durationSeconds": 60, "text": "pasta is ready"}""", false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var armed = (await store.GetAsync(Home, "pasta", CancellationToken.None)).ShouldNotBeNull();
        armed.Target.ShouldBeNull();
        armed.Owner.ShouldBe(_fran);
        armed.AgentId.ShouldBe("jonas");
    }

    // The household's guest has no channels of its own to ring on, so its timer needs a satellite.
    [Fact]
    public async Task Create_WithNoTarget_ForTheHouseholdsGuest_IsRefused()
    {
        var mount = BuildMount();

        var result = await mount.As(GuestOf(Home)).CreateAsync(
            "/pasta/timer.json", """{"durationSeconds": 60}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        err.Error.Message.ShouldContain("target");
        (await mount.Store.ListAsync(Home, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ForTheHouseholdsGuest_RingsOnItsHouseholdsSatellite()
    {
        var mount = BuildMount();

        var result = await mount.As(GuestOf(Home)).CreateAsync(
            "/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var armed = (await mount.Store.GetAsync(Home, "pasta", CancellationToken.None)).ShouldNotBeNull();
        armed.Owner.ShouldBeNull();
        armed.Target.ShouldNotBeNull().Room.ShouldBe("Kitchen");
    }

    // A timer is its household's: another household's view does not list it, read it, find it or
    // cancel it, even with the very same id in its hand.
    [Fact]
    public async Task ATimer_IsListedReadAndCancelledOnlyByItsHousehold()
    {
        var mount = BuildMount();
        var fran = mount.As(PersonOf(_fran));
        var ana = mount.As(PersonOf(_ana));
        (await fran.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        (await ana.GlobAsync("/", "**", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldBe(["/dismiss", "/satellites.json"]);
        (await ana.ReadAsync("/pasta/timer.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        (await ana.SearchAsync("pasta", false, null, null, null, 10, 0, VfsTextSearchOutputMode.Content,
            CancellationToken.None)).ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value.TotalMatches.ShouldBe(0);
        (await ana.DeleteAsync("/pasta", CancellationToken.None)).ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
        (await mount.Store.GetAsync(Home, "pasta", CancellationToken.None)).ShouldNotBeNull();
    }

    // Ids are the model's to choose, so two households' "pasta" is the ordinary case, not a clash.
    [Fact]
    public async Task TwoHouseholds_EachArmATimerOfTheSameId()
    {
        var mount = BuildMount();
        var fran = mount.As(PersonOf(_fran));
        var ana = mount.As(PersonOf(_ana));

        (await fran.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        (await ana.CreateAsync("/pasta/timer.json", PastaSpec.Replace("300", "600"), false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        (await fran.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"remainingSeconds\": 300");
        (await ana.ReadAsync("/pasta/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldContain("\"remainingSeconds\": 600");
    }

    // A call the agent signed for nobody has no household, so there are no timers for it to see or
    // set: it is refused rather than shown somebody's.
    [Fact]
    public async Task ACallActingForNobody_IsRefused()
    {
        var mount = BuildMount();

        (await mount.Server.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        (await mount.Server.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        (await mount.Server.ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        (await mount.Store.ListAsync(Home, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Exec_Dismiss_SilencesOnlyTheCallersHousehold()
    {
        var mount = BuildMount();
        var homeRinging = (FakeDismisser)mount.Voices.Households[Home].Dismisser;
        var neighboursRinging = (FakeDismisser)mount.Voices.Households[Neighbours].Dismisser;
        homeRinging.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));
        neighboursRinging.Ringing.Add(new DismissedAlert("tea", AnnounceKind.Timer));
        mount.Alerts.Ringing[Neighbours] = [new DismissedAlert("bread", AnnounceKind.Timer)];

        var result = (await mount.As(PersonOf(_fran)).ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.Stdout.ShouldContain("timer \"pasta\"");
        result.Stdout.ShouldNotContain("tea");
        result.Stdout.ShouldNotContain("bread");
        neighboursRinging.Ringing.ShouldHaveSingleItem();
        mount.Alerts.Ringing.ShouldContainKey(Neighbours);
    }

    // What rings on a person's channels is stopped by the same ./dismiss as a satellite's ring: one
    // call silences everything the household has ringing, wherever it rings.
    [Fact]
    public async Task Exec_Dismiss_StopsWhatTheHouseholdHasRingingOnChannels()
    {
        var mount = BuildMount();
        mount.Alerts.Ringing[Home] = [new DismissedAlert("bread is ready", AnnounceKind.Timer)];

        var result = (await mount.As(PersonOf(_fran)).ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.Stdout.ShouldContain("timer \"bread is ready\"");
        mount.Alerts.Ringing.ShouldNotContainKey(Home);
    }

    // A household with no voice server connected may still have timers ringing on its channels, and
    // a dismiss that stopped them did its whole task.
    [Fact]
    public async Task Exec_Dismiss_WithTheVoiceServerOffline_StillStopsWhatRingsOnChannels()
    {
        var mount = BuildMount();
        mount.Voices.Households.Remove(Home);
        mount.Alerts.Ringing[Home] = [new DismissedAlert("bread is ready", AnnounceKind.Timer)];

        var result = (await mount.As(PersonOf(_fran)).ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("timer \"bread is ready\"");
    }

    // Where a timer can ring is the household's own: the satellites its voice server answers for,
    // read per call, since a prompt is fetched for nobody and shared by every household.
    [Fact]
    public async Task Read_Satellites_ListsTheCallersHouseholdsSatellites()
    {
        var mount = BuildMount();
        mount.Voices.Households[Neighbours] = (new FakeDismisser(), new EmptyCatalog());

        var home = (await mount.As(PersonOf(_fran)).ReadAsync("/satellites.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
        var neighbours = (await mount.As(PersonOf(_ana)).ReadAsync("/satellites.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

        home.ShouldContain("kitchen-01");
        home.ShouldContain("Kitchen");
        neighbours.ShouldNotContain("kitchen-01");
    }

    // A household whose voice server is not connected has nowhere to ring but its people's channels.
    [Fact]
    public async Task Read_Satellites_WithTheVoiceServerOffline_ListsNone()
    {
        var mount = BuildMount();
        mount.Voices.Households.Remove(Neighbours);

        var content = (await mount.As(PersonOf(_ana)).ReadAsync("/satellites.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

        content.ShouldNotContain("kitchen-01");
        content.ShouldContain("not connected");
    }

    [Fact]
    public async Task Create_UnresolvableRoom_IsRejectedWithTheRoster()
    {
        var (fs, store, _, _) = Build();

        var result = await fs.CreateAsync(
            "/pasta/timer.json",
            """{"durationSeconds": 300, "target": {"room": "Basement"}}""",
            false, true, CancellationToken.None);

        // A target the announcer cannot resolve arms a timer that never rings: the fire path
        // swallows the failure and the timer is already gone by then. Reject at create instead,
        // and name the satellites so the agent can retry with a real one.
        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        err.Error.Message.ShouldContain("kitchen-01");
        err.Error.Message.ShouldContain("Kitchen");
        (await store.ListAsync(Home)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_UnknownSatelliteInList_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/pasta/timer.json",
            """{"durationSeconds": 300, "target": {"satelliteIds": ["kitchen-01", "ghost-01"]}}""",
            false, true, CancellationToken.None);

        // Half-resolvable is still wrong: silently ringing only the kitchen hides the typo.
        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        err.Error.Message.ShouldContain("ghost-01");
    }

    [Fact]
    public async Task Create_BlankText_IsRejected()
    {
        var (fs, store, _, _) = Build();

        var result = await fs.CreateAsync(
            "/pasta/timer.json",
            """{"durationSeconds": 300, "text": "   ", "target": {"room": "Kitchen"}}""",
            false, true, CancellationToken.None);

        // A blank (non-null) text bypasses the `?? "<id> timer"` fallback and the announce endpoint
        // rejects it at fire time (400) -> the timer never rings. Reject at create so validation
        // matches the fire-time contract. (Omitting text is fine -- it auto-names.)
        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        err.Error.Message.ShouldContain("text");
        (await store.ListAsync(Home)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_HubUnreachable_ReturnsRetryableUnavailableAndDoesNotArm()
    {
        var mount = BuildMount();
        mount.Voices.Households[Home] = (new FakeDismisser(), new UnreachableCatalog());
        var (fs, store) = (mount.As(PersonOf(_fran)), mount.Store);

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        // Fail closed and say so: no unvalidated timer gets armed, and the agent learns this is
        // the hub being down (retryable), not a bad spec — instead of a raw exception envelope.
        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        err.Error.Retryable.ShouldBeTrue();
        err.Error.Message.ShouldContain("voice hub");
        err.Error.Message.ShouldContain("not armed");
        (await store.ListAsync(Home)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Exec_Dismiss_HubUnreachable_ReturnsRetryableUnavailable()
    {
        var mount = BuildMount();
        mount.Voices.Households[Home] = (new UnreachableDismisser(), new FakeSatelliteCatalog());
        var fs = mount.As(PersonOf(_fran));

        var result = await fs.ExecAsync("/", "./dismiss", null, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsExecResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        err.Error.Retryable.ShouldBeTrue();
        err.Error.Message.ShouldContain("voice hub");
    }

    [Fact]
    public async Task Create_DuplicateId_IsRejected()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, true, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Fact]
    public async Task Create_WrongPathShape_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync("/pasta.json", PastaSpec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    // A timer is immutable, so the mount does not implement edit at all: no override, nothing
    // advertised, and the base's unsupported envelope is the answer.
    [Fact]
    public async Task Edit_IsNotAnOperationThisMountHas()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = await fs.EditAsync("/pasta/timer.json",
            [new TextEdit("300", "600")], CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsEditResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        typeof(TimerFileSystem).GetMethod(nameof(TimerFileSystem.EditAsync))!
            .DeclaringType.ShouldNotBe(typeof(TimerFileSystem));
    }

    [Fact]
    public async Task Delete_TimerJsonFile_IsRejected_DirIsTheUnit()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        (await fs.DeleteAsync("/pasta/timer.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    // `./dismiss` is the spelling taught, because a real shell needs it once exec runs in the
    // sandbox; the bare name is what a model that reads the listing types, and it runs the same.
    [Theory]
    [InlineData("./dismiss")]
    [InlineData("dismiss")]
    public async Task Exec_DismissAtRoot_SilencesRingingAlertsAndReportsThem(string command)
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/", command, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("alarm \"Take out the trash\"");
        result.Stdout.ShouldContain("timer \"pasta\"");
    }

    [Fact]
    public async Task Exec_OnDismissScriptPath_AlsoWorks()
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/dismiss", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("timer \"pasta\"");
    }

    [Fact]
    public async Task Exec_DismissWithNothingRinging_SaysSo()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("nothing is ringing");
    }

    // The action lost its suffix: the old spelling names nothing, and the refusal lists what
    // does, so a model that learned `dismiss.sh` finds the new name in the same answer.
    [Theory]
    [InlineData("dismiss.sh")]
    [InlineData("./dismiss.sh")]
    public async Task Exec_TheOldScriptName_IsNotFoundAndNamesTheAction(string command)
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/", command, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("command not found: dismiss.sh");
        result.Stderr.ShouldContain("available: dismiss");
        dismisser.Ringing.ShouldNotBeEmpty();
    }

    // "nothing is ringing" is the same sentence whether the model has just silenced the alert
    // itself or never found it, and a model that cannot tell those apart goes looking for the
    // alarm somewhere else — glm-5.3-flash dismissed correctly, read the line, and then hunted
    // through /ha for the calendar event behind it. The line has to close the task.
    [Fact]
    public async Task Exec_DismissWithNothingRinging_SaysTheSilenceIsTheAnswer()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("nothing is ringing");
        result.Stdout.ShouldContain("nothing further to do");
    }

    [Fact]
    public async Task Exec_DismissThatSilencedSomething_SaysTheTaskIsDone()
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/", "./dismiss", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.Stdout.ShouldContain("timer \"pasta\"");
        result.Stdout.ShouldContain("nothing further to do");
    }

    // dismiss takes no arguments, and a model that reaches for a plausible one ("--all", to
    // be sure it covers every satellite) got a bare "command not found" naming the file it had
    // just typed. The refusal has to say the flag is the problem, not the script, or the next
    // guess is that the script is somewhere else.
    [Fact]
    public async Task Exec_DismissWithAnArgument_SaysTheScriptTakesNone()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "./dismiss --all", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("takes no arguments");
        result.Stderr.ShouldContain("dismiss");
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "reboot", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("available: dismiss");
    }

    // An action file runs and is never opened, on every route: a read that answered a comment
    // taught it was a script to look inside, and a write would have nothing to mean.
    [Fact]
    public async Task Read_TheDismissAction_IsRefusedAsExecutableOnly()
    {
        var (fs, _, _, _) = Build();

        var error = (await fs.ReadAsync("/dismiss", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error;

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain("executable-only");
        error.Hint.ShouldNotBeNull().ShouldContain("./dismiss");
    }

    [Fact]
    public async Task CreateOrDelete_TheDismissAction_IsRefusedAsExecutableOnly()
    {
        var (fs, store, _, _) = Build();

        (await fs.CreateAsync("/dismiss", PastaSpec, true, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.Message.ShouldContain("executable-only");
        (await fs.DeleteAsync("/dismiss", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.Message.ShouldContain("executable-only");
        (await store.ListAsync(Home, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Info_TheDismissAction_IsExecutable_AndNothingElseIs()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var action = (await fs.InfoAsync("/dismiss", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        var spec = (await fs.InfoAsync("/pasta/timer.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;

        action.Exists.ShouldBeTrue();
        action.IsDirectory.ShouldBe(false);
        action.Executable.ShouldBeTrue();
        spec.Executable.ShouldBeFalse();
    }

    [Fact]
    public async Task Glob_ThatMatchesNoAction_MarksNothing()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/", "*/*.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Executables.ShouldBeNull();
    }

    [Fact]
    public async Task Search_FindsTimerSpecContent()
    {
        var (fs, _, _, _) = Build();
        await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        var result = (await fs.SearchAsync(
                "pasta is ready", false, null, null, null, 10, 0,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;

        result.TotalMatches.ShouldBe(1);
        result.Results[0].File.ShouldBe("/pasta/timer.json");
    }

    // A caller-supplied pattern that backtracks catastrophically must end the search as a timeout
    // envelope, not stall the turn. The match timeout is injected tiny so it trips deterministically.
    [Fact]
    public async Task Search_PathologicalRegex_ReturnsTimeoutEnvelope()
    {
        var fs = BuildMount(regexMatchTimeout: TimeSpan.FromMilliseconds(1)).As(PersonOf(_fran));
        var spec = PastaSpec.Replace("pasta is ready", new string('a', 60), StringComparison.Ordinal);
        await fs.CreateAsync("/pasta/timer.json", spec, false, true, CancellationToken.None);

        var result = await fs.SearchAsync(
            "(a+)+b", regex: true, null, null, null, 10, 0,
            VfsTextSearchOutputMode.Content, CancellationToken.None);

        var error = result.ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.Timeout);
    }
}