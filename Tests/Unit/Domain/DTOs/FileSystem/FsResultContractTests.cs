using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsResultContractTests
{
    [Fact]
    public void ToNode_SerializesReadResult_WithCamelCaseAndOmittedNulls()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md",
            Content = "1: hi",
            TotalLines = 1,
            Truncated = false
        });

        var json = node.ToJsonString();
        json.ShouldContain("\"filePath\":\"/vault/a.md\"");
        json.ShouldContain("\"totalLines\":1");
        json.ShouldNotContain("suggestion");
    }

    [Fact]
    public void TryValidate_AcceptsConformingPayload()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md", Content = "x", TotalLines = 1, Truncated = false
        });

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_RejectsExtraMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\",\"totalLines\":1,\"truncated\":false,\"bogus\":true}");

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidate_RejectsMissingRequiredMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\"}");

        FsResultContract.TryValidate("fs_read", node, out _).ShouldBeFalse();
    }

    // Fails closed: returning success for a name nobody recognises let a typo skip the schema
    // check the method exists to perform.
    [Fact]
    public void TryValidate_UnknownTool_Fails()
    {
        var node = JsonNodeWith("{\"anything\":1}");

        FsResultContract.TryValidate("fs_not_a_tool", node, out var error).ShouldBeFalse();
        error.ShouldNotBeNull().ShouldContain("fs_not_a_tool");
    }

    [Theory]
    [InlineData("fs_read")]
    [InlineData("fs_info")]
    [InlineData("fs_glob")]
    [InlineData("fs_search")]
    [InlineData("fs_exec")]
    [InlineData("fs_create")]
    [InlineData("fs_edit")]
    [InlineData("fs_move")]
    [InlineData("fs_delete")]
    [InlineData("fs_copy")]
    [InlineData("fs_blob_read")]
    [InlineData("fs_blob_write")]
    public void ResultTypes_CoversEveryFsTool(string toolName)
    {
        FsResultContract.ResultTypes.ShouldContainKey(toolName);
    }

    [Fact]
    public void CreateResult_OmitsNote_WhenNull_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1
        });

        node.ToJsonString().ShouldNotContain("note");
        FsResultContract.TryValidate("fs_create", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void CreateResult_IncludesNote_WhenSet_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1, Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_create", node, out _).ShouldBeTrue();
    }

    [Fact]
    public void EditResult_WithNote_Validates()
    {
        var node = FsResultContract.ToNode(new FsEditResult
        {
            Status = "edited", FilePath = "/vault/a.md", TotalOccurrencesReplaced = 1,
            Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }],
            Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_edit", node, out _).ShouldBeTrue();
    }

    // An action file is marked on the two answers that describe a path without opening it. Every
    // other backend leaves the marks at their defaults, and its answer must stay byte-identical.
    [Fact]
    public void InfoResult_OmitsExecutable_WhenFalse()
    {
        var node = FsResultContract.ToNode(new FsInfoResult { Exists = true, Path = "/vault/a.md", IsDirectory = false });

        node.ToJsonString().ShouldBe("{\"exists\":true,\"path\":\"/vault/a.md\",\"isDirectory\":false}");
    }

    [Fact]
    public void InfoResult_CarriesExecutable_WhenTrue_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsInfoResult
        {
            Exists = true, Path = "/timers/dismiss", IsDirectory = false, Executable = true
        });

        node.ToJsonString().ShouldContain("\"executable\":true");
        FsResultContract.TryValidate("fs_info", node, out var error).ShouldBeTrue(error);
    }

    [Fact]
    public void GlobResult_OmitsExecutables_WhenUnset()
    {
        var node = FsResultContract.ToNode(new FsGlobResult { Entries = ["/a.md"], Truncated = false, Total = 1 });

        node.ToJsonString().ShouldNotContain("executables");
    }

    [Fact]
    public void GlobResult_CarriesExecutables_WhenSet_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsGlobResult
        {
            Entries = ["/dismiss", "/kitchen/"], Truncated = false, Total = 2, Executables = ["/dismiss"]
        });

        node.ToJsonString().ShouldContain("\"executables\":[\"/dismiss\"]");
        FsResultContract.TryValidate("fs_glob", node, out var error).ShouldBeTrue(error);
    }

    private static JsonNode JsonNodeWith(string json) => JsonNode.Parse(json)!;
}