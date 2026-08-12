using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class McpAgentMultiFileSystemTests(MultiFileSystemFixture fsFixture, RedisFixture redisFixture)
    : IClassFixture<MultiFileSystemFixture>, IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentMultiFileSystemTests>()
        .Build();

    private static readonly IReadOnlySet<string> _allFileSystemTools = FileSystemToolFeature.AllToolKeys;

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";

        return new OpenRouterChatClient(apiUrl, apiKey, "~deepseek/deepseek-v4-flash-latest:nitro");
    }

    private McpAgent CreateAgent(OpenRouterChatClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, new RetentionSettings { PurgeHorizon = TimeSpan.FromMinutes(10) }, TimeProvider.System);
        return new McpAgent(
            TestAgentSpec.Default with
            {
                DisplayName = "test-multi-fs-agent",
                McpServerEndpoints = [fsFixture.LibraryEndpoint, fsFixture.NotesEndpoint],
                FilesystemEnabledTools = _allFileSystemTools
            },
            llmClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);
    }

    private Task<List<AiResponse>> RunAsync(
        OpenRouterChatClient llmClient, string prompt, Func<IReadOnlyList<AiResponse>, bool> landed) =>
        LlmAttempt.TurnAsync(() => CreateAgent(llmClient), prompt, landed);

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_CanReadFromBoth()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        fsFixture.CreateLibraryFile("multi-read.md", "Library content alpha");
        fsFixture.CreateNotesFile("multi-read.md", "Notes content bravo");

        // Act
        var responses = await RunAsync(llmClient,
            "Read both of these files using the domain__filesystem__text_read tool and tell me their contents:\n" +
            "- filePath: /library/multi-read.md\n" +
            "- filePath: /notes/multi-read.md\n" +
            "IMPORTANT: Every filePath MUST begin with one of the mounted prefixes (/library or /notes). " +
            "Pass the filePath values exactly as written above — do not shorten, rename, or invent paths.",
            landed: r => LlmAttempt.Combine(r).Contains("alpha") && LlmAttempt.Combine(r).Contains("bravo"));

        // Assert
        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content));
        combined.ShouldContain("alpha");
        combined.ShouldContain("bravo");
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_CanCreateOnEach()
    {
        // Arrange
        var llmClient = CreateLlmClient();

        // Act
        var responses = await RunAsync(llmClient,
            "Create these two files using the domain__filesystem__text_create tool (one call per file):\n" +
            "1. filePath: /library/multi-create.md   content: 'library file'\n" +
            "2. filePath: /notes/multi-create.md     content: 'notes file'\n" +
            "IMPORTANT: Every filePath MUST begin with one of the mounted prefixes (/library or /notes). " +
            "Pass the filePath values exactly as written above — do not shorten, rename, or invent paths.",
            landed: _ => File.Exists(Path.Combine(fsFixture.LibraryPath, "multi-create.md"))
                         && File.Exists(Path.Combine(fsFixture.NotesPath, "multi-create.md")));

        // Assert
        responses.ShouldNotBeEmpty();

        var libraryFile = Path.Combine(fsFixture.LibraryPath, "multi-create.md");
        File.Exists(libraryFile).ShouldBeTrue("File should exist in library filesystem");
        (await File.ReadAllTextAsync(libraryFile)).ShouldContain("library");

        var notesFile = Path.Combine(fsFixture.NotesPath, "multi-create.md");
        File.Exists(notesFile).ShouldBeTrue("File should exist in notes filesystem");
        (await File.ReadAllTextAsync(notesFile)).ShouldContain("notes");
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_KnowsAvailableMountPoints()
    {
        // Arrange
        var llmClient = CreateLlmClient();

        // Act
        var responses = await RunAsync(llmClient,
            "Based on your tool descriptions and system prompt alone, list every filesystem mount point " +
            "that is available to you. Do NOT call any tools to answer this — just read the tool metadata " +
            "you already have and reply in text.",
            landed: r => LlmAttempt.Combine(r).Contains("/library") && LlmAttempt.Combine(r).Contains("/notes"));

        // Assert
        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content)).ToLowerInvariant();
        combined.ShouldContain("/library");
        combined.ShouldContain("/notes");
    }
}