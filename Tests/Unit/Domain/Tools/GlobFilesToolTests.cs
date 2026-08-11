using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

public class GlobFilesToolTests
{
    private const string BasePath = "/library";
    private readonly Mock<IFileSystemClient> _mockClient = new();
    private readonly TestableGlobFilesTool _tool;

    public GlobFilesToolTests()
    {
        _tool = new TestableGlobFilesTool(_mockClient.Object, new LibraryPathConfig(BasePath));
    }

    [Fact]
    public async Task Run_WithMatchingEntries_ReturnsEntryList()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/book.pdf", "/library/sub/"]);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var array = result["entries"]!.AsArray();
        array.Count.ShouldBe(2);
        array.ShouldContain(n => n!.GetValue<string>() == "sub/");
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithAbsolutePathUnderBasePath_StripsBaseAndUsesRelative()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "docs/**/*.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/docs/book.pdf"]);

        var result = await _tool.TestRun("/library/docs/**/*.pdf", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithAbsoluteTrailingSlashPattern_PreservesDirsOnly()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "movies/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/movies/"]);

        var result = await _tool.TestRun("/library/movies/", CancellationToken.None);

        result["entries"]!.AsArray()[0]!.GetValue<string>().ShouldBe("movies/");
        _mockClient.Verify(c => c.Glob(BasePath, "movies/", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Run_WithEmptyPattern_ReturnsInvalidArgument(string? pattern)
    {
        await ShouldBeInvalid(() => _tool.TestRun(pattern!, CancellationToken.None));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("foo/../../bar")]
    [InlineData("..")]
    public async Task Run_WithDotDotPattern_ReturnsInvalidArgument(string pattern)
    {
        await ShouldBeInvalid(() => _tool.TestRun(pattern, CancellationToken.None));
    }

    // /library-backup is a different directory from /library, and a prefix match without a
    // separator used to admit it and then hand the client a '../' pattern.
    [Fact]
    public async Task Run_WithAbsolutePathInASiblingOfBasePath_ReturnsInvalidArgument()
    {
        await ShouldBeInvalid(() => _tool.TestRun("/library-backup/**/*.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task Run_OverCap_ReturnsTruncatedObject()
    {
        var entries = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var obj = result.AsObject();
        obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
        obj["total"]!.GetValue<int>().ShouldBe(250);
        obj["entries"]!.AsArray().Count.ShouldBe(200);
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithBasePath_UsesJoinedRoot()
    {
        var expectedRoot = Path.Combine(BasePath, "docs");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Path.Combine(expectedRoot, "a.txt")]);

        var result = await _tool.TestRun("**/*", "docs", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
        _mockClient.Verify(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("foo/../bar")]
    public async Task Run_WithBasePathContainingDotDot_ReturnsInvalidArgument(string basePath)
    {
        await ShouldBeInvalid(() => _tool.TestRun("**/*", basePath, CancellationToken.None));
    }

    // An absolute pattern must be relativized against the root the matcher actually runs from:
    // relativizing against the mount root while matching under root+basePath searched books/books.
    [Fact]
    public async Task Run_AbsolutePatternWithBasePath_RelativizesAgainstTheBasePathRoot()
    {
        var expectedRoot = Path.Combine(BasePath, "books");
        _mockClient.Setup(c => c.Glob(expectedRoot, "*.epub", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Path.Combine(expectedRoot, "moby-dick.epub")]);

        var result = await _tool.TestRun("/library/books/*.epub", "books", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
        _mockClient.Verify(c => c.Glob(expectedRoot, "*.epub", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_AbsolutePatternOutsideTheBasePath_ReturnsInvalidArgument()
    {
        await ShouldBeInvalid(() => _tool.TestRun("/library/movies/*.mkv", "books", CancellationToken.None));
    }

    // ".." inside a name is not a traversal segment; only a whole ".." path segment escapes.
    [Fact]
    public async Task Run_WithANameContainingConsecutiveDots_MatchesInsteadOfRefusing()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/v1..2.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["/library/docs/v1..2.md"]);

        var result = await _tool.TestRun("**/v1..2.md", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithABasePathNameContainingConsecutiveDots_IsAccepted()
    {
        var expectedRoot = Path.Combine(BasePath, "v1..2");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Path.Combine(expectedRoot, "a.txt")]);

        var result = await _tool.TestRun("**/*", "v1..2", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    // The client refuses a pattern whose brace expansion overflows the cap; the tool turns that
    // refusal into the same invalid-pattern envelope every other bad pattern gets.
    [Fact]
    public async Task Run_PatternWhoseExpansionOverflowsTheCap_ReturnsInvalidArgument()
    {
        _mockClient.Setup(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Glob pattern expands to too many patterns."));

        await ShouldBeInvalid(() => _tool.TestRun(string.Concat(Enumerable.Repeat("{a,b}", 30)), CancellationToken.None));
    }

    private static async Task ShouldBeInvalid(Func<Task<JsonNode>> call)
    {
        var result = await call();

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
    }

    private class TestableGlobFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath)
        : GlobFilesTool(client, libraryPath)
    {
        public async Task<JsonNode> TestRun(string pattern, CancellationToken cancellationToken)
            => (await Run(pattern, cancellationToken)).ToNode();

        public async Task<JsonNode> TestRun(string pattern, string basePath, CancellationToken cancellationToken)
            => (await Run(pattern, cancellationToken, basePath)).ToNode();
    }
}