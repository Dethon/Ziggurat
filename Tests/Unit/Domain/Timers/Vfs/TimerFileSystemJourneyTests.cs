using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Exceptions;
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

    private static (TimerFileSystem Fs, InMemoryTimerStore Store, FakeTimeProvider Time, FakeDismisser Dismisser) Build()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var store = new InMemoryTimerStore();
        var dismisser = new FakeDismisser();
        return (new TimerFileSystem(store, time, dismisser, new FakeSatelliteCatalog()), store, time, dismisser);
    }

    private const string PastaSpec = """
        {"durationSeconds": 300, "text": "pasta is ready", "target": {"room": "Kitchen"}}
        """;

    [Fact]
    public async Task CreateReadStatusCancel_FullJourney()
    {
        var (fs, _, time, _) = Build();

        var created = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

        var glob = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldBe(["/dismiss.sh", "/pasta/", "/pasta/status.json", "/pasta/timer.json"]);

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

    [Fact]
    public async Task Create_MissingTarget_IsRejected()
    {
        var (fs, _, _, _) = Build();

        var result = await fs.CreateAsync(
            "/bad/timer.json", """{"durationSeconds": 60}""", false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.Message.ShouldContain("target");
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
        (await store.ListAsync()).ShouldBeEmpty();
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
        (await store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_HubUnreachable_ReturnsRetryableUnavailableAndDoesNotArm()
    {
        var store = new InMemoryTimerStore();
        var fs = new TimerFileSystem(store, new FakeTimeProvider(), new FakeDismisser(), new UnreachableCatalog());

        var result = await fs.CreateAsync("/pasta/timer.json", PastaSpec, false, true, CancellationToken.None);

        // Fail closed and say so: no unvalidated timer gets armed, and the agent learns this is
        // the hub being down (retryable), not a bad spec — instead of a raw exception envelope.
        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.Unavailable);
        err.Error.Retryable.ShouldBeTrue();
        err.Error.Message.ShouldContain("voice hub");
        err.Error.Message.ShouldContain("not armed");
        (await store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Exec_Dismiss_HubUnreachable_ReturnsRetryableUnavailable()
    {
        var fs = new TimerFileSystem(
            new InMemoryTimerStore(), new FakeTimeProvider(), new UnreachableDismisser(), new FakeSatelliteCatalog());

        var result = await fs.ExecAsync("/", "dismiss.sh", null, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsExecResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.Unavailable);
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

    [Fact]
    public async Task Exec_DismissAtRoot_SilencesRingingAlertsAndReportsThem()
    {
        var (fs, _, _, dismisser) = Build();
        dismisser.Ringing.Add(new DismissedAlert("Take out the trash", AnnounceKind.Alarm));
        dismisser.Ringing.Add(new DismissedAlert("pasta", AnnounceKind.Timer));

        var result = (await fs.ExecAsync("/", "dismiss.sh", null, CancellationToken.None))
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

        var result = (await fs.ExecAsync("/dismiss.sh", "dismiss.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("timer \"pasta\"");
    }

    [Fact]
    public async Task Exec_DismissWithNothingRinging_SaysSo()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "dismiss.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("nothing is ringing");
    }

    [Fact]
    public async Task Exec_UnknownCommand_Returns127()
    {
        var (fs, _, _, _) = Build();

        var result = (await fs.ExecAsync("/", "reboot.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;

        result.ExitCode.ShouldBe(127);
        result.Stderr.ShouldContain("dismiss.sh");
    }

    [Fact]
    public async Task Read_DismissScript_ExplainsItself()
    {
        var (fs, _, _, _) = Build();

        var read = (await fs.ReadAsync("/dismiss.sh", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        read.Content.ShouldContain("exec dismiss.sh");
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
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(_madrid);
        var fs = new TimerFileSystem(
            new InMemoryTimerStore(), time, new FakeDismisser(), new FakeSatelliteCatalog(),
            regexMatchTimeout: TimeSpan.FromMilliseconds(1));
        var spec = PastaSpec.Replace("pasta is ready", new string('a', 60), StringComparison.Ordinal);
        await fs.CreateAsync("/pasta/timer.json", spec, false, true, CancellationToken.None);

        var result = await fs.SearchAsync(
            "(a+)+b", regex: true, null, null, null, 10, 0,
            VfsTextSearchOutputMode.Content, CancellationToken.None);

        var error = result.ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.Timeout);
    }
}