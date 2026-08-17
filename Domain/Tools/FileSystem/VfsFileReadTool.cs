using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsFileReadTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "read";
    public const string Name = "file_read";

    public const string ToolDescription = """
        Reads a text file and returns its content with line numbers.
        Returns content formatted as "1: first line\n2: second line\n..." with trailing metadata.
        Large files are truncated — use offset and limit for pagination.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Start from this line number (1-based, default: 1)")]
        int? offset = null,
        [Description("Max lines to return")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(filePath).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.ReadAsync(resolution.RelativePath, offset, limit, cancellationToken);
        // The backend reports the file path in its own coordinates — a disk root even reports the
        // container-absolute one, which the registry would refuse if reused. Echoing the virtual path
        // the caller passed keeps the result reusable as input to every other filesystem tool.
        return result.Map(read => read with { FilePath = filePath }).ToNode();
    }
}