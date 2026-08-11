using System.Globalization;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

// Journey-style tests that exercise ScheduleFileSystem end-to-end as a FileSystemToolFeature
// consumer would. Each fact covers a real-world scenario rather than a single backend method.
public class ScheduleFileSystemJourneyTests
{
    private const string ValidSpec = """{"prompt":"summarize news","cron":"0 8 * * *"}""";

    private static readonly TimeZoneInfo _testZone =
        TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "test-plus2", "test-plus2");

    private static ScheduleFileSystem Build(
        FakeScheduleStore? store = null,
        params AgentCatalogEntry[] agents)
    {
        var catalog = new MutableAgentCatalog();
        var entries = agents.Length == 0
            ? new[] { new AgentCatalogEntry("jonas", "Jonas", "general") }
            : agents;
        catalog.Replace(entries);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(_testZone);
        return new ScheduleFileSystem(store ?? new FakeScheduleStore(), catalog, new CronValidator(), clock);
    }

    private static Schedule SeedSchedule(string id = "morning-news", string agentId = "jonas",
        string prompt = "summarize news", string cron = "0 8 * * *",
        DateTime? nextRunAt = null, DateTime? lastRunAt = null) =>
        new()
        {
            Id = id,
            AgentId = agentId,
            Prompt = prompt,
            CronExpression = cron,
            NextRunAt = nextRunAt,
            LastRunAt = lastRunAt,
            CreatedAt = DateTime.UtcNow
        };

    [Fact]
    public async Task Lifecycle_CreateGlobReadDelete_RoundTripsSchedule()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "library"));

        var create = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var saved = store.Items["morning-news"];
        saved.AgentId.ShouldBe("jonas");
        saved.CronExpression.ShouldBe("0 8 * * *");
        saved.NextRunAt.ShouldNotBeNull();

        // Glob root — every catalog agent shows up, even ones with no schedules. Directory
        // results are marked with a trailing slash, like every other filesystem.
        var rootGlob = (await fs.GlobAsync("/", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        rootGlob.Entries.ShouldContain("/jonas/");
        rootGlob.Entries.ShouldContain("/jack/");

        var agentGlob = (await fs.GlobAsync("/jonas", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        agentGlob.Entries.ShouldContain("/jonas/morning-news/");

        // Read schedule.json — spec is rendered without agentId (path already encodes it).
        var readSchedule = (await fs.ReadAsync("/jonas/morning-news/schedule.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        readSchedule.Content.ShouldContain("summarize news");
        readSchedule.Content.ShouldNotContain("agentId");

        var readInfo = (await fs.ReadAsync("/jonas/agent_info.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        readInfo.Content.ShouldContain("\"id\"");
        readInfo.Content.ShouldContain("Jonas");

        var info = (await fs.InfoAsync("/jonas", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(true);

        var delete = await fs.DeleteAsync("/jonas/morning-news", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        store.Items.ContainsKey("morning-news").ShouldBeFalse();
    }

    [Fact]
    public async Task Glob_StarAtRoot_ListsAgentDirsOnly_MarkedWithSlash_NoRecursion()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "library"));
        await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        var root = (await fs.GlobAsync("/", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        root.Entries.ShouldContain("/jonas/");
        root.Entries.ShouldContain("/jack/");
        root.Entries.ShouldNotContain(e => e.Contains("morning-news"));
    }

    [Fact]
    public async Task Glob_DoubleStar_RecursesIntoScheduleFiles()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store, new AgentCatalogEntry("jonas", "Jonas", "general"));
        await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/", "**/*.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("/jonas/agent_info.json");
        glob.Entries.ShouldContain("/jonas/morning-news/schedule.json");
        glob.Entries.ShouldContain("/jonas/morning-news/status.json");
        glob.Entries.ShouldNotContain("/jonas/morning-news/run_now.sh");
    }

    [Fact]
    public async Task Glob_TrailingSlash_ReturnsDirectoriesOnly()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store, new AgentCatalogEntry("jonas", "Jonas", "general"));
        await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/jonas", "*/", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldBe(["/jonas/morning-news/"]);
    }

    [Fact]
    public async Task Glob_BraceExpansion_MatchesEitherControlFile()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store, new AgentCatalogEntry("jonas", "Jonas", "general"));
        await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/jonas/morning-news", "{schedule.json,run_now.sh}", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldContain("/jonas/morning-news/schedule.json");
        glob.Entries.ShouldContain("/jonas/morning-news/run_now.sh");
        glob.Entries.ShouldNotContain("/jonas/morning-news/status.json");
    }

    [Fact]
    public async Task Glob_ScheduleDirectory_ListsItsControlFiles()
    {
        var store = new FakeScheduleStore();
        var fs = Build(store, new AgentCatalogEntry("jonas", "Jonas", "general"));
        await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);

        var glob = (await fs.GlobAsync("/jonas/morning-news", "*", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldBe([
            "/jonas/morning-news/run_now.sh",
            "/jonas/morning-news/schedule.json",
            "/jonas/morning-news/status.json"
        ]);
    }

    [Fact]
    public async Task UnknownAgent_ReadsAndCreatesAndInfo_AllFailGracefully()
    {
        var fs = Build();

        (await fs.CreateAsync("/ghost/x/schedule.json", ValidSpec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        (await fs.ReadAsync("/ghost/x/schedule.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>();

        var info = (await fs.InfoAsync("/ghost", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Edit_PromptAndCron_UpdatesSpecAndRecomputesNextRunAtAppropriately()
    {
        var nextRun = new DateTime(2999, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(prompt: "summarize news", nextRunAt: nextRun));
        var fs = Build(store);

        var promptEdit = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("summarize news", "summarize sports")], CancellationToken.None);
        promptEdit.ShouldBeOfType<FsResult<FsEditResult>.Ok>();
        store.Items["morning-news"].Prompt.ShouldBe("summarize sports");
        store.Items["morning-news"].NextRunAt.ShouldBe(nextRun);

        await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("0 8 * * *", "0 9 * * *")], CancellationToken.None);
        store.Items["morning-news"].CronExpression.ShouldBe("0 9 * * *");
        store.Items["morning-news"].NextRunAt.ShouldNotBe(nextRun);
    }

    [Fact]
    public async Task Edit_ReadOnlyOrInvalid_IsRejectedWithProperErrorCode()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        var fs = Build(store);

        var invalid = await fs.EditAsync("/jonas/morning-news/schedule.json",
            [new TextEdit("\"prompt\": \"summarize news\"", "\"prompt\": \"\"")], CancellationToken.None);
        invalid.ShouldBeOfType<FsResult<FsEditResult>.Err>();

        var status = await fs.EditAsync("/jonas/morning-news/status.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        status.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        var info = await fs.EditAsync("/jonas/agent_info.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        info.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        var missing = await fs.EditAsync("/jonas/ghost/schedule.json",
            [new TextEdit("a", "b")], CancellationToken.None);
        missing.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    [Fact]
    public async Task Move_ReassignsAgent_OrFailsOnConflicts_AndRespectsReadOnlyDeletes()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        await store.CreateAsync(SeedSchedule(id: "evening-news", prompt: "p", cron: "0 20 * * *"));
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", null),
            new AgentCatalogEntry("home", "Home", null));

        var move = await fs.MoveAsync("/jonas/morning-news", "/home/morning-news", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Ok>();
        store.Items["morning-news"].AgentId.ShouldBe("home");

        var conflict = await fs.MoveAsync("/home/morning-news", "/jonas/evening-news", CancellationToken.None);
        conflict.ShouldBeOfType<FsResult<FsMoveResult>.Err>();

        var deleteStatus = await fs.DeleteAsync("/jonas/evening-news/status.json", CancellationToken.None);
        deleteStatus.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);

        var deleteGhost = await fs.DeleteAsync("/jonas/ghost-schedule", CancellationToken.None);
        deleteGhost.ShouldBeOfType<FsResult<FsRemoveResult>.Err>();
    }

    [Fact]
    public async Task Create_RejectsDuplicateIdAndConflictingTriggers()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule());
        var fs = Build(store);

        var duplicate = await fs.CreateAsync("/jonas/morning-news/schedule.json", ValidSpec, false, true, CancellationToken.None);
        duplicate.ShouldBeOfType<FsResult<FsCreateResult>.Err>();

        var both = await fs.CreateAsync("/jonas/x/schedule.json",
            """{"prompt":"p","cron":"0 8 * * *","runAt":"2999-01-01T00:00:00Z"}""",
            false, true, CancellationToken.None);
        both.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
    }

    [Theory]
    [InlineData("2999-01-01T00:00:00", "2998-12-31T22:00:00")]    // bare → local +02:00 → UTC
    [InlineData("2999-01-01T14:30:00.5", "2999-01-01T12:30:00.5")] // bare with fractional seconds
    public async Task Create_BareRunAt_InterpretedInLocalZone(string runAtInput, string expectedUtc)
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);

        var result = await fs.CreateAsync(
            "/jonas/wake/schedule.json",
            $$"""{"prompt":"p","runAt":"{{runAtInput}}"}""",
            false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var saved = store.Items["wake"];
        saved.RunAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        saved.RunAt.ShouldBe(DateTime.SpecifyKind(
            DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("2999-07-15T14:30:00Z", "2999-07-15T14:30:00Z")]       // UTC, unchanged
    [InlineData("2999-07-15T14:30:00+05:00", "2999-07-15T09:30:00Z")]  // ahead of UTC
    [InlineData("2999-07-15T14:30:00-08:00", "2999-07-15T22:30:00Z")]  // behind UTC
    [InlineData("2999-01-15T14:30:00+02:00", "2999-01-15T12:30:00Z")]  // winter offset
    public async Task Create_ZonedRunAt_NormalizesToUtc(string runAtInput, string expectedUtc)
    {
        var store = new FakeScheduleStore();
        var fs = Build(store);
        var spec = $$"""{"prompt":"p","runAt":"{{runAtInput}}"}""";

        var result = await fs.CreateAsync("/jonas/once/schedule.json", spec, false, true, CancellationToken.None);

        result.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var expected = DateTime.Parse(expectedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var saved = store.Items["once"];
        saved.RunAt!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        saved.RunAt.ShouldBe(expected);
        saved.NextRunAt.ShouldBe(expected);
    }

    [Fact]
    public async Task Search_HonorsModesScopesRegexAndTruncation()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(prompt: "summarize the news"));
        await store.CreateAsync(SeedSchedule(id: "weather", agentId: "jack",
            prompt: "the weather report", cron: "0 7 * * *"));
        var fs = Build(store,
            new AgentCatalogEntry("jonas", "Jonas", "general"),
            new AgentCatalogEntry("jack", "Jack", "library"));

        var content = (await fs.SearchAsync("news", false, null, null, null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        var contentFile = content.Results.ShouldHaveSingleItem();
        contentFile.File.ShouldBe("/jonas/morning-news/schedule.json");
        var contentMatch = contentFile.Matches.ShouldNotBeNull().ShouldHaveSingleItem();
        contentMatch.Text.ShouldContain("summarize the news");
        contentMatch.Line.ShouldBeGreaterThan(0);
        contentFile.MatchCount.ShouldBeNull();

        var filesOnly = (await fs.SearchAsync("news", false, null, null, null, 50, 1,
                VfsTextSearchOutputMode.FilesOnly, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        var filesOnlyFile = filesOnly.Results.ShouldHaveSingleItem();
        filesOnlyFile.MatchCount.ShouldBe(1);
        filesOnlyFile.Matches.ShouldBeNull();

        var echoed = (await fs.SearchAsync("news", true, null, "/jonas", null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        echoed.Regex.ShouldBeTrue();
        echoed.Query.ShouldBe("news");
        echoed.Path.ShouldBe("/jonas");

        var scoped = (await fs.SearchAsync("the", false, null, "/jack", null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        scoped.FilesSearched.ShouldBe(1);
        scoped.Results.ShouldHaveSingleItem().File.ShouldBe("/jack/weather/schedule.json");

        var regex = (await fs.SearchAsync("news|weather", true, null, null, null, 50, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        regex.Results.Count.ShouldBe(2);

        var singletonStore = new FakeScheduleStore();
        await singletonStore.CreateAsync(SeedSchedule(id: "s", prompt: "p"));
        var singletonFs = Build(singletonStore);
        var truncated = (await singletonFs.SearchAsync("null", false, null, null, null, 1, 1,
                VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        truncated.TotalMatches.ShouldBe(1);
        truncated.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task Exec_RunNow_TriggersImmediateFire_AndUnknownCommandsOrSchedulesFail()
    {
        var future = DateTime.UtcNow.AddDays(1);
        var lastRun = DateTime.UtcNow.AddHours(-3);

        var neverRunStore = new FakeScheduleStore();
        await neverRunStore.CreateAsync(SeedSchedule(id: "n", prompt: "p", nextRunAt: future));
        var neverRunFs = Build(neverRunStore);

        var neverRun = await neverRunFs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);
        var neverRunResult = neverRun.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        neverRunResult.ExitCode.ShouldBe(0);
        (neverRunStore.Items["n"].NextRunAt <= DateTime.UtcNow).ShouldBeTrue();
        neverRunStore.Items["n"].LastRunAt.ShouldBeNull();

        // run_now.sh preserves an existing LastRunAt (it represents the last actual fire, not this trigger).
        var alreadyRunStore = new FakeScheduleStore();
        await alreadyRunStore.CreateAsync(SeedSchedule(id: "n", prompt: "p", nextRunAt: future, lastRunAt: lastRun));
        var alreadyRunFs = Build(alreadyRunStore);
        await alreadyRunFs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);
        alreadyRunStore.Items["n"].LastRunAt.ShouldBe(lastRun);

        var unknownCommandStore = new FakeScheduleStore();
        await unknownCommandStore.CreateAsync(SeedSchedule(id: "n", prompt: "p"));
        var unknownCommandFs = Build(unknownCommandStore);
        var unknownCommand = await unknownCommandFs.ExecAsync("/jonas/n", "ls -la", null, CancellationToken.None);
        var unknownExec = unknownCommand.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
        unknownExec.ExitCode.ShouldBe(127);
        unknownExec.Stderr.ShouldContain("run_now.sh");

        var ghostFs = Build();
        (await ghostFs.ExecAsync("/jonas/ghost", "run_now.sh", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>();
    }

    // A non-disk mount reaches the brace-expansion cap through the shared prologue; the caller
    // gets the invalid-argument envelope, not a raw ArgumentException.
    [Fact]
    public async Task Glob_TooManyBraceAlternatives_ReturnsInvalidArgumentEnvelope()
    {
        var fs = Build();
        var group = "{a,b,c,d,e,f,g,h,i}";

        var result = await fs.GlobAsync("", string.Concat(group, group, group), CancellationToken.None);

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    // Build() freezes the clock at 2026-01-01; run_now.sh must stamp that instant, not the wall clock.
    [Fact]
    public async Task Exec_RunNow_StampsNextRunAtFromTheInjectedClock()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(id: "n", prompt: "p", nextRunAt: DateTime.UtcNow.AddDays(1)));
        var fs = Build(store);

        await fs.ExecAsync("/jonas/n", "run_now.sh", null, CancellationToken.None);

        store.Items["n"].NextRunAt.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Create_BareRunAt_InDstGap_ReturnsInvalidArgumentError()
    {
        // Europe/Madrid spring-forward 2027: 02:00 local → 03:00 (gap), so 02:30 does not exist.
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var gapTime = new DateTime(2027, 3, 28, 2, 30, 0, DateTimeKind.Unspecified);

        // Precondition: verify our tz database actually has a gap at this instant.
        madrid.IsInvalidTime(gapTime).ShouldBeTrue("2027-03-28T02:30 must fall in the Europe/Madrid DST gap");

        var store = new FakeScheduleStore();
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(madrid);
        var fs = new ScheduleFileSystem(store, catalog, new CronValidator(), clock);

        var result = await fs.CreateAsync(
            "/jonas/wake/schedule.json",
            """{"prompt":"p","runAt":"2027-03-28T02:30:00"}""",
            false, true, CancellationToken.None);

        var err = result.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        err.Error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        err.Error.Message.ShouldContain("daylight-saving gap");
    }

    // A caller-supplied pattern that backtracks catastrophically must end the search as a timeout
    // envelope, not stall the turn. The match timeout is injected tiny so it trips deterministically.
    [Fact]
    public async Task Search_PathologicalRegex_ReturnsTimeoutEnvelope()
    {
        var store = new FakeScheduleStore();
        await store.CreateAsync(SeedSchedule(prompt: new string('a', 60)), CancellationToken.None);
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("jonas", "Jonas", "general")]);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(_testZone);
        var fs = new ScheduleFileSystem(store, catalog, new CronValidator(), clock,
            regexMatchTimeout: TimeSpan.FromMilliseconds(1));

        var result = await fs.SearchAsync(
            "(a+)+b", regex: true, null, null, null, 10, 0,
            VfsTextSearchOutputMode.Content, CancellationToken.None);

        var error = result.ShouldBeOfType<FsResult<FsSearchResult>.Err>().Error;
        error.ErrorCode.ShouldBe(ToolError.Codes.Timeout);
    }
}