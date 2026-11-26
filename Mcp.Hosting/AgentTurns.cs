using System.Collections.Concurrent;
using System.Text;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Mcp.Hosting;

// What came back for a turn somebody is waiting on. Only an answer carries text; everything else is
// a reason the one waiting is told why there is none.
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

// The turns a channel server is waiting on, by the key each was sent under. The agent echoes the key
// on every reply chunk, which is what lets a reply be matched to its request even when two turns of
// one conversation overlap, or a cancelled turn's last chunks arrive after the next has started.
//
// The clock and the deadline are the caller's, per send: a server waits on the agent on a clock of
// its own, which a test may move without moving the inbox's liveness with it.
public sealed class AgentTurns(ChannelNotificationEmitter emitter, ILogger<AgentTurns> logger)
{
    private readonly ConcurrentDictionary<string, PendingTurn> _pending = new();

    // The message goes out as a turn of its own, under a key minted here, and the whole reply to it is
    // waited for. A turn nobody waits on any more is cancelled: an answer nobody will read is not
    // worth the rest of the turn.
    public async Task<TurnReply> SendAsync(
        ChannelMessageNotification message, TimeSpan timeout, TimeProvider clock, CancellationToken ct)
    {
        using var turn = Open();
        if (!await emitter.EmitAsync(message with { TurnKey = turn.TurnKey }, ct))
        {
            return new TurnReply.NotListening();
        }

        try
        {
            var reply = await turn.Reply.WaitAsync(timeout, clock, ct);
            // The agent's reason is the exception it caught, often with the provider's own body, and
            // in English: kept here for whoever looks into it; what reaches a page is the server's to say.
            if (reply is TurnReply.Failed failed)
            {
                logger.LogWarning("The agent failed turn {TurnKey} of {ConversationId}: {Reason}",
                    turn.TurnKey, message.ConversationId, failed.Reason);
            }

            return reply;
        }
        catch (TimeoutException)
        {
            await CancelAsync(message, clock);
            return new TurnReply.TimedOut();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelAsync(message, clock);
            throw;
        }
    }

    // A chunk for a turn nobody is waiting on any more (it timed out, the request gave up) is
    // dropped: there is no one left to show it to.
    public void Receive(SendReplyParams chunk)
    {
        if (chunk.TurnKey is { } key && _pending.TryGetValue(key, out var turn))
        {
            turn.Receive(chunk);
        }
    }

    internal PendingTurn Open()
    {
        var turn = new PendingTurn(Guid.NewGuid().ToString("N"), this);
        _pending[turn.TurnKey] = turn;
        return turn;
    }

    internal void Close(string turnKey) => _pending.TryRemove(turnKey, out _);

    // Naming the agent the message named: the agent groups messages by conversation and agent, so a
    // cancel naming none reaches only a turn whose message named none either.
    private Task<bool> CancelAsync(ChannelMessageNotification message, TimeProvider clock) =>
        emitter.EmitCancelAsync(new ChannelCancelNotification
        {
            ConversationId = message.ConversationId,
            AgentId = message.AgentId,
            Timestamp = clock.GetUtcNow()
        });
}

internal sealed class PendingTurn : IDisposable
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