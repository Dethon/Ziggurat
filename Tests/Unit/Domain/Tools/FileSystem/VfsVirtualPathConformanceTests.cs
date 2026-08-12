using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// The invariant, enforced once: a tool answers in the coordinates it was asked in. Every path in a
// response is either the caller's own string echoed back, or a backend entry with its mount point
// prefixed — never the backend's own spelling.
//
// This defect was fixed twice before, months apart, on whichever tools were noticed at the time,
// because nothing failed when the next tool forgot. Now something does. The set of tools covered
// comes from FileSystemOperations.All, the way the server conformance test, the payload-type table
// and the tool feature's key set all already do, so an operation added without a case here cannot
// compile past the first run.
public class VfsVirtualPathConformanceTests
{
    private const string Mount = "/mnt";
    private const string Elsewhere = "/elsewhere";

    // The three spellings a backend really answers in. A disk root reports the container-absolute
    // path; the virtual filesystems disagree with each other about the leading slash. Every tool
    // runs against all three.
    public static readonly IReadOnlyDictionary<string, Func<string, string>> Spellings =
        new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
        {
            ["container-absolute"] = p => "/srv/data/" + p,
            ["leading-slash mount-relative"] = p => "/" + p,
            ["bare mount-relative"] = p => p
        };

    // The fields that genuinely have no virtual path, listed where the rule is enforced so that an
    // exemption is a decision rather than an oversight. There are two.
    private static readonly IReadOnlyList<Exemption> _exemptions =
    [
        new("cwd", [VfsExecTool.Key],
            "The working directory a backend reports is its own — four backends fill it with four "
            + "different meanings, and no mount can claim it."),
        new("error", [VfsCopyTool.Key, VfsMoveTool.Key],
            "A glob entry outside the requested source directory is outside the coordinate frame, so "
            + "it has no virtual path. The entry reports no source at all and the backend's raw "
            + "string goes here, where it reads as diagnostics rather than as a path to retry.")
    ];

    private sealed record Exemption(string Field, IReadOnlyList<string> ToolKeys, string Reason);

    public static TheoryData<string, string> EveryToolInEverySpelling()
    {
        var data = new TheoryData<string, string>();
        foreach (var key in ToolKeys)
        {
            foreach (var spelling in Spellings.Keys)
            {
                data.Add(key, spelling);
            }
        }

        return data;
    }

    private static IEnumerable<string> ToolKeys =>
        FileSystemOperations.All.Where(o => o.ToolKey is not null).Select(o => o.ToolKey!);

    [Theory]
    [MemberData(nameof(EveryToolInEverySpelling))]
    public async Task EveryToolAnswersInVirtualPaths_WhateverItsBackendSpellsThemAs(string toolKey, string spelling)
    {
        var response = await Invoke(toolKey, Spellings[spelling]);

        // A tool that failed would pass the path check by having no paths at all.
        response["ok"].ShouldBeNull($"{toolKey} answered an error envelope: {response.ToJsonString()}");

        var strings = PathShapedStrings(response).ToList();
        strings.ShouldNotBeEmpty($"{toolKey} reported no path at all, so this proves nothing");

        var leaked = strings
            .Where(s => !IsExempt(toolKey, s.Field))
            .Where(s => !s.Value.StartsWith(Mount + "/", StringComparison.Ordinal)
                && !s.Value.StartsWith(Elsewhere + "/", StringComparison.Ordinal))
            .ToList();

        leaked.ShouldBeEmpty(
            $"{toolKey} answered in backend coordinates ({spelling}): "
            + string.Join(", ", leaked.Select(s => $"{s.Field}='{s.Value}'")));
    }

    private static bool IsExempt(string toolKey, string field) =>
        _exemptions.Any(e => e.Field == field && e.ToolKeys.Contains(toolKey));

    // The two transfer tools are mapped in by hand: their answer is FsTransferResult, which is not
    // any backend operation's result type, so nothing derives it from the one list for us.
    private static async Task<JsonNode> Invoke(string toolKey, Func<string, string> spell)
    {
        var backend = new HostileBackend(spell);
        var sink = new StreamingSink();
        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve(It.Is<string>(p => p.StartsWith(Mount, StringComparison.Ordinal))))
            .Returns<string>(p => Resolved(backend, p[(Mount.Length + 1)..], Mount));
        registry.Setup(r => r.Resolve(It.Is<string>(p => p.StartsWith(Elsewhere, StringComparison.Ordinal))))
            .Returns<string>(p => Resolved(sink, p[(Elsewhere.Length + 1)..], Elsewhere));

        var vfs = registry.Object;
        return toolKey switch
        {
            "read" => await new VfsTextReadTool(vfs).RunAsync($"{Mount}/docs/note.md"),
            "create" => await new VfsTextCreateTool(vfs).RunAsync($"{Mount}/docs/note.md", "hello"),
            "edit" => await new VfsTextEditTool(vfs).RunAsync(
                $"{Mount}/docs/note.md", [new TextEdit("a", "b")]),
            "glob" => await new VfsGlobFilesTool(vfs).RunAsync($"{Mount}/docs", "**/*"),
            "search" => await new VfsTextSearchTool(vfs).RunAsync("needle", directoryPath: $"{Mount}/docs"),
            "move" => await new VfsMoveTool(vfs).RunAsync($"{Mount}/docs", $"{Elsewhere}/docs"),
            "copy" => await new VfsCopyTool(vfs).RunAsync($"{Mount}/docs", $"{Elsewhere}/docs"),
            "remove" => await new VfsRemoveTool(vfs).RunAsync($"{Mount}/docs/note.md"),
            "info" => await new VfsFileInfoTool(vfs).RunAsync($"{Mount}/docs/note.md"),
            // Two segments deep so the bare mount-relative spelling still carries a slash — one
            // segment would make the check vacuous for this tool.
            "exec" => await new VfsExecTool(vfs).RunAsync($"{Mount}/docs/sub", "ls"),
            _ => throw new ArgumentOutOfRangeException(nameof(toolKey), toolKey, "No case for this tool.")
        };
    }

    // Every string in the response that looks like a path, with the field it came from. The
    // stand-in backend fills everything else — content, messages, sizes, timestamps — with strings
    // that carry no slash, so a slash here means a path.
    private static IEnumerable<(string Field, string Value)> PathShapedStrings(JsonNode? node, string field = "$") =>
        node switch
        {
            JsonObject obj => obj.SelectMany(p => PathShapedStrings(p.Value, p.Key)),
            JsonArray array => array.SelectMany(e => PathShapedStrings(e, field)),
            JsonValue value when value.TryGetValue<string>(out var text) && text.Contains('/') =>
                [(field, text)],
            _ => []
        };

    private static FsResult<FileSystemResolution> Resolved(
        IFileSystemBackend backend, string relativePath, string mountPoint) =>
        new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend, relativePath, mountPoint));

    // A backend that answers every operation in the spelling it was built with, and never in the
    // caller's. Nothing else in its answers carries a slash, so anything slash-shaped in a response
    // came from here.
    private sealed class HostileBackend(Func<string, string> spell) : FileSystemBackendBase
    {
        public override string FilesystemName => "mnt";

        public override string DescribeMount => "Answers in its own coordinates, on purpose.";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            Task.FromResult<FsResult<FsReadResult>>(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = spell(path), Content = "1: one line", TotalLines = 1, Truncated = false
            }));

        public override Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
            Task.FromResult<FsResult<FsInfoResult>>(new FsResult<FsInfoResult>.Ok(new FsInfoResult
            {
                Exists = true,
                Path = spell(path),
                IsDirectory = !path.EndsWith(".md", StringComparison.Ordinal),
                Size = 11,
                LastModified = "2026-08-06T09:00:00Z"
            }));

        public override Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
            bool createDirectories, CancellationToken ct) =>
            Task.FromResult<FsResult<FsCreateResult>>(new FsResult<FsCreateResult>.Ok(new FsCreateResult
            {
                Status = "created", FilePath = spell(path), Size = "5 B", Lines = 1
            }));

        public override Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
            Task.FromResult<FsResult<FsEditResult>>(new FsResult<FsEditResult>.Ok(new FsEditResult
            {
                Status = "edited",
                FilePath = spell(path),
                TotalOccurrencesReplaced = 1,
                Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }]
            }));

        // Two entries under the requested directory and one outside it — the case with no virtual
        // path, which a transfer reports without a source.
        public override Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
            Task.FromResult<FsResult<FsGlobResult>>(new FsResult<FsGlobResult>.Ok(new FsGlobResult
            {
                Entries = [spell("docs/note.md"), spell("docs/sub/"), spell("elsewhere/secret.md")],
                Truncated = false,
                Total = 3
            }));

        public override Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
            string? directoryPath, string? filePattern, int maxResults, int contextLines,
            VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
            Task.FromResult<FsResult<FsSearchResult>>(new FsResult<FsSearchResult>.Ok(new FsSearchResult
            {
                Query = query,
                Regex = regex,
                Path = spell(directoryPath ?? path ?? ""),
                FilesSearched = 1,
                FilesWithMatches = 1,
                TotalMatches = 1,
                Truncated = false,
                Results =
                [
                    new FsSearchFileResult
                    {
                        File = spell("docs/note.md"),
                        MatchCount = 1,
                        Matches = [new FsSearchMatch { Line = 1, Text = "a needle here" }]
                    }
                ]
            }));

        public override Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
            Task.FromResult<FsResult<FsRemoveResult>>(new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
            {
                Status = "success",
                Message = "Moved to trash",
                OriginalPath = spell(path),
                TrashPath = spell(".trash/note.md")
            }));

        public override Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
            Task.FromResult<FsResult<FsExecResult>>(new FsResult<FsExecResult>.Ok(new FsExecResult
            {
                Stdout = "note.md",
                Stderr = "",
                ExitCode = 0,
                Truncated = false,
                TimedOut = false,
                DurationMs = 1,
                Cwd = spell(path)
            }));

        public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            string path, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return Encoding.UTF8.GetBytes("bytes");
        }
    }

    // The other end of a cross-mount transfer: it takes bytes and says nothing about paths.
    private sealed class StreamingSink : FileSystemBackendBase
    {
        public override string FilesystemName => "elsewhere";

        public override string DescribeMount => "Takes bytes.";

        public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
            bool overwrite, bool createDirectories, CancellationToken ct)
        {
            long total = 0;
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                total += chunk.Length;
            }

            return total;
        }
    }
}