using Domain.DTOs.FileSystem;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsResultTests
{
    [Fact]
    public void Map_OnASuccess_TransformsThePayload()
    {
        var result = new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = "docs/note.md" });

        var mapped = result.Map(info => info with { Path = "/vault/docs/note.md" });

        mapped.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.Path.ShouldBe("/vault/docs/note.md");
    }

    [Fact]
    public void Map_OnAnError_PassesItThroughAndNeverRunsTheTransform()
    {
        var ran = false;
        var error = new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound, Message = "Path not found: docs/note.md"
        };
        var result = new FsResult<FsInfoResult>.Err(error);

        var mapped = result.Map(info =>
        {
            ran = true;
            return info;
        });

        ran.ShouldBeFalse();
        mapped.TryGetValue(out _, out var passedThrough).ShouldBeFalse();
        passedThrough.ShouldBeSameAs(error);
    }
}