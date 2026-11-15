using Domain.DTOs.WebChat;
using WebChat.Client.Extensions;
using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

// The page's bubbles read against the history once a streamed turn is kept: each bubble the history
// holds takes what the store knows of it — its entry id, the versions it begins, the tools its turn
// called — and keeps what only the stream showed (reasoning, tool calls drawn as they ran, the page's
// own send). Matched in order: by the provider's id for a reply, which the stream grouped it by, else by
// role and words. A bubble the history does not hold (an error that was only shown) stays as it was; a
// "No reply" line goes unless the history has it; anything the history holds that the page never drew
// (such a line, another page's message) is drawn where the history puts it.
public static class EntryIdNaming
{
    public static IReadOnlyList<ChatMessageModel> Apply(
        IReadOnlyList<ChatMessageModel> messages, IReadOnlyList<ChatHistoryMessage> history)
    {
        List<ChatMessageModel> result = [];
        var next = 0;
        foreach (var message in messages.Where(m => !m.NoReply))
        {
            var at = message.EntryId is { } named
                ? IndexFrom(history, next, h => h.EntryId == named)
                : IndexFrom(history, next, h => IsSame(message, h));
            if (at < 0)
            {
                result.Add(message);
                continue;
            }

            result.AddRange(history.Skip(next).Take(at - next).Select(h => h.ToChatMessageModel()));
            result.Add(Known(message, history[at]));
            next = at + 1;
        }

        result.AddRange(history.Skip(next).Select(h => h.ToChatMessageModel()));
        return result;
    }

    private static ChatMessageModel Known(ChatMessageModel shown, ChatHistoryMessage kept) => shown with
    {
        EntryId = kept.EntryId,
        Versions = kept.Versions,
        ToolsCalled = kept.ToolsCalled
    };

    private static int IndexFrom(IReadOnlyList<ChatHistoryMessage> history, int start, Func<ChatHistoryMessage, bool> match) =>
        Enumerable.Range(start, Math.Max(0, history.Count - start)).FirstOrDefault(i => match(history[i]), -1);

    private static bool IsSame(ChatMessageModel message, ChatHistoryMessage history) =>
        !history.NoReply && message.Role == history.Role && !message.IsError &&
        (message.MessageId is not null && history.MessageId is not null
            ? message.MessageId == history.MessageId
            : message.Content == history.Content);
}