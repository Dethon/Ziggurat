using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Files;

namespace Domain.Tools.FileSystem;

public class VfsGlobFilesTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "glob";
    public const string Name = "glob";

    // The walk's budget is not described here: it is explained by the result that hits it, in the
    // `hint` below, so a request pays for the paragraph only on the turn it is true. The pattern
    // syntax is on the parameter, where the model reads it when it writes one.
    public static readonly string ToolDescription = $"""
        Searches a filesystem for files and directories matching a glob pattern. Entries are full
        virtual paths (mount point included), sorted, ready for the other filesystem tools;
        directories carry a trailing slash, files do not. A response carries up to
        {GlobFilesTool.FileResultCap} entries; `truncated` means more matched than fit.
        """;

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
        var node = result
            .Map(glob => glob with { Entries = glob.Entries.Select(resolution.ToVirtualPath).ToList() })
            .ToNode();

        // Said only when true, and said where the numbers are: a walk that stopped before the tree
        // ended is the one case the model has to read differently, and an empty result under it is
        // "not reached", never "nothing matched".
        if (result is FsResult<FsGlobResult>.Ok { Value.BudgetReached: true } ok)
        {
            node["hint"] = ok.Value.Entries.Count == 0
                ? $"Nothing matched within the {ok.Value.EntriesScanned:N0} entries the walk reached before "
                  + "stopping, and the rest of the tree was not enumerated: scope basePath to a narrower "
                  + "directory and search again rather than refining the pattern."
                : $"The walk stopped after {ok.Value.EntriesScanned:N0} entries, before the tree ended, so "
                  + "matches deeper in it are not here: scope basePath to a narrower directory for the rest.";
        }

        return node;
    }
}