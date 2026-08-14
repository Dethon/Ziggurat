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
        if (source is null || !messages.Any(NeedsHydrating))
        {
            return messages;
        }

        // Counted over conversation messages alone. The function-calling client re-sends the
        // whole list once per tool iteration, growing it by a call and a result each time, so a
        // depth measured over the raw list would push an attachment out of its own turn partway
        // through and tell the model the file it was just given is gone.
        var distances = ConversationDistances(messages);

        var hydrated = new List<ChatMessage>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            hydrated.Add(NeedsHydrating(message)
                ? await HydrateAsync(
                    message, source, withinDepth: distances[index] < depthMessages, ct)
                : message);
        }

        return hydrated;
    }

    // How many conversation messages sit between each message and the end of the list. A
    // tool-call or tool-result message belongs to the turn being run rather than to the
    // conversation, so it moves nothing further away.
    private static int[] ConversationDistances(IReadOnlyList<ChatMessage> messages)
    {
        var distances = new int[messages.Count];
        var seen = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            distances[index] = seen;
            if (IsConversational(messages[index]))
            {
                seen++;
            }
        }

        return distances;
    }

    private static bool IsConversational(ChatMessage message) =>
        !message.Contents.Any(c => c is FunctionCallContent or FunctionResultContent);

    private static bool NeedsHydrating(ChatMessage message) =>
        message.GetAttachments() is { Count: > 0 }
        || message.GetSandboxPaths() is { Count: > 0 }
        || message.GetLandingFailures() is { Count: > 0 };

    private static async Task<ChatMessage> HydrateAsync(
        ChatMessage message, IAttachmentSource source, bool withinDepth, CancellationToken ct)
    {
        var contents = new List<AIContent>();

        // Where the files landed in the sandbox, named so the model can act on them without
        // being told a tool exists. It survives the hydration distance, because the file stays
        // in the sandbox long after the bytes stop being sent.
        if (message.GetSandboxPaths() is { Count: > 0 } paths)
        {
            contents.Add(new TextContent(AttachmentLanding.Describe(paths)));
        }

        // Which files never got there, bounded by the same distance as the bytes rather than by the
        // landed paths' unbounded life: past it the model has neither the bytes nor the file, and a
        // months-old notice about a file it could not have used anyway is noise.
        if (withinDepth && message.GetLandingFailures() is { Count: > 0 } failed)
        {
            contents.Add(new TextContent(AttachmentLanding.DescribeFailures(failed)));
        }

        var attachments = message.GetAttachments() ?? [];
        var channelId = message.GetAttachmentChannelId();
        if (attachments.Count > 0 && string.IsNullOrWhiteSpace(channelId))
        {
            return Append(message, contents);
        }
        foreach (var attachment in attachments)
        {
            var bytes = withinDepth
                ? await source.FetchAsync(channelId!, attachment.Id, ct)
                : null;
            contents.Add(bytes is not null
                ? new DataContent(bytes, attachment.MediaType)
                : new TextContent(Placeholder(attachment)));
        }

        return Append(message, contents);
    }

    private static ChatMessage Append(ChatMessage message, IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return message;
        }

        var copy = message.Clone();
        copy.Contents = [.. copy.Contents, .. contents];
        return copy;
    }

    private static string Placeholder(AttachmentReference attachment) =>
        $"[The attachment {attachment.FileName} ({attachment.MediaType}) is no longer available to you. " +
        "Say so if asked about it rather than describing what it contained.]";
}