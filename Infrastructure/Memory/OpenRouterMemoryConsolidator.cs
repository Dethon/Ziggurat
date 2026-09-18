using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Memory;
using Domain.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryConsolidator(
    IChatClient chatClient,
    MemoryJudge judge,
    ILogger<OpenRouterMemoryConsolidator> logger) : IMemoryConsolidator
{
    private const double ClusterSimilarityThreshold = 0.60;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private static readonly ChatOptions _consolidationChatOptions = new()
    {
        Instructions = MemoryPrompts.ConsolidationSystemPrompt,
        ResponseFormat = ChatResponseFormat.ForJsonSchema<ConsolidationResponseDto>(
            serializerOptions: _jsonOptions)
    };

    private static readonly ChatOptions _profileSynthesisChatOptions = new()
    {
        Instructions = MemoryPrompts.ProfileSynthesisSystemPrompt,
        ResponseFormat = ChatResponseFormat.ForJsonSchema<ProfileDto>(
            serializerOptions: _jsonOptions)
    };

    public async Task<Consolidation> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        if (memories.Count == 0)
        {
            return Consolidation.Empty;
        }

        var decisions = new List<MergeDecision>();
        var groups = new List<IReadOnlySet<string>>();

        foreach (var cluster in BuildClusters(memories))
        {
            // Cosine proposes; the pair judgment disposes. Only memories it links — the same
            // fact, or one updating the other — reach the merge model together, and only those
            // groups may later be merged. Unanswered, the cluster goes as cosine made it and its
            // decisions are applied unvetoed: today's behaviour, by decision.
            var verdict = await judge.RelateAsync(NearestToCentroid(cluster, judge.MaxClusterMemories), Context(cluster), ct);
            var groupsToMerge = verdict.Answered ? verdict.Linked : [cluster];

            foreach (var group in groupsToMerge)
            {
                decisions.AddRange(await ConsolidateClusterAsync(group, ct));
                groups.Add(group.Select(m => m.Id).ToHashSet(StringComparer.Ordinal));
            }
        }

        return new Consolidation(decisions, groups);
    }

    private static MemoryJudgmentContext Context(IReadOnlyList<MemoryEntry> cluster) => new(cluster[0].UserId);

    // A cluster over the cap is judged on its members nearest the centroid; the rest wait for a
    // later pass. A cluster with no embeddings has no centroid and is cut in its own order.
    private static IReadOnlyList<MemoryEntry> NearestToCentroid(IReadOnlyList<MemoryEntry> cluster, int cap)
    {
        if (cluster.Count <= cap)
        {
            return cluster;
        }

        var embedded = cluster.Where(m => m.Embedding is { Length: > 0 }).ToList();
        if (embedded.Count == 0)
        {
            return cluster.Take(cap).ToList();
        }

        var width = embedded[0].Embedding!.Length;
        var centroid = Enumerable.Range(0, width)
            .Select(i => embedded.Average(m => m.Embedding!.Length > i ? m.Embedding[i] : 0f))
            .ToArray();

        return embedded
            .OrderByDescending(m => CosineSimilarity(m.Embedding!, centroid))
            .Take(cap)
            .ToList();
    }

    private async Task<IReadOnlyList<MergeDecision>> ConsolidateClusterAsync(
        IReadOnlyList<MemoryEntry> cluster, CancellationToken ct)
    {
        var ordered = cluster
            .OrderBy(m => m.Category)
            .ThenBy(m => m.Content, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summary = string.Join("\n", ordered.Select(m =>
            $"- [{m.Id}] ({m.Category.ToString().ToLowerInvariant()}) {m.Content} (importance: {m.Importance:F1}, created: {m.CreatedAt:yyyy-MM-dd})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, summary)
        };

        var response = await chatClient.GetResponseAsync(messages, _consolidationChatOptions, ct);
        return ParseMergeDecisions(response.Text);
    }

    private static IReadOnlyList<IReadOnlyList<MemoryEntry>> BuildClusters(IReadOnlyList<MemoryEntry> memories)
    {
        var withEmbeddings = memories.Where(m => m.Embedding is { Length: > 0 }).ToList();
        var withoutEmbeddings = memories.Where(m => m.Embedding is null or { Length: 0 }).ToList();

        // No embeddings available at all → fall back to a single cluster over everything.
        if (withEmbeddings.Count == 0)
        {
            return [memories];
        }

        var clusters = GreedyClusterByCosine(withEmbeddings, ClusterSimilarityThreshold);

        // Keep only multi-member clusters — singletons have nothing to merge against.
        var multiMember = clusters.Where(c => c.Count >= 2).Cast<IReadOnlyList<MemoryEntry>>().ToList();

        // Memories without embeddings still deserve a consolidation pass among themselves.
        if (withoutEmbeddings.Count >= 2)
        {
            multiMember.Add(withoutEmbeddings);
        }

        return multiMember;
    }

    private static List<List<MemoryEntry>> GreedyClusterByCosine(
        IReadOnlyList<MemoryEntry> memories, double threshold)
    {
        var clusters = new List<List<MemoryEntry>>();
        var centroids = new List<float[]>();

        foreach (var memory in memories)
        {
            var embedding = memory.Embedding!;
            var bestIndex = -1;
            var bestSimilarity = threshold;

            for (var i = 0; i < centroids.Count; i++)
            {
                var similarity = CosineSimilarity(embedding, centroids[i]);
                if (similarity >= bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
            {
                clusters[bestIndex].Add(memory);
                centroids[bestIndex] = UpdateCentroid(centroids[bestIndex], embedding, clusters[bestIndex].Count);
            }
            else
            {
                clusters.Add([memory]);
                centroids.Add((float[])embedding.Clone());
            }
        }

        return clusters;
    }

    private static float[] UpdateCentroid(float[] current, float[] addition, int newCount)
    {
        var updated = new float[current.Length];
        var prevCount = newCount - 1;
        for (var i = 0; i < current.Length; i++)
        {
            updated[i] = (current[i] * prevCount + addition[i]) / newCount;
        }
        return updated;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0.0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom == 0 ? 0.0 : dot / denom;
    }

    public async Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var summary = string.Join("\n", memories.Select(m =>
            $"- ({m.Category.ToString().ToLowerInvariant()}) {m.Content} (importance: {m.Importance:F1})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, summary)
        };

        var response = await chatClient.GetResponseAsync(messages, _profileSynthesisChatOptions, ct);
        return ParseProfile(userId, memories.Count, response.Text);
    }

    private IReadOnlyList<MergeDecision> ParseMergeDecisions(string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var wrapper = JsonSerializer.Deserialize<ConsolidationResponseDto>(json, _jsonOptions);

            return wrapper?.Decisions?
                .Select(d => new MergeDecision(
                    d.SourceIds ?? [],
                    d.Action,
                    d.MergedContent,
                    d.Category,
                    d.Importance,
                    d.Tags))
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse consolidation response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

    private PersonalityProfile ParseProfile(string userId, int memoryCount, string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var dto = JsonSerializer.Deserialize<ProfileDto>(json, _jsonOptions);
            if (dto is null)
            {
                return EmptyProfile(userId, memoryCount);
            }


            return new PersonalityProfile
            {
                UserId = userId,
                Summary = dto.Summary ?? string.Empty,
                CommunicationStyle = dto.CommunicationStyle is null ? null : new CommunicationStyle
                {
                    Preference = dto.CommunicationStyle.Preference,
                    Avoidances = dto.CommunicationStyle.Avoidances ?? [],
                    Appreciated = dto.CommunicationStyle.Appreciated ?? []
                },
                TechnicalContext = dto.TechnicalContext is null ? null : new TechnicalContext
                {
                    Expertise = dto.TechnicalContext.Expertise ?? [],
                    Learning = dto.TechnicalContext.Learning ?? []
                },
                InteractionGuidelines = dto.InteractionGuidelines ?? [],
                ActiveProjects = dto.ActiveProjects ?? [],
                Confidence = Math.Min(1.0, (double)memoryCount / 20),
                BasedOnMemoryCount = memoryCount,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse profile synthesis response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return EmptyProfile(userId, memoryCount);
        }
    }

    private static PersonalityProfile EmptyProfile(string userId, int memoryCount) => new()
    {
        UserId = userId,
        Summary = string.Empty,
        Confidence = Math.Min(1.0, (double)memoryCount / 20),
        BasedOnMemoryCount = memoryCount,
        LastUpdated = DateTimeOffset.UtcNow
    };

    private static string StripCodeFences(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```"))
        {
            return json;
        }


        var firstNewline = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```");
        return firstNewline >= 0 && lastFence > firstNewline
            ? json[(firstNewline + 1)..lastFence].Trim()
            : json;
    }

    private sealed record ConsolidationResponseDto
    {
        public List<MergeDecisionDto>? Decisions { get; init; }
    }

    private sealed record MergeDecisionDto
    {
        public IReadOnlyList<string>? SourceIds { get; init; }
        public MergeAction Action { get; init; }
        public string? MergedContent { get; init; }
        public MemoryCategory? Category { get; init; }
        public double? Importance { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
    }

    private sealed record ProfileDto
    {
        public string? Summary { get; init; }
        public CommunicationStyleDto? CommunicationStyle { get; init; }
        public TechnicalContextDto? TechnicalContext { get; init; }
        public IReadOnlyList<string>? InteractionGuidelines { get; init; }
        public IReadOnlyList<string>? ActiveProjects { get; init; }
    }

    private sealed record CommunicationStyleDto
    {
        public string? Preference { get; init; }
        public IReadOnlyList<string>? Avoidances { get; init; }
        public IReadOnlyList<string>? Appreciated { get; init; }
    }

    private sealed record TechnicalContextDto
    {
        public IReadOnlyList<string>? Expertise { get; init; }
        public IReadOnlyList<string>? Learning { get; init; }
    }
}