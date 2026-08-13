using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

public class VfsTextSearchTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "search";
    public const string Name = "text_search";

    public const string ToolDescription = """
        Searches for text across files in a filesystem, or within a single file.
        Returns matching files with line numbers and context.

        On a file mount a search over a directory is bounded twice: it stops after 50,000 entries
        have been enumerated and after 5,000 files have been read, whichever comes first.
        `budgetReached` says one of those ended the walk — distinct from `truncated`, which means
        maxResults was reached — and `entriesScanned` beside `filesSearched` says how much of the
        tree the answer covers. A large scan against zero files searched is a filePattern that
        excluded everything, not an empty directory. Searching a single filePath walks nothing.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Search within this single file only (virtual path)")]
        string? filePath = null,
        [Description("Virtual directory path to search in")]
        string? directoryPath = null,
        [Description("Glob pattern to filter files (e.g., *.md)")]
        string? filePattern = null,
        [Description("Maximum number of matches to return (default: 50)")]
        int maxResults = 50,
        [Description("Lines of context around each match (default: 1)")]
        int contextLines = 1,
        [Description("Return full content with context, or just the matching file paths")]
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content,
        CancellationToken cancellationToken = default)
    {
        if (filePath is not null)
        {
            if (!registry.Resolve(filePath).TryGetValue(out var fileResolution, out var unresolvedFile))
            {
                return unresolvedFile.ToNode();
            }

            var fileResult = await fileResolution.Backend.SearchAsync(
                query, regex, fileResolution.RelativePath, null, filePattern,
                maxResults, contextLines, outputMode, cancellationToken);
            return Normalize(fileResult, filePath, fileResolution).ToNode();
        }

        if (directoryPath is null)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Either filePath or directoryPath must be provided",
                retryable: false);
        }

        if (!registry.Resolve(directoryPath).TryGetValue(out var dirResolution, out var unresolvedDir))
        {
            return unresolvedDir.ToNode();
        }

        var result = await dirResolution.Backend.SearchAsync(
            query, regex, null, dirResolution.RelativePath, filePattern,
            maxResults, contextLines, outputMode, cancellationToken);
        return Normalize(result, directoryPath, dirResolution).ToNode();
    }

    // Both halves of the invariant in one place. The caller named the scope, so it is echoed; the
    // hits are the backend's own and are translated. Without this the obvious next call, feeding a
    // hit to text_read, came back "No filesystem mounted".
    private static FsResult<FsSearchResult> Normalize(
        FsResult<FsSearchResult> result, string virtualPath, FileSystemResolution resolution) =>
        result.Map(search => search with
        {
            Path = virtualPath,
            Results = search.Results
                .Select(r => r with { File = resolution.ToVirtualPath(r.File) })
                .ToList()
        });
}