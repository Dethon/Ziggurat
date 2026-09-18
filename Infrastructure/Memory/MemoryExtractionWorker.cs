using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryExtractionOptions
{
    public double SimilarityThreshold { get; init; } = 0.85;
    public int MaxCandidatesPerMessage { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public int WindowMixedTurns { get; init; } = 6;
}

public class MemoryExtractionWorker(
    MemoryExtractionQueue queue,
    IMemoryExtractor extractor,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    IThreadStateStore threadStateStore,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryExtractionWorker> logger,
    MemoryExtractionOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(ct))
            {
                await ProcessRequestAsync(request, ct);
            }

        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcessRequestAsync(MemoryExtractionRequest request, CancellationToken ct)
    {
        if (!agentDefinitionProvider.HasFeatureEnabled(request.AgentId, "memory"))
        {
            return;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var extraction = await ExtractWithRetryAsync(request, ct);

            var storeResults = await Task.WhenAll(
                extraction.Candidates.Take(options.MaxCandidatesPerMessage)
                    .Select(c => StoreIfNovelAsync(request.UserId, c, request.ConversationId, ct)));

            sw.Stop();
            metricsPublisher.Publish(new MemoryExtractionEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                CandidateCount = extraction.Candidates.Count,
                StoredCount = storeResults.Count(stored => stored),
                Outcome = extraction.Outcome,
                UserId = request.UserId,
                AgentId = AgentName(request),
                ConversationId = request.ConversationId
            });
        }
        catch (Exception ex)
        {
            // Both: the error page sees what broke, and the memory page sees a turn that failed
            // rather than the same zero as a turn with nothing in it.
            logger.LogError(ex, "Memory extraction failed for user {UserId}", request.UserId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction failed: {ex.Message}"
            });
            metricsPublisher.Publish(new MemoryExtractionEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                CandidateCount = 0,
                StoredCount = 0,
                Outcome = MemoryExtractionOutcomes.Failed,
                UserId = request.UserId,
                AgentId = AgentName(request),
                ConversationId = request.ConversationId
            });
        }
    }

    private string? AgentName(MemoryExtractionRequest request) =>
        request.AgentId is not null ? agentDefinitionProvider.GetById(request.AgentId)?.Name ?? request.AgentId : null;

    // What the extractor produced and how it ended: an empty window and an empty answer are both
    // "empty", and a turn the gate judges to hold nothing lasting will be "gated".
    private sealed record Extraction(IReadOnlyList<ExtractionCandidate> Candidates, string Outcome)
    {
        public static readonly Extraction Empty = new([], MemoryExtractionOutcomes.Empty);

        public static Extraction Of(IReadOnlyList<ExtractionCandidate> candidates) =>
            candidates.Count == 0 ? Empty : new Extraction(candidates, MemoryExtractionOutcomes.Extracted);
    }

    private async Task<Extraction> ExtractWithRetryAsync(
        MemoryExtractionRequest request, CancellationToken ct)
    {
        var window = await BuildExtractionWindowAsync(request);
        if (window.Count == 0)
        {
            logger.LogDebug(
                "Extraction dropped: no window could be built (user {UserId}, key {Key}, anchor {Anchor})",
                request.UserId, request.ThreadStateKey, request.Anchor.PersistedMessageCount);
            return Extraction.Empty;
        }

        return await ExtractWithRetryAsync(window, request.UserId, ct);
    }

    private async Task<IReadOnlyList<ChatMessage>> BuildExtractionWindowAsync(MemoryExtractionRequest request)
    {
        var thread = request.ThreadStateKey is not null
            ? await threadStateStore.GetMessagesAsync(request.ThreadStateKey)
            : null;

        return ExtractionWindow.Build(
            thread, request.Anchor, request.FallbackContent, options.WindowMixedTurns);
    }

    private async Task<Extraction> ExtractWithRetryAsync(
        IReadOnlyList<ChatMessage> window, string userId, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return Extraction.Of(await extractor.ExtractAsync(window, userId, ct));
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Extraction attempt {Attempt} failed for user {UserId}, retrying",
                    attempt + 1, userId);
            }
        }

        throw new UnreachableException("The last attempt either returned or threw");
    }

    private async Task<bool> StoreIfNovelAsync(
        string userId, ExtractionCandidate candidate, string? conversationId, CancellationToken ct)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(candidate.Content, ct);

        var similar = await store.SearchAsync(
            userId, queryEmbedding: embedding, categories: [candidate.Category], limit: 3, ct: ct);

        var bestMatch = similar.FirstOrDefault(s => s.Relevance > options.SimilarityThreshold);

        if (bestMatch is not null)
        {
            logger.LogDebug(
                "Skipping duplicate memory for user {UserId}: {Content} (similar to {ExistingId}, relevance {Relevance:F2})",
                userId, candidate.Content, bestMatch.Memory.Id, bestMatch.Relevance);
            return false;
        }

        var memory = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = candidate.Category,
            Content = candidate.Content,
            Context = candidate.Context,
            Importance = Math.Clamp(candidate.Importance, 0, 1),
            Confidence = Math.Clamp(candidate.Confidence, 0, 1),
            Embedding = embedding,
            Tags = candidate.Tags,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Source = new MemorySource(conversationId, null)
        };

        await store.StoreAsync(memory, ct);
        return true;
    }
}