using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsQueryTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "query";
    public const string Name = "query";

    // Enough rows to answer a question, few enough that an answer never becomes a table read into
    // the conversation: the reason this tool exists instead of reading the files.
    public const int DefaultMaxRows = 100;
    public const int MaxRowsCeiling = 1000;

    public const string ToolDescription = """
        Answers one SQL statement over the tables of a mount that offers `query` — only rows that answer
        the question come back, so prefer it to reading a table's files. The dialect is DuckDB SQL. Every
        table of the mount is visible to the statement at once, so a join across them is one call; the
        mount's own description names its tables. The answer lists the columns with their types, at most
        maxRows rows, the number of rows the statement produced, and truncated=true when there were more:
        narrow the statement (WHERE, GROUP BY, LIMIT) rather than reporting a cut answer as whole.
        Read-only: nothing a statement does changes the mount.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("A path on the mount whose tables to query (e.g. /sugary)")]
        string path,
        [Description("One DuckDB SQL statement")]
        string sql,
        [Description("Most rows to return (default 100, at most 1000)")]
        int maxRows = DefaultMaxRows,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.QueryAsync(
            resolution.RelativePath, sql, Math.Clamp(maxRows, 1, MaxRowsCeiling), cancellationToken);
        return result.Map(answer => answer with { Path = path }).ToNode();
    }
}