using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryForgetTool(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    FeatureConfig featureConfig)
{
    private const int ContentPreviewLength = 100;
    private const int SearchLimit = 100;

    public const string Name = "memory_forget";

    public const string Description = """
                                         Removes memories. Use when information is outdated, wrong, or user
                                         explicitly asks you to forget something.

                                         When to use:
                                         - User corrects previous information → delete the outdated memory
                                         - User explicitly requests forgetting
                                         - Information is clearly outdated
                                         - Bulk cleanup of low-importance memories

                                         Use semantic query (not exact text) to find memories — e.g. "my job" will match
                                         memories about employment. The search is nearest-first with no relevance floor,
                                         so a query can reach memories unrelated to it: a query that reaches exactly one
                                         memory deletes it, and one that reaches several deletes NOTHING and returns the
                                         candidates instead — review them and call again with memoryIds naming only the
                                         ones that should go.
                                         """;

    public async Task<JsonNode> Run(
        string? memoryId = null,
        string[]? memoryIds = null,
        string? query = null,
        MemoryCategory[]? categories = null,
        string? tags = null,
        string? olderThan = null,
        double? maxImportance = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        var userId = featureConfig.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Memory is scoped per user, so a run carrying no identity has nothing to scope to.
            // That is a missing credential rather than a passing outage: waiting changes nothing.
            return ToolError.Authentication(
                "Memory is scoped to a user and this run carries no user identity",
                "Nothing here can supply it; tell the user their memories cannot be reached in this "
                + "conversation.").ToNode();
        }

        if (string.IsNullOrWhiteSpace(memoryId) && memoryIds is not { Length: > 0 }
            && string.IsNullOrWhiteSpace(query))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Either memoryId, memoryIds or query must be provided");
        }

        if (!string.IsNullOrWhiteSpace(memoryId))
        {
            return CreateSuccessResponse(await ForgetById(userId, memoryId, ct), reason);
        }

        if (memoryIds is { Length: > 0 })
        {
            return CreateSuccessResponse(await ForgetByIds(userId, memoryIds, ct), reason);
        }

        var candidates = await SearchCandidates(userId, query!, categories?.ToList(),
            ParseTags(tags), ParseDate(olderThan), maxImportance, ct);

        // The search is a k-nearest query with no relevance floor, so "what it reached" is not
        // "what the user meant": one match acts, several become a question. Deleting them all
        // would take every memory a small store holds.
        if (candidates.Count > 1)
        {
            return ConfirmationResponse(query!, candidates);
        }

        var affected = await DeleteAll(userId, candidates, ct);
        return CreateSuccessResponse(affected, reason);
    }

    private async Task<List<AffectedMemory>> ForgetById(
        string userId, string memoryId, CancellationToken ct)
    {
        var memory = await store.GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return [];
        }

        var success = await store.DeleteAsync(userId, memory.Id, ct);
        return success ? [new AffectedMemory(memory.Id, TruncateContent(memory.Content))] : [];
    }

    private async Task<List<AffectedMemory>> ForgetByIds(
        string userId, IEnumerable<string> memoryIds, CancellationToken ct)
    {
        var affected = await Task.WhenAll(memoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .Select(id => ForgetById(userId, id, ct)));

        return affected.SelectMany(a => a).ToList();
    }

    private async Task<List<MemorySearchResult>> SearchCandidates(
        string userId, string query, List<MemoryCategory>? parsedCategories, List<string>? parsedTags,
        DateTimeOffset? olderThan, double? maxImportance, CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await store.SearchAsync(
            userId, query, queryEmbedding, parsedCategories, parsedTags,
            minImportance: null, limit: SearchLimit, ct);

        return results
            .Where(r => (!olderThan.HasValue || r.Memory.CreatedAt < olderThan.Value)
                     && (!maxImportance.HasValue || r.Memory.Importance <= maxImportance.Value))
            .ToList();
    }

    private async Task<List<AffectedMemory>> DeleteAll(
        string userId, IEnumerable<MemorySearchResult> candidates, CancellationToken ct)
    {
        var affected = await Task.WhenAll(candidates
            .Select(async r =>
            {
                var success = await store.DeleteAsync(userId, r.Memory.Id, ct);
                return success ? new AffectedMemory(r.Memory.Id, TruncateContent(r.Memory.Content)) : null;
            }));

        return affected.OfType<AffectedMemory>().ToList();
    }

    private static JsonObject ConfirmationResponse(
        string query, IReadOnlyList<MemorySearchResult> candidates)
    {
        return new JsonObject
        {
            ["status"] = "confirmation_required",
            ["action"] = "none",
            ["affectedCount"] = 0,
            ["query"] = query,
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Memory.Id,
                ["content"] = TruncateContent(c.Memory.Content),
                ["relevance"] = c.Relevance
            }).ToArray()),
            ["message"] = "The query reached more than one memory and nothing was deleted — the "
                + "search is nearest-first with no relevance floor, so this list can hold memories "
                + "unrelated to the query. Call memory_forget again with memoryIds naming exactly "
                + "the ones to remove."
        };
    }

    private static List<string>? ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static DateTimeOffset? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        return DateTimeOffset.TryParse(date, out var result) ? result : null;
    }

    private static string TruncateContent(string content)
    {
        return content.Length > ContentPreviewLength
            ? content[..ContentPreviewLength] + "..."
            : content;
    }

    private static JsonObject CreateSuccessResponse(List<AffectedMemory> affected, string? reason)
    {
        var response = new JsonObject
        {
            ["status"] = "success",
            ["action"] = "delete",
            ["affectedCount"] = affected.Count,
            ["affectedMemories"] = new JsonArray(affected.Select(m => m.ToJson()).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            response["reason"] = reason;
        }

        return response;
    }

    private sealed record AffectedMemory(string Id, string Content)
    {
        public JsonNode ToJson()
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["content"] = Content
            };
        }
    }
}