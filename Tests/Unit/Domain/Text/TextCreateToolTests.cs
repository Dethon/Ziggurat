using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextCreateToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextCreateTool _tool;

    public TextCreateToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-create-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextCreateTool(_testDir, [".md", ".txt", ".json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_CreatesNewFile()
    {
        var result = _tool.TestRun("new-note.md", "# My Note\n\nContent here");

        result["status"]!.ToString().ShouldBe("created");
        result["filePath"]!.ToString().ShouldBe("new-note.md");

        var content = File.ReadAllText(Path.Combine(_testDir, "new-note.md"));
        content.ShouldBe("# My Note\n\nContent here");
    }

    [Fact]
    public void Run_CreatesParentDirectories()
    {
        var result = _tool.TestRun("deep/nested/path/note.md", "Content");

        result["status"]!.ToString().ShouldBe("created");
        File.Exists(Path.Combine(_testDir, "deep/nested/path/note.md")).ShouldBeTrue();
    }

    [Fact]
    public void Run_WithCreateDirectoriesFalse_FailsIfParentMissing()
    {
        _tool.TestRun("nonexistent/note.md", "Content", createDirectories: false)
            .ShouldBeError(ToolError.Codes.NotFound);
    }

    [Fact]
    public void Run_FileAlreadyExists_ReturnsAnErrorEnvelope()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

        var message = _tool.TestRun("existing.md", "New content")
            .ShouldBeError(ToolError.Codes.AlreadyExists)["message"]!.GetValue<string>();
        message.ShouldContain("already exists");
        message.ShouldContain("edit tool");
    }

    [Fact]
    public void Run_DisallowedExtension_ReturnsAnErrorEnvelope()
    {
        _tool.TestRun("script.ps1", "Get-Process")
            .ShouldBeError(ToolError.Codes.InvalidArgument)["message"]!.GetValue<string>()
            .ShouldContain("not allowed");
    }

    [Fact]
    public void Run_PathOutsideVault_ReturnsAnErrorEnvelope()
    {
        _tool.TestRun("../outside.md", "Content").ShouldBeError(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public void Run_ReturnsFileMetadata()
    {
        var content = "Line 1\nLine 2\nLine 3";
        var result = _tool.TestRun("meta.md", content);

        result["lines"]!.GetValue<int>().ShouldBe(3);
        result["size"]!.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Run_WithOverwriteTrue_OverwritesExistingFile()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

        var result = _tool.TestRun("existing.md", "New content", overwrite: true);

        result["status"]!.ToString().ShouldBe("created");
        File.ReadAllText(Path.Combine(_testDir, "existing.md")).ShouldBe("New content");
    }

    private class TestableTextCreateTool(string vaultPath, string[] allowedExtensions)
        : TextCreateTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(
            string filePath,
            string content,
            bool overwrite = false,
            bool createDirectories = true)
        {
            return Run(filePath, content, overwrite, createDirectories).ToNode();
        }
    }
}