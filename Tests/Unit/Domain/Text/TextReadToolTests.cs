using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextReadToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextReadTool _tool;

    public TextReadToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-read-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextReadTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_ReturnsWholeFileWithLineNumbers()
    {
        var filePath = CreateTestFile("test.txt", "Line A\nLine B\nLine C");

        var result = _tool.TestRun(filePath);

        result["content"]!.ToString().ShouldBe("1: Line A\n2: Line B\n3: Line C");
    }

    [Fact]
    public void Run_WithPagination_ReturnsCorrectContent()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        var offsetResult = _tool.TestRun(filePath, offset: 3);
        offsetResult["content"]!.ToString().ShouldBe("3: Line 3\n4: Line 4\n5: Line 5");

        var limitResult = _tool.TestRun(filePath, limit: 2);
        limitResult["content"]!.ToString().ShouldBe("1: Line 1\n2: Line 2");

        var paginatedResult = _tool.TestRun(filePath, offset: 2, limit: 3);
        paginatedResult["content"]!.ToString().ShouldBe("2: Line 2\n3: Line 3\n4: Line 4");
    }

    [Fact]
    public void Run_LargeFile_TruncatesAt500Lines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 600).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("large.txt", content);

        var result = _tool.TestRun(filePath);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["suggestion"]!.ToString().ShouldContain("offset=501");
        result["totalLines"]!.GetValue<int>().ShouldBe(600);
        result["content"]!.ToString().Split('\n').Length.ShouldBe(500);
    }

    [Fact]
    public void Run_SmallFile_NotTruncated()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2");

        var result = _tool.TestRun(filePath);

        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        result.AsObject().ContainsKey("suggestion").ShouldBeFalse();
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextReadTool(string vaultPath, string[] allowedExtensions)
        : TextReadTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, int? offset = null, int? limit = null)
        {
            return Run(filePath, offset, limit).ToNode();
        }
    }
}