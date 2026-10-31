using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsMoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "move";
    public const string Name = "move";

    public const string ToolDescription = """
        Moves and/or renames a file or directory. Source and destination can be on the same
        filesystem (atomic native move) or on different filesystems (streamed copy + delete; not atomic).
        Directory sources are handled recursively for cross-FS moves.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")] string sourcePath,
        [Description("Virtual path to destination")] string destinationPath,
        [Description("Overwrite destination if it exists (default: false)")] bool overwrite = false,
        [Description("Create destination parent directories if missing (default: true)")] bool createDirectories = true,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(sourcePath).TryGetValue(out var src, out var unresolvedSource))
        {
            return unresolvedSource.ToNode();
        }

        if (!registry.Resolve(destinationPath).TryGetValue(out var dst, out var unresolvedDestination))
        {
            return unresolvedDestination.ToNode();
        }

        var (source, destination) = ToolCaller.As(src, dst, arguments);
        var result = await Transfer.RunAsync(
            new TransferRequest
            {
                Source = source,
                Destination = destination,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Intent = TransferIntent.Move,
                Overwrite = overwrite,
                CreateDirectories = createDirectories
            },
            cancellationToken);

        return result.ToNode();
    }
}