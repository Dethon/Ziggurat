using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Tools;
using Domain.Tools.Sugary.Vfs;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Over MCP the writing tools simply do not exist on this server. An agent's own filesystem tools
// still route a write at /sugary to the backend, and what it answers there is this refusal.
public class SugaryFileSystemTests
{
    private readonly SugaryFileSystem _mount = new(Mock.Of<ISugaryLog>(), TimeZoneInfo.Utc, Mock.Of<ITableEngine>());

    public static TheoryData<string> Writes => new() { "create", "move", "delete", "create a dose", "delete a dose", "create exercise" };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWrite_IsRefusedNamingWhatTheMountCanDo(string write)
    {
        var error = write switch
        {
            "create" => Error(await _mount.CreateAsync("/meals/2026-10.csv", "x", true, true, CancellationToken.None)),
            "move" => Error(await _mount.MoveAsync("/foods.csv", "/old.csv", CancellationToken.None)),
            "create a dose" => Error(await _mount.CreateAsync("/doses/2026-10.csv", "x", true, true, CancellationToken.None)),
            "delete a dose" => Error(await _mount.DeleteAsync("/doses/2026-10.csv", CancellationToken.None)),
            "create exercise" => Error(await _mount.CreateAsync("/exercise/2026-10.csv", "x", true, true, CancellationToken.None)),
            _ => Error(await _mount.DeleteAsync("/meals/2026-10.csv", CancellationToken.None))
        };

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Hint.ShouldNotBeNull();
        error.Hint.ShouldContain("read");
        error.Hint.ShouldContain("glob");
        error.Hint.ShouldContain("search");
        error.Hint.ShouldNotContain("create");
    }

    // A month has a doses file when a dose was taken in it, whether or not a meal was, and a meal
    // month without doses has none.
    [Fact]
    public async Task DoseMonths_AreListedBesideMealMonths()
    {
        var log = new Mock<ISugaryLog>();
        log.Setup(l => l.MealTimesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero)]);
        log.Setup(l => l.DoseTimesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DateTimeOffset(2026, 8, 30, 21, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 21, 0, 0, TimeSpan.Zero)]);
        log.Setup(l => l.ExerciseTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var mount = new SugaryFileSystem(log.Object, TimeZoneInfo.Utc, Mock.Of<ITableEngine>());

        var all = (await mount.GlobAsync("/", "**", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        var doses = (await mount.GlobAsync("/doses", "*.csv", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        all.Entries.ShouldBe([
            "/README.md", "/day-notes.csv", "/doses/", "/doses/2026-08.csv", "/doses/2026-09.csv", "/exercise/",
            "/foods.csv", "/items/", "/items/2026-10.csv", "/meals/", "/meals/2026-10.csv", "/periods.csv"
        ]);
        doses.Entries.ShouldBe(["/doses/2026-08.csv", "/doses/2026-09.csv"]);
    }

    // Her cycle is two tables at the root, there with headers before she has recorded anything.
    [Fact]
    public async Task HerCycleFiles_AreAtTheRoot_AndReadWithTheirHeaders()
    {
        var log = new Mock<ISugaryLog>();
        log.Setup(l => l.PeriodsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new LoggedPeriod(Guid.Empty, new DateOnly(2026, 10, 8), null)]);
        log.Setup(l => l.DayNotesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        log.Setup(l => l.MealTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        log.Setup(l => l.DoseTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        log.Setup(l => l.ExerciseTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var mount = new SugaryFileSystem(log.Object, TimeZoneInfo.Utc, Mock.Of<ITableEngine>());

        var glob = (await mount.GlobAsync("/", "*.csv", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        var periods = (await mount.ReadAsync("/periods.csv", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        var notes = (await mount.ReadAsync("/day-notes.csv", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;

        glob.Entries.ShouldBe(["/day-notes.csv", "/foods.csv", "/periods.csv"]);
        periods.Content.ShouldBe($"period_id,start_date,end_date,days,cycle_days\n{Guid.Empty},2026-10-08,,,\n");
        notes.Content.ShouldBe("date,note\n");
    }

    // A month has an exercise file when a session started in it, whatever else she logged then.
    [Fact]
    public async Task ExerciseMonths_AreListedBesideTheOthers()
    {
        var log = new Mock<ISugaryLog>();
        log.Setup(l => l.MealTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        log.Setup(l => l.DoseTimesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        log.Setup(l => l.ExerciseTimesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)]);
        var mount = new SugaryFileSystem(log.Object, TimeZoneInfo.Utc, Mock.Of<ITableEngine>());

        var exercise = (await mount.GlobAsync("/exercise", "*.csv", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        exercise.Entries.ShouldBe(["/exercise/2026-09.csv", "/exercise/2026-10.csv"]);
    }

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Err>().Error;
}