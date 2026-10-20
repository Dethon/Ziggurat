using System.Collections.Concurrent;
using System.Text;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerSugary.Channel;

// What came back for a turn the app is waiting on. Only an answer carries text; everything else is
// a reason the app is told why there is no estimate.
public abstract record TurnReply
{
    private TurnReply()
    {
    }

    public sealed record Answered(string Text) : TurnReply;

    // The agent reported an error on the turn: a refused request, a provider that failed.
    public sealed record Failed(string Reason) : TurnReply;

    // The turn ended and its last message carried no text.
    public sealed record Unanswered : TurnReply;

    // Nobody was polling, so the turn was never delivered (gate-on-live).
    public sealed record NotListening : TurnReply;

    public sealed record TimedOut : TurnReply;
}

// The turns the app is waiting on, by the key each was sent under. The agent echoes the key on every
// reply chunk, which is what lets a reply be matched to its request even when two estimates of one
// conversation overlap, or a cancelled turn's last chunks arrive after the next has started.
public sealed class AgentTurns
{
    private readonly ConcurrentDictionary<string, PendingTurn> _pending = new();

    public PendingTurn Open()
    {
        var turn = new PendingTurn(Guid.NewGuid().ToString("N"), this);
        _pending[turn.TurnKey] = turn;
        return turn;
    }

    // A chunk for a turn nobody is waiting on any more (it timed out, the phone gave up) is dropped:
    // there is no one left to show it to.
    public void Receive(SendReplyParams chunk)
    {
        if (chunk.TurnKey is { } key && _pending.TryGetValue(key, out var turn))
        {
            turn.Receive(chunk);
        }
    }

    internal void Close(string turnKey) => _pending.TryRemove(turnKey, out _);
}

public sealed class PendingTurn : IDisposable
{
    private readonly AgentTurns _turns;
    private readonly TaskCompletionSource<TurnReply> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(string? MessageId, StringBuilder Text)> _messages = [];
    private readonly Lock _gate = new();

    internal PendingTurn(string turnKey, AgentTurns turns)
    {
        TurnKey = turnKey;
        _turns = turns;
    }

    public string TurnKey { get; }

    public Task<TurnReply> Reply => _reply.Task;

    internal void Receive(SendReplyParams chunk)
    {
        lock (_gate)
        {
            switch (chunk.ContentType)
            {
                case ReplyContentType.Text:
                    Append(chunk.MessageId, chunk.Content);
                    break;
                case ReplyContentType.Error:
                    _reply.TrySetResult(new TurnReply.Failed(chunk.Content));
                    break;
            }

            if (chunk.ContentType == ReplyContentType.StreamComplete || chunk.IsComplete)
            {
                _reply.TrySetResult(Final());
            }
        }
    }

    // A message is one model call's text. A turn that calls a tool can say something before the call
    // and answer after it; the response format applies to the final answer, so the last message is
    // the answer and nothing before it is read as part of it.
    private void Append(string? messageId, string text)
    {
        if (_messages.Count == 0 || _messages[^1].MessageId != messageId)
        {
            _messages.Add((messageId, new StringBuilder()));
        }

        _messages[^1].Text.Append(text);
    }

    private TurnReply Final() =>
        _messages.Count > 0 && _messages[^1].Text.ToString() is var text && !string.IsNullOrWhiteSpace(text)
            ? new TurnReply.Answered(text)
            : new TurnReply.Unanswered();

    public void Dispose() => _turns.Close(TurnKey);
}