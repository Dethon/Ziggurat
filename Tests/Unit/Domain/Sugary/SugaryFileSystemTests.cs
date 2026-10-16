using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Sugary.Vfs;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Over MCP the writing tools simply do not exist on this server. An agent's own filesystem tools
// still route a write at /sugary to the backend, and what it answers there is this refusal.
public class SugaryFileSystemTests
{
    private readonly SugaryFileSystem _mount = new(Mock.Of<ISugaryLog>(), TimeZoneInfo.Utc);

    public static TheoryData<string> Writes => new() { "create", "move", "delete", "create a dose", "delete a dose" };

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
        var mount = new SugaryFileSystem(log.Object, TimeZoneInfo.Utc);

        var all = (await mount.GlobAsync("/", "**", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        var doses = (await mount.GlobAsync("/doses", "*.csv", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        all.Entries.ShouldBe([
            "/README.md", "/doses/", "/doses/2026-08.csv", "/doses/2026-09.csv", "/foods.csv", "/items/",
            "/items/2026-10.csv", "/meals/", "/meals/2026-10.csv"
        ]);
        doses.Entries.ShouldBe(["/doses/2026-08.csv", "/doses/2026-09.csv"]);
    }

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Err>().Error;
}