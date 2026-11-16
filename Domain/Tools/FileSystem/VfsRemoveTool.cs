using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsRemoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "remove";
    public const string Name = "remove";

    public const string ToolDescription = """
        Removes a file or directory.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file or directory to remove")]
        string path,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(path, arguments).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.DeleteAsync(resolution.RelativePath, cancellationToken);
        // The backend names the path it deleted in its own coordinates — a disk root reports the
        // container-absolute one — and the registry refuses that if the model feeds it back. The
        // caller's own string is the only spelling that stays usable.
        //
        // The trash location has no virtual path at all: it sits outside every mount, so there is
        // nothing to translate and nothing the model could read or restore from. It is reported
        // empty rather than as a path that looks actionable and is not.
        return result
            .Map(removal => removal with { OriginalPath = path, TrashPath = "" })
            .ToNode();
    }
}