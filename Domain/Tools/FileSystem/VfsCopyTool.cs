using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsCopyTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "copy";
    public const string Name = "copy";

    public const string ToolDescription = """
        Copies a file or directory between any two virtual paths, including across different filesystems.
        Same-filesystem copies use the backend's native primitive. Cross-filesystem copies stream content
        through the agent. Directory sources are recursed automatically. Best-effort: per-file failures
        do not abort the rest of the transfer.
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
                Intent = TransferIntent.Copy,
                Overwrite = overwrite,
                CreateDirectories = createDirectories
            },
            cancellationToken);

        return result.ToNode();
    }
}