using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary.Vfs;
using Infrastructure.Agents;
using McpServerSugary.Tables;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Another person's log, for a reader an owner let read everyone's (the members page's "Reads everyone's
// Sugary log"): under people/<name>/ with the files of their own, and a query asked there answers over
// theirs. Without the grant there is no people/ and asking into it is refused, never an empty log.
public class SugaryFileSystemPeopleTests
{
    private const string Household = "household-home";
    private const string Dethon = "user-dethon";
    private const string Tradaly = "user-tradaly";
    private const string Dani = "user-dani";

    private readonly PerOwnerSugaryLog _logs = new();

    public SugaryFileSystemPeopleTests()
    {
        _logs.Of(Dethon).Meals.Add(Meal("2026-08-01T12:00:00Z", 40));
        _logs.Of(Tradaly).Meals.AddRange([Meal("2026-07-12T12:30:00Z", 80), Meal("2026-08-09T12:30:00Z", 70)]);
        _logs.Of(Tradaly).Doses.Add(new LoggedDose(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-09T12:25:00Z"), 7,
            DoseKind.Rapid, [], null));
    }

    [Fact]
    public async Task AGrantedReader_QueriesAnotherPersonsLog_UnderTheirName()
    {
        var mount = Mount(reader: Dethon, People(granted: Dethon));

        var hers = await QueryAsync(mount, "/sugary/people/tradaly", "select count(*) from meals");
        var his = await QueryAsync(mount, "/sugary", "select count(*) from meals");
        var hersFromAFile = await QueryAsync(mount, "/sugary/people/tradaly/doses/2026-08.parquet", "select sum(units) from doses");

        hers["rows"]!.AsArray().Single()![0]!.GetValue<int>().ShouldBe(2);
        his["rows"]!.AsArray().Single()![0]!.GetValue<int>().ShouldBe(1);
        hersFromAFile["rows"]!.AsArray().Single()![0]!.GetValue<double>().ShouldBe(7);
        _logs.Asked.ShouldContain(new Account(Household, Tradaly));
    }

    [Fact]
    public async Task AGrantedReader_SeesEveryOtherPersonsFiles_AndNotTheirOwnAgain()
    {
        var mount = Mount(reader: Dethon, People(granted: Dethon));

        var people = Ok(await mount.GlobAsync("/people", "*/", CancellationToken.None));
        var hers = Ok(await mount.GlobAsync("/people/tradaly", "**", CancellationToken.None));
        var root = Ok(await mount.GlobAsync("/", "*/", CancellationToken.None));

        people.Entries.ShouldBe(["/people/dani/", "/people/tradaly/"]);
        hers.Entries.ShouldContain("/people/tradaly/meals/2026-07.parquet");
        hers.Entries.ShouldContain("/people/tradaly/doses/2026-08.parquet");
        hers.Entries.ShouldContain("/people/tradaly/README.md");
        root.Entries.ShouldContain("/people/");
        (await mount.InfoAsync("/people/tradaly/meals/2026-08.parquet", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeTrue();
    }

    [Fact]
    public async Task AReaderWithoutTheGrant_HasNoPeople_AndIsRefusedAskingIntoThem()
    {
        var mount = Mount(reader: Tradaly, People(granted: Dethon));

        var all = Ok(await mount.GlobAsync("/", "**", CancellationToken.None));

        all.Entries.ShouldNotContain(entry => entry.StartsWith("/people", StringComparison.Ordinal));
        Error(await mount.QueryAsync("/people/dethon", "select 1", 10, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await mount.InfoAsync("/people/dethon/meals/2026-08.parquet", CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await mount.ReadBlobAsync("/people/dethon/meals/2026-08.parquet", 0, 100, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        Error(await mount.GlobAsync("/people", "**", CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
        _logs.Asked.ShouldNotContain(new Account(Household, Dethon));
    }

    // A call that proved no household cannot be checked for the grant, so it reads its own log alone.
    [Fact]
    public async Task ACallProvingNoHousehold_ReadsNobodyElses()
    {
        var mount = new SugaryFileSystem(_logs, TimeZoneInfo.Utc, new DuckDbTableEngine()).For(Caller(Dethon, household: null));

        Error(await mount.QueryAsync("/people/tradaly", "select 1", 10, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
    }

    [Fact]
    public async Task ANameNobodyGoesBy_IsNotFound_NamingWhoIsThere()
    {
        var mount = Mount(reader: Dethon, People(granted: Dethon));

        var error = Error(await mount.QueryAsync("/people/nobody", "select 1", 10, CancellationToken.None));

        error.ErrorCode.ShouldBe(ToolError.Codes.NotFound);
        error.Message.ShouldContain("tradaly");
    }

    // A display name is anybody's words: a folder is its lowercase letters and digits, and two people
    // going by one name are told apart by the order they joined.
    [Fact]
    public async Task AFoldersName_IsTheDisplayNameMadeAPathSegment_AndTwinsAreNumbered()
    {
        var people = new HouseholdSugaryPeople([
            new SugaryPerson(Dethon, "Dethon", true),
            new SugaryPerson(Tradaly, "Ana María/López", false),
            new SugaryPerson(Dani, "ana maría lópez", false)
        ]);
        var mount = Mount(reader: Dethon, people);

        Ok(await mount.GlobAsync("/people", "*/", CancellationToken.None)).Entries
            .ShouldBe(["/people/ana-maría-lópez-2/", "/people/ana-maría-lópez/"]);
        (await QueryAsync(mount, "/sugary/people/ana-maría-lópez", "select count(*) from meals"))["rows"]!.AsArray()
            .Single()![0]!.GetValue<int>().ShouldBe(2);
    }

    // The people view says who the household's people are; the call's principal says who is reading. A
    // reader the view does not list (disabled since, or of another household) holds no grant.
    [Fact]
    public async Task AReaderTheViewDoesNotList_HoldsNoGrant()
    {
        var mount = Mount(reader: "user-stranger", People(granted: Dethon));

        Error(await mount.QueryAsync("/people/tradaly", "select 1", 10, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
    }

    private static HouseholdSugaryPeople People(string granted) => new([
        new SugaryPerson(Dethon, "Dethon", Dethon == granted),
        new SugaryPerson(Tradaly, "Tradaly", Tradaly == granted),
        new SugaryPerson(Dani, "Dani", Dani == granted)
    ]);

    private FileSystemBackendBase Mount(string reader, HouseholdSugaryPeople people)
    {
        var household = new Mock<ICallHousehold>();
        household.SetupGet(h => h.Id).Returns(Household);
        household.Setup(h => h.SettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HouseholdSettings { Name = "Casa", TimeZone = "UTC" });
        household.Setup(h => h.SugaryPeopleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(people);
        return new SugaryFileSystem(_logs, TimeZoneInfo.Utc, new DuckDbTableEngine()).For(Caller(reader, household.Object));
    }

    private static FileSystemCaller Caller(string reader, ICallHousehold? household) => new(
        new ConversationContext("jonas", "conversation-1", reader, new ReplyTarget("signalr", "conversation-1"),
            Principal: new Principal("signalr", reader, new Account(Household, reader))),
        null, Household: household);

    private static async Task<JsonNode> QueryAsync(FileSystemBackendBase mount, string path, string sql)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount(SugaryFileSystem.Name, "/sugary", "the logs"), mount);
        var answer = await new VfsQueryTool(registry).RunAsync(path, sql, VfsQueryTool.DefaultMaxRows);
        answer["ok"]?.GetValue<bool>().ShouldNotBe(false, answer.ToJsonString());
        return answer;
    }

    private static T Ok<T>(FsResult<T> result) where T : class => result.ShouldBeOfType<FsResult<T>.Ok>().Value;

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Err>().Error;

    private static LoggedMeal Meal(string eatenAt, double carbs) =>
        new(Guid.NewGuid(), DateTimeOffset.Parse(eatenAt), new CarbRange(carbs * 0.6, carbs, carbs * 1.6), null,
            MealOutcome.InRange, null,
            [new LoggedItem(0, "paella", null, null, null, new CarbRange(carbs * 0.6, carbs, carbs * 1.6),
                ItemSource.Estimate, ItemConfidence.Medium, false, null)]);

    // Each person's log kept apart, answering the read for whoever it names, as the store does.
    private sealed class PerOwnerSugaryLog : ISugaryLog
    {
        private readonly Dictionary<string, InMemorySugaryLog> _logs = new(StringComparer.Ordinal);
        private readonly List<Account> _asked = [];

        public IReadOnlyList<Account> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _asked];
                }
            }
        }

        public InMemorySugaryLog Of(string userId) =>
            _logs.TryGetValue(userId, out var log) ? log : _logs[userId] = new InMemorySugaryLog();

        private InMemorySugaryLog For(Account owner)
        {
            lock (_asked)
            {
                _asked.Add(owner);
            }

            return owner.TenantId == Household ? Of(owner.UserId) : new InMemorySugaryLog();
        }

        public Task<IReadOnlyList<DateTimeOffset>> MealTimesAsync(Account owner, CancellationToken ct) =>
            For(owner).MealTimesAsync(owner, ct);

        public Task<IReadOnlyList<LoggedMeal>> MealsAsync(Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            For(owner).MealsAsync(owner, from, to, ct);

        public Task<IReadOnlyList<DateTimeOffset>> DoseTimesAsync(Account owner, CancellationToken ct) =>
            For(owner).DoseTimesAsync(owner, ct);

        public Task<IReadOnlyList<LoggedDose>> DosesAsync(Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            For(owner).DosesAsync(owner, from, to, ct);

        public Task<IReadOnlyList<DateTimeOffset>> ExerciseTimesAsync(Account owner, CancellationToken ct) =>
            For(owner).ExerciseTimesAsync(owner, ct);

        public Task<IReadOnlyList<LoggedExercise>> ExerciseAsync(
            Account owner, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
            For(owner).ExerciseAsync(owner, from, to, ct);

        public Task<IReadOnlyList<LoggedPeriod>> PeriodsAsync(Account owner, CancellationToken ct) => For(owner).PeriodsAsync(owner, ct);

        public Task<IReadOnlyList<LoggedDayNote>> DayNotesAsync(Account owner, CancellationToken ct) =>
            For(owner).DayNotesAsync(owner, ct);

        public Task<IReadOnlyList<SavedFood>> FoodsAsync(Account owner, CancellationToken ct) => For(owner).FoodsAsync(owner, ct);
    }
}