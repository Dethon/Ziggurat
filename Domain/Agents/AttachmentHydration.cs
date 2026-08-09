using Domain.Contracts;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// Putting an attachment's bytes back where its reference sits, on the way to the model and never
// on the way in. It sits beside TurnDecoration for the same reason: what the model reads that the
// history does not hold belongs in one place, and the copy it produces is thrown away with the
// request (ADR 0020).
public static class AttachmentHydration
{
    public static async Task<IReadOnlyList<ChatMessage>> ApplyAsync(
        IReadOnlyList<ChatMessage> messages,
        IAttachmentSource? source,
        CancellationToken ct)
    {
        if (source is null || !messages.Any(HasAttachments))
        {
            return messages;
        }

        var hydrated = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            hydrated.Add(HasAttachments(message)
                ? await HydrateAsync(message, source, ct)
                : message);
        }

        return hydrated;
    }

    private static bool HasAttachments(ChatMessage message) =>
        message.GetAttachments() is { Count: > 0 };

    private static async Task<ChatMessage> HydrateAsync(
        ChatMessage message, IAttachmentSource source, CancellationToken ct)
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
            var bytes = await source.FetchAsync(channelId, attachment.Id, ct);
            if (bytes is not null)
            {
                contents.Add(new DataContent(bytes, attachment.MediaType));
            }
        }

        if (contents.Count == 0)
        {
            return message;
        }

        var copy = message.Clone();
        copy.Contents = [.. copy.Contents, .. contents];
        return copy;
    }
}