using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

public class FileSystemBackendBaseTests
{
    private sealed class BareBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "bare";

        public override string DescribeMount => "A bare mount that implements nothing.";

        public FsResult<GlobScope> Prologue(string? basePath, string pattern) =>
            GlobPrologue(basePath, pattern);

        public FsResult<Regex> Compile(string query, bool regex) =>
            CompileSearchRegex(query, regex);

        public FsResult<FsGlobResult> Listing(string pattern, IEnumerable<string> entries) =>
            Glob(pattern, () => entries.ToList());

        public Task<FsResult<FsSearchResult>> Scan(
            IEnumerable<string> nodes, FsSearchScan scan, Func<string, string?> content) =>
            SearchNodesAsync(nodes, (n, _) => ValueTask.FromResult(($"/{n}", content(n))), scan, CancellationToken.None);
    }

    private static readonly BareBackend _bare = new();

    public static TheoryData<string, Func<FileSystemBackendBase, Task<ToolErrorResult?>>> Operations() => new()
    {
        { VfsTextReadTool.Name, async b => ErrorOf(await b.ReadAsync("/x", null, null, default)) },
        { VfsFileInfoTool.Name, async b => ErrorOf(await b.InfoAsync("/x", default)) },
        { VfsTextCreateTool.Name, async b => ErrorOf(await b.CreateAsync("/x", "c", false, true, default)) },
        { VfsTextEditTool.Name, async b => ErrorOf(await b.EditAsync("/x", [], default)) },
        { VfsGlobFilesTool.Name, async b => ErrorOf(await b.GlobAsync("/", "*", default)) },
        {
            VfsTextSearchTool.Name,
            async b => ErrorOf(await b.SearchAsync("q", false, null, null, null, 50, 1, VfsTextSearchOutputMode.Content, default))
        },
        { VfsMoveTool.Name, async b => ErrorOf(await b.MoveAsync("/a", "/b", default)) },
        { VfsRemoveTool.Name, async b => ErrorOf(await b.DeleteAsync("/x", default)) },
        { VfsExecTool.Name, async b => ErrorOf(await b.ExecAsync("/", "c", null, default)) },
        { VfsCopyTool.Name, async b => ErrorOf(await b.CopyAsync("/a", "/b", false, true, default)) }
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task Operation_NotOverridden_ReturnsUnsupportedNamingTheOperation(
        string operation, Func<FileSystemBackendBase, Task<ToolErrorResult?>> call)
    {
        var error = await call(_bare);

        error.ShouldNotBeNull();
        error.ErrorCode.ShouldBe(ToolError.Codes.UnsupportedOperation);
        error.Message.ShouldContain(operation);
        error.Message.ShouldContain("bare");
    }

    [Fact]
    public async Task BlobOperations_NotOverridden_ReportUnsupported()
    {
        await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in _bare.ReadChunksAsync("/x", default))
            {
            }
        });

        await Should.ThrowAsync<NotSupportedException>(
            () => _bare.WriteChunksAsync("/x", AsyncEmpty(), false, true, default));
    }

    [Fact]
    public void GlobPrologue_BasePath_ScopesThePattern()
    {
        var (dirsOnly, matches) = _bare.Prologue("/docs", "*.md")
            .ShouldBeOfType<FsResult<GlobScope>.Ok>().Value;

        dirsOnly.ShouldBeFalse();
        matches("docs/notes.md").ShouldBeTrue();
        matches("other/notes.md").ShouldBeFalse();
    }

    [Fact]
    public void GlobPrologue_TrailingSlash_AsksForDirectoriesOnly()
    {
        var (dirsOnly, matches) = _bare.Prologue(null, "*/")
            .ShouldBeOfType<FsResult<GlobScope>.Ok>().Value;

        dirsOnly.ShouldBeTrue();
        matches("docs").ShouldBeTrue();
    }

    // The brace-expansion cap used to escape as a raw ArgumentException; only the disk glob caught
    // it. The prologue answers the standard envelope so every mount reports the pattern as invalid.
    [Fact]
    public void GlobPrologue_TooManyBraceAlternatives_ReturnsInvalidArgument()
    {
        var group = "{a,b,c,d,e,f,g,h,i}";

        var result = _bare.Prologue(null, string.Concat(group, group, group));

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        error.Message.ShouldContain("brace");
    }

    // The glob pattern is matched under a bounded timeout, exactly like the search pattern, and the
    // entries are produced lazily inside each backend's LINQ chain — so the timeout lands while the
    // listing is being materialized. The search path has always answered the timeout envelope; the
    // glob path used to let the raw RegexMatchTimeoutException out of the tool.
    [Fact]
    public void Glob_MatchingTimesOut_ReturnsTheTimeoutEnvelope()
    {
        var result = _bare.Listing("**/*", TimingOut());

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.Timeout);
        error.Message.ShouldContain("**/*");
    }

    private static IEnumerable<string> TimingOut()
    {
        yield return "/a";
        throw new RegexMatchTimeoutException("candidate", "pattern", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void CompileSearchRegex_LiteralQuery_MatchesCaseInsensitively()
    {
        var compiled = _bare.Compile("a.b", regex: false);

        compiled.TryGetValue(out var matcher, out _).ShouldBeTrue();
        matcher!.IsMatch("A.B").ShouldBeTrue();
        matcher.IsMatch("axb").ShouldBeFalse();
        matcher.MatchTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void CompileSearchRegex_UncompilablePattern_ReturnsInvalidArgument()
    {
        var compiled = _bare.Compile("[unclosed", regex: true);

        compiled.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    [Fact]
    public async Task SearchNodesAsync_CountsFilesAndMatches()
    {
        var result = await _bare.Scan(
            ["a", "b", "c"],
            Scan("needle"),
            n => n == "c" ? null : $"line one\nneedle in {n}\nlast");

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.FilesSearched.ShouldBe(2);
        value.FilesWithMatches.ShouldBe(2);
        value.TotalMatches.ShouldBe(2);
        value.Truncated.ShouldBeFalse();
        value.Results.Select(r => r.File).ShouldBe(["/a", "/b"]);
    }

    [Fact]
    public async Task SearchNodesAsync_MaxResultsReached_ReportsTruncated()
    {
        var result = await _bare.Scan(
            ["a", "b"],
            Scan("needle") with { MaxResults = 1 },
            _ => "needle\nneedle");

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.TotalMatches.ShouldBe(1);
        value.Truncated.ShouldBeTrue();
    }

    [Fact]
    public async Task SearchNodesAsync_UncompilablePattern_ReturnsInvalidArgument()
    {
        var result = await _bare.Scan(["a"], Scan("[unclosed") with { Regex = true }, _ => "text");

        result.TryGetValue(out _, out var error).ShouldBeFalse();
        error!.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    // A backend that follows the documented pattern: it overrides only the chunk methods and lets
    // the base defaults answer the ranged wire calls.
    private sealed class ChunkOnlyBackend(byte[] content) : FileSystemBackendBase
    {
        public override string FilesystemName => "chunked";

        public override string DescribeMount => "A chunk-only mount driven by the base blob defaults.";

        public byte[]? Written { get; private set; }

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var chunk in content.Chunk(4))
            {
                yield return chunk;
            }
        }

        public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct)
        {
            var all = new List<byte>();
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                all.AddRange(chunk.ToArray());
            }

            Written = [.. all];
            return all.Count;
        }
    }

    [Fact]
    public async Task WriteBlobDefault_AtOffsetZero_DrivesTheChunkStream()
    {
        var backend = new ChunkOnlyBackend([]);

        var result = await backend.WriteBlobAsync(
            "/f", Convert.ToBase64String([1, 2, 3]), offset: 0, overwrite: false, createDirectories: true, default);

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.BytesWritten.ShouldBe(3);
        value.TotalBytes.ShouldBe(3);
        backend.Written.ShouldBe([1, 2, 3]);
    }

    // The streamed default always writes from the start, so honouring a nonzero offset is
    // impossible; silently dropping it corrupted every multi-chunk transfer. It must refuse.
    [Fact]
    public async Task WriteBlobDefault_NonzeroOffset_RefusesInsteadOfWritingFromTheStart()
    {
        var backend = new ChunkOnlyBackend([]);

        var result = await backend.WriteBlobAsync(
            "/f", Convert.ToBase64String([4, 5]), offset: 3, overwrite: false, createDirectories: true, default);

        var error = ErrorOf(result);
        error.ShouldNotBeNull();
        error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        error.Message.ShouldContain("offset");
        backend.Written.ShouldBeNull();
    }

    [Fact]
    public async Task ReadBlobDefault_HonoursTheRequestedRangeAcrossChunks()
    {
        var backend = new ChunkOnlyBackend([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var result = await backend.ReadBlobAsync("/f", offset: 3, length: 4, default);

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        Convert.FromBase64String(value!.ContentBase64).ShouldBe(new byte[] { 4, 5, 6, 7 });
        value.Eof.ShouldBeFalse();
        value.TotalBytes.ShouldBe(10);
    }

    [Fact]
    public async Task ReadBlobDefault_RangeReachingTheEnd_ReportsEof()
    {
        var backend = new ChunkOnlyBackend([1, 2, 3, 4, 5, 6]);

        var result = await backend.ReadBlobAsync("/f", offset: 4, length: 10, default);

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        Convert.FromBase64String(value!.ContentBase64).ShouldBe(new byte[] { 5, 6 });
        value.Eof.ShouldBeTrue();
        value.TotalBytes.ShouldBe(6);
    }

    [Fact]
    public async Task ReadBlobDefault_OffsetPastTheEnd_ReturnsEmptyAndEof()
    {
        var backend = new ChunkOnlyBackend([1, 2]);

        var result = await backend.ReadBlobAsync("/f", offset: 9, length: 4, default);

        result.TryGetValue(out var value, out _).ShouldBeTrue();
        value!.ContentBase64.ShouldBe(string.Empty);
        value.Eof.ShouldBeTrue();
        value.TotalBytes.ShouldBe(2);
    }

    private static FsSearchScan Scan(string query) => new()
    {
        Query = query,
        Regex = false,
        Path = "/",
        MaxResults = 50,
        ContextLines = 0,
        OutputMode = VfsTextSearchOutputMode.Content
    };

    private static ToolErrorResult? ErrorOf<T>(FsResult<T> result) where T : class =>
        result is FsResult<T>.Err err ? err.Error : null;

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}