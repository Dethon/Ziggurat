using System.ComponentModel;
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

    // Four filters (categories, tags, olderThan, maxImportance) used to widen this schema for a
    // sweep nobody made through them: the recall block spells each memory's id and importance,
    // so a sweep names ids. What is left is what a forget needs, re-sent on every request.
    public const string Description = """
                                         Removes memories. Use when information is outdated, wrong, or user
                                         explicitly asks you to forget something.

                                         When to use:
                                         - User corrects previous information → delete the outdated memory
                                         - User explicitly requests forgetting
                                         - Information is clearly outdated — a plan the user has just reported done
                                           (home from the conference, moved in, the course finished) goes in that turn, unasked
                                         - Bulk cleanup of low-importance memories, by the ids the context block shows

                                         A query is semantic, not the memory's exact text: nearest-first with no
                                         relevance floor, so it can reach unrelated memories. Reaching exactly one
                                         deletes it; reaching several deletes NOTHING and returns them as candidates
                                         to name in memoryIds.
                                         """;

    public async Task<JsonNode> Run(
        [Description("The `id` of one memory, exactly as the [Memory context] block, a search "
                     + "result or this tool's own `candidates` list spelled it — the bracketed id at "
                     + "the head of the fact's line. Never the memory's text, and never a list — use "
                     + "memoryIds for several.")]
        string? memoryId = null,
        [Description("Several memory `id`s to DELETE, each as the [Memory context] block, a search "
                     + "result or the `candidates` list spelled it. Only the ones that must go: this "
                     + "call deletes every id it names, and is no place to list the ones to keep.")]
        string[]? memoryIds = null,
        [Description("A semantic description of what to forget, when you have no id — 'my job', "
                     + "not the memory's exact words. Reaching one memory deletes it; reaching "
                     + "several deletes nothing and returns them as candidates.")]
        string? query = null,
        [Description("Why this is being forgotten, in a few words. Recorded, never shown to the user.")]
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
            var byId = await ForgetById(userId, memoryId, ct);
            // An id nothing has is a guess, and "success, nothing affected" let the guess read as
            // a deletion; the model then went looking for the fact by query, one call over.
            return byId.Count == 0
                ? ToolError.Create(
                    ToolError.Codes.NotFound,
                    $"No memory has the id '{NormalizeId(memoryId)}'.",
                    "The ids are the bracketed ones in the [Memory context] block, exactly as "
                    + "written there; pass one of those, or forget by query.")
                : CreateSuccessResponse(byId, reason);
        }

        if (memoryIds is { Length: > 0 })
        {
            var asked = memoryIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(NormalizeId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var byIds = await ForgetByIds(userId, asked, ct);
            var unknown = asked.Where(id => !byIds.Any(a => string.Equals(a.Id, id, StringComparison.Ordinal))).ToList();

            // The same guess as the singular form, with a list around it: a list naming nothing is
            // refused rather than answered "success, nothing affected". A list that partly landed
            // is a success that names what was not there, so a partial pass is not read as a whole
            // one.
            return unknown.Count == asked.Count
                ? ToolError.Create(
                    ToolError.Codes.NotFound,
                    $"No memory has any of these ids: {string.Join(", ", unknown.Select(id => $"'{id}'"))}.",
                    "The ids are the bracketed ones in the [Memory context] block, exactly as "
                    + "written there; pass those, or forget by query.")
                : CreateSuccessResponse(byIds, reason, unknown);
        }

        var candidates = await SearchCandidates(userId, query!, ct);

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

    // The block writes an id in brackets, so a model copying one as it reads it brings them
    // along. Refusing that with "exactly as written there" is a loop the model cannot leave.
    private static string NormalizeId(string memoryId) =>
        memoryId.Trim() is ['[', .. var inner, ']'] ? inner.Trim() : memoryId.Trim();

    private async Task<List<AffectedMemory>> ForgetById(
        string userId, string memoryId, CancellationToken ct)
    {
        memoryId = NormalizeId(memoryId);
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
        string userId, string query, CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await store.SearchAsync(
            userId, query, queryEmbedding, categories: null, tags: null,
            minImportance: null, limit: SearchLimit, ct);

        return [.. results];
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

    private static string TruncateContent(string content)
    {
        return content.Length > ContentPreviewLength
            ? content[..ContentPreviewLength] + "..."
            : content;
    }

    private static JsonObject CreateSuccessResponse(
        List<AffectedMemory> affected, string? reason, IReadOnlyList<string>? unknownIds = null)
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

        // Named so a list that partly landed is not read as a whole one.
        if (unknownIds is { Count: > 0 })
        {
            response["unknownIds"] = new JsonArray([.. unknownIds.Select(id => JsonValue.Create(id))]);
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