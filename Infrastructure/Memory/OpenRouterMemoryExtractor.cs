using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Domain.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

// Its model is asked through the payer's own client (PaidChatClients): on the household's key, counted
// under it.
public class OpenRouterMemoryExtractor(
    Func<ModelPayer, IChatClient> chatClients,
    IMemoryStore store,
    ILogger<OpenRouterMemoryExtractor> logger) : IMemoryExtractor
{
    // One client for everybody, as a test drives it.
    public OpenRouterMemoryExtractor(IChatClient chatClient, IMemoryStore store, ILogger<OpenRouterMemoryExtractor> logger)
        : this(_ => chatClient, store, logger)
    {
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // The schema tells the model that every candidate field is required. Reading the answer back
    // must not enforce that: a model still drops a field now and then, and a required-property
    // failure throws away the whole batch instead of the one candidate that came back incomplete.
    private static readonly JsonSerializerOptions _readOptions = new(_jsonOptions)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { MakeEveryPropertyOptional } }
    };

    private static readonly ChatOptions _extractionChatOptions = new()
    {
        Instructions = MemoryPrompts.ExtractionSystemPrompt,
        ResponseFormat = ChatResponseFormat.ForJsonSchema<ExtractionResponseDto>(
            serializerOptions: _jsonOptions)
    };

    private static void MakeEveryPropertyOptional(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        foreach (var property in typeInfo.Properties)
        {
            property.IsRequired = false;
        }
    }

    public async Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, ModelPayer payer, CancellationToken ct)
    {
        if (contextWindow.Count == 0)
        {
            return [];
        }

        var profile = await store.GetProfileAsync(userId, ct);
        var renderedWindow = ExtractionWindow.Render(contextWindow);

        var userPrompt = profile is not null
            ? $"Existing user profile:\n{profile.Summary}\n\nConversation window:\n{renderedWindow}"
            : $"Conversation window:\n{renderedWindow}";

        var userMessage = new ChatMessage(ChatRole.User, userPrompt);
        userMessage.SetSenderId(userId);

        var messages = new List<ChatMessage> { userMessage };

        var response = await chatClients(payer).GetResponseAsync(messages, _extractionChatOptions, ct);
        return ParseCandidates(response.Text);
    }

    private IReadOnlyList<ExtractionCandidate> ParseCandidates(string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var wrapper = JsonSerializer.Deserialize<ExtractionResponseDto>(json, _readOptions);

            // A model that leaves a field off one candidate must not cost us the whole batch, so
            // an incomplete candidate is dropped or defaulted here rather than failing the parse.
            return wrapper?.Candidates?
                .Where(c => !string.IsNullOrWhiteSpace(c.Content))
                .Select(c => new ExtractionCandidate(
                    c.Content!,
                    c.Category ?? MemoryCategory.Fact,
                    Math.Clamp(c.Importance, 0, 1),
                    Math.Clamp(c.Confidence, 0, 1),
                    c.Tags ?? [],
                    c.Context))
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse extraction response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

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

    private sealed record ExtractionResponseDto
    {
        public List<ExtractionCandidateDto>? Candidates { get; init; }
    }

    private sealed record ExtractionCandidateDto
    {
        [JsonRequired] public string? Content { get; init; }
        [JsonRequired] public MemoryCategory? Category { get; init; }
        public double Importance { get; init; }
        public double Confidence { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        public string? Context { get; init; }
    }
}