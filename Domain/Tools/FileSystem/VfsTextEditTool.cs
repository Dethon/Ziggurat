using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class VfsTextEditTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "edit";
    public const string Name = "text_edit";

    private const string CoercionNote =
        "One or more edits provided 'oldString'/'newString' as structured JSON rather than strings; their JSON text was used. Pass them as strings next time.";

    public const string ToolDescription = """
        Edits a text file by applying a non-empty list of edits in order, atomically.
        Each edit has oldString (exact, case-sensitive), newString, and replaceAll (default false).
        Edits are applied sequentially — edit N sees the result of edits 1…N-1.
        If any edit fails (oldString not found, or multiple matches without replaceAll), the file is not written.
        When replaceAll is false, oldString must appear exactly once at that point in the sequence.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Edits to apply in order, atomically. Must be non-empty.")]
        IReadOnlyList<TextEdit> edits,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(filePath, arguments).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        var result = await resolution.Backend.EditAsync(resolution.RelativePath, edits, cancellationToken);

        // Same reason as create: the backend answers in its own coordinates, so reading back what
        // was just edited failed on the path the edit reported.
        var edited = result.Map(edit => edit with { FilePath = filePath });

        if (TextArg.EditsWereCoercedArg(arguments) && edited.TryGetValue(out var value, out _))
        {
            return FsResultContract.ToNode(value with { Note = CoercionNote });
        }

        return edited.ToNode();
    }
}