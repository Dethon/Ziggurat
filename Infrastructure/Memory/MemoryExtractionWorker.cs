using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Memory;
using Domain.Metrics;
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
        // What extracting from a turn costs is the household's whose turn it was.
        var metrics = metricsPublisher.ForHousehold(request.TenantId);

        // Every model call made here — the gate, the extractor, the check — is the household's, on its
        // key, so who pays is settled before anything is asked: a household that cannot be read just
        // now is not extracted from, rather than paid for on the deployment's key.
        if (await PayerAsync(request, metrics, ct) is not { } payer)
        {
            return;
        }

        var agent = await AgentAsync(request, ct);
        if (agent is not null && !agent.EnabledFeatures.Contains("memory", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var agentName = request.AgentId is null ? null : agent?.Name ?? request.AgentId;

        var sw = Stopwatch.StartNew();

        // What the candidates did, filled in as they settle: a failure partway through still
        // reports what the ones before it stored, rather than the zeros that read as a turn which
        // wrote nothing. Written by one thread per candidate, so it is locked on write.
        var outcomes = new List<CandidateOutcome>();
        var candidateCount = 0;

        try
        {
            var extraction = await ExtractWithRetryAsync(request, agentName, payer, ct);
            candidateCount = extraction.Candidates.Count;

            // Each candidate is checked and then stored, in parallel across candidates as the
            // store fan-out always was. The embedding dedup runs after the check, unchanged.
            await Task.WhenAll(
                extraction.Candidates.Take(options.MaxCandidatesPerMessage)
                    .Select(c => VerifyThenStoreAsync(request, agentName, payer, extraction.Window, c, outcomes, ct)));

            sw.Stop();
            metrics.Publish(Event(request, agentName, sw, candidateCount, outcomes, extraction.Outcome));
        }
        catch (Exception ex) when (!IsCancellation(ex, ct))
        {
            // Both: the error page sees what broke, and the memory page sees a turn that failed
            // rather than the same zero as a turn with nothing in it. The counts are whatever the
            // candidates managed before the throw — Task.WhenAll lets its siblings finish.
            logger.LogError(ex, "Memory extraction failed for user {UserId}", request.UserId);
            metrics.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction failed: {ex.Message}"
            });
            metrics.Publish(Event(request, agentName, sw, candidateCount, outcomes, MemoryExtractionOutcomes.Failed));
        }
    }

    // Shutdown is not a failed extraction: the host's stop token unwinds the worker, and an error
    // plus a `failed` turn for it is noise on both pages on every deploy. MemoryDreamingService
    // excludes it from its own catch for the same reason.
    private static bool IsCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private static MemoryExtractionEvent Event(
        MemoryExtractionRequest request, string? agentName, Stopwatch sw, int candidateCount,
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
                AgentId = request.AgentId,
                AgentName = agentName,
                ConversationId = request.ConversationId
            };
        }
    }

    // Who pays for the extraction: the request's household, on its key (the home's on the deployment's;
    // a request naming none, too). None for a household of its own that set no key — its turns are
    // refused, so this is one whose key went away after the turn — or one the configuration could not
    // answer for just now: the turn's memory is lost rather than paid for on somebody else's key. None
    // either for a household suspended, or a person disabled, after their turn was admitted: both stop
    // every model call made for them, as dreaming skips them off the same view.
    private async Task<ModelPayer?> PayerAsync(MemoryExtractionRequest request, IMetricsPublisher metrics, CancellationToken ct)
    {
        try
        {
            switch (await agentDefinitionProvider.ForHouseholdAsync(request.TenantId, ct))
            {
                case { Suspended: true }:
                    logger.LogInformation("Household {Household} is suspended; nothing is extracted for user {UserId}",
                        request.TenantId, request.UserId);
                    return null;
                case { Configuration.DisabledPeople: { } disabled } when disabled.Contains(request.UserId):
                    logger.LogInformation("User {UserId} is disabled in household {Household}; nothing is extracted",
                        request.UserId, request.TenantId);
                    return null;
                case { Payer: { } payer }:
                    return payer with { TenantId = request.TenantId };
                default:
                    logger.LogWarning("Household {Household} has set no OpenRouter key; nothing is extracted for user {UserId}",
                        request.TenantId, request.UserId);
                    return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not read household {Household}; nothing is extracted for user {UserId}",
                request.TenantId, request.UserId);
            metrics.Publish(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction skipped: household {request.TenantId} could not be read"
            });
            metrics.Publish(Event(request, request.AgentId, Stopwatch.StartNew(), 0, [], MemoryExtractionOutcomes.Failed));
            return null;
        }
    }

    // The request's agent as its household has it, or none: unknown, or a household that could not be
    // read just now, which fails open as an unknown agent does. A person's custom agent is found under
    // the account the request names — its household and its person — as recall found it.
    private async Task<AgentDefinition?> AgentAsync(MemoryExtractionRequest request, CancellationToken ct)
    {
        if (request.AgentId is null)
        {
            return null;
        }

        try
        {
            var owner = request.TenantId is { Length: > 0 } tenant ? new Account(tenant, request.UserId) : null;
            return await agentDefinitionProvider.GetByIdAsync(request.AgentId, request.TenantId, owner, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read agent {AgentId} of household {Household}; extracting as for an unknown agent",
                request.AgentId, request.TenantId);
            return null;
        }
    }

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
        MemoryExtractionRequest request, string? agentName, ModelPayer payer, CancellationToken ct)
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
        var gate = await judge.GateAsync(window, Context(request, agentName, payer), ct);
        if (gate.Skip)
        {
            logger.LogDebug("Extraction gated for user {UserId}: nothing lasting in the turn", request.UserId);
            return Extraction.Gated;
        }

        return await ExtractWithRetryAsync(window, request.UserId, request.AgentId, payer, ct);
    }

    private static MemoryJudgmentContext Context(MemoryExtractionRequest request, string? agentName, ModelPayer payer) =>
        new(request.UserId, request.AgentId, request.ConversationId) { Payer = payer };

    private async Task<IReadOnlyList<ChatMessage>> BuildExtractionWindowAsync(MemoryExtractionRequest request)
    {
        var thread = request.ThreadStateKey is not null
            ? await threadStateStore.GetMessagesAsync(request.ThreadStateKey)
            : null;

        return ExtractionWindow.Build(
            thread, request.Anchor, request.FallbackContent, options.WindowMixedTurns);
    }

    private async Task<Extraction> ExtractWithRetryAsync(
        IReadOnlyList<ChatMessage> window, string userId, string? agentId, ModelPayer payer, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return Extraction.Of(await extractor.ExtractAsync(window, userId, agentId, payer, ct), window);
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
        MemoryExtractionRequest request, string? agentName, ModelPayer payer, IReadOnlyList<ChatMessage> window,
        ExtractionCandidate candidate, List<CandidateOutcome> outcomes, CancellationToken ct)
    {
        var outcome = await VerifyThenStoreAsync(request, agentName, payer, window, candidate, ct);
        lock (outcomes)
        {
            outcomes.Add(outcome);
        }
    }

    private async Task<CandidateOutcome> VerifyThenStoreAsync(
        MemoryExtractionRequest request, string? agentName, ModelPayer payer, IReadOnlyList<ChatMessage> window,
        ExtractionCandidate candidate, CancellationToken ct)
    {
        var verdict = await judge.VerifyAsync(window, candidate, Context(request, agentName, payer), ct);
        if (!verdict.Store)
        {
            logger.LogDebug("Dropping candidate for user {UserId}: {Content}", request.UserId, candidate.Content);
            return CandidateOutcome.Dropped;
        }

        return await StoreIfNovelAsync(
                metricsPublisher.ForHousehold(request.TenantId), request.UserId, candidate, request.ConversationId, ct)
            ? CandidateOutcome.Stored
            : CandidateOutcome.Duplicate;
    }

    private async Task<bool> StoreIfNovelAsync(
        IMetricsPublisher metrics, string userId, ExtractionCandidate candidate, string? conversationId,
        CancellationToken ct)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(candidate.Content, ct);
        metrics.RecordEmbedded(1, conversationId);

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