using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTextSearchToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsTextSearchTool _tool;

    public VfsTextSearchToolTests()
    {
        _tool = new VfsTextSearchTool(_registry.Object);
    }

    // directoryPath scopes a subtree: the backend gets it as `directoryPath` and no path.
    [Fact]
    public async Task RunAsync_DirectoryPath_SearchesThatSubtree()
    {
        _registry.Setup(r => r.Resolve("/vault/notes"))
            .Returns(Resolved(_backend.Object, "notes"));
        _backend.Setup(b => b.SearchAsync("api", true, null, "notes", "*.md", 10, 0,
                VfsTextSearchOutputMode.FilesOnly, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found("notes/api.md"));

        var result = await _tool.RunAsync("api", regex: true, directoryPath: "/vault/notes",
            filePattern: "*.md", maxResults: 10, contextLines: 0,
            outputMode: VfsTextSearchOutputMode.FilesOnly);

        result!["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        _backend.Verify(b => b.SearchAsync("api", true, null, "notes", "*.md", 10, 0,
            VfsTextSearchOutputMode.FilesOnly, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The branch a model hits when it guesses at the argument shape: neither scope given.
    [Fact]
    public async Task RunAsync_NeitherFilePathNorDirectoryPath_ReturnsInvalidArgument()
    {
        var result = await _tool.RunAsync("anything");

        result!["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["message"]!.GetValue<string>().ShouldContain("filePath or directoryPath");
        _registry.Verify(r => r.Resolve(It.IsAny<string>()), Times.Never);
    }

    // The single-file scope, and a backend whose paths carry a leading slash (the non-disk mounts).
    [Fact]
    public async Task RunAsync_SingleFileScope_ReportsTheCallersVirtualPath()
    {
        _registry.Setup(r => r.Resolve("/ha/light/kitchen/state.json"))
            .Returns(Resolved(_backend.Object, "light/kitchen/state.json", "/ha"));
        _backend.Setup(b => b.SearchAsync("on", false, "light/kitchen/state.json", null, null, 50, 1,
                VfsTextSearchOutputMode.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found("/light/kitchen/state.json"));

        var result = await _tool.RunAsync("on", filePath: "/ha/light/kitchen/state.json");

        result!["path"]!.GetValue<string>().ShouldBe("/ha/light/kitchen/state.json");
        result["results"]![0]!["file"]!.GetValue<string>().ShouldBe("/ha/light/kitchen/state.json");
    }

    // Same bargain as the glob: the budget is explained by the result that hit it, not by the
    // description every request pays for. The search has two budgets and the hint names which
    // ended the walk, because "0 files searched" against a large scan is a filePattern that
    // excluded everything, and only the hint can say so.
    [Fact]
    public async Task RunAsync_BudgetReached_SaysSoInTheResult_AndHowToNarrow()
    {
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(Resolved(_backend.Object, "", "/sandbox"));
        _backend.Setup(b => b.SearchAsync("needle", false, null, "", "*.xyz", 50, 1,
                VfsTextSearchOutputMode.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsSearchResult>.Ok(new FsSearchResult
            {
                Query = "needle",
                Regex = false,
                Path = "",
                FilesSearched = 0,
                FilesWithMatches = 0,
                TotalMatches = 0,
                Truncated = false,
                EntriesScanned = 50_000,
                BudgetReached = true,
                Results = []
            }));

        var result = await _tool.RunAsync("needle", directoryPath: "/sandbox", filePattern: "*.xyz");

        var hint = result!["hint"]!.GetValue<string>();
        hint.ShouldContain("50,000");
        hint.ShouldContain("filePattern");
        hint.ShouldContain("directoryPath");
    }

    [Fact]
    public async Task RunAsync_WalkEnded_CarriesNoHint()
    {
        _registry.Setup(r => r.Resolve("/vault/notes"))
            .Returns(Resolved(_backend.Object, "notes"));
        _backend.Setup(b => b.SearchAsync("api", false, null, "notes", null, 50, 1,
                VfsTextSearchOutputMode.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Found("notes/api.md"));

        var result = await _tool.RunAsync("api", directoryPath: "/vault/notes");

        result!["hint"].ShouldBeNull();
    }

    private static FsResult<FsSearchResult> Found(string file) =>
        new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = "q",
            Regex = false,
            Path = file,
            FilesSearched = 1,
            FilesWithMatches = 1,
            TotalMatches = 1,
            Truncated = false,
            Results = [new FsSearchFileResult { File = file, MatchCount = 1 }]
        });

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint = "") =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));
}