namespace Domain.DTOs.WebChat;

// What of a topic's stream a page drew from the state it was handed (StreamState.BufferedMessages): how
// many entries, and how far each message had got. A page resuming a stream reads its state, then opens
// its wire, and whatever the stream carried between the two was on neither — Carlos, opening a topic
// mid-reply, had 34 characters missing from the middle of a word. The hub, subscribing, hands the page
// the rest (Since) under the same lock as its subscription.
public sealed record StreamSeen(int Count, IReadOnlyList<SeenMessage> Messages)
{
    public static StreamSeen Of(IReadOnlyList<ChatStreamMessage> buffered) => new(
        buffered.Count,
        [
            .. buffered.Where(m => m.MessageId is not null)
                .Select(m => new SeenMessage(m.MessageId!, m.Content?.Length ?? 0, m.Reasoning?.Length ?? 0,
                    Tools: ToolProgress(m.Tools)))
        ]);

    // The stream as it is now, less what the page has: the rest of each message it had begun, and every
    // entry after the ones it had. A buffer that dropped its oldest entries since (it keeps a hundred)
    // can at worst hand back a person's message the page already shows, which the page skips.
    public IReadOnlyList<ChatStreamMessage> Since(IReadOnlyList<ChatStreamMessage> buffered)
    {
        var had = Messages.GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.First());
        return
        [
            .. buffered.Select((entry, index) => entry.MessageId is { } id && had.TryGetValue(id, out var seen) && HasText(entry)
                    ? Rest(entry, seen)
                    : index >= Count ? entry : null)
                .OfType<ChatStreamMessage>()
        ];
    }

    private static ChatStreamMessage? Rest(ChatStreamMessage entry, SeenMessage seen)
    {
        var content = After(entry.Content, seen.Content);
        var reasoning = After(entry.Reasoning, seen.Reasoning);
        // A call's answer changes a step the page drew, so the calls go again whole once they moved on: a
        // page folds them in by id, and drawing one twice changes nothing.
        var tools = ToolProgress(entry.Tools) > seen.Tools ? entry.Tools : null;
        return content is null && reasoning is null && tools is null
            ? null
            : entry with { Content = content, Reasoning = reasoning, Tools = tools, IsComplete = false };
    }

    // A message's end marker is an entry of its own under the message's id, with no text.
    private static bool HasText(ChatStreamMessage entry) =>
        entry.Content is not null || entry.Reasoning is not null || entry.Tools is not null;

    // How far a message's calls had got: one for each call, one more for each that answered.
    public static int ToolProgress(IReadOnlyList<ToolCallStep>? tools) =>
        tools?.Sum(t => t.HasAnswered ? 2 : 1) ?? 0;

    private static string? After(string? text, int had) =>
        text is not null && text.Length > had ? text[had..] : null;
}

// ToolCalls is what a page from before sends (the length of a text nothing writes now); Tools is how far
// the message's calls had got (StreamSeen.ToolProgress).
public sealed record SeenMessage(string MessageId, int Content, int Reasoning, int ToolCalls = 0, int Tools = 0);