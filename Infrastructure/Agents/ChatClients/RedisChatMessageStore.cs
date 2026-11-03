using System.Runtime.CompilerServices;
using Domain.Contracts;
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

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly IMetricsPublisher _metricsPublisher = metricsPublisher ?? NoOpMetricsPublisher.Instance;

    // What this turn's history read returned, by session, for the context providers that run
    // after it on the same turn: the framework hands them the caller's messages alone, and the
    // skills provider needs the history to know what is already loaded. Reading the thread twice
    // per turn is the cost this avoids; weakly keyed, so a session that ends takes its entry.
    private readonly ConditionalWeakTable<AgentSession, IReadOnlyList<ChatMessage>> _lastProvided = [];

    // The failed turn the conversation ended on when this turn read it, by session, so the turn that
    // retries it can take its place without reading the conversation again.
    private readonly ConditionalWeakTable<AgentSession, FailedTail> _failedTail = [];

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
        var stored = await store.GetMessagesAsync(redisKey) ?? [];
        _failedTail.AddOrUpdate(context.Session, FailedTail.Of(stored));
        var messages = ModelView(stored);
        _lastProvided.AddOrUpdate(context.Session, messages);
        return messages;
    }

    // A turn that failed is kept too — the person's message and what they were told — so the
    // conversation they come back to shows both, rather than a message that was never there. A turn
    // the person stopped is not a failure, and keeps nothing, as before. The base's filters still
    // decide which of the request's messages are new.
    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not { } failure || failure is OperationCanceledException)
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

        var failed = context.ResponseMessages?.Any(m => m.IsFailedTurn()) == true;
        var requests = failed ? context.RequestMessages.Select(Failed) : context.RequestMessages;
        var newMessages = requests
            .Concat(context.ResponseMessages ?? [])
            .ToArray();

        // The same words sent again after a failed turn are its retry, which takes its place: the
        // conversation keeps what was said once, as the person's screen does after retrying.
        var replaced = _failedTail.TryGetValue(context.Session, out var tail) && tail?.RetriedBy(context.RequestMessages) == true
            ? tail.Count
            : 0;

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
        }
    }

    private static ChatMessage Failed(ChatMessage message)
    {
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties is null ? [] : new(message.AdditionalProperties);
        copy.MarkFailedTurn();
        return copy;
    }

    // The failed turn a conversation ends on: how many messages it is, and what the person said in it.
    private sealed record FailedTail(int Count, IReadOnlyList<string> Said)
    {
        public static FailedTail? Of(IReadOnlyList<ChatMessage> stored)
        {
            var tail = stored.Reverse().TakeWhile(m => m.IsFailedTurn()).Reverse().ToList();
            return tail.Count == 0 ? null : new FailedTail(tail.Count, SaidIn(tail));
        }

        public bool RetriedBy(IEnumerable<ChatMessage> requests) => SaidIn(requests).SequenceEqual(Said);

        private static IReadOnlyList<string> SaidIn(IEnumerable<ChatMessage> messages) =>
            [.. messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text)];
    }
}