using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryExtractorTests
{
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly OpenRouterMemoryExtractor _extractor;

    public OpenRouterMemoryExtractorTests()
    {
        _extractor = new OpenRouterMemoryExtractor(
            _chatClient.Object,
            _store.Object,
            Mock.Of<ILogger<OpenRouterMemoryExtractor>>());
    }

    [Fact]
    public async Task ExtractAsync_WithStorableFacts_ReturnsCandidates()
    {
        var extractionJson = """
            {
              "candidates": [
                {
                  "content": "Works at Contoso",
                  "category": "fact",
                  "importance": 0.8,
                  "confidence": 0.9,
                  "tags": ["work", "company"],
                  "context": "User mentioned during introduction"
                }
              ]
            }
            """;

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, extractionJson)));

        var result = await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Hello, I work at Contoso")],
            "user1", ModelPayer.Deployment, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Works at Contoso");
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[0].Importance.ShouldBe(0.8);
    }

    [Fact]
    public async Task ExtractAsync_WithEmptyArray_ReturnsEmpty()
    {
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates": []}""")));

        var result = await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Just saying hi")],
            "user1", ModelPayer.Deployment, CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WithCandidateMissingCategory_KeepsTheBatchAndCallsItAFact()
    {
        var extractionJson = """
            {
              "candidates": [
                { "content": "Is a senior Python developer at Google", "importance": 0.9 },
                { "content": "Prefers dark mode", "category": "preference", "importance": 0.8 }
              ]
            }
            """;

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, extractionJson)));

        var result = await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "I'm a senior Python developer at Google and I prefer dark mode")],
            "user1", ModelPayer.Deployment, CancellationToken.None);

        result.Count.ShouldBe(2);
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[1].Category.ShouldBe(MemoryCategory.Preference);
    }

    [Fact]
    public async Task ExtractAsync_WithCandidateMissingContent_DropsOnlyThatCandidate()
    {
        var extractionJson = """
            {
              "candidates": [
                { "memory": "Prefers dark mode", "category": "preference", "importance": 0.8 },
                { "content": "Uses Vim keybindings", "category": "preference", "importance": 0.7 }
              ]
            }
            """;

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, extractionJson)));

        var result = await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "I prefer dark mode and use Vim keybindings")],
            "user1", ModelPayer.Deployment, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Uses Vim keybindings");
    }

    // The parser tolerates a candidate that omits a field; the schema must still ask for all of
    // them, or tolerance turns into an invitation to leave them out.
    [Fact]
    public async Task ExtractAsync_AsksTheModelForEveryCandidateField()
    {
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        ChatOptions? capturedOptions = null;
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates": []}""")));

        await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Hello")], "user1", ModelPayer.Deployment, CancellationToken.None);

        var schema = capturedOptions.ShouldNotBeNull().ResponseFormat
            .ShouldBeOfType<ChatResponseFormatJson>().Schema.ShouldNotBeNull();

        var required = schema.GetProperty("properties").GetProperty("Candidates")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        required.ShouldContain("Content");
        required.ShouldContain("Category");
    }

    [Fact]
    public async Task ExtractAsync_WithMalformedJson_ReturnsEmpty()
    {
        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        var result = await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            "user1", ModelPayer.Deployment, CancellationToken.None);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_IncludesExistingProfileInPrompt()
    {
        var profile = new PersonalityProfile
        {
            UserId = "user1",
            Summary = "Senior .NET developer who prefers concise responses",
            LastUpdated = DateTimeOffset.UtcNow
        };

        _store.Setup(s => s.GetProfileAsync("user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient.Setup(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(),
            It.IsAny<ChatOptions>(),
            It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedMessages = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates": []}""")));

        await _extractor.ExtractAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            "user1", ModelPayer.Deployment, CancellationToken.None);

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.Last();
        userMsg.Text.ShouldContain("Senior .NET developer");
    }

    [Fact]
    public async Task ExtractAsync_WithEmptyContextWindow_ReturnsEmptyAndSkipsChatClient()
    {
        var result = await _extractor.ExtractAsync(
            [],
            "user1", ModelPayer.Deployment, CancellationToken.None);

        result.ShouldBeEmpty();
        _chatClient.Verify(
            c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _store.Verify(
            s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExtractAsync_WithMultiTurnWindow_BuildsPromptContainingCurrentMarkerAndTurns()
    {
        _store.Setup(s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        ChatMessage? capturedUserPrompt = null;
        _chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => capturedUserPrompt = msgs.Single())
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"candidates":[]}""")));

        var window = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "hot or cold?"),
            new(ChatRole.User, "cold")
        };

        await _extractor.ExtractAsync(window, "user1", ModelPayer.Deployment, CancellationToken.None);

        capturedUserPrompt.ShouldNotBeNull();
        var promptText = capturedUserPrompt.Text;
        promptText.ShouldContain("[CURRENT]");
        promptText.ShouldContain("cold");
        promptText.ShouldContain("hot or cold?");
    }
}