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
                                         memories about employment.
                                         """;

    public async Task<JsonNode> Run(
        string? memoryId = null,
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

        if (string.IsNullOrWhiteSpace(memoryId) && string.IsNullOrWhiteSpace(query))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Either memoryId or query must be provided");
        }

        var affectedMemories = !string.IsNullOrWhiteSpace(memoryId)
            ? await ForgetById(userId, memoryId, ct)
            : await ForgetBySearch(userId, query!, categories?.ToList(), ParseTags(tags),
                ParseDate(olderThan), maxImportance, ct);

        return CreateSuccessResponse(affectedMemories, reason);
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

    private async Task<List<AffectedMemory>> ForgetBySearch(
        string userId, string query, List<MemoryCategory>? parsedCategories, List<string>? parsedTags,
        DateTimeOffset? olderThan, double? maxImportance, CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await store.SearchAsync(
            userId, query, queryEmbedding, parsedCategories, parsedTags,
            minImportance: null, limit: SearchLimit, ct);

        var affected = await Task.WhenAll(results
            .Where(r => (!olderThan.HasValue || r.Memory.CreatedAt < olderThan.Value)
                     && (!maxImportance.HasValue || r.Memory.Importance <= maxImportance.Value))
            .Select(async r =>
            {
                var success = await store.DeleteAsync(userId, r.Memory.Id, ct);
                return success ? new AffectedMemory(r.Memory.Id, TruncateContent(r.Memory.Content)) : null;
            }));

        return affected.OfType<AffectedMemory>().ToList();
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