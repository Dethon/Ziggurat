using Domain.DTOs.WebChat;
using WebChat.Client.Extensions;
using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

// The page's bubbles read against the history once a streamed turn is kept: each bubble the history
// holds takes what the store knows of it — its entry id, the versions it begins, the tools its turn
// called — and keeps what only the stream showed (reasoning, tool calls drawn as they ran, the page's
// own send). The page's order and its bubbles stay as they are: two people's turns in a shared topic
// are drawn in the order they streamed, not the order they were kept, and a bubble drawn twice (one
// provider id on two) breaks the next resume's rebuild. A bubble is found by its entry id, a reply by
// the provider's id the stream grouped it by, anything else by role and words, nearest after the last
// found first. A bubble the history does not hold (an error that was only shown) stays as it was. The
// "No reply" line is the history's alone: it goes, unless the history ends on one.
public static class EntryIdNaming
{
    public static IReadOnlyList<ChatMessageModel> Apply(
        IReadOnlyList<ChatMessageModel> messages, IReadOnlyList<ChatHistoryMessage> history)
    {
        var used = new HashSet<int>();
        var last = -1;
        List<ChatMessageModel> result = [];
        foreach (var message in messages.Where(m => !m.NoReply))
        {
            var at = Find(message, history, used, last);
            if (at < 0)
            {
                result.Add(message);
                continue;
            }

            used.Add(at);
            last = at;
            result.Add(message with
            {
                EntryId = history[at].EntryId,
                Versions = history[at].Versions,
                ToolsCalled = history[at].ToolsCalled,
                Model = history[at].Model
            });
        }

        return history.LastOrDefault() is { NoReply: true } noReply ? [.. result, noReply.ToChatMessageModel()] : result;
    }

    private static int Find(ChatMessageModel message, IReadOnlyList<ChatHistoryMessage> history, HashSet<int> used, int last)
    {
        var free = Enumerable.Range(0, history.Count).Where(i => !used.Contains(i) && !history[i].NoReply).ToList();
        if (message.EntryId is { } named)
        {
            return free.FirstOrDefault(i => history[i].EntryId == named, -1);
        }

        if (message.IsError)
        {
            return -1;
        }

        // A reply's words are no name: two replies saying "OK" are two replies, so one is found by its id
        // alone.
        if (message.MessageId is not null)
        {
            return free.FirstOrDefault(i => history[i].MessageId == message.MessageId, -1);
        }

        var same = free.Where(i => history[i].Role == message.Role && history[i].Content == message.Content).ToList();
        return same.FirstOrDefault(i => i > last, same.FirstOrDefault(-1));
    }
}