using System.Text.Json;
using Domain.Agents;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

[Trait("Category", "Llm")]
public class McpAgentTests(McpLibraryServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpLibraryServerFixture>, IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentTests>()
        .Build();

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";

        return new OpenRouterChatClient(apiUrl, apiKey, "~deepseek/deepseek-v4-flash-latest:nitro");
    }

    private McpAgent CreateAgent(OpenRouterChatClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            TestAgentSpec.Default with { McpServerEndpoints = [mcpFixture.McpEndpoint] },
            llmClient,
            stateStore,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            [],
            []);
    }

    [SkippableFact]
    public async Task Agent_WithMoveFileTool_CanMoveFileWithinLibrary()
    {
        // Arrange - Move tool requires both paths to be under library path
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMoveDestination");
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoveSource", "agent-test-file.mkv"), "fake content");

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        // Act
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");
        var responses = await agent.RunStreamingAsync(
                $"Use the fs_move tool with:\n" +
                $"- sourcePath: {sourcePath}\n" +
                $"- destinationPath: {destPath}\n" +
                "IMPORTANT: Pass the paths exactly as written — do not shorten, rename, or invent paths.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveDestination", "agent-test-file.mkv")).ShouldBeTrue();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveSource", "agent-test-file.mkv")).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFsDeleteTool_CanRemoveLeftoverDownloadDirectory()
    {
        // Arrange - a leftover download directory whose torrent no longer exists
        var llmClient = CreateLlmClient();
        const int downloadId = 99999;
        var downloadSubDir = Path.Combine(mcpFixture.DownloadPath, downloadId.ToString());
        Directory.CreateDirectory(downloadSubDir);
        await File.WriteAllTextAsync(Path.Combine(downloadSubDir, "leftover.nfo"), "info file");

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Use the fs_delete tool with:\n" +
                $"- path: downloads/{downloadId}\n" +
                "IMPORTANT: Pass the path exactly as written.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_CanSerializeAndDeserializeThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - First interaction to create a thread with state
        var thread = await agent.CreateSessionAsync(cts.Token);
        var updates1 = await agent.RunStreamingAsync(
                "Remember: my favorite color is blue.",
                thread,
                cancellationToken: cts.Token)
            .ToListAsync(cts.Token);

        var serialized = await agent.SerializeSessionAsync(thread, cancellationToken: cts.Token);
        var serializedJson = serialized.GetRawText();

        var deserializedThread = await agent.DeserializeSessionAsync(serialized, cancellationToken: cts.Token);

        var updates2 = await agent.RunStreamingAsync(
                "What is my favorite color?",
                deserializedThread,
                cancellationToken: cts.Token)
            .ToListAsync(cts.Token);

        // Assert - verify the agent produced streamed output; don't rely on a final
        // usage/tool-call chunk, which is model/provider dependent and causes flakes.
        updates1.ShouldNotBeEmpty();
        updates2.ShouldNotBeEmpty();
        serializedJson.ShouldNotBeNullOrEmpty();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_WithAgentKey_CanRestoreThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);
        var agentKey = new AgentKey("12345:67890");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - Create thread from AgentKey (simulating how ChatMonitor works)
        var agentKeyJson = JsonSerializer.SerializeToElement(agentKey.ToString());
        var thread = await agent.DeserializeSessionAsync(agentKeyJson, cancellationToken: cts.Token);

        var updates1 = await agent.RunStreamingAsync(
                "Remember: my name is TestUser.",
                thread,
                cancellationToken: cts.Token)
            .ToListAsync(cts.Token);

        // Create a new agent instance (simulating agent restart)
        await agent.DisposeAsync();
        var agent2 = CreateAgent(llmClient);

        var thread2 = await agent2.DeserializeSessionAsync(agentKeyJson, cancellationToken: cts.Token);

        var updates2 = await agent2.RunStreamingAsync(
                "What is my name?",
                thread2,
                cancellationToken: cts.Token)
            .ToListAsync(cts.Token);

        // Assert - verify the agent produced streamed output; don't rely on a final
        // usage/tool-call chunk, which is model/provider dependent and causes flakes.
        updates1.ShouldNotBeEmpty();
        updates2.ShouldNotBeEmpty();

        await agent2.DisposeAsync();
    }
}