using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob";

    public const string ToolDescription = """
        Searches a filesystem for files and directories matching a glob pattern. The pattern
        alone decides what matches — there is no mode. `*` matches one path segment, `**`
        recurses, `?` matches one character. Brace alternation expands too:
        `**/*.{jpg,png,gif}` matches any of the listed extensions. A trailing slash restricts the match to
        directories (e.g. `*/`, `src/**/`); without it, both files and directories match.
        Directory results are returned with a trailing slash so you can tell them apart; files
        are not. Entries are full virtual paths (including the mount point), ready to pass straight
        to other filesystem tools, and sorted within the response.

        On a file mount the walk is bounded: it stops once it has the 200 entries the response
        carries, and stops in any case after 50,000 entries have been enumerated. `truncated` means
        more matched than fit; `budgetReached` means the walk stopped before the tree ended, and
        `entriesScanned` says how much of it the answer covers. An empty result with
        `budgetReached` false means nothing matched; with it true, scope the basePath and try again
        rather than refining the pattern.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual base path to search from (e.g., /library or /library/docs)")]
        string basePath,
        [Description("Glob pattern. `*` = one segment, `**` = recursive, `?` = one char, "
            + "`{a,b}` = brace alternation (e.g. `**/*.{jpg,png}`). "
            + "A trailing slash (e.g. `*/`, `src/**/`) matches directories only; otherwise files "
            + "and directories both match, with directory results marked by a trailing slash.")]
        string pattern,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(basePath).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.GlobAsync(resolution.RelativePath, pattern, cancellationToken);
        // The caller named the base path, never the entries, so every entry is translated rather
        // than echoed. That yields one uniform full-virtual-path format across every filesystem,
        // directly reusable as input to read/edit/info.
        return result
            .Map(glob => glob with { Entries = glob.Entries.Select(resolution.ToVirtualPath).ToList() })
            .ToNode();
    }
}