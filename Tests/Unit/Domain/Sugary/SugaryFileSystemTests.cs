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

    public static TheoryData<string> Writes => new() { "create", "move", "delete" };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWrite_IsRefusedNamingWhatTheMountCanDo(string write)
    {
        var error = write switch
        {
            "create" => Error(await _mount.CreateAsync("/meals/2026-10.csv", "x", true, true, CancellationToken.None)),
            "move" => Error(await _mount.MoveAsync("/foods.csv", "/old.csv", CancellationToken.None)),
            _ => Error(await _mount.DeleteAsync("/meals/2026-10.csv", CancellationToken.None))
        };

        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Hint.ShouldNotBeNull();
        error.Hint.ShouldContain("read");
        error.Hint.ShouldContain("glob");
        error.Hint.ShouldContain("search");
        error.Hint.ShouldNotContain("create");
    }

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.ShouldBeOfType<FsResult<T>.Err>().Error;
}