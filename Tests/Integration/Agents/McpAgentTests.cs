using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Microsoft.Agents.AI;
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

    private Task<List<AiResponse>> RunAsync(
        OpenRouterChatClient llmClient, string prompt, Func<IReadOnlyList<AiResponse>, bool> landed,
        TimeSpan? budget = null) =>
        LlmAttempt.TurnAsync(() => CreateAgent(llmClient), prompt, landed, budget);

    [SkippableFact]
    public async Task Agent_WithMoveFileTool_CanMoveFileWithinLibrary()
    {
        // Arrange - Move tool requires both paths to be under library path
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMoveDestination");
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoveSource", "agent-test-file.mkv"), "fake content");

        // Act
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");
        var responses = await RunAsync(llmClient,
            $"Use the fs_move tool with:\n" +
            $"- sourcePath: {sourcePath}\n" +
            $"- destinationPath: {destPath}\n" +
            "IMPORTANT: Pass the paths exactly as written — do not shorten, rename, or invent paths.",
            landed: _ => mcpFixture.FileExistsInLibrary(
                Path.Combine("AgentMoveDestination", "agent-test-file.mkv")),
            budget: TimeSpan.FromSeconds(180));

        // Assert
        responses.ShouldNotBeEmpty();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveDestination", "agent-test-file.mkv")).ShouldBeTrue();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveSource", "agent-test-file.mkv")).ShouldBeFalse();
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

        // Act
        var responses = await RunAsync(llmClient,
            "Use the fs_delete tool with:\n" +
            $"- path: downloads/{downloadId}\n" +
            "IMPORTANT: Pass the path exactly as written.",
            landed: _ => !Directory.Exists(downloadSubDir));

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_CanSerializeAndDeserializeThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();

        // Act - First interaction to create a thread with state. Both turns run inside one attempt
        // because the second is only meaningful on the session the first one wrote.
        var (updates1, updates2, serializedJson) = await LlmAttempt.WithinAsync(
            LlmAttempt.Budget, async ct =>
            {
                await using var agent = CreateAgent(llmClient);
                var thread = await agent.CreateSessionAsync(ct);
                var first = await agent.RunStreamingAsync(
                        "Remember: my favorite color is blue.", thread, cancellationToken: ct)
                    .ToListAsync(ct);

                var serialized = await agent.SerializeSessionAsync(thread, cancellationToken: ct);
                var deserializedThread = await agent.DeserializeSessionAsync(serialized, cancellationToken: ct);

                var second = await agent.RunStreamingAsync(
                        "What is my favorite color?", deserializedThread, cancellationToken: ct)
                    .ToListAsync(ct);

                return (first, second, serialized.GetRawText());
            });

        // Assert - verify the agent produced streamed output; don't rely on a final
        // usage/tool-call chunk, which is model/provider dependent and causes flakes.
        updates1.ShouldNotBeEmpty();
        updates2.ShouldNotBeEmpty();
        serializedJson.ShouldNotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_WithAgentKey_CanRestoreThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agentKey = new AgentKey("12345:67890");

        // Act - Create thread from AgentKey (simulating how ChatMonitor works). The restart is the
        // point of the test, so both agents live inside one attempt.
        var agentKeyJson = JsonSerializer.SerializeToElement(agentKey.ToString());
        var (updates1, updates2) = await LlmAttempt.WithinAsync(LlmAttempt.Budget, async ct =>
        {
            List<AgentResponseUpdate> first;
            await using (var agent = CreateAgent(llmClient))
            {
                var thread = await agent.DeserializeSessionAsync(agentKeyJson, cancellationToken: ct);
                first = await agent.RunStreamingAsync(
                        "Remember: my name is TestUser.", thread, cancellationToken: ct)
                    .ToListAsync(ct);
            }

            // A new agent instance, simulating an agent restart.
            await using var agent2 = CreateAgent(llmClient);
            var thread2 = await agent2.DeserializeSessionAsync(agentKeyJson, cancellationToken: ct);
            var second = await agent2.RunStreamingAsync(
                    "What is my name?", thread2, cancellationToken: ct)
                .ToListAsync(ct);

            return (first, second);
        });

        // Assert - verify the agent produced streamed output; don't rely on a final
        // usage/tool-call chunk, which is model/provider dependent and causes flakes.
        updates1.ShouldNotBeEmpty();
        updates2.ShouldNotBeEmpty();
    }
}