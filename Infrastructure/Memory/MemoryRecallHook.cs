using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Extensions;
using Domain.Memory;
using Domain.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
    public int WindowUserTurns { get; init; } = 3;
    public int RecallTailMessages { get; init; } = 200;
}

public class MemoryRecallHook(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    IThreadStateStore threadStateStore,
    MemoryExtractionQueue extractionQueue,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryRecallHook> logger,
    MemoryRecallOptions options) : IMemoryRecallHook
{
    public const string EmbeddingErrorService = "memory-embedding";

    public async Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct)
    {
        if (!agentDefinitionProvider.HasFeatureEnabled(agentId, "memory"))
        {
            return;
        }

        // The second reason nothing is written to memory, stated beside the first. A person picks
        // a model on their own box so that what they type stays there, and extraction sends
        // exactly their own words to a hosted model — so a turn addressed to the box is not
        // extracted from. It reads what the turn *asked for*, because that is all there is here:
        // this runs before the agent, so no host has been chosen yet, and the gate therefore
        // fails closed. Recall below is deliberately not skipped with it: the box may read what
        // the agent already knew, and the boundary is one-way on purpose.
        var isLemonadeTurn = LemonadeModelId.IsLemonade(message.GetConfigPatch()?.Model);

        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            // Opened here rather than where the stopwatch used to start, which was above the guard:
            // the scope publishes on disposal, so opening it earlier would report a recall latency
            // for a recall that never happened. The guard is a string emptiness check, so nothing
            // measurable moves.
            var agentName = agentId is not null
                ? agentDefinitionProvider.GetById(agentId)?.Name ?? agentId
                : null;
            using var latency = metricsPublisher.MeasureLatency(
                LatencyStage.MemoryRecall, conversationId, agentName);

            // Whether the user is remembered at all needs nothing from the thread, and both
            // are Redis round trips on the blocking path, so it is asked alongside the read
            // instead of behind it. The search below always awaits it.
            var hasMemoriesTask = store.HasAnyMemoriesAsync(userId, ct);

            // Read before the agent is handed this turn, which is what the anchor's factory
            // says it needs and what the chat monitor test pins.
            var (persisted, persistedCount, stateKey) = await TryFetchThreadAsync(thread);
            var anchor = MemoryAnchor.TakenBeforeCurrentTurnIsPersisted(persistedCount);

            // Started before the search so the two still overlap. A user can have a profile
            // and no memories, and on the voice path that profile is what carries how the
            // agent should speak to them, so it is fetched whether or not there is anything
            // to search.
            var profileTask = options.IncludePersonalityProfile
                ? TryFetchProfileAsync(userId, ct)
                : Task.FromResult<PersonalityProfile?>(null);

            var memories = await SearchMemoriesAsync(
                userId, messageText, persisted, hasMemoriesTask, conversationId, agentName, ct);
            var profile = await profileTask;

            if (memories.Count > 0 || profile is not null)
            {
                message.SetMemoryContext(new MemoryContext(memories, profile));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(memories.Select(m => store.UpdateAccessAsync(userId, m.Memory.Id, CancellationToken.None)));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to update access timestamps for user {UserId}", userId);
                    metricsPublisher.Publish(new ErrorEvent
                    {
                        Service = "memory",
                        ErrorType = ex.GetType().Name,
                        Message = $"Access timestamp update failed: {ex.Message}"
                    });
                }
            });

            if (!isLemonadeTurn)
            {
                await extractionQueue.EnqueueAsync(
                    new MemoryExtractionRequest(userId, stateKey, anchor, conversationId, agentId)
                    {
                        FallbackContent = messageText
                    }, ct);
            }

            // Same duration as the latency event the scope publishes, taken off the scope rather
            // than from a second stopwatch.
            metricsPublisher.Publish(new MemoryRecallEvent
            {
                DurationMs = latency.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId,
                ConversationId = conversationId,
                AgentId = agentName
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory recall failed for user {UserId}", userId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Recall failed: {ex.Message}"
            });
        }
    }

    private async Task<IReadOnlyList<MemorySearchResult>> SearchMemoriesAsync(
        string userId,
        string messageText,
        ChatMessage[]? persisted,
        Task<bool> hasMemoriesTask,
        string? conversationId,
        string? agentName,
        CancellationToken ct)
    {
        // An unremembered user — one with no stored memory entries — pays neither an
        // embedding nor a vector search of an empty set. Read from storage on every turn
        // rather than cached, so a first stored memory takes effect on the very next turn
        // and there is nothing to invalidate when memories are removed.
        if (!await hasMemoriesTask)
        {
            return [];
        }

        var embeddingInput = BuildRecallWindowText(messageText, persisted, options.WindowUserTurns);

        // Timed on its own so an operator can say how much of a recall was the embedding
        // round trip and how much was everything else, rather than inferring it from the
        // difference between the stage and what storage is known to cost. The scope
        // publishes on disposal, so a failed call is measured too.
        float[]? embedding;
        using (metricsPublisher.MeasureLatency(LatencyStage.MemoryEmbedding, conversationId, agentName))
        {
            embedding = await EmbedAsync(embeddingInput, userId, ct);
        }

        // An embedding outage costs the turn its recall block and nothing else. Throwing
        // here would carry the extraction enqueue below out with it, so an outage would stop
        // the write path too and everything said during it would be dropped for good.
        return embedding is null
            ? []
            : await store.SearchAsync(userId, queryEmbedding: embedding, limit: options.DefaultLimit, ct: ct);
    }

    // Never faults. It runs alongside the search rather than after it, so a search that
    // throws would otherwise leave this abandoned and its own failure would surface later as
    // an unobserved task exception with nothing pointing back here. Failing soft also means
    // a hiccup reading one profile does not cost the turn its recalled memories.
    private async Task<PersonalityProfile?> TryFetchProfileAsync(string userId, CancellationToken ct)
    {
        try
        {
            return await store.GetProfileAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to read the personality profile for user {UserId}", userId);
            return null;
        }
    }

    private async Task<float[]?> EmbedAsync(string input, string userId, CancellationToken ct)
    {
        try
        {
            return await embeddingService.GenerateEmbeddingAsync(input, ct);
        }
        // Only a cancellation the turn itself asked for propagates. An HttpClient timeout
        // also arrives as a TaskCanceledException, and a hung server is the likeliest way
        // this fails — letting that one through would skip the metric below and carry the
        // extraction enqueue out with it.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // There is deliberately no fallback to a hosted provider. Hosted vectors are
            // 1536 wide against a 1024-wide index, so they would be invalid rather than
            // merely slower, and every search against them would error. A failure here
            // degrades to a turn with no recall block, which is what already happened when
            // the hosted provider failed. See
            // docs/adr/0019-recall-embeds-locally-with-no-cross-provider-fallback.md.
            //
            // Published under its own service name so an embedding outage is distinguishable
            // from an ordinary recall that simply found nothing.
            logger.LogWarning(ex, "Embedding the recall window failed for user {UserId}", userId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = EmbeddingErrorService,
                ErrorType = ex.GetType().Name,
                Message = $"Embedding failed: {ex.Message}"
            });
            return null;
        }
    }

    private async Task<(ChatMessage[]? Messages, long Count, string? StateKey)> TryFetchThreadAsync(AgentSession thread)
    {
        if (!RedisChatMessageStore.TryGetStateKey(thread, out var stateKey) || stateKey is null)
        {
            return (null, 0, null);
        }

        try
        {
            var countTask = threadStateStore.GetMessageCountAsync(stateKey);
            var tailTask = threadStateStore.GetTailMessagesAsync(stateKey, options.RecallTailMessages);
            await Task.WhenAll(countTask, tailTask);
            return (await tailTask, await countTask, stateKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch thread history for recall window (key {Key})", stateKey);
            return (null, 0, stateKey);
        }
    }

    private static string BuildRecallWindowText(string currentText, ChatMessage[]? persisted, int windowUserTurns)
    {
        if (persisted is null || persisted.Length == 0 || windowUserTurns <= 1)
        {
            return currentText;
        }

        var lines = persisted
            .Where(m => m.Role == ChatRole.User)
            .TakeLast(windowUserTurns - 1)
            .Select(m => m.Text)
            .Append(currentText);

        return string.Join("\n", lines);
    }
}