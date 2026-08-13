using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
            .Returns(Walk("/library/book.pdf", "/library/sub/"));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var array = result["entries"]!.AsArray();
        array.Count.ShouldBe(2);
        array.ShouldContain(n => n!.GetValue<string>() == "sub/");
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithAbsoluteTrailingSlashPattern_PreservesDirsOnly()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "movies/", It.IsAny<CancellationToken>()))
            .Returns(Walk("/library/movies/"));

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
            .Returns(Walk(entries));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        var obj = result.AsObject();
        obj["truncated"]!.GetValue<bool>().ShouldBeTrue();
        // The total is what the walk found before it stopped, which is one past the cap — it stops
        // there rather than counting matches nobody will see.
        obj["total"]!.GetValue<int>().ShouldBe(201);
        obj["entries"]!.AsArray().Count.ShouldBe(200);
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    [Fact]
    public async Task Run_WithBasePath_UsesJoinedRoot()
    {
        var expectedRoot = Path.Combine(BasePath, "docs");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .Returns(Walk(Path.Combine(expectedRoot, "a.txt")));

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
            .Returns(Walk(Path.Combine(expectedRoot, "moby-dick.epub")));

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
            .Returns(Walk("/library/docs/v1..2.md"));

        var result = await _tool.TestRun("**/v1..2.md", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WithABasePathNameContainingConsecutiveDots_IsAccepted()
    {
        var expectedRoot = Path.Combine(BasePath, "v1..2");
        _mockClient.Setup(c => c.Glob(expectedRoot, "**/*", It.IsAny<CancellationToken>()))
            .Returns(Walk(Path.Combine(expectedRoot, "a.txt")));

        var result = await _tool.TestRun("**/*", "v1..2", CancellationToken.None);

        result["entries"]!.AsArray().Count.ShouldBe(1);
    }

    // The client refuses a pattern whose brace expansion overflows the cap; the tool turns that
    // refusal into the same invalid-pattern envelope every other bad pattern gets.
    [Fact]
    public async Task Run_PatternWhoseExpansionOverflowsTheCap_ReturnsInvalidArgument()
    {
        _mockClient.Setup(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Throws(new ArgumentException("Glob pattern expands to too many patterns."));

        await ShouldBeInvalid(() => _tool.TestRun(string.Concat(Enumerable.Repeat("{a,b}", 30)), CancellationToken.None));
    }

    // The cap is what ends the walk now, so a stream that never ends still answers. Without the
    // tool stopping at one match past the cap, this test hangs rather than fails.
    [Fact]
    public async Task Run_OverCap_StopsPullingOneMatchPastTheCap()
    {
        var pulled = 0;
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .Returns(new GlobWalk(_ => Endless(() => pulled++)));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        pulled.ShouldBe(GlobFilesTool.FileResultCap + 1);
        result["entries"]!.AsArray().Count.ShouldBe(GlobFilesTool.FileResultCap);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
    }

    // Truncation and the coverage flag answer different questions, so a walk that filled the
    // response without running out of tree says one and not the other.
    [Fact]
    public async Task Run_CappedButCompleteWalk_ReportsTruncationWithoutTheCoverageFlag()
    {
        var entries = Enumerable.Range(1, 250).Select(i => $"/library/file{i}.pdf").ToArray();
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .Returns(Walk(entries));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["budgetReached"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Run_WalkThatRanOutOfBudget_ReportsTheCoverageFlagAndWhatItScanned()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .Returns(new GlobWalk(walk => Bounded(walk, "/library/found.pdf")));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        result["budgetReached"]!.GetValue<bool>().ShouldBeTrue();
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        result["entriesScanned"]!.GetValue<int>().ShouldBe(50);
        result["total"]!.GetValue<int>().ShouldBe(1);
        FsResultContract.TryValidate("fs_glob", result, out var err).ShouldBeTrue(err);
    }

    // The walk is pulled, not awaited, so a missing base directory arrives mid-enumeration.
    [Fact]
    public async Task Run_BasePathRaisedAsMissingWhilePulling_ReturnsNotFound()
    {
        _mockClient.Setup(c => c.Glob(BasePath, "**/*", It.IsAny<CancellationToken>()))
            .Returns(new GlobWalk(_ => Throwing(new DirectoryNotFoundException(BasePath))));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
    }

    // The shared matcher gave the disk path a match timeout it could never hit before, and it is
    // raised while the caller is pulling. Same envelope as on every other mount.
    [Fact]
    public async Task Run_PatternThatTripsTheMatchTimeout_ReturnsTheTimeoutEnvelope()
    {
        _mockClient.Setup(c => c.Glob(BasePath, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new GlobWalk(_ => Throwing(new RegexMatchTimeoutException())));

        var result = await _tool.TestRun("**/*", CancellationToken.None);

        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.Timeout);
    }

    private static GlobWalk Walk(params string[] hits) => new(_ => Stream(hits));

    private static async IAsyncEnumerable<string> Stream(string[] hits)
    {
        await Task.Yield();
        foreach (var hit in hits)
        {
            yield return hit;
        }
    }

    private static async IAsyncEnumerable<string> Endless(Action onPull)
    {
        await Task.Yield();
        for (var i = 0; ; i++)
        {
            onPull();
            yield return $"/library/file{i}.pdf";
        }
    }

    private static async IAsyncEnumerable<string> Bounded(GlobWalk walk, string hit)
    {
        await Task.Yield();
        yield return hit;
        walk.EntriesScanned = 50;
        walk.BudgetReached = true;
    }

    private static async IAsyncEnumerable<string> Throwing(Exception error)
    {
        await Task.Yield();
        throw error;
#pragma warning disable CS0162 // The iterator needs a yield to be one; the throw above is the point.
        yield break;
#pragma warning restore CS0162
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