using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsTextCreateTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "create";
    public const string Name = "text_create";

    private const string CoercionNote =
        "The 'content' argument arrived as structured JSON rather than a string; its JSON text was written. Pass 'content' as a string next time.";

    public const string ToolDescription = """
        Creates a new text file.
        The file must not already exist unless overwrite is set to true.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path for the new file (e.g., /library/notes/new-topic.md)")]
        string filePath,
        [Description("Initial content for the file")]
        string content,
        [Description("Overwrite if file already exists (default: false)")]
        bool overwrite = false,
        [Description("Create parent directories if they don't exist (default: true)")]
        bool createDirectories = true,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(filePath).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.CreateAsync(
            resolution.RelativePath, content, overwrite, createDirectories, cancellationToken);

        // Each backend names the file it wrote in whatever spelling it uses, and two mounts disagree
        // about the same file. Echoing the caller's own path means a follow-up edit targets the file
        // this create just made — unless the backend wrote under that path: a body aimed at a
        // schedule's or a timer's directory lands in the one file it holds, and echoing the
        // directory back had a model "moving it into place" afterwards.
        var created = result.Map(create => create with { FilePath = Written(filePath, resolution, create.FilePath) });

        if (TextArg.WasCoercedArg(arguments, "content") && created.TryGetValue(out var value, out _))
        {
            return FsResultContract.ToNode(value with { Note = CoercionNote });
        }

        return created.ToNode();
    }

    private static string Written(string callerPath, FileSystemResolution resolution, string backendPath)
    {
        var callerRelative = resolution.RelativePath.Trim('/');
        var backendRelative = backendPath.Trim('/');
        return backendRelative.Length > callerRelative.Length
               && backendRelative.StartsWith(callerRelative + "/", StringComparison.Ordinal)
            ? $"{resolution.MountPoint.TrimEnd('/')}/{backendRelative}"
            : callerPath;
    }
}