using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextSearchToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextSearchTool _tool;

    public TextSearchToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-search-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextSearchTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_FindsMatchesAcrossMultipleFiles()
    {
        CreateTestFile("doc1.md", "# About Kubernetes\nKubernetes is great");
        CreateTestFile("doc2.md", "# Setup\nNo match here");
        CreateTestFile("doc3.md", "# Config\nConfigure kubernetes cluster");

        var result = _tool.TestRun("kubernetes");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_WithRegex_MatchesPattern()
    {
        CreateTestFile("todos.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");

        var result = _tool.TestRun("TODO:.*", regex: true);

        result["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_IncludesNearestHeading()
    {
        CreateTestFile("doc.md", "# Introduction\nSome text\n## Setup\nFind this target");

        var result = _tool.TestRun("target");

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        match["section"]!.ToString().ShouldBe("Setup");
    }

    [Fact]
    public void Run_WithContextLines_IncludesContext()
    {
        CreateTestFile("doc.md", "Line 1\nLine 2\nTarget line\nLine 4\nLine 5");

        var result = _tool.TestRun("Target", contextLines: 2);

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        var context = match["context"]!;
        context["before"]!.AsArray().Count.ShouldBe(2);
        context["after"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Run_RespectsMaxResults()
    {
        var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"match line {i}"));
        CreateTestFile("many.md", content);

        var result = _tool.TestRun("match", maxResults: 10);

        result["totalMatches"]!.GetValue<int>().ShouldBe(10);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Run_CaseInsensitiveByDefault()
    {
        CreateTestFile("doc.md", "KUBERNETES\nkubernetes\nKubernetes");

        var result = _tool.TestRun("kubernetes");

        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_SkipsDisallowedExtensions()
    {
        CreateTestFile("doc.md", "Find this");
        CreateTestFile("script.ps1", "Find this too");

        var result = _tool.TestRun("Find");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_WithFilePath_SearchesSingleFileCorrectly()
    {
        CreateTestFile("target.md", "Find this line\nAnd this one too");
        CreateTestFile("other.md", "Find this also");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("Find", filePath: filePath);

        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["totalMatches"]!.GetValue<int>().ShouldBe(1);

        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        CreateTestFile("subdir/target.md", "Find this line");

        var subFilePath = Path.Combine(_testDir, "subdir", "target.md");
        var result2 = _tool.TestRun("Find", filePath: subFilePath, filePattern: "*.txt", directoryPath: "/nonexistent");

        result2["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result2["totalMatches"]!.GetValue<int>().ShouldBe(1);

        var result3 = _tool.TestRun("nonexistent", filePath: filePath);

        result3["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
        result3["totalMatches"]!.GetValue<int>().ShouldBe(0);

        CreateTestFile("todos.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");
        var todosPath = Path.Combine(_testDir, "todos.md");
        var result4 = _tool.TestRun("TODO:.*", regex: true, filePath: todosPath);

        result4["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_OutputModes_ReturnCorrectFormat()
    {
        CreateTestFile("doc1.md", "Hello World\nHello again");
        CreateTestFile("doc2.md", "Hello there");

        var filesOnlyResult = _tool.TestRun("Hello", outputMode: VfsTextSearchOutputMode.FilesOnly);

        filesOnlyResult["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
        var firstResult = filesOnlyResult["results"]!.AsArray()[0]!;
        firstResult["matchCount"]!.GetValue<int>().ShouldBeGreaterThan(0);
        firstResult.AsObject().ContainsKey("matches").ShouldBeFalse();

        var contentResult = _tool.TestRun("Hello", outputMode: VfsTextSearchOutputMode.Content);

        var contentFirstResult = contentResult["results"]!.AsArray()[0]!;
        contentFirstResult["matches"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    // The jail vets the search root, but a symlink discovered inside the tree can point
    // anywhere — the recursive scan must not follow it, or foreign file content gets served
    // as search results (and a cycle would recurse forever).
    [Fact]
    public void Run_SymlinkedDirectoryInsideVault_IsNotSearched()
    {
        var outside = _testDir + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "leak.md"), "kubernetes secret");
            Directory.CreateSymbolicLink(Path.Combine(_testDir, "linked"), outside);
            CreateTestFile("real.md", "kubernetes real");

            var result = _tool.TestRun("kubernetes");

            result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
            result["totalMatches"]!.GetValue<int>().ShouldBe(1);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    // filePattern is the caller's, and .NET resolves a leading "../" inside a search pattern, so
    // handing it to EnumerateFiles reads files above the vault root.
    [Fact]
    public void Run_FilePatternEscapingTheVault_MatchesNothingOutsideIt()
    {
        var outside = _testDir + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.md"), "kubernetes secret");
            CreateTestFile("real.md", "kubernetes real");

            var result = _tool.TestRun("kubernetes", filePattern: "../*.md");

            result["results"]!.AsArray()
                .Select(r => r!["file"]!.ToString())
                .ShouldNotContain(f => f.Contains("secret"));
            result["totalMatches"]!.GetValue<int>().ShouldBe(0);
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void Run_FilePatternNamingAMissingDirectory_AnswersAnEmptyResult()
    {
        CreateTestFile("real.md", "kubernetes real");

        var result = _tool.TestRun("kubernetes", filePattern: "docs/*.md");

        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
        result["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Run_FilePatternWithADirectory_MatchesTheVaultRelativePath()
    {
        CreateTestFile("docs/guide.md", "kubernetes doc");
        CreateTestFile("root.md", "kubernetes root");

        var result = _tool.TestRun("kubernetes", filePattern: "docs/*.md");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["results"]!.AsArray()[0]!["file"]!.ToString().ShouldBe("docs/guide.md");
    }

    [Fact]
    public void Run_BareFilePattern_StillMatchesNestedFiles()
    {
        CreateTestFile("docs/guide.md", "kubernetes doc");
        CreateTestFile("docs/notes.txt", "kubernetes notes");

        var result = _tool.TestRun("kubernetes", filePattern: "*.md");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["results"]!.AsArray()[0]!["file"]!.ToString().ShouldBe("docs/guide.md");
    }

    private void CreateTestFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
    }

    private class TestableTextSearchTool(string vaultPath, string[] allowedExtensions)
        : TextSearchTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(
            string query,
            bool regex = false,
            string? filePath = null,
            string? filePattern = null,
            string directoryPath = "/",
            int maxResults = 50,
            int contextLines = 1,
            VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content)
        {
            return Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines, outputMode).ToNode();
        }
    }
}