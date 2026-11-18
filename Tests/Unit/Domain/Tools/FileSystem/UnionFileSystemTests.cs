using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// One mount point, several filesystems: each top-level entry belongs to the member holding it, and
// a new one to the member its content's values are claimed by. The members here are two in-memory
// stores standing for the home's watches (the fallback) and Sugary's (claiming glucose kinds); the
// union knows nothing of either.
public class UnionFileSystemTests
{
    private const string RouteBy = "/triggers/*/trigger";

    private readonly EntryMount _home = new("ha-watches", new Dictionary<string, string>
    {
        ["porch/watch.json"] = """{"triggers":[{"trigger":"state"}]}""",
        ["porch/status.json"] = """{"armed":true}"""
    });

    private readonly EntryMount _sugary = new("sugary-watches", new Dictionary<string, string>
    {
        ["low/watch.json"] = """{"triggers":[{"trigger":"glucose"}]}"""
    });

    private UnionFileSystem Union() => new(
    [
        new UnionFileSystem.Member("ha-watches", new FileSystemUnion(RouteBy, [], Fallback: true), _home),
        new UnionFileSystem.Member("sugary-watches",
            new FileSystemUnion(RouteBy, ["glucose", "glucose_trend", "glucose_forecast", "sensor"]), _sugary)
    ]);

    private static string Trigger(params string[] kinds) =>
        $$"""{"triggers":[{{string.Join(",", kinds.Select(k => $$"""{"trigger":"{{k}}"}"""))}}]}""";

    [Fact]
    public async Task TheListing_MergesEveryMembersEntries()
    {
        var glob = Ok(await Union().GlobAsync("", "*", CancellationToken.None));

        glob.Entries.ShouldBe(["low/", "porch/"]);
        glob.Total.ShouldBe(2);
        glob.Unavailable.ShouldBeNull();
    }

    [Fact]
    public async Task ADeepListing_MergesTooAndStaysSorted()
    {
        var glob = Ok(await Union().GlobAsync("", "**/*", CancellationToken.None));

        glob.Entries.ShouldBe(["low/watch.json", "porch/status.json", "porch/watch.json"]);
    }

    [Fact]
    public async Task ReadEditAndRemove_ReachTheMemberHoldingTheEntry()
    {
        var union = Union();

        Ok(await union.ReadAsync("low/watch.json", null, null, CancellationToken.None)).Content.ShouldContain("glucose");
        Ok(await union.ReadAsync("porch/status.json", null, null, CancellationToken.None)).Content.ShouldContain("armed");

        Ok(await union.EditAsync("low/watch.json", [new TextEdit("glucose", "glucose_trend")], CancellationToken.None));
        _sugary.Files["low/watch.json"].ShouldContain("glucose_trend");

        Ok(await union.DeleteAsync("porch", CancellationToken.None));
        _home.Files.Keys.ShouldNotContain("porch/watch.json");
        _sugary.Files.Keys.ShouldContain("low/watch.json");
    }

    [Fact]
    public async Task ACreateOfANewEntry_GoesToTheMemberClaimingItsValues()
    {
        Ok(await Union().CreateAsync("high/watch.json", Trigger("glucose", "glucose_trend"), false, true, CancellationToken.None));

        _sugary.Files.Keys.ShouldContain("high/watch.json");
        _home.Files.Keys.ShouldNotContain("high/watch.json");
    }

    [Fact]
    public async Task ACreateClaimingNothing_GoesToTheFallback()
    {
        Ok(await Union().CreateAsync("door/watch.json", Trigger("state"), false, true, CancellationToken.None));

        _home.Files.Keys.ShouldContain("door/watch.json");
        _sugary.Files.Keys.ShouldNotContain("door/watch.json");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"triggers":[]}""")]
    public async Task ContentWithNothingAtThePointer_GoesToTheFallback(string content)
    {
        Ok(await Union().CreateAsync("misc/notes.md", content, false, true, CancellationToken.None));

        _home.Files.Keys.ShouldContain("misc/notes.md");
    }

    // A value that is not a string is nobody's claim, so it is the fallback's like any unclaimed one.
    [Fact]
    public async Task ANonStringValue_IsUnclaimed()
    {
        Ok(await Union().CreateAsync("odd/watch.json", """{"triggers":[{"trigger":7}]}""", false, true, CancellationToken.None));

        _home.Files.Keys.ShouldContain("odd/watch.json");
    }

    [Fact]
    public async Task ACreateMixingClaimedAndUnclaimedValues_IsRefusedNamingWhereEachBelongs()
    {
        var refused = Error(await Union().CreateAsync(
            "mixed/watch.json", Trigger("glucose", "state"), false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        refused.Message.ShouldContain("'glucose'");
        refused.Message.ShouldContain("sugary-watches");
        refused.Message.ShouldContain("'state'");
        refused.Message.ShouldContain("ha-watches");
        _home.Files.Keys.ShouldNotContain("mixed/watch.json");
        _sugary.Files.Keys.ShouldNotContain("mixed/watch.json");
    }

    [Fact]
    public async Task ACreateMixingTwoMembersClaims_IsRefused()
    {
        var third = new EntryMount("weather", []);
        var union = new UnionFileSystem(
        [
            new UnionFileSystem.Member("sugary-watches", new FileSystemUnion(RouteBy, ["glucose"]), _sugary),
            new UnionFileSystem.Member("weather", new FileSystemUnion(RouteBy, ["rain"]), third)
        ]);

        var refused = Error(await union.CreateAsync("x/watch.json", Trigger("glucose", "rain"), false, true, CancellationToken.None));

        refused.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        refused.Message.ShouldContain("'rain'");
        refused.Message.ShouldContain("weather");
    }

    // With no fallback, a value nobody claims has nowhere to go.
    [Fact]
    public async Task AnUnclaimedCreateWithNoFallback_IsRefused()
    {
        var union = new UnionFileSystem(
            [new UnionFileSystem.Member("sugary-watches", new FileSystemUnion(RouteBy, ["glucose"]), _sugary)]);

        Error(await union.CreateAsync("x/watch.json", Trigger("state"), false, true, CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task ACreateOfAnEntryAnotherMemberHolds_GoesToTheHolder()
    {
        Ok(await Union().CreateAsync("porch/watch.json", Trigger("glucose"), true, true, CancellationToken.None));

        _home.Files["porch/watch.json"].ShouldContain("glucose");
        _sugary.Files.Keys.ShouldNotContain("porch/watch.json");
    }

    [Fact]
    public async Task AMemberRefusingTheCaller_AddsNothingAndFailsNothing()
    {
        _sugary.Refuses = ToolError.Codes.PermissionDenied;

        var glob = Ok(await Union().GlobAsync("", "*", CancellationToken.None));

        glob.Entries.ShouldBe(["porch/"]);
        glob.Unavailable.ShouldBeNull();
    }

    [Fact]
    public async Task AMemberFailingForNow_IsNamedAsUnavailable()
    {
        _sugary.Refuses = ToolError.Codes.TransientDependency;

        var glob = Ok(await Union().GlobAsync("", "*", CancellationToken.None));

        glob.Entries.ShouldBe(["porch/"]);
        glob.Unavailable.ShouldBe(["sugary-watches"]);
    }

    [Fact]
    public async Task AMemberThatThrows_IsNamedAsUnavailable()
    {
        _sugary.Throws = true;

        var glob = Ok(await Union().GlobAsync("", "*", CancellationToken.None));

        glob.Entries.ShouldBe(["porch/"]);
        glob.Unavailable.ShouldBe(["sugary-watches"]);
    }

    // Nobody answering is still a listing that says who did not answer, never an empty one alone.
    [Fact]
    public async Task EveryMemberFailing_ListsNothingAndNamesThemAll()
    {
        _sugary.Throws = true;
        _home.Refuses = ToolError.Codes.TransientDependency;

        var glob = Ok(await Union().GlobAsync("", "*", CancellationToken.None));

        glob.Entries.ShouldBeEmpty();
        glob.Unavailable.ShouldBe(["ha-watches", "sugary-watches"]);
    }

    [Fact]
    public async Task EveryMemberRefusingTheCaller_IsTheRefusal()
    {
        _sugary.Refuses = ToolError.Codes.PermissionDenied;
        _home.Refuses = ToolError.Codes.PermissionDenied;

        Error(await Union().GlobAsync("", "*", CancellationToken.None)).ErrorCode.ShouldBe(ToolError.Codes.PermissionDenied);
    }

    // An entry nobody who answered holds may be the silent member's: "not found" would be the lie the
    // unavailable list exists to prevent.
    [Fact]
    public async Task AReadOfAnEntryNobodyAnsweringHolds_WhileAMemberIsAway_IsTransient()
    {
        _sugary.Throws = true;

        var error = Error(await Union().ReadAsync("low/watch.json", null, null, CancellationToken.None));

        error.ErrorCode.ShouldBe(ToolError.Codes.TransientDependency);
        error.Message.ShouldContain("sugary-watches");
    }

    [Fact]
    public async Task ARootStat_IsADirectory()
    {
        var info = Ok(await Union().InfoAsync("", CancellationToken.None));

        info.Exists.ShouldBeTrue();
        info.IsDirectory.ShouldBe(true);
    }

    [Fact]
    public async Task AMoveBetweenEntriesOfTwoMembers_IsRefused()
    {
        Error(await Union().MoveAsync("low/watch.json", "porch/watch.json", CancellationToken.None))
            .ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        _sugary.Files.Keys.ShouldContain("low/watch.json");
    }

    [Fact]
    public async Task AMoveWithinOneMembersEntry_IsThatMembers()
    {
        Ok(await Union().MoveAsync("low/watch.json", "lower/watch.json", CancellationToken.None));

        _sugary.Files.Keys.ShouldContain("lower/watch.json");
    }

    [Fact]
    public async Task SearchResults_MergeEveryMembers()
    {
        var search = Ok(await Union().SearchAsync("trigger", false, null, null, null, 50, 0,
            VfsTextSearchOutputMode.FilesOnly, CancellationToken.None));

        search.Results.Select(r => r.File).ShouldBe(["low/watch.json", "porch/watch.json"]);
        search.TotalMatches.ShouldBe(2);
    }

    // Each member is told who is calling, as each would be on a mount of its own: a watch records
    // the conversation that wrote it.
    [Fact]
    public async Task TheCaller_ReachesEveryMember()
    {
        var caller = new ConversationContext("jonas", "conv-1", "fran", new ReplyTarget("telegram", "conv-1"));
        var homeSeen = new List<string?>();
        var sugarySeen = new List<string?>();
        var union = new UnionFileSystem(
        [
            new UnionFileSystem.Member("ha-watches", new FileSystemUnion(RouteBy, [], Fallback: true), new Bound(_home, homeSeen)),
            new UnionFileSystem.Member("sugary-watches", new FileSystemUnion(RouteBy, ["glucose"]), new Bound(_sugary, sugarySeen))
        ]);

        var bound = ((ICallerBoundBackend)union).As(caller);
        await bound.CreateAsync("a/watch.json", Trigger("glucose"), false, true, CancellationToken.None);
        await bound.CreateAsync("b/watch.json", Trigger("state"), false, true, CancellationToken.None);

        sugarySeen.ShouldContain("conv-1");
        homeSeen.ShouldContain("conv-1");
        sugarySeen.ShouldAllBe(c => c == "conv-1");
        homeSeen.ShouldAllBe(c => c == "conv-1");
    }

    private static T Ok<T>(FsResult<T> result) where T : class =>
        result.TryGetValue(out var value, out var error) ? value : throw new ShouldAssertException(error.Message);

    private static ToolErrorResult Error<T>(FsResult<T> result) where T : class =>
        result.TryGetValue(out _, out var error) ? throw new ShouldAssertException("expected an error") : error;

    // A member that tells its server who is calling, as the agent's proxy does: every operation it
    // is asked notes the conversation of the view it was asked through.
    private sealed class Bound(EntryMount inner, List<string?> seen, ConversationContext? caller = null)
        : ICallerBoundBackend, IFileSystemBackend
    {
        public IFileSystemBackend As(ConversationContext? caller) => new Bound(inner, seen, caller);

        private IFileSystemBackend Note()
        {
            lock (seen)
            {
                seen.Add(caller?.ConversationId);
            }

            return inner;
        }

        public string FilesystemName => inner.FilesystemName;
        public Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) => Note().ReadAsync(path, offset, limit, ct);
        public Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) => Note().InfoAsync(path, ct);
        public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) => Note().CreateAsync(path, content, overwrite, createDirectories, ct);
        public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) => Note().EditAsync(path, edits, ct);
        public Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) => Note().GlobAsync(basePath, pattern, ct);
        public Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct) => Note().SearchAsync(query, regex, path, directoryPath, filePattern, maxResults, contextLines, outputMode, ct);
        public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) => Note().MoveAsync(sourcePath, destinationPath, ct);
        public Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) => Note().DeleteAsync(path, ct);
        public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) => Note().ExecAsync(path, command, timeoutSeconds, ct);
        public Task<FsResult<FsQueryResult>> QueryAsync(string path, string sql, int maxRows, CancellationToken ct) => Note().QueryAsync(path, sql, maxRows, ct);
        public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath, bool overwrite, bool createDirectories, CancellationToken ct) => Note().CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct);
        public Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) => Note().MoveOutCheckAsync(path, ct);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct) => Note().ReadChunksAsync(path, ct);
        public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, bool overwrite, bool createDirectories, CancellationToken ct) => Note().WriteChunksAsync(path, chunks, overwrite, createDirectories, ct);
    }
}

// A store of entries keyed by mount-relative path: `*` at the root lists its entries as
// directories, any other pattern every file under the base path. It can be made to refuse or to
// throw, as a member over the wire can.
internal sealed class EntryMount(string name, Dictionary<string, string> files) : FileSystemBackendBase
{
    public Dictionary<string, string> Files { get; } = files;

    public string? Refuses { get; set; }

    public bool Throws { get; set; }

    public override string FilesystemName => name;

    public override string DescribeMount => $"The {name} entries.";

    private FsResult<T>? Failing<T>() where T : class =>
        Throws
            ? throw new HttpRequestException("connection refused")
            : Refuses is { } code ? FsError.Fail<T>(code, $"{name} refused") : null;

    public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (Failing<FsGlobResult>() is { } failed)
        {
            return Task.FromResult(failed);
        }

        var scope = basePath.Trim('/');
        var under = Files.Keys.Where(k => scope.Length == 0 || k.StartsWith(scope + "/", StringComparison.Ordinal)).ToList();
        IReadOnlyList<string> entries = pattern == "*" && scope.Length == 0
            ? under.Select(k => k.Split('/')[0] + "/").Distinct().ToList()
            : under;
        return Task.FromResult(Glob(entries));
    }

    public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        Task.FromResult(Failing<FsReadResult>() ?? (Files.TryGetValue(path, out var content)
            ? new FsResult<FsReadResult>.Ok(new FsReadResult { FilePath = path, Content = content, TotalLines = 1, Truncated = false })
            : NotFound<FsReadResult>(path)));

    public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = path.Length == 0 || Files.Keys.Any(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)),
            Path = path,
            IsDirectory = !Files.ContainsKey(path)
        }));

    public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct)
    {
        Files[path] = content;
        return Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created", FilePath = path, Size = $"{content.Length} B", Lines = 1
        }));
    }

    public override Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        if (!Files.TryGetValue(path, out var content))
        {
            return Task.FromResult(NotFound<FsEditResult>(path));
        }

        Files[path] = edits.Aggregate(content, (text, e) => text.Replace(e.OldString, e.NewString));
        return Task.FromResult<FsResult<FsEditResult>>(new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "edited", FilePath = path, TotalOccurrencesReplaced = edits.Count, Edits = []
        }));
    }

    public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var gone = Files.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
        gone.ForEach(k => Files.Remove(k));
        return Task.FromResult(gone.Count == 0
            ? NotFound<FsRemoveResult>(path)
            : new FsResult<FsRemoveResult>.Ok(new FsRemoveResult { Status = "success", Message = "removed", OriginalPath = path, TrashPath = "none" }));
    }

    public override Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (!Files.Remove(sourcePath, out var content))
        {
            return Task.FromResult(NotFound<FsMoveResult>(sourcePath));
        }

        Files[destinationPath] = content;
        return Task.FromResult<FsResult<FsMoveResult>>(new FsResult<FsMoveResult>.Ok(new FsMoveResult
        {
            Status = "moved", Message = "moved", Source = sourcePath, Destination = destinationPath
        }));
    }

    public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        if (Failing<FsSearchResult>() is { } failed)
        {
            return Task.FromResult(failed);
        }

        var hits = Files.Where(f => f.Value.Contains(query, StringComparison.Ordinal)).Select(f => f.Key).Order().ToList();
        return Task.FromResult<FsResult<FsSearchResult>>(new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = directoryPath ?? path ?? "",
            FilesSearched = Files.Count,
            FilesWithMatches = hits.Count,
            TotalMatches = hits.Count,
            Truncated = false,
            Results = hits.Select(h => new FsSearchFileResult { File = h, MatchCount = 1 }).ToList()
        }));
    }
}