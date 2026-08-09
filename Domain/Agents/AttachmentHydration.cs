using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// Putting an attachment's bytes back where its reference sits, on the way to the model and never
// on the way in. It sits beside TurnDecoration for the same reason: what the model reads that the
// history does not hold belongs in one place, and the copy it produces is thrown away with the
// request (ADR 0020).
//
// One rule for every kind of attachment, and one distance measured in messages. Beyond it — and
// for any reference whose bytes are gone, at any distance — the model gets a placeholder naming
// the file, so it says the file is gone rather than inventing what it contained.
public static class AttachmentHydration
{
    public const int DefaultDepthMessages = 20;

    public static async Task<IReadOnlyList<ChatMessage>> ApplyAsync(
        IReadOnlyList<ChatMessage> messages,
        IAttachmentSource? source,
        int depthMessages,
        CancellationToken ct)
    {
        if (source is null || !messages.Any(HasAttachments))
        {
            return messages;
        }

        var hydrated = new List<ChatMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            hydrated.Add(HasAttachments(message)
                ? await HydrateAsync(
                    message, source, withinDepth: messages.Count - index <= depthMessages, ct)
                : message);
        }

        return hydrated;
    }

    private static bool HasAttachments(ChatMessage message) =>
        message.GetAttachments() is { Count: > 0 };

    private static async Task<ChatMessage> HydrateAsync(
        ChatMessage message, IAttachmentSource source, bool withinDepth, CancellationToken ct)
    {
        var attachments = message.GetAttachments()!;
        var channelId = message.GetAttachmentChannelId();
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return message;
        }

        var contents = new List<AIContent>();
        foreach (var attachment in attachments)
        {
            var bytes = withinDepth
                ? await source.FetchAsync(channelId, attachment.Id, ct)
                : null;
            contents.Add(bytes is not null
                ? new DataContent(bytes, attachment.MediaType)
                : new TextContent(Placeholder(attachment)));
        }

        var copy = message.Clone();
        copy.Contents = [.. copy.Contents, .. contents];
        return copy;
    }

    private static string Placeholder(AttachmentReference attachment) =>
        $"[The attachment {attachment.FileName} ({attachment.MediaType}) is no longer available to you. " +
        "Say so if asked about it rather than describing what it contained.]";
}