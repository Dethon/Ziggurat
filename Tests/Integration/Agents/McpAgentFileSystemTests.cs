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
public class McpAgentFileSystemTests(McpVaultServerFixture vaultFixture, RedisFixture redisFixture)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentFileSystemTests>()
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
                DisplayName = "test-fs-agent",
                McpServerEndpoints = [vaultFixture.McpEndpoint],
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
    public async Task Agent_WithFileSystemFeature_CanReadFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("read-test.md", "# Secret Document\nThis is the content.");

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__text_read tool with filePath: /vault/read-test.md and tell me its content. " +
            "IMPORTANT: the filePath argument MUST start with the mounted prefix /vault. " +
            "Pass it exactly as written — do not shorten, rename, or invent paths.",
            landed: r => LlmAttempt.Combine(r).Contains("Secret Document"));

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content));
        combinedResponse.ShouldContain("Secret Document");
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanCreateFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__text_create tool with:\n" +
            "- filePath: /vault/created-by-agent.md\n" +
            "- content: '# Created\nHello from agent'\n" +
            "IMPORTANT: the filePath argument MUST start with the mounted prefix /vault. " +
            "Pass it exactly as written — do not shorten, rename, or invent paths.",
            landed: _ => File.Exists(Path.Combine(vaultFixture.VaultPath, "created-by-agent.md")));

        // Assert
        responses.ShouldNotBeEmpty();
        var filePath = Path.Combine(vaultFixture.VaultPath, "created-by-agent.md");
        File.Exists(filePath).ShouldBeTrue("Agent should have created the file");
        var content = await File.ReadAllTextAsync(filePath);
        content.ShouldContain("Created");
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanEditFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("edit-test.md", "Hello World");

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__text_edit tool with filePath: /vault/edit-test.md and edits: [{ oldString: 'World', newString: 'Agent' }]. " +
            "IMPORTANT: the filePath argument MUST start with the mounted prefix /vault. " +
            "Pass it exactly as written — do not shorten, rename, or invent paths.",
            landed: _ => File.ReadAllText(Path.Combine(vaultFixture.VaultPath, "edit-test.md")).Contains("Agent"));

        // Assert
        responses.ShouldNotBeEmpty();
        var content = await File.ReadAllTextAsync(Path.Combine(vaultFixture.VaultPath, "edit-test.md"));
        content.ShouldContain("Agent");
        content.ShouldNotContain("World");
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanSearchFiles()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("search-test", "doc1.md"), "The quick brown fox jumps over the lazy dog.");
        vaultFixture.CreateFile(Path.Combine("search-test", "doc2.md"), "A different document without the target phrase.");

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__text_search tool with directoryPath: /vault/search-test and query: 'quick brown fox'. " +
            "IMPORTANT: the directoryPath argument MUST start with the mounted prefix /vault. " +
            "Pass it exactly as written — do not shorten, rename, or invent paths.",
            landed: r => LlmAttempt.Combine(r).Contains("doc1"));

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = LlmAttempt.Combine(responses);
        combinedResponse.ShouldContain("doc1");
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanMoveFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("move-src", "moveme.md"), "move content");
        Directory.CreateDirectory(Path.Combine(vaultFixture.VaultPath, "move-dst"));

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__move tool with:\n" +
            "- sourcePath: /vault/move-src/moveme.md\n" +
            "- destinationPath: /vault/move-dst/moveme.md\n" +
            "IMPORTANT: both path arguments MUST start with the mounted prefix /vault. " +
            "Pass them exactly as written — do not shorten, rename, or invent paths.",
            landed: _ => File.Exists(Path.Combine(vaultFixture.VaultPath, "move-dst", "moveme.md")));

        // Assert
        responses.ShouldNotBeEmpty();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-dst", "moveme.md")).ShouldBeTrue();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-src", "moveme.md")).ShouldBeFalse();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanRemoveFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("remove-me.md", "to be deleted");

        // Act
        var responses = await RunAsync(llmClient,
            "Use the domain__filesystem__remove tool with path: /vault/remove-me.md to delete that file. " +
            "IMPORTANT: the path argument MUST start with the mounted prefix /vault. " +
            "Pass it exactly as written — do not shorten, rename, or invent paths.",
            landed: _ => !File.Exists(Path.Combine(vaultFixture.VaultPath, "remove-me.md")));

        // Assert
        responses.ShouldNotBeEmpty();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "remove-me.md")).ShouldBeFalse();
    }

}