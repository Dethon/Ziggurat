using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

// Names a page's bubbles from the history, in order: a bubble already named moves the reading on to
// its entry, an unnamed one takes the next history message that is the same message — by the provider's
// id for a reply, which the stream grouped it by, else by role and words. A bubble the history does
// not hold (an error that was only shown) stays unnamed.
public static class EntryIdNaming
{
    public static IReadOnlyList<ChatMessageModel> Apply(
        IReadOnlyList<ChatMessageModel> messages, IReadOnlyList<ChatHistoryMessage> history)
    {
        var next = 0;
        return messages.Select(message =>
        {
            var at = message.EntryId is { } named
                ? IndexFrom(history, next, h => h.EntryId == named)
                : IndexFrom(history, next, h => IsSame(message, h));
            if (at < 0)
            {
                return message;
            }

            next = at + 1;
            return message.EntryId is null ? message with { EntryId = history[at].EntryId } : message;
        }).ToList();
    }

    private static int IndexFrom(IReadOnlyList<ChatHistoryMessage> history, int start, Func<ChatHistoryMessage, bool> match) =>
        Enumerable.Range(start, Math.Max(0, history.Count - start)).FirstOrDefault(i => match(history[i]), -1);

    private static bool IsSame(ChatMessageModel message, ChatHistoryMessage history) =>
        message.Role == history.Role && !message.IsError &&
        (message.MessageId is not null && history.MessageId is not null
            ? message.MessageId == history.MessageId
            : message.Content == history.Content);
}