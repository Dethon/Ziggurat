using System.ClientModel;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using OpenAI;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Memory;

[Trait("Category", "Integration")]
[Trait("Category", "Llm")]
public class MemoryExtractionResponseFormatTests : IAsyncLifetime
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<MemoryExtractionResponseFormatTests>()
        .AddEnvironmentVariables()
        .Build();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.Delay(TimeSpan.FromMilliseconds(500));

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["Memory:Extraction:Model"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    private static (OpenRouterMemoryExtractor Extractor, List<string> Warnings) CreateExtractor(
        string apiUrl, string apiKey, string model)
    {
        var chatClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(apiUrl) })
            .GetChatClient(model)
            .AsIChatClient();

        var store = new Mock<IMemoryStore>();
        store.Setup(s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var logger = LoggerFactory.Create(builder => builder.AddProvider(logs))
            .CreateLogger<OpenRouterMemoryExtractor>();

        return (new OpenRouterMemoryExtractor(chatClient, store.Object, logger), logs.Messages);
    }

    [SkippableFact]
    public async Task ExtractAsync_WithRichMessage_ReturnsValidCandidates()
    {
        var (apiUrl, apiKey, model) = GetConfig();
        var (extractor, warnings) = CreateExtractor(apiUrl, apiKey, model);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await LlmAttempt.UntilAsync(
            () => extractor.ExtractAsync(
                [new ChatMessage(ChatRole.User,
                    "I'm a senior Python developer at Google. I prefer dark mode and use Vim keybindings. " +
                    "I'm currently learning Rust and working on a distributed cache project.")],
                "test_user", cts.Token),
            candidates => candidates.Count > 0);

        result.ShouldNotBeEmpty(LlmAttempt.Explain(
            "LLM should extract at least one memory candidate", warnings));

        foreach (var candidate in result)
        {
            candidate.Content.ShouldNotBeNullOrWhiteSpace("Each candidate must have content");
            Enum.IsDefined(candidate.Category).ShouldBeTrue(
                $"Category '{candidate.Category}' should be a valid MemoryCategory");
            candidate.Importance.ShouldBeInRange(0, 1);
            candidate.Confidence.ShouldBeInRange(0, 1);
            candidate.Tags.ShouldNotBeNull();
        }
    }

    [SkippableFact]
    public async Task ExtractAsync_WithTrivialMessage_ReturnsEmptyOrFewCandidates()
    {
        var (apiUrl, apiKey, model) = GetConfig();
        var (extractor, _) = CreateExtractor(apiUrl, apiKey, model);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Hello, how are you?")],
            "test_user", cts.Token);

        result.Count.ShouldBeLessThanOrEqualTo(1);
    }
}

[Trait("Category", "Integration")]
[Trait("Category", "Llm")]
public class MemoryConsolidationResponseFormatTests : IAsyncLifetime
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<MemoryConsolidationResponseFormatTests>()
        .AddEnvironmentVariables()
        .Build();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.Delay(TimeSpan.FromMilliseconds(500));

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["Memory:Dreaming:Model"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    private static (OpenRouterMemoryConsolidator Consolidator, List<string> Warnings) CreateConsolidator(
        string apiUrl, string apiKey, string model)
    {
        var chatClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(apiUrl) })
            .GetChatClient(model)
            .AsIChatClient();

        var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var logger = LoggerFactory.Create(builder => builder.AddProvider(logs))
            .CreateLogger<OpenRouterMemoryConsolidator>();

        return (new OpenRouterMemoryConsolidator(chatClient, logger), logs.Messages);
    }

    private static MemoryEntry CreateMemory(string id, string content,
        MemoryCategory category = MemoryCategory.Fact, double importance = 0.7) => new()
        {
            Id = id,
            UserId = "test_user",
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastAccessedAt = DateTimeOffset.UtcNow
        };

    [SkippableFact]
    public async Task ConsolidateAsync_WithOverlappingMemories_ReturnsValidDecisions()
    {
        var (apiUrl, apiKey, model) = GetConfig();
        var (consolidator, warnings) = CreateConsolidator(apiUrl, apiKey, model);

        var memories = new[]
        {
            CreateMemory("mem_1", "User works at Google"),
            CreateMemory("mem_2", "User is a software engineer at Google working on distributed systems"),
            CreateMemory("mem_3", "User prefers dark mode", MemoryCategory.Preference),
            CreateMemory("mem_4", "User likes dark themes in all editors", MemoryCategory.Preference)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await LlmAttempt.UntilAsync(
            () => consolidator.ConsolidateAsync(memories, cts.Token),
            decisions => decisions.Count > 0);

        result.ShouldNotBeEmpty(LlmAttempt.Explain(
            "LLM should produce at least one merge decision for overlapping memories", warnings));

        foreach (var decision in result)
        {
            Enum.IsDefined(decision.Action).ShouldBeTrue(
                $"Action '{decision.Action}' should be a valid MergeAction");
            decision.SourceIds.ShouldNotBeEmpty("Each decision must reference source memory IDs");
            decision.SourceIds.ShouldAllBe(id => memories.Any(m => m.Id == id),
                "Source IDs should reference input memory IDs");

            if (decision.Action == MergeAction.Merge)
            {
                decision.MergedContent.ShouldNotBeNullOrWhiteSpace(
                    "Merge decisions must include merged content");
                decision.Category.ShouldNotBeNull("Merge decisions must specify a category");
            }
        }
    }

    [SkippableFact]
    public async Task ConsolidateAsync_WithDistinctMemories_ReturnsKeepDecisions()
    {
        var (apiUrl, apiKey, model) = GetConfig();
        var (consolidator, _) = CreateConsolidator(apiUrl, apiKey, model);

        var memories = new[]
        {
            CreateMemory("mem_1", "User is allergic to peanuts", MemoryCategory.Fact),
            CreateMemory("mem_2", "User prefers Vim keybindings", MemoryCategory.Preference),
            CreateMemory("mem_3", "User is learning Japanese", MemoryCategory.Skill)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await consolidator.ConsolidateAsync(memories, cts.Token);

        result.ShouldAllBe(d => d.Action != MergeAction.Merge,
            "Distinct, unrelated memories should not be merged");
    }
}

[Trait("Category", "Integration")]
[Trait("Category", "Llm")]
public class MemoryProfileSynthesisResponseFormatTests : IAsyncLifetime
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<MemoryProfileSynthesisResponseFormatTests>()
        .AddEnvironmentVariables()
        .Build();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.Delay(TimeSpan.FromMilliseconds(500));

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["Memory:Dreaming:Model"] ?? "~deepseek/deepseek-v4-flash-latest:nitro";
        return (apiUrl, apiKey, model);
    }

    private static (OpenRouterMemoryConsolidator Consolidator, List<string> Warnings) CreateConsolidator(
        string apiUrl, string apiKey, string model)
    {
        var chatClient = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(apiUrl) })
            .GetChatClient(model)
            .AsIChatClient();

        var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var logger = LoggerFactory.Create(builder => builder.AddProvider(logs))
            .CreateLogger<OpenRouterMemoryConsolidator>();

        return (new OpenRouterMemoryConsolidator(chatClient, logger), logs.Messages);
    }

    private static MemoryEntry CreateMemory(string id, string content,
        MemoryCategory category = MemoryCategory.Fact, double importance = 0.7) => new()
        {
            Id = id,
            UserId = "test_user",
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        };

    [SkippableFact]
    public async Task SynthesizeProfileAsync_WithVariedMemories_ReturnsValidProfile()
    {
        var (apiUrl, apiKey, model) = GetConfig();
        var (consolidator, warnings) = CreateConsolidator(apiUrl, apiKey, model);

        var memories = new[]
        {
            CreateMemory("mem_1", "User is a senior .NET developer", MemoryCategory.Skill),
            CreateMemory("mem_2", "User works at a fintech startup", MemoryCategory.Fact),
            CreateMemory("mem_3", "User prefers concise, direct answers", MemoryCategory.Preference),
            CreateMemory("mem_4", "User dislikes verbose explanations", MemoryCategory.Preference),
            CreateMemory("mem_5", "User is learning Rust", MemoryCategory.Skill),
            CreateMemory("mem_6", "User uses Docker and Kubernetes daily", MemoryCategory.Skill),
            CreateMemory("mem_7", "User is building an AI agent project", MemoryCategory.Project)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var result = await LlmAttempt.UntilAsync(
            () => consolidator.SynthesizeProfileAsync("test_user", memories, cts.Token),
            profile => !string.IsNullOrWhiteSpace(profile.Summary)
                       && profile.CommunicationStyle is not null
                       && profile.TechnicalContext is not null);

        result.UserId.ShouldBe("test_user");
        result.BasedOnMemoryCount.ShouldBe(7);
        result.Summary.ShouldNotBeNullOrWhiteSpace(
            LlmAttempt.Explain("Profile must have a summary", warnings));
        result.LastUpdated.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));

        result.CommunicationStyle.ShouldNotBeNull(
            LlmAttempt.Explain("Profile should include communication style", warnings));
        result.CommunicationStyle!.Preference.ShouldNotBeNullOrWhiteSpace();

        result.TechnicalContext.ShouldNotBeNull(
            LlmAttempt.Explain("Profile should include technical context", warnings));
        result.TechnicalContext!.Expertise.ShouldNotBeEmpty("Should identify areas of expertise");
    }
}