using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsFileInfoTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "info";
    public const string Name = "file_info";

    public const string ToolDescription = """
        Returns metadata about a path: exists, isDirectory, size (files only), and lastModified.
        Cheap existence/metadata check — use before read/edit/move/delete to avoid errors on missing paths.
        Works for both files and directories. Never throws on missing paths; returns exists=false instead.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory (e.g., /library/notes/todo.md)")]
        string path,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path, arguments).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.InfoAsync(resolution.RelativePath, cancellationToken);
        // The backend reports the path in its own coordinates — a disk root even reports the
        // container-absolute one, which the registry would refuse if reused. Echoing the virtual path
        // the caller passed keeps the result reusable as input to every other filesystem tool.
        return result.Map(info => info with { Path = path }).ToNode();
    }
}