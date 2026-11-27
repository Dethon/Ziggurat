using Domain.Agents;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;

namespace Domain.Conversations;

// The pictures a tool's answer showed the model (a file_read of an image, a view_image, an MCP tool's
// image), each by where its bytes rest for the model (the read-image store's key, ReadImageHydration's
// own rule) and by the reference a chat that draws tool calls keeps its copy under. The reference is a
// function of the conversation and the call alone, so the agent copying a picture as its answer streams
// and the history naming it on a reload name the same file, with nothing more kept on the line.
public static class ToolImages
{
    public sealed record Shown(string StoreKey, AttachmentReference Reference);

    private const int NameLength = 60;

    public static IReadOnlyList<Shown> Of(FunctionResultContent result, string conversationId) =>
    [
        .. ReadImageHydration.Recognise(result.CallId, result.Result)
            .Select(read => (Read: read, Key: ReadImageHydration.StoreKey(read)))
            .Select(x => new Shown(x.Key, new AttachmentReference
            {
                Id = $"{AttachmentEndpointPaths.ConversationDirectory(conversationId)}/tool-{Segment(x.Key)}",
                FileName = FileNameOf(x.Read),
                MediaType = x.Read.MediaType ?? "image/png",
                SizeBytes = x.Read.SizeBytes
            }))
    ];

    // A call id is the provider's; the store keeps each file in a directory named by the reference.
    private static string Segment(string key)
    {
        var safe = new string([.. key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '-')]);
        return safe.Length <= 120 ? safe : safe[..120];
    }

    // A file read is named by its own file; a page's picture by the label the model chose it by.
    private static string FileNameOf(ReadImageReference read)
    {
        if (read.Origin == ReadImageOrigin.Mount)
        {
            return Path.GetFileName(read.Name.TrimEnd('/')) is { Length: > 0 } file ? file : "image";
        }

        var label = read.Name.Trim();
        var name = label.Length <= NameLength ? label : label[..NameLength].TrimEnd();
        return $"{(name.Length > 0 ? name : "image")}{ExtensionOf(read.MediaType)}";
    }

    private static string ExtensionOf(string? mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ""
    };
}