using WebChat.Client.Models;

namespace WebChat.Client.Components.Chat;

public enum MessageActionKind
{
    Edit,
    Delete
}

// What the page knows about the topic a bubble is in: whether a reply is being written to it, and
// whether this person may rewrite it (StoredTopic.MayRewrite).
public sealed record MessageActionContext(bool Streaming, bool MayRewrite);

// The actions a bubble's menu offers. None while a reply streams, since the hub refuses every rewrite
// then, and none for a message not stored yet, which has no entry id to name it by.
public static class MessageActionsLogic
{
    public static IReadOnlyList<MessageActionKind> For(ChatMessageModel message, MessageActionContext context) =>
        context.Streaming || message.EntryId is null
            ? []
            : [.. Rewrites(message, context)];

    private static IEnumerable<MessageActionKind> Rewrites(ChatMessageModel message, MessageActionContext context)
    {
        if (!context.MayRewrite)
        {
            yield break;
        }

        if (message is { Role: "user", IsError: false })
        {
            yield return MessageActionKind.Edit;
        }

        yield return MessageActionKind.Delete;
    }

    // What running the message at an index again may do again: every tool a reply from there on called.
    public static IReadOnlyList<string> ToolsFrom(IReadOnlyList<ChatMessageModel> messages, int index) =>
        [.. messages.Skip(index).SelectMany(m => m.ToolsCalled ?? []).Distinct()];
}