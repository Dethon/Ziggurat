using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

public class FileInfoToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableFileInfoTool _tool;

    public FileInfoToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"file-info-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableFileInfoTool(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_ExistingFile_ReturnsMetadata()
    {
        var filePath = Path.Combine(_testDir, "note.md");
        File.WriteAllText(filePath, "Hello world");

        var result = _tool.TestRun(filePath);

        result["exists"]!.GetValue<bool>().ShouldBeTrue();
        result["isDirectory"]!.GetValue<bool>().ShouldBeFalse();
        result["size"]!.GetValue<long>().ShouldBe("Hello world".Length);
        result["lastModified"]!.ToString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Run_ExistingDirectory_ReturnsIsDirectoryTrue()
    {
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);

        var result = _tool.TestRun(subDir);

        result["exists"]!.GetValue<bool>().ShouldBeTrue();
        result["isDirectory"]!.GetValue<bool>().ShouldBeTrue();
        result["lastModified"]!.ToString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Run_MissingPath_ReturnsExistsFalse()
    {
        var missing = Path.Combine(_testDir, "nope.md");

        var result = _tool.TestRun(missing);

        result["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Run_SiblingDirectoryWithRootPrefix_ReturnsInvalidArgument()
    {
        var sibling = _testDir + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var leakTarget = Path.Combine(sibling, "secret.md");
            File.WriteAllText(leakTarget, "shh");

            _tool.TestRun(leakTarget).ShouldBeError(ToolError.Codes.InvalidArgument);
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, true);
            }
        }
    }

    [Fact]
    public void Run_RelativePath_ResolvesUnderRoot()
    {
        File.WriteAllText(Path.Combine(_testDir, "rel.md"), "x");

        var result = _tool.TestRun("rel.md");

        result["exists"]!.GetValue<bool>().ShouldBeTrue();
        result["isDirectory"]!.GetValue<bool>().ShouldBeFalse();
    }

    private class TestableFileInfoTool(string rootPath) : FileInfoTool(rootPath)
    {
        public JsonNode TestRun(string path) => Run(path).ToNode();
    }
}