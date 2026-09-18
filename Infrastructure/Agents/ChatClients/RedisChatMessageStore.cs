using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Metrics.Enums;
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

    public IReadOnlyList<ChatMessage> LastProvided(AgentSession? session) =>
        session is not null && _lastProvided.TryGetValue(session, out var messages) ? messages : [];

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
        var messages = await store.GetMessagesAsync(redisKey) ?? [];
        _lastProvided.AddOrUpdate(context.Session, messages);
        return messages;
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

        var newMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .ToArray();

        // The lock serializes concurrent same-conversation turns through the one-time
        // legacy migration in AppendMessagesAsync and preserves per-turn message ordering.
        using var latency = _metricsPublisher.MeasureLatency(LatencyStage.HistoryStore, conversationId);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await store.AppendMessagesAsync(redisKey, newMessages);
        }
        finally
        {
            _lock.Release();
        }
    }
}