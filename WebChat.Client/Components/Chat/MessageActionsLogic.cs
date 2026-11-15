using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

public enum MessageActionKind
{
    Reroll,
    RerollWith,
    Edit,
    Fork,
    Delete
}

// What the page knows about the topic a bubble is in: whether a reply is being written to it, and
// whether this person may rewrite it (StoredTopic.MayRewrite).
// IsLastReply: the bubble is the conversation's last reply, the one a reroll answers again.
public sealed record MessageActionContext(bool Streaming, bool MayRewrite, bool IsLastReply = false);

// The actions a bubble's menu offers. None while a reply streams, since the hub refuses every rewrite
// then, and none for a message not stored yet, which has no entry id to name it by.
public static class MessageActionsLogic
{
    public static IReadOnlyList<MessageActionKind> For(ChatMessageModel message, MessageActionContext context) =>
        context.Streaming || message.EntryId is null
            ? []
            : [.. Rewrites(message, context).Order()];

    private static IEnumerable<MessageActionKind> RewritesOf(ChatMessageModel message, MessageActionContext context)
    {
        if (!context.MayRewrite)
        {
            yield break;
        }

        // A failed reply has its Retry, which replaces it rather than keeping it as a version.
        if (context.IsLastReply && message is { Role: "assistant", IsError: false })
        {
            yield return MessageActionKind.Reroll;
            yield return MessageActionKind.RerollWith;
        }

        if (!message.IsError && (message.Role == "user" || !string.IsNullOrEmpty(message.Content)))
        {
            yield return MessageActionKind.Edit;
        }

        yield return MessageActionKind.Delete;
    }

    // Forking reads the conversation and writes nothing of it, so whoever sees a topic may.
    private static IEnumerable<MessageActionKind> Rewrites(ChatMessageModel message, MessageActionContext context) =>
        [.. RewritesOf(message, context), MessageActionKind.Fork];

    // What running the message at an index again may do again: every tool a reply from there on called.
    public static IReadOnlyList<string> ToolsFrom(IReadOnlyList<ChatMessageModel> messages, int index) =>
        [.. messages.Skip(index).SelectMany(m => m.ToolsCalled ?? []).Distinct()];
}