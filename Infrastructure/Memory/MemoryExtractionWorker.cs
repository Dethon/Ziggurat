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
    MemoryJudge judge,
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

        // What the candidates did, filled in as they settle: a failure partway through still
        // reports what the ones before it stored, rather than the zeros that read as a turn which
        // wrote nothing. Written by one thread per candidate, so it is locked on write.
        var outcomes = new List<CandidateOutcome>();
        var candidateCount = 0;

        try
        {
            var extraction = await ExtractWithRetryAsync(request, ct);
            candidateCount = extraction.Candidates.Count;

            // Each candidate is checked and then stored, in parallel across candidates as the
            // store fan-out always was. The embedding dedup runs after the check, unchanged.
            await Task.WhenAll(
                extraction.Candidates.Take(options.MaxCandidatesPerMessage)
                    .Select(c => VerifyThenStoreAsync(request, extraction.Window, c, outcomes, ct)));

            sw.Stop();
            metricsPublisher.Publish(Event(request, sw, candidateCount, outcomes, extraction.Outcome));
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            // Both: the error page sees what broke, and the memory page sees a turn that failed
            // rather than the same zero as a turn with nothing in it. The counts are whatever the
            // candidates managed before the throw — Task.WhenAll lets its siblings finish.
            logger.LogError(ex, "Memory extraction failed for user {UserId}", request.UserId);
            metricsPublisher.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction failed: {ex.Message}"
            });
            metricsPublisher.Publish(Event(request, sw, candidateCount, outcomes, MemoryExtractionOutcomes.Failed));
        }
    }

    // Shutdown is not a failed extraction: the host's stop token unwinds the worker, and an error
    // plus a `failed` turn for it is noise on both pages on every deploy. MemoryDreamingService
    // excludes it from its own catch for the same reason.
    private static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private MemoryExtractionEvent Event(
        MemoryExtractionRequest request, Stopwatch sw, int candidateCount,
        List<CandidateOutcome> outcomes, string outcome)
    {
        lock (outcomes)
        {
            return new MemoryExtractionEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                CandidateCount = candidateCount,
                DroppedCount = outcomes.Count(o => o == CandidateOutcome.Dropped),
                StoredCount = outcomes.Count(o => o == CandidateOutcome.Stored),
                Outcome = outcome,
                UserId = request.UserId,
                AgentId = AgentName(request),
                ConversationId = request.ConversationId
            };
        }
    }

    private string? AgentName(MemoryExtractionRequest request) =>
        request.AgentId is not null ? agentDefinitionProvider.GetById(request.AgentId)?.Name ?? request.AgentId : null;

    // What the extractor produced and how it ended: an empty window and an empty answer are both
    // "empty", and a turn the gate judges to hold nothing lasting will be "gated".
    private sealed record Extraction(IReadOnlyList<ExtractionCandidate> Candidates, string Outcome, IReadOnlyList<ChatMessage> Window)
    {
        public static readonly Extraction Empty = new([], MemoryExtractionOutcomes.Empty, []);
        public static readonly Extraction Gated = new([], MemoryExtractionOutcomes.Gated, []);

        public static Extraction Of(IReadOnlyList<ExtractionCandidate> candidates, IReadOnlyList<ChatMessage> window) =>
            candidates.Count == 0 ? Empty : new Extraction(candidates, MemoryExtractionOutcomes.Extracted, window);
    }

    private enum CandidateOutcome
    {
        Dropped,
        Duplicate,
        Stored
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

        // The gate: a turn judged to hold nothing lasting is not extracted from. It fails toward
        // the extractor, so an unsure, late or absent answer costs a fraction of a cent and never
        // a memory. A Lemonade turn never reaches here — the recall hook did not enqueue it — so
        // the ADR 0042 boundary covers Jev without a second gate.
        var gate = await judge.GateAsync(window, Context(request), ct);
        if (gate.Skip)
        {
            logger.LogDebug("Extraction gated for user {UserId}: nothing lasting in the turn", request.UserId);
            return Extraction.Gated;
        }

        return await ExtractWithRetryAsync(window, request.UserId, ct);
    }

    private MemoryJudgmentContext Context(MemoryExtractionRequest request) =>
        new(request.UserId, AgentName(request), request.ConversationId);

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
                return Extraction.Of(await extractor.ExtractAsync(window, userId, ct), window);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Extraction attempt {Attempt} failed for user {UserId}, retrying",
                    attempt + 1, userId);
            }
        }

        throw new UnreachableException("The last attempt either returned or threw");
    }

    private async Task VerifyThenStoreAsync(
        MemoryExtractionRequest request, IReadOnlyList<ChatMessage> window, ExtractionCandidate candidate,
        List<CandidateOutcome> outcomes, CancellationToken ct)
    {
        var outcome = await VerifyThenStoreAsync(request, window, candidate, ct);
        lock (outcomes)
        {
            outcomes.Add(outcome);
        }
    }

    private async Task<CandidateOutcome> VerifyThenStoreAsync(
        MemoryExtractionRequest request, IReadOnlyList<ChatMessage> window, ExtractionCandidate candidate, CancellationToken ct)
    {
        var verdict = await judge.VerifyAsync(window, candidate, Context(request), ct);
        if (!verdict.Store)
        {
            logger.LogDebug("Dropping candidate for user {UserId}: {Content}", request.UserId, candidate.Content);
            return CandidateOutcome.Dropped;
        }

        return await StoreIfNovelAsync(request.UserId, candidate, request.ConversationId, ct)
            ? CandidateOutcome.Stored
            : CandidateOutcome.Duplicate;
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