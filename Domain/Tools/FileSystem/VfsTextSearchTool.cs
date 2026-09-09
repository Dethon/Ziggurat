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

    // The two walk budgets are not described here: the result that hits one explains it, in the
    // `hint` below, so a request pays for the paragraph only on the turn it is true.
    public const string ToolDescription = """
        Searches for text across the files of a directory, or within a single file, and returns
        the matches with line numbers and context. `truncated` means maxResults was reached.
        """;

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
            return WithHint(Normalize(fileResult, filePath, fileResolution));
        }

        if (directoryPath is null)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Either filePath or directoryPath must be provided");
        }

        if (!registry.Resolve(directoryPath).TryGetValue(out var dirResolution, out var unresolvedDir))
        {
            return unresolvedDir.ToNode();
        }

        var result = await dirResolution.Backend.SearchAsync(
            query, regex, null, dirResolution.RelativePath, filePattern,
            maxResults, contextLines, outputMode, cancellationToken);
        return WithHint(Normalize(result, directoryPath, dirResolution));
    }

    // Said only when true, and said beside the numbers the model would otherwise have to read
    // against each other: a walk that stopped before the tree ended, and — the case no flag alone
    // can explain — a large scan that read no file at all, which is a filePattern that excluded
    // everything rather than an empty directory.
    private static JsonNode WithHint(FsResult<FsSearchResult> result)
    {
        var node = result.ToNode();
        if (result is not FsResult<FsSearchResult>.Ok { Value.BudgetReached: true } ok)
        {
            return node;
        }

        var search = ok.Value;
        node["hint"] = search.FilesSearched == 0
            ? $"The walk enumerated {search.EntriesScanned:N0} entries and read none of them before stopping: "
              + "filePattern excluded every file it met. Widen filePattern, or scope directoryPath to where "
              + "the files are."
            : $"The walk stopped after {search.EntriesScanned:N0} entries and {search.FilesSearched:N0} files "
              + "read, before the tree ended, so matches deeper in it are not here: scope directoryPath to a "
              + "narrower directory, or tighten filePattern, for the rest.";
        return node;
    }

    // Both halves of the invariant in one place. The caller named the scope, so it is echoed; the
    // hits are the backend's own and are translated. Without this the obvious next call, feeding a
    // hit to file_read, came back "No filesystem mounted".
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