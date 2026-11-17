using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Metrics.Enums;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Metrics;
using Infrastructure.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class RedisChatMessageStore(
    IThreadStateStore store,
    IMetricsPublisher? metricsPublisher = null,
    string? conversationId = null) : ChatHistoryProvider
{
    internal const string StateKey = "ChatHistoryProviderState";

    public static bool TryGetStateKey(AgentSession session, out string? stateKey)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.StateBag.TryGetValue<string>(StateKey, out var key) && !string.IsNullOrEmpty(key))
        {
            stateKey = key;
            return true;
        }
        stateKey = null;
        return false;
    }

    // Longer than any turn runs, approvals waited on included: it only frees a conversation whose agent
    // died mid-turn.
    private static readonly TimeSpan _turnInFlightExpiry = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IMetricsPublisher _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;

    // What this turn's history read returned, by session, for the context providers that run
    // after it on the same turn: the framework hands them the caller's messages alone, and the
    // skills provider needs the history to know what is already loaded. Reading the thread twice
    // per turn is the cost this avoids; weakly keyed, so a session that ends takes its entry.
    private readonly ConditionalWeakTable<AgentSession, IReadOnlyList<ChatMessage>> _lastProvided = [];

    // The failed turn the conversation ended on when this turn read it, by session, so the turn that
    // retries it can take its place without reading the conversation again.
    private readonly ConditionalWeakTable<AgentSession, FailedTail?> _failedTail = [];

    // What this turn's request said of the line it was read against, by session: a reroll's message is
    // the one the line ends on, and a message whose name the line already holds elsewhere is a new one.
    private readonly ConditionalWeakTable<AgentSession, Rerun> _rerun = [];

    // How the line ended when this turn read it, by session: a stopped turn is kept only on the line it
    // read, never on one a delete or a clear removed under it.
    private readonly ConditionalWeakTable<AgentSession, LineRead> _lineRead = [];

    private sealed record LineRead(int Count, string? LastEntryId);

    // What this turn's reply has streamed so far, by session, so a turn the person stops keeps what was
    // said before the stop. Fed by whoever reads the reply (McpAgent), since the framework hands a
    // cancelled turn's history provider nothing of it.
    private readonly ConditionalWeakTable<AgentSession, List<AgentResponseUpdate>> _streamed = [];

    // The turn's handle from its conversation group, by session, which says whether a stopped turn's
    // message was handed back to its sender, so nothing of it is kept.
    private readonly ConditionalWeakTable<AgentSession, TurnSteering> _turns = [];

    public void BeginCollecting(AgentSession session, TurnSteering? turn = null)
    {
        _streamed.AddOrUpdate(session, []);
        if (turn is null)
        {
            _turns.Remove(session);
        }
        else
        {
            _turns.AddOrUpdate(session, turn);
        }
    }

    public void Collect(AgentSession session, AgentResponseUpdate update)
    {
        if (_streamed.TryGetValue(session, out var updates))
        {
            updates.Add(update);
        }
    }

    public IReadOnlyList<ChatMessage> LastProvided(AgentSession? session) =>
        session is not null && _lastProvided.TryGetValue(session, out var messages) ? messages : [];

    // The conversation as the model reads it, for a caller that needs it before the turn runs. A
    // session with no key yet has no history, and asking must not mint one.
    public async Task<IReadOnlyList<ChatMessage>> ReadAsync(AgentSession session) =>
        TryGetStateKey(session, out var key) ? ModelView(await store.GetMessagesAsync(key!) ?? []) : [];

    // A failed turn is kept for the person, never for the model: it was not answered, and its answer
    // is what the person was told about the failure, not something the model said.
    public static IReadOnlyList<ChatMessage> ModelView(IEnumerable<ChatMessage> messages) =>
        [.. messages.Where(m => !m.IsFailedTurn())];

    public override IReadOnlyList<string> StateKeys => [StateKey];

    private static string ResolveRedisKey(AgentSession session)
    {
        if (TryGetStateKey(session, out var key))
        {
            return key!;
        }

        var newKey = Guid.NewGuid().ToString();
        session.StateBag.SetValue(StateKey, newKey);
        return newKey;
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context.Session);
        var redisKey = ResolveRedisKey(context.Session);

        // Before the read: a rewrite made after it would be written over by this turn's append.
        await store.MarkTurnInFlightAsync(redisKey, _turnInFlightExpiry);
        var stored = await store.GetMessagesAsync(redisKey) ?? [];
        var failedTail = FailedTail.Of(stored);
        _failedTail.AddOrUpdate(context.Session, failedTail);

        // A reroll's message is the one the line ends on, and the request carries it again: read once.
        _lineRead.AddOrUpdate(context.Session, new LineRead(stored.Length, stored.LastOrDefault()?.GetEntryId()));
        var rerun = Rerun.Of(stored, context.RequestMessages);
        _rerun.AddOrUpdate(context.Session, rerun);
        // A retry of a failed turn's last messages answers the whole of it: the person's earlier words in it,
        // hidden from the model while the turn stood failed, are read again before what was sent.
        IReadOnlyList<ChatMessage> messages =
        [
            .. ModelView(rerun.TrailingId is null ? stored : stored.SkipLast(1)),
            .. failedTail?.RetriedBy(context.RequestMessages)?.Earlier ?? []
        ];
        _lastProvided.AddOrUpdate(context.Session, messages);
        return messages;
    }

    // A turn that failed is kept too — the person's message and what they were told — so the
    // conversation they come back to shows both, rather than a message that was never there. A turn
    // the person stopped is not a failure, and keeps nothing, as before. The base's filters still
    // decide which of the request's messages are new.
    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.Session is not null && _turns.TryGetValue(context.Session, out var settled))
        {
            settled.Settle();
        }

        // A turn the person stopped was said as far as it got: kept, marked interrupted, and read.
        if (context.InvokeException is OperationCanceledException)
        {
            if (context.Session is null)
            {
                return default;
            }

            return KeepStoppedAsync(context, context.Session);
        }

        if (context.InvokeException is not { } failure)
        {
            return base.InvokedCoreAsync(context, cancellationToken);
        }

        var told = new ChatMessage(ChatRole.Assistant, TurnFailure.Answer(failure));
        told.MarkFailedTurn();
#pragma warning disable MAAI001 // the framework's own context, rebuilt with the answer the failure gave
        return base.InvokedCoreAsync(
            new InvokedContext(context.Agent, context.Session, context.RequestMessages, [told]), cancellationToken);
#pragma warning restore MAAI001
    }

    protected override async ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context.Session);
        var redisKey = ResolveRedisKey(context.Session);

        // ToChatResponse does not preserve AdditionalProperties on ChatMessage objects,
        // so response messages arrive without timestamps. Stamp them before persisting.
        var now = DateTimeOffset.UtcNow;
        foreach (var message in context.ResponseMessages?.Where(x => x.GetTimestamp() is null) ?? [])
        {
            message.SetTimestamp(now);
        }

        // A reply that ran out of room to say more was cut off as surely as one the person stopped.
        if (CutOffByTheLimit(context.Session))
        {
            context.ResponseMessages?.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text))
                ?.MarkInterrupted();
        }

        // Each reply message keeps the model its turn ran on, which the turn's message carries.
        var model = context.RequestMessages.LastOrDefault(m => m.Role == ChatRole.User)?.GetModel();
        foreach (var message in context.ResponseMessages?.Where(x => x.GetModel() is null) ?? [])
        {
            message.SetModel(model);
        }

        var failed = context.ResponseMessages?.Any(m => m.IsFailedTurn()) == true;
        var rerun = _rerun.TryGetValue(context.Session, out var read) ? read : Rerun.None;

        // A name the line already holds elsewhere is not this message's, and a legacy one is nobody's: it
        // is kept under a new one.
        foreach (var taken in context.RequestMessages.Where(m =>
                     m.GetEntryId() is { } id && (rerun.Taken.Contains(id) || HistoryEntries.IsLegacy(id))))
        {
            taken.SetEntryId(Guid.NewGuid().ToString("N"));
        }

        // A reroll's message is on the line already. Answered, it stays where it is; failed, it is taken
        // off and kept again marked as the failed turn it was, in its place and under its name.
        var rerunFailed = failed && rerun.TrailingId is not null;

        // The same words sent again after a failed turn are its retry, which takes its place: the
        // conversation keeps what was said once, as the person's screen does after retrying. A page retries
        // a failed turn by its last message, so a turn of several (steers that went in together) is retried
        // by the last of them, and its earlier ones are kept again, ahead of it, as the turn's.
        var retried = _failedTail.TryGetValue(context.Session, out var tail) ? tail?.RetriedBy(context.RequestMessages) : null;
        var requests = (retried?.Earlier ?? []).Concat(context.RequestMessages.Where(m => rerunFailed || !rerun.Ends(m)));
        var newMessages = (failed ? requests.Select(Failed) : requests)
            .Concat(context.ResponseMessages ?? [])
            .ToArray();
        var replaced = (retried?.Count ?? 0) + (rerunFailed ? 1 : 0);

        // A retry keeps the names of the messages it replaces, so whatever was kept under them — the
        // versions of a reroll that failed — is still found by them.
        var retriedIds = new Queue<string>(retried?.RetriedEntryIds ?? []);
        foreach (var message in newMessages.Where(m => m.GetEntryId() is null))
        {
            message.SetEntryId(message.Role == ChatRole.User && retriedIds.TryDequeue(out var kept)
                ? kept
                : Guid.NewGuid().ToString("N"));
        }

        // The lock serializes concurrent same-conversation turns through the one-time
        // legacy migration in AppendMessagesAsync and preserves per-turn message ordering.
        using var latency = _metricsPublisher.MeasureLatency(LatencyStage.HistoryStore, conversationId);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (replaced > 0)
            {
                await store.DropTailAsync(redisKey, replaced);
                _failedTail.Remove(context.Session);
            }

            await store.AppendMessagesAsync(redisKey, newMessages);
        }
        finally
        {
            _lock.Release();
            await store.ClearTurnInFlightAsync(redisKey);
        }
    }

    // Messages no run took (the conversation group's: stopped before their turn started, or a turn that
    // could not be set up), at the end of the line. Failed, each is marked so, and what the person was told
    // follows, as a failed turn is kept, so the retry of its last message takes its place.
    public async Task KeepUnansweredAsync(AgentSession session, IReadOnlyList<ChatMessage> messages, string? failedWith)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var redisKey = ResolveRedisKey(session);
        var told = failedWith is null ? null : new ChatMessage(ChatRole.Assistant, failedWith);
        told?.SetTimestamp(DateTimeOffset.UtcNow);
        told?.MarkFailedTurn();
        ChatMessage[] kept = told is null ? [.. messages] : [.. messages.Select(Failed), told];
        foreach (var message in kept.Where(m => m.GetEntryId() is null))
        {
            message.SetEntryId(Guid.NewGuid().ToString("N"));
        }

        await _lock.WaitAsync();
        try
        {
            await store.AppendMessagesAsync(redisKey, kept);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask KeepStoppedAsync(InvokedContext context, AgentSession session)
    {
        var said = Said(session);
        // A turn whose message the channel handed back to its sender is theirs to send again, not kept.
        if ((_turns.TryGetValue(session, out var turn) && turn.IsReturned) || !await StillTheLineReadAsync(session))
        {
            await ClearTurnInFlightAsync(session);
            return;
        }

#pragma warning disable MAAI001 // the framework's own context, rebuilt with what the stopped reply said
        // Not the turn's token, which the stop cancelled: what was said is kept all the same.
        await base.InvokedCoreAsync(
            new InvokedContext(context.Agent, session, context.RequestMessages, said), CancellationToken.None);
#pragma warning restore MAAI001
    }

    // A delete or a clear stops the reply and removes the conversation at once, so the stop is kept only
    // while the line ends where the turn read it — and a first turn's only while its topic still holds it.
    private async Task<bool> StillTheLineReadAsync(AgentSession session)
    {
        if (!_lineRead.TryGetValue(session, out var read) || !TryGetStateKey(session, out var key))
        {
            return true;
        }

        if (read.Count == 0)
        {
            return AgentKey.ChatConversationParts(key!) is not { } parts
                   || await store.GetTopicByChatIdAndThreadIdAsync(parts.AgentId, parts.ChatId, parts.ThreadId) is not null;
        }

        var tail = await store.GetTailMessagesAsync(key!, 1);
        return await store.GetMessageCountAsync(key!) == read.Count
               && (read.LastEntryId is null || tail?.LastOrDefault()?.GetEntryId() == read.LastEntryId);
    }

    private async ValueTask ClearTurnInFlightAsync(AgentSession session)
    {
        if (TryGetStateKey(session, out var key))
        {
            await store.ClearTurnInFlightAsync(key!);
        }
    }

    // Whether the turn's last model call ended for its output limit, as the streamed updates said.
    private bool CutOffByTheLimit(AgentSession session)
    {
        if (!_streamed.TryGetValue(session, out var updates))
        {
            return false;
        }

        _streamed.Remove(session);
        return updates.Select(u => (u.RawRepresentation as ChatResponseUpdate)?.FinishReason)
            .LastOrDefault(reason => reason is not null) == ChatFinishReason.Length;
    }

    // What a stopped reply had said: its text and every tool call that got its result, the last text
    // marked interrupted. Reasoning goes unless it led to a call that is kept, and a call with no answer
    // goes too, since no model can continue from it.
    private IReadOnlyList<ChatMessage> Said(AgentSession session)
    {
        if (!_streamed.TryGetValue(session, out var updates) || updates.Count == 0)
        {
            return [];
        }

        _streamed.Remove(session);
        var messages = updates.ToAgentResponse().Messages;
        var answered = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.CallId).ToHashSet();
        var kept = messages
            .Select(m =>
            {
                var copy = m.Clone();
                var kept = m.Contents.Where(c => c is not FunctionCallContent call || answered.Contains(call.CallId)).ToList();

                // Reasoning stays beside a call it led to, which a provider may refuse without it.
                copy.Contents = kept.Any(c => c is FunctionCallContent) ? kept : [.. kept.Where(c => c is not TextReasoningContent)];
                return copy;
            })
            .Where(m => m.Contents.Count > 0 && (m.Role != ChatRole.Assistant || m.Contents.Any(c => c is not TextContent t || t.Text.Length > 0)))
            .ToList();

        kept.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text))?.MarkInterrupted();
        return kept;
    }

    private static ChatMessage Failed(ChatMessage message)
    {
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties);
        copy.MarkFailedTurn();
        return copy;
    }

    // A request's messages read against the line: the one it ends on that the request carries again (a
    // reroll), and the names the line holds elsewhere, which a request's message cannot take.
    private sealed record Rerun(string? TrailingId, IReadOnlySet<string> Taken)
    {
        public static readonly Rerun None = new(null, new HashSet<string>());

        public static Rerun Of(IReadOnlyList<ChatMessage> stored, IEnumerable<ChatMessage> requests)
        {
            var asked = requests.Select(m => m.GetEntryId()).OfType<string>().ToHashSet();
            if (asked.Count == 0)
            {
                return None;
            }

            // Only a person's message is answered again: a name that is a reply's hides nothing from the model.
            var trailing = stored.LastOrDefault() is { Role: var role } end && role == ChatRole.User
                           && end.GetEntryId() is { } last && asked.Contains(last)
                ? last
                : null;
            var held = stored.Select(m => m.GetEntryId()).OfType<string>().Where(id => id != trailing).ToHashSet();
            return new Rerun(trailing, asked.Where(held.Contains).ToHashSet());
        }

        public bool Ends(ChatMessage request) => TrailingId is not null && request.GetEntryId() == TrailingId;
    }

    // The failed turn a conversation ends on: its messages, and what the person said in it. One turn alone —
    // what its person said and what they were told — however many failed before it: a different message
    // sent after a failed turn leaves that turn where it is, and if it fails too, its own failure is the turn
    // a retry takes the place of.
    private sealed record FailedTail(IReadOnlyList<ChatMessage> Messages)
    {
        public static FailedTail? Of(IReadOnlyList<ChatMessage> stored)
        {
            var failed = stored.Reverse().TakeWhile(m => m.IsFailedTurn()).Reverse().ToList();
            return failed.Count == 0
                ? null
                : new FailedTail([.. failed.SkipLast(1).Reverse().TakeWhile(m => m.Role == ChatRole.User).Reverse(), failed[^1]]);
        }

        // A request saying again what the turn's person said last in it — all of it, or its last messages,
        // as a page retries a turn of several by its last — retries it.
        public FailedRetry? RetriedBy(IEnumerable<ChatMessage> requests)
        {
            var asked = SaidIn(requests);
            var said = Messages.Where(m => m.Role == ChatRole.User).ToList();
            if (asked.Count == 0 || asked.Count > said.Count
                || !said.TakeLast(asked.Count).Select(m => m.Text).SequenceEqual(asked))
            {
                return null;
            }

            var earlier = said.Take(said.Count - asked.Count).ToList();
            return new FailedRetry(
                Messages.Count,
                [.. earlier.Select(Answered)],
                [.. said.TakeLast(asked.Count).Select(m => m.GetEntryId()).OfType<string>()]);
        }

        private static IReadOnlyList<string> SaidIn(IEnumerable<ChatMessage> messages) =>
            [.. messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];

        private static ChatMessage Answered(ChatMessage message)
        {
            var copy = message.Clone();
            copy.AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties);
            copy.UnmarkFailedTurn();
            return copy;
        }
    }

    // A retry of a failed turn: how many messages of the line it replaces, the person's earlier messages of
    // that turn (kept again, under their own names, ahead of what was sent), and the names of those it sent
    // again, which its own messages take.
    private sealed record FailedRetry(int Count, IReadOnlyList<ChatMessage> Earlier, IReadOnlyList<string> RetriedEntryIds);
}